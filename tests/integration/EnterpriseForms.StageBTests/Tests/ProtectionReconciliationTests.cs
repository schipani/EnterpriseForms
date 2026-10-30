using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using EnterpriseForms.SecurityTests.Harness;
using EnterpriseForms.StageBTests.Harness;
using Xunit;

namespace EnterpriseForms.StageBTests.Tests;

/// <summary>
/// Phase C7 M4 — G19 on the real stack (PostgreSQL ×2, SpiceDB, Vault Transit, Authority, API, Worker with the separate
/// field-protection verifier): a protection migration completes only after the Authority attests the form's complete
/// registered population; records concealed from every business listing, forged business rows, rolled-back content,
/// pending creations, archived records and retained history are each accounted for; restarts, outages and concurrent edits
/// never produce a false completion. The hostile business DBA is the attacker-sql container; Authority state is read only
/// through the read-only observer.
/// </summary>
[Collection(StageBStackCollectionDefinition.Name)]
public sealed class ProtectionReconciliationTests(StageBFixture fixture)
{
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed record Workspace(StageBScenario S, Guid FormId, Guid NoteId, Guid CountId, List<Guid> Records);

    /// <summary>A published form with a plaintext Internal note and count, and <paramref name="count"/> records holding both (marker text per record).</summary>
    private async Task<Workspace> WorkspaceAsync(string name, int count, StageBScenario? existing = null)
    {
        var s = existing ?? new StageBScenario(fixture, name);
        if (existing is null)
        {
            await s.BootstrapAsync();
        }

        var formId = await s.CreateFormAsync("Reconcile " + name + " " + s.Name, s.Clerk);
        var noteId = (await fixture.Api.PostAsync($"/api/v1/form-definitions/{formId:D}/fields", s.Token(s.Designer), new { fieldKey = "plain_note", fieldDisplayName = "Plain note", fieldDescription = string.Empty, fieldDataType = "String", options = (object?)null, fieldClassification = "Internal", fieldProtectionMode = "Standard" })).Root.GetProperty("fieldDefinitionId").GetGuid();
        var countId = (await fixture.Api.PostAsync($"/api/v1/form-definitions/{formId:D}/fields", s.Token(s.Designer), new { fieldKey = "exhibit_count", fieldDisplayName = "Exhibit count", fieldDescription = string.Empty, fieldDataType = "Integer", options = (object?)null, fieldClassification = "Internal", fieldProtectionMode = "Standard" })).Root.GetProperty("fieldDefinitionId").GetGuid();
        await s.SaveDraftAsync(formId, StageBScenario.Layout("Reconcile", StageBScenario.Section("main", "Main", StageBScenario.Field(noteId, "text", "Plain note", 4), StageBScenario.Field(countId, "number", "Exhibit count", 2))));
        await s.PublishAsync(formId, "v1 plaintext");
        var records = new List<Guid>();
        for (var i = 0; i < count; i++)
        {
            var created = await fixture.Api.PostAsync($"/api/v1/form-definitions/{formId:D}/records", s.Token(s.Clerk), new { values = new Dictionary<string, string> { ["plain_note"] = Marker(s, i), ["exhibit_count"] = i.ToString(CultureInfo.InvariantCulture) }, ownerTeamId = (Guid?)null });
            Assert.Equal(HttpStatusCode.Created, created.Status);
            records.Add(created.Root.GetProperty("formRecordId").GetGuid());
        }

        foreach (var record in records)
        {
            await s.WaitProjectionAsync(s.Clerk, record);
        }

        return new Workspace(s, formId, noteId, countId, records);
    }

    private static string Marker(StageBScenario s, int i) => $"m4-plaintext-marker-{i}-{s.Name}";

    private Task<HttpResult> StrengthenAsync(Workspace w, Guid? field = null, string classification = "Confidential") =>
        fixture.Api.PostAsync($"/api/v1/form-definitions/{w.FormId:D}/fields/{(field ?? w.NoteId):D}/protection", w.S.Token(w.S.Designer), new { targetClassification = classification, targetProtectionMode = "ApplicationEncrypted", changeReason = "M4 reconciliation proof", expectedProtectionRevision = 1 });

    private Task<HttpResult> StatusAsync(Workspace w, Guid? field = null) => fixture.Api.GetAsync($"/api/v1/form-definitions/{w.FormId:D}/fields/{(field ?? w.NoteId):D}/protection", w.S.Token(w.S.Designer));

    private Task<HttpResult> RequestVerificationAsync(Workspace w, TestUser user, string reason = "owner M4 acceptance check") =>
        fixture.Api.PostAsync($"/api/v1/form-definitions/{w.FormId:D}/fields/{w.NoteId:D}/protection/reconciliations", w.S.Token(user), new { requestReason = reason });

    private async Task<JsonElement> WaitStatusAsync(Workspace w, Func<JsonElement, bool> done, TimeSpan? timeout = null, Guid? field = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(300));
        JsonElement last = default;
        while (DateTime.UtcNow < deadline)
        {
            var status = await StatusAsync(w, field);
            if (status.Status == HttpStatusCode.ServiceUnavailable)
            {
                await Task.Delay(1000, Ct);
                continue;
            }

            Assert.True(status.IsSuccess, status.Body);
            last = status.Root.Clone();
            if (done(last))
            {
                return last;
            }

            await Task.Delay(1000, Ct);
        }

        throw new TimeoutException("protection status did not reach the expected state: " + last.GetRawText());
    }

    /// <summary>Requests a verify-only reconciliation and waits until that very reconciliation (not an earlier one) is terminal.</summary>
    private async Task<JsonElement> VerifyAndWaitAsync(Workspace w, string reason, TimeSpan? timeout = null, Action? afterRequest = null)
    {
        var requested = await RequestVerificationAsync(w, w.S.Designer, reason);
        Assert.True(requested.IsSuccess, requested.Body);
        var id = Reconciliation(requested.Root).GetProperty("fieldProtectionReconciliationId").GetGuid();
        Assert.Equal("VerifyOnly", Reconciliation(requested.Root).GetProperty("mode").GetString());
        afterRequest?.Invoke();
        return await WaitStatusAsync(w, s => Terminal(s) && Reconciliation(s).GetProperty("fieldProtectionReconciliationId").GetGuid() == id, timeout);
    }

    private static string MigrationState(JsonElement status) => status.GetProperty("migration").GetProperty("migrationState").GetString()!;

    private static JsonElement Reconciliation(JsonElement status) => status.GetProperty("reconciliation");

    private static string ReconciliationState(JsonElement status) => status.TryGetProperty("reconciliation", out var r) && r.ValueKind == JsonValueKind.Object ? r.GetProperty("state").GetString()! : string.Empty;

    private static bool Terminal(JsonElement status) => ReconciliationState(status) is "Complete" or "Incomplete" or "Unavailable" or "Superseded";

    private static JsonElement Attestation(JsonElement status) => Reconciliation(status).GetProperty("attestation");

    private static int Int(JsonElement e, string name) => e.GetProperty(name).GetInt32();

    private Guid CommandOf(Workspace w, Guid? field = null) =>
        Guid.Parse(fixture.Stack.AttackerSql($"SELECT protection_authority_command_id FROM field_definitions WHERE field_definition_id = '{(field ?? w.NoteId):D}';").Trim());

    private string Observer(string sql) => fixture.Stack.AuthorityObserverSql(sql).Trim();

    private async Task<Guid> PendingCreationAsync(Workspace w)
    {
        using var client = new HttpClient { BaseAddress = new Uri(StageBFixture.ApiBaseAddress) };
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/form-definitions/{w.FormId:D}/records") { Content = JsonContent.Create(new { values = new Dictionary<string, string> { ["plain_note"] = "pending creation", ["exhibit_count"] = "9" }, ownerTeamId = (Guid?)null }) };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", w.S.Token(w.S.Clerk));
        request.Headers.Add("Idempotency-Key", "m4-pending-" + Guid.CreateVersion7().ToString("N"));
        request.Headers.Add("X-Testing-Fault", "record-create:after-register");
        using var response = await client.SendAsync(request, Ct);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var pending = Observer($"SELECT r.resource_entity_id FROM authority_resources r JOIN record_content_heads h ON h.form_record_id = r.resource_entity_id WHERE r.organization_id = '{w.S.OrganizationId:D}' AND r.resource_type_name = 'form_record' AND r.parent_resource_entity_id = '{w.FormId:D}' AND h.committed_content_revision = 0;");
        return Guid.Parse(pending.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Single());
    }

    /// <summary>
    /// Owner list 1, 3, 4, 6, 13, 16–18, 31, 32: a strengthening completes only through the Authority's attestation of the
    /// complete population — undiscoverable, archived and older-layout records included, the pending creation counted apart,
    /// every converted value decrypt-checked, history kept and reported, evidence journaled, no plaintext in logs.
    /// </summary>
    [Fact]
    public async Task AStrengtheningCompletesOnlyThroughTheAuthoritysAttestationOfTheCompletePopulation()
    {
        var w = await WorkspaceAsync("m4-complete", 4);
        // An archived record stays in scope (signed header status; registry still Active).
        var archived = w.Records[0];
        var view = (await w.S.WaitProjectionAsync(w.S.Clerk, archived)).Root;
        var archive = await fixture.Api.PostAsync($"/api/v1/form-records/{archived:D}/archive", w.S.Token(w.S.Clerk), new { expectedContentRevision = view.GetProperty("record").GetProperty("contentRevision").GetInt64(), expectedStatus = "Draft", reason = "M4 archived record in scope" });
        Assert.True(archive.IsSuccess, archive.Body);
        // Version 2 no longer shows the note: values of a field absent from the current layout are still converted (historical form versions).
        await w.S.SaveDraftAsync(w.FormId, StageBScenario.Layout("Reconcile v2", StageBScenario.Section("main", "Main", StageBScenario.Field(w.CountId, "number", "Exhibit count", 2))));
        await w.S.PublishAsync(w.FormId, "v2 without the note");
        var pending = await PendingCreationAsync(w);
        // No ordinary user but the owning clerk can discover these records: the designer and the reader find none in their catalogues.
        foreach (var user in new[] { w.S.Reader, w.S.Designer })
        {
            var catalogue = await fixture.Authority.SendAsync(HttpMethod.Post, "/internal/v1/search-catalogue", TestIdentity.WorkloadToken(), new { organizationId = w.S.OrganizationId, formDefinitionId = w.FormId, maximumEntries = 500 }, w.S.Token(user));
            Assert.True(catalogue.IsSuccess, catalogue.Body);
            Assert.Equal(0, catalogue.Root.GetProperty("entries").GetArrayLength());
        }

        var strengthened = await StrengthenAsync(w);
        Assert.True(strengthened.IsSuccess, strengthened.Body);
        var done = await WaitStatusAsync(w, s => s.GetProperty("authorityReconciledComplete").GetBoolean() || MigrationState(s) is "Failed");
        Assert.True(done.GetProperty("authorityReconciledComplete").GetBoolean(), done.GetRawText());
        Assert.Equal("Completed", MigrationState(done));
        var reconciliation = Reconciliation(done);
        Assert.Equal("MigrationCompletion", reconciliation.GetProperty("mode").GetString());
        Assert.Equal("Complete", reconciliation.GetProperty("state").GetString());
        var attestation = Attestation(done);
        Assert.True(attestation.GetProperty("complete").GetBoolean());
        Assert.Equal(5, Int(attestation, "expectedRecordCount"));
        Assert.Equal(4, Int(attestation, "acceptedRecordCount"));
        Assert.Equal(1, Int(attestation, "pendingCreationRecordCount"));
        Assert.Equal(4, Int(attestation, "compliantRecordCount"));
        Assert.Equal(1, Int(attestation, "archivedRecordCount"));
        Assert.Equal(0, Int(attestation, "quarantinedRecordCount"));
        Assert.Equal(0, Int(attestation, "oldFormRemainingRecordCount"));
        Assert.Equal(4, Int(attestation, "targetFormOccurrenceCount"));
        Assert.True(attestation.GetProperty("retainedHistoryRevisionCount").GetInt64() >= 5, attestation.GetRawText()); // plaintext revisions stay as immutable history
        Assert.Contains("retained earlier revision", attestation.GetProperty("coverageStatement").GetString(), StringComparison.Ordinal);
        Assert.Equal(4, Int(reconciliation, "decryptCheckedValueCount"));
        Assert.Equal(0, Int(reconciliation, "decryptFailedValueCount"));
        Assert.Equal(0, Int(reconciliation, "logicalTypeMismatchCount"));
        Assert.Equal(0, Int(reconciliation, "businessOnlyRecordCount"));
        Assert.True(Int(reconciliation, "retainedOldFormRowCount") >= 4, reconciliation.GetRawText());
        Assert.Contains("current versions only", reconciliation.GetProperty("basis").GetString(), StringComparison.Ordinal);

        // The Authority holds the attestation and journaled it on the audit stream (identifiers and counts only, no record ids).
        var attestationId = attestation.GetProperty("attestationId").GetGuid();
        Assert.Equal("t|" + attestation.GetProperty("digestHex").GetString(), Observer($"SELECT attestation_complete::text || '|' || encode(attestation_digest, 'hex') FROM protection_reconciliation_attestations WHERE protection_reconciliation_attestation_id = '{attestationId:D}';").Replace("true", "t", StringComparison.Ordinal));
        var journal = Observer($"SELECT journal_event_body_json FROM security_journal_entries WHERE organization_id = '{w.S.OrganizationId:D}' AND journal_stream_name = 'audit' AND journal_event_kind = 'ProtectionReconciliationAttested' AND journal_event_body_json LIKE '%{attestationId:D}%';");
        Assert.NotEmpty(journal);
        Assert.All(w.Records.Append(pending), r => Assert.DoesNotContain(r.ToString("D"), journal, StringComparison.OrdinalIgnoreCase));
        Assert.Equal("4", Observer($"SELECT count(*) FROM protection_census_results WHERE scoping_security_command_id = '{CommandOf(w):D}' AND census_outcome_name = 'Verified';"));

        // Every record's accepted head now holds the note only as ciphertext; the earlier plaintext rows are kept, not deleted.
        Assert.Equal(0, AuthorityOracle.RowsInAcceptedHeads(fixture.Stack, "field_string_values", w.NoteId, w.Records).Values);
        Assert.Equal(4, AuthorityOracle.RowsInAcceptedHeads(fixture.Stack, "field_encrypted_values", w.NoteId, w.Records).Values);
        Assert.True(int.Parse(fixture.Stack.AttackerSql($"SELECT count(*) FROM field_string_values WHERE field_definition_id = '{w.NoteId:D}';").Trim(), CultureInfo.InvariantCulture) >= 4); // the version-1 plaintext rows are history, kept
        // The owner still reads the converted values (correct type, AEAD-authenticated).
        var read = (await w.S.WaitProjectionAsync(w.S.Clerk, w.Records[1])).Root.GetProperty("record").GetProperty("values").EnumerateArray().Single(v => v.GetProperty("fieldKey").GetString() == "plain_note");
        Assert.Equal(Marker(w.S, 1), read.GetProperty("stringValue").GetString());
        Assert.True(read.GetProperty("applicationEncrypted").GetBoolean());

        // Organization coverage (verifier only) names this decision as attested complete.
        var coverage = await fixture.Authority.SendAsync(HttpMethod.Post, "/internal/v1/verifier/protection-coverage", TestIdentity.VerifierToken(), new { organizationId = w.S.OrganizationId });
        Assert.True(coverage.IsSuccess, coverage.Body);
        var decision = coverage.Root.GetProperty("decisions").EnumerateArray().Single(d => d.GetProperty("field_definition_id").GetGuid() == w.NoteId);
        Assert.True(decision.GetProperty("latest_attestation").GetProperty("attestation_complete").GetBoolean());

        // No protected plaintext in the worker's or the Authority's logs.
        var logs = fixture.Stack.Logs("worker", 2000) + fixture.Stack.Logs("authority", 2000);
        for (var i = 0; i < w.Records.Count; i++)
        {
            Assert.DoesNotContain(Marker(w.S, i), logs, StringComparison.Ordinal);
        }

        // Business audit: the attestation event carries counts and ids, never values.
        var audit = fixture.Stack.AttackerSql($"SELECT event_metadata_json FROM audit_events WHERE tenant_id = '{w.S.OrganizationId:D}' AND event_action = 'FIELD_PROTECTION_RECONCILIATION_ATTESTED';");
        Assert.Contains(attestationId.ToString("D"), audit, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("m4-plaintext-marker", audit, StringComparison.Ordinal);
    }

    /// <summary>
    /// G19 core (owner list 3, 7, 8, 10, 11, 12): the DBA hides a record from every business listing — moved to another form and its
    /// value row relabelled, so neither the record list nor the estimate sees it — and injects a forged business row. The
    /// business-side post-check alone passes; the Authority's registry still contains the hidden record, its census fails and
    /// quarantines it, and the migration is never claimed complete. The forged row is reported and ignored.
    /// </summary>
    [Fact]
    public async Task ARecordConcealedFromEveryBusinessListingBlocksAFalseCompletionAndForgedRowsAreIgnored()
    {
        var w = await WorkspaceAsync("m4-conceal", 3);
        var elsewhere = await w.S.CreateFormAsync("Elsewhere " + w.S.Name, w.S.Clerk);
        var concealed = w.Records[0];
        fixture.Stack.AttackerSql($"UPDATE form_records SET form_definition_id = '{elsewhere:D}' WHERE form_record_id = '{concealed:D}'; UPDATE field_string_values SET field_definition_id = '{w.CountId:D}' WHERE form_record_id = '{concealed:D}' AND field_definition_id = '{w.NoteId:D}';");
        var forged = Guid.CreateVersion7();
        fixture.Stack.AttackerSql($"INSERT INTO form_records SELECT (jsonb_populate_record(NULL::form_records, to_jsonb(r) || jsonb_build_object('form_record_id', '{forged:D}'))).* FROM form_records r WHERE r.form_record_id = '{w.Records[1]:D}';");

        var strengthened = await StrengthenAsync(w);
        Assert.True(strengthened.IsSuccess, strengthened.Body);
        var done = await WaitStatusAsync(w, s => MigrationState(s) is "Failed" or "Completed");
        Assert.Equal("Failed", MigrationState(done));
        Assert.StartsWith("RECONCILIATION_INCOMPLETE:RECORDS_QUARANTINED", done.GetProperty("migration").GetProperty("lastErrorCode").GetString(), StringComparison.Ordinal);
        Assert.False(done.GetProperty("authorityReconciledComplete").GetBoolean());
        Assert.False(done.GetProperty("fullyEncrypted").GetBoolean());
        // The business-side post-check over the listed records found nothing wrong: without M4 this would have been "Completed".
        Assert.Equal(0, Int(done.GetProperty("verification"), "remainingRecordCount"));
        Assert.Equal(0, Int(done.GetProperty("verification"), "unverifiedRecordCount"));

        var reconciliation = Reconciliation(done);
        Assert.Equal("Incomplete", reconciliation.GetProperty("state").GetString());
        var attestation = Attestation(done);
        Assert.Equal(3, Int(attestation, "expectedRecordCount"));
        Assert.Equal(1, Int(attestation, "quarantinedRecordCount"));
        Assert.Equal(2, Int(attestation, "compliantRecordCount"));
        Assert.Equal("RECORDS_QUARANTINED", attestation.GetProperty("incompleteReason").GetString());
        Assert.Equal(1, Int(reconciliation, "businessOnlyRecordCount")); // the forged row: not in the registry, never read
        Assert.Equal(1, Int(reconciliation, "businessMissingRecordCount")); // the hidden record: not listed under this form
        Assert.Equal("t", Observer($"SELECT content_head_quarantined FROM record_content_heads WHERE form_record_id = '{concealed:D}';"));
        Assert.Equal(string.Empty, Observer($"SELECT resource_entity_id FROM authority_resources WHERE resource_entity_id = '{forged:D}';"));
        // Nothing was repaired or re-signed: the concealed record's accepted head is unchanged.
        Assert.Equal(1, AuthorityOracle.Head(fixture.Stack, concealed).ContentRevision);
    }

    /// <summary>
    /// Owner list 9, 10, 19, 27: after a verified completion, the DBA rolls one record's accepted rows back to plaintext (valid
    /// earlier bytes under the current operation). A verify-only reconciliation detects it, quarantines the record, reports
    /// incomplete, never re-signs, and never converts anything; the field is no longer reported Authority-verified.
    /// </summary>
    [Fact]
    public async Task AVerifyOnlyReconciliationDetectsAPostCompletionRollbackAndNeverRepairsIt()
    {
        var w = await WorkspaceAsync("m4-rollback", 2);
        Assert.True((await StrengthenAsync(w)).IsSuccess);
        await WaitStatusAsync(w, s => s.GetProperty("authorityReconciledComplete").GetBoolean());
        var target = w.Records[0];
        var head = AuthorityOracle.Head(fixture.Stack, target);
        fixture.Stack.AttackerSql($@"DELETE FROM field_encrypted_values WHERE form_record_id = '{target:D}' AND field_definition_id = '{w.NoteId:D}' AND content_change_operation_id = '{head.OperationId:D}';
INSERT INTO field_string_values SELECT (jsonb_populate_record(NULL::field_string_values, to_jsonb(v) || jsonb_build_object('field_string_value_id', '{Guid.CreateVersion7():D}', 'content_change_operation_id', '{head.OperationId:D}'))).* FROM field_string_values v WHERE v.form_record_id = '{target:D}' AND v.field_definition_id = '{w.NoteId:D}' LIMIT 1;");

        var done = await VerifyAndWaitAsync(w, "after a suspected rollback");
        Assert.Equal("Incomplete", ReconciliationState(done));
        Assert.False(done.GetProperty("authorityReconciledComplete").GetBoolean());
        Assert.Equal(1, Int(Attestation(done), "quarantinedRecordCount"));
        Assert.Equal("Completed", MigrationState(done)); // verify-only never touches the migration
        Assert.Equal("t", Observer($"SELECT content_head_quarantined FROM record_content_heads WHERE form_record_id = '{target:D}';"));
        Assert.Equal(head.ContentRevision, AuthorityOracle.Head(fixture.Stack, target).ContentRevision); // no new accepted revision: nothing re-signed
        Assert.Equal("0", fixture.Stack.AttackerSql($"SELECT count(*) FROM field_protection_migration_items i JOIN field_protection_migrations m ON m.field_protection_migration_id = i.field_protection_migration_id WHERE m.field_definition_id = '{w.NoteId:D}' AND i.item_state = 'Pending';").Trim());
    }

    /// <summary>
    /// Owner list 20–22, 25 (outage): an Authority outage blocks a queued reconciliation (retried, nothing claimed meanwhile), a
    /// worker restart mid-run resumes it, and census results the Authority already holds are reused instead of re-verified.
    /// </summary>
    [Fact]
    public async Task OutagesAndRestartsResumeTheReconciliationAndHeldCensusResultsAreReused()
    {
        var w = await WorkspaceAsync("m4-resume", 12);
        Assert.True((await StrengthenAsync(w)).IsSuccess);
        await WaitStatusAsync(w, s => s.GetProperty("authorityReconciledComplete").GetBoolean());

        // Authority outage: the queued verification blocks (read from the business database; the status itself needs the Authority).
        fixture.Stack.Pause("worker");
        Assert.True((await RequestVerificationAsync(w, w.S.Designer)).IsSuccess);
        fixture.Stack.Pause("authority");
        try
        {
            fixture.Stack.Unpause("worker");
            var deadline = DateTime.UtcNow.AddSeconds(90);
            string state;
            do
            {
                await Task.Delay(1000, Ct);
                state = fixture.Stack.AttackerSql($"SELECT reconciliation_state || '|' || coalesce(last_error_code, '') FROM field_protection_reconciliations WHERE field_definition_id = '{w.NoteId:D}' AND reconciliation_mode = 'VerifyOnly';").Trim();
            }
            while (!state.StartsWith("Blocked", StringComparison.Ordinal) && DateTime.UtcNow < deadline);
            Assert.StartsWith("Blocked|", state, StringComparison.Ordinal);
        }
        finally
        {
            fixture.Stack.Unpause("authority");
        }

        var resumed = await WaitStatusAsync(w, s => Terminal(s) && Reconciliation(s).GetProperty("mode").GetString() == "VerifyOnly");
        Assert.Equal("Complete", ReconciliationState(resumed));
        // Every record is verified again (first round), never vouched for by the census the completion reconciliation left behind.
        Assert.True(Int(Reconciliation(resumed), "censusVerifiedRecordCount") == 12, Reconciliation(resumed).GetRawText());
        Assert.Equal(0, Int(Reconciliation(resumed), "censusReusedRecordCount"));
        Assert.True(fixture.Stack.AttackerSql($"SELECT transient_failure_count FROM field_protection_reconciliations WHERE field_definition_id = '{w.NoteId:D}' AND reconciliation_mode = 'VerifyOnly';").Trim() != "0");

        // Worker restart during a run: it resumes from its durable state and completes; no duplicate census rows.
        fixture.Stack.Pause("worker");
        var restarted = await VerifyAndWaitAsync(w, "after a worker restart", afterRequest: () =>
        {
            fixture.Stack.Unpause("worker");
            fixture.Stack.Compose("restart worker", 120);
        });
        Assert.Equal("Complete", ReconciliationState(restarted));
        Assert.Equal("12", Observer($"SELECT count(*) FROM protection_census_results WHERE scoping_security_command_id = '{CommandOf(w):D}';"));
    }

    /// <summary>
    /// Owner list 23, 24: records edited while a reconciliation runs are caught up in a further round (census results of unchanged
    /// records carry over) and never mixed: the result is complete for the heads that are current when the Authority attests.
    /// </summary>
    [Fact]
    public async Task EditsDuringAReconciliationAreCaughtUpInANewRoundNotMixed()
    {
        var w = await WorkspaceAsync("m4-concurrent", 20);
        Assert.True((await StrengthenAsync(w)).IsSuccess);
        await WaitStatusAsync(w, s => s.GetProperty("authorityReconciledComplete").GetBoolean());
        var edits = new List<Task>();
        var done = await VerifyAndWaitAsync(w, "with concurrent edits", afterRequest: () =>
        {
            foreach (var record in w.Records.Take(3))
            {
                edits.Add(Task.Run(async () =>
                {
                    var view = (await w.S.WaitProjectionAsync(w.S.Clerk, record)).Root;
                    var edit = await fixture.Api.PatchAsync($"/api/v1/form-records/{record:D}", w.S.Token(w.S.Clerk), new { expectedContentRevision = view.GetProperty("record").GetProperty("contentRevision").GetInt64(), values = new Dictionary<string, string> { ["plain_note"] = "edited during reconciliation" } });
                    Assert.True(edit.IsSuccess || edit.ErrorCode == "CONTENT_REVISION_CONFLICT", edit.Body);
                }, Ct));
            }
        });
        await Task.WhenAll(edits);
        // An edit that landed after the attestation is caught by the next verification, which must again be complete.
        done = await VerifyAndWaitAsync(w, "after concurrent edits");
        Assert.Equal("Complete", ReconciliationState(done));
        Assert.Equal(20, Int(Attestation(done), "compliantRecordCount"));
        Assert.Equal(0, Int(Attestation(done), "notYetCensusedRecordCount"));
        // The attestation covers exactly the heads that are current now.
        var heads = AuthorityOracle.Heads(fixture.Stack, w.Records);
        var list = string.Join(",", heads.Select(h => $"('{h.Key:D}','{h.Value.OperationId:D}')"));
        Assert.Equal("20", Observer($"SELECT count(*) FROM protection_census_results WHERE scoping_security_command_id = '{CommandOf(w):D}' AND census_outcome_name = 'Verified' AND (form_record_id::text, committed_operation_id::text) IN ({list});"));
    }

    /// <summary>Owner list 28–30: only authorized people request a verification; only the verifier reaches census, attestation and coverage; organizations stay apart.</summary>
    [Fact]
    public async Task OnlyAuthorizedPeopleRequestAVerificationAndOnlyTheVerifierReachesTheAuthorityOperations()
    {
        var w = await WorkspaceAsync("m4-authz", 2);
        // Before any protection change there is nothing to reconcile.
        Assert.Equal("NO_PROTECTION_DECISION", (await RequestVerificationAsync(w, w.S.Designer)).ErrorCode);
        Assert.True((await StrengthenAsync(w)).IsSuccess);
        await WaitStatusAsync(w, s => s.GetProperty("authorityReconciledComplete").GetBoolean());

        foreach (var user in new[] { w.S.Reader, w.S.Clerk })
        {
            var refused = await RequestVerificationAsync(w, user);
            Assert.Contains(refused.Status, new[] { HttpStatusCode.Forbidden, HttpStatusCode.NotFound });
        }

        fixture.Stack.Pause("worker");
        try
        {
            Assert.True((await RequestVerificationAsync(w, w.S.Designer)).IsSuccess);
            Assert.Equal("RECONCILIATION_IN_PROGRESS", (await RequestVerificationAsync(w, w.S.Designer)).ErrorCode);
            // A field whose migration is still working cannot be verified separately (it reconciles itself when it finishes).
            Assert.True((await StrengthenAsync(w, w.CountId)).IsSuccess);
            Assert.Equal("MIGRATION_IN_PROGRESS", (await fixture.Api.PostAsync($"/api/v1/form-definitions/{w.FormId:D}/fields/{w.CountId:D}/protection/reconciliations", w.S.Token(w.S.Designer), new { requestReason = "while migrating" })).ErrorCode);
        }
        finally
        {
            fixture.Stack.Unpause("worker");
        }

        // The business workload credential, an operator credential and user tokens never reach the verifier routes.
        var snapshotId = Guid.Parse(fixture.Stack.AttackerSql($"SELECT catalogue_snapshot_id FROM field_protection_reconciliations WHERE field_definition_id = '{w.NoteId:D}' AND catalogue_snapshot_id IS NOT NULL ORDER BY reconciliation_requested_on LIMIT 1;").Trim());
        foreach (var (method, route, body) in new (HttpMethod, string, object?)[]
                 {
                     (HttpMethod.Post, $"/internal/v1/verifier/migration-catalogue/{snapshotId:D}/census", new { records = new[] { new { formRecordId = w.Records[0], expectedCommittedOperationId = Guid.CreateVersion7(), observedItems = Array.Empty<object>() } } }),
                     (HttpMethod.Post, $"/internal/v1/verifier/migration-catalogue/{snapshotId:D}/attestation", null),
                     (HttpMethod.Post, "/internal/v1/verifier/protection-coverage", new { organizationId = w.S.OrganizationId }),
                 })
        {
            Assert.Contains((await fixture.Authority.SendAsync(method, route, TestIdentity.WorkloadToken(), body, w.S.Token(w.S.Designer))).Status, new[] { HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden });
            Assert.Contains((await fixture.Authority.SendAsync(method, route, TestIdentity.OperatorToken(), body)).Status, new[] { HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden });
        }

        // A snapshot belongs to the verifier workload that issued it.
        Assert.Equal(HttpStatusCode.NotFound, (await fixture.Authority.SendAsync(HttpMethod.Post, $"/internal/v1/verifier/migration-catalogue/{snapshotId:D}/attestation", TestIdentity.VerifierToken("another-verifier"), null)).Status);

        // Another organization's coverage never lists this organization's decisions.
        var other = new StageBScenario(fixture, "m4-authz-other");
        await other.BootstrapAsync();
        var foreign = await fixture.Authority.SendAsync(HttpMethod.Post, "/internal/v1/verifier/protection-coverage", TestIdentity.VerifierToken(), new { organizationId = other.OrganizationId });
        Assert.True(foreign.IsSuccess, foreign.Body);
        Assert.DoesNotContain(foreign.Root.GetProperty("decisions").EnumerateArray(), d => d.GetProperty("field_definition_id").GetGuid() == w.NoteId);
    }

    /// <summary>Owner list 2: organization-wide coverage is claimed only when every current decision, across forms, has a complete attestation.</summary>
    [Fact]
    public async Task OrganizationCoverageIsCompleteOnlyWhenEveryDecisionAcrossFormsIsAttested()
    {
        var first = await WorkspaceAsync("m4-coverage", 2);
        Assert.True((await StrengthenAsync(first)).IsSuccess);
        await WaitStatusAsync(first, s => s.GetProperty("authorityReconciledComplete").GetBoolean());
        var second = await WorkspaceAsync("m4-coverage-b", 1, first.S);
        async Task<JsonElement> CoverageAsync()
        {
            var result = await fixture.Authority.SendAsync(HttpMethod.Post, "/internal/v1/verifier/protection-coverage", TestIdentity.VerifierToken(), new { organizationId = first.S.OrganizationId });
            Assert.True(result.IsSuccess, result.Body);
            return result.Root.Clone();
        }

        Assert.True((await CoverageAsync()).GetProperty("organization_complete").GetBoolean());
        fixture.Stack.Pause("worker");
        try
        {
            Assert.True((await StrengthenAsync(second)).IsSuccess);
            await first.S.WaitGateReadyAsync();
            var partial = await CoverageAsync();
            Assert.Equal(2, partial.GetProperty("decision_count").GetInt32());
            Assert.False(partial.GetProperty("organization_complete").GetBoolean()); // the second form's decision is not attested yet
        }
        finally
        {
            fixture.Stack.Unpause("worker");
        }

        await WaitStatusAsync(second, s => s.GetProperty("authorityReconciledComplete").GetBoolean());
        var full = await CoverageAsync();
        Assert.True(full.GetProperty("organization_complete").GetBoolean());
        Assert.Equal(2, full.GetProperty("attested_complete_decision_count").GetInt32());
    }

    // ---- C7 M4 follow-up: the previously missing adversarial tests (owner instruction, 2026-10-29) ----

    /// <summary>Recreates one service with test-only environment overrides (restored by calling again with null values).</summary>
    private void Recreate(string service, params (string Variable, string? Value)[] overrides)
    {
        foreach (var (variable, value) in overrides)
        {
            Environment.SetEnvironmentVariable(variable, value);
        }

        fixture.Stack.Compose($"up -d --no-deps --force-recreate --wait {service}", 300);
    }

    private async Task<JsonElement> WaitReconciliationAsync(Workspace w, Guid reconciliationId, TimeSpan? timeout = null) =>
        await WaitStatusAsync(w, s => Terminal(s) && Reconciliation(s).GetProperty("fieldProtectionReconciliationId").GetGuid() == reconciliationId, timeout);

    private async Task<Guid> RequestIdAsync(Workspace w, string reason)
    {
        var requested = await RequestVerificationAsync(w, w.S.Designer, reason);
        Assert.True(requested.IsSuccess, requested.Body);
        return Reconciliation(requested.Root).GetProperty("fieldProtectionReconciliationId").GetGuid();
    }

    private string ReconciliationRow(Guid reconciliationId) =>
        fixture.Stack.AttackerSql($"SELECT reconciliation_state || '|' || coalesce(last_error_code, '') || '|' || reconciliation_round_count FROM field_protection_reconciliations WHERE field_protection_reconciliation_id = '{reconciliationId:D}';").Trim();

    private async Task SetPolicyAsync(StageBScenario s, bool permitDowngrade)
    {
        var current = await fixture.Api.GetAsync("/api/v1/organization/security-policy", s.Token(s.Designer));
        Assert.True(current.IsSuccess, current.Body);
        var updated = await fixture.Api.SendAsync(HttpMethod.Put, "/api/v1/organization/security-policy", s.Token(s.Designer), new { minimumFieldClassification = "Internal", plaintextInternalFieldsPermitted = true, plaintextDowngradePermitted = permitDowngrade, policyStatementText = permitDowngrade ? "M4 test: governance approved removing encryption from Internal fields." : "M4 test: removal of encryption no longer permitted.", expectedPolicyRevision = current.Root.GetProperty("securityPolicyRevision").GetInt64() }, null);
        Assert.True(updated.IsSuccess, updated.Body);
    }

    /// <summary>
    /// Owner: a policy change during an active reconciliation. An organization policy change (authorization revision moves) does
    /// not falsify or block the result — the attestation re-derives everything; a change of the field's protection decision
    /// supersedes the running reconciliation (nothing attested under the old decision), and the new decision's own migration
    /// is reconciled in the new direction.
    /// </summary>
    [Fact]
    public async Task ADecisionChangeDuringAReconciliationSupersedesItWhileAnOrganizationPolicyChangeDoesNot()
    {
        var w = await WorkspaceAsync("m4-policy", 3);
        Assert.True((await StrengthenAsync(w, classification: "Internal")).IsSuccess);
        await WaitStatusAsync(w, s => s.GetProperty("authorityReconciledComplete").GetBoolean());
        try
        {
            fixture.Stack.Pause("worker");
            Guid unaffected;
            try
            {
                unaffected = await RequestIdAsync(w, "organization policy changes meanwhile");
                await SetPolicyAsync(w.S, permitDowngrade: true);
            }
            finally
            {
                fixture.Stack.Unpause("worker");
            }

            Assert.Equal("Complete", ReconciliationState(await WaitReconciliationAsync(w, unaffected)));

            fixture.Stack.Pause("worker");
            Guid superseded;
            try
            {
                superseded = await RequestIdAsync(w, "the decision changes meanwhile");
                var field = (await StatusAsync(w)).Root;
                var downgrade = await fixture.Api.PostAsync($"/api/v1/form-definitions/{w.FormId:D}/fields/{w.NoteId:D}/protection/downgrade", w.S.Token(w.S.Administrator), new { changeReason = "M4 test: decision changes during a reconciliation", expectedProtectionRevision = field.GetProperty("protectionPolicyRevision").GetInt64() });
                Assert.True(downgrade.IsSuccess, downgrade.Body);
            }
            finally
            {
                fixture.Stack.Unpause("worker");
            }

            var deadline = DateTime.UtcNow.AddSeconds(180);
            string row;
            do
            {
                await Task.Delay(1000, Ct);
                row = ReconciliationRow(superseded);
            }
            while ((row.StartsWith("Queued", StringComparison.Ordinal) || row.StartsWith("Running", StringComparison.Ordinal) || row.StartsWith("Blocked", StringComparison.Ordinal)) && DateTime.UtcNow < deadline);
            Assert.StartsWith("Superseded|", row, StringComparison.Ordinal); // never Complete under a superseded decision
            Assert.Equal(string.Empty, fixture.Stack.AttackerSql($"SELECT authority_attestation_id FROM field_protection_reconciliations WHERE field_protection_reconciliation_id = '{superseded:D}';").Trim()); // nothing attested under it

            // The new decision (removal) is reconciled in its own direction before its migration completes.
            var decrypted = await WaitStatusAsync(w, s => s.GetProperty("authorityReconciledComplete").GetBoolean() || MigrationState(s) == "Failed");
            Assert.True(decrypted.GetProperty("authorityReconciledComplete").GetBoolean(), decrypted.GetRawText());
            Assert.Equal("Decrypt", decrypted.GetProperty("migration").GetProperty("migrationDirection").GetString());
            Assert.Equal(3, Int(Attestation(decrypted), "compliantRecordCount"));
        }
        finally
        {
            await SetPolicyAsync(w.S, permitDowngrade: false);
        }
    }

    /// <summary>Owner: a missing or unavailable encryption key. The decrypt check cannot run, so the reconciliation blocks (retried) and is never Complete until the key returns.</summary>
    [Fact]
    public async Task AnUnavailableEncryptionKeyBlocksTheReconciliationAndNeverCompletesIt()
    {
        var w = await WorkspaceAsync("m4-key", 2);
        Assert.True((await StrengthenAsync(w)).IsSuccess);
        await WaitStatusAsync(w, s => s.GetProperty("authorityReconciledComplete").GetBoolean());
        Guid id;
        try
        {
            fixture.Stack.Pause("worker");
            id = await RequestIdAsync(w, "key provider outage");
            Recreate("worker", ("WORKER_TESTING_KEY_PROVIDER", "Unconfigured")); // replaces the paused worker: the queued run starts without a key
            var deadline = DateTime.UtcNow.AddSeconds(120);
            string row;
            do
            {
                await Task.Delay(1000, Ct);
                row = ReconciliationRow(id);
            }
            while (!row.StartsWith("Blocked", StringComparison.Ordinal) && !row.StartsWith("Complete", StringComparison.Ordinal) && !row.StartsWith("Incomplete", StringComparison.Ordinal) && DateTime.UtcNow < deadline);
            Assert.StartsWith("Blocked|ENCRYPTION_UNAVAILABLE", row, StringComparison.Ordinal);
            await Task.Delay(5000, Ct);
            Assert.StartsWith("Blocked|", ReconciliationRow(id), StringComparison.Ordinal); // still waiting, never completed without the check
        }
        finally
        {
            Recreate("worker", ("WORKER_TESTING_KEY_PROVIDER", null));
        }

        Assert.Equal("Complete", ReconciliationState(await WaitReconciliationAsync(w, id)));
    }

    /// <summary>
    /// Owner: the verification limit and snapshot expiry. Above the migration-catalogue ceiling the reconciliation ends Incomplete
    /// (RECONCILIATION_LIMIT, no partial certificate) and the migration is not claimed complete; with a snapshot lifetime far
    /// shorter than one round, the reconciliation renews the catalogue, reuses only what it verified itself and completes —
    /// or ends Incomplete — but never claims completeness from an expired snapshot.
    /// </summary>
    [Fact]
    public async Task TheCatalogueCeilingEndsIncompleteAndExpiredSnapshotsAreRenewedSafely()
    {
        var limited = await WorkspaceAsync("m4-limit", 4);
        var large = await WorkspaceAsync("m4-expiry", 160, limited.S);
        Assert.True((await StrengthenAsync(large)).IsSuccess);
        await WaitStatusAsync(large, s => s.GetProperty("authorityReconciledComplete").GetBoolean(), TimeSpan.FromMinutes(10));
        try
        {
            Recreate("authority", ("AUTHORITY_TESTING_MAX_MIGRATION_ENTRIES", "3"));
            Assert.True((await StrengthenAsync(limited)).IsSuccess);
            var done = await WaitStatusAsync(limited, s => MigrationState(s) is "Failed" or "Completed");
            Assert.Equal("Failed", MigrationState(done));
            Assert.Equal("RECONCILIATION_INCOMPLETE:RECONCILIATION_LIMIT", done.GetProperty("migration").GetProperty("lastErrorCode").GetString());
            Assert.Equal("Incomplete", ReconciliationState(done));
            Assert.False(done.GetProperty("authorityReconciledComplete").GetBoolean());
            Assert.Equal(JsonValueKind.Null, Reconciliation(done).GetProperty("attestation").ValueKind); // nothing partial attested

            Recreate("authority", ("AUTHORITY_TESTING_MAX_MIGRATION_ENTRIES", null), ("AUTHORITY_TESTING_MIGRATION_SNAPSHOT_SECONDS", "2"));
            var id = await RequestIdAsync(large, "snapshots expire within one round");
            var renewed = await WaitReconciliationAsync(large, id, TimeSpan.FromMinutes(10));
            var row = ReconciliationRow(id);
            // Each round verifies at least one batch before its snapshot expires and later rounds reuse only what this
            // reconciliation verified, so the form completes across several renewals; the attestation is still re-derived.
            Assert.Equal("Complete", ReconciliationState(renewed));
            Assert.Equal(160, Int(Attestation(renewed), "compliantRecordCount"));
            var firstRound = fixture.Stack.AttackerSql($"SELECT first_round_snapshot_id FROM field_protection_reconciliations WHERE field_protection_reconciliation_id = '{id:D}';").Trim();
            Assert.Equal("160", Observer($"SELECT count(*) FROM protection_census_results WHERE scoping_security_command_id = '{CommandOf(large):D}' AND census_outcome_name = 'Verified' AND census_verified_on >= (SELECT snapshot_issued_on FROM search_catalogue_snapshots WHERE search_catalogue_snapshot_id = '{firstRound}');")); // every record verified within this reconciliation
            Assert.True(Int(Reconciliation(renewed), "roundCount") >= 2, row); // at least one renewal happened
            TestContext.Current.TestOutputHelper?.WriteLine("2-second snapshot lifetime, 160 records: " + row + " " + Reconciliation(renewed).GetRawText());
        }
        finally
        {
            Recreate("authority", ("AUTHORITY_TESTING_MAX_MIGRATION_ENTRIES", null), ("AUTHORITY_TESTING_MIGRATION_SNAPSHOT_SECONDS", null));
        }
    }

    /// <summary>
    /// Owner: a required audit archival outage. The attestation is journaled on the Authority audit stream in the same
    /// transaction (non-gating); a completion is never reported without that journal entry, which is archived once the archive
    /// returns. AUD-C7-01 (archival durability of mandatory evidence) stays open: delivery is asynchronous.
    /// </summary>
    [Fact]
    public async Task AnArchiveOutageNeverYieldsACompletionWithoutAuthorityEvidence()
    {
        var w = await WorkspaceAsync("m4-archive", 2);
        Assert.True((await StrengthenAsync(w)).IsSuccess);
        await WaitStatusAsync(w, s => s.GetProperty("authorityReconciledComplete").GetBoolean());
        fixture.Stack.Stop("archive-store");
        Guid id;
        string row;
        try
        {
            id = await RequestIdAsync(w, "archive outage");
            var deadline = DateTime.UtcNow.AddSeconds(180);
            do
            {
                await Task.Delay(1000, Ct);
                row = ReconciliationRow(id);
            }
            while ((row.StartsWith("Queued", StringComparison.Ordinal) || row.StartsWith("Running", StringComparison.Ordinal)) && DateTime.UtcNow < deadline);
        }
        finally
        {
            fixture.Stack.Start("archive-store");
        }

        TestContext.Current.TestOutputHelper?.WriteLine("during the archive outage: " + row);
        var during = await WaitReconciliationAsync(w, id, TimeSpan.FromSeconds(240)); // Blocked (if any) resumes once the archive is back
        var state = ReconciliationState(during);
        if (state == "Complete")
        {
            var attestationId = Attestation(during).GetProperty("attestationId").GetGuid();
            Assert.NotEqual(string.Empty, Observer($"SELECT archive_delivery_state FROM security_journal_entries WHERE journal_event_kind = 'ProtectionReconciliationAttested' AND journal_event_body_json LIKE '%{attestationId:D}%';"));
            var deadline = DateTime.UtcNow.AddSeconds(180);
            string delivery;
            do
            {
                await Task.Delay(2000, Ct);
                delivery = Observer($"SELECT archive_delivery_state FROM security_journal_entries WHERE journal_event_kind = 'ProtectionReconciliationAttested' AND journal_event_body_json LIKE '%{attestationId:D}%';");
            }
            while (delivery != "Anchored" && DateTime.UtcNow < deadline);
            Assert.Equal("Anchored", delivery); // delivered once the archive is back
        }
        else
        {
            Assert.Contains(state, new[] { "Incomplete", "Blocked" });
        }
    }

    /// <summary>Owner: accepted changes after an attestation. The protection barrier admits a new revision only in the required form; the next reconciliation verifies the new head and stays complete.</summary>
    [Fact]
    public async Task ARecordChangedAfterTheAttestationIsVerifiedByTheNextReconciliation()
    {
        var w = await WorkspaceAsync("m4-after", 3);
        Assert.True((await StrengthenAsync(w)).IsSuccess);
        await WaitStatusAsync(w, s => s.GetProperty("authorityReconciledComplete").GetBoolean());
        var target = w.Records[0];
        var before = AuthorityOracle.Head(fixture.Stack, target);
        var view = (await w.S.WaitProjectionAsync(w.S.Clerk, target)).Root;
        var edit = await fixture.Api.PatchAsync($"/api/v1/form-records/{target:D}", w.S.Token(w.S.Clerk), new { expectedContentRevision = view.GetProperty("record").GetProperty("contentRevision").GetInt64(), values = new Dictionary<string, string> { ["plain_note"] = "changed after the attestation" } });
        Assert.True(edit.IsSuccess, edit.Body);
        var after = AuthorityOracle.Head(fixture.Stack, target);
        Assert.True(after.ContentRevision > before.ContentRevision);
        Assert.Equal(0, AuthorityOracle.RowsInAcceptedHeads(fixture.Stack, "field_string_values", w.NoteId, [target]).Values); // the barrier: only ciphertext was accepted
        var id = await RequestIdAsync(w, "after a change");
        var done = await WaitReconciliationAsync(w, id);
        Assert.Equal("Complete", ReconciliationState(done));
        Assert.Equal(3, Int(Reconciliation(done), "censusVerifiedRecordCount"));
        Assert.Equal("1", Observer($"SELECT count(*) FROM protection_census_results WHERE form_record_id = '{target:D}' AND committed_operation_id = '{after.OperationId:D}' AND census_outcome_name = 'Verified';"));
    }

    /// <summary>The isolated stack identifies itself to the browser-test guard (API and Authority, Testing only).</summary>
    [Fact]
    public async Task TheIsolatedStackIdentifiesItselfAndTheAuthorityConfirmsIt()
    {
        using var client = new HttpClient { BaseAddress = new Uri(StageBFixture.ApiBaseAddress) };
        var identity = await client.GetFromJsonAsync<JsonElement>("/api/v1/testing/stack-identity", Ct);
        Assert.True(identity.GetProperty("isolated").GetBoolean(), identity.GetRawText());
        Assert.Equal("Testing", identity.GetProperty("apiEnvironment").GetString());
        Assert.Equal("Testing", identity.GetProperty("authorityEnvironment").GetString());
        Assert.Equal("enterpriseforms-security-test", identity.GetProperty("authorityStackIdentity").GetString());
        // The Authority route needs the business workload credential.
        Assert.Contains((await fixture.Authority.SendAsync(HttpMethod.Get, "/internal/v1/testing/stack-identity", null, null)).Status, new[] { HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden });
    }

    // ---- PB-09 measurement (engineering only; not a capacity claim) ----

    [Fact]
    public async Task MeasureReconciliationThroughputAndRecoveryAt100And250Records()
    {
        var results = new List<object>();
        foreach (var size in new[] { 100, 250 })
        {
            var w = await WorkspaceAsync("m4-bench-" + size.ToString(CultureInfo.InvariantCulture), size);
            var migrationClock = Stopwatch.StartNew();
            Assert.True((await StrengthenAsync(w)).IsSuccess);
            var migrated = await WaitStatusAsync(w, s => s.GetProperty("authorityReconciledComplete").GetBoolean(), TimeSpan.FromMinutes(15));
            migrationClock.Stop();
            var completion = Reconciliation(migrated);
            var completionSeconds = (DateTimeOffset.Parse(completion.GetProperty("completedOn").GetString()!, CultureInfo.InvariantCulture) - DateTimeOffset.Parse(completion.GetProperty("requestedOn").GetString()!, CultureInfo.InvariantCulture)).TotalSeconds;

            // Verify-only runs: no census of an earlier reconciliation is reused, so each run is a full verification.
            var runs = new List<double>();
            for (var run = 0; run < 3; run++)
            {
                var started = Stopwatch.StartNew();
                var done = await VerifyAndWaitAsync(w, "benchmark run " + run.ToString(CultureInfo.InvariantCulture), TimeSpan.FromMinutes(10));
                Assert.Equal("Complete", ReconciliationState(done));
                runs.Add(started.Elapsed.TotalSeconds);
            }

            // Recovery: worker restart in the middle of a verify-only run.
            fixture.Stack.Pause("worker");
            var recovery = new Stopwatch();
            var recovered = await VerifyAndWaitAsync(w, "benchmark recovery", TimeSpan.FromMinutes(10), () =>
            {
                fixture.Stack.Unpause("worker");
                recovery.Start();
                fixture.Stack.Compose("restart worker", 120);
            });
            recovery.Stop();
            Assert.Equal("Complete", ReconciliationState(recovered));
            runs.Sort();
            results.Add(new
            {
                records = size,
                migrationAndReconciliationSeconds = Math.Round(migrationClock.Elapsed.TotalSeconds, 2),
                completionReconciliationSeconds = Math.Round(completionSeconds, 2),
                completionCensusVerified = Int(completion, "censusVerifiedRecordCount"),
                verifiedRecordsPerSecond = Math.Round(Int(completion, "censusVerifiedRecordCount") / Math.Max(completionSeconds, 0.001), 1),
                verifyOnlyFullReverificationRunSeconds = new { p50 = Math.Round(runs[1], 2), max = Math.Round(runs[^1], 2), min = Math.Round(runs[0], 2) },
                restartRecoverySeconds = Math.Round(recovery.Elapsed.TotalSeconds, 2),
                authorityCallsPerFullRound = 1 + (int)Math.Ceiling(size / 1000.0) * 2 + (int)Math.Ceiling(size / 50.0) + 1,
            });
        }

        var json = JsonSerializer.Serialize(new { measuredOn = DateTimeOffset.UtcNow, scenario = "strengthen a plaintext Internal String field; worker migration then MigrationCompletion reconciliation (catalogue, census of every record, decrypt check, attestation); then three verify-only runs (first round re-verifies every record); then a worker restart mid-run", note = "engineering measurement on the isolated security-test stack; status polling at 1 s adds up to 1 s per measurement; small samples; not a capacity claim", results }, Indented);
        TestContext.Current.TestOutputHelper?.WriteLine(json);
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "EnterpriseForms.All.slnx")))
        {
            root = root.Parent;
        }

        if (root is not null)
        {
            var directory = Path.Combine(root.FullName, "artifacts", "stage-b");
            Directory.CreateDirectory(directory);
            await File.WriteAllTextAsync(Path.Combine(directory, "c7-m4-benchmark.json"), json, Ct);
        }
    }
}
