using System.Reflection;
using System.Security.Cryptography;
using EnterpriseForms.Application.Features.Documents.Ports;
using EnterpriseForms.Application.Features.FormRecords.CreateFormRecord;
using EnterpriseForms.Application.Features.FormRecords.Encryption;
using EnterpriseForms.Application.Features.Organizations.ReasonNotes;
using EnterpriseForms.Domain.Features.FormRecords;
using EnterpriseForms.Infrastructure.Features.Documents;
using EnterpriseForms.Infrastructure.Features.FormRecords;
using EnterpriseForms.Infrastructure.Features.Organizations;
using Microsoft.Extensions.Options;
using Xunit;

namespace EnterpriseForms.Api.ContractTests;

/// <summary>
/// E-1 key-provider interface v2 (E1-8): organization isolation, purpose isolation, current and historical version
/// resolution, unknown and disabled versions, legacy compatibility, cancellation propagation, missing-key fail-closed
/// behaviour, and that every consumer names its organization and purpose. The v1 vectors (E1-8.9) are in
/// <see cref="EncryptionKnownAnswerTests"/>; the API and Worker environment rule (E1-8.10) in
/// <see cref="EncryptionRemediationTests"/> and the architecture suite.
/// </summary>
public sealed class KeyProviderInterfaceV2Tests
{
    private const string LegacyReference = "local-dev-kek-v1";
    private static readonly Guid OrgA = Guid.Parse("019a5b2c-a000-7000-8000-00000000000a");
    private static readonly Guid OrgB = Guid.Parse("019a5b2c-b000-7000-8000-00000000000b");
    private static readonly byte[] Kek = RandomNumberGenerator.GetBytes(32);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static LocalDevelopmentKeyProvider Keys(Action<EncryptionOptions>? configure = null, byte[]? kek = null)
    {
        var options = new EncryptionOptions { KeyProvider = EncryptionOptions.LocalDevelopmentProviderName, LocalDevelopmentKekBase64 = Convert.ToBase64String(kek ?? Kek), LocalDevelopmentKeyReference = LegacyReference };
        configure?.Invoke(options);
        return new LocalDevelopmentKeyProvider(Options.Create(options));
    }

    private static DataKeyScope Scope(Guid organization, EncryptionKeyPurpose purpose) => new(organization, purpose);

    private static byte[] Dek() => RandomNumberGenerator.GetBytes(32);

    /// <summary>A legacy (pre-E-1) wrap, built exactly as the old provider did: AES-GCM under the KEK, AAD "DynamicForms/DekWrap/v1:" + reference.</summary>
    private static byte[] LegacyWrap(byte[] dek, byte[]? kek = null)
    {
        var nonce = RandomNumberGenerator.GetBytes(12);
        var cipher = new byte[dek.Length];
        var tag = new byte[16];
        using var aes = new AesGcm(kek ?? Kek, 16);
        aes.Encrypt(nonce, dek, cipher, tag, System.Text.Encoding.ASCII.GetBytes("DynamicForms/DekWrap/v1:" + LegacyReference));
        return [.. nonce, .. cipher, .. tag];
    }

    // ------------------------------------------------------------------ 1. Organization isolation

    [Fact]
    public async Task AKeyOfOneOrganizationCannotBeUsedForAnother()
    {
        var keys = Keys();
        var dek = Dek();
        var a = await keys.WrapDataKeyAsync(Scope(OrgA, EncryptionKeyPurpose.FieldValue), dek, Ct);
        var b = await keys.WrapDataKeyAsync(Scope(OrgB, EncryptionKeyPurpose.FieldValue), dek, Ct);
        Assert.Equal("localdev:" + OrgA.ToString("D") + ":field-value:1", a.KeyReference);
        Assert.Equal("localdev:" + OrgB.ToString("D") + ":field-value:1", b.KeyReference);

        // A's reference presented for B is refused before any key is used.
        var mismatch = await Assert.ThrowsAnyAsync<CryptographicException>(() => keys.UnwrapDataKeyAsync(Scope(OrgB, EncryptionKeyPurpose.FieldValue), a.KeyReference, a.WrappedKeyBytes, Ct));
        Assert.DoesNotContain(OrgA.ToString("D"), mismatch.Message, StringComparison.OrdinalIgnoreCase);
        // Relabelling A's wrapped key with B's reference does not open it: B's key is a different key, and the wrap AAD binds the organization.
        await Assert.ThrowsAnyAsync<CryptographicException>(() => keys.UnwrapDataKeyAsync(Scope(OrgB, EncryptionKeyPurpose.FieldValue), b.KeyReference, a.WrappedKeyBytes, Ct));
        Assert.Equal(dek, await keys.UnwrapDataKeyAsync(Scope(OrgA, EncryptionKeyPurpose.FieldValue), a.KeyReference, a.WrappedKeyBytes, Ct));

        // Same DEK, same wrap nonce, different organization: different key-encryption key, different wrapped bytes.
        var nonce = RandomNumberGenerator.GetBytes(12);
        var fixedA = await keys.WrapDataKeyAsync(Scope(OrgA, EncryptionKeyPurpose.FieldValue), dek, (byte[])nonce.Clone(), Ct);
        var fixedB = await keys.WrapDataKeyAsync(Scope(OrgB, EncryptionKeyPurpose.FieldValue), dek, (byte[])nonce.Clone(), Ct);
        Assert.NotEqual(fixedA.WrappedKeyBytes, fixedB.WrappedKeyBytes);
    }

    [Fact]
    public async Task AFieldValueOrDocumentOfOneOrganizationDoesNotOpenUnderAnother()
    {
        var keys = Keys();
        var fields = new AesGcmFieldEncryptionService(keys);
        var sealedRow = await fields.EncryptAsync(new FieldEncryptionContext(OrgA, Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), "String"), "Avery", EncryptionKeyPurpose.FieldValue, Ct);
        var moved = Clone(sealedRow);
        moved.TenantId = OrgB;
        var thrown = await Assert.ThrowsAnyAsync<CryptographicException>(() => fields.DecryptAsync(moved, EncryptionKeyPurpose.FieldValue, Ct));
        Assert.Equal(DecryptionFailureReasons.KeyMismatch, DecryptionFailure.Find(thrown)!.ReasonCode);

        var documents = new AesGcmFileEncryptionService(keys);
        var context = new FileEncryptionContext(OrgA, Guid.CreateVersion7(), Guid.CreateVersion7(), 1);
        using var sink = new MemoryStream();
        var outcome = await documents.EncryptAsync(new MemoryStream([1, 2, 3]), sink, context, Ct);
        await Assert.ThrowsAnyAsync<CryptographicException>(() => documents.DecryptAsync(new MemoryStream(sink.ToArray()), new MemoryStream(), outcome.Envelope, context with { TenantId = OrgB }, Ct));
    }

    [Fact]
    public void EveryKeyOperationNamesAnOrganization()
    {
        Assert.Throws<ArgumentException>(() => new DataKeyScope(Guid.Empty, EncryptionKeyPurpose.FieldValue));
        Assert.Throws<ArgumentNullException>(() => new DataKeyScope(OrgA, null!));
        var operations = typeof(IEncryptionKeyProvider).GetMethods().Where(m => !m.IsSpecialName).ToList();
        Assert.Equal(["UnwrapDataKeyAsync", "WrapDataKeyAsync"], operations.Select(m => m.Name).Order(StringComparer.Ordinal).ToArray());
        Assert.All(operations, m => Assert.Equal(typeof(DataKeyScope), m.GetParameters()[0].ParameterType));
        Assert.All(operations, m => Assert.Equal(typeof(CancellationToken), m.GetParameters()[^1].ParameterType));
        Assert.All(operations, m => Assert.True(typeof(Task).IsAssignableFrom(m.ReturnType)));
    }

    // ------------------------------------------------------------------ 2. Purpose isolation

    [Fact]
    public async Task AKeyOfOnePurposeCannotBeUsedForAnother()
    {
        var keys = Keys();
        var dek = Dek();
        var nonce = RandomNumberGenerator.GetBytes(12);
        var wrapped = new Dictionary<EncryptionKeyPurpose, WrappedDataKey>();
        foreach (var purpose in EncryptionKeyPurpose.All)
        {
            wrapped[purpose] = await keys.WrapDataKeyAsync(Scope(OrgA, purpose), dek, (byte[])nonce.Clone(), Ct);
        }

        Assert.Equal(4, wrapped.Values.Select(w => w.KeyReference).Distinct().Count());
        Assert.Equal(4, wrapped.Values.Select(w => Convert.ToHexString(w.WrappedKeyBytes)).Distinct().Count()); // distinct keys per purpose
        foreach (var (purpose, key) in wrapped)
        {
            foreach (var other in EncryptionKeyPurpose.All.Where(p => p != purpose))
            {
                // Presented for another purpose: refused; relabelled with that purpose's reference: does not open.
                await Assert.ThrowsAnyAsync<CryptographicException>(() => keys.UnwrapDataKeyAsync(Scope(OrgA, other), key.KeyReference, key.WrappedKeyBytes, Ct));
                await Assert.ThrowsAnyAsync<CryptographicException>(() => keys.UnwrapDataKeyAsync(Scope(OrgA, other), wrapped[other].KeyReference, key.WrappedKeyBytes, Ct));
            }

            Assert.Equal(dek, await keys.UnwrapDataKeyAsync(Scope(OrgA, purpose), key.KeyReference, key.WrappedKeyBytes, Ct));
        }
    }

    [Fact]
    public async Task PurposesAreAClosedStronglyTypedSetAndServicesRefuseForeignPurposes()
    {
        Assert.Empty(typeof(EncryptionKeyPurpose).GetConstructors(BindingFlags.Public | BindingFlags.Instance));
        Assert.Equal(["AuditorReasonNote", "Document", "FieldValue", "IdempotencyProtection"], EncryptionKeyPurpose.All.Select(p => p.Name).Order(StringComparer.Ordinal).ToArray());

        var fields = new AesGcmFieldEncryptionService(Keys());
        var context = new FieldEncryptionContext(OrgA, Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), "String");
        await Assert.ThrowsAsync<ArgumentException>(() => fields.EncryptAsync(context, "x", EncryptionKeyPurpose.Document, Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => fields.EncryptAsync(context, "x", EncryptionKeyPurpose.AuditorReasonNote, Ct));

        // A value sealed for idempotency protection is not a field value, and vice versa.
        var fingerprintKey = await fields.EncryptAsync(context, "secret", EncryptionKeyPurpose.IdempotencyProtection, Ct);
        await Assert.ThrowsAnyAsync<CryptographicException>(() => fields.DecryptAsync(fingerprintKey, EncryptionKeyPurpose.FieldValue, Ct));
        var fieldValue = await fields.EncryptAsync(context, "secret", EncryptionKeyPurpose.FieldValue, Ct);
        await Assert.ThrowsAnyAsync<CryptographicException>(() => fields.DecryptAsync(fieldValue, EncryptionKeyPurpose.IdempotencyProtection, Ct));
        Assert.Equal("secret", await fields.DecryptAsync(fingerprintKey, EncryptionKeyPurpose.IdempotencyProtection, Ct));
    }

    // ------------------------------------------------------------------ 3./4. Current and historical versions

    [Fact]
    public async Task NewDataUsesTheCurrentVersionOfItsOrganizationAndPurpose()
    {
        var keys = Keys(o =>
        {
            o.LocalDevelopmentKeyring.Purposes["FieldValue"] = new LocalKeyVersionOptions { ProvisionedVersions = [1, 2, 3], CurrentVersion = 3, RetainedVersions = [1, 2] };
            o.LocalDevelopmentKeyring.Organizations[OrgB.ToString("D")] = new() { ["FieldValue"] = new LocalKeyVersionOptions { ProvisionedVersions = [5], CurrentVersion = 5 } };
        });
        Assert.EndsWith(":field-value:3", (await keys.WrapDataKeyAsync(Scope(OrgA, EncryptionKeyPurpose.FieldValue), Dek(), Ct)).KeyReference, StringComparison.Ordinal);
        Assert.EndsWith(":field-value:5", (await keys.WrapDataKeyAsync(Scope(OrgB, EncryptionKeyPurpose.FieldValue), Dek(), Ct)).KeyReference, StringComparison.Ordinal);
        Assert.EndsWith(":document:1", (await keys.WrapDataKeyAsync(Scope(OrgA, EncryptionKeyPurpose.Document), Dek(), Ct)).KeyReference, StringComparison.Ordinal); // default table
        Assert.Equal((3, 5), (keys.CurrentVersion(Scope(OrgA, EncryptionKeyPurpose.FieldValue)), keys.CurrentVersion(Scope(OrgB, EncryptionKeyPurpose.FieldValue))));

        // The field service stores the reference of the version it used.
        var sealedRow = await new AesGcmFieldEncryptionService(keys).EncryptAsync(new FieldEncryptionContext(OrgA, Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), "String"), "v3", EncryptionKeyPurpose.FieldValue, Ct);
        Assert.Equal("localdev:" + OrgA.ToString("D") + ":field-value:3", sealedRow.EncryptionKeyReference);
    }

    [Fact]
    public async Task ExplicitlyRetainedVersionsStillDecryptAfterANewVersionBecomesCurrent()
    {
        var context = new FieldEncryptionContext(OrgA, Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), "String");
        var v1 = await new AesGcmFieldEncryptionService(Keys()).EncryptAsync(context, "written under v1", EncryptionKeyPurpose.FieldValue, Ct);
        Assert.EndsWith(":field-value:1", v1.EncryptionKeyReference, StringComparison.Ordinal);

        var later = new AesGcmFieldEncryptionService(Keys(o => o.LocalDevelopmentKeyring.Purposes["FieldValue"] = new LocalKeyVersionOptions { ProvisionedVersions = [1, 2], CurrentVersion = 2, RetainedVersions = [1] }));
        Assert.Equal("written under v1", await later.DecryptAsync(v1, EncryptionKeyPurpose.FieldValue, Ct)); // historical version, not rewritten
        var v2 = await later.EncryptAsync(context, "written under v2", EncryptionKeyPurpose.FieldValue, Ct);
        Assert.EndsWith(":field-value:2", v2.EncryptionKeyReference, StringComparison.Ordinal);
        Assert.Equal("written under v2", await later.DecryptAsync(v2, EncryptionKeyPurpose.FieldValue, Ct));
    }

    // ------------------------------------------------------------------ 5. Unknown and disabled versions

    [Fact]
    public async Task UnknownAndDisabledVersionsFailClosedWithoutFallback()
    {
        var dek = Dek();
        var v1 = await Keys().WrapDataKeyAsync(Scope(OrgA, EncryptionKeyPurpose.Document), dek, Ct);
        var scope = Scope(OrgA, EncryptionKeyPurpose.Document);

        // v1 no longer retained: unknown. The current v2 key is never tried in its place.
        var notRetained = Keys(o => o.LocalDevelopmentKeyring.Purposes["Document"] = new LocalKeyVersionOptions { ProvisionedVersions = [1, 2], CurrentVersion = 2 });
        var unknown = await Assert.ThrowsAsync<EncryptionUnavailableException>(() => notRetained.UnwrapDataKeyAsync(scope, v1.KeyReference, v1.WrappedKeyBytes, Ct));
        Assert.Equal("the key version is not retained", unknown.Message);
        var disabled = Keys(o => o.LocalDevelopmentKeyring.Purposes["Document"] = new LocalKeyVersionOptions { ProvisionedVersions = [1, 2], CurrentVersion = 2, DisabledVersions = [1] });
        Assert.Equal("the key version is disabled", (await Assert.ThrowsAsync<EncryptionUnavailableException>(() => disabled.UnwrapDataKeyAsync(scope, v1.KeyReference, v1.WrappedKeyBytes, Ct))).Message);
        // A version above the current one is unknown too (no key is derived or generated on demand).
        await Assert.ThrowsAsync<EncryptionUnavailableException>(() => Keys().UnwrapDataKeyAsync(scope, "localdev:" + OrgA.ToString("D") + ":document:7", v1.WrappedKeyBytes, Ct));
        Assert.Equal(1, Keys().CurrentVersion(scope));

        // Non-canonical or malformed references are not references.
        foreach (var malformed in new[]
                 {
                     "localdev:" + OrgA.ToString("D").ToUpperInvariant() + ":document:1", "localdev:" + OrgA.ToString("D") + ":document:01", "localdev:" + OrgA.ToString("D") + ":document:0",
                     "localdev:" + OrgA.ToString("D") + ":document:1:extra", "localdev:" + OrgA.ToString("N") + ":document:1", "localdev:" + OrgA.ToString("D") + ":Document:1",
                     "localdev:" + OrgA.ToString("D") + ":unknown-purpose:1", "localdev:" + Guid.Empty.ToString("D") + ":document:1", "localdev:" + OrgA.ToString("D") + ": document:1", "rotated-kek-v2", "",
                 })
        {
            Assert.False(LocalDevelopmentKeyProvider.TryParseScopedReference(malformed, out _, out _, out _), malformed);
            await Assert.ThrowsAsync<EncryptionUnavailableException>(() => Keys().UnwrapDataKeyAsync(scope, malformed, v1.WrappedKeyBytes, Ct));
        }

        Assert.Equal(dek, await Keys().UnwrapDataKeyAsync(scope, v1.KeyReference, v1.WrappedKeyBytes, Ct));
    }

    [Fact]
    public async Task AnInvalidKeyVersionTableFailsClosedForEveryOperation()
    {
        var invalid = new Action<EncryptionOptions>[]
        {
            o => o.LocalDevelopmentKeyring.Purposes["FieldValue"] = new LocalKeyVersionOptions { CurrentVersion = 2, DisabledVersions = [2] },
            o => o.LocalDevelopmentKeyring.Purposes["FieldValue"] = new LocalKeyVersionOptions { CurrentVersion = 3, RetainedVersions = [1], DisabledVersions = [1] },
            o => o.LocalDevelopmentKeyring.Purposes["FieldValue"] = new LocalKeyVersionOptions { CurrentVersion = 0 },
            o => o.LocalDevelopmentKeyring.Purposes["Signing"] = new LocalKeyVersionOptions(),
            o => o.LocalDevelopmentKeyring.Organizations["not-an-organization"] = new() { ["FieldValue"] = new LocalKeyVersionOptions() },
            o => o.LocalDevelopmentKeyring.RootKeyBase64 = Convert.ToBase64String(new byte[16]),
            o => o.LocalDevelopmentKeyring.RootKeyBase64 = "not base64!",
            o => o.LocalDevelopmentKeyReference = "localdev:" + OrgA.ToString("D") + ":field-value:1", // the legacy name cannot impersonate a scoped reference
        };
        foreach (var configure in invalid)
        {
            var keys = Keys(configure);
            Assert.False(keys.IsConfigured);
            var refused = await Assert.ThrowsAsync<EncryptionUnavailableException>(() => keys.WrapDataKeyAsync(Scope(OrgA, EncryptionKeyPurpose.FieldValue), Dek(), Ct));
            Assert.DoesNotContain(Convert.ToBase64String(Kek), refused.Message, StringComparison.Ordinal);
            await Assert.ThrowsAsync<EncryptionUnavailableException>(() => keys.UnwrapDataKeyAsync(Scope(OrgA, EncryptionKeyPurpose.FieldValue), LegacyReference, LegacyWrap(Dek()), Ct));
        }
    }

    // ------------------------------------------------------------------ 6. Legacy compatibility

    [Fact]
    public async Task LegacyDataOpensOnlyThroughTheConfiguredLegacyKeyAndNewDataNeverUsesIt()
    {
        var dek = Dek();
        var legacy = LegacyWrap(dek);
        var keys = Keys();
        foreach (var purpose in EncryptionKeyPurpose.All)
        {
            Assert.Equal(dek, await keys.UnwrapDataKeyAsync(Scope(OrgA, purpose), LegacyReference, legacy, Ct)); // all four pre-E-1 uses
        }

        // New wraps never produce the legacy reference.
        foreach (var purpose in EncryptionKeyPurpose.All)
        {
            Assert.StartsWith("localdev:", (await keys.WrapDataKeyAsync(Scope(OrgA, purpose), dek, Ct)).KeyReference, StringComparison.Ordinal);
        }

        // The legacy reference resolves only to the legacy KEK: another legacy KEK, a scoped wrap relabelled as legacy, and a
        // legacy wrap relabelled as scoped do not open; with legacy decryption disabled or no legacy KEK, it is unavailable.
        await Assert.ThrowsAnyAsync<CryptographicException>(() => Keys(kek: RandomNumberGenerator.GetBytes(32)).UnwrapDataKeyAsync(Scope(OrgA, EncryptionKeyPurpose.FieldValue), LegacyReference, legacy, Ct));
        var scoped = await keys.WrapDataKeyAsync(Scope(OrgA, EncryptionKeyPurpose.FieldValue), dek, Ct);
        await Assert.ThrowsAnyAsync<CryptographicException>(() => keys.UnwrapDataKeyAsync(Scope(OrgA, EncryptionKeyPurpose.FieldValue), LegacyReference, scoped.WrappedKeyBytes, Ct));
        await Assert.ThrowsAnyAsync<CryptographicException>(() => keys.UnwrapDataKeyAsync(Scope(OrgA, EncryptionKeyPurpose.FieldValue), scoped.KeyReference, legacy, Ct));
        await Assert.ThrowsAsync<EncryptionUnavailableException>(() => Keys(o => o.LocalDevelopmentLegacyKeyDecryptionEnabled = false).UnwrapDataKeyAsync(Scope(OrgA, EncryptionKeyPurpose.FieldValue), LegacyReference, legacy, Ct));
        var rootOnly = Keys(o =>
        {
            o.LocalDevelopmentKekBase64 = null;
            o.LocalDevelopmentKeyring.RootKeyBase64 = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        });
        Assert.True(rootOnly.IsConfigured);
        await Assert.ThrowsAsync<EncryptionUnavailableException>(() => rootOnly.UnwrapDataKeyAsync(Scope(OrgA, EncryptionKeyPurpose.FieldValue), LegacyReference, legacy, Ct));

        // A separate keyring root gives different scoped keys than the legacy-derived root (the legacy key itself is unchanged).
        var separateRoot = Keys(o => o.LocalDevelopmentKeyring.RootKeyBase64 = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));
        await Assert.ThrowsAnyAsync<CryptographicException>(() => separateRoot.UnwrapDataKeyAsync(Scope(OrgA, EncryptionKeyPurpose.FieldValue), scoped.KeyReference, scoped.WrappedKeyBytes, Ct));
        Assert.Equal(dek, await separateRoot.UnwrapDataKeyAsync(Scope(OrgA, EncryptionKeyPurpose.FieldValue), LegacyReference, legacy, Ct));
    }

    [Fact]
    public async Task ExistingLegacyIdempotencyAttemptsStillMatch()
    {
        // An attempt sealed before E-1: the fingerprint key under the legacy reference (built with the pre-E-1 construction).
        var tenant = OrgA;
        var record = Guid.CreateVersion7();
        var canonical = CreationRequestFingerprint.Canonicalize(new CreationRequestFingerprint.Request("https://issuer.test", "alice", Guid.CreateVersion7(), null, null, null, new Dictionary<string, string> { ["given_name"] = "Avery" }, null));
        var fields = new AesGcmFieldEncryptionService(Keys());
        var attempt = await CreationRequestFingerprint.CreateAsync(fields, tenant, record, Guid.CreateVersion7(), DateTimeOffset.UtcNow, canonical, Ct);
        Assert.Contains(":idempotency-protection:1", attempt.FingerprintKeyEnvelopeJson, StringComparison.Ordinal);
        Assert.True(await CreationRequestFingerprint.MatchesAsync(fields, attempt, canonical, Ct));

        var key = RandomNumberGenerator.GetBytes(32);
        var dek = Dek();
        var nonce = RandomNumberGenerator.GetBytes(12);
        var plaintext = System.Text.Encoding.UTF8.GetBytes(Convert.ToBase64String(key));
        var cipher = new byte[plaintext.Length];
        var tag = new byte[16];
        using (var aes = new AesGcm(dek, 16))
        {
            aes.Encrypt(nonce, plaintext, cipher, tag, AesGcmFieldEncryptionService.Aad(new FieldEncryptionContext(tenant, record, CreationRequestFingerprint.SealPurposeId, attempt.FormRecordCreationAttemptId, CreationRequestFingerprint.SealLogicalType)));
        }

        var legacyEnvelope = System.Text.Json.JsonSerializer.Serialize(new { Ciphertext = Convert.ToBase64String(cipher), Nonce = Convert.ToBase64String(nonce), Tag = Convert.ToBase64String(tag), WrappedKey = Convert.ToBase64String(LegacyWrap(dek)), Algorithm = "AES-256-GCM", KeyReference = LegacyReference, EnvelopeVersion = 1 });
        var legacyAttempt = new FormRecordCreationAttempt { FormRecordCreationAttemptId = attempt.FormRecordCreationAttemptId, TenantId = tenant, FormRecordId = record, CreationRequestFingerprint = HMACSHA256.HashData(key, canonical), FingerprintKeyEnvelopeJson = legacyEnvelope, CreatedOn = attempt.CreatedOn, CreatedByUserId = attempt.CreatedByUserId };
        Assert.True(await CreationRequestFingerprint.MatchesAsync(fields, legacyAttempt, canonical, Ct));
    }

    [Fact]
    public async Task ANewlySealedReasonNoteWithAScopedReferencePassesTheAuthoritysEnvelopeValidation()
    {
        // The envelope format is unchanged; only the key reference value differs (it fits the Authority's key-reference rule).
        var noteId = Guid.CreateVersion7();
        var sealedNote = await new AesGcmAuditorNoteSealer(Keys()).SealAsync(new AuditorReasonNoteContext(OrgA, Guid.CreateVersion7(), AuditorReasonNoteFormat.RoleName, AuditorReasonNoteFormat.Appoint, noteId), "Covering the Q4 review", Ct);
        Assert.Equal("localdev:" + OrgA.ToString("D") + ":auditor-reason-note:1", sealedNote.KeyReference);
        var asReceived = new AuthorizationAuthority.Contracts.V1.AuditorReasonNoteEnvelope(sealedNote.EnvelopeFormat, sealedNote.EnvelopeVersion, sealedNote.AlgorithmName, sealedNote.KeyReference, sealedNote.NoteId, sealedNote.NonceB64, sealedNote.CiphertextB64, sealedNote.TagB64, sealedNote.WrappedDataKeyB64);
        Assert.Equal(noteId, AuthorizationAuthority.Application.Features.SecurityCommands.AuditorReasonNoteEnvelopeValidation.Validate(asReceived));
    }

    // ------------------------------------------------------------------ 7. Cancellation propagation (and E1-7: every consumer names its organization and purpose)

    /// <summary>Records every key operation's scope and token, then delegates.</summary>
    private sealed class SpyKeys(IEncryptionKeyProvider inner) : IEncryptionKeyProvider
    {
        public List<(string Operation, DataKeyScope Scope, CancellationToken Token)> Calls { get; } = [];

        public string ProviderName => inner.ProviderName;

        public bool IsConfigured => inner.IsConfigured;

        public Task<WrappedDataKey> WrapDataKeyAsync(DataKeyScope scope, ReadOnlyMemory<byte> dataKey, CancellationToken cancellationToken)
        {
            Calls.Add(("wrap", scope, cancellationToken));
            return inner.WrapDataKeyAsync(scope, dataKey, cancellationToken);
        }

        public Task<byte[]> UnwrapDataKeyAsync(DataKeyScope scope, string keyReference, ReadOnlyMemory<byte> wrappedDataKey, CancellationToken cancellationToken)
        {
            Calls.Add(("unwrap", scope, cancellationToken));
            return inner.UnwrapDataKeyAsync(scope, keyReference, wrappedDataKey, cancellationToken);
        }
    }

    [Fact]
    public async Task EveryConsumerPassesItsOrganizationPurposeAndCancellationToken()
    {
        using var source = new CancellationTokenSource();
        var token = source.Token;
        var spy = new SpyKeys(Keys());
        var record = Guid.CreateVersion7();

        var fields = new AesGcmFieldEncryptionService(spy);
        var sealedRow = await fields.EncryptAsync(new FieldEncryptionContext(OrgA, record, Guid.CreateVersion7(), Guid.CreateVersion7(), "String"), "x", EncryptionKeyPurpose.FieldValue, token);
        await fields.DecryptAsync(sealedRow, EncryptionKeyPurpose.FieldValue, token);

        var documents = new AesGcmFileEncryptionService(spy);
        var documentContext = new FileEncryptionContext(OrgB, record, Guid.CreateVersion7(), 1);
        using var sink = new MemoryStream();
        var outcome = await documents.EncryptAsync(new MemoryStream([9, 9]), sink, documentContext, token);
        await documents.DecryptAsync(new MemoryStream(sink.ToArray()), new MemoryStream(), outcome.Envelope, documentContext, token);

        await new AesGcmAuditorNoteSealer(spy).SealAsync(new AuditorReasonNoteContext(OrgA, Guid.CreateVersion7(), AuditorReasonNoteFormat.RoleName, AuditorReasonNoteFormat.Appoint, Guid.CreateVersion7()), "note", token);

        var canonical = new byte[] { 1 };
        var attempt = await CreationRequestFingerprint.CreateAsync(fields, OrgB, record, Guid.CreateVersion7(), DateTimeOffset.UtcNow, canonical, token);
        Assert.True(await CreationRequestFingerprint.MatchesAsync(fields, attempt, canonical, token));

        Assert.Equal(
            [
                ("wrap", OrgA, "FieldValue"), ("unwrap", OrgA, "FieldValue"),
                ("wrap", OrgB, "Document"), ("unwrap", OrgB, "Document"),
                ("wrap", OrgA, "AuditorReasonNote"),
                ("wrap", OrgB, "IdempotencyProtection"), ("unwrap", OrgB, "IdempotencyProtection"),
            ],
            spy.Calls.Select(c => (c.Operation, c.Scope.OrganizationId, c.Scope.Purpose.Name)).ToList());
        Assert.All(spy.Calls, c => Assert.Equal(token, c.Token));
    }

    [Fact]
    public async Task ACancelledOperationStopsBeforeAnyKeyIsUsedAndIsNotADecryptionFailure()
    {
        var keys = Keys();
        var fields = new AesGcmFieldEncryptionService(keys);
        var context = new FieldEncryptionContext(OrgA, Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), "String");
        var sealedRow = await fields.EncryptAsync(context, "x", EncryptionKeyPurpose.FieldValue, Ct);
        var documents = new AesGcmFileEncryptionService(keys);
        var documentContext = new FileEncryptionContext(OrgA, Guid.CreateVersion7(), Guid.CreateVersion7(), 1);
        using var sink = new MemoryStream();
        var outcome = await documents.EncryptAsync(new MemoryStream([1]), sink, documentContext, Ct);

        using var source = new CancellationTokenSource();
        await source.CancelAsync();
        var cancelled = source.Token;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => keys.WrapDataKeyAsync(Scope(OrgA, EncryptionKeyPurpose.FieldValue), Dek(), cancelled));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => keys.UnwrapDataKeyAsync(Scope(OrgA, EncryptionKeyPurpose.FieldValue), sealedRow.EncryptionKeyReference, sealedRow.WrappedDataKeyBytes, cancelled));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fields.EncryptAsync(context, "x", EncryptionKeyPurpose.FieldValue, cancelled));
        var decryptCancelled = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fields.DecryptAsync(sealedRow, EncryptionKeyPurpose.FieldValue, cancelled));
        Assert.Null(DecryptionFailure.Find(decryptCancelled));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => documents.EncryptAsync(new MemoryStream([1]), new MemoryStream(), documentContext, cancelled));
        var documentCancelled = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => documents.DecryptAsync(new MemoryStream(sink.ToArray()), new MemoryStream(), outcome.Envelope, documentContext, cancelled));
        Assert.Null(DecryptionFailure.Find(documentCancelled));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new AesGcmAuditorNoteSealer(keys).SealAsync(new AuditorReasonNoteContext(OrgA, Guid.CreateVersion7(), AuditorReasonNoteFormat.RoleName, AuditorReasonNoteFormat.Appoint, Guid.CreateVersion7()), "note", cancelled));
    }

    // ------------------------------------------------------------------ 8. Missing keys fail closed

    [Fact]
    public async Task WithoutKeyMaterialEveryOperationFailsClosedAndNothingIsGenerated()
    {
        var none = Keys(o => o.LocalDevelopmentKekBase64 = null);
        Assert.False(none.IsConfigured);
        await Assert.ThrowsAsync<EncryptionUnavailableException>(() => none.WrapDataKeyAsync(Scope(OrgA, EncryptionKeyPurpose.FieldValue), Dek(), Ct));
        await Assert.ThrowsAsync<EncryptionUnavailableException>(() => none.UnwrapDataKeyAsync(Scope(OrgA, EncryptionKeyPurpose.FieldValue), LegacyReference, LegacyWrap(Dek()), Ct));
        await Assert.ThrowsAsync<EncryptionUnavailableException>(() => none.UnwrapDataKeyAsync(Scope(OrgA, EncryptionKeyPurpose.FieldValue), "localdev:" + OrgA.ToString("D") + ":field-value:1", LegacyWrap(Dek()), Ct));
        var fields = new AesGcmFieldEncryptionService(none);
        Assert.False(fields.IsAvailable);
        await Assert.ThrowsAsync<EncryptionUnavailableException>(() => fields.EncryptAsync(new FieldEncryptionContext(OrgA, Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), "String"), "x", EncryptionKeyPurpose.FieldValue, Ct));
        Assert.False(new AesGcmAuditorNoteSealer(none).IsAvailable);

        var unavailable = new UnavailableKeyProvider("test: no provider");
        await Assert.ThrowsAsync<EncryptionUnavailableException>(() => unavailable.WrapDataKeyAsync(Scope(OrgA, EncryptionKeyPurpose.Document), Dek(), Ct));
        await Assert.ThrowsAsync<EncryptionUnavailableException>(() => unavailable.UnwrapDataKeyAsync(Scope(OrgA, EncryptionKeyPurpose.Document), LegacyReference, LegacyWrap(Dek()), Ct));

        // Unwrap failures never carry key material.
        var thrown = await Assert.ThrowsAnyAsync<CryptographicException>(() => Keys().UnwrapDataKeyAsync(Scope(OrgA, EncryptionKeyPurpose.FieldValue), LegacyReference, LegacyWrap(Dek(), RandomNumberGenerator.GetBytes(32)), Ct));
        Assert.DoesNotContain(Convert.ToBase64String(Kek), thrown.ToString(), StringComparison.Ordinal);
    }

    private static FieldEncryptedValue Clone(FieldEncryptedValue v) => new()
    {
        FieldEncryptedValueId = v.FieldEncryptedValueId, TenantId = v.TenantId, FormRecordId = v.FormRecordId, FieldDefinitionId = v.FieldDefinitionId, FieldValueItemId = v.FieldValueItemId,
        EncryptedValueBytes = [.. v.EncryptedValueBytes], EncryptionNonceBytes = [.. v.EncryptionNonceBytes], EncryptionAuthTagBytes = [.. v.EncryptionAuthTagBytes], WrappedDataKeyBytes = [.. v.WrappedDataKeyBytes],
        EncryptionAlgorithmName = v.EncryptionAlgorithmName, EncryptionKeyReference = v.EncryptionKeyReference, EncryptionEnvelopeVersion = v.EncryptionEnvelopeVersion, EncryptedLogicalType = v.EncryptedLogicalType,
    };
}
