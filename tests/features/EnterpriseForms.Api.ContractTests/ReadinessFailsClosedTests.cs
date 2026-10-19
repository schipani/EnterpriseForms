using System.Net;
using EnterpriseForms.Api.Composition;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Xunit;

namespace EnterpriseForms.Api.ContractTests;

/// <summary>Readiness is tied to the independent Authority: unconfigured or unreachable authority ⇒ 503, reachable ⇒ 200. Protected routes require a user assertion.</summary>
public sealed class ReadinessFailsClosedTests
{
    [Fact]
    public async Task LivenessDoesNotDependOnAuthority()
    {
        using var api = new WebApplicationFactory<Program>().WithWebHostBuilder(b => b.UseEnvironment("Development"));
        using var client = api.CreateClient();
        var response = await client.GetAsync(new Uri("/health/live", UriKind.Relative), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
    }

    [Fact]
    public async Task ReadinessIs503WhenAuthorityIsNotConfigured()
    {
        using var api = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseEnvironment("Development");
            b.ConfigureAppConfiguration((_, cfg) => cfg.AddInMemoryCollection(new Dictionary<string, string?> { ["TrustAuthority:BaseAddress"] = string.Empty }));
        });
        using var client = api.CreateClient();
        var response = await client.GetAsync(new Uri("/health/ready", UriKind.Relative), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Contains("AuthorityNotConfigured", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReadinessIs503WhenAuthorityIsUnreachable()
    {
        using var api = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseEnvironment("Development");
            b.ConfigureAppConfiguration((_, cfg) => cfg.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["TrustAuthority:BaseAddress"] = "http://127.0.0.1:9",
                ["TrustAuthority:ReadinessTimeoutSeconds"] = "1",
            }));
        });
        using var client = api.CreateClient();
        var response = await client.GetAsync(new Uri("/health/ready", UriKind.Relative), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    [Fact]
    public async Task ReadinessIs200WhenTheRealAuthorityHostIsLive()
    {
        using var authority = new WebApplicationFactory<AuthorizationAuthority.Api.Program>().WithWebHostBuilder(b => b.UseEnvironment("Development"));
        var authorityHandler = authority.Server.CreateHandler();

        using var api = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseEnvironment("Development");
            b.ConfigureAppConfiguration((_, cfg) => cfg.AddInMemoryCollection(new Dictionary<string, string?> { ["TrustAuthority:BaseAddress"] = "http://authority.test" }));
            b.ConfigureServices(services =>
                services.AddHttpClient(TrustAuthorityReadinessProbe.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => authorityHandler));
        });

        using var client = api.CreateClient();
        var response = await client.GetAsync(new Uri("/health/ready", UriKind.Relative), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task ProtectedRoutesRequireAnAssertion()
    {
        using var api = new WebApplicationFactory<Program>().WithWebHostBuilder(b => b.UseEnvironment("Development"));
        using var client = api.CreateClient();
        var response = await client.GetAsync(new Uri("/api/v1/form-records/" + Guid.NewGuid().ToString("D"), UriKind.Relative), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task TestIdentityProviderIsRefusedInStaging()
    {
        // Deterministic check of the guard itself (A-F19: when the host fails to start, WebApplicationFactory may surface
        // "Cannot access a disposed object" instead of the guard's message, so the message is asserted on the guard directly).
        var guard = new UserAuthenticationStartupGuard(
            Options.Create(new UserAuthenticationOptions
            {
                ProviderKind = "TestIdentityProvider",
                Issuer = "https://test-idp.enterpriseforms.invalid",
                Audience = "enterpriseforms",
                SharedKeyBase64 = "dGVzdC1pZHAtZGV2LW9ubHktc2lnbmluZy1rZXktMDAwMDAwMDAwMA==",
            }),
            new FixedEnvironment("Staging"));
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => guard.StartAsync(TestContext.Current.CancellationToken));
        Assert.Contains("not permitted in Staging", ex.Message, StringComparison.Ordinal);

        // And the host as a whole refuses to start with that configuration (fail closed), whatever exception surfaces.
        using var api = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseEnvironment("Staging");
            b.ConfigureAppConfiguration((_, cfg) => cfg.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["UserAuthentication:ProviderKind"] = "TestIdentityProvider",
                ["UserAuthentication:Issuer"] = "https://test-idp.enterpriseforms.invalid",
                ["UserAuthentication:Audience"] = "enterpriseforms",
                ["UserAuthentication:SharedKeyBase64"] = "dGVzdC1pZHAtZGV2LW9ubHktc2lnbmluZy1rZXktMDAwMDAwMDAwMA==",
            }));
        });
        Assert.ThrowsAny<Exception>(() => api.CreateClient());
    }

    [Fact]
    public async Task TestIdentityProviderIsAcceptedOnlyWhenConfiguredInDevelopmentOrTesting()
    {
        var configured = new UserAuthenticationOptions { ProviderKind = "TestIdentityProvider", Issuer = "https://test-idp.enterpriseforms.invalid", Audience = "enterpriseforms", SharedKeyBase64 = "dGVzdC1pZHAtZGV2LW9ubHktc2lnbmluZy1rZXktMDAwMDAwMDAwMA==" };
        await new UserAuthenticationStartupGuard(Options.Create(configured), new FixedEnvironment("Development")).StartAsync(TestContext.Current.CancellationToken);
        await new UserAuthenticationStartupGuard(Options.Create(configured), new FixedEnvironment("Testing")).StartAsync(TestContext.Current.CancellationToken);
        foreach (var env in new[] { "Staging", "Production" })
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => new UserAuthenticationStartupGuard(Options.Create(configured), new FixedEnvironment(env)).StartAsync(TestContext.Current.CancellationToken));
        }

        var unconfigured = new UserAuthenticationOptions { ProviderKind = "TestIdentityProvider" };
        await Assert.ThrowsAsync<InvalidOperationException>(() => new UserAuthenticationStartupGuard(Options.Create(unconfigured), new FixedEnvironment("Development")).StartAsync(TestContext.Current.CancellationToken));
    }

    private sealed class FixedEnvironment(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;

        public string ApplicationName { get; set; } = "EnterpriseForms.Api";

        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;

        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
