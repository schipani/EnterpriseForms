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
/// </summary>
public sealed class AesGcmFieldEncryptionService(IEncryptionKeyProvider keys) : IFieldEncryptionService
{
    public const string AlgorithmName = "AES-256-GCM";
    public const int EnvelopeVersion = 1;
    private const int TagSize = 16;
    private const int NonceSize = 12;
    private static readonly byte[] Domain = Encoding.ASCII.GetBytes("DynamicForms/FieldValue/v1");

    public string ProviderName => keys.ProviderName;

    public bool IsAvailable => keys.IsConfigured;

    public FieldEncryptedValue Encrypt(FieldEncryptionContext context, string canonicalPlaintext)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(canonicalPlaintext);
        if (!keys.IsConfigured)
        {
            throw new EncryptionUnavailableException("no encryption key provider is configured; encrypted fields cannot be written");
        }

        var dek = RandomNumberGenerator.GetBytes(32);
        try
        {
            var keyReference = keys.CurrentKeyReference;
            var wrapped = keys.WrapDataKey(dek, keyReference);
            var nonce = RandomNumberGenerator.GetBytes(NonceSize);
            var plaintext = Encoding.UTF8.GetBytes(canonicalPlaintext);
            var ciphertext = new byte[plaintext.Length];
            var tag = new byte[TagSize];
            using (var aes = new AesGcm(dek, TagSize))
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
            CryptographicOperations.ZeroMemory(dek);
        }
    }

    public string Decrypt(FieldEncryptedValue value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.EncryptionAlgorithmName != AlgorithmName || value.EncryptionEnvelopeVersion != EnvelopeVersion || value.EncryptionNonceBytes.Length != NonceSize || value.EncryptionAuthTagBytes.Length != TagSize)
        {
            throw new CryptographicException("unsupported field encryption envelope");
        }

        if (!keys.IsConfigured)
        {
            throw new EncryptionUnavailableException("no encryption key provider is configured; encrypted fields cannot be read");
        }

        var dek = keys.UnwrapDataKey(value.WrappedDataKeyBytes, value.EncryptionKeyReference);
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

    private static byte[] Aad(FieldEncryptionContext c)
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
