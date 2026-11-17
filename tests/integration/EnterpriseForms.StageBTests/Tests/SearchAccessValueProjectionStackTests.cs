using System.Globalization;
using System.Net;
using System.Text.Json;
using EnterpriseForms.SecurityTests.Harness;
using EnterpriseForms.StageBTests.Harness;
using Xunit;

namespace EnterpriseForms.StageBTests.Tests;

/// <summary>
/// IX-5b (C-148) on the real security-test stack: the dark B2 value projection. Value rows come only from accepted searchable values
/// (Tier 1 postings at the head under Complete Internal coverages), one per reading principal; readiness covers the authorization
/// revision, the content revision and the coverage set. Covers content revisions, sharing paths, revocation, ownership transfer,
/// Confidential and unapproved exclusion, multi-condition correctness (internal evaluation only), content-journal tampering,
/// crash recovery, concurrent maintainers, isolation and copy amplification. No search path reads Tier 2. Synthetic data only;
/// a focused development test class (C-131), not Stage B.
/// </summary>
[Collection(StageBStackCollectionDefinition.Name)]
public sealed partial class SearchAccessValueProjectionStackTests(StageBFixture fixture)
{
    private const string Prefix = "/internal/v1/testing/search-access";

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed record Ws(StageBScenario S, Guid FormId, Guid Count, Guid Status, Guid Notes, Guid Unapproved, Guid Open, Guid Closed, Guid Team);

    // ---- Authority testing endpoints --------------------------------------------------------------------------------------

    private async Task<JsonElement> AuthorityAsync(HttpMethod method, string path, object? body = null)
    {
        var result = await fixture.Authority.SendAsync(method, Prefix + path, TestIdentity.WorkloadToken(), body);
        Assert.True(result.IsSuccess, path + ": " + result.Body);
        return result.Root.Clone();
    }

    private async Task<JsonElement> StatusAsync(Guid org) => await AuthorityAsync(HttpMethod.Get, $"/{org:D}/status");

    private Task<JsonElement> CatchUpAsync(Guid org) => AuthorityAsync(HttpMethod.Post, $"/{org:D}/catch-up", new { maximum_events = 1000 });

    private Task<JsonElement> PauseAsync(bool paused) => AuthorityAsync(HttpMethod.Post, "/maintenance", new { paused });

    private async Task<JsonElement> ReadyAsync(Guid org, int seconds = 90)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        JsonElement status;
        do
        {
            status = (await CatchUpAsync(org)).GetProperty("status");
            if (status.GetProperty("status_name").GetString() == "Ready")
            {
                return status;
            }

            Assert.NotEqual("Quarantined", status.GetProperty("status_name").GetString());
            await Task.Delay(300, Ct);
        }
        while (DateTime.UtcNow < deadline);
        Assert.Fail("not ready: " + status);
        return status;
    }

    private async Task<JsonElement> ReadyAndVerifiedAsync(Guid org)
    {
        for (var attempt = 0; ; attempt++)
        {
            await ReadyAsync(org);
            var verified = await AuthorityAsync(HttpMethod.Post, $"/{org:D}/verify");
            var outcome = verified.GetProperty("outcome_code").GetString();
            if (outcome is "MOVED" or "NOT_AT_HEAD" or "SPICEDB_NOT_READY" && attempt < 20)
            {
                await Task.Delay(500, Ct);
                continue;
            }

            Assert.True(outcome == "CLEAN", "verification: " + verified);
            var replay = await AuthorityAsync(HttpMethod.Get, $"/{org:D}/journal-replay");
            Assert.True(replay.GetProperty("outcome_code").GetString() == "COMPLETE", "journal replay: " + replay);
            return verified;
        }
    }

    private async Task<List<(string Type, Guid Id, Guid Field, Guid Item, long? Integer, Guid? Option)>> ValuesAsync(Guid org, Guid record)
    {
        var result = await fixture.Authority.SendAsync(HttpMethod.Get, Prefix + $"/{org:D}/records/{record:D}/values", TestIdentity.WorkloadToken(), null);
        Assert.True(result.IsSuccess, result.Body);
        using var doc = JsonDocument.Parse(result.Body);
        return doc.RootElement.EnumerateArray().Select(v => (v.GetProperty("principal_type_name").GetString()!, v.GetProperty("principal_entity_id").GetGuid(), v.GetProperty("field_definition_id").GetGuid(), v.GetProperty("manifest_item_id").GetGuid(),
            v.GetProperty("integer_value").ValueKind == JsonValueKind.Number ? v.GetProperty("integer_value").GetInt64() : (long?)null,
            v.GetProperty("field_option_id").ValueKind == JsonValueKind.String ? v.GetProperty("field_option_id").GetGuid() : (Guid?)null)).ToList();
    }

    private async Task<List<Guid>> EvaluateAsync(Ws w, TestUser user, params object[] conditions)
    {
        var evaluated = await AuthorityAsync(HttpMethod.Post, $"/{w.S.OrganizationId:D}/evaluate", new { form_definition_id = w.FormId, authority_user_id = user.AuthorityUserId, conditions });
        return evaluated.GetProperty("form_record_ids").EnumerateArray().Select(e => e.GetGuid()).ToList();
    }

    private static object CountBetween(Ws w, long min, long max) => new { field_definition_id = w.Count, value_type_name = "Integer", minimum_integer = min, maximum_integer = max };

    private static object StatusIs(Ws w, Guid option) => new { field_definition_id = w.Status, value_type_name = "Option", field_option_id = option };

    // ---- business API -----------------------------------------------------------------------------------------------------

    private static async Task<T> RetryAsync<T>(Func<Task<HttpResult>> call, Func<HttpResult, T> success, string what)
    {
        for (var attempt = 0; ; attempt++)
        {
            var result = await call();
            if (result.IsSuccess)
            {
                return success(result);
            }

            Assert.True(attempt < 40 && result.ErrorCode is "AUTHORITY_NOT_READY" or "COMMAND_PENDING" or "STALE_AUTHORIZATION_REVISION" or "CONTENT_REVISION_CONFLICT", what + " failed: " + result.Body);
            await Task.Delay(300, Ct);
        }
    }

    private async Task<Ws> WorkspaceAsync(string name)
    {
        var s = new StageBScenario(fixture, "ix5b-" + name);
        await s.BootstrapAsync();
        var current = await fixture.Api.GetAsync("/api/v1/organization/security-policy", s.Token(s.Designer));
        var policy = await fixture.Api.SendAsync(HttpMethod.Put, "/api/v1/organization/security-policy", s.Token(s.Designer), new { minimumFieldClassification = "Internal", plaintextInternalFieldsPermitted = true, plaintextDowngradePermitted = false, plaintextSearchPermitted = true, policyStatementText = "IX-5b synthetic organization.", expectedPolicyRevision = current.Root.GetProperty("securityPolicyRevision").GetInt64() }, Guid.NewGuid().ToString("N"));
        Assert.True(policy.IsSuccess, policy.Body);
        var formId = await s.CreateFormAsync("IX-5b " + s.Name, s.Clerk);

        async Task<JsonElement> FieldAsync(string key, string type, string classification, string protection, object? options = null, string? mode = null)
        {
            var added = await fixture.Api.PostAsync($"/api/v1/form-definitions/{formId:D}/fields", s.Token(s.Designer), new { fieldKey = key, fieldDisplayName = key, fieldDescription = string.Empty, fieldDataType = type, options, fieldClassification = classification, fieldProtectionMode = protection, optionSelectionMode = mode });
            Assert.True(added.Status == HttpStatusCode.Created, $"add {key}: " + added.Body);
            return added.Root.Clone();
        }

        var count = (await FieldAsync("exhibit_count", "Integer", "Internal", "Standard")).GetProperty("fieldDefinitionId").GetGuid();
        var status = await FieldAsync("case_status", "Option", "Internal", "Standard", new[] { new { optionKey = "open", optionDisplayText = "Open" }, new { optionKey = "closed", optionDisplayText = "Closed" } }, "Single");
        var notes = (await FieldAsync("case_notes", "String", "Confidential", "ApplicationEncrypted")).GetProperty("fieldDefinitionId").GetGuid();
        var unapproved = (await FieldAsync("internal_ref", "Integer", "Internal", "Standard")).GetProperty("fieldDefinitionId").GetGuid();
        var statusId = status.GetProperty("fieldDefinitionId").GetGuid();
        var options = status.GetProperty("options").EnumerateArray().ToDictionary(o => o.GetProperty("optionKey").GetString()!, o => o.GetProperty("fieldOptionId").GetGuid());
        string Snapshot() => string.Join(",", options.Select((o, i) => $$"""{"fieldOptionId":"{{o.Value:D}}","label":"{{o.Key}}","sortOrder":{{i + 1}}}"""));
        await s.SaveDraftAsync(formId, StageBScenario.Layout("IX-5b", StageBScenario.Section("main", "Main",
            StageBScenario.Field(count, "number", "Count", 2),
            $$"""{"kind":"field","fieldDefinitionId":"{{statusId:D}}","control":"select","label":"Status","layout":{"columnSpan":2,"startOnNewRow":false},"optionsSnapshot":[{{Snapshot()}}]}""",
            StageBScenario.Field(notes, "text", "Notes", 4),
            StageBScenario.Field(unapproved, "number", "Internal reference", 2))));
        Assert.True((await s.PublishAsync(formId, "v1")).GetProperty("authorityRegistered").GetBoolean());
        foreach (var (field, ops) in new[] { (count, new[] { "equals" }), (statusId, new[] { "is" }) })
        {
            var approved = await fixture.Api.PostAsync($"/api/v1/form-definitions/{formId:D}/fields/{field:D}/search-approval", s.Token(s.Administrator), new { operators = ops, approvalReference = "IX-5b", useDevelopmentStepUp = true });
            Assert.True(approved.IsSuccess, approved.Body);
        }

        var team = await fixture.Api.PostAsync("/api/v1/teams", s.Token(s.Designer), new { teamName = "IX-5b readers " + s.Name });
        Assert.Equal(HttpStatusCode.Created, team.Status);
        var teamId = team.Root.GetProperty("teamId").GetGuid();
        await RetryAsync(() => fixture.Api.PostAsync($"/api/v1/teams/{teamId:D}/members", s.Token(s.Designer), new { userId = s.Administrator.AuthorityUserId }, "ix5b-add-" + Guid.NewGuid().ToString("N")), _ => true, "add member");
        await s.WaitGateReadyAsync();
        return new Ws(s, formId, count, statusId, notes, unapproved, options["open"], options["closed"], teamId);
    }

    private Task WaitCompleteAsync(Ws w) => WaitCompleteAsync(w.S.OrganizationId, "2/2");

    /// <summary>Waits until the organization's non-withdrawn coverages report <paramref name="expected"/> (Complete/total).</summary>
    private async Task WaitCompleteAsync(Guid organizationId, string expected)
    {
        var deadline = DateTime.UtcNow.AddSeconds(300);
        string last;
        do
        {
            last = fixture.Stack.AuthorityObserverSql($"SELECT count(*) FILTER (WHERE coverage_state_name = 'Complete') || '/' || count(*) FROM authority_search.search_field_coverages WHERE organization_id = '{organizationId:D}' AND coverage_state_name <> 'Withdrawn';").Trim().Split('\n')[0].Trim();
            if (last == expected)
            {
                return;
            }

            await Task.Delay(1000, Ct);
        }
        while (DateTime.UtcNow < deadline);
        Assert.Fail("coverages not Complete: " + last);
    }

    private async Task<Guid> CreateAsync(Ws w, int count, string status, string notes)
    {
        var values = new Dictionary<string, string> { ["exhibit_count"] = count.ToString(CultureInfo.InvariantCulture), ["case_status"] = status, ["case_notes"] = notes, ["internal_ref"] = "99" };
        var id = await RetryAsync(() => fixture.Api.PostAsync($"/api/v1/form-definitions/{w.FormId:D}/records", w.S.Token(w.S.Clerk), new { values }, "ix5b-" + Guid.CreateVersion7().ToString("N")), r => r.Root.GetProperty("formRecordId").GetGuid(), "create");
        await w.S.WaitProjectionAsync(w.S.Clerk, id);
        return id;
    }

    private async Task EditAsync(Ws w, TestUser owner, Guid record, Dictionary<string, string> values)
    {
        await RetryAsync(async () =>
        {
            var revision = (await w.S.WaitProjectionAsync(owner, record)).Root.GetProperty("record").GetProperty("contentRevision").GetInt64();
            return await fixture.Api.PatchAsync($"/api/v1/form-records/{record:D}", w.S.Token(owner), new { expectedContentRevision = revision, values });
        }, _ => true, "edit");
        await w.S.WaitProjectionAsync(owner, record);
    }

    private async Task<Guid> ShareAsync(Ws w, Guid record, Guid? user, Guid? team, params string[] fieldKeys)
    {
        var id = await RetryAsync(() => fixture.Api.PostAsync($"/api/v1/form-records/{record:D}/shares", w.S.Token(w.S.Clerk), new { recipientUserId = user, recipientTeamId = team, recipientRole = "Reader", fieldKeys }), r => r.Root.GetProperty("recordShareId").GetGuid(), "share");
        await w.S.WaitGateReadyAsync();
        return id;
    }

    // ---- tests ------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task ValueRowsFollowContentSharingRevocationAndOwnershipAndExcludeConfidentialAndUnapprovedFields()
    {
        var w = await WorkspaceAsync("paths");
        var org = w.S.OrganizationId;
        await WaitCompleteAsync(w);
        var r1 = await CreateAsync(w, 5, "open", "synthetic secret one");
        var r2 = await CreateAsync(w, 7, "closed", "synthetic secret two");
        var direct = await ShareAsync(w, r1, w.S.Reader.AuthorityUserId, null, "exhibit_count");
        await ShareAsync(w, r2, null, w.Team, "case_status");
        var verified = await ReadyAndVerifiedAsync(org);

        var v1 = await ValuesAsync(org, r1);
        Assert.True(v1.Where(v => v.Id == w.S.Clerk.AuthorityUserId).Select(v => v.Field).ToHashSet().SetEquals([w.Count, w.Status])); // the owner: both searchable fields
        Assert.Equal([(w.Count, 5L)], v1.Where(v => v.Id == w.S.Reader.AuthorityUserId).Select(v => (v.Field, v.Integer ?? 0)).ToList()); // the direct share's field only
        Assert.True((await ValuesAsync(org, r2)).Where(v => v.Type == "team").Select(v => (v.Id, v.Field)).SequenceEqual([(w.Team, w.Status)])); // the team principal
        Assert.DoesNotContain((await ValuesAsync(org, r2)), v => v.Id == w.S.Administrator.AuthorityUserId); // never the team member
        foreach (var record in new[] { r1, r2 })
        {
            Assert.DoesNotContain(await ValuesAsync(org, record), v => v.Field == w.Notes || v.Field == w.Unapproved); // Confidential/encrypted and unapproved: never
        }

        // A content revision: the changed value is projected after consumption; readiness closed meanwhile.
        await PauseAsync(true);
        try
        {
            await EditAsync(w, w.S.Clerk, r1, new() { ["exhibit_count"] = "6" });
            var building = await StatusAsync(org);
            Assert.Equal("Building", building.GetProperty("status_name").GetString());
            Assert.True(building.GetProperty("consumed_content_integrity_revision").GetInt64() < building.GetProperty("current_content_integrity_revision").GetInt64());
        }
        finally
        {
            await PauseAsync(false);
        }

        await ReadyAndVerifiedAsync(org);
        Assert.All((await ValuesAsync(org, r1)).Where(v => v.Field == w.Count), v => Assert.Equal(6, v.Integer));

        // Multi-condition correctness (internal evaluation only): owner, direct share, team path, share-only field rule.
        Assert.Equal([r1], await EvaluateAsync(w, w.S.Clerk, CountBetween(w, 6, 6), StatusIs(w, w.Open)));
        Assert.Equal([r2], await EvaluateAsync(w, w.S.Clerk, CountBetween(w, 7, 7)));
        Assert.Equal([r1], await EvaluateAsync(w, w.S.Reader, CountBetween(w, 1, 10)));
        Assert.Empty(await EvaluateAsync(w, w.S.Reader, CountBetween(w, 6, 6), StatusIs(w, w.Open))); // status not shared to Reader
        Assert.Equal([r2], await EvaluateAsync(w, w.S.Administrator, StatusIs(w, w.Closed))); // through the team
        Assert.Empty(await EvaluateAsync(w, w.S.Administrator, CountBetween(w, 1, 10)));

        // Revocation: closed at once; the old rows go once consumed.
        await PauseAsync(true);
        try
        {
            await RetryAsync(() => fixture.Api.PostAsync($"/api/v1/form-records/{r1:D}/shares/{direct:D}/revoke", w.S.Token(w.S.Clerk), new { }), _ => true, "revoke");
            await w.S.WaitGateReadyAsync();
            Assert.Equal("Building", (await StatusAsync(org)).GetProperty("status_name").GetString());
            Assert.Contains(await ValuesAsync(org, r1), v => v.Id == w.S.Reader.AuthorityUserId); // present, but not servable
        }
        finally
        {
            await PauseAsync(false);
        }

        await ReadyAndVerifiedAsync(org);
        Assert.DoesNotContain(await ValuesAsync(org, r1), v => v.Id == w.S.Reader.AuthorityUserId);
        Assert.Empty(await EvaluateAsync(w, w.S.Reader, CountBetween(w, 1, 10)));

        // Ownership transfer to the team: the owner's value rows move to the team principal.
        await RetryAsync(() => fixture.Api.PostAsync($"/api/v1/form-records/{r1:D}/transfer-ownership", w.S.Token(w.S.Clerk), new { newOwnerUserId = (Guid?)null, newOwnerTeamId = w.Team, transferReason = "IX-5b transfer" }), _ => true, "transfer");
        await w.S.WaitGateReadyAsync();
        var after = await ReadyAndVerifiedAsync(org);
        var r1Values = await ValuesAsync(org, r1);
        Assert.DoesNotContain(r1Values, v => v.Id == w.S.Clerk.AuthorityUserId);
        Assert.True(r1Values.Where(v => v.Type == "team").Select(v => v.Field).ToHashSet().SetEquals([w.Count, w.Status]));

        // Copy amplification and cost evidence (counts only; timings are not reported on battery power).
        var report = new { searchable_postings = after.GetProperty("searchable_postings").GetInt32(), value_postings = after.GetProperty("value_postings").GetInt32(), first = new { searchable_postings = verified.GetProperty("searchable_postings").GetInt32(), value_postings = verified.GetProperty("value_postings").GetInt32() } };
        Directory.CreateDirectory(Path.Combine(ComposeStack.RepositoryRoot, "artifacts", "ix-5"));
        await File.WriteAllTextAsync(Path.Combine(ComposeStack.RepositoryRoot, "artifacts", "ix-5", "ix5b-copy-amplification.json"), JsonSerializer.Serialize(report, Indented), Ct);
    }

    [Theory]
    [InlineData("body", "ACCESS_JOURNAL_DIGEST_MISMATCH")]
    [InlineData("gap", "ACCESS_JOURNAL_GAP")]
    public async Task ContentJournalTamperingQuarantinesTier2(string tamper, string expectedCode)
    {
        var w = await WorkspaceAsync("tamper-" + tamper);
        var org = w.S.OrganizationId;
        await WaitCompleteAsync(w);
        var record = await CreateAsync(w, 1, "open", "n");
        await ReadyAsync(org);
        await PauseAsync(true);
        try
        {
            await EditAsync(w, w.S.Clerk, record, new() { ["exhibit_count"] = "2" });
            var head = long.Parse(fixture.Stack.AuthorityObserverSql($"SELECT content_integrity_revision FROM organization_authorization_states WHERE organization_id = '{org:D}';").Trim().Split('\n')[0].Trim(), CultureInfo.InvariantCulture);
            var where = $"organization_id = '{org:D}' AND journal_stream_name = 'content' AND journal_stream_revision = {head}";
            fixture.Stack.AuthorityAdministratorSql(tamper == "body"
                ? $"UPDATE authority.security_journal_entries SET journal_event_body_json = replace(journal_event_body_json, '\"committed_content_revision\"', '\"committed_content_revisioN\"') WHERE {where};"
                : $"UPDATE authority.security_journal_entries SET journal_stream_revision = journal_stream_revision + 1000000 WHERE {where};");
            Assert.Equal("Quarantined", (await CatchUpAsync(org)).GetProperty("outcome_name").GetString());
            var status = await StatusAsync(org);
            Assert.Equal(("Quarantined", expectedCode), (status.GetProperty("status_name").GetString(), status.GetProperty("quarantine_reason_code").GetString()));
        }
        finally
        {
            await PauseAsync(false);
        }

        await Task.Delay(1500, Ct);
        Assert.Equal("Quarantined", (await StatusAsync(org)).GetProperty("status_name").GetString());
    }

    [Fact]
    public async Task ACrashedContentStepAndConcurrentMaintainersEndExactlyConsistent()
    {
        var w = await WorkspaceAsync("crash");
        var org = w.S.OrganizationId;
        await WaitCompleteAsync(w);
        var record = await CreateAsync(w, 1, "open", "n");
        var before = await ReadyAsync(org);
        const string Marker = "FAULT-INJECTION: terminating process at 'CrashBeforeAccessProjectionCommit'";
        int Crashes() => fixture.Stack.Logs("authority", 5000).Split(Marker).Length - 1;
        var crashesBefore = Crashes();
        await PauseAsync(true);
        await EditAsync(w, w.S.Clerk, record, new() { ["exhibit_count"] = "2" });
        Assert.True((await fixture.Authority.OperatorAsync(HttpMethod.Post, "/internal/v1/operator/fault-injection", new { fault_name = "CrashBeforeAccessProjectionCommit" })).IsSuccess);
        try
        {
            _ = await fixture.Authority.SendAsync(HttpMethod.Post, Prefix + $"/{org:D}/catch-up", TestIdentity.WorkloadToken(), new { maximum_events = 1000 });
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            // the process terminated mid-transaction (expected)
        }

        var deadline = DateTime.UtcNow.AddSeconds(90);
        while (Crashes() <= crashesBefore)
        {
            Assert.True(DateTime.UtcNow < deadline, "the armed crash did not happen");
            await Task.Delay(1000, Ct);
        }

        for (var wait = DateTime.UtcNow.AddSeconds(240); ; )
        {
            try
            {
                await w.S.WaitGateReadyAsync(TimeSpan.FromSeconds(30));
                break;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException && DateTime.UtcNow < wait)
            {
                await Task.Delay(2000, Ct);
            }
        }

        await fixture.Authority.OperatorAsync(HttpMethod.Post, "/internal/v1/operator/fault-injection", new { fault_name = "None" });

        // Concurrent maintainers over several content events.
        await PauseAsync(true);
        try
        {
            for (var i = 3; i <= 5; i++)
            {
                await EditAsync(w, w.S.Clerk, record, new() { ["exhibit_count"] = i.ToString(CultureInfo.InvariantCulture) });
            }

            await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => CatchUpAsync(org)));
        }
        finally
        {
            await PauseAsync(false);
        }

        var after = await ReadyAsync(org);
        Assert.Equal(after.GetProperty("current_content_integrity_revision").GetInt64(), after.GetProperty("consumed_content_integrity_revision").GetInt64());
        Assert.True(after.GetProperty("checkpoint_revision").GetInt64() > before.GetProperty("checkpoint_revision").GetInt64());
        await ReadyAndVerifiedAsync(org);
        Assert.All((await ValuesAsync(org, record)).Where(v => v.Field == w.Count), v => Assert.Equal(5, v.Integer));
    }

    [Fact]
    public async Task ValuePostingsAreIsolatedByOrganizationAndHiddenFromTheSearchReader()
    {
        var a = await WorkspaceAsync("iso-a");
        await WaitCompleteAsync(a);
        await CreateAsync(a, 1, "open", "n");
        await ReadyAsync(a.S.OrganizationId);
        var b = new StageBScenario(fixture, "ix5b-iso-b");
        await b.BootstrapAsync();
        var role = fixture.Stack.AuthorityObserverSql("SELECT grantee FROM information_schema.role_table_grants WHERE table_schema = 'authority' AND table_name = 'security_journal_entries' AND privilege_type = 'INSERT' AND grantee NOT IN (SELECT rolname FROM pg_roles WHERE rolsuper) LIMIT 1;").Trim().Split('\n')[0].Trim();
        string As(string asRole, Guid? org, string sql) => fixture.Stack.AuthorityAdministratorSql(
            $"BEGIN; SET LOCAL ROLE {asRole};{(org is { } o ? $" SELECT set_config('authority_search_access.organization_id', '{o:D}', true);" : string.Empty)} {sql}; COMMIT;", throwOnError: false);
        static string Last(string output) => output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.Trim()).Last(l => l.Length > 0 && char.IsDigit(l[0]));
        Assert.NotEqual("0", Last(As(role, a.S.OrganizationId, "SELECT count(*) FROM authority_search_access.search_access_value_postings")));
        Assert.Equal("0", Last(As(role, b.OrganizationId, $"SELECT count(*) FROM authority_search_access.search_access_value_postings WHERE organization_id = '{a.S.OrganizationId:D}'")));
        Assert.Equal("0", Last(As(role, null, "SELECT count(*) FROM authority_search_access.search_access_value_postings")));
        Assert.Contains("permission denied", As(role, a.S.OrganizationId, "UPDATE authority_search_access.search_access_value_postings SET integer_value = integer_value WHERE false"), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("permission denied", As("authority_search_reader", a.S.OrganizationId, "SELECT count(*) FROM authority_search_access.search_access_value_postings"), StringComparison.OrdinalIgnoreCase);
    }
}
