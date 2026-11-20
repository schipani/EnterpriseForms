using System.Diagnostics;
using System.Globalization;
using System.Text;
using AuthorizationAuthority.Application.Common.Ports;
using AuthorizationAuthority.Application.Features.Journal;
using AuthorizationAuthority.Application.Features.Journal.Reconciliation;
using AuthorizationAuthority.Application.Features.Journal.Recovery;
using AuthorizationAuthority.Application.Features.Journal.Verification;
using AuthorizationAuthority.Domain.Features.Journal;
using AuthorizationAuthority.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Xunit;
using GateState = AuthorizationAuthority.Contracts.V1.ProjectionGateState;

namespace AuthorizationAuthority.Tests;

/// <summary>
/// SEC-BL-01 WP-7A (C-160, A-9) on real stores (PostgreSQL 18 with the G08 triggers and the runtime role, the Azurite archive and the separately
/// keyed Azurite journal-verification store), all three streams. F-1 (TR-6′: receipts re-establish current state within the freshness bound and
/// withdrawn evidence stays refused), OI-DR-7 (VF-15: the full sweep re-reads and byte-compares every historical archived object) and the J2
/// revocation-deletion phases (RV-4: a stream head with successor evidence outside AuthorityDb does not open the gate). Synthetic data only.
/// </summary>
public sealed partial class JournalVerificationPostgresIntegrationTests
{
    private static readonly TimeSpan Wp7aPhi = TimeSpan.FromSeconds(1);

    public static TheoryData<string, string> WP7AF1Attacks
    {
        get
        {
            var data = new TheoryData<string, string>();
            foreach (var stream in new[] { JournalStreamNames.Audit, JournalStreamNames.Authorization, JournalStreamNames.Content })
            {
                foreach (var attack in new[] { "instance-replaced", "object-deleted", "object-replaced", "archive-commitment-deleted", "store-commitment-changed", "generation-substituted" })
                {
                    data.Add(stream, attack);
                }
            }

            return data;
        }
    }

    private static async Task Wp7aObserveAsync(string line)
    {
        TestContext.Current.TestOutputHelper?.WriteLine(line);
        if (Environment.GetEnvironmentVariable("SECBL01_EVIDENCE_DIR") is { Length: > 0 } directory)
        {
            Directory.CreateDirectory(directory);
            await File.AppendAllTextAsync(Path.Combine(directory, "wp7a-observations.log"), line.Replace("\r\n", "\n", StringComparison.Ordinal) + "\n", Ct);
        }
    }

    private static TrustedJournalReceipts Wp7aReceipts(StreamRig rig, TimeProvider clock, TimeSpan? maxAge = null) =>
        new(rig.Archive, Durable(rig.Container), new TrustedJournalReceiptOptions { CurrentStateMaxAge = maxAge ?? Wp7aPhi }, clock);

    private static async Task<SecurityJournalEntry> Wp7aRowAsync(Guid org, string stream, long revision)
    {
        await using var context = AdminContext();
        return await context.JournalEntries.AsNoTracking().SingleAsync(e => e.OrganizationId == org && e.JournalStreamName == stream && e.JournalStreamRevision == revision, Ct);
    }

    private static string Wp7aIntent(Guid org, string stream, long revision, int generation = 0) =>
        $"{org:D}/{stream}/{revision.ToString("D12", CultureInfo.InvariantCulture)}" + (generation == 0 ? string.Empty : ".r" + generation.ToString("D4", CultureInfo.InvariantCulture)) + ".json";

    private static async Task Wp7aApplyAsync(StreamRig rig, string attack, long revision)
    {
        var raw = Wp2Azurite.RawArchiveContainer(rig.Container);
        var store = Wp2Azurite.RawCheckpointContainer(rig.StoreContainer);
        var name = Wp7aIntent(rig.Org, rig.Stream, revision);
        switch (attack)
        {
            case "instance-replaced": // J4/J5: another instance answers at the same endpoint
                await raw.GetBlobClient("meta/archive-identity.json").DeleteAsync(cancellationToken: Ct);
                await rig.Archive.CreateIdentityAsync(ArchiveIdentityCodec.NewIdentity(DateTimeOffset.UtcNow), Ct);
                break;
            case "object-deleted":
                await raw.GetBlobClient(StreamKey(rig.Org, rig.Stream, revision)).DeleteAsync(cancellationToken: Ct);
                break;
            case "object-replaced":
            {
                var blob = raw.GetBlobClient(StreamKey(rig.Org, rig.Stream, revision));
                var original = (await blob.DownloadContentAsync(Ct)).Value.Content.ToString();
                await blob.UploadAsync(BinaryData.FromString(original.Replace("synthetic", "SYNTHETIC", StringComparison.Ordinal)), overwrite: true, Ct);
                break;
            }

            case "archive-commitment-deleted":
                await raw.GetBlobClient("meta/dplus-intents/" + name).DeleteAsync(cancellationToken: Ct);
                break;
            case "store-commitment-changed": // J6: one copy names other row bytes
            {
                var blob = store.GetBlobClient("intents/" + name);
                var changed = DPlusRecoveryCodec.TryParse<DPlusEvidenceCommitment>((await blob.DownloadContentAsync(Ct)).Value.Content.ToArray())! with { RowBodySha256Hex = new string('d', 64) };
                await blob.UploadAsync(BinaryData.FromBytes(DPlusRecoveryCodec.Serialize(changed)), overwrite: true, Ct);
                break;
            }

            case "generation-substituted": // J4 + J6 consistently: a generation-1 commitment naming the same row, without any stop
            {
                var genuine = DPlusRecoveryCodec.TryParse<DPlusEvidenceCommitment>((await store.GetBlobClient("intents/" + name).DownloadContentAsync(Ct)).Value.Content.ToArray())!;
                var bytes = BinaryData.FromBytes(DPlusRecoveryCodec.Serialize(genuine with { StopGeneration = 1 }));
                await raw.GetBlobClient("meta/dplus-intents/" + Wp7aIntent(rig.Org, rig.Stream, revision, 1)).UploadAsync(bytes, overwrite: false, Ct);
                await store.GetBlobClient("intents/" + Wp7aIntent(rig.Org, rig.Stream, revision, 1)).UploadAsync(bytes, overwrite: false, Ct);
                break;
            }

            default:
                throw new ArgumentOutOfRangeException(nameof(attack), attack, null);
        }
    }

    // ---------------------------------------------------------------------------------------------------------------------------------
    // WP7A-PG-01: F-1 (TR-6′) — the original attack, after remediation, on every stream (gate for authorization, release receipt otherwise)
    // ---------------------------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// WP7A-PG-01 (F-1 after remediation; WP7-PG-09a was the pre-remediation characterization). One long-lived process-wide verifier: a receipt is
    /// established and used; then the external evidence of the same, unchanged entry is changed (J4, J5, J6 or J4 + J6). Within the freshness
    /// bound the earlier attestation still answers (bounded staleness); past it the evidence is re-established, the receipt is refused and stays
    /// refused in that process (EvidenceWithdrawn), and the real gate is closed. A fresh process decides from the stores only.
    /// </summary>
    [Theory]
    [MemberData(nameof(WP7AF1Attacks))]
    public async Task WP7AF1ChangedEvidenceOfAnUnchangedHeadIsRefusedPastTheFreshnessBound(string stream, string attack)
    {
        Require();
        using var approvers = Approvers();
        var rig = await StreamRigAsync(stream, approvers);
        await AppendStreamAsync(rig.Org, stream, 4);
        Assert.Equal(4, await rig.Processor().ProcessOrganizationAsync(rig.Org, Ct));
        var head = await Wp7aRowAsync(rig.Org, stream, 4);
        var clock = new ManualClock(DateTimeOffset.UtcNow);
        var receipts = Wp7aReceipts(rig, clock);
        Assert.True((await receipts.VerifyStreamHeadAsync(head, Ct)).IsVerified);
        Assert.True((await receipts.VerifyAsync(head, Ct)).IsVerified);
        if (stream == JournalStreamNames.Authorization)
        {
            Assert.Equal(GateState.Ready, await GateAsync(rig.Org, receipts));
        }

        await Wp7aApplyAsync(rig, attack, 4);

        clock.Advance(Wp7aPhi - TimeSpan.FromMilliseconds(1));
        var within = await receipts.VerifyStreamHeadAsync(head, Ct);
        clock.Advance(TimeSpan.FromMilliseconds(2));
        var past = await receipts.VerifyStreamHeadAsync(head, Ct);
        var entry = await receipts.VerifyAsync(head, Ct);
        var fresh = await Wp7aReceipts(rig, clock).VerifyStreamHeadAsync(head, Ct);
        await Wp7aObserveAsync($"{stream} {attack}: within bound={within.Code}; past bound head={past.Code} entry={entry.Code}; fresh process={fresh.Code}");
        Assert.True(within.IsVerified); // the bounded staleness RV-2 permits (≤ CurrentStateMaxAge), never longer
        Assert.Equal(JournalReceiptCodes.EvidenceWithdrawn, past.Code);
        Assert.Equal(JournalReceiptCodes.EvidenceWithdrawn, entry.Code);
        Assert.False(fresh.IsVerified);
        if (stream == JournalStreamNames.Authorization)
        {
            Assert.Equal(GateState.AwaitingArchive, await GateAsync(rig.Org, receipts));
        }
    }

    /// <summary>WP7A-PG-01b: with a zero bound the same change is refused at the very next access (every access re-establishes), on real stores.</summary>
    [Theory]
    [MemberData(nameof(WP7Streams))]
    public async Task WP7AF1WithAZeroBoundTheNextAccessIsRefusedOnRealStores(string stream)
    {
        Require();
        using var approvers = Approvers();
        var rig = await StreamRigAsync(stream, approvers);
        await AppendStreamAsync(rig.Org, stream, 3);
        Assert.Equal(3, await rig.Processor().ProcessOrganizationAsync(rig.Org, Ct));
        var head = await Wp7aRowAsync(rig.Org, stream, 3);
        var clock = new ManualClock(DateTimeOffset.UtcNow);
        var receipts = Wp7aReceipts(rig, clock, TimeSpan.Zero);
        Assert.True((await receipts.VerifyStreamHeadAsync(head, Ct)).IsVerified);
        await Wp7aApplyAsync(rig, "instance-replaced", 3);
        clock.Advance(TimeSpan.FromTicks(1));
        Assert.Equal(JournalReceiptCodes.EvidenceWithdrawn, (await receipts.VerifyStreamHeadAsync(head, Ct)).Code);
    }

    /// <summary>
    /// WP7A-PG-02: a newly established durable stop after the receipt was cached closes the gate within the bound, and a recovery generation
    /// established by the real G14-R2 flow verifies (the stop it resolves exists), on the authorization and content streams.
    /// </summary>
    [Theory]
    [MemberData(nameof(WP5BStreams))]
    public async Task WP7AANewStopClosesACachedReceiptAndARealRecoveryGenerationVerifies(string stream)
    {
        Require();
        using var approvers = Approvers();
        var (rig, genuine) = await StoppedStreamAsync(stream, 3, approvers); // stop at revision 4 (gen 0), predecessor 3 deleted by J4
        var clock = new ManualClock(DateTimeOffset.UtcNow);
        var receipts = Wp7aReceipts(rig, clock);
        var two = await Wp7aRowAsync(rig.Org, stream, 2);
        Assert.True((await receipts.VerifyAsync(two, Ct)).IsVerified); // below the stop: an entry receipt
        Assert.Equal(JournalReceiptCodes.SuccessorEvidence, (await receipts.VerifyStreamHeadAsync(two, Ct)).Code); // never a head while 3 and the stop exist

        var (id, binding) = await ProposeRestoreAsync(rig, genuine, 3);
        foreach (var approver in Wp7TwoApprovers)
        {
            Assert.Equal(DPlusRecoveryCommandOutcome.Done, (await rig.Service().RecordDecisionAsync(rig.Org, stream, id, approvers.Sign(approver, binding, DateTimeOffset.UtcNow), Ct)).Outcome);
        }

        Assert.Equal(DPlusRecoveryCommandOutcome.Done, (await rig.Service().ExecuteAsync(rig.Org, stream, id, "wp7a-executor", Ct)).Outcome);
        Assert.Equal(1, await rig.Processor().ProcessOrganizationAsync(rig.Org, Ct));
        clock.Advance(Wp7aPhi + TimeSpan.FromMilliseconds(1));
        var recovered = await Wp7aRowAsync(rig.Org, stream, 4);
        var verdict = await receipts.VerifyStreamHeadAsync(recovered, Ct);
        await Wp7aObserveAsync(stream + " recovered head: " + verdict);
        Assert.True(verdict.IsVerified); // the gen-0 stop at 4 is resolved by the gen-1 commitment at 4
        Assert.Equal(1, verdict.StopGeneration);
        if (stream == JournalStreamNames.Authorization)
        {
            Assert.Equal(GateState.Ready, await GateAsync(rig.Org, receipts));
        }

        // A forged unresolved stop at the head's own generation (J4) closes it within the bound.
        await Wp2Azurite.RawArchiveContainer(rig.Container).GetBlobClient(DPlusStreamStops.MarkerObjectName(rig.Org, stream, 4, 1)).UploadAsync(BinaryData.FromString("{}"), Ct);
        clock.Advance(Wp7aPhi + TimeSpan.FromMilliseconds(1));
        Assert.Equal(JournalReceiptCodes.StopUnresolved, (await receipts.VerifyStreamHeadAsync(recovered, Ct)).Code);
        if (stream == JournalStreamNames.Authorization)
        {
            Assert.Equal(GateState.AwaitingArchive, await GateAsync(rig.Org, receipts));
        }
    }

    // ---------------------------------------------------------------------------------------------------------------------------------
    // WP7A-PG-03: OI-DR-7 (VF-15) — historical archive verification by the full sweep, every stream
    // ---------------------------------------------------------------------------------------------------------------------------------

    public static TheoryData<string, string> WP7AHistoricalAttacks
    {
        get
        {
            var data = new TheoryData<string, string>();
            foreach (var stream in new[] { JournalStreamNames.Audit, JournalStreamNames.Authorization, JournalStreamNames.Content })
            {
                foreach (var attack in new[] { "replaced", "deleted", "digest-substituted", "identity-substituted", "row-disagrees", "commitment-disagrees" })
                {
                    data.Add(stream, attack);
                }
            }

            return data;
        }
    }

    /// <summary>
    /// WP7A-PG-03 (OI-DR-7 after remediation; WP7-PG-04b was the pre-remediation characterization). The trusted checkpoint is advanced to v = 8,
    /// then one historical revision (2) is attacked. The incremental sweep is unchanged (it never re-reads below v: IV-5, A-1); the scheduled full
    /// sweep detects every variant without a re-baseline: J4 replacement (byte comparison), deletion (enumeration), digest substitution (another
    /// genuine entry's bytes at the key), identity substitution (the right revision of another organization), J2 row disagreement and J4 + J6
    /// commitment disagreement.
    /// </summary>
    [Theory]
    [MemberData(nameof(WP7AHistoricalAttacks))]
    public async Task WP7AOiDr7TheFullSweepVerifiesEveryHistoricalArchivedObject(string stream, string attack)
    {
        Require();
        using var approvers = Approvers();
        var w = await Wp7RigAsync(stream, approvers);
        var advanced = await w.Verifier.SweepAsync(w.Org, stream, JournalSweepKind.Incremental, Ct);
        Assert.Equal((JournalSweepOutcome.Clean, 8L), (advanced.Outcome, advanced.NewCheckpoint!.VerifiedRevision));
        var clean = await Wp7Verifier(w.Rig, w.Reader, new RecordingAlerts()).SweepAsync(w.Org, stream, JournalSweepKind.Full, Ct);
        Assert.Equal(JournalSweepOutcome.Clean, clean.Outcome);
        Assert.True(clean.Metrics.ArchiveObjectsRead >= 8, "the full sweep reads every archived object: " + clean.Metrics.ArchiveObjectsRead);

        var raw = Wp2Azurite.RawArchiveContainer(w.Rig.Container);
        var key = StreamKey(w.Org, stream, 2);
        var original = (await raw.GetBlobClient(key).DownloadContentAsync(Ct)).Value.Content;
        (string Code, string? Subtype) expected;
        switch (attack)
        {
            case "replaced":
                await raw.GetBlobClient(key).UploadAsync(BinaryData.FromString(original.ToString().Replace("synthetic", "SYNTHETIC", StringComparison.Ordinal)), overwrite: true, Ct);
                expected = (JournalFindingCodes.AnchorMismatch, null);
                break;
            case "deleted":
                await raw.GetBlobClient(key).DeleteAsync(cancellationToken: Ct);
                expected = (JournalFindingCodes.ArchiveDiscontinuity, null);
                break;
            case "digest-substituted": // genuine bytes of revision 3 under key 2
                await raw.GetBlobClient(key).UploadAsync((await raw.GetBlobClient(StreamKey(w.Org, stream, 3)).DownloadContentAsync(Ct)).Value.Content, overwrite: true, Ct);
                expected = (JournalFindingCodes.AnchorMismatch, null);
                break;
            case "identity-substituted": // the genuine revision 2 of another organization's stream
            {
                using var others = Approvers();
                var other = await Wp7RigAsync(stream, others);
                var foreign = (await Wp2Azurite.RawArchiveContainer(other.Rig.Container).GetBlobClient(StreamKey(other.Org, stream, 2)).DownloadContentAsync(Ct)).Value.Content;
                await raw.GetBlobClient(key).UploadAsync(foreign, overwrite: true, Ct);
                expected = (JournalFindingCodes.AnchorMismatch, null);
                break;
            }

            case "row-disagrees": // J2 rewrites the AuthorityDb row below v without re-chaining (the archive keeps the genuine bytes)
                await AdminSqlAsync("UPDATE authority.security_journal_entries SET journal_event_body_json = replace(journal_event_body_json, 'synthetic', 'SYNTHETIC')" + RowWhere + "2", w.Org, stream);
                expected = (JournalFindingCodes.ChainIntegrityFailure, null);
                break;
            case "commitment-disagrees": // J4 + J6 consistently rewrite revision 2's commitment (a characterized collusion for receipts; detected here)
            {
                var name = Wp7aIntent(w.Org, stream, 2);
                var genuine = DPlusRecoveryCodec.TryParse<DPlusEvidenceCommitment>((await Wp2Azurite.RawCheckpointContainer(w.Rig.StoreContainer).GetBlobClient("intents/" + name).DownloadContentAsync(Ct)).Value.Content.ToArray())!;
                var forged = BinaryData.FromBytes(DPlusRecoveryCodec.Serialize(genuine with { RowBodySha256Hex = new string('e', 64) }));
                await raw.GetBlobClient("meta/dplus-intents/" + name).UploadAsync(forged, overwrite: true, Ct);
                await Wp2Azurite.RawCheckpointContainer(w.Rig.StoreContainer).GetBlobClient("intents/" + name).UploadAsync(forged, overwrite: true, Ct);
                expected = (JournalFindingCodes.DPlusCommitmentMismatch, null);
                break;
            }

            default:
                throw new ArgumentOutOfRangeException(nameof(attack), attack, null);
        }

        var incremental = await Wp7Verifier(w.Rig, w.Reader, new RecordingAlerts()).SweepAsync(w.Org, stream, JournalSweepKind.Incremental, Ct);
        var full = await Wp7Verifier(w.Rig, w.Reader, new RecordingAlerts()).SweepAsync(w.Org, stream, JournalSweepKind.Full, Ct);
        await Wp7aObserveAsync($"{stream} {attack} below v: incremental {Describe(incremental)}; full {Describe(full)}");
        if (attack is "replaced" or "digest-substituted" or "identity-substituted" or "deleted")
        {
            Assert.Equal(JournalSweepOutcome.Clean, incremental.Outcome); // IV-5 unchanged: the incremental sweep does not look below v
        }

        Assert.Equal(JournalSweepOutcome.Findings, full.Outcome);
        Assert.Contains(full.Findings, f => f.FindingCode == expected.Code && (f.FirstRevision is null || f.FirstRevision <= 3));
        Assert.Null(full.NewCheckpoint); // a finding never advances the checkpoint
        Assert.False(await QuarantinedAsync(w.Org)); // detection is alert-only (OD-DV-3)
    }

    // ---------------------------------------------------------------------------------------------------------------------------------
    // WP7A-PG-04: J2 revocation deletion in three phases (authorization stream, real gate)
    // ---------------------------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// WP7A-PG-04 (package §5; WP7-PG-09c was the pre-remediation characterization). J2 deletes the newest authorization entry (a revocation) and
    /// resets the head columns:
    /// (1) before its W2-S evidence commitment exists — nothing outside AuthorityDb records it: the gate opens on the previous state and no
    /// sweep detects it (unchanged; residual, owner decision);
    /// (2) after its commitment, before delivery — RV-4: the commitment above the head is successor evidence, the gate stays closed; the
    /// incremental sweep also reports BEYOND_HEAD;
    /// (3) after verified delivery — RV-4: the archived object above the head is successor evidence, the gate stays closed (it was Ready only
    /// while the revocation was the head); the incremental sweep reports BEHIND_ARCHIVE. A long-lived verifier whose previous-head attestation
    /// is fresh answers for at most the freshness bound; the demonstrated wall-clock window is measured.
    /// </summary>
    [Fact]
    public async Task WP7AJ2RevocationDeletionPhasesOnTheRealGate()
    {
        Require();
        using var approvers = Approvers();
        const string stream = JournalStreamNames.Authorization;
        var reader = SharedRuntimeReader.Value; // WP-7A: one pool for the run (per-test readers were never disposed)

        // (1) before commitment
        var one = await StreamRigAsync(stream, approvers);
        await AppendStreamAsync(one.Org, stream, 2);
        Assert.Equal(2, await one.Processor().ProcessOrganizationAsync(one.Org, Ct));
        Assert.Equal(nameof(JournalBaselineOutcome.Clean), (await Wp7BaselineAsync(one, reader)).Outcome);
        await AppendStreamAsync(one.Org, stream, 1);
        Assert.Equal(GateState.AwaitingArchive, await RealGateAsync(one));
        await AdminSqlAsync("DELETE FROM authority.security_journal_entries" + RowWhere + "3", one.Org, stream);
        await ResetHeadAsync(one.Org, stream, 2);
        var gateOne = await RealGateAsync(one);
        var sweepOne = await Wp7Verifier(one, reader, new RecordingAlerts()).SweepAsync(one.Org, stream, JournalSweepKind.Full, Ct);
        await Wp7aObserveAsync("J2 phase 1 (before commitment): gate=" + gateOne + "; full sweep " + Describe(sweepOne));
        Assert.Equal(GateState.Ready, gateOne); // residual: the revocation never becomes effective; nothing outside AuthorityDb knows it
        Assert.Equal(JournalSweepOutcome.Clean, sweepOne.Outcome);

        // (2) after commitment, before delivery
        var two = await StreamRigAsync(stream, approvers);
        await AppendStreamAsync(two.Org, stream, 2);
        Assert.Equal(2, await two.Processor().ProcessOrganizationAsync(two.Org, Ct));
        Assert.Equal(nameof(JournalBaselineOutcome.Clean), (await Wp7BaselineAsync(two, reader)).Outcome);
        await AppendStreamAsync(two.Org, stream, 1);
        var interrupted = new FailingWriteArchive(two.Archive, r => r == 3, _ => false);
        Assert.Equal(0, await two.Processor(interrupted).ProcessOrganizationAsync(two.Org, Ct));
        Assert.Single(await RawAsync(Wp2Azurite.RawCheckpointContainer(two.StoreContainer), $"intents/{two.Org:D}/{stream}/{Rev(3).PadLeft(12, '0')}"));
        await AdminSqlAsync("DELETE FROM authority.security_journal_entries" + RowWhere + "3", two.Org, stream);
        await ResetHeadAsync(two.Org, stream, 2);
        var gateTwo = await RealGateAsync(two);
        var sweepTwo = await Wp7Verifier(two, reader, new RecordingAlerts()).SweepAsync(two.Org, stream, JournalSweepKind.Incremental, Ct);
        await Wp7aObserveAsync("J2 phase 2 (after commitment, before delivery): gate=" + gateTwo + "; incremental sweep " + Describe(sweepTwo));
        Assert.Equal(GateState.AwaitingArchive, gateTwo); // RV-4: prevented at the gate
        AssertFinding(sweepTwo, JournalFindingCodes.DPlusCommitmentMismatch, DPlusCommitmentInspection.BeyondHead, 3); // and detected

        // (3) after verified delivery, with a long-lived process-wide verifier (real clock) whose attestation of revision 2 as head is fresh
        var three = await StreamRigAsync(stream, approvers);
        await AppendStreamAsync(three.Org, stream, 2);
        Assert.Equal(2, await three.Processor().ProcessOrganizationAsync(three.Org, Ct));
        Assert.Equal(nameof(JournalBaselineOutcome.Clean), (await Wp7BaselineAsync(three, reader)).Outcome);
        var longLived = Wp7aReceipts(three, TimeProvider.System);
        Assert.Equal(GateState.Ready, await GateAsync(three.Org, longLived)); // revision 2 attested as head
        await AppendStreamAsync(three.Org, stream, 1);
        Assert.Equal(1, await three.Processor().ProcessOrganizationAsync(three.Org, Ct)); // the revocation delivered and effective
        Assert.Equal(GateState.Ready, await GateAsync(three.Org, longLived));
        var deleted = Stopwatch.StartNew();
        await AdminSqlAsync("DELETE FROM authority.security_journal_entries" + RowWhere + "3", three.Org, stream);
        await ResetHeadAsync(three.Org, stream, 2);
        var firstAfter = await GateAsync(three.Org, longLived);
        var exposed = firstAfter == GateState.Ready;
        while (await GateAsync(three.Org, longLived) == GateState.Ready)
        {
            Assert.True(deleted.Elapsed < TimeSpan.FromSeconds(10), "the gate stayed open beyond the freshness bound");
            await Task.Delay(25, Ct);
        }

        var window = deleted.Elapsed;
        var freshGate = await RealGateAsync(three);
        var sweepThree = await Wp7Verifier(three, reader, new RecordingAlerts()).SweepAsync(three.Org, stream, JournalSweepKind.Incremental, Ct);
        await Wp7aObserveAsync($"J2 phase 3 (after delivery): long-lived first evaluation={firstAfter}; closed after {window.TotalMilliseconds:F0} ms (bound {Wp7aPhi.TotalMilliseconds} ms); fresh gate={freshGate}; incremental sweep {Describe(sweepThree)}");
        Assert.True(window <= Wp7aPhi + TimeSpan.FromSeconds(2), "window " + window);
        Assert.Equal(GateState.AwaitingArchive, freshGate); // RV-4: a fresh evaluation is refused immediately
        AssertFinding(sweepThree, JournalFindingCodes.BehindArchive, null, 3);
        await Wp7aObserveAsync("phase 3 exposed through a fresh attestation: " + exposed);
    }
}
