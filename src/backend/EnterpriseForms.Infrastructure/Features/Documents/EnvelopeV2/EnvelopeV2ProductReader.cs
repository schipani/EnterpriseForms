using EnterpriseForms.Application.Features.Documents.Ports;
using EnterpriseForms.Application.Features.FormRecords.Encryption;
using EnterpriseForms.Domain.Features.FormRecords;

namespace EnterpriseForms.Infrastructure.Features.Documents.EnvelopeV2;

/// <summary>
/// E3-M2c (dark reader): the only product entry into the Envelope v2 core. It is reachable only from the verified decryption paths
/// of <c>AesGcmFieldEncryptionService</c> and <c>AesGcmFileEncryptionService</c> (EnvelopeV2IsolationTests), and it references
/// opener types only: no seal batch, no document encryption, no key schedule. It turns the stored columns of a signed schema-2 /
/// envelope-2 row into the M1 envelope objects, whose constructors validate every length, code, reference, organization, purpose
/// and identity before any key operation, then delegates to the M1 opener (one unwrap per data-key group, key commitment before
/// AEAD) or to the M1 document decryptor. The caller has already applied the S-8 (schema, envelope) dispatch and checked that the
/// v2 columns are present.
/// </summary>
internal static class EnvelopeV2ProductReader
{
    /// <summary>The AEAD algorithm name a v2 row carries (same spelling as v1).</summary>
    public const string AlgorithmName = "AES-256-GCM";

    /// <summary>
    /// The Envelope v2 field rows of one verified read of one organization. <see cref="Add"/> validates a row completely (no key
    /// needed); <see cref="OpenAsync"/> opens every added row in a single M1 opener call, so every request's identity and every
    /// data-key group's consistency are checked before the first unwrap, and each group's seed is unwrapped exactly once.
    /// </summary>
    internal sealed class FieldBatch(Guid organizationId)
    {
        private readonly List<FieldOpenRequestV2> requests = [];

        public int Count => requests.Count;

        /// <summary>Validates one stored v2 field row (FieldValue purpose) and queues it; throws <see cref="EnvelopeV2Exception"/> when the row is not a well-formed v2 envelope of this organization.</summary>
        public void Add(FieldEncryptedValue row)
        {
            ArgumentNullException.ThrowIfNull(row);
            if (row.TenantId != organizationId)
            {
                throw new EnvelopeV2Exception(EnvelopeV2FailureReason.IdentityMismatch);
            }

            var header = HeaderOf(EnvelopeV2PayloadKind.FieldValueItem, organizationId, row.EncryptionAlgorithmName, row.KeyWrapAlgorithmCode, row.EncryptionKeyReference, row.DataKeyIdentifier, row.DataKeyCommitmentBytes);
            var identity = new FieldItemIdentity(row.FormRecordId, row.FieldDefinitionId, row.FieldValueItemId, row.EncryptedLogicalType);
            var envelope = new FieldEnvelopeV2(header, identity, row.WrappedDataKeyBytes ?? [], row.EncryptionNonceBytes ?? [], row.EncryptionAuthTagBytes ?? [], row.EncryptedValueBytes ?? []);
            requests.Add(new FieldOpenRequestV2(envelope, identity));
        }

        public Task<IReadOnlyList<string>> OpenAsync(IEncryptionKeyProvider keys, CancellationToken cancellationToken) =>
            FieldEnvelopeV2Opener.OpenAsync(keys, organizationId, EncryptionKeyPurpose.FieldValue, requests, cancellationToken);
    }

    /// <summary>
    /// Validates a stored v2 document envelope against the verified version identity of <paramref name="context"/> (no key needed).
    /// Throws <see cref="EnvelopeV2Exception"/> when it is not a well-formed v2 document envelope of that organization and version.
    /// </summary>
    public static PreparedDocument PrepareDocument(VerifiedDocumentEnvelope envelope, FileEncryptionContext context)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        ArgumentNullException.ThrowIfNull(context);
        var header = HeaderOf(EnvelopeV2PayloadKind.DocumentVersion, context.TenantId, envelope.AlgorithmName, envelope.KeyWrapAlgorithmCode, envelope.KeyReference, envelope.DataKeyIdentifier, envelope.DataKeyCommitmentBytes);
        var identity = new DocumentVersionIdentity(context.FormRecordId, context.FileAttachmentId, context.VersionNumber);
        var documentContext = new DocumentContext(identity, envelope.SegmentSizeBytes, envelope.NoncePrefix ?? []);
        return new PreparedDocument(new DocumentEnvelopeV2(header, documentContext, envelope.WrappedDataKey ?? []), context.TenantId, identity);
    }

    /// <summary>A validated v2 document envelope, ready to decrypt.</summary>
    internal sealed class PreparedDocument
    {
        private readonly DocumentEnvelopeV2 envelope;
        private readonly Guid organizationId;
        private readonly DocumentVersionIdentity identity;

        internal PreparedDocument(DocumentEnvelopeV2 envelope, Guid organizationId, DocumentVersionIdentity identity)
        {
            this.envelope = envelope;
            this.organizationId = organizationId;
            this.identity = identity;
        }

        /// <summary>M1 document decryption (key commitment before any segment; streaming caller contract of DocumentEnvelopeV2Cipher).</summary>
        public Task DecryptAsync(Stream ciphertext, Stream plaintextSink, IEncryptionKeyProvider keys, CancellationToken cancellationToken) =>
            DocumentEnvelopeV2Cipher.DecryptAsync(ciphertext, plaintextSink, envelope, organizationId, identity, keys, cancellationToken);
    }

    /// <summary>
    /// The E0-4 reason of a failure of this reader (the mapping announced by <see cref="EnvelopeV2FailureReason"/>), or null when the
    /// exception is not a decryption failure (cancellation, an I/O error of the sink).
    /// </summary>
    public static string? ReasonFor(Exception exception) => exception switch
    {
        EnvelopeV2Exception { Reason: EnvelopeV2FailureReason.KeyCommitmentMismatch or EnvelopeV2FailureReason.InvalidDataKey } => DecryptionFailureReasons.KeyMismatch,
        EnvelopeV2Exception { Reason: EnvelopeV2FailureReason.AuthenticationFailed } => DecryptionFailureReasons.AuthenticationFailed,
        EnvelopeV2Exception => DecryptionFailureReasons.UnsupportedEnvelope,
        EncryptionUnavailableException => DecryptionFailureReasons.KeyUnavailable,
        System.Security.Cryptography.CryptographicException => DecryptionFailureReasons.KeyMismatch, // only the key provider's unwrap raises another cryptographic exception here
        _ => null,
    };

    /// <summary>The header H of a stored row: closed algorithm and wrap codes, every v2 column present, the reference in the wrap code's family naming exactly this organization and the kind's purpose (validated by <see cref="EnvelopeV2HeaderPrefix.Create"/>).</summary>
    private static EnvelopeV2Header HeaderOf(EnvelopeV2PayloadKind kind, Guid organizationId, string? algorithmName, int? wrapCode, string? keyReference, Guid? dataKeyIdentifier, byte[]? commitment)
    {
        if (!string.Equals(algorithmName, AlgorithmName, StringComparison.Ordinal) || wrapCode is not { } code || code is < 0 or > byte.MaxValue || !Enum.IsDefined((EnvelopeV2WrapAlgorithm)code))
        {
            throw EnvelopeV2Format.Unsupported();
        }

        if (dataKeyIdentifier is not { } dataKey || commitment is null || keyReference is null)
        {
            throw EnvelopeV2Format.Malformed();
        }

        var prefix = EnvelopeV2HeaderPrefix.Create(kind, (EnvelopeV2WrapAlgorithm)code, organizationId, keyReference, dataKey);
        return new EnvelopeV2Header(prefix, commitment);
    }
}
