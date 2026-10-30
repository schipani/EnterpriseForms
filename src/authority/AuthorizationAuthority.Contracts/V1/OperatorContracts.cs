namespace AuthorizationAuthority.Contracts.V1;

// Operator-plane contract. Reachable only with the separate operator credential; never with the business workload credential.
// Stage A: the bootstrap replaces the PIM/dual-control provisioning workflow that production requires (documented gap).

public sealed record BootstrapIdentity(Guid AuthorityUserId, string IssuerUri, string SubjectIdentifier, IReadOnlyList<string> OrganizationRoles);

public static class OrganizationRoleNames
{
    public const string OrganizationOwner = "organization_owner";
    public const string SystemAdministrator = "system_administrator";
    public const string Designer = "designer";
}

public sealed record BootstrapOrganizationRequest(Guid OrganizationId, string OrganizationDisplayName, IReadOnlyList<BootstrapIdentity> Identities);

public sealed record BootstrapOrganizationResponse(Guid OrganizationId, long AuthorizationRevision, Guid BootstrapCommandId, string IntegrityKeyName, int SigningKeyVersion, Guid CorrelationId);

public sealed record OrganizationGateStatus(Guid OrganizationId, long AuthorizationRevision, long ProjectionAppliedRevision, long ContentIntegrityRevision, ProjectionGateState ProjectionGateState, int PendingArchiveEntries, bool IntegrityQuarantineEnabled);

/// <summary>Testing-environment-only deterministic crash points (SEC-P09). Refused unless FaultInjection:Enabled.</summary>
public sealed record FaultInjectionRequest(string FaultName);

public static class FaultNames
{
    public const string None = "None";
    public const string CrashAfterJournalCommit = "CrashAfterJournalCommit";
    public const string CrashAfterProjectionWrite = "CrashAfterProjectionWrite";
    public const string CrashAfterContentHeadCommit = "CrashAfterContentHeadCommit";

    /// <summary>Testing only: terminate after a prepared content operation is durably stored, before the response is sent (the caller sees a transport failure and retries).</summary>
    public const string CrashAfterPrepareCommit = "CrashAfterPrepareCommit";

    /// <summary>Testing only: hold the next content commit before it takes the organization lock until released (fault "None") or 60 s pass (Phase C5 revocation races).</summary>
    public const string PauseBeforeContentCommit = "PauseBeforeContentCommit";
}

/// <summary>Testing only: state of the fault injector (armed fault and number of operations currently held at a pause point).</summary>
public sealed record FaultInjectionStatus(string ArmedFault, int PausedOperations);

/// <summary>Testing only: override of the contributor-write switch (null restores the deployment configuration).</summary>
public sealed record ContributorWriteOverrideRequest(bool? Enabled);

public sealed record ContributorWriteSwitchState(bool Enabled, bool ConfiguredEnabled);

// ---- DR-C7-3: first and recovery Security Administrator for an existing organization (operator plane only) ----

/// <summary>The two separate operator operations; a recovery is never a first appointment and vice versa.</summary>
public static class SecurityAdministratorProvisioningKinds
{
    public const string FirstAppointment = "FirstAppointment";
    public const string Recovery = "Recovery";
}

/// <summary>Claims of an operator approval (compact JWS, ES256, one per approver key). Bound to the approval statement digest.</summary>
public static class OperatorApprovalClaims
{
    public const string Issuer = "enterpriseforms-operator-approval";
    public const string Audience = "authorization-authority-operator-approval";
    public const string StatementDigest = "statement_sha256";
    public const string OperationId = "operation_id";
    public const string OperationKind = "operation_kind";
    public const string OrganizationId = "organization_id";
    public const string TargetAuthorityUserId = "target_authority_user_id";
}

/// <summary>
/// What each approver signs (canonical JSON; its SHA-256 is the <c>statement_sha256</c> claim): the exact organization,
/// member (immutable identity), role, operation kind, justification, incident reference and recovery scope, under a
/// single-use operation id.
/// </summary>
public sealed record SecurityAdministratorApprovalStatement(
    int StatementVersion,
    Guid OperationId,
    string OperationKind,
    Guid OrganizationId,
    Guid TargetAuthorityUserId,
    string TargetIssuerUri,
    string TargetSubjectIdentifier,
    string RoleName,
    string Justification,
    string? IncidentReference,
    IReadOnlyList<Guid> RemovedAdministratorIds);

/// <summary>
/// First Security Administrator of an organization that has no usable one. <c>DryRun</c> returns the preview and the
/// statement to approve without changing anything; execution needs two approvals from distinct registered approvers.
/// </summary>
public sealed record FirstSecurityAdministratorRequest(
    Guid OperationId,
    Guid ConfirmOrganizationId,
    Guid TargetAuthorityUserId,
    string TargetIssuerUri,
    string TargetSubjectIdentifier,
    string Justification,
    bool DryRun,
    IReadOnlyList<string>? Approvals = null);

/// <summary>
/// Recovery when the organization's Security Administrators are unavailable: appoint one verified member and, explicitly,
/// remove named unavailable administrators. Incident reference required; raises a security alert.
/// </summary>
public sealed record SecurityAdministratorRecoveryRequest(
    Guid OperationId,
    Guid ConfirmOrganizationId,
    Guid TargetAuthorityUserId,
    string TargetIssuerUri,
    string TargetSubjectIdentifier,
    string Justification,
    string IncidentReference,
    IReadOnlyList<Guid> RemovedAdministratorIds,
    bool DryRun,
    IReadOnlyList<string>? Approvals = null);

public sealed record OperatorOrganizationPrincipal(Guid AuthorityUserId, string IssuerUri, string SubjectIdentifier, bool IdentityEnabled, bool IsSecurityAdministrator, DateTimeOffset? AppointedOn);

/// <summary>Operator view of an organization's Security Administrators and verified members (identities only; display names live in the business directory).</summary>
public sealed record OperatorSecurityAdministratorStatus(
    Guid OrganizationId,
    string OrganizationDisplayName,
    long AuthorizationRevision,
    int UsableAdministratorCount,
    string ApprovalMode,
    bool ApprovalsOperational,
    string ApprovalAssurance,
    int RequiredApprovals,
    int MaxApprovalLifetimeSeconds,
    IReadOnlyList<OperatorOrganizationPrincipal> Members,
    Guid CorrelationId);

/// <summary>Preview (dry run) or outcome. <c>Replayed</c>: the operation id was already executed with this statement; nothing new happened.</summary>
public sealed record SecurityAdministratorProvisioningResponse(
    bool DryRun,
    bool Eligible,
    string? RefusalCode,
    string? RefusalDetail,
    Guid OperationId,
    string OperationKind,
    Guid OrganizationId,
    string OrganizationDisplayName,
    OperatorOrganizationPrincipal Target,
    string RoleName,
    IReadOnlyList<OperatorOrganizationPrincipal> CurrentAdministrators,
    IReadOnlyList<Guid> RemovedAdministratorIds,
    IReadOnlyList<string> Consequences,
    string ApprovalStatementJson,
    string ApprovalStatementSha256,
    string ApprovalMode,
    string ApprovalAssurance,
    int RequiredApprovals,
    int MaxApprovalLifetimeSeconds,
    Guid? SecurityCommandId,
    string? CommandStatus,
    bool Replayed,
    long AuthorizationRevision,
    Guid CorrelationId);
