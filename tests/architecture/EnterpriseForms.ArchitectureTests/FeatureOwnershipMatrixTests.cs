using System.Text.RegularExpressions;
using Xunit;

namespace EnterpriseForms.ArchitectureTests;

/// <summary>Every PG-001..PG-069 screen has exactly one frontend owner and the ownership matrix agrees with CLAUDE-DESIGN.MD §17 (§4U.5 item 6).</summary>
public sealed partial class FeatureOwnershipMatrixTests
{
    [GeneratedRegex(@"^\| PG-(\d{3}) \| `([a-z-]+)` \|", RegexOptions.Multiline)]
    private static partial Regex DesignOwnerRow();

    [GeneratedRegex(@"^\| `([a-z-]+)` \| ([^|]*) \|", RegexOptions.Multiline)]
    private static partial Regex MatrixRow();

    [GeneratedRegex(@"PG-(\d{3})")]
    private static partial Regex PageId();

    [Fact]
    public void DesignDocumentAssignsEveryScreenExactlyOnce()
    {
        var design = File.ReadAllText(RepositoryPaths.Combine("CLAUDE-DESIGN.MD"));
        var ids = DesignOwnerRow().Matches(design).Select(m => int.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture)).ToList();
        Assert.Equal(Enumerable.Range(1, 69), ids.OrderBy(i => i));
        Assert.Equal(ids.Count, ids.Distinct().Count());
    }

    [Fact]
    public void OwnershipMatrixAgreesWithDesignDocument()
    {
        var design = File.ReadAllText(RepositoryPaths.Combine("CLAUDE-DESIGN.MD"));
        var designOwners = DesignOwnerRow().Matches(design).ToDictionary(m => int.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture), m => m.Groups[2].Value);

        var matrix = File.ReadAllText(RepositoryPaths.Combine("docs", "architecture", "FEATURE_OWNERSHIP_MATRIX.md"));
        var matrixOwners = new Dictionary<int, string>();
        foreach (Match row in MatrixRow().Matches(matrix))
        {
            foreach (Match id in PageId().Matches(row.Groups[2].Value))
            {
                var page = int.Parse(id.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
                Assert.False(matrixOwners.ContainsKey(page), $"PG-{page:000} listed under two owners in FEATURE_OWNERSHIP_MATRIX.md");
                matrixOwners[page] = row.Groups[1].Value;
            }
        }

        Assert.Equal(Enumerable.Range(1, 69), matrixOwners.Keys.OrderBy(k => k));
        foreach (var (page, owner) in designOwners)
        {
            Assert.True(matrixOwners.TryGetValue(page, out var matrixOwner) && matrixOwner == owner, $"PG-{page:000}: design says `{owner}`, matrix says `{matrixOwners.GetValueOrDefault(page)}`");
        }
    }
}
