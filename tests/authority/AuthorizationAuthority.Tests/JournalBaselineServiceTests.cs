using System.Text;
using AuthorizationAuthority.Application.Common;
using AuthorizationAuthority.Application.Common.Ports;
using AuthorizationAuthority.Application.Features.Journal.Reconciliation;
using AuthorizationAuthority.Application.Features.Journal.Verification;
using AuthorizationAuthority.Domain.Features.Journal;
using AuthorizationAuthority.Infrastructure.Archive;
using Xunit;

namespace AuthorizationAuthority.Tests;

/// <summary>
/// SEC-BL-01 WP-3 (C-155, contract §10): baseline outcomes CLEAN, EXCEPTION_ACCEPTED, FAILED, INCONCLUSIVE; owner dispositions only;
/// deterministic report; checkpoint 1 only when requested; a re-baseline only when explicitly requested (A-1, see
/// <see cref="JournalAcceptedExceptionTests"/>); no AuthorityDb writes.
/// </summary>
public sealed class JournalBaselineServiceTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string Audit = JournalStreamNames.Audit;

    private static async Task<(JournalVerificationWorld World, Guid Org)> DeliveredWorldAsync(int rows = 5, string stream = Audit)
    {
        var world = new JournalVerificationWorld();
        var org = world.AddOrganization();
        world.Append(org, stream, rows);
        await world.DeliverAsync(org, stream);
        return (world, org);
    }

    private static Task<JournalBaselineReport> RunAsync(JournalVerificationWorld world, Guid org, string stream = Audit, bool createCheckpoint = true, bool createSentinel = true,
        IReadOnlyList<JournalBaselineExceptionDisposition>? dispositions = null) =>
        world.Baseline().RunAsync(JournalVerificationWorld.BaselineRequest(org, stream, createCheckpoint, createSentinel, dispositions), Ct);

    [Fact]
    public async Task ACleanBaselineCreatesCheckpointOneWithFullAssuranceAndCarriesTheMandatoryStatement()
    {
        var (world, org) = await DeliveredWorldAsync();
        var before = world.Fingerprint();
        var report = await RunAsync(world, org);

        Assert.Equal(nameof(JournalBaselineOutcome.Clean), report.Outcome);
        Assert.Equal("Full", report.AssuranceLevel);
        Assert.Empty(report.Findings);
        Assert.Equal((5L, 5L), (report.DatabaseHeadRevision, report.ArchiveContiguousRevision));
        Assert.Equal("Created", report.SentinelAction);
        Assert.StartsWith(nameof(CheckpointAppendOutcome.Created), report.CheckpointAction, StringComparison.Ordinal);
        Assert.Equal(JournalBaselineService.HistoricalStatement, report.HistoricalStatement);
        Assert.Equal((5L, 5L), (report.Metrics.DatabaseRowsInspected, report.Metrics.ArchiveObjectsRead));

        var head = await JournalVerificationCheckpoints.DiscoverAsync(world.Checkpoints, org, Audit, CheckpointDiscoveryMode.FullChain, 0, 10, Ct);
        Assert.Equal((1L, 5L, CheckpointWalkKind.Baseline, CheckpointAssuranceLevel.Full), (head.Head!.CheckpointSequence, head.Head.VerifiedRevision, head.Head.WalkKind, head.Head.AssuranceLevel));
        Assert.Equal(Application.Common.CanonicalJson.Digest(world.Row(org, Audit, 5).JournalEventBodyJson), head.Head.VerifiedDigest); // anchored in the archived bytes
        Assert.Equal(before, world.Fingerprint()); // no AuthorityDb write
    }

    [Fact]
    public async Task TheReportIsDeterministicForTheSameInputsAndClock()
    {
        var first = await DeliveredWorldAsync();
        var second = await DeliveredWorldAsync();
        var a = JournalBaselineService.Serialize(await RunAsync(first.World, first.Org, createCheckpoint: false));
        var b = JournalBaselineService.Serialize(await RunAsync(first.World, first.Org, createCheckpoint: false));
        Assert.Equal(Encoding.UTF8.GetString(a).Replace("\"Created\"", "\"Existing\"", StringComparison.Ordinal), Encoding.UTF8.GetString(b)); // only the sentinel action differs (created by the first run)
        Assert.NotNull(second.World);
    }

    [Fact]
    public async Task WithoutCheckpointPermissionNothingIsCreatedAndWithoutSentinelPermissionTheBaselineIsInconclusive()
    {
        var (world, org) = await DeliveredWorldAsync();
        var noCheckpoint = await RunAsync(world, org, createCheckpoint: false);
        Assert.Equal((nameof(JournalBaselineOutcome.Clean), "NotCreated:NOT_REQUESTED"), (noCheckpoint.Outcome, noCheckpoint.CheckpointAction));
        Assert.Equal(CheckpointDiscoveryOutcome.Empty, (await JournalVerificationCheckpoints.DiscoverAsync(world.Checkpoints, org, Audit, CheckpointDiscoveryMode.FullChain, 0, 10, Ct)).Outcome);

        var (fresh, freshOrg) = await DeliveredWorldAsync();
        var noSentinel = await RunAsync(fresh, freshOrg, createSentinel: false);
        Assert.Equal(nameof(JournalBaselineOutcome.Inconclusive), noSentinel.Outcome);
        Assert.Contains(JournalInconclusiveReasons.SentinelAbsent, noSentinel.InconclusiveReasons);
        Assert.Equal(StoredObjectStatus.Absent, (await fresh.Archive.ReadIdentityAsync(Ct)).Status);
    }

    [Fact]
    public async Task AnEmptyStreamBaselinesAtRevisionZero()
    {
        var world = new JournalVerificationWorld();
        var org = world.AddOrganization();
        var report = await RunAsync(world, org, JournalStreamNames.Content);
        Assert.Equal(nameof(JournalBaselineOutcome.Clean), report.Outcome);
        var head = (await JournalVerificationCheckpoints.DiscoverAsync(world.Checkpoints, org, JournalStreamNames.Content, CheckpointDiscoveryMode.FullChain, 0, 10, Ct)).Head!;
        Assert.Equal((0L, (byte[]?)null), (head.VerifiedRevision, head.VerifiedDigest));
    }

    // ---- Legacy exceptions (BL-4, BL-7) ----

    /// <summary>A jsonb-era style row (E-12): body no longer matches its digest, refused by delivery, never archived; later rows stay pending.</summary>
    private static async Task<(JournalVerificationWorld World, Guid Org)> LegacyWorldAsync()
    {
        var world = new JournalVerificationWorld();
        var org = world.AddOrganization();
        world.Append(org, Audit, 3);
        await world.DeliverAsync(org, Audit);
        world.Append(org, Audit, 3);
        var legacy = world.Row(org, Audit, 4);
        legacy.JournalEventBodyJson = legacy.JournalEventBodyJson.Replace("{\"envelope_format\"", "{ \"envelope_format\"", StringComparison.Ordinal); // normalized text, digest kept
        legacy.ArchiveDeliveryState = ArchiveDeliveryStates.VerificationFailed;
        world.Clock.Advance(TimeSpan.FromHours(1));
        return (world, org);
    }

    [Fact]
    public async Task ALegacyRowWithoutAnOwnerDispositionFailsTheBaselineAndIsOnlyACandidateException()
    {
        var (world, org) = await LegacyWorldAsync();
        var report = await RunAsync(world, org);
        Assert.Equal(nameof(JournalBaselineOutcome.Failed), report.Outcome);
        var legacy = Assert.Single(report.Findings, f => f.Subtype == JournalFailureCodes.BodyDigestMismatch);
        Assert.Equal(("LEGACY_UNVERIFIABLE", (string?)null, 4L), (legacy.CandidateExceptionClass, legacy.AcceptedByExceptionId, legacy.FirstRevision));
        Assert.Contains(report.Findings, f => f.FindingCode == JournalFindingCodes.DeliveryRefused && f.Severity == "Operational");
        Assert.Contains(report.Findings, f => f.FindingCode == JournalFindingCodes.DeliveryLag && f.FirstRevision == 5 && f.LastRevision == 6);
        Assert.Equal("NotCreated:OUTCOME_FAILED", report.CheckpointAction);
        Assert.Empty(report.AppliedExceptions);
    }

    [Fact]
    public async Task AnOwnerDispositionCoveringTheExactScopeGivesExceptionAcceptedWithReducedAssuranceAndProvenance()
    {
        var (world, org) = await LegacyWorldAsync();
        var disposition = JournalVerificationWorld.Disposition("BX-LEGACY-0001", org, Audit, JournalFindingCodes.ChainIntegrityFailure, JournalFailureCodes.BodyDigestMismatch, 4, 4);
        var report = await RunAsync(world, org, dispositions: [disposition]);

        Assert.Equal((nameof(JournalBaselineOutcome.ExceptionAccepted), "Reduced"), (report.Outcome, report.AssuranceLevel));
        var applied = Assert.Single(report.AppliedExceptions);
        Assert.Equal(("BX-LEGACY-0001", "C-TEST-0001", "operator-test", "test-commit", "test-tool"), (applied.ExceptionId, applied.OwnerDispositionReference, applied.OperatorLabel, applied.SourceCommit, applied.ToolVersion));
        Assert.NotNull(applied.ArchiveInstanceId);
        Assert.Contains("Reduced assurance", applied.AssuranceStatement, StringComparison.Ordinal);

        var head = (await JournalVerificationCheckpoints.DiscoverAsync(world.Checkpoints, org, Audit, CheckpointDiscoveryMode.FullChain, 0, 10, Ct)).Head!;
        Assert.Equal((CheckpointAssuranceLevel.Reduced, 3L), (head.AssuranceLevel, head.VerifiedRevision));
        Assert.Equal(["BX-LEGACY-0001"], head.BaselineExceptionIds);
    }

    [Fact]
    public async Task ADispositionOutsideTheFindingScopeIsNotAppliedAndIsReportedUnused()
    {
        var (world, org) = await LegacyWorldAsync();
        var wrongRange = JournalVerificationWorld.Disposition("BX-WRONG", org, Audit, JournalFindingCodes.ChainIntegrityFailure, JournalFailureCodes.BodyDigestMismatch, 5, 9, "C-TEST-0002");
        var wrongCode = JournalVerificationWorld.Disposition("BX-CODE", org, Audit, JournalFindingCodes.AnchorMismatch, null, 1, 9, "C-TEST-0003");
        var report = await RunAsync(world, org, dispositions: [wrongRange, wrongCode]);
        Assert.Equal(nameof(JournalBaselineOutcome.Failed), report.Outcome);
        Assert.Equal(["BX-CODE", "BX-WRONG"], report.UnusedDispositions);
    }

    // ---- Failed and inconclusive ----

    [Fact]
    public async Task AnArchivedObjectThatDiffersFromTheRowFailsTheBaseline()
    {
        var (world, org) = await DeliveredWorldAsync();
        var row = world.Row(org, Audit, 3);
        world.Archive.PutObjectForTesting(JournalVerificationWorld.ArchiveKey(org, Audit, 3), Encoding.UTF8.GetBytes(row.JournalEventBodyJson.Replace("entry 3", "entry X", StringComparison.Ordinal)));
        var report = await RunAsync(world, org);
        Assert.Equal(nameof(JournalBaselineOutcome.Failed), report.Outcome);
        Assert.Contains(report.Findings, f => f.FindingCode == JournalFindingCodes.AnchorMismatch && f.FirstRevision == 3);
        Assert.Equal(CheckpointDiscoveryOutcome.Empty, (await JournalVerificationCheckpoints.DiscoverAsync(world.Checkpoints, org, Audit, CheckpointDiscoveryMode.FullChain, 0, 10, Ct)).Outcome);
    }

    [Fact]
    public async Task MissingArchiveObjectsForgedAnchoredRowsRedigestedRowsAndATruncatedTailAreReported()
    {
        // Missing archive object (discontinuity)
        var (gap, gapOrg) = await DeliveredWorldAsync();
        gap.Archive.RemoveObjectForTesting(JournalVerificationWorld.ArchiveKey(gapOrg, Audit, 2));
        Assert.Contains((await RunAsync(gap, gapOrg)).Findings, f => f.FindingCode == JournalFindingCodes.ArchiveDiscontinuity && f.FirstRevision == 2);

        // Forged Anchored row: claims archival, archive never received it
        var (forged, forgedOrg) = await DeliveredWorldAsync();
        forged.Append(forgedOrg, Audit, 1);
        forged.Row(forgedOrg, Audit, 6).ArchiveDeliveryState = ArchiveDeliveryStates.Anchored;
        forged.Row(forgedOrg, Audit, 6).ArchiveReceiptReference = "fake-receipt";
        Assert.Contains((await RunAsync(forged, forgedOrg)).Findings, f => f.FindingCode == JournalFindingCodes.FakeReceipt && f.FirstRevision == 6);

        // Recomputed digest of a delivered row (consistent J1 edit of one row)
        var (redigest, redigestOrg) = await DeliveredWorldAsync();
        var row = redigest.Row(redigestOrg, Audit, 2);
        row.JournalEventBodyJson = row.JournalEventBodyJson.Replace("entry 2", "entry Z", StringComparison.Ordinal);
        row.JournalEventDigest = CanonicalJson.Digest(row.JournalEventBodyJson);
        var redigested = await RunAsync(redigest, redigestOrg);
        Assert.Contains(redigested.Findings, f => f.FindingCode == JournalFindingCodes.AnchorMismatch && f.FirstRevision == 2);
        Assert.Contains(redigested.Findings, f => f.Subtype == JournalFailureCodes.LinkMismatch && f.FirstRevision == 3);

        // Tail truncation with the head reset (restore or deletion): the archive is ahead of AuthorityDb
        var (truncated, truncatedOrg) = await DeliveredWorldAsync();
        truncated.Rows.RemoveAll(r => r.OrganizationId == truncatedOrg && r.JournalStreamRevision > 3);
        truncated.SetHead(truncatedOrg, Audit, 3, truncated.Row(truncatedOrg, Audit, 3).JournalEventDigest);
        var behind = await RunAsync(truncated, truncatedOrg);
        Assert.Contains(behind.Findings, f => f.FindingCode == JournalFindingCodes.BehindArchive && f.FirstRevision == 4 && f.LastRevision == 5);
        Assert.Equal(nameof(JournalBaselineOutcome.Failed), behind.Outcome);
    }

    [Fact]
    public async Task UnavailableArchiveOrCheckpointStoreIsInconclusiveNeverClean()
    {
        var (world, org) = await DeliveredWorldAsync();
        var archiveDown = await world.Baseline(archive: new UnavailableSecurityEventArchive("test outage"))
            .RunAsync(JournalVerificationWorld.BaselineRequest(org, Audit), Ct);
        Assert.Equal(nameof(JournalBaselineOutcome.Inconclusive), archiveDown.Outcome);
        Assert.Contains(JournalInconclusiveReasons.ArchiveUnavailable, archiveDown.InconclusiveReasons);

        var storeDown = await world.Baseline(new UnavailableCheckpointStore()).RunAsync(JournalVerificationWorld.BaselineRequest(org, Audit), Ct);
        Assert.Equal(nameof(JournalBaselineOutcome.Inconclusive), storeDown.Outcome);
        Assert.Contains(JournalInconclusiveReasons.CheckpointStoreUnavailable, storeDown.InconclusiveReasons);
    }

    [Fact]
    public async Task AnExistingChainIsNotRebaselinedUnlessExplicitlyRequestedTheContinuationIsOnlyProposed()
    {
        var (world, org) = await DeliveredWorldAsync();
        await RunAsync(world, org);
        world.Append(org, Audit, 2);
        await world.DeliverAsync(org, Audit);
        var again = await RunAsync(world, org);

        Assert.Equal(nameof(JournalBaselineOutcome.Clean), again.Outcome);
        Assert.Equal("NotCreated:REBASELINE_NOT_REQUESTED", again.CheckpointAction);
        Assert.Contains("sequence 2, walk kind Baseline", again.RebaselineProposal, StringComparison.Ordinal);
        var listed = await world.Checkpoints.ListAsync(org, Audit, null, 100, Ct);
        Assert.Single(listed.Objects); // history unchanged
    }

    [Fact]
    public async Task AConcurrentSweepOfTheSameStreamMakesTheBaselineInconclusive()
    {
        var (world, org) = await DeliveredWorldAsync();
        await using var holder = await world.Reader.OpenSnapshotAsync(Ct);
        Assert.True(await holder.TryAcquireSweepLockAsync(org, Audit, Ct));
        var report = await RunAsync(world, org);
        Assert.Equal(nameof(JournalBaselineOutcome.Inconclusive), report.Outcome);
        Assert.Contains(JournalInconclusiveReasons.SweepLockHeld, report.InconclusiveReasons);
    }

    private sealed class UnavailableCheckpointStore : IVerificationCheckpointStore
    {
        public string StoreName => "Unavailable";

        public Task<CheckpointCreateResult> CreateAsync(Guid organizationId, string streamName, long checkpointSequence, ReadOnlyMemory<byte> checkpointBytes, CancellationToken cancellationToken) =>
            throw new CheckpointStoreUnavailableException("test outage");

        public Task<StoredObjectRead> ReadAsync(Guid organizationId, string streamName, long checkpointSequence, CancellationToken cancellationToken) => throw new CheckpointStoreUnavailableException("test outage");

        public Task<CheckpointListingPage> ListAsync(Guid organizationId, string streamName, string? continuationToken, int pageSizeHint, CancellationToken cancellationToken) =>
            throw new CheckpointStoreUnavailableException("test outage");
    }
}
