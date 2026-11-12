using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace EnterpriseForms.Api.ContractTests;

/// <summary>
/// E2-7: a deterministic in-process stand-in for the HashiCorp Vault Transit HTTP API, limited to what the business encryption
/// provider uses and modelled on Vault's documented behaviour: <c>aes256-gcm96</c> keys with versions, <c>key_version</c> on
/// encrypt, <c>associated_data</c>, ciphertext <c>vault:vN:base64(nonce ‖ ciphertext ‖ tag)</c>, <c>min_decryption_version</c>, and a
/// token policy that grants <c>update</c> (never <c>create</c>) on one mount's encrypt/decrypt paths — so encrypting under a key that
/// does not exist is refused instead of creating it. Real Vault behaviour is checked separately by <c>VaultTransitDockerIntegrationTests</c>.
/// </summary>
internal sealed class FakeVaultTransit : HttpMessageHandler
{
    public const string BusinessToken = "fake-business-encryption-token";

    private readonly Dictionary<string, FakeKey> keys = new(StringComparer.Ordinal);

    public FakeVaultTransit(string mount = "enterpriseforms-encryption") => Mount = mount;

    public string Mount { get; }

    /// <summary>Tokens and the mount each may use (encrypt/decrypt only).</summary>
    public Dictionary<string, string> TokenMounts { get; } = new(StringComparer.Ordinal) { [BusinessToken] = "enterpriseforms-encryption" };

    public List<(string Path, string? Token, JsonElement Body)> Requests { get; } = [];

    public Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>? Override { get; set; }

    public IReadOnlyCollection<string> KeyNames => keys.Keys;

    /// <summary>Provisions a key (an operator action; never done by the provider). Returns the latest version.</summary>
    public int Provision(string name, int versions = 1)
    {
        if (!keys.TryGetValue(name, out var key))
        {
            key = new FakeKey();
            keys[name] = key;
        }

        while (key.Versions.Count < versions)
        {
            key.Versions.Add(RandomNumberGenerator.GetBytes(32));
        }

        return key.Versions.Count;
    }

    /// <summary>Makes <paramref name="name"/> another name of the key <paramref name="target"/> (an operator mistake the wrap context must survive).</summary>
    public void Alias(string name, string target) => keys[name] = keys[target];

    public void SetMinimumDecryptionVersion(string name, int version) => keys[name].MinimumDecryptionVersion = version;

    public IHttpClientFactory Factory() => new SingleClientFactory(this);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var token = request.Headers.TryGetValues("X-Vault-Token", out var values) ? values.Single() : null;
        var text = request.Content is null ? "{}" : await request.Content.ReadAsStringAsync(cancellationToken);
        using (var parsed = JsonDocument.Parse(text))
        {
            Requests.Add((request.RequestUri!.AbsolutePath, token, parsed.RootElement.Clone()));
        }

        if (Override is not null)
        {
            return await Override(request, cancellationToken);
        }

        var segments = request.RequestUri!.AbsolutePath.Trim('/').Split('/');
        if (segments.Length != 4 || segments[0] != "v1" || request.Method != HttpMethod.Post)
        {
            return Error(HttpStatusCode.NotFound, "unsupported path");
        }

        var (mount, operation, name) = (segments[1], segments[2], Uri.UnescapeDataString(segments[3]));
        if (token is null || !TokenMounts.TryGetValue(token, out var allowedMount) || allowedMount != mount || mount != Mount || operation is not ("encrypt" or "decrypt"))
        {
            return Error(HttpStatusCode.Forbidden, "permission denied");
        }

        using var body = JsonDocument.Parse(text);
        var root = body.RootElement;
        var aad = root.TryGetProperty("associated_data", out var a) ? Convert.FromBase64String(a.GetString()!) : [];
        if (!keys.TryGetValue(name, out var key))
        {
            // Encrypt would upsert, which needs the create capability this token lacks; decrypt reports a missing key.
            return operation == "encrypt" ? Error(HttpStatusCode.Forbidden, "permission denied") : Error(HttpStatusCode.BadRequest, "encryption key not found");
        }

        if (operation == "encrypt")
        {
            var version = root.TryGetProperty("key_version", out var v) ? v.GetInt32() : key.Versions.Count;
            if (version > key.Versions.Count)
            {
                return Error(HttpStatusCode.BadRequest, "requested version for encryption is higher than the latest key version");
            }

            var plain = Convert.FromBase64String(root.GetProperty("plaintext").GetString()!);
            var nonce = RandomNumberGenerator.GetBytes(12);
            var cipher = new byte[plain.Length];
            var tag = new byte[16];
            using (var aes = new AesGcm(key.Versions[version - 1], 16))
            {
                aes.Encrypt(nonce, plain, cipher, tag, aad);
            }

            return Json(new { data = new { ciphertext = "vault:v" + version.ToString(CultureInfo.InvariantCulture) + ":" + Convert.ToBase64String([.. nonce, .. cipher, .. tag]), key_version = version } });
        }

        var ciphertext = root.GetProperty("ciphertext").GetString()!;
        var parts = ciphertext.Split(':');
        if (parts.Length != 3 || parts[0] != "vault" || !parts[1].StartsWith('v') || !int.TryParse(parts[1][1..], NumberStyles.None, CultureInfo.InvariantCulture, out var cipherVersion))
        {
            return Error(HttpStatusCode.BadRequest, "invalid ciphertext: no prefix");
        }

        if (cipherVersion < key.MinimumDecryptionVersion)
        {
            return Error(HttpStatusCode.BadRequest, "ciphertext version is disallowed by policy (too old)");
        }

        if (cipherVersion < 1 || cipherVersion > key.Versions.Count)
        {
            return Error(HttpStatusCode.BadRequest, "invalid key version");
        }

        var raw = Convert.FromBase64String(parts[2]);
        var opened = new byte[raw.Length - 28];
        try
        {
            using var aes = new AesGcm(key.Versions[cipherVersion - 1], 16);
            aes.Decrypt(raw.AsSpan(0, 12), raw.AsSpan(12, raw.Length - 28), raw.AsSpan(raw.Length - 16), opened, aad);
        }
        catch (CryptographicException)
        {
            return Error(HttpStatusCode.BadRequest, "cipher: message authentication failed");
        }

        return Json(new { data = new { plaintext = Convert.ToBase64String(opened) } });
    }

    public static HttpResponseMessage Error(HttpStatusCode status, string error) =>
        new(status) { Content = new StringContent(JsonSerializer.Serialize(new { errors = new[] { error } }), Encoding.UTF8, "application/json") };

    public static HttpResponseMessage Json(object value) =>
        new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json") };

    private sealed class FakeKey
    {
        public List<byte[]> Versions { get; } = [];

        public int MinimumDecryptionVersion { get; set; } = 1;
    }

    private sealed class SingleClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }
}
