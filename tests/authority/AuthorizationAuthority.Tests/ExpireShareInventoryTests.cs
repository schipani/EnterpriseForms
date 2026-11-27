using System.Text.RegularExpressions;
using AuthorizationAuthority.Contracts.V1;
using AuthorizationAuthority.Domain.Features.SecurityCommands;
using Xunit;

namespace AuthorizationAuthority.Tests;

/// <summary>
/// C10 WP-0 Section 4 (C10-A, approved C-194), source and contract inventory: test 16 (no route of the Authority or the business maps to
/// <c>ExpireShare</c>; no published contract lists it) and "Generic-bypass prevention" (only <c>ShareExpiryConvergenceService</c>
/// records the command, and only <c>ShareExpiryLoop</c> drives it). Added after the C10 independent review found them unasserted.
/// </summary>
public sealed partial class ExpireShareInventoryTests
{
    [Fact]
    public void NoPublishedContractOrRouteNamesTheInternalCommand()
    {
        var contracts = Directory.EnumerateFiles(RepositoryRoot("contracts", "openapi"), "*.json").ToList();
        Assert.NotEmpty(contracts);
        foreach (var contract in contracts)
        {
            Assert.DoesNotContain(SecurityCommandKinds.ExpireShare, File.ReadAllText(contract), StringComparison.Ordinal);
        }

        Assert.DoesNotContain(Enum.GetNames<SecurityCommandKind>(), n => n == SecurityCommandKinds.ExpireShare);
        foreach (var file in SourceFiles())
        {
            var text = File.ReadAllText(file);
            foreach (Match route in RouteMapping().Matches(text))
            {
                Assert.DoesNotContain("expire", route.Value, StringComparison.OrdinalIgnoreCase);
            }
        }
    }

    [Fact]
    public void OnlyTheConvergenceServiceRecordsTheCommandAndOnlyTheLoopDrivesIt()
    {
        var recorders = SourceFiles().Where(f => File.ReadAllText(f).Contains("CommandKindName = SecurityCommandKinds.ExpireShare", StringComparison.Ordinal)).Select(Path.GetFileName).ToList();
        Assert.Equal(["ShareExpiryConvergenceService.cs"], recorders);

        var drivers = SourceFiles().Where(f => Path.GetFileName(f) != "ShareExpiryConvergenceService.cs" && File.ReadAllText(f).Contains(".ConvergeAsync(", StringComparison.Ordinal)).Select(Path.GetFileName).ToList();
        Assert.Equal(["ShareExpiryLoop.cs"], drivers);
    }

    private static IEnumerable<string> SourceFiles() =>
        Directory.EnumerateFiles(RepositoryRoot("src"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal) &&
                        !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal));

    [GeneratedRegex(@"\.Map(Get|Post|Put|Patch|Delete|Methods)\(\s*""[^""]*""")]
    private static partial Regex RouteMapping();

    private static string RepositoryRoot(params string[] parts)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "CLAUDE.md")))
        {
            directory = directory.Parent;
        }

        return Path.Combine([directory?.FullName ?? throw new InvalidOperationException("repository root not found"), .. parts]);
    }
}
