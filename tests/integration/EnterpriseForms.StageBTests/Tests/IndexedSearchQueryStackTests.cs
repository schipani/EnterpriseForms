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
/// IX-3 (C-132) on the real security-test stack: the Authority's Tier 1 indexed query and page operations (design §8, §17.2,
/// §17.3, §17.9.2, §18.1.4, §18.4) and the business Shadow engine (R1 §G.3). The Authority is called directly with the business
/// workload credential, a delegated user assertion and a real Authority session; the shadow comparisons go through the business
/// API's Testing endpoint, which runs V1 and the indexed engine on the same request. Synthetic data only. This is a focused
/// development test class (C-131), not a Stage B group run.
/// </summary>
[Collection(StageBStackCollectionDefinition.Name)]
public sealed class IndexedSearchQueryStackTests(StageBFixture fixture)
{
    private sealed record Ws(StageBScenario S, Guid FormId, Guid Reference, Guid Count, Guid Hearing, Guid Status, Guid Open, Guid Closed, Guid Urgent, Guid Note);

    private sealed record Session(string Digest, TestUser User);

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string Sql(string sql) => fixture.Stack.AuthorityObserverSql(sql).Trim();

    private string Scalar(string sql) => Sql(sql).Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim() ?? string.Empty;

    // ---- workspace ---------------------------------------------------------------------------------------------------------

    private async Task<Ws> WorkspaceAsync(string name)
    {
        var s = new StageBScenario(fixture, name);
        await s.BootstrapAsync();
        var current = await fixture.Api.GetAsync("/api/v1/organization/security-policy", s.Token(s.Designer));
        var policy = await fixture.Api.SendAsync(HttpMethod.Put, "/api/v1/organization/security-policy", s.Token(s.Designer), new { minimumFieldClassification = "Internal", plaintextInternalFieldsPermitted = true, plaintextDowngradePermitted = false, plaintextSearchPermitted = true, policyStatementText = "IX-3 synthetic organization.", expectedPolicyRevision = current.Root.GetProperty("securityPolicyRevision").GetInt64() }, Guid.NewGuid().ToString("N"));
        Assert.True(policy.IsSuccess, policy.Body);
        var formId = await s.CreateFormAsync("IX-3 " + s.Name, s.Clerk);
        async Task<JsonElement> FieldAsync(string key, string label, string type, string classification = "Internal", string protection = "Standard", object? options = null)
        {
            var added = await fixture.Api.PostAsync($"/api/v1/form-definitions/{formId:D}/fields", s.Token(s.Designer), new { fieldKey = key, fieldDisplayName = label, fieldDescription = string.Empty, fieldDataType = type, options, fieldClassification = classification, fieldProtectionMode = protection });
            Assert.True(added.Status == HttpStatusCode.Created, $"add {key}: " + added.Body);
            return added.Root.Clone();
        }

        var reference = (await FieldAsync("case_reference", "Case reference", "String")).GetProperty("fieldDefinitionId").GetGuid();
        var count = (await FieldAsync("exhibit_count", "Exhibit count", "Integer")).GetProperty("fieldDefinitionId").GetGuid();
        var hearing = (await FieldAsync("hearing_date", "Hearing date", "Date")).GetProperty("fieldDefinitionId").GetGuid();
        var status = await FieldAsync("case_status", "Case status", "Option", options: new[] { new { optionKey = "open", optionDisplayText = "Open" }, new { optionKey = "closed", optionDisplayText = "Closed" } });
        var statusId = status.GetProperty("fieldDefinitionId").GetGuid();
        var options = status.GetProperty("options").EnumerateArray().ToDictionary(o => o.GetProperty("optionKey").GetString()!, o => o.GetProperty("fieldOptionId").GetGuid());
        var urgent = (await FieldAsync("urgent", "Urgent", "Boolean")).GetProperty("fieldDefinitionId").GetGuid();
        var note = (await FieldAsync("case_note", "Case note", "String", "Confidential", "ApplicationEncrypted")).GetProperty("fieldDefinitionId").GetGuid();
        await s.SaveDraftAsync(formId, StageBScenario.Layout("IX-3", StageBScenario.Section("main", "Main",
            StageBScenario.Field(reference, "text", "Case reference", 2), StageBScenario.Field(count, "number", "Exhibit count", 2), StageBScenario.Field(hearing, "date", "Hearing date", 2),
            $$"""{"kind":"field","fieldDefinitionId":"{{statusId:D}}","control":"select","label":"Case status","layout":{"columnSpan":2,"startOnNewRow":false},"optionsSnapshot":[{"fieldOptionId":"{{options["open"]:D}}","label":"Open","sortOrder":1},{"fieldOptionId":"{{options["closed"]:D}}","label":"Closed","sortOrder":2}]}""",
            StageBScenario.Field(urgent, "checkbox", "Urgent", 2), StageBScenario.Field(note, "textarea", "Case note", 4))));
        Assert.True((await s.PublishAsync(formId, "v1")).GetProperty("authorityRegistered").GetBoolean());
        var w = new Ws(s, formId, reference, count, hearing, statusId, options["open"], options["closed"], urgent, note);
        foreach (var (field, ops) in new[] { (reference, new[] { "equals", "startsWith", "contains" }), (count, new[] { "equals", "greaterThan", "greaterThanOrEqual", "lessThan", "lessThanOrEqual", "between" }), (hearing, new[] { "equals", "before", "after", "between" }), (statusId, new[] { "is", "includes" }), (urgent, new[] { "isTrue", "isFalse" }) })
        {
            var approved = await fixture.Api.PostAsync($"/api/v1/form-definitions/{formId:D}/fields/{field:D}/search-approval", s.Token(s.Administrator), new { operators = ops, approvalReference = "IX-3", useDevelopmentStepUp = true });
            Assert.True(approved.IsSuccess, approved.Body);
        }

        var granted = await fixture.Api.SendAsync(HttpMethod.Put, $"/api/v1/form-definitions/{formId:D}/field-searchers", s.Token(s.Administrator), new { grantUserIds = new[] { s.Clerk.AuthorityUserId, s.Reader.AuthorityUserId }, grantTeamIds = Array.Empty<Guid>(), revokeUserIds = Array.Empty<Guid>(), revokeTeamIds = Array.Empty<Guid>() }, Guid.NewGuid().ToString("N"));
        Assert.True(granted.Status == HttpStatusCode.NoContent, granted.Body);
        return w;
    }

    private static Dictionary<string, string> Values(string reference, int count, string hearing, string status, bool urgent) => new(StringComparer.Ordinal)
    {
        ["case_reference"] = reference, ["exhibit_count"] = count.ToString(CultureInfo.InvariantCulture), ["hearing_date"] = hearing, ["case_status"] = status, ["urgent"] = urgent ? "true" : "false", ["case_note"] = "note " + reference,
    };

    private async Task<Guid> CreateAsync(Ws w, Dictionary<string, string> values)
    {
        var key = "ix3-" + Guid.CreateVersion7().ToString("N");
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

    private async Task<HttpResult> EditAsync(Ws w, Guid record, Dictionary<string, string> values)
    {
        for (var attempt = 0; ; attempt++)
        {
            var head = AuthorityOracle.Head(fixture.Stack, record);
            var edit = await fixture.Api.PatchAsync($"/api/v1/form-records/{record:D}", w.S.Token(w.S.Clerk), new { expectedContentRevision = head.ContentRevision, values });
            if (edit.IsSuccess || attempt >= 30 || edit.ErrorCode is not ("AUTHORITY_NOT_READY" or "COMMAND_PENDING" or "CONTENT_REVISION_CONFLICT" or "STALE_CONTENT_REVISION"))
            {
                return edit;
            }

            await Task.Delay(300, Ct);
        }
    }

    private async Task<HttpResult> ShareAsync(Ws w, Guid record, TestUser recipient, params string[] fieldKeys)
    {
        for (var attempt = 0; ; attempt++)
        {
            var share = await fixture.Api.PostAsync($"/api/v1/form-records/{record:D}/shares", w.S.Token(w.S.Clerk), new { recipientUserId = recipient.AuthorityUserId, recipientTeamId = (Guid?)null, recipientRole = "Reader", fieldKeys });
            if (share.IsSuccess || attempt >= 30 || share.ErrorCode is not ("AUTHORITY_NOT_READY" or "COMMAND_PENDING" or "STALE_AUTHORIZATION_REVISION"))
            {
                return share;
            }

            await Task.Delay(300, Ct);
        }
    }

    private string Coverages(Guid org) => Scalar($"SELECT count(*) FILTER (WHERE coverage_state_name = 'Complete') || '/' || count(*) FROM authority_search.search_field_coverages WHERE organization_id = '{org:D}' AND coverage_state_name <> 'Withdrawn';");

    private string Projection(Guid org) => Scalar($"SELECT projection_status_name || '|' || coalesce(projection_status_reason_code, '') || '|' || verification_suspended FROM authority_search.search_projection_states WHERE organization_id = '{org:D}';");

    private int Snapshots(Guid org) => int.Parse(Scalar($"SELECT count(*) FROM authority_search.search_result_snapshots WHERE organization_id = '{org:D}';"), CultureInfo.InvariantCulture);

    private async Task WaitCompleteAsync(Ws w, int coverages = 5)
    {
        var deadline = DateTime.UtcNow.AddSeconds(300);
        string last;
        do
        {
            last = Coverages(w.S.OrganizationId);
            if (last == $"{coverages}/{coverages}")
            {
                return;
            }

            await Task.Delay(1000, Ct);
        }
        while (DateTime.UtcNow < deadline);
        Assert.Fail("coverages not Complete: " + last);
    }

    // ---- Authority calls -----------------------------------------------------------------------------------------------------

    private async Task<Session> SessionAsync(Ws w, TestUser user)
    {
        var digest = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var created = await fixture.Authority.SendAsync(HttpMethod.Post, "/internal/v1/sessions", TestIdentity.WorkloadToken(), new { organization_id = w.S.OrganizationId, session_digest_base64 = digest, absolute_expires_on = DateTimeOffset.UtcNow.AddHours(1) }, w.S.Token(user));
        Assert.True(created.IsSuccess, created.Body);
        return new Session(digest, user);
    }

    private static object Cond(Guid field, string op, string valueType, string? value = null, string? lower = null, string? upper = null) =>
        new { field_definition_id = field, @operator = op, value_type = valueType, value, lower_bound = lower, upper_bound = upper };

    private static object Query(Ws w, object[] conditions, int pageSize = 10, string? status = null, bool includeArchived = false, string? sortBy = null, string? sortDirection = null) =>
        new { form_definition_id = w.FormId, conditions, record_status = status, include_children = false, include_archived = includeArchived, sort_by = sortBy, sort_direction = sortDirection, page_size = pageSize };

    private async Task<HttpResult> IndexedAsync(Ws w, Session session, string nonce, object query)
    {
        for (var attempt = 0; ; attempt++)
        {
            var result = await fixture.Authority.SendAsync(HttpMethod.Post, "/internal/v1/search/indexed-query", TestIdentity.WorkloadToken(),
                new { organization_id = w.S.OrganizationId, session_digest_base64 = session.Digest, query_nonce_base64 = nonce, query }, w.S.Token(session.User));
            if (result.IsSuccess || attempt >= 30 || result.ErrorCode is not ("AUTHORITY_NOT_READY" or "SEARCH_SNAPSHOT_CHANGED"))
            {
                return result;
            }

            await Task.Delay(400, Ct);
        }
    }

    private Task<HttpResult> PageAsync(Ws w, Session session, string nonce, object query, Guid snapshot, int offset) =>
        fixture.Authority.SendAsync(HttpMethod.Post, $"/internal/v1/search/indexed-query/{snapshot:D}/page", TestIdentity.WorkloadToken(),
            new { organization_id = w.S.OrganizationId, session_digest_base64 = session.Digest, query_nonce_base64 = nonce, query, offset }, w.S.Token(session.User));

    private static string Nonce() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

    private static List<Guid> Ids(HttpResult result)
    {
        Assert.True(result.IsSuccess, result.Body);
        return result.Root.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("form_record_id").GetGuid()).ToList();
    }

    private static Guid SnapshotId(HttpResult result) => result.Root.GetProperty("search_result_snapshot_id").GetGuid();

    private static void AssertNoPage(HttpResult result, string code)
    {
        Assert.False(result.IsSuccess, result.Body);
        Assert.Equal(code, result.ErrorCode);
        Assert.DoesNotContain("form_record_id", result.Body, StringComparison.Ordinal);
    }

    private static async Task<JsonElement> ShadowAsync(Ws w, Session session, object[] conditions, string? status = null, bool includeArchived = false, string? sortBy = null, string? sortDirection = null, int pageSize = 25)
    {
        using var http = new HttpClient { BaseAddress = new Uri(StageBFixture.ApiBaseAddress), Timeout = TimeSpan.FromMinutes(2) };
        for (var attempt = 0; ; attempt++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/testing/field-search-shadow")
            {
                Content = JsonContent.Create(new { formDefinitionId = w.FormId, conditions = conditions.Select(Camel).ToArray(), recordStatus = status, includeArchived, includeChildren = false, sortBy, sortDirection, pageSize }),
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", w.S.Token(session.User));
            request.Headers.Add("X-Testing-Session-Digest", session.Digest);
            using var response = await http.SendAsync(request, Ct);
            var text = await response.Content.ReadAsStringAsync(Ct);
            Assert.True(response.IsSuccessStatusCode, text);
            var root = JsonDocument.Parse(text).RootElement.Clone();
            var v1Error = root.GetProperty("verifiedScanError").GetString();
            var indexedError = root.GetProperty("indexedError").GetString();
            var retry = v1Error is "SEARCH_SNAPSHOT_CHANGED" || indexedError is "SEARCH_SNAPSHOT_CHANGED" or "AUTHORITY_NOT_READY" || (v1Error?.StartsWith("SEARCH_VERIFICATION_UNAVAILABLE:AWAITING_ARCHIVE", StringComparison.Ordinal) ?? false);
            if (!retry || attempt >= 20)
            {
                return root;
            }

            await Task.Delay(500, Ct);
        }

        static object Camel(object condition)
        {
            var e = JsonSerializer.SerializeToElement(condition);
            return new
            {
                fieldDefinitionId = e.GetProperty("field_definition_id").GetGuid(),
                @operator = e.GetProperty("operator").GetString(),
                valueType = e.GetProperty("value_type").GetString(),
                value = e.GetProperty("value").ValueKind == JsonValueKind.Null ? null : e.GetProperty("value").GetString(),
                lowerBound = e.GetProperty("lower_bound").ValueKind == JsonValueKind.Null ? null : e.GetProperty("lower_bound").GetString(),
                upperBound = e.GetProperty("upper_bound").ValueKind == JsonValueKind.Null ? null : e.GetProperty("upper_bound").GetString(),
            };
        }
    }

    // ---- shared read-only workspace ------------------------------------------------------------------------------------------

    private static readonly SemaphoreSlim SharedGate = new(1, 1);
    private static (Ws W, List<Guid> Records, Guid SharedRecord, Guid ArchivedRecord)? shared;

    /// <summary>One synthetic organization for the read-only tests: 24 varied records (one archived), one shared with the Reader on case_reference only.</summary>
    private async Task<(Ws W, List<Guid> Records, Guid SharedRecord, Guid ArchivedRecord)> SharedAsync()
    {
        await SharedGate.WaitAsync(Ct);
        try
        {
            if (shared is { } existing)
            {
                return existing;
            }

            var w = await WorkspaceAsync("ix3-shared");
            var records = new List<Guid>();
            for (var i = 0; i < 24; i++)
            {
                var reference = i % 3 == 0 ? $"CR-{1000 + i}" : i % 3 == 1 ? $"Ärger-{i:D2} case" : $"cr-{2000 + i} Straße";
                records.Add(await CreateAsync(w, Values(reference, i % 7, $"2026-0{1 + (i % 9)}-1{i % 9}", i % 2 == 0 ? "open" : "closed", i % 4 == 0)));
            }

            var archived = records[5];
            var archive = await fixture.Api.PostAsync($"/api/v1/form-records/{archived:D}/archive", w.S.Token(w.S.Clerk), new { expectedContentRevision = AuthorityOracle.Head(fixture.Stack, archived).ContentRevision, expectedStatus = "Draft", reason = "IX-3 archived record in scope" });
            Assert.True(archive.IsSuccess, archive.Body);
            Assert.True((await ShareAsync(w, records[0], w.S.Reader, "case_reference")).IsSuccess);
            await WaitCompleteAsync(w);
            shared = (w, records, records[0], archived);
            return shared.Value;
        }
        finally
        {
            SharedGate.Release();
        }
    }

    private static object[][] Matrix(Ws w) =>
    [
        [Cond(w.Reference, "equals", "String", "CR-1000")],
        [Cond(w.Reference, "equals", "String", "cr-1000")],
        [Cond(w.Reference, "startsWith", "String", "cr-")],
        [Cond(w.Reference, "startsWith", "String", "ÄRG")],
        [Cond(w.Reference, "contains", "String", "straSSe")],
        [Cond(w.Reference, "contains", "String", "straße")],
        [Cond(w.Reference, "contains", "String", "case")],
        [Cond(w.Reference, "contains", "String", "%_\\")],
        [Cond(w.Count, "equals", "Integer", "3")],
        [Cond(w.Count, "greaterThan", "Integer", "4")],
        [Cond(w.Count, "greaterThanOrEqual", "Integer", "4")],
        [Cond(w.Count, "lessThan", "Integer", "2")],
        [Cond(w.Count, "lessThanOrEqual", "Integer", "2")],
        [Cond(w.Count, "between", "Integer", null, "2", "5")],
        [Cond(w.Hearing, "equals", "Date", "2026-03-12")],
        [Cond(w.Hearing, "before", "Date", "2026-04-01")],
        [Cond(w.Hearing, "after", "Date", "2026-04-01")],
        [Cond(w.Hearing, "between", "Date", null, "2026-02-01", "2026-06-30")],
        [Cond(w.Status, "is", "Option", w.Open.ToString("D"))],
        [Cond(w.Status, "includes", "Option", w.Closed.ToString("D"))],
        [Cond(w.Urgent, "isTrue", "Boolean")],
        [Cond(w.Urgent, "isFalse", "Boolean")],
        [Cond(w.Reference, "startsWith", "String", "cr-"), Cond(w.Count, "greaterThan", "Integer", "1")],
        [Cond(w.Status, "is", "Option", w.Open.ToString("D")), Cond(w.Urgent, "isTrue", "Boolean"), Cond(w.Count, "lessThan", "Integer", "6")],
        [Cond(w.Reference, "equals", "String", "NO-MATCH-ANYWHERE")],
    ];

    // ---- tests ---------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// The IX-3 exit criterion's synthetic part (R1 §G.3): every search of the matrix, for the owner (complete projection) and for the
    /// partial Reader, under each sort, status filter and page size, is answered identically by V1 and the indexed engine — same
    /// ordered ids, same total, or the same error. Zero unexplained differences. The measured N is recorded.
    /// </summary>
    [Fact]
    public async Task ShadowFindsNoUnexplainedDifferenceAcrossTheSyntheticSearchMatrix()
    {
        var (w, _, _, _) = await SharedAsync();
        var clerk = await SessionAsync(w, w.S.Clerk);
        var reader = await SessionAsync(w, w.S.Reader);
        var outcomes = new List<object>();
        var sorts = new (string? By, string? Direction)[] { (null, null), ("createdOn", "asc"), ("recordStatus", "desc"), ("modifiedOn", "asc") };
        var n = 0;
        foreach (var (session, who) in new[] { (clerk, "owner"), (reader, "partial-reader") })
        {
            foreach (var conditions in Matrix(w))
            {
                foreach (var (by, direction) in sorts)
                {
                    foreach (var (status, archived) in new (string?, bool)[] { (null, false), (null, true), ("Submitted", false) })
                    {
                        var shadow = await ShadowAsync(w, session, conditions, status, archived, by, direction);
                        n++;
                        var classification = shadow.GetProperty("classification").GetString();
                        outcomes.Add(new { who, classification, v1 = shadow.GetProperty("verifiedScanTotal").ToString(), indexed = shadow.GetProperty("indexedTotal").ToString(), v1_error = shadow.GetProperty("verifiedScanError").ToString(), indexed_error = shadow.GetProperty("indexedError").ToString() });
                        Assert.True(classification == "Match", $"{who} {JsonSerializer.Serialize(conditions)} sort={by}/{direction} status={status} archived={archived}: {shadow}");
                    }
                }
            }
        }

        var summary = new
        {
            searches = n,
            matches = outcomes.Count,
            unexplained_differences = 0,
            note = "IX-3 synthetic shadow differential (C-132). Local Docker security-test stack. The Stage B search shards were NOT run (C-131); see the evidence report.",
            measured_on = DateTimeOffset.UtcNow,
        };
        Directory.CreateDirectory(Path.Combine(ComposeStack.RepositoryRoot, "artifacts", "ix-3"));
        await File.WriteAllTextAsync(Path.Combine(ComposeStack.RepositoryRoot, "artifacts", "ix-3", "shadow-differential.json"), JsonSerializer.Serialize(summary, Indented), Ct);
    }

    /// <summary>IX-08, IX-09: counts and limits depend on the caller's own records only; matches outside the caller's scope change nothing.</summary>
    [Fact]
    public async Task IX08IX09CountsAndRefusalsDependOnlyOnTheCallersOwnRecords()
    {
        var (w, records, sharedRecord, _) = await SharedAsync();
        var reader = await SessionAsync(w, w.S.Reader);
        var clerk = await SessionAsync(w, w.S.Clerk);
        // Every record matches "exhibit_count >= 0" for the owner; the Reader discovers one record only, readable on case_reference only.
        var all = await IndexedAsync(w, clerk, Nonce(), Query(w, [Cond(w.Count, "greaterThanOrEqual", "Integer", "0")], 100, includeArchived: true));
        Assert.Equal(records.Count, all.Root.GetProperty("total_count").GetInt32());
        var everyReference = await IndexedAsync(w, clerk, Nonce(), Query(w, [Cond(w.Reference, "startsWith", "String", "CR-1")], 100, includeArchived: true));
        Assert.True(everyReference.Root.GetProperty("total_count").GetInt32() > 1, everyReference.Body);
        var mine = await IndexedAsync(w, reader, Nonce(), Query(w, [Cond(w.Reference, "startsWith", "String", "CR-1")], 100, includeArchived: true));
        Assert.Equal([sharedRecord], Ids(mine));
        Assert.Equal(1, mine.Root.GetProperty("total_count").GetInt32());
        Assert.Equal(1, mine.Root.GetProperty("scope_record_count").GetInt32());
        // A predicate nobody's record matches and one that only unauthorized records match look the same to the Reader.
        var unauthorizedOnly = await IndexedAsync(w, reader, Nonce(), Query(w, [Cond(w.Reference, "equals", "String", "CR-1003")]));
        var nowhere = await IndexedAsync(w, reader, Nonce(), Query(w, [Cond(w.Reference, "equals", "String", "NO-SUCH-VALUE")]));
        Assert.Equal(0, unauthorizedOnly.Root.GetProperty("total_count").GetInt32());
        Assert.Equal(0, nowhere.Root.GetProperty("total_count").GetInt32());
        Assert.Equal(unauthorizedOnly.Root.GetProperty("scope_record_count").GetInt32(), nowhere.Root.GetProperty("scope_record_count").GetInt32());
    }

    /// <summary>IX-10, IX-11, IX-36: a partial reader matches only on readable items; on an unreadable field the record is not evaluated (Evaluable parity, incl. the option field).</summary>
    [Fact]
    public async Task IX10IX11IX36PartialAndDiscoverOnlyReadersMatchOnlyOnReadableItems()
    {
        var (w, _, sharedRecord, _) = await SharedAsync();
        var reader = await SessionAsync(w, w.S.Reader);
        Assert.Equal([sharedRecord], Ids(await IndexedAsync(w, reader, Nonce(), Query(w, [Cond(w.Reference, "equals", "String", "CR-1000")]))));
        foreach (var unreadable in new[] { Cond(w.Count, "equals", "Integer", "0"), Cond(w.Status, "is", "Option", w.Open.ToString("D")), Cond(w.Status, "includes", "Option", w.Open.ToString("D")), Cond(w.Urgent, "isTrue", "Boolean") })
        {
            Assert.Empty(Ids(await IndexedAsync(w, reader, Nonce(), Query(w, [unreadable]))));
            Assert.Empty(Ids(await IndexedAsync(w, reader, Nonce(), Query(w, [Cond(w.Reference, "equals", "String", "CR-1000"), unreadable]))));
        }

    }

    /// <summary>IX-12, IX-30: unknown and foreign forms, forged fields, operators, value types and options, and an encrypted field all get the V1 denials.</summary>
    [Fact]
    public async Task IX12IX30ForgedPredicatesAndUnknownFormsGetTheIndistinguishableDenial()
    {
        var (w, _, _, _) = await SharedAsync();
        var clerk = await SessionAsync(w, w.S.Clerk);
        Assert.Equal("SEARCH_FIELD_NOT_ALLOWED", (await IndexedAsync(w, clerk, Nonce(), new { form_definition_id = Guid.CreateVersion7(), conditions = new[] { Cond(w.Reference, "equals", "String", "CR-1000") }, include_children = false, include_archived = false, page_size = 10 })).ErrorCode);
        Assert.Equal("SEARCH_FIELD_NOT_ALLOWED", (await IndexedAsync(w, clerk, Nonce(), Query(w, [Cond(Guid.CreateVersion7(), "equals", "String", "x")]))).ErrorCode);
        Assert.Equal("SEARCH_FIELD_NOT_ALLOWED", (await IndexedAsync(w, clerk, Nonce(), Query(w, [Cond(w.Note, "equals", "String", "note CR-1000")]))).ErrorCode);
        Assert.Equal("SEARCH_FIELD_NOT_ALLOWED", (await IndexedAsync(w, clerk, Nonce(), Query(w, [Cond(w.Count, "equals", "String", "3")]))).ErrorCode);
        Assert.Equal("SEARCH_OPERATOR_NOT_SUPPORTED", (await IndexedAsync(w, clerk, Nonce(), Query(w, [Cond(w.Hearing, "greaterThan", "Date", "2026-01-01")]))).ErrorCode);
        Assert.Equal("SEARCH_OPERATOR_NOT_SUPPORTED", (await IndexedAsync(w, clerk, Nonce(), Query(w, [Cond(w.Status, "is", "Option", Guid.CreateVersion7().ToString("D"))]))).ErrorCode);

        // Another organization's user, with its own valid session, cannot search this form.
        var other = new StageBScenario(fixture, "ix3-other");
        await other.BootstrapAsync();
        var foreignSession = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        Assert.True((await fixture.Authority.SendAsync(HttpMethod.Post, "/internal/v1/sessions", TestIdentity.WorkloadToken(), new { organization_id = other.OrganizationId, session_digest_base64 = foreignSession, absolute_expires_on = DateTimeOffset.UtcNow.AddHours(1) }, other.Token(other.Clerk))).IsSuccess);
        var foreign = await fixture.Authority.SendAsync(HttpMethod.Post, "/internal/v1/search/indexed-query", TestIdentity.WorkloadToken(),
            new { organization_id = other.OrganizationId, session_digest_base64 = foreignSession, query_nonce_base64 = Nonce(), query = Query(w, [Cond(w.Reference, "equals", "String", "CR-1000")]) }, other.Token(other.Clerk));
        Assert.False(foreign.IsSuccess, foreign.Body);
        Assert.Contains(foreign.ErrorCode, new[] { "SEARCH_FIELD_NOT_ALLOWED", "SEARCH_INDEX_NOT_READY" });

        // A session of another identity, or none, is refused before anything else.
        var stolen = await fixture.Authority.SendAsync(HttpMethod.Post, "/internal/v1/search/indexed-query", TestIdentity.WorkloadToken(),
            new { organization_id = w.S.OrganizationId, session_digest_base64 = clerk.Digest, query_nonce_base64 = Nonce(), query = Query(w, [Cond(w.Reference, "equals", "String", "CR-1000")]) }, w.S.Token(w.S.Reader));
        Assert.Equal("SEARCH_SESSION_INVALID", stolen.ErrorCode);
    }

    /// <summary>§8.2 plan-shape test: the candidate statement is driven by the scope, with per-record probes; no plan scans a value index.</summary>
    [Fact]
    public async Task PlanShapeTheCandidateStatementIsScopeDrivenAndNeverScansAValueIndex()
    {
        var (w, records, _, _) = await SharedAsync();
        foreach (var conditions in Matrix(w))
        {
            var plan = await fixture.Authority.SendAsync(HttpMethod.Post, "/internal/v1/testing/search/candidate-plan", TestIdentity.WorkloadToken(),
                new { organization_id = w.S.OrganizationId, scope_record_ids = records, query = Query(w, conditions) });
            Assert.True(plan.IsSuccess, plan.Body);
            var json = plan.Root.GetProperty("plan_json").GetString()!;
            var nodes = new List<JsonElement>();
            void Walk(JsonElement node)
            {
                nodes.Add(node);
                if (node.TryGetProperty("Plans", out var children))
                {
                    foreach (var child in children.EnumerateArray())
                    {
                        Walk(child);
                    }
                }
            }

            Walk(JsonDocument.Parse(json).RootElement[0].GetProperty("Plan"));
            var types = nodes.Select(n => n.GetProperty("Node Type").GetString()!).ToList();
            Assert.DoesNotContain(types, t => t is "Hash Join" or "Merge Join" or "Bitmap Heap Scan" or "Bitmap Index Scan" or "Hash" or "Seq Scan");
            Assert.Contains("Function Scan", types); // the scope array drives the statement
            var indexes = nodes.Where(n => n.TryGetProperty("Index Name", out _)).Select(n => n.GetProperty("Index Name").GetString()!).ToList();
            Assert.All(indexes, index => Assert.True(index.EndsWith("_postings_record", StringComparison.Ordinal) || index.EndsWith("_postings_entry", StringComparison.Ordinal) || index.StartsWith("uq_search_record_entry", StringComparison.Ordinal),
                $"value index or unexpected index in the candidate plan: {index}\n{json}"));
        }
    }

    private static readonly (string Table, string Column)[] PostingValueColumns =
    [
        ("search_string_postings", "normalized_string_value"), ("search_integer_postings", "integer_value"), ("search_decimal_postings", "decimal_value"),
        ("search_date_postings", "date_value"), ("search_date_time_postings", "date_time_value"), ("search_boolean_postings", "boolean_value"), ("search_option_postings", "field_option_id"),
    ];

    /// <summary>
    /// C-135 PD-9 (design §8.2, §22.4), alongside the unchanged plan-shape test above:
    /// <list type="number">
    /// <item>structural — every non-primary posting index is a B-tree keyed (organization_id, form_record_id, …) and contains no value
    /// column (no B-tree value index, no trigram GIN), so no plan can skip-scan a value index;</item>
    /// <item>plans under different value distributions (unique, two values, skewed, Boolean and option) and planner statistics
    /// (as analyzed; n_distinct forced low; n_distinct forced all-distinct; statistics target 1), each with EXPLAIN (ANALYZE, BUFFERS):
    /// the existing node and index rules, and every posting access is an index condition on the record;</item>
    /// <item>an indicator (not a proof) that the probe's work does not grow with out-of-scope data: shared buffers of a fixed 5-record
    /// scope before and after 120 out-of-scope records with distinct values are added.</item>
    /// </list>
    /// The plans and buffers are written to artifacts/ix-4/pd9-plans.json.
    /// </summary>
    [Fact]
    public async Task PD9NoPostingIndexIsValueKeyedAndPlansStayRecordScopedAcrossDistributionsAndStatistics()
    {
        // 1. Structural.
        var definitions = Sql("""
            SELECT i.relname || '|' || am.amname || '|' || x.indisprimary || '|' || pg_get_indexdef(i.oid)
            FROM pg_index x JOIN pg_class i ON i.oid = x.indexrelid JOIN pg_class t ON t.oid = x.indrelid
            JOIN pg_namespace n ON n.oid = t.relnamespace JOIN pg_am am ON am.oid = i.relam
            WHERE n.nspname = 'authority_search' AND t.relname LIKE 'search\_%\_postings' ORDER BY 1;
            """).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        Assert.Equal(PostingValueColumns.Length * 3, definitions.Length); // primary key, record probe index, entry index per table
        foreach (var line in definitions.Where(l => l.Split('|')[2] is "false" or "f"))
        {
            var parts = line.Split('|', 4);
            Assert.Equal("btree", parts[1]);
            Assert.Contains("(organization_id, form_record_id, ", parts[3], StringComparison.Ordinal);
            Assert.DoesNotContain(PostingValueColumns, c => parts[3].Contains(c.Column, StringComparison.Ordinal));
            Assert.True(parts[0].EndsWith("_postings_record", StringComparison.Ordinal) || parts[0].EndsWith("_postings_entry", StringComparison.Ordinal), line);
        }

        // 2. Distributions: unique references, two exhibit counts, a skewed hearing date, Boolean and option values.
        var w = await WorkspaceAsync("ix4-pd9");
        var records = new List<Guid>();
        for (var i = 0; i < 40; i++)
        {
            records.Add(await CreateAsync(w, Values($"P9-{i:D4}", i % 2, i < 36 ? "2026-07-07" : $"2026-0{1 + (i % 9)}-0{1 + (i % 8)}", i % 5 == 0 ? "closed" : "open", i % 3 == 0)));
        }

        await WaitCompleteAsync(w);
        var smallScope = records.Take(5).ToList();
        object[][] matrix =
        [
            [Cond(w.Reference, "equals", "String", "P9-0003")], [Cond(w.Reference, "startsWith", "String", "P9-00")], [Cond(w.Reference, "contains", "String", "-00")],
            [Cond(w.Count, "equals", "Integer", "1")], [Cond(w.Count, "between", "Integer", null, "0", "1")], [Cond(w.Hearing, "equals", "Date", "2026-07-07")],
            [Cond(w.Status, "is", "Option", w.Open.ToString("D"))], [Cond(w.Urgent, "isTrue", "Boolean")],
            [Cond(w.Reference, "startsWith", "String", "P9-"), Cond(w.Count, "equals", "Integer", "0"), Cond(w.Status, "includes", "Option", w.Closed.ToString("D"))],
        ];

        var evidence = new List<object>();
        async Task<long> PlanAsync(string scenario, IReadOnlyList<Guid> scope, object[] conditions)
        {
            var plan = await fixture.Authority.SendAsync(HttpMethod.Post, "/internal/v1/testing/search/candidate-plan", TestIdentity.WorkloadToken(),
                new { organization_id = w.S.OrganizationId, scope_record_ids = scope, query = Query(w, conditions), analyze = true });
            Assert.True(plan.IsSuccess, plan.Body);
            var json = plan.Root.GetProperty("plan_json").GetString()!;
            var nodes = new List<JsonElement>();
            void Walk(JsonElement node)
            {
                nodes.Add(node);
                if (node.TryGetProperty("Plans", out var children))
                {
                    foreach (var child in children.EnumerateArray())
                    {
                        Walk(child);
                    }
                }
            }

            Walk(JsonDocument.Parse(json).RootElement[0].GetProperty("Plan"));
            var types = nodes.Select(n => n.GetProperty("Node Type").GetString()!).ToList();
            Assert.DoesNotContain(types, t => t is "Hash Join" or "Merge Join" or "Bitmap Heap Scan" or "Bitmap Index Scan" or "Hash" or "Seq Scan");
            Assert.Contains("Function Scan", types);
            var indexNodes = nodes.Where(n => n.TryGetProperty("Index Name", out _)).ToList();
            Assert.All(indexNodes, n =>
            {
                var index = n.GetProperty("Index Name").GetString()!;
                Assert.True(index.EndsWith("_postings_record", StringComparison.Ordinal) || index.EndsWith("_postings_entry", StringComparison.Ordinal) || index.StartsWith("uq_search_record_entry", StringComparison.Ordinal), $"{scenario}: unexpected index {index}\n{json}");
                Assert.Contains("form_record_id", n.GetProperty("Index Cond").GetString()!, StringComparison.Ordinal); // every index access is keyed by the record
            });
            long Blocks(JsonElement n) => (n.TryGetProperty("Shared Hit Blocks", out var h) ? h.GetInt64() : 0) + (n.TryGetProperty("Shared Read Blocks", out var r) ? r.GetInt64() : 0);
            var root = JsonDocument.Parse(json).RootElement[0].GetProperty("Plan");
            var buffers = Blocks(root);
            evidence.Add(new { scenario, scope = scope.Count, conditions = conditions.Length, buffers, indexes = indexNodes.Select(n => n.GetProperty("Index Name").GetString()).Distinct().Order().ToList(), node_types = types.Distinct().Order().ToList() });
            return buffers;
        }

        string Statistics(string clause) => string.Join(" ", PostingValueColumns.Select(c => $"ALTER TABLE authority_search.{c.Table} ALTER COLUMN {c.Column} {clause};"));
        var analyze = string.Join(" ", PostingValueColumns.Select(c => $"ANALYZE authority_search.{c.Table};"));
        try
        {
            foreach (var (scenario, setup) in new[]
            {
                ("analyzed", ""),
                ("n_distinct_low", Statistics("SET (n_distinct = 2)")),
                ("n_distinct_all_distinct", Statistics("SET (n_distinct = -1)")),
                ("statistics_target_1", Statistics("RESET (n_distinct)") + " " + Statistics("SET STATISTICS 1")),
            })
            {
                fixture.Stack.AuthorityAdministratorSql(setup + " " + analyze);
                foreach (var conditions in matrix)
                {
                    await PlanAsync(scenario, records, conditions);
                    await PlanAsync(scenario, smallScope, conditions);
                }
            }

            // 3. Indicator: the small scope's work before and after out-of-scope records with many distinct values are added.
            fixture.Stack.AuthorityAdministratorSql(Statistics("RESET (n_distinct)") + " " + Statistics("SET STATISTICS -1") + " " + analyze);
            object[] probe = [Cond(w.Reference, "startsWith", "String", "P9-00")];
            var before = await PlanAsync("indicator_before", smallScope, probe);
            for (var i = 0; i < 120; i++)
            {
                await CreateAsync(w, Values($"P9-{1000 + i:D4}-{Guid.NewGuid():N}", 100 + i, $"2027-0{1 + (i % 9)}-1{i % 9}", "open", i % 2 == 0));
            }

            await WaitCompleteAsync(w);
            fixture.Stack.AuthorityAdministratorSql(analyze);
            var after = await PlanAsync("indicator_after_120_out_of_scope", smallScope, probe);
            Assert.True(after <= before * 2 + 20, $"the 5-record probe read {before} blocks before and {after} after out-of-scope records were added");
        }
        finally
        {
            fixture.Stack.AuthorityAdministratorSql(Statistics("RESET (n_distinct)") + " " + Statistics("SET STATISTICS -1") + " " + analyze);
        }

        Directory.CreateDirectory(Path.Combine(ComposeStack.RepositoryRoot, "artifacts", "ix-4"));
        await File.WriteAllTextAsync(Path.Combine(ComposeStack.RepositoryRoot, "artifacts", "ix-4", "pd9-plans.json"), JsonSerializer.Serialize(new { measured_on = DateTimeOffset.UtcNow, git_commit = ComposeStack.GitCommit(), indexes = definitions, plans = evidence }, Indented), Ct);
    }

    /// <summary>IX-20, IX-78: a revocation between pages deletes the organization's older snapshots in its own transaction; the next page is refused.</summary>
    [Fact]
    public async Task IX20IX78ARevocationBetweenPagesDeletesTheSnapshotAndRefusesThePage()
    {
        var w = await WorkspaceAsync("ix3-revocation");
        for (var i = 0; i < 12; i++)
        {
            await CreateAsync(w, Values($"REV-{i:D2}", i, "2026-02-02", "open", false));
        }

        await WaitCompleteAsync(w);
        var clerk = await SessionAsync(w, w.S.Clerk);
        var nonce = Nonce();
        var query = Query(w, [Cond(w.Reference, "startsWith", "String", "REV-")], 10);
        var first = await IndexedAsync(w, clerk, nonce, query);
        Assert.Equal(12, first.Root.GetProperty("total_count").GetInt32());
        Assert.Equal(10, Ids(first).Count);
        var snapshot = SnapshotId(first);
        Assert.Equal(1, Snapshots(w.S.OrganizationId));
        // Page 2 is served while nothing changed.
        var second = await PageAsync(w, clerk, nonce, query, snapshot, 10);
        Assert.Equal(2, Ids(second).Count);
        Assert.Empty(Ids(first).Intersect(Ids(second)));

        // Any authorization change (here a share) advances the revision: the snapshot is deleted in that transaction.
        Assert.True((await ShareAsync(w, Ids(first)[0], w.S.Reader, "case_reference")).IsSuccess);
        Assert.Equal(0, Snapshots(w.S.OrganizationId));
        AssertNoPage(await PageAsync(w, clerk, nonce, query, snapshot, 10), "SEARCH_SNAPSHOT_NOT_FOUND");
    }

    /// <summary>IX-79: logout (session revocation) deletes the session's snapshots in the revoking transaction; IX-80: expiry and idle timeout are checked at page time without any revoking transaction.</summary>
    [Fact]
    public async Task IX79IX80SessionEndAndSessionTimeoutsRefuseThePage()
    {
        var (w, _, _, _) = await SharedAsync();
        var query = Query(w, [Cond(w.Count, "greaterThanOrEqual", "Integer", "0")], 10, includeArchived: true);

        var loggedOut = await SessionAsync(w, w.S.Clerk);
        var nonce = Nonce();
        var snapshot = SnapshotId(await IndexedAsync(w, loggedOut, nonce, query));
        Assert.True((await fixture.Authority.SendAsync(HttpMethod.Post, "/internal/v1/sessions/revoke", TestIdentity.WorkloadToken(), new { organization_id = w.S.OrganizationId, session_digest_base64 = loggedOut.Digest }, w.S.Token(w.S.Clerk))).IsSuccess);
        Assert.Equal("0", Scalar($"SELECT count(*) FROM authority_search.search_result_snapshots WHERE search_result_snapshot_id = '{snapshot:D}';"));
        AssertNoPage(await PageAsync(w, loggedOut, nonce, query, snapshot, 10), "SEARCH_SESSION_INVALID");

        foreach (var column in new[] { "session_last_seen_on = now() - interval '2 days'", "session_expires_on = session_created_on + interval '1 millisecond'" })
        {
            var session = await SessionAsync(w, w.S.Clerk);
            var n = Nonce();
            var id = SnapshotId(await IndexedAsync(w, session, n, query));
            var digestHex = Convert.ToHexStringLower(Convert.FromBase64String(session.Digest));
            fixture.Stack.AuthorityAdministratorSql($"UPDATE authority.authenticated_sessions SET {column} WHERE session_secret_digest = '\\x{digestHex}'::bytea;");
            Assert.Equal("1", Scalar($"SELECT count(*) FROM authority_search.search_result_snapshots WHERE search_result_snapshot_id = '{id:D}';")); // no revoking transaction ran
            AssertNoPage(await PageAsync(w, session, n, query, id, 10), "SEARCH_SESSION_INVALID");
        }
    }

    /// <summary>IX-81, IX-22: searchability changes and a commit to a record of the form between pages invalidate the snapshot.</summary>
    [Fact]
    public async Task IX81IX22SearchabilityChangesAndCommitsBetweenPagesInvalidate()
    {
        var w = await WorkspaceAsync("ix3-searchability");
        var records = new List<Guid>();
        for (var i = 0; i < 12; i++)
        {
            records.Add(await CreateAsync(w, Values($"SCH-{i:D2}", i, "2026-02-02", "open", false)));
        }

        await WaitCompleteAsync(w);
        var clerk = await SessionAsync(w, w.S.Clerk);
        var nonce = Nonce();
        var query = Query(w, [Cond(w.Reference, "startsWith", "String", "SCH-"), Cond(w.Count, "greaterThan", "Integer", "-1")], 10);

        // A commit to any record of the form advances form_content_revision: the next page is refused (409) and the snapshot deleted.
        var snapshot = SnapshotId(await IndexedAsync(w, clerk, nonce, query));
        Assert.True((await EditAsync(w, records[3], new Dictionary<string, string>(StringComparer.Ordinal) { ["case_reference"] = "SCH-03b" })).IsSuccess);
        AssertNoPage(await PageAsync(w, clerk, nonce, query, snapshot, 10), "SEARCH_SNAPSHOT_CHANGED");
        Assert.Equal("0", Scalar($"SELECT count(*) FROM authority_search.search_result_snapshots WHERE search_result_snapshot_id = '{snapshot:D}';"));

        // Withdrawing a predicate field's approval deletes the form's snapshots in the AC-6 transaction.
        snapshot = SnapshotId(await IndexedAsync(w, clerk, nonce, query));
        var withdrawn = await fixture.Api.PostAsync($"/api/v1/form-definitions/{w.FormId:D}/fields/{w.Count:D}/search-approval/withdraw", w.S.Token(w.S.Administrator), new { reason = "IX-3 withdrawal between pages" });
        Assert.True(withdrawn.IsSuccess, withdrawn.Body);
        Assert.Equal("0", Scalar($"SELECT count(*) FROM authority_search.search_result_snapshots WHERE search_result_snapshot_id = '{snapshot:D}';"));
        AssertNoPage(await PageAsync(w, clerk, nonce, query, snapshot, 10), "SEARCH_SNAPSHOT_NOT_FOUND");
        Assert.Equal("SEARCH_FIELD_NOT_ALLOWED", (await IndexedAsync(w, clerk, Nonce(), query)).ErrorCode);
    }

    /// <summary>IX-23, IX-43: an S1 organization quarantine refuses search and pages with 423 and purges the snapshots (never a search-index state).</summary>
    [Fact]
    public async Task IX23IX43AnOrganizationQuarantineRefusesSearchAndPagesWith423()
    {
        var w = await WorkspaceAsync("ix3-s1");
        await CreateAsync(w, Values("S1-01", 1, "2026-02-02", "open", false));
        await WaitCompleteAsync(w);
        var clerk = await SessionAsync(w, w.S.Clerk);
        var nonce = Nonce();
        var query = Query(w, [Cond(w.Reference, "startsWith", "String", "S1-")]);
        var snapshot = SnapshotId(await IndexedAsync(w, clerk, nonce, query));
        fixture.Stack.AuthorityAdministratorSql($"UPDATE authority.organization_authorization_states SET integrity_quarantine_enabled = true, integrity_quarantine_reason_code = 'IX3_TEST_QUARANTINE' WHERE organization_id = '{w.S.OrganizationId:D}';");
        Assert.Equal(0, Snapshots(w.S.OrganizationId));
        Assert.Equal("INTEGRITY_QUARANTINE", (await IndexedAsync(w, clerk, Nonce(), query)).ErrorCode);
        AssertNoPage(await PageAsync(w, clerk, nonce, query, snapshot, 0), "INTEGRITY_QUARANTINE"); // Q1: the gate answers before the (purged) snapshot
        // The V1 fallback path is refused by the same gate (Q2): the business search over the same form.
        var v1 = await fixture.Api.PostAsync("/api/v1/form-records/field-search", w.S.Token(w.S.Clerk), new { formDefinitionId = w.FormId, conditions = new[] { new { fieldDefinitionId = w.Reference, @operator = "startsWith", valueType = "String", value = "S1-" } }, pageSize = 25 });
        Assert.False(v1.IsSuccess, v1.Body);
        Assert.StartsWith("Active", Projection(w.S.OrganizationId), StringComparison.Ordinal); // the search index state is untouched (Q3)
    }

    /// <summary>IX-24 and D-IX2-7 rule 1 (R-18): a quarantined record in scope fails the whole search; it never appears; the index is not quarantined for it.</summary>
    [Fact]
    public async Task IX24AQuarantinedRecordInScopeFailsTheWholeSearchAndNeverAppears()
    {
        var w = await WorkspaceAsync("ix3-record-quarantine");
        var a = await CreateAsync(w, Values("RQ-01", 1, "2026-02-02", "open", false));
        await CreateAsync(w, Values("RQ-02", 2, "2026-02-02", "open", false));
        await WaitCompleteAsync(w);
        var clerk = await SessionAsync(w, w.S.Clerk);
        Assert.Equal(2, (await IndexedAsync(w, clerk, Nonce(), Query(w, [Cond(w.Reference, "startsWith", "String", "RQ-")]))).Root.GetProperty("total_count").GetInt32());
        fixture.Stack.AuthorityAdministratorSql($"UPDATE authority.record_content_heads SET content_head_quarantined = true, quarantine_reason_code = 'IX3_TEST' WHERE form_record_id = '{a:D}';");
        foreach (var conditions in new[] { new[] { Cond(w.Reference, "startsWith", "String", "RQ-") }, new[] { Cond(w.Reference, "equals", "String", "RQ-02") } })
        {
            AssertNoPage(await IndexedAsync(w, clerk, Nonce(), Query(w, conditions)), "RECORD_QUARANTINED");
        }

        Assert.StartsWith("Active|", Projection(w.S.OrganizationId), StringComparison.Ordinal);
    }

    /// <summary>
    /// IX-44, IX-34, I-2 and the seal: an Authority-domain change to a posting value (I-2), or the deletion of a page record's posting
    /// (seal: posting count), fails the search, quarantines the search index only (503, never 423), and leaves record reads working.
    /// </summary>
    [Theory]
    [InlineData("posting-value")]
    [InlineData("posting-deleted")]
    public async Task IX44IX34AnAuthorityDomainPostingChangeQuarantinesOnlyTheSearchIndex(string tampering)
    {
        var w = await WorkspaceAsync("ix3-tamper-" + tampering);
        var a = await CreateAsync(w, Values("TP-01", 1, "2026-02-02", "open", false));
        await CreateAsync(w, Values("TP-02", 2, "2026-02-02", "open", false));
        await WaitCompleteAsync(w);
        var clerk = await SessionAsync(w, w.S.Clerk);
        var query = Query(w, [Cond(w.Reference, "startsWith", "String", "TP-")]);
        Assert.Equal(2, (await IndexedAsync(w, clerk, Nonce(), query)).Root.GetProperty("total_count").GetInt32());
        var sql = tampering == "posting-value"
            ? $"SELECT set_config('authority_search.organization_id', '{w.S.OrganizationId:D}', false); UPDATE authority_search.search_string_postings SET normalized_string_value = 'TP-99' WHERE form_record_id = '{a:D}';"
            : $"SELECT set_config('authority_search.organization_id', '{w.S.OrganizationId:D}', false); DELETE FROM authority_search.search_integer_postings WHERE form_record_id = '{a:D}';";
        fixture.Stack.AuthorityAdministratorSql(sql);
        AssertNoPage(await IndexedAsync(w, clerk, Nonce(), query), "SEARCH_INDEX_INCONSISTENT");
        Assert.StartsWith("Quarantined|", Projection(w.S.OrganizationId), StringComparison.Ordinal);
        Assert.Equal("f", Scalar($"SELECT integrity_quarantine_enabled FROM organization_authorization_states WHERE organization_id = '{w.S.OrganizationId:D}';"));
        Assert.True((await w.S.WaitProjectionAsync(w.S.Clerk, a)).IsSuccess); // record reads unaffected (IX-44)
        AssertNoPage(await IndexedAsync(w, clerk, Nonce(), query), "SEARCH_INDEX_INCONSISTENT");
    }

    /// <summary>IX-47, IX-48: an expired snapshot is refused and deleted, the purge job removes expired ones, and a fourth snapshot of a session evicts the oldest.</summary>
    [Fact]
    public async Task IX47IX48ExpiryPurgeAndPerSessionEviction()
    {
        var (w, _, _, _) = await SharedAsync();
        var session = await SessionAsync(w, w.S.Clerk);
        var query = Query(w, [Cond(w.Count, "greaterThanOrEqual", "Integer", "0")], 10, includeArchived: true);
        var nonces = Enumerable.Range(0, 4).Select(_ => Nonce()).ToList();
        var ids = new List<Guid>();
        foreach (var n in nonces)
        {
            ids.Add(SnapshotId(await IndexedAsync(w, session, n, query)));
        }

        Assert.Equal("0", Scalar($"SELECT count(*) FROM authority_search.search_result_snapshots WHERE search_result_snapshot_id = '{ids[0]:D}';"));
        AssertNoPage(await PageAsync(w, session, nonces[0], query, ids[0], 10), "SEARCH_SNAPSHOT_NOT_FOUND");
        Assert.True((await PageAsync(w, session, nonces[3], query, ids[3], 10)).IsSuccess);

        fixture.Stack.AuthorityAdministratorSql($"UPDATE authority_search.search_result_snapshots SET snapshot_expires_on = snapshot_created_on + interval '1 millisecond' WHERE search_result_snapshot_id IN ('{ids[1]:D}', '{ids[2]:D}');");
        AssertNoPage(await PageAsync(w, session, nonces[1], query, ids[1], 10), "SEARCH_SNAPSHOT_CHANGED");
        Assert.Equal("0", Scalar($"SELECT count(*) FROM authority_search.search_result_snapshots WHERE search_result_snapshot_id = '{ids[1]:D}';"));
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (Scalar($"SELECT count(*) FROM authority_search.search_result_snapshots WHERE search_result_snapshot_id = '{ids[2]:D}';") != "0" && DateTime.UtcNow < deadline)
        {
            await Task.Delay(1000, Ct);
        }

        Assert.Equal("0", Scalar($"SELECT count(*) FROM authority_search.search_result_snapshots WHERE search_result_snapshot_id = '{ids[2]:D}';"));
    }

    /// <summary>IX-49, IX-50: snapshots hold record ids and positions only; the digest is bound to the nonce and the query (another nonce or query: not found).</summary>
    [Fact]
    public async Task IX49IX50SnapshotsHoldNoValuesAndTheDigestBindsTheNonceAndQuery()
    {
        var (w, _, _, _) = await SharedAsync();
        var session = await SessionAsync(w, w.S.Clerk);
        var nonce = Nonce();
        var query = Query(w, [Cond(w.Reference, "contains", "String", "Straße")], 10);
        var first = await IndexedAsync(w, session, nonce, query);
        var snapshot = SnapshotId(first);
        var row = Sql($"SELECT row_to_json(s)::text FROM authority_search.search_result_snapshots s WHERE search_result_snapshot_id = '{snapshot:D}';") + Sql($"SELECT coalesce(json_agg(e)::text, '') FROM authority_search.search_result_snapshot_entries e WHERE search_result_snapshot_id = '{snapshot:D}';");
        Assert.DoesNotContain("STRASSE", row.ToUpperInvariant(), StringComparison.Ordinal);
        Assert.DoesNotContain("Straße", row, StringComparison.Ordinal);
        Assert.Equal("32", Scalar($"SELECT octet_length(query_digest) FROM authority_search.search_result_snapshots WHERE search_result_snapshot_id = '{snapshot:D}';"));
        Assert.True((await PageAsync(w, session, nonce, query, snapshot, 0)).IsSuccess);
        AssertNoPage(await PageAsync(w, session, Nonce(), query, snapshot, 0), "SEARCH_SNAPSHOT_NOT_FOUND");
        AssertNoPage(await PageAsync(w, session, nonce, Query(w, [Cond(w.Reference, "contains", "String", "Strasse")], 10), snapshot, 0), "SEARCH_SNAPSHOT_NOT_FOUND");
    }

    /// <summary>IX-55: a failed snapshot deletion aborts the revoking transaction (X2): no revocation is committed with its snapshots left in place.</summary>
    [Fact]
    public async Task IX55AFailedSnapshotDeletionAbortsTheRevokingTransaction()
    {
        var w = await WorkspaceAsync("ix3-x2");
        var a = await CreateAsync(w, Values("X2-01", 1, "2026-02-02", "open", false));
        await WaitCompleteAsync(w);
        var session = await SessionAsync(w, w.S.Clerk);
        var snapshot = SnapshotId(await IndexedAsync(w, session, Nonce(), Query(w, [Cond(w.Reference, "startsWith", "String", "X2-")])));
        var runtime = Scalar("SELECT rolname FROM pg_roles WHERE rolname NOT LIKE 'pg_%' AND rolcanlogin AND NOT rolsuper AND rolname NOT LIKE 'authority_search_reader%' ORDER BY rolname LIMIT 1;");
        var revisionBefore = Scalar($"SELECT authorization_revision FROM organization_authorization_states WHERE organization_id = '{w.S.OrganizationId:D}';");
        try
        {
            fixture.Stack.AuthorityAdministratorSql($"REVOKE DELETE ON authority_search.search_result_snapshots FROM {runtime};");
            var share = await fixture.Api.PostAsync($"/api/v1/form-records/{a:D}/shares", w.S.Token(w.S.Clerk), new { recipientUserId = w.S.Reader.AuthorityUserId, recipientTeamId = (Guid?)null, recipientRole = "Reader", fieldKeys = new[] { "case_reference" } });
            Assert.False(share.IsSuccess, share.Body);
            Assert.Equal(revisionBefore, Scalar($"SELECT authorization_revision FROM organization_authorization_states WHERE organization_id = '{w.S.OrganizationId:D}';"));
            Assert.Equal("1", Scalar($"SELECT count(*) FROM authority_search.search_result_snapshots WHERE search_result_snapshot_id = '{snapshot:D}';"));
        }
        finally
        {
            fixture.Stack.AuthorityAdministratorSql($"GRANT DELETE ON authority_search.search_result_snapshots TO {runtime};");
        }

        // With the privilege restored, an authorization change commits and deletes the snapshot (a different recipient: the refused
        // command may still be completed later by the business outbox, which is not under test here).
        Assert.True((await ShareAsync(w, a, w.S.Designer, "case_reference")).IsSuccess);
        Assert.Equal(0, Snapshots(w.S.OrganizationId));
    }

    /// <summary>
    /// C-133 PD-3: an S1 organization quarantine whose stored-result deletion fails (X2, trigger in the quarantine transaction) is
    /// never reported as success and releases nothing: the transaction rolls back (organization not quarantined, snapshot kept),
    /// the page, a new indexed search and the V1 path are all refused, the failure raises a Critical alert, and once the deletion
    /// can succeed the next request commits the quarantine (423) and the snapshots are deleted. The mismatch is an AuthorityDb
    /// state naming a foreign journal history at the same authorization revision, so the page reaches its S1 pin (the
    /// snapshot's revision check passes) and the quarantine is attempted on the page path itself.
    /// </summary>
    [Fact]
    public async Task PD3AnS1QuarantineWhoseSnapshotDeletionFailsReleasesNothingAndIsRetried()
    {
        var w = await WorkspaceAsync("ix3-pd3");
        for (var i = 0; i < 12; i++)
        {
            await CreateAsync(w, Values($"PD-{i:D2}", i, "2026-02-02", "open", false));
        }

        await WaitCompleteAsync(w);
        var clerk = await SessionAsync(w, w.S.Clerk);
        var nonce = Nonce();
        var query = Query(w, [Cond(w.Reference, "startsWith", "String", "PD-")]);
        var first = await IndexedAsync(w, clerk, nonce, query);
        Assert.Equal(12, first.Root.GetProperty("total_count").GetInt32());
        var snapshot = SnapshotId(first);
        var org = w.S.OrganizationId;
        var runtime = Scalar("SELECT rolname FROM pg_roles WHERE rolname NOT LIKE 'pg_%' AND rolcanlogin AND NOT rolsuper AND rolname NOT LIKE 'authority_search_reader%' ORDER BY rolname LIMIT 1;");
        string Quarantine() => Scalar($"SELECT integrity_quarantine_enabled::text || '|' || coalesce(integrity_quarantine_reason_code, '') FROM organization_authorization_states WHERE organization_id = '{org:D}';");
        var alert = $"SECURITY ALERT {ProjectionIntegrityAlertNotCommitted} organization {org:D}";
        try
        {
            fixture.Stack.AuthorityAdministratorSql($"REVOKE DELETE ON authority_search.search_result_snapshots FROM {runtime};");
            // The AuthorityDb state names another journal history at the same revision (a restore from a foreign backup): the gate
            // and the snapshot's revision check (check 4) still pass, so the page reaches its S1 pin, which confirms the mismatch.
            fixture.Stack.AuthorityAdministratorSql($"UPDATE organization_authorization_states SET latest_security_journal_digest = decode(repeat('ab', 32), 'hex') WHERE organization_id = '{org:D}';");
            Assert.Equal(1, Snapshots(org));

            // The page: its S1 pin confirms the mismatch and the quarantine transaction fails on the deletion (alerted on this request).
            int Alerts() => fixture.Stack.Logs("authority", 20000).Split('\n').Count(l => l.Contains(alert, StringComparison.Ordinal));
            var alertsBefore = Alerts();
            var page = await PageAsync(w, clerk, nonce, query, snapshot, 10);
            Assert.True(Alerts() > alertsBefore, "the failed quarantine of the page request raised the Critical alert");
            Assert.False(page.IsSuccess, page.Body);
            Assert.True((int)page.Status >= 500, page.Body); // a store failure (503 AUTHORITY_NOT_READY), never 200 or 423
            Assert.DoesNotContain("form_record_id", page.Body, StringComparison.Ordinal);
            Assert.Equal("false|", Quarantine()); // rolled back: no partial quarantine was committed
            Assert.Equal(1, Snapshots(org)); // and nothing of it either

            // A new indexed search and the complete V1 path are refused the same way (no data, no success).
            var search = await fixture.Authority.SendAsync(HttpMethod.Post, "/internal/v1/search/indexed-query", TestIdentity.WorkloadToken(),
                new { organization_id = org, session_digest_base64 = clerk.Digest, query_nonce_base64 = Nonce(), query }, w.S.Token(clerk.User));
            Assert.False(search.IsSuccess, search.Body);
            Assert.DoesNotContain("form_record_id", search.Body, StringComparison.Ordinal);
            var v1 = await fixture.Api.PostAsync("/api/v1/form-records/field-search", w.S.Token(w.S.Clerk), new { formDefinitionId = w.FormId, conditions = new[] { new { fieldDefinitionId = w.Reference, @operator = "startsWith", valueType = "String", value = "PD-" } }, pageSize = 25 });
            Assert.False(v1.IsSuccess, v1.Body);
            Assert.DoesNotContain("formRecordId", v1.Body, StringComparison.Ordinal);
            Assert.Equal("false|", Quarantine());

            // Observable: a Critical alert names the failed quarantine (organization id only; no subject or record ids).
            Assert.Contains(alert, fixture.Stack.Logs("authority", 5000), StringComparison.Ordinal);
        }
        finally
        {
            fixture.Stack.AuthorityAdministratorSql($"GRANT DELETE ON authority_search.search_result_snapshots TO {runtime};");
        }

        // Retry: the next request confirms the persistent mismatch again and the quarantine commits, deleting the snapshots.
        var retried = await PageAsync(w, clerk, nonce, query, snapshot, 10);
        AssertNoPage(retried, "INTEGRITY_QUARANTINE");
        Assert.Equal("true|PROJECTION_FOREIGN_HISTORY", Quarantine());
        Assert.Equal(0, Snapshots(org));
        Assert.Equal("INTEGRITY_QUARANTINE", (await IndexedAsync(w, clerk, Nonce(), query)).ErrorCode);
    }

    private const string ProjectionIntegrityAlertNotCommitted = "PROJECTION_INTEGRITY_QUARANTINE_NOT_COMMITTED";

    /// <summary>IX-82: concurrent page requests on one snapshot, racing a revocation, each return a complete correct page or a typed refusal — never a partial or mixed page.</summary>
    [Fact]
    public async Task IX82ConcurrentPagesRacingARevocationAreCompleteOrRefusedNeverPartial()
    {
        var w = await WorkspaceAsync("ix3-concurrency");
        for (var i = 0; i < 30; i++)
        {
            await CreateAsync(w, Values($"CC-{i:D2}", i, "2026-02-02", "open", false));
        }

        await WaitCompleteAsync(w);
        var session = await SessionAsync(w, w.S.Clerk);
        var nonce = Nonce();
        var query = Query(w, [Cond(w.Reference, "startsWith", "String", "CC-")], 10);
        var first = await IndexedAsync(w, session, nonce, query);
        var snapshot = SnapshotId(first);
        var expected = new Dictionary<int, List<Guid>> { [0] = Ids(first), [10] = Ids(await PageAsync(w, session, nonce, query, snapshot, 10)), [20] = Ids(await PageAsync(w, session, nonce, query, snapshot, 20)) };
        var pages = Enumerable.Range(0, 24).Select(i => PageAsync(w, session, nonce, query, snapshot, (i % 3) * 10)).ToList();
        var revoke = ShareAsync(w, expected[0][0], w.S.Reader, "case_reference");
        var results = await Task.WhenAll(pages);
        Assert.True((await revoke).IsSuccess);
        for (var i = 0; i < results.Length; i++)
        {
            var r = results[i];
            if (r.IsSuccess)
            {
                Assert.Equal(expected[(i % 3) * 10], Ids(r));
            }
            else
            {
                Assert.Contains(r.ErrorCode, new[] { "SEARCH_SNAPSHOT_CHANGED", "SEARCH_SNAPSHOT_NOT_FOUND", "AUTHORITY_NOT_READY" });
                Assert.DoesNotContain("form_record_id", r.Body, StringComparison.Ordinal);
            }
        }

        AssertNoPage(await PageAsync(w, session, nonce, query, snapshot, 0), "SEARCH_SNAPSHOT_NOT_FOUND");
    }

    /// <summary>
    /// IX-31/32 under IX-D3 (R-A): business bytes of a matching record are altered. V1 fails the whole search; the indexed engine
    /// answers from Authority-accepted content. The shadow classifies it as the documented legitimate difference, not an unexplained one.
    /// </summary>
    [Fact]
    public async Task ShadowClassifiesBusinessCopyDamageAsTheDocumentedLegitimateDifference()
    {
        var w = await WorkspaceAsync("ix3-business-damage");
        var a = await CreateAsync(w, Values("BD-01", 1, "2026-02-02", "open", false));
        await CreateAsync(w, Values("BD-02", 2, "2026-02-02", "open", false));
        await WaitCompleteAsync(w);
        var session = await SessionAsync(w, w.S.Clerk);
        var conditions = new[] { Cond(w.Reference, "startsWith", "String", "BD-") };
        Assert.Equal("Match", (await ShadowAsync(w, session, conditions)).GetProperty("classification").GetString());
        fixture.Stack.AttackerSql($"UPDATE field_integer_values SET integer_value = 77 WHERE form_record_id = '{a:D}';");
        var shadow = await ShadowAsync(w, session, conditions);
        Assert.True(shadow.GetProperty("classification").GetString() == "LegitimateBusinessCopyNotVerified", shadow.ToString());
        // V1 failed on the damaged business copy (and its verification quarantined the record); the indexed engine either answered from
        // accepted content (both records) or, having run after the quarantine, refused the scope (D3 parity).
        Assert.Contains("RECORD_VERIFICATION_FAILED", shadow.GetProperty("verifiedScanError").GetString(), StringComparison.Ordinal);
        Assert.True(shadow.GetProperty("indexedError").GetString() == "RECORD_QUARANTINED" || shadow.GetProperty("indexedTotal").GetInt32() == 2, shadow.ToString());
    }

    /// <summary>§5.1, IX-42: the reader login is SELECT-only and read-only; it reads no other Authority table; the runtime cannot read through it.</summary>
    [Fact]
    public void TheReaderLoginIsSelectOnlyReadOnlyAndConfinedToItsTables()
    {
        // SET ROLE checks the reader's privileges (role settings such as the read-only default apply at login, checked separately below).
        var asReader = (string sql) => fixture.Stack.AuthorityAdministratorSql("SET ROLE authority_search_reader_login; " + sql, throwOnError: false);
        foreach (var write in new[]
                 {
                     "INSERT INTO authority_search.search_result_snapshot_entries (search_result_snapshot_entry_id, search_result_snapshot_id, organization_id, result_entry_position, form_record_id, indexed_operation_id) VALUES (gen_random_uuid(), gen_random_uuid(), gen_random_uuid(), 0, gen_random_uuid(), gen_random_uuid());",
                     "DELETE FROM authority_search.search_projection_states;",
                     "UPDATE authority.record_content_heads SET content_head_quarantined = true;",
                     "SELECT count(*) FROM authority.identity_bindings;",
                     "SELECT count(*) FROM authority.security_journal_entries;",
                     "SELECT count(*) FROM authority.authenticated_sessions;",
                 })
        {
            Assert.Contains("permission denied", asReader(write), StringComparison.OrdinalIgnoreCase);
        }

        Assert.DoesNotContain("permission denied", asReader("SELECT count(*) FROM authority_search.search_record_entries;"), StringComparison.OrdinalIgnoreCase);
        var settings = Scalar("SELECT array_to_string(s.setconfig, ',') FROM pg_db_role_setting s JOIN pg_roles r ON r.oid = s.setrole WHERE r.rolname = 'authority_search_reader_login';");
        Assert.Contains("default_transaction_read_only=on", settings, StringComparison.Ordinal);
        Assert.Contains("log_parameter_max_length=0", settings, StringComparison.Ordinal);
        Assert.Equal("false|false|false|false|true", Scalar("SELECT rolsuper || '|' || rolbypassrls || '|' || rolcreaterole || '|' || rolcreatedb || '|' || rolcanlogin FROM pg_roles WHERE rolname = 'authority_search_reader_login';"));
        Assert.Equal("authority_search_reader", Scalar("SELECT string_agg(g.rolname, ',') FROM pg_auth_members m JOIN pg_roles g ON g.oid = m.roleid JOIN pg_roles u ON u.oid = m.member WHERE u.rolname = 'authority_search_reader_login';"));
    }

    /// <summary>IX-13 (measure and report, R1 §F.2): indexed search latency for a small caller scope while unauthorized matches grow. Report only.</summary>
    [Fact]
    public async Task IX13TimingReportForASmallScopeWhileUnauthorizedMatchesGrow()
    {
        var (w, _, _, _) = await SharedAsync();
        var reader = await SessionAsync(w, w.S.Reader);
        var timings = new List<double>();
        for (var i = 0; i < 20; i++)
        {
            var started = System.Diagnostics.Stopwatch.GetTimestamp();
            Assert.True((await IndexedAsync(w, reader, Nonce(), Query(w, [Cond(w.Count, "greaterThanOrEqual", "Integer", "0")]))).IsSuccess);
            timings.Add(System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        }

        timings.Sort();
        var report = new { scope_records = 1, organization_records = 24, searches = timings.Count, p50_ms = timings[timings.Count / 2], p95_ms = timings[(int)(timings.Count * 0.95) - 1], note = "IX-13 report only (local Docker, small data). The scope-driven plan (plan-shape test) is the control; not a PERF-01 result.", measured_on = DateTimeOffset.UtcNow };
        Directory.CreateDirectory(Path.Combine(ComposeStack.RepositoryRoot, "artifacts", "ix-3"));
        await File.WriteAllTextAsync(Path.Combine(ComposeStack.RepositoryRoot, "artifacts", "ix-3", "timing-report.json"), JsonSerializer.Serialize(report, Indented), Ct);
    }
}
