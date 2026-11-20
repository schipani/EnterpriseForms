using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace EnterpriseForms.Api.ContractTests;

/// <summary>
/// SEC-BL-01 WP-5a.1 (amendment A-2 correction): D+ needs a durable finding store, so a Development/Testing Authority that delivers to an
/// archive refuses to start without the journal-verification store, a Blob archive refuses a non-durable store, and an unreadable key
/// file is a startup failure (Development stands in for both Development and Testing: the rule is identical). A host without an archive
/// (nothing to deliver) and production hosting are unchanged.
/// </summary>
public sealed class DPlusFindingStoreStartupTests
{
    private static WebApplicationFactory<AuthorizationAuthority.Api.Program> Host(string environment, Dictionary<string, string?> settings) =>
        new WebApplicationFactory<AuthorizationAuthority.Api.Program>().WithWebHostBuilder(b =>
        {
            b.UseEnvironment(environment);
            b.ConfigureAppConfiguration((_, cfg) => cfg.AddInMemoryCollection(settings));
        });

    private static string Flatten(Exception ex) => ex.InnerException is null ? ex.Message : ex.Message + " | " + Flatten(ex.InnerException);

    [Fact]
    public void ADevelopmentHostWithAnArchiveButNoFindingStoreRefusesToStart()
    {
        using var authority = Host("Development", new() { ["JournalVerificationCheckpointStore:Provider"] = "Unconfigured" });
        Assert.Contains("requires the journal-verification store for durable findings", Flatten(Assert.ThrowsAny<Exception>(() => authority.CreateClient())), StringComparison.Ordinal);
    }

    [Fact]
    public void ABlobArchiveRefusesANonDurableFindingStore()
    {
        using var authority = Host("Development", new()
        {
            ["SecurityArchive:Provider"] = "Blob", ["SecurityArchive:BlobServiceUri"] = "http://archive.invalid:10000/acct", ["SecurityArchive:AccountName"] = "acct",
            ["SecurityArchive:AccountKey"] = Convert.ToBase64String(new byte[64]), ["JournalVerificationCheckpointStore:Provider"] = "InMemory",
        });
        Assert.Contains("requires a Blob journal-verification store", Flatten(Assert.ThrowsAny<Exception>(() => authority.CreateClient())), StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnreadableStoreKeyFileIsAStartupFailure()
    {
        using var authority = Host("Development", new()
        {
            ["SecurityArchive:Provider"] = "Blob", ["SecurityArchive:BlobServiceUri"] = "http://archive.invalid:10000/acct", ["SecurityArchive:AccountName"] = "acct",
            ["SecurityArchive:AccountKey"] = Convert.ToBase64String(new byte[64]), ["JournalVerificationCheckpointStore:Provider"] = "Blob",
            ["JournalVerificationCheckpointStore:BlobServiceUri"] = "http://store.invalid:10000/store", ["JournalVerificationCheckpointStore:AccountName"] = "store",
            ["JournalVerificationCheckpointStore:AccountKeyFile"] = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "missing.key"),
        });
        Assert.Contains("AccountKeyFile cannot be read", Flatten(Assert.ThrowsAny<Exception>(() => authority.CreateClient())), StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheDefaultDevelopmentHostPairsTheInMemoryArchiveWithTheInMemoryStoreAndStarts()
    {
        using var authority = Host("Development", []);
        using var client = authority.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(new Uri("/internal/v1/health/live", UriKind.Relative), TestContext.Current.CancellationToken)).StatusCode);
    }
}
