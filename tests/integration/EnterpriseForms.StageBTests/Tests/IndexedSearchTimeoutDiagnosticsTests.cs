using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using EnterpriseForms.SecurityTests.Harness;
using EnterpriseForms.StageBTests.Harness;
using Xunit;

namespace EnterpriseForms.StageBTests.Tests;

/// <summary>
/// C-135 R-33 and ingestion diagnostics (bounded, synthetic; a Benchmark — never part of focused functional runs). One small
/// organization: 20 records created one at a time and 150 at concurrency 4 (outcome counts, latency, refusal and retry counts per
/// creation), then 40 sequential indexed searches immediately afterwards (end-to-end latency, status, correlation id) and 40 more
/// after a 60-second quiet period, while AuthorityDb's <c>pg_stat_activity</c> is sampled every 250 ms (state, wait event, duration
/// and the first 60 characters of each statement — parameterized SQL only, never values). Report:
/// artifacts/ix-4/r33-diagnostics.json. No assertion on latency; it fails only if the harness itself cannot run.
/// </summary>
[Collection(StageBStackCollectionDefinition.Name)]
public sealed class IndexedSearchTimeoutDiagnosticsTests(StageBFixture fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    private sealed record Creation(int Index, int Concurrency, double Milliseconds, int Attempts, Dictionary<string, int> Refusals);

    private sealed record Search(string Phase, int Number, double Milliseconds, int Status, string? Code, string? Reason, string Correlation);

    [Fact]
    [Trait("Category", "Benchmark")]
    public async Task DiagnoseSearchTimeoutsAndIngestionConflictsOnASmallOrganization()
    {
        var s = new StageBScenario(fixture, "ix4-r33");
        await s.BootstrapAsync();
        var current = await fixture.Api.GetAsync("/api/v1/organization/security-policy", s.Token(s.Designer));
        Assert.True((await fixture.Api.SendAsync(HttpMethod.Put, "/api/v1/organization/security-policy", s.Token(s.Designer), new { minimumFieldClassification = "Internal", plaintextInternalFieldsPermitted = true, plaintextDowngradePermitted = false, plaintextSearchPermitted = true, policyStatementText = "R-33 diagnostics (synthetic).", expectedPolicyRevision = current.Root.GetProperty("securityPolicyRevision").GetInt64() }, Guid.NewGuid().ToString("N"))).IsSuccess);
        var formId = await s.CreateFormAsync("R-33 diagnostics", s.Clerk);
        var added = await fixture.Api.PostAsync($"/api/v1/form-definitions/{formId:D}/fields", s.Token(s.Designer), new { fieldKey = "case_reference", fieldDisplayName = "Case reference", fieldDescription = string.Empty, fieldDataType = "String", options = (object?)null, fieldClassification = "Internal", fieldProtectionMode = "Standard" });
        Assert.True(added.Status == HttpStatusCode.Created, added.Body);
        var field = added.Root.GetProperty("fieldDefinitionId").GetGuid();
        await s.SaveDraftAsync(formId, StageBScenario.Layout("R-33", StageBScenario.Section("main", "Main", StageBScenario.Field(field, "text", "Case reference", 4))));
        Assert.True((await s.PublishAsync(formId, "v1")).GetProperty("authorityRegistered").GetBoolean());
        Assert.True((await fixture.Api.PostAsync($"/api/v1/form-definitions/{formId:D}/fields/{field:D}/search-approval", s.Token(s.Administrator), new { operators = new[] { "equals", "startsWith" }, approvalReference = "R-33", useDevelopmentStepUp = true })).IsSuccess);
        Assert.Equal(HttpStatusCode.NoContent, (await fixture.Api.SendAsync(HttpMethod.Put, $"/api/v1/form-definitions/{formId:D}/field-searchers", s.Token(s.Administrator), new { grantUserIds = new[] { s.Clerk.AuthorityUserId }, grantTeamIds = Array.Empty<Guid>(), revokeUserIds = Array.Empty<Guid>(), revokeTeamIds = Array.Empty<Guid>() }, Guid.NewGuid().ToString("N"))).Status);

        // pg_stat_activity sampler (AuthorityDb), running for the whole diagnostic.
        var samples = new ConcurrentBag<string>();
        using var stopSampler = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var sampler = Task.Run(async () =>
        {
            while (!stopSampler.IsCancellationRequested)
            {
                var at = DateTimeOffset.UtcNow.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture);
                var rows = fixture.Stack.AuthorityObserverSql("""
                    SELECT usename || '|' || state || '|' || coalesce(wait_event_type, '-') || ':' || coalesce(wait_event, '-') || '|' ||
                           round(extract(epoch FROM (clock_timestamp() - query_start)) * 1000) || '|' || regexp_replace(left(query, 60), '\s+', ' ', 'g')
                    FROM pg_stat_activity WHERE datname = current_database() AND pid <> pg_backend_pid() AND state <> 'idle';
                    """);
                foreach (var row in rows.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    samples.Add(at + "|" + row);
                }

                try
                {
                    await Task.Delay(250, stopSampler.Token);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }, Ct);

        // Ingestion: one at a time, then concurrency 4.
        var creations = new ConcurrentBag<Creation>();
        async Task CreateAsync(int i, int concurrency)
        {
            var refusals = new Dictionary<string, int>(StringComparer.Ordinal);
            var key = $"r33-{s.OrganizationId:N}-{i}";
            var clock = Stopwatch.StartNew();
            for (var attempt = 1; ; attempt++)
            {
                var created = await fixture.Api.PostAsync($"/api/v1/form-definitions/{formId:D}/records", s.Token(s.Clerk), new { values = new Dictionary<string, string> { ["case_reference"] = $"R33-{i:D4}" } }, key);
                if (created.Status is HttpStatusCode.Created or HttpStatusCode.OK)
                {
                    creations.Add(new Creation(i, concurrency, clock.Elapsed.TotalMilliseconds, attempt, refusals));
                    return;
                }

                var code = created.ErrorCode ?? ((int)created.Status).ToString(CultureInfo.InvariantCulture);
                refusals[code] = refusals.GetValueOrDefault(code) + 1;
                Assert.True(attempt < 300, $"create {i}: {created.Body}");
                if (code == "STALE_AUTHORIZATION_REVISION")
                {
                    key = $"r33-{s.OrganizationId:N}-{i}-{attempt}";
                }

                await Task.Delay(100, Ct);
            }
        }

        var serial = Stopwatch.StartNew();
        for (var i = 0; i < 20; i++)
        {
            await CreateAsync(i, 1);
        }

        var serialSeconds = serial.Elapsed.TotalSeconds;
        var concurrent = Stopwatch.StartNew();
        await Parallel.ForEachAsync(Enumerable.Range(20, 150), new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = Ct }, async (i, _) => await CreateAsync(i, 4));
        var concurrentSeconds = concurrent.Elapsed.TotalSeconds;

        // Searches through the business API with an Authority session.
        var digest = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        Assert.True((await fixture.Authority.SendAsync(HttpMethod.Post, "/internal/v1/sessions", TestIdentity.WorkloadToken(), new { organization_id = s.OrganizationId, session_digest_base64 = digest, absolute_expires_on = DateTimeOffset.UtcNow.AddHours(1) }, s.Token(s.Clerk))).IsSuccess);
        using var http = new HttpClient { BaseAddress = new Uri(StageBFixture.ApiBaseAddress), Timeout = TimeSpan.FromMinutes(2) };
        var searches = new List<Search>();
        async Task SearchAsync(string phase, int n)
        {
            var correlation = Guid.CreateVersion7().ToString("D");
            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/form-records/field-search")
            {
                Content = JsonContent.Create(new { formDefinitionId = formId, conditions = new[] { new { fieldDefinitionId = field, @operator = "startsWith", valueType = "String", value = "R33-00" } }, pageSize = 25 }),
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", s.Token(s.Clerk));
            request.Headers.Add("X-Testing-Session-Digest", digest);
            request.Headers.Add("X-Correlation-Id", correlation);
            var clock = Stopwatch.StartNew();
            using var response = await http.SendAsync(request, Ct);
            var body = await response.Content.ReadAsStringAsync(Ct);
            var root = JsonDocument.Parse(body).RootElement;
            string? Str(string name) => root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            searches.Add(new Search(phase, n, clock.Elapsed.TotalMilliseconds, (int)response.StatusCode, Str("title"), Str("unavailable_reason"), correlation));
        }

        for (var n = 0; n < 40; n++)
        {
            await SearchAsync("after_ingestion", n);
        }

        await Task.Delay(TimeSpan.FromSeconds(60), Ct);
        for (var n = 0; n < 40; n++)
        {
            await SearchAsync("after_quiet_60s", n);
        }

        await stopSampler.CancelAsync();
        await sampler;

        var authorityLog = fixture.Stack.Logs("authority", 20000);
        var failed = searches.Where(x => x.Status != 200).ToList();
        var failureLog = failed.ToDictionary(x => x.Correlation, x => authorityLog.Split('\n').Where(l => l.Contains(x.Correlation, StringComparison.OrdinalIgnoreCase)).Select(l => l.Length > 300 ? l[..300] : l).Take(6).ToList());
        static object Stats(IEnumerable<double> values)
        {
            var v = values.Order().ToList();
            double P(double p) => v.Count == 0 ? 0 : v[Math.Clamp((int)Math.Ceiling(p * v.Count) - 1, 0, v.Count - 1)];
            return new { count = v.Count, p50 = Math.Round(P(0.5), 1), p95 = Math.Round(P(0.95), 1), max = Math.Round(v.Count == 0 ? 0 : v[^1], 1) };
        }

        var waits = samples.Select(x => x.Split('|')).Where(p => p.Length >= 6).GroupBy(p => p[3]).ToDictionary(g => g.Key, g => g.Count());
        var longRunning = samples.Select(x => x.Split('|')).Where(p => p.Length >= 6 && long.TryParse(p[4], out var ms) && ms >= 1000).Select(p => string.Join('|', p[0], p[1], p[2], p[3], p[4], p[5]))
            .GroupBy(x => string.Join('|', x.Split('|').Skip(1).Where((_, i) => i != 3))).Select(g => new { sample = g.Key, occurrences = g.Count() }).OrderByDescending(g => g.occurrences).Take(25).ToList();
        var report = new Dictionary<string, object?>
        {
            ["kind"] = "R-33 and ingestion diagnostics (C-135; bounded synthetic workload; local Docker)",
            ["measured_on"] = DateTimeOffset.UtcNow, ["git_commit"] = ComposeStack.GitCommit(),
            ["ingestion"] = new
            {
                serial = new { records = 20, seconds = Math.Round(serialSeconds, 1), records_per_second = Math.Round(20 / serialSeconds, 2), latency_ms = Stats(creations.Where(c => c.Concurrency == 1).Select(c => c.Milliseconds)) },
                concurrency_4 = new { records = 150, seconds = Math.Round(concurrentSeconds, 1), records_per_second = Math.Round(150 / concurrentSeconds, 2), latency_ms = Stats(creations.Where(c => c.Concurrency == 4).Select(c => c.Milliseconds)), attempts = Stats(creations.Where(c => c.Concurrency == 4).Select(c => (double)c.Attempts)) },
                refusals = creations.SelectMany(c => c.Refusals).GroupBy(r => r.Key).ToDictionary(g => g.Key, g => g.Sum(r => r.Value)),
            },
            ["searches"] = searches.GroupBy(x => x.Phase).ToDictionary(g => g.Key, g => (object)new
            {
                outcomes = g.GroupBy(x => x.Status == 200 ? "200" : $"{x.Status} {x.Code} {x.Reason}").ToDictionary(o => o.Key, o => o.Count()),
                answered_ms = Stats(g.Where(x => x.Status == 200).Select(x => x.Milliseconds)),
                all_ms = Stats(g.Select(x => x.Milliseconds)),
            }),
            ["failed_searches"] = failed, ["failed_search_authority_log"] = failureLog,
            ["authority_db_samples"] = samples.Count, ["authority_db_wait_events"] = waits, ["authority_db_statements_running_1s_or_more"] = longRunning,
        };
        Directory.CreateDirectory(Path.Combine(ComposeStack.RepositoryRoot, "artifacts", "ix-4"));
        await File.WriteAllTextAsync(Path.Combine(ComposeStack.RepositoryRoot, "artifacts", "ix-4", "r33-diagnostics.json"), JsonSerializer.Serialize(report, Indented), Ct);
        Assert.Equal(170, creations.Count);
    }

    /// <summary>
    /// C-135 R-33, second bounded probe: 40 sequential indexed searches (about 100 matches each) on an <b>existing</b> large synthetic
    /// organization (the 1,000-record IX-4 benchmark organization), with no seeding. <c>IX4_R33_PROBE</c> =
    /// <c>organization|form|stringField|authorityUserId|subject</c>; skipped without it. Report: artifacts/ix-4/r33-probe-existing.json.
    /// </summary>
    [Fact]
    [Trait("Category", "Benchmark")]
    public async Task ProbeIndexedSearchesOnAnExistingLargeOrganization()
    {
        var spec = Environment.GetEnvironmentVariable("IX4_R33_PROBE");
        Assert.SkipWhen(string.IsNullOrEmpty(spec), "IX4_R33_PROBE not set");
        var parts = spec!.Split('|');
        var organization = Guid.Parse(parts[0]);
        var form = Guid.Parse(parts[1]);
        var field = Guid.Parse(parts[2]);
        var user = new TestUser(Guid.Parse(parts[3]), parts[4], "Clerk");
        var token = TestIdentity.UserToken(user, organization);
        var digest = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var session = await fixture.Authority.SendAsync(HttpMethod.Post, "/internal/v1/sessions", TestIdentity.WorkloadToken(), new { organization_id = organization, session_digest_base64 = digest, absolute_expires_on = DateTimeOffset.UtcNow.AddHours(1) }, token);
        Assert.True(session.IsSuccess, session.Body);

        var samples = new ConcurrentBag<string>();
        using var stopSampler = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var sampler = Task.Run(async () =>
        {
            while (!stopSampler.IsCancellationRequested)
            {
                var at = DateTimeOffset.UtcNow.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture);
                foreach (var row in fixture.Stack.AuthorityObserverSql("SELECT usename || '|' || state || '|' || coalesce(wait_event_type, '-') || ':' || coalesce(wait_event, '-') || '|' || round(extract(epoch FROM (clock_timestamp() - query_start)) * 1000) || '|' || regexp_replace(left(query, 60), '\\s+', ' ', 'g') FROM pg_stat_activity WHERE datname = current_database() AND pid <> pg_backend_pid() AND state <> 'idle';").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    samples.Add(at + "|" + row);
                }

                try
                {
                    await Task.Delay(250, stopSampler.Token);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }, Ct);

        using var http = new HttpClient { BaseAddress = new Uri(StageBFixture.ApiBaseAddress), Timeout = TimeSpan.FromMinutes(2) };
        var searches = new List<Search>();
        for (var n = 0; n < 40; n++)
        {
            var correlation = Guid.CreateVersion7().ToString("D");
            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/form-records/field-search")
            {
                Content = JsonContent.Create(new { formDefinitionId = form, conditions = new[] { new { fieldDefinitionId = field, @operator = "startsWith", valueType = "String", value = "BM-0001" } }, pageSize = 25 }),
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            request.Headers.Add("X-Testing-Session-Digest", digest);
            request.Headers.Add("X-Correlation-Id", correlation);
            var clock = Stopwatch.StartNew();
            using var response = await http.SendAsync(request, Ct);
            var body = await response.Content.ReadAsStringAsync(Ct);
            var root = JsonDocument.Parse(body).RootElement;
            string? Str(string name) => root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            searches.Add(new Search("existing_1000", n, clock.Elapsed.TotalMilliseconds, (int)response.StatusCode, Str("title"), Str("unavailable_reason"), correlation));
        }

        await stopSampler.CancelAsync();
        await sampler;
        var authorityLog = fixture.Stack.Logs("authority", 20000).Split('\n');
        var failed = searches.Where(x => x.Status != 200).ToList();
        var answered = searches.Where(x => x.Status == 200).Select(x => x.Milliseconds).Order().ToList();
        var all = searches.Select(x => x.Milliseconds).Order().ToList();
        double P(List<double> v, double p) => v.Count == 0 ? 0 : Math.Round(v[Math.Clamp((int)Math.Ceiling(p * v.Count) - 1, 0, v.Count - 1)], 1);
        var report = new Dictionary<string, object?>
        {
            ["kind"] = "R-33 probe on an existing 1,000-record synthetic organization (C-135; 40 sequential searches; no seeding)",
            ["measured_on"] = DateTimeOffset.UtcNow, ["git_commit"] = ComposeStack.GitCommit(),
            ["outcomes"] = searches.GroupBy(x => x.Status == 200 ? "200" : $"{x.Status} {x.Code} {x.Reason}").ToDictionary(g => g.Key, g => g.Count()),
            ["answered_ms"] = new { count = answered.Count, p50 = P(answered, 0.5), p95 = P(answered, 0.95), max = answered.Count == 0 ? 0 : Math.Round(answered[^1], 1) },
            ["all_ms"] = new { p50 = P(all, 0.5), p95 = P(all, 0.95), max = Math.Round(all[^1], 1) },
            ["per_search_ms"] = searches.Select(x => new { x.Number, ms = Math.Round(x.Milliseconds), x.Status, x.Code, x.Reason }).ToList(),
            ["failed_search_authority_log"] = failed.ToDictionary(x => x.Correlation, x => authorityLog.Where(l => l.Contains(x.Correlation, StringComparison.OrdinalIgnoreCase)).Select(l => l.Length > 300 ? l[..300] : l).Take(8).ToList()),
            ["authority_db_wait_events"] = samples.Select(x => x.Split('|')).Where(p => p.Length >= 6).GroupBy(p => p[3]).ToDictionary(g => g.Key, g => g.Count()),
            ["authority_db_statements_running_1s_or_more"] = samples.Select(x => x.Split('|')).Where(p => p.Length >= 6 && long.TryParse(p[4], out var ms) && ms >= 1000).Select(p => $"{p[0]}|{p[1]}|{p[3]}|{p[4]}ms|{p[5]}").Take(40).ToList(),
        };
        Directory.CreateDirectory(Path.Combine(ComposeStack.RepositoryRoot, "artifacts", "ix-4"));
        await File.WriteAllTextAsync(Path.Combine(ComposeStack.RepositoryRoot, "artifacts", "ix-4", "r33-probe-existing.json"), JsonSerializer.Serialize(report, Indented), Ct);
    }
}
