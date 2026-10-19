using System.Globalization;
using System.Net;
using System.Text.Json;
using EnterpriseForms.SecurityTests.Harness;
using EnterpriseForms.StageBTests.Harness;
using Xunit;

namespace EnterpriseForms.StageBTests.Tests;

/// <summary>
/// ADR-PB-004 Amendment A1 — controlled removal of field encryption on the real stack: the authority-held policy gate,
/// the roles (designers never; organization owner / system administrator; a distinct principal for declassified data),
/// the atomic cutover barrier under concurrent writes, interrupted and tampered migrations, forged completion, rollback
/// with the exposure window recorded, and cross-tenant isolation. The encrypt direction (B-28) stays covered by
/// <see cref="FieldProtectionMigrationTests"/>.
/// </summary>
[Collection(StageBStackCollectionDefinition.Name)]
public sealed class FieldProtectionDowngradeTests(StageBFixture fixture)
{
    private sealed record Workspace(StageBScenario Scenario, Guid FormId, Guid InternalId, Guid ConfidentialId, Guid CountId, List<Guid> Records);

    /// <summary>A form with an encrypted Internal String field, an encrypted Confidential String field and a plaintext Integer field, published, with <paramref name="recordCount"/> records holding all three.</summary>
    private async Task<Workspace> EncryptedWorkspaceAsync(string name, int recordCount)
    {
        var s = new StageBScenario(fixture, name);
        await s.BootstrapAsync();
        var formId = await s.CreateFormAsync("Release " + s.Name, s.Clerk);
        var internalId = (await fixture.Api.PostAsync($"/api/v1/form-definitions/{formId:D}/fields", s.Token(s.Designer), new { fieldKey = "sealed_note", fieldDisplayName = "Sealed note", fieldDescription = string.Empty, fieldDataType = "String", options = (object?)null, fieldClassification = "Internal", fieldProtectionMode = "ApplicationEncrypted" })).Root.GetProperty("fieldDefinitionId").GetGuid();
        var confidentialId = (await fixture.Api.PostAsync($"/api/v1/form-definitions/{formId:D}/fields", s.Token(s.Designer), new { fieldKey = "case_note", fieldDisplayName = "Case note", fieldDescription = string.Empty, fieldDataType = "String", options = (object?)null, fieldClassification = "Confidential", fieldProtectionMode = "ApplicationEncrypted" })).Root.GetProperty("fieldDefinitionId").GetGuid();
        var countId = (await fixture.Api.PostAsync($"/api/v1/form-definitions/{formId:D}/fields", s.Token(s.Designer), new { fieldKey = "exhibit_count", fieldDisplayName = "Exhibit count", fieldDescription = string.Empty, fieldDataType = "Integer", options = (object?)null, fieldClassification = "Internal", fieldProtectionMode = "Standard" })).Root.GetProperty("fieldDefinitionId").GetGuid();
        await s.SaveDraftAsync(formId, StageBScenario.Layout("Release", StageBScenario.Section("main", "Main", StageBScenario.Field(internalId, "text", "Sealed note", 4, required: true), StageBScenario.Field(confidentialId, "text", "Case note", 4), StageBScenario.Field(countId, "number", "Exhibit count", 2))));
        await s.PublishAsync(formId, "v1 encrypted");
        var records = new List<Guid>();
        for (var i = 0; i < recordCount; i++)
        {
            var created = await fixture.Api.PostAsync($"/api/v1/form-definitions/{formId:D}/records", s.Token(s.Clerk), new { values = new Dictionary<string, string> { ["sealed_note"] = $"sealed {i} of {s.Name}", ["case_note"] = $"case {i}", ["exhibit_count"] = i.ToString(CultureInfo.InvariantCulture) }, ownerTeamId = (Guid?)null });
            Assert.Equal(HttpStatusCode.Created, created.Status);
            records.Add(created.Root.GetProperty("formRecordId").GetGuid());
        }

        foreach (var record in records)
        {
            await s.WaitProjectionAsync(s.Clerk, record);
        }

        return new Workspace(s, formId, internalId, confidentialId, countId, records);
    }

    /// <summary>The organization owner publishes a policy permitting plaintext and the removal of encryption (authority first, then the local projection).</summary>
    private async Task PermitDowngradesAsync(StageBScenario s, bool permit = true)
    {
        var current = await fixture.Api.GetAsync("/api/v1/organization/security-policy", s.Token(s.Designer));
        Assert.True(current.IsSuccess, current.Body);
        var updated = await fixture.Api.SendAsync(HttpMethod.Put, "/api/v1/organization/security-policy", s.Token(s.Designer), new { minimumFieldClassification = "Internal", plaintextInternalFieldsPermitted = true, plaintextDowngradePermitted = permit, policyStatementText = permit ? "Governance approved the removal of encryption from Internal fields for this test organization." : "Removal of encryption is no longer permitted.", expectedPolicyRevision = current.Root.GetProperty("securityPolicyRevision").GetInt64() }, null);
        Assert.True(updated.IsSuccess, updated.Body);
        Assert.Equal(permit, updated.Root.GetProperty("plaintextDowngradePermitted").GetBoolean());
        Assert.NotNull(updated.Root.GetProperty("authorityCommandId").GetString());
    }

    private Task<HttpResult> DowngradeAsync(Workspace w, TestUser user, Guid fieldId, long expectedRevision, string reason = "approved release to plaintext") =>
        fixture.Api.PostAsync($"/api/v1/form-definitions/{w.FormId:D}/fields/{fieldId:D}/protection/downgrade", w.Scenario.Token(user), new { changeReason = reason, expectedProtectionRevision = expectedRevision });

    private Task<HttpResult> ReclassifyAsync(Workspace w, TestUser user, Guid fieldId, string target = "Internal", string approval = "GOV-2026-017") =>
        fixture.Api.PostAsync($"/api/v1/form-definitions/{w.FormId:D}/fields/{fieldId:D}/protection/reclassify", w.Scenario.Token(user), new { targetClassification = target, approvalReference = approval, changeReason = "governance approved the declassification" });

    private Task<HttpResult> CancelAsync(Workspace w, TestUser user, Guid fieldId) =>
        fixture.Api.PostAsync($"/api/v1/form-definitions/{w.FormId:D}/fields/{fieldId:D}/protection/cancel-downgrade", w.Scenario.Token(user), new { changeReason = "security review withdrew the approval" });

    private Task<HttpResult> StatusAsync(Workspace w, Guid fieldId, TestUser? user = null) => fixture.Api.GetAsync($"/api/v1/form-definitions/{w.FormId:D}/fields/{fieldId:D}/protection", w.Scenario.Token(user ?? w.Scenario.Designer));

    private async Task<JsonElement> WaitMigrationAsync(Workspace w, Guid fieldId, Func<JsonElement, bool> done, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(240));
        JsonElement last = default;
        while (DateTime.UtcNow < deadline)
        {
            var status = await StatusAsync(w, fieldId);
            if (status.Status == HttpStatusCode.ServiceUnavailable)
            {
                await Task.Delay(1000, TestContext.Current.CancellationToken);
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

    private string EncryptedRowsInLatestSnapshots(Workspace w, Guid fieldId) =>
        fixture.Stack.AttackerSql($@"SELECT count(*) FROM field_encrypted_values v WHERE v.field_definition_id = '{fieldId:D}' AND v.content_change_operation_id = (SELECT r.content_change_operation_id FROM form_record_revisions r WHERE r.form_record_id = v.form_record_id ORDER BY r.content_revision DESC LIMIT 1);").Trim();

    private string PlaintextRowsInLatestSnapshots(Workspace w, Guid fieldId) =>
        fixture.Stack.AttackerSql($@"SELECT count(*) FROM field_string_values v WHERE v.field_definition_id = '{fieldId:D}' AND v.content_change_operation_id = (SELECT r.content_change_operation_id FROM form_record_revisions r WHERE r.form_record_id = v.form_record_id ORDER BY r.content_revision DESC LIMIT 1);").Trim();

    /// <summary>The activity feed is actor-scoped; service events of a migration are attributed to the administrator who started it.</summary>
    private async Task<List<string?>> ActionsAsync(Workspace w, TestUser? user = null) =>
        (await fixture.Api.GetAsync("/api/v1/my-activity?limit=300", w.Scenario.Token(user ?? w.Scenario.Designer))).Root.EnumerateArray().Select(e => e.GetProperty("eventAction").GetString()).ToList();

    [Fact]
    public async Task NothingIsReleasedUntilTheOwnerPublishesThePolicyAndDesignersClerksReadersAndOtherTenantsNeverCan()
    {
        var w = await EncryptedWorkspaceAsync("release-gate", 1);
        var revision = (await StatusAsync(w, w.InternalId)).Root.GetProperty("protectionPolicyRevision").GetInt64();

        // Default policy: no plaintext downgrade. Even the organization owner is refused (locally and by the authority).
        var closed = await DowngradeAsync(w, w.Scenario.Designer, w.InternalId, revision);
        Assert.Equal("PLAINTEXT_DOWNGRADE_NOT_PERMITTED", closed.ErrorCode);
        Assert.Equal("PLAINTEXT_DOWNGRADE_NOT_PERMITTED", (await ReclassifyAsync(w, w.Scenario.Designer, w.ConfidentialId)).ErrorCode);

        // A designer or clerk cannot publish the policy; the owner can.
        var designerPolicy = await fixture.Api.SendAsync(HttpMethod.Put, "/api/v1/organization/security-policy", w.Scenario.Token(w.Scenario.DesignerOnly), new { minimumFieldClassification = "Internal", plaintextInternalFieldsPermitted = true, plaintextDowngradePermitted = true, policyStatementText = "designer tries to open the gate", expectedPolicyRevision = 1 }, null);
        Assert.Contains(designerPolicy.Status, new[] { HttpStatusCode.Forbidden, HttpStatusCode.NotFound });
        await PermitDowngradesAsync(w.Scenario);

        // Policy open: a designer, a clerk and a reader still cannot release or declassify anything (Amendment A1 rule 5).
        foreach (var user in new[] { w.Scenario.DesignerOnly, w.Scenario.Clerk, w.Scenario.Reader })
        {
            Assert.Contains((await DowngradeAsync(w, user, w.InternalId, revision)).Status, new[] { HttpStatusCode.Forbidden, HttpStatusCode.NotFound });
            Assert.Contains((await ReclassifyAsync(w, user, w.ConfidentialId)).Status, new[] { HttpStatusCode.Forbidden, HttpStatusCode.NotFound });
            Assert.Contains((await CancelAsync(w, user, w.InternalId)).Status, new[] { HttpStatusCode.Forbidden, HttpStatusCode.NotFound });
        }

        // The designer's own strengthening route still refuses any downgrade, policy or not.
        var viaStrengthen = await fixture.Api.PostAsync($"/api/v1/form-definitions/{w.FormId:D}/fields/{w.InternalId:D}/protection", w.Scenario.Token(w.Scenario.Designer), new { targetClassification = "Internal", targetProtectionMode = "Standard", changeReason = "designer checkbox", expectedProtectionRevision = revision });
        Assert.Equal("FIELD_PROTECTION_DOWNGRADE_REFUSED", viaStrengthen.ErrorCode);

        // Another organization's administrator cannot touch this form, even with its own policy open.
        var other = await EncryptedWorkspaceAsync("release-gate-other", 0);
        await PermitDowngradesAsync(other.Scenario);
        var cross = await fixture.Api.PostAsync($"/api/v1/form-definitions/{w.FormId:D}/fields/{w.InternalId:D}/protection/downgrade", other.Scenario.Token(other.Scenario.Administrator), new { changeReason = "cross-tenant", expectedProtectionRevision = revision });
        Assert.Equal(HttpStatusCode.NotFound, cross.Status);
        Assert.Equal(HttpStatusCode.NotFound, (await fixture.Api.PostAsync($"/api/v1/form-definitions/{w.FormId:D}/fields/{w.ConfidentialId:D}/protection/reclassify", other.Scenario.Token(other.Scenario.Administrator), new { targetClassification = "Internal", approvalReference = "X", changeReason = "cross-tenant" })).Status);

        // A stale revision is refused; wrong field kinds are refused; the field is still encrypted throughout.
        Assert.Equal("FIELD_REVISION_CONFLICT", (await DowngradeAsync(w, w.Scenario.Administrator, w.InternalId, revision + 7)).ErrorCode);
        Assert.Equal("INVALID_REQUEST", (await DowngradeAsync(w, w.Scenario.Administrator, w.CountId, 1)).ErrorCode); // already plaintext
        Assert.Equal("DECLASSIFICATION_REQUIRED", (await DowngradeAsync(w, w.Scenario.Administrator, w.ConfidentialId, 1)).ErrorCode);
        var still = await StatusAsync(w, w.InternalId);
        Assert.Equal("ApplicationEncrypted", still.Root.GetProperty("fieldProtectionMode").GetString());
        Assert.Equal("1", EncryptedRowsInLatestSnapshots(w, w.InternalId));
        Assert.Equal("0", fixture.Stack.AttackerSql($"SELECT count(*) FROM field_protection_migrations WHERE field_definition_id = '{w.InternalId:D}';").Trim());
    }

    [Fact]
    public async Task ReleasingAnInternalFieldFlipsTheBarrierDecryptsEveryRecordThroughTheAuthorityAndClaimsPlaintextOnlyAfterVerification()
    {
        var w = await EncryptedWorkspaceAsync("release", 6);
        await PermitDowngradesAsync(w.Scenario);
        var before = await StatusAsync(w, w.InternalId);
        Assert.True(before.Root.GetProperty("fullyEncrypted").GetBoolean());
        Assert.False(before.Root.GetProperty("fullyPlaintext").GetBoolean());
        Assert.Equal("6", EncryptedRowsInLatestSnapshots(w, w.InternalId));
        var revisionsBefore = w.Records.ToDictionary(r => r, r => long.Parse(fixture.Stack.AttackerSql($"SELECT max(content_revision) FROM form_record_revisions WHERE form_record_id = '{r:D}';").Trim(), CultureInfo.InvariantCulture));

        // 1. The system administrator releases the field: the authority's command is the cutover; the migration is queued with the "before" counts; nothing claims plaintext yet.
        var released = await DowngradeAsync(w, w.Scenario.Administrator, w.InternalId, before.Root.GetProperty("protectionPolicyRevision").GetInt64());
        Assert.True(released.IsSuccess, released.Body);
        Assert.Equal("Standard", released.Root.GetProperty("fieldProtectionMode").GetString());
        Assert.Equal("Internal", released.Root.GetProperty("fieldClassification").GetString());
        Assert.False(released.Root.GetProperty("fullyPlaintext").GetBoolean());
        Assert.False(released.Root.GetProperty("fullyEncrypted").GetBoolean());
        var migration = released.Root.GetProperty("migration");
        Assert.Equal("Decrypt", migration.GetProperty("migrationDirection").GetString());
        Assert.Equal(6, migration.GetProperty("totalRecordCount").GetInt32());
        Assert.Contains(MigrationState(released.Root), new[] { "Queued", "Running" });
        Assert.StartsWith("Decryption", released.Root.GetProperty("migrationState").GetString(), StringComparison.Ordinal);
        var commandId = migration.GetProperty("authorityCommandId").GetGuid();
        Assert.Equal("Standard|" + commandId.ToString("D"), fixture.Stack.AttackerSql($"SELECT field_protection_mode || '|' || protection_authority_command_id FROM field_definitions WHERE field_definition_id = '{w.InternalId:D}';").Trim());

        // Overlapping changes are refused while the migration runs.
        Assert.Equal("MIGRATION_IN_PROGRESS", (await fixture.Api.PostAsync($"/api/v1/form-definitions/{w.FormId:D}/fields/{w.InternalId:D}/protection", w.Scenario.Token(w.Scenario.Designer), new { targetClassification = "Internal", targetProtectionMode = "ApplicationEncrypted", changeReason = "re-encrypt right away", expectedProtectionRevision = released.Root.GetProperty("protectionPolicyRevision").GetInt64() })).ErrorCode);

        // 2. The worker decrypts every record; the field reports Plaintext + fullyPlaintext only when complete and verified against the authority heads.
        var done = await WaitMigrationAsync(w, w.InternalId, s => s.GetProperty("fullyPlaintext").GetBoolean());
        Assert.Equal("Plaintext", done.GetProperty("migrationState").GetString());
        Assert.Equal("Completed", MigrationState(done));
        Assert.Equal(6, done.GetProperty("migration").GetProperty("completedRecordCount").GetInt32());
        Assert.Equal(0, done.GetProperty("migration").GetProperty("failedRecordCount").GetInt32());
        Assert.NotNull(done.GetProperty("migration").GetProperty("verifiedOn").GetString());
        Assert.Equal(0, done.GetProperty("legacyEncryptedRecordCount").GetInt32());

        // 3. Business PostgreSQL: no encrypted row of the field in any latest snapshot, a typed plaintext row instead; the other fields untouched; one new revision per record; history kept.
        Assert.Equal("0", EncryptedRowsInLatestSnapshots(w, w.InternalId));
        Assert.Equal("6", PlaintextRowsInLatestSnapshots(w, w.InternalId));
        Assert.Equal("6", EncryptedRowsInLatestSnapshots(w, w.ConfidentialId));
        foreach (var record in w.Records)
        {
            Assert.Equal(revisionsBefore[record] + 1, long.Parse(fixture.Stack.AttackerSql($"SELECT max(content_revision) FROM form_record_revisions WHERE form_record_id = '{record:D}';").Trim(), CultureInfo.InvariantCulture));
        }

        Assert.Equal("6", fixture.Stack.AttackerSql($"SELECT count(*) FROM field_encrypted_values WHERE field_definition_id = '{w.InternalId:D}';").Trim()); // immutable history, never rewritten

        // 4. Every record still verifies and reads for the clerk as plaintext; the reader is still denied (no new access from the migration).
        for (var i = 0; i < w.Records.Count; i++)
        {
            var projection = await w.Scenario.WaitProjectionAsync(w.Scenario.Clerk, w.Records[i]);
            var values = projection.Root.GetProperty("record").GetProperty("values").EnumerateArray().ToList();
            var note = values.Single(v => v.GetProperty("fieldKey").GetString() == "sealed_note");
            Assert.Equal($"sealed {i} of {w.Scenario.Name}", note.GetProperty("stringValue").GetString());
            Assert.False(note.GetProperty("applicationEncrypted").GetBoolean());
            Assert.True(values.Single(v => v.GetProperty("fieldKey").GetString() == "case_note").GetProperty("applicationEncrypted").GetBoolean());
            Assert.Equal("Verified", projection.Root.GetProperty("record").GetProperty("integrityStatus").GetString());
        }

        Assert.Contains((await w.Scenario.ProjectionAsync(w.Scenario.Reader, w.Records[0])).Status, new[] { HttpStatusCode.Forbidden, HttpStatusCode.NotFound });

        // 5. Published v1 is unchanged (immutable snapshot still says ApplicationEncrypted); the audit trail names the release, every record and the verified completion, without plaintext values.
        var versions = await fixture.Api.GetAsync($"/api/v1/form-definitions/{w.FormId:D}/versions", w.Scenario.Token(w.Scenario.Designer));
        Assert.Single(versions.Root.EnumerateArray());
        var activity = await fixture.Api.GetAsync("/api/v1/my-activity?limit=300", w.Scenario.Token(w.Scenario.Administrator)); // the administrator released the field; the feed is actor-scoped
        var actions = activity.Root.EnumerateArray().Select(e => e.GetProperty("eventAction").GetString()).ToList();
        Assert.Contains("FIELD_PROTECTION_DOWNGRADED", actions);
        Assert.Contains("FIELD_PROTECTION_MIGRATION_STARTED", actions);
        Assert.Contains("FIELD_PROTECTION_MIGRATION_COMPLETED", actions);
        Assert.Equal(6, actions.Count(a => a == "FIELD_VALUES_DEPROTECTED"));
        Assert.DoesNotContain("sealed 0 of", activity.Body, StringComparison.Ordinal);

        // 6. New writes are plaintext; the field can be strengthened again later (a new requirement), and that is a normal encrypt migration.
        var head = await w.Scenario.WaitProjectionAsync(w.Scenario.Clerk, w.Records[0]);
        var edit = await fixture.Api.PatchAsync($"/api/v1/form-records/{w.Records[0]:D}", w.Scenario.Token(w.Scenario.Clerk), new { expectedContentRevision = head.Root.GetProperty("record").GetProperty("contentRevision").GetInt64(), values = new Dictionary<string, string> { ["sealed_note"] = "written after the release" } });
        Assert.True(edit.IsSuccess, edit.Body);
        var latest = fixture.Stack.AttackerSql($"SELECT content_change_operation_id FROM form_record_revisions WHERE form_record_id = '{w.Records[0]:D}' ORDER BY content_revision DESC LIMIT 1;").Trim();
        Assert.Equal("written after the release", fixture.Stack.AttackerSql($"SELECT string_value FROM field_string_values WHERE form_record_id = '{w.Records[0]:D}' AND field_definition_id = '{w.InternalId:D}' AND content_change_operation_id = '{latest}';").Trim());
        var strengthened = await fixture.Api.PostAsync($"/api/v1/form-definitions/{w.FormId:D}/fields/{w.InternalId:D}/protection", w.Scenario.Token(w.Scenario.Designer), new { targetClassification = "Internal", targetProtectionMode = "ApplicationEncrypted", changeReason = "re-encrypt after the exception ended", expectedProtectionRevision = done.GetProperty("protectionPolicyRevision").GetInt64() });
        Assert.True(strengthened.IsSuccess, strengthened.Body);
        Assert.Equal("Encrypt", strengthened.Root.GetProperty("migration").GetProperty("migrationDirection").GetString());
        var reencrypted = await WaitMigrationAsync(w, w.InternalId, s => s.GetProperty("fullyEncrypted").GetBoolean());
        Assert.Equal("Encrypted", reencrypted.GetProperty("migrationState").GetString());
        Assert.Equal("0", PlaintextRowsInLatestSnapshots(w, w.InternalId));
    }

    [Fact]
    public async Task AConfidentialFieldNeedsADeclassificationByOneAdministratorAndTheReleaseByADifferentOne()
    {
        var w = await EncryptedWorkspaceAsync("release-two-person", 2);
        await PermitDowngradesAsync(w.Scenario);
        var revision = (await StatusAsync(w, w.ConfidentialId)).Root.GetProperty("protectionPolicyRevision").GetInt64();

        // Release without declassification: refused locally; the approval reference is mandatory; a designer cannot declassify.
        Assert.Equal("DECLASSIFICATION_REQUIRED", (await DowngradeAsync(w, w.Scenario.Administrator, w.ConfidentialId, revision)).ErrorCode);
        Assert.Equal("INVALID_REQUEST", (await ReclassifyAsync(w, w.Scenario.Designer, w.ConfidentialId, approval: " ")).ErrorCode);
        Assert.Equal("INVALID_REQUEST", (await ReclassifyAsync(w, w.Scenario.Designer, w.ConfidentialId, target: "Confidential")).ErrorCode);
        Assert.Contains((await ReclassifyAsync(w, w.Scenario.DesignerOnly, w.ConfidentialId)).Status, new[] { HttpStatusCode.Forbidden, HttpStatusCode.NotFound });

        // Owner declassifies (authority ReclassifyField command): the field is now Internal but still encrypted; readers unchanged.
        var declassified = await ReclassifyAsync(w, w.Scenario.Designer, w.ConfidentialId);
        Assert.True(declassified.IsSuccess, declassified.Body);
        Assert.Equal("Internal", declassified.Root.GetProperty("fieldClassification").GetString());
        Assert.Equal("ApplicationEncrypted", declassified.Root.GetProperty("fieldProtectionMode").GetString());
        Assert.True(declassified.Root.GetProperty("fullyEncrypted").GetBoolean());
        Assert.Equal("2", EncryptedRowsInLatestSnapshots(w, w.ConfidentialId));
        Assert.Contains("FIELD_DECLASSIFIED", await ActionsAsync(w));
        var declassifiedRevision = declassified.Root.GetProperty("protectionPolicyRevision").GetInt64();

        // The same principal (the owner who declassified) cannot release: the authority refuses the command (distinct principal rule).
        var samePerson = await DowngradeAsync(w, w.Scenario.Designer, w.ConfidentialId, declassifiedRevision);
        Assert.False(samePerson.IsSuccess, samePerson.Body);
        Assert.Contains(samePerson.Status, new[] { HttpStatusCode.Forbidden, HttpStatusCode.UnprocessableEntity });
        Assert.Equal("ApplicationEncrypted", (await StatusAsync(w, w.ConfidentialId)).Root.GetProperty("fieldProtectionMode").GetString());

        // A different administrator releases it; the migration decrypts both records.
        var released = await DowngradeAsync(w, w.Scenario.Administrator, w.ConfidentialId, declassifiedRevision);
        Assert.True(released.IsSuccess, released.Body);
        Assert.Equal("Standard", released.Root.GetProperty("fieldProtectionMode").GetString());
        var done = await WaitMigrationAsync(w, w.ConfidentialId, s => s.GetProperty("fullyPlaintext").GetBoolean());
        Assert.Equal("Plaintext", done.GetProperty("migrationState").GetString());
        Assert.Equal("0", EncryptedRowsInLatestSnapshots(w, w.ConfidentialId));
        var projection = await w.Scenario.WaitProjectionAsync(w.Scenario.Clerk, w.Records[1]);
        var note = projection.Root.GetProperty("record").GetProperty("values").EnumerateArray().Single(v => v.GetProperty("fieldKey").GetString() == "case_note");
        Assert.Equal("case 1", note.GetProperty("stringValue").GetString());
        Assert.False(note.GetProperty("applicationEncrypted").GetBoolean());

        // The declassification was consumed with the release: re-encrypting and releasing again needs a fresh two-person round
        // (the authority recorded the re-publication as Internal, so the second release needs no declassification but still an administrator).
        var strengthened = await fixture.Api.PostAsync($"/api/v1/form-definitions/{w.FormId:D}/fields/{w.ConfidentialId:D}/protection", w.Scenario.Token(w.Scenario.Designer), new { targetClassification = "Internal", targetProtectionMode = "ApplicationEncrypted", changeReason = "seal again", expectedProtectionRevision = done.GetProperty("protectionPolicyRevision").GetInt64() });
        Assert.True(strengthened.IsSuccess, strengthened.Body);
        await WaitMigrationAsync(w, w.ConfidentialId, s => s.GetProperty("fullyEncrypted").GetBoolean());
        Assert.Contains((await DowngradeAsync(w, w.Scenario.DesignerOnly, w.ConfidentialId, strengthened.Root.GetProperty("protectionPolicyRevision").GetInt64())).Status, new[] { HttpStatusCode.Forbidden, HttpStatusCode.NotFound });
    }

    [Fact]
    public async Task ConcurrentWritesAcrossTheCutoverCannotReintroduceCiphertextAndNoEditIsLost()
    {
        var w = await EncryptedWorkspaceAsync("release-race", 24);
        await PermitDowngradesAsync(w.Scenario);
        var revision = (await StatusAsync(w, w.InternalId)).Root.GetProperty("protectionPolicyRevision").GetInt64();
        var targets = w.Records.Take(6).ToList();
        var writes = new Dictionary<Guid, int>();
        var refused = 0;
        using var stop = new CancellationTokenSource();

        // Writers hammer the migrating field before, during and after the release command; every write either commits or is refused for a stated reason, never silently.
        async Task WriterAsync(Guid record)
        {
            var n = 0;
            while (!stop.IsCancellationRequested)
            {
                var head = await w.Scenario.ProjectionAsync(w.Scenario.Clerk, record);
                if (!head.IsSuccess)
                {
                    await Task.Delay(100);
                    continue;
                }

                var edit = await fixture.Api.PatchAsync($"/api/v1/form-records/{record:D}", w.Scenario.Token(w.Scenario.Clerk), new { expectedContentRevision = head.Root.GetProperty("record").GetProperty("contentRevision").GetInt64(), values = new Dictionary<string, string> { ["sealed_note"] = $"race write {++n} on {record:N}" } });
                if (edit.IsSuccess)
                {
                    lock (writes)
                    {
                        writes[record] = n;
                    }
                }
                else
                {
                    // Refusals are stated reasons (revision conflict, the cutover barrier, a transient 503) — never a silent partial write; the final state below is what matters.
                    Assert.True(edit.Status is HttpStatusCode.Conflict or HttpStatusCode.ServiceUnavailable or HttpStatusCode.PreconditionFailed or HttpStatusCode.Locked, edit.Body);
                    if (edit.ErrorCode == "FIELD_PROTECTION_CHANGED")
                    {
                        Interlocked.Increment(ref refused);
                    }

                    n--;
                }
            }
        }

        var writers = targets.Select(WriterAsync).ToList();
        await Task.Delay(1500, TestContext.Current.CancellationToken);
        var released = await DowngradeAsync(w, w.Scenario.Administrator, w.InternalId, revision);
        Assert.True(released.IsSuccess, released.Body);
        fixture.Stack.Compose("restart worker", 120);
        await Task.Delay(4000, TestContext.Current.CancellationToken);
        stop.Cancel();
        await Task.WhenAll(writers);

        var done = await WaitMigrationAsync(w, w.InternalId, s => s.GetProperty("fullyPlaintext").GetBoolean(), TimeSpan.FromSeconds(420));
        Assert.Equal("Completed", MigrationState(done));
        Assert.Equal(24, done.GetProperty("migration").GetProperty("completedRecordCount").GetInt32());

        // The barrier held: no encrypted row of the field survives in any latest snapshot, and every record's last committed write is visible as plaintext.
        Assert.Equal("0", EncryptedRowsInLatestSnapshots(w, w.InternalId));
        Assert.Equal("24", PlaintextRowsInLatestSnapshots(w, w.InternalId));
        foreach (var record in targets)
        {
            var projection = await w.Scenario.WaitProjectionAsync(w.Scenario.Clerk, record);
            var note = projection.Root.GetProperty("record").GetProperty("values").EnumerateArray().Single(v => v.GetProperty("fieldKey").GetString() == "sealed_note");
            Assert.False(note.GetProperty("applicationEncrypted").GetBoolean());
            Assert.Equal(writes.TryGetValue(record, out var last) ? $"race write {last} on {record:N}" : $"sealed {w.Records.IndexOf(record)} of {w.Scenario.Name}", note.GetProperty("stringValue").GetString());
            Assert.Equal("Verified", projection.Root.GetProperty("record").GetProperty("integrityStatus").GetString());
        }

        Assert.True(writes.Count > 0, "no concurrent write ever committed; the race was not exercised");
        // Encrypted rows written by user commits after the release never reached an accepted head (the authority refuses them at prepare and at commit).
        Assert.Equal("0", fixture.Stack.AttackerSql($"SELECT count(*) FROM field_encrypted_values e WHERE e.field_definition_id = '{w.InternalId:D}' AND e.content_change_operation_id IN (SELECT r.content_change_operation_id FROM form_record_revisions r WHERE r.form_record_id = e.form_record_id AND r.content_revision > (SELECT max(m.completed_content_revision) FROM field_protection_migration_items m WHERE m.form_record_id = e.form_record_id));").Trim());
        _ = refused; // observed count of barrier refusals (0 is legitimate when no writer straddled the cutover)
    }

    [Fact]
    public async Task InterruptedAndTamperedMigrationsNeverClaimPlaintextAndForgedCompletionIsIgnored()
    {
        var w = await EncryptedWorkspaceAsync("release-adversarial", 5);
        await PermitDowngradesAsync(w.Scenario);
        var tampered = w.Records[2];
        // A hostile database writer corrupts the committed ciphertext of one record before the migration starts.
        fixture.Stack.AttackerSql($"UPDATE field_encrypted_values SET encrypted_value_bytes = decode('00', 'hex') || encrypted_value_bytes WHERE form_record_id = '{tampered:D}' AND field_definition_id = '{w.InternalId:D}';");

        // The authority is unreachable: the release command cannot be accepted, so nothing changes.
        fixture.Stack.Pause("authority");
        try
        {
            var during = await DowngradeAsync(w, w.Scenario.Administrator, w.InternalId, 1);
            Assert.False(during.IsSuccess, during.Body);
        }
        finally
        {
            fixture.Stack.Unpause("authority");
        }

        await w.Scenario.WaitGateReadyAsync();
        Assert.Equal("ApplicationEncrypted", fixture.Stack.AttackerSql($"SELECT field_protection_mode FROM field_definitions WHERE field_definition_id = '{w.InternalId:D}';").Trim());
        var released = await DowngradeAsync(w, w.Scenario.Administrator, w.InternalId, 1);
        Assert.True(released.IsSuccess, released.Body);

        // Interruption: the authority goes away mid-migration; the worker parks the migration as Blocked and resumes afterwards.
        fixture.Stack.Pause("authority");
        try
        {
            var blockedDeadline = DateTime.UtcNow.AddSeconds(120);
            string row;
            do
            {
                row = fixture.Stack.AttackerSql($"SELECT migration_state || '|' || coalesce(last_error_code, '') FROM field_protection_migrations WHERE field_definition_id = '{w.InternalId:D}';").Trim();
                if (row.StartsWith("Blocked|", StringComparison.Ordinal) || row.StartsWith("Completed|", StringComparison.Ordinal) || row.StartsWith("Failed|", StringComparison.Ordinal))
                {
                    break;
                }

                await Task.Delay(1000, TestContext.Current.CancellationToken);
            }
            while (DateTime.UtcNow < blockedDeadline);
            Assert.False(row.StartsWith("Completed|", StringComparison.Ordinal), row);
        }
        finally
        {
            fixture.Stack.Unpause("authority");
        }

        fixture.Stack.Compose("restart worker", 120);

        // Four records decrypt; the tampered one fails (its ciphertext does not authenticate / its row does not verify) and stays as it is; the field is NOT fully plaintext.
        var ended = await WaitMigrationAsync(w, w.InternalId, s => MigrationState(s) is "Failed" or "Completed", TimeSpan.FromSeconds(420));
        Assert.Equal("Failed", MigrationState(ended));
        Assert.Equal("DecryptionMigrationFailed", ended.GetProperty("migrationState").GetString());
        Assert.False(ended.GetProperty("fullyPlaintext").GetBoolean());
        Assert.Equal(4, ended.GetProperty("migration").GetProperty("completedRecordCount").GetInt32());
        Assert.Equal(1, ended.GetProperty("migration").GetProperty("failedRecordCount").GetInt32());
        Assert.Equal(1, ended.GetProperty("legacyEncryptedRecordCount").GetInt32());
        Assert.Equal("Failed", fixture.Stack.AttackerSql($"SELECT item_state FROM field_protection_migration_items WHERE form_record_id = '{tampered:D}';").Trim());
        Assert.Contains(await ActionsAsync(w, w.Scenario.Administrator), a => a == "FIELD_PROTECTION_MIGRATION_FAILED");

        // Forged completion in the business database does not make the field report as fully plaintext (the status recounts against the authority-accepted heads).
        fixture.Stack.AttackerSql($"UPDATE field_protection_migrations SET migration_state = 'Completed', failed_record_count = 0, completed_record_count = 5, migration_verified_on = now() WHERE field_definition_id = '{w.InternalId:D}'; UPDATE field_protection_migration_items SET item_state = 'Completed' WHERE form_record_id = '{tampered:D}';");
        var forged = await StatusAsync(w, w.InternalId);
        Assert.False(forged.Root.GetProperty("fullyPlaintext").GetBoolean());
        Assert.NotEqual("Plaintext", forged.Root.GetProperty("migrationState").GetString());

        // Retrying a decrypt migration is an administrator action (designers may retry encrypt migrations only); the tampered record stays refused.
        fixture.Stack.AttackerSql($"UPDATE field_protection_migrations SET migration_state = 'Failed', failed_record_count = 1, completed_record_count = 4, migration_verified_on = NULL WHERE field_definition_id = '{w.InternalId:D}'; UPDATE field_protection_migration_items SET item_state = 'Failed' WHERE form_record_id = '{tampered:D}';");
        Assert.Contains((await fixture.Api.PostAsync($"/api/v1/form-definitions/{w.FormId:D}/fields/{w.InternalId:D}/protection/retry", w.Scenario.Token(w.Scenario.DesignerOnly), new { retryReason = "designer tries" })).Status, new[] { HttpStatusCode.Forbidden, HttpStatusCode.NotFound });
        var retried = await fixture.Api.PostAsync($"/api/v1/form-definitions/{w.FormId:D}/fields/{w.InternalId:D}/protection/retry", w.Scenario.Token(w.Scenario.Administrator), new { retryReason = "after investigating the record" });
        Assert.True(retried.IsSuccess, retried.Body);
        var retriedEnd = await WaitMigrationAsync(w, w.InternalId, s => MigrationState(s) is "Failed" or "Completed", TimeSpan.FromSeconds(300));
        Assert.Equal("Failed", MigrationState(retriedEnd));
        Assert.False(retriedEnd.GetProperty("fullyPlaintext").GetBoolean());
    }

    [Fact]
    public async Task CancellingAReleaseReEncryptsConvertedValuesAndRecordsTheIrreversibleExposureWindow()
    {
        var w = await EncryptedWorkspaceAsync("release-rollback", 8);
        await PermitDowngradesAsync(w.Scenario);
        var nothingToCancel = await CancelAsync(w, w.Scenario.Administrator, w.InternalId);
        Assert.Contains(nothingToCancel.ErrorCode, new[] { "NO_DOWNGRADE_TO_CANCEL", "NOT_FOUND" }); // no downgrade exists yet
        var released = await DowngradeAsync(w, w.Scenario.Administrator, w.InternalId, 1);
        Assert.True(released.IsSuccess, released.Body);
        var decryptId = released.Root.GetProperty("migration").GetProperty("fieldProtectionMigrationId").GetGuid();

        // Let the migration make progress (or finish), then the owner cancels: encryption is re-required through the authority, the decrypt migration is Cancelled, and a rollback Encrypt migration is queued.
        await WaitMigrationAsync(w, w.InternalId, s => s.GetProperty("migration").GetProperty("completedRecordCount").GetInt32() >= 2 || MigrationState(s) is "Completed" or "Failed");
        var cancelled = await CancelAsync(w, w.Scenario.Designer, w.InternalId);
        Assert.True(cancelled.IsSuccess, cancelled.Body);
        Assert.Equal("ApplicationEncrypted", cancelled.Root.GetProperty("fieldProtectionMode").GetString());
        var rollback = cancelled.Root.GetProperty("migration");
        Assert.Equal("Encrypt", rollback.GetProperty("migrationDirection").GetString());
        Assert.Equal(decryptId, rollback.GetProperty("rollbackOfMigrationId").GetGuid());
        Assert.Equal("Cancelled", fixture.Stack.AttackerSql($"SELECT migration_state FROM field_protection_migrations WHERE field_protection_migration_id = '{decryptId:D}';").Trim());
        Assert.Equal("NO_DOWNGRADE_TO_CANCEL", (await CancelAsync(w, w.Scenario.Administrator, w.InternalId)).ErrorCode);

        var done = await WaitMigrationAsync(w, w.InternalId, s => s.GetProperty("fullyEncrypted").GetBoolean(), TimeSpan.FromSeconds(420));
        Assert.Equal("Encrypted", done.GetProperty("migrationState").GetString());
        Assert.Equal("0", PlaintextRowsInLatestSnapshots(w, w.InternalId));
        Assert.Equal("8", EncryptedRowsInLatestSnapshots(w, w.InternalId));
        Assert.Equal("Completed", fixture.Stack.AttackerSql($"SELECT migration_state FROM field_protection_migrations WHERE rollback_of_migration_id = '{decryptId:D}';").Trim());
        Assert.Equal("Cancelled", fixture.Stack.AttackerSql($"SELECT migration_state FROM field_protection_migrations WHERE field_protection_migration_id = '{decryptId:D}';").Trim()); // the cancelled migration never resumes

        // Values read correctly and are encrypted again; the audit records the cancellation with the exposure window, and states plainly that historical plaintext is not reversible.
        for (var i = 0; i < w.Records.Count; i++)
        {
            var projection = await w.Scenario.WaitProjectionAsync(w.Scenario.Clerk, w.Records[i]);
            var note = projection.Root.GetProperty("record").GetProperty("values").EnumerateArray().Single(v => v.GetProperty("fieldKey").GetString() == "sealed_note");
            Assert.Equal($"sealed {i} of {w.Scenario.Name}", note.GetProperty("stringValue").GetString());
            Assert.True(note.GetProperty("applicationEncrypted").GetBoolean());
        }

        var activity = await fixture.Api.GetAsync("/api/v1/my-activity?limit=300", w.Scenario.Token(w.Scenario.Designer));
        var cancelEvent = activity.Root.EnumerateArray().Single(e => e.GetProperty("eventAction").GetString() == "FIELD_PROTECTION_DOWNGRADE_CANCELLED");
        Assert.Contains("not reversible", cancelEvent.GetProperty("eventDescription").GetString(), StringComparison.Ordinal);
        Assert.Contains("exposureWindowStart", fixture.Stack.AttackerSql($"SELECT event_metadata_json FROM audit_events WHERE audit_event_id = '{cancelEvent.GetProperty("auditEventId").GetGuid():D}';"), StringComparison.Ordinal);
        // The plaintext written during the window is still in the immutable history of the business database: the test states the residual, it does not hide it.
        Assert.True(int.Parse(fixture.Stack.AttackerSql($"SELECT count(*) FROM field_string_values WHERE field_definition_id = '{w.InternalId:D}';").Trim(), CultureInfo.InvariantCulture) >= 2);
    }
}
