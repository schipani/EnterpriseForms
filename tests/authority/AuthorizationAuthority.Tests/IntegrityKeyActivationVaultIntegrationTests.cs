using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AuthorizationAuthority.Application.Common;
using AuthorizationAuthority.Application.Common.Ports;
using AuthorizationAuthority.Application.Features.Keys;
using AuthorizationAuthority.Domain.Features.Keys;
using AuthorizationAuthority.Domain.Features.Organizations;
using AuthorizationAuthority.Infrastructure.Signing;
using Microsoft.Extensions.Options;
using Xunit;

namespace AuthorizationAuthority.Tests;

/// <summary>
/// E-5 targeted real-Vault test: the Authority's <see cref="VaultTransitHmacProvider"/> and <see cref="IntegrityKeyActivationService"/>
/// against a REAL HashiCorp Vault 1.20.4 dev server. Run only by <c>scripts/dev/e5-vault-hmac-integration.ps1</c>, which creates the
/// organization keys as key custody does (root token), gives the Authority a token with the development stack's onboarding policy
/// (read/hmac/verify on org-* only), provisions version 2 of the first organization's key with provision-integrity-key-version.sh
/// and checks that script's refusals. Skipped without the E5_VAULT_* variables. The root token is used only to inspect keys and,
/// in one test, to prepare a separate throwaway key (operator actions the Authority never performs).
/// </summary>
public sealed class IntegrityKeyActivationVaultIntegrationTests
{
    private static readonly string? Address = Environment.GetEnvironmentVariable("E5_VAULT_ADDRESS");
    private static readonly string? RootToken = Environment.GetEnvironmentVariable("E5_VAULT_ROOT_TOKEN");
    private static readonly string? AuthorityToken = Environment.GetEnvironmentVariable("E5_VAULT_AUTHORITY_TOKEN");
    private static readonly string? RotatedOrganizationText = Environment.GetEnvironmentVariable("E5_VAULT_ROTATED_ORGANIZATION");
    private static readonly string? SingleVersionOrganizationText = Environment.GetEnvironmentVariable("E5_VAULT_SINGLE_VERSION_ORGANIZATION");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static Guid Rotated => Guid.Parse(RotatedOrganizationText!);

    private static Guid SingleVersion => Guid.Parse(SingleVersionOrganizationText!);

    private static void RequireVault() => Assert.SkipWhen(
        new[] { Address, RootToken, AuthorityToken, RotatedOrganizationText, SingleVersionOrganizationText }.Any(string.IsNullOrEmpty),
        "E5_VAULT_* not set (run scripts/dev/e5-vault-hmac-integration.ps1)");

    private static string KeyName(Guid organization) => IntegrityKeyNaming.ProviderKeyName(organization, IntegrityKeyPurposes.RowIntegrityV1);

    private static IntegrityKeyReference Reference(Guid organization, int version) => new(organization, IntegrityKeyPurposes.RowIntegrityV1, KeyName(organization), version);

    private static VaultTransitHmacProvider Provider(string? token = null) =>
        new(new Factory(), Options.Create(new VaultTransitOptions { Token = token ?? AuthorityToken, TimeoutSeconds = 5 }));

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

    private static async Task<(int Latest, int MinimumDecryption, string AutoRotate, bool Exportable)> KeyStateAsync(string name)
    {
        using var info = await SendAsync(HttpMethod.Get, $"v1/transit/keys/{name}", RootToken!);
        Assert.Equal(HttpStatusCode.OK, info.StatusCode);
        var data = JsonDocument.Parse(await info.Content.ReadAsStringAsync(Ct)).RootElement.GetProperty("data");
        var autoRotate = data.GetProperty("auto_rotate_period");
        return (data.GetProperty("latest_version").GetInt32(), data.GetProperty("min_decryption_version").GetInt32(),
            autoRotate.ValueKind == JsonValueKind.Number ? autoRotate.GetRawText() : autoRotate.GetString() ?? string.Empty, data.GetProperty("exportable").GetBoolean());
    }

    /// <summary>An in-memory Authority catalog holding the organization's registration at version 1, as onboarding left it.</summary>
    private static (InMemoryAuthorityStore Store, IntegrityKeyRegistration Key) Catalog(Guid organization)
    {
        var store = new InMemoryAuthorityStore();
        store.Organizations.Add(new OrganizationAuthorizationState { OrganizationId = organization, AuthorizationRevision = 1, ProjectionAppliedRevision = 1, CheckpointEnforcementModeName = CheckpointEnforcementModes.Asynchronous }.WithJournalDigest());
        var key = new IntegrityKeyRegistration { IntegrityKeyRegistrationId = Guid.CreateVersion7(), OrganizationId = organization, ProviderName = "VaultTransit", ProviderKeyName = KeyName(organization), CurrentSigningKeyVersion = 1, MinimumVerificationKeyVersion = 1 };
        store.Keys.Add(key);
        return (store, key);
    }

    [Fact]
    public async Task AScriptProvisionedVersionIsUnusedUntilActivatedAndBothVersionsVerifyOnlyAsThemselves()
    {
        RequireVault();
        Assert.Equal((2, 1, "0", false), await KeyStateAsync(KeyName(Rotated)));
        var provider = Provider();
        var inspection = await provider.InspectKeyAsync(KeyName(Rotated), Ct);
        Assert.Equal((true, true, 2), (inspection.Exists, inspection.Usable, inspection.LatestVersion));

        // Signing is version-pinned: the registration still says 1, so a v1 MAC is produced although Vault's latest is 2.
        var payload = "DynamicForms/E5/real-vault payload"u8.ToArray();
        var v1 = await provider.SignAsync(Reference(Rotated, 1), payload, Ct);
        Assert.Equal(1, v1.KeyVersion);

        var (store, key) = Catalog(Rotated);
        var activation = new IntegrityKeyActivationService(store, provider, TimeProvider.System);
        var result = await activation.ActivateAsync(Rotated, IntegrityKeyPurposes.RowIntegrityV1, 1, 2, "e5-vault-test", "real Vault activation", Guid.CreateVersion7(), Ct);
        Assert.Equal((1, 2), (result.PreviousSigningKeyVersion, key.CurrentSigningKeyVersion));
        Assert.Single(store.Journal, j => j.JournalEventKind == IntegrityKeyActivationService.ActivatedEventKind);

        var v2 = await provider.SignAsync(Reference(Rotated, key.CurrentSigningKeyVersion), payload, Ct);
        Assert.Equal(2, v2.KeyVersion);
        Assert.NotEqual(v1.Mac, v2.Mac);
        Assert.True(await provider.VerifyAsync(Reference(Rotated, 1), payload, v1.Mac, Ct));   // historical, exact version
        Assert.True(await provider.VerifyAsync(Reference(Rotated, 2), payload, v2.Mac, Ct));
        Assert.False(await provider.VerifyAsync(Reference(Rotated, 2), payload, v1.Mac, Ct));  // never substituted
        Assert.False(await provider.VerifyAsync(Reference(Rotated, 1), payload, v2.Mac, Ct));
        Assert.Equal((2, 1, "0", false), await KeyStateAsync(KeyName(Rotated))); // activation changed nothing in Vault
    }

    [Fact]
    public async Task ActivatingAVersionVaultDoesNotHaveIsRefusedAndKeysDoNotCrossOrganizations()
    {
        RequireVault();
        var provider = Provider();
        var (store, key) = Catalog(SingleVersion);
        var refused = await Assert.ThrowsAsync<AuthorityException>(() => new IntegrityKeyActivationService(store, provider, TimeProvider.System)
            .ActivateAsync(SingleVersion, IntegrityKeyPurposes.RowIntegrityV1, 1, 2, "e5-vault-test", "unprovisioned", Guid.CreateVersion7(), Ct));
        Assert.Equal(AuthorityErrorCodes.OrganizationKeyNotProvisioned, refused.ErrorCode);
        Assert.Equal(1, key.CurrentSigningKeyVersion);
        Assert.Empty(store.Journal);
        Assert.Equal(1, (await KeyStateAsync(KeyName(SingleVersion))).Latest);

        // Signing with a version Vault does not have fails closed (no "latest" fallback).
        var payload = "cross-organization"u8.ToArray();
        await Assert.ThrowsAsync<IntegrityProviderUnavailableException>(() => provider.SignAsync(Reference(SingleVersion, 2), payload, Ct));

        // The rotated organization's v2 MAC is not valid under the other organization's key: its v1 says no, its missing v2 refuses.
        var foreign = await provider.SignAsync(Reference(Rotated, 2), payload, Ct);
        Assert.False(await provider.VerifyAsync(Reference(SingleVersion, 1), payload, foreign.Mac, Ct));
        await Assert.ThrowsAsync<IntegrityProviderUnavailableException>(() => provider.VerifyAsync(Reference(SingleVersion, 2), payload, foreign.Mac, Ct));

        // A key reference that does not match the organization is refused before Vault is called.
        await Assert.ThrowsAnyAsync<System.Security.Cryptography.CryptographicException>(() => provider.VerifyAsync(Reference(SingleVersion, 1) with { ProviderKeyName = KeyName(Rotated) }, payload, foreign.Mac, Ct));
    }

    [Fact]
    public async Task ARetiredOrMissingHistoricalVersionFailsClosedAndTheActiveVersionIsNeverTriedInstead()
    {
        RequireVault();
        // Fixture (root, operator action): a throwaway organization key with versions 1 and 2.
        var organization = Guid.CreateVersion7();
        using (var create = await SendAsync(HttpMethod.Post, $"v1/transit/keys/{KeyName(organization)}", RootToken!, new { type = "hmac", key_size = 32, exportable = false, allow_plaintext_backup = false }))
        {
            Assert.True(create.IsSuccessStatusCode);
        }

        using (var rotate = await SendAsync(HttpMethod.Post, $"v1/transit/keys/{KeyName(organization)}/rotate", RootToken!, new { }))
        {
            Assert.True(rotate.IsSuccessStatusCode);
        }

        var provider = Provider();
        var payload = "historical"u8.ToArray();
        var v1 = await provider.SignAsync(Reference(organization, 1), payload, Ct);
        var v2 = await provider.SignAsync(Reference(organization, 2), payload, Ct);

        // A version that never existed: refused by Vault, reported as unavailable (never valid, never another version).
        await Assert.ThrowsAsync<IntegrityProviderUnavailableException>(() => provider.VerifyAsync(Reference(organization, 5), payload, v2.Mac, Ct));

        // Version 1 retired in Vault (operator raises min_decryption_version): its evidence fails closed; version 2 is unaffected.
        using (var retire = await SendAsync(HttpMethod.Post, $"v1/transit/keys/{KeyName(organization)}/config", RootToken!, new { min_decryption_version = 2 }))
        {
            Assert.True(retire.IsSuccessStatusCode);
        }

        await Assert.ThrowsAsync<IntegrityProviderUnavailableException>(() => provider.VerifyAsync(Reference(organization, 1), payload, v1.Mac, Ct));
        Assert.True(await provider.VerifyAsync(Reference(organization, 2), payload, v2.Mac, Ct));

        // Undoing the retirement (operator) restores verification of the untouched evidence.
        using (var restore = await SendAsync(HttpMethod.Post, $"v1/transit/keys/{KeyName(organization)}/config", RootToken!, new { min_decryption_version = 1 }))
        {
            Assert.True(restore.IsSuccessStatusCode);
        }

        Assert.True(await provider.VerifyAsync(Reference(organization, 1), payload, v1.Mac, Ct));

        // Cancellation propagates as cancellation, not as an outage.
        using var source = new CancellationTokenSource();
        await source.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => provider.VerifyAsync(Reference(organization, 1), payload, v1.Mac, source.Token));
    }

    [Fact]
    public async Task TheAuthorityTokenCanOnlyReadSignAndVerifyAndCannotProvisionActivateOrRetireVersions()
    {
        RequireVault();
        var key = KeyName(Rotated);
        var probes = new (HttpMethod Method, string Path, object? Body)[]
        {
            (HttpMethod.Post, $"v1/transit/keys/{key}/rotate", new { }),                            // provision a version
            (HttpMethod.Post, $"v1/transit/keys/{key}/config", new { min_decryption_version = 2 }),  // retire a version
            (HttpMethod.Post, $"v1/transit/keys/{key}/config", new { min_encryption_version = 2 }),
            (HttpMethod.Post, $"v1/transit/keys/{key}/config", new { auto_rotate_period = "1h" }),   // automatic rotation
            (HttpMethod.Post, $"v1/transit/keys/{key}/config", new { deletion_allowed = true }),
            (HttpMethod.Post, $"v1/transit/keys/{key}/trim", new { min_available_version = 2 }),     // delete a version
            (HttpMethod.Post, $"v1/transit/keys/{KeyName(Guid.CreateVersion7())}", new { type = "hmac", key_size = 32 }), // create
            (HttpMethod.Delete, $"v1/transit/keys/{key}", null),
            (HttpMethod.Get, $"v1/transit/export/hmac-key/{key}", null),
            (HttpMethod.Get, $"v1/transit/backup/{key}", null),
            (HttpMethod.Get, "v1/sys/mounts", null),
        };
        foreach (var (method, path, body) in probes)
        {
            using var response = await SendAsync(method, path, AuthorityToken!, body);
            Assert.True(response.StatusCode == HttpStatusCode.Forbidden, path + " → " + (int)response.StatusCode);
        }

        Assert.Equal((2, 1, "0", false), await KeyStateAsync(key)); // nothing changed

        // A provider that holds the Authority token still signs and verifies (the token is not merely broken).
        var mac = await Provider().SignAsync(Reference(Rotated, 1), "ok"u8.ToArray(), Ct);
        Assert.True(await Provider().VerifyAsync(Reference(Rotated, 1), "ok"u8.ToArray(), mac.Mac, Ct));
        await Assert.ThrowsAsync<IntegrityProviderUnavailableException>(() => Provider("hvs.not-a-real-token").SignAsync(Reference(Rotated, 1), "ok"u8.ToArray(), Ct));
    }

    private sealed class Factory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new SocketsHttpHandler { AllowAutoRedirect = false }) { BaseAddress = new Uri(Address!) };
    }
}
