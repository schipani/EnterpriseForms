namespace AuthorizationAuthority.Contracts.V1;

// C8 M2 Stages 1-2 (D8-33…D8-44): the platform plane. Reachable only with the Platform Console credential AND a Platform Operator
// assertion naming a registered, active operator; the recovery ceremony and journal replay stay on the operator plane. Nothing
// here is an organization permission: an operator never gains membership, SpiceDB edges or record access.

/// <summary>Claims of a piece of operator evidence (Development/Testing: compact JWS, ES256, the operator's own synthetic key).</summary>
public static class PlatformOperatorEvidenceClaims
{
    public const string Issuer = "enterpriseforms-platform-operator-evidence";
    public const string Audience = "authorization-authority-platform-operator";
    public const string PlatformOperatorId = "platform_operator_id";
    public const string Decision = "decision";
    public const string OperationKind = "operation_kind";
    public const string OperationId = "operation_id";
    public const string OrganizationId = "organization_id";
    public const string StatementDigest = "statement_sha256";
    public const string JustificationDigest = "justification_sha256";
}

public static class PlatformOperationKinds
{
    public const string MembershipApproval = "MembershipApproval";
    public const string PlatformOperatorChange = "PlatformOperatorChange";
    public const string PlatformOperatorSuspension = "PlatformOperatorSuspension";
    public const string PlatformOperatorBootstrap = "PlatformOperatorBootstrap";
}

/// <summary>The four platform permissions (D8-34). V1: every active operator holds all four; checked by the Authority on every call.</summary>
public static class PlatformPermissions
{
    public const string ReviewMembershipRequests = "platform.membership_requests.review";
    public const string DecideMembershipRequests = "platform.membership_requests.decide";
    public const string ViewOperators = "platform.operators.view";
    public const string ManageOperators = "platform.operators.manage";

    public static readonly IReadOnlyList<string> All = [ReviewMembershipRequests, DecideMembershipRequests, ViewOperators, ManageOperators];
}

/// <summary>Status filter of the cross-organization request search.</summary>
public static class PlatformRequestFilters
{
    public const string NeedsDecision = "needs_decision";
    public const string AwaitingFirstApproval = "awaiting_first_approval";
    public const string AwaitingSecondApproval = "awaiting_second_approval";
    public const string Approved = "approved";
    public const string Rejected = "rejected";
    public const string Expired = "expired";
    public const string CancelledOrReplaced = "cancelled_or_replaced";
    public const string All = "all";

    public static readonly IReadOnlyList<string> Values = [NeedsDecision, AwaitingFirstApproval, AwaitingSecondApproval, Approved, Rejected, Expired, CancelledOrReplaced, All];
}

// ---- statements (canonical JSON; their SHA-256 is what evidence binds) ----

/// <summary>
/// D8-36 statement v2: every approval-relevant immutable field of a membership request, built by the Authority from its own rows.
/// No justification and no operator, so two operators sign the identical statement; each justification is bound separately.
/// </summary>
public sealed record MembershipApprovalStatementV2(
    int StatementVersion,
    string OperationKind,
    Guid OperationId,
    Guid OrganizationId,
    string OrganizationDisplayName,
    Guid TargetAuthorityUserId,
    string TargetIssuerUri,
    string TargetSubjectIdentifier,
    string TargetDisplayNameHint,
    string? InitialRoleName,
    Guid RequestedByAuthorityUserId,
    string RequestedByIssuerUri,
    string RequestedBySubjectIdentifier,
    string RequestReasonSha256,
    DateTimeOffset RequestCreatedOn,
    DateTimeOffset RequestExpiresOn,
    string RequestDigest,
    Guid? ReplacesMembershipRequestId);

/// <summary>Two-operator registry change: what the proposer and the confirming operator both sign.</summary>
public sealed record PlatformOperatorChangeStatement(
    int StatementVersion,
    string OperationKind,
    Guid OperationId,
    string Change,
    Guid TargetPlatformOperatorId,
    string TargetIssuerUri,
    string TargetSubjectIdentifier,
    string TargetDisplayName,
    string TargetEvidenceKind,
    string? TargetSyntheticKeyIdentifier,
    string? TargetSyntheticPublicKeySha256,
    long TargetRowRevision,
    Guid ProposedByPlatformOperatorId);

/// <summary>Single-operator emergency suspension.</summary>
public sealed record PlatformOperatorSuspensionStatement(
    int StatementVersion,
    string OperationKind,
    Guid OperationId,
    Guid TargetPlatformOperatorId,
    string TargetIssuerUri,
    string TargetSubjectIdentifier,
    long TargetRowRevision,
    Guid SuspendedByPlatformOperatorId);

/// <summary>Recovery ceremony: approved by two DR-C7-3 custodian approvals (operator plane), not by operators.</summary>
public sealed record PlatformOperatorBootstrapStatement(
    int StatementVersion,
    string OperationKind,
    Guid OperationId,
    IReadOnlyList<PlatformOperatorBootstrapCandidateStatement> Operators,
    string Justification,
    string? IncidentReference,
    int ActiveOperatorCountBefore);

public sealed record PlatformOperatorBootstrapCandidateStatement(string IssuerUri, string SubjectIdentifier, string DisplayName, string EvidenceKind, string? SyntheticKeyIdentifier, string? SyntheticPublicKeySha256);

// ---- shared views ----

public sealed record PlatformIdentity(string IssuerUri, string SubjectIdentifier);

public sealed record PlatformJournalStatus(long Revision, int PendingArchiveEntries, bool Quarantined, string? QuarantineReason);

public sealed record PlatformOperatorSummary(
    Guid PlatformOperatorId,
    string IssuerUri,
    string SubjectIdentifier,
    string DisplayName,
    string Status,
    string EvidenceKind,
    string? SyntheticKeyIdentifier,
    long RowRevision,
    DateTimeOffset ProvisionedOn,
    DateTimeOffset StatusChangedOn,
    string StatusReason);

/// <summary>A new operator identity (Provision or recovery ceremony). Public key only, never a private key.</summary>
public sealed record PlatformOperatorCandidate(string IssuerUri, string SubjectIdentifier, string DisplayName, string EvidenceKind, string? SyntheticKeyIdentifier, string? SyntheticPublicKeyBase64);

// ---- operator plane: recovery ceremony, status, journal replay ----

public sealed record PlatformOperatorBootstrapRequest(
    Guid OperationId,
    IReadOnlyList<PlatformOperatorCandidate> Operators,
    string Justification,
    string? IncidentReference,
    bool DryRun,
    IReadOnlyList<string>? Approvals = null);

/// <summary>Field names match the other operator-plane previews where approval tooling reads them (operation id/kind, organization, target, statement digest).</summary>
public sealed record PlatformCeremonyTarget(Guid AuthorityUserId);

public sealed record PlatformOperatorBootstrapResponse(
    bool DryRun,
    bool Eligible,
    string? RefusalCode,
    string? RefusalDetail,
    Guid OperationId,
    string OperationKind,
    Guid OrganizationId,
    PlatformCeremonyTarget Target,
    string ApprovalStatementJson,
    string ApprovalStatementSha256,
    string ApprovalMode,
    string ApprovalAssurance,
    int RequiredApprovals,
    IReadOnlyList<PlatformOperatorSummary> RegisteredOperators,
    PlatformJournalStatus Journal,
    string JournalArchiveState,
    Guid CorrelationId);

public sealed record PlatformOperatorPlaneStatus(
    string Mode,
    bool Operational,
    string EvidenceAssurance,
    int ActiveOperatorCount,
    bool RecoveryCeremonyAllowed,
    IReadOnlyList<PlatformOperatorSummary> Operators,
    PlatformJournalStatus Journal,
    string? JournalGateRefusal,
    Guid CorrelationId);

public sealed record PlatformJournalReplayResponse(long RevisionBefore, long RevisionAfter, int EventsReplayed, bool GateOpen, string? GateRefusal, Guid CorrelationId);

// ---- platform plane: operator registry ----

public sealed record PlatformOperatorProfile(PlatformOperatorSummary Operator, string Mode, string EvidenceAssurance, IReadOnlyList<string> Permissions, int ActiveOperatorCount, PlatformJournalStatus Journal, Guid CorrelationId);

public sealed record PlatformOperatorProposalView(
    Guid ProposalId,
    string Change,
    Guid TargetPlatformOperatorId,
    string TargetIssuerUri,
    string TargetSubjectIdentifier,
    string TargetDisplayName,
    Guid ProposedByPlatformOperatorId,
    string Justification,
    DateTimeOffset CreatedOn,
    DateTimeOffset ExpiresOn,
    string State,
    string StatementSha256);

public sealed record PlatformOperatorRegistryView(
    IReadOnlyList<PlatformOperatorSummary> Operators,
    IReadOnlyList<PlatformOperatorProposalView> PendingProposals,
    int ActiveOperatorCount,
    bool QuorumAvailable,
    PlatformJournalStatus Journal,
    Guid CorrelationId);

/// <summary>Propose a two-operator change. <c>OperationId</c> becomes the proposal id (and, for Provision, the new operator id). Dry run returns the statement to sign.</summary>
public sealed record PlatformOperatorProposalRequest(
    Guid OperationId,
    string Change,
    Guid? TargetPlatformOperatorId,
    PlatformOperatorCandidate? Candidate,
    string Justification,
    bool DryRun,
    string? EvidenceKind = null,
    string? Evidence = null);

public sealed record PlatformOperatorConfirmationRequest(string ExpectedStatementSha256, string Justification, bool DryRun, string? EvidenceKind = null, string? Evidence = null);

public sealed record PlatformOperatorSuspensionRequest(Guid OperationId, string Justification, bool DryRun, string? EvidenceKind = null, string? Evidence = null);

public sealed record PlatformOperatorDecisionResponse(
    bool DryRun,
    bool Eligible,
    string? RefusalCode,
    string? RefusalDetail,
    string OperationKind,
    Guid OperationId,
    string Decision,
    string StatementJson,
    string StatementSha256,
    string Outcome,
    PlatformOperatorSummary? Target,
    Guid? ProposalId,
    long JournalRevision,
    string JournalArchiveState,
    string EvidenceAssurance,
    Guid CorrelationId);

// ---- platform plane: membership requests ----

public sealed record PlatformMembershipRequestSearchRequest(string? Query = null, string? Status = null, string? Cursor = null, int? Limit = null);

public sealed record PlatformApprovalStatus(int Recorded, int Required, bool ApprovedByMe, bool AwaitingOtherOperator, DateTimeOffset? PendingApprovalExpiresOn);

/// <summary>Metadata only (D8-41): no record, document, team, share or roster data.</summary>
public sealed record PlatformMembershipRequestItem(
    Guid RequestId,
    Guid OrganizationId,
    string OrganizationDisplayName,
    string DisplayNameHint,
    PlatformIdentity Target,
    string? InitialRoleName,
    Guid RequestedByAuthorityUserId,
    PlatformIdentity? RequestedBy,
    DateTimeOffset RequestedOn,
    DateTimeOffset ExpiresOn,
    string RequestState,
    PlatformApprovalStatus ApprovalStatus,
    IReadOnlyList<string> AvailableActions);

public sealed record PlatformMembershipRequestPage(
    IReadOnlyList<PlatformMembershipRequestItem> Items,
    int TotalCount,
    string? NextCursor,
    int Limit,
    string Status,
    PlatformJournalStatus Journal,
    string EvidenceAssurance,
    Guid CorrelationId);

public sealed record PlatformApprovalView(
    Guid ApprovalId,
    Guid PlatformOperatorId,
    string OperatorDisplayName,
    PlatformIdentity OperatorIdentity,
    DateTimeOffset RecordedOn,
    DateTimeOffset ValidUntil,
    string Justification,
    string State,
    string? ClosureReason,
    bool IsMine);

public sealed record PlatformEligibility(bool CanApprove, bool CanReject, bool CanWithdraw, string? RefusalCode, string? RefusalDetail);

public sealed record PlatformMembershipRequestDetail(
    PlatformMembershipRequestItem Request,
    string RequestReason,
    Guid? ReplacesRequestId,
    string StatementJson,
    string StatementSha256,
    IReadOnlyList<string> Consequences,
    IReadOnlyList<PlatformApprovalView> Approvals,
    PlatformEligibility MyEligibility,
    string EvidenceAssurance,
    Guid CorrelationId);

/// <summary>Approve, reject or withdraw. Dry run returns the statement and eligibility without evidence and changes nothing.</summary>
public sealed record PlatformMembershipDecisionRequest(string ExpectedStatementSha256, string Justification, bool DryRun, string? EvidenceKind = null, string? Evidence = null);

public sealed record PlatformMembershipDecisionResponse(
    bool DryRun,
    bool Eligible,
    string? RefusalCode,
    string? RefusalDetail,
    Guid RequestId,
    Guid OrganizationId,
    string Decision,
    string StatementJson,
    string StatementSha256,
    string Outcome,
    int ApprovalsRecorded,
    int ApprovalsRequired,
    Guid? MyApprovalId,
    Guid? SecurityCommandId,
    string? CommandStatus,
    long AuthorizationRevision,
    long JournalRevision,
    string JournalArchiveState,
    string EvidenceAssurance,
    Guid CorrelationId);
