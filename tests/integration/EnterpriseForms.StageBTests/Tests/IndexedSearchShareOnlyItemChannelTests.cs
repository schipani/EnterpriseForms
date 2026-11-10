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
/// UF-25 on the real security-test stack. C-140 reproduced the defect: Tier 1's share-only readability step (design R1 §E.3)
/// bulk-checked <c>field_item#read</c> on every posting item of each predicate field, including items the caller cannot read, and
/// applied the <c>items</c> limit to that count. C-141 corrects it (U-1b): the candidates come from the caller's own grants, the
/// limit counts them only, and evaluability compares distinct confirmed items with the accepted per-field item count. These are the
/// RQ-1…RQ-8 regression tests: hidden content changes neither the item checks (Testing-only probe) nor the refusal; results and the
/// partially-shared-field semantics are unchanged; revocation and team membership narrow the candidates; tampered, stale or missing
/// counts and a SpiceDB contradiction fail closed. Synthetic data only. A focused development test class (C-131), not Stage B.
/// </summary>
[Collection(StageBStackCollectionDefinition.Name)]
public sealed class IndexedSearchShareOnlyItemChannelTests(StageBFixture fixture)
{
    private const string LimitVariable = "IX_SHARE_ONLY_ITEM_LIMIT";
    private const int SmallLimit = 2;

    private sealed record Ws(StageBScenario S, string Path, Guid FormId, Guid? TeamId, Guid Count, Guid Status, Guid Open, Guid Tags, IReadOnlyDictionary<string, Guid> TagOptions);

    private sealed record Session(string Digest, TestUser User);

    private sealed record Probe(int ShareOnlyRecords, int CandidateItems, int ItemChecksIssued, int EvaluableRecords, string OutcomeCode);

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string Scalar(string sql) => fixture.Stack.AuthorityObserverSql(sql).Trim().Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim() ?? string.Empty;

    private string Projection(Guid org) => Scalar($"SELECT projection_status_name || '|' || coalesce(projection_status_reason_code, '') FROM authority_search.search_projection_states WHERE organization_id = '{org:D}';");

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

    /// <summary>Shares the listed fields with the Reader along <paramref name="path"/> (overlapping: a direct and a team share). Returns the share ids.</summary>
    private async Task<List<Guid>> ShareViaAsync(Ws w, Guid record, string? path, params string[] fieldKeys)
    {
        var recipients = (path ?? w.Path) switch
        {
            "direct" => new (Guid? User, Guid? Team)[] { (w.S.Reader.AuthorityUserId, null) },
            "team" => new (Guid? User, Guid? Team)[] { (null, w.TeamId) },
            _ => new (Guid? User, Guid? Team)[] { (w.S.Reader.AuthorityUserId, null), (null, w.TeamId) },
        };
        var ids = new List<Guid>();
        foreach (var (user, team) in recipients)
        {
            for (var attempt = 0; ; attempt++)
            {
                var share = await fixture.Api.PostAsync($"/api/v1/form-records/{record:D}/shares", w.S.Token(w.S.Clerk), new { recipientUserId = user, recipientTeamId = team, recipientRole = "Reader", fieldKeys });
                if (share.IsSuccess)
                {
                    ids.Add(share.Root.GetProperty("recordShareId").GetGuid());
                    break;
                }

                Assert.True(attempt < 30 && share.ErrorCode is "AUTHORITY_NOT_READY" or "COMMAND_PENDING" or "STALE_AUTHORIZATION_REVISION", "share failed: " + share.Body);
                await Task.Delay(300, Ct);
            }
        }

        Assert.True((await w.S.WaitProjectionAsync(w.S.Reader, record, TimeSpan.FromSeconds(40))).IsSuccess);
        return ids;
    }

    private Task<List<Guid>> ShareAsync(Ws w, Guid record, params string[] fieldKeys) => ShareViaAsync(w, record, null, fieldKeys);

    /// <summary>Posting items of the field on the record that a share whose reader is the Reader reaches (the readable items, from the Authority's canonical edges).</summary>
    private int ReadableItems(Ws w, Guid record, Guid field) => int.Parse(Scalar(
        $"SELECT count(DISTINCT p.manifest_item_id) FROM authority_search.search_option_postings p " +
        $"JOIN authority_relationship_edges i ON i.organization_id = p.organization_id AND i.resource_type_name = 'field_item' AND i.resource_entity_id = p.manifest_item_id AND i.relationship_name = 'shared_via' AND i.removed_by_security_command_id IS NULL " +
        $"JOIN authority_relationship_edges r ON r.organization_id = p.organization_id AND r.resource_type_name = 'record_share' AND r.resource_entity_id = i.subject_entity_id AND r.subject_type_name = 'user' AND r.subject_entity_id = '{w.S.Reader.AuthorityUserId:D}' AND r.removed_by_security_command_id IS NULL " +
        $"WHERE p.organization_id = '{w.S.OrganizationId:D}' AND p.form_record_id = '{record:D}' AND p.field_definition_id = '{field:D}';"), CultureInfo.InvariantCulture);

    private async Task RevokeAsync(Ws w, Guid record, Guid share)
    {
        for (var attempt = 0; ; attempt++)
        {
            var revoked = await fixture.Api.PostAsync($"/api/v1/form-records/{record:D}/shares/{share:D}/revoke", w.S.Token(w.S.Clerk), new { });
            if (revoked.IsSuccess)
            {
                await w.S.WaitGateReadyAsync();
                return;
            }

            Assert.True(attempt < 30 && revoked.ErrorCode is "AUTHORITY_NOT_READY" or "COMMAND_PENDING" or "STALE_AUTHORIZATION_REVISION", "revoke failed: " + revoked.Body);
            await Task.Delay(300, Ct);
        }
    }

    private async Task WaitCompleteAsync(Ws w, int coverages = 3, int seconds = 300)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
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

    /// <summary>All posting items of a field over the records (readable or not): evidence that the hidden content exists in the index.</summary>
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

    private Guid ActiveApproval(Guid field) => Guid.Parse(Scalar($"SELECT field_search_approval_id FROM field_search_approvals WHERE field_definition_id = '{field:D}' AND approval_state_name = 'Active';"));

    /// <summary>The stored item count of the field on the record's entry (C-141), as the Authority wrote it.</summary>
    private int StoredCount(Ws w, Guid record, Guid field) => int.Parse(Scalar(
        $"SELECT derived_field_item_counts[array_position(derived_field_search_approval_ids, '{ActiveApproval(field):D}'::uuid)] FROM authority_search.search_record_entries WHERE organization_id = '{w.S.OrganizationId:D}' AND form_record_id = '{record:D}';"), CultureInfo.InvariantCulture);

    private void SetStoredCount(Ws w, Guid record, Guid field, int count) => fixture.Stack.AuthorityAdministratorSql(
        $"SELECT set_config('authority_search.organization_id', '{w.S.OrganizationId:D}', false); UPDATE authority_search.search_record_entries SET derived_field_item_counts[array_position(derived_field_search_approval_ids, '{ActiveApproval(field):D}'::uuid)] = {count} WHERE organization_id = '{w.S.OrganizationId:D}' AND form_record_id = '{record:D}';");

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

    private static object CountEquals(Guid field, int value) => new { field_definition_id = field, @operator = "equals", value_type = "Integer", value = value.ToString(CultureInfo.InvariantCulture), lower_bound = (string?)null, upper_bound = (string?)null };

    private static object Query(Ws w, object condition) =>
        new { form_definition_id = w.FormId, conditions = new[] { condition }, record_status = (string?)null, include_children = false, include_archived = false, sort_by = (string?)null, sort_direction = (string?)null, page_size = 50 };

    /// <summary>One search; returns the result and the probe of its (last) attempt.</summary>
    private async Task<(HttpResult Result, Probe? Probe)> IndexedAsync(Ws w, Session session, object query)
    {
        for (var attempt = 0; ; attempt++)
        {
            var nonce = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
            var correlation = Guid.CreateVersion7();
            var result = await fixture.Authority.SendAsync(HttpMethod.Post, "/internal/v1/search/indexed-query", TestIdentity.WorkloadToken(),
                new { organization_id = w.S.OrganizationId, session_digest_base64 = session.Digest, query_nonce_base64 = nonce, query }, w.S.Token(session.User), correlationId: correlation);
            if (result.IsSuccess || attempt >= 30 || result.ErrorCode is not ("AUTHORITY_NOT_READY" or "SEARCH_SNAPSHOT_CHANGED"))
            {
                return (result, await ProbeAsync(correlation));
            }

            await Task.Delay(400, Ct);
        }
    }

    private async Task<Probe?> ProbeAsync(Guid correlation)
    {
        var probe = await fixture.Authority.SendAsync(HttpMethod.Get, $"/internal/v1/testing/search/share-only-probe/{correlation:D}", TestIdentity.WorkloadToken(), null);
        if (probe.Status == HttpStatusCode.NotFound)
        {
            return null;
        }

        Assert.True(probe.IsSuccess, probe.Body);
        var last = probe.Root.EnumerateArray().Last();
        int Int(string name) => last.GetProperty(name).GetInt32();
        return new Probe(Int("share_only_records"), Int("candidate_items"), Int("item_checks_issued"), Int("evaluable_records"), last.GetProperty("outcome_code").GetString()!);
    }

    private static List<Guid> Ids(HttpResult result)
    {
        Assert.True(result.IsSuccess, result.Body);
        return result.Root.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("form_record_id").GetGuid()).Order().ToList();
    }

    private static void AssertRefused(HttpResult result, string code)
    {
        Assert.False(result.IsSuccess, result.Body);
        Assert.Equal(code, result.ErrorCode);
        Assert.DoesNotContain("form_record_id", result.Body, StringComparison.Ordinal);
    }

    private void RecreateAuthority(int? limit)
    {
        Environment.SetEnvironmentVariable(LimitVariable, limit?.ToString(CultureInfo.InvariantCulture));
        fixture.Stack.Compose("up -d --no-deps --force-recreate --wait authority", 300);
    }

    // ---- RQ-2, RQ-5, RQ-8: hidden content, the limit and the semantics ----------------------------------------------------------

    private sealed record Case(Ws W, Guid Control, Guid[] ShareOnly, Guid PartialTags);

    /// <summary>
    /// The inverse of the C-140 reproduction. With an items limit of 2, the Reader's search on <c>case_status</c> succeeds before and
    /// after the owner writes statuses the Reader cannot read into three share-only records, with the same single item check
    /// (probe) although the index now holds four status items for those records. The limit still refuses a search whose
    /// <i>readable</i> candidates exceed it. With the default limit the results are identical, and a partially shared multi-select
    /// still drops out of the results once the owner adds a selection the Reader cannot read (R1 §E.3 semantics, RQ-8). Direct
    /// share, team share, and overlapping direct and team shares (distinct items counted once).
    /// </summary>
    [Fact]
    public async Task UF25HiddenItemsChangeNeitherTheItemChecksNorTheRefusalAndResultsAreUnchanged()
    {
        var cases = new List<Case>();
        var evidence = new List<object>();
        try
        {
            RecreateAuthority(SmallLimit);
            foreach (var path in new[] { "direct", "team", "overlapping" })
            {
                var w = await WorkspaceAsync(path);
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

                var (before, probeBefore) = await IndexedAsync(w, reader, statusOpen);
                Assert.Equal([control], Ids(before));
                Assert.Equal(new Probe(5, 1, 1, 5, "DECIDED"), probeBefore); // one readable status item (also through two paths: counted once)
                var itemsBefore = PostingItems(w, w.Status, [control, partial, .. shareOnly]);
                var tagsT01 = Query(w, Cond(w.Tags, "includes", w.TagOptions["t01"]));
                Assert.Equal(new[] { control, partial }.Order().ToList(), Ids((await IndexedAsync(w, reader, tagsT01)).Result));

                foreach (var record in shareOnly)
                {
                    await EditAsync(w, record, new(StringComparer.Ordinal) { ["case_status"] = "open" });
                }

                await WaitPostingItemsAsync(w, w.Status, [control, partial, .. shareOnly], 1 + shareOnly.Length);
                foreach (var record in shareOnly)
                {
                    await AssertReaderCannotReadAsync(w, record, "case_status");
                    Assert.Equal(1, StoredCount(w, record, w.Status)); // the accepted total now counts the hidden status
                }

                var (after, probeAfter) = await IndexedAsync(w, reader, statusOpen);
                Assert.Equal([control], Ids(after)); // C-140: 422 SEARCH_VERIFICATION_LIMIT (items) here
                Assert.Equal(new Probe(5, 1, 1, 2, "DECIDED"), probeAfter); // same check count; the three records are no longer evaluable on status

                // RQ-5: the limit counts readable candidates. exhibit_count is readable on all five records: 5 > 2.
                var (limited, limitedProbe) = await IndexedAsync(w, reader, Query(w, CountEquals(w.Count, 1)));
                AssertRefused(limited, "SEARCH_VERIFICATION_LIMIT");
                Assert.Contains("items", limited.Body, StringComparison.Ordinal);
                // The reader stops at limit + 1 candidates (it never reads more than it needs to refuse); no item check is issued.
                Assert.Equal(new Probe(5, SmallLimit + 1, 0, 0, "LIMIT_ITEMS"), limitedProbe);

                await EditAsync(w, partial, new(StringComparer.Ordinal) { ["topic_tags"] = "t01,t02" });
                await WaitPostingItemsAsync(w, w.Tags, [partial], 2);
                cases.Add(new Case(w, control, shareOnly, partial));
                evidence.Add(new
                {
                    path,
                    items_limit = SmallLimit,
                    status_posting_items_in_reader_scope_before = itemsBefore,
                    status_posting_items_in_reader_scope_after = PostingItems(w, w.Status, [control, partial, .. shareOnly]),
                    before = new { http = (int)before.Status, results = Ids(before).Count, probe = probeBefore },
                    after = new { http = (int)after.Status, results = Ids(after).Count, probe = probeAfter },
                    readable_limit_refusal = new { http = (int)limited.Status, error_code = limited.ErrorCode, probe = limitedProbe },
                });
            }
        }
        finally
        {
            RecreateAuthority(null);
        }

        var semantics = new List<object>();
        foreach (var c in cases)
        {
            var reader = await SessionAsync(c.W, c.W.S.Reader);
            Assert.Equal([c.Control], Ids((await IndexedAsync(c.W, reader, Query(c.W, Cond(c.W.Status, "is", c.W.Open)))).Result));
            // RQ-8: the partial record's readable selection t01 is unchanged, but it is not evaluable once an unreadable selection exists.
            var (tagged, probe) = await IndexedAsync(c.W, reader, Query(c.W, Cond(c.W.Tags, "includes", c.W.TagOptions["t01"])));
            Assert.Equal([c.Control], Ids(tagged));
            Assert.Equal(2, probe!.ItemChecksIssued); // the control's t01 and the partial record's t01: never the hidden t02
            semantics.Add(new { path = c.W.Path, status_is_open = "control only", tags_includes_t01_after_unshared_selection_added = "control only (partial record excluded, R1 §E.3)", tags_item_checks = probe.ItemChecksIssued });
        }

        await WriteAsync("uf25-regression.json", new Dictionary<string, object?>
        {
            ["kind"] = "C-141 UF-25 regression (U-1b): share-only item checks and items limit independent of unauthorized content (focused, local Docker, synthetic)",
            ["measured_on"] = DateTimeOffset.UtcNow,
            ["git_commit"] = ComposeStack.GitCommit(),
            ["cases"] = evidence,
            ["default_limit_results"] = semantics,
        });
    }

    // ---- Revocation and team membership --------------------------------------------------------------------------------------

    /// <summary>
    /// The candidates follow the caller's current grants at the pinned revision: revoking one of two overlapping paths keeps the
    /// record (the other path still reaches the same items, counted once); revoking the only path removes it; removing the Reader
    /// from the team removes every record reached only through the team.
    /// </summary>
    [Fact]
    public async Task RevocationAndTeamMembershipChangesNarrowTheCandidatesAndTheResults()
    {
        var w = await WorkspaceAsync("overlapping");
        var direct = await CreateAsync(w, new(StringComparer.Ordinal) { ["exhibit_count"] = "1", ["case_status"] = "open" });
        var team = await CreateAsync(w, new(StringComparer.Ordinal) { ["exhibit_count"] = "2", ["case_status"] = "open" });
        var both = await CreateAsync(w, new(StringComparer.Ordinal) { ["exhibit_count"] = "3", ["case_status"] = "open" });
        var directShare = (await ShareViaAsync(w, direct, "direct", "case_status")).Single();
        await ShareViaAsync(w, team, "team", "case_status");
        var bothShares = await ShareViaAsync(w, both, "overlapping", "case_status");
        await WaitCompleteAsync(w);
        var reader = await SessionAsync(w, w.S.Reader);
        var query = Query(w, Cond(w.Status, "is", w.Open));

        var (all, probe) = await IndexedAsync(w, reader, query);
        Assert.Equal(new[] { direct, team, both }.Order().ToList(), Ids(all));
        Assert.Equal(3, probe!.ItemChecksIssued); // "both" is reached by two paths: one item, one check

        await RevokeAsync(w, both, bothShares[0]); // its direct share; the team share remains
        (all, probe) = await IndexedAsync(w, reader, query);
        Assert.Equal(new[] { direct, team, both }.Order().ToList(), Ids(all));
        Assert.Equal(3, probe!.ItemChecksIssued);

        await RevokeAsync(w, direct, directShare);
        (all, probe) = await IndexedAsync(w, reader, query);
        Assert.Equal(new[] { team, both }.Order().ToList(), Ids(all));
        Assert.Equal(2, probe!.ItemChecksIssued);

        var removed = await fixture.Api.PostAsync($"/api/v1/teams/{w.TeamId:D}/members/{w.S.Reader.AuthorityUserId:D}/remove", w.S.Token(w.S.Designer), new { });
        Assert.True(removed.IsSuccess, removed.Body);
        await w.S.WaitGateReadyAsync();
        var (none, noneProbe) = await IndexedAsync(w, reader, query);
        Assert.True(none.IsSuccess, none.Body);
        Assert.Empty(Ids(none));
        Assert.True(noneProbe is null or { ItemChecksIssued: 0 }, "no candidate after the membership removal");
    }

    // ---- RQ-4: stale, missing and contradictory metadata; Q4 -------------------------------------------------------------------

    /// <summary>
    /// A count lowered so that a partially readable record looks completely readable is caught on the bound match before anything is
    /// released (index quarantined, 503 SEARCH_INDEX_INCONSISTENT, the organization itself not quarantined). A count raised on a
    /// completely readable record cannot be told apart from a partial share at query time (that would require reading the hidden
    /// items): the record is excluded, and the cheap sweep S-A quarantines the index within its interval.
    /// </summary>
    [Fact]
    public async Task TamperedItemCountsFailClosed()
    {
        // Lowered: P has t01 (shared) and t02 (not shared); its tags count is set to 1.
        var w = await WorkspaceAsync("direct");
        var p = await CreateAsync(w, new(StringComparer.Ordinal) { ["exhibit_count"] = "1", ["topic_tags"] = "t01" });
        await ShareAsync(w, p, "exhibit_count", "topic_tags");
        await EditAsync(w, p, new(StringComparer.Ordinal) { ["topic_tags"] = "t01,t02" });
        await WaitPostingItemsAsync(w, w.Tags, [p], 2);
        await WaitCompleteAsync(w);
        var reader = await SessionAsync(w, w.S.Reader);
        var tags = Query(w, Cond(w.Tags, "includes", w.TagOptions["t01"]));
        Assert.Empty(Ids((await IndexedAsync(w, reader, tags)).Result));
        Assert.Equal(2, StoredCount(w, p, w.Tags));
        Assert.Equal(1, ReadableItems(w, p, w.Tags)); // t01 kept its item id through the edit and is still shared; t02 is not
        SetStoredCount(w, p, w.Tags, 1);
        var (lowered, _) = await IndexedAsync(w, reader, tags);
        AssertRefused(lowered, "SEARCH_INDEX_INCONSISTENT");
        Assert.StartsWith("Quarantined|", Projection(w.S.OrganizationId), StringComparison.Ordinal);
        Assert.Equal("f", Scalar($"SELECT integrity_quarantine_enabled FROM organization_authorization_states WHERE organization_id = '{w.S.OrganizationId:D}';"));

        // Raised: Q is completely readable on tags; its count is set to 2.
        var w2 = await WorkspaceAsync("direct");
        var q = await CreateAsync(w2, new(StringComparer.Ordinal) { ["exhibit_count"] = "1", ["topic_tags"] = "t01" });
        await ShareAsync(w2, q, "exhibit_count", "topic_tags");
        await WaitCompleteAsync(w2);
        var reader2 = await SessionAsync(w2, w2.S.Reader);
        var tags2 = Query(w2, Cond(w2.Tags, "includes", w2.TagOptions["t01"]));
        Assert.Equal([q], Ids((await IndexedAsync(w2, reader2, tags2)).Result));
        SetStoredCount(w2, q, w2.Tags, 2);
        var deadline = DateTime.UtcNow.AddSeconds(90);
        string state;
        while (!(state = Projection(w2.S.OrganizationId)).StartsWith("Quarantined|", StringComparison.Ordinal))
        {
            Assert.True(DateTime.UtcNow < deadline, "S-A did not quarantine the index: " + state);
            await Task.Delay(1000, Ct);
        }

        Assert.Contains("FIELD_ITEM_COUNT_MISMATCH", state, StringComparison.Ordinal); // S-A, or S-B if its pass came first
        AssertRefused((await IndexedAsync(w2, reader2, tags2)).Result, "SEARCH_INDEX_INCONSISTENT");
    }

    /// <summary>
    /// C-142 (R-48): a forged downgrade. The form is attested Complete by this image (its journaled attestation carries the attesting
    /// derivation version 3); a search-schema writer then rewrites a share-only record's entry as version 2 without counts. The search
    /// is refused with the index quarantined (SEARCH_INDEX_INCONSISTENT, reason ...DERIVATION_DOWNGRADED), the organization is not
    /// quarantined, no derivation upgrade is journaled, and the entry is left in place as evidence (never quietly re-derived).
    /// The genuine-legacy upgrade path is covered by unit tests and by the C-141 deployment on this stack's older data.
    /// </summary>
    [Fact]
    public async Task R48AForgedDowngradeIsQuarantinedAndNeverRebuiltAsLegacy()
    {
        var w = await WorkspaceAsync("direct");
        var r = await CreateAsync(w, new(StringComparer.Ordinal) { ["exhibit_count"] = "1", ["case_status"] = "open" });
        await ShareAsync(w, r, "exhibit_count", "case_status");
        await WaitCompleteAsync(w);
        Assert.NotEqual("0", Scalar($"SELECT count(*) FROM security_journal_entries WHERE organization_id = '{w.S.OrganizationId:D}' AND journal_event_kind = 'SearchIndexCoverageAttested' AND (journal_event_body_json::jsonb -> 'body' ->> 'attesting_entry_derivation_version') = '3';"));
        var reader = await SessionAsync(w, w.S.Reader);
        var query = Query(w, Cond(w.Status, "is", w.Open));
        Assert.Equal([r], Ids((await IndexedAsync(w, reader, query)).Result));

        fixture.Stack.AuthorityAdministratorSql($"SELECT set_config('authority_search.organization_id', '{w.S.OrganizationId:D}', false); UPDATE authority_search.search_record_entries SET entry_derivation_version = 2, derived_field_item_counts = NULL WHERE organization_id = '{w.S.OrganizationId:D}' AND form_record_id = '{r:D}';");
        var (refused, _) = await IndexedAsync(w, reader, query);
        AssertRefused(refused, "SEARCH_INDEX_INCONSISTENT"); // the query path, or S-A if its pass came first
        var state = Projection(w.S.OrganizationId);
        Assert.StartsWith("Quarantined|", state, StringComparison.Ordinal);
        Assert.Contains("ENTRY_DERIVATION_DOWNGRADED", state, StringComparison.Ordinal);
        Assert.Equal("f", Scalar($"SELECT integrity_quarantine_enabled FROM organization_authorization_states WHERE organization_id = '{w.S.OrganizationId:D}';"));
        await Task.Delay(TimeSpan.FromSeconds(15), Ct); // longer than one S-A interval (10 s on this stack): S-A must not turn it into an upgrade
        Assert.Equal("0", Scalar($"SELECT count(*) FROM security_journal_entries WHERE organization_id = '{w.S.OrganizationId:D}' AND journal_event_kind = 'SearchIndexDerivationUpgradeStarted';"));
        Assert.Equal("2", Scalar($"SELECT entry_derivation_version FROM authority_search.search_record_entries WHERE organization_id = '{w.S.OrganizationId:D}' AND form_record_id = '{r:D}';"));
        Assert.Equal("0", Scalar($"SELECT count(*) FROM authority_search.search_field_coverages WHERE organization_id = '{w.S.OrganizationId:D}' AND coverage_state_name = 'Building';"));
    }

    /// <summary>
    /// Q4 (FD-4): SpiceDB denies a candidate that the Authority's canonical edges grant at the same revision (a projection tuple
    /// removed out of band in SpiceDB's datastore, made visible by a later unrelated write). The S1 decisive verification runs
    /// first (the markers agree), then the search index is quarantined; nothing is released and the organization is not quarantined.
    /// </summary>
    [Fact]
    public async Task ASpiceDbDenialOfAGrantedCandidateIsEscalatedAndFailsClosed()
    {
        var w = await WorkspaceAsync("direct");
        var r = await CreateAsync(w, new(StringComparer.Ordinal) { ["exhibit_count"] = "1", ["case_status"] = "open" });
        await ShareAsync(w, r, "exhibit_count", "case_status");
        var other = await CreateAsync(w, new(StringComparer.Ordinal) { ["exhibit_count"] = "2" });
        await WaitCompleteAsync(w);
        var reader = await SessionAsync(w, w.S.Reader);
        var query = Query(w, Cond(w.Status, "is", w.Open));
        Assert.Equal([r], Ids((await IndexedAsync(w, reader, query)).Result));

        var item = Guid.Parse(Scalar($"SELECT manifest_item_id FROM authority_search.search_option_postings WHERE organization_id = '{w.S.OrganizationId:D}' AND form_record_id = '{r:D}' AND field_definition_id = '{w.Status:D}';"));
        fixture.Stack.SpiceDbDatastoreSimulationShell($"psql -v ON_ERROR_STOP=1 -qAt -c \"UPDATE relation_tuple SET deleted_xid = pg_current_xact_id() WHERE namespace = 'field_item' AND object_id = '{item:D}' AND relation = 'shared_via' AND deleted_xid = '9223372036854775807'::xid8;\"");
        await ShareAsync(w, other, "exhibit_count"); // an unrelated security command: a new SpiceDB revision that sees the removal
        var (escalated, probe) = await IndexedAsync(w, reader, query);
        AssertRefused(escalated, "SEARCH_INDEX_INCONSISTENT");
        Assert.Equal("CANDIDATE_DENIED", probe!.OutcomeCode);
        Assert.Equal("Quarantined|SHARE_CANDIDATE_DENIED_BY_PROJECTION", Projection(w.S.OrganizationId));
        Assert.Equal("f", Scalar($"SELECT integrity_quarantine_enabled FROM organization_authorization_states WHERE organization_id = '{w.S.OrganizationId:D}';"));
    }

    // ---- RQ-7: work and latency (informational, AC power) ---------------------------------------------------------------------

    /// <summary>
    /// Informational measurement, no timing assertion: the Reader's search latency on <c>topic_tags</c> with no unreadable selections
    /// in the share-only scope, then after the owner writes 40 selections into each of 25 share-only records (1,000 posting items the
    /// Reader cannot read). Results must be identical and, since C-141, the item checks must be identical (probe).
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

        async Task<(List<double> Ms, List<Guid> Ids, HashSet<int> Checks)> MeasureAsync()
        {
            await IndexedAsync(w, reader, query); // warm-up, not counted
            var times = new List<double>();
            var checks = new HashSet<int>();
            List<Guid>? ids = null;
            for (var n = 0; n < searches; n++)
            {
                var clock = Stopwatch.StartNew();
                var (result, probe) = await IndexedAsync(w, reader, query);
                times.Add(clock.Elapsed.TotalMilliseconds);
                checks.Add(probe!.ItemChecksIssued);
                var current = Ids(result);
                Assert.True(ids is null || ids.SequenceEqual(current));
                ids = current;
            }

            return (times, ids!, checks);
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
        Assert.Equal([1], before.Checks);
        Assert.Equal(before.Checks, after.Checks);

        static object Stats(List<double> ms)
        {
            var sorted = ms.Order().ToList();
            double At(double q) => sorted[(int)Math.Min(sorted.Count - 1, Math.Ceiling(q * sorted.Count) - 1)];
            return new { n = sorted.Count, p50_ms = Math.Round(At(0.50), 1), p95_ms = Math.Round(At(0.95), 1), min_ms = Math.Round(sorted[0], 1), max_ms = Math.Round(sorted[^1], 1) };
        }

        await WriteAsync("uf25-work.json", new Dictionary<string, object?>
        {
            ["kind"] = "UF-25 informational latency: Tier 1 share-only step with and without unauthorized items (focused, local Docker, synthetic; client-measured round trips to the Authority)",
            ["measured_on"] = DateTimeOffset.UtcNow,
            ["git_commit"] = ComposeStack.GitCommit(),
            ["power_line_status"] = PowerLineStatus(),
            ["share_only_records"] = records,
            ["posting_items_in_scope_before"] = itemsBefore,
            ["posting_items_in_scope_after"] = PostingItems(w, w.Tags, scope),
            ["item_checks_before"] = before.Checks.Single(),
            ["item_checks_after"] = after.Checks.Single(),
            ["readable_items_for_reader"] = 1,
            ["before"] = Stats(before.Ms),
            ["after"] = Stats(after.Ms),
            ["results_identical"] = true,
            ["note"] = "Informational only: local single-node stack, one run of 21 searches per phase; not a constant-time claim and not a gate.",
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
