using EnterpriseForms.SecurityTests.Harness;
using Xunit;

namespace EnterpriseForms.SecurityTests.Tests;

/// <summary>Guards the harness itself: the minimum discovered-test count is enforced by the runner (--minimum-expected-tests), and the manifest writer never counts a missing test as PASS.</summary>
[Collection(StageACollection.Name)]
public sealed class ScenarioSmokeTests(StageAFixture fixture)
{
    [Fact]
    public void FixtureRecordsCommitAndImageFingerprint()
    {
        Assert.Matches("^[0-9a-f]{40}$", fixture.CommitSha);
        Assert.StartsWith("sha256:", fixture.ImageFingerprint, StringComparison.Ordinal);
    }
}
