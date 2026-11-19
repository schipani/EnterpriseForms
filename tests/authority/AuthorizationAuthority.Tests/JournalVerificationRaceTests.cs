using AuthorizationAuthority.Application.Common.Ports;
using AuthorizationAuthority.Application.Features.Journal.Reconciliation;
using AuthorizationAuthority.Domain.Features.Journal;
using AuthorizationAuthority.Infrastructure.Archive;
using Xunit;

namespace AuthorizationAuthority.Tests;

/// <summary>
/// SEC-BL-01 WP-7 (C-159; VF-8, mutation MV-18): an entry genuinely delivered between the sweep's archive listing and its row walk is not a
/// fake receipt. The row is <c>Anchored</c> above the listed head; the second read finds the object, so the sweep is Inconclusive
/// (<c>ARCHIVE_CHANGED_DURING_SWEEP</c>) and creates no checkpoint; the next sweep is Clean. Without the second read the race would be
/// reported as <c>FAKE_RECEIPT</c> (a false security finding).
/// </summary>
public sealed class JournalVerificationRaceTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>The archive as a listing taken just before <paramref name="hidden"/> was appended would show it: every other operation is live.</summary>
    private sealed class ListingBeforeAppendArchive(InMemorySecurityEventArchive inner, long hidden) : ISecurityEventArchiveInventory
    {
        private static ArchiveListingPage Hide(ArchiveListingPage page, long revision) =>
            page with { Objects = [.. page.Objects.Where(o => o.StreamRevision != revision)] };

        public Task<ArchiveIdentityRead> ReadIdentityAsync(CancellationToken cancellationToken) => inner.ReadIdentityAsync(cancellationToken);

        public Task<ArchiveIdentityCreateResult> CreateIdentityAsync(ArchiveIdentity identity, CancellationToken cancellationToken) => inner.CreateIdentityAsync(identity, cancellationToken);

        public Task<StoredObjectRead> ReadEntryAsync(Guid organizationId, string streamName, long streamRevision, CancellationToken cancellationToken) =>
            inner.ReadEntryAsync(organizationId, streamName, streamRevision, cancellationToken);

        public async Task<ArchiveListingPage> ListEntriesAsync(Guid organizationId, string? streamName, string? continuationToken, int pageSizeHint, CancellationToken cancellationToken) =>
            Hide(await inner.ListEntriesAsync(organizationId, streamName, continuationToken, pageSizeHint, cancellationToken), hidden);

        public async Task<ArchiveListingPage> ListEntriesAboveAsync(Guid organizationId, string streamName, long afterRevision, string? continuationToken, int pageSizeHint, CancellationToken cancellationToken) =>
            Hide(await inner.ListEntriesAboveAsync(organizationId, streamName, afterRevision, continuationToken, pageSizeHint, cancellationToken), hidden);

        public Task<ArchiveContentSummary> SummarizeAsync(CancellationToken cancellationToken) => inner.SummarizeAsync(cancellationToken);

        public Task<CheckpointCreateResult> CreateStopMarkerAsync(Guid organizationId, string streamName, long failedRevision, ReadOnlyMemory<byte> markerBytes, CancellationToken cancellationToken) =>
            inner.CreateStopMarkerAsync(organizationId, streamName, failedRevision, markerBytes, cancellationToken);

        public Task<IReadOnlyList<ArchiveMetaObject>> ReadStopMarkersAsync(Guid organizationId, string streamName, CancellationToken cancellationToken) =>
            inner.ReadStopMarkersAsync(organizationId, streamName, cancellationToken);

        public Task<IReadOnlyList<string>> ListMetaObjectNamesAsync(CancellationToken cancellationToken) => inner.ListMetaObjectNamesAsync(cancellationToken);
    }

    [Theory]
    [InlineData(JournalStreamNames.Audit)]
    [InlineData(JournalStreamNames.Authorization)]
    [InlineData(JournalStreamNames.Content)]
    public async Task AnEntryDeliveredBetweenTheListingAndTheRowWalkIsInconclusiveNeverAFakeReceipt(string stream)
    {
        var world = new JournalVerificationWorld();
        await world.CreateSentinelAsync();
        var org = world.AddOrganization();
        world.Append(org, stream, 5);
        await world.DeliverAsync(org, stream);
        Assert.Equal(nameof(JournalBaselineOutcome.Clean), (await world.Baseline().RunAsync(JournalVerificationWorld.BaselineRequest(org, stream, createSentinel: false), Ct)).Outcome);

        world.Append(org, stream, 1);
        await world.DeliverAsync(org, stream); // revision 6 archived and Anchored after the listing below was taken
        var raced = await world.Verifier(archive: new ListingBeforeAppendArchive(world.Archive, 6)).SweepAsync(org, stream, JournalSweepKind.Incremental, Ct);
        Assert.Equal(JournalSweepOutcome.Inconclusive, raced.Outcome);
        Assert.Contains(JournalInconclusiveReasons.ArchiveChangedDuringSweep + "@6", raced.InconclusiveReasons);
        Assert.DoesNotContain(raced.Findings, f => f.FindingCode == JournalFindingCodes.FakeReceipt);
        Assert.Null(raced.NewCheckpoint);

        var next = await world.Verifier().SweepAsync(org, stream, JournalSweepKind.Incremental, Ct);
        Assert.Equal((JournalSweepOutcome.Clean, 6L), (next.Outcome, next.NewCheckpoint!.VerifiedRevision));
    }

    /// <summary>Negative control: the same row with no archived object is a fake receipt (the second read also finds nothing).</summary>
    [Fact]
    public async Task TheSameRowWithNoArchivedObjectIsAFakeReceipt()
    {
        var world = new JournalVerificationWorld();
        await world.CreateSentinelAsync();
        var org = world.AddOrganization();
        world.Append(org, JournalStreamNames.Audit, 5);
        await world.DeliverAsync(org, JournalStreamNames.Audit);
        Assert.Equal(nameof(JournalBaselineOutcome.Clean), (await world.Baseline().RunAsync(JournalVerificationWorld.BaselineRequest(org, JournalStreamNames.Audit, createSentinel: false), Ct)).Outcome);
        var forged = world.Append(org, JournalStreamNames.Audit, 1)[0];
        forged.ArchiveDeliveryState = ArchiveDeliveryStates.Anchored; // J1
        forged.ArchiveReceiptReference = "forged";
        var run = await world.Verifier().SweepAsync(org, JournalStreamNames.Audit, JournalSweepKind.Incremental, Ct);
        Assert.Contains(run.Findings, f => f.FindingCode == JournalFindingCodes.FakeReceipt && f.FirstRevision == 6);
    }
}
