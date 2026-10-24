namespace AuthorizationAuthority.Contracts.V1;

// Phase C7 M2 — AC-6 Authority-held field search policy (C-56 D2, C-60 OD-2/OD-3/OD-4/OD-7/OD-8). Snake_case on the wire.

/// <summary>
/// OD-3: the Authority's record of one published form version, sent by the business right after it publishes. The layout
/// digest is SHA-256 of the canonical layout JSON (digest scheme <c>layout-digest/v1</c>); each field entry is what the
/// designer published for it. Approvals bind to these records, never to later business rows.
/// </summary>
public sealed record RegisterPublishedFormVersionPayload(Guid FormDefinitionId, int FormVersionNumber, string LayoutDigestHex, IReadOnlyList<PublishedFieldEntry> Fields);

public sealed record PublishedFieldEntry(Guid FieldDefinitionId, string FieldDataType, string FieldClassification, string ProtectionMode, string FieldLabel, IReadOnlyList<PublishedOptionEntry>? Options = null);

public sealed record PublishedOptionEntry(Guid FieldOptionId, string OptionLabel);

/// <summary>
/// AC-6: approve plaintext value search of one field for the given operators. Only <c>administer_security</c>; requires
/// step-up evidence the Authority accepts (none in production or staging until PB-08); <c>step_up_evidence</c> is the
/// development-only synthetic marker where that path is enabled.
/// </summary>
public sealed record ApproveFieldSearchPayload(Guid FormDefinitionId, Guid FieldDefinitionId, IReadOnlyList<string> Operators, string ApprovalReference, string? StepUpEvidence = null);

public sealed record WithdrawFieldSearchPayload(Guid FormDefinitionId, Guid FieldDefinitionId, string WithdrawalReason);

/// <summary>OD-4: grants or revokes the per-form <c>field_searcher</c> relation (query permission). Grants nothing else.</summary>
public sealed record UpdateFieldSearchersPayload(Guid FormDefinitionId, IReadOnlyList<Guid> GrantUserIds, IReadOnlyList<Guid> GrantTeamIds, IReadOnlyList<Guid> RevokeUserIds, IReadOnlyList<Guid> RevokeTeamIds);

public sealed record FieldSearchCapabilitiesRequest(Guid OrganizationId, Guid FormDefinitionId);

/// <summary>An option the approval froze (id and label at approval time).</summary>
public sealed record FieldSearchOption(Guid FieldOptionId, string OptionLabel);

/// <summary>A field the caller may query: label, type, operators and options from the Authority's approval snapshot.</summary>
public sealed record SearchableFieldCapability(Guid FieldDefinitionId, string FieldLabel, string FieldDataType, IReadOnlyList<string> Operators, IReadOnlyList<FieldSearchOption> Options, int ApprovedFormVersionNumber);

/// <summary>
/// Configuration view of one field of the latest registered version (only for callers with <c>design</c> on the form or
/// <c>administer_security</c>). <c>Eligibility</c>: Eligible, Encrypted, ClassificationNotPermitted, OrganizationSearchDisabled,
/// ProtectionPending, TypeNotSearchable, OrganizationPolicyMissing. <c>ApprovalState</c>: None, Active, Withdrawn, Invalidated.
/// </summary>
public sealed record FieldSearchStatus(Guid FieldDefinitionId, string FieldLabel, string FieldDataType, string FieldClassification, string ProtectionMode, string Eligibility,
    string ApprovalState, string? InvalidationReason, IReadOnlyList<string> ApprovedOperators, IReadOnlyList<string> PermittedOperators);

/// <remarks><c>FieldSearchers</c>: who holds <c>field_searcher</c> on the form — returned only to security administrators (who alone may change it), otherwise null.</remarks>
public sealed record FieldSearchConfiguration(bool CanApprove, bool ApprovalOperational, string ApprovalUnavailableReason, int? RegisteredFormVersionNumber, IReadOnlyList<FieldSearchStatus> Fields,
    IReadOnlyList<FormAccessSubject>? FieldSearchers = null);

/// <summary>
/// Capabilities for one form. <c>Fields</c> is empty unless the caller holds <c>query_fields</c> on the form and approvals
/// exist; a caller with no access gets the same answer as for a form that does not exist. <c>Configuration</c> is null
/// unless the caller may see field search configuration.
/// </summary>
public sealed record FieldSearchCapabilitiesResponse(Guid FormDefinitionId, bool PlaintextSearchPermitted, long SearchPolicyRevision, long AuthorizationRevision,
    IReadOnlyList<SearchableFieldCapability> Fields, FieldSearchConfiguration? Configuration, Guid CorrelationId);
