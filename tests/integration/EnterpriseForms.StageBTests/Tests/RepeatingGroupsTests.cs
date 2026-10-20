using System.Net;
using System.Text.Json;
using EnterpriseForms.SecurityTests.Harness;
using EnterpriseForms.StageBTests.Harness;
using Xunit;

namespace EnterpriseForms.StageBTests.Tests;

/// <summary>
/// Phase C3 — repeating groups and dependent dropdowns on the real stack (business API, independent authority, Vault
/// signing, PostgreSQL). Entries are signed RepeatedGroup items of the record's accepted manifest, so their identity, order
/// and membership are protected like every other value; the server enforces section settings, per-entry rules and
/// dependent-option validity; fields of repeating sections are not shareable on their own.
/// </summary>
[Collection(StageBStackCollectionDefinition.Name)]
public sealed class RepeatingGroupsTests(StageBFixture fixture)
{
    private sealed class Case
    {
        public Guid FormId { get; init; }

        public Guid GroupId { get; init; }

        public Guid Title { get; init; }

        public Guid Country { get; init; }

        public Guid Province { get; init; }

        public Guid Name { get; init; }

        public Guid Role { get; init; }

        public Guid Notes { get; init; }

        public Guid Region { get; init; }
    }

    private static object Eq(Guid field, string value) => new { op = "equals", fieldDefinitionId = field, value };

    private static string Element(Guid fieldId, string control, string label, bool required = false, Dictionary<string, object?>? extra = null)
    {
        var element = new Dictionary<string, object?> { ["kind"] = "field", ["fieldDefinitionId"] = fieldId, ["control"] = control, ["label"] = label, ["required"] = required, ["layout"] = new { columnSpan = 2, startOnNewRow = false }, ["validation"] = new { } };
        foreach (var (key, value) in extra ?? [])
        {
            element[key] = value;
        }

        return JsonSerializer.Serialize(element);
    }

    private static string RepeatingSection(string key, string title, Guid groupId, int min, int max, bool add, bool remove, bool reorder, params string[] elements) =>
        $$"""{"sectionKey":"{{key}}","title":"{{title}}","gridColumns":4,"repeat":{"groupDefinitionId":"{{groupId:D}}","minOccurrences":{{min}},"maxOccurrences":{{max}},"allowAdd":{{(add ? "true" : "false")}},"allowRemove":{{(remove ? "true" : "false")}},"allowReorder":{{(reorder ? "true" : "false")}}},"elements":[{{string.Join(",", elements)}}]}""";

    private static readonly Dictionary<string, string[]> ProvinceMap = new() { ["ontario"] = ["canada"], ["quebec"] = ["canada"], ["texas"] = ["usa"] };

    private async Task<Guid> AddAsync(StageBScenario s, Guid formId, string key, string name, string type, string[]? options = null, Guid? groupId = null)
    {
        var result = await fixture.Api.PostAsync($"/api/v1/form-definitions/{formId:D}/fields", s.Token(s.Designer), new { fieldKey = key, fieldDisplayName = name, fieldDescription = string.Empty, fieldDataType = type, options = options?.Select(o => new { optionKey = o, optionDisplayText = char.ToUpperInvariant(o[0]) + o[1..] }).ToArray(), groupDefinitionId = groupId });
        Assert.True(result.Status == HttpStatusCode.Created, result.Body);
        return result.Root.GetProperty("fieldDefinitionId").GetGuid();
    }

    private async Task<Case> BuildAsync(StageBScenario s, int min = 0, int max = 3, bool add = true, bool remove = true, bool reorder = true)
    {
        var formId = await s.CreateFormAsync("People " + s.Name, s.Clerk);
        var group = await fixture.Api.PostAsync($"/api/v1/form-definitions/{formId:D}/groups", s.Token(s.Designer), new { groupKey = "people", groupDisplayName = "People" });
        Assert.True(group.Status == HttpStatusCode.Created, group.Body);
        var groupId = group.Root.GetProperty("groupDefinitionId").GetGuid();
        var c = new Case
        {
            FormId = formId,
            GroupId = groupId,
            Title = await AddAsync(s, formId, "case_title", "Case title", "String"),
            Country = await AddAsync(s, formId, "country", "Country", "Option", ["canada", "usa"]),
            Province = await AddAsync(s, formId, "province", "Province", "Option", ["ontario", "quebec", "texas"]),
            Name = await AddAsync(s, formId, "person_name", "Name", "String", groupId: groupId), // free text: Confidential, application-encrypted by default
            Role = await AddAsync(s, formId, "person_role", "Role", "Option", ["witness", "suspect"], groupId),
            Notes = await AddAsync(s, formId, "person_notes", "Notes", "String", groupId: groupId),
            Region = await AddAsync(s, formId, "person_region", "Region", "Option", ["ontario", "texas"], groupId),
        };
        await s.SaveDraftAsync(formId, LayoutOf(c, min, max, add, remove, reorder));
        await s.PublishAsync(formId, "people section");
        return c;
    }

    private static string LayoutOf(Case c, int min, int max, bool add, bool remove, bool reorder) => StageBScenario.Layout("Case",
        StageBScenario.Section("main", "Case",
            Element(c.Title, "text", "Case title", required: true),
            Element(c.Country, "select", "Country"),
            Element(c.Province, "select", "Province", extra: new() { ["optionFilter"] = new { parentFieldDefinitionId = c.Country, parentOptionKeysByOption = ProvinceMap } })),
        RepeatingSection("people", "People", c.GroupId, min, max, add, remove, reorder,
            Element(c.Name, "text", "Name", required: true),
            Element(c.Role, "select", "Role"),
            Element(c.Notes, "text", "Notes", extra: new() { ["visibleWhen"] = Eq(c.Role, "suspect"), ["requiredWhen"] = Eq(c.Role, "suspect") }),
            Element(c.Region, "select", "Region", extra: new() { ["optionFilter"] = new { parentFieldDefinitionId = c.Country, parentOptionKeysByOption = new Dictionary<string, string[]> { ["ontario"] = ["canada"], ["texas"] = ["usa"] } } })));

    private static object Entry(Guid? id, params (string Key, string Value)[] values) => new { formRecordGroupId = id, values = values.ToDictionary(v => v.Key, v => v.Value) };

    private static object[] People(params object[] entries) => [new { groupKey = "people", occurrences = entries }];

    private Task<HttpResult> CreateAsync(StageBScenario s, Case c, Dictionary<string, string> values, object[]? groups) =>
        fixture.Api.PostAsync($"/api/v1/form-definitions/{c.FormId:D}/records", s.Token(s.Clerk), new { values, ownerTeamId = (Guid?)null, groups });

    private async Task<HttpResult> EditAsync(StageBScenario s, Guid record, Dictionary<string, string> values, object[]? groups, long? expectedRevision = null)
    {
        var revision = expectedRevision ?? (await s.WaitProjectionAsync(s.Clerk, record)).Root.GetProperty("record").GetProperty("contentRevision").GetInt64();
        return await fixture.Api.PatchAsync($"/api/v1/form-records/{record:D}", s.Token(s.Clerk), new { expectedContentRevision = revision, values, groups });
    }

    /// <summary>The record's entries in order, each with its values by field key.</summary>
    private static List<(Guid Id, Dictionary<string, string> Values)> Entries(JsonElement projectionRoot)
    {
        var record = projectionRoot.GetProperty("record");
        var occurrences = record.GetProperty("groups").EnumerateArray().Single(g => g.GetProperty("groupKey").GetString() == "people").GetProperty("occurrences").EnumerateArray().Select(o => o.GetProperty("formRecordGroupId").GetGuid()).ToList();
        var values = record.GetProperty("values").EnumerateArray().Where(v => v.TryGetProperty("formRecordGroupId", out var g) && g.ValueKind == JsonValueKind.String).ToList();
        return occurrences.Select(id => (id, values.Where(v => v.GetProperty("formRecordGroupId").GetGuid() == id).ToDictionary(v => v.GetProperty("fieldKey").GetString()!, v => v.GetProperty("stringValue").GetString()!))).ToList();
    }

    [Fact]
    public async Task EntriesKeepStableIdentitiesThroughEditsReorderRemoveAndAddAndAreSignedManifestItems()
    {
        var s = new StageBScenario(fixture, "groups-lifecycle");
        await s.BootstrapAsync();
        var c = await BuildAsync(s);

        var created = await CreateAsync(s, c, new() { ["case_title"] = "Case 1", ["country"] = "canada" }, People(Entry(null, ("person_name", "Ada Synthetic"), ("person_role", "witness")), Entry(null, ("person_name", "Bo Synthetic"), ("person_role", "suspect"), ("person_notes", "seen at noon"))));
        Assert.True(created.Status == HttpStatusCode.Created, created.Body);
        var record = created.Root.GetProperty("formRecordId").GetGuid();
        var first = Entries((await s.WaitProjectionAsync(s.Clerk, record)).Root);
        Assert.Equal(2, first.Count);
        Assert.Equal("Ada Synthetic", first[0].Values["person_name"]);
        Assert.Equal("seen at noon", first[1].Values["person_notes"]);
        var (ada, bo) = (first[0].Id, first[1].Id);

        // Two signed occurrence rows in the accepted operation; entry names are application-encrypted per entry (no plaintext copy).
        string Accepted(string table) => fixture.Stack.AttackerSql($"SELECT count(*) FROM {table} WHERE form_record_id = '{record:D}' AND content_change_operation_id = (SELECT accepted_content_change_operation_id FROM form_records WHERE form_record_id = '{record:D}');").Trim();
        Assert.Equal("2", Accepted("form_record_group_revisions"));
        Assert.Equal("0", fixture.Stack.AttackerSql($"SELECT count(*) FROM field_string_values WHERE form_record_id = '{record:D}';").Trim());
        // Ada's name, Bo's name and Bo's notes (text is Confidential + encrypted); the Role dropdown is Internal plaintext under the development policy.
        Assert.Equal("3", fixture.Stack.AttackerSql($"SELECT count(*) FROM field_encrypted_values WHERE form_record_id = '{record:D}' AND form_record_group_id IS NOT NULL AND content_change_operation_id = (SELECT accepted_content_change_operation_id FROM form_records WHERE form_record_id = '{record:D}');").Trim());
        Assert.DoesNotContain("Synthetic", fixture.Stack.AttackerSql($"SELECT encode(encrypted_value_bytes,'escape') FROM field_encrypted_values WHERE form_record_id = '{record:D}';"), StringComparison.Ordinal);

        // Reorder and edit: identities stay, values follow their entry.
        var reordered = await EditAsync(s, record, [], People(Entry(bo), Entry(ada, ("person_name", "Ada Renamed"))));
        Assert.True(reordered.IsSuccess, reordered.Body);
        var second = Entries((await s.WaitProjectionAsync(s.Clerk, record)).Root);
        Assert.Equal([bo, ada], second.Select(e => e.Id));
        Assert.Equal("Ada Renamed", second[1].Values["person_name"]);
        Assert.Equal("seen at noon", second[0].Values["person_notes"]);

        // Remove one, add one: the removed entry's values leave the accepted revision, the new entry gets a fresh identity.
        var replaced = await EditAsync(s, record, [], People(Entry(ada), Entry(null, ("person_name", "Cy Synthetic"))));
        Assert.True(replaced.IsSuccess, replaced.Body);
        var third = Entries((await s.WaitProjectionAsync(s.Clerk, record)).Root);
        Assert.Equal(2, third.Count);
        Assert.Equal(ada, third[0].Id);
        Assert.DoesNotContain(third, e => e.Id == bo);
        Assert.Equal("Cy Synthetic", third[1].Values["person_name"]);

        // The audit trail records the structure change (ids and counts, never values).
        var activity = await fixture.Api.GetAsync($"/api/v1/form-records/{record:D}/activity", s.Token(s.Clerk));
        Assert.True(activity.IsSuccess, activity.Body);
        Assert.DoesNotContain("Cy Synthetic", activity.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ForgedEntriesSettingsBoundsAndStaleRevisionsAreRefusedWithoutChangingTheRecord()
    {
        var s = new StageBScenario(fixture, "groups-refusals");
        await s.BootstrapAsync();
        var c = await BuildAsync(s, min: 1, max: 2, add: true, remove: false, reorder: false);
        var other = await BuildAsync(s, min: 0, max: 3);

        Assert.Equal("GROUP_OCCURRENCE_COUNT", (await CreateAsync(s, c, new() { ["case_title"] = "x" }, People())).ErrorCode);
        Assert.Equal("GROUP_OCCURRENCE_COUNT", (await CreateAsync(s, c, new() { ["case_title"] = "x" }, People(Entry(null, ("person_name", "A")), Entry(null, ("person_name", "B")), Entry(null, ("person_name", "C"))))).ErrorCode);
        var missing = await CreateAsync(s, c, new() { ["case_title"] = "x" }, People(Entry(null, ("person_role", "suspect"))));
        Assert.Equal("VALIDATION_FAILED", missing.ErrorCode);
        Assert.Contains("people[1].person_name", missing.Body, StringComparison.Ordinal);
        Assert.Contains("people[1].person_notes", missing.Body, StringComparison.Ordinal);
        Assert.Equal("INVALID_REQUEST", (await CreateAsync(s, c, new() { ["case_title"] = "x", ["person_name"] = "top-level" }, People(Entry(null, ("person_name", "A"))))).ErrorCode);
        Assert.Equal("FIELD_HIDDEN_BY_RULE", (await CreateAsync(s, c, new() { ["case_title"] = "x" }, People(Entry(null, ("person_name", "A"), ("person_role", "witness"), ("person_notes", "sneaky"))))).ErrorCode);

        var created = await CreateAsync(s, c, new() { ["case_title"] = "Case" }, People(Entry(null, ("person_name", "Ada")), Entry(null, ("person_name", "Bo"))));
        Assert.True(created.Status == HttpStatusCode.Created, created.Body);
        var record = created.Root.GetProperty("formRecordId").GetGuid();
        var projection = await s.WaitProjectionAsync(s.Clerk, record);
        var revision = projection.Root.GetProperty("record").GetProperty("contentRevision").GetInt64();
        var entries = Entries(projection.Root);

        var otherRecord = await CreateAsync(s, other, new() { ["case_title"] = "Other" }, People(Entry(null, ("person_name", "Zed"))));
        var foreignId = Entries((await s.WaitProjectionAsync(s.Clerk, otherRecord.Root.GetProperty("formRecordId").GetGuid())).Root)[0].Id;

        Assert.Equal("OCCURRENCE_UNKNOWN", (await EditAsync(s, record, [], People(Entry(entries[0].Id), Entry(Guid.CreateVersion7(), ("person_name", "Forged"))), revision)).ErrorCode);
        Assert.Equal("OCCURRENCE_UNKNOWN", (await EditAsync(s, record, [], People(Entry(entries[0].Id), Entry(foreignId, ("person_name", "Stolen"))), revision)).ErrorCode);
        Assert.Equal("INVALID_REQUEST", (await EditAsync(s, record, [], People(Entry(entries[0].Id), Entry(entries[0].Id)), revision)).ErrorCode);
        Assert.Equal("GROUP_REMOVE_NOT_ALLOWED", (await EditAsync(s, record, [], People(Entry(entries[0].Id)), revision)).ErrorCode);
        Assert.Equal("GROUP_REORDER_NOT_ALLOWED", (await EditAsync(s, record, [], People(Entry(entries[1].Id), Entry(entries[0].Id)), revision)).ErrorCode);
        Assert.Equal("GROUP_OCCURRENCE_COUNT", (await EditAsync(s, record, [], People(Entry(entries[0].Id), Entry(entries[1].Id), Entry(null, ("person_name", "Cy"))), revision)).ErrorCode);
        Assert.Equal("INVALID_REQUEST", (await EditAsync(s, record, [], [new { groupKey = "not_a_section", occurrences = Array.Empty<object>() }], revision)).ErrorCode);

        // Concurrent edits: the second writer with the same expected revision gets a conflict, nothing is lost silently.
        var winner = await EditAsync(s, record, [], People(Entry(entries[0].Id, ("person_name", "Ada One")), Entry(entries[1].Id)), revision);
        Assert.True(winner.IsSuccess, winner.Body);
        await s.WaitProjectionAsync(s.Clerk, record); // reads answer 503 until the winner's archive receipt exists
        var loser = await EditAsync(s, record, [], People(Entry(entries[0].Id, ("person_name", "Ada Two")), Entry(entries[1].Id)), revision);
        Assert.Equal(HttpStatusCode.Conflict, loser.Status);
        var after = Entries((await s.WaitProjectionAsync(s.Clerk, record)).Root);
        Assert.Equal("Ada One", after[0].Values["person_name"]);
        Assert.Equal([entries[0].Id, entries[1].Id], after.Select(e => e.Id));
    }

    [Fact]
    public async Task TamperingWithEntriesIsDetectedAndEntryFieldsCannotBeShared()
    {
        var s = new StageBScenario(fixture, "groups-tamper");
        await s.BootstrapAsync();
        var c = await BuildAsync(s);
        async Task<Guid> NewRecordAsync()
        {
            var created = await CreateAsync(s, c, new() { ["case_title"] = "Case", ["country"] = "canada" }, People(Entry(null, ("person_name", "Ada"), ("person_role", "witness")), Entry(null, ("person_name", "Bo"), ("person_role", "witness"))));
            Assert.True(created.Status == HttpStatusCode.Created, created.Body);
            var id = created.Root.GetProperty("formRecordId").GetGuid();
            await s.WaitProjectionAsync(s.Clerk, id);
            return id;
        }

        // Fields of repeating sections are refused for field shares (V1); record-level fields still share normally.
        var shareable = await NewRecordAsync();
        var refused = await fixture.Api.PostAsync($"/api/v1/form-records/{shareable:D}/shares", s.Token(s.Clerk), new { recipientUserId = s.Reader.AuthorityUserId, recipientTeamId = (Guid?)null, recipientRole = "Reader", fieldKeys = new[] { "person_role" } });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.Status);
        Assert.Equal("FIELD_IN_REPEATING_GROUP", refused.ErrorCode);
        var share = await fixture.Api.PostAsync($"/api/v1/form-records/{shareable:D}/shares", s.Token(s.Clerk), new { recipientUserId = s.Reader.AuthorityUserId, recipientTeamId = (Guid?)null, recipientRole = "Reader", fieldKeys = new[] { "country" } });
        Assert.True(share.IsSuccess, share.Body);
        var shared = await s.WaitProjectionAsync(s.Reader, shareable, TimeSpan.FromSeconds(40));
        Assert.Equal(["country"], shared.Root.GetProperty("record").GetProperty("values").EnumerateArray().Select(v => v.GetProperty("fieldKey").GetString()).ToArray());
        var partialLayout = shared.Root.GetProperty("layoutVersion").GetProperty("layoutJson").GetString()!;
        Assert.DoesNotContain("repeat", partialLayout, StringComparison.Ordinal);
        Assert.DoesNotContain("People", partialLayout, StringComparison.Ordinal);
        Assert.Equal(0, shared.Root.GetProperty("record").GetProperty("groups").GetArrayLength());

        // (a) Reordering entries directly in the database (signed sort order) is detected.
        var reorder = await NewRecordAsync();
        // A swap in two steps (the position index is unique per operation): 0,1 → 100,101 → 1,0.
        fixture.Stack.AttackerSql($"UPDATE form_record_group_revisions SET group_sort_order = group_sort_order + 100 WHERE form_record_id = '{reorder:D}'; UPDATE form_record_group_revisions SET group_sort_order = 101 - group_sort_order WHERE form_record_id = '{reorder:D}';");
        var read = await s.ProjectionAsync(s.Clerk, reorder);
        Assert.Equal(HttpStatusCode.Locked, read.Status);
        Assert.Equal("INTEGRITY_VERIFICATION_FAILED", read.ErrorCode);
        Assert.DoesNotContain("Ada", read.Body, StringComparison.Ordinal);

        // (b) Moving a value to the other entry is detected.
        var moved = await NewRecordAsync();
        fixture.Stack.AttackerSql($"UPDATE field_option_values v SET form_record_group_id = (SELECT g.form_record_group_id FROM form_record_group_revisions g WHERE g.form_record_id = v.form_record_id AND g.content_change_operation_id = v.content_change_operation_id AND g.form_record_group_id <> v.form_record_group_id LIMIT 1) WHERE v.form_record_id = '{moved:D}' AND v.form_record_group_id IS NOT NULL;");
        Assert.Equal(HttpStatusCode.Locked, (await s.ProjectionAsync(s.Clerk, moved)).Status);

        // (c) Deleting an entry row is detected (the accepted manifest is complete).
        var deleted = await NewRecordAsync();
        fixture.Stack.AttackerSql($"DELETE FROM form_record_group_revisions WHERE form_record_id = '{deleted:D}' AND group_sort_order = 1;");
        Assert.Equal(HttpStatusCode.Locked, (await s.ProjectionAsync(s.Clerk, deleted)).Status);
    }

    [Fact]
    public async Task DependentDropdownsAreEnforcedOnTheServerAndInvalidDesignsAreRefused()
    {
        var s = new StageBScenario(fixture, "groups-dependent");
        await s.BootstrapAsync();
        var c = await BuildAsync(s);

        var invalid = await CreateAsync(s, c, new() { ["case_title"] = "x", ["country"] = "usa", ["province"] = "ontario" }, null);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, invalid.Status);
        Assert.Equal("DEPENDENT_OPTION_INVALID", invalid.ErrorCode);
        var inEntry = await CreateAsync(s, c, new() { ["case_title"] = "x", ["country"] = "usa" }, People(Entry(null, ("person_name", "A"), ("person_region", "ontario"))));
        Assert.Equal("DEPENDENT_OPTION_INVALID", inEntry.ErrorCode);
        Assert.Contains("people[1].person_region", inEntry.Body, StringComparison.Ordinal);

        var created = await CreateAsync(s, c, new() { ["case_title"] = "x", ["country"] = "canada", ["province"] = "ontario" }, People(Entry(null, ("person_name", "A"), ("person_region", "ontario"))));
        Assert.True(created.Status == HttpStatusCode.Created, created.Body);
        var record = created.Root.GetProperty("formRecordId").GetGuid();
        // Changing the parent alone leaves invalid children: the server refuses rather than clearing anything.
        var parentOnly = await EditAsync(s, record, new() { ["country"] = "usa" }, null);
        Assert.Equal("DEPENDENT_OPTION_INVALID", parentOnly.ErrorCode);
        var entryId = Entries((await s.WaitProjectionAsync(s.Clerk, record)).Root)[0].Id;
        var fixedAll = await EditAsync(s, record, new() { ["country"] = "usa", ["province"] = "texas" }, People(Entry(entryId, ("person_region", "texas"))));
        Assert.True(fixedAll.IsSuccess, fixedAll.Body);

        // Design-time: circular dependencies, cross-scope parents and unknown keys are refused when the draft is saved.
        async Task Rejects(string fragment, string layout)
        {
            var draft = await fixture.Api.SendAsync(HttpMethod.Put, $"/api/v1/form-definitions/{c.FormId:D}/draft", s.Token(s.Designer), new { expectedDraftRevision = await s.CurrentDraftRevisionAsync(c.FormId), layoutJson = layout }, null);
            Assert.Equal(HttpStatusCode.UnprocessableEntity, draft.Status);
            Assert.Equal("LAYOUT_INVALID", draft.ErrorCode);
            Assert.Contains(fragment, draft.Body, StringComparison.OrdinalIgnoreCase);
        }

        await Rejects("circular dropdown dependency", StageBScenario.Layout("x", StageBScenario.Section("main", "Main",
            Element(c.Country, "select", "Country", extra: new() { ["optionFilter"] = new { parentFieldDefinitionId = c.Province, parentOptionKeysByOption = new Dictionary<string, string[]>() } }),
            Element(c.Province, "select", "Province", extra: new() { ["optionFilter"] = new { parentFieldDefinitionId = c.Country, parentOptionKeysByOption = ProvinceMap } }))));
        await Rejects("is not an option", StageBScenario.Layout("x", StageBScenario.Section("main", "Main",
            Element(c.Country, "select", "Country"),
            Element(c.Province, "select", "Province", extra: new() { ["optionFilter"] = new { parentFieldDefinitionId = c.Country, parentOptionKeysByOption = new Dictionary<string, string[]> { ["ontario"] = ["mars"] } } }))));
        await Rejects("same repeating section", StageBScenario.Layout("x",
            StageBScenario.Section("main", "Main", Element(c.Province, "select", "Province", extra: new() { ["optionFilter"] = new { parentFieldDefinitionId = c.Role, parentOptionKeysByOption = new Dictionary<string, string[]>() } })),
            RepeatingSection("people", "People", c.GroupId, 0, 3, true, true, true, Element(c.Role, "select", "Role"))));
        await Rejects("can only be placed in that section", StageBScenario.Layout("x", StageBScenario.Section("main", "Main", Element(c.Name, "text", "Name"))));
        await Rejects("one value per record", StageBScenario.Layout("x", StageBScenario.Section("main", "Main"), RepeatingSection("people", "People", c.GroupId, 0, 3, true, true, true, Element(c.Title, "text", "Title"))));
        await Rejects("one value per entry", StageBScenario.Layout("x", StageBScenario.Section("main", "Main", Element(c.Title, "text", "Title", extra: new() { ["visibleWhen"] = Eq(c.Role, "suspect") })), RepeatingSection("people", "People", c.GroupId, 0, 3, true, true, true, Element(c.Role, "select", "Role"))));
        await Rejects("does not belong", StageBScenario.Layout("x", RepeatingSection("ghost", "Ghost", Guid.CreateVersion7(), 0, 3, true, true, true)));
        await Rejects("repeating", StageBScenario.Layout("x", RepeatingSection("people", "People", c.GroupId, 2, 1, true, true, true)));

        // Grouped fields are created for an existing group only, never for documents.
        var documents = await fixture.Api.PostAsync($"/api/v1/form-definitions/{c.FormId:D}/fields", s.Token(s.Designer), new { fieldKey = "entry_files", fieldDisplayName = "Files", fieldDescription = string.Empty, fieldDataType = "File", groupDefinitionId = c.GroupId });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, documents.Status);
        var ghostGroup = await fixture.Api.PostAsync($"/api/v1/form-definitions/{c.FormId:D}/fields", s.Token(s.Designer), new { fieldKey = "ghost_field", fieldDisplayName = "Ghost", fieldDescription = string.Empty, fieldDataType = "String", groupDefinitionId = Guid.CreateVersion7() });
        Assert.Equal(HttpStatusCode.NotFound, ghostGroup.Status);
        var duplicateGroup = await fixture.Api.PostAsync($"/api/v1/form-definitions/{c.FormId:D}/groups", s.Token(s.Designer), new { groupKey = "people", groupDisplayName = "Again" });
        Assert.Equal(HttpStatusCode.Conflict, duplicateGroup.Status);
        var clerkGroup = await fixture.Api.PostAsync($"/api/v1/form-definitions/{c.FormId:D}/groups", s.Token(s.Clerk), new { groupKey = "clerk_group", groupDisplayName = "Clerk" });
        Assert.Contains(clerkGroup.Status, new[] { HttpStatusCode.Forbidden, HttpStatusCode.NotFound });
    }

    [Fact]
    public async Task RecordsCreatedBeforeASectionAllowedMultipleEntriesKeepWorkingAndOldVersionsStayUnchanged()
    {
        var s = new StageBScenario(fixture, "groups-versions");
        await s.BootstrapAsync();
        var formId = await s.CreateFormAsync("Versions " + s.Name, s.Clerk);
        var title = await AddAsync(s, formId, "case_title", "Case title", "String");
        await s.SaveDraftAsync(formId, StageBScenario.Layout("Case", StageBScenario.Section("main", "Case", Element(title, "text", "Case title", required: true))));
        await s.PublishAsync(formId, "v1 without entries");
        var v1 = (await s.GetFormAsync(formId)).GetProperty("currentVersion").GetProperty("layoutJson").GetString()!;
        Assert.DoesNotContain("repeat", v1, StringComparison.Ordinal);
        var old = await fixture.Api.PostAsync($"/api/v1/form-definitions/{formId:D}/records", s.Token(s.Clerk), new { values = new Dictionary<string, string> { ["case_title"] = "Old record" }, ownerTeamId = (Guid?)null });
        Assert.True(old.Status == HttpStatusCode.Created, old.Body);
        var oldRecord = old.Root.GetProperty("formRecordId").GetGuid();

        var group = (await fixture.Api.PostAsync($"/api/v1/form-definitions/{formId:D}/groups", s.Token(s.Designer), new { groupKey = "people", groupDisplayName = "People" })).Root.GetProperty("groupDefinitionId").GetGuid();
        var name = await AddAsync(s, formId, "person_name", "Name", "String", groupId: group);
        await s.SaveDraftAsync(formId, StageBScenario.Layout("Case", StageBScenario.Section("main", "Case", Element(title, "text", "Case title", required: true)), RepeatingSection("people", "People", group, 0, 5, true, true, true, Element(name, "text", "Name", required: true))));
        await s.PublishAsync(formId, "v2 adds people");

        // The old record has no entries; it shows an empty section and can be edited — with or without adding entries.
        var projection = (await s.WaitProjectionAsync(s.Clerk, oldRecord)).Root;
        Assert.Empty(Entries(projection));
        Assert.True((await EditAsync(s, oldRecord, new() { ["case_title"] = "Old record, edited" }, null)).IsSuccess);
        Assert.True((await EditAsync(s, oldRecord, [], People(Entry(null, ("person_name", "Late Entry"))))).IsSuccess);
        Assert.Equal("Late Entry", Entries((await s.WaitProjectionAsync(s.Clerk, oldRecord)).Root)[0].Values["person_name"]);

        var versions = (await fixture.Api.GetAsync($"/api/v1/form-definitions/{formId:D}/versions", s.Token(s.Designer))).Root.EnumerateArray().ToList();
        Assert.Equal(v1, versions.Single(v => v.GetProperty("versionNumber").GetInt32() == 1).GetProperty("layoutJson").GetString());
    }
}
