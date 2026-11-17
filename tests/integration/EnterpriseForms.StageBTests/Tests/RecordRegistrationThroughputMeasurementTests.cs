using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using EnterpriseForms.SecurityTests.Harness;
using EnterpriseForms.StageBTests.Harness;
using Xunit;

namespace EnterpriseForms.StageBTests.Tests;

/// <summary>
/// C-137 (R-37) bounded measurement of record creation (a Benchmark; never in focused functional runs). One synthetic
/// organization grows serially through the public API to each size in <c>R37_SIZES</c> (default 100 and 1,000). At each size,
/// after a pause, <c>R37_WINDOW</c> creations (default 40) run serially, then the same number at concurrency 4. For every
/// window: the client latency, attempts and refusal codes; the business phase durations (event 7310), the Authority's
/// RegisterRecord command phases including the organization-lock wait and the relationship-graph load (event 9306), the
/// content prepare and commit lock waits (event 9307), and, from AuthorityDb timestamps, the archive and projection waits.
/// The api and authority containers are recreated with <c>R37_OPERATION_TIMING=true</c> and restored afterwards. Synthetic
/// data only. Report: artifacts/r-37/record-registration.json. No latency assertion.
/// </summary>
[Collection(StageBStackCollectionDefinition.Name)]
public sealed partial class RecordRegistrationThroughputMeasurementTests(StageBFixture fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    [GeneratedRegex(@"R37-OPERATION operation=CreateRecord organization=(?<org>[0-9a-f-]{36}) outcome=(?<outcome>\S+) polls=(?<polls>[0-9]+) total_ms=(?<total>[0-9.]+) phases=(?<phases>\S+)")]
    private static partial Regex OperationLine();

    [GeneratedRegex(@"R37-COMMAND organization=(?<org>[0-9a-f-]{36}) kind=RegisterRecord outcome=(?<outcome>\S+) lock_wait_ms=(?<lock>[0-9.]+) graph_ms=(?<graph>[0-9.]+) edges=(?<edges>[0-9]+) plan_ms=(?<plan>[0-9.]+) commit_ms=(?<commit>[0-9.]+) total_ms=(?<total>[0-9.]+)")]
    private static partial Regex CommandLine();

    [GeneratedRegex(@"R37-CONTENT organization=(?<org>[0-9a-f-]{36}) operation=(?<op>Prepare|Commit) outcome=(?<outcome>\S+) lock_wait_ms=(?<lock>[0-9.]+) total_ms=(?<total>[0-9.]+)")]
    private static partial Regex ContentLine();

    private static double D(string value) => double.Parse(value, CultureInfo.InvariantCulture);

    private static object Stats(IEnumerable<double> values)
    {
        var v = values.Order().ToList();
        double P(double p) => v.Count == 0 ? 0 : Math.Round(v[Math.Clamp((int)Math.Ceiling(p * v.Count) - 1, 0, v.Count - 1)], 1);
        return new { count = v.Count, mean = v.Count == 0 ? 0 : Math.Round(v.Average(), 1), p50 = P(0.5), p95 = P(0.95), max = v.Count == 0 ? 0 : Math.Round(v[^1], 1) };
    }

    private string AuthorityQuery(string sql)
    {
        var encodedSql = Convert.ToBase64String(Encoding.UTF8.GetBytes(sql));
        var shell = $"echo {encodedSql} | base64 -d | PGPASSWORD=\"$POSTGRES_PASSWORD\" PGOPTIONS='-c search_path=authority' psql -U \"$POSTGRES_USER\" -d \"$POSTGRES_DB\" -v ON_ERROR_STOP=1 -qAt";
        return fixture.Stack.Compose($"exec -T authority-db sh -c \"echo {Convert.ToBase64String(Encoding.UTF8.GetBytes(shell))} | base64 -d | sh\"", 120);
    }

    private sealed record Creation(double Milliseconds, int Attempts, List<string> Refusals);

    [Fact]
    [Trait("Category", "Benchmark")]
    public async Task MeasureRecordCreationPhasesAtOrganizationSizes()
    {
        var sizes = (Environment.GetEnvironmentVariable("R37_SIZES") ?? "100,1000").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(int.Parse).Order().ToList();
        var window = int.Parse(Environment.GetEnvironmentVariable("R37_WINDOW") ?? "40", CultureInfo.InvariantCulture);
        Environment.SetEnvironmentVariable("R37_OPERATION_TIMING", "true");
        fixture.Stack.Compose("up -d --no-deps --force-recreate --wait authority api", 300);
        var windows = new List<object>();
        var total = Stopwatch.StartNew();
        try
        {
            var s = new StageBScenario(fixture, "r37-registration");
            await s.BootstrapAsync();
            var current = await fixture.Api.GetAsync("/api/v1/organization/security-policy", s.Token(s.Designer));
            Assert.True((await fixture.Api.SendAsync(HttpMethod.Put, "/api/v1/organization/security-policy", s.Token(s.Designer), new { minimumFieldClassification = "Internal", plaintextInternalFieldsPermitted = true, plaintextDowngradePermitted = false, plaintextSearchPermitted = true, policyStatementText = "C-137 measurement (synthetic).", expectedPolicyRevision = current.Root.GetProperty("securityPolicyRevision").GetInt64() }, Guid.NewGuid().ToString("N"))).IsSuccess);
            var formId = await s.CreateFormAsync("C-137 registration", s.Clerk);
            var added = await fixture.Api.PostAsync($"/api/v1/form-definitions/{formId:D}/fields", s.Token(s.Designer), new { fieldKey = "case_reference", fieldDisplayName = "Case reference", fieldDescription = string.Empty, fieldDataType = "String", options = (object?)null, fieldClassification = "Internal", fieldProtectionMode = "Standard" });
            Assert.True(added.Status == HttpStatusCode.Created, added.Body);
            var field = added.Root.GetProperty("fieldDefinitionId").GetGuid();
            await s.SaveDraftAsync(formId, StageBScenario.Layout("C-137", StageBScenario.Section("main", "Main", StageBScenario.Field(field, "text", "Case reference", 4))));
            Assert.True((await s.PublishAsync(formId, "v1")).GetProperty("authorityRegistered").GetBoolean());
            // The IX-4 configuration: an approved searchable field, so each content commit also derives and seals index entries.
            Assert.True((await fixture.Api.PostAsync($"/api/v1/form-definitions/{formId:D}/fields/{field:D}/search-approval", s.Token(s.Administrator), new { operators = new[] { "equals", "startsWith" }, approvalReference = "C-137", useDevelopmentStepUp = true })).IsSuccess);

            var next = 0;
            async Task<Creation> CreateAsync()
            {
                var i = Interlocked.Increment(ref next) - 1;
                var key = $"c137-{s.OrganizationId:N}-{i}";
                var refusals = new List<string>();
                var clock = Stopwatch.StartNew();
                for (var attempt = 1; ; attempt++)
                {
                    var created = await fixture.Api.PostAsync($"/api/v1/form-definitions/{formId:D}/records", s.Token(s.Clerk), new { values = new Dictionary<string, string> { ["case_reference"] = $"RR-{i:D5}" } }, key);
                    if (created.Status is HttpStatusCode.Created or HttpStatusCode.OK)
                    {
                        return new Creation(clock.Elapsed.TotalMilliseconds, attempt, refusals);
                    }

                    refusals.Add(created.ErrorCode ?? ((int)created.Status).ToString(CultureInfo.InvariantCulture));
                    Assert.True(attempt < 300, $"creation {i}: {created.Body}");
                    if (created.ErrorCode == "STALE_AUTHORIZATION_REVISION")
                    {
                        key = $"c137-{s.OrganizationId:N}-{i}-{attempt}"; // the API replays a refusal for the same key
                    }

                    await Task.Delay(100, Ct);
                }
            }

            async Task<object> WindowAsync(int concurrency)
            {
                await Task.Delay(TimeSpan.FromSeconds(2), Ct); // separates this window's log lines and commands from the previous one
                var before = next;
                var started = DateTimeOffset.UtcNow;
                var clock = Stopwatch.StartNew();
                var creations = new List<Creation>();
                var gate = new object();
                var remaining = window;
                await Task.WhenAll(Enumerable.Range(0, concurrency).Select(async _ =>
                {
                    while (Interlocked.Decrement(ref remaining) >= 0)
                    {
                        var c = await CreateAsync();
                        lock (gate)
                        {
                            creations.Add(c);
                        }
                    }
                }));
                var seconds = clock.Elapsed.TotalSeconds;
                var ended = DateTimeOffset.UtcNow;
                await Task.Delay(TimeSpan.FromSeconds(2), Ct);
                var since = started.AddSeconds(-1).ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
                var until = ended.AddSeconds(1).ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
                var apiLog = fixture.Stack.Compose($"logs --no-color --since {since} --until {until} api", 120, throwOnError: false);
                var authorityLog = fixture.Stack.Compose($"logs --no-color --since {since} --until {until} authority", 120, throwOnError: false);

                var operations = OperationLine().Matches(apiLog).Where(m => Guid.Parse(m.Groups["org"].Value) == s.OrganizationId).ToList();
                var phaseNames = operations.SelectMany(m => m.Groups["phases"].Value.Split(',')).Select(p => p.Split(':')[0]).Distinct().ToList();
                var phases = phaseNames.ToDictionary(n => n, n => Stats(operations.SelectMany(m => m.Groups["phases"].Value.Split(',')).Select(p => p.Split(':')).Where(p => p[0] == n).Select(p => D(p[1]))));
                var commands = CommandLine().Matches(authorityLog).Where(m => Guid.Parse(m.Groups["org"].Value) == s.OrganizationId).ToList();
                var content = ContentLine().Matches(authorityLog).Where(m => Guid.Parse(m.Groups["org"].Value) == s.OrganizationId).ToList();
                var rows = AuthorityQuery($"""
                    SELECT round((extract(epoch FROM (j.journal_archived_on - c.command_created_on)) * 1000)::numeric, 1) || '|' ||
                           round((extract(epoch FROM (c.command_effective_on - j.journal_archived_on)) * 1000)::numeric, 1) || '|' ||
                           round((extract(epoch FROM (c.command_effective_on - c.command_created_on)) * 1000)::numeric, 1)
                    FROM security_commands c JOIN security_journal_entries j ON j.security_command_id = c.security_command_id AND j.journal_stream_name = 'authorization'
                    WHERE c.organization_id = '{s.OrganizationId:D}' AND c.command_kind_name = 'RegisterRecord' AND c.command_effective_on IS NOT NULL AND j.journal_archived_on IS NOT NULL
                      AND c.command_created_on >= '{started.AddSeconds(-1):O}' AND c.command_created_on <= '{ended.AddSeconds(1):O}'
                    """).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(l => l.Split('|').Select(D).ToArray()).ToList();
                return new
                {
                    organization_records_before = before,
                    concurrency,
                    creations = creations.Count,
                    seconds = Math.Round(seconds, 1),
                    records_per_second = Math.Round(creations.Count / seconds, 2),
                    client_ms = Stats(creations.Select(c => c.Milliseconds)),
                    attempts = Stats(creations.Select(c => (double)c.Attempts)),
                    refusals = creations.SelectMany(c => c.Refusals).GroupBy(r => r).ToDictionary(g => g.Key, g => g.Count()),
                    business = new
                    {
                        attempts_logged = operations.Count,
                        outcomes = operations.GroupBy(m => m.Groups["outcome"].Value).ToDictionary(g => g.Key, g => g.Count()),
                        status_polls = Stats(operations.Where(m => m.Groups["outcome"].Value == "Created").Select(m => D(m.Groups["polls"].Value))),
                        total_ms = Stats(operations.Where(m => m.Groups["outcome"].Value == "Created").Select(m => D(m.Groups["total"].Value))),
                        phases_ms_successful_and_refused = phases,
                    },
                    register_record_command = new
                    {
                        calls = commands.Count,
                        outcomes = commands.GroupBy(m => m.Groups["outcome"].Value).ToDictionary(g => g.Key, g => g.Count()),
                        lock_wait_ms = Stats(commands.Select(m => D(m.Groups["lock"].Value))),
                        graph_load_ms = Stats(commands.Where(m => m.Groups["outcome"].Value == "Committed").Select(m => D(m.Groups["graph"].Value))),
                        graph_edges = Stats(commands.Where(m => m.Groups["outcome"].Value == "Committed").Select(m => D(m.Groups["edges"].Value))),
                        plan_ms = Stats(commands.Where(m => m.Groups["outcome"].Value == "Committed").Select(m => D(m.Groups["plan"].Value))),
                        commit_ms = Stats(commands.Where(m => m.Groups["outcome"].Value == "Committed").Select(m => D(m.Groups["commit"].Value))),
                        total_ms = Stats(commands.Select(m => D(m.Groups["total"].Value))),
                    },
                    effective_wait_from_authority_timestamps = new
                    {
                        commands = rows.Count,
                        created_to_archived_ms = Stats(rows.Select(r => r[0])),
                        archived_to_projected_effective_ms = Stats(rows.Select(r => r[1])),
                        created_to_effective_ms = Stats(rows.Select(r => r[2])),
                    },
                    content = new
                    {
                        prepare_lock_wait_ms = Stats(content.Where(m => m.Groups["op"].Value == "Prepare").Select(m => D(m.Groups["lock"].Value))),
                        prepare_total_ms = Stats(content.Where(m => m.Groups["op"].Value == "Prepare").Select(m => D(m.Groups["total"].Value))),
                        commit_lock_wait_ms = Stats(content.Where(m => m.Groups["op"].Value == "Commit").Select(m => D(m.Groups["lock"].Value))),
                        commit_total_ms = Stats(content.Where(m => m.Groups["op"].Value == "Commit").Select(m => D(m.Groups["total"].Value))),
                        outcomes = content.GroupBy(m => m.Groups["op"].Value + ":" + m.Groups["outcome"].Value).ToDictionary(g => g.Key, g => g.Count()),
                    },
                };
            }

            var seedSeconds = 0.0;
            foreach (var size in sizes)
            {
                var seedClock = Stopwatch.StartNew();
                while (next < size - window)
                {
                    await CreateAsync();
                }

                seedSeconds += seedClock.Elapsed.TotalSeconds;
                windows.Add(await WindowAsync(1));
                windows.Add(await WindowAsync(4));
            }

            var report = new Dictionary<string, object?>
            {
                ["kind"] = "C-137 R-37 record-creation phase measurement (bounded; local Docker; synthetic; seal sweeps every 10 s in the security-test stack)",
                ["measured_on"] = DateTimeOffset.UtcNow, ["git_commit"] = ComposeStack.GitCommit(), ["sizes"] = sizes, ["window"] = window,
                ["records_created"] = next, ["seeding_seconds"] = Math.Round(seedSeconds), ["total_seconds"] = Math.Round(total.Elapsed.TotalSeconds),
                ["windows"] = windows,
            };
            Directory.CreateDirectory(Path.Combine(ComposeStack.RepositoryRoot, "artifacts", "r-37"));
            await File.WriteAllTextAsync(Path.Combine(ComposeStack.RepositoryRoot, "artifacts", "r-37", "record-registration.json"), JsonSerializer.Serialize(report, Indented), Ct);
        }
        finally
        {
            Environment.SetEnvironmentVariable("R37_OPERATION_TIMING", null);
            fixture.Stack.Compose("up -d --no-deps --force-recreate --wait authority api", 300);
        }
    }
}
