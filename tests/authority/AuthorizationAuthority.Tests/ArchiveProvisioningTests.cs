using System.Text;
using AuthorizationAuthority.Application.Common.Ports;
using AuthorizationAuthority.Application.Features.Journal.Reconciliation;
using AuthorizationAuthority.Application.Features.Journal.Verification;
using AuthorizationAuthority.Domain.Features.Journal;
using AuthorizationAuthority.Infrastructure.Archive;
using Xunit;

namespace AuthorizationAuthority.Tests;

/// <summary>
/// SEC-BL-01 WP-5a.1 (amendment A-2 correction §22.8): explicit provisioning of a disposable archive. Only a demonstrably new instance is
/// provisioned (no record, no sentinel, no container, no journal rows, fresh store); established, partially or fully wiped, record-less,
/// mismatched, ambiguous and unavailable states are refused; concurrent runs converge or fail closed; create-only throughout.
/// </summary>
public sealed class ArchiveProvisioningTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed record Setup(JournalVerificationWorld World, InMemorySecurityEventArchive Archive, InMemoryArchiveProvisioningStore Store)
    {
        public ArchiveProvisioningService Service(ISecurityEventArchiveInventory? archive = null, IArchiveProvisioningStore? store = null) =>
            new(World.Reader, archive ?? Archive, store ?? Store, World.Clock);
    }

    private static Setup Fresh() => new(new JournalVerificationWorld(), new InMemorySecurityEventArchive(), new InMemoryArchiveProvisioningStore());

    private static Task<ArchiveProvisioningResult> RunAsync(ArchiveProvisioningService service) => service.ProvisionAsync("test-tool", Ct);

    private static async Task AssertNothingWrittenAsync(Setup s)
    {
        Assert.Equal(StoredObjectStatus.Absent, (await s.Store.ReadRecordAsync(Ct)).Status);
        Assert.Equal(StoredObjectStatus.Absent, (await s.Archive.ReadIdentityAsync(Ct)).Status);
    }

    [Fact]
    public async Task AFreshDisposableArchiveIsProvisionedWithTheRecordsInstanceIdentifierAndARerunIsIdempotent()
    {
        var s = Fresh();
        var first = await RunAsync(s.Service());
        Assert.Equal(ArchiveProvisioningOutcome.Provisioned, first.Outcome);
        var record = ArchiveProvisioningRecords.TryParse((await s.Store.ReadRecordAsync(Ct)).Bytes)!;
        var sentinel = (await s.Archive.ReadIdentityAsync(Ct)).Identity!;
        Assert.Equal(record.ArchiveInstanceId, sentinel.ArchiveInstanceId);
        Assert.Equal(first.ArchiveInstanceId, sentinel.ArchiveInstanceId);

        // The stack now runs: journal rows and archive objects appear. A rerun (stack restart) is idempotent and changes nothing.
        var org = s.World.AddOrganization();
        s.World.Append(org, JournalStreamNames.Audit, 3);
        foreach (var row in s.World.Rows)
        {
            await s.Archive.AppendAsync(org, JournalStreamNames.Audit, row.JournalStreamRevision, Encoding.UTF8.GetBytes(row.JournalEventBodyJson), row.JournalEventDigest, Ct);
        }

        var again = await RunAsync(s.Service());
        Assert.Equal((ArchiveProvisioningOutcome.AlreadyProvisioned, sentinel.ArchiveInstanceId), (again.Outcome, again.ArchiveInstanceId));
        Assert.Equal(sentinel, (await s.Archive.ReadIdentityAsync(Ct)).Identity);
    }

    [Fact]
    public async Task PlatformJournalActivityBeforeInitializationIsRefused()
    {
        var s = Fresh();
        s.World.PlatformJournalRows = 1;
        var result = await RunAsync(s.Service());
        Assert.Equal((ArchiveProvisioningOutcome.Refused, ArchiveProvisioningReasons.JournalActivityPresent), (result.Outcome, result.ReasonCode));
        await AssertNothingWrittenAsync(s);
    }

    [Fact]
    public async Task SecurityJournalActivityBeforeInitializationIsRefused()
    {
        var s = Fresh();
        var org = s.World.AddOrganization();
        s.World.Append(org, JournalStreamNames.Authorization, 1);
        var result = await RunAsync(s.Service());
        Assert.Equal(ArchiveProvisioningReasons.JournalActivityPresent, result.ReasonCode);
        await AssertNothingWrittenAsync(s);
    }

    [Fact]
    public async Task AnExistingArchiveWithoutASentinelIsNeverStamped()
    {
        var s = Fresh();
        s.Archive.PutObjectForTesting($"{Guid.CreateVersion7():D}/audit/1", "{}"u8.ToArray()); // established archive, no sentinel
        var result = await RunAsync(s.Service());
        Assert.Equal(ArchiveProvisioningReasons.ArchiveContainerExists, result.ReasonCode);
        await AssertNothingWrittenAsync(s);

        // Even platform-journal entries only (the WP-5a A-2 rule accepted this case; WP-5a.1 does not).
        var platform = Fresh();
        platform.Archive.PutObjectForTesting($"{Guid.Empty:D}/platform-operators/1", "{}"u8.ToArray());
        Assert.Equal(ArchiveProvisioningReasons.ArchiveContainerExists, (await RunAsync(platform.Service())).ReasonCode);
        await AssertNothingWrittenAsync(platform);
    }

    [Fact]
    public async Task AnArchiveWithASentinelButNoRecordIsRefused()
    {
        var s = Fresh();
        await s.Archive.CreateIdentityAsync(ArchiveIdentityCodec.NewIdentity(s.World.Clock.GetUtcNow()), Ct); // e.g. established by a baseline
        Assert.Equal(ArchiveProvisioningReasons.SentinelWithoutRecord, (await RunAsync(s.Service())).ReasonCode);
        Assert.Equal(StoredObjectStatus.Absent, (await s.Store.ReadRecordAsync(Ct)).Status);
    }

    [Fact]
    public async Task PartialAndFullArchiveLossAfterProvisioningAreRefused()
    {
        foreach (var loss in new[] { "sentinel-only", "partial", "full" })
        {
            var s = Fresh();
            Assert.Equal(ArchiveProvisioningOutcome.Provisioned, (await RunAsync(s.Service())).Outcome);
            var org = s.World.AddOrganization();
            s.World.Append(org, JournalStreamNames.Audit, 2);
            foreach (var row in s.World.Rows)
            {
                await s.Archive.AppendAsync(org, JournalStreamNames.Audit, row.JournalStreamRevision, Encoding.UTF8.GetBytes(row.JournalEventBodyJson), row.JournalEventDigest, Ct);
            }

            switch (loss)
            {
                case "sentinel-only":
                    s.Archive.RemoveIdentityForTesting();
                    break;
                case "partial":
                    s.Archive.RemoveIdentityForTesting();
                    s.Archive.RemoveObjectForTesting(JournalVerificationWorld.ArchiveKey(org, JournalStreamNames.Audit, 1));
                    break;
                default:
                    s.Archive.WipeForTesting(); // the container is gone too: deleting archive objects cannot make it look new
                    break;
            }

            var result = await RunAsync(s.Service());
            Assert.Equal((ArchiveProvisioningOutcome.Refused, ArchiveProvisioningReasons.ArchiveLostAfterProvisioning), (result.Outcome, result.ReasonCode));
            Assert.Equal(StoredObjectStatus.Absent, (await s.Archive.ReadIdentityAsync(Ct)).Status); // never re-stamped
        }
    }

    [Fact]
    public async Task ALostSentinelOnlyArchiveBeforeAnyActivityIsAmbiguousAndFailsClosed()
    {
        var s = Fresh();
        Assert.Equal(ArchiveProvisioningOutcome.Provisioned, (await RunAsync(s.Service())).Outcome);
        s.Archive.WipeForTesting(); // container gone, no journal rows yet: indistinguishable from an interrupted provisioning
        var result = await RunAsync(s.Service());
        Assert.Equal((ArchiveProvisioningOutcome.Refused, ArchiveProvisioningReasons.ProvisioningIncomplete), (result.Outcome, result.ReasonCode));
        Assert.Equal(StoredObjectStatus.Absent, (await s.Archive.ReadIdentityAsync(Ct)).Status);
    }

    [Fact]
    public async Task ProvisioningRecordLossIsRefused()
    {
        var s = Fresh();
        Assert.Equal(ArchiveProvisioningOutcome.Provisioned, (await RunAsync(s.Service())).Outcome);
        s.Store.RemoveRecordForTesting();
        Assert.Equal(ArchiveProvisioningReasons.SentinelWithoutRecord, (await RunAsync(s.Service())).ReasonCode);

        // Record and archive both gone, but the store still holds checkpoints or findings: not a fresh store.
        var used = Fresh();
        used.Store.HoldsOtherObjects = true;
        Assert.Equal(ArchiveProvisioningReasons.VerificationStoreNotFresh, (await RunAsync(used.Service())).ReasonCode);
        await AssertNothingWrittenAsync(used);
    }

    [Fact]
    public async Task AnInvalidRecordOrAMismatchedSentinelIsRefused()
    {
        var s = Fresh();
        await s.Store.CreateRecordAsync("{\"not\":\"a record\"}"u8.ToArray(), Ct);
        Assert.Equal(ArchiveProvisioningReasons.RecordInvalid, (await RunAsync(s.Service())).ReasonCode);

        var m = Fresh();
        Assert.Equal(ArchiveProvisioningOutcome.Provisioned, (await RunAsync(m.Service())).Outcome);
        m.Archive.RemoveIdentityForTesting();
        await m.Archive.CreateIdentityAsync(ArchiveIdentityCodec.NewIdentity(m.World.Clock.GetUtcNow()), Ct); // another instance
        Assert.Equal(ArchiveProvisioningReasons.ArchiveIdentityMismatch, (await RunAsync(m.Service())).ReasonCode);
    }

    [Fact]
    public async Task ConcurrentProvisioningConvergesOnOneIdentityOrFailsClosed()
    {
        for (var round = 0; round < 20; round++)
        {
            var s = Fresh();
            var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Task.Run(() => RunAsync(s.Service()), Ct)));
            Assert.Single(results, r => r.Outcome == ArchiveProvisioningOutcome.Provisioned);
            // Losers either see the finished winner or fail closed (incomplete, store not fresh, container exists), never a second identity.
            Assert.All(results, r => Assert.True(r.Outcome is ArchiveProvisioningOutcome.Provisioned or ArchiveProvisioningOutcome.AlreadyProvisioned or ArchiveProvisioningOutcome.Refused,
                r.Outcome + " " + r.ReasonCode));
            Assert.DoesNotContain(results, r => r.ReasonCode is ArchiveProvisioningReasons.ArchiveIdentityMismatch or ArchiveProvisioningReasons.RecordInvalid);
            var record = ArchiveProvisioningRecords.TryParse((await s.Store.ReadRecordAsync(Ct)).Bytes)!;
            Assert.Equal(record.ArchiveInstanceId, (await s.Archive.ReadIdentityAsync(Ct)).Identity!.ArchiveInstanceId);
            Assert.Equal(ArchiveProvisioningOutcome.AlreadyProvisioned, (await RunAsync(s.Service())).Outcome);
        }
    }

    [Fact]
    public async Task StorageAndEnumerationFailuresAreUnavailableNeverEmpty()
    {
        var s = Fresh();
        Assert.Equal(ArchiveProvisioningOutcome.Unavailable, (await RunAsync(s.Service(archive: new UnavailableSecurityEventArchive("archive down")))).Outcome);
        Assert.Equal(ArchiveProvisioningOutcome.Unavailable, (await RunAsync(s.Service(store: new UnavailableProvisioningStore()))).Outcome);
        Assert.Equal(ArchiveProvisioningOutcome.Unavailable, (await RunAsync(s.Service(archive: new FailingListingArchive(s.Archive)))).Outcome);
        await AssertNothingWrittenAsync(s);
    }

    private sealed class UnavailableProvisioningStore : IArchiveProvisioningStore
    {
        public Task<CheckpointCreateResult> CreateRecordAsync(ReadOnlyMemory<byte> recordBytes, CancellationToken cancellationToken) => throw new CheckpointStoreUnavailableException("store down");

        public Task<StoredObjectRead> ReadRecordAsync(CancellationToken cancellationToken) => throw new CheckpointStoreUnavailableException("store down");

        public Task<bool> HoldsAnyObjectAsync(CancellationToken cancellationToken) => throw new CheckpointStoreUnavailableException("store down");
    }

    /// <summary>Reads succeed, but the complete listing fails (an incompletely enumerated archive).</summary>
    private sealed class FailingListingArchive(InMemorySecurityEventArchive inner) : ISecurityEventArchiveInventory
    {
        public Task<ArchiveIdentityRead> ReadIdentityAsync(CancellationToken cancellationToken) => inner.ReadIdentityAsync(cancellationToken);

        public Task<ArchiveIdentityCreateResult> CreateIdentityAsync(ArchiveIdentity identity, CancellationToken cancellationToken) => inner.CreateIdentityAsync(identity, cancellationToken);

        public Task<StoredObjectRead> ReadEntryAsync(Guid organizationId, string streamName, long streamRevision, CancellationToken cancellationToken) =>
            inner.ReadEntryAsync(organizationId, streamName, streamRevision, cancellationToken);

        public Task<ArchiveListingPage> ListEntriesAsync(Guid organizationId, string? streamName, string? continuationToken, int pageSizeHint, CancellationToken cancellationToken) =>
            throw new ArchiveUnavailableException("listing failed");

        public Task<ArchiveListingPage> ListEntriesAboveAsync(Guid organizationId, string streamName, long afterRevision, string? continuationToken, int pageSizeHint, CancellationToken cancellationToken) =>
            throw new ArchiveUnavailableException("listing failed");

        public Task<ArchiveContentSummary> SummarizeAsync(CancellationToken cancellationToken) => throw new ArchiveUnavailableException("listing failed after two pages");

        public Task<CheckpointCreateResult> CreateStopMarkerAsync(Guid organizationId, string streamName, long failedRevision, ReadOnlyMemory<byte> markerBytes, CancellationToken cancellationToken) =>
            inner.CreateStopMarkerAsync(organizationId, streamName, failedRevision, markerBytes, cancellationToken);

        public Task<IReadOnlyList<ArchiveMetaObject>> ReadStopMarkersAsync(Guid organizationId, string streamName, CancellationToken cancellationToken) => throw new ArchiveUnavailableException("listing failed");

        public Task<IReadOnlyList<string>> ListMetaObjectNamesAsync(CancellationToken cancellationToken) => throw new ArchiveUnavailableException("listing failed");
    }
}
