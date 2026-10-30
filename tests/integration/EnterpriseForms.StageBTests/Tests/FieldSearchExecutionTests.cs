using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using EnterpriseForms.SecurityTests.Harness;
using EnterpriseForms.StageBTests.Harness;
using Xunit;

namespace EnterpriseForms.StageBTests.Tests;

/// <summary>
/// Phase C7 M3 — verified field-value search on the real stack (business API, Authorization Authority, SpiceDB, Vault
/// Transit, both PostgreSQL servers). Approved plaintext fields are searched over the Authority's catalogue of the caller's
/// discoverable records, every record verified against its accepted head; hostile business-database changes (tampered,
/// deleted, substituted, injected or rolled-back rows) fail the whole search with no partial result; revocation ends open
/// pages; pending creations never block; overload is refused as busy; predicate values never reach URLs or logs. Each
/// tampering scenario uses its own organization because a failed verification quarantines the record.
/// </summary>
[Collection(StageBStackCollectionDefinition.Name)]
public sealed class FieldSearchExecutionTests(StageBFixture fixture)
{
    private sealed record Ws(StageBScenario S, Guid FormId, Guid Reference, Guid Count, Guid Hearing, Guid Status, Guid Open, Guid Closed, Guid Urgent, Guid Note);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<Ws> WorkspaceAsync(string name)
    {
        var s = new StageBScenario(fixture, name);
        await s.BootstrapAsync();
        var current = await fixture.Api.GetAsync("/api/v1/organization/security-policy", s.Token(s.Designer));
        var policy = await fixture.Api.SendAsync(HttpMethod.Put, "/api/v1/organization/security-policy", s.Token(s.Designer), new { minimumFieldClassification = "Internal", plaintextInternalFieldsPermitted = true, plaintextDowngradePermitted = false, plaintextSearchPermitted = true, policyStatementText = "Selected plaintext Internal fields may be searched in this synthetic organization.", expectedPolicyRevision = current.Root.GetProperty("securityPolicyRevision").GetInt64() }, Guid.NewGuid().ToString("N"));
        Assert.True(policy.IsSuccess, policy.Body);
        var formId = await s.CreateFormAsync("Search exec " + s.Name, s.Clerk);
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
        await s.SaveDraftAsync(formId, StageBScenario.Layout("Search exec", StageBScenario.Section("main", "Main",
            StageBScenario.Field(reference, "text", "Case reference", 2), StageBScenario.Field(count, "number", "Exhibit count", 2), StageBScenario.Field(hearing, "date", "Hearing date", 2),
            $$"""{"kind":"field","fieldDefinitionId":"{{statusId:D}}","control":"select","label":"Case status","layout":{"columnSpan":2,"startOnNewRow":false},"optionsSnapshot":[{"fieldOptionId":"{{options["open"]:D}}","label":"Open","sortOrder":1},{"fieldOptionId":"{{options["closed"]:D}}","label":"Closed","sortOrder":2}]}""",
            StageBScenario.Field(urgent, "checkbox", "Urgent", 2), StageBScenario.Field(note, "textarea", "Case note", 4))));
        Assert.True((await s.PublishAsync(formId, "v1")).GetProperty("authorityRegistered").GetBoolean());
        var w = new Ws(s, formId, reference, count, hearing, statusId, options["open"], options["closed"], urgent, note);
        foreach (var (field, ops) in new[] { (reference, new[] { "equals", "startsWith", "contains" }), (count, new[] { "equals", "greaterThan", "between" }), (hearing, new[] { "equals", "before", "after", "between" }), (statusId, new[] { "is" }), (urgent, new[] { "isTrue", "isFalse" }) })
        {
            var approved = await fixture.Api.PostAsync($"/api/v1/form-definitions/{formId:D}/fields/{field:D}/search-approval", s.Token(s.Administrator), new { operators = ops, approvalReference = "GOV-M3", useDevelopmentStepUp = true });
            Assert.True(approved.IsSuccess, approved.Body);
        }

        await GrantAsync(w, s.Clerk, s.Reader);
        return w;
    }

    private async Task GrantAsync(Ws w, params TestUser[] users)
    {
        var granted = await fixture.Api.SendAsync(HttpMethod.Put, $"/api/v1/form-definitions/{w.FormId:D}/field-searchers", w.S.Token(w.S.Administrator), new { grantUserIds = users.Select(u => u.AuthorityUserId).ToArray(), grantTeamIds = Array.Empty<Guid>(), revokeUserIds = Array.Empty<Guid>(), revokeTeamIds = Array.Empty<Guid>() }, Guid.NewGuid().ToString("N"));
        Assert.True(granted.Status == HttpStatusCode.NoContent, granted.Body);
    }

    private static Dictionary<string, string> Values(string reference, int count, string hearing, string status, bool urgent) => new(StringComparer.Ordinal)
    {
        ["case_reference"] = reference, ["exhibit_count"] = count.ToString(System.Globalization.CultureInfo.InvariantCulture), ["hearing_date"] = hearing, ["case_status"] = status, ["urgent"] = urgent ? "true" : "false", ["case_note"] = "note " + reference,
    };

    private async Task<Guid> CreateAsync(Ws w, Dictionary<string, string> values, string? key = null, Guid? ownerTeamId = null)
    {
        key ??= "m3-" + Guid.CreateVersion7().ToString("N");
        for (var attempt = 0; ; attempt++)
        {
            var created = await fixture.Api.PostAsync($"/api/v1/form-definitions/{w.FormId:D}/records", w.S.Token(w.S.Clerk), new { values, ownerTeamId }, key);
            if (created.Status is HttpStatusCode.Created or HttpStatusCode.OK)
            {
                var id = created.Root.GetProperty("formRecordId").GetGuid();
                Assert.True((await w.S.WaitProjectionAsync(w.S.Clerk, id)).IsSuccess);
                return id;
            }

            Assert.True(attempt < 60 && created.ErrorCode is "AUTHORITY_NOT_READY" or "STALE_AUTHORIZATION_REVISION" or "COMMAND_PENDING", "create failed: " + created.Body);
            await Task.Delay(500, Ct);
        }
    }

    private Task<HttpResult> SearchOnceAsync(Ws w, TestUser user, object[] conditions, string? status = null, bool includeArchived = false, int pageSize = 25, string? sortBy = null, string? sortDirection = null) =>
        fixture.Api.PostAsync("/api/v1/form-records/field-search", w.S.Token(user), new { formDefinitionId = w.FormId, conditions, recordStatus = status, includeArchived, includeChildren = false, sortBy, sortDirection, pageSize });

    /// <summary>A client's retry: only the typed retryable refusals (snapshot changed, archive settling, busy) are resent, briefly.</summary>
    private async Task<HttpResult> SearchAsync(Ws w, TestUser user, object[] conditions, string? status = null, bool includeArchived = false, int pageSize = 25, string? sortBy = null, string? sortDirection = null)
    {
        for (var attempt = 0; ; attempt++)
        {
            var result = await SearchOnceAsync(w, user, conditions, status, includeArchived, pageSize, sortBy, sortDirection);
            var retryable = result.Body.Length > 0 && result.Json is not null && result.Root.ValueKind == JsonValueKind.Object && result.Root.TryGetProperty("retryable", out var r) && r.GetBoolean();
            if (result.IsSuccess || !retryable || attempt >= 40)
            {
                return result;
            }

            await Task.Delay(500, Ct);
        }
    }

    private static object Cond(Guid field, string op, string valueType, string? value = null, string? lower = null, string? upper = null) => new { fieldDefinitionId = field, @operator = op, valueType, value, lowerBound = lower, upperBound = upper };

    private static HashSet<Guid> Ids(HttpResult result)
    {
        Assert.True(result.IsSuccess, result.Body);
        Assert.Equal("Complete", result.Root.GetProperty("completeness").GetString());
        return result.Root.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("formRecordId").GetGuid()).ToHashSet();
    }

    private string CommittedOperation(Guid record) =>
        fixture.Stack.AuthorityObserverSql($"SELECT committed_operation_id FROM record_content_heads WHERE form_record_id = '{record:D}';").Trim();

    private static void AssertNoResult(HttpResult result, string code, string? reason = null)
    {
        Assert.False(result.IsSuccess, result.Body);
        Assert.Equal(code, result.ErrorCode);
        if (reason is not null)
        {
            Assert.Equal(reason, result.Root.GetProperty("unavailable_reason").GetString());
        }

        Assert.False(result.Root.TryGetProperty("items", out _));
        Assert.False(result.Root.TryGetProperty("totalCount", out _));
    }

    /// <summary>ENC-SR-04/05/07, owner tests: approved equality, operators, AND, date/numeric/dropdown/boolean, the My Records shape.</summary>
    [Fact]
    public async Task ApprovedPredicatesOfEveryTypeReturnExactlyTheVerifiedMatchesInTheMyRecordsShape()
    {
        var w = await WorkspaceAsync("m3-types");
        var a = await CreateAsync(w, Values("CR-1001", 4, "2026-03-01", "open", true));
        var b = await CreateAsync(w, Values("CR-1002", 9, "2026-09-01", "closed", false));
        var c = await CreateAsync(w, Values("XR-2000", 1, "2026-05-15", "open", false));

        var capabilities = await fixture.Api.GetAsync($"/api/v1/form-records/search-capabilities?formDefinitionId={w.FormId:D}", w.S.Token(w.S.Clerk));
        Assert.True(capabilities.Root.GetProperty("fieldValueSearchAvailable").GetBoolean());
        Assert.Equal(5, capabilities.Root.GetProperty("fields").GetArrayLength());

        Assert.Equal([a], Ids(await SearchAsync(w, w.S.Clerk, [Cond(w.Reference, "equals", "String", "cr-1001")])));
        Assert.Equal([a, b], Ids(await SearchAsync(w, w.S.Clerk, [Cond(w.Reference, "startsWith", "String", "CR-")])));
        Assert.Equal([c], Ids(await SearchAsync(w, w.S.Clerk, [Cond(w.Reference, "contains", "String", "2000")])));
        Assert.Equal([b], Ids(await SearchAsync(w, w.S.Clerk, [Cond(w.Count, "greaterThan", "Integer", "4")])));
        Assert.Equal([a, c], Ids(await SearchAsync(w, w.S.Clerk, [Cond(w.Count, "between", "Integer", null, "1", "4")])));
        Assert.Equal([a], Ids(await SearchAsync(w, w.S.Clerk, [Cond(w.Hearing, "before", "Date", "2026-03-02")])));
        Assert.Equal([c], Ids(await SearchAsync(w, w.S.Clerk, [Cond(w.Hearing, "equals", "Date", "2026-05-15")])));
        Assert.Equal([a, c], Ids(await SearchAsync(w, w.S.Clerk, [Cond(w.Status, "is", "Option", w.Open.ToString("D"))])));
        Assert.Equal([a], Ids(await SearchAsync(w, w.S.Clerk, [Cond(w.Urgent, "isTrue", "Boolean")])));
        // Two and three AND conditions.
        Assert.Equal([c], Ids(await SearchAsync(w, w.S.Clerk, [Cond(w.Status, "is", "Option", w.Open.ToString("D")), Cond(w.Urgent, "isFalse", "Boolean")])));
        Assert.Empty(Ids(await SearchAsync(w, w.S.Clerk, [Cond(w.Reference, "startsWith", "String", "CR-"), Cond(w.Count, "greaterThan", "Integer", "4"), Cond(w.Urgent, "isTrue", "Boolean")])));

        var result = await SearchAsync(w, w.S.Clerk, [Cond(w.Reference, "startsWith", "String", "CR-")], sortBy: "createdOn", sortDirection: "asc");
        var items = result.Root.GetProperty("items").EnumerateArray().ToList();
        Assert.Equal([a, b], items.Select(i => i.GetProperty("formRecordId").GetGuid()));
        Assert.All(items, i =>
        {
            Assert.True(i.GetProperty("projectionVerified").GetBoolean());
            Assert.StartsWith("REC-", i.GetProperty("recordReference").GetString(), StringComparison.Ordinal);
            Assert.Equal("Draft", i.GetProperty("recordStatus").GetString()); // verified signed header status of a new record
        });
        Assert.Equal(2, result.Root.GetProperty("totalCount").GetInt32());

        // Disallowed operator, unapproved and encrypted fields, forged value type: the typed single denials.
        Assert.Equal("SEARCH_OPERATOR_NOT_SUPPORTED", (await SearchOnceAsync(w, w.S.Clerk, [Cond(w.Status, "includes", "Option", w.Open.ToString("D"))])).ErrorCode);
        Assert.Equal("SEARCH_FIELD_NOT_ALLOWED", (await SearchOnceAsync(w, w.S.Clerk, [Cond(w.Note, "equals", "String", "note")])).ErrorCode);
        Assert.Equal("SEARCH_FIELD_NOT_ALLOWED", (await SearchOnceAsync(w, w.S.Clerk, [Cond(w.Reference, "equals", "Integer", "4")])).ErrorCode);
        Assert.Equal("SEARCH_FIELD_NOT_ALLOWED", (await SearchOnceAsync(w, w.S.Designer, [Cond(w.Reference, "equals", "String", "CR-1001")])).ErrorCode); // no field_searcher

        // OD-8: completed executions are journaled by the Authority (fields and counts, never values).
        var executed = fixture.Stack.AuthorityObserverSql($"SELECT string_agg(journal_event_body_json, ' ;; ') FROM security_journal_entries WHERE organization_id = '{w.S.OrganizationId:D}' AND journal_stream_name = 'audit' AND journal_event_kind = 'FieldSearchExecuted';");
        Assert.Contains("COMPLETED", executed, StringComparison.Ordinal);
        Assert.DoesNotContain("CR-1001", executed, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>ENC-SR-07, AC6-T08, owner: unauthorized records and fields the caller cannot read never appear or influence a result.</summary>
    [Fact]
    public async Task UnauthorizedRecordsAndUnreadableFieldValuesNeverAppearOrInfluenceAResult()
    {
        var w = await WorkspaceAsync("m3-scope");
        var a = await CreateAsync(w, Values("CR-3001", 5, "2026-01-01", "open", true));
        await CreateAsync(w, Values("CR-3002", 5, "2026-01-01", "open", true));

        // The reader is a field searcher but discovers no record: a complete, empty answer.
        var none = await SearchAsync(w, w.S.Reader, [Cond(w.Reference, "startsWith", "String", "CR-")]);
        Assert.Empty(Ids(none));
        Assert.Equal(0, none.Root.GetProperty("totalCount").GetInt32());

        // Share record a with only case_reference: matching on that field finds it; on exhibit_count it is not evaluated.
        var share = await fixture.Api.PostAsync($"/api/v1/form-records/{a:D}/shares", w.S.Token(w.S.Clerk), new { recipientUserId = w.S.Reader.AuthorityUserId, recipientTeamId = (Guid?)null, recipientRole = "Reader", fieldKeys = new[] { "case_reference" } });
        Assert.True(share.IsSuccess, share.Body);
        Assert.Equal([a], Ids(await SearchAsync(w, w.S.Reader, [Cond(w.Reference, "equals", "String", "CR-3001")])));
        Assert.Empty(Ids(await SearchAsync(w, w.S.Reader, [Cond(w.Count, "equals", "Integer", "5")])));

        // Another organization: no capability and the single denial.
        var other = new StageBScenario(fixture, "m3-scope-other");
        await other.BootstrapAsync();
        var foreign = await fixture.Api.PostAsync("/api/v1/form-records/field-search", other.Token(other.Clerk), new { formDefinitionId = w.FormId, conditions = new[] { Cond(w.Reference, "equals", "String", "CR-3001") }, pageSize = 25 });
        Assert.Equal("SEARCH_FIELD_NOT_ALLOWED", foreign.ErrorCode);
    }

    /// <summary>AC6-T09, ENC-SR-14, owner: revocation and withdrawal stop searching and end open result pages.</summary>
    [Fact]
    public async Task RevokingSearchPermissionOrWithdrawingApprovalEndsOpenPagesAndFurtherSearches()
    {
        var w = await WorkspaceAsync("m3-revoke");
        for (var i = 0; i < 12; i++)
        {
            await CreateAsync(w, Values($"CR-4{i:000}", i, "2026-02-02", "open", false));
        }

        var first = await SearchAsync(w, w.S.Clerk, [Cond(w.Reference, "startsWith", "String", "CR-4")], pageSize: 10);
        Assert.Equal(12, first.Root.GetProperty("totalCount").GetInt32());
        var next = first.Root.GetProperty("continuationToken").GetString();
        Assert.False(string.IsNullOrEmpty(next));
        var second = await fixture.Api.PostAsync("/api/v1/form-records/field-search/page", w.S.Token(w.S.Clerk), new { cursor = next });
        Assert.True(second.IsSuccess, second.Body);
        Assert.Equal(2, second.Root.GetProperty("items").GetArrayLength());
        Assert.Empty(Ids(first).Intersect(Ids(second)));

        // The cursor belongs to its caller.
        Assert.Equal("SEARCH_SNAPSHOT_CHANGED", (await fixture.Api.PostAsync("/api/v1/form-records/field-search/page", w.S.Token(w.S.Reader), new { cursor = next })).ErrorCode);

        // Revoke the clerk's query permission: the open page is refused, new searches are not allowed.
        var revoked = await fixture.Api.SendAsync(HttpMethod.Put, $"/api/v1/form-definitions/{w.FormId:D}/field-searchers", w.S.Token(w.S.Administrator), new { grantUserIds = Array.Empty<Guid>(), grantTeamIds = Array.Empty<Guid>(), revokeUserIds = new[] { w.S.Clerk.AuthorityUserId }, revokeTeamIds = Array.Empty<Guid>() }, Guid.NewGuid().ToString("N"));
        Assert.Equal(HttpStatusCode.NoContent, revoked.Status);
        var stale = await fixture.Api.PostAsync("/api/v1/form-records/field-search/page", w.S.Token(w.S.Clerk), new { cursor = next });
        AssertNoResult(stale, "SEARCH_SNAPSHOT_CHANGED");
        Assert.Equal("SEARCH_FIELD_NOT_ALLOWED", (await SearchOnceAsync(w, w.S.Clerk, [Cond(w.Reference, "startsWith", "String", "CR-4")])).ErrorCode);

        // Withdrawing the approval removes the field for everyone.
        await GrantAsync(w, w.S.Clerk);
        Assert.Equal(12, (await SearchAsync(w, w.S.Clerk, [Cond(w.Reference, "startsWith", "String", "CR-4")])).Root.GetProperty("totalCount").GetInt32());
        var withdrawn = await fixture.Api.PostAsync($"/api/v1/form-definitions/{w.FormId:D}/fields/{w.Reference:D}/search-approval/withdraw", w.S.Token(w.S.Administrator), new { reason = "review" });
        Assert.True(withdrawn.IsSuccess, withdrawn.Body);
        Assert.Equal("SEARCH_FIELD_NOT_ALLOWED", (await SearchOnceAsync(w, w.S.Clerk, [Cond(w.Reference, "startsWith", "String", "CR-4")])).ErrorCode);
    }

    public static TheoryData<string> Tamperings => ["altered", "deleted", "field-substituted", "forged-row", "rolled-back"];

    /// <summary>ENC-SR-08/09/11, AC6-T11, owner: any hostile change to an accepted record's rows fails the whole search, typed, never partial.</summary>
    [Theory]
    [MemberData(nameof(Tamperings))]
    public async Task AHostileChangeToOneAcceptedRecordFailsTheWholeSearchWithNoPartialResult(string tampering)
    {
        var w = await WorkspaceAsync("m3-tamper-" + tampering);
        var good = await CreateAsync(w, Values("CR-5001", 3, "2026-04-04", "open", true));
        var victim = await CreateAsync(w, Values("CR-5002", 7, "2026-04-04", "open", true));
        if (tampering == "rolled-back")
        {
            // A second accepted revision, so an older valid revision exists to roll back to.
            var view = await w.S.WaitProjectionAsync(w.S.Clerk, victim);
            var edited = await fixture.Api.PatchAsync($"/api/v1/form-records/{victim:D}", w.S.Token(w.S.Clerk), new { expectedContentRevision = view.Root.GetProperty("record").GetProperty("contentRevision").GetInt64(), values = new Dictionary<string, string> { ["case_reference"] = "CR-5002-B" } });
            Assert.True(edited.IsSuccess, edited.Body);
            Assert.Equal(2, Ids(await SearchAsync(w, w.S.Clerk, [Cond(w.Reference, "startsWith", "String", "CR-50")])).Count);
        }
        else
        {
            Assert.Equal(2, Ids(await SearchAsync(w, w.S.Clerk, [Cond(w.Reference, "startsWith", "String", "CR-50")])).Count);
        }

        var operation = CommittedOperation(victim);
        var sql = tampering switch
        {
            "altered" => $"UPDATE field_string_values SET string_value = 'CR-9999' WHERE form_record_id = '{victim:D}' AND content_change_operation_id = '{operation}' AND field_definition_id = '{w.Reference:D}';",
            "deleted" => $"DELETE FROM field_integer_values WHERE form_record_id = '{victim:D}' AND content_change_operation_id = '{operation}';",
            "field-substituted" => $"UPDATE field_integer_values SET field_definition_id = '{w.Hearing:D}' WHERE form_record_id = '{victim:D}' AND content_change_operation_id = '{operation}';",
            // An extra row in the accepted operation, claiming another field (a forged search row): an unexpected leaf.
            "forged-row" => $@"DO $$ DECLARE cols text; BEGIN
  SELECT string_agg(quote_ident(column_name), ',') INTO cols FROM information_schema.columns WHERE table_schema = current_schema() AND table_name = 'field_string_values' AND column_name NOT IN ('field_string_value_id','field_value_item_id','field_definition_id');
  EXECUTE format('INSERT INTO field_string_values (field_string_value_id, field_value_item_id, field_definition_id, %s) SELECT gen_random_uuid(), gen_random_uuid(), %L::uuid, %s FROM field_string_values WHERE form_record_id = %L::uuid AND content_change_operation_id = %L::uuid AND field_definition_id = %L::uuid', cols, '{w.Count:D}', cols, '{victim:D}', '{operation}', '{w.Reference:D}');
END $$;",
            _ => $"DELETE FROM field_string_values WHERE form_record_id = '{victim:D}' AND content_change_operation_id = '{operation}' AND field_definition_id = '{w.Reference:D}';"
                 + $" UPDATE field_string_values SET content_change_operation_id = '{operation}' WHERE form_record_id = '{victim:D}' AND content_change_operation_id <> '{operation}' AND field_definition_id = '{w.Reference:D}';",
        };
        fixture.Stack.AttackerSql(sql);

        // The condition matches the untouched record too: a partial answer would be [good]; there is none.
        var refused = await SearchAsync(w, w.S.Clerk, [Cond(w.Hearing, "equals", "Date", "2026-04-04")]);
        AssertNoResult(refused, "SEARCH_VERIFICATION_UNAVAILABLE");
        Assert.False(refused.Root.GetProperty("retryable").GetBoolean());
        Assert.Contains(refused.Root.GetProperty("unavailable_reason").GetString(), new[] { "RECORD_VERIFICATION_FAILED", "RECORD_QUARANTINED" });
        // The record stays quarantined: later searches of this form fail too, never silently skip it.
        AssertNoResult(await SearchAsync(w, w.S.Clerk, [Cond(w.Hearing, "equals", "Date", "2026-04-04")]), "SEARCH_VERIFICATION_UNAVAILABLE", "RECORD_QUARANTINED");
        _ = good;
    }

    /// <summary>ENC-SR-11: rows injected for a record the Authority never registered are not in scope and change nothing.</summary>
    [Fact]
    public async Task RowsInjectedForAnUnregisteredRecordNeverEnterTheResult()
    {
        var w = await WorkspaceAsync("m3-inject");
        var a = await CreateAsync(w, Values("CR-6001", 2, "2026-06-06", "open", false));
        var operation = CommittedOperation(a);
        var injected = Guid.CreateVersion7();
        fixture.Stack.AttackerSql($@"DO $$ DECLARE cols text; BEGIN
  SELECT string_agg(quote_ident(column_name), ',') INTO cols FROM information_schema.columns WHERE table_schema = current_schema() AND table_name = 'field_string_values' AND column_name NOT IN ('field_string_value_id','form_record_id');
  EXECUTE format('INSERT INTO field_string_values (field_string_value_id, form_record_id, %s) SELECT gen_random_uuid(), %L::uuid, %s FROM field_string_values WHERE form_record_id = %L::uuid AND content_change_operation_id = %L::uuid', cols, '{injected:D}', cols, '{a:D}', '{operation}');
END $$;", throwOnError: false);
        Assert.Equal([a], Ids(await SearchAsync(w, w.S.Clerk, [Cond(w.Reference, "equals", "String", "CR-6001")])));
    }

    /// <summary>Owner (pending vs accepted): an abandoned creation never blocks searches; once resumed it is found.</summary>
    [Fact]
    public async Task APendingCreationNeverBlocksSearchesAndIsFoundOnceResumed()
    {
        var w = await WorkspaceAsync("m3-pending");
        var a = await CreateAsync(w, Values("CR-7001", 1, "2026-07-07", "open", false));
        var key = "m3-pending-" + Guid.CreateVersion7().ToString("N");
        var values = Values("CR-7002", 2, "2026-07-07", "open", false);
        using (var client = new HttpClient { BaseAddress = new Uri(StageBFixture.ApiBaseAddress) })
        using (var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/form-definitions/{w.FormId:D}/records") { Content = JsonContent.Create(new { values, ownerTeamId = (Guid?)null }) })
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", w.S.Token(w.S.Clerk));
            request.Headers.Add("Idempotency-Key", key);
            request.Headers.Add("X-Testing-Fault", "record-create:after-register");
            using var response = await client.SendAsync(request, Ct);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        }

        var pending = await SearchAsync(w, w.S.Clerk, [Cond(w.Hearing, "equals", "Date", "2026-07-07")]);
        Assert.Equal([a], Ids(pending));
        Assert.Contains("1 pending creation", pending.Root.GetProperty("scopeStatement").GetString(), StringComparison.Ordinal);

        // The same request resumes the creation; the record is then found (and a concurrent search is complete or typed).
        var searching = SearchAsync(w, w.S.Clerk, [Cond(w.Hearing, "equals", "Date", "2026-07-07")]);
        var resumed = await CreateAsync(w, values, key);
        var during = await searching;
        Assert.True(during.IsSuccess || during.ErrorCode is "SEARCH_SNAPSHOT_CHANGED" or "SEARCH_VERIFICATION_UNAVAILABLE", during.Body);
        Assert.Equal([a, resumed], Ids(await SearchAsync(w, w.S.Clerk, [Cond(w.Hearing, "equals", "Date", "2026-07-07")])));
    }

    /// <summary>Owner: overload is refused as busy and retryable, never queued without bound, never partial.</summary>
    [Fact]
    public async Task ConcurrentSearchesOfOneUserAreAdmittedOneAtATimeAndTheRestAreRefusedAsBusy()
    {
        var w = await WorkspaceAsync("m3-busy");
        for (var i = 0; i < 6; i++)
        {
            await CreateAsync(w, Values($"CR-8{i:000}", i, "2026-08-08", "open", false));
        }

        var results = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => SearchOnceAsync(w, w.S.Clerk, [Cond(w.Reference, "startsWith", "String", "CR-8")])));
        Assert.Contains(results, r => r.IsSuccess);
        foreach (var result in results.Where(r => !r.IsSuccess))
        {
            Assert.True(result.Root.GetProperty("retryable").GetBoolean(), result.Body);
            Assert.False(result.Root.TryGetProperty("items", out _));
        }

        Assert.Contains(results, r => !r.IsSuccess && r.Root.TryGetProperty("unavailable_reason", out var reason) && reason.GetString() == "SEARCH_CAPACITY_EXCEEDED");
        Assert.All(results.Where(r => r.IsSuccess), r => Assert.Equal(6, r.Root.GetProperty("totalCount").GetInt32()));
    }

    /// <summary>Owner: during concurrent writes a search is either complete or a typed refusal — never a partial or stale count.</summary>
    [Fact]
    public async Task SearchesDuringConcurrentWritesAreCompleteOrTypedNeverPartial()
    {
        var w = await WorkspaceAsync("m3-churn");
        var ids = new List<Guid>();
        for (var i = 0; i < 8; i++)
        {
            ids.Add(await CreateAsync(w, Values($"CR-9{i:000}", i, "2026-09-09", "open", false)));
        }

        using var stop = new CancellationTokenSource();
        var writer = Task.Run(async () =>
        {
            var n = 0;
            while (!stop.IsCancellationRequested)
            {
                var id = ids[n++ % ids.Count];
                var view = await w.S.ProjectionAsync(w.S.Clerk, id);
                if (view.IsSuccess)
                {
                    await fixture.Api.PatchAsync($"/api/v1/form-records/{id:D}", w.S.Token(w.S.Clerk), new { expectedContentRevision = view.Root.GetProperty("record").GetProperty("contentRevision").GetInt64(), values = new Dictionary<string, string> { ["exhibit_count"] = n.ToString(System.Globalization.CultureInfo.InvariantCulture) } });
                }

                await Task.Delay(150, CancellationToken.None);
            }
        }, CancellationToken.None);

        var complete = 0;
        for (var i = 0; i < 12; i++)
        {
            var result = await SearchOnceAsync(w, w.S.Clerk, [Cond(w.Reference, "startsWith", "String", "CR-9")]);
            if (result.IsSuccess)
            {
                complete++;
                Assert.Equal(8, result.Root.GetProperty("totalCount").GetInt32());
                Assert.Equal("Complete", result.Root.GetProperty("completeness").GetString());
            }
            else
            {
                Assert.True(result.ErrorCode is "SEARCH_SNAPSHOT_CHANGED" or "SEARCH_VERIFICATION_UNAVAILABLE", result.Body);
                Assert.False(result.Root.TryGetProperty("items", out _));
            }
        }

        await stop.CancelAsync();
        await writer;
        TestContext.Current.TestOutputHelper?.WriteLine($"complete {complete} of 12 during writes");
    }

    /// <summary>ENC-SR-18, owner: predicate values never reach URLs, responses beyond results, or the API/Authority logs.</summary>
    [Fact]
    public async Task PredicateValuesNeverReachUrlsErrorsOrLogs()
    {
        const string Sentinel = "Zq7M3SentinelValue";
        var w = await WorkspaceAsync("m3-logs");
        await CreateAsync(w, Values("CR-1101", 1, "2026-11-11", "open", false));
        var found = await SearchAsync(w, w.S.Clerk, [Cond(w.Reference, "contains", "String", Sentinel)]);
        Assert.Empty(Ids(found));
        Assert.DoesNotContain(Sentinel, found.Body, StringComparison.Ordinal);
        var denied = await SearchOnceAsync(w, w.S.Designer, [Cond(w.Reference, "contains", "String", Sentinel)]);
        Assert.DoesNotContain(Sentinel, denied.Body, StringComparison.Ordinal);
        Assert.DoesNotContain(Sentinel, fixture.Stack.Logs("api", 2000), StringComparison.Ordinal);
        Assert.DoesNotContain(Sentinel, fixture.Stack.Logs("authority", 2000), StringComparison.Ordinal);
        Assert.Equal("0", fixture.Stack.AuthorityObserverSql($"SELECT count(*) FROM security_journal_entries WHERE journal_event_body_json LIKE '%{Sentinel}%';").Trim());
    }

    // ---- PB-09 benchmark of the complete M3 query (engineering measurement; not a capacity claim) ----

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    private static object Percentiles(List<double> samples)
    {
        if (samples.Count == 0)
        {
            return new { samples = 0 };
        }

        var sorted = samples.Order().ToList();
        double At(double p) => sorted[Math.Clamp((int)Math.Ceiling(p * sorted.Count) - 1, 0, sorted.Count - 1)];
        return new { p50 = Math.Round(At(0.50), 1), p95 = Math.Round(At(0.95), 1), p99 = Math.Round(At(0.99), 1), max = Math.Round(sorted[^1], 1), samples = sorted.Count };
    }

    /// <summary>docker stats for the trust and data services (best effort; empty where docker is not reachable).</summary>
    private static List<string> ContainerLoad()
    {
        try
        {
            using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("docker", "stats --no-stream --format {{.Name}}|{{.CPUPerc}}|{{.MemUsage}}") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false })!;
            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(30_000);
            return output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(line => line.Contains("sectest", StringComparison.Ordinal) && (line.Contains("-api", StringComparison.Ordinal) || line.Contains("authority", StringComparison.Ordinal) || line.Contains("business-db", StringComparison.Ordinal) || line.Contains("spicedb-1", StringComparison.Ordinal) || line.Contains("vault-1", StringComparison.Ordinal)))
                .ToList();
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return [];
        }
    }

    private (long Transactions, long TuplesReturned) BusinessDbLoad() => DbLoad(fixture.Stack.AttackerSql("SELECT xact_commit || '|' || tup_returned FROM pg_stat_database WHERE datname = current_database();", throwOnError: false));

    private (long Transactions, long TuplesReturned) AuthorityDbLoad() => DbLoad(fixture.Stack.AuthorityObserverSql("SELECT xact_commit || '|' || tup_returned FROM pg_stat_database WHERE datname = current_database();"));

    private static (long, long) DbLoad(string text)
    {
        var parts = text.Trim().Split('|');
        return parts.Length == 2 && long.TryParse(parts[0], System.Globalization.CultureInfo.InvariantCulture, out var x) && long.TryParse(parts[1], System.Globalization.CultureInfo.InvariantCulture, out var y) ? (x, y) : (0, 0);
    }

    [Fact]
    public async Task BenchmarkTheCompleteVerifiedSearchAt50To480RecordsUnderConcurrencyWritesAndRevocations()
    {
        var w = await WorkspaceAsync("m3-bench");
        await w.S.BuildAccusedWorkspaceAsync(); // a second form for unrelated background writes
        var team = await fixture.Api.PostAsync("/api/v1/teams", w.S.Token(w.S.Designer), new { teamName = "Benchmark team " + w.S.Name });
        Assert.Equal(HttpStatusCode.Created, team.Status);
        var teamId = team.Root.GetProperty("teamId").GetGuid();
        var users = new[] { w.S.Clerk, w.S.Reader, w.S.Designer, w.S.Administrator, w.S.DesignerOnly };
        foreach (var user in users)
        {
            var added = await fixture.Api.PostAsync($"/api/v1/teams/{teamId:D}/members", w.S.Token(w.S.Designer), new { userId = user.AuthorityUserId });
            Assert.True(added.IsSuccess, added.Body);
        }

        var searchers = await fixture.Api.SendAsync(HttpMethod.Put, $"/api/v1/form-definitions/{w.FormId:D}/field-searchers", w.S.Token(w.S.Administrator), new { grantUserIds = Array.Empty<Guid>(), grantTeamIds = new[] { teamId }, revokeUserIds = Array.Empty<Guid>(), revokeTeamIds = Array.Empty<Guid>() }, Guid.NewGuid().ToString("N"));
        Assert.Equal(HttpStatusCode.NoContent, searchers.Status);
        var secondary = new BusinessApiClient("http://127.0.0.1:5181");
        var accused = await w.S.CreateAccusedAsync(w.S.Clerk);
        var created = 0;
        async Task GrowAsync(int target)
        {
            for (; created < target; created++)
            {
                await CreateAsync(w, Values($"CR-B{created:0000}", created % 50, $"2026-{1 + (created % 12):00}-15", created % 3 == 0 ? "closed" : "open", created % 2 == 0), ownerTeamId: teamId);
            }
        }

        object[] twoConditions = [Cond(w.Reference, "startsWith", "String", "CR-B"), Cond(w.Count, "between", "Integer", null, "10", "40")];
        object[] threeConditions = [Cond(w.Status, "is", "Option", w.Open.ToString("D")), Cond(w.Urgent, "isTrue", "Boolean"), Cond(w.Hearing, "after", "Date", "2026-03-01")];
        var sizes = new List<object>();
        foreach (var size in new[] { 50, 150, 200, 480 })
        {
            await GrowAsync(size);
            await w.S.WaitGateReadyAsync();
            var scope = await fixture.Api.PostAsync("/api/v1/testing/search-scope-verification", w.S.Token(w.S.Clerk), new { formDefinitionId = w.FormId });
            var levels = new List<object>();
            foreach (var (concurrency, rounds) in new[] { (1, 8), (4, 3), (8, 2) })
            {
                using var stop = new CancellationTokenSource();
                var background = Task.Run(async () =>
                {
                    var n = 0;
                    while (!stop.IsCancellationRequested)
                    {
                        try
                        {
                            var view = await w.S.ProjectionAsync(w.S.Clerk, accused);
                            if (view.IsSuccess)
                            {
                                await fixture.Api.PatchAsync($"/api/v1/form-records/{accused:D}", w.S.Token(w.S.Clerk), new { expectedContentRevision = view.Root.GetProperty("record").GetProperty("contentRevision").GetInt64(), values = new Dictionary<string, string> { ["given_name"] = $"Bench{n++}" } });
                            }
                        }
                        catch (Exception ex) when (ex is InvalidOperationException or KeyNotFoundException or HttpRequestException)
                        {
                        }

                        await Task.Delay(400, CancellationToken.None);
                    }
                }, CancellationToken.None);

                var businessBefore = BusinessDbLoad();
                var authorityBefore = AuthorityDbLoad();
                var latencies = new List<double>();
                var paging = new List<double>();
                var outcomes = new Dictionary<string, int>(StringComparer.Ordinal);
                var load = new List<string>();
                for (var round = 0; round <= rounds; round++)
                {
                    if (round == 1 && concurrency is 4 or 8)
                    {
                        // An authorization change in the middle of the level — a grant (4) or a revocation (8) of an individual
                        // searcher; any authorization change invalidates open snapshots (OD-5) and is measured as retries/refusals.
                        var grant = concurrency == 4;
                        var changed = await fixture.Api.SendAsync(HttpMethod.Put, $"/api/v1/form-definitions/{w.FormId:D}/field-searchers", w.S.Token(w.S.Administrator),
                            new { grantUserIds = grant ? new[] { w.S.DesignerOnly.AuthorityUserId } : [], grantTeamIds = Array.Empty<Guid>(), revokeUserIds = grant ? [] : new[] { w.S.DesignerOnly.AuthorityUserId }, revokeTeamIds = Array.Empty<Guid>() }, Guid.NewGuid().ToString("N"));
                        Assert.Equal(HttpStatusCode.NoContent, changed.Status);
                    }

                    var sampler = concurrency == 8 && round == 1 ? Task.Run(ContainerLoad, CancellationToken.None) : Task.FromResult(new List<string>());
                    var results = await Task.WhenAll(Enumerable.Range(0, concurrency).Select(async i =>
                    {
                        var user = users[i % users.Length];
                        var api = i % 2 == 0 ? fixture.Api : secondary;
                        var conditions = i % 2 == 0 ? twoConditions : threeConditions;
                        var watch = System.Diagnostics.Stopwatch.StartNew();
                        var result = await api.PostAsync("/api/v1/form-records/field-search", w.S.Token(user), new { formDefinitionId = w.FormId, conditions, pageSize = 25 });
                        watch.Stop();
                        double? page = null;
                        if (result.IsSuccess && result.Root.GetProperty("continuationToken").GetString() is { } next)
                        {
                            var pageWatch = System.Diagnostics.Stopwatch.StartNew();
                            var second = await api.PostAsync("/api/v1/form-records/field-search/page", w.S.Token(user), new { cursor = next });
                            pageWatch.Stop();
                            page = second.IsSuccess ? pageWatch.Elapsed.TotalMilliseconds : null;
                        }

                        return (result, watch.Elapsed.TotalMilliseconds, page);
                    }));
                    load.AddRange(await sampler);
                    foreach (var (result, elapsed, page) in results)
                    {
                        var outcome = result.IsSuccess ? "COMPLETED" : result.ErrorCode + (result.Root.TryGetProperty("unavailable_reason", out var reason) ? ":" + reason.GetString() : result.Root.TryGetProperty("limit_kind", out var kind) ? ":" + kind.GetString() : string.Empty);
                        if (!result.IsSuccess)
                        {
                            // Never a partial answer: every refusal is typed and carries no rows or counts.
                            Assert.False(result.Root.TryGetProperty("items", out _), result.Body);
                        }
                        else
                        {
                            Assert.Equal("Complete", result.Root.GetProperty("completeness").GetString());
                        }

                        outcomes[outcome] = outcomes.GetValueOrDefault(outcome) + 1;
                        if (round > 0 && result.IsSuccess)
                        {
                            latencies.Add(elapsed);
                            if (page is { } p)
                            {
                                paging.Add(p);
                            }
                        }
                    }
                }

                await stop.CancelAsync();
                await background;
                var businessAfter = BusinessDbLoad();
                var authorityAfter = AuthorityDbLoad();
                levels.Add(new
                {
                    concurrency, rounds, attempts = (rounds + 1) * concurrency, outcomes, searchMilliseconds = Percentiles(latencies), secondPageMilliseconds = Percentiles(paging),
                    businessDb = new { transactions = businessAfter.Transactions - businessBefore.Transactions, tuplesReturned = businessAfter.TuplesReturned - businessBefore.TuplesReturned },
                    authorityDb = new { transactions = authorityAfter.Transactions - authorityBefore.Transactions, tuplesReturned = authorityAfter.TuplesReturned - authorityBefore.TuplesReturned },
                    containerLoadAtConcurrency8 = load,
                });
            }

            sizes.Add(new
            {
                records = size,
                scope = scope.IsSuccess ? (object)new { authorityCallsForScope = scope.Root.GetProperty("authorityCalls").GetInt32(), verificationBatches = scope.Root.GetProperty("verificationBatches").GetInt32(), verifiedBytes = scope.Root.GetProperty("verifiedBytes").GetInt64(), scopeMilliseconds = scope.Root.GetProperty("elapsedMilliseconds").GetDouble() } : new { error = scope.ErrorCode },
                authorityCallsPerSearch = $"capabilities 1 + catalogue 1 + batches ceil({size}/50) + validation 1 + evidence 1 + edit check 1 (+ the same again on the single automatic retry)",
                levels,
            });
        }

        // Past the 500-record ceiling: the typed records-limit refusal, nothing partial.
        await GrowAsync(505);
        var over = await SearchAsync(w, w.S.Clerk, twoConditions);
        AssertNoResult(over, "SEARCH_VERIFICATION_LIMIT");
        Assert.Equal("records", over.Root.GetProperty("limit_kind").GetString());

        var json = JsonSerializer.Serialize(new { measuredOn = DateTimeOffset.UtcNow, query = "complete M3 query: capabilities, condition authorization, admission, Authority catalogue, batch verification of every record, snapshot validation, predicate evaluation, sort, execution evidence, first page; plus the second page", conditions = new[] { "String startsWith + Integer between", "Option is + Boolean isTrue + Date after" }, users = "5 team members, alternating two API instances", backgroundWorkload = "edits of a record of another form every 400 ms; one individual field_searcher grant (concurrency 4) and revocation (concurrency 8) mid-level", note = "engineering measurement; small samples; p99 of small n is near the maximum; not a capacity claim", sizes }, Indented);
        TestContext.Current.TestOutputHelper?.WriteLine(json);
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "EnterpriseForms.All.slnx")))
        {
            root = root.Parent;
        }

        if (root is not null)
        {
            var directory = Path.Combine(root.FullName, "artifacts", "stage-b");
            Directory.CreateDirectory(directory);
            await File.WriteAllTextAsync(Path.Combine(directory, "c7-m3-benchmark.json"), json, Ct);
        }
    }
}
