using System.Text.RegularExpressions;
using Xunit;

namespace EnterpriseForms.ArchitectureTests;

/// <summary>
/// E3-M2f-3b (docs/security/E3_M2F3_CONTRACT_CLOSURE.md §3, §5, §6, §10.5): the source shape of the capability-refusal evidence path.
/// One recorder is the sole producer of the evidence kind; it is reached only from the endpoint filter on the seven capability routes
/// (and its retry service); its T2 follows the specified order; it never reaches the ledger, the Release B fence or quarantine; the
/// authoritative time is <c>clock_timestamp()</c> after the lock; the refusal context is attached only by the capability floor; no
/// endpoint or permission is added. Behaviour is covered by the Authority and contract tests; these checks keep the shape.
/// </summary>
public sealed class CapabilityRefusalEvidenceRecorderBoundaryTests
{
    private const string Recorder = "AuthorizationAuthority.Application/Features/Content/CapabilityRefusalEvidenceRecorder.cs";
    private const string Lookup = "AuthorizationAuthority.Application/Features/Content/CapabilityRefusalEvidenceLookup.cs";
    private const string Vocabulary = "AuthorizationAuthority.Application/Features/Content/CapabilityRefusalEvidence.cs";
    private const string Composition = "AuthorizationAuthority.Api/Composition/CapabilityRefusalEvidenceComposition.cs";
    private const string Index = "AuthorizationAuthority.Infrastructure/Persistence/CapabilityRefusalEvidenceIndex.cs";
    private const string Store = "AuthorizationAuthority.Infrastructure/Persistence/AuthorityStore.cs";
    private const string Floor = "AuthorizationAuthority.Application/Features/Content/EnvelopeCapabilityFloor.cs";

    /// <summary>C8 M4-3 (C-165, PD-09 option B): the one approved reader of the kind, the security-command timeline allowlist (restricted fields).</summary>
    private const string TimelineCatalog = "AuthorizationAuthority.Application/Features/Audit/SecurityCommandTimelineCatalog.cs";
    private static readonly string Authority = RepositoryPaths.Combine("src", "authority");

    private static string Source(string relative) =>
        File.ReadAllText(Path.Combine(Authority, relative.Replace('/', Path.DirectorySeparatorChar))).Replace("\r\n", "\n", StringComparison.Ordinal);

    private static List<string> FilesContaining(string text) =>
        Directory.EnumerateFiles(Authority, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                        && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(f => File.ReadAllText(f).Contains(text, StringComparison.Ordinal))
            .Select(f => Path.GetRelativePath(Authority, f).Replace('\\', '/'))
            .Order(StringComparer.Ordinal)
            .ToList();

    // ---- M2F3-38: the sole producer ----

    [Fact]
    public void TheEventKindLiteralLivesOnlyInTheVocabulary() => Assert.Equal([Vocabulary], FilesContaining("\"EnvelopeCapabilityRefused\""));

    [Fact]
    public void OnlyTheRecorderAppendsTheEvidenceKind()
    {
        Assert.Equal(new[] { Index, Lookup, Recorder, TimelineCatalog }.Order(StringComparer.Ordinal), FilesContaining("CapabilityRefusalEvidence.EventKind"));
        var catalog = Source(TimelineCatalog); // a reader only: no append, journal-entry or store path of any kind
        foreach (var writePath in new[] { "Append(", "AddJournalEntry", "JournalChain", "IAuthorityScope", "SaveChanges" })
        {
            Assert.DoesNotContain(writePath, catalog, StringComparison.Ordinal);
        }
        var recorder = Source(Recorder);
        Assert.Single(Regex.Matches(recorder, @"AuditJournal\.Append\("));
        Assert.Contains("AuditJournal.Append(scope, locked, CapabilityRefusalEvidence.EventKind, body, clock)", recorder, StringComparison.Ordinal);
        Assert.DoesNotContain("AddJournalEntry", recorder, StringComparison.Ordinal);
        Assert.DoesNotContain("JournalChain", recorder, StringComparison.Ordinal);
        Assert.DoesNotContain("ISecurityEventArchive", recorder, StringComparison.Ordinal);
    }

    // ---- M2F3-39: reached only from the filter and the retry service ----

    [Fact]
    public void TheRecorderIsReachedOnlyThroughTheFilterAndItsRetryService()
    {
        Assert.Equal([Composition, Recorder], FilesContaining(".RecordAsync(refusal, EntryPoint").Concat(FilesContaining("RecordAsync(CapabilityRefusalContext")).Distinct().Order(StringComparer.Ordinal).ToList());
        Assert.Equal([Composition, Recorder], FilesContaining("RetryDueAsync("));
        var filter = Source(Composition);
        Assert.Contains("catch (Exception refused) when (CapabilityRefusalContext.From(refused) is not null)", filter, StringComparison.Ordinal);
        Assert.Contains("            throw;\n", filter, StringComparison.Ordinal);
        Assert.Contains("ApplicationStopping", filter, StringComparison.Ordinal);
        Assert.DoesNotContain("RequestAborted", filter, StringComparison.Ordinal); // T2-5
        Assert.DoesNotContain("throw refused", filter, StringComparison.Ordinal);    // the same instance, not a rethrow that resets the stack
    }

    [Fact]
    public void TheEvidencePortIsCalledOnlyByTheRecorder()
    {
        foreach (var method in new[] { "SetCapabilityEvidenceLockTimeoutAsync(", "StartCapabilityEvidenceLookupAsync(", "IsCapabilityRefusalEvidenceIndexUsableAsync(", "GetNewestCapabilityRefusalEvidenceAsync(" })
        {
            Assert.Equal(["AuthorizationAuthority.Application/Common/Ports/IAuthorityStore.cs", Recorder, Store], FilesContaining(method));
        }
    }

    // ---- M2F3-40 and §3.3: never the ledger, the fence, quarantine or another state change ----

    [Theory]
    [InlineData("OrganizationHasDataKeyBindingsAsync")]
    [InlineData("RequireDataKeyLedgerAsync")]
    [InlineData("DataKeyBinding")]
    [InlineData("RequireContentAcceptanceFloorAsync")]
    [InlineData("OrganizationQuarantine")]
    [InlineData("IntegrityQuarantine")]
    [InlineData("AdvanceContentIntegrityRevision")]
    [InlineData("AuthorizationRevision")]
    [InlineData("AddCommand")]
    [InlineData("PlatformJournal")]
    [InlineData("ApplyPlan")]
    public void TheRecorderNeverReaches(string forbidden) => Assert.DoesNotContain(forbidden, Source(Recorder), StringComparison.Ordinal);

    // ---- §3.2, §5.2: the T2 order and the authoritative clock ----

    [Fact]
    public void TheEvidenceTransactionFollowsTheSpecifiedOrder()
    {
        var recorder = Source(Recorder);
        var t2 = recorder[recorder.IndexOf("private async Task<(CapabilityRefusalEvidenceOutcome Outcome", StringComparison.Ordinal)..];
        string[] steps = ["store.BeginScopeAsync(", "SetCapabilityEvidenceLockTimeoutAsync(", "LockOrganizationAsync(", "StartCapabilityEvidenceLookupAsync(",
            "IsCapabilityRefusalEvidenceIndexUsableAsync(", "GetNewestCapabilityRefusalEvidenceAsync(", "CapabilityRefusalEvidenceLookup.Decide(", "AuditJournal.Append(", "committing = true", "CommitAsync("];
        var positions = steps.Select(s => t2.IndexOf(s, StringComparison.Ordinal)).ToList();
        Assert.All(positions, p => Assert.True(p >= 0));
        Assert.Equal(positions.Order(), positions);
    }

    [Fact]
    public void TheAuthoritativeTimeIsClockTimestampReadAfterTheLock()
    {
        var index = Source(Index);
        Assert.Contains("public const string DatabaseClockSql = \"SELECT pg_catalog.clock_timestamp()\";", index, StringComparison.Ordinal);
        Assert.Contains("public const string EvidenceLockTimeoutSql = \"SET LOCAL lock_timeout = '1s'\";", index, StringComparison.Ordinal);
        Assert.Contains("public const string EvidenceStatementTimeoutSql = \"SET LOCAL statement_timeout = '500ms'\";", index, StringComparison.Ordinal);
        foreach (var source in new[] { Source(Recorder), Source(Lookup), index, Source(Store) })
        {
            // Code only (documentation comments name the unsuitable functions on purpose).
            var code = string.Join("\n", source.Split('\n').Where(l => !l.TrimStart().StartsWith("//", StringComparison.Ordinal)));
            Assert.DoesNotMatch(@"(?i)\b(now\(\)|transaction_timestamp|statement_timestamp|current_timestamp|localtimestamp)", code);
        }

        var store = Source(Store);
        var start = store[store.IndexOf("public async Task<DateTimeOffset> StartCapabilityEvidenceLookupAsync", StringComparison.Ordinal)..];
        Assert.True(start.IndexOf("EvidenceStatementTimeoutSql", StringComparison.Ordinal) < start.IndexOf("DatabaseClockSql", StringComparison.Ordinal));
        Assert.Contains("EvidenceCommand(CapabilityRefusalEvidenceIndex.EvidenceLookupSql)", store, StringComparison.Ordinal);
        Assert.DoesNotContain("GetUtcNow", Source(Lookup), StringComparison.Ordinal); // the lookup decides on t_db only
    }

    // ---- the refusal context and the routes ----

    [Fact]
    public void OnlyTheCapabilityFloorAttachesTheRefusalContext()
    {
        Assert.Equal([Floor], FilesContaining("CapabilityRefusalContext.Attach("));
        Assert.Equal([Vocabulary], FilesContaining("refusal.Data[ExceptionDataKey]"));
    }

    [Fact]
    public void TheSevenCapabilityRoutesCarryTheFilterWithTheirOwnEntryPoints()
    {
        var expected = new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["AuthorizationAuthority.Api/Features/Content/ContentEndpoints.cs"] = ["Prepare", "Commit", "Verify", "VerifyBatch"],
            ["AuthorizationAuthority.Api/Features/Catalogue/CatalogueEndpoints.cs"] = ["ProtectionCensus"],
            ["AuthorizationAuthority.Api/Features/SearchIndex/SearchIndexBuilderEndpoints.cs"] = ["IndexBackfill", "IndexDeepVerify"],
        };
        Assert.Equal(expected.Keys.Order(StringComparer.Ordinal), FilesContaining(".WithCapabilityRefusalEvidence("));
        foreach (var (file, entries) in expected)
        {
            var source = Source(file);
            var applied = Regex.Matches(source, @"\.WithEnvelopeCapabilityContract\(\)\n\s+\.WithCapabilityRefusalEvidence\([\w.]+EntryPoints\.(\w+)\)").Select(m => m.Groups[1].Value).ToList();
            Assert.Equal(entries, applied);
            Assert.Equal(Regex.Count(source, @"\.WithEnvelopeCapabilityContract\(\)"), Regex.Count(source, @"\.WithCapabilityRefusalEvidence\("));
        }
    }

    // ---- M2F3-41: no endpoint, permission or configuration ----

    [Fact]
    public void TheEvidenceCompositionAddsNoEndpointPermissionOrConfiguration()
    {
        var composition = Source(Composition);
        Assert.DoesNotMatch(@"\.Map(Get|Post|Put|Patch|Delete|Group|Methods)?\(", composition);
        Assert.DoesNotContain("RequireAuthorization", composition, StringComparison.Ordinal);
        Assert.DoesNotContain("IConfiguration", composition, StringComparison.Ordinal);
        Assert.DoesNotContain("GetSection", Source(Recorder), StringComparison.Ordinal);
        Assert.DoesNotContain("IConfiguration", Source(Recorder), StringComparison.Ordinal);
    }
}
