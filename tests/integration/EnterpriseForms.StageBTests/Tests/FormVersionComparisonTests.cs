using System.Net;
using System.Text.Json;
using EnterpriseForms.SecurityTests.Harness;
using EnterpriseForms.StageBTests.Harness;
using Xunit;

namespace EnterpriseForms.StageBTests.Tests;

/// <summary>
/// Phase C9 M2 on the real stack: the publish-impact preview (semantic comparison of the current version with the draft
/// as it would be published, potential-impact notices, approximate counts over records the designer can discover only),
/// the change summary audited with the publish, and version-to-version and version-to-draft comparison — design
/// permission only, own versions only, stored values unchanged.
/// </summary>
[Collection(StageBStackCollectionDefinition.Name)]
public sealed class FormVersionComparisonTests(StageBFixture fixture)
{
    private static readonly HttpStatusCode[] Denied = [HttpStatusCode.Forbidden, HttpStatusCode.NotFound];

    private Task<HttpResult> ImpactAsync(StageBScenario s, TestUser user, Guid formId, long draftRevision) =>
        fixture.Api.GetAsync($"/api/v1/form-definitions/{formId:D}/publish-impact?expectedDraftRevision={draftRevision}", s.Token(user));

    private Task<HttpResult> CompareAsync(StageBScenario s, TestUser user, Guid formId, string from, string to) =>
        fixture.Api.GetAsync($"/api/v1/form-definitions/{formId:D}/versions/compare?from={Uri.EscapeDataString(from)}&to={Uri.EscapeDataString(to)}", s.Token(user));

    private static List<string> Labels(JsonElement impact, string code) =>
        impact.GetProperty("notices").EnumerateArray().Where(n => n.GetProperty("code").GetString() == code).SelectMany(n => n.GetProperty("elementLabels").EnumerateArray().Select(l => l.GetString()!)).ToList();

    private async Task<Guid> CreateRecordAsync(StageBScenario s, TestUser user, Guid formId, Dictionary<string, string> values)
    {
        var created = await fixture.Api.PostAsync($"/api/v1/form-definitions/{formId:D}/records", s.Token(user), new { values, ownerTeamId = (Guid?)null });
        Assert.True(created.Status == HttpStatusCode.Created, "create record failed: " + created.Body);
        var id = created.Root.GetProperty("formRecordId").GetGuid();
        await s.WaitProjectionAsync(user, id);
        return id;
    }

    [Fact]
    public async Task ThePublishImpactAndVersionComparisonDescribeSemanticChangesAndCountOnlyDiscoverableRecords()
    {
        var s = new StageBScenario(fixture, "c9-compare");
        await s.BootstrapAsync();
        var formId = await s.CreateFormAsync("Compare intake", s.Clerk, s.Designer);
        var fullName = await s.AddFieldAsync(formId, "full_name", "Full name", "String");
        var phone = await s.AddFieldAsync(formId, "phone_number", "Phone number", "String");
        var alias = await s.AddFieldAsync(formId, "alias_name", "Alias", "String");
        await s.SaveDraftAsync(formId, StageBScenario.Layout("Intake", StageBScenario.Section("main", "Main",
            StageBScenario.Field(fullName, "text", "Full name", 4, required: true, validation: new { maxLength = 100 }),
            StageBScenario.Field(phone, "text", "Phone number", 4))));
        var v1 = (await s.PublishAsync(formId, "intake v1")).GetProperty("formVersionId").GetGuid();
        var clerkRecord = await CreateRecordAsync(s, s.Clerk, formId, new() { ["full_name"] = "Avery", ["phone_number"] = "555-0101" });
        var designerRecord = await CreateRecordAsync(s, s.Designer, formId, new() { ["full_name"] = "Drew", ["phone_number"] = "555-0102" });

        // Draft v2: Full name relabelled and stricter, Alias added as required, Phone number removed from the layout.
        var draftRevision = await s.SaveDraftAsync(formId, StageBScenario.Layout("Intake", StageBScenario.Section("main", "Main",
            StageBScenario.Field(fullName, "text", "First name", 4, required: true, validation: new { maxLength = 50 }),
            StageBScenario.Field(alias, "text", "Alias", 4, required: true))));

        // Only a designer of the form; never another organization; never a stale draft.
        Assert.Contains((await ImpactAsync(s, s.Clerk, formId, draftRevision)).Status, Denied);
        Assert.Contains((await CompareAsync(s, s.Clerk, formId, v1.ToString("D"), "draft")).Status, Denied);
        var other = new StageBScenario(fixture, "c9-compare-other");
        await other.BootstrapAsync();
        Assert.Contains((await ImpactAsync(other, other.Designer, formId, draftRevision)).Status, Denied);
        Assert.Contains((await CompareAsync(other, other.Designer, formId, v1.ToString("D"), "draft")).Status, Denied);
        var stale = await ImpactAsync(s, s.Designer, formId, draftRevision - 1);
        Assert.Equal(HttpStatusCode.Conflict, stale.Status);
        Assert.Equal("DRAFT_REVISION_CONFLICT", stale.ErrorCode);

        var impact = await ImpactAsync(s, s.Designer, formId, draftRevision);
        Assert.True(impact.IsSuccess, impact.Body);
        var root = impact.Root.Clone();
        Assert.True(root.GetProperty("canPublish").GetBoolean());
        Assert.Equal((1, 2), (root.GetProperty("currentVersionNumber").GetInt32(), root.GetProperty("nextVersionNumber").GetInt32()));
        var comparison = root.GetProperty("comparison");
        Assert.Equal((1, 1, 1), (comparison.GetProperty("addedCount").GetInt32(), comparison.GetProperty("removedCount").GetInt32(), comparison.GetProperty("behaviourChangeCount").GetInt32()));
        var renamed = comparison.GetProperty("changes").EnumerateArray().Single(c => c.GetProperty("elementId").GetString() == fullName.ToString("D"));
        Assert.Equal(("Modified", "Behaviour", "First name"), (renamed.GetProperty("changeType").GetString(), renamed.GetProperty("significance").GetString(), renamed.GetProperty("label").GetString()));
        Assert.Contains(renamed.GetProperty("details").EnumerateArray(), d => d.GetProperty("aspect").GetString() == "Label" && d.GetProperty("before").GetString() == "Full name");
        Assert.Contains(renamed.GetProperty("details").EnumerateArray(), d => d.GetProperty("aspect").GetString() == "MaxLength" && d.GetProperty("tightened").GetBoolean());
        Assert.Equal(["Alias"], Labels(root, "NEW_REQUIRED_FIELDS"));
        Assert.Equal(["First name"], Labels(root, "VALIDATION_TIGHTENED"));
        Assert.Equal(["Phone number"], Labels(root, "ELEMENTS_REMOVED"));
        var records = root.GetProperty("records");
        Assert.Equal((1, 1, 1), (records.GetProperty("visibleRecordCount").GetInt32(), records.GetProperty("shownWithNewVersionCount").GetInt32(), records.GetProperty("editedWithNewVersionCount").GetInt32()));
        Assert.True(records.GetProperty("countsAreApproximate").GetBoolean());
        Assert.Contains("never deletes or changes stored values", root.GetProperty("dataStatement").GetString(), StringComparison.Ordinal);
        Assert.DoesNotContain(clerkRecord.ToString("D"), impact.Body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(designerRecord.ToString("D"), impact.Body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("555-01", impact.Body, StringComparison.Ordinal); // no stored value travels in an impact

        // Publishing: the change summary is audited (counts only); stored values are unchanged.
        var v2 = (await s.PublishAsync(formId, "intake v2")).GetProperty("formVersionId").GetGuid();
        var audit = fixture.Stack.AttackerSql($"SELECT event_metadata_json::text FROM audit_events WHERE tenant_id = '{s.OrganizationId:D}' AND form_definition_id = '{formId:D}' AND event_action = 'FORM_VERSION_PUBLISHED' ORDER BY audit_sequence DESC LIMIT 1;").Replace(" ", string.Empty, StringComparison.Ordinal);
        Assert.Contains("\"addedElements\":1", audit, StringComparison.Ordinal);
        Assert.Contains("\"removedElements\":1", audit, StringComparison.Ordinal);
        Assert.Contains("\"behaviourChanges\":1", audit, StringComparison.Ordinal);
        Assert.DoesNotContain("First name", audit, StringComparison.Ordinal);
        var view = (await s.WaitProjectionAsync(s.Clerk, clerkRecord)).Root;
        Assert.Contains(view.GetProperty("record").GetProperty("values").EnumerateArray(), v => v.GetProperty("fieldKey").GetString() == "phone_number" && v.GetProperty("stringValue").GetString() == "555-0101");

        // Version to version: the same semantic changes the preview showed.
        var compared = await CompareAsync(s, s.Designer, formId, v1.ToString("D"), v2.ToString("D"));
        Assert.True(compared.IsSuccess, compared.Body);
        Assert.Equal(JsonSerializer.Serialize(comparison), JsonSerializer.Serialize(compared.Root.GetProperty("comparison")));
        Assert.Equal(("Version", 1, "Version", 2), (compared.Root.GetProperty("from").GetProperty("kind").GetString(), compared.Root.GetProperty("from").GetProperty("versionNumber").GetInt32(), compared.Root.GetProperty("to").GetProperty("kind").GetString(), compared.Root.GetProperty("to").GetProperty("versionNumber").GetInt32()));
        var reversed = await CompareAsync(s, s.Designer, formId, v2.ToString("D"), v1.ToString("D"));
        Assert.Equal((1, 1), (reversed.Root.GetProperty("comparison").GetProperty("addedCount").GetInt32(), reversed.Root.GetProperty("comparison").GetProperty("removedCount").GetInt32()));

        // The draft after publishing equals v2: nothing to compare, nothing to publish.
        var afterPublish = await CompareAsync(s, s.Designer, formId, v2.ToString("D"), "draft");
        Assert.True(afterPublish.IsSuccess, afterPublish.Body);
        Assert.False(afterPublish.Root.GetProperty("comparison").GetProperty("hasChanges").GetBoolean());
        var nothing = await ImpactAsync(s, s.Designer, formId, await s.CurrentDraftRevisionAsync(formId));
        Assert.False(nothing.Root.GetProperty("canPublish").GetBoolean());
        Assert.Equal("NO_DRAFT_CHANGES", nothing.Root.GetProperty("blockedReasonCode").GetString());

        // Only this form's versions; references are checked.
        var otherForm = await s.CreateFormAsync("Other form");
        var otherField = await s.AddFieldAsync(otherForm, "note_text", "Note", "String");
        await s.SaveDraftAsync(otherForm, StageBScenario.Layout("Other", StageBScenario.Section("main", "Main", StageBScenario.Field(otherField, "text", "Note", 4))));
        var otherVersion = (await s.PublishAsync(otherForm, "other v1")).GetProperty("formVersionId").GetGuid();
        var foreign = await CompareAsync(s, s.Designer, formId, otherVersion.ToString("D"), v2.ToString("D"));
        Assert.Equal(HttpStatusCode.NotFound, foreign.Status);
        Assert.Equal("VERSION_NOT_FOUND", foreign.ErrorCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await CompareAsync(s, s.Designer, formId, "v1", "draft")).Status);
    }
}
