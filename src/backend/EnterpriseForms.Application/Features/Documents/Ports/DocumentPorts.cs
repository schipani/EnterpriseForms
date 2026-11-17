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

/// <summary>
/// E3-M2c: the stored envelope of one Authority-verified document version, with its signed schema version and the three Envelope
/// v2 columns (NULL for every envelope-1 row). The reader dispatches on (signed schema, envelope version) under rule S-8.
/// </summary>
public sealed record VerifiedDocumentEnvelope(
    int SignedSchemaVersion,
    string AlgorithmName,
    int EnvelopeVersion,
    string KeyReference,
    byte[] WrappedDataKey,
    byte[] NoncePrefix,
    int SegmentSizeBytes,
    Guid? DataKeyIdentifier,
    byte[]? DataKeyCommitmentBytes,
    int? KeyWrapAlgorithmCode)
{
    /// <summary>The envelope columns of a verified attachment-version row, exactly as stored.</summary>
    public static VerifiedDocumentEnvelope Of(Domain.Features.Documents.FileAttachmentVersion version)
    {
        ArgumentNullException.ThrowIfNull(version);
        return new(version.RowSignatureSchemaVersion, version.EncryptionAlgorithmName, version.EncryptionEnvelopeVersion, version.EncryptionKeyReference, version.WrappedDataKey,
            version.EncryptionNoncePrefix, version.EncryptionSegmentSizeBytes, version.DataKeyIdentifier, version.DataKeyCommitmentBytes, version.KeyWrapAlgorithmCode);
    }
}

/// <summary>Authenticated streaming encryption (segmented AES-256-GCM). Keys never leave the provider; nonces are unique per data key.</summary>
public interface IFileEncryptionService
{
    Task<EncryptionOutcome> EncryptAsync(Stream plaintext, Stream ciphertextSink, FileEncryptionContext context, CancellationToken cancellationToken);

    /// <summary>The Envelope v1 decryptor (envelope version 1 only). Product reads of stored documents use <see cref="DecryptVerifiedAsync"/>; this overload has no product caller of its own (DecryptionCallSiteTests).</summary>
    Task DecryptAsync(Stream ciphertext, Stream plaintextSink, FileEncryptionEnvelope envelope, FileEncryptionContext context, CancellationToken cancellationToken);

    /// <summary>
    /// E3-M2c: decrypts an Authority-verified document version. Strict S-8 dispatch: signed schema 1 with envelope 1 (no v2 column
    /// set) uses the v1 decryptor; schema 2 with envelope 2 (every v2 column set) uses the Envelope v2 reader (key commitment checked
    /// before any segment); schema 0 and every other combination is refused before the key provider is asked, with no fallback.
    /// Streaming caller contract as v1 (OD-E3-15): only normal completion signals success; on any exception the sink must be discarded.
    /// </summary>
    Task DecryptVerifiedAsync(Stream ciphertext, Stream plaintextSink, VerifiedDocumentEnvelope envelope, FileEncryptionContext context, CancellationToken cancellationToken);
}

/// <summary>
/// E1-3: the cryptographic purpose of a key-encryption key. A closed set: instances exist only as the static members below
/// (no public constructor, no parsing from caller input), so a key can never be requested for an arbitrary string. Keys of
/// different purposes are distinct keys and are not interchangeable.
/// </summary>
public sealed class EncryptionKeyPurpose
{
    /// <summary>Application-encrypted typed field values.</summary>
    public static readonly EncryptionKeyPurpose FieldValue = new("FieldValue", "field-value");

    /// <summary>Segmented document (attachment version) encryption.</summary>
    public static readonly EncryptionKeyPurpose Document = new("Document", "document");

    /// <summary>Security Auditor reason notes (seal only).</summary>
    public static readonly EncryptionKeyPurpose AuditorReasonNote = new("AuditorReasonNote", "auditor-reason-note");

    /// <summary>The sealed HMAC key of a record-creation idempotency fingerprint.</summary>
    public static readonly EncryptionKeyPurpose IdempotencyProtection = new("IdempotencyProtection", "idempotency-protection");

    private EncryptionKeyPurpose(string name, string token)
    {
        Name = name;
        Token = token;
    }

    public static IReadOnlyList<EncryptionKeyPurpose> All { get; } = [FieldValue, Document, AuditorReasonNote, IdempotencyProtection];

    /// <summary>Configuration and diagnostic name (for example "FieldValue").</summary>
    public string Name { get; }

    /// <summary>Stable token used inside key references and key-derivation contexts (for example "field-value").</summary>
    public string Token { get; }

    public override string ToString() => Name;
}

/// <summary>E1-2/E1-3: whose key, for what. Every wrap and unwrap names its organization and purpose; an empty organization is refused.</summary>
public sealed record DataKeyScope
{
    public DataKeyScope(Guid organizationId, EncryptionKeyPurpose purpose)
    {
        if (organizationId == Guid.Empty)
        {
            throw new ArgumentException("an organization is required for every key operation", nameof(organizationId));
        }

        OrganizationId = organizationId;
        Purpose = purpose ?? throw new ArgumentNullException(nameof(purpose));
    }

    public Guid OrganizationId { get; }

    public EncryptionKeyPurpose Purpose { get; }
}

/// <summary>A data key wrapped under the current key version of a scope, with the opaque reference that names that exact key version.</summary>
public sealed record WrappedDataKey(string KeyReference, byte[] WrappedKeyBytes);

/// <summary>
/// E-1 key-encryption-key provider (interface v2). Asynchronous and cancellable (remote-capable), organization-scoped,
/// purpose-separated and versioned; provider-neutral (no SDK types). Wrapping always uses the scope's current key version and
/// returns the reference it used; unwrapping resolves the reference to exactly one retained version of exactly that scope and
/// fails closed otherwise — unknown, disabled or foreign versions are never substituted. Production providers (Azure Key
/// Vault/Managed HSM) are not implemented; Development/Testing: the local keyring, refused elsewhere at startup.
/// </summary>
public interface IEncryptionKeyProvider
{
    string ProviderName { get; }

    /// <summary>Local, non-remote readiness: false when no permitted key material or a valid key-version table is configured.</summary>
    bool IsConfigured { get; }

    /// <summary>Wraps <paramref name="dataKey"/> under the current key version of <paramref name="scope"/>.</summary>
    /// <exception cref="EncryptionUnavailableException">No usable key is configured for the scope.</exception>
    Task<WrappedDataKey> WrapDataKeyAsync(DataKeyScope scope, ReadOnlyMemory<byte> dataKey, CancellationToken cancellationToken);

    /// <summary>Unwraps a data key wrapped under <paramref name="keyReference"/>, which must name a retained version of <paramref name="scope"/>.</summary>
    /// <exception cref="EncryptionUnavailableException">The reference is unknown, or names an unknown or disabled version.</exception>
    /// <exception cref="System.Security.Cryptography.CryptographicException">The reference names another organization or purpose, or the wrapped key does not authenticate.</exception>
    Task<byte[]> UnwrapDataKeyAsync(DataKeyScope scope, string keyReference, ReadOnlyMemory<byte> wrappedDataKey, CancellationToken cancellationToken);
}

public sealed class EncryptionUnavailableException(string detail) : Exception(detail);
