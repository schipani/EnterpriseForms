namespace AuthorizationAuthority.Contracts.V1;

// IX-3 (design §8, §17.3, §18.4, R1 §E): the Tier 1 indexed field search of the Authority. Private contract (business workload
// credential + delegated user assertion). Record ids, header facts, counts and revisions cross it; field values never do in a
// response. The request carries the caller's predicate values (needed to evaluate), which the Authority never stores or logs:
// a result snapshot keeps only record ids and a digest over a per-snapshot random nonce that the business API alone holds.

/// <summary>One predicate, exactly as the business API validated it (the Authority validates it again, never trusts it).</summary>
public sealed record IndexedSearchCondition(Guid FieldDefinitionId, string Operator, string ValueType, string? Value, string? LowerBound, string? UpperBound);

/// <summary>The query: one form, 1..5 AND conditions, the metadata filters and sort of Advanced Field Search, a page size.</summary>
public sealed record IndexedSearchQuery(
    Guid FormDefinitionId,
    IReadOnlyList<IndexedSearchCondition> Conditions,
    string? RecordStatus,
    bool IncludeChildren,
    bool IncludeArchived,
    string? SortBy,
    string? SortDirection,
    int PageSize);

/// <summary>
/// A new indexed search. <c>SessionDigestBase64</c> binds the result snapshot to the caller's Authority session (§18.4);
/// <c>QueryNonceBase64</c> is 32 random bytes chosen by the business API for this snapshot (§17.3).
/// </summary>
public sealed record IndexedSearchRequest(Guid OrganizationId, string SessionDigestBase64, string QueryNonceBase64, IndexedSearchQuery Query);

/// <summary>Another page of an existing snapshot: the same query and nonce must be presented (the digest is recomputed and compared).</summary>
public sealed record IndexedSearchPageRequest(Guid OrganizationId, string SessionDigestBase64, string QueryNonceBase64, IndexedSearchQuery Query, int Offset);

/// <summary>One authorized match: verified header facts and Authority-held times (sort keys), plus a fresh edit decision.</summary>
public sealed record IndexedSearchItem(
    Guid FormRecordId,
    string RecordStatusName,
    Guid? ParentFormRecordId,
    Guid CreatedByBusinessUserId,
    DateTimeOffset RecordRegisteredOn,
    DateTimeOffset ContentHeadModifiedOn,
    bool Editable);

/// <summary>
/// A page of an indexed result. <c>TotalCount</c> is the exact number of authorized matches at the snapshot (never unauthorized
/// ones). <c>ScopeRecordCount</c> is the number of the caller's discoverable records of the form that were in scope; records
/// without accepted content are counted separately (they cannot match). <c>ResultBasis</c> is always
/// <c>AuthorityAcceptedContent</c>: results reflect content the Authority accepted, not a verification of the business copy.
/// </summary>
public sealed record IndexedSearchResponse(
    Guid SearchResultSnapshotId,
    Guid FormDefinitionId,
    int TotalCount,
    int Offset,
    int PageSize,
    IReadOnlyList<IndexedSearchItem> Items,
    int ScopeRecordCount,
    int RecordsWithoutContent,
    long AuthorizationRevision,
    long ContentIntegrityRevision,
    long FormContentRevision,
    DateTimeOffset SnapshotExpiresOn,
    string ResultBasis,
    Guid CorrelationId);

/// <summary>Error codes of the indexed search (problem details <c>title</c>), in addition to the generic Authority codes.</summary>
public static class IndexedSearchErrorCodes
{
    /// <summary>422: a condition names a field, operator, value type or option the caller may not search (indistinguishable, as V1).</summary>
    public const string FieldNotAllowed = "SEARCH_FIELD_NOT_ALLOWED";

    /// <summary>422: the operator, or an option value, is not approved for the field.</summary>
    public const string OperatorNotSupported = "SEARCH_OPERATOR_NOT_SUPPORTED";

    /// <summary>422: a caller-owned quantity exceeds a limit (discoverable records, share-only items, results) or the time budget.</summary>
    public const string VerificationLimit = "SEARCH_VERIFICATION_LIMIT";

    /// <summary>409: authorization, content or searchability changed (during the search or since the snapshot): run it again.</summary>
    public const string SnapshotChanged = "SEARCH_SNAPSHOT_CHANGED";

    /// <summary>503 (retryable): a coverage is still Building, the projection is Rebuilding or ReconciliationRequired, or the query path is not configured.</summary>
    public const string IndexNotReady = "SEARCH_INDEX_NOT_READY";

    /// <summary>503: the search index is quarantined (an integrity check failed); never 423 (that is the organization quarantine).</summary>
    public const string IndexInconsistent = "SEARCH_INDEX_INCONSISTENT";

    /// <summary>503: an overdue verification scope suspended the index (§17.10).</summary>
    public const string IndexVerificationOverdue = "SEARCH_INDEX_VERIFICATION_OVERDUE";

    /// <summary>503: a record in scope (or on the page) is quarantined (D3 parity).</summary>
    public const string RecordQuarantined = "RECORD_QUARANTINED";

    /// <summary>422: an approved predicate field holds an encrypted value on a record in scope (C7 parity).</summary>
    public const string PredicateValueEncrypted = "PREDICATE_VALUE_ENCRYPTED";

    /// <summary>503 (retryable): the instance-wide stored-result capacity is exhausted (aggregate load only, §17.3).</summary>
    public const string CapacityExceeded = "SEARCH_CAPACITY_EXCEEDED";

    /// <summary>401: the bound session is unknown, revoked, expired or idle.</summary>
    public const string SessionInvalid = "SEARCH_SESSION_INVALID";

    /// <summary>404: no such snapshot for this caller, session and query.</summary>
    public const string SnapshotNotFound = "SEARCH_SNAPSHOT_NOT_FOUND";

    public const string ResultBasisAuthorityAcceptedContent = "AuthorityAcceptedContent";
}
