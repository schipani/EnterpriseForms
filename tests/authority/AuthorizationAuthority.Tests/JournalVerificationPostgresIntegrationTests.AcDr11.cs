using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using AuthorizationAuthority.Application.Common.Ports;
using AuthorizationAuthority.Application.Features.Journal;
using AuthorizationAuthority.Application.Features.Journal.Reconciliation;
using AuthorizationAuthority.Application.Features.Journal.Recovery;
using AuthorizationAuthority.Application.Features.Journal.Verification;
using AuthorizationAuthority.Domain.Features.Journal;
using AuthorizationAuthority.Domain.Features.Organizations;
using AuthorizationAuthority.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace AuthorizationAuthority.Tests;

/// <summary>
/// SEC-BL-01 WP-7A §6 (AC-DR-11; OI-DR-6: the E3-M2f-3G pattern of 2,000,000 audit entries for one organization plus 500,000 over four others).
/// Opt-in (<c>SECBL01_SCALE_ROWS</c> = target-organization rows; the other four organizations hold a quarter of that in total).
/// <para>
/// <b>Accelerated, equivalent setup.</b> Every entry is produced exactly as D+ delivery leaves it: <see cref="JournalChain.Append"/> (the
/// Authority's own chain builder) on the organization state, the archived object written by the real Blob adapter's create-only
/// <c>AppendAsync</c> with its digest check, the W2-S commitments built by D+'s own <see cref="DPlusEvidenceCommitments.Build"/> (generation 0,
/// genesis/present predecessor states, the pinned instance) and written create-only to both stores by the real adapters, and the row stored as
/// <c>Anchored</c> with the archive's receipt. Only the scheduling differs: writes are issued with bounded concurrency in chunks instead of
/// one D+ pass at a time. The measured operations (baseline, sweeps, D+ delivery, detection) are the unmodified product code; the clean
/// baseline over the seeded data re-verifies every row and every archived object, which is the equivalence check of the seed itself.
/// </para>
/// </summary>
public sealed partial class JournalVerificationPostgresIntegrationTests
{
    private sealed record ScaleBody(string NoteText, long CounterValue);

    private static async Task ScaleProgressAsync(string line)
    {
        TestContext.Current.TestOutputHelper?.WriteLine(line);
        if (Environment.GetEnvironmentVariable("SECBL01_EVIDENCE_DIR") is { Length: > 0 } directory)
        {
            Directory.CreateDirectory(directory);
            await File.AppendAllTextAsync(Path.Combine(directory, "acdr11-progress.log"), DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture) + " " + line + "\n", Ct);
        }
    }

    private static async Task<Guid> ScaleOrganizationAsync(string label)
    {
        var org = Guid.CreateVersion7();
        await using var context = AdminContext();
        context.OrganizationStates.Add(new OrganizationAuthorizationState
        {
            OrganizationAuthorizationStateId = Guid.CreateVersion7(), OrganizationId = org, OrganizationDisplayName = "sec-bl-01 ac-dr-11 " + label, StateModifiedOn = DateTimeOffset.UtcNow,
            CheckpointEnforcementModeName = CheckpointEnforcementModes.RequiredBeforeEffective,
        });
        await context.SaveChangesAsync(Ct);
        return org;
    }

    /// <summary>Delivered audit entries 1..rows of one organization, as D+ leaves them (see the class summary), in chunks.</summary>
    private static async Task SeedDeliveredAuditAsync(Guid org, long rows, string archiveContainer, string instance, int concurrency, int chunk, string label)
    {
        var archive = Wp2Azurite.NewArchive(archiveContainer);
        var store = Durable(archiveContainer).Records;
        byte[]? predecessor = null;
        var watch = Stopwatch.StartNew();
        for (long offset = 0; offset < rows; offset += chunk)
        {
            await using var context = AdminContext();
            context.ChangeTracker.AutoDetectChangesEnabled = false;
            var state = await context.OrganizationStates.SingleAsync(s => s.OrganizationId == org, Ct);
            var entries = new List<SecurityJournalEntry>();
            var predecessors = new List<byte[]?>();
            for (var i = offset; i < Math.Min(rows, offset + chunk); i++)
            {
                var revision = state.AuditJournalRevision + 1;
                state.AuditJournalRevision = revision;
                var entry = JournalChain.Append(state, JournalStreamNames.Audit, revision, "SyntheticEvent", new ScaleBody("synthetic audit " + revision, revision), null, null, TimeProvider.System);
                entries.Add(entry);
                predecessors.Add(predecessor);
                predecessor = Encoding.UTF8.GetBytes(entry.JournalEventBodyJson);
            }

            await Parallel.ForEachAsync(Enumerable.Range(0, entries.Count), new ParallelOptions { MaxDegreeOfParallelism = concurrency, CancellationToken = Ct }, async (i, ct) =>
            {
                var entry = entries[i];
                var body = Encoding.UTF8.GetBytes(entry.JournalEventBodyJson);
                var commitment = DPlusRecoveryCodec.Serialize(DPlusEvidenceCommitments.Build(
                    new AuditDeliveryEvidence(entry, instance, entry.JournalStreamRevision == 1 ? AuditPredecessorState.Genesis : AuditPredecessorState.Present, predecessors[i]), 0));
                var name = DPlusRecoveryNames.Commitment(org, JournalStreamNames.Audit, entry.JournalStreamRevision, 0);
                Assert.Equal(CheckpointCreateStatus.Created, (await archive.CreateMetaAsync(DPlusRecoveryNames.ArchiveCommitmentPrefix + name, commitment, ct)).Status);
                Assert.Equal(CheckpointCreateStatus.Created, (await store.CreateAsync(DPlusRecoveryNames.StoreCommitmentPrefix + name, commitment, ct)).Status);
                var receipt = await archive.AppendAsync(org, JournalStreamNames.Audit, entry.JournalStreamRevision, body, entry.JournalEventDigest, ct);
                entry.ArchiveDeliveryState = ArchiveDeliveryStates.Anchored;
                entry.ArchiveReceiptReference = receipt.ReceiptReference;
                entry.ArchiveReceiptDigest = receipt.ReceiptDigest;
                entry.JournalArchivedOn = DateTimeOffset.UtcNow;
            });
            context.JournalEntries.AddRange(entries);
            context.ChangeTracker.DetectChanges();
            await context.SaveChangesAsync(Ct);
            var done = Math.Min(rows, offset + chunk);
            if (done % (chunk * 10) == 0 || done == rows)
            {
                await ScaleProgressAsync($"seed {label}: {done}/{rows} rows, {watch.Elapsed.TotalSeconds:F0} s, {done / Math.Max(1, watch.Elapsed.TotalSeconds):F0} rows/s");
            }
        }
    }

    /// <summary>
    /// AC-DR-11: D+ added latency per delivery; incremental and full sweep duration and archive reads/enumeration; observed detection latencies
    /// against §8.6 — on a synthetic organization of <c>SECBL01_SCALE_ROWS</c> delivered audit entries (2,000,000 for the acceptance run).
    /// </summary>
    [Fact]
    public async Task MeasureAcDr11ScaleOrganization()
    {
        Require();
        Assert.SkipUnless(long.TryParse(Environment.GetEnvironmentVariable("SECBL01_SCALE_ROWS"), out var rows) && rows > 0, "SECBL01_SCALE_ROWS not set (opt-in AC-DR-11 scale measurement)");
        var concurrency = int.TryParse(Environment.GetEnvironmentVariable("SECBL01_SCALE_CONCURRENCY"), out var c) ? c : 32;
        var chunk = int.TryParse(Environment.GetEnvironmentVariable("SECBL01_SCALE_CHUNK"), out var k) ? k : 10_000;
        using var approvers = Approvers();
        var rig = await StreamRigAsync(JournalStreamNames.Audit, approvers);
        var org = rig.Org;
        var total = Stopwatch.StartNew();
        await ScaleProgressAsync($"AC-DR-11 start: target rows {rows}, other organizations 4 x {rows / 16}, concurrency {concurrency}, chunk {chunk}");

        var seeding = Stopwatch.StartNew();
        await SeedDeliveredAuditAsync(org, rows, rig.Container, rig.Instance, concurrency, chunk, "target");
        var targetSeedSeconds = seeding.Elapsed.TotalSeconds;
        for (var o = 1; o <= 4 && rows >= 16; o++)
        {
            var other = await ScaleOrganizationAsync("other " + o);
            await SeedDeliveredAuditAsync(other, rows / 16, rig.Container, rig.Instance, concurrency, chunk, "other-" + o);
        }

        seeding.Stop();
        var reader = SharedRuntimeReader.Value;
        var checkpoints = Wp2Azurite.NewCheckpointStore(rig.StoreContainer);
        var exceptions = Wp2Azurite.NewExceptionStore(rig.StoreContainer);
        var alerts = new RecordingAlerts();
        var verifier = new JournalVerifier(reader, rig.Archive, checkpoints, rig.Findings, exceptions, alerts, TimeProvider.System, new JournalVerifierOptions(), Wp2Azurite.NewRecordStore(rig.StoreContainer));
        var process = Process.GetCurrentProcess();

        // 1. Baseline (BL-1 reads and byte-compares every row and object: the equivalence check of the seed).
        var watch = Stopwatch.StartNew();
        var baseline = await new JournalBaselineService(reader, rig.Archive, checkpoints, exceptions, TimeProvider.System)
            .RunAsync(JournalVerificationWorld.BaselineRequest(org, JournalStreamNames.Audit, createSentinel: false), Ct);
        var baselineSeconds = watch.Elapsed.TotalSeconds;
        await ScaleProgressAsync($"baseline: {baseline.Outcome} in {baselineSeconds:F1} s; rows {baseline.Metrics.DatabaseRowsInspected}, listed {baseline.Metrics.ArchiveObjectsListed}, read {baseline.Metrics.ArchiveObjectsRead}");
        Assert.Equal(nameof(JournalBaselineOutcome.Clean), baseline.Outcome);

        // 2. Incremental sweep with nothing new (the fixed per-sweep cost, including the commitment listing of the whole stream).
        var idle = await verifier.SweepAsync(org, JournalStreamNames.Audit, JournalSweepKind.Incremental, Ct);
        await ScaleProgressAsync($"incremental (idle): {Describe(idle)} {idle.Metrics.ElapsedMilliseconds} ms, listed {idle.Metrics.ArchiveObjectsListed}, read {idle.Metrics.ArchiveObjectsRead}");
        Assert.Equal(JournalSweepOutcome.Clean, idle.Outcome);

        // 3. D+ delivery on the large organization: 200 new entries through the real ArchiveProcessor.
        await AppendStreamAsync(org, JournalStreamNames.Audit, 200);
        var processor = new ArchiveProcessor(RuntimeStore(), rig.Archive, TimeProvider.System, new RecordingAlerts(), rig.Findings, Durable(rig.Container));
        var passes = new List<double>();
        var delivered = 0;
        watch.Restart();
        while (delivered < 200 && passes.Count < 1000)
        {
            var pass = Stopwatch.StartNew();
            delivered += await processor.ProcessOrganizationAsync(org, Ct);
            passes.Add(pass.Elapsed.TotalMilliseconds);
        }

        var deliverySeconds = watch.Elapsed.TotalSeconds;
        await ScaleProgressAsync($"D+ delivery: {delivered} entries in {deliverySeconds:F1} s over {passes.Count} passes ({deliverySeconds * 1000 / Math.Max(1, delivered):F1} ms per entry)");
        Assert.Equal(200, delivered);

        // 4. Incremental sweep over the 200 new entries.
        var incremental = await verifier.SweepAsync(org, JournalStreamNames.Audit, JournalSweepKind.Incremental, Ct);
        await ScaleProgressAsync($"incremental (+200): {Describe(incremental)} {incremental.Metrics.ElapsedMilliseconds} ms, listed {incremental.Metrics.ArchiveObjectsListed}, read {incremental.Metrics.ArchiveObjectsRead}");
        Assert.Equal(JournalSweepOutcome.Clean, incremental.Outcome);

        // 5. Full sweep (VF-12/VF-13 and, since WP-7A, VF-15: every archived object re-read).
        var full = await verifier.SweepAsync(org, JournalStreamNames.Audit, JournalSweepKind.Full, Ct);
        process.Refresh();
        await ScaleProgressAsync($"full: {Describe(full)} {full.Metrics.ElapsedMilliseconds} ms, rows {full.Metrics.DatabaseRowsInspected}, listed {full.Metrics.ArchiveObjectsListed}, read {full.Metrics.ArchiveObjectsRead}; peak working set {process.PeakWorkingSet64}");
        Assert.Equal(JournalSweepOutcome.Clean, full.Outcome);

        // 6. Detection latencies (§8.6: configuration interval + measured sweep duration).
        var raw = Wp2Azurite.RawArchiveContainer(rig.Container);
        var head = rows + 200;
        var above = await AppendStreamAsync(org, JournalStreamNames.Audit, 10);
        Assert.Equal(10, await processor.ProcessOrganizationAsync(org, Ct));
        var newest = raw.GetBlobClient(StreamKey(org, JournalStreamNames.Audit, head + 10));
        await newest.UploadAsync(BinaryData.FromString((await newest.DownloadContentAsync(Ct)).Value.Content.ToString().Replace("synthetic", "SYNTHETIC", StringComparison.Ordinal)), overwrite: true, Ct);
        var aboveRun = await verifier.SweepAsync(org, JournalStreamNames.Audit, JournalSweepKind.Incremental, Ct);
        await ScaleProgressAsync($"detection above the checkpoint (replaced newest object): {Describe(aboveRun)} in {aboveRun.Metrics.ElapsedMilliseconds} ms");
        Assert.Contains(aboveRun.Findings, f => f.FindingCode == JournalFindingCodes.AnchorMismatch);

        var middle = raw.GetBlobClient(StreamKey(org, JournalStreamNames.Audit, rows / 2));
        await middle.UploadAsync(BinaryData.FromString((await middle.DownloadContentAsync(Ct)).Value.Content.ToString().Replace("synthetic", "SYNTHETIC", StringComparison.Ordinal)), overwrite: true, Ct);
        var belowFull = await verifier.SweepAsync(org, JournalStreamNames.Audit, JournalSweepKind.Full, Ct);
        await ScaleProgressAsync($"detection below the checkpoint (replaced revision {rows / 2}), full sweep: {Describe(belowFull)} in {belowFull.Metrics.ElapsedMilliseconds} ms");
        Assert.Contains(belowFull.Findings, f => f.FindingCode == JournalFindingCodes.AnchorMismatch && f.FirstRevision == rows / 2);

        var forged = await AppendStreamAsync(org, JournalStreamNames.Audit, 1);
        await AdminSqlAsync("UPDATE authority.security_journal_entries SET archive_delivery_state = 'Anchored', archive_receipt_reference = 'forged', archive_receipt_digest = decode(repeat('ab', 32), 'hex'), journal_archived_on = now()"
            + RowWhere + (head + 11).ToString(CultureInfo.InvariantCulture), org, JournalStreamNames.Audit);
        var fakeRun = await verifier.SweepAsync(org, JournalStreamNames.Audit, JournalSweepKind.Incremental, Ct);
        await ScaleProgressAsync($"detection of a forged receipt: {Describe(fakeRun)} in {fakeRun.Metrics.ElapsedMilliseconds} ms");
        Assert.Contains(fakeRun.Findings, f => f.FindingCode == JournalFindingCodes.FakeReceipt);
        _ = (above, forged);

        process.Refresh();
        var measurement = new
        {
            TargetRows = rows,
            OtherOrganizationRows = rows >= 16 ? 4 * (rows / 16) : 0,
            Concurrency = concurrency,
            Chunk = chunk,
            SeedingSeconds = Math.Round(seeding.Elapsed.TotalSeconds, 1),
            TargetSeedingSeconds = Math.Round(targetSeedSeconds, 1),
            Baseline = new { Seconds = Math.Round(baselineSeconds, 1), baseline.Metrics.DatabaseRowsInspected, baseline.Metrics.ArchiveObjectsListed, baseline.Metrics.ArchiveObjectsRead },
            IdleIncremental = new { idle.Metrics.ElapsedMilliseconds, idle.Metrics.DatabaseRowsInspected, idle.Metrics.ArchiveObjectsListed, idle.Metrics.ArchiveObjectsRead },
            DPlusDelivery = new { Entries = delivered, Seconds = Math.Round(deliverySeconds, 2), MillisecondsPerEntry = Math.Round(deliverySeconds * 1000 / Math.Max(1, delivered), 1), Passes = passes.Count,
                PassP50Milliseconds = Math.Round(passes.Order().ElementAt(passes.Count / 2), 1), PassMaxMilliseconds = Math.Round(passes.Max(), 1) },
            Incremental = new { incremental.Metrics.ElapsedMilliseconds, incremental.Metrics.DatabaseRowsInspected, incremental.Metrics.ArchiveObjectsListed, incremental.Metrics.ArchiveObjectsRead },
            Full = new { full.Metrics.ElapsedMilliseconds, full.Metrics.DatabaseRowsInspected, full.Metrics.ArchiveObjectsListed, full.Metrics.ArchiveObjectsRead },
            Detection = new
            {
                AboveCheckpointIncrementalMilliseconds = aboveRun.Metrics.ElapsedMilliseconds,
                BelowCheckpointFullMilliseconds = belowFull.Metrics.ElapsedMilliseconds,
                ForgedReceiptIncrementalMilliseconds = fakeRun.Metrics.ElapsedMilliseconds,
                Note = "observed latency = configured interval (T_inc 5 min, T_full 24 h, §8.6) + the measured sweep duration",
            },
            PeakWorkingSetBytes = process.PeakWorkingSet64,
            TotalSeconds = Math.Round(total.Elapsed.TotalSeconds, 1),
        };
        var json = JsonSerializer.Serialize(measurement, Indented);
        await ScaleProgressAsync(json);
        if (Environment.GetEnvironmentVariable("SECBL01_EVIDENCE_DIR") is { Length: > 0 } directory)
        {
            await File.WriteAllTextAsync(Path.Combine(directory, "acdr11-measurement.json"), json.Replace("\r\n", "\n", StringComparison.Ordinal) + "\n", Ct);
        }
    }
}
