using System.Text;
using AuthorizationAuthority.Application.Common.Ports;
using AuthorizationAuthority.Application.Features.Journal.Reconciliation;
using AuthorizationAuthority.Application.Features.Journal.Verification;
using AuthorizationAuthority.Domain.Features.Journal;
using AuthorizationAuthority.Infrastructure.Archive;
using Xunit;

namespace AuthorizationAuthority.Tests;

/// <summary>
/// SEC-BL-01 WP-4A (contract amendment A-1): create-only accepted-exception records and their exact-match re-verification; re-baseline as
/// a continuation of the checkpoint chain (OBS-4); bounded incremental listing with the full sweep as the detector below the checkpoint.
/// </summary>
public sealed class JournalAcceptedExceptionTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string Audit = JournalStreamNames.Audit;

    private static Task<JournalBaselineReport> BaselineAsync(JournalVerificationWorld world, Guid org, IReadOnlyList<JournalBaselineExceptionDisposition>? dispositions = null, bool rebaseline = false,
        IVerificationCheckpointStore? checkpoints = null) =>
        world.Baseline(checkpoints).RunAsync(JournalVerificationWorld.BaselineRequest(org, Audit, dispositions: dispositions, rebaseline: rebaseline), Ct);

    private static Task<CheckpointDiscoveryResult> HeadAsync(JournalVerificationWorld world, Guid org) =>
        JournalVerificationCheckpoints.DiscoverAsync(world.Checkpoints, org, Audit, CheckpointDiscoveryMode.FullChain, 0, 100, Ct);

    private static JournalBaselineExceptionDisposition GapDisposition(Guid org, string id = "BX-GAP-0003", long first = 3, long last = 3) =>
        JournalVerificationWorld.Disposition(id, org, Audit, JournalFindingCodes.ArchiveDiscontinuity, null, first, last, exceptionClass: "LEGACY_ARCHIVE_GAP");

    /// <summary>Five delivered rows whose archive object at revision 3 is missing (a legacy archive gap), baselined with an owner disposition.</summary>
    private static async Task<(JournalVerificationWorld World, Guid Org, JournalBaselineReport Report)> AcceptedGapWorldAsync()
    {
        var world = new JournalVerificationWorld();
        var org = world.AddOrganization();
        world.Append(org, Audit, 5);
        await world.DeliverAsync(org, Audit);
        Assert.True(world.Archive.RemoveObjectForTesting(JournalVerificationWorld.ArchiveKey(org, Audit, 3)));
        var report = await BaselineAsync(world, org, [GapDisposition(org)]);
        return (world, org, report);
    }

    // ---- A. Accepted-exception records ----

    [Fact]
    public async Task AnAcceptedLegacyGapIsStoredAsACreateOnlyRecordAndTheCheckpointCrossesItWithReducedAssurance()
    {
        var (world, org, report) = await AcceptedGapWorldAsync();

        Assert.Equal((nameof(JournalBaselineOutcome.ExceptionAccepted), "Reduced"), (report.Outcome, report.AssuranceLevel));
        Assert.Equal(["BX-GAP-0003"], report.AcceptedExceptionRecordsWritten);
        Assert.Equal(5L, report.ArchiveContiguousRevision); // the gap is accepted, so the contiguous head crosses it
        var head = (await HeadAsync(world, org)).Head!;
        Assert.Equal((1L, 5L, CheckpointAssuranceLevel.Reduced), (head.CheckpointSequence, head.VerifiedRevision, head.AssuranceLevel));
        Assert.Equal(["BX-GAP-0003"], head.BaselineExceptionIds);

        var stored = await world.Exceptions.ReadAsync(org, Audit, "BX-GAP-0003", Ct);
        var record = JournalAcceptedExceptions.TryParse(stored.Bytes)!;
        Assert.Equal((JournalFindingCodes.ArchiveDiscontinuity, 3L, 3L, "C-TEST-0001", JournalAcceptedExceptionRecord.ReducedAssurance, 1L),
            (record.FindingCode, record.FirstRevision, record.LastRevision, record.OwnerDispositionReference, record.AssuranceClassification, record.BaselineCheckpointSequence));
        Assert.Equal((JournalVerificationWorld.Start.AddHours(-1), head.ArchiveInstanceId, "test-commit", "operator-test"),
            (record.DispositionRecordedAtUtc, record.ArchiveInstanceId!, record.SourceCommit!, record.OperatorLabel));
        Assert.Equal(64, record.EvidenceDigestHex.Length);

        // Create-only: other bytes under the same id are a conflict, identical bytes are idempotent; nothing is overwritten.
        Assert.Equal(CheckpointCreateStatus.Conflict, (await world.Exceptions.CreateAsync(org, Audit, "BX-GAP-0003", "{}"u8.ToArray(), Ct)).Status);
        Assert.Equal(CheckpointCreateStatus.IdenticalExists, (await world.Exceptions.CreateAsync(org, Audit, "BX-GAP-0003", stored.Bytes!, Ct)).Status);
        Assert.Equal(stored.Bytes, (await world.Exceptions.ReadAsync(org, Audit, "BX-GAP-0003", Ct)).Bytes);
    }

    [Fact]
    public async Task LaterSweepsClassifyTheUnchangedAcceptedGapAsAnObservationAndAdvanceWhileStayingReduced()
    {
        var (world, org, _) = await AcceptedGapWorldAsync();
        world.Append(org, Audit, 2);
        await world.DeliverAsync(org, Audit);
        var verifier = world.Verifier();

        var incremental = await verifier.SweepAsync(org, Audit, JournalSweepKind.Incremental, Ct);
        Assert.Equal(JournalSweepOutcome.Clean, incremental.Outcome);
        Assert.Equal(7L, incremental.NewCheckpoint!.VerifiedRevision);

        var full = await verifier.SweepAsync(org, Audit, JournalSweepKind.Full, Ct);
        Assert.Equal(JournalSweepOutcome.Clean, full.Outcome);
        Assert.Empty(full.Findings);
        var observation = Assert.Single(full.AcceptedObservations!);
        Assert.Equal(("BX-GAP-0003", JournalFindingCodes.ArchiveDiscontinuity, 3L, 3L), (observation.ExceptionId, observation.FindingCode, observation.FirstRevision, observation.LastRevision));

        var head = (await HeadAsync(world, org)).Head!;
        Assert.Equal((3L, CheckpointAssuranceLevel.Reduced, CheckpointWalkKind.Full), (head.CheckpointSequence, head.AssuranceLevel, head.WalkKind));
        Assert.Equal(["BX-GAP-0003"], head.BaselineExceptionIds); // never silently upgraded
        Assert.Empty(world.Alerts.Raised);
    }

    [Fact]
    public async Task ChangedEvidenceInsideTheAcceptedScopeIsANewFindingAndTheCheckpointDoesNotAdvance()
    {
        var (world, org, _) = await AcceptedGapWorldAsync();

        // Someone places an object into the accepted gap: the evidence digest no longer matches.
        world.Archive.PutObjectForTesting(JournalVerificationWorld.ArchiveKey(org, Audit, 3), Encoding.UTF8.GetBytes(world.Row(org, Audit, 3).JournalEventBodyJson));
        var full = await world.Verifier().SweepAsync(org, Audit, JournalSweepKind.Full, Ct);
        Assert.Equal(JournalSweepOutcome.Findings, full.Outcome);
        Assert.Contains(full.Findings, f => f.FindingCode == JournalFindingCodes.AcceptedExceptionEvidenceChanged && f.FirstRevision == 3);
        Assert.Empty(full.AcceptedObservations!);
        Assert.StartsWith("NotCreated", full.CheckpointAction, StringComparison.Ordinal);
        Assert.Contains(world.Alerts.Raised, a => a.Summary.Contains(JournalFindingCodes.AcceptedExceptionEvidenceChanged, StringComparison.Ordinal));

        // Changing the AuthorityDb row inside the scope also changes the evidence.
        var (rowWorld, rowOrg, _) = await AcceptedGapWorldAsync();
        var row = rowWorld.Row(rowOrg, Audit, 3);
        row.JournalEventBodyJson = row.JournalEventBodyJson.Replace("entry 3", "entry Q", StringComparison.Ordinal);
        var rowRun = await rowWorld.Verifier().SweepAsync(rowOrg, Audit, JournalSweepKind.Full, Ct);
        Assert.Contains(rowRun.Findings, f => f.FindingCode == JournalFindingCodes.AcceptedExceptionEvidenceChanged);
    }

    [Fact]
    public async Task AMissingOrForeignRecordIsAFindingAndTheGapIsNoLongerAccepted()
    {
        var (world, org, _) = await AcceptedGapWorldAsync();
        Assert.True(world.Exceptions.RemoveForTesting(org, Audit, "BX-GAP-0003"));
        var run = await world.Verifier().SweepAsync(org, Audit, JournalSweepKind.Full, Ct);
        Assert.Equal(JournalSweepOutcome.Findings, run.Outcome);
        Assert.Contains(run.Findings, f => f.FindingCode == JournalFindingCodes.AcceptedExceptionRecordInvalid);
        Assert.Contains(run.Findings, f => f.FindingCode == JournalFindingCodes.ArchiveDiscontinuity && f.FirstRevision == 3); // reported again, not accepted

        // A record whose provenance names another archive instance does not match exactly.
        var (other, otherOrg, _) = await AcceptedGapWorldAsync();
        var original = JournalAcceptedExceptions.TryParse((await other.Exceptions.ReadAsync(otherOrg, Audit, "BX-GAP-0003", Ct)).Bytes)!;
        other.Exceptions.PutForTesting(otherOrg, Audit, "BX-GAP-0003", JournalAcceptedExceptions.Serialize(original with { ArchiveInstanceId = "another-archive" }));
        var foreign = await other.Verifier().SweepAsync(otherOrg, Audit, JournalSweepKind.Full, Ct);
        Assert.Contains(foreign.Findings, f => f.FindingCode == JournalFindingCodes.AcceptedExceptionRecordInvalid);

        // A record whose scope was widened no longer matches its evidence digest.
        var (widened, widenedOrg, _) = await AcceptedGapWorldAsync();
        var record = JournalAcceptedExceptions.TryParse((await widened.Exceptions.ReadAsync(widenedOrg, Audit, "BX-GAP-0003", Ct)).Bytes)!;
        widened.Exceptions.PutForTesting(widenedOrg, Audit, "BX-GAP-0003", JournalAcceptedExceptions.Serialize(record with { FirstRevision = 2, LastRevision = 4 }));
        var widenedRun = await widened.Verifier().SweepAsync(widenedOrg, Audit, JournalSweepKind.Full, Ct);
        Assert.Contains(widenedRun.Findings, f => f.FindingCode == JournalFindingCodes.AcceptedExceptionEvidenceChanged);
    }

    [Fact]
    public async Task AnAdditionalFindingOutsideTheAcceptedScopeIsReportedAndNotAccepted()
    {
        var (world, org, _) = await AcceptedGapWorldAsync();
        world.Append(org, Audit, 3);
        await world.DeliverAsync(org, Audit);
        world.Archive.RemoveObjectForTesting(JournalVerificationWorld.ArchiveKey(org, Audit, 6)); // a new gap above the checkpoint
        var run = await world.Verifier().SweepAsync(org, Audit, JournalSweepKind.Incremental, Ct);
        Assert.Equal(JournalSweepOutcome.Findings, run.Outcome);
        Assert.Contains(run.Findings, f => f.FindingCode == JournalFindingCodes.ArchiveDiscontinuity && f.FirstRevision == 6);
        Assert.True(run.BoundedListing);
    }

    [Fact]
    public async Task NoFindingIsEverAcceptedWithoutADisposition()
    {
        var world = new JournalVerificationWorld();
        var org = world.AddOrganization();
        world.Append(org, Audit, 5);
        await world.DeliverAsync(org, Audit);
        world.Archive.RemoveObjectForTesting(JournalVerificationWorld.ArchiveKey(org, Audit, 3));
        var report = await BaselineAsync(world, org);
        Assert.Equal(nameof(JournalBaselineOutcome.Failed), report.Outcome);
        Assert.Empty(report.AcceptedExceptionRecordsWritten);
        Assert.Empty(world.Exceptions.Records);
        Assert.Equal(CheckpointDiscoveryOutcome.Empty, (await HeadAsync(world, org)).Outcome);
    }

    [Theory]
    [InlineData("missing-time")]
    [InlineData("future-time")]
    [InlineData("duplicate-id")]
    [InlineData("invalid-id")]
    [InlineData("oversized-scope")]
    [InlineData("missing-reference")]
    public async Task AMalformedDispositionMakesTheBaselineInconclusiveAndWritesNothing(string defect)
    {
        var world = new JournalVerificationWorld();
        var org = world.AddOrganization();
        world.Append(org, Audit, 5);
        await world.DeliverAsync(org, Audit);
        world.Archive.RemoveObjectForTesting(JournalVerificationWorld.ArchiveKey(org, Audit, 3));
        var good = GapDisposition(org);
        IReadOnlyList<JournalBaselineExceptionDisposition> dispositions = defect switch
        {
            "missing-time" => [good with { DispositionRecordedAtUtc = default }],
            "future-time" => [good with { DispositionRecordedAtUtc = JournalVerificationWorld.Start.AddDays(1) }],
            "duplicate-id" => [good, good with { FirstRevision = 1, LastRevision = 1 }],
            "invalid-id" => [good with { ExceptionId = "../escape" }],
            "oversized-scope" => [good with { FirstRevision = 1, LastRevision = JournalAcceptedExceptions.MaximumScopeRevisions + 1 }],
            _ => [good with { OwnerDispositionReference = string.Empty }],
        };

        var report = await BaselineAsync(world, org, dispositions);
        Assert.Equal(nameof(JournalBaselineOutcome.Inconclusive), report.Outcome);
        Assert.Contains(JournalInconclusiveReasons.DispositionInvalid, report.InconclusiveReasons);
        Assert.Empty(world.Exceptions.Records);
        Assert.Equal(CheckpointDiscoveryOutcome.Empty, (await HeadAsync(world, org)).Outcome);
    }

    [Fact]
    public async Task AConflictingRecordUnderTheSameIdentifierBlocksTheCheckpoint()
    {
        var world = new JournalVerificationWorld();
        var org = world.AddOrganization();
        world.Append(org, Audit, 5);
        await world.DeliverAsync(org, Audit);
        world.Archive.RemoveObjectForTesting(JournalVerificationWorld.ArchiveKey(org, Audit, 3));
        world.Exceptions.PutForTesting(org, Audit, "BX-GAP-0003", "{\"something\":\"else\"}"u8.ToArray());
        var report = await BaselineAsync(world, org, [GapDisposition(org)]);
        Assert.Equal("NotCreated:EXCEPTION_RECORD_CONFLICT", report.CheckpointAction);
        Assert.Equal(CheckpointDiscoveryOutcome.Empty, (await HeadAsync(world, org)).Outcome);
    }

    [Fact]
    public void ARecordIsCanonicalStrictAndAlwaysReduced()
    {
        var record = new JournalAcceptedExceptionRecord(JournalAcceptedExceptionRecord.SchemaV1, "BX-1", "LEGACY", Guid.CreateVersion7(), Audit, 1, 2, JournalFindingCodes.ArchiveDiscontinuity, null,
            new string('a', 64), "C-TEST", JournalVerificationWorld.Start, Guid.CreateVersion7(), 1, "archive-x", "tool", "commit", "operator", JournalVerificationWorld.Start, "Reduced");
        var bytes = JournalAcceptedExceptions.Serialize(record);
        Assert.Equal(record, JournalAcceptedExceptions.TryParse(bytes));
        Assert.Throws<ArgumentException>(() => JournalAcceptedExceptions.Serialize(record with { AssuranceClassification = "Full" }));
        Assert.Throws<ArgumentException>(() => JournalAcceptedExceptions.Serialize(record with { EvidenceDigestHex = "ABC" }));
        Assert.Null(JournalAcceptedExceptions.TryParse(Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(bytes).Replace("}", ",\"extra\":1}", StringComparison.Ordinal))));
        Assert.Null(JournalAcceptedExceptions.TryParse(Encoding.UTF8.GetBytes(" " + Encoding.UTF8.GetString(bytes)))); // non-canonical bytes
    }

    // ---- B. Re-baseline continues the chain (OBS-4) ----

    [Fact]
    public async Task ARebaselineContinuesTheChainAtTheNextSequenceAndLinksToThePreviousCheckpoint()
    {
        var world = new JournalVerificationWorld();
        var org = world.AddOrganization();
        world.Append(org, Audit, 5);
        await world.DeliverAsync(org, Audit);
        var first = await BaselineAsync(world, org);
        world.Append(org, Audit, 2);
        await world.DeliverAsync(org, Audit);

        var again = await BaselineAsync(world, org, rebaseline: true);
        Assert.Equal(nameof(JournalBaselineOutcome.Clean), again.Outcome);
        Assert.True(again.Rebaseline);
        Assert.Equal(first.Checkpoint, again.PreviousCheckpoint);
        Assert.Equal((2L, 7L), (again.Checkpoint!.CheckpointSequence, again.Checkpoint.VerifiedRevision));

        var discovery = await HeadAsync(world, org); // FullChain verifies every link
        Assert.Equal((CheckpointDiscoveryOutcome.Head, 2L, CheckpointWalkKind.Baseline), (discovery.Outcome, discovery.Head!.CheckpointSequence, discovery.Head.WalkKind));
        Assert.Equal(2, (await world.Checkpoints.ListAsync(org, Audit, null, 100, Ct)).Objects.Count); // checkpoint 1 untouched
    }

    [Fact]
    public async Task ARebaselineCarriesEveryAcceptedExceptionNeverUpgradesAndMayAddNewOnes()
    {
        var (world, org, _) = await AcceptedGapWorldAsync();
        world.Append(org, Audit, 3);
        await world.DeliverAsync(org, Audit);

        // Without a new disposition: carried, still Reduced.
        var carried = await BaselineAsync(world, org, rebaseline: true);
        Assert.Equal((nameof(JournalBaselineOutcome.ExceptionAccepted), "Reduced"), (carried.Outcome, carried.AssuranceLevel));
        Assert.Equal(["BX-GAP-0003"], carried.CarriedExceptionIds);
        Assert.Contains(carried.Findings, f => f.AcceptedByExceptionId == "BX-GAP-0003");
        var head = (await HeadAsync(world, org)).Head!;
        Assert.Equal((2L, 8L, CheckpointAssuranceLevel.Reduced), (head.CheckpointSequence, head.VerifiedRevision, head.AssuranceLevel));
        Assert.Equal(["BX-GAP-0003"], head.BaselineExceptionIds);

        // A newly accepted gap above the head is added; the old one stays.
        world.Append(org, Audit, 3);
        await world.DeliverAsync(org, Audit);
        world.Archive.RemoveObjectForTesting(JournalVerificationWorld.ArchiveKey(org, Audit, 10));
        var added = await BaselineAsync(world, org, [GapDisposition(org, "BX-GAP-0010", 10, 10)], rebaseline: true);
        Assert.Equal(nameof(JournalBaselineOutcome.ExceptionAccepted), added.Outcome);
        Assert.Equal(["BX-GAP-0010"], added.AcceptedExceptionRecordsWritten);
        head = (await HeadAsync(world, org)).Head!;
        Assert.Equal((3L, 11L), (head.CheckpointSequence, head.VerifiedRevision));
        Assert.Equal(["BX-GAP-0003", "BX-GAP-0010"], head.BaselineExceptionIds);
    }

    [Fact]
    public async Task ARebaselineThatWouldMoveTheVerifiedRevisionBackwardIsRejected()
    {
        var world = new JournalVerificationWorld();
        var org = world.AddOrganization();
        world.Append(org, Audit, 5);
        await world.DeliverAsync(org, Audit);
        await BaselineAsync(world, org);

        // The archive tail 4..5 disappears and the rows are reset to Pending (as if never delivered): no finding a baseline can see, but
        // the archive no longer reaches the verified revision.
        foreach (var revision in new long[] { 4, 5 })
        {
            world.Archive.RemoveObjectForTesting(JournalVerificationWorld.ArchiveKey(org, Audit, revision));
            var row = world.Row(org, Audit, revision);
            row.ArchiveDeliveryState = ArchiveDeliveryStates.Pending;
            row.ArchiveReceiptReference = null;
            row.ArchiveReceiptDigest = null;
        }

        var report = await BaselineAsync(world, org, rebaseline: true);
        Assert.Equal("NotCreated:REBASELINE_REVISION_REGRESSION", report.CheckpointAction);
        Assert.Single((await world.Checkpoints.ListAsync(org, Audit, null, 100, Ct)).Objects);
    }

    [Fact]
    public async Task ARebaselineAgainstAnotherArchiveInstanceIsRejected()
    {
        var world = new JournalVerificationWorld();
        var org = world.AddOrganization();
        world.Append(org, Audit, 5);
        await world.DeliverAsync(org, Audit);
        await BaselineAsync(world, org);

        // The archive is replaced by another instance holding the same bytes (a restore): that is recovery (G14), not a re-baseline.
        var bodies = world.Rows.Where(r => r.OrganizationId == org).ToDictionary(r => r.JournalStreamRevision, r => r.JournalEventBodyJson);
        world.Archive.WipeForTesting();
        await world.Archive.CreateIdentityAsync(ArchiveIdentityCodec.NewIdentity(JournalVerificationWorld.Start.AddDays(1)), Ct);
        foreach (var (revision, body) in bodies)
        {
            world.Archive.PutObjectForTesting(JournalVerificationWorld.ArchiveKey(org, Audit, revision), Encoding.UTF8.GetBytes(body));
        }

        var report = await BaselineAsync(world, org, rebaseline: true);
        Assert.Equal("NotCreated:REBASELINE_ARCHIVE_INSTANCE_CHANGED", report.CheckpointAction);
        Assert.Single((await world.Checkpoints.ListAsync(org, Audit, null, 100, Ct)).Objects);
    }

    [Fact]
    public async Task AConcurrentlyCreatedNextCheckpointMakesTheRebaselineAConflictNeverAnOverwrite()
    {
        var world = new JournalVerificationWorld();
        var org = world.AddOrganization();
        world.Append(org, Audit, 5);
        await world.DeliverAsync(org, Audit);
        await BaselineAsync(world, org);
        var racing = new RacingCheckpointStore(world.Checkpoints);
        var report = await BaselineAsync(world, org, rebaseline: true, checkpoints: racing);
        Assert.Equal("NotCreated:REBASELINE_CONFLICT", report.CheckpointAction);
        Assert.Equal(racing.RacedBytes, (await world.Checkpoints.ReadAsync(org, Audit, 2, Ct)).Bytes); // the other writer's bytes stay
    }

    [Fact]
    public async Task ARebaselineReportsAChainFindingAndCreatesNothingWhenACarriedRecordChanged()
    {
        var (world, org, _) = await AcceptedGapWorldAsync();
        world.Exceptions.RemoveForTesting(org, Audit, "BX-GAP-0003");
        var report = await BaselineAsync(world, org, rebaseline: true);
        Assert.Equal(nameof(JournalBaselineOutcome.Failed), report.Outcome);
        Assert.Contains(report.Findings, f => f.FindingCode == JournalFindingCodes.AcceptedExceptionRecordInvalid);
        Assert.Single((await world.Checkpoints.ListAsync(org, Audit, null, 100, Ct)).Objects);
    }

    // ---- C. Bounded incremental verification ----

    [Fact]
    public async Task AnIncrementalSweepListsOnlyAboveTheCheckpointAndStillReadsTheAnchor()
    {
        var world = new JournalVerificationWorld();
        var org = world.AddOrganization();
        world.Append(org, Audit, 40);
        await world.DeliverAsync(org, Audit);
        await BaselineAsync(world, org);
        world.Append(org, Audit, 3);
        await world.DeliverAsync(org, Audit);

        var bounded = await world.Verifier().SweepAsync(org, Audit, JournalSweepKind.Incremental, Ct);
        Assert.Equal(JournalSweepOutcome.Clean, bounded.Outcome);
        Assert.True(bounded.BoundedListing);
        Assert.Equal((3L, 4L), (bounded.Metrics.ArchiveObjectsListed, bounded.Metrics.ArchiveObjectsRead)); // 41..43 listed; anchor 40 plus 41..43 read

        world.Append(org, Audit, 3);
        await world.DeliverAsync(org, Audit);
        var unbounded = await world.Verifier(new JournalVerifierOptions { BoundedIncrementalListing = false }).SweepAsync(org, Audit, JournalSweepKind.Incremental, Ct);
        Assert.Equal(JournalSweepOutcome.Clean, unbounded.Outcome);
        Assert.False(unbounded.BoundedListing);
        Assert.Equal(46L, unbounded.Metrics.ArchiveObjectsListed); // the whole stream
    }

    [Fact]
    public async Task ADeletionBelowTheCheckpointIsInvisibleToTheBoundedIncrementalSweepAndDetectedByTheFullSweep()
    {
        var world = new JournalVerificationWorld();
        var org = world.AddOrganization();
        world.Append(org, Audit, 10);
        await world.DeliverAsync(org, Audit);
        await BaselineAsync(world, org);
        world.Archive.RemoveObjectForTesting(JournalVerificationWorld.ArchiveKey(org, Audit, 4));
        var verifier = world.Verifier();

        var incremental = await verifier.SweepAsync(org, Audit, JournalSweepKind.Incremental, Ct);
        Assert.Equal(JournalSweepOutcome.Clean, incremental.Outcome); // the documented latency trade-off (A-1)
        Assert.True(incremental.BoundedListing);

        var full = await verifier.SweepAsync(org, Audit, JournalSweepKind.Full, Ct);
        Assert.Equal(JournalSweepOutcome.Findings, full.Outcome);
        Assert.Contains(full.Findings, f => f.FindingCode == JournalFindingCodes.ArchiveDiscontinuity && f.FirstRevision == 4);
        Assert.False(full.BoundedListing);
    }

    [Fact]
    public async Task AChangedAnchorAndADatabaseArchiveMismatchAboveTheCheckpointAreStillDetectedByTheBoundedSweep()
    {
        var world = new JournalVerificationWorld();
        var org = world.AddOrganization();
        world.Append(org, Audit, 10);
        await world.DeliverAsync(org, Audit);
        await BaselineAsync(world, org);
        world.Archive.PutObjectForTesting(JournalVerificationWorld.ArchiveKey(org, Audit, 10), "{\"replaced\":true}"u8.ToArray());
        var anchor = await world.Verifier().SweepAsync(org, Audit, JournalSweepKind.Incremental, Ct);
        Assert.Contains(anchor.Findings, f => f.FindingCode == JournalFindingCodes.ArchiveRegression && f.FirstRevision == 10);

        var other = new JournalVerificationWorld();
        var otherOrg = other.AddOrganization();
        other.Append(otherOrg, Audit, 10);
        await other.DeliverAsync(otherOrg, Audit);
        await BaselineAsync(other, otherOrg);
        other.Append(otherOrg, Audit, 3);
        await other.DeliverAsync(otherOrg, Audit);
        var row = other.Row(otherOrg, Audit, 12);
        row.JournalEventBodyJson = row.JournalEventBodyJson.Replace("entry 12", "entry W", StringComparison.Ordinal);
        var mismatch = await other.Verifier().SweepAsync(otherOrg, Audit, JournalSweepKind.Incremental, Ct);
        Assert.True(mismatch.BoundedListing);
        Assert.Contains(mismatch.Findings, f => f.FindingCode == JournalFindingCodes.AnchorMismatch && f.FirstRevision == 12);
    }

    [Fact]
    public async Task TheLastCompleteVerificationIsTrackedSeparatelyFromIncrementalProgress()
    {
        var world = new JournalVerificationWorld();
        var org = world.AddOrganization();
        world.Append(org, Audit, 5);
        await world.DeliverAsync(org, Audit);
        await BaselineAsync(world, org);
        var baselineAt = (await HeadAsync(world, org)).Head!.VerifiedAtUtc;
        world.Append(org, Audit, 1);
        await world.DeliverAsync(org, Audit);
        world.Clock.Advance(TimeSpan.FromMinutes(5));

        var verifier = world.Verifier();
        var incremental = await verifier.SweepAsync(org, Audit, JournalSweepKind.Incremental, Ct);
        Assert.Equal(baselineAt, incremental.LastCompleteVerificationAtUtc); // the incremental advance is not a complete verification

        // A new process sees only an Incremental head: no complete verification is known until a full sweep.
        world.Clock.Advance(TimeSpan.FromMinutes(5));
        var restarted = world.Verifier();
        var unknown = await restarted.SweepAsync(org, Audit, JournalSweepKind.Incremental, Ct);
        Assert.Null(unknown.LastCompleteVerificationAtUtc);
        world.Clock.Advance(TimeSpan.FromMinutes(5));
        var full = await restarted.SweepAsync(org, Audit, JournalSweepKind.Full, Ct);
        Assert.Equal(JournalSweepOutcome.Clean, full.Outcome);
        Assert.Equal(world.Clock.GetUtcNow(), full.LastCompleteVerificationAtUtc);
    }

    [Fact]
    public void TheRevisionPrefixesCoverExactlyTheRevisionsAboveTheCheckpoint()
    {
        foreach (var after in new long[] { 0, 1, 5, 9, 10, 99, 999, 1234, 19_999, 20_000, 999_999_999_998 })
        {
            var prefixes = BlobSecurityEventArchive.RevisionPrefixesAbove(after);
            Assert.True(prefixes.Count <= 1 + (12 * 9));
            Assert.Equal(prefixes.Count, prefixes.Distinct(StringComparer.Ordinal).Count());
            foreach (var probe in new[] { after - 1, after, after + 1, after + 2, after + 9, after + 10, after + 11, after + 1000, 999_999_999_999 }.Where(p => p >= 1 && p <= 999_999_999_999))
            {
                var name = probe.ToString("D12", System.Globalization.CultureInfo.InvariantCulture);
                var matches = prefixes.Count(p => name.StartsWith(p, StringComparison.Ordinal));
                Assert.Equal(probe > after ? 1 : 0, matches); // exactly one prefix for every revision above, none at or below
            }
        }

        Assert.Empty(BlobSecurityEventArchive.RevisionPrefixesAbove(999_999_999_999));
    }

    /// <summary>On the first create, another writer stores different bytes at the same sequence first (the race the re-baseline must lose safely).</summary>
    private sealed class RacingCheckpointStore(IVerificationCheckpointStore inner) : IVerificationCheckpointStore
    {
        public byte[]? RacedBytes { get; private set; }

        public string StoreName => "Racing";

        public async Task<CheckpointCreateResult> CreateAsync(Guid organizationId, string streamName, long checkpointSequence, ReadOnlyMemory<byte> checkpointBytes, CancellationToken cancellationToken)
        {
            if (RacedBytes is null)
            {
                RacedBytes = [.. checkpointBytes.ToArray().Select(b => b == (byte)'B' ? (byte)'I' : b)];
                await inner.CreateAsync(organizationId, streamName, checkpointSequence, RacedBytes, cancellationToken);
            }

            return await inner.CreateAsync(organizationId, streamName, checkpointSequence, checkpointBytes, cancellationToken);
        }

        public Task<StoredObjectRead> ReadAsync(Guid organizationId, string streamName, long checkpointSequence, CancellationToken cancellationToken) =>
            inner.ReadAsync(organizationId, streamName, checkpointSequence, cancellationToken);

        public Task<CheckpointListingPage> ListAsync(Guid organizationId, string streamName, string? continuationToken, int pageSizeHint, CancellationToken cancellationToken) =>
            inner.ListAsync(organizationId, streamName, continuationToken, pageSizeHint, cancellationToken);
    }
}
