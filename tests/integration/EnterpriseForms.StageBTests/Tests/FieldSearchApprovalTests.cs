using System.Net;
using System.Text.Json;
using EnterpriseForms.SecurityTests.Harness;
using EnterpriseForms.StageBTests.Harness;
using Xunit;

namespace EnterpriseForms.StageBTests.Tests;

/// <summary>
/// Phase C7 M2 — AC-6 search approval on the real stack (business API, Authorization Authority, SpiceDB, both
/// PostgreSQL servers). Approvals are Authority security commands restricted to administer_security, bound to the
/// Authority-registered published version and refused for anything encrypted, Confidential/Restricted or below the
/// organization floor; query permission is the field_searcher relation; strengthening, policy changes, republishing
/// and revocation take effect at once; business-database tampering with labels or options changes nothing the
/// Authority offers. Execution stays off: every authorized condition still ends in TRUSTED_SEARCH_NOT_ENABLED.
/// The synthetic development step-up is enabled on this isolated stack only (compose.security-test.yaml).
/// </summary>
[Collection(StageBStackCollectionDefinition.Name)]
public sealed class FieldSearchApprovalTests(StageBFixture fixture)
{
    private sealed record Workspace(StageBScenario S, Guid FormId, Guid Reference, Guid Count, Guid Status, Guid Open, Guid Closed, Guid Note, Guid Sealed);

    private async Task<Workspace> WorkspaceAsync(string name, bool permitSearch = true)
    {
        var s = new StageBScenario(fixture, name);
        await s.BootstrapAsync();
        await PolicyAsync(s, permitSearch);
        var formId = await s.CreateFormAsync("Search " + s.Name, s.Clerk);
        async Task<JsonElement> FieldAsync(string key, string label, string type, string classification, string protection, object? options = null)
        {
            var added = await fixture.Api.PostAsync($"/api/v1/form-definitions/{formId:D}/fields", s.Token(s.Designer), new { fieldKey = key, fieldDisplayName = label, fieldDescription = string.Empty, fieldDataType = type, options, fieldClassification = classification, fieldProtectionMode = protection });
            Assert.True(added.Status == HttpStatusCode.Created, $"add {key}: " + added.Body);
            return added.Root.Clone();
        }

        var reference = (await FieldAsync("case_reference", "Case reference", "String", "Public", "Standard")).GetProperty("fieldDefinitionId").GetGuid();
        var count = (await FieldAsync("exhibit_count", "Exhibit count", "Integer", "Internal", "Standard")).GetProperty("fieldDefinitionId").GetGuid();
        var status = await FieldAsync("case_status", "Case status", "Option", "Internal", "Standard", new[] { new { optionKey = "open", optionDisplayText = "Open" }, new { optionKey = "closed", optionDisplayText = "Closed" } });
        var statusId = status.GetProperty("fieldDefinitionId").GetGuid();
        var options = status.GetProperty("options").EnumerateArray().ToDictionary(o => o.GetProperty("optionKey").GetString()!, o => o.GetProperty("fieldOptionId").GetGuid());
        var note = (await FieldAsync("case_note", "Case note", "String", "Confidential", "ApplicationEncrypted")).GetProperty("fieldDefinitionId").GetGuid();
        var sealedNote = (await FieldAsync("sealed_note", "Sealed note", "String", "Internal", "ApplicationEncrypted")).GetProperty("fieldDefinitionId").GetGuid();
        await s.SaveDraftAsync(formId, Layout(reference, count, statusId, options["open"], options["closed"], note, sealedNote, "Case reference"));
        var published = await s.PublishAsync(formId, "v1");
        Assert.True(published.GetProperty("authorityRegistered").GetBoolean(), "the published version must be registered with the Authority: " + published.GetRawText());
        return new Workspace(s, formId, reference, count, statusId, options["open"], options["closed"], note, sealedNote);
    }

    private static string Layout(Guid reference, Guid count, Guid status, Guid open, Guid closed, Guid note, Guid sealedNote, string referenceLabel) =>
        StageBScenario.Layout("Search", StageBScenario.Section("main", "Main",
            StageBScenario.Field(reference, "text", referenceLabel, 2),
            StageBScenario.Field(count, "number", "Exhibit count", 2),
            $$"""{"kind":"field","fieldDefinitionId":"{{status:D}}","control":"select","label":"Case status","layout":{"columnSpan":2,"startOnNewRow":false},"optionsSnapshot":[{"fieldOptionId":"{{open:D}}","label":"Open","sortOrder":1},{"fieldOptionId":"{{closed:D}}","label":"Closed","sortOrder":2}]}""",
            StageBScenario.Field(note, "textarea", "Case note", 4),
            StageBScenario.Field(sealedNote, "text", "Sealed note", 4)));

    /// <summary>The organization owner publishes a policy: Public minimum, plaintext Internal permitted, plaintext search as given.</summary>
    private async Task PolicyAsync(StageBScenario s, bool permitSearch, string minimum = "Public")
    {
        var current = await fixture.Api.GetAsync("/api/v1/organization/security-policy", s.Token(s.Designer));
        Assert.True(current.IsSuccess, current.Body);
        var updated = await fixture.Api.SendAsync(HttpMethod.Put, "/api/v1/organization/security-policy", s.Token(s.Designer), new { minimumFieldClassification = minimum, plaintextInternalFieldsPermitted = true, plaintextDowngradePermitted = false, plaintextSearchPermitted = permitSearch, policyStatementText = permitSearch ? "Governance approved searching selected plaintext Public/Internal fields." : "Plaintext search is not permitted for this organization.", expectedPolicyRevision = current.Root.GetProperty("securityPolicyRevision").GetInt64() }, Guid.NewGuid().ToString("N"));
        Assert.True(updated.IsSuccess, updated.Body);
        Assert.Equal(permitSearch, updated.Root.GetProperty("plaintextSearchPermitted").GetBoolean());
    }

    private Task<HttpResult> ConfigurationAsync(Workspace w, TestUser user) => fixture.Api.GetAsync($"/api/v1/form-definitions/{w.FormId:D}/field-search", w.S.Token(user));

    private Task<HttpResult> CapabilitiesAsync(Workspace w, TestUser user, Guid? formId = null) => fixture.Api.GetAsync($"/api/v1/form-records/search-capabilities?formDefinitionId={formId ?? w.FormId:D}", w.S.Token(user));

    private Task<HttpResult> ApproveAsync(Workspace w, TestUser user, Guid fieldId, string[] operators, bool stepUp = true, string reference = "GOV-SEARCH-001") =>
        fixture.Api.PostAsync($"/api/v1/form-definitions/{w.FormId:D}/fields/{fieldId:D}/search-approval", w.S.Token(user), new { operators, approvalReference = reference, useDevelopmentStepUp = stepUp });

    private Task<HttpResult> WithdrawAsync(Workspace w, TestUser user, Guid fieldId) =>
        fixture.Api.PostAsync($"/api/v1/form-definitions/{w.FormId:D}/fields/{fieldId:D}/search-approval/withdraw", w.S.Token(user), new { reason = "security review withdrew it" });

    private Task<HttpResult> SearchersAsync(Workspace w, TestUser user, Guid[]? grant = null, Guid[]? revoke = null) =>
        fixture.Api.SendAsync(HttpMethod.Put, $"/api/v1/form-definitions/{w.FormId:D}/field-searchers", w.S.Token(user), new { grantUserIds = grant ?? [], grantTeamIds = Array.Empty<Guid>(), revokeUserIds = revoke ?? [], revokeTeamIds = Array.Empty<Guid>() }, Guid.NewGuid().ToString("N"));

    private Task<HttpResult> SearchAsync(Workspace w, TestUser user, params object[] conditions) =>
        fixture.Api.PostAsync("/api/v1/form-records/field-search", w.S.Token(user), new { formDefinitionId = w.FormId, conditions, pageSize = 25 });

    private static JsonElement StatusOf(HttpResult configuration, Guid fieldId) =>
        configuration.Root.GetProperty("fields").EnumerateArray().Single(f => f.GetProperty("fieldDefinitionId").GetGuid() == fieldId);

    private static List<Guid> SearchableFields(HttpResult capabilities) =>
        capabilities.Root.GetProperty("fields").EnumerateArray().Select(f => f.GetProperty("fieldDefinitionId").GetGuid()).ToList();

    private string AuthorityApprovalState(Workspace w, Guid fieldId) =>
        fixture.Stack.AuthorityObserverSql($"SELECT approval_state_name || '|' || coalesce(invalidation_reason_code, '') FROM field_search_approvals WHERE organization_id = '{w.S.OrganizationId:D}' AND field_definition_id = '{fieldId:D}' ORDER BY approved_on DESC LIMIT 1;").Trim();

    private string AuthorityAudit(Workspace w, string eventKind) =>
        fixture.Stack.AuthorityObserverSql($"SELECT string_agg(journal_event_body_json, ' ;; ' ORDER BY journal_stream_revision) FROM security_journal_entries WHERE organization_id = '{w.S.OrganizationId:D}' AND journal_stream_name = 'audit' AND journal_event_kind = '{eventKind}';").Trim();

    private int CommittedCommands(Workspace w, string commandKind) =>
        int.Parse(fixture.Stack.AuthorityObserverSql($"SELECT count(*) FROM security_journal_entries WHERE organization_id = '{w.S.OrganizationId:D}' AND journal_stream_name = 'authorization' AND journal_event_body_json LIKE '%\"{commandKind}\"%';").Trim(), System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>AC6-T01/T02/T05 (tests 1, 13, 17): an administrator approves; the Authority holds the binding; only granted searchers see it; execution stays off.</summary>
    [Fact]
    public async Task ASecurityAdministratorApprovesAPublicFieldAndOnlyGrantedSearchersSeeIt()
    {
        var w = await WorkspaceAsync("approve");

        var before = await ConfigurationAsync(w, w.S.Administrator);
        Assert.True(before.IsSuccess, before.Body);
        Assert.True(before.Root.GetProperty("canApprove").GetBoolean());
        Assert.True(before.Root.GetProperty("approvalOperational").GetBoolean()); // synthetic development step-up on this stack only
        Assert.Equal(1, before.Root.GetProperty("registeredFormVersionNumber").GetInt32());
        Assert.Equal("Eligible", StatusOf(before, w.Reference).GetProperty("eligibility").GetString());
        Assert.Equal("Encrypted", StatusOf(before, w.Note).GetProperty("eligibility").GetString());
        Assert.Equal("Encrypted", StatusOf(before, w.Sealed).GetProperty("eligibility").GetString());
        Assert.Equal(["equals", "startsWith", "contains"], StatusOf(before, w.Reference).GetProperty("permittedOperators").EnumerateArray().Select(o => o.GetString()));
        Assert.Empty(before.Root.GetProperty("fieldSearchers").EnumerateArray());

        var approved = await ApproveAsync(w, w.S.Administrator, w.Reference, ["equals", "startsWith"]);
        Assert.True(approved.IsSuccess, approved.Body);
        var status = StatusOf(approved, w.Reference);
        Assert.Equal("Active", status.GetProperty("approvalState").GetString());
        Assert.Equal(["equals", "startsWith"], status.GetProperty("approvedOperators").EnumerateArray().Select(o => o.GetString()));

        // The binding is Authority-held: version, digest, type, classification, protection, label.
        var binding = fixture.Stack.AuthorityObserverSql($"SELECT approved_form_version_number || '|' || approved_field_data_type || '|' || approved_field_classification || '|' || approved_protection_mode || '|' || approved_field_label_snapshot || '|' || octet_length(approved_layout_digest) FROM field_search_approvals WHERE organization_id = '{w.S.OrganizationId:D}' AND field_definition_id = '{w.Reference:D}';").Trim();
        Assert.Equal("1|String|Public|Standard|Case reference|32", binding);

        // Approval alone gives nobody query permission: not the record owner (AC6-T08), not the designer.
        var owned = await fixture.Api.PostAsync($"/api/v1/form-definitions/{w.FormId:D}/records", w.S.Token(w.S.Clerk), new { values = new Dictionary<string, string> { ["case_reference"] = "CR-1001" }, ownerTeamId = (Guid?)null });
        Assert.Equal(HttpStatusCode.Created, owned.Status);
        Assert.Empty(SearchableFields(await CapabilitiesAsync(w, w.S.Clerk)));
        Assert.Empty(SearchableFields(await CapabilitiesAsync(w, w.S.Designer)));
        var granted = await SearchersAsync(w, w.S.Administrator, grant: [w.S.Clerk.AuthorityUserId]);
        Assert.True(granted.Status == HttpStatusCode.NoContent, granted.Body);
        var clerk = await CapabilitiesAsync(w, w.S.Clerk);
        Assert.True(clerk.IsSuccess, clerk.Body);
        Assert.Equal([w.Reference], SearchableFields(clerk));
        Assert.Equal("Case reference", clerk.Root.GetProperty("fields")[0].GetProperty("displayLabel").GetString());
        Assert.Equal(["equals", "startsWith"], clerk.Root.GetProperty("fields")[0].GetProperty("operators").EnumerateArray().Select(o => o.GetString()));
        Assert.False(clerk.Root.GetProperty("fieldValueSearchAvailable").GetBoolean());
        Assert.Equal("TRUSTED_SEARCH_NOT_ENABLED", clerk.Root.GetProperty("unavailableReasonCode").GetString());
        // A searcher who is not a designer learns nothing about the form's other fields.
        Assert.Empty(clerk.Root.GetProperty("notSearchableFields").EnumerateArray());
        Assert.Equal(HttpStatusCode.NotFound, (await ConfigurationAsync(w, w.S.Clerk)).Status);
        var listed = await ConfigurationAsync(w, w.S.Administrator);
        Assert.Equal([w.S.Clerk.AuthorityUserId], listed.Root.GetProperty("fieldSearchers").EnumerateArray().Select(e => e.GetProperty("subjectId").GetGuid()));
        Assert.Equal(JsonValueKind.Null, (await ConfigurationAsync(w, w.S.Designer)).Root.GetProperty("fieldSearchers").ValueKind); // designers see status, not who may search

        // An approved condition still does not run; an unapproved field, operator or type is refused first.
        var allowed = await SearchAsync(w, w.S.Clerk, new { fieldDefinitionId = w.Reference, @operator = "startsWith", valueType = "String", value = "CR-" });
        Assert.Equal(HttpStatusCode.ServiceUnavailable, allowed.Status);
        Assert.Equal("SEARCH_VERIFICATION_UNAVAILABLE", allowed.ErrorCode);
        Assert.Equal("SEARCH_FIELD_NOT_ALLOWED", (await SearchAsync(w, w.S.Clerk, new { fieldDefinitionId = w.Count, @operator = "equals", valueType = "Integer", value = "3" })).ErrorCode);
        Assert.Equal("SEARCH_OPERATOR_NOT_SUPPORTED", (await SearchAsync(w, w.S.Clerk, new { fieldDefinitionId = w.Reference, @operator = "contains", valueType = "String", value = "CR-1" })).ErrorCode);
        Assert.Equal("SEARCH_FIELD_NOT_ALLOWED", (await SearchAsync(w, w.S.Clerk, new { fieldDefinitionId = w.Reference, @operator = "equals", valueType = "Integer", value = "3" })).ErrorCode); // forged value type
        Assert.Equal("SEARCH_FIELD_NOT_ALLOWED", (await SearchAsync(w, w.S.Reader, new { fieldDefinitionId = w.Reference, @operator = "equals", valueType = "String", value = "CR-1" })).ErrorCode); // no query permission

        // Audit evidence: committed Authority commands, and the business activity of the administrator.
        Assert.Equal(1, CommittedCommands(w, "ApproveFieldSearch"));
        Assert.Equal(1, CommittedCommands(w, "UpdateFieldSearchers"));
        Assert.True(CommittedCommands(w, "RegisterPublishedFormVersion") >= 1);
        var activity = (await fixture.Api.GetAsync("/api/v1/my-activity?limit=100", w.S.Token(w.S.Administrator))).Root.EnumerateArray().Select(e => e.GetProperty("eventAction").GetString()).ToList();
        Assert.Contains("FIELD_SEARCH_APPROVED", activity);
        Assert.Contains("FIELD_SEARCHERS_CHANGED", activity);
    }

    /// <summary>AC6-T03/T04 (tests 2, 3, 4, 14): designers, organization owners and other organizations cannot approve or grant — at the business API and directly at the Authority.</summary>
    [Fact]
    public async Task OnlyAdministerSecurityApprovesOrGrantsAndTheAuthorityEnforcesItWithoutTheBusinessPreCheck()
    {
        var w = await WorkspaceAsync("deny");
        foreach (var user in new[] { w.S.Designer, w.S.DesignerOnly, w.S.Clerk })
        {
            Assert.Equal(HttpStatusCode.Forbidden, (await ApproveAsync(w, user, w.Reference, ["equals"])).Status);
            Assert.Equal(HttpStatusCode.Forbidden, (await SearchersAsync(w, user, grant: [user.AuthorityUserId])).Status);
        }

        // A compromised business API skipping its pre-check: the Authority refuses and journals the refusal.
        var direct = await SubmitAsync(w, w.S.Designer, "ApproveFieldSearch", new { form_definition_id = w.FormId, field_definition_id = w.Reference, operators = new[] { "equals" }, approval_reference = "forged", step_up_evidence = "synthetic-development-step-up" });
        Assert.Equal(HttpStatusCode.Forbidden, direct.Status);
        var selfGrant = await SubmitAsync(w, w.S.Designer, "UpdateFieldSearchers", new { form_definition_id = w.FormId, grant_user_ids = new[] { w.S.Designer.AuthorityUserId }, grant_team_ids = Array.Empty<Guid>(), revoke_user_ids = Array.Empty<Guid>(), revoke_team_ids = Array.Empty<Guid>() });
        Assert.Equal(HttpStatusCode.Forbidden, selfGrant.Status);
        Assert.Contains("\"rejection_reason\"", AuthorityAudit(w, "FieldSearchCommandRejected"), StringComparison.Ordinal);
        Assert.Equal(0, CommittedCommands(w, "ApproveFieldSearch"));
        Assert.Equal("", AuthorityApprovalState(w, w.Reference));

        // Another organization's administrator cannot touch this form, through either path.
        var other = new StageBScenario(fixture, "deny-other");
        await other.BootstrapAsync();
        var foreign = await fixture.Api.PostAsync($"/api/v1/form-definitions/{w.FormId:D}/fields/{w.Reference:D}/search-approval", other.Token(other.Administrator), new { operators = new[] { "equals" }, approvalReference = "GOV-X", useDevelopmentStepUp = true });
        Assert.False(foreign.IsSuccess, foreign.Body);
        Assert.Equal(HttpStatusCode.NotFound, (await fixture.Api.GetAsync($"/api/v1/form-definitions/{w.FormId:D}/field-search", other.Token(other.Administrator))).Status);
        Assert.Equal("", AuthorityApprovalState(w, w.Reference));
        Assert.Empty(SearchableFields(await fixture.Api.GetAsync($"/api/v1/form-records/search-capabilities?formDefinitionId={w.FormId:D}", other.Token(other.Administrator))));
    }

    /// <summary>AC6-T06/T07 (tests 5, 6, 7, 16): encrypted, Confidential and below-floor fields and unsupported operators are never approved; without the step-up nothing is.</summary>
    [Fact]
    public async Task EncryptedConfidentialAndUnsupportedRequestsAreRefusedAndApprovalNeedsTheStepUp()
    {
        var w = await WorkspaceAsync("refuse");
        foreach (var field in new[] { w.Note, w.Sealed })
        {
            var refused = await ApproveAsync(w, w.S.Administrator, field, ["equals"]);
            Assert.Equal("FIELD_SEARCH_NOT_ELIGIBLE", refused.ErrorCode);
        }

        Assert.Equal("FIELD_SEARCH_NOT_ELIGIBLE", (await ApproveAsync(w, w.S.Administrator, w.Count, ["contains"])).ErrorCode); // not an Integer operator
        Assert.False((await ApproveAsync(w, w.S.Administrator, w.Reference, [])).IsSuccess);
        Assert.Equal("STEP_UP_REQUIRED", (await ApproveAsync(w, w.S.Administrator, w.Reference, ["equals"], stepUp: false)).ErrorCode);
        // A forged step-up marker sent straight to the Authority is not the development marker either.
        Assert.Equal("STEP_UP_REQUIRED", (await SubmitAsync(w, w.S.Administrator, "ApproveFieldSearch", new { form_definition_id = w.FormId, field_definition_id = w.Reference, operators = new[] { "equals" }, approval_reference = "GOV-1", step_up_evidence = "passkey-ok" })).ErrorCode);
        foreach (var field in new[] { w.Note, w.Sealed, w.Count, w.Reference })
        {
            Assert.Equal("", AuthorityApprovalState(w, field));
        }

        // Raising the floor to Internal makes the Public field ineligible.
        await PolicyAsync(w.S, permitSearch: true, minimum: "Internal");
        Assert.Equal("ClassificationNotPermitted", StatusOf(await ConfigurationAsync(w, w.S.Administrator), w.Reference).GetProperty("eligibility").GetString());
        Assert.Equal("FIELD_SEARCH_NOT_ELIGIBLE", (await ApproveAsync(w, w.S.Administrator, w.Reference, ["equals"])).ErrorCode);
        Assert.Contains("FIELD_SEARCH_NOT_ELIGIBLE", AuthorityAudit(w, "FieldSearchCommandRejected"), StringComparison.Ordinal);
    }

    /// <summary>AC6-T08/T09/T10 (tests 8, 9, 10): approvals bind to the Authority-registered version; business-database tampering with layouts, labels, options or field identities changes nothing offered.</summary>
    [Fact]
    public async Task VersionFieldAndMetadataSubstitutionInTheBusinessDatabaseChangesNothingTheAuthorityOffers()
    {
        var w = await WorkspaceAsync("tamper");
        Assert.True((await ApproveAsync(w, w.S.Administrator, w.Status, ["is"])).IsSuccess);
        Assert.True((await ApproveAsync(w, w.S.Administrator, w.Reference, ["equals"])).IsSuccess);
        Assert.Equal(HttpStatusCode.NoContent, (await SearchersAsync(w, w.S.Administrator, grant: [w.S.Clerk.AuthorityUserId])).Status);

        // Hostile business DBA: relabel the field and substitute an injected option identity in the published layout.
        var injected = Guid.CreateVersion7();
        fixture.Stack.AttackerSql($"UPDATE form_versions SET layout_json = replace(replace(layout_json::text, 'Case reference', 'Injected label'), '{w.Closed:D}', '{injected:D}')::jsonb WHERE form_definition_id = '{w.FormId:D}';");
        Assert.Contains(injected.ToString("D"), fixture.Stack.AttackerSql($"SELECT layout_json::text FROM form_versions WHERE form_definition_id = '{w.FormId:D}';"), StringComparison.Ordinal);
        fixture.Stack.AttackerSql($"UPDATE field_definitions SET field_display_name = 'Injected label' WHERE field_definition_id = '{w.Reference:D}';");
        // AC6-T03: the DBA also declares the encrypted Confidential field Public, plaintext and searchable.
        fixture.Stack.AttackerSql($"UPDATE field_definitions SET field_classification = 'Public', is_searchable = true, field_protection_mode = 'Standard' WHERE field_definition_id = '{w.Note:D}';");

        var clerk = await CapabilitiesAsync(w, w.S.Clerk);
        var fields = clerk.Root.GetProperty("fields").EnumerateArray().ToDictionary(f => f.GetProperty("fieldDefinitionId").GetGuid());
        Assert.Equal("Case reference", fields[w.Reference].GetProperty("displayLabel").GetString());
        Assert.Equal([w.Open, w.Closed], fields[w.Status].GetProperty("options").EnumerateArray().Select(o => o.GetProperty("fieldOptionId").GetGuid()));
        Assert.Equal("SEARCH_OPERATOR_NOT_SUPPORTED", (await SearchAsync(w, w.S.Clerk, new { fieldDefinitionId = w.Status, @operator = "is", valueType = "Option", value = injected.ToString("D") })).ErrorCode);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await SearchAsync(w, w.S.Clerk, new { fieldDefinitionId = w.Status, @operator = "is", valueType = "Option", value = w.Open.ToString("D") })).Status);
        Assert.DoesNotContain(w.Note, fields.Keys);
        Assert.Equal("SEARCH_FIELD_NOT_ALLOWED", (await SearchAsync(w, w.S.Clerk, new { fieldDefinitionId = w.Note, @operator = "equals", valueType = "String", value = "x" })).ErrorCode);
        var note = StatusOf(await ConfigurationAsync(w, w.S.Administrator), w.Note);
        Assert.Equal("Confidential", note.GetProperty("fieldClassification").GetString()); // the Authority's registered facts, not the tampered row
        Assert.Equal("Encrypted", note.GetProperty("eligibility").GetString());
        Assert.Equal("FIELD_SEARCH_NOT_ELIGIBLE", (await ApproveAsync(w, w.S.Administrator, w.Note, ["equals"])).ErrorCode);

        // A field that is not in the registered version, and a field of another form, cannot be approved.
        var unregistered = await w.S.AddFieldAsync(w.FormId, "late_field", "Late field", "String");
        Assert.Equal("FORM_VERSION_UNTRUSTED", (await ApproveAsync(w, w.S.Administrator, unregistered, ["equals"])).ErrorCode);
        var otherForm = await w.S.CreateFormAsync("Other " + w.S.Name, w.S.Clerk);
        var substituted = await fixture.Api.PostAsync($"/api/v1/form-definitions/{otherForm:D}/fields/{w.Reference:D}/search-approval", w.S.Token(w.S.Administrator), new { operators = new[] { "equals" }, approvalReference = "GOV-2", useDevelopmentStepUp = true });
        Assert.Equal("FORM_VERSION_UNTRUSTED", substituted.ErrorCode);

        // Forging a registration through the Authority with a mismatched protection is refused (G21: only the publish path registers).
        var forged = await SubmitAsync(w, w.S.Designer, "RegisterPublishedFormVersion", new { form_definition_id = w.FormId, form_version_number = 2, layout_digest_hex = new string('a', 64), fields = new[] { new { field_definition_id = w.Note, field_data_type = "String", field_classification = "Public", protection_mode = "Standard", field_label = "Case note", options = (object?)null } } });
        Assert.Equal("FORM_VERSION_UNTRUSTED", forged.ErrorCode);
        Assert.Equal("1", fixture.Stack.AuthorityObserverSql($"SELECT max(form_version_number) FROM published_form_versions WHERE organization_id = '{w.S.OrganizationId:D}' AND form_definition_id = '{w.FormId:D}';").Trim());
    }

    /// <summary>AC6-T11/T12 (tests 11, 12): strengthening, a changed republish, withdrawal, revocation and the policy switch take effect immediately.</summary>
    [Fact]
    public async Task StrengtheningRepublishingWithdrawalRevocationAndPolicyChangesInvalidateAtOnce()
    {
        var w = await WorkspaceAsync("invalidate");
        foreach (var (field, op) in new[] { (w.Reference, "equals"), (w.Count, "greaterThan"), (w.Status, "is") })
        {
            Assert.True((await ApproveAsync(w, w.S.Administrator, field, [op])).IsSuccess);
        }

        Assert.Equal(HttpStatusCode.NoContent, (await SearchersAsync(w, w.S.Administrator, grant: [w.S.Clerk.AuthorityUserId, w.S.Reader.AuthorityUserId])).Status);
        Assert.Equal(3, SearchableFields(await CapabilitiesAsync(w, w.S.Clerk)).Count);

        // Strengthening the Integer field invalidates its approval in the same command.
        var protection = await fixture.Api.GetAsync($"/api/v1/form-definitions/{w.FormId:D}/fields/{w.Count:D}/protection", w.S.Token(w.S.Designer));
        var strengthened = await fixture.Api.PostAsync($"/api/v1/form-definitions/{w.FormId:D}/fields/{w.Count:D}/protection", w.S.Token(w.S.Designer), new { targetClassification = "Internal", targetProtectionMode = "ApplicationEncrypted", changeReason = "counts are now sensitive", expectedProtectionRevision = protection.Root.GetProperty("protectionPolicyRevision").GetInt64() });
        Assert.True(strengthened.IsSuccess, strengthened.Body);
        Assert.Equal("Invalidated|FIELD_STRENGTHENED", AuthorityApprovalState(w, w.Count));
        Assert.DoesNotContain(w.Count, SearchableFields(await CapabilitiesAsync(w, w.S.Clerk)));

        // Republishing with a changed label invalidates that approval; an unchanged field keeps its approval.
        await w.S.SaveDraftAsync(w.FormId, Layout(w.Reference, w.Count, w.Status, w.Open, w.Closed, w.Note, w.Sealed, "Court reference"));
        Assert.True((await w.S.PublishAsync(w.FormId, "v2 relabel")).GetProperty("authorityRegistered").GetBoolean());
        Assert.Equal("Invalidated|FORM_VERSION_CHANGED", AuthorityApprovalState(w, w.Reference));
        Assert.Equal("Active|", AuthorityApprovalState(w, w.Status));
        Assert.Equal([w.Status], SearchableFields(await CapabilitiesAsync(w, w.S.Clerk)));

        // Revoking query permission removes everything for that person only.
        Assert.Equal(HttpStatusCode.NoContent, (await SearchersAsync(w, w.S.Administrator, revoke: [w.S.Reader.AuthorityUserId])).Status);
        Assert.Empty(SearchableFields(await CapabilitiesAsync(w, w.S.Reader)));
        Assert.Equal("SEARCH_FIELD_NOT_ALLOWED", (await SearchAsync(w, w.S.Reader, new { fieldDefinitionId = w.Status, @operator = "is", valueType = "Option", value = w.Open.ToString("D") })).ErrorCode);
        Assert.Equal([w.Status], SearchableFields(await CapabilitiesAsync(w, w.S.Clerk)));

        // Withdrawal is administrator-only and immediate.
        Assert.Equal(HttpStatusCode.Forbidden, (await WithdrawAsync(w, w.S.Designer, w.Status)).Status);
        Assert.True((await WithdrawAsync(w, w.S.Administrator, w.Status)).IsSuccess);
        Assert.Equal("Withdrawn|", AuthorityApprovalState(w, w.Status));
        Assert.Empty(SearchableFields(await CapabilitiesAsync(w, w.S.Clerk)));

        // Re-approve, then turn plaintext search off: every approval is invalidated by the policy command.
        Assert.True((await ApproveAsync(w, w.S.Administrator, w.Status, ["is", "includes"])).IsSuccess);
        Assert.Equal([w.Status], SearchableFields(await CapabilitiesAsync(w, w.S.Clerk)));
        await PolicyAsync(w.S, permitSearch: false);
        Assert.Equal("Invalidated|ORGANIZATION_POLICY_CHANGED", AuthorityApprovalState(w, w.Status));
        Assert.Empty(SearchableFields(await CapabilitiesAsync(w, w.S.Clerk)));
        Assert.Equal("OrganizationSearchDisabled", StatusOf(await ConfigurationAsync(w, w.S.Administrator), w.Status).GetProperty("eligibility").GetString());
    }

    private async Task<HttpResult> SubmitAsync(Workspace w, TestUser user, string commandKind, object payload)
    {
        var gate = await fixture.Authority.GateAsync(w.S.OrganizationId);
        return await fixture.Authority.SendAsync(HttpMethod.Post, "/internal/v1/security-commands", TestIdentity.WorkloadToken(), new
        {
            organization_id = w.S.OrganizationId,
            command_kind = commandKind,
            resource = new { resource_kind = "FormDefinition", resource_id = w.FormId },
            expected_authorization_revision = gate.GetProperty("authorization_revision").GetInt64(),
            command_payload = payload,
            change_reason = "harness: direct command",
        }, w.S.Token(user), Guid.NewGuid().ToString("N"));
    }
}
