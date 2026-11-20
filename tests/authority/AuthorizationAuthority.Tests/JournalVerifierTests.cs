using System.Text;
using AuthorizationAuthority.Application.Common;
using AuthorizationAuthority.Application.Features.Journal;
using AuthorizationAuthority.Application.Features.Journal.Reconciliation;
using AuthorizationAuthority.Application.Features.Journal.Verification;
using AuthorizationAuthority.Domain.Features.Journal;
using AuthorizationAuthority.Infrastructure.Archive;
using Xunit;

namespace AuthorizationAuthority.Tests;

/// <summary>
/// SEC-BL-01 WP-4 (C-155, contract §8, §9): incremental and full sweeps over the three streams. Detection of modification, deletion,
/// insertion, gaps, chain divergence, fake receipts, archive regression and discontinuity, database rollback, delivery lag and staleness;
/// checkpoint continuity and its rollback limit (CKR-3); isolation; non-overlap; cancellation and restart; alert-only behaviour.
/// </summary>
public sealed class JournalVerifierTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string Audit = JournalStreamNames.Audit;

    /// <summary>A baselined stream: <paramref name="rows"/> delivered entries and checkpoint 1 at the archive head.</summary>
    private static async Task<(JournalVerificationWorld World, Guid Org)> BaselinedAsync(int rows = 5, string stream = Audit)
    {
        var world = new JournalVerificationWorld();
        var org = world.AddOrganization();
        world.Append(org, stream, rows);
        await world.DeliverAsync(org, stream);
        var report = await world.Baseline().RunAsync(JournalVerificationWorld.BaselineRequest(org, stream), Ct);
        Assert.Equal(nameof(JournalBaselineOutcome.Clean), report.Outcome);
        return (world, org);
    }

    private static Task<JournalVerificationRun> SweepAsync(JournalVerifier verifier, Guid org, JournalSweepKind kind = JournalSweepKind.Incremental, string stream = Audit) =>
        verifier.SweepAsync(org, stream, kind, Ct);

    private static void AssertFinding(JournalVerificationRun run, string code, long? first = null, string? subtype = null)
    {
        Assert.Equal(JournalSweepOutcome.Findings, run.Outcome);
        Assert.Contains(run.Findings, f => f.FindingCode == code && (first is null || f.FirstRevision == first) && (subtype is null || f.Subtype == subtype));
        Assert.StartsWith("NotCreated", run.CheckpointAction, StringComparison.Ordinal);
    }

    // ---- Baseline prerequisite and clean progress ----

    [Fact]
    public async Task AStreamWithoutABaselineIsInconclusiveNeverVerified()
    {
        var world = new JournalVerificationWorld();
        var org = world.AddOrganization();
        world.Append(org, Audit, 3);
        await world.DeliverAsync(org, Audit);
        var run = await SweepAsync(world.Verifier(), org);
        Assert.Equal(JournalSweepOutcome.Inconclusive, run.Outcome);
        Assert.Contains(JournalInconclusiveReasons.NotBaselined, run.InconclusiveReasons);
    }

    [Fact]
    public async Task ACleanIncrementalSweepAdvancesTheCheckpointChainToTheArchivedHead()
    {
        var (world, org) = await BaselinedAsync();
        world.Append(org, Audit, 4);
        await world.DeliverAsync(org, Audit);
        var before = world.Fingerprint();
        var run = await SweepAsync(world.Verifier(), org);

        Assert.Equal(JournalSweepOutcome.Clean, run.Outcome);
        Assert.Equal((1L, 5L), (run.StartCheckpoint!.CheckpointSequence, run.StartCheckpoint.VerifiedRevision));
        Assert.Equal((2L, 9L), (run.NewCheckpoint!.CheckpointSequence, run.NewCheckpoint.VerifiedRevision));
        Assert.Equal((5L, 4L), (run.Metrics.DatabaseRowsInspected, run.Metrics.ArchiveObjectsRead - 1)); // rows 5..9; archive 6..9 plus the anchor at 5
        var head = (await JournalVerificationCheckpoints.DiscoverAsync(world.Checkpoints, org, Audit, CheckpointDiscoveryMode.FullChain, 0, 10, Ct)).Head!;
        Assert.Equal(CheckpointWalkKind.Incremental, head.WalkKind);
        Assert.Equal(CanonicalJson.Digest(world.Row(org, Audit, 9).JournalEventBodyJson), head.VerifiedDigest);
        Assert.Equal(before, world.Fingerprint());
        Assert.Empty(world.Alerts.Raised);
    }

    [Fact]
    public async Task UndeliveredRowsDoNotAdvanceTheCheckpointBeyondTheArchiveAndAreNotYetLag()
    {
        var (world, org) = await BaselinedAsync();
        world.Append(org, Audit, 2);
        var run = await SweepAsync(world.Verifier(), org);
        Assert.Equal(JournalSweepOutcome.Clean, run.Outcome);
        Assert.Equal(5L, run.NewCheckpoint!.VerifiedRevision);
    }

    [Fact]
    public async Task AFullSweepRunsOnAllThreeStreams()
    {
        var world = new JournalVerificationWorld();
        var org = world.AddOrganization();
        foreach (var stream in ArchiveCompleteness.KnownStreams)
        {
            world.Append(org, stream, 3);
            await world.DeliverAsync(org, stream);
            Assert.Equal(nameof(JournalBaselineOutcome.Clean), (await world.Baseline().RunAsync(JournalVerificationWorld.BaselineRequest(org, stream), Ct)).Outcome);
        }

        var verifier = world.Verifier();
        foreach (var stream in ArchiveCompleteness.KnownStreams)
        {
            var run = await SweepAsync(verifier, org, JournalSweepKind.Full, stream);
            Assert.Equal(JournalSweepOutcome.Clean, run.Outcome);
            Assert.Equal(3L, run.Metrics.DatabaseRowsInspected);
        }
    }

    // ---- Detection above the checkpoint ----

    [Fact]
    public async Task AModifiedDeliveredRowIsAnAnchorMismatchAndADeletedRowIsAGap()
    {
        var (world, org) = await BaselinedAsync();
        world.Append(org, Audit, 3);
        await world.DeliverAsync(org, Audit);
        var row = world.Row(org, Audit, 7);
        row.JournalEventBodyJson = row.JournalEventBodyJson.Replace("entry 7", "entry Q", StringComparison.Ordinal);
        row.JournalEventDigest = CanonicalJson.Digest(row.JournalEventBodyJson); // recomputed digest
        var modified = await SweepAsync(world.Verifier(), org);
        AssertFinding(modified, JournalFindingCodes.AnchorMismatch, 7);
        AssertFinding(modified, JournalFindingCodes.ChainIntegrityFailure, 8, JournalFailureCodes.LinkMismatch);

        var (deleted, deletedOrg) = await BaselinedAsync();
        deleted.Append(deletedOrg, Audit, 3);
        await deleted.DeliverAsync(deletedOrg, Audit);
        deleted.Rows.Remove(deleted.Row(deletedOrg, Audit, 7));
        AssertFinding(await SweepAsync(deleted.Verifier(), deletedOrg), JournalFindingCodes.ChainIntegrityFailure, 7, JournalFailureCodes.RevisionGap);
    }

    [Fact]
    public async Task AForgedAnchoredRowIsAFakeReceipt()
    {
        var (world, org) = await BaselinedAsync();
        var forged = world.Append(org, Audit, 1)[0];
        forged.ArchiveDeliveryState = ArchiveDeliveryStates.Anchored;
        forged.ArchiveReceiptReference = "forged";
        AssertFinding(await SweepAsync(world.Verifier(), org), JournalFindingCodes.FakeReceipt, 6);
    }

    [Fact]
    public async Task ADeletedArchiveObjectIsADiscontinuityAndADeletedAnchorIsARegression()
    {
        var (world, org) = await BaselinedAsync();
        world.Append(org, Audit, 3);
        await world.DeliverAsync(org, Audit);
        world.Archive.RemoveObjectForTesting(JournalVerificationWorld.ArchiveKey(org, Audit, 7));
        AssertFinding(await SweepAsync(world.Verifier(), org), JournalFindingCodes.ArchiveDiscontinuity, 7);

        var (anchor, anchorOrg) = await BaselinedAsync();
        anchor.Archive.RemoveObjectForTesting(JournalVerificationWorld.ArchiveKey(anchorOrg, Audit, 5));
        AssertFinding(await SweepAsync(anchor.Verifier(), anchorOrg), JournalFindingCodes.ArchiveRegression, 5);
    }

    /// <summary>
    /// WP-7A (A-9 VF-15, OI-DR-7): the full sweep re-reads and byte-compares every archived object, including those at or below the trusted
    /// checkpoint; the incremental sweep still lists and reads only above it (IV-1/IV-5 unchanged), so a historical replacement is found by
    /// the next full sweep without a re-baseline.
    /// </summary>
    [Theory]
    [InlineData(Audit)]
    [InlineData(JournalStreamNames.Authorization)]
    [InlineData(JournalStreamNames.Content)]
    public async Task AHistoricalArchivedObjectReplacedBelowTheCheckpointIsFoundByTheFullSweepOnly(string stream)
    {
        var (world, org) = await BaselinedAsync(stream: stream);
        var key = JournalVerificationWorld.ArchiveKey(org, stream, 2);
        var genuine = (await world.Archive.ReadAsync(org, stream, 2, Ct))!;
        world.Archive.PutObjectForTesting(key, [.. genuine.Take(genuine.Length - 1), (byte)' ']);
        var incremental = await SweepAsync(world.Verifier(), org, JournalSweepKind.Incremental, stream);
        Assert.Equal(JournalSweepOutcome.Clean, incremental.Outcome);
        var full = await SweepAsync(world.Verifier(), org, JournalSweepKind.Full, stream);
        AssertFinding(full, JournalFindingCodes.AnchorMismatch, 2);
        Assert.True(full.Metrics.ArchiveObjectsRead >= world.Row(org, stream, 2).JournalStreamRevision + 3, "every archived object is read: " + full.Metrics.ArchiveObjectsRead);

        world.Archive.PutObjectForTesting(key, genuine); // restored: clean again (the verifier never repairs; the store was restored)
        Assert.Equal(JournalSweepOutcome.Clean, (await SweepAsync(world.Verifier(), org, JournalSweepKind.Full, stream)).Outcome);
    }

    [Fact]
    public async Task AWipedArchiveOrSentinelIsAnArchiveRegression()
    {
        var (world, org) = await BaselinedAsync();
        world.Archive.WipeForTesting();
        var run = await SweepAsync(world.Verifier(), org);
        AssertFinding(run, JournalFindingCodes.ArchiveRegression);
        Assert.Contains(run.Findings, f => f.Subtype == JournalInconclusiveReasons.ArchiveIdentityMismatch);
    }

    // ---- Chain rewrites below the checkpoint ----

    [Fact]
    public async Task AConsistentChainRewriteIsDetectedAtTheCheckpointAnchor()
    {
        var (world, org) = await BaselinedAsync();
        // J1 rewrites every delivered row consistently (bodies, digests, links, head): internally valid, only the anchor can tell.
        world.Rows.RemoveAll(r => r.OrganizationId == org);
        world.SetHead(org, Audit, 0, null);
        world.Append(org, Audit, 5, "forged");
        foreach (var row in world.Rows.Where(r => r.OrganizationId == org))
        {
            row.ArchiveDeliveryState = ArchiveDeliveryStates.Anchored;
            row.ArchiveReceiptReference = "copied";
        }

        var run = await SweepAsync(world.Verifier(), org);
        AssertFinding(run, JournalFindingCodes.AnchorMismatch, 5);
    }

    [Fact]
    public async Task ANonRechainedEditBelowTheCheckpointIsMissedIncrementallyAndFoundByTheFullSweep()
    {
        var (world, org) = await BaselinedAsync();
        var row = world.Row(org, Audit, 2);
        row.JournalEventBodyJson = row.JournalEventBodyJson.Replace("entry 2", "entry W", StringComparison.Ordinal);
        row.JournalEventDigest = CanonicalJson.Digest(row.JournalEventBodyJson);
        var verifier = world.Verifier();
        Assert.Equal(JournalSweepOutcome.Clean, (await SweepAsync(verifier, org)).Outcome); // rows below the checkpoint are not read (contract §8.3)
        AssertFinding(await SweepAsync(verifier, org, JournalSweepKind.Full), JournalFindingCodes.ChainIntegrityFailure, 3, JournalFailureCodes.LinkMismatch);
    }

    [Fact]
    public async Task RewriteAndExtendIsDetectedByTheAnchorAndTheArchiveSideLink()
    {
        // W-1: consistent rewrite of the delivered history, then a new entry chained to the forged history is delivered (no D+ yet).
        var (world, org) = await BaselinedAsync();
        world.Rows.RemoveAll(r => r.OrganizationId == org);
        world.SetHead(org, Audit, 0, null);
        world.Append(org, Audit, 5, "forged");
        foreach (var row in world.Rows.Where(r => r.OrganizationId == org))
        {
            row.ArchiveDeliveryState = ArchiveDeliveryStates.Anchored;
        }

        world.Append(org, Audit, 1, "extension");
        await world.DeliverAsync(org, Audit);
        var run = await SweepAsync(world.Verifier(), org);
        AssertFinding(run, JournalFindingCodes.AnchorMismatch, 5);
        AssertFinding(run, JournalFindingCodes.ChainIntegrityFailure, 6, JournalFailureCodes.LinkMismatch);
    }

    // ---- Database restore and tail truncation ----

    [Fact]
    public async Task ADatabaseRolledBackBelowTheCheckpointIsBehindTheArchive()
    {
        var (world, org) = await BaselinedAsync();
        world.Rows.RemoveAll(r => r.OrganizationId == org && r.JournalStreamRevision > 3);
        world.SetHead(org, Audit, 3, world.Row(org, Audit, 3).JournalEventDigest);
        var run = await SweepAsync(world.Verifier(), org);
        AssertFinding(run, JournalFindingCodes.BehindArchive, 4);
        AssertFinding(run, JournalFindingCodes.ChainIntegrityFailure, 4, JournalFailureCodes.RevisionGap);
    }

    [Fact]
    public async Task AnOrganizationHeadThatDisagreesWithTheRowsIsReported()
    {
        var (world, org) = await BaselinedAsync();
        world.SetHead(org, Audit, 9, world.Row(org, Audit, 5).JournalEventDigest);
        AssertFinding(await SweepAsync(world.Verifier(), org), JournalFindingCodes.ChainIntegrityFailure, subtype: JournalFindingCodes.OrganizationHeadMismatch);
    }

    // ---- Outages, lag, staleness ----

    [Fact]
    public async Task ArchiveAndCheckpointStoreOutagesAreInconclusiveWithOperationalFindings()
    {
        var (world, org) = await BaselinedAsync();
        var archiveDown = await SweepAsync(world.Verifier(archive: new UnavailableSecurityEventArchive("outage")), org);
        Assert.Equal(JournalSweepOutcome.Inconclusive, archiveDown.Outcome);
        Assert.Contains(archiveDown.Findings, f => f.FindingCode == JournalFindingCodes.ArchiveUnavailable && f.Severity == JournalFindingSeverity.Operational);

        var storeDown = await SweepAsync(world.Verifier(checkpoints: new ThrowingCheckpointStore()), org);
        Assert.Equal(JournalSweepOutcome.Inconclusive, storeDown.Outcome);
        Assert.Contains(storeDown.Findings, f => f.FindingCode == JournalFindingCodes.CheckpointStoreUnavailable);
        Assert.Empty(world.Alerts.Raised); // operational only
    }

    [Fact]
    public async Task DeliveryLagIsOperationalBlocksTheCheckpointAndBecomesStale()
    {
        var (world, org) = await BaselinedAsync();
        world.Append(org, Audit, 2);
        world.Clock.Advance(TimeSpan.FromMinutes(16));
        var run = await SweepAsync(world.Verifier(), org);
        Assert.Equal(JournalSweepOutcome.Findings, run.Outcome);
        var lag = Assert.Single(run.Findings, f => f.FindingCode == JournalFindingCodes.DeliveryLag);
        Assert.Equal((JournalFindingSeverity.Operational, 6L, 7L), (lag.Severity, lag.FirstRevision, lag.LastRevision));
        Assert.Contains(run.Findings, f => f.FindingCode == JournalFindingCodes.VerificationStale);
        Assert.Empty(world.Alerts.Raised);
    }

    // ---- Checkpoint continuity and rollback limits ----

    [Fact]
    public async Task ACheckpointRollbackIsDetectedWithinTheProcessButNotAfterARestart()
    {
        var (world, org) = await BaselinedAsync();
        var verifier = world.Verifier();
        world.Append(org, Audit, 1);
        await world.DeliverAsync(org, Audit);
        Assert.Equal(JournalSweepOutcome.Clean, (await SweepAsync(verifier, org)).Outcome); // head now sequence 2
        world.Checkpoints.RemoveObjectForTesting(InMemoryVerificationCheckpointStore.KeyForTesting(org, Audit, 2));

        AssertFinding(await SweepAsync(verifier, org), JournalFindingCodes.CheckpointStoreRegression);
        // CKR-3: a restarted verifier has no trusted head and accepts the shorter chain (production needs an independent anchor).
        Assert.Equal(JournalSweepOutcome.Clean, (await SweepAsync(world.Verifier(), org)).Outcome);
    }

    [Fact]
    public async Task ACorruptCheckpointChainIsDetectedByTheFullSweep()
    {
        var (world, org) = await BaselinedAsync();
        var verifier = world.Verifier();
        Assert.Equal(JournalSweepOutcome.Clean, (await SweepAsync(verifier, org)).Outcome);
        Assert.Equal(JournalSweepOutcome.Clean, (await SweepAsync(verifier, org)).Outcome); // sequence 3
        var first = (await world.Checkpoints.ReadAsync(org, Audit, 1, Ct)).Bytes!;
        world.Checkpoints.PutObjectForTesting(InMemoryVerificationCheckpointStore.KeyForTesting(org, Audit, 1),
            Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(first).Replace("verifier-test", "verifier-evil", StringComparison.Ordinal)));
        AssertFinding(await SweepAsync(verifier, org, JournalSweepKind.Full), JournalFindingCodes.CheckpointChainInvalid);
    }

    // ---- Isolation ----

    [Fact]
    public async Task TamperingInOneOrganizationAndStreamDoesNotAffectOthers()
    {
        var world = new JournalVerificationWorld();
        var a = world.AddOrganization();
        var b = world.AddOrganization();
        foreach (var (org, stream) in new[] { (a, Audit), (a, JournalStreamNames.Authorization), (b, Audit) })
        {
            world.Append(org, stream, 4);
            await world.DeliverAsync(org, stream);
            Assert.Equal(nameof(JournalBaselineOutcome.Clean), (await world.Baseline().RunAsync(JournalVerificationWorld.BaselineRequest(org, stream), Ct)).Outcome);
        }

        world.Append(a, Audit, 1);
        await world.DeliverAsync(a, Audit);
        world.Archive.PutObjectForTesting(JournalVerificationWorld.ArchiveKey(a, Audit, 5), "{}"u8.ToArray());
        var verifier = world.Verifier();
        AssertFinding(await SweepAsync(verifier, a), JournalFindingCodes.AnchorMismatch, 5);
        Assert.Equal(JournalSweepOutcome.Clean, (await SweepAsync(verifier, a, stream: JournalStreamNames.Authorization)).Outcome);
        Assert.Equal(JournalSweepOutcome.Clean, (await SweepAsync(verifier, b)).Outcome);
        Assert.All(world.Alerts.Raised, alert => Assert.Equal(a, alert.OrganizationId));
    }

    // ---- Non-overlap, cancellation, restart ----

    [Fact]
    public async Task ASecondSweepOfTheSameStreamIsSkippedWhileOneIsRunningInProcessOrElsewhere()
    {
        var (world, org) = await BaselinedAsync();
        var gate = new TaskCompletionSource();
        var entered = new TaskCompletionSource();
        world.Reader.BeforeRowBatch = async _ =>
        {
            entered.TrySetResult();
            await gate.Task;
        };
        var verifier = world.Verifier();
        var first = SweepAsync(verifier, org);
        await entered.Task;

        Assert.Equal(JournalSweepOutcome.Skipped, (await SweepAsync(verifier, org)).Outcome); // same process
        Assert.Equal(JournalSweepOutcome.Skipped, (await SweepAsync(world.Verifier(), org)).Outcome); // another instance: the shared sweep lock
        gate.SetResult();
        world.Reader.BeforeRowBatch = null;
        Assert.Equal(JournalSweepOutcome.Clean, (await first).Outcome);
        Assert.Equal(JournalSweepOutcome.Clean, (await SweepAsync(verifier, org)).Outcome); // lock released
    }

    [Fact]
    public async Task ASweepCancelledMidwayCreatesNoCheckpointAndARestartedSweepCompletes()
    {
        var (world, org) = await BaselinedAsync();
        world.Append(org, Audit, 3);
        await world.DeliverAsync(org, Audit);
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        world.Reader.BeforeRowBatch = async _ => await cancel.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => world.Verifier().SweepAsync(org, Audit, JournalSweepKind.Incremental, cancel.Token));
        Assert.Single((await world.Checkpoints.ListAsync(org, Audit, null, 10, Ct)).Objects);

        world.Reader.BeforeRowBatch = null;
        var restarted = await SweepAsync(world.Verifier(), org);
        Assert.Equal((JournalSweepOutcome.Clean, 8L), (restarted.Outcome, restarted.NewCheckpoint!.VerifiedRevision));
    }

    // ---- Alert-only ----

    [Fact]
    public async Task FindingsAreRecordedCreateOnlyAndAlertedOnceAndNothingIsQuarantinedOrRepaired()
    {
        var (world, org) = await BaselinedAsync();
        world.Append(org, Audit, 1);
        world.Row(org, Audit, 6).ArchiveDeliveryState = ArchiveDeliveryStates.Anchored;
        var before = world.Fingerprint();
        var verifier = world.Verifier();
        var first = await SweepAsync(verifier, org);
        var second = await SweepAsync(verifier, org);

        var finding = Assert.Single(first.Findings, f => f.FindingCode == JournalFindingCodes.FakeReceipt);
        Assert.Equal((first.RunId, "Findings", 1L), (finding.RunId, finding.SweepOutcome, finding.Checkpoint!.CheckpointSequence));
        Assert.Contains("row:" + world.Row(org, Audit, 6).SecurityJournalEntryId.ToString("D"), finding.EvidenceReferences);
        Assert.DoesNotContain("envelope_format", Encoding.UTF8.GetString(finding.ToCanonicalBytes()), StringComparison.Ordinal); // no body in the record
        Assert.Single(world.Findings.Records); // re-observation does not create a second record
        Assert.Single(world.Alerts.Raised); // alerted once
        Assert.NotEqual(first.RunId, second.RunId);
        Assert.Equal(before, world.Fingerprint()); // no repair, no quarantine, no AuthorityDb write
        Assert.False(world.Organizations[org].IntegrityQuarantineEnabled);
    }

    // ---- Schedule and coordinator ----

    [Fact]
    public void TheScheduleRunsFullFirstThenIncrementalEveryFiveMinutesAndFullEveryDay()
    {
        var options = new JournalVerifierOptions();
        var t = JournalVerificationWorld.Start;
        Assert.Equal(JournalSweepKind.Full, JournalVerificationSchedule.Due(t, null, null, options));
        Assert.Null(JournalVerificationSchedule.Due(t.AddMinutes(4), t, t, options));
        Assert.Equal(JournalSweepKind.Incremental, JournalVerificationSchedule.Due(t.AddMinutes(5), t, t, options));
        Assert.Equal(JournalSweepKind.Full, JournalVerificationSchedule.Due(t.AddHours(24), t.AddHours(23).AddMinutes(58), t, options));
    }

    [Fact]
    public async Task TheCoordinatorSweepsEveryOrganizationAndStreamOnScheduleOnly()
    {
        var (world, org) = await BaselinedAsync();
        var coordinator = new JournalVerificationCoordinator(world.Verifier(), world.Reader, world.Clock);
        var first = await coordinator.RunDueAsync(Ct);
        Assert.Equal(3, first.Count);
        Assert.All(first, r => Assert.Equal(JournalSweepKind.Full, r.Kind));
        Assert.Equal(JournalSweepOutcome.Clean, first.Single(r => r.StreamName == Audit).Outcome);
        Assert.All(first.Where(r => r.StreamName != Audit), r => Assert.Contains(JournalInconclusiveReasons.NotBaselined, r.InconclusiveReasons));

        Assert.Empty(await coordinator.RunDueAsync(Ct));
        world.Clock.Advance(TimeSpan.FromMinutes(5));
        Assert.All(await coordinator.RunDueAsync(Ct), r => Assert.Equal(JournalSweepKind.Incremental, r.Kind));
        Assert.NotEqual(Guid.Empty, org);
    }

    private sealed class ThrowingCheckpointStore : Application.Common.Ports.IVerificationCheckpointStore
    {
        public string StoreName => "Throwing";

        public Task<Application.Common.Ports.CheckpointCreateResult> CreateAsync(Guid organizationId, string streamName, long checkpointSequence, ReadOnlyMemory<byte> checkpointBytes, CancellationToken cancellationToken) =>
            throw new Application.Common.Ports.CheckpointStoreUnavailableException("outage");

        public Task<Application.Common.Ports.StoredObjectRead> ReadAsync(Guid organizationId, string streamName, long checkpointSequence, CancellationToken cancellationToken) =>
            throw new Application.Common.Ports.CheckpointStoreUnavailableException("outage");

        public Task<Application.Common.Ports.CheckpointListingPage> ListAsync(Guid organizationId, string streamName, string? continuationToken, int pageSizeHint, CancellationToken cancellationToken) =>
            throw new Application.Common.Ports.CheckpointStoreUnavailableException("outage");
    }
}
