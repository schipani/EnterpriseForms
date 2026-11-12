namespace AuthorizationAuthority.Contracts.V1;

// IX-2 (design §7, §12, R1 §C.3-C.7): the search-index builder workload plane and the SearchProjectionRebuild command.
// Identifiers, counts and outcome codes only: no field value, label or search term ever crosses these contracts in a response.
// The builder submits bytes (observed items) and nothing else; it can never supply postings, entries or coverage states.

/// <summary>One form whose coverage the Authority still has to build (work list of the builder; no contents).</summary>
public sealed record SearchIndexBuildingForm(Guid OrganizationId, Guid FormDefinitionId, int BuildingCoverageCount, string ProjectionStatusName);

public sealed record SearchIndexBuilderWorkResponse(IReadOnlyList<SearchIndexBuildingForm> BuildingForms, IReadOnlyList<Guid> IndexedOrganizationIds, Guid CorrelationId);

/// <summary>A page of the form's registered population (record id order, after <c>AfterFormRecordId</c>; at most 500).</summary>
public sealed record SearchIndexPopulationRequest(Guid OrganizationId, Guid FormDefinitionId, Guid? AfterFormRecordId = null, int Limit = 500);

/// <summary>
/// One registered record with its accepted head. <c>NeedsIndexing</c> is true when the record has accepted content and no entry
/// at its head derived under every current non-withdrawn coverage of the form. Quarantined records are listed with their flag
/// (they block attestation; the builder does not submit them).
/// </summary>
public sealed record SearchIndexPopulationEntry(Guid FormRecordId, long CommittedContentRevision, Guid? CommittedOperationId, bool ContentHeadQuarantined, bool ResourceQuarantined, bool NeedsIndexing);

public sealed record SearchIndexPopulationPage(Guid OrganizationId, Guid FormDefinitionId, IReadOnlyList<SearchIndexPopulationEntry> Entries, Guid? NextAfterFormRecordId, Guid CorrelationId);

/// <summary>One record's bytes as read by the builder from the business database: untrusted until the Authority verified them.</summary>
public sealed record SearchIndexBackfillItem(Guid FormRecordId, Guid ExpectedCommittedOperationId, IReadOnlyList<StagedContentItem> ObservedItems);

/// <summary>1..50 records of one form, verified and derived in one Authority transaction under the organization lock.</summary>
public sealed record SearchIndexBackfillRequest(Guid OrganizationId, Guid FormDefinitionId, IReadOnlyList<SearchIndexBackfillItem> Records);

/// <summary>
/// Per record: <c>Indexed</c> (derived, sealed and written at the head), <c>AlreadyIndexed</c> (an entry at the head under the
/// current coverages exists; nothing written), <c>HEAD_CHANGED</c>, <c>NOT_IN_SCOPE</c>, <c>NO_COMMITTED_CONTENT</c>,
/// <c>QUARANTINED</c>, <c>AWAITING_ARCHIVE</c>, <c>NOT_COVERED</c>, <c>KEY_UNAVAILABLE</c>, or an integrity failure code (the
/// record's head was quarantined and the incident journaled).
/// </summary>
public sealed record SearchIndexBackfillResult(Guid FormRecordId, string Outcome, long CommittedContentRevision, Guid? CommittedOperationId, bool RecordQuarantined);

public sealed record SearchIndexBackfillResponse(Guid OrganizationId, Guid FormDefinitionId, IReadOnlyList<SearchIndexBackfillResult> Results, Guid CorrelationId);

public sealed record SearchIndexAttestationRequest(Guid OrganizationId, Guid FormDefinitionId);

/// <summary>
/// The Authority's attestation, computed now under the organization lock from its registry, heads and entries (nothing supplied
/// by the builder). <c>CoverageComplete</c> is true only when every contentful registered record of the form has an entry at its
/// head under every Building coverage and no record is quarantined; the Building coverages are then Complete.
/// </summary>
public sealed record SearchIndexAttestation(
    Guid OrganizationId,
    Guid FormDefinitionId,
    bool CoverageComplete,
    string? IncompleteReasonCode,
    int RegisteredRecordCount,
    int ContentfulRecordCount,
    int IndexedRecordCount,
    int MissingRecordCount,
    int QuarantinedRecordCount,
    int PendingCreationRecordCount,
    int CoveragesCompleted,
    string ProjectionStatusName,
    Guid? AttestationJournalEntryId,
    Guid CorrelationId);

/// <summary>S-C work: the entries the Authority chose for deep comparison (oldest first, plus its own random sample).</summary>
public sealed record SearchIndexDeepSweepWorkRequest(Guid OrganizationId, int OldestLimit = 50, int SampleLimit = 0);

public sealed record SearchIndexDeepSweepWorkItem(Guid FormRecordId, Guid FormDefinitionId, Guid IndexedOperationId);

public sealed record SearchIndexDeepSweepWorkResponse(Guid OrganizationId, IReadOnlyList<SearchIndexDeepSweepWorkItem> Entries, Guid CorrelationId);

public sealed record SearchIndexDeepVerifyRequest(Guid OrganizationId, IReadOnlyList<SearchIndexBackfillItem> Records);

/// <summary>
/// Per entry (§17.10.2 classes): <c>Pass</c> (re-derived postings equal the stored ones; S-C timestamp written), <c>Fail</c>
/// (posting mismatch: the search index is quarantined), <c>Inconclusive</c> (no timestamp). <c>RecordQuarantined</c>: the business
/// bytes did not verify, so the record head was quarantined (existing rule) and the unit is Inconclusive for the index.
/// </summary>
public sealed record SearchIndexDeepVerifyResult(Guid FormRecordId, string UnitOutcome, string OutcomeCode, bool RecordQuarantined);

public sealed record SearchIndexDeepVerifyResponse(Guid OrganizationId, IReadOnlyList<SearchIndexDeepVerifyResult> Results, string ProjectionStatusName, Guid CorrelationId);

/// <summary>SearchProjectionRebuild (security administrators, administer_security): purge and rebuild the organization's index.</summary>
public sealed record SearchProjectionRebuildRequest(Guid OrganizationId, string RebuildReasonCode);

public sealed record SearchProjectionRebuildResponse(Guid OrganizationId, string PreviousProjectionStatusName, string? PreviousStatusReasonCode, string ProjectionStatusName, int EntriesPurged, int CoveragesReturnedToBuilding, long AuthorizationRevision, Guid CorrelationId);
