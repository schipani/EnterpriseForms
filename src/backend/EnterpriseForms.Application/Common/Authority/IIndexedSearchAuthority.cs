namespace EnterpriseForms.Application.Common.Authority;

// IX-3 (design §8, §17.3, R1 §E): the business API's view of the Authority's Tier 1 indexed field search. The Authority decides
// everything (population, readability, matches, integrity); the business API passes the validated request, its session digest and
// a per-snapshot random nonce that it alone holds, and receives record ids, verified header facts, counts and revisions.

public sealed record AuthorityIndexedSearchCondition(Guid FieldDefinitionId, string Operator, string ValueType, string? Value, string? LowerBound, string? UpperBound);

public sealed record AuthorityIndexedSearchQuery(Guid FormDefinitionId, IReadOnlyList<AuthorityIndexedSearchCondition> Conditions, string? RecordStatus, bool IncludeChildren, bool IncludeArchived,
    string? SortBy, string? SortDirection, int PageSize);

public sealed record AuthorityIndexedSearchItem(Guid FormRecordId, string RecordStatusName, Guid? ParentFormRecordId, Guid CreatedByBusinessUserId, DateTimeOffset RecordRegisteredOn,
    DateTimeOffset ContentHeadModifiedOn, bool Editable);

public sealed record AuthorityIndexedSearchResult(Guid SearchResultSnapshotId, Guid FormDefinitionId, int TotalCount, int Offset, int PageSize, IReadOnlyList<AuthorityIndexedSearchItem> Items,
    int ScopeRecordCount, int RecordsWithoutContent, long AuthorizationRevision, long ContentIntegrityRevision, long FormContentRevision, DateTimeOffset SnapshotExpiresOn, string ResultBasis, Guid CorrelationId);

public interface IIndexedSearchAuthority
{
    /// <param name="sessionDigestBase64">SHA-256 of the browser session secret (the Authority binds the snapshot to that session).</param>
    /// <param name="queryNonceBase64">32 random bytes chosen for this snapshot; the same value must be presented for every page.</param>
    Task<AuthorityIndexedSearchResult> SearchAsync(DelegatedUserContext user, string sessionDigestBase64, string queryNonceBase64, AuthorityIndexedSearchQuery query, CancellationToken cancellationToken);

    Task<AuthorityIndexedSearchResult> PageAsync(DelegatedUserContext user, Guid searchResultSnapshotId, string sessionDigestBase64, string queryNonceBase64, AuthorityIndexedSearchQuery query, int offset, CancellationToken cancellationToken);
}
