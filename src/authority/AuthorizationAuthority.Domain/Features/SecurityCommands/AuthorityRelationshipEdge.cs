namespace AuthorizationAuthority.Domain.Features.SecurityCommands;

/// <summary>Normalized desired security state. Written only by the command service inside the organization transaction; SpiceDB is a projection of these rows.</summary>
public sealed class AuthorityRelationshipEdge
{
    public Guid AuthorityRelationshipEdgeId { get; set; }

    public Guid OrganizationId { get; set; }

    public string ResourceTypeName { get; set; } = string.Empty;

    public Guid ResourceEntityId { get; set; }

    public string RelationshipName { get; set; } = string.Empty;

    public string SubjectTypeName { get; set; } = string.Empty;

    public Guid SubjectEntityId { get; set; }

    public string SubjectRelationName { get; set; } = string.Empty;

    public Guid CreatedBySecurityCommandId { get; set; }

    public DateTimeOffset RelationshipCreatedOn { get; set; }

    /// <summary>Set when a later command removes the edge; the row is retained for history (append-only journal semantics).</summary>
    public Guid? RemovedBySecurityCommandId { get; set; }

    public DateTimeOffset? RelationshipRemovedOn { get; set; }

    public RelationshipTuple ToTuple() => new(ResourceTypeName, ResourceEntityId.ToString("D"), RelationshipName, SubjectTypeName, SubjectEntityId.ToString("D"), SubjectRelationName);
}

/// <summary>One relationship tuple as projected to SpiceDB.</summary>
public sealed record RelationshipTuple(string ResourceType, string ResourceId, string Relation, string SubjectType, string SubjectId, string SubjectRelation)
{
    public string Canonical => $"{ResourceType}:{ResourceId}#{Relation}@{SubjectType}:{SubjectId}" + (SubjectRelation.Length == 0 ? string.Empty : "#" + SubjectRelation);
}

/// <summary>Exact projection delta entry, persisted in the outbox and applied verbatim.</summary>
public sealed record RelationshipDeltaEntry(string Operation, RelationshipTuple Tuple);

public static class RelationshipDeltaOperations
{
    public const string Touch = "touch";
    public const string Delete = "delete";
}
