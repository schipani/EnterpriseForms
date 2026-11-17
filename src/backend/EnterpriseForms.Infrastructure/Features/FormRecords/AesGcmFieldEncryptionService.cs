using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using EnterpriseForms.Application.Features.Documents.Ports;
using EnterpriseForms.Application.Features.FormRecords.Encryption;
using EnterpriseForms.Domain.Features.FormRecords;

namespace EnterpriseForms.Infrastructure.Features.FormRecords;

/// <summary>
/// AES-256-GCM per field value (CLAUDE.md §4C.3): a random 256-bit DEK per value, wrapped by the organization KEK of the
/// configured <see cref="IEncryptionKeyProvider"/>; a random 96-bit nonce (unique because the DEK is single-use);
/// AAD = domain label ‖ envelope version ‖ tenant ‖ record ‖ field ‖ item ‖ logical type, so a ciphertext cannot be
/// transplanted to another tenant, record, field, item or type. The row HMAC separately binds the whole envelope.
/// The v1 format is pinned by known-answer vectors (E0-6, <c>EncryptionKnownAnswerTests</c>).
/// </summary>
public sealed class AesGcmFieldEncryptionService(IEncryptionKeyProvider keys) : IFieldEncryptionService
{
    public const string AlgorithmName = "AES-256-GCM";
    public const int EnvelopeVersion = 1;
    private const int TagSize = 16;
    private const int NonceSize = 12;
    private const int DataKeySize = 32;
    private static readonly byte[] Domain = Encoding.ASCII.GetBytes("DynamicForms/FieldValue/v1");

    public string ProviderName => keys.ProviderName;

    public bool IsAvailable => keys.IsConfigured;

    public Task<FieldEncryptedValue> EncryptAsync(FieldEncryptionContext context, string canonicalPlaintext, EncryptionKeyPurpose purpose, CancellationToken cancellationToken) =>
        EncryptAsync(context, canonicalPlaintext, purpose, RandomNumberGenerator.GetBytes(DataKeySize), RandomNumberGenerator.GetBytes(NonceSize), cancellationToken);

    /// <summary>
    /// Deterministic core (the public overload supplies a random single-use DEK and nonce; tests reach this one for the
    /// known-answer vectors through InternalsVisibleTo). Takes ownership of <paramref name="dataKey"/>: it is zeroed on every exit path.
    /// </summary>
    internal async Task<FieldEncryptedValue> EncryptAsync(FieldEncryptionContext context, string canonicalPlaintext, EncryptionKeyPurpose purpose, byte[] dataKey, byte[] nonce, CancellationToken cancellationToken)
    {
        try
        {
            ArgumentNullException.ThrowIfNull(context);
            RequireSupportedPurpose(purpose);
            ArgumentNullException.ThrowIfNull(canonicalPlaintext);
            ArgumentNullException.ThrowIfNull(dataKey);
            ArgumentNullException.ThrowIfNull(nonce);
            if (!keys.IsConfigured)
            {
                throw new EncryptionUnavailableException("no encryption key provider is configured; encrypted fields cannot be written");
            }

            if (dataKey.Length != DataKeySize || nonce.Length != NonceSize)
            {
                throw new ArgumentException("a 256-bit data key and a 96-bit nonce are required");
            }

            // E1-2/E1-3: the organization's current key of the requested purpose wraps the single-use DEK.
            var wrapped = await keys.WrapDataKeyAsync(new DataKeyScope(context.TenantId, purpose), dataKey, cancellationToken).ConfigureAwait(false);
            var plaintext = Encoding.UTF8.GetBytes(canonicalPlaintext);
            var ciphertext = new byte[plaintext.Length];
            var tag = new byte[TagSize];
            using (var aes = new AesGcm(dataKey, TagSize))
            {
                aes.Encrypt(nonce, plaintext, ciphertext, tag, Aad(context));
            }

            CryptographicOperations.ZeroMemory(plaintext);
            return new FieldEncryptedValue
            {
                FieldEncryptedValueId = Guid.CreateVersion7(),
                TenantId = context.TenantId,
                FormRecordId = context.FormRecordId,
                FieldDefinitionId = context.FieldDefinitionId,
                FieldValueItemId = context.FieldValueItemId,
                EncryptedValueBytes = ciphertext,
                EncryptionNonceBytes = nonce,
                EncryptionAuthTagBytes = tag,
                WrappedDataKeyBytes = wrapped.WrappedKeyBytes,
                EncryptionAlgorithmName = AlgorithmName,
                EncryptionKeyReference = wrapped.KeyReference,
                EncryptionEnvelopeVersion = EnvelopeVersion,
                EncryptedLogicalType = context.LogicalType,
            };
        }
        finally
        {
            CryptographicOperations.ZeroMemory(dataKey);
        }
    }

    /// <summary>
    /// E3-M2c OD-M2c-02a Mode B (self-sealed): envelope 1 only, signed schema 0 or 1, every Envelope v2 column NULL, then the
    /// unchanged v1 decryption. Never reaches the Envelope v2 reader; a refusal happens before the key provider is asked.
    /// </summary>
    public Task<string> DecryptAsync(FieldEncryptedValue value, EncryptionKeyPurpose purpose, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(value);
        RequireSupportedPurpose(purpose);
        if (value.EncryptionEnvelopeVersion != EnvelopeVersion || value.RowSignatureSchemaVersion is not (0 or 1) || HasAnyEnvelopeV2Column(value))
        {
            throw Refused(value, "the self-sealed field decryption accepts only an Envelope v1 value of signed schema 0 or 1 without Envelope v2 columns");
        }

        return OpenV1Async(value, purpose, cancellationToken);
    }

    /// <summary>
    /// E3-M2c OD-M2c-02a Mode A (Authority-verified, FieldValue purpose): strict S-8 dispatch per row — (schema 1, envelope 1, no v2
    /// column) to the v1 reader; (schema 2, envelope 2, every v2 column) to the Envelope v2 reader; anything else (schema 0 included)
    /// refused. Every row is validated before any key operation; v1 rows unwrap once each, v2 rows once per data-key group; the first
    /// failure aborts the call (no partial result, no fallback).
    /// </summary>
    public async Task<IReadOnlyList<string>> DecryptVerifiedAsync(Guid organizationId, IReadOnlyList<FieldEncryptedValue> values, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (organizationId == Guid.Empty)
        {
            throw new ArgumentException("a verified decryption names its organization", nameof(organizationId));
        }

        var seen = new HashSet<FieldEncryptedValue>(ReferenceEqualityComparer.Instance);
        var isV2 = new bool[values.Count];
        var v2Indexes = new List<int>();
        var batch = new Documents.EnvelopeV2.EnvelopeV2ProductReader.FieldBatch(organizationId);

        // 1. Everything that needs no key, for every row, before the key provider is asked anything.
        for (var i = 0; i < values.Count; i++)
        {
            var value = values[i] ?? throw new ArgumentException("a verified decryption batch has no null rows", nameof(values));
            if (!seen.Add(value))
            {
                throw new ArgumentException("a verified decryption batch lists each row once", nameof(values));
            }

            if (value.TenantId != organizationId)
            {
                throw Refused(value, "a verified decryption batch belongs to exactly one organization");
            }

            switch ((value.RowSignatureSchemaVersion, value.EncryptionEnvelopeVersion))
            {
                case (1, EnvelopeVersion) when !HasAnyEnvelopeV2Column(value):
                    break;
                case (2, 2) when HasEveryEnvelopeV2Column(value):
                    try
                    {
                        batch.Add(value);
                    }
                    catch (Exception ex) when (Documents.EnvelopeV2.EnvelopeV2ProductReader.ReasonFor(ex) is { } reason)
                    {
                        AttachFieldFailure(ex, reason, value);
                        throw;
                    }

                    isV2[i] = true;
                    v2Indexes.Add(i);
                    break;
                default:
                    throw Refused(value, "unsupported signed schema and envelope version combination for a verified field read");
            }
        }

        if (values.Count > 0 && !keys.IsConfigured)
        {
            var unavailable = new EncryptionUnavailableException("no encryption key provider is configured; encrypted fields cannot be read");
            AttachFieldFailure(unavailable, DecryptionFailureReasons.KeyUnavailable, values[0]);
            throw unavailable;
        }

        // 2. Key operations: v1 rows one by one, then every v2 row in one opener call.
        var results = new string[values.Count];
        for (var i = 0; i < values.Count; i++)
        {
            if (!isV2[i])
            {
                results[i] = await OpenV1Async(values[i], EncryptionKeyPurpose.FieldValue, cancellationToken).ConfigureAwait(false);
            }
        }

        if (batch.Count > 0)
        {
            IReadOnlyList<string> opened;
            try
            {
                opened = await batch.OpenAsync(keys, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (Documents.EnvelopeV2.EnvelopeV2ProductReader.ReasonFor(ex) is { } reason)
            {
                // The opener refuses the whole batch; the failure names the record and the batch's first v2 item (R-M2c-3).
                AttachFieldFailure(ex, reason, values[v2Indexes[0]]);
                throw;
            }

            for (var k = 0; k < v2Indexes.Count; k++)
            {
                results[v2Indexes[k]] = opened[k];
            }
        }

        return results;
    }

    /// <summary>
    /// E3-M2c R-M2c-1: opens a stored creation-fingerprint key. The envelope type carries no field identity, logical type, schema or
    /// v2 member; the seal identity is fixed here, so only a fingerprint seal of exactly this attempt can authenticate (AAD) and only
    /// under the organization's IdempotencyProtection key (key scope). Envelope v1 only; a key that is not 32 bytes is refused.
    /// </summary>
    public async Task<byte[]> OpenCreationFingerprintKeyAsync(CreationFingerprintKeyEnvelope envelope, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        var value = new FieldEncryptedValue
        {
            TenantId = envelope.TenantId,
            FormRecordId = envelope.FormRecordId,
            FieldDefinitionId = Application.Features.FormRecords.CreateFormRecord.CreationRequestFingerprint.SealPurposeId,
            FieldValueItemId = envelope.FormRecordCreationAttemptId,
            EncryptedLogicalType = Application.Features.FormRecords.CreateFormRecord.CreationRequestFingerprint.SealLogicalType,
            EncryptedValueBytes = envelope.Ciphertext ?? [],
            EncryptionNonceBytes = envelope.Nonce ?? [],
            EncryptionAuthTagBytes = envelope.Tag ?? [],
            WrappedDataKeyBytes = envelope.WrappedKey ?? [],
            EncryptionAlgorithmName = envelope.AlgorithmName ?? string.Empty,
            EncryptionKeyReference = envelope.KeyReference ?? string.Empty,
            EncryptionEnvelopeVersion = envelope.EnvelopeVersion, // signed schema stays 0 (never staged) and no v2 column exists on the input type
        };
        if (envelope.TenantId == Guid.Empty || envelope.FormRecordId == Guid.Empty || envelope.FormRecordCreationAttemptId == Guid.Empty || envelope.EnvelopeVersion != EnvelopeVersion)
        {
            throw Refused(value, "a creation-fingerprint key opens only from an Envelope v1 seal of one attempt");
        }

        var encoded = await OpenV1Async(value, EncryptionKeyPurpose.IdempotencyProtection, cancellationToken).ConfigureAwait(false);
        byte[] key;
        try
        {
            key = Convert.FromBase64String(encoded);
        }
        catch (FormatException)
        {
            throw new CryptographicException("the creation-fingerprint key is malformed");
        }

        if (key.Length != 32)
        {
            CryptographicOperations.ZeroMemory(key);
            throw new CryptographicException("the creation-fingerprint key is malformed");
        }

        return key;
    }

    private static bool HasAnyEnvelopeV2Column(FieldEncryptedValue value) => value.DataKeyIdentifier is not null || value.DataKeyCommitmentBytes is not null || value.KeyWrapAlgorithmCode is not null;

    private static bool HasEveryEnvelopeV2Column(FieldEncryptedValue value) => value.DataKeyIdentifier is not null && value.DataKeyCommitmentBytes is not null && value.KeyWrapAlgorithmCode is not null;

    /// <summary>A refusal before any key operation (E0-4 reason UNSUPPORTED_ENVELOPE, identifiers only).</summary>
    private static CryptographicException Refused(FieldEncryptedValue value, string message)
    {
        var refusal = new CryptographicException(message);
        AttachFieldFailure(refusal, DecryptionFailureReasons.UnsupportedEnvelope, value);
        return refusal;
    }

    private static void AttachFieldFailure(Exception exception, string reason, FieldEncryptedValue value) =>
        DecryptionFailure.ForFieldValue(reason, value.TenantId, value.FormRecordId, value.FieldDefinitionId, value.FieldValueItemId).AttachTo(exception);

    /// <summary>The unchanged Envelope v1 field decryption (shared by both modes after their own admission checks).</summary>
    private async Task<string> OpenV1Async(FieldEncryptedValue value, EncryptionKeyPurpose purpose, CancellationToken cancellationToken)
    {
        var stage = DecryptionStage.Envelope;
        try
        {
            if (value.EncryptionAlgorithmName != AlgorithmName || value.EncryptionEnvelopeVersion != EnvelopeVersion || value.EncryptionNonceBytes.Length != NonceSize || value.EncryptionAuthTagBytes.Length != TagSize)
            {
                throw new CryptographicException("unsupported field encryption envelope");
            }

            if (!keys.IsConfigured)
            {
                throw new EncryptionUnavailableException("no encryption key provider is configured; encrypted fields cannot be read");
            }

            stage = DecryptionStage.KeyUnwrap;
            var dek = await keys.UnwrapDataKeyAsync(new DataKeyScope(value.TenantId, purpose), value.EncryptionKeyReference, value.WrappedDataKeyBytes, cancellationToken).ConfigureAwait(false);
            stage = DecryptionStage.Payload;
            var plaintext = new byte[value.EncryptedValueBytes.Length];
            try
            {
                using var aes = new AesGcm(dek, TagSize);
                aes.Decrypt(value.EncryptionNonceBytes, value.EncryptedValueBytes, value.EncryptionAuthTagBytes, plaintext, Aad(new FieldEncryptionContext(value.TenantId, value.FormRecordId, value.FieldDefinitionId, value.FieldValueItemId, value.EncryptedLogicalType)));
                return Encoding.UTF8.GetString(plaintext);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(dek);
                CryptographicOperations.ZeroMemory(plaintext);
            }
        }
        catch (Exception ex) when (DecryptionFailure.ReasonFor(ex, stage) is { } reason)
        {
            // E0-4: identifiers and a reason only; the exception (type, message, fail-closed handling) is otherwise unchanged.
            DecryptionFailure.ForFieldValue(reason, value.TenantId, value.FormRecordId, value.FieldDefinitionId, value.FieldValueItemId).AttachTo(ex);
            throw;
        }
    }

    /// <summary>This envelope seals field values and the idempotency fingerprint key; other purposes have their own services.</summary>
    private static void RequireSupportedPurpose(EncryptionKeyPurpose purpose)
    {
        ArgumentNullException.ThrowIfNull(purpose);
        if (!ReferenceEquals(purpose, EncryptionKeyPurpose.FieldValue) && !ReferenceEquals(purpose, EncryptionKeyPurpose.IdempotencyProtection))
        {
            throw new ArgumentException("the field-value envelope is used only for field values and idempotency protection", nameof(purpose));
        }
    }

    /// <summary>The exact v1 AAD (internal for the known-answer vectors).</summary>
    internal static byte[] Aad(FieldEncryptionContext c)
    {
        var type = Encoding.UTF8.GetBytes(c.LogicalType);
        var aad = new byte[Domain.Length + 4 + (16 * 4) + 4 + type.Length];
        var offset = 0;
        Domain.CopyTo(aad, offset);
        offset += Domain.Length;
        BinaryPrimitives.WriteInt32BigEndian(aad.AsSpan(offset, 4), EnvelopeVersion);
        offset += 4;
        foreach (var id in new[] { c.TenantId, c.FormRecordId, c.FieldDefinitionId, c.FieldValueItemId })
        {
            id.TryWriteBytes(aad.AsSpan(offset, 16), bigEndian: true, out _);
            offset += 16;
        }

        BinaryPrimitives.WriteInt32BigEndian(aad.AsSpan(offset, 4), type.Length);
        offset += 4;
        type.CopyTo(aad, offset);
        return aad;
    }
}
