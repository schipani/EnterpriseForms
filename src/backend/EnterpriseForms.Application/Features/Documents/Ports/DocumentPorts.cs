namespace EnterpriseForms.Application.Features.Documents.Ports;

public enum StorageArea
{
    /// <summary>Quarantine for raw uploaded bytes (never served, never decrypted-from).</summary>
    Staging,

    /// <summary>Ciphertext-only committed documents.</summary>
    Documents,
}

/// <summary>Provider-neutral object storage (CLAUDE.md §4A). No SDK types; keys are server-generated and opaque.</summary>
public interface IFileStorageProvider
{
    string ProviderName { get; }

    Task StageChunkAsync(string objectKey, int chunkIndex, Stream content, long length, CancellationToken cancellationToken);

    /// <summary>Assembles the staged chunks 0..chunkCount-1 into one readable object (block list commit).</summary>
    Task CommitStagedChunksAsync(string objectKey, int chunkCount, CancellationToken cancellationToken);

    Task<Stream> OpenReadAsync(StorageArea area, string objectKey, CancellationToken cancellationToken);

    Task UploadAsync(StorageArea area, string objectKey, Stream content, string contentType, CancellationToken cancellationToken);

    Task DeleteAsync(StorageArea area, string objectKey, CancellationToken cancellationToken);

    Task<(bool Ready, string Detail)> ProbeAsync(CancellationToken cancellationToken);
}

public sealed record ScanResult(string Verdict, string ScannerName, string? SignatureName, string? Detail);

/// <summary>Malware scanner port. An unavailable scanner is a fail-closed condition: the job stays ScanPending, the document is never released.</summary>
public interface IFileMalwareScanner
{
    string ScannerName { get; }

    Task<ScanResult> ScanAsync(Stream content, long length, CancellationToken cancellationToken);

    Task<(bool Ready, string Detail)> ProbeAsync(CancellationToken cancellationToken);
}

public sealed class ScannerUnavailableException(string detail, Exception? inner = null) : Exception(detail, inner);

/// <summary>AAD binding of a document ciphertext: tenant, record, attachment identity and version (CLAUDE.md §4C.3).</summary>
public sealed record FileEncryptionContext(Guid TenantId, Guid FormRecordId, Guid FileAttachmentId, int VersionNumber);

public sealed record FileEncryptionEnvelope(string AlgorithmName, int EnvelopeVersion, string KeyReference, byte[] WrappedDataKey, byte[] NoncePrefix, int SegmentSizeBytes);

public sealed record EncryptionOutcome(FileEncryptionEnvelope Envelope, long PlaintextSizeBytes, byte[] PlaintextSha256, long CiphertextSizeBytes, byte[] CiphertextSha256);

/// <summary>Authenticated streaming encryption (segmented AES-256-GCM). Keys never leave the provider; nonces are unique per data key.</summary>
public interface IFileEncryptionService
{
    Task<EncryptionOutcome> EncryptAsync(Stream plaintext, Stream ciphertextSink, FileEncryptionContext context, CancellationToken cancellationToken);

    Task DecryptAsync(Stream ciphertext, Stream plaintextSink, FileEncryptionEnvelope envelope, FileEncryptionContext context, CancellationToken cancellationToken);
}

/// <summary>Key-encryption-key provider. Production: Azure Key Vault/Managed HSM (not implemented in Stage B); Development/Testing: local KEK from configuration, refused elsewhere.</summary>
public interface IEncryptionKeyProvider
{
    string ProviderName { get; }

    bool IsConfigured { get; }

    string CurrentKeyReference { get; }

    byte[] WrapDataKey(ReadOnlySpan<byte> dataKey, string keyReference);

    byte[] UnwrapDataKey(ReadOnlySpan<byte> wrappedDataKey, string keyReference);
}

public sealed class EncryptionUnavailableException(string detail) : Exception(detail);
