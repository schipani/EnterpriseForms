using System.Net;
using System.Text.Json;
using EnterpriseForms.SecurityTests.Harness;
using EnterpriseForms.StageBTests.Harness;
using Xunit;

namespace EnterpriseForms.StageBTests.Tests;

/// <summary>
/// Phase C6 M1 — record lifecycle on the real stack (PostgreSQL ×2, SpiceDB, Vault Transit, Authority, API): the owner-only
/// Draft → Submitted → Archived transitions, locked content for owners and shared contributors, validation on submit,
/// verbatim preservation of encrypted values and documents, stale and concurrent changes, listing, and a hostile business
/// database administrator. Status is always read from the Authority-verified signed header (the projection response).
/// </summary>
[Collection(StageBStackCollectionDefinition.Name)]
public sealed class RecordLifecycleTests(StageBFixture fixture)
{
    private async Task<(StageBScenario S, Guid Record)> AccusedAsync(string name, bool withDocument = false)
    {
        var s = new StageBScenario(fixture, name);
        await s.BootstrapAsync();
        await s.BuildAccusedWorkspaceAsync();
        var record = await s.CreateAccusedAsync(s.Clerk);
        if (withDocument)
        {
            await s.UploadAsync(s.Clerk, record, "lifecycle-note.txt", "text/plain", StageBScenario.SyntheticBytes(1024, 7));
        }

        return (s, record);
    }

    private static long Revision(JsonElement projection) => projection.GetProperty("record").GetProperty("contentRevision").GetInt64();

    private static string Status(JsonElement projection) => projection.GetProperty("lifecycle").GetProperty("status").GetString()!;

    private static List<string> Allowed(JsonElement projection) => projection.GetProperty("lifecycle").GetProperty("allowedTransitions").EnumerateArray().Select(e => e.GetString()!).ToList();

    private static async Task<JsonElement> ViewAsync(StageBScenario s, TestUser user, Guid record) => (await s.WaitProjectionAsync(user, record)).Root.Clone();

    private Task<HttpResult> TransitionAsync(StageBScenario s, TestUser user, Guid record, string transition, long revision, string? expectedStatus = null, string? reason = null, string? idempotencyKey = null) =>
        fixture.Api.PostAsync($"/api/v1/form-records/{record:D}/{transition}", s.Token(user), new { expectedContentRevision = revision, expectedStatus, reason }, idempotencyKey);

    /// <summary>Applies a transition from the record's current verified state and returns the new projection.</summary>
    private async Task<JsonElement> StepAsync(StageBScenario s, Guid record, string transition, string? reason = null)
    {
        var current = await ViewAsync(s, s.Clerk, record);
        var result = await TransitionAsync(s, s.Clerk, record, transition, Revision(current), Status(current), reason);
        Assert.True(result.IsSuccess, transition + ": " + result.Body);
        var after = await ViewAsync(s, s.Clerk, record);
        Assert.Equal(Revision(current) + 1, Revision(after));
        Assert.Equal(result.Root.GetProperty("status").GetString(), Status(after));
        return after;
    }

    private Task<HttpResult> EditAsync(StageBScenario s, TestUser user, Guid record, Dictionary<string, string> values, long revision) =>
        fixture.Api.PatchAsync($"/api/v1/form-records/{record:D}", s.Token(user), new { expectedContentRevision = revision, values });

    private async Task<List<string?>> ActionsAsync(StageBScenario s, Guid record)
    {
        var activity = await fixture.Api.GetAsync($"/api/v1/form-records/{record:D}/activity", s.Token(s.Clerk));
        Assert.True(activity.IsSuccess, activity.Body);
        return activity.Root.GetProperty("events").EnumerateArray().Select(e => e.GetProperty("eventAction").GetString()).ToList();
    }

    private async Task SetContributorWritesAsync(bool? enabled)
    {
        var result = await fixture.Authority.OperatorAsync(HttpMethod.Post, "/internal/v1/operator/testing/contributor-writes", new { enabled });
        Assert.True(result.IsSuccess, result.Body);
    }

    private async Task<List<Guid>> ListedAsync(StageBScenario s, TestUser user, string query = "")
    {
        var list = await fixture.Api.GetAsync("/api/v1/form-records?pageSize=100" + query, s.Token(user));
        Assert.True(list.IsSuccess, list.Body);
        return list.Root.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("formRecordId").GetGuid()).ToList();
    }

    [Fact]
    public async Task TheOwnerWalksTheLifecycleAndEveryStepIsAVerifiedAcceptedRevisionWithAuditEvents()
    {
        var (s, record) = await AccusedAsync("c6-walk");
        var draft = await ViewAsync(s, s.Clerk, record);
        Assert.Equal("Draft", Status(draft));
        Assert.Equal(new[] { "Submit", "Archive" }, Allowed(draft));
        Assert.True(draft.GetProperty("lifecycle").GetProperty("contentEditable").GetBoolean());

        var submitted = await StepAsync(s, record, "submit");
        Assert.Equal("Submitted", Status(submitted));
        Assert.Equal(new[] { "Reopen", "Archive" }, Allowed(submitted));
        Assert.False(submitted.GetProperty("lifecycle").GetProperty("contentEditable").GetBoolean());
        var submittedOn = submitted.GetProperty("lifecycle").GetProperty("submittedOn").GetString();
        Assert.NotNull(submittedOn);
        Assert.Equal("Verified", submitted.GetProperty("record").GetProperty("integrityStatus").GetString());

        // Reopening needs a reason; a blank one is refused before anything is staged.
        var revision = Revision(submitted);
        var noReason = await TransitionAsync(s, s.Clerk, record, "reopen", revision, "Submitted", "   ");
        Assert.Equal("REASON_REQUIRED", noReason.ErrorCode);
        Assert.Equal(revision, Revision(await ViewAsync(s, s.Clerk, record)));
        var reopened = await StepAsync(s, record, "reopen", "a witness statement was missing");
        Assert.Equal("Draft", Status(reopened));
        Assert.Equal(JsonValueKind.Null, reopened.GetProperty("lifecycle").GetProperty("submittedOn").ValueKind);

        // Archive from Draft; unarchive returns a never-submitted record to Draft.
        Assert.Equal("Archived", Status(await StepAsync(s, record, "archive", "duplicate entry")));
        Assert.Equal(new[] { "Unarchive" }, Allowed(await ViewAsync(s, s.Clerk, record)));
        Assert.Equal("Draft", Status(await StepAsync(s, record, "unarchive")));

        // Submit → archive → unarchive restores exactly Submitted, with the original submission time (signed in the header).
        var again = await StepAsync(s, record, "submit");
        var secondSubmittedOn = again.GetProperty("lifecycle").GetProperty("submittedOn").GetString();
        Assert.Equal("Archived", Status(await StepAsync(s, record, "archive")));
        var restored = await StepAsync(s, record, "unarchive");
        Assert.Equal("Submitted", Status(restored));
        Assert.Equal(secondSubmittedOn, restored.GetProperty("lifecycle").GetProperty("submittedOn").GetString());

        // Transitions that do not exist for the current state are refused without staging anything.
        var revisionNow = Revision(restored);
        foreach (var impossible in new[] { "submit", "unarchive" })
        {
            var refused = await TransitionAsync(s, s.Clerk, record, impossible, revisionNow);
            Assert.Equal("LIFECYCLE_TRANSITION_NOT_ALLOWED", refused.ErrorCode);
            Assert.Equal(HttpStatusCode.Conflict, refused.Status);
        }

        Assert.Equal(revisionNow, Revision(await ViewAsync(s, s.Clerk, record)));
        var actions = await ActionsAsync(s, record);
        foreach (var expected in new[] { "RECORD_SUBMITTED", "RECORD_REOPENED", "RECORD_ARCHIVED", "RECORD_UNARCHIVED" })
        {
            Assert.Contains(expected, actions);
        }
    }

    [Fact]
    public async Task ArchiveAndUnarchivePreserveEncryptedValuesAndDocumentsByteForByteWithoutRewritingThem()
    {
        var (s, record) = await AccusedAsync("c6-preserve", withDocument: true);
        string OperationOf() => AuthorityOracle.Head(fixture.Stack, record).OperationId!.Value.ToString("D");
        string Ciphertext(string op, string fieldKey) => fixture.Stack.AttackerSql(
            $"SELECT encode(e.encrypted_value_bytes,'hex') || ':' || encode(e.encryption_nonce_bytes,'hex') || ':' || encode(e.wrapped_data_key_bytes,'hex') FROM field_encrypted_values e JOIN field_definitions f ON f.field_definition_id = e.field_definition_id WHERE e.form_record_id = '{record:D}' AND f.field_key = '{fieldKey}' AND e.content_change_operation_id = '{op}';").Trim();
        string Document(string op) => fixture.Stack.AttackerSql($"SELECT storage_object_key || ':' || encode(ciphertext_sha256,'hex') || ':' || encode(wrapped_data_key,'hex') FROM file_attachment_versions WHERE form_record_id = '{record:D}' AND content_change_operation_id = '{op}';").Trim();
        string Plaintext(string op, string table, string valueColumn) => fixture.Stack.AttackerSql($"SELECT string_agg({valueColumn}::text, ',' ORDER BY field_value_item_id) FROM {table} WHERE form_record_id = '{record:D}' AND content_change_operation_id = '{op}';").Trim();

        var before = OperationOf();
        var givenBefore = Ciphertext(before, "given_name");
        var familyBefore = Ciphertext(before, "family_name");
        var documentBefore = Document(before);
        var dateBefore = Plaintext(before, "field_date_values", "date_value");
        Assert.NotEmpty(givenBefore);
        Assert.NotEmpty(documentBefore);

        await StepAsync(s, record, "archive", "closing the file");
        var archivedOperation = OperationOf();
        Assert.NotEqual(before, archivedOperation);
        await StepAsync(s, record, "unarchive");
        var after = OperationOf();
        Assert.NotEqual(archivedOperation, after);

        foreach (var operation in new[] { archivedOperation, after })
        {
            Assert.Equal(givenBefore, Ciphertext(operation, "given_name"));
            Assert.Equal(familyBefore, Ciphertext(operation, "family_name"));
            Assert.Equal(documentBefore, Document(operation));
            Assert.Equal(dateBefore, Plaintext(operation, "field_date_values", "date_value"));
        }

        // Still readable by the owner, unchanged, with its document.
        var view = await ViewAsync(s, s.Clerk, record);
        var values = view.GetProperty("record").GetProperty("values").EnumerateArray().ToDictionary(v => v.GetProperty("fieldKey").GetString()!, v => v.GetProperty("stringValue").GetString()!);
        Assert.Equal("Avery", values["given_name"]);
        Assert.Single(view.GetProperty("documents").EnumerateArray());
    }

    [Fact]
    public async Task OnlyTheOwnerCanChangeStatusAndEveryRefusalIsAuditedWithoutChangingTheRecord()
    {
        var (s, record) = await AccusedAsync("c6-unauthorized");
        var startRevision = Revision(await ViewAsync(s, s.Clerk, record));

        // No access at all: the record does not exist for them; a reader/designer without a grant likewise.
        foreach (var stranger in new[] { s.Reader, s.DesignerOnly, s.Administrator })
        {
            foreach (var transition in new[] { "submit", "archive" })
            {
                var result = await TransitionAsync(s, stranger, record, transition, startRevision);
                Assert.Contains(result.Status, new[] { HttpStatusCode.NotFound, HttpStatusCode.Forbidden });
            }
        }

        // A tenant outside the organization never reaches the record.
        var other = new StageBScenario(fixture, "c6-unauthorized-other");
        await other.BootstrapAsync();
        var cross = await fixture.Api.PostAsync($"/api/v1/form-records/{record:D}/archive", other.Token(other.Designer), new { expectedContentRevision = startRevision });
        Assert.Contains(cross.Status, new[] { HttpStatusCode.NotFound, HttpStatusCode.Forbidden });

        // Shared recipients (reader and contributor) can see the record exists but cannot change its status.
        var share = await fixture.Api.PostAsync($"/api/v1/form-records/{record:D}/shares", s.Token(s.Clerk), new { recipientUserId = s.Designer.AuthorityUserId, recipientTeamId = (Guid?)null, recipientRole = "Contributor", fieldKeys = new[] { "given_name" } });
        Assert.True(share.IsSuccess, share.Body);
        await s.WaitGateReadyAsync(TimeSpan.FromSeconds(40));
        var shared = await s.WaitProjectionAsync(s.Designer, record, TimeSpan.FromSeconds(40));
        Assert.Empty(Allowed(shared.Root));
        foreach (var transition in new[] { "submit", "reopen", "archive", "unarchive" })
        {
            var result = await TransitionAsync(s, s.Designer, record, transition, startRevision);
            Assert.Equal(HttpStatusCode.Forbidden, result.Status);
            Assert.Equal("FORBIDDEN", result.ErrorCode);
        }

        // Nothing changed, and the contributor's refusals are in the owner's activity history.
        var after = await ViewAsync(s, s.Clerk, record);
        Assert.Equal(startRevision, Revision(after));
        Assert.Equal("Draft", Status(after));
        Assert.Contains("RECORD_LIFECYCLE_DENIED", await ActionsAsync(s, record));
        Assert.DoesNotContain("RECORD_SUBMITTED", await ActionsAsync(s, record));
    }

    [Fact]
    public async Task ASubmittedRecordRejectsEveryKindOfContentChangeForOwnersAndSharedContributorsAndAnArchivedOneToo()
    {
        var (s, record) = await AccusedAsync("c6-locked", withDocument: true);
        var addressId = await s.CreateAddressAsync(s.Clerk, record, "1 Lockdown Way");
        try
        {
            await SetContributorWritesAsync(true);
            var share = await fixture.Api.PostAsync($"/api/v1/form-records/{record:D}/shares", s.Token(s.Clerk), new { recipientUserId = s.Designer.AuthorityUserId, recipientTeamId = (Guid?)null, recipientRole = "Contributor", fieldKeys = new[] { "given_name" } });
            Assert.True(share.IsSuccess, share.Body);
            await s.WaitGateReadyAsync(TimeSpan.FromSeconds(40));

            // While Draft the contributor can edit (the switch is on) — the lock below is the only thing that changes.
            var draftView = await s.WaitProjectionAsync(s.Designer, record, TimeSpan.FromSeconds(40));
            Assert.True(draftView.Root.GetProperty("permissions").GetProperty("canEditSharedFields").GetBoolean());
            var draftEdit = await EditAsync(s, s.Designer, record, new() { ["given_name"] = "Before-Submit" }, Revision(draftView.Root));
            Assert.True(draftEdit.IsSuccess, draftEdit.Body);

            foreach (var lockingTransition in new[] { "submit", "archive" })
            {
                if (lockingTransition == "archive")
                {
                    // Back to a lockable state, then archive (a second locked state).
                    await StepAsync(s, record, "reopen", "check the archived lock too");
                }

                var locked = await StepAsync(s, record, lockingTransition);
                var revision = Revision(locked);
                var stateName = Status(locked);

                // Owner edit (full snapshot path).
                var ownerEdit = await EditAsync(s, s.Clerk, record, new() { ["family_name"] = "Locked-Out" }, revision);
                Assert.Equal(HttpStatusCode.Conflict, ownerEdit.Status);
                Assert.Equal("RECORD_NOT_EDITABLE", ownerEdit.ErrorCode);

                // Shared contributor (partial path): the lock is decided before scope rules, so this is the lock, not a scope refusal.
                var contributorView = await s.WaitProjectionAsync(s.Designer, record, TimeSpan.FromSeconds(40));
                Assert.False(contributorView.Root.GetProperty("permissions").GetProperty("canEditSharedFields").GetBoolean());
                Assert.Equal(stateName, Status(contributorView.Root));
                var contributorEdit = await EditAsync(s, s.Designer, record, new() { ["given_name"] = "Contributor-Bypass" }, Revision(contributorView.Root));
                Assert.Equal(HttpStatusCode.Conflict, contributorEdit.Status);
                Assert.Equal("RECORD_NOT_EDITABLE", contributorEdit.ErrorCode);

                // Documents: starting an upload, and removing the existing document.
                var upload = await fixture.Api.PostAsync("/api/v1/upload-sessions", s.Token(s.Clerk), new { formRecordId = record, fieldDefinitionId = s.AccusedFields["case_documents"], originalFileName = "late.txt", contentType = "text/plain", sizeBytes = 100L, replacesFileAttachmentId = (Guid?)null });
                Assert.Equal("RECORD_NOT_EDITABLE", upload.ErrorCode);
                var documentId = locked.GetProperty("documents").EnumerateArray().First().GetProperty("fileAttachmentId").GetGuid();
                var removal = await fixture.Api.PostAsync($"/api/v1/form-records/{record:D}/documents/{documentId:D}/remove", s.Token(s.Clerk), new { expectedContentRevision = revision, removalReason = "should be refused" });
                Assert.Equal("RECORD_NOT_EDITABLE", removal.ErrorCode);

                // Related records: adding a child, and unlinking one.
                var child = await fixture.Api.PostAsync($"/api/v1/form-records/{record:D}/related", s.Token(s.Clerk), new { formRelationshipDefinitionId = s.RelationshipId, values = new Dictionary<string, string> { ["street_address"] = "2 Too Late Rd" } });
                Assert.Equal("RECORD_NOT_EDITABLE", child.ErrorCode);
                var unlink = await fixture.Api.PostAsync($"/api/v1/form-records/{record:D}/related/{addressId:D}/unlink", s.Token(s.Clerk), new { removalReason = "should be refused" });
                Assert.Equal("RECORD_NOT_EDITABLE", unlink.ErrorCode);

                // Nothing was accepted by the Authority: the head is exactly the lock revision, content unchanged.
                Assert.Equal(revision, AuthorityOracle.Head(fixture.Stack, record).ContentRevision);
                var values = (await ViewAsync(s, s.Clerk, record)).GetProperty("record").GetProperty("values").EnumerateArray().ToDictionary(v => v.GetProperty("fieldKey").GetString()!, v => v.GetProperty("stringValue").GetString()!);
                Assert.Equal("Before-Submit", values["given_name"]);
                Assert.Equal("Synthetic", values["family_name"]);

                // Sharing is not content: revoking/granting access stays possible on a locked record (OD-06).
                Assert.True((await fixture.Api.GetAsync($"/api/v1/form-records/{record:D}/shares", s.Token(s.Clerk))).IsSuccess);
            }
        }
        finally
        {
            await SetContributorWritesAsync(null);
        }
    }

    [Fact]
    public async Task SubmissionIsValidatedAgainstTheCurrentFormAndWaitsForUploadsAndRejectedAttemptsChangeNothing()
    {
        var (s, record) = await AccusedAsync("c6-validation");

        // The form gains a required field after the record was created: the record no longer satisfies it.
        var caseNumber = await s.AddFieldAsync(s.AccusedFormId, "case_number", "Case number", "String");
        var layoutWithRequired = s.AccusedLayout().Replace("\"sections\":[", "\"sections\":[" + StageBScenario.Section("filing", "Filing", StageBScenario.Field(caseNumber, "text", "Case number", 4, required: true)) + ",", StringComparison.Ordinal);
        await s.SaveDraftAsync(s.AccusedFormId, layoutWithRequired);
        await s.PublishAsync(s.AccusedFormId, "adds a required case number");

        var view = await ViewAsync(s, s.Clerk, record);
        var revision = Revision(view);
        var incomplete = await TransitionAsync(s, s.Clerk, record, "submit", revision, "Draft");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, incomplete.Status);
        Assert.Equal("VALIDATION_FAILED", incomplete.ErrorCode);
        Assert.Contains("case_number", incomplete.Body, StringComparison.Ordinal);
        Assert.Equal(revision, AuthorityOracle.Head(fixture.Stack, record).ContentRevision);
        Assert.Equal("Draft", Status(await ViewAsync(s, s.Clerk, record)));

        // Archiving needs no validation: the same record can be archived (and unarchived) despite the missing required value.
        Assert.Equal("Archived", Status(await StepAsync(s, record, "archive")));
        Assert.Equal("Draft", Status(await StepAsync(s, record, "unarchive")));

        // An upload that is still in flight blocks submission; cancelling it releases the block.
        var begun = await fixture.Api.PostAsync("/api/v1/upload-sessions", s.Token(s.Clerk), new { formRecordId = record, fieldDefinitionId = s.AccusedFields["case_documents"], originalFileName = "pending.txt", contentType = "text/plain", sizeBytes = 200L, replacesFileAttachmentId = (Guid?)null });
        Assert.Equal(HttpStatusCode.Created, begun.Status);
        var fixedUp = await EditAsync(s, s.Clerk, record, new() { ["case_number"] = "2026-0001" }, Revision(await ViewAsync(s, s.Clerk, record)));
        Assert.True(fixedUp.IsSuccess, fixedUp.Body);
        var current = await ViewAsync(s, s.Clerk, record);
        var blocked = await TransitionAsync(s, s.Clerk, record, "submit", Revision(current), "Draft");
        Assert.Equal(HttpStatusCode.Conflict, blocked.Status);
        Assert.Equal("UPLOADS_IN_PROGRESS", blocked.ErrorCode);
        var sessionId = begun.Root.GetProperty("uploadSessionId").GetGuid();
        var cancelled = await fixture.Api.SendAsync(HttpMethod.Delete, $"/api/v1/upload-sessions/{sessionId:D}", s.Token(s.Clerk), null, null);
        Assert.True(cancelled.IsSuccess, cancelled.Body);
        Assert.Equal("Submitted", Status(await StepAsync(s, record, "submit")));
    }

    [Fact]
    public async Task StaleVersionsWrongStatusAndConcurrentTransitionsNeverBothWin()
    {
        var (s, record) = await AccusedAsync("c6-concurrency");
        var revision = Revision(await ViewAsync(s, s.Clerk, record));

        // A stale revision and a stale expected status are both refused without staging anything.
        var staleRevision = await TransitionAsync(s, s.Clerk, record, "submit", revision - 1, "Draft");
        Assert.Equal("CONTENT_REVISION_CONFLICT", staleRevision.ErrorCode);
        var staleStatus = await TransitionAsync(s, s.Clerk, record, "submit", revision, "Submitted");
        Assert.Equal("LIFECYCLE_STATE_CONFLICT", staleStatus.ErrorCode);
        Assert.Equal(revision, AuthorityOracle.Head(fixture.Stack, record).ContentRevision);

        // Submit and archive prepared on the same revision and held before commit: the Authority accepts one, the other loses the head compare-and-swap.
        var arm = await fixture.Authority.OperatorAsync(HttpMethod.Post, "/internal/v1/operator/fault-injection", new { fault_name = "PauseBeforeContentCommit" });
        Assert.True(arm.IsSuccess, arm.Body);
        HttpResult submit, archive;
        try
        {
            var submitting = TransitionAsync(s, s.Clerk, record, "submit", revision, "Draft");
            var archiving = TransitionAsync(s, s.Clerk, record, "archive", revision, "Draft");
            await WaitPausedAsync(2);
            await fixture.Authority.OperatorAsync(HttpMethod.Post, "/internal/v1/operator/fault-injection", new { fault_name = "None" });
            submit = await submitting;
            archive = await archiving;
        }
        finally
        {
            await fixture.Authority.OperatorAsync(HttpMethod.Post, "/internal/v1/operator/fault-injection", new { fault_name = "None" });
        }

        Assert.True(submit.IsSuccess ^ archive.IsSuccess, "exactly one concurrent transition is accepted: " + submit.Body + " / " + archive.Body);
        var loser = submit.IsSuccess ? archive : submit;
        Assert.Equal("CONTENT_REVISION_CONFLICT", loser.ErrorCode);
        var finalView = await ViewAsync(s, s.Clerk, record);
        Assert.Equal(revision + 1, Revision(finalView));
        Assert.Equal(submit.IsSuccess ? "Submitted" : "Archived", Status(finalView));
    }

    private async Task WaitPausedAsync(int count)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            var status = await fixture.Authority.OperatorAsync(HttpMethod.Get, "/internal/v1/operator/fault-injection", null);
            Assert.True(status.IsSuccess, status.Body);
            if (status.Root.GetProperty("paused_operations").GetInt32() >= count)
            {
                return;
            }

            await Task.Delay(100, TestContext.Current.CancellationToken);
        }

        throw new TimeoutException($"{count} commit(s) did not reach the pause point");
    }

    [Fact]
    public async Task ArchivedRecordsAreHiddenFromTheDefaultListButFoundWhenAskedFor()
    {
        var (s, record) = await AccusedAsync("c6-listing");
        var other = await s.CreateAccusedAsync(s.Clerk, StageBScenario.AccusedValues("Blair", "Listing"));
        Assert.Contains(record, await ListedAsync(s, s.Clerk));

        await StepAsync(s, record, "archive");
        var ordinary = await ListedAsync(s, s.Clerk);
        Assert.DoesNotContain(record, ordinary);
        Assert.Contains(other, ordinary);
        Assert.Contains(record, await ListedAsync(s, s.Clerk, "&includeArchived=true"));
        var onlyArchived = await ListedAsync(s, s.Clerk, "&recordStatus=Archived");
        Assert.Contains(record, onlyArchived);
        Assert.DoesNotContain(other, onlyArchived);

        // Discovery is unchanged: the archived record is still opened directly (verified) and a stranger still does not see it.
        Assert.Equal("Archived", Status(await ViewAsync(s, s.Clerk, record)));
        Assert.DoesNotContain(record, await ListedAsync(s, s.Reader, "&includeArchived=true"));
    }

    [Fact]
    public async Task ABusinessDatabaseAdministratorCannotUnlockOrFakeTheLifecycleStatus()
    {
        var (s, record) = await AccusedAsync("c6-tamper");
        var submitted = await StepAsync(s, record, "submit");

        // (1) Flipping the unsigned listing projection does not unlock anything: the decision uses the verified signed header.
        fixture.Stack.AttackerSql($"UPDATE form_records SET record_status = 'Draft', submitted_on = NULL WHERE form_record_id = '{record:D}';");
        var edit = await EditAsync(s, s.Clerk, record, new() { ["family_name"] = "Unlocked-By-DBA" }, Revision(submitted));
        Assert.Equal("RECORD_NOT_EDITABLE", edit.ErrorCode);
        var reread = await ViewAsync(s, s.Clerk, record);
        Assert.Equal("Submitted", Status(reread));
        // The next verified read repaired the projection from the signed header.
        Assert.Equal("Submitted", fixture.Stack.AttackerSql($"SELECT record_status FROM form_records WHERE form_record_id = '{record:D}';").Trim());

        // (2) Hiding a Draft record by faking Archived in the projection only affects the unsigned list, and opening the record repairs it.
        var (s2, draftRecord) = await AccusedAsync("c6-tamper-hide");
        fixture.Stack.AttackerSql($"UPDATE form_records SET record_status = 'Archived' WHERE form_record_id = '{draftRecord:D}';");
        Assert.Equal("Draft", Status(await ViewAsync(s2, s2.Clerk, draftRecord)));
        Assert.Contains(draftRecord, await ListedAsync(s2, s2.Clerk));

        // (3) Altering the signed header row itself breaks verification: nothing is released, no transition can be built on it.
        var head = AuthorityOracle.Head(fixture.Stack, record);
        fixture.Stack.AttackerSql($"UPDATE form_record_revisions SET record_status = 'Draft', submitted_on = NULL WHERE form_record_id = '{record:D}' AND content_change_operation_id = '{head.OperationId:D}';");
        var tampered = await s.ProjectionAsync(s.Clerk, record);
        Assert.Equal(HttpStatusCode.Locked, tampered.Status);
        var reopen = await TransitionAsync(s, s.Clerk, record, "reopen", head.ContentRevision, "Submitted", "tampered");
        Assert.Equal(HttpStatusCode.Locked, reopen.Status);
        Assert.Equal(head.ContentRevision, AuthorityOracle.Head(fixture.Stack, record).ContentRevision);
    }
}
