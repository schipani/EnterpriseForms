using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using AuthorizationAuthority.Application.Common.Ports;
using AuthorizationAuthority.Application.Features.SearchIndex;
using Azure.Core;

namespace AuthorizationAuthority.Infrastructure.Signing;

/// <summary>IX-7 (D-IX1-13): configuration of the Azure Managed HSM seal provider, section <c>SearchIndex:Seal:ManagedHsm</c>.</summary>
public sealed class ManagedHsmSealOptions
{
    public const string SectionName = "SearchIndex:Seal:ManagedHsm";

    /// <summary>The Managed HSM endpoint, e.g. <c>https://contoso-hsm.managedhsm.azure.net/</c> (HTTPS only).</summary>
    public string? VaultBaseUrl { get; set; }

    /// <summary>
    /// The HSM key version identifier of each seal key version the deployment uses (key: <c>SearchIndex:Seal:SigningKeyVersion</c> and
    /// the pinned versions; value: the 32-character Managed HSM version id). Deployment configuration only.
    /// </summary>
    public Dictionary<string, string> KeyVersionIdentifiers { get; set; } = new(StringComparer.Ordinal);

    /// <summary>The client id of a user-assigned managed identity; empty: the system-assigned identity. No secret is ever configured.</summary>
    public string? ManagedIdentityClientId { get; set; }

    public int TimeoutSeconds { get; set; } = 10;

    public const string ApiVersion = "7.4";

    public const string TokenScope = "https://managedhsm.azure.net/.default";

    /// <summary>Startup validation (fail closed): an HTTPS endpoint and a version identifier for the signing version and every pinned version.</summary>
    public void Validate(SearchSealOptions seal)
    {
        ArgumentNullException.ThrowIfNull(seal);
        if (string.IsNullOrWhiteSpace(VaultBaseUrl) || !Uri.TryCreate(VaultBaseUrl, UriKind.Absolute, out var url) || url.Scheme != Uri.UriSchemeHttps)
        {
            throw new InvalidOperationException("SearchIndex:Seal:ManagedHsm:VaultBaseUrl must be the HTTPS endpoint of the Managed HSM.");
        }

        foreach (var version in seal.PinnedKeys.Where(k => k.KeyName == seal.SigningKeyName).Select(k => k.KeyVersion).Append(seal.SigningKeyVersion).Distinct())
        {
            var text = version.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (!KeyVersionIdentifiers.TryGetValue(text, out var identifier) || string.IsNullOrEmpty(identifier) || identifier.Length > 64 || !identifier.All(char.IsAsciiLetterOrDigit))
            {
                throw new InvalidOperationException($"SearchIndex:Seal:ManagedHsm:KeyVersionIdentifiers must map seal key version {text} to its Managed HSM version identifier.");
            }
        }

        if (TimeoutSeconds is < 1 or > 60)
        {
            throw new InvalidOperationException("SearchIndex:Seal:ManagedHsm:TimeoutSeconds must be between 1 and 60.");
        }
    }
}

/// <summary>
/// IX-7 (D-IX1-13): the production seal signer. An Azure Managed HSM <c>EC-HSM</c> P-256 key, non-exportable, signs the SHA-256 digest
/// of the canonical seal message with <c>ES256</c> through the Managed HSM REST API (key operation <c>sign</c>), authenticated with the
/// Authority's managed identity (bearer token for <c>https://managedhsm.azure.net</c>; no secret in configuration). The signature is the
/// 64-byte P1363 r || s the local verifier checks against the pinned public key (<c>SearchSealSigningGate</c> verifies every signature
/// before it is used). Every failure — unreachable, throttled (429), unauthorized, a wrong key type, an exportable key, a malformed
/// answer, a <c>kid</c> naming another key — fails closed with <see cref="SearchSealUnavailableException"/>; there is no fallback signer.
/// <para>Not validated against a real Managed HSM (no staging deployment): unit-tested against a fake HSM endpoint only (IX-7 ST-PERF-1, ST-PERF-7).</para>
/// </summary>
public sealed class ManagedHsmSearchSealSigner(IHttpClientFactory httpClientFactory, TokenCredential credential, ManagedHsmSealOptions hsm, SearchSealOptions options) : ISearchSealSigner
{
    public const string HttpClientName = "managed-hsm-seal";

    private static readonly TokenRequestContext TokenRequest = new([ManagedHsmSealOptions.TokenScope]);

    public string ProviderName => "AzureManagedHsm";

    public string SigningKeyName => options.SigningKeyName;

    public int SigningKeyVersion => options.SigningKeyVersion;

    public async Task<SearchSealSignature> SignAsync(ReadOnlyMemory<byte> message, CancellationToken cancellationToken)
    {
        var version = VersionIdentifier(SigningKeyVersion);
        var digest = SHA256.HashData(message.Span);
        using var document = await SendAsync(HttpMethod.Post, $"keys/{Uri.EscapeDataString(SigningKeyName)}/{version}/sign?api-version={ManagedHsmSealOptions.ApiVersion}",
            new { alg = "ES256", value = Base64UrlEncode(digest) }, cancellationToken).ConfigureAwait(false);
        var root = document.RootElement;
        RequireKid(root.TryGetProperty("kid", out var kid) ? kid.GetString() : null, SigningKeyName, version);
        var signature = Base64UrlDecode(root.GetProperty("value").GetString() ?? throw new SearchSealUnavailableException("Managed HSM returned no signature"));
        if (signature.Length != 64)
        {
            throw new SearchSealUnavailableException("Managed HSM returned a signature that is not a 64-byte P1363 ES256 signature");
        }

        return new SearchSealSignature(signature, SigningKeyName, SigningKeyVersion);
    }

    public async Task<byte[]> GetPublicKeyAsync(string keyName, int keyVersion, CancellationToken cancellationToken)
    {
        var version = VersionIdentifier(keyVersion);
        using var document = await SendAsync(HttpMethod.Get, $"keys/{Uri.EscapeDataString(keyName)}/{version}?api-version={ManagedHsmSealOptions.ApiVersion}", null, cancellationToken).ConfigureAwait(false);
        var root = document.RootElement;
        var key = root.GetProperty("key");
        RequireKid(key.TryGetProperty("kid", out var kid) ? kid.GetString() : null, keyName, version);
        if (key.GetProperty("kty").GetString() != "EC-HSM" || key.GetProperty("crv").GetString() != "P-256")
        {
            throw new SearchSealUnavailableException("the Managed HSM seal key is not an EC-HSM P-256 key");
        }

        if (!key.TryGetProperty("key_ops", out var operations) || !operations.EnumerateArray().Any(o => o.GetString() == "sign"))
        {
            throw new SearchSealUnavailableException("the Managed HSM seal key does not permit sign");
        }

        // Non-exportable custody (D-IX1-13): an exportable or released key is refused.
        if (root.TryGetProperty("attributes", out var attributes) && ((attributes.TryGetProperty("exportable", out var exportable) && exportable.GetBoolean())
                                                                       || (attributes.TryGetProperty("enabled", out var enabled) && !enabled.GetBoolean())))
        {
            throw new SearchSealUnavailableException("the Managed HSM seal key is exportable or disabled");
        }

        if (root.TryGetProperty("release_policy", out var policy) && policy.ValueKind == JsonValueKind.Object)
        {
            throw new SearchSealUnavailableException("the Managed HSM seal key has a release policy");
        }

        using var ecdsa = ECDsa.Create(new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            Q = new ECPoint { X = Base64UrlDecode(key.GetProperty("x").GetString() ?? string.Empty), Y = Base64UrlDecode(key.GetProperty("y").GetString() ?? string.Empty) },
        });
        return ecdsa.ExportSubjectPublicKeyInfo();
    }

    private string VersionIdentifier(int keyVersion)
    {
        var text = keyVersion.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (!hsm.KeyVersionIdentifiers.TryGetValue(text, out var identifier) || string.IsNullOrEmpty(identifier) || identifier.Length > 64 || !identifier.All(char.IsAsciiLetterOrDigit))
        {
            throw new SearchSealUnavailableException($"no Managed HSM version identifier is configured for seal key version {text}");
        }

        return identifier;
    }

    private static void RequireKid(string? kid, string keyName, string version)
    {
        if (kid is null || !kid.EndsWith($"/keys/{keyName}/{version}", StringComparison.Ordinal))
        {
            throw new SearchSealUnavailableException("Managed HSM answered for another key or version");
        }
    }

    private async Task<JsonDocument> SendAsync(HttpMethod method, string path, object? body, CancellationToken cancellationToken)
    {
        try
        {
            var client = httpClientFactory.CreateClient(HttpClientName);
            client.Timeout = TimeSpan.FromSeconds(Math.Clamp(hsm.TimeoutSeconds, 1, 60));
            var baseUrl = hsm.VaultBaseUrl is { Length: > 0 } url && Uri.TryCreate(url.EndsWith('/') ? url : url + "/", UriKind.Absolute, out var parsed) && parsed.Scheme == Uri.UriSchemeHttps
                ? parsed
                : throw new SearchSealUnavailableException("SearchIndex:Seal:ManagedHsm:VaultBaseUrl must be an HTTPS Managed HSM endpoint");
            AccessToken token;
            try
            {
                token = await credential.GetTokenAsync(TokenRequest, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Any credential failure (no managed identity, IMDS unreachable, refused) fails closed; never a fallback credential.
                throw new SearchSealUnavailableException("the Authority managed identity could not obtain a Managed HSM token", ex);
            }

            using var request = new HttpRequestMessage(method, new Uri(baseUrl, path));
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token.Token);
            if (body is not null)
            {
                request.Content = JsonContent.Create(body);
            }

            using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode == (HttpStatusCode)429)
            {
                throw new SearchSealUnavailableException("Managed HSM throttled the seal operation (429)");
            }

            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                throw new SearchSealUnavailableException($"Managed HSM refused the Authority identity ({(int)response.StatusCode})");
            }

            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                throw new SearchSealUnavailableException("the Managed HSM seal key or version does not exist");
            }

            if (!response.IsSuccessStatusCode)
            {
                throw new SearchSealUnavailableException($"Managed HSM returned {(int)response.StatusCode} for the seal key");
            }

            return await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false), cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new SearchSealUnavailableException("Managed HSM unreachable", ex);
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new SearchSealUnavailableException("Managed HSM timeout", ex);
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or CryptographicException or ArgumentException or FormatException)
        {
            throw new SearchSealUnavailableException("Managed HSM response unreadable", ex);
        }
    }

    private static string Base64UrlEncode(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

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
            throw new SearchSealUnavailableException("Managed HSM value is not base64url", ex);
        }
    }
}
