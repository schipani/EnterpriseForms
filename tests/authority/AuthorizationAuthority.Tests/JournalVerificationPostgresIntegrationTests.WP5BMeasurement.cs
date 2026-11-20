using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json;
using AuthorizationAuthority.Application.Common.Ports;
using AuthorizationAuthority.Application.Features.Journal;
using AuthorizationAuthority.Application.Features.Journal.Reconciliation;
using AuthorizationAuthority.Application.Features.Journal.Recovery;
using AuthorizationAuthority.Application.Features.Journal.Verification;
using AuthorizationAuthority.Domain.Features.Journal;
using AuthorizationAuthority.Infrastructure.Archive;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit;

namespace AuthorizationAuthority.Tests;

/// <summary>Every archive port the processor probes for, so a counting proxy can stand in for the Blob archive.</summary>
public interface ICountedArchive : ISecurityEventArchive, ISecurityEventArchiveInventory, IArchiveMetaRecords;

/// <summary>Counts every call through a port (by port and method) and forwards it unchanged. Measurement only.</summary>
public class CountingProxy<T> : DispatchProxy where T : class
{
    public object Target { get; set; } = null!;

    public string Port { get; set; } = string.Empty;

    public System.Collections.Concurrent.ConcurrentDictionary<string, long> Counts { get; set; } = null!;

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        ArgumentNullException.ThrowIfNull(targetMethod);
        Counts.AddOrUpdate(Port + "." + targetMethod.Name, 1, (_, n) => n + 1);
        try
        {
            return targetMethod.Invoke(Target, args);
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
            throw;
        }
    }
}

/// <summary>Creates counting proxies.</summary>
public static class Counting
{
    public static T Wrap<T>(object target, string port, System.Collections.Concurrent.ConcurrentDictionary<string, long> counts) where T : class
    {
        var proxy = DispatchProxy.Create<T, CountingProxy<T>>();
        var self = (CountingProxy<T>)(object)proxy;
        self.Target = target;
        self.Port = port;
        self.Counts = counts;
        return proxy;
    }
}

/// <summary>
/// SEC-BL-01 WP-5b (C-157, P2-D5): the incremental cost of W2-S and D+ per stream, measured on real stores (disposable PostgreSQL 18 and the
/// two Azurite instances). The same test runs at each code state (audit-only D+, content added, all three streams) and records which streams
/// are enforced, so the results compare like for like. Opt-in (<c>SECBL01_DPLUS_MEASURE=1</c>); synthetic data; Development/Testing evidence.
/// Security checks are never relaxed for the measurement.
/// </summary>
public sealed partial class JournalVerificationPostgresIntegrationTests
{
    private sealed record MeasuredBody(string NoteText, long CounterValue);

    /// <summary>Appends <paramref name="rows"/> Pending rows to <paramref name="stream"/> after its current head (as the Authority does).</summary>
    private static async Task<List<SecurityJournalEntry>> AppendStreamAsync(Guid org, string stream, int rows, string? archiveContainer = null, int deliver = 0)
    {
        var archive = archiveContainer is null ? null : Wp2Azurite.NewArchive(archiveContainer);
        await using var context = AdminContext();
        var state = await context.OrganizationStates.SingleAsync(s => s.OrganizationId == org, Ct);
        var head = await context.JournalEntries.Where(e => e.OrganizationId == org && e.JournalStreamName == stream).Select(e => (long?)e.JournalStreamRevision).MaxAsync(Ct) ?? 0;
        var added = new List<SecurityJournalEntry>();
        for (var i = 0; i < rows; i++)
        {
            var revision = head + i + 1;
            if (stream == JournalStreamNames.Audit)
            {
                state.AuditJournalRevision = revision;
            }
            else if (stream == JournalStreamNames.Authorization)
            {
                // The gate's projection precondition holds, so only archive delivery decides it (OrganizationGate).
                state.AuthorizationRevision = revision;
                state.ProjectionAppliedRevision = revision;
                state.ProjectionZedToken = "synthetic-zed-" + revision.ToString(CultureInfo.InvariantCulture);
            }
            else if (stream == JournalStreamNames.Content)
            {
                // The organization's content head column, as ContentRevisionCoordinator advances it (no search projection exists for these organizations).
                context.Entry(state).Property(nameof(state.ContentIntegrityRevision)).CurrentValue = revision;
            }

            var entry = JournalChain.Append(state, stream, revision, "SyntheticEvent", new MeasuredBody("synthetic " + stream + " " + revision, revision), null, null, TimeProvider.System);
            if (i < deliver && archive is not null)
            {
                var receipt = await archive.AppendAsync(org, stream, revision, Encoding.UTF8.GetBytes(entry.JournalEventBodyJson), entry.JournalEventDigest, Ct);
                entry.ArchiveDeliveryState = ArchiveDeliveryStates.Anchored;
                entry.ArchiveReceiptReference = receipt.ReceiptReference;
                entry.ArchiveReceiptDigest = receipt.ReceiptDigest;
            }

            added.Add(entry);
        }

        context.JournalEntries.AddRange(added);
        await context.SaveChangesAsync(Ct);
        return added;
    }

    private static async Task<long> PendingAsync(Guid org)
    {
        await using var connection = new NpgsqlConnection(Admin);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand("SELECT count(*) FROM authority.security_journal_entries WHERE organization_id = @org AND archive_delivery_state = 'Pending'", connection);
        command.Parameters.AddWithValue("org", org);
        return (long)(await command.ExecuteScalarAsync(Ct))!;
    }

    private sealed class MeasuredRig
    {
        public required Guid Org { get; init; }

        public required string Container { get; init; }

        public required string StoreContainer { get; init; }

        public required ArchiveProcessor Processor { get; init; }

        public required System.Collections.Concurrent.ConcurrentDictionary<string, long> Counts { get; init; }
    }

    /// <summary>An organization with an empty provisioned, pinned archive, and a processor whose every archive and store call is counted.</summary>
    private static async Task<MeasuredRig> MeasuredAsync()
    {
        await EnsureSchemaAsync();
        var (org, _, container, _) = await SeedAsync(0, 0);
        var archive = Wp2Azurite.NewArchive(container);
        var identity = ArchiveIdentityCodec.NewIdentity(DateTimeOffset.UtcNow);
        Assert.Equal(ArchiveIdentityCreateStatus.Created, (await archive.CreateIdentityAsync(identity, Ct)).Status);
        var storeContainer = await PinAsync(container, identity.ArchiveInstanceId, DPlusRecoveryApprovalVerifier.Unavailable);
        var counts = new System.Collections.Concurrent.ConcurrentDictionary<string, long>(StringComparer.Ordinal);
        var durability = Durable(container);
        var counted = new DPlusDurability(
            Counting.Wrap<IJournalVerificationRecordStore>(durability.Records, "store", counts),
            Counting.Wrap<IArchiveProvisioningStore>(durability.Provisioning, "provisioning", counts),
            Counting.Wrap<IVerificationCheckpointStore>(durability.Checkpoints, "checkpoints", counts),
            durability.Approvals);
        var processor = new ArchiveProcessor(RuntimeStore(), Counting.Wrap<ICountedArchive>(archive, "archive", counts), TimeProvider.System, new RecordingAlerts(),
            Counting.Wrap<IJournalVerificationFindingStore>(Wp2Azurite.NewFindingStore(storeContainer), "findings", counts), counted);
        return new MeasuredRig { Org = org, Container = container, StoreContainer = storeContainer, Processor = processor, Counts = counts };
    }

    private static async Task<(long Count, long Bytes)> BlobUsageAsync(Azure.Storage.Blobs.BlobContainerClient container, string prefix)
    {
        long count = 0;
        long bytes = 0;
        await foreach (var blob in container.GetBlobsAsync(Azure.Storage.Blobs.Models.BlobTraits.None, Azure.Storage.Blobs.Models.BlobStates.None, prefix, Ct))
        {
            count++;
            bytes += blob.Properties.ContentLength ?? 0;
        }

        return (count, bytes);
    }

    private static double Percentile(List<double> values, double p)
    {
        if (values.Count == 0)
        {
            return 0;
        }

        var sorted = values.Order().ToList();
        return sorted[Math.Clamp((int)Math.Ceiling(p * sorted.Count) - 1, 0, sorted.Count - 1)];
    }

    private static Dictionary<string, long> Snapshot(System.Collections.Concurrent.ConcurrentDictionary<string, long> counts) =>
        counts.OrderBy(c => c.Key, StringComparer.Ordinal).ToDictionary(c => c.Key, c => c.Value, StringComparer.Ordinal);

    private static Dictionary<string, double> PerEntry(Dictionary<string, long> counts, long entries) =>
        counts.ToDictionary(c => c.Key, c => entries == 0 ? 0 : Math.Round((double)c.Value / entries, 3), StringComparer.Ordinal);

    /// <summary>MEAS-1..MEAS-4: backlog drain throughput, per-entry store operations, commitment growth, steady-state latency, discovery overhead.</summary>
    [Fact]
    public async Task WP5BDPlusDeliveryMeasurement()
    {
        Require();
        Assert.SkipUnless(string.Equals(Environment.GetEnvironmentVariable("SECBL01_DPLUS_MEASURE"), "1", StringComparison.Ordinal),
            "SECBL01_DPLUS_MEASURE=1 not set (opt-in measurement; run scripts/dev/sec-bl-01-wp4-pg-azurite-gate.ps1 -Measure)");
        var configuration = string.Join("+", AuditDeliveryEnforcement.EnforcedStreams);
        var streams = new[] { JournalStreamNames.Audit, JournalStreamNames.Authorization, JournalStreamNames.Content };
        var backlogs = (Environment.GetEnvironmentVariable("SECBL01_DPLUS_BACKLOGS") ?? "32,256,2048").Split(',').Select(s => int.Parse(s, CultureInfo.InvariantCulture)).ToList();
        var report = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["measurement"] = "SEC-BL-01 WP-5b D+/W2-S delivery cost",
            ["enforced_streams"] = configuration,
            ["stores"] = "PostgreSQL 18.0 + Azurite 3.9.0 (two instances), loopback, single process",
            ["measured_utc"] = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
        };

        // MEAS-1/2/3: drain a backlog of B pending rows in each of the three streams.
        var drains = new List<object>();
        foreach (var backlog in backlogs)
        {
            var rig = await MeasuredAsync();
            foreach (var stream in streams)
            {
                await AppendStreamAsync(rig.Org, stream, backlog);
            }

            rig.Counts.Clear();
            var passTimes = new List<double>();
            var watch = Stopwatch.StartNew();
            var delivered = 0;
            for (var pass = 0; pass < 2000 && await PendingAsync(rig.Org) > 0; pass++)
            {
                var one = Stopwatch.StartNew();
                delivered += await rig.Processor.ProcessOrganizationAsync(rig.Org, Ct);
                passTimes.Add(one.Elapsed.TotalMilliseconds);
            }

            watch.Stop();
            Assert.Equal(0, await PendingAsync(rig.Org));
            var counts = Snapshot(rig.Counts);
            var entries = (long)backlog * streams.Length;
            var archiveIntents = await BlobUsageAsync(Wp2Azurite.RawArchiveContainer(rig.Container), "meta/dplus-intents/");
            var storeIntents = await BlobUsageAsync(Wp2Azurite.RawCheckpointContainer(rig.StoreContainer), "intents/");
            drains.Add(new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["backlog_per_stream"] = backlog,
                ["entries_total"] = entries,
                ["delivered"] = delivered,
                ["passes"] = passTimes.Count,
                ["wall_ms"] = Math.Round(watch.Elapsed.TotalMilliseconds, 1),
                ["entries_per_second"] = Math.Round(entries / watch.Elapsed.TotalSeconds, 1),
                ["ms_per_entry"] = Math.Round(watch.Elapsed.TotalMilliseconds / entries, 3),
                ["pass_ms_p50"] = Math.Round(Percentile(passTimes, 0.5), 1),
                ["pass_ms_p95"] = Math.Round(Percentile(passTimes, 0.95), 1),
                ["store_operations_total"] = counts.Values.Sum(),
                ["store_operations_per_entry"] = Math.Round((double)counts.Values.Sum() / entries, 3),
                ["operations_per_entry_by_method"] = PerEntry(counts, entries),
                ["commitments_archive"] = new { objects = archiveIntents.Count, bytes = archiveIntents.Bytes },
                ["commitments_store"] = new { objects = storeIntents.Count, bytes = storeIntents.Bytes },
                ["commitment_bytes_per_entry"] = entries == 0 ? 0 : Math.Round((double)(archiveIntents.Bytes + storeIntents.Bytes) / entries, 1),
            });
        }

        report["drain"] = drains;

        // MEAS-4: steady state, one new row per stream per pass.
        {
            var rig = await MeasuredAsync();
            foreach (var stream in streams)
            {
                await AppendStreamAsync(rig.Org, stream, 1);
            }

            await rig.Processor.ProcessOrganizationAsync(rig.Org, Ct); // genesis outside the sample
            rig.Counts.Clear();
            var times = new List<double>();
            const int Iterations = 40;
            for (var i = 0; i < Iterations; i++)
            {
                foreach (var stream in streams)
                {
                    await AppendStreamAsync(rig.Org, stream, 1);
                }

                var one = Stopwatch.StartNew();
                Assert.Equal(streams.Length, await rig.Processor.ProcessOrganizationAsync(rig.Org, Ct));
                times.Add(one.Elapsed.TotalMilliseconds);
            }

            var counts = Snapshot(rig.Counts);
            report["steady_state"] = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["iterations"] = Iterations,
                ["entries_per_pass"] = streams.Length,
                ["pass_ms_p50"] = Math.Round(Percentile(times, 0.5), 1),
                ["pass_ms_p95"] = Math.Round(Percentile(times, 0.95), 1),
                ["pass_ms_max"] = Math.Round(times.Max(), 1),
                ["operations_per_pass"] = Math.Round((double)counts.Values.Sum() / Iterations, 2),
                ["operations_per_pass_by_method"] = counts.ToDictionary(c => c.Key, c => Math.Round((double)c.Value / Iterations, 2), StringComparer.Ordinal),
            };
        }

        // MEAS-5: discovery overhead on the audit stream (always D+) before and after one executed SR-A recovery.
        {
            using var approvers = Approvers();
            var (recovered, genuine) = await StoppedAsync(3, approvers);
            await Wp2Azurite.RawArchiveContainer(recovered.Container).GetBlobClient(Key(recovered.Org, 3)).UploadAsync(genuine, Ct);
            await ExecutedAsync(recovered);
            Assert.Equal(1, await Processor(recovered).ProcessOrganizationAsync(recovered.Org, Ct));
            var clean = await MeasuredAsync();
            await AppendStreamAsync(clean.Org, JournalStreamNames.Audit, 1);
            await clean.Processor.ProcessOrganizationAsync(clean.Org, Ct);

            async Task<object> SampleAsync(Guid org, Func<Task<int>> pass, System.Collections.Concurrent.ConcurrentDictionary<string, long>? counter)
            {
                counter?.Clear();
                var times = new List<double>();
                for (var i = 0; i < 20; i++)
                {
                    await AppendStreamAsync(org, JournalStreamNames.Audit, 1);
                    var one = Stopwatch.StartNew();
                    Assert.Equal(1, await pass());
                    times.Add(one.Elapsed.TotalMilliseconds);
                }

                return new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["pass_ms_p50"] = Math.Round(Percentile(times, 0.5), 1),
                    ["pass_ms_p95"] = Math.Round(Percentile(times, 0.95), 1),
                    ["operations_per_pass"] = counter is null ? null : Math.Round((double)counter.Values.Sum() / 20, 2),
                };
            }

            var countsRecovered = new System.Collections.Concurrent.ConcurrentDictionary<string, long>(StringComparer.Ordinal);
            var durability = Durable(recovered.Container);
            var countedRecovered = new ArchiveProcessor(RuntimeStore(), Counting.Wrap<ICountedArchive>(recovered.Archive, "archive", countsRecovered), TimeProvider.System, new RecordingAlerts(),
                Counting.Wrap<IJournalVerificationFindingStore>(recovered.Findings, "findings", countsRecovered),
                new DPlusDurability(Counting.Wrap<IJournalVerificationRecordStore>(durability.Records, "store", countsRecovered), durability.Provisioning, durability.Checkpoints, durability.Approvals));
            report["discovery"] = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["clean_stream"] = await SampleAsync(clean.Org, () => clean.Processor.ProcessOrganizationAsync(clean.Org, Ct), clean.Counts),
                ["after_one_executed_recovery"] = await SampleAsync(recovered.Org, () => countedRecovered.ProcessOrganizationAsync(recovered.Org, Ct), countsRecovered),
                ["recovery_records_archive"] = (await BlobUsageAsync(Wp2Azurite.RawArchiveContainer(recovered.Container), "meta/dplus-recoveries/")).Count,
                ["stop_records_archive"] = (await BlobUsageAsync(Wp2Azurite.RawArchiveContainer(recovered.Container), "meta/dplus-stops/")).Count,
            };
        }

        var json = JsonSerializer.Serialize(report, Indented);
        TestContext.Current.SendDiagnosticMessage(json);
        if (Environment.GetEnvironmentVariable("SECBL01_EVIDENCE_DIR") is { Length: > 0 } dir)
        {
            await File.WriteAllTextAsync(Path.Combine(dir, "dplus-measurement-" + configuration.Replace('+', '-') + ".json"), json + "\n", Ct);
        }
    }
}
