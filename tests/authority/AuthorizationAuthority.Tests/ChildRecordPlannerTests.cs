using System.Text.Json;
using AuthorizationAuthority.Application.Common;
using AuthorizationAuthority.Application.Features.SecurityCommands;
using AuthorizationAuthority.Contracts.V1;
using AuthorizationAuthority.Domain.Features.Organizations;
using AuthorizationAuthority.Domain.Features.SecurityCommands;
using Xunit;

namespace AuthorizationAuthority.Tests;

/// <summary>B-04: child records derive ownership from the parent; only the parent's effective owner may create or unlink a child; cycles and self-parenting are rejected.</summary>
public sealed class ChildRecordPlannerTests
{
    private static readonly Guid Org = Guid.CreateVersion7();
    private static readonly Guid OwnerUser = Guid.CreateVersion7();
    private static readonly Guid OtherUser = Guid.CreateVersion7();
    private static readonly Guid Parent = Guid.CreateVersion7();
    private static readonly Guid Child = Guid.CreateVersion7();
    private static readonly Guid Grandchild = Guid.CreateVersion7();

    [Fact]
    public void EffectiveOwnershipFollowsTheParentChainButNotSharesOrUnrelatedRecords()
    {
        var graph = new DesiredStateGraph(
        [
            Edge(Parent, "owner", ResourceTypeNames.User, OwnerUser),
            Edge(Child, "parent", ResourceTypeNames.FormRecord, Parent),
            Edge(Grandchild, "parent", ResourceTypeNames.FormRecord, Child),
        ]);
        Assert.True(graph.IsRecordOwner(OwnerUser, Parent));
        Assert.True(graph.IsRecordOwner(OwnerUser, Child));
        Assert.True(graph.IsRecordOwner(OwnerUser, Grandchild));
        Assert.False(graph.IsDirectRecordOwner(OwnerUser, Child));
        Assert.False(graph.IsRecordOwner(OtherUser, Child));
        Assert.Equal(Parent, graph.ParentRecordOf(Child));
        Assert.Null(graph.ParentRecordOf(Parent));
    }

    [Fact]
    public void ParentCyclesTerminate()
    {
        var graph = new DesiredStateGraph(
        [
            Edge(Parent, "parent", ResourceTypeNames.FormRecord, Child),
            Edge(Child, "parent", ResourceTypeNames.FormRecord, Parent),
        ]);
        Assert.False(graph.IsRecordOwner(OwnerUser, Child));
    }

    [Fact]
    public void RegisterRecordPayloadCarriesTheOptionalParentBinding()
    {
        var payload = CanonicalJson.Deserialize<RegisterRecordPayload>(JsonSerializer.SerializeToElement(new { form_record_id = Child, form_definition_id = Guid.CreateVersion7(), owner_team_id = (Guid?)null, parent_form_record_id = Parent, form_relationship_definition_id = Guid.CreateVersion7() }));
        Assert.Equal(Parent, payload.ParentFormRecordId);
        Assert.NotNull(payload.FormRelationshipDefinitionId);
        var legacy = CanonicalJson.Deserialize<RegisterRecordPayload>(JsonSerializer.SerializeToElement(new { form_record_id = Child, form_definition_id = Guid.CreateVersion7(), owner_team_id = (Guid?)null }));
        Assert.Null(legacy.ParentFormRecordId);
    }

    private static AuthorityRelationshipEdge Edge(Guid resourceId, string relation, string subjectType, Guid subjectId) => new()
    {
        AuthorityRelationshipEdgeId = Guid.CreateVersion7(),
        OrganizationId = Org,
        ResourceTypeName = ResourceTypeNames.FormRecord,
        ResourceEntityId = resourceId,
        RelationshipName = relation,
        SubjectTypeName = subjectType,
        SubjectEntityId = subjectId,
        SubjectRelationName = string.Empty,
    };
}
