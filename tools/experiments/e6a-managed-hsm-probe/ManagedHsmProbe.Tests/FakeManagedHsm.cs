using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Azure.Core;

namespace EnterpriseForms.Experiments.ManagedHsmProbe.Tests;

/// <summary>How the fake models HS256 "sign"/"verify" over the request <c>value</c>/<c>digest</c> bytes. The real behaviour is unknown.</summary>
internal enum FakeSemantics
{
    /// <summary>HMAC-SHA-256(key, value) for any length (the hoped-for Experiment B behaviour).</summary>
    HmacOverValue,

    /// <summary>HMAC-SHA-256(key, SHA-256(value)): the service hashes whatever it receives.</summary>
    HashThenHmac,

    /// <summary>Only 32-byte "digests" are accepted (400 otherwise); HMAC over the 32 bytes.</summary>
    DigestLengthOnly,

    /// <summary>32-byte values are MACed as-is; anything else is hashed first.</summary>
    HashUnlessDigestLength,
}

/// <summary>How the fake reports a failed verification (the real service was observed to use HTTP 400 RET_SIGNATURE_VERIFICATION_FAILED).</summary>
internal enum FakeVerifyStyle
{
    /// <summary>200 {"value":false} on mismatch (the documented KeyVerifyResult).</summary>
    BooleanFalse,

    /// <summary>HTTP 400 RET_SIGNATURE_VERIFICATION_FAILED on mismatch; 200 true on a match.</summary>
    Http400OnMismatch,

    /// <summary>HTTP 400 RET_SIGNATURE_VERIFICATION_FAILED even for a genuine signature (a service that cannot verify).</summary>
    Http400Always,

    /// <summary>HTTP 400 with a different error code.</summary>
    OtherBadRequest,

    /// <summary>The right code with the wrong status (409).</summary>
    SameCodeWrongStatus,

    /// <summary>The exact verification token under a different error code (not recognized).</summary>
    TokenUnderWrongCode,

    /// <summary>The token only as part of a longer identifier (not recognized).</summary>
    TokenOnlyAsSubstring,

    /// <summary>HTTP 500.</summary>
    ServerError,

    /// <summary>200 true for anything (a service that verifies nothing).</summary>
    AcceptAll,
}

internal sealed record RecordedRequest(string Method, string PathAndQuery, string? Body, string? Authorization);

/// <summary>
/// A local stand-in for the Managed HSM keys data plane (Key Vault REST shapes). It models the documented request/response
/// contracts and lets each test choose a semantics model and failure; it is NOT evidence of real Managed HSM behaviour.
/// </summary>
internal sealed class FakeManagedHsm : HttpMessageHandler
{
    public const string Host = "probe-test.managedhsm.azure.net";
    public const string KeyName = "e6a-probe-hmac";

    private readonly List<(string Id, byte[] Key, long Created)> versions = [];
    private int counter;

    public FakeSemantics Semantics { get; set; } = FakeSemantics.HmacOverValue;

    public int? MaxValueBytes { get; set; }

    /// <summary>A dishonest service: signs with the latest version whatever version is requested, but echoes the requested kid.</summary>
    public bool IgnoreVersionUseLatest { get; set; }

    public HttpStatusCode? ForcedStatus { get; set; }

    /// <summary>Every authenticated request (sign, verify, get, import, ...) is answered with this exact error.</summary>
    public (HttpStatusCode Status, string Code, string Message)? ForcedError { get; set; }

    /// <summary>The error body the observed service sends for a failed verification: 400, code BadParameter, token in the message.</summary>
    public const string VerificationFailedMessage = "Signature verification failed: RET_SIGNATURE_VERIFICATION_FAILED";

    public TimeSpan? Delay { get; set; }

    public bool MalformedBody { get; set; }

    public bool WrongKid { get; set; }

    public bool Exportable { get; set; }

    public bool AllowImport { get; set; } = true;

    /// <summary>Answer unauthenticated requests with a Key Vault style Bearer challenge (what the Azure SDK expects).</summary>
    public bool ChallengeUnauthenticated { get; set; } = true;

    /// <summary>How a verify call reports a signature that does not match.</summary>
    public FakeVerifyStyle VerifyStyle { get; set; } = FakeVerifyStyle.BooleanFalse;

    /// <summary>Models the observed E-6b behaviour: zero-length raw input is refused with HTTP 400.</summary>
    public bool RejectEmptyValue { get; set; }

    /// <summary>Status used when <see cref="MaxValueBytes"/> is exceeded (the observed service answers 413).</summary>
    public HttpStatusCode OversizeStatus { get; set; } = HttpStatusCode.BadRequest;

    public List<RecordedRequest> Requests { get; } = [];

    public IReadOnlyList<string> VersionIds => [.. versions.Select(v => v.Id)];

    public static Uri Endpoint => new($"https://{Host}/");

    public string AddVersion(byte[] key)
    {
        counter++;
        var id = Convert.ToHexStringLower(SHA256.HashData(Encoding.ASCII.GetBytes("fake-version-" + counter)))[..32];
        versions.Add((id, (byte[])key.Clone(), 1_700_000_000L + counter));
        return id;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var authorization = request.Headers.Authorization?.ToString();
        Requests.Add(new RecordedRequest(request.Method.Method, request.RequestUri!.PathAndQuery, body, authorization));
        if (Delay is { } delay)
        {
            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
        }

        if (request.RequestUri.Host != Host)
        {
            return Error(HttpStatusCode.NotFound, "HostNotFound", "unknown host");
        }

        if (authorization is null || !authorization.StartsWith("Bearer ", StringComparison.Ordinal))
        {
            var challenge = new HttpResponseMessage(HttpStatusCode.Unauthorized) { Content = new StringContent("{\"error\":{\"code\":\"Unauthorized\",\"message\":\"AKV10000: Request is missing a Bearer or PoP token.\"}}") };
            if (ChallengeUnauthenticated)
            {
                challenge.Headers.TryAddWithoutValidation("WWW-Authenticate", "Bearer authorization=\"https://login.microsoftonline.com/00000000-0000-0000-0000-000000000001\", resource=\"https://managedhsm.azure.net\"");
            }

            return challenge;
        }

        if (ForcedError is { } forcedError)
        {
            return Error(forcedError.Status, forcedError.Code, forcedError.Message);
        }

        if (ForcedStatus is { } status)
        {
            var forced = Error(status, status.ToString(), "forced by the test");
            if (status == HttpStatusCode.Unauthorized)
            {
                forced.Headers.TryAddWithoutValidation("WWW-Authenticate", "Bearer authorization=\"https://login.microsoftonline.com/00000000-0000-0000-0000-000000000001\", resource=\"https://managedhsm.azure.net\"");
            }

            return forced;
        }

        var query = request.RequestUri.Query;
        if (!query.Contains("api-version=", StringComparison.Ordinal))
        {
            return Error(HttpStatusCode.BadRequest, "BadParameter", "api-version is required");
        }

        var segments = request.RequestUri.AbsolutePath.Trim('/').Split('/');
        if (segments.Length < 2 || segments[0] != "keys")
        {
            return Error(HttpStatusCode.NotFound, "NotFound", "unknown route");
        }

        if (segments[1] != KeyName)
        {
            return Error(HttpStatusCode.NotFound, "KeyNotFound", "A key with (name/id) was not found in this key vault.");
        }

        if (request.Method == HttpMethod.Put && segments.Length == 2)
        {
            return Import(body);
        }

        if (request.Method == HttpMethod.Get && segments.Length == 3 && segments[2] == "versions")
        {
            return Json(w =>
            {
                w.WriteStartArray("value");
                foreach (var v in versions)
                {
                    w.WriteStartObject();
                    w.WriteString("kid", Kid(v.Id));
                    w.WriteStartObject("attributes");
                    w.WriteNumber("created", v.Created);
                    w.WriteBoolean("enabled", true);
                    w.WriteEndObject();
                    w.WriteEndObject();
                }

                w.WriteEndArray();
                w.WriteNull("nextLink");
            });
        }

        var version = versions.FirstOrDefault(v => v.Id == (segments.Length > 2 ? segments[2] : string.Empty));
        if (version.Id is null)
        {
            return Error(HttpStatusCode.NotFound, "KeyNotFound", "A key with (name/id) was not found in this key vault.");
        }

        if (request.Method == HttpMethod.Get && segments.Length == 3)
        {
            return KeyBundle(version.Id, version.Created);
        }

        if (request.Method == HttpMethod.Post && segments.Length == 4 && segments[3] is "sign" or "verify")
        {
            using var document = JsonDocument.Parse(body ?? "{}");
            var root = document.RootElement;
            if (root.GetProperty("alg").GetString() != "HS256")
            {
                return Error(HttpStatusCode.BadRequest, "BadParameter", "algorithm not supported for this key");
            }

            var key = IgnoreVersionUseLatest ? versions[^1].Key : version.Key;
            var input = FromBase64Url(root.GetProperty(segments[3] == "sign" ? "value" : "digest").GetString()!);
            if (segments[3] == "sign" && RejectEmptyValue && input.Length == 0)
            {
                return Error(HttpStatusCode.BadRequest, "BadParameter", "empty value");
            }

            if (segments[3] == "sign" && MaxValueBytes is { } limit && input.Length > limit && OversizeStatus != HttpStatusCode.BadRequest)
            {
                return Error(OversizeStatus, "RequestEntityTooLarge", "value too large");
            }

            var mac = Mac(key, input);
            if (mac is null)
            {
                return Error(HttpStatusCode.BadRequest, "BadParameter", "Invalid digest length.");
            }

            if (MalformedBody)
            {
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{not json") };
            }

            if (segments[3] == "sign")
            {
                return Json(w =>
                {
                    w.WriteString("kid", WrongKid ? Kid("ffffffffffffffffffffffffffffffff") : Kid(version.Id));
                    w.WriteString("value", ToBase64Url(mac));
                });
            }

            var signature = FromBase64Url(root.GetProperty("value").GetString()!);
            var matches = CryptographicOperations.FixedTimeEquals(mac, signature);
            switch (VerifyStyle)
            {
                case FakeVerifyStyle.Http400OnMismatch when !matches:
                case FakeVerifyStyle.Http400Always:
                    return Error(HttpStatusCode.BadRequest, "BadParameter", VerificationFailedMessage);
                case FakeVerifyStyle.OtherBadRequest:
                    return Error(HttpStatusCode.BadRequest, "BadParameter", "digest length is wrong");
                case FakeVerifyStyle.SameCodeWrongStatus when !matches:
                    return Error(HttpStatusCode.Conflict, "BadParameter", VerificationFailedMessage);
                case FakeVerifyStyle.TokenUnderWrongCode when !matches:
                    return Error(HttpStatusCode.BadRequest, "Forbidden", VerificationFailedMessage);
                case FakeVerifyStyle.TokenOnlyAsSubstring when !matches:
                    return Error(HttpStatusCode.BadRequest, "BadParameter", "XRET_SIGNATURE_VERIFICATION_FAILED_2");
                case FakeVerifyStyle.ServerError:
                    return Error(HttpStatusCode.InternalServerError, "InternalError", "boom");
                case FakeVerifyStyle.AcceptAll:
                    return Json(w => w.WriteBoolean("value", true));
                default:
                    return Json(w => w.WriteBoolean("value", matches));
            }
        }

        return Error(HttpStatusCode.MethodNotAllowed, "MethodNotAllowed", "unsupported");
    }

    private byte[]? Mac(byte[] key, byte[] input)
    {
        if (MaxValueBytes is { } max && input.Length > max)
        {
            return null;
        }

        return Semantics switch
        {
            FakeSemantics.HmacOverValue => HMACSHA256.HashData(key, input),
            FakeSemantics.HashThenHmac => HMACSHA256.HashData(key, SHA256.HashData(input)),
            FakeSemantics.DigestLengthOnly => input.Length == 32 ? HMACSHA256.HashData(key, input) : null,
            FakeSemantics.HashUnlessDigestLength => HMACSHA256.HashData(key, input.Length == 32 ? input : SHA256.HashData(input)),
            _ => null,
        };
    }

    private HttpResponseMessage Import(string? body)
    {
        if (!AllowImport)
        {
            return Error(HttpStatusCode.BadRequest, "BadParameter", "Import of plaintext symmetric keys is not supported.");
        }

        using var document = JsonDocument.Parse(body ?? "{}");
        var key = document.RootElement.GetProperty("key");
        if (key.GetProperty("kty").GetString() != "oct-HSM")
        {
            return Error(HttpStatusCode.BadRequest, "BadParameter", "unsupported kty");
        }

        var id = AddVersion(FromBase64Url(key.GetProperty("k").GetString()!));
        return KeyBundle(id, versions[^1].Created);
    }

    private HttpResponseMessage KeyBundle(string id, long created) => Json(w =>
    {
        w.WriteStartObject("key");
        w.WriteString("kid", Kid(id));
        w.WriteString("kty", "oct-HSM");
        w.WriteStartArray("key_ops");
        w.WriteStringValue("sign");
        w.WriteStringValue("verify");
        w.WriteEndArray();
        w.WriteNumber("key_size", 256);
        w.WriteEndObject();
        w.WriteStartObject("attributes");
        w.WriteBoolean("enabled", true);
        w.WriteBoolean("exportable", Exportable);
        w.WriteNumber("created", created);
        w.WriteEndObject();
    });

    private static string Kid(string id) => $"https://{Host}/keys/{KeyName}/{id}";

    private static HttpResponseMessage Json(Action<Utf8JsonWriter> write)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            write(writer);
            writer.WriteEndObject();
        }

        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(stream.ToArray()) { Headers = { { "Content-Type", "application/json; charset=utf-8" } } } };
    }

    private static HttpResponseMessage Error(HttpStatusCode status, string code, string message) =>
        new(status) { Content = new StringContent($"{{\"error\":{{\"code\":\"{code}\",\"message\":\"{message}\"}}}}", Encoding.UTF8, "application/json") };

    internal static string ToBase64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    internal static byte[] FromBase64Url(string value)
    {
        var padded = value.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(padded.PadRight(padded.Length + ((4 - (padded.Length % 4)) % 4), '='));
    }
}

/// <summary>Test credential: records requested scopes; its token must never appear in any output.</summary>
internal sealed class FakeCredential(bool fail = false) : TokenCredential
{
    public const string TokenValue = "fake-access-token-MUST-NEVER-BE-PRINTED";

    public List<string> RequestedScopes { get; } = [];

    public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
        GetTokenAsync(requestContext, cancellationToken).AsTask().GetAwaiter().GetResult();

    public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken)
    {
        RequestedScopes.AddRange(requestContext.Scopes);
        if (fail)
        {
            throw new InvalidOperationException("no credential available in this test");
        }

        return ValueTask.FromResult(new AccessToken(TokenValue, DateTimeOffset.UtcNow.AddHours(1)));
    }
}
