namespace AuthorizationAuthority.Contracts.V1;

// Phase C7 M2 — AC-6 Authority-held field search policy (C-56 D2, C-60 OD-2/OD-3/OD-4/OD-7/OD-8). Snake_case on the wire.

/// <summary>
/// OD-3: the Authority's record of one published form version, sent by the business right after it publishes. The layout
/// digest is SHA-256 of the canonical layout JSON (digest scheme <c>layout-digest/v1</c>); each field entry is what the
/// designer published for it. Approvals bind to these records, never to later business rows.
/// </summary>
/// <remarks>Phase C7 M3: <c>FormTitle</c> (the published layout title, shown to security administrators) is recorded with the version.</remarks>
public sealed record RegisterPublishedFormVersionPayload(Guid FormDefinitionId, int FormVersionNumber, string LayoutDigestHex, IReadOnlyList<PublishedFieldEntry> Fields, string? FormTitle = null);

/// <remarks>Phase C7 M3: <c>InRepeatingGroup</c> — the field is placed inside a repeating section; such fields are not searchable in V1.</remarks>
public sealed record PublishedFieldEntry(Guid FieldDefinitionId, string FieldDataType, string FieldClassification, string ProtectionMode, string FieldLabel, IReadOnlyList<PublishedOptionEntry>? Options = null, bool InRepeatingGroup = false);

public sealed record PublishedOptionEntry(Guid FieldOptionId, string OptionLabel);

/// <summary>
/// C-188 BF-1: sent by the business publish request <b>before</b> it commits the version (same entries as
/// <see cref="RegisterPublishedFormVersionPayload"/>, plus the business FormVersionId). The Authority stages it as the form's pending
/// publication; until it is resolved no field search approval of the form confers search. Another prepare of the form is refused until it is resolved (C-191).
/// </summary>
public sealed record PrepareFormVersionPublicationPayload(Guid FormDefinitionId, Guid FormVersionId, int FormVersionNumber, string LayoutDigestHex, IReadOnlyList<PublishedFieldEntry> Fields, string? FormTitle = null);

/// <summary>
/// C-188 BF-1: resolves the form's pending publication, which must be exactly this one (version id, number and layout digest).
/// <c>Resolution</c> <c>Registered</c>: the business committed it - the Authority registers the manifest it staged at prepare time (never
/// one rebuilt later from business rows, OD-3). <c>Abandoned</c>: the business never committed it - the pending state ends, nothing is
/// registered. Either way field search on the form resumes from the Authority's latest registered version.
/// </summary>
public sealed record ResolveFormVersionPublicationPayload(Guid FormDefinitionId, Guid FormVersionId, int FormVersionNumber, string LayoutDigestHex, string Resolution);

/// <summary>C-188 BF-1: the form's pending publication as shown to designers and security administrators (configuration view).</summary>
public sealed record PendingFormVersionPublication(Guid FormVersionId, int FormVersionNumber, string LayoutDigestHex, DateTimeOffset PreparedOn);

/// <summary>
/// AC-6: approve plaintext value search of one field for the given operators. Only <c>administer_security</c>; requires
/// step-up evidence the Authority accepts (none in production or staging until PB-08); <c>step_up_evidence</c> is the
/// development-only synthetic marker where that path is enabled.
/// </summary>
/// <remarks>C-188: <c>ApprovalScope</c> null or <c>CurrentVersion</c> approves a field of the latest registered version (unchanged
/// behaviour); <c>RetainedField</c> approves searching the latest retained values of a field that is no longer in the form, bound to
/// the most recent registered version that placed it.</remarks>
public sealed record ApproveFieldSearchPayload(Guid FormDefinitionId, Guid FieldDefinitionId, IReadOnlyList<string> Operators, string ApprovalReference, string? StepUpEvidence = null, string? ApprovalScope = null);

public sealed record WithdrawFieldSearchPayload(Guid FormDefinitionId, Guid FieldDefinitionId, string WithdrawalReason);

/// <summary>OD-4: grants or revokes the per-form <c>field_searcher</c> relation (query permission). Grants nothing else.</summary>
public sealed record UpdateFieldSearchersPayload(Guid FormDefinitionId, IReadOnlyList<Guid> GrantUserIds, IReadOnlyList<Guid> GrantTeamIds, IReadOnlyList<Guid> RevokeUserIds, IReadOnlyList<Guid> RevokeTeamIds);

public sealed record FieldSearchCapabilitiesRequest(Guid OrganizationId, Guid FormDefinitionId);

/// <summary>An option the approval froze (id and label at approval time).</summary>
public sealed record FieldSearchOption(Guid FieldOptionId, string OptionLabel);

/// <summary>A field the caller may query: label, type, operators and options from the Authority's approval snapshot.</summary>
/// <remarks>C-188: <c>InCurrentVersion</c> false for a field searched under a retained-field approval ("No longer in the form").</remarks>
public sealed record SearchableFieldCapability(Guid FieldDefinitionId, string FieldLabel, string FieldDataType, IReadOnlyList<string> Operators, IReadOnlyList<FieldSearchOption> Options, int ApprovedFormVersionNumber, bool InCurrentVersion = true);

/// <summary>
/// Configuration view of one field of the latest registered version (only for callers with <c>design</c> on the form or
/// <c>administer_security</c>). <c>Eligibility</c>: Eligible, Encrypted, ClassificationNotPermitted, OrganizationSearchDisabled,
/// ProtectionPending, TypeNotSearchable, OrganizationPolicyMissing. <c>ApprovalState</c>: None, Active, Withdrawn, Invalidated.
/// </summary>
/// <remarks>C-188: <c>InCurrentVersion</c> false for a field of an earlier registered version that the latest version no longer places
/// (described from <c>LastPlacedVersionNumber</c>'s entry); <c>ApprovalScope</c> is the scope of the latest approval (null when none).</remarks>
public sealed record FieldSearchStatus(Guid FieldDefinitionId, string FieldLabel, string FieldDataType, string FieldClassification, string ProtectionMode, string Eligibility,
    string ApprovalState, string? InvalidationReason, IReadOnlyList<string> ApprovedOperators, IReadOnlyList<string> PermittedOperators,
    bool InCurrentVersion = true, int? LastPlacedVersionNumber = null, string? ApprovalScope = null);

/// <remarks><c>FieldSearchers</c>: who holds <c>field_searcher</c> on the form — returned only to security administrators (who alone may change it), otherwise null.</remarks>
/// <remarks>C-188: <c>RetainedFieldApprovalsEnabled</c> — whether this Authority accepts and honours retained-field approvals (FieldSearch:RetainedFieldApprovals).</remarks>
/// <remarks>C-188 BF-1: <c>PendingPublication</c> - the publication staged and not yet resolved (null when none); while it is set no approval of
/// the form confers search and new approvals are refused.</remarks>
public sealed record FieldSearchConfiguration(bool CanApprove, bool ApprovalOperational, string ApprovalUnavailableReason, int? RegisteredFormVersionNumber, IReadOnlyList<FieldSearchStatus> Fields,
    IReadOnlyList<FormAccessSubject>? FieldSearchers = null, bool RetainedFieldApprovalsEnabled = false, PendingFormVersionPublication? PendingPublication = null);

/// <summary>
/// Capabilities for one form. <c>Fields</c> is empty unless the caller holds <c>query_fields</c> on the form and approvals
/// exist; a caller with no access gets the same answer as for a form that does not exist. <c>Configuration</c> is null
/// unless the caller may see field search configuration.
/// </summary>
/// <remarks>C-188 BF-1: <c>PublicationPending</c> - a publication of the form awaits registration, so <c>Fields</c> is empty (field search
/// fails closed). Reported only to callers who may know the form (otherwise false, the same answer as for an unknown form).</remarks>
public sealed record FieldSearchCapabilitiesResponse(Guid FormDefinitionId, bool PlaintextSearchPermitted, long SearchPolicyRevision, long AuthorizationRevision,
    IReadOnlyList<SearchableFieldCapability> Fields, FieldSearchConfiguration? Configuration, Guid CorrelationId, bool PublicationPending = false);

// ---- Phase C7 M3: search-approval administration for security administrators (no Designer or record access needed) ----

public sealed record FieldSearchAdministrationRequest(Guid OrganizationId);

/// <summary>
/// One form with an Authority-registered published version, as a security administrator needs it to administer search:
/// the title and field facts recorded at registration and approval counts. Never record contents.
/// </summary>
/// <remarks>C-188 (D-H8): <c>RetainedApprovalCount</c> — Active approvals of fields no longer in the form (the review inventory).</remarks>
public sealed record FieldSearchAdministrationForm(Guid FormDefinitionId, string? FormTitle, int RegisteredFormVersionNumber, DateTimeOffset RegisteredOn, int FieldCount, int EligibleFieldCount, int ActiveApprovalCount, int InvalidatedApprovalCount, int RetainedApprovalCount = 0);

/// <summary>Only for <c>administer_security</c>; forms of the caller's organization whose form resource is Active.</summary>
public sealed record FieldSearchAdministrationResponse(bool PlaintextSearchPermitted, bool ApprovalOperational, string ApprovalUnavailableReason, IReadOnlyList<FieldSearchAdministrationForm> Forms, long AuthorizationRevision, Guid CorrelationId);

// ---- Security Administrators (owner message 7). "Security Administrator" = organization relation system_administrator
// (permission administer_security); appointed and removed by GrantSystemRole / RevokeSystemRole security commands. ----

/// <summary>Payload of GrantSystemRole / RevokeSystemRole. <c>RoleName</c> must be <c>system_administrator</c>.</summary>
public sealed record SecurityAdministratorPayload(Guid AuthorityUserId, string RoleName, string Reason, string? StepUpEvidence = null);

/// <summary>
/// C8 M4 (AC-8.1, C-110): payload of GrantSystemRole / RevokeSystemRole when <c>RoleName</c> is <c>security_auditor</c>.
/// <c>ReasonCode</c> is required and must be one of <see cref="SecurityAuditorReasonCodes"/> for the command's direction
/// (exact, case-sensitive). A plaintext <c>ReasonNote</c> is refused permanently (<c>REASON_NOTE_NOT_ACCEPTED</c>, D8-72);
/// an optional note arrives only as <c>ReasonNoteEnvelope</c>, sealed by the business API (C8 M4 M1, Design A revision 2):
/// the Authority validates its structure and stores ciphertext only, holding no key. Free-text <c>reason</c> is not accepted
/// for this role (strict binding rejects it).
/// </summary>
public sealed record SecurityAuditorPayload(Guid AuthorityUserId, string RoleName, string ReasonCode, string? ReasonNote = null, string? StepUpEvidence = null, AuditorReasonNoteEnvelope? ReasonNoteEnvelope = null);

/// <summary>
/// C8 M4 M1 (Design A revision 2 §1.4): a Security Auditor reason note sealed with AES-256-GCM by the business API. The
/// authenticated context (organization, target, role, direction, note id) is bound in the AAD; organization, target, role and
/// direction come from the command, only the note id from the envelope. Nothing in the product decrypts it (D8-73).
/// </summary>
public sealed record AuditorReasonNoteEnvelope(
    string EnvelopeFormat,
    int EnvelopeVersion,
    string AlgorithmName,
    string KeyReference,
    string NoteId,
    string NonceB64,
    string CiphertextB64,
    string TagB64,
    string WrappedDataKeyB64);

/// <summary>C8 M4 (C-110): the owner-approved, closed reason codes for Security Auditor appointment and removal. No "Other".</summary>
public static class SecurityAuditorReasonCodes
{
    public const string AuditCoverageAssignment = "AuditCoverageAssignment";
    public const string InvestigationSupport = "InvestigationSupport";
    public const string ReplacementOfDepartingAuditor = "ReplacementOfDepartingAuditor";
    public const string ComplianceReview = "ComplianceReview";

    public const string AssignmentEnded = "AssignmentEnded";
    public const string RoleChange = "RoleChange";
    public const string MembershipEnding = "MembershipEnding";
    public const string ConflictOfInterest = "ConflictOfInterest";
    public const string SecurityConcern = "SecurityConcern";

    public const int MaximumNoteLength = 500;

    public static readonly IReadOnlyList<string> Appointment = [AuditCoverageAssignment, InvestigationSupport, ReplacementOfDepartingAuditor, ComplianceReview];

    public static readonly IReadOnlyList<string> Removal = [AssignmentEnded, RoleChange, MembershipEnding, ConflictOfInterest, SecurityConcern];

    /// <summary>Exact ordinal match against the list for the direction (appointment or removal).</summary>
    public static bool IsValid(string? code, bool appointment) => code is not null && (appointment ? Appointment : Removal).Contains(code, StringComparer.Ordinal);
}

public sealed record SecurityAdministratorRosterRequest(Guid OrganizationId);

/// <summary>A current Security Administrator: verified identity binding and the command that appointed them.</summary>
public sealed record SecurityAdministratorEntry(Guid AuthorityUserId, string IssuerUri, string SubjectIdentifier, DateTimeOffset AppointedOn, Guid AppointedBySecurityCommandId);

/// <summary>An enabled, verified identity of the organization that could be appointed.</summary>
public sealed record SecurityAdministratorCandidate(Guid AuthorityUserId, string IssuerUri, string SubjectIdentifier);

/// <summary>
/// The organization's Security Administrators, for its own Security Administrators and organization owners only.
/// <c>CanManage</c>: the caller holds administer_security; <c>Candidates</c> only then. <c>AppointmentOperational</c> false
/// where the Authority cannot accept the step-up (production and staging until PB-08).
/// </summary>
public sealed record SecurityAdministratorRoster(bool CanManage, bool AppointmentOperational, string AppointmentUnavailableReason, IReadOnlyList<SecurityAdministratorEntry> Administrators,
    IReadOnlyList<SecurityAdministratorCandidate>? Candidates, long AuthorizationRevision, Guid CorrelationId);
