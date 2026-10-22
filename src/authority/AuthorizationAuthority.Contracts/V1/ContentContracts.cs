namespace AuthorizationAuthority.Contracts.V1;

/// <summary>Merkle v1 item kinds (MERKLE_RECORD_MANIFEST_PROTOCOL.md §2.1). Wire values equal the kind codes.</summary>
public enum ManifestItemKind
{
    RecordHeader = 1,
    RepeatedGroup = 2,
    FieldValue = 3,
    FieldSelection = 4,
    Attachment = 5,
    AttachmentLifecycle = 6,
}

/// <summary>
/// One candidate item of the complete proposed snapshot. The canonical bytes are DynamicForms/RowHmac/v1 framed
/// bytes produced by the trusted business runtime from the staged values; the authority binds them to the approved
/// organization/key version/entity kind before signing (no arbitrary sign-any-bytes endpoint, invariant A9).
/// </summary>
public sealed record CandidateContentItem(ManifestItemKind ItemKind, Guid ItemId, string CanonicalBytesBase64);

/// <summary>
/// Full-snapshot prepare (v1 policy: every leaf is re-signed under the candidate operation). The caller proposes the
/// operation id (derived from its idempotency key) because the canonical bytes of every candidate item bind that id;
/// the authority enforces uniqueness and records it as the only acceptable committed operation id for this candidate.
/// </summary>
public sealed record PrepareContentChangeRequest(
    Guid OrganizationId,
    Guid FormRecordId,
    Guid OperationId,
    long ExpectedAuthorizationRevision,
    long ExpectedContentRevision,
    string ExpectedManifestRootBase64,
    IReadOnlyList<CandidateContentItem> CandidateItems,
    Guid? ReprotectionCommandId = null,
    IReadOnlyList<StagedContentItem>? CurrentItems = null);

/// <summary>
/// Reprotection operation (workload-only, no delegated user): <c>reprotection_command_id</c> names an effective
/// PublishFormSecurityRevision command and <c>current_items</c> the complete accepted snapshot the candidate was built
/// from. The authority verifies the current snapshot against the head and accepts the candidate only if it replaces
/// plaintext rows of that command's field with encrypted rows of the same item identity and changes nothing else.
/// </summary>
public static class ReprotectionRequestNotes
{
}

public sealed record SignedCandidateItem(ManifestItemKind ItemKind, Guid ItemId, string RowSignatureBase64, string CanonicalPayloadDigestBase64, string MutationKind);

public sealed record PreparedContentOperation(
    Guid OperationId,
    long CandidateContentRevision,
    int SignatureKeyVersion,
    string CandidateManifestRootBase64,
    IReadOnlyList<SignedCandidateItem> SignedItems,
    string OperationStatus,
    Guid CorrelationId);

/// <summary>An item as read back from the (untrusted) business store after durable staging.</summary>
public sealed record StagedContentItem(ManifestItemKind ItemKind, Guid ItemId, string CanonicalBytesBase64, string RowSignatureBase64);

public sealed record CommitContentChangeRequest(Guid OrganizationId, Guid FormRecordId, IReadOnlyList<StagedContentItem> StagedItems);

public sealed record ContentRevision(
    Guid FormRecordId,
    long CommittedContentRevision,
    Guid? CommittedOperationId,
    string CommittedManifestRootBase64,
    long AuthorizationRevision,
    int IntegrityKeyVersion,
    int CurrentSigningKeyVersion,
    string ArchiveState,
    Guid CorrelationId);

/// <summary>
/// historic_committed_operation_id (optional): verify the observed items against the complete manifest the authority accepted
/// for that earlier operation of the same record instead of the current head (historic document versions). Requires full
/// read permission now, an open gate and an unquarantined head; never authorizes anything the current head would not.
/// </summary>
public sealed record VerifyRecordProjectionRequest(Guid OrganizationId, Guid FormRecordId, RequestedAction RequestedAction, IReadOnlyList<StagedContentItem> ObservedItems, Guid? HistoricCommittedOperationId = null);

public sealed record VerifiedProjectionItem(ManifestItemKind ItemKind, Guid ItemId);

/// <summary>
/// verified=true only when the observed set equals the authority's complete accepted leaf index for the current head,
/// every row signature verifies under the recorded key version, the recomputed root equals the accepted root, the
/// organization gate is open and the content checkpoint is archived. authorized_items is the subset the caller may release.
/// Phase C5 (D7, additive): for a partial projection readable_items and editable_items are both returned (advisory only;
/// prepare and commit decide) and contributor_writes_enabled reports the Authority's deployment switch (D10).
/// </summary>
public sealed record VerifyRecordProjectionResult(
    bool Verified,
    string? FailureReasonCode,
    long CommittedContentRevision,
    Guid? CommittedOperationId,
    long AuthorizationRevision,
    IReadOnlyList<VerifiedProjectionItem> AuthorizedItems,
    bool IsCompleteProjection,
    Guid CorrelationId,
    IReadOnlyList<VerifiedProjectionItem>? ReadableItems = null,
    IReadOnlyList<VerifiedProjectionItem>? EditableItems = null,
    bool ContributorWritesEnabled = false);
