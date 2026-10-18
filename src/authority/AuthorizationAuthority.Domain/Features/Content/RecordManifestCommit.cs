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
