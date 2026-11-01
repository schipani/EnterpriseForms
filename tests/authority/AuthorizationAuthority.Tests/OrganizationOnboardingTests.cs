using System.Buffers.Text;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AuthorizationAuthority.Application.Common;
using AuthorizationAuthority.Application.Common.Ports;
using AuthorizationAuthority.Application.Features.OperatorPlane;
using AuthorizationAuthority.Application.Features.OrganizationOnboarding;
using AuthorizationAuthority.Application.Features.PlatformOperators;
using AuthorizationAuthority.Contracts.V1;
using AuthorizationAuthority.Domain.Features.Journal;
using AuthorizationAuthority.Domain.Features.Keys;
using AuthorizationAuthority.Domain.Features.Organizations;
using AuthorizationAuthority.Domain.Features.PlatformOperators;
using AuthorizationAuthority.Domain.Features.SecurityCommands;
using AuthorizationAuthority.Infrastructure.Archive;
using AuthorizationAuthority.Infrastructure.Signing;
using Xunit;

namespace AuthorizationAuthority.Tests;

/// <summary>
/// C8 onboarding (C-104) through the real services over the in-memory store, archive and local HMAC provider: create (validation,
/// Authority-generated ids, conflicts, idempotency), two distinct operator approvals with per-operator evidence (same operator twice,
/// impersonation, replay, stale fingerprint, expiry, suspended first approver), reject/cancel/withdraw, key custody verification
/// (missing, provider down), atomic initialization (owners and owner roles only, gate closed until projected), interrupted
/// initialization and retry, archive outage and platform journal replay, the organizations directory, and the legacy fixture
/// bootstrap restriction and OB-F2 fix. The Docker path is OrganizationOnboardingStackTests (Stage B).
/// </summary>
public sealed class OrganizationOnboardingTests : IDisposable
{
    private const string OwnerIssuer = "https://idp.test.invalid";
    private const string OperatorIssuer = "https://platform-operators.test.invalid/";
    private const string Justification = "Contract signed; owners verified by support callback (OPS-91)";
    private const string Synthetic = PlatformOperatorEvidenceKinds.SyntheticDevelopmentJws;
    private static readonly Guid ExistingOrg = Guid.CreateVersion7();

    private readonly InMemoryAuthorityStore store = new();
    private readonly ManualClock clock = new(new DateTimeOffset(2026, 11, 1, 9, 0, 0, TimeSpan.Zero));
    private readonly SwitchableArchive archive = new();
    private readonly RecordingAlerts alerts = new();
    private readonly SwitchableMac mac = new();
    private readonly ISpiceDbProjection spiceProxy = DispatchProxy.Create<ISpiceDbProjection, SpiceDb>();
    private readonly Op alice;
    private readonly Op blake;
    private readonly Op casey;

    private SpiceDb SpiceDbFake => (SpiceDb)spiceProxy;

    public OrganizationOnboardingTests()
    {
        store.Organizations.Add(new OrganizationAuthorizationState { OrganizationId = ExistingOrg, OrganizationDisplayName = "Riverside Clinic", AuthorizationRevision = 3, ProjectionAppliedRevision = 3 });
        alice = Seed("Alice Operator");
        blake = Seed("Blake Operator");
        casey = Seed("Casey Operator");
    }

    public void Dispose()
    {
        foreach (var key in new[] { alice.Key, blake.Key, casey.Key })
        {
            key.Dispose();
        }
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static PlatformOperatorPolicy Platform => PlatformOperatorPolicy.Create(new PlatformOperatorOptions { Mode = PlatformOperatorPolicy.SyntheticMode }, "Testing");

    private static OrganizationOnboardingPolicy Policy(bool legacy = false) =>
        OrganizationOnboardingPolicy.Create(new OrganizationOnboardingOptions { ApprovedIdentityIssuers = [OwnerIssuer, "https://other-idp.test.invalid"], LegacyOrganizationBootstrapEnabled = legacy }, "Testing");

    private PlatformJournal Journal => new(store, archive, alerts, clock);

    private OrganizationOnboardingService Service => new(store, Platform, Policy(), new PlatformOperatorEvidenceVerifier(Platform), Journal, mac, spiceProxy, alerts, clock);

    private OrganizationDirectoryService Directory => new(store, Platform, Journal, clock);

    private static async Task<string> RefusedAsync(Func<Task> action) => (await Assert.ThrowsAsync<AuthorityException>(action)).ErrorCode;

    private sealed record Op(Guid Id, string Subject, string KeyId, ECDsa Key, string Issuer = OperatorIssuer)
    {
        public VerifiedActor Actor => new(Issuer, Subject, "platform-console");
    }

    private Op Seed(string name, string issuer = OperatorIssuer, string? subject = null)
    {
        var op = new Op(Guid.CreateVersion7(), subject ?? "operator-" + Guid.NewGuid().ToString("N"), "key-" + Guid.NewGuid().ToString("N")[..12], ECDsa.Create(ECCurve.NamedCurves.nistP256), issuer);
        store.PlatformOperators.Add(new PlatformOperator
        {
            PlatformOperatorId = op.Id, OperatorIssuerUri = issuer, OperatorSubjectIdentifier = op.Subject, OperatorDisplayName = name, OperatorEvidenceKindName = Synthetic,
            SyntheticApprovalKeyIdentifier = op.KeyId, SyntheticApprovalPublicKey = op.Key.ExportSubjectPublicKeyInfo(), OperatorProvisionedOn = clock.GetUtcNow(), OperatorStatusChangedOn = clock.GetUtcNow(),
        });
        return op;
    }

    private string Evidence(Op op, string decision, Guid requestId, Guid organizationId, string statementSha256, string justification = Justification, ECDsa? signer = null)
    {
        var now = clock.GetUtcNow();
        var claims = new Dictionary<string, object>
        {
            ["iss"] = PlatformOperatorEvidenceClaims.Issuer,
            ["aud"] = PlatformOperatorEvidenceClaims.Audience,
            ["sub"] = op.KeyId,
            ["jti"] = Guid.NewGuid().ToString("N"),
            ["iat"] = now.ToUnixTimeSeconds(),
            ["nbf"] = now.AddSeconds(-5).ToUnixTimeSeconds(),
            ["exp"] = now.AddMinutes(5).ToUnixTimeSeconds(),
            [PlatformOperatorEvidenceClaims.PlatformOperatorId] = op.Id.ToString("D"),
            [PlatformOperatorEvidenceClaims.Decision] = decision,
            [PlatformOperatorEvidenceClaims.OperationKind] = PlatformOperationKinds.OrganizationOnboarding,
            [PlatformOperatorEvidenceClaims.OperationId] = requestId.ToString("D"),
            [PlatformOperatorEvidenceClaims.OrganizationId] = organizationId.ToString("D"),
            [PlatformOperatorEvidenceClaims.StatementDigest] = statementSha256,
            [PlatformOperatorEvidenceClaims.JustificationDigest] = PlatformOperatorEvidenceVerifier.JustificationSha256(justification),
        };
        var header = Base64Url.EncodeToString(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { alg = "ES256", typ = "JWT", kid = op.KeyId })));
        var body = Base64Url.EncodeToString(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(claims)));
        return header + "." + body + "." + Base64Url.EncodeToString((signer ?? op.Key).SignData(Encoding.ASCII.GetBytes(header + "." + body), HashAlgorithmName.SHA256));
    }

    private static OrganizationOnboardingOwnerInput Owner(string subject, string issuer = OwnerIssuer, string method = OnboardingVerificationMethods.SupportCallback) =>
        new(issuer, subject, "Owner " + subject, method, "CASE-" + subject);

    private Task<OrganizationOnboardingCreateResponse> CreateAsync(Op op, string name = "Harbour Legal Aid", IReadOnlyList<OrganizationOnboardingOwnerInput>? owners = null, string? key = null, bool dryRun = false) =>
        Service.CreateAsync(op.Actor, new OrganizationOnboardingCreateRequest(key ?? "create-" + Guid.NewGuid().ToString("N"), name, owners ?? [Owner("owner-one"), Owner("owner-two"), Owner("owner-three")], Justification, dryRun), Guid.CreateVersion7(), Ct);

    private async Task<OrganizationOnboardingDecisionResponse> DecideAsync(Op op, OrganizationOnboardingDetail request, string decision, string? expected = null, string? evidence = null)
    {
        var sha = request.StatementSha256;
        var input = new OrganizationOnboardingDecisionRequest(expected ?? sha, Justification, Synthetic, evidence ?? Evidence(op, decision, request.RequestId, request.OrganizationId, sha));
        var service = Service;
        return decision switch
        {
            PlatformDecisions.Approve => await service.ApproveAsync(op.Actor, request.RequestId, input, Guid.CreateVersion7(), Ct),
            PlatformDecisions.Withdraw => await service.WithdrawAsync(op.Actor, request.RequestId, input, Guid.CreateVersion7(), Ct),
            PlatformDecisions.Reject => await service.RejectAsync(op.Actor, request.RequestId, input, Guid.CreateVersion7(), Ct),
            PlatformDecisions.Cancel => await service.CancelAsync(op.Actor, request.RequestId, input, Guid.CreateVersion7(), Ct),
            _ => await service.InitializeAsync(op.Actor, request.RequestId, input, Guid.CreateVersion7(), Ct),
        };
    }

    private async Task<OrganizationOnboardingDetail> ApprovedAsync()
    {
        var request = (await CreateAsync(alice)).Request!;
        await DecideAsync(alice, request, PlatformDecisions.Approve);
        Assert.Equal("Approved", (await DecideAsync(blake, request, PlatformDecisions.Approve)).Outcome);
        return request;
    }

    private void ProvisionKey(OrganizationOnboardingDetail request) =>
        mac.Inner.ProvisionKeyAsync(request.OrganizationId, IntegrityKeyPurposes.RowIntegrityV1, IntegrityKeyNaming.ProviderKeyName(request.OrganizationId, IntegrityKeyPurposes.RowIntegrityV1), Ct).GetAwaiter().GetResult();

    private OrganizationOnboardingRequest Row(OrganizationOnboardingDetail request) => store.OnboardingRequests.Single(r => r.OrganizationOnboardingRequestId == request.RequestId);

    private int Journaled(string kind) => store.PlatformJournal.Count(j => j.JournalEventKind == kind);

    // ---- create ----

    [Fact]
    public async Task CreatingFreezesAnImmutableStatementWithAuthorityGeneratedIdsAndGrantsNothing()
    {
        var created = await CreateAsync(alice);
        var request = created.Request!;
        Assert.False(created.Replayed);
        Assert.Equal(OrganizationOnboardingStates.PendingApproval, request.State);
        Assert.Equal(OrganizationOnboardingDisplayStates.AwaitingApproval, request.DisplayState);
        Assert.Equal(0, request.ApprovalsRecorded);
        Assert.Equal(3, request.Owners.Count);
        Assert.All(request.Owners, o => Assert.NotEqual(Guid.Empty, o.AuthorityUserId));
        Assert.Equal(3, request.Owners.Select(o => o.AuthorityUserId).Distinct().Count());
        Assert.NotEqual(Guid.Empty, request.OrganizationId);
        Assert.Null(store.Organizations.SingleOrDefault(o => o.OrganizationId == request.OrganizationId)); // nothing exists until initialization
        Assert.Contains(request.OrganizationId.ToString("D"), request.StatementJson, StringComparison.Ordinal);
        Assert.Equal(1, Journaled("OrganizationOnboardingRequested"));
        Assert.Equal(PlatformOperatorPolicy.SyntheticAssurance, request.ApprovalAssurance);

        var twoOwners = await CreateAsync(alice, "Two Owner Trust", [Owner("a-1"), Owner("a-2")]);
        Assert.Contains(twoOwners.Warnings, w => w.Contains("three are recommended", StringComparison.Ordinal));
    }

    [Fact]
    public async Task OwnerRulesAreEnforced()
    {
        Assert.Equal(AuthorityErrorCodes.OrganizationOwnersInsufficient, await RefusedAsync(() => CreateAsync(alice, owners: [Owner("only-one")])));
        Assert.Equal(AuthorityErrorCodes.OrganizationOwnersInsufficient, await RefusedAsync(() => CreateAsync(alice, owners: [])));
        Assert.Equal(AuthorityErrorCodes.OrganizationOwnerIdentityInvalid, await RefusedAsync(() => CreateAsync(alice, owners: [Owner("dup"), Owner("dup")])));
        Assert.Equal(AuthorityErrorCodes.OrganizationOwnerIssuerMismatch, await RefusedAsync(() => CreateAsync(alice, owners: [Owner("x-1"), Owner("x-2", "https://other-idp.test.invalid")])));
        Assert.Equal(AuthorityErrorCodes.OrganizationOwnerIdentityInvalid, await RefusedAsync(() => CreateAsync(alice, owners: [Owner("y-1", "https://unapproved.test.invalid"), Owner("y-2", "https://unapproved.test.invalid")])));
        Assert.Equal(AuthorityErrorCodes.OrganizationOwnerIdentityInvalid, await RefusedAsync(() => CreateAsync(alice, owners: [Owner("someone@example.com"), Owner("z-2")])));
        Assert.Equal(AuthorityErrorCodes.OrganizationOwnerIdentityInvalid, await RefusedAsync(() => CreateAsync(alice, owners: [Owner("v-1", method: "Trust me"), Owner("v-2")])));
        Assert.Equal(AuthorityErrorCodes.OrganizationOwnerIdentityInvalid, await RefusedAsync(() => CreateAsync(alice, owners: Enumerable.Range(1, 11).Select(i => Owner("many-" + i)).ToList())));
        Assert.Empty(store.OnboardingRequests);
        Assert.Equal(8, Journaled("PlatformOperatorDecisionRefused")); // refusals of an authenticated operator are journaled
    }

    [Fact]
    public async Task AnOperatorCanNeverAppointThemselfAnInitialOwner()
    {
        // An operator registered under an organization issuer (development fixture): their identity is refused as an owner.
        var dana = Seed("Dana Operator", OwnerIssuer, "dana-operator");
        Assert.Equal(AuthorityErrorCodes.OrganizationOwnerIsOperator, await RefusedAsync(() => CreateAsync(dana, owners: [Owner("dana-operator"), Owner("friend-1")])));
        Assert.Equal(AuthorityErrorCodes.OrganizationOwnerIsOperator, await RefusedAsync(() => CreateAsync(alice, owners: [Owner("dana-operator"), Owner("friend-1")])));

        // An owner identity registered as an operator after the request was created blocks approval and initialization.
        var request = (await CreateAsync(alice, owners: [Owner("later-operator"), Owner("friend-2"), Owner("friend-3")])).Request!;
        Seed("Late Operator", OwnerIssuer, "later-operator");
        Assert.Equal(AuthorityErrorCodes.OrganizationOwnerIsOperator, await RefusedAsync(() => DecideAsync(alice, request, PlatformDecisions.Approve)));
        Assert.Empty(store.OnboardingApprovals);
    }

    [Fact]
    public async Task DuplicateOrganizationsAndNamesAreRefusedAndIdempotencyIsDigestBound()
    {
        Assert.Equal(AuthorityErrorCodes.OrganizationNameConflict, await RefusedAsync(() => CreateAsync(alice, "  riverside   CLINIC ")));
        var first = await CreateAsync(alice, "Harbour Legal Aid", key: "harbour-key-0001");
        Assert.Equal(AuthorityErrorCodes.OrganizationNameConflict, await RefusedAsync(() => CreateAsync(blake, "HARBOUR legal aid")));

        var replay = await CreateAsync(alice, "Harbour Legal Aid", key: "harbour-key-0001");
        Assert.True(replay.Replayed);
        Assert.Equal(first.Request!.RequestId, replay.Request!.RequestId);
        Assert.Equal(AuthorityErrorCodes.IdempotencyConflict, await RefusedAsync(() => CreateAsync(alice, "Harbour Legal Aid Two", key: "harbour-key-0001")));
        Assert.Single(store.OnboardingRequests);

        // An expired request releases the name and is recorded Expired by the next request that uses it.
        clock.Advance(TimeSpan.FromDays(8));
        var again = await CreateAsync(blake, "Harbour Legal Aid");
        Assert.NotEqual(first.Request.RequestId, again.Request!.RequestId);
        Assert.Equal(OrganizationOnboardingStates.Expired, store.OnboardingRequests.Single(r => r.OrganizationOnboardingRequestId == first.Request.RequestId).OnboardingStateName);
    }

    // ---- approvals ----

    [Fact]
    public async Task TwoDistinctOperatorsMustApproveTheSameStatement()
    {
        var request = (await CreateAsync(alice)).Request!;
        var first = await DecideAsync(alice, request, PlatformDecisions.Approve); // the creator may give one approval
        Assert.Equal("AwaitingSecondApproval", first.Outcome);
        Assert.Equal(1, first.ApprovalsRecorded);

        // Same operator again, with fresh evidence: refused, still 1 of 2.
        Assert.Equal(AuthorityErrorCodes.OperatorAlreadyApproved, await RefusedAsync(() => DecideAsync(alice, request, PlatformDecisions.Approve)));
        Assert.Single(store.OnboardingApprovals);

        // Blake's session presenting Alice's evidence is refused (impersonation).
        Assert.Equal(AuthorityErrorCodes.OperatorEvidenceIdentityMismatch, await RefusedAsync(() => DecideAsync(blake, request, PlatformDecisions.Approve, evidence: Evidence(alice, PlatformDecisions.Approve, request.RequestId, request.OrganizationId, request.StatementSha256))));

        var second = await DecideAsync(blake, request, PlatformDecisions.Approve);
        Assert.Equal("Approved", second.Outcome);
        Assert.Equal(OrganizationOnboardingStates.AwaitingKeyCustody, second.State);
        Assert.All(store.OnboardingApprovals, a => Assert.Equal(OnboardingApprovalStates.Consumed, a.ApprovalStateName));
        Assert.Equal(2, store.OnboardingApprovals.Select(a => a.PlatformOperatorId).Distinct().Count());
        Assert.Null(store.Organizations.SingleOrDefault(o => o.OrganizationId == request.OrganizationId)); // approval creates nothing
        Assert.Equal(AuthorityErrorCodes.OrganizationOnboardingNotPending, await RefusedAsync(() => DecideAsync(casey, request, PlatformDecisions.Approve)));
    }

    [Fact]
    public async Task StaleFingerprintsReplayedOrMisboundEvidenceAndExpiredRequestsAreRefused()
    {
        var request = (await CreateAsync(alice)).Request!;
        var other = (await CreateAsync(alice, "Second Clinic")).Request!;
        Assert.Equal(AuthorityErrorCodes.ApprovalStatementChanged, await RefusedAsync(() => DecideAsync(alice, request, PlatformDecisions.Approve, expected: other.StatementSha256)));
        // Evidence for another request, or for another decision, never verifies.
        Assert.Equal(AuthorityErrorCodes.OperatorEvidenceInvalid, await RefusedAsync(() => DecideAsync(alice, request, PlatformDecisions.Approve, evidence: Evidence(alice, PlatformDecisions.Approve, other.RequestId, other.OrganizationId, other.StatementSha256))));
        Assert.Equal(AuthorityErrorCodes.OperatorEvidenceInvalid, await RefusedAsync(() => DecideAsync(alice, request, PlatformDecisions.Approve, evidence: Evidence(alice, PlatformDecisions.Reject, request.RequestId, request.OrganizationId, request.StatementSha256))));
        // The same evidence is accepted once only.
        var evidence = Evidence(alice, PlatformDecisions.Approve, request.RequestId, request.OrganizationId, request.StatementSha256);
        await DecideAsync(alice, request, PlatformDecisions.Approve, evidence: evidence);
        await DecideAsync(alice, request, PlatformDecisions.Withdraw);
        Assert.Equal(AuthorityErrorCodes.OperatorEvidenceReplayed, await RefusedAsync(() => DecideAsync(alice, request, PlatformDecisions.Approve, evidence: evidence)));

        clock.Advance(TimeSpan.FromDays(7));
        Assert.Equal(AuthorityErrorCodes.OrganizationOnboardingExpired, await RefusedAsync(() => DecideAsync(blake, request, PlatformDecisions.Approve)));
        Assert.Equal(OrganizationOnboardingDisplayStates.Expired, (await Service.GetDetailAsync(blake.Actor, request.RequestId, Guid.CreateVersion7(), Ct)).DisplayState);
    }

    [Fact]
    public async Task ASuspendedFirstApproverNoLongerCounts()
    {
        var request = (await CreateAsync(alice)).Request!;
        await DecideAsync(alice, request, PlatformDecisions.Approve);
        store.PlatformOperators.Single(o => o.PlatformOperatorId == alice.Id).OperatorStatusName = PlatformOperatorStates.Suspended;
        var outcome = await DecideAsync(blake, request, PlatformDecisions.Approve);
        Assert.Equal("AwaitingSecondApproval", outcome.Outcome);
        Assert.Equal(OnboardingApprovalStates.Invalidated, store.OnboardingApprovals.Single(a => a.PlatformOperatorId == alice.Id).ApprovalStateName);
        Assert.Equal(AuthorityErrorCodes.PlatformOperatorNotActive, await RefusedAsync(() => DecideAsync(alice, request, PlatformDecisions.Approve)));
        Assert.Equal("Approved", (await DecideAsync(casey, request, PlatformDecisions.Approve)).Outcome);
    }

    [Fact]
    public async Task RejectWithdrawAndCancelCloseWithoutCreatingAnything()
    {
        var rejected = (await CreateAsync(alice, "Rejected Org")).Request!;
        await DecideAsync(alice, rejected, PlatformDecisions.Approve);
        Assert.Equal("Rejected", (await DecideAsync(blake, rejected, PlatformDecisions.Reject)).Outcome);
        Assert.Equal(OnboardingApprovalStates.Invalidated, store.OnboardingApprovals.Single(a => a.OrganizationOnboardingRequestId == rejected.RequestId).ApprovalStateName);
        Assert.Equal(AuthorityErrorCodes.OrganizationOnboardingNotPending, await RefusedAsync(() => DecideAsync(casey, rejected, PlatformDecisions.Approve)));

        var withdrawn = (await CreateAsync(alice, "Withdrawn Org")).Request!;
        await DecideAsync(alice, withdrawn, PlatformDecisions.Approve);
        Assert.Equal("ApprovalWithdrawn", (await DecideAsync(alice, withdrawn, PlatformDecisions.Withdraw)).Outcome);
        Assert.Equal(AuthorityErrorCodes.NoPendingApproval, await RefusedAsync(() => DecideAsync(blake, withdrawn, PlatformDecisions.Withdraw)));
        Assert.Equal("AwaitingSecondApproval", (await DecideAsync(blake, withdrawn, PlatformDecisions.Approve)).Outcome); // withdrawn approval no longer counts

        var cancelled = await ApprovedAsync();
        Assert.Equal(AuthorityErrorCodes.OrganizationOnboardingNotPending, await RefusedAsync(() => DecideAsync(casey, cancelled, PlatformDecisions.Reject)));
        Assert.Equal("Cancelled", (await DecideAsync(casey, cancelled, PlatformDecisions.Cancel)).Outcome);
        ProvisionKey(cancelled);
        Assert.Equal(AuthorityErrorCodes.OrganizationOnboardingNotPending, await RefusedAsync(() => DecideAsync(casey, cancelled, PlatformDecisions.Initialize)));
        Assert.Single(store.Organizations); // only the pre-existing one
    }

    // ---- key custody and initialization ----

    [Fact]
    public async Task InitializationNeedsTheCustodyKeyAndCommitsOwnersAndOwnerRolesOnly()
    {
        var request = await ApprovedAsync();
        var detail = await Service.GetDetailAsync(casey.Actor, request.RequestId, Guid.CreateVersion7(), Ct);
        Assert.Equal("Missing", detail.KeyCustody.Status);
        Assert.False(detail.MyEligibility.CanInitialize);

        Assert.Equal(AuthorityErrorCodes.OrganizationKeyNotProvisioned, await RefusedAsync(() => DecideAsync(casey, request, PlatformDecisions.Initialize)));
        Assert.Equal(AuthorityErrorCodes.OrganizationKeyNotProvisioned, Row(request).LastInitializationFailureCode);
        Assert.Single(store.Organizations);
        Assert.Equal(1, Journaled("OrganizationOnboardingInitializationRefused"));

        ProvisionKey(request); // the custody step (development: the dedicated create-only Vault credential)
        Assert.Equal("Ready", (await Service.GetDetailAsync(casey.Actor, request.RequestId, Guid.CreateVersion7(), Ct)).KeyCustody.Status);
        var initialized = await DecideAsync(casey, request, PlatformDecisions.Initialize);
        Assert.Equal("Initialized", initialized.Outcome);
        Assert.NotNull(initialized.SecurityCommandId);

        var state = store.Organizations.Single(o => o.OrganizationId == request.OrganizationId);
        Assert.Equal(1, state.AuthorizationRevision);
        Assert.Equal(request.RequestId, state.OrganizationOnboardingRequestId);
        var bindings = store.Bindings.Where(b => b.OrganizationId == request.OrganizationId).ToList();
        Assert.Equal(request.Owners.Select(o => o.AuthorityUserId).Order(), bindings.Select(b => b.AuthorityUserId).Order());
        Assert.All(bindings, b => Assert.Equal(request.RequestId, b.OrganizationOnboardingRequestId));
        var edges = store.Edges.Where(e => e.OrganizationId == request.OrganizationId).ToList();
        Assert.Equal(3, edges.Count);
        Assert.All(edges, e => Assert.Equal(OrganizationRoleNames.OrganizationOwner, e.RelationshipName)); // no Security Administrator, designer, team or record edges
        var command = store.Commands.Single(c => c.OrganizationId == request.OrganizationId);
        Assert.Equal(OrganizationOnboardingService.InitializeCommandKind, command.CommandKindName);
        Assert.Contains(alice.Id.ToString("D"), command.CommandPayloadJson, StringComparison.Ordinal);
        Assert.Contains(blake.Id.ToString("D"), command.CommandPayloadJson, StringComparison.Ordinal);
        Assert.Single(store.Keys, k => k.OrganizationId == request.OrganizationId);
        Assert.Contains(alerts.Raised, a => a.AlertKind == "OrganizationOnboarded");

        // Not usable before activation: revision 1 is committed but not projected, so the organization gate stays closed.
        await using (var scope = await store.BeginScopeAsync(Ct))
        {
            Assert.Equal(ProjectionGateState.AwaitingProjection, await OrganizationGate.EvaluateAsync(scope, state, Ct));
        }

        var after = await Service.GetDetailAsync(casey.Actor, request.RequestId, Guid.CreateVersion7(), Ct);
        Assert.Equal(OrganizationOnboardingDisplayStates.Activating, after.DisplayState);
        Assert.Equal("Activating", after.Activation!.Status);
    }

    [Fact]
    public async Task ProviderOutageAndAnInterruptedInitializationFailClosedAndRetryOnce()
    {
        var request = await ApprovedAsync();
        ProvisionKey(request);
        mac.Available = false;
        Assert.Equal(AuthorityErrorCodes.SigningUnavailable, await RefusedAsync(() => DecideAsync(casey, request, PlatformDecisions.Initialize)));
        Assert.Single(store.Organizations);
        mac.Available = true;

        SpiceDbFake.Unavailable = true; // interrupted before the commit
        Assert.Equal(AuthorityErrorCodes.AuthorityNotReady, await RefusedAsync(() => DecideAsync(casey, request, PlatformDecisions.Initialize)));
        Assert.Single(store.Organizations);
        Assert.DoesNotContain(store.Bindings, b => b.OrganizationId == request.OrganizationId);
        SpiceDbFake.Unavailable = false;

        Assert.Equal("Initialized", (await DecideAsync(casey, request, PlatformDecisions.Initialize)).Outcome);
        var retry = await DecideAsync(alice, request, PlatformDecisions.Initialize); // a retry after success replays
        Assert.True(retry.Replayed);
        Assert.Single(store.Organizations, o => o.OrganizationId == request.OrganizationId);
        Assert.Single(store.Commands, c => c.OrganizationId == request.OrganizationId);
    }

    [Fact]
    public async Task TheKeyCustodyWindowExpires()
    {
        var request = await ApprovedAsync();
        clock.Advance(TimeSpan.FromHours(73));
        ProvisionKey(request);
        Assert.Equal(AuthorityErrorCodes.OrganizationOnboardingExpired, await RefusedAsync(() => DecideAsync(casey, request, PlatformDecisions.Initialize)));
        Assert.False((await Service.GetKeyCustodyAsync(request.RequestId, Guid.CreateVersion7(), Ct)).ProvisionAllowed);
    }

    [Fact]
    public async Task KeyCustodyStatusNamesOnlyTheApprovedRequestsKey()
    {
        var pending = (await CreateAsync(alice, "Pending Org")).Request!;
        Assert.False((await Service.GetKeyCustodyAsync(pending.RequestId, Guid.CreateVersion7(), Ct)).ProvisionAllowed);
        var request = await ApprovedAsync();
        var custody = await Service.GetKeyCustodyAsync(request.RequestId, Guid.CreateVersion7(), Ct);
        Assert.True(custody.ProvisionAllowed);
        Assert.Equal(IntegrityKeyNaming.ProviderKeyName(request.OrganizationId, IntegrityKeyPurposes.RowIntegrityV1), custody.KeyName);
        ProvisionKey(request);
        Assert.False((await Service.GetKeyCustodyAsync(request.RequestId, Guid.CreateVersion7(), Ct)).ProvisionAllowed); // already Ready
    }

    // ---- journal ----

    [Fact]
    public async Task AnArchiveOutageFailsClosedAndAReplayRestoresOnboardingAfterARestore()
    {
        archive.Available = false;
        Assert.Equal(AuthorityErrorCodes.PlatformJournalUnverifiable, await RefusedAsync(() => CreateAsync(alice)));
        Assert.Empty(store.OnboardingRequests);
        archive.Available = true;

        var request = (await CreateAsync(alice)).Request!;
        await DecideAsync(alice, request, PlatformDecisions.Approve);
        var snapshot = Snapshot();
        await DecideAsync(blake, request, PlatformDecisions.Approve);
        Restore(snapshot); // AuthorityDb restored to before the second approval; the archive keeps the accepted event

        Assert.Equal(AuthorityErrorCodes.PlatformJournalBehindArchive, await RefusedAsync(() => Service.GetDetailAsync(casey.Actor, request.RequestId, Guid.CreateVersion7(), Ct)));
        var replay = await Journal.ReplayAsync("test-operator", Guid.CreateVersion7(), Ct);
        Assert.True(replay.GateOpen);
        Assert.Equal(OrganizationOnboardingStates.AwaitingKeyCustody, Row(request).OnboardingStateName);
        Assert.Equal(2, store.OnboardingApprovals.Count(a => a.OrganizationOnboardingRequestId == request.RequestId && a.ApprovalStateName == OnboardingApprovalStates.Consumed));
    }

    // ---- directory ----

    [Fact]
    public async Task TheDirectoryListsMetadataAndAuditsDetailViews()
    {
        var request = await ApprovedAsync();
        ProvisionKey(request);
        await DecideAsync(casey, request, PlatformDecisions.Initialize);
        var page = await Directory.SearchAsync(alice.Actor, new OrganizationDirectorySearchRequest(null, null, null, null), Guid.CreateVersion7(), Ct);
        Assert.Equal(2, page.TotalCount);
        var harbour = page.Items.Single(i => i.OrganizationId == request.OrganizationId);
        Assert.Equal(3, harbour.EnabledOwnerCount);
        Assert.Equal("Activating", harbour.Lifecycle);
        Assert.True(page.Items.Single(i => i.OrganizationId == ExistingOrg).NeedsOwners);
        Assert.Single((await Directory.SearchAsync(alice.Actor, new OrganizationDirectorySearchRequest(null, OrganizationDirectoryFilters.NeedsOwners, null, null), Guid.CreateVersion7(), Ct)).Items);

        var paged = await Directory.SearchAsync(alice.Actor, new OrganizationDirectorySearchRequest(null, null, null, 1), Guid.CreateVersion7(), Ct);
        Assert.NotNull(paged.NextCursor);
        var next = await Directory.SearchAsync(alice.Actor, new OrganizationDirectorySearchRequest(null, null, paged.NextCursor, 1), Guid.CreateVersion7(), Ct);
        Assert.NotEqual(paged.Items[0].OrganizationId, next.Items[0].OrganizationId);
        Assert.Equal(AuthorityErrorCodes.CursorInvalid, await RefusedAsync(() => Directory.SearchAsync(blake.Actor, new OrganizationDirectorySearchRequest(null, null, paged.NextCursor, 1), Guid.CreateVersion7(), Ct)));

        var audits = store.Journal.Count(j => j.OrganizationId == request.OrganizationId && j.JournalEventKind == "PlatformOrganizationDirectoryViewed");
        var detail = await Directory.GetDetailAsync(alice.Actor, request.OrganizationId, Guid.CreateVersion7(), Ct);
        Assert.Equal(3, detail.Owners.Count);
        Assert.Equal(["Alice Operator", "Blake Operator"], detail.OnboardingApprovedBy.Order().ToArray());
        Assert.Equal(audits + 1, store.Journal.Count(j => j.OrganizationId == request.OrganizationId && j.JournalEventKind == "PlatformOrganizationDirectoryViewed"));
    }

    // ---- legacy fixture bootstrap (D8-60, OB-F2) ----

    [Fact]
    public async Task TheLegacyBootstrapIsRestrictedToFixturesAndNoLongerAnswersFalseSuccess()
    {
        var org = Guid.CreateVersion7();
        var owner = Guid.CreateVersion7();
        var bootstrap = new BootstrapOrganizationRequest(org, "Fixture Org", [new BootstrapIdentity(owner, OwnerIssuer, "fixture-owner", [OrganizationRoleNames.OrganizationOwner])]);
        var disabled = new OrganizationBootstrapService(store, mac, spiceProxy, Policy(legacy: false), clock);
        Assert.Equal(AuthorityErrorCodes.LegacyOrganizationBootstrapDisabled, await RefusedAsync(() => disabled.BootstrapAsync(bootstrap, "dev-seed", Guid.CreateVersion7(), Ct)));

        var legacy = new OrganizationBootstrapService(store, mac, spiceProxy, Policy(legacy: true), clock);
        var created = await legacy.BootstrapAsync(bootstrap, "dev-seed", Guid.CreateVersion7(), Ct);
        Assert.Equal(1, created.AuthorizationRevision);
        // Repeating the identical fixture is idempotent (the operator workload name is not part of the comparison).
        Assert.Equal(Guid.Empty, (await legacy.BootstrapAsync(bootstrap, "another-workload", Guid.CreateVersion7(), Ct)).BootstrapCommandId);
        // Different input for an existing id is refused instead of the former false success.
        Assert.Equal(AuthorityErrorCodes.IdempotencyConflict, await RefusedAsync(() => legacy.BootstrapAsync(bootstrap with { OrganizationDisplayName = "Other Name" }, "dev-seed", Guid.CreateVersion7(), Ct)));
        Assert.Equal(AuthorityErrorCodes.IdempotencyConflict, await RefusedAsync(() => legacy.BootstrapAsync(bootstrap with { Identities = [new BootstrapIdentity(owner, OwnerIssuer, "someone-else", [OrganizationRoleNames.OrganizationOwner])] }, "dev-seed", Guid.CreateVersion7(), Ct)));
        Assert.Equal(AuthorityErrorCodes.IdempotencyConflict, await RefusedAsync(() => legacy.BootstrapAsync(bootstrap with { Identities = [new BootstrapIdentity(owner, OwnerIssuer, "fixture-owner", [OrganizationRoleNames.OrganizationOwner, OrganizationRoleNames.Designer])] }, "dev-seed", Guid.CreateVersion7(), Ct)));

        // An id reserved by an onboarding request cannot be taken by the legacy path.
        var reserved = (await CreateAsync(alice, "Reserved Org")).Request!;
        Assert.Equal(AuthorityErrorCodes.OrganizationIdReserved, await RefusedAsync(() => legacy.BootstrapAsync(bootstrap with { OrganizationId = reserved.OrganizationId, OrganizationDisplayName = "Squatter" }, "dev-seed", Guid.CreateVersion7(), Ct)));
    }

    // ---- fakes ----

    private sealed record StoreSnapshot(string Requests, string Owners, string Approvals, string Evidence, string Journal, string Head);

    private StoreSnapshot Snapshot() => new(JsonSerializer.Serialize(store.OnboardingRequests), JsonSerializer.Serialize(store.OnboardingOwners), JsonSerializer.Serialize(store.OnboardingApprovals),
        JsonSerializer.Serialize(store.PlatformEvidenceUses), JsonSerializer.Serialize(store.PlatformJournal), JsonSerializer.Serialize(store.PlatformHead));

    private void Restore(StoreSnapshot s)
    {
        static void Reset<T>(List<T> list, string json)
        {
            list.Clear();
            list.AddRange(JsonSerializer.Deserialize<List<T>>(json)!);
        }

        Reset(store.OnboardingRequests, s.Requests);
        Reset(store.OnboardingOwners, s.Owners);
        Reset(store.OnboardingApprovals, s.Approvals);
        Reset(store.PlatformEvidenceUses, s.Evidence);
        Reset(store.PlatformJournal, s.Journal);
        store.PlatformHead = JsonSerializer.Deserialize<PlatformJournalHead>(s.Head);
    }

    /// <summary>SpiceDB stand-in: only the cursor initialization is used here; it can be made unavailable to interrupt initialization.</summary>
    public class SpiceDb : DispatchProxy
    {
        public bool Unavailable { get; set; }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name != nameof(ISpiceDbProjection.InitializeCursorAsync))
            {
                throw new NotSupportedException("unexpected call " + targetMethod?.Name);
            }

            return Unavailable ? Task.FromException<string>(new SpiceDbUnavailableException("down (test)")) : Task.FromResult("zed-token");
        }
    }

    private sealed class SwitchableMac : IIntegrityMacProvider
    {
        public LocalHmacProvider Inner { get; } = new();

        public bool Available { get; set; } = true;

        public string ProviderName => Inner.ProviderName;

        public Task<IntegrityMacResult> SignAsync(IntegrityKeyReference keyReference, ReadOnlyMemory<byte> canonicalPayload, CancellationToken cancellationToken) =>
            Available ? Inner.SignAsync(keyReference, canonicalPayload, cancellationToken) : throw new IntegrityProviderUnavailableException("down (test)");

        public Task<bool> VerifyAsync(IntegrityKeyReference keyReference, ReadOnlyMemory<byte> canonicalPayload, ReadOnlyMemory<byte> expectedMac, CancellationToken cancellationToken) =>
            Available ? Inner.VerifyAsync(keyReference, canonicalPayload, expectedMac, cancellationToken) : throw new IntegrityProviderUnavailableException("down (test)");

        public Task<int> ProvisionKeyAsync(Guid organizationId, string keyPurpose, string providerKeyName, CancellationToken cancellationToken) => Inner.ProvisionKeyAsync(organizationId, keyPurpose, providerKeyName, cancellationToken);

        public Task<(bool Ready, string Detail)> ProbeAsync(CancellationToken cancellationToken) => Inner.ProbeAsync(cancellationToken);

        public Task<IntegrityKeyInspection> InspectKeyAsync(string providerKeyName, CancellationToken cancellationToken) =>
            Available ? Inner.InspectKeyAsync(providerKeyName, cancellationToken) : throw new IntegrityProviderUnavailableException("down (test)");
    }

    private sealed class SwitchableArchive : ISecurityEventArchive
    {
        private readonly InMemorySecurityEventArchive inner = new();

        public bool Available { get; set; } = true;

        public string ArchiveName => "Switchable(InMemory)";

        public Task<ArchiveReceipt> AppendAsync(Guid organizationId, string streamName, long streamRevision, ReadOnlyMemory<byte> eventBody, byte[] expectedDigest, CancellationToken cancellationToken) =>
            Available ? inner.AppendAsync(organizationId, streamName, streamRevision, eventBody, expectedDigest, cancellationToken) : throw new ArchiveUnavailableException("archive down (test)");

        public Task<(bool Ready, string Detail)> ProbeAsync(CancellationToken cancellationToken) => Task.FromResult((Available, "test"));

        public Task<byte[]?> ReadAsync(Guid organizationId, string streamName, long streamRevision, CancellationToken cancellationToken) =>
            Available ? inner.ReadAsync(organizationId, streamName, streamRevision, cancellationToken) : throw new ArchiveUnavailableException("archive down (test)");
    }

    private sealed class RecordingAlerts : ISecurityAlertSink
    {
        public List<SecurityAlert> Raised { get; } = [];

        public void Raise(SecurityAlert alert) => Raised.Add(alert);
    }

    private sealed class ManualClock(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset now = start;

        public override DateTimeOffset GetUtcNow() => now;

        public void Advance(TimeSpan by) => now += by;
    }
}
