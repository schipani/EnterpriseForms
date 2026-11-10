using System.Text.RegularExpressions;
using AuthorizationAuthority.Domain.Features.SearchAccess;

namespace AuthorizationAuthority.Application.Features.SearchAccess;

/// <summary>IX-5a (C-147): configuration of the Tier 2 access projection. Off by default; Dark is Development/Testing only.</summary>
public sealed class SearchAccessOptions
{
    public const string SectionName = "SearchIndex:Access";

    /// <summary>Off: nothing is maintained. Dark: maintained and verified, never read by any search path (IX-5a).</summary>
    public SearchAccessMode Mode { get; set; } = SearchAccessMode.Off;

    public int MaintenanceIntervalMilliseconds { get; set; } = 500;

    /// <summary>Journal events consumed per organization per loop pass (one transaction each).</summary>
    public int EventsPerPass { get; set; } = 200;

    /// <summary>Records sampled for the SpiceDB team- and user-level comparison of a verification.</summary>
    public int VerificationSampleSize { get; set; } = 20;

    /// <summary>Upper bound of the effective-owner parent chain (the Authority refuses cycles; deeper is a defect).</summary>
    public int MaximumParentDepth { get; set; } = 16;
}

public enum SearchAccessMode
{
    Off = 0,
    Dark = 1,
}

/// <summary>One relationship tuple of the projection's model (the journal's canonical tuple, typed).</summary>
public sealed record AccessEdge(string ResourceType, Guid ResourceId, string Relation, string SubjectType, Guid SubjectId, string SubjectRelation)
{
    public string Canonical => $"{ResourceType}:{ResourceId:D}#{Relation}@{SubjectType}:{SubjectId:D}" + (SubjectRelation.Length == 0 ? string.Empty : "#" + SubjectRelation);

    public static AccessEdge Of(SearchAccessEdge row) => new(row.ResourceTypeName, row.ResourceEntityId, row.RelationshipName, row.SubjectTypeName, row.SubjectEntityId, row.SubjectRelationName);
}

/// <summary>A parsed journal delta entry. <see cref="Edge"/> is null for tuples the projection does not mirror.</summary>
public sealed record AccessDeltaEntry(bool IsTouch, AccessEdge? Edge);

/// <summary>Typed failure of the projection's integrity contract. Every one quarantines Tier 2 for the organization.</summary>
public sealed class SearchAccessIntegrityException(string reasonCode, string detail) : Exception(detail)
{
    public string ReasonCode { get; } = reasonCode;
}

public static class SearchAccessReasonCodes
{
    public const string JournalGap = "ACCESS_JOURNAL_GAP";
    public const string JournalDigestMismatch = "ACCESS_JOURNAL_DIGEST_MISMATCH";
    public const string JournalChainBreak = "ACCESS_JOURNAL_CHAIN_BREAK";
    public const string JournalEnvelopeInvalid = "ACCESS_JOURNAL_ENVELOPE_INVALID";
    public const string JournalRevisionMismatch = "ACCESS_JOURNAL_REVISION_MISMATCH";
    public const string DeltaMalformed = "ACCESS_DELTA_MALFORMED";
    public const string MirrorDeleteAbsent = "ACCESS_MIRROR_DELETE_ABSENT";
    public const string UnexpectedSubject = "ACCESS_UNEXPECTED_SUBJECT";
    public const string ParentChainTooDeep = "ACCESS_PARENT_CHAIN_TOO_DEEP";
    public const string CheckpointAhead = "ACCESS_CHECKPOINT_AHEAD";
    public const string CheckpointDigestMismatch = "ACCESS_CHECKPOINT_DIGEST_MISMATCH";
    public const string MirrorDiverged = "ACCESS_MIRROR_DIVERGED";
    public const string RowsDiverged = "ACCESS_ROWS_DIVERGED";
    public const string SpiceDbDiverged = "ACCESS_SPICEDB_DIVERGED";
    public const string CompareAndSetLost = "ACCESS_CHECKPOINT_CAS_LOST";
}

/// <summary>Parses the authorization journal's <c>relationship_delta</c> strings (<c>RelationshipTuple.Canonical</c> with an operation).</summary>
public static partial class AccessRelationshipDelta
{
    [GeneratedRegex(@"^(touch|delete) ([a-z_]+):([^#@\s]+)#([a-z_]+)@([a-z_]+):([^#@\s]+)(?:#([a-z_]+))?$", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex EntryPattern();

    /// <summary>The access-relevant edges (zed: form_record owner/parent/shared_via, record_share roles, field_item shared_via, team member).</summary>
    public static bool IsMirrored(string resourceType, string relation) => (resourceType, relation) switch
    {
        ("form_record", "owner" or "parent" or "shared_via") => true,
        ("record_share", "reader" or "contributor" or "manager") => true,
        ("field_item", "shared_via") => true,
        ("team", "member") => true,
        _ => false,
    };

    public static AccessDeltaEntry Parse(string entry)
    {
        var match = EntryPattern().Match(entry ?? string.Empty);
        if (!match.Success)
        {
            throw new SearchAccessIntegrityException(SearchAccessReasonCodes.DeltaMalformed, "unparseable relationship delta entry");
        }

        var resourceType = match.Groups[2].Value;
        var relation = match.Groups[4].Value;
        var touch = match.Groups[1].Value == "touch";
        if (!IsMirrored(resourceType, relation))
        {
            return new AccessDeltaEntry(touch, null);
        }

        if (!Guid.TryParseExact(match.Groups[3].Value, "D", out var resourceId) || !Guid.TryParseExact(match.Groups[6].Value, "D", out var subjectId))
        {
            throw new SearchAccessIntegrityException(SearchAccessReasonCodes.DeltaMalformed, "access edge identifiers must be UUIDs");
        }

        return new AccessDeltaEntry(touch, new AccessEdge(resourceType, resourceId, relation, match.Groups[5].Value, subjectId, match.Groups[7].Success ? match.Groups[7].Value : string.Empty));
    }
}

/// <summary>An in-memory set of access edges with the two lookups the derivation needs.</summary>
public sealed class AccessEdgeSet
{
    private readonly HashSet<AccessEdge> edges = [];
    private readonly Dictionary<(string Type, Guid Id), List<AccessEdge>> byResource = [];
    private readonly Dictionary<(string Type, Guid Id), List<AccessEdge>> bySubject = [];

    public AccessEdgeSet(IEnumerable<AccessEdge> source)
    {
        foreach (var edge in source)
        {
            Add(edge);
        }
    }

    public IReadOnlyCollection<AccessEdge> Edges => edges;

    public bool Add(AccessEdge edge)
    {
        if (!edges.Add(edge))
        {
            return false;
        }

        Index(byResource, (edge.ResourceType, edge.ResourceId), edge);
        Index(bySubject, (edge.SubjectType, edge.SubjectId), edge);
        return true;
    }

    public bool Remove(AccessEdge edge)
    {
        if (!edges.Remove(edge))
        {
            return false;
        }

        byResource[(edge.ResourceType, edge.ResourceId)].Remove(edge);
        bySubject[(edge.SubjectType, edge.SubjectId)].Remove(edge);
        return true;
    }

    public IEnumerable<AccessEdge> Of(string resourceType, Guid resourceId, string relation) =>
        byResource.TryGetValue((resourceType, resourceId), out var list) ? list.Where(e => e.Relation == relation) : [];

    public IEnumerable<AccessEdge> WithSubject(string subjectType, Guid subjectId, string resourceType, string relation) =>
        bySubject.TryGetValue((subjectType, subjectId), out var list) ? list.Where(e => e.ResourceType == resourceType && e.Relation == relation) : [];

    private static void Index(Dictionary<(string, Guid), List<AccessEdge>> index, (string, Guid) key, AccessEdge edge)
    {
        if (!index.TryGetValue(key, out var list))
        {
            index[key] = list = [];
        }

        list.Add(edge);
    }
}

/// <summary>The derived rows of a set of records.</summary>
public sealed record DerivedAccessRows(IReadOnlyList<SearchAccessRecordGrant> RecordGrants, IReadOnlyList<SearchAccessFieldItemGrant> FieldItemGrants);

/// <summary>
/// IX-5a: the pure derivation of principal-first access rows from access edges, mirroring authority.schema.zed:
/// <c>form_record.effective_owner = owner + parent->effective_owner</c>; <c>record_share.read = reader + contributor + manager</c>;
/// <c>field_item.read = parent_record->effective_owner + shared_via->read</c>. Teams are principals (<c>team#participant</c>) and
/// are never expanded (D-1). Owners read every item without per-item rows; shares produce one row per (principal, item, share).
/// </summary>
public static class SearchAccessDerivation
{
    /// <summary>Version of this derivation (checkpoint fencing). Increment on any change of the rows it produces.</summary>
    public const int ProtocolVersion = 1;

    private static readonly string[] ShareRoles = ["reader", "contributor", "manager"];

    public static DerivedAccessRows Derive(Guid organizationId, AccessEdgeSet edges, IEnumerable<Guid> recordIds, long revision, int maximumParentDepth)
    {
        ArgumentNullException.ThrowIfNull(edges);
        var records = new List<SearchAccessRecordGrant>();
        var items = new List<SearchAccessFieldItemGrant>();
        foreach (var record in recordIds.Distinct())
        {
            foreach (var (type, id) in EffectiveOwners(edges, record, maximumParentDepth))
            {
                records.Add(new SearchAccessRecordGrant { SearchAccessRecordGrantId = Guid.CreateVersion7(), OrganizationId = organizationId, AccessPrincipalTypeName = type, AccessPrincipalEntityId = id, FormRecordId = record, AccessKindName = SearchAccessKinds.EffectiveOwner, DerivedAuthorizationRevision = revision });
            }

            foreach (var share in edges.Of("form_record", record, "shared_via").Where(e => e.SubjectType == "record_share" && e.SubjectRelation.Length == 0).Select(e => e.SubjectId).Distinct())
            {
                var fieldItems = edges.WithSubject("record_share", share, "field_item", "shared_via").Where(e => e.SubjectRelation.Length == 0).Select(e => e.ResourceId).Distinct().ToList();
                foreach (var role in ShareRoles)
                {
                    foreach (var (type, id) in edges.Of("record_share", share, role).Select(Principal).Distinct())
                    {
                        records.Add(new SearchAccessRecordGrant { SearchAccessRecordGrantId = Guid.CreateVersion7(), OrganizationId = organizationId, AccessPrincipalTypeName = type, AccessPrincipalEntityId = id, FormRecordId = record, AccessKindName = SearchAccessKinds.ShareRecipient, RecordShareId = share, ShareRoleName = role, DerivedAuthorizationRevision = revision });
                        foreach (var item in fieldItems)
                        {
                            items.Add(new SearchAccessFieldItemGrant { SearchAccessFieldItemGrantId = Guid.CreateVersion7(), OrganizationId = organizationId, AccessPrincipalTypeName = type, AccessPrincipalEntityId = id, FormRecordId = record, ManifestItemId = item, RecordShareId = share, ShareRoleName = role, DerivedAuthorizationRevision = revision });
                        }
                    }
                }
            }
        }

        return new DerivedAccessRows(records, items);
    }

    /// <summary>The records whose rows a delta can change: every record named, the records of every share named, and all descendants.</summary>
    public static HashSet<Guid> AffectedRecords(IEnumerable<AccessEdge> deltaEdges, AccessEdgeSet edges, int maximumParentDepth)
    {
        ArgumentNullException.ThrowIfNull(edges);
        var affected = new HashSet<Guid>();
        foreach (var edge in deltaEdges)
        {
            switch (edge.ResourceType)
            {
                case "form_record":
                    affected.Add(edge.ResourceId);
                    break;
                case "record_share":
                    affected.UnionWith(RecordsOfShare(edges, edge.ResourceId));
                    break;
                case "field_item" when edge.SubjectType == "record_share":
                    affected.UnionWith(RecordsOfShare(edges, edge.SubjectId));
                    break;
            }
        }

        var frontier = affected.ToList();
        for (var depth = 0; frontier.Count > 0; depth++)
        {
            if (depth > maximumParentDepth)
            {
                throw new SearchAccessIntegrityException(SearchAccessReasonCodes.ParentChainTooDeep, "descendant chain exceeds the maximum depth");
            }

            frontier = frontier.SelectMany(r => edges.WithSubject("form_record", r, "form_record", "parent").Select(e => e.ResourceId)).Where(affected.Add).ToList();
        }

        return affected;
    }

    public static IEnumerable<Guid> RecordsOfShare(AccessEdgeSet edges, Guid share) =>
        edges.WithSubject("record_share", share, "form_record", "shared_via").Select(e => e.ResourceId);

    /// <summary>A comparable key of a record row (identifiers are surrogate and excluded).</summary>
    public static string Key(SearchAccessRecordGrant g) => $"{g.AccessPrincipalTypeName}:{g.AccessPrincipalEntityId:D}|{g.FormRecordId:D}|{g.AccessKindName}|{g.RecordShareId:D}|{g.ShareRoleName}";

    public static string Key(SearchAccessFieldItemGrant g) => $"{g.AccessPrincipalTypeName}:{g.AccessPrincipalEntityId:D}|{g.FormRecordId:D}|{g.ManifestItemId:D}|{g.RecordShareId:D}|{g.ShareRoleName}";

    private static HashSet<(string Type, Guid Id)> EffectiveOwners(AccessEdgeSet edges, Guid record, int maximumParentDepth)
    {
        var owners = new HashSet<(string, Guid)>();
        var current = new List<Guid> { record };
        var visited = new HashSet<Guid> { record };
        for (var depth = 0; current.Count > 0; depth++)
        {
            if (depth > maximumParentDepth)
            {
                throw new SearchAccessIntegrityException(SearchAccessReasonCodes.ParentChainTooDeep, "parent chain exceeds the maximum depth");
            }

            var next = new List<Guid>();
            foreach (var r in current)
            {
                owners.UnionWith(edges.Of("form_record", r, "owner").Select(Principal));
                foreach (var parent in edges.Of("form_record", r, "parent"))
                {
                    if (parent.SubjectType != "form_record" || parent.SubjectRelation.Length != 0)
                    {
                        throw new SearchAccessIntegrityException(SearchAccessReasonCodes.UnexpectedSubject, "a parent edge must name a form_record");
                    }

                    if (!visited.Add(parent.SubjectId))
                    {
                        throw new SearchAccessIntegrityException(SearchAccessReasonCodes.ParentChainTooDeep, "parent chain has a cycle");
                    }

                    next.Add(parent.SubjectId);
                }
            }

            current = next;
        }

        return owners;
    }

    private static (string Type, Guid Id) Principal(AccessEdge edge) => (edge.SubjectType, edge.SubjectRelation) switch
    {
        ("user", "") => ("user", edge.SubjectId),
        ("team", "participant") => ("team", edge.SubjectId),
        _ => throw new SearchAccessIntegrityException(SearchAccessReasonCodes.UnexpectedSubject, "an access subject must be a user or a team participant"),
    };
}
