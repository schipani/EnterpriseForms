using System.Text;
using AuthorizationAuthority.Application.Common.Ports;
using AuthorizationAuthority.Application.Features.Journal;
using AuthorizationAuthority.Application.Features.Journal.Reconciliation;
using AuthorizationAuthority.Application.Features.Journal.Recovery;
using AuthorizationAuthority.Domain.Features.Journal;
using AuthorizationAuthority.Infrastructure.Archive;
using Xunit;

namespace AuthorizationAuthority.Tests;

/// <summary>
/// SEC-BL-01 WP-5b (C-157 P2-D5, amendment A-6 §26.7): the verifier's independent inspection of W2-S evidence commitments against the archived
/// entries (EP-5), rules VC-1..VC-9 of <see cref="DPlusCommitmentInspection"/>, for each journal stream. Commitments are built exactly as D+
/// builds them (<see cref="DPlusEvidenceCommitments.Build"/>) and written to both stores; each case then tampers with one aspect (J4 on the
/// archive copy, J6 on the store copy, or both) and a full sweep must report it. Alert-only: nothing is repaired.
/// </summary>
public sealed class DPlusCommitmentVerificationTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static TheoryData<string> Streams => [JournalStreamNames.Audit, JournalStreamNames.Content, JournalStreamNames.Authorization];

    private sealed class Rig
    {
        public required JournalVerificationWorld World { get; init; }

        public required Guid Org { get; init; }

        public required string Stream { get; init; }

        public required string Instance { get; init; }

        public InMemoryJournalVerificationRecordStore Store { get; } = new();

        public List<SecurityJournalEntry> Rows { get; } = [];

        public string Name(long revision, int generation = 0) => DPlusRecoveryNames.Commitment(Org, Stream, revision, generation);

        public DPlusEvidenceCommitment Genuine(long revision, int generation = 0)
        {
            var row = Rows.Single(r => r.JournalStreamRevision == revision);
            var predecessor = revision == 1 ? null : Encoding.UTF8.GetBytes(Rows.Single(r => r.JournalStreamRevision == revision - 1).JournalEventBodyJson);
            return DPlusEvidenceCommitments.Build(new AuditDeliveryEvidence(row, Instance, revision == 1 ? AuditPredecessorState.Genesis : AuditPredecessorState.Present, predecessor), generation);
        }

        public async Task PutBothAsync(string name, byte[] bytes)
        {
            World.Archive.PutMetaForTesting(DPlusRecoveryNames.ArchiveCommitmentPrefix + name, bytes);
            Store.PutForTesting(DPlusRecoveryNames.StoreCommitmentPrefix + name, bytes);
            await Task.CompletedTask;
        }

        public JournalVerifier Verifier(IJournalVerificationRecordStore? store) =>
            new(World.Reader, World.Archive, World.Checkpoints, World.Findings, World.Exceptions, World.Alerts, World.Clock, new JournalVerifierOptions(), store);

        public async Task<IReadOnlyList<JournalVerificationFinding>> SweepAsync(IJournalVerificationRecordStore? store = null, bool useStore = true) =>
            (await Verifier(useStore ? store ?? Store : null).SweepAsync(Org, Stream, JournalSweepKind.Full, Ct)).Findings;
    }

    /// <summary>A baselined stream of five delivered entries, each with its genuine commitment in both stores.</summary>
    private static async Task<Rig> RigAsync(string stream)
    {
        var world = new JournalVerificationWorld();
        var org = world.AddOrganization();
        await world.CreateSentinelAsync();
        var instance = (await world.Archive.ReadIdentityAsync(Ct)).Identity!.ArchiveInstanceId;
        var rig = new Rig { World = world, Org = org, Stream = stream, Instance = instance };
        rig.Rows.AddRange(world.Append(org, stream, 5));
        await world.DeliverAsync(org, stream);
        Assert.Equal(nameof(JournalBaselineOutcome.Clean), (await world.Baseline().RunAsync(JournalVerificationWorld.BaselineRequest(org, stream, createSentinel: false), Ct)).Outcome);
        for (var revision = 1; revision <= 5; revision++)
        {
            await rig.PutBothAsync(rig.Name(revision), DPlusRecoveryCodec.Serialize(rig.Genuine(revision)));
        }

        return rig;
    }

    private static IEnumerable<JournalVerificationFinding> Mismatches(IReadOnlyList<JournalVerificationFinding> findings) =>
        findings.Where(f => f.FindingCode == JournalFindingCodes.DPlusCommitmentMismatch);

    private static void AssertReported(IReadOnlyList<JournalVerificationFinding> findings, string subtype, long? revision)
    {
        Assert.Contains(Mismatches(findings), f => f.Subtype == subtype && f.FirstRevision == revision);
        Assert.Equal(JournalFindingSeverity.Security, JournalFindingCodes.SeverityOf(JournalFindingCodes.DPlusCommitmentMismatch));
    }

    [Theory]
    [MemberData(nameof(Streams))]
    public async Task GenuineCommitmentsMatchTheArchiveAndProduceNoFinding(string stream)
    {
        var rig = await RigAsync(stream);
        Assert.Empty(Mismatches(await rig.SweepAsync()));
    }

    /// <summary>VC-1..VC-9: each defect is reported with its subtype and revision by a full sweep.</summary>
    [Theory]
    [MemberData(nameof(Streams))]
    public async Task EveryCommitmentDefectIsReported(string stream)
    {
        var cases = new (string Name, Func<Rig, Task> Tamper, string Subtype, long? Revision)[]
        {
            ("missing (both copies removed)", async r =>
            {
                r.World.Archive.RemoveMetaForTesting(DPlusRecoveryNames.ArchiveCommitmentPrefix + r.Name(3));
                r.Store.RemoveForTesting(DPlusRecoveryNames.StoreCommitmentPrefix + r.Name(3));
                await Task.CompletedTask;
            }, DPlusCommitmentInspection.Missing, 3),
            ("malformed", r => r.PutBothAsync(r.Name(3), "{}"u8.ToArray()), DPlusCommitmentInspection.Malformed, 3),
            ("non-canonical name", r => r.PutBothAsync(r.Org.ToString("D") + "/" + r.Stream + "/3.json", DPlusRecoveryCodec.Serialize(r.Genuine(3))), DPlusCommitmentInspection.Malformed, null),
            ("conflicting copies", async r =>
            {
                r.World.Archive.PutMetaForTesting(DPlusRecoveryNames.ArchiveCommitmentPrefix + r.Name(3), DPlusRecoveryCodec.Serialize(r.Genuine(3) with { RowBodySha256Hex = new string('b', 64) }));
                await Task.CompletedTask;
            }, DPlusCommitmentInspection.ConflictingCopies, 3),
            ("one copy missing", async r =>
            {
                r.Store.RemoveForTesting(DPlusRecoveryNames.StoreCommitmentPrefix + r.Name(3));
                await Task.CompletedTask;
            }, DPlusCommitmentInspection.CopyMissing, 3),
            ("wrong scope (another organization)", r => r.PutBothAsync(r.Name(3), DPlusRecoveryCodec.Serialize(r.Genuine(3) with { OrganizationId = Guid.CreateVersion7() })),
                DPlusCommitmentInspection.Malformed, 3),
            ("wrong revision (another revision's commitment under this key)", r => r.PutBothAsync(r.Name(3), DPlusRecoveryCodec.Serialize(r.Genuine(4))), DPlusCommitmentInspection.Malformed, 3),
            ("wrong archive instance", r => r.PutBothAsync(r.Name(3), DPlusRecoveryCodec.Serialize(r.Genuine(3) with { ArchiveInstanceId = "another-instance" })),
                DPlusCommitmentInspection.WrongInstance, 3),
            ("row digest (both copies replaced consistently)", r => r.PutBothAsync(r.Name(3), DPlusRecoveryCodec.Serialize(r.Genuine(3) with { RowBodySha256Hex = new string('c', 64) })),
                DPlusCommitmentInspection.RowDigest, 3),
            ("wrong row identity", r => r.PutBothAsync(r.Name(3), DPlusRecoveryCodec.Serialize(r.Genuine(3) with { RowEntryId = Guid.CreateVersion7() })), DPlusCommitmentInspection.WrongRow, 3),
            ("predecessor digest", r => r.PutBothAsync(r.Name(3), DPlusRecoveryCodec.Serialize(r.Genuine(3) with { PredecessorSha256Hex = new string('d', 64) })),
                DPlusCommitmentInspection.PredecessorDigest, 3),
            ("predecessor recorded absent for a delivered entry", r => r.PutBothAsync(r.Name(3), DPlusRecoveryCodec.Serialize(r.Genuine(3) with { PredecessorState = "Absent", PredecessorSha256Hex = null })),
                DPlusCommitmentInspection.PredecessorDigest, 3),
            ("genesis with a predecessor", r => r.PutBothAsync(r.Name(1), DPlusRecoveryCodec.Serialize(r.Genuine(1) with { PredecessorState = "Present", PredecessorSha256Hex = new string('e', 64) })),
                DPlusCommitmentInspection.PredecessorDigest, 1),
            ("invalid stop generation", r => r.PutBothAsync(r.Name(3, 1), DPlusRecoveryCodec.Serialize(r.Genuine(3, 1))), DPlusCommitmentInspection.InvalidGeneration, 3),
            ("beyond the AuthorityDb head", r => r.PutBothAsync(r.Name(99), DPlusRecoveryCodec.Serialize(r.Genuine(5) with { StreamRevision = 99 })), DPlusCommitmentInspection.BeyondHead, 99),
            ("archived evidence changed after commitment (J4 replaces entry 6, delivered after the checkpoint)", async r =>
            {
                // Archived objects at or below the checkpoint are not re-read by a full sweep (OI-DR-7); a newly delivered one is.
                r.Rows.AddRange(r.World.Append(r.Org, r.Stream, 2));
                await r.World.DeliverAsync(r.Org, r.Stream);
                await r.PutBothAsync(r.Name(6), DPlusRecoveryCodec.Serialize(r.Genuine(6)));
                await r.PutBothAsync(r.Name(7), DPlusRecoveryCodec.Serialize(r.Genuine(7)));
                var row = r.Rows.Single(x => x.JournalStreamRevision == 6);
                r.World.Archive.PutObjectForTesting(JournalVerificationWorld.ArchiveKey(r.Org, r.Stream, 6), Encoding.UTF8.GetBytes(row.JournalEventBodyJson.Replace("entry", "ENTRY", StringComparison.Ordinal)));
            }, DPlusCommitmentInspection.RowDigest, 6),
        };

        foreach (var (name, tamper, subtype, revision) in cases)
        {
            var rig = await RigAsync(stream);
            await tamper(rig);
            var findings = await rig.SweepAsync();
            Assert.True(Mismatches(findings).Any(f => f.Subtype == subtype && f.FirstRevision == revision),
                $"{stream}/{name}: expected {subtype}@{revision}; got [{string.Join(", ", Mismatches(findings).Select(f => f.Subtype + "@" + f.FirstRevision))}]");
        }
    }

    /// <summary>A higher generation (an executed recovery's re-evaluation) supersedes the earlier commitment of the same revision.</summary>
    [Theory]
    [MemberData(nameof(Streams))]
    public async Task AHigherGenerationSupersedesAnEarlierCommitmentWhenAStopOfThePreviousGenerationExists(string stream)
    {
        var rig = await RigAsync(stream);
        await rig.PutBothAsync(rig.Name(3), DPlusRecoveryCodec.Serialize(rig.Genuine(3) with { PredecessorState = "Absent", PredecessorSha256Hex = null })); // the gen-0 stop's evidence
        await rig.PutBothAsync(rig.Name(3, 1), DPlusRecoveryCodec.Serialize(rig.Genuine(3, 1)));
        var marker = new DPlusStreamStop(DPlusStreamStop.SchemaV1, rig.Org, stream, 3, JournalFindingCodes.PredecessorMissing, null, "identity", rig.Rows[2].SecurityJournalEntryId, rig.Instance,
            JournalVerificationWorld.Start, "test", 0);
        await rig.World.Archive.CreateStopMarkerAsync(rig.Org, stream, 3, DPlusStreamStops.Serialize(marker), Ct);
        Assert.Empty(Mismatches(await rig.SweepAsync()));
    }

    /// <summary>Without the journal-verification store only the archive copies are inspected (no single-copy finding); its outage is inconclusive.</summary>
    [Theory]
    [MemberData(nameof(Streams))]
    public async Task WithoutTheStoreOnlyArchiveCopiesAreInspectedAndAStoreOutageIsInconclusive(string stream)
    {
        var rig = await RigAsync(stream);
        rig.Store.RemoveForTesting(DPlusRecoveryNames.StoreCommitmentPrefix + rig.Name(3));
        Assert.Empty(Mismatches(await rig.SweepAsync(useStore: false)));
        AssertReported(await rig.SweepAsync(), DPlusCommitmentInspection.CopyMissing, 3);

        rig.Store.Unavailable = true;
        var run = await rig.Verifier(rig.Store).SweepAsync(rig.Org, stream, JournalSweepKind.Full, Ct);
        Assert.Empty(Mismatches(run.Findings));
        Assert.NotEqual(JournalSweepOutcome.Clean, run.Outcome); // never verified while a mandatory listing failed
    }

    /// <summary>A stream without any commitment (history delivered before W2-S was enabled for it) is not inspected and costs no read.</summary>
    [Theory]
    [MemberData(nameof(Streams))]
    public async Task AStreamWithoutCommitmentsIsNotInspected(string stream)
    {
        var world = new JournalVerificationWorld();
        var org = world.AddOrganization();
        await world.CreateSentinelAsync();
        world.Append(org, stream, 3);
        await world.DeliverAsync(org, stream);
        Assert.Equal(nameof(JournalBaselineOutcome.Clean), (await world.Baseline().RunAsync(JournalVerificationWorld.BaselineRequest(org, stream, createSentinel: false), Ct)).Outcome);
        var run = await new JournalVerifier(world.Reader, world.Archive, world.Checkpoints, world.Findings, world.Exceptions, world.Alerts, world.Clock, new JournalVerifierOptions(),
            new InMemoryJournalVerificationRecordStore()).SweepAsync(org, stream, JournalSweepKind.Full, Ct);
        Assert.Empty(Mismatches(run.Findings));
    }
}
