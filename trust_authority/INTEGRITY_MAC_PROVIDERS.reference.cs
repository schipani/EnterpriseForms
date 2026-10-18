// REFERENCE CODE ONLY. .NET 10. Adapt to DI, logging, tests, and independently protected key catalog.
// NEVER register these providers in the business API and NEVER expose generic sign endpoints.
using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace DynamicForms.TrustAuthority.Crypto;

public sealed record IntegrityKeyReference(Guid OrganizationId, string KeyPurpose, string KeyVersion);
public sealed record IntegrityMacResult(byte[] Mac, string KeyVersion, string Algorithm);

public interface IIntegrityMacProvider
{
    Task<IntegrityMacResult> SignAsync(
        IntegrityKeyReference key, ReadOnlyMemory<byte> message, CancellationToken cancellationToken);
    Task<bool> VerifyAsync(
        IntegrityKeyReference key, ReadOnlyMemory<byte> message,
        ReadOnlyMemory<byte> expectedMac, CancellationToken cancellationToken);
}

// Development ONLY. Keys are generated on first use and disappear when process restarts.
// NEVER load this class in production; this is not a secure key custodian.
public sealed class LocalHmacProvider : IIntegrityMacProvider
{
    private readonly ConcurrentDictionary<(Guid OrganizationId, string Purpose), byte[]> _keys = new();

    public Task<IntegrityMacResult> SignAsync(
        IntegrityKeyReference key, ReadOnlyMemory<byte> message, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidateKey(key);
        byte[] secret = _keys.GetOrAdd((key.OrganizationId, key.KeyPurpose), _ => RandomNumberGenerator.GetBytes(32));
        byte[] mac = HMACSHA256.HashData(secret, message.Span);
        return Task.FromResult(new IntegrityMacResult(mac, key.KeyVersion, "HMAC-SHA-256"));
    }

    public Task<bool> VerifyAsync(
        IntegrityKeyReference key, ReadOnlyMemory<byte> message,
        ReadOnlyMemory<byte> expectedMac, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidateKey(key);
        if (expectedMac.Length != 32 || !_keys.TryGetValue((key.OrganizationId, key.KeyPurpose), out var secret))
            return Task.FromResult(false);
        byte[] actualMac = HMACSHA256.HashData(secret, message.Span);
        return Task.FromResult(CryptographicOperations.FixedTimeEquals(actualMac, expectedMac.Span));
    }

    private static void ValidateKey(IntegrityKeyReference key)
    {
        if (key.OrganizationId == Guid.Empty || key.KeyVersion != "dev-v1" ||
            !IntegrityPurposes.IsAllowed(key.KeyPurpose))
            throw new CryptographicException("Unrecognized development integrity key reference.");
    }
}

internal static class IntegrityPurposes
{
    public static bool IsAllowed(string purpose) => purpose is
        "row-integrity-v1" or "manifest-integrity-v1" or "security-checkpoint-v1";
}

// Local Docker development ONLY. Instantiate HttpClient with 127.0.0.1:8200 base address.
// Vault dev root token MUST be loaded from development-only secret settings, not checked-in config.
public sealed class VaultTransitHmacProvider(HttpClient httpClient, string devToken) : IIntegrityMacProvider
{
    public async Task<IntegrityMacResult> SignAsync(
        IntegrityKeyReference key, ReadOnlyMemory<byte> message, CancellationToken cancellationToken)
    {
        int version = ValidateAndGetVersion(key);
        string path = GetPath(key, "hmac");
        using var response = await PostJsonAsync(path,
            new { input = Convert.ToBase64String(message.Span), key_version = version }, cancellationToken);
        using JsonDocument parsed = await JsonDocument.ParseAsync(
            await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
        string hmacToken = parsed.RootElement.GetProperty("data").GetProperty("hmac").GetString()
            ?? throw new CryptographicException("Missing Vault HMAC.");
        byte[] mac = ExtractRawMac(hmacToken, version);
        return new IntegrityMacResult(mac, key.KeyVersion, "HMAC-SHA-256");
    }

    public async Task<bool> VerifyAsync(
        IntegrityKeyReference key, ReadOnlyMemory<byte> message,
        ReadOnlyMemory<byte> expectedMac, CancellationToken cancellationToken)
    {
        int version = ValidateAndGetVersion(key);
        if (expectedMac.Length != 32) return false;
        string hmacToken = $"vault:v{version}:{Convert.ToBase64String(expectedMac.Span)}";
        using var response = await PostJsonAsync(GetPath(key, "verify"),
            new { input = Convert.ToBase64String(message.Span), hmac = hmacToken }, cancellationToken);
        using JsonDocument parsed = await JsonDocument.ParseAsync(
            await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
        return parsed.RootElement.GetProperty("data").GetProperty("valid").GetBoolean();
    }

    private async Task<HttpResponseMessage> PostJsonAsync(
        string path, object body, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path);
        request.Headers.Add("X-Vault-Token", devToken);
        request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        HttpResponseMessage response = await httpClient.SendAsync(request, cancellationToken);
        try { response.EnsureSuccessStatusCode(); return response; }
        catch { response.Dispose(); throw; } // Fail closed. No fallback to LocalHmacProvider.
    }

    private static string GetPath(IntegrityKeyReference key, string action)
    {
        string compactId = key.OrganizationId.ToString("N", CultureInfo.InvariantCulture);
        string name = $"org-{compactId}-{key.KeyPurpose}";
        return $"v1/transit/{action}/{Uri.EscapeDataString(name)}/sha2-256";
    }

    private static int ValidateAndGetVersion(IntegrityKeyReference key)
    {
        if (key.OrganizationId == Guid.Empty || !IntegrityPurposes.IsAllowed(key.KeyPurpose) ||
            !int.TryParse(key.KeyVersion, NumberStyles.None, CultureInfo.InvariantCulture, out int version) ||
            version <= 0)
            throw new CryptographicException("Unrecognized Vault integrity key reference.");
        return version;
    }

    private static byte[] ExtractRawMac(string hmacToken, int expectedVersion)
    {
        string[] parts = hmacToken.Split(':', 3);
        if (parts.Length != 3 || parts[0] != "vault" || parts[1] != $"v{expectedVersion}")
            throw new CryptographicException("Vault returned an unexpected key version.");
        byte[] mac = Convert.FromBase64String(parts[2]);
        if (mac.Length != 32) throw new CryptographicException("Invalid HMAC-SHA-256 length.");
        return mac;
    }
}
