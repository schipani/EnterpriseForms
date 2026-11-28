using System.Text.Json;
using AuthorizationAuthority.Application.Common;
using AuthorizationAuthority.Application.Features.SecurityCommands;
using AuthorizationAuthority.Contracts.V1;
using AuthorizationAuthority.Domain.Features.Journal;
using AuthorizationAuthority.Domain.Features.Organizations;
using AuthorizationAuthority.Domain.Features.SecurityCommands;
using AuthorizationAuthority.Domain.Features.Sharing;
using AuthorizationAuthority.Infrastructure.Faults;
using Xunit;

namespace AuthorizationAuthority.Tests;

/// <summary>
/// C10 M1-WP5 (WP-0 Section 3.4): removing a member from a team that gave them a Manager share invalidates, in the same command, the
/// shares that member delegated under it and their subtrees (GrantorPathLost); other members' delegations, a surviving path and
/// independent grants are untouched; the membership payload stays readable for reconciliation.
/// </summary>
public sealed class ShareGrantorPathLossTests
{
    private readonly Guid org = Guid.NewGuid();
    private readonly Guid record = Guid.NewGuid();
    private readonly Guid owner = Guid.NewGuid();
    private readonly Guid team = Guid.NewGuid();
    private readonly Guid leaving = Guid.NewGuid();
    private readonly Guid staying = Guid.NewGuid();
    private readonly Guid recipient = Guid.NewGuid();
    private readonly Guid item = Guid.NewGuid();
    private readonly InMemoryAuthorityStore store = new();
    private readonly Dictionary<Guid, Guid> bindings = [];

    public ShareGrantorPathLossTests()
    {
        store.Organizations.Add(new OrganizationAuthorizationState { OrganizationId = org, AuthorizationRevision = 5, ProjectionAppliedRevision = 5 });
        foreach (var user in new[] { owner, leaving, staying, recipient })
        {
            bindings[user] = Guid.NewGuid();
            store.Bindings.Add(new IdentityBinding { IdentityBindingId = bindings[user], OrganizationId = org, AuthorityUserId = user, IdentityIssuerUri = "https://idp", IdentitySubjectIdentifier = user.ToString("N"), IdentityBindingEnabled = true });
        }

        store.Resources.Add(new AuthorityResource { OrganizationId = org, ResourceEntityId = record, ResourceTypeName = ResourceTypeNames.FormRecord, ResourceLifecycleState = ResourceLifecycleStates.Active });
        store.Resources.Add(new AuthorityResource { OrganizationId = org, ResourceEntityId = team, ResourceTypeName = ResourceTypeNames.Team, ResourceLifecycleState = ResourceLifecycleStates.Active });
        Edge(ResourceTypeNames.Organization, org, OrganizationRoleNames.OrganizationOwner, ResourceTypeNames.User, owner);
        Edge(ResourceTypeNames.FormRecord, record, "owner", ResourceTypeNames.User, owner);
        Edge(ResourceTypeNames.Team, team, "member", ResourceTypeNames.User, leaving);
        Edge(ResourceTypeNames.Team, team, "member", ResourceTypeNames.User, staying);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private Guid Share(Guid grantor, string role, Guid? parent, Guid? teamRecipient = null, Guid? userRecipient = null)
    {
        var id = Guid.NewGuid();
        var command = Guid.NewGuid();
        store.Commands.Add(new SecurityCommand { SecurityCommandId = command, OrganizationId = org, ActorIdentityBindingId = bindings[grantor], CommandKindName = SecurityCommandKinds.CreateShare, CommandStateName = SecurityCommandStates.Effective, CommandIdempotencyKey = "seed-" + id.ToString("N") });
        store.Resources.Add(new AuthorityResource { OrganizationId = org, ResourceEntityId = id, ResourceTypeName = ResourceTypeNames.RecordShare, ParentResourceEntityId = record, ResourceLifecycleState = ResourceLifecycleStates.Active, RegisteredBySecurityCommandId = command });
        var depth = parent is { } p ? store.ShareMetadata.Single(m => m.RecordShareId == p).DelegationDepth + 1 : 1;
        store.ShareMetadata.Add(new ShareResourceMetadata { OrganizationId = org, RecordShareId = id, ParentRecordShareId = parent, DelegationDepth = depth });
        if (teamRecipient is { } t)
        {
            store.Edges.Add(new AuthorityRelationshipEdge { AuthorityRelationshipEdgeId = Guid.NewGuid(), OrganizationId = org, ResourceTypeName = ResourceTypeNames.RecordShare, ResourceEntityId = id, RelationshipName = role, SubjectTypeName = ResourceTypeNames.Team, SubjectEntityId = t, SubjectRelationName = "participant" });
        }
        else
        {
            Edge(ResourceTypeNames.RecordShare, id, role, ResourceTypeNames.User, userRecipient ?? recipient);
        }

        Edge(ResourceTypeNames.FormRecord, record, "shared_via", ResourceTypeNames.RecordShare, id);
        Edge(ResourceTypeNames.FieldItem, item, "shared_via", ResourceTypeNames.RecordShare, id);
        return id;
    }

    private void Edge(string type, Guid id, string relation, string subjectType, Guid subject) =>
        store.Edges.Add(new AuthorityRelationshipEdge { AuthorityRelationshipEdgeId = Guid.NewGuid(), OrganizationId = org, ResourceTypeName = type, ResourceEntityId = id, RelationshipName = relation, SubjectTypeName = subjectType, SubjectEntityId = subject });

    private Task<OperationStatus> RemoveAsync(Guid member)
    {
        var request = new SecurityCommandRequest(org, SecurityCommandKind.RemoveTeamMember, new ResourceRef(ResourceKind.Team, team), store.Organizations.Single().AuthorizationRevision,
            JsonSerializer.SerializeToElement(new TeamMemberPayload(team, member), CanonicalJson.Options));
        return new SecurityCommandService(store, new NoOpFaultInjector(), TimeProvider.System, FieldSearchApprovalGate.Unavailable)
            .SubmitAsync(new VerifiedActor("https://idp", owner.ToString("N"), "test"), request, "remove-member-" + member.ToString("N")[..8], Guid.NewGuid(), Ct);
    }

    private string State(Guid share) => store.Resources.Single(r => r.ResourceEntityId == share).ResourceLifecycleState;

    [Fact]
    public async Task RemovingTheTeamPathInvalidatesOnlyTheLeavingMembersDelegations()
    {
        var teamManager = Share(owner, "manager", null, teamRecipient: team);
        var delegated = Share(leaving, "manager", teamManager, userRecipient: recipient);
        var below = Share(recipient, "reader", delegated);
        var otherMember = Share(staying, "reader", teamManager);
        var independent = Share(owner, "reader", null);

        var status = await RemoveAsync(leaving);

        Assert.Equal(ResourceLifecycleStates.Invalidated, State(delegated));
        Assert.Equal(ResourceLifecycleStates.Invalidated, State(below));
        Assert.Equal(ShareInvalidationReasons.GrantorPathLost, store.ShareMetadata.Single(m => m.RecordShareId == delegated).ShareInvalidationReasonCode);
        Assert.Equal(ResourceLifecycleStates.Active, State(otherMember));
        Assert.Equal(ResourceLifecycleStates.Active, State(teamManager));
        Assert.Equal(ResourceLifecycleStates.Active, State(independent));
        Assert.DoesNotContain(store.Edges, e => (e.ResourceEntityId == delegated || e.SubjectEntityId == delegated) && e.RemovedBySecurityCommandId is null);

        // One command; the membership payload is unchanged, so reconciliation still reads it.
        var command = store.Commands.Single(c => c.SecurityCommandId == status.CommandId);
        Assert.Equal((team, leaving), SecurityCommandOutcomeService.ReadPair(command.CommandPayloadJson));
        var invalidated = store.Journal.Where(j => j.JournalStreamName == JournalStreamNames.Audit && j.JournalEventKind == ShareLifecycleJournal.ShareInvalidated).ToList();
        Assert.Equal(2, invalidated.Count);
        Assert.All(invalidated, j => Assert.Contains(ShareInvalidationReasons.GrantorPathLost, j.JournalEventBodyJson, StringComparison.Ordinal));
    }

    [Fact]
    public async Task ASurvivingDirectPathKeepsTheDelegations()
    {
        var teamManager = Share(owner, "manager", null, teamRecipient: team);
        Edge(ResourceTypeNames.RecordShare, teamManager, "manager", ResourceTypeNames.User, leaving); // also held directly
        var delegated = Share(leaving, "reader", teamManager);

        await RemoveAsync(leaving);

        Assert.Equal(ResourceLifecycleStates.Active, State(delegated));
        Assert.DoesNotContain(store.Journal, j => j.JournalEventKind == ShareLifecycleJournal.ShareInvalidated);
    }
}
