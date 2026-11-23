using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace EnterpriseForms.Api.ContractTests;

/// <summary>Authority refuses to start with a forbidden or missing signing provider or identity configuration (ADR-PB-005 §5, G04, PB-08); readiness fails closed without its stores.</summary>
public sealed class AuthoritySigningPolicyTests
{
    [Fact]
    public async Task DevelopmentWithLocalProviderIsLiveButNotReadyWithoutStores()
    {
        using var authority = new WebApplicationFactory<AuthorizationAuthority.Api.Program>().WithWebHostBuilder(b => b.UseEnvironment("Development"));
        using var client = authority.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(new Uri("/internal/v1/health/live", UriKind.Relative), TestContext.Current.CancellationToken)).StatusCode);
        var ready = await client.GetAsync(new Uri("/internal/v1/health/ready", UriKind.Relative), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, ready.StatusCode);
        Assert.Contains("AuthorityDbUnreachable", await ready.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), StringComparison.Ordinal);
    }

    [Fact]
    public void ProductionRefusesLocalProvider()
    {
        using var authority = new WebApplicationFactory<AuthorizationAuthority.Api.Program>().WithWebHostBuilder(b =>
        {
            b.UseEnvironment("Production");
            b.ConfigureAppConfiguration((_, cfg) => cfg.AddInMemoryCollection(new Dictionary<string, string?> { ["Signing:Provider"] = "LocalDevelopment" }));
        });
        StartupRefusal.Assert(authority, "forbidden in Production");
    }

    [Fact]
    public void ProductionRefusesVaultTransit()
    {
        using var authority = new WebApplicationFactory<AuthorizationAuthority.Api.Program>().WithWebHostBuilder(b =>
        {
            b.UseEnvironment("Production");
            b.ConfigureAppConfiguration((_, cfg) => cfg.AddInMemoryCollection(new Dictionary<string, string?> { ["Signing:Provider"] = "VaultTransit", ["Signing:VaultAddress"] = "http://vault:8200" }));
        });
        StartupRefusal.Assert(authority, "forbidden in Production");
    }

    [Fact]
    public void UnconfiguredProviderRefusesStartupInEveryEnvironment()
    {
        using var authority = new WebApplicationFactory<AuthorizationAuthority.Api.Program>().WithWebHostBuilder(b =>
        {
            b.UseEnvironment("Development");
            b.ConfigureAppConfiguration((_, cfg) => cfg.AddInMemoryCollection(new Dictionary<string, string?> { ["Signing:Provider"] = "Unconfigured" }));
        });
        StartupRefusal.Assert(authority, "not configured");
    }

    [Fact]
    public void ProductionWithManagedHsmRefusesStartWithoutEntraIdentityValidation()
    {
        // Stage A implements only the TestIdentityProvider; Production must fail closed until Entra validation (PB-08) exists.
        using var authority = new WebApplicationFactory<AuthorizationAuthority.Api.Program>().WithWebHostBuilder(b =>
        {
            b.UseEnvironment("Production");
            b.ConfigureAppConfiguration((_, cfg) => cfg.AddInMemoryCollection(DevelopmentIdentity(new Dictionary<string, string?> { ["Signing:Provider"] = "AzureManagedHsm", ["Identity:UserAssertion:ProviderKind"] = "EntraId" })));
        });
        StartupRefusal.Assert(authority, "not implemented in Stage A");
    }

    [Fact]
    public void TestIdentityProviderIsRefusedOutsideDevelopmentAndTesting()
    {
        using var authority = new WebApplicationFactory<AuthorizationAuthority.Api.Program>().WithWebHostBuilder(b =>
        {
            b.UseEnvironment("Staging");
            b.ConfigureAppConfiguration((_, cfg) => cfg.AddInMemoryCollection(DevelopmentIdentity(new Dictionary<string, string?> { ["Signing:Provider"] = "AzureManagedHsm" })));
        });
        StartupRefusal.Assert(authority, "permitted only in Development/Testing");
    }

    [Fact]
    public void IdenticalWorkloadAndOperatorSecretsAreRefused()
    {
        using var authority = new WebApplicationFactory<AuthorizationAuthority.Api.Program>().WithWebHostBuilder(b =>
        {
            b.UseEnvironment("Development");
            b.ConfigureAppConfiguration((_, cfg) => cfg.AddInMemoryCollection(new Dictionary<string, string?> { ["Identity:Operator:SharedKeyBase64"] = "d29ya2xvYWQtZGV2LW9ubHktc2hhcmVkLWtleS0wMDAwMDAwMDAwMA==" }));
        });
        StartupRefusal.Assert(authority, "must be distinct");
    }

    private static Dictionary<string, string?> DevelopmentIdentity(Dictionary<string, string?> extra)
    {
        var values = new Dictionary<string, string?>
        {
            ["Identity:Workload:Issuer"] = "enterpriseforms-business-api",
            ["Identity:Workload:Audience"] = "authorization-authority",
            ["Identity:Workload:SharedKeyBase64"] = "d29ya2xvYWQtZGV2LW9ubHktc2hhcmVkLWtleS0wMDAwMDAwMDAwMA==",
            ["Identity:Operator:Issuer"] = "authority-operator",
            ["Identity:Operator:Audience"] = "authorization-authority-operator",
            ["Identity:Operator:SharedKeyBase64"] = "b3BlcmF0b3ItZGV2LW9ubHktc2hhcmVkLWtleS0wMDAwMDAwMDAwMA==",
            ["Identity:UserAssertion:ProviderKind"] = "TestIdentityProvider",
            ["Identity:UserAssertion:Issuer"] = "https://test-idp.enterpriseforms.invalid",
            ["Identity:UserAssertion:Audience"] = "enterpriseforms",
            ["Identity:UserAssertion:SharedKeyBase64"] = "dGVzdC1pZHAtZGV2LW9ubHktc2lnbmluZy1rZXktMDAwMDAwMDAwMA==",
            // RB-CARV-1: outside Development the phase is mandatory; without it the host stops at that refusal first.
            ["ContentAcceptance:EnforcementPhase"] = "ReleaseA",
        };
        foreach (var (key, value) in extra)
        {
            values[key] = value;
        }

        return values;
    }
}
