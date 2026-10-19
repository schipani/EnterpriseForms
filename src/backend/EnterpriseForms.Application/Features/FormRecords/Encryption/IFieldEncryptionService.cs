using EnterpriseForms.Domain.Features.FormRecords;

namespace EnterpriseForms.Application.Features.FormRecords.Encryption;

/// <summary>AAD binding of one encrypted field value (CLAUDE.md §4C.3): tenant, record, stable field id, stable item id and the logical type the plaintext was serialized as.</summary>
public sealed record FieldEncryptionContext(Guid TenantId, Guid FormRecordId, Guid FieldDefinitionId, Guid FieldValueItemId, string LogicalType);

/// <summary>
/// Authenticated application-layer encryption of a single typed field value. The plaintext is the field's canonical
/// invariant wire string; the ciphertext envelope (ciphertext, nonce, tag, wrapped per-value DEK, algorithm, key
/// reference, envelope version, logical type) is what the row HMAC and the Merkle manifest bind. Keys never leave the
/// key provider; decryption succeeds only for the exact tenant/record/field/item it was sealed for.
/// </summary>
public interface IFieldEncryptionService
{
    string ProviderName { get; }

    bool IsAvailable { get; }

    /// <summary>Encrypts <paramref name="canonicalPlaintext"/> into a fresh envelope. Throws <see cref="Documents.Ports.EncryptionUnavailableException"/> when no permitted key provider is configured.</summary>
    FieldEncryptedValue Encrypt(FieldEncryptionContext context, string canonicalPlaintext);

    /// <summary>Decrypts and authenticates an envelope; a tag mismatch, foreign AAD or unknown key is a <see cref="System.Security.Cryptography.CryptographicException"/> (fail closed, nothing released).</summary>
    string Decrypt(FieldEncryptedValue value);
}
