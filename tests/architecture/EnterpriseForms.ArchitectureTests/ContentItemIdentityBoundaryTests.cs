using System.Text.RegularExpressions;
using AuthorizationAuthority.Infrastructure.Persistence;
using Xunit;

namespace EnterpriseForms.ArchitectureTests;

/// <summary>
/// R-53 (C-148) fitness functions: the item-identity invariant is enforced at the Authority's single content-acceptance boundary
/// for every write path, and its bindings are insert-only.
/// </summary>
public sealed partial class ContentItemIdentityBoundaryTests
{
    private static readonly string Authority = RepositoryPaths.Combine("src", "authority");

    private static IEnumerable<(string Path, string Text)> AuthoritySources() =>
        Directory.EnumerateFiles(Authority, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                        && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                        && !f.Contains($"{Path.DirectorySeparatorChar}Migrations{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Select(f => (Path.GetRelativePath(Authority, f).Replace('\\', '/'), File.ReadAllText(f)));

    private static List<string> Matching(Regex pattern) => AuthoritySources().Where(s => pattern.IsMatch(s.Text)).Select(s => s.Path).Order(StringComparer.Ordinal).ToList();

    [GeneratedRegex(@"ContentItemIdentityRules\.EnforceAtPrepareAsync\s*\(")]
    private static partial Regex PrepareEnforcement();

    [GeneratedRegex(@"ContentItemIdentityRules\.BindAtCommitAsync\s*\(")]
    private static partial Regex CommitBinding();

    [GeneratedRegex(@"\.AddItemIdentities\s*\(")]
    private static partial Regex IdentityWrite();

    [Fact]
    public void EveryPrepareAndEveryCommitGoesThroughTheIdentityRules()
    {
        const string service = "AuthorizationAuthority.Application/Features/Content/ContentService.cs";
        Assert.Equal([service], Matching(PrepareEnforcement()));
        Assert.Equal([service], Matching(CommitBinding()));
        Assert.Equal([service], Matching(IdentityWrite()));
        var text = File.ReadAllText(RepositoryPaths.Combine("src", "authority", "AuthorizationAuthority.Application", "Features", "Content", "ContentService.cs"));
        // Exactly one prepare core and one commit core, each calling its rule once (no path around them: owner, share editor and
        // protection migration all pass through PrepareCoreAsync / CommitCoreAsync).
        Assert.Single(PrepareEnforcement().Matches(text));
        Assert.Single(CommitBinding().Matches(text));
        var prepare = text[text.IndexOf("private async Task<PreparedContentOperation> PrepareCoreAsync(", StringComparison.Ordinal)..];
        prepare = prepare[..prepare.IndexOf("\n    }\n", StringComparison.Ordinal)];
        Assert.Matches(PrepareEnforcement(), prepare);
        Assert.Contains("BindAcceptedSnapshotAsync(", prepare, StringComparison.Ordinal);
    }

    [Fact]
    public void IdentityBindingsAreInsertOnlyForTheRuntime()
    {
        Assert.Contains("content_item_identities", AuthorityRuntimeGrants.InsertOnlyTables);
        Assert.Contains("content_item_identities", AuthorityRuntimeGrants.GrantedTables);
    }
}
