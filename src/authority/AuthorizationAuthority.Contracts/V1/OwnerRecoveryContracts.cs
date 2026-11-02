namespace AuthorizationAuthority.Contracts.V1;

// C8 owner recovery (C-105). Platform plane: open, approve (three distinct operators), withdraw, cancel and execute recovery cases.
// Business plane (workload credential + the delegated user): an enabled owner or Security Administrator of the organization reads
// the open case and may object. Execution only adds owner roles (and bindings for new identities); nothing else changes.

public static class OwnerRecoveryFilters
{
    public const string Open = "open";
    public const string AwaitingApprovals = "awaiting_approvals";
    public const string WaitingPeriod = "waiting_period";
    public const string Ready = "ready";
    public const string Objected = "objected";
    public const string Recovered = "recovered";
    public const string Closed = "closed";
    public const string All = "all";

    public static readonly IReadOnlyList<string> Values = [Open, AwaitingApprovals, WaitingPeriod, Ready, Objected, Recovered, Closed, All];
}

/// <summary>Display state of a case: AwaitingApprovals, WaitingPeriod, ReadyForRecovery, BlockedByObjection, Recovering, Recovered, Cancelled, Expired, Invalidated.</summary>
public static class OwnerRecoveryDisplayStates
{
    public const string AwaitingApprovals = "AwaitingApprovals";
    public const string WaitingPeriod = "WaitingPeriod";
    public const string ReadyForRecovery = "ReadyForRecovery";
    public const string BlockedByObjection = "BlockedByObjection";
    public const string Recovering = "Recovering";
    public const string Recovered = "Recovered";
    public const string Cancelled = "Cancelled";
    public const string Expired = "Expired";
    public const string Invalidated = "Invalidated";
}

// ---- statement ----

public sealed record OwnerRecoveryStatement(
    int StatementVersion,
    string OperationKind,
    Guid OperationId,
    Guid OrganizationId,
    string OrganizationDisplayName,
    string IncidentReference,
    string RecoveryReasonSha256,
    IReadOnlyList<OwnerRecoveryOwnerStatement> CurrentOwners,
    IReadOnlyList<OwnerRecoveryDeclaredStatement> DeclaredUnavailable,
    IReadOnlyList<OwnerRecoveryReplacementStatement> Replacements,
    int RequiredApprovals,
    Guid OpenedByPlatformOperatorId,
    DateTimeOffset CaseOpenedOn,
    DateTimeOffset WaitingPeriodEndsOn,
    DateTimeOffset CaseExpiresOn);

public sealed record OwnerRecoveryOwnerStatement(Guid AuthorityUserId, string IssuerUri, string SubjectIdentifier, bool Enabled);

public sealed record OwnerRecoveryDeclaredStatement(Guid AuthorityUserId, string IssuerUri, string SubjectIdentifier, string Reason, string EvidenceReference, string EvidenceSummarySha256);

public sealed record OwnerRecoveryReplacementStatement(Guid AuthorityUserId, string IssuerUri, string SubjectIdentifier, string DisplayNameHint, bool IsNewIdentity, string VerificationMethod, string VerificationReference);

// ---- platform plane requests ----

public sealed record OwnerRecoveryDeclaredInput(Guid AuthorityUserId, string? Reason, string? EvidenceReference, string? EvidenceSummary);

/// <summary>A replacement owner: an existing enabled member (<c>ExistingAuthorityUserId</c>) or a new identity (issuer + subject).</summary>
public sealed record OwnerRecoveryReplacementInput(Guid? ExistingAuthorityUserId, string? IssuerUri, string? SubjectIdentifier, string? DisplayNameHint, string? VerificationMethod, string? VerificationReference);

public sealed record OwnerRecoveryCreateRequest(
    string? IdempotencyKey,
    Guid OrganizationId,
    string? IncidentReference,
    string? Justification,
    IReadOnlyList<OwnerRecoveryDeclaredInput>? DeclaredUnavailable,
    IReadOnlyList<OwnerRecoveryReplacementInput>? Replacements,
    bool DryRun = false);

public sealed record OwnerRecoveryDecisionRequest(string? ExpectedStatementSha256, string? Justification, string? EvidenceKind, string? Evidence, bool DryRun = false);

public sealed record OwnerRecoverySearchRequest(string? Query, string? Status, string? Cursor, int? Limit);

// ---- platform plane responses ----

public sealed record OwnerRecoveryOwnerView(Guid AuthorityUserId, string IssuerUri, string SubjectIdentifier, bool Enabled, bool DeclaredUnavailable, bool ActiveSinceCaseOpened);

public sealed record OwnerRecoverySubjectView(string Role, Guid AuthorityUserId, string IssuerUri, string SubjectIdentifier, string DisplayNameHint, bool IsNewIdentity, string? VerificationMethod, string? VerificationReference,
    string? UnavailabilityReason, string? EvidenceReference, string? EvidenceSummary);

public sealed record OwnerRecoveryApprovalView(Guid ApprovalId, Guid PlatformOperatorId, string OperatorDisplayName, bool IsMine, string Justification, DateTimeOffset RecordedOn, string State, string? ClosureReason);

public sealed record OwnerRecoveryEligibility(bool CanApprove, bool CanWithdraw, bool CanCancel, bool CanExecute, string? ApproveRefusalCode, string? ApproveRefusalDetail, string? ExecuteRefusalCode, string? ExecuteRefusalDetail);

public sealed record OwnerRecoveryObjectionView(DateTimeOffset ObjectedOn, Guid ObjectedByAuthorityUserId, string ObjectedBySubjectIdentifier, string Reason);

public sealed record OwnerRecoveryCaseDetail(
    Guid CaseId,
    Guid OrganizationId,
    string OrganizationDisplayName,
    string State,
    string DisplayState,
    string IncidentReference,
    string Justification,
    Guid OpenedByPlatformOperatorId,
    string OpenedByOperatorDisplayName,
    DateTimeOffset OpenedOn,
    DateTimeOffset WaitingPeriodEndsOn,
    long WaitingPeriodRemainingSeconds,
    DateTimeOffset ExpiresOn,
    DateTimeOffset? RecoveredOn,
    DateTimeOffset? ClosedOn,
    string? ClosureReason,
    string? ClosureJustification,
    string? LastExecutionFailureCode,
    DateTimeOffset? LastExecutionFailureOn,
    int EligibleOwnersBefore,
    int EligibleOwnersAfter,
    IReadOnlyList<OwnerRecoveryOwnerView> CurrentOwners,
    IReadOnlyList<OwnerRecoverySubjectView> Subjects,
    IReadOnlyList<OwnerRecoveryApprovalView> Approvals,
    int ApprovalsRecorded,
    int ApprovalsRequired,
    OwnerRecoveryObjectionView? Objection,
    string StatementJson,
    string StatementSha256,
    OrganizationActivation? Activation,
    OwnerRecoveryEligibility MyEligibility,
    long RowRevision,
    string ApprovalAssurance,
    Guid CorrelationId);

public sealed record OwnerRecoveryCreateResponse(bool DryRun, bool Eligible, string? RefusalCode, string? RefusalDetail, IReadOnlyList<string> Warnings, OwnerRecoveryCaseDetail? Case, bool Replayed, Guid CorrelationId);

public sealed record OwnerRecoveryDecisionResponse(
    bool DryRun,
    bool Eligible,
    string? RefusalCode,
    string? RefusalDetail,
    Guid CaseId,
    Guid OrganizationId,
    string Decision,
    string StatementJson,
    string StatementSha256,
    string Outcome,
    int ApprovalsRecorded,
    int ApprovalsRequired,
    string State,
    string DisplayState,
    Guid? SecurityCommandId,
    string? ArchiveState,
    bool Replayed,
    string ApprovalAssurance,
    Guid CorrelationId);

public sealed record OwnerRecoveryListItem(Guid CaseId, Guid OrganizationId, string OrganizationDisplayName, string IncidentReference, string State, string DisplayState, DateTimeOffset OpenedOn, DateTimeOffset WaitingPeriodEndsOn,
    DateTimeOffset ExpiresOn, int ApprovalsRecorded, int ApprovalsRequired, bool ApprovedByMe, int ReplacementCount, string OpenedByOperatorDisplayName);

public sealed record OwnerRecoveryPage(IReadOnlyList<OwnerRecoveryListItem> Items, int TotalCount, string? NextCursor, int Limit, string Status, long WaitingPeriodSeconds, string ApprovalAssurance, Guid CorrelationId);

// ---- platform plane: replacement-owner member picker (C-106) ----

/// <summary>Search the organization's members who may be proposed as replacement owners (server-side filter and paging).</summary>
public sealed record OwnerRecoveryMemberSearchRequest(string? Query, string? Cursor, int? Limit);

/// <summary>
/// The minimum member metadata for choosing a replacement: the immutable user id, the verified identity and the best known name
/// (a hint recorded when the member was onboarded or approved; the subject when none exists). Only enabled, non-owner, non-operator
/// members are returned. No roles, teams, records, shares or documents.
/// </summary>
public sealed record OwnerRecoveryMemberItem(Guid AuthorityUserId, string IssuerUri, string SubjectIdentifier, string DisplayName, DateTimeOffset MemberSince);

public sealed record OwnerRecoveryMemberPage(IReadOnlyList<OwnerRecoveryMemberItem> Items, int TotalCount, string? NextCursor, int Limit, Guid CorrelationId);

// ---- business plane (enabled owners and Security Administrators of the organization) ----

public sealed record OwnerRecoveryNoticeRequest(Guid OrganizationId);

public sealed record OwnerRecoveryNoticeSubject(string Role, Guid AuthorityUserId, string SubjectIdentifier, string DisplayNameHint, bool IsNewIdentity, string? UnavailabilityReason);

/// <summary>The organization's open recovery case for an owner or Security Administrator (null case when none). No operator identities.</summary>
public sealed record OwnerRecoveryNotice(OwnerRecoveryNoticeCase? Case, bool CanObject, Guid CorrelationId);

public sealed record OwnerRecoveryNoticeCase(Guid CaseId, string IncidentReference, string Justification, DateTimeOffset OpenedOn, DateTimeOffset WaitingPeriodEndsOn, int ApprovalsRecorded, int ApprovalsRequired,
    IReadOnlyList<OwnerRecoveryNoticeSubject> Subjects);

public sealed record OwnerRecoveryObjectionRequest(Guid OrganizationId, Guid CaseId, string? Reason, string? StepUpEvidence);

public sealed record OwnerRecoveryObjectionResponse(Guid CaseId, string State, DateTimeOffset ObjectedOn, Guid CorrelationId);
