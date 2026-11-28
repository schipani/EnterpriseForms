using System.Text.Json;
using AuthorizationAuthority.Application.Common;
using AuthorizationAuthority.Application.Features.SecurityCommands;
using AuthorizationAuthority.Contracts.V1;
using AuthorizationAuthority.Domain.Features.Organizations;
using AuthorizationAuthority.Domain.Features.SecurityCommands;
using AuthorizationAuthority.Infrastructure.Faults;
using Xunit;

namespace AuthorizationAuthority.Tests;

/// <summary>
/// C10 child-record ownership (owner decision 2026-11-27; CLAUDE.md §4B single owner, §4S inheritance; WP-0 §3.4.1, §3.7): a record
/// with a parent is owned only through its parent and is never transferred on its own, by its effective owner or by an administrator
/// override. The reported retention sequence (transfer the child to oneself, then transfer the parent away) is refused at its first
/// step, so the former parent owner keeps nothing; transferring the parent moves the child; a share on the child is unaffected.
/// Driven through <see cref="SecurityCommandService"/> on the in-memory store.
/// </summary>
public sealed class ChildRecordOwnershipTransferTests
{
    private static readonly HumanCommandStepUpGate Gate = HumanCommandStepUpGate.Create(HumanCommandStepUpGate.SyntheticMode, "Testing", "TestIdentityProvider");
    private readonly Guid org = Guid.NewGuid();
    private readonly Guid parent = Guid.NewGuid();
    private readonly Guid child = Guid.NewGuid();
    private readonly Guid owner = Guid.NewGuid();
    private readonly Guid successor = Guid.NewGuid();
    private readonly Guid administrator = Guid.NewGuid();
    private readonly Guid recipient = Guid.NewGuid();
    private readonly Guid team = Guid.NewGuid();
    private readonly InMemoryAuthorityStore store = new();

    public ChildRecordOwnershipTransferTests()
    {
        store.Organizations.Add(new OrganizationAuthorizationState { OrganizationId = org, AuthorizationRevision = 5, ProjectionAppliedRevision = 5 });
        foreach (var user in new[] { owner, successor, administrator, recipient })
        {
            store.Bindings.Add(new IdentityBinding { IdentityBindingId = Guid.NewGuid(), OrganizationId = org, AuthorityUserId = user, IdentityIssuerUri = "https://idp", IdentitySubjectIdentifier = user.ToString("N"), IdentityBindingEnabled = true });
        }

        foreach (var (id, type) in new[] { (parent, ResourceTypeNames.FormRecord), (child, ResourceTypeNames.FormRecord), (team, ResourceTypeNames.Team) })
        {
            store.Resources.Add(new AuthorityResource { OrganizationId = org, ResourceEntityId = id, ResourceTypeName = type, ResourceLifecycleState = ResourceLifecycleStates.Active });
        }

        Edge(ResourceTypeNames.FormRecord, parent, "owner", ResourceTypeNames.User, owner);
        Edge(ResourceTypeNames.FormRecord, child, "parent", ResourceTypeNames.FormRecord, parent);
        Edge(ResourceTypeNames.Team, team, "member", ResourceTypeNames.User, owner);
        Edge(ResourceTypeNames.Organization, org, OrganizationRoleNames.SystemAdministrator, ResourceTypeNames.User, administrator);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private DesiredStateGraph Graph => new(store.Edges.Where(e => e.RelationshipRemovedOn is null).ToList());

    private void Edge(string type, Guid id, string relation, string subjectType, Guid subject, string subjectRelation = "") =>
        store.Edges.Add(new AuthorityRelationshipEdge { AuthorityRelationshipEdgeId = Guid.NewGuid(), OrganizationId = org, ResourceTypeName = type, ResourceEntityId = id, RelationshipName = relation, SubjectTypeName = subjectType, SubjectEntityId = subject, SubjectRelationName = subjectRelation });

    private Task<OperationStatus> TransferAsync(Guid actor, Guid record, Guid? toUser, Guid? toTeam = null) =>
        new SecurityCommandService(store, new NoOpFaultInjector(), TimeProvider.System, FieldSearchApprovalGate.Unavailable, humanCommandGate: Gate).SubmitAsync(
            new VerifiedActor("https://idp", actor.ToString("N"), "test"),
            new SecurityCommandRequest(org, SecurityCommandKind.TransferOwnership, new ResourceRef(ResourceKind.FormRecord, record), store.Organizations.Single().AuthorizationRevision,
                JsonSerializer.SerializeToElement(new TransferOwnershipPayload(record, toUser, toTeam, HumanCommandStepUpGate.SyntheticStepUpEvidence), CanonicalJson.Options)),
            "transfer-" + Guid.NewGuid().ToString("N")[..12], Guid.NewGuid(), Ct);

    private async Task RefusedAsync(Guid actor, Guid? toUser, Guid? toTeam = null)
    {
        var before = (store.Edges.Count, store.Organizations.Single().AuthorizationRevision);
        var commands = store.Commands.Count;
        var refused = await Assert.ThrowsAsync<AuthorityException>(() => TransferAsync(actor, child, toUser, toTeam));
        Assert.Equal((AuthorityErrorCodes.ChildRecordOwnershipFollowsParent, 422), (refused.ErrorCode, refused.HttpStatusCode));
        Assert.Equal(before, (store.Edges.Count, store.Organizations.Single().AuthorizationRevision)); // nothing committed
        Assert.All(store.Commands.Skip(commands), c => Assert.Equal(SecurityCommandStates.Rejected, c.CommandStateName)); // only the rejection is recorded
        Assert.DoesNotContain(store.Edges, e => e.ResourceEntityId == child && e.RelationshipName == "owner");
    }

    [Fact]
    public async Task TheReportedRetentionSequenceIsRefusedAtItsFirstStepAndTheParentTransferMovesTheChild()
    {
        await RefusedAsync(owner, owner);          // 1. the parent owner transfers the child to themself
        await RefusedAsync(owner, null, team);     //    or to a team they belong to
        await TransferAsync(owner, parent, successor); // 2. then transfers the parent away

        Assert.False(Graph.IsRecordOwner(owner, child)); // nothing retained
        Assert.True(Graph.IsRecordOwner(successor, child));
        Assert.Single(Graph.EdgesOf(ResourceTypeNames.FormRecord, child, "parent"));
    }

    [Fact]
    public async Task AnOwnerAndAnAdministratorOverrideAreBothRefusedForAChildRecord()
    {
        await RefusedAsync(owner, successor);
        await RefusedAsync(administrator, successor);
        Assert.True(Graph.IsRecordOwner(owner, child));
    }

    [Fact]
    public async Task AShareOnTheChildSurvivesTheParentTransferAndTopLevelTransfersAreUnchanged()
    {
        var share = Guid.NewGuid();
        store.Resources.Add(new AuthorityResource { OrganizationId = org, ResourceEntityId = share, ResourceTypeName = ResourceTypeNames.RecordShare, ParentResourceEntityId = child, ResourceLifecycleState = ResourceLifecycleStates.Active });
        Edge(ResourceTypeNames.FormRecord, child, "shared_via", ResourceTypeNames.RecordShare, share);
        Edge(ResourceTypeNames.RecordShare, share, "reader", ResourceTypeNames.User, recipient);

        await TransferAsync(owner, parent, successor);
        await TransferAsync(administrator, parent, recipient); // the audited override on a top-level record still works

        Assert.Contains(Graph.EdgesOf(ResourceTypeNames.FormRecord, child, "shared_via"), e => e.SubjectEntityId == share);
        Assert.Contains(Graph.EdgesOf(ResourceTypeNames.RecordShare, share, "reader"), e => e.SubjectEntityId == recipient);
        Assert.True(Graph.IsRecordOwner(recipient, child));
        Assert.False(Graph.IsRecordOwner(successor, child));
    }
}
