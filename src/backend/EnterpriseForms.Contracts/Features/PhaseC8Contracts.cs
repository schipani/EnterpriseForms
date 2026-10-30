namespace EnterpriseForms.Contracts.Features;

// ---- Phase C8 M1: administration shell (no Authority change). Every value is organization-scoped and decided by the Authority. ----

/// <summary>One independently computed part of the organization overview; an unavailable part never fails the page.</summary>
public sealed record OverviewPartStatus(bool Available, string? UnavailableReason);

/// <summary>Safe organization identity: display name and logo presence (no colours, no secrets).</summary>
public sealed record OrganizationOverviewOrganization(OverviewPartStatus Status, string? OrganizationDisplayName, bool HasLogo, long? BrandingRevision);

/// <summary>Member counts from the business projection (labelled) and Security Administrator count from the Authority roster.</summary>
public sealed record OrganizationOverviewMembers(OverviewPartStatus Status, int? ProjectedMemberCount, string MemberCountSource, int? SecurityAdministratorCount, bool SecurityAdministratorsCanManage);

public sealed record OrganizationOverviewTeams(OverviewPartStatus Status, int? TeamCount);

public sealed record OrganizationOverviewDataProtection(OverviewPartStatus Status, string? MinimumFieldClassification, bool? PlaintextInternalFieldsPermitted, bool? IsUnreviewedDefault, long? SecurityPolicyRevision);

/// <summary>Authentication policy as it stands today: delegated passwordless sign-in; FIDO2 enforcement awaits PB-08. Status only.</summary>
public sealed record OrganizationOverviewAuthentication(string AuthenticationRequirementName, string AuthenticationRolloutStatus, string StatusExplanation);

public sealed record OrganizationOverviewResponse(bool CanManageConfiguration, bool CanAdministerSecurity, OrganizationOverviewOrganization Organization, OrganizationOverviewMembers Members, OrganizationOverviewTeams Teams, OrganizationOverviewDataProtection DataProtection, OrganizationOverviewAuthentication Authentication);

/// <summary>A person seen in this organization: projection row (labels only) with Authority-sourced role badges where available.</summary>
public sealed record OrganizationMemberResponse(Guid UserId, string DisplayName, string IdentityIssuer, string IdentitySubject, DateTimeOffset FirstSeenOn, IReadOnlyList<string> AuthorityRoles, IReadOnlyList<DirectoryTeamResponse> Teams, bool IsCurrentUser);

/// <summary><c>Source</c> names where the rows come from (business projection of identities that have signed in); <c>RolesAvailable</c> says whether the Authority roster answered.</summary>
public sealed record OrganizationMemberDirectoryResponse(string Source, IReadOnlyList<OrganizationMemberResponse> Members, bool RolesAvailable, string? RolesUnavailableReason);

public sealed record TeamSummaryResponse(Guid TeamId, string TeamName, bool IsActive, int MemberCount, DateTimeOffset CreatedOn, DateTimeOffset ModifiedOn);

public sealed record TeamDirectoryResponse(string Source, bool CanManage, IReadOnlyList<TeamSummaryResponse> Teams);

public sealed record TeamMemberResponse(Guid UserId, string DisplayName, DateTimeOffset MemberSince, Guid? AuthorityCommandId);

/// <summary><c>MembershipOperations</c> (C8-M1-01): the team's open add/remove operations from the server, so every administrator sees them; never their idempotency keys.</summary>
public sealed record TeamDetailResponse(Guid TeamId, string TeamName, bool IsActive, DateTimeOffset CreatedOn, DateTimeOffset ModifiedOn, string Source, bool CanManage, IReadOnlyList<TeamMemberResponse> Members, IReadOnlyList<DirectoryUserResponse> AvailableMembers, IReadOnlyList<TeamMembershipOperationResponse> MembershipOperations);

/// <summary>
/// Phase C8 (C8-M1-01, R-4): one durable add/remove operation as the business side tracks it. <c>Completed</c> means terminal;
/// <c>RosterUpdated</c> means the Worker projected the Authority's confirmed state (the only way the roster changes).
/// <c>OperationState</c>: AwaitingCommit, AwaitingEffective, AwaitingQuarantineClearance, ReconciliationError, Projected,
/// Rejected, AuthorityFailed, NotCommitted. Never carries the idempotency key.
/// </summary>
public sealed record TeamMembershipOperationResponse(
    Guid OperationId,
    Guid TeamId,
    Guid MemberUserId,
    string MemberDisplayName,
    string Direction,
    string OperationState,
    bool Completed,
    bool RosterUpdated,
    string? OutcomeCode,
    bool SupersededByLaterChange,
    string? RejectionReasonCode,
    string? ErrorCode,
    bool NeedsAttention,
    bool LateCommitDetected,
    bool Reviewed,
    DateTimeOffset StartedOn,
    DateTimeOffset? CompletedOn,
    bool CanRecheck,
    bool CanMarkReviewed);

/// <summary>Add/remove response: the Authority command outcome (<c>Command.Effective</c> = effective in the Authority) and the business operation that projects it.</summary>
public sealed record TeamMembershipChangeResponse(CommandOutcome Command, TeamMembershipOperationResponse Operation);

/// <summary>Display-only rename of the team projection (D8-8); <c>ExpectedModifiedOn</c> is the detail's <c>modifiedOn</c> (409 TEAM_MODIFIED_CONCURRENTLY when stale).</summary>
public sealed record RenameTeamRequest(string TeamName, DateTimeOffset ExpectedModifiedOn);
