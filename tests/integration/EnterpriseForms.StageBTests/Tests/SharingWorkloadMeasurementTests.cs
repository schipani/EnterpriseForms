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
/// C-149 P-4: a bounded, focused sharing workload (a Benchmark; never in focused functional runs; at most about 15 minutes with the
/// defaults). One synthetic organization: <c>P4_USERS</c> extra users (default 40), overlapping teams of 5, 10 and 20 members, three
/// forms of <c>P4_RECORDS_PER_FORM</c> records (default 30): two searchable, one blocked (a record forged by the business database
/// administrator before approval, so its coverage can never be attested Complete). Over <c>P4_SECONDS</c> (default 300) at a fixed
/// rate of one command per second: direct and team shares of field subsets (overlapping grants included), revocations and team
/// member additions and removals, while indexed searches run every 2 s. Records: command latency, the time until the dark Tier 2
/// projection is Ready again after each command (loop only, no catch-up call), journal events per minute by kind, archive backlog
/// and lag, SpiceDB calls (when <c>IX5_SPICEDB_CALL_COUNTING=true</c>) and search outcomes. Then checks correctness: Tier 2 Ready,
/// verified CLEAN and journal-complete, and for every user the internal Tier 2 evaluation equals the test's own model of who may
/// read each record's count (direct or team share including that field; the share-only item rule). The blocked form has no value
/// rows. Report: artifacts/ix-5/p4-sharing-workload.json, written before any assertion. Synthetic data only.
/// </summary>
[Collection(StageBStackCollectionDefinition.Name)]
public sealed class SharingWorkloadMeasurementTests(StageBFixture fixture)
{
    private const string Prefix = "/internal/v1/testing/search-access";

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static int Setting(string name, int fallback) => int.TryParse(Environment.GetEnvironmentVariable(name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : fallback;

    private static object Stats(IEnumerable<double> values)
    {
        var v = values.Order().ToList();
        double P(double p) => v.Count == 0 ? 0 : Math.Round(v[Math.Clamp((int)Math.Ceiling(p * v.Count) - 1, 0, v.Count - 1)], 1);
        return new { count = v.Count, p50 = P(0.5), p95 = P(0.95), p99 = P(0.99), max = v.Count == 0 ? 0 : Math.Round(v[^1], 1) };
    }

    private sealed record Share(Guid Record, Guid ShareId, Guid? User, Guid? Team, string[] Fields);

    private static async Task<T> RetryAsync<T>(Func<Task<HttpResult>> call, Func<HttpResult, T> success, string what)
    {
        for (var attempt = 0; ; attempt++)
        {
            var result = await call();
            if (result.IsSuccess)
            {
                return success(result);
            }

            Assert.True(attempt < 60 && result.ErrorCode is "AUTHORITY_NOT_READY" or "COMMAND_PENDING" or "STALE_AUTHORIZATION_REVISION" or "CONTENT_REVISION_CONFLICT" or "TEAM_MEMBERSHIP_OPERATION_IN_PROGRESS", what + " failed: " + result.Body);
            await Task.Delay(250, Ct);
        }
    }

    private async Task<JsonElement> StatusAsync(Guid org)
    {
        var result = await fixture.Authority.SendAsync(HttpMethod.Get, Prefix + $"/{org:D}/status", TestIdentity.WorkloadToken(), null);
        Assert.True(result.IsSuccess, result.Body);
        return result.Root.Clone();
    }

    /// <summary>C-149 P-2: SpiceDB calls of the Authority's background loops over an idle window (no request), as the baseline the
    /// P-4 window's background bucket is compared with. Needs IX5_SPICEDB_CALL_COUNTING=true on the Authority. artifacts/ix-5/p2-spicedb-idle-background.json.</summary>
    [Fact]
    [Trait("Category", "Benchmark")]
    public async Task MeasureIdleBackgroundSpiceDbCalls()
    {
        var seconds = Setting("P2_IDLE_SECONDS", 120);
        var reset = await fixture.Authority.SendAsync(HttpMethod.Post, "/internal/v1/testing/spicedb-calls/reset", TestIdentity.WorkloadToken(), new { });
        Assert.True(reset.IsSuccess, "SpiceDB call counting is not enabled: " + reset.Body);
        var organizations = fixture.Stack.AuthorityObserverSql("SELECT count(*) FROM organization_authorization_states;").Trim().Split('\n')[0].Trim();
        await Task.Delay(TimeSpan.FromSeconds(seconds), Ct);
        var calls = await fixture.Authority.SendAsync(HttpMethod.Get, "/internal/v1/testing/spicedb-calls", TestIdentity.WorkloadToken(), null);
        var report = new { kind = "C-149 P-2 idle background SpiceDB calls (local Docker; no requests in the window)", window_seconds = seconds, organizations, measured_on = DateTimeOffset.UtcNow, git_commit = ComposeStack.GitCommit(), calls = JsonDocument.Parse(calls.Body).RootElement.Clone() };
        Directory.CreateDirectory(Path.Combine(ComposeStack.RepositoryRoot, "artifacts", "ix-5"));
        await File.WriteAllTextAsync(Path.Combine(ComposeStack.RepositoryRoot, "artifacts", "ix-5", "p2-spicedb-idle-background.json"), JsonSerializer.Serialize(report, Indented), Ct);
    }

    [Fact]
    [Trait("Category", "Benchmark")]
    public async Task MeasureABoundedSharingWorkloadAndCheckTier2AgainstTheModel()
    {
        var userCount = Setting("P4_USERS", 40);
        var perForm = Setting("P4_RECORDS_PER_FORM", 30);
        var seconds = Setting("P4_SECONDS", 300);
        var random = new Random(149);
        var s = new StageBScenario(fixture, "p4-sharing");
        var users = Enumerable.Range(0, userCount).Select(i => TestUser.New("P4User" + i.ToString(CultureInfo.InvariantCulture))).ToList();

        // Bootstrap with the personas and the extra users (as StageBScenario.BootstrapAsync, plus the users).
        var personas = new[] { (s.Designer, new[] { "organization_owner", "designer" }), (s.Clerk, Array.Empty<string>()), (s.Reader, Array.Empty<string>()), (s.Administrator, new[] { "system_administrator" }), (s.DesignerOnly, new[] { "designer" }) };
        var bootstrap = await fixture.Authority.OperatorAsync(HttpMethod.Post, "/internal/v1/operator/organizations", new
        {
            organization_id = s.OrganizationId,
            organization_display_name = "Stage B " + s.Name,
            identities = personas.Select(p => (object)new { authority_user_id = p.Item1.AuthorityUserId, issuer_uri = TestIdentity.Issuer, subject_identifier = p.Item1.Subject, organization_roles = p.Item2 })
                .Concat(users.Select(u => (object)new { authority_user_id = u.AuthorityUserId, issuer_uri = TestIdentity.Issuer, subject_identifier = u.Subject, organization_roles = Array.Empty<string>() })).ToArray(),
        });
        Assert.True(bootstrap.IsSuccess, bootstrap.Body);
        await s.WaitGateReadyAsync();
        foreach (var user in personas.Select(p => p.Item1).Concat(users))
        {
            var me = await fixture.Api.GetAsync("/api/v1/users/me", s.Token(user));
            Assert.True(me.IsSuccess, me.Body);
        }

        var org = s.OrganizationId;
        var current = await fixture.Api.GetAsync("/api/v1/organization/security-policy", s.Token(s.Designer));
        Assert.True((await fixture.Api.SendAsync(HttpMethod.Put, "/api/v1/organization/security-policy", s.Token(s.Designer), new { minimumFieldClassification = "Internal", plaintextInternalFieldsPermitted = true, plaintextDowngradePermitted = false, plaintextSearchPermitted = true, policyStatementText = "C-149 P-4 (synthetic).", expectedPolicyRevision = current.Root.GetProperty("securityPolicyRevision").GetInt64() }, Guid.NewGuid().ToString("N"))).IsSuccess);

        async Task<(Guid Form, Guid Count, Guid Status, Dictionary<string, Guid> Options)> FormAsync(string name)
        {
            var formId = await s.CreateFormAsync("P-4 " + name + " " + s.Name, s.Clerk);
            var count = await fixture.Api.PostAsync($"/api/v1/form-definitions/{formId:D}/fields", s.Token(s.Designer), new { fieldKey = "exhibit_count", fieldDisplayName = "Count", fieldDescription = string.Empty, fieldDataType = "Integer", fieldClassification = "Internal", fieldProtectionMode = "Standard" });
            Assert.Equal(HttpStatusCode.Created, count.Status);
            var status = await fixture.Api.PostAsync($"/api/v1/form-definitions/{formId:D}/fields", s.Token(s.Designer), new { fieldKey = "case_status", fieldDisplayName = "Status", fieldDescription = string.Empty, fieldDataType = "Option", fieldClassification = "Internal", fieldProtectionMode = "Standard", optionSelectionMode = "Single", options = new[] { new { optionKey = "open", optionDisplayText = "Open" }, new { optionKey = "closed", optionDisplayText = "Closed" } } });
            Assert.Equal(HttpStatusCode.Created, status.Status);
            var countId = count.Root.GetProperty("fieldDefinitionId").GetGuid();
            var statusId = status.Root.GetProperty("fieldDefinitionId").GetGuid();
            var options = status.Root.GetProperty("options").EnumerateArray().ToDictionary(o => o.GetProperty("optionKey").GetString()!, o => o.GetProperty("fieldOptionId").GetGuid());
            var snapshot = string.Join(",", options.Select((o, i) => $$"""{"fieldOptionId":"{{o.Value:D}}","label":"{{o.Key}}","sortOrder":{{i + 1}}}"""));
            await s.SaveDraftAsync(formId, StageBScenario.Layout("P-4", StageBScenario.Section("main", "Main", StageBScenario.Field(countId, "number", "Count", 2),
                $$"""{"kind":"field","fieldDefinitionId":"{{statusId:D}}","control":"select","label":"Status","layout":{"columnSpan":2,"startOnNewRow":false},"optionsSnapshot":[{{snapshot}}]}""")));
            Assert.True((await s.PublishAsync(formId, "v1")).GetProperty("authorityRegistered").GetBoolean());
            return (formId, countId, statusId, options);
        }

        async Task ApproveAsync(Guid form, Guid count, Guid status)
        {
            foreach (var (field, ops) in new[] { (count, new[] { "equals", "between" }), (status, new[] { "is" }) })
            {
                var approved = await fixture.Api.PostAsync($"/api/v1/form-definitions/{form:D}/fields/{field:D}/search-approval", s.Token(s.Administrator), new { operators = ops, approvalReference = "P-4", useDevelopmentStepUp = true });
                Assert.True(approved.IsSuccess, approved.Body);
            }
        }

        var forms = new[] { await FormAsync("A"), await FormAsync("B"), await FormAsync("C blocked") };
        var records = new Dictionary<Guid, List<Guid>>();
        var countValue = new Dictionary<Guid, int>();
        var seedClock = Stopwatch.StartNew();
        foreach (var form in forms)
        {
            records[form.Form] = [];
            for (var i = 0; i < perForm; i++)
            {
                var value = random.Next(0, 1000);
                var values = new Dictionary<string, string> { ["exhibit_count"] = value.ToString(CultureInfo.InvariantCulture), ["case_status"] = i % 2 == 0 ? "open" : "closed" };
                var id = await RetryAsync(() => fixture.Api.PostAsync($"/api/v1/form-definitions/{form.Form:D}/records", s.Token(s.Clerk), new { values }, "p4-" + Guid.CreateVersion7().ToString("N")), r => r.Root.GetProperty("formRecordId").GetGuid(), "create");
                records[form.Form].Add(id);
                countValue[id] = value;
            }
        }

        var seedSeconds = seedClock.Elapsed.TotalSeconds;
        await s.WaitProjectionAsync(s.Clerk, records[forms[2].Form][^1]);

        // The blocked form: one record forged by the business database administrator before approval (as R-52).
        var forged = records[forms[2].Form][0];
        fixture.Stack.AttackerSql($"UPDATE field_integer_values SET integer_value = integer_value + 100000 WHERE form_record_id = '{forged:D}';");
        foreach (var form in forms)
        {
            await ApproveAsync(form.Form, form.Count, form.Status);
        }

        string Scalar(string sql) => fixture.Stack.AuthorityObserverSql(sql).Trim().Split('\n')[0].Trim();
        async Task WaitAsync(Func<bool> condition, int timeoutSeconds, string what)
        {
            var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
            while (!condition())
            {
                Assert.True(DateTime.UtcNow < deadline, what);
                await Task.Delay(1000, Ct);
            }
        }

        await WaitAsync(() => Scalar($"SELECT count(*) FROM authority_search.search_field_coverages WHERE organization_id = '{org:D}' AND coverage_state_name = 'Complete' AND form_definition_id IN ('{forms[0].Form:D}', '{forms[1].Form:D}');") == "4", 300, "searchable coverages not Complete");
        await WaitAsync(() => Scalar($"SELECT content_head_quarantined::text FROM record_content_heads WHERE organization_id = '{org:D}' AND form_record_id = '{forged:D}';") == "true", 120, "the forged record was not quarantined");

        // Teams (overlapping: the first five users belong to all three).
        var teams = new List<(Guid Team, HashSet<Guid> Members)>();
        foreach (var size in new[] { 5, 10, 20 }.Where(n => n <= userCount))
        {
            var team = await fixture.Api.PostAsync("/api/v1/teams", s.Token(s.Designer), new { teamName = $"P-4 team of {size} {s.Name}" });
            Assert.Equal(HttpStatusCode.Created, team.Status);
            var teamId = team.Root.GetProperty("teamId").GetGuid();
            foreach (var user in users.Take(size))
            {
                await RetryAsync(() => fixture.Api.PostAsync($"/api/v1/teams/{teamId:D}/members", s.Token(s.Designer), new { userId = user.AuthorityUserId }, "p4-add-" + Guid.NewGuid().ToString("N")), _ => true, "add member");
            }

            teams.Add((teamId, users.Take(size).Select(u => u.AuthorityUserId).ToHashSet()));
        }

        await s.WaitGateReadyAsync(TimeSpan.FromSeconds(120));
        var shares = new List<Share>();
        string[][] subsets = [["exhibit_count"], ["case_status"], ["exhibit_count", "case_status"]];
        Guid HonestRecord() { var form = forms[random.Next(forms.Length)]; var list = records[form.Form]; return list[random.Next(form == forms[2] ? 1 : 0, list.Count)]; }

        async Task<Share> ShareAsync(Guid record, Guid? user, Guid? team, string[] fields)
        {
            var id = await RetryAsync(() => fixture.Api.PostAsync($"/api/v1/form-records/{record:D}/shares", s.Token(s.Clerk), new { useDevelopmentStepUp = true, recipientUserId = user, recipientTeamId = team, recipientRole = "Reader", fieldKeys = fields }), r => r.Root.GetProperty("recordShareId").GetGuid(), "share");
            var share = new Share(record, id, user, team, fields);
            shares.Add(share);
            return share;
        }

        // Overlapping grants before the window: the first user reaches one record directly and through two teams.
        var overlap = records[forms[0].Form][0];
        await ShareAsync(overlap, users[0].AuthorityUserId, null, ["exhibit_count"]);
        await ShareAsync(overlap, null, teams[0].Team, ["exhibit_count", "case_status"]);
        if (teams.Count > 1)
        {
            await ShareAsync(overlap, null, teams[1].Team, ["case_status"]);
        }

        // Indexed searches (the Clerk, with a Testing session digest) every 2 s during the window.
        var formA = forms[0];
        Assert.Equal(HttpStatusCode.NoContent, (await fixture.Api.SendAsync(HttpMethod.Put, $"/api/v1/form-definitions/{formA.Form:D}/field-searchers", s.Token(s.Administrator), new { grantUserIds = new[] { s.Clerk.AuthorityUserId }, grantTeamIds = Array.Empty<Guid>(), revokeUserIds = Array.Empty<Guid>(), revokeTeamIds = Array.Empty<Guid>() }, Guid.NewGuid().ToString("N"))).Status);
        var digest = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        Assert.True((await fixture.Authority.SendAsync(HttpMethod.Post, "/internal/v1/sessions", TestIdentity.WorkloadToken(), new { organization_id = org, session_digest_base64 = digest, absolute_expires_on = DateTimeOffset.UtcNow.AddHours(2) }, s.Token(s.Clerk))).IsSuccess);
        using var http = new HttpClient { BaseAddress = new Uri(StageBFixture.ApiBaseAddress), Timeout = TimeSpan.FromMinutes(2) };
        var searches = new ConcurrentBag<(double Ms, int Status, string? Code)>();

        var counting = (await fixture.Authority.SendAsync(HttpMethod.Post, "/internal/v1/testing/spicedb-calls/reset", TestIdentity.WorkloadToken(), new { })).IsSuccess;
        var latencies = new Dictionary<string, List<double>>();
        var tier2Lag = new List<double>();
        var tier2LagTimeouts = 0;
        var backlog = new List<(double Pending, double OldestPendingSeconds)>();
        var start = DateTimeOffset.UtcNow;
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var searcher = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/form-records/field-search")
                {
                    Content = JsonContent.Create(new { formDefinitionId = formA.Form, conditions = new[] { new { fieldDefinitionId = formA.Count, @operator = "between", valueType = "Integer", lowerBound = "0", upperBound = "500" } }, pageSize = 25 }),
                };
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", s.Token(s.Clerk));
                request.Headers.Add("X-Testing-Session-Digest", digest);
                var clock = Stopwatch.StartNew();
                try
                {
                    using var response = await http.SendAsync(request, CancellationToken.None);
                    var body = await response.Content.ReadAsStringAsync(CancellationToken.None);
                    using var doc = JsonDocument.Parse(body.Length == 0 ? "{}" : body);
                    searches.Add((clock.Elapsed.TotalMilliseconds, (int)response.StatusCode, doc.RootElement.TryGetProperty("title", out var t) ? t.GetString() : null));
                }
                catch (HttpRequestException ex)
                {
                    searches.Add((clock.Elapsed.TotalMilliseconds, 0, ex.GetType().Name));
                }

                try
                {
                    await Task.Delay(2000, stop.Token);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }, CancellationToken.None);

        var window = Stopwatch.StartNew();
        var issued = 0;
        try
        {
            while (window.Elapsed < TimeSpan.FromSeconds(seconds))
            {
                var due = TimeSpan.FromSeconds(issued) - window.Elapsed;
                if (due > TimeSpan.Zero)
                {
                    await Task.Delay(due, Ct);
                }

                issued++;
                var roll = random.Next(100);
                var team = teams[random.Next(teams.Count)];
                string kind;
                var clock = Stopwatch.StartNew();
                if (roll < 30)
                {
                    kind = "direct_share";
                    await ShareAsync(HonestRecord(), users[random.Next(users.Count)].AuthorityUserId, null, subsets[random.Next(subsets.Length)]);
                }
                else if (roll < 50)
                {
                    kind = "team_share";
                    await ShareAsync(HonestRecord(), null, team.Team, subsets[random.Next(subsets.Length)]);
                }
                else if (roll < 75 && shares.Count > 0)
                {
                    kind = "revoke";
                    var share = shares[random.Next(shares.Count)];
                    await RetryAsync(() => fixture.Api.PostAsync($"/api/v1/form-records/{share.Record:D}/shares/{share.ShareId:D}/revoke?useDevelopmentStepUp=true", s.Token(s.Clerk), new { }), _ => true, "revoke");
                    shares.Remove(share);
                }
                else if (roll < 88 || team.Members.Count <= 1)
                {
                    var candidates = users.Where(u => !team.Members.Contains(u.AuthorityUserId)).ToList();
                    if (candidates.Count == 0)
                    {
                        continue;
                    }

                    kind = "member_add";
                    var user = candidates[random.Next(candidates.Count)];
                    await RetryAsync(() => fixture.Api.PostAsync($"/api/v1/teams/{team.Team:D}/members", s.Token(s.Designer), new { userId = user.AuthorityUserId }, "p4-add-" + Guid.NewGuid().ToString("N")), _ => true, "add member");
                    team.Members.Add(user.AuthorityUserId);
                }
                else
                {
                    kind = "member_remove";
                    var member = team.Members.ElementAt(random.Next(team.Members.Count));
                    await RetryAsync(() => fixture.Api.PostAsync($"/api/v1/teams/{team.Team:D}/members/{member:D}/remove", s.Token(s.Designer), new { }, "p4-remove-" + Guid.NewGuid().ToString("N")), _ => true, "remove member");
                    team.Members.Remove(member);
                }

                (latencies.TryGetValue(kind, out var list) ? list : latencies[kind] = []).Add(clock.Elapsed.TotalMilliseconds);

                // Tier 2 lag: until the loop (no catch-up call) has consumed the command and the projection is Ready again.
                var lag = Stopwatch.StartNew();
                while (true)
                {
                    var status = await StatusAsync(org);
                    if (status.GetProperty("status_name").GetString() == "Ready" && status.GetProperty("consumed_authorization_revision").GetInt64() >= status.GetProperty("current_authorization_revision").GetInt64())
                    {
                        tier2Lag.Add(lag.Elapsed.TotalMilliseconds);
                        break;
                    }

                    Assert.NotEqual("Quarantined", status.GetProperty("status_name").GetString());
                    if (lag.Elapsed > TimeSpan.FromSeconds(20))
                    {
                        tier2LagTimeouts++;
                        break;
                    }

                    await Task.Delay(50, Ct);
                }

                if (issued % 10 == 0)
                {
                    var row = Scalar($"SELECT count(*) || '|' || coalesce(extract(epoch FROM now() - min(journal_created_on)), 0) FROM security_journal_entries WHERE organization_id = '{org:D}' AND archive_delivery_state = 'Pending';").Split('|');
                    backlog.Add((double.Parse(row[0], CultureInfo.InvariantCulture), double.Parse(row[1], CultureInfo.InvariantCulture)));
                }
            }
        }
        finally
        {
            await stop.CancelAsync();
            await searcher;
        }

        var end = DateTimeOffset.UtcNow;
        var calls = counting ? (await fixture.Authority.SendAsync(HttpMethod.Get, "/internal/v1/testing/spicedb-calls", TestIdentity.WorkloadToken(), null)).Body : null;
        var minutes = (end - start).TotalMinutes;
        var journal = fixture.Stack.AuthorityObserverSql($"SELECT kind || '|' || n FROM (SELECT journal_stream_name || ':' || journal_event_kind AS kind, count(*) AS n FROM security_journal_entries WHERE organization_id = '{org:D}' AND journal_created_on BETWEEN '{start:O}' AND '{end:O}' GROUP BY 1) q ORDER BY 1;")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(l => l.Split('|')).Where(p => p.Length == 2)
            .ToDictionary(p => p[0], p => Math.Round(double.Parse(p[1], CultureInfo.InvariantCulture) / minutes, 1));

        // Correctness: canonical membership has settled to the model; Tier 2 is Ready, verified and journal-complete.
        await s.WaitGateReadyAsync(TimeSpan.FromSeconds(120));
        HashSet<(Guid Team, Guid User)> Canonical() => fixture.Stack.AuthorityObserverSql($"SELECT resource_entity_id || '|' || subject_entity_id FROM authority_relationship_edges WHERE organization_id = '{org:D}' AND resource_type_name = 'team' AND relationship_name = 'member' AND subject_type_name = 'user' AND removed_by_security_command_id IS NULL;")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(l => l.Split('|')).Where(p => p.Length == 2).Select(p => (Guid.Parse(p[0]), Guid.Parse(p[1]))).ToHashSet();
        var model = teams.SelectMany(t => t.Members.Select(m => (t.Team, m))).ToHashSet();
        await WaitAsync(() => Canonical().SetEquals(model), 120, "the Authority's team membership did not settle to the model");
        JsonElement verified = default;
        for (var attempt = 0; attempt < 60; attempt++)
        {
            var catchUp = await fixture.Authority.SendAsync(HttpMethod.Post, Prefix + $"/{org:D}/catch-up", TestIdentity.WorkloadToken(), new { maximum_events = 5000 });
            Assert.True(catchUp.IsSuccess, catchUp.Body);
            var result = await fixture.Authority.SendAsync(HttpMethod.Post, Prefix + $"/{org:D}/verify", TestIdentity.WorkloadToken(), new { });
            Assert.True(result.IsSuccess, result.Body);
            verified = result.Root.Clone();
            if (verified.GetProperty("outcome_code").GetString() == "CLEAN")
            {
                break;
            }

            await Task.Delay(500, Ct);
        }

        var replay = await fixture.Authority.SendAsync(HttpMethod.Get, Prefix + $"/{org:D}/journal-replay", TestIdentity.WorkloadToken(), null);
        var mismatches = new List<string>();
        var evaluations = 0;
        foreach (var form in forms.Take(2))
        {
            foreach (var user in users)
            {
                var teamsOfUser = teams.Where(t => t.Members.Contains(user.AuthorityUserId)).Select(t => t.Team).ToHashSet();
                var expected = shares.Where(sh => records[form.Form].Contains(sh.Record) && sh.Fields.Contains("exhibit_count") && (sh.User == user.AuthorityUserId || (sh.Team is { } t && teamsOfUser.Contains(t))))
                    .Select(sh => sh.Record).ToHashSet();
                var evaluated = await fixture.Authority.SendAsync(HttpMethod.Post, Prefix + $"/{org:D}/evaluate", TestIdentity.WorkloadToken(), new { form_definition_id = form.Form, authority_user_id = user.AuthorityUserId, conditions = new[] { new { field_definition_id = form.Count, value_type_name = "Integer", minimum_integer = 0L, maximum_integer = 1_000_000L } } });
                Assert.True(evaluated.IsSuccess, evaluated.Body);
                evaluations++;
                var actual = evaluated.Root.GetProperty("form_record_ids").EnumerateArray().Select(e => e.GetGuid()).ToHashSet();
                if (!actual.SetEquals(expected))
                {
                    mismatches.Add($"{user.DisplayName} form {form.Form:D}: expected {expected.Count}, evaluated {actual.Count}, missing {expected.Except(actual).Count()}, extra {actual.Except(expected).Count()}");
                }
            }
        }

        var blockedRows = fixture.Stack.AuthorityAdministratorSql($"SELECT count(*) FROM authority_search_access.search_access_value_postings WHERE organization_id = '{org:D}' AND form_definition_id = '{forms[2].Form:D}';").Trim().Split('\n')[0].Trim();
        var blockedCoverages = Scalar($"SELECT string_agg(coverage_state_name, ',' ORDER BY coverage_state_name) FROM authority_search.search_field_coverages WHERE organization_id = '{org:D}' AND form_definition_id = '{forms[2].Form:D}';");
        var report = new
        {
            kind = "C-149 P-4 bounded sharing workload (focused, local Docker, synthetic; indicative only, not staging)",
            measured_on = DateTimeOffset.UtcNow,
            git_commit = ComposeStack.GitCommit(),
            stack = fixture.Stack.ImageFingerprint(),
            parameters = new { users = userCount, teams = teams.Select(t => t.Members.Count), records_per_form = perForm, forms = 3, blocked_forms = 1, window_seconds = seconds, command_rate_per_second = 1, search_interval_seconds = 2 },
            seeding = new { records = forms.Length * perForm, seconds = Math.Round(seedSeconds, 1) },
            commands_issued = issued,
            command_latency_ms = latencies.OrderBy(l => l.Key, StringComparer.Ordinal).ToDictionary(l => l.Key, l => Stats(l.Value)),
            tier2_ready_after_command_ms = Stats(tier2Lag),
            tier2_lag_timeouts_over_20_seconds = tier2LagTimeouts,
            journal_events_per_minute_by_kind = journal,
            archive_pending = new { samples = backlog.Count, max_pending = backlog.Count == 0 ? 0 : backlog.Max(b => b.Pending), max_oldest_pending_seconds = backlog.Count == 0 ? 0 : Math.Round(backlog.Max(b => b.OldestPendingSeconds), 1) },
            searches = new { outcomes = searches.GroupBy(x => x.Status == 200 ? "200" : $"{x.Status} {x.Code}").ToDictionary(g => g.Key, g => g.Count()), answered_ms = Stats(searches.Where(x => x.Status == 200).Select(x => x.Ms)) },
            spicedb_calls = calls is null ? (JsonElement?)null : JsonDocument.Parse(calls).RootElement.Clone(),
            correctness = new
            {
                verification = verified.ValueKind == JsonValueKind.Undefined ? null : verified.GetProperty("outcome_code").GetString(),
                journal_replay = replay.IsSuccess ? replay.Root.GetProperty("outcome_code").GetString() : replay.Body,
                active_shares = shares.Count,
                evaluations,
                evaluation_mismatches = mismatches,
                blocked_form_value_rows = blockedRows,
                blocked_form_coverages = blockedCoverages,
            },
        };
        var directory = Path.Combine(ComposeStack.RepositoryRoot, "artifacts", "ix-5");
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(Path.Combine(directory, "p4-sharing-workload.json"), JsonSerializer.Serialize(report, Indented), Ct);

        Assert.Equal("CLEAN", verified.GetProperty("outcome_code").GetString());
        Assert.Equal("COMPLETE", replay.Root.GetProperty("outcome_code").GetString());
        Assert.True(mismatches.Count == 0, string.Join("; ", mismatches));
        Assert.Equal("0", blockedRows);
        Assert.DoesNotContain("Complete", blockedCoverages, StringComparison.Ordinal);
        Assert.Equal(0, tier2LagTimeouts);
    }
}
