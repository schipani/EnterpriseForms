using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AuthorizationAuthority.Application.Common;
using AuthorizationAuthority.Application.Common.Ports;
using AuthorizationAuthority.Application.Features.Membership;
using AuthorizationAuthority.Application.Features.OperatorPlane;
using AuthorizationAuthority.Application.Features.PlatformOperators;
using AuthorizationAuthority.Application.Features.SecurityCommands;
using AuthorizationAuthority.Contracts.V1;
using AuthorizationAuthority.Domain.Features.Organizations;
using AuthorizationAuthority.Domain.Features.PlatformOperators;
using AuthorizationAuthority.Domain.Features.SecurityCommands;
using AuthorizationAuthority.Infrastructure.Archive;
using Xunit;

namespace AuthorizationAuthority.Tests;

/// <summary>
/// C8 M2 Stages 1-2 (D8-33…D8-44) through the real services over the in-memory store and archive: the Platform Operator registry
/// (two-operator provision/reinstate/remove, one-operator suspension, recovery ceremony), per-operator evidence (impersonation,
/// replay, expiry, mis-binding), two-person membership approval (pending evidence, atomic completion, withdrawal, rejection,
/// invalidation on cancel/replace/expiry/suspension, conflict of interest), cross-organization reads, and the platform journal
/// (archive delivery, rollback detection after a simulated AuthorityDb restore, replay, quarantine). PO-T numbers refer to
/// docs/architecture/PHASE_C8_M2_PLATFORM_OPERATOR_APPROVALS.md §10.2. The Docker path is PlatformOperatorApprovalTests (Stage B).
/// </summary>
public sealed class PlatformOperatorTests : IDisposable
{
    private const string OrgIssuer = "https://idp";
    private const string OperatorIssuer = "https://platform-operators.test.invalid/";
    private const string Justification = "Verified with the requesting owner by phone (OPS-77)";
    private const string Synthetic = PlatformOperatorEvidenceKinds.SyntheticDevelopmentJws;
    private static readonly Guid Org = Guid.CreateVersion7();
    private static readonly Guid OwnerA = Guid.CreateVersion7();
    private static readonly Guid OwnerB = Guid.CreateVersion7();
    private static readonly FieldSearchApprovalGate StepUp = FieldSearchApprovalGate.Create(FieldSearchApprovalGate.SyntheticMode, "Testing", "TestIdentityProvider");

    private readonly InMemoryAuthorityStore store = new();
    private readonly ManualClock clock = new(new DateTimeOffset(2026, 10, 31, 9, 0, 0, TimeSpan.Zero));
    private readonly SwitchableArchive archive = new();
    private readonly RecordingAlerts alerts = new();
    private readonly ECDsa custodianOne = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private readonly ECDsa custodianTwo = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private readonly Op alice;
    private readonly Op blake;
    private readonly Op casey;

    public PlatformOperatorTests()
    {
        store.Organizations.Add(new OrganizationAuthorizationState { OrganizationId = Org, OrganizationDisplayName = "Riverside Clinic", AuthorizationRevision = 5, ProjectionAppliedRevision = 5 });
        foreach (var owner in new[] { OwnerA, OwnerB })
        {
            store.Bindings.Add(new IdentityBinding { IdentityBindingId = Guid.CreateVersion7(), OrganizationId = Org, AuthorityUserId = owner, IdentityIssuerUri = OrgIssuer, IdentitySubjectIdentifier = "subject-" + owner.ToString("N"), IdentityBindingEnabled = true });
            store.Edges.Add(new AuthorityRelationshipEdge
            {
                AuthorityRelationshipEdgeId = Guid.CreateVersion7(), OrganizationId = Org, ResourceTypeName = ResourceTypeNames.Organization, ResourceEntityId = Org, RelationshipName = OrganizationRoleNames.OrganizationOwner,
                SubjectTypeName = ResourceTypeNames.User, SubjectEntityId = owner, SubjectRelationName = string.Empty, RelationshipCreatedOn = DateTimeOffset.UtcNow,
            });
        }

        alice = Seed("Alice Operator");
        blake = Seed("Blake Operator");
        casey = Seed("Casey Operator");
    }

    public void Dispose()
    {
        foreach (var key in new[] { custodianOne, custodianTwo, alice.Key, blake.Key, casey.Key })
        {
            key.Dispose();
        }
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static PlatformOperatorPolicy Policy => PlatformOperatorPolicy.Create(new PlatformOperatorOptions { Mode = PlatformOperatorPolicy.SyntheticMode }, "Testing");

    private OperatorApprovalVerifier Custodians => OperatorApprovalVerifier.Create(new OperatorApprovalOptions
    {
        Mode = OperatorApprovalVerifier.SyntheticMode,
        Approvers =
        [
            new OperatorApproverOptions { ApproverId = "custodian-1", PublicKeyBase64 = Convert.ToBase64String(custodianOne.ExportSubjectPublicKeyInfo()) },
            new OperatorApproverOptions { ApproverId = "custodian-2", PublicKeyBase64 = Convert.ToBase64String(custodianTwo.ExportSubjectPublicKeyInfo()) },
        ],
    }, "Testing");

    private PlatformJournal Journal => new(store, archive, alerts, clock);

    private PlatformOperatorRegistryService Registry(PlatformOperatorPolicy? policy = null) => new(store, policy ?? Policy, new PlatformOperatorEvidenceVerifier(policy ?? Policy), Journal, Custodians, alerts, clock);

    private PlatformMembershipApprovalService Approvals(PlatformOperatorPolicy? policy = null) => new(store, policy ?? Policy, new PlatformOperatorEvidenceVerifier(policy ?? Policy), Journal, Registry(policy), clock);

    private static async Task<string> RefusedAsync(Func<Task> action) => (await Assert.ThrowsAsync<AuthorityException>(action)).ErrorCode;

    private sealed record Op(Guid Id, string Subject, string KeyId, ECDsa Key)
    {
        public VerifiedActor Actor => new(OperatorIssuer, Subject, "platform-console");
    }

    private Op Seed(string name)
    {
        var op = new Op(Guid.CreateVersion7(), "operator-" + Guid.NewGuid().ToString("N"), "key-" + name.Split(' ')[0].ToLowerInvariant(), ECDsa.Create(ECCurve.NamedCurves.nistP256));
        store.PlatformOperators.Add(new PlatformOperator
        {
            PlatformOperatorId = op.Id, OperatorIssuerUri = OperatorIssuer, OperatorSubjectIdentifier = op.Subject, OperatorDisplayName = name, OperatorEvidenceKindName = Synthetic,
            SyntheticApprovalKeyIdentifier = op.KeyId, SyntheticApprovalPublicKey = op.Key.ExportSubjectPublicKeyInfo(), OperatorProvisionedOn = clock.GetUtcNow(), OperatorStatusChangedOn = clock.GetUtcNow(),
        });
        return op;
    }

    private PlatformOperator Row(Op op) => store.PlatformOperators.Single(o => o.PlatformOperatorId == op.Id);

    /// <summary>A Platform Operator's evidence as the (future) Platform Console would produce it with that operator's synthetic key.</summary>
    private string Evidence(Op op, string decision, string kind, Guid operationId, Guid organizationId, string statementSha256, string justification, ECDsa? signer = null, Action<Dictionary<string, object>>? tamper = null, TimeSpan? lifetime = null)
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
            ["exp"] = now.Add(lifetime ?? TimeSpan.FromMinutes(5)).ToUnixTimeSeconds(),
            [PlatformOperatorEvidenceClaims.PlatformOperatorId] = op.Id.ToString("D"),
            [PlatformOperatorEvidenceClaims.Decision] = decision,
            [PlatformOperatorEvidenceClaims.OperationKind] = kind,
            [PlatformOperatorEvidenceClaims.OperationId] = operationId.ToString("D"),
            [PlatformOperatorEvidenceClaims.OrganizationId] = organizationId.ToString("D"),
            [PlatformOperatorEvidenceClaims.StatementDigest] = statementSha256,
            [PlatformOperatorEvidenceClaims.JustificationDigest] = PlatformOperatorEvidenceVerifier.JustificationSha256(justification),
        };
        tamper?.Invoke(claims);
        var header = Base64Url.EncodeToString(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { alg = "ES256", typ = "JWT", kid = (string)claims["sub"] })));
        var body = Base64Url.EncodeToString(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(claims)));
        return header + "." + body + "." + Base64Url.EncodeToString((signer ?? op.Key).SignData(Encoding.ASCII.GetBytes(header + "." + body), HashAlgorithmName.SHA256));
    }

    private async Task<OrganizationMembershipRequest> RequestAsync(string subject, string? role = OrganizationRoleNames.Designer)
    {
        var status = await new SecurityCommandService(store, new NoFaults(), clock, StepUp).SubmitAsync(new VerifiedActor(OrgIssuer, "subject-" + OwnerA.ToString("N"), "test"),
            new SecurityCommandRequest(Org, SecurityCommandKind.RequestOrganizationMembership, new ResourceRef(ResourceKind.Organization, Org), store.Organizations.Single(o => o.OrganizationId == Org).AuthorizationRevision,
                JsonSerializer.SerializeToElement(new MembershipRequestPayload(OrgIssuer, subject, "Nia Newcomer", role, "joins the intake team", FieldSearchApprovalGate.SyntheticStepUpEvidence), CanonicalJson.Options)),
            "po-" + Guid.NewGuid().ToString("N"), Guid.CreateVersion7(), Ct);
        return store.MembershipRequests.Single(r => r.OrganizationMembershipRequestId == status.CommandId);
    }

    private async Task<string> StatementAsync(Op op, Guid requestId) =>
        (await Approvals().ApproveAsync(op.Actor, Org, requestId, new PlatformMembershipDecisionRequest(string.Empty, Justification, DryRun: true), Guid.CreateVersion7(), Ct)).StatementSha256;

    private async Task<PlatformMembershipDecisionResponse> DecideAsync(Op op, Guid requestId, string decision = PlatformDecisions.Approve, string? evidence = null, string? expected = null, string justification = Justification)
    {
        var sha = await StatementAsync(op, requestId);
        var input = new PlatformMembershipDecisionRequest(expected ?? sha, justification, false, Synthetic, evidence ?? Evidence(op, decision, PlatformOperationKinds.MembershipApproval, requestId, Org, sha, justification));
        var service = Approvals();
        return decision switch
        {
            PlatformDecisions.Approve => await service.ApproveAsync(op.Actor, Org, requestId, input, Guid.CreateVersion7(), Ct),
            PlatformDecisions.Reject => await service.RejectAsync(op.Actor, Org, requestId, input, Guid.CreateVersion7(), Ct),
            _ => await service.WithdrawAsync(op.Actor, Org, requestId, input, Guid.CreateVersion7(), Ct),
        };
    }

    private bool IsMember(OrganizationMembershipRequest request) => store.Bindings.Any(b => b.OrganizationId == Org && b.AuthorityUserId == request.RequestedAuthorityUserId);

    private int ApprovalCommands => store.Commands.Count(c => c.CommandKindName == "ApproveOrganizationMembership");

    private int Journaled(string kind) => store.PlatformJournal.Count(j => j.JournalEventKind == kind);

    // ---- two-person membership approval ----

    [Fact]
    public async Task TwoDistinctOperatorsCreateTheMembershipOnlyAfterTheSecondValidApproval()
    {
        var request = await RequestAsync("new-person-01");
        var first = await DecideAsync(alice, request.OrganizationMembershipRequestId);
        Assert.Equal("AwaitingSecondApproval", first.Outcome);
        Assert.Equal(1, first.ApprovalsRecorded);
        Assert.False(IsMember(request));
        Assert.Equal(0, ApprovalCommands);
        Assert.Equal(MembershipRequestStates.Pending, request.RequestStateName);
        Assert.Equal("Anchored", first.JournalArchiveState);

        var second = await DecideAsync(blake, request.OrganizationMembershipRequestId);
        Assert.Equal("Approved", second.Outcome);
        Assert.True(IsMember(request));
        Assert.Equal(1, ApprovalCommands);
        Assert.Equal(MembershipRequestStates.Approved, request.RequestStateName);
        Assert.Equal(blake.Id, request.RequestDecidedByPlatformOperatorId);
        Assert.Equal(2, store.PlatformApprovals.Count(a => a.ApprovalStateName == PlatformApprovalStates.Consumed && a.ConsumedBySecurityCommandId == second.SecurityCommandId));
        Assert.Contains(store.Edges, e => e.SubjectEntityId == request.RequestedAuthorityUserId && e.RelationshipName == OrganizationRoleNames.Designer && e.RemovedBySecurityCommandId is null);

        var command = store.Commands.Single(c => c.CommandKindName == "ApproveOrganizationMembership");
        Assert.Contains(alice.Id.ToString("D"), command.CommandPayloadJson, StringComparison.Ordinal);
        Assert.Contains(blake.Id.ToString("D"), command.CommandPayloadJson, StringComparison.Ordinal);
        Assert.Equal(1, Journaled("PlatformMembershipApprovalRecorded"));
        Assert.Equal(1, Journaled("PlatformMembershipApprovalCompleted"));
        Assert.Contains(store.Journal, j => j.JournalEventKind == "OrganizationMembershipApproved");
        Assert.Contains(PlatformOperatorPolicy.SyntheticAssurance, command.CommandPayloadJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheSameOperatorCannotGiveBothApprovalsEvenWithFreshEvidence()
    {
        var request = await RequestAsync("new-person-02");
        await DecideAsync(alice, request.OrganizationMembershipRequestId);
        Assert.Equal(AuthorityErrorCodes.OperatorAlreadyApproved, await RefusedAsync(() => DecideAsync(alice, request.OrganizationMembershipRequestId)));
        Assert.False(IsMember(request));
        Assert.Single(store.PlatformApprovals, a => a.ApprovalStateName == PlatformApprovalStates.Pending);
        Assert.Contains(store.PlatformJournal, j => j.JournalEventKind == "PlatformOperatorDecisionRefused" && j.JournalEventBodyJson.Contains(AuthorityErrorCodes.OperatorAlreadyApproved, StringComparison.Ordinal));
        Assert.Contains(store.Journal, j => j.JournalEventKind == "PlatformOperatorDecisionRefused");
    }

    [Fact]
    public async Task AnOperatorCannotImpersonateAnotherOperator()
    {
        var request = await RequestAsync("new-person-03");
        var id = request.OrganizationMembershipRequestId;
        var sha = await StatementAsync(alice, id);
        // Alice's session submitting Blake's genuine evidence (both "approvals" from one session).
        var blakes = Evidence(blake, PlatformDecisions.Approve, PlatformOperationKinds.MembershipApproval, id, Org, sha, Justification);
        Assert.Equal(AuthorityErrorCodes.OperatorEvidenceIdentityMismatch, await RefusedAsync(() => DecideAsync(alice, id, evidence: blakes)));
        // Evidence claiming Alice's key and id but signed with Blake's key.
        var forged = Evidence(alice, PlatformDecisions.Approve, PlatformOperationKinds.MembershipApproval, id, Org, sha, Justification, signer: blake.Key);
        Assert.Equal(AuthorityErrorCodes.OperatorEvidenceInvalid, await RefusedAsync(() => DecideAsync(alice, id, evidence: forged)));
        // Alice's key, but claiming to be Blake's operator id.
        var relabelled = Evidence(alice, PlatformDecisions.Approve, PlatformOperationKinds.MembershipApproval, id, Org, sha, Justification, tamper: c => c[PlatformOperatorEvidenceClaims.PlatformOperatorId] = blake.Id.ToString("D"));
        Assert.Equal(AuthorityErrorCodes.OperatorEvidenceIdentityMismatch, await RefusedAsync(() => DecideAsync(alice, id, evidence: relabelled)));
        Assert.Empty(store.PlatformApprovals);
        Assert.False(IsMember(request));
    }

    [Fact]
    public async Task ReplayedExpiredAndMisboundEvidenceIsRefused()
    {
        var request = await RequestAsync("new-person-04");
        var id = request.OrganizationMembershipRequestId;
        var sha = await StatementAsync(alice, id);
        var evidence = Evidence(alice, PlatformDecisions.Approve, PlatformOperationKinds.MembershipApproval, id, Org, sha, Justification);
        await DecideAsync(alice, id, evidence: evidence);
        await DecideAsync(alice, id, PlatformDecisions.Withdraw);
        Assert.Equal(AuthorityErrorCodes.OperatorEvidenceReplayed, await RefusedAsync(() => DecideAsync(alice, id, evidence: evidence)));

        string Misbound(Action<Dictionary<string, object>> tamper) => Evidence(alice, PlatformDecisions.Approve, PlatformOperationKinds.MembershipApproval, id, Org, sha, Justification, tamper: tamper);
        Assert.Equal(AuthorityErrorCodes.OperatorEvidenceInvalid, await RefusedAsync(() => DecideAsync(alice, id, evidence: Misbound(c => c[PlatformOperatorEvidenceClaims.Decision] = PlatformDecisions.Reject))));
        Assert.Equal(AuthorityErrorCodes.OperatorEvidenceInvalid, await RefusedAsync(() => DecideAsync(alice, id, evidence: Misbound(c => c[PlatformOperatorEvidenceClaims.OperationId] = Guid.CreateVersion7().ToString("D")))));
        Assert.Equal(AuthorityErrorCodes.OperatorEvidenceInvalid, await RefusedAsync(() => DecideAsync(alice, id, evidence: Misbound(c => c[PlatformOperatorEvidenceClaims.OrganizationId] = Guid.CreateVersion7().ToString("D")))));
        Assert.Equal(AuthorityErrorCodes.OperatorEvidenceInvalid, await RefusedAsync(() => DecideAsync(alice, id, evidence: Misbound(c => c[PlatformOperatorEvidenceClaims.JustificationDigest] = new string('0', 64)))));
        Assert.Equal(AuthorityErrorCodes.OperatorEvidenceInvalid, await RefusedAsync(() => DecideAsync(alice, id, evidence: Misbound(c => c[PlatformOperatorEvidenceClaims.StatementDigest] = new string('a', 64)))));
        Assert.Equal(AuthorityErrorCodes.OperatorEvidenceInvalid, await RefusedAsync(() => DecideAsync(alice, id, evidence: Evidence(alice, PlatformDecisions.Approve, PlatformOperationKinds.MembershipApproval, id, Org, sha, Justification, lifetime: TimeSpan.FromHours(1)))));

        var stale = Evidence(alice, PlatformDecisions.Approve, PlatformOperationKinds.MembershipApproval, id, Org, sha, Justification);
        clock.Advance(TimeSpan.FromMinutes(6));
        Assert.Equal(AuthorityErrorCodes.OperatorEvidenceInvalid, await RefusedAsync(() => DecideAsync(alice, id, evidence: stale)));
        Assert.Equal(AuthorityErrorCodes.OperatorEvidenceInvalid, await RefusedAsync(() => Approvals().ApproveAsync(alice.Actor, Org, id, new PlatformMembershipDecisionRequest(sha, Justification, false), Guid.CreateVersion7(), Ct)));
        Assert.Equal(AuthorityErrorCodes.OperatorEvidenceUnavailable, await RefusedAsync(() => Approvals().ApproveAsync(alice.Actor, Org, id, new PlatformMembershipDecisionRequest(sha, Justification, false, PlatformOperatorEvidenceKinds.EntraTransactionStepUp, "eyJ.generic.entra-token"), Guid.CreateVersion7(), Ct)));
        Assert.Equal(AuthorityErrorCodes.ApprovalStatementChanged, await RefusedAsync(() => DecideAsync(alice, id, expected: new string('b', 64))));
        Assert.DoesNotContain(store.PlatformApprovals, a => a.ApprovalStateName == PlatformApprovalStates.Pending);
    }

    [Fact]
    public async Task ReplacingOrCancellingARequestAfterTheFirstApprovalInvalidatesIt()
    {
        var request = await RequestAsync("new-person-05");
        await DecideAsync(alice, request.OrganizationMembershipRequestId);
        var oldSha = await StatementAsync(blake, request.OrganizationMembershipRequestId);
        await new SecurityCommandService(store, new NoFaults(), clock, StepUp).SubmitAsync(new VerifiedActor(OrgIssuer, "subject-" + OwnerB.ToString("N"), "test"),
            new SecurityCommandRequest(Org, SecurityCommandKind.ReplaceOrganizationMembershipRequest, new ResourceRef(ResourceKind.Organization, Org), store.Organizations.Single(o => o.OrganizationId == Org).AuthorizationRevision,
                JsonSerializer.SerializeToElement(new ReplaceMembershipRequestPayload(request.OrganizationMembershipRequestId, MembershipRequestDigest.Compute(request), "Nia Newcomer-Okafor", null, "corrected after the intake call", FieldSearchApprovalGate.SyntheticStepUpEvidence), CanonicalJson.Options)),
            "po-" + Guid.NewGuid().ToString("N"), Guid.CreateVersion7(), Ct);
        var aliceApproval = store.PlatformApprovals.Single(a => a.PlatformOperatorId == alice.Id);
        Assert.Equal(PlatformApprovalStates.Invalidated, aliceApproval.ApprovalStateName);
        Assert.Equal(PlatformApprovalClosureReasons.RequestReplaced, aliceApproval.ApprovalClosureReasonName);
        Assert.Contains(store.Journal, j => j.JournalEventKind == "PlatformOperatorApprovalsInvalidated");

        // Blake approving the old request (the statement he reviewed) is refused; nothing carries over to the replacement.
        var evidence = Evidence(blake, PlatformDecisions.Approve, PlatformOperationKinds.MembershipApproval, request.OrganizationMembershipRequestId, Org, oldSha, Justification);
        Assert.Contains(await RefusedAsync(() => DecideAsync(blake, request.OrganizationMembershipRequestId, evidence: evidence, expected: oldSha)), new[] { AuthorityErrorCodes.ApprovalStatementChanged, AuthorityErrorCodes.MembershipRequestNotPending });
        var replacement = store.MembershipRequests.Single(r => r.ReplacesMembershipRequestId == request.OrganizationMembershipRequestId);
        Assert.Equal("AwaitingSecondApproval", (await DecideAsync(blake, replacement.OrganizationMembershipRequestId)).Outcome);
        Assert.False(IsMember(replacement));

        // Cancel after the first approval of the replacement.
        await new SecurityCommandService(store, new NoFaults(), clock, StepUp).SubmitAsync(new VerifiedActor(OrgIssuer, "subject-" + OwnerA.ToString("N"), "test"),
            new SecurityCommandRequest(Org, SecurityCommandKind.CancelOrganizationMembershipRequest, new ResourceRef(ResourceKind.Organization, Org), store.Organizations.Single(o => o.OrganizationId == Org).AuthorizationRevision,
                JsonSerializer.SerializeToElement(new CancelMembershipRequestPayload(replacement.OrganizationMembershipRequestId, MembershipRequestDigest.Compute(replacement), "entered for the wrong unit", FieldSearchApprovalGate.SyntheticStepUpEvidence), CanonicalJson.Options)),
            "po-" + Guid.NewGuid().ToString("N"), Guid.CreateVersion7(), Ct);
        Assert.Equal(PlatformApprovalClosureReasons.RequestCancelled, store.PlatformApprovals.Single(a => a.OrganizationMembershipRequestId == replacement.OrganizationMembershipRequestId).ApprovalClosureReasonName);
        Assert.Contains(await RefusedAsync(() => DecideAsync(casey, replacement.OrganizationMembershipRequestId)), new[] { AuthorityErrorCodes.MembershipRequestNotPending });
        Assert.Equal(0, ApprovalCommands);
    }

    [Fact]
    public async Task APendingApprovalExpiresAfter24HoursAndNeverOutlivesTheRequest()
    {
        var request = await RequestAsync("new-person-06");
        var nearExpiry = await RequestAsync("new-person-07");
        var first = await DecideAsync(alice, request.OrganizationMembershipRequestId);
        Assert.Equal(clock.GetUtcNow().AddHours(24), store.PlatformApprovals.Single().ApprovalValidUntil);
        clock.Advance(TimeSpan.FromHours(25));
        var late = await DecideAsync(blake, request.OrganizationMembershipRequestId);
        Assert.Equal("AwaitingSecondApproval", late.Outcome); // Alice's approval expired, so Blake's is a new first approval
        Assert.False(IsMember(request));
        Assert.Equal(PlatformApprovalClosureReasons.PendingApprovalExpired, store.PlatformApprovals.Single(a => a.PlatformOperatorId == alice.Id).ApprovalClosureReasonName);
        Assert.NotEqual(first.MyApprovalId, late.MyApprovalId);

        // One hour before the request expires: the approval is valid only until the request's expiry, not for 24 hours.
        clock.Advance(nearExpiry.RequestExpiresOn - clock.GetUtcNow() - TimeSpan.FromHours(1));
        await DecideAsync(alice, nearExpiry.OrganizationMembershipRequestId);
        Assert.Equal(nearExpiry.RequestExpiresOn, store.PlatformApprovals.Single(a => a.OrganizationMembershipRequestId == nearExpiry.OrganizationMembershipRequestId).ApprovalValidUntil);

        clock.Advance(TimeSpan.FromHours(2));
        Assert.Equal(AuthorityErrorCodes.MembershipRequestExpired, await RefusedAsync(() => DecideAsync(casey, nearExpiry.OrganizationMembershipRequestId)));
        Assert.Equal(MembershipRequestStates.Expired, nearExpiry.RequestStateName);
        Assert.Equal(PlatformApprovalClosureReasons.RequestExpired, store.PlatformApprovals.Single(a => a.OrganizationMembershipRequestId == nearExpiry.OrganizationMembershipRequestId).ApprovalClosureReasonName);
        Assert.False(IsMember(nearExpiry));
        Assert.False(IsMember(request));
    }

    [Fact]
    public async Task ASuspendedOperatorCannotActAndTheirPendingApprovalIsInvalidated()
    {
        var request = await RequestAsync("new-person-08");
        await DecideAsync(alice, request.OrganizationMembershipRequestId);
        var suspension = await SuspendAsync(casey, alice);
        Assert.Equal("Suspended", suspension.Outcome);
        Assert.Equal(PlatformOperatorStates.Suspended, Row(alice).OperatorStatusName);
        Assert.Equal(PlatformApprovalClosureReasons.OperatorSuspended, store.PlatformApprovals.Single().ApprovalClosureReasonName);
        Assert.Contains(alerts.Raised, a => a.AlertKind == "PlatformOperatorSuspended");

        Assert.Equal(AuthorityErrorCodes.PlatformOperatorNotActive, await RefusedAsync(() => DecideAsync(alice, request.OrganizationMembershipRequestId)));
        Assert.Equal("AwaitingSecondApproval", (await DecideAsync(blake, request.OrganizationMembershipRequestId)).Outcome);
        Assert.False(IsMember(request));
        Assert.Equal(AuthorityErrorCodes.PlatformOperatorNotActive, await RefusedAsync(() => Approvals().SearchAsync(alice.Actor, new PlatformMembershipRequestSearchRequest(), Guid.CreateVersion7(), Ct)));
    }

    [Fact]
    public async Task AfterTheSecondApprovalFurtherApprovalsAreRefusedAndOnlyOneMembershipExists()
    {
        var request = await RequestAsync("new-person-09");
        await DecideAsync(alice, request.OrganizationMembershipRequestId);
        await DecideAsync(blake, request.OrganizationMembershipRequestId);
        Assert.Equal(AuthorityErrorCodes.MembershipRequestNotPending, await RefusedAsync(() => DecideAsync(casey, request.OrganizationMembershipRequestId)));
        Assert.Equal(1, ApprovalCommands);
        Assert.Single(store.Bindings, b => b.AuthorityUserId == request.RequestedAuthorityUserId);
    }

    [Fact]
    public async Task AnOperatorWhoBelongsToTheOrganizationRequestedOrIsTheMemberCannotDecide()
    {
        var request = await RequestAsync("new-person-10");
        store.Bindings.Add(new IdentityBinding { IdentityBindingId = Guid.CreateVersion7(), OrganizationId = Org, AuthorityUserId = Guid.CreateVersion7(), IdentityIssuerUri = OperatorIssuer, IdentitySubjectIdentifier = casey.Subject, IdentityBindingEnabled = false });
        Assert.Equal(AuthorityErrorCodes.OperatorConflictOfInterest, await RefusedAsync(() => DecideAsync(casey, request.OrganizationMembershipRequestId)));
        Assert.Equal(AuthorityErrorCodes.OperatorConflictOfInterest, await RefusedAsync(() => DecideAsync(casey, request.OrganizationMembershipRequestId, PlatformDecisions.Reject)));

        var self = new OrganizationMembershipRequest
        {
            OrganizationMembershipRequestId = Guid.CreateVersion7(), OrganizationId = Org, RequestedAuthorityUserId = Guid.CreateVersion7(), RequestedIssuerUri = OperatorIssuer, RequestedSubjectIdentifier = blake.Subject,
            RequestedDisplayNameHint = "Blake", RequestedByAuthorityUserId = OwnerA, RequestReasonText = "self", RequestCreatedOn = clock.GetUtcNow(), RequestExpiresOn = clock.GetUtcNow().AddDays(7),
        };
        store.MembershipRequests.Add(self);
        Assert.Equal(AuthorityErrorCodes.OperatorConflictOfInterest, await RefusedAsync(() => DecideAsync(blake, self.OrganizationMembershipRequestId)));
        Assert.Empty(store.PlatformApprovals);
    }

    [Fact]
    public async Task OnlyTheOriginalOperatorWithdrawsTheirApproval()
    {
        var request = await RequestAsync("new-person-11");
        await DecideAsync(alice, request.OrganizationMembershipRequestId);
        Assert.Equal(AuthorityErrorCodes.NoPendingApproval, await RefusedAsync(() => DecideAsync(blake, request.OrganizationMembershipRequestId, PlatformDecisions.Withdraw)));
        Assert.Equal("Withdrawn", (await DecideAsync(alice, request.OrganizationMembershipRequestId, PlatformDecisions.Withdraw)).Outcome);
        Assert.Equal(PlatformApprovalStates.Withdrawn, store.PlatformApprovals.Single().ApprovalStateName);
        Assert.Equal("AwaitingSecondApproval", (await DecideAsync(blake, request.OrganizationMembershipRequestId)).Outcome);
        Assert.False(IsMember(request));
    }

    [Fact]
    public async Task OneOperatorRejectsWithJustificationAndEvidenceAndNothingIsGranted()
    {
        var request = await RequestAsync("new-person-12");
        await DecideAsync(alice, request.OrganizationMembershipRequestId);
        Assert.Equal(AuthorityErrorCodes.InvalidCommand, await RefusedAsync(() => DecideAsync(blake, request.OrganizationMembershipRequestId, PlatformDecisions.Reject, justification: "short")));
        var rejected = await DecideAsync(blake, request.OrganizationMembershipRequestId, PlatformDecisions.Reject);
        Assert.Equal("Rejected", rejected.Outcome);
        Assert.Equal(MembershipRequestStates.Rejected, request.RequestStateName);
        Assert.Equal(blake.Id, request.RequestDecidedByPlatformOperatorId);
        Assert.Equal(PlatformApprovalClosureReasons.RequestRejected, store.PlatformApprovals.Single().ApprovalClosureReasonName);
        Assert.Contains(store.Journal, j => j.JournalEventKind == "OrganizationMembershipRejected");
        Assert.Equal(AuthorityErrorCodes.MembershipRequestNotPending, await RefusedAsync(() => DecideAsync(casey, request.OrganizationMembershipRequestId)));
        Assert.False(IsMember(request));
    }

    [Fact]
    public async Task TheLegacyOperatorPlaneApprovalClosesAPendingPlatformApproval()
    {
        var request = await RequestAsync("new-person-13");
        await DecideAsync(alice, request.OrganizationMembershipRequestId);
        var legacy = new MembershipProvisioningService(store, Custodians, clock);
        var preview = await legacy.ApproveAsync(Org, request.OrganizationMembershipRequestId, new MembershipApprovalRequest(Org, Justification, true), "operator-test", Guid.CreateVersion7(), Ct);
        await legacy.ApproveAsync(Org, request.OrganizationMembershipRequestId, new MembershipApprovalRequest(Org, Justification, false,
            [Custodian(custodianOne, "custodian-1", preview.ApprovalStatementSha256, preview.OperationId, preview.OperationKind, Org, preview.Target.AuthorityUserId), Custodian(custodianTwo, "custodian-2", preview.ApprovalStatementSha256, preview.OperationId, preview.OperationKind, Org, preview.Target.AuthorityUserId)]),
            "operator-test", Guid.CreateVersion7(), Ct);
        Assert.True(IsMember(request));
        Assert.Equal(PlatformApprovalClosureReasons.RequestDecidedElsewhere, store.PlatformApprovals.Single().ApprovalClosureReasonName);
        Assert.Equal(AuthorityErrorCodes.MembershipRequestNotPending, await RefusedAsync(() => DecideAsync(blake, request.OrganizationMembershipRequestId)));
        Assert.Equal(1, ApprovalCommands);
    }

    // ---- separation: operators gain nothing in organizations ----

    [Fact]
    public async Task OperatorsGainNoOrganizationMembershipRolesOrAccess()
    {
        var request = await RequestAsync("new-person-14");
        await DecideAsync(alice, request.OrganizationMembershipRequestId);
        await DecideAsync(blake, request.OrganizationMembershipRequestId);
        foreach (var op in new[] { alice, blake })
        {
            Assert.DoesNotContain(store.Bindings, b => b.IdentityIssuerUri == OperatorIssuer && b.IdentitySubjectIdentifier == op.Subject);
            Assert.DoesNotContain(store.Edges, e => e.SubjectEntityId == op.Id);
            // An operator identity presented as an organization user is not bound: every organization command is refused.
            await Assert.ThrowsAsync<AuthorityException>(() => new SecurityCommandService(store, new NoFaults(), clock, StepUp).SubmitAsync(op.Actor,
                new SecurityCommandRequest(Org, SecurityCommandKind.RequestOrganizationMembership, new ResourceRef(ResourceKind.Organization, Org), store.Organizations.Single(o => o.OrganizationId == Org).AuthorizationRevision,
                    JsonSerializer.SerializeToElement(new MembershipRequestPayload(OrgIssuer, "someone-else", "X", null, "operator attempt", FieldSearchApprovalGate.SyntheticStepUpEvidence), CanonicalJson.Options)),
                "po-" + Guid.NewGuid().ToString("N"), Guid.CreateVersion7(), Ct));
        }

        // An organization owner is not a Platform Operator.
        Assert.Equal(AuthorityErrorCodes.PlatformOperatorNotActive, await RefusedAsync(() => Approvals().SearchAsync(new VerifiedActor(OrgIssuer, "subject-" + OwnerA.ToString("N"), "platform-console"), new PlatformMembershipRequestSearchRequest(), Guid.CreateVersion7(), Ct)));
    }

    [Fact]
    public async Task EverythingIsRefusedWhenThePlatformPlaneIsUnavailableAndSyntheticModeIsDevelopmentOnly()
    {
        var request = await RequestAsync("new-person-15");
        var unavailable = PlatformOperatorPolicy.Unavailable;
        Assert.Equal(AuthorityErrorCodes.PlatformOperatorsUnavailable, await RefusedAsync(() => Approvals(unavailable).SearchAsync(alice.Actor, new PlatformMembershipRequestSearchRequest(), Guid.CreateVersion7(), Ct)));
        Assert.Equal(AuthorityErrorCodes.PlatformOperatorsUnavailable, await RefusedAsync(() => Approvals(unavailable).ApproveAsync(alice.Actor, Org, request.OrganizationMembershipRequestId, new PlatformMembershipDecisionRequest("x", Justification, true), Guid.CreateVersion7(), Ct)));
        Assert.Equal(AuthorityErrorCodes.PlatformOperatorsUnavailable, await RefusedAsync(() => Registry(unavailable).GetProfileAsync(alice.Actor, Guid.CreateVersion7(), Ct)));
        Assert.Throws<InvalidOperationException>(() => PlatformOperatorPolicy.Create(new PlatformOperatorOptions { Mode = PlatformOperatorPolicy.SyntheticMode }, "Production"));
        Assert.Throws<InvalidOperationException>(() => PlatformOperatorPolicy.Create(new PlatformOperatorOptions { Mode = PlatformOperatorPolicy.SyntheticMode }, "Staging"));
        Assert.Throws<InvalidOperationException>(() => PlatformOperatorPolicy.Create(new PlatformOperatorOptions { Mode = "EntraTransactionStepUp" }, "Production"));
    }

    // ---- cross-organization reads ----

    [Fact]
    public async Task OperatorsSearchRequestsAcrossOrganizationsWithBoundCursorsAndSeeOnlyMetadata()
    {
        var other = Guid.CreateVersion7();
        store.Organizations.Add(new OrganizationAuthorizationState { OrganizationId = other, OrganizationDisplayName = "Harbour Legal", AuthorizationRevision = 1, ProjectionAppliedRevision = 1 });
        var ids = new List<Guid>();
        for (var i = 0; i < 17; i++)
        {
            ids.Add((await RequestAsync($"new-person-2{i:00}")).OrganizationMembershipRequestId);
            clock.Advance(TimeSpan.FromSeconds(1));
        }

        store.MembershipRequests.Add(new OrganizationMembershipRequest
        {
            OrganizationMembershipRequestId = Guid.CreateVersion7(), OrganizationId = other, RequestedAuthorityUserId = Guid.CreateVersion7(), RequestedIssuerUri = "https://other-idp", RequestedSubjectIdentifier = "harbour-person",
            RequestedDisplayNameHint = "Harbour Person", RequestedByAuthorityUserId = Guid.CreateVersion7(), RequestReasonText = "joins", RequestCreatedOn = clock.GetUtcNow(), RequestExpiresOn = clock.GetUtcNow().AddDays(7),
        });
        await DecideAsync(alice, ids[0]);

        var service = Approvals();
        var page = await service.SearchAsync(blake.Actor, new PlatformMembershipRequestSearchRequest(), Guid.CreateVersion7(), Ct);
        Assert.Equal(15, page.Items.Count);
        Assert.Equal(18, page.TotalCount);
        Assert.Contains(page.Items, i => i.OrganizationDisplayName == "Harbour Legal");
        Assert.NotNull(page.NextCursor);
        var next = await service.SearchAsync(blake.Actor, new PlatformMembershipRequestSearchRequest(Cursor: page.NextCursor), Guid.CreateVersion7(), Ct);
        Assert.Equal(3, next.Items.Count);
        Assert.Empty(page.Items.Select(i => i.RequestId).Intersect(next.Items.Select(i => i.RequestId)));
        Assert.Equal(AuthorityErrorCodes.CursorInvalid, await RefusedAsync(() => service.SearchAsync(casey.Actor, new PlatformMembershipRequestSearchRequest(Cursor: page.NextCursor), Guid.CreateVersion7(), Ct)));
        Assert.Equal(AuthorityErrorCodes.CursorInvalid, await RefusedAsync(() => service.SearchAsync(blake.Actor, new PlatformMembershipRequestSearchRequest(Status: PlatformRequestFilters.All, Cursor: page.NextCursor), Guid.CreateVersion7(), Ct)));
        Assert.Equal(AuthorityErrorCodes.CursorInvalid, await RefusedAsync(() => service.SearchAsync(blake.Actor, new PlatformMembershipRequestSearchRequest(Cursor: page.NextCursor![..^2] + "AA"), Guid.CreateVersion7(), Ct)));

        var second = await service.SearchAsync(blake.Actor, new PlatformMembershipRequestSearchRequest(Status: PlatformRequestFilters.AwaitingSecondApproval), Guid.CreateVersion7(), Ct);
        var only = Assert.Single(second.Items);
        Assert.Equal(ids[0], only.RequestId);
        Assert.Equal(1, only.ApprovalStatus.Recorded);
        Assert.True((await service.SearchAsync(alice.Actor, new PlatformMembershipRequestSearchRequest(Status: PlatformRequestFilters.AwaitingSecondApproval), Guid.CreateVersion7(), Ct)).Items.Single().ApprovalStatus.ApprovedByMe);
        Assert.Single((await service.SearchAsync(blake.Actor, new PlatformMembershipRequestSearchRequest(Query: "harbour"), Guid.CreateVersion7(), Ct)).Items);
        Assert.Equal(AuthorityErrorCodes.InvalidCommand, await RefusedAsync(() => service.SearchAsync(blake.Actor, new PlatformMembershipRequestSearchRequest(Limit: 51), Guid.CreateVersion7(), Ct)));

        var detail = await service.GetDetailAsync(blake.Actor, Org, ids[0], Guid.CreateVersion7(), Ct);
        Assert.True(detail.MyEligibility.CanApprove);
        Assert.Single(detail.Approvals, a => a.PlatformOperatorId == alice.Id && a.Justification == Justification);
        Assert.Equal(2, JsonDocument.Parse(detail.StatementJson).RootElement.GetProperty("statement_version").GetInt32());
        Assert.DoesNotContain("justification", detail.StatementJson, StringComparison.Ordinal);
        Assert.Contains(store.PlatformJournal, j => j.JournalEventKind == "PlatformMembershipRequestViewed");
        Assert.False((await service.GetDetailAsync(alice.Actor, Org, ids[0], Guid.CreateVersion7(), Ct)).MyEligibility.CanApprove);
    }

    // ---- registry: provision, reinstate, remove, suspend, recovery ----

    private Task<PlatformOperatorDecisionResponse> SuspendAsync(Op actor, Op target)
    {
        var operationId = Guid.CreateVersion7();
        var statement = CanonicalJson.Serialize(new PlatformOperatorSuspensionStatement(1, PlatformOperationKinds.PlatformOperatorSuspension, operationId, target.Id, OperatorIssuer, target.Subject, Row(target).OperatorRowRevision, actor.Id));
        var sha = Convert.ToHexStringLower(CanonicalJson.Digest(statement));
        return Registry().SuspendAsync(actor.Actor, target.Id, new PlatformOperatorSuspensionRequest(operationId, Justification, false, Synthetic,
            Evidence(actor, PlatformDecisions.Suspend, PlatformOperationKinds.PlatformOperatorSuspension, operationId, Guid.Empty, sha, Justification)), Guid.CreateVersion7(), Ct);
    }

    private async Task<PlatformOperatorDecisionResponse> ProposeAsync(Op actor, string change, Guid? target = null, PlatformOperatorCandidate? candidate = null)
    {
        var operationId = Guid.CreateVersion7();
        var preview = await Registry().ProposeAsync(actor.Actor, new PlatformOperatorProposalRequest(operationId, change, target, candidate, Justification, true), Guid.CreateVersion7(), Ct);
        if (!preview.Eligible)
        {
            throw new AuthorityException(preview.RefusalCode!, 409, false, preview.RefusalDetail);
        }

        return await Registry().ProposeAsync(actor.Actor, new PlatformOperatorProposalRequest(operationId, change, target, candidate, Justification, false, Synthetic,
            Evidence(actor, PlatformDecisions.Propose, PlatformOperationKinds.PlatformOperatorChange, operationId, Guid.Empty, preview.StatementSha256, Justification)), Guid.CreateVersion7(), Ct);
    }

    private async Task<PlatformOperatorDecisionResponse> ConfirmAsync(Op actor, Guid proposalId)
    {
        var preview = await Registry().ConfirmAsync(actor.Actor, proposalId, new PlatformOperatorConfirmationRequest(string.Empty, Justification, true), Guid.CreateVersion7(), Ct);
        return await Registry().ConfirmAsync(actor.Actor, proposalId, new PlatformOperatorConfirmationRequest(preview.StatementSha256, Justification, false, Synthetic,
            Evidence(actor, PlatformDecisions.Confirm, PlatformOperationKinds.PlatformOperatorChange, proposalId, Guid.Empty, preview.StatementSha256, Justification)), Guid.CreateVersion7(), Ct);
    }

    private static PlatformOperatorCandidate Candidate(string subject, ECDsa key, string keyId) => new(OperatorIssuer, subject, "Dana Operator", Synthetic, keyId, Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()));

    [Fact]
    public async Task ProvisioningReinstatementAndRemovalNeedTwoDistinctOperatorsAndNobodyChangesThemself()
    {
        using var danaKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var proposal = await ProposeAsync(alice, PlatformOperatorChanges.Provision, candidate: Candidate("operator-dana", danaKey, "key-dana"));
        Assert.Equal("ProposalRecorded", proposal.Outcome);
        Assert.DoesNotContain(store.PlatformOperators, o => o.OperatorSubjectIdentifier == "operator-dana");
        Assert.Equal(AuthorityErrorCodes.OperatorApprovalsNotIndependent, await RefusedAsync(() => ConfirmAsync(alice, proposal.ProposalId!.Value)));
        var provisioned = await ConfirmAsync(blake, proposal.ProposalId!.Value);
        Assert.Equal("Provisioned", provisioned.Outcome);
        var dana = new Op(proposal.ProposalId.Value, "operator-dana", "key-dana", danaKey);
        Assert.Equal(PlatformOperatorStates.Active, Row(dana).OperatorStatusName);
        Assert.Equal(alice.Id, Row(dana).ProvisionedByPlatformOperatorId);
        Assert.Equal(AuthorityErrorCodes.ProposalNotPending, await RefusedAsync(() => ConfirmAsync(casey, proposal.ProposalId.Value)));

        // Identities and keys are never reused; nobody provisions their own identity.
        Assert.Equal(AuthorityErrorCodes.OperatorIdentityExists, await RefusedAsync(() => ProposeAsync(casey, PlatformOperatorChanges.Provision, candidate: Candidate("operator-dana", danaKey, "key-dana-2"))));
        using var otherKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        Assert.Equal(AuthorityErrorCodes.SelfChangeForbidden, await RefusedAsync(() => ProposeAsync(casey, PlatformOperatorChanges.Provision, candidate: Candidate(casey.Subject, otherKey, "key-casey-2"))));

        // Suspend (one operator), then reinstate (two operators, neither the target).
        await SuspendAsync(alice, dana);
        Assert.Equal(PlatformOperatorStates.Suspended, Row(dana).OperatorStatusName);
        var reinstate = await ProposeAsync(blake, PlatformOperatorChanges.Reinstate, dana.Id);
        Assert.Equal(AuthorityErrorCodes.PlatformOperatorNotActive, await RefusedAsync(() => ConfirmAsync(dana, reinstate.ProposalId!.Value)));
        Assert.Equal("Reinstated", (await ConfirmAsync(casey, reinstate.ProposalId!.Value)).Outcome);
        Assert.Equal(PlatformOperatorStates.Active, Row(dana).OperatorStatusName);

        // Removal: the target cannot confirm their own removal, and the proposer must still be active at confirmation.
        Assert.Equal(AuthorityErrorCodes.SelfChangeForbidden, await RefusedAsync(() => ProposeAsync(dana, PlatformOperatorChanges.Remove, dana.Id)));
        var removal = await ProposeAsync(alice, PlatformOperatorChanges.Remove, dana.Id);
        Assert.Equal(AuthorityErrorCodes.SelfChangeForbidden, await RefusedAsync(() => ConfirmAsync(dana, removal.ProposalId!.Value)));
        Assert.Equal("Removed", (await ConfirmAsync(blake, removal.ProposalId!.Value)).Outcome);
        Assert.Equal(PlatformOperatorStates.Removed, Row(dana).OperatorStatusName);
        Assert.Equal(AuthorityErrorCodes.PlatformOperatorNotActive, await RefusedAsync(() => Registry().GetProfileAsync(dana.Actor, Guid.CreateVersion7(), Ct)));

        var stale = await ProposeAsync(casey, PlatformOperatorChanges.Remove, blake.Id);
        await SuspendAsync(alice, casey);
        Assert.Equal(AuthorityErrorCodes.PlatformOperatorNotActive, await RefusedAsync(() => ConfirmAsync(alice, stale.ProposalId!.Value)));
        Assert.Contains(alerts.Raised, a => a.AlertKind == "PlatformOperatorRemoved");
    }

    [Fact]
    public async Task ARegistryProposalExpiresAndAStaleTargetStatementIsRefused()
    {
        var removal = await ProposeAsync(alice, PlatformOperatorChanges.Remove, casey.Id);
        clock.Advance(TimeSpan.FromHours(25));
        Assert.Equal(AuthorityErrorCodes.ProposalExpired, await RefusedAsync(() => ConfirmAsync(blake, removal.ProposalId!.Value)));

        var second = await ProposeAsync(alice, PlatformOperatorChanges.Remove, casey.Id);
        Row(casey).OperatorRowRevision++; // the target changed after the proposal (e.g. suspended and reinstated)
        Assert.Equal(AuthorityErrorCodes.ApprovalStatementChanged, await RefusedAsync(() => ConfirmAsync(blake, second.ProposalId!.Value)));
        Assert.Equal(PlatformOperatorStates.Active, Row(casey).OperatorStatusName);
    }

    [Fact]
    public async Task TheRecoveryCeremonyAppliesOnlyBelowTwoActiveOperators()
    {
        using var dana = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var erin = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var candidates = new[] { Candidate("operator-dana", dana, "key-dana"), Candidate("operator-erin", erin, "key-erin") with { DisplayName = "Erin Operator" } };
        var registry = Registry();
        var refused = await registry.BootstrapAsync(new PlatformOperatorBootstrapRequest(Guid.CreateVersion7(), candidates, Justification, "INC-9", true), "operator-test", Guid.CreateVersion7(), Ct);
        Assert.False(refused.Eligible);
        Assert.Equal(AuthorityErrorCodes.OperatorQuorumPresent, refused.RefusalCode);

        await SuspendAsync(alice, blake);
        await SuspendAsync(alice, casey);
        Assert.Equal(AuthorityErrorCodes.OperatorQuorumUnavailable, await RefusedAsync(() => ProposeAsync(alice, PlatformOperatorChanges.Reinstate, blake.Id)));

        var operationId = Guid.CreateVersion7();
        var preview = await registry.BootstrapAsync(new PlatformOperatorBootstrapRequest(operationId, candidates, Justification, "INC-9", true), "operator-test", Guid.CreateVersion7(), Ct);
        Assert.True(preview.Eligible, preview.RefusalDetail);
        Assert.Equal(AuthorityErrorCodes.OperatorApprovalsNotIndependent, await RefusedAsync(() => registry.BootstrapAsync(new PlatformOperatorBootstrapRequest(operationId, candidates, Justification, "INC-9", false,
            [Ceremony(custodianOne, "custodian-1", preview), Ceremony(custodianOne, "custodian-1", preview)]), "operator-test", Guid.CreateVersion7(), Ct)));
        var done = await registry.BootstrapAsync(new PlatformOperatorBootstrapRequest(operationId, candidates, Justification, "INC-9", false,
            [Ceremony(custodianOne, "custodian-1", preview), Ceremony(custodianTwo, "custodian-2", preview)]), "operator-test", Guid.CreateVersion7(), Ct);
        Assert.True(done.Eligible);
        Assert.Equal(3, store.PlatformOperators.Count(o => o.OperatorStatusName == PlatformOperatorStates.Active));
        Assert.Contains(alerts.Raised, a => a.AlertKind == "PlatformOperatorsBootstrapped" && a.Severity == "Critical");
        Assert.Equal(1, Journaled("PlatformOperatorsBootstrapped"));
        Assert.Equal(AuthorityErrorCodes.OperatorQuorumPresent, await RefusedAsync(() => registry.BootstrapAsync(new PlatformOperatorBootstrapRequest(operationId, candidates, Justification, "INC-9", false,
            [Ceremony(custodianOne, "custodian-1", preview), Ceremony(custodianTwo, "custodian-2", preview)]), "operator-test", Guid.CreateVersion7(), Ct)));

        // The recovered operators act normally; suspended ones stay suspended (the ceremony never reinstates).
        var request = await RequestAsync("new-person-30");
        var danaOp = new Op(store.PlatformOperators.Single(o => o.OperatorSubjectIdentifier == "operator-dana").PlatformOperatorId, "operator-dana", "key-dana", dana);
        var erinOp = new Op(store.PlatformOperators.Single(o => o.OperatorSubjectIdentifier == "operator-erin").PlatformOperatorId, "operator-erin", "key-erin", erin);
        await DecideAsync(danaOp, request.OrganizationMembershipRequestId);
        Assert.Equal("Approved", (await DecideAsync(erinOp, request.OrganizationMembershipRequestId)).Outcome);
        Assert.Equal(PlatformOperatorStates.Suspended, Row(blake).OperatorStatusName);
    }

    // ---- platform journal: archive, rollback detection, replay, quarantine ----

    [Fact]
    public async Task EveryPlatformEventIsChainedAndAnchoredInTheIndependentArchive()
    {
        var request = await RequestAsync("new-person-40");
        await DecideAsync(alice, request.OrganizationMembershipRequestId);
        await SuspendAsync(alice, casey);
        await DecideAsync(blake, request.OrganizationMembershipRequestId);
        var entries = store.PlatformJournal.OrderBy(e => e.PlatformJournalRevision).ToList();
        Assert.All(entries, e => Assert.Equal("Anchored", e.ArchiveDeliveryState));
        for (var i = 0; i < entries.Count; i++)
        {
            Assert.Equal(i + 1, entries[i].PlatformJournalRevision);
            Assert.Equal(CanonicalJson.Digest(Encoding.UTF8.GetBytes(entries[i].JournalEventBodyJson)), entries[i].JournalEventDigest);
            Assert.Equal(i == 0 ? null : Convert.ToHexStringLower(entries[i - 1].JournalEventDigest), JsonDocument.Parse(entries[i].JournalEventBodyJson).RootElement.GetProperty("previous_digest_hex").GetString());
            Assert.NotNull(await archive.ReadAsync(PlatformJournal.ArchiveScopeId, PlatformJournal.StreamName, entries[i].PlatformJournalRevision, Ct));
        }
    }

    [Fact]
    public async Task ARestoredDatabaseCannotSilentlyReverseASuspensionAndReplayRestoresIt()
    {
        var request = await RequestAsync("new-person-41");
        var beforeSuspension = Snapshot.Take(store);
        await SuspendAsync(alice, casey);
        Assert.Equal(PlatformOperatorStates.Suspended, Row(casey).OperatorStatusName);

        // Simulated AuthorityDb restore from before the suspension: Casey looks Active again in the database.
        beforeSuspension.Restore(store);
        Assert.Equal(PlatformOperatorStates.Active, Row(casey).OperatorStatusName);
        Assert.Equal(AuthorityErrorCodes.PlatformJournalBehindArchive, await RefusedAsync(() => DecideAsync(casey, request.OrganizationMembershipRequestId)));
        Assert.Equal(AuthorityErrorCodes.PlatformJournalBehindArchive, await RefusedAsync(() => Approvals().SearchAsync(casey.Actor, new PlatformMembershipRequestSearchRequest(), Guid.CreateVersion7(), Ct)));
        Assert.Equal(AuthorityErrorCodes.PlatformJournalBehindArchive, await RefusedAsync(() => SuspendAsync(casey, alice)));
        Assert.Contains(alerts.Raised, a => a.AlertKind == "PlatformJournalBehindArchive" && a.Severity == "Critical");
        Assert.Equal(PlatformOperatorStates.Active, Row(alice).OperatorStatusName);

        var replay = await Journal.ReplayAsync("operator-test", Guid.CreateVersion7(), Ct);
        Assert.Equal(1, replay.EventsReplayed);
        Assert.True(replay.GateOpen);
        Assert.Equal(PlatformOperatorStates.Suspended, Row(casey).OperatorStatusName);
        Assert.Equal(AuthorityErrorCodes.PlatformOperatorNotActive, await RefusedAsync(() => DecideAsync(casey, request.OrganizationMembershipRequestId)));
        Assert.Equal("AwaitingSecondApproval", (await DecideAsync(alice, request.OrganizationMembershipRequestId)).Outcome);
    }

    [Fact]
    public async Task ARestoreCannotResurrectAWithdrawnApprovalOrReuseEvidence()
    {
        var request = await RequestAsync("new-person-42");
        var sha = await StatementAsync(alice, request.OrganizationMembershipRequestId);
        var evidence = Evidence(alice, PlatformDecisions.Approve, PlatformOperationKinds.MembershipApproval, request.OrganizationMembershipRequestId, Org, sha, Justification);
        await DecideAsync(alice, request.OrganizationMembershipRequestId, evidence: evidence);
        var afterApproval = Snapshot.Take(store);
        await DecideAsync(alice, request.OrganizationMembershipRequestId, PlatformDecisions.Withdraw);

        afterApproval.Restore(store); // the withdrawn approval is Pending again in the database
        Assert.Equal(AuthorityErrorCodes.PlatformJournalBehindArchive, await RefusedAsync(() => DecideAsync(blake, request.OrganizationMembershipRequestId)));
        Assert.False(IsMember(request));
        await Journal.ReplayAsync("operator-test", Guid.CreateVersion7(), Ct);
        Assert.Equal(PlatformApprovalStates.Withdrawn, store.PlatformApprovals.Single().ApprovalStateName);
        Assert.Equal("AwaitingSecondApproval", (await DecideAsync(blake, request.OrganizationMembershipRequestId)).Outcome);
        Assert.False(IsMember(request));
        Assert.Equal(AuthorityErrorCodes.OperatorEvidenceReplayed, await RefusedAsync(() => DecideAsync(alice, request.OrganizationMembershipRequestId, evidence: evidence)));
    }

    [Fact]
    public async Task AnUnreachableArchiveFailsClosedAndATamperedBodyQuarantinesThePlatformJournal()
    {
        var request = await RequestAsync("new-person-43");
        archive.Available = false;
        Assert.Equal(AuthorityErrorCodes.PlatformJournalUnverifiable, await RefusedAsync(() => DecideAsync(alice, request.OrganizationMembershipRequestId)));
        Assert.Equal(AuthorityErrorCodes.PlatformJournalUnverifiable, await RefusedAsync(() => SuspendAsync(alice, casey)));
        Assert.Empty(store.PlatformApprovals);

        archive.Available = true;
        await DecideAsync(alice, request.OrganizationMembershipRequestId);
        archive.WritesAvailable = false; // reads work, writes fail: the next event stays Pending
        await SuspendAsync(blake, casey);
        Assert.Equal(1, store.PlatformJournal.Count(e => e.ArchiveDeliveryState == "Pending"));
        Assert.Equal(AuthorityErrorCodes.PlatformJournalArchivePending, await RefusedAsync(() => DecideAsync(blake, request.OrganizationMembershipRequestId)));
        Assert.False(IsMember(request));

        var pending = store.PlatformJournal.Single(e => e.ArchiveDeliveryState == "Pending");
        pending.JournalEventBodyJson = pending.JournalEventBodyJson.Replace("Suspended", "Active", StringComparison.Ordinal);
        archive.WritesAvailable = true;
        Assert.Equal("Pending", await Journal.DeliverAsync(Ct));
        Assert.True(store.PlatformHead!.PlatformQuarantineEnabled);
        Assert.Equal(AuthorityErrorCodes.PlatformJournalQuarantined, await RefusedAsync(() => DecideAsync(blake, request.OrganizationMembershipRequestId)));
        Assert.Contains(alerts.Raised, a => a.AlertKind == "PlatformJournalQuarantined");
        Assert.False(IsMember(request));
    }

    // ---- helpers ----

    private string Custodian(ECDsa key, string approverId, string statementSha256, Guid operationId, string operationKind, Guid organizationId, Guid target)
    {
        var now = clock.GetUtcNow();
        var claims = new Dictionary<string, object>
        {
            ["iss"] = OperatorApprovalClaims.Issuer, ["aud"] = OperatorApprovalClaims.Audience, ["sub"] = approverId, ["jti"] = Guid.NewGuid().ToString("N"),
            ["iat"] = now.ToUnixTimeSeconds(), ["nbf"] = now.AddSeconds(-5).ToUnixTimeSeconds(), ["exp"] = now.AddMinutes(10).ToUnixTimeSeconds(),
            [OperatorApprovalClaims.StatementDigest] = statementSha256, [OperatorApprovalClaims.OperationId] = operationId.ToString("D"), [OperatorApprovalClaims.OperationKind] = operationKind,
            [OperatorApprovalClaims.OrganizationId] = organizationId.ToString("D"), [OperatorApprovalClaims.TargetAuthorityUserId] = target.ToString("D"),
        };
        var header = Base64Url.EncodeToString(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { alg = "ES256", typ = "JWT", kid = approverId })));
        var body = Base64Url.EncodeToString(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(claims)));
        return header + "." + body + "." + Base64Url.EncodeToString(key.SignData(Encoding.ASCII.GetBytes(header + "." + body), HashAlgorithmName.SHA256));
    }

    private string Ceremony(ECDsa key, string approverId, PlatformOperatorBootstrapResponse preview) =>
        Custodian(key, approverId, preview.ApprovalStatementSha256, preview.OperationId, preview.OperationKind, Guid.Empty, Guid.Empty);

    /// <summary>An AuthorityDb restore at unit level: the platform tables and membership requests as they were (the archive is untouched).</summary>
    private sealed record Snapshot(string Operators, string Proposals, string Approvals, string Uses, string Journal, string? Head, string Requests)
    {
        public static Snapshot Take(InMemoryAuthorityStore s) => new(J(s.PlatformOperators), J(s.PlatformProposals), J(s.PlatformApprovals), J(s.PlatformEvidenceUses), J(s.PlatformJournal), s.PlatformHead is null ? null : J(s.PlatformHead), J(s.MembershipRequests));

        public void Restore(InMemoryAuthorityStore s)
        {
            Reset(s.PlatformOperators, Operators);
            Reset(s.PlatformProposals, Proposals);
            Reset(s.PlatformApprovals, Approvals);
            Reset(s.PlatformEvidenceUses, Uses);
            Reset(s.PlatformJournal, Journal);
            Reset(s.MembershipRequests, Requests);
            s.PlatformHead = Head is null ? null : JsonSerializer.Deserialize<PlatformJournalHead>(Head);
        }

        private static string J<T>(T value) => JsonSerializer.Serialize(value);

        private static void Reset<T>(List<T> list, string json)
        {
            list.Clear();
            list.AddRange(JsonSerializer.Deserialize<List<T>>(json)!);
        }
    }

    private sealed class SwitchableArchive : ISecurityEventArchive
    {
        private readonly InMemorySecurityEventArchive inner = new();

        public bool Available { get; set; } = true;

        public bool WritesAvailable { get; set; } = true;

        public string ArchiveName => "Switchable(InMemory)";

        public Task<ArchiveReceipt> AppendAsync(Guid organizationId, string streamName, long streamRevision, ReadOnlyMemory<byte> eventBody, byte[] expectedDigest, CancellationToken cancellationToken) =>
            Available && WritesAvailable ? inner.AppendAsync(organizationId, streamName, streamRevision, eventBody, expectedDigest, cancellationToken) : throw new ArchiveUnavailableException("archive down (test)");

        public Task<(bool Ready, string Detail)> ProbeAsync(CancellationToken cancellationToken) => Task.FromResult((Available, "test"));

        public Task<byte[]?> ReadAsync(Guid organizationId, string streamName, long streamRevision, CancellationToken cancellationToken) =>
            Available ? inner.ReadAsync(organizationId, streamName, streamRevision, cancellationToken) : throw new ArchiveUnavailableException("archive down (test)");
    }

    private sealed class RecordingAlerts : ISecurityAlertSink
    {
        public List<SecurityAlert> Raised { get; } = [];

        public void Raise(SecurityAlert alert) => Raised.Add(alert);
    }

    private sealed class NoFaults : IFaultInjector
    {
        public void CrashIfArmed(string faultName)
        {
        }
    }

    private sealed class ManualClock(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset now = start;

        public override DateTimeOffset GetUtcNow() => now;

        public void Advance(TimeSpan by) => now += by;
    }
}
