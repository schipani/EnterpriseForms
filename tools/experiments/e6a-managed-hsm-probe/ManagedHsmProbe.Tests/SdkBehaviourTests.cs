using System.Security.Cryptography;
using System.Text.Json;
using Azure.Core.Pipeline;
using Azure.Security.KeyVault.Keys.Cryptography;
using Xunit;

namespace EnterpriseForms.Experiments.ManagedHsmProbe.Tests;

/// <summary>
/// What the real Azure SDK (Azure.Security.KeyVault.Keys 4.10.2) puts on the wire for HS256, observed through the fake
/// transport. This is local evidence of SDK request behaviour only, not of Managed HSM behaviour.
/// </summary>
public sealed class SdkBehaviourTests
{
    [Fact]
    public async Task SdkSignAsyncSendsThePreHashedDigestUnchanged()
    {
        var (hsm, configuration) = TestSetup.KnownKeys();
        var signer = new SdkDigestSigner(configuration, new FakeCredential(), Transport(hsm));
        var digest = SHA256.HashData(ReferenceVectorGenerator.LengthInput(1024));
        var result = await signer.SignDigestAsync(configuration.KeyVersionIdentifiers["1"], digest, TestContext.Current.CancellationToken);

        Assert.Equal(HsmOutcome.Success, result.Outcome);
        Assert.Equal(digest, SentSignValue(hsm));
        Assert.Equal(HMACSHA256.HashData(TestKeys.Get(TestKeys.TestKey1Id), digest), result.Bytes);
    }

    [Fact]
    public async Task SdkSignDataAsyncHashesTheDataLocallyWithSha256ForHs256()
    {
        var (hsm, configuration) = TestSetup.KnownKeys();
        var data = ReferenceVectorGenerator.LengthInput(1024);
        var client = new CryptographyClient(ManagedHsmUrls.KeyIdentifier(configuration.EndpointUri, FakeManagedHsm.KeyName, configuration.KeyVersionIdentifiers["1"]),
            new FakeCredential(), Options(hsm));
        var result = await client.SignDataAsync(SignatureAlgorithm.HS256, data, TestContext.Current.CancellationToken);

        // The SDK sends SHA-256(data), so its natural HS256 path yields HMAC(key, SHA-256(data)), not the EnterpriseForms MAC.
        Assert.Equal(SHA256.HashData(data), SentSignValue(hsm));
        Assert.Equal(HMACSHA256.HashData(TestKeys.Get(TestKeys.TestKey1Id), SHA256.HashData(data)), result.Signature);
        Assert.NotEqual(HMACSHA256.HashData(TestKeys.Get(TestKeys.TestKey1Id), data), result.Signature);
    }

    [Fact]
    public async Task TheSdkAnswersTheBearerChallengeWithTheManagedHsmScopeAndNeverRetriesThrottling()
    {
        var (hsm, configuration) = TestSetup.KnownKeys();
        var credential = new FakeCredential();
        var signer = new SdkDigestSigner(configuration, credential, Transport(hsm));
        Assert.Equal(HsmOutcome.Success, (await signer.SignDigestAsync(configuration.KeyVersionIdentifiers["1"], new byte[32], TestContext.Current.CancellationToken)).Outcome);
        Assert.Null(hsm.Requests[0].Authorization);
        Assert.Contains(ProbeConfiguration.ManagedHsmTokenScope, credential.RequestedScopes);

        hsm.Requests.Clear();
        hsm.ForcedStatus = System.Net.HttpStatusCode.TooManyRequests;
        var throttled = await signer.SignDigestAsync(configuration.KeyVersionIdentifiers["1"], new byte[32], TestContext.Current.CancellationToken);
        Assert.Equal(HsmOutcome.Throttled, throttled.Outcome);
        // Exactly one request and no retry. Observed: a new CryptographyClient first GETs the key (to decide whether it can
        // operate locally) before it POSTs sign, so the SDK path also needs key read permission and the 429 surfaced on that GET.
        var only = Assert.Single(hsm.Requests);
        Assert.Equal("GET", only.Method);
    }

    [Fact]
    public async Task ASdkSignFetchesTheKeyOnceAndThenSignsRemotelyBecauseOctHsmMaterialIsNeverReturned()
    {
        var (hsm, configuration) = TestSetup.KnownKeys();
        var signer = new SdkDigestSigner(configuration, new FakeCredential(), Transport(hsm));
        await signer.SignDigestAsync(configuration.KeyVersionIdentifiers["1"], new byte[32], TestContext.Current.CancellationToken);
        var authenticated = hsm.Requests.Where(r => r.Authorization is not null).Select(r => r.Method + " " + r.PathAndQuery.Split('?')[0]).ToList();
        var version = configuration.KeyVersionIdentifiers["1"];
        Assert.Equal([$"GET /keys/{FakeManagedHsm.KeyName}/{version}", $"POST /keys/{FakeManagedHsm.KeyName}/{version}/sign"], authenticated);
    }

    private static byte[] SentSignValue(FakeManagedHsm hsm)
    {
        var sign = hsm.Requests.Last(r => r.Method == "POST" && r.PathAndQuery.Contains("/sign", StringComparison.Ordinal) && r.Authorization is not null);
        using var body = JsonDocument.Parse(sign.Body!);
        Assert.Equal("HS256", body.RootElement.GetProperty("alg").GetString());
        return FakeManagedHsm.FromBase64Url(body.RootElement.GetProperty("value").GetString()!);
    }

#pragma warning disable CA2000 // test transport over the fake handler
    private static HttpClientTransport Transport(FakeManagedHsm hsm) => new(new HttpClient(hsm, disposeHandler: false));
#pragma warning restore CA2000

    private static CryptographyClientOptions Options(FakeManagedHsm hsm)
    {
        var options = new CryptographyClientOptions(CryptographyClientOptions.ServiceVersion.V2025_07_01) { Transport = Transport(hsm) };
        options.Retry.MaxRetries = 0;
        return options;
    }
}
