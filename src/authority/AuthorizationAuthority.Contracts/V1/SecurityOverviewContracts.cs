namespace AuthorizationAuthority.Contracts.V1;

// ---- C8 M4-4 (AC-8.4, AC-8.5; PHASE_C8_M4_AC8_SECURITY_CONTRACT.md §5.4, §5.5) ----
// Counts, codes, kinds, revisions and times only. No payload, target, record id, field key or label, note or free text.

/// <summary>The organization is the one the delegated user names; the Authority binds the user to it (unbound ⇒ 404).</summary>
public sealed record SecurityOverviewRequest(Guid OrganizationId, Guid CorrelationId);

/// <summary>
/// One section of the AC-8.4 security overview. Exactly one of <see cref="Gate"/>, <see cref="Commands"/> and
/// <see cref="Archive"/> is set, matching <see cref="Section"/>. Authorized by the Effective-edge rule (§5.4) from one
/// read-only AuthorityDb snapshot, so it is available while the organization gate is closed.
/// </summary>
public sealed record SecurityOverviewResponse(
    Guid OrganizationId,
    string Section,
    string AuthorizationBasis,
    DateTimeOffset ComputedOn,
    SecurityOverviewGate? Gate,
    SecurityOverviewCommands? Commands,
    SecurityOverviewArchive? Archive,
    Guid CorrelationId);

/// <param name="GateState">The organization gate as the Authority evaluates it (Ready, AwaitingProjection, AwaitingArchive, Quarantined).</param>
public sealed record SecurityOverviewGate(
    long AuthorizationRevision,
    long ProjectionAppliedRevision,
    long ContentIntegrityRevision,
    string GateState,
    bool IntegrityQuarantined);

/// <param name="StateCounts">One entry per unresolved state (Pending, CommittedAwaitingProjection, FailedReconciliation), zero included.</param>
/// <param name="OldestUnresolvedCreatedOn">Creation time of the oldest command in any unresolved state, or null when none.</param>
/// <param name="Commands">At most <see cref="ListLimit"/> unresolved commands, oldest first: kind, state, revision and times only.</param>
public sealed record SecurityOverviewCommands(
    IReadOnlyList<SecurityOverviewStateCount> StateCounts,
    DateTimeOffset? OldestUnresolvedCreatedOn,
    long? OldestUnresolvedAgeSeconds,
    IReadOnlyList<SecurityOverviewCommand> Commands,
    int ListLimit,
    string ListStatement);

public sealed record SecurityOverviewStateCount(string CommandState, int CommandCount);

public sealed record SecurityOverviewCommand(string CommandKind, string CommandState, long? CommittedAuthorizationRevision, DateTimeOffset CreatedOn, long AgeSeconds);

/// <param name="Streams">One entry per journal stream (authorization, content, audit), zero counts included.</param>
/// <param name="ArchiveStatement">What "Anchored" means locally (RR-6): never an immutability or production claim.</param>
public sealed record SecurityOverviewArchive(IReadOnlyList<SecurityOverviewArchiveStreamState> Streams, string ArchiveStatement);

public sealed record SecurityOverviewArchiveStreamState(
    string StreamName,
    int PendingCount,
    int AnchoredCount,
    int VerificationFailedCount,
    DateTimeOffset? OldestPendingCreatedOn,
    long? OldestPendingAgeSeconds,
    DateTimeOffset? LastAnchoredOn);

/// <summary>AC-8.5 coverage request (user plane, delegated Security Administrator).</summary>
public sealed record SecurityCoverageRequest(Guid OrganizationId, Guid CorrelationId);

/// <summary>
/// AC-8.5 (DR-C7-5): every current field protection decision of the organization (Authority protection resources, never the
/// business form list) with its latest Authority attestation, as counts. Never record ids, values or per-record rows. The
/// staleness of each attestation is an <b>organization-wide</b> comparison (OD-1): it is not a per-form verification result.
/// </summary>
public sealed record SecurityCoverageResponse(
    Guid OrganizationId,
    int RegisteredFormCount,
    int DecisionCount,
    int AttestedCompleteDecisionCount,
    int MissingDecisionCount,
    int StaleDecisionCount,
    long OrganizationContentIntegrityRevision,
    IReadOnlyList<SecurityCoverageDecision> Decisions,
    DateTimeOffset ComputedOn,
    string CoverageStatement,
    string StalenessStatement,
    string AuthorizationBasis,
    Guid CorrelationId);

/// <param name="AttestationState">
/// <c>Missing</c> (no attestation under this decision), <c>StaleOrganizationWide</c> (the organization's content-integrity
/// revision advanced after the attestation, anywhere in the organization), or <c>NoLaterOrganizationContentChange</c>.
/// </param>
public sealed record SecurityCoverageDecision(
    Guid SecurityCommandId,
    Guid FormDefinitionId,
    Guid FieldDefinitionId,
    string ProtectionDirection,
    bool FormRegisteredActive,
    string AttestationState,
    SecurityCoverageAttestation? Attestation);

public sealed record SecurityCoverageAttestation(
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

public static class SecurityOverviewSections
{
    public const string Gate = "gate";
    public const string Commands = "commands";
    public const string Archive = "archive";

    public static readonly IReadOnlyList<string> All = [Gate, Commands, Archive];
}

public static class SecurityCoverageAttestationStates
{
    public const string Missing = "Missing";
    public const string StaleOrganizationWide = "StaleOrganizationWide";
    public const string NoLaterOrganizationContentChange = "NoLaterOrganizationContentChange";
}
