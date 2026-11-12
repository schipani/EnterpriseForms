namespace EnterpriseForms.Application.Common.Authority;

// IX-2 (IX-D6): mirrors of AuthorizationAuthority.Contracts.V1 SearchIndexBuilderContracts. Identifiers, counts and outcome codes
// only; the builder sends business bytes (observed items) and nothing else.

public sealed record SearchIndexBuildingForm(Guid OrganizationId, Guid FormDefinitionId, int BuildingCoverageCount, string ProjectionStatusName);

public sealed record SearchIndexBuilderWork(IReadOnlyList<SearchIndexBuildingForm> BuildingForms, IReadOnlyList<Guid> IndexedOrganizationIds, Guid CorrelationId);

public sealed record SearchIndexPopulationEntry(Guid FormRecordId, long CommittedContentRevision, Guid? CommittedOperationId, bool ContentHeadQuarantined, bool ResourceQuarantined, bool NeedsIndexing);

public sealed record SearchIndexPopulationPage(Guid OrganizationId, Guid FormDefinitionId, IReadOnlyList<SearchIndexPopulationEntry> Entries, Guid? NextAfterFormRecordId, Guid CorrelationId);

public sealed record SearchIndexBackfillItem(Guid FormRecordId, Guid ExpectedCommittedOperationId, IReadOnlyList<StagedContentItem> ObservedItems);

public sealed record SearchIndexBackfillResult(Guid FormRecordId, string Outcome, long CommittedContentRevision, Guid? CommittedOperationId, bool RecordQuarantined);

public sealed record SearchIndexBackfillResponse(Guid OrganizationId, Guid FormDefinitionId, IReadOnlyList<SearchIndexBackfillResult> Results, Guid CorrelationId);

public sealed record SearchIndexAttestation(Guid OrganizationId, Guid FormDefinitionId, bool CoverageComplete, string? IncompleteReasonCode, int RegisteredRecordCount, int ContentfulRecordCount, int IndexedRecordCount,
    int MissingRecordCount, int QuarantinedRecordCount, int PendingCreationRecordCount, int CoveragesCompleted, string ProjectionStatusName, Guid? AttestationJournalEntryId, Guid CorrelationId);

public sealed record SearchIndexDeepSweepWorkItem(Guid FormRecordId, Guid FormDefinitionId, Guid IndexedOperationId);

public sealed record SearchIndexDeepSweepWork(Guid OrganizationId, IReadOnlyList<SearchIndexDeepSweepWorkItem> Entries, Guid CorrelationId);

public sealed record SearchIndexDeepVerifyResult(Guid FormRecordId, string UnitOutcome, string OutcomeCode, bool RecordQuarantined);

public sealed record SearchIndexDeepVerifyResponse(Guid OrganizationId, IReadOnlyList<SearchIndexDeepVerifyResult> Results, string ProjectionStatusName, Guid CorrelationId);

/// <summary>
/// IX-2 (IX-D6, OD-1 pattern): the separate <c>search-index-builder</c> workload's view of the Authority. Its credential is
/// configured only for the Worker and registered only by the Worker host; the business API never resolves this port. The port
/// can read the population, submit bytes for verification and ask for an attestation; it cannot write the index, choose what is
/// indexed, set a coverage state or read any indexed value.
/// </summary>
public interface ISearchIndexBuilderAuthority
{
    /// <summary>False when this deployment has no builder credential: the builder then stays idle (fail closed).</summary>
    bool IsConfigured { get; }

    Task<SearchIndexBuilderWork> GetWorkAsync(CancellationToken cancellationToken);

    Task<SearchIndexPopulationPage> ReadPopulationAsync(Guid organizationId, Guid formDefinitionId, Guid? afterFormRecordId, int limit, CancellationToken cancellationToken);

    Task<SearchIndexBackfillResponse> BackfillAsync(Guid organizationId, Guid formDefinitionId, IReadOnlyList<SearchIndexBackfillItem> records, CancellationToken cancellationToken);

    Task<SearchIndexAttestation> AttestAsync(Guid organizationId, Guid formDefinitionId, CancellationToken cancellationToken);

    Task<SearchIndexDeepSweepWork> GetDeepSweepWorkAsync(Guid organizationId, int oldestLimit, int sampleLimit, CancellationToken cancellationToken);

    Task<SearchIndexDeepVerifyResponse> DeepVerifyAsync(Guid organizationId, IReadOnlyList<SearchIndexBackfillItem> records, CancellationToken cancellationToken);
}

public sealed record SearchProjectionRebuildCommand(Guid OrganizationId, string RebuildReasonCode);

public sealed record AuthoritySearchProjectionRebuild(Guid OrganizationId, string PreviousProjectionStatusName, string? PreviousStatusReasonCode, string ProjectionStatusName, int EntriesPurged, int CoveragesReturnedToBuilding, long AuthorizationRevision, Guid CorrelationId);

/// <summary>IX-2: SearchProjectionRebuild on behalf of a delegated Security Administrator (narrow port; only the Search feature uses it).</summary>
public interface ISearchIndexAdministrationAuthority
{
    Task<AuthoritySearchProjectionRebuild> RebuildSearchProjectionAsync(DelegatedUserContext user, SearchProjectionRebuildCommand command, CancellationToken cancellationToken);
}
