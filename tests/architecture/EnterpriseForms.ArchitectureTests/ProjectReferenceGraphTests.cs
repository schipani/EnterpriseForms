using System.Xml.Linq;
using Xunit;

namespace EnterpriseForms.ArchitectureTests;

/// <summary>Compile-time dependency direction (ADR-ARCH-001 §4U.1, §4U.5 item 1) checked from the csproj files themselves.</summary>
public sealed class ProjectReferenceGraphTests
{
    private static readonly IReadOnlyDictionary<string, IReadOnlySet<string>> Graph = LoadGraph();

    private static readonly IReadOnlyDictionary<string, string[]> AllowedReferences = new Dictionary<string, string[]>(StringComparer.Ordinal)
    {
        ["EnterpriseForms.Domain"] = [],
        ["EnterpriseForms.Contracts"] = [],
        ["EnterpriseForms.Application"] = ["EnterpriseForms.Domain", "EnterpriseForms.Contracts", "EnterpriseForms.Integrity"],
        ["EnterpriseForms.Infrastructure"] = ["EnterpriseForms.Application", "EnterpriseForms.Domain"],
        ["EnterpriseForms.Api"] = ["EnterpriseForms.Application", "EnterpriseForms.Infrastructure", "EnterpriseForms.Contracts"],
        ["EnterpriseForms.Worker"] = ["EnterpriseForms.Application", "EnterpriseForms.Infrastructure"],
        ["EnterpriseForms.Integrity"] = [],
        ["AuthorizationAuthority.Domain"] = [],
        ["AuthorizationAuthority.Contracts"] = [],
        ["AuthorizationAuthority.Application"] = ["AuthorizationAuthority.Domain", "AuthorizationAuthority.Contracts", "EnterpriseForms.Integrity"],
        ["AuthorizationAuthority.Infrastructure"] = ["AuthorizationAuthority.Application", "AuthorizationAuthority.Domain"],
        ["AuthorizationAuthority.Api"] = ["AuthorizationAuthority.Application", "AuthorizationAuthority.Infrastructure", "AuthorizationAuthority.Contracts"],
    };

    [Fact]
    public void EveryProjectUnderSrcIsRegisteredInTheAllowList()
    {
        var unknown = Graph.Keys.Except(AllowedReferences.Keys, StringComparer.Ordinal).ToList();
        Assert.True(unknown.Count == 0, "New project needs an explicit dependency allow-list entry: " + string.Join(", ", unknown));
    }

    [Theory]
    [MemberData(nameof(ProjectNames))]
    public void ProjectReferencesOnlyAllowedProjects(string project)
    {
        var actual = Graph[project];
        var allowed = AllowedReferences[project].ToHashSet(StringComparer.Ordinal);
        var forbidden = actual.Except(allowed, StringComparer.Ordinal).ToList();
        Assert.True(forbidden.Count == 0, $"{project} references forbidden projects: {string.Join(", ", forbidden)}");
    }

    [Fact]
    public void BusinessAssembliesNeverReferenceTheAuthority()
    {
        foreach (var (project, refs) in Graph.Where(p => p.Key.StartsWith("EnterpriseForms.", StringComparison.Ordinal)))
        {
            Assert.DoesNotContain(refs, r => r.StartsWith("AuthorizationAuthority.", StringComparison.Ordinal));
            _ = project;
        }
    }

    [Fact]
    public void AuthorityAssembliesNeverReferenceBusinessCode()
    {
        foreach (var (_, refs) in Graph.Where(p => p.Key.StartsWith("AuthorizationAuthority.", StringComparison.Ordinal)))
        {
            Assert.DoesNotContain(refs, r => r.StartsWith("EnterpriseForms.", StringComparison.Ordinal) && r != "EnterpriseForms.Integrity");
        }
    }

    [Fact]
    public void DomainAndContractsHaveNoPackageDependencies()
    {
        foreach (var name in new[] { "EnterpriseForms.Domain", "EnterpriseForms.Contracts", "AuthorizationAuthority.Domain", "AuthorizationAuthority.Contracts", "EnterpriseForms.Integrity" })
        {
            var csproj = Directory.EnumerateFiles(RepositoryPaths.Combine("src"), name + ".csproj", SearchOption.AllDirectories).Single();
            var packages = XDocument.Load(csproj).Descendants("PackageReference").ToList();
            Assert.True(packages.Count == 0, $"{name} must not reference NuGet packages: {string.Join(", ", packages.Select(p => p.Attribute("Include")?.Value))}");
        }
    }

    [Fact]
    public void NoProjectPinsPackageVersionsLocally()
    {
        foreach (var csproj in Directory.EnumerateFiles(RepositoryPaths.Combine("src"), "*.csproj", SearchOption.AllDirectories)
                     .Concat(Directory.EnumerateFiles(RepositoryPaths.Combine("tests"), "*.csproj", SearchOption.AllDirectories)))
        {
            var withVersion = XDocument.Load(csproj).Descendants("PackageReference").Where(p => p.Attribute("Version") is not null).ToList();
            Assert.True(withVersion.Count == 0, $"{Path.GetFileName(csproj)} pins versions locally; use Directory.Packages.props (ADR-PB-011).");
        }
    }

    public static TheoryData<string> ProjectNames()
    {
        var data = new TheoryData<string>();
        foreach (var key in Graph.Keys.OrderBy(k => k, StringComparer.Ordinal))
        {
            data.Add(key);
        }

        return data;
    }

    private static Dictionary<string, IReadOnlySet<string>> LoadGraph()
    {
        var graph = new Dictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal);
        foreach (var csproj in Directory.EnumerateFiles(RepositoryPaths.Combine("src"), "*.csproj", SearchOption.AllDirectories))
        {
            var name = Path.GetFileNameWithoutExtension(csproj);
            var refs = XDocument.Load(csproj).Descendants("ProjectReference")
                .Select(r => Path.GetFileNameWithoutExtension(r.Attribute("Include")!.Value.Replace('\\', '/')))
                .ToHashSet(StringComparer.Ordinal);
            graph[name] = refs;
        }

        return graph;
    }
}
