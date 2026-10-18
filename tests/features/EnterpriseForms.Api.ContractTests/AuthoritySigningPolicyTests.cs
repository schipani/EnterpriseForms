using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace EnterpriseForms.Api.ContractTests;

/// <summary>Authority refuses to start with a forbidden or missing signing provider (ADR-PB-005 §5, G04).</summary>
public sealed class AuthoritySigningPolicyTests
{
    [Fact]
    public async Task DevelopmentWithLocalProviderIsLiveAndReadyWithTestOnlyLabel()
    {
        using var authority = new WebApplicationFactory<AuthorizationAuthority.Api.Program>().WithWebHostBuilder(b => b.UseEnvironment("Development"));
        using var client = authority.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(new Uri("/internal/v1/health/live", UriKind.Relative), TestContext.Current.CancellationToken)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(new Uri("/internal/v1/health/ready", UriKind.Relative), TestContext.Current.CancellationToken)).StatusCode);
    }

    [Fact]
    public void ProductionRefusesLocalProvider()
    {
        using var authority = new WebApplicationFactory<AuthorizationAuthority.Api.Program>().WithWebHostBuilder(b =>
        {
            b.UseEnvironment("Production");
            b.ConfigureAppConfiguration((_, cfg) => cfg.AddInMemoryCollection(new Dictionary<string, string?> { ["Signing:Provider"] = "LocalDevelopment" }));
        });
        var ex = Assert.ThrowsAny<Exception>(() => authority.CreateClient());
        Assert.Contains("forbidden in Production", Flatten(ex), StringComparison.Ordinal);
    }

    [Fact]
    public void ProductionRefusesVaultTransit()
    {
        using var authority = new WebApplicationFactory<AuthorizationAuthority.Api.Program>().WithWebHostBuilder(b =>
        {
            b.UseEnvironment("Production");
            b.ConfigureAppConfiguration((_, cfg) => cfg.AddInMemoryCollection(new Dictionary<string, string?> { ["Signing:Provider"] = "VaultTransit", ["Signing:VaultAddress"] = "http://vault:8200" }));
        });
        Assert.Contains("forbidden in Production", Flatten(Assert.ThrowsAny<Exception>(() => authority.CreateClient())), StringComparison.Ordinal);
    }

    [Fact]
    public void UnconfiguredProviderRefusesStartupInEveryEnvironment()
    {
        using var authority = new WebApplicationFactory<AuthorizationAuthority.Api.Program>().WithWebHostBuilder(b =>
        {
            b.UseEnvironment("Development");
            b.ConfigureAppConfiguration((_, cfg) => cfg.AddInMemoryCollection(new Dictionary<string, string?> { ["Signing:Provider"] = "Unconfigured" }));
        });
        Assert.Contains("not configured", Flatten(Assert.ThrowsAny<Exception>(() => authority.CreateClient())), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ProductionWithManagedHsmStartsButIsNotReadyUntilProviderIsImplemented()
    {
        using var authority = new WebApplicationFactory<AuthorizationAuthority.Api.Program>().WithWebHostBuilder(b =>
        {
            b.UseEnvironment("Production");
            b.ConfigureAppConfiguration((_, cfg) => cfg.AddInMemoryCollection(new Dictionary<string, string?> { ["Signing:Provider"] = "AzureManagedHsm" }));
        });
        using var client = authority.CreateClient();
        var response = await client.GetAsync(new Uri("/internal/v1/health/ready", UriKind.Relative), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Contains("AzureManagedHsmProviderNotImplemented", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), StringComparison.Ordinal);
    }

    private static string Flatten(Exception ex)
    {
        var messages = new List<string>();
        for (Exception? e = ex; e is not null; e = e.InnerException)
        {
            messages.Add(e.Message);
        }

        return string.Join(" | ", messages);
    }
}
