using AuthorizationAuthority.Application.Common.Ports;
using AuthorizationAuthority.Application.Features.SearchAccess;
using AuthorizationAuthority.Domain.Features.SearchAccess;
using Xunit;

namespace AuthorizationAuthority.Tests;

/// <summary>
/// C-149 (R-55) unit tests of change-aware Tier 2 maintenance: an unchanged organization costs no transaction between reconciliation
/// passes, a moved marker is caught up at once, a restart or reconciliation pass probes every organization (read-only), a change that
/// advances no marker is consumed at the next reconciliation while readiness stays closed, and a damaged checkpoint is never settled
/// by the probe but quarantined by the locked steps. In-memory store; the stack covers PostgreSQL.
/// </summary>
public sealed partial class SearchAccessProjectionTests
{
    private sealed class ManualClock : TimeProvider
    {
        private long ticks = 1;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => ticks;

        public void Advance(TimeSpan by) => ticks += by.Ticks;
    }

    private static readonly SearchAccessOptions Reconcile60 = new() { Mode = SearchAccessMode.Dark, ReconciliationIntervalSeconds = 60 };

    private sealed record Pass(int WriteScopes, int ReadOnlyScopes, IReadOnlyList<SearchAccessMaintenanceItem> Items, bool Reconciliation, int Skipped);

    /// <summary>One loop pass exactly as SearchAccessMaintenanceLoop runs it (markers, plan, one step per planned organization).</summary>
    private static async Task<Pass> PassAsync(World w, SearchAccessMaintenanceSchedule schedule)
    {
        int writes = w.Store.ScopeCount, reads = w.Store.ReadOnlyScopeCount;
        var (items, reconciliation, skipped) = schedule.Plan(await w.Store.GetOrganizationChangeMarkersAsync(Ct));
        foreach (var item in items)
        {
            await w.Service.MaintainAsync(schedule, item, Ct);
        }

        return new(w.Store.ScopeCount - writes, w.Store.ReadOnlyScopeCount - reads, items, reconciliation, skipped);
    }

    [Fact]
    public void TheScheduleSkipsSettledOrganizationsCatchesUpMovedOnesAndReconcilesOnTheInterval()
    {
        var clock = new ManualClock();
        var schedule = new SearchAccessMaintenanceSchedule(Reconcile60, clock);
        var a = new OrganizationChangeMarker(Guid.NewGuid(), 3, 2, 5);
        var b = new OrganizationChangeMarker(Guid.NewGuid(), 1, 0, 1);

        var first = schedule.Plan([a, b]);
        Assert.True(first.Reconciliation);
        Assert.All(first.Items, i => Assert.Equal(SearchAccessMaintenanceAction.Probe, i.Action)); // after a restart: everything probed
        schedule.Settle(a);
        schedule.Settle(b);

        var idle = schedule.Plan([a, b]);
        Assert.False(idle.Reconciliation);
        Assert.Empty(idle.Items);
        Assert.Equal(2, idle.Skipped);

        foreach (var moved in new[] { a with { AuthorizationRevision = 4 }, a with { ContentIntegrityRevision = 3 }, a with { AuditJournalRevision = 6 } })
        {
            var plan = schedule.Plan([moved, b]);
            Assert.Equal([(moved, SearchAccessMaintenanceAction.CatchUp)], plan.Items.Select(i => (i.Marker, i.Action)).ToList());
        }

        schedule.Unsettle(b.OrganizationId); // work remained: due again, probed (no evidence of what moved)
        Assert.Contains(schedule.Plan([a, b]).Items, i => i.Marker == b && i.Action == SearchAccessMaintenanceAction.Probe);
        schedule.Settle(b);

        clock.Advance(TimeSpan.FromSeconds(59));
        Assert.Empty(schedule.Plan([a, b]).Items);
        clock.Advance(TimeSpan.FromSeconds(1));
        var reconciliation = schedule.Plan([a, b]);
        Assert.True(reconciliation.Reconciliation);
        Assert.Equal(2, reconciliation.Items.Count(i => i.Action == SearchAccessMaintenanceAction.Probe));

        Assert.Empty(schedule.Plan([a]).Items); // a removed organization is forgotten
        Assert.Equal(1, schedule.SettledCount);

        Assert.True(SearchAccessMaintenanceSchedule.Settles(SearchAccessStepOutcome.Idle));
        Assert.True(SearchAccessMaintenanceSchedule.Settles(SearchAccessStepOutcome.Quarantined));
        Assert.True(SearchAccessMaintenanceSchedule.Settles(SearchAccessStepOutcome.Fenced));
        foreach (var due in new[] { SearchAccessStepOutcome.Advanced, SearchAccessStepOutcome.Contended, SearchAccessStepOutcome.NotBuilt, SearchAccessStepOutcome.UpgradeRequired })
        {
            Assert.False(SearchAccessMaintenanceSchedule.Settles(due));
        }
    }

    [Fact]
    public async Task AnUnchangedOrganizationCostsNoTransactionAndAMovedMarkerIsCaughtUpAtOnce()
    {
        var f = await FixtureAsync();
        var clock = new ManualClock();
        var schedule = new SearchAccessMaintenanceSchedule(Reconcile60, clock);

        var first = await PassAsync(f.W, schedule);
        Assert.Equal((0, 1), (first.WriteScopes, first.ReadOnlyScopes)); // probed in one read-only snapshot; no row lock, no write
        for (var i = 0; i < 20; i++)
        {
            var idle = await PassAsync(f.W, schedule);
            Assert.Equal((0, 0, 1), (idle.WriteScopes, idle.ReadOnlyScopes, idle.Skipped)); // nothing at all between reconciliations
        }

        // A revocation moves the authorization marker: readiness closes at once (live comparison), the next pass consumes it.
        f.W.Delete(Reader(f.Share, f.Bob));
        Assert.Equal(SearchAccessStatusNames.Building, await f.W.StatusAsync());
        var caught = await PassAsync(f.W, schedule);
        Assert.Equal(SearchAccessMaintenanceAction.CatchUp, Assert.Single(caught.Items).Action);
        Assert.Equal(SearchAccessStatusNames.Ready, await f.W.StatusAsync());
        Assert.DoesNotContain(f.W.Store.SearchAccess.ValuePostings, v => v.AccessPrincipalEntityId == f.Bob);
        f.W.AssertRowsEqualCanonical();

        // A content commit moves the content marker.
        f.W.Store.SearchAccess.Searchable.RemoveAll(s => s.ManifestItemId == f.CountItem);
        f.W.Store.SearchAccess.Searchable.Add(Value(f, f.Count, f.CountApproval, f.CountItem, "Integer", integer: 11));
        f.W.CommitContent(f.Record);
        Assert.Equal(SearchAccessStatusNames.Building, await f.W.StatusAsync());
        Assert.Equal(SearchAccessMaintenanceAction.CatchUp, Assert.Single((await PassAsync(f.W, schedule)).Items).Action);
        Assert.Equal(SearchAccessStatusNames.Ready, await f.W.StatusAsync());
        Assert.All(f.W.Store.SearchAccess.ValuePostings.Where(v => v.ManifestItemId == f.CountItem), v => Assert.Equal(11, v.IntegerValue));
        Assert.Equal(0, (await PassAsync(f.W, schedule)).WriteScopes);
        Assert.True((await f.W.Service.VerifyAsync(Org, Guid.NewGuid(), Ct)).Clean);
    }

    [Fact]
    public async Task AChangeThatAdvancesNoMarkerStaysUnservableUntilTheNextReconciliationConsumesIt()
    {
        var f = await FixtureAsync();
        var clock = new ManualClock();
        var schedule = new SearchAccessMaintenanceSchedule(Reconcile60, clock);
        await PassAsync(f.W, schedule);

        // A coverage withdrawal applied without a journal event (as the Tier 1 cheap sweep withdraws a coverage): no marker moves.
        f.W.Store.SearchAccess.Coverages.RemoveAll(c => c.FieldSearchApprovalId == f.TagApproval);
        for (var i = 0; i < 5; i++)
        {
            clock.Advance(TimeSpan.FromSeconds(10));
            Assert.Empty((await PassAsync(f.W, schedule)).Items);
            Assert.Equal(SearchAccessStatusNames.Building, await f.W.StatusAsync()); // fail closed: never Ready from the old rows
        }

        clock.Advance(TimeSpan.FromSeconds(10));
        var reconciliation = await PassAsync(f.W, schedule);
        Assert.True(reconciliation.Reconciliation);
        Assert.Equal(SearchAccessStatusNames.Ready, await f.W.StatusAsync());
        Assert.DoesNotContain(f.W.Store.SearchAccess.ValuePostings, v => v.FieldSearchApprovalId == f.TagApproval);
    }

    [Fact]
    public async Task ARestartedLoopProbesEveryOrganizationAndLosesNoCommittedWork()
    {
        var f = await FixtureAsync();
        var before = new SearchAccessMaintenanceSchedule(Reconcile60, new ManualClock());
        await PassAsync(f.W, before);

        // Commands committed while no maintainer runs (crash, restart, deployment): the new process starts with an empty memo.
        f.W.Delete(Item(f.CountItem, f.Share));
        f.W.CommitContent(f.Record);
        var restarted = new SearchAccessMaintenanceSchedule(Reconcile60, new ManualClock());
        var first = await PassAsync(f.W, restarted);
        Assert.Equal(SearchAccessMaintenanceAction.Probe, Assert.Single(first.Items).Action);
        Assert.Equal(SearchAccessStatusNames.Ready, await f.W.StatusAsync());
        Assert.Equal(f.W.State.AuthorizationRevision, f.W.Checkpoint.ConsumedAuthorizationRevision);
        Assert.Equal(f.W.State.ContentIntegrityRevision, f.W.Checkpoint.ConsumedContentIntegrityRevision);
        f.W.AssertRowsEqualCanonical();
    }

    [Fact]
    public async Task TheProbeNeverSettlesADamagedCheckpointTheLockedStepsQuarantineItAndAQuarantinedOrganizationIsThenSkipped()
    {
        var f = await FixtureAsync();
        var clock = new ManualClock();
        var schedule = new SearchAccessMaintenanceSchedule(Reconcile60, clock);
        await PassAsync(f.W, schedule);

        // Out-of-band damage to the checkpoint at an unchanged revision (no marker moves): found by the next reconciliation probe.
        f.W.Checkpoint.ConsumedJournalDigest = new byte[32];
        Assert.Equal(SearchAccessProbeOutcome.WorkPending, await f.W.Service.ProbeAsync(Org, Ct));
        clock.Advance(TimeSpan.FromSeconds(60));
        var reconciliation = await PassAsync(f.W, schedule);
        Assert.True(reconciliation.WriteScopes > 0); // the steps ran under the row lock
        var status = await f.W.Service.StatusAsync(Org, Ct);
        Assert.Equal((SearchAccessStatusNames.Quarantined, SearchAccessReasonCodes.CheckpointDigestMismatch), (status.StatusName, status.QuarantineReasonCode));
        Assert.Single(f.W.Alerts.Raised, a => a.AlertKind == "SEARCH_ACCESS_PROJECTION_QUARANTINED");

        // The quarantine was journaled (the audit marker moved): one more step settles it; afterwards nothing until a rebuild.
        await PassAsync(f.W, schedule);
        for (var i = 0; i < 5; i++)
        {
            Assert.Equal((0, 0), ((await PassAsync(f.W, schedule)).WriteScopes, (await PassAsync(f.W, schedule)).ReadOnlyScopes));
        }

        Assert.Equal(SearchAccessProbeOutcome.Quarantined, await f.W.Service.ProbeAsync(Org, Ct));
        await f.W.Service.RebuildAsync(Org, "OPERATOR", Guid.NewGuid(), Ct); // the only way out; journaled, so the marker moves again
        await PassAsync(f.W, schedule);
        Assert.Equal(SearchAccessStatusNames.Ready, await f.W.StatusAsync());
    }

    [Fact]
    public async Task AMissingCheckpointIsBuiltAndThenSettled()
    {
        var w = new World();
        Guid alice = Guid.NewGuid(), record = Guid.NewGuid();
        w.Touch(Owner(record, alice));
        var schedule = new SearchAccessMaintenanceSchedule(Reconcile60, new ManualClock());
        Assert.Equal(SearchAccessProbeOutcome.WorkPending, await w.Service.ProbeAsync(Org, Ct));
        await PassAsync(w, schedule); // probe -> NotBuilt -> initial build (journaled)
        Assert.Equal(SearchAccessStatusNames.Ready, await w.StatusAsync());
        await PassAsync(w, schedule); // the build's audit event moved the marker: caught up once, settled
        Assert.Equal((0, 0), ((await PassAsync(w, schedule)).WriteScopes, (await PassAsync(w, schedule)).ReadOnlyScopes));
        w.AssertRowsEqualCanonical();
    }
}
