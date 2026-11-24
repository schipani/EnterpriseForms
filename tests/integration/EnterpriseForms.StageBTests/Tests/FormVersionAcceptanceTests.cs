using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using EnterpriseForms.SecurityTests.Harness;
using EnterpriseForms.StageBTests.Harness;
using Xunit;

namespace EnterpriseForms.StageBTests.Tests;

/// <summary>
/// Phase C9 M3 (WP-7): the consolidated acceptance scenarios of CLAUDE.md §11 #1–#6 on the real stack (business API,
/// Authorization Authority, SpiceDB, Vault Transit, both PostgreSQL servers). Synthetic data only.
/// <list type="number">
/// <item>V1 → V2 → V3 (restored from V1) → V4 (the original email field re-added): field identities, created-version
/// metadata, stored values, authorized history, verified field search where the existing capability supports it,
/// semantic comparisons, publish-impact notices, immutable snapshots and share projections.</item>
/// <item>Historical, Latest and Hybrid on three forms with equivalent histories: view and edit layouts, created and
/// last-edited versions, values outside the layout, authorization, restore against the edit layout, fail-closed edit
/// layouts and stale-layout refusals (update, Submit, restore, upload begin) with no partial write.</item>
/// <item>Changing the behaviour after records exist: impact preview, authorization, validation, concurrency, audit and
/// no migration of values, records or versions.</item>
/// </list>
/// Unchanged existing behaviour that the scenarios record rather than alter: publishing a version without a searchable
/// field invalidates that field's search approval in the Authority (FORM_VERSION_CHANGED); restoring the field does not
/// reinstate the approval, an administrator approves it again.
/// </summary>
[Collection(StageBStackCollectionDefinition.Name)]
public sealed class FormVersionAcceptanceTests(StageBFixture fixture)
{
    private static readonly HttpStatusCode[] Denied = [HttpStatusCode.Forbidden, HttpStatusCode.NotFound];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ---------------------------------------------------------------- helpers

    /// <summary>Reads and history reads fail closed (503, retryable) while the Authority archives the latest journal entries; clients retry, so do the tests.</summary>
    private static async Task<HttpResult> RetryUnavailableAsync(Func<Task<HttpResult>> read)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(40);
        HttpResult last;
        do
        {
            last = await read();
            if (last.Status != HttpStatusCode.ServiceUnavailable)
            {
                return last;
            }

            await Task.Delay(250, Ct);
        }
        while (DateTime.UtcNow < deadline);
        return last;
    }

    private static async Task<JsonElement> ViewAsync(StageBScenario s, TestUser user, Guid record) => (await s.WaitProjectionAsync(user, record, TimeSpan.FromSeconds(40))).Root.Clone();

    private Task<HttpResult> EditProjectionAsync(StageBScenario s, TestUser user, Guid record) =>
        RetryUnavailableAsync(() => fixture.Api.GetAsync($"/api/v1/form-records/{record:D}/projection?purpose=edit", s.Token(user)));

    private async Task<JsonElement> EditAsync(StageBScenario s, TestUser user, Guid record)
    {
        var result = await EditProjectionAsync(s, user, record);
        Assert.True(result.IsSuccess, "edit projection failed: " + result.Body);
        return result.Root.Clone();
    }

    private static long Revision(JsonElement projection) => projection.GetProperty("record").GetProperty("contentRevision").GetInt64();

    private static int LayoutNumber(JsonElement projection) => projection.GetProperty("layoutVersion").GetProperty("versionNumber").GetInt32();

    private static Guid LayoutId(JsonElement projection) => projection.GetProperty("layoutVersion").GetProperty("formVersionId").GetGuid();

    private static Dictionary<string, JsonElement> Values(JsonElement projection) =>
        projection.GetProperty("record").GetProperty("values").EnumerateArray().Where(v => v.GetProperty("formRecordGroupId").ValueKind == JsonValueKind.Null).ToDictionary(v => v.GetProperty("fieldKey").GetString()!, v => v.Clone(), StringComparer.Ordinal);

    private static string? ValueOf(JsonElement projection, string fieldKey) => Values(projection).TryGetValue(fieldKey, out var value) ? value.GetProperty("stringValue").GetString() : null;

    /// <summary>The value's stable field identity: a value is never attributed to another field.</summary>
    private static Guid FieldOf(JsonElement projection, string fieldKey) => Values(projection)[fieldKey].GetProperty("fieldDefinitionId").GetGuid();

    private Task<HttpResult> PatchAsync(StageBScenario s, TestUser user, Guid record, long revision, Dictionary<string, string> values, Guid? expectedLayout) =>
        fixture.Api.PatchAsync($"/api/v1/form-records/{record:D}", s.Token(user), new { expectedContentRevision = revision, values, expectedLayoutVersionId = expectedLayout });

    private Task<HttpResult> SubmitAsync(StageBScenario s, TestUser user, Guid record, long revision, Guid? expectedLayout) =>
        fixture.Api.PostAsync($"/api/v1/form-records/{record:D}/submit", s.Token(user), new { expectedContentRevision = revision, expectedLayoutVersionId = expectedLayout });

    private Task<HttpResult> RestorePreviewAsync(StageBScenario s, TestUser user, Guid record, long revision) =>
        RetryUnavailableAsync(() => fixture.Api.GetAsync($"/api/v1/form-records/{record:D}/versions/{revision}/restore-preview", s.Token(user)));

    private Task<HttpResult> RestoreAsync(StageBScenario s, TestUser user, Guid record, long revision, JsonElement preview, Guid? expectedLayout) =>
        fixture.Api.PostAsync($"/api/v1/form-records/{record:D}/versions/{revision}/restore", s.Token(user), new { expectedCurrentContentRevision = preview.GetProperty("currentContentRevision").GetInt64(), sourceOperationId = preview.GetProperty("sourceOperationId").GetGuid(), restoreReason = "acceptance restore", expectedLayoutVersionId = expectedLayout });

    private Task<HttpResult> HistoryAsync(StageBScenario s, TestUser user, Guid record) =>
        RetryUnavailableAsync(() => fixture.Api.GetAsync($"/api/v1/form-records/{record:D}/versions", s.Token(user)));

    private Task<HttpResult> HistoryVersionAsync(StageBScenario s, TestUser user, Guid record, long revision) =>
        RetryUnavailableAsync(() => fixture.Api.GetAsync($"/api/v1/form-records/{record:D}/versions/{revision}", s.Token(user)));

    private Task<HttpResult> CompareAsync(StageBScenario s, Guid formId, Guid from, Guid to) =>
        fixture.Api.GetAsync($"/api/v1/form-definitions/{formId:D}/versions/compare?from={from:D}&to={to:D}", s.Token(s.Designer));

    private string Sql(string query) => fixture.Stack.AttackerSql(query).Trim();

    private string CreatedVersion(Guid record) => Sql($"SELECT created_form_version_id::text FROM form_records WHERE form_record_id = '{record:D}';");

    private string LastEdited(Guid record) => Sql($"SELECT coalesce(last_edited_form_version_id::text, '') FROM form_records WHERE form_record_id = '{record:D}';");

    private string ApprovalState(StageBScenario s, Guid field) =>
        fixture.Stack.AuthorityObserverSql($"SELECT approval_state_name || '|' || coalesce(invalidation_reason_code, '') FROM field_search_approvals WHERE organization_id = '{s.OrganizationId:D}' AND field_definition_id = '{field:D}' ORDER BY approved_on DESC LIMIT 1;").Trim();

    /// <summary>Creates a record; a closed organization gate right after a security command is retried with the same idempotency key.</summary>
    private async Task<Guid> CreateRecordAsync(StageBScenario s, TestUser user, Guid formId, Dictionary<string, string> values)
    {
        var key = "c9-m3-" + Guid.CreateVersion7().ToString("N");
        for (var attempt = 0; ; attempt++)
        {
            var created = await fixture.Api.PostAsync($"/api/v1/form-definitions/{formId:D}/records", s.Token(user), new { values, ownerTeamId = (Guid?)null }, key);
            if (created.Status is HttpStatusCode.Created or HttpStatusCode.OK)
            {
                var id = created.Root.GetProperty("formRecordId").GetGuid();
                await s.WaitProjectionAsync(user, id, TimeSpan.FromSeconds(40));
                return id;
            }

            Assert.True(attempt < 60 && created.ErrorCode is "AUTHORITY_NOT_READY" or "STALE_AUTHORIZATION_REVISION" or "COMMAND_PENDING", "create record failed: " + created.Body);
            await Task.Delay(500, Ct);
        }
    }

    private async Task<Guid> AddFieldAsync(StageBScenario s, Guid formId, string key, string label, string dataType, string? classification = null, string? protection = null)
    {
        var added = await fixture.Api.PostAsync($"/api/v1/form-definitions/{formId:D}/fields", s.Token(s.Designer), new { fieldKey = key, fieldDisplayName = label, fieldDescription = string.Empty, fieldDataType = dataType, options = (object?)null, fieldClassification = classification, fieldProtectionMode = protection });
        Assert.True(added.Status == HttpStatusCode.Created, $"add field {key} failed: " + added.Body);
        return added.Root.GetProperty("fieldDefinitionId").GetGuid();
    }

    private static async Task<long> FormRevisionAsync(StageBScenario s, Guid formId) => (await s.GetFormAsync(formId)).GetProperty("summary").GetProperty("formDefinitionRevision").GetInt64();

    private Task<HttpResult> ChangeBehaviorAsync(StageBScenario s, Guid formId, TestUser actor, string behavior, long expectedRevision, string reason) =>
        fixture.Api.SendAsync(HttpMethod.Put, $"/api/v1/form-definitions/{formId:D}/version-behavior", s.Token(actor), new { versionBehavior = behavior, expectedFormDefinitionRevision = expectedRevision, changeReason = reason }, null);

    private async Task SetBehaviorAsync(StageBScenario s, Guid formId, string behavior)
    {
        var changed = await ChangeBehaviorAsync(s, formId, s.Designer, behavior, await FormRevisionAsync(s, formId), "acceptance: " + behavior);
        Assert.True(changed.IsSuccess, changed.Body);
        Assert.Equal(behavior, changed.Root.GetProperty("summary").GetProperty("versionBehavior").GetString());
    }

    private async Task<Dictionary<int, JsonElement>> VersionsAsync(StageBScenario s, Guid formId)
    {
        var versions = await fixture.Api.GetAsync($"/api/v1/form-definitions/{formId:D}/versions", s.Token(s.Designer));
        Assert.True(versions.IsSuccess, versions.Body);
        return versions.Root.EnumerateArray().ToDictionary(v => v.GetProperty("versionNumber").GetInt32(), v => v.Clone());
    }

    private static JsonNode? Parsed(JsonElement version) => JsonNode.Parse(version.GetProperty("layoutJson").GetString()!);

    private static List<Guid> LayoutFieldIds(JsonElement version) =>
        Parsed(version)!["sections"]!.AsArray().SelectMany(section => section!["elements"]!.AsArray()).Where(e => e!["fieldDefinitionId"] is not null).Select(e => Guid.Parse(e!["fieldDefinitionId"]!.GetValue<string>())).ToList();

    private static List<string> NoticeLabels(JsonElement impact, string code) =>
        impact.GetProperty("notices").EnumerateArray().Where(n => n.GetProperty("code").GetString() == code).SelectMany(n => n.GetProperty("elementLabels").EnumerateArray().Select(l => l.GetString()!)).ToList();

    private static List<(string ChangeType, Guid ElementId)> Changes(JsonElement comparison) =>
        comparison.GetProperty("changes").EnumerateArray().Where(c => c.GetProperty("changeType").GetString() is "Added" or "Removed")
            .Select(c => (c.GetProperty("changeType").GetString()!, Guid.Parse(c.GetProperty("elementId").GetString()!))).OrderBy(c => c.Item1, StringComparer.Ordinal).ToList();

    private async Task SearchablePolicyAsync(StageBScenario s)
    {
        var current = await fixture.Api.GetAsync("/api/v1/organization/security-policy", s.Token(s.Designer));
        Assert.True(current.IsSuccess, current.Body);
        var policy = await fixture.Api.SendAsync(HttpMethod.Put, "/api/v1/organization/security-policy", s.Token(s.Designer), new { minimumFieldClassification = "Internal", plaintextInternalFieldsPermitted = true, plaintextDowngradePermitted = false, plaintextSearchPermitted = true, policyStatementText = "Selected plaintext Internal fields may be searched in this synthetic organization.", expectedPolicyRevision = current.Root.GetProperty("securityPolicyRevision").GetInt64() }, Guid.NewGuid().ToString("N"));
        Assert.True(policy.IsSuccess, policy.Body);
    }

    private async Task ApproveSearchAsync(StageBScenario s, Guid formId, Guid field)
    {
        var approved = await fixture.Api.PostAsync($"/api/v1/form-definitions/{formId:D}/fields/{field:D}/search-approval", s.Token(s.Administrator), new { operators = new[] { "equals", "startsWith" }, approvalReference = "C9-M3", useDevelopmentStepUp = true });
        Assert.True(approved.IsSuccess, approved.Body);
        await s.WaitGateReadyAsync(TimeSpan.FromSeconds(40));
    }

    /// <summary>A client's retry: only typed retryable refusals (snapshot changed, archive settling, busy) are resent, briefly.</summary>
    private async Task<HttpResult> SearchAsync(StageBScenario s, TestUser user, Guid formId, Guid field, string op, string value)
    {
        for (var attempt = 0; ; attempt++)
        {
            var result = await fixture.Api.PostAsync("/api/v1/form-records/field-search", s.Token(user), new
            {
                formDefinitionId = formId,
                conditions = new[] { new { fieldDefinitionId = field, @operator = op, valueType = "String", value, lowerBound = (string?)null, upperBound = (string?)null } },
                recordStatus = (string?)null,
                includeArchived = false,
                includeChildren = false,
                sortBy = (string?)null,
                sortDirection = (string?)null,
                pageSize = 25,
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

    private static void AssertNoResult(HttpResult result, string code)
    {
        Assert.False(result.IsSuccess, result.Body);
        Assert.Equal(code, result.ErrorCode);
        Assert.False(result.Root.TryGetProperty("items", out _));
        Assert.False(result.Root.TryGetProperty("totalCount", out _));
    }

    private static string Element(Guid fieldId, string label, bool required = false) => StageBScenario.Field(fieldId, "text", label, 4, required: required);

    // ---------------------------------------------------------------- scenarios 1–4

    [Fact]
    public async Task TheV1ToV4LifecycleKeepsFieldIdentitiesValuesHistoryAndSnapshots()
    {
        var s = new StageBScenario(fixture, "c9-m3-lifecycle");
        await s.BootstrapAsync();
        await SearchablePolicyAsync(s);
        var formId = await s.CreateFormAsync("Acceptance intake " + Guid.NewGuid().ToString("N")[..6], s.Clerk, s.Designer);
        var first = await AddFieldAsync(s, formId, "first_name", "First name", "String");
        var last = await AddFieldAsync(s, formId, "last_name", "Last name", "String");
        var phone = await AddFieldAsync(s, formId, "phone_number", "Phone", "String", "Internal", "Standard"); // plaintext Internal: approvable for search (ADR-PB-004)
        var other = new StageBScenario(fixture, "c9-m3-lifecycle-other");
        await other.BootstrapAsync();

        // ================= Scenario 1: create and use V1 =================
        await s.SaveDraftAsync(formId, StageBScenario.Layout("Acceptance intake", StageBScenario.Section("main", "Main",
            Element(first, "First name", required: true), Element(last, "Last name", required: true), Element(phone, "Phone"))));
        var v1Published = await s.PublishAsync(formId, "acceptance v1");
        Assert.True(v1Published.GetProperty("authorityRegistered").GetBoolean());
        var v1 = v1Published.GetProperty("formVersionId").GetGuid();
        Assert.Equal(1, v1Published.GetProperty("versionNumber").GetInt32());
        await ApproveSearchAsync(s, formId, phone);
        Assert.Equal(HttpStatusCode.NoContent, (await fixture.Api.SendAsync(HttpMethod.Put, $"/api/v1/form-definitions/{formId:D}/field-searchers", s.Token(s.Administrator), new { grantUserIds = new[] { s.Clerk.AuthorityUserId }, grantTeamIds = Array.Empty<Guid>(), revokeUserIds = Array.Empty<Guid>(), revokeTeamIds = Array.Empty<Guid>() }, Guid.NewGuid().ToString("N"))).Status);
        await s.WaitGateReadyAsync(TimeSpan.FromSeconds(40));

        var r1 = await CreateRecordAsync(s, s.Clerk, formId, new() { ["first_name"] = "Avery", ["last_name"] = "Synthetic", ["phone_number"] = "555-0101" });
        var r2 = await CreateRecordAsync(s, s.Clerk, formId, new() { ["first_name"] = "Blake", ["last_name"] = "Synthetic", ["phone_number"] = "555-0102" });
        var rD = await CreateRecordAsync(s, s.Designer, formId, new() { ["first_name"] = "Drew", ["last_name"] = "Synthetic", ["phone_number"] = "555-0199" }); // the designer's own record

        var view = await ViewAsync(s, s.Clerk, r1);
        Assert.Equal((1, 1), (LayoutNumber(view), view.GetProperty("createdFormVersionNumber").GetInt32()));
        Assert.Equal(("Avery", "Synthetic", "555-0101"), (ValueOf(view, "first_name"), ValueOf(view, "last_name"), ValueOf(view, "phone_number")));
        Assert.Equal((first, last, phone), (FieldOf(view, "first_name"), FieldOf(view, "last_name"), FieldOf(view, "phone_number")));
        Assert.Equal(v1.ToString("D"), CreatedVersion(r1));
        Assert.Equal(v1.ToString("D"), CreatedVersion(r2));
        var submitted = await SubmitAsync(s, s.Clerk, r2, Revision(await ViewAsync(s, s.Clerk, r2)), v1);
        Assert.True(submitted.IsSuccess, submitted.Body);

        // History: one version, holding the phone value.
        var history = await HistoryAsync(s, s.Clerk, r1);
        Assert.True(history.IsSuccess, history.Body);
        Assert.Equal([1L], history.Root.GetProperty("versions").EnumerateArray().Select(v => v.GetProperty("contentRevision").GetInt64()));

        // Verified field search over the clerk's discoverable records only (never the designer's record).
        Assert.Equal([r1], Matches(await SearchAsync(s, s.Clerk, formId, phone, "equals", "555-0101")));
        Assert.Equal(new HashSet<Guid> { r1, r2 }, Matches(await SearchAsync(s, s.Clerk, formId, phone, "startsWith", "555-01")));

        // Unauthorized: no grant, another organization, a person without query permission.
        Assert.Contains((await s.ProjectionAsync(s.Newcomer, r1)).Status, Denied);
        Assert.Contains((await other.ProjectionAsync(other.Clerk, r1)).Status, Denied);
        Assert.Contains((await HistoryAsync(other, other.Clerk, r1)).Status, Denied);
        AssertNoResult(await SearchAsync(s, s.Reader, formId, phone, "equals", "555-0101"), "SEARCH_FIELD_NOT_ALLOWED");

        // ================= Scenario 2: publish V2 (phone removed, email added) =================
        var email = await AddFieldAsync(s, formId, "email_address", "Email", "String"); // unclassified free text: Confidential, encrypted by default
        var draftV2 = await s.SaveDraftAsync(formId, StageBScenario.Layout("Acceptance intake", StageBScenario.Section("main", "Main",
            Element(first, "First name", required: true), Element(last, "Last name", required: true), Element(email, "Email", required: true))));
        var impact = await fixture.Api.GetAsync($"/api/v1/form-definitions/{formId:D}/publish-impact?expectedDraftRevision={draftV2}", s.Token(s.Designer));
        Assert.True(impact.IsSuccess, impact.Body);
        Assert.True(impact.Root.GetProperty("canPublish").GetBoolean());
        Assert.Equal(["Email"], NoticeLabels(impact.Root, "NEW_REQUIRED_FIELDS"));
        Assert.Equal(["Phone"], NoticeLabels(impact.Root, "ELEMENTS_REMOVED"));
        Assert.Equal([("Added", email), ("Removed", phone)], Changes(impact.Root.GetProperty("comparison")));
        var impactRecords = impact.Root.GetProperty("records");
        Assert.Equal((1, 1, 1), (impactRecords.GetProperty("visibleRecordCount").GetInt32(), impactRecords.GetProperty("shownWithNewVersionCount").GetInt32(), impactRecords.GetProperty("editedWithNewVersionCount").GetInt32())); // the designer's own record only
        Assert.True(impactRecords.GetProperty("countsAreApproximate").GetBoolean());
        Assert.Contains("never deletes or changes stored values", impact.Root.GetProperty("dataStatement").GetString(), StringComparison.Ordinal);
        foreach (var leaked in new[] { r1.ToString("D"), r2.ToString("D"), rD.ToString("D"), "555-01", "Avery", "Drew" })
        {
            Assert.DoesNotContain(leaked, impact.Body, StringComparison.OrdinalIgnoreCase);
        }

        Assert.Contains((await fixture.Api.GetAsync($"/api/v1/form-definitions/{formId:D}/publish-impact?expectedDraftRevision={draftV2}", s.Token(s.Clerk))).Status, Denied);
        var v2 = (await s.PublishAsync(formId, "acceptance v2: email added, phone removed")).GetProperty("formVersionId").GetGuid();
        var snapshots = await VersionsAsync(s, formId);
        var (v1Json, v2Json) = (Parsed(snapshots[1])!.ToJsonString(), Parsed(snapshots[2])!.ToJsonString());

        // V1 records are neither migrated nor deleted: same created version, phone kept, shown with V2 under Latest.
        view = await ViewAsync(s, s.Clerk, r1);
        Assert.Equal((2, 1), (LayoutNumber(view), view.GetProperty("createdFormVersionNumber").GetInt32()));
        Assert.Equal("555-0101", ValueOf(view, "phone_number"));
        Assert.Equal(phone, FieldOf(view, "phone_number"));
        Assert.Equal(v1.ToString("D"), CreatedVersion(r1));
        Assert.Equal("555-0102", ValueOf(await ViewAsync(s, s.Clerk, r2), "phone_number"));
        Assert.Equal("Submitted", (await ViewAsync(s, s.Clerk, r2)).GetProperty("lifecycle").GetProperty("status").GetString());
        var historical = await HistoryVersionAsync(s, s.Clerk, r1, 1);
        Assert.True(historical.IsSuccess, historical.Body);
        Assert.Equal("555-0101", ValueOf(historical.Root.GetProperty("projection"), "phone_number"));

        // Search: the existing capability invalidates an approval when a version without the field is registered (fail closed).
        Assert.Equal("Invalidated|FORM_VERSION_CHANGED", ApprovalState(s, phone));
        AssertNoResult(await SearchAsync(s, s.Clerk, formId, phone, "equals", "555-0101"), "SEARCH_FIELD_NOT_ALLOWED");
        AssertNoResult(await SearchAsync(s, s.Clerk, formId, email, "equals", "x@example.test"), "SEARCH_FIELD_NOT_ALLOWED"); // encrypted: never searchable (ADR-PB-004)

        // New records use V2; an existing record edited under Latest is validated against V2 and keeps its phone value.
        var r3 = await CreateRecordAsync(s, s.Clerk, formId, new() { ["first_name"] = "Casey", ["last_name"] = "Synthetic", ["email_address"] = "casey@example.test" });
        view = await ViewAsync(s, s.Clerk, r3);
        Assert.Equal((2, 2), (LayoutNumber(view), view.GetProperty("createdFormVersionNumber").GetInt32()));
        Assert.Equal(v2.ToString("D"), CreatedVersion(r3));
        Assert.Null(ValueOf(view, "phone_number"));
        var edit = await EditAsync(s, s.Clerk, r1);
        Assert.Equal(v2, LayoutId(edit));
        var missingEmail = await PatchAsync(s, s.Clerk, r1, Revision(edit), new() { ["first_name"] = "Avery" }, v2);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, missingEmail.Status);
        var saved = await PatchAsync(s, s.Clerk, r1, Revision(edit), new() { ["email_address"] = "avery@example.test" }, v2);
        Assert.True(saved.IsSuccess, saved.Body);
        view = await ViewAsync(s, s.Clerk, r1);
        Assert.Equal(("avery@example.test", "555-0101"), (ValueOf(view, "email_address"), ValueOf(view, "phone_number")));
        Assert.Equal((v2.ToString("D"), v1.ToString("D")), (LastEdited(r1), CreatedVersion(r1)));

        // Version comparison V1 → V2: exactly email added and phone removed, by stable identity.
        var compared = await CompareAsync(s, formId, v1, v2);
        Assert.True(compared.IsSuccess, compared.Body);
        Assert.Equal([("Added", email), ("Removed", phone)], Changes(compared.Root.GetProperty("comparison")));

        // A partial share never exposes a field outside its scope, whatever the layout; history stays whole-record only.
        var share = await fixture.Api.PostAsync($"/api/v1/form-records/{r1:D}/shares", s.Token(s.Clerk), new { recipientUserId = s.Reader.AuthorityUserId, recipientTeamId = (Guid?)null, recipientRole = "Reader", fieldKeys = new[] { "first_name", "email_address" } });
        Assert.True(share.IsSuccess, share.Body);
        await s.WaitGateReadyAsync(TimeSpan.FromSeconds(40));
        var readerView = await ViewAsync(s, s.Reader, r1);
        Assert.Equal(["email_address", "first_name"], Values(readerView).Keys.Order(StringComparer.Ordinal));
        Assert.DoesNotContain("555-0101", readerView.GetRawText(), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.NotFound, (await HistoryVersionAsync(s, s.Reader, r1, 1)).Status);

        // ================= Scenario 3: restore V1 as a draft and publish V3 =================
        var restoredDraft = await fixture.Api.PostAsync($"/api/v1/form-definitions/{formId:D}/restore-draft", s.Token(s.Designer), new { fromFormVersionId = v1 });
        Assert.True(restoredDraft.IsSuccess, restoredDraft.Body);
        Assert.Equal(v1, restoredDraft.Root.GetProperty("basedOnFormVersionId").GetGuid());
        Assert.Contains((await fixture.Api.PostAsync($"/api/v1/form-definitions/{formId:D}/restore-draft", s.Token(s.Clerk), new { fromFormVersionId = v1 })).Status, Denied);
        var impactV3 = await fixture.Api.GetAsync($"/api/v1/form-definitions/{formId:D}/publish-impact?expectedDraftRevision={restoredDraft.Root.GetProperty("draftRevision").GetInt64()}", s.Token(s.Designer));
        Assert.True(impactV3.IsSuccess, impactV3.Body);
        Assert.Equal([("Added", phone), ("Removed", email)], Changes(impactV3.Root.GetProperty("comparison")));
        Assert.Equal(["Email"], NoticeLabels(impactV3.Root, "ELEMENTS_REMOVED"));
        var v3Published = await s.PublishAsync(formId, "acceptance v3: restore v1");
        var v3 = v3Published.GetProperty("formVersionId").GetGuid();
        Assert.Equal(3, v3Published.GetProperty("versionNumber").GetInt32());
        Assert.Equal(v1, v3Published.GetProperty("restoredFromFormVersionId").GetGuid());
        Assert.Equal([first, last, phone], LayoutFieldIds(v3Published)); // phone reappears with its original identity
        snapshots = await VersionsAsync(s, formId);
        Assert.Equal((v1Json, v2Json), (Parsed(snapshots[1])!.ToJsonString(), Parsed(snapshots[2])!.ToJsonString())); // V1 and V2 unchanged
        Assert.False((await CompareAsync(s, formId, v1, v3)).Root.GetProperty("comparison").GetProperty("hasChanges").GetBoolean());
        var v3Json = Parsed(snapshots[3])!.ToJsonString();

        view = await ViewAsync(s, s.Clerk, r1);
        Assert.Equal(3, LayoutNumber(view));
        Assert.Equal(("555-0101", phone), (ValueOf(view, "phone_number"), FieldOf(view, "phone_number")));
        Assert.Equal(("avery@example.test", email), (ValueOf(view, "email_address"), FieldOf(view, "email_address"))); // email kept although no longer in the layout
        Assert.Equal("555-0102", ValueOf(await ViewAsync(s, s.Clerk, r2), "phone_number"));
        view = await ViewAsync(s, s.Clerk, r3);
        Assert.Equal(("casey@example.test", (string?)null), (ValueOf(view, "email_address"), ValueOf(view, "phone_number")));
        history = await HistoryAsync(s, s.Clerk, r1);
        Assert.Equal([1L, 2L], history.Root.GetProperty("versions").EnumerateArray().Select(v => v.GetProperty("contentRevision").GetInt64()));
        var revisionTwo = (await HistoryVersionAsync(s, s.Clerk, r1, 2)).Root.GetProperty("projection");
        Assert.Equal(("555-0101", "avery@example.test"), (ValueOf(revisionTwo, "phone_number"), ValueOf(revisionTwo, "email_address")));

        // Restoring the field does not reinstate its invalidated approval; an administrator approves it again.
        AssertNoResult(await SearchAsync(s, s.Clerk, formId, phone, "equals", "555-0101"), "SEARCH_FIELD_NOT_ALLOWED");
        await ApproveSearchAsync(s, formId, phone);
        Assert.Equal("Active|", ApprovalState(s, phone));
        Assert.Equal([r1], Matches(await SearchAsync(s, s.Clerk, formId, phone, "equals", "555-0101")));
        Assert.Equal(new HashSet<Guid> { r1, r2 }, Matches(await SearchAsync(s, s.Clerk, formId, phone, "startsWith", "555-01")));

        // ================= Scenario 4: re-add the original email field and publish V4 =================
        var duplicate = await fixture.Api.PostAsync($"/api/v1/form-definitions/{formId:D}/fields", s.Token(s.Designer), new { fieldKey = "email_address", fieldDisplayName = "Email", fieldDescription = string.Empty, fieldDataType = "String" });
        Assert.Equal(HttpStatusCode.Conflict, duplicate.Status);
        Assert.Equal("FIELD_KEY_EXISTS", duplicate.ErrorCode);
        var draft = (await s.GetFormAsync(formId)).GetProperty("draft");
        Assert.Equal(v3, draft.GetProperty("basedOnFormVersionId").GetGuid());
        var v4Layout = JsonNode.Parse(draft.GetProperty("layoutJson").GetString()!)!;
        v4Layout["sections"]![0]!["elements"]!.AsArray().Add(JsonNode.Parse(Element(email, "Email", required: true)));
        await s.SaveDraftAsync(formId, v4Layout.ToJsonString(), draft.GetProperty("draftRevision").GetInt64());
        var v4Published = await s.PublishAsync(formId, "acceptance v4: original email field re-added");
        var v4 = v4Published.GetProperty("formVersionId").GetGuid();
        Assert.Equal(4, v4Published.GetProperty("versionNumber").GetInt32());
        Assert.Equal(JsonValueKind.Null, v4Published.GetProperty("restoredFromFormVersionId").ValueKind);
        Assert.Equal([first, last, phone, email], LayoutFieldIds(v4Published));

        var fields = (await s.GetFormAsync(formId)).GetProperty("fields").EnumerateArray().Select(f => (f.GetProperty("fieldKey").GetString()!, f.GetProperty("fieldDefinitionId").GetGuid())).OrderBy(f => f.Item1, StringComparer.Ordinal).ToList();
        Assert.Equal([("email_address", email), ("first_name", first), ("last_name", last), ("phone_number", phone)], fields); // no duplicate logical field
        snapshots = await VersionsAsync(s, formId);
        Assert.Equal((v1Json, v2Json, v3Json), (Parsed(snapshots[1])!.ToJsonString(), Parsed(snapshots[2])!.ToJsonString(), Parsed(snapshots[3])!.ToJsonString()));
        Assert.Equal([1, 2, 3, 4], snapshots.Keys.Order());
        var v3ToV4 = await CompareAsync(s, formId, v3, v4);
        Assert.Equal([("Added", email)], Changes(v3ToV4.Root.GetProperty("comparison")));
        Assert.Equal((1, 0), (v3ToV4.Root.GetProperty("comparison").GetProperty("addedCount").GetInt32(), v3ToV4.Root.GetProperty("comparison").GetProperty("removedCount").GetInt32()));

        // Email values are associated with the original identity; no crossover between records.
        view = await ViewAsync(s, s.Clerk, r1);
        Assert.Equal(4, LayoutNumber(view));
        Assert.Equal(("avery@example.test", email, "555-0101"), (ValueOf(view, "email_address"), FieldOf(view, "email_address"), ValueOf(view, "phone_number")));
        view = await ViewAsync(s, s.Clerk, r3);
        Assert.Equal(("casey@example.test", email, (string?)null), (ValueOf(view, "email_address"), FieldOf(view, "email_address"), ValueOf(view, "phone_number")));
        Assert.Null(ValueOf(await ViewAsync(s, s.Clerk, r2), "email_address"));
        edit = await EditAsync(s, s.Clerk, r3);
        Assert.Equal(v4, LayoutId(edit));
        var r3Saved = await PatchAsync(s, s.Clerk, r3, Revision(edit), new() { ["phone_number"] = "555-0103" }, v4);
        Assert.True(r3Saved.IsSuccess, r3Saved.Body);
        view = await ViewAsync(s, s.Clerk, r3);
        Assert.Equal(("casey@example.test", "555-0103"), (ValueOf(view, "email_address"), ValueOf(view, "phone_number")));
        Assert.Equal((v4.ToString("D"), v2.ToString("D")), (LastEdited(r3), CreatedVersion(r3)));

        // An unchanged field's approval carries over to V4.
        Assert.Equal("Active|", ApprovalState(s, phone));
        Assert.Equal(new HashSet<Guid> { r1, r2, r3 }, Matches(await SearchAsync(s, s.Clerk, formId, phone, "startsWith", "555-01")));
    }

    // ---------------------------------------------------------------- scenario 5

    private sealed record BehaviourForm(string Behavior, Guid FormId, Guid First, Guid Phone, Guid Email, Guid Documents, Guid V1, Guid V2, Guid Record);

    private static string DocumentElement(Guid fieldId, params string[] contentTypes) =>
        JsonSerializer.Serialize(new { kind = "documentCollection", fieldDefinitionId = fieldId, label = "Documents", layout = new { columnSpan = 4, startOnNewRow = true }, upload = new { allowMultiple = true, maxFiles = 5, maxFileSizeBytes = 1024 * 1024, allowedContentTypes = contentTypes } });

    /// <summary>V1: first name (required), phone, documents (text or PDF). V2: first name, email (required), documents (PDF only). One record created with V1.</summary>
    private async Task<BehaviourForm> BehaviourFormAsync(StageBScenario s, string behavior)
    {
        var formId = await s.CreateFormAsync($"{behavior} intake " + Guid.NewGuid().ToString("N")[..6], s.Clerk);
        var first = await AddFieldAsync(s, formId, "first_name", "First name", "String");
        var phone = await AddFieldAsync(s, formId, "phone_number", "Phone", "String");
        var email = await AddFieldAsync(s, formId, "email_address", "Email", "String");
        var documents = await AddFieldAsync(s, formId, "supporting_documents", "Documents", "File");
        await s.SaveDraftAsync(formId, StageBScenario.Layout($"{behavior} intake", StageBScenario.Section("main", "Main",
            Element(first, "First name", required: true), Element(phone, "Phone"), DocumentElement(documents, "text/plain", "application/pdf"))));
        var v1 = (await s.PublishAsync(formId, "v1")).GetProperty("formVersionId").GetGuid();
        var record = await CreateRecordAsync(s, s.Clerk, formId, new() { ["first_name"] = "Avery", ["phone_number"] = "555-0101" });
        await s.SaveDraftAsync(formId, StageBScenario.Layout($"{behavior} intake", StageBScenario.Section("main", "Main",
            Element(first, "First name", required: true), Element(email, "Email", required: true), DocumentElement(documents, "application/pdf"))));
        var v2 = (await s.PublishAsync(formId, "v2: email required, phone removed, PDF only")).GetProperty("formVersionId").GetGuid();
        if (behavior != "Latest")
        {
            await SetBehaviorAsync(s, formId, behavior);
        }

        return new BehaviourForm(behavior, formId, first, phone, email, documents, v1, v2, record);
    }

    private Task<HttpResult> BeginUploadAsync(StageBScenario s, BehaviourForm form, string contentType, Guid? expectedLayout) =>
        fixture.Api.PostAsync("/api/v1/upload-sessions", s.Token(s.Clerk), new { formRecordId = form.Record, fieldDefinitionId = form.Documents, originalFileName = contentType == "text/plain" ? "note.txt" : "note.pdf", contentType, sizeBytes = 512L, replacesFileAttachmentId = (Guid?)null, expectedLayoutVersionId = expectedLayout });

    private async Task CancelUploadAsync(StageBScenario s, HttpResult begun)
    {
        Assert.Equal(HttpStatusCode.Created, begun.Status);
        Assert.True((await fixture.Api.SendAsync(HttpMethod.Delete, $"/api/v1/upload-sessions/{begun.Root.GetProperty("uploadSessionId").GetGuid():D}", s.Token(s.Clerk), null, null)).IsSuccess);
    }

    /// <summary>A refused write changed nothing: same content revision and values, same last-edited version.</summary>
    private async Task AssertUnchangedAsync(StageBScenario s, Guid record, JsonElement before, string lastEditedBefore)
    {
        var after = await ViewAsync(s, s.Clerk, record);
        Assert.Equal(Revision(before), Revision(after));
        Assert.Equal(JsonSerializer.Serialize(before.GetProperty("record").GetProperty("values")), JsonSerializer.Serialize(after.GetProperty("record").GetProperty("values")));
        Assert.Equal(lastEditedBefore, LastEdited(record));
    }

    [Fact]
    public async Task HistoricalLatestAndHybridFollowTheirLayoutsOnEquivalentHistories()
    {
        var s = new StageBScenario(fixture, "c9-m3-behaviours");
        await s.BootstrapAsync();
        var other = new StageBScenario(fixture, "c9-m3-behaviours-other");
        await other.BootstrapAsync();
        var forms = new List<BehaviourForm>();
        foreach (var behavior in new[] { "Historical", "Latest", "Hybrid" })
        {
            forms.Add(await BehaviourFormAsync(s, behavior));
        }

        var expected = new Dictionary<string, (int View, int Edit)> { ["Historical"] = (1, 1), ["Latest"] = (2, 2), ["Hybrid"] = (1, 2) };
        foreach (var form in forms)
        {
            var (viewNumber, editNumber) = expected[form.Behavior];
            var editVersion = editNumber == 1 ? form.V1 : form.V2;

            // View and edit layouts, version numbers and metadata.
            var view = await ViewAsync(s, s.Clerk, form.Record);
            Assert.Equal(form.Behavior, view.GetProperty("versionBehavior").GetString());
            Assert.Equal((viewNumber, 1, editVersion, editNumber), (LayoutNumber(view), view.GetProperty("createdFormVersionNumber").GetInt32(), view.GetProperty("editLayoutVersionId").GetGuid(), view.GetProperty("editLayoutVersionNumber").GetInt32()));
            var edit = await EditAsync(s, s.Clerk, form.Record);
            Assert.Equal(("Edit", editVersion), (edit.GetProperty("layoutPurpose").GetString(), LayoutId(edit)));

            // An update is validated against the edit layout; the created version never changes; values outside the layout are kept.
            if (editNumber == 2)
            {
                var missing = await PatchAsync(s, s.Clerk, form.Record, Revision(edit), new() { ["first_name"] = "Avery B" }, editVersion);
                Assert.Equal(HttpStatusCode.UnprocessableEntity, missing.Status);
                Assert.Contains("email_address", missing.Body, StringComparison.Ordinal);
            }

            var update = editNumber == 2 ? new Dictionary<string, string> { ["first_name"] = "Avery B", ["email_address"] = "avery@example.test" } : new Dictionary<string, string> { ["first_name"] = "Avery B" };
            var saved = await PatchAsync(s, s.Clerk, form.Record, Revision(edit), update, editVersion);
            Assert.True(saved.IsSuccess, form.Behavior + ": " + saved.Body);
            Assert.Equal((form.V1.ToString("D"), editVersion.ToString("D")), (CreatedVersion(form.Record), LastEdited(form.Record)));
            view = await ViewAsync(s, s.Clerk, form.Record);
            Assert.Equal((viewNumber, "Avery B", "555-0101"), (LayoutNumber(view), ValueOf(view, "first_name"), ValueOf(view, "phone_number")));
            Assert.Equal(editNumber == 2 ? "avery@example.test" : null, ValueOf(view, "email_address"));

            // Authorization and field-level visibility do not depend on the behaviour.
            Assert.Contains((await s.ProjectionAsync(s.Reader, form.Record)).Status, Denied);
            Assert.Contains((await other.ProjectionAsync(other.Clerk, form.Record)).Status, Denied);
            Assert.Contains((await EditProjectionAsync(s, s.Reader, form.Record)).Status, Denied);
            var share = await fixture.Api.PostAsync($"/api/v1/form-records/{form.Record:D}/shares", s.Token(s.Clerk), new { recipientUserId = s.Reader.AuthorityUserId, recipientTeamId = (Guid?)null, recipientRole = "Reader", fieldKeys = new[] { "first_name" } });
            Assert.True(share.IsSuccess, share.Body);
            await s.WaitGateReadyAsync(TimeSpan.FromSeconds(40));
            var readerView = await ViewAsync(s, s.Reader, form.Record);
            Assert.Equal(["first_name"], Values(readerView).Keys);
            Assert.Equal(viewNumber, LayoutNumber(readerView));
            Assert.DoesNotContain("555-0101", readerView.GetRawText(), StringComparison.Ordinal);
            Assert.Contains((await PatchAsync(s, s.Reader, form.Record, Revision(readerView), new() { ["first_name"] = "Reader edit" }, editVersion)).Status, new[] { HttpStatusCode.Forbidden, HttpStatusCode.NotFound });

            // Restore validates against the edit layout (D9-6): revision 1 has no email, which V2 requires.
            var preview = await RestorePreviewAsync(s, s.Clerk, form.Record, 1);
            Assert.True(preview.IsSuccess, preview.Body);
            Assert.Equal(editNumber, preview.Root.GetProperty("layoutVersionNumber").GetInt32());
            if (editNumber == 1)
            {
                Assert.True(preview.Root.GetProperty("canRestore").GetBoolean(), preview.Body);
                var restored = await RestoreAsync(s, s.Clerk, form.Record, 1, preview.Root, form.V1);
                Assert.True(restored.IsSuccess, restored.Body);
                Assert.Equal("Avery", ValueOf(await ViewAsync(s, s.Clerk, form.Record), "first_name"));
                Assert.Equal(form.V1.ToString("D"), LastEdited(form.Record));
            }
            else
            {
                Assert.False(preview.Root.GetProperty("canRestore").GetBoolean());
                Assert.Equal("RESTORE_VALIDATION_FAILED", preview.Root.GetProperty("blockedReasonCode").GetString());
            }
        }

        // ---- A missing or foreign edit version fails closed and is never substituted; Historical needs only the created version ----
        var historical = forms.Single(f => f.Behavior == "Historical");
        var latest = forms.Single(f => f.Behavior == "Latest");
        var hybrid = forms.Single(f => f.Behavior == "Hybrid");
        Sql($"UPDATE form_definitions SET current_form_version_id = '{historical.V2:D}' WHERE form_definition_id = '{latest.FormId:D}';"); // another form's version
        Sql($"UPDATE form_definitions SET current_form_version_id = NULL WHERE form_definition_id IN ('{hybrid.FormId:D}', '{historical.FormId:D}');");
        try
        {
            foreach (var form in new[] { latest, hybrid })
            {
                var unavailable = await EditProjectionAsync(s, s.Clerk, form.Record);
                Assert.Equal((HttpStatusCode.Conflict, "EDIT_LAYOUT_UNAVAILABLE"), (unavailable.Status, unavailable.ErrorCode));
                var before = await ViewAsync(s, s.Clerk, form.Record);
                Assert.Equal(1, LayoutNumber(before)); // viewing falls back to the created version
                Assert.Equal(JsonValueKind.Null, before.GetProperty("editLayoutVersionId").ValueKind);
                var lastEdited = LastEdited(form.Record);
                Assert.Equal("EDIT_LAYOUT_UNAVAILABLE", (await PatchAsync(s, s.Clerk, form.Record, Revision(before), new() { ["first_name"] = "Never" }, null)).ErrorCode);
                Assert.Equal("EDIT_LAYOUT_UNAVAILABLE", (await SubmitAsync(s, s.Clerk, form.Record, Revision(before), null)).ErrorCode);
                Assert.Equal("EDIT_LAYOUT_UNAVAILABLE", (await RestorePreviewAsync(s, s.Clerk, form.Record, 1)).ErrorCode);
                Assert.Equal("EDIT_LAYOUT_UNAVAILABLE", (await BeginUploadAsync(s, form, "application/pdf", null)).ErrorCode);
                await AssertUnchangedAsync(s, form.Record, before, lastEdited);
            }

            var historicalEdit = await EditAsync(s, s.Clerk, historical.Record);
            Assert.Equal(historical.V1, LayoutId(historicalEdit));
        }
        finally
        {
            Sql($"UPDATE form_definitions SET current_form_version_id = '{latest.V2:D}' WHERE form_definition_id = '{latest.FormId:D}';");
            Sql($"UPDATE form_definitions SET current_form_version_id = '{hybrid.V2:D}' WHERE form_definition_id = '{hybrid.FormId:D}';");
            Sql($"UPDATE form_definitions SET current_form_version_id = '{historical.V2:D}' WHERE form_definition_id = '{historical.FormId:D}';");
        }

        // ---- Stale layout after a newer version is published (Latest): update, Submit, restore and upload begin are refused ----
        var opened = await EditAsync(s, s.Clerk, latest.Record);
        Assert.Equal(latest.V2, LayoutId(opened));
        var openedPreview = await RestorePreviewAsync(s, s.Clerk, latest.Record, 1); // reviewed before the publish (blocked by V2 validation, but the guard runs first)
        Assert.True(openedPreview.IsSuccess, openedPreview.Body);
        await s.SaveDraftAsync(latest.FormId, StageBScenario.Layout("Latest intake", StageBScenario.Section("main", "Main",
            Element(latest.First, "First name", required: true), Element(latest.Email, "Email", required: true), Element(latest.Phone, "Phone"), DocumentElement(latest.Documents, "application/pdf"))));
        var v3 = (await s.PublishAsync(latest.FormId, "v3: phone back")).GetProperty("formVersionId").GetGuid();
        var beforeStale = await ViewAsync(s, s.Clerk, latest.Record);
        var lastEditedBefore = LastEdited(latest.Record);
        foreach (var refused in new[]
        {
            await PatchAsync(s, s.Clerk, latest.Record, Revision(opened), new() { ["first_name"] = "Stale" }, latest.V2),
            await SubmitAsync(s, s.Clerk, latest.Record, Revision(opened), latest.V2),
            await RestoreAsync(s, s.Clerk, latest.Record, 1, openedPreview.Root, latest.V2),
            await BeginUploadAsync(s, latest, "application/pdf", latest.V2),
        })
        {
            Assert.Equal((HttpStatusCode.Conflict, "EDIT_LAYOUT_CHANGED"), (refused.Status, refused.ErrorCode));
        }

        await AssertUnchangedAsync(s, latest.Record, beforeStale, lastEditedBefore);
        await CancelUploadAsync(s, await BeginUploadAsync(s, latest, "application/pdf", v3)); // the current layout is accepted
        var reloaded = await EditAsync(s, s.Clerk, latest.Record);
        Assert.Equal(v3, LayoutId(reloaded));
        var current = await PatchAsync(s, s.Clerk, latest.Record, Revision(reloaded), new() { ["first_name"] = "Reloaded" }, v3);
        Assert.True(current.IsSuccess, current.Body);
        Assert.Equal(v3.ToString("D"), LastEdited(latest.Record));

        // ---- Stale layout after the effective edit layout changes (Hybrid → Historical): the V2 edit layout is now V1 ----
        var hybridOpened = await EditAsync(s, s.Clerk, hybrid.Record);
        Assert.Equal(hybrid.V2, LayoutId(hybridOpened));
        await SetBehaviorAsync(s, hybrid.FormId, "Historical");
        var hybridBefore = await ViewAsync(s, s.Clerk, hybrid.Record);
        var hybridLastEdited = LastEdited(hybrid.Record);
        Assert.Equal("EDIT_LAYOUT_CHANGED", (await PatchAsync(s, s.Clerk, hybrid.Record, Revision(hybridOpened), new() { ["first_name"] = "Stale" }, hybrid.V2)).ErrorCode);
        Assert.Equal("EDIT_LAYOUT_CHANGED", (await SubmitAsync(s, s.Clerk, hybrid.Record, Revision(hybridOpened), hybrid.V2)).ErrorCode);
        Assert.Equal("EDIT_LAYOUT_CHANGED", (await BeginUploadAsync(s, hybrid, "text/plain", hybrid.V2)).ErrorCode);
        await AssertUnchangedAsync(s, hybrid.Record, hybridBefore, hybridLastEdited);
        await CancelUploadAsync(s, await BeginUploadAsync(s, hybrid, "text/plain", hybrid.V1)); // V1 accepts text documents again

        // ---- Compatibility exception (C-181, unchanged): a client that omits the expected layout is validated against the current edit layout ----
        var legacy = await EditAsync(s, s.Clerk, hybrid.Record);
        var legacySave = await PatchAsync(s, s.Clerk, hybrid.Record, Revision(legacy), new() { ["first_name"] = "Legacy client" }, null);
        Assert.True(legacySave.IsSuccess, legacySave.Body);
        Assert.Equal(hybrid.V1.ToString("D"), LastEdited(hybrid.Record));
    }

    // ---------------------------------------------------------------- scenario 6

    [Fact]
    public async Task ChangingTheBehaviourAfterRecordsExistIsDesignOnlyValidatedAuditedAndMigratesNothing()
    {
        var s = new StageBScenario(fixture, "c9-m3-setting");
        await s.BootstrapAsync();
        var other = new StageBScenario(fixture, "c9-m3-setting-other");
        await other.BootstrapAsync();
        var formId = await s.CreateFormAsync("Setting intake " + Guid.NewGuid().ToString("N")[..6], s.Clerk, s.Designer);
        var first = await AddFieldAsync(s, formId, "first_name", "First name", "String");
        var phone = await AddFieldAsync(s, formId, "phone_number", "Phone", "String");
        var email = await AddFieldAsync(s, formId, "email_address", "Email", "String");
        await s.SaveDraftAsync(formId, StageBScenario.Layout("Setting intake", StageBScenario.Section("main", "Main", Element(first, "First name", required: true), Element(phone, "Phone"))));
        var v1 = (await s.PublishAsync(formId, "v1")).GetProperty("formVersionId").GetGuid();
        var clerkRecords = new[]
        {
            await CreateRecordAsync(s, s.Clerk, formId, new() { ["first_name"] = "Avery", ["phone_number"] = "555-0101" }),
            await CreateRecordAsync(s, s.Clerk, formId, new() { ["first_name"] = "Blake", ["phone_number"] = "555-0102" }),
        };
        var designerRecord = await CreateRecordAsync(s, s.Designer, formId, new() { ["first_name"] = "Drew", ["phone_number"] = "555-0199" });
        await s.SaveDraftAsync(formId, StageBScenario.Layout("Setting intake", StageBScenario.Section("main", "Main", Element(first, "First name", required: true), Element(email, "Email", required: true))));
        var v2 = (await s.PublishAsync(formId, "v2")).GetProperty("formVersionId").GetGuid();

        string Snapshot(Guid record) => Sql($"SELECT created_form_version_id::text || '|' || coalesce(last_edited_form_version_id::text, '') FROM form_records WHERE form_record_id = '{record:D}';");
        var all = clerkRecords.Append(designerRecord).ToArray();
        var valuesBefore = new Dictionary<Guid, string>();
        foreach (var record in all)
        {
            var owner = record == designerRecord ? s.Designer : s.Clerk;
            valuesBefore[record] = JsonSerializer.Serialize((await ViewAsync(s, owner, record)).GetProperty("record").GetProperty("values")) + "#" + Snapshot(record);
        }

        var versionsBefore = JsonSerializer.Serialize((await VersionsAsync(s, formId)).OrderBy(v => v.Key).Select(v => Parsed(v.Value)!.ToJsonString()));
        var revision = await FormRevisionAsync(s, formId);
        string AuditCount() => Sql($"SELECT count(*) FROM audit_events WHERE tenant_id = '{s.OrganizationId:D}' AND form_definition_id = '{formId:D}' AND event_action = 'FORM_VERSION_BEHAVIOR_CHANGED';");

        // The impact preview, before any change: approximate, discoverable records only, no values.
        var impact = await fixture.Api.GetAsync($"/api/v1/form-definitions/{formId:D}/version-behavior-impact?target=Historical", s.Token(s.Designer));
        Assert.True(impact.IsSuccess, impact.Body);
        Assert.Equal((1, 1, 1), (impact.Root.GetProperty("visibleRecordCount").GetInt32(), impact.Root.GetProperty("viewLayoutChangeCount").GetInt32(), impact.Root.GetProperty("editLayoutChangeCount").GetInt32()));
        Assert.True(impact.Root.GetProperty("countsAreApproximate").GetBoolean());
        foreach (var leaked in all.Select(r => r.ToString("D")).Concat(["555-01", "Avery", "Drew"]))
        {
            Assert.DoesNotContain(leaked, impact.Body, StringComparison.OrdinalIgnoreCase);
        }

        // Design permission only; another organization; validation; a stale revision. None changes anything or writes an audit event.
        Assert.Contains((await fixture.Api.GetAsync($"/api/v1/form-definitions/{formId:D}/version-behavior-impact?target=Historical", s.Token(s.Clerk))).Status, Denied);
        Assert.Contains((await ChangeBehaviorAsync(s, formId, s.Clerk, "Historical", revision, "clerk attempt")).Status, Denied);
        Assert.Contains((await ChangeBehaviorAsync(other, formId, other.Designer, "Historical", revision, "other organization")).Status, Denied);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await ChangeBehaviorAsync(s, formId, s.Designer, "Historical", revision, "ab")).Status);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await ChangeBehaviorAsync(s, formId, s.Designer, "Historical", revision, new string('r', 501))).Status);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await ChangeBehaviorAsync(s, formId, s.Designer, "Newest", revision, "unknown behaviour")).Status);
        var stale = await ChangeBehaviorAsync(s, formId, s.Designer, "Historical", revision - 1, "stale revision");
        Assert.Equal((HttpStatusCode.Conflict, "FORM_DEFINITION_MODIFIED_CONCURRENTLY"), (stale.Status, stale.ErrorCode));
        Assert.Equal(revision, await FormRevisionAsync(s, formId));
        Assert.Equal("0", AuditCount());

        // Latest → Historical with a reason of exactly 3 characters; Historical → Hybrid with exactly 500.
        var historical = await ChangeBehaviorAsync(s, formId, s.Designer, "Historical", revision, "why");
        Assert.True(historical.IsSuccess, historical.Body);
        Assert.Equal(revision + 1, historical.Root.GetProperty("summary").GetProperty("formDefinitionRevision").GetInt64());
        Assert.Equal("1", AuditCount());
        var view = await ViewAsync(s, s.Clerk, clerkRecords[0]);
        Assert.Equal((1, v1), (LayoutNumber(view), view.GetProperty("editLayoutVersionId").GetGuid()));
        Assert.Equal(v1, LayoutId(await EditAsync(s, s.Clerk, clerkRecords[0])));
        var hybrid = await ChangeBehaviorAsync(s, formId, s.Designer, "Hybrid", revision + 1, new string('r', 500));
        Assert.True(hybrid.IsSuccess, hybrid.Body);
        Assert.Equal("2", AuditCount());
        view = await ViewAsync(s, s.Clerk, clerkRecords[0]);
        Assert.Equal((1, v2), (LayoutNumber(view), view.GetProperty("editLayoutVersionId").GetGuid()));
        Assert.Equal(v2, LayoutId(await EditAsync(s, s.Clerk, clerkRecords[0])));

        var events = Sql($"SELECT actor_user_id || '|' || event_metadata_json::text FROM audit_events WHERE tenant_id = '{s.OrganizationId:D}' AND form_definition_id = '{formId:D}' AND event_action = 'FORM_VERSION_BEHAVIOR_CHANGED' ORDER BY audit_sequence;")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(e => e.Replace(" ", string.Empty, StringComparison.Ordinal)).ToList();
        Assert.Equal(2, events.Count);
        Assert.All(events, e => Assert.StartsWith(s.Designer.AuthorityUserId.ToString("D") + "|", e, StringComparison.Ordinal));
        Assert.Contains("\"previousVersionBehavior\":\"Latest\"", events[0], StringComparison.Ordinal);
        Assert.Contains("\"versionBehavior\":\"Historical\"", events[0], StringComparison.Ordinal);
        Assert.Contains("\"previousVersionBehavior\":\"Historical\"", events[1], StringComparison.Ordinal);
        Assert.Contains("\"versionBehavior\":\"Hybrid\"", events[1], StringComparison.Ordinal);
        Assert.Equal("0", Sql($"SELECT count(*) FROM audit_events WHERE tenant_id = '{other.OrganizationId:D}' AND event_action = 'FORM_VERSION_BEHAVIOR_CHANGED';"));

        // Nothing was migrated or deleted: values, created and last-edited versions, published snapshots.
        foreach (var record in all)
        {
            var owner = record == designerRecord ? s.Designer : s.Clerk;
            Assert.Equal(valuesBefore[record], JsonSerializer.Serialize((await ViewAsync(s, owner, record)).GetProperty("record").GetProperty("values")) + "#" + Snapshot(record));
        }

        Assert.Equal(versionsBefore, JsonSerializer.Serialize((await VersionsAsync(s, formId)).OrderBy(v => v.Key).Select(v => Parsed(v.Value)!.ToJsonString())));
    }
}
