namespace AuthorizationAuthority.Domain.Features.Content;

/// <summary>Immutable accepted manifest snapshot: root, count and the complete leaf identity-and-digest index (protocol §1, §3).</summary>
public sealed class RecordManifestCommit
{
    public Guid RecordManifestCommitId { get; set; }

    public Guid OrganizationId { get; set; }

    public Guid FormRecordId { get; set; }

    public Guid CommittedOperationId { get; set; }

    public long CommittedContentRevision { get; set; }

    public int ManifestSchemaVersion { get; set; } = 1;

    public int ManifestAlgorithmVersion { get; set; } = 1;

    public int RowSignatureKeyVersion { get; set; }

    public long ManifestLeafCount { get; set; }

    public byte[] ManifestRootDigest { get; set; } = [];

    public byte[] ManifestLeafIndexDigest { get; set; } = [];

    public DateTimeOffset ManifestCommittedOn { get; set; }
}

public sealed class RecordManifestLeaf
{
    public Guid RecordManifestLeafId { get; set; }

    public Guid RecordManifestCommitId { get; set; }

    public long ManifestLeafPosition { get; set; }

    public byte[] ManifestItemKey { get; set; } = [];

    public byte ManifestItemKindCode { get; set; }

    public Guid ManifestItemId { get; set; }

    public byte[] CanonicalPayloadDigest { get; set; } = [];

    public byte[] RowSignature { get; set; } = [];

    public byte[] ManifestLeafDigest { get; set; } = [];

    /// <summary>
    /// C10 WP8 (Option A): for an attachment leaf, the version number read from the attachment row the Authority verified for this
    /// accepted commit (its signed <c>version_number</c> column); null for other leaves and for leaves accepted before C10 WP8.
    /// Not part of the leaf digest, the row signature or the Merkle root.
    /// </summary>
    public int? AttachmentVersionNumber { get; set; }
}

/// <summary>Per-organization hash-chained content checkpoint (protocol §2.2 step 6); archived through the journal.</summary>
public sealed class ContentIntegrityCheckpoint
{
    public Guid ContentIntegrityCheckpointId { get; set; }

    public Guid OrganizationId { get; set; }

    public long ContentIntegrityRevision { get; set; }

    public Guid FormRecordId { get; set; }

    public long CommittedContentRevision { get; set; }

    public Guid CommittedOperationId { get; set; }

    public byte[] ManifestRootDigest { get; set; } = [];

    public byte[]? PreviousCheckpointDigest { get; set; }

    public byte[] CheckpointEventDigest { get; set; } = [];

    public DateTimeOffset CheckpointCreatedOn { get; set; }
}

/// <summary>
/// R-53 (C-148): the permanent identity of one content item id, recorded when the item is first accepted. An item id is bound
/// for its whole life to one organization, record, item kind and identity anchor (field and group occurrence for values,
/// field for attachments, group definition for group entries, form for the record header). It is never updated or deleted:
/// a removed item's id stays bound, so it can never be reintroduced (which could revive shares that name it) or moved.
/// Unique across organizations, because SpiceDB item objects are global.
/// </summary>
public sealed class ContentItemIdentity
{
    public Guid ContentItemIdentityId { get; set; }

    public Guid ManifestItemId { get; set; }

    public Guid OrganizationId { get; set; }

    public Guid FormRecordId { get; set; }

    public byte ManifestItemKindCode { get; set; }

    public Guid? BoundFieldDefinitionId { get; set; }

    public Guid? BoundFormRecordGroupId { get; set; }

    public Guid? BoundGroupDefinitionId { get; set; }

    public Guid? BoundFormDefinitionId { get; set; }

    public Guid FirstCommittedOperationId { get; set; }

    public DateTimeOffset IdentityBoundOn { get; set; }
}
