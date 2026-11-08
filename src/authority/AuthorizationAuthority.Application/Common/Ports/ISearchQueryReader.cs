using AuthorizationAuthority.Application.Features.SearchIndex;
using AuthorizationAuthority.Domain.Features.FieldSearch;
using AuthorizationAuthority.Domain.Features.SearchIndex;

namespace AuthorizationAuthority.Application.Common.Ports;

/// <summary>
/// IX-3 (design §5.1, §8): the read side of the indexed query path, on the SELECT-only <c>authority_search_reader</c> login,
/// never the runtime credential. One session is one READ ONLY, REPEATABLE READ transaction: every fact a search or page uses
/// (scope guard, candidates, binding data) is read at one AuthorityDb snapshot. Parameterized SQL from fixed templates only.
/// </summary>
public interface ISearchQueryReader
{
    /// <summary>False when no reader connection is configured: the query path then fails closed (SEARCH_INDEX_NOT_READY).</summary>
    bool IsConfigured { get; }

    Task<ISearchQuerySession> BeginAsync(Guid organizationId, CancellationToken cancellationToken);
}

public interface ISearchQuerySession : IAsyncDisposable
{
    /// <summary>Organization, projection, form state, coverages and approvals of the form, at the session's snapshot.</summary>
    Task<SearchQueryStateFacts> ReadStateAsync(Guid formDefinitionId, CancellationToken cancellationToken);

    /// <summary>
    /// The scope guard input (I-1): for each discoverable record id that is registered as a record of the form (Active or
    /// Quarantined), its registry state, head and entry facts, and whether an approved predicate field carries an encryption anomaly.
    /// </summary>
    Task<IReadOnlyList<SearchScopeRecordFacts>> ReadScopeAsync(Guid formDefinitionId, IReadOnlyCollection<Guid> discoverableRecordIds, IReadOnlyCollection<Guid> predicateFieldIds, CancellationToken cancellationToken);

    /// <summary>Posting item ids of the predicate fields for the given records (R1 §E.3 step 4): ids only, never values.</summary>
    Task<IReadOnlyList<(Guid FormRecordId, Guid FieldDefinitionId, Guid ManifestItemId)>> ReadPredicateItemsAsync(IReadOnlyCollection<Guid> formRecordIds, IReadOnlyCollection<Guid> predicateFieldIds, CancellationToken cancellationToken);

    /// <summary>
    /// The candidate statement (§8.2): driven from <paramref name="scope"/> by a per-record probe on the posting-by-record index,
    /// with a fixed template per (value type, operator) and the metadata filters on entry facts. Returns at most
    /// <paramref name="limit"/> record ids, unordered. The plan never scans a value index across records.
    /// </summary>
    Task<IReadOnlyList<Guid>> FindCandidatesAsync(Guid formDefinitionId, IReadOnlyCollection<Guid> scope, IReadOnlyList<PreparedIndexedCondition> conditions, SearchMetadataFilter filter, int limit, CancellationToken cancellationToken);

    /// <summary>Testing only (plan-shape test, §8.2): the JSON EXPLAIN of the candidate statement, without executing it.</summary>
    /// <param name="analyze">C-135 (PD-9): <c>EXPLAIN (ANALYZE, BUFFERS)</c> — executes the read-only candidate statement and reports actual rows and buffers (Testing only).</param>
    Task<string> ExplainCandidatesAsync(Guid formDefinitionId, IReadOnlyCollection<Guid> scope, IReadOnlyList<PreparedIndexedCondition> conditions, SearchMetadataFilter filter, int limit, bool analyze, CancellationToken cancellationToken);

    /// <summary>
    /// Binding data for I-2 and the seal check: entry, postings (of <paramref name="fieldIds"/>, or every field when null),
    /// anomalies, head, manifest commit and all its leaves.
    /// </summary>
    Task<IReadOnlyList<SearchRecordBindingData>> ReadBindingDataAsync(IReadOnlyCollection<Guid> formRecordIds, IReadOnlyCollection<Guid>? fieldIds, CancellationToken cancellationToken);
}

/// <summary>Metadata filters of Advanced Field Search on verified header facts (as the V1 handler applies them).</summary>
public sealed record SearchMetadataFilter(string? RecordStatus, bool IncludeChildren, bool IncludeArchived);

public sealed record SearchQueryStateFacts(
    long AuthorizationRevision,
    long ContentIntegrityRevision,
    bool IntegrityQuarantineEnabled,
    SearchProjectionState? Projection,
    long FormContentRevision,
    IReadOnlyList<SearchFieldCoverage> Coverages,
    IReadOnlyList<FieldSearchApproval> Approvals,
    string? FormLifecycleState);

public sealed record SearchScopeRecordFacts(
    Guid FormRecordId,
    string ResourceLifecycleState,
    DateTimeOffset RecordRegisteredOn,
    long CommittedContentRevision,
    Guid? CommittedOperationId,
    bool ContentHeadQuarantined,
    DateTimeOffset? ContentHeadModifiedOn,
    Guid? EntryIndexedOperationId,
    int? EntryDerivationVersion,
    string? EntryRecordStatusName,
    Guid? EntryParentFormRecordId,
    Guid? EntryCreatedByBusinessUserId,
    bool PredicateFieldEncrypted);

/// <summary>One manifest leaf as the Authority recorded it (the accepted content of the record's current commit).</summary>
public sealed record SearchManifestLeafFacts(byte[] ManifestItemKey, byte ManifestItemKindCode, Guid ManifestItemId, byte[] CanonicalPayloadDigest, byte[] RowSignature);

public sealed record SearchRecordBindingData(
    SearchRecordEntry Entry,
    IReadOnlyList<SearchPostingBase> Postings,
    IReadOnlyList<SearchRecordFieldAnomaly> Anomalies,
    long HeadContentRevision,
    Guid? HeadOperationId,
    byte[] HeadManifestRoot,
    bool HeadQuarantined,
    Guid? CurrentRecordManifestCommitId,
    long? ManifestLeafCount,
    byte[]? ManifestRootDigest,
    IReadOnlyList<SearchManifestLeafFacts> Leaves);

/// <summary>Thrown by the reader when PostgreSQL cancels a statement on its timeout (SEARCH_VERIFICATION_LIMIT, time).</summary>
public sealed class SearchQueryTimeoutException(string message, Exception inner) : Exception(message, inner);
