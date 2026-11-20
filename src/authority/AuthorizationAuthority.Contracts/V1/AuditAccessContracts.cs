namespace AuthorizationAuthority.Contracts.V1;

// C8 M4-2 (AC-8.3, PHASE_C8_M4_AC8_SECURITY_CONTRACT.md §5.3): audit-access evidence. The business API asks the Authority to
// authorize one read of its audit projection (an explorer page or one event's detail). The Authority checks view_audit and
// commits an AuditAccessAuthorized entry in the organization's audit stream before it answers; only that answer lets the
// business serve audit data. The entry states authorization, never delivery, and carries no business-reported result count.

/// <summary>The kind of audit read being authorized.</summary>
public enum AuditAccessKind
{
    /// <summary>One page of the audit explorer (PG-054).</summary>
    ExplorerPage,

    /// <summary>The detail of one audit event (PG-055).</summary>
    EventDetail,
}

/// <param name="OrganizationId">The caller's organization (bound again to the delegated user by the Authority).</param>
/// <param name="AccessKind">Explorer page or event detail.</param>
/// <param name="FilterDigest">Lower-case hex SHA-256 (64 characters) of the canonical filter: ids, codes and dates only, never free text.</param>
/// <param name="PageSize">1–50 for an explorer page; 1 for an event detail.</param>
/// <param name="AuditEventId">The event of an EventDetail read; absent for an explorer page.</param>
/// <param name="CorrelationId">The business request's correlation id, recorded in the entry.</param>
public sealed record AuditAccessRequest(Guid OrganizationId, AuditAccessKind AccessKind, string FilterDigest, int PageSize, Guid? AuditEventId, Guid CorrelationId);

/// <param name="Allowed">Always true; a refusal is an error response (403, 429 or 503).</param>
/// <param name="AuditJournalRevision">Audit-stream revision of the committed AuditAccessAuthorized entry.</param>
/// <param name="AuthorizationRevision">Authorization revision R the view_audit decision was taken and recorded at.</param>
/// <param name="Delivery">Always "not_attested": the Authority records authorization, not delivery.</param>
/// <param name="CorrelationId">The correlation id recorded in the entry.</param>
public sealed record AuditAccessDecision(bool Allowed, long AuditJournalRevision, long AuthorizationRevision, string Delivery, Guid CorrelationId);
