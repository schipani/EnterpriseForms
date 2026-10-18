using System.Net;
using System.Text.Json;
using EnterpriseForms.StageBTests.Harness;
using Xunit;

namespace EnterpriseForms.StageBTests.Tests;

/// <summary>Designer persistence, draft CAS, publish/versioning and restore (requirement 4; B-03).</summary>
[Collection(StageBStackCollectionDefinition.Name)]
public sealed class DesignerTests(StageBFixture fixture)
{
    [Fact]
    public async Task DraftSaveIsRevisionGuardedAndPublishCreatesImmutableVersions()
    {
        var s = new StageBScenario(fixture, "designer");
        await s.BootstrapAsync();
        await s.BuildAccusedWorkspaceAsync();

        var detail = await s.GetFormAsync(s.AccusedFormId);
        Assert.Equal(1, detail.GetProperty("summary").GetProperty("currentVersionNumber").GetInt32());
        Assert.Equal(6, detail.GetProperty("fields").GetArrayLength());
        Assert.Single(detail.GetProperty("relationships").EnumerateArray());
        var publishedKinds = Kinds(detail.GetProperty("currentVersion").GetProperty("layoutJson").GetString()!);
        Assert.Contains("relatedFormList", publishedKinds);
        Assert.Contains("documentCollection", publishedKinds);
        Assert.Equal(5, publishedKinds.Count(k => k == "field"));
        Assert.Equal(2, detail.GetProperty("currentVersion").GetProperty("layoutSchemaVersion").GetInt32());

        // Stale draft revision is rejected (optimistic concurrency), the fresh one is accepted.
        var stale = await fixture.Api.SendAsync(HttpMethod.Put, $"/api/v1/form-definitions/{s.AccusedFormId:D}/draft", s.Token(s.Designer), new { expectedDraftRevision = 0, layoutJson = s.AccusedLayout(maxFiles: 3) }, null);
        Assert.Equal(HttpStatusCode.Conflict, stale.Status);
        Assert.Equal("DRAFT_REVISION_CONFLICT", stale.ErrorCode);
        var revision = await s.SaveDraftAsync(s.AccusedFormId, s.AccusedLayout(maxFiles: 3));
        Assert.True(revision >= 2);

        // Layouts referencing foreign fields, bad spans or script content are refused by the server.
        var foreign = await fixture.Api.SendAsync(HttpMethod.Put, $"/api/v1/form-definitions/{s.AccusedFormId:D}/draft", s.Token(s.Designer), new { expectedDraftRevision = revision, layoutJson = StageBScenario.Layout("x", StageBScenario.Section("main", "Main", StageBScenario.Field(s.AddressFields["street_address"], "text", "Not my field", 4))) }, null);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, foreign.Status);
        Assert.Equal("LAYOUT_INVALID", foreign.ErrorCode);
        var script = await fixture.Api.SendAsync(HttpMethod.Put, $"/api/v1/form-definitions/{s.AccusedFormId:D}/draft", s.Token(s.Designer), new { expectedDraftRevision = revision, layoutJson = s.AccusedLayout().Replace("Given name", "<img src=x onerror=alert(1)>", StringComparison.Ordinal) }, null);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, script.Status);

        // Publish v2, then restore v1 into the draft and publish v3 with lineage; v1 and v2 stay unchanged.
        var v2 = await s.PublishAsync(s.AccusedFormId, "tightened document limit");
        Assert.Equal(2, v2.GetProperty("versionNumber").GetInt32());
        var versions = await fixture.Api.GetAsync($"/api/v1/form-definitions/{s.AccusedFormId:D}/versions", s.Token(s.Designer));
        var list = versions.Root.EnumerateArray().ToList();
        Assert.Equal(2, list.Count);
        var v1Id = list.Single(v => v.GetProperty("versionNumber").GetInt32() == 1).GetProperty("formVersionId").GetGuid();
        var restored = await fixture.Api.PostAsync($"/api/v1/form-definitions/{s.AccusedFormId:D}/restore-draft", s.Token(s.Designer), new { fromFormVersionId = v1Id });
        Assert.True(restored.IsSuccess, restored.Body);
        Assert.Equal(5, MaxFiles(restored.Root.GetProperty("layoutJson").GetString()!));
        var v3 = await s.PublishAsync(s.AccusedFormId, "restore v1 layout");
        Assert.Equal(3, v3.GetProperty("versionNumber").GetInt32());
        Assert.Equal(v1Id, v3.GetProperty("restoredFromFormVersionId").GetGuid());
        var after = (await fixture.Api.GetAsync($"/api/v1/form-definitions/{s.AccusedFormId:D}/versions", s.Token(s.Designer))).Root.EnumerateArray().ToList();
        Assert.Equal(3, after.Count);
        Assert.Equal(3, MaxFiles(after.Single(v => v.GetProperty("versionNumber").GetInt32() == 2).GetProperty("layoutJson").GetString()!));
        Assert.Equal(5, MaxFiles(after.Single(v => v.GetProperty("versionNumber").GetInt32() == 3).GetProperty("layoutJson").GetString()!));

        // Publishing requires a changed draft: an immediate re-publish has nothing to publish.
        var nothing = await fixture.Api.PostAsync($"/api/v1/form-definitions/{s.AccusedFormId:D}/publish", s.Token(s.Designer), new { expectedDraftRevision = await s.CurrentDraftRevisionAsync(s.AccusedFormId), expectedCurrentVersionNumber = 3, changeReason = "again" });
        Assert.Equal(HttpStatusCode.Conflict, nothing.Status);
        Assert.Equal("NO_DRAFT_CHANGES", nothing.ErrorCode);
    }

    private static List<string> Kinds(string layoutJson)
    {
        using var document = JsonDocument.Parse(layoutJson);
        return document.RootElement.GetProperty("sections").EnumerateArray().SelectMany(s => s.GetProperty("elements").EnumerateArray()).Select(e => e.GetProperty("kind").GetString()!).ToList();
    }

    private static int MaxFiles(string layoutJson)
    {
        using var document = JsonDocument.Parse(layoutJson);
        return document.RootElement.GetProperty("sections").EnumerateArray().SelectMany(s => s.GetProperty("elements").EnumerateArray()).Single(e => e.GetProperty("kind").GetString() == "documentCollection").GetProperty("upload").GetProperty("maxFiles").GetInt32();
    }

    [Fact]
    public async Task OptionsAreStableIdentitiesAndDeactivationBlocksNewSelectionOnly()
    {
        var s = new StageBScenario(fixture, "options");
        await s.BootstrapAsync();
        await s.BuildAccusedWorkspaceAsync();
        var record = await s.CreateAccusedAsync(s.Clerk);

        var detail = await s.GetFormAsync(s.AccusedFormId);
        var statusField = detail.GetProperty("fields").EnumerateArray().Single(f => f.GetProperty("fieldKey").GetString() == "case_status");
        var options = statusField.GetProperty("options").EnumerateArray().ToList();
        var open = options.Single(o => o.GetProperty("optionKey").GetString() == "open");
        var closed = options.Single(o => o.GetProperty("optionKey").GetString() == "closed");

        // Rename "closed", deactivate "open", add "stayed": ids are preserved, nothing is deleted.
        var changed = await fixture.Api.SendAsync(HttpMethod.Put, $"/api/v1/form-definitions/{s.AccusedFormId:D}/fields/{statusField.GetProperty("fieldDefinitionId").GetGuid():D}/options", s.Token(s.Designer), new
        {
            changes = new object[]
            {
                new { fieldOptionId = open.GetProperty("fieldOptionId").GetGuid(), optionKey = "open", optionDisplayText = "Open", optionSortOrder = 1, isActive = false },
                new { fieldOptionId = closed.GetProperty("fieldOptionId").GetGuid(), optionKey = "closed", optionDisplayText = "Closed (final)", optionSortOrder = 2, isActive = true },
                new { fieldOptionId = (Guid?)null, optionKey = "stayed", optionDisplayText = "Stayed", optionSortOrder = 3, isActive = true },
            },
        }, null);
        Assert.True(changed.IsSuccess, changed.Body);
        var updated = changed.Root.GetProperty("options").EnumerateArray().ToList();
        Assert.Equal(3, updated.Count);
        Assert.Equal(open.GetProperty("fieldOptionId").GetGuid(), updated.Single(o => o.GetProperty("optionKey").GetString() == "open").GetProperty("fieldOptionId").GetGuid());
        Assert.False(updated.Single(o => o.GetProperty("optionKey").GetString() == "open").GetProperty("isActive").GetBoolean());

        // The existing record still shows its historical value; a new record cannot select the deactivated option.
        var projection = await s.WaitProjectionAsync(s.Clerk, record);
        var statusValue = projection.Root.GetProperty("record").GetProperty("values").EnumerateArray().Single(v => v.GetProperty("fieldKey").GetString() == "case_status");
        Assert.Equal("open", statusValue.GetProperty("stringValue").GetString());
        var rejected = await fixture.Api.PostAsync($"/api/v1/form-definitions/{s.AccusedFormId:D}/records", s.Token(s.Clerk), new { values = StageBScenario.AccusedValues("Blocked", "Option"), ownerTeamId = (Guid?)null });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, rejected.Status);
        Assert.Equal("ORGANIZATION_OPTION_DISABLED", rejected.ErrorCode);
        var values = StageBScenario.AccusedValues("Allowed", "Option");
        values["case_status"] = "stayed";
        var accepted = await fixture.Api.PostAsync($"/api/v1/form-definitions/{s.AccusedFormId:D}/records", s.Token(s.Clerk), new { values, ownerTeamId = (Guid?)null });
        Assert.Equal(HttpStatusCode.Created, accepted.Status);
    }

    [Fact]
    public async Task TypedValuesAreValidatedServerSideAndRoundTrip()
    {
        var s = new StageBScenario(fixture, "typed");
        await s.BootstrapAsync();
        await s.BuildAccusedWorkspaceAsync();

        foreach (var (key, bad) in new[] { ("date_of_birth", "28/02/1990"), ("in_custody", "yes"), ("case_status", "unknown"), ("given_name", new string('x', 101)) })
        {
            var values = StageBScenario.AccusedValues();
            values[key] = bad;
            var rejected = await fixture.Api.PostAsync($"/api/v1/form-definitions/{s.AccusedFormId:D}/records", s.Token(s.Clerk), new { values, ownerTeamId = (Guid?)null });
            Assert.True(rejected.Status == HttpStatusCode.UnprocessableEntity, $"{key}={bad} should be rejected: {rejected.Status} {rejected.Body}");
        }

        var missingRequired = StageBScenario.AccusedValues();
        missingRequired.Remove("family_name");
        var required = await fixture.Api.PostAsync($"/api/v1/form-definitions/{s.AccusedFormId:D}/records", s.Token(s.Clerk), new { values = missingRequired, ownerTeamId = (Guid?)null });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, required.Status);

        var record = await s.CreateAccusedAsync(s.Clerk);
        var projection = await s.WaitProjectionAsync(s.Clerk, record);
        var byKey = projection.Root.GetProperty("record").GetProperty("values").EnumerateArray().ToDictionary(v => v.GetProperty("fieldKey").GetString()!, v => v);
        Assert.Equal("1990-02-28", byKey["date_of_birth"].GetProperty("stringValue").GetString());
        Assert.Equal("Date", byKey["date_of_birth"].GetProperty("fieldDataType").GetString());
        Assert.Equal("true", byKey["in_custody"].GetProperty("stringValue").GetString());
        Assert.Equal("open", byKey["case_status"].GetProperty("stringValue").GetString());
        Assert.Equal("Verified", projection.Root.GetProperty("record").GetProperty("integrityStatus").GetString());
        Assert.True(projection.Root.GetProperty("permissions").GetProperty("canEdit").GetBoolean());

        // Edit with If-Match semantics: stale revision conflicts; fresh revision succeeds and bumps the revision.
        var stale = await fixture.Api.PatchAsync($"/api/v1/form-records/{record:D}", s.Token(s.Clerk), new { expectedContentRevision = 99, values = new Dictionary<string, string> { ["given_name"] = "Changed" } });
        Assert.True(stale.Status is HttpStatusCode.Conflict or HttpStatusCode.PreconditionFailed, stale.Body);
        var edit = await fixture.Api.PatchAsync($"/api/v1/form-records/{record:D}", s.Token(s.Clerk), new { expectedContentRevision = projection.Root.GetProperty("record").GetProperty("contentRevision").GetInt64(), values = new Dictionary<string, string> { ["given_name"] = "Changed", ["in_custody"] = "false" } });
        Assert.True(edit.IsSuccess, edit.Body);
        var after = await s.WaitProjectionAsync(s.Clerk, record);
        var afterValues = after.Root.GetProperty("record").GetProperty("values").EnumerateArray().ToDictionary(v => v.GetProperty("fieldKey").GetString()!, v => v.GetProperty("stringValue").GetString());
        Assert.Equal("Changed", afterValues["given_name"]);
        Assert.Equal("false", afterValues["in_custody"]);
        Assert.Equal("1990-02-28", afterValues["date_of_birth"]);
        Assert.Equal(2, after.Root.GetProperty("record").GetProperty("contentRevision").GetInt64());

        // Audit history is a local projection and lists the create and update.
        var activity = await fixture.Api.GetAsync($"/api/v1/form-records/{record:D}/activity", s.Token(s.Clerk));
        Assert.True(activity.IsSuccess, activity.Body);
        var actions = activity.Root.GetProperty("events").EnumerateArray().Select(e => e.GetProperty("eventAction").GetString()).ToList();
        Assert.Contains("RECORD_CREATED", actions);
        Assert.Contains("RECORD_UPDATED", actions);
        Assert.Equal(JsonValueKind.String, activity.Root.GetProperty("trustNote").ValueKind);
    }
}
