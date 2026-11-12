using EnterpriseForms.Application.Features.Documents.Ports;
using EnterpriseForms.Domain.Features.FormRecords;

namespace EnterpriseForms.Application.Features.FormRecords.Encryption;

/// <summary>AAD binding of one encrypted field value (CLAUDE.md §4C.3): tenant, record, stable field id, stable item id and the logical type the plaintext was serialized as.</summary>
public sealed record FieldEncryptionContext(Guid TenantId, Guid FormRecordId, Guid FieldDefinitionId, Guid FieldValueItemId, string LogicalType);

/// <summary>
/// Authenticated application-layer encryption of a single typed field value. The plaintext is the field's canonical
/// invariant wire string; the ciphertext envelope (ciphertext, nonce, tag, wrapped per-value DEK, algorithm, key
/// reference, envelope version, logical type) is what the row HMAC and the Merkle manifest bind. Keys never leave the
/// key provider; decryption succeeds only for the exact tenant/record/field/item it was sealed for.
/// E-1: asynchronous and cancellable (the key provider is remote-capable); every call names its key purpose — field values
/// use <see cref="EncryptionKeyPurpose.FieldValue"/>, the creation-idempotency fingerprint key
/// <see cref="EncryptionKeyPurpose.IdempotencyProtection"/> — and the organization is the value's tenant.
/// </summary>
public interface IFieldEncryptionService
{
    string ProviderName { get; }

    bool IsAvailable { get; }

    /// <summary>Encrypts <paramref name="canonicalPlaintext"/> into a fresh envelope under the tenant's current key of <paramref name="purpose"/>. Throws <see cref="EncryptionUnavailableException"/> when no permitted key provider is configured.</summary>
    Task<FieldEncryptedValue> EncryptAsync(FieldEncryptionContext context, string canonicalPlaintext, EncryptionKeyPurpose purpose, CancellationToken cancellationToken);

    /// <summary>Decrypts and authenticates an envelope with a key of the value's tenant and <paramref name="purpose"/>; a tag mismatch, foreign AAD, foreign or unknown key is an exception (fail closed, nothing released).</summary>
    Task<string> DecryptAsync(FieldEncryptedValue value, EncryptionKeyPurpose purpose, CancellationToken cancellationToken);
}
