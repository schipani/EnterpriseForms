using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using AuthorizationAuthority.Api.Composition;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Xunit;

namespace EnterpriseForms.Api.ContractTests;

/// <summary>
/// Phase C8 AC-9 (RC-18, RC-27 and the C-1/C-2 guards of RC-28) on the real Authority host pipeline: the outcome lookup accepts
/// only the separate reconciler credential. The business workload, verifier and operator credentials, wrong roles, default
/// keys, expired and wrong-audience tokens never pass; an unconfigured reconciler means nothing passes; a delegated user
/// assertion is refused even with the genuine credential; the rate limit answers 429 with Retry-After. The store is
/// deliberately unreachable, so a request that passes every check fails closed with 503 — never 200.
/// </summary>
public sealed class ReconcilerBoundaryTests
{
    private const string WorkloadKey = "d29ya2xvYWQtZGV2LW9ubHktc2hhcmVkLWtleS0wMDAwMDAwMDAwMA==";
    private const string OperatorKey = "b3BlcmF0b3ItZGV2LW9ubHktc2hhcmVkLWtleS0wMDAwMDAwMDAwMA==";
    private const string VerifierKey = "dmVyaWZpZXItdGVzdC1vbmx5LXNoYXJlZC1rZXktMDAwMDAwMDAw";
    private const string ReconcilerKey = "cmVjb25jaWxlci10ZXN0LW9ubHktc2hhcmVkLWtleS0wMDAwMDA=";
    private const string ReconcilerIssuer = "enterpriseforms-team-membership-reconciler";
    private const string ReconcilerAudience = "authorization-authority-reconciler";
    private const string ReconcilerRole = "business-projection-reconciler";
    private const string Route = "/internal/v1/security-command-outcomes/lookup";

    private static string Token(string keyBase64, string issuer, string audience, string role, DateTime? expires = null) =>
        new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = issuer,
            Audience = audience,
            IssuedAt = (expires ?? DateTime.UtcNow.AddMinutes(5)).AddMinutes(-10),
            NotBefore = (expires ?? DateTime.UtcNow.AddMinutes(5)).AddMinutes(-10),
            Expires = expires ?? DateTime.UtcNow.AddMinutes(5),
            Claims = new Dictionary<string, object> { ["sub"] = "c8-reconciler-test", ["workload_role"] = role, ["workload_name"] = "c8-reconciler-test" },
            SigningCredentials = new SigningCredentials(new SymmetricSecurityKey(Convert.FromBase64String(keyBase64)), SecurityAlgorithms.HmacSha256),
        });

    private static string Genuine() => Token(ReconcilerKey, ReconcilerIssuer, ReconcilerAudience, ReconcilerRole);

    private static WebApplicationFactory<AuthorizationAuthority.Api.Program> Authority(bool reconcilerConfigured, Action<IWebHostBuilder>? more = null) =>
        new WebApplicationFactory<AuthorizationAuthority.Api.Program>().WithWebHostBuilder(b =>
        {
            b.UseEnvironment("Development");
            b.UseSetting("ConnectionStrings:AuthorityDb", "Host=127.0.0.1;Port=9;Database=none;Username=none;Password=none;Timeout=2");
            b.UseSetting("Identity:Verifier:Issuer", "enterpriseforms-field-protection-verifier");
            b.UseSetting("Identity:Verifier:Audience", "authorization-authority-verifier");
            b.UseSetting("Identity:Verifier:SharedKeyBase64", VerifierKey);
            if (reconcilerConfigured)
            {
                b.UseSetting("Identity:Reconciler:Issuer", ReconcilerIssuer);
                b.UseSetting("Identity:Reconciler:Audience", ReconcilerAudience);
                b.UseSetting("Identity:Reconciler:SharedKeyBase64", ReconcilerKey);
            }

            more?.Invoke(b);
        });

    private static async Task<HttpResponseMessage> PostAsync(HttpClient client, string? bearer, object? body = null, bool withCorrelation = true, string? assertion = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, Route)
        {
            Content = JsonContent.Create(body ?? new { organization_id = Guid.CreateVersion7(), idempotency_key = "team-add:" + Guid.NewGuid().ToString("D"), expected_command_kind = "AddTeamMember" }),
        };
        if (bearer is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        }

        if (withCorrelation)
        {
            request.Headers.Add("X-Correlation-Id", Guid.CreateVersion7().ToString("D"));
        }

        if (assertion is not null)
        {
            request.Headers.Add("X-Delegated-User-Assertion", assertion);
        }

        return await client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    private static async Task<HttpStatusCode> StatusAsync(HttpClient client, string? bearer)
    {
        using var response = await PostAsync(client, bearer);
        return response.StatusCode;
    }

    [Fact]
    public async Task RC18OnlyTheReconcilerCredentialReachesTheOutcomeLookup()
    {
        using var authority = Authority(reconcilerConfigured: true);
        using var client = authority.CreateClient();
        var refused = new[] { HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden };
        // Business API workload (its own credential, and claiming the reconciler role), verifier, operator: refused.
        Assert.Contains(await StatusAsync(client, Token(WorkloadKey, "enterpriseforms-business-api", "authorization-authority", "business-api")), refused);
        Assert.Contains(await StatusAsync(client, Token(WorkloadKey, "enterpriseforms-business-api", "authorization-authority", ReconcilerRole)), refused);
        Assert.Contains(await StatusAsync(client, Token(VerifierKey, "enterpriseforms-field-protection-verifier", "authorization-authority-verifier", "field-protection-verifier")), refused);
        Assert.Contains(await StatusAsync(client, Token(VerifierKey, "enterpriseforms-field-protection-verifier", "authorization-authority-verifier", ReconcilerRole)), refused);
        Assert.Contains(await StatusAsync(client, Token(OperatorKey, "authority-operator", "authorization-authority-operator", "authority-operator")), refused);
        // Reconciler key with the wrong role, wrong audience, expired, a default zero key, and no token: refused.
        Assert.Contains(await StatusAsync(client, Token(ReconcilerKey, ReconcilerIssuer, ReconcilerAudience, "business-api")), refused);
        Assert.Contains(await StatusAsync(client, Token(ReconcilerKey, ReconcilerIssuer, "authorization-authority", ReconcilerRole)), refused);
        Assert.Contains(await StatusAsync(client, Token(ReconcilerKey, ReconcilerIssuer, ReconcilerAudience, ReconcilerRole, DateTime.UtcNow.AddMinutes(-10))), refused);
        Assert.Contains(await StatusAsync(client, Token(Convert.ToBase64String(new byte[32]), "unconfigured", "unconfigured", ReconcilerRole)), refused);
        Assert.Equal(HttpStatusCode.Unauthorized, await StatusAsync(client, null));

        // The genuine credential passes authorization; the unreachable store then fails closed (503), never 200.
        Assert.Equal(HttpStatusCode.ServiceUnavailable, await StatusAsync(client, Genuine()));
    }

    [Fact]
    public async Task RC18TheReconcilerCredentialReachesNoOtherInternalEndpoint()
    {
        using var authority = Authority(reconcilerConfigured: true);
        using var client = authority.CreateClient();
        foreach (var (method, path) in new[]
                 {
                     (HttpMethod.Post, "/internal/v1/security-commands/"),
                     (HttpMethod.Get, $"/internal/v1/security-commands/{Guid.CreateVersion7():D}"),
                     (HttpMethod.Post, "/internal/v1/verifier/migration-catalogue"),
                     (HttpMethod.Post, "/internal/v1/search-catalogue/"),
                 })
        {
            using var request = new HttpRequestMessage(method, path) { Content = method == HttpMethod.Post ? JsonContent.Create(new { }) : null };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Genuine());
            using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
            Assert.Contains(response.StatusCode, new[] { HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden });
        }
    }

    [Fact]
    public async Task RC18WithoutAReconcilerCredentialNothingReachesTheOutcomeLookup()
    {
        using var authority = Authority(reconcilerConfigured: false);
        using var client = authority.CreateClient();
        foreach (var token in new[] { Genuine(), Token(WorkloadKey, "enterpriseforms-business-api", "authorization-authority", ReconcilerRole), Token(Convert.ToBase64String(new byte[32]), "unconfigured", "unconfigured", ReconcilerRole) })
        {
            Assert.Contains(await StatusAsync(client, token), new[] { HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden });
        }
    }

    [Fact]
    public async Task RC18ADelegatedAssertionAMissingCorrelationAndMalformedOrDisallowedRequestsAreRefusedBeforeTheStore()
    {
        using var authority = Authority(reconcilerConfigured: true);
        using var client = authority.CreateClient();

        using (var assertion = await PostAsync(client, Genuine(), assertion: "any-user-assertion"))
        {
            Assert.Equal(HttpStatusCode.BadRequest, assertion.StatusCode);
            Assert.Contains("DELEGATED_ASSERTION_NOT_ACCEPTED", await assertion.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), StringComparison.Ordinal);
        }

        using (var noCorrelation = await PostAsync(client, Genuine(), withCorrelation: false))
        {
            Assert.Equal(HttpStatusCode.BadRequest, noCorrelation.StatusCode);
        }

        foreach (var body in new object[]
                 {
                     new { organization_id = Guid.CreateVersion7(), idempotency_key = "short", expected_command_kind = "AddTeamMember" },
                     new { organization_id = Guid.CreateVersion7(), idempotency_key = "team-add:" + Guid.NewGuid(), expected_command_kind = "NoSuchKind" },
                     new { organization_id = Guid.CreateVersion7(), idempotency_key = "team-add:" + Guid.NewGuid(), expected_command_kind = "1" },
                     new { organization_id = "not-a-uuid", idempotency_key = "team-add:" + Guid.NewGuid(), expected_command_kind = "AddTeamMember" },
                     new { team_id = Guid.CreateVersion7() },
                 })
        {
            using var response = await PostAsync(client, Genuine(), body);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }

        using var disallowed = await PostAsync(client, Genuine(), new { organization_id = Guid.CreateVersion7(), idempotency_key = "team-add:" + Guid.NewGuid(), expected_command_kind = "GrantSystemRole" });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, disallowed.StatusCode);
        Assert.Contains("KIND_NOT_RECONCILABLE", await disallowed.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), StringComparison.Ordinal);
    }

    [Fact]
    public async Task RC27TheRateLimitAnswers429WithRetryAfter()
    {
        using var authority = Authority(reconcilerConfigured: true, b =>
        {
            b.UseSetting("Reconciler:LookupRateLimit:TokensPerSecond", "1");
            b.UseSetting("Reconciler:LookupRateLimit:BurstLimit", "1");
        });
        using var client = authority.CreateClient();
        // Burst 1: of several simultaneous lookups at most one token is available, so the others are limited at once.
        var responses = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => PostAsync(client, Genuine())));
        var limited = responses.FirstOrDefault(r => r.StatusCode == HttpStatusCode.TooManyRequests);
        foreach (var response in responses.Where(r => !ReferenceEquals(r, limited)))
        {
            response.Dispose();
        }

        Assert.NotNull(limited);
        using (limited)
        {
            Assert.True(limited.Headers.RetryAfter?.Delta is { } delta && delta >= TimeSpan.FromSeconds(1), "429 must carry Retry-After");
            Assert.Contains("RATE_LIMITED", await limited.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData("0", "1")]
    [InlineData("51", "60")]
    [InlineData("20", "10")]
    public void RC28OutOfBoundsRateLimitsRefuseToStart(string perSecond, string burst)
    {
        using var authority = Authority(reconcilerConfigured: true, b =>
        {
            b.UseSetting("Reconciler:LookupRateLimit:TokensPerSecond", perSecond);
            b.UseSetting("Reconciler:LookupRateLimit:BurstLimit", burst);
        });
        var failure = Assert.ThrowsAny<Exception>(() => authority.CreateClient());
        Assert.Contains("Reconciler:LookupRateLimit", Flatten(failure), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("partial")]
    [InlineData("workload-key")]
    [InlineData("verifier-key")]
    [InlineData("operator-key")]
    [InlineData("workload-issuer")]
    [InlineData("verifier-issuer")]
    public void C2APartialOrReusedReconcilerCredentialRefusesToStart(string variant)
    {
        using var authority = new WebApplicationFactory<AuthorizationAuthority.Api.Program>().WithWebHostBuilder(b =>
        {
            b.UseEnvironment("Development");
            b.UseSetting("Identity:Verifier:Issuer", "enterpriseforms-field-protection-verifier");
            b.UseSetting("Identity:Verifier:Audience", "authorization-authority-verifier");
            b.UseSetting("Identity:Verifier:SharedKeyBase64", VerifierKey);
            b.UseSetting("Identity:Reconciler:Issuer", variant switch
            {
                "workload-issuer" => "enterpriseforms-business-api",
                "verifier-issuer" => "enterpriseforms-field-protection-verifier",
                _ => ReconcilerIssuer,
            });
            if (variant != "partial")
            {
                b.UseSetting("Identity:Reconciler:Audience", ReconcilerAudience);
                b.UseSetting("Identity:Reconciler:SharedKeyBase64", variant switch
                {
                    "workload-key" => WorkloadKey,
                    "verifier-key" => VerifierKey,
                    "operator-key" => OperatorKey,
                    _ => ReconcilerKey,
                });
            }
        });
        var failure = Assert.ThrowsAny<Exception>(() => authority.CreateClient());
        Assert.Contains(variant == "partial" ? "configured completely" : "own key and issuer", Flatten(failure), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Production", null, true)]
    [InlineData("Staging", "AllowCertificate", true)]
    [InlineData("Staging", "RequireCertificate", false)]
    [InlineData("Testing", null, false)]
    [InlineData("Development", null, false)]
    public void C1OutsideDevelopmentAndTestingAConfiguredReconcilerRequiresClientCertificates(string environmentName, string? clientCertificateMode, bool refused)
    {
        var identity = new IdentityOptions
        {
            Workload = new CredentialOptions { Issuer = "enterpriseforms-business-api", Audience = "authorization-authority", SharedKeyBase64 = WorkloadKey },
            Reconciler = new CredentialOptions { Issuer = ReconcilerIssuer, Audience = ReconcilerAudience, SharedKeyBase64 = ReconcilerKey },
        };
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Kestrel:EndpointDefaults:ClientCertificateMode"] = clientCertificateMode }).Build();
        var environment = new FixedEnvironment(environmentName);
        if (refused)
        {
            var failure = Assert.Throws<InvalidOperationException>(() => IdentityPolicyStartupGuard.ValidateReconciler(identity, environment, configuration));
            Assert.Contains("client certificates", failure.Message, StringComparison.Ordinal);
        }
        else
        {
            IdentityPolicyStartupGuard.ValidateReconciler(identity, environment, configuration);
        }

        // An absent reconciler credential never requires anything (the policy simply denies everything).
        IdentityPolicyStartupGuard.ValidateReconciler(new IdentityOptions(), environment, configuration);
    }

    private sealed class FixedEnvironment(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;

        public string ApplicationName { get; set; } = "authority-test";

        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;

        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private static string Flatten(Exception exception)
    {
        var text = string.Empty;
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            text += current.Message + "\n";
        }

        return text;
    }
}
