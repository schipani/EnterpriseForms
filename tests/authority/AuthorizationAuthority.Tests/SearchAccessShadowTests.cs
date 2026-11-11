using AuthorizationAuthority.Application.Common.Ports;
using AuthorizationAuthority.Application.Features.SearchAccess;
using AuthorizationAuthority.Application.Features.SearchIndex;
using Xunit;

namespace AuthorizationAuthority.Tests;

/// <summary>
/// C-150 IX-5c shadow preparation, unit level (in-memory store; the stack covers PostgreSQL, RLS and the Tier 1 hook): only an equivalent
/// authorized snapshot is compared; a Tier 2 answer equal to Tier 1's is a Match, any unexplained difference a Mismatch (alerted, counts
/// only); a moved revision, a closed gate or an unready Tier 2 is NotComparable, never a mismatch; revocation after the search is never
/// re-evaluated at the new snapshot; the share-only item rule and Tier 1's metadata filter apply; the queue is bounded and never blocks;
/// a comparison has its own time budget.
/// </summary>
public sealed partial class SearchAccessProjectionTests
{
    private static PreparedIndexedCondition CountEquals(Fixture f, long value) => new(1, f.Count, "equals", "Integer", value, null, null);

    private static PreparedIndexedCondition StatusIs(Fixture f) => new(1, f.Status, "is", "Option", f.Open, null, null);

    private static PreparedIndexedCondition TagEquals(Fixture f, string value) => new(1, f.Tag, "equals", "String", value, null, null);

    /// <summary>The fixture with its organization gate Ready (projection at the authorization revision) and a Tier 1 entry and form state.</summary>
    private static async Task<Fixture> ShadowFixtureAsync(string recordStatus = "Draft")
    {
        var f = await FixtureAsync();
        f.W.State.ProjectionAppliedRevision = f.W.State.AuthorizationRevision;
        f.W.State.ProjectionZedToken = "zed";
        f.W.Store.SearchAccess.Tier1Headers.Add(new Tier1RecordHeader(f.Record, recordStatus, null));
        f.W.Store.SearchAccess.Tier1FormContentRevision = 3;
        return f;
    }

    private static IndexedSearchShadowObservation Observed(Fixture f, Guid user, IReadOnlyList<Guid> tier1, params PreparedIndexedCondition[] conditions) =>
        new(Org, FormId, user, conditions, [f.CountApproval, f.StatusApproval, f.TagApproval], null, false, false, tier1, f.W.State.AuthorizationRevision, f.W.State.ContentIntegrityRevision, 3, Guid.CreateVersion7());

    [Fact]
    public async Task ATier2AnswerEqualToTier1sAtTheSameSnapshotIsAMatch()
    {
        var f = await ShadowFixtureAsync();
        async Task<SearchAccessShadowResult> Compare(Guid user, IReadOnlyList<Guid> tier1, params PreparedIndexedCondition[] c) => await f.W.Service.CompareShadowAsync(Observed(f, user, tier1, c), Ct);

        Assert.Equal(SearchAccessShadowClassifications.Match, (await Compare(f.Alice, [f.Record], CountEquals(f, 5), StatusIs(f), TagEquals(f, "urgent"))).Classification);
        Assert.Equal(SearchAccessShadowClassifications.Match, (await Compare(f.Alice, [], CountEquals(f, 6))).Classification);
        Assert.Equal(SearchAccessShadowClassifications.Match, (await Compare(f.Bob, [f.Record], CountEquals(f, 5))).Classification); // direct share
        Assert.Equal(SearchAccessShadowClassifications.Match, (await Compare(f.Carol, [f.Record], StatusIs(f))).Classification); // through the team
        Assert.Equal(SearchAccessShadowClassifications.Match, (await Compare(f.Bob, [], CountEquals(f, 5), StatusIs(f))).Classification); // status is not Bob's
        Assert.Equal(SearchAccessShadowClassifications.Match, (await Compare(f.Carol, [], TagEquals(f, "urgent"))).Classification);
    }

    [Fact]
    public async Task AnUnexplainedDifferenceIsAMismatchWithCountsOnlyAndIsAlerted()
    {
        var f = await ShadowFixtureAsync();
        var comparer = new SearchAccessShadowComparer(f.W.Service, new SearchAccessOptions { Mode = SearchAccessMode.Dark }, f.W.Alerts);

        var extra = await comparer.CompareAsync(Observed(f, f.Alice, [], CountEquals(f, 5)), Ct); // Tier 1 said none; Tier 2 finds the record
        Assert.Equal((SearchAccessShadowClassifications.Mismatch, 0, 1, 0), (extra.Classification, extra.OnlyInTier1Count, extra.OnlyInTier2Count, extra.ExcludedByMetadataCount));
        var missing = await comparer.CompareAsync(Observed(f, f.Bob, [f.Record], StatusIs(f)), Ct); // Tier 1 returned what Bob's Tier 2 rows cannot match
        Assert.Equal((SearchAccessShadowClassifications.Mismatch, 1, 0), (missing.Classification, missing.OnlyInTier1Count, missing.OnlyInTier2Count));

        var alerts = f.W.Alerts.Raised.Where(a => a.AlertKind == "SEARCH_ACCESS_SHADOW_MISMATCH").ToList();
        Assert.Equal(2, alerts.Count);
        Assert.All(alerts, a => Assert.DoesNotContain(f.Record.ToString("D"), a.Summary, StringComparison.OrdinalIgnoreCase)); // counts only, never a record id
        Assert.All(alerts, a => Assert.DoesNotContain("urgent", a.Summary, StringComparison.Ordinal));
        Assert.Equal(2, comparer.Snapshot().Mismatched);
    }

    [Fact]
    public async Task OnlyAnEquivalentAuthorizedSnapshotIsComparedEverythingElseIsNotComparable()
    {
        var f = await ShadowFixtureAsync();
        var current = Observed(f, f.Alice, [f.Record], CountEquals(f, 5));
        async Task<string?> Reason(IndexedSearchShadowObservation o)
        {
            var r = await f.W.Service.CompareShadowAsync(o, Ct);
            Assert.True(r.Classification is SearchAccessShadowClassifications.NotComparable or SearchAccessShadowClassifications.Match, r.Classification);
            return r.Classification == SearchAccessShadowClassifications.Match ? null : r.ReasonCode;
        }

        Assert.Null(await Reason(current));
        Assert.Equal(SearchAccessShadowReasonCodes.SnapshotMoved, await Reason(current with { AuthorizationRevision = current.AuthorizationRevision - 1 }));
        Assert.Equal(SearchAccessShadowReasonCodes.SnapshotMoved, await Reason(current with { ContentIntegrityRevision = current.ContentIntegrityRevision + 1 }));
        Assert.Equal(SearchAccessShadowReasonCodes.SnapshotMoved, await Reason(current with { FormContentRevision = 2 }));
        Assert.Equal(SearchAccessShadowReasonCodes.OrganizationNotFound, await Reason(current with { OrganizationId = Guid.CreateVersion7() }));

        f.W.State.ProjectionAppliedRevision = f.W.State.AuthorizationRevision - 1; // S1 behind: the gate is closed
        Assert.Equal(SearchAccessShadowReasonCodes.GateClosed, await Reason(current));
        f.W.State.ProjectionAppliedRevision = f.W.State.AuthorizationRevision;
        f.W.State.IntegrityQuarantineEnabled = true;
        Assert.Equal(SearchAccessShadowReasonCodes.GateClosed, await Reason(current));
        f.W.State.IntegrityQuarantineEnabled = false;

        // A new command the Tier 2 maintainer has not consumed: the gate may be Ready, but Tier 2 is not (A-3 exact equality).
        f.W.Touch(Member(f.Team, f.Bob));
        f.W.State.ProjectionAppliedRevision = f.W.State.AuthorizationRevision;
        Assert.Equal(SearchAccessShadowReasonCodes.Tier2NotReady, await Reason(Observed(f, f.Alice, [f.Record], CountEquals(f, 5))));
    }

    [Fact]
    public async Task ARevocationAfterTheSearchIsNeverAMismatchAndNeverReevaluatedAtTheNewSnapshot()
    {
        var f = await ShadowFixtureAsync();
        var beforeRevocation = Observed(f, f.Bob, [f.Record], CountEquals(f, 5)); // Tier 1 answered while Bob was shared
        f.W.Delete(Reader(f.Share, f.Bob));
        await f.W.Service.CatchUpAsync(Org, 1000, Ct);
        f.W.State.ProjectionAppliedRevision = f.W.State.AuthorizationRevision;
        var result = await f.W.Service.CompareShadowAsync(beforeRevocation, Ct);
        Assert.Equal((SearchAccessShadowClassifications.NotComparable, SearchAccessShadowReasonCodes.SnapshotMoved), (result.Classification, result.ReasonCode));
        Assert.Null(result.Tier2Count); // nothing was evaluated at the new snapshot

        // At the new snapshot Tier 2 no longer finds the record for Bob (a Tier 1 answer of none is a Match).
        Assert.Equal(SearchAccessShadowClassifications.Match, (await f.W.Service.CompareShadowAsync(Observed(f, f.Bob, [], CountEquals(f, 5)), Ct)).Classification);
    }

    [Fact]
    public async Task TheShareOnlyItemRuleAndTier1sMetadataFilterApply()
    {
        var f = await ShadowFixtureAsync(recordStatus: "Archived");
        // Archived and not asked for: Tier 1 excludes it; Tier 2's candidate is explained by the metadata filter, not a difference.
        var archived = await f.W.Service.CompareShadowAsync(Observed(f, f.Alice, [], CountEquals(f, 5)), Ct);
        Assert.Equal((SearchAccessShadowClassifications.Match, 1, 0), (archived.Classification, archived.ExcludedByMetadataCount, archived.Tier2Count));
        var asked = await f.W.Service.CompareShadowAsync(Observed(f, f.Alice, [f.Record], CountEquals(f, 5)) with { IncludeArchived = true }, Ct);
        Assert.Equal(SearchAccessShadowClassifications.Match, asked.Classification);

        // A share-only caller who reads only one of two accepted count items cannot match (UF-25): Tier 1's empty answer matches.
        f.W.Store.SearchAccess.ItemCounts[(f.Record, f.CountApproval)] = 2;
        Assert.Equal(SearchAccessShadowClassifications.Match, (await f.W.Service.CompareShadowAsync(Observed(f, f.Bob, [], CountEquals(f, 5)) with { IncludeArchived = true }, Ct)).Classification);
    }

    [Fact]
    public async Task TheQueueIsBoundedAndNeverBlocksTheOfferingSearch()
    {
        var f = await ShadowFixtureAsync();
        var comparer = new SearchAccessShadowComparer(f.W.Service, new SearchAccessOptions { Mode = SearchAccessMode.Dark, ShadowQueueCapacity = 2 }, f.W.Alerts);
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var accepted = Enumerable.Range(0, 5).Select(_ => comparer.Offer(Observed(f, f.Alice, [f.Record], CountEquals(f, 5)))).ToList();
        Assert.True(clock.Elapsed < TimeSpan.FromMilliseconds(200));
        Assert.Equal([true, true, false, false, false], accepted);
        Assert.Equal((5, 3), (comparer.Snapshot().Offered, comparer.Snapshot().Dropped));

        using var stop = new CancellationTokenSource();
        var worker = comparer.RunAsync(stop.Token);
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (comparer.Snapshot().Matched < 2 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10, Ct);
        }

        Assert.Equal(2, comparer.Snapshot().Matched);
        await stop.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => worker);
    }

    [Fact]
    public async Task AComparisonPastItsTimeBudgetIsCancelledAndTheHostsStopPropagates()
    {
        var f = await ShadowFixtureAsync();
        var slow = new SearchAccessProjectionService(new SlowSnapshotStore(f.W.Store), TestProjection.Verifier(f.W.Store, new UnusedSpiceDb()), new SearchAccessOptions { Mode = SearchAccessMode.Dark }, f.W.Alerts, new NoFaults(), TimeProvider.System);
        var comparer = new SearchAccessShadowComparer(slow, new SearchAccessOptions { Mode = SearchAccessMode.Dark, ShadowMaximumMilliseconds = 100 }, f.W.Alerts);
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var result = await comparer.CompareAsync(Observed(f, f.Alice, [f.Record], CountEquals(f, 5)), Ct);
        Assert.Equal((SearchAccessShadowClassifications.Cancelled, SearchAccessShadowReasonCodes.TimeBudget), (result.Classification, result.ReasonCode));
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(3));
        Assert.Equal(1, comparer.Snapshot().Cancelled);

        using var stopping = new CancellationTokenSource();
        await stopping.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => comparer.CompareAsync(Observed(f, f.Alice, [f.Record], CountEquals(f, 5)), stopping.Token));
    }

    [Fact]
    public void ShadowComparisonIsOffByDefault() => Assert.False(new SearchAccessOptions().ShadowComparisonEnabled);

    /// <summary>A store whose read-only snapshots take 10 s to open (honouring cancellation): the comparison's time budget must end it.</summary>
    private sealed class SlowSnapshotStore(InMemoryAuthorityStore inner) : IAuthorityStore
    {
        public Task<IAuthorityScope> BeginScopeAsync(CancellationToken cancellationToken) => inner.BeginScopeAsync(cancellationToken);

        public async Task<IAuthorityScope> BeginReadOnlySnapshotScopeAsync(CancellationToken cancellationToken)
        {
            await Task.Delay(TimeSpan.FromSeconds(10), cancellationToken);
            return await inner.BeginReadOnlySnapshotScopeAsync(cancellationToken);
        }

        public Task<IReadOnlyList<Guid>> GetOrganizationsWithPendingWorkAsync(CancellationToken cancellationToken) => inner.GetOrganizationsWithPendingWorkAsync(cancellationToken);

        public Task<IReadOnlyList<Guid>> GetAllOrganizationIdsAsync(CancellationToken cancellationToken) => inner.GetAllOrganizationIdsAsync(cancellationToken);

        public Task<IReadOnlyList<OrganizationChangeMarker>> GetOrganizationChangeMarkersAsync(CancellationToken cancellationToken) => inner.GetOrganizationChangeMarkersAsync(cancellationToken);
    }
}
