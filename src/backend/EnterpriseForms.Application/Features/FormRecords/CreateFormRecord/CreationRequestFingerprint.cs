using System.Security.Cryptography;
using System.Text.Json;
using EnterpriseForms.Application.Common;
using EnterpriseForms.Application.Features.Documents.Ports;
using EnterpriseForms.Application.Features.FormRecords.Encryption;
using EnterpriseForms.Contracts.Features;
using EnterpriseForms.Domain.Features.FormRecords;

namespace EnterpriseForms.Application.Features.FormRecords.CreateFormRecord;

/// <summary>
/// F-C7-01: decides whether a creation request repeats an earlier one with the same idempotency key. The canonical
/// request (actor, form, owner team, parent, every value and repeating entry) is MACed with a random per-attempt key that
/// is stored only sealed by the field-encryption envelope, so the stored fingerprint reveals nothing testable about the
/// values to a database reader. Comparison is fixed-time. An envelope that does not open (tampered, foreign) is treated
/// as a conflict: the request cannot be shown to be the same one, so it is not processed as a retry.
/// </summary>
public static class CreationRequestFingerprint
{
    /// <summary>AAD purpose for the sealed key (never a real field id: real ids are UUIDv7).</summary>
    public static readonly Guid SealPurposeId = new("f0c70100-0000-8000-8000-000000000001");

    public const string SealLogicalType = "CreationFingerprintKey/v1";

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    /// <summary>E3-M2c R-M2c-1: the stored envelope JSON is read strictly; an unknown member (for example Envelope v2 metadata) is refused, never ignored.</summary>
    private static readonly JsonSerializerOptions StrictEnvelopeJson = new() { UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow };

    public sealed record Request(string ActorIssuer, string ActorSubject, Guid FormDefinitionId, Guid? OwnerTeamId, Guid? ParentFormRecordId, Guid? FormRelationshipDefinitionId,
        IReadOnlyDictionary<string, string> Values, IReadOnlyList<RecordGroupValuesRequest>? Groups);

    /// <summary>Deterministic bytes: ordinal-sorted value keys; repeating entries keep their order (order is part of the request).</summary>
    public static byte[] Canonicalize(Request request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var canonical = new
        {
            v = 1,
            actor_issuer = request.ActorIssuer,
            actor_subject = request.ActorSubject,
            form_definition_id = request.FormDefinitionId,
            owner_team_id = request.OwnerTeamId,
            parent_form_record_id = request.ParentFormRecordId,
            form_relationship_definition_id = request.FormRelationshipDefinitionId,
            values = Sorted(request.Values),
            groups = (request.Groups ?? []).Select(g => new
            {
                group_key = g.GroupKey,
                occurrences = g.Occurrences.Select(o => new { form_record_group_id = o.FormRecordGroupId, values = Sorted(o.Values) }).ToList(),
            }).ToList(),
        };
        return JsonSerializer.SerializeToUtf8Bytes(canonical, Json);
    }

    /// <summary>E-1: the key is sealed under the tenant's IdempotencyProtection key (purpose-separated from field values).</summary>
    public static async Task<FormRecordCreationAttempt> CreateAsync(IFieldEncryptionService encryption, Guid tenantId, Guid formRecordId, Guid actorUserId, DateTimeOffset now, byte[] canonicalRequest, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(encryption);
        var attemptId = Guid.CreateVersion7();
        var key = RandomNumberGenerator.GetBytes(32);
        try
        {
            var sealedKey = await encryption.EncryptAsync(new FieldEncryptionContext(tenantId, formRecordId, SealPurposeId, attemptId, SealLogicalType), Convert.ToBase64String(key), EncryptionKeyPurpose.IdempotencyProtection, cancellationToken).ConfigureAwait(false);
            return new FormRecordCreationAttempt
            {
                FormRecordCreationAttemptId = attemptId,
                TenantId = tenantId,
                FormRecordId = formRecordId,
                CreationRequestFingerprint = HMACSHA256.HashData(key, canonicalRequest),
                FingerprintKeyEnvelopeJson = JsonSerializer.Serialize(Envelope.From(sealedKey), Json),
                CreatedOn = now,
                CreatedByUserId = actorUserId,
            };
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    /// <summary>
    /// True only when the stored envelope opens for this tenant/record/attempt and the MAC of <paramref name="canonicalRequest"/> matches.
    /// E-1: opens with the tenant's IdempotencyProtection key, or the legacy development key for attempts sealed before E-1.
    /// E3-M2c R-M2c-1: through the narrow <see cref="IFieldEncryptionService.OpenCreationFingerprintKeyAsync"/> (Envelope v1, fixed
    /// purpose and seal identity), not a general decrypt API. The key is used only for the fixed-time MAC comparison and zeroed.
    /// </summary>
    public static async Task<bool> MatchesAsync(IFieldEncryptionService encryption, FormRecordCreationAttempt attempt, byte[] canonicalRequest, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(encryption);
        ArgumentNullException.ThrowIfNull(attempt);
        byte[] key;
        try
        {
            var envelope = JsonSerializer.Deserialize<Envelope>(attempt.FingerprintKeyEnvelopeJson, StrictEnvelopeJson) ?? throw new JsonException("empty envelope");
            key = await encryption.OpenCreationFingerprintKeyAsync(envelope.ToSealedKey(attempt), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is CryptographicException or JsonException or FormatException or ArgumentException)
        {
            return false;
        }

        try
        {
            return attempt.CreationRequestFingerprint.Length == 32
                && CryptographicOperations.FixedTimeEquals(attempt.CreationRequestFingerprint, HMACSHA256.HashData(key, canonicalRequest));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    private static SortedDictionary<string, string> Sorted(IReadOnlyDictionary<string, string>? values) =>
        new((values ?? new Dictionary<string, string>()).ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal), StringComparer.Ordinal);

    /// <summary>The envelope fields; the AAD identities come from the attempt row itself, so a row moved to another record does not open.</summary>
    private sealed record Envelope(string Ciphertext, string Nonce, string Tag, string WrappedKey, string Algorithm, string KeyReference, int EnvelopeVersion)
    {
        public static Envelope From(FieldEncryptedValue value) => new(
            Convert.ToBase64String(value.EncryptedValueBytes), Convert.ToBase64String(value.EncryptionNonceBytes), Convert.ToBase64String(value.EncryptionAuthTagBytes),
            Convert.ToBase64String(value.WrappedDataKeyBytes), value.EncryptionAlgorithmName, value.EncryptionKeyReference, value.EncryptionEnvelopeVersion);

        public CreationFingerprintKeyEnvelope ToSealedKey(FormRecordCreationAttempt attempt) => new(
            attempt.TenantId, attempt.FormRecordId, attempt.FormRecordCreationAttemptId,
            Convert.FromBase64String(Ciphertext), Convert.FromBase64String(Nonce), Convert.FromBase64String(Tag), Convert.FromBase64String(WrappedKey),
            Algorithm, KeyReference, EnvelopeVersion);
    }
}
