namespace AuthorizationAuthority.Contracts.V1;

// C8 onboarding (C-104, D8-45…D8-53 as approved). Platform plane only (Platform Console credential + operator assertion), except the
// read-only key-custody status on the operator plane for the development custody tool. Nothing here confers an organization
// permission on an operator: the only organization rows written are the initial owners' bindings and owner roles, and only by
// initialization of a request two different operators approved.

public static class OrganizationOnboardingFilters
{
    public const string NeedsApproval = "needs_approval";
    public const string AwaitingKeyCustody = "awaiting_key_custody";
    public const string Initialized = "initialized";
    public const string Closed = "closed";
    public const string All = "all";

    public static readonly IReadOnlyList<string> Values = [NeedsApproval, AwaitingKeyCustody, Initialized, Closed, All];
}

public static class OrganizationDirectoryFilters
{
    public const string All = "all";

    /// <summary>Fewer than two enabled owners (owner recovery candidates).</summary>
    public const string NeedsOwners = "needs_owners";

    public static readonly IReadOnlyList<string> Values = [All, NeedsOwners];
}

/// <summary>Display state of a request: the stored state, refined by time and by the organization's activation.</summary>
public static class OrganizationOnboardingDisplayStates
{
    public const string AwaitingApproval = "AwaitingApproval";
    public const string AwaitingKeyProvisioning = "AwaitingKeyProvisioning";
    public const string Activating = "Activating";
    public const string Active = "Active";
    public const string Rejected = "Rejected";
    public const string Cancelled = "Cancelled";
    public const string Expired = "Expired";
}

// ---- statement (canonical JSON; its SHA-256 is what both approvals bind) ----

/// <summary>
/// Everything an approving operator reviews, built by the Authority from its own rows when the request is created and never
/// changed afterwards (immutable request). No justification of an approver and no approver, so both operators sign the same bytes.
/// </summary>
public sealed record OrganizationOnboardingStatement(
    int StatementVersion,
    string OperationKind,
    Guid OperationId,
    Guid OrganizationId,
    string OrganizationDisplayName,
    string OrganizationNormalizedName,
    string IdentityIssuerUri,
    IReadOnlyList<OrganizationOnboardingOwnerStatement> InitialOwners,
    int OwnerCount,
    string IntegrityKeyName,
    string RequestReasonSha256,
    Guid CreatedByPlatformOperatorId,
    DateTimeOffset RequestCreatedOn,
    DateTimeOffset RequestExpiresOn);

public sealed record OrganizationOnboardingOwnerStatement(Guid AuthorityUserId, string IssuerUri, string SubjectIdentifier, string DisplayNameHint, string VerificationMethod, string VerificationReference);

// ---- requests ----

public sealed record OrganizationOnboardingOwnerInput(string? IssuerUri, string? SubjectIdentifier, string? DisplayNameHint, string? VerificationMethod, string? VerificationReference);

/// <summary>Create (no evidence: creating grants nothing; approvals carry evidence). <c>DryRun</c> validates and reports conflicts without storing.</summary>
public sealed record OrganizationOnboardingCreateRequest(string? IdempotencyKey, string? OrganizationDisplayName, IReadOnlyList<OrganizationOnboardingOwnerInput>? InitialOwners, string? Justification, bool DryRun = false);

/// <summary>Approve, withdraw, reject, cancel or initialize: the reviewed statement digest, the operator's own justification and evidence.</summary>
public sealed record OrganizationOnboardingDecisionRequest(string? ExpectedStatementSha256, string? Justification, string? EvidenceKind, string? Evidence, bool DryRun = false);

public sealed record OrganizationOnboardingSearchRequest(string? Query, string? Status, string? Cursor, int? Limit);

public sealed record OrganizationDirectorySearchRequest(string? Query, string? Filter, string? Cursor, int? Limit);

// ---- responses ----

public sealed record OrganizationOnboardingCreateResponse(bool DryRun, bool Eligible, string? RefusalCode, string? RefusalDetail, IReadOnlyList<string> Warnings, OrganizationOnboardingDetail? Request, bool Replayed, Guid CorrelationId);

public sealed record OrganizationOnboardingDecisionResponse(
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
    string State,
    string DisplayState,
    Guid? SecurityCommandId,
    string? ArchiveState,
    bool Replayed,
    string ApprovalAssurance,
    Guid CorrelationId);

public sealed record OrganizationOnboardingOwnerView(Guid AuthorityUserId, string IssuerUri, string SubjectIdentifier, string DisplayNameHint, string VerificationMethod, string VerificationReference, bool MemberOfOtherOrganizations);

public sealed record OrganizationOnboardingApprovalView(Guid ApprovalId, Guid PlatformOperatorId, string OperatorDisplayName, bool IsMine, string Justification, DateTimeOffset RecordedOn, string State, string? ClosureReason);

public sealed record OrganizationOnboardingEligibility(bool CanApprove, bool CanWithdraw, bool CanReject, bool CanCancel, bool CanInitialize, string? ApproveRefusalCode, string? ApproveRefusalDetail, string? InitializeRefusalCode, string? InitializeRefusalDetail);

/// <summary><c>Status</c>: NotRequired (not awaiting custody), Missing, Ready, Invalid (exists but unusable), Unavailable (provider unreachable).</summary>
public sealed record OrganizationOnboardingKeyCustody(string KeyName, string Status, string? Detail);

/// <summary>After initialization: <c>Status</c> Activating (first command not yet Effective), Active, or Quarantined.</summary>
public sealed record OrganizationActivation(string Status, long AuthorizationRevision, long ProjectionAppliedRevision);

public sealed record OrganizationOnboardingDetail(
    Guid RequestId,
    Guid OrganizationId,
    string OrganizationDisplayName,
    string IdentityIssuerUri,
    string State,
    string DisplayState,
    string Justification,
    Guid CreatedByPlatformOperatorId,
    string CreatedByOperatorDisplayName,
    DateTimeOffset CreatedOn,
    DateTimeOffset ExpiresOn,
    DateTimeOffset? ApprovedOn,
    DateTimeOffset? KeyCustodyExpiresOn,
    DateTimeOffset? InitializedOn,
    DateTimeOffset? ClosedOn,
    string? ClosureReason,
    string? ClosureJustification,
    string? LastInitializationFailureCode,
    DateTimeOffset? LastInitializationFailureOn,
    IReadOnlyList<OrganizationOnboardingOwnerView> Owners,
    IReadOnlyList<OrganizationOnboardingApprovalView> Approvals,
    int ApprovalsRecorded,
    int ApprovalsRequired,
    string StatementJson,
    string StatementSha256,
    OrganizationOnboardingKeyCustody KeyCustody,
    OrganizationActivation? Activation,
    OrganizationOnboardingEligibility MyEligibility,
    long RowRevision,
    string ApprovalAssurance,
    Guid CorrelationId);

public sealed record OrganizationOnboardingListItem(
    Guid RequestId,
    Guid OrganizationId,
    string OrganizationDisplayName,
    int OwnerCount,
    string State,
    string DisplayState,
    DateTimeOffset CreatedOn,
    DateTimeOffset ExpiresOn,
    int ApprovalsRecorded,
    int ApprovalsRequired,
    bool ApprovedByMe,
    string CreatedByOperatorDisplayName);

/// <summary><c>ApprovedIdentityIssuers</c>: the owner issuers this environment accepts (the console's create form offers exactly these); empty when onboarding is unavailable.</summary>
public sealed record OrganizationOnboardingPage(IReadOnlyList<OrganizationOnboardingListItem> Items, int TotalCount, string? NextCursor, int Limit, string Status, IReadOnlyList<string> ApprovedIdentityIssuers, string ApprovalAssurance, Guid CorrelationId);

public sealed record OrganizationDirectoryItem(
    Guid OrganizationId,
    string OrganizationDisplayName,
    string Lifecycle,
    int EnabledOwnerCount,
    int SecurityAdministratorCount,
    int MemberCount,
    bool NeedsOwners,
    Guid? OnboardingRequestId);

public sealed record OrganizationDirectoryPage(IReadOnlyList<OrganizationDirectoryItem> Items, int TotalCount, string? NextCursor, int Limit, string Filter, Guid CorrelationId);

public sealed record OrganizationDirectoryOwner(Guid AuthorityUserId, string IssuerUri, string SubjectIdentifier, bool Enabled);

public sealed record OrganizationDirectoryDetail(
    OrganizationDirectoryItem Summary,
    long AuthorizationRevision,
    long ProjectionAppliedRevision,
    IReadOnlyList<OrganizationDirectoryOwner> Owners,
    IReadOnlyList<string> OnboardingApprovedBy,
    Guid CorrelationId);

/// <summary>Operator plane, read-only: what the development custody tool may provision for one request (nothing else).</summary>
public sealed record OrganizationKeyCustodyStatus(Guid RequestId, Guid OrganizationId, string State, bool ProvisionAllowed, string KeyName, string KeyPurpose, string ProviderName, string KeyStatus, DateTimeOffset? KeyCustodyExpiresOn, Guid CorrelationId);
