using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Azure.Core;

namespace EnterpriseForms.Experiments.ManagedHsmProbe;

internal enum HsmOutcome
{
    Success,

    /// <summary>A 4xx refusal other than authentication, authorization, not-found or throttling (e.g. 400 BadParameter).</summary>
    Rejected,

    NotFound,

    /// <summary>401, or the credential could not produce a token.</summary>
    Unauthenticated,

    /// <summary>403: authenticated but not permitted (missing Managed HSM local RBAC role).</summary>
    Forbidden,

    Throttled,

    ServiceError,

    Unreachable,

    Timeout,

    /// <summary>The service answered with content the probe cannot interpret.</summary>
    Malformed,

    /// <summary>The service answered for another key or version than the one requested.</summary>
    WrongKeyIdentifier,
}

/// <summary>One HSM call. Never carries an access token or key material; error text is truncated.</summary>
internal sealed record HsmCallResult(HsmOutcome Outcome, int? HttpStatus = null, string? ErrorCode = null, string? ErrorMessage = null)
{
    public string? KeyIdentifier { get; init; }

    public byte[]? Bytes { get; init; }

    public bool? Verified { get; init; }

    public JsonElement? Body { get; init; }

    public IReadOnlyList<string> VersionIdentifiers { get; init; } = [];

    public string? LatestVersionIdentifier { get; init; }

    /// <summary>The <c>resource</c>/<c>scope</c> parameter of a 401 WWW-Authenticate challenge (no token material).</summary>
    public string? ChallengeResource { get; init; }

    /// <summary>Availability, authentication, authorization, throttling and transport failures: the probe cannot conclude anything.</summary>
    public bool IsInfrastructureFailure => Outcome is HsmOutcome.Unauthenticated or HsmOutcome.Forbidden or HsmOutcome.Throttled
        or HsmOutcome.ServiceError or HsmOutcome.Unreachable or HsmOutcome.Timeout or HsmOutcome.Malformed;

    public string Describe() => Outcome == HsmOutcome.Success
        ? "Success"
        : $"{Outcome}{(HttpStatus is { } s ? " HTTP " + s.ToString(System.Globalization.CultureInfo.InvariantCulture) : string.Empty)}{(ErrorCode is null ? string.Empty : " " + ErrorCode)}{(ErrorMessage is null ? string.Empty : ": " + ErrorMessage)}";
}

internal interface IManagedHsmClient
{
    Task<HsmCallResult> GetKeyAsync(string versionId, CancellationToken cancellationToken);

    Task<HsmCallResult> ListKeyVersionsAsync(CancellationToken cancellationToken);

    /// <summary>POST .../sign {"alg":"HS256","value":base64url(value)}. The bytes are sent exactly as given (no hashing here).</summary>
    Task<HsmCallResult> SignAsync(string versionId, ReadOnlyMemory<byte> value, CancellationToken cancellationToken);

    /// <summary>POST .../verify {"alg":"HS256","digest":base64url(digest),"value":base64url(signature)}.</summary>
    Task<HsmCallResult> VerifyAsync(string versionId, ReadOnlyMemory<byte> digest, ReadOnlyMemory<byte> signature, CancellationToken cancellationToken);

    /// <summary>UNDOCUMENTED-BEHAVIOUR EXPERIMENT: plaintext JWK import of a published test key as oct-HSM (PUT /keys/{name}).</summary>
    Task<HsmCallResult> ImportOctKeyAsync(ReadOnlyMemory<byte> testKey, CancellationToken cancellationToken);
}

/// <summary>
/// Direct Managed HSM REST client used for the probe's experiments. It sends exactly the bytes it is given, authenticates
/// with the explicitly configured Azure Identity credential for <see cref="ProbeConfiguration.ManagedHsmTokenScope"/>,
/// never retries, bounds every call with the configured timeout and propagates caller cancellation.
/// </summary>
internal sealed class ManagedHsmRestClient(HttpClient httpClient, TokenCredential credential, ProbeConfiguration configuration) : IManagedHsmClient
{
    public const string Algorithm = "HS256";

    private const int MaxErrorText = 300;

    private static readonly TokenRequestContext TokenRequest = new([ProbeConfiguration.ManagedHsmTokenScope]);

    private readonly Uri endpoint = configuration.EndpointUri;

    private readonly string keyName = configuration.KeyName ?? throw new ArgumentException("KeyName missing", nameof(configuration));

    public Task<HsmCallResult> GetKeyAsync(string versionId, CancellationToken cancellationToken) =>
        SendAsync(HttpMethod.Get, ManagedHsmUrls.Key(endpoint, keyName, versionId, configuration.ApiVersion), null, (root, result) =>
        {
            if (!root.TryGetProperty("key", out var key) || key.ValueKind != JsonValueKind.Object)
            {
                return result with { Outcome = HsmOutcome.Malformed, ErrorMessage = "no key object" };
            }

            var kid = key.TryGetProperty("kid", out var k) ? k.GetString() : null;
            return KidMatches(kid, versionId)
                ? result with { KeyIdentifier = kid, Body = root.Clone() }
                : result with { Outcome = HsmOutcome.WrongKeyIdentifier, KeyIdentifier = kid };
        }, cancellationToken);

    public async Task<HsmCallResult> ListKeyVersionsAsync(CancellationToken cancellationToken)
    {
        var identifiers = new List<string>();
        string? latest = null;
        long latestCreated = long.MinValue;
        Uri? next = ManagedHsmUrls.Versions(endpoint, keyName, configuration.ApiVersion);
        for (var page = 0; next is not null; page++)
        {
            if (page == 10)
            {
                return new HsmCallResult(HsmOutcome.Malformed, ErrorMessage: "more than 10 pages of key versions");
            }

            Uri? following = null;
            var result = await SendAsync(HttpMethod.Get, next, null, (root, r) =>
            {
                if (!root.TryGetProperty("value", out var items) || items.ValueKind != JsonValueKind.Array)
                {
                    return r with { Outcome = HsmOutcome.Malformed, ErrorMessage = "no value array" };
                }

                foreach (var item in items.EnumerateArray())
                {
                    var kid = item.TryGetProperty("kid", out var k) ? k.GetString() : null;
                    var prefix = new Uri(endpoint, $"keys/{keyName}/").AbsoluteUri;
                    if (kid is null || !kid.StartsWith(prefix, StringComparison.Ordinal))
                    {
                        return r with { Outcome = HsmOutcome.WrongKeyIdentifier, KeyIdentifier = kid };
                    }

                    var id = kid[prefix.Length..];
                    identifiers.Add(id);
                    var created = item.TryGetProperty("attributes", out var a) && a.TryGetProperty("created", out var c) && c.TryGetInt64(out var t) ? t : long.MinValue;
                    if (created > latestCreated)
                    {
                        latestCreated = created;
                        latest = id;
                    }
                }

                if (root.TryGetProperty("nextLink", out var link) && link.GetString() is { Length: > 0 } nextLink)
                {
                    // Follow only same-host HTTPS links (a nextLink is service-provided content).
                    if (!Uri.TryCreate(nextLink, UriKind.Absolute, out var parsed) || parsed.Scheme != Uri.UriSchemeHttps || parsed.Host != endpoint.Host)
                    {
                        return r with { Outcome = HsmOutcome.Malformed, ErrorMessage = "nextLink points elsewhere" };
                    }

                    following = parsed;
                }

                return r;
            }, cancellationToken).ConfigureAwait(false);
            if (result.Outcome != HsmOutcome.Success)
            {
                return result;
            }

            next = following;
        }

        return new HsmCallResult(HsmOutcome.Success, 200) { VersionIdentifiers = identifiers, LatestVersionIdentifier = latestCreated == long.MinValue ? null : latest };
    }

    public Task<HsmCallResult> SignAsync(string versionId, ReadOnlyMemory<byte> value, CancellationToken cancellationToken) =>
        SendAsync(HttpMethod.Post, ManagedHsmUrls.Sign(endpoint, keyName, versionId, configuration.ApiVersion), Json(w =>
        {
            w.WriteString("alg", Algorithm);
            w.WriteString("value", Codec.ToBase64Url(value.Span));
        }), (root, result) =>
        {
            var kid = root.TryGetProperty("kid", out var k) ? k.GetString() : null;
            if (!KidMatches(kid, versionId))
            {
                return result with { Outcome = HsmOutcome.WrongKeyIdentifier, KeyIdentifier = kid };
            }

            return root.TryGetProperty("value", out var v) && Codec.TryFromBase64Url(v.GetString(), out var mac)
                ? result with { KeyIdentifier = kid, Bytes = mac }
                : result with { Outcome = HsmOutcome.Malformed, ErrorMessage = "sign response has no base64url value" };
        }, cancellationToken);

    public Task<HsmCallResult> VerifyAsync(string versionId, ReadOnlyMemory<byte> digest, ReadOnlyMemory<byte> signature, CancellationToken cancellationToken) =>
        SendAsync(HttpMethod.Post, ManagedHsmUrls.Verify(endpoint, keyName, versionId, configuration.ApiVersion), Json(w =>
        {
            w.WriteString("alg", Algorithm);
            w.WriteString("digest", Codec.ToBase64Url(digest.Span));
            w.WriteString("value", Codec.ToBase64Url(signature.Span));
        }), (root, result) => root.TryGetProperty("value", out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? result with { Verified = v.GetBoolean() }
            : result with { Outcome = HsmOutcome.Malformed, ErrorMessage = "verify response has no boolean value" }, cancellationToken);

    public Task<HsmCallResult> ImportOctKeyAsync(ReadOnlyMemory<byte> testKey, CancellationToken cancellationToken) =>
        SendAsync(HttpMethod.Put, ManagedHsmUrls.Import(endpoint, keyName, configuration.ApiVersion), Json(w =>
        {
            w.WriteStartObject("key");
            w.WriteString("kty", "oct-HSM");
            w.WriteString("k", Codec.ToBase64Url(testKey.Span));
            w.WriteStartArray("key_ops");
            w.WriteStringValue("sign");
            w.WriteStringValue("verify");
            w.WriteEndArray();
            w.WriteEndObject();
            w.WriteBoolean("Hsm", true);
            w.WriteStartObject("attributes");
            w.WriteBoolean("enabled", true);
            w.WriteBoolean("exportable", false);
            w.WriteEndObject();
        }), (root, result) =>
        {
            var kid = root.TryGetProperty("key", out var key) && key.TryGetProperty("kid", out var k) ? k.GetString() : null;
            var prefix = new Uri(endpoint, $"keys/{keyName}/").AbsoluteUri;
            if (kid is null || !kid.StartsWith(prefix, StringComparison.Ordinal) || !ProbeConfiguration.VersionIdentifierPattern().IsMatch(kid[prefix.Length..]))
            {
                return result with { Outcome = HsmOutcome.WrongKeyIdentifier, KeyIdentifier = kid };
            }

            return result with { KeyIdentifier = kid, LatestVersionIdentifier = kid[prefix.Length..], Body = RedactKeyMaterial(root) };
        }, cancellationToken);

    /// <summary>The exact JSON request shapes, for the report (key material shown as a placeholder).</summary>
    public static IReadOnlyDictionary<string, string> RequestShapes(string apiVersion) => new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["sign"] = $"POST {{endpoint}}/keys/{{key-name}}/{{key-version}}/sign?api-version={apiVersion} {{\"alg\":\"HS256\",\"value\":\"<base64url bytes>\"}}",
        ["verify"] = $"POST {{endpoint}}/keys/{{key-name}}/{{key-version}}/verify?api-version={apiVersion} {{\"alg\":\"HS256\",\"digest\":\"<base64url bytes>\",\"value\":\"<base64url MAC>\"}}",
        ["get-key"] = $"GET {{endpoint}}/keys/{{key-name}}/{{key-version}}?api-version={apiVersion}",
        ["list-versions"] = $"GET {{endpoint}}/keys/{{key-name}}/versions?api-version={apiVersion}&maxresults=25",
        ["import"] = $"PUT {{endpoint}}/keys/{{key-name}}?api-version={apiVersion} {{\"key\":{{\"kty\":\"oct-HSM\",\"k\":\"<published test key, never logged>\",\"key_ops\":[\"sign\",\"verify\"]}},\"Hsm\":true,\"attributes\":{{\"enabled\":true,\"exportable\":false}}}}",
        ["token-scope"] = ProbeConfiguration.ManagedHsmTokenScope,
    };

    private bool KidMatches(string? kid, string versionId) =>
        kid is not null && kid.EndsWith($"/keys/{keyName}/{versionId}", StringComparison.Ordinal)
                        && Uri.TryCreate(kid, UriKind.Absolute, out var parsed) && parsed.Host == endpoint.Host;

    private static byte[] Json(Action<Utf8JsonWriter> body)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            body(writer);
            writer.WriteEndObject();
        }

        return stream.ToArray();
    }

    private static JsonElement RedactKeyMaterial(JsonElement root)
    {
        // A conforming service never returns "k" for an HSM key; strip it defensively so it can never reach the report.
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            Copy(root, writer, "k");
        }

        using var document = JsonDocument.Parse(stream.ToArray());
        return document.RootElement.Clone();
    }

    private static void Copy(JsonElement element, Utf8JsonWriter writer, string dropProperty)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in element.EnumerateObject().Where(p => p.Name != dropProperty))
                {
                    writer.WritePropertyName(property.Name);
                    Copy(property.Value, writer, dropProperty);
                }

                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray())
                {
                    Copy(item, writer, dropProperty);
                }

                writer.WriteEndArray();
                break;
            default:
                element.WriteTo(writer);
                break;
        }
    }

    private async Task<HsmCallResult> SendAsync(HttpMethod method, Uri uri, byte[]? body, Func<JsonElement, HsmCallResult, HsmCallResult> interpret, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(configuration.OperationTimeoutSeconds, 1, 60)));
        try
        {
            AccessToken token;
            try
            {
                token = await credential.GetTokenAsync(TokenRequest, timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
#pragma warning disable CA1031 // any credential failure is reported as Unauthenticated (Inconclusive); no fallback credential
            catch (Exception ex)
#pragma warning restore CA1031
            {
                return new HsmCallResult(HsmOutcome.Unauthenticated, ErrorMessage: "credential could not obtain a token (" + ex.GetType().Name + ")");
            }

            using var request = new HttpRequestMessage(method, uri);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);
            if (body is not null)
            {
                request.Content = new ByteArrayContent(body);
                request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
            }

            using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseContentRead, timeout.Token).ConfigureAwait(false);
            var text = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
            var status = (int)response.StatusCode;
            if (!response.IsSuccessStatusCode)
            {
                var (code, message) = ParseError(text);
                var outcome = response.StatusCode switch
                {
                    HttpStatusCode.Unauthorized => HsmOutcome.Unauthenticated,
                    HttpStatusCode.Forbidden => HsmOutcome.Forbidden,
                    HttpStatusCode.NotFound => HsmOutcome.NotFound,
                    HttpStatusCode.TooManyRequests => HsmOutcome.Throttled,
                    >= HttpStatusCode.InternalServerError => HsmOutcome.ServiceError,
                    _ => HsmOutcome.Rejected,
                };
                return new HsmCallResult(outcome, status, code, message) { ChallengeResource = ChallengeResource(response) };
            }

            try
            {
                using var document = JsonDocument.Parse(text);
                return interpret(document.RootElement, new HsmCallResult(HsmOutcome.Success, status));
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
            {
                return new HsmCallResult(HsmOutcome.Malformed, status, ErrorMessage: "response is not the documented JSON shape");
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new HsmCallResult(HsmOutcome.Timeout, ErrorMessage: $"no answer within {configuration.OperationTimeoutSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture)} s");
        }
        catch (HttpRequestException ex)
        {
            return new HsmCallResult(HsmOutcome.Unreachable, ErrorMessage: Truncate(ex.Message));
        }
    }

    private static (string? Code, string? Message) ParseError(string text)
    {
        try
        {
            using var document = JsonDocument.Parse(text);
            if (document.RootElement.TryGetProperty("error", out var error))
            {
                return (error.TryGetProperty("code", out var c) ? Truncate(c.GetString()) : null, error.TryGetProperty("message", out var m) ? Truncate(m.GetString()) : null);
            }
        }
        catch (JsonException)
        {
        }

        return (null, string.IsNullOrEmpty(text) ? null : Truncate(text));
    }

    private static string? ChallengeResource(HttpResponseMessage response)
    {
        foreach (var header in response.Headers.WwwAuthenticate)
        {
            foreach (var name in new[] { "resource=\"", "scope=\"" })
            {
                var parameter = header.Parameter ?? string.Empty;
                var start = parameter.IndexOf(name, StringComparison.OrdinalIgnoreCase);
                if (start >= 0)
                {
                    start += name.Length;
                    var end = parameter.IndexOf('"', start);
                    if (end > start)
                    {
                        return Truncate(parameter[start..end]);
                    }
                }
            }
        }

        return null;
    }

    private static string? Truncate(string? text)
    {
        if (text is null)
        {
            return null;
        }

        var single = new StringBuilder(Math.Min(text.Length, MaxErrorText));
        foreach (var c in text.Take(MaxErrorText))
        {
            single.Append(char.IsControl(c) ? ' ' : c);
        }

        return single.ToString();
    }
}
