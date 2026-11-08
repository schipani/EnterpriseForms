using EnterpriseForms.SecurityTests.Harness;
using EnterpriseForms.StageBTests.Harness;
using Xunit;

namespace EnterpriseForms.StageBTests.Tests;

/// <summary>
/// C8 M4 S1 behavioural contract tests CT-1…CT-6 (Design B revision 2 §2.2/§2.11), pinned to the stack's SpiceDB image
/// (authzed/spicedb:v1.56.2). They turn the characterised SpiceDB behaviour S1 relies on, or deliberately does not rely on,
/// into executable checks, so an upgrade that changes it fails here:
/// CT-1 the marker read returns its <c>read_at</c>; CT-2 a zero-match read streams nothing; CT-3 exact-snapshot decisions echo
/// the snapshot (<c>checked_at</c>, <c>looked_up_at</c>); CT-4 <c>at_least_as_fresh</c> silently accepts foreign and future
/// tokens (why the ZedToken is never integrity evidence); CT-5 the exact-snapshot error classes; CT-6 a marker CAS and its
/// relationship change are atomic under concurrent snapshot reads. IX-2 adds IX2-CT-07 (P-S1-1 §4, O-1): the
/// CheckBulkPermissions per-request item maximum (accepted requests are complete and echo each item in order; larger requests
/// are refused, never truncated; the Authority's chunk size is accepted). CT-7 stays reserved for S1 Design B §2.11 (dump
/// restore, not implemented); the IX-2 test was renamed IX2-CT-07 by owner decision C-129.
/// The probe runs in a container on the authority network.
/// </summary>
[Collection(StageBStackCollectionDefinition.Name)]
public sealed class SpiceDbBehaviourContractTests(StageBFixture fixture)
{
    private static string? probeOutput;

    /// <summary>The probe's build output next to this test assembly's configuration (Debug or Release) and framework.</summary>
    internal static string ProbeDirectory()
    {
        var testBin = new DirectoryInfo(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        var directory = Path.Combine(ComposeStack.RepositoryRoot, "tests", "integration", "EnterpriseForms.SpiceDbContractProbe", "bin", testBin.Parent!.Name, testBin.Name);
        return File.Exists(Path.Combine(directory, "EnterpriseForms.SpiceDbContractProbe.dll"))
            ? directory
            : throw new InvalidOperationException("the SpiceDB contract probe is not built at " + directory);
    }

    [Theory]
    [InlineData("CT-1")]
    [InlineData("CT-2")]
    [InlineData("CT-3")]
    [InlineData("CT-4")]
    [InlineData("CT-5")]
    [InlineData("CT-6")]
    [InlineData("IX2-CT-07")]
    public void TheSpiceDbBehaviourTheProtocolReliesOnHolds(string contract)
    {
        probeOutput ??= fixture.Stack.SpiceDbProbe(ProbeDirectory(), string.Empty, 300, throwOnError: false);
        var line = probeOutput.Split('\n', StringSplitOptions.TrimEntries).FirstOrDefault(l => l.StartsWith(contract + " ", StringComparison.Ordinal));
        Assert.True(line is not null, $"{contract} did not report. Probe output:\n{probeOutput}");
        Assert.True(line!.StartsWith(contract + " PASS", StringComparison.Ordinal), line);
    }
}
