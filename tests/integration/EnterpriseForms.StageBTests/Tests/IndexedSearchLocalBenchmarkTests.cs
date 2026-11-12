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
/// IX-4 local benchmark (design §16 IX-4, R1 §F.2) of the Indexed engine, on the local security-test stack (relative numbers only;
/// Azure staging PERF-01 decides, IX-7). C-134 §6: three phases are measured separately, all through the real paths — nothing is
/// written to AuthorityDb directly, no index entry is fabricated, nothing bypasses authorization or signing:
/// <list type="number">
/// <item><b>Ingestion:</b> synthetic records created through the public API (Authority commit, inline derivation and entry seal per
/// record); records per second;</item>
/// <item><b>Query execution:</b> end-to-end p50/p95 through the business API for a selective predicate, 1 % and 10 % selectivity, the
/// full scope (refused above the 10,000 result cap), an AND, page 2, and a 50-record share-only partial reader (IX-09/IX-29);</item>
/// <item><b>Index building:</b> after the last step, one approval change returns the form's coverages to Building and the builder
/// rebuilds the index from verified business bytes (Authority verification, seal signing, attestation); time to Complete.</item>
/// </list>
/// The dataset is reused by growing one organization step by step (<c>IX4_BENCHMARK_SIZES</c>, default <c>1000</c>). A step whose
/// projected ingestion time exceeds <c>IX4_BENCHMARK_BUDGET_MINUTES</c> (default 30) is not started and is reported as skipped. The
/// 50,000-record ceiling step (one record beyond the discover ceiling) runs only when 50,000 is reached, which needs separate owner
/// authorization (C-134). Report: artifacts/ix-4/benchmark.json. Report-only; a Benchmark (-ExcludeBenchmarks skips it).
/// </summary>
[Collection(StageBStackCollectionDefinition.Name)]
public sealed class IndexedSearchLocalBenchmarkTests(StageBFixture fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    private const int Shared = 50;
    private const int Repetitions = 20;

    private sealed record Ws(StageBScenario S, Guid FormId, Guid Reference, Guid Count, Guid Bucket);

    private string Scalar(string sql) => fixture.Stack.AuthorityObserverSql(sql).Trim().Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim() ?? string.Empty;

    private async Task<Ws> WorkspaceAsync()
    {
        var s = new StageBScenario(fixture, "ix4-benchmark");
        await s.BootstrapAsync();
        var current = await fixture.Api.GetAsync("/api/v1/organization/security-policy", s.Token(s.Designer));
        var policy = await fixture.Api.SendAsync(HttpMethod.Put, "/api/v1/organization/security-policy", s.Token(s.Designer), new { minimumFieldClassification = "Internal", plaintextInternalFieldsPermitted = true, plaintextDowngradePermitted = false, plaintextSearchPermitted = true, policyStatementText = "IX-4 benchmark organization (synthetic).", expectedPolicyRevision = current.Root.GetProperty("securityPolicyRevision").GetInt64() }, Guid.NewGuid().ToString("N"));
        Assert.True(policy.IsSuccess, policy.Body);
        var formId = await s.CreateFormAsync("IX-4 benchmark", s.Clerk);
        async Task<Guid> FieldAsync(string key, string label, string type)
        {
            var added = await fixture.Api.PostAsync($"/api/v1/form-definitions/{formId:D}/fields", s.Token(s.Designer), new { fieldKey = key, fieldDisplayName = label, fieldDescription = string.Empty, fieldDataType = type, options = (object?)null, fieldClassification = "Internal", fieldProtectionMode = "Standard" });
            Assert.True(added.Status == HttpStatusCode.Created, $"add {key}: " + added.Body);
            return added.Root.GetProperty("fieldDefinitionId").GetGuid();
        }

        var reference = await FieldAsync("case_reference", "Case reference", "String");
        var count = await FieldAsync("exhibit_count", "Exhibit count", "Integer");
        var bucket = await FieldAsync("region_bucket", "Region bucket", "Integer");
        await s.SaveDraftAsync(formId, StageBScenario.Layout("IX-4 benchmark", StageBScenario.Section("main", "Main",
            StageBScenario.Field(reference, "text", "Case reference", 2), StageBScenario.Field(count, "number", "Exhibit count", 2), StageBScenario.Field(bucket, "number", "Region bucket", 2))));
        Assert.True((await s.PublishAsync(formId, "v1")).GetProperty("authorityRegistered").GetBoolean());
        foreach (var (field, ops) in new[] { (reference, new[] { "equals", "startsWith" }), (count, new[] { "equals", "greaterThanOrEqual" }), (bucket, new[] { "equals" }) })
        {
            var approved = await fixture.Api.PostAsync($"/api/v1/form-definitions/{formId:D}/fields/{field:D}/search-approval", s.Token(s.Administrator), new { operators = ops, approvalReference = "IX-4 benchmark", useDevelopmentStepUp = true });
            Assert.True(approved.IsSuccess, approved.Body);
        }

        var granted = await fixture.Api.SendAsync(HttpMethod.Put, $"/api/v1/form-definitions/{formId:D}/field-searchers", s.Token(s.Administrator), new { grantUserIds = new[] { s.Clerk.AuthorityUserId, s.Reader.AuthorityUserId }, grantTeamIds = Array.Empty<Guid>(), revokeUserIds = Array.Empty<Guid>(), revokeTeamIds = Array.Empty<Guid>() }, Guid.NewGuid().ToString("N"));
        Assert.True(granted.Status == HttpStatusCode.NoContent, granted.Body);
        return new Ws(s, formId, reference, count, bucket);
    }

    /// <summary>Record i: unique reference, exhibit_count = i % 100 (1 % per value), region_bucket = i % 10 (10 % per value).</summary>
    private async Task<Guid> CreateAsync(Ws w, int i)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["case_reference"] = $"BM-{i:D6}", ["exhibit_count"] = (i % 100).ToString(CultureInfo.InvariantCulture), ["region_bucket"] = (i % 10).ToString(CultureInfo.InvariantCulture),
        };
        var key = $"ix4-bm-{w.S.OrganizationId:N}-{i}";
        for (var attempt = 0; ; attempt++)
        {
            var created = await fixture.Api.PostAsync($"/api/v1/form-definitions/{w.FormId:D}/records", w.S.Token(w.S.Clerk), new { values }, key);
            if (created.Status is HttpStatusCode.Created or HttpStatusCode.OK)
            {
                return created.Root.GetProperty("formRecordId").GetGuid();
            }

            if (created.ErrorCode == "STALE_AUTHORIZATION_REVISION")
            {
                // A concurrent creation advanced the organization's authorization revision; the refused command created nothing, and the
                // API replays a refusal for the same idempotency key, so the retry is a new command.
                key = $"ix4-bm-{w.S.OrganizationId:N}-{i}-{attempt + 1}";
            }

            Assert.True(attempt < 200 && (created.ErrorCode is "AUTHORITY_NOT_READY" or "STALE_AUTHORIZATION_REVISION" or "COMMAND_PENDING" || (int)created.Status is >= 500 or 429), $"create {i} failed: " + created.Body);
            await Task.Delay(250 + Random.Shared.Next(250), Ct);
        }
    }

    private async Task<string> SessionAsync(Ws w, TestUser user)
    {
        var digest = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var created = await fixture.Authority.SendAsync(HttpMethod.Post, "/internal/v1/sessions", TestIdentity.WorkloadToken(), new { organization_id = w.S.OrganizationId, session_digest_base64 = digest, absolute_expires_on = DateTimeOffset.UtcNow.AddHours(4) }, w.S.Token(user));
        Assert.True(created.IsSuccess, created.Body);
        return digest;
    }

    private static object Cond(Guid field, string op, string valueType, string value) => new { fieldDefinitionId = field, @operator = op, valueType, value };

    private sealed record Outcome(HttpStatusCode Status, string? Code, string? Kind, string? Basis, int? Total, string? Cursor, double Milliseconds);

    private static async Task<Outcome> PostAsync(HttpClient http, Ws w, TestUser user, string session, string path, object body)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body) };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", w.S.Token(user));
        request.Headers.Add("X-Testing-Session-Digest", session);
        var clock = Stopwatch.StartNew();
        using var response = await http.SendAsync(request, Ct);
        var text = await response.Content.ReadAsStringAsync(Ct);
        var elapsed = clock.Elapsed.TotalMilliseconds;
        var root = JsonDocument.Parse(text).RootElement;
        string? Str(string name) => root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        return new Outcome(response.StatusCode, Str("title"), Str("limit_kind") ?? Str("unavailable_reason"), Str("resultBasis"),
            root.TryGetProperty("totalCount", out var t) ? t.GetInt32() : null, Str("continuationToken"), elapsed);
    }

    /// <summary>
    /// Every run counts (C-134 §6: actual measured durations): outcomes by status and code, and percentiles over the answered runs
    /// only, so a failed run (for example a 503 after a timeout) is reported as a failure, never hidden in a latency figure.
    /// </summary>
    private static Dictionary<string, object?> Summarize(string name, List<Outcome> runs)
    {
        static double Percentile(List<double> sorted, double p) => sorted.Count == 0 ? double.NaN : sorted[Math.Clamp((int)Math.Ceiling(p * sorted.Count) - 1, 0, sorted.Count - 1)];
        var answered = runs.Where(r => r.Status == HttpStatusCode.OK).Select(r => r.Milliseconds).Order().ToList();
        var all = runs.Select(r => r.Milliseconds).Order().ToList();
        var outcomes = runs.GroupBy(r => r.Status == HttpStatusCode.OK ? "200 " + r.Basis : $"{(int)r.Status} {r.Code} {r.Kind}").ToDictionary(g => g.Key, g => g.Count());
        var answeredRun = runs.FirstOrDefault(r => r.Status == HttpStatusCode.OK);
        return new Dictionary<string, object?>
        {
            ["case"] = name, ["runs"] = runs.Count, ["outcomes"] = outcomes, ["answered"] = answered.Count,
            ["answered_p50_ms"] = Math.Round(Percentile(answered, 0.50), 1), ["answered_p95_ms"] = Math.Round(Percentile(answered, 0.95), 1), ["answered_max_ms"] = answered.Count == 0 ? null : Math.Round(answered[^1], 1),
            ["all_runs_p95_ms"] = Math.Round(Percentile(all, 0.95), 1), ["total_count"] = answeredRun?.Total,
            ["unexpected_failures"] = runs.Count(r => r.Status != HttpStatusCode.OK && r.Code != "SEARCH_VERIFICATION_LIMIT"),
        };
    }

    [Fact]
    [Trait("Category", "Benchmark")]
    public async Task BenchmarkTheIndexedEngineAsTheOrganizationGrowsTo50000Records()
    {
        var sizes = (Environment.GetEnvironmentVariable("IX4_BENCHMARK_SIZES") ?? "1000").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(int.Parse).ToList();
        var concurrency = int.Parse(Environment.GetEnvironmentVariable("IX4_BENCHMARK_CREATE_CONCURRENCY") ?? "4", CultureInfo.InvariantCulture);
        var budget = TimeSpan.FromMinutes(double.Parse(Environment.GetEnvironmentVariable("IX4_BENCHMARK_BUDGET_MINUTES") ?? "30", CultureInfo.InvariantCulture));
        var skipped = new List<string>();
        double? recordsPerSecond = null;
        var w = await WorkspaceAsync();
        var created = new ConcurrentDictionary<int, Guid>();
        var report = new List<Dictionary<string, object?>>();
        var steps = new List<Dictionary<string, object?>>();
        using var http = new HttpClient { BaseAddress = new Uri(StageBFixture.ApiBaseAddress), Timeout = TimeSpan.FromMinutes(5) };
        string? readerSession = null;
        var have = 0;
        Dictionary<string, object?>? rebuild = null;
        foreach (var size in sizes)
        {
            if (recordsPerSecond is { } rate && TimeSpan.FromSeconds((size - have) / rate) > budget)
            {
                skipped.Add($"{size} records: projected ingestion {(size - have) / rate / 60:F0} min at {rate:F1} records/s exceeds the {budget.TotalMinutes:F0} min budget; not started");
                break;
            }

            var seeding = Stopwatch.StartNew();
            await Parallel.ForEachAsync(Enumerable.Range(have, size - have), new ParallelOptions { MaxDegreeOfParallelism = concurrency, CancellationToken = Ct }, async (i, _) => created[i] = await CreateAsync(w, i));
            var seeded = seeding.Elapsed;
            recordsPerSecond = (size - have) / Math.Max(1, seeded.TotalSeconds);
            have = size;
            if (readerSession is null)
            {
                // A share-only partial reader: 50 records, readable on case_reference only (its scope never grows).
                foreach (var i in Enumerable.Range(0, Shared))
                {
                    for (var attempt = 0; ; attempt++)
                    {
                        var share = await fixture.Api.PostAsync($"/api/v1/form-records/{created[i * 7]:D}/shares", w.S.Token(w.S.Clerk), new { recipientUserId = w.S.Reader.AuthorityUserId, recipientTeamId = (Guid?)null, recipientRole = "Reader", fieldKeys = new[] { "case_reference" } });
                        if (share.IsSuccess || attempt >= 60)
                        {
                            Assert.True(share.IsSuccess, share.Body);
                            break;
                        }

                        await Task.Delay(300, Ct);
                    }
                }

                readerSession = await SessionAsync(w, w.S.Reader);
            }

            // Every record committed and projected (the organization gate open) before measuring.
            var owner = await SessionAsync(w, w.S.Clerk);
            var probe = new { formDefinitionId = w.FormId, conditions = new[] { Cond(w.Reference, "equals", "String", "BM-000000") }, pageSize = 25 };
            for (var attempt = 0; ; attempt++)
            {
                var ready = await PostAsync(http, w, w.S.Clerk, owner, "/api/v1/form-records/field-search", probe);
                if (ready.Status == HttpStatusCode.OK && ready.Basis == "AuthorityAcceptedContent")
                {
                    break;
                }

                Assert.True(attempt < 240, $"not ready at {size}: {ready.Status} {ready.Code} {ready.Kind} {ready.Basis}");
                await Task.Delay(1000, Ct);
            }

            var contentful = Scalar($"SELECT count(*) FROM record_content_heads WHERE organization_id = '{w.S.OrganizationId:D}';");
            steps.Add(new Dictionary<string, object?> { ["organization_records"] = size, ["seeding_seconds"] = Math.Round(seeded.TotalSeconds), ["records_per_second"] = Math.Round((size - (steps.Count == 0 ? 0 : (int)steps[^1]["organization_records"]!)) / Math.Max(1, seeded.TotalSeconds), 1), ["authority_heads"] = contentful });

            async Task Measure(string name, TestUser user, string session, object[] conditions, bool page2 = false)
            {
                var runs = new List<Outcome>();
                var pages = new List<Outcome>();
                for (var r = 0; r < Repetitions; r++)
                {
                    var outcome = await PostAsync(http, w, user, session, "/api/v1/form-records/field-search", new { formDefinitionId = w.FormId, conditions, pageSize = 25, includeArchived = true });
                    runs.Add(outcome);
                    if (page2 && outcome.Cursor is { } cursor)
                    {
                        pages.Add(await PostAsync(http, w, user, session, "/api/v1/form-records/field-search/page", new { cursor }));
                    }
                }

                var summary = Summarize(name, runs);
                summary["organization_records"] = size;
                report.Add(summary);
                if (pages.Count > 0)
                {
                    var p = Summarize(name + " (page 2)", pages);
                    p["organization_records"] = size;
                    report.Add(p);
                }
            }

            await Measure("owner selective (1 match)", w.S.Clerk, owner, [Cond(w.Reference, "equals", "String", $"BM-{size / 2:D6}")]);
            await Measure("owner 1% (exhibit_count = 7)", w.S.Clerk, owner, [Cond(w.Count, "equals", "Integer", "7")], page2: true);
            await Measure("owner 10% (region_bucket = 3)", w.S.Clerk, owner, [Cond(w.Bucket, "equals", "Integer", "3")], page2: true);
            await Measure("owner 100% (exhibit_count >= 0)", w.S.Clerk, owner, [Cond(w.Count, "greaterThanOrEqual", "Integer", "0")]);
            await Measure("owner AND (prefix + 10%)", w.S.Clerk, owner, [Cond(w.Reference, "startsWith", "String", "BM-00"), Cond(w.Bucket, "equals", "Integer", "3")]);
            await Measure("share-only partial reader (50 records, prefix)", w.S.Reader, readerSession, [Cond(w.Reference, "startsWith", "String", "BM-")]);

            // Phase 3, index building (measured once, at the first step, so the 30-minute budget holds; C-134): an approval change returns every coverage of the form to Building; the builder re-derives and
            // re-seals every record from verified business bytes and the Authority attests each coverage (IX-2 path).
            if (rebuild is null)
            {
                var withdrawn = await fixture.Api.PostAsync($"/api/v1/form-definitions/{w.FormId:D}/fields/{w.Bucket:D}/search-approval/withdraw", w.S.Token(w.S.Administrator), new { reason = "IX-4 benchmark index rebuild" });
                Assert.True(withdrawn.IsSuccess, withdrawn.Body);
                var started = Stopwatch.StartNew();
                var approved = await fixture.Api.PostAsync($"/api/v1/form-definitions/{w.FormId:D}/fields/{w.Bucket:D}/search-approval", w.S.Token(w.S.Administrator), new { operators = new[] { "equals" }, approvalReference = "IX-4 benchmark", useDevelopmentStepUp = true });
                Assert.True(approved.IsSuccess, approved.Body);
                string Coverage() => Scalar($"SELECT count(*) FILTER (WHERE coverage_state_name = 'Complete') || '/' || count(*) FROM authority_search.search_field_coverages WHERE organization_id = '{w.S.OrganizationId:D}' AND coverage_state_name <> 'Withdrawn';");
                var limit = TimeSpan.FromSeconds(Math.Max(600, have / 2.0));
                while (Coverage() != "3/3" && started.Elapsed < limit)
                {
                    await Task.Delay(1000, Ct);
                }

                rebuild = new Dictionary<string, object?> { ["organization_records"] = have, ["coverages"] = Coverage(), ["seconds_to_complete"] = Math.Round(started.Elapsed.TotalSeconds, 1), ["record_coverages_per_second"] = Math.Round(have * 3 / Math.Max(1, started.Elapsed.TotalSeconds), 1), ["note"] = "3 coverages rebuilt; the builder backfills and the Authority verifies, seals and attests (local Vault provider)" };
                Assert.Equal("3/3", (string)rebuild["coverages"]!);
            }
        }

        // IX-29 / IX-09 at the ceiling: one record beyond the owner's 50,000-record discover ceiling refuses the owner's search with the
        // caller-owned "discoverable" limit (never an index state, never a fallback), while the reader's 50-record scope still answers.
        Dictionary<string, object?>? ceiling = null;
        if (have == 50_000)
        {
            created[have] = await CreateAsync(w, have);
            var owner = await SessionAsync(w, w.S.Clerk);
            Outcome refused;
            var deadline = DateTime.UtcNow.AddMinutes(2);
            do
            {
                refused = await PostAsync(http, w, w.S.Clerk, owner, "/api/v1/form-records/field-search", new { formDefinitionId = w.FormId, conditions = new[] { Cond(w.Count, "equals", "Integer", "7") }, pageSize = 25 });
                if (refused.Code == "SEARCH_VERIFICATION_LIMIT")
                {
                    break;
                }

                await Task.Delay(1000, Ct);
            }
            while (DateTime.UtcNow < deadline);
            Assert.Equal(("SEARCH_VERIFICATION_LIMIT", "discoverable"), (refused.Code, refused.Kind));
            var reader = await PostAsync(http, w, w.S.Reader, readerSession!, "/api/v1/form-records/field-search", new { formDefinitionId = w.FormId, conditions = new[] { Cond(w.Reference, "startsWith", "String", "BM-") }, pageSize = 25 });
            Assert.Equal((HttpStatusCode.OK, Shared), (reader.Status, reader.Total));
            ceiling = new Dictionary<string, object?> { ["organization_records"] = have + 1, ["owner"] = $"{(int)refused.Status} {refused.Code} {refused.Kind} ({refused.Milliseconds:F0} ms)", ["reader"] = $"{(int)reader.Status} total {reader.Total} ({reader.Milliseconds:F0} ms)" };
        }

        var summaryReport = new Dictionary<string, object?>
        {
            ["kind"] = "IX-4 local benchmark (report only; local Docker, relative numbers; PERF-01 on Azure staging decides)", ["measured_on"] = DateTimeOffset.UtcNow,
            ["git_commit"] = ComposeStack.GitCommit(), ["images"] = fixture.Stack.ImageFingerprint(), ["repetitions_per_case"] = Repetitions, ["page_size"] = 25,
            ["create_concurrency"] = concurrency, ["budget_minutes"] = budget.TotalMinutes, ["ingestion"] = steps, ["queries"] = report, ["index_building"] = rebuild, ["skipped"] = skipped, ["ceiling"] = ceiling,
            ["limitations"] = "local Docker Desktop on one laptop (AC power checked), Vault Transit HMAC and local ES256 instead of Managed HSM, one Authority instance, synthetic uniform data; the 50,000-record step needs separate owner authorization (C-134)",
        };
        Directory.CreateDirectory(Path.Combine(ComposeStack.RepositoryRoot, "artifacts", "ix-4"));
        await File.WriteAllTextAsync(Path.Combine(ComposeStack.RepositoryRoot, "artifacts", "ix-4", "benchmark.json"), JsonSerializer.Serialize(summaryReport, Indented), Ct);

        // Functional assertions only (the numbers are a report): every run of every case was answered by the index or refused by a
        // caller-owned limit — never by a fallback, an index state or an availability failure (those fail the benchmark).
        Assert.All(report, r =>
        {
            var outcomes = (Dictionary<string, int>)r["outcomes"]!;
            Assert.True(outcomes.Keys.All(k => k == "200 AuthorityAcceptedContent" || k.StartsWith("422 SEARCH_VERIFICATION_LIMIT", StringComparison.Ordinal)), JsonSerializer.Serialize(r));
        });
    }
}
