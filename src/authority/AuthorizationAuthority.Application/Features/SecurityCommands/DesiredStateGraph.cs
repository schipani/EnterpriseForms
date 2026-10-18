using AuthorizationAuthority.Contracts.V1;
using AuthorizationAuthority.Domain.Features.Organizations;
using AuthorizationAuthority.Domain.Features.SecurityCommands;

namespace AuthorizationAuthority.Application.Features.SecurityCommands;

/// <summary>
/// In-memory view of the organization's active desired-state edges, loaded under the organization lock.
/// Command authorization is computed from this (A6), never from SpiceDB or business-database projections.
/// </summary>
public sealed class DesiredStateGraph(IReadOnlyList<AuthorityRelationshipEdge> activeEdges)
{
    public IReadOnlyList<AuthorityRelationshipEdge> Edges { get; } = activeEdges;

    public bool HasEdge(string resourceType, Guid resourceId, string relation, string subjectType, Guid subjectId, string subjectRelation = "") =>
        Edges.Any(e => e.RemovedBySecurityCommandId is null
                       && e.ResourceTypeName == resourceType && e.ResourceEntityId == resourceId && e.RelationshipName == relation
                       && e.SubjectTypeName == subjectType && e.SubjectEntityId == subjectId && e.SubjectRelationName == subjectRelation);

    public IEnumerable<AuthorityRelationshipEdge> EdgesOf(string resourceType, Guid resourceId, string? relation = null) =>
        Edges.Where(e => e.RemovedBySecurityCommandId is null && e.ResourceTypeName == resourceType && e.ResourceEntityId == resourceId && (relation is null || e.RelationshipName == relation));

    public IEnumerable<AuthorityRelationshipEdge> EdgesWithSubject(string subjectType, Guid subjectId) =>
        Edges.Where(e => e.RemovedBySecurityCommandId is null && e.SubjectTypeName == subjectType && e.SubjectEntityId == subjectId);

    public bool HasOrganizationRole(Guid organizationId, Guid userId, string role) =>
        HasEdge(ResourceTypeNames.Organization, organizationId, role, ResourceTypeNames.User, userId);

    public bool IsTeamMember(Guid userId, Guid teamId) =>
        HasEdge(ResourceTypeNames.Team, teamId, "member", ResourceTypeNames.User, userId);

    /// <summary>Direct user subject, or team#participant subject where the user is a member.</summary>
    public bool SubjectIncludesUser(AuthorityRelationshipEdge edge, Guid userId) =>
        (edge.SubjectTypeName == ResourceTypeNames.User && edge.SubjectEntityId == userId)
        || (edge.SubjectTypeName == ResourceTypeNames.Team && edge.SubjectRelationName == "participant" && IsTeamMember(userId, edge.SubjectEntityId));

    /// <summary>Direct owner edge of the record itself (no parent derivation).</summary>
    public bool IsDirectRecordOwner(Guid userId, Guid formRecordId) =>
        EdgesOf(ResourceTypeNames.FormRecord, formRecordId, "owner").Any(e => SubjectIncludesUser(e, userId));

    /// <summary>Parent record of a child record (B-04), or null for top-level records.</summary>
    public Guid? ParentRecordOf(Guid formRecordId) =>
        EdgesOf(ResourceTypeNames.FormRecord, formRecordId, "parent").Where(e => e.SubjectTypeName == ResourceTypeNames.FormRecord).Select(e => (Guid?)e.SubjectEntityId).FirstOrDefault();

    /// <summary>Effective ownership: a direct owner edge on the record or on any ancestor through parent edges (bounded depth, cycle-safe).</summary>
    public bool IsRecordOwner(Guid userId, Guid formRecordId)
    {
        var current = (Guid?)formRecordId;
        var visited = new HashSet<Guid>();
        for (var depth = 0; current is { } id && depth < 8 && visited.Add(id); depth++)
        {
            if (IsDirectRecordOwner(userId, id))
            {
                return true;
            }

            current = ParentRecordOf(id);
        }

        return false;
    }

    public bool CanCreateRecord(Guid userId, Guid formDefinitionId) =>
        EdgesOf(ResourceTypeNames.FormDefinition, formDefinitionId, "form_user").Any(e => SubjectIncludesUser(e, userId));

    /// <summary>Design permission on one form: a designer edge naming the user or a team they belong to.</summary>
    public bool CanDesignForm(Guid userId, Guid formDefinitionId) =>
        EdgesOf(ResourceTypeNames.FormDefinition, formDefinitionId, "designer").Any(e => SubjectIncludesUser(e, userId));

    public bool CanCreateForm(Guid organizationId, Guid userId) =>
        HasOrganizationRole(organizationId, userId, OrganizationRoleNames.Designer);

    public IEnumerable<Guid> ShareIdsOfRecord(Guid formRecordId) =>
        EdgesOf(ResourceTypeNames.FormRecord, formRecordId, "shared_via").Where(e => e.SubjectTypeName == ResourceTypeNames.RecordShare).Select(e => e.SubjectEntityId);

    /// <summary>Roles the user holds on a share (reader/contributor/manager), through direct or team membership.</summary>
    public IReadOnlySet<string> ShareRolesOf(Guid userId, Guid recordShareId) =>
        EdgesOf(ResourceTypeNames.RecordShare, recordShareId).Where(e => SubjectIncludesUser(e, userId)).Select(e => e.RelationshipName).ToHashSet(StringComparer.Ordinal);

    public IReadOnlySet<Guid> FieldItemsOfShare(Guid recordShareId) =>
        EdgesWithSubject(ResourceTypeNames.RecordShare, recordShareId).Where(e => e.ResourceTypeName == ResourceTypeNames.FieldItem && e.RelationshipName == "shared_via").Select(e => e.ResourceEntityId).ToHashSet();

    /// <summary>Field items the user may edit through shares (contributor/manager), excluding ownership.</summary>
    public IReadOnlySet<Guid> EditableFieldItemsViaShares(Guid userId, Guid formRecordId)
    {
        var items = new HashSet<Guid>();
        foreach (var shareId in ShareIdsOfRecord(formRecordId))
        {
            var roles = ShareRolesOf(userId, shareId);
            if (roles.Contains("contributor") || roles.Contains("manager"))
            {
                items.UnionWith(FieldItemsOfShare(shareId));
            }
        }

        return items;
    }

    public IReadOnlySet<Guid> DelegableFieldItems(Guid userId, Guid formRecordId)
    {
        var items = new HashSet<Guid>();
        foreach (var shareId in ShareIdsOfRecord(formRecordId))
        {
            if (ShareRolesOf(userId, shareId).Contains("manager"))
            {
                items.UnionWith(FieldItemsOfShare(shareId));
            }
        }

        return items;
    }
}
