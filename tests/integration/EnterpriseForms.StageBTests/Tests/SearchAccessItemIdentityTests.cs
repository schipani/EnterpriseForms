using System.Net;
using System.Text.Json;
using EnterpriseForms.SecurityTests.Harness;
using EnterpriseForms.StageBTests.Harness;
using Xunit;

namespace EnterpriseForms.StageBTests.Tests;

/// <summary>
/// IX-5a first gate (C-147 §2, TQ-6): the field-item identities that Tier 2 access rows are keyed by. On the real stack, through
/// the business API and the Authority's accepted manifests:
/// <list type="bullet">
/// <item>an edited scalar or single selection keeps its item id across accepted content revisions;</item>
/// <item>a multi-select keeps the id of each retained option; a newly selected option, and a value entered after a clear, get new
/// ids that no existing share covers;</item>
/// <item>repeating-group entries and their values keep their ids through edits; a new entry gets new ids;</item>
/// <item>direct and team shares of the same fields cover the same items;</item>
/// <item>an ownership transfer changes no share edge and grants no item.</item>
/// </list>
/// Synthetic data only. A focused development test class (C-131), not Stage B.
/// </summary>
[Collection(StageBStackCollectionDefinition.Name)]
public sealed class SearchAccessItemIdentityTests(StageBFixture fixture)
{
    private sealed record Ws(StageBScenario S, Guid FormId, Guid GroupId);

    private sealed record Item(string FieldKey, Guid? GroupEntryId, Guid ItemId, string Value);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string Sql(string sql) => fixture.Stack.AuthorityObserverSql(sql).Trim();

    private async Task<Guid> AddFieldAsync(StageBScenario s, Guid formId, string key, string type, string[]? options = null, Guid? groupId = null, string? mode = null)
    {
        var result = await fixture.Api.PostAsync($"/api/v1/form-definitions/{formId:D}/fields", s.Token(s.Designer), new { fieldKey = key, fieldDisplayName = key, fieldDescription = string.Empty, fieldDataType = type, options = options?.Select(o => new { optionKey = o, optionDisplayText = o.ToUpperInvariant() }).ToArray(), groupDefinitionId = groupId, optionSelectionMode = mode });
        Assert.True(result.Status == HttpStatusCode.Created, result.Body);
        return result.Root.GetProperty("fieldDefinitionId").GetGuid();
    }

    private static string Element(Guid fieldId, string control, string label) =>
        JsonSerializer.Serialize(new Dictionary<string, object?> { ["kind"] = "field", ["fieldDefinitionId"] = fieldId, ["control"] = control, ["label"] = label, ["required"] = false, ["layout"] = new { columnSpan = 2, startOnNewRow = false }, ["validation"] = new { } });

    private async Task<Ws> WorkspaceAsync(string name)
    {
        var s = new StageBScenario(fixture, "tq6-" + name);
        await s.BootstrapAsync();
        var formId = await s.CreateFormAsync("TQ-6 " + s.Name, s.Clerk);
        var group = await fixture.Api.PostAsync($"/api/v1/form-definitions/{formId:D}/groups", s.Token(s.Designer), new { groupKey = "people", groupDisplayName = "People" });
        Assert.True(group.Status == HttpStatusCode.Created, group.Body);
        var groupId = group.Root.GetProperty("groupDefinitionId").GetGuid();
        var title = await AddFieldAsync(s, formId, "case_title", "String");
        var count = await AddFieldAsync(s, formId, "exhibit_count", "Integer");
        var status = await AddFieldAsync(s, formId, "case_status", "Option", ["open", "closed"], mode: "Single");
        var tags = await AddFieldAsync(s, formId, "topic_tags", "Option", ["t01", "t02", "t03", "t04"], mode: "Multiple");
        var role = await AddFieldAsync(s, formId, "person_role", "Option", ["witness", "suspect"], groupId);
        var note = await AddFieldAsync(s, formId, "person_note", "String", groupId: groupId);
        await s.SaveDraftAsync(formId, StageBScenario.Layout("TQ-6",
            StageBScenario.Section("main", "Main", Element(title, "text", "Title"), Element(count, "number", "Count"), Element(status, "select", "Status"), Element(tags, "multiselect", "Topics")),
            $$"""{"sectionKey":"people","title":"People","gridColumns":4,"repeat":{"groupDefinitionId":"{{groupId:D}}","minOccurrences":0,"maxOccurrences":5,"allowAdd":true,"allowRemove":true,"allowReorder":true},"elements":[{{Element(role, "select", "Role")}},{{Element(note, "text", "Note")}}]}"""));
        await s.PublishAsync(formId, "tq6");
        return new Ws(s, formId, groupId);
    }

    private static object Entry(Guid? id, params (string Key, string Value)[] values) => new { formRecordGroupId = id, values = values.ToDictionary(v => v.Key, v => v.Value) };

    private static object[] People(params object[] entries) => [new { groupKey = "people", occurrences = entries }];

    private async Task<Guid> CreateAsync(Ws w, Dictionary<string, string> values, object[] groups)
    {
        for (var attempt = 0; ; attempt++)
        {
            var created = await fixture.Api.PostAsync($"/api/v1/form-definitions/{w.FormId:D}/records", w.S.Token(w.S.Clerk), new { values, ownerTeamId = (Guid?)null, groups }, "tq6-" + Guid.CreateVersion7().ToString("N"));
            if (created.Status is HttpStatusCode.Created or HttpStatusCode.OK)
            {
                var id = created.Root.GetProperty("formRecordId").GetGuid();
                await w.S.WaitProjectionAsync(w.S.Clerk, id);
                return id;
            }

            Assert.True(attempt < 60 && created.ErrorCode is "AUTHORITY_NOT_READY" or "STALE_AUTHORIZATION_REVISION" or "COMMAND_PENDING", "create failed: " + created.Body);
            await Task.Delay(400, Ct);
        }
    }

    private async Task EditAsync(Ws w, TestUser owner, Guid record, Dictionary<string, string> values, object[]? groups)
    {
        for (var attempt = 0; ; attempt++)
        {
            var revision = (await w.S.WaitProjectionAsync(owner, record)).Root.GetProperty("record").GetProperty("contentRevision").GetInt64();
            var edit = await fixture.Api.PatchAsync($"/api/v1/form-records/{record:D}", w.S.Token(owner), new { expectedContentRevision = revision, values, groups });
            if (edit.IsSuccess)
            {
                await w.S.WaitProjectionAsync(owner, record);
                return;
            }

            Assert.True(attempt < 30 && edit.ErrorCode is "AUTHORITY_NOT_READY" or "COMMAND_PENDING" or "CONTENT_REVISION_CONFLICT" or "STALE_CONTENT_REVISION", "edit failed: " + edit.Body);
            await Task.Delay(300, Ct);
        }
    }

    private async Task<Guid> ShareAsync(Ws w, TestUser owner, Guid record, Guid? user, Guid? team, params string[] fieldKeys)
    {
        for (var attempt = 0; ; attempt++)
        {
            var share = await fixture.Api.PostAsync($"/api/v1/form-records/{record:D}/shares", w.S.Token(owner), new { recipientUserId = user, recipientTeamId = team, recipientRole = "Reader", fieldKeys });
            if (share.IsSuccess)
            {
                await w.S.WaitGateReadyAsync();
                return share.Root.GetProperty("recordShareId").GetGuid();
            }

            Assert.True(attempt < 30 && share.ErrorCode is "AUTHORITY_NOT_READY" or "COMMAND_PENDING" or "STALE_AUTHORIZATION_REVISION", "share failed: " + share.Body);
            await Task.Delay(300, Ct);
        }
    }

    private async Task RevokeAsync(Ws w, Guid record, Guid share)
    {
        for (var attempt = 0; ; attempt++)
        {
            var revoked = await fixture.Api.PostAsync($"/api/v1/form-records/{record:D}/shares/{share:D}/revoke", w.S.Token(w.S.Clerk), new { });
            if (revoked.IsSuccess)
            {
                await w.S.WaitGateReadyAsync();
                return;
            }

            Assert.True(attempt < 30 && revoked.ErrorCode is "AUTHORITY_NOT_READY" or "COMMAND_PENDING" or "STALE_AUTHORIZATION_REVISION", "revoke failed: " + revoked.Body);
            await Task.Delay(300, Ct);
        }
    }

    private async Task<Guid> TeamAsync(Ws w, params TestUser[] members)
    {
        var team = await fixture.Api.PostAsync("/api/v1/teams", w.S.Token(w.S.Designer), new { teamName = "TQ-6 team " + Guid.NewGuid().ToString("N")[..8] });
        Assert.Equal(HttpStatusCode.Created, team.Status);
        var teamId = team.Root.GetProperty("teamId").GetGuid();
        foreach (var member in members)
        {
            await AddMemberAsync(w, teamId, member);
        }

        return teamId;
    }

    private async Task AddMemberAsync(Ws w, Guid teamId, TestUser member)
    {
        var added = await fixture.Api.PostAsync($"/api/v1/teams/{teamId:D}/members", w.S.Token(w.S.Designer), new { userId = member.AuthorityUserId }, "tq6-add-" + Guid.NewGuid().ToString("N"));
        Assert.True(added.IsSuccess, added.Body);
        await w.S.WaitGateReadyAsync();
    }

    /// <summary>The caller's authorized projection, item by item (multi-select values are one item per selected option).</summary>
    private static async Task<List<Item>> ItemsAsync(Ws w, TestUser user, Guid record)
    {
        var projection = await w.S.WaitProjectionAsync(user, record, TimeSpan.FromSeconds(40));
        return projection.Root.GetProperty("record").GetProperty("values").EnumerateArray().Select(v => new Item(
            v.GetProperty("fieldKey").GetString()!,
            v.TryGetProperty("formRecordGroupId", out var g) && g.ValueKind == JsonValueKind.String ? g.GetGuid() : null,
            v.GetProperty("fieldValueItemId").GetGuid(),
            v.GetProperty("stringValue").GetString()!)).ToList();
    }

    /// <summary>Field items (value and selection leaves) of the record's accepted manifest at its current head, from the Authority.</summary>
    private HashSet<Guid> AcceptedFieldItems(Guid record) => Sql(
            $"SELECT l.manifest_item_id FROM record_manifest_leaves l JOIN record_content_heads h ON h.current_record_manifest_commit_id = l.record_manifest_commit_id " +
            $"WHERE h.form_record_id = '{record:D}' AND l.manifest_item_kind_code IN (3, 4);")
        .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(Guid.Parse).ToHashSet();

    /// <summary>The live (item, share) edges of the record's shares, from the Authority's canonical edges.</summary>
    private string ShareItemEdges(Guid record) => Sql(
        $"SELECT string_agg(i.resource_entity_id || '>' || i.subject_entity_id, ',' ORDER BY i.resource_entity_id, i.subject_entity_id) FROM authority_relationship_edges i " +
        $"JOIN authority_relationship_edges r ON r.organization_id = i.organization_id AND r.resource_type_name = 'form_record' AND r.resource_entity_id = '{record:D}' AND r.relationship_name = 'shared_via' AND r.subject_entity_id = i.subject_entity_id AND r.removed_by_security_command_id IS NULL " +
        $"WHERE i.resource_type_name = 'field_item' AND i.relationship_name = 'shared_via' AND i.removed_by_security_command_id IS NULL;");

    private static Guid Id(List<Item> items, string key, string? value = null, Guid? entry = null) =>
        items.Single(i => i.FieldKey == key && (value is null || i.Value == value) && (entry is null || i.GroupEntryId == entry)).ItemId;

    [Fact]
    public async Task ItemIdentitiesAreStableAcrossAcceptedRevisionsAndNewItemsInheritNoShare()
    {
        var w = await WorkspaceAsync("stable");
        var s = w.S;
        var record = await CreateAsync(w,
            new() { ["case_title"] = "Alpha", ["exhibit_count"] = "1", ["case_status"] = "open", ["topic_tags"] = "t01,t02" },
            People(Entry(null, ("person_role", "witness"), ("person_note", "first")), Entry(null, ("person_role", "suspect"), ("person_note", "second"))));
        var v1 = await ItemsAsync(w, s.Clerk, record);
        Assert.True(v1.Select(i => i.ItemId).ToHashSet().SetEquals(AcceptedFieldItems(record)), "projection items differ from the accepted manifest at revision 1");
        var entries = v1.Where(i => i.GroupEntryId is not null).Select(i => i.GroupEntryId!.Value).Distinct().ToList();
        Assert.Equal(2, entries.Count);

        await ShareAsync(w, s.Clerk, record, s.Reader.AuthorityUserId, null, "exhibit_count", "topic_tags");
        var edgesBeforeEdit = ShareItemEdges(record);
        var readerBefore = await ItemsAsync(w, s.Reader, record);
        Assert.True(readerBefore.Select(i => i.ItemId).ToHashSet().SetEquals([Id(v1, "exhibit_count"), Id(v1, "topic_tags", "t01"), Id(v1, "topic_tags", "t02")]));

        // Revision 2: scalar, single selection, multi-select (keep t02, drop t01, add t03) and both entries edited, one entry added.
        await EditAsync(w, s.Clerk, record,
            new() { ["exhibit_count"] = "2", ["case_status"] = "closed", ["topic_tags"] = "t02,t03" },
            People(Entry(entries[0], ("person_role", "suspect"), ("person_note", "first edited")), Entry(entries[1], ("person_role", "suspect"), ("person_note", "second")), Entry(null, ("person_role", "witness"), ("person_note", "third"))));
        var v2 = await ItemsAsync(w, s.Clerk, record);
        Assert.True(v2.Select(i => i.ItemId).ToHashSet().SetEquals(AcceptedFieldItems(record)), "projection items differ from the accepted manifest at revision 2");
        Assert.Equal(Id(v1, "exhibit_count"), Id(v2, "exhibit_count"));
        Assert.Equal(Id(v1, "case_status"), Id(v2, "case_status"));
        Assert.Equal(Id(v1, "case_title"), Id(v2, "case_title"));
        Assert.Equal(Id(v1, "topic_tags", "t02"), Id(v2, "topic_tags", "t02"));
        var t03 = Id(v2, "topic_tags", "t03");
        Assert.DoesNotContain(t03, v1.Select(i => i.ItemId));
        Assert.DoesNotContain(v2, i => i.ItemId == Id(v1, "topic_tags", "t01"));
        foreach (var entry in entries)
        {
            Assert.Equal(Id(v1, "person_role", entry: entry), Id(v2, "person_role", entry: entry));
            Assert.Equal(Id(v1, "person_note", entry: entry), Id(v2, "person_note", entry: entry));
        }

        var third = v2.Select(i => i.GroupEntryId).Where(g => g is not null && !entries.Contains(g.Value)).Distinct().Single()!.Value;
        Assert.DoesNotContain(Id(v2, "person_role", entry: third), v1.Select(i => i.ItemId));

        // The share's edges are unchanged by the edit; the Reader keeps the edited retained items and gains no new item.
        Assert.Equal(edgesBeforeEdit, ShareItemEdges(record));
        var readerAfter = await ItemsAsync(w, s.Reader, record);
        Assert.True(readerAfter.Select(i => i.ItemId).ToHashSet().SetEquals([Id(v2, "exhibit_count"), Id(v2, "topic_tags", "t02")]), "reader items after edit: " + string.Join(",", readerAfter.Select(i => i.FieldKey + "=" + i.Value)));
        Assert.Equal("2", readerAfter.Single(i => i.FieldKey == "exhibit_count").Value);
        Assert.DoesNotContain(readerAfter, i => i.ItemId == t03 || i.GroupEntryId is not null);

        // Revision 3 and 4: clearing the count removes its item; a value entered afterwards is a new item that no share covers.
        await EditAsync(w, s.Clerk, record, new() { ["exhibit_count"] = string.Empty }, null);
        var v3 = await ItemsAsync(w, s.Clerk, record);
        Assert.DoesNotContain(v3, i => i.FieldKey == "exhibit_count");
        await EditAsync(w, s.Clerk, record, new() { ["exhibit_count"] = "5" }, null);
        var v4 = await ItemsAsync(w, s.Clerk, record);
        Assert.True(v4.Select(i => i.ItemId).ToHashSet().SetEquals(AcceptedFieldItems(record)));
        Assert.NotEqual(Id(v1, "exhibit_count"), Id(v4, "exhibit_count"));
        Assert.Equal(edgesBeforeEdit, ShareItemEdges(record));
        var readerFinal = await ItemsAsync(w, s.Reader, record);
        Assert.True(readerFinal.Select(i => i.ItemId).ToHashSet().SetEquals([Id(v4, "topic_tags", "t02")]), "reader items after re-entry: " + string.Join(",", readerFinal.Select(i => i.FieldKey + "=" + i.Value)));
    }

    [Fact]
    public async Task DirectAndTeamSharesCoverTheSameItemsAndOwnershipTransferGrantsNothing()
    {
        var w = await WorkspaceAsync("paths");
        var s = w.S;
        var record = await CreateAsync(w, new() { ["case_title"] = "Beta", ["exhibit_count"] = "3", ["case_status"] = "open", ["topic_tags"] = "t01,t04" }, People(Entry(null, ("person_role", "witness"), ("person_note", "n"))));
        var owner = await ItemsAsync(w, s.Clerk, record);
        var team = await TeamAsync(w, s.Administrator);
        var direct = await ShareAsync(w, s.Clerk, record, s.Reader.AuthorityUserId, null, "exhibit_count", "topic_tags");
        await ShareAsync(w, s.Clerk, record, null, team, "exhibit_count", "topic_tags");

        // Both shares carry exactly the same item set (the business API maps field keys to the accepted items).
        var pairs = ShareItemEdges(record).Split(',').Select(p => p.Split('>')).ToList();
        var byShare = pairs.GroupBy(p => p[1]).Select(g => g.Select(p => p[0]).Order().ToList()).ToList();
        Assert.Equal(2, byShare.Count);
        Assert.Equal(byShare[0], byShare[1]);
        var expected = new HashSet<Guid> { Id(owner, "exhibit_count"), Id(owner, "topic_tags", "t01"), Id(owner, "topic_tags", "t04") };
        Assert.True(byShare[0].Select(Guid.Parse).ToHashSet().SetEquals(expected));
        Assert.True((await ItemsAsync(w, s.Reader, record)).Select(i => i.ItemId).ToHashSet().SetEquals(expected));
        Assert.True((await ItemsAsync(w, s.Administrator, record)).Select(i => i.ItemId).ToHashSet().SetEquals(expected));

        // The direct path's revocation leaves the team path; joining the team restores the same items through it.
        await RevokeAsync(w, record, direct);
        Assert.True((await s.ProjectionAsync(s.Reader, record)).Status is HttpStatusCode.Forbidden or HttpStatusCode.NotFound);
        await AddMemberAsync(w, team, s.Reader);
        Assert.True((await ItemsAsync(w, s.Reader, record)).Select(i => i.ItemId).ToHashSet().SetEquals(expected));

        // Ownership transfer to another user: no share edge changes, the new owner reads every item, the previous owner reads none,
        // and the recipients' item sets are unchanged.
        var edgesBefore = ShareItemEdges(record);
        for (var attempt = 0; ; attempt++)
        {
            var transfer = await fixture.Api.PostAsync($"/api/v1/form-records/{record:D}/transfer-ownership", s.Token(s.Clerk), new { newOwnerUserId = s.Designer.AuthorityUserId, newOwnerTeamId = (Guid?)null, transferReason = "TQ-6 transfer" });
            if (transfer.IsSuccess)
            {
                break;
            }

            Assert.True(attempt < 30 && transfer.ErrorCode is "AUTHORITY_NOT_READY" or "COMMAND_PENDING" or "STALE_AUTHORIZATION_REVISION", "transfer failed: " + transfer.Body);
            await Task.Delay(300, Ct);
        }

        await s.WaitGateReadyAsync();
        Assert.Equal(edgesBefore, ShareItemEdges(record));
        Assert.True((await ItemsAsync(w, s.Designer, record)).Select(i => i.ItemId).ToHashSet().SetEquals(owner.Select(i => i.ItemId)));
        Assert.True((await s.ProjectionAsync(s.Clerk, record)).Status is HttpStatusCode.Forbidden or HttpStatusCode.NotFound);
        Assert.True((await ItemsAsync(w, s.Reader, record)).Select(i => i.ItemId).ToHashSet().SetEquals(expected));
        Assert.True((await ItemsAsync(w, s.Administrator, record)).Select(i => i.ItemId).ToHashSet().SetEquals(expected));

        // Repeating-group fields cannot be shared on their own (only ownership reads them).
        var refused = await fixture.Api.PostAsync($"/api/v1/form-records/{record:D}/shares", s.Token(s.Designer), new { recipientUserId = s.Reader.AuthorityUserId, recipientTeamId = (Guid?)null, recipientRole = "Reader", fieldKeys = new[] { "person_role" } });
        Assert.Equal("FIELD_IN_REPEATING_GROUP", refused.ErrorCode);
    }
}
