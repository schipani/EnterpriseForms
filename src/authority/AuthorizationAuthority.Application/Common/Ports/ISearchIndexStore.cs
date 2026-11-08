using AuthorizationAuthority.Domain.Features.FieldSearch;
using AuthorizationAuthority.Domain.Features.SearchIndex;

namespace AuthorizationAuthority.Application.Common.Ports;

/// <summary>Everything derived for one record at one accepted head: the entry (with its seal) and its postings and anomalies.</summary>
public sealed record DerivedRecordIndex(
    SearchRecordEntry Entry,
    IReadOnlyList<SearchStringPosting> StringPostings,
    IReadOnlyList<SearchIntegerPosting> IntegerPostings,
    IReadOnlyList<SearchDecimalPosting> DecimalPostings,
    IReadOnlyList<SearchDatePosting> DatePostings,
    IReadOnlyList<SearchDateTimePosting> DateTimePostings,
    IReadOnlyList<SearchBooleanPosting> BooleanPostings,
    IReadOnlyList<SearchOptionPosting> OptionPostings,
    IReadOnlyList<SearchRecordFieldAnomaly> Anomalies)
{
    public IEnumerable<SearchPostingBase> AllPostings =>
        StringPostings.Cast<SearchPostingBase>().Concat(IntegerPostings).Concat(DecimalPostings).Concat(DatePostings).Concat(DateTimePostings).Concat(BooleanPostings).Concat(OptionPostings);
}

/// <summary>The stored rows of one record, as read back for seal verification.</summary>
public sealed record StoredRecordIndex(SearchRecordEntry Entry, IReadOnlyList<SearchPostingBase> Postings, IReadOnlyList<SearchRecordFieldAnomaly> Anomalies);

/// <summary>Counts of S-A set checks over one organization (each must be zero for a passing cheap sweep).</summary>
/// <param name="RecordsMissingFromCompleteCoverage">
/// IX-2 (R1 §C.6 first check): contentful registered records of a form whose coverage is Complete that have no entry at their
/// head derived under that coverage's approval. Complete is set only by attestation, after which every commit derives inline,
/// so a missing entry is an Authority-domain omission (a Fail).
/// </param>
public sealed record CheapSweepFindings(
    int EntriesNotAtHead,
    int EntriesForInactiveOrMissingRecords,
    int RowsNotBoundToEntry,
    int RowsOfWithdrawnCoverage,
    int EntriesWithoutActiveCoverage,
    int RowsOfAnotherOrganization,
    int EntriesWithoutSeal,
    int RecordsMissingFromCompleteCoverage = 0)
{
    public bool Clean => EntriesNotAtHead == 0 && EntriesForInactiveOrMissingRecords == 0 && RowsNotBoundToEntry == 0 && RowsOfWithdrawnCoverage == 0
                         && EntriesWithoutActiveCoverage == 0 && RowsOfAnotherOrganization == 0 && EntriesWithoutSeal == 0 && RecordsMissingFromCompleteCoverage == 0;

    public string FirstFindingCode() =>
        EntriesNotAtHead > 0 ? "ENTRY_NOT_AT_HEAD"
        : EntriesForInactiveOrMissingRecords > 0 ? "ENTRY_FOR_INACTIVE_RECORD"
        : RowsNotBoundToEntry > 0 ? "ROW_NOT_BOUND_TO_ENTRY"
        : RowsOfWithdrawnCoverage > 0 ? "WITHDRAWN_FIELD_RESIDUE"
        : EntriesWithoutActiveCoverage > 0 ? "ENTRY_WITHOUT_COVERAGE"
        : RowsOfAnotherOrganization > 0 ? "CROSS_ORGANIZATION_ROW"
        : EntriesWithoutSeal > 0 ? "ENTRY_WITHOUT_SEAL"
        : RecordsMissingFromCompleteCoverage > 0 ? "COMPLETE_COVERAGE_MISSING_ENTRY"
        : "NONE";
}

/// <summary>IX-2: one form with coverage still to be built (builder work list; identifiers and counts only).</summary>
public sealed record SearchBuildingForm(Guid OrganizationId, Guid FormDefinitionId, int BuildingCoverageCount, string ProjectionStatusName);

/// <summary>
/// Oldest and newest scope timestamps of an organization's entries (deadline evaluation, design §17.10). The S-B values cover
/// every entry. The S-C values cover only entries of records that are not quarantined in the Authority (D-IX2-7, C-129):
/// <paramref name="DeepDeadlineExcludedEntryCount"/> entries were left out, <paramref name="DeepDeadlineEntryCount"/> counted.
/// </summary>
public sealed record SearchScopeTimestamps(int EntryCount, DateTimeOffset? OldestSealVerifiedOn, DateTimeOffset? NewestSealVerifiedOn, DateTimeOffset? OldestDeepVerifiedOn, DateTimeOffset? NewestDeepVerifiedOn,
    int DeepDeadlineEntryCount = -1, int DeepDeadlineExcludedEntryCount = 0)
{
    /// <summary>Entries counted for the S-C deadline (all entries when the store does not report the exclusion).</summary>
    public int DeepEntriesCounted => DeepDeadlineEntryCount < 0 ? EntryCount : DeepDeadlineEntryCount;
}

/// <summary>
/// IX-1: the Authority-owned search index (schema <c>authority_search</c>). Obtained from an <see cref="IAuthorityScope"/>, so
/// every operation runs in that scope's single transaction and commits or rolls back with it. Implemented only in
/// AuthorizationAuthority.Infrastructure (and an in-memory double in the unit tests). Never reachable from the business API.
/// </summary>
public interface ISearchIndexStore
{
    /// <summary>Sets the transaction-local organization context used by the RLS policies (defence in depth, not authorization).</summary>
    Task BindOrganizationAsync(Guid organizationId, CancellationToken cancellationToken);

    Task<SearchProjectionState?> FindProjectionStateAsync(Guid organizationId, CancellationToken cancellationToken);

    void AddProjectionState(SearchProjectionState state);

    /// <summary>Organizations that have a search projection (sweeps).</summary>
    Task<List<Guid>> GetIndexedOrganizationIdsAsync(CancellationToken cancellationToken);

    Task<List<SearchFieldCoverage>> GetCoveragesAsync(Guid organizationId, Guid? formDefinitionId, CancellationToken cancellationToken);

    void AddCoverage(SearchFieldCoverage coverage);

    /// <summary>Deletes the record's entry, postings and anomalies, then inserts <paramref name="derived"/>.</summary>
    Task ReplaceRecordIndexAsync(DerivedRecordIndex derived, CancellationToken cancellationToken);

    /// <summary>
    /// IX-1 remediation, content commit (W1): the form's approvals that are Active and have a coverage other than Withdrawn,
    /// ordered by approval id, in one read. Same result as <see cref="GetCoveragesAsync"/> plus the Authority approvals filtered
    /// in memory.
    /// </summary>
    Task<List<FieldSearchApproval>> GetCoveredActiveApprovalsAsync(Guid organizationId, Guid formDefinitionId, CancellationToken cancellationToken);

    /// <summary>
    /// IX-1 remediation, content commit (W1), one command: binds the organization context (RLS), deletes the record's entry,
    /// postings and anomalies, and increments the form's index revision when <paramref name="alwaysAdvanceFormState"/> is true
    /// or an entry was deleted. Returns the number of entries deleted (0 or 1).
    /// </summary>
    Task<int> ResetRecordIndexAsync(Guid organizationId, Guid formDefinitionId, Guid formRecordId, bool alwaysAdvanceFormState, DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>Queues a derived record index for insertion by the scope's next write (after <see cref="ResetRecordIndexAsync"/>).</summary>
    void AddRecordIndex(DerivedRecordIndex derived);

    /// <summary>Deletes the record's entry, postings and anomalies; returns the number of entries deleted (0 or 1).</summary>
    Task<int> DeleteRecordIndexAsync(Guid organizationId, Guid formRecordId, CancellationToken cancellationToken);

    /// <summary>Deletes every entry, posting and anomaly of a form; returns the number of entries deleted.</summary>
    Task<int> PurgeFormIndexAsync(Guid organizationId, Guid formDefinitionId, CancellationToken cancellationToken);

    /// <summary>Increments (creating if needed) the form's index revision.</summary>
    Task BumpFormStateAsync(Guid organizationId, Guid formDefinitionId, DateTimeOffset now, CancellationToken cancellationToken);

    Task<SearchFormState?> FindFormStateAsync(Guid organizationId, Guid formDefinitionId, CancellationToken cancellationToken);

    /// <summary>Entries ordered by oldest seal verification first (S-B), excluding <paramref name="excluding"/> (entries already attempted in the current pass).</summary>
    Task<List<SearchRecordEntry>> GetEntriesForSealCheckAsync(Guid organizationId, int limit, IReadOnlyCollection<Guid> excluding, CancellationToken cancellationToken);

    Task<StoredRecordIndex?> GetStoredRecordIndexAsync(Guid organizationId, Guid formRecordId, CancellationToken cancellationToken);

    /// <summary>Column-limited update of the S-B timestamps of one entry (verified time only on a passing verification, §17.10 D1).</summary>
    Task RecordSealCheckAsync(Guid searchRecordEntryId, DateTimeOffset? verifiedOn, DateTimeOffset attemptedOn, string outcomeCode, CancellationToken cancellationToken);

    Task<CheapSweepFindings> RunCheapChecksAsync(Guid organizationId, CancellationToken cancellationToken);

    /// <summary>
    /// ReconciliationRequired only: deletes (with their postings and anomalies) the entries whose indexed operation is no longer
    /// the record's committed head operation; returns the forms affected. Nothing is re-derived (no bytes are held).
    /// </summary>
    Task<IReadOnlyList<Guid>> DeleteEntriesNotAtHeadAsync(Guid organizationId, CancellationToken cancellationToken);

    Task<SearchScopeTimestamps> GetScopeTimestampsAsync(Guid organizationId, CancellationToken cancellationToken);

    // ---- IX-2: backfill, attestation, deep sweep, rebuild --------------------------------------------------------------

    /// <summary>Forms (across organizations) with a Building coverage whose projection is not Quarantined (builder work list).</summary>
    Task<List<SearchBuildingForm>> GetBuildingFormsAsync(CancellationToken cancellationToken);

    /// <summary>The entries of a form; restricted to <paramref name="formRecordIds"/> when given.</summary>
    Task<List<SearchRecordEntry>> GetFormEntriesAsync(Guid organizationId, Guid formDefinitionId, IReadOnlyCollection<Guid>? formRecordIds, CancellationToken cancellationToken);

    /// <summary>Deletes every entry, posting and anomaly of the organization (SearchProjectionRebuild); returns the entries deleted.</summary>
    Task<int> PurgeOrganizationIndexAsync(Guid organizationId, CancellationToken cancellationToken);

    /// <summary>S-C: entries of records not quarantined in the Authority (D-IX2-7), ordered by oldest deep verification first.</summary>
    Task<List<SearchRecordEntry>> GetEntriesForDeepCheckAsync(Guid organizationId, int limit, CancellationToken cancellationToken);

    /// <summary>S-C: a uniformly random sample of the organization's entries of non-quarantined records (chosen by the Authority, never by the builder).</summary>
    Task<List<SearchRecordEntry>> GetRandomEntriesAsync(Guid organizationId, int limit, CancellationToken cancellationToken);

    /// <summary>Column-limited update of the S-C timestamps of one entry (verified time only on a passing comparison, §17.10 D1).</summary>
    Task RecordDeepCheckAsync(Guid searchRecordEntryId, DateTimeOffset? verifiedOn, DateTimeOffset attemptedOn, string outcomeCode, CancellationToken cancellationToken);

    // ---- IX-3: result snapshots (design §17.3, §18.4). Ids and positions only. ----------------------------------------

    void AddResultSnapshot(SearchResultSnapshot snapshot, IReadOnlyList<SearchResultSnapshotEntry> entries);

    Task<SearchResultSnapshot?> FindResultSnapshotAsync(Guid searchResultSnapshotId, CancellationToken cancellationToken);

    /// <summary>
    /// Records that a page is being served (UPDATE of the served-on column): the row lock serializes page requests on one snapshot
    /// with its deletion (§18.4). Returns the snapshot, or null when it no longer exists.
    /// </summary>
    Task<SearchResultSnapshot?> MarkResultSnapshotServedAsync(Guid searchResultSnapshotId, DateTimeOffset servedOn, CancellationToken cancellationToken);

    Task<List<SearchResultSnapshotEntry>> GetResultSnapshotEntriesAsync(Guid searchResultSnapshotId, int offset, int count, CancellationToken cancellationToken);

    Task<int> DeleteResultSnapshotAsync(Guid searchResultSnapshotId, CancellationToken cancellationToken);

    /// <summary>Deletes the session's oldest snapshots so that at most <paramref name="keep"/> remain (a fourth evicts the oldest).</summary>
    Task<int> EvictSessionSnapshotsAsync(Guid authenticatedSessionId, int keep, CancellationToken cancellationToken);

    /// <summary>Live (unexpired) result entries created by one Authority instance (capacity guard only, X3).</summary>
    Task<long> CountLiveResultEntriesAsync(string creatingInstanceName, DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>
    /// The purge job (§18.4): at most <paramref name="batchSize"/> snapshots that are expired, of a quarantined organization, of an
    /// older authorization revision, of a projection that is not Active or is suspended, or of a revoked, expired, idle or missing
    /// session. Rows locked by a page request in progress are skipped (the next run deletes them).
    /// </summary>
    Task<int> PurgeResultSnapshotsAsync(DateTimeOffset now, int batchSize, CancellationToken cancellationToken);
}
