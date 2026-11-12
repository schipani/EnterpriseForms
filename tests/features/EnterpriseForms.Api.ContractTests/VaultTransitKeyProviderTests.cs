using System.Net;
using System.Security.Cryptography;
using EnterpriseForms.Application.Features.Documents.Ports;
using EnterpriseForms.Application.Features.FormRecords.Encryption;
using EnterpriseForms.Application.Features.Organizations.ReasonNotes;
using EnterpriseForms.Domain.Features.FormRecords;
using EnterpriseForms.Infrastructure;
using EnterpriseForms.Infrastructure.Features.Documents;
using EnterpriseForms.Infrastructure.Features.FormRecords;
using EnterpriseForms.Infrastructure.Features.Organizations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace EnterpriseForms.Api.ContractTests;

/// <summary>
/// E-2 Vault Transit key provider (E2-7, items 1-12) against the deterministic <see cref="FakeVaultTransit"/>. The same behaviours
/// against a real Vault are in <see cref="VaultTransitDockerIntegrationTests"/>.
/// </summary>
public sealed class VaultTransitKeyProviderTests
{
    private const string Mount = "enterpriseforms-encryption";
    private static readonly Guid OrgA = Guid.Parse("019a5b2c-a000-7000-8000-00000000000a");
    private static readonly Guid OrgB = Guid.Parse("019a5b2c-b000-7000-8000-00000000000b");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static DataKeyScope Scope(Guid organization, EncryptionKeyPurpose purpose) => new(organization, purpose);

    private static byte[] Dek() => RandomNumberGenerator.GetBytes(32);

    private static string KeyName(Guid organization, EncryptionKeyPurpose purpose) => "ef-enc-" + organization.ToString("N") + "-" + purpose.Token;

    /// <summary>A fake Vault with every organization/purpose key of OrgA and OrgB provisioned at <paramref name="versions"/> versions.</summary>
    private static FakeVaultTransit Vault(int versions = 1)
    {
        var vault = new FakeVaultTransit(Mount);
        foreach (var organization in new[] { OrgA, OrgB })
        {
            foreach (var purpose in EncryptionKeyPurpose.All)
            {
                vault.Provision(KeyName(organization, purpose), versions);
            }
        }

        return vault;
    }

    private static EncryptionOptions Settings(Action<VaultTransitEncryptionOptions>? configure = null)
    {
        var options = new EncryptionOptions
        {
            KeyProvider = EncryptionOptions.VaultTransitProviderName,
            VaultTransit = new VaultTransitEncryptionOptions { Address = "http://vault.test:8200", Token = FakeVaultTransit.BusinessToken, MountPath = Mount, TimeoutSeconds = 2 },
        };
        configure?.Invoke(options.VaultTransit);
        return options;
    }

    private static VaultTransitKeyProvider Keys(FakeVaultTransit vault, Action<VaultTransitEncryptionOptions>? configure = null) =>
        new(vault.Factory(), Options.Create(Settings(configure)));

    // ------------------------------------------------------------------ 1. Successful wrap and unwrap

    [Fact]
    public async Task AWrappedDataKeyRoundTripsThroughVaultUnderTheScopesOwnKey()
    {
        var vault = Vault();
        var keys = Keys(vault);
        var dek = Dek();
        var scope = Scope(OrgA, EncryptionKeyPurpose.FieldValue);

        var wrapped = await keys.WrapDataKeyAsync(scope, dek, Ct);
        Assert.Equal("vault-transit:" + OrgA.ToString("D") + ":field-value:1", wrapped.KeyReference);
        Assert.Equal(60, wrapped.WrappedKeyBytes.Length); // nonce(12) ‖ ciphertext(32) ‖ tag(16), the stored layout of every provider
        Assert.NotEqual(dek, wrapped.WrappedKeyBytes[12..44]); // the key is sealed, not carried

        var encrypt = vault.Requests.Single();
        Assert.Equal("/v1/" + Mount + "/encrypt/" + KeyName(OrgA, EncryptionKeyPurpose.FieldValue), encrypt.Path);
        Assert.Equal(FakeVaultTransit.BusinessToken, encrypt.Token);
        Assert.Equal(1, encrypt.Body.GetProperty("key_version").GetInt32());
        Assert.Equal(VaultTransitKeyProvider.WrapAad(scope, 1), Convert.FromBase64String(encrypt.Body.GetProperty("associated_data").GetString()!));
        Assert.Equal(["associated_data", "key_version", "plaintext"], encrypt.Body.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal).ToArray()); // no type: never a key creation request

        Assert.Equal(dek, await keys.UnwrapDataKeyAsync(scope, wrapped.KeyReference, wrapped.WrappedKeyBytes, Ct));
        var decrypt = vault.Requests[1];
        Assert.Equal("/v1/" + Mount + "/decrypt/" + KeyName(OrgA, EncryptionKeyPurpose.FieldValue), decrypt.Path);
        Assert.Equal("vault:v1:" + Convert.ToBase64String(wrapped.WrappedKeyBytes), decrypt.Body.GetProperty("ciphertext").GetString());

        Assert.True(keys.IsConfigured);
        Assert.Equal("VaultTransit", keys.ProviderName);
    }

    // ------------------------------------------------------------------ 2. Organization isolation

    [Fact]
    public async Task AKeyOfOneOrganizationCannotBeUsedForAnother()
    {
        var vault = Vault();
        var keys = Keys(vault);
        var dek = Dek();
        var a = await keys.WrapDataKeyAsync(Scope(OrgA, EncryptionKeyPurpose.FieldValue), dek, Ct);
        var b = await keys.WrapDataKeyAsync(Scope(OrgB, EncryptionKeyPurpose.FieldValue), dek, Ct);
        Assert.NotEqual(vault.Requests[0].Path, vault.Requests[1].Path); // each organization has its own Vault key

        // A's reference presented for B is refused before Vault is called.
        var before = vault.Requests.Count;
        var mismatch = await Assert.ThrowsAnyAsync<CryptographicException>(() => keys.UnwrapDataKeyAsync(Scope(OrgB, EncryptionKeyPurpose.FieldValue), a.KeyReference, a.WrappedKeyBytes, Ct));
        Assert.Equal(before, vault.Requests.Count);
        Assert.DoesNotContain(OrgA.ToString("D"), mismatch.Message, StringComparison.OrdinalIgnoreCase);

        // A's wrapped key relabelled with B's reference reaches B's key and context, and does not authenticate.
        await Assert.ThrowsAnyAsync<CryptographicException>(() => keys.UnwrapDataKeyAsync(Scope(OrgB, EncryptionKeyPurpose.FieldValue), b.KeyReference, a.WrappedKeyBytes, Ct));
        Assert.Equal(dek, await keys.UnwrapDataKeyAsync(Scope(OrgA, EncryptionKeyPurpose.FieldValue), a.KeyReference, a.WrappedKeyBytes, Ct));
    }

    [Fact]
    public async Task TheWrapContextBindsTheOrganizationEvenWhenTwoScopesReachTheSameVaultKey()
    {
        // Defence in depth: even if an operator error made two organizations share one Vault key, the associated data still separates them.
        var vault = new FakeVaultTransit(Mount);
        vault.Provision(KeyName(OrgA, EncryptionKeyPurpose.FieldValue));
        vault.Alias(KeyName(OrgB, EncryptionKeyPurpose.FieldValue), KeyName(OrgA, EncryptionKeyPurpose.FieldValue));
        var keys = Keys(vault);
        var a = await keys.WrapDataKeyAsync(Scope(OrgA, EncryptionKeyPurpose.FieldValue), Dek(), Ct);
        var forged = Scope(OrgB, EncryptionKeyPurpose.FieldValue);
        await Assert.ThrowsAnyAsync<CryptographicException>(() => keys.UnwrapDataKeyAsync(forged, VaultTransitKeyProvider.Reference(forged, 1), a.WrappedKeyBytes, Ct));
    }

    // ------------------------------------------------------------------ 3. Purpose isolation

    [Fact]
    public async Task AKeyOfOnePurposeCannotBeUsedForAnother()
    {
        var vault = Vault();
        var keys = Keys(vault);
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var purpose in EncryptionKeyPurpose.All)
        {
            var wrapped = await keys.WrapDataKeyAsync(Scope(OrgA, purpose), Dek(), Ct);
            Assert.Equal("vault-transit:" + OrgA.ToString("D") + ":" + purpose.Token + ":1", wrapped.KeyReference);
            names.Add(vault.Requests[^1].Path);
        }

        Assert.Equal(4, names.Count); // FieldValue, Document, AuditorReasonNote, IdempotencyProtection: four distinct Vault keys

        var field = await keys.WrapDataKeyAsync(Scope(OrgA, EncryptionKeyPurpose.FieldValue), Dek(), Ct);
        var document = await keys.WrapDataKeyAsync(Scope(OrgA, EncryptionKeyPurpose.Document), Dek(), Ct);
        await Assert.ThrowsAnyAsync<CryptographicException>(() => keys.UnwrapDataKeyAsync(Scope(OrgA, EncryptionKeyPurpose.Document), field.KeyReference, field.WrappedKeyBytes, Ct));
        await Assert.ThrowsAnyAsync<CryptographicException>(() => keys.UnwrapDataKeyAsync(Scope(OrgA, EncryptionKeyPurpose.Document), document.KeyReference, field.WrappedKeyBytes, Ct));
    }

    // ------------------------------------------------------------------ 4. Key-reference substitution

    [Fact]
    public async Task SubstitutedOrNonCanonicalReferencesAreRefused()
    {
        var vault = Vault(versions: 2);
        var keys = Keys(vault, o => o.Purposes["FieldValue"] = new KeyVersionOptions { CurrentVersion = 2, RetainedVersions = [1] });
        var scope = Scope(OrgA, EncryptionKeyPurpose.FieldValue);
        var v2 = await keys.WrapDataKeyAsync(scope, Dek(), Ct);
        Assert.EndsWith(":2", v2.KeyReference, StringComparison.Ordinal);

        // Version substitution: v2 bytes under a v1 reference are decrypted with v1 and the v1 context, and fail authentication.
        await Assert.ThrowsAnyAsync<CryptographicException>(() => keys.UnwrapDataKeyAsync(scope, VaultTransitKeyProvider.Reference(scope, 1), v2.WrappedKeyBytes, Ct));

        // References of another provider, malformed or non-canonical spellings never reach Vault.
        var before = vault.Requests.Count;
        foreach (var reference in new[]
                 {
                     "localdev:" + OrgA.ToString("D") + ":field-value:2",
                     "local-dev-kek-v1",
                     "vault-transit:" + OrgA.ToString("D").ToUpperInvariant() + ":field-value:2",
                     "vault-transit:" + OrgA.ToString("N") + ":field-value:2",
                     "vault-transit:" + OrgA.ToString("D") + ":field-value:02",
                     "vault-transit:" + OrgA.ToString("D") + ":field-value:2:extra",
                     "vault-transit:" + OrgA.ToString("D") + ":FieldValue:2",
                     "vault-transit:" + OrgA.ToString("D") + ":field-value:0",
                     "Vault-Transit:" + OrgA.ToString("D") + ":field-value:2",
                     "vault-transit:" + OrgA.ToString("D") + ":field-value:2 ",
                     "ef-enc-" + OrgA.ToString("N") + "-field-value",
                     "",
                 })
        {
            await Assert.ThrowsAsync<EncryptionUnavailableException>(() => keys.UnwrapDataKeyAsync(scope, reference, v2.WrappedKeyBytes, Ct));
        }

        Assert.Equal(before, vault.Requests.Count);

        // A wrapped key that is not exactly the 60-byte layout is refused before Vault is called (no Vault ciphertext text is accepted).
        await Assert.ThrowsAnyAsync<CryptographicException>(() => keys.UnwrapDataKeyAsync(scope, v2.KeyReference, System.Text.Encoding.ASCII.GetBytes("vault:v2:" + Convert.ToBase64String(v2.WrappedKeyBytes)), Ct));
        await Assert.ThrowsAnyAsync<CryptographicException>(() => keys.UnwrapDataKeyAsync(scope, v2.KeyReference, v2.WrappedKeyBytes.AsMemory(0, 59), Ct));
        Assert.Equal(before, vault.Requests.Count);

        // Tampered wrapped bytes fail authentication in Vault.
        var tampered = (byte[])v2.WrappedKeyBytes.Clone();
        tampered[20] ^= 0x01;
        await Assert.ThrowsAnyAsync<CryptographicException>(() => keys.UnwrapDataKeyAsync(scope, v2.KeyReference, tampered, Ct));
    }

    [Fact]
    public void TheVaultKeyNameIsDerivedOnlyFromTrustedConfigurationAndTheScope()
    {
        var keys = Keys(Vault(), o => o.KeyNamePrefix = "dev-enc");
        Assert.Equal("dev-enc-" + OrgA.ToString("N") + "-idempotency-protection", keys.KeyName(Scope(OrgA, EncryptionKeyPurpose.IdempotencyProtection)));
        // The provider interface has no parameter that names a key or a mount.
        foreach (var method in typeof(IEncryptionKeyProvider).GetMethods())
        {
            Assert.DoesNotContain(method.GetParameters(), p => p.Name is not null && (p.Name.Contains("name", StringComparison.OrdinalIgnoreCase) || p.Name.Contains("mount", StringComparison.OrdinalIgnoreCase)));
        }
    }

    // ------------------------------------------------------------------ 5. Current-version resolution

    [Fact]
    public async Task NewWrapsUseTheConfiguredCurrentVersionAndRefuseAnyOtherReportedVersion()
    {
        var vault = Vault(versions: 3);
        var keys = Keys(vault, o => o.Purposes["FieldValue"] = new KeyVersionOptions { CurrentVersion = 2, RetainedVersions = [1] });
        var wrapped = await keys.WrapDataKeyAsync(Scope(OrgA, EncryptionKeyPurpose.FieldValue), Dek(), Ct);
        Assert.Equal("vault-transit:" + OrgA.ToString("D") + ":field-value:2", wrapped.KeyReference);
        Assert.Equal(2, vault.Requests[^1].Body.GetProperty("key_version").GetInt32()); // Vault's latest is 3; the configured current version is used, never "latest"

        // Other purposes keep their own (default) table.
        Assert.EndsWith(":1", (await keys.WrapDataKeyAsync(Scope(OrgA, EncryptionKeyPurpose.Document), Dek(), Ct)).KeyReference, StringComparison.Ordinal);

        // A Vault response for a different version (or another ciphertext prefix) is not accepted.
        vault.Override = (_, _) => Task.FromResult(FakeVaultTransit.Json(new { data = new { ciphertext = "vault:v3:" + Convert.ToBase64String(new byte[60]), key_version = 3 } }));
        await Assert.ThrowsAsync<EncryptionUnavailableException>(() => keys.WrapDataKeyAsync(Scope(OrgA, EncryptionKeyPurpose.FieldValue), Dek(), Ct));
        vault.Override = (_, _) => Task.FromResult(FakeVaultTransit.Json(new { data = new { ciphertext = "vault:v3:" + Convert.ToBase64String(new byte[60]), key_version = 2 } }));
        await Assert.ThrowsAsync<EncryptionUnavailableException>(() => keys.WrapDataKeyAsync(Scope(OrgA, EncryptionKeyPurpose.FieldValue), Dek(), Ct));
        vault.Override = (_, _) => Task.FromResult(FakeVaultTransit.Json(new { data = new { ciphertext = "vault:v2:" + Convert.ToBase64String(new byte[60]), key_version = 3 } }));
        await Assert.ThrowsAsync<EncryptionUnavailableException>(() => keys.WrapDataKeyAsync(Scope(OrgA, EncryptionKeyPurpose.FieldValue), Dek(), Ct));
        vault.Override = (_, _) => Task.FromResult(FakeVaultTransit.Json(new { data = new { ciphertext = "vault:v2:" + Convert.ToBase64String(new byte[60]) } }));
        await Assert.ThrowsAsync<EncryptionUnavailableException>(() => keys.WrapDataKeyAsync(Scope(OrgA, EncryptionKeyPurpose.FieldValue), Dek(), Ct));
        vault.Override = (_, _) => Task.FromResult(FakeVaultTransit.Json(new { data = new { ciphertext = "vault:v2:" + Convert.ToBase64String(new byte[59]), key_version = 2 } }));
        await Assert.ThrowsAsync<EncryptionUnavailableException>(() => keys.WrapDataKeyAsync(Scope(OrgA, EncryptionKeyPurpose.FieldValue), Dek(), Ct));
    }

    // ------------------------------------------------------------------ 6. Historical-version resolution

    [Fact]
    public async Task RetainedHistoricalVersionsStillUnwrapAfterTheCurrentVersionMoves()
    {
        var vault = Vault(versions: 2);
        var scope = Scope(OrgA, EncryptionKeyPurpose.Document);
        var dek = Dek();
        var old = await Keys(vault).WrapDataKeyAsync(scope, dek, Ct);
        Assert.EndsWith(":1", old.KeyReference, StringComparison.Ordinal);

        var moved = Keys(vault, o => o.Purposes["Document"] = new KeyVersionOptions { CurrentVersion = 2, RetainedVersions = [1] });
        Assert.Equal(dek, await moved.UnwrapDataKeyAsync(scope, old.KeyReference, old.WrappedKeyBytes, Ct));
        Assert.StartsWith("vault:v1:", vault.Requests[^1].Body.GetProperty("ciphertext").GetString(), StringComparison.Ordinal); // decrypted with exactly the referenced version
        var fresh = await moved.WrapDataKeyAsync(scope, dek, Ct);
        Assert.EndsWith(":2", fresh.KeyReference, StringComparison.Ordinal);
        Assert.Equal(dek, await moved.UnwrapDataKeyAsync(scope, fresh.KeyReference, fresh.WrappedKeyBytes, Ct));

        // A per-organization table replaces the purpose table for that organization only.
        var perOrganization = Keys(vault, o => o.Organizations[OrgB.ToString("D")] = new() { ["Document"] = new KeyVersionOptions { CurrentVersion = 2 } });
        Assert.EndsWith(":2", (await perOrganization.WrapDataKeyAsync(Scope(OrgB, EncryptionKeyPurpose.Document), dek, Ct)).KeyReference, StringComparison.Ordinal);
        Assert.EndsWith(":1", (await perOrganization.WrapDataKeyAsync(scope, dek, Ct)).KeyReference, StringComparison.Ordinal);
        await Assert.ThrowsAsync<EncryptionUnavailableException>(() => perOrganization.UnwrapDataKeyAsync(Scope(OrgB, EncryptionKeyPurpose.Document), "vault-transit:" + OrgB.ToString("D") + ":document:1", old.WrappedKeyBytes, Ct)); // 1 not retained for OrgB
    }

    // ------------------------------------------------------------------ 7. Missing and disabled versions

    [Fact]
    public async Task MissingDisabledAndVaultRetiredVersionsFailClosed()
    {
        var vault = Vault(versions: 2);
        var scope = Scope(OrgA, EncryptionKeyPurpose.FieldValue);
        var v1 = await Keys(vault).WrapDataKeyAsync(scope, Dek(), Ct);

        // Unknown (never configured) and disabled versions are refused before Vault is called.
        var disabled = Keys(vault, o => o.Purposes["FieldValue"] = new KeyVersionOptions { CurrentVersion = 2, DisabledVersions = [1] });
        var before = vault.Requests.Count;
        Assert.Contains("disabled", (await Assert.ThrowsAsync<EncryptionUnavailableException>(() => disabled.UnwrapDataKeyAsync(scope, v1.KeyReference, v1.WrappedKeyBytes, Ct))).Message, StringComparison.Ordinal);
        await Assert.ThrowsAsync<EncryptionUnavailableException>(() => Keys(vault).UnwrapDataKeyAsync(scope, VaultTransitKeyProvider.Reference(scope, 7), v1.WrappedKeyBytes, Ct));
        Assert.Equal(before, vault.Requests.Count);

        // A version Vault itself no longer allows (min_decryption_version) fails closed as unavailable, not as tampering.
        vault.SetMinimumDecryptionVersion(KeyName(OrgA, EncryptionKeyPurpose.FieldValue), 2);
        await Assert.ThrowsAsync<EncryptionUnavailableException>(() => Keys(vault).UnwrapDataKeyAsync(scope, v1.KeyReference, v1.WrappedKeyBytes, Ct));

        // A configured current version Vault does not have, and a key that was never provisioned, fail closed; nothing is created.
        await Assert.ThrowsAsync<EncryptionUnavailableException>(() => Keys(vault, o => o.Purposes["FieldValue"] = new KeyVersionOptions { CurrentVersion = 3 }).WrapDataKeyAsync(scope, Dek(), Ct));
        var unprovisioned = Guid.CreateVersion7();
        var keyCount = vault.KeyNames.Count;
        await Assert.ThrowsAsync<EncryptionUnavailableException>(() => Keys(vault).WrapDataKeyAsync(Scope(unprovisioned, EncryptionKeyPurpose.FieldValue), Dek(), Ct));
        await Assert.ThrowsAsync<EncryptionUnavailableException>(() => Keys(vault).UnwrapDataKeyAsync(Scope(unprovisioned, EncryptionKeyPurpose.FieldValue), "vault-transit:" + unprovisioned.ToString("D") + ":field-value:1", v1.WrappedKeyBytes, Ct));
        Assert.Equal(keyCount, vault.KeyNames.Count);
    }

    // ------------------------------------------------------------------ 8. Vault unavailability

    [Fact]
    public async Task AnUnreachableSlowFailingOrGarbledVaultFailsClosed()
    {
        var scope = Scope(OrgA, EncryptionKeyPurpose.FieldValue);
        var vault = Vault();
        var good = await Keys(vault).WrapDataKeyAsync(scope, Dek(), Ct);
        var keys = Keys(vault, o => o.TimeoutSeconds = 1);

        var cases = new Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>[]
        {
            (_, _) => throw new HttpRequestException("connection refused"),
            async (_, token) => { await Task.Delay(Timeout.Infinite, token); return FakeVaultTransit.Json(new { }); },
            (_, _) => Task.FromResult(FakeVaultTransit.Error(HttpStatusCode.ServiceUnavailable, "Vault is sealed")),
            (_, _) => Task.FromResult(FakeVaultTransit.Error(HttpStatusCode.InternalServerError, "internal error")),
            (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("not json") }),
            (_, _) => Task.FromResult(FakeVaultTransit.Json(new { data = new { } })),
            (_, _) => Task.FromResult(FakeVaultTransit.Json(new { data = new { plaintext = "%%%", ciphertext = "%%%" } })),
        };
        foreach (var behaviour in cases)
        {
            vault.Override = behaviour;
            var wrap = await Assert.ThrowsAsync<EncryptionUnavailableException>(() => keys.WrapDataKeyAsync(scope, Dek(), Ct));
            var unwrap = await Assert.ThrowsAsync<EncryptionUnavailableException>(() => keys.UnwrapDataKeyAsync(scope, good.KeyReference, good.WrappedKeyBytes, Ct));
            foreach (var message in new[] { wrap.Message, unwrap.Message })
            {
                Assert.DoesNotContain(FakeVaultTransit.BusinessToken, message, StringComparison.Ordinal);
                Assert.DoesNotContain("sealed", message, StringComparison.OrdinalIgnoreCase); // no Vault error text
                Assert.DoesNotContain("connection refused", message, StringComparison.OrdinalIgnoreCase);
            }
        }

        // A Vault returning a 31-byte "data key" is not trusted.
        vault.Override = (_, _) => Task.FromResult(FakeVaultTransit.Json(new { data = new { plaintext = Convert.ToBase64String(new byte[31]) } }));
        await Assert.ThrowsAnyAsync<CryptographicException>(() => keys.UnwrapDataKeyAsync(scope, good.KeyReference, good.WrappedKeyBytes, Ct));
    }

    // ------------------------------------------------------------------ 9. Invalid credentials and permission denial

    [Fact]
    public async Task InvalidOrUnauthorizedCredentialsFailClosedWithoutRevealingThem()
    {
        var vault = Vault();
        var scope = Scope(OrgA, EncryptionKeyPurpose.FieldValue);
        var good = await Keys(vault).WrapDataKeyAsync(scope, Dek(), Ct);

        const string expired = "an-expired-or-revoked-token";
        var wrong = Keys(vault, o => o.Token = expired);
        foreach (var refusal in new Exception[]
                 {
                     await Assert.ThrowsAsync<EncryptionUnavailableException>(() => wrong.WrapDataKeyAsync(scope, Dek(), Ct)),
                     await Assert.ThrowsAsync<EncryptionUnavailableException>(() => wrong.UnwrapDataKeyAsync(scope, good.KeyReference, good.WrappedKeyBytes, Ct)),
                 })
        {
            Assert.Contains("status 403", refusal.Message, StringComparison.Ordinal);
            Assert.DoesNotContain(expired, refusal.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("permission denied", refusal.Message, StringComparison.Ordinal);
        }

        // The business token is limited to its own mount: a mount it has no policy for (for example the Authority's) is refused.
        vault.TokenMounts[FakeVaultTransit.BusinessToken] = "some-other-mount";
        await Assert.ThrowsAsync<EncryptionUnavailableException>(() => Keys(vault).WrapDataKeyAsync(scope, Dek(), Ct));
    }

    // ------------------------------------------------------------------ 10. Cancellation propagation

    [Fact]
    public async Task CancellationPropagatesAndIsNotReportedAsAnOutage()
    {
        var vault = Vault();
        var keys = Keys(vault, o => o.TimeoutSeconds = 30);
        var scope = Scope(OrgA, EncryptionKeyPurpose.FieldValue);
        var good = await keys.WrapDataKeyAsync(scope, Dek(), Ct);
        var before = vault.Requests.Count;

        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => keys.WrapDataKeyAsync(scope, Dek(), cancelled.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => keys.UnwrapDataKeyAsync(scope, good.KeyReference, good.WrappedKeyBytes, cancelled.Token));
        Assert.Equal(before, vault.Requests.Count); // nothing was sent

        // Cancelled while Vault is answering: the caller's token reaches the HTTP call and OperationCanceledException propagates.
        var observed = new List<CancellationToken>();
        vault.Override = async (_, token) => { observed.Add(token); await Task.Delay(Timeout.Infinite, token); return FakeVaultTransit.Json(new { }); };
        using var midway = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        var wrap = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => keys.WrapDataKeyAsync(scope, Dek(), midway.Token));
        Assert.IsNotType<EncryptionUnavailableException>(wrap);
        using var midway2 = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => keys.UnwrapDataKeyAsync(scope, good.KeyReference, good.WrappedKeyBytes, midway2.Token));
        Assert.Equal(2, observed.Count);

        // Through a consumer: field encryption propagates the cancellation unchanged.
        using var cancelled2 = new CancellationTokenSource();
        await cancelled2.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new AesGcmFieldEncryptionService(keys).EncryptAsync(FieldContext, "x", EncryptionKeyPurpose.FieldValue, cancelled2.Token));
    }

    // ------------------------------------------------------------------ 11. Existing v1 envelope compatibility

    private static readonly FieldEncryptionContext FieldContext = new(
        Guid.Parse("019a5b2c-e001-7000-8000-000000000001"), Guid.Parse("019a5b2c-e002-7000-8000-000000000002"),
        Guid.Parse("019a5b2c-e003-7000-8000-000000000003"), Guid.Parse("019a5b2c-e004-7000-8000-000000000004"), "String");

    private static byte[] Seq(int start, int count) => Enumerable.Range(0, count).Select(i => (byte)((start + i) % 256)).ToArray();

    [Fact]
    public async Task TheVaultProviderProducesUnchangedV1EnvelopesForEveryConsumer()
    {
        var vault = new FakeVaultTransit(Mount);
        foreach (var purpose in EncryptionKeyPurpose.All)
        {
            vault.Provision(KeyName(FieldContext.TenantId, purpose));
        }

        var keys = Keys(vault);
        var service = new AesGcmFieldEncryptionService(keys);

        // E0-6 vector F1: the payload bytes are a function of DEK, nonce and AAD only, so they are byte-identical under the Vault provider.
        var sealedRow = await service.EncryptAsync(FieldContext, "Avery Synthetic — Léa 42", EncryptionKeyPurpose.FieldValue, Seq(0x20, 32), Seq(0x50, 12), Ct);
        Assert.Equal("f0d6cd60480f24745a7b627f1960e08f3e6cdb1f3435d06182c8ea", Convert.ToHexStringLower(sealedRow.EncryptedValueBytes));
        Assert.Equal("d82e6790db93b540e70daa1bd08e423b", Convert.ToHexStringLower(sealedRow.EncryptionAuthTagBytes));
        Assert.Equal(("AES-256-GCM", 1, "vault-transit:019a5b2c-e001-7000-8000-000000000001:field-value:1", 60), (sealedRow.EncryptionAlgorithmName, sealedRow.EncryptionEnvelopeVersion, sealedRow.EncryptionKeyReference, sealedRow.WrappedDataKeyBytes.Length));
        Assert.Equal("Avery Synthetic — Léa 42", await service.DecryptAsync(sealedRow, EncryptionKeyPurpose.FieldValue, Ct));

        // A Vault-wrapped value that no longer authenticates is reported as a key mismatch (E0-4), with nothing released.
        var rewrapped = Clone(sealedRow);
        rewrapped.WrappedDataKeyBytes[30] ^= 0x01;
        Assert.Equal(DecryptionFailureReasons.KeyMismatch, DecryptionFailure.Find(await Assert.ThrowsAnyAsync<CryptographicException>(() => service.DecryptAsync(rewrapped, EncryptionKeyPurpose.FieldValue, Ct)))!.ReasonCode);

        // Idempotency protection uses the same envelope under its own purpose and key.
        var idempotency = await service.EncryptAsync(FieldContext, "fingerprint-key", EncryptionKeyPurpose.IdempotencyProtection, Ct);
        Assert.Equal("vault-transit:019a5b2c-e001-7000-8000-000000000001:idempotency-protection:1", idempotency.EncryptionKeyReference);
        Assert.Equal("fingerprint-key", await service.DecryptAsync(idempotency, EncryptionKeyPurpose.IdempotencyProtection, Ct));

        // E0-6 vector D1: the segmented document format is unchanged.
        var documents = new AesGcmFileEncryptionService(keys);
        var context = new FileEncryptionContext(FieldContext.TenantId, Guid.Parse("019a5b2c-d002-7000-8000-000000000002"), Guid.Parse("019a5b2c-d003-7000-8000-000000000003"), 3);
        var plaintext = Enumerable.Range(0, 37).Select(i => (byte)(((i * 7) + 3) % 256)).ToArray();
        using var sink = new MemoryStream();
        var outcome = await documents.EncryptAsync(new MemoryStream(plaintext), sink, context, Seq(0xC0, 32), Seq(0xE0, 8), Ct);
        Assert.Equal(("AES-256-GCM", 1, "vault-transit:019a5b2c-e001-7000-8000-000000000001:document:1"), (outcome.Envelope.AlgorithmName, outcome.Envelope.EnvelopeVersion, outcome.Envelope.KeyReference));
        using var opened = new MemoryStream();
        await documents.DecryptAsync(new MemoryStream(sink.ToArray()), opened, outcome.Envelope, context, Ct);
        Assert.Equal(plaintext, opened.ToArray());

        // Reason notes: the envelope the Authority validates accepts the Vault reference and the 60-byte wrapped key unchanged.
        var noteId = Guid.CreateVersion7();
        var note = await new AesGcmAuditorNoteSealer(keys).SealAsync(new AuditorReasonNoteContext(FieldContext.TenantId, Guid.CreateVersion7(), AuditorReasonNoteFormat.RoleName, AuditorReasonNoteFormat.Appoint, noteId), "Covering the Q4 review", Ct);
        Assert.Equal("vault-transit:019a5b2c-e001-7000-8000-000000000001:auditor-reason-note:1", note.KeyReference);
        var asReceived = new AuthorizationAuthority.Contracts.V1.AuditorReasonNoteEnvelope(note.EnvelopeFormat, note.EnvelopeVersion, note.AlgorithmName, note.KeyReference, note.NoteId, note.NonceB64, note.CiphertextB64, note.TagB64, note.WrappedDataKeyB64);
        Assert.Equal(noteId, AuthorizationAuthority.Application.Features.SecurityCommands.AuditorReasonNoteEnvelopeValidation.Validate(asReceived));
    }

    private static FieldEncryptedValue Clone(FieldEncryptedValue v) => new()
    {
        FieldEncryptedValueId = v.FieldEncryptedValueId,
        TenantId = v.TenantId,
        FormRecordId = v.FormRecordId,
        FieldDefinitionId = v.FieldDefinitionId,
        FieldValueItemId = v.FieldValueItemId,
        EncryptedValueBytes = (byte[])v.EncryptedValueBytes.Clone(),
        EncryptionNonceBytes = (byte[])v.EncryptionNonceBytes.Clone(),
        EncryptionAuthTagBytes = (byte[])v.EncryptionAuthTagBytes.Clone(),
        WrappedDataKeyBytes = (byte[])v.WrappedDataKeyBytes.Clone(),
        EncryptionAlgorithmName = v.EncryptionAlgorithmName,
        EncryptionKeyReference = v.EncryptionKeyReference,
        EncryptionEnvelopeVersion = v.EncryptionEnvelopeVersion,
        EncryptedLogicalType = v.EncryptedLogicalType,
    };

    // ------------------------------------------------------------------ 12. No fallback to LocalDevelopment

    private static IConfiguration Configuration(params (string Key, string? Value)[] values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values.Select(v => new KeyValuePair<string, string?>(v.Key, v.Value))).Build();

    private static readonly (string, string?)[] CompleteVaultSettings =
    [
        ("Encryption:KeyProvider", "VaultTransit"),
        ("Encryption:VaultTransit:Address", "http://127.0.0.1:1"), // nothing listens: Vault is unavailable
        ("Encryption:VaultTransit:Token", "dev-business-encryption-token"),
        // The legacy local KEK is also present: it must not be used.
        ("Encryption:LocalDevelopmentKekBase64", Convert.ToBase64String(new byte[32])),
    ];

    [Fact]
    public async Task SelectingVaultTransitRegistersOnlyItAndNeverFallsBackToTheLocalKeyring()
    {
        var services = new ServiceCollection();
        services.AddBusinessInfrastructure(Configuration(CompleteVaultSettings));
        await using var provider = services.BuildServiceProvider();
        var registered = provider.GetServices<IEncryptionKeyProvider>().ToList();
        var keys = Assert.IsType<VaultTransitKeyProvider>(Assert.Single(registered));
        Assert.True(keys.IsConfigured);

        // Vault unreachable: wrap fails closed instead of using the configured local KEK.
        var scope = Scope(OrgA, EncryptionKeyPurpose.FieldValue);
        await Assert.ThrowsAsync<EncryptionUnavailableException>(() => keys.WrapDataKeyAsync(scope, Dek(), Ct));

        // Data wrapped by the local keyring (scoped or legacy reference) is not opened by the Vault provider.
        var local = new LocalDevelopmentKeyProvider(Options.Create(new EncryptionOptions { KeyProvider = "LocalDevelopment", LocalDevelopmentKekBase64 = Convert.ToBase64String(new byte[32]) }));
        var localWrap = await local.WrapDataKeyAsync(scope, Dek(), Ct);
        await Assert.ThrowsAsync<EncryptionUnavailableException>(() => keys.UnwrapDataKeyAsync(scope, localWrap.KeyReference, localWrap.WrappedKeyBytes, Ct));
        await Assert.ThrowsAsync<EncryptionUnavailableException>(() => keys.UnwrapDataKeyAsync(scope, "local-dev-kek-v1", localWrap.WrappedKeyBytes, Ct));

        // Conversely the local keyring does not open Vault references.
        await Assert.ThrowsAsync<EncryptionUnavailableException>(() => local.UnwrapDataKeyAsync(scope, VaultTransitKeyProvider.Reference(scope, 1), new byte[60], Ct));
    }

    [Theory]
    [InlineData("Development", true)]
    [InlineData("Testing", true)]
    [InlineData("Production", false)]
    [InlineData("Staging", false)]
    [InlineData("", false)]
    public void VaultTransitIsADevelopmentAndTestingProviderOnly(string environment, bool permitted)
    {
        var configuration = Configuration(CompleteVaultSettings);
        Assert.Equal(permitted, EncryptionProviderEnvironmentPolicy.IsPermitted("VaultTransit", environment));
        if (permitted)
        {
            EncryptionProviderEnvironmentPolicy.EnsurePermitted(configuration, environment);
        }
        else
        {
            var refused = Assert.Throws<InvalidOperationException>(() => EncryptionProviderEnvironmentPolicy.EnsurePermitted(configuration, environment));
            Assert.Contains("Refusing to start", refused.Message, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData("Encryption:VaultTransit:Address", null, "Address is not set")]
    [InlineData("Encryption:VaultTransit:Address", "vault:8200", "Address must be")]
    [InlineData("Encryption:VaultTransit:Address", "ftp://vault:8200", "Address must be")]
    [InlineData("Encryption:VaultTransit:Address", "http://user:pw@vault:8200", "Address must be")]
    [InlineData("Encryption:VaultTransit:Address", "http://vault:8200/v1/", "Address must be")]
    [InlineData("Encryption:VaultTransit:Token", null, "Token is not set")]
    [InlineData("Encryption:VaultTransit:Token", "a token with spaces", "Token is malformed")]
    [InlineData("Encryption:VaultTransit:MountPath", "transit", "MountPath must be")] // the Authority's HMAC mount
    [InlineData("Encryption:VaultTransit:MountPath", "Encryption/../sys", "MountPath must be")]
    [InlineData("Encryption:VaultTransit:KeyNamePrefix", "ef-", "KeyNamePrefix must be")]
    [InlineData("Encryption:VaultTransit:KeyNamePrefix", "EF", "KeyNamePrefix must be")]
    [InlineData("Encryption:VaultTransit:TimeoutSeconds", "0", "TimeoutSeconds must be")]
    [InlineData("Encryption:VaultTransit:Purposes:FieldValue:CurrentVersion", "0", "key versions are invalid")]
    [InlineData("Encryption:VaultTransit:Purposes:FieldValue:DisabledVersions:0", "1", "key versions are invalid")]
    [InlineData("Encryption:VaultTransit:Purposes:SearchIndex:CurrentVersion", "1", "unknown key purpose")]
    [InlineData("Encryption:VaultTransit:Organizations:not-a-guid:FieldValue:CurrentVersion", "1", "organization key table")]
    public async Task IncompleteOrInvalidVaultSettingsAreRefusedAtStartupAndAtUse(string key, string? value, string expected)
    {
        var settings = CompleteVaultSettings.Where(s => s.Item1 != key).Append((key, value)).ToArray();
        var configuration = Configuration(settings);
        var refused = Assert.Throws<InvalidOperationException>(() => EncryptionProviderEnvironmentPolicy.EnsurePermitted(configuration, "Development"));
        Assert.Contains(expected, refused.Message, StringComparison.Ordinal);
        Assert.Contains("Refusing to start", refused.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("dev-business-encryption-token", refused.Message, StringComparison.Ordinal);

        // Were it started anyway, the provider is unavailable for every operation (it never falls back).
        var services = new ServiceCollection();
        services.AddBusinessInfrastructure(configuration);
        await using var provider = services.BuildServiceProvider();
        var keys = Assert.IsType<VaultTransitKeyProvider>(provider.GetRequiredService<IEncryptionKeyProvider>());
        Assert.False(keys.IsConfigured);
        var scope = Scope(OrgA, EncryptionKeyPurpose.FieldValue);
        await Assert.ThrowsAsync<EncryptionUnavailableException>(() => keys.WrapDataKeyAsync(scope, Dek(), Ct));
        await Assert.ThrowsAsync<EncryptionUnavailableException>(() => keys.UnwrapDataKeyAsync(scope, VaultTransitKeyProvider.Reference(scope, 1), new byte[60], Ct));
    }

    [Fact]
    public void TheApiAndWorkerStartupGuardsApplyTheVaultSettingsCheck()
    {
        var incomplete = Configuration(("Encryption:KeyProvider", "VaultTransit"));
        var api = new EnterpriseForms.Api.Composition.DocumentProviderStartupGuard(incomplete, new TestEnvironment("Development"));
        Assert.Throws<InvalidOperationException>(() => { _ = api.StartAsync(CancellationToken.None); });
        Assert.True(new EnterpriseForms.Api.Composition.DocumentProviderStartupGuard(Configuration(CompleteVaultSettings), new TestEnvironment("Development")).StartAsync(CancellationToken.None).IsCompletedSuccessfully);
    }

    private sealed class TestEnvironment(string name) : Microsoft.Extensions.Hosting.IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;

        public string ApplicationName { get; set; } = "EnterpriseForms.Tests";

        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;

        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = new Microsoft.Extensions.FileProviders.NullFileProvider();
    }
}
