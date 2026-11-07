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
public sealed record CheapSweepFindings(
    int EntriesNotAtHead,
    int EntriesForInactiveOrMissingRecords,
    int RowsNotBoundToEntry,
    int RowsOfWithdrawnCoverage,
    int EntriesWithoutActiveCoverage,
    int RowsOfAnotherOrganization,
    int EntriesWithoutSeal)
{
    public bool Clean => EntriesNotAtHead == 0 && EntriesForInactiveOrMissingRecords == 0 && RowsNotBoundToEntry == 0 && RowsOfWithdrawnCoverage == 0
                         && EntriesWithoutActiveCoverage == 0 && RowsOfAnotherOrganization == 0 && EntriesWithoutSeal == 0;

    public string FirstFindingCode() =>
        EntriesNotAtHead > 0 ? "ENTRY_NOT_AT_HEAD"
        : EntriesForInactiveOrMissingRecords > 0 ? "ENTRY_FOR_INACTIVE_RECORD"
        : RowsNotBoundToEntry > 0 ? "ROW_NOT_BOUND_TO_ENTRY"
        : RowsOfWithdrawnCoverage > 0 ? "WITHDRAWN_FIELD_RESIDUE"
        : EntriesWithoutActiveCoverage > 0 ? "ENTRY_WITHOUT_COVERAGE"
        : RowsOfAnotherOrganization > 0 ? "CROSS_ORGANIZATION_ROW"
        : EntriesWithoutSeal > 0 ? "ENTRY_WITHOUT_SEAL"
        : "NONE";
}

/// <summary>Oldest and newest scope timestamps of an organization's entries (deadline evaluation, design §17.10).</summary>
public sealed record SearchScopeTimestamps(int EntryCount, DateTimeOffset? OldestSealVerifiedOn, DateTimeOffset? NewestSealVerifiedOn, DateTimeOffset? OldestDeepVerifiedOn, DateTimeOffset? NewestDeepVerifiedOn);

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

    /// <summary>Deletes the record's entry, postings and anomalies; returns the number of entries deleted (0 or 1).</summary>
    Task<int> DeleteRecordIndexAsync(Guid organizationId, Guid formRecordId, CancellationToken cancellationToken);

    /// <summary>Deletes every entry, posting and anomaly of a form; returns the number of entries deleted.</summary>
    Task<int> PurgeFormIndexAsync(Guid organizationId, Guid formDefinitionId, CancellationToken cancellationToken);

    /// <summary>Increments (creating if needed) the form's index revision.</summary>
    Task BumpFormStateAsync(Guid organizationId, Guid formDefinitionId, DateTimeOffset now, CancellationToken cancellationToken);

    Task<SearchFormState?> FindFormStateAsync(Guid organizationId, Guid formDefinitionId, CancellationToken cancellationToken);

    /// <summary>Entries ordered by oldest seal verification first (S-B).</summary>
    Task<List<SearchRecordEntry>> GetEntriesForSealCheckAsync(Guid organizationId, int limit, CancellationToken cancellationToken);

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
}
