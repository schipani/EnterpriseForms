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

    // ---- AC-2 and M1 regression coverage (C-46) ----

    /// <summary>Recreates only the api container with the Testing-only business bypass of the contributor lifecycle lock on or off.</summary>
    private void SetBusinessContributorLockBypass(bool bypass)
    {
        Environment.SetEnvironmentVariable("API_TESTING_BYPASS_CONTRIBUTOR_LIFECYCLE_LOCK", bypass ? "true" : "false");
        fixture.Stack.Compose("up -d --no-deps --wait api", 300);
        Assert.Contains(bypass ? "true" : "false", fixture.Stack.Compose("exec -T api printenv Testing__BypassContributorLifecycleLock", 60), StringComparison.Ordinal);
    }

    private string AuthorityOperationCount(Guid record) =>
        fixture.Stack.AuthorityObserverSql($"SELECT count(*) FROM content_change_operations WHERE form_record_id = '{record:D}';").Trim();

    /// <summary>
    /// AC-2: with the business API's own lifecycle lock switched off for contributors (a compromised or bypassed business
    /// runtime), a contributor with a valid edit share and the contributor-write switch on can edit the Draft record, but the
    /// Authorization Authority refuses the same write once the record is Submitted or Archived — decided from the signed status
    /// of the accepted header. The Authority stores no operation for the refused attempt.
    /// </summary>
    [Fact]
    public async Task WithTheBusinessLockBypassedTheAuthorityAloneRefusesContributorWritesToLockedRecords()
    {
        var (s, record) = await AccusedAsync("c6-ac2-bypass");
        try
        {
            await SetContributorWritesAsync(true);
            var share = await fixture.Api.PostAsync($"/api/v1/form-records/{record:D}/shares", s.Token(s.Clerk), new { recipientUserId = s.Designer.AuthorityUserId, recipientTeamId = (Guid?)null, recipientRole = "Contributor", fieldKeys = new[] { "given_name" } });
            Assert.True(share.IsSuccess, share.Body);
            await s.WaitGateReadyAsync(TimeSpan.FromSeconds(40));
            SetBusinessContributorLockBypass(true);

            // Control: through the bypassed business API the contributor's write to the Draft record is accepted.
            var draft = await s.WaitProjectionAsync(s.Designer, record, TimeSpan.FromSeconds(60));
            var control = await EditAsync(s, s.Designer, record, new() { ["given_name"] = "Draft-Edit" }, Revision(draft.Root));
            Assert.True(control.IsSuccess, control.Body);

            foreach (var transition in new[] { "submit", "archive" })
            {
                if (transition == "archive")
                {
                    await StepAsync(s, record, "reopen", "test the archived lock too");
                }

                var locked = await StepAsync(s, record, transition);
                var operationsBefore = AuthorityOperationCount(record);
                var contributorView = await s.WaitProjectionAsync(s.Designer, record, TimeSpan.FromSeconds(60));
                var attempt = await EditAsync(s, s.Designer, record, new() { ["given_name"] = "Bypass-Attempt" }, Revision(contributorView.Root));
                Assert.Equal(HttpStatusCode.Conflict, attempt.Status);
                Assert.Equal("RECORD_NOT_EDITABLE", attempt.ErrorCode);
                Assert.Contains("Authorization Authority", attempt.Body, StringComparison.Ordinal); // refused by the Authority, not by the bypassed business check
                Assert.Equal(operationsBefore, AuthorityOperationCount(record)); // nothing prepared, signed or stored
                Assert.Equal(Revision(locked), AuthorityOracle.Head(fixture.Stack, record).ContentRevision);
                var values = (await ViewAsync(s, s.Clerk, record)).GetProperty("record").GetProperty("values").EnumerateArray().ToDictionary(v => v.GetProperty("fieldKey").GetString()!, v => v.GetProperty("stringValue").GetString()!);
                Assert.Equal("Draft-Edit", values["given_name"]);
            }
        }
        finally
        {
            SetBusinessContributorLockBypass(false);
            await SetContributorWritesAsync(null);
        }
    }

    /// <summary>
    /// Regression: archiving while a field's encryption migration has not yet reached the record. The archive candidate copies
    /// the legacy plaintext row verbatim, so the Authority's protection barrier refuses it (fail closed: nothing committed,
    /// status unchanged, nothing decrypted). Once the worker converts the record the same archive succeeds and the value is
    /// preserved, now encrypted. The worker is held (paused container) to make the window deterministic.
    /// </summary>
    [Fact]
    public async Task ArchivingWhileAProtectionMigrationIsPendingFailsClosedAndSucceedsAfterTheMigration()
    {
        var (s, record) = await AccusedAsync("c6-migration-pending");
        var dateField = s.AccusedFields["date_of_birth"];
        var before = await ViewAsync(s, s.Clerk, record);
        fixture.Stack.Pause("worker");
        try
        {
            var strengthened = await fixture.Api.PostAsync($"/api/v1/form-definitions/{s.AccusedFormId:D}/fields/{dateField:D}/protection", s.Token(s.Designer), new { targetClassification = "Confidential", targetProtectionMode = "ApplicationEncrypted", changeReason = "dates of birth are personal data", expectedProtectionRevision = 1 });
            Assert.True(strengthened.IsSuccess, strengthened.Body);

            var refused = await TransitionAsync(s, s.Clerk, record, "archive", Revision(before), "Draft");
            Assert.Equal(HttpStatusCode.Conflict, refused.Status);
            Assert.Equal("FIELD_PROTECTION_MIGRATION_PENDING", refused.ErrorCode);
            Assert.Equal(Revision(before), AuthorityOracle.Head(fixture.Stack, record).ContentRevision);
            Assert.Equal("Draft", Status(await ViewAsync(s, s.Clerk, record)));
        }
        finally
        {
            fixture.Stack.Unpause("worker");
        }

        var deadline = DateTime.UtcNow.AddSeconds(240);
        JsonElement protection;
        do
        {
            var status = await fixture.Api.GetAsync($"/api/v1/form-definitions/{s.AccusedFormId:D}/fields/{dateField:D}/protection", s.Token(s.Designer));
            protection = status.IsSuccess ? status.Root.Clone() : default;
            if (protection.ValueKind == JsonValueKind.Object && protection.GetProperty("fullyEncrypted").GetBoolean())
            {
                break;
            }

            await Task.Delay(1000, TestContext.Current.CancellationToken);
        }
        while (DateTime.UtcNow < deadline);
        Assert.True(protection.ValueKind == JsonValueKind.Object && protection.GetProperty("fullyEncrypted").GetBoolean(), "migration did not complete: " + protection.GetRawText());

        var archived = await StepAsync(s, record, "archive", "closing after the migration");
        Assert.Equal("Archived", Status(archived));
        var date = archived.GetProperty("record").GetProperty("values").EnumerateArray().Single(v => v.GetProperty("fieldKey").GetString() == "date_of_birth");
        Assert.Equal("1990-02-28", date.GetProperty("stringValue").GetString());
        Assert.True(date.GetProperty("applicationEncrypted").GetBoolean());
    }

    /// <summary>
    /// Regression: an upload processed while the record was a Draft cannot be committed after the record is locked. Submit
    /// waits for uploads in flight (OD-04); Archive does not, and the later commit is refused (RECORD_NOT_EDITABLE) with the
    /// session left Processed and no document added. After Unarchive the same session commits (until it expires).
    /// </summary>
    [Fact]
    public async Task AnUploadCannotCompleteIntoALockedRecordAndCanAfterUnlocking()
    {
        var (s, record) = await AccusedAsync("c6-upload-after-lock");
        var session = await s.TransferAsync(s.Clerk, record, "late.txt", "text/plain", StageBScenario.SyntheticBytes(512, 11));
        Assert.Equal("Processed", session.GetProperty("sessionState").GetString());
        var sessionId = session.GetProperty("uploadSessionId").GetGuid();

        var view = await ViewAsync(s, s.Clerk, record);
        var submit = await TransitionAsync(s, s.Clerk, record, "submit", Revision(view), "Draft");
        Assert.Equal("UPLOADS_IN_PROGRESS", submit.ErrorCode);

        var archived = await StepAsync(s, record, "archive");
        var commit = await fixture.Api.PostAsync($"/api/v1/upload-sessions/{sessionId:D}/commit", s.Token(s.Clerk), new { });
        Assert.Equal(HttpStatusCode.Conflict, commit.Status);
        Assert.Equal("RECORD_NOT_EDITABLE", commit.ErrorCode);
        Assert.Equal("Processed", (await fixture.Api.GetAsync($"/api/v1/upload-sessions/{sessionId:D}", s.Token(s.Clerk))).Root.GetProperty("sessionState").GetString());
        Assert.Equal(Revision(archived), AuthorityOracle.Head(fixture.Stack, record).ContentRevision);
        Assert.Empty((await ViewAsync(s, s.Clerk, record)).GetProperty("documents").EnumerateArray());

        await StepAsync(s, record, "unarchive");
        var committed = await fixture.Api.PostAsync($"/api/v1/upload-sessions/{sessionId:D}/commit", s.Token(s.Clerk), new { });
        Assert.True(committed.IsSuccess, committed.Body);
        Assert.Single((await ViewAsync(s, s.Clerk, record)).GetProperty("documents").EnumerateArray());
        Assert.Equal("Submitted", Status(await StepAsync(s, record, "submit")));
    }

    /// <summary>
    /// Regression: replaying a successful transition with the same Idempotency-Key (a retry after a lost response) returns the
    /// committed result without staging anything or writing a second audit event; the same request with a new key is refused
    /// as stale, never applied twice.
    /// </summary>
    [Fact]
    public async Task ReplayingASuccessfulTransitionIsIdempotentAndANewKeyIsRefusedAsStale()
    {
        var (s, record) = await AccusedAsync("c6-replay");
        var view = await ViewAsync(s, s.Clerk, record);
        var key = "c6-replay-" + Guid.NewGuid().ToString("N");
        var first = await TransitionAsync(s, s.Clerk, record, "submit", Revision(view), "Draft", idempotencyKey: key);
        Assert.True(first.IsSuccess, first.Body);
        var operationsAfterFirst = AuthorityOperationCount(record);

        var replay = await TransitionAsync(s, s.Clerk, record, "submit", Revision(view), "Draft", idempotencyKey: key);
        Assert.True(replay.IsSuccess, replay.Body);
        Assert.Equal(first.Root.GetProperty("committedOperationId").GetGuid(), replay.Root.GetProperty("committedOperationId").GetGuid());
        Assert.Equal(first.Root.GetProperty("contentRevision").GetInt64(), replay.Root.GetProperty("contentRevision").GetInt64());
        Assert.Equal("Submitted", replay.Root.GetProperty("status").GetString());
        Assert.Equal("Draft", replay.Root.GetProperty("previousStatus").GetString());
        Assert.Equal(operationsAfterFirst, AuthorityOperationCount(record));

        var fresh = await TransitionAsync(s, s.Clerk, record, "submit", Revision(view), "Draft");
        Assert.Equal(HttpStatusCode.Conflict, fresh.Status);
        Assert.Contains(fresh.ErrorCode, new[] { "CONTENT_REVISION_CONFLICT", "LIFECYCLE_STATE_CONFLICT" });
        Assert.Equal(Revision(view) + 1, AuthorityOracle.Head(fixture.Stack, record).ContentRevision);
        Assert.Single(await ActionsAsync(s, record), a => a == "RECORD_SUBMITTED");
    }
}
