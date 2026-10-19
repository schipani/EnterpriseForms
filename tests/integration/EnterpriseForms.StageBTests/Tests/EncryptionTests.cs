using System.Net;
using System.Text.Json;
using EnterpriseForms.SecurityTests.Harness;
using EnterpriseForms.StageBTests.Harness;
using Xunit;

namespace EnterpriseForms.StageBTests.Tests;

/// <summary>
/// Phase 1B — application-level field encryption (ADR-PB-004, CLAUDE.md §4C): encrypted and plaintext fields, ciphertext
/// in Business PostgreSQL, authorized decryption, denial for unauthorized users and a hostile database administrator,
/// policy enforcement against direct API requests, immutable published versions, the audited plaintext→encrypted
/// migration, and fail-closed handling of tampered envelopes. Runs against the real Docker stack.
/// </summary>
[Collection(StageBStackCollectionDefinition.Name)]
public sealed class EncryptionTests(StageBFixture fixture)
{
    private static readonly HttpStatusCode[] Denied = [HttpStatusCode.Forbidden, HttpStatusCode.NotFound];

    private Task<HttpResult> AddFieldAsync(StageBScenario s, Guid formId, string key, string name, string dataType, string? classification, string? protectionMode) =>
        fixture.Api.PostAsync($"/api/v1/form-definitions/{formId:D}/fields", s.Token(s.Designer), new { fieldKey = key, fieldDisplayName = name, fieldDescription = string.Empty, fieldDataType = dataType, options = (object?)null, fieldClassification = classification, fieldProtectionMode = protectionMode });

    private static Dictionary<string, JsonElement> ValuesByKey(JsonElement projection) =>
        projection.GetProperty("record").GetProperty("values").EnumerateArray().ToDictionary(v => v.GetProperty("fieldKey").GetString()!, v => v);

    [Fact]
    public async Task EncryptedFieldsAreCiphertextInPostgresAndPlaintextOnlyForAuthorizedReaders()
    {
        var s = new StageBScenario(fixture, "encryption");
        await s.BootstrapAsync();
        var formId = await s.CreateFormAsync("Vault " + s.Name, s.Clerk);

        // Defaults: free text is Confidential + encrypted; a structured value is Internal + plaintext under the development policy.
        var secret = await AddFieldAsync(s, formId, "case_notes", "Case notes", "String", null, null);
        Assert.Equal(HttpStatusCode.Created, secret.Status);
        Assert.Equal("Confidential", secret.Root.GetProperty("fieldClassification").GetString());
        Assert.Equal("ApplicationEncrypted", secret.Root.GetProperty("fieldProtectionMode").GetString());
        var secretId = secret.Root.GetProperty("fieldDefinitionId").GetGuid();
        var count = await AddFieldAsync(s, formId, "exhibit_count", "Exhibit count", "Integer", null, null);
        Assert.Equal("Internal", count.Root.GetProperty("fieldClassification").GetString());
        Assert.Equal("Standard", count.Root.GetProperty("fieldProtectionMode").GetString());
        var countId = count.Root.GetProperty("fieldDefinitionId").GetGuid();
        var restricted = await AddFieldAsync(s, formId, "informant_name", "Informant name", "String", "Restricted", null);
        Assert.Equal("ApplicationEncrypted", restricted.Root.GetProperty("fieldProtectionMode").GetString());
        var restrictedId = restricted.Root.GetProperty("fieldDefinitionId").GetGuid();
        var encryptedDate = await AddFieldAsync(s, formId, "sealed_on", "Sealed on", "Date", "Internal", "ApplicationEncrypted");
        Assert.Equal("ApplicationEncrypted", encryptedDate.Root.GetProperty("fieldProtectionMode").GetString());
        var encryptedDateId = encryptedDate.Root.GetProperty("fieldDefinitionId").GetGuid();

        // Policy cannot be bypassed by a direct request: Confidential/Restricted plaintext and sub-floor classifications are refused.
        var bypass = await AddFieldAsync(s, formId, "leak_text", "Leak", "String", "Confidential", "Standard");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, bypass.Status);
        Assert.Equal("FIELD_PROTECTION_REQUIRED", bypass.ErrorCode);
        var restrictedPlain = await AddFieldAsync(s, formId, "leak_two", "Leak", "Integer", "Restricted", "Standard");
        Assert.Equal("FIELD_PROTECTION_REQUIRED", restrictedPlain.ErrorCode);
        var belowFloor = await AddFieldAsync(s, formId, "leak_three", "Leak", "Integer", "Public", "Standard");
        Assert.Equal("CLASSIFICATION_BELOW_FLOOR", belowFloor.ErrorCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await AddFieldAsync(s, formId, "leak_four", "Leak", "String", "TopSecret", null)).Status);

        await s.SaveDraftAsync(formId, StageBScenario.Layout("Vault", StageBScenario.Section("main", "Main",
            StageBScenario.Field(secretId, "textarea", "Case notes", 4, required: true),
            StageBScenario.Field(countId, "number", "Exhibit count", 2),
            StageBScenario.Field(restrictedId, "text", "Informant name", 2),
            StageBScenario.Field(encryptedDateId, "date", "Sealed on", 2))));
        var published = await s.PublishAsync(formId, "initial vault");
        using (var layout = JsonDocument.Parse(published.GetProperty("layoutJson").GetString()!))
        {
            // The published version declares each field's protection (immutable snapshot).
            var protections = layout.RootElement.GetProperty("sections")[0].GetProperty("elements").EnumerateArray().ToDictionary(e => e.GetProperty("fieldDefinitionId").GetGuid(), e => e.GetProperty("protection"));
            Assert.Equal("ApplicationEncrypted", protections[secretId].GetProperty("protectionMode").GetString());
            Assert.Equal("Confidential", protections[secretId].GetProperty("classification").GetString());
            Assert.Equal("Standard", protections[countId].GetProperty("protectionMode").GetString());
        }

        const string notes = "Informant met the accused at the marina on 4 March";
        const string informant = "ZETA-7741-UNIQUE";
        var created = await fixture.Api.PostAsync($"/api/v1/form-definitions/{formId:D}/records", s.Token(s.Clerk), new { values = new Dictionary<string, string> { ["case_notes"] = notes, ["exhibit_count"] = "3", ["informant_name"] = informant, ["sealed_on"] = "2026-03-01" }, ownerTeamId = (Guid?)null });
        Assert.Equal(HttpStatusCode.Created, created.Status);
        var recordId = created.Root.GetProperty("formRecordId").GetGuid();
        Assert.True(created.Root.GetProperty("values").EnumerateArray().Single(v => v.GetProperty("fieldKey").GetString() == "case_notes").GetProperty("applicationEncrypted").GetBoolean());

        // Business PostgreSQL holds ciphertext: no plaintext row for the encrypted fields, no plaintext bytes anywhere in the encrypted table, plaintext for the Standard field.
        var stringRows = fixture.Stack.AttackerSql($"SELECT count(*) FROM field_string_values WHERE form_record_id = '{recordId:D}';").Trim();
        Assert.Equal("0", stringRows);
        var dateRows = fixture.Stack.AttackerSql($"SELECT count(*) FROM field_date_values WHERE form_record_id = '{recordId:D}';").Trim();
        Assert.Equal("0", dateRows);
        var encryptedRows = fixture.Stack.AttackerSql($"SELECT count(*) FROM field_encrypted_values WHERE form_record_id = '{recordId:D}';").Trim();
        Assert.Equal("3", encryptedRows);
        var dump = fixture.Stack.AttackerSql($"SELECT encode(encrypted_value_bytes,'hex') || ' ' || encode(encryption_nonce_bytes,'hex') || ' ' || encode(wrapped_data_key_bytes,'hex') || ' ' || encryption_key_reference || ' ' || encrypted_logical_type FROM field_encrypted_values WHERE form_record_id = '{recordId:D}';");
        var hexOfNotes = Convert.ToHexStringLower(System.Text.Encoding.UTF8.GetBytes(notes));
        var hexOfInformant = Convert.ToHexStringLower(System.Text.Encoding.UTF8.GetBytes(informant));
        Assert.DoesNotContain(hexOfNotes, dump, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(hexOfInformant, dump, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("marina", dump, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("local-dev-kek-v1", dump, StringComparison.Ordinal); // key reference only, never key material
        var plainCount = fixture.Stack.AttackerSql($"SELECT integer_value FROM field_integer_values WHERE form_record_id = '{recordId:D}';").Trim();
        Assert.Equal("3", plainCount);
        var hostileSelect = fixture.Stack.AttackerSql($"SELECT coalesce(string_agg(convert_from(encrypted_value_bytes, 'SQL_ASCII'), '|'), '') FROM field_encrypted_values WHERE form_record_id = '{recordId:D}';", throwOnError: false);
        Assert.DoesNotContain("marina", hostileSelect, StringComparison.OrdinalIgnoreCase);

        // Authorized reader (owner) receives decrypted values; unauthorized reader and another organization do not.
        var projection = await s.WaitProjectionAsync(s.Clerk, recordId);
        var values = ValuesByKey(projection.Root);
        Assert.Equal(notes, values["case_notes"].GetProperty("stringValue").GetString());
        Assert.True(values["case_notes"].GetProperty("applicationEncrypted").GetBoolean());
        Assert.Equal(informant, values["informant_name"].GetProperty("stringValue").GetString());
        Assert.Equal("2026-03-01", values["sealed_on"].GetProperty("stringValue").GetString());
        Assert.True(values["sealed_on"].GetProperty("applicationEncrypted").GetBoolean());
        Assert.False(values["exhibit_count"].GetProperty("applicationEncrypted").GetBoolean());
        Assert.Contains((await s.ProjectionAsync(s.Reader, recordId)).Status, Denied);
        Assert.Contains((await s.ProjectionAsync(s.Designer, recordId)).Status, Denied); // designing a form never reads its records
        var other = new StageBScenario(fixture, "encryption-other");
        await other.BootstrapAsync();
        Assert.Contains((await fixture.Api.GetAsync($"/api/v1/form-records/{recordId:D}/projection", other.Token(other.Designer))).Status, Denied);

        // A field-scoped share releases exactly the shared encrypted value, decrypted, and nothing else.
        var share = await fixture.Api.PostAsync($"/api/v1/form-records/{recordId:D}/shares", s.Token(s.Clerk), new { recipientUserId = s.Reader.AuthorityUserId, recipientTeamId = (Guid?)null, recipientRole = "Reader", fieldKeys = new[] { "case_notes" } });
        Assert.True(share.IsSuccess, share.Body);
        var shared = await s.WaitProjectionAsync(s.Reader, recordId, TimeSpan.FromSeconds(40));
        var sharedValues = ValuesByKey(shared.Root);
        Assert.Equal([ "case_notes" ], sharedValues.Keys.ToArray());
        Assert.Equal(notes, sharedValues["case_notes"].GetProperty("stringValue").GetString());

        // Edits re-seal: the changed value is a fresh envelope, the unchanged ones are still ciphertext.
        var edit = await fixture.Api.PatchAsync($"/api/v1/form-records/{recordId:D}", s.Token(s.Clerk), new { expectedContentRevision = projection.Root.GetProperty("record").GetProperty("contentRevision").GetInt64(), values = new Dictionary<string, string> { ["case_notes"] = notes + " (amended)" } });
        Assert.True(edit.IsSuccess, edit.Body);
        var after = await s.WaitProjectionAsync(s.Clerk, recordId);
        Assert.Equal(notes + " (amended)", ValuesByKey(after.Root)["case_notes"].GetProperty("stringValue").GetString());
        Assert.Equal("0", fixture.Stack.AttackerSql($"SELECT count(*) FROM field_string_values WHERE form_record_id = '{recordId:D}';").Trim());

        // Audit and error surfaces never carry the plaintext.
        var activity = await fixture.Api.GetAsync($"/api/v1/form-records/{recordId:D}/activity", s.Token(s.Clerk));
        Assert.True(activity.IsSuccess, activity.Body);
        Assert.DoesNotContain("marina", activity.Body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(informant, activity.Body, StringComparison.Ordinal);
        var invalid = await fixture.Api.PatchAsync($"/api/v1/form-records/{recordId:D}", s.Token(s.Clerk), new { expectedContentRevision = after.Root.GetProperty("record").GetProperty("contentRevision").GetInt64(), values = new Dictionary<string, string> { ["sealed_on"] = "not a date " + informant } });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, invalid.Status);
        Assert.DoesNotContain(informant, invalid.Body, StringComparison.Ordinal);

        // Hostile DBA: flipping one ciphertext byte is caught by the signed manifest before any decryption; nothing is released.
        var flipped = fixture.Stack.AttackerSql($"UPDATE field_encrypted_values SET encrypted_value_bytes = set_byte(encrypted_value_bytes, 0, (get_byte(encrypted_value_bytes, 0) # 1)) WHERE form_record_id = '{recordId:D}' AND field_definition_id = '{secretId:D}' AND content_change_operation_id = (SELECT content_change_operation_id FROM form_record_revisions WHERE form_record_id = '{recordId:D}' ORDER BY content_revision DESC LIMIT 1); SELECT count(*) FROM field_encrypted_values WHERE form_record_id = '{recordId:D}';", throwOnError: false);
        var tampered = await s.ProjectionAsync(s.Clerk, recordId);
        Assert.Equal(HttpStatusCode.Locked, tampered.Status);
        Assert.DoesNotContain("marina", tampered.Body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(informant, tampered.Body, StringComparison.Ordinal);
        _ = flipped;
    }

    [Fact]
    public async Task StrengtheningAFieldIsAnAuditedMigrationThatRewritesPlaintextOnTheNextSaveAndNeverDowngrades()
    {
        var s = new StageBScenario(fixture, "encryption-migrate");
        await s.BootstrapAsync();
        var formId = await s.CreateFormAsync("Migrate " + s.Name, s.Clerk);
        var note = await AddFieldAsync(s, formId, "plain_note", "Plain note", "String", "Internal", "Standard");
        Assert.Equal(HttpStatusCode.Created, note.Status);
        Assert.Equal("Standard", note.Root.GetProperty("fieldProtectionMode").GetString());
        var noteId = note.Root.GetProperty("fieldDefinitionId").GetGuid();
        await s.SaveDraftAsync(formId, StageBScenario.Layout("Migrate", StageBScenario.Section("main", "Main", StageBScenario.Field(noteId, "text", "Plain note", 4, required: true))));
        await s.PublishAsync(formId, "v1 plaintext");

        var created = await fixture.Api.PostAsync($"/api/v1/form-definitions/{formId:D}/records", s.Token(s.Clerk), new { values = new Dictionary<string, string> { ["plain_note"] = "legacy plaintext note" }, ownerTeamId = (Guid?)null });
        Assert.Equal(HttpStatusCode.Created, created.Status);
        var recordId = created.Root.GetProperty("formRecordId").GetGuid();
        var first = await s.WaitProjectionAsync(s.Clerk, recordId);
        Assert.Equal("1", fixture.Stack.AttackerSql($"SELECT count(*) FROM field_string_values WHERE form_record_id = '{recordId:D}';").Trim());

        var status = await fixture.Api.GetAsync($"/api/v1/form-definitions/{formId:D}/fields/{noteId:D}/protection", s.Token(s.Designer));
        Assert.True(status.IsSuccess, status.Body);
        Assert.Equal("Plaintext", status.Root.GetProperty("migrationState").GetString());
        var revision = status.Root.GetProperty("protectionPolicyRevision").GetInt64();

        // Only designers may strengthen; the reason is mandatory; downgrades and no-ops are refused.
        Assert.Contains((await fixture.Api.PostAsync($"/api/v1/form-definitions/{formId:D}/fields/{noteId:D}/protection", s.Token(s.Clerk), new { targetClassification = "Confidential", targetProtectionMode = "ApplicationEncrypted", changeReason = "clerk tries", expectedProtectionRevision = revision })).Status, Denied);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await fixture.Api.PostAsync($"/api/v1/form-definitions/{formId:D}/fields/{noteId:D}/protection", s.Token(s.Designer), new { targetClassification = "Confidential", targetProtectionMode = "ApplicationEncrypted", changeReason = "x", expectedProtectionRevision = revision })).Status);
        var noop = await fixture.Api.PostAsync($"/api/v1/form-definitions/{formId:D}/fields/{noteId:D}/protection", s.Token(s.Designer), new { targetClassification = "Internal", targetProtectionMode = "Standard", changeReason = "nothing changes", expectedProtectionRevision = revision });
        Assert.Equal("NO_PROTECTION_CHANGE", noop.ErrorCode);
        var below = await fixture.Api.PostAsync($"/api/v1/form-definitions/{formId:D}/fields/{noteId:D}/protection", s.Token(s.Designer), new { targetClassification = "Public", targetProtectionMode = "Standard", changeReason = "make it public", expectedProtectionRevision = revision });
        Assert.Equal("FIELD_PROTECTION_DOWNGRADE_REFUSED", below.ErrorCode);

        var strengthened = await fixture.Api.PostAsync($"/api/v1/form-definitions/{formId:D}/fields/{noteId:D}/protection", s.Token(s.Designer), new { targetClassification = "Confidential", targetProtectionMode = "ApplicationEncrypted", changeReason = "notes contain personal data", expectedProtectionRevision = revision });
        Assert.True(strengthened.IsSuccess, strengthened.Body);
        Assert.Equal("ApplicationEncrypted", strengthened.Root.GetProperty("fieldProtectionMode").GetString());
        Assert.Equal("EncryptionPendingRewrite", strengthened.Root.GetProperty("migrationState").GetString());
        Assert.Equal(1, strengthened.Root.GetProperty("legacyPlaintextRecordCount").GetInt32());

        // The existing record is still readable (its plaintext copy is flagged, not hidden); published v1 is unchanged.
        var legacy = await s.WaitProjectionAsync(s.Clerk, recordId);
        var legacyValue = legacy.Root.GetProperty("record").GetProperty("values").EnumerateArray().Single();
        Assert.Equal("legacy plaintext note", legacyValue.GetProperty("stringValue").GetString());
        Assert.True(legacyValue.GetProperty("legacyPlaintext").GetBoolean());
        Assert.False(legacyValue.GetProperty("applicationEncrypted").GetBoolean());
        var versions = await fixture.Api.GetAsync($"/api/v1/form-definitions/{formId:D}/versions", s.Token(s.Designer));
        Assert.Single(versions.Root.EnumerateArray());
        Assert.Contains("\"protectionMode\":\"Standard\"", versions.Root[0].GetProperty("layoutJson").GetString()!.Replace(" ", string.Empty, StringComparison.Ordinal), StringComparison.Ordinal);

        // The next commit of the record rewrites the value as ciphertext: no plaintext row remains; the status reports completion.
        var edit = await fixture.Api.PatchAsync($"/api/v1/form-records/{recordId:D}", s.Token(s.Clerk), new { expectedContentRevision = legacy.Root.GetProperty("record").GetProperty("contentRevision").GetInt64(), values = new Dictionary<string, string> { ["plain_note"] = "legacy plaintext note" } });
        Assert.True(edit.IsSuccess, edit.Body);
        var rewritten = await s.WaitProjectionAsync(s.Clerk, recordId);
        var value = rewritten.Root.GetProperty("record").GetProperty("values").EnumerateArray().Single();
        Assert.Equal("legacy plaintext note", value.GetProperty("stringValue").GetString());
        Assert.True(value.GetProperty("applicationEncrypted").GetBoolean());
        Assert.False(value.GetProperty("legacyPlaintext").GetBoolean());
        var latestOperation = fixture.Stack.AttackerSql($"SELECT content_change_operation_id FROM form_record_revisions WHERE form_record_id = '{recordId:D}' ORDER BY content_revision DESC LIMIT 1;").Trim();
        Assert.Equal("0", fixture.Stack.AttackerSql($"SELECT count(*) FROM field_string_values WHERE form_record_id = '{recordId:D}' AND content_change_operation_id = '{latestOperation}';").Trim());
        Assert.Equal("1", fixture.Stack.AttackerSql($"SELECT count(*) FROM field_encrypted_values WHERE form_record_id = '{recordId:D}' AND content_change_operation_id = '{latestOperation}';").Trim());
        var done = await fixture.Api.GetAsync($"/api/v1/form-definitions/{formId:D}/fields/{noteId:D}/protection", s.Token(s.Designer));
        Assert.Equal("Encrypted", done.Root.GetProperty("migrationState").GetString());
        Assert.Equal(0, done.Root.GetProperty("legacyPlaintextRecordCount").GetInt32());

        // Once encrypted, no API path makes it plaintext again; the catalog edit route cannot touch protection either.
        var downgrade = await fixture.Api.PostAsync($"/api/v1/form-definitions/{formId:D}/fields/{noteId:D}/protection", s.Token(s.Designer), new { targetClassification = "Internal", targetProtectionMode = "Standard", changeReason = "undo", expectedProtectionRevision = revision + 1 });
        Assert.Equal("FIELD_PROTECTION_DOWNGRADE_REFUSED", downgrade.ErrorCode);
        var catalogEdit = await fixture.Api.PatchAsync($"/api/v1/form-definitions/{formId:D}/fields/{noteId:D}", s.Token(s.Designer), new { fieldDisplayName = "Plain note", fieldDescription = "renamed only", expectedFieldRevision = 1, fieldClassification = "Internal", fieldProtectionMode = "Standard" });
        Assert.True(catalogEdit.IsSuccess, catalogEdit.Body);
        Assert.Equal("ApplicationEncrypted", catalogEdit.Root.GetProperty("fieldProtectionMode").GetString());
        Assert.Equal("Confidential", catalogEdit.Root.GetProperty("fieldClassification").GetString());

        var activity = await fixture.Api.GetAsync("/api/v1/my-activity?limit=100", s.Token(s.Designer));
        Assert.Contains("FIELD_PROTECTION_STRENGTHENED", activity.Root.EnumerateArray().Select(e => e.GetProperty("eventAction").GetString()));
    }

    [Fact]
    public async Task OrganizationPolicyGovernsPlaintextAndIsOwnerOnlyAndAudited()
    {
        var s = new StageBScenario(fixture, "encryption-policy");
        await s.BootstrapAsync();
        var formId = await s.CreateFormAsync("Policy " + s.Name, s.Clerk);

        var initial = await fixture.Api.GetAsync("/api/v1/organization/security-policy", s.Token(s.Clerk));
        Assert.True(initial.IsSuccess, initial.Body);
        Assert.True(initial.Root.GetProperty("isUnreviewedDefault").GetBoolean());
        Assert.True(initial.Root.GetProperty("plaintextInternalFieldsPermitted").GetBoolean());
        var revision = initial.Root.GetProperty("securityPolicyRevision").GetInt64();

        // Only the organization owner may change the policy; a reason is required.
        var clerk = await fixture.Api.SendAsync(HttpMethod.Put, "/api/v1/organization/security-policy", s.Token(s.Clerk), new { minimumFieldClassification = "Internal", plaintextInternalFieldsPermitted = false, policyStatementText = "clerk tries to change the policy", expectedPolicyRevision = revision }, null);
        Assert.Contains(clerk.Status, Denied);
        var noReason = await fixture.Api.SendAsync(HttpMethod.Put, "/api/v1/organization/security-policy", s.Token(s.Designer), new { minimumFieldClassification = "Internal", plaintextInternalFieldsPermitted = false, policyStatementText = "short", expectedPolicyRevision = revision }, null);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, noReason.Status);

        var tightened = await fixture.Api.SendAsync(HttpMethod.Put, "/api/v1/organization/security-policy", s.Token(s.Designer), new { minimumFieldClassification = "Internal", plaintextInternalFieldsPermitted = false, policyStatementText = "All customer-entered data must be encrypted at the application layer.", expectedPolicyRevision = revision }, null);
        Assert.True(tightened.IsSuccess, tightened.Body);
        Assert.False(tightened.Root.GetProperty("isUnreviewedDefault").GetBoolean());

        // With plaintext not permitted, a plaintext field is refused even for structured types; encrypted ones are fine; Stage A's plaintext route is refused too.
        var refused = await AddFieldAsync(s, formId, "plain_count", "Count", "Integer", "Internal", "Standard");
        Assert.Equal("PLAINTEXT_NOT_PERMITTED", refused.ErrorCode);
        var defaulted = await AddFieldAsync(s, formId, "default_count", "Count", "Integer", null, null);
        Assert.Equal("PLAINTEXT_NOT_PERMITTED", defaulted.ErrorCode);
        var encrypted = await AddFieldAsync(s, formId, "sealed_count", "Count", "Integer", "Internal", "ApplicationEncrypted");
        Assert.Equal(HttpStatusCode.Created, encrypted.Status);
        var stageA = await fixture.Api.PostAsync("/api/v1/form-definitions", s.Token(s.Designer), new { formDefinitionName = "StageA " + s.Name, formDefinitionDescription = "", fields = new[] { new { fieldKey = "legacy_text", fieldDisplayName = "Legacy" } }, formUserUserIds = Array.Empty<Guid>(), formUserTeamIds = Array.Empty<Guid>() });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, stageA.Status);
        Assert.Equal("PLAINTEXT_NOT_PERMITTED", stageA.ErrorCode);

        // Loosening is possible for the owner, recorded as such, and then plaintext Internal fields are allowed again.
        var loosened = await fixture.Api.SendAsync(HttpMethod.Put, "/api/v1/organization/security-policy", s.Token(s.Designer), new { minimumFieldClassification = "Internal", plaintextInternalFieldsPermitted = true, policyStatementText = "Internal structured values may be plaintext for reporting; DBA visibility accepted by the owner.", expectedPolicyRevision = revision + 1 }, null);
        Assert.True(loosened.IsSuccess, loosened.Body);
        Assert.Equal(HttpStatusCode.Created, (await AddFieldAsync(s, formId, "plain_count", "Count", "Integer", "Internal", "Standard")).Status);
        var stale = await fixture.Api.SendAsync(HttpMethod.Put, "/api/v1/organization/security-policy", s.Token(s.Designer), new { minimumFieldClassification = "Confidential", plaintextInternalFieldsPermitted = true, policyStatementText = "stale revision attempt that must fail", expectedPolicyRevision = revision }, null);
        Assert.Equal(HttpStatusCode.Conflict, stale.Status);

        var activity = await fixture.Api.GetAsync("/api/v1/my-activity?limit=100", s.Token(s.Designer));
        Assert.Equal(2, activity.Root.EnumerateArray().Count(e => e.GetProperty("eventAction").GetString() == "ORGANIZATION_SECURITY_POLICY_UPDATED"));
    }
}
