using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using EnterpriseForms.SecurityTests.Harness;
using EnterpriseForms.StageBTests.Harness;
using Xunit;

namespace EnterpriseForms.StageBTests.Tests;

/// <summary>
/// IX-6 (IX-D12, R1 §E.6) on the real security-test stack: My Records answered by the Authority from verified index entries, through the
/// public endpoint, with the engine chosen per request by the Testing-only <c>X-Testing-Record-List-Engine</c> header (the stack default
/// stays the C-29 listing). The indexed listing must return exactly what the unchanged listing returns for a fully covered caller; it falls
/// back when any of the caller's records has no current entry; revocation is immediate (fresh SpiceDB discover at the pin); damaged header
/// facts quarantine the index and the unchanged listing answers; an organization quarantine is never bypassed. Synthetic data only. A
/// focused development test class (C-131), not a Stage B group run.
/// </summary>
[Collection(StageBStackCollectionDefinition.Name)]
public sealed class Ix6RecordListStackTests(StageBFixture fixture)
{
    private const string Indexed = "Indexed";
    private const string VerifiedScan = "VerifiedScan";

    private sealed record Ws(StageBScenario S, Guid FormId, Guid Reference);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string Scalar(string sql) => fixture.Stack.AuthorityObserverSql(sql).Trim().Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim() ?? string.Empty;

    private async Task<Ws> WorkspaceAsync(string name)
    {
        var s = new StageBScenario(fixture, name);
        await s.BootstrapAsync();
        var current = await fixture.Api.GetAsync("/api/v1/organization/security-policy", s.Token(s.Designer));
        var policy = await fixture.Api.SendAsync(HttpMethod.Put, "/api/v1/organization/security-policy", s.Token(s.Designer), new { minimumFieldClassification = "Internal", plaintextInternalFieldsPermitted = true, plaintextDowngradePermitted = false, plaintextSearchPermitted = true, policyStatementText = "IX-6 synthetic organization.", expectedPolicyRevision = current.Root.GetProperty("securityPolicyRevision").GetInt64() }, Guid.NewGuid().ToString("N"));
        Assert.True(policy.IsSuccess, policy.Body);
        var formId = await s.CreateFormAsync("IX-6 " + s.Name, s.Clerk);
        var reference = await FieldAsync(s, formId, "case_reference", "Case reference");
        await s.SaveDraftAsync(formId, StageBScenario.Layout("IX-6", StageBScenario.Section("main", "Main", StageBScenario.Field(reference, "text", "Case reference", 4))));
        Assert.True((await s.PublishAsync(formId, "v1")).GetProperty("authorityRegistered").GetBoolean());
        var approved = await fixture.Api.PostAsync($"/api/v1/form-definitions/{formId:D}/fields/{reference:D}/search-approval", s.Token(s.Administrator), new { operators = new[] { "equals" }, approvalReference = "IX-6", useDevelopmentStepUp = true });
        Assert.True(approved.IsSuccess, approved.Body);
        return new Ws(s, formId, reference);
    }

    private async Task<Guid> FieldAsync(StageBScenario s, Guid formId, string key, string label)
    {
        var added = await fixture.Api.PostAsync($"/api/v1/form-definitions/{formId:D}/fields", s.Token(s.Designer), new { fieldKey = key, fieldDisplayName = label, fieldDescription = string.Empty, fieldDataType = "String", options = (object?)null, fieldClassification = "Internal", fieldProtectionMode = "Standard" });
        Assert.True(added.Status == HttpStatusCode.Created, $"add {key}: " + added.Body);
        return added.Root.GetProperty("fieldDefinitionId").GetGuid();
    }

    private async Task<Guid> CreateAsync(StageBScenario s, Guid formId, string reference)
    {
        var key = "ix6-" + Guid.CreateVersion7().ToString("N");
        for (var attempt = 0; ; attempt++)
        {
            var created = await fixture.Api.PostAsync($"/api/v1/form-definitions/{formId:D}/records", s.Token(s.Clerk), new { values = new Dictionary<string, string>(StringComparer.Ordinal) { ["case_reference"] = reference } }, key);
            if (created.Status is HttpStatusCode.Created or HttpStatusCode.OK)
            {
                var id = created.Root.GetProperty("formRecordId").GetGuid();
                Assert.True((await s.WaitProjectionAsync(s.Clerk, id)).IsSuccess);
                return id;
            }

            Assert.True(attempt < 60 && created.ErrorCode is "AUTHORITY_NOT_READY" or "STALE_AUTHORIZATION_REVISION" or "COMMAND_PENDING", "create failed: " + created.Body);
            await Task.Delay(400, Ct);
        }
    }

    private async Task WaitCompleteAsync(Guid org, int coverages = 1)
    {
        var deadline = DateTime.UtcNow.AddSeconds(300);
        string last;
        do
        {
            last = Scalar($"SELECT count(*) FILTER (WHERE coverage_state_name = 'Complete') || '/' || count(*) FROM authority_search.search_field_coverages WHERE organization_id = '{org:D}' AND coverage_state_name <> 'Withdrawn';");
            if (last == $"{coverages}/{coverages}")
            {
                return;
            }

            await Task.Delay(1000, Ct);
        }
        while (DateTime.UtcNow < deadline);
        Assert.Fail("coverages not Complete: " + last);
    }

    private sealed record Response(HttpStatusCode Status, JsonElement Root, string Body)
    {
        public bool IsSuccess => (int)Status is >= 200 and < 300;

        public string? Code => Root.ValueKind == JsonValueKind.Object && Root.TryGetProperty("title", out var title) ? title.GetString() : null;

        public List<Guid> Ids => Root.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("formRecordId").GetGuid()).ToList();

        public string? Next => Root.GetProperty("continuationToken").GetString();
    }

    private static async Task<Response> ListAsync(StageBScenario s, TestUser user, string engine, string query, string? cursor = null)
    {
        using var http = new HttpClient { BaseAddress = new Uri(StageBFixture.ApiBaseAddress), Timeout = TimeSpan.FromMinutes(2) };
        var path = "/api/v1/form-records/?" + query + (cursor is null ? string.Empty : "&continuationToken=" + Uri.EscapeDataString(cursor));
        for (var attempt = 0; ; attempt++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, path);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", s.Token(user));
            request.Headers.Add("X-Testing-Record-List-Engine", engine);
            using var response = await http.SendAsync(request, Ct);
            var text = await response.Content.ReadAsStringAsync(Ct);
            var result = new Response(response.StatusCode, text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone(), text);
            // A security command still settling (AUTHORIZATION_STATE_CHANGED from the C-29 enumeration) is retried; nothing else is.
            if (result.IsSuccess || attempt >= 20 || cursor is not null || result.Code is not ("AUTHORIZATION_STATE_CHANGED" or "AUTHORITY_NOT_READY"))
            {
                return result;
            }

            await Task.Delay(500, Ct);
        }
    }

    private static async Task<(List<Guid> Ids, int Total, List<string> Statuses)> AllPagesAsync(StageBScenario s, TestUser user, string engine, string query)
    {
        var first = await ListAsync(s, user, engine, query);
        Assert.True(first.IsSuccess, first.Body);
        var ids = first.Ids;
        var statuses = first.Root.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("recordStatus").GetString()!).ToList();
        var next = first.Next;
        while (next is not null)
        {
            var page = await ListAsync(s, user, engine, query, next);
            Assert.True(page.IsSuccess, page.Body);
            ids.AddRange(page.Ids);
            statuses.AddRange(page.Root.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("recordStatus").GetString()!));
            next = page.Next;
        }

        return (ids, first.Root.GetProperty("totalCount").GetInt32(), statuses);
    }

    private static void AssertSame((List<Guid> Ids, int Total, List<string> Statuses) expected, (List<Guid> Ids, int Total, List<string> Statuses) actual, string query)
    {
        Assert.True(expected.Ids.SequenceEqual(actual.Ids), $"{query}: ids differ: [{string.Join(',', expected.Ids)}] vs [{string.Join(',', actual.Ids)}]");
        Assert.True(expected.Statuses.SequenceEqual(actual.Statuses, StringComparer.Ordinal), $"{query}: statuses differ");
        Assert.Equal(expected.Total, actual.Total);
    }

    private int EngineLines(string engine, string reason) =>
        fixture.Stack.Logs("api", 20000).Split('\n').Count(l => l.Contains($"IX6-RECORD-LIST engine={engine} fallback_reason={reason}", StringComparison.Ordinal));

    // ---- tests ---------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// For a caller whose every record has a current entry, the indexed engine answers (log: engine=Indexed) and returns exactly the
    /// ordered ids, totals and statuses of the unchanged listing for the default sort, the status filter, archived records and the other
    /// supported sorts; a page cursor of one engine is refused by the other (409 LIST_CHANGED).
    /// </summary>
    [Fact]
    public async Task TheIndexedListingAnswersAndMatchesTheUnchangedListing()
    {
        var w = await WorkspaceAsync("ix6-equal");
        var records = new List<Guid>();
        for (var i = 0; i < 5; i++)
        {
            records.Add(await CreateAsync(w.S, w.FormId, $"EQ-{i:D2}"));
        }

        var archive = await fixture.Api.PostAsync($"/api/v1/form-records/{records[1]:D}/archive", w.S.Token(w.S.Clerk), new { expectedContentRevision = AuthorityOracle.Head(fixture.Stack, records[1]).ContentRevision, expectedStatus = "Draft", reason = "IX-6 archived record" });
        Assert.True(archive.IsSuccess, archive.Body);
        await WaitCompleteAsync(w.S.OrganizationId);

        var indexedBefore = EngineLines(Indexed, "-");
        foreach (var query in new[] { "pageSize=2", "pageSize=2&recordStatus=Draft", "pageSize=2&includeArchived=true", "pageSize=3&sortBy=createdOn&sortDirection=asc", "pageSize=2&sortBy=recordStatus&includeArchived=true", $"pageSize=10&formDefinitionId={w.FormId:D}" })
        {
            var scan = await AllPagesAsync(w.S, w.S.Clerk, VerifiedScan, query);
            var indexed = await AllPagesAsync(w.S, w.S.Clerk, Indexed, query);
            AssertSame(scan, indexed, query);
            Assert.True(scan.Ids.Count > 0, query);
        }

        Assert.True(EngineLines(Indexed, "-") > indexedBefore);
        Assert.Contains(records[1], (await AllPagesAsync(w.S, w.S.Clerk, Indexed, "pageSize=10&includeArchived=true")).Ids);
        Assert.DoesNotContain(records[1], (await AllPagesAsync(w.S, w.S.Clerk, Indexed, "pageSize=10")).Ids);

        var indexedFirst = await ListAsync(w.S, w.S.Clerk, Indexed, "pageSize=2");
        var crossed = await ListAsync(w.S, w.S.Clerk, VerifiedScan, "pageSize=2", indexedFirst.Next);
        Assert.Equal((HttpStatusCode.Conflict, "LIST_CHANGED"), (crossed.Status, crossed.Code));
        var scanFirst = await ListAsync(w.S, w.S.Clerk, VerifiedScan, "pageSize=2");
        var continued = await ListAsync(w.S, w.S.Clerk, Indexed, "pageSize=2", scanFirst.Next);
        Assert.True(continued.IsSuccess, continued.Body); // a C-29 listing continues on its own engine
    }

    /// <summary>A record on a form without search coverage has no entry: the indexed engine refuses (I-1) and the unchanged listing answers, identically.</summary>
    [Fact]
    public async Task ARecordWithoutAnIndexEntryMakesTheListingFallBackUnchanged()
    {
        var w = await WorkspaceAsync("ix6-fallback");
        await CreateAsync(w.S, w.FormId, "FB-01");
        await WaitCompleteAsync(w.S.OrganizationId);
        var other = await w.S.CreateFormAsync("IX-6 uncovered " + w.S.Name, w.S.Clerk);
        var field = await FieldAsync(w.S, other, "case_reference", "Case reference");
        await w.S.SaveDraftAsync(other, StageBScenario.Layout("IX-6 uncovered", StageBScenario.Section("main", "Main", StageBScenario.Field(field, "text", "Case reference", 4))));
        Assert.True((await w.S.PublishAsync(other, "v1")).GetProperty("authorityRegistered").GetBoolean());
        var uncovered = await CreateAsync(w.S, other, "FB-02");

        var fallbacks = EngineLines(VerifiedScan, "RECORD_LIST_INDEX_INCOMPLETE");
        var indexed = await AllPagesAsync(w.S, w.S.Clerk, Indexed, "pageSize=10");
        AssertSame(await AllPagesAsync(w.S, w.S.Clerk, VerifiedScan, "pageSize=10"), indexed, "fallback");
        Assert.Contains(uncovered, indexed.Ids);
        Assert.True(EngineLines(VerifiedScan, "RECORD_LIST_INDEX_INCOMPLETE") > fallbacks);
    }

    /// <summary>
    /// Revocation is immediate on the indexed engine: a Reader sees exactly the records shared with them; after a revocation the next page
    /// of a listing is refused (409) and a new listing no longer contains the record (fresh discover at the S1 pin, no cache).
    /// </summary>
    [Fact]
    public async Task ARevocationIsImmediateOnTheIndexedListing()
    {
        var w = await WorkspaceAsync("ix6-revocation");
        var first = await CreateAsync(w.S, w.FormId, "RV-01");
        var second = await CreateAsync(w.S, w.FormId, "RV-02");
        await WaitCompleteAsync(w.S.OrganizationId);
        var shares = new List<Guid>();
        foreach (var record in new[] { first, second })
        {
            var share = await fixture.Api.PostAsync($"/api/v1/form-records/{record:D}/shares", w.S.Token(w.S.Clerk), new { useDevelopmentStepUp = true, recipientUserId = w.S.Reader.AuthorityUserId, recipientTeamId = (Guid?)null, recipientRole = "Reader", fieldKeys = new[] { "case_reference" } });
            Assert.True(share.IsSuccess, share.Body);
            shares.Add(share.Root.GetProperty("recordShareId").GetGuid());
            await w.S.WaitGateReadyAsync(TimeSpan.FromSeconds(40));
        }

        var before = EngineLines(Indexed, "-");
        var shared = await AllPagesAsync(w.S, w.S.Reader, Indexed, "pageSize=10");
        Assert.Equal(new[] { first, second }.Order(), shared.Ids.Order());
        Assert.True(EngineLines(Indexed, "-") > before);

        var page = await ListAsync(w.S, w.S.Reader, Indexed, "pageSize=1");
        Assert.True(page.IsSuccess, page.Body);
        Assert.NotNull(page.Next);
        var revoke = await fixture.Api.PostAsync($"/api/v1/form-records/{first:D}/shares/{shares[0]:D}/revoke?useDevelopmentStepUp=true", w.S.Token(w.S.Clerk), new { });
        Assert.True(revoke.IsSuccess, revoke.Body);
        await w.S.WaitGateReadyAsync(TimeSpan.FromSeconds(40));

        var refused = await ListAsync(w.S, w.S.Reader, Indexed, "pageSize=1", page.Next);
        Assert.Equal((HttpStatusCode.Conflict, "LIST_CHANGED"), (refused.Status, refused.Code));
        Assert.DoesNotContain("formRecordId", refused.Body, StringComparison.Ordinal);
        var after = await AllPagesAsync(w.S, w.S.Reader, Indexed, "pageSize=10");
        Assert.Equal([second], after.Ids);
        Assert.Equal(1, after.Total);
    }

    /// <summary>
    /// Damaged header facts (a status changed directly in the index) are detected by the binding check (I-2): the index is quarantined and
    /// the unchanged listing answers from the business listing; an S1 organization quarantine refuses both engines identically (423).
    /// </summary>
    [Fact]
    public async Task DamagedHeaderFactsQuarantineTheIndexAndAnOrganizationQuarantineIsNeverBypassed()
    {
        var w = await WorkspaceAsync("ix6-damage");
        var victim = await CreateAsync(w.S, w.FormId, "DM-01");
        await CreateAsync(w.S, w.FormId, "DM-02");
        await WaitCompleteAsync(w.S.OrganizationId);
        var expected = await AllPagesAsync(w.S, w.S.Clerk, VerifiedScan, "pageSize=10");
        fixture.Stack.AuthorityAdministratorSql($"SELECT set_config('authority_search.organization_id', '{w.S.OrganizationId:D}', false); UPDATE authority_search.search_record_entries SET record_status_name = 'Submitted' WHERE form_record_id = '{victim:D}';");

        var fallbacks = EngineLines(VerifiedScan, "SEARCH_INDEX_INCONSISTENT");
        var answered = await AllPagesAsync(w.S, w.S.Clerk, Indexed, "pageSize=10");
        AssertSame(expected, answered, "damaged");
        Assert.True(EngineLines(VerifiedScan, "SEARCH_INDEX_INCONSISTENT") > fallbacks);
        Assert.StartsWith("Quarantined|", Scalar($"SELECT projection_status_name || '|' || coalesce(projection_status_reason_code, '') FROM authority_search.search_projection_states WHERE organization_id = '{w.S.OrganizationId:D}';"), StringComparison.Ordinal);

        fixture.Stack.AuthorityAdministratorSql($"UPDATE authority.organization_authorization_states SET integrity_quarantine_enabled = true, integrity_quarantine_reason_code = 'IX6_TEST_QUARANTINE' WHERE organization_id = '{w.S.OrganizationId:D}';");
        var indexed = await ListAsync(w.S, w.S.Clerk, Indexed, "pageSize=10");
        var scan = await ListAsync(w.S, w.S.Clerk, VerifiedScan, "pageSize=10");
        Assert.False(indexed.IsSuccess, indexed.Body);
        Assert.Equal((scan.Status, scan.Code), (indexed.Status, indexed.Code));
        Assert.DoesNotContain("formRecordId", indexed.Body, StringComparison.Ordinal);
    }
}
