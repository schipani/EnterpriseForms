using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AuthorizationAuthority.Application.Common;
using AuthorizationAuthority.Application.Common.Ports;
using AuthorizationAuthority.Application.Features.Membership;
using AuthorizationAuthority.Application.Features.OperatorPlane;
using AuthorizationAuthority.Application.Features.SecurityCommands;
using AuthorizationAuthority.Contracts.V1;
using AuthorizationAuthority.Domain.Features.Organizations;
using AuthorizationAuthority.Domain.Features.SecurityCommands;
using AuthorizationAuthority.Domain.Features.Sessions;
using Xunit;

namespace AuthorizationAuthority.Tests;

/// <summary>
/// C8 M2 (AC-7, D8-24…D8-32) through the real command service over the in-memory store: owners only, no self-change,
/// step-up, one owner for designers and two distinct owners for owner changes (with expiry, single use, proposer still an
/// owner), disable/enable keeping edges and revoking sessions, the record-ownership and last-holder protections, membership
/// requests (issuer, subject, duplicates, expiry) and their operator approval with two independent ES256 approvals, the
/// roster read, idempotent retries and journaled refusals. The Docker path is AC7 in the Stage B suite.
/// </summary>
public sealed class MembershipAdministrationTests : IDisposable
{
    private const string Issuer = "https://idp";
    private static readonly Guid Org = Guid.CreateVersion7();
    private static readonly Guid OwnerA = Guid.CreateVersion7();
    private static readonly Guid OwnerB = Guid.CreateVersion7();
    private static readonly Guid OwnerC = Guid.CreateVersion7();
    private static readonly Guid Designer = Guid.CreateVersion7();
    private static readonly Guid Member = Guid.CreateVersion7();
    private static readonly Guid Administrator = Guid.CreateVersion7();
    private static readonly Guid Team = Guid.CreateVersion7();
    private static readonly FieldSearchApprovalGate StepUp = FieldSearchApprovalGate.Create(FieldSearchApprovalGate.SyntheticMode, "Testing", "TestIdentityProvider");

    private readonly InMemoryAuthorityStore store = new();
    private readonly ManualClock clock = new(new DateTimeOffset(2026, 10, 31, 9, 0, 0, TimeSpan.Zero));
    private readonly ECDsa approverOne = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private readonly ECDsa approverTwo = ECDsa.Create(ECCurve.NamedCurves.nistP256);

    public MembershipAdministrationTests()
    {
        store.Organizations.Add(new OrganizationAuthorizationState { OrganizationId = Org, OrganizationDisplayName = "Org", AuthorizationRevision = 5, ProjectionAppliedRevision = 5 });
        foreach (var user in new[] { OwnerA, OwnerB, OwnerC, Designer, Member, Administrator })
        {
            store.Bindings.Add(new IdentityBinding { IdentityBindingId = Guid.CreateVersion7(), OrganizationId = Org, AuthorityUserId = user, IdentityIssuerUri = Issuer, IdentitySubjectIdentifier = Subject(user), IdentityBindingEnabled = true });
        }

        foreach (var owner in new[] { OwnerA, OwnerB, OwnerC })
        {
            store.Edges.Add(Edge(ResourceTypeNames.Organization, Org, OrganizationRoleNames.OrganizationOwner, owner));
        }

        store.Edges.Add(Edge(ResourceTypeNames.Organization, Org, OrganizationRoleNames.Designer, Designer));
        store.Edges.Add(Edge(ResourceTypeNames.Organization, Org, OrganizationRoleNames.SystemAdministrator, Administrator));
        store.Resources.Add(new AuthorityResource { AuthorityResourceId = Guid.CreateVersion7(), OrganizationId = Org, ResourceTypeName = ResourceTypeNames.Team, ResourceEntityId = Team, ResourceLifecycleState = ResourceLifecycleStates.Active });
        store.Edges.Add(Edge(ResourceTypeNames.Team, Team, "member", Member));
    }

    public void Dispose()
    {
        approverOne.Dispose();
        approverTwo.Dispose();
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Subject(Guid user) => "subject-" + user.ToString("N");

    private static AuthorityRelationshipEdge Edge(string type, Guid id, string relation, Guid user) => new()
    {
        AuthorityRelationshipEdgeId = Guid.CreateVersion7(), OrganizationId = Org, ResourceTypeName = type, ResourceEntityId = id, RelationshipName = relation,
        SubjectTypeName = ResourceTypeNames.User, SubjectEntityId = user, SubjectRelationName = string.Empty, RelationshipCreatedOn = DateTimeOffset.UtcNow,
    };

    private SecurityCommandService Commands(FieldSearchApprovalGate? gate = null) => new(store, new NoFaults(), clock, gate ?? StepUp);

    private static VerifiedActor Actor(Guid user) => new(Issuer, Subject(user), "test");

    private long Revision => store.Organizations.Single().AuthorizationRevision;

    private bool Holds(string role, Guid user) => store.Edges.Any(e => e.RemovedBySecurityCommandId is null && e.ResourceEntityId == Org && e.RelationshipName == role && e.SubjectEntityId == user);

    private IdentityBinding Binding(Guid user) => store.Bindings.Single(b => b.AuthorityUserId == user);

    private Task<OperationStatus> SubmitAsync(Guid actor, SecurityCommandKind kind, object payload, string? key = null, long? expected = null, FieldSearchApprovalGate? gate = null) =>
        Commands(gate).SubmitAsync(Actor(actor), new SecurityCommandRequest(Org, kind, new ResourceRef(ResourceKind.Organization, Org), expected ?? Revision, JsonSerializer.SerializeToElement(payload, CanonicalJson.Options)),
            key ?? "ac7-" + Guid.NewGuid().ToString("N"), Guid.CreateVersion7(), Ct);

    private Task<OperationStatus> RoleAsync(Guid actor, Guid target, string role, bool grant, Guid? proposal = null, string? stepUp = FieldSearchApprovalGate.SyntheticStepUpEvidence, string? key = null) =>
        SubmitAsync(actor, grant ? SecurityCommandKind.GrantOrganizationRole : SecurityCommandKind.RevokeOrganizationRole, new OrganizationRolePayload(target, role, "governance ticket 7", proposal, stepUp), key);

    private async Task<Guid> ProposeAsync(Guid actor, Guid target, string change) =>
        (await SubmitAsync(actor, SecurityCommandKind.ProposeOrganizationOwnerChange, new OwnerChangeProposalPayload(target, change, "board decision", FieldSearchApprovalGate.SyntheticStepUpEvidence))).CommandId;

    private Task<OperationStatus> MembershipAsync(Guid actor, Guid target, bool enable, Guid? proposal = null) =>
        SubmitAsync(actor, enable ? SecurityCommandKind.EnableIdentityBinding : SecurityCommandKind.DisableIdentityBinding, new MembershipStatePayload(target, "left the unit", proposal, FieldSearchApprovalGate.SyntheticStepUpEvidence));

    private Task<OperationStatus> RequestAsync(Guid actor, string subject, string issuer = Issuer, string? role = OrganizationRoleNames.Designer) =>
        SubmitAsync(actor, SecurityCommandKind.RequestOrganizationMembership, new MembershipRequestPayload(issuer, subject, "Nia Newcomer", role, "joins intake", FieldSearchApprovalGate.SyntheticStepUpEvidence));

    private static async Task<string> RefusedAsync(Func<Task> action) => (await Assert.ThrowsAsync<AuthorityException>(action)).ErrorCode;

    private int RejectionsJournaled => store.Journal.Count(j => j.JournalEventKind == "MembershipCommandRejected");

    // ---- roles ----

    [Fact]
    public async Task AnOwnerGrantsAndRevokesDesignerAloneAndNothingElseChanges()
    {
        var before = Revision;
        await RoleAsync(OwnerA, Member, OrganizationRoleNames.Designer, grant: true);
        Assert.True(Holds(OrganizationRoleNames.Designer, Member));
        Assert.Equal(before + 1, Revision);
        Assert.DoesNotContain("synthetic", store.Commands.Last().CommandPayloadJson, StringComparison.Ordinal); // step-up never journaled
        await RoleAsync(OwnerA, Member, OrganizationRoleNames.Designer, grant: false);
        Assert.False(Holds(OrganizationRoleNames.Designer, Member));
        Assert.Contains(store.Edges, e => e.RemovedBySecurityCommandId is null && e.ResourceEntityId == Team && e.SubjectEntityId == Member);
    }

    [Theory]
    [InlineData("SecurityAdministrator")]
    [InlineData("Designer")]
    [InlineData("Member")]
    public async Task OnlyOwnersAdministerRolesAndRefusalsAreJournaled(string who)
    {
        var actor = who switch { "SecurityAdministrator" => Administrator, "Designer" => Designer, _ => Member };
        var target = actor == Member ? Designer : Member;
        var before = Revision;
        Assert.Equal(AuthorityErrorCodes.Forbidden, await RefusedAsync(() => RoleAsync(actor, target, OrganizationRoleNames.Designer, grant: true)));
        Assert.Equal(before, Revision);
        Assert.Equal(1, RejectionsJournaled);
        Assert.Equal(SecurityCommandStates.Rejected, store.Commands.Single().CommandStateName);
    }

    [Fact]
    public async Task NobodyChangesTheirOwnRolesOrMembership()
    {
        Assert.Equal(AuthorityErrorCodes.SelfChangeForbidden, await RefusedAsync(() => RoleAsync(OwnerA, OwnerA, OrganizationRoleNames.Designer, grant: true)));
        Assert.Equal(AuthorityErrorCodes.SelfChangeForbidden, await RefusedAsync(() => MembershipAsync(OwnerA, OwnerA, enable: false)));
        Assert.Equal(AuthorityErrorCodes.SelfChangeForbidden, await RefusedAsync(() => ProposeAsync(OwnerA, OwnerA, OwnerChangeKinds.RevokeOwner)));
    }

    [Fact]
    public async Task StepUpIsRequiredAndUnavailableOutsideDevelopment()
    {
        Assert.Equal(AuthorityErrorCodes.StepUpRequired, await RefusedAsync(() => RoleAsync(OwnerA, Member, OrganizationRoleNames.Designer, grant: true, stepUp: null)));
        Assert.Equal(AuthorityErrorCodes.StepUpRequired, await RefusedAsync(() => SubmitAsync(OwnerA, SecurityCommandKind.GrantOrganizationRole,
            new OrganizationRolePayload(Member, OrganizationRoleNames.Designer, "x", null, FieldSearchApprovalGate.SyntheticStepUpEvidence), gate: FieldSearchApprovalGate.Unavailable)));
        Assert.Throws<InvalidOperationException>(() => FieldSearchApprovalGate.Create(FieldSearchApprovalGate.SyntheticMode, "Production", "TestIdentityProvider"));
    }

    [Fact]
    public async Task SecurityAdministratorAndUnknownRolesAreNotAdministeredHere()
    {
        Assert.Equal(AuthorityErrorCodes.InvalidCommand, await RefusedAsync(() => RoleAsync(OwnerA, Member, OrganizationRoleNames.SystemAdministrator, grant: true)));
        Assert.Equal(AuthorityErrorCodes.RoleAlreadyHeld, await RefusedAsync(() => RoleAsync(OwnerA, Designer, OrganizationRoleNames.Designer, grant: true)));
        Assert.Equal(AuthorityErrorCodes.RoleNotHeld, await RefusedAsync(() => RoleAsync(OwnerA, Member, OrganizationRoleNames.Designer, grant: false)));
    }

    // ---- two distinct owners ----

    [Fact]
    public async Task OwnerGrantNeedsAProposalFromAnotherOwner()
    {
        Assert.Equal(AuthorityErrorCodes.OwnerApprovalRequired, await RefusedAsync(() => RoleAsync(OwnerA, Member, OrganizationRoleNames.OrganizationOwner, grant: true)));
        var proposal = await ProposeAsync(OwnerA, Member, OwnerChangeKinds.GrantOwner);
        Assert.False(Holds(OrganizationRoleNames.OrganizationOwner, Member)); // a proposal grants nothing
        Assert.Equal(AuthorityErrorCodes.OwnerApprovalRequired, await RefusedAsync(() => RoleAsync(OwnerA, Member, OrganizationRoleNames.OrganizationOwner, grant: true, proposal)));
        await RoleAsync(OwnerB, Member, OrganizationRoleNames.OrganizationOwner, grant: true, proposal);
        Assert.True(Holds(OrganizationRoleNames.OrganizationOwner, Member));
        var consumed = store.Proposals.Single();
        Assert.Equal(ProposalStates.Consumed, consumed.ProposalStateName);
        Assert.Equal(store.Commands.Last().SecurityCommandId, consumed.ConsumedBySecurityCommandId);
    }

    [Fact]
    public async Task AProposalIsSingleUseSoASecondConcurrentApprovalIsRefused()
    {
        var proposal = await ProposeAsync(OwnerA, OwnerC, OwnerChangeKinds.RevokeOwner);
        await RoleAsync(OwnerB, OwnerC, OrganizationRoleNames.OrganizationOwner, grant: false, proposal);
        Assert.False(Holds(OrganizationRoleNames.OrganizationOwner, OwnerC));
        Assert.Equal(AuthorityErrorCodes.ProposalNotPending, await RefusedAsync(() => RoleAsync(OwnerB, Member, OrganizationRoleNames.OrganizationOwner, grant: true, proposal)));
    }

    [Fact]
    public async Task AnExpiredMismatchedOrOrphanedProposalCannotBeUsed()
    {
        var proposal = await ProposeAsync(OwnerA, Member, OwnerChangeKinds.GrantOwner);
        Assert.Equal(AuthorityErrorCodes.ProposalMismatch, await RefusedAsync(() => RoleAsync(OwnerB, Designer, OrganizationRoleNames.OrganizationOwner, grant: true, proposal)));
        Assert.Equal(AuthorityErrorCodes.ProposalNotPending, await RefusedAsync(() => RoleAsync(OwnerB, Member, OrganizationRoleNames.OrganizationOwner, grant: true, Guid.CreateVersion7())));
        clock.Advance(TimeSpan.FromHours(24));
        Assert.Equal(AuthorityErrorCodes.ProposalExpired, await RefusedAsync(() => RoleAsync(OwnerB, Member, OrganizationRoleNames.OrganizationOwner, grant: true, proposal)));
        Assert.False(Holds(OrganizationRoleNames.OrganizationOwner, Member));
        // An expired proposal no longer blocks proposing again.
        await ProposeAsync(OwnerA, Member, OwnerChangeKinds.GrantOwner);
    }

    [Fact]
    public async Task ADelayedApprovalIsRefusedWhenTheProposerIsNoLongerAnOwner()
    {
        var proposal = await ProposeAsync(OwnerA, Member, OwnerChangeKinds.GrantOwner);
        var revokeA = await ProposeAsync(OwnerB, OwnerA, OwnerChangeKinds.RevokeOwner);
        await RoleAsync(OwnerC, OwnerA, OrganizationRoleNames.OrganizationOwner, grant: false, revokeA);
        Assert.Equal(AuthorityErrorCodes.OwnerApprovalRequired, await RefusedAsync(() => RoleAsync(OwnerB, Member, OrganizationRoleNames.OrganizationOwner, grant: true, proposal)));
    }

    [Fact]
    public async Task IdenticalPendingProposalsAreRefused()
    {
        await ProposeAsync(OwnerA, Member, OwnerChangeKinds.GrantOwner);
        Assert.Equal(AuthorityErrorCodes.ProposalExists, await RefusedAsync(() => ProposeAsync(OwnerB, Member, OwnerChangeKinds.GrantOwner)));
        Assert.Equal(AuthorityErrorCodes.RoleNotHeld, await RefusedAsync(() => ProposeAsync(OwnerA, Member, OwnerChangeKinds.DisableOwner)));
    }

    // ---- disable / enable ----

    [Fact]
    public async Task DisablingKeepsEdgesRevokesSessionsAndRefusesTheIdentityThenEnablingRestoresIt()
    {
        var binding = Binding(Member);
        store.Sessions.Add(new AuthenticatedSession { AuthenticatedSessionId = Guid.CreateVersion7(), OrganizationId = Org, IdentityBindingId = binding.IdentityBindingId, SessionSecretDigest = RandomNumberGenerator.GetBytes(32) });
        store.Sessions.Add(new AuthenticatedSession { AuthenticatedSessionId = Guid.CreateVersion7(), OrganizationId = Org, IdentityBindingId = binding.IdentityBindingId, SessionSecretDigest = RandomNumberGenerator.GetBytes(32) });
        await RoleAsync(OwnerA, Member, OrganizationRoleNames.Designer, grant: true);

        var disabled = await MembershipAsync(OwnerA, Member, enable: false);
        Assert.False(binding.IdentityBindingEnabled);
        Assert.Equal(clock.GetUtcNow(), binding.IdentityDisabledOn);
        Assert.Equal(disabled.CommandId, binding.IdentityDisabledBySecurityCommandId);
        Assert.All(store.Sessions, s => Assert.Equal(SessionRevocationReasons.MembershipDisabled, s.SessionRevocationReason));
        Assert.True(Holds(OrganizationRoleNames.Designer, Member)); // roles and team edges are kept
        Assert.Contains(store.Edges, e => e.RemovedBySecurityCommandId is null && e.ResourceEntityId == Team && e.SubjectEntityId == Member);
        Assert.Equal(AuthorityErrorCodes.ActorNotBound, await RefusedAsync(() => RoleAsync(Member, Designer, OrganizationRoleNames.Designer, grant: false)));
        Assert.Equal(AuthorityErrorCodes.MembershipStateConflict, await RefusedAsync(() => MembershipAsync(OwnerA, Member, enable: false)));

        await MembershipAsync(OwnerB, Member, enable: true);
        Assert.True(binding.IdentityBindingEnabled);
        Assert.Null(binding.IdentityDisabledOn);
        Assert.All(store.Sessions, s => Assert.NotNull(s.SessionRevokedOn)); // revoked sessions stay revoked: sign in again
    }

    [Fact]
    public async Task ADisabledMemberCannotReceiveARole()
    {
        await MembershipAsync(OwnerA, Member, enable: false);
        Assert.Equal(AuthorityErrorCodes.MembershipStateConflict, await RefusedAsync(() => RoleAsync(OwnerA, Member, OrganizationRoleNames.Designer, grant: true)));
    }

    [Fact]
    public async Task DisablingIsRefusedWhileThePersonDirectlyOwnsActiveRecords()
    {
        var record = Guid.CreateVersion7();
        var teamRecord = Guid.CreateVersion7();
        foreach (var id in new[] { record, teamRecord })
        {
            store.Resources.Add(new AuthorityResource { AuthorityResourceId = Guid.CreateVersion7(), OrganizationId = Org, ResourceTypeName = ResourceTypeNames.FormRecord, ResourceEntityId = id, ResourceLifecycleState = ResourceLifecycleStates.Active });
        }

        store.Edges.Add(Edge(ResourceTypeNames.FormRecord, record, "owner", Member));
        var ex = await Assert.ThrowsAsync<AuthorityException>(() => MembershipAsync(OwnerA, Member, enable: false));
        Assert.Equal(AuthorityErrorCodes.OwnsRecords, ex.ErrorCode);
        Assert.Contains("1 active record", ex.Detail, StringComparison.Ordinal);
        Assert.True(Binding(Member).IdentityBindingEnabled);
        store.Resources.Single(r => r.ResourceEntityId == record).ResourceLifecycleState = ResourceLifecycleStates.Revoked;
        await MembershipAsync(OwnerA, Member, enable: false); // a revoked record no longer blocks
    }

    [Fact]
    public async Task TheLastEnabledSecurityAdministratorCannotBeDisabled()
    {
        Assert.Equal(AuthorityErrorCodes.LastSecurityAdministrator, await RefusedAsync(() => MembershipAsync(OwnerA, Administrator, enable: false)));
        store.Edges.Add(Edge(ResourceTypeNames.Organization, Org, OrganizationRoleNames.SystemAdministrator, Member));
        await MembershipAsync(OwnerA, Administrator, enable: false);
    }

    [Fact]
    public async Task DisablingOrReEnablingAnOwnerNeedsTwoDistinctOwners()
    {
        Assert.Equal(AuthorityErrorCodes.OwnerApprovalRequired, await RefusedAsync(() => MembershipAsync(OwnerA, OwnerC, enable: false)));
        var disable = await ProposeAsync(OwnerA, OwnerC, OwnerChangeKinds.DisableOwner);
        Assert.Equal(AuthorityErrorCodes.ProposalMismatch, await RefusedAsync(() => RoleAsync(OwnerB, OwnerC, OrganizationRoleNames.OrganizationOwner, grant: false, disable)));
        await MembershipAsync(OwnerB, OwnerC, enable: false, disable);
        Assert.False(Binding(OwnerC).IdentityBindingEnabled);
        Assert.True(Holds(OrganizationRoleNames.OrganizationOwner, OwnerC)); // kept, unusable
        Assert.Equal(AuthorityErrorCodes.OwnerApprovalRequired, await RefusedAsync(() => MembershipAsync(OwnerA, OwnerC, enable: true)));
        var enable = await ProposeAsync(OwnerB, OwnerC, OwnerChangeKinds.EnableOwner);
        await MembershipAsync(OwnerA, OwnerC, enable: true, enable);
        Assert.True(Binding(OwnerC).IdentityBindingEnabled);
    }

    [Fact]
    public async Task TheLastEnabledOwnerIsNeverRemovedEvenWithAProposal()
    {
        // With owners A and B only, removing B needs a proposer and an executor other than B: only A exists, so it is impossible.
        store.Edges.RemoveAll(e => e.RelationshipName == OrganizationRoleNames.OrganizationOwner && e.SubjectEntityId == OwnerC);
        var proposal = await ProposeAsync(OwnerA, OwnerB, OwnerChangeKinds.RevokeOwner);
        Assert.Equal(AuthorityErrorCodes.OwnerApprovalRequired, await RefusedAsync(() => RoleAsync(OwnerA, OwnerB, OrganizationRoleNames.OrganizationOwner, grant: false, proposal)));
        Assert.Equal(AuthorityErrorCodes.SelfChangeForbidden, await RefusedAsync(() => RoleAsync(OwnerB, OwnerB, OrganizationRoleNames.OrganizationOwner, grant: false, proposal)));
        Assert.True(Holds(OrganizationRoleNames.OrganizationOwner, OwnerB));
    }

    // ---- idempotency and revisions ----

    [Fact]
    public async Task RetriesAreIdempotentAndStaleRevisionsAreRefused()
    {
        var first = await RoleAsync(OwnerA, Member, OrganizationRoleNames.Designer, grant: true, key: "ac7-retry-key-1");
        var revision = Revision;
        var retry = await RoleAsync(OwnerA, Member, OrganizationRoleNames.Designer, grant: true, key: "ac7-retry-key-1");
        Assert.Equal(first.CommandId, retry.CommandId);
        Assert.Equal(revision, Revision);
        Assert.Equal(AuthorityErrorCodes.IdempotencyConflict, await RefusedAsync(() => RoleAsync(OwnerA, Designer, OrganizationRoleNames.Designer, grant: false, key: "ac7-retry-key-1")));
        Assert.Equal(AuthorityErrorCodes.StaleAuthorizationRevision, await RefusedAsync(() => SubmitAsync(OwnerA, SecurityCommandKind.RevokeOrganizationRole,
            new OrganizationRolePayload(Member, OrganizationRoleNames.Designer, "x", null, FieldSearchApprovalGate.SyntheticStepUpEvidence), expected: revision - 1)));
    }

    // ---- membership requests and operator approval ----

    [Fact]
    public async Task AMembershipRequestGrantsNothingAndIsLimitedToTheOrganizationIssuerAndRealSubjects()
    {
        var bindings = store.Bindings.Count;
        await RequestAsync(OwnerA, "new-person-01");
        var request = store.MembershipRequests.Single();
        Assert.Equal(MembershipRequestStates.Pending, request.RequestStateName);
        Assert.Equal(clock.GetUtcNow().AddDays(7), request.RequestExpiresOn);
        Assert.Equal(bindings, store.Bindings.Count);
        Assert.Equal(AuthorityErrorCodes.IdentityNotAllowed, await RefusedAsync(() => RequestAsync(OwnerA, "someone", issuer: "https://other-tenant")));
        Assert.Equal(AuthorityErrorCodes.IdentityNotAllowed, await RefusedAsync(() => RequestAsync(OwnerA, "person@example.org")));
        Assert.Equal(AuthorityErrorCodes.IdentityAlreadyBound, await RefusedAsync(() => RequestAsync(OwnerA, Subject(Member))));
        Assert.Equal(AuthorityErrorCodes.MembershipRequestExists, await RefusedAsync(() => RequestAsync(OwnerB, "new-person-01")));
        Assert.Equal(AuthorityErrorCodes.InvalidCommand, await RefusedAsync(() => RequestAsync(OwnerA, "new-person-02", role: OrganizationRoleNames.OrganizationOwner)));
        Assert.Equal(AuthorityErrorCodes.Forbidden, await RefusedAsync(() => RequestAsync(Administrator, "new-person-03")));
        clock.Advance(TimeSpan.FromDays(7));
        await RequestAsync(OwnerB, "new-person-01"); // the expired request is recorded as Expired and no longer blocks
        Assert.Equal(MembershipRequestStates.Expired, request.RequestStateName);
    }

    [Fact]
    public void EntraIdentitiesMustUseTheObjectIdAsSubject()
    {
        Assert.True(CommandPlanner.IsValidSubject("https://login.microsoftonline.com/7f0c2f6e-0000-0000-0000-000000000000/v2.0", "0f1e2d3c-4b5a-6978-8796-a5b4c3d2e1f0"));
        Assert.False(CommandPlanner.IsValidSubject("https://login.microsoftonline.com/7f0c2f6e-0000-0000-0000-000000000000/v2.0", "nia@contoso.example"));
        Assert.False(CommandPlanner.IsValidSubject("https://login.microsoftonline.com/7f0c2f6e-0000-0000-0000-000000000000/v2.0", "nia-newcomer"));
        Assert.False(CommandPlanner.IsValidSubject(Issuer, "nia@contoso.example"));
    }

    [Fact]
    public async Task TwoIndependentOperatorApprovalsCreateTheBindingOnce()
    {
        await RequestAsync(OwnerA, "new-person-04");
        var request = store.MembershipRequests.Single();
        var service = Provisioning();
        var preview = await service.ApproveAsync(Org, request.OrganizationMembershipRequestId, Approval(dryRun: true), "operator-test", Guid.CreateVersion7(), Ct);
        Assert.True(preview.Eligible);
        Assert.Equal(AuthorityErrorCodes.OperatorApprovalInvalid, await RefusedAsync(() => service.ApproveAsync(Org, request.OrganizationMembershipRequestId, Approval(false, [Sign(approverOne, "approver-1", preview)]), "operator-test", Guid.CreateVersion7(), Ct)));
        Assert.Equal(AuthorityErrorCodes.OperatorApprovalsNotIndependent, await RefusedAsync(() => service.ApproveAsync(Org, request.OrganizationMembershipRequestId,
            Approval(false, [Sign(approverOne, "approver-1", preview), Sign(approverOne, "approver-1", preview)]), "operator-test", Guid.CreateVersion7(), Ct)));
        Assert.Equal(MembershipRequestStates.Pending, request.RequestStateName);

        var outcome = await service.ApproveAsync(Org, request.OrganizationMembershipRequestId, Approval(false, [Sign(approverOne, "approver-1", preview), Sign(approverTwo, "approver-2", preview)]), "operator-test", Guid.CreateVersion7(), Ct);
        Assert.NotNull(outcome.SecurityCommandId);
        var binding = Binding(request.RequestedAuthorityUserId);
        Assert.True(binding.IdentityBindingEnabled);
        Assert.Equal("new-person-04", binding.IdentitySubjectIdentifier);
        Assert.Equal(request.OrganizationMembershipRequestId, binding.OrganizationMembershipRequestId);
        Assert.True(Holds(OrganizationRoleNames.Designer, request.RequestedAuthorityUserId));
        Assert.Equal(MembershipRequestStates.Approved, request.RequestStateName);
        Assert.Contains(store.Journal, j => j.JournalEventKind == "OrganizationMembershipApproved");

        var replay = await service.ApproveAsync(Org, request.OrganizationMembershipRequestId, Approval(false, [Sign(approverOne, "approver-1", preview), Sign(approverTwo, "approver-2", preview)]), "operator-test", Guid.CreateVersion7(), Ct);
        Assert.True(replay.Replayed);
        Assert.Single(store.Bindings, b => b.IdentitySubjectIdentifier == "new-person-04");
    }

    [Fact]
    public async Task ALateApprovalIsRefusedWhenTheRequestExpiredOrTheRequesterIsNoLongerAnOwner()
    {
        await RequestAsync(OwnerA, "new-person-05");
        await RequestAsync(OwnerB, "new-person-06");
        var expiring = store.MembershipRequests.Single(r => r.RequestedSubjectIdentifier == "new-person-05");
        var orphaned = store.MembershipRequests.Single(r => r.RequestedSubjectIdentifier == "new-person-06");
        var service = Provisioning();
        var orphanPreview = await service.ApproveAsync(Org, orphaned.OrganizationMembershipRequestId, Approval(true), "operator-test", Guid.CreateVersion7(), Ct);
        var revokeB = await ProposeAsync(OwnerA, OwnerB, OwnerChangeKinds.RevokeOwner);
        await RoleAsync(OwnerC, OwnerB, OrganizationRoleNames.OrganizationOwner, grant: false, revokeB);
        Assert.Equal(AuthorityErrorCodes.OwnerApprovalRequired, await RefusedAsync(() => service.ApproveAsync(Org, orphaned.OrganizationMembershipRequestId,
            Approval(false, [Sign(approverOne, "approver-1", orphanPreview), Sign(approverTwo, "approver-2", orphanPreview)]), "operator-test", Guid.CreateVersion7(), Ct)));

        var preview = await service.ApproveAsync(Org, expiring.OrganizationMembershipRequestId, Approval(true), "operator-test", Guid.CreateVersion7(), Ct);
        clock.Advance(TimeSpan.FromDays(7));
        Assert.Equal(AuthorityErrorCodes.MembershipRequestExpired, await RefusedAsync(() => service.ApproveAsync(Org, expiring.OrganizationMembershipRequestId,
            Approval(false, [Sign(approverOne, "approver-1", preview), Sign(approverTwo, "approver-2", preview)]), "operator-test", Guid.CreateVersion7(), Ct)));
        Assert.Equal(MembershipRequestStates.Expired, expiring.RequestStateName);
        Assert.DoesNotContain(store.Bindings, b => b.IdentitySubjectIdentifier is "new-person-05" or "new-person-06");
    }

    [Fact]
    public async Task RejectedRequestsCannotBeApprovedAndApprovalIsUnavailableWithoutDualControl()
    {
        await RequestAsync(OwnerA, "new-person-07");
        var request = store.MembershipRequests.Single();
        Assert.Equal(AuthorityErrorCodes.OperatorDualControlUnavailable, await RefusedAsync(() => new MembershipProvisioningService(store, OperatorApprovalVerifier.Unavailable, clock)
            .ApproveAsync(Org, request.OrganizationMembershipRequestId, Approval(false, ["a.b.c", "d.e.f"]), "operator-test", Guid.CreateVersion7(), Ct)));
        var rejected = await Provisioning().RejectAsync(Org, request.OrganizationMembershipRequestId, new MembershipRejectionRequest(Org, "not a member of this unit"), "operator-test", Guid.CreateVersion7(), Ct);
        Assert.Equal(MembershipRequestStates.Rejected, rejected.RequestState);
        var preview = await Provisioning().ApproveAsync(Org, request.OrganizationMembershipRequestId, Approval(true), "operator-test", Guid.CreateVersion7(), Ct);
        Assert.False(preview.Eligible);
        Assert.Equal(AuthorityErrorCodes.MembershipRequestNotPending, preview.RefusalCode);
    }

    // ---- AC-7.5: cancel and replace (edit) a pending membership request ----

    private Task<OperationStatus> CancelRequestAsync(Guid actor, OrganizationMembershipRequest request, string? digest = null, string? key = null, string? stepUp = FieldSearchApprovalGate.SyntheticStepUpEvidence) =>
        SubmitAsync(actor, SecurityCommandKind.CancelOrganizationMembershipRequest, new CancelMembershipRequestPayload(request.OrganizationMembershipRequestId, digest ?? MembershipRequestDigest.Compute(request), "entered for the wrong unit", stepUp), key);

    private Task<OperationStatus> ReplaceRequestAsync(Guid actor, OrganizationMembershipRequest request, string hint = "Nia Newcomer-Okafor", string? role = null, string reason = "corrected after the intake call", string? digest = null, string? key = null, string? stepUp = FieldSearchApprovalGate.SyntheticStepUpEvidence) =>
        SubmitAsync(actor, SecurityCommandKind.ReplaceOrganizationMembershipRequest, new ReplaceMembershipRequestPayload(request.OrganizationMembershipRequestId, digest ?? MembershipRequestDigest.Compute(request), hint, role, reason, stepUp), key);

    private Task<MembershipApprovalResponse> TryApproveAsync(Guid requestId, bool dryRun, object? preview = null) =>
        Provisioning().ApproveAsync(Org, requestId, dryRun ? Approval(true) : Approval(false, [Sign(approverOne, "approver-1", (MembershipApprovalResponse)preview!), Sign(approverTwo, "approver-2", (MembershipApprovalResponse)preview!)]), "operator-test", Guid.CreateVersion7(), Ct);

    [Fact]
    public async Task CancellingARequestRecordsWhoWhenAndWhyGrantsNothingAndItCanNeverBeApprovedOrRejected()
    {
        await RequestAsync(OwnerA, "cancel-person-01");
        var request = store.MembershipRequests.Single();
        var before = Revision;
        var bindings = store.Bindings.Count;
        var outcome = await CancelRequestAsync(OwnerB, request);
        Assert.Equal(before + 1, Revision);
        Assert.Equal(MembershipRequestStates.Cancelled, request.RequestStateName);
        Assert.Equal(OwnerB, request.RequestCancelledByAuthorityUserId);
        Assert.Equal(clock.GetUtcNow(), request.RequestCancelledOn);
        Assert.Equal("entered for the wrong unit", request.RequestCancellationReasonText);
        Assert.Equal(outcome.CommandId, request.DecidedBySecurityCommandId);
        Assert.Equal(bindings, store.Bindings.Count); // nothing granted, nothing changed
        Assert.Contains(store.Journal, j => j.JournalEventKind == "SecurityCommandCommitted" && j.JournalEventBodyJson.Contains("CancelOrganizationMembershipRequest", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain("synthetic", store.Commands.Last().CommandPayloadJson, StringComparison.Ordinal); // step-up never journaled

        // Operator plane: neither a dry run, nor a stale approval, nor a rejection can decide a cancelled request.
        var preview = await TryApproveAsync(request.OrganizationMembershipRequestId, dryRun: true);
        Assert.False(preview.Eligible);
        Assert.Equal(AuthorityErrorCodes.MembershipRequestNotPending, preview.RefusalCode);
        Assert.Equal(AuthorityErrorCodes.MembershipRequestNotPending, await RefusedAsync(() => Provisioning().RejectAsync(Org, request.OrganizationMembershipRequestId, new MembershipRejectionRequest(Org, "not a member of this unit"), "operator-test", Guid.CreateVersion7(), Ct)));
        Assert.DoesNotContain(store.Bindings, b => b.IdentitySubjectIdentifier == "cancel-person-01");

        // A new request for the same identity may follow a cancellation (existing rules apply).
        await RequestAsync(OwnerA, "cancel-person-01");
        Assert.Equal(2, store.MembershipRequests.Count(r => r.RequestedSubjectIdentifier == "cancel-person-01"));
        Assert.Single(store.MembershipRequests, r => r.RequestedSubjectIdentifier == "cancel-person-01" && r.RequestStateName == MembershipRequestStates.Pending);
    }

    [Fact]
    public async Task ReplacingARequestKeepsTheOriginalCreatesANewPendingOneWithLineageAndAFreshExpiryAndCopiesTheIdentity()
    {
        await RequestAsync(OwnerA, "replace-person-01");
        var original = store.MembershipRequests.Single();
        var originalCreated = original.RequestCreatedOn;
        var originalExpiry = original.RequestExpiresOn;
        var oldPreview = await TryApproveAsync(original.OrganizationMembershipRequestId, dryRun: true); // an operator already looked at the original
        Assert.True(oldPreview.Eligible);
        clock.Advance(TimeSpan.FromDays(3));
        var outcome = await ReplaceRequestAsync(OwnerB, original, "Nia Newcomer-Okafor", role: null, reason: "corrected after the intake call");

        var replacement = store.MembershipRequests.Single(r => r.OrganizationMembershipRequestId == outcome.CommandId);
        Assert.NotEqual(original.OrganizationMembershipRequestId, replacement.OrganizationMembershipRequestId);
        Assert.Equal(MembershipRequestStates.Pending, replacement.RequestStateName);
        Assert.Equal(original.OrganizationMembershipRequestId, replacement.ReplacesMembershipRequestId);
        Assert.Equal(clock.GetUtcNow() + TimeSpan.FromDays(7), replacement.RequestExpiresOn); // a fresh seven days
        Assert.True(replacement.RequestExpiresOn > originalExpiry);
        Assert.Equal(OwnerB, replacement.RequestedByAuthorityUserId);
        Assert.Equal(original.RequestedIssuerUri, replacement.RequestedIssuerUri); // identity copied by the Authority, never supplied
        Assert.Equal(original.RequestedSubjectIdentifier, replacement.RequestedSubjectIdentifier);
        Assert.NotEqual(original.RequestedAuthorityUserId, replacement.RequestedAuthorityUserId);
        Assert.Equal("Nia Newcomer-Okafor", replacement.RequestedDisplayNameHint);
        Assert.Null(replacement.RequestedInitialRoleName);

        // The original is preserved in full, only marked Replaced with who/when/why.
        Assert.Equal(MembershipRequestStates.Replaced, original.RequestStateName);
        Assert.Equal("Nia Newcomer", original.RequestedDisplayNameHint);
        Assert.Equal(OrganizationRoleNames.Designer, original.RequestedInitialRoleName);
        Assert.Equal("joins intake", original.RequestReasonText);
        Assert.Equal(originalCreated, original.RequestCreatedOn);
        Assert.Equal(OwnerB, original.RequestCancelledByAuthorityUserId);
        Assert.Equal(outcome.CommandId, original.DecidedBySecurityCommandId);
        Assert.DoesNotContain(store.Bindings, b => b.IdentitySubjectIdentifier == "replace-person-01"); // replacing grants nothing

        // The roster shows the lineage in both directions; one pending request for the identity.
        ProjectionCaughtUp();
        var view = await new OrganizationRosterService(store, StepUp, OperatorApprovalVerifier.Unavailable, clock).GetAsync(Actor(OwnerA), new OrganizationRosterRequest(Org), Guid.CreateVersion7(), Ct);
        var oldEntry = view.MembershipRequests.Single(r => r.RequestId == original.OrganizationMembershipRequestId);
        var newEntry = view.MembershipRequests.Single(r => r.RequestId == replacement.OrganizationMembershipRequestId);
        Assert.Equal("Replaced", oldEntry.RequestState);
        Assert.Equal(replacement.OrganizationMembershipRequestId, oldEntry.ReplacedByRequestId);
        Assert.Equal(original.OrganizationMembershipRequestId, newEntry.ReplacesRequestId);
        Assert.NotEqual(oldEntry.RequestDigest, newEntry.RequestDigest);
        Assert.Single(store.MembershipRequests, r => r.RequestedSubjectIdentifier == "replace-person-01" && r.RequestStateName == MembershipRequestStates.Pending);

        // Two-operator approval applies to the replacement only; the original can never be approved, with or without an old preview.
        var stale = await TryApproveAsync(original.OrganizationMembershipRequestId, dryRun: true);
        Assert.False(stale.Eligible);
        Assert.Equal(AuthorityErrorCodes.MembershipRequestNotPending, stale.RefusalCode);
        var preview = await TryApproveAsync(replacement.OrganizationMembershipRequestId, dryRun: true);
        Assert.True(preview.Eligible);
        // Two valid operator approvals signed over the OLD request's preview are refused: the original is no longer pending.
        Assert.Equal(AuthorityErrorCodes.MembershipRequestNotPending, await RefusedAsync(() => Provisioning().ApproveAsync(Org, original.OrganizationMembershipRequestId,
            Approval(false, [Sign(approverOne, "approver-1", oldPreview), Sign(approverTwo, "approver-2", oldPreview)]), "operator-test", Guid.CreateVersion7(), Ct)));
        // Approvals signed for the new request never apply to the old one either (whatever the refusal, nothing is bound).
        Assert.Contains(await RefusedAsync(() => Provisioning().ApproveAsync(Org, original.OrganizationMembershipRequestId,
            Approval(false, [Sign(approverOne, "approver-1", preview), Sign(approverTwo, "approver-2", preview)]), "operator-test", Guid.CreateVersion7(), Ct)), new[] { AuthorityErrorCodes.MembershipRequestNotPending, AuthorityErrorCodes.OperatorApprovalInvalid });
        Assert.DoesNotContain(store.Bindings, b => b.IdentitySubjectIdentifier == "replace-person-01");
        await TryApproveAsync(replacement.OrganizationMembershipRequestId, dryRun: false, preview);
        var binding = Binding(replacement.RequestedAuthorityUserId);
        Assert.Equal("replace-person-01", binding.IdentitySubjectIdentifier);
        Assert.Equal(replacement.OrganizationMembershipRequestId, binding.OrganizationMembershipRequestId);
        Assert.False(Holds(OrganizationRoleNames.Designer, replacement.RequestedAuthorityUserId)); // the edited initial role (none) was applied
        Assert.Equal(MembershipRequestStates.Replaced, original.RequestStateName);
    }

    [Theory]
    [InlineData("SecurityAdministrator")]
    [InlineData("Designer")]
    [InlineData("Member")]
    public async Task OnlyOwnersCancelOrReplaceARequestAndRefusalsAreJournaled(string who)
    {
        await RequestAsync(OwnerA, "authz-person-01");
        var request = store.MembershipRequests.Single();
        var actor = who switch { "SecurityAdministrator" => Administrator, "Designer" => Designer, _ => Member };
        var before = Revision;
        Assert.Equal(AuthorityErrorCodes.Forbidden, await RefusedAsync(() => CancelRequestAsync(actor, request)));
        Assert.Equal(AuthorityErrorCodes.Forbidden, await RefusedAsync(() => ReplaceRequestAsync(actor, request)));
        Assert.Equal(before, Revision);
        Assert.Equal(MembershipRequestStates.Pending, request.RequestStateName);
        Assert.Equal(2, RejectionsJournaled);
    }

    [Fact]
    public async Task StepUpIsRequiredAndAnotherOrganizationsRequestIsNotFound()
    {
        await RequestAsync(OwnerA, "authz-person-02");
        var request = store.MembershipRequests.Single();
        Assert.NotEqual(string.Empty, await RefusedAsync(() => CancelRequestAsync(OwnerA, request, stepUp: null)));
        Assert.NotEqual(string.Empty, await RefusedAsync(() => ReplaceRequestAsync(OwnerA, request, stepUp: null)));
        Assert.NotEqual(string.Empty, await RefusedAsync(() => CancelRequestAsync(OwnerA, request, stepUp: "forged")));
        Assert.Equal(MembershipRequestStates.Pending, request.RequestStateName);

        // A request that belongs to another organization is simply not found for this organization's owner.
        var otherOrg = Guid.CreateVersion7();
        var foreign = new OrganizationMembershipRequest
        {
            OrganizationMembershipRequestId = Guid.CreateVersion7(), OrganizationId = otherOrg, RequestedAuthorityUserId = Guid.CreateVersion7(), RequestedIssuerUri = Issuer, RequestedSubjectIdentifier = "foreign-person",
            RequestedDisplayNameHint = "Foreign", RequestedByAuthorityUserId = Guid.CreateVersion7(), RequestReasonText = "other organization", RequestCreatedOn = clock.GetUtcNow(), RequestExpiresOn = clock.GetUtcNow().AddDays(7), RequestStateName = MembershipRequestStates.Pending,
        };
        store.MembershipRequests.Add(foreign);
        Assert.Equal(AuthorityErrorCodes.NotFound, await RefusedAsync(() => CancelRequestAsync(OwnerA, foreign)));
        Assert.Equal(AuthorityErrorCodes.NotFound, await RefusedAsync(() => ReplaceRequestAsync(OwnerA, foreign)));
        Assert.Equal(MembershipRequestStates.Pending, foreign.RequestStateName);
    }

    [Fact]
    public async Task StaleDigestsExpiredAndDecidedRequestsAreRefusedAndNothingChanges()
    {
        await RequestAsync(OwnerA, "stale-person-01");
        var request = store.MembershipRequests.Single();
        var readByBothOwners = MembershipRequestDigest.Compute(request);

        // Two owners edit the same reading: exactly one wins, the other is refused as changed and can neither cancel nor replace.
        var first = await ReplaceRequestAsync(OwnerA, request, digest: readByBothOwners);
        Assert.Equal(AuthorityErrorCodes.MembershipRequestChanged, await RefusedAsync(() => ReplaceRequestAsync(OwnerB, request, "Someone Else", digest: readByBothOwners)));
        Assert.Equal(AuthorityErrorCodes.MembershipRequestChanged, await RefusedAsync(() => CancelRequestAsync(OwnerB, request, digest: readByBothOwners)));
        Assert.Single(store.MembershipRequests, r => r.ReplacesMembershipRequestId == request.OrganizationMembershipRequestId);
        var replacement = store.MembershipRequests.Single(r => r.OrganizationMembershipRequestId == first.CommandId);

        // A digest of the right request but an older reading is refused too (the edited content changed the digest).
        Assert.Equal(AuthorityErrorCodes.MembershipRequestChanged, await RefusedAsync(() => CancelRequestAsync(OwnerA, replacement, digest: readByBothOwners)));
        Assert.Equal(AuthorityErrorCodes.MembershipRequestChanged, await RefusedAsync(() => CancelRequestAsync(OwnerA, replacement, digest: "00" + MembershipRequestDigest.Compute(replacement)[2..])));
        Assert.Equal(AuthorityErrorCodes.InvalidCommand, await RefusedAsync(() => CancelRequestAsync(OwnerA, replacement, digest: " ")));

        // An already approved request cannot be cancelled or replaced.
        var preview = await TryApproveAsync(replacement.OrganizationMembershipRequestId, dryRun: true);
        await TryApproveAsync(replacement.OrganizationMembershipRequestId, dryRun: false, preview);
        Assert.Equal(MembershipRequestStates.Approved, replacement.RequestStateName);
        Assert.Equal(AuthorityErrorCodes.MembershipRequestNotPending, await RefusedAsync(() => CancelRequestAsync(OwnerA, replacement)));
        Assert.Equal(AuthorityErrorCodes.MembershipRequestNotPending, await RefusedAsync(() => ReplaceRequestAsync(OwnerA, replacement)));
        Assert.True(Binding(replacement.RequestedAuthorityUserId).IdentityBindingEnabled); // an effective membership is never touched

        // An expired request cannot be changed; the owner makes a new one.
        await RequestAsync(OwnerA, "stale-person-02");
        var aging = store.MembershipRequests.Single(r => r.RequestedSubjectIdentifier == "stale-person-02");
        clock.Advance(TimeSpan.FromDays(7));
        Assert.Equal(AuthorityErrorCodes.MembershipRequestExpired, await RefusedAsync(() => CancelRequestAsync(OwnerA, aging)));
        Assert.Equal(AuthorityErrorCodes.MembershipRequestExpired, await RefusedAsync(() => ReplaceRequestAsync(OwnerA, aging)));
        Assert.Equal(MembershipRequestStates.Pending, aging.RequestStateName);
    }

    [Fact]
    public async Task ReplaceValidatesTheEditableFieldsAndRefusesNoChange()
    {
        await RequestAsync(OwnerA, "valid-person-01");
        var request = store.MembershipRequests.Single();
        Assert.Equal(AuthorityErrorCodes.InvalidCommand, await RefusedAsync(() => ReplaceRequestAsync(OwnerA, request, hint: " ")));
        Assert.Equal(AuthorityErrorCodes.InvalidCommand, await RefusedAsync(() => ReplaceRequestAsync(OwnerA, request, hint: new string('x', 201))));
        Assert.Equal(AuthorityErrorCodes.InvalidCommand, await RefusedAsync(() => ReplaceRequestAsync(OwnerA, request, hint: "Bad\u0007Name")));
        Assert.Equal(AuthorityErrorCodes.InvalidCommand, await RefusedAsync(() => ReplaceRequestAsync(OwnerA, request, role: OrganizationRoleNames.OrganizationOwner)));
        Assert.Equal(AuthorityErrorCodes.InvalidCommand, await RefusedAsync(() => ReplaceRequestAsync(OwnerA, request, role: OrganizationRoleNames.SystemAdministrator)));
        Assert.Equal(AuthorityErrorCodes.InvalidCommand, await RefusedAsync(() => ReplaceRequestAsync(OwnerA, request, reason: " ")));
        Assert.Equal(AuthorityErrorCodes.InvalidCommand, await RefusedAsync(() => ReplaceRequestAsync(OwnerA, request, hint: request.RequestedDisplayNameHint, role: request.RequestedInitialRoleName, reason: request.RequestReasonText)));
        Assert.Equal(MembershipRequestStates.Pending, request.RequestStateName);
        Assert.Single(store.MembershipRequests);
    }

    [Fact]
    public async Task RetriesOfCancelAndReplaceAreIdempotentAndCreateOneChange()
    {
        await RequestAsync(OwnerA, "idem-person-01");
        await RequestAsync(OwnerA, "idem-person-02");
        var toReplace = store.MembershipRequests.Single(r => r.RequestedSubjectIdentifier == "idem-person-01");
        var toCancel = store.MembershipRequests.Single(r => r.RequestedSubjectIdentifier == "idem-person-02");
        var digestOne = MembershipRequestDigest.Compute(toReplace);
        var one = await ReplaceRequestAsync(OwnerA, toReplace, key: "ac75-replace-0001", digest: digestOne);
        var oneAgain = await ReplaceRequestAsync(OwnerA, toReplace, key: "ac75-replace-0001", digest: digestOne);
        Assert.Equal(one.CommandId, oneAgain.CommandId);
        Assert.Equal(1, store.MembershipRequests.Count(r => r.ReplacesMembershipRequestId == toReplace.OrganizationMembershipRequestId));
        var digestTwo = MembershipRequestDigest.Compute(toCancel);
        var two = await CancelRequestAsync(OwnerA, toCancel, digestTwo, key: "ac75-cancel-0001");
        var twoAgain = await CancelRequestAsync(OwnerA, toCancel, digestTwo, key: "ac75-cancel-0001");
        Assert.Equal(two.CommandId, twoAgain.CommandId);
        Assert.Equal(MembershipRequestStates.Cancelled, toCancel.RequestStateName);
    }

    // ---- roster ----

    [Fact]
    public async Task TheRosterIsForOwnersAndSecurityAdministratorsAndShowsStateRolesProposalsAndRequests()
    {
        await MembershipAsync(OwnerA, Member, enable: false);
        await ProposeAsync(OwnerA, Designer, OwnerChangeKinds.GrantOwner);
        await RequestAsync(OwnerA, "new-person-08", role: null);
        ProjectionCaughtUp();
        var roster = new OrganizationRosterService(store, StepUp, OperatorApprovalVerifier.Unavailable, clock);
        var view = await roster.GetAsync(Actor(OwnerB), new OrganizationRosterRequest(Org), Guid.CreateVersion7(), Ct);
        Assert.True(view.CanManageMembers);
        Assert.Equal(Issuer, view.OrganizationIdentityIssuer);
        Assert.Equal(6, view.Members.Count);
        Assert.False(view.Members.Single(m => m.AuthorityUserId == Member).MembershipEnabled);
        Assert.Equal([OrganizationRoleNames.OrganizationOwner], view.Members.Single(m => m.AuthorityUserId == OwnerA).OrganizationRoles);
        Assert.Equal("Pending", Assert.Single(view.OwnerProposals).ProposalState);
        Assert.Equal("Pending", Assert.Single(view.MembershipRequests).RequestState);
        var asAdministrator = await roster.GetAsync(Actor(Administrator), new OrganizationRosterRequest(Org), Guid.CreateVersion7(), Ct);
        Assert.False(asAdministrator.CanManageMembers);
        Assert.Equal(AuthorityErrorCodes.NotFound, await RefusedAsync(() => roster.GetAsync(Actor(Designer), new OrganizationRosterRequest(Org), Guid.CreateVersion7(), Ct)));
        clock.Advance(TimeSpan.FromHours(25));
        Assert.Equal("Expired", (await roster.GetAsync(Actor(OwnerB), new OrganizationRosterRequest(Org), Guid.CreateVersion7(), Ct)).OwnerProposals.Single().ProposalState);
    }

    /// <summary>The projector is outside this unit test (the in-memory store reports no pending archive): mark every revision applied so the gate opens.</summary>
    private void ProjectionCaughtUp()
    {
        var state = store.Organizations.Single();
        state.ProjectionAppliedRevision = state.AuthorizationRevision;
        state.ProjectionZedToken = "test-zed-token";
    }

    private MembershipProvisioningService Provisioning() => new(store, OperatorApprovalVerifier.Create(new OperatorApprovalOptions
    {
        Mode = OperatorApprovalVerifier.SyntheticMode,
        Approvers =
        [
            new OperatorApproverOptions { ApproverId = "approver-1", PublicKeyBase64 = Convert.ToBase64String(approverOne.ExportSubjectPublicKeyInfo()) },
            new OperatorApproverOptions { ApproverId = "approver-2", PublicKeyBase64 = Convert.ToBase64String(approverTwo.ExportSubjectPublicKeyInfo()) },
        ],
    }, "Testing"), clock);

    private static MembershipApprovalRequest Approval(bool dryRun, IReadOnlyList<string>? approvals = null) => new(Org, "Owner request verified with the unit lead (OPS-12)", dryRun, approvals);

    private string Sign(ECDsa key, string approverId, MembershipApprovalResponse preview)
    {
        var now = clock.GetUtcNow();
        var claims = new Dictionary<string, object>
        {
            ["iss"] = OperatorApprovalClaims.Issuer,
            ["aud"] = OperatorApprovalClaims.Audience,
            ["sub"] = approverId,
            ["jti"] = Guid.NewGuid().ToString("N"),
            ["iat"] = now.ToUnixTimeSeconds(),
            ["nbf"] = now.AddSeconds(-5).ToUnixTimeSeconds(),
            ["exp"] = now.AddMinutes(10).ToUnixTimeSeconds(),
            [OperatorApprovalClaims.StatementDigest] = preview.ApprovalStatementSha256,
            [OperatorApprovalClaims.OperationId] = preview.OperationId.ToString("D"),
            [OperatorApprovalClaims.OperationKind] = preview.OperationKind,
            [OperatorApprovalClaims.OrganizationId] = preview.OrganizationId.ToString("D"),
            [OperatorApprovalClaims.TargetAuthorityUserId] = preview.Target.AuthorityUserId.ToString("D"),
        };
        var header = Base64Url.EncodeToString(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { alg = "ES256", typ = "JWT", kid = approverId })));
        var body = Base64Url.EncodeToString(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(claims)));
        return header + "." + body + "." + Base64Url.EncodeToString(key.SignData(Encoding.ASCII.GetBytes(header + "." + body), HashAlgorithmName.SHA256));
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
