namespace AuthorizationAuthority.Application.Common;

/// <summary>Stable machine-readable error family (CROSS_SYSTEM_CONSISTENCY_PROTOCOL.md §6). Status codes follow TRUST_AUTHORITY_ARCHITECTURE.md §5.</summary>
public static class AuthorityErrorCodes
{
    public const string Unauthorized = "UNAUTHORIZED";
    public const string Forbidden = "FORBIDDEN";
    public const string ActorNotBound = "ACTOR_NOT_BOUND";
    public const string NotFound = "NOT_FOUND";
    public const string ResourceNotRegistered = "RESOURCE_NOT_REGISTERED";
    public const string AuthorityNotReady = "AUTHORITY_NOT_READY";
    public const string StaleAuthorizationRevision = "STALE_AUTHORIZATION_REVISION";
    public const string ContentRevisionConflict = "CONTENT_REVISION_CONFLICT";

    /// <summary>R-53: a retained item id changed its identity anchor (field, group occurrence, group definition or form).</summary>
    public const string ItemIdentityChanged = "ITEM_IDENTITY_CHANGED";

    /// <summary>R-53: a new item names an id that is already bound or was accepted before (reintroduction or reuse).</summary>
    public const string ItemIdentityReused = "ITEM_IDENTITY_REUSED";
    public const string IntegrityQuarantine = "INTEGRITY_QUARANTINE";
    public const string IntegrityVerificationFailed = "INTEGRITY_VERIFICATION_FAILED";
    public const string IdempotencyConflict = "IDEMPOTENCY_CONFLICT";
    public const string ProjectionDivergence = "PROJECTION_DIVERGENCE";
    public const string InvalidCommand = "INVALID_COMMAND";
    public const string CommandKindNotImplemented = "COMMAND_KIND_NOT_IMPLEMENTED";
    public const string SigningUnavailable = "SIGNING_UNAVAILABLE";
    public const string ArchiveUnavailable = "ARCHIVE_UNAVAILABLE";
    public const string OperationExpired = "OPERATION_EXPIRED";
    public const string OperationStateInvalid = "OPERATION_STATE_INVALID";

    /// <summary>
    /// E3-M2b-1 CARV: the operation was prepared under content acceptance rules other than this build's (an older build, or a
    /// build on the other side of a rule-version change), or the database refused its transition under the CARV fence. It can
    /// never be accepted; the caller starts a fresh operation (new idempotency key). Never retried with the same operation.
    /// </summary>
    public const string OperationRulesOutdated = "OPERATION_RULES_OUTDATED";

    /// <summary>
    /// RB-CARV-1 (contract §22): 503, not retryable. A Release B instance refused a content write because the required CARV floor
    /// is not verified in the write transaction (missing, lowered, weakened, unverifiable) or the policy is invalid. Never a content
    /// conflict: retrying cannot help until an operator restores the floor or the configuration.
    /// </summary>
    public const string ContentAcceptanceFloorNotEnforced = "CONTENT_ACCEPTANCE_FLOOR_NOT_ENFORCED";

    /// <summary>E3-M2e L-13: the data-key ledger's unique index or P-1/P-2 triggers are not verified in the write transaction (503, not retryable).</summary>
    public const string ContentDataKeyLedgerNotEnforced = "CONTENT_DATA_KEY_LEDGER_NOT_ENFORCED";

    /// <summary>E3-M2e DK-6 / P-4: the staged v2 data-key groups disagree with the ledger; the operation is aborted and an incident is journaled (409).</summary>
    public const string DataKeyBindingMismatch = "DATA_KEY_BINDING_MISMATCH";

    /// <summary>E3-M2e L-12: the database refused a ledger write through the P-1 binding guard or P-2 immutability (an invariant failure; 503, not retryable).</summary>
    public const string DataKeyLedgerInvariantViolated = "DATA_KEY_LEDGER_INVARIANT_VIOLATED";

    /// <summary>
    /// E3-M2f-2 (docs/security/E3_M2F_CONTRACT_CLOSURE.md CAP-E1/E2): 409, not retryable. The business envelope capability the request
    /// declares (header <c>EnterpriseForms-Envelope-Capability</c>) is below the effective floor, so the request is refused before any
    /// content is verified. One fixed response whichever floor applies; nothing was written. Not a content conflict: retrying, or a
    /// fresh operation, is refused the same way until a current business build makes the request.
    /// </summary>
    public const string ClientEnvelopeCapabilityInsufficient = "CLIENT_ENVELOPE_CAPABILITY_INSUFFICIENT";
    public const string KeyUnavailable = "KEY_UNAVAILABLE";
    public const string CandidateMismatch = "CANDIDATE_MISMATCH";
    public const string LimitExceeded = "LIMIT_EXCEEDED";

    /// <summary>A content change carries rows of a field in a protection form the authority no longer accepts (atomic cutover barrier, Amendment A1); rebuild and retry.</summary>
    public const string FieldProtectionChanged = "FIELD_PROTECTION_CHANGED";

    /// <summary>A discovery continuation token was issued at an earlier authorization revision; restart the enumeration from the first page (C-28).</summary>
    public const string ContinuationInvalidated = "CONTINUATION_INVALIDATED";

    /// <summary>A share editor's current snapshot is not exactly the accepted, verified head (coverage, digest, binding or root); nothing is signed (Phase C5, D4).</summary>
    public const string CurrentSnapshotMismatch = "CURRENT_SNAPSHOT_MISMATCH";

    /// <summary>Field-scoped contributor writes are disabled by deployment configuration (Phase C5, D10; the default everywhere).</summary>
    public const string ContributorWritesDisabled = "CONTRIBUTOR_WRITES_DISABLED";
    public const string StepUpRequired = "STEP_UP_REQUIRED";
    public const string FieldSearchNotEligible = "FIELD_SEARCH_NOT_ELIGIBLE";
    public const string LastSecurityAdministrator = "LAST_SECURITY_ADMINISTRATOR";

    /// <summary>DR-C7-3: dual-controlled operator approval is not available here (production: privileged identity model not implemented).</summary>
    public const string OperatorDualControlUnavailable = "OPERATOR_DUAL_CONTROL_UNAVAILABLE";

    /// <summary>DR-C7-3: an operator approval is missing, malformed, forged, expired or bound to a different operation.</summary>
    public const string OperatorApprovalInvalid = "OPERATOR_APPROVAL_INVALID";

    /// <summary>DR-C7-3: both approvals come from the same approver.</summary>
    public const string OperatorApprovalsNotIndependent = "OPERATOR_APPROVALS_NOT_INDEPENDENT";

    /// <summary>DR-C7-3: a first appointment was requested for an organization that already has a usable Security Administrator.</summary>
    public const string SecurityAdministratorExists = "SECURITY_ADMINISTRATOR_EXISTS";

    /// <summary>DR-C7-3: recovery was requested for an organization with no Security Administrator (use a first appointment).</summary>
    public const string RecoveryNotApplicable = "RECOVERY_NOT_APPLICABLE";
    public const string FormVersionUntrusted = "FORM_VERSION_UNTRUSTED";

    /// <summary>C-188 (D-H7, D-H13): the field definition is bound to another form in this organization's Authority; nothing is registered, approved or published.</summary>
    public const string FieldBoundToAnotherForm = "FIELD_BOUND_TO_ANOTHER_FORM";

    /// <summary>C-188 (D-H18): approvals of retained (removed) fields are disabled by deployment configuration (FieldSearch:RetainedFieldApprovals).</summary>
    public const string RetainedFieldApprovalsDisabled = "RETAINED_FIELD_APPROVALS_DISABLED";

    /// <summary>C-188 BF-1: a publication of the form is pending registration (approvals and one-step registration are refused until it is resolved).</summary>
    public const string FormPublicationPending = "FORM_PUBLICATION_PENDING";

    /// <summary>C-188 BF-1: the resolution names a publication that is not the form's pending one (already resolved or never prepared).</summary>
    public const string FormPublicationNotPending = "FORM_PUBLICATION_NOT_PENDING";

    /// <summary>
    /// AC-2 (Phase C6): a share editor's content write targets a record whose accepted, signed header status is not Draft
    /// (Submitted or Archived). Decided from the signed header bound to the accepted head, never from a business projection.
    /// </summary>
    public const string RecordNotEditable = "RECORD_NOT_EDITABLE";

    // Phase C8 AC-9 (read-only security-command outcome lookup for the business reconciler).
    public const string InvalidRequest = "INVALID_REQUEST";
    public const string DelegatedAssertionNotAccepted = "DELEGATED_ASSERTION_NOT_ACCEPTED";
    public const string KindNotReconcilable = "KIND_NOT_RECONCILABLE";
    public const string RateLimited = "RATE_LIMITED";

    // C8 M2 (AC-7) membership and organization-role administration.
    public const string SelfChangeForbidden = "SELF_CHANGE_FORBIDDEN";

    /// <summary>C8 M4 (D8-72, C-113): Security Auditor reason notes are not accepted until the approved note encryption (R1) exists.</summary>
    public const string ReasonNoteNotAccepted = "REASON_NOTE_NOT_ACCEPTED";
    public const string OwnerApprovalRequired = "OWNER_APPROVAL_REQUIRED";
    public const string ProposalNotPending = "PROPOSAL_NOT_PENDING";
    public const string ProposalExpired = "PROPOSAL_EXPIRED";
    public const string ProposalMismatch = "PROPOSAL_MISMATCH";
    public const string ProposalExists = "PROPOSAL_EXISTS";
    public const string LastOwner = "LAST_OWNER";
    public const string RoleAlreadyHeld = "ROLE_ALREADY_HELD";
    public const string RoleNotHeld = "ROLE_NOT_HELD";
    public const string MembershipStateConflict = "MEMBERSHIP_STATE_CONFLICT";
    public const string OwnsRecords = "OWNS_RECORDS";
    public const string IdentityAlreadyBound = "IDENTITY_ALREADY_BOUND";
    public const string IdentityNotAllowed = "IDENTITY_NOT_ALLOWED";
    public const string MembershipRequestExists = "MEMBERSHIP_REQUEST_EXISTS";
    public const string MembershipRequestNotPending = "MEMBERSHIP_REQUEST_NOT_PENDING";

    /// <summary>C8 M2 (AC-7.5): the request was cancelled, replaced, decided or edited since the caller read it.</summary>
    public const string MembershipRequestChanged = "MEMBERSHIP_REQUEST_CHANGED";
    public const string MembershipRequestExpired = "MEMBERSHIP_REQUEST_EXPIRED";

    // C8 M2 Stages 1-2: Platform Operators (D8-33…D8-44).
    public const string PlatformOperatorsUnavailable = "PLATFORM_OPERATORS_UNAVAILABLE";
    public const string PlatformOperatorNotActive = "PLATFORM_OPERATOR_NOT_ACTIVE";
    public const string OperatorEvidenceInvalid = "OPERATOR_EVIDENCE_INVALID";
    public const string OperatorEvidenceIdentityMismatch = "OPERATOR_EVIDENCE_IDENTITY_MISMATCH";
    public const string OperatorEvidenceReplayed = "OPERATOR_EVIDENCE_REPLAYED";
    public const string OperatorEvidenceUnavailable = "OPERATOR_EVIDENCE_UNAVAILABLE";
    public const string ApprovalStatementChanged = "APPROVAL_STATEMENT_CHANGED";
    public const string OperatorAlreadyApproved = "OPERATOR_ALREADY_APPROVED";
    public const string OperatorConflictOfInterest = "OPERATOR_CONFLICT_OF_INTEREST";
    public const string OperatorQuorumUnavailable = "OPERATOR_QUORUM_UNAVAILABLE";
    public const string OperatorQuorumPresent = "OPERATOR_QUORUM_PRESENT";
    public const string OperatorIdentityExists = "OPERATOR_IDENTITY_EXISTS";
    public const string OperatorStateConflict = "OPERATOR_STATE_CONFLICT";
    public const string NoPendingApproval = "NO_PENDING_APPROVAL";
    public const string PlatformOperationReplayed = "PLATFORM_OPERATION_REPLAYED";
    public const string PlatformJournalBehindArchive = "PLATFORM_JOURNAL_BEHIND_ARCHIVE";
    public const string PlatformJournalUnverifiable = "PLATFORM_JOURNAL_UNVERIFIABLE";
    public const string PlatformJournalArchivePending = "PLATFORM_JOURNAL_ARCHIVE_PENDING";
    public const string PlatformJournalQuarantined = "PLATFORM_JOURNAL_QUARANTINED";

    // C8 onboarding (C-104) and OB-F8.
    public const string OrganizationOnboardingUnavailable = "ORGANIZATION_ONBOARDING_UNAVAILABLE";
    public const string OrganizationNameConflict = "ORGANIZATION_NAME_CONFLICT";
    public const string OrganizationOwnerIdentityInvalid = "ORGANIZATION_OWNER_IDENTITY_INVALID";
    public const string OrganizationOwnersInsufficient = "ORGANIZATION_OWNERS_INSUFFICIENT";
    public const string OrganizationOwnerIssuerMismatch = "ORGANIZATION_OWNER_ISSUER_MISMATCH";
    public const string OrganizationOwnerIsOperator = "ORGANIZATION_OWNER_IS_OPERATOR";
    public const string OrganizationOnboardingNotPending = "ORGANIZATION_ONBOARDING_NOT_PENDING";
    public const string OrganizationOnboardingExpired = "ORGANIZATION_ONBOARDING_EXPIRED";
    public const string OrganizationKeyNotProvisioned = "ORGANIZATION_KEY_NOT_PROVISIONED";
    public const string OrganizationIdReserved = "ORGANIZATION_ID_RESERVED";
    public const string LegacyOrganizationBootstrapDisabled = "LEGACY_ORGANIZATION_BOOTSTRAP_DISABLED";
    public const string TeamJoinRequiresAnotherOwner = "TEAM_JOIN_REQUIRES_ANOTHER_OWNER";

    // C8 owner recovery (C-105).
    public const string OwnerRecoveryUnavailable = "OWNER_RECOVERY_UNAVAILABLE";
    public const string OwnerRecoveryNotApplicable = "OWNER_RECOVERY_NOT_APPLICABLE";
    public const string OwnerRecoveryCaseExists = "OWNER_RECOVERY_CASE_EXISTS";
    public const string OwnerRecoveryNotOpen = "OWNER_RECOVERY_NOT_OPEN";
    public const string OwnerRecoveryExpired = "OWNER_RECOVERY_EXPIRED";
    public const string OwnerRecoveryApprovalsIncomplete = "OWNER_RECOVERY_APPROVALS_INCOMPLETE";
    public const string OwnerRecoveryWaitingPeriod = "OWNER_RECOVERY_WAITING_PERIOD";
    public const string OwnerRecoveryObjected = "OWNER_RECOVERY_OBJECTED";
    public const string OwnerRecoveryOwnerSetChanged = "OWNER_RECOVERY_OWNER_SET_CHANGED";
    public const string OwnerRecoveryDeclaredOwnerActive = "OWNER_RECOVERY_DECLARED_OWNER_ACTIVE";
    public const string OwnerRecoveryIdentityConflict = "OWNER_RECOVERY_IDENTITY_CONFLICT";
    public const string OwnerRecoveryEvidenceRequired = "OWNER_RECOVERY_EVIDENCE_REQUIRED";
    public const string OwnerRecoveryProbation = "OWNER_RECOVERY_PROBATION";

    /// <summary>C-107: the organization already has (or would exceed) the maximum number of active owners.</summary>
    public const string OrganizationOwnerLimit = "ORGANIZATION_OWNER_LIMIT";
    public const string CursorInvalid = "CURSOR_INVALID";

    /// <summary>IX-1: the search index could not be derived for a content commit; the commit is refused (IX-D7, fail closed).</summary>
    public const string SearchIndexDerivationFailed = "SEARCH_INDEX_DERIVATION_FAILED";

    /// <summary>IX-1: the search entry seal could not be produced with the pinned key; the commit is refused (fail closed).</summary>
    public const string SearchSealUnavailable = "SEARCH_SEAL_UNAVAILABLE";

    /// <summary>IX-2: the organization has no search index, or this deployment does not maintain one (Disabled mode).</summary>
    public const string SearchIndexNotPresent = "SEARCH_INDEX_NOT_PRESENT";

    /// <summary>IX-2: the search index is quarantined; only a SearchProjectionRebuild clears it (IX-D7). Never an organization quarantine.</summary>
    public const string SearchIndexQuarantined = "SEARCH_INDEX_QUARANTINED";

    // C8 M4-2 (AC-8.3): audit-access evidence.

    /// <summary>403: the caller does not hold view_audit (Security Auditor) in the organization.</summary>
    public const string AuditViewForbidden = "AUDIT_VIEW_FORBIDDEN";

    /// <summary>503, retryable: the organization lock stayed busy (or the authorization state moved) for every bounded attempt; nothing was served.</summary>
    public const string AuditEvidenceBusy = "AUDIT_EVIDENCE_BUSY";

    /// <summary>503, retryable: the AuditAccessAuthorized entry could not be committed (failure, timeout or unknown outcome); nothing was served.</summary>
    public const string AuditEvidenceUnavailable = "AUDIT_EVIDENCE_UNAVAILABLE";
}

/// <summary>Thrown by application services; mapped to an ErrorResponse by the host. Never carries secrets or protected values.</summary>
public sealed class AuthorityException(string errorCode, int httpStatusCode, bool retryable, string? detail = null) : Exception(detail ?? errorCode)
{
    public string ErrorCode { get; } = errorCode;

    public int HttpStatusCode { get; } = httpStatusCode;

    public bool Retryable { get; } = retryable;

    public string? Detail { get; } = detail;

    public static AuthorityException Forbidden(string? detail = null) => new(AuthorityErrorCodes.Forbidden, 403, false, detail);

    public static AuthorityException NotReady(string detail) => new(AuthorityErrorCodes.AuthorityNotReady, 503, true, detail);

    public static AuthorityException Invalid(string detail) => new(AuthorityErrorCodes.InvalidCommand, 422, false, detail);

    public static AuthorityException NotRegistered(string detail) => new(AuthorityErrorCodes.ResourceNotRegistered, 404, false, detail);

    public static AuthorityException Quarantined(string detail) => new(AuthorityErrorCodes.IntegrityQuarantine, 423, false, detail);
}
