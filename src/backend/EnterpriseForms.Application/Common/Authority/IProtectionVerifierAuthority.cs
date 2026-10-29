namespace EnterpriseForms.Application.Common.Authority;

// Phase C7 M4 — G19 protection reconciliation (mirrors of AuthorizationAuthority.Contracts.V1 CatalogueContracts, verifier part).

public sealed record MigrationCataloguePage(Guid SearchCatalogueSnapshotId, Guid OrganizationId, Guid FormDefinitionId, Guid SecurityCommandId, long AuthorizationRevision, long ContentIntegrityRevision,
    int TotalEntryCount, IReadOnlyList<CatalogueEntry> Entries, int? NextOffset, Guid CorrelationId);

public sealed record ProtectionCensusItem(Guid FormRecordId, Guid ExpectedCommittedOperationId, IReadOnlyList<StagedContentItem> ObservedItems);

public sealed record ProtectionCensusRecordResult(Guid FormRecordId, string Outcome, long CommittedContentRevision, Guid? CommittedOperationId, int FieldOccurrenceCount, int OldFormOccurrenceCount, int EnvelopeIssueCount, bool Reused)
{
    public bool Verified => Outcome == "Verified";
}

public sealed record ProtectionCensusResponse(Guid SearchCatalogueSnapshotId, Guid FieldDefinitionId, string ProtectionDirection, IReadOnlyList<ProtectionCensusRecordResult> Results, Guid CorrelationId);

/// <summary>The Authority's reconciliation report for one protection decision (computed and journaled by the Authority; nothing here comes from the business database).</summary>
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

public sealed record ProtectionCoverageDecision(Guid SecurityCommandId, Guid FormDefinitionId, Guid FieldDefinitionId, string ProtectionDirection, bool FormRegisteredActive, ProtectionAttestation? LatestAttestation);

public sealed record ProtectionCoverage(Guid OrganizationId, int RegisteredFormCount, int DecisionCount, int AttestedCompleteDecisionCount, bool OrganizationComplete, IReadOnlyList<ProtectionCoverageDecision> Decisions,
    long AuthorizationRevision, DateTimeOffset ComputedOn, string CoverageStatement, Guid CorrelationId);

/// <summary>
/// Phase C7 M4 (OD-1): the separate <c>field-protection-verifier</c> workload's view of the Authority — the complete migration
/// catalogue, the census and the attestation. Its credential is configured only for the Worker and registered only by the
/// Worker host; the business API never resolves this port, so it can never enumerate an organization's records. The port
/// can verify, count and attest; it cannot write content or change any protection decision.
/// </summary>
public interface IProtectionVerifierAuthority
{
    /// <summary>False when this deployment has no verifier credential: reconciliation then reports itself unavailable (fail closed).</summary>
    bool IsConfigured { get; }

    Task<MigrationCataloguePage> IssueMigrationCatalogueAsync(Guid organizationId, Guid formDefinitionId, Guid securityCommandId, CancellationToken cancellationToken);

    Task<MigrationCataloguePage> ReadMigrationCatalogueAsync(Guid searchCatalogueSnapshotId, int offset, CancellationToken cancellationToken);

    /// <summary><paramref name="reverify"/>: verify the rows even when the Authority already holds a census of the same accepted operation (first round of every reconciliation).</summary>
    Task<ProtectionCensusResponse> TakeCensusAsync(Guid searchCatalogueSnapshotId, IReadOnlyList<ProtectionCensusItem> records, bool reverify, CancellationToken cancellationToken);

    Task<ProtectionAttestation> AttestAsync(Guid searchCatalogueSnapshotId, CancellationToken cancellationToken);

    Task<ProtectionCoverage> GetCoverageAsync(Guid organizationId, CancellationToken cancellationToken);
}
