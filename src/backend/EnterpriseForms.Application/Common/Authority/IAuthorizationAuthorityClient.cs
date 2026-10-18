using System.Text.Json;

namespace EnterpriseForms.Application.Common.Authority;

// Port for the independent Authorization Authority (trust_authority/trust-authority.openapi.yaml, Stage A revision).
// The only implementation is an authenticated internal HTTP adapter in Infrastructure. It never derives decisions from
// business PostgreSQL owner/share/team rows and exposes no generic signing method. DTO shapes mirror the private
// contract; the authority re-validates identity, tenant and every revision independently.

public enum ProtectedResourceKind
{
    FormDefinition,
    FormRecord,
    FieldItem,
    DocumentItem,
    Team,
    Organization,
}

public enum ProtectedAction
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
}

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
}

public enum SecurityCommandStatus
{
    Pending,
    CommittedAwaitingProjection,
    Effective,
    Rejected,
    FailedReconciliation,
}

public enum ManifestItemKind
{
    RecordHeader = 1,
    RepeatedGroup = 2,
    FieldValue = 3,
    FieldSelection = 4,
    Attachment = 5,
    AttachmentLifecycle = 6,
}

public sealed record ResourceReference(ProtectedResourceKind ResourceKind, Guid ResourceId);

public sealed record PermissionCheckRequest(Guid OrganizationId, ResourceReference Resource, ProtectedAction RequestedAction, Guid? UnlockReference = null, long? MinimumContentRevision = null);

public sealed record PermissionDecision(bool Allowed, string? DenialReasonCode, long AuthorizationRevision, long ProjectionRevision, long? ContentRevision, Guid CorrelationId);

public sealed record SecurityCommandRequest(Guid OrganizationId, SecurityCommandKind CommandKind, ResourceReference Resource, long ExpectedAuthorizationRevision, JsonElement CommandPayload, string? ChangeReason = null, Guid? ApprovalReference = null);

public sealed record SecurityCommandResult(Guid CommandId, SecurityCommandStatus CommandStatus, long AuthorizationRevision, long? ProjectionRevision, string ProjectionGateState, string? RejectionReasonCode, Guid CorrelationId);

public sealed record CandidateContentItem(ManifestItemKind ItemKind, Guid ItemId, string CanonicalBytesBase64);

public sealed record PrepareContentChangeRequest(Guid OrganizationId, Guid FormRecordId, Guid OperationId, long ExpectedAuthorizationRevision, long ExpectedContentRevision, string ExpectedManifestRootBase64, IReadOnlyList<CandidateContentItem> CandidateItems);

public sealed record SignedCandidateItem(ManifestItemKind ItemKind, Guid ItemId, string RowSignatureBase64, string CanonicalPayloadDigestBase64, string MutationKind);

public sealed record PreparedContentOperation(Guid OperationId, long CandidateContentRevision, int SignatureKeyVersion, string CandidateManifestRootBase64, IReadOnlyList<SignedCandidateItem> SignedItems, string OperationStatus, Guid CorrelationId);

public sealed record StagedContentItem(ManifestItemKind ItemKind, Guid ItemId, string CanonicalBytesBase64, string RowSignatureBase64);

public sealed record CommitContentChangeRequest(Guid OrganizationId, Guid FormRecordId, IReadOnlyList<StagedContentItem> StagedItems);

public sealed record AcceptedContentRevision(Guid FormRecordId, long CommittedContentRevision, Guid? CommittedOperationId, string CommittedManifestRootBase64, long AuthorizationRevision, int IntegrityKeyVersion, int CurrentSigningKeyVersion, string ArchiveState, Guid CorrelationId);

public sealed record VerifyRecordProjectionRequest(Guid OrganizationId, Guid FormRecordId, ProtectedAction RequestedAction, IReadOnlyList<StagedContentItem> ObservedItems);

public sealed record VerifiedProjectionItem(ManifestItemKind ItemKind, Guid ItemId);

public sealed record VerifyRecordProjectionResult(bool Verified, string? FailureReasonCode, long CommittedContentRevision, Guid? CommittedOperationId, long AuthorizationRevision, IReadOnlyList<VerifiedProjectionItem> AuthorizedItems, bool IsCompleteProjection, Guid CorrelationId);

public sealed record RecordDiscoveryPage(IReadOnlyList<Guid> FormRecordIds, long AuthorizationRevision, string? ContinuationToken);

/// <summary>Stable authority error surfaced to callers; the business API maps it to a Problem Details response without leaking internals.</summary>
public sealed class AuthorityClientException(string errorCode, int httpStatusCode, bool retryable, string? detail = null) : Exception(detail ?? errorCode)
{
    public string ErrorCode { get; } = errorCode;

    public int HttpStatusCode { get; } = httpStatusCode;

    public bool Retryable { get; } = retryable;
}

/// <summary>The verified end-user context forwarded to the authority: the raw assertion is forwarded unchanged; no claims are re-asserted by the business API.</summary>
public sealed record DelegatedUserContext(string IssuerUri, string SubjectIdentifier, Guid OrganizationId, string RawAssertion);

public interface IAuthorizationAuthorityClient
{
    Task<PermissionDecision> CheckPermissionAsync(DelegatedUserContext user, PermissionCheckRequest request, CancellationToken cancellationToken);

    Task<IReadOnlyList<PermissionDecision>> CheckPermissionsBatchAsync(DelegatedUserContext user, IReadOnlyList<PermissionCheckRequest> requests, CancellationToken cancellationToken);

    Task<RecordDiscoveryPage> DiscoverAuthorizedRecordsAsync(DelegatedUserContext user, Guid organizationId, int pageSize, string? continuationToken, CancellationToken cancellationToken);

    Task<SecurityCommandResult> SubmitSecurityCommandAsync(DelegatedUserContext user, SecurityCommandRequest request, string idempotencyKey, CancellationToken cancellationToken);

    Task<SecurityCommandResult> GetSecurityCommandStatusAsync(DelegatedUserContext user, Guid commandId, CancellationToken cancellationToken);

    Task<PreparedContentOperation> PrepareContentChangeAsync(DelegatedUserContext user, PrepareContentChangeRequest request, string idempotencyKey, CancellationToken cancellationToken);

    Task<AcceptedContentRevision> CommitContentChangeAsync(DelegatedUserContext user, Guid operationId, CommitContentChangeRequest request, CancellationToken cancellationToken);

    Task<AcceptedContentRevision> GetCommittedContentRevisionAsync(DelegatedUserContext user, Guid organizationId, Guid formRecordId, CancellationToken cancellationToken);

    Task<VerifyRecordProjectionResult> VerifyRecordProjectionAsync(DelegatedUserContext user, VerifyRecordProjectionRequest request, CancellationToken cancellationToken);
}
