using System.Globalization;
using EnterpriseForms.StageBTests.Harness;
using Xunit;

namespace EnterpriseForms.StageBTests.Tests;

/// <summary>
/// C-149 (R-55): seeds <c>C149_SEED_ORGANIZATIONS</c> (default 130) empty synthetic organizations on the security-test stack, so the
/// per-organization cost of background maintenance can be measured at the organization count of a long-lived stack. A measurement
/// helper (Benchmark), never a functional test; synthetic identities only.
/// </summary>
[Collection(StageBStackCollectionDefinition.Name)]
public sealed class SyntheticOrganizationSeedTests(StageBFixture fixture)
{
    [Fact]
    [Trait("Category", "Benchmark")]
    public async Task SeedEmptySyntheticOrganizations()
    {
        var count = int.TryParse(Environment.GetEnvironmentVariable("C149_SEED_ORGANIZATIONS"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : 130;
        for (var i = 0; i < count; i++)
        {
            await new StageBScenario(fixture, "c149-seed-" + i.ToString(CultureInfo.InvariantCulture)).BootstrapAsync();
        }
    }
}
