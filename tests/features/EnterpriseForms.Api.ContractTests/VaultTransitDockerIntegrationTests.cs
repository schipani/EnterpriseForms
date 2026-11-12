using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using EnterpriseForms.Application.Features.Documents.Ports;
using EnterpriseForms.Infrastructure.Features.Documents;
using EnterpriseForms.Infrastructure.Features.FormRecords;
using Microsoft.Extensions.Options;
using Xunit;

namespace EnterpriseForms.Api.ContractTests;

/// <summary>
/// E2-7 targeted local Docker Vault integration test: the provider against a REAL HashiCorp Vault 1.20.4 dev server prepared with the
/// repository's own init script and policy (vault/business-encryption-init.sh, business-encryption-policy.hcl) and provisioning script.
/// Run only by <c>scripts/dev/e2-vault-integration.ps1</c>, which starts an isolated, throwaway Vault container and sets
/// E2_VAULT_ADDRESS, E2_VAULT_ROOT_TOKEN and E2_VAULT_BUSINESS_TOKEN; skipped otherwise. The root token is used by the tests ONLY for
/// fixture setup (provisioning fresh keys, adding a key version, raising min_decryption_version, inspecting keys) — operator
/// actions the application never performs.
/// </summary>
public sealed class VaultTransitDockerIntegrationTests
{
    private const string Mount = "enterpriseforms-encryption";
    private static readonly string? Address = Environment.GetEnvironmentVariable("E2_VAULT_ADDRESS");
    private static readonly string? RootToken = Environment.GetEnvironmentVariable("E2_VAULT_ROOT_TOKEN");
    private static readonly string? BusinessToken = Environment.GetEnvironmentVariable("E2_VAULT_BUSINESS_TOKEN");

    /// <summary>Provisioned by the runner script with provision-business-encryption-keys.sh.</summary>
    private static readonly Guid ScriptOrganization = Guid.Parse("019a5b2c-a000-7000-8000-00000000000a");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static void RequireVault() => Assert.SkipWhen(string.IsNullOrEmpty(Address) || string.IsNullOrEmpty(RootToken) || string.IsNullOrEmpty(BusinessToken), "E2_VAULT_* not set (run scripts/dev/e2-vault-integration.ps1)");

    private static VaultTransitKeyProvider Keys(Action<VaultTransitEncryptionOptions>? configure = null)
    {
        var options = new EncryptionOptions
        {
            KeyProvider = EncryptionOptions.VaultTransitProviderName,
            VaultTransit = new VaultTransitEncryptionOptions { Address = Address, Token = BusinessToken, MountPath = Mount },
        };
        configure?.Invoke(options.VaultTransit);
        return new VaultTransitKeyProvider(new PlainFactory(), Options.Create(options));
    }

    private static string KeyName(Guid organization, EncryptionKeyPurpose purpose) => "ef-enc-" + organization.ToString("N") + "-" + purpose.Token;

    private static DataKeyScope Scope(Guid organization, EncryptionKeyPurpose purpose) => new(organization, purpose);

    /// <summary>Fixture setup with the root token: a fresh organization with all four purpose keys (aes256-gcm96, non-exportable).</summary>
    private static async Task<Guid> FreshOrganizationAsync()
    {
        var organization = Guid.CreateVersion7();
        foreach (var purpose in EncryptionKeyPurpose.All)
        {
            using var response = await SendAsync(HttpMethod.Post, $"v1/{Mount}/keys/{KeyName(organization, purpose)}", RootToken!, new { type = "aes256-gcm96", exportable = false, allow_plaintext_backup = false });
            Assert.True(response.IsSuccessStatusCode, ((int)response.StatusCode).ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        return organization;
    }

    private static async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, string token, object? body = null)
    {
        using var client = new HttpClient { BaseAddress = new Uri(Address!) };
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Add("X-Vault-Token", token);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        return await client.SendAsync(request, Ct);
    }

    [Fact]
    public async Task RealVaultWrapsAndUnwrapsUnderTheScriptProvisionedKeys()
    {
        RequireVault();
        var keys = Keys();
        Assert.True(keys.IsConfigured);
        foreach (var purpose in EncryptionKeyPurpose.All)
        {
            // The provisioning script's keys have the required properties (checked here with the root token, never by the application).
            using var info = await SendAsync(HttpMethod.Get, $"v1/{Mount}/keys/{KeyName(ScriptOrganization, purpose)}", RootToken!);
            Assert.Equal(HttpStatusCode.OK, info.StatusCode);
            var data = JsonDocument.Parse(await info.Content.ReadAsStringAsync(Ct)).RootElement.GetProperty("data");
            Assert.Equal(("aes256-gcm96", false, false), (data.GetProperty("type").GetString(), data.GetProperty("exportable").GetBoolean(), data.GetProperty("allow_plaintext_backup").GetBoolean()));

            var scope = Scope(ScriptOrganization, purpose);
            var dek = RandomNumberGenerator.GetBytes(32);
            var wrapped = await keys.WrapDataKeyAsync(scope, dek, Ct);
            Assert.Equal("vault-transit:" + ScriptOrganization.ToString("D") + ":" + purpose.Token + ":1", wrapped.KeyReference);
            Assert.Equal(60, wrapped.WrappedKeyBytes.Length); // Vault's aes256-gcm96 body is nonce(12) ‖ ciphertext(32) ‖ tag(16)
            Assert.Equal(dek, await keys.UnwrapDataKeyAsync(scope, wrapped.KeyReference, wrapped.WrappedKeyBytes, Ct));
        }

        // The v1 field envelope round-trips through real Vault.
        var service = new AesGcmFieldEncryptionService(keys);
        var context = new EnterpriseForms.Application.Features.FormRecords.Encryption.FieldEncryptionContext(ScriptOrganization, Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), "String");
        var sealedRow = await service.EncryptAsync(context, "Avery Synthetic — Léa 42", EncryptionKeyPurpose.FieldValue, Ct);
        Assert.Equal(("AES-256-GCM", 1), (sealedRow.EncryptionAlgorithmName, sealedRow.EncryptionEnvelopeVersion));
        Assert.Equal("Avery Synthetic — Léa 42", await service.DecryptAsync(sealedRow, EncryptionKeyPurpose.FieldValue, Ct));
    }

    [Fact]
    public async Task RealVaultRejectsCrossOrganizationCrossPurposeAndVersionSubstitution()
    {
        RequireVault();
        var a = await FreshOrganizationAsync();
        var b = await FreshOrganizationAsync();
        var keys = Keys();
        var dek = RandomNumberGenerator.GetBytes(32);
        var wrappedA = await keys.WrapDataKeyAsync(Scope(a, EncryptionKeyPurpose.FieldValue), dek, Ct);

        // Organization: A's bytes under B's reference reach B's key in Vault and fail authentication (classified as tampering, not outage).
        await Assert.ThrowsAnyAsync<CryptographicException>(() => keys.UnwrapDataKeyAsync(Scope(b, EncryptionKeyPurpose.FieldValue), VaultTransitKeyProvider.Reference(Scope(b, EncryptionKeyPurpose.FieldValue), 1), wrappedA.WrappedKeyBytes, Ct));
        // Purpose: A's field-value bytes as A's document key.
        await Assert.ThrowsAnyAsync<CryptographicException>(() => keys.UnwrapDataKeyAsync(Scope(a, EncryptionKeyPurpose.Document), VaultTransitKeyProvider.Reference(Scope(a, EncryptionKeyPurpose.Document), 1), wrappedA.WrappedKeyBytes, Ct));
        // Tampering.
        var tampered = (byte[])wrappedA.WrappedKeyBytes.Clone();
        tampered[40] ^= 0x80;
        await Assert.ThrowsAnyAsync<CryptographicException>(() => keys.UnwrapDataKeyAsync(Scope(a, EncryptionKeyPurpose.FieldValue), wrappedA.KeyReference, tampered, Ct));

        // Versions: add version 2 (operator action), make it current with 1 retained.
        var name = KeyName(a, EncryptionKeyPurpose.FieldValue);
        using (var rotate = await SendAsync(HttpMethod.Post, $"v1/{Mount}/keys/{name}/rotate", RootToken!, new { }))
        {
            Assert.True(rotate.IsSuccessStatusCode);
        }

        var moved = Keys(o => o.Purposes["FieldValue"] = new KeyVersionOptions { CurrentVersion = 2, RetainedVersions = [1] });
        var wrapped2 = await moved.WrapDataKeyAsync(Scope(a, EncryptionKeyPurpose.FieldValue), dek, Ct);
        Assert.EndsWith(":2", wrapped2.KeyReference, StringComparison.Ordinal);
        Assert.Equal(dek, await moved.UnwrapDataKeyAsync(Scope(a, EncryptionKeyPurpose.FieldValue), wrapped2.KeyReference, wrapped2.WrappedKeyBytes, Ct));
        Assert.Equal(dek, await moved.UnwrapDataKeyAsync(Scope(a, EncryptionKeyPurpose.FieldValue), wrappedA.KeyReference, wrappedA.WrappedKeyBytes, Ct)); // historical v1
        // v2 bytes under the v1 reference, and v1 bytes under the v2 reference, both fail authentication in Vault.
        await Assert.ThrowsAnyAsync<CryptographicException>(() => moved.UnwrapDataKeyAsync(Scope(a, EncryptionKeyPurpose.FieldValue), wrappedA.KeyReference, wrapped2.WrappedKeyBytes, Ct));
        await Assert.ThrowsAnyAsync<CryptographicException>(() => moved.UnwrapDataKeyAsync(Scope(a, EncryptionKeyPurpose.FieldValue), wrapped2.KeyReference, wrappedA.WrappedKeyBytes, Ct));

        // A version Vault does not have (configured current 5) and a version Vault retired (min_decryption_version) fail closed.
        await Assert.ThrowsAsync<EncryptionUnavailableException>(() => Keys(o => o.Purposes["FieldValue"] = new KeyVersionOptions { CurrentVersion = 5 }).WrapDataKeyAsync(Scope(a, EncryptionKeyPurpose.FieldValue), dek, Ct));
        using (var config = await SendAsync(HttpMethod.Post, $"v1/{Mount}/keys/{name}/config", RootToken!, new { min_decryption_version = 2 }))
        {
            Assert.True(config.IsSuccessStatusCode);
        }

        await Assert.ThrowsAsync<EncryptionUnavailableException>(() => moved.UnwrapDataKeyAsync(Scope(a, EncryptionKeyPurpose.FieldValue), wrappedA.KeyReference, wrappedA.WrappedKeyBytes, Ct));
        Assert.Equal(dek, await moved.UnwrapDataKeyAsync(Scope(a, EncryptionKeyPurpose.FieldValue), wrapped2.KeyReference, wrapped2.WrappedKeyBytes, Ct));
    }

    [Fact]
    public async Task TheApplicationCannotCreateAKeyByEncryptingUnderAnUnprovisionedOrganization()
    {
        RequireVault();
        var unprovisioned = Guid.CreateVersion7();
        var refusal = await Assert.ThrowsAsync<EncryptionUnavailableException>(() => Keys().WrapDataKeyAsync(Scope(unprovisioned, EncryptionKeyPurpose.FieldValue), RandomNumberGenerator.GetBytes(32), Ct));
        Assert.Contains("status 403", refusal.Message, StringComparison.Ordinal); // encrypt-upsert needs `create`, which the business token lacks
        using var check = await SendAsync(HttpMethod.Get, $"v1/{Mount}/keys/{KeyName(unprovisioned, EncryptionKeyPurpose.FieldValue)}", RootToken!);
        Assert.Equal(HttpStatusCode.NotFound, check.StatusCode); // and no key was created
        await Assert.ThrowsAsync<EncryptionUnavailableException>(() => Keys().UnwrapDataKeyAsync(Scope(unprovisioned, EncryptionKeyPurpose.FieldValue), VaultTransitKeyProvider.Reference(Scope(unprovisioned, EncryptionKeyPurpose.FieldValue), 1), new byte[60], Ct));
    }

    [Fact]
    public async Task TheBusinessTokenHasNoAdministrativeExportOrAuthorityCapability()
    {
        RequireVault();
        var key = KeyName(ScriptOrganization, EncryptionKeyPurpose.FieldValue);
        var authorityKey = "org-" + ScriptOrganization.ToString("N") + "-row-integrity"; // created on the Authority's mount by the runner script
        var probes = new (HttpMethod Method, string Path, object? Body)[]
        {
            (HttpMethod.Get, $"v1/{Mount}/keys/{key}", null),                                  // read key metadata
            (HttpMethod.Post, $"v1/{Mount}/keys/ef-enc-{Guid.CreateVersion7():N}-field-value", new { type = "aes256-gcm96" }), // create
            (HttpMethod.Post, $"v1/{Mount}/keys/{key}/rotate", new { }),                       // rotate
            (HttpMethod.Post, $"v1/{Mount}/keys/{key}/config", new { exportable = true }),     // reconfigure (export)
            (HttpMethod.Delete, $"v1/{Mount}/keys/{key}", null),                               // delete
            (HttpMethod.Get, $"v1/{Mount}/export/encryption-key/{key}", null),                 // export
            (HttpMethod.Get, $"v1/{Mount}/backup/{key}", null),                                // backup
            (HttpMethod.Post, $"v1/{Mount}/datakey/plaintext/{key}", new { }),                 // datakey generation
            (HttpMethod.Post, $"v1/{Mount}/rewrap/{key}", new { ciphertext = "vault:v1:AAAA" }), // rewrap
            (HttpMethod.Post, $"v1/{Mount}/encrypt/other-prefix-key", new { plaintext = "AAAA" }), // outside the business prefix
            (HttpMethod.Post, $"v1/transit/hmac/{authorityKey}/sha2-256", new { input = "AAAA" }), // the Authority's HMAC mount
            (HttpMethod.Post, $"v1/transit/encrypt/{authorityKey}", new { plaintext = "AAAA" }),
            (HttpMethod.Get, "v1/sys/mounts", null),
            (HttpMethod.Get, "v1/auth/token/lookup-self", null),
        };
        foreach (var (method, path, body) in probes)
        {
            using var response = await SendAsync(method, path, BusinessToken!, body);
            Assert.True(response.StatusCode == HttpStatusCode.Forbidden, path + " → " + (int)response.StatusCode);
        }

        // The Authority-mount key still exists and was untouched (root check).
        using var authority = await SendAsync(HttpMethod.Get, $"v1/transit/keys/{authorityKey}", RootToken!);
        Assert.Equal(HttpStatusCode.OK, authority.StatusCode);
    }

    [Fact]
    public async Task InvalidCredentialsAndAnUnreachableVaultFailClosed()
    {
        RequireVault();
        var scope = Scope(ScriptOrganization, EncryptionKeyPurpose.Document);
        var good = await Keys().WrapDataKeyAsync(scope, RandomNumberGenerator.GetBytes(32), Ct);

        var wrongToken = Keys(o => o.Token = "hvs.not-a-real-token");
        Assert.Contains("status 403", (await Assert.ThrowsAsync<EncryptionUnavailableException>(() => wrongToken.WrapDataKeyAsync(scope, RandomNumberGenerator.GetBytes(32), Ct))).Message, StringComparison.Ordinal);
        await Assert.ThrowsAsync<EncryptionUnavailableException>(() => wrongToken.UnwrapDataKeyAsync(scope, good.KeyReference, good.WrappedKeyBytes, Ct));

        var unreachable = Keys(o => { o.Address = "http://127.0.0.1:1"; o.TimeoutSeconds = 2; });
        // Refused or timed out, depending on the host's TCP stack (Windows retries a refused loopback connection for ~2 s): both fail closed.
        var outage = (await Assert.ThrowsAsync<EncryptionUnavailableException>(() => unreachable.WrapDataKeyAsync(scope, RandomNumberGenerator.GetBytes(32), Ct))).Message;
        Assert.True(outage.Contains("unreachable", StringComparison.Ordinal) || outage.Contains("timed out", StringComparison.Ordinal), outage);
        await Assert.ThrowsAsync<EncryptionUnavailableException>(() => unreachable.UnwrapDataKeyAsync(scope, good.KeyReference, good.WrappedKeyBytes, Ct));
    }

    private sealed class PlainFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new SocketsHttpHandler { AllowAutoRedirect = false });
    }
}
