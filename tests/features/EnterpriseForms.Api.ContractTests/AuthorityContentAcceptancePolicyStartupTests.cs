using AuthorizationAuthority.Application.Features.Content;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EnterpriseForms.Api.ContractTests;

/// <summary>
/// RB-CARV-1 (contract §22, scenario E) on the composed Authority host: the content acceptance phase is mandatory deployment
/// configuration. Outside Development (no settings file supplies it) an absent phase refuses the start; malformed, unsupported or
/// downgraded values refuse it in every environment; nothing ever silently becomes Release A. Development declares Release A
/// explicitly in its settings file, and an explicit Release B configuration starts.
/// </summary>
public sealed class AuthorityContentAcceptancePolicyStartupTests
{
    private static WebApplicationFactory<AuthorizationAuthority.Api.Program> Authority(string environment, Dictionary<string, string?> values) =>
        new WebApplicationFactory<AuthorizationAuthority.Api.Program>().WithWebHostBuilder(b =>
        {
            b.UseEnvironment(environment);
            b.ConfigureAppConfiguration((_, cfg) => cfg.AddInMemoryCollection(values));
        });

    private static Dictionary<string, string?> StagingBaseline(string? phase, string? floor)
    {
        var values = new Dictionary<string, string?>
        {
            ["Signing:Provider"] = "AzureManagedHsm", // permitted in Staging; the signing guard runs before the policy guard
            ["Identity:Workload:Issuer"] = "enterpriseforms-business-api",
            ["Identity:Workload:Audience"] = "authorization-authority",
            ["Identity:Workload:SharedKeyBase64"] = "d29ya2xvYWQtZGV2LW9ubHktc2hhcmVkLWtleS0wMDAwMDAwMDAwMA==",
            ["Identity:Operator:Issuer"] = "authority-operator",
            ["Identity:Operator:Audience"] = "authorization-authority-operator",
            ["Identity:Operator:SharedKeyBase64"] = "b3BlcmF0b3ItZGV2LW9ubHktc2hhcmVkLWtleS0wMDAwMDAwMDAwMA==",
        };
        if (phase is not null)
        {
            values["ContentAcceptance:EnforcementPhase"] = phase;
        }

        if (floor is not null)
        {
            values["ContentAcceptance:RequiredFloor"] = floor;
        }

        return values;
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

    public static TheoryData<string?, string?, string> RefusedConfigurations => new()
    {
        { null, null, ContentAcceptancePolicy.PhaseUnconfigured }, // appsettings.json supplies "Unconfigured"
        { "", null, ContentAcceptancePolicy.PhaseUnconfigured },
        { "Unconfigured", null, ContentAcceptancePolicy.PhaseUnconfigured },
        { null, "1", ContentAcceptancePolicy.PhaseUnconfigured },
        { "releaseb", "1", ContentAcceptancePolicy.PhaseUnsupported },
        { "ReleaseB", null, ContentAcceptancePolicy.FloorMissing },
        { "ReleaseB", "one", ContentAcceptancePolicy.FloorMalformed },
        { "ReleaseB", "0", ContentAcceptancePolicy.FloorBelowMinimum },
        { "ReleaseB", "4", ContentAcceptancePolicy.FloorUnsupported }, // E3-M2e: floors 1-3 are recognized; 4 is above this build
        { "ReleaseA", "1", ContentAcceptancePolicy.FloorNotPermittedInReleaseA },
    };

    [Theory]
    [MemberData(nameof(RefusedConfigurations))]
    public void AStagingHostRefusesToStartWithoutAnExactlyValidPolicy(string? phase, string? floor, string reason)
    {
        using var authority = Authority("Staging", StagingBaseline(phase, floor));
        var message = Flatten(Assert.ThrowsAny<Exception>(() => authority.CreateClient()));
        Assert.Contains($"Content acceptance policy is invalid ({reason})", message, StringComparison.Ordinal);
        Assert.Contains("a missing value is never treated as Release A", message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("ReleaseB", null, ContentAcceptancePolicy.FloorMissing)]
    [InlineData("ReleaseB", "0", ContentAcceptancePolicy.FloorBelowMinimum)]
    [InlineData("Release B", "1", ContentAcceptancePolicy.PhaseUnsupported)]
    public void AnInvalidOverrideOfTheDevelopmentSettingIsRefusedToo(string phase, string? floor, string reason)
    {
        using var authority = Authority("Development", new Dictionary<string, string?> { ["ContentAcceptance:EnforcementPhase"] = phase, ["ContentAcceptance:RequiredFloor"] = floor });
        Assert.Contains($"Content acceptance policy is invalid ({reason})", Flatten(Assert.ThrowsAny<Exception>(() => authority.CreateClient())), StringComparison.Ordinal);
    }

    [Fact]
    public void DevelopmentDeclaresReleaseAExplicitlyAndAnExplicitReleaseBStarts()
    {
        using (var development = Authority("Development", []))
        {
            using var client = development.CreateClient();
            Assert.Equal("phase=ReleaseA", development.Services.GetRequiredService<ContentAcceptancePolicy>().Describe());
        }

        using var releaseB = Authority("Development", new Dictionary<string, string?> { ["ContentAcceptance:EnforcementPhase"] = "ReleaseB", ["ContentAcceptance:RequiredFloor"] = "1" });
        using var releaseBClient = releaseB.CreateClient();
        var policy = releaseB.Services.GetRequiredService<ContentAcceptancePolicy>();
        Assert.Equal(("phase=ReleaseB requiredFloor=1", true), (policy.Describe(), policy.GuardsContentWrites));

        // E3-M2d (D-4): a version-2 build also starts with the recognized floor 2 as its required minimum.
        using var floor2 = Authority("Development", new Dictionary<string, string?> { ["ContentAcceptance:EnforcementPhase"] = "ReleaseB", ["ContentAcceptance:RequiredFloor"] = "2" });
        using var floor2Client = floor2.CreateClient();
        var floor2Policy = floor2.Services.GetRequiredService<ContentAcceptancePolicy>();
        Assert.Equal(("phase=ReleaseB requiredFloor=2", true), (floor2Policy.Describe(), floor2Policy.GuardsContentWrites));
    }

    // ---- E3-M2e: the Envelope v2 acceptance switch on the composed host (OD-M2e-07, AM-5; default off, never enabled here) ----

    [Fact]
    public void TheComposedHostHasTheSwitchOffByDefaultWithDevelopmentWrapCodes()
    {
        using var development = Authority("Development", []);
        using var client = development.CreateClient();
        var acceptance = development.Services.GetRequiredService<EnvelopeV2AcceptanceSwitch>();
        Assert.Equal((false, false, false), (acceptance.ConfiguredEnabled, acceptance.Requested, acceptance.Enabled));
        Assert.Equal([1, 2], acceptance.AllowedWrapCodes.Order());
        Assert.Same(acceptance, development.Services.GetRequiredService<EnvelopeV2AcceptanceSwitch>()); // one instance, resolved once
    }

    [Fact]
    public void AStagingHostRefusesToStartWithAcceptanceConfiguredOn()
    {
        var values = StagingBaseline("ReleaseB", "3");
        values["Content:EnvelopeV2AcceptanceEnabled"] = "true";
        using var authority = Authority("Staging", values);
        Assert.Contains("must not be enabled outside Development/Testing", Flatten(Assert.ThrowsAny<Exception>(() => authority.CreateClient())), StringComparison.Ordinal);
    }

    [Fact]
    public void ADevelopmentHostRefusesToStartWithAcceptanceOnInReleaseA()
    {
        using var authority = Authority("Development", new Dictionary<string, string?> { ["Content:EnvelopeV2AcceptanceEnabled"] = "true" });
        Assert.Contains("requires ContentAcceptance:EnforcementPhase = ReleaseB and ContentAcceptance:RequiredFloor = 3", Flatten(Assert.ThrowsAny<Exception>(() => authority.CreateClient())), StringComparison.Ordinal);
    }

    [Fact]
    public void ADevelopmentHostRefusesToStartWithAcceptanceOnWhenTheLedgerMigrationCannotBeVerified()
    {
        // Release B floor 3 satisfies the static prerequisites; no reachable AuthorityDb means the ledger migration is not verified: refused.
        using var authority = Authority("Development", new Dictionary<string, string?>
        {
            ["Content:EnvelopeV2AcceptanceEnabled"] = "true",
            ["ContentAcceptance:EnforcementPhase"] = "ReleaseB",
            ["ContentAcceptance:RequiredFloor"] = "3",
            ["ConnectionStrings:AuthorityDb"] = "Host=unreachable.invalid;Database=authority;Username=nobody;Password=none;Timeout=2",
        });
        var ex = Assert.ThrowsAny<Exception>(() => authority.CreateClient());
        var types = new List<string>();
        for (Exception? e = ex; e is not null; e = e.InnerException)
        {
            types.Add(e.GetType().FullName ?? string.Empty);
        }

        Assert.Contains(types, t => t.StartsWith("Npgsql.", StringComparison.Ordinal) || t.StartsWith("System.Net.Sockets.", StringComparison.Ordinal)); // the ledger read itself failed
        Assert.DoesNotContain("must not be enabled outside", Flatten(ex), StringComparison.Ordinal);
        Assert.DoesNotContain("requires ContentAcceptance:EnforcementPhase", Flatten(ex), StringComparison.Ordinal);
    }
}
