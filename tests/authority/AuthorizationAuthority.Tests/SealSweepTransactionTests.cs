using AuthorizationAuthority.Application.Features.SearchIndex;
using AuthorizationAuthority.Domain.Features.SearchIndex;
using Xunit;

namespace AuthorizationAuthority.Tests;

/// <summary>
/// C-136 (R-33/R-36): the S-B seal pass in bounded transactions. Each transaction takes the organization lock, re-reads the
/// projection and approvals, verifies its entries against the current heads and commits; the per-entry verification timestamp
/// stays the only unit of completion, so an interrupted, failed or stopped pass advances exactly the entries it verified.
/// </summary>
public sealed class SealSweepTransactionTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<(SearchIndexTests.Harness H, List<Guid> Records)> SetUpAsync(int count, int transactionSize)
    {
        var h = new SearchIndexTests.Harness();
        await h.ApproveStandardFieldsAsync();
        var records = new List<Guid>();
        for (var i = 0; i < count; i++)
        {
            var record = h.Record();
            await h.WriteAsync(record);
            records.Add(record);
        }

        // A deterministic oldest-first order: record i was last verified i minutes after the base time.
        var baseTime = h.Clock.Now;
        for (var i = 0; i < count; i++)
        {
            h.Entry(records[i]).LastSealVerifiedOn = baseTime.AddMinutes(i);
        }

        h.Clock.Now = baseTime.AddHours(2);
        h.Options.SealCheckTransactionSize = transactionSize;
        return (h, records);
    }

    [Fact]
    public async Task APassVerifiesEveryEntryOnceInBoundedTransactions()
    {
        var (h, records) = await SetUpAsync(7, 3);
        using var _ = h;
        var commits = h.Store.ScopeCommitCount;
        var result = await h.Sweep.RunSealPassAsync(h.Org, Ct);
        Assert.Equal((SweepUnitOutcome.Pass, 7, 0, 3), (result.Outcome, result.UnitsPassed, result.UnitsInconclusive, result.Transactions));
        Assert.Equal(commits + 3, h.Store.ScopeCommitCount); // 3 + 3 + 1 entries, one commit each; the empty fourth selection writes nothing
        Assert.Equal(4, h.Index.SealCheckSelections);
        Assert.All(records, r => Assert.Equal(h.Clock.Now, h.Entry(r).LastSealVerifiedOn));
    }

    [Fact]
    public async Task AnInconclusiveEntryIsNotSelectedAgainInTheSamePassAndKeepsItsTimestamp()
    {
        var (h, records) = await SetUpAsync(4, 1);
        using var _ = h;
        h.Entry(records[0]).SealKeyVersion = 99;
        h.Entry(records[2]).SealKeyVersion = 99;
        var before = h.Entry(records[0]).LastSealVerifiedOn;
        var result = await h.Sweep.RunSealPassAsync(h.Org, Ct);
        Assert.Equal((SweepUnitOutcome.Inconclusive, 2, 2, 4), (result.Outcome, result.UnitsPassed, result.UnitsInconclusive, result.Transactions));
        Assert.Equal(before, h.Entry(records[0]).LastSealVerifiedOn); // never counted as verified
        Assert.Equal(5, h.Index.SealCheckSelections); // four entries, then an empty selection: no loop on inconclusive entries
    }

    [Fact]
    public async Task AFailInALaterTransactionQuarantinesTheIndexAndAdvancesOnlyTheEntriesVerifiedBefore()
    {
        var (h, records) = await SetUpAsync(6, 2);
        using var _ = h;
        var untouched = h.Entry(records[5]).LastSealVerifiedOn;
        h.Entry(records[4]).EntrySealSignature[3] ^= 0x01; // the third transaction's first entry
        var result = await h.Sweep.RunSealPassAsync(h.Org, Ct);
        Assert.Equal((SweepUnitOutcome.Fail, "SEAL_INVALID", 4, 3), (result.Outcome, result.FindingCode, result.UnitsPassed, result.Transactions));
        Assert.Equal(SearchProjectionStatuses.Quarantined, h.Projection.ProjectionStatusName);
        Assert.False(h.State.IntegrityQuarantineEnabled); // S1 untouched (§17.2 Q3)
        Assert.All(records.Take(4), r => Assert.Equal(h.Clock.Now, h.Entry(r).LastSealVerifiedOn));
        Assert.NotEqual(h.Clock.Now, h.Entry(records[4]).LastSealVerifiedOn);
        Assert.Equal(untouched, h.Entry(records[5]).LastSealVerifiedOn); // never attempted, never reported as verified
        Assert.Contains(h.Alerts.Raised, a => a.AlertKind == "SEARCH_INDEX_QUARANTINE");
    }

    [Fact]
    public async Task AnInterruptionBetweenTransactionsAdvancesOnlyTheCommittedEntriesAndTheNextPassResumes()
    {
        var (h, records) = await SetUpAsync(6, 2);
        using var _ = h;
        var oldest = records.Select(r => h.Entry(r).LastSealVerifiedOn).ToList();
        h.Index.BeforeSealCheckSelection = n =>
        {
            if (n == 1)
            {
                throw new OperationCanceledException("process stopped"); // cancellation or a crash before the second transaction did anything
            }
        };
        await Assert.ThrowsAsync<OperationCanceledException>(() => h.Sweep.RunSealPassAsync(h.Org, Ct));
        Assert.All(records.Take(2), r => Assert.Equal(h.Clock.Now, h.Entry(r).LastSealVerifiedOn));
        Assert.All(Enumerable.Range(2, 4), i => Assert.Equal(oldest[i], h.Entry(records[i]).LastSealVerifiedOn));

        // The deadline sees the oldest unverified entry; the next pass starts from it (oldest first).
        h.Index.BeforeSealCheckSelection = null;
        h.Clock.Now = h.Clock.Now.AddMinutes(1);
        var resumed = await h.Sweep.RunSealPassAsync(h.Org, Ct);
        Assert.Equal((SweepUnitOutcome.Pass, 6), (resumed.Outcome, resumed.UnitsPassed));
        Assert.All(records, r => Assert.Equal(h.Clock.Now, h.Entry(r).LastSealVerifiedOn));
    }

    [Fact]
    public async Task ACancellationRequestedBetweenTransactionsStopsBeforeTheNextOne()
    {
        var (h, records) = await SetUpAsync(4, 2);
        using var _ = h;
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        h.Index.BeforeSealCheckSelection = n =>
        {
            if (n == 0)
            {
                stop.Cancel(); // during the first transaction: it completes, the next one is never started
            }
        };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => h.Sweep.RunSealPassAsync(h.Org, stop.Token));
        Assert.Equal(1, h.Index.SealCheckSelections);
    }

    [Fact]
    public async Task AQuarantineBetweenTransactionsEndsThePassAsInconclusiveWithoutVerifyingTheRest()
    {
        var (h, records) = await SetUpAsync(6, 2);
        using var _ = h;
        var oldest = records.Select(r => h.Entry(r).LastSealVerifiedOn).ToList();
        h.Index.BeforeSealCheckSelection = n =>
        {
            if (n == 0)
            {
                h.Projection.ProjectionStatusName = SearchProjectionStatuses.Quarantined; // e.g. a search-side finding committed between batches
            }
        };
        var result = await h.Sweep.RunSealPassAsync(h.Org, Ct);
        Assert.Equal((SweepUnitOutcome.Inconclusive, "QUARANTINED", 2, 1), (result.Outcome, result.FindingCode, result.UnitsPassed, result.Transactions));
        Assert.All(Enumerable.Range(2, 4), i => Assert.Equal(oldest[i], h.Entry(records[i]).LastSealVerifiedOn));
        Assert.Equal(SearchProjectionStatuses.Quarantined, h.Projection.ProjectionStatusName); // never cleared by the sweep
    }

    [Fact]
    public async Task ARecordCommittedBetweenTransactionsIsVerifiedAgainstItsNewHead()
    {
        var (h, records) = await SetUpAsync(4, 2);
        using var _ = h;
        h.Index.BeforeSealCheckSelection = n =>
        {
            if (n == 1)
            {
                // A save between the first and second transaction: a new revision, a new entry derived and sealed at commit.
                h.WriteAsync(records[3], h.Rows(records[3], text: "changed between batches")).GetAwaiter().GetResult();
            }
        };
        var result = await h.Sweep.RunSealPassAsync(h.Org, Ct);
        Assert.Equal(SweepUnitOutcome.Pass, result.Outcome);
        Assert.Equal(h.Head(records[3]).CommittedOperationId, h.Entry(records[3]).IndexedOperationId);
        Assert.Equal(SearchProjectionStatuses.Active, h.Projection.ProjectionStatusName);
    }

    [Fact]
    public async Task ADeadlineIsEvaluatedFromTheOldestUnverifiedEntryOfAnInterruptedPass()
    {
        var (h, records) = await SetUpAsync(4, 2);
        using var _ = h;
        h.Options.SealVerificationDeadlineSeconds = 3_600; // Testing only; entries 2 and 3 were last verified about two hours ago
        h.Index.BeforeSealCheckSelection = n =>
        {
            if (n == 1)
            {
                throw new OperationCanceledException("interrupted");
            }
        };
        await Assert.ThrowsAsync<OperationCanceledException>(() => h.Sweep.RunSealPassAsync(h.Org, Ct));
        h.Index.BeforeSealCheckSelection = null;
        var evaluation = await h.Sweep.EvaluateDeadlinesAsync(h.Org, Ct);
        Assert.Contains(SearchIndexSweepService.SealSweep, evaluation.OverdueScopes); // half a pass is never a completed pass
        Assert.True(h.Projection.VerificationSuspended);
    }
}
