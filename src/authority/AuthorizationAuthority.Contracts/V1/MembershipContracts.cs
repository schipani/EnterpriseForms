namespace AuthorizationAuthority.Contracts.V1;

// ---- C8 M2 (AC-7): membership and organization-role administration. Owners only; step-up; journaled. ----

/// <summary>
/// Payload of GrantOrganizationRole / RevokeOrganizationRole. <c>RoleName</c>: <c>organization_owner</c> or
/// <c>designer</c>. <c>ProposalId</c>: required for <c>organization_owner</c> — another owner's pending, unexpired
/// ProposeOrganizationOwnerChange for exactly this person and change.
/// </summary>
public sealed record OrganizationRolePayload(Guid AuthorityUserId, string RoleName, string Reason, Guid? ProposalId = null, string? StepUpEvidence = null);

/// <summary>Payload of ProposeOrganizationOwnerChange. <c>ProposedChange</c>: GrantOwner, RevokeOwner, DisableOwner, EnableOwner.</summary>
public sealed record OwnerChangeProposalPayload(Guid AuthorityUserId, string ProposedChange, string Reason, string? StepUpEvidence = null);

/// <summary>Payload of DisableIdentityBinding / EnableIdentityBinding. <c>ProposalId</c>: required when the person holds organization_owner.</summary>
public sealed record MembershipStatePayload(Guid AuthorityUserId, string Reason, Guid? ProposalId = null, string? StepUpEvidence = null);

/// <summary>
/// Payload of RequestOrganizationMembership. The issuer must be the organization's identity issuer (the Microsoft Entra
/// tenant its owners were bootstrapped with); the subject is the immutable object id, never an email address.
/// <c>InitialRoleName</c>: null or <c>designer</c>.
/// </summary>
public sealed record MembershipRequestPayload(string IssuerUri, string SubjectIdentifier, string DisplayNameHint, string? InitialRoleName, string Reason, string? StepUpEvidence = null);

public sealed record OrganizationRosterRequest(Guid OrganizationId);

/// <summary>
/// One identity binding of the organization. <c>OrganizationRoles</c> are the Authority's organization relations
/// (organization_owner, designer, system_administrator). A disabled member keeps its roles and edges; they are unusable
/// while <c>MembershipEnabled</c> is false. <c>DisplayNameHint</c> comes from the approved membership request, if any.
/// </summary>
public sealed record OrganizationRosterMember(Guid AuthorityUserId, string IssuerUri, string SubjectIdentifier, bool MembershipEnabled, DateTimeOffset BoundOn, DateTimeOffset? DisabledOn,
    IReadOnlyList<string> OrganizationRoles, string? DisplayNameHint);

/// <summary><c>ProposalState</c>: Pending, Expired or Consumed (computed against the Authority clock).</summary>
public sealed record OrganizationOwnerProposal(Guid ProposalId, Guid TargetAuthorityUserId, string ProposedChange, Guid ProposedByAuthorityUserId, string Reason, DateTimeOffset ProposedOn, DateTimeOffset ExpiresOn,
    string ProposalState, Guid? ConsumedBySecurityCommandId);

/// <summary><c>RequestState</c>: Pending, Expired, Approved or Rejected.</summary>
public sealed record OrganizationMembershipRequestEntry(Guid RequestId, Guid RequestedAuthorityUserId, string IssuerUri, string SubjectIdentifier, string DisplayNameHint, string? InitialRoleName,
    Guid RequestedByAuthorityUserId, string Reason, DateTimeOffset RequestedOn, DateTimeOffset ExpiresOn, string RequestState, DateTimeOffset? DecidedOn, string? DecisionReason);

/// <summary>
/// AC-7.3 complete roster for organization owners and Security Administrators (anyone else: not found). Proposals and
/// requests from the last 30 days. <c>CanManageMembers</c>: the caller is an organization owner. <c>CommandsOperational</c>:
/// the Authority can accept step-up here (false in staging and production until PB-08). <c>OperatorApprovalOperational</c>:
/// membership requests can be approved by the operator plane here (Development/Testing only).
/// </summary>
public sealed record OrganizationRosterResponse(bool CanManageMembers, bool CanAdministerSecurity, bool CommandsOperational, string CommandsUnavailableReason, bool OperatorApprovalOperational,
    string? OrganizationIdentityIssuer, IReadOnlyList<OrganizationRosterMember> Members, IReadOnlyList<OrganizationOwnerProposal> OwnerProposals,
    IReadOnlyList<OrganizationMembershipRequestEntry> MembershipRequests, long AuthorizationRevision, Guid CorrelationId);

/// <summary>What each operator approver signs for a membership request (canonical JSON; SHA-256 = statement_sha256).</summary>
public sealed record MembershipApprovalStatement(int StatementVersion, Guid OperationId, string OperationKind, Guid OrganizationId, Guid TargetAuthorityUserId, string TargetIssuerUri,
    string TargetSubjectIdentifier, string? InitialRoleName, Guid RequestedByAuthorityUserId, DateTimeOffset RequestExpiresOn, string Justification);

/// <summary>Operator plane: approve a membership request (dry run first). The request id is the single-use operation id.</summary>
public sealed record MembershipApprovalRequest(Guid ConfirmOrganizationId, string Justification, bool DryRun, IReadOnlyList<string>? Approvals = null);

/// <summary>Operator plane: reject a pending membership request (single operator; grants nothing, so no dual approval).</summary>
public sealed record MembershipRejectionRequest(Guid ConfirmOrganizationId, string Reason);

/// <summary>
/// Preview or outcome of an operator membership decision. Field names match the Security Administrator provisioning
/// response where the approval tooling reads them (operation id/kind, organization, target, statement digest).
/// </summary>
public sealed record MembershipApprovalResponse(bool DryRun, bool Eligible, string? RefusalCode, string? RefusalDetail, Guid OperationId, string OperationKind, Guid OrganizationId, string OrganizationDisplayName,
    OperatorOrganizationPrincipal Target, string? InitialRoleName, string RequestState, IReadOnlyList<string> Consequences, string ApprovalStatementJson, string ApprovalStatementSha256, string ApprovalMode,
    string ApprovalAssurance, int RequiredApprovals, Guid? SecurityCommandId, string? CommandStatus, bool Replayed, long AuthorizationRevision, Guid CorrelationId);

public sealed record OperatorMembershipRequests(Guid OrganizationId, string OrganizationDisplayName, string ApprovalMode, bool ApprovalsOperational, IReadOnlyList<OrganizationMembershipRequestEntry> Requests, Guid CorrelationId);
