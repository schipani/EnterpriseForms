using System.Net;
using EnterpriseForms.Api.Composition;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EnterpriseForms.Api.ContractTests;

/// <summary>Readiness is tied to the independent Authority: unconfigured or unreachable authority ⇒ 503, reachable ⇒ 200.</summary>
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
}
