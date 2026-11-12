using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using EnterpriseForms.Application.Features.Documents.Ports;
using EnterpriseForms.Infrastructure.Features.Documents;
using Microsoft.Extensions.Options;
using Xunit;

namespace EnterpriseForms.Api.ContractTests;

/// <summary>
/// E-4 controlled key activation against a REAL HashiCorp Vault 1.20.4 dev server. Run only by <c>scripts/dev/e2-vault-integration.ps1</c>,
/// which provisions the activation organization's keys with provision-business-encryption-keys.sh and then provisions version 2 of
/// its <c>field-value</c> key with the E-4 operator script provision-business-encryption-key-version.sh (and checks that script's
/// refusals). Skipped without E2_VAULT_* and E4_VAULT_ACTIVATION_ORGANIZATION. The root token is used here only to inspect keys.
/// These tests never add versions or change key configuration, so they do not depend on each other's order.
/// </summary>
public sealed class KeyActivationVaultIntegrationTests
{
    private const string Mount = "enterpriseforms-encryption";
    private static readonly string? Address = Environment.GetEnvironmentVariable("E2_VAULT_ADDRESS");
    private static readonly string? RootToken = Environment.GetEnvironmentVariable("E2_VAULT_ROOT_TOKEN");
    private static readonly string? BusinessToken = Environment.GetEnvironmentVariable("E2_VAULT_BUSINESS_TOKEN");
    private static readonly string? ActivationOrganizationText = Environment.GetEnvironmentVariable("E4_VAULT_ACTIVATION_ORGANIZATION");

    /// <summary>Provisioned by the runner at version 1 only (the E-2 tests' organization): never given a second version.</summary>
    private static readonly Guid ScriptOrganization = Guid.Parse("019a5b2c-a000-7000-8000-00000000000a");

    private static Guid ActivationOrganization => Guid.Parse(ActivationOrganizationText!);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static void RequireVault() => Assert.SkipWhen(
        string.IsNullOrEmpty(Address) || string.IsNullOrEmpty(RootToken) || string.IsNullOrEmpty(BusinessToken) || string.IsNullOrEmpty(ActivationOrganizationText),
        "E2_VAULT_* / E4_VAULT_ACTIVATION_ORGANIZATION not set (run scripts/dev/e2-vault-integration.ps1)");

    private static string KeyName(Guid organization, EncryptionKeyPurpose purpose) => "ef-enc-" + organization.ToString("N") + "-" + purpose.Token;

    private static DataKeyScope Scope(Guid organization, EncryptionKeyPurpose purpose) => new(organization, purpose);

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

    /// <summary>The explicit activation: version 2 current and version 1 retained, for one organization and purpose only.</summary>
    private static void Activate(VaultTransitEncryptionOptions options, Guid organization, string purpose, int current = 2, List<int>? retained = null) =>
        options.Organizations[organization.ToString("D")] = new(StringComparer.Ordinal) { [purpose] = new KeyVersionOptions { CurrentVersion = current, RetainedVersions = retained ?? [1] } };

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

    /// <summary>(latest, min decryption, min encryption, auto-rotation period, exportable) of a key, read with the root token.</summary>
    private static async Task<(int Latest, int MinimumDecryption, int MinimumEncryption, string AutoRotate, bool Exportable)> KeyStateAsync(Guid organization, EncryptionKeyPurpose purpose)
    {
        using var info = await SendAsync(HttpMethod.Get, $"v1/{Mount}/keys/{KeyName(organization, purpose)}", RootToken!);
        Assert.Equal(HttpStatusCode.OK, info.StatusCode);
        var data = JsonDocument.Parse(await info.Content.ReadAsStringAsync(Ct)).RootElement.GetProperty("data");
        var autoRotate = data.GetProperty("auto_rotate_period");
        return (data.GetProperty("latest_version").GetInt32(), data.GetProperty("min_decryption_version").GetInt32(), data.GetProperty("min_encryption_version").GetInt32(),
            autoRotate.ValueKind == JsonValueKind.Number ? autoRotate.GetRawText() : autoRotate.GetString() ?? string.Empty, data.GetProperty("exportable").GetBoolean());
    }

    [Fact]
    public async Task TheVersionScriptProvisionedVersionTwoWithoutActivatingIt()
    {
        RequireVault();
        var organization = ActivationOrganization;

        // Vault has version 2 of exactly that key; nothing else changed, and auto-rotation is off.
        Assert.Equal((2, 1, 0, "0", false), await KeyStateAsync(organization, EncryptionKeyPurpose.FieldValue));
        foreach (var purpose in EncryptionKeyPurpose.All.Where(p => p != EncryptionKeyPurpose.FieldValue))
        {
            Assert.Equal(1, (await KeyStateAsync(organization, purpose)).Latest);
        }

        Assert.Equal(1, (await KeyStateAsync(ScriptOrganization, EncryptionKeyPurpose.FieldValue)).Latest);

        // Configuration has not activated it: wraps still use version 1 (the provider asks for version 1 and checks Vault used it).
        var keys = Keys();
        var dek = RandomNumberGenerator.GetBytes(32);
        var wrapped = await keys.WrapDataKeyAsync(Scope(organization, EncryptionKeyPurpose.FieldValue), dek, Ct);
        Assert.Equal(VaultTransitKeyProvider.Reference(Scope(organization, EncryptionKeyPurpose.FieldValue), 1), wrapped.KeyReference);
        Assert.Equal(dek, await keys.UnwrapDataKeyAsync(Scope(organization, EncryptionKeyPurpose.FieldValue), wrapped.KeyReference, wrapped.WrappedKeyBytes, Ct));

        // And Vault confirms the wrap is a version-1 ciphertext: it decrypts as vault:v1 with the root token.
        using var check = await SendAsync(HttpMethod.Post, $"v1/{Mount}/decrypt/{KeyName(organization, EncryptionKeyPurpose.FieldValue)}", RootToken!, new
        {
            ciphertext = "vault:v1:" + Convert.ToBase64String(wrapped.WrappedKeyBytes),
            associated_data = Convert.ToBase64String(VaultTransitKeyProvider.WrapAad(Scope(organization, EncryptionKeyPurpose.FieldValue), 1)),
        });
        Assert.Equal(HttpStatusCode.OK, check.StatusCode);
    }

    [Fact]
    public async Task ActivatingVersionTwoChangesOnlyThatOrganizationAndPurposeAndKeepsVersionOneReadable()
    {
        RequireVault();
        var organization = ActivationOrganization;
        var scope = Scope(organization, EncryptionKeyPurpose.FieldValue);
        var dek = RandomNumberGenerator.GetBytes(32);
        var v1 = await Keys().WrapDataKeyAsync(scope, dek, Ct);

        var activated = Keys(o => Activate(o, organization, "FieldValue"));
        var v2 = await activated.WrapDataKeyAsync(scope, dek, Ct);
        Assert.Equal(VaultTransitKeyProvider.Reference(scope, 2), v2.KeyReference);
        Assert.Equal(dek, await activated.UnwrapDataKeyAsync(scope, v2.KeyReference, v2.WrappedKeyBytes, Ct));
        Assert.Equal(dek, await activated.UnwrapDataKeyAsync(scope, v1.KeyReference, v1.WrappedKeyBytes, Ct)); // historical, exact version

        // Other purposes of the organization and other organizations stay on version 1.
        Assert.EndsWith(":document:1", (await activated.WrapDataKeyAsync(Scope(organization, EncryptionKeyPurpose.Document), dek, Ct)).KeyReference, StringComparison.Ordinal);
        Assert.EndsWith(":field-value:1", (await activated.WrapDataKeyAsync(Scope(ScriptOrganization, EncryptionKeyPurpose.FieldValue), dek, Ct)).KeyReference, StringComparison.Ordinal);

        // Version substitution in either direction fails authentication in Vault.
        await Assert.ThrowsAnyAsync<CryptographicException>(() => activated.UnwrapDataKeyAsync(scope, v1.KeyReference, v2.WrappedKeyBytes, Ct));
        await Assert.ThrowsAnyAsync<CryptographicException>(() => activated.UnwrapDataKeyAsync(scope, v2.KeyReference, v1.WrappedKeyBytes, Ct));
        // Cross-organization and cross-purpose use of the version-2 wrap is refused.
        await Assert.ThrowsAnyAsync<CryptographicException>(() => activated.UnwrapDataKeyAsync(Scope(ScriptOrganization, EncryptionKeyPurpose.FieldValue), v2.KeyReference, v2.WrappedKeyBytes, Ct));
        await Assert.ThrowsAnyAsync<CryptographicException>(() => activated.UnwrapDataKeyAsync(Scope(organization, EncryptionKeyPurpose.Document), v2.KeyReference, v2.WrappedKeyBytes, Ct));

        // Activating a version Vault does not have (the script organization has only version 1) fails closed; Vault gains no version.
        var unprovisioned = Keys(o => Activate(o, ScriptOrganization, "FieldValue"));
        await Assert.ThrowsAsync<EncryptionUnavailableException>(() => unprovisioned.WrapDataKeyAsync(Scope(ScriptOrganization, EncryptionKeyPurpose.FieldValue), dek, Ct));
        Assert.Equal(1, (await KeyStateAsync(ScriptOrganization, EncryptionKeyPurpose.FieldValue)).Latest);
    }

    [Fact]
    public async Task AnIncorrectActivationThatDropsVersionOneFailsClosedAndCorrectingItRecoversTheData()
    {
        RequireVault();
        var organization = ActivationOrganization;
        var scope = Scope(organization, EncryptionKeyPurpose.FieldValue);
        var dek = RandomNumberGenerator.GetBytes(32);
        var v1 = await Keys().WrapDataKeyAsync(scope, dek, Ct);

        var forgotten = Keys(o => Activate(o, organization, "FieldValue", retained: []));
        Assert.Equal("the key version is not retained", (await Assert.ThrowsAsync<EncryptionUnavailableException>(() => forgotten.UnwrapDataKeyAsync(scope, v1.KeyReference, v1.WrappedKeyBytes, Ct))).Message);
        var disabled = Keys(o => o.Organizations[organization.ToString("D")] = new(StringComparer.Ordinal) { ["FieldValue"] = new KeyVersionOptions { CurrentVersion = 2, DisabledVersions = [1] } });
        Assert.Equal("the key version is disabled", (await Assert.ThrowsAsync<EncryptionUnavailableException>(() => disabled.UnwrapDataKeyAsync(scope, v1.KeyReference, v1.WrappedKeyBytes, Ct))).Message);

        // Vault still holds version 1 (the application never trims or retires it); correcting the configuration recovers the data.
        Assert.Equal((2, 1), ((await KeyStateAsync(organization, EncryptionKeyPurpose.FieldValue)).Latest, (await KeyStateAsync(organization, EncryptionKeyPurpose.FieldValue)).MinimumDecryption));
        Assert.Equal(dek, await Keys(o => Activate(o, organization, "FieldValue")).UnwrapDataKeyAsync(scope, v1.KeyReference, v1.WrappedKeyBytes, Ct));
    }

    [Fact]
    public async Task TheBusinessTokenCannotProvisionActivateOrRetireVersions()
    {
        RequireVault();
        var organization = ActivationOrganization;
        var key = KeyName(organization, EncryptionKeyPurpose.FieldValue);
        var probes = new (HttpMethod Method, string Path, object? Body)[]
        {
            (HttpMethod.Post, $"v1/{Mount}/keys/{key}/rotate", new { }),                                 // provision a version
            (HttpMethod.Post, $"v1/{Mount}/keys/{key}/config", new { min_encryption_version = 2 }),      // force a version for encryption
            (HttpMethod.Post, $"v1/{Mount}/keys/{key}/config", new { min_decryption_version = 2 }),      // retire version 1
            (HttpMethod.Post, $"v1/{Mount}/keys/{key}/config", new { auto_rotate_period = "1h" }),       // enable automatic rotation
            (HttpMethod.Post, $"v1/{Mount}/keys/{key}/trim", new { min_available_version = 2 }),         // delete version 1
            (HttpMethod.Get, $"v1/{Mount}/keys/{key}", null),                                            // read versions
            (HttpMethod.Post, $"v1/{Mount}/rewrap/{key}", new { ciphertext = "vault:v1:AAAA" }),         // rewrap historical ciphertext
        };
        foreach (var (method, path, body) in probes)
        {
            using var response = await SendAsync(method, path, BusinessToken!, body);
            Assert.True(response.StatusCode == HttpStatusCode.Forbidden, path + " → " + (int)response.StatusCode);
        }

        // Nothing changed (root check).
        Assert.Equal((2, 1, 0, "0", false), await KeyStateAsync(organization, EncryptionKeyPurpose.FieldValue));
    }

    private sealed class PlainFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new SocketsHttpHandler { AllowAutoRedirect = false });
    }
}
