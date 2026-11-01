using System.Text.Json;
using AuthorizationAuthority.Application.Common;
using AuthorizationAuthority.Application.Features.SecurityCommands;
using AuthorizationAuthority.Contracts.V1;
using AuthorizationAuthority.Domain.Features.Organizations;
using AuthorizationAuthority.Domain.Features.SecurityCommands;
using Xunit;

namespace AuthorizationAuthority.Tests;

/// <summary>
/// OB-F8 (C-104) at unit level: an organization owner can no longer reach team-owned records on their own authority. Self-joining a
/// team is refused for every role (owner, Security Administrator, owner who is also a Security Administrator); adding an owner to a team
/// needs a different organization owner; leaving a team and adding non-owners are unchanged; the administrator ownership-transfer
/// override still cannot move a record to the administrator's own team. Team membership is the only command that writes team member
/// edges, so these planner rules cover every route (business API, alternative clients). The Docker path is OwnerTeamJoinStackTests.
/// </summary>
public sealed class OwnerTeamJoinTests
{
    private static readonly Guid Org = Guid.CreateVersion7();
    private static readonly Guid OwnerA = Guid.CreateVersion7();
    private static readonly Guid OwnerB = Guid.CreateVersion7();
    private static readonly Guid OwnerAndAdministrator = Guid.CreateVersion7();
    private static readonly Guid Administrator = Guid.CreateVersion7();
    private static readonly Guid Member = Guid.CreateVersion7();
    private static readonly Guid Team = Guid.CreateVersion7();
    private static readonly Guid TeamRecord = Guid.CreateVersion7();
    private static readonly FieldSearchApprovalGate Synthetic = FieldSearchApprovalGate.Create(FieldSearchApprovalGate.SyntheticMode, "Testing", "TestIdentityProvider");

    private readonly InMemoryAuthorityStore store = new();

    public OwnerTeamJoinTests()
    {
        store.Organizations.Add(new OrganizationAuthorizationState { OrganizationId = Org, AuthorizationRevision = 3, ProjectionAppliedRevision = 3 });
        foreach (var user in new[] { OwnerA, OwnerB, OwnerAndAdministrator, Administrator, Member })
        {
            store.Bindings.Add(new IdentityBinding { IdentityBindingId = Guid.CreateVersion7(), OrganizationId = Org, AuthorityUserId = user, IdentityIssuerUri = "https://idp", IdentitySubjectIdentifier = user.ToString("N"), IdentityBindingEnabled = true });
        }

        store.Resources.Add(new AuthorityResource { AuthorityResourceId = Guid.CreateVersion7(), OrganizationId = Org, ResourceTypeName = ResourceTypeNames.Team, ResourceEntityId = Team, ResourceLifecycleState = ResourceLifecycleStates.Active });
        store.Resources.Add(new AuthorityResource { AuthorityResourceId = Guid.CreateVersion7(), OrganizationId = Org, ResourceTypeName = ResourceTypeNames.FormRecord, ResourceEntityId = TeamRecord, ResourceLifecycleState = ResourceLifecycleStates.Active });
        foreach (var owner in new[] { OwnerA, OwnerB, OwnerAndAdministrator })
        {
            store.Edges.Add(Edge(ResourceTypeNames.Organization, Org, OrganizationRoleNames.OrganizationOwner, ResourceTypeNames.User, owner));
        }

        store.Edges.Add(Edge(ResourceTypeNames.Organization, Org, OrganizationRoleNames.SystemAdministrator, ResourceTypeNames.User, OwnerAndAdministrator));
        store.Edges.Add(Edge(ResourceTypeNames.Organization, Org, OrganizationRoleNames.SystemAdministrator, ResourceTypeNames.User, Administrator));
        // The team owns a record: membership would grant full owner access to it.
        store.Edges.Add(Edge(ResourceTypeNames.FormRecord, TeamRecord, "owner", ResourceTypeNames.Team, Team, "participant"));
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static AuthorityRelationshipEdge Edge(string type, Guid id, string relation, string subjectType, Guid subject, string subjectRelation = "") => new()
    {
        AuthorityRelationshipEdgeId = Guid.CreateVersion7(),
        OrganizationId = Org,
        ResourceTypeName = type,
        ResourceEntityId = id,
        RelationshipName = relation,
        SubjectTypeName = subjectType,
        SubjectEntityId = subject,
        SubjectRelationName = subjectRelation,
    };

    private BoundActor Actor(Guid user) => new(new VerifiedActor("https://idp", user.ToString("N"), "test"), store.Bindings.Single(b => b.AuthorityUserId == user));

    private async Task<CommandPlan> PlanAsync(Guid user, SecurityCommandKind kind, ResourceKind resourceKind, Guid resourceId, object payload)
    {
        await using var scope = await store.BeginScopeAsync(Ct);
        var graph = new DesiredStateGraph(store.Edges.Where(e => e.RemovedBySecurityCommandId is null).ToList());
        var request = new SecurityCommandRequest(Org, kind, new ResourceRef(resourceKind, resourceId), 3, JsonSerializer.SerializeToElement(payload, CanonicalJson.Options));
        return await CommandPlanner.PlanAsync(scope, Org, Actor(user), request, graph, Ct, Synthetic);
    }

    private Task<CommandPlan> AddAsync(Guid actor, Guid member) => PlanAsync(actor, SecurityCommandKind.AddTeamMember, ResourceKind.Team, Team, new TeamMemberPayload(Team, member));

    private static async Task<string> RejectedAsync(Func<Task> action) => (await Assert.ThrowsAsync<AuthorityException>(action)).ErrorCode;

    private bool CanReachTeamRecord(Guid user, CommandPlan? applied = null)
    {
        var edges = store.Edges.ToList();
        if (applied is not null)
        {
            edges.AddRange(applied.AddEdges.Select(e => Edge(e.ResourceType, e.ResourceId, e.Relation, e.SubjectType, e.SubjectId, e.SubjectRelation)));
        }

        return new DesiredStateGraph(edges).IsRecordOwner(user, TeamRecord);
    }

    [Fact]
    public async Task AnOwnerCannotJoinATeamOnTheirOwnAuthorityAndSoCannotReachItsRecords()
    {
        Assert.False(CanReachTeamRecord(OwnerA));
        Assert.Equal(AuthorityErrorCodes.TeamJoinRequiresAnotherOwner, await RejectedAsync(() => AddAsync(OwnerA, OwnerA)));
        Assert.Equal(AuthorityErrorCodes.TeamJoinRequiresAnotherOwner, await RejectedAsync(() => AddAsync(OwnerAndAdministrator, OwnerAndAdministrator)));
        Assert.Equal(AuthorityErrorCodes.Forbidden, await RejectedAsync(() => AddAsync(Administrator, Administrator)));
        Assert.False(CanReachTeamRecord(OwnerA));
    }

    [Fact]
    public async Task OnlyADifferentOwnerCanAddAnOwnerToATeam()
    {
        Assert.Equal(AuthorityErrorCodes.TeamJoinRequiresAnotherOwner, await RejectedAsync(() => AddAsync(Administrator, OwnerA)));
        var plan = await AddAsync(OwnerB, OwnerA); // the second, independent owner decides
        Assert.Single(plan.AddEdges);
        Assert.True(CanReachTeamRecord(OwnerA, plan));
        Assert.Single((await AddAsync(OwnerAndAdministrator, OwnerB)).AddEdges); // an owner who is also a Security Administrator acts as an owner for others
    }

    [Fact]
    public async Task AddingNonOwnersAndLeavingATeamAreUnchanged()
    {
        Assert.Single((await AddAsync(OwnerA, Member)).AddEdges);
        Assert.Single((await AddAsync(Administrator, Member)).AddEdges);
        store.Edges.Add(Edge(ResourceTypeNames.Team, Team, "member", ResourceTypeNames.User, OwnerA));
        Assert.Single((await PlanAsync(OwnerA, SecurityCommandKind.RemoveTeamMember, ResourceKind.Team, Team, new TeamMemberPayload(Team, OwnerA))).RemoveEdges);
    }

    [Fact]
    public async Task TheAdministratorTransferOverrideStillCannotTargetTheAdministratorsOwnTeam()
    {
        store.Edges.Add(Edge(ResourceTypeNames.Team, Team, "member", ResourceTypeNames.User, Administrator));
        var otherRecord = Guid.CreateVersion7();
        store.Resources.Add(new AuthorityResource { AuthorityResourceId = Guid.CreateVersion7(), OrganizationId = Org, ResourceTypeName = ResourceTypeNames.FormRecord, ResourceEntityId = otherRecord, ResourceLifecycleState = ResourceLifecycleStates.Active });
        store.Edges.Add(Edge(ResourceTypeNames.FormRecord, otherRecord, "owner", ResourceTypeNames.User, Member));
        Assert.Equal(AuthorityErrorCodes.Forbidden, await RejectedAsync(() => PlanAsync(Administrator, SecurityCommandKind.TransferOwnership, ResourceKind.FormRecord, otherRecord, new TransferOwnershipPayload(otherRecord, null, Team))));
    }
}
