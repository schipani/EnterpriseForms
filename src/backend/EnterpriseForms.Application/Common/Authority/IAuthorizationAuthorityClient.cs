using System.Text.Json;

namespace EnterpriseForms.Application.Common.Authority;

// Port for the independent Authorization Authority (trust_authority/trust-authority.openapi.yaml, Stage A revision).
// The only implementation is an authenticated internal HTTP adapter in Infrastructure. It never derives decisions from
// business PostgreSQL owner/share/team rows and exposes no generic signing method. DTO shapes mirror the private
// contract; the authority re-validates identity, tenant and every revision independently.

public enum ProtectedResourceKind
{
    FormDefinition,
    FormRecord,
    FieldItem,
    DocumentItem,
    Team,
    Organization,
}

public enum ProtectedAction
{
    Discover,
    Read,
    Edit,
    Download,
    Replace,
    Delegate,
    ManageAccess,
    TransferOwnership,
    DesignForm,
    CreateRecord,
    ManageOrganization,

    /// <summary>Organization security administration (system administrators); Amendment A1 governance routes accept it alongside ManageOrganization.</summary>
    AdministerSecurity,

    /// <summary>C8 M4 (AC-8): read organization audit (Authority <c>view_audit</c>: Security Auditors). Grants no record content.</summary>
    ViewAudit,
}

public enum SecurityCommandKind
{
    RegisterTeam,
    AddTeamMember,
    RemoveTeamMember,
    RegisterFormDefinition,
    RegisterRecord,
    TransferOwnership,
    CreateShare,
    UpdateShareScope,
    RevokeShare,
    GrantSystemRole,
    RevokeSystemRole,
    UpdateMandatoryPolicy,
    PublishFormSecurityRevision,
    UnlinkChildRecord,
    UpdateFormUsers,
    ReclassifyField,
    RegisterPublishedFormVersion,
    ApproveFieldSearch,
    WithdrawFieldSearch,
    UpdateFieldSearchers,
    GrantOrganizationRole,
    RevokeOrganizationRole,
    ProposeOrganizationOwnerChange,
    DisableIdentityBinding,
    EnableIdentityBinding,
    RequestOrganizationMembership,

    /// <summary>C8 M2 (AC-7.5): cancel a Pending membership request.</summary>
    CancelOrganizationMembershipRequest,

    /// <summary>C8 M2 (AC-7.5): edit a Pending request by replacing it with a new request.</summary>
    ReplaceOrganizationMembershipRequest,

    /// <summary>C-188 BF-1: record the version about to be committed (before the business commit); field search on the form fails closed until resolved.</summary>
    PrepareFormVersionPublication,

    /// <summary>C-188 BF-1: resolve the pending publication (Registered from the Authority's staged manifest, or Abandoned when never committed).</summary>
    ResolveFormVersionPublication,
}

public enum SecurityCommandStatus
{
    Pending,
    CommittedAwaitingProjection,
    Effective,
    Rejected,
    FailedReconciliation,
}

public enum ManifestItemKind
{
    RecordHeader = 1,
    RepeatedGroup = 2,
    FieldValue = 3,
    FieldSelection = 4,
    Attachment = 5,
    AttachmentLifecycle = 6,
}

public sealed record ResourceReference(ProtectedResourceKind ResourceKind, Guid ResourceId);

public sealed record PermissionCheckRequest(Guid OrganizationId, ResourceReference Resource, ProtectedAction RequestedAction, Guid? UnlockReference = null, long? MinimumContentRevision = null);

public sealed record PermissionDecision(bool Allowed, string? DenialReasonCode, long AuthorizationRevision, long ProjectionRevision, long? ContentRevision, Guid CorrelationId);

public sealed record SecurityCommandRequest(Guid OrganizationId, SecurityCommandKind CommandKind, ResourceReference Resource, long ExpectedAuthorizationRevision, JsonElement CommandPayload, string? ChangeReason = null, Guid? ApprovalReference = null);

public sealed record SecurityCommandResult(Guid CommandId, SecurityCommandStatus CommandStatus, long AuthorizationRevision, long? ProjectionRevision, string ProjectionGateState, string? RejectionReasonCode, Guid CorrelationId);

public sealed record CandidateContentItem(ManifestItemKind ItemKind, Guid ItemId, string CanonicalBytesBase64);

/// <summary>ReprotectionCommandId + CurrentItems: a workload reprotection operation (no delegated user) under an effective PublishFormSecurityRevision command; null for user operations.</summary>
public sealed record PrepareContentChangeRequest(Guid OrganizationId, Guid FormRecordId, Guid OperationId, long ExpectedAuthorizationRevision, long ExpectedContentRevision, string ExpectedManifestRootBase64, IReadOnlyList<CandidateContentItem> CandidateItems, Guid? ReprotectionCommandId = null, IReadOnlyList<StagedContentItem>? CurrentItems = null);

public sealed record SignedCandidateItem(ManifestItemKind ItemKind, Guid ItemId, string RowSignatureBase64, string CanonicalPayloadDigestBase64, string MutationKind);

public sealed record PreparedContentOperation(Guid OperationId, long CandidateContentRevision, int SignatureKeyVersion, string CandidateManifestRootBase64, IReadOnlyList<SignedCandidateItem> SignedItems, string OperationStatus, Guid CorrelationId);

public sealed record StagedContentItem(ManifestItemKind ItemKind, Guid ItemId, string CanonicalBytesBase64, string RowSignatureBase64);

public sealed record CommitContentChangeRequest(Guid OrganizationId, Guid FormRecordId, IReadOnlyList<StagedContentItem> StagedItems);

public sealed record AcceptedContentRevision(Guid FormRecordId, long CommittedContentRevision, Guid? CommittedOperationId, string CommittedManifestRootBase64, long AuthorizationRevision, int IntegrityKeyVersion, int CurrentSigningKeyVersion, string ArchiveState, Guid CorrelationId);

/// <summary>AC-1 (Phase C6): one accepted revision as the Authority records it (see the Authority contract of the same name).</summary>
public sealed record ContentHistoryEntry(long ContentRevision, Guid CommittedOperationId, DateTimeOffset AcceptedOn, string ActorKind, Guid? ActorAuthorityUserId, Guid? ReprotectionCommandId, long ManifestLeafCount, int RowSignatureKeyVersion, bool VerifiableNow, bool IsCurrent);

public sealed record ContentHistoryPage(Guid OrganizationId, Guid FormRecordId, long CurrentContentRevision, IReadOnlyList<ContentHistoryEntry> Entries, long? NextAfterRevision, long AuthorizationRevision, Guid CorrelationId);

/// <summary>HistoricCommittedOperationId: verify an earlier accepted manifest of the record (historic document versions; full readers only).</summary>
public sealed record VerifyRecordProjectionRequest(Guid OrganizationId, Guid FormRecordId, ProtectedAction RequestedAction, IReadOnlyList<StagedContentItem> ObservedItems, Guid? HistoricCommittedOperationId = null);

public sealed record VerifiedProjectionItem(ManifestItemKind ItemKind, Guid ItemId);

/// <summary>Phase C5 (D7, additive): readable/editable subsets of a partial projection (advisory) and the Authority's contributor-write switch (D10).</summary>
public sealed record VerifyRecordProjectionResult(bool Verified, string? FailureReasonCode, long CommittedContentRevision, Guid? CommittedOperationId, long AuthorizationRevision, IReadOnlyList<VerifiedProjectionItem> AuthorizedItems, bool IsCompleteProjection, Guid CorrelationId,
    IReadOnlyList<VerifiedProjectionItem>? ReadableItems = null, IReadOnlyList<VerifiedProjectionItem>? EditableItems = null, bool ContributorWritesEnabled = false,
    IReadOnlyList<VerifiedProjectionItem>? DownloadableItems = null);

public sealed record FormAccessSubjectState(string SubjectKind, Guid SubjectId);

/// <summary>Authoritative designers and create-record principals of a form, read from the authority's desired state behind its gate.</summary>
public sealed record FormAccessState(Guid FormDefinitionId, long AuthorizationRevision, long ProjectionRevision, IReadOnlyList<FormAccessSubjectState> Designers, IReadOnlyList<FormAccessSubjectState> FormUsers);

public sealed record RecordDiscoveryPage(IReadOnlyList<Guid> FormRecordIds, long AuthorizationRevision, string? ContinuationToken);

// Phase C7 M1 — AC-5 record catalogue and OD-6 batch verification (mirrors of the Authority's internal contract).

public sealed record SearchCatalogueRequest(Guid OrganizationId, Guid FormDefinitionId, int MaximumEntries);

/// <remarks>Phase C7 M3: Authority-held registration and head-change times (sorting and display of verified results).</remarks>
public sealed record CatalogueEntry(Guid FormRecordId, long CommittedContentRevision, Guid? CommittedOperationId, bool ContentHeadQuarantined, bool ResourceQuarantined,
    DateTimeOffset? RecordRegisteredOn = null, DateTimeOffset? ContentHeadModifiedOn = null, string? ProtectionCensusOutcome = null);

/// <summary>The Authority's expected population of one form for the caller, with accepted heads, at one snapshot.</summary>
public sealed record SearchCatalogue(Guid SearchCatalogueSnapshotId, Guid OrganizationId, Guid FormDefinitionId, long AuthorizationRevision, long ProjectionRevision, long ContentIntegrityRevision,
    IReadOnlyList<CatalogueEntry> Entries, DateTimeOffset SnapshotExpiresOn, Guid CorrelationId);

public sealed record CatalogueSnapshotValidation(Guid SearchCatalogueSnapshotId, bool Valid, string? InvalidationReason, long CurrentAuthorizationRevision, long CurrentContentIntegrityRevision, Guid CorrelationId);

public sealed record BatchVerificationItem(Guid FormRecordId, Guid ExpectedCommittedOperationId, IReadOnlyList<StagedContentItem> ObservedItems);

public sealed record VerifyRecordProjectionsRequest(Guid OrganizationId, IReadOnlyList<BatchVerificationItem> Records);

public sealed record BatchVerificationResult(Guid FormRecordId, bool Verified, string? FailureReasonCode, long CommittedContentRevision, Guid? CommittedOperationId, bool IsCompleteProjection, IReadOnlyList<VerifiedProjectionItem> ReadableItems);

public sealed record VerifyRecordProjectionsResponse(IReadOnlyList<BatchVerificationResult> Results, long AuthorizationRevision, Guid CorrelationId);

/// <summary>Stable authority error surfaced to callers; the business API maps it to a Problem Details response without leaking internals.</summary>
public sealed class AuthorityClientException(string errorCode, int httpStatusCode, bool retryable, string? detail = null) : Exception(detail ?? errorCode)
{
    public string ErrorCode { get; } = errorCode;

    public int HttpStatusCode { get; } = httpStatusCode;

    public bool Retryable { get; } = retryable;
}

/// <summary>The verified end-user context forwarded to the authority: the raw assertion is forwarded unchanged; no claims are re-asserted by the business API.</summary>
public sealed record DelegatedUserContext(string IssuerUri, string SubjectIdentifier, Guid OrganizationId, string RawAssertion);

public interface IAuthorizationAuthorityClient
{
    Task<PermissionDecision> CheckPermissionAsync(DelegatedUserContext user, PermissionCheckRequest request, CancellationToken cancellationToken);

    Task<FormAccessState> GetFormAccessAsync(DelegatedUserContext user, Guid organizationId, Guid formDefinitionId, CancellationToken cancellationToken);

    Task<IReadOnlyList<PermissionDecision>> CheckPermissionsBatchAsync(DelegatedUserContext user, IReadOnlyList<PermissionCheckRequest> requests, CancellationToken cancellationToken);

    Task<RecordDiscoveryPage> DiscoverAuthorizedRecordsAsync(DelegatedUserContext user, Guid organizationId, int pageSize, string? continuationToken, CancellationToken cancellationToken);

    Task<SecurityCommandResult> SubmitSecurityCommandAsync(DelegatedUserContext user, SecurityCommandRequest request, string idempotencyKey, CancellationToken cancellationToken);

    Task<SecurityCommandResult> GetSecurityCommandStatusAsync(DelegatedUserContext user, Guid commandId, CancellationToken cancellationToken);

    Task<PreparedContentOperation> PrepareContentChangeAsync(DelegatedUserContext user, PrepareContentChangeRequest request, string idempotencyKey, CancellationToken cancellationToken);

    Task<AcceptedContentRevision> CommitContentChangeAsync(DelegatedUserContext user, Guid operationId, CommitContentChangeRequest request, CancellationToken cancellationToken);

    Task<AcceptedContentRevision> GetCommittedContentRevisionAsync(DelegatedUserContext user, Guid organizationId, Guid formRecordId, CancellationToken cancellationToken);

    Task<VerifyRecordProjectionResult> VerifyRecordProjectionAsync(DelegatedUserContext user, VerifyRecordProjectionRequest request, CancellationToken cancellationToken);

    /// <summary>Phase C7 AC-5: the user-scoped catalogue of one form (Authority snapshot).</summary>
    Task<SearchCatalogue> IssueSearchCatalogueAsync(DelegatedUserContext user, SearchCatalogueRequest request, CancellationToken cancellationToken);

    /// <summary>Phase C7 AC-5: validates the caller's own snapshot against current Authority state.</summary>
    Task<CatalogueSnapshotValidation> ValidateSearchCatalogueAsync(DelegatedUserContext user, Guid searchCatalogueSnapshotId, CancellationToken cancellationToken);

    /// <summary>Phase C7 OD-6: verifies 1..50 records, each independently against its expected accepted head.</summary>
    Task<VerifyRecordProjectionsResponse> VerifyRecordProjectionsAsync(DelegatedUserContext user, VerifyRecordProjectionsRequest request, CancellationToken cancellationToken);

    /// <summary>AC-1 (Phase C6): accepted content revisions of a record, ascending from <paramref name="afterRevision"/>; full readers only (404 otherwise).</summary>
    Task<ContentHistoryPage> GetContentHistoryAsync(DelegatedUserContext user, Guid organizationId, Guid formRecordId, long? afterRevision, int limit, CancellationToken cancellationToken);

    // Workload reprotection operations (B-28): no delegated user; the authority authorizes them from the effective PublishFormSecurityRevision command only.
    Task<AcceptedContentRevision> GetCommittedContentRevisionForReprotectionAsync(Guid organizationId, Guid formRecordId, Guid reprotectionCommandId, CancellationToken cancellationToken);

    Task<PreparedContentOperation> PrepareReprotectionAsync(PrepareContentChangeRequest request, string idempotencyKey, CancellationToken cancellationToken);

    Task<AcceptedContentRevision> CommitReprotectionAsync(Guid operationId, CommitContentChangeRequest request, CancellationToken cancellationToken);
}

/// <summary>G23: the Authority's verdict on a browser session (reason for the trusted business API only, never the browser).</summary>
public sealed record BrowserSessionState(bool Valid, string? InvalidReason, DateTimeOffset? ExpiresOn, Guid CorrelationId);

/// <summary>
/// G23: server-side browser session state, held by the Authority (outside BusinessDb). Only SHA-256 digests of session
/// secrets are sent; the secret itself stays in the encrypted session cookie. Separate from
/// <see cref="IAuthorizationAuthorityClient"/> because only the BFF session composition uses it.
/// </summary>
public interface IBrowserSessionStateAuthority
{
    /// <summary>Registers a new session and revokes <paramref name="replacedSessionDigestBase64"/> (the session the browser presented), if any.</summary>
    Task<BrowserSessionState> CreateSessionAsync(DelegatedUserContext user, string sessionDigestBase64, DateTimeOffset absoluteExpiresOn, string? replacedSessionDigestBase64, CancellationToken cancellationToken);

    Task<BrowserSessionState> ValidateSessionAsync(DelegatedUserContext user, string sessionDigestBase64, CancellationToken cancellationToken);

    /// <summary>Idempotent.</summary>
    Task<BrowserSessionState> RevokeSessionAsync(DelegatedUserContext user, string sessionDigestBase64, CancellationToken cancellationToken);
}

// ---- Phase C7 M2 (AC-6): Authority field search capabilities (mirrors AuthorizationAuthority.Contracts.V1.FieldSearchContracts).

public sealed record FieldSearchCapabilitiesQuery(Guid OrganizationId, Guid FormDefinitionId);

public sealed record FieldSearchOptionSnapshot(Guid FieldOptionId, string OptionLabel);

/// <remarks>C-188: <c>InCurrentVersion</c> false for a field searched under a retained-field approval (no longer in the form).</remarks>
public sealed record AuthoritySearchableField(Guid FieldDefinitionId, string FieldLabel, string FieldDataType, IReadOnlyList<string> Operators, IReadOnlyList<FieldSearchOptionSnapshot> Options, int ApprovedFormVersionNumber, bool InCurrentVersion = true);

public sealed record AuthorityFieldSearchStatus(Guid FieldDefinitionId, string FieldLabel, string FieldDataType, string FieldClassification, string ProtectionMode, string Eligibility,
    string ApprovalState, string? InvalidationReason, IReadOnlyList<string> ApprovedOperators, IReadOnlyList<string> PermittedOperators,
    bool InCurrentVersion = true, int? LastPlacedVersionNumber = null, string? ApprovalScope = null);

/// <summary>
/// C-188 BF-1: the form's publication staged with the Authority and not yet resolved (business version id, number, layout digest).
/// C-191: <c>FormVersionPublicationId</c> is the Authority's identifier of the staged publication (an abandonment attestation is bound to it).
/// </summary>
public sealed record AuthorityPendingFormVersionPublication(Guid FormVersionId, int FormVersionNumber, string LayoutDigestHex, DateTimeOffset PreparedOn, Guid? FormVersionPublicationId = null);

public sealed record AuthorityFieldSearchConfiguration(bool CanApprove, bool ApprovalOperational, string ApprovalUnavailableReason, int? RegisteredFormVersionNumber, IReadOnlyList<AuthorityFieldSearchStatus> Fields,
    IReadOnlyList<FormAccessSubjectState>? FieldSearchers = null, bool RetainedFieldApprovalsEnabled = false, AuthorityPendingFormVersionPublication? PendingPublication = null);

/// <remarks>C-188 BF-1: <c>PublicationPending</c> - a publication of the form awaits registration; no field of it is searchable until resolved.</remarks>
public sealed record AuthorityFieldSearchCapabilities(Guid FormDefinitionId, bool PlaintextSearchPermitted, long SearchPolicyRevision, long AuthorizationRevision,
    IReadOnlyList<AuthoritySearchableField> Fields, AuthorityFieldSearchConfiguration? Configuration, Guid CorrelationId, bool PublicationPending = false);

/// <summary>Phase C7 M3 (OD-8): evidence of one execution — field ids, operators, counts, outcome (never values or record ids).</summary>
public sealed record SearchExecutionEvidence(IReadOnlyList<Guid> FieldDefinitionIds, IReadOnlyList<string> Operators, int ConditionCount, string Outcome, int? EvaluatedRecordCount, int? MatchedRecordCount, long ElapsedMilliseconds);

public sealed record FieldSearchAdministrationQuery(Guid OrganizationId);

public sealed record AuthorityFieldSearchAdministrationForm(Guid FormDefinitionId, string? FormTitle, int RegisteredFormVersionNumber, DateTimeOffset RegisteredOn, int FieldCount, int EligibleFieldCount, int ActiveApprovalCount, int InvalidatedApprovalCount, int RetainedApprovalCount = 0);

public sealed record AuthorityFieldSearchAdministration(bool PlaintextSearchPermitted, bool ApprovalOperational, string ApprovalUnavailableReason, IReadOnlyList<AuthorityFieldSearchAdministrationForm> Forms, long AuthorizationRevision, Guid CorrelationId);

/// <summary>Phase C7 M2/M3: the Authority's field search capabilities, administration listing and execution evidence (narrow port; only the Search feature uses it).</summary>
public interface IFieldSearchPolicyAuthority
{
    Task<AuthorityFieldSearchCapabilities> GetFieldSearchCapabilitiesAsync(DelegatedUserContext user, FieldSearchCapabilitiesQuery query, CancellationToken cancellationToken);

    /// <summary>Security administrators only (<c>administer_security</c>): forms with a registered published version and their search-approval counts.</summary>
    Task<AuthorityFieldSearchAdministration> GetFieldSearchAdministrationAsync(DelegatedUserContext user, FieldSearchAdministrationQuery query, CancellationToken cancellationToken);

    /// <summary>Journals one execution over the caller's own catalogue snapshot on the Authority audit stream.</summary>
    Task RecordSearchExecutionAsync(DelegatedUserContext user, Guid searchCatalogueSnapshotId, SearchExecutionEvidence evidence, CancellationToken cancellationToken);
}

// ---- Owner message 7: Security Administrators (mirrors AuthorizationAuthority.Contracts.V1 SecurityAdministratorRoster) ----

public sealed record SecurityAdministratorRosterQuery(Guid OrganizationId);

public sealed record AuthoritySecurityAdministrator(Guid AuthorityUserId, string IssuerUri, string SubjectIdentifier, DateTimeOffset AppointedOn, Guid AppointedBySecurityCommandId);

public sealed record AuthoritySecurityAdministratorCandidate(Guid AuthorityUserId, string IssuerUri, string SubjectIdentifier);

public sealed record AuthoritySecurityAdministratorRoster(bool CanManage, bool AppointmentOperational, string AppointmentUnavailableReason, IReadOnlyList<AuthoritySecurityAdministrator> Administrators,
    IReadOnlyList<AuthoritySecurityAdministratorCandidate>? Candidates, long AuthorizationRevision, Guid CorrelationId);

// ---- C8 M2 (AC-7.3): the organization's complete roster from the Authority (owners and Security Administrators only) ----

public sealed record OrganizationRosterQuery(Guid OrganizationId);

public sealed record AuthorityRosterMember(Guid AuthorityUserId, string IssuerUri, string SubjectIdentifier, bool MembershipEnabled, DateTimeOffset BoundOn, DateTimeOffset? DisabledOn, IReadOnlyList<string> OrganizationRoles, string? DisplayNameHint, DateTimeOffset? RecoveryRestrictionsUntil = null);

public sealed record AuthorityOwnerProposal(Guid ProposalId, Guid TargetAuthorityUserId, string ProposedChange, Guid ProposedByAuthorityUserId, string Reason, DateTimeOffset ProposedOn, DateTimeOffset ExpiresOn, string ProposalState, Guid? ConsumedBySecurityCommandId);

public sealed record AuthorityMembershipRequest(Guid RequestId, Guid RequestedAuthorityUserId, string IssuerUri, string SubjectIdentifier, string DisplayNameHint, string? InitialRoleName, Guid RequestedByAuthorityUserId,
    string Reason, DateTimeOffset RequestedOn, DateTimeOffset ExpiresOn, string RequestState, DateTimeOffset? DecidedOn, string? DecisionReason,
    string RequestDigest = "", Guid? ReplacesRequestId = null, Guid? ReplacedByRequestId = null, DateTimeOffset? CancelledOn = null);

public sealed record AuthorityOrganizationRoster(bool CanManageMembers, bool CanAdministerSecurity, bool CommandsOperational, string CommandsUnavailableReason, bool OperatorApprovalOperational, string? OrganizationIdentityIssuer,
    IReadOnlyList<AuthorityRosterMember> Members, IReadOnlyList<AuthorityOwnerProposal> OwnerProposals, IReadOnlyList<AuthorityMembershipRequest> MembershipRequests, long AuthorizationRevision, Guid CorrelationId);

/// <summary>C8 M2 (AC-7.3): narrow port to the Authority roster read. Confers nothing; every change is a security command.</summary>
public interface IOrganizationRosterAuthority
{
    Task<AuthorityOrganizationRoster> GetRosterAsync(DelegatedUserContext user, OrganizationRosterQuery query, CancellationToken cancellationToken);
}

// ---- C8 owner recovery (C-105): the organization's in-app notice and objection (mirrors AuthorizationAuthority.Contracts.V1 OwnerRecoveryNotice) ----

public sealed record OwnerRecoveryNoticeQuery(Guid OrganizationId);

public sealed record AuthorityOwnerRecoveryNoticeSubject(string Role, Guid AuthorityUserId, string SubjectIdentifier, string DisplayNameHint, bool IsNewIdentity, string? UnavailabilityReason);

public sealed record AuthorityOwnerRecoveryNoticeCase(Guid CaseId, string IncidentReference, string Justification, DateTimeOffset OpenedOn, DateTimeOffset WaitingPeriodEndsOn, int ApprovalsRecorded, int ApprovalsRequired,
    IReadOnlyList<AuthorityOwnerRecoveryNoticeSubject> Subjects);

public sealed record AuthorityOwnerRecoveryNotice(AuthorityOwnerRecoveryNoticeCase? Case, bool CanObject, Guid CorrelationId);

public sealed record OwnerRecoveryObjectionCommand(Guid OrganizationId, Guid CaseId, string Reason, string? StepUpEvidence);

public sealed record AuthorityOwnerRecoveryObjection(Guid CaseId, string State, DateTimeOffset ObjectedOn, Guid CorrelationId);

/// <summary>
/// C-105: the only owner-recovery capability of the business plane: read the open case (enabled owners and Security Administrators;
/// not found for everyone else) and object to it. There is no business route that opens, approves or executes a case.
/// </summary>
public interface IOwnerRecoveryNoticeAuthority
{
    Task<AuthorityOwnerRecoveryNotice> GetOwnerRecoveryNoticeAsync(DelegatedUserContext user, OwnerRecoveryNoticeQuery query, CancellationToken cancellationToken);

    Task<AuthorityOwnerRecoveryObjection> ObjectToOwnerRecoveryAsync(DelegatedUserContext user, OwnerRecoveryObjectionCommand command, CancellationToken cancellationToken);
}

// ---- C8 M4-2 (AC-8.3): audit-access evidence (mirrors AuthorizationAuthority.Contracts.V1 AuditAccessRequest/AuditAccessDecision) ----

/// <summary>AccessKind is ExplorerPage or EventDetail; FilterDigest is lower-case hex SHA-256 of the canonical filter (ids, codes and dates only).</summary>
public sealed record AuditAccessQuery(Guid OrganizationId, string AccessKind, string FilterDigest, int PageSize, Guid? AuditEventId, Guid CorrelationId);

/// <summary>An allow with the committed AuditAccessAuthorized entry's audit-stream revision; Delivery is always "not_attested".</summary>
public sealed record AuthorityAuditAccessDecision(bool Allowed, long AuditJournalRevision, long AuthorizationRevision, string Delivery, Guid CorrelationId);

/// <summary>
/// C8 M4-2: the only way the business may serve its audit projection. The Authority checks view_audit and commits evidence
/// before it answers; any refusal or failure is an AuthorityClientException and nothing is served (fail closed).
/// </summary>
public interface IAuditAccessAuthority
{
    Task<AuthorityAuditAccessDecision> AuthorizeAuditAccessAsync(DelegatedUserContext user, AuditAccessQuery query, CancellationToken cancellationToken);
}

// ---- C8 M4-3 (AC-8.2): the security-command timeline (mirrors AuthorizationAuthority.Contracts.V1 SecurityCommandTimeline*) ----

/// <summary>After is the Authority's keyset position (the business wraps it in its protected cursor); kinds and states are codes.</summary>
public sealed record AuthorityTimelineQuery(Guid OrganizationId, string? After, int PageSize, IReadOnlyList<string>? Kinds, IReadOnlyList<string>? States, DateTimeOffset? From, DateTimeOffset? To, Guid CorrelationId);

/// <summary>One page read and served by the Authority after its AuditAccessAuthorized entry committed (ServedBy "authority", Delivery "not_attested").</summary>
public sealed record AuthorityTimelinePage(List<AuthorityTimelineEntry> Items, string? After, long AuditJournalRevision, long AuthorizationRevision, string ServedBy, string Delivery);

public sealed record AuthorityTimelineEntry(string Source, string Kind, DateTimeOffset OccurredOn, Guid? CommandId, string? CommandState, long? CommittedAuthorizationRevision, string? RejectionCode,
    Guid? ActorUserId, DateTimeOffset? EffectiveOn, string EvidenceBasis, string ReportedBy, AuthorityTimelineEvidence? Evidence, List<AuthorityTimelineDetail> Details, List<string> WithheldFields);

public sealed record AuthorityTimelineEvidence(string Stream, long StreamRevision, string EntryDigestHex, string ArchiveState, bool ReceiptReferencePresent);

public sealed record AuthorityTimelineDetail(string Key, string Kind, string? Value, List<string>? Values);

/// <summary>
/// C8 M4-3: the only source of the security-command timeline. The Authority checks view_audit, reads the page inside its audit-access
/// transaction and commits evidence before answering; the business renders it and stores nothing (R-5: no business delivery row).
/// </summary>
public interface ISecurityCommandTimelineAuthority
{
    Task<AuthorityTimelinePage> ReadSecurityCommandTimelineAsync(DelegatedUserContext user, AuthorityTimelineQuery query, CancellationToken cancellationToken);
}

// ---- C8 M4-4 (AC-8.4, AC-8.5): Security Home overview and coverage (mirrors AuthorizationAuthority.Contracts.V1 SecurityOverview*) ----

public sealed record SecurityOverviewQuery(Guid OrganizationId, Guid CorrelationId);

public sealed record AuthoritySecurityOverview(Guid OrganizationId, string Section, string AuthorizationBasis, DateTimeOffset ComputedOn,
    AuthorityOverviewGate? Gate, AuthorityOverviewCommands? Commands, AuthorityOverviewArchive? Archive, Guid CorrelationId);

public sealed record AuthorityOverviewGate(long AuthorizationRevision, long ProjectionAppliedRevision, long ContentIntegrityRevision, string GateState, bool IntegrityQuarantined);

public sealed record AuthorityOverviewCommands(List<AuthorityOverviewStateCount> StateCounts, DateTimeOffset? OldestUnresolvedCreatedOn, long? OldestUnresolvedAgeSeconds,
    List<AuthorityOverviewCommand> Commands, int ListLimit, string ListStatement);

public sealed record AuthorityOverviewStateCount(string CommandState, int CommandCount);

public sealed record AuthorityOverviewCommand(string CommandKind, string CommandState, long? CommittedAuthorizationRevision, DateTimeOffset CreatedOn, long AgeSeconds);

public sealed record AuthorityOverviewArchive(List<AuthorityOverviewArchiveStreamState> Streams, string ArchiveStatement);

public sealed record AuthorityOverviewArchiveStreamState(string StreamName, int PendingCount, int AnchoredCount, int VerificationFailedCount,
    DateTimeOffset? OldestPendingCreatedOn, long? OldestPendingAgeSeconds, DateTimeOffset? LastAnchoredOn);

public sealed record AuthoritySecurityCoverage(Guid OrganizationId, int RegisteredFormCount, int DecisionCount, int AttestedCompleteDecisionCount, int MissingDecisionCount, int StaleDecisionCount,
    long OrganizationContentIntegrityRevision, List<AuthorityCoverageDecision> Decisions, DateTimeOffset ComputedOn, string CoverageStatement, string StalenessStatement, string AuthorizationBasis, Guid CorrelationId);

public sealed record AuthorityCoverageDecision(Guid SecurityCommandId, Guid FormDefinitionId, Guid FieldDefinitionId, string ProtectionDirection, bool FormRegisteredActive, string AttestationState,
    AuthorityCoverageAttestation? Attestation);

public sealed record AuthorityCoverageAttestation(Guid AttestationId, bool AttestationComplete, string? AttestationIncompleteReasonCode, int ExpectedRecordCount, int AcceptedRecordCount, int CompliantRecordCount,
    int PendingCreationRecordCount, int QuarantinedRecordCount, int UnverifiedRecordCount, int NotYetCensusedRecordCount, int RetiredRecordCount, long RetainedHistoryRevisionCount,
    long AuditJournalRevision, long AttestedContentIntegrityRevision, DateTimeOffset AttestedOn);

/// <summary>
/// C8 M4-4: the only source of the Security Home's gate, commands, archive and coverage sections. The Authority authorizes each read
/// with the Effective-edge rule in one read-only snapshot (no SpiceDB), so the sections answer while the gate is closed; any refusal
/// or failure is an AuthorityClientException and nothing is shown. Incidents and search approvals keep their own ports and checks (OD-6).
/// </summary>
public interface ISecurityOverviewAuthority
{
    Task<AuthoritySecurityOverview> ReadSecurityOverviewAsync(DelegatedUserContext user, string section, SecurityOverviewQuery query, CancellationToken cancellationToken);

    Task<AuthoritySecurityCoverage> ReadProtectionCoverageAsync(DelegatedUserContext user, SecurityOverviewQuery query, CancellationToken cancellationToken);
}

/// <summary>Owner message 7: the Authority's Security Administrator roster (narrow port; used by Organizations, and read-only by SecurityOperations to list and validate incident investigators).</summary>
public interface ISecurityAdministratorAuthority
{
    Task<AuthoritySecurityAdministratorRoster> GetSecurityAdministratorsAsync(DelegatedUserContext user, SecurityAdministratorRosterQuery query, CancellationToken cancellationToken);
}

// ---- C7 M4 incident correction: isolated test-stack identity (Testing environment only) ----

public sealed record AuthorityStackIdentity(string EnvironmentName, string StackIdentity);

/// <summary>The Authority's configured test-stack identity (Testing only; anything else fails). Used solely by the Testing-only identity endpoint.</summary>
public interface ITestStackIdentityAuthority
{
    Task<AuthorityStackIdentity> GetTestStackIdentityAsync(CancellationToken cancellationToken);
}

/// <summary>C10 M2: the Authority's committed state of one share (internal <c>GetShareDetails</c>), read behind the open gate.</summary>
public sealed record AuthorityShareDetails(
    Guid RecordShareId,
    Guid FormRecordId,
    string LifecycleState,
    string? RecipientKind,
    Guid? RecipientId,
    string? RecipientRole,
    IReadOnlyList<Guid> FieldItemIds,
    DateTimeOffset? ShareExpiresOn,
    DateTimeOffset? EffectiveExpiresOn,
    long ShareRevision,
    Guid? ParentRecordShareId,
    int DelegationDepth,
    bool LegacyLineageUnresolved,
    string? InvalidationReasonCode,
    Guid? GrantorUserId,
    long AuthorizationRevision,
    IReadOnlyList<AuthorityShareDocumentDetail>? Documents = null);

public sealed record AuthorityShareDocumentDetail(Guid FileAttachmentId, bool CanViewDocument, bool CanDownloadDocument, bool CanReplaceDocument, int SharedThroughVersionNumber);

/// <summary>C10 M2 (WP-0 Section 3.5): the authoritative share-details read; the Authority decides who may read it.</summary>
public interface IShareDetailsAuthority
{
    Task<AuthorityShareDetails> GetShareDetailsAsync(DelegatedUserContext user, Guid organizationId, Guid formRecordId, Guid recordShareId, CancellationToken cancellationToken);

    /// <summary>C10 M1-WP9: the record's sharing and ownership history from committed Authority edges (owner or ManageAccess).</summary>
    Task<AuthorityRecordSecurityHistory> GetRecordSecurityHistoryAsync(DelegatedUserContext user, Guid organizationId, Guid formRecordId, CancellationToken cancellationToken);
}

public sealed record AuthorityRecordSecurityChange(string ChangeKind, Guid? RecordShareId, string? ShareEndState, string? InvalidationReasonCode, string? SubjectKind, Guid? SubjectId, DateTimeOffset? ShareExpiresOn = null);

public sealed record AuthorityRecordSecurityHistoryItem(Guid SecurityCommandId, string CommandKind, string CommandState, DateTimeOffset OccurredOn, DateTimeOffset? EffectiveOn,
    long? CommittedAuthorizationRevision, Guid? ActorUserId, IReadOnlyList<AuthorityRecordSecurityChange> Changes, bool JournalBacked = false);

public sealed record AuthorityRecordSecurityHistory(Guid FormRecordId, IReadOnlyList<AuthorityRecordSecurityHistoryItem> Items, bool Truncated, long AuthorizationRevision);
