using EnterpriseForms.Api;
using EnterpriseForms.Application.Common.Authority;
using EnterpriseForms.Infrastructure.Authority;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EnterpriseForms.Api.ContractTests;

/// <summary>
/// Phase C7 M4 (OD-1) on the business side: the field-protection verifier port exists only in the Worker host. The business
/// API never resolves it, even when a verifier credential is (wrongly) present in its configuration; the Worker's
/// registration refuses a partial credential and one that reuses the business workload key or issuer.
/// </summary>
public sealed class ProtectionVerifierBoundaryTests
{
    private const string VerifierKey = "dmVyaWZpZXItdGVzdC1vbmx5LXNoYXJlZC1rZXktMDAwMDAwMDAw";
    private const string WorkloadKey = "d29ya2xvYWQtZGV2LW9ubHktc2hhcmVkLWtleS0wMDAwMDAwMDAwMA==";

    [Fact]
    public void TheBusinessApiNeverRegistersTheVerifierPortEvenWithAVerifierCredentialInItsConfiguration()
    {
        using var api = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseEnvironment("Development");
            b.UseSetting("ProtectionVerifier:Issuer", "enterpriseforms-field-protection-verifier");
            b.UseSetting("ProtectionVerifier:Audience", "authorization-authority-verifier");
            b.UseSetting("ProtectionVerifier:SharedKeyBase64", VerifierKey);
        });
        Assert.Null(api.Services.GetService<IProtectionVerifierAuthority>());
        Assert.Null(api.Services.GetService<Application.Features.FormDesigner.ProtectionMigration.FieldProtectionReconciliationService>());
    }

    [Fact]
    public void TheWorkerRegistrationAcceptsACompleteDistinctCredentialAndReportsAnAbsentOneAsUnconfigured()
    {
        Assert.True(Resolve(("ProtectionVerifier:Issuer", "enterpriseforms-field-protection-verifier"), ("ProtectionVerifier:Audience", "authorization-authority-verifier"), ("ProtectionVerifier:SharedKeyBase64", VerifierKey)).IsConfigured);
        Assert.False(Resolve().IsConfigured); // nothing configured: every call fails closed with VERIFIER_NOT_CONFIGURED
    }

    [Theory]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, true)]
    public void TheWorkerRegistrationRefusesAPartialOrReusedCredential(bool partial, bool reuseKey, bool reuseIssuer)
    {
        var settings = new List<(string, string)> { ("ProtectionVerifier:Issuer", reuseIssuer ? "enterpriseforms-business-api" : "enterpriseforms-field-protection-verifier") };
        if (!partial)
        {
            settings.Add(("ProtectionVerifier:Audience", "authorization-authority-verifier"));
            settings.Add(("ProtectionVerifier:SharedKeyBase64", reuseKey ? WorkloadKey : VerifierKey));
        }

        var failure = Assert.Throws<InvalidOperationException>(() => Resolve([.. settings]));
        Assert.Contains(partial ? "configured completely" : "own key and issuer", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnUnconfiguredVerifierFailsClosedWithoutContactingTheAuthority()
    {
        var verifier = Resolve();
        var refused = await Assert.ThrowsAsync<AuthorityClientException>(() => verifier.IssueMigrationCatalogueAsync(Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), TestContext.Current.CancellationToken));
        Assert.Equal("VERIFIER_NOT_CONFIGURED", refused.ErrorCode);
        Assert.False(refused.Retryable);
    }

    private static IProtectionVerifierAuthority Resolve(params (string Key, string Value)[] settings)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["TrustAuthority:BaseAddress"] = "http://authority.invalid:8080",
                ["TrustAuthority:WorkloadIssuer"] = "enterpriseforms-business-api",
                ["TrustAuthority:WorkloadSharedKeyBase64"] = WorkloadKey,
            }.Concat(settings.Select(s => new KeyValuePair<string, string?>(s.Key, s.Value)))).Build();
        var services = new ServiceCollection();
        services.AddSingleton(TimeProvider.System);
        services.Configure<AuthorityClientOptions>(configuration.GetSection(AuthorityClientOptions.SectionName));
        services.AddProtectionVerifier(configuration);
        return services.BuildServiceProvider().GetRequiredService<IProtectionVerifierAuthority>();
    }
}
