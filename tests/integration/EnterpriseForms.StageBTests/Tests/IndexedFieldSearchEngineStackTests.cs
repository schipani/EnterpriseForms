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
/// IX-4 (C-133) on the real security-test stack: the business <c>Indexed</c> engine of Advanced Field Search through the public
/// endpoints (design §16 IX-4, R1 §E.1, §G.2, §6.6, §17.2). Callers name an Authority session they registered for the same identity
/// with the Testing-only <c>X-Testing-Session-Digest</c> header (the harness has no cookie session); without it the bearer caller is
/// answered by V1 (D-IX4-2). Synthetic data only. A focused development test class (C-131), not a Stage B group run.
/// </summary>
[Collection(StageBStackCollectionDefinition.Name)]
public sealed class IndexedFieldSearchEngineStackTests(StageBFixture fixture)
{
    private sealed record Ws(StageBScenario S, Guid FormId, Guid Reference, Guid Count, Guid Hearing);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string AcceptedContentStatement = "Results reflect the content the Authorization Authority last accepted for each record, at one Authority snapshot. Each returned record's searched values were checked against that accepted content. The business database copy is not checked by search; it is fully verified when you open a record.";

    private string Scalar(string sql) => fixture.Stack.AuthorityObserverSql(sql).Trim().Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim() ?? string.Empty;

    private async Task<Ws> WorkspaceAsync(string name)
    {
        var s = new StageBScenario(fixture, name);
        await s.BootstrapAsync();
        var current = await fixture.Api.GetAsync("/api/v1/organization/security-policy", s.Token(s.Designer));
        var policy = await fixture.Api.SendAsync(HttpMethod.Put, "/api/v1/organization/security-policy", s.Token(s.Designer), new { minimumFieldClassification = "Internal", plaintextInternalFieldsPermitted = true, plaintextDowngradePermitted = false, plaintextSearchPermitted = true, policyStatementText = "IX-4 synthetic organization.", expectedPolicyRevision = current.Root.GetProperty("securityPolicyRevision").GetInt64() }, Guid.NewGuid().ToString("N"));
        Assert.True(policy.IsSuccess, policy.Body);
        var formId = await s.CreateFormAsync("IX-4 " + s.Name, s.Clerk);
        async Task<Guid> FieldAsync(string key, string label, string type)
        {
            var added = await fixture.Api.PostAsync($"/api/v1/form-definitions/{formId:D}/fields", s.Token(s.Designer), new { fieldKey = key, fieldDisplayName = label, fieldDescription = string.Empty, fieldDataType = type, options = (object?)null, fieldClassification = "Internal", fieldProtectionMode = "Standard" });
            Assert.True(added.Status == HttpStatusCode.Created, $"add {key}: " + added.Body);
            return added.Root.GetProperty("fieldDefinitionId").GetGuid();
        }

        var reference = await FieldAsync("case_reference", "Case reference", "String");
        var count = await FieldAsync("exhibit_count", "Exhibit count", "Integer");
        var hearing = await FieldAsync("hearing_date", "Hearing date", "Date");
        await s.SaveDraftAsync(formId, StageBScenario.Layout("IX-4", StageBScenario.Section("main", "Main",
            StageBScenario.Field(reference, "text", "Case reference", 2), StageBScenario.Field(count, "number", "Exhibit count", 2), StageBScenario.Field(hearing, "date", "Hearing date", 2))));
        Assert.True((await s.PublishAsync(formId, "v1")).GetProperty("authorityRegistered").GetBoolean());
        foreach (var (field, ops) in new[] { (reference, new[] { "equals", "startsWith", "contains" }), (count, new[] { "equals", "greaterThan", "greaterThanOrEqual", "lessThan", "between" }), (hearing, new[] { "equals", "before", "after", "between" }) })
        {
            await ApproveAsync(s, formId, field, ops);
        }

        var granted = await fixture.Api.SendAsync(HttpMethod.Put, $"/api/v1/form-definitions/{formId:D}/field-searchers", s.Token(s.Administrator), new { grantUserIds = new[] { s.Clerk.AuthorityUserId, s.Reader.AuthorityUserId }, grantTeamIds = Array.Empty<Guid>(), revokeUserIds = Array.Empty<Guid>(), revokeTeamIds = Array.Empty<Guid>() }, Guid.NewGuid().ToString("N"));
        Assert.True(granted.Status == HttpStatusCode.NoContent, granted.Body);
        return new Ws(s, formId, reference, count, hearing);
    }

    private async Task ApproveAsync(StageBScenario s, Guid formId, Guid field, string[] operators)
    {
        var approved = await fixture.Api.PostAsync($"/api/v1/form-definitions/{formId:D}/fields/{field:D}/search-approval", s.Token(s.Administrator), new { operators, approvalReference = "IX-4", useDevelopmentStepUp = true });
        Assert.True(approved.IsSuccess, approved.Body);
    }

    private static Dictionary<string, string> Values(string reference, int count, string hearing) => new(StringComparer.Ordinal)
    {
        ["case_reference"] = reference, ["exhibit_count"] = count.ToString(CultureInfo.InvariantCulture), ["hearing_date"] = hearing,
    };

    private async Task<Guid> CreateAsync(Ws w, Dictionary<string, string> values)
    {
        var key = "ix4-" + Guid.CreateVersion7().ToString("N");
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

    private string Coverages(Guid org) => Scalar($"SELECT count(*) FILTER (WHERE coverage_state_name = 'Complete') || '/' || count(*) FROM authority_search.search_field_coverages WHERE organization_id = '{org:D}' AND coverage_state_name <> 'Withdrawn';");

    private async Task WaitCompleteAsync(Ws w, int coverages = 3)
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

    private async Task<string> SessionAsync(Ws w, TestUser user)
    {
        var digest = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var created = await fixture.Authority.SendAsync(HttpMethod.Post, "/internal/v1/sessions", TestIdentity.WorkloadToken(), new { organization_id = w.S.OrganizationId, session_digest_base64 = digest, absolute_expires_on = DateTimeOffset.UtcNow.AddHours(1) }, w.S.Token(user));
        Assert.True(created.IsSuccess, created.Body);
        return digest;
    }

    private static object Cond(Guid field, string op, string valueType, string? value = null, string? lower = null, string? upper = null) =>
        new { fieldDefinitionId = field, @operator = op, valueType, value, lowerBound = lower, upperBound = upper };

    private sealed record Response(HttpStatusCode Status, JsonElement Root, string Body)
    {
        public bool IsSuccess => (int)Status is >= 200 and < 300;

        public string? Code => Root.ValueKind == JsonValueKind.Object && Root.TryGetProperty("title", out var title) ? title.GetString() : null;

        public string? Reason => Root.ValueKind == JsonValueKind.Object && Root.TryGetProperty("unavailable_reason", out var reason) ? reason.GetString() : null;
    }

    /// <summary>The public field-search endpoints, as the user (bearer) with or without the Testing session header.</summary>
    private static async Task<Response> PostAsync(Ws w, TestUser user, string path, object body, string? session)
    {
        using var http = new HttpClient { BaseAddress = new Uri(StageBFixture.ApiBaseAddress), Timeout = TimeSpan.FromMinutes(2) };
        using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body) };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", w.S.Token(user));
        if (session is not null)
        {
            request.Headers.Add("X-Testing-Session-Digest", session);
        }

        using var response = await http.SendAsync(request, Ct);
        var text = await response.Content.ReadAsStringAsync(Ct);
        return new Response(response.StatusCode, text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone(), text);
    }

    private static async Task<Response> SearchAsync(Ws w, TestUser user, string? session, object[] conditions, int pageSize = 10, bool includeArchived = false)
    {
        for (var attempt = 0; ; attempt++)
        {
            var result = await PostAsync(w, user, "/api/v1/form-records/field-search", new { formDefinitionId = w.FormId, conditions, includeArchived, pageSize }, session);
            if (result.IsSuccess || attempt >= 20 || !(result.Code is "SEARCH_SNAPSHOT_CHANGED" || result.Reason is "AWAITING_ARCHIVE" or "AUTHORITY_UNAVAILABLE"))
            {
                return result;
            }

            await Task.Delay(500, Ct);
        }
    }

    private static Task<Response> PageAsync(Ws w, TestUser user, string? session, string cursor) =>
        PostAsync(w, user, "/api/v1/form-records/field-search/page", new { cursor }, session);

    private static List<Guid> Ids(Response result)
    {
        Assert.True(result.IsSuccess, result.Body);
        return result.Root.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("formRecordId").GetGuid()).ToList();
    }

    private static string Basis(Response result) => result.Root.GetProperty("resultBasis").GetString()!;

    private static async Task<List<Guid>> AllPagesAsync(Ws w, TestUser user, string? session, Response first)
    {
        var ids = Ids(first);
        var next = first.Root.GetProperty("continuationToken").GetString();
        while (next is not null)
        {
            var page = await PageAsync(w, user, session, next);
            Assert.True(page.IsSuccess, page.Body);
            Assert.Equal(Basis(first), Basis(page)); // one search never combines engines
            ids.AddRange(Ids(page));
            next = page.Root.GetProperty("continuationToken").GetString();
        }

        return ids;
    }

    private int FallbackLogLines() =>
        fixture.Stack.Logs("api", 20000).Split('\n').Count(l => l.Contains("IX4-ENGINE fallback", StringComparison.Ordinal));

    // ---- tests ---------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// The Indexed engine answers through the public API (IX-37: <c>resultBasis</c> AuthorityAcceptedContent, every row
    /// <c>projectionVerified=false</c>, the §6.2 statement), pages through the Authority, and returns exactly the ordered ids V1
    /// returns for the same request; a bearer caller without a session is answered by V1 (<c>VerifiedScan</c>, verified rows).
    /// </summary>
    [Fact]
    public async Task TheIndexedEngineAnswersAsAcceptedContentPagesThroughTheAuthorityAndMatchesV1()
    {
        var w = await WorkspaceAsync("ix4-engine");
        for (var i = 0; i < 30; i++)
        {
            await CreateAsync(w, Values($"EN-{i:D2}", i % 9, $"2026-0{1 + (i % 9)}-15"));
        }

        await WaitCompleteAsync(w);
        var session = await SessionAsync(w, w.S.Clerk);
        object[] conditions = [Cond(w.Count, "greaterThanOrEqual", "Integer", "2")];
        var indexed = await SearchAsync(w, w.S.Clerk, session, conditions);
        Assert.True(indexed.IsSuccess, indexed.Body);
        Assert.Equal("AuthorityAcceptedContent", Basis(indexed));
        Assert.All(indexed.Root.GetProperty("items").EnumerateArray(), i => Assert.False(i.GetProperty("projectionVerified").GetBoolean()));
        Assert.Contains(AcceptedContentStatement, indexed.Root.GetProperty("scopeStatement").GetString(), StringComparison.Ordinal);
        Assert.StartsWith("Complete: all 30 records of this form that you can find were searched at one Authority snapshot", indexed.Root.GetProperty("scopeStatement").GetString(), StringComparison.Ordinal);
        var total = indexed.Root.GetProperty("totalCount").GetInt32();
        Assert.True(total > 10, indexed.Body);
        Assert.StartsWith("i.", indexed.Root.GetProperty("continuationToken").GetString(), StringComparison.Ordinal);
        Assert.Equal(indexed.Root.GetProperty("snapshotReference").GetString(), Scalar($"SELECT search_result_snapshot_id FROM authority_search.search_result_snapshots WHERE search_result_snapshot_id = '{indexed.Root.GetProperty("snapshotReference").GetString()}';"));
        var indexedIds = await AllPagesAsync(w, w.S.Clerk, session, indexed);
        Assert.Equal(total, indexedIds.Count);

        var v1 = await SearchAsync(w, w.S.Clerk, null, conditions);
        Assert.True(v1.IsSuccess, v1.Body);
        Assert.Equal("VerifiedScan", Basis(v1));
        Assert.All(v1.Root.GetProperty("items").EnumerateArray(), i => Assert.True(i.GetProperty("projectionVerified").GetBoolean()));
        Assert.DoesNotContain(AcceptedContentStatement, v1.Root.GetProperty("scopeStatement").GetString(), StringComparison.Ordinal);
        Assert.Equal(await AllPagesAsync(w, w.S.Clerk, null, v1), indexedIds); // same ordered ids, same count

        // An indexed cursor is bound to its session: another session of the same user, or no session, cannot page it.
        var cursor = indexed.Root.GetProperty("continuationToken").GetString()!;
        Assert.Equal("SEARCH_SNAPSHOT_CHANGED", (await PageAsync(w, w.S.Clerk, await SessionAsync(w, w.S.Clerk), cursor)).Code);
        Assert.Equal("SEARCH_SNAPSHOT_CHANGED", (await PageAsync(w, w.S.Clerk, null, cursor)).Code);
    }

    /// <summary>
    /// IX-D8 fallback: with a predicate field's coverage Building (builder stopped), the complete V1 path answers within V1's limits,
    /// says so (resultBasis VerifiedScan, the statement note, verified rows) and is logged; once the coverage is Complete again the
    /// index answers.
    /// </summary>
    [Fact]
    public async Task WhileACoverageIsBuildingTheCompleteV1PathAnswersAndSaysSo()
    {
        var w = await WorkspaceAsync("ix4-building");
        for (var i = 0; i < 6; i++)
        {
            await CreateAsync(w, Values($"BU-{i:D2}", i, "2026-03-03"));
        }

        await WaitCompleteAsync(w);
        var session = await SessionAsync(w, w.S.Clerk);
        object[] conditions = [Cond(w.Count, "lessThan", "Integer", "3")];
        Assert.Equal("AuthorityAcceptedContent", Basis(await SearchAsync(w, w.S.Clerk, session, conditions)));
        var fallbacks = FallbackLogLines();
        fixture.Stack.Stop("worker");
        try
        {
            var withdrawn = await fixture.Api.PostAsync($"/api/v1/form-definitions/{w.FormId:D}/fields/{w.Count:D}/search-approval/withdraw", w.S.Token(w.S.Administrator), new { reason = "IX-4 coverage rebuild" });
            Assert.True(withdrawn.IsSuccess, withdrawn.Body);
            await ApproveAsync(w.S, w.FormId, w.Count, ["equals", "greaterThan", "greaterThanOrEqual", "lessThan", "between"]);
            // exhibit_count is Building (an approval change returns the form's coverages to Building) and stays so without the builder.
            Assert.Equal("Building", Scalar($"SELECT coverage_state_name FROM authority_search.search_field_coverages WHERE organization_id = '{w.S.OrganizationId:D}' AND field_definition_id = '{w.Count:D}' AND coverage_state_name <> 'Withdrawn';"));

            var answered = await SearchAsync(w, w.S.Clerk, session, conditions);
            Assert.True(answered.IsSuccess, answered.Body);
            Assert.Equal("VerifiedScan", Basis(answered));
            Assert.Equal(3, answered.Root.GetProperty("totalCount").GetInt32());
            Assert.All(answered.Root.GetProperty("items").EnumerateArray(), i => Assert.True(i.GetProperty("projectionVerified").GetBoolean()));
            Assert.EndsWith("The search index could not answer this search, so every record in scope was verified instead.", answered.Root.GetProperty("scopeStatement").GetString(), StringComparison.Ordinal);
            Assert.True(FallbackLogLines() > fallbacks, "the fallback is logged (event 9333)");
            Assert.Contains("IX4-ENGINE fallback index_reason=SEARCH_INDEX_NOT_READY verified_scan_outcome=COMPLETED", fixture.Stack.Logs("api", 2000), StringComparison.Ordinal);
        }
        finally
        {
            fixture.Stack.Start("worker");
        }

        await WaitCompleteAsync(w);
        Assert.Equal("AuthorityAcceptedContent", Basis(await SearchAsync(w, w.S.Clerk, session, conditions)));
    }

    /// <summary>
    /// §17.2: a search-index quarantine (here caused by an Authority-domain posting change, IX-44) falls back to the complete V1 path,
    /// which reads and verifies the business copy; an S1 organization quarantine never falls back (Q2) and answers as V1 does.
    /// </summary>
    [Fact]
    public async Task ASearchIndexQuarantineFallsBackToV1ButAnOrganizationQuarantineNeverDoes()
    {
        var w = await WorkspaceAsync("ix4-quarantine");
        var a = await CreateAsync(w, Values("QA-01", 1, "2026-02-02"));
        await CreateAsync(w, Values("QA-02", 2, "2026-02-02"));
        await WaitCompleteAsync(w);
        var session = await SessionAsync(w, w.S.Clerk);
        object[] conditions = [Cond(w.Reference, "startsWith", "String", "QA-")];
        Assert.Equal("AuthorityAcceptedContent", Basis(await SearchAsync(w, w.S.Clerk, session, conditions)));
        fixture.Stack.AuthorityAdministratorSql($"SELECT set_config('authority_search.organization_id', '{w.S.OrganizationId:D}', false); UPDATE authority_search.search_string_postings SET normalized_string_value = 'QA-99' WHERE form_record_id = '{a:D}';");

        // The first indexed attempt detects the change and quarantines the index; V1 answers from the verified business copy.
        var answered = await SearchAsync(w, w.S.Clerk, session, conditions);
        Assert.True(answered.IsSuccess, answered.Body);
        Assert.Equal("VerifiedScan", Basis(answered));
        Assert.Equal(2, answered.Root.GetProperty("totalCount").GetInt32());
        Assert.StartsWith("Quarantined|", Scalar($"SELECT projection_status_name || '|' || coalesce(projection_status_reason_code, '') FROM authority_search.search_projection_states WHERE organization_id = '{w.S.OrganizationId:D}';"), StringComparison.Ordinal);
        Assert.Contains("IX4-ENGINE fallback index_reason=SEARCH_INDEX_INCONSISTENT verified_scan_outcome=COMPLETED", fixture.Stack.Logs("api", 2000), StringComparison.Ordinal);

        // S1 organization quarantine: refused before either engine runs (the Authority's capability decision answers 423, §17.2 Q1),
        // identically for the indexed and the V1 caller; the fallback is never attempted.
        var fallbacks = FallbackLogLines();
        fixture.Stack.AuthorityAdministratorSql($"UPDATE authority.organization_authorization_states SET integrity_quarantine_enabled = true, integrity_quarantine_reason_code = 'IX4_TEST_QUARANTINE' WHERE organization_id = '{w.S.OrganizationId:D}';");
        var refused = await SearchAsync(w, w.S.Clerk, session, conditions);
        Assert.False(refused.IsSuccess, refused.Body);
        Assert.Equal((HttpStatusCode.Locked, "INTEGRITY_QUARANTINE"), (refused.Status, refused.Code));
        Assert.DoesNotContain("formRecordId", refused.Body, StringComparison.Ordinal);
        var v1 = await SearchAsync(w, w.S.Clerk, null, conditions);
        Assert.Equal((refused.Status, refused.Code, refused.Reason), (v1.Status, v1.Code, v1.Reason)); // the same answer as the V1 path gives
        Assert.Equal(fallbacks, FallbackLogLines());
    }

    /// <summary>
    /// IX-31, IX-32 (ENC-SR-08/09 under IX-D3, R-A): damage to a matching record's business copy is not detected by indexed search —
    /// the record is returned from Authority-accepted content and never labelled verified; opening it performs the full verified read,
    /// fails and quarantines it; every later search whose scope contains it fails with the V1 reason (D3 parity).
    /// </summary>
    [Fact]
    public async Task IX31IX32BusinessCopyDamageIsDetectedWhenTheRecordIsOpenedNotBySearch()
    {
        var w = await WorkspaceAsync("ix4-business-copy");
        await CreateAsync(w, Values("BC-01", 1, "2026-05-05"));
        var victim = await CreateAsync(w, Values("BC-02", 2, "2026-05-05"));
        await WaitCompleteAsync(w);
        var session = await SessionAsync(w, w.S.Clerk);
        var operation = Scalar($"SELECT committed_operation_id FROM record_content_heads WHERE form_record_id = '{victim:D}';");
        fixture.Stack.AttackerSql($"UPDATE field_string_values SET string_value = 'BC-99' WHERE form_record_id = '{victim:D}' AND content_change_operation_id = '{operation}' AND field_definition_id = '{w.Reference:D}';");

        var found = await SearchAsync(w, w.S.Clerk, session, [Cond(w.Reference, "equals", "String", "BC-02")]);
        Assert.Equal([victim], Ids(found)); // evaluated on the accepted content, not on the damaged business copy
        Assert.Equal("AuthorityAcceptedContent", Basis(found));
        Assert.False(found.Root.GetProperty("items")[0].GetProperty("projectionVerified").GetBoolean());

        var opened = await w.S.ProjectionAsync(w.S.Clerk, victim);
        Assert.False(opened.IsSuccess, opened.Body); // the full verified read on open fails
        Assert.Equal("t", Scalar($"SELECT content_head_quarantined FROM record_content_heads WHERE form_record_id = '{victim:D}';"));

        var after = await SearchAsync(w, w.S.Clerk, session, [Cond(w.Hearing, "equals", "Date", "2026-05-05")]);
        Assert.False(after.IsSuccess, after.Body);
        Assert.Equal(("SEARCH_VERIFICATION_UNAVAILABLE", "RECORD_QUARANTINED"), (after.Code, after.Reason));
        Assert.DoesNotContain("formRecordId", after.Body, StringComparison.Ordinal);
    }

    /// <summary>IX-20 through the business API: any authorization change between pages refuses the next page (409), never a stale or partial page.</summary>
    [Fact]
    public async Task AnAuthorizationChangeBetweenPagesRefusesTheNextPage()
    {
        var w = await WorkspaceAsync("ix4-revocation");
        var records = new List<Guid>();
        for (var i = 0; i < 12; i++)
        {
            records.Add(await CreateAsync(w, Values($"RV-{i:D2}", i, "2026-06-06")));
        }

        await WaitCompleteAsync(w);
        var session = await SessionAsync(w, w.S.Clerk);
        var first = await SearchAsync(w, w.S.Clerk, session, [Cond(w.Reference, "startsWith", "String", "RV-")]);
        Assert.Equal(12, first.Root.GetProperty("totalCount").GetInt32());
        for (var attempt = 0; ; attempt++)
        {
            var share = await fixture.Api.PostAsync($"/api/v1/form-records/{records[0]:D}/shares", w.S.Token(w.S.Clerk), new { recipientUserId = w.S.Reader.AuthorityUserId, recipientTeamId = (Guid?)null, recipientRole = "Reader", fieldKeys = new[] { "case_reference" } });
            if (share.IsSuccess || attempt >= 30)
            {
                Assert.True(share.IsSuccess, share.Body);
                break;
            }

            await Task.Delay(300, Ct);
        }

        var page = await PageAsync(w, w.S.Clerk, session, first.Root.GetProperty("continuationToken").GetString()!);
        Assert.False(page.IsSuccess, page.Body);
        Assert.Equal(HttpStatusCode.Conflict, page.Status);
        Assert.Equal("SEARCH_SNAPSHOT_CHANGED", page.Code);
        Assert.DoesNotContain("formRecordId", page.Body, StringComparison.Ordinal);

        // The Reader's own indexed search sees exactly the shared record (its own scope only).
        var readerSession = await SessionAsync(w, w.S.Reader);
        Assert.Equal([records[0]], Ids(await SearchAsync(w, w.S.Reader, readerSession, [Cond(w.Reference, "startsWith", "String", "RV-")])));
    }
}
