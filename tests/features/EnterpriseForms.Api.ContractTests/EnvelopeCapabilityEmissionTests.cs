using System.Net;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AuthorizationAuthority.Api.Composition;
using AuthorizationAuthority.Application.Common;
using EnterpriseForms.Application.Common.Authority;
using EnterpriseForms.Application.Features.Search.IndexBuilder;
using EnterpriseForms.Infrastructure.Authority;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Xunit;
using AuthorityPrepareRequest = AuthorizationAuthority.Contracts.V1.PrepareContentChangeRequest;
using BusinessPrepareRequest = EnterpriseForms.Application.Common.Authority.PrepareContentChangeRequest;

namespace EnterpriseForms.Api.ContractTests;

/// <summary>
/// E3-M2f-1 (<c>docs/security/E3_M2F_CONTRACT_CLOSURE.md</c> §3, §12.1; tests M2F-01..M2F-05): the three content workload clients
/// declare <c>EnterpriseForms-Envelope-Capability: 2</c> on every Authority request; the reconciler does not. The declaration is
/// a fixed build constant that no caller, user, organization or configuration can change. Bodies, request digests, idempotency
/// keys and workload credentials are unchanged. The existing Authority (no E3-M2f-2 enforcement) gives identical outcomes with any
/// header value or none.
/// </summary>
public sealed class EnvelopeCapabilityEmissionTests
{
    private const string WorkloadKey = "d29ya2xvYWQtZGV2LW9ubHktc2hhcmVkLWtleS0wMDAwMDAwMDAwMA==";
    private const string WorkloadIssuer = "enterpriseforms-business-api";
    private const string WorkloadAudience = "authorization-authority";
    private const string UserAssertionKey = "dGVzdC1pZHAtZGV2LW9ubHktc2lnbmluZy1rZXktMDAwMDAwMDAwMA==";
    private const string UserAssertionIssuer = "https://test-idp.enterpriseforms.invalid";
    private const string UserAssertionAudience = "enterpriseforms";
    private const string VerifierKey = "dmVyaWZpZXItdGVzdC1vbmx5LXNoYXJlZC1rZXktMDAwMDAwMDAw";
    private const string VerifierIssuer = "enterpriseforms-field-protection-verifier";
    private const string VerifierAudience = "authorization-authority-verifier";
    private const string BuilderIssuer = "enterpriseforms-search-index-builder";
    private const string BuilderAudience = "authorization-authority-search-index-builder";
    private const string AuthorityBase = "http://authority.invalid:8080";
    private static readonly string BuilderKey = Convert.ToBase64String(Encoding.ASCII.GetBytes("search-index-builder-e3m2f1-test-only-key-0000"));
    private static readonly string ReconcilerKey = Convert.ToBase64String(Encoding.ASCII.GetBytes("team-reconciler-e3m2f1-test-only-shared-key-00"));

    /// <summary>The business clients' wire options (AuthorityHttpClient.Json), replicated to prove the body is exactly the request.</summary>
    private static readonly JsonSerializerOptions ClientJson = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DictionaryKeyPolicy = JsonNamingPolicy.SnakeCaseLower,
        Converters = { new JsonStringEnumConverter() },
    };

    // ---- M2F-01..M2F-03: every request path of the three clients declares the capability exactly once ----

    [Fact]
    public async Task EveryAuthorityHttpClientRequestDeclaresCapabilityTwoExactlyOnce()
    {
        var handler = new RecordingHandler();
        var covered = await InvokeEveryPublicMethodAsync(AuthorityClient(handler), handler);
        Assert.True(covered.Count >= 30, $"expected the full AuthorityHttpClient surface, covered {covered.Count}");
        Assert.Contains("PrepareContentChangeAsync", covered);
        Assert.Contains("CommitContentChangeAsync", covered);
        Assert.Contains("VerifyRecordProjectionAsync", covered);
        Assert.Contains("VerifyRecordProjectionsAsync", covered);
        Assert.Contains("PrepareReprotectionAsync", covered);
        Assert.Contains("CommitReprotectionAsync", covered);
        AssertEveryRequestDeclaresTwo(handler);
    }

    [Fact]
    public async Task EveryProtectionVerifierRequestDeclaresCapabilityTwoExactlyOnce()
    {
        var handler = new RecordingHandler();
        var covered = await InvokeEveryPublicMethodAsync(VerifierClient(handler), handler);
        Assert.Equal(["AttestAsync", "GetCoverageAsync", "IssueMigrationCatalogueAsync", "ReadMigrationCatalogueAsync", "TakeCensusAsync"], covered.Order(StringComparer.Ordinal));
        AssertEveryRequestDeclaresTwo(handler);
    }

    [Fact]
    public async Task EverySearchIndexBuilderRequestDeclaresCapabilityTwoExactlyOnce()
    {
        var handler = new RecordingHandler();
        var covered = await InvokeEveryPublicMethodAsync(BuilderClient(handler), handler);
        Assert.Equal(["AttestAsync", "BackfillAsync", "DeepVerifyAsync", "GetDeepSweepWorkAsync", "GetWorkAsync", "ReadPopulationAsync"], covered.Order(StringComparer.Ordinal));
        AssertEveryRequestDeclaresTwo(handler);
    }

    // ---- M2F-04: the reconciler submits no content and is not covered (CAP-S2) ----

    [Fact]
    public async Task TheTeamMembershipReconcilerDoesNotDeclareTheCapability()
    {
        var handler = new RecordingHandler();
        var reconciler = new TeamMembershipReconcilerHttpClient(Factory(handler), Options.Create(new TeamMembershipReconcilerOptions { Issuer = "enterpriseforms-team-membership-reconciler", Audience = "authorization-authority-reconciler", SharedKeyBase64 = ReconcilerKey }),
            Options.Create(new AuthorityClientOptions { BaseAddress = AuthorityBase }), TimeProvider.System);
        try
        {
            await reconciler.LookupAsync(Guid.CreateVersion7(), "e3-m2f1-reconciler-key", "AddTeamMember", Guid.CreateVersion7(), TestContext.Current.CancellationToken);
        }
        catch (ReconcilerLookupException)
        {
            // The stub answers 503; only the outgoing request matters here.
        }

        var request = Assert.Single(handler.Requests);
        Assert.False(request.Headers.ContainsKey(EnvelopeCapability.HeaderName));
    }

    // ---- Retries: every attempt is a fresh request through the same send path and declares the capability ----

    [Fact]
    public async Task ARetriedPrepareDeclaresTheCapabilityOnEveryAttemptWithAnUnchangedBodyAndIdempotencyKey()
    {
        var attempt = 0;
        var handler = new RecordingHandler(_ => ++attempt == 1 ? Error(HttpStatusCode.ServiceUnavailable, "AUTHORITY_NOT_READY", true) : Error(HttpStatusCode.Conflict, "IDEMPOTENCY_CONFLICT", false));
        var client = AuthorityClient(handler);
        var user = User(Guid.CreateVersion7());
        var prepare = Prepare(user.OrganizationId, reprotection: null);
        const string key = "e3-m2f1-retry-idempotency-key";

        var first = await Assert.ThrowsAsync<AuthorityClientException>(() => client.PrepareContentChangeAsync(user, prepare, key, TestContext.Current.CancellationToken));
        Assert.True(first.Retryable);
        await Assert.ThrowsAsync<AuthorityClientException>(() => client.PrepareContentChangeAsync(user, prepare, key, TestContext.Current.CancellationToken));

        Assert.Equal(2, handler.Requests.Count);
        AssertEveryRequestDeclaresTwo(handler);
        Assert.Equal(handler.Requests[0].Body, handler.Requests[1].Body);
        Assert.Equal([key], handler.Requests[0].Headers["Idempotency-Key"]);
        Assert.Equal([key], handler.Requests[1].Headers["Idempotency-Key"]);
        Assert.NotEqual(handler.Requests[0].Headers["X-Correlation-Id"], handler.Requests[1].Headers["X-Correlation-Id"]); // unchanged behaviour: a fresh correlation per attempt
    }

    // ---- No caller can override the declared capability ----

    [Fact]
    public void TheDeclaredCapabilityIsAFixedBuildConstant()
    {
        Assert.Equal("EnterpriseForms-Envelope-Capability", EnvelopeCapability.HeaderName);
        Assert.Equal("2", EnvelopeCapability.DeclaredLevel);
        var declare = typeof(EnvelopeCapability).GetMethod("Declare", BindingFlags.NonPublic | BindingFlags.Static)!;
        Assert.True(declare.IsAssembly, "Declare must stay internal to the business Infrastructure assembly");
        Assert.Equal([typeof(HttpRequestMessage)], declare.GetParameters().Select(p => p.ParameterType));
        foreach (var options in new[] { typeof(AuthorityClientOptions), typeof(ProtectionVerifierOptions), typeof(SearchIndexBuilderOptions) })
        {
            Assert.DoesNotContain(options.GetProperties(), p => p.Name.Contains("Capab", StringComparison.OrdinalIgnoreCase) || p.Name.Contains("Envelope", StringComparison.OrdinalIgnoreCase));
        }
    }

    [Fact]
    public void DeclaringReplacesAnyPresetValueWithExactlyOneTwo()
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "internal/v1/content-changes/prepare");
        request.Headers.Add(EnvelopeCapability.HeaderName, "1");
        request.Headers.Add(EnvelopeCapability.HeaderName, "3");
        EnvelopeCapability.Declare(request);
        Assert.Equal(["2"], request.Headers.GetValues(EnvelopeCapability.HeaderName));
    }

    [Fact]
    public async Task ConfigurationUsersAndOrganizationsCannotChangeTheDeclaredCapability()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["TrustAuthority:BaseAddress"] = AuthorityBase,
            ["TrustAuthority:WorkloadIssuer"] = WorkloadIssuer,
            ["TrustAuthority:WorkloadAudience"] = WorkloadAudience,
            ["TrustAuthority:WorkloadSharedKeyBase64"] = WorkloadKey,
            ["TrustAuthority:EnvelopeCapability"] = "1",
            ["TrustAuthority:DeclaredLevel"] = "1",
            ["TrustAuthority:EnterpriseForms-Envelope-Capability"] = "1",
            ["Content:MinimumBusinessEnvelopeCapability"] = "1",
        }).Build();
        var bound = configuration.GetSection(AuthorityClientOptions.SectionName).Get<AuthorityClientOptions>()!;
        var handler = new RecordingHandler();
        var client = new AuthorityHttpClient(Factory(handler), Options.Create(bound), TimeProvider.System);
        foreach (var organization in new[] { Guid.CreateVersion7(), Guid.CreateVersion7() })
        {
            var user = User(organization);
            await IgnoreAuthorityErrorAsync(() => client.VerifyRecordProjectionAsync(user, new VerifyRecordProjectionRequest(organization, Guid.CreateVersion7(), ProtectedAction.Read, []), TestContext.Current.CancellationToken));
        }

        Assert.Equal(2, handler.Requests.Count);
        AssertEveryRequestDeclaresTwo(handler);
    }

    // ---- M2F-05: request digests, bodies and idempotency semantics are unchanged ----

    [Fact]
    public async Task ThePrepareBodyIsExactlyTheRequestAndTheAuthorityDigestDependsOnTheBodyOnly()
    {
        var handler = new RecordingHandler();
        var client = AuthorityClient(handler);
        var user = User(Guid.CreateVersion7());
        var prepare = Prepare(user.OrganizationId, reprotection: null);
        await IgnoreAuthorityErrorAsync(() => client.PrepareContentChangeAsync(user, prepare, "e3-m2f1-digest-key-0001", TestContext.Current.CancellationToken));
        await IgnoreAuthorityErrorAsync(() => client.PrepareReprotectionAsync(prepare with { ReprotectionCommandId = Guid.CreateVersion7() }, "e3-m2f1-digest-key-0002", TestContext.Current.CancellationToken));

        var userPrepare = handler.Requests[0];
        Assert.Equal(JsonSerializer.SerializeToUtf8Bytes(prepare, ClientJson), userPrepare.Body);
        using (var document = JsonDocument.Parse(userPrepare.Body))
        {
            Assert.DoesNotContain(document.RootElement.EnumerateObject(), p => p.Name.Contains("capab", StringComparison.OrdinalIgnoreCase));
        }

        // The Authority computes the Prepare digest from the deserialized body alone (ContentService.PrepareCoreAsync); the header
        // is not part of its input. The digest of the captured body equals the digest of the same request serialized with no HTTP at all.
        var authorityJson = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        AuthorityJson.Configure(authorityJson);
        var received = JsonSerializer.Deserialize<AuthorityPrepareRequest>(userPrepare.Body, authorityJson)!;
        var direct = JsonSerializer.Deserialize<AuthorityPrepareRequest>(JsonSerializer.SerializeToUtf8Bytes(prepare, ClientJson), authorityJson)!;
        Assert.Equal(
            CanonicalJson.Digest(CanonicalJson.SerializeBytes(direct with { ExpectedAuthorizationRevision = 0 })),
            CanonicalJson.Digest(CanonicalJson.SerializeBytes(received with { ExpectedAuthorizationRevision = 0 })));

        Assert.Equal(["e3-m2f1-digest-key-0001"], handler.Requests[0].Headers["Idempotency-Key"]);
        Assert.Equal(["e3-m2f1-digest-key-0002"], handler.Requests[1].Headers["Idempotency-Key"]);
        AssertEveryRequestDeclaresTwo(handler);
    }

    // ---- Authentication and workload credentials are unaffected ----

    [Fact]
    public async Task HeadersAndWorkloadCredentialsAreUnchangedApartFromTheDeclaration()
    {
        var handler = new RecordingHandler();
        var user = User(Guid.CreateVersion7());
        await IgnoreAuthorityErrorAsync(() => AuthorityClient(handler).PrepareContentChangeAsync(user, Prepare(user.OrganizationId, null), "e3-m2f1-header-key-0001", TestContext.Current.CancellationToken));
        await IgnoreAuthorityErrorAsync(() => AuthorityClient(handler).PrepareReprotectionAsync(Prepare(user.OrganizationId, Guid.CreateVersion7()), "e3-m2f1-header-key-0002", TestContext.Current.CancellationToken));
        await IgnoreAuthorityErrorAsync(() => VerifierClient(handler).GetCoverageAsync(user.OrganizationId, TestContext.Current.CancellationToken));
        await IgnoreAuthorityErrorAsync(() => BuilderClient(handler).GetWorkAsync(TestContext.Current.CancellationToken));

        Assert.Equal(["Authorization", "EnterpriseForms-Envelope-Capability", "Idempotency-Key", "X-Correlation-Id", "X-Delegated-User-Assertion"], handler.Requests[0].Headers.Keys.Order(StringComparer.Ordinal));
        Assert.Equal([user.RawAssertion], handler.Requests[0].Headers["X-Delegated-User-Assertion"]);
        Assert.Equal(["Authorization", "EnterpriseForms-Envelope-Capability", "Idempotency-Key", "X-Correlation-Id"], handler.Requests[1].Headers.Keys.Order(StringComparer.Ordinal));
        Assert.Equal(["Authorization", "EnterpriseForms-Envelope-Capability", "X-Correlation-Id"], handler.Requests[2].Headers.Keys.Order(StringComparer.Ordinal));
        Assert.Equal(["Authorization", "EnterpriseForms-Envelope-Capability", "X-Correlation-Id"], handler.Requests[3].Headers.Keys.Order(StringComparer.Ordinal));

        await AssertWorkloadTokenAsync(handler.Requests[0], WorkloadKey, WorkloadIssuer, WorkloadAudience, "business-api", "enterpriseforms-api");
        await AssertWorkloadTokenAsync(handler.Requests[1], WorkloadKey, WorkloadIssuer, WorkloadAudience, "business-api", "enterpriseforms-api");
        await AssertWorkloadTokenAsync(handler.Requests[2], VerifierKey, VerifierIssuer, VerifierAudience, "field-protection-verifier", "field-protection-verifier");
        await AssertWorkloadTokenAsync(handler.Requests[3], BuilderKey, BuilderIssuer, BuilderAudience, "search-index-builder", "search-index-builder");
    }

    // ---- Existing Authority compatibility: the real host pipeline ignores the header (no E3-M2f-2 enforcement) ----

    [Fact]
    public async Task TheExistingAuthorityGivesIdenticalOutcomesWithAnyCapabilityValueOrNone()
    {
        using var authority = new WebApplicationFactory<AuthorizationAuthority.Api.Program>().WithWebHostBuilder(b =>
        {
            b.UseEnvironment("Development");
            b.UseSetting("ConnectionStrings:AuthorityDb", "Host=127.0.0.1;Port=9;Database=none;Username=none;Password=none;Timeout=2");
            b.UseSetting("Identity:Verifier:Issuer", VerifierIssuer);
            b.UseSetting("Identity:Verifier:Audience", VerifierAudience);
            b.UseSetting("Identity:Verifier:SharedKeyBase64", VerifierKey);
            b.UseSetting("Identity:SearchIndexBuilder:Issuer", BuilderIssuer);
            b.UseSetting("Identity:SearchIndexBuilder:Audience", BuilderAudience);
            b.UseSetting("Identity:SearchIndexBuilder:SharedKeyBase64", BuilderKey);
        });
        using var started = authority.CreateClient();
        var recorder = new RecordingHandler(inner: authority.Server.CreateHandler());
        var user = User(Guid.CreateVersion7(), UserAssertion());
        var cancellation = TestContext.Current.CancellationToken;

        // The real clients against the real Authority host: every content entry point of the three planes.
        var business = AuthorityClient(recorder, "http://localhost/");
        await IgnoreAuthorityErrorAsync(() => business.PrepareContentChangeAsync(user, Prepare(user.OrganizationId, null), "e3-m2f1-compat-key-0001", cancellation));
        await IgnoreAuthorityErrorAsync(() => business.CommitContentChangeAsync(user, Guid.CreateVersion7(), new CommitContentChangeRequest(user.OrganizationId, Guid.CreateVersion7(), []), cancellation));
        await IgnoreAuthorityErrorAsync(() => business.VerifyRecordProjectionAsync(user, new VerifyRecordProjectionRequest(user.OrganizationId, Guid.CreateVersion7(), ProtectedAction.Read, []), cancellation));
        await IgnoreAuthorityErrorAsync(() => business.VerifyRecordProjectionsAsync(user, new VerifyRecordProjectionsRequest(user.OrganizationId, [new BatchVerificationItem(Guid.CreateVersion7(), Guid.CreateVersion7(), [])]), cancellation));
        await IgnoreAuthorityErrorAsync(() => VerifierClient(recorder, "http://localhost/").TakeCensusAsync(Guid.CreateVersion7(), [], null, cancellation));
        await IgnoreAuthorityErrorAsync(() => BuilderClient(recorder, "http://localhost/").BackfillAsync(user.OrganizationId, Guid.CreateVersion7(), [], cancellation));
        await IgnoreAuthorityErrorAsync(() => BuilderClient(recorder, "http://localhost/").DeepVerifyAsync(user.OrganizationId, [], cancellation));
        Assert.Equal(7, recorder.Requests.Count);
        AssertEveryRequestDeclaresTwo(recorder);

        using var raw = new HttpClient(authority.Server.CreateHandler()) { BaseAddress = new Uri("http://localhost/") };
        foreach (var sent in recorder.Requests)
        {
            var original = (sent.ResponseStatus, ErrorCode(sent.ResponseBody));
            Assert.NotEqual(HttpStatusCode.BadRequest, sent.ResponseStatus); // the declaration is never rejected as malformed
            foreach (var variant in new string[]?[] { null, ["1"], ["2"], ["3"], ["not-a-level"], ["1", "2"] })
            {
                using var replay = new HttpRequestMessage(sent.Method, sent.Uri);
                foreach (var (name, values) in sent.Headers.Where(h => h.Key != EnvelopeCapability.HeaderName))
                {
                    replay.Headers.TryAddWithoutValidation(name, values);
                }

                if (variant is not null)
                {
                    replay.Headers.TryAddWithoutValidation(EnvelopeCapability.HeaderName, variant);
                }

                replay.Content = new ByteArrayContent(sent.Body);
                replay.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
                using var response = await raw.SendAsync(replay, cancellation);
                var body = await response.Content.ReadAsByteArrayAsync(cancellation);
                Assert.Equal(original, (response.StatusCode, ErrorCode(body)));
            }
        }
    }

    // ---- helpers ----

    private static void AssertEveryRequestDeclaresTwo(RecordingHandler handler)
    {
        Assert.NotEmpty(handler.Requests);
        foreach (var request in handler.Requests)
        {
            Assert.True(request.Headers.TryGetValue(EnvelopeCapability.HeaderName, out var values), $"{request.Uri} carries no capability");
            Assert.Equal(["2"], values);
        }
    }

    /// <summary>Invokes every public instance method of the client once with synthetic arguments; each must send exactly one request.</summary>
    private static async Task<List<string>> InvokeEveryPublicMethodAsync(object client, RecordingHandler handler)
    {
        var covered = new List<string>();
        var user = User(Guid.CreateVersion7());
        foreach (var method in client.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly).Where(m => !m.IsSpecialName).OrderBy(m => m.Name, StringComparer.Ordinal))
        {
            var before = handler.Requests.Count;
            var arguments = method.GetParameters().Select(p => Argument(p.ParameterType, user, method.Name)).ToArray();
            await IgnoreAuthorityErrorAsync(() => (Task)method.Invoke(client, arguments)!);
            Assert.True(handler.Requests.Count == before + 1, $"{method.Name} sent {handler.Requests.Count - before} requests");
            covered.Add(method.Name);
        }

        return covered;
    }

    private static object? Argument(Type type, DelegatedUserContext user, string methodName)
    {
        if (type == typeof(CancellationToken))
        {
            return TestContext.Current.CancellationToken;
        }

        if (type == typeof(DelegatedUserContext))
        {
            return user;
        }

        if (type == typeof(BusinessPrepareRequest))
        {
            return Prepare(user.OrganizationId, methodName == "PrepareReprotectionAsync" ? Guid.CreateVersion7() : null);
        }

        if (type == typeof(Guid))
        {
            return Guid.CreateVersion7();
        }

        if (type == typeof(string))
        {
            return "e3-m2f1-synthetic-argument";
        }

        if (type == typeof(int))
        {
            return 1;
        }

        if (type == typeof(long))
        {
            return 1L;
        }

        if (type == typeof(bool))
        {
            return false;
        }

        if (type == typeof(DateTimeOffset))
        {
            return DateTimeOffset.UtcNow.AddMinutes(30);
        }

        if (Nullable.GetUnderlyingType(type) is not null)
        {
            return null;
        }

        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IReadOnlyList<>))
        {
            var element = type.GetGenericArguments()[0];
            var array = Array.CreateInstance(element, 1);
            array.SetValue(Argument(element, user, methodName), 0);
            return array;
        }

        if (type.IsEnum)
        {
            return Enum.GetValues(type).GetValue(0);
        }

        if (type == typeof(JsonElement))
        {
            using var empty = JsonDocument.Parse("{}");
            return empty.RootElement.Clone();
        }

        if (type.IsValueType)
        {
            return Activator.CreateInstance(type);
        }

        // Records and DTOs: the widest public constructor with synthetic arguments, so every member is serializable.
        var constructor = type.GetConstructors().OrderByDescending(c => c.GetParameters().Length).FirstOrDefault();
        return constructor is null
            ? RuntimeHelpers.GetUninitializedObject(type)
            : constructor.Invoke(constructor.GetParameters().Select(p => Argument(p.ParameterType, user, methodName)).ToArray());
    }

    private static async Task IgnoreAuthorityErrorAsync(Func<Task> call)
    {
        try
        {
            await call();
        }
        catch (AuthorityClientException)
        {
        }
    }

    private static BusinessPrepareRequest Prepare(Guid organizationId, Guid? reprotection) =>
        new(organizationId, Guid.CreateVersion7(), Guid.CreateVersion7(), 7, 3, Convert.ToBase64String(new byte[32]),
            [new CandidateContentItem(ManifestItemKind.FieldValue, Guid.CreateVersion7(), Convert.ToBase64String(Encoding.ASCII.GetBytes("canonical-bytes")))], reprotection,
            [new StagedContentItem(ManifestItemKind.RecordHeader, Guid.CreateVersion7(), Convert.ToBase64String(Encoding.ASCII.GetBytes("current")), Convert.ToBase64String(new byte[32]))]);

    private static DelegatedUserContext User(Guid organizationId, string? assertion = null) =>
        new(UserAssertionIssuer, "e3-m2f1-user", organizationId, assertion ?? "e3-m2f1-synthetic-assertion");

    private static string UserAssertion() =>
        new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = UserAssertionIssuer,
            Audience = UserAssertionAudience,
            IssuedAt = DateTime.UtcNow,
            NotBefore = DateTime.UtcNow.AddSeconds(-5),
            Expires = DateTime.UtcNow.AddMinutes(5),
            Claims = new Dictionary<string, object> { ["sub"] = "e3-m2f1-user" },
            SigningCredentials = new SigningCredentials(new SymmetricSecurityKey(Convert.FromBase64String(UserAssertionKey)), SecurityAlgorithms.HmacSha256),
        });

    private static async Task AssertWorkloadTokenAsync(CapturedRequest request, string keyBase64, string issuer, string audience, string role, string name)
    {
        var authorization = Assert.Single(request.Headers["Authorization"]);
        Assert.StartsWith("Bearer ", authorization, StringComparison.Ordinal);
        var token = authorization["Bearer ".Length..];
        var result = await new JsonWebTokenHandler().ValidateTokenAsync(token, new TokenValidationParameters
        {
            ValidIssuer = issuer,
            ValidAudience = audience,
            IssuerSigningKey = new SymmetricSecurityKey(Convert.FromBase64String(keyBase64)),
            ValidAlgorithms = [SecurityAlgorithms.HmacSha256],
        });
        Assert.True(result.IsValid, $"{request.Uri}: workload token invalid");
        Assert.Equal(role, result.Claims["workload_role"]);
        Assert.Equal(name, result.Claims["workload_name"]);
        Assert.DoesNotContain(result.Claims, c => c.Key.Contains("capab", StringComparison.OrdinalIgnoreCase) || (c.Value?.ToString() ?? string.Empty).Contains("capab", StringComparison.OrdinalIgnoreCase));
    }

    private static string? ErrorCode(byte[] body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            return document.RootElement.TryGetProperty("error_code", out var code) ? code.GetString() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static HttpResponseMessage Error(HttpStatusCode status, string code, bool retryable) =>
        new(status) { Content = new StringContent($"{{\"error_code\":\"{code}\",\"retryable\":{(retryable ? "true" : "false")}}}", Encoding.UTF8, "application/json") };

    private static IHttpClientFactory Factory(HttpMessageHandler handler)
    {
        var services = new ServiceCollection();
        foreach (var name in new[] { AuthorityHttpClient.HttpClientName, ProtectionVerifierHttpClient.HttpClientName, SearchIndexBuilderHttpClient.HttpClientName, TeamMembershipReconcilerHttpClient.HttpClientName })
        {
            services.AddHttpClient(name).ConfigurePrimaryHttpMessageHandler(() => handler);
        }

        return services.BuildServiceProvider().GetRequiredService<IHttpClientFactory>();
    }

    private static AuthorityHttpClient AuthorityClient(HttpMessageHandler handler, string baseAddress = AuthorityBase) =>
        new(Factory(handler), Options.Create(new AuthorityClientOptions { BaseAddress = baseAddress, WorkloadIssuer = WorkloadIssuer, WorkloadAudience = WorkloadAudience, WorkloadSharedKeyBase64 = WorkloadKey }), TimeProvider.System);

    private static ProtectionVerifierHttpClient VerifierClient(HttpMessageHandler handler, string baseAddress = AuthorityBase) =>
        new(Factory(handler), Options.Create(new ProtectionVerifierOptions { Issuer = VerifierIssuer, Audience = VerifierAudience, SharedKeyBase64 = VerifierKey }), Options.Create(new AuthorityClientOptions { BaseAddress = baseAddress }), TimeProvider.System);

    private static SearchIndexBuilderHttpClient BuilderClient(HttpMessageHandler handler, string baseAddress = AuthorityBase) =>
        new(Factory(handler), Options.Create(new SearchIndexBuilderOptions { Issuer = BuilderIssuer, Audience = BuilderAudience, SharedKeyBase64 = BuilderKey }), Options.Create(new AuthorityClientOptions { BaseAddress = baseAddress }), TimeProvider.System);

    private sealed record CapturedRequest(HttpMethod Method, Uri Uri, Dictionary<string, string[]> Headers, byte[] Body, HttpStatusCode ResponseStatus, byte[] ResponseBody);

    /// <summary>Records each request (headers, body) and either answers it (default: 503 AUTHORITY_NOT_READY) or forwards it to an inner handler.</summary>
    private sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage>? respond = null, HttpMessageHandler? inner = null) : DelegatingHandler(inner ?? new NotUsedHandler())
    {
        private readonly bool forward = inner is not null;

        public List<CapturedRequest> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? [] : await request.Content.ReadAsByteArrayAsync(cancellationToken);
            var headers = request.Headers.ToDictionary(h => h.Key, h => h.Value.ToArray(), StringComparer.Ordinal);
            var response = forward ? await base.SendAsync(request, cancellationToken) : respond?.Invoke(request) ?? Error(HttpStatusCode.ServiceUnavailable, "AUTHORITY_NOT_READY", true);
            var responseBody = await response.Content.ReadAsByteArrayAsync(cancellationToken);
            Requests.Add(new CapturedRequest(request.Method, request.RequestUri!, headers, body, response.StatusCode, responseBody));
            var copy = new HttpResponseMessage(response.StatusCode) { Content = new ByteArrayContent(responseBody) };
            copy.Content.Headers.ContentType = response.Content.Headers.ContentType;
            response.Dispose();
            return copy;
        }
    }

    private sealed class NotUsedHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => throw new InvalidOperationException("not forwarding");
    }
}
