using Xunit;

namespace EnterpriseForms.ArchitectureTests;

/// <summary>
/// C-150 fitness functions, by source scan:
/// <list type="bullet">
/// <item>IX-5c: Tier 1 knows the shadow comparison only as <c>IIndexedSearchShadowSink</c>, offers only after its result snapshot and
/// evidence committed, and swallows anything the sink does; the live search endpoints never reach Tier 2 (no serving);</item>
/// <item>R56-B: the work notification is issued inside the committing transaction (PostgreSQL delivers it only on commit) and the
/// in-process wake-up only after the commit returned; the work loop keeps its poll and its durable scan.</item>
/// </list>
/// </summary>
public sealed class C150SearchAvailabilityBoundaryTests
{
    private static string Source(params string[] parts) => File.ReadAllText(RepositoryPaths.Combine(["src", "authority", .. parts]));

    private static string Method(string source, string signature)
    {
        var start = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, "missing: " + signature);
        var end = source.IndexOf("\n    }\n", start, StringComparison.Ordinal);
        if (end < 0)
        {
            end = source.IndexOf("\r\n    }\r\n", start, StringComparison.Ordinal);
        }

        return source[start..end];
    }

    [Fact]
    public void Tier1OffersTheShadowOnlyAfterItsCommitAndThroughTheSinkInterfaceOnly()
    {
        var tier1 = Source("AuthorizationAuthority.Application", "Features", "SearchIndex", "IndexedSearchService.cs");
        var search = Method(tier1, "public async Task<IndexedSearchResponse> SearchAsync(");
        var commit = search.LastIndexOf("await scope.CommitAsync(cancellationToken)", StringComparison.Ordinal);
        var offer = search.IndexOf("OfferShadow(", StringComparison.Ordinal);
        Assert.True(commit > 0 && offer > commit, "the shadow observation must be offered after the commit");
        Assert.Equal(1, CountOf(tier1, "OfferShadow(new IndexedSearchShadowObservation"));
        Assert.DoesNotContain("SearchAccessShadowComparer", tier1, StringComparison.Ordinal);
        Assert.DoesNotContain("CompareShadowAsync", tier1, StringComparison.Ordinal);
        var offerShadow = Method(tier1, "private void OfferShadow(");
        Assert.Contains("catch (Exception)", offerShadow, StringComparison.Ordinal);
        Assert.DoesNotContain("await", offerShadow, StringComparison.Ordinal); // never waits for the comparison
    }

    [Fact]
    public void NoServingPathReadsTheShadowResults()
    {
        // The comparison's result is counters and logs only: no search, page or business-facing endpoint reads it.
        var serving = new[]
        {
            Source("AuthorizationAuthority.Api", "Features", "SearchIndex", "IndexedSearchEndpoints.cs"),
            Source("AuthorizationAuthority.Application", "Features", "SearchIndex", "IndexedSearchService.cs"),
        };
        foreach (var text in serving)
        {
            Assert.DoesNotContain("SearchAccessShadowSnapshot", text, StringComparison.Ordinal);
            Assert.DoesNotContain("SearchAccessShadowResult", text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void TheWorkNotificationIsTransactionalAndTheLocalWakeupFollowsTheCommit()
    {
        var store = Source("AuthorizationAuthority.Infrastructure", "Persistence", "AuthorityStore.cs");
        var commit = Method(store, "public async Task CommitAsync(CancellationToken cancellationToken)");
        var notify = commit.IndexOf("pg_notify", StringComparison.Ordinal);
        var transactionCommit = commit.IndexOf("await transaction.CommitAsync(", StringComparison.Ordinal);
        var signal = commit.IndexOf("Signal(AuthorityWorkSignalSource.Commit)", StringComparison.Ordinal);
        Assert.True(notify > 0 && transactionCommit > notify && signal > transactionCommit, "pg_notify inside the transaction, the local signal after the commit");
        Assert.Equal(1, CountOf(store, "pg_notify"));

        var loop = Source("AuthorizationAuthority.Api", "Composition", "BackgroundProcessors.cs");
        var execute = loop[loop.IndexOf("class AuthorityWorkLoop", StringComparison.Ordinal)..loop.IndexOf("[LoggerMessage(Level = LogLevel.Critical", StringComparison.Ordinal)];
        Assert.Contains("store.GetOrganizationsWithPendingWorkAsync(", execute, StringComparison.Ordinal); // the durable scan decides the work
        Assert.Contains("signal.WaitAsync(didWork ? TimeSpan.FromMilliseconds(50) : TimeSpan.FromMilliseconds(250)", execute, StringComparison.Ordinal); // the poll stays the fallback
        Assert.DoesNotContain("Task.Delay(", execute, StringComparison.Ordinal);
    }

    private static int CountOf(string text, string value)
    {
        var count = 0;
        for (var i = text.IndexOf(value, StringComparison.Ordinal); i >= 0; i = text.IndexOf(value, i + value.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }
}
