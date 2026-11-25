using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using EnterpriseForms.SecurityTests.Harness;
using Xunit;

namespace EnterpriseForms.StageBTests.Tests;

/// <summary>
/// C-149 (R-02): the interleaved, paired commit-latency benchmark of IX-1 evidence section 6.5 (a Benchmark; never in focused functional
/// runs). Each iteration creates one record on the indexed form and one on an identical plain form (no search approvals), in a
/// randomized order with a recorded seed, so both series see the same load at the same time. The first <c>P5_WARMUP_PAIRS</c>
/// (default 50) pairs are excluded and reported; nothing else is excluded. Measured value: the Authority-side ContentService.CommitAsync
/// duration and command count from the Testing-only IX1-COMMIT-TIMING log line. Primary statistic: p95(indexed) − p95(plain) over the
/// run's pairs, with a 95% percentile-bootstrap interval resampling pairs (<c>P5_BOOTSTRAP</c>, default 10,000). Raw samples go to
/// CSV beside the JSON report (artifacts/ix-5/r02/p5-paired-{P5_LABEL}.*); the pooled multi-run criterion is computed from the CSVs.
/// No assertion on latency: the gate is decided on the evidence, not by this test.
/// </summary>
public sealed partial class SearchIndexFoundationTests
{
    [GeneratedRegex(@"IX1-COMMIT-TIMING record=(?<record>[0-9a-f-]{36}) elapsed_ms=(?<elapsed>[0-9.,]+) db_commands=(?<commands>[0-9]+)")]
    private static partial Regex CommitTimingLine();

    [GeneratedRegex(@"IX1-DERIVE-TIMING record=(?<record>[0-9a-f-]{36}) reads_ms=(?<reads>[0-9.,]+) derive_ms=(?<derive>[0-9.,]+) sign_ms=(?<sign>[0-9.,]+) writes_ms=(?<writes>[0-9.,]+)")]
    private static partial Regex DeriveTimingLine();

    private static double Nearest(List<double> sorted, double p) => sorted[Math.Clamp((int)Math.Ceiling(p * sorted.Count) - 1, 0, sorted.Count - 1)];

    private static double Number(string text) => double.Parse(text.Replace(',', '.'), CultureInfo.InvariantCulture);

    [Fact]
    [Trait("Category", "Benchmark")]
    public async Task MeasurePairedInterleavedCommitLatencyWithAndWithoutTheIndex()
    {
        int Setting(string name, int fallback) => int.TryParse(Environment.GetEnvironmentVariable(name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : fallback;
        var pairs = Setting("P5_PAIRS", 2000);
        var warmup = Setting("P5_WARMUP_PAIRS", 50);
        var resamples = Setting("P5_BOOTSTRAP", 10_000);
        var seed = Setting("P5_SEED", 149);
        var label = Environment.GetEnvironmentVariable("P5_LABEL") is { Length: > 0 } l ? l : "run";
        // C-149 continuation: a time cap for the creation loop (0 = none); a capped run is reported as partial, never as complete.
        var maximumSeconds = Setting("P5_MAX_SECONDS", 0);
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var capped = false;
        var indexed = await WorkspaceAsync("p5-paired-indexed");
        var plain = await WorkspaceAsync("p5-paired-plain", approve: false);
        var random = new Random(seed);
        var created = new List<(int Pair, string Series, int Order, Guid Record, DateTimeOffset At)>();
        var started = DateTimeOffset.UtcNow;
        for (var pair = 0; pair < warmup + pairs; pair++)
        {
            if (maximumSeconds > 0 && stopwatch.Elapsed.TotalSeconds >= maximumSeconds)
            {
                capped = true;
                break;
            }

            var indexedFirst = random.Next(2) == 0;
            foreach (var (w, series, order) in indexedFirst ? new[] { (indexed, "indexed", 0), (plain, "plain", 1) } : new[] { (plain, "plain", 0), (indexed, "indexed", 1) })
            {
                var at = DateTimeOffset.UtcNow;
                var record = await CreateAsync(w, Values($"Paired {pair}", pair % 1000, pair % 2 == 0 ? "open" : "closed"));
                created.Add((pair, series, order, record, at));
            }
        }

        var ended = DateTimeOffset.UtcNow;
        var log = fixture.Stack.Compose($"logs --no-color --since {started.AddSeconds(-5):yyyy-MM-ddTHH:mm:ssZ} authority", 300, throwOnError: false);
        var commits = CommitTimingLine().Matches(log).ToDictionary(m => Guid.Parse(m.Groups["record"].Value), m => (Elapsed: Number(m.Groups["elapsed"].Value), Commands: int.Parse(m.Groups["commands"].Value, CultureInfo.InvariantCulture)));
        var stages = DeriveTimingLine().Matches(log).ToDictionary(m => Guid.Parse(m.Groups["record"].Value), m => (Reads: Number(m.Groups["reads"].Value), Derive: Number(m.Groups["derive"].Value), Sign: Number(m.Groups["sign"].Value), Writes: Number(m.Groups["writes"].Value)));

        // Raw samples (every created record, warm-up included and flagged).
        var csv = new StringBuilder("pair,warmup,series,order,form_record_id,created_on,elapsed_ms,db_commands,reads_ms,derive_ms,sign_ms,writes_ms\n");
        foreach (var c in created)
        {
            var hasTiming = commits.TryGetValue(c.Record, out var t);
            var hasStage = stages.TryGetValue(c.Record, out var st);
            csv.Append(CultureInfo.InvariantCulture, $"{c.Pair},{(c.Pair < warmup ? 1 : 0)},{c.Series},{c.Order},{c.Record:D},{c.At:O},{(hasTiming ? t.Elapsed.ToString(CultureInfo.InvariantCulture) : string.Empty)},{(hasTiming ? t.Commands.ToString(CultureInfo.InvariantCulture) : string.Empty)},")
               .Append(hasStage ? string.Create(CultureInfo.InvariantCulture, $"{st.Reads},{st.Derive},{st.Sign},{st.Writes}") : ",,,").Append('\n');
        }

        // Measured pairs: both members timed, warm-up excluded.
        var measured = created.Where(c => c.Pair >= warmup).GroupBy(c => c.Pair)
            .Select(g => (Indexed: g.Single(x => x.Series == "indexed").Record, Plain: g.Single(x => x.Series == "plain").Record))
            .Where(p => commits.ContainsKey(p.Indexed) && commits.ContainsKey(p.Plain))
            .Select(p => (Indexed: commits[p.Indexed].Elapsed, Plain: commits[p.Plain].Elapsed, IndexedCommands: commits[p.Indexed].Commands, PlainCommands: commits[p.Plain].Commands)).ToList();
        var indexedSorted = measured.Select(p => p.Indexed).Order().ToList();
        var plainSorted = measured.Select(p => p.Plain).Order().ToList();
        double DeltaP95((double Indexed, double Plain, int IndexedCommands, int PlainCommands)[] sample) =>
            Nearest(sample.Select(p => p.Indexed).Order().ToList(), 0.95) - Nearest(sample.Select(p => p.Plain).Order().ToList(), 0.95);
        var bootstrap = new Random(seed + 1);
        var deltas = new List<double>(resamples);
        for (var r = 0; r < resamples && measured.Count > 0; r++)
        {
            var draw = new (double, double, int, int)[measured.Count];
            for (var i = 0; i < draw.Length; i++)
            {
                draw[i] = measured[bootstrap.Next(measured.Count)];
            }

            deltas.Add(DeltaP95(draw));
        }

        deltas.Sort();
        var pairDeltas = measured.Select(p => p.Indexed - p.Plain).Order().ToList();
        object Series(List<double> s) => new { p50 = Math.Round(Nearest(s, 0.50), 2), p95 = Math.Round(Nearest(s, 0.95), 2), p99 = Math.Round(Nearest(s, 0.99), 2), max = Math.Round(s[^1], 2), largest_ten = s.TakeLast(10).Reverse().Select(v => Math.Round(v, 2)) };
        var report = new
        {
            kind = "C-149 R-02 paired interleaved commit-latency benchmark (IX-1 evidence section 6.5 design; local Docker; Vault Transit dev ES256; indicative, not staging)",
            label,
            git_commit = ComposeStack.GitCommit(),
            stack = fixture.Stack.ImageFingerprint(),
            seed,
            warmup_pairs_excluded = warmup,
            pairs_requested = pairs,
            pairs_measured = measured.Count,
            started_on = started,
            ended_on = ended,
            sweep_interval_seconds = Environment.GetEnvironmentVariable("IX1_SEARCH_SWEEP_INTERVAL_SECONDS") ?? "unset: security-test overlay default (300 s since BH-1, C-193)",
            tier2_mode = Environment.GetEnvironmentVariable("IX5_ACCESS_MODE") ?? "Dark (security-test default)",
            indexed_ms = Series(indexedSorted),
            plain_ms = Series(plainSorted),
            delta_p50_ms = Math.Round(Nearest(indexedSorted, 0.50) - Nearest(plainSorted, 0.50), 2),
            delta_p95_ms = Math.Round(Nearest(indexedSorted, 0.95) - Nearest(plainSorted, 0.95), 2),
            delta_p99_ms = Math.Round(Nearest(indexedSorted, 0.99) - Nearest(plainSorted, 0.99), 2),
            delta_p95_bootstrap = new { resamples = deltas.Count, ci95_lower_ms = Math.Round(Nearest(deltas, 0.025), 2), ci95_upper_ms = Math.Round(Nearest(deltas, 0.975), 2) },
            per_pair_difference_ms = new { p50 = Math.Round(Nearest(pairDeltas, 0.50), 2), p95 = Math.Round(Nearest(pairDeltas, 0.95), 2), p99 = Math.Round(Nearest(pairDeltas, 0.99), 2), note = "percentiles of (indexed minus plain) within each pair; not the same statistic as delta_p95_ms, which is p95(indexed) minus p95(plain)" },
            capped_by_time_limit = capped,
            time_limit_seconds = maximumSeconds,
            creation_seconds = Math.Round(stopwatch.Elapsed.TotalSeconds, 1),
            db_commands = new { indexed_median = measured.Select(p => p.IndexedCommands).Order().ElementAt(measured.Count / 2), plain_median = measured.Select(p => p.PlainCommands).Order().ElementAt(measured.Count / 2), indexed_max = measured.Max(p => p.IndexedCommands), plain_max = measured.Max(p => p.PlainCommands) },
            gate = new { p95_delta_ms = 20, pass_criterion = "section 6.5: pooled upper 95% bound at or under 20 ms and no run's interval entirely above 20 ms (computed across runs from the CSVs)" },
            missing_timing_lines = created.Count(c => !commits.ContainsKey(c.Record)),
        };
        var directory = Path.Combine(ComposeStack.RepositoryRoot, "artifacts", "ix-5", "r02");
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(Path.Combine(directory, $"p5-paired-{label}.csv"), csv.ToString(), Ct);
        await File.WriteAllTextAsync(Path.Combine(directory, $"p5-paired-{label}.json"), JsonSerializer.Serialize(report, Indented), Ct);
        Assert.True(capped || measured.Count >= pairs * 0.95, $"only {measured.Count} of {pairs} pairs had both timing lines");
    }
}
