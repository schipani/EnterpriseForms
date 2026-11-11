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
/// C-149 continuation (R-56): Tier 1 indexed-search availability under permission churn (a Benchmark; never in focused functional
/// runs; about 14 minutes). One synthetic organization: form A (40 records, the count field approved) and form B (20 records). The
/// Reader searches form A through the business API every second while share toggles of form A records to the Reader run at 0, one
/// per 10 s, one per 5 s and one per second, then one per second on form B only (records the Reader never searches), each phase
/// 120 s. Per search: the first-attempt outcome (business status and code), then a bounded client retry (250 ms apart, at most
/// 4 more attempts) and its eventual outcome and latency. In parallel the organization gate is polled every 50 ms, giving the closed
/// intervals and their durations, and the authorization revision rate. Correctness: every answered result is checked against
/// the toggles' commit windows (a record definitely unshared over the whole search must be absent; definitely shared, present).
/// Report: artifacts/ix-5/r56-search-availability.json, written before any assertion. Nothing is relaxed: the test only observes.
/// </summary>
[Collection(StageBStackCollectionDefinition.Name)]
public sealed class SearchAvailabilityUnderPermissionChurnTests(StageBFixture fixture)
{
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static object Stats(IEnumerable<double> values)
    {
        var v = values.Order().ToList();
        double P(double p) => v.Count == 0 ? 0 : Math.Round(v[Math.Clamp((int)Math.Ceiling(p * v.Count) - 1, 0, v.Count - 1)], 1);
        return new { count = v.Count, p50 = P(0.5), p95 = P(0.95), p99 = P(0.99), max = v.Count == 0 ? 0 : Math.Round(v[^1], 1) };
    }

    /// <summary>A share state change of one record: the state before, the state after, and the window [Requested, Confirmed] in which it committed.</summary>
    private sealed record Toggle(Guid Record, bool SharedAfter, DateTimeOffset Requested, DateTimeOffset Confirmed);

    private sealed record Attempt(int Status, string? Code, double Ms);

    private sealed record SearchObservation(string Phase, DateTimeOffset Started, DateTimeOffset Ended, List<Attempt> Attempts, List<Guid>? Result);

    [Fact]
    [Trait("Category", "Benchmark")]
    public async Task MeasureIndexedSearchAvailabilityAtFourPermissionChangeRates()
    {
        // R56_ARRIVAL=random: each search starts at a uniformly random offset inside its one-second slot, so search arrivals are not
        // phase-locked to the permission changes (both otherwise run on aligned one-second schedules).
        var randomArrival = string.Equals(Environment.GetEnvironmentVariable("R56_ARRIVAL"), "random", StringComparison.OrdinalIgnoreCase);
        var arrivals = new Random(5601);
        var phaseSeconds = int.TryParse(Environment.GetEnvironmentVariable("R56_PHASE_SECONDS"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var ps) ? ps : 120;
        var s = new StageBScenario(fixture, "r56-churn");
        await s.BootstrapAsync();
        var org = s.OrganizationId;
        var current = await fixture.Api.GetAsync("/api/v1/organization/security-policy", s.Token(s.Designer));
        Assert.True((await fixture.Api.SendAsync(HttpMethod.Put, "/api/v1/organization/security-policy", s.Token(s.Designer), new { minimumFieldClassification = "Internal", plaintextInternalFieldsPermitted = true, plaintextDowngradePermitted = false, plaintextSearchPermitted = true, policyStatementText = "C-149 R-56 (synthetic).", expectedPolicyRevision = current.Root.GetProperty("securityPolicyRevision").GetInt64() }, Guid.NewGuid().ToString("N"))).IsSuccess);

        async Task<(Guid Form, Guid Count)> FormAsync(string name, bool approve)
        {
            var form = await s.CreateFormAsync("R-56 " + name + " " + s.Name, s.Clerk);
            var added = await fixture.Api.PostAsync($"/api/v1/form-definitions/{form:D}/fields", s.Token(s.Designer), new { fieldKey = "exhibit_count", fieldDisplayName = "Count", fieldDescription = string.Empty, fieldDataType = "Integer", fieldClassification = "Internal", fieldProtectionMode = "Standard" });
            Assert.Equal(HttpStatusCode.Created, added.Status);
            var count = added.Root.GetProperty("fieldDefinitionId").GetGuid();
            await s.SaveDraftAsync(form, StageBScenario.Layout("R-56", StageBScenario.Section("main", "Main", StageBScenario.Field(count, "number", "Count", 4))));
            Assert.True((await s.PublishAsync(form, "v1")).GetProperty("authorityRegistered").GetBoolean());
            if (approve)
            {
                Assert.True((await fixture.Api.PostAsync($"/api/v1/form-definitions/{form:D}/fields/{count:D}/search-approval", s.Token(s.Administrator), new { operators = new[] { "between" }, approvalReference = "R-56", useDevelopmentStepUp = true })).IsSuccess);
                Assert.Equal(HttpStatusCode.NoContent, (await fixture.Api.SendAsync(HttpMethod.Put, $"/api/v1/form-definitions/{form:D}/field-searchers", s.Token(s.Administrator), new { grantUserIds = new[] { s.Reader.AuthorityUserId }, grantTeamIds = Array.Empty<Guid>(), revokeUserIds = Array.Empty<Guid>(), revokeTeamIds = Array.Empty<Guid>() }, Guid.NewGuid().ToString("N"))).Status);
            }

            return (form, count);
        }

        async Task<HttpResult> Retrying(Func<Task<HttpResult>> call)
        {
            for (var attempt = 0; ; attempt++)
            {
                var result = await call();
                if (result.IsSuccess || attempt >= 60 || result.ErrorCode is not ("AUTHORITY_NOT_READY" or "COMMAND_PENDING" or "STALE_AUTHORIZATION_REVISION"))
                {
                    return result;
                }

                await Task.Delay(250, Ct);
            }
        }

        var a = await FormAsync("A", approve: true);
        var b = await FormAsync("B", approve: false);
        var recordsA = new List<Guid>();
        var recordsB = new List<Guid>();
        foreach (var (form, list, n) in new[] { (a.Form, recordsA, 40), (b.Form, recordsB, 20) })
        {
            for (var i = 0; i < n; i++)
            {
                var created = await Retrying(() => fixture.Api.PostAsync($"/api/v1/form-definitions/{form:D}/records", s.Token(s.Clerk), new { values = new Dictionary<string, string> { ["exhibit_count"] = i.ToString(CultureInfo.InvariantCulture) } }, "r56-" + Guid.CreateVersion7().ToString("N")));
                Assert.True(created.IsSuccess, created.Body);
                list.Add(created.Root.GetProperty("formRecordId").GetGuid());
            }
        }

        await s.WaitProjectionAsync(s.Clerk, recordsB[^1]);
        await s.WaitGateReadyAsync(TimeSpan.FromSeconds(60));
        var deadline = DateTime.UtcNow.AddSeconds(300);
        while (fixture.Stack.AuthorityObserverSql($"SELECT count(*) FROM authority_search.search_field_coverages WHERE organization_id = '{org:D}' AND coverage_state_name = 'Complete';").Trim().Split('\n')[0].Trim() != "1")
        {
            Assert.True(DateTime.UtcNow < deadline, "coverage not Complete");
            await Task.Delay(1000, Ct);
        }

        // Share state of every record of A (to the Reader) and of B (to the Designer, for the other-form phase).
        var shareOf = new Dictionary<Guid, Guid?>();
        var toggles = new ConcurrentBag<Toggle>();
        async Task<Guid> ShareAsync(Guid record, Guid user)
        {
            var r = await Retrying(() => fixture.Api.PostAsync($"/api/v1/form-records/{record:D}/shares", s.Token(s.Clerk), new { recipientUserId = user, recipientTeamId = (Guid?)null, recipientRole = "Reader", fieldKeys = new[] { "exhibit_count" } }));
            Assert.True(r.IsSuccess, r.Body);
            return r.Root.GetProperty("recordShareId").GetGuid();
        }

        async Task ToggleAsync(Guid record, Guid user)
        {
            var requested = DateTimeOffset.UtcNow;
            if (shareOf.TryGetValue(record, out var share) && share is { } id)
            {
                var r = await Retrying(() => fixture.Api.PostAsync($"/api/v1/form-records/{record:D}/shares/{id:D}/revoke", s.Token(s.Clerk), new { }));
                Assert.True(r.IsSuccess, r.Body);
                shareOf[record] = null;
                toggles.Add(new Toggle(record, false, requested, DateTimeOffset.UtcNow));
            }
            else
            {
                shareOf[record] = await ShareAsync(record, user);
                toggles.Add(new Toggle(record, true, requested, DateTimeOffset.UtcNow));
            }
        }

        var initialShared = new HashSet<Guid>();
        foreach (var record in recordsA.Take(20))
        {
            shareOf[record] = await ShareAsync(record, s.Reader.AuthorityUserId);
            initialShared.Add(record);
        }

        await s.WaitGateReadyAsync(TimeSpan.FromSeconds(60));
        var digest = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        Assert.True((await fixture.Authority.SendAsync(HttpMethod.Post, "/internal/v1/sessions", TestIdentity.WorkloadToken(), new { organization_id = org, session_digest_base64 = digest, absolute_expires_on = DateTimeOffset.UtcNow.AddHours(2) }, s.Token(s.Reader))).IsSuccess);
        using var http = new HttpClient { BaseAddress = new Uri(StageBFixture.ApiBaseAddress), Timeout = TimeSpan.FromMinutes(1) };

        async Task<(Attempt Attempt, List<Guid>? Result)> SearchOnceAsync()
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/form-records/field-search")
            {
                Content = JsonContent.Create(new { formDefinitionId = a.Form, conditions = new[] { new { fieldDefinitionId = a.Count, @operator = "between", valueType = "Integer", lowerBound = "0", upperBound = "1000" } }, pageSize = 100 }),
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", s.Token(s.Reader));
            request.Headers.Add("X-Testing-Session-Digest", digest);
            var clock = Stopwatch.StartNew();
            using var response = await http.SendAsync(request, CancellationToken.None);
            var body = await response.Content.ReadAsStringAsync(CancellationToken.None);
            using var doc = JsonDocument.Parse(body.Length == 0 ? "{}" : body);
            var root = doc.RootElement;
            var code = root.TryGetProperty("errorCode", out var e) && e.ValueKind == JsonValueKind.String ? e.GetString() : root.TryGetProperty("title", out var t) ? t.GetString() : null;
            var result = response.IsSuccessStatusCode ? root.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("formRecordId").GetGuid()).ToList() : null;
            return (new Attempt((int)response.StatusCode, response.IsSuccessStatusCode ? null : code, clock.Elapsed.TotalMilliseconds), result);
        }

        // Gate poller: closed intervals and authorization revisions over the whole measurement.
        var gateSamples = new ConcurrentQueue<(DateTimeOffset At, bool Ready, long Revision, string State)>();
        using var stopGate = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var gatePoller = Task.Run(async () =>
        {
            while (!stopGate.IsCancellationRequested)
            {
                try
                {
                    var gate = await fixture.Authority.GateAsync(org);
                    var state = gate.GetProperty("projection_gate_state").GetString() ?? string.Empty;
                    gateSamples.Enqueue((DateTimeOffset.UtcNow, state == "Ready", gate.TryGetProperty("authorization_revision", out var rev) ? rev.GetInt64() : -1, state));
                    await Task.Delay(50, stopGate.Token);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }, CancellationToken.None);

        var observations = new List<SearchObservation>();
        var phases = new (string Name, double ChangesPerSecond, bool OtherForm)[] { ("none", 0, false), ("every_10s", 0.1, false), ("every_5s", 0.2, false), ("every_1s", 1, false), ("every_1s_other_form", 1, true) };
        var phaseWindows = new List<(string Name, DateTimeOffset Start, DateTimeOffset End, int Changes)>();
        var random = new Random(56);
        try
        {
            foreach (var (name, rate, otherForm) in phases)
            {
                var start = DateTimeOffset.UtcNow;
                var changes = 0;
                using var stopChanges = CancellationTokenSource.CreateLinkedTokenSource(Ct);
                var changer = rate == 0 ? Task.CompletedTask : Task.Run(async () =>
                {
                    var clock = Stopwatch.StartNew();
                    var issued = 0;
                    while (!stopChanges.IsCancellationRequested)
                    {
                        var due = TimeSpan.FromSeconds(issued / rate) - clock.Elapsed;
                        if (due > TimeSpan.Zero)
                        {
                            try
                            {
                                await Task.Delay(due, stopChanges.Token);
                            }
                            catch (OperationCanceledException)
                            {
                                return;
                            }
                        }

                        issued++;
                        if (otherForm)
                        {
                            await ToggleAsync(recordsB[random.Next(recordsB.Count)], s.Designer.AuthorityUserId);
                        }
                        else
                        {
                            await ToggleAsync(recordsA[random.Next(recordsA.Count)], s.Reader.AuthorityUserId);
                        }

                        Interlocked.Increment(ref changes);
                    }
                }, CancellationToken.None);

                var phaseClock = Stopwatch.StartNew();
                var searches = 0;
                while (phaseClock.Elapsed < TimeSpan.FromSeconds(phaseSeconds))
                {
                    var due = TimeSpan.FromSeconds(searches + (randomArrival ? arrivals.NextDouble() : 0)) - phaseClock.Elapsed;
                    if (due > TimeSpan.Zero)
                    {
                        await Task.Delay(due, Ct);
                    }

                    searches++;
                    var started = DateTimeOffset.UtcNow;
                    var attempts = new List<Attempt>();
                    List<Guid>? result = null;
                    for (var attempt = 0; attempt < 5; attempt++)
                    {
                        var (outcome, items) = await SearchOnceAsync();
                        attempts.Add(outcome);
                        if (outcome.Status == 200)
                        {
                            result = items;
                            break;
                        }

                        if (outcome.Code is not ("AUTHORITY_NOT_READY" or "SEARCH_SNAPSHOT_CHANGED"))
                        {
                            break; // a non-retryable refusal: recorded as is
                        }

                        await Task.Delay(250, Ct);
                    }

                    observations.Add(new SearchObservation(name, started, DateTimeOffset.UtcNow, attempts, result));
                }

                await stopChanges.CancelAsync();
                await changer;
                phaseWindows.Add((name, start, DateTimeOffset.UtcNow, changes));
                await s.WaitGateReadyAsync(TimeSpan.FromSeconds(60));
            }
        }
        finally
        {
            await stopGate.CancelAsync();
            await gatePoller;
        }

        // Correctness: shared state of each record of A as a function of time, with uncertainty windows at each toggle.
        var timeline = toggles.Where(t => recordsA.Contains(t.Record)).GroupBy(t => t.Record).ToDictionary(g => g.Key, g => g.OrderBy(t => t.Requested).ToList());
        bool? DefinitelyShared(Guid record, DateTimeOffset from, DateTimeOffset to)
        {
            var state = initialShared.Contains(record);
            var list = timeline.GetValueOrDefault(record) ?? [];
            bool? constant = null;
            var cursorState = state;
            var known = new List<(DateTimeOffset From, DateTimeOffset To, bool? Shared)>();
            var last = DateTimeOffset.MinValue;
            foreach (var t in list)
            {
                known.Add((last, t.Requested, cursorState));
                known.Add((t.Requested, t.Confirmed, null)); // uncertain while the command commits
                cursorState = t.SharedAfter;
                last = t.Confirmed;
            }

            known.Add((last, DateTimeOffset.MaxValue, cursorState));
            foreach (var (f, t2, shared) in known.Where(k => k.To > from && k.From < to))
            {
                if (shared is null || (constant is { } c && c != shared))
                {
                    return null;
                }

                constant = shared;
            }

            return constant;
        }

        var violations = new List<string>();
        foreach (var o in observations.Where(o => o.Result is not null))
        {
            foreach (var record in recordsA)
            {
                var definitely = DefinitelyShared(record, o.Started, o.Ended);
                var present = o.Result!.Contains(record);
                if (definitely == false && present)
                {
                    violations.Add($"{o.Phase}: record {record:D} returned while definitely unshared");
                }
                else if (definitely == true && !present)
                {
                    violations.Add($"{o.Phase}: record {record:D} missing while definitely shared");
                }
            }

            if (o.Result!.Any(r => !recordsA.Contains(r)))
            {
                violations.Add($"{o.Phase}: a record outside form A was returned");
            }
        }

        var samples = gateSamples.ToList();
        object PhaseReport((string Name, DateTimeOffset Start, DateTimeOffset End, int Changes) w)
        {
            var obs = observations.Where(o => o.Phase == w.Name).ToList();
            var gate = samples.Where(g => g.At >= w.Start && g.At <= w.End).ToList();
            var closed = new List<double>();
            DateTimeOffset? closedSince = null;
            foreach (var g in gate)
            {
                if (!g.Ready && closedSince is null)
                {
                    closedSince = g.At;
                }
                else if (g.Ready && closedSince is { } since)
                {
                    closed.Add((g.At - since).TotalMilliseconds);
                    closedSince = null;
                }
            }

            var minutes = (w.End - w.Start).TotalMinutes;
            var revisions = gate.Count < 2 ? 0 : gate[^1].Revision - gate[0].Revision;
            return new
            {
                phase = w.Name,
                seconds = Math.Round((w.End - w.Start).TotalSeconds, 1),
                permission_changes = w.Changes,
                authorization_revisions_per_minute = Math.Round(revisions / minutes, 1),
                gate_samples = gate.Count,
                gate_closed_fraction = gate.Count == 0 ? 0 : Math.Round(gate.Count(g => !g.Ready) / (double)gate.Count, 3),
                gate_samples_by_state = gate.GroupBy(g => g.State).ToDictionary(g => g.Key, g => g.Count()),
                gate_closed_interval_ms = Stats(closed),
                searches = obs.Count,
                first_attempt = obs.GroupBy(o => o.Attempts[0].Status == 200 ? "200" : $"{o.Attempts[0].Status} {o.Attempts[0].Code}").ToDictionary(g => g.Key, g => g.Count()),
                first_attempt_answered_fraction = obs.Count == 0 ? 0 : Math.Round(obs.Count(o => o.Attempts[0].Status == 200) / (double)obs.Count, 3),
                with_bounded_retry = new
                {
                    answered = obs.Count(o => o.Result is not null),
                    answered_fraction = obs.Count == 0 ? 0 : Math.Round(obs.Count(o => o.Result is not null) / (double)obs.Count, 3),
                    still_refused = obs.Where(o => o.Result is null).GroupBy(o => $"{o.Attempts[^1].Status} {o.Attempts[^1].Code}").ToDictionary(g => g.Key, g => g.Count()),
                    attempts_when_answered = Stats(obs.Where(o => o.Result is not null).Select(o => (double)o.Attempts.Count)),
                    total_ms_when_answered = Stats(obs.Where(o => o.Result is not null).Select(o => (o.Ended - o.Started).TotalMilliseconds)),
                },
                answered_attempt_ms = Stats(obs.SelectMany(o => o.Attempts).Where(x => x.Status == 200).Select(x => x.Ms)),
                refused_attempt_ms = Stats(obs.SelectMany(o => o.Attempts).Where(x => x.Status != 200).Select(x => x.Ms)),
            };
        }

        var report = new
        {
            kind = "C-149 R-56 Tier 1 indexed-search availability under permission churn (focused, local Docker, synthetic; indicative)",
            measured_on = DateTimeOffset.UtcNow,
            git_commit = ComposeStack.GitCommit(),
            stack = fixture.Stack.ImageFingerprint(),
            phase_seconds = phaseSeconds,
            search_arrival = randomArrival ? "uniform random offset within each one-second slot" : "aligned one-second slots (phase-locked with the changes)",
            client_retry = "on 503 AUTHORITY_NOT_READY or 409 SEARCH_SNAPSHOT_CHANGED: 250 ms pause, at most 4 more attempts (test client only; the business API's own single immediate retry is unchanged)",
            phases = phaseWindows.Select(PhaseReport),
            correctness = new { answered_searches_checked = observations.Count(o => o.Result is not null), violations },
        };
        var directory = Path.Combine(ComposeStack.RepositoryRoot, "artifacts", "ix-5");
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(Path.Combine(directory, (randomArrival ? "r56-search-availability-random-arrival.json" : "r56-search-availability.json")), JsonSerializer.Serialize(report, Indented), Ct);
        Assert.True(violations.Count == 0, string.Join("; ", violations.Take(10)));
    }
}
