using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using EnterpriseForms.SecurityTests.Harness;
using EnterpriseForms.StageBTests.Harness;
using Xunit;

namespace EnterpriseForms.StageBTests.Tests;

/// <summary>
/// C-136 (R-33/R-36) bounded measurement of the bounded-transaction seal sweep (a Benchmark; never in focused functional runs).
/// One synthetic organization of 1,000 records (created serially through the public API while the sweeps run; save latency
/// recorded), then, for each S-B transaction size (<c>IX4_SEAL_SIZES</c>, default 2000 = the former single transaction, 200 and 50)
/// the Authority alone is recreated with that size and, over at least two sweep cycles: every S-B transaction's organization-lock
/// hold for this organization (event 9305), 40 sequential indexed searches and 10 saves. The Authority is restored to the
/// default size afterwards. Report: artifacts/ix-4/seal-sweep-lock.json. No latency assertion.
/// </summary>
[Collection(StageBStackCollectionDefinition.Name)]
public sealed partial class SealSweepLockMeasurementTests(StageBFixture fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    [GeneratedRegex(@"IX4-SWEEP-LOCK organization=(?<org>[0-9a-f-]{36}) s_a_ms=(?<a>[0-9.]+) s_b_transactions=(?<n>[0-9]+) s_b_longest_ms=(?<longest>[0-9.]+) s_b_total_ms=(?<total>[0-9.]+)")]
    private static partial Regex LockLine();

    private static object Stats(IEnumerable<double> values)
    {
        var v = values.Order().ToList();
        double P(double p) => v.Count == 0 ? 0 : Math.Round(v[Math.Clamp((int)Math.Ceiling(p * v.Count) - 1, 0, v.Count - 1)], 1);
        return new { count = v.Count, p50 = P(0.5), p95 = P(0.95), max = v.Count == 0 ? 0 : Math.Round(v[^1], 1) };
    }

    [Fact]
    [Trait("Category", "Benchmark")]
    public async Task MeasureOrganizationLockHoldSearchesAndSavesWhileSealSweepsRun()
    {
        var sizes = (Environment.GetEnvironmentVariable("IX4_SEAL_SIZES") ?? "2000,200,50").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(int.Parse).ToList();
        var records = int.Parse(Environment.GetEnvironmentVariable("IX4_SEAL_RECORDS") ?? "1000", CultureInfo.InvariantCulture);
        var s = new StageBScenario(fixture, "ix4-seal-lock");
        await s.BootstrapAsync();
        var current = await fixture.Api.GetAsync("/api/v1/organization/security-policy", s.Token(s.Designer));
        Assert.True((await fixture.Api.SendAsync(HttpMethod.Put, "/api/v1/organization/security-policy", s.Token(s.Designer), new { minimumFieldClassification = "Internal", plaintextInternalFieldsPermitted = true, plaintextDowngradePermitted = false, plaintextSearchPermitted = true, policyStatementText = "C-136 measurement (synthetic).", expectedPolicyRevision = current.Root.GetProperty("securityPolicyRevision").GetInt64() }, Guid.NewGuid().ToString("N"))).IsSuccess);
        var formId = await s.CreateFormAsync("C-136 seal lock", s.Clerk);
        var added = await fixture.Api.PostAsync($"/api/v1/form-definitions/{formId:D}/fields", s.Token(s.Designer), new { fieldKey = "case_reference", fieldDisplayName = "Case reference", fieldDescription = string.Empty, fieldDataType = "String", options = (object?)null, fieldClassification = "Internal", fieldProtectionMode = "Standard" });
        Assert.True(added.Status == HttpStatusCode.Created, added.Body);
        var field = added.Root.GetProperty("fieldDefinitionId").GetGuid();
        await s.SaveDraftAsync(formId, StageBScenario.Layout("C-136", StageBScenario.Section("main", "Main", StageBScenario.Field(field, "text", "Case reference", 4))));
        Assert.True((await s.PublishAsync(formId, "v1")).GetProperty("authorityRegistered").GetBoolean());
        Assert.True((await fixture.Api.PostAsync($"/api/v1/form-definitions/{formId:D}/fields/{field:D}/search-approval", s.Token(s.Administrator), new { operators = new[] { "equals", "startsWith" }, approvalReference = "C-136", useDevelopmentStepUp = true })).IsSuccess);
        Assert.Equal(HttpStatusCode.NoContent, (await fixture.Api.SendAsync(HttpMethod.Put, $"/api/v1/form-definitions/{formId:D}/field-searchers", s.Token(s.Administrator), new { grantUserIds = new[] { s.Clerk.AuthorityUserId }, grantTeamIds = Array.Empty<Guid>(), revokeUserIds = Array.Empty<Guid>(), revokeTeamIds = Array.Empty<Guid>() }, Guid.NewGuid().ToString("N"))).Status);

        var next = 0;
        async Task<(double Ms, int Attempts)> SaveAsync()
        {
            var i = Interlocked.Increment(ref next) - 1;
            var key = $"c136-{s.OrganizationId:N}-{i}";
            var clock = Stopwatch.StartNew();
            for (var attempt = 1; ; attempt++)
            {
                var created = await fixture.Api.PostAsync($"/api/v1/form-definitions/{formId:D}/records", s.Token(s.Clerk), new { values = new Dictionary<string, string> { ["case_reference"] = $"SL-{i:D5}" } }, key);
                if (created.Status is HttpStatusCode.Created or HttpStatusCode.OK)
                {
                    return (clock.Elapsed.TotalMilliseconds, attempt);
                }

                Assert.True(attempt < 300, $"save {i}: {created.Body}");
                if (created.ErrorCode == "STALE_AUTHORIZATION_REVISION")
                {
                    key = $"c136-{s.OrganizationId:N}-{i}-{attempt}";
                }

                await Task.Delay(100, Ct);
            }
        }

        var seedClock = Stopwatch.StartNew();
        var seedSaves = new List<double>();
        for (var i = 0; i < records; i++)
        {
            seedSaves.Add((await SaveAsync()).Ms);
        }

        var seedSeconds = seedClock.Elapsed.TotalSeconds;
        var digest = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        Assert.True((await fixture.Authority.SendAsync(HttpMethod.Post, "/internal/v1/sessions", TestIdentity.WorkloadToken(), new { organization_id = s.OrganizationId, session_digest_base64 = digest, absolute_expires_on = DateTimeOffset.UtcNow.AddHours(2) }, s.Token(s.Clerk))).IsSuccess);
        using var http = new HttpClient { BaseAddress = new Uri(StageBFixture.ApiBaseAddress), Timeout = TimeSpan.FromMinutes(2) };
        async Task<(double Ms, int Status, string? Code)> SearchAsync()
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/form-records/field-search")
            {
                Content = JsonContent.Create(new { formDefinitionId = formId, conditions = new[] { new { fieldDefinitionId = field, @operator = "startsWith", valueType = "String", value = "SL-001" } }, pageSize = 25 }),
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", s.Token(s.Clerk));
            request.Headers.Add("X-Testing-Session-Digest", digest);
            var clock = Stopwatch.StartNew();
            using var response = await http.SendAsync(request, Ct);
            var body = await response.Content.ReadAsStringAsync(Ct);
            var root = JsonDocument.Parse(body).RootElement;
            return (clock.Elapsed.TotalMilliseconds, (int)response.StatusCode, root.TryGetProperty("title", out var t) ? t.GetString() : null);
        }

        var configurations = new List<object>();
        try
        {
            foreach (var size in sizes)
            {
                Environment.SetEnvironmentVariable("IX4_SEAL_TRANSACTION_SIZE", size.ToString(CultureInfo.InvariantCulture));
                fixture.Stack.Compose("up -d --no-deps --force-recreate --wait authority", 300);
                var since = DateTimeOffset.UtcNow;
                await Task.Delay(TimeSpan.FromSeconds(25), Ct); // at least two sweep cycles (10 s interval) before measuring
                var searches = new List<(double Ms, int Status, string? Code)>();
                var saves = new List<double>();
                for (var n = 0; n < 40; n++)
                {
                    searches.Add(await SearchAsync());
                    if (n % 4 == 3)
                    {
                        saves.Add((await SaveAsync()).Ms);
                    }
                }

                var log = fixture.Stack.Compose($"logs --no-color --since {since:yyyy-MM-ddTHH:mm:ssZ} authority", 120, throwOnError: false);
                var passes = LockLine().Matches(log).Where(m => Guid.Parse(m.Groups["org"].Value) == s.OrganizationId).ToList();
                configurations.Add(new
                {
                    seal_transaction_size = size,
                    seal_passes = passes.Count,
                    seal_transactions_per_pass = Stats(passes.Select(m => double.Parse(m.Groups["n"].Value, CultureInfo.InvariantCulture))),
                    longest_seal_transaction_ms = Stats(passes.Select(m => double.Parse(m.Groups["longest"].Value, CultureInfo.InvariantCulture))),
                    seal_pass_total_locked_ms = Stats(passes.Select(m => double.Parse(m.Groups["total"].Value, CultureInfo.InvariantCulture))),
                    cheap_pass_ms = Stats(passes.Select(m => double.Parse(m.Groups["a"].Value, CultureInfo.InvariantCulture))),
                    verification_entries_per_second = passes.Count == 0 ? 0 : Math.Round(records / (passes.Average(m => double.Parse(m.Groups["total"].Value, CultureInfo.InvariantCulture)) / 1000), 1),
                    searches = new { outcomes = searches.GroupBy(x => x.Status == 200 ? "200" : $"{x.Status} {x.Code}").ToDictionary(g => g.Key, g => g.Count()), answered_ms = Stats(searches.Where(x => x.Status == 200).Select(x => x.Ms)), all_ms = Stats(searches.Select(x => x.Ms)) },
                    saves_ms = Stats(saves),
                });
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable("IX4_SEAL_TRANSACTION_SIZE", null);
            fixture.Stack.Compose("up -d --no-deps --force-recreate --wait authority", 300);
        }

        var report = new Dictionary<string, object?>
        {
            ["kind"] = "C-136 seal-sweep organization-lock measurement (bounded; local Docker; sweep every 10 s in the security-test stack)",
            ["measured_on"] = DateTimeOffset.UtcNow, ["git_commit"] = ComposeStack.GitCommit(), ["organization_records"] = records,
            ["seeding"] = new { seconds = Math.Round(seedSeconds), records_per_second = Math.Round(records / seedSeconds, 2), save_ms = Stats(seedSaves), note = "serial saves through the public API while sweeps ran with the default transaction size of the image" },
            ["configurations"] = configurations,
        };
        Directory.CreateDirectory(Path.Combine(ComposeStack.RepositoryRoot, "artifacts", "ix-4"));
        await File.WriteAllTextAsync(Path.Combine(ComposeStack.RepositoryRoot, "artifacts", "ix-4", "seal-sweep-lock.json"), JsonSerializer.Serialize(report, Indented), Ct);
    }
}
