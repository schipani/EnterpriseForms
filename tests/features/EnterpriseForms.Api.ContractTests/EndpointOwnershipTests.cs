using EnterpriseForms.Api.Composition;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EnterpriseForms.Api.ContractTests;

/// <summary>Every routed business endpoint names exactly one owning feature and lives under /api/v1 or the probe paths (ADR-PB-006).</summary>
public sealed class EndpointOwnershipTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> factory;

    public EndpointOwnershipTests(WebApplicationFactory<Program> factory)
    {
        this.factory = factory.WithWebHostBuilder(b => b.UseEnvironment("Development"));
    }

    [Fact]
    public void EveryRoutedEndpointHasAFeatureOwner()
    {
        var endpoints = RoutedEndpoints();
        Assert.NotEmpty(endpoints);
        var unowned = endpoints.Where(e => e.Metadata.GetMetadata<FeatureOwnerMetadata>() is null).Select(e => e.RoutePattern.RawText).ToList();
        Assert.True(unowned.Count == 0, "Endpoints without FeatureOwnerMetadata: " + string.Join(", ", unowned));
    }

    [Fact]
    public void BusinessRoutesUseTheV1Prefix()
    {
        foreach (var endpoint in RoutedEndpoints())
        {
            var pattern = endpoint.RoutePattern.RawText ?? string.Empty;
            if (pattern.StartsWith("/api", StringComparison.Ordinal))
            {
                Assert.StartsWith(ApiVersions.V1Prefix + "/", pattern, StringComparison.Ordinal);
            }
            else
            {
                Assert.StartsWith("/health/", pattern, StringComparison.Ordinal);
            }
        }
    }

    [Fact]
    public void NoAuthorityInternalRoutesAreExposedByTheBusinessApi()
    {
        Assert.DoesNotContain(RoutedEndpoints(), e => (e.RoutePattern.RawText ?? string.Empty).StartsWith("/internal", StringComparison.Ordinal));
    }

    private List<RouteEndpoint> RoutedEndpoints() =>
        factory.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .Where(e => !(e.RoutePattern.RawText ?? string.Empty).StartsWith("/openapi", StringComparison.Ordinal))
            .ToList();
}
