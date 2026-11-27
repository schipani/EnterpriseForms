using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using EnterpriseForms.SecurityTests.Harness;
using EnterpriseForms.StageBTests.Harness;
using Xunit;

namespace EnterpriseForms.StageBTests.Tests;

/// <summary>
/// C-188 (Option B) on the real stack (business API, Authority, SpiceDB, Vault Transit, both PostgreSQL servers, the search
/// index builder). CLAUDE.md §11 #2: a field removed from the current version stays unsearchable until a security administrator
/// explicitly approves searching its latest retained values; then both search engines find the original values on the original
/// records — only records the caller can discover and read, never earlier revisions — and the approval returns to normal when the
/// field returns unchanged. Withdrawal ends searches and open pages; an invalidated approval is never revived (a database trigger
/// forbids it); Authority binaries built before C-188 cannot see retained approvals (row-level security fence); the migration's
/// backfill stops on a conflicting field binding; disabling the feature (rollback rehearsal) revokes capabilities and open pages
/// while withdrawal still works. Synthetic data only; the stack runs with FieldSearch:RetainedFieldApprovals=Enabled
/// (security-test overlay only).
/// </summary>
[Collection(StageBStackCollectionDefinition.Name)]
public sealed class RetainedFieldSearchTests(StageBFixture fixture)
{
    private sealed record Ws(StageBScenario S, Guid FormId, Guid First, Guid Phone, Guid Email);

    private sealed record Removed(Ws W, Guid R1, Guid R2, List<Guid> Seeded);

    private sealed record Session(string Digest, TestUser User);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ---------------------------------------------------------------- helpers

    private string Scalar(string sql) => fixture.Stack.AuthorityObserverSql(sql).Trim();

    /// <summary>The phone field's approval rows, oldest first: id|state|reason|scope|source version (read as the database owner: the fence does not apply).</summary>
    private List<string> Approvals(Ws w) =>
        Scalar($"SELECT field_search_approval_id || '|' || approval_state_name || '|' || coalesce(invalidation_reason_code, '') || '|' || approval_scope_name || '|' || coalesce(source_form_version_number::text, '') FROM field_search_approvals WHERE organization_id = '{w.S.OrganizationId:D}' AND field_definition_id = '{w.Phone:D}' ORDER BY approved_on;")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    private static string Tail(string row) => string.Join('|', row.Split('|').Skip(1));

    private async Task<Ws> WorkspaceAsync(string name)
    {
        var s = new StageBScenario(fixture, name);
        await s.BootstrapAsync();
        var current = await fixture.Api.GetAsync("/api/v1/organization/security-policy", s.Token(s.Designer));
        var policy = await fixture.Api.SendAsync(HttpMethod.Put, "/api/v1/organization/security-policy", s.Token(s.Designer), new { minimumFieldClassification = "Internal", plaintextInternalFieldsPermitted = true, plaintextDowngradePermitted = false, plaintextSearchPermitted = true, policyStatementText = "Selected plaintext Internal fields may be searched in this synthetic organization.", expectedPolicyRevision = current.Root.GetProperty("securityPolicyRevision").GetInt64() }, Guid.NewGuid().ToString("N"));
        Assert.True(policy.IsSuccess, policy.Body);
        var formId = await s.CreateFormAsync("Retained " + Guid.NewGuid().ToString("N")[..6], s.Clerk, s.Designer);
        async Task<Guid> FieldAsync(string key, string label, string? classification, string? protection)
        {
            var added = await fixture.Api.PostAsync($"/api/v1/form-definitions/{formId:D}/fields", s.Token(s.Designer), new { fieldKey = key, fieldDisplayName = label, fieldDescription = string.Empty, fieldDataType = "String", options = (object?)null, fieldClassification = classification, fieldProtectionMode = protection });
            Assert.True(added.Status == HttpStatusCode.Created, $"add {key}: " + added.Body);
            return added.Root.GetProperty("fieldDefinitionId").GetGuid();
        }

        var w = new Ws(s, formId, await FieldAsync("first_name", "First name", "Internal", "Standard"), await FieldAsync("phone_number", "Phone", "Internal", "Standard"), await FieldAsync("email_address", "Email", null, null));
        Assert.Equal(HttpStatusCode.NoContent, (await fixture.Api.SendAsync(HttpMethod.Put, $"/api/v1/form-definitions/{formId:D}/field-searchers", s.Token(s.Administrator), new { grantUserIds = new[] { s.Clerk.AuthorityUserId, s.Reader.AuthorityUserId }, grantTeamIds = Array.Empty<Guid>(), revokeUserIds = Array.Empty<Guid>(), revokeTeamIds = Array.Empty<Guid>() }, Guid.NewGuid().ToString("N"))).Status);
        await s.WaitGateReadyAsync(TimeSpan.FromSeconds(40));
        return w;
    }

    private static async Task<int> PublishAsync(Ws w, params (Guid Field, string Label)[] elements)
    {
        await w.S.SaveDraftAsync(w.FormId, StageBScenario.Layout("Retained", StageBScenario.Section("main", "Main", elements.Select(e => StageBScenario.Field(e.Field, "text", e.Label, 4)).ToArray())));
        var published = await w.S.PublishAsync(w.FormId, "c188 " + string.Join(", ", elements.Select(e => e.Label)));
        Assert.True(published.GetProperty("authorityRegistered").GetBoolean());
        await w.S.WaitGateReadyAsync(TimeSpan.FromSeconds(40));
        return published.GetProperty("versionNumber").GetInt32();
    }

    private async Task<HttpResult> ApproveAsync(Ws w, Guid field, string? scope, TestUser? actor = null)
    {
        var result = await fixture.Api.PostAsync($"/api/v1/form-definitions/{w.FormId:D}/fields/{field:D}/search-approval", w.S.Token(actor ?? w.S.Administrator), new { operators = new[] { "equals", "startsWith" }, approvalReference = "C-188", useDevelopmentStepUp = true, approvalScope = scope });
        if (result.IsSuccess)
        {
            await w.S.WaitGateReadyAsync(TimeSpan.FromSeconds(40));
        }

        return result;
    }

    /// <summary>
    /// The publish request alone, for a race: expectations are read before the race, and only the Authority's transient refusals (a gate
    /// closed by the concurrent command) are retried, as the SPA does. A refused attempt writes nothing; a conflict fails the test.
    /// </summary>
    private async Task<JsonElement> PublishRacingAsync(Ws w, long expectedDraftRevision, int expectedCurrentVersionNumber, string changeReason)
    {
        for (var attempt = 0; ; attempt++)
        {
            var published = await fixture.Api.PostAsync($"/api/v1/form-definitions/{w.FormId:D}/publish", w.S.Token(w.S.Designer), new { expectedDraftRevision, expectedCurrentVersionNumber, changeReason });
            if (published.Status == HttpStatusCode.Created)
            {
                return published.Root.Clone();
            }

            // C-188 BF-1: AUTHORITY_UNAVAILABLE - the Authority stayed busy before it staged the version, so nothing was published.
            Assert.True(attempt < 80 && published.ErrorCode is "AUTHORITY_NOT_READY" or "STALE_AUTHORIZATION_REVISION" or "COMMAND_PENDING" or "AUTHORITY_UNAVAILABLE", "publish failed: " + published.Body);
            await Task.Delay(250, Ct);
        }
    }

    private async Task<HttpResult> WithdrawAsync(Ws w)
    {
        var result = await fixture.Api.PostAsync($"/api/v1/form-definitions/{w.FormId:D}/fields/{w.Phone:D}/search-approval/withdraw", w.S.Token(w.S.Administrator), new { reason = "rollback rehearsal" });
        if (result.IsSuccess)
        {
            await w.S.WaitGateReadyAsync(TimeSpan.FromSeconds(40));
        }

        return result;
    }

    private async Task<Guid> CreateAsync(Ws w, Dictionary<string, string> values, TestUser? owner = null)
    {
        var key = "c188-" + Guid.CreateVersion7().ToString("N");
        for (var attempt = 0; ; attempt++)
        {
            var created = await fixture.Api.PostAsync($"/api/v1/form-definitions/{w.FormId:D}/records", w.S.Token(owner ?? w.S.Clerk), new { values, ownerTeamId = (Guid?)null }, key);
            if (created.Status is HttpStatusCode.Created or HttpStatusCode.OK)
            {
                var id = created.Root.GetProperty("formRecordId").GetGuid();
                await w.S.WaitProjectionAsync(owner ?? w.S.Clerk, id, TimeSpan.FromSeconds(40));
                return id;
            }

            Assert.True(attempt < 60 && created.ErrorCode is "AUTHORITY_NOT_READY" or "STALE_AUTHORIZATION_REVISION" or "COMMAND_PENDING", "create failed: " + created.Body);
            await Task.Delay(500, Ct);
        }
    }

    private async Task<HttpResult> SearchAsync(Ws w, TestUser user, Guid field, string op, string value, int pageSize = 25)
    {
        for (var attempt = 0; ; attempt++)
        {
            var result = await fixture.Api.PostAsync("/api/v1/form-records/field-search", w.S.Token(user), new
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

    private static void AssertRefused(HttpResult result)
    {
        Assert.False(result.IsSuccess, result.Body);
        Assert.Equal("SEARCH_FIELD_NOT_ALLOWED", result.ErrorCode);
        Assert.False(result.Root.TryGetProperty("items", out _));
        Assert.False(result.Root.TryGetProperty("totalCount", out _));
    }

    private async Task<JsonElement> CapabilitiesAsync(Ws w, TestUser user)
    {
        var capabilities = await fixture.Api.GetAsync($"/api/v1/form-records/search-capabilities?formDefinitionId={w.FormId:D}", w.S.Token(user));
        Assert.True(capabilities.IsSuccess, capabilities.Body);
        return capabilities.Root.Clone();
    }

    private static JsonElement? Capability(JsonElement capabilities, Guid field) =>
        capabilities.GetProperty("fields").EnumerateArray().Where(f => f.GetProperty("fieldDefinitionId").GetGuid() == field).Select(f => (JsonElement?)f.Clone()).FirstOrDefault();

    private async Task<Session> SessionAsync(Ws w, TestUser user)
    {
        var digest = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var created = await fixture.Authority.SendAsync(HttpMethod.Post, "/internal/v1/sessions", TestIdentity.WorkloadToken(), new { organization_id = w.S.OrganizationId, session_digest_base64 = digest, absolute_expires_on = DateTimeOffset.UtcNow.AddHours(1) }, w.S.Token(user));
        Assert.True(created.IsSuccess, created.Body);
        return new Session(digest, user);
    }

    /// <summary>The indexed engine (Authority internal query); retried only on its typed transient answers, including a coverage still being built.</summary>
    private async Task<HttpResult> IndexedAsync(Ws w, Session session, string prefix)
    {
        var query = new { form_definition_id = w.FormId, conditions = new[] { new { field_definition_id = w.Phone, @operator = "startsWith", value_type = "String", value = prefix, lower_bound = (string?)null, upper_bound = (string?)null } }, record_status = (string?)null, include_children = false, include_archived = false, sort_by = (string?)null, sort_direction = (string?)null, page_size = 25 };
        for (var attempt = 0; ; attempt++)
        {
            var result = await fixture.Authority.SendAsync(HttpMethod.Post, "/internal/v1/search/indexed-query", TestIdentity.WorkloadToken(),
                new { organization_id = w.S.OrganizationId, session_digest_base64 = session.Digest, query_nonce_base64 = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)), query }, w.S.Token(session.User));
            if (result.IsSuccess || attempt >= 120 || result.ErrorCode is not ("AUTHORITY_NOT_READY" or "SEARCH_SNAPSHOT_CHANGED" or "SEARCH_INDEX_NOT_READY"))
            {
                return result;
            }

            await Task.Delay(1000, Ct);
        }
    }

    private static HashSet<Guid> IndexedIds(HttpResult result)
    {
        Assert.True(result.IsSuccess, result.Body);
        return result.Root.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("form_record_id").GetGuid()).ToHashSet();
    }

    private async Task SetBehaviorAsync(Ws w, string behavior)
    {
        var revision = (await w.S.GetFormAsync(w.FormId)).GetProperty("summary").GetProperty("formDefinitionRevision").GetInt64();
        var changed = await fixture.Api.SendAsync(HttpMethod.Put, $"/api/v1/form-definitions/{w.FormId:D}/version-behavior", w.S.Token(w.S.Designer), new { versionBehavior = behavior, expectedFormDefinitionRevision = revision, changeReason = "c188 " + behavior }, null);
        Assert.True(changed.IsSuccess, changed.Body);
    }

    private async Task<JsonElement> EditProjectionAsync(Ws w, Guid record)
    {
        for (var attempt = 0; ; attempt++)
        {
            var result = await fixture.Api.GetAsync($"/api/v1/form-records/{record:D}/projection?purpose=edit", w.S.Token(w.S.Clerk));
            if (result.Status != HttpStatusCode.ServiceUnavailable || attempt >= 80)
            {
                Assert.True(result.IsSuccess, result.Body);
                return result.Root.Clone();
            }

            await Task.Delay(250, Ct);
        }
    }

    private void RecreateAuthority(string? retainedSetting)
    {
        Environment.SetEnvironmentVariable("SECTEST_RETAINED_FIELD_APPROVALS", retainedSetting);
        fixture.Stack.Compose("up -d --no-deps --force-recreate --wait authority", 300);
    }

    /// <summary>
    /// V1 (first, phone) with phone approved and searched; r1, r2 and <paramref name="extra"/> more clerk records hold phone values,
    /// one designer record too (not discoverable to the clerk); then V2 (first, email) invalidates the phone approval.
    /// </summary>
    private async Task<Removed> RemovedPhoneAsync(string name, int extra = 0)
    {
        var w = await WorkspaceAsync(name);
        Assert.Equal(1, await PublishAsync(w, (w.First, "First name"), (w.Phone, "Phone")));
        Assert.True((await ApproveAsync(w, w.Phone, null)).IsSuccess);
        var r1 = await CreateAsync(w, new() { ["first_name"] = "Avery", ["phone_number"] = "555-0101" });
        var r2 = await CreateAsync(w, new() { ["first_name"] = "Blake", ["phone_number"] = "555-0102" });
        var seeded = new List<Guid>();
        for (var i = 0; i < extra; i++)
        {
            seeded.Add(await CreateAsync(w, new() { ["first_name"] = $"Seed {i}", ["phone_number"] = $"555-02{i:00}" }));
        }

        await CreateAsync(w, new() { ["first_name"] = "Drew", ["phone_number"] = "555-0199" }, w.S.Designer);
        Assert.Equal([r1], Matches(await SearchAsync(w, w.S.Clerk, w.Phone, "equals", "555-0101")));
        Assert.Equal(2, await PublishAsync(w, (w.First, "First name"), (w.Email, "Email")));
        Assert.Equal("Invalidated|FORM_VERSION_CHANGED|CurrentVersion|", Tail(Approvals(w).Single())); // D-H3 unchanged
        return new Removed(w, r1, r2, seeded);
    }

    // ---------------------------------------------------------------- T1–T15: lifecycle on both engines

    [Fact]
    public async Task ARemovedFieldIsSearchableOnlyUnderAnExplicitRetainedApprovalOnBothEnginesAndReturnsToNormal()
    {
        var (w, r1, r2, _) = await RemovedPhoneAsync("c188-lifecycle");

        // T3: refused before a retained approval; not offered to searchers; listed as no longer in the form only for administrators/designers (D-H9).
        AssertRefused(await SearchAsync(w, w.S.Clerk, w.Phone, "equals", "555-0101"));
        Assert.Null(Capability(await CapabilitiesAsync(w, w.S.Clerk), w.Phone));
        var configuration = await fixture.Api.GetAsync($"/api/v1/form-definitions/{w.FormId:D}/field-search", w.S.Token(w.S.Administrator));
        Assert.True(configuration.IsSuccess, configuration.Body);
        Assert.True(configuration.Root.GetProperty("retainedFieldApprovalsEnabled").GetBoolean());
        var removed = configuration.Root.GetProperty("fields").EnumerateArray().Single(f => f.GetProperty("fieldDefinitionId").GetGuid() == w.Phone);
        Assert.Equal((false, 1, "Invalidated"), (removed.GetProperty("inCurrentVersion").GetBoolean(), removed.GetProperty("lastPlacedVersionNumber").GetInt32(), removed.GetProperty("approvalState").GetString()));
        Assert.Equal(HttpStatusCode.NotFound, (await fixture.Api.GetAsync($"/api/v1/form-definitions/{w.FormId:D}/field-search", w.S.Token(w.S.Reader))).Status);

        // T4: only a security administrator approves; a current-version approval of a removed field is refused.
        Assert.False((await ApproveAsync(w, w.Phone, "RetainedField", w.S.Designer)).IsSuccess);
        Assert.Equal("FORM_VERSION_UNTRUSTED", (await ApproveAsync(w, w.Phone, null)).ErrorCode);
        Assert.Equal("FORM_VERSION_UNTRUSTED", (await ApproveAsync(w, w.First, "RetainedField")).ErrorCode); // still in the form
        var retained = await ApproveAsync(w, w.Phone, "RetainedField");
        Assert.True(retained.IsSuccess, retained.Body);
        var rows = Approvals(w);
        Assert.Equal(2, rows.Count);
        Assert.Equal("Invalidated|FORM_VERSION_CHANGED|CurrentVersion|", Tail(rows[0])); // the old approval is never revived
        Assert.Equal("Active||RetainedField|1", Tail(rows[1]));
        Assert.Equal(w.FormId.ToString("D"), Scalar($"SELECT form_definition_id FROM field_form_bindings WHERE organization_id = '{w.S.OrganizationId:D}' AND field_definition_id = '{w.Phone:D}';"));
        var audit = fixture.Stack.AttackerSql($"SELECT event_metadata_json::text FROM audit_events WHERE tenant_id = '{w.S.OrganizationId:D}' AND event_action = 'FIELD_SEARCH_APPROVED' ORDER BY audit_sequence DESC LIMIT 1;").Replace(" ", string.Empty, StringComparison.Ordinal);
        Assert.Contains("\"approvalScope\":\"RetainedField\"", audit, StringComparison.Ordinal);

        // T5 (V1 engine): the latest retained values of the clerk's discoverable records; never the designer's record.
        var phone = Capability(await CapabilitiesAsync(w, w.S.Clerk), w.Phone);
        Assert.NotNull(phone);
        Assert.False(phone!.Value.GetProperty("inCurrentVersion").GetBoolean());
        Assert.Equal([r1], Matches(await SearchAsync(w, w.S.Clerk, w.Phone, "equals", "555-0101")));
        Assert.Equal(new HashSet<Guid> { r1, r2 }, Matches(await SearchAsync(w, w.S.Clerk, w.Phone, "startsWith", "555-01")));

        // T5/T6 (indexed engine): the same answer once the new coverage is Complete (until then it refuses as not ready, never partial).
        var clerk = await SessionAsync(w, w.S.Clerk);
        Assert.Equal(new HashSet<Guid> { r1, r2 }, IndexedIds(await IndexedAsync(w, clerk, "555-01")));

        // Latest retained value only: r1's phone is changed (Historical keeps V1, which places phone, as r1's edit layout). The old value
        // no longer matches on either engine; earlier revisions are never searched.
        await SetBehaviorAsync(w, "Historical");
        var edit = await EditProjectionAsync(w, r1);
        var saved = await fixture.Api.PatchAsync($"/api/v1/form-records/{r1:D}", w.S.Token(w.S.Clerk), new { expectedContentRevision = edit.GetProperty("record").GetProperty("contentRevision").GetInt64(), values = new Dictionary<string, string> { ["phone_number"] = "555-0111" }, expectedLayoutVersionId = edit.GetProperty("layoutVersion").GetProperty("formVersionId").GetGuid() });
        Assert.True(saved.IsSuccess, saved.Body);
        Assert.Empty(Matches(await SearchAsync(w, w.S.Clerk, w.Phone, "equals", "555-0101")));
        Assert.Equal([r1], Matches(await SearchAsync(w, w.S.Clerk, w.Phone, "equals", "555-0111")));
        Assert.Equal([r2], IndexedIds(await IndexedAsync(w, clerk, "555-010")));

        // T9: the version behaviour never changes what search may see.
        await SetBehaviorAsync(w, "Hybrid");
        Assert.Equal(new HashSet<Guid> { r1, r2 }, Matches(await SearchAsync(w, w.S.Clerk, w.Phone, "startsWith", "555-01")));
        await SetBehaviorAsync(w, "Latest");

        // T7/T12: query permission without record access finds nothing; a partial share without phone excludes the record.
        Assert.Empty(Matches(await SearchAsync(w, w.S.Reader, w.Phone, "startsWith", "555-01")));
        var share = await fixture.Api.PostAsync($"/api/v1/form-records/{r2:D}/shares", w.S.Token(w.S.Clerk), new { useDevelopmentStepUp = true, recipientUserId = w.S.Reader.AuthorityUserId, recipientTeamId = (Guid?)null, recipientRole = "Reader", fieldKeys = new[] { "first_name" } });
        Assert.True(share.IsSuccess, share.Body);
        await w.S.WaitGateReadyAsync(TimeSpan.FromSeconds(40));
        Assert.Empty(Matches(await SearchAsync(w, w.S.Reader, w.Phone, "startsWith", "555-01")));

        // T10: another organization is refused, indistinguishably.
        var other = new StageBScenario(fixture, "c188-other");
        await other.BootstrapAsync();
        AssertRefused(await SearchAsync(new Ws(other, w.FormId, w.First, w.Phone, w.Email), other.Clerk, w.Phone, "equals", "555-0102"));

        // T15: the no-revival trigger refuses even the database owner.
        var revive = fixture.Stack.AuthorityAdministratorSql($"UPDATE field_search_approvals SET approval_state_name = 'Active', invalidation_reason_code = NULL WHERE field_search_approval_id = '{rows[0].Split('|')[0]}';", throwOnError: false);
        Assert.Contains("is final and cannot change", revive, StringComparison.Ordinal);
        Assert.Equal("Invalidated|FORM_VERSION_CHANGED|CurrentVersion|", Tail(Approvals(w)[0]));

        // T13: phone returns unchanged in V3 — the retained approval becomes the current-version approval in place (same id).
        var retainedId = rows[1].Split('|')[0];
        Assert.Equal(3, await PublishAsync(w, (w.First, "First name"), (w.Phone, "Phone"), (w.Email, "Email")));
        Assert.Equal($"{retainedId}|Active||CurrentVersion|", Approvals(w)[1]);
        Assert.True(Capability(await CapabilitiesAsync(w, w.S.Clerk), w.Phone)!.Value.GetProperty("inCurrentVersion").GetBoolean());
        Assert.Equal(new HashSet<Guid> { r1, r2 }, Matches(await SearchAsync(w, w.S.Clerk, w.Phone, "startsWith", "555-01")));
        Assert.Equal(new HashSet<Guid> { r1, r2 }, IndexedIds(await IndexedAsync(w, clerk, "555-01")));

        // D-H3 unchanged: removing it again invalidates it; no silent retention.
        Assert.Equal(4, await PublishAsync(w, (w.First, "First name"), (w.Email, "Email")));
        Assert.Equal($"{retainedId}|Invalidated|FORM_VERSION_CHANGED|CurrentVersion|", Approvals(w)[1]);
        AssertRefused(await SearchAsync(w, w.S.Clerk, w.Phone, "equals", "555-0111"));
    }

    // ---------------------------------------------------------------- T12, T14: withdrawal with an open page; changed return

    [Fact]
    public async Task WithdrawalEndsSearchesAndOpenPagesAndAChangedReturnInvalidates()
    {
        var (w, _, _, seeded) = await RemovedPhoneAsync("c188-withdraw", extra: 10);
        Assert.True((await ApproveAsync(w, w.Phone, "RetainedField")).IsSuccess);

        // 12 matches: an open first page of 10, then a withdrawal ends the page and every further search.
        var first = await SearchAsync(w, w.S.Clerk, w.Phone, "startsWith", "555-0", 10);
        Assert.Equal(12, first.Root.GetProperty("totalCount").GetInt32());
        var next = first.Root.GetProperty("continuationToken").GetString();
        Assert.False(string.IsNullOrEmpty(next));
        Assert.True((await WithdrawAsync(w)).IsSuccess);
        Assert.Equal("SEARCH_SNAPSHOT_CHANGED", (await fixture.Api.PostAsync("/api/v1/form-records/field-search/page", w.S.Token(w.S.Clerk), new { cursor = next })).ErrorCode);
        AssertRefused(await SearchAsync(w, w.S.Clerk, w.Phone, "startsWith", "555-0"));
        Assert.Equal("Withdrawn||RetainedField|1", Tail(Approvals(w)[^1]));
        Assert.Equal(10, seeded.Count);

        // A new retained approval, then phone returns relabelled: invalidated, never converted.
        Assert.True((await ApproveAsync(w, w.Phone, "RetainedField")).IsSuccess);
        Assert.Equal(3, await PublishAsync(w, (w.First, "First name"), (w.Phone, "Telephone"), (w.Email, "Email")));
        Assert.Equal("Invalidated|FORM_VERSION_CHANGED|RetainedField|1", Tail(Approvals(w)[^1]));
        AssertRefused(await SearchAsync(w, w.S.Clerk, w.Phone, "equals", "555-0101"));
        Assert.DoesNotContain(Approvals(w), row => row.Contains("|Active|", StringComparison.Ordinal));
    }

    // ---------------------------------------------------------------- T16: concurrency on the real Authority

    /// <summary>
    /// T16: the Authority serializes commands per organization (row lock and revision CAS). Concurrent retained approvals of one field
    /// leave exactly one Active approval (each later one supersedes the earlier); a retained approval racing a publication that places the
    /// field again either lands first and is converted by the registration, or is refused because the field is in the latest version —
    /// never two Active approvals and never an Active retained approval of a field that the latest version places.
    /// </summary>
    [Fact]
    public async Task ConcurrentApprovalsAndAReturningPublicationLeaveAtMostOneConsistentActiveApproval()
    {
        var w = (await RemovedPhoneAsync("c188-concurrency")).W;
        // C-188 BF-1: an approval that meets the publication staged before the business commit is refused (FORM_PUBLICATION_PENDING).
        string[] transient = ["STALE_AUTHORIZATION_REVISION", "COMMAND_PENDING", "AUTHORITY_NOT_READY", "FORM_PUBLICATION_PENDING"];
        List<string> Active() => Approvals(w).Where(r => r.Split('|')[1] == "Active").ToList();

        // Four concurrent retained approvals of the same removed field.
        var approvals = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => ApproveAsync(w, w.Phone, "RetainedField")));
        Assert.Contains(approvals, a => a.IsSuccess);
        Assert.All(approvals.Where(a => !a.IsSuccess), a => Assert.Contains(a.ErrorCode, transient));
        Assert.Equal("RetainedField", Assert.Single(Active()).Split('|')[3]);
        Assert.Equal(approvals.Count(a => a.IsSuccess) - 1, Approvals(w).Count(r => r.Split('|')[1] == "Withdrawn"));
        Assert.True((await WithdrawAsync(w)).IsSuccess);
        Assert.Empty(Active());

        // Three rounds: a retained approval races the publication that brings phone back unchanged.
        for (var round = 0; round < 3; round++)
        {
            var draftRevision = await w.S.SaveDraftAsync(w.FormId, StageBScenario.Layout("Retained", StageBScenario.Section("main", "Main", StageBScenario.Field(w.First, "text", "First name", 4), StageBScenario.Field(w.Phone, "text", "Phone", 4))));
            var currentVersion = (await w.S.GetFormAsync(w.FormId)).GetProperty("summary").GetProperty("currentVersionNumber").GetInt32();
            var approve = ApproveAsync(w, w.Phone, "RetainedField");
            var publish = PublishRacingAsync(w, draftRevision, currentVersion, $"c188 race {round}");
            await Task.WhenAll(approve, publish);
            var registered = (await publish).GetProperty("authorityRegistered").GetBoolean(); // a registration refused while the gate was closed leaves V published and pending (C-188 BF-1: field search on the form fails closed until resolved)
            await w.S.WaitGateReadyAsync(TimeSpan.FromSeconds(40));
            var approved = await approve;
            var active = Active();
            Assert.True(active.Count <= 1, string.Join("; ", active));

            // The Authority's latest registered version decides what an approval may be.
            var phoneInLatest = Scalar($"SELECT count(*) FROM published_form_versions v, jsonb_array_elements(v.field_manifest_json::jsonb) e WHERE v.organization_id = '{w.S.OrganizationId:D}' AND v.form_definition_id = '{w.FormId:D}' AND v.form_version_number = (SELECT max(form_version_number) FROM published_form_versions WHERE organization_id = '{w.S.OrganizationId:D}' AND form_definition_id = '{w.FormId:D}') AND (e->>'field_definition_id')::uuid = '{w.Phone:D}';") == "1";
            Assert.Equal(registered, phoneInLatest);
            if (approved.IsSuccess)
            {
                // Landed first and converted by the registration (D-H4), or the registration was refused and phone is still absent.
                Assert.Equal(phoneInLatest ? "CurrentVersion" : "RetainedField", Assert.Single(active).Split('|')[3]);
            }
            else
            {
                Assert.True((approved.ErrorCode == "FORM_VERSION_UNTRUSTED" && phoneInLatest) || transient.Contains(approved.ErrorCode), approved.Body);

                // A definite refusal commits nothing. A transient answer can come after the approval committed (the business re-reads the
                // configuration and the publication staged next moves the revision during that read - observed in the C-188 BF-1 run): the
                // journal, not the response, decides; the consistency check below then applies to that approval like any other.
                if (!transient.Contains(approved.ErrorCode))
                {
                    Assert.Empty(active);
                }
            }

            Assert.DoesNotContain(active, r => r.Split('|')[3] == (phoneInLatest ? "RetainedField" : "CurrentVersion"));
            TestContext.Current.TestOutputHelper?.WriteLine($"C188-RACE round={round} registered={registered} approval={(approved.IsSuccess ? "Success" : approved.ErrorCode)} active={(active.Count == 0 ? "none" : active[0].Split('|')[3])}");

            // C-191: a publication left pending is never superseded by the next publish (it would be refused FORM_PUBLICATION_PENDING); the
            // designer resolves it first, as the product requires. Registering it applies D-H4 to the approval like any registration.
            if ((await publish).GetProperty("authorityRegistrationPending").GetBoolean())
            {
                var recovered = await fixture.Api.PostAsync($"/api/v1/form-definitions/{w.FormId:D}/authority-registration/recover", w.S.Token(w.S.Designer), new { });
                Assert.True(recovered.IsSuccess && recovered.Root.GetProperty("outcome").GetString() == "Registered", recovered.Body);
                await w.S.WaitGateReadyAsync(TimeSpan.FromSeconds(40));
                Assert.DoesNotContain(Active(), r => r.Split('|')[3] == "RetainedField"); // phone is in the registered version now
                TestContext.Current.TestOutputHelper?.WriteLine($"C188-RACE round={round} pending publication recovered");
            }

            // Phone removed again (registered): a current-version approval is invalidated (D-H3); a retained one stays while phone is absent.
            await PublishAsync(w, (w.First, "First name"), (w.Email, "Email"));
            Assert.DoesNotContain(Active(), r => r.Split('|')[3] == "CurrentVersion");
            if (Active().Count > 0)
            {
                Assert.True((await WithdrawAsync(w)).IsSuccess);
            }

            Assert.Empty(Active());
        }
    }

    // ---------------------------------------------------------------- T21: migration, fence, grants and backfill guard

    [Fact]
    public async Task TheMigrationFenceGrantsAndBackfillGuardHoldOnTheRealAuthorityDatabase()
    {
        var (w, _, _, _) = await RemovedPhoneAsync("c188-schema");
        Assert.True((await ApproveAsync(w, w.Phone, "RetainedField")).IsSuccess);
        var retainedId = Approvals(w)[^1].Split('|')[0];

        // Fence: under the runtime role a retained approval is invisible (to select or update) until the session declares scope awareness,
        // which only C-188 binaries do (per connection). The table forces row-level security.
        string AsRuntime(string declare, string statement) =>
            fixture.Stack.AuthorityAdministratorSql($"BEGIN; SET LOCAL ROLE authority_runtime; {declare} {statement} ROLLBACK;", throwOnError: false).Trim();
        var select = $"SELECT 'seen:' || count(*) FROM field_search_approvals WHERE field_search_approval_id = '{retainedId}';";
        var update = $"UPDATE field_search_approvals SET invalidation_reason_code = invalidation_reason_code WHERE field_search_approval_id = '{retainedId}' RETURNING 'updated';";
        Assert.Contains("seen:0", AsRuntime(string.Empty, select), StringComparison.Ordinal);
        Assert.DoesNotContain("updated", AsRuntime(string.Empty, update), StringComparison.Ordinal);
        Assert.Contains("seen:1", AsRuntime("SET LOCAL authority.field_search_scope_aware = 'on';", select), StringComparison.Ordinal);
        Assert.Contains("updated", AsRuntime("SET LOCAL authority.field_search_scope_aware = 'on';", update), StringComparison.Ordinal);
        Assert.Contains("seen:1", AsRuntime(string.Empty, $"SELECT 'seen:' || count(*) FROM field_search_approvals WHERE organization_id = '{w.S.OrganizationId:D}' AND approval_scope_name = 'CurrentVersion';"), StringComparison.Ordinal); // current-version rows stay visible to every binary
        Assert.Equal("true|true", Scalar("SELECT relrowsecurity || '|' || relforcerowsecurity FROM pg_class WHERE oid = 'authority.field_search_approvals'::regclass;"));

        // Grants: bindings are insert-only for the runtime; constraints refuse an unknown scope.
        Assert.Contains("permission denied", AsRuntime(string.Empty, $"UPDATE field_form_bindings SET form_definition_id = gen_random_uuid() WHERE organization_id = '{w.S.OrganizationId:D}';"), StringComparison.Ordinal);
        Assert.Contains("permission denied", AsRuntime(string.Empty, $"DELETE FROM field_form_bindings WHERE organization_id = '{w.S.OrganizationId:D}';"), StringComparison.Ordinal);
        Assert.Contains("ck_field_search_approval_scope", fixture.Stack.AuthorityAdministratorSql($"BEGIN; UPDATE field_search_approvals SET approval_scope_name = 'Historical' WHERE field_search_approval_id = '{retainedId}'; ROLLBACK;", throwOnError: false), StringComparison.Ordinal);
        Assert.True((await ApproveAsync(w, w.First, null)).IsSuccess); // a current-version approval of a field still in the form
        var demote = fixture.Stack.AuthorityAdministratorSql($"BEGIN; UPDATE field_search_approvals SET approval_scope_name = 'RetainedField', source_form_version_number = 1 WHERE organization_id = '{w.S.OrganizationId:D}' AND field_definition_id = '{w.First:D}' AND approval_state_name = 'Active'; ROLLBACK;", throwOnError: false);
        Assert.Contains("never becomes a retained one", demote, StringComparison.Ordinal);

        // Backfill guard: the migration's own SQL stops on a field associated with two forms (seeded inside a transaction that rolls back).
        var migration = File.ReadAllText(Path.Combine(ComposeStack.RepositoryRoot, "src", "authority", "AuthorizationAuthority.Infrastructure", "Persistence", "Migrations", "20261124031236_AuthorityC188RetainedFieldSearchApprovals.cs"));
        var backfill = Regex.Match(migration, "(DO \\$\\$\\s*DECLARE conflicting.*?END\\s*\\$\\$;)", RegexOptions.Singleline).Groups[1].Value;
        Assert.Contains("RAISE EXCEPTION 'C-188:", backfill, StringComparison.Ordinal);
        var versionsBefore = Scalar($"SELECT count(*) FROM published_form_versions WHERE organization_id = '{w.S.OrganizationId:D}';");
        var conflict = fixture.Stack.AuthorityAdministratorSql(
            "BEGIN; INSERT INTO published_form_versions (published_form_version_id, organization_id, form_definition_id, form_version_number, layout_digest, layout_digest_scheme, field_manifest_json, registered_by_security_command_id, registered_on) " +
            $"SELECT gen_random_uuid(), organization_id, gen_random_uuid(), 1, layout_digest, layout_digest_scheme, field_manifest_json, registered_by_security_command_id, now() FROM published_form_versions WHERE organization_id = '{w.S.OrganizationId:D}' AND form_definition_id = '{w.FormId:D}' AND form_version_number = 1; " +
            backfill + " ROLLBACK;", throwOnError: false);
        Assert.Contains("associated with more than one form", conflict, StringComparison.Ordinal);
        Assert.Equal(versionsBefore, Scalar($"SELECT count(*) FROM published_form_versions WHERE organization_id = '{w.S.OrganizationId:D}';")); // nothing persisted
    }

    // ---------------------------------------------------------------- T22/T23: rollback rehearsal (flag off)

    [Fact]
    public async Task DisablingRetainedApprovalsRevokesCapabilitiesAndOpenPagesWhileWithdrawalStillWorks()
    {
        var (w, _, _, _) = await RemovedPhoneAsync("c188-rollback", extra: 10);
        Assert.True((await ApproveAsync(w, w.Phone, "RetainedField")).IsSuccess);
        var first = await SearchAsync(w, w.S.Clerk, w.Phone, "startsWith", "555-0", 10);
        Assert.Equal(12, first.Root.GetProperty("totalCount").GetInt32());
        var next = first.Root.GetProperty("continuationToken").GetString();
        try
        {
            RecreateAuthority("Disabled");
            await w.S.WaitGateReadyAsync(TimeSpan.FromSeconds(180));

            // Not effective at once: the open V1 page (snapshot issued by the previous process) ends, no capability, searches refused,
            // no new retained approval; the approval row itself is untouched until an administrator withdraws it.
            Assert.Equal("SEARCH_SNAPSHOT_CHANGED", (await fixture.Api.PostAsync("/api/v1/form-records/field-search/page", w.S.Token(w.S.Clerk), new { cursor = next })).ErrorCode);
            Assert.Null(Capability(await CapabilitiesAsync(w, w.S.Clerk), w.Phone));
            AssertRefused(await SearchAsync(w, w.S.Clerk, w.Phone, "startsWith", "555-0"));
            Assert.Equal("RETAINED_FIELD_APPROVALS_DISABLED", (await ApproveAsync(w, w.Phone, "RetainedField")).ErrorCode);
            Assert.Equal("Active||RetainedField|1", Tail(Approvals(w)[^1]));
            var configuration = await fixture.Api.GetAsync($"/api/v1/form-definitions/{w.FormId:D}/field-search", w.S.Token(w.S.Administrator));
            Assert.False(configuration.Root.GetProperty("retainedFieldApprovalsEnabled").GetBoolean());

            // Durable rollback: withdraw (audited; advances the revision). No active retained approval remains in the organization.
            Assert.True((await WithdrawAsync(w)).IsSuccess);
            Assert.Equal("Withdrawn||RetainedField|1", Tail(Approvals(w)[^1]));
            Assert.Equal("0", Scalar($"SELECT count(*) FROM field_search_approvals WHERE organization_id = '{w.S.OrganizationId:D}' AND approval_scope_name = 'RetainedField' AND approval_state_name = 'Active';"));
        }
        finally
        {
            RecreateAuthority(null); // back to the overlay default (Enabled)
            await w.S.WaitGateReadyAsync(TimeSpan.FromSeconds(180));
        }

        // Re-enabled: the withdrawn approval stays withdrawn (never revived); searching needs a new explicit approval.
        AssertRefused(await SearchAsync(w, w.S.Clerk, w.Phone, "startsWith", "555-0"));
        Assert.True((await ApproveAsync(w, w.Phone, "RetainedField")).IsSuccess);
        Assert.Equal(12, (await SearchAsync(w, w.S.Clerk, w.Phone, "startsWith", "555-0")).Root.GetProperty("totalCount").GetInt32());
    }
}
