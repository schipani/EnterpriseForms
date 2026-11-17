using System.Collections.Concurrent;
using System.Text.Json;
using AuthorizationAuthority.Application.Common.Ports;
using AuthorizationAuthority.Application.Features.Content;
using AuthorizationAuthority.Application.Features.Journal;
using AuthorizationAuthority.Domain.Features.Journal;
using AuthorizationAuthority.Infrastructure.Archive;
using Xunit;

namespace AuthorizationAuthority.Tests;

/// <summary>
/// FU-M2f3-01, closure test M2F3-25 (docs/security/E3_M2F3_CONTRACT_CLOSURE.md §10.2): "No summary event is ever written". Governing
/// decisions: PD-07 (suppressed-repeat accounting is <b>metrics only</b>; summary events deferred), §9.1 ("No summary event"), §3.3 (the
/// only permitted T2 write is one <c>EnvelopeCapabilityRefused</c> entry) and §12.3 (summary events out of scope).
/// <para>
/// The recorder is driven through <b>every</b> outcome it can report (committed, already recorded, the four suppressions, attempt limit,
/// deferred, both ambiguous commits, inconclusive, both abandonments, unapproved key), through retries, the attempt-window rollover,
/// an authoritative-hour rollover and archive delivery. After every step the test diffs the durable journal and the organization rows of
/// the transactional AuthorityDb double: a step may add at most one entry, and only an <c>EnvelopeCapabilityRefused</c> entry with the
/// closed §9.2 body for the refused key in the current bucket; every outcome that is not a commit adds nothing and leaves every audit
/// revision unchanged. Suppressed refusals reach only the observer (the metrics port). Seeded test fixtures are excluded from the diff.
/// </para>
/// </summary>
public sealed class CapabilityRefusalEvidenceNoSummaryTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly CapabilityRefusalEvidenceOutcome[] MayCommit = [CapabilityRefusalEvidenceOutcome.Committed, CapabilityRefusalEvidenceOutcome.Ambiguous];

    private sealed class ObservedOutcomes : ICapabilityRefusalEvidenceObserver
    {
        public ConcurrentQueue<(Guid Org, string Entry, CapabilityRefusalEvidenceOutcome Outcome, CapabilityRefusalEvidenceReason Reason)> All { get; } = new();

        public void Observed(Guid organizationId, string entryPoint, CapabilityRefusalEvidenceOutcome outcome, CapabilityRefusalEvidenceReason reason) => All.Enqueue((organizationId, entryPoint, outcome, reason));
    }

    /// <summary>One recorder action and what it did to the durable state.</summary>
    private sealed record StepRecord(string Label, IReadOnlyList<CapabilityRefusalEvidenceOutcome> Outcomes, IReadOnlyList<SecurityJournalEntry> Added);

    /// <summary>
    /// Runs recorder actions one step at a time and diffs the journal and every organization row around each step. The outcomes of a
    /// step are taken from the observer (the recorder reports every outcome, including those of retries, exactly once).
    /// </summary>
    private sealed class Ledger(CapabilityRefusalEvidenceWorld world, ObservedOutcomes observed)
    {
        public List<StepRecord> Steps { get; } = [];

        public async Task<CapabilityRefusalEvidenceOutcome> Step(string label, Guid organization, string entryPoint, Func<Task<CapabilityRefusalEvidenceOutcome>> action)
        {
            CapabilityRefusalEvidenceOutcome result = default;
            await Run(label, async () => result = await action(), (organization, entryPoint));
            return result;
        }

        /// <summary>
        /// One step that may run several recorder calls (a concurrent burst, a retry pass, archive delivery). <paramref name="refused"/>
        /// names the only key that may receive an entry in this step; null means that no entry may be added at all.
        /// </summary>
        public async Task<IReadOnlyList<CapabilityRefusalEvidenceOutcome>> Run(string label, Func<Task> action, (Guid Organization, string EntryPoint)? refused)
        {
            HashSet<Guid> before;
            Dictionary<Guid, (long Audit, long Authorization, long Content, long Projection, bool Quarantined)> rowsBefore;
            lock (world.Journal)
            {
                before = world.Journal.Select(e => e.SecurityJournalEntryId).ToHashSet();
                rowsBefore = world.Organizations.ToDictionary(o => o.Key, o => (o.Value.AuditJournalRevision, o.Value.AuthorizationRevision, o.Value.ContentIntegrityRevision, o.Value.ProjectionAppliedRevision, o.Value.IntegrityQuarantineEnabled));
            }

            var reported = observed.All.Count;
            await action();
            var outcomes = observed.All.Skip(reported).Select(o => o.Outcome).ToList();
            List<SecurityJournalEntry> added;
            lock (world.Journal)
            {
                added = world.Journal.Where(e => !before.Contains(e.SecurityJournalEntryId)).ToList();
            }

            // Only an outcome that requested a commit can have written anything, and at most one entry per step.
            var committing = outcomes.Count(o => MayCommit.Contains(o));
            Assert.True(added.Count <= Math.Min(1, committing), $"{label}: {added.Count} entries added by outcomes [{string.Join(", ", outcomes)}]");

            foreach (var entry in added)
            {
                AssertEvidenceOnly(label, entry, refused);
            }

            // Organization rows: only the audit stream of the refused organization may move, by exactly the entries added.
            foreach (var (organization, row) in rowsBefore)
            {
                var now = world.Organizations[organization];
                var expectedAudit = row.Audit + added.Count(e => e.OrganizationId == organization);
                Assert.Equal((expectedAudit, row.Authorization, row.Content, row.Projection, row.Quarantined),
                    (now.AuditJournalRevision, now.AuthorizationRevision, now.ContentIntegrityRevision, now.ProjectionAppliedRevision, now.IntegrityQuarantineEnabled));
            }

            Steps.Add(new StepRecord(label, outcomes, added));
            return outcomes;
        }

        private void AssertEvidenceOnly(string label, SecurityJournalEntry entry, (Guid Organization, string EntryPoint)? refused)
        {
            Assert.True(refused is not null, label + ": an entry was written by a step that refused nothing");
            Assert.Equal((JournalStreamNames.Audit, CapabilityRefusalEvidence.EventKind), (entry.JournalStreamName, entry.JournalEventKind));
            var verified = CapabilityRefusalEvidenceLookup.Verify(new CapabilityRefusalEvidenceRow(entry.SecurityJournalEntryId, entry.JournalStreamRevision, entry.JournalEventBodyJson, entry.JournalEventDigest), entry.OrganizationId);
            Assert.NotNull(verified);
            Assert.Equal(CapabilityRefusalEvidenceKey.For(refused!.Value.Organization, refused.Value.EntryPoint), verified.Key);
            Assert.Equal(CapabilityRefusalEvidence.BucketOf(world.DatabaseTime ?? world.Clock.GetUtcNow()), verified.Bucket);

            // The closed body: exactly the §9.2 members, so no count, total, window or other aggregate can travel in an entry either.
            using var document = JsonDocument.Parse(entry.JournalEventBodyJson);
            Assert.Equal(CapabilityRefusalEvidenceBody.MemberNames.Order(StringComparer.Ordinal), document.RootElement.GetProperty("body").EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
        }
    }

    private static Task<CapabilityRefusalEvidenceOutcome> Refuse(CapabilityRefusalEvidenceRecorder recorder, Guid org, string entry) =>
        recorder.RecordAsync(new CapabilityRefusalContext(org, 1), entry, CancellationToken.None);

    [Fact]
    public async Task NoOutcomeRetryWindowOrHourRolloverEverWritesAnythingButOneEvidenceEntryPerKeyAndBucket()
    {
        const string Prepare = CapabilityRefusalEvidence.EntryPoints.Prepare;
        const string Commit = CapabilityRefusalEvidence.EntryPoints.Commit;
        const string Verify = CapabilityRefusalEvidence.EntryPoints.Verify;
        const string Census = CapabilityRefusalEvidence.EntryPoints.ProtectionCensus;
        const string Backfill = CapabilityRefusalEvidence.EntryPoints.IndexBackfill;
        const string DeepVerify = CapabilityRefusalEvidence.EntryPoints.IndexDeepVerify;
        const string Batch = CapabilityRefusalEvidence.EntryPoints.VerifyBatch;

        var world = new CapabilityRefusalEvidenceWorld();
        var observed = new ObservedOutcomes();
        var ledger = new Ledger(world, observed);
        var org = world.AddOrganization();
        var recorder = new CapabilityRefusalEvidenceRecorder(world.Store, world.Clock, observed);

        // 1. A first refusal commits one entry; 50 repeats are suppressed by the pre-filter.
        Assert.Equal(CapabilityRefusalEvidenceOutcome.Committed, await ledger.Step("first", org, Verify, () => Refuse(recorder, org, Verify)));
        for (var i = 0; i < 50; i++)
        {
            Assert.Equal(CapabilityRefusalEvidenceOutcome.SuppressedRecorded, await ledger.Step("repeat " + i, org, Verify, () => Refuse(recorder, org, Verify)));
        }

        // 2. Another instance (no in-process state) finds the durable entry.
        var other = new CapabilityRefusalEvidenceRecorder(world.Store, world.Clock, observed);
        Assert.Equal(CapabilityRefusalEvidenceOutcome.AlreadyRecorded, await ledger.Step("other instance", org, Verify, () => Refuse(other, org, Verify)));

        // 3. A concurrent burst of one key: one T2, the rest suppressed in flight (or recorded); one entry for the whole burst.
        world.LookupDelay = TimeSpan.FromMilliseconds(300);
        var burst = await ledger.Run("burst", () => Task.WhenAll(Enumerable.Range(0, 30).Select(_ => Task.Run(() => Refuse(recorder, org, Prepare), Ct))), (org, Prepare));
        world.LookupDelay = TimeSpan.Zero;
        Assert.Equal(1, burst.Count(o => o == CapabilityRefusalEvidenceOutcome.Committed));
        Assert.Contains(CapabilityRefusalEvidenceOutcome.SuppressedInFlight, burst);

        // 4. Deferred (index unusable), pending repeats, a retry pass that still defers, then the retry that records.
        world.IndexUsable = false;
        Assert.Equal(CapabilityRefusalEvidenceOutcome.Deferred, await ledger.Step("deferred", org, Commit, () => Refuse(recorder, org, Commit)));
        for (var i = 0; i < 20; i++)
        {
            Assert.Equal(CapabilityRefusalEvidenceOutcome.SuppressedPending, await ledger.Step("pending " + i, org, Commit, () => Refuse(recorder, org, Commit)));
        }

        world.Clock.Advance(TimeSpan.FromSeconds(6));
        Assert.Equal([CapabilityRefusalEvidenceOutcome.Deferred], await ledger.Run("retry while unusable", () => recorder.RetryDueAsync(Ct), null));
        world.IndexUsable = true;
        world.Clock.Advance(TimeSpan.FromSeconds(15));
        Assert.Equal([CapabilityRefusalEvidenceOutcome.Committed], await ledger.Run("retry records", () => recorder.RetryDueAsync(Ct), (org, Commit)));

        // 5. Ambiguous commit that did not take effect: nothing; its retry records once.
        world.NextCommit = EvidenceCommitBehavior.FailBeforeEffect;
        Assert.Equal(CapabilityRefusalEvidenceOutcome.Ambiguous, await ledger.Step("ambiguous, not committed", org, Census, () => Refuse(recorder, org, Census)));
        world.Clock.Advance(TimeSpan.FromSeconds(6));
        Assert.Equal([CapabilityRefusalEvidenceOutcome.Committed], await ledger.Run("ambiguous retry", () => recorder.RetryDueAsync(Ct), (org, Census)));

        // 6. Ambiguous commit that did take effect: one entry; the retry reconciles without a second one.
        world.NextCommit = EvidenceCommitBehavior.FailAfterEffect;
        Assert.Equal(CapabilityRefusalEvidenceOutcome.Ambiguous, await ledger.Step("ambiguous, committed", org, Backfill, () => Refuse(recorder, org, Backfill)));
        world.Clock.Advance(TimeSpan.FromSeconds(6));
        Assert.Equal([CapabilityRefusalEvidenceOutcome.AlreadyRecorded], await ledger.Run("ambiguous reconcile", () => recorder.RetryDueAsync(Ct), null));

        // 7. Inconclusive (an unverifiable fixture among the newest eight of another organization), then its negative cache.
        var damaged = world.AddOrganization();
        var fixture = world.SeedEvidence(damaged, Prepare, world.Clock.GetUtcNow().AddMinutes(-1));
        fixture.JournalEventBodyJson = fixture.JournalEventBodyJson.Replace("prepare", "commit", StringComparison.Ordinal);
        Assert.Equal(CapabilityRefusalEvidenceOutcome.Inconclusive, await ledger.Step("inconclusive", damaged, Verify, () => Refuse(recorder, damaged, Verify)));
        for (var i = 0; i < 10; i++)
        {
            Assert.Equal(CapabilityRefusalEvidenceOutcome.SuppressedInconclusive, await ledger.Step("negative cache " + i, damaged, Verify, () => Refuse(recorder, damaged, Verify)));
        }

        // 8. The attempt limit: two of four organizations recorded, two quota-suppressed (one signal each), repeats negatively cached.
        var limited = new CapabilityRefusalEvidenceRecorder(world.Store, world.Clock, observed, new CapabilityRefusalEvidenceLimits { AttemptLimitPerWindow = 2 });
        var crowd = Enumerable.Range(0, 4).Select(_ => world.AddOrganization()).ToList();
        var quota = new List<CapabilityRefusalEvidenceOutcome>();
        foreach (var member in crowd)
        {
            quota.Add(await ledger.Step("quota", member, DeepVerify, () => Refuse(limited, member, DeepVerify)));
            Assert.NotEqual(CapabilityRefusalEvidenceOutcome.Committed, await ledger.Step("quota repeat", member, DeepVerify, () => Refuse(limited, member, DeepVerify)));
        }

        Assert.Equal([CapabilityRefusalEvidenceOutcome.Committed, CapabilityRefusalEvidenceOutcome.Committed, CapabilityRefusalEvidenceOutcome.QuotaSuppressed, CapabilityRefusalEvidenceOutcome.QuotaSuppressed], quota);

        // 9. Abandonment: retry capacity reached, then age; nothing is written when a refusal is given up.
        var bounded = new CapabilityRefusalEvidenceRecorder(world.Store, world.Clock, observed, new CapabilityRefusalEvidenceLimits { RetryCapacity = 1 });
        var first = world.AddOrganization();
        var second = world.AddOrganization();
        world.IndexUsable = false;
        Assert.Equal(CapabilityRefusalEvidenceOutcome.Deferred, await ledger.Step("capacity: deferred", first, Batch, () => Refuse(bounded, first, Batch)));
        Assert.Equal(CapabilityRefusalEvidenceOutcome.Abandoned, await ledger.Step("capacity: abandoned", second, Batch, () => Refuse(bounded, second, Batch)));
        for (var minute = 0; minute < 70; minute++)
        {
            world.Clock.Advance(TimeSpan.FromMinutes(1));
            await ledger.Run("age " + minute, async () => { await bounded.RetryDueAsync(Ct); await recorder.RetryDueAsync(Ct); await limited.RetryDueAsync(Ct); }, null);
        }

        Assert.Contains(observed.All, o => o.Org == first && o.Outcome == CapabilityRefusalEvidenceOutcome.Abandoned && o.Reason == CapabilityRefusalEvidenceReason.Abandoned);
        world.IndexUsable = true;

        // 10. An unapproved key is never attempted.
        Assert.Equal(CapabilityRefusalEvidenceOutcome.NotAttempted, await ledger.Step("unapproved", org, "history", () => recorder.RecordAsync(new CapabilityRefusalContext(org, 1), "history", CancellationToken.None)));

        // 11. Seventy minutes have passed: a new authoritative hour and a new attempt window. Every key of the first organization is
        //     refused again many times on three instances: each key gets exactly one new entry in the new bucket, and nothing marks
        //     the end of the previous hour or window (no roll-up of what was suppressed there).
        var instances = new[] { recorder, other, new CapabilityRefusalEvidenceRecorder(world.Store, world.Clock, observed) };
        foreach (var entry in CapabilityRefusalEvidence.EntryPoints.All)
        {
            for (var round = 0; round < 5; round++)
            {
                foreach (var instance in instances)
                {
                    await ledger.Step("next hour " + entry, org, entry, () => Refuse(instance, org, entry));
                }
            }
        }

        // 12. Archive delivery appends nothing to the journal either.
        Assert.Empty(await ledger.Run("archive", () => new ArchiveProcessor(world.Store, new InMemorySecurityEventArchive(), world.Clock).ProcessOrganizationAsync(org, Ct), null));

        // ---- the whole history ----
        var written = ledger.Steps.SelectMany(s => s.Added).ToList();
        Assert.All(written, e => Assert.Equal(CapabilityRefusalEvidence.EventKind, e.JournalEventKind));
        var keys = written.Select(e => CapabilityRefusalEvidenceLookup.Verify(new CapabilityRefusalEvidenceRow(e.SecurityJournalEntryId, e.JournalStreamRevision, e.JournalEventBodyJson, e.JournalEventDigest), e.OrganizationId)!)
            .Select(v => (v.Key, v.Bucket)).ToList();
        Assert.Equal(keys.Count, keys.Distinct().Count()); // at most one entry per key and bucket, over the whole run

        // Exactly the expected entries: first hour 5 keys of org (verify, prepare, commit, census, backfill) + 2 quota winners; new hour 7.
        var firstHour = CapabilityRefusalEvidence.BucketOf(new DateTimeOffset(2026, 11, 17, 10, 15, 0, TimeSpan.Zero));
        Assert.Equal(7, keys.Count(k => k.Bucket == firstHour));
        Assert.Equal(7, keys.Count(k => k.Bucket == firstHour.AddHours(1) && k.Key.OrganizationId == org));
        Assert.Equal(14, written.Count);

        // The journal holds nothing else (the only other entry is the seeded unverifiable fixture).
        lock (world.Journal)
        {
            Assert.Equal(written.Count + 1, world.Journal.Count);
            Assert.Equal([CapabilityRefusalEvidence.EventKind], world.Journal.Select(e => e.JournalEventKind).Distinct());
        }

        // PD-07: the suppressed repeats were accounted for, but only on the metrics port.
        var suppressed = observed.All.Count(o => o.Outcome is CapabilityRefusalEvidenceOutcome.SuppressedRecorded or CapabilityRefusalEvidenceOutcome.SuppressedPending
            or CapabilityRefusalEvidenceOutcome.SuppressedInFlight or CapabilityRefusalEvidenceOutcome.SuppressedInconclusive or CapabilityRefusalEvidenceOutcome.QuotaSuppressed);
        Assert.True(suppressed >= 50 + 20 + 10 + 2 + 29, "suppressed refusals observed: " + suppressed);
        Assert.Empty(world.Unexpected);
    }
}
