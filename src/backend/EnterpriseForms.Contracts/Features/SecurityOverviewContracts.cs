namespace EnterpriseForms.Contracts.Features;

// C8 M4-4 (AC-8.4, AC-8.5; PG-052 Security Home). One section per response; exactly one of Gate, Commands, Archive and Coverage is
// set, matching Section. Counts, codes, kinds, revisions, times and identifiers only: no payload, target, record id, field key or
// label, note or free text. Read from the Authorization Authority's committed state; operational information, not verified evidence
// or a compliance conclusion.

/// <param name="Section">gate, commands, archive or coverage.</param>
/// <param name="AuthorizationBasis">How the Authority authorized the read (the Effective-edge rule; works while the gate is closed).</param>
public sealed record SecurityOverviewSectionResponse(
    string Section,
    string AuthorizationBasis,
    DateTimeOffset ComputedOn,
    SecurityOverviewGateResponse? Gate,
    SecurityOverviewCommandsResponse? Commands,
    SecurityOverviewArchiveResponse? Archive,
    SecurityCoverageResponse? Coverage);

/// <param name="GateState">Ready, AwaitingProjection, AwaitingArchive or Quarantined.</param>
public sealed record SecurityOverviewGateResponse(long AuthorizationRevision, long ProjectionAppliedRevision, long ContentIntegrityRevision, string GateState, bool IntegrityQuarantined);

/// <param name="StateCounts">Pending, CommittedAwaitingProjection and FailedReconciliation (OD-4), zero included.</param>
/// <param name="Commands">At most ListLimit unresolved commands, oldest first: kind, state, revision and times only.</param>
public sealed record SecurityOverviewCommandsResponse(
    IReadOnlyList<SecurityOverviewStateCountResponse> StateCounts,
    DateTimeOffset? OldestUnresolvedCreatedOn,
    long? OldestUnresolvedAgeSeconds,
    IReadOnlyList<SecurityOverviewCommandResponse> Commands,
    int ListLimit,
    string ListStatement);

public sealed record SecurityOverviewStateCountResponse(string CommandState, int CommandCount);

public sealed record SecurityOverviewCommandResponse(string CommandKind, string CommandState, long? CommittedAuthorizationRevision, DateTimeOffset CreatedOn, long AgeSeconds);

/// <param name="ArchiveStatement">What "Anchored" means locally (RR-6); never an immutability or production claim.</param>
public sealed record SecurityOverviewArchiveResponse(IReadOnlyList<SecurityOverviewArchiveStreamResponse> Streams, string ArchiveStatement);

public sealed record SecurityOverviewArchiveStreamResponse(string StreamName, int PendingCount, int AnchoredCount, int VerificationFailedCount,
    DateTimeOffset? OldestPendingCreatedOn, long? OldestPendingAgeSeconds, DateTimeOffset? LastAnchoredOn);

/// <param name="StalenessStatement">Staleness is an organization-wide comparison (OD-1), never a per-form verification result.</param>
/// <param name="FormNameStatement">Form names come from the business directory (OD-3); fields are identified by id only.</param>
public sealed record SecurityCoverageResponse(
    int RegisteredFormCount,
    int DecisionCount,
    int AttestedCompleteDecisionCount,
    int MissingDecisionCount,
    int StaleDecisionCount,
    long OrganizationContentIntegrityRevision,
    IReadOnlyList<SecurityCoverageDecisionResponse> Decisions,
    string CoverageStatement,
    string StalenessStatement,
    string FormNameStatement);

/// <param name="FormDisplayName">Business directory name of the form, or null when the directory has none.</param>
/// <param name="AttestationState">Missing, StaleOrganizationWide or NoLaterOrganizationContentChange.</param>
public sealed record SecurityCoverageDecisionResponse(
    Guid SecurityCommandId,
    Guid FormDefinitionId,
    string? FormDisplayName,
    Guid FieldDefinitionId,
    string ProtectionDirection,
    bool FormRegisteredActive,
    string AttestationState,
    SecurityCoverageAttestationResponse? Attestation);

public sealed record SecurityCoverageAttestationResponse(
    Guid AttestationId,
    bool AttestationComplete,
    string? AttestationIncompleteReasonCode,
    int ExpectedRecordCount,
    int AcceptedRecordCount,
    int CompliantRecordCount,
    int PendingCreationRecordCount,
    int QuarantinedRecordCount,
    int UnverifiedRecordCount,
    int NotYetCensusedRecordCount,
    int RetiredRecordCount,
    long RetainedHistoryRevisionCount,
    long AuditJournalRevision,
    long AttestedContentIntegrityRevision,
    DateTimeOffset AttestedOn);
