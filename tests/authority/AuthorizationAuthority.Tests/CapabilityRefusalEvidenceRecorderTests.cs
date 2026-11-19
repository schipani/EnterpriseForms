using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json;
using AuthorizationAuthority.Application.Features.Content;
using AuthorizationAuthority.Application.Features.Journal;
using AuthorizationAuthority.Domain.Features.Journal;
using AuthorizationAuthority.Domain.Features.Organizations;
using AuthorizationAuthority.Infrastructure.Archive;
using Xunit;

namespace AuthorizationAuthority.Tests;

/// <summary>
/// E3-M2f-3b (docs/security/E3_M2F3_CONTRACT_CLOSURE.md §3–§5, §10.2, §10.4) on <see cref="CapabilityRefusalEvidenceWorld"/>: a
/// transactional double of AuthorityDb with a real organization row lock. The PostgreSQL behaviour of the same SQL is E3-M2f-3G.
/// </summary>
public sealed class CapabilityRefusalEvidenceRecorderTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class Outcomes : ICapabilityRefusalEvidenceObserver
    {
        public ConcurrentQueue<(Guid Org, string Entry, CapabilityRefusalEvidenceOutcome Outcome, CapabilityRefusalEvidenceReason Reason)> All { get; } = new();

        public void Observed(Guid organizationId, string entryPoint, CapabilityRefusalEvidenceOutcome outcome, CapabilityRefusalEvidenceReason reason) => All.Enqueue((organizationId, entryPoint, outcome, reason));
    }

    private static (CapabilityRefusalEvidenceWorld World, Guid Org, CapabilityRefusalEvidenceRecorder Recorder, Outcomes Seen) Setup(CapabilityRefusalEvidenceLimits? limits = null)
    {
        var world = new CapabilityRefusalEvidenceWorld();
        var org = world.AddOrganization();
        var seen = new Outcomes();
        return (world, org, new CapabilityRefusalEvidenceRecorder(world.Store, world.Clock, seen, limits), seen);
    }

    private static Task<CapabilityRefusalEvidenceOutcome> Refuse(CapabilityRefusalEvidenceRecorder recorder, Guid org, string entry = CapabilityRefusalEvidence.EntryPoints.Verify) =>
        recorder.RecordAsync(new CapabilityRefusalContext(org, 1), entry, CancellationToken.None);

    private static List<string> CallsOf(CapabilityRefusalEvidenceWorld world, int scope) =>
        world.Events.Where(e => e.EndsWith("#" + scope, StringComparison.Ordinal)).Select(e => e[..e.IndexOf('#', StringComparison.Ordinal)]).ToList();

    // ---- first refusal: exactly one entry, exact T2 footprint (M2F3-08..14 core, M2F3-54, M2F3-56, M2F3-01) ----

    [Fact]
    public async Task AFirstRefusalCommitsOneEntryWithTheExactFootprint()
    {
        var (world, org, recorder, _) = Setup();
        var before = CapabilityRefusalEvidenceWorld.Clone(world.Organizations[org]);
        world.DatabaseTime = new DateTimeOffset(2026, 11, 17, 10, 42, 7, 123, TimeSpan.Zero);

        Assert.Equal(CapabilityRefusalEvidenceOutcome.Committed, await Refuse(recorder, org));

        // One scope, exactly these calls in this order: no fence, no ledger, no other write.
        Assert.Equal(["BeginScopeAsync", "SetCapabilityEvidenceLockTimeoutAsync", "LockOrganizationAsync", "StartCapabilityEvidenceLookupAsync",
            "IsCapabilityRefusalEvidenceIndexUsableAsync", "GetNewestCapabilityRefusalEvidenceAsync", "AddJournalEntry", "CommitAsync", "DisposeAsync"], CallsOf(world, 1));
        Assert.Empty(world.Unexpected);

        // The organization row: exactly audit revision +1, the audit digest and the modification time (B-M2f3-3).
        var after = world.Organizations[org];
        var changed = typeof(OrganizationAuthorizationState).GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Where(p => !Equals(p.GetValue(before), p.GetValue(after)) && !(p.GetValue(before) is byte[] a && p.GetValue(after) is byte[] b && a.AsSpan().SequenceEqual(b)))
            .Select(p => p.Name).Order(StringComparer.Ordinal).ToList();
        Assert.Equal(["AuditJournalRevision", "LatestAuditJournalDigest", "StateModifiedOn"], changed);
        Assert.Equal(before.AuditJournalRevision + 1, after.AuditJournalRevision);
        Assert.Equal((before.AuthorizationRevision, before.ContentIntegrityRevision, before.ProjectionAppliedRevision, before.IntegrityQuarantineEnabled),
            (after.AuthorizationRevision, after.ContentIntegrityRevision, after.ProjectionAppliedRevision, after.IntegrityQuarantineEnabled));

        // One audit entry, archive delivery pending, the closed body with the authoritative time and its bucket.
        var entry = Assert.Single(world.Journal);
        Assert.Equal((org, JournalStreamNames.Audit, CapabilityRefusalEvidence.EventKind, after.AuditJournalRevision, ArchiveDeliveryStates.Pending, (Guid?)null, (Guid?)null),
            (entry.OrganizationId, entry.JournalStreamName, entry.JournalEventKind, entry.JournalStreamRevision, entry.ArchiveDeliveryState, entry.SecurityCommandId, entry.ContentChangeOperationId));
        using var document = JsonDocument.Parse(entry.JournalEventBodyJson);
        var body = document.RootElement.GetProperty("body");
        Assert.Equal(CapabilityRefusalEvidenceBody.MemberNames.Order(StringComparer.Ordinal), body.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
        Assert.Equal(world.DatabaseTime, body.GetProperty("evidence_database_time_utc").GetDateTimeOffset());
        Assert.Equal(new DateTimeOffset(2026, 11, 17, 10, 0, 0, TimeSpan.Zero), body.GetProperty("evidence_bucket_start_utc").GetDateTimeOffset());
        Assert.Equal(world.Clock.GetUtcNow(), body.GetProperty("refusal_observed_on_utc").GetDateTimeOffset()); // informational: instance clock
        Assert.Equal(("business-api", "verify", 1, CapabilityRefusalEvidence.RefusalCode),
            (body.GetProperty("workload_name").GetString(), body.GetProperty("entry_point").GetString(), body.GetProperty("client_capability_level").GetInt32(), body.GetProperty("refusal_code").GetString()));
        Assert.Single(world.EvidenceOf(org));
    }

    [Fact]
    public async Task TheCommittedEntryIsDeliveredByTheUnchangedArchiveProcessor()
    {
        var (world, org, recorder, _) = Setup();
        await Refuse(recorder, org);
        Assert.Equal(ArchiveDeliveryStates.Pending, Assert.Single(world.Journal).ArchiveDeliveryState);
        var archive = new InMemorySecurityEventArchive();
        await world.ProvisionArchiveAsync(archive); // WP-5a: sentinel and the archived predecessor (D+ on the audit stream)
        Assert.Equal(1, await new ArchiveProcessor(world.Store, archive, world.Clock, durability: world.DPlus.Durability).ProcessOrganizationAsync(org, Ct));
        Assert.Equal(ArchiveDeliveryStates.Anchored, Assert.Single(world.Journal).ArchiveDeliveryState); // D-3: delivery completed
        Assert.False(world.Organizations[org].IntegrityQuarantineEnabled);
    }

    // ---- deduplication and the bound (M2F3-18, M2F3-27, M2F3-50..52, M2F3-72) ----

    [Fact]
    public async Task ARepeatInTheBucketCostsNoTransaction()
    {
        var (world, org, recorder, _) = Setup();
        await Refuse(recorder, org);
        var scopes = world.Scopes;
        Assert.Equal(CapabilityRefusalEvidenceOutcome.SuppressedRecorded, await Refuse(recorder, org));
        Assert.Equal(scopes, world.Scopes);
        Assert.Single(world.Journal);
    }

    [Fact]
    public async Task ARestartedProcessFindsTheEntryAndAppendsNothing()
    {
        var (world, org, recorder, _) = Setup();
        await Refuse(recorder, org);
        var restarted = new CapabilityRefusalEvidenceRecorder(world.Store, world.Clock);
        Assert.Equal(CapabilityRefusalEvidenceOutcome.AlreadyRecorded, await Refuse(restarted, org));
        Assert.Single(world.Journal);
    }

    [Fact]
    public async Task EvictionOnlyAddsAttemptsNeverEntries()
    {
        var (world, org, recorder, _) = Setup(new CapabilityRefusalEvidenceLimits { PreFilterCapacity = 1 });
        for (var round = 0; round < 3; round++)
        {
            foreach (var entry in CapabilityRefusalEvidence.EntryPoints.All)
            {
                await Refuse(recorder, org, entry);
            }
        }

        Assert.Equal(7, world.EvidenceOf(org).Count);
        Assert.Equal(7, world.EvidenceOf(org).Select(e => e.Key).Distinct().Count());
    }

    [Fact]
    public async Task ConcurrentInstancesNeverExceedOnePerKeyOrSevenPerBucket()
    {
        var world = new CapabilityRefusalEvidenceWorld();
        var orgs = Enumerable.Range(0, 3).Select(_ => world.AddOrganization()).ToList();
        var instances = Enumerable.Range(0, 3).Select(_ => new CapabilityRefusalEvidenceRecorder(world.Store, world.Clock)).ToList();
        var work = from instance in instances
                   from org in orgs
                   from entry in CapabilityRefusalEvidence.EntryPoints.All
                   from repeat in Enumerable.Range(0, 4)
                   select Task.Run(() => instance.RecordAsync(new CapabilityRefusalContext(org, 1), entry, CancellationToken.None), Ct);
        await Task.WhenAll(work);

        foreach (var org in orgs)
        {
            var entries = world.EvidenceOf(org);
            Assert.Equal(7, entries.Count);
            Assert.All(entries.GroupBy(e => (e.Key, e.Bucket)), g => Assert.Single(g));
            Assert.All(entries.GroupBy(e => e.Bucket), g => Assert.True(g.Count() <= 7));
        }
    }

    [Fact]
    public async Task TheNextHourStartsANewBucket()
    {
        var (world, org, recorder, _) = Setup();
        var hour = new DateTimeOffset(2026, 11, 17, 11, 0, 0, TimeSpan.Zero);
        world.DatabaseTime = hour.AddMilliseconds(-1);
        Assert.Equal(CapabilityRefusalEvidenceOutcome.Committed, await Refuse(recorder, org));
        world.DatabaseTime = hour;
        world.Clock.Advance(TimeSpan.FromMilliseconds(1)); // the pre-filter deadline was 1 ms of local monotonic time
        Assert.Equal(CapabilityRefusalEvidenceOutcome.Committed, await Refuse(recorder, org));
        Assert.Equal([hour.AddHours(-1), hour], world.EvidenceOf(org).Select(e => e.Bucket).ToList());
    }

    // ---- inconclusive lookups never append (M2F3-62..M2F3-66, D-7) ----

    [Fact]
    public async Task AnUnusableIndexDefersAndRecordsOnceItIsUsable()
    {
        var (world, org, recorder, seen) = Setup();
        world.IndexUsable = false;
        Assert.Equal(CapabilityRefusalEvidenceOutcome.Deferred, await Refuse(recorder, org));
        Assert.Empty(world.Journal);
        Assert.Contains(seen.All, o => o.Reason == CapabilityRefusalEvidenceReason.IndexUnavailable);
        Assert.Equal(CapabilityRefusalEvidenceOutcome.SuppressedPending, await Refuse(recorder, org));

        world.IndexUsable = true;
        world.Clock.Advance(TimeSpan.FromSeconds(6));
        Assert.Equal(1, await recorder.RetryDueAsync(Ct));
        Assert.Single(world.Journal);
        Assert.Equal(0, recorder.PendingCount);
    }

    [Fact]
    public async Task AClockRegressionDefersUntilTheDatabaseTimeCatchesUp()
    {
        var (world, org, recorder, seen) = Setup();
        world.DatabaseTime = new DateTimeOffset(2026, 11, 17, 10, 40, 0, TimeSpan.Zero);
        await Refuse(recorder, org, CapabilityRefusalEvidence.EntryPoints.Prepare);
        world.DatabaseTime = new DateTimeOffset(2026, 11, 17, 10, 10, 0, TimeSpan.Zero); // failover to a host 30 minutes behind
        Assert.Equal(CapabilityRefusalEvidenceOutcome.Deferred, await Refuse(recorder, org));
        Assert.Contains(seen.All, o => o.Reason == CapabilityRefusalEvidenceReason.ClockRegression);
        Assert.Single(world.Journal);

        world.DatabaseTime = new DateTimeOffset(2026, 11, 17, 10, 41, 0, TimeSpan.Zero);
        world.Clock.Advance(TimeSpan.FromSeconds(6));
        await recorder.RetryDueAsync(Ct);
        Assert.Equal(2, world.EvidenceOf(org).Count);
    }

    [Fact]
    public async Task ALookupThatExceedsTheBudgetAppendsNothing()
    {
        var (world, org, recorder, seen) = Setup(new CapabilityRefusalEvidenceLimits { Budget = TimeSpan.FromMilliseconds(100) });
        world.LookupDelay = TimeSpan.FromSeconds(5);
        Assert.Equal(CapabilityRefusalEvidenceOutcome.Deferred, await Refuse(recorder, org));
        Assert.Contains(seen.All, o => o.Reason == CapabilityRefusalEvidenceReason.Timeout);
        Assert.Empty(world.Journal);
        Assert.Equal(0, world.Commits);
    }

    [Theory]
    [InlineData("SetCapabilityEvidenceLockTimeoutAsync")]
    [InlineData("LockOrganizationAsync")]
    [InlineData("StartCapabilityEvidenceLookupAsync")]
    [InlineData("IsCapabilityRefusalEvidenceIndexUsableAsync")]
    [InlineData("GetNewestCapabilityRefusalEvidenceAsync")]
    public async Task ADatabaseErrorBeforeTheCommitAppendsNothing(string failing)
    {
        var (world, org, recorder, seen) = Setup();
        world.FailOn[failing] = new InvalidOperationException("55P03 lock not available (test)");
        Assert.Equal(CapabilityRefusalEvidenceOutcome.Deferred, await Refuse(recorder, org));
        Assert.Contains(seen.All, o => o.Reason == CapabilityRefusalEvidenceReason.Error);
        Assert.Empty(world.Journal);
        Assert.Equal(40, world.Organizations[org].AuditJournalRevision);
    }

    [Fact]
    public async Task AnUnverifiableNewestEntryIsInconclusiveAndNotRetried()
    {
        var (world, org, recorder, seen) = Setup();
        var seeded = world.SeedEvidence(org, CapabilityRefusalEvidence.EntryPoints.Prepare, world.Clock.GetUtcNow().AddMinutes(-1));
        seeded.JournalEventBodyJson = seeded.JournalEventBodyJson.Replace("prepare", "commit", StringComparison.Ordinal); // digest no longer matches
        Assert.Equal(CapabilityRefusalEvidenceOutcome.Inconclusive, await Refuse(recorder, org));
        Assert.Contains(seen.All, o => o.Reason == CapabilityRefusalEvidenceReason.UnverifiableBody);
        Assert.Single(world.Journal);
        Assert.Equal(0, recorder.PendingCount);
        Assert.Equal(CapabilityRefusalEvidenceOutcome.SuppressedInconclusive, await Refuse(recorder, org));
    }

    [Fact]
    public async Task AnInconsistentBucketIsInconclusive()
    {
        var (world, org, recorder, seen) = Setup();
        world.SeedEvidence(org, CapabilityRefusalEvidence.EntryPoints.Prepare, world.Clock.GetUtcNow().AddMinutes(-2));
        world.SeedEvidence(org, CapabilityRefusalEvidence.EntryPoints.Prepare, world.Clock.GetUtcNow().AddMinutes(-1));
        Assert.Equal(CapabilityRefusalEvidenceOutcome.Inconclusive, await Refuse(recorder, org));
        Assert.Contains(seen.All, o => o.Reason == CapabilityRefusalEvidenceReason.BoundInconsistent);
        Assert.Equal(2, world.Journal.Count);
    }

    // ---- ambiguous commits (M2F3-48..M2F3-50, D-6) ----

    [Fact]
    public async Task AnAmbiguousCommitThatTookEffectIsReconciledWithoutASecondEntry()
    {
        var (world, org, recorder, _) = Setup();
        world.NextCommit = EvidenceCommitBehavior.FailAfterEffect;
        Assert.Equal(CapabilityRefusalEvidenceOutcome.Ambiguous, await Refuse(recorder, org));
        Assert.Single(world.Journal);
        world.Clock.Advance(TimeSpan.FromSeconds(6));
        Assert.Equal(1, await recorder.RetryDueAsync(Ct));
        Assert.Single(world.Journal);
        Assert.Equal(0, recorder.PendingCount);
    }

    [Fact]
    public async Task AnAmbiguousCommitThatDidNotTakeEffectIsRecordedOnceByTheRetry()
    {
        var (world, org, recorder, _) = Setup();
        world.NextCommit = EvidenceCommitBehavior.FailBeforeEffect;
        Assert.Equal(CapabilityRefusalEvidenceOutcome.Ambiguous, await Refuse(recorder, org));
        Assert.Empty(world.Journal);
        Assert.Equal(40, world.Organizations[org].AuditJournalRevision); // the uncommitted append left no trace
        world.Clock.Advance(TimeSpan.FromSeconds(6));
        await recorder.RetryDueAsync(Ct);
        Assert.Single(world.Journal);
    }

    [Fact]
    public async Task ARestartAfterAnAmbiguousCommitStillWritesAtMostOneEntry()
    {
        var (world, org, recorder, _) = Setup();
        world.NextCommit = EvidenceCommitBehavior.FailAfterEffect;
        await Refuse(recorder, org);
        var restarted = new CapabilityRefusalEvidenceRecorder(world.Store, world.Clock); // the retry state is lost with the process
        Assert.Equal(CapabilityRefusalEvidenceOutcome.AlreadyRecorded, await Refuse(restarted, org));
        Assert.Single(world.Journal);
    }

    // ---- bounded in-process controls: attempt limit, retry capacity, abandonment (M2F3-06, M2F3-20, M2F3-37) ----

    [Fact]
    public async Task TheAttemptLimitSuppressesNewKeysUntilTheWindowEndsAndSaysSo()
    {
        var (world, _, recorder, seen) = Setup(new CapabilityRefusalEvidenceLimits { AttemptLimitPerWindow = 3 });
        var orgs = Enumerable.Range(0, 5).Select(_ => world.AddOrganization()).ToList();
        var results = new List<CapabilityRefusalEvidenceOutcome>();
        foreach (var org in orgs)
        {
            results.Add(await Refuse(recorder, org));
        }

        Assert.Equal(3, results.Count(r => r == CapabilityRefusalEvidenceOutcome.Committed));
        Assert.Equal(2, results.Count(r => r == CapabilityRefusalEvidenceOutcome.QuotaSuppressed));
        Assert.Equal(2, seen.All.Count(o => o.Reason == CapabilityRefusalEvidenceReason.AttemptLimit));
        Assert.Equal(3, world.Journal.Count);
        Assert.Equal(3, recorder.AttemptsInWindow);

        // Liveness cost: the suppressed first refusals stay unrecorded in this window (one signal each, not one per request).
        Assert.Equal(CapabilityRefusalEvidenceOutcome.SuppressedInconclusive, await Refuse(recorder, orgs[3]));
        Assert.Equal(2, seen.All.Count(o => o.Reason == CapabilityRefusalEvidenceReason.AttemptLimit));

        world.Clock.Advance(TimeSpan.FromHours(1));
        Assert.Equal(CapabilityRefusalEvidenceOutcome.Committed, await Refuse(recorder, orgs[3]));
        Assert.Equal(4, world.Journal.Count);
    }

    [Fact]
    public async Task ABurstOfOneKeyCostsOneAttemptSoOtherKeysStayRecordable()
    {
        var (world, org, recorder, _) = Setup(new CapabilityRefusalEvidenceLimits { AttemptLimitPerWindow = 2 });
        var other = world.AddOrganization();
        world.LookupDelay = TimeSpan.FromMilliseconds(300); // the first T2 is still running while the burst arrives
        var burst = await Task.WhenAll(Enumerable.Range(0, 30).Select(_ => Task.Run(() => Refuse(recorder, org), Ct)));

        Assert.Equal(1, burst.Count(o => o == CapabilityRefusalEvidenceOutcome.Committed));
        Assert.All(burst.Where(o => o != CapabilityRefusalEvidenceOutcome.Committed), o => Assert.Contains(o, new[] { CapabilityRefusalEvidenceOutcome.SuppressedInFlight, CapabilityRefusalEvidenceOutcome.SuppressedRecorded }));
        Assert.Equal(1, recorder.AttemptsInWindow);
        world.LookupDelay = TimeSpan.Zero;
        Assert.Equal(CapabilityRefusalEvidenceOutcome.Committed, await Refuse(recorder, other)); // the limit was not exhausted by the burst
        Assert.Single(world.EvidenceOf(org));
    }

    [Fact]
    public async Task RetriesAreBoundedAndAbandonedAfterAnHourWithASignal()
    {
        var (world, org, recorder, seen) = Setup(new CapabilityRefusalEvidenceLimits { RetryCapacity = 1 });
        var other = world.AddOrganization();
        world.IndexUsable = false;
        Assert.Equal(CapabilityRefusalEvidenceOutcome.Deferred, await Refuse(recorder, org));
        Assert.Equal(CapabilityRefusalEvidenceOutcome.Abandoned, await Refuse(recorder, other)); // retry capacity
        Assert.Contains(seen.All, o => o.Org == other && o.Reason == CapabilityRefusalEvidenceReason.RetryCapacity);

        for (var minute = 0; minute < 70; minute++)
        {
            world.Clock.Advance(TimeSpan.FromMinutes(1));
            await recorder.RetryDueAsync(Ct);
        }

        Assert.Equal(0, recorder.PendingCount);
        Assert.Contains(seen.All, o => o.Org == org && o.Outcome == CapabilityRefusalEvidenceOutcome.Abandoned && o.Reason == CapabilityRefusalEvidenceReason.Abandoned);
        Assert.Empty(world.Journal);
    }

    // ---- isolation, keys, independence from the request ----

    [Fact]
    public async Task OrganizationsAreIsolated()
    {
        var (world, org, recorder, _) = Setup();
        var other = world.AddOrganization();
        await Refuse(recorder, org);
        Assert.Equal(CapabilityRefusalEvidenceOutcome.Committed, await Refuse(recorder, other));
        Assert.Single(world.EvidenceOf(org));
        Assert.Single(world.EvidenceOf(other));
        Assert.Equal(41, world.Organizations[other].AuditJournalRevision);
    }

    [Fact]
    public async Task AnUnapprovedEntryPointIsNeverAttempted()
    {
        var (world, org, recorder, _) = Setup();
        Assert.Equal(CapabilityRefusalEvidenceOutcome.NotAttempted, await recorder.RecordAsync(new CapabilityRefusalContext(org, 1), "history", CancellationToken.None));
        Assert.Equal(0, world.Scopes);
    }

    [Fact]
    public async Task ACancelledTokenSkipsEvidenceSoTheFilterMustNotPassTheRequestToken()
    {
        // T2-5: the recorder honours its token; the filter therefore passes the host lifetime, not the request abort token (architecture test).
        var (world, org, recorder, _) = Setup();
        using var aborted = new CancellationTokenSource();
        await aborted.CancelAsync();
        Assert.Equal(CapabilityRefusalEvidenceOutcome.Deferred, await recorder.RecordAsync(new CapabilityRefusalContext(org, 1), "verify", aborted.Token));
        Assert.Empty(world.Journal);
    }

    [Fact]
    public async Task TheRecorderNeverTouchesTheLedgerOrAnythingElse()
    {
        var (world, org, recorder, _) = Setup();
        world.SeedEvidence(org, CapabilityRefusalEvidence.EntryPoints.Commit, world.Clock.GetUtcNow().AddHours(-2));
        foreach (var entry in CapabilityRefusalEvidence.EntryPoints.All)
        {
            await Refuse(recorder, org, entry);
        }

        Assert.Empty(world.Unexpected);
        Assert.DoesNotContain(world.Events, e => e.StartsWith("OrganizationHasDataKeyBindingsAsync", StringComparison.Ordinal) || e.StartsWith("RequireContentAcceptanceFloorAsync", StringComparison.Ordinal));
        Assert.Equal(8, world.EvidenceOf(org).Count);
    }

    [Fact]
    public async Task UnrelatedAuditHistoryDoesNotAffectTheDecision()
    {
        var (world, org, recorder, _) = Setup();
        for (var i = 0; i < 2000; i++)
        {
            world.Seed(org, "FieldSearchExecuted", new { outcome = "COMPLETED", index = i });
        }

        Assert.Equal(CapabilityRefusalEvidenceOutcome.Committed, await Refuse(recorder, org));
        Assert.Single(world.EvidenceOf(org));
    }
}
