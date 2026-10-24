using System.Net;
using System.Text.Json;
using EnterpriseForms.SecurityTests.Harness;
using EnterpriseForms.StageBTests.Harness;
using Xunit;

namespace EnterpriseForms.StageBTests.Tests;

/// <summary>
/// Phase C6 M3 — restore an earlier version as a new revision, on the real stack (PostgreSQL ×2, SpiceDB, Vault Transit,
/// Authority, API, worker): owner-only, Draft only, reason required and audited, history preserved and extended, documents and
/// related records untouched, all-or-nothing validation against the current form, current protection policy, no resurrected
/// share coverage (identity rule I-R), stale heads refused, idempotent retries, and fail-closed on tampered or missing history.
/// </summary>
[Collection(StageBStackCollectionDefinition.Name)]
public sealed class RecordRestoreTests(StageBFixture fixture)
{
    private async Task<(StageBScenario S, Guid Record)> AccusedAsync(string name, Dictionary<string, string>? values = null)
    {
        var s = new StageBScenario(fixture, name);
        await s.BootstrapAsync();
        await s.BuildAccusedWorkspaceAsync();
        var record = await s.CreateAccusedAsync(s.Clerk, values);
        await s.WaitProjectionAsync(s.Clerk, record);
        return (s, record);
    }

    private static long Revision(JsonElement projection) => projection.GetProperty("record").GetProperty("contentRevision").GetInt64();

    private static Dictionary<string, string> Values(JsonElement projection) =>
        projection.GetProperty("record").GetProperty("values").EnumerateArray().Where(v => !v.TryGetProperty("formRecordGroupId", out var g) || g.ValueKind != JsonValueKind.String)
            .ToDictionary(v => v.GetProperty("fieldKey").GetString()!, v => v.GetProperty("stringValue").GetString()!);

    private static async Task<JsonElement> ViewAsync(StageBScenario s, TestUser user, Guid record) => (await s.WaitProjectionAsync(user, record)).Root.Clone();

    private async Task<JsonElement> EditAsync(StageBScenario s, Guid record, Dictionary<string, string> values, object[]? groups = null)
    {
        var view = await ViewAsync(s, s.Clerk, record);
        var edit = await fixture.Api.PatchAsync($"/api/v1/form-records/{record:D}", s.Token(s.Clerk), new { expectedContentRevision = Revision(view), values, groups });
        Assert.True(edit.IsSuccess, edit.Body);
        return await ViewAsync(s, s.Clerk, record);
    }

    private async Task StepAsync(StageBScenario s, Guid record, string transition, string? reason = null)
    {
        var view = await ViewAsync(s, s.Clerk, record);
        var result = await fixture.Api.PostAsync($"/api/v1/form-records/{record:D}/{transition}", s.Token(s.Clerk), new { expectedContentRevision = Revision(view), expectedStatus = view.GetProperty("lifecycle").GetProperty("status").GetString(), reason });
        Assert.True(result.IsSuccess, transition + ": " + result.Body);
        await ViewAsync(s, s.Clerk, record);
    }

    private Task<HttpResult> PreviewAsync(StageBScenario s, TestUser user, Guid record, long version) =>
        fixture.Api.GetAsync($"/api/v1/form-records/{record:D}/versions/{version}/restore-preview", s.Token(user));

    private Task<HttpResult> RestoreAsync(StageBScenario s, TestUser user, Guid record, long version, long expectedCurrent, Guid sourceOperation, string? reason = "restoring for the test", bool acknowledge = false, string? key = null) =>
        fixture.Api.PostAsync($"/api/v1/form-records/{record:D}/versions/{version}/restore", s.Token(user), new { expectedCurrentContentRevision = expectedCurrent, sourceOperationId = sourceOperation, restoreReason = reason, acknowledgePlaintext = acknowledge }, key);

    private Guid OperationOf(Guid record, long revision) =>
        Guid.Parse(fixture.Stack.AuthorityObserverSql($"SELECT committed_operation_id FROM record_manifest_commits WHERE form_record_id = '{record:D}' AND committed_content_revision = {revision};").Trim());

    private string Commits(Guid record) =>
        fixture.Stack.AuthorityObserverSql($"SELECT string_agg(committed_content_revision::text, ',' ORDER BY committed_content_revision) FROM record_manifest_commits WHERE form_record_id = '{record:D}';").Trim();

    private string OperationCount(Guid record) =>
        fixture.Stack.AuthorityObserverSql($"SELECT count(*) FROM content_change_operations WHERE form_record_id = '{record:D}';").Trim();

    [Fact]
    public async Task AnOwnerRestoresAnEarlierVersionAsANewRevisionHistoryIsKeptDocumentsAndRelatedRecordsStayAndEditingContinues()
    {
        var (s, record) = await AccusedAsync("c6-restore");
        var child = await s.CreateAddressAsync(s.Clerk, record, "1 Synthetic Way");                                       // a related record
        await EditAsync(s, record, new() { ["given_name"] = "Blake" });
        await s.UploadAsync(s.Clerk, record, "evidence.txt", "text/plain", StageBScenario.SyntheticBytes(512, 3));      // a document
        var beforeRestore = await ViewAsync(s, s.Clerk, record);
        var head = Revision(beforeRestore);
        var commitsBefore = Commits(record);
        Assert.Single(beforeRestore.GetProperty("documents").EnumerateArray());
        var childrenBefore = (await s.ChildrenAsync(s.Clerk, record)).Body;
        var childRevisionBefore = Revision(await ViewAsync(s, s.Clerk, child));

        var preview = await PreviewAsync(s, s.Clerk, record, 1);
        Assert.True(preview.IsSuccess, preview.Body);
        Assert.True(preview.Root.GetProperty("canRestore").GetBoolean());
        Assert.Equal(head, preview.Root.GetProperty("currentContentRevision").GetInt64());
        Assert.Equal(1, preview.Root.GetProperty("unchangedDocumentCount").GetInt32());
        Assert.True(preview.Root.GetProperty("formHasRelatedRecords").GetBoolean());
        Assert.Equal(1, preview.Root.GetProperty("version").GetProperty("contentRevision").GetInt64());
        var given = preview.Root.GetProperty("fields").EnumerateArray().Single(f => f.GetProperty("fieldKey").GetString() == "given_name");
        Assert.Equal(("Blake", "Avery", "Changed"), (given.GetProperty("currentValue").GetString(), given.GetProperty("restoredValue").GetString(), given.GetProperty("change").GetString()));
        var sourceOperation = preview.Root.GetProperty("sourceOperationId").GetGuid();
        Assert.Equal(OperationOf(record, 1), sourceOperation); // the Authority's operation for version 1
        Assert.Equal(commitsBefore, Commits(record));          // a preview writes nothing

        var restored = await RestoreAsync(s, s.Clerk, record, 1, head, sourceOperation, "the given name was changed by mistake");
        Assert.True(restored.IsSuccess, restored.Body);
        Assert.Equal(head + 1, restored.Root.GetProperty("contentRevision").GetInt64());
        Assert.Equal(1, restored.Root.GetProperty("restoredFromContentRevision").GetInt64());
        Assert.Contains("given_name", restored.Root.GetProperty("changedFieldKeys").EnumerateArray().Select(k => k.GetString()));

        var after = await ViewAsync(s, s.Clerk, record);
        Assert.Equal("Avery", Values(after)["given_name"]);
        Assert.Equal("Draft", after.GetProperty("lifecycle").GetProperty("status").GetString());
        // Documents and related records are not restored: the current document and the child stay exactly as they were.
        var documents = after.GetProperty("documents").EnumerateArray().ToList();
        Assert.Single(documents);
        Assert.Equal("evidence.txt", documents[0].GetProperty("originalFileName").GetString());
        Assert.Equal(childrenBefore, (await s.ChildrenAsync(s.Clerk, record)).Body);
        Assert.Equal(childRevisionBefore, Revision(await ViewAsync(s, s.Clerk, child)));

        // History: every earlier revision kept, the new one appended and current.
        Assert.Equal(commitsBefore + "," + (head + 1).ToString(System.Globalization.CultureInfo.InvariantCulture), Commits(record));
        var versions = (await fixture.Api.GetAsync($"/api/v1/form-records/{record:D}/versions", s.Token(s.Clerk))).Root.GetProperty("versions").EnumerateArray().ToList();
        Assert.Equal(head + 1, versions.Count);
        Assert.Equal(("Restored version 1", "ActivityLog", true), (versions[^1].GetProperty("actionLabel").GetString(), versions[^1].GetProperty("actionSource").GetString(), versions[^1].GetProperty("isCurrent").GetBoolean()));
        Assert.Equal("Blake", Values((await fixture.Api.GetAsync($"/api/v1/form-records/{record:D}/versions/{head}", s.Token(s.Clerk))).Root.GetProperty("projection"))["given_name"]); // the replaced version is unchanged

        // The reason is in the activity log (never values).
        var activity = await fixture.Api.GetAsync($"/api/v1/form-records/{record:D}/activity", s.Token(s.Clerk));
        Assert.Contains(activity.Root.GetProperty("events").EnumerateArray(), e => e.GetProperty("eventAction").GetString() == "RECORD_VERSION_RESTORED");
        var metadata = fixture.Stack.AttackerSql($"SELECT event_metadata_json FROM audit_events WHERE form_record_id = '{record:D}' AND event_action = 'RECORD_VERSION_RESTORED';");
        Assert.Contains("the given name was changed by mistake", metadata, StringComparison.Ordinal);
        Assert.DoesNotContain("Avery", metadata, StringComparison.Ordinal);
        Assert.DoesNotContain("Blake", metadata, StringComparison.Ordinal);

        // Ordinary editing continues normally.
        var next = await EditAsync(s, record, new() { ["family_name"] = "Synthetic-Edited" });
        Assert.Equal(head + 2, Revision(next));
        Assert.Equal("Avery", Values(next)["given_name"]);
    }

    [Fact]
    public async Task AnEmptyReasonASubmittedRecordAndAnArchivedRecordAreRefusedAndNothingIsStaged()
    {
        var (s, record) = await AccusedAsync("c6-restore-refused");
        await EditAsync(s, record, new() { ["given_name"] = "Blake" });
        var source = OperationOf(record, 1);

        var noReason = await RestoreAsync(s, s.Clerk, record, 1, 2, source, "   ");
        Assert.Equal((HttpStatusCode.UnprocessableEntity, "REASON_REQUIRED"), (noReason.Status, noReason.ErrorCode));

        await StepAsync(s, record, "submit");
        var operations = OperationCount(record);
        var submitted = await RestoreAsync(s, s.Clerk, record, 1, 3, source);
        Assert.Equal((HttpStatusCode.Conflict, "RECORD_NOT_EDITABLE"), (submitted.Status, submitted.ErrorCode));
        var preview = await PreviewAsync(s, s.Clerk, record, 1);
        Assert.False(preview.Root.GetProperty("canRestore").GetBoolean());
        Assert.Equal("RECORD_NOT_EDITABLE", preview.Root.GetProperty("blockedReasonCode").GetString());

        await StepAsync(s, record, "archive");
        operations = OperationCount(record);
        var archived = await RestoreAsync(s, s.Clerk, record, 1, 4, source);
        Assert.Equal((HttpStatusCode.Conflict, "RECORD_NOT_EDITABLE"), (archived.Status, archived.ErrorCode));
        Assert.Equal(operations, OperationCount(record)); // nothing prepared or staged
        Assert.Equal("1,2,3,4", Commits(record));
    }

    [Fact]
    public async Task NobodyButAnOwnerCanPreviewOrRestoreAndTheAnswerDisclosesNothing()
    {
        var (s, record) = await AccusedAsync("c6-restore-access");
        await EditAsync(s, record, new() { ["given_name"] = "Blake" });
        var source = OperationOf(record, 1);

        // A field-share contributor (with contributor writes on) can edit the shared field but can never restore.
        await fixture.Authority.OperatorAsync(HttpMethod.Post, "/internal/v1/operator/testing/contributor-writes", new { enabled = true });
        try
        {
            var share = await fixture.Api.PostAsync($"/api/v1/form-records/{record:D}/shares", s.Token(s.Clerk), new { recipientUserId = s.Reader.AuthorityUserId, recipientTeamId = (Guid?)null, recipientRole = "Contributor", fieldKeys = new[] { "given_name" } });
            Assert.True(share.IsSuccess, share.Body);
            await s.WaitGateReadyAsync(TimeSpan.FromSeconds(40));
            Assert.Equal(HttpStatusCode.NotFound, (await PreviewAsync(s, s.Reader, record, 1)).Status);
            Assert.Equal(HttpStatusCode.NotFound, (await RestoreAsync(s, s.Reader, record, 1, 2, source)).Status);
        }
        finally
        {
            await fixture.Authority.OperatorAsync(HttpMethod.Post, "/internal/v1/operator/testing/contributor-writes", new { enabled = (bool?)null });
        }

        var other = new StageBScenario(fixture, "c6-restore-other");
        await other.BootstrapAsync();
        foreach (var token in new[] { s.Token(s.Designer), other.Token(other.Clerk) })
        {
            Assert.Equal(HttpStatusCode.NotFound, (await fixture.Api.GetAsync($"/api/v1/form-records/{record:D}/versions/1/restore-preview", token)).Status);
            Assert.Equal(HttpStatusCode.NotFound, (await fixture.Api.PostAsync($"/api/v1/form-records/{record:D}/versions/1/restore", token, new { expectedCurrentContentRevision = 2, sourceOperationId = source, restoreReason = "x" })).Status);
        }

        Assert.Equal("1,2", Commits(record)); // shares change no content, and nothing was restored
    }

    [Theory]
    [InlineData("tampered")]
    [InlineData("missing")]
    public async Task TamperedOrMissingHistoricalContentFailsClosedAndQuarantinesTheRecord(string attack)
    {
        var (s, record) = await AccusedAsync("c6-restore-" + attack);
        await EditAsync(s, record, new() { ["date_of_birth"] = "1991-03-01" });
        var source = OperationOf(record, 1);
        var sql = attack == "tampered"
            ? $"UPDATE field_date_values SET date_value = DATE '2001-01-01' WHERE form_record_id = '{record:D}' AND content_change_operation_id = '{source:D}'; SELECT 1;"
            : $"DELETE FROM field_date_values WHERE form_record_id = '{record:D}' AND content_change_operation_id = '{source:D}'; SELECT 1;";
        fixture.Stack.AttackerSql(sql);
        var operations = OperationCount(record);

        var refused = await RestoreAsync(s, s.Clerk, record, 1, 2, source);
        Assert.Equal((HttpStatusCode.Locked, "INTEGRITY_VERIFICATION_FAILED"), (refused.Status, refused.ErrorCode));
        Assert.DoesNotContain("2001-01-01", refused.Body, StringComparison.Ordinal);
        Assert.Equal(operations, OperationCount(record));
        Assert.Equal("t", fixture.Stack.AuthorityObserverSql($"SELECT content_head_quarantined FROM record_content_heads WHERE form_record_id = '{record:D}';").Trim()); // AC-4
        Assert.Equal("1,2", Commits(record));
    }

    [Fact]
    public async Task ForgedVersionsAStaleHeadAndRetriesAreHandledWithoutDuplicates()
    {
        var (s, record) = await AccusedAsync("c6-restore-races");
        await EditAsync(s, record, new() { ["given_name"] = "Blake" });                       // r2
        var source = OperationOf(record, 1);

        Assert.Equal(HttpStatusCode.NotFound, (await RestoreAsync(s, s.Clerk, record, 99, 2, source)).Status);
        Assert.Equal("VERSION_IS_CURRENT", (await RestoreAsync(s, s.Clerk, record, 2, 2, OperationOf(record, 2))).ErrorCode);
        Assert.Equal("VERSION_CHANGED", (await RestoreAsync(s, s.Clerk, record, 1, 2, Guid.CreateVersion7())).ErrorCode); // not the Authority's operation for version 1

        // Reviewed at r2, then someone saves: the restore is stale and must be reviewed again.
        var preview = await PreviewAsync(s, s.Clerk, record, 1);
        Assert.Equal(2, preview.Root.GetProperty("currentContentRevision").GetInt64());
        await EditAsync(s, record, new() { ["family_name"] = "Changed-Meanwhile" });         // r3
        var stale = await RestoreAsync(s, s.Clerk, record, 1, 2, source);
        Assert.Equal((HttpStatusCode.Conflict, "CONTENT_REVISION_CONFLICT"), (stale.Status, stale.ErrorCode));
        Assert.Equal("1,2,3", Commits(record));

        // A retry with the same Idempotency-Key returns the committed revision; nothing is committed twice.
        var key = "c6-restore-" + Guid.NewGuid().ToString("N");
        var first = await RestoreAsync(s, s.Clerk, record, 1, 3, source, key: key);
        Assert.True(first.IsSuccess, first.Body);
        await ViewAsync(s, s.Clerk, record);
        var operations = OperationCount(record);
        var replay = await RestoreAsync(s, s.Clerk, record, 1, 3, source, key: key);
        Assert.True(replay.IsSuccess, replay.Body);
        Assert.Equal(first.Root.GetProperty("contentRevision").GetInt64(), replay.Root.GetProperty("contentRevision").GetInt64());
        Assert.Equal(first.Root.GetProperty("committedOperationId").GetGuid(), replay.Root.GetProperty("committedOperationId").GetGuid());
        Assert.Equal(operations, OperationCount(record));
        var fresh = await RestoreAsync(s, s.Clerk, record, 1, 3, source); // same body, new key: the head moved on
        Assert.Equal("CONTENT_REVISION_CONFLICT", fresh.ErrorCode);
        Assert.Equal("1,2,3,4", Commits(record));
    }

    [Fact]
    public async Task AVersionThatFailsTheCurrentFormIsRefusedEntirelyAndLeavesNoPartialRevision()
    {
        var values = StageBScenario.AccusedValues();
        values.Remove("external_reference");
        var (s, record) = await AccusedAsync("c6-restore-validation", values);              // r1 without external reference
        await EditAsync(s, record, new() { ["external_reference"] = "0199a2b3-c4d5-7e6f-8123-456789abcdef", ["given_name"] = "Blake" }); // r2

        // The form now requires the external reference, which version 1 does not have.
        var id = s.AccusedFields["external_reference"];
        var layout = s.AccusedLayout().Replace(StageBScenario.Field(id, "guid", "External reference", 2), StageBScenario.Field(id, "guid", "External reference", 2, required: true), StringComparison.Ordinal);
        await s.SaveDraftAsync(s.AccusedFormId, layout);
        await s.PublishAsync(s.AccusedFormId, "external reference now required");
        var operations = OperationCount(record);

        var preview = await PreviewAsync(s, s.Clerk, record, 1);
        Assert.False(preview.Root.GetProperty("canRestore").GetBoolean());
        Assert.Equal("RESTORE_VALIDATION_FAILED", preview.Root.GetProperty("blockedReasonCode").GetString());
        Assert.Contains("external_reference", preview.Root.GetProperty("validation").GetProperty("fieldKeys").EnumerateArray().Select(k => k.GetString()));

        var refused = await RestoreAsync(s, s.Clerk, record, 1, 2, OperationOf(record, 1));
        Assert.Equal((HttpStatusCode.UnprocessableEntity, "RESTORE_VALIDATION_FAILED"), (refused.Status, refused.ErrorCode));
        Assert.Equal(operations, OperationCount(record));
        Assert.Equal("1,2", Commits(record));
        Assert.Equal("Blake", Values(await ViewAsync(s, s.Clerk, record))["given_name"]); // nothing restored, not even the valid fields
    }

    [Fact]
    public async Task ARestoredValueIsANewItemThatAnOldShareDoesNotCover()
    {
        var (s, record) = await AccusedAsync("c6-restore-shares");
        var share = await fixture.Api.PostAsync($"/api/v1/form-records/{record:D}/shares", s.Token(s.Clerk), new { recipientUserId = s.Reader.AuthorityUserId, recipientTeamId = (Guid?)null, recipientRole = "Reader", fieldKeys = new[] { "family_name", "date_of_birth" } });
        Assert.True(share.IsSuccess, share.Body);
        await s.WaitGateReadyAsync(TimeSpan.FromSeconds(40));
        Assert.Equal("1990-02-28", Values(await ViewAsync(s, s.Reader, record))["date_of_birth"]);
        var originalItem = fixture.Stack.AttackerSql($"SELECT field_value_item_id FROM field_date_values WHERE form_record_id = '{record:D}' AND content_change_operation_id = '{OperationOf(record, 1):D}';").Trim();

        await EditAsync(s, record, new() { ["date_of_birth"] = "" });                          // r2: the shared value is removed
        Assert.False(Values(await ViewAsync(s, s.Reader, record)).ContainsKey("date_of_birth"));

        var restored = await RestoreAsync(s, s.Clerk, record, 1, 2, OperationOf(record, 1));
        Assert.True(restored.IsSuccess, restored.Body);
        Assert.Equal("1990-02-28", Values(await ViewAsync(s, s.Clerk, record))["date_of_birth"]);

        // The restored value has a new identity, and the share that covered the old item does not cover it.
        var restoredItem = fixture.Stack.AttackerSql($"SELECT field_value_item_id FROM field_date_values WHERE form_record_id = '{record:D}' AND content_change_operation_id = '{OperationOf(record, 3):D}';").Trim();
        Assert.NotEqual(originalItem, restoredItem);
        Assert.False(Values(await ViewAsync(s, s.Reader, record)).ContainsKey("date_of_birth"));
    }

    [Fact]
    public async Task ARestoredValueIsStoredUnderTheFieldsCurrentEncryptionPolicy()
    {
        // A plaintext (Internal/Standard) note at version 1; the field is then strengthened to ApplicationEncrypted and migrated.
        var s = new StageBScenario(fixture, "c6-restore-encryption");
        await s.BootstrapAsync();
        var formId = await s.CreateFormAsync("Protect " + s.Name, s.Clerk);
        var noteId = (await fixture.Api.PostAsync($"/api/v1/form-definitions/{formId:D}/fields", s.Token(s.Designer), new { fieldKey = "plain_note", fieldDisplayName = "Plain note", fieldDescription = string.Empty, fieldDataType = "String", options = (object?)null, fieldClassification = "Internal", fieldProtectionMode = "Standard" })).Root.GetProperty("fieldDefinitionId").GetGuid();
        await s.SaveDraftAsync(formId, StageBScenario.Layout("Protect", StageBScenario.Section("main", "Main", StageBScenario.Field(noteId, "text", "Plain note", 4))));
        await s.PublishAsync(formId, "v1 plaintext");
        var created = await fixture.Api.PostAsync($"/api/v1/form-definitions/{formId:D}/records", s.Token(s.Clerk), new { values = new Dictionary<string, string> { ["plain_note"] = "the original note" }, ownerTeamId = (Guid?)null });
        Assert.Equal(HttpStatusCode.Created, created.Status);
        var record = created.Root.GetProperty("formRecordId").GetGuid();
        await ViewAsync(s, s.Clerk, record);
        var sourceOperation = OperationOf(record, 1);
        Assert.Equal("1", fixture.Stack.AttackerSql($"SELECT count(*) FROM field_string_values WHERE form_record_id = '{record:D}' AND content_change_operation_id = '{sourceOperation:D}';").Trim());

        var strengthened = await fixture.Api.PostAsync($"/api/v1/form-definitions/{formId:D}/fields/{noteId:D}/protection", s.Token(s.Designer), new { targetClassification = "Confidential", targetProtectionMode = "ApplicationEncrypted", changeReason = "notes hold personal data", expectedProtectionRevision = 1 });
        Assert.True(strengthened.IsSuccess, strengthened.Body);
        var deadline = DateTime.UtcNow.AddSeconds(300);
        var state = string.Empty;
        while (DateTime.UtcNow < deadline && state is not ("Completed" or "Failed"))
        {
            var status = await fixture.Api.GetAsync($"/api/v1/form-definitions/{formId:D}/fields/{noteId:D}/protection", s.Token(s.Designer));
            if (status.IsSuccess)
            {
                state = status.Root.GetProperty("migration").GetProperty("migrationState").GetString()!;
            }

            await Task.Delay(1000, TestContext.Current.CancellationToken);
        }

        Assert.Equal("Completed", state);
        await EditAsync(s, record, new() { ["plain_note"] = "a later note" });
        var head = Revision(await ViewAsync(s, s.Clerk, record));

        var preview = await PreviewAsync(s, s.Clerk, record, 1);
        Assert.True(preview.IsSuccess, preview.Body);
        var note = preview.Root.GetProperty("fields").EnumerateArray().Single(f => f.GetProperty("fieldKey").GetString() == "plain_note");
        Assert.Equal(("a later note", "the original note"), (note.GetProperty("currentValue").GetString(), note.GetProperty("restoredValue").GetString()));
        Assert.True(note.GetProperty("storedEncrypted").GetBoolean());
        Assert.False(note.GetProperty("plaintextAcknowledgementRequired").GetBoolean());

        // A forged "acknowledge plaintext" from the client cannot lower the field below its current policy.
        var restored = await RestoreAsync(s, s.Clerk, record, 1, head, sourceOperation, acknowledge: true);
        Assert.True(restored.IsSuccess, restored.Body);
        var newOperation = restored.Root.GetProperty("committedOperationId").GetGuid();
        Assert.Equal("0", fixture.Stack.AttackerSql($"SELECT count(*) FROM field_string_values WHERE form_record_id = '{record:D}' AND content_change_operation_id = '{newOperation:D}';").Trim()); // no plaintext copy
        Assert.Equal("1", fixture.Stack.AttackerSql($"SELECT count(*) FROM field_encrypted_values WHERE form_record_id = '{record:D}' AND field_definition_id = '{noteId:D}' AND content_change_operation_id = '{newOperation:D}';").Trim());
        Assert.Equal("the original note", Values(await ViewAsync(s, s.Clerk, record))["plain_note"]);
    }

    [Fact]
    public async Task ValuesThatWouldLoseEncryptionNeedTheExplicitAcknowledgement()
    {
        // An encrypted Internal note at version 1; the organization permits plaintext and an administrator releases the field.
        var s = new StageBScenario(fixture, "c6-restore-downgrade");
        await s.BootstrapAsync();
        var formId = await s.CreateFormAsync("Release " + s.Name, s.Clerk);
        var noteId = (await fixture.Api.PostAsync($"/api/v1/form-definitions/{formId:D}/fields", s.Token(s.Designer), new { fieldKey = "sealed_note", fieldDisplayName = "Sealed note", fieldDescription = string.Empty, fieldDataType = "String", options = (object?)null, fieldClassification = "Internal", fieldProtectionMode = "ApplicationEncrypted" })).Root.GetProperty("fieldDefinitionId").GetGuid();
        await s.SaveDraftAsync(formId, StageBScenario.Layout("Release", StageBScenario.Section("main", "Main", StageBScenario.Field(noteId, "text", "Sealed note", 4))));
        await s.PublishAsync(formId, "v1 encrypted");
        var created = await fixture.Api.PostAsync($"/api/v1/form-definitions/{formId:D}/records", s.Token(s.Clerk), new { values = new Dictionary<string, string> { ["sealed_note"] = "sealed original" }, ownerTeamId = (Guid?)null });
        Assert.Equal(HttpStatusCode.Created, created.Status);
        var record = created.Root.GetProperty("formRecordId").GetGuid();
        await ViewAsync(s, s.Clerk, record);
        var sourceOperation = OperationOf(record, 1);

        var policy = await fixture.Api.GetAsync("/api/v1/organization/security-policy", s.Token(s.Designer));
        var permitted = await fixture.Api.SendAsync(HttpMethod.Put, "/api/v1/organization/security-policy", s.Token(s.Designer), new { minimumFieldClassification = "Internal", plaintextInternalFieldsPermitted = true, plaintextDowngradePermitted = true, policyStatementText = "Governance approved the removal of encryption from Internal fields for this test organization.", expectedPolicyRevision = policy.Root.GetProperty("securityPolicyRevision").GetInt64() }, null);
        Assert.True(permitted.IsSuccess, permitted.Body);
        var status = await fixture.Api.GetAsync($"/api/v1/form-definitions/{formId:D}/fields/{noteId:D}/protection", s.Token(s.Designer));
        var released = await fixture.Api.PostAsync($"/api/v1/form-definitions/{formId:D}/fields/{noteId:D}/protection/downgrade", s.Token(s.Administrator), new { changeReason = "approved release to plaintext", expectedProtectionRevision = status.Root.GetProperty("protectionPolicyRevision").GetInt64() });
        Assert.True(released.IsSuccess, released.Body);
        var deadline = DateTime.UtcNow.AddSeconds(300);
        var plaintext = false;
        while (DateTime.UtcNow < deadline && !plaintext)
        {
            var current = await fixture.Api.GetAsync($"/api/v1/form-definitions/{formId:D}/fields/{noteId:D}/protection", s.Token(s.Designer));
            plaintext = current.IsSuccess && current.Root.GetProperty("fullyPlaintext").GetBoolean();
            await Task.Delay(1000, TestContext.Current.CancellationToken);
        }

        Assert.True(plaintext, "the release did not complete");
        await EditAsync(s, record, new() { ["sealed_note"] = "a later note" });
        var head = Revision(await ViewAsync(s, s.Clerk, record));

        var preview = await PreviewAsync(s, s.Clerk, record, 1);
        Assert.True(preview.IsSuccess, preview.Body);
        Assert.True(preview.Root.GetProperty("canRestore").GetBoolean());
        Assert.Equal(["sealed_note"], preview.Root.GetProperty("plaintextAcknowledgementFieldKeys").EnumerateArray().Select(k => k.GetString()));
        var note = preview.Root.GetProperty("fields").EnumerateArray().Single(f => f.GetProperty("fieldKey").GetString() == "sealed_note");
        Assert.True(note.GetProperty("plaintextAcknowledgementRequired").GetBoolean());
        Assert.False(note.GetProperty("storedEncrypted").GetBoolean());

        var operations = OperationCount(record);
        var refused = await RestoreAsync(s, s.Clerk, record, 1, head, sourceOperation);
        Assert.Equal((HttpStatusCode.UnprocessableEntity, "PLAINTEXT_ACKNOWLEDGEMENT_REQUIRED"), (refused.Status, refused.ErrorCode));
        Assert.Contains("sealed_note", refused.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("sealed original", refused.Body, StringComparison.Ordinal);
        Assert.Equal(operations, OperationCount(record));
        Assert.Equal("a later note", Values(await ViewAsync(s, s.Clerk, record))["sealed_note"]);

        var acknowledged = await RestoreAsync(s, s.Clerk, record, 1, head, sourceOperation, acknowledge: true);
        Assert.True(acknowledged.IsSuccess, acknowledged.Body);
        var newOperation = acknowledged.Root.GetProperty("committedOperationId").GetGuid();
        Assert.Equal("1", fixture.Stack.AttackerSql($"SELECT count(*) FROM field_string_values WHERE form_record_id = '{record:D}' AND content_change_operation_id = '{newOperation:D}';").Trim()); // stored as today's policy says
        Assert.Equal("sealed original", Values(await ViewAsync(s, s.Clerk, record))["sealed_note"]);
        Assert.Matches("\"plaintextAcknowledged\"\\s*:\\s*true", fixture.Stack.AttackerSql($"SELECT event_metadata_json FROM audit_events WHERE form_record_id = '{record:D}' AND event_action = 'RECORD_VERSION_RESTORED';"));
    }

    [Fact]
    public async Task RepeatingSectionEntriesAreRestoredExactlyWithNewIdentitiesForEntriesThatNoLongerExist()
    {
        var s = new StageBScenario(fixture, "c6-restore-groups");
        await s.BootstrapAsync();
        var formId = await s.CreateFormAsync("People " + s.Name, s.Clerk);
        var group = await fixture.Api.PostAsync($"/api/v1/form-definitions/{formId:D}/groups", s.Token(s.Designer), new { groupKey = "people", groupDisplayName = "People" });
        Assert.True(group.Status == HttpStatusCode.Created, group.Body);
        var groupId = group.Root.GetProperty("groupDefinitionId").GetGuid();
        async Task<Guid> Add(string key, Guid? inGroup = null)
        {
            var result = await fixture.Api.PostAsync($"/api/v1/form-definitions/{formId:D}/fields", s.Token(s.Designer), new { fieldKey = key, fieldDisplayName = key, fieldDescription = string.Empty, fieldDataType = "String", options = (object?)null, groupDefinitionId = inGroup });
            Assert.True(result.Status == HttpStatusCode.Created, result.Body);
            return result.Root.GetProperty("fieldDefinitionId").GetGuid();
        }

        var title = await Add("case_title");
        var name = await Add("person_name", groupId);
        await s.SaveDraftAsync(formId, StageBScenario.Layout("Case", StageBScenario.Section("main", "Case", StageBScenario.Field(title, "text", "Case title", 4, required: true)),
            $$"""{"sectionKey":"people","title":"People","gridColumns":4,"repeat":{"groupDefinitionId":"{{groupId:D}}","minOccurrences":0,"maxOccurrences":5,"allowAdd":true,"allowRemove":true,"allowReorder":true},"elements":[{{StageBScenario.Field(name, "text", "Name", 4)}}]}"""));
        await s.PublishAsync(formId, "people");

        var created = await fixture.Api.PostAsync($"/api/v1/form-definitions/{formId:D}/records", s.Token(s.Clerk), new { values = new Dictionary<string, string> { ["case_title"] = "Groups" }, ownerTeamId = (Guid?)null, groups = new object[] { new { groupKey = "people", occurrences = new object[] { new { formRecordGroupId = (Guid?)null, values = new Dictionary<string, string> { ["person_name"] = "Ada" } }, new { formRecordGroupId = (Guid?)null, values = new Dictionary<string, string> { ["person_name"] = "Bo" } } } } } });
        Assert.True(created.Status == HttpStatusCode.Created, created.Body);
        var record = created.Root.GetProperty("formRecordId").GetGuid();
        static List<(Guid Id, string Name)> Entries(JsonElement view)
        {
            var record = view.GetProperty("record");
            var ids = record.GetProperty("groups").EnumerateArray().Single(g => g.GetProperty("groupKey").GetString() == "people").GetProperty("occurrences").EnumerateArray().Select(o => o.GetProperty("formRecordGroupId").GetGuid()).ToList();
            var values = record.GetProperty("values").EnumerateArray().Where(v => v.TryGetProperty("formRecordGroupId", out var g) && g.ValueKind == JsonValueKind.String).ToList();
            return ids.Select(id => (id, values.Single(v => v.GetProperty("formRecordGroupId").GetGuid() == id).GetProperty("stringValue").GetString()!)).ToList();
        }

        var v1 = Entries(await ViewAsync(s, s.Clerk, record));
        Assert.Equal(["Ada", "Bo"], v1.Select(e => e.Name));
        // r2: Ada renamed, Bo removed, Cy added.
        await EditAsync(s, record, new(), [new { groupKey = "people", occurrences = new object[] { new { formRecordGroupId = (Guid?)v1[0].Id, values = new Dictionary<string, string> { ["person_name"] = "Ada Renamed" } }, new { formRecordGroupId = (Guid?)null, values = new Dictionary<string, string> { ["person_name"] = "Cy" } } } }]);

        var restored = await RestoreAsync(s, s.Clerk, record, 1, 2, OperationOf(record, 1));
        Assert.True(restored.IsSuccess, restored.Body);
        var v3 = Entries(await ViewAsync(s, s.Clerk, record));
        Assert.Equal(["Ada", "Bo"], v3.Select(e => e.Name));
        Assert.Equal(v1[0].Id, v3[0].Id);    // the entry that still existed keeps its identity
        Assert.NotEqual(v1[1].Id, v3[1].Id); // the removed entry comes back as a NEW entry (I-R)
        Assert.Equal("1,2,3", Commits(record));
    }
}
