namespace AuthorizationAuthority.Contracts.V1;

// C8 M4-3 (AC-8.2, PHASE_C8_M4_AC8_SECURITY_CONTRACT.md §5.2): the security-command timeline. The Authority checks view_audit,
// reads one page of its own command rows and allowlisted audit-stream entries inside the audit-access transaction (one
// AuthorityDb snapshot; not SpiceDB), commits an AuditAccessAuthorized entry (access_kind CommandTimelinePage, served_by
// authority, delivery not_attested) and only then answers. Every envelope is built field by field from an allowlist.

/// <param name="OrganizationId">The caller's organization (bound again to the delegated user by the Authority).</param>
/// <param name="After">The Authority's keyset position from the previous page (opaque; the business wraps it in its own protected cursor).</param>
/// <param name="PageSize">1–50.</param>
/// <param name="Kinds">Optional: only these timeline kinds (each must be a classified, included kind).</param>
/// <param name="States">Optional: only command rows in these states (audit-stream entries have no state and are then omitted).</param>
/// <param name="From">Optional inclusive lower bound of the event time (UTC).</param>
/// <param name="To">Optional exclusive upper bound of the event time (UTC).</param>
/// <param name="CorrelationId">The business request's correlation id, recorded in the access entry.</param>
public sealed record SecurityCommandTimelineRequest(Guid OrganizationId, string? After, int PageSize, IReadOnlyList<string>? Kinds, IReadOnlyList<string>? States, DateTimeOffset? From, DateTimeOffset? To, Guid CorrelationId);

/// <param name="Items">Newest first: (event time, source, key) descending.</param>
/// <param name="After">The keyset position for the next page, or null on the last page.</param>
/// <param name="AuditJournalRevision">Audit-stream revision of the committed AuditAccessAuthorized entry for this page.</param>
/// <param name="AuthorizationRevision">Authorization revision R the view_audit decision was taken and recorded at.</param>
/// <param name="ServedBy">Always "authority".</param>
/// <param name="Delivery">Always "not_attested": the Authority records the authorized read, not the client's receipt.</param>
public sealed record SecurityCommandTimelinePage(IReadOnlyList<SecurityCommandTimelineEntry> Items, string? After, long AuditJournalRevision, long AuthorizationRevision, string ServedBy, string Delivery);

/// <param name="Source">"Command" (a security-command row) or "AuditStream" (an allowlisted audit-stream entry).</param>
/// <param name="Kind">The command kind or the audit-stream event kind.</param>
/// <param name="OccurredOn">Command creation time, or the journal entry time.</param>
/// <param name="CommandId">The security command (command rows; audit entries that name one).</param>
/// <param name="CommandState">Pending, CommittedAwaitingProjection, Effective, Rejected or FailedReconciliation (command rows only).</param>
/// <param name="CommittedAuthorizationRevision">The revision a committed command produced.</param>
/// <param name="RejectionCode">The structured rejection code of a Rejected command (never a message).</param>
/// <param name="ActorUserId">The acting user (command rows: the actor binding's user).</param>
/// <param name="EffectiveOn">When the command became Effective.</param>
/// <param name="EvidenceBasis">
/// "JournalBacked": a journal entry naming this item was found (its stream, revision, digest and archive state are given). For a
/// command item this does not verify the command row's fields (kind, state, revision, details), which are read from the mutable
/// command row, and the read does not re-verify the journal chain.
/// "CommandRowOnly": only the AuthorityDb command row exists (for example most rejected commands, which are not journaled); this is
/// expected for such rows and is not a sign of tampering, and a command row itself is not cryptographically verified.
/// </param>
/// <param name="ReportedBy">"Authority", or "BusinessRuntime" for evidence recorded on a business-runtime report (EnvelopeCapabilityRefused, PD-09).</param>
/// <param name="Evidence">The journal entry backing the item, when EvidenceBasis is JournalBacked.</param>
/// <param name="Details">The kind's allowlisted fields, each shape-checked; anything else is never returned.</param>
/// <param name="WithheldFields">Names of fields present but withheld by policy (never their values or lengths).</param>
public sealed record SecurityCommandTimelineEntry(
    string Source,
    string Kind,
    DateTimeOffset OccurredOn,
    Guid? CommandId,
    string? CommandState,
    long? CommittedAuthorizationRevision,
    string? RejectionCode,
    Guid? ActorUserId,
    DateTimeOffset? EffectiveOn,
    string EvidenceBasis,
    string ReportedBy,
    SecurityCommandTimelineEvidence? Evidence,
    IReadOnlyList<SecurityCommandTimelineDetail> Details,
    IReadOnlyList<string> WithheldFields);

/// <param name="Stream">authorization or audit.</param>
/// <param name="StreamRevision">The entry's revision in its stream.</param>
/// <param name="EntryDigestHex">The entry's hash-chain digest (lower-case hex).</param>
/// <param name="ArchiveState">Pending, Anchored or VerificationFailed (local archive delivery state, RR-6).</param>
/// <param name="ReceiptReferencePresent">Whether an archive receipt reference is recorded (the reference itself is not returned).</param>
public sealed record SecurityCommandTimelineEvidence(string Stream, long StreamRevision, string EntryDigestHex, string ArchiveState, bool ReceiptReferencePresent);

/// <param name="Key">The allowlisted field name (snake_case).</param>
/// <param name="Kind">Identifier, IdentifierList, Principal, PrincipalList, Code, CodeList, Count, Flag, Timestamp or Digest.</param>
/// <param name="Value">The single value (Identifier, Principal, Code, Count, Flag, Timestamp, Digest).</param>
/// <param name="Values">The values of a list kind (at most 200).</param>
public sealed record SecurityCommandTimelineDetail(string Key, string Kind, string? Value, IReadOnlyList<string>? Values);
