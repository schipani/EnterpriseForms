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
/// C-188 BF-1 on the real stack (business API, Authority, SpiceDB, Vault Transit, both PostgreSQL servers, the search index builder).
/// A published form version the Authority has not registered must never leave a stale field search approval usable. The business
/// stages each version with the Authority before committing it; while that publication is pending, field search on the form fails
/// closed on every path - business and Authority capabilities, the V1 (verified scan) and indexed engines, open pages of both, and new
/// approvals - including when the publication removes an approved field (BF-1) and when it brings back a field under a retained
/// approval. Recovery completes or withdraws the pending publication without a new publication; concurrent security commands never
/// open a window; the database refuses to revive, rewrite or duplicate a pending publication, and the runtime cannot delete one.
/// Lost registrations and lost commits are produced with the Testing-only publication fault checkpoints. Synthetic data only.
/// </summary>
[Collection(StageBStackCollectionDefinition.Name)]
public sealed class FormVersionPublicationBarrierTests(StageBFixture fixture)
{
    private const string LostRegistration = "publish:after-commit";
    private const string LostCommit = "publish:after-prepare";

    private sealed record Ws(StageBScenario S, Guid FormId, Guid First, Guid Phone, Guid Email);

    private sealed record Session(string Digest, TestUser User);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ---------------------------------------------------------------- helpers

    private string Scalar(string sql) => fixture.Stack.AuthorityObserverSql(sql).Trim();

    /// <summary>The field's approval rows, oldest first: id|state|reason|scope.</summary>
    private List<string> Approvals(Ws w, Guid field) =>
        Scalar($"SELECT field_search_approval_id || '|' || approval_state_name || '|' || coalesce(invalidation_reason_code, '') || '|' || approval_scope_name FROM field_search_approvals WHERE organization_id = '{w.S.OrganizationId:D}' AND field_definition_id = '{field:D}' ORDER BY approved_on;")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    private static string Tail(string row) => string.Join('|', row.Split('|').Skip(1));

    /// <summary>The form's staged publications, oldest first: number|state.</summary>
    private List<string> Publications(Ws w) =>
        Scalar($"SELECT form_version_number || '|' || publication_state_name FROM form_version_publications WHERE organization_id = '{w.S.OrganizationId:D}' AND form_definition_id = '{w.FormId:D}' ORDER BY prepared_on;")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    private int LatestRegistered(Ws w) =>
        int.Parse(Scalar($"SELECT coalesce(max(form_version_number), 0) FROM published_form_versions WHERE organization_id = '{w.S.OrganizationId:D}' AND form_definition_id = '{w.FormId:D}';"), System.Globalization.CultureInfo.InvariantCulture);

    private static async Task<int> CurrentVersionAsync(Ws w) => (await w.S.GetFormAsync(w.FormId)).GetProperty("summary").GetProperty("currentVersionNumber").GetInt32();

    private async Task<Ws> WorkspaceAsync(string name)
    {
        var s = new StageBScenario(fixture, name);
        await s.BootstrapAsync();
        var current = await fixture.Api.GetAsync("/api/v1/organization/security-policy", s.Token(s.Designer));
        var policy = await fixture.Api.SendAsync(HttpMethod.Put, "/api/v1/organization/security-policy", s.Token(s.Designer), new { minimumFieldClassification = "Internal", plaintextInternalFieldsPermitted = true, plaintextDowngradePermitted = false, plaintextSearchPermitted = true, policyStatementText = "Selected plaintext Internal fields may be searched in this synthetic organization.", expectedPolicyRevision = current.Root.GetProperty("securityPolicyRevision").GetInt64() }, Guid.NewGuid().ToString("N"));
        Assert.True(policy.IsSuccess, policy.Body);
        var formId = await s.CreateFormAsync("BF-1 " + Guid.NewGuid().ToString("N")[..6], s.Clerk, s.Designer);
        async Task<Guid> FieldAsync(string key, string label)
        {
            var added = await fixture.Api.PostAsync($"/api/v1/form-definitions/{formId:D}/fields", s.Token(s.Designer), new { fieldKey = key, fieldDisplayName = label, fieldDescription = string.Empty, fieldDataType = "String", options = (object?)null, fieldClassification = "Internal", fieldProtectionMode = "Standard" });
            Assert.True(added.Status == HttpStatusCode.Created, $"add {key}: " + added.Body);
            return added.Root.GetProperty("fieldDefinitionId").GetGuid();
        }

        var w = new Ws(s, formId, await FieldAsync("first_name", "First name"), await FieldAsync("phone_number", "Phone"), await FieldAsync("email_address", "Email"));
        Assert.Equal(HttpStatusCode.NoContent, (await fixture.Api.SendAsync(HttpMethod.Put, $"/api/v1/form-definitions/{formId:D}/field-searchers", s.Token(s.Administrator), new { grantUserIds = new[] { s.Clerk.AuthorityUserId }, grantTeamIds = Array.Empty<Guid>(), revokeUserIds = Array.Empty<Guid>(), revokeTeamIds = Array.Empty<Guid>() }, Guid.NewGuid().ToString("N"))).Status);
        await s.WaitGateReadyAsync(TimeSpan.FromSeconds(40));
        return w;
    }

    private static string LayoutOf(Ws w, params (Guid Field, string Label)[] elements) =>
        StageBScenario.Layout("BF-1", StageBScenario.Section("main", "Main", elements.Select(e => StageBScenario.Field(e.Field, "text", e.Label, 4)).ToArray()));

    private static async Task<int> PublishAsync(Ws w, params (Guid Field, string Label)[] elements)
    {
        await w.S.SaveDraftAsync(w.FormId, LayoutOf(w, elements));
        var published = await w.S.PublishAsync(w.FormId, "bf1 " + string.Join(", ", elements.Select(e => e.Label)));
        Assert.True(published.GetProperty("authorityRegistered").GetBoolean(), published.ToString());
        Assert.False(published.GetProperty("authorityRegistrationPending").GetBoolean());
        await w.S.WaitGateReadyAsync(TimeSpan.FromSeconds(40));
        return published.GetProperty("versionNumber").GetInt32();
    }

    /// <summary>A publish with a Testing-only publication fault (bearer client, no retry): the response is the injected 503.</summary>
    private static async Task<HttpResult> PublishWithFaultAsync(Ws w, string fault, params (Guid Field, string Label)[] elements)
    {
        var draftRevision = await w.S.SaveDraftAsync(w.FormId, LayoutOf(w, elements));
        var current = await CurrentVersionAsync(w);
        using var client = new HttpClient { BaseAddress = new Uri(StageBFixture.ApiBaseAddress), Timeout = TimeSpan.FromMinutes(2) };
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/form-definitions/{w.FormId:D}/publish") { Content = JsonContent.Create(new { expectedDraftRevision = draftRevision, expectedCurrentVersionNumber = current, changeReason = "bf1 fault " + fault }) };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", w.S.Token(w.S.Designer));
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("N"));
        request.Headers.Add("X-Testing-Fault", fault);
        using var response = await client.SendAsync(request, Ct);
        var body = await response.Content.ReadAsStringAsync(Ct);
        var result = new HttpResult(response.StatusCode, body, body.Length > 0 ? JsonDocument.Parse(body) : null);
        await w.S.WaitGateReadyAsync(TimeSpan.FromSeconds(40));
        return result;
    }

    /// <summary>The publish request alone, for a race: the Authority's transient refusals are retried as the SPA does; nothing is published by a refused attempt.</summary>
    private async Task<JsonElement> PublishRacingAsync(Ws w, long expectedDraftRevision, int expectedCurrentVersionNumber, string changeReason)
    {
        for (var attempt = 0; ; attempt++)
        {
            var published = await fixture.Api.PostAsync($"/api/v1/form-definitions/{w.FormId:D}/publish", w.S.Token(w.S.Designer), new { expectedDraftRevision, expectedCurrentVersionNumber, changeReason });
            if (published.Status == HttpStatusCode.Created)
            {
                return published.Root.Clone();
            }

            Assert.True(attempt < 80 && published.ErrorCode is "AUTHORITY_NOT_READY" or "STALE_AUTHORIZATION_REVISION" or "COMMAND_PENDING" or "AUTHORITY_UNAVAILABLE", "publish failed: " + published.Body);
            await Task.Delay(250, Ct);
        }
    }

    private async Task<HttpResult> ApproveAsync(Ws w, Guid field, string? scope = null)
    {
        var result = await fixture.Api.PostAsync($"/api/v1/form-definitions/{w.FormId:D}/fields/{field:D}/search-approval", w.S.Token(w.S.Administrator), new { operators = new[] { "equals", "startsWith" }, approvalReference = "C-188 BF-1", useDevelopmentStepUp = true, approvalScope = scope });
        if (result.IsSuccess)
        {
            await w.S.WaitGateReadyAsync(TimeSpan.FromSeconds(40));
        }

        return result;
    }

    private async Task<Guid> CreateAsync(Ws w, Dictionary<string, string> values)
    {
        var key = "bf1-" + Guid.CreateVersion7().ToString("N");
        for (var attempt = 0; ; attempt++)
        {
            var created = await fixture.Api.PostAsync($"/api/v1/form-definitions/{w.FormId:D}/records", w.S.Token(w.S.Clerk), new { values, ownerTeamId = (Guid?)null }, key);
            if (created.Status is HttpStatusCode.Created or HttpStatusCode.OK)
            {
                var id = created.Root.GetProperty("formRecordId").GetGuid();
                await w.S.WaitProjectionAsync(w.S.Clerk, id, TimeSpan.FromSeconds(40));
                return id;
            }

            Assert.True(attempt < 60 && created.ErrorCode is "AUTHORITY_NOT_READY" or "STALE_AUTHORIZATION_REVISION" or "COMMAND_PENDING", "create failed: " + created.Body);
            await Task.Delay(500, Ct);
        }
    }

    private async Task<HttpResult> SearchAsync(Ws w, Guid field, string op, string value, int pageSize = 25)
    {
        for (var attempt = 0; ; attempt++)
        {
            var result = await fixture.Api.PostAsync("/api/v1/form-records/field-search", w.S.Token(w.S.Clerk), new
            {
                formDefinitionId = w.FormId,
                conditions = new[] { new { fieldDefinitionId = field, @operator = op, valueType = "String", value, lowerBound = (string?)null, upperBound = (string?)null } },
                recordStatus = (string?)null, includeArchived = false, includeChildren = false, sortBy = (string?)null, sortDirection = (string?)null, pageSize,
            });
            var retryable = result.Body.Length > 0 && result.Json is not null && result.Root.ValueKind == JsonValueKind.Object && result.Root.TryGetProperty("retryable", out var r) && r.GetBoolean();
            if (result.IsSuccess || !retryable || attempt >= 40)
            {
                return result;
            }

            await Task.Delay(500, Ct);
        }
    }

    private static HashSet<Guid> Matches(HttpResult result)
    {
        Assert.True(result.IsSuccess, result.Body);
        Assert.Equal("Complete", result.Root.GetProperty("completeness").GetString());
        return result.Root.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("formRecordId").GetGuid()).ToHashSet();
    }

    /// <summary>BF-1: refused because the form's publication awaits registration - never an empty result, never a partial one.</summary>
    private static void AssertPending(HttpResult result)
    {
        Assert.False(result.IsSuccess, result.Body);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, result.Status);
        Assert.Equal("SEARCH_VERIFICATION_UNAVAILABLE", result.ErrorCode);
        Assert.Equal("FORM_PUBLICATION_PENDING", result.Root.GetProperty("unavailable_reason").GetString());
        Assert.False(result.Root.GetProperty("retryable").GetBoolean());
        Assert.False(result.Root.TryGetProperty("items", out _));
        Assert.False(result.Root.TryGetProperty("totalCount", out _));
    }

    private static void AssertNotAllowed(HttpResult result)
    {
        Assert.False(result.IsSuccess, result.Body);
        Assert.Equal("SEARCH_FIELD_NOT_ALLOWED", result.ErrorCode);
        Assert.False(result.Root.TryGetProperty("items", out _));
    }

    private async Task<JsonElement> CapabilitiesAsync(Ws w)
    {
        var capabilities = await fixture.Api.GetAsync($"/api/v1/form-records/search-capabilities?formDefinitionId={w.FormId:D}", w.S.Token(w.S.Clerk));
        Assert.True(capabilities.IsSuccess, capabilities.Body);
        return capabilities.Root.Clone();
    }

    /// <summary>The Authority's capabilities answered directly (internal API, the clerk's delegated assertion), not through the business API.</summary>
    private async Task<JsonElement> AuthorityCapabilitiesAsync(Ws w, TestUser user)
    {
        for (var attempt = 0; ; attempt++)
        {
            var result = await fixture.Authority.SendAsync(HttpMethod.Post, "/internal/v1/field-search/capabilities", TestIdentity.WorkloadToken(), new { organization_id = w.S.OrganizationId, form_definition_id = w.FormId }, w.S.Token(user));
            if (result.IsSuccess || attempt >= 40 || result.ErrorCode != "AUTHORITY_NOT_READY")
            {
                Assert.True(result.IsSuccess, result.Body);
                return result.Root.Clone();
            }

            await Task.Delay(500, Ct);
        }
    }

    private async Task<Session> SessionAsync(Ws w, TestUser user)
    {
        var digest = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var created = await fixture.Authority.SendAsync(HttpMethod.Post, "/internal/v1/sessions", TestIdentity.WorkloadToken(), new { organization_id = w.S.OrganizationId, session_digest_base64 = digest, absolute_expires_on = DateTimeOffset.UtcNow.AddHours(1) }, w.S.Token(user));
        Assert.True(created.IsSuccess, created.Body);
        return new Session(digest, user);
    }

    private static object IndexedQuery(Ws w, Guid field, string prefix, int pageSize) =>
        new { form_definition_id = w.FormId, conditions = new[] { new { field_definition_id = field, @operator = "startsWith", value_type = "String", value = prefix, lower_bound = (string?)null, upper_bound = (string?)null } }, record_status = (string?)null, include_children = false, include_archived = false, sort_by = (string?)null, sort_direction = (string?)null, page_size = pageSize };

    /// <summary>The indexed engine (Authority internal query, direct); retried only on its typed transient answers, including a coverage still being built.</summary>
    private async Task<(HttpResult Result, string Nonce, object Query)> IndexedAsync(Ws w, Session session, Guid field, string prefix, int pageSize = 25)
    {
        var query = IndexedQuery(w, field, prefix, pageSize);
        for (var attempt = 0; ; attempt++)
        {
            var nonce = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
            var result = await fixture.Authority.SendAsync(HttpMethod.Post, "/internal/v1/search/indexed-query", TestIdentity.WorkloadToken(),
                new { organization_id = w.S.OrganizationId, session_digest_base64 = session.Digest, query_nonce_base64 = nonce, query }, w.S.Token(session.User));
            if (result.IsSuccess || attempt >= 120 || result.ErrorCode is not ("AUTHORITY_NOT_READY" or "SEARCH_SNAPSHOT_CHANGED" or "SEARCH_INDEX_NOT_READY"))
            {
                return (result, nonce, query);
            }

            await Task.Delay(1000, Ct);
        }
    }

    private Task<HttpResult> IndexedPageAsync(Ws w, Session session, string nonce, object query, Guid snapshot, int offset) =>
        fixture.Authority.SendAsync(HttpMethod.Post, $"/internal/v1/search/indexed-query/{snapshot:D}/page", TestIdentity.WorkloadToken(),
            new { organization_id = w.S.OrganizationId, session_digest_base64 = session.Digest, query_nonce_base64 = nonce, query, offset }, w.S.Token(session.User));

    private static HashSet<Guid> IndexedIds(HttpResult result)
    {
        Assert.True(result.IsSuccess, result.Body);
        return result.Root.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("form_record_id").GetGuid()).ToHashSet();
    }

    private async Task<JsonElement> RegistrationAsync(Ws w)
    {
        var status = await fixture.Api.GetAsync($"/api/v1/form-definitions/{w.FormId:D}/authority-registration", w.S.Token(w.S.Designer));
        Assert.True(status.IsSuccess, status.Body);
        return status.Root.Clone();
    }

    private async Task<JsonElement> RecoverAsync(Ws w)
    {
        for (var attempt = 0; ; attempt++)
        {
            var recovered = await fixture.Api.PostAsync($"/api/v1/form-definitions/{w.FormId:D}/authority-registration/recover", w.S.Token(w.S.Designer), new { });
            if (recovered.IsSuccess || attempt >= 40 || recovered.ErrorCode != "AUTHORITY_UNAVAILABLE")
            {
                Assert.True(recovered.IsSuccess, recovered.Body);
                await w.S.WaitGateReadyAsync(TimeSpan.FromSeconds(40));
                return recovered.Root.Clone();
            }

            await Task.Delay(500, Ct);
        }
    }

    private static (string? Outcome, string State, int? Current, int? Registered, int? Pending) Describe(JsonElement r) =>
        (r.TryGetProperty("outcome", out var o) && o.ValueKind == JsonValueKind.String ? o.GetString() : null, r.GetProperty("registrationState").GetString()!,
            Number(r, "currentVersionNumber"), Number(r, "registeredVersionNumber"), Number(r, "pendingVersionNumber"));

    private static int? Number(JsonElement r, string name) => r.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : null;

    private string BusinessAudit(Ws w, string action) =>
        fixture.Stack.AttackerSql($"SELECT coalesce(string_agg(event_metadata_json::text, '~' ORDER BY audit_sequence), '') FROM audit_events WHERE tenant_id = '{w.S.OrganizationId:D}' AND event_action = '{action}';").Replace(" ", string.Empty, StringComparison.Ordinal);

    // ---------------------------------------------------------------- BF-1: a removal whose registration is lost

    [Fact]
    public async Task ALostRegistrationOfARemovalKeepsFieldSearchClosedOnEveryPathUntilRecovered()
    {
        var w = await WorkspaceAsync("bf1-removal");
        Assert.Equal(1, await PublishAsync(w, (w.First, "First name"), (w.Phone, "Phone")));
        Assert.True((await ApproveAsync(w, w.Phone)).IsSuccess);
        var records = new List<Guid>();
        for (var i = 0; i < 12; i++)
        {
            records.Add(await CreateAsync(w, new() { ["first_name"] = $"P{i}", ["phone_number"] = $"555-03{i:00}" }));
        }

        // Open pages on both engines before the publication.
        var v1Page = await SearchAsync(w, w.Phone, "startsWith", "555-03", 10);
        Assert.Equal(12, v1Page.Root.GetProperty("totalCount").GetInt32());
        var v1Next = v1Page.Root.GetProperty("continuationToken").GetString();
        Assert.False(string.IsNullOrEmpty(v1Next));
        var clerk = await SessionAsync(w, w.S.Clerk);
        var (indexedFirst, nonce, query) = await IndexedAsync(w, clerk, w.Phone, "555-03", 10);
        Assert.Equal(10, IndexedIds(indexedFirst).Count);
        var indexedSnapshot = indexedFirst.Root.GetProperty("search_result_snapshot_id").GetGuid();

        // V2 removes phone. The business commits it; the registration is then lost (fault after the commit).
        var lost = await PublishWithFaultAsync(w, LostRegistration, (w.First, "First name"), (w.Email, "Email"));
        Assert.Equal((HttpStatusCode.ServiceUnavailable, "TESTING_FAULT_INJECTED"), (lost.Status, lost.ErrorCode));
        Assert.Equal(2, await CurrentVersionAsync(w)); // the new layout is current in the business
        Assert.Equal(1, LatestRegistered(w)); // the Authority never registered it
        Assert.Equal(["1|Registered", "2|Pending"], Publications(w));
        Assert.Equal("Active||CurrentVersion", Tail(Approvals(w, w.Phone).Single())); // not invalidated: the Authority does not know the outcome

        // Closed on every path while the publication is pending (BF-1).
        var capabilities = await CapabilitiesAsync(w);
        Assert.False(capabilities.GetProperty("fieldValueSearchAvailable").GetBoolean());
        Assert.Equal("FORM_PUBLICATION_PENDING", capabilities.GetProperty("unavailableReasonCode").GetString());
        Assert.Empty(capabilities.GetProperty("fields").EnumerateArray());
        var direct = await AuthorityCapabilitiesAsync(w, w.S.Clerk);
        Assert.True(direct.GetProperty("publication_pending").GetBoolean());
        Assert.Empty(direct.GetProperty("fields").EnumerateArray());
        AssertPending(await SearchAsync(w, w.Phone, "equals", "555-0300")); // V1 engine
        Assert.Equal("SEARCH_SNAPSHOT_CHANGED", (await fixture.Api.PostAsync("/api/v1/form-records/field-search/page", w.S.Token(w.S.Clerk), new { cursor = v1Next })).ErrorCode); // open V1 page
        AssertNotAllowed((await IndexedAsync(w, clerk, w.Phone, "555-03")).Result); // indexed engine, directly at the Authority
        var indexedPage = await IndexedPageAsync(w, clerk, nonce, query, indexedSnapshot, 10); // open indexed page
        Assert.False(indexedPage.IsSuccess, indexedPage.Body);
        Assert.False(indexedPage.Body.Contains("form_record_id", StringComparison.Ordinal));
        Assert.Equal("FORM_PUBLICATION_PENDING", (await ApproveAsync(w, w.First)).ErrorCode); // no new approval either
        var status = Describe(await RegistrationAsync(w));
        Assert.Equal(("Pending", 2, 1, 2), (status.State, status.Current, status.Registered, status.Pending));

        // Recovery without a new publication: the staged version is registered; D-H3 applies now.
        var recovered = Describe(await RecoverAsync(w));
        Assert.Equal(("Registered", "Registered", 2, 2, (int?)null), (recovered.Outcome, recovered.State, recovered.Current, recovered.Registered, recovered.Pending));
        Assert.Equal(["1|Registered", "2|Registered"], Publications(w));
        Assert.Equal("Invalidated|FORM_VERSION_CHANGED|CurrentVersion", Tail(Approvals(w, w.Phone).Single()));
        AssertNotAllowed(await SearchAsync(w, w.Phone, "equals", "555-0300"));
        AssertNotAllowed((await IndexedAsync(w, clerk, w.Phone, "555-03")).Result);
        var after = await CapabilitiesAsync(w);
        Assert.True(after.GetProperty("fieldValueSearchAvailable").GetBoolean());
        Assert.DoesNotContain(after.GetProperty("fields").EnumerateArray(), f => f.GetProperty("fieldDefinitionId").GetGuid() == w.Phone);
        Assert.Contains("\"registrationSource\":\"Recovery\"", BusinessAudit(w, "FORM_VERSION_AUTHORITY_REGISTERED"), StringComparison.Ordinal);

        // Idempotent: a second recovery sends nothing.
        var resolutions = Scalar($"SELECT count(*) FROM security_commands WHERE organization_id = '{w.S.OrganizationId:D}' AND command_kind_name = 'ResolveFormVersionPublication' AND command_state_name <> 'Rejected';");
        Assert.Equal("AlreadyRegistered", Describe(await RecoverAsync(w)).Outcome);
        Assert.Equal(resolutions, Scalar($"SELECT count(*) FROM security_commands WHERE organization_id = '{w.S.OrganizationId:D}' AND command_kind_name = 'ResolveFormVersionPublication' AND command_state_name <> 'Rejected';"));
        Assert.Equal(12, records.Count);
    }

    // ---------------------------------------------------------------- a lost commit: the staged version is withdrawn

    [Fact]
    public async Task ALostCommitIsWithdrawnByRecoveryAndTheRegisteredStateAppliesAgain()
    {
        var w = await WorkspaceAsync("bf1-abandon");
        Assert.Equal(1, await PublishAsync(w, (w.First, "First name"), (w.Phone, "Phone")));
        Assert.True((await ApproveAsync(w, w.Phone)).IsSuccess);
        var r1 = await CreateAsync(w, new() { ["first_name"] = "Avery", ["phone_number"] = "555-0401" });
        Assert.Equal([r1], Matches(await SearchAsync(w, w.Phone, "equals", "555-0401")));

        // The Authority stages V2; the business commit is then lost (fault before the commit): nothing is published.
        var lost = await PublishWithFaultAsync(w, LostCommit, (w.First, "First name"), (w.Email, "Email"));
        Assert.Equal((HttpStatusCode.ServiceUnavailable, "TESTING_FAULT_INJECTED"), (lost.Status, lost.ErrorCode));
        Assert.Equal(1, await CurrentVersionAsync(w));
        Assert.Equal(["1|Registered", "2|Pending"], Publications(w));
        AssertPending(await SearchAsync(w, w.Phone, "equals", "555-0401")); // fail closed: the Authority cannot know the commit was lost
        var status = Describe(await RegistrationAsync(w));
        Assert.Equal(("Pending", 1, 1, 2), (status.State, status.Current, status.Registered, status.Pending));

        var recovered = Describe(await RecoverAsync(w));
        Assert.Equal(("Abandoned", "Registered", 1, 1), (recovered.Outcome, recovered.State, recovered.Current, recovered.Registered));
        Assert.Equal(["1|Registered", "2|Abandoned"], Publications(w));
        Assert.Equal("Active||CurrentVersion", Tail(Approvals(w, w.Phone).Single()));
        Assert.Equal([r1], Matches(await SearchAsync(w, w.Phone, "equals", "555-0401")));
        Assert.NotEmpty(BusinessAudit(w, "FORM_VERSION_AUTHORITY_PUBLICATION_ABANDONED"));

        // Publishing the removal for real registers it and invalidates the approval (D-H3).
        Assert.Equal(2, await PublishAsync(w, (w.First, "First name"), (w.Email, "Email")));
        Assert.Equal("Invalidated|FORM_VERSION_CHANGED|CurrentVersion", Tail(Approvals(w, w.Phone).Single()));
        AssertNotAllowed(await SearchAsync(w, w.Phone, "equals", "555-0401"));
    }

    // ---------------------------------------------------------------- the field-return case (C-188 retained approval)

    [Fact]
    public async Task AReturningFieldStaysClosedUnderItsRetainedApprovalUntilRecoveryConvertsIt()
    {
        var w = await WorkspaceAsync("bf1-return");
        Assert.Equal(1, await PublishAsync(w, (w.First, "First name"), (w.Phone, "Phone")));
        Assert.True((await ApproveAsync(w, w.Phone)).IsSuccess);
        var r1 = await CreateAsync(w, new() { ["first_name"] = "Blake", ["phone_number"] = "555-0501" });
        Assert.Equal(2, await PublishAsync(w, (w.First, "First name"), (w.Email, "Email")));
        Assert.True((await ApproveAsync(w, w.Phone, "RetainedField")).IsSuccess);
        Assert.Equal([r1], Matches(await SearchAsync(w, w.Phone, "equals", "555-0501")));
        var retainedId = Approvals(w, w.Phone)[^1].Split('|')[0];
        var clerk = await SessionAsync(w, w.S.Clerk);
        Assert.Equal([r1], IndexedIds((await IndexedAsync(w, clerk, w.Phone, "555-05")).Result));

        // V3 brings phone back; its registration is lost.
        var lost = await PublishWithFaultAsync(w, LostRegistration, (w.First, "First name"), (w.Phone, "Phone"), (w.Email, "Email"));
        Assert.Equal("TESTING_FAULT_INJECTED", lost.ErrorCode);
        Assert.Equal(3, await CurrentVersionAsync(w));
        Assert.Equal($"{retainedId}|Active||RetainedField", Approvals(w, w.Phone)[^1]);
        AssertPending(await SearchAsync(w, w.Phone, "equals", "555-0501"));
        AssertNotAllowed((await IndexedAsync(w, clerk, w.Phone, "555-05")).Result);
        Assert.Empty((await AuthorityCapabilitiesAsync(w, w.S.Clerk)).GetProperty("fields").EnumerateArray());

        // Recovery registers V3: the retained approval is converted in place (D-H4) and search resumes, as a current field.
        Assert.Equal("Registered", Describe(await RecoverAsync(w)).Outcome);
        Assert.Equal($"{retainedId}|Active||CurrentVersion", Approvals(w, w.Phone)[^1]);
        Assert.Equal([r1], Matches(await SearchAsync(w, w.Phone, "equals", "555-0501")));
        var phone = (await CapabilitiesAsync(w)).GetProperty("fields").EnumerateArray().Single(f => f.GetProperty("fieldDefinitionId").GetGuid() == w.Phone);
        Assert.True(phone.GetProperty("inCurrentVersion").GetBoolean());
        Assert.Equal([r1], IndexedIds((await IndexedAsync(w, clerk, w.Phone, "555-05")).Result));
    }

    // ---------------------------------------------------------------- concurrent security commands

    /// <summary>
    /// A removal publication races a stream of concurrent security commands (re-approvals, which close the organization gate). Whatever
    /// the interleaving - prepare refused and retried, registration completed, or registration refused and left pending - a field removed
    /// by the current business layout is never searchable: either the Authority registered the removal (approval invalidated) or the
    /// publication is pending (every approval of the form ineffective). Recovery completes any pending publication.
    /// </summary>
    [Fact]
    public async Task ConcurrentSecurityCommandsNeverOpenAWindowForARemovedField()
    {
        var w = await WorkspaceAsync("bf1-concurrency");
        Assert.Equal(1, await PublishAsync(w, (w.First, "First name"), (w.Phone, "Phone")));
        await CreateAsync(w, new() { ["first_name"] = "Casey", ["phone_number"] = "555-0601" });
        string[] tolerated = ["STALE_AUTHORIZATION_REVISION", "COMMAND_PENDING", "AUTHORITY_NOT_READY", "FORM_PUBLICATION_PENDING"];
        var outcomes = new List<string>();
        for (var round = 0; round < 4; round++)
        {
            Assert.True((await ApproveAsync(w, w.Phone)).IsSuccess);
            Assert.Single(Matches(await SearchAsync(w, w.Phone, "equals", "555-0601")));

            var draftRevision = await w.S.SaveDraftAsync(w.FormId, LayoutOf(w, (w.First, "First name"), (w.Email, "Email")));
            var currentVersion = await CurrentVersionAsync(w);
            var publish = PublishRacingAsync(w, draftRevision, currentVersion, $"bf1 race {round}");
            var commands = Task.WhenAll(Enumerable.Range(0, 2).Select(async _ =>
            {
                var results = new List<HttpResult>();
                for (var i = 0; i < 6 && !publish.IsCompleted; i++)
                {
                    results.Add(await fixture.Api.PostAsync($"/api/v1/form-definitions/{w.FormId:D}/fields/{w.First:D}/search-approval", w.S.Token(w.S.Administrator), new { operators = new[] { "equals" }, approvalReference = "BF-1 race", useDevelopmentStepUp = true, approvalScope = (string?)null }));
                }

                return results;
            }));
            var published = await publish;
            var concurrent = (await commands).SelectMany(r => r).ToList();
            Assert.All(concurrent.Where(r => !r.IsSuccess), r => Assert.Contains(r.ErrorCode, tolerated));
            await w.S.WaitGateReadyAsync(TimeSpan.FromSeconds(40));

            var registered = published.GetProperty("authorityRegistered").GetBoolean();
            var pending = published.GetProperty("authorityRegistrationPending").GetBoolean();
            var current = await CurrentVersionAsync(w);
            Assert.Equal(published.GetProperty("versionNumber").GetInt32(), current);
            var phoneSearch = await SearchAsync(w, w.Phone, "equals", "555-0601");
            Assert.False(phoneSearch.IsSuccess, "a removed field was searchable: " + phoneSearch.Body);
            if (LatestRegistered(w) == current)
            {
                Assert.DoesNotContain(Publications(w), p => p.EndsWith("|Pending", StringComparison.Ordinal));
                Assert.Equal("Invalidated|FORM_VERSION_CHANGED|CurrentVersion", Tail(Approvals(w, w.Phone)[^1]));
                AssertNotAllowed(phoneSearch);
            }
            else
            {
                Assert.True(pending, published.ToString());
                Assert.Equal($"{current}|Pending", Publications(w)[^1]);
                AssertPending(phoneSearch);
                Assert.Empty((await AuthorityCapabilitiesAsync(w, w.S.Clerk)).GetProperty("fields").EnumerateArray());
                Assert.Equal("Registered", Describe(await RecoverAsync(w)).Outcome);
                Assert.Equal("Invalidated|FORM_VERSION_CHANGED|CurrentVersion", Tail(Approvals(w, w.Phone)[^1]));
            }

            outcomes.Add($"round={round} registered={registered} pending={pending} concurrent={concurrent.Count} refused={concurrent.Count(r => !r.IsSuccess)}");
            TestContext.Current.TestOutputHelper?.WriteLine("BF1-RACE " + outcomes[^1]);

            // Phone back for the next round (registered).
            await PublishAsync(w, (w.First, "First name"), (w.Phone, "Phone"));
        }
    }

    // ---------------------------------------------------------------- database guards on the real Authority database

    [Fact]
    public async Task TheDatabaseNeverRevivesRewritesDuplicatesOrLetsTheRuntimeDeleteAPendingPublication()
    {
        var w = await WorkspaceAsync("bf1-schema");
        Assert.Equal(1, await PublishAsync(w, (w.First, "First name"), (w.Phone, "Phone")));
        Assert.Equal("TESTING_FAULT_INJECTED", (await PublishWithFaultAsync(w, LostRegistration, (w.First, "First name"))).ErrorCode);
        Assert.Equal(["1|Registered", "2|Pending"], Publications(w));
        var where = $"organization_id = '{w.S.OrganizationId:D}' AND form_definition_id = '{w.FormId:D}'";
        string Owner(string sql) => fixture.Stack.AuthorityAdministratorSql($"BEGIN; {sql} ROLLBACK;", throwOnError: false);
        string AsRuntime(string sql) => fixture.Stack.AuthorityAdministratorSql($"BEGIN; SET LOCAL ROLE authority_runtime; {sql} ROLLBACK;", throwOnError: false);

        Assert.Contains("is final and cannot change", Owner($"UPDATE form_version_publications SET publication_state_name = 'Pending', resolved_by_security_command_id = NULL, resolved_on = NULL WHERE {where} AND form_version_number = 1;"), StringComparison.Ordinal);
        Assert.Contains("the staged publication is immutable", Owner($"UPDATE form_version_publications SET publication_state_name = 'Abandoned', resolved_by_security_command_id = prepared_by_security_command_id, resolved_on = now(), field_manifest_json = '[]'::jsonb WHERE {where} AND form_version_number = 2;"), StringComparison.Ordinal);
        Assert.Contains("changes only by being resolved", Owner($"UPDATE form_version_publications SET form_title_snapshot = 'x' WHERE {where} AND form_version_number = 2;"), StringComparison.Ordinal);
        Assert.Contains("at most one pending publication", fixture.Stack.AuthorityAdministratorSql(
            $"BEGIN; INSERT INTO form_version_publications (form_version_publication_id, organization_id, form_definition_id, form_version_number, business_form_version_id, layout_digest, field_manifest_json, publication_state_name, prepared_by_security_command_id, prepared_on) SELECT gen_random_uuid(), organization_id, form_definition_id, 3, gen_random_uuid(), layout_digest, field_manifest_json, 'Pending', prepared_by_security_command_id, now() FROM form_version_publications WHERE {where} AND form_version_number = 2; COMMIT;", throwOnError: false), StringComparison.Ordinal);
        Assert.Contains("permission denied", AsRuntime($"DELETE FROM form_version_publications WHERE {where};"), StringComparison.Ordinal);
        Assert.Equal(["1|Registered", "2|Pending"], Publications(w)); // nothing changed

        Assert.Equal("Registered", Describe(await RecoverAsync(w)).Outcome);
        Assert.Equal(["1|Registered", "2|Registered"], Publications(w));
    }
}
