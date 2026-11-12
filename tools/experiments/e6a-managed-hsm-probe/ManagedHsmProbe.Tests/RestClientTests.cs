using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Xunit;

namespace EnterpriseForms.Experiments.ManagedHsmProbe.Tests;

/// <summary>Direct REST request shapes, explicit versions, failure classification, timeouts and cancellation (fake data plane).</summary>
public sealed class RestClientTests
{
    [Fact]
    public async Task SignSendsExactlyTheGivenBytesAsUnpaddedBase64UrlWithAnExplicitVersionAndApiVersion()
    {
        var (hsm, configuration) = TestSetup.KnownKeys();
        var credential = new FakeCredential();
        var client = TestSetup.Client(hsm, configuration, credential);
        var value = Enumerable.Range(0, 33).Select(i => (byte)(250 + i)).ToArray(); // includes bytes that need '-' / '_' in base64url
        var result = await client.SignAsync(configuration.KeyVersionIdentifiers["1"], value, TestContext.Current.CancellationToken);

        Assert.Equal(HsmOutcome.Success, result.Outcome);
        var request = Assert.Single(hsm.Requests);
        Assert.Equal("POST", request.Method);
        Assert.Equal($"/keys/{FakeManagedHsm.KeyName}/{configuration.KeyVersionIdentifiers["1"]}/sign?api-version=2025-07-01", request.PathAndQuery);
        using var body = JsonDocument.Parse(request.Body!);
        Assert.Equal(["alg", "value"], body.RootElement.EnumerateObject().Select(p => p.Name));
        Assert.Equal("HS256", body.RootElement.GetProperty("alg").GetString());
        var sent = body.RootElement.GetProperty("value").GetString()!;
        Assert.DoesNotContain('=', sent);
        Assert.Equal(value, FakeManagedHsm.FromBase64Url(sent));
        Assert.Equal([ProbeConfiguration.ManagedHsmTokenScope], credential.RequestedScopes);
        Assert.Equal(HMACSHA256.HashData(TestKeys.Get(TestKeys.TestKey1Id), value), result.Bytes);
    }

    [Fact]
    public async Task VerifySendsDigestAndSignatureAndReturnsTheBoolean()
    {
        var (hsm, configuration) = TestSetup.KnownKeys();
        var client = TestSetup.Client(hsm, configuration);
        var digest = SHA256.HashData([1, 2, 3]);
        var mac = HMACSHA256.HashData(TestKeys.Get(TestKeys.TestKey2Id), digest);
        var good = await client.VerifyAsync(configuration.KeyVersionIdentifiers["2"], digest, mac, TestContext.Current.CancellationToken);
        var wrongVersion = await client.VerifyAsync(configuration.KeyVersionIdentifiers["1"], digest, mac, TestContext.Current.CancellationToken);
        Assert.True(good.Verified);
        Assert.False(wrongVersion.Verified);
        using var body = JsonDocument.Parse(hsm.Requests[0].Body!);
        Assert.Equal(["alg", "digest", "value"], body.RootElement.EnumerateObject().Select(p => p.Name));
    }

    [Fact]
    public void NoRequestCanBeBuiltWithoutAnExplicitVersion()
    {
        var endpoint = FakeManagedHsm.Endpoint;
        Assert.Throws<ArgumentException>(() => ManagedHsmUrls.Sign(endpoint, FakeManagedHsm.KeyName, string.Empty, "2025-07-01"));
        Assert.Throws<ArgumentException>(() => ManagedHsmUrls.Verify(endpoint, FakeManagedHsm.KeyName, " ", "2025-07-01"));
        Assert.Throws<ArgumentException>(() => ManagedHsmUrls.Key(endpoint, FakeManagedHsm.KeyName, "../versions", "2025-07-01"));
        Assert.Throws<ArgumentException>(() => ManagedHsmUrls.Sign(endpoint, "a/b", "abc", "2025-07-01"));
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, nameof(HsmOutcome.Unauthenticated))]
    [InlineData(HttpStatusCode.Forbidden, nameof(HsmOutcome.Forbidden))]
    [InlineData(HttpStatusCode.NotFound, nameof(HsmOutcome.NotFound))]
    [InlineData(HttpStatusCode.TooManyRequests, nameof(HsmOutcome.Throttled))]
    [InlineData(HttpStatusCode.ServiceUnavailable, nameof(HsmOutcome.ServiceError))]
    [InlineData(HttpStatusCode.BadRequest, nameof(HsmOutcome.Rejected))]
    public async Task HttpFailuresAreClassifiedAndCarryTheServiceErrorCode(HttpStatusCode status, string expectedOutcome)
    {
        var expected = Enum.Parse<HsmOutcome>(expectedOutcome);
        var (hsm, configuration) = TestSetup.KnownKeys();
        hsm.ForcedStatus = status;
        var result = await TestSetup.Client(hsm, configuration).SignAsync(configuration.KeyVersionIdentifiers["1"], new byte[32], TestContext.Current.CancellationToken);
        Assert.Equal(expected, result.Outcome);
        Assert.Equal((int)status, result.HttpStatus);
        Assert.Equal(status.ToString(), result.ErrorCode);
        Assert.Equal(expected is not (HsmOutcome.NotFound or HsmOutcome.Rejected), result.IsInfrastructureFailure);
        if (status == HttpStatusCode.Unauthorized)
        {
            Assert.Equal("https://managedhsm.azure.net", result.ChallengeResource);
        }
    }

    [Fact]
    public async Task AMissingVersionIsNotFoundAndAWrongKidOrMalformedBodyIsNeverASuccess()
    {
        var (hsm, configuration) = TestSetup.KnownKeys();
        var client = TestSetup.Client(hsm, configuration);
        Assert.Equal(HsmOutcome.NotFound, (await client.SignAsync("0123456789abcdef0123456789abcdef", new byte[32], TestContext.Current.CancellationToken)).Outcome);
        hsm.WrongKid = true;
        Assert.Equal(HsmOutcome.WrongKeyIdentifier, (await client.SignAsync(configuration.KeyVersionIdentifiers["1"], new byte[32], TestContext.Current.CancellationToken)).Outcome);
        hsm.WrongKid = false;
        hsm.MalformedBody = true;
        var malformed = await client.SignAsync(configuration.KeyVersionIdentifiers["1"], new byte[32], TestContext.Current.CancellationToken);
        Assert.Equal(HsmOutcome.Malformed, malformed.Outcome);
        Assert.Null(malformed.Bytes);
    }

    [Fact]
    public async Task ACredentialFailureIsUnauthenticatedAndSendsNoRequest()
    {
        var (hsm, configuration) = TestSetup.KnownKeys();
        var result = await TestSetup.Client(hsm, configuration, new FakeCredential(fail: true)).SignAsync(configuration.KeyVersionIdentifiers["1"], new byte[32], TestContext.Current.CancellationToken);
        Assert.Equal(HsmOutcome.Unauthenticated, result.Outcome);
        Assert.Empty(hsm.Requests);
    }

    [Fact]
    public async Task ASlowServiceTimesOutWithinTheConfiguredBound()
    {
        var (hsm, configuration) = TestSetup.KnownKeys();
        configuration.OperationTimeoutSeconds = 1;
        hsm.Delay = TimeSpan.FromSeconds(30);
        var started = DateTime.UtcNow;
        var result = await TestSetup.Client(hsm, configuration).SignAsync(configuration.KeyVersionIdentifiers["1"], new byte[32], TestContext.Current.CancellationToken);
        Assert.Equal(HsmOutcome.Timeout, result.Outcome);
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task CallerCancellationPropagatesInsteadOfBeingReportedAsATimeout()
    {
        var (hsm, configuration) = TestSetup.KnownKeys();
        configuration.OperationTimeoutSeconds = 30;
        hsm.Delay = TimeSpan.FromSeconds(30);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cancellation.CancelAfter(TimeSpan.FromMilliseconds(200));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => TestSetup.Client(hsm, configuration).SignAsync(configuration.KeyVersionIdentifiers["1"], new byte[32], cancellation.Token));
    }

    [Fact]
    public async Task ImportSendsAnOctHsmSignVerifyNonExportableJwkAndReturnsTheNewVersion()
    {
        var (hsm, configuration) = TestSetup.KnownKeys();
        var result = await TestSetup.Client(hsm, configuration).ImportOctKeyAsync(TestKeys.Get(TestKeys.TestKey1Id), TestContext.Current.CancellationToken);
        Assert.Equal(HsmOutcome.Success, result.Outcome);
        Assert.Equal(hsm.VersionIds[^1], result.LatestVersionIdentifier);
        var request = Assert.Single(hsm.Requests);
        Assert.Equal("PUT", request.Method);
        Assert.Equal($"/keys/{FakeManagedHsm.KeyName}?api-version=2025-07-01", request.PathAndQuery);
        using var body = JsonDocument.Parse(request.Body!);
        var key = body.RootElement.GetProperty("key");
        Assert.Equal("oct-HSM", key.GetProperty("kty").GetString());
        Assert.Equal(["sign", "verify"], key.GetProperty("key_ops").EnumerateArray().Select(e => e.GetString()));
        Assert.True(body.RootElement.GetProperty("Hsm").GetBoolean());
        Assert.False(body.RootElement.GetProperty("attributes").GetProperty("exportable").GetBoolean());
        Assert.False(result.Body!.Value.GetProperty("key").TryGetProperty("k", out _));
    }

    [Fact]
    public async Task ListingVersionsReturnsEveryIdentifierAndTheLatestByCreationTime()
    {
        var (hsm, configuration) = TestSetup.KnownKeys();
        var result = await TestSetup.Client(hsm, configuration).ListKeyVersionsAsync(TestContext.Current.CancellationToken);
        Assert.Equal(HsmOutcome.Success, result.Outcome);
        Assert.Equal(hsm.VersionIds, result.VersionIdentifiers);
        Assert.Equal(hsm.VersionIds[^1], result.LatestVersionIdentifier);
    }
}
