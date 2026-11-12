using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AuthorizationAuthority.Application.Common.Ports;
using AuthorizationAuthority.Application.Features.SearchIndex;
using AuthorizationAuthority.Infrastructure.Signing;
using Azure.Core;
using Xunit;

namespace AuthorizationAuthority.Tests;

/// <summary>
/// IX-7 (D-IX1-13): the Azure Managed HSM ES256 seal signer against a fake Managed HSM endpoint (no real HSM was available: no staging
/// deployment). The fake holds a P-256 key and answers the REST shapes of <c>keys/{name}/{version}/sign</c> and <c>keys/{name}/{version}</c>.
/// Shown: the digest that is signed, P1363 output the local verifier accepts, the bearer token of the managed identity, the key checks
/// (EC-HSM, P-256, sign, non-exportable, no release policy), the kid binding, the version mapping, and fail-closed answers.
/// </summary>
public sealed class ManagedHsmSearchSealSignerTests : IDisposable
{
    private const string KeyName = "search-entry-seal-v1";
    private const string VersionId = "0123456789abcdef0123456789abcdef";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly ECDsa hsmKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);

    public void Dispose() => hsmKey.Dispose();

    private sealed class FakeCredential(bool fail = false) : TokenCredential
    {
        public List<string[]> Scopes { get; } = [];

        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken) => GetTokenAsync(requestContext, cancellationToken).AsTask().Result;

        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken)
        {
            Scopes.Add(requestContext.Scopes);
            return fail ? throw new Azure.Identity.AuthenticationFailedException("no managed identity") : ValueTask.FromResult(new AccessToken("token-123", DateTimeOffset.UtcNow.AddHours(1)));
        }
    }

    private sealed class FakeHsm(ECDsa key, Func<HttpRequestMessage, HttpResponseMessage?>? intercept = null) : HttpMessageHandler
    {
        public List<(HttpMethod Method, string Path, string? Authorization, string? Body)> Requests { get; } = [];

        public bool Exportable { get; set; }

        public string KeyType { get; set; } = "EC-HSM";

        public string? KidOverride { get; set; }

        private static readonly string[] KeyOperations = ["sign", "verify"];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add((request.Method, request.RequestUri!.PathAndQuery, request.Headers.Authorization?.ToString(), body));
            if (intercept?.Invoke(request) is { } intercepted)
            {
                return intercepted;
            }

            var kid = KidOverride ?? $"https://hsm.example/keys/{KeyName}/{VersionId}";
            if (request.Method == HttpMethod.Post && request.RequestUri!.AbsolutePath == $"/keys/{KeyName}/{VersionId}/sign")
            {
                using var json = JsonDocument.Parse(body!);
                var digest = Decode(json.RootElement.GetProperty("value").GetString()!);
                Assert.Equal("ES256", json.RootElement.GetProperty("alg").GetString());
                var signature = key.SignHash(digest, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
                return Json(new { kid, value = Encode(signature) });
            }

            if (request.Method == HttpMethod.Get && request.RequestUri!.AbsolutePath == $"/keys/{KeyName}/{VersionId}")
            {
                var p = key.ExportParameters(false);
                return Json(new { key = new { kid, kty = KeyType, crv = "P-256", key_ops = KeyOperations, x = Encode(p.Q.X!), y = Encode(p.Q.Y!) }, attributes = new { enabled = true, exportable = Exportable } });
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        private static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json") };
    }

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private (ManagedHsmSearchSealSigner Signer, FakeHsm Hsm, FakeCredential Credential) Make(Func<HttpRequestMessage, HttpResponseMessage?>? intercept = null, bool failToken = false, string? baseUrl = "https://hsm.example/")
    {
        var hsm = new FakeHsm(hsmKey, intercept);
        var credential = new FakeCredential(failToken);
        var options = new ManagedHsmSealOptions { VaultBaseUrl = baseUrl, KeyVersionIdentifiers = { ["1"] = VersionId } };
        return (new ManagedHsmSearchSealSigner(new Factory(hsm), credential, options, new SearchSealOptions { Provider = SearchSealProviderKind.AzureManagedHsm, SigningKeyName = KeyName, SigningKeyVersion = 1 }), hsm, credential);
    }

    private static string Encode(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] Decode(string value)
    {
        var padded = value.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(padded.PadRight(padded.Length + ((4 - (padded.Length % 4)) % 4), '='));
    }

    [Fact]
    public async Task ItSignsTheSha256DigestWithES256AndTheSignatureVerifiesAgainstTheHsmPublicKey()
    {
        var (signer, hsm, credential) = Make();
        var message = Encoding.UTF8.GetBytes("DynamicForms/SearchEntrySeal/v1 synthetic message");
        var signature = await signer.SignAsync(message, Ct);
        Assert.Equal((64, KeyName, 1), (signature.Signature.Length, signature.KeyName, signature.KeyVersion));
        using var verifier = ECDsa.Create();
        verifier.ImportSubjectPublicKeyInfo(await signer.GetPublicKeyAsync(KeyName, 1, Ct), out _);
        Assert.True(verifier.VerifyData(message, signature.Signature, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation));
        Assert.Equal(Encode(SHA256.HashData(message)), JsonDocument.Parse(hsm.Requests[0].Body!).RootElement.GetProperty("value").GetString()); // only the digest leaves
        Assert.All(hsm.Requests, r => Assert.Equal("Bearer token-123", r.Authorization));
        Assert.All(hsm.Requests, r => Assert.Contains("api-version=7.4", r.Path, StringComparison.Ordinal));
        Assert.All(credential.Scopes, s => Assert.Equal(["https://managedhsm.azure.net/.default"], s));
        Assert.Equal("AzureManagedHsm", signer.ProviderName);
    }

    [Theory]
    [InlineData(429)]
    [InlineData(401)]
    [InlineData(403)]
    [InlineData(404)]
    [InlineData(500)]
    public async Task EveryHsmFailureFailsClosed(int status)
    {
        var (signer, _, _) = Make(_ => new HttpResponseMessage((HttpStatusCode)status));
        await Assert.ThrowsAsync<SearchSealUnavailableException>(() => signer.SignAsync(new byte[] { 1, 2, 3 }, Ct));
        await Assert.ThrowsAsync<SearchSealUnavailableException>(() => signer.GetPublicKeyAsync(KeyName, 1, Ct));
    }

    [Fact]
    public async Task AnUnreachableHsmAMissingTokenAndAMalformedAnswerFailClosed()
    {
        var (unreachable, _, _) = Make(_ => throw new HttpRequestException("down"));
        await Assert.ThrowsAsync<SearchSealUnavailableException>(() => unreachable.SignAsync(new byte[] { 1 }, Ct));
        var (tokenless, hsm, _) = Make(failToken: true);
        await Assert.ThrowsAsync<SearchSealUnavailableException>(() => tokenless.SignAsync(new byte[] { 1 }, Ct));
        Assert.Empty(hsm.Requests); // nothing is sent without a token
        var (garbled, _, _) = Make(r => r.Method == HttpMethod.Post ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"kid\":\"https://hsm.example/keys/search-entry-seal-v1/0123456789abcdef0123456789abcdef\",\"value\":\"AAEC\"}") } : null);
        await Assert.ThrowsAsync<SearchSealUnavailableException>(() => garbled.SignAsync(new byte[] { 1 }, Ct)); // not 64 bytes
        var (plainHttp, _, _) = Make(baseUrl: "http://hsm.example/");
        await Assert.ThrowsAsync<SearchSealUnavailableException>(() => plainHttp.SignAsync(new byte[] { 1 }, Ct));
    }

    [Fact]
    public async Task OnlyANonExportableEcHsmP256SigningKeyOfTheConfiguredKidIsAccepted()
    {
        var (exportable, hsm1, _) = Make();
        hsm1.Exportable = true;
        await Assert.ThrowsAsync<SearchSealUnavailableException>(() => exportable.GetPublicKeyAsync(KeyName, 1, Ct));
        var (software, hsm2, _) = Make();
        hsm2.KeyType = "EC"; // a software key, not HSM-backed
        await Assert.ThrowsAsync<SearchSealUnavailableException>(() => software.GetPublicKeyAsync(KeyName, 1, Ct));
        var (otherKey, hsm3, _) = Make();
        hsm3.KidOverride = "https://hsm.example/keys/another-key/" + VersionId;
        await Assert.ThrowsAsync<SearchSealUnavailableException>(() => otherKey.SignAsync(new byte[] { 1 }, Ct));
        await Assert.ThrowsAsync<SearchSealUnavailableException>(() => otherKey.GetPublicKeyAsync(KeyName, 1, Ct));
    }

    [Fact]
    public async Task AnUnmappedKeyVersionIsRefusedBeforeAnyCall()
    {
        var (signer, hsm, _) = Make();
        await Assert.ThrowsAsync<SearchSealUnavailableException>(() => signer.GetPublicKeyAsync(KeyName, 2, Ct));
        Assert.Empty(hsm.Requests);
    }
}
