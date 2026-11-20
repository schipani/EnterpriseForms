namespace EnterpriseForms.Contracts.Features;

// ---- C8 M4-2: PG-054 audit explorer and PG-055 audit event detail (AC-8 §6). Security Auditors only (Authority view_audit);
// every page and detail is authorized and recorded by the Authority before any row is read (AC-8.3). Rows are the business
// audit projection, never independently verified (AUD-C7-01/G24 open). No record titles, values, file names, field keys or
// labels, free text or share scopes; record and field identifiers only. ----

/// <summary>Who performed the event: the local user id with its directory display name (D8-67a), and the actor type.</summary>
public sealed record AuditExplorerActorResponse(Guid? UserId, string? DisplayName, string ActorType);

public sealed record AuditExplorerTargetResponse(string? EntityType, Guid? EntityId);

/// <summary>
/// The event envelope. <c>ActionLabel</c> comes from the redaction catalog; <c>Recognized</c> is false for an action not in the
/// catalog (forged or newer), which is then shown as this envelope only. <c>FieldDefinitionId</c> is an id only (no key or label).
/// <c>Evidence</c> is always <c>BusinessProjection</c>.
/// </summary>
public sealed record AuditExplorerEventResponse(Guid AuditEventId, DateTimeOffset OccurredOn, long Sequence, string Category, string Action, string ActionLabel, bool Recognized, string Outcome,
    AuditExplorerActorResponse Actor, AuditExplorerTargetResponse Target, Guid? FormDefinitionId, string? FormDisplayName, Guid? FormRecordId, Guid? FieldDefinitionId, Guid CorrelationId, int SchemaVersion, string Evidence);

/// <summary>The Authority's evidence for this response: the audit-stream revision of its AuditAccessAuthorized entry. Delivery is business-reported.</summary>
public sealed record AuditAccessEvidenceResponse(long AuthorityJournalRevision, string Statement);

/// <summary>
/// One page, newest first by event time with the sequence as tie-break (not commit order). <c>NextCursor</c> is opaque and bound
/// to the caller, the organization and these filters; every page is authorized and recorded again.
/// </summary>
public sealed record AuditExplorerPageResponse(IReadOnlyList<AuditExplorerEventResponse> Items, string? NextCursor, int PageSize, string OrderingStatement, string EvidenceStatement, AuditAccessEvidenceResponse AccessEvidence);

/// <summary>An allowlisted metadata value. <c>Kind</c>: Identifier, Count, Status, Code or DirectoryName (no free text in M4).</summary>
public sealed record AuditMetadataItemResponse(string Key, string Label, string Kind, string Value);

/// <summary>A metadata item present but withheld by policy: its label and the policy only, never the value or its length.</summary>
public sealed record AuditWithheldMetadataResponse(string Label, string Policy);

/// <summary>A link to a normal application route that authorizes on its own (seeing the link grants nothing).</summary>
public sealed record AuditEventLinkResponse(string Relation, string Label, string Href);

/// <summary>Archive receipt of the business row: always <c>None</c> until AUD-C7-01/G24.</summary>
public sealed record AuditReceiptResponse(string State, string Reason);

public sealed record AuditEventDetailResponse(AuditExplorerEventResponse Event, string Description, IReadOnlyList<AuditMetadataItemResponse> Metadata, IReadOnlyList<AuditWithheldMetadataResponse> WithheldMetadata,
    IReadOnlyList<AuditEventLinkResponse> Links, AuditReceiptResponse Receipt, string OrderingStatement, string EvidenceStatement, AuditAccessEvidenceResponse AccessEvidence);
