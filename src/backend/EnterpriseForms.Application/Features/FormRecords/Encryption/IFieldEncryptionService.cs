using EnterpriseForms.Application.Features.Documents.Ports;
using EnterpriseForms.Domain.Features.FormRecords;

namespace EnterpriseForms.Application.Features.FormRecords.Encryption;

/// <summary>AAD binding of one encrypted field value (CLAUDE.md §4C.3): tenant, record, stable field id, stable item id and the logical type the plaintext was serialized as.</summary>
public sealed record FieldEncryptionContext(Guid TenantId, Guid FormRecordId, Guid FieldDefinitionId, Guid FieldValueItemId, string LogicalType);

/// <summary>
/// E3-M2c R-M2c-1: the stored sealed HMAC key of one record-creation attempt (F-C7-01), exactly as the attempt row holds it. It has
/// no field, logical type, signed schema or Envelope v2 member: the service supplies the fixed seal identity itself, so this type can
/// carry nothing but a creation-fingerprint key envelope.
/// </summary>
public sealed record CreationFingerprintKeyEnvelope(
    Guid TenantId,
    Guid FormRecordId,
    Guid FormRecordCreationAttemptId,
    byte[] Ciphertext,
    byte[] Nonce,
    byte[] Tag,
    byte[] WrappedKey,
    string AlgorithmName,
    string KeyReference,
    int EnvelopeVersion);

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

    /// <summary>
    /// E3-M2c OD-M2c-02a <b>Mode B, self-sealed decryption</b>: only for an envelope the current trusted operation itself sealed
    /// from its own input, before Authority staging (the reviewed call sites are pinned by <c>DecryptionCallSiteTests</c>).
    /// Envelope version 1 only, signed schema 0 (not yet staged) or 1, and every Envelope v2 column NULL; anything else is refused
    /// before the key provider is asked. Never reaches the Envelope v2 opener and never falls back to another format. Decrypts and
    /// authenticates with a key of the value's tenant and <paramref name="purpose"/>; a tag mismatch, foreign AAD, foreign or unknown
    /// key is an exception (fail closed, nothing released). Stored or Authority-verified content uses <see cref="DecryptVerifiedAsync"/>.
    /// </summary>
    Task<string> DecryptAsync(FieldEncryptedValue value, EncryptionKeyPurpose purpose, CancellationToken cancellationToken);

    /// <summary>
    /// E3-M2c OD-M2c-02a <b>Mode A, Authority-verified decryption</b> of stored field values of one organization (FieldValue
    /// purpose), in one batch. Strict S-8 dispatch per row: signed schema 1 with envelope 1 (and no Envelope v2 column set) opens
    /// with the v1 reader; schema 2 with envelope 2 (and every v2 column set) opens with the Envelope v2 reader, one key unwrap per
    /// data-key group; schema 0 and every other combination is refused. Every row is checked before the key provider is asked; the
    /// first failure aborts the whole call (fail closed, no partial result); there is no format guessing or provider fallback.
    /// Returns one plaintext per row, in order.
    /// <para>
    /// <b>This method does not perform Authority verification</b> (R-M2c-2). It validates the cryptographic envelope and the S-8
    /// format rules only; it checks no RowHmac, Merkle manifest, accepted head or permission. "Verified" names the caller contract:
    /// pass only rows of an Authority-verified read (<c>VerifiedRecordReader</c>), or — for the two Worker paths — stored rows of the
    /// head the Authority names, whose plaintext stays inside the trusted operation until the Authority binds those rows. The reviewed
    /// callers are pinned by <c>DecryptionCallSiteTests</c>.
    /// </para>
    /// </summary>
    Task<IReadOnlyList<string>> DecryptVerifiedAsync(Guid organizationId, IReadOnlyList<FieldEncryptedValue> values, CancellationToken cancellationToken);

    /// <summary>
    /// E3-M2c R-M2c-1: the single, narrowly scoped exception for stored content outside both modes — opens the sealed HMAC key of one
    /// creation attempt. Fixed IdempotencyProtection purpose, Envelope v1 only, AAD identity fixed to the fingerprint seal
    /// (<c>CreationRequestFingerprint.SealPurposeId</c>, <c>CreationFingerprintKey/v1</c>, the attempt id), and the result must be a
    /// 32-byte key. Anything else is refused before the key provider is asked. A field-value envelope cannot open here (other key
    /// purpose and other AAD). The returned key is the caller's to zero; it is never released beyond the MAC comparison. Pinned to
    /// its one caller by <c>DecryptionCallSiteTests</c>.
    /// </summary>
    Task<byte[]> OpenCreationFingerprintKeyAsync(CreationFingerprintKeyEnvelope envelope, CancellationToken cancellationToken);
}
