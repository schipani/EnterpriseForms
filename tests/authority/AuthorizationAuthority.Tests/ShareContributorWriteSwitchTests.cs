using AuthorizationAuthority.Application.Features.Content;
using Xunit;

namespace AuthorizationAuthority.Tests;

/// <summary>Phase C5 rollout switch (D10, C-34 condition 8): off by default, fail-closed parsing, refused in Production, Testing-only override.</summary>
public sealed class ShareContributorWriteSwitchTests
{
    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData("false", false)]
    [InlineData("1", false)]
    [InlineData("yes", false)]
    [InlineData("on", false)]
    [InlineData("truee", false)]
    [InlineData("true", true)]
    [InlineData("TRUE", true)]
    [InlineData(" true ", true)]
    public void OnlyTheLiteralTrueEnables(string? raw, bool expected) => Assert.Equal(expected, ShareContributorWriteSwitch.Parse(raw));

    [Fact]
    public void ProductionRefusesToStartWithContributorWritesEnabled()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => ShareContributorWriteSwitch.Create("true", isProductionEnvironment: true, isTestingEnvironment: false));
        Assert.Contains("Production", ex.Message, StringComparison.Ordinal);
        Assert.False(ShareContributorWriteSwitch.Create(null, isProductionEnvironment: true, isTestingEnvironment: false).Enabled);
        Assert.False(ShareContributorWriteSwitch.Create("false", isProductionEnvironment: true, isTestingEnvironment: false).Enabled);
    }

    [Fact]
    public void TheTestingOverrideExistsOnlyInTesting()
    {
        var development = ShareContributorWriteSwitch.Create("true", isProductionEnvironment: false, isTestingEnvironment: false);
        Assert.True(development.Enabled);
        Assert.Throws<InvalidOperationException>(() => development.SetTestingOverride(false));

        var testing = ShareContributorWriteSwitch.Create(null, isProductionEnvironment: false, isTestingEnvironment: true);
        Assert.False(testing.Enabled);
        testing.SetTestingOverride(true);
        Assert.True(testing.Enabled);
        Assert.False(testing.ConfiguredEnabled);
        testing.SetTestingOverride(null);
        Assert.False(testing.Enabled);
    }
}
