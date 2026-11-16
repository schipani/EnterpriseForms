using System.Text.RegularExpressions;
using Xunit;

namespace EnterpriseForms.ArchitectureTests;

/// <summary>
/// E3-M2d fitness functions (docs/security/E3_M2D_IMPLEMENTATION_RECORD.md): the dark Envelope v2 rules are applied at the Authority's
/// single content-acceptance boundary, before signing (Prepare) and before the head advances (Commit); the v1 envelope-version
/// allow-list is not widened; and the floor command migrates the floor context to exactly one requested floor.
/// </summary>
public sealed partial class EnvelopeV2RuleBoundaryTests
{
    private static readonly string Authority = RepositoryPaths.Combine("src", "authority");
    private const string Service = "AuthorizationAuthority.Application/Features/Content/ContentService.cs";
    private const string Rules = "AuthorizationAuthority.Application/Features/Content/EnvelopeRules.cs";

    private static IEnumerable<(string Path, string Text)> AuthoritySources() =>
        Directory.EnumerateFiles(Authority, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                        && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Select(f => (Path.GetRelativePath(Authority, f).Replace('\\', '/'), File.ReadAllText(f).Replace("\r\n", "\n", StringComparison.Ordinal)));

    private static List<string> Matching(Regex pattern) => AuthoritySources().Where(s => pattern.IsMatch(s.Text)).Select(s => s.Path).Order(StringComparer.Ordinal).ToList();

    private static string Source(string relative) => AuthoritySources().Single(s => s.Path == relative).Text;

    private static string Method(string text, string signature)
    {
        var start = text.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, signature);
        var body = text[start..];
        return body[..body.IndexOf("\n    }\n", StringComparison.Ordinal)];
    }

    [GeneratedRegex(@"EnvelopeV2Rules\.EnforceAgainstCurrent\s*\(")]
    private static partial Regex AgainstCurrent();

    [GeneratedRegex(@"EnvelopeV2Rules\.CommitViolation\s*\(")]
    private static partial Regex CommitRefusal();

    [GeneratedRegex(@"EnvelopeV2Rules\.Enforce\s*\(")]
    private static partial Regex CandidateRules();

    [Fact]
    public void ThePrepareOnlyV2RulesRunOnceInPrepareAfterTheSnapshotProofAndBeforeSigning()
    {
        Assert.Equal([Service], Matching(AgainstCurrent()));
        var text = Source(Service);
        Assert.Single(AgainstCurrent().Matches(text));
        var prepare = Method(text, "private async Task<PreparedContentOperation> PrepareCoreAsync(");
        var rule = AgainstCurrent().Match(prepare).Index;
        Assert.True(rule > prepare.IndexOf("BindAcceptedSnapshotAsync(", StringComparison.Ordinal)); // the verified current revision
        Assert.True(rule < prepare.IndexOf("macProvider.SignAsync(", StringComparison.Ordinal)); // a new v2 row never reaches signing
        Assert.True(rule < prepare.IndexOf("scope.AddOperation(", StringComparison.Ordinal));
    }

    [Fact]
    public void CommitRefusesEveryV2RowOnceAfterTheStagedRulesAndBeforeTheHeadAdvances()
    {
        Assert.Equal([Service], Matching(CommitRefusal()));
        var text = Source(Service);
        Assert.Single(CommitRefusal().Matches(text));
        var commit = Method(text, "private async Task<ContentRevision> CommitCoreAsync(");
        var refusal = CommitRefusal().Match(commit).Index;
        Assert.True(refusal > commit.IndexOf("EnvelopeRules.Enforce(stagedDocuments", StringComparison.Ordinal)); // the v2 shape and batch rules first
        Assert.True(refusal < commit.IndexOf("scope.AddManifestCommit(", StringComparison.Ordinal));
        Assert.True(refusal < commit.IndexOf("ContentItemIdentityRules.BindAtCommitAsync(", StringComparison.Ordinal));
    }

    [Fact]
    public void TheCandidateV2RulesAreReachedOnlyThroughTheDispatchingV1EntryPoint()
    {
        // EnvelopeRules.Enforce is what ParseCandidate (Prepare) and CommitCoreAsync (Commit) call; it dispatches to the v2 rules.
        Assert.Equal([Rules], Matching(CandidateRules()));
        var enforce = Method(Source(Rules), "public static void Enforce(IEnumerable<RowCanonicalDocument> documents)");
        Assert.Matches(CandidateRules(), enforce);
        Assert.Contains("EnvelopeV2Rules.IsV2(document)", enforce, StringComparison.Ordinal);
    }

    [Fact]
    public void TheV1EnvelopeVersionAllowListIsNotWidened()
    {
        var rules = Source(Rules);
        Assert.Contains("AllowedEnvelopeVersions = new HashSet<int> { EnvelopeVersionV1 };", rules, StringComparison.Ordinal);
        Assert.DoesNotContain(AuthoritySources(), s => s.Text.Contains("AllowedEnvelopeVersions = new HashSet<int> { EnvelopeVersionV1, ", StringComparison.Ordinal));
        Assert.Contains("AllowedEnvelopeVersions = EnvelopeRules.AllowedEnvelopeVersions;", Source("AuthorizationAuthority.Application/Features/Content/ContentDeltaRules.cs"), StringComparison.Ordinal);
    }

    [Fact]
    public void TheFloorCommandMigratesTheFloorContextToExactlyOneRequestedFloor()
    {
        var command = Source("AuthorizationAuthority.Api/Composition/ContentAcceptanceFloorCommand.cs");
        Assert.Contains(".Migrate(target.MigrationId)", command, StringComparison.Ordinal);
        Assert.DoesNotContain("context.Database.Migrate()", command, StringComparison.Ordinal); // would apply every pending floor
        foreach (var designer in Directory.EnumerateFiles(RepositoryPaths.Combine("src", "authority", "AuthorizationAuthority.Infrastructure", "Persistence", "FloorMigrations"), "*.Designer.cs"))
        {
            Assert.Contains("[DbContext(typeof(ContentAcceptanceFloorContext))]", File.ReadAllText(designer), StringComparison.Ordinal);
        }
    }
}
