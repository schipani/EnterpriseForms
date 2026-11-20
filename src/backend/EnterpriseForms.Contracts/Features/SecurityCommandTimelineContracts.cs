namespace EnterpriseForms.Contracts.Features;

// C8 M4-3 (AC-8.2): the security-command timeline tab of PG-054. Every envelope is built by the Authorization Authority field by
// field from an allowlist; free text, notes, step-up evidence, share scopes, identity identifiers, rejection details and matched or
// evaluated counts are never present. WithheldFields names withheld fields only (never their values or lengths).

/// <param name="Items">Newest first.</param>
/// <param name="NextCursor">Opaque, bound to the caller, organization and filters; null on the last page.</param>
public sealed record SecurityCommandTimelinePageResponse(
    IReadOnlyList<SecurityCommandTimelineItemResponse> Items,
    string? NextCursor,
    int PageSize,
    string OrderingStatement,
    string EvidenceStatement,
    AuditAccessEvidenceResponse AccessEvidence);

/// <param name="Source">"Command" (a security-command row) or "AuditStream" (an allowlisted audit-stream entry).</param>
/// <param name="EvidenceBasis">"JournalBacked" (a journal entry naming the item was found; a command item's fields are still read from the unverified command row and are not checked against that entry) or "CommandRowOnly" (expected for most rejected commands; not a sign of tampering).</param>
/// <param name="ReportedBy">"Authority", or "BusinessRuntime" for evidence recorded on a business-runtime report.</param>
public sealed record SecurityCommandTimelineItemResponse(
    string Source,
    string Kind,
    DateTimeOffset OccurredOn,
    Guid? CommandId,
    string? CommandState,
    long? CommittedAuthorizationRevision,
    string? RejectionCode,
    Guid? ActorUserId,
    string? ActorDisplayName,
    DateTimeOffset? EffectiveOn,
    string EvidenceBasis,
    string ReportedBy,
    SecurityCommandTimelineEvidenceResponse? Evidence,
    IReadOnlyList<SecurityCommandTimelineDetailResponse> Details,
    IReadOnlyList<string> WithheldFields);

/// <param name="ArchiveState">Pending, Anchored or VerificationFailed (local archive delivery state).</param>
public sealed record SecurityCommandTimelineEvidenceResponse(string Stream, long StreamRevision, string EntryDigestHex, string ArchiveState, bool ReceiptReferencePresent);

/// <param name="Kind">Identifier, IdentifierList, Principal, PrincipalList, Code, CodeList, Count, Flag, Timestamp or Digest.</param>
/// <param name="DisplayName">The organization directory name of an Identifier that is a member's user id (D8-67a), else null.</param>
public sealed record SecurityCommandTimelineDetailResponse(string Key, string Kind, string? Value, IReadOnlyList<string>? Values, string? DisplayName);
