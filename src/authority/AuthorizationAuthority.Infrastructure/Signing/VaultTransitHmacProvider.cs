using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using AuthorizationAuthority.Application.Common.Ports;
using AuthorizationAuthority.Domain.Features.Keys;
using Microsoft.Extensions.Options;

namespace AuthorizationAuthority.Infrastructure.Signing;

public sealed class VaultTransitOptions
{
    public const string SectionName = "Signing:VaultTransit";

    /// <summary>Loaded from the authority's secret configuration (environment), never from committed JSON.</summary>
    public string? Token { get; set; }

    public int TimeoutSeconds { get; set; } = 5;
}

/// <summary>
/// HashiCorp Vault Transit adapter for the local Docker integration stack (CRYPTOGRAPHIC_SIGNING_AUTHORITY.md §7).
/// Keys are <c>type=hmac</c>, non-exportable, named <c>org-&lt;compact uuid&gt;-&lt;purpose&gt;</c> by the authority (never from
/// caller input). Vault's <c>vault:vN:&lt;base64&gt;</c> token is normalized to the raw 32-byte tag so every provider
/// produces identical bytes for identical canonical input. Any HTTP failure fails closed; there is no local fallback.
/// Vault dev mode is in-memory and insecure; it is forbidden in Staging/Production by the host policy.
/// </summary>
public sealed class VaultTransitHmacProvider(IHttpClientFactory httpClientFactory, IOptions<VaultTransitOptions> options) : IIntegrityMacProvider
{
    public const string HttpClientName = "vault-transit";

    public string ProviderName => "VaultTransit";

    public async Task<IntegrityMacResult> SignAsync(IntegrityKeyReference keyReference, ReadOnlyMemory<byte> canonicalPayload, CancellationToken cancellationToken)
    {
        ValidateReference(keyReference);
        using var document = await PostAsync($"v1/transit/hmac/{Uri.EscapeDataString(keyReference.ProviderKeyName)}/sha2-256",
            new { input = Convert.ToBase64String(canonicalPayload.Span), key_version = keyReference.KeyVersion }, cancellationToken).ConfigureAwait(false);
        var token = document.RootElement.GetProperty("data").GetProperty("hmac").GetString() ?? throw new IntegrityProviderUnavailableException("Vault returned no hmac");
        return new IntegrityMacResult(ExtractRawMac(token, keyReference.KeyVersion), keyReference.KeyVersion, "HMAC-SHA-256");
    }

    public async Task<bool> VerifyAsync(IntegrityKeyReference keyReference, ReadOnlyMemory<byte> canonicalPayload, ReadOnlyMemory<byte> expectedMac, CancellationToken cancellationToken)
    {
        ValidateReference(keyReference);
        if (expectedMac.Length != 32)
        {
            return false;
        }

        var token = $"vault:v{keyReference.KeyVersion.ToString(CultureInfo.InvariantCulture)}:{Convert.ToBase64String(expectedMac.Span)}";
        using var document = await PostAsync($"v1/transit/verify/{Uri.EscapeDataString(keyReference.ProviderKeyName)}/sha2-256",
            new { input = Convert.ToBase64String(canonicalPayload.Span), hmac = token }, cancellationToken).ConfigureAwait(false);
        return document.RootElement.GetProperty("data").GetProperty("valid").GetBoolean();
    }

    public async Task<int> ProvisionKeyAsync(Guid organizationId, string keyPurpose, string providerKeyName, CancellationToken cancellationToken)
    {
        if (organizationId == Guid.Empty || !IntegrityKeyPurposes.IsAllowed(keyPurpose) || providerKeyName != IntegrityKeyNaming.ProviderKeyName(organizationId, keyPurpose))
        {
            throw new IntegrityProviderUnavailableException("invalid key provisioning request");
        }

        // Idempotent: creating an existing key is a no-op in Vault; the key is non-exportable HMAC material.
        using (await PostAsync($"v1/transit/keys/{Uri.EscapeDataString(providerKeyName)}", new { type = "hmac", exportable = false, key_size = 32 }, cancellationToken).ConfigureAwait(false))
        {
        }

        using var read = await GetAsync($"v1/transit/keys/{Uri.EscapeDataString(providerKeyName)}", cancellationToken).ConfigureAwait(false);
        var data = read.RootElement.GetProperty("data");
        if (data.TryGetProperty("exportable", out var exportable) && exportable.GetBoolean())
        {
            throw new IntegrityProviderUnavailableException("key is exportable; refusing to use it");
        }

        return data.GetProperty("latest_version").GetInt32();
    }

    public async Task<(bool Ready, string Detail)> ProbeAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var client = CreateClient();
            using var response = await client.GetAsync(new Uri("v1/sys/health", UriKind.Relative), cancellationToken).ConfigureAwait(false);
            return response.IsSuccessStatusCode ? (true, "VaultTransit reachable (DEV ONLY)") : (false, "VaultUnhealthy:" + (int)response.StatusCode);
        }
        catch (HttpRequestException)
        {
            return (false, "VaultUnreachable");
        }
        catch (TaskCanceledException)
        {
            return (false, "VaultTimeout");
        }
    }

    private HttpClient CreateClient()
    {
        var client = httpClientFactory.CreateClient(HttpClientName);
        client.Timeout = TimeSpan.FromSeconds(Math.Max(1, options.Value.TimeoutSeconds));
        return client;
    }

    private async Task<JsonDocument> PostAsync(string path, object body, CancellationToken cancellationToken)
    {
        try
        {
            using var client = CreateClient();
            using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(path, UriKind.Relative));
            request.Headers.Add("X-Vault-Token", options.Value.Token ?? throw new IntegrityProviderUnavailableException("Vault token not configured"));
            request.Content = JsonContent.Create(body);
            using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NoContent)
            {
                return JsonDocument.Parse("{}");
            }

            if (!response.IsSuccessStatusCode)
            {
                throw new IntegrityProviderUnavailableException($"Vault returned {(int)response.StatusCode} for {path.Split('/')[2]}");
            }

            return await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false), cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new IntegrityProviderUnavailableException("Vault unreachable", ex);
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new IntegrityProviderUnavailableException("Vault timeout", ex);
        }
        catch (JsonException ex)
        {
            throw new IntegrityProviderUnavailableException("Vault response unreadable", ex);
        }
    }

    private async Task<JsonDocument> GetAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            using var client = CreateClient();
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(path, UriKind.Relative));
            request.Headers.Add("X-Vault-Token", options.Value.Token ?? throw new IntegrityProviderUnavailableException("Vault token not configured"));
            using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw new IntegrityProviderUnavailableException($"Vault returned {(int)response.StatusCode}");
            }

            return await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false), cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new IntegrityProviderUnavailableException("Vault unreachable", ex);
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new IntegrityProviderUnavailableException("Vault timeout", ex);
        }
    }

    private static void ValidateReference(IntegrityKeyReference reference)
    {
        if (reference.OrganizationId == Guid.Empty || !IntegrityKeyPurposes.IsAllowed(reference.KeyPurpose) || reference.KeyVersion <= 0
            || reference.ProviderKeyName != IntegrityKeyNaming.ProviderKeyName(reference.OrganizationId, reference.KeyPurpose))
        {
            throw new CryptographicException("Unrecognized Vault integrity key reference.");
        }
    }

    private static byte[] ExtractRawMac(string hmacToken, int expectedVersion)
    {
        var parts = hmacToken.Split(':', 3);
        if (parts.Length != 3 || parts[0] != "vault" || parts[1] != "v" + expectedVersion.ToString(CultureInfo.InvariantCulture))
        {
            throw new IntegrityProviderUnavailableException("Vault returned an unexpected key version");
        }

        var mac = Convert.FromBase64String(parts[2]);
        return mac.Length == 32 ? mac : throw new IntegrityProviderUnavailableException("invalid HMAC length");
    }
}
