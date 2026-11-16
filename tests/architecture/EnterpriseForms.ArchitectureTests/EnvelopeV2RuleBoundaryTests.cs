using System.Text.RegularExpressions;
using Xunit;

namespace EnterpriseForms.ArchitectureTests;

/// <summary>
/// E3-M2d fitness functions (docs/security/E3_M2D_IMPLEMENTATION_RECORD.md): the dark Envelope v2 rules are applied at the Authority's
/// single content-acceptance boundary, before signing (Prepare) and before the head advances (Commit); the v1 envelope-version
/// allow-list is not widened; and the floor command migrates the floor context to exactly one requested floor. E3-M2e
/// (docs/security/E3_M2E_CONTRACT_CLOSURE.md): the Prepare-only rules now classify (ClassifyAgainstCurrent) and New rows are refused
/// right after unless acceptance is on; DK-4 runs after authorization and before signing, DK-5 with the operation, DK-6/P-4 and the
/// switch re-check at Commit before the head advances, the ledger is verified (L-13) before it is read, every binding insert is caught
/// by the RB-CARV-1 store catch-all, and no code updates or deletes a binding.
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

    [GeneratedRegex(@"EnvelopeV2Rules\.ClassifyAgainstCurrent\s*\(")]
    private static partial Regex AgainstCurrent();

    [GeneratedRegex(@"DataKeyLedgerRules\.CommitDecision\s*\(")]
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

        // E3-M2e: with acceptance off, the New rows are refused immediately after the classification (the E3-M2d position).
        var refusal = prepare.IndexOf("if (newV2Rows.Count > 0 && !v2Acceptance.Enabled)", StringComparison.Ordinal);
        Assert.True(refusal > rule && refusal < prepare.IndexOf("ContentItemIdentityRules.EnforceAtPrepareAsync(", StringComparison.Ordinal));
    }

    [Fact]
    public void Dk4RunsAfterAuthorizationAndBeforeSigningAndDk5InsertsWithTheOperation()
    {
        var prepare = Method(Source(Service), "private async Task<PreparedContentOperation> PrepareCoreAsync(");
        var verify = prepare.IndexOf("await scope.RequireDataKeyLedgerAsync(", StringComparison.Ordinal);
        var lookup = prepare.IndexOf("scope.FindDataKeyBindingsAsync(", StringComparison.Ordinal);
        var dk4 = prepare.IndexOf("DataKeyLedgerRules.PrepareViolation(", StringComparison.Ordinal);
        var dk5 = prepare.IndexOf("scope.AddDataKeyBindings(", StringComparison.Ordinal);
        Assert.True(verify > prepare.IndexOf("AuthorizeMutations(graph, bound", StringComparison.Ordinal)); // OD-M2e-12: after authorization
        Assert.True(verify > prepare.IndexOf("AuthorizeReprotectionAsync(", StringComparison.Ordinal));
        Assert.True(verify < lookup && lookup < dk4); // L-13 before the ledger is read
        Assert.True(dk4 < prepare.IndexOf("macProvider.SignAsync(", StringComparison.Ordinal)); // nothing is signed for a reused identifier
        Assert.True(dk5 > prepare.IndexOf("scope.AddOperation(operation)", StringComparison.Ordinal) && dk5 < prepare.IndexOf("await scope.CommitAsync(", StringComparison.Ordinal)); // one transaction
    }

    [Fact]
    public void TheLedgerBackedCommitDecisionRunsOnceAfterTheStagedRulesAndBeforeTheHeadAdvances()
    {
        Assert.Equal([Service], Matching(CommitRefusal()));
        var text = Source(Service);
        Assert.Single(CommitRefusal().Matches(text));
        var commit = Method(text, "private async Task<ContentRevision> CommitCoreAsync(");
        var refusal = CommitRefusal().Match(commit).Index;
        Assert.True(refusal > commit.IndexOf("EnvelopeRules.Enforce(stagedDocuments", StringComparison.Ordinal)); // the v2 shape and batch rules first
        Assert.True(refusal < commit.IndexOf("scope.AddManifestCommit(", StringComparison.Ordinal));
        Assert.True(refusal < commit.IndexOf("ContentItemIdentityRules.BindAtCommitAsync(", StringComparison.Ordinal));
        Assert.True(refusal > commit.IndexOf("await scope.RequireDataKeyLedgerAsync(", StringComparison.Ordinal)); // L-13 first
        Assert.True(refusal > commit.IndexOf("VerifyStagedAgainstLeavesAsync(request.StagedItems", StringComparison.Ordinal)); // verified bytes only
        var recheck = commit.IndexOf("if (newGroups.Count > 0 && !v2Acceptance.Enabled)", StringComparison.Ordinal);
        Assert.True(recheck > refusal && recheck < commit.IndexOf("scope.AddManifestCommit(", StringComparison.Ordinal)); // the switch re-check, after the integrity decision
        Assert.DoesNotContain("CommitViolation", Source("AuthorizationAuthority.Application/Features/Content/EnvelopeV2Rules.cs"), StringComparison.Ordinal);

        // DK-8 (AM-3), source-shape only (a non-empty body needs acceptance on, E3-M2e-2): the body archives the bindings of this
        // operation, which DK-6 has just proven equal to the New groups.
        Assert.Contains("newBindings = bindingsOfOperation;", commit, StringComparison.Ordinal);
        var archive = commit.IndexOf("DataKeyLedgerRules.ArchiveTuples(newBindings)", StringComparison.Ordinal);
        Assert.True(archive > recheck && archive < commit.LastIndexOf("await scope.CommitAsync(cancellationToken)", StringComparison.Ordinal));
    }

    [Fact]
    public void LedgerBindingsAreContentAcceptanceWritesAndNoCodeUpdatesOrDeletesThem()
    {
        var store = Source("AuthorizationAuthority.Infrastructure/Persistence/AuthorityStore.cs");
        Assert.Contains("ContentIntegrityCheckpoint or ContentDataKeyBinding:", store, StringComparison.Ordinal); // R-CARV-26 catch-all
        Assert.Contains("DataKeyLedgerRefusals.Translate(ex, alerts)", store, StringComparison.Ordinal);
        foreach (var (path, text) in AuthoritySources())
        {
            Assert.False(Regex.IsMatch(text, @"ContentDataKeyBindings\.(Remove|RemoveRange|Update|UpdateRange|Attach|ExecuteDelete|ExecuteUpdate)"), path);
            Assert.False(Regex.IsMatch(text, @"(UPDATE|DELETE FROM|TRUNCATE)\s+authority\.content_data_key_bindings", RegexOptions.IgnoreCase) && !path.EndsWith("ContentDataKeyLedgerSql.cs", StringComparison.Ordinal), path);
        }

        Assert.Contains("\"content_data_key_bindings\"", Source("AuthorizationAuthority.Infrastructure/Persistence/AuthorityRuntimeGrants.cs"), StringComparison.Ordinal);
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
