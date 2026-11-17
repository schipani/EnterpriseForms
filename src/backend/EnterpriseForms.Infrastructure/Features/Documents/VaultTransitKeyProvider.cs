using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using EnterpriseForms.Application.Features.Documents.Ports;
using Microsoft.Extensions.Options;

namespace EnterpriseForms.Infrastructure.Features.Documents;

/// <summary>
/// E2-1/E2-4: settings of <see cref="VaultTransitKeyProvider"/> (section <c>Encryption:VaultTransit</c>). The token is supplied by
/// the environment (the local Docker stack's <c>.env</c>), never by committed JSON. The mount and token are dedicated to business
/// encryption: they are not the Authority's HMAC mount (<c>transit</c>) or token.
/// </summary>
public sealed partial class VaultTransitEncryptionOptions
{
    /// <summary>The Authority's HMAC signing mount: never acceptable as the business encryption mount.</summary>
    public const string AuthorityTransitMount = "transit";

    /// <summary>Vault's address, an absolute http(s) URI without path, query or credentials (for example <c>http://vault:8200</c>).</summary>
    public string? Address { get; set; }

    /// <summary>The dedicated business-encryption Vault token (encrypt/decrypt on the business mount only). Secret configuration.</summary>
    public string? Token { get; set; }

    /// <summary>The dedicated Transit mount for business encryption keys.</summary>
    public string MountPath { get; set; } = "enterpriseforms-encryption";

    /// <summary>Prefix of the deterministic key names <c>{prefix}-{organization, 32 hex}-{purpose token}</c>.</summary>
    public string KeyNamePrefix { get; set; } = "ef-enc";

    /// <summary>Per-operation timeout; exceeding it fails closed.</summary>
    public int TimeoutSeconds { get; set; } = 5;

    /// <summary>Key versions per purpose name, for every organization. Absent: current version 1 only.</summary>
    public Dictionary<string, KeyVersionOptions> Purposes { get; set; } = new(StringComparer.Ordinal);

    /// <summary>Optional per-organization tables (organization id → purpose name → versions), replacing the purpose table for that organization.</summary>
    public Dictionary<string, Dictionary<string, KeyVersionOptions>> Organizations { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    private static readonly string[] ReservedMounts = [AuthorityTransitMount, "sys", "auth", "identity", "cubbyhole", "secret"];

    /// <summary>The first configuration problem, or null when the settings are complete and valid. Messages never repeat the token.</summary>
    public string? Problem()
    {
        if (string.IsNullOrWhiteSpace(Address))
        {
            return "Encryption:VaultTransit:Address is not set";
        }

        if (!Uri.TryCreate(Address, UriKind.Absolute, out var address) || (address.Scheme != Uri.UriSchemeHttp && address.Scheme != Uri.UriSchemeHttps)
            || address.UserInfo.Length > 0 || address.AbsolutePath != "/" || address.Query.Length > 0 || address.Fragment.Length > 0)
        {
            return "Encryption:VaultTransit:Address must be an absolute http(s) URI without path, query or credentials";
        }

        if (string.IsNullOrWhiteSpace(Token))
        {
            return "Encryption:VaultTransit:Token is not set";
        }

        if (Token.Length > 512 || Token.Any(c => char.IsWhiteSpace(c) || char.IsControl(c)))
        {
            return "Encryption:VaultTransit:Token is malformed";
        }

        if (!NamePattern().IsMatch(MountPath ?? string.Empty) || ReservedMounts.Contains(MountPath, StringComparer.Ordinal))
        {
            return "Encryption:VaultTransit:MountPath must be a dedicated mount name (lowercase letters, digits, hyphens; not 'transit')";
        }

        if (!PrefixPattern().IsMatch(KeyNamePrefix ?? string.Empty))
        {
            return "Encryption:VaultTransit:KeyNamePrefix must be 1-24 lowercase letters, digits or inner hyphens";
        }

        if (TimeoutSeconds is < 1 or > 60)
        {
            return "Encryption:VaultTransit:TimeoutSeconds must be between 1 and 60";
        }

        try
        {
            new KeyVersionTables().Load(Purposes, Organizations, "Vault Transit");
        }
        catch (KeyConfigurationException ex)
        {
            return "Encryption:VaultTransit key versions are invalid: " + ex.Message;
        }

        return null;
    }

    [GeneratedRegex("^[a-z][a-z0-9-]{0,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex NamePattern();

    [GeneratedRegex("^[a-z](?:[a-z0-9-]{0,22}[a-z0-9])?$", RegexOptions.CultureInvariant)]
    private static partial Regex PrefixPattern();
}

/// <summary>
/// E-2 Development/Testing key provider backed by HashiCorp Vault Transit (refused outside Development/Testing at startup,
/// <see cref="EncryptionProviderEnvironmentPolicy"/>). The application never holds a wrapping key: each data key is wrapped and
/// unwrapped by Vault's <c>encrypt</c>/<c>decrypt</c> endpoints under a non-exportable <c>aes256-gcm96</c> key.
/// <list type="bullet">
/// <item><b>Key selection</b> is deterministic from trusted configuration only: mount <see cref="VaultTransitEncryptionOptions.MountPath"/>,
/// key name <c>{prefix}-{organization, 32 hex}-{purpose token}</c>, version from the configured table. Nothing a caller supplies
/// names a key.</item>
/// <item><b>Reference</b> (stored in the unchanged v1 envelopes): <c>vault-transit:{organization}:{purpose token}:{version}</c>,
/// canonical and unique per key version.</item>
/// <item><b>Wrap context</b>: Vault's AES-GCM associated data is "DynamicForms/VaultTransit/DekWrap/v1" ‖ organization ‖ purpose ‖
/// version (the E-1 scope encoding), so a wrapped key also authenticates its scope and version, in addition to being sealed under
/// that scope's own Vault key.</item>
/// <item><b>Wrapped-key bytes</b>: Vault's ciphertext body for <c>aes256-gcm96</c>, nonce(12) ‖ ciphertext(32) ‖ tag(16) — the same
/// 60-byte layout as the local keyring. The <c>vault:vN:</c> prefix is not stored; it is rebuilt from the reference's version, so the
/// version Vault decrypts with is always the one the reference names.</item>
/// </list>
/// Unknown, disabled and mismatched versions, foreign organizations and purposes, an unreachable Vault, refused credentials and
/// unreadable responses all fail closed; there is no fallback to another provider or version and no key is ever created here
/// (the dedicated token has no <c>create</c> capability, so Vault's encrypt-upsert cannot create one either). Exceptions carry no
/// token, key material or Vault error text.
/// </summary>
public sealed class VaultTransitKeyProvider : IEncryptionKeyProvider
{
    public const string ProviderNameValue = "VaultTransit";
    public const string HttpClientName = "business-encryption-vault-transit";
    public const string ReferencePrefix = "vault-transit";
    private const int KeySize = 32;
    private const int WrappedSize = 12 + KeySize + 16;
    private static readonly byte[] WrapDomain = Encoding.ASCII.GetBytes("DynamicForms/VaultTransit/DekWrap/v1");

    private readonly IHttpClientFactory httpClientFactory;
    private readonly VaultTransitEncryptionOptions settings;
    private readonly KeyVersionTables tables = new();
    private readonly Uri? baseAddress;
    private readonly string? configurationError;

    public VaultTransitKeyProvider(IHttpClientFactory httpClientFactory, IOptions<EncryptionOptions> options)
    {
        ArgumentNullException.ThrowIfNull(httpClientFactory);
        ArgumentNullException.ThrowIfNull(options);
        this.httpClientFactory = httpClientFactory;
        settings = options.Value.VaultTransit ?? new VaultTransitEncryptionOptions();
        configurationError = settings.Problem();
        if (configurationError is null)
        {
            tables.Load(settings.Purposes, settings.Organizations, "Vault Transit");
            baseAddress = new Uri(settings.Address!, UriKind.Absolute);
        }
    }

    public string ProviderName => ProviderNameValue;

    public bool IsConfigured => configurationError is null;

    /// <summary>The canonical reference of one key version.</summary>
    public static string Reference(DataKeyScope scope, int version)
    {
        ArgumentNullException.ThrowIfNull(scope);
        return KeyScopeEncoding.Reference(ReferencePrefix, scope, version);
    }

    /// <summary>Parses a canonical reference; any other spelling is not a reference.</summary>
    public static bool TryParseReference(string reference, out Guid organizationId, out EncryptionKeyPurpose purpose, out int version) =>
        KeyScopeEncoding.TryParseReference(reference, ReferencePrefix, out organizationId, out purpose, out version);

    /// <summary>The Vault key name of a scope: deterministic from the configured prefix, the organization and the purpose.</summary>
    public string KeyName(DataKeyScope scope)
    {
        ArgumentNullException.ThrowIfNull(scope);
        return settings.KeyNamePrefix + "-" + scope.OrganizationId.ToString("N") + "-" + scope.Purpose.Token;
    }

    internal static byte[] WrapAad(DataKeyScope scope, int version) => KeyScopeEncoding.Context(WrapDomain, scope, version);

    public async Task<WrappedDataKey> WrapDataKeyAsync(DataKeyScope scope, ReadOnlyMemory<byte> dataKey, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scope);
        cancellationToken.ThrowIfCancellationRequested();
        RequireConfigured();
        if (dataKey.Length != KeySize)
        {
            throw new ArgumentException("a 256-bit data key is required");
        }

        var version = tables.For(scope).Current;
        var body = new Dictionary<string, object>
        {
            ["plaintext"] = Convert.ToBase64String(dataKey.Span),
            ["associated_data"] = Convert.ToBase64String(WrapAad(scope, version)),
            ["key_version"] = version,
        };
        using var response = await PostAsync("encrypt", scope, body, cancellationToken).ConfigureAwait(false);
        if (response.Status != HttpStatusCode.OK)
        {
            throw new EncryptionUnavailableException(Refusal("wrap", response.Status));
        }

        var data = Data(response.Document);
        var ciphertext = Text(data, "ciphertext");
        if (!data.TryGetProperty("key_version", out var reported) || reported.ValueKind != JsonValueKind.Number || !reported.TryGetInt32(out var reportedVersion) || reportedVersion != version)
        {
            throw new EncryptionUnavailableException("the encryption key service wrapped under an unexpected key version");
        }

        var wrapped = ParseCiphertext(ciphertext, version) ?? throw new EncryptionUnavailableException("the encryption key service returned an unexpected wrapped key");
        return new WrappedDataKey(Reference(scope, version), wrapped);
    }

    public async Task<byte[]> UnwrapDataKeyAsync(DataKeyScope scope, string keyReference, ReadOnlyMemory<byte> wrappedDataKey, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(keyReference);
        cancellationToken.ThrowIfCancellationRequested();
        RequireConfigured();
        if (!TryParseReference(keyReference, out var organization, out var purpose, out var version))
        {
            // Includes the local keyring's references: this provider never falls back to another one (E2-5).
            throw new EncryptionUnavailableException("unknown key reference");
        }

        if (organization != scope.OrganizationId || !ReferenceEquals(purpose, scope.Purpose))
        {
            throw new CryptographicException("the key reference belongs to another organization or purpose");
        }

        switch (tables.For(scope).Resolve(version))
        {
            case KeyVersionState.Disabled:
                throw new EncryptionUnavailableException("the key version is disabled");
            case KeyVersionState.Unknown:
                throw new EncryptionUnavailableException("the key version is not retained");
        }

        if (wrappedDataKey.Length != WrappedSize)
        {
            throw new CryptographicException("the wrapped key does not have the Vault Transit aes256-gcm96 layout");
        }

        var body = new Dictionary<string, object>
        {
            ["ciphertext"] = "vault:v" + version.ToString(CultureInfo.InvariantCulture) + ":" + Convert.ToBase64String(wrappedDataKey.Span),
            ["associated_data"] = Convert.ToBase64String(WrapAad(scope, version)),
        };
        using var response = await PostAsync("decrypt", scope, body, cancellationToken).ConfigureAwait(false);
        if (response.Status == HttpStatusCode.BadRequest && response.IsAuthenticationFailure)
        {
            // The wrapped key did not authenticate under this scope's key, version and context (tampered, relabelled or foreign).
            throw new CryptographicException("the wrapped data key failed authentication");
        }

        if (response.Status != HttpStatusCode.OK)
        {
            throw new EncryptionUnavailableException(Refusal("unwrap", response.Status));
        }

        byte[] plain;
        try
        {
            plain = Convert.FromBase64String(Text(Data(response.Document), "plaintext"));
        }
        catch (FormatException)
        {
            throw new EncryptionUnavailableException("the encryption key service returned an unreadable response");
        }

        if (plain.Length != KeySize)
        {
            CryptographicOperations.ZeroMemory(plain);
            throw new CryptographicException("the unwrapped data key has the wrong length");
        }

        return plain;
    }

    /// <summary>Vault's ciphertext <c>vault:v{version}:{base64}</c> → the 60-byte body, or null when it is not exactly that.</summary>
    internal static byte[]? ParseCiphertext(string ciphertext, int version)
    {
        var prefix = "vault:v" + version.ToString(CultureInfo.InvariantCulture) + ":";
        if (!ciphertext.StartsWith(prefix, StringComparison.Ordinal))
        {
            return null;
        }

        try
        {
            var body = Convert.FromBase64String(ciphertext[prefix.Length..]);
            return body.Length == WrappedSize ? body : null;
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private static string Refusal(string operation, HttpStatusCode status) =>
        "the encryption key service refused the " + operation + " (status " + ((int)status).ToString(CultureInfo.InvariantCulture) + ")";

    private void RequireConfigured()
    {
        if (configurationError is not null)
        {
            throw new EncryptionUnavailableException("the Vault Transit encryption provider is not configured: " + configurationError);
        }
    }

    private static JsonElement Data(JsonDocument? document)
    {
        if (document is null || document.RootElement.ValueKind != JsonValueKind.Object || !document.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
        {
            throw new EncryptionUnavailableException("the encryption key service returned an unreadable response");
        }

        return data;
    }

    private static string Text(JsonElement data, string name) =>
        data.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } text
            ? text
            : throw new EncryptionUnavailableException("the encryption key service returned an unreadable response");

    private async Task<VaultResponse> PostAsync(string operation, DataKeyScope scope, Dictionary<string, object> body, CancellationToken cancellationToken)
    {
        var path = "v1/" + settings.MountPath + "/" + operation + "/" + Uri.EscapeDataString(KeyName(scope));
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(settings.TimeoutSeconds));
        try
        {
            using var client = httpClientFactory.CreateClient(HttpClientName);
            using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(baseAddress!, path));
            request.Headers.TryAddWithoutValidation("X-Vault-Token", settings.Token);
            request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseContentRead, timeout.Token).ConfigureAwait(false);
            var text = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
            JsonDocument? document = null;
            try
            {
                document = text.Length == 0 ? null : JsonDocument.Parse(text);
            }
            catch (JsonException) when (!response.IsSuccessStatusCode)
            {
                document = null;
            }
            catch (JsonException)
            {
                throw new EncryptionUnavailableException("the encryption key service returned an unreadable response");
            }

            return new VaultResponse(response.StatusCode, document);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw; // E2-6: the caller's cancellation propagates unchanged.
        }
        catch (OperationCanceledException)
        {
            throw new EncryptionUnavailableException("the encryption key service timed out");
        }
        catch (HttpRequestException)
        {
            throw new EncryptionUnavailableException("the encryption key service is unreachable");
        }
    }

    private sealed class VaultResponse(HttpStatusCode status, JsonDocument? document) : IDisposable
    {
        public HttpStatusCode Status { get; } = status;

        public JsonDocument? Document { get; } = document;

        /// <summary>Vault's AES-GCM authentication failure (only used to classify a decrypt refusal; the text is never surfaced).</summary>
        public bool IsAuthenticationFailure =>
            Document is { RootElement.ValueKind: JsonValueKind.Object } d && d.RootElement.TryGetProperty("errors", out var errors) && errors.ValueKind == JsonValueKind.Array
            && errors.EnumerateArray().Any(e => e.ValueKind == JsonValueKind.String && (e.GetString() ?? string.Empty).Contains("message authentication failed", StringComparison.Ordinal));

        public void Dispose() => Document?.Dispose();
    }
}
