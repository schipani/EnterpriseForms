// Reference contracts, .NET 10. NOT a complete, security-reviewed implementation.
// These live in the business application integration library and are implemented
// by an authenticated internal HTTP/gRPC adapter. They never implement authorization
// by reading business PostgreSQL owner/share/team tables.
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace DynamicForms.Security.Contracts;

public enum ProtectedResourceKind
{
    FormDefinition,
    FormRecord,
    FieldItem,
    DocumentItem,
    Team,
    Organization
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
    ManageOrganization
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
    PublishFormSecurityRevision
}

public enum SecurityCommandStatus
{
    Pending,
    CommittedAwaitingProjection,
    Effective,
    Rejected,
    FailedReconciliation
}

public sealed record ResourceReference(ProtectedResourceKind ResourceKind, Guid ResourceId);

// Identity and tenant are validated independently by the AUTHORITY from
// workload and delegated user tokens. These DTOs are not trusted claims.
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

public enum ContentMutationKind { Add, Update, Remove }
public sealed record ChangedItem(
    ResourceReference Resource,
    ContentMutationKind MutationKind,
    string CanonicalDigest);

public sealed record PrepareContentChangeRequest(
    Guid OrganizationId,
    Guid FormRecordId,
    long ExpectedAuthorizationRevision,
    long ExpectedContentRevision,
    string ExpectedManifestRoot,
    IReadOnlyList<ChangedItem> ChangedItems);

public sealed record PreparedContentOperation(
    Guid OperationId,
    long CandidateContentRevision);

public sealed record CommitContentChangeRequest(
    Guid OrganizationId,
    Guid OperationId,
    string CandidateManifestRoot,
    string PreparedContentDigest);

public sealed record AcceptedContentRevision(
    Guid FormRecordId,
    long CommittedContentRevision,
    string CommittedManifestRoot,
    long AuthorizationRevision,
    int IntegrityKeyVersion);

public sealed record SignedItem(
    ResourceReference Resource,
    string CanonicalPayloadDigest,
    string RowSignature,
    int RowSignatureKeyVersion,
    long RowRevision);

public sealed record VerifyCommittedItemsRequest(
    Guid OrganizationId,
    Guid FormRecordId,
    long CommittedContentRevision,
    IReadOnlyList<SignedItem> SignedItems,
    string? MembershipProof = null);

public sealed record VerifyCommittedItemsResult(
    bool Verified,
    long CommittedContentRevision,
    Guid CorrelationId,
    int FailedItemCount);

public sealed record RecordDiscoveryPage(
    IReadOnlyList<Guid> FormRecordIds,
    long AuthorizationRevision,
    string? ContinuationToken);

public interface IAuthorizationAuthorityClient
{
    Task<PermissionDecision> CheckPermissionAsync(
        PermissionCheckRequest request, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<PermissionDecision>> CheckPermissionsBatchAsync(
        IReadOnlyList<PermissionCheckRequest> requests,
        CancellationToken cancellationToken = default);

    Task<RecordDiscoveryPage> DiscoverAuthorizedRecordsAsync(
        Guid organizationId, int pageSize, string? continuationToken,
        CancellationToken cancellationToken = default);

    Task<SecurityCommandResult> SubmitSecurityCommandAsync(
        SecurityCommandRequest request, string idempotencyKey,
        CancellationToken cancellationToken = default);

    Task<SecurityCommandResult> GetSecurityCommandStatusAsync(
        Guid commandId, CancellationToken cancellationToken = default);

    Task<PreparedContentOperation> PrepareContentChangeAsync(
        PrepareContentChangeRequest request, string idempotencyKey,
        CancellationToken cancellationToken = default);

    Task<AcceptedContentRevision> CommitContentChangeAsync(
        CommitContentChangeRequest request, string idempotencyKey,
        CancellationToken cancellationToken = default);

    Task<AcceptedContentRevision> GetCommittedContentRevisionAsync(
        Guid formRecordId, CancellationToken cancellationToken = default);

    Task<VerifyCommittedItemsResult> VerifyCommittedItemsAsync(
        VerifyCommittedItemsRequest request, CancellationToken cancellationToken = default);
}

// The actual adapter must attach a verified user OBO token and workload
// credential securely, enforce TLS identity, instrument correlation/audit,
// use bounded timeouts and retries only for idempotent operations, and reject
// stale/unknown conditional authz responses. Never cache Allowed=true without
// authority-controlled invalidation. No SignAnyPayload or RawSpiceDbWrite methods.
