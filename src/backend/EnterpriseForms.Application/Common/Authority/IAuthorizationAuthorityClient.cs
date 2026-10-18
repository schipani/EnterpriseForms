namespace EnterpriseForms.Application.Common.Authority;

// Port for the independent Authorization Authority, adapted from trust_authority/TRUST_AUTHORITY_DOTNET_CONTRACTS.cs.
// The only implementation lives in Infrastructure as an authenticated internal HTTP adapter (Stage A).
// It never derives decisions from business PostgreSQL owner/share/team rows and exposes no generic signing method.

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
    TransferOwnership,
    CreateShare,
    UpdateShareScope,
    RevokeShare,
    AddTeamMember,
    RemoveTeamMember,
    GrantSystemRole,
    RevokeSystemRole,
    UpdateMandatoryPolicy,
    PublishFormSecurityRevision,
}

public enum SecurityCommandStatus
{
    Pending,
    CommittedAwaitingProjection,
    Effective,
    Rejected,
    FailedReconciliation,
}

public enum ContentMutationKind
{
    Add,
    Update,
    Remove,
}

public sealed record ResourceReference(ProtectedResourceKind ResourceKind, Guid ResourceId);

/// <summary>Identity and tenant are validated by the authority from workload and delegated user evidence; these fields are not trusted claims.</summary>
public sealed record PermissionCheckRequest(
    Guid OrganizationId,
    ResourceReference Resource,
    ProtectedAction RequestedAction,
    Guid? UnlockReference = null,
    long? MinimumContentRevision = null);

public sealed record PermissionDecision(
    bool Allowed,
    long AuthorizationRevision,
    long ProjectionRevision,
    long? ContentRevision,
    Guid CorrelationId,
    string? DenialReasonCode = null);

public sealed record SecurityCommandRequest(
    Guid OrganizationId,
    SecurityCommandKind CommandKind,
    ResourceReference Resource,
    long ExpectedAuthorizationRevision,
    string ValidatedCommandPayloadJson,
    string? ChangeReason = null,
    Guid? ApprovalReference = null);

public sealed record SecurityCommandResult(
    Guid CommandId,
    SecurityCommandStatus CommandStatus,
    long AuthorizationRevision,
    long? ProjectionRevision,
    Guid CorrelationId);

public sealed record ChangedItem(ResourceReference Resource, ContentMutationKind MutationKind, string CanonicalDigest);

public sealed record PrepareContentChangeRequest(
    Guid OrganizationId,
    Guid FormRecordId,
    long ExpectedAuthorizationRevision,
    long ExpectedContentRevision,
    string ExpectedManifestRoot,
    IReadOnlyList<ChangedItem> ChangedItems);

public sealed record PreparedContentOperation(Guid OperationId, long CandidateContentRevision);

public sealed record CommitContentChangeRequest(Guid OrganizationId, Guid OperationId, string CandidateManifestRoot, string PreparedContentDigest);

public sealed record AcceptedContentRevision(
    Guid FormRecordId,
    long CommittedContentRevision,
    string CommittedManifestRoot,
    long AuthorizationRevision,
    int IntegrityKeyVersion);

public sealed record SignedItem(ResourceReference Resource, string CanonicalPayloadDigest, string RowSignature, int RowSignatureKeyVersion, long RowRevision);

public sealed record VerifyCommittedItemsRequest(
    Guid OrganizationId,
    Guid FormRecordId,
    long CommittedContentRevision,
    IReadOnlyList<SignedItem> SignedItems,
    string? MembershipProof = null);

public sealed record VerifyCommittedItemsResult(bool Verified, long CommittedContentRevision, Guid CorrelationId, int FailedItemCount);

public sealed record RecordDiscoveryPage(IReadOnlyList<Guid> FormRecordIds, long AuthorizationRevision, string? ContinuationToken);

public interface IAuthorizationAuthorityClient
{
    Task<PermissionDecision> CheckPermissionAsync(PermissionCheckRequest request, CancellationToken cancellationToken);

    Task<IReadOnlyList<PermissionDecision>> CheckPermissionsBatchAsync(IReadOnlyList<PermissionCheckRequest> requests, CancellationToken cancellationToken);

    Task<RecordDiscoveryPage> DiscoverAuthorizedRecordsAsync(Guid organizationId, int pageSize, string? continuationToken, CancellationToken cancellationToken);

    Task<SecurityCommandResult> SubmitSecurityCommandAsync(SecurityCommandRequest request, string idempotencyKey, CancellationToken cancellationToken);

    Task<SecurityCommandResult> GetSecurityCommandStatusAsync(Guid commandId, CancellationToken cancellationToken);

    Task<PreparedContentOperation> PrepareContentChangeAsync(PrepareContentChangeRequest request, string idempotencyKey, CancellationToken cancellationToken);

    Task<AcceptedContentRevision> CommitContentChangeAsync(CommitContentChangeRequest request, string idempotencyKey, CancellationToken cancellationToken);

    Task<AcceptedContentRevision> GetCommittedContentRevisionAsync(Guid formRecordId, CancellationToken cancellationToken);

    Task<VerifyCommittedItemsResult> VerifyCommittedItemsAsync(VerifyCommittedItemsRequest request, CancellationToken cancellationToken);
}
