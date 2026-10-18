using System.Net;
using System.Text.Json;
using EnterpriseForms.SecurityTests.Harness;
using EnterpriseForms.StageBTests.Harness;
using Xunit;

namespace EnterpriseForms.StageBTests.Tests;

/// <summary>
/// Owner acceptance defect: an existing field (including a File field) can be renamed and its properties edited. The
/// field's permanent identity (id, key, storage type) never changes, published versions stay immutable, and stored
/// values and attachments are preserved because they reference the field id.
/// </summary>
[Collection(StageBStackCollectionDefinition.Name)]
public sealed class FieldEditTests(StageBFixture fixture)
{
    private static readonly HttpStatusCode[] Denied = [HttpStatusCode.Forbidden, HttpStatusCode.NotFound];

    private static JsonElement FieldOf(JsonElement detail, string key) =>
        detail.GetProperty("fields").EnumerateArray().Single(f => f.GetProperty("fieldKey").GetString() == key);

    private Task<HttpResult> PatchFieldAsync(StageBScenario s, Guid formId, Guid fieldId, TestUser actor, object body) =>
        fixture.Api.PatchAsync($"/api/v1/form-definitions/{formId:D}/fields/{fieldId:D}", s.Token(actor), body);

    private static string ReplaceFirst(string text, string oldValue, string newValue)
    {
        var index = text.IndexOf(oldValue, StringComparison.Ordinal);
        Assert.True(index >= 0, "layout does not contain " + oldValue);
        return string.Concat(text.AsSpan(0, index), newValue, text.AsSpan(index + oldValue.Length));
    }

    /// <summary>PostgreSQL jsonb re-serializes stored layouts, so labels are asserted on the parsed structure, never on raw text.</summary>
    private static string? LabelOf(string layoutJson, Guid fieldId)
    {
        using var layout = JsonDocument.Parse(layoutJson);
        return layout.RootElement.GetProperty("sections").EnumerateArray()
            .SelectMany(section => section.GetProperty("elements").EnumerateArray())
            .Single(e => e.TryGetProperty("fieldDefinitionId", out var id) && id.GetGuid() == fieldId)
            .GetProperty("label").GetString();
    }

    private async Task<JsonElement> VersionsAsync(StageBScenario s, Guid formId)
    {
        var versions = await fixture.Api.GetAsync($"/api/v1/form-definitions/{formId:D}/versions", s.Token(s.Designer));
        Assert.True(versions.IsSuccess, versions.Body);
        return versions.Root.Clone();
    }

    [Fact]
    public async Task RenamingFieldsAndTheDocumentCollectionPreservesIdentityDataAttachmentsAndPublishedVersions()
    {
        var s = new StageBScenario(fixture, "field-edit");
        await s.BootstrapAsync();
        await s.BuildAccusedWorkspaceAsync(); // published v1
        var record = await s.CreateAccusedAsync(s.Clerk);
        var original = StageBScenario.SyntheticBytes(3000, 41);
        var firstDocument = await s.UploadAsync(s.Clerk, record, "evidence-one.txt", "text/plain", original);
        var firstAttachment = firstDocument.GetProperty("fileAttachmentId").GetGuid();

        var detail = await s.GetFormAsync(s.AccusedFormId);
        var givenName = FieldOf(detail, "given_name");
        var documents = FieldOf(detail, "case_documents");
        var givenId = givenName.GetProperty("fieldDefinitionId").GetGuid();
        var documentsId = documents.GetProperty("fieldDefinitionId").GetGuid();
        Assert.Equal(s.AccusedFields["given_name"], givenId);
        var givenRevision = givenName.GetProperty("fieldDefinitionRevision").GetInt64();

        // Identity is permanent: a different key or storage type is refused with an explanation and nothing changes.
        var rekey = await PatchFieldAsync(s, s.AccusedFormId, givenId, s.Designer, new { fieldDisplayName = "Forename", fieldDescription = "x", expectedFieldRevision = givenRevision, fieldKey = "forename" });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, rekey.Status);
        Assert.Equal("FIELD_KEY_IMMUTABLE", rekey.ErrorCode);
        var retype = await PatchFieldAsync(s, s.AccusedFormId, givenId, s.Designer, new { fieldDisplayName = "Forename", fieldDescription = "x", expectedFieldRevision = givenRevision, fieldDataType = "Integer" });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, retype.Status);
        Assert.Equal("FIELD_TYPE_IMMUTABLE", retype.ErrorCode);
        Assert.Equal(HttpStatusCode.Conflict, (await PatchFieldAsync(s, s.AccusedFormId, givenId, s.Designer, new { fieldDisplayName = "Forename", fieldDescription = "x", expectedFieldRevision = givenRevision + 7 })).Status);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await PatchFieldAsync(s, s.AccusedFormId, givenId, s.Designer, new { fieldDisplayName = " ", fieldDescription = "x", expectedFieldRevision = givenRevision })).Status);
        Assert.Equal("Given name", FieldOf(await s.GetFormAsync(s.AccusedFormId), "given_name").GetProperty("fieldDisplayName").GetString());

        // Only designers of the form may edit; another organization may not.
        Assert.Contains((await PatchFieldAsync(s, s.AccusedFormId, givenId, s.Clerk, new { fieldDisplayName = "Hijacked", fieldDescription = "", expectedFieldRevision = givenRevision })).Status, Denied);
        var other = new StageBScenario(fixture, "field-edit-other");
        await other.BootstrapAsync();
        Assert.Contains((await PatchFieldAsync(other, s.AccusedFormId, givenId, other.Designer, new { fieldDisplayName = "Hijacked", fieldDescription = "", expectedFieldRevision = givenRevision })).Status, Denied);

        // Rename the text field and the document collection (repeatedly): same ids, same keys, same types.
        var renamed = await PatchFieldAsync(s, s.AccusedFormId, givenId, s.Designer, new { fieldDisplayName = "Forename", fieldDescription = "Legal first name", expectedFieldRevision = givenRevision });
        Assert.True(renamed.IsSuccess, renamed.Body);
        Assert.Equal(givenId, renamed.Root.GetProperty("fieldDefinitionId").GetGuid());
        Assert.Equal("given_name", renamed.Root.GetProperty("fieldKey").GetString());
        Assert.Equal("String", renamed.Root.GetProperty("fieldDataType").GetString());
        Assert.Equal("Forename", renamed.Root.GetProperty("fieldDisplayName").GetString());
        Assert.Equal(givenRevision + 1, renamed.Root.GetProperty("fieldDefinitionRevision").GetInt64());
        var renamedAgain = await PatchFieldAsync(s, s.AccusedFormId, givenId, s.Designer, new { fieldDisplayName = "First name", fieldDescription = "Legal first name", expectedFieldRevision = givenRevision + 1 });
        Assert.True(renamedAgain.IsSuccess, renamedAgain.Body);
        var documentsRenamed = await PatchFieldAsync(s, s.AccusedFormId, documentsId, s.Designer, new { fieldDisplayName = "Evidence", fieldDescription = "Scanned exhibits", expectedFieldRevision = documents.GetProperty("fieldDefinitionRevision").GetInt64() });
        Assert.True(documentsRenamed.IsSuccess, documentsRenamed.Body);
        Assert.Equal(documentsId, documentsRenamed.Root.GetProperty("fieldDefinitionId").GetGuid());
        Assert.Equal("File", documentsRenamed.Root.GetProperty("fieldDataType").GetString());

        // Published version 1 is an immutable snapshot: it still carries the labels it was published with.
        var versionsBefore = await VersionsAsync(s, s.AccusedFormId);
        Assert.Single(versionsBefore.EnumerateArray());
        var v1 = versionsBefore[0].GetProperty("layoutJson").GetString()!;
        Assert.Contains("Given name", v1, StringComparison.Ordinal);
        Assert.Contains("Case documents", v1, StringComparison.Ordinal);
        Assert.DoesNotContain("Evidence", v1, StringComparison.Ordinal);

        // Draft edits can be saved repeatedly and only the published version changes anything for records.
        var draftOne = ReplaceFirst(ReplaceFirst(s.AccusedLayout(), "\"Given name\"", "\"First name\""), "\"Case documents\"", "\"Evidence\"");
        var firstRevision = await s.SaveDraftAsync(s.AccusedFormId, draftOne);
        var draftTwo = ReplaceFirst(ReplaceFirst(draftOne, "\"label\":\"First name\"", "\"label\":\"First name\",\"helpText\":\"As on the charge sheet\""), "\"maxLength\":100", "\"maxLength\":60");
        var secondRevision = await s.SaveDraftAsync(s.AccusedFormId, draftTwo);
        Assert.True(secondRevision > firstRevision);
        Assert.Single((await VersionsAsync(s, s.AccusedFormId)).EnumerateArray()); // saving a draft publishes nothing
        var published = await s.PublishAsync(s.AccusedFormId, "rename given name and document collection");
        Assert.Equal(2, published.GetProperty("versionNumber").GetInt32());

        var versionsAfter = await VersionsAsync(s, s.AccusedFormId);
        Assert.Equal(2, versionsAfter.GetArrayLength());
        Assert.Contains("Given name", versionsAfter.EnumerateArray().Single(v => v.GetProperty("versionNumber").GetInt32() == 1).GetProperty("layoutJson").GetString()!, StringComparison.Ordinal);

        // The existing record keeps every value and its attachment; the renamed collection is the same field, so uploads continue to work.
        var projection = await s.WaitProjectionAsync(s.Clerk, record);
        var layoutJson = projection.Root.GetProperty("layoutVersion").GetProperty("layoutJson").GetString()!;
        Assert.Equal(2, projection.Root.GetProperty("layoutVersion").GetProperty("versionNumber").GetInt32());
        using (var layout = JsonDocument.Parse(layoutJson))
        {
            var elements = layout.RootElement.GetProperty("sections").EnumerateArray().SelectMany(section => section.GetProperty("elements").EnumerateArray()).ToList();
            var collection = elements.Single(e => e.GetProperty("kind").GetString() == "documentCollection");
            Assert.Equal(documentsId, collection.GetProperty("fieldDefinitionId").GetGuid());
            Assert.Equal("Evidence", collection.GetProperty("label").GetString());
            var name = elements.Single(e => e.TryGetProperty("fieldDefinitionId", out var id) && id.GetGuid() == givenId);
            Assert.Equal("First name", name.GetProperty("label").GetString());
            Assert.Equal("As on the charge sheet", name.GetProperty("helpText").GetString());
            Assert.Equal(60, name.GetProperty("validation").GetProperty("maxLength").GetInt32());
        }

        var values = projection.Root.GetProperty("record").GetProperty("values").EnumerateArray().ToDictionary(v => v.GetProperty("fieldKey").GetString()!, v => v.GetProperty("stringValue").GetString());
        Assert.Equal("Avery", values["given_name"]);
        Assert.Equal("1990-02-28", values["date_of_birth"]);
        var listed = projection.Root.GetProperty("documents").EnumerateArray().Single();
        Assert.Equal(firstAttachment, listed.GetProperty("fileAttachmentId").GetGuid());
        Assert.Equal(documentsId, listed.GetProperty("fieldDefinitionId").GetGuid());
        Assert.True(listed.GetProperty("inAcceptedManifest").GetBoolean());
        var download = await fixture.DownloadAsync($"/api/v1/form-records/{record:D}/documents/{firstAttachment:D}/download", s.Token(s.Clerk));
        Assert.Equal(HttpStatusCode.OK, download.Status);
        Assert.Equal(StageBScenario.Sha256Hex(original), StageBScenario.Sha256Hex(download.Body));

        await s.UploadAsync(s.Clerk, record, "evidence-two.txt", "text/plain", StageBScenario.SyntheticBytes(1500, 42));
        var afterSecond = await s.WaitProjectionAsync(s.Clerk, record);
        Assert.Equal(2, afterSecond.Root.GetProperty("documents").GetArrayLength());

        // Edits are audited.
        var activity = await fixture.Api.GetAsync("/api/v1/my-activity?limit=100", s.Token(s.Designer));
        Assert.Contains("FIELD_CONFIGURATION_CHANGED", activity.Root.EnumerateArray().Select(e => e.GetProperty("eventAction").GetString()));
    }

    [Fact]
    public async Task IncompatibleRulesAreRejectedWithAnExplanationAndPublishedLabelsAreSnapshots()
    {
        var s = new StageBScenario(fixture, "field-rules");
        await s.BootstrapAsync();
        var formId = await s.CreateFormAsync("Rules " + s.Name, s.Clerk);
        var age = await s.AddFieldAsync(formId, "age_years", "Age", "Integer");
        var note = await s.AddFieldAsync(formId, "note_text", "Note", "String");
        var seen = await s.AddFieldAsync(formId, "seen_on", "Seen on", "Date");

        async Task<HttpResult> TryDraft(string layout) =>
            await fixture.Api.SendAsync(HttpMethod.Put, $"/api/v1/form-definitions/{formId:D}/draft", s.Token(s.Designer), new { expectedDraftRevision = await s.CurrentDraftRevisionAsync(formId), layoutJson = layout }, null);

        string Layout(params string[] elements) => StageBScenario.Layout("Rules", StageBScenario.Section("main", "Main", elements));

        foreach (var (layout, expected) in new[]
        {
            (Layout(StageBScenario.Field(age, "number", "Age", 2, validation: new { maxLength = 5 })), "String"),
            (Layout(StageBScenario.Field(note, "text", "Note", 2, validation: new { minimum = 1 })), "Integer and Decimal"),
            (Layout(StageBScenario.Field(note, "text", "Note", 2, validation: new { minDate = "2026-01-01" })), "Date and DateTime"),
            (Layout(StageBScenario.Field(seen, "date", "Seen", 2, validation: new { pattern = "[0-9]+" })), "String"),
            (Layout(StageBScenario.Field(note, "text", "Note", 2, validation: new { minLength = 10, maxLength = 5 })), "bounds are inconsistent"),
            (Layout(StageBScenario.Field(age, "number", "Age", 2, validation: new { minimum = 50, maximum = 5 })), "bounds are inconsistent"),
        })
        {
            var rejected = await TryDraft(layout);
            Assert.Equal(HttpStatusCode.UnprocessableEntity, rejected.Status);
            Assert.Equal("LAYOUT_INVALID", rejected.ErrorCode);
            Assert.Contains(expected, rejected.Body, StringComparison.Ordinal);
        }

        // A compatible layout saves (repeatedly) and publishes; an unlabelled element is snapshotted with the catalog name.
        var compatible = Layout(StageBScenario.Field(age, "number", "", 2, validation: new { minimum = 0, maximum = 130 }), StageBScenario.Field(note, "text", "Note", 2, validation: new { minLength = 1, maxLength = 40 }));
        Assert.Equal(HttpStatusCode.OK, (await TryDraft(compatible)).Status);
        Assert.Equal(HttpStatusCode.OK, (await TryDraft(compatible.Replace("\"maxLength\":40", "\"maxLength\":50", StringComparison.Ordinal))).Status);
        await s.PublishAsync(formId, "initial rules");
        var v1 = (await VersionsAsync(s, formId))[0].GetProperty("layoutJson").GetString()!;
        Assert.Equal("Age", LabelOf(v1, age));

        var ageField = FieldOf(await s.GetFormAsync(formId), "age_years");
        var rename = await PatchFieldAsync(s, formId, age, s.Designer, new { fieldDisplayName = "Age in years", fieldDescription = "", expectedFieldRevision = ageField.GetProperty("fieldDefinitionRevision").GetInt64() });
        Assert.True(rename.IsSuccess, rename.Body);
        var after = (await VersionsAsync(s, formId))[0].GetProperty("layoutJson").GetString()!;
        Assert.Equal(v1, after); // the published snapshot did not move
        Assert.Equal("Age", LabelOf(after, age));

        // The draft element has no label of its own, so the next publish snapshots the catalog name as it is then: the rename reaches
        // records only through a new immutable version, and version 1 stays exactly as published.
        var next = await fixture.Api.PostAsync($"/api/v1/form-definitions/{formId:D}/publish", s.Token(s.Designer), new { expectedDraftRevision = await s.CurrentDraftRevisionAsync(formId), expectedCurrentVersionNumber = 1, changeReason = "pick up the new age label" });
        Assert.Equal(HttpStatusCode.Created, next.Status);
        var versions = await VersionsAsync(s, formId);
        Assert.Equal(2, versions.GetArrayLength());
        Assert.Equal("Age in years", LabelOf(versions.EnumerateArray().Single(v => v.GetProperty("versionNumber").GetInt32() == 2).GetProperty("layoutJson").GetString()!, age));
        Assert.Equal(v1, versions.EnumerateArray().Single(v => v.GetProperty("versionNumber").GetInt32() == 1).GetProperty("layoutJson").GetString());
    }
}
