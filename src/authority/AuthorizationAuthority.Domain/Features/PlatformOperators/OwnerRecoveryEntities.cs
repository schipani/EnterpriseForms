namespace AuthorizationAuthority.Domain.Features.PlatformOperators;

/// <summary>
/// C8 owner recovery (C-105): one case restoring the owner quorum of an existing organization. Platform-scoped and Authority-owned;
/// immutable after it is opened (a different scope is a new case). Grants nothing until execution, which only adds owner roles (and a
/// binding for a new identity). Waiting period from opening; any enabled owner or Security Administrator may object until execution,
/// and an objection ends the case. At most one open case per organization.
/// </summary>
public sealed class OrganizationOwnerRecoveryCase
{
    public Guid OrganizationOwnerRecoveryCaseId { get; set; }

    public Guid OrganizationId { get; set; }

    /// <summary>One of <see cref="OwnerRecoveryStates"/>.</summary>
    public string RecoveryStateName { get; set; } = OwnerRecoveryStates.Open;

    public string IncidentReferenceText { get; set; } = string.Empty;

    public string RecoveryReasonText { get; set; } = string.Empty;

    public Guid OpenedByPlatformOperatorId { get; set; }

    public string CreateIdempotencyKey { get; set; } = string.Empty;

    public byte[] CreateRequestDigest { get; set; } = [];

    /// <summary>The canonical statement every approving operator signs (text, never jsonb).</summary>
    public string RecoveryStatementJson { get; set; } = string.Empty;

    public byte[] RecoveryStatementSha256 { get; set; } = [];

    /// <summary>SHA-256 of the organization's owner set (user id + enabled state) when the case was opened; any change invalidates the case.</summary>
    public byte[] OwnerSetSha256 { get; set; } = [];

    public DateTimeOffset CaseOpenedOn { get; set; }

    /// <summary>Execution is refused before this instant (trusted Authority clock).</summary>
    public DateTimeOffset WaitingPeriodEndsOn { get; set; }

    public DateTimeOffset CaseExpiresOn { get; set; }

    public DateTimeOffset? RecoveredOn { get; set; }

    public Guid? ExecutedByPlatformOperatorId { get; set; }

    public Guid? ExecutedBySecurityCommandId { get; set; }

    public DateTimeOffset? ObjectedOn { get; set; }

    public Guid? ObjectedByAuthorityUserId { get; set; }

    public string? ObjectionReasonText { get; set; }

    public DateTimeOffset? CaseClosedOn { get; set; }

    public Guid? ClosedByPlatformOperatorId { get; set; }

    /// <summary>One of <see cref="OwnerRecoveryClosureReasons"/> once closed.</summary>
    public string? CaseClosureReasonName { get; set; }

    public string? ClosureJustificationText { get; set; }

    public string? LastExecutionFailureCode { get; set; }

    public DateTimeOffset? LastExecutionFailureOn { get; set; }

    public long CaseRowRevision { get; set; } = 1;
}

/// <summary>One person a case names: a replacement owner (existing member or new identity) or an owner declared unavailable. Immutable.</summary>
public sealed class OrganizationOwnerRecoverySubject
{
    public Guid OrganizationOwnerRecoverySubjectId { get; set; }

    public Guid OrganizationOwnerRecoveryCaseId { get; set; }

    /// <summary>One of <see cref="OwnerRecoverySubjectRoles"/>.</summary>
    public string RecoverySubjectRoleName { get; set; } = string.Empty;

    public int SubjectOrdinalNumber { get; set; }

    /// <summary>The existing member's user id, or the Authority-generated id a new identity will receive.</summary>
    public Guid SubjectAuthorityUserId { get; set; }

    public string SubjectIssuerUri { get; set; } = string.Empty;

    public string SubjectIdentifier { get; set; } = string.Empty;

    public string SubjectDisplayNameHint { get; set; } = string.Empty;

    public bool IsNewIdentity { get; set; }

    /// <summary>Replacement: one of <see cref="OnboardingVerificationMethods"/>.</summary>
    public string? VerificationMethodName { get; set; }

    public string? VerificationReferenceText { get; set; }

    /// <summary>Declared unavailable: one of <see cref="OwnerUnavailabilityReasons"/>.</summary>
    public string? UnavailabilityReasonName { get; set; }

    public string? EvidenceReferenceText { get; set; }

    public string? EvidenceSummaryText { get; set; }
}

/// <summary>One operator's approval of one recovery case (three distinct operators are required).</summary>
public sealed class OrganizationOwnerRecoveryApproval
{
    public Guid OrganizationOwnerRecoveryApprovalId { get; set; }

    public Guid OrganizationOwnerRecoveryCaseId { get; set; }

    public Guid PlatformOperatorId { get; set; }

    public byte[] ApprovalStatementSha256 { get; set; } = [];

    public string ApprovalJustificationText { get; set; } = string.Empty;

    public string ApprovalEvidenceKindName { get; set; } = string.Empty;

    public byte[] ApprovalEvidenceSha256 { get; set; } = [];

    public string ApprovalEvidenceIdentifier { get; set; } = string.Empty;

    public DateTimeOffset ApprovalRecordedOn { get; set; }

    /// <summary>One of <see cref="OnboardingApprovalStates"/>. Closed rows never change again.</summary>
    public string ApprovalStateName { get; set; } = OnboardingApprovalStates.Valid;

    public DateTimeOffset? ApprovalClosedOn { get; set; }

    public string? ApprovalClosureReasonName { get; set; }
}

/// <summary>
/// D8-57 as directed (C-105): for a period after recovery, owners recovered by the same case cannot add each other to teams or grant
/// each other the designer role. The permanent OB-F8 rule (no self-join; only a different owner adds an owner) applies regardless.
/// </summary>
public sealed class OrganizationRecoveredOwnerProbation
{
    public Guid OrganizationRecoveredOwnerProbationId { get; set; }

    public Guid OrganizationId { get; set; }

    public Guid AuthorityUserId { get; set; }

    public Guid OrganizationOwnerRecoveryCaseId { get; set; }

    public DateTimeOffset ProbationCreatedOn { get; set; }

    public DateTimeOffset ProbationEndsOn { get; set; }
}

public static class OwnerRecoveryStates
{
    public const string Open = "Open";
    public const string Objected = "Objected";
    public const string Recovered = "Recovered";
    public const string Cancelled = "Cancelled";
    public const string Expired = "Expired";
    public const string Invalidated = "Invalidated";

    public static bool IsClosed(string state) => state is not Open;
}

public static class OwnerRecoveryClosureReasons
{
    public const string Cancelled = "CancelledByOperator";
    public const string Objected = "ObjectedByOrganization";
    public const string Expired = "CaseExpired";
    public const string OwnerSetChanged = "OwnerSetChanged";
    public const string DeclaredOwnerActive = "DeclaredOwnerActive";
    public const string IdentityConflict = "IdentityConflict";
    public const string NoLongerApplicable = "NoLongerApplicable";
}

public static class OwnerRecoverySubjectRoles
{
    public const string Replacement = "Replacement";
    public const string DeclaredUnavailable = "DeclaredUnavailable";
}

public static class OwnerUnavailabilityReasons
{
    public const string LeftOrganization = "LeftOrganization";
    public const string AccountDeleted = "AccountDeleted";
    public const string Deceased = "Deceased";
    public const string LostAuthenticator = "LostAuthenticator";
    public const string Unresponsive = "Unresponsive";

    public static readonly IReadOnlyList<string> All = [LeftOrganization, AccountDeleted, Deceased, LostAuthenticator, Unresponsive];
}
