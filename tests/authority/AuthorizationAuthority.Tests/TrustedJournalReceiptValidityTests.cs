using System.Security.Cryptography;
using AuthorizationAuthority.Application.Common.Ports;
using AuthorizationAuthority.Application.Features.Journal;
using AuthorizationAuthority.Application.Features.Journal.Recovery;
using AuthorizationAuthority.Application.Features.Journal.Verification;
using AuthorizationAuthority.Domain.Features.Journal;
using AuthorizationAuthority.Infrastructure.Archive;
using Xunit;

namespace AuthorizationAuthority.Tests;

/// <summary>
/// SEC-BL-01 WP-7A (C-160, A-9 TR-6′ RV-1..RV-9): the receipt validity model that remediates F-1. A verified receipt answers protected access
/// only while its current-state attestation is at most <see cref="TrustedJournalReceiptOptions.CurrentStateMaxAge"/> old; evidence that verified
/// a key and later fails or changes is refused for the process lifetime; a stream head is refused while successor evidence exists. Each test
/// forges or removes one piece of evidence after a genuine D+ delivery, on every stream.
/// </summary>
public sealed class TrustedJournalReceiptValidityTests
{
    private static readonly TimeSpan Phi = TimeSpan.FromSeconds(1);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static TheoryData<string> Streams => [JournalStreamNames.Audit, JournalStreamNames.Authorization, JournalStreamNames.Content];

    /// <summary>F-1 attack variants: the J4/J5/J6 change made after a receipt was established, and what it does to the key's own evidence.</summary>
    public static TheoryData<string, string> Attacks
    {
        get
        {
            var data = new TheoryData<string, string>();
            foreach (var stream in new[] { JournalStreamNames.Audit, JournalStreamNames.Authorization, JournalStreamNames.Content })
            {
                foreach (var attack in new[] { "instance-replaced", "sentinel-deleted", "object-deleted", "object-replaced", "commitment-archive-deleted", "commitment-store-deleted",
                    "commitment-modified", "generation-substituted", "generation-substituted-with-stop", "pin-record-deleted" })
                {
                    data.Add(stream, attack);
                }
            }

            return data;
        }
    }

    private sealed class Rig(string stream)
    {
        public CapabilityRefusalEvidenceWorld World { get; } = new();

        public InMemorySecurityEventArchive Archive { get; } = new();

        public Guid Org { get; private set; }

        public string Stream { get; } = stream;

        public TrustedJournalReceipts Receipts(TimeSpan? maxAge = null) =>
            new(Archive, World.DPlus.Durability, new TrustedJournalReceiptOptions { CurrentStateMaxAge = maxAge ?? Phi }, World.Clock);

        public ArchiveProcessor Processor() => new(World.Store, Archive, World.Clock, new RecordingAlerts(), new InMemoryJournalVerificationFindingStore(), World.DPlus.Durability);

        public SecurityJournalEntry Append(string note)
        {
            World.Clock.Advance(TimeSpan.FromSeconds(1));
            return World.SeedStream(Org, Stream, "TestEvent", new { note });
        }

        public string ObjectKey(long revision) => $"{Org:D}/{Stream}/{revision}";

        public string Commitment(long revision, int generation = 0) => DPlusRecoveryNames.Commitment(Org, Stream, revision, generation);

        public static async Task<Rig> CreateAsync(string stream)
        {
            var rig = new Rig(stream);
            rig.Org = rig.World.AddOrganization(freshAuditStream: true);
            await rig.World.DPlus.ProvisionAsync(rig.Archive, rig.World.Clock.GetUtcNow());
            return rig;
        }

        /// <summary>Two genuinely delivered entries (revisions 1 and 2).</summary>
        public async Task<(SecurityJournalEntry First, SecurityJournalEntry Second)> DeliveredAsync()
        {
            var first = Append("one");
            var second = Append("two");
            Assert.Equal(2, await Processor().ProcessOrganizationAsync(Org, Ct));
            Assert.All([first, second], e => Assert.Equal(ArchiveDeliveryStates.Anchored, e.ArchiveDeliveryState));
            return (first, second);
        }

        public async Task ApplyAsync(string attack)
        {
            var name = Commitment(2);
            switch (attack)
            {
                case "instance-replaced": // J4/J5: another archive instance under the same endpoint
                    Archive.RemoveIdentityForTesting();
                    await Archive.CreateIdentityAsync(ArchiveIdentityCodec.NewIdentity(World.Clock.GetUtcNow()), Ct);
                    break;
                case "sentinel-deleted":
                    Archive.RemoveIdentityForTesting();
                    break;
                case "object-deleted":
                    Assert.True(Archive.RemoveObjectForTesting(ObjectKey(2)));
                    break;
                case "object-replaced":
                    Archive.PutObjectForTesting(ObjectKey(2), "{\"forged\":true}"u8.ToArray());
                    break;
                case "commitment-archive-deleted":
                    Assert.True(Archive.RemoveMetaForTesting(DPlusRecoveryNames.ArchiveCommitmentPrefix + name));
                    break;
                case "commitment-store-deleted":
                    Assert.True(World.DPlus.Records.RemoveForTesting(DPlusRecoveryNames.StoreCommitmentPrefix + name));
                    break;
                case "commitment-modified": // J6: both copies, consistently, naming other evidence
                {
                    var commitment = DPlusRecoveryCodec.TryParse<DPlusEvidenceCommitment>(Archive.MetaForTesting[DPlusRecoveryNames.ArchiveCommitmentPrefix + name])!;
                    var bytes = DPlusRecoveryCodec.Serialize(commitment with { ArchiveInstanceId = "another-instance" });
                    Archive.PutMetaForTesting(DPlusRecoveryNames.ArchiveCommitmentPrefix + name, bytes);
                    World.DPlus.Records.PutForTesting(DPlusRecoveryNames.StoreCommitmentPrefix + name, bytes);
                    break;
                }

                case "generation-substituted": // a recovery-generation commitment that names the same row: still a change of the established evidence
                {
                    var commitment = DPlusRecoveryCodec.TryParse<DPlusEvidenceCommitment>(Archive.MetaForTesting[DPlusRecoveryNames.ArchiveCommitmentPrefix + name])!;
                    var bytes = DPlusRecoveryCodec.Serialize(commitment with { StopGeneration = 1 });
                    Archive.PutMetaForTesting(DPlusRecoveryNames.ArchiveCommitmentPrefix + Commitment(2, 1), bytes);
                    World.DPlus.Records.PutForTesting(DPlusRecoveryNames.StoreCommitmentPrefix + Commitment(2, 1), bytes);
                    break;
                }

                case "generation-substituted-with-stop": // J4 + J6: a forged gen-0 stop marker makes the gen-1 commitment verify on its own; only RV-6 sees the change
                {
                    var commitment = DPlusRecoveryCodec.TryParse<DPlusEvidenceCommitment>(Archive.MetaForTesting[DPlusRecoveryNames.ArchiveCommitmentPrefix + name])!;
                    var bytes = DPlusRecoveryCodec.Serialize(commitment with { StopGeneration = 1 });
                    Archive.PutMetaForTesting(DPlusStreamStops.MarkerObjectName(Org, Stream, 2, 0), "{}"u8.ToArray());
                    Archive.PutMetaForTesting(DPlusRecoveryNames.ArchiveCommitmentPrefix + Commitment(2, 1), bytes);
                    World.DPlus.Records.PutForTesting(DPlusRecoveryNames.StoreCommitmentPrefix + Commitment(2, 1), bytes);
                    break;
                }

                case "pin-record-deleted": // the trusted expected instance itself disappears (J6)
                    World.DPlus.Provisioning.RemoveRecordForTesting();
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(attack), attack, null);
            }
        }
    }

    /// <summary>
    /// F-1, after remediation: the original attack (a change of the external evidence after the receipt of an unchanged entry was cached) is
    /// honoured for at most the freshness bound, then refused, for the head and for any entry; restoring the evidence does not re-open it in the
    /// same process (RV-6). A restart re-establishes from the stores only (RV-9).
    /// </summary>
    [Theory]
    [MemberData(nameof(Attacks))]
    public async Task F1AChangedExternalEvidenceIsRefusedWithinTheFreshnessBoundAndStaysRefused(string stream, string attack)
    {
        var rig = await Rig.CreateAsync(stream);
        var (_, second) = await rig.DeliveredAsync();
        var receipts = rig.Receipts();
        Assert.True((await receipts.VerifyStreamHeadAsync(second, Ct)).IsVerified);
        Assert.True((await receipts.VerifyAsync(second, Ct)).IsVerified);
        var established = receipts.StoreVerifications;

        await rig.ApplyAsync(attack);

        // Within the bound: the earlier attestation still answers (this is the bounded staleness RV-2 permits, never longer).
        rig.World.Clock.Advance(Phi - TimeSpan.FromMilliseconds(1));
        Assert.True((await receipts.VerifyStreamHeadAsync(second, Ct)).IsVerified);
        Assert.Equal(established, receipts.StoreVerifications);

        // Past the bound: re-established from the stores and refused; never a receipt again in this process.
        rig.World.Clock.Advance(TimeSpan.FromMilliseconds(2));
        var head = await receipts.VerifyStreamHeadAsync(second, Ct);
        Assert.False(head.IsVerified, attack);
        Assert.Equal(JournalReceiptCodes.EvidenceWithdrawn, head.Code);
        Assert.False((await receipts.VerifyAsync(second, Ct)).IsVerified);
        rig.World.Clock.Advance(TimeSpan.FromMinutes(10));
        Assert.Equal(JournalReceiptCodes.EvidenceWithdrawn, (await receipts.VerifyStreamHeadAsync(second, Ct)).Code);

        // A restart starts empty and decides from the stores only (RV-9). The with-stop generation substitution is self-consistent evidence, so a
        // fresh process verifies it at generation 1 (limit L-9; the verifier's sweeps report the forged stop and commitment, VC-5/SM-7).
        var fresh = await rig.Receipts().VerifyStreamHeadAsync(second, Ct);
        Assert.Equal(attack != "generation-substituted-with-stop", !fresh.IsVerified);
    }

    /// <summary>F-1 with <c>CurrentStateMaxAge = 0</c>: every access re-establishes, so the change is refused at the very next access.</summary>
    [Theory]
    [MemberData(nameof(Attacks))]
    public async Task F1WithAZeroBoundTheNextAccessIsRefused(string stream, string attack)
    {
        var rig = await Rig.CreateAsync(stream);
        var (_, second) = await rig.DeliveredAsync();
        var receipts = rig.Receipts(TimeSpan.Zero);
        Assert.True((await receipts.VerifyStreamHeadAsync(second, Ct)).IsVerified);
        rig.World.Clock.Advance(TimeSpan.FromTicks(1));
        Assert.True((await receipts.VerifyStreamHeadAsync(second, Ct)).IsVerified);
        Assert.Equal(2, receipts.StoreVerifications); // re-established on every access

        await rig.ApplyAsync(attack);
        rig.World.Clock.Advance(TimeSpan.FromTicks(1));
        Assert.Equal(JournalReceiptCodes.EvidenceWithdrawn, (await receipts.VerifyStreamHeadAsync(second, Ct)).Code);
    }

    /// <summary>RV-6: restoring withdrawn evidence never re-opens the key in the same process; a restart sees the restored evidence (RV-9 limit).</summary>
    [Theory]
    [MemberData(nameof(Streams))]
    public async Task RestoredEvidenceStaysRefusedUntilARestart(string stream)
    {
        var rig = await Rig.CreateAsync(stream);
        var (_, second) = await rig.DeliveredAsync();
        var receipts = rig.Receipts();
        Assert.True((await receipts.VerifyStreamHeadAsync(second, Ct)).IsVerified);
        var original = rig.Archive.MetaForTesting.Count; // unchanged below
        var bytes = (await rig.Archive.ReadAsync(rig.Org, stream, 2, Ct))!;
        Assert.True(rig.Archive.RemoveObjectForTesting(rig.ObjectKey(2)));
        rig.World.Clock.Advance(Phi + TimeSpan.FromMilliseconds(1));
        Assert.Equal(JournalReceiptCodes.EvidenceWithdrawn, (await receipts.VerifyStreamHeadAsync(second, Ct)).Code);

        rig.Archive.PutObjectForTesting(rig.ObjectKey(2), bytes); // the attacker puts it back
        rig.World.Clock.Advance(Phi + TimeSpan.FromMilliseconds(1));
        Assert.Equal(JournalReceiptCodes.EvidenceWithdrawn, (await receipts.VerifyStreamHeadAsync(second, Ct)).Code);
        Assert.Equal(original, rig.Archive.MetaForTesting.Count);

        Assert.True((await rig.Receipts().VerifyStreamHeadAsync(second, Ct)).IsVerified); // RV-9: a restart decides from the stores only
    }

    /// <summary>RV-4: a claimed stream head is refused while any successor evidence exists outside AuthorityDb; an entry receipt is not.</summary>
    [Theory]
    [MemberData(nameof(Streams))]
    public async Task AStreamHeadWithSuccessorEvidenceIsRefusedAndTheRefusalIsPositionalOnly(string stream)
    {
        var rig = await Rig.CreateAsync(stream);
        var (first, second) = await rig.DeliveredAsync();
        var receipts = rig.Receipts(TimeSpan.Zero);

        // Revision 1 is not the head: revision 2 is archived above it.
        Assert.Equal(JournalReceiptCodes.SuccessorEvidence, (await receipts.VerifyStreamHeadAsync(first, Ct)).Code);
        Assert.True((await receipts.VerifyAsync(first, Ct)).IsVerified);

        // Successor evidence for the head: an archived object, or a commitment in either store, or a stop marker above it.
        var successors = new (string Name, Action Plant, Action Remove)[]
        {
            ("object", () => rig.Archive.PutObjectForTesting(rig.ObjectKey(3), "{}"u8.ToArray()), () => rig.Archive.RemoveObjectForTesting(rig.ObjectKey(3))),
            ("archive-commitment", () => rig.Archive.PutMetaForTesting(DPlusRecoveryNames.ArchiveCommitmentPrefix + rig.Commitment(3), "{}"u8.ToArray()),
                () => rig.Archive.RemoveMetaForTesting(DPlusRecoveryNames.ArchiveCommitmentPrefix + rig.Commitment(3))),
            ("store-commitment", () => rig.World.DPlus.Records.PutForTesting(DPlusRecoveryNames.StoreCommitmentPrefix + rig.Commitment(3), "{}"u8.ToArray()),
                () => rig.World.DPlus.Records.RemoveForTesting(DPlusRecoveryNames.StoreCommitmentPrefix + rig.Commitment(3))),
            ("stop-marker", () => rig.Archive.PutMetaForTesting(DPlusStreamStops.MarkerObjectName(rig.Org, stream, 3, 0), "{}"u8.ToArray()),
                () => rig.Archive.RemoveMetaForTesting(DPlusStreamStops.MarkerObjectName(rig.Org, stream, 3, 0))),
        };
        foreach (var (name, plant, remove) in successors)
        {
            plant();
            rig.World.Clock.Advance(TimeSpan.FromTicks(1));
            Assert.True(JournalReceiptCodes.SuccessorEvidence == (await receipts.VerifyStreamHeadAsync(second, Ct)).Code, name);
            Assert.True((await receipts.VerifyAsync(second, Ct)).IsVerified, name); // the entry's own evidence is intact
            remove();
            rig.World.Clock.Advance(TimeSpan.FromTicks(1));
            Assert.True((await receipts.VerifyStreamHeadAsync(second, Ct)).IsVerified, name); // positional: not withdrawn
        }
    }

    /// <summary>RV-5: a newly established durable stop at or below the entry closes it until a later-generation commitment resolves the stop.</summary>
    [Theory]
    [MemberData(nameof(Streams))]
    public async Task ANewlyEstablishedStopAtOrBelowTheEntryIsRefusedUntilResolved(string stream)
    {
        var rig = await Rig.CreateAsync(stream);
        var (first, second) = await rig.DeliveredAsync();
        var receipts = rig.Receipts();
        Assert.True((await receipts.VerifyStreamHeadAsync(second, Ct)).IsVerified);

        var marker = DPlusStreamStops.MarkerObjectName(rig.Org, stream, 1, 0);
        rig.Archive.PutMetaForTesting(marker, "{}"u8.ToArray()); // a stop at revision 1, generation 0, below the head
        rig.World.Clock.Advance(Phi + TimeSpan.FromMilliseconds(1));
        Assert.Equal(JournalReceiptCodes.StopUnresolved, (await receipts.VerifyStreamHeadAsync(second, Ct)).Code);
        Assert.Equal(JournalReceiptCodes.StopUnresolved, (await receipts.VerifyAsync(second, Ct)).Code);
        Assert.Equal(JournalReceiptCodes.StopUnresolved, (await receipts.VerifyAsync(first, Ct)).Code);

        // A later-generation commitment in only one store does not resolve it; in both it does.
        var genuine = rig.Archive.MetaForTesting[DPlusRecoveryNames.ArchiveCommitmentPrefix + rig.Commitment(1)];
        var resolved = DPlusRecoveryCodec.Serialize(DPlusRecoveryCodec.TryParse<DPlusEvidenceCommitment>(genuine)! with { StopGeneration = 1 });
        rig.Archive.PutMetaForTesting(DPlusRecoveryNames.ArchiveCommitmentPrefix + rig.Commitment(1, 1), resolved);
        rig.World.Clock.Advance(Phi + TimeSpan.FromMilliseconds(1));
        Assert.Equal(JournalReceiptCodes.StopUnresolved, (await receipts.VerifyStreamHeadAsync(second, Ct)).Code);
        rig.World.DPlus.Records.PutForTesting(DPlusRecoveryNames.StoreCommitmentPrefix + rig.Commitment(1, 1), resolved);
        rig.World.Clock.Advance(Phi + TimeSpan.FromMilliseconds(1));
        Assert.True((await receipts.VerifyStreamHeadAsync(second, Ct)).IsVerified);

        // A stop at the head's own revision at its current generation is unresolved.
        rig.Archive.PutMetaForTesting(DPlusStreamStops.MarkerObjectName(rig.Org, stream, 2, 0), "{}"u8.ToArray());
        rig.World.Clock.Advance(Phi + TimeSpan.FromMilliseconds(1));
        Assert.Equal(JournalReceiptCodes.StopUnresolved, (await receipts.VerifyStreamHeadAsync(second, Ct)).Code);
    }

    /// <summary>RV-3/TR-5: an outage after establishment is honoured only within the bound, then Unavailable (never reused, never withdrawn).</summary>
    [Theory]
    [MemberData(nameof(Streams))]
    public async Task AnOutageIsNeverLastKnownGood(string stream)
    {
        var rig = await Rig.CreateAsync(stream);
        var (_, second) = await rig.DeliveredAsync();
        var receipts = rig.Receipts();
        Assert.True((await receipts.VerifyStreamHeadAsync(second, Ct)).IsVerified);
        rig.World.DPlus.Records.Unavailable = true;
        rig.World.Clock.Advance(Phi + TimeSpan.FromMilliseconds(1));
        var down = await receipts.VerifyStreamHeadAsync(second, Ct);
        Assert.Equal((JournalReceiptStatus.Unavailable, JournalReceiptCodes.StoreUnavailable), (down.Status, down.Code));
        Assert.Equal(JournalReceiptStatus.Unavailable, (await receipts.VerifyStreamHeadAsync(second, Ct)).Status); // not reused as Verified
        rig.World.DPlus.Records.Unavailable = false;
        Assert.True((await receipts.VerifyStreamHeadAsync(second, Ct)).IsVerified); // an outage is not a contradiction
    }

    /// <summary>RV-7: concurrent accesses share one in-flight establishment; a new head (concurrent delivery) is a new key and never shares the old one.</summary>
    [Theory]
    [MemberData(nameof(Streams))]
    public async Task ConcurrentAccessesShareOneEstablishmentAndANewHeadIsNeverAnsweredByTheOld(string stream)
    {
        var rig = await Rig.CreateAsync(stream);
        var (_, second) = await rig.DeliveredAsync();
        var receipts = rig.Receipts(TimeSpan.Zero);
        var verdicts = await Task.WhenAll(Enumerable.Range(0, 32).Select(_ => receipts.VerifyStreamHeadAsync(second, Ct)));
        Assert.All(verdicts, v => Assert.True(v.IsVerified));
        Assert.Equal(1, receipts.StoreVerifications);

        var third = rig.Append("three"); // a newly committed head, not delivered yet
        Assert.Equal(JournalReceiptCodes.NotDelivered, (await receipts.VerifyStreamHeadAsync(third, Ct)).Code);
        Assert.Equal(1, await rig.Processor().ProcessOrganizationAsync(rig.Org, Ct));
        rig.World.Clock.Advance(TimeSpan.FromTicks(1));
        var both = await Task.WhenAll(receipts.VerifyStreamHeadAsync(second, Ct), receipts.VerifyStreamHeadAsync(third, Ct));
        Assert.Equal(JournalReceiptCodes.SuccessorEvidence, both[0].Code); // the old head is no longer the head
        Assert.True(both[1].IsVerified);
    }

    /// <summary>Unchanged heads are never trusted indefinitely: after the bound every access returns to the stores, however long the head is unchanged.</summary>
    [Theory]
    [MemberData(nameof(Streams))]
    public async Task AnUnchangedHeadIsReEstablishedAfterEveryBound(string stream)
    {
        var rig = await Rig.CreateAsync(stream);
        var (_, second) = await rig.DeliveredAsync();
        var receipts = rig.Receipts();
        for (var i = 1; i <= 5; i++)
        {
            Assert.True((await receipts.VerifyStreamHeadAsync(second, Ct)).IsVerified);
            Assert.True((await receipts.VerifyStreamHeadAsync(second, Ct)).IsVerified);
            Assert.Equal(i, receipts.StoreVerifications);
            rig.World.Clock.Advance(Phi + TimeSpan.FromMilliseconds(1));
        }
    }

    /// <summary>Missing or conflicting evidence at first sight is refused without being recorded as withdrawn (nothing verified before).</summary>
    [Theory]
    [MemberData(nameof(Streams))]
    public async Task EvidenceThatNeverVerifiedIsRefusedButNotWithdrawn(string stream)
    {
        var rig = await Rig.CreateAsync(stream);
        var (_, second) = await rig.DeliveredAsync();
        var bytes = (await rig.Archive.ReadAsync(rig.Org, stream, 2, Ct))!;
        Assert.True(rig.Archive.RemoveObjectForTesting(rig.ObjectKey(2)));
        var receipts = rig.Receipts();
        Assert.Equal(JournalReceiptCodes.ArchiveAbsent, (await receipts.VerifyStreamHeadAsync(second, Ct)).Code);
        rig.Archive.PutObjectForTesting(rig.ObjectKey(2), bytes);
        Assert.True((await receipts.VerifyStreamHeadAsync(second, Ct)).IsVerified); // never verified before: no contradiction to remember
        Assert.Equal(SHA256.HashData(bytes), second.JournalEventDigest);
    }
}
