using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using EnterpriseForms.SecurityTests.Harness;
using EnterpriseForms.StageBTests.Harness;
using Xunit;

namespace EnterpriseForms.StageBTests.Tests;

/// <summary>
/// C-140 UF-25 reproduction (investigation only; Tier 1 behaviour is unchanged by this class). Tier 1's readable-for-predicate
/// scope (design R1 §E.3) bulk-checks <c>field_item#read</c> on <b>every</b> posting item of each predicate field of the caller's
/// share-only records, including items the caller cannot read, and applies the <c>items</c> limit
/// (<c>SearchIndex:Query:MaximumShareOnlyItems</c>) to that count. These tests establish, on the real security-test stack, whether
/// content the caller is not authorized to read changes observable search behaviour: the refusal (deterministic, with a small
/// limit set only for this test), the work and latency (informational; Benchmark trait), and the results (which must not change).
/// Synthetic data only. A focused development test class (C-131), not a Stage B group run.
/// </summary>
[Collection(StageBStackCollectionDefinition.Name)]
public sealed class IndexedSearchShareOnlyItemChannelTests(StageBFixture fixture)
{
    private const string LimitVariable = "IX_SHARE_ONLY_ITEM_LIMIT";
    private const int SmallLimit = 2;

    private sealed record Ws(StageBScenario S, string Path, Guid FormId, Guid? TeamId, Guid Count, Guid Status, Guid Open, Guid Tags, IReadOnlyDictionary<string, Guid> TagOptions);

    private sealed record Session(string Digest, TestUser User);

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string Scalar(string sql) => fixture.Stack.AuthorityObserverSql(sql).Trim().Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim() ?? string.Empty;

    // ---- workspace ---------------------------------------------------------------------------------------------------------

    /// <param name="path">direct: shares to the Reader; team: shares to a team of which the Reader is a member; overlapping: both.</param>
    private async Task<Ws> WorkspaceAsync(string path, int tagOptionCount = 3)
    {
        var s = new StageBScenario(fixture, "uf25-" + path);
        await s.BootstrapAsync();
        var current = await fixture.Api.GetAsync("/api/v1/organization/security-policy", s.Token(s.Designer));
        var policy = await fixture.Api.SendAsync(HttpMethod.Put, "/api/v1/organization/security-policy", s.Token(s.Designer), new { minimumFieldClassification = "Internal", plaintextInternalFieldsPermitted = true, plaintextDowngradePermitted = false, plaintextSearchPermitted = true, policyStatementText = "UF-25 synthetic organization.", expectedPolicyRevision = current.Root.GetProperty("securityPolicyRevision").GetInt64() }, Guid.NewGuid().ToString("N"));
        Assert.True(policy.IsSuccess, policy.Body);
        var formId = await s.CreateFormAsync("UF-25 " + s.Name, s.Clerk);

        async Task<JsonElement> FieldAsync(string key, string label, string type, object? options = null, string? mode = null)
        {
            var added = await fixture.Api.PostAsync($"/api/v1/form-definitions/{formId:D}/fields", s.Token(s.Designer), new { fieldKey = key, fieldDisplayName = label, fieldDescription = string.Empty, fieldDataType = type, options, fieldClassification = "Internal", fieldProtectionMode = "Standard", optionSelectionMode = mode });
            Assert.True(added.Status == HttpStatusCode.Created, $"add {key}: " + added.Body);
            return added.Root.Clone();
        }

        static Dictionary<string, Guid> OptionIds(JsonElement field) => field.GetProperty("options").EnumerateArray().ToDictionary(o => o.GetProperty("optionKey").GetString()!, o => o.GetProperty("fieldOptionId").GetGuid());

        var count = (await FieldAsync("exhibit_count", "Exhibit count", "Integer")).GetProperty("fieldDefinitionId").GetGuid();
        var status = await FieldAsync("case_status", "Case status", "Option", new[] { new { optionKey = "open", optionDisplayText = "Open" }, new { optionKey = "closed", optionDisplayText = "Closed" } }, "Single");
        var tagKeys = Enumerable.Range(1, tagOptionCount).Select(i => "t" + i.ToString("D2", CultureInfo.InvariantCulture)).ToArray();
        var tags = await FieldAsync("topic_tags", "Topics", "Option", tagKeys.Select(k => new { optionKey = k, optionDisplayText = "Topic " + k }).ToArray(), "Multiple");
        var statusId = status.GetProperty("fieldDefinitionId").GetGuid();
        var tagsId = tags.GetProperty("fieldDefinitionId").GetGuid();
        var statusOptions = OptionIds(status);
        var tagOptions = OptionIds(tags);
        string Snapshot(IReadOnlyDictionary<string, Guid> options) => string.Join(",", options.Select((o, i) => $$"""{"fieldOptionId":"{{o.Value:D}}","label":"{{o.Key}}","sortOrder":{{i + 1}}}"""));
        await s.SaveDraftAsync(formId, StageBScenario.Layout("UF-25", StageBScenario.Section("main", "Main",
            StageBScenario.Field(count, "number", "Exhibit count", 2),
            $$"""{"kind":"field","fieldDefinitionId":"{{statusId:D}}","control":"select","label":"Case status","layout":{"columnSpan":2,"startOnNewRow":false},"optionsSnapshot":[{{Snapshot(statusOptions)}}]}""",
            $$"""{"kind":"field","fieldDefinitionId":"{{tagsId:D}}","control":"multiselect","label":"Topics","layout":{"columnSpan":4,"startOnNewRow":true},"optionsSnapshot":[{{Snapshot(tagOptions)}}]}""")));
        Assert.True((await s.PublishAsync(formId, "v1")).GetProperty("authorityRegistered").GetBoolean());
        foreach (var (field, ops) in new[] { (count, new[] { "equals" }), (statusId, new[] { "is", "includes" }), (tagsId, new[] { "is", "includes" }) })
        {
            var approved = await fixture.Api.PostAsync($"/api/v1/form-definitions/{formId:D}/fields/{field:D}/search-approval", s.Token(s.Administrator), new { operators = ops, approvalReference = "UF-25", useDevelopmentStepUp = true });
            Assert.True(approved.IsSuccess, approved.Body);
        }

        var granted = await fixture.Api.SendAsync(HttpMethod.Put, $"/api/v1/form-definitions/{formId:D}/field-searchers", s.Token(s.Administrator), new { grantUserIds = new[] { s.Clerk.AuthorityUserId, s.Reader.AuthorityUserId }, grantTeamIds = Array.Empty<Guid>(), revokeUserIds = Array.Empty<Guid>(), revokeTeamIds = Array.Empty<Guid>() }, Guid.NewGuid().ToString("N"));
        Assert.True(granted.Status == HttpStatusCode.NoContent, granted.Body);

        Guid? teamId = null;
        if (path is "team" or "overlapping")
        {
            var team = await fixture.Api.PostAsync("/api/v1/teams", s.Token(s.Designer), new { teamName = "UF-25 readers " + s.Name });
            Assert.Equal(HttpStatusCode.Created, team.Status);
            teamId = team.Root.GetProperty("teamId").GetGuid();
            var added = await fixture.Api.PostAsync($"/api/v1/teams/{teamId:D}/members", s.Token(s.Designer), new { userId = s.Reader.AuthorityUserId }, "uf25-add-" + Guid.NewGuid().ToString("N"));
            Assert.True(added.IsSuccess, added.Body);
            await s.WaitGateReadyAsync();
        }

        return new Ws(s, path, formId, teamId, count, statusId, statusOptions["open"], tagsId, tagOptions);
    }

    private async Task<Guid> CreateAsync(Ws w, Dictionary<string, string> values)
    {
        var key = "uf25-" + Guid.CreateVersion7().ToString("N");
        for (var attempt = 0; ; attempt++)
        {
            var created = await fixture.Api.PostAsync($"/api/v1/form-definitions/{w.FormId:D}/records", w.S.Token(w.S.Clerk), new { values }, key);
            if (created.Status is HttpStatusCode.Created or HttpStatusCode.OK)
            {
                var id = created.Root.GetProperty("formRecordId").GetGuid();
                Assert.True((await w.S.WaitProjectionAsync(w.S.Clerk, id)).IsSuccess);
                return id;
            }

            Assert.True(attempt < 60 && created.ErrorCode is "AUTHORITY_NOT_READY" or "STALE_AUTHORIZATION_REVISION" or "COMMAND_PENDING", "create failed: " + created.Body);
            await Task.Delay(400, Ct);
        }
    }

    private async Task EditAsync(Ws w, Guid record, Dictionary<string, string> values)
    {
        for (var attempt = 0; ; attempt++)
        {
            var head = AuthorityOracle.Head(fixture.Stack, record);
            var edit = await fixture.Api.PatchAsync($"/api/v1/form-records/{record:D}", w.S.Token(w.S.Clerk), new { expectedContentRevision = head.ContentRevision, values });
            if (edit.IsSuccess)
            {
                return;
            }

            Assert.True(attempt < 30 && edit.ErrorCode is "AUTHORITY_NOT_READY" or "COMMAND_PENDING" or "CONTENT_REVISION_CONFLICT" or "STALE_CONTENT_REVISION", "edit failed: " + edit.Body);
            await Task.Delay(300, Ct);
        }
    }

    /// <summary>Shares the listed fields of the record with the Reader along the workspace's path (overlapping: a direct and a team share).</summary>
    private async Task ShareAsync(Ws w, Guid record, params string[] fieldKeys)
    {
        var recipients = w.Path switch
        {
            "direct" => new (Guid? User, Guid? Team)[] { (w.S.Reader.AuthorityUserId, null) },
            "team" => new (Guid? User, Guid? Team)[] { (null, w.TeamId) },
            _ => new (Guid? User, Guid? Team)[] { (w.S.Reader.AuthorityUserId, null), (null, w.TeamId) },
        };
        foreach (var (user, team) in recipients)
        {
            for (var attempt = 0; ; attempt++)
            {
                var share = await fixture.Api.PostAsync($"/api/v1/form-records/{record:D}/shares", w.S.Token(w.S.Clerk), new { recipientUserId = user, recipientTeamId = team, recipientRole = "Reader", fieldKeys });
                if (share.IsSuccess)
                {
                    break;
                }

                Assert.True(attempt < 30 && share.ErrorCode is "AUTHORITY_NOT_READY" or "COMMAND_PENDING" or "STALE_AUTHORIZATION_REVISION", "share failed: " + share.Body);
                await Task.Delay(300, Ct);
            }
        }

        Assert.True((await w.S.WaitProjectionAsync(w.S.Reader, record, TimeSpan.FromSeconds(40))).IsSuccess);
    }

    private async Task WaitCompleteAsync(Ws w, int coverages = 3)
    {
        var deadline = DateTime.UtcNow.AddSeconds(300);
        string last;
        do
        {
            last = Scalar($"SELECT count(*) FILTER (WHERE coverage_state_name = 'Complete') || '/' || count(*) FROM authority_search.search_field_coverages WHERE organization_id = '{w.S.OrganizationId:D}' AND coverage_state_name <> 'Withdrawn';");
            if (last == $"{coverages}/{coverages}")
            {
                return;
            }

            await Task.Delay(1000, Ct);
        }
        while (DateTime.UtcNow < deadline);
        Assert.Fail("coverages not Complete: " + last);
    }

    /// <summary>The posting items of a field over the given records at their indexed operations: exactly the set Tier 1 checks for share-only records.</summary>
    private int PostingItems(Ws w, Guid field, IEnumerable<Guid> records) => int.Parse(Scalar(
        $"SELECT count(*) FROM authority_search.search_option_postings p JOIN authority_search.search_record_entries e ON e.organization_id = p.organization_id AND e.form_record_id = p.form_record_id AND e.indexed_operation_id = p.indexed_operation_id " +
        $"WHERE p.organization_id = '{w.S.OrganizationId:D}' AND p.field_definition_id = '{field:D}' AND p.form_record_id IN ({string.Join(",", records.Select(r => $"'{r:D}'"))});"), CultureInfo.InvariantCulture);

    private async Task WaitPostingItemsAsync(Ws w, Guid field, IReadOnlyCollection<Guid> records, int expected)
    {
        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (PostingItems(w, field, records) != expected)
        {
            Assert.True(DateTime.UtcNow < deadline, $"postings not indexed: {PostingItems(w, field, records)} of {expected}");
            await Task.Delay(500, Ct);
        }
    }

    /// <summary>The Reader's authorized projection of the record lists no value of the field (the content is not authorized to the Reader).</summary>
    private static async Task AssertReaderCannotReadAsync(Ws w, Guid record, string fieldKey)
    {
        var projection = await w.S.WaitProjectionAsync(w.S.Reader, record, TimeSpan.FromSeconds(40));
        Assert.True(projection.IsSuccess, projection.Body);
        Assert.DoesNotContain(projection.Root.GetProperty("record").GetProperty("values").EnumerateArray(), v => v.GetProperty("fieldKey").GetString() == fieldKey);
    }

    // ---- Authority calls -----------------------------------------------------------------------------------------------------

    private async Task<Session> SessionAsync(Ws w, TestUser user)
    {
        var digest = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var created = await fixture.Authority.SendAsync(HttpMethod.Post, "/internal/v1/sessions", TestIdentity.WorkloadToken(), new { organization_id = w.S.OrganizationId, session_digest_base64 = digest, absolute_expires_on = DateTimeOffset.UtcNow.AddHours(1) }, w.S.Token(user));
        Assert.True(created.IsSuccess, created.Body);
        return new Session(digest, user);
    }

    private static object Cond(Guid field, string op, Guid option) => new { field_definition_id = field, @operator = op, value_type = "Option", value = option.ToString("D"), lower_bound = (string?)null, upper_bound = (string?)null };

    private static object Query(Ws w, object condition) =>
        new { form_definition_id = w.FormId, conditions = new[] { condition }, record_status = (string?)null, include_children = false, include_archived = false, sort_by = (string?)null, sort_direction = (string?)null, page_size = 50 };

    private async Task<HttpResult> IndexedAsync(Ws w, Session session, object query)
    {
        for (var attempt = 0; ; attempt++)
        {
            var nonce = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
            var result = await fixture.Authority.SendAsync(HttpMethod.Post, "/internal/v1/search/indexed-query", TestIdentity.WorkloadToken(),
                new { organization_id = w.S.OrganizationId, session_digest_base64 = session.Digest, query_nonce_base64 = nonce, query }, w.S.Token(session.User));
            if (result.IsSuccess || attempt >= 30 || result.ErrorCode is not ("AUTHORITY_NOT_READY" or "SEARCH_SNAPSHOT_CHANGED"))
            {
                return result;
            }

            await Task.Delay(400, Ct);
        }
    }

    private static List<Guid> Ids(HttpResult result)
    {
        Assert.True(result.IsSuccess, result.Body);
        return result.Root.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("form_record_id").GetGuid()).Order().ToList();
    }

    private static void AssertItemsRefusal(HttpResult result)
    {
        Assert.False(result.IsSuccess, result.Body);
        Assert.Equal("SEARCH_VERIFICATION_LIMIT", result.ErrorCode);
        Assert.Contains("items", result.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("form_record_id", result.Body, StringComparison.Ordinal);
    }

    private void RecreateAuthority(int? limit)
    {
        Environment.SetEnvironmentVariable(LimitVariable, limit?.ToString(CultureInfo.InvariantCulture));
        fixture.Stack.Compose("up -d --no-deps --force-recreate --wait authority", 300);
    }

    // ---- UF-25: deterministic reproduction ------------------------------------------------------------------------------------

    private sealed record Case(Ws W, Guid Control, Guid[] ShareOnly, Guid PartialTags);

    /// <summary>
    /// With an items limit of 2, the Reader's search on <c>case_status</c> succeeds while the share-only records hold no status value,
    /// and is refused (<c>SEARCH_VERIFICATION_LIMIT</c>, <c>items</c>) once the owner writes a status the Reader is not authorized to
    /// read, although nothing the Reader is authorized for changed. With the default limit the results are identical before and
    /// after. Reproduced for a direct share, a team share and overlapping direct and team shares. Also records the result-level
    /// effect of the parity rule (a new unshared selection removes a record from the Reader's results), which a fix of the
    /// work/limit channel must preserve unless the owner changes the search semantics.
    /// </summary>
    [Fact]
    public async Task UF25UnauthorizedItemsChangeTheItemsRefusalButNotTheResults()
    {
        var cases = new List<Case>();
        var evidence = new List<object>();
        try
        {
            RecreateAuthority(SmallLimit);
            foreach (var path in new[] { "direct", "team", "overlapping" })
            {
                var w = await WorkspaceAsync(path);
                // Control: readable status and tags. Share-only records: only exhibit_count shared; no status value yet. Partial: tags shared.
                var control = await CreateAsync(w, new(StringComparer.Ordinal) { ["exhibit_count"] = "1", ["case_status"] = "open", ["topic_tags"] = "t01" });
                var shareOnly = new Guid[3];
                for (var i = 0; i < shareOnly.Length; i++)
                {
                    shareOnly[i] = await CreateAsync(w, new(StringComparer.Ordinal) { ["exhibit_count"] = (10 + i).ToString(CultureInfo.InvariantCulture) });
                }

                var partial = await CreateAsync(w, new(StringComparer.Ordinal) { ["exhibit_count"] = "20", ["topic_tags"] = "t01" });
                await ShareAsync(w, control, "exhibit_count", "case_status", "topic_tags");
                foreach (var record in shareOnly)
                {
                    await ShareAsync(w, record, "exhibit_count");
                }

                await ShareAsync(w, partial, "exhibit_count", "topic_tags");
                await WaitCompleteAsync(w);
                var reader = await SessionAsync(w, w.S.Reader);
                var statusOpen = Query(w, Cond(w.Status, "is", w.Open));

                // Before: one status item in the Reader's share-only scope (the control's), within the limit of 2.
                var before = await IndexedAsync(w, reader, statusOpen);
                Assert.Equal([control], Ids(before));
                var itemsBefore = PostingItems(w, w.Status, [control, partial, .. shareOnly]);
                Assert.Equal(1, itemsBefore);
                // Before the owner adds an unshared selection, the partially shared record matches on its readable selection t01.
                var tagsT01 = Query(w, Cond(w.Tags, "includes", w.TagOptions["t01"]));
                Assert.Equal(new[] { control, partial }.Order().ToList(), Ids(await IndexedAsync(w, reader, tagsT01)));

                // The owner writes "open" into the share-only records' status: content the Reader is not authorized to read.
                foreach (var record in shareOnly)
                {
                    await EditAsync(w, record, new(StringComparer.Ordinal) { ["case_status"] = "open" });
                }

                await WaitPostingItemsAsync(w, w.Status, [control, partial, .. shareOnly], 1 + shareOnly.Length);
                foreach (var record in shareOnly)
                {
                    await AssertReaderCannotReadAsync(w, record, "case_status");
                }

                // After: the same query by the same caller, with unchanged authorization, is refused on the items limit.
                var after = await IndexedAsync(w, reader, statusOpen);
                AssertItemsRefusal(after);
                // The owner adds a selection to the partially shared multi-select (a new item is never shared automatically, §4H).
                await EditAsync(w, partial, new(StringComparer.Ordinal) { ["topic_tags"] = "t01,t02" });
                await WaitPostingItemsAsync(w, w.Tags, [partial], 2);
                await AssertReaderCannotReadAsync(w, shareOnly[0], "case_status");
                cases.Add(new Case(w, control, shareOnly, partial));
                evidence.Add(new
                {
                    path,
                    items_limit = SmallLimit,
                    status_items_in_reader_scope_before = itemsBefore,
                    status_items_in_reader_scope_after = PostingItems(w, w.Status, [control, partial, .. shareOnly]),
                    readable_status_items_for_reader = 1,
                    before = new { http = (int)before.Status, results = Ids(before).Count },
                    after = new { http = (int)after.Status, error_code = after.ErrorCode },
                });
            }
        }
        finally
        {
            RecreateAuthority(null);
        }

        // Default limit: results do not depend on the unauthorized status values; the parity rule's result-level effect is recorded.
        var semantics = new List<object>();
        foreach (var c in cases)
        {
            var reader = await SessionAsync(c.W, c.W.S.Reader);
            Assert.Equal([c.Control], Ids(await IndexedAsync(c.W, reader, Query(c.W, Cond(c.W.Status, "is", c.W.Open)))));
            var tagged = Ids(await IndexedAsync(c.W, reader, Query(c.W, Cond(c.W.Tags, "includes", c.W.TagOptions["t01"]))));
            // The Reader can still read the partial record's shared selection t01, but the record is no longer evaluable for topic_tags
            // because a selection the Reader cannot read now exists (R1 §E.3 parity, unchanged Tier 1 semantics).
            Assert.Equal([c.Control], tagged);
            semantics.Add(new { path = c.W.Path, status_is_open = "control only (unchanged by unauthorized values)", tags_includes_t01_after_unshared_selection_added = "control only (partial record excluded by parity)" });
        }

        await WriteAsync("uf25-reproduction.json", new Dictionary<string, object?>
        {
            ["kind"] = "C-140 UF-25 reproduction: Tier 1 share-only items limit and unauthorized content (focused, local Docker, synthetic)",
            ["measured_on"] = DateTimeOffset.UtcNow,
            ["git_commit"] = ComposeStack.GitCommit(),
            ["cases"] = evidence,
            ["default_limit_results"] = semantics,
        });
    }

    // ---- UF-25: work and latency (informational) --------------------------------------------------------------------------------

    /// <summary>
    /// Informational measurement, no timing assertion: the Reader's search latency on <c>topic_tags</c> with no unreadable selections in
    /// the share-only scope, then after the owner writes 40 selections into each of 25 share-only records (1,000 posting items the
    /// Reader cannot read, each of which Tier 1 bulk-checks). Results must be identical; the latency difference is reported.
    /// </summary>
    [Fact]
    [Trait("Category", "Benchmark")]
    public async Task UF25UnauthorizedItemCountChangesTheShareOnlyCheckWork()
    {
        const int records = 25;
        const int selections = 40;
        const int searches = 21;
        var w = await WorkspaceAsync("direct", tagOptionCount: selections);
        var control = await CreateAsync(w, new(StringComparer.Ordinal) { ["exhibit_count"] = "1", ["topic_tags"] = "t01" });
        await ShareAsync(w, control, "exhibit_count", "topic_tags");
        var shareOnly = new List<Guid>();
        for (var i = 0; i < records; i++)
        {
            var record = await CreateAsync(w, new(StringComparer.Ordinal) { ["exhibit_count"] = (100 + i).ToString(CultureInfo.InvariantCulture) });
            await ShareAsync(w, record, "exhibit_count");
            shareOnly.Add(record);
        }

        await WaitCompleteAsync(w);
        var reader = await SessionAsync(w, w.S.Reader);
        var query = Query(w, Cond(w.Tags, "includes", w.TagOptions["t01"]));

        async Task<(List<double> Ms, List<Guid> Ids)> MeasureAsync()
        {
            await IndexedAsync(w, reader, query); // warm-up, not counted
            var times = new List<double>();
            List<Guid>? ids = null;
            for (var n = 0; n < searches; n++)
            {
                var clock = Stopwatch.StartNew();
                var result = await IndexedAsync(w, reader, query);
                times.Add(clock.Elapsed.TotalMilliseconds);
                var current = Ids(result);
                Assert.True(ids is null || ids.SequenceEqual(current));
                ids = current;
            }

            return (times, ids!);
        }

        var scope = shareOnly.Append(control).ToList();
        var itemsBefore = PostingItems(w, w.Tags, scope);
        var before = await MeasureAsync();
        var all = string.Join(",", Enumerable.Range(1, selections).Select(i => "t" + i.ToString("D2", CultureInfo.InvariantCulture)));
        foreach (var record in shareOnly)
        {
            await EditAsync(w, record, new(StringComparer.Ordinal) { ["topic_tags"] = all });
        }

        await WaitPostingItemsAsync(w, w.Tags, scope, itemsBefore + (records * selections));
        await AssertReaderCannotReadAsync(w, shareOnly[0], "topic_tags");
        var after = await MeasureAsync();
        Assert.Equal([control], before.Ids);
        Assert.Equal(before.Ids, after.Ids);

        static object Stats(List<double> ms)
        {
            var sorted = ms.Order().ToList();
            double At(double q) => sorted[(int)Math.Min(sorted.Count - 1, Math.Ceiling(q * sorted.Count) - 1)];
            return new { n = sorted.Count, p50_ms = Math.Round(At(0.50), 1), p95_ms = Math.Round(At(0.95), 1), min_ms = Math.Round(sorted[0], 1), max_ms = Math.Round(sorted[^1], 1) };
        }

        await WriteAsync("uf25-work.json", new Dictionary<string, object?>
        {
            ["kind"] = "C-140 UF-25 informational latency: Tier 1 share-only check set with and without unauthorized items (focused, local Docker, synthetic; client-measured round trips to the Authority)",
            ["measured_on"] = DateTimeOffset.UtcNow,
            ["git_commit"] = ComposeStack.GitCommit(),
            ["power_line_status"] = PowerLineStatus(),
            ["share_only_records"] = records,
            ["posting_items_checked_before"] = itemsBefore,
            ["posting_items_checked_after"] = PostingItems(w, w.Tags, scope),
            ["readable_items_for_reader"] = 1,
            ["before"] = Stats(before.Ms),
            ["after"] = Stats(after.Ms),
            ["results_identical"] = true,
            ["note"] = "Informational only: local single-node stack, no repetition across runs; the difference includes the SpiceDB bulk field_item#read checks on the unauthorized items and the posting reads. Not a gate.",
        });
    }

    private static string PowerLineStatus()
    {
        try
        {
            var start = new ProcessStartInfo("powershell.exe", "-NoProfile -Command \"Add-Type -AssemblyName System.Windows.Forms; [System.Windows.Forms.SystemInformation]::PowerStatus.PowerLineStatus\"") { RedirectStandardOutput = true, UseShellExecute = false };
            using var process = Process.Start(start)!;
            var output = process.StandardOutput.ReadToEnd().Trim();
            process.WaitForExit(10_000);
            return output.Length == 0 ? "unknown" : output;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return "unknown";
        }
    }

    private static async Task WriteAsync(string name, object report)
    {
        var directory = Path.Combine(ComposeStack.RepositoryRoot, "artifacts", "ix-5");
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(Path.Combine(directory, name), JsonSerializer.Serialize(report, Indented), Ct);
    }
}
