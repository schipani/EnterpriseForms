using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using AuthorizationAuthority.Application.Common.Ports;
using AuthorizationAuthority.Application.Features.SearchIndex;
using Microsoft.Extensions.Options;

namespace AuthorizationAuthority.Infrastructure.Signing;

/// <summary>
/// DEVELOPMENT/TEST ONLY: an in-process P-256 key loaded from configuration (a synthetic test key). Refused outside
/// Development/Testing by the host policy. Not a key custodian.
/// </summary>
public sealed class LocalSearchSealSigner : ISearchSealSigner, IDisposable
{
    private readonly ECDsa key;
    private readonly SearchSealOptions options;

    public LocalSearchSealSigner(SearchSealOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        this.options = options;
        if (string.IsNullOrWhiteSpace(options.LocalPrivateKeyPkcs8Base64))
        {
            throw new InvalidOperationException("SearchIndex:Seal:LocalPrivateKeyPkcs8Base64 is required for the LocalDevelopment seal provider.");
        }

        key = ECDsa.Create();
        key.ImportPkcs8PrivateKey(Convert.FromBase64String(options.LocalPrivateKeyPkcs8Base64), out _);
        if (key.ExportParameters(false).Curve.Oid?.Value != ECCurve.NamedCurves.nistP256.Oid.Value)
        {
            throw new InvalidOperationException("the local seal key is not a P-256 key");
        }
    }

    public string ProviderName => "LocalDevelopment";

    public string SigningKeyName => options.SigningKeyName;

    public int SigningKeyVersion => options.SigningKeyVersion;

    public Task<SearchSealSignature> SignAsync(ReadOnlyMemory<byte> message, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new SearchSealSignature(key.SignData(message.Span, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation), SigningKeyName, SigningKeyVersion));
    }

    public Task<byte[]> GetPublicKeyAsync(string keyName, int keyVersion, CancellationToken cancellationToken) =>
        keyName == SigningKeyName && keyVersion == SigningKeyVersion
            ? Task.FromResult(key.ExportSubjectPublicKeyInfo())
            : throw new SearchSealUnavailableException("the local seal provider holds one key version only");

    public void Dispose() => key.Dispose();
}

/// <summary>
/// HashiCorp Vault Transit adapter for the Docker stacks: a non-exportable <c>ecdsa-p256</c> key, signed with
/// <c>sha2-256</c> and the <c>jws</c> marshaling (raw r || s, base64url). Any HTTP or format failure fails closed.
/// Vault dev mode is in-memory and insecure; it is forbidden in Staging/Production by the host policy.
/// </summary>
public sealed class VaultTransitSearchSealSigner(IHttpClientFactory httpClientFactory, IOptions<VaultTransitOptions> vault, SearchSealOptions options) : ISearchSealSigner
{
    public string ProviderName => "VaultTransit";

    public string SigningKeyName => options.SigningKeyName;

    public int SigningKeyVersion => options.SigningKeyVersion;

    public async Task<SearchSealSignature> SignAsync(ReadOnlyMemory<byte> message, CancellationToken cancellationToken)
    {
        using var document = await SendAsync(HttpMethod.Post, $"v1/transit/sign/{Uri.EscapeDataString(SigningKeyName)}/sha2-256",
            new { input = Convert.ToBase64String(message.Span), key_version = SigningKeyVersion, marshaling_algorithm = "jws" }, cancellationToken).ConfigureAwait(false);
        var token = document.RootElement.GetProperty("data").GetProperty("signature").GetString() ?? throw new SearchSealUnavailableException("Vault returned no signature");
        var prefix = $"vault:v{SigningKeyVersion.ToString(System.Globalization.CultureInfo.InvariantCulture)}:";
        if (!token.StartsWith(prefix, StringComparison.Ordinal))
        {
            throw new SearchSealUnavailableException("Vault signed with an unexpected key version");
        }

        var raw = Base64UrlDecode(token[prefix.Length..]);
        if (raw.Length != 64)
        {
            throw new SearchSealUnavailableException("Vault returned a signature that is not a 64-byte P1363 ES256 signature");
        }

        return new SearchSealSignature(raw, SigningKeyName, SigningKeyVersion);
    }

    public async Task<byte[]> GetPublicKeyAsync(string keyName, int keyVersion, CancellationToken cancellationToken)
    {
        using var document = await SendAsync(HttpMethod.Get, $"v1/transit/keys/{Uri.EscapeDataString(keyName)}", null, cancellationToken).ConfigureAwait(false);
        var data = document.RootElement.GetProperty("data");
        if (data.GetProperty("type").GetString() != "ecdsa-p256" || (data.TryGetProperty("exportable", out var exportable) && exportable.GetBoolean())
            || (data.TryGetProperty("allow_plaintext_backup", out var backup) && backup.GetBoolean()))
        {
            throw new SearchSealUnavailableException("the Vault seal key is not a non-exportable ecdsa-p256 key");
        }

        var pem = data.GetProperty("keys").GetProperty(keyVersion.ToString(System.Globalization.CultureInfo.InvariantCulture)).GetProperty("public_key").GetString()
                  ?? throw new SearchSealUnavailableException("Vault returned no public key");
        using var key = ECDsa.Create();
        key.ImportFromPem(pem);
        return key.ExportSubjectPublicKeyInfo();
    }

    private async Task<JsonDocument> SendAsync(HttpMethod method, string path, object? body, CancellationToken cancellationToken)
    {
        try
        {
            var client = httpClientFactory.CreateClient(VaultTransitHmacProvider.HttpClientName);
            client.Timeout = TimeSpan.FromSeconds(Math.Max(1, vault.Value.TimeoutSeconds));
            using var request = new HttpRequestMessage(method, new Uri(path, UriKind.Relative));
            request.Headers.Add("X-Vault-Token", vault.Value.Token ?? throw new SearchSealUnavailableException("Vault token not configured"));
            if (body is not null)
            {
                request.Content = JsonContent.Create(body);
            }

            using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                throw new SearchSealUnavailableException("the Vault seal key does not exist");
            }

            if (!response.IsSuccessStatusCode)
            {
                throw new SearchSealUnavailableException($"Vault returned {(int)response.StatusCode} for the seal key");
            }

            return await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false), cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new SearchSealUnavailableException("Vault unreachable", ex);
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new SearchSealUnavailableException("Vault timeout", ex);
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or CryptographicException or ArgumentException)
        {
            throw new SearchSealUnavailableException("Vault response unreadable", ex);
        }
    }

    private static byte[] Base64UrlDecode(string value)
    {
        var padded = value.Replace('-', '+').Replace('_', '/');
        padded = padded.PadRight(padded.Length + ((4 - (padded.Length % 4)) % 4), '=');
        try
        {
            return Convert.FromBase64String(padded);
        }
        catch (FormatException ex)
        {
            throw new SearchSealUnavailableException("Vault signature is not base64url", ex);
        }
    }
}

/// <summary>Registered when no approved seal provider is usable (unconfigured, or Managed HSM which IX-1 does not implement): every call fails closed.</summary>
public sealed class UnavailableSearchSealSigner(string reason, SearchSealOptions options) : ISearchSealSigner
{
    public string ProviderName => "Unavailable";

    public string SigningKeyName => options.SigningKeyName;

    public int SigningKeyVersion => options.SigningKeyVersion;

    public Task<SearchSealSignature> SignAsync(ReadOnlyMemory<byte> message, CancellationToken cancellationToken) => throw new SearchSealUnavailableException(reason);

    public Task<byte[]> GetPublicKeyAsync(string keyName, int keyVersion, CancellationToken cancellationToken) => throw new SearchSealUnavailableException(reason);
}
