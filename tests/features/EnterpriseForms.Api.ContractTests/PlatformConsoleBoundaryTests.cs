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
/// C8 M2 Stage 1 (PO-T01, PO-T02, PO-T16, PO-T19, PO-T20) on the real Authority host pipeline: the platform plane accepts only the
/// separate Platform Console credential together with a Platform Operator assertion signed with its own key. The business workload,
/// operator, verifier and reconciler credentials, wrong roles, expired tokens and business user assertions never pass; the console
/// credential reaches no operator-plane or business endpoint; an unconfigured console means nothing passes; the startup guard
/// refuses partial, shared and (outside Development/Testing) certificate-less configurations. The store is unreachable, so a
/// request that passes every check fails closed (5xx), never 200.
/// </summary>
public sealed class PlatformConsoleBoundaryTests
{
    private const string WorkloadKey = "d29ya2xvYWQtZGV2LW9ubHktc2hhcmVkLWtleS0wMDAwMDAwMDAwMA==";
    private const string OperatorKey = "b3BlcmF0b3ItZGV2LW9ubHktc2hhcmVkLWtleS0wMDAwMDAwMDAwMA==";
    private const string UserKey = "dGVzdC1pZHAtZGV2LW9ubHktc2lnbmluZy1rZXktMDAwMDAwMDAwMA==";
    private const string ConsoleKey = "cGxhdGZvcm0tY29uc29sZS10ZXN0LW9ubHktc2hhcmVkLWtleS0wMDAw";
    private const string AssertionKey = "cGxhdGZvcm0tb3BlcmF0b3ItYXNzZXJ0aW9uLXRlc3Qtb25seS1rZXktMDA=";
    private const string ConsoleIssuer = "enterpriseforms-platform-console";
    private const string ConsoleAudience = "authorization-authority-platform";
    private const string AssertionIssuer = "https://platform-operators.test.invalid/";
    private const string AssertionAudience = "authorization-authority-platform-operator";
    private const string Profile = "/internal/v1/platform/operator/me";

    private static string Token(string keyBase64, string issuer, string audience, string subject, Dictionary<string, object>? claims = null, DateTime? expires = null) =>
        new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = issuer,
            Audience = audience,
            IssuedAt = (expires ?? DateTime.UtcNow.AddMinutes(5)).AddMinutes(-10),
            NotBefore = (expires ?? DateTime.UtcNow.AddMinutes(5)).AddMinutes(-10),
            Expires = expires ?? DateTime.UtcNow.AddMinutes(5),
            Claims = new Dictionary<string, object>(claims ?? []) { ["sub"] = subject },
            SigningCredentials = new SigningCredentials(new SymmetricSecurityKey(Convert.FromBase64String(keyBase64)), SecurityAlgorithms.HmacSha256),
        });

    private static string Workload(string keyBase64, string issuer, string audience, string role, DateTime? expires = null) =>
        Token(keyBase64, issuer, audience, "po-boundary-test", new Dictionary<string, object> { ["workload_role"] = role, ["workload_name"] = "po-boundary-test" }, expires);

    private static string Console() => Workload(ConsoleKey, ConsoleIssuer, ConsoleAudience, InternalAuthentication.PlatformConsoleRole);

    private static string OperatorAssertion(string keyBase64 = AssertionKey, string issuer = AssertionIssuer) => Token(keyBase64, issuer, AssertionAudience, "operator-boundary-test");

    private static WebApplicationFactory<AuthorizationAuthority.Api.Program> Authority(bool consoleConfigured, Action<IWebHostBuilder>? more = null) =>
        new WebApplicationFactory<AuthorizationAuthority.Api.Program>().WithWebHostBuilder(b =>
        {
            b.UseEnvironment("Development");
            b.UseSetting("ConnectionStrings:AuthorityDb", "Host=127.0.0.1;Port=9;Database=none;Username=none;Password=none;Timeout=2");
            b.UseSetting("PlatformOperators:Mode", "SyntheticDevelopment");
            if (consoleConfigured)
            {
                b.UseSetting("Identity:PlatformConsole:Issuer", ConsoleIssuer);
                b.UseSetting("Identity:PlatformConsole:Audience", ConsoleAudience);
                b.UseSetting("Identity:PlatformConsole:SharedKeyBase64", ConsoleKey);
                b.UseSetting("Identity:OperatorAssertion:Issuer", AssertionIssuer);
                b.UseSetting("Identity:OperatorAssertion:Audience", AssertionAudience);
                b.UseSetting("Identity:OperatorAssertion:SharedKeyBase64", AssertionKey);
            }

            more?.Invoke(b);
        });

    private static async Task<HttpStatusCode> SendAsync(HttpClient client, HttpMethod method, string path, string? bearer, string? operatorAssertion = null, string? userAssertion = null)
    {
        using var request = new HttpRequestMessage(method, path) { Content = method == HttpMethod.Post ? JsonContent.Create(new { }) : null };
        if (bearer is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        }

        if (operatorAssertion is not null)
        {
            request.Headers.Add(InternalAuthentication.OperatorAssertionHeader, operatorAssertion);
        }

        if (userAssertion is not null)
        {
            request.Headers.Add(InternalAuthentication.UserAssertionHeader, userAssertion);
        }

        request.Headers.Add("X-Correlation-Id", Guid.CreateVersion7().ToString("D"));
        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        return response.StatusCode;
    }

    private static readonly HttpStatusCode[] Refused = [HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden];

    [Fact]
    public async Task OnlyTheConsoleCredentialWithAnOperatorAssertionReachesThePlatformPlane()
    {
        using var authority = Authority(consoleConfigured: true);
        using var client = authority.CreateClient();
        var assertion = OperatorAssertion();
        // Other workloads (with their own role, and claiming the console role), wrong roles, wrong audience, expired, default key, none.
        Assert.Contains(await SendAsync(client, HttpMethod.Get, Profile, Workload(WorkloadKey, "enterpriseforms-business-api", "authorization-authority", "business-api"), assertion), Refused);
        Assert.Contains(await SendAsync(client, HttpMethod.Get, Profile, Workload(WorkloadKey, "enterpriseforms-business-api", "authorization-authority", InternalAuthentication.PlatformConsoleRole), assertion), Refused);
        Assert.Contains(await SendAsync(client, HttpMethod.Get, Profile, Workload(OperatorKey, "authority-operator", "authorization-authority-operator", "authority-operator"), assertion), Refused);
        Assert.Contains(await SendAsync(client, HttpMethod.Get, Profile, Workload(OperatorKey, "authority-operator", "authorization-authority-operator", InternalAuthentication.PlatformConsoleRole), assertion), Refused);
        Assert.Contains(await SendAsync(client, HttpMethod.Get, Profile, Workload(ConsoleKey, ConsoleIssuer, ConsoleAudience, "business-api"), assertion), Refused);
        Assert.Contains(await SendAsync(client, HttpMethod.Get, Profile, Workload(ConsoleKey, ConsoleIssuer, "authorization-authority", InternalAuthentication.PlatformConsoleRole), assertion), Refused);
        Assert.Contains(await SendAsync(client, HttpMethod.Get, Profile, Workload(ConsoleKey, ConsoleIssuer, ConsoleAudience, InternalAuthentication.PlatformConsoleRole, DateTime.UtcNow.AddMinutes(-10)), assertion), Refused);
        Assert.Contains(await SendAsync(client, HttpMethod.Get, Profile, Workload(Convert.ToBase64String(new byte[32]), "unconfigured", "unconfigured", InternalAuthentication.PlatformConsoleRole), assertion), Refused);
        Assert.Equal(HttpStatusCode.Unauthorized, await SendAsync(client, HttpMethod.Get, Profile, null, assertion));

        // The genuine console credential without an operator, with a business user assertion, or with assertions signed by other keys.
        Assert.Equal(HttpStatusCode.Unauthorized, await SendAsync(client, HttpMethod.Get, Profile, Console()));
        Assert.Equal(HttpStatusCode.Unauthorized, await SendAsync(client, HttpMethod.Get, Profile, Console(), userAssertion: Token(UserKey, "https://test-idp.enterpriseforms.invalid", "enterpriseforms", "user-1")));
        Assert.Equal(HttpStatusCode.Unauthorized, await SendAsync(client, HttpMethod.Get, Profile, Console(), Token(UserKey, "https://test-idp.enterpriseforms.invalid", "enterpriseforms", "user-1")));
        Assert.Equal(HttpStatusCode.Unauthorized, await SendAsync(client, HttpMethod.Get, Profile, Console(), OperatorAssertion(ConsoleKey)));
        Assert.Equal(HttpStatusCode.Unauthorized, await SendAsync(client, HttpMethod.Get, Profile, Console(), OperatorAssertion(issuer: "https://elsewhere.invalid/")));

        // Every check passed: the unreachable store fails closed — never 200.
        var passed = await SendAsync(client, HttpMethod.Get, Profile, Console(), assertion);
        Assert.True((int)passed >= 500, passed.ToString());
    }

    [Fact]
    public async Task TheConsoleCredentialReachesNoOperatorPlaneOrBusinessEndpoint()
    {
        using var authority = Authority(consoleConfigured: true);
        using var client = authority.CreateClient();
        var org = Guid.CreateVersion7();
        foreach (var (method, path) in new[]
                 {
                     (HttpMethod.Post, "/internal/v1/operator/platform-operators/bootstrap"),
                     (HttpMethod.Get, "/internal/v1/operator/platform-operators"),
                     (HttpMethod.Post, "/internal/v1/operator/platform-journal/replay"),
                     (HttpMethod.Get, $"/internal/v1/operator/organizations/{org:D}/membership-requests"),
                     (HttpMethod.Post, $"/internal/v1/operator/organizations/{org:D}/membership-requests/{Guid.CreateVersion7():D}/approve"),
                     (HttpMethod.Post, "/internal/v1/security-commands/"),
                     (HttpMethod.Post, "/internal/v1/organizations/roster"),
                     (HttpMethod.Post, "/internal/v1/search-catalogue/"),
                 })
        {
            Assert.Contains(await SendAsync(client, method, path, Console(), OperatorAssertion()), Refused);
        }

        // ...and the operator-plane credential does not reach the platform plane either, even with an operator assertion.
        Assert.Contains(await SendAsync(client, HttpMethod.Post, "/internal/v1/platform/membership-requests/search", Workload(OperatorKey, "authority-operator", "authorization-authority-operator", "authority-operator"), OperatorAssertion()), Refused);
    }

    [Fact]
    public async Task WithoutAConsoleCredentialNothingReachesThePlatformPlane()
    {
        using var authority = Authority(consoleConfigured: false);
        using var client = authority.CreateClient();
        foreach (var token in new[] { Console(), Workload(WorkloadKey, "enterpriseforms-business-api", "authorization-authority", InternalAuthentication.PlatformConsoleRole), Workload(Convert.ToBase64String(new byte[32]), "unconfigured", "unconfigured", InternalAuthentication.PlatformConsoleRole) })
        {
            Assert.Contains(await SendAsync(client, HttpMethod.Get, Profile, token, OperatorAssertion()), Refused);
        }
    }

    [Theory]
    [InlineData("console-only")]
    [InlineData("assertion-only")]
    [InlineData("partial")]
    [InlineData("shares-workload-key")]
    [InlineData("shares-operator-issuer")]
    [InlineData("console-equals-assertion")]
    public void TheStartupGuardRefusesIncompleteOrSharedPlatformCredentials(string variant)
    {
        var console = new CredentialOptions { Issuer = ConsoleIssuer, Audience = ConsoleAudience, SharedKeyBase64 = ConsoleKey };
        var assertion = new CredentialOptions { Issuer = AssertionIssuer, Audience = AssertionAudience, SharedKeyBase64 = AssertionKey };
        var identity = new IdentityOptions
        {
            Workload = new CredentialOptions { Issuer = "enterpriseforms-business-api", Audience = "authorization-authority", SharedKeyBase64 = WorkloadKey },
            Operator = new CredentialOptions { Issuer = "authority-operator", Audience = "authorization-authority-operator", SharedKeyBase64 = OperatorKey },
            PlatformConsole = variant == "assertion-only" ? new CredentialOptions() : console,
            OperatorAssertion = variant == "console-only" ? new CredentialOptions() : assertion,
        };
        switch (variant)
        {
            case "partial":
                identity.OperatorAssertion = new CredentialOptions { Issuer = AssertionIssuer };
                break;
            case "shares-workload-key":
                identity.PlatformConsole = new CredentialOptions { Issuer = ConsoleIssuer, Audience = ConsoleAudience, SharedKeyBase64 = WorkloadKey };
                break;
            case "shares-operator-issuer":
                identity.OperatorAssertion = new CredentialOptions { Issuer = "authority-operator", Audience = AssertionAudience, SharedKeyBase64 = AssertionKey };
                break;
            case "console-equals-assertion":
                identity.OperatorAssertion = new CredentialOptions { Issuer = AssertionIssuer, Audience = AssertionAudience, SharedKeyBase64 = ConsoleKey };
                break;
        }

        Assert.Throws<InvalidOperationException>(() => IdentityPolicyStartupGuard.ValidatePlatformConsole(identity, new FixedEnvironment("Development"), new ConfigurationBuilder().Build()));
    }

    [Theory]
    [InlineData("Production", null, true)]
    [InlineData("Staging", "RequireCertificate", false)]
    [InlineData("Testing", null, false)]
    public void OutsideDevelopmentAndTestingAConfiguredConsoleRequiresClientCertificates(string environmentName, string? clientCertificateMode, bool refused)
    {
        var identity = new IdentityOptions
        {
            Workload = new CredentialOptions { Issuer = "enterpriseforms-business-api", Audience = "authorization-authority", SharedKeyBase64 = WorkloadKey },
            PlatformConsole = new CredentialOptions { Issuer = ConsoleIssuer, Audience = ConsoleAudience, SharedKeyBase64 = ConsoleKey },
            OperatorAssertion = new CredentialOptions { Issuer = AssertionIssuer, Audience = AssertionAudience, SharedKeyBase64 = AssertionKey },
        };
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Kestrel:EndpointDefaults:ClientCertificateMode"] = clientCertificateMode }).Build();
        if (refused)
        {
            Assert.Contains("client certificates", Assert.Throws<InvalidOperationException>(() => IdentityPolicyStartupGuard.ValidatePlatformConsole(identity, new FixedEnvironment(environmentName), configuration)).Message, StringComparison.Ordinal);
        }
        else
        {
            IdentityPolicyStartupGuard.ValidatePlatformConsole(identity, new FixedEnvironment(environmentName), configuration);
        }

        IdentityPolicyStartupGuard.ValidatePlatformConsole(new IdentityOptions(), new FixedEnvironment(environmentName), configuration); // absent: nothing required
    }

    [Fact]
    public void TheSyntheticPlatformModeIsRefusedOutsideDevelopmentAndTesting()
    {
        using var authority = Authority(consoleConfigured: true, b => b.UseEnvironment("Staging"));
        var failure = Assert.ThrowsAny<Exception>(() => authority.CreateClient());
        var messages = new List<string>();
        for (Exception? e = failure; e is not null; e = e.InnerException)
        {
            messages.Add(e.Message);
        }

        Assert.Contains("PlatformOperators:Mode=SyntheticDevelopment is development-only", string.Join(" | ", messages), StringComparison.Ordinal);
    }

    private sealed class FixedEnvironment(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;

        public string ApplicationName { get; set; } = "authority-test";

        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;

        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
