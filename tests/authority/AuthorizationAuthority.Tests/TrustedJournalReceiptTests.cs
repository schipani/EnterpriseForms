using System.Security.Cryptography;
using System.Text;
using AuthorizationAuthority.Application.Common;
using AuthorizationAuthority.Application.Common.Ports;
using AuthorizationAuthority.Application.Features.Journal;
using AuthorizationAuthority.Application.Features.Journal.Recovery;
using AuthorizationAuthority.Application.Features.Journal.Verification;
using AuthorizationAuthority.Domain.Features.Journal;
using AuthorizationAuthority.Infrastructure.Archive;
using Xunit;

namespace AuthorizationAuthority.Tests;

/// <summary>
/// SEC-BL-01 E-6/E-7 (C-158, A-7 §27 TR-1..TR-8): the trusted delivery receipt. Genuine receipts come from real D+ delivery
/// (<see cref="ArchiveProcessor"/> with W2-S commitments in both stores and a pinned archive); each test then forges or removes one piece of
/// evidence. Only evidence held outside AuthorityDb can make a receipt verified; every forged AuthorityDb field is ignored.
/// </summary>
public sealed class TrustedJournalReceiptTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static TheoryData<string> Streams => [JournalStreamNames.Audit, JournalStreamNames.Authorization, JournalStreamNames.Content];

    private sealed class Rig
    {
        public Rig(string stream)
        {
            Stream = stream;
            Org = World.AddOrganization(freshAuditStream: true);
        }

        public CapabilityRefusalEvidenceWorld World { get; } = new();

        public InMemorySecurityEventArchive Archive { get; } = new();

        public Guid Org { get; }

        public string Stream { get; }

        public string Instance { get; set; } = string.Empty;

        public TrustedJournalReceipts Receipts(ISecurityEventArchive? over = null) => new(over ?? Archive, World.DPlus.Durability);

        public ArchiveProcessor Processor() => new(World.Store, Archive, World.Clock, new RecordingAlerts(), new InMemoryJournalVerificationFindingStore(), World.DPlus.Durability);

        public SecurityJournalEntry Append(string note = "event")
        {
            World.Clock.Advance(TimeSpan.FromSeconds(1));
            return World.SeedStream(Org, Stream, "TestEvent", new { note });
        }

        /// <summary>Two genuinely delivered entries (revisions 1 and 2) of the stream.</summary>
        public async Task<(SecurityJournalEntry First, SecurityJournalEntry Second)> DeliveredAsync()
        {
            var first = Append("one");
            var second = Append("two");
            Assert.Equal(2, await Processor().ProcessOrganizationAsync(Org, Ct));
            Assert.All([first, second], e => Assert.Equal(ArchiveDeliveryStates.Anchored, e.ArchiveDeliveryState));
            return (first, second);
        }

        public string CommitmentName(long revision, int generation = 0) => DPlusRecoveryNames.Commitment(Org, Stream, revision, generation);
    }

    private static async Task<Rig> RigAsync(string stream)
    {
        var rig = new Rig(stream);
        rig.Instance = await rig.World.DPlus.ProvisionAsync(rig.Archive, rig.World.Clock.GetUtcNow());
        return rig;
    }

    private static void AssertNot(JournalReceiptVerdict verdict, string code)
    {
        Assert.False(verdict.IsVerified, "unexpectedly verified");
        Assert.Equal(code, verdict.Code);
    }

    [Theory]
    [MemberData(nameof(Streams))]
    public async Task AGenuinelyDeliveredEntryHasAVerifiedReceiptBoundToTheInstanceAndGeneration(string stream)
    {
        var rig = await RigAsync(stream);
        var (_, second) = await rig.DeliveredAsync();
        var verdict = await rig.Receipts().VerifyAsync(second, Ct);
        Assert.True(verdict.IsVerified, verdict.Code);
        Assert.Equal((0, rig.Instance), (verdict.StopGeneration, verdict.ArchiveInstanceId));
    }

    /// <summary>Forged delivery state, receipt identifiers, receipt digests and timestamps on an undelivered entry are never a receipt.</summary>
    [Theory]
    [MemberData(nameof(Streams))]
    public async Task ForgedAnchoredStateReceiptIdentifiersDigestsAndTimestampsAreNotAReceipt(string stream)
    {
        var rig = await RigAsync(stream);
        await rig.DeliveredAsync();
        var pending = rig.Append("never delivered");
        AssertNot(await rig.Receipts().VerifyAsync(pending, Ct), JournalReceiptCodes.NotDelivered);
        pending.ArchiveDeliveryState = ArchiveDeliveryStates.Anchored; // J1
        pending.ArchiveReceiptReference = "receipt-of-another-entry";
        pending.ArchiveReceiptDigest = SHA256.HashData("forged"u8);
        pending.JournalArchivedOn = rig.World.Clock.GetUtcNow().AddDays(-3);
        AssertNot(await rig.Receipts().VerifyAsync(pending, Ct), JournalReceiptCodes.ArchiveAbsent);
    }

    /// <summary>A row altered after its receipt was established (J2 rewrites body and digest) misses the cache and fails against the archive.</summary>
    [Theory]
    [MemberData(nameof(Streams))]
    public async Task EvidenceChangedAfterTheReceiptWasEstablishedIsNotVerifiedAndTheCacheIsNotStale(string stream)
    {
        var rig = await RigAsync(stream);
        var (first, _) = await rig.DeliveredAsync();
        var receipts = rig.Receipts();
        Assert.True((await receipts.VerifyAsync(first, Ct)).IsVerified);
        first.JournalEventBodyJson = first.JournalEventBodyJson.Replace("one", "ONE", StringComparison.Ordinal);
        AssertNot(await receipts.VerifyAsync(first, Ct), JournalReceiptCodes.RowDigest); // body no longer matches its digest
        first.JournalEventDigest = CanonicalJson.Digest(Encoding.UTF8.GetBytes(first.JournalEventBodyJson)); // consistently re-digested
        AssertNot(await receipts.VerifyAsync(first, Ct), JournalReceiptCodes.ArchiveMismatch);
    }

    /// <summary>A restart starts with an empty cache and re-establishes receipts from the stores only.</summary>
    [Theory]
    [MemberData(nameof(Streams))]
    public async Task ARestartReEstablishesReceiptsFromTheStoresOnly(string stream)
    {
        var rig = await RigAsync(stream);
        var (_, second) = await rig.DeliveredAsync();
        var before = rig.Receipts();
        Assert.True((await before.VerifyAsync(second, Ct)).IsVerified);
        Assert.True((await before.VerifyAsync(second, Ct)).IsVerified);
        Assert.Equal(1, before.StoreVerifications); // the second check was served from the cache

        var after = rig.Receipts(); // a restart
        Assert.True((await after.VerifyAsync(second, Ct)).IsVerified);
        Assert.Equal(1, after.StoreVerifications);
        Assert.True(rig.Archive.RemoveObjectForTesting($"{rig.Org:D}/{rig.Stream}/2"));
        AssertNot(await rig.Receipts().VerifyAsync(second, Ct), JournalReceiptCodes.ArchiveAbsent); // and nothing survives in AuthorityDb
    }

    /// <summary>Archive, verification-store and pin outages are Unavailable (not delivered), never "absent", and nothing is cached.</summary>
    [Theory]
    [MemberData(nameof(Streams))]
    public async Task OutagesAreUnavailableAndNeverCached(string stream)
    {
        var rig = await RigAsync(stream);
        var (_, second) = await rig.DeliveredAsync();
        var down = rig.Receipts(ContentStreamDPlusReadabilityTests.OutageArchive.Create());
        Assert.Equal(JournalReceiptStatus.Unavailable, (await down.VerifyAsync(second, Ct)).Status);
        Assert.Equal(0, down.CachedReceipts);

        var receipts = rig.Receipts();
        rig.World.DPlus.Records.Unavailable = true;
        Assert.Equal((JournalReceiptStatus.Unavailable, JournalReceiptCodes.StoreUnavailable), ((await receipts.VerifyAsync(second, Ct)).Status, (await receipts.VerifyAsync(second, Ct)).Code));
        rig.World.DPlus.Records.Unavailable = false;
        Assert.True((await receipts.VerifyAsync(second, Ct)).IsVerified);
    }

    /// <summary>A substituted archive (another instance's sentinel) or a missing pin is never a receipt.</summary>
    [Theory]
    [MemberData(nameof(Streams))]
    public async Task ArchiveInstanceSubstitutionAndAMissingPinAreNotAReceipt(string stream)
    {
        var rig = await RigAsync(stream);
        var (_, second) = await rig.DeliveredAsync();
        rig.Archive.RemoveIdentityForTesting();
        await rig.Archive.CreateIdentityAsync(ArchiveIdentityCodec.NewIdentity(rig.World.Clock.GetUtcNow()), Ct);
        AssertNot(await rig.Receipts().VerifyAsync(second, Ct), JournalReceiptCodes.InstanceMismatch);

        var unpinned = new CapabilityRefusalEvidenceWorld();
        AssertNot(await new TrustedJournalReceipts(rig.Archive, unpinned.DPlus.Durability).VerifyAsync(second, Ct), JournalReceiptCodes.InstanceUnpinned);
    }

    /// <summary>The W2-S commitment binds the receipt: missing, single-copy, contradictory, mismatching or superseded by a higher generation that names other evidence.</summary>
    [Theory]
    [MemberData(nameof(Streams))]
    public async Task TheCommitmentAtTheHighestStopGenerationMustNameTheRowInBothStores(string stream)
    {
        var rig = await RigAsync(stream);
        var (first, second) = await rig.DeliveredAsync();
        var name = rig.CommitmentName(2);
        var archiveCopy = rig.Archive.MetaForTesting[DPlusRecoveryNames.ArchiveCommitmentPrefix + name];

        rig.World.DPlus.Records.RemoveForTesting(DPlusRecoveryNames.StoreCommitmentPrefix + name);
        AssertNot(await rig.Receipts().VerifyAsync(second, Ct), JournalReceiptCodes.CommitmentMissing); // one copy only
        rig.Archive.RemoveMetaForTesting(DPlusRecoveryNames.ArchiveCommitmentPrefix + name);
        AssertNot(await rig.Receipts().VerifyAsync(second, Ct), JournalReceiptCodes.CommitmentMissing); // none (e.g. delivered before W2-S)

        var commitment = DPlusRecoveryCodec.TryParse<DPlusEvidenceCommitment>(archiveCopy)!;
        rig.Archive.PutMetaForTesting(DPlusRecoveryNames.ArchiveCommitmentPrefix + name, archiveCopy);
        rig.World.DPlus.Records.PutForTesting(DPlusRecoveryNames.StoreCommitmentPrefix + name, DPlusRecoveryCodec.Serialize(commitment with { RowBodySha256Hex = new string('a', 64) }));
        AssertNot(await rig.Receipts().VerifyAsync(second, Ct), JournalReceiptCodes.CommitmentContradictory);

        foreach (var forged in new[] { commitment with { RowEntryId = Guid.CreateVersion7() }, commitment with { ArchiveInstanceId = "another-instance" }, commitment with { RowPreviousDigestHex = new string('b', 64) } })
        {
            var bytes = DPlusRecoveryCodec.Serialize(forged);
            rig.Archive.PutMetaForTesting(DPlusRecoveryNames.ArchiveCommitmentPrefix + name, bytes);
            rig.World.DPlus.Records.PutForTesting(DPlusRecoveryNames.StoreCommitmentPrefix + name, bytes);
            AssertNot(await rig.Receipts().VerifyAsync(second, Ct), JournalReceiptCodes.CommitmentMismatch);
        }

        // A recovery generation change: a generation-1 commitment that names other row bytes supersedes the genuine generation-0 one.
        rig.Archive.PutMetaForTesting(DPlusRecoveryNames.ArchiveCommitmentPrefix + name, archiveCopy);
        rig.World.DPlus.Records.PutForTesting(DPlusRecoveryNames.StoreCommitmentPrefix + name, archiveCopy);
        Assert.True((await rig.Receipts().VerifyAsync(second, Ct)).IsVerified);
        var nextGeneration = DPlusRecoveryCodec.Serialize(commitment with { StopGeneration = 1, RowBodySha256Hex = new string('c', 64) });
        rig.Archive.PutMetaForTesting(DPlusRecoveryNames.ArchiveCommitmentPrefix + rig.CommitmentName(2, 1), nextGeneration);
        rig.World.DPlus.Records.PutForTesting(DPlusRecoveryNames.StoreCommitmentPrefix + rig.CommitmentName(2, 1), nextGeneration);
        AssertNot(await rig.Receipts().VerifyAsync(second, Ct), JournalReceiptCodes.CommitmentMismatch);
        Assert.True((await rig.Receipts().VerifyAsync(first, Ct)).IsVerified); // other revisions are unaffected
    }

    /// <summary>Evidence of one organization or stream never verifies another's entry (the key and the commitment bind both).</summary>
    [Theory]
    [MemberData(nameof(Streams))]
    public async Task EvidenceIsNeverReusedAcrossOrganizationsOrStreams(string stream)
    {
        var rig = await RigAsync(stream);
        var (_, second) = await rig.DeliveredAsync();
        var otherOrg = new SecurityJournalEntry
        {
            SecurityJournalEntryId = second.SecurityJournalEntryId, OrganizationId = Guid.CreateVersion7(), JournalStreamName = second.JournalStreamName,
            JournalStreamRevision = second.JournalStreamRevision, JournalEventKind = second.JournalEventKind, JournalEventBodyJson = second.JournalEventBodyJson,
            JournalEventDigest = second.JournalEventDigest, JournalPreviousDigest = second.JournalPreviousDigest, ArchiveDeliveryState = ArchiveDeliveryStates.Anchored,
        };
        AssertNot(await rig.Receipts().VerifyAsync(otherOrg, Ct), JournalReceiptCodes.ArchiveAbsent);

        var otherStream = AuditDeliveryEnforcement.KnownStreams.First(s => s != stream);
        var copied = await rig.Archive.ReadAsync(rig.Org, stream, 2, Ct);
        await rig.Archive.AppendAsync(rig.Org, otherStream, 2, copied!, SHA256.HashData(copied!), Ct); // J4 plants the bytes under the other stream
        var crossStream = new SecurityJournalEntry
        {
            SecurityJournalEntryId = second.SecurityJournalEntryId, OrganizationId = rig.Org, JournalStreamName = otherStream, JournalStreamRevision = 2,
            JournalEventKind = second.JournalEventKind, JournalEventBodyJson = second.JournalEventBodyJson, JournalEventDigest = second.JournalEventDigest,
            JournalPreviousDigest = second.JournalPreviousDigest, ArchiveDeliveryState = ArchiveDeliveryStates.Anchored,
        };
        AssertNot(await rig.Receipts().VerifyAsync(crossStream, Ct), JournalReceiptCodes.CommitmentMissing); // no commitment for that stream
    }

    [Fact]
    public async Task WithoutDPlusDurabilityNothingIsEverVerified()
    {
        var rig = await RigAsync(JournalStreamNames.Authorization);
        var (_, second) = await rig.DeliveredAsync();
        AssertNot(await new TrustedJournalReceipts(rig.Archive, null).VerifyAsync(second, Ct), JournalReceiptCodes.NotComposed);
        AssertNot(await NotComposedJournalReceiptVerifier.Instance.VerifyAsync(second, Ct), JournalReceiptCodes.NotComposed);
    }

    [Fact]
    public void TheCacheIsBoundedAndTheFreshnessBoundHasAHardCeiling()
    {
        var defaults = new TrustedJournalReceiptOptions();
        Assert.Equal(200_000, defaults.CacheLimit); // TR-7
        Assert.Equal(TimeSpan.FromSeconds(1), defaults.CurrentStateMaxAge); // WP-7A RV-2 default (D-7A-1)
        Assert.Equal(TimeSpan.FromSeconds(30), TrustedJournalReceiptOptions.MaximumCurrentStateMaxAge);
        var archive = new InMemorySecurityEventArchive();
        Assert.Throws<InvalidOperationException>(() => new TrustedJournalReceipts(archive, null, new TrustedJournalReceiptOptions { CurrentStateMaxAge = TimeSpan.FromSeconds(31) }));
        Assert.Throws<InvalidOperationException>(() => new TrustedJournalReceipts(archive, null, new TrustedJournalReceiptOptions { CurrentStateMaxAge = TimeSpan.FromMilliseconds(-1) }));
        Assert.Throws<InvalidOperationException>(() => new TrustedJournalReceipts(archive, null, new TrustedJournalReceiptOptions { CacheLimit = 0 }));
        _ = new TrustedJournalReceipts(archive, null, new TrustedJournalReceiptOptions { CurrentStateMaxAge = TimeSpan.Zero });
    }

    /// <summary>The recovery hint never fabricates a receipt: after an SR-C1 lift and before delivery the entry is Pending and unverified.</summary>
    [Fact]
    public async Task RecoveryNeverFabricatesAReceipt()
    {
        var rig = await RigAsync(JournalStreamNames.Authorization);
        var (_, second) = await rig.DeliveredAsync();
        Assert.True(rig.Archive.RemoveObjectForTesting($"{rig.Org:D}/{rig.Stream}/2")); // J4
        var third = rig.Append("three");
        Assert.Equal(0, await rig.Processor().ProcessOrganizationAsync(rig.Org, Ct));
        third.ArchiveDeliveryState = ArchiveDeliveryStates.Pending; // what the recovery hint leaves
        third.LastArchiveErrorCode = ArchiveProcessor.RecoveryAuthorizedCodePrefix + Guid.CreateVersion7().ToString("D");
        AssertNot(await rig.Receipts().VerifyAsync(third, Ct), JournalReceiptCodes.NotDelivered);
        third.ArchiveDeliveryState = ArchiveDeliveryStates.Anchored;
        AssertNot(await rig.Receipts().VerifyAsync(third, Ct), JournalReceiptCodes.ArchiveAbsent);
        AssertNot(await rig.Receipts().VerifyAsync(second, Ct), JournalReceiptCodes.ArchiveAbsent);
    }
}
