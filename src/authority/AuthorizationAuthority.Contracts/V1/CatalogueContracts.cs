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
/// <remarks>
/// Phase C7 M3: <c>RecordRegisteredOn</c> (when the Authority registered the record) and <c>ContentHeadModifiedOn</c> (when
/// its accepted head last changed; null without content) are Authority-held times, used to sort and display verified search
/// results instead of unsigned business timestamps.
/// </remarks>
/// <remarks>
/// Phase C7 M4: in a migration catalogue page, <c>ProtectionCensusOutcome</c> is the outcome of a census the Authority already
/// holds for exactly this record, accepted operation and scoping command (null: none yet), so the verifier only sends the
/// rows of records that still need one. Always null in a user catalogue.
/// </remarks>
public sealed record CatalogueEntry(
    Guid FormRecordId,
    long CommittedContentRevision,
    Guid? CommittedOperationId,
    bool ContentHeadQuarantined,
    bool ResourceQuarantined,
    DateTimeOffset? RecordRegisteredOn = null,
    DateTimeOffset? ContentHeadModifiedOn = null,
    string? ProtectionCensusOutcome = null);

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

/// <summary>
/// Phase C7 M3 (OD-8): evidence of one field-search execution over a catalogue snapshot the caller holds — which fields and
/// operators were used, how many records were evaluated and matched, and the outcome code. Never predicate values,
/// record ids or field values.
/// </summary>
public sealed record SearchExecutionEvidence(IReadOnlyList<Guid> FieldDefinitionIds, IReadOnlyList<string> Operators, int ConditionCount, string Outcome, int? EvaluatedRecordCount, int? MatchedRecordCount, long ElapsedMilliseconds);

public sealed record SearchExecutionRecorded(Guid SearchCatalogueSnapshotId, Guid CorrelationId);

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

// Phase C7 M4 — G19 protection reconciliation (verifier workload only, OD-1; docs/architecture/PHASE_C7_ADVANCED_FIELD_SEARCH.md §6F).
// The Authority verifies each record's accepted revision itself and counts the field's rows in the old and the target form
// from the verified canonical bytes; the attestation is computed from Authority state only. Nothing here carries record
// contents, field values or ciphertext.

/// <summary>One record of a census: the head the verifier expects (from its migration snapshot) and the observed staged items of that operation.</summary>
public sealed record ProtectionCensusItem(Guid FormRecordId, Guid ExpectedCommittedOperationId, IReadOnlyList<StagedContentItem> ObservedItems);

/// <summary>
/// 1..50 records of the snapshot, each verified fully and independently. A census the Authority already holds for the same
/// (decision, record, accepted operation) is returned as <c>Reused</c> only when <c>ReuseSinceSnapshotId</c> names an earlier
/// migration snapshot of the same verifier, decision and form and the held census was verified at or after that snapshot
/// was issued (Authority clock) — i.e. within the same reconciliation. Without it every record is verified again, so a held
/// result from an earlier reconciliation never stands in for a fresh one. Rows must always be sent.
/// </summary>
public sealed record ProtectionCensusRequest(IReadOnlyList<ProtectionCensusItem> Records, Guid? ReuseSinceSnapshotId = null);

/// <summary>
/// Per-record census. <c>Outcome</c> is <c>Verified</c> or a typed failure: <c>HEAD_CHANGED</c> (nothing verified),
/// <c>NOT_IN_SCOPE</c> (not an entry of the snapshot / not a registered record of the form), <c>QUARANTINED</c>,
/// <c>AWAITING_ARCHIVE</c>, <c>NO_COMMITTED_CONTENT</c>, <c>KEY_UNAVAILABLE</c>, or an integrity code
/// (<c>MISSING_ITEM</c>, <c>EXTRA_ITEM</c>, <c>ITEM_DIGEST_MISMATCH</c>, …; the record head is then quarantined).
/// <c>Reused</c>: the Authority already held a census for this exact accepted operation and returned it without re-verifying.
/// </summary>
public sealed record ProtectionCensusRecordResult(
    Guid FormRecordId,
    string Outcome,
    long CommittedContentRevision,
    Guid? CommittedOperationId,
    int FieldOccurrenceCount,
    int OldFormOccurrenceCount,
    int EnvelopeIssueCount,
    bool Reused);

public sealed record ProtectionCensusResponse(Guid SearchCatalogueSnapshotId, Guid FieldDefinitionId, string ProtectionDirection, IReadOnlyList<ProtectionCensusRecordResult> Results, Guid CorrelationId);

/// <summary>
/// The Authority's reconciliation report for one protection decision, computed under the organization lock from its registry,
/// accepted heads and census results. Complete only when every accepted record's <b>current</b> revision was verified with no
/// row of the field left in the old form and nothing quarantined, unverified or uncensused, and no search approval of the
/// field is active. Pending creations hold no accepted content (the protection barrier admits their first revision only in
/// the required form). Retained earlier revisions and retired records are outside the executable scope and never claimed.
/// </summary>
public sealed record ProtectionAttestation(
    Guid ProtectionReconciliationAttestationId,
    Guid OrganizationId,
    Guid SecurityCommandId,
    Guid FormDefinitionId,
    Guid FieldDefinitionId,
    string ProtectionDirection,
    Guid? SearchCatalogueSnapshotId,
    int ExpectedRecordCount,
    int AcceptedRecordCount,
    int PendingCreationRecordCount,
    int QuarantinedRecordCount,
    int CompliantRecordCount,
    int NotApplicableRecordCount,
    int OldFormRemainingRecordCount,
    int OldFormRemainingOccurrenceCount,
    int TargetFormOccurrenceCount,
    int UnverifiedRecordCount,
    int NotYetCensusedRecordCount,
    int EnvelopeIssueRecordCount,
    int ArchivedRecordCount,
    int RetiredRecordCount,
    long RetainedHistoryRevisionCount,
    int ActiveSearchApprovalCount,
    bool AttestationComplete,
    string? AttestationIncompleteReason,
    string AttestationDigestHex,
    long AuthorizationRevision,
    long ContentIntegrityRevision,
    long AuditJournalRevision,
    DateTimeOffset AttestedOn,
    string CoverageStatement,
    Guid CorrelationId);

/// <summary>Organization-wide coverage: every current protection decision of the organization and its latest attestation.</summary>
public sealed record ProtectionCoverageRequest(Guid OrganizationId);

public sealed record ProtectionCoverageDecision(Guid SecurityCommandId, Guid FormDefinitionId, Guid FieldDefinitionId, string ProtectionDirection, bool FormRegisteredActive, ProtectionAttestation? LatestAttestation);

/// <summary>
/// <c>OrganizationComplete</c> only when every current decision has a complete attestation taken under that same decision.
/// The decision list comes from the Authority's protection resources, never from the business database's form list.
/// </summary>
public sealed record ProtectionCoverageResponse(
    Guid OrganizationId,
    int RegisteredFormCount,
    int DecisionCount,
    int AttestedCompleteDecisionCount,
    bool OrganizationComplete,
    IReadOnlyList<ProtectionCoverageDecision> Decisions,
    long AuthorizationRevision,
    DateTimeOffset ComputedOn,
    string CoverageStatement,
    Guid CorrelationId);
