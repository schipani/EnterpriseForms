using System.Net;
using EnterpriseForms.Api;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace EnterpriseForms.Api.ContractTests;

/// <summary>
/// C7 M4 incident correction: the browser-test isolation proof exists only on isolated Testing stacks. A Development host (the
/// persistent development stack) has no identity route, so the browser guard can never accept it; and a stack identity
/// configured outside Testing refuses startup, so an ordinary deployment cannot impersonate a disposable test stack.
/// </summary>
public sealed class TestStackIdentityBoundaryTests
{
    [Fact]
    public async Task ADevelopmentApiHasNoStackIdentityRoute()
    {
        using var api = new WebApplicationFactory<Program>().WithWebHostBuilder(b => b.UseEnvironment("Development"));
        using var client = api.CreateClient();
        var response = await client.GetAsync("/api/v1/testing/stack-identity", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public void AStackIdentityOutsideTestingRefusesToStartTheApi()
    {
        using var api = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseEnvironment("Development");
            b.UseSetting("Testing:StackIdentity", "enterpriseforms-security-test");
        });
        var failure = Assert.ThrowsAny<Exception>(() => api.CreateClient());
        Assert.Contains("permitted only in the Testing environment", Flatten(failure), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ADevelopmentAuthorityHasNoStackIdentityRouteAndRefusesAnIdentityOutsideTesting()
    {
        using (var authority = new WebApplicationFactory<AuthorizationAuthority.Api.Program>().WithWebHostBuilder(b =>
               {
                   b.UseEnvironment("Development");
                   b.UseSetting("ConnectionStrings:AuthorityDb", "Host=127.0.0.1;Port=9;Database=none;Username=none;Password=none;Timeout=2");
               }))
        {
            using var client = authority.CreateClient();
            Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/internal/v1/testing/stack-identity", TestContext.Current.CancellationToken)).StatusCode);
        }

        using var misconfigured = new WebApplicationFactory<AuthorizationAuthority.Api.Program>().WithWebHostBuilder(b =>
        {
            b.UseEnvironment("Development");
            b.UseSetting("ConnectionStrings:AuthorityDb", "Host=127.0.0.1;Port=9;Database=none;Username=none;Password=none;Timeout=2");
            b.UseSetting("Testing:StackIdentity", "enterpriseforms-security-test");
        });
        Assert.Contains("permitted only in the Testing environment", Flatten(Assert.ThrowsAny<Exception>(() => misconfigured.CreateClient())), StringComparison.Ordinal);
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
