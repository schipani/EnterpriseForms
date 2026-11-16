using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;
using EnterpriseForms.Application.Features.Documents.Ports;
using EnterpriseForms.Application.Features.FormRecords.CreateFormRecord;
using EnterpriseForms.Application.Features.FormRecords.Encryption;
using EnterpriseForms.Application.Features.FormRecords.Values;
using EnterpriseForms.Domain.Features.FormDefinitions;
using EnterpriseForms.Domain.Features.FormRecords;
using EnterpriseForms.Infrastructure.Features.FormRecords;
using Xunit;

namespace EnterpriseForms.Api.ContractTests;

/// <summary>
/// E3-M2c closure: R-M2c-1 (the narrow creation-fingerprint key operation cannot become a general stored-content decrypt) and §5
/// (the self-sealed registry's scope, binding and lifetime).
/// </summary>
public sealed class DecryptionClosureRuntimeTests
{
    private static readonly Guid Org = Guid.Parse("019a5b2c-1000-7000-8000-0000000000a1");
    private static readonly Guid OtherOrg = Guid.Parse("019a5b2c-1000-7000-8000-0000000000a2");
    private static readonly Guid Record = Guid.Parse("019a5b2c-1000-7000-8000-0000000000a3");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly byte[] Canonical = CreationRequestFingerprint.Canonicalize(new CreationRequestFingerprint.Request("https://issuer.test", "alice", Guid.CreateVersion7(), null, null, null, new Dictionary<string, string> { ["given_name"] = "Avery" }, null));

    private static (AesGcmFieldEncryptionService Service, EnvelopeV2KeyDouble Keys) Service()
    {
        var keys = new EnvelopeV2KeyDouble();
        return (new AesGcmFieldEncryptionService(keys), keys);
    }

    private static FormRecordCreationAttempt WithEnvelope(FormRecordCreationAttempt attempt, string json) => new()
    {
        FormRecordCreationAttemptId = attempt.FormRecordCreationAttemptId, TenantId = attempt.TenantId, FormRecordId = attempt.FormRecordId,
        CreationRequestFingerprint = attempt.CreationRequestFingerprint, FingerprintKeyEnvelopeJson = json, CreatedOn = attempt.CreatedOn, CreatedByUserId = attempt.CreatedByUserId,
    };

    private static CreationFingerprintKeyEnvelope EnvelopeOf(FormRecordCreationAttempt attempt)
    {
        using var doc = JsonDocument.Parse(attempt.FingerprintKeyEnvelopeJson);
        var r = doc.RootElement;
        return new(attempt.TenantId, attempt.FormRecordId, attempt.FormRecordCreationAttemptId, Convert.FromBase64String(r.GetProperty("Ciphertext").GetString()!), Convert.FromBase64String(r.GetProperty("Nonce").GetString()!),
            Convert.FromBase64String(r.GetProperty("Tag").GetString()!), Convert.FromBase64String(r.GetProperty("WrappedKey").GetString()!), r.GetProperty("Algorithm").GetString()!, r.GetProperty("KeyReference").GetString()!, r.GetProperty("EnvelopeVersion").GetInt32());
    }

    // ---- R-M2c-1 ------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task TheFingerprintOperationOpensALegitimateAttemptKeyOnly()
    {
        var (service, _) = Service();
        var attempt = await CreationRequestFingerprint.CreateAsync(service, Org, Record, Guid.CreateVersion7(), DateTimeOffset.UtcNow, Canonical, Ct);
        Assert.True(await CreationRequestFingerprint.MatchesAsync(service, attempt, Canonical, Ct));
        var key = await service.OpenCreationFingerprintKeyAsync(EnvelopeOf(attempt), Ct);
        Assert.Equal(32, key.Length);
        Assert.True(CryptographicOperations.FixedTimeEquals(attempt.CreationRequestFingerprint, HMACSHA256.HashData(key, Canonical)));

        // Another attempt id, record or organization: the AAD no longer matches (fail closed).
        foreach (var moved in new[] { EnvelopeOf(attempt) with { FormRecordCreationAttemptId = Guid.CreateVersion7() }, EnvelopeOf(attempt) with { FormRecordId = Guid.CreateVersion7() }, EnvelopeOf(attempt) with { TenantId = OtherOrg } })
        {
            await Assert.ThrowsAnyAsync<CryptographicException>(() => service.OpenCreationFingerprintKeyAsync(moved, Ct));
        }
    }

    [Theory]
    [InlineData(2)]
    [InlineData(0)]
    [InlineData(3)]
    public async Task TheFingerprintOperationRefusesEveryEnvelopeVersionButOneBeforeTheProvider(int version)
    {
        var (service, keys) = Service();
        var attempt = await CreationRequestFingerprint.CreateAsync(service, Org, Record, Guid.CreateVersion7(), DateTimeOffset.UtcNow, Canonical, Ct);
        var unwraps = keys.UnwrapCalls;
        var ex = await Assert.ThrowsAnyAsync<CryptographicException>(() => service.OpenCreationFingerprintKeyAsync(EnvelopeOf(attempt) with { EnvelopeVersion = version }, Ct));
        Assert.Equal(DecryptionFailureReasons.UnsupportedEnvelope, DecryptionFailure.Find(ex)?.ReasonCode);
        Assert.Equal(unwraps, keys.UnwrapCalls);

        var json = attempt.FingerprintKeyEnvelopeJson.Replace("\"EnvelopeVersion\":1", "\"EnvelopeVersion\":" + version, StringComparison.Ordinal);
        Assert.NotEqual(attempt.FingerprintKeyEnvelopeJson, json);
        Assert.False(await CreationRequestFingerprint.MatchesAsync(service, WithEnvelope(attempt, json), Canonical, Ct)); // a conflict, never a match
        Assert.Equal(unwraps, keys.UnwrapCalls);
    }

    [Theory]
    [InlineData("\"DataKeyIdentifier\":\"019a5b2c-1000-7000-8000-0000000000ff\"")]
    [InlineData("\"DataKeyCommitmentBytes\":\"AAAA\"")]
    [InlineData("\"KeyWrapAlgorithmCode\":2")]
    [InlineData("\"RowSignatureSchemaVersion\":2")]
    public async Task StoredFingerprintJsonCarryingAnyEnvelopeV2OrSchemaMemberIsRefused(string member)
    {
        var (service, keys) = Service();
        var attempt = await CreationRequestFingerprint.CreateAsync(service, Org, Record, Guid.CreateVersion7(), DateTimeOffset.UtcNow, Canonical, Ct);
        var json = attempt.FingerprintKeyEnvelopeJson.TrimEnd('}') + "," + member + "}";
        var unwraps = keys.UnwrapCalls;
        Assert.False(await CreationRequestFingerprint.MatchesAsync(service, WithEnvelope(attempt, json), Canonical, Ct));
        Assert.Equal(unwraps, keys.UnwrapCalls); // refused by the strict parser, before any key operation
        Assert.True(await CreationRequestFingerprint.MatchesAsync(service, attempt, Canonical, Ct)); // the untouched attempt still matches
    }

    [Fact]
    public async Task AStoredFieldValueEnvelopeCannotBeOpenedThroughTheFingerprintException()
    {
        var (service, _) = Service();
        var item = Guid.CreateVersion7();
        var field = await service.EncryptAsync(new FieldEncryptionContext(Org, Record, Guid.CreateVersion7(), item, "String"), Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)), EncryptionKeyPurpose.FieldValue, Ct);
        var smuggled = new CreationFingerprintKeyEnvelope(Org, Record, item, field.EncryptedValueBytes, field.EncryptionNonceBytes, field.EncryptionAuthTagBytes, field.WrappedDataKeyBytes, field.EncryptionAlgorithmName, field.EncryptionKeyReference, 1);
        // The field value's key is a FieldValue key and its AAD names a real field: neither matches the fingerprint seal.
        await Assert.ThrowsAnyAsync<CryptographicException>(() => service.OpenCreationFingerprintKeyAsync(smuggled, Ct));

        // Even a well-formed IdempotencyProtection seal of a value that is not a 32-byte key is refused.
        var attemptId = Guid.CreateVersion7();
        var shortKey = await service.EncryptAsync(new FieldEncryptionContext(Org, Record, CreationRequestFingerprint.SealPurposeId, attemptId, CreationRequestFingerprint.SealLogicalType), Convert.ToBase64String(new byte[16]), EncryptionKeyPurpose.IdempotencyProtection, Ct);
        await Assert.ThrowsAnyAsync<CryptographicException>(() => service.OpenCreationFingerprintKeyAsync(new(Org, Record, attemptId, shortKey.EncryptedValueBytes, shortKey.EncryptionNonceBytes, shortKey.EncryptionAuthTagBytes, shortKey.WrappedDataKeyBytes, shortKey.EncryptionAlgorithmName, shortKey.EncryptionKeyReference, 1), Ct));

        // And a fingerprint seal never opens through the self-sealed wrappers (it was not sealed by ProtectAsync, and they are FieldValue only).
        await Assert.ThrowsAsync<InvalidOperationException>(() => DecryptedFieldValues.DecryptSelfSealedAsync(service, [shortKey], Ct));
    }

    // ---- §5 provenance scope and lifetime ------------------------------------------------------------------------------

    private static FieldDefinition EncryptedField() => new() { FieldDefinitionId = Guid.CreateVersion7(), FieldKey = "secret", FieldDisplayName = "secret", FieldDataType = "String", FieldProtectionMode = "ApplicationEncrypted", FieldClassification = "Confidential" };

    private static async Task<FieldEncryptedValue> SealAsync(IFieldEncryptionService service, FieldDefinition field, string wire, Guid? tenant = null)
    {
        var typed = FieldValueCodec.CreateRow(field, null, [], wire, tenant ?? Org, Record, Guid.CreateVersion7(), DateTimeOffset.UtcNow, Guid.CreateVersion7());
        return (FieldEncryptedValue)await FieldValueCodec.ProtectAsync(typed, field, new Dictionary<Guid, FieldOption>(), service, Ct);
    }

    private static Task<string> SelfSealedAsync(IFieldEncryptionService service, FieldEncryptedValue row) => FieldValueCodec.FormatSelfSealedAsync(row, new Dictionary<Guid, FieldOption>(), service, CancellationToken.None);

    [Fact]
    public async Task MutatingARegisteredSealVoidsItsProvenanceButStagingMetadataDoesNot()
    {
        var (service, _) = Service();
        var field = EncryptedField();
        var own = await SealAsync(service, field, "own");
        var other = await SealAsync(service, field, "other");
        var foreign = await SealAsync(service, field, "foreign", OtherOrg);

        // In-place staging metadata (what the stager sets on the same instance) keeps provenance.
        own.ContentChangeOperationId = Guid.CreateVersion7();
        own.ContentRevision = own.RowRevision = 2;
        own.RowSignature = RandomNumberGenerator.GetBytes(32);
        own.RowSignatureKeyVersion = 1;
        own.RowSignatureSchemaVersion = 1;
        own.FormRecordGroupId = Guid.CreateVersion7();
        Assert.Equal("own", await SelfSealedAsync(service, own));

        // Copying another (stored or foreign) envelope's bytes, organization or identity into a registered instance voids it.
        void CopyFrom(FieldEncryptedValue target, FieldEncryptedValue source)
        {
            target.EncryptedValueBytes = source.EncryptedValueBytes.ToArray();
            target.EncryptionNonceBytes = source.EncryptionNonceBytes.ToArray();
            target.EncryptionAuthTagBytes = source.EncryptionAuthTagBytes.ToArray();
            target.WrappedDataKeyBytes = source.WrappedDataKeyBytes.ToArray();
            target.FieldValueItemId = source.FieldValueItemId;
        }

        var substituted = await SealAsync(service, field, "victim");
        CopyFrom(substituted, other);
        await Assert.ThrowsAsync<InvalidOperationException>(() => SelfSealedAsync(service, substituted));

        var reorganized = await SealAsync(service, field, "mine");
        reorganized.TenantId = OtherOrg;
        await Assert.ThrowsAsync<InvalidOperationException>(() => SelfSealedAsync(service, reorganized));

        var retargeted = await SealAsync(service, field, "mine");
        retargeted.EncryptionKeyReference = foreign.EncryptionKeyReference;
        await Assert.ThrowsAsync<InvalidOperationException>(() => SelfSealedAsync(service, retargeted));

        var relabelled = await SealAsync(service, field, "mine");
        relabelled.DataKeyIdentifier = Guid.CreateVersion7();
        await Assert.ThrowsAsync<InvalidOperationException>(() => SelfSealedAsync(service, relabelled));

        // Equal bytes in a new instance confer nothing.
        var twin = new FieldEncryptedValue
        {
            TenantId = other.TenantId, FormRecordId = other.FormRecordId, FieldDefinitionId = other.FieldDefinitionId, FieldValueItemId = other.FieldValueItemId, EncryptedLogicalType = other.EncryptedLogicalType,
            EncryptedValueBytes = other.EncryptedValueBytes, EncryptionNonceBytes = other.EncryptionNonceBytes, EncryptionAuthTagBytes = other.EncryptionAuthTagBytes, WrappedDataKeyBytes = other.WrappedDataKeyBytes,
            EncryptionAlgorithmName = other.EncryptionAlgorithmName, EncryptionKeyReference = other.EncryptionKeyReference, EncryptionEnvelopeVersion = other.EncryptionEnvelopeVersion,
        };
        await Assert.ThrowsAsync<InvalidOperationException>(() => SelfSealedAsync(service, twin));
        Assert.Equal("other", await SelfSealedAsync(service, other));
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference SealAndDrop(IFieldEncryptionService service)
    {
        var sealedRow = SealAsync(service, EncryptedField(), "short-lived").GetAwaiter().GetResult();
        Assert.Equal("short-lived", SelfSealedAsync(service, sealedRow).GetAwaiter().GetResult());
        return new WeakReference(sealedRow);
    }

    [Fact]
    public void RegistrationsDoNotKeepSealsAliveOrAccumulate()
    {
        var (service, _) = Service();
        var references = Enumerable.Range(0, 50).Select(_ => SealAndDrop(service)).ToList();
        for (var i = 0; i < 3 && references.Any(r => r.IsAlive); i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }

        Assert.All(references, r => Assert.False(r.IsAlive)); // the registry holds no strong reference: provenance ends with the operation's instance
    }

    [Fact]
    public async Task AFailedOrCancelledSealLeavesNoProvenance()
    {
        var (service, keys) = Service();
        var field = EncryptedField();
        var typed = FieldValueCodec.CreateRow(field, null, [], "never sealed", Org, Record, Guid.CreateVersion7(), DateTimeOffset.UtcNow, Guid.CreateVersion7());
        using (var cancelled = new CancellationTokenSource())
        {
            await cancelled.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => FieldValueCodec.ProtectAsync(typed, field, new Dictionary<Guid, FieldOption>(), service, cancelled.Token));
        }

        keys.Unavailable = true;
        await Assert.ThrowsAsync<EncryptionUnavailableException>(() => FieldValueCodec.ProtectAsync(typed, field, new Dictionary<Guid, FieldOption>(), service, Ct));
        keys.Unavailable = false;

        // No envelope instance came out of either attempt, so nothing exists that could carry provenance; the plaintext row is not an envelope.
        Assert.IsType<FieldStringValue>(typed);
        Assert.Equal("never sealed", await FieldValueCodec.FormatSelfSealedAsync(typed, new Dictionary<Guid, FieldOption>(), service, Ct));
    }

    [Fact]
    public async Task ProvenanceDoesNotCrossServiceInstancesThroughSharedState()
    {
        // The singleton field service holds only its key provider: no envelope instance, cache or registry of its own.
        var instanceFields = typeof(AesGcmFieldEncryptionService).GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        Assert.Equal([typeof(IEncryptionKeyProvider)], instanceFields.Select(f => f.FieldType));

        // A seal of one "request" stays bound to its own instance and organization; another request's copy of it is not its seal.
        var (service, _) = Service();
        var field = EncryptedField();
        var requestOne = await SealAsync(service, field, "request one");
        var requestTwoView = new FieldEncryptedValue
        {
            TenantId = requestOne.TenantId, FormRecordId = requestOne.FormRecordId, FieldDefinitionId = requestOne.FieldDefinitionId, FieldValueItemId = requestOne.FieldValueItemId, EncryptedLogicalType = requestOne.EncryptedLogicalType,
            EncryptedValueBytes = requestOne.EncryptedValueBytes.ToArray(), EncryptionNonceBytes = requestOne.EncryptionNonceBytes.ToArray(), EncryptionAuthTagBytes = requestOne.EncryptionAuthTagBytes.ToArray(),
            WrappedDataKeyBytes = requestOne.WrappedDataKeyBytes.ToArray(), EncryptionAlgorithmName = requestOne.EncryptionAlgorithmName, EncryptionKeyReference = requestOne.EncryptionKeyReference, EncryptionEnvelopeVersion = 1,
        };
        await Assert.ThrowsAsync<InvalidOperationException>(() => SelfSealedAsync(service, requestTwoView));
        Assert.Equal("request one", (await service.DecryptVerifiedAsync(Org, [WithSchema1(requestTwoView)], Ct))[0]); // as stored content it opens only on the verified path
    }

    private static FieldEncryptedValue WithSchema1(FieldEncryptedValue row)
    {
        row.RowSignatureSchemaVersion = 1;
        return row;
    }
}
