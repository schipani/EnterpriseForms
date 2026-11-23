using System.Net;
using System.Text.Json;
using EnterpriseForms.SecurityTests.Harness;
using EnterpriseForms.StageBTests.Harness;
using Xunit;

namespace EnterpriseForms.StageBTests.Tests;

/// <summary>
/// Phase C9 M1 on the real stack (CLAUDE.md §3; D9-1…D9-8). For Historical, Latest and Hybrid: the record page and the
/// editor get the specified layouts; update, submit, restore, documents/uploads and child records validate against the
/// edit layout; stable field identities and values outside the layout are kept; a stale edit layout is refused (409
/// EDIT_LAYOUT_CHANGED); a missing edit layout fails closed (409 EDIT_LAYOUT_UNAVAILABLE) and is never substituted. The
/// setting is design-permission-only, optimistic, audited and changes no data; its impact preview counts only records the
/// caller can discover.
/// </summary>
[Collection(StageBStackCollectionDefinition.Name)]
public sealed class FormVersionBehaviorTests(StageBFixture fixture)
{
    private static readonly HttpStatusCode[] Denied = [HttpStatusCode.Forbidden, HttpStatusCode.NotFound];

    private sealed record IntakeForm(Guid FormId, Guid FullName, Guid Phone, Guid Alias, Guid V1, Guid V2);

    private Task<HttpResult> ChangeBehaviorAsync(StageBScenario s, Guid formId, TestUser actor, string behavior, long expectedRevision, string reason = "testing record layouts") =>
        fixture.Api.SendAsync(HttpMethod.Put, $"/api/v1/form-definitions/{formId:D}/version-behavior", s.Token(actor), new { versionBehavior = behavior, expectedFormDefinitionRevision = expectedRevision, changeReason = reason }, null);

    private async Task SetBehaviorAsync(StageBScenario s, Guid formId, string behavior)
    {
        var revision = (await s.GetFormAsync(formId)).GetProperty("summary").GetProperty("formDefinitionRevision").GetInt64();
        var changed = await ChangeBehaviorAsync(s, formId, s.Designer, behavior, revision);
        Assert.True(changed.IsSuccess, changed.Body);
        Assert.Equal(behavior, changed.Root.GetProperty("summary").GetProperty("versionBehavior").GetString());
    }

    private static async Task<JsonElement> ViewAsync(StageBScenario s, TestUser user, Guid record) => (await s.WaitProjectionAsync(user, record)).Root.Clone();

    private async Task<HttpResult> EditProjectionAsync(StageBScenario s, TestUser user, Guid record)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        HttpResult last;
        do
        {
            last = await fixture.Api.GetAsync($"/api/v1/form-records/{record:D}/projection?purpose=edit", s.Token(user));
            if (last.Status != HttpStatusCode.ServiceUnavailable)
            {
                return last;
            }

            await Task.Delay(250);
        }
        while (DateTime.UtcNow < deadline);
        return last;
    }

    private async Task<JsonElement> EditAsync(StageBScenario s, TestUser user, Guid record)
    {
        var result = await EditProjectionAsync(s, user, record);
        Assert.True(result.IsSuccess, "edit projection failed: " + result.Body);
        return result.Root.Clone();
    }

    private static long Revision(JsonElement projection) => projection.GetProperty("record").GetProperty("contentRevision").GetInt64();

    private static int LayoutNumber(JsonElement projection) => projection.GetProperty("layoutVersion").GetProperty("versionNumber").GetInt32();

    private static Guid LayoutId(JsonElement projection) => projection.GetProperty("layoutVersion").GetProperty("formVersionId").GetGuid();

    private static string? ValueOf(JsonElement projection, string fieldKey) =>
        projection.GetProperty("record").GetProperty("values").EnumerateArray().Where(v => v.GetProperty("fieldKey").GetString() == fieldKey).Select(v => v.GetProperty("stringValue").GetString()).FirstOrDefault();

    private Task<HttpResult> PatchAsync(StageBScenario s, TestUser user, Guid record, long revision, Dictionary<string, string> values, Guid? expectedLayout = null) =>
        fixture.Api.PatchAsync($"/api/v1/form-records/{record:D}", s.Token(user), new { expectedContentRevision = revision, values, expectedLayoutVersionId = expectedLayout });

    private string LastEdited(Guid record) => fixture.Stack.AttackerSql($"SELECT coalesce(last_edited_form_version_id::text, '') FROM form_records WHERE form_record_id = '{record:D}';").Trim();

    private async Task<Guid> CreateRecordAsync(StageBScenario s, TestUser user, Guid formId, Dictionary<string, string> values)
    {
        var created = await fixture.Api.PostAsync($"/api/v1/form-definitions/{formId:D}/records", s.Token(user), new { values, ownerTeamId = (Guid?)null });
        Assert.True(created.Status == HttpStatusCode.Created, "create record failed: " + created.Body);
        var id = created.Root.GetProperty("formRecordId").GetGuid();
        await s.WaitProjectionAsync(user, id);
        return id;
    }

    /// <summary>v1: full name (required) + phone; v2: full name + alias (required), phone removed from the layout.</summary>
    private static async Task<IntakeForm> PublishIntakeV1Async(StageBScenario s, params TestUser[] users)
    {
        var formId = await s.CreateFormAsync("Intake " + Guid.NewGuid().ToString("N")[..6], users);
        var fullName = await s.AddFieldAsync(formId, "full_name", "Full name", "String");
        var phone = await s.AddFieldAsync(formId, "phone_number", "Phone number", "String");
        var alias = await s.AddFieldAsync(formId, "alias_name", "Alias", "String");
        await s.SaveDraftAsync(formId, StageBScenario.Layout("Intake", StageBScenario.Section("main", "Main",
            StageBScenario.Field(fullName, "text", "Full name", 4, required: true),
            StageBScenario.Field(phone, "text", "Phone number", 4))));
        var v1 = (await s.PublishAsync(formId, "intake v1")).GetProperty("formVersionId").GetGuid();
        return new IntakeForm(formId, fullName, phone, alias, v1, Guid.Empty);
    }

    private static async Task<IntakeForm> PublishIntakeV2Async(StageBScenario s, IntakeForm form)
    {
        await s.SaveDraftAsync(form.FormId, StageBScenario.Layout("Intake", StageBScenario.Section("main", "Main",
            StageBScenario.Field(form.FullName, "text", "Full name", 4, required: true),
            StageBScenario.Field(form.Alias, "text", "Alias", 4, required: true))));
        var v2 = (await s.PublishAsync(form.FormId, "intake v2: alias required, phone removed")).GetProperty("formVersionId").GetGuid();
        return form with { V2 = v2 };
    }

    [Fact]
    public async Task EachBehaviourViewsAndEditsWithItsLayoutForUpdateSubmitAndRestore()
    {
        var s = new StageBScenario(fixture, "c9-behaviour");
        await s.BootstrapAsync();
        var form = await PublishIntakeV1Async(s, s.Clerk);
        var r1 = await CreateRecordAsync(s, s.Clerk, form.FormId, new() { ["full_name"] = "Avery", ["phone_number"] = "555-0101" });
        var r2 = await CreateRecordAsync(s, s.Clerk, form.FormId, new() { ["full_name"] = "Blake", ["phone_number"] = "555-0102" });
        var r3 = await CreateRecordAsync(s, s.Clerk, form.FormId, new() { ["full_name"] = "Casey", ["phone_number"] = "555-0103" });
        form = await PublishIntakeV2Async(s, form);
        Assert.Equal("Latest", (await s.GetFormAsync(form.FormId)).GetProperty("summary").GetProperty("versionBehavior").GetString());

        // ---- Latest (default): view and edit with v2 ----
        var view = await ViewAsync(s, s.Clerk, r1);
        Assert.Equal(2, LayoutNumber(view));
        Assert.Equal("Latest", view.GetProperty("versionBehavior").GetString());
        Assert.Equal("View", view.GetProperty("layoutPurpose").GetString());
        Assert.Equal(1, view.GetProperty("createdFormVersionNumber").GetInt32());
        Assert.Equal(form.V2, view.GetProperty("editLayoutVersionId").GetGuid());
        var edit = await EditAsync(s, s.Clerk, r1);
        Assert.Equal(2, LayoutNumber(edit));
        Assert.Equal("Edit", edit.GetProperty("layoutPurpose").GetString());
        var missingAlias = await PatchAsync(s, s.Clerk, r1, Revision(edit), new() { ["full_name"] = "Avery B" }, form.V2);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, missingAlias.Status);
        Assert.Contains("alias_name", missingAlias.Body, StringComparison.Ordinal);
        var latestSave = await PatchAsync(s, s.Clerk, r1, Revision(edit), new() { ["full_name"] = "Avery B", ["alias_name"] = "Ave" }, form.V2);
        Assert.True(latestSave.IsSuccess, latestSave.Body);
        view = await ViewAsync(s, s.Clerk, r1);
        Assert.Equal("555-0101", ValueOf(view, "phone_number")); // removed from the layout, kept unchanged (stable field identity)
        Assert.Equal("Ave", ValueOf(view, "alias_name"));
        Assert.Equal(form.V2.ToString("D"), LastEdited(r1));
        var latestSubmit = await fixture.Api.PostAsync($"/api/v1/form-records/{r3:D}/submit", s.Token(s.Clerk), new { expectedContentRevision = Revision(await ViewAsync(s, s.Clerk, r3)), expectedLayoutVersionId = form.V2 });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, latestSubmit.Status); // v2 requires the alias r3 does not have

        // ---- Historical: view and edit with the created version (v1) ----
        await SetBehaviorAsync(s, form.FormId, "Historical");
        view = await ViewAsync(s, s.Clerk, r2);
        Assert.Equal(1, LayoutNumber(view));
        Assert.Equal(form.V1, view.GetProperty("editLayoutVersionId").GetGuid());
        edit = await EditAsync(s, s.Clerk, r2);
        Assert.Equal(1, LayoutNumber(edit));
        var stale = await PatchAsync(s, s.Clerk, r2, Revision(edit), new() { ["full_name"] = "Blake B" }, form.V2); // opened before the change
        Assert.Equal(HttpStatusCode.Conflict, stale.Status);
        Assert.Equal("EDIT_LAYOUT_CHANGED", stale.ErrorCode);
        Assert.Equal("Blake", ValueOf(await ViewAsync(s, s.Clerk, r2), "full_name"));
        var historicalSave = await PatchAsync(s, s.Clerk, r2, Revision(edit), new() { ["full_name"] = "Blake B" }, form.V1); // v1 has no alias field
        Assert.True(historicalSave.IsSuccess, historicalSave.Body);
        Assert.Equal(form.V1.ToString("D"), LastEdited(r2));
        var staleSubmit = await fixture.Api.PostAsync($"/api/v1/form-records/{r2:D}/submit", s.Token(s.Clerk), new { expectedContentRevision = Revision(await ViewAsync(s, s.Clerk, r2)), expectedLayoutVersionId = form.V2 });
        Assert.Equal("EDIT_LAYOUT_CHANGED", staleSubmit.ErrorCode);
        var historicalSubmit = await fixture.Api.PostAsync($"/api/v1/form-records/{r2:D}/submit", s.Token(s.Clerk), new { expectedContentRevision = Revision(await ViewAsync(s, s.Clerk, r2)), expectedLayoutVersionId = form.V1 });
        Assert.True(historicalSubmit.IsSuccess, historicalSubmit.Body);
        var r3Edit = await PatchAsync(s, s.Clerk, r3, Revision(await EditAsync(s, s.Clerk, r3)), new() { ["full_name"] = "Casey B" }, form.V1);
        Assert.True(r3Edit.IsSuccess, r3Edit.Body); // r3 now has revision 2; revision 1 is restorable below

        // ---- Hybrid: view with v1, edit with v2 ----
        await SetBehaviorAsync(s, form.FormId, "Hybrid");
        view = await ViewAsync(s, s.Clerk, r3);
        Assert.Equal(1, LayoutNumber(view));
        Assert.Equal(form.V2, view.GetProperty("editLayoutVersionId").GetGuid());
        Assert.Equal(2, view.GetProperty("editLayoutVersionNumber").GetInt32());
        edit = await EditAsync(s, s.Clerk, r3);
        Assert.Equal(2, LayoutNumber(edit));
        Assert.Equal(form.V2, LayoutId(edit));
        var hybridMissing = await PatchAsync(s, s.Clerk, r3, Revision(edit), new() { ["full_name"] = "Casey C" }, form.V2);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, hybridMissing.Status);
        var hybridPreview = await fixture.Api.GetAsync($"/api/v1/form-records/{r3:D}/versions/1/restore-preview", s.Token(s.Clerk));
        Assert.True(hybridPreview.IsSuccess, hybridPreview.Body);
        Assert.Equal(2, hybridPreview.Root.GetProperty("layoutVersionNumber").GetInt32());
        Assert.False(hybridPreview.Root.GetProperty("canRestore").GetBoolean()); // the edit layout (v2) requires the alias
        Assert.Equal("RESTORE_VALIDATION_FAILED", hybridPreview.Root.GetProperty("blockedReasonCode").GetString());
        var hybridSave = await PatchAsync(s, s.Clerk, r3, Revision(edit), new() { ["full_name"] = "Casey C", ["alias_name"] = "Cee" }, form.V2);
        Assert.True(hybridSave.IsSuccess, hybridSave.Body);
        view = await ViewAsync(s, s.Clerk, r3);
        Assert.Equal(1, LayoutNumber(view)); // still shown with the created version
        Assert.Equal("Cee", ValueOf(view, "alias_name")); // authorized values are released whatever the layout
        Assert.Equal("555-0103", ValueOf(view, "phone_number"));

        // ---- Restore validates against the edit layout (D9-6): Historical uses the created version ----
        await SetBehaviorAsync(s, form.FormId, "Historical");
        var preview = await fixture.Api.GetAsync($"/api/v1/form-records/{r3:D}/versions/1/restore-preview", s.Token(s.Clerk));
        Assert.True(preview.IsSuccess, preview.Body);
        Assert.Equal(1, preview.Root.GetProperty("layoutVersionNumber").GetInt32());
        Assert.True(preview.Root.GetProperty("canRestore").GetBoolean(), preview.Body);
        var current = preview.Root.GetProperty("currentContentRevision").GetInt64();
        var source = preview.Root.GetProperty("sourceOperationId").GetGuid();
        var staleRestore = await fixture.Api.PostAsync($"/api/v1/form-records/{r3:D}/versions/1/restore", s.Token(s.Clerk), new { expectedCurrentContentRevision = current, sourceOperationId = source, restoreReason = "back to the original", expectedLayoutVersionId = form.V2 });
        Assert.Equal("EDIT_LAYOUT_CHANGED", staleRestore.ErrorCode);
        var restored = await fixture.Api.PostAsync($"/api/v1/form-records/{r3:D}/versions/1/restore", s.Token(s.Clerk), new { expectedCurrentContentRevision = current, sourceOperationId = source, restoreReason = "back to the original", expectedLayoutVersionId = form.V1 });
        Assert.True(restored.IsSuccess, restored.Body);
        view = await ViewAsync(s, s.Clerk, r3);
        Assert.Equal("Casey", ValueOf(view, "full_name"));
        Assert.Equal("555-0103", ValueOf(view, "phone_number"));

        // ---- A missing edit layout fails closed and is never substituted ----
        await SetBehaviorAsync(s, form.FormId, "Latest");
        fixture.Stack.AttackerSql($"UPDATE form_definitions SET current_form_version_id = NULL WHERE form_definition_id = '{form.FormId:D}';");
        try
        {
            var unavailable = await EditProjectionAsync(s, s.Clerk, r1);
            Assert.Equal(HttpStatusCode.Conflict, unavailable.Status);
            Assert.Equal("EDIT_LAYOUT_UNAVAILABLE", unavailable.ErrorCode);
            var refused = await PatchAsync(s, s.Clerk, r1, Revision(await ViewAsync(s, s.Clerk, r1)), new() { ["full_name"] = "Avery C", ["alias_name"] = "Ave" });
            Assert.Equal("EDIT_LAYOUT_UNAVAILABLE", refused.ErrorCode);
            view = await ViewAsync(s, s.Clerk, r1); // the record can still be read, with the created version
            Assert.Equal(1, LayoutNumber(view));
            Assert.Equal(JsonValueKind.Null, view.GetProperty("editLayoutVersionId").ValueKind);
            Assert.Equal("Avery B", ValueOf(view, "full_name"));
        }
        finally
        {
            fixture.Stack.AttackerSql($"UPDATE form_definitions SET current_form_version_id = '{form.V2:D}' WHERE form_definition_id = '{form.FormId:D}';");
        }

        // The three behaviour changes above never created a version or touched a value.
        var versions = (await fixture.Api.GetAsync($"/api/v1/form-definitions/{form.FormId:D}/versions", s.Token(s.Designer))).Root.EnumerateArray().Select(v => v.GetProperty("versionNumber").GetInt32()).OrderBy(n => n).ToList();
        Assert.Equal([1, 2], versions);
    }

    [Fact]
    public async Task AStaleEditLayoutIsRefusedAfterANewerVersionIsPublishedForOwnersAndSharedEditors()
    {
        var s = new StageBScenario(fixture, "c9-stale-layout");
        await s.BootstrapAsync();
        var form = await PublishIntakeV1Async(s, s.Clerk);
        var record = await CreateRecordAsync(s, s.Clerk, form.FormId, new() { ["full_name"] = "Avery", ["phone_number"] = "555-0101" });
        var ownerEdit = await EditAsync(s, s.Clerk, record); // the owner opens the editor on v1 (Latest)
        Assert.Equal(form.V1, LayoutId(ownerEdit));
        var shared = await fixture.Api.PostAsync($"/api/v1/form-records/{record:D}/shares", s.Token(s.Clerk), new { recipientUserId = s.Designer.AuthorityUserId, recipientTeamId = (Guid?)null, recipientRole = "Contributor", fieldKeys = new[] { "full_name" } });
        Assert.True(shared.IsSuccess, shared.Body);
        await s.WaitGateReadyAsync(TimeSpan.FromSeconds(40));
        var contributorWrites = await fixture.Authority.OperatorAsync(HttpMethod.Post, "/internal/v1/operator/testing/contributor-writes", new { enabled = true });
        Assert.True(contributorWrites.IsSuccess, contributorWrites.Body);
        try
        {
            var sharedEdit = await EditAsync(s, s.Designer, record); // the share editor opens the editor on v1 too
            Assert.Equal(form.V1, LayoutId(sharedEdit));
            Assert.False(sharedEdit.GetProperty("permissions").GetProperty("isCompleteProjection").GetBoolean());

            // v2 is published while both editors are open; both saves were prepared against v1.
            form = await PublishIntakeV2Async(s, form);
            var ownerStale = await PatchAsync(s, s.Clerk, record, Revision(ownerEdit), new() { ["full_name"] = "Avery B", ["alias_name"] = "Ave" }, form.V1);
            Assert.Equal(HttpStatusCode.Conflict, ownerStale.Status);
            Assert.Equal("EDIT_LAYOUT_CHANGED", ownerStale.ErrorCode);
            var sharedStale = await PatchAsync(s, s.Designer, record, Revision(sharedEdit), new() { ["full_name"] = "Avery S" }, form.V1);
            Assert.Equal(HttpStatusCode.Conflict, sharedStale.Status);
            Assert.Equal("EDIT_LAYOUT_CHANGED", sharedStale.ErrorCode);
            Assert.Equal("Avery", ValueOf(await ViewAsync(s, s.Clerk, record), "full_name")); // nothing was written

            // After reloading onto v2 the share editor's save goes through, validated against v2.
            sharedEdit = await EditAsync(s, s.Designer, record);
            Assert.Equal(form.V2, LayoutId(sharedEdit));
            var sharedSaved = await PatchAsync(s, s.Designer, record, Revision(sharedEdit), new() { ["full_name"] = "Avery S" }, form.V2);
            Assert.True(sharedSaved.IsSuccess, sharedSaved.Body);
            Assert.Equal(form.V2.ToString("D"), LastEdited(record));

            // The owner reloads too: a submit prepared against the outdated v1 is also refused.
            var staleSubmit = await fixture.Api.PostAsync($"/api/v1/form-records/{record:D}/submit", s.Token(s.Clerk), new { expectedContentRevision = Revision(await ViewAsync(s, s.Clerk, record)), expectedLayoutVersionId = form.V1 });
            Assert.Equal("EDIT_LAYOUT_CHANGED", staleSubmit.ErrorCode);
        }
        finally
        {
            Assert.True((await fixture.Authority.OperatorAsync(HttpMethod.Post, "/internal/v1/operator/testing/contributor-writes", new { enabled = (bool?)null })).IsSuccess);
        }
    }

    [Fact]
    public async Task DocumentsUploadsAndChildRecordsUseTheirFormsEditLayout()
    {
        var s = new StageBScenario(fixture, "c9-documents");
        await s.BootstrapAsync();
        await s.BuildAccusedWorkspaceAsync(); // Accused v1 accepts text/plain documents
        var accused = await s.CreateAccusedAsync(s.Clerk);
        var address = await s.CreateAddressAsync(s.Clerk, accused, "1 Front Street");

        // Accused v2 accepts only PDF and octet-stream documents.
        var v1Layout = s.AccusedLayout();
        var v2Layout = v1Layout.Replace("\"text/plain\",", string.Empty, StringComparison.Ordinal);
        Assert.NotEqual(v1Layout, v2Layout);
        await s.SaveDraftAsync(s.AccusedFormId, v2Layout);
        await s.PublishAsync(s.AccusedFormId, "documents: PDF only");

        var bytes = StageBScenario.SyntheticBytes(512, 9);
        Task<HttpResult> BeginAsync() => fixture.Api.PostAsync("/api/v1/upload-sessions", s.Token(s.Clerk), new { formRecordId = accused, fieldDefinitionId = s.AccusedFields["case_documents"], originalFileName = "note.txt", contentType = "text/plain", sizeBytes = bytes.LongLength, replacesFileAttachmentId = (Guid?)null });

        // Latest: the v2 upload policy applies.
        var latest = await BeginAsync();
        Assert.Equal(HttpStatusCode.UnprocessableEntity, latest.Status);
        Assert.Equal("CONTENT_TYPE_NOT_ALLOWED", latest.ErrorCode);

        // Historical: the record's created version (v1) applies to the upload and its commit.
        await SetBehaviorAsync(s, s.AccusedFormId, "Historical");

        // D9-5 for uploads: the page's expected edit layout travels with upload begin; a stale one is refused, the current one accepted.
        var accusedView = await ViewAsync(s, s.Clerk, accused);
        var v1Id = accusedView.GetProperty("editLayoutVersionId").GetGuid();
        var v2Id = (await s.GetFormAsync(s.AccusedFormId)).GetProperty("summary").GetProperty("currentFormVersionId").GetGuid();
        Assert.NotEqual(v1Id, v2Id);
        Task<HttpResult> BeginExpectingAsync(Guid expected) => fixture.Api.PostAsync("/api/v1/upload-sessions", s.Token(s.Clerk), new { formRecordId = accused, fieldDefinitionId = s.AccusedFields["case_documents"], originalFileName = "note.txt", contentType = "text/plain", sizeBytes = bytes.LongLength, replacesFileAttachmentId = (Guid?)null, expectedLayoutVersionId = expected });
        var staleUpload = await BeginExpectingAsync(v2Id); // the page loaded while the form was Latest (v2)
        Assert.Equal(HttpStatusCode.Conflict, staleUpload.Status);
        Assert.Equal("EDIT_LAYOUT_CHANGED", staleUpload.ErrorCode);
        var currentUpload = await BeginExpectingAsync(v1Id);
        Assert.Equal(HttpStatusCode.Created, currentUpload.Status);
        Assert.True((await fixture.Api.SendAsync(HttpMethod.Delete, $"/api/v1/upload-sessions/{currentUpload.Root.GetProperty("uploadSessionId").GetGuid():D}", s.Token(s.Clerk), null, null)).IsSuccess);
        var committed = await s.UploadAsync(s.Clerk, accused, "note.txt", "text/plain", bytes);
        Assert.Equal(JsonValueKind.Object, committed.ValueKind);
        var documents = (await ViewAsync(s, s.Clerk, accused)).GetProperty("documents").EnumerateArray().Select(d => d.GetProperty("originalFileName").GetString()).ToList();
        Assert.Contains("note.txt", documents);

        // Child records: created and listed through the parent's edit and view layouts; the child keeps its own form's behaviour.
        var second = await s.CreateAddressAsync(s.Clerk, accused, "2 King Street");
        var children = await s.ChildrenAsync(s.Clerk, accused);
        Assert.True(children.IsSuccess, children.Body);
        Assert.Contains(second.ToString("D"), children.Body, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(address.ToString("D"), children.Body, StringComparison.OrdinalIgnoreCase);

        var postal = await s.AddFieldAsync(s.AddressFormId, "postal_code", "Postal code", "String");
        await s.SaveDraftAsync(s.AddressFormId, StageBScenario.Layout("AccusedAddress", StageBScenario.Section("main", "Address",
            StageBScenario.Field(s.AddressFields["street_address"], "text", "Street address", 4, required: true),
            StageBScenario.Field(s.AddressFields["city_name"], "text", "City", 2),
            StageBScenario.Field(s.AddressFields["address_kind"], "select", "Address kind", 2),
            StageBScenario.Field(postal, "text", "Postal code", 4, required: true))));
        await s.PublishAsync(s.AddressFormId, "address v2: postal code required");
        var child = await EditAsync(s, s.Clerk, address);
        Assert.Equal(2, LayoutNumber(child));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await PatchAsync(s, s.Clerk, address, Revision(child), new() { ["city_name"] = "Ottawa" }, LayoutId(child))).Status);
        await SetBehaviorAsync(s, s.AddressFormId, "Historical");
        child = await EditAsync(s, s.Clerk, address);
        Assert.Equal(1, LayoutNumber(child));
        var childSave = await PatchAsync(s, s.Clerk, address, Revision(child), new() { ["city_name"] = "Ottawa" }, LayoutId(child));
        Assert.True(childSave.IsSuccess, childSave.Body);

        // A parent without an edit layout refuses uploads and new children (fail closed); reading still works.
        await SetBehaviorAsync(s, s.AccusedFormId, "Latest");
        var current = fixture.Stack.AttackerSql($"SELECT current_form_version_id FROM form_definitions WHERE form_definition_id = '{s.AccusedFormId:D}';").Trim();
        fixture.Stack.AttackerSql($"UPDATE form_definitions SET current_form_version_id = NULL WHERE form_definition_id = '{s.AccusedFormId:D}';");
        try
        {
            var pdf = await fixture.Api.PostAsync("/api/v1/upload-sessions", s.Token(s.Clerk), new { formRecordId = accused, fieldDefinitionId = s.AccusedFields["case_documents"], originalFileName = "a.pdf", contentType = "application/pdf", sizeBytes = bytes.LongLength, replacesFileAttachmentId = (Guid?)null });
            Assert.Equal("EDIT_LAYOUT_UNAVAILABLE", pdf.ErrorCode);
            var newChild = await fixture.Api.PostAsync($"/api/v1/form-records/{accused:D}/related", s.Token(s.Clerk), new { formRelationshipDefinitionId = s.RelationshipId, values = new Dictionary<string, string> { ["street_address"] = "3 Bay Street", ["city_name"] = "Toronto", ["address_kind"] = "home" } });
            Assert.Equal("EDIT_LAYOUT_UNAVAILABLE", newChild.ErrorCode);
            Assert.True((await s.ProjectionAsync(s.Clerk, accused)).IsSuccess);
        }
        finally
        {
            fixture.Stack.AttackerSql($"UPDATE form_definitions SET current_form_version_id = '{current}' WHERE form_definition_id = '{s.AccusedFormId:D}';");
        }
    }

    [Fact]
    public async Task TheSettingIsDesignOnlyOptimisticAuditedChangesNoDataAndCountsOnlyDiscoverableRecords()
    {
        var s = new StageBScenario(fixture, "c9-setting");
        await s.BootstrapAsync();
        var form = await PublishIntakeV1Async(s, s.Clerk, s.Designer);
        var clerkRecord = await CreateRecordAsync(s, s.Clerk, form.FormId, new() { ["full_name"] = "Avery", ["phone_number"] = "555-0101" });
        var designerRecord = await CreateRecordAsync(s, s.Designer, form.FormId, new() { ["full_name"] = "Drew", ["phone_number"] = "555-0104" });
        form = await PublishIntakeV2Async(s, form);
        var valuesBefore = JsonSerializer.Serialize((await ViewAsync(s, s.Clerk, clerkRecord)).GetProperty("record").GetProperty("values"));
        var summary = (await s.GetFormAsync(form.FormId)).GetProperty("summary");
        var revision = summary.GetProperty("formDefinitionRevision").GetInt64();

        // Only a designer of the form; never another organization.
        Assert.Contains((await ChangeBehaviorAsync(s, form.FormId, s.Clerk, "Historical", revision)).Status, Denied);
        Assert.Contains((await fixture.Api.GetAsync($"/api/v1/form-definitions/{form.FormId:D}/version-behavior-impact?target=Historical", s.Token(s.Clerk))).Status, Denied);
        var other = new StageBScenario(fixture, "c9-setting-other");
        await other.BootstrapAsync();
        Assert.Contains((await ChangeBehaviorAsync(other, form.FormId, other.Designer, "Historical", revision)).Status, Denied);

        // Validation and concurrency change nothing.
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await ChangeBehaviorAsync(s, form.FormId, s.Designer, "Newest", revision)).Status);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await ChangeBehaviorAsync(s, form.FormId, s.Designer, "Historical", revision, "ab")).Status);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await ChangeBehaviorAsync(s, form.FormId, s.Designer, "Historical", revision, new string('x', 501))).Status);
        var stale = await ChangeBehaviorAsync(s, form.FormId, s.Designer, "Historical", revision + 3);
        Assert.Equal(HttpStatusCode.Conflict, stale.Status);
        Assert.Equal("FORM_DEFINITION_MODIFIED_CONCURRENTLY", stale.ErrorCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await fixture.Api.GetAsync($"/api/v1/form-definitions/{form.FormId:D}/version-behavior-impact?target=Newest", s.Token(s.Designer))).Status);

        // Impact: the designer sees only the record they can discover (their own), never the clerk's.
        var impact = await fixture.Api.GetAsync($"/api/v1/form-definitions/{form.FormId:D}/version-behavior-impact?target=Historical", s.Token(s.Designer));
        Assert.True(impact.IsSuccess, impact.Body);
        Assert.Equal(1, impact.Root.GetProperty("visibleRecordCount").GetInt32());
        Assert.Equal(1, impact.Root.GetProperty("viewLayoutChangeCount").GetInt32());
        Assert.Equal(1, impact.Root.GetProperty("editLayoutChangeCount").GetInt32());
        Assert.True(impact.Root.GetProperty("countsAreApproximate").GetBoolean());
        Assert.DoesNotContain(clerkRecord.ToString("D"), impact.Body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(designerRecord.ToString("D"), impact.Body, StringComparison.OrdinalIgnoreCase);

        var changed = await ChangeBehaviorAsync(s, form.FormId, s.Designer, "Historical", revision, "  keep records on their version  ");
        Assert.True(changed.IsSuccess, changed.Body);
        Assert.Equal(revision + 1, changed.Root.GetProperty("summary").GetProperty("formDefinitionRevision").GetInt64());
        Assert.Equal(form.V2, changed.Root.GetProperty("summary").GetProperty("currentFormVersionId").GetGuid());
        var resent = await ChangeBehaviorAsync(s, form.FormId, s.Designer, "Historical", revision);
        Assert.True(resent.IsSuccess, resent.Body);
        Assert.Equal(revision + 1, resent.Root.GetProperty("summary").GetProperty("formDefinitionRevision").GetInt64());
        Assert.Equal(HttpStatusCode.Conflict, (await ChangeBehaviorAsync(s, form.FormId, s.Designer, "Hybrid", revision)).Status);

        // No data changed: values, versions; only the layout used to show the record.
        var after = await ViewAsync(s, s.Clerk, clerkRecord);
        Assert.Equal(valuesBefore, JsonSerializer.Serialize(after.GetProperty("record").GetProperty("values")));
        Assert.Equal(1, LayoutNumber(after));
        Assert.Equal("Historical", after.GetProperty("versionBehavior").GetString());

        // One audit event, by the designer, with both behaviours; refused attempts wrote none.
        var audit = fixture.Stack.AttackerSql($"SELECT form_definition_id || '|' || actor_user_id || '|' || event_metadata_json::text FROM audit_events WHERE tenant_id = '{s.OrganizationId:D}' AND event_action = 'FORM_VERSION_BEHAVIOR_CHANGED' ORDER BY audit_sequence;")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var single = Assert.Single(audit);
        Assert.StartsWith($"{form.FormId:D}|{s.Designer.AuthorityUserId:D}|", single, StringComparison.Ordinal);
        Assert.Contains("\"previousVersionBehavior\": \"Latest\"", single, StringComparison.Ordinal);
        Assert.Contains("\"versionBehavior\": \"Historical\"", single, StringComparison.Ordinal);
        Assert.Contains("\"changeReason\": \"keep records on their version\"", single, StringComparison.Ordinal);
        Assert.Equal("0", fixture.Stack.AttackerSql($"SELECT count(*) FROM audit_events WHERE tenant_id = '{other.OrganizationId:D}' AND event_action = 'FORM_VERSION_BEHAVIOR_CHANGED';").Trim());
    }
}
