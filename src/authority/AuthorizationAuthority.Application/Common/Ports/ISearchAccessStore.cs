using AuthorizationAuthority.Domain.Features.SearchAccess;

namespace AuthorizationAuthority.Application.Common.Ports;

/// <summary>
/// IX-5a (C-147): the Tier 2 access projection, in the scope's transaction. Schema <c>authority_search_access</c>, granted to the
/// Authority runtime only (never to the search reader); forced row-level security on the transaction's bound organization.
/// </summary>
public interface ISearchAccessStore
{
    /// <summary>Binds the transaction-local organization context the row-level security policies check (defence in depth).</summary>
    Task BindOrganizationAsync(Guid organizationId, CancellationToken cancellationToken);

    /// <summary>SELECT ... FOR UPDATE on the organization's checkpoint (serializes maintainers); untracked.</summary>
    Task<SearchAccessCheckpoint?> FindCheckpointForUpdateAsync(Guid organizationId, CancellationToken cancellationToken);

    Task<SearchAccessCheckpoint?> FindCheckpointAsync(Guid organizationId, CancellationToken cancellationToken);

    /// <summary>Inserts a checkpoint (immediately; the unique organization key refuses a second one).</summary>
    Task InsertCheckpointAsync(SearchAccessCheckpoint checkpoint, CancellationToken cancellationToken);

    /// <summary>
    /// Compare-and-set: updates the checkpoint only if its revision is still <paramref name="expectedCheckpointRevision"/>, and
    /// increments it. Returns false when another writer advanced it (nothing is changed).
    /// </summary>
    Task<bool> TryUpdateCheckpointAsync(Guid organizationId, long expectedCheckpointRevision, SearchAccessCheckpointValues values, DateTimeOffset now, CancellationToken cancellationToken);

    Task<List<SearchAccessEdge>> GetEdgesByResourceAsync(Guid organizationId, string resourceTypeName, IReadOnlyCollection<Guid> resourceEntityIds, CancellationToken cancellationToken);

    Task<List<SearchAccessEdge>> GetEdgesBySubjectAsync(Guid organizationId, string subjectTypeName, IReadOnlyCollection<Guid> subjectEntityIds, string resourceTypeName, string relationshipName, CancellationToken cancellationToken);

    Task<SearchAccessEdge?> FindEdgeAsync(Guid organizationId, string resourceTypeName, Guid resourceEntityId, string relationshipName, string subjectTypeName, Guid subjectEntityId, string subjectRelationName, CancellationToken cancellationToken);

    void AddEdge(SearchAccessEdge edge);

    void RemoveEdge(SearchAccessEdge edge);

    /// <summary>Writes pending adds and removals inside the transaction, so later reads in the scope see them.</summary>
    Task FlushAsync(CancellationToken cancellationToken);

    Task DeleteGrantsOfRecordsAsync(Guid organizationId, IReadOnlyCollection<Guid> formRecordIds, CancellationToken cancellationToken);

    void AddRecordGrants(IEnumerable<SearchAccessRecordGrant> grants);

    void AddFieldItemGrants(IEnumerable<SearchAccessFieldItemGrant> grants);

    /// <summary>Deletes every edge and grant of the organization (rebuild); the checkpoint stays.</summary>
    Task PurgeOrganizationAsync(Guid organizationId, CancellationToken cancellationToken);

    Task<List<SearchAccessEdge>> GetAllEdgesAsync(Guid organizationId, CancellationToken cancellationToken);

    Task<List<SearchAccessRecordGrant>> GetRecordGrantsAsync(Guid organizationId, IReadOnlyCollection<Guid>? formRecordIds, CancellationToken cancellationToken);

    Task<List<SearchAccessFieldItemGrant>> GetFieldItemGrantsAsync(Guid organizationId, IReadOnlyCollection<Guid>? formRecordIds, CancellationToken cancellationToken);

    /// <summary>
    /// IX-5b: the searchable values of accepted content: Tier 1 postings of each record's entry at its accepted head (the entry's
    /// operation equals the head's committed operation) under a Complete coverage of a Public or Internal field. Null records:
    /// the whole organization. Nothing else is ever read: no business data, no encrypted content, no staged candidate.
    /// </summary>
    Task<List<SearchablePosting>> GetSearchablePostingsAsync(Guid organizationId, IReadOnlyCollection<Guid>? formRecordIds, CancellationToken cancellationToken);

    /// <summary>IX-5b: the organization's Complete Public/Internal coverages (the inputs of the coverage digest).</summary>
    Task<List<SearchableCoverage>> GetSearchableCoveragesAsync(Guid organizationId, CancellationToken cancellationToken);

    /// <summary>IX-5b: the Tier 1 projection status (null when the organization has no search projection).</summary>
    Task<string?> GetTier1ProjectionStatusAsync(Guid organizationId, CancellationToken cancellationToken);

    /// <summary>IX-5b: the accepted per-field item counts of the records' Tier 1 entries at head, by (record, approval).</summary>
    Task<Dictionary<(Guid FormRecordId, Guid FieldSearchApprovalId), int>> GetFieldItemCountsAsync(Guid organizationId, IReadOnlyCollection<Guid> formRecordIds, CancellationToken cancellationToken);

    /// <summary>C-150 IX-5c: the form's Tier 1 content revision (0 when the form has no Tier 1 state), for the shadow snapshot equivalence.</summary>
    Task<long> GetTier1FormContentRevisionAsync(Guid organizationId, Guid formDefinitionId, CancellationToken cancellationToken);

    /// <summary>C-150 IX-5c: Tier 1's verified header facts (status, parent) of the records' entries, for Tier 1's metadata filter in the shadow comparison.</summary>
    Task<List<Tier1RecordHeader>> GetTier1RecordHeadersAsync(Guid organizationId, Guid formDefinitionId, IReadOnlyCollection<Guid> formRecordIds, CancellationToken cancellationToken);

    Task DeleteValuePostingsOfRecordsAsync(Guid organizationId, IReadOnlyCollection<Guid>? formRecordIds, CancellationToken cancellationToken);

    void AddValuePostings(IEnumerable<SearchAccessValuePosting> postings);

    Task<List<SearchAccessValuePosting>> GetValuePostingsAsync(Guid organizationId, IReadOnlyCollection<Guid>? formRecordIds, CancellationToken cancellationToken);
}

/// <summary>Every checkpoint column the maintainer sets in one compare-and-set.</summary>
public sealed record SearchAccessCheckpointValues(
    long ConsumedAuthorizationRevision,
    byte[]? ConsumedJournalDigest,
    long ConsumedContentIntegrityRevision,
    byte[]? ConsumedContentJournalDigest,
    byte[]? ConsumedCoverageDigest,
    string StateName,
    string? QuarantineReasonCode,
    int ProtocolVersion)
{
    public static SearchAccessCheckpointValues Of(SearchAccessCheckpoint checkpoint)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);
        return new(checkpoint.ConsumedAuthorizationRevision, checkpoint.ConsumedJournalDigest, checkpoint.ConsumedContentIntegrityRevision, checkpoint.ConsumedContentJournalDigest,
            checkpoint.ConsumedCoverageDigest, checkpoint.AccessProjectionStateName, checkpoint.QuarantineReasonCode, checkpoint.AccessProjectionProtocolVersion);
    }
}

/// <summary>One searchable value of accepted content (a Tier 1 posting at the head under a Complete Public/Internal coverage).</summary>
public sealed record SearchablePosting(
    Guid FormDefinitionId,
    Guid FieldDefinitionId,
    Guid FieldSearchApprovalId,
    Guid FormRecordId,
    Guid ManifestItemId,
    Guid IndexedOperationId,
    string ValueTypeName,
    string? StringValue = null,
    long? IntegerValue = null,
    decimal? DecimalValue = null,
    DateOnly? DateValue = null,
    DateTimeOffset? DateTimeValue = null,
    bool? BooleanValue = null,
    Guid? FieldOptionId = null);

public sealed record SearchableCoverage(Guid FieldSearchApprovalId, Guid FieldDefinitionId, Guid FormDefinitionId);

/// <summary>C-150 IX-5c: the header facts Tier 1 decoded from a record's verified header leaf (no value, no payload).</summary>
public sealed record Tier1RecordHeader(Guid FormRecordId, string RecordStatusName, Guid? ParentFormRecordId);
