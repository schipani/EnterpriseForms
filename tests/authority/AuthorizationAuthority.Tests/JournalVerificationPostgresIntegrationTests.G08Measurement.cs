using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using AuthorizationAuthority.Application.Common;
using AuthorizationAuthority.Application.Common.Ports;
using AuthorizationAuthority.Application.Features.Content;
using AuthorizationAuthority.Application.Features.Journal;
using AuthorizationAuthority.Application.Features.Journal.Recovery;
using AuthorizationAuthority.Application.Features.Journal.Verification;
using AuthorizationAuthority.Domain.Features.Content;
using AuthorizationAuthority.Domain.Features.Journal;
using AuthorizationAuthority.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace AuthorizationAuthority.Tests;

/// <summary>
/// SEC-BL-01 G08 / E-6 / E-7 (C-158, package §7): the cost of trusted delivery receipts on real stores (disposable PostgreSQL 18 and the two
/// Azurite instances): gate evaluation before (the pending count alone, the pre-G08 archive condition) and after (journal head + receipt),
/// cold and warm, store operations per cold receipt, a new head (revocation) refresh, concurrency, and the content-release check. Opt-in
/// (<c>SECBL01_DPLUS_MEASURE=1</c>); synthetic data; Development/Testing evidence. No check is relaxed for the measurement.
/// </summary>
public sealed partial class JournalVerificationPostgresIntegrationTests
{
    private static readonly JsonSerializerOptions IndentedJson = new() { WriteIndented = true };

    private sealed record MeasuredCheckpointBody(Guid FormRecordId, long CommittedContentRevision, Guid CommittedOperationId, string ManifestRootHex);

    private sealed class ReceiptRig
    {
        public required MeasuredRig Rig { get; init; }

        public required System.Collections.Concurrent.ConcurrentDictionary<string, long> Counts { get; init; }

        public TrustedJournalReceipts NewVerifier()
        {
            var durability = Durable(Rig.Container);
            var counted = new DPlusDurability(
                Counting.Wrap<IJournalVerificationRecordStore>(durability.Records, "store", Counts),
                Counting.Wrap<IArchiveProvisioningStore>(durability.Provisioning, "provisioning", Counts),
                Counting.Wrap<IVerificationCheckpointStore>(durability.Checkpoints, "checkpoints", Counts),
                durability.Approvals);
            // WP-7A (A-9 RV-2): the freshness bound under measurement (default 1 s; SECBL01_RECEIPT_MAX_AGE_MS=0 measures re-establishment on every access).
            var maxAge = int.TryParse(Environment.GetEnvironmentVariable("SECBL01_RECEIPT_MAX_AGE_MS"), out var ms) ? TimeSpan.FromMilliseconds(ms) : new TrustedJournalReceiptOptions().CurrentStateMaxAge;
            return new TrustedJournalReceipts(Counting.Wrap<ICountedArchive>(Wp2Azurite.NewArchive(Rig.Container), "archive", Counts), counted,
                new TrustedJournalReceiptOptions { CurrentStateMaxAge = maxAge });
        }

        public static AuthorityStore Store(IJournalReceiptVerifier verifier) =>
            new(new RuntimeContextFactory(Runtime!), ContentAcceptancePolicy.Resolve(null, null), journalReceipts: verifier);
    }

    /// <summary>Appends one content checkpoint entry (ContentHeadCommitted, canonical body) and returns the head it names.</summary>
    private static async Task<RecordContentHead> AppendContentCheckpointAsync(Guid org)
    {
        await using var context = AdminContext();
        var state = await context.OrganizationStates.SingleAsync(s => s.OrganizationId == org, Ct);
        var revision = (await context.JournalEntries.Where(e => e.OrganizationId == org && e.JournalStreamName == JournalStreamNames.Content)
            .Select(e => (long?)e.JournalStreamRevision).MaxAsync(Ct) ?? 0) + 1;
        context.Entry(state).Property(nameof(state.ContentIntegrityRevision)).CurrentValue = revision;
        var head = new RecordContentHead
        {
            OrganizationId = org, FormRecordId = Guid.CreateVersion7(), CommittedContentRevision = 1, CommittedOperationId = Guid.CreateVersion7(),
            CommittedManifestRoot = RandomNumberGenerator.GetBytes(32),
        };
        var entry = JournalChain.Append(state, JournalStreamNames.Content, revision, ContentCheckpointReceipts.CheckpointEventKind,
            new MeasuredCheckpointBody(head.FormRecordId, 1, head.CommittedOperationId!.Value, Convert.ToHexStringLower(head.CommittedManifestRoot)), null, head.CommittedOperationId, TimeProvider.System);
        context.JournalEntries.Add(entry);
        await context.SaveChangesAsync(Ct);
        return head;
    }

    private static async Task DeliverAllAsync(MeasuredRig rig)
    {
        for (var pass = 0; pass < 200 && await PendingAsync(rig.Org) > 0; pass++)
        {
            await rig.Processor.ProcessOrganizationAsync(rig.Org, Ct);
        }

        Assert.Equal(0, await PendingAsync(rig.Org));
    }

    private static Dictionary<string, object?> Latency(List<double> values) => new(StringComparer.Ordinal)
    {
        ["samples"] = values.Count,
        ["p50_ms"] = Math.Round(Percentile(values, 0.5), 3),
        ["p95_ms"] = Math.Round(Percentile(values, 0.95), 3),
        ["p99_ms"] = Math.Round(Percentile(values, 0.99), 3),
        ["mean_ms"] = values.Count == 0 ? 0 : Math.Round(values.Average(), 3),
    };

    private static async Task<double> TimeAsync(Func<Task> action)
    {
        var watch = Stopwatch.StartNew();
        await action();
        return watch.Elapsed.TotalMilliseconds;
    }

    /// <summary>G08-MEAS-1..G08-MEAS-6.</summary>
    [Fact]
    public async Task G08ReceiptMeasurement()
    {
        Require();
        Assert.SkipUnless(string.Equals(Environment.GetEnvironmentVariable("SECBL01_DPLUS_MEASURE"), "1", StringComparison.Ordinal),
            "SECBL01_DPLUS_MEASURE=1 not set (opt-in measurement; run scripts/dev/sec-bl-01-wp4-pg-azurite-gate.ps1 -Measure)");
        const int Iterations = 200;
        var rig = await MeasuredAsync();
        await AppendStreamAsync(rig.Org, JournalStreamNames.Authorization, 64);
        await AppendStreamAsync(rig.Org, JournalStreamNames.Audit, 8);
        var contentHead = await AppendContentCheckpointAsync(rig.Org);
        await DeliverAllAsync(rig);
        var counts = new System.Collections.Concurrent.ConcurrentDictionary<string, long>(StringComparer.Ordinal);
        var receipts = new ReceiptRig { Rig = rig, Counts = counts };
        var report = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["measurement"] = "SEC-BL-01 G08 / E-6 / E-7 trusted delivery receipt cost",
            ["stores"] = "PostgreSQL 18.0 + Azurite 3.9.0 (two instances), loopback, single process",
            ["measured_utc"] = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
            ["authorization_entries"] = 64,
            ["iterations"] = Iterations,
        };

        // G08-MEAS-1: gate evaluation with an open scope and the organization state read (as on the request path).
        var before = new List<double>();
        var warm = new List<double>();
        var cold = new List<double>();
        var shared = receipts.NewVerifier();
        await using (var scope = await ReceiptRig.Store(shared).BeginScopeAsync(Ct))
        {
            var state = (await scope.ReadOrganizationAsync(rig.Org, Ct))!;
            Assert.Equal(AuthorizationAuthority.Contracts.V1.ProjectionGateState.Ready, await OrganizationGate.EvaluateAsync(scope, state, Ct)); // warms the cache
            for (var i = 0; i < Iterations; i++)
            {
                // The pre-G08 archive condition: the pending count (plus the in-memory projection comparison, negligible).
                before.Add(await TimeAsync(async () => Assert.Equal(0, await scope.CountPendingArchiveAsync(rig.Org, JournalStreamNames.Authorization, Ct))));
                warm.Add(await TimeAsync(async () => Assert.Equal(AuthorizationAuthority.Contracts.V1.ProjectionGateState.Ready, await OrganizationGate.EvaluateAsync(scope, state, Ct))));
            }
        }

        counts.Clear();
        for (var i = 0; i < Iterations; i++)
        {
            var fresh = receipts.NewVerifier(); // a restarted process: empty cache
            await using var scope = await ReceiptRig.Store(fresh).BeginScopeAsync(Ct);
            var state = (await scope.ReadOrganizationAsync(rig.Org, Ct))!;
            cold.Add(await TimeAsync(async () => Assert.Equal(AuthorizationAuthority.Contracts.V1.ProjectionGateState.Ready, await OrganizationGate.EvaluateAsync(scope, state, Ct))));
            Assert.Equal(1, fresh.StoreVerifications);
        }

        var coldCounts = Snapshot(counts);
        report["gate"] = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["before_pending_count_only"] = Latency(before),
            ["after_warm_receipt_cached"] = Latency(warm),
            ["after_cold_receipt_established"] = Latency(cold),
            ["warm_incremental_p50_ms"] = Math.Round(Percentile(warm, 0.5) - Percentile(before, 0.5), 3),
            ["cold_store_operations_per_verification"] = Math.Round((double)coldCounts.Values.Sum() / Iterations, 3),
            ["cold_store_operations_by_method"] = PerEntry(coldCounts, Iterations),
        };

        // G08-MEAS-2: a new head (a committed revocation) after delivery: the first evaluation establishes its receipt, later ones are cached.
        var firstAfterNewHead = new List<double>();
        var secondAfterNewHead = new List<double>();
        counts.Clear();
        for (var i = 0; i < 20; i++)
        {
            await AppendStreamAsync(rig.Org, JournalStreamNames.Authorization, 1);
            await using (var scope = await ReceiptRig.Store(shared).BeginScopeAsync(Ct))
            {
                var state = (await scope.ReadOrganizationAsync(rig.Org, Ct))!;
                Assert.Equal(AuthorizationAuthority.Contracts.V1.ProjectionGateState.AwaitingArchive, await OrganizationGate.EvaluateAsync(scope, state, Ct)); // closed until delivered
            }

            await DeliverAllAsync(rig);
            await using (var scope = await ReceiptRig.Store(shared).BeginScopeAsync(Ct))
            {
                var state = (await scope.ReadOrganizationAsync(rig.Org, Ct))!;
                firstAfterNewHead.Add(await TimeAsync(async () => Assert.Equal(AuthorizationAuthority.Contracts.V1.ProjectionGateState.Ready, await OrganizationGate.EvaluateAsync(scope, state, Ct))));
                secondAfterNewHead.Add(await TimeAsync(async () => Assert.Equal(AuthorizationAuthority.Contracts.V1.ProjectionGateState.Ready, await OrganizationGate.EvaluateAsync(scope, state, Ct))));
            }
        }

        report["new_head_refresh"] = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["first_evaluation_after_delivery"] = Latency(firstAfterNewHead),
            ["second_evaluation"] = Latency(secondAfterNewHead),
            ["note"] = "each new head is AwaitingArchive until delivered (asserted), then needs exactly one receipt establishment",
        };

        // G08-MEAS-3: concurrency, 16 workers x 25 evaluations each, warm (shared verifier) and cold (one fresh verifier per worker).
        foreach (var mode in new[] { "warm", "cold" })
        {
            var samples = new System.Collections.Concurrent.ConcurrentBag<double>();
            var watch = Stopwatch.StartNew();
            await Task.WhenAll(Enumerable.Range(0, 16).Select(async _ =>
            {
                var verifier = mode == "warm" ? shared : receipts.NewVerifier();
                for (var i = 0; i < 25; i++)
                {
                    await using var scope = await ReceiptRig.Store(verifier).BeginScopeAsync(Ct);
                    var state = (await scope.ReadOrganizationAsync(rig.Org, Ct))!;
                    samples.Add(await TimeAsync(async () => Assert.Equal(AuthorizationAuthority.Contracts.V1.ProjectionGateState.Ready, await OrganizationGate.EvaluateAsync(scope, state, Ct))));
                }
            }));
            watch.Stop();
            report["concurrency_" + mode] = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["workers"] = 16,
                ["evaluations"] = samples.Count,
                ["wall_ms"] = Math.Round(watch.Elapsed.TotalMilliseconds, 1),
                ["evaluations_per_second"] = Math.Round(samples.Count / watch.Elapsed.TotalSeconds, 1),
                ["latency"] = Latency(samples.ToList()),
            };
        }

        // G08-MEAS-4: content release check (CR-1 binding + CR-2 receipt) versus the pre-G08 row-state check.
        var contentBefore = new List<double>();
        var contentWarm = new List<double>();
        var contentCold = new List<double>();
        SecurityJournalEntry checkpoint;
        await using (var scope = await ReceiptRig.Store(shared).BeginScopeAsync(Ct))
        {
            checkpoint = (await scope.FindContentCommitJournalEntryAsync(rig.Org, contentHead.CommittedOperationId!.Value, Ct))!;
            Assert.True(await ContentCheckpointReceipts.IsDeliveredAsync(scope, contentHead, checkpoint, Ct));
            for (var i = 0; i < Iterations; i++)
            {
                contentBefore.Add(await TimeAsync(async () =>
                {
                    var row = await scope.FindContentCommitJournalEntryAsync(rig.Org, contentHead.CommittedOperationId!.Value, Ct);
                    Assert.Equal(ArchiveDeliveryStates.Anchored, row!.ArchiveDeliveryState);
                }));
                contentWarm.Add(await TimeAsync(async () =>
                {
                    var row = await scope.FindContentCommitJournalEntryAsync(rig.Org, contentHead.CommittedOperationId!.Value, Ct);
                    Assert.True(await ContentCheckpointReceipts.IsDeliveredAsync(scope, contentHead, row, Ct));
                }));
            }
        }

        for (var i = 0; i < Iterations; i++)
        {
            await using var scope = await ReceiptRig.Store(receipts.NewVerifier()).BeginScopeAsync(Ct);
            contentCold.Add(await TimeAsync(async () =>
            {
                var row = await scope.FindContentCommitJournalEntryAsync(rig.Org, contentHead.CommittedOperationId!.Value, Ct);
                Assert.True(await ContentCheckpointReceipts.IsDeliveredAsync(scope, contentHead, row, Ct));
            }));
        }

        var binding = new List<double>();
        for (var i = 0; i < Iterations; i++)
        {
            binding.Add(await TimeAsync(() =>
            {
                Assert.True(ContentCheckpointReceipts.Binds(checkpoint, contentHead));
                return Task.CompletedTask;
            }));
        }

        report["content_release_check"] = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["before_row_state_only"] = Latency(contentBefore),
            ["after_warm"] = Latency(contentWarm),
            ["after_cold"] = Latency(contentCold),
            ["binding_only_in_process"] = Latency(binding),
            ["note"] = "checkpoint lookup + release decision only; HMAC/manifest verification of the content itself is unchanged and not included",
        };

        // G08-MEAS-5: cache footprint.
        report["cache"] = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["shared_verifier_cached_receipts"] = shared.CachedReceipts,
            ["shared_verifier_store_verifications"] = shared.StoreVerifications,
            ["limit"] = 200_000,
            ["current_state_max_age_ms"] = shared.Options.CurrentStateMaxAge.TotalMilliseconds, // WP-7A (A-9 RV-2)
        };

        var json = JsonSerializer.Serialize(report, IndentedJson);
        Console.WriteLine(json);
        if (Environment.GetEnvironmentVariable("SECBL01_EVIDENCE_DIR") is { Length: > 0 } dir)
        {
            Directory.CreateDirectory(dir);
            await File.WriteAllTextAsync(Path.Combine(dir, "g08-receipt-measurement" + (Environment.GetEnvironmentVariable("SECBL01_RECEIPT_MAX_AGE_MS") is { Length: > 0 } ms ? "-maxage-" + ms + "ms" : string.Empty) + ".json"), json, Ct);
        }
    }
}
