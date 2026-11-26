namespace AuthorizationAuthority.Domain.Features.Lineage;

/// <summary>
/// C10 M1-WP10: classification outcomes for legacy shares in the lineage migration dry-run report (Section 5.1).
/// Each class corresponds to a specific scenario when evaluating existing shares that lack lineage tracking.
/// </summary>
public enum LegacyShareClassificationOutcome
{
    /// <summary>
    /// The share was registered by the record's owner (or an owning-team member) at registration time.
    /// Proposed outcome: depth 1, no parent.
    /// </summary>
    OwnerGranted = 1,

    /// <summary>
    /// The share was registered by a non-owner holding exactly one Active Manager share on the record
    /// whose current effective scope covers the share's items, role and documents.
    /// Proposed outcome: parent = that share, depth 2 (or 3 if parent is depth 2 and share is not Manager).
    /// </summary>
    UnambiguousParent = 2,

    /// <summary>
    /// More than one covering Manager share exists. The dry-run tool never resolves automatically; the owner must decide.
    /// Proposed outcome: listed for owner decision; invalidated only if owner chooses.
    /// </summary>
    MultipleCandidateParents = 3,

    /// <summary>
    /// The grantor holds no covering Manager share (lost, narrowed, never had).
    /// This is a verified non-owner classification where ownership evidence was verified.
    /// Proposed outcome: Invalidated with reason <c>LINEAGE_UNRESOLVABLE</c>.
    /// </summary>
    NoCoveringParent = 4,

    /// <summary>
    /// Assignment would place the share beyond depth 3, or place a Manager share at depth 3.
    /// Proposed outcome: Invalidated with reason <c>DELEGATION_DEPTH_EXCEEDED</c>.
    /// </summary>
    ExceedsDepth = 5,

    /// <summary>
    /// Historical evidence is insufficient for reliable classification. This includes:
    /// - Unknown/null grantor at registration time
    /// - Unverifiable historical ownership (IsVerifiable = false)
    /// - Ambiguous ownership (both user and team owner present)
    /// - Missing or invalid registration evidence
    ///
    /// This is NOT a verified non-owner outcome. No assignment or invalidation claim
    /// can be made on unverifiable inputs. The report builder propagates this
    /// as a refusal (<see cref="LegacyLineageClassificationReport.EvidenceRefusals"/>), not as a classification decision.
    /// </summary>
    InsufficientEvidence = 6
}

/// <summary>
/// C10 M1-WP10: annotation indicating the recipient also holds equivalent access through another independent grant.
/// </summary>
public enum IndependentCoverageAnnotation
{
    /// <summary>No independent coverage evaluated.</summary>
    NotEvaluated = 0,

    /// <summary>The recipient holds equivalent or broader access through another grant that would survive this share's invalidation.</summary>
    IndependentlyCovered = 1,

    /// <summary>The recipient has no other covering grant; invalidation would remove their access.</summary>
    NotCovered = 2
}

/// <summary>
/// C10 M1-WP10: a single classified share in the legacy lineage migration dry-run report.
/// Contains only opaque IDs, counts, enum outcomes and keyed pseudonyms; no PII.
/// </summary>
public sealed record LegacyShareClassificationEntry
{
    /// <summary>Opaque share identifier (the record_share_id).</summary>
    public required Guid RecordShareId { get; init; }

    /// <summary>Opaque record identifier (the form_record_id).</summary>
    public required Guid FormRecordId { get; init; }

    /// <summary>Opaque organization identifier.</summary>
    public required Guid OrganizationId { get; init; }

    /// <summary>Whether the recipient is a user or a team.</summary>
    public required RecipientKind RecipientKind { get; init; }

    /// <summary>
    /// Keyed pseudonymous reference for the recipient. Domain-separated with organization binding.
    /// Never contains names or email addresses.
    /// </summary>
    public required string RecipientPseudonymousReference { get; init; }

    /// <summary>
    /// Keyed pseudonymous reference for the grantor at registration time. Domain-separated with organization binding.
    /// Never contains names or email addresses. Null only for <see cref="LegacyShareClassificationOutcome.InsufficientEvidence"/>
    /// with <see cref="InsufficientEvidenceReason.UnknownGrantor"/>: the report never invents a grantor reference.
    /// </summary>
    public required string? GrantorPseudonymousReference { get; init; }

    /// <summary>The classification outcome.</summary>
    public required LegacyShareClassificationOutcome Classification { get; init; }

    /// <summary>Count of field items in the share's scope. No field names or values.</summary>
    public required int FieldItemCount { get; init; }

    /// <summary>Count of document items in the share's scope. No file names.</summary>
    public required int DocumentItemCount { get; init; }

    /// <summary>Candidate parent share IDs for <see cref="LegacyShareClassificationOutcome.MultipleCandidateParents"/> or <see cref="LegacyShareClassificationOutcome.UnambiguousParent"/>.</summary>
    public IReadOnlyList<Guid> CandidateParentShareIds { get; init; } = [];

    /// <summary>The proposed parent share ID for <see cref="LegacyShareClassificationOutcome.UnambiguousParent"/>.</summary>
    public Guid? ProposedParentShareId { get; init; }

    /// <summary>The proposed delegation depth (1 for owner-granted, 2 or 3 for delegated).</summary>
    public int? ProposedDelegationDepth { get; init; }

    /// <summary>The share's current role (reader, contributor, manager).</summary>
    public required string ShareRole { get; init; }

    /// <summary>Whether the recipient retains equivalent access through an independent grant.</summary>
    public IndependentCoverageAnnotation IndependentCoverage { get; init; } = IndependentCoverageAnnotation.NotEvaluated;

    /// <summary>Whether the share has an expiry time set.</summary>
    public bool HasExpiry { get; init; }

    /// <summary>Indicates the share was created before lineage tracking existed.</summary>
    public required bool IsLegacyShare { get; init; }

    /// <summary>
    /// When classification is <see cref="LegacyShareClassificationOutcome.InsufficientEvidence"/>,
    /// contains the structured validation error with privacy-safe reason.
    /// Null for all other classification outcomes.
    /// </summary>
    public LineageEvidenceValidationError? EvidenceValidationError { get; init; }
}

/// <summary>Identifies whether a share recipient is a user or a team.</summary>
public enum RecipientKind
{
    /// <summary>The recipient is an individual user.</summary>
    User = 1,

    /// <summary>The recipient is a team (access conveyed to all active team members).</summary>
    Team = 2
}

/// <summary>
/// C10 M1-WP10: reasons why historical evidence is insufficient for classification.
/// Privacy-safe: contains no PII, actor names, emails or specific field/document identifiers.
/// </summary>
public enum InsufficientEvidenceReason
{
    /// <summary>The grantor at registration time is unknown or null.</summary>
    UnknownGrantor = 1,

    /// <summary>Historical ownership evidence exists but is not verifiable (IsVerifiable = false).</summary>
    UnverifiableOwnership = 2,

    /// <summary>Both user and team ownership present - ambiguous ownership state.</summary>
    AmbiguousOwnership = 3,

    /// <summary>No historical ownership evidence is available (null OwnerInfoAtRegistration).</summary>
    MissingOwnershipEvidence = 4
}

/// <summary>
/// C10 M1-WP10: structured validation error for incomplete evidence in lineage classification.
/// Privacy-safe: contains only opaque share ID, organization ID and enum reason.
/// No actor names, emails, field names, document names or values.
/// </summary>
public sealed record LineageEvidenceValidationError
{
    /// <summary>Opaque share identifier.</summary>
    public required Guid RecordShareId { get; init; }

    /// <summary>Opaque organization identifier.</summary>
    public required Guid OrganizationId { get; init; }

    /// <summary>The reason evidence is insufficient.</summary>
    public required InsufficientEvidenceReason Reason { get; init; }

    /// <summary>Stable, privacy-safe refusal code.</summary>
    public string ErrorCode { get; } = LineageEvidenceErrorCodes.EvidenceIncomplete;
}

/// <summary>C10 M1-WP10: stable refusal codes of the legacy lineage dry run.</summary>
public static class LineageEvidenceErrorCodes
{
    /// <summary>Historical grantor or ownership evidence is missing, unverifiable or ambiguous; no outcome is proposed.</summary>
    public const string EvidenceIncomplete = "LINEAGE_EVIDENCE_INCOMPLETE";
}

/// <summary>
/// C10 M1-WP10: summary statistics for the legacy lineage classification report.
/// </summary>
public sealed record LegacyShareClassificationSummary
{
    /// <summary>Opaque organization identifier.</summary>
    public required Guid OrganizationId { get; init; }

    /// <summary>Total number of active shares evaluated.</summary>
    public required int TotalSharesEvaluated { get; init; }

    /// <summary>Shares classified as <see cref="LegacyShareClassificationOutcome.OwnerGranted"/>.</summary>
    public required int OwnerGrantedCount { get; init; }

    /// <summary>Shares classified as <see cref="LegacyShareClassificationOutcome.UnambiguousParent"/>.</summary>
    public required int UnambiguousParentCount { get; init; }

    /// <summary>Shares classified as <see cref="LegacyShareClassificationOutcome.MultipleCandidateParents"/>.</summary>
    public required int MultipleCandidateParentsCount { get; init; }

    /// <summary>Shares classified as <see cref="LegacyShareClassificationOutcome.NoCoveringParent"/>.</summary>
    public required int NoCoveringParentCount { get; init; }

    /// <summary>Shares classified as <see cref="LegacyShareClassificationOutcome.ExceedsDepth"/>.</summary>
    public required int ExceedsDepthCount { get; init; }

    /// <summary>Shares where the recipient is independently covered (access survives invalidation).</summary>
    public required int IndependentlyCoveredCount { get; init; }

    /// <summary>
    /// Shares classified as <see cref="LegacyShareClassificationOutcome.InsufficientEvidence"/>: refused, no proposed outcome.
    /// </summary>
    public required int InsufficientEvidenceCount { get; init; }
}

/// <summary>
/// C10 M1-WP10: the complete dry-run report output. Contains only opaque IDs, counts, enums and pseudonyms.
/// </summary>
public sealed record LegacyLineageClassificationReport
{
    /// <summary>The authorization revision at which this snapshot was taken (REPEATABLE READ).</summary>
    public required long AuthorizationRevisionRead { get; init; }

    /// <summary>The tool version that produced this report.</summary>
    public required string ToolVersion { get; init; }

    /// <summary>When the report was generated (UTC).</summary>
    public required DateTimeOffset GeneratedOn { get; init; }

    /// <summary>Per-organization summary statistics.</summary>
    public IReadOnlyList<LegacyShareClassificationSummary> Summaries { get; init; } = [];

    /// <summary>All classified share entries.</summary>
    public IReadOnlyList<LegacyShareClassificationEntry> Entries { get; init; } = [];

    /// <summary>
    /// The privacy-safe refusals of every share whose historical evidence was incomplete. Not empty means the evidence for
    /// this revision is incomplete: no assignment or invalidation is proposed for those shares, and a later execution
    /// (separately authorized, Section 5.1) must not proceed from this report.
    /// </summary>
    public IReadOnlyList<LineageEvidenceValidationError> EvidenceRefusals { get; init; } = [];

    /// <summary>True when every evaluated share had complete, verified historical evidence.</summary>
    public bool IsEvidenceComplete => EvidenceRefusals.Count == 0;
}
