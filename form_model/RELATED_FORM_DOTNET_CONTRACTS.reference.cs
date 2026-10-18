// Reference contracts — namespace/project placement adapts to existing .NET 10 repository.
// This is not a working implementation; the trusted authority retains all final access decisions.
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace DynamicForms.RelatedForms;

public sealed record RelatedFormDefinitionRequest(
    Guid ParentFormDefinitionId,
    Guid ChildFormDefinitionId,
    string RelationshipDefinitionKey,
    string RelationshipDisplayName);

public sealed record RelatedFormListSettings(
    int MinimumItems,
    int MaximumItems,
    bool AllowCreate,
    bool AllowLinkExisting,
    bool AllowReorder,
    IReadOnlyList<Guid> SummaryFieldDefinitionIds);

public sealed record CreateRelatedChildRequest(
    Guid ParentFormRecordId,
    Guid FormRelationshipDefinitionId,
    Guid ChildFormVersionId,
    IReadOnlyDictionary<Guid, object?> InitialFieldValues,
    long ExpectedParentContentRevision,
    long ExpectedAuthorityRevision,
    Guid IdempotencyKey);

public sealed record LinkExistingChildRequest(
    Guid ParentFormRecordId,
    Guid FormRelationshipDefinitionId,
    Guid ChildFormRecordId,
    long ExpectedParentContentRevision,
    long ExpectedAuthorityRevision,
    Guid IdempotencyKey);

public sealed record ReorderRelatedChildRequest(
    Guid FormRecordRelationshipId,
    int ChildRecordSortOrder,
    long ExpectedRelationshipRevision,
    long ExpectedAuthorityRevision,
    Guid IdempotencyKey);

public sealed record UnlinkRelatedChildRequest(
    Guid FormRecordRelationshipId,
    string RelationshipRemovalReason,
    long ExpectedRelationshipRevision,
    long ExpectedAuthorityRevision,
    Guid IdempotencyKey);

public sealed record AuthorizedRelatedRecordSummary(
    Guid FormRecordRelationshipId,
    Guid ChildFormRecordId,
    int ChildRecordSortOrder,
    IReadOnlyDictionary<Guid, object?> AuthorizedSummaryFieldValues,
    bool CanEditChild,
    bool CanUnlinkChild);

public sealed record RelatedRecordPage(
    IReadOnlyList<AuthorizedRelatedRecordSummary> Records,
    string? NextCursor);

public sealed record RelatedFormCommandResult(
    Guid SecurityOperationId,
    string CommandStatus, // Pending/Committed/Rejected; never report Active before authority barrier
    Guid? FormRecordRelationshipId,
    Guid? ChildFormRecordId,
    long? CommittedAuthorityRevision);

public interface IRelatedFormCollectionService
{
    Task<RelatedFormCommandResult> CreateChildAsync(
        CreateRelatedChildRequest request, CancellationToken cancellationToken);
    Task<RelatedFormCommandResult> LinkExistingAsync(
        LinkExistingChildRequest request, CancellationToken cancellationToken);
    Task<RelatedFormCommandResult> ReorderAsync(
        ReorderRelatedChildRequest request, CancellationToken cancellationToken);
    Task<RelatedFormCommandResult> UnlinkAsync(
        UnlinkRelatedChildRequest request, CancellationToken cancellationToken);
}

public interface IRelatedFormProjectionService
{
    // Permission must be checked on relationship membership, child discoverability,
    // and each projected field. No unapproved fields, IDs or counts may be returned.
    Task<RelatedRecordPage> GetAuthorizedChildrenAsync(
        Guid parentFormRecordId,
        Guid formRelationshipDefinitionId,
        int pageSize,
        string? continuationToken,
        CancellationToken cancellationToken);
}
