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
    Task<bool> TryUpdateCheckpointAsync(Guid organizationId, long expectedCheckpointRevision, long consumedAuthorizationRevision, byte[]? consumedJournalDigest, string stateName, string? quarantineReasonCode, int protocolVersion, DateTimeOffset now, CancellationToken cancellationToken);

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
}
