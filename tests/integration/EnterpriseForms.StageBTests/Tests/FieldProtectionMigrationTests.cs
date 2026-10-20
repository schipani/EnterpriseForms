using System.Net;
using System.Text.Json;
using EnterpriseForms.SecurityTests.Harness;
using EnterpriseForms.StageBTests.Harness;
using Xunit;

namespace EnterpriseForms.StageBTests.Tests;

/// <summary>
/// Background plaintext→encrypted migration (B-28) on the real stack: counts before, authority-governed reprotection of
/// every record, concurrent user edits, worker interruption, authority outage, hostile row tampering, forged completion,
/// retry, and the "fully encrypted" claim only after verification.
/// </summary>
[Collection(StageBStackCollectionDefinition.Name)]
public sealed class FieldProtectionMigrationTests(StageBFixture fixture)
{
    private const string NoOperation = "00000000-0000-0000-0000-000000000000";

    private sealed record Workspace(StageBScenario Scenario, Guid FormId, Guid FieldId, Guid CountId, List<Guid> Records);

    /// <summary>A form with a plaintext Internal String field and a plaintext Integer field, published, with <paramref name="recordCount"/> records holding both.</summary>
    private async Task<Workspace> PlaintextWorkspaceAsync(string name, int recordCount)
    {
        var s = new StageBScenario(fixture, name);
        await s.BootstrapAsync();
        var formId = await s.CreateFormAsync("Migrate " + s.Name, s.Clerk);
        var noteId = (await fixture.Api.PostAsync($"/api/v1/form-definitions/{formId:D}/fields", s.Token(s.Designer), new { fieldKey = "plain_note", fieldDisplayName = "Plain note", fieldDescription = string.Empty, fieldDataType = "String", options = (object?)null, fieldClassification = "Internal", fieldProtectionMode = "Standard" })).Root.GetProperty("fieldDefinitionId").GetGuid();
        var countId = (await fixture.Api.PostAsync($"/api/v1/form-definitions/{formId:D}/fields", s.Token(s.Designer), new { fieldKey = "exhibit_count", fieldDisplayName = "Exhibit count", fieldDescription = string.Empty, fieldDataType = "Integer", options = (object?)null, fieldClassification = "Internal", fieldProtectionMode = "Standard" })).Root.GetProperty("fieldDefinitionId").GetGuid();
        await s.SaveDraftAsync(formId, StageBScenario.Layout("Migrate", StageBScenario.Section("main", "Main", StageBScenario.Field(noteId, "text", "Plain note", 4, required: true), StageBScenario.Field(countId, "number", "Exhibit count", 2))));
        await s.PublishAsync(formId, "v1 plaintext");
        var records = new List<Guid>();
        for (var i = 0; i < recordCount; i++)
        {
            var created = await fixture.Api.PostAsync($"/api/v1/form-definitions/{formId:D}/records", s.Token(s.Clerk), new { values = new Dictionary<string, string> { ["plain_note"] = $"note {i} of {s.Name}", ["exhibit_count"] = i.ToString(System.Globalization.CultureInfo.InvariantCulture) }, ownerTeamId = (Guid?)null });
            Assert.Equal(HttpStatusCode.Created, created.Status);
            records.Add(created.Root.GetProperty("formRecordId").GetGuid());
        }

        foreach (var record in records)
        {
            await s.WaitProjectionAsync(s.Clerk, record);
        }

        return new Workspace(s, formId, noteId, countId, records);
    }

    private Task<HttpResult> StrengthenAsync(Workspace w, long expectedRevision = 1) =>
        fixture.Api.PostAsync($"/api/v1/form-definitions/{w.FormId:D}/fields/{w.FieldId:D}/protection", w.Scenario.Token(w.Scenario.Designer), new { targetClassification = "Confidential", targetProtectionMode = "ApplicationEncrypted", changeReason = "notes hold personal data", expectedProtectionRevision = expectedRevision });

    private Task<HttpResult> StatusAsync(Workspace w) => fixture.Api.GetAsync($"/api/v1/form-definitions/{w.FormId:D}/fields/{w.FieldId:D}/protection", w.Scenario.Token(w.Scenario.Designer));

    private async Task<JsonElement> WaitMigrationAsync(Workspace w, Func<JsonElement, bool> done, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(180));
        JsonElement last = default;
        while (DateTime.UtcNow < deadline)
        {
            var status = await StatusAsync(w);
            if (status.Status == HttpStatusCode.ServiceUnavailable)
            {
                await Task.Delay(1000, TestContext.Current.CancellationToken); // the designer's status itself needs the authority (design permission); poll again
                continue;
            }

            Assert.True(status.IsSuccess, status.Body);
            last = status.Root.Clone();
            if (done(last))
            {
                return last;
            }

            await Task.Delay(1000, TestContext.Current.CancellationToken);
        }

        throw new TimeoutException("migration did not reach the expected state: " + last.GetRawText());
    }

    private static string MigrationState(JsonElement status) => status.GetProperty("migration").GetProperty("migrationState").GetString()!;

    private string PlaintextRowsInLatestSnapshots(Workspace w) =>
        fixture.Stack.AttackerSql($@"SELECT count(*) FROM field_string_values v WHERE v.field_definition_id = '{w.FieldId:D}' AND v.content_change_operation_id = (SELECT COALESCE(f.accepted_content_change_operation_id, (SELECT r.content_change_operation_id FROM form_record_revisions r WHERE r.form_record_id = v.form_record_id ORDER BY r.content_revision DESC LIMIT 1)) FROM form_records f WHERE f.form_record_id = v.form_record_id);").Trim();

    /// <summary>
    /// form_records.accepted_content_change_operation_id is an untrusted business projection. A hostile business
    /// PostgreSQL administrator who forges it — alone, or consistently with record_revision and a bogus staged header —
    /// can at most skew the local "legacy plaintext" estimate. It never selects the content that is served (reads verify
    /// against the authority head), and it cannot make a migration claim completion: the post-check verifies every record
    /// of the form against its authority-accepted head and picks up the record the forgery hid from the enumeration.
    /// </summary>
    [Fact]
    public async Task AForgedAcceptedOperationProjectionCannotSelectContentHideARecordOrForgeMigrationCompletion()
    {
        var w = await PlaintextWorkspaceAsync("forged-pointer", 4);
        string Accepted(Guid record) => fixture.Stack.AttackerSql($"SELECT accepted_content_change_operation_id FROM form_records WHERE form_record_id = '{record:D}';").Trim();

        // (a) A single forged column (dangling pointer) is not corroborated by a staged header and is ignored by the estimate (checked via the enumeration below).
        fixture.Stack.AttackerSql($"UPDATE form_records SET accepted_content_change_operation_id = '{Guid.CreateVersion7():D}' WHERE form_record_id = '{w.Records[0]:D}';");

        // (b) Pointer aimed at a planted, never-accepted candidate carrying a different value: reads still serve the accepted value.
        var planted = Guid.CreateVersion7();
        var record3 = w.Records[3];
        var accepted3 = Accepted(record3);
        fixture.Stack.AttackerSql(PlantCandidateSql(record3, accepted3, planted, w.FieldId, "forged note value", withValue: true));
        fixture.Stack.AttackerSql($"UPDATE form_records SET accepted_content_change_operation_id = '{planted:D}', record_revision = record_revision + 1 WHERE form_record_id = '{record3:D}';");
        var read3 = await w.Scenario.WaitProjectionAsync(w.Scenario.Clerk, record3);
        Assert.Equal($"note 3 of {w.Scenario.Name}", read3.Root.GetProperty("record").GetProperty("values").EnumerateArray().Single(v => v.GetProperty("fieldKey").GetString() == "plain_note").GetProperty("stringValue").GetString());

        // (c) Consistent forgery that hides record 1 from the estimate: a bogus header without the field's row, record_revision and pointer aligned to it.
        var bogus = Guid.CreateVersion7();
        var record1 = w.Records[1];
        fixture.Stack.AttackerSql(PlantCandidateSql(record1, Accepted(record1), bogus, w.FieldId, null, withValue: false));
        fixture.Stack.AttackerSql($"UPDATE form_records SET accepted_content_change_operation_id = '{bogus:D}', record_revision = record_revision + 1 WHERE form_record_id = '{record1:D}';");

        // Strengthening enumerates from the skewed estimate, yet the worker's post-check verifies every record of the form against the authority head.
        var strengthened = await StrengthenAsync(w);
        Assert.True(strengthened.IsSuccess, strengthened.Body);
        // The estimate-based enumeration: record 0 is still counted (the dangling pointer was ignored), record 3 is counted
        // (its planted candidate also holds plaintext), and only record 1 is hidden — the documented residual of the untrusted estimate.
        Assert.Equal(3, strengthened.Root.GetProperty("migration").GetProperty("totalRecordCount").GetInt32());
        var done = await WaitMigrationAsync(w, s => MigrationState(s) is "Completed" or "Failed", TimeSpan.FromSeconds(300));
        Assert.Equal("Completed", MigrationState(done));
        Assert.Equal(4, done.GetProperty("migration").GetProperty("totalRecordCount").GetInt32()); // the hidden record was found and added
        Assert.Equal(4, done.GetProperty("migration").GetProperty("completedRecordCount").GetInt32());

        // Every record — including the hidden one and the one with a planted candidate — is encrypted in its authority-accepted head.
        foreach (var record in w.Records)
        {
            var projection = await w.Scenario.WaitProjectionAsync(w.Scenario.Clerk, record);
            var note = projection.Root.GetProperty("record").GetProperty("values").EnumerateArray().Single(v => v.GetProperty("fieldKey").GetString() == "plain_note");
            Assert.True(note.GetProperty("applicationEncrypted").GetBoolean(), "record " + record.ToString("D") + " is not encrypted in its accepted head");
            Assert.StartsWith("note ", note.GetProperty("stringValue").GetString(), StringComparison.Ordinal);
        }
    }

    /// <summary>Hostile-DBA SQL: copies the record's accepted header (and optionally its value row of the field with a new value) under a new operation one revision ahead.</summary>
    private static string PlantCandidateSql(Guid record, string acceptedOperation, Guid newOperation, Guid fieldId, string? value, bool withValue) => $@"DO $$
DECLARE header_cols text; value_cols text;
BEGIN
  SELECT string_agg(quote_ident(column_name), ',') INTO header_cols FROM information_schema.columns
   WHERE table_schema = current_schema() AND table_name = 'form_record_revisions' AND column_name NOT IN ('form_record_revision_id','content_change_operation_id','content_revision','row_revision','candidate_state');
  EXECUTE format('INSERT INTO form_record_revisions (form_record_revision_id, content_change_operation_id, content_revision, row_revision, candidate_state, %s) SELECT %L::uuid, %L::uuid, content_revision + 1, row_revision + 1, ''Staged'', %s FROM form_record_revisions WHERE form_record_id = %L::uuid AND content_change_operation_id = %L::uuid',
    header_cols, '{Guid.CreateVersion7():D}', '{newOperation:D}', header_cols, '{record:D}', '{acceptedOperation}');
  IF {(withValue ? "true" : "false")} THEN
    SELECT string_agg(quote_ident(column_name), ',') INTO value_cols FROM information_schema.columns
     WHERE table_schema = current_schema() AND table_name = 'field_string_values' AND column_name NOT IN ('field_string_value_id','content_change_operation_id','content_revision','row_revision','string_value');
    EXECUTE format('INSERT INTO field_string_values (field_string_value_id, content_change_operation_id, content_revision, row_revision, string_value, %s) SELECT %L::uuid, %L::uuid, content_revision + 1, row_revision + 1, %L, %s FROM field_string_values WHERE form_record_id = %L::uuid AND field_definition_id = %L::uuid AND content_change_operation_id = %L::uuid',
      value_cols, '{Guid.CreateVersion7():D}', '{newOperation:D}', '{value ?? string.Empty}', value_cols, '{record:D}', '{fieldId:D}', '{acceptedOperation}');
  END IF;
END $$;";

    /// <summary>
    /// Regression (InterruptedAndTampered… intermittent failure): the authority stores a prepared reprotection operation
    /// and dies before answering. The worker sees a transport failure, parks the migration and retries; its rebuilt
    /// candidate differs from the stored one (fresh row identities, fresh ciphertext), so reusing the attempt's
    /// idempotency key is refused with IDEMPOTENCY_CONFLICT. That must move the record to a fresh attempt — never fail
    /// a healthy record — and the field must end fully encrypted through the authority. Deterministic: one-shot,
    /// Testing-only crash point CrashAfterPrepareCommit (same mechanism as SEC-P09).
    /// </summary>
    [Fact]
    public async Task AnInterruptedPrepareNeverFailsAHealthyRecordTheNextAttemptUsesAFreshOperation()
    {
        var w = await PlaintextWorkspaceAsync("interrupted-prepare", 3);
        var arm = await fixture.Authority.OperatorAsync(HttpMethod.Post, "/internal/v1/operator/fault-injection", new { fault_name = "CrashAfterPrepareCommit" });
        Assert.True(arm.IsSuccess, arm.Body);
        try
        {
            var strengthened = await StrengthenAsync(w);
            Assert.True(strengthened.IsSuccess, strengthened.Body);
            await w.Scenario.WaitGateReadyAsync();
            var done = await WaitMigrationAsync(w, s => MigrationState(s) is "Completed" or "Failed", TimeSpan.FromSeconds(300));
            var items = fixture.Stack.AttackerSql($@"SELECT string_agg(i.item_state || '|' || i.attempt_count || '|' || coalesce(i.last_error_code, ''), ' ; ') FROM field_protection_migration_items i JOIN field_protection_migrations m ON m.field_protection_migration_id = i.field_protection_migration_id WHERE m.field_definition_id = '{w.FieldId:D}';").Trim();
            Assert.True(MigrationState(done) == "Completed", "migration did not complete; items: " + items + "; status: " + done.GetRawText());
            Assert.Equal(3, done.GetProperty("migration").GetProperty("completedRecordCount").GetInt32());
            Assert.Equal(0, done.GetProperty("migration").GetProperty("failedRecordCount").GetInt32());
            // The interrupted record needed more than one attempt: the crash really happened between Prepare and its response.
            Assert.True(int.Parse(fixture.Stack.AttackerSql($@"SELECT max(i.attempt_count) FROM field_protection_migration_items i JOIN field_protection_migrations m ON m.field_protection_migration_id = i.field_protection_migration_id WHERE m.field_definition_id = '{w.FieldId:D}';").Trim(), System.Globalization.CultureInfo.InvariantCulture) >= 2, "expected a retried item; items: " + items);
            var verified = await WaitMigrationAsync(w, s => s.GetProperty("fullyEncrypted").GetBoolean());
            Assert.Equal("Encrypted", verified.GetProperty("migrationState").GetString());
            foreach (var record in w.Records)
            {
                var projection = await w.Scenario.WaitProjectionAsync(w.Scenario.Clerk, record);
                Assert.True(projection.Root.GetProperty("record").GetProperty("values").EnumerateArray().Single(v => v.GetProperty("fieldKey").GetString() == "plain_note").GetProperty("applicationEncrypted").GetBoolean());
            }
        }
        finally
        {
            await fixture.Authority.OperatorAsync(HttpMethod.Post, "/internal/v1/operator/fault-injection", new { fault_name = "None" }); // disarm if the crash point was never reached
        }
    }

    [Fact]
    public async Task StrengtheningCountsPlaintextThenTheWorkerReprotectsEveryRecordThroughTheAuthorityAndOnlyThenClaimsFullEncryption()
    {
        var w = await PlaintextWorkspaceAsync("migrate", 6);
        var before = await StatusAsync(w);
        Assert.Equal("Plaintext", before.Root.GetProperty("migrationState").GetString());
        Assert.False(before.Root.GetProperty("fullyEncrypted").GetBoolean());
        Assert.Equal("6", PlaintextRowsInLatestSnapshots(w));
        var revisionsBefore = w.Records.ToDictionary(r => r, r => long.Parse(fixture.Stack.AttackerSql($"SELECT max(content_revision) FROM form_record_revisions WHERE form_record_id = '{r:D}';").Trim(), System.Globalization.CultureInfo.InvariantCulture));

        // 1. Strengthening: the authority accepts the requirement first, then the migration is queued with the "before" counts; nothing claims encryption yet.
        var strengthened = await StrengthenAsync(w);
        Assert.True(strengthened.IsSuccess, strengthened.Body);
        Assert.Equal("ApplicationEncrypted", strengthened.Root.GetProperty("fieldProtectionMode").GetString());
        Assert.False(strengthened.Root.GetProperty("fullyEncrypted").GetBoolean());
        var migration = strengthened.Root.GetProperty("migration");
        Assert.Equal(6, migration.GetProperty("totalRecordCount").GetInt32());
        Assert.Equal(6, migration.GetProperty("totalValueCount").GetInt32());
        Assert.Contains(MigrationState(strengthened.Root), new[] { "Queued", "Running" });
        var commandId = migration.GetProperty("authorityCommandId").GetGuid();
        Assert.NotEqual(Guid.Empty, commandId);
        Assert.Equal("ApplicationEncrypted|" + commandId.ToString("D"), fixture.Stack.AttackerSql($"SELECT field_protection_mode || '|' || protection_authority_command_id FROM field_definitions WHERE field_definition_id = '{w.FieldId:D}';").Trim());

        // A second strengthening while the migration runs is refused (no overlapping migrations).
        var overlapping = await fixture.Api.PostAsync($"/api/v1/form-definitions/{w.FormId:D}/fields/{w.FieldId:D}/protection", w.Scenario.Token(w.Scenario.Designer), new { targetClassification = "Restricted", targetProtectionMode = "ApplicationEncrypted", changeReason = "raise again", expectedProtectionRevision = 2 });
        Assert.True(overlapping.ErrorCode is "MIGRATION_IN_PROGRESS" or null && (overlapping.ErrorCode is not null || overlapping.IsSuccess), overlapping.Body);

        // 2. The worker re-protects every record; the field reports Encrypted + fullyEncrypted only when complete and verified.
        var done = await WaitMigrationAsync(w, s => s.GetProperty("fullyEncrypted").GetBoolean());
        Assert.Equal("Encrypted", done.GetProperty("migrationState").GetString());
        Assert.Equal("Completed", MigrationState(done));
        Assert.Equal(6, done.GetProperty("migration").GetProperty("completedRecordCount").GetInt32());
        Assert.Equal(0, done.GetProperty("migration").GetProperty("failedRecordCount").GetInt32());
        Assert.Equal(0, done.GetProperty("migration").GetProperty("pendingRecordCount").GetInt32());
        Assert.NotNull(done.GetProperty("migration").GetProperty("verifiedOn").GetString());
        Assert.Equal(0, done.GetProperty("legacyPlaintextRecordCount").GetInt32());

        // 3. Business PostgreSQL: no plaintext copy of the field in any latest snapshot; the Integer field is untouched; each record advanced by exactly one revision.
        Assert.Equal("0", PlaintextRowsInLatestSnapshots(w));
        foreach (var record in w.Records)
        {
            var after = long.Parse(fixture.Stack.AttackerSql($"SELECT max(content_revision) FROM form_record_revisions WHERE form_record_id = '{record:D}';").Trim(), System.Globalization.CultureInfo.InvariantCulture);
            Assert.Equal(revisionsBefore[record] + 1, after);
            var latest = fixture.Stack.AttackerSql($"SELECT content_change_operation_id FROM form_record_revisions WHERE form_record_id = '{record:D}' ORDER BY content_revision DESC LIMIT 1;").Trim();
            Assert.Equal("1", fixture.Stack.AttackerSql($"SELECT count(*) FROM field_encrypted_values WHERE form_record_id = '{record:D}' AND field_definition_id = '{w.FieldId:D}' AND content_change_operation_id = '{latest}';").Trim());
            Assert.Equal("1", fixture.Stack.AttackerSql($"SELECT count(*) FROM field_integer_values WHERE form_record_id = '{record:D}' AND field_definition_id = '{w.CountId:D}' AND content_change_operation_id = '{latest}';").Trim());
        }

        // The historical plaintext rows of earlier operations are immutable history, never rewritten.
        Assert.Equal("6", fixture.Stack.AttackerSql($"SELECT count(*) FROM field_string_values WHERE field_definition_id = '{w.FieldId:D}';").Trim());

        // 4. Every record still verifies through the authority and decrypts for its owner; the reader is still denied.
        for (var i = 0; i < w.Records.Count; i++)
        {
            var projection = await w.Scenario.WaitProjectionAsync(w.Scenario.Clerk, w.Records[i]);
            var value = projection.Root.GetProperty("record").GetProperty("values").EnumerateArray().Single(v => v.GetProperty("fieldKey").GetString() == "plain_note");
            Assert.Equal($"note {i} of {w.Scenario.Name}", value.GetProperty("stringValue").GetString());
            Assert.True(value.GetProperty("applicationEncrypted").GetBoolean());
            Assert.False(value.GetProperty("legacyPlaintext").GetBoolean());
            Assert.Equal("Verified", projection.Root.GetProperty("record").GetProperty("integrityStatus").GetString());
        }

        Assert.Contains((await w.Scenario.ProjectionAsync(w.Scenario.Reader, w.Records[0])).Status, new[] { HttpStatusCode.Forbidden, HttpStatusCode.NotFound });

        // 5. Published v1 is unchanged; the audit trail names the start, every record and the completion without plaintext.
        var versions = await fixture.Api.GetAsync($"/api/v1/form-definitions/{w.FormId:D}/versions", w.Scenario.Token(w.Scenario.Designer));
        Assert.Single(versions.Root.EnumerateArray());
        var activity = await fixture.Api.GetAsync("/api/v1/my-activity?limit=200", w.Scenario.Token(w.Scenario.Designer));
        var actions = activity.Root.EnumerateArray().Select(e => e.GetProperty("eventAction").GetString()).ToList();
        Assert.Contains("FIELD_PROTECTION_MIGRATION_STARTED", actions);
        Assert.Contains("FIELD_PROTECTION_MIGRATION_COMPLETED", actions);
        Assert.Equal(6, actions.Count(a => a == "FIELD_VALUES_REPROTECTED"));
        Assert.DoesNotContain("note 0 of", activity.Body, StringComparison.Ordinal);

        // 6. The authority recorded each reprotection operation under the command, and the business store holds nothing that pretends otherwise.
        Assert.Equal("6", fixture.Stack.AttackerSql($"SELECT count(*) FROM field_protection_migration_items WHERE item_state = 'Completed' AND completed_by_user_commit = false AND field_protection_migration_id = (SELECT field_protection_migration_id FROM field_protection_migrations WHERE field_definition_id = '{w.FieldId:D}');").Trim());
    }

    [Fact]
    public async Task TheAuthorityRefusesReprotectionWithoutAnEffectiveCommandAndRefusesAnyOtherChangeUnderOne()
    {
        var w = await PlaintextWorkspaceAsync("migrate-oracle", 1);
        var record = w.Records[0];
        var workload = TestIdentity.WorkloadToken();
        var operationId = Guid.CreateVersion7();
        var prepare = new { organization_id = w.Scenario.OrganizationId, form_record_id = record, operation_id = operationId, expected_authorization_revision = 1, expected_content_revision = 1, expected_manifest_root_base64 = Convert.ToBase64String(new byte[32]), candidate_items = Array.Empty<object>() };

        // No delegated user and no command: 401. A workload cannot sign anything on its own.
        var anonymous = await fixture.Authority.SendAsync(HttpMethod.Post, "/internal/v1/content-changes/prepare", workload, prepare, null, "oracle-" + Guid.NewGuid().ToString("N"));
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.Status);

        // A made-up command id, or a command of another kind, authorizes nothing.
        var forged = await fixture.Authority.SendAsync(HttpMethod.Post, "/internal/v1/content-changes/prepare", workload, new { organization_id = w.Scenario.OrganizationId, form_record_id = record, operation_id = operationId, expected_authorization_revision = 1, expected_content_revision = 1, expected_manifest_root_base64 = Convert.ToBase64String(new byte[32]), candidate_items = Array.Empty<object>(), reprotection_command_id = Guid.CreateVersion7(), current_items = Array.Empty<object>() }, null, "oracle-" + Guid.NewGuid().ToString("N"));
        Assert.Equal(HttpStatusCode.Forbidden, forged.Status);
        var head = await fixture.Authority.SendAsync(HttpMethod.Get, $"/internal/v1/content-revisions/{w.Scenario.OrganizationId:D}/{record:D}?reprotectionCommandId={Guid.CreateVersion7():D}", workload, null);
        Assert.Equal(HttpStatusCode.Forbidden, head.Status);

        // A real command plus a delegated user is not a valid shape either (reprotection is never a user operation).
        var strengthened = await StrengthenAsync(w);
        Assert.True(strengthened.IsSuccess, strengthened.Body);
        var commandId = strengthened.Root.GetProperty("migration").GetProperty("authorityCommandId").GetGuid();
        var mixed = await fixture.Authority.SendAsync(HttpMethod.Post, "/internal/v1/content-changes/prepare", workload, new { organization_id = w.Scenario.OrganizationId, form_record_id = record, operation_id = operationId, expected_authorization_revision = 1, expected_content_revision = 1, expected_manifest_root_base64 = Convert.ToBase64String(new byte[32]), candidate_items = Array.Empty<object>(), reprotection_command_id = commandId, current_items = Array.Empty<object>() }, w.Scenario.Token(w.Scenario.Clerk), "oracle-" + Guid.NewGuid().ToString("N"));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, mixed.Status);

        // The command of one organization does not open records of another.
        var other = await PlaintextWorkspaceAsync("migrate-oracle-other", 1);
        var crossOrganization = await fixture.Authority.SendAsync(HttpMethod.Post, "/internal/v1/content-changes/prepare", workload, new { organization_id = other.Scenario.OrganizationId, form_record_id = other.Records[0], operation_id = Guid.CreateVersion7(), expected_authorization_revision = 1, expected_content_revision = 1, expected_manifest_root_base64 = Convert.ToBase64String(new byte[32]), candidate_items = Array.Empty<object>(), reprotection_command_id = commandId, current_items = Array.Empty<object>() }, null, "oracle-" + Guid.NewGuid().ToString("N"));
        Assert.Equal(HttpStatusCode.Forbidden, crossOrganization.Status);

        // The genuine migration still completes for the real record.
        var done = await WaitMigrationAsync(w, s => s.GetProperty("fullyEncrypted").GetBoolean());
        Assert.Equal("Completed", MigrationState(done));
    }

    [Fact]
    public async Task ConcurrentUserEditsAndAWorkerRestartNeitherLoseDataNorBlockCompletion()
    {
        var w = await PlaintextWorkspaceAsync("migrate-concurrent", 24);
        var strengthened = await StrengthenAsync(w);
        Assert.True(strengthened.IsSuccess, strengthened.Body);

        // While the worker runs: the clerk edits two records (one touching the migrating field, one not), and the worker is restarted mid-way.
        var editedField = w.Records[^1];
        var editedOther = w.Records[^2];
        var headField = await w.Scenario.WaitProjectionAsync(w.Scenario.Clerk, editedField);
        var edit1 = await fixture.Api.PatchAsync($"/api/v1/form-records/{editedField:D}", w.Scenario.Token(w.Scenario.Clerk), new { expectedContentRevision = headField.Root.GetProperty("record").GetProperty("contentRevision").GetInt64(), values = new Dictionary<string, string> { ["plain_note"] = "edited by the clerk during migration" } });
        Assert.True(edit1.IsSuccess || edit1.ErrorCode == "CONTENT_REVISION_CONFLICT", edit1.Body);
        if (!edit1.IsSuccess)
        {
            var again = await w.Scenario.WaitProjectionAsync(w.Scenario.Clerk, editedField);
            edit1 = await fixture.Api.PatchAsync($"/api/v1/form-records/{editedField:D}", w.Scenario.Token(w.Scenario.Clerk), new { expectedContentRevision = again.Root.GetProperty("record").GetProperty("contentRevision").GetInt64(), values = new Dictionary<string, string> { ["plain_note"] = "edited by the clerk during migration" } });
            Assert.True(edit1.IsSuccess, edit1.Body);
        }

        fixture.Stack.Compose("restart worker", 120);

        var headOther = await w.Scenario.WaitProjectionAsync(w.Scenario.Clerk, editedOther);
        var edit2 = await fixture.Api.PatchAsync($"/api/v1/form-records/{editedOther:D}", w.Scenario.Token(w.Scenario.Clerk), new { expectedContentRevision = headOther.Root.GetProperty("record").GetProperty("contentRevision").GetInt64(), values = new Dictionary<string, string> { ["exhibit_count"] = "777" } });
        Assert.True(edit2.IsSuccess || edit2.ErrorCode == "CONTENT_REVISION_CONFLICT", edit2.Body);
        if (!edit2.IsSuccess)
        {
            var again = await w.Scenario.WaitProjectionAsync(w.Scenario.Clerk, editedOther);
            edit2 = await fixture.Api.PatchAsync($"/api/v1/form-records/{editedOther:D}", w.Scenario.Token(w.Scenario.Clerk), new { expectedContentRevision = again.Root.GetProperty("record").GetProperty("contentRevision").GetInt64(), values = new Dictionary<string, string> { ["exhibit_count"] = "777" } });
            Assert.True(edit2.IsSuccess, edit2.Body);
        }

        var done = await WaitMigrationAsync(w, s => s.GetProperty("fullyEncrypted").GetBoolean(), TimeSpan.FromSeconds(300));
        Assert.Equal("Completed", MigrationState(done));
        Assert.Equal(24, done.GetProperty("migration").GetProperty("completedRecordCount").GetInt32());
        Assert.Equal("0", PlaintextRowsInLatestSnapshots(w));

        // Neither edit was lost and both records are encrypted.
        var p1 = await w.Scenario.WaitProjectionAsync(w.Scenario.Clerk, editedField);
        var v1 = p1.Root.GetProperty("record").GetProperty("values").EnumerateArray().Single(v => v.GetProperty("fieldKey").GetString() == "plain_note");
        Assert.Equal("edited by the clerk during migration", v1.GetProperty("stringValue").GetString());
        Assert.True(v1.GetProperty("applicationEncrypted").GetBoolean());
        var p2 = await w.Scenario.WaitProjectionAsync(w.Scenario.Clerk, editedOther);
        Assert.Equal("777", p2.Root.GetProperty("record").GetProperty("values").EnumerateArray().Single(v => v.GetProperty("fieldKey").GetString() == "exhibit_count").GetProperty("stringValue").GetString());
        Assert.True(p2.Root.GetProperty("record").GetProperty("values").EnumerateArray().Single(v => v.GetProperty("fieldKey").GetString() == "plain_note").GetProperty("applicationEncrypted").GetBoolean());

        // Records re-protected by the clerk's own commit are recognised as completed by a user commit, not re-signed by the worker.
        Assert.Equal("1", fixture.Stack.AttackerSql($"SELECT count(*) FROM field_protection_migration_items WHERE form_record_id = '{editedField:D}' AND item_state = 'Completed';").Trim());
    }

    [Fact]
    public async Task AuthorityOutageBlocksAndResumesAndHostileRowTamperingFailsTheRecordWithoutClaimingEncryption()
    {
        var w = await PlaintextWorkspaceAsync("migrate-adversarial", 5);
        var tampered = w.Records[2];

        // A hostile database writer alters the committed plaintext of one record before the migration starts.
        fixture.Stack.AttackerSql($"UPDATE field_string_values SET string_value = 'forged by dba' WHERE form_record_id = '{tampered:D}' AND field_definition_id = '{w.FieldId:D}';");

        // The authority is unreachable when the migration starts: nothing is attempted, the migration is Blocked with the reason, nothing is lost.
        fixture.Stack.Pause("authority");
        try
        {
            var during = await StrengthenAsync(w);
            Assert.False(during.IsSuccess, "strengthening needs the authority's command and must not succeed while it is down: " + during.Body);
        }
        finally
        {
            fixture.Stack.Unpause("authority");
        }

        await w.Scenario.WaitGateReadyAsync();
        var strengthened = await StrengthenAsync(w);
        Assert.True(strengthened.IsSuccess, strengthened.Body);
        Assert.Equal(5, strengthened.Root.GetProperty("migration").GetProperty("totalRecordCount").GetInt32());

        fixture.Stack.Pause("authority");
        try
        {
            // While the authority is unreachable every protected API (including the designer's status) answers 503; the business
            // database shows the migration parked as Blocked with the reason, and nothing marked Completed or Failed meanwhile.
            var blockedDeadline = DateTime.UtcNow.AddSeconds(120);
            string row;
            do
            {
                row = fixture.Stack.AttackerSql($"SELECT migration_state || '|' || coalesce(last_error_code, '') FROM field_protection_migrations WHERE field_definition_id = '{w.FieldId:D}';").Trim();
                if (row.StartsWith("Blocked|", StringComparison.Ordinal))
                {
                    break;
                }

                await Task.Delay(1000, TestContext.Current.CancellationToken);
            }
            while (DateTime.UtcNow < blockedDeadline);
            Assert.StartsWith("Blocked|", row, StringComparison.Ordinal);
            Assert.True(row.Length > "Blocked|".Length, row);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, (await StatusAsync(w)).Status);
        }
        finally
        {
            fixture.Stack.Unpause("authority");
        }

        // After the outage: four records complete; the tampered one fails (the authority refuses to build on rows that do not verify) and stays plaintext; the field is NOT fully encrypted.
        var ended = await WaitMigrationAsync(w, s => MigrationState(s) is "Failed" or "Completed", TimeSpan.FromSeconds(420));
        Assert.Equal("Failed", MigrationState(ended));
        Assert.Equal("EncryptionMigrationFailed", ended.GetProperty("migrationState").GetString());
        Assert.False(ended.GetProperty("fullyEncrypted").GetBoolean());
        Assert.Equal(4, ended.GetProperty("migration").GetProperty("completedRecordCount").GetInt32());
        Assert.Equal(1, ended.GetProperty("migration").GetProperty("failedRecordCount").GetInt32());
        Assert.Equal(1, ended.GetProperty("legacyPlaintextRecordCount").GetInt32());
        Assert.Equal("Failed", fixture.Stack.AttackerSql($"SELECT item_state FROM field_protection_migration_items WHERE form_record_id = '{tampered:D}';").Trim());
        Assert.Equal(HttpStatusCode.Locked, (await w.Scenario.ProjectionAsync(w.Scenario.Clerk, tampered)).Status); // the record itself is quarantined by the authority
        Assert.Contains((await fixture.Api.GetAsync("/api/v1/my-activity?limit=200", w.Scenario.Token(w.Scenario.Designer))).Root.EnumerateArray().Select(e => e.GetProperty("eventAction").GetString()), a => a == "FIELD_PROTECTION_MIGRATION_FAILED");

        // Forged completion in the business database does not make the field report as encrypted.
        fixture.Stack.AttackerSql($"UPDATE field_protection_migrations SET migration_state = 'Completed', failed_record_count = 0, completed_record_count = 5, migration_verified_on = now() WHERE field_definition_id = '{w.FieldId:D}'; UPDATE field_protection_migration_items SET item_state = 'Completed' WHERE form_record_id = '{tampered:D}';");
        var forged = await StatusAsync(w);
        Assert.False(forged.Root.GetProperty("fullyEncrypted").GetBoolean());
        Assert.NotEqual("Encrypted", forged.Root.GetProperty("migrationState").GetString());

        // Only designers may retry; a retry re-queues the failed record (the tampered record stays refused: nothing is re-signed silently).
        fixture.Stack.AttackerSql($"UPDATE field_protection_migrations SET migration_state = 'Failed', failed_record_count = 1, completed_record_count = 4, migration_verified_on = NULL WHERE field_definition_id = '{w.FieldId:D}'; UPDATE field_protection_migration_items SET item_state = 'Failed' WHERE form_record_id = '{tampered:D}';");
        Assert.Contains((await fixture.Api.PostAsync($"/api/v1/form-definitions/{w.FormId:D}/fields/{w.FieldId:D}/protection/retry", w.Scenario.Token(w.Scenario.Clerk), new { retryReason = "clerk tries" })).Status, new[] { HttpStatusCode.Forbidden, HttpStatusCode.NotFound });
        var retried = await fixture.Api.PostAsync($"/api/v1/form-definitions/{w.FormId:D}/fields/{w.FieldId:D}/protection/retry", w.Scenario.Token(w.Scenario.Designer), new { retryReason = "after investigating the record" });
        Assert.True(retried.IsSuccess, retried.Body);
        Assert.Equal("Queued", MigrationState(retried.Root));
        var retriedEnd = await WaitMigrationAsync(w, s => MigrationState(s) is "Failed" or "Completed", TimeSpan.FromSeconds(300));
        Assert.Equal("Failed", MigrationState(retriedEnd));
        Assert.False(retriedEnd.GetProperty("fullyEncrypted").GetBoolean());
    }
}
