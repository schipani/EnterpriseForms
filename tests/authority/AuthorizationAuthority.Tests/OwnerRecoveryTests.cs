using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AuthorizationAuthority.Application.Common;
using AuthorizationAuthority.Application.Common.Ports;
using AuthorizationAuthority.Application.Features.Membership;
using AuthorizationAuthority.Application.Features.OperatorPlane;
using AuthorizationAuthority.Application.Features.OrganizationOnboarding;
using AuthorizationAuthority.Application.Features.PlatformOperators;
using AuthorizationAuthority.Application.Features.SecurityCommands;
using AuthorizationAuthority.Contracts.V1;
using AuthorizationAuthority.Domain.Features.Journal;
using AuthorizationAuthority.Domain.Features.Organizations;
using AuthorizationAuthority.Domain.Features.PlatformOperators;
using AuthorizationAuthority.Domain.Features.SecurityCommands;
using AuthorizationAuthority.Domain.Features.Sessions;
using AuthorizationAuthority.Infrastructure.Archive;
using Xunit;

namespace AuthorizationAuthority.Tests;

/// <summary>
/// C8 owner recovery (C-105) through the real service over the in-memory store and archive: eligibility (one owner, zero eligible
/// owners, two eligible owners refused, declared-unavailable evidence), three distinct operators (same operator twice, conflict of
/// interest, suspended approver, replayed or stale evidence), the trusted-clock waiting period, objections (block, unauthorized,
/// cross-organization), invalidation (owner set changed, declared owner active, identity conflict), the add-only execution
/// (owner roles and new bindings only; nothing removed; no team, record or share edge), idempotent retries, archive outage and
/// replay, and the recovered-owner safeguard on top of OB-F8. The Docker path is OwnerRecoveryStackTests (Stage B).
/// </summary>
public sealed class OwnerRecoveryTests : IDisposable
{
    private const string OwnerIssuer = "https://idp.test.invalid";
    private const string OperatorIssuer = "https://platform-operators.test.invalid/";
    private const string Justification = "Sole owner left; replacement verified by support callback (INC-7)";
    private const string Synthetic = PlatformOperatorEvidenceKinds.SyntheticDevelopmentJws;
    private static readonly TimeSpan Waiting = TimeSpan.FromSeconds(60);
    private static readonly FieldSearchApprovalGate StepUp = FieldSearchApprovalGate.Create(FieldSearchApprovalGate.SyntheticMode, "Testing", "TestIdentityProvider");

    private readonly Guid org = Guid.CreateVersion7();
    private readonly Guid otherOrg = Guid.CreateVersion7();
    private readonly Guid soleOwner = Guid.CreateVersion7();
    private readonly Guid member = Guid.CreateVersion7();
    private readonly Guid administrator = Guid.CreateVersion7();
    private readonly Guid otherOwner = Guid.CreateVersion7();
    private readonly Guid team = Guid.CreateVersion7();
    private readonly Guid teamRecord = Guid.CreateVersion7();
    private readonly Guid ownedRecord = Guid.CreateVersion7();
    private readonly InMemoryAuthorityStore store = new();
    private readonly ManualClock clock = new(new DateTimeOffset(2026, 11, 1, 9, 0, 0, TimeSpan.Zero));
    private readonly SwitchableArchive archive = new();
    private readonly RecordingAlerts alerts = new();
    private readonly Op alice;
    private readonly Op blake;
    private readonly Op casey;
    private readonly Op dana;

    public OwnerRecoveryTests()
    {
        store.Organizations.Add(new OrganizationAuthorizationState { OrganizationId = org, OrganizationDisplayName = "Riverside Clinic", AuthorizationRevision = 5, ProjectionAppliedRevision = 5 });
        store.Organizations.Add(new OrganizationAuthorizationState { OrganizationId = otherOrg, OrganizationDisplayName = "Harbour Legal", AuthorizationRevision = 2, ProjectionAppliedRevision = 2 });
        Bind(org, soleOwner);
        Bind(org, member);
        Bind(org, administrator);
        Bind(otherOrg, otherOwner);
        store.Edges.Add(Edge(org, ResourceTypeNames.Organization, org, OrganizationRoleNames.OrganizationOwner, ResourceTypeNames.User, soleOwner));
        store.Edges.Add(Edge(org, ResourceTypeNames.Organization, org, OrganizationRoleNames.SystemAdministrator, ResourceTypeNames.User, administrator));
        store.Edges.Add(Edge(otherOrg, ResourceTypeNames.Organization, otherOrg, OrganizationRoleNames.OrganizationOwner, ResourceTypeNames.User, otherOwner));
        store.Resources.Add(new AuthorityResource { AuthorityResourceId = Guid.CreateVersion7(), OrganizationId = org, ResourceTypeName = ResourceTypeNames.Team, ResourceEntityId = team, ResourceLifecycleState = ResourceLifecycleStates.Active });
        store.Edges.Add(Edge(org, ResourceTypeNames.FormRecord, teamRecord, "owner", ResourceTypeNames.Team, team, "participant"));
        store.Edges.Add(Edge(org, ResourceTypeNames.FormRecord, ownedRecord, "owner", ResourceTypeNames.User, soleOwner));
        alice = Seed("Alice Operator");
        blake = Seed("Blake Operator");
        casey = Seed("Casey Operator");
        dana = Seed("Dana Operator");
    }

    public void Dispose()
    {
        foreach (var key in new[] { alice.Key, blake.Key, casey.Key, dana.Key })
        {
            key.Dispose();
        }
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static PlatformOperatorPolicy Platform => PlatformOperatorPolicy.Create(new PlatformOperatorOptions { Mode = PlatformOperatorPolicy.SyntheticMode }, "Testing");

    private static OrganizationOnboardingPolicy Policy(string environment = "Testing", int waitingSeconds = 60) =>
        OrganizationOnboardingPolicy.Create(new OrganizationOnboardingOptions { ApprovedIdentityIssuers = environment is "Testing" ? [OwnerIssuer] : [], RecoveryWaitingPeriodSeconds = waitingSeconds }, environment);

    private PlatformJournal Journal => new(store, archive, alerts, clock);

    private OwnerRecoveryService Service => ServiceWith(Policy());

    private OwnerRecoveryService ServiceWith(OrganizationOnboardingPolicy policy) => new(store, Platform, policy, new PlatformOperatorEvidenceVerifier(Platform), Journal, StepUp, alerts, clock);

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

    private IdentityBinding Bind(Guid organizationId, Guid user, string? subject = null, string issuer = OwnerIssuer)
    {
        var binding = new IdentityBinding { IdentityBindingId = Guid.CreateVersion7(), OrganizationId = organizationId, AuthorityUserId = user, IdentityIssuerUri = issuer, IdentitySubjectIdentifier = subject ?? "user-" + user.ToString("N"), IdentityBindingEnabled = true };
        store.Bindings.Add(binding);
        return binding;
    }

    private static AuthorityRelationshipEdge Edge(Guid organizationId, string type, Guid id, string relation, string subjectType, Guid subject, string subjectRelation = "") => new()
    {
        AuthorityRelationshipEdgeId = Guid.CreateVersion7(), OrganizationId = organizationId, ResourceTypeName = type, ResourceEntityId = id, RelationshipName = relation,
        SubjectTypeName = subjectType, SubjectEntityId = subject, SubjectRelationName = subjectRelation,
    };

    private VerifiedActor UserActor(Guid user) => store.Bindings.Single(b => b.AuthorityUserId == user) is var b ? new VerifiedActor(b.IdentityIssuerUri, b.IdentitySubjectIdentifier, "business-api") : throw new InvalidOperationException();

    private string Evidence(Op op, string decision, Guid caseId, Guid organizationId, string statementSha256, string justification = Justification, string operationKind = PlatformOperationKinds.OrganizationOwnerRecovery)
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
            [PlatformOperatorEvidenceClaims.OperationKind] = operationKind,
            [PlatformOperatorEvidenceClaims.OperationId] = caseId.ToString("D"),
            [PlatformOperatorEvidenceClaims.OrganizationId] = organizationId.ToString("D"),
            [PlatformOperatorEvidenceClaims.StatementDigest] = statementSha256,
            [PlatformOperatorEvidenceClaims.JustificationDigest] = PlatformOperatorEvidenceVerifier.JustificationSha256(justification),
        };
        var header = Base64Url.EncodeToString(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { alg = "ES256", typ = "JWT", kid = op.KeyId })));
        var body = Base64Url.EncodeToString(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(claims)));
        return header + "." + body + "." + Base64Url.EncodeToString(op.Key.SignData(Encoding.ASCII.GetBytes(header + "." + body), HashAlgorithmName.SHA256));
    }

    private static OwnerRecoveryReplacementInput NewOwner(string subject, string issuer = OwnerIssuer) =>
        new(null, issuer, subject, "Owner " + subject, OnboardingVerificationMethods.SupportCallback, "CASE-" + subject);

    private static OwnerRecoveryReplacementInput ExistingOwner(Guid user) => new(user, null, null, null, OnboardingVerificationMethods.SupportCallback, "CASE-existing");

    private static OwnerRecoveryDeclaredInput Declared(Guid user, string? evidence = "HR-LEAVER-0042", string? summary = "HR confirms the owner left the organization on 2026-10-30") =>
        new(user, OwnerUnavailabilityReasons.LeftOrganization, evidence, summary);

    private Task<OwnerRecoveryCreateResponse> OpenAsync(Op op, Guid? organizationId = null, IReadOnlyList<OwnerRecoveryReplacementInput>? replacements = null, IReadOnlyList<OwnerRecoveryDeclaredInput>? declared = null,
        string? key = null, bool dryRun = false, string incident = "INC-2026-0007") =>
        Service.CreateAsync(op.Actor, new OwnerRecoveryCreateRequest(key ?? "recovery-" + Guid.NewGuid().ToString("N"), organizationId ?? org, incident, Justification, declared ?? [],
            replacements ?? [NewOwner("recovered-one"), NewOwner("recovered-two")], dryRun), Guid.CreateVersion7(), Ct);

    private async Task<OwnerRecoveryDecisionResponse> DecideAsync(Op op, OwnerRecoveryCaseDetail recoveryCase, string decision, string? expected = null, string? evidence = null)
    {
        var sha = recoveryCase.StatementSha256;
        var input = new OwnerRecoveryDecisionRequest(expected ?? sha, Justification, Synthetic, evidence ?? Evidence(op, decision, recoveryCase.CaseId, recoveryCase.OrganizationId, sha));
        var service = Service;
        return decision switch
        {
            PlatformDecisions.Approve => await service.ApproveAsync(op.Actor, recoveryCase.CaseId, input, Guid.CreateVersion7(), Ct),
            PlatformDecisions.Withdraw => await service.WithdrawAsync(op.Actor, recoveryCase.CaseId, input, Guid.CreateVersion7(), Ct),
            PlatformDecisions.Cancel => await service.CancelAsync(op.Actor, recoveryCase.CaseId, input, Guid.CreateVersion7(), Ct),
            _ => await service.ExecuteAsync(op.Actor, recoveryCase.CaseId, input, Guid.CreateVersion7(), Ct),
        };
    }

    private async Task<OwnerRecoveryCaseDetail> ApprovedAsync(IReadOnlyList<OwnerRecoveryReplacementInput>? replacements = null, IReadOnlyList<OwnerRecoveryDeclaredInput>? declared = null)
    {
        var recoveryCase = (await OpenAsync(alice, replacements: replacements, declared: declared)).Case!;
        await DecideAsync(alice, recoveryCase, PlatformDecisions.Approve);
        await DecideAsync(blake, recoveryCase, PlatformDecisions.Approve);
        Assert.Equal("ApprovalsComplete", (await DecideAsync(casey, recoveryCase, PlatformDecisions.Approve)).Outcome);
        return recoveryCase;
    }

    private Task<OwnerRecoveryObjectionResponse> ObjectAsync(Guid user, Guid caseId, Guid? organizationId = null, string? stepUp = FieldSearchApprovalGate.SyntheticStepUpEvidence) =>
        Service.ObjectAsync(UserActor(user), new OwnerRecoveryObjectionRequest(organizationId ?? org, caseId, "I am still here and did not ask for this", stepUp), Guid.CreateVersion7(), Ct);

    private OrganizationOwnerRecoveryCase Row(OwnerRecoveryCaseDetail recoveryCase) => store.RecoveryCases.Single(c => c.OrganizationOwnerRecoveryCaseId == recoveryCase.CaseId);

    private List<Guid> Owners(Guid organizationId) => store.Edges.Where(e => e.OrganizationId == organizationId && e.RemovedBySecurityCommandId is null && e.ResourceTypeName == ResourceTypeNames.Organization
        && e.RelationshipName == OrganizationRoleNames.OrganizationOwner).Select(e => e.SubjectEntityId).ToList();

    private int Journaled(string kind) => store.PlatformJournal.Count(j => j.JournalEventKind == kind);

    // ---- eligibility ----

    [Fact]
    public async Task OpeningACaseForASingleOwnerOrganizationFreezesAStatementAndGrantsNothing()
    {
        var edges = store.Edges.Count;
        var created = await OpenAsync(alice);
        var recoveryCase = created.Case!;
        Assert.Equal(OwnerRecoveryStates.Open, recoveryCase.State);
        Assert.Equal(OwnerRecoveryDisplayStates.AwaitingApprovals, recoveryCase.DisplayState);
        Assert.Equal(1, recoveryCase.EligibleOwnersBefore);
        Assert.Equal(3, recoveryCase.EligibleOwnersAfter);
        Assert.Equal(clock.GetUtcNow() + Waiting, recoveryCase.WaitingPeriodEndsOn);
        Assert.Equal(edges, store.Edges.Count); // nothing granted
        Assert.Equal(5, store.Organizations.Single(o => o.OrganizationId == org).AuthorizationRevision);
        Assert.Equal(1, Journaled("OwnerRecoveryOpened"));
        Assert.Single(store.Journal, j => j.OrganizationId == org && j.JournalEventKind == "OwnerRecoveryOpened"); // the organization's notice
        Assert.Contains(alerts.Raised, a => a.AlertKind == "OwnerRecoveryOpened");
        Assert.Contains("INC-2026-0007", recoveryCase.StatementJson, StringComparison.Ordinal);
        Assert.Equal(PlatformOperatorPolicy.SyntheticAssurance, recoveryCase.ApprovalAssurance);

        // Two eligible owners after recovery is allowed but warned about.
        store.RecoveryCases.Clear();
        var two = await OpenAsync(blake, replacements: [NewOwner("only-replacement")]);
        Assert.Contains(two.Warnings, w => w.Contains("three are recommended", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RecoveryAppliesOnlyBelowTwoEligibleOwners()
    {
        var second = Guid.CreateVersion7();
        Bind(org, second);
        store.Edges.Add(Edge(org, ResourceTypeNames.Organization, org, OrganizationRoleNames.OrganizationOwner, ResourceTypeNames.User, second));
        Assert.Equal(AuthorityErrorCodes.OwnerRecoveryNotApplicable, await RefusedAsync(() => OpenAsync(alice)));

        // A disabled owner is ineligible by state; no evidence is needed for it.
        store.Bindings.Single(b => b.AuthorityUserId == second).IdentityBindingEnabled = false;
        Assert.True((await OpenAsync(alice, dryRun: true)).Eligible);

        // Zero eligible owners: the sole enabled owner is declared unavailable with evidence.
        store.Bindings.Single(b => b.AuthorityUserId == second).IdentityBindingEnabled = true;
        var declared = await OpenAsync(alice, declared: [Declared(soleOwner)]);
        Assert.Equal(1, declared.Case!.EligibleOwnersBefore);
        store.RecoveryCases.Clear();
        store.RecoverySubjects.Clear();
        var zero = await OpenAsync(alice, declared: [Declared(soleOwner), Declared(second)]);
        Assert.Equal(0, zero.Case!.EligibleOwnersBefore);
        Assert.Equal(2, zero.Case.EligibleOwnersAfter);
    }

    [Fact]
    public async Task ADeclaredUnavailableOwnerNeedsDocumentedEvidence()
    {
        var second = Guid.CreateVersion7();
        Bind(org, second);
        store.Edges.Add(Edge(org, ResourceTypeNames.Organization, org, OrganizationRoleNames.OrganizationOwner, ResourceTypeNames.User, second));
        Assert.Equal(AuthorityErrorCodes.OwnerRecoveryEvidenceRequired, await RefusedAsync(() => OpenAsync(alice, declared: [Declared(second, evidence: null)])));
        Assert.Equal(AuthorityErrorCodes.OwnerRecoveryEvidenceRequired, await RefusedAsync(() => OpenAsync(alice, declared: [Declared(second, summary: "gone")])));
        Assert.Equal(AuthorityErrorCodes.OwnerRecoveryEvidenceRequired, await RefusedAsync(() => OpenAsync(alice, declared: [new OwnerRecoveryDeclaredInput(second, "Because I say so", "HR-1", "HR confirms the owner left")])));
        Assert.Equal(AuthorityErrorCodes.InvalidCommand, await RefusedAsync(() => OpenAsync(alice, declared: [Declared(member)]))); // not an owner
        Assert.Empty(store.RecoveryCases);
    }

    [Fact]
    public async Task NonexistentOrganizationsAndInvalidReplacementsAreRefused()
    {
        Assert.Equal(AuthorityErrorCodes.NotFound, await RefusedAsync(() => OpenAsync(alice, organizationId: Guid.CreateVersion7())));
        Assert.Equal(AuthorityErrorCodes.InvalidCommand, await RefusedAsync(() => OpenAsync(alice, incident: "")));
        Assert.Equal(AuthorityErrorCodes.InvalidCommand, await RefusedAsync(() => OpenAsync(alice, replacements: [])));
        Assert.Equal(AuthorityErrorCodes.OrganizationOwnerIdentityInvalid, await RefusedAsync(() => OpenAsync(alice, replacements: [NewOwner("someone@example.com")])));
        Assert.Equal(AuthorityErrorCodes.OrganizationOwnerIssuerMismatch, await RefusedAsync(() => OpenAsync(alice, replacements: [NewOwner("x-1", "https://unapproved.test.invalid")])));
        var memberSubject = store.Bindings.Single(b => b.AuthorityUserId == member).IdentitySubjectIdentifier;
        Assert.Equal(AuthorityErrorCodes.OwnerRecoveryIdentityConflict, await RefusedAsync(() => OpenAsync(alice, replacements: [NewOwner(memberSubject)]))); // bound already: name the member instead
        Assert.Equal(AuthorityErrorCodes.OwnerRecoveryIdentityConflict, await RefusedAsync(() => OpenAsync(alice, replacements: [ExistingOwner(soleOwner)]))); // already an owner
        Assert.True((await OpenAsync(alice, replacements: [ExistingOwner(member)], dryRun: true)).Eligible);
        Assert.Empty(store.RecoveryCases);
    }

    [Fact]
    public async Task OperatorsWhoBelongToTheOrganizationOrWouldBecomeOwnersAreExcluded()
    {
        var insider = Seed("Insider Operator", OwnerIssuer, store.Bindings.Single(b => b.AuthorityUserId == member).IdentitySubjectIdentifier);
        Assert.Equal(AuthorityErrorCodes.OperatorConflictOfInterest, await RefusedAsync(() => OpenAsync(insider)));
        Assert.Equal(AuthorityErrorCodes.OrganizationOwnerIsOperator, await RefusedAsync(() => OpenAsync(alice, replacements: [NewOwner("op-subject"), new OwnerRecoveryReplacementInput(null, OwnerIssuer, Seed("Pat", OwnerIssuer, "pat-op").Subject, "Pat", OnboardingVerificationMethods.SupportCallback, "CASE-pat")])));

        var recoveryCase = (await OpenAsync(alice)).Case!;
        Assert.Equal(AuthorityErrorCodes.OperatorConflictOfInterest, await RefusedAsync(() => DecideAsync(insider, recoveryCase, PlatformDecisions.Approve)));
        Assert.Equal(AuthorityErrorCodes.OperatorConflictOfInterest, await RefusedAsync(() => DecideAsync(insider, recoveryCase, PlatformDecisions.Execute)));
    }

    // ---- approvals ----

    [Fact]
    public async Task ThreeDistinctOperatorsMustApproveWithTheirOwnEvidence()
    {
        var recoveryCase = (await OpenAsync(alice)).Case!;
        Assert.Equal("ApprovalRecorded", (await DecideAsync(alice, recoveryCase, PlatformDecisions.Approve)).Outcome); // the opener may be one of the three
        Assert.Equal(AuthorityErrorCodes.OperatorAlreadyApproved, await RefusedAsync(() => DecideAsync(alice, recoveryCase, PlatformDecisions.Approve)));
        // Another operator's evidence, the onboarding operation kind, and a stale fingerprint are refused.
        Assert.NotEqual("ApprovalRecorded", (await Assert.ThrowsAsync<AuthorityException>(() => DecideAsync(blake, recoveryCase, PlatformDecisions.Approve, evidence: Evidence(casey, PlatformDecisions.Approve, recoveryCase.CaseId, org, recoveryCase.StatementSha256)))).ErrorCode);
        await Assert.ThrowsAsync<AuthorityException>(() => DecideAsync(blake, recoveryCase, PlatformDecisions.Approve, evidence: Evidence(blake, PlatformDecisions.Approve, recoveryCase.CaseId, org, recoveryCase.StatementSha256, operationKind: PlatformOperationKinds.OrganizationOnboarding)));
        Assert.Equal(AuthorityErrorCodes.ApprovalStatementChanged, await RefusedAsync(() => DecideAsync(blake, recoveryCase, PlatformDecisions.Approve, expected: new string('0', 64))));
        var evidence = Evidence(blake, PlatformDecisions.Approve, recoveryCase.CaseId, org, recoveryCase.StatementSha256);
        await DecideAsync(blake, recoveryCase, PlatformDecisions.Approve, evidence: evidence);
        Assert.Equal(AuthorityErrorCodes.OperatorAlreadyApproved, await RefusedAsync(() => DecideAsync(blake, recoveryCase, PlatformDecisions.Approve, evidence: evidence)));
        Assert.Equal(AuthorityErrorCodes.OwnerRecoveryApprovalsIncomplete, await RefusedAsync(() => DecideAsync(casey, recoveryCase, PlatformDecisions.Execute)));
        Assert.Equal("ApprovalsComplete", (await DecideAsync(casey, recoveryCase, PlatformDecisions.Approve)).Outcome);
        Assert.Equal(3, store.RecoveryApprovals.Select(a => a.PlatformOperatorId).Distinct().Count());
        Assert.Equal(AuthorityErrorCodes.OperatorEvidenceInvalid, await RefusedAsync(() => DecideAsync(blake, recoveryCase, PlatformDecisions.Withdraw, evidence: evidence))); // bound to "approve"
        Assert.Equal("ApprovalWithdrawn", (await DecideAsync(blake, recoveryCase, PlatformDecisions.Withdraw)).Outcome);
        Assert.Equal(AuthorityErrorCodes.OperatorEvidenceReplayed, await RefusedAsync(() => DecideAsync(blake, recoveryCase, PlatformDecisions.Approve, evidence: evidence))); // single use
    }

    [Fact]
    public async Task ASuspendedOrRemovedApproverNoLongerCounts()
    {
        var recoveryCase = await ApprovedAsync();
        clock.Advance(Waiting + TimeSpan.FromSeconds(1));
        store.PlatformOperators.Single(o => o.PlatformOperatorId == blake.Id).OperatorStatusName = PlatformOperatorStates.Suspended;
        Assert.Equal(AuthorityErrorCodes.OwnerRecoveryApprovalsIncomplete, await RefusedAsync(() => DecideAsync(dana, recoveryCase, PlatformDecisions.Execute)));
        Assert.Single(Owners(org));
        await DecideAsync(dana, recoveryCase, PlatformDecisions.Approve); // the stale approval is invalidated; a fourth operator replaces it
        Assert.Equal(OnboardingApprovalStates.Invalidated, store.RecoveryApprovals.Single(a => a.PlatformOperatorId == blake.Id).ApprovalStateName);
        Assert.Equal("Recovered", (await DecideAsync(dana, recoveryCase, PlatformDecisions.Execute)).Outcome);
    }

    // ---- waiting period and execution ----

    [Fact]
    public async Task ExecutionWaitsForTheTrustedClockAndOnlyAddsOwners()
    {
        var recoveryCase = await ApprovedAsync([NewOwner("recovered-one"), ExistingOwner(member)]);
        Assert.Equal(AuthorityErrorCodes.OwnerRecoveryWaitingPeriod, await RefusedAsync(() => DecideAsync(dana, recoveryCase, PlatformDecisions.Execute)));
        Assert.Equal(AuthorityErrorCodes.OwnerRecoveryWaitingPeriod, Row(recoveryCase).LastExecutionFailureCode);
        var before = store.Edges.Where(e => e.RemovedBySecurityCommandId is null).Select(e => e.AuthorityRelationshipEdgeId).ToHashSet();
        var bindingsBefore = store.Bindings.Count(b => b.OrganizationId == org);

        clock.Advance(Waiting);
        var executed = await DecideAsync(dana, recoveryCase, PlatformDecisions.Execute);
        Assert.Equal("Recovered", executed.Outcome);
        Assert.NotNull(executed.SecurityCommandId);
        var row = Row(recoveryCase);
        Assert.Equal(OwnerRecoveryStates.Recovered, row.RecoveryStateName);

        // Add-only: every previous edge survives; the only new edges are owner roles for the replacements.
        var active = store.Edges.Where(e => e.RemovedBySecurityCommandId is null).ToList();
        Assert.True(before.IsSubsetOf(active.Select(e => e.AuthorityRelationshipEdgeId)));
        var added = active.Where(e => !before.Contains(e.AuthorityRelationshipEdgeId)).ToList();
        Assert.Equal(2, added.Count);
        Assert.All(added, e => Assert.Equal(OrganizationRoleNames.OrganizationOwner, e.RelationshipName));
        Assert.Contains(member, Owners(org));
        Assert.Contains(soleOwner, Owners(org)); // the existing owner is never removed
        Assert.True(store.Bindings.Single(b => b.AuthorityUserId == soleOwner).IdentityBindingEnabled);
        Assert.Equal(6, store.Organizations.Single(o => o.OrganizationId == org).AuthorizationRevision);
        Assert.Equal(bindingsBefore + 1, store.Bindings.Count(b => b.OrganizationId == org));
        var created = store.Bindings.Single(b => b.IdentitySubjectIdentifier == "recovered-one");
        Assert.Equal(recoveryCase.CaseId, created.OrganizationOwnerRecoveryCaseId);
        Assert.Equal(2, store.RecoveryProbations.Count);
        Assert.Equal(3, store.RecoveryApprovals.Count(a => a.ApprovalStateName == OnboardingApprovalStates.Consumed));
        Assert.Single(store.Journal, j => j.OrganizationId == org && j.JournalEventKind == "OwnerRecoveryExecuted");
        Assert.Contains(alerts.Raised, a => a.AlertKind == "OwnerRecoveryExecuted");

        // A retried execution replays the same result and changes nothing.
        var edgeCount = store.Edges.Count;
        var replay = await DecideAsync(casey, recoveryCase, PlatformDecisions.Execute);
        Assert.True(replay.Replayed);
        Assert.Equal(executed.SecurityCommandId, replay.SecurityCommandId);
        Assert.Equal(edgeCount, store.Edges.Count);
        Assert.Single(store.Commands, c => c.CommandKindName == OwnerRecoveryService.RecoverCommandKind);
    }

    [Fact]
    public async Task RecoveryNeverReachesRecordsTeamsOrShares()
    {
        var recoveryCase = await ApprovedAsync();
        clock.Advance(Waiting);
        await DecideAsync(dana, recoveryCase, PlatformDecisions.Execute);
        var graph = new DesiredStateGraph(store.Edges.Where(e => e.RemovedBySecurityCommandId is null).ToList());
        foreach (var recovered in store.RecoveryProbations.Select(p => p.AuthorityUserId))
        {
            Assert.False(graph.IsRecordOwner(recovered, teamRecord));
            Assert.False(graph.IsRecordOwner(recovered, ownedRecord));
            Assert.DoesNotContain(store.Edges, e => e.SubjectEntityId == recovered && e.ResourceTypeName != ResourceTypeNames.Organization);
            Assert.False(graph.HasOrganizationRole(org, recovered, OrganizationRoleNames.SystemAdministrator));
            Assert.False(graph.HasOrganizationRole(org, recovered, OrganizationRoleNames.Designer));
        }

        Assert.True(graph.IsRecordOwner(soleOwner, ownedRecord)); // record ownership unchanged
    }

    // ---- objection ----

    [Fact]
    public async Task AnObjectionBlocksExecutionImmediatelyAndCannotBeDisregarded()
    {
        var recoveryCase = await ApprovedAsync();
        var notice = await Service.GetNoticeAsync(UserActor(soleOwner), new OwnerRecoveryNoticeRequest(org), Guid.CreateVersion7(), Ct);
        Assert.Equal(recoveryCase.CaseId, notice.Case!.CaseId);
        Assert.True(notice.CanObject);
        Assert.Equal(AuthorityErrorCodes.StepUpRequired, await RefusedAsync(() => ObjectAsync(soleOwner, recoveryCase.CaseId, stepUp: null)));

        var objected = await ObjectAsync(administrator, recoveryCase.CaseId); // a Security Administrator may object too
        Assert.Equal(OwnerRecoveryStates.Objected, objected.State);
        Assert.Equal(1, Journaled("OwnerRecoveryObjected"));
        Assert.Single(store.Journal, j => j.OrganizationId == org && j.JournalEventKind == "OwnerRecoveryObjected");
        Assert.All(store.RecoveryApprovals, a => Assert.Equal(OnboardingApprovalStates.Invalidated, a.ApprovalStateName));

        clock.Advance(Waiting);
        Assert.Equal(AuthorityErrorCodes.OwnerRecoveryObjected, await RefusedAsync(() => DecideAsync(dana, recoveryCase, PlatformDecisions.Execute)));
        Assert.Equal(AuthorityErrorCodes.OwnerRecoveryObjected, await RefusedAsync(() => DecideAsync(dana, recoveryCase, PlatformDecisions.Approve)));
        Assert.Equal(AuthorityErrorCodes.OwnerRecoveryObjected, await RefusedAsync(() => DecideAsync(dana, recoveryCase, PlatformDecisions.Cancel)));
        Assert.Single(Owners(org));
        var detail = await Service.GetDetailAsync(alice.Actor, recoveryCase.CaseId, Guid.CreateVersion7(), Ct);
        Assert.Equal(OwnerRecoveryDisplayStates.BlockedByObjection, detail.DisplayState);
        Assert.NotNull(detail.Objection);
        // A new case is the only way forward (separately authorized resolution).
        Assert.NotNull((await OpenAsync(blake)).Case);
    }

    [Fact]
    public async Task OnlyEnabledOwnersAndSecurityAdministratorsOfTheOrganizationCanSeeOrObject()
    {
        var recoveryCase = (await OpenAsync(alice)).Case!;
        Assert.Equal(AuthorityErrorCodes.NotFound, await RefusedAsync(() => Service.GetNoticeAsync(UserActor(member), new OwnerRecoveryNoticeRequest(org), Guid.CreateVersion7(), Ct)));
        Assert.Equal(AuthorityErrorCodes.NotFound, await RefusedAsync(() => ObjectAsync(member, recoveryCase.CaseId)));
        Assert.Equal(AuthorityErrorCodes.NotFound, await RefusedAsync(() => ObjectAsync(otherOwner, recoveryCase.CaseId))); // another organization's owner
        Assert.Equal(AuthorityErrorCodes.NotFound, await RefusedAsync(() => ObjectAsync(otherOwner, recoveryCase.CaseId, otherOrg))); // their own organization, not this case
        Assert.Null((await Service.GetNoticeAsync(UserActor(otherOwner), new OwnerRecoveryNoticeRequest(otherOrg), Guid.CreateVersion7(), Ct)).Case);
        store.Bindings.Single(b => b.AuthorityUserId == soleOwner).IdentityBindingEnabled = false;
        Assert.Equal(AuthorityErrorCodes.NotFound, await RefusedAsync(() => ObjectAsync(soleOwner, recoveryCase.CaseId))); // disabled
        Assert.Equal(OwnerRecoveryStates.Open, Row(recoveryCase).RecoveryStateName);
    }

    // ---- invalidation and conflicts ----

    [Fact]
    public async Task AChangedOwnerSetInvalidatesTheCase()
    {
        var recoveryCase = (await OpenAsync(alice)).Case!;
        await DecideAsync(alice, recoveryCase, PlatformDecisions.Approve);
        var newOwner = Guid.CreateVersion7();
        Bind(org, newOwner);
        store.Edges.Add(Edge(org, ResourceTypeNames.Organization, org, OrganizationRoleNames.OrganizationOwner, ResourceTypeNames.User, newOwner));
        Assert.Equal(AuthorityErrorCodes.OwnerRecoveryOwnerSetChanged, await RefusedAsync(() => DecideAsync(blake, recoveryCase, PlatformDecisions.Approve)));
        Assert.Equal(OwnerRecoveryStates.Invalidated, Row(recoveryCase).RecoveryStateName);
        Assert.Equal(OnboardingApprovalStates.Invalidated, store.RecoveryApprovals.Single().ApprovalStateName);
        Assert.Equal(1, Journaled("OwnerRecoveryInvalidated"));
    }

    [Fact]
    public async Task ADeclaredOwnerWhoSignsInInvalidatesTheCase()
    {
        var second = Guid.CreateVersion7();
        var binding = Bind(org, second);
        store.Edges.Add(Edge(org, ResourceTypeNames.Organization, org, OrganizationRoleNames.OrganizationOwner, ResourceTypeNames.User, second));
        var recoveryCase = await ApprovedAsync(declared: [Declared(second)]);
        clock.Advance(Waiting);
        store.Sessions.Add(new AuthenticatedSession { AuthenticatedSessionId = Guid.CreateVersion7(), OrganizationId = org, IdentityBindingId = binding.IdentityBindingId, SessionCreatedOn = clock.GetUtcNow() });
        Assert.Equal(AuthorityErrorCodes.OwnerRecoveryDeclaredOwnerActive, await RefusedAsync(() => DecideAsync(dana, recoveryCase, PlatformDecisions.Execute)));
        Assert.Equal(OwnerRecoveryClosureReasons.DeclaredOwnerActive, Row(recoveryCase).CaseClosureReasonName);
        Assert.Equal(2, Owners(org).Count);
    }

    [Fact]
    public async Task AnIdentityBoundAfterOpeningInvalidatesTheCase()
    {
        var recoveryCase = await ApprovedAsync();
        Bind(org, Guid.CreateVersion7(), "recovered-two"); // e.g. a membership request approved meanwhile
        clock.Advance(Waiting);
        Assert.Equal(AuthorityErrorCodes.OwnerRecoveryIdentityConflict, await RefusedAsync(() => DecideAsync(dana, recoveryCase, PlatformDecisions.Execute)));
        Assert.Equal(OwnerRecoveryStates.Invalidated, Row(recoveryCase).RecoveryStateName);
        Assert.Single(Owners(org));
    }

    [Fact]
    public async Task OneOpenCaseAtATimeAndIdempotentOpening()
    {
        var created = await OpenAsync(alice, key: "recovery-key-0001");
        Assert.True((await OpenAsync(alice, key: "recovery-key-0001")).Replayed);
        Assert.Equal(AuthorityErrorCodes.IdempotencyConflict, await RefusedAsync(() => OpenAsync(alice, key: "recovery-key-0001", incident: "INC-OTHER")));
        Assert.Equal(AuthorityErrorCodes.OwnerRecoveryCaseExists, await RefusedAsync(() => OpenAsync(blake)));
        await DecideAsync(blake, created.Case!, PlatformDecisions.Cancel);
        Assert.NotNull((await OpenAsync(blake)).Case);
        Assert.Single(store.RecoveryCases, c => c.RecoveryStateName == OwnerRecoveryStates.Open);
    }

    [Fact]
    public async Task AnExpiredCaseCannotBeExecuted()
    {
        var recoveryCase = await ApprovedAsync();
        clock.Advance(Waiting + TimeSpan.FromDays(8));
        Assert.Equal(AuthorityErrorCodes.OwnerRecoveryExpired, await RefusedAsync(() => DecideAsync(dana, recoveryCase, PlatformDecisions.Execute)));
        Assert.Equal(OwnerRecoveryDisplayStates.Expired, (await Service.GetDetailAsync(alice.Actor, recoveryCase.CaseId, Guid.CreateVersion7(), Ct)).DisplayState);
        Assert.NotNull((await OpenAsync(blake)).Case); // the expired case is closed when a new one is opened
        Assert.Equal(OwnerRecoveryStates.Expired, Row(recoveryCase).RecoveryStateName);
    }

    // ---- journal ----

    [Fact]
    public async Task AnArchiveOutageFailsClosedAndAReplayRestoresTheCaseAfterARestore()
    {
        archive.Available = false;
        Assert.Equal(AuthorityErrorCodes.PlatformJournalUnverifiable, await RefusedAsync(() => OpenAsync(alice)));
        Assert.Empty(store.RecoveryCases);
        archive.Available = true;

        var recoveryCase = (await OpenAsync(alice)).Case!;
        await DecideAsync(alice, recoveryCase, PlatformDecisions.Approve);
        var snapshot = Snapshot();
        await DecideAsync(blake, recoveryCase, PlatformDecisions.Approve);
        Restore(snapshot); // AuthorityDb restored to before the second approval; the archive keeps the accepted event

        Assert.Equal(AuthorityErrorCodes.PlatformJournalBehindArchive, await RefusedAsync(() => Service.GetDetailAsync(casey.Actor, recoveryCase.CaseId, Guid.CreateVersion7(), Ct)));
        var replay = await Journal.ReplayAsync("test-operator", Guid.CreateVersion7(), Ct);
        Assert.True(replay.GateOpen);
        Assert.Equal(2, store.RecoveryApprovals.Count(a => a.ApprovalStateName == OnboardingApprovalStates.Valid));
        Assert.Equal(AuthorityErrorCodes.OperatorAlreadyApproved, await RefusedAsync(() => DecideAsync(blake, recoveryCase, PlatformDecisions.Approve)));
    }

    // ---- recovered-owner safeguard (on top of OB-F8) ----

    [Fact]
    public async Task RecoveredOwnersCannotGrantEachOtherTeamsOrDesignerDuringTheSafeguard()
    {
        var recoveryCase = await ApprovedAsync();
        clock.Advance(Waiting);
        await DecideAsync(dana, recoveryCase, PlatformDecisions.Execute);
        var recovered = store.RecoveryProbations.Select(p => p.AuthorityUserId).ToList();
        var (first, second) = (recovered[0], recovered[1]);

        Assert.Equal(AuthorityErrorCodes.TeamJoinRequiresAnotherOwner, await RefusedAsync(() => PlanAsync(first, SecurityCommandKind.AddTeamMember, ResourceKind.Team, team, new TeamMemberPayload(team, first)))); // OB-F8: no self-join
        Assert.Equal(AuthorityErrorCodes.OwnerRecoveryProbation, await RefusedAsync(() => PlanAsync(first, SecurityCommandKind.AddTeamMember, ResourceKind.Team, team, new TeamMemberPayload(team, second))));
        Assert.Equal(AuthorityErrorCodes.OwnerRecoveryProbation, await RefusedAsync(() => PlanAsync(first, SecurityCommandKind.GrantOrganizationRole, ResourceKind.Organization, org,
            new OrganizationRolePayload(second, OrganizationRoleNames.Designer, "needs designer access now", null, FieldSearchApprovalGate.SyntheticStepUpEvidence))));
        Assert.Single((await PlanAsync(soleOwner, SecurityCommandKind.AddTeamMember, ResourceKind.Team, team, new TeamMemberPayload(team, first))).AddEdges); // an independent owner can

        clock.Advance(TimeSpan.FromDays(15));
        Assert.Single((await PlanAsync(first, SecurityCommandKind.AddTeamMember, ResourceKind.Team, team, new TeamMemberPayload(team, second))).AddEdges);
        Assert.Equal(AuthorityErrorCodes.TeamJoinRequiresAnotherOwner, await RefusedAsync(() => PlanAsync(first, SecurityCommandKind.AddTeamMember, ResourceKind.Team, team, new TeamMemberPayload(team, first)))); // OB-F8 is permanent
    }

    [Fact]
    public async Task RecoveryIsUnavailableOutsideDevelopmentAndTesting()
    {
        var production = ServiceWith(Policy("Production", OrganizationOnboardingPolicy.ProductionRecoveryWaitingPeriodSeconds));
        Assert.Equal(AuthorityErrorCodes.OwnerRecoveryUnavailable, await RefusedAsync(() => production.CreateAsync(alice.Actor, new OwnerRecoveryCreateRequest("recovery-prod-0001", org, "INC-1", Justification, [], [NewOwner("p-1"), NewOwner("p-2")], false), Guid.CreateVersion7(), Ct)));
        Assert.Throws<InvalidOperationException>(() => Policy("Production", 3600)); // the 72-hour floor outside Development/Testing
        Assert.Throws<InvalidOperationException>(() => Policy("Testing", 5));
    }

    // ---- replacement-owner member picker (C-106) ----

    /// <summary>The Authority records a name hint only when a member joined through an approved membership request.</summary>
    private void Hint(Guid user, string name) => store.MembershipRequests.Add(new OrganizationMembershipRequest
    {
        OrganizationMembershipRequestId = Guid.CreateVersion7(), OrganizationId = org, RequestedAuthorityUserId = user, RequestedDisplayNameHint = name, RequestStateName = MembershipRequestStates.Approved,
        RequestCreatedOn = clock.GetUtcNow(), RequestExpiresOn = clock.GetUtcNow().AddDays(7),
    });

    private Task<OwnerRecoveryMemberPage> MembersAsync(Op op, Guid? organizationId = null, string? query = null, string? cursor = null, int? limit = null, OwnerRecoveryService? service = null) =>
        (service ?? Service).SearchMembersAsync(op.Actor, organizationId ?? org, new OwnerRecoveryMemberSearchRequest(query, cursor, limit), Guid.CreateVersion7(), Ct);

    [Fact]
    public async Task ThePickerListsOnlyEnabledNonOwnerNonOperatorMembersWithMinimumMetadata()
    {
        var disabled = Guid.CreateVersion7();
        Bind(org, disabled).IdentityBindingEnabled = false;
        var operatorLike = Seed("Pat Operator", OwnerIssuer, "pat-as-member");
        Bind(org, Guid.CreateVersion7(), "pat-as-member");
        Hint(member, "Morgan Member");
        var page = await MembersAsync(alice);
        Assert.Equal(new[] { member, administrator }.Order().ToArray(), page.Items.Select(i => i.AuthorityUserId).Order().ToArray()); // not the owner, not the disabled member, not the operator identity
        Assert.Equal(2, page.TotalCount);
        var named = page.Items.Single(i => i.AuthorityUserId == member);
        Assert.Equal("Morgan Member", named.DisplayName); // the Authority's recorded hint
        Assert.Equal(OwnerIssuer, named.IssuerUri);
        Assert.Equal(store.Bindings.Single(b => b.AuthorityUserId == administrator).IdentitySubjectIdentifier, page.Items.Single(i => i.AuthorityUserId == administrator).DisplayName); // no hint: the subject
        Assert.DoesNotContain(page.Items, i => i.AuthorityUserId == soleOwner || i.AuthorityUserId == disabled || i.SubjectIdentifier == operatorLike.Subject);
        Assert.Single(store.Journal, j => j.OrganizationId == org && j.JournalEventKind == "PlatformOwnerRecoveryMembersViewed"); // audited before disclosure
        Assert.DoesNotContain("Morgan", store.Journal.Single(j => j.JournalEventKind == "PlatformOwnerRecoveryMembersViewed").JournalEventBodyJson, StringComparison.Ordinal); // identities are not copied into the audit body
    }

    [Fact]
    public async Task ThePickerSearchesAndPagesOnTheServerWithAnOperatorBoundCursor()
    {
        foreach (var name in new[] { "Avery", "Blair", "Casey", "Devon" })
        {
            var id = Guid.CreateVersion7();
            Bind(org, id);
            Hint(id, name + " Person");
        }

        var first = await MembersAsync(alice, limit: 2);
        Assert.Equal(2, first.Items.Count);
        Assert.Equal(6, first.TotalCount);
        Assert.NotNull(first.NextCursor);
        var second = await MembersAsync(alice, limit: 2, cursor: first.NextCursor);
        var third = await MembersAsync(alice, limit: 2, cursor: second.NextCursor);
        Assert.Null(third.NextCursor);
        var all = first.Items.Concat(second.Items).Concat(third.Items).Select(i => i.AuthorityUserId).ToList();
        Assert.Equal(6, all.Distinct().Count()); // every candidate exactly once
        Assert.Equal(AuthorityErrorCodes.CursorInvalid, await RefusedAsync(() => MembersAsync(blake, limit: 2, cursor: first.NextCursor))); // another operator cannot use it
        Assert.Equal(AuthorityErrorCodes.CursorInvalid, await RefusedAsync(() => MembersAsync(alice, query: "Avery", limit: 2, cursor: first.NextCursor))); // nor under another filter
        var found = await MembersAsync(alice, query: "casey");
        Assert.Equal("Casey Person", Assert.Single(found.Items).DisplayName);
        Assert.Empty((await MembersAsync(alice, query: "nobody-matches-this")).Items);
        Assert.Equal(AuthorityErrorCodes.InvalidCommand, await RefusedAsync(() => MembersAsync(alice, limit: 500)));
    }

    [Fact]
    public async Task ThePickerIsIsolatedPerOrganizationAndRefusesOperatorsOfTheOrganization()
    {
        var elsewhere = otherOrg;
        var stranger = Guid.CreateVersion7();
        Bind(elsewhere, stranger);
        var here = await MembersAsync(alice);
        Assert.DoesNotContain(here.Items, i => i.AuthorityUserId == stranger || i.AuthorityUserId == otherOwner);
        Assert.Equal(AuthorityErrorCodes.NotFound, await RefusedAsync(() => MembersAsync(alice, organizationId: Guid.CreateVersion7())));
        var insider = Seed("Insider Operator", OwnerIssuer, store.Bindings.Single(b => b.AuthorityUserId == member).IdentitySubjectIdentifier);
        Assert.Equal(AuthorityErrorCodes.OperatorConflictOfInterest, await RefusedAsync(() => MembersAsync(insider)));
        var production = ServiceWith(Policy("Production", OrganizationOnboardingPolicy.ProductionRecoveryWaitingPeriodSeconds));
        Assert.Equal(AuthorityErrorCodes.OwnerRecoveryUnavailable, await RefusedAsync(() => MembersAsync(alice, service: production)));
        store.PlatformOperators.Single(o => o.PlatformOperatorId == dana.Id).OperatorStatusName = PlatformOperatorStates.Suspended;
        await Assert.ThrowsAsync<AuthorityException>(() => MembersAsync(dana)); // a suspended operator sees nothing
        archive.Available = false;
        Assert.Equal(AuthorityErrorCodes.PlatformJournalUnverifiable, await RefusedAsync(() => MembersAsync(alice))); // fail closed on an unverifiable journal
    }

    [Fact]
    public async Task ASelectedMemberIsBoundByUserIdIntoTheCaseAndExecution()
    {
        var picked = (await MembersAsync(alice)).Items.Single(i => i.AuthorityUserId == member);
        var recoveryCase = (await OpenAsync(alice, replacements: [ExistingOwner(member), NewOwner("brand-new")])).Case!;
        Assert.Contains(recoveryCase.Subjects, s => s.Role == OwnerRecoverySubjectRoles.Replacement && s.AuthorityUserId == picked.AuthorityUserId && !s.IsNewIdentity);
        await DecideAsync(alice, recoveryCase, PlatformDecisions.Approve);
        await DecideAsync(blake, recoveryCase, PlatformDecisions.Approve);
        await DecideAsync(casey, recoveryCase, PlatformDecisions.Approve);
        clock.Advance(Waiting);
        await DecideAsync(dana, recoveryCase, PlatformDecisions.Execute);
        Assert.Contains(member, Owners(org));
        Assert.DoesNotContain(store.Bindings.Where(b => b.OrganizationId == org), b => b.AuthorityUserId == member && b.OrganizationOwnerRecoveryCaseId is not null); // an existing member keeps its own binding
        // Once an owner, the member is no longer offered, and a second case cannot name them again.
        Assert.DoesNotContain((await MembersAsync(alice)).Items, i => i.AuthorityUserId == member);
    }

    // ---- recovered-owner restriction visibility (C-106) ----

    [Fact]
    public async Task TheRosterShowsTheAuthorityExpiryOfTheRestrictionAndDropsItWhenItEnds()
    {
        var recoveryCase = await ApprovedAsync();
        clock.Advance(Waiting);
        await DecideAsync(dana, recoveryCase, PlatformDecisions.Execute);
        var state = store.Organizations.Single(o => o.OrganizationId == org);
        state.ProjectionAppliedRevision = state.AuthorizationRevision; // the projection catches up (the gate itself is tested elsewhere)
        state.ProjectionZedToken = "zed-token";
        state.CheckpointEnforcementModeName = CheckpointEnforcementModes.Asynchronous;
        var roster = new OrganizationRosterService(store, StepUp, OperatorApprovalVerifier.Unavailable, clock);
        var view = await roster.GetAsync(UserActor(soleOwner), new OrganizationRosterRequest(org), Guid.CreateVersion7(), Ct);
        var recovered = store.RecoveryProbations.ToDictionary(p => p.AuthorityUserId, p => p.ProbationEndsOn);
        Assert.Equal(2, recovered.Count);
        foreach (var m in view.Members)
        {
            Assert.Equal(recovered.TryGetValue(m.AuthorityUserId, out var ends) ? ends : null, m.RecoveryRestrictionsUntil); // exactly the Authority timestamp, others none
        }

        Assert.Null(view.Members.Single(m => m.AuthorityUserId == soleOwner).RecoveryRestrictionsUntil);
        clock.Advance(TimeSpan.FromDays(15));
        var later = await roster.GetAsync(UserActor(soleOwner), new OrganizationRosterRequest(org), Guid.CreateVersion7(), Ct);
        Assert.All(later.Members, m => Assert.Null(m.RecoveryRestrictionsUntil));
    }

    // ---- C-107: unified selection rules and the owner limit ----

    private List<Guid> AddOwners(int count, bool enabled = true)
    {
        var added = new List<Guid>();
        for (var i = 0; i < count; i++)
        {
            var id = Guid.CreateVersion7();
            Bind(org, id).IdentityBindingEnabled = enabled;
            store.Edges.Add(Edge(org, ResourceTypeNames.Organization, org, OrganizationRoleNames.OrganizationOwner, ResourceTypeNames.User, id));
            added.Add(id);
        }

        return added;
    }

    [Fact]
    public async Task RecoveryNeverExceedsTenActiveOwnersAndCountsDeclaredUnavailableOwnersButNotDisabledOnes()
    {
        // Nine active owners, eight of them declared unavailable (they keep their role and stay enabled, so they count): one eligible owner.
        var extra = AddOwners(8);
        var declared = extra.Take(7).Append(soleOwner).Select(id => Declared(id)).ToList();
        var refused = await Assert.ThrowsAsync<AuthorityException>(() => OpenAsync(alice, declared: declared, replacements: [NewOwner("cap-a"), NewOwner("cap-b")]));
        Assert.Equal(AuthorityErrorCodes.OrganizationOwnerLimit, refused.ErrorCode);
        Assert.Contains("9 active owners", refused.Detail, StringComparison.Ordinal);
        Assert.Contains("maximum is 10", refused.Detail, StringComparison.Ordinal);
        Assert.Empty(store.RecoveryCases); // nothing opened, nobody removed
        var exactlyTen = await OpenAsync(alice, declared: declared, replacements: [NewOwner("cap-c")]);
        Assert.Equal(2, exactlyTen.Case!.EligibleOwnersAfter);
        Assert.Equal(9, Owners(org).Count);

        // Disabled owners are not active: eight disabled owners leave one active owner, so three replacements fit.
        store.RecoveryCases.Clear();
        store.RecoverySubjects.Clear();
        foreach (var id in extra)
        {
            store.Bindings.Single(b => b.AuthorityUserId == id).IdentityBindingEnabled = false;
        }

        Assert.Equal(3, (await OpenAsync(alice, replacements: [NewOwner("fit-a"), NewOwner("fit-b"), NewOwner("fit-c")])).Case!.Subjects.Count(x => x.Role == OwnerRecoverySubjectRoles.Replacement));
    }

    [Fact]
    public async Task ServerSideRulesRefuseDuplicateIdentitiesAndMoreThanThreeReplacements()
    {
        var memberSubject = store.Bindings.Single(b => b.AuthorityUserId == member).IdentitySubjectIdentifier;
        // The same existing member twice.
        Assert.Equal(AuthorityErrorCodes.InvalidCommand, await RefusedAsync(() => OpenAsync(alice, replacements: [ExistingOwner(member), ExistingOwner(member)])));
        // An existing member chosen once and the same verified identity proposed as a new one (any casing).
        Assert.Equal(AuthorityErrorCodes.OwnerRecoveryIdentityConflict, await RefusedAsync(() => OpenAsync(alice, replacements: [ExistingOwner(member), NewOwner(memberSubject.ToUpperInvariant())])));
        Assert.Equal(AuthorityErrorCodes.OwnerRecoveryIdentityConflict, await RefusedAsync(() => OpenAsync(alice, replacements: [NewOwner(memberSubject)])));
        // Two proposed identities that differ only in casing.
        Assert.Equal(AuthorityErrorCodes.InvalidCommand, await RefusedAsync(() => OpenAsync(alice, replacements: [NewOwner("dup-case-x"), NewOwner("DUP-CASE-X")])));
        // More than three.
        Assert.Equal(AuthorityErrorCodes.InvalidCommand, await RefusedAsync(() => OpenAsync(alice, replacements: [NewOwner("m-1"), NewOwner("m-2"), NewOwner("m-3"), NewOwner("m-4")])));
        Assert.Empty(store.RecoveryCases);
        // Mixed existing members and new identities, up to three, are accepted.
        var other = Guid.CreateVersion7();
        Bind(org, other);
        var mixed = await OpenAsync(alice, replacements: [ExistingOwner(member), ExistingOwner(other), NewOwner("mixed-new")]);
        Assert.Equal(3, mixed.Case!.Subjects.Count(x => x.Role == OwnerRecoverySubjectRoles.Replacement));
        Assert.Equal(2, mixed.Case.Subjects.Count(x => x.Role == OwnerRecoverySubjectRoles.Replacement && !x.IsNewIdentity));
        Assert.Equal(4, mixed.Case.EligibleOwnersAfter); // one owner plus three replacements
    }

    [Fact]
    public async Task ThePickerServesTenPerPageExcludesSelectedMembersAndReportsOwnerCapacity()
    {
        for (var i = 0; i < 12; i++)
        {
            var id = Guid.CreateVersion7();
            Bind(org, id);
            Hint(id, $"Person {i:00}");
        }

        var first = await MembersAsync(alice);
        Assert.Equal(10, first.Items.Count); // the default and the maximum
        Assert.Equal(14, first.TotalCount); // 12 + the two seeded members
        Assert.NotNull(first.NextCursor);
        Assert.Equal(1, first.ActiveOwnerCount);
        Assert.Equal(OrganizationOwnerLimits.MaximumActiveOwners, first.MaximumOwners);
        Assert.Equal(AuthorityErrorCodes.InvalidCommand, await RefusedAsync(() => MembersAsync(alice, limit: 11)));
        var second = await MembersAsync(alice, cursor: first.NextCursor);
        Assert.Equal(4, second.Items.Count);
        Assert.Empty(first.Items.Select(i => i.AuthorityUserId).Intersect(second.Items.Select(i => i.AuthorityUserId)));
        // Already selected members are excluded server-side, so counts and paging stay accurate.
        var chosen = first.Items.Take(2).Select(i => i.AuthorityUserId).ToList();
        var narrowed = await Service.SearchMembersAsync(alice.Actor, org, new OwnerRecoveryMemberSearchRequest(null, null, null, chosen), Guid.CreateVersion7(), Ct);
        Assert.Equal(12, narrowed.TotalCount);
        Assert.DoesNotContain(narrowed.Items, i => chosen.Contains(i.AuthorityUserId));
        Assert.Equal(AuthorityErrorCodes.InvalidCommand, await RefusedAsync(() => Service.SearchMembersAsync(alice.Actor, org, new OwnerRecoveryMemberSearchRequest(null, null, null, first.Items.Take(4).Select(i => i.AuthorityUserId).ToList()), Guid.CreateVersion7(), Ct)));
    }

    private async Task<CommandPlan> PlanAsync(Guid user, SecurityCommandKind kind, ResourceKind resourceKind, Guid resourceId, object payload)
    {
        await using var scope = await store.BeginScopeAsync(Ct);
        var graph = new DesiredStateGraph(store.Edges.Where(e => e.RemovedBySecurityCommandId is null).ToList());
        var state = store.Organizations.Single(o => o.OrganizationId == org);
        var request = new SecurityCommandRequest(org, kind, new ResourceRef(resourceKind, resourceId), state.AuthorizationRevision, JsonSerializer.SerializeToElement(payload, CanonicalJson.Options));
        var actor = new BoundActor(UserActor(user), store.Bindings.Single(b => b.OrganizationId == org && b.AuthorityUserId == user));
        return await CommandPlanner.PlanAsync(scope, org, actor, request, graph, Ct, StepUp, clock.GetUtcNow());
    }

    // ---- fakes ----

    private sealed record StoreSnapshot(string Cases, string Subjects, string Approvals, string Evidence, string Journal, string Head);

    private StoreSnapshot Snapshot() => new(JsonSerializer.Serialize(store.RecoveryCases), JsonSerializer.Serialize(store.RecoverySubjects), JsonSerializer.Serialize(store.RecoveryApprovals),
        JsonSerializer.Serialize(store.PlatformEvidenceUses), JsonSerializer.Serialize(store.PlatformJournal), JsonSerializer.Serialize(store.PlatformHead));

    private void Restore(StoreSnapshot s)
    {
        static void Reset<T>(List<T> list, string json)
        {
            list.Clear();
            list.AddRange(JsonSerializer.Deserialize<List<T>>(json)!);
        }

        Reset(store.RecoveryCases, s.Cases);
        Reset(store.RecoverySubjects, s.Subjects);
        Reset(store.RecoveryApprovals, s.Approvals);
        Reset(store.PlatformEvidenceUses, s.Evidence);
        Reset(store.PlatformJournal, s.Journal);
        store.PlatformHead = JsonSerializer.Deserialize<PlatformJournalHead>(s.Head);
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
