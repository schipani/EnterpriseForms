namespace AuthorizationAuthority.Contracts.V1;

// Phase C7 M1 — AC-5 Authority record catalogue (docs/architecture/PHASE_C7_ADVANCED_FIELD_SEARCH.md §3). Private internal
// contract (/internal/v1), snake_case on the wire. The catalogue is the expected record population; business rows are only
// a source of bytes that the batch verification checks against the heads fixed here.

/// <summary>User-scoped catalogue: registered records of one form that the delegated user may discover, with accepted heads.</summary>
public sealed record SearchCatalogueRequest(Guid OrganizationId, Guid FormDefinitionId, int MaximumEntries);

/// <summary>
/// One record of a catalogue. <c>CommittedContentRevision</c> 0 = registered without accepted content (it can match
/// nothing; it is listed so its first content invalidates the snapshot). Quarantined heads/resources are listed with their
/// flag: a trusted search over them must fail, never skip them.
/// </summary>
public sealed record CatalogueEntry(
    Guid FormRecordId,
    long CommittedContentRevision,
    Guid? CommittedOperationId,
    bool ContentHeadQuarantined,
    bool ResourceQuarantined);

public sealed record SearchCatalogueResponse(
    Guid SearchCatalogueSnapshotId,
    Guid OrganizationId,
    Guid FormDefinitionId,
    long AuthorizationRevision,
    long ProjectionRevision,
    long ContentIntegrityRevision,
    IReadOnlyList<CatalogueEntry> Entries,
    DateTimeOffset SnapshotExpiresOn,
    Guid CorrelationId);

/// <summary>Validation of a snapshot against current Authority state (never against the id alone).</summary>
public sealed record CatalogueSnapshotValidation(
    Guid SearchCatalogueSnapshotId,
    bool Valid,
    string? InvalidationReason,
    long CurrentAuthorizationRevision,
    long CurrentContentIntegrityRevision,
    Guid CorrelationId);

/// <summary>
/// Privileged migration catalogue (verifier workload only, OD-1): the complete expected population of the form scoped by an
/// effective protection command, including records no user can discover. Paged from the immutable snapshot.
/// </summary>
public sealed record MigrationCatalogueRequest(Guid OrganizationId, Guid FormDefinitionId, Guid SecurityCommandId);

public sealed record MigrationCataloguePage(
    Guid SearchCatalogueSnapshotId,
    Guid OrganizationId,
    Guid FormDefinitionId,
    Guid SecurityCommandId,
    long AuthorizationRevision,
    long ContentIntegrityRevision,
    int TotalEntryCount,
    IReadOnlyList<CatalogueEntry> Entries,
    int? NextOffset,
    Guid CorrelationId);

/// <summary>One record to verify in a batch: the head the caller expects (from its snapshot) and the observed staged items.</summary>
public sealed record BatchVerificationItem(Guid FormRecordId, Guid ExpectedCommittedOperationId, IReadOnlyList<StagedContentItem> ObservedItems);

/// <summary>Batch verification (OD-6): 1..50 records, each verified fully and independently, read action only.</summary>
public sealed record VerifyRecordProjectionsRequest(Guid OrganizationId, IReadOnlyList<BatchVerificationItem> Records);

/// <summary>
/// Per-record outcome. <c>Verified</c> only when the observed items equal the complete accepted leaf index of the expected
/// head with verifying signatures and root (exactly as VerifyRecordProjection). Failure codes: the integrity codes of
/// VerifyRecordProjection (the head is then quarantined), <c>HEAD_CHANGED</c> (current head is not the expected one;
/// nothing verified), <c>RECORD_UNAVAILABLE</c> (not discoverable / not registered — concealment-safe),
/// <c>QUARANTINED</c>, <c>AWAITING_ARCHIVE</c>, <c>NO_COMMITTED_CONTENT</c>, <c>KEY_UNAVAILABLE</c>.
/// <c>ReadableItems</c> is empty unless verified; for a full reader it is every item.
/// </summary>
public sealed record BatchVerificationResult(
    Guid FormRecordId,
    bool Verified,
    string? FailureReasonCode,
    long CommittedContentRevision,
    Guid? CommittedOperationId,
    bool IsCompleteProjection,
    IReadOnlyList<VerifiedProjectionItem> ReadableItems);

public sealed record VerifyRecordProjectionsResponse(IReadOnlyList<BatchVerificationResult> Results, long AuthorizationRevision, Guid CorrelationId);
