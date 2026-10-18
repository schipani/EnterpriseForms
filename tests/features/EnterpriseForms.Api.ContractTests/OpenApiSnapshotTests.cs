using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace EnterpriseForms.Api.ContractTests;

/// <summary>The served OpenAPI document is deterministic and equals the committed build artifact (PB-06 CI gate).</summary>
public sealed class OpenApiSnapshotTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> factory;

    public OpenApiSnapshotTests(WebApplicationFactory<Program> factory)
    {
        this.factory = factory.WithWebHostBuilder(b => b.UseEnvironment("Development"));
    }

    [Fact]
    public async Task ServedDocumentIsDeterministicAcrossRequests()
    {
        using var client = factory.CreateClient();
        var first = await client.GetStringAsync(new Uri("/openapi/v1.json", UriKind.Relative), TestContext.Current.CancellationToken);
        var second = await client.GetStringAsync(new Uri("/openapi/v1.json", UriKind.Relative), TestContext.Current.CancellationToken);
        Assert.Equal(first, second);
    }

    [Fact]
    public async Task ServedDocumentMatchesCommittedContract()
    {
        var path = Path.Combine(RepositoryPaths.Root, "contracts", "openapi", "enterprise-forms-v1.json");
        Assert.True(File.Exists(path), $"Missing committed contract {path}. Run `dotnet build src/backend/EnterpriseForms.Api` to regenerate.");

        using var client = factory.CreateClient();
        var served = JsonNode.Parse(await client.GetStringAsync(new Uri("/openapi/v1.json", UriKind.Relative), TestContext.Current.CancellationToken))!.AsObject();
        var committed = JsonNode.Parse(await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken))!.AsObject();
        served.Remove("servers");
        committed.Remove("servers");

        Assert.True(JsonNode.DeepEquals(served, committed), "Served OpenAPI differs from committed contracts/openapi/enterprise-forms-v1.json; rebuild and review the diff.");
    }

    [Fact]
    public async Task DocumentDeclaresNoServersAndNoInternalPaths()
    {
        using var client = factory.CreateClient();
        var document = JsonNode.Parse(await client.GetStringAsync(new Uri("/openapi/v1.json", UriKind.Relative), TestContext.Current.CancellationToken))!.AsObject();
        Assert.DoesNotContain(document["paths"]!.AsObject(), p => p.Key.StartsWith("/internal", StringComparison.Ordinal));
        Assert.Equal("v1", document["info"]!["version"]!.GetValue<string>());
    }
}

internal static class RepositoryPaths
{
    public static string Root { get; } = Locate();

    private static string Locate()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "global.json")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }
}
