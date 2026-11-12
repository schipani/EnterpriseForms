using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using EnterpriseForms.SecurityTests.Harness;
using EnterpriseForms.StageBTests.Harness;
using Xunit;

namespace EnterpriseForms.StageBTests.Tests;

/// <summary>
/// C-150 (R-02 read-stage investigation), a bounded Benchmark (never in functional runs): one indexed form, then
/// <c>R02_READ_RECORDS</c> (default 300) record creations one at a time. For each the Authority logs the derivation stages
/// (<c>IX1-DERIVE-TIMING</c>) and the reads-stage split (<c>IX1-READ-SPLIT</c>, Testing only): registry lookup, the two change-tracker
/// inspections, the approvals statement (call to last row) and EF Core's command time inside it, the number of commands, tracked entities
/// and approval rows. Then the same statement is run 50 times under <c>EXPLAIN (ANALYZE, BUFFERS)</c> as the Authority's runtime role for
/// the server-side execution time. Report: artifacts/ix-5/r02-read-stage[-R02_LABEL].json. The 20 ms p95 requirement is not assessed here
/// (that is the IX-1 §6.5 paired protocol); this only attributes the reads stage.
/// </summary>
[Collection(StageBStackCollectionDefinition.Name)]
public sealed partial class IndexedCommitReadStageMeasurementTests(StageBFixture fixture)
{
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [GeneratedRegex(@"IX1-DERIVE-TIMING record=(?<r>[0-9a-f-]{36}) reads_ms=(?<reads>[0-9.,E-]+) derive_ms=(?<derive>[0-9.,E-]+) sign_ms=(?<sign>[0-9.,E-]+) writes_ms=(?<writes>[0-9.,E-]+)")]
    private static partial Regex StagesLine();

    [GeneratedRegex(@"IX1-READ-SPLIT record=(?<r>[0-9a-f-]{36}) registry_ms=(?<registry>[0-9.,E-]+) first_scan_ms=(?<first>[0-9.,E-]+) second_scan_ms=(?<second>[0-9.,E-]+) query_ms=(?<query>[0-9.,E-]+) command_ms=(?<command>[0-9.,E-]+) commands=(?<commands>\d+) tracked_entities=(?<tracked>\d+) approval_rows=(?<rows>\d+) tracked_merge=(?<merge>\w+)")]
    private static partial Regex SplitLine();

    [GeneratedRegex(@"Execution Time: (?<t>[0-9.]+) ms")]
    private static partial Regex ExecutionTime();

    private static double D(Match m, string group) => double.Parse(m.Groups[group].Value, CultureInfo.InvariantCulture);

    private static object Stats(IEnumerable<double> values)
    {
        var v = values.Order().ToList();
        double P(double p) => v.Count == 0 ? 0 : Math.Round(v[Math.Clamp((int)Math.Ceiling(p * v.Count) - 1, 0, v.Count - 1)], 3);
        return new { count = v.Count, p50 = P(0.5), p95 = P(0.95), p99 = P(0.99), mean = v.Count == 0 ? 0 : Math.Round(v.Average(), 3), max = v.Count == 0 ? 0 : Math.Round(v[^1], 3) };
    }

    [Fact]
    [Trait("Category", "Benchmark")]
    public async Task MeasureTheIndexedCommitReadStage()
    {
        var count = int.TryParse(Environment.GetEnvironmentVariable("R02_READ_RECORDS"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : 300;
        var label = Environment.GetEnvironmentVariable("R02_LABEL") is { Length: > 0 } l ? "-" + l : string.Empty;
        var w = await C150SearchWorkspace.CreateAsync(fixture, "r02-read", 0);
        var created = new HashSet<Guid>();
        var started = DateTimeOffset.UtcNow;
        for (var i = 0; i < count; i++)
        {
            var r = await C150SearchWorkspace.RetryingAsync(() => fixture.Api.PostAsync($"/api/v1/form-definitions/{w.Form:D}/records", w.S.Token(w.S.Clerk), new { values = new Dictionary<string, string> { ["exhibit_count"] = i.ToString(CultureInfo.InvariantCulture) } }, "r02-" + Guid.CreateVersion7().ToString("N")));
            Assert.True(r.IsSuccess, r.Body);
            created.Add(r.Root.GetProperty("formRecordId").GetGuid());
        }

        var elapsed = DateTimeOffset.UtcNow - started;
        var logs = fixture.Stack.Logs("authority", 400_000);
        var stages = StagesLine().Matches(logs).Where(m => created.Contains(Guid.Parse(m.Groups["r"].Value))).GroupBy(m => m.Groups["r"].Value).Select(g => g.Last()).ToList();
        var splits = SplitLine().Matches(logs).Where(m => created.Contains(Guid.Parse(m.Groups["r"].Value))).GroupBy(m => m.Groups["r"].Value).Select(g => g.Last()).ToList();
        var reads = stages.ToDictionary(m => m.Groups["r"].Value, m => D(m, "reads"));

        // The approvals statement as EF Core issues it (same predicates), executed as the runtime role, 50 times.
        // Five calls of ten statements each (one call of fifty exceeds the Windows command-line limit of the docker invocation).
        var sql = $"SET ROLE authority_runtime; " + string.Concat(Enumerable.Repeat(
            $"EXPLAIN (ANALYZE, BUFFERS) SELECT f.* FROM authority.field_search_approvals AS f WHERE f.organization_id = '{w.S.OrganizationId:D}' AND f.form_definition_id = '{w.Form:D}' AND f.approval_state_name = 'Active' " +
            $"AND EXISTS (SELECT 1 FROM authority_search.search_field_coverages AS s WHERE s.organization_id = '{w.S.OrganizationId:D}' AND s.form_definition_id = '{w.Form:D}' AND s.field_search_approval_id = f.field_search_approval_id AND s.coverage_state_name <> 'Withdrawn') ORDER BY f.field_search_approval_id; ", 10));
        var explain = string.Concat(Enumerable.Range(0, 5).Select(_ => fixture.Stack.AuthorityAdministratorSql(sql) + "\n"));
        var server = ExecutionTime().Matches(explain).Select(m => double.Parse(m.Groups["t"].Value, CultureInfo.InvariantCulture)).ToList();
        var plan = explain.Split('\n').TakeWhile(line => !line.Contains("Execution Time", StringComparison.Ordinal)).ToList();

        var report = new
        {
            kind = "C-150 R-02 read-stage split of the indexed commit's derivation (focused, local Docker, synthetic; bounded; indicative, not the §6.5 protocol)",
            measured_on = DateTimeOffset.UtcNow,
            git_commit = ComposeStack.GitCommit(),
            stack = fixture.Stack.ImageFingerprint(),
            records_created = created.Count,
            creation_seconds = Math.Round(elapsed.TotalSeconds, 1),
            stage_lines = stages.Count,
            split_lines = splits.Count,
            stages_ms = new
            {
                reads = Stats(stages.Select(m => D(m, "reads"))),
                derive = Stats(stages.Select(m => D(m, "derive"))),
                sign = Stats(stages.Select(m => D(m, "sign"))),
                writes = Stats(stages.Select(m => D(m, "writes"))),
            },
            read_split_ms = new
            {
                registry = Stats(splits.Select(m => D(m, "registry"))),
                first_change_tracker_scan = Stats(splits.Select(m => D(m, "first"))),
                second_change_tracker_scan = Stats(splits.Select(m => D(m, "second"))),
                approvals_query_total = Stats(splits.Select(m => D(m, "query"))),
                approvals_command_execution = Stats(splits.Select(m => D(m, "command"))),
                approvals_client_read_and_materialization = Stats(splits.Select(m => D(m, "query") - D(m, "command"))),
                unattributed_remainder = Stats(splits.Where(m => reads.ContainsKey(m.Groups["r"].Value)).Select(m => reads[m.Groups["r"].Value] - D(m, "registry") - D(m, "first") - D(m, "second") - D(m, "query"))),
            },
            read_split_counts = new
            {
                commands = Stats(splits.Select(m => (double)int.Parse(m.Groups["commands"].Value, CultureInfo.InvariantCulture))),
                tracked_entities = Stats(splits.Select(m => (double)int.Parse(m.Groups["tracked"].Value, CultureInfo.InvariantCulture))),
                approval_rows = Stats(splits.Select(m => (double)int.Parse(m.Groups["rows"].Value, CultureInfo.InvariantCulture))),
                tracked_merge_path = splits.Count(m => m.Groups["merge"].Value == "True"),
            },
            approvals_statement_server_execution_ms = Stats(server),
            approvals_statement_plan = plan,
        };
        var directory = Path.Combine(ComposeStack.RepositoryRoot, "artifacts", "ix-5");
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(Path.Combine(directory, "r02-read-stage" + label + ".json"), JsonSerializer.Serialize(report, Indented), Ct);
        Assert.True(splits.Count >= created.Count * 9 / 10, $"split lines found for {splits.Count} of {created.Count} records");
    }
}
