using System.Text.Json;

namespace AuthorizationAuthority.Contracts.V1;

// Private internal contract (/internal/v1). Serialized with snake_case property names.
// Identity and tenant are resolved by the authority from the workload credential and the delegated user
// assertion; the organization_id in these DTOs is an assertion to check, never a source of truth.

public enum ResourceKind
{
    FormDefinition,
    FormRecord,
    FieldItem,
    DocumentItem,
    Team,
    Organization,
}

public enum RequestedAction
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

    /// <summary>Organization-level security administration (SpiceDB <c>administer_security</c>: system administrators). Amendment A1 governance routes accept it alongside ManageOrganization.</summary>
    AdministerSecurity,
}

public sealed record ResourceRef(ResourceKind ResourceKind, Guid ResourceId);

public sealed record PermissionCheckRequest(
    Guid OrganizationId,
    ResourceRef Resource,
    RequestedAction RequestedAction,
    Guid? UnlockReference = null,
    long? MinimumContentRevision = null);

public sealed record PermissionDecision(
    bool Allowed,
    string? DenialReasonCode,
    long AuthorizationRevision,
    long ProjectionRevision,
    long? ContentRevision,
    Guid CorrelationId);

public sealed record PermissionCheckBatchRequest(IReadOnlyList<PermissionCheckRequest> Checks);

public sealed record PermissionCheckBatchResponse(IReadOnlyList<PermissionDecision> Decisions);

public sealed record DiscoverRecordsRequest(Guid OrganizationId, int PageSize, string? ContinuationToken = null);

public sealed record DiscoverResponse(IReadOnlyList<Guid> FormRecordIds, long AuthorizationRevision, string? ContinuationToken);

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
    /// <summary>Grants or revokes the right to create records for an existing form (form_user edges). Never touches record ownership or shares.</summary>
    UpdateFormUsers,
    /// <summary>Declassifies a field to Public/Internal (organization owner or system administrator, governance approval reference required). Amendment A1.</summary>
    ReclassifyField,
}

/// <summary>command_payload is validated against the exact command-specific schema in the authority; unknown fields are rejected.</summary>
public sealed record SecurityCommandRequest(
    Guid OrganizationId,
    SecurityCommandKind CommandKind,
    ResourceRef Resource,
    long ExpectedAuthorizationRevision,
    JsonElement CommandPayload,
    string? ChangeReason = null,
    Guid? ApprovalReference = null);

public enum CommandStatus
{
    Pending,
    CommittedAwaitingProjection,
    Effective,
    Rejected,
    FailedReconciliation,
}

public enum ProjectionGateState
{
    Ready,
    AwaitingProjection,
    AwaitingArchive,
    Quarantined,
}

public sealed record OperationStatus(
    Guid CommandId,
    CommandStatus CommandStatus,
    long AuthorizationRevision,
    long? ProjectionRevision,
    ProjectionGateState ProjectionGateState,
    string? RejectionReasonCode,
    Guid CorrelationId);

// Typed command payloads (serialized into SecurityCommandRequest.CommandPayload).

public sealed record RegisterTeamPayload(Guid TeamId);

public sealed record TeamMemberPayload(Guid TeamId, Guid MemberUserId);

public sealed record RegisterFormDefinitionPayload(Guid FormDefinitionId, IReadOnlyList<Guid> FormUserUserIds, IReadOnlyList<Guid> FormUserTeamIds);

/// <summary>Delta of form_user subjects (create-record permission) for an existing form; independent of any form version.</summary>
public sealed record UpdateFormUsersPayload(Guid FormDefinitionId, IReadOnlyList<Guid> GrantUserIds, IReadOnlyList<Guid> GrantTeamIds, IReadOnlyList<Guid> RevokeUserIds, IReadOnlyList<Guid> RevokeTeamIds);

/// <summary>
/// Records in the authority that a field must be application-encrypted from this protection policy revision on (the
/// designer's audited strengthening). It is the only basis on which the authority accepts workload reprotection
/// operations for that field; it creates no permission and touches no relationship.
/// </summary>
public sealed record PublishFormSecurityRevisionPayload(Guid FormDefinitionId, Guid FieldDefinitionId, string RequiredProtectionMode, long ProtectionPolicyRevision, string? FieldClassification = null);

/// <summary>The organization's data-protection policy as decided by its owner/system administrator; the authority keeps it for the reclassification and downgrade planners.</summary>
public sealed record UpdateMandatoryPolicyPayload(string MinimumFieldClassification, bool PlaintextInternalFieldsPermitted, bool PlaintextDowngradePermitted);

/// <summary>Declassification of an encrypted field to Public or Internal; the approval reference identifies the governance decision and is journaled.</summary>
public sealed record ReclassifyFieldPayload(Guid FormDefinitionId, Guid FieldDefinitionId, string TargetClassification, string ApprovalReference);

/// <summary>One principal in a form's authoritative access state.</summary>
public sealed record FormAccessSubject(string SubjectKind, Guid SubjectId);

/// <summary>The authority's desired-state view of who may design and who may create records for a form, read behind the organization gate.</summary>
public sealed record FormAccessState(Guid FormDefinitionId, long AuthorizationRevision, long ProjectionRevision, IReadOnlyList<FormAccessSubject> Designers, IReadOnlyList<FormAccessSubject> FormUsers);

/// <summary>Top-level record: owner = actor or an eligible team. Child record (B-04): parent + relationship definition set, no owner edge; permissions derive from the parent.</summary>
public sealed record RegisterRecordPayload(Guid FormRecordId, Guid FormDefinitionId, Guid? OwnerTeamId = null, Guid? ParentFormRecordId = null, Guid? FormRelationshipDefinitionId = null);

/// <summary>Removes the parent edge of a child record; the acting user becomes its owner (data preserved, no cascade delete).</summary>
public sealed record UnlinkChildRecordPayload(Guid FormRecordId, Guid ParentFormRecordId);

public sealed record TransferOwnershipPayload(Guid FormRecordId, Guid? NewOwnerUserId, Guid? NewOwnerTeamId);

public sealed record CreateSharePayload(Guid RecordShareId, Guid FormRecordId, Guid? RecipientUserId, Guid? RecipientTeamId, string RecipientRole, IReadOnlyList<Guid> FieldItemIds);

public sealed record RevokeSharePayload(Guid RecordShareId, Guid FormRecordId);

public static class RecipientRoles
{
    public const string Reader = "Reader";
    public const string Contributor = "Contributor";
    public const string Manager = "Manager";
}

public sealed record ErrorResponse(string ErrorCode, Guid CorrelationId, bool Retryable, string? Detail = null);
