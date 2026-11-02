using EnterpriseForms.StageBTests.Harness;
using Xunit;

namespace EnterpriseForms.StageBTests.Tests;

/// <summary>
/// Registers the three synthetic Platform Console operators of this disposable stack (idempotent: already-registered operators are left
/// alone). The browser end-to-end job runs only this test before Playwright, because browser specs that drive the isolated Platform
/// Console (for example owner-recovery-objection) sign in as a registered synthetic operator and the two-operator registration
/// ceremony lives in this harness. It asserts only that the trio is registered and Active.
/// </summary>
[Collection(StageBStackCollectionDefinition.Name)]
public sealed class ConsoleOperatorRegistrationTests(StageBFixture fixture)
{
    [Fact]
    public async Task RegisterTheConsoleOperatorsForBrowserSpecs()
    {
        var operators = await PlatformConsoleHarness.EnsureConsoleOperatorsAsync(fixture);
        Assert.Equal(3, operators.Count);
    }
}
