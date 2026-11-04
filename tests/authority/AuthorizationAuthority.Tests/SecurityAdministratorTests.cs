using System.Text.Json;
using AuthorizationAuthority.Application.Common;
using AuthorizationAuthority.Application.Features.SecurityCommands;
using AuthorizationAuthority.Contracts.V1;
using AuthorizationAuthority.Domain.Features.Organizations;
using AuthorizationAuthority.Domain.Features.SecurityCommands;
using Xunit;

namespace AuthorizationAuthority.Tests;

/// <summary>
/// Owner message 7 at unit level: GrantSystemRole / RevokeSystemRole (Security Administrator = system_administrator) and the
/// separation-of-duties tightenings that keep security administration from becoming record access or policy control. The
/// full path (journal, SpiceDB, business API, audit) runs on Docker in the Stage B suite (SecurityAdministratorTests there).
/// </summary>
public sealed class SecurityAdministratorTests
{
    private static readonly Guid Org = Guid.CreateVersion7();
    private static readonly Guid Administrator = Guid.CreateVersion7();
    private static readonly Guid Owner = Guid.CreateVersion7();
    private static readonly Guid Designer = Guid.CreateVersion7();
    private static readonly Guid Member = Guid.CreateVersion7();
    private static readonly Guid Record = Guid.CreateVersion7();
    private static readonly Guid Team = Guid.CreateVersion7();
    private static readonly FieldSearchApprovalGate Synthetic = FieldSearchApprovalGate.Create(FieldSearchApprovalGate.SyntheticMode, "Testing", "TestIdentityProvider");

    private readonly InMemoryAuthorityStore store = new();

    public SecurityAdministratorTests()
    {
        store.Organizations.Add(new OrganizationAuthorizationState { OrganizationId = Org, AuthorizationRevision = 3, ProjectionAppliedRevision = 3 });
        foreach (var user in new[] { Administrator, Owner, Designer, Member })
        {
            store.Bindings.Add(new IdentityBinding { IdentityBindingId = Guid.CreateVersion7(), OrganizationId = Org, AuthorityUserId = user, IdentityIssuerUri = "https://idp", IdentitySubjectIdentifier = user.ToString("N"), IdentityBindingEnabled = true });
        }

        store.Resources.Add(new AuthorityResource { AuthorityResourceId = Guid.CreateVersion7(), OrganizationId = Org, ResourceTypeName = ResourceTypeNames.Organization, ResourceEntityId = Org, ResourceLifecycleState = ResourceLifecycleStates.Active });
        store.Resources.Add(new AuthorityResource { AuthorityResourceId = Guid.CreateVersion7(), OrganizationId = Org, ResourceTypeName = ResourceTypeNames.FormRecord, ResourceEntityId = Record, ResourceLifecycleState = ResourceLifecycleStates.Active });
        store.Resources.Add(new AuthorityResource { AuthorityResourceId = Guid.CreateVersion7(), OrganizationId = Org, ResourceTypeName = ResourceTypeNames.Team, ResourceEntityId = Team, ResourceLifecycleState = ResourceLifecycleStates.Active });
        store.Edges.Add(Edge(ResourceTypeNames.Organization, Org, OrganizationRoleNames.SystemAdministrator, Administrator));
        store.Edges.Add(Edge(ResourceTypeNames.Organization, Org, OrganizationRoleNames.OrganizationOwner, Owner));
        store.Edges.Add(Edge(ResourceTypeNames.Organization, Org, OrganizationRoleNames.Designer, Designer));
        store.Edges.Add(Edge(ResourceTypeNames.FormRecord, Record, "owner", Member));
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static AuthorityRelationshipEdge Edge(string type, Guid id, string relation, Guid user) => new()
    {
        AuthorityRelationshipEdgeId = Guid.CreateVersion7(),
        OrganizationId = Org,
        ResourceTypeName = type,
        ResourceEntityId = id,
        RelationshipName = relation,
        SubjectTypeName = ResourceTypeNames.User,
        SubjectEntityId = user,
        SubjectRelationName = string.Empty,
    };

    private BoundActor Actor(Guid user) => new(new VerifiedActor("https://idp", user.ToString("N"), "test"), store.Bindings.Single(b => b.AuthorityUserId == user));

    private async Task<CommandPlan> PlanAsync(Guid user, SecurityCommandKind kind, ResourceKind resourceKind, Guid resourceId, object payload, FieldSearchApprovalGate? gate = null)
    {
        await using var scope = await store.BeginScopeAsync(Ct);
        var graph = new DesiredStateGraph(store.Edges.Where(e => e.RemovedBySecurityCommandId is null).ToList());
        var request = new SecurityCommandRequest(Org, kind, new ResourceRef(resourceKind, resourceId), 3, JsonSerializer.SerializeToElement(payload, CanonicalJson.Options));
        return await CommandPlanner.PlanAsync(scope, Org, Actor(user), request, graph, Ct, gate ?? Synthetic);
    }

    private Task<CommandPlan> AppointAsync(Guid actor, Guid target, string? stepUp = FieldSearchApprovalGate.SyntheticStepUpEvidence, string role = OrganizationRoleNames.SystemAdministrator, FieldSearchApprovalGate? gate = null) =>
        PlanAsync(actor, SecurityCommandKind.GrantSystemRole, ResourceKind.Organization, Org, new SecurityAdministratorPayload(target, role, "governance approved", stepUp), gate);

    private Task<CommandPlan> RemoveAsync(Guid actor, Guid target) =>
        PlanAsync(actor, SecurityCommandKind.RevokeSystemRole, ResourceKind.Organization, Org, new SecurityAdministratorPayload(target, OrganizationRoleNames.SystemAdministrator, "rotation", FieldSearchApprovalGate.SyntheticStepUpEvidence));

    private static async Task<string> RejectedAsync(Func<Task> action)
    {
        var ex = await Assert.ThrowsAsync<AuthorityException>(action);
        return ex.ErrorCode;
    }

    [Fact]
    public async Task ASecurityAdministratorAppointsAVerifiedMemberAndOnlyThatRelationChanges()
    {
        var plan = await AppointAsync(Administrator, Member);
        var edge = Assert.Single(plan.AddEdges);
        Assert.Equal((ResourceTypeNames.Organization, Org, OrganizationRoleNames.SystemAdministrator, ResourceTypeNames.User, Member), (edge.Item1, edge.Item2, edge.Item3, edge.Item4, edge.Item5));
        Assert.Empty(plan.RemoveEdges);
        Assert.DoesNotContain("synthetic", plan.NormalizedPayloadJson, StringComparison.Ordinal); // step-up evidence is not journaled
    }

    [Fact]
    public async Task OwnersDesignersAndMembersCannotAppointAnyoneIncludingThemselves()
    {
        foreach (var actor in new[] { Owner, Designer, Member })
        {
            Assert.Equal(AuthorityErrorCodes.Forbidden, await RejectedAsync(() => AppointAsync(actor, actor)));
            Assert.Equal(AuthorityErrorCodes.Forbidden, await RejectedAsync(() => AppointAsync(actor, Member)));
        }
    }

    [Fact]
    public async Task OnlyTheSecurityAdministratorRoleOnlyEnabledBindingsOfThisOrganizationAndOnlyWithStepUp()
    {
        Assert.Equal(AuthorityErrorCodes.InvalidCommand, await RejectedAsync(() => AppointAsync(Administrator, Member, role: OrganizationRoleNames.OrganizationOwner)));
        Assert.Equal(AuthorityErrorCodes.InvalidCommand, await RejectedAsync(() => AppointAsync(Administrator, Guid.CreateVersion7()))); // not a principal here (another organization's user included)
        store.Bindings.Single(b => b.AuthorityUserId == Designer).IdentityBindingEnabled = false;
        Assert.Equal(AuthorityErrorCodes.InvalidCommand, await RejectedAsync(() => AppointAsync(Administrator, Designer)));
        Assert.Equal("STEP_UP_REQUIRED", await RejectedAsync(() => AppointAsync(Administrator, Member, stepUp: null)));
        Assert.Equal("STEP_UP_REQUIRED", await RejectedAsync(() => AppointAsync(Administrator, Member, stepUp: "passkey-ok")));
        Assert.Equal("STEP_UP_REQUIRED", await RejectedAsync(() => AppointAsync(Administrator, Member, gate: FieldSearchApprovalGate.Unavailable))); // production/staging
        Assert.Equal(AuthorityErrorCodes.SelfChangeForbidden, await RejectedAsync(() => AppointAsync(Administrator, Administrator))); // explicit A2, no longer only "already one"
    }

    /// <summary>D8-62a (C-109/C-110): self-removal, allowed before C-110, is refused; another administrator must remove.</summary>
    [Fact]
    public async Task NobodyRemovesThemselfAndAnotherAdministratorPerformsTheRemoval()
    {
        Assert.Equal(AuthorityErrorCodes.SelfChangeForbidden, await RejectedAsync(() => RemoveAsync(Administrator, Administrator))); // even as the only one
        store.Edges.Add(Edge(ResourceTypeNames.Organization, Org, OrganizationRoleNames.SystemAdministrator, Member));
        Assert.Equal(AuthorityErrorCodes.SelfChangeForbidden, await RejectedAsync(() => RemoveAsync(Administrator, Administrator))); // still refused with another one present
        Assert.Equal(AuthorityErrorCodes.SelfChangeForbidden, await RejectedAsync(() => RemoveAsync(Member, Member)));
        Assert.Equal(Administrator, Assert.Single((await RemoveAsync(Member, Administrator)).RemoveEdges).SubjectEntityId);
        Assert.Equal(Member, Assert.Single((await RemoveAsync(Administrator, Member)).RemoveEdges).SubjectEntityId);
        Assert.Equal(AuthorityErrorCodes.Forbidden, await RejectedAsync(() => RemoveAsync(Owner, Member)));
        Assert.Equal(AuthorityErrorCodes.InvalidCommand, await RejectedAsync(() => RemoveAsync(Administrator, Designer))); // not an administrator
    }

    /// <summary>
    /// A disabled administrator can be removed. The last-administrator rule stays as defence in depth: with self-removal
    /// refused, the acting administrator always remains, so through the real command path (ActorResolver refuses a disabled
    /// actor) it is unreachable; at planner level it still refuses a removal that would leave only disabled holders.
    /// </summary>
    [Fact]
    public async Task ADisabledAdministratorCanBeRemovedAndTheLastAdministratorRuleRemainsAsDefenceInDepth()
    {
        store.Edges.Add(Edge(ResourceTypeNames.Organization, Org, OrganizationRoleNames.SystemAdministrator, Member));
        store.Bindings.Single(b => b.AuthorityUserId == Member).IdentityBindingEnabled = false;
        Assert.Equal(AuthorityErrorCodes.LastSecurityAdministrator, await RejectedAsync(() => RemoveAsync(Member, Administrator))); // only a disabled one would remain
        Assert.Equal(Member, Assert.Single((await RemoveAsync(Administrator, Member)).RemoveEdges).SubjectEntityId);
    }

    [Fact]
    public async Task AnAdministratorOverrideNeverMakesTheAdministratorOrTheirTeamTheRecordOwner()
    {
        Assert.Equal(AuthorityErrorCodes.Forbidden, await RejectedAsync(() => PlanAsync(Administrator, SecurityCommandKind.TransferOwnership, ResourceKind.FormRecord, Record, new TransferOwnershipPayload(Record, Administrator, null))));
        store.Edges.Add(Edge(ResourceTypeNames.Team, Team, "member", Administrator));
        Assert.Equal(AuthorityErrorCodes.Forbidden, await RejectedAsync(() => PlanAsync(Administrator, SecurityCommandKind.TransferOwnership, ResourceKind.FormRecord, Record, new TransferOwnershipPayload(Record, null, Team))));
        var toOther = await PlanAsync(Administrator, SecurityCommandKind.TransferOwnership, ResourceKind.FormRecord, Record, new TransferOwnershipPayload(Record, Designer, null));
        Assert.Contains(toOther.AddEdges, e => e.Item3 == "owner" && e.Item5 == Designer);
    }

    [Fact]
    public async Task NeitherASecurityAdministratorNorAnOwnerCanJoinATeamOnTheirOwnAuthority()
    {
        Assert.Equal(AuthorityErrorCodes.Forbidden, await RejectedAsync(() => PlanAsync(Administrator, SecurityCommandKind.AddTeamMember, ResourceKind.Team, Team, new TeamMemberPayload(Team, Administrator))));
        Assert.Single((await PlanAsync(Administrator, SecurityCommandKind.AddTeamMember, ResourceKind.Team, Team, new TeamMemberPayload(Team, Member))).AddEdges);
        // OB-F8 (C-104): the former owner self-join is refused, and a Security Administrator cannot add an owner either.
        Assert.Equal(AuthorityErrorCodes.TeamJoinRequiresAnotherOwner, await RejectedAsync(() => PlanAsync(Owner, SecurityCommandKind.AddTeamMember, ResourceKind.Team, Team, new TeamMemberPayload(Team, Owner))));
        Assert.Equal(AuthorityErrorCodes.TeamJoinRequiresAnotherOwner, await RejectedAsync(() => PlanAsync(Administrator, SecurityCommandKind.AddTeamMember, ResourceKind.Team, Team, new TeamMemberPayload(Team, Owner))));
    }

    [Fact]
    public async Task OnlyTheOrganizationOwnerSetsTheEncryptionPolicy()
    {
        Assert.Equal(AuthorityErrorCodes.Forbidden, await RejectedAsync(() => PlanAsync(Administrator, SecurityCommandKind.UpdateMandatoryPolicy, ResourceKind.Organization, Org, new UpdateMandatoryPolicyPayload("Public", true, true, true))));
        Assert.NotNull((await PlanAsync(Owner, SecurityCommandKind.UpdateMandatoryPolicy, ResourceKind.Organization, Org, new UpdateMandatoryPolicyPayload("Internal", true, false, true))).ProtectionPolicy);
    }
}
