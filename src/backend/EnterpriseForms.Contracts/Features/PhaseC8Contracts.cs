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

/// <summary>
/// A person of this organization (C8 M2). <c>DisplayName</c> is empty for an Authority-bound identity that has never signed
/// in (no name is known yet). <c>SignInStatus</c>: <c>SignedIn</c> (a sign-in was recorded), <c>NotRecorded</c> (seen in the
/// business directory before sign-ins were recorded), <c>NeverSignedIn</c> (bound by the Authority, never seen).
/// <c>AvailableActions</c> are hints for the caller (the Authority decides every command): <c>AppointSecurityAdministrator</c>,
/// <c>RemoveSecurityAdministrator</c>.
/// </summary>
public sealed record OrganizationMemberResponse(Guid UserId, string DisplayName, string IdentityIssuer, string IdentitySubject, DateTimeOffset? FirstSeenOn, IReadOnlyList<string> AuthorityRoles, IReadOnlyList<DirectoryTeamResponse> Teams, bool IsCurrentUser,
    DateTimeOffset? LastSignedInOn = null, string SignInStatus = "NotRecorded", IReadOnlyList<string>? AvailableActions = null, string MembershipState = "Enabled", DateTimeOffset? DisabledOn = null);

/// <summary>
/// C8 M2 (AC-7): the first half of a two-owner change. <c>ProposedChange</c>: GrantOwner, RevokeOwner, DisableOwner,
/// EnableOwner. <c>ProposalState</c>: Pending, Expired or Consumed. <c>CanApprove</c>: the caller is another owner than the
/// proposer and the person (the Authority re-checks everything when the approval is submitted).
/// </summary>
public sealed record OwnerProposalResponse(Guid ProposalId, Guid TargetUserId, string TargetDisplayName, string ProposedChange, Guid ProposedByUserId, string ProposedByDisplayName, string Reason,
    DateTimeOffset ProposedOn, DateTimeOffset ExpiresOn, string ProposalState, bool CanApprove);

/// <summary>C8 M2 (AC-7.4): an owner's request to add a person. <c>RequestState</c>: Pending (awaiting two operator approvals), Approved, Rejected or Expired. Membership is not sign-in readiness.</summary>
public sealed record MembershipRequestResponse(Guid RequestId, Guid RequestedUserId, string IssuerUri, string SubjectIdentifier, string DisplayNameHint, string? InitialRoleName, Guid RequestedByUserId, string RequestedByDisplayName,
    string Reason, DateTimeOffset RequestedOn, DateTimeOffset ExpiresOn, string RequestState, DateTimeOffset? DecidedOn, string? DecisionReason);

/// <summary>
/// One server page (default 15) of the people directory. <c>Source</c> and <c>CoverageStatement</c> say where rows come from
/// and what is missing; <c>IncludesMembersWhoNeverSignedIn</c> is true only when the Authority roster listed every enabled
/// binding (Security Administrators); <c>RolesAvailable</c> says whether the Authority roster answered.
/// </summary>
public sealed record OrganizationMemberDirectoryResponse(string Source, IReadOnlyList<OrganizationMemberResponse> Members, bool RolesAvailable, string? RolesUnavailableReason,
    int TotalCount = 0, int PageStart = 0, int PageSize = 0, string? NextToken = null, string? PreviousToken = null, bool IncludesMembersWhoNeverSignedIn = false, string CoverageStatement = "",
    bool SecurityAdministratorChangesOperational = false, string? SecurityAdministratorChangesUnavailableReason = null,
    bool CanManageMembers = false, bool MembershipCommandsOperational = false, string? MembershipCommandsUnavailableReason = null, bool OperatorApprovalOperational = false, string? OrganizationIdentityIssuer = null,
    IReadOnlyList<OwnerProposalResponse>? OwnerProposals = null, IReadOnlyList<MembershipRequestResponse>? MembershipRequests = null);

/// <summary>C8 M2: grant or revoke the Designer role (one owner). Owner changes go through proposals.</summary>
public sealed record MemberRoleChangeRequest(string RoleName, string Reason, bool UseDevelopmentStepUp = false);

/// <summary>C8 M2: disable, re-enable, or remove a role from a member (one owner; an owner's membership goes through a proposal).</summary>
public sealed record MemberCommandRequest(string Reason, bool UseDevelopmentStepUp = false);

/// <summary>C8 M2: propose GrantOwner, RevokeOwner, DisableOwner or EnableOwner; another owner approves it within 24 hours.</summary>
public sealed record OwnerProposalRequest(string Change, string Reason, bool UseDevelopmentStepUp = false);

/// <summary>
/// C8 M2 (AC-7.4): ask to add a person. The identity provider is the organization's configured Microsoft Entra tenant (set
/// by the server); <c>SubjectIdentifier</c> is the person's immutable object id, never an email address. <c>InitialRoleName</c>:
/// null or <c>Designer</c>. Two operator approvals are required before the person becomes a member.
/// </summary>
public sealed record CreateMembershipRequestRequest(string SubjectIdentifier, string DisplayNameHint, string? InitialRoleName, string Reason, bool UseDevelopmentStepUp = false);

/// <summary>Outcome of a membership or role command: <c>CommandStatus</c> is Effective once the Authority applied it. <c>ProposalId</c>/<c>RequestId</c> for proposals and requests.</summary>
public sealed record MemberCommandResponse(Guid CommandId, string CommandStatus, long AuthorizationRevision, Guid? ProposalId = null, Guid? RequestId = null, DateTimeOffset? ExpiresOn = null);

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
