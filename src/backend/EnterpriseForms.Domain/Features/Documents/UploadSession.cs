using EnterpriseForms.Domain.Features.FormRecords;

namespace EnterpriseForms.Domain.Features.Documents;

public static class UploadSessionStates
{
    public const string Open = "Open";
    public const string Completed = "Completed";
    public const string Processing = "Processing";
    public const string Processed = "Processed";
    public const string Committed = "Committed";
    public const string Rejected = "Rejected";
    public const string Failed = "Failed";
    public const string Cancelled = "Cancelled";
    public const string Expired = "Expired";
}

/// <summary>
/// Authorized resumable upload session (CLAUDE.md §4A, CLAUDE-DESIGN §6.2). Chunks are staged as block-blob blocks in a
/// private quarantine container; nothing in this row confers permission — finalize and commit re-check the authority.
/// </summary>
public sealed class UploadSession
{
    public Guid UploadSessionId { get; set; }

    public Guid TenantId { get; set; }

    public Guid FormRecordId { get; set; }

    public Guid FieldDefinitionId { get; set; }

    /// <summary>Existing attachment when the session replaces a document (new version); null for a new attachment.</summary>
    public Guid? ReplacesFileAttachmentId { get; set; }

    public Guid OwnerUserId { get; set; }

    public string SessionIdempotencyKey { get; set; } = string.Empty;

    public string OriginalFileName { get; set; } = string.Empty;

    public string DeclaredContentType { get; set; } = string.Empty;

    public long DeclaredSizeBytes { get; set; }

    public int ChunkSizeBytes { get; set; }

    public int ExpectedChunkCount { get; set; }

    /// <summary>Server-side bitmap of acknowledged chunks (one byte per chunk, 1 = received); the client view is a hint only.</summary>
    public byte[] ReceivedChunkBitmap { get; set; } = [];

    public long ReceivedBytes { get; set; }

    public string StagingObjectKey { get; set; } = string.Empty;

    public string SessionState { get; set; } = UploadSessionStates.Open;

    public string? FailureReasonCode { get; set; }

    public DateTimeOffset SessionCreatedOn { get; set; }

    public DateTimeOffset SessionExpiresOn { get; set; }

    public DateTimeOffset? SessionCompletedOn { get; set; }

    public DateTimeOffset SessionModifiedOn { get; set; }

    /// <summary>Set when processing finished: the version row that the user's commit will bind into the manifest.</summary>
    public Guid? ProducedFileAttachmentVersionId { get; set; }
}

public static class AttachmentLifecycleStates
{
    public const string Ready = "Ready";
    public const string Removed = "Removed";
}

/// <summary>Attachment identity (CLAUDE.md §4A): metadata and object keys only, never bytes. Current version is the latest committed version.</summary>
public sealed class FileAttachment
{
    public Guid FileAttachmentId { get; set; }

    public Guid TenantId { get; set; }

    public Guid FormRecordId { get; set; }

    public Guid FieldDefinitionId { get; set; }

    public string OriginalFileName { get; set; } = string.Empty;

    public int CurrentVersionNumber { get; set; }

    public string AttachmentLifecycleState { get; set; } = AttachmentLifecycleStates.Ready;

    public DateTimeOffset AttachmentCreatedOn { get; set; }

    public Guid AttachmentCreatedByUserId { get; set; }

    public DateTimeOffset AttachmentModifiedOn { get; set; }

    public Guid AttachmentModifiedByUserId { get; set; }
}

public static class ScanVerdicts
{
    public const string Clean = "Clean";
    public const string Infected = "Infected";
    public const string ScanError = "ScanError";
}

/// <summary>
/// Immutable document version: ciphertext object reference, verified plaintext/ciphertext digests, detected content
/// type, scanner verdict and the AES-GCM envelope metadata. Signed under RowHmac/v1 entity kind
/// <c>file_attachment_versions</c> (Merkle kind Attachment, MERKLE §2.1/§4.2). The item id is the attachment id so a
/// replacement version supersedes the leaf of the same attachment; the primary key is the version row id.
/// </summary>
public sealed class FileAttachmentVersion : ISignedContentRow
{
    public Guid FileAttachmentVersionId { get; set; }

    public Guid TenantId { get; set; }

    public Guid FormRecordId { get; set; }

    public Guid FileAttachmentId { get; set; }

    public Guid FieldDefinitionId { get; set; }

    public int VersionNumber { get; set; }

    public string OriginalFileName { get; set; } = string.Empty;

    public string DetectedContentType { get; set; } = string.Empty;

    public long PlaintextSizeBytes { get; set; }

    public byte[] PlaintextSha256 { get; set; } = [];

    public long CiphertextSizeBytes { get; set; }

    public byte[] CiphertextSha256 { get; set; } = [];

    public string StorageProviderName { get; set; } = string.Empty;

    public string StorageObjectKey { get; set; } = string.Empty;

    public string EncryptionAlgorithmName { get; set; } = "AES-256-GCM";

    public string EncryptionKeyReference { get; set; } = string.Empty;

    public int EncryptionEnvelopeVersion { get; set; } = 1;

    public byte[] WrappedDataKey { get; set; } = [];

    public byte[] EncryptionNoncePrefix { get; set; } = [];

    public int EncryptionSegmentSizeBytes { get; set; }

    public string ScanVerdict { get; set; } = ScanVerdicts.ScanError;

    public string ScannerName { get; set; } = string.Empty;

    public string? ScanSignatureName { get; set; }

    public DateTimeOffset ScannedOn { get; set; }

    public Guid UploadedByUserId { get; set; }

    public DateTimeOffset UploadedOn { get; set; }

    public Guid ContentChangeOperationId { get; set; }

    public long ContentRevision { get; set; }

    public long RowRevision { get; set; }

    public byte[] RowSignature { get; set; } = [];

    public int RowSignatureKeyVersion { get; set; }

    public int RowSignatureSchemaVersion { get; set; }

    /// <summary>Processed (encrypted, not yet in any manifest) | Committed | Superseded | Removed — local hint only.</summary>
    public string VersionState { get; set; } = "Processed";

    public string EntityKind => "file_attachment_versions";

    public byte ItemKindCode => ContentItemKindCodes.Attachment;

    public Guid ItemId => FileAttachmentId;

    public Guid RowPrimaryKey => FileAttachmentVersionId;
}

public static class ProcessingJobStates
{
    public const string Queued = "Queued";
    public const string Leased = "Leased";
    public const string ScanPending = "ScanPending";
    public const string Processed = "Processed";
    public const string Rejected = "Rejected";
    public const string Failed = "Failed";
}

/// <summary>Durable leased job for scan → encrypt → store of a completed upload (CLAUDE-DESIGN §5.5). Not a permission source.</summary>
public sealed class DocumentProcessingJob
{
    public Guid DocumentProcessingJobId { get; set; }

    public Guid TenantId { get; set; }

    public Guid UploadSessionId { get; set; }

    public string JobState { get; set; } = ProcessingJobStates.Queued;

    public int AttemptCount { get; set; }

    public string? LeaseOwnerName { get; set; }

    public DateTimeOffset? LeaseExpiresOn { get; set; }

    public DateTimeOffset NextAttemptOn { get; set; }

    public string? LastErrorCode { get; set; }

    public DateTimeOffset JobCreatedOn { get; set; }

    public DateTimeOffset JobModifiedOn { get; set; }
}
