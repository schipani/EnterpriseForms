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

    public FieldEncryptedValue Encrypt(FieldEncryptionContext context, string canonicalPlaintext) =>
        Encrypt(context, canonicalPlaintext, RandomNumberGenerator.GetBytes(DataKeySize), RandomNumberGenerator.GetBytes(NonceSize));

    /// <summary>
    /// Deterministic core (the public overload supplies a random single-use DEK and nonce; tests reach this one for the
    /// known-answer vectors through InternalsVisibleTo). Takes ownership of <paramref name="dataKey"/>: it is zeroed on every exit path.
    /// </summary>
    internal FieldEncryptedValue Encrypt(FieldEncryptionContext context, string canonicalPlaintext, byte[] dataKey, byte[] nonce)
    {
        try
        {
            ArgumentNullException.ThrowIfNull(context);
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

            var keyReference = keys.CurrentKeyReference;
            var wrapped = keys.WrapDataKey(dataKey, keyReference);
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
                WrappedDataKeyBytes = wrapped,
                EncryptionAlgorithmName = AlgorithmName,
                EncryptionKeyReference = keyReference,
                EncryptionEnvelopeVersion = EnvelopeVersion,
                EncryptedLogicalType = context.LogicalType,
            };
        }
        finally
        {
            CryptographicOperations.ZeroMemory(dataKey);
        }
    }

    public string Decrypt(FieldEncryptedValue value)
    {
        ArgumentNullException.ThrowIfNull(value);
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
            var dek = keys.UnwrapDataKey(value.WrappedDataKeyBytes, value.EncryptionKeyReference);
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
