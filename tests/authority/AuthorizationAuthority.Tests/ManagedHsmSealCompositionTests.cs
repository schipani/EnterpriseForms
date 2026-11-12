using AuthorizationAuthority.Api.Composition;
using AuthorizationAuthority.Application.Common.Ports;
using AuthorizationAuthority.Infrastructure.Signing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace AuthorizationAuthority.Tests;

/// <summary>
/// IX-7: the AzureManagedHsm seal provider is chosen from deployment configuration only and refuses to start without a valid endpoint
/// and version mapping. Composition only: no Managed HSM is contacted (the provider is unit-tested against a fake endpoint). Dark mode
/// remains refused outside Development/Testing, so this does not activate the provider anywhere.
/// </summary>
public sealed class ManagedHsmSealCompositionTests
{
    private sealed class Environment(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;

        public string ApplicationName { get; set; } = "AuthorizationAuthority.Tests";

        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;

        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private static IConfiguration Configuration(Dictionary<string, string?> extra)
    {
        var values = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["SearchIndex:Seal:Provider"] = "AzureManagedHsm",
            ["SearchIndex:Seal:SigningKeyName"] = "search-entry-seal-v1",
            ["SearchIndex:Seal:SigningKeyVersion"] = "1",
        };
        foreach (var (key, value) in extra)
        {
            values[key] = value;
        }

        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }

    [Fact]
    public void TheConfiguredProviderIsTheManagedHsmSignerWithAManagedIdentityCredential()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSearchIndex(Configuration(new()
        {
            ["SearchIndex:Seal:ManagedHsm:VaultBaseUrl"] = "https://example-hsm.managedhsm.azure.net/",
            ["SearchIndex:Seal:ManagedHsm:KeyVersionIdentifiers:1"] = "0123456789abcdef0123456789abcdef",
        }), new Environment("Testing"));
        using var provider = services.BuildServiceProvider();
        var signer = provider.GetRequiredService<ISearchSealSigner>();
        Assert.IsType<ManagedHsmSearchSealSigner>(signer);
        Assert.Equal("AzureManagedHsm", signer.ProviderName);
        Assert.IsType<Azure.Identity.ManagedIdentityCredential>(provider.GetRequiredService<Azure.Core.TokenCredential>());
    }

    [Theory]
    [InlineData(null, "0123456789abcdef0123456789abcdef")]
    [InlineData("http://example-hsm.managedhsm.azure.net/", "0123456789abcdef0123456789abcdef")]
    [InlineData("https://example-hsm.managedhsm.azure.net/", null)]
    [InlineData("https://example-hsm.managedhsm.azure.net/", "../keys/other")]
    public void AMissingOrUnsafeConfigurationRefusesToStart(string? baseUrl, string? versionId)
    {
        var services = new ServiceCollection();
        Assert.Throws<InvalidOperationException>(() => services.AddSearchIndex(Configuration(new()
        {
            ["SearchIndex:Seal:ManagedHsm:VaultBaseUrl"] = baseUrl,
            ["SearchIndex:Seal:ManagedHsm:KeyVersionIdentifiers:1"] = versionId,
        }), new Environment("Testing")));
    }
}
