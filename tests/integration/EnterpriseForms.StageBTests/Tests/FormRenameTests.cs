using System.Net;
using System.Text.Json;
using EnterpriseForms.SecurityTests.Harness;
using EnterpriseForms.StageBTests.Harness;
using Xunit;

namespace EnterpriseForms.StageBTests.Tests;

/// <summary>
/// Form rename on the real stack: only a designer of the form may rename it (the Authority decides); a stale revision is
/// refused; renaming a published form keeps its id, current version, every published snapshot, its records and their
/// values, and its form permissions; renaming a draft-only form creates no version and leaves the draft untouched; the
/// rename is audited with the previous and new name; another organization cannot touch it.
/// </summary>
[Collection(StageBStackCollectionDefinition.Name)]
public sealed class FormRenameTests(StageBFixture fixture)
{
    private static readonly HttpStatusCode[] Denied = [HttpStatusCode.Forbidden, HttpStatusCode.NotFound];

    private Task<HttpResult> RenameAsync(StageBScenario s, Guid formId, TestUser actor, string name, long expectedRevision) =>
        fixture.Api.PatchAsync($"/api/v1/form-definitions/{formId:D}", s.Token(actor), new { formDefinitionName = name, expectedFormDefinitionRevision = expectedRevision });

    private async Task<JsonElement> GetAsync(StageBScenario s, string path, TestUser actor)
    {
        var result = await fixture.Api.GetAsync(path, s.Token(actor));
        Assert.True(result.IsSuccess, path + ": " + result.Body);
        return result.Root.Clone();
    }

    private static List<(Guid Id, int Number, string Layout)> Snapshots(JsonElement versions) =>
        versions.EnumerateArray().Select(v => (v.GetProperty("formVersionId").GetGuid(), v.GetProperty("versionNumber").GetInt32(), JsonSerializer.Serialize(JsonDocument.Parse(v.GetProperty("layoutJson").GetString()!).RootElement))).OrderBy(v => v.Item2).ToList();

    [Fact]
    public async Task RenamingPublishedAndDraftFormsChangesOnlyTheNameAndIsAudited()
    {
        var s = new StageBScenario(fixture, "form-rename");
        await s.BootstrapAsync();
        await s.BuildAccusedWorkspaceAsync(); // published v1, records allowed for the clerk
        var record = await s.CreateAccusedAsync(s.Clerk);
        var formId = s.AccusedFormId;

        var before = (await s.GetFormAsync(formId)).GetProperty("summary").Clone();
        var originalName = before.GetProperty("formDefinitionName").GetString()!;
        var revision = before.GetProperty("formDefinitionRevision").GetInt64();
        var currentVersion = before.GetProperty("currentFormVersionId").GetGuid();
        var versionsBefore = Snapshots(await GetAsync(s, $"/api/v1/form-definitions/{formId:D}/versions", s.Designer));
        var accessBefore = JsonSerializer.Serialize(await GetAsync(s, $"/api/v1/form-definitions/{formId:D}/access", s.Designer));
        var valuesBefore = JsonSerializer.Serialize((await s.WaitProjectionAsync(s.Clerk, record)).Root.GetProperty("record").GetProperty("values"));

        // Authorization: a clerk who may create records but not design, and another organization's designer, are refused.
        Assert.Contains((await RenameAsync(s, formId, s.Clerk, "Hijacked", revision)).Status, Denied);
        var other = new StageBScenario(fixture, "form-rename-other");
        await other.BootstrapAsync();
        Assert.Contains((await RenameAsync(other, formId, other.Designer, "Hijacked", revision)).Status, Denied);

        // Validation and concurrency: nothing changes.
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await RenameAsync(s, formId, s.Designer, "   ", revision)).Status);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await RenameAsync(s, formId, s.Designer, new string('x', 201), revision)).Status);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await RenameAsync(s, formId, s.Designer, "Two\nlines", revision)).Status);
        var stale = await RenameAsync(s, formId, s.Designer, "Stale name", revision + 5);
        Assert.Equal(HttpStatusCode.Conflict, stale.Status);
        Assert.Equal("FORM_DEFINITION_MODIFIED_CONCURRENTLY", stale.ErrorCode);
        Assert.Equal(originalName, (await s.GetFormAsync(formId)).GetProperty("summary").GetProperty("formDefinitionName").GetString());

        // The designer renames the published form.
        var renamed = await RenameAsync(s, formId, s.Designer, "  Accused person  ", revision);
        Assert.True(renamed.IsSuccess, renamed.Body);
        var summary = renamed.Root.GetProperty("summary");
        Assert.Equal("Accused person", summary.GetProperty("formDefinitionName").GetString());
        Assert.Equal(revision + 1, summary.GetProperty("formDefinitionRevision").GetInt64());
        Assert.Equal(formId, summary.GetProperty("formDefinitionId").GetGuid());
        Assert.Equal(currentVersion, summary.GetProperty("currentFormVersionId").GetGuid());

        // A stale second editor cannot overwrite it; re-sending the same rename is harmless.
        Assert.Equal(HttpStatusCode.Conflict, (await RenameAsync(s, formId, s.Designer, "Someone else's name", revision)).Status);
        var resent = await RenameAsync(s, formId, s.Designer, "Accused person", revision);
        Assert.True(resent.IsSuccess, resent.Body);
        Assert.Equal(revision + 1, resent.Root.GetProperty("summary").GetProperty("formDefinitionRevision").GetInt64());

        // Published snapshots, permissions, records and values are unchanged; the new name is shown everywhere.
        Assert.Equal(versionsBefore, Snapshots(await GetAsync(s, $"/api/v1/form-definitions/{formId:D}/versions", s.Designer)));
        Assert.Equal(accessBefore, JsonSerializer.Serialize(await GetAsync(s, $"/api/v1/form-definitions/{formId:D}/access", s.Designer)));
        var projection = await s.WaitProjectionAsync(s.Clerk, record);
        Assert.Equal("Accused person", projection.Root.GetProperty("formDefinitionName").GetString());
        Assert.Equal(currentVersion, projection.Root.GetProperty("layoutVersion").GetProperty("formVersionId").GetGuid());
        Assert.Equal(valuesBefore, JsonSerializer.Serialize(projection.Root.GetProperty("record").GetProperty("values")));
        var list = await GetAsync(s, "/api/v1/form-records?pageSize=15", s.Clerk);
        Assert.Equal("Accused person", list.GetProperty("items").EnumerateArray().Single(i => i.GetProperty("formRecordId").GetGuid() == record).GetProperty("formDefinitionName").GetString());
        var catalog = await GetAsync(s, "/api/v1/form-definitions", s.Designer);
        Assert.Equal("Accused person", catalog.EnumerateArray().Single(f => f.GetProperty("formDefinitionId").GetGuid() == formId).GetProperty("formDefinitionName").GetString());

        // A draft-only form: renamed without publishing anything and without touching its draft.
        var draftForm = await s.CreateFormAsync("Witness statement");
        var draftBefore = await s.GetFormAsync(draftForm);
        var draftRevision = draftBefore.GetProperty("draft").GetProperty("draftRevision").GetInt64();
        var draftLayout = draftBefore.GetProperty("draft").GetProperty("layoutJson").GetString();
        var draftRenamed = await RenameAsync(s, draftForm, s.Designer, "Witness statement (2026)", draftBefore.GetProperty("summary").GetProperty("formDefinitionRevision").GetInt64());
        Assert.True(draftRenamed.IsSuccess, draftRenamed.Body);
        Assert.Equal(JsonValueKind.Null, draftRenamed.Root.GetProperty("summary").GetProperty("currentVersionNumber").ValueKind);
        Assert.Equal(draftRevision, draftRenamed.Root.GetProperty("draft").GetProperty("draftRevision").GetInt64());
        Assert.Equal(draftLayout, draftRenamed.Root.GetProperty("draft").GetProperty("layoutJson").GetString());
        Assert.Empty((await GetAsync(s, $"/api/v1/form-definitions/{draftForm:D}/versions", s.Designer)).EnumerateArray());

        // Audit: one FORM_RENAMED per effective rename, by the designer, with both names; refused attempts change nothing.
        var audit = fixture.Stack.AttackerSql($"SELECT form_definition_id || '|' || actor_user_id || '|' || event_metadata_json::text FROM audit_events WHERE tenant_id = '{s.OrganizationId:D}' AND event_action = 'FORM_RENAMED' ORDER BY audit_sequence;")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        Assert.Equal(2, audit.Length);
        Assert.StartsWith($"{formId:D}|{s.Designer.AuthorityUserId:D}|", audit[0], StringComparison.Ordinal);
        Assert.Contains("\"previousFormDefinitionName\": \"" + originalName + "\"", audit[0], StringComparison.Ordinal);
        Assert.Contains("\"formDefinitionName\": \"Accused person\"", audit[0], StringComparison.Ordinal);
        Assert.StartsWith($"{draftForm:D}|", audit[1], StringComparison.Ordinal);
        Assert.Equal("0", fixture.Stack.AttackerSql($"SELECT count(*) FROM audit_events WHERE tenant_id = '{other.OrganizationId:D}' AND event_action = 'FORM_RENAMED';").Trim());
    }
}
