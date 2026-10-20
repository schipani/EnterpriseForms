using System.Net;
using System.Text.Json;
using EnterpriseForms.SecurityTests.Harness;
using EnterpriseForms.StageBTests.Harness;
using Xunit;

namespace EnterpriseForms.StageBTests.Tests;

/// <summary>
/// Phase C2 priority 1 — dropdown and multi-select fields on the real stack: permanent option identities, typed storage
/// (one plaintext row per selected option, ciphertext rows for encrypted fields), server-side validation of every
/// selection, published option snapshots and defaults, deactivation that never corrupts stored records, and reusable
/// option lists with explicit sync.
/// </summary>
[Collection(StageBStackCollectionDefinition.Name)]
public sealed class DropdownTests(StageBFixture fixture)
{
    private static readonly HttpStatusCode[] Denied = [HttpStatusCode.Forbidden, HttpStatusCode.NotFound];

    private Task<HttpResult> AddOptionFieldAsync(StageBScenario s, Guid formId, string key, string name, string mode, string[] options, string? classification = null, Guid? optionListId = null) =>
        fixture.Api.PostAsync($"/api/v1/form-definitions/{formId:D}/fields", s.Token(s.Designer), new
        {
            fieldKey = key,
            fieldDisplayName = name,
            fieldDescription = string.Empty,
            fieldDataType = "Option",
            options = optionListId is null ? options.Select(o => new { optionKey = o, optionDisplayText = char.ToUpperInvariant(o[0]) + o[1..] }).ToArray() : null,
            fieldClassification = classification,
            fieldProtectionMode = (string?)null,
            optionSelectionMode = mode,
            optionListId,
        });

    private static string Element(Guid fieldId, string control, string label, bool required = false, object? extra = null)
    {
        var element = new Dictionary<string, object?> { ["kind"] = "field", ["fieldDefinitionId"] = fieldId, ["control"] = control, ["label"] = label, ["required"] = required, ["layout"] = new { columnSpan = 2, startOnNewRow = false }, ["validation"] = new { } };
        if (extra is not null)
        {
            foreach (var property in extra.GetType().GetProperties())
            {
                element[property.Name] = property.GetValue(extra);
            }
        }

        return JsonSerializer.Serialize(element);
    }

    private static List<JsonElement> ValuesFor(JsonElement projectionRoot, string key) =>
        projectionRoot.GetProperty("record").GetProperty("values").EnumerateArray().Where(v => v.GetProperty("fieldKey").GetString() == key).ToList();

    private static string[] Keys(IEnumerable<JsonElement> values) => values.Select(v => v.GetProperty("stringValue").GetString()!).Order(StringComparer.Ordinal).ToArray();

    private async Task<HttpResult> SaveDraftRawAsync(StageBScenario s, Guid formId, string layoutJson) =>
        await fixture.Api.SendAsync(HttpMethod.Put, $"/api/v1/form-definitions/{formId:D}/draft", s.Token(s.Designer), new { expectedDraftRevision = await s.CurrentDraftRevisionAsync(formId), layoutJson }, null);

    [Fact]
    public async Task MultiSelectAndDropdownsStoreTypedRowsValidateOnTheServerAndKeepStableIdentities()
    {
        var s = new StageBScenario(fixture, "dropdowns");
        await s.BootstrapAsync();
        var formId = await s.CreateFormAsync("Choices " + s.Name, s.Clerk);

        var priority = await AddOptionFieldAsync(s, formId, "priority_level", "Priority", "Single", ["low", "medium", "high"]);
        Assert.Equal(HttpStatusCode.Created, priority.Status);
        Assert.Equal("Single", priority.Root.GetProperty("optionSelectionMode").GetString());
        var priorityId = priority.Root.GetProperty("fieldDefinitionId").GetGuid();
        var tags = await AddOptionFieldAsync(s, formId, "topic_tags", "Topics", "Multiple", ["red", "green", "blue"]);
        Assert.Equal("Multiple", tags.Root.GetProperty("optionSelectionMode").GetString());
        var tagsId = tags.Root.GetProperty("fieldDefinitionId").GetGuid();
        var secret = await AddOptionFieldAsync(s, formId, "sealed_tags", "Sealed topics", "Multiple", ["north", "south"], classification: "Confidential");
        Assert.Equal("ApplicationEncrypted", secret.Root.GetProperty("fieldProtectionMode").GetString());
        var secretId = secret.Root.GetProperty("fieldDefinitionId").GetGuid();

        // A selection mode or list on a non-dropdown, and an unknown mode, are refused.
        var textWithMode = await fixture.Api.PostAsync($"/api/v1/form-definitions/{formId:D}/fields", s.Token(s.Designer), new { fieldKey = "plain_text", fieldDisplayName = "Plain", fieldDescription = "", fieldDataType = "String", options = (object?)null, optionSelectionMode = "Multiple" });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, textWithMode.Status);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await AddOptionFieldAsync(s, formId, "odd_mode", "Odd", "Several", ["a"])).Status);

        // The control must match the permanent selection mode; defaults must name active options and fit the mode.
        Assert.Equal("LAYOUT_INVALID", (await SaveDraftRawAsync(s, formId, StageBScenario.Layout("x", StageBScenario.Section("main", "Main", Element(tagsId, "select", "Topics"))))).ErrorCode);
        Assert.Equal("LAYOUT_INVALID", (await SaveDraftRawAsync(s, formId, StageBScenario.Layout("x", StageBScenario.Section("main", "Main", Element(priorityId, "multiselect", "Priority"))))).ErrorCode);
        var badDefault = await SaveDraftRawAsync(s, formId, StageBScenario.Layout("x", StageBScenario.Section("main", "Main", Element(priorityId, "select", "Priority", extra: new { defaultOptionKeys = new[] { "urgent" } }))));
        Assert.Equal("LAYOUT_INVALID", badDefault.ErrorCode);
        var twoDefaults = await SaveDraftRawAsync(s, formId, StageBScenario.Layout("x", StageBScenario.Section("main", "Main", Element(priorityId, "select", "Priority", extra: new { defaultOptionKeys = new[] { "low", "high" } }))));
        Assert.Equal("LAYOUT_INVALID", twoDefaults.ErrorCode);
        Assert.Contains("only one default", twoDefaults.Body, StringComparison.OrdinalIgnoreCase);

        await s.SaveDraftAsync(formId, StageBScenario.Layout("Choices", StageBScenario.Section("main", "Main",
            Element(priorityId, "select", "Priority", required: true, extra: new { defaultOptionKeys = new[] { "medium" } }),
            Element(tagsId, "multiselect", "Topics", extra: new { defaultOptionKeys = new[] { "red", "green" } }),
            Element(secretId, "multiselect", "Sealed topics"))));
        var published = await s.PublishAsync(formId, "dropdowns");

        // The published version carries an immutable snapshot of each dropdown's options (ids, keys, labels, order, availability) and its defaults.
        using (var layout = JsonDocument.Parse(published.GetProperty("layoutJson").GetString()!))
        {
            var elements = layout.RootElement.GetProperty("sections")[0].GetProperty("elements").EnumerateArray().ToDictionary(e => e.GetProperty("fieldDefinitionId").GetGuid());
            var snapshot = elements[tagsId].GetProperty("options").EnumerateArray().ToList();
            Assert.Equal(["red", "green", "blue"], snapshot.Select(o => o.GetProperty("optionKey").GetString()).ToArray());
            Assert.All(snapshot, o => Assert.Equal(JsonValueKind.String, o.GetProperty("fieldOptionId").ValueKind));
            Assert.Equal("Red", snapshot[0].GetProperty("label").GetString());
            Assert.Equal(["red", "green"], elements[tagsId].GetProperty("defaultOptionKeys").EnumerateArray().Select(d => d.GetString()).ToArray());
            Assert.Equal("multiselect", elements[tagsId].GetProperty("control").GetString());
        }

        // Create: one typed row per selected option for a plaintext multi-select; ciphertext rows (no typed copy) for the encrypted one.
        var created = await fixture.Api.PostAsync($"/api/v1/form-definitions/{formId:D}/records", s.Token(s.Clerk), new { values = new Dictionary<string, string> { ["priority_level"] = "high", ["topic_tags"] = "blue,red", ["sealed_tags"] = "south,north" }, ownerTeamId = (Guid?)null });
        Assert.Equal(HttpStatusCode.Created, created.Status);
        var record = created.Root.GetProperty("formRecordId").GetGuid();
        Assert.Equal("3", fixture.Stack.AttackerSql($"SELECT count(*) FROM field_option_values WHERE form_record_id = '{record:D}';").Trim()); // priority + two topics
        Assert.Equal("2", fixture.Stack.AttackerSql($"SELECT count(*) FROM field_encrypted_values WHERE form_record_id = '{record:D}';").Trim()); // the two sealed topics
        var sealedDump = fixture.Stack.AttackerSql($"SELECT encode(encrypted_value_bytes,'hex') FROM field_encrypted_values WHERE form_record_id = '{record:D}';");
        Assert.DoesNotContain(Convert.ToHexStringLower("north"u8.ToArray()), sealedDump, StringComparison.OrdinalIgnoreCase);

        var projection = await s.WaitProjectionAsync(s.Clerk, record);
        Assert.Equal(["blue", "red"], Keys(ValuesFor(projection.Root, "topic_tags")));
        var sealedValues = ValuesFor(projection.Root, "sealed_tags");
        Assert.Equal(["north", "south"], Keys(sealedValues));
        Assert.All(sealedValues, v => Assert.True(v.GetProperty("applicationEncrypted").GetBoolean()));
        Assert.Equal("high", Keys(ValuesFor(projection.Root, "priority_level")).Single());
        var redItem = ValuesFor(projection.Root, "topic_tags").Single(v => v.GetProperty("stringValue").GetString() == "red").GetProperty("fieldValueItemId").GetGuid();

        // Every selection is validated on the server: unknown, duplicated-as-list for a single-select, and wrong-field keys fail atomically.
        long Revision(HttpResult p) => p.Root.GetProperty("record").GetProperty("contentRevision").GetInt64();
        async Task<HttpResult> EditAsync(Dictionary<string, string> values) =>
            await fixture.Api.PatchAsync($"/api/v1/form-records/{record:D}", s.Token(s.Clerk), new { expectedContentRevision = Revision(await s.WaitProjectionAsync(s.Clerk, record)), values });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await EditAsync(new() { ["topic_tags"] = "red,purple" })).Status);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await EditAsync(new() { ["priority_level"] = "low,high" })).Status);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await EditAsync(new() { ["priority_level"] = "north" })).Status);
        Assert.Equal(["blue", "red"], Keys(ValuesFor((await s.WaitProjectionAsync(s.Clerk, record)).Root, "topic_tags"))); // nothing changed

        // Editing the selection keeps the identity of every option that stays selected and drops only the removed one.
        var edit = await EditAsync(new() { ["topic_tags"] = "red,green" });
        Assert.True(edit.IsSuccess, edit.Body);
        var edited = await s.WaitProjectionAsync(s.Clerk, record);
        Assert.Equal(["green", "red"], Keys(ValuesFor(edited.Root, "topic_tags")));
        Assert.Equal(redItem, ValuesFor(edited.Root, "topic_tags").Single(v => v.GetProperty("stringValue").GetString() == "red").GetProperty("fieldValueItemId").GetGuid());
        Assert.Equal(["north", "south"], Keys(ValuesFor(edited.Root, "sealed_tags")));
        var cleared = await EditAsync(new() { ["topic_tags"] = string.Empty });
        Assert.True(cleared.IsSuccess, cleared.Body);
        Assert.Empty(ValuesFor((await s.WaitProjectionAsync(s.Clerk, record)).Root, "topic_tags"));
        Assert.True((await EditAsync(new() { ["topic_tags"] = "green,blue" })).IsSuccess);

        // Option management: relabel, reorder, add and deactivate keep ids; keys are permanent; nothing can be deleted.
        var detail = await s.GetFormAsync(formId);
        var tagField = detail.GetProperty("fields").EnumerateArray().Single(f => f.GetProperty("fieldKey").GetString() == "topic_tags");
        var byKey = tagField.GetProperty("options").EnumerateArray().ToDictionary(o => o.GetProperty("optionKey").GetString()!, o => o.GetProperty("fieldOptionId").GetGuid());
        string OptionsPath() => $"/api/v1/form-definitions/{formId:D}/fields/{tagsId:D}/options";
        var renamedKey = await fixture.Api.SendAsync(HttpMethod.Put, OptionsPath(), s.Token(s.Designer), new { changes = new object[] { new { fieldOptionId = byKey["red"], optionKey = "crimson", optionDisplayText = "Crimson", optionSortOrder = 0, isActive = true } } }, null);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, renamedKey.Status);
        var dupe = await fixture.Api.SendAsync(HttpMethod.Put, OptionsPath(), s.Token(s.Designer), new { changes = new object[] { new { fieldOptionId = (Guid?)null, optionKey = "red", optionDisplayText = "Another red", optionSortOrder = 9, isActive = true } } }, null);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, dupe.Status);
        var reorder = await fixture.Api.SendAsync(HttpMethod.Put, OptionsPath(), s.Token(s.Designer), new
        {
            changes = new object[]
            {
                new { fieldOptionId = byKey["blue"], optionKey = "blue", optionDisplayText = "Deep blue", optionSortOrder = 0, isActive = true },
                new { fieldOptionId = byKey["red"], optionKey = "red", optionDisplayText = "Red", optionSortOrder = 1, isActive = false },
                new { fieldOptionId = byKey["green"], optionKey = "green", optionDisplayText = "Green", optionSortOrder = 2, isActive = true },
                new { fieldOptionId = (Guid?)null, optionKey = "violet", optionDisplayText = "Violet", optionSortOrder = 3, isActive = true },
            },
        }, null);
        Assert.True(reorder.IsSuccess, reorder.Body);
        var after = reorder.Root.GetProperty("options").EnumerateArray().ToList();
        Assert.Equal(["blue", "red", "green", "violet"], after.Select(o => o.GetProperty("optionKey").GetString()).ToArray());
        Assert.Equal(byKey["red"], after.Single(o => o.GetProperty("optionKey").GetString() == "red").GetProperty("fieldOptionId").GetGuid());

        // The published version is untouched by the catalog changes (snapshot), and a stored selection stays readable.
        var versions = await fixture.Api.GetAsync($"/api/v1/form-definitions/{formId:D}/versions", s.Token(s.Designer));
        using (var layout = JsonDocument.Parse(versions.Root.EnumerateArray().Single(v => v.GetProperty("versionNumber").GetInt32() == 1).GetProperty("layoutJson").GetString()!))
        {
            var snapshot = layout.RootElement.GetProperty("sections")[0].GetProperty("elements").EnumerateArray().Single(e => e.GetProperty("fieldDefinitionId").GetGuid() == tagsId).GetProperty("options").EnumerateArray().ToList();
            Assert.Equal(["red", "green", "blue"], snapshot.Select(o => o.GetProperty("optionKey").GetString()).ToArray());
            Assert.All(snapshot, o => Assert.True(o.GetProperty("isActive").GetBoolean()));
        }

        // Deactivation never rewrites stored records: a record still holding the deactivated option stays readable, an edit that keeps it
        // is refused with a structured reason until it is replaced, and nothing is silently cleared.
        var redRecord = (await fixture.Api.PostAsync($"/api/v1/form-definitions/{formId:D}/records", s.Token(s.Clerk), new { values = new Dictionary<string, string> { ["priority_level"] = "low", ["topic_tags"] = "green,violet" }, ownerTeamId = (Guid?)null })).Root.GetProperty("formRecordId").GetGuid();
        await s.WaitProjectionAsync(s.Clerk, redRecord);
        var denied = await fixture.Api.PostAsync($"/api/v1/form-definitions/{formId:D}/records", s.Token(s.Clerk), new { values = new Dictionary<string, string> { ["priority_level"] = "low", ["topic_tags"] = "red" }, ownerTeamId = (Guid?)null });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, denied.Status);
        Assert.Equal("ORGANIZATION_OPTION_DISABLED", denied.ErrorCode);
        Assert.Equal(["blue", "green"], Keys(ValuesFor((await s.WaitProjectionAsync(s.Clerk, record)).Root, "topic_tags")));
        var reactivate = await fixture.Api.SendAsync(HttpMethod.Put, OptionsPath(), s.Token(s.Designer), new { changes = new object[] { new { fieldOptionId = byKey["red"], optionKey = "red", optionDisplayText = "Red", optionSortOrder = 1, isActive = true } } }, null);
        Assert.True(reactivate.IsSuccess, reactivate.Body);
        var deactivateGreen = await fixture.Api.SendAsync(HttpMethod.Put, OptionsPath(), s.Token(s.Designer), new { changes = new object[] { new { fieldOptionId = byKey["green"], optionKey = "green", optionDisplayText = "Green", optionSortOrder = 2, isActive = false } } }, null);
        Assert.True(deactivateGreen.IsSuccess, deactivateGreen.Body);
        var blocked = await EditAsync(new() { ["priority_level"] = "medium" });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, blocked.Status);
        Assert.Equal("ORGANIZATION_OPTION_DISABLED", blocked.ErrorCode);
        var fixedUp = await EditAsync(new() { ["topic_tags"] = "blue,red", ["priority_level"] = "medium" });
        Assert.True(fixedUp.IsSuccess, fixedUp.Body);
        Assert.Equal(["blue", "red"], Keys(ValuesFor((await s.WaitProjectionAsync(s.Clerk, record)).Root, "topic_tags")));

        // Option administration is a design permission: the form user cannot change options, and another organization cannot see them.
        Assert.Contains((await fixture.Api.SendAsync(HttpMethod.Put, OptionsPath(), s.Token(s.Clerk), new { changes = Array.Empty<object>() }, null)).Status, Denied);
        var other = new StageBScenario(fixture, "dropdowns-other");
        await other.BootstrapAsync();
        Assert.Contains((await fixture.Api.SendAsync(HttpMethod.Put, OptionsPath(), other.Token(other.Designer), new { changes = Array.Empty<object>() }, null)).Status, Denied);
    }

    [Fact]
    public async Task FieldScopedSharesCoverEverySelectionOfADropdownOrMultiSelectAndNothingElse()
    {
        var s = new StageBScenario(fixture, "dropdown-shares");
        await s.BootstrapAsync();
        var formId = await s.CreateFormAsync("Shared choices " + s.Name, s.Clerk);
        var priorityId = (await AddOptionFieldAsync(s, formId, "priority_level", "Priority", "Single", ["low", "high"])).Root.GetProperty("fieldDefinitionId").GetGuid();
        var tagsId = (await AddOptionFieldAsync(s, formId, "topic_tags", "Topics", "Multiple", ["red", "green", "blue"])).Root.GetProperty("fieldDefinitionId").GetGuid();
        var sealedId = (await AddOptionFieldAsync(s, formId, "sealed_tags", "Sealed topics", "Multiple", ["north", "south"], classification: "Confidential")).Root.GetProperty("fieldDefinitionId").GetGuid();
        var noteId = (await fixture.Api.PostAsync($"/api/v1/form-definitions/{formId:D}/fields", s.Token(s.Designer), new { fieldKey = "plain_count", fieldDisplayName = "Count", fieldDescription = string.Empty, fieldDataType = "Integer", options = (object?)null })).Root.GetProperty("fieldDefinitionId").GetGuid();
        await s.SaveDraftAsync(formId, StageBScenario.Layout("Shared choices", StageBScenario.Section("main", "Main",
            Element(priorityId, "select", "Priority"), Element(tagsId, "multiselect", "Topics"), Element(sealedId, "multiselect", "Sealed topics"), Element(noteId, "number", "Count"))));
        await s.PublishAsync(formId, "shares");
        var record = (await fixture.Api.PostAsync($"/api/v1/form-definitions/{formId:D}/records", s.Token(s.Clerk), new { values = new Dictionary<string, string> { ["priority_level"] = "high", ["topic_tags"] = "red,blue", ["sealed_tags"] = "north,south", ["plain_count"] = "7" }, ownerTeamId = (Guid?)null })).Root.GetProperty("formRecordId").GetGuid();
        await s.WaitProjectionAsync(s.Clerk, record);

        // Sharing a multi-select field shares every current selection of it; the plaintext dropdown and the encrypted one behave alike.
        var share = await fixture.Api.PostAsync($"/api/v1/form-records/{record:D}/shares", s.Token(s.Clerk), new { recipientUserId = s.Reader.AuthorityUserId, recipientTeamId = (Guid?)null, recipientRole = "Reader", fieldKeys = new[] { "topic_tags", "sealed_tags", "priority_level" } });
        Assert.True(share.IsSuccess, share.Body);
        var shared = await s.WaitProjectionAsync(s.Reader, record, TimeSpan.FromSeconds(40));
        Assert.False(shared.Root.GetProperty("permissions").GetProperty("isCompleteProjection").GetBoolean());
        Assert.Equal(["blue", "red"], Keys(ValuesFor(shared.Root, "topic_tags")));
        Assert.Equal(["north", "south"], Keys(ValuesFor(shared.Root, "sealed_tags")));
        Assert.Equal(["high"], Keys(ValuesFor(shared.Root, "priority_level")));
        Assert.Empty(ValuesFor(shared.Root, "plain_count")); // never shared, never released
        var layoutJson = shared.Root.GetProperty("layoutVersion").GetProperty("layoutJson").GetString()!;
        Assert.DoesNotContain(noteId.ToString(), layoutJson, StringComparison.Ordinal);

        // A shared reader cannot change anything; a field-scoped contributor cannot change a multi-selection (adding and removing rows needs record ownership).
        Assert.Contains((await fixture.Api.PatchAsync($"/api/v1/form-records/{record:D}", s.Token(s.Reader), new { expectedContentRevision = shared.Root.GetProperty("record").GetProperty("contentRevision").GetInt64(), values = new Dictionary<string, string> { ["topic_tags"] = "green" } })).Status, Denied);
        var contributor = await fixture.Api.PostAsync($"/api/v1/form-records/{record:D}/shares", s.Token(s.Clerk), new { recipientUserId = s.Designer.AuthorityUserId, recipientTeamId = (Guid?)null, recipientRole = "Contributor", fieldKeys = new[] { "topic_tags" } });
        Assert.True(contributor.IsSuccess, contributor.Body);
        var forContributor = await s.WaitProjectionAsync(s.Designer, record, TimeSpan.FromSeconds(40));
        var change = await fixture.Api.PatchAsync($"/api/v1/form-records/{record:D}", s.Token(s.Designer), new { expectedContentRevision = forContributor.Root.GetProperty("record").GetProperty("contentRevision").GetInt64(), values = new Dictionary<string, string> { ["topic_tags"] = "green" } });
        Assert.Equal(HttpStatusCode.Forbidden, change.Status);
        Assert.Equal(["blue", "red"], Keys(ValuesFor((await s.WaitProjectionAsync(s.Clerk, record)).Root, "topic_tags")));

        // Revocation removes the reader's access to every selection at once.
        var shareId = share.Root.GetProperty("recordShareId").GetGuid();
        var revoked = await fixture.Api.PostAsync($"/api/v1/form-records/{record:D}/shares/{shareId:D}/revoke", s.Token(s.Clerk), new { });
        Assert.True(revoked.IsSuccess, revoked.Body);
        var deadline = DateTime.UtcNow.AddSeconds(40);
        HttpResult after;
        do
        {
            after = await s.ProjectionAsync(s.Reader, record);
            if (Denied.Contains(after.Status))
            {
                break;
            }

            await Task.Delay(250, TestContext.Current.CancellationToken);
        }
        while (DateTime.UtcNow < deadline);
        Assert.Contains(after.Status, Denied);
    }

    [Fact]
    public async Task SharedOptionListsAreCopiedIntoFieldsAndReachThemOnlyThroughAnExplicitSync()
    {
        var s = new StageBScenario(fixture, "option-lists");
        await s.BootstrapAsync();

        // Only designers manage lists; the response is revision-guarded and keys are permanent.
        Assert.Contains((await fixture.Api.GetAsync("/api/v1/option-lists", s.Token(s.Clerk))).Status, Denied);
        var invalid = await fixture.Api.PostAsync("/api/v1/option-lists", s.Token(s.Designer), new { optionListName = "Bad", optionListDescription = (string?)null, items = new[] { new { optionKey = "Not Snake", optionDisplayText = "x" } } });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, invalid.Status);
        var created = await fixture.Api.PostAsync("/api/v1/option-lists", s.Token(s.Designer), new { optionListName = "Court outcomes", optionListDescription = "Shared across forms", items = new[] { new { optionKey = "convicted", optionDisplayText = "Convicted" }, new { optionKey = "acquitted", optionDisplayText = "Acquitted" }, new { optionKey = "stayed", optionDisplayText = "Stayed" } } });
        Assert.Equal(HttpStatusCode.Created, created.Status);
        var listId = created.Root.GetProperty("optionListId").GetGuid();
        Assert.Equal(1, created.Root.GetProperty("optionListRevision").GetInt64());
        Assert.Equal("OPTION_LIST_EXISTS", (await fixture.Api.PostAsync("/api/v1/option-lists", s.Token(s.Designer), new { optionListName = "court outcomes", optionListDescription = (string?)null, items = new[] { new { optionKey = "a", optionDisplayText = "A" } } })).ErrorCode);

        // Two forms share the list; each field gets its own copy with stable ids.
        var formA = await s.CreateFormAsync("Sentencing " + s.Name, s.Clerk);
        var formB = await s.CreateFormAsync("Appeals " + s.Name, s.Clerk);
        var fieldA = await AddOptionFieldAsync(s, formA, "outcome", "Outcome", "Single", [], optionListId: listId);
        var fieldB = await AddOptionFieldAsync(s, formB, "outcome", "Outcome", "Multiple", [], optionListId: listId);
        Assert.Equal(HttpStatusCode.Created, fieldA.Status);
        Assert.Equal(HttpStatusCode.Created, fieldB.Status);
        Assert.Equal(listId, fieldA.Root.GetProperty("optionListId").GetGuid());
        var aOptions = fieldA.Root.GetProperty("options").EnumerateArray().ToList();
        Assert.Equal(["convicted", "acquitted", "stayed"], aOptions.Select(o => o.GetProperty("optionKey").GetString()).ToArray());
        Assert.All(aOptions, o => Assert.NotEqual(JsonValueKind.Null, o.GetProperty("optionListItemId").ValueKind));
        var aId = fieldA.Root.GetProperty("fieldDefinitionId").GetGuid();
        var bId = fieldB.Root.GetProperty("fieldDefinitionId").GetGuid();
        await s.SaveDraftAsync(formA, StageBScenario.Layout("Sentencing", StageBScenario.Section("main", "Main", Element(aId, "select", "Outcome"))));
        await s.PublishAsync(formA, "uses the shared list");
        var recordA = (await fixture.Api.PostAsync($"/api/v1/form-definitions/{formA:D}/records", s.Token(s.Clerk), new { values = new Dictionary<string, string> { ["outcome"] = "stayed" }, ownerTeamId = (Guid?)null })).Root.GetProperty("formRecordId").GetGuid();
        await s.WaitProjectionAsync(s.Clerk, recordA);

        // Editing the list (relabel, add, deactivate) bumps its revision and changes NO field, version or record.
        var items = created.Root.GetProperty("items").EnumerateArray().ToDictionary(i => i.GetProperty("optionKey").GetString()!, i => i.GetProperty("optionListItemId").GetGuid());
        var stale = await fixture.Api.SendAsync(HttpMethod.Put, $"/api/v1/option-lists/{listId:D}", s.Token(s.Designer), new { expectedOptionListRevision = 7, optionListName = (string?)null, optionListDescription = (string?)null, changes = Array.Empty<object>() }, null);
        Assert.Equal("OPTION_LIST_REVISION_CONFLICT", stale.ErrorCode);
        var renameKey = await fixture.Api.SendAsync(HttpMethod.Put, $"/api/v1/option-lists/{listId:D}", s.Token(s.Designer), new { expectedOptionListRevision = 1, optionListName = (string?)null, optionListDescription = (string?)null, changes = new object[] { new { optionListItemId = items["stayed"], optionKey = "stay_ordered", optionDisplayText = "Stayed", optionSortOrder = 2, isActive = true } } }, null);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, renameKey.Status);
        var edited = await fixture.Api.SendAsync(HttpMethod.Put, $"/api/v1/option-lists/{listId:D}", s.Token(s.Designer), new
        {
            expectedOptionListRevision = 1,
            optionListName = (string?)null,
            optionListDescription = (string?)null,
            changes = new object[]
            {
                new { optionListItemId = items["convicted"], optionKey = "convicted", optionDisplayText = "Convicted (all counts)", optionSortOrder = 0, isActive = true },
                new { optionListItemId = items["acquitted"], optionKey = "acquitted", optionDisplayText = "Acquitted", optionSortOrder = 1, isActive = false },
                new { optionListItemId = (Guid?)null, optionKey = "withdrawn", optionDisplayText = "Withdrawn", optionSortOrder = 3, isActive = true },
            },
        }, null);
        Assert.True(edited.IsSuccess, edited.Body);
        Assert.Equal(2, edited.Root.GetProperty("optionListRevision").GetInt64());
        Assert.Equal(2, edited.Root.GetProperty("boundFieldCount").GetInt32());

        var untouched = (await s.GetFormAsync(formA)).GetProperty("fields").EnumerateArray().Single(f => f.GetProperty("fieldKey").GetString() == "outcome");
        Assert.Equal(["Convicted", "Acquitted", "Stayed"], untouched.GetProperty("options").EnumerateArray().Select(o => o.GetProperty("optionDisplayText").GetString()).ToArray());
        Assert.All(untouched.GetProperty("options").EnumerateArray(), o => Assert.True(o.GetProperty("isActive").GetBoolean()));

        // A list-managed label cannot be edited in the field; the sync pulls labels, new items and deactivations — ids and keys never change and the record keeps its value.
        var localRelabel = await fixture.Api.SendAsync(HttpMethod.Put, $"/api/v1/form-definitions/{formA:D}/fields/{aId:D}/options", s.Token(s.Designer), new { changes = new object[] { new { fieldOptionId = aOptions[0].GetProperty("fieldOptionId").GetGuid(), optionKey = "convicted", optionDisplayText = "Local rename", optionSortOrder = 0, isActive = true } } }, null);
        Assert.Equal("OPTION_MANAGED_BY_LIST", localRelabel.ErrorCode);
        var synced = await fixture.Api.PostAsync($"/api/v1/form-definitions/{formA:D}/fields/{aId:D}/options/sync", s.Token(s.Designer), new { });
        Assert.True(synced.IsSuccess, synced.Body);
        var syncedOptions = synced.Root.GetProperty("options").EnumerateArray().ToDictionary(o => o.GetProperty("optionKey").GetString()!);
        Assert.Equal("Convicted (all counts)", syncedOptions["convicted"].GetProperty("optionDisplayText").GetString());
        Assert.False(syncedOptions["acquitted"].GetProperty("isActive").GetBoolean());
        Assert.True(syncedOptions.ContainsKey("withdrawn"));
        Assert.Equal(aOptions.Single(o => o.GetProperty("optionKey").GetString() == "stayed").GetProperty("fieldOptionId").GetGuid(), syncedOptions["stayed"].GetProperty("fieldOptionId").GetGuid());
        Assert.Equal("stayed", Keys(ValuesFor((await s.WaitProjectionAsync(s.Clerk, recordA)).Root, "outcome")).Single());
        // Form B, never synced, still has its copy; a field without a list cannot sync.
        var formBDetail = (await s.GetFormAsync(formB)).GetProperty("fields").EnumerateArray().Single(f => f.GetProperty("fieldKey").GetString() == "outcome");
        Assert.Equal("Convicted", formBDetail.GetProperty("options").EnumerateArray().First().GetProperty("optionDisplayText").GetString());
        var plainField = await AddOptionFieldAsync(s, formB, "plain_choice", "Plain", "Single", ["x", "y"]);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await fixture.Api.PostAsync($"/api/v1/form-definitions/{formB:D}/fields/{plainField.Root.GetProperty("fieldDefinitionId").GetGuid():D}/options/sync", s.Token(s.Designer), new { })).Status);
        Assert.Equal(bId, formBDetail.GetProperty("fieldDefinitionId").GetGuid());

        // Lists are organization-scoped: another organization neither sees nor binds them.
        var other = new StageBScenario(fixture, "option-lists-other");
        await other.BootstrapAsync();
        Assert.Empty((await fixture.Api.GetAsync("/api/v1/option-lists", other.Token(other.Designer))).Root.EnumerateArray());
        var otherForm = await other.CreateFormAsync("Foreign " + other.Name, other.Clerk);
        Assert.Equal(HttpStatusCode.NotFound, (await AddOptionFieldAsync(other, otherForm, "stolen", "Stolen", "Single", [], optionListId: listId)).Status);
        Assert.Contains((await fixture.Api.SendAsync(HttpMethod.Put, $"/api/v1/option-lists/{listId:D}", other.Token(other.Designer), new { expectedOptionListRevision = 2, optionListName = "Hijacked", optionListDescription = (string?)null, changes = Array.Empty<object>() }, null)).Status, Denied);
    }
}
