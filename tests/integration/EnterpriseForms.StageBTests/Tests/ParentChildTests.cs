using System.Net;
using EnterpriseForms.StageBTests.Harness;
using Xunit;

namespace EnterpriseForms.StageBTests.Tests;

/// <summary>Accused → AccusedAddress child records through the relationship model (requirement 6; B-04).</summary>
[Collection(StageBStackCollectionDefinition.Name)]
public sealed class ParentChildTests(StageBFixture fixture)
{
    [Fact]
    public async Task MultipleAddressesAreIndependentChildRecordsOwnedThroughTheParent()
    {
        var s = new StageBScenario(fixture, "parent-child");
        await s.BootstrapAsync();
        await s.BuildAccusedWorkspaceAsync();
        var accused = await s.CreateAccusedAsync(s.Clerk);

        var home = await s.CreateAddressAsync(s.Clerk, accused, "12 King Street W", "Toronto", "home");
        var work = await s.CreateAddressAsync(s.Clerk, accused, "100 Queen Street W", "Toronto", "work");

        var list = await s.ChildrenAsync(s.Clerk, accused);
        Assert.True(list.IsSuccess, list.Body);
        var items = list.Root.GetProperty("items").EnumerateArray().ToList();
        Assert.Equal(2, items.Count);
        Assert.All(items, item => Assert.Equal("Verified", item.GetProperty("integrityStatus").GetString()));
        Assert.Equal([1L, 2L], items.Select(i => i.GetProperty("listSortOrder").GetInt64()).ToArray());
        Assert.Contains(items, i => i.GetProperty("summaryValues").EnumerateArray().Any(v => v.GetProperty("stringValue").GetString() == "12 King Street W"));

        // The child is a full record: its projection carries the parent link and is editable by the parent's owner.
        var child = await s.WaitProjectionAsync(s.Clerk, home);
        Assert.Equal(accused, child.Root.GetProperty("parentFormRecordId").GetGuid());
        Assert.Equal(s.RelationshipId, child.Root.GetProperty("formRelationshipDefinitionId").GetGuid());
        Assert.True(child.Root.GetProperty("permissions").GetProperty("canEdit").GetBoolean());
        var edit = await fixture.Api.PatchAsync($"/api/v1/form-records/{home:D}", s.Token(s.Clerk), new { expectedContentRevision = child.Root.GetProperty("record").GetProperty("contentRevision").GetInt64(), values = new Dictionary<string, string> { ["city_name"] = "Mississauga" } });
        Assert.True(edit.IsSuccess, edit.Body);

        // Parent projection keeps the relationship metadata; list includes children only when asked.
        var parent = await s.WaitProjectionAsync(s.Clerk, accused);
        Assert.Single(parent.Root.GetProperty("relationships").EnumerateArray());
        var topLevel = await fixture.Api.GetAsync("/api/v1/form-records?pageSize=50", s.Token(s.Clerk));
        Assert.DoesNotContain(topLevel.Root.GetProperty("items").EnumerateArray(), i => i.GetProperty("formRecordId").GetGuid() == home);
        var withChildren = await fixture.Api.GetAsync("/api/v1/form-records?includeChildren=true&pageSize=50", s.Token(s.Clerk));
        Assert.Contains(withChildren.Root.GetProperty("items").EnumerateArray(), i => i.GetProperty("formRecordId").GetGuid() == home && i.GetProperty("parentFormRecordId").GetGuid() == accused);

        // Unlink preserves the child; the list shrinks; the capacity bound is enforced.
        var unlink = await fixture.Api.PostAsync($"/api/v1/form-records/{accused:D}/related/{work:D}/unlink", s.Token(s.Clerk), new { removalReason = "moved out" });
        Assert.True(unlink.IsSuccess, unlink.Body);
        var remaining = (await s.ChildrenAsync(s.Clerk, accused)).Root.GetProperty("items").EnumerateArray().ToList();
        Assert.Single(remaining);
        Assert.Equal(home, remaining[0].GetProperty("formRecordId").GetGuid());
        var unlinked = await s.WaitProjectionAsync(s.Clerk, work);
        Assert.Equal("Verified", unlinked.Root.GetProperty("record").GetProperty("integrityStatus").GetString());

        for (var i = 0; i < 9; i++)
        {
            await s.CreateAddressAsync(s.Clerk, accused, $"{i} Capacity Lane");
        }

        var overflow = await fixture.Api.PostAsync($"/api/v1/form-records/{accused:D}/related", s.Token(s.Clerk), new { formRelationshipDefinitionId = s.RelationshipId, values = new Dictionary<string, string> { ["street_address"] = "one too many" } });
        Assert.Equal(HttpStatusCode.Conflict, overflow.Status);
        Assert.Equal("RELATIONSHIP_CAPACITY_EXCEEDED", overflow.ErrorCode);
    }

    [Fact]
    public async Task ChildAccessFollowsTheAuthorityNotTheDatabaseLink()
    {
        var s = new StageBScenario(fixture, "child-authz");
        await s.BootstrapAsync();
        await s.BuildAccusedWorkspaceAsync();
        var accused = await s.CreateAccusedAsync(s.Clerk);
        var home = await s.CreateAddressAsync(s.Clerk, accused, "1 Private Way");

        // A member with no grant discovers nothing and cannot read parent, child or the list.
        var readerList = await fixture.Api.GetAsync("/api/v1/form-records?includeChildren=true&pageSize=50", s.Token(s.Reader));
        Assert.True(readerList.IsSuccess, readerList.Body);
        Assert.Empty(readerList.Root.GetProperty("items").EnumerateArray());
        Assert.True((await s.ProjectionAsync(s.Reader, accused)).Status is HttpStatusCode.Forbidden or HttpStatusCode.NotFound);
        Assert.True((await s.ProjectionAsync(s.Reader, home)).Status is HttpStatusCode.Forbidden or HttpStatusCode.NotFound);
        Assert.True((await s.ChildrenAsync(s.Reader, accused)).Status is HttpStatusCode.Forbidden or HttpStatusCode.NotFound);
        var readerCreate = await fixture.Api.PostAsync($"/api/v1/form-records/{accused:D}/related", s.Token(s.Reader), new { formRelationshipDefinitionId = s.RelationshipId, values = new Dictionary<string, string> { ["street_address"] = "forged" } });
        Assert.True(readerCreate.Status is HttpStatusCode.Forbidden or HttpStatusCode.NotFound, readerCreate.Body);

        // Sharing the parent's fields does not expose the child (no automatic child access, CLAUDE.md §4S.4).
        var share = await fixture.Api.PostAsync($"/api/v1/form-records/{accused:D}/shares", s.Token(s.Clerk), new { recipientUserId = s.Reader.AuthorityUserId, recipientTeamId = (Guid?)null, recipientRole = "Reader", fieldKeys = new[] { "given_name", "family_name" } });
        Assert.True(share.IsSuccess, share.Body);
        var parentForReader = await s.WaitProjectionAsync(s.Reader, accused, TimeSpan.FromSeconds(40));
        Assert.False(parentForReader.Root.GetProperty("permissions").GetProperty("isCompleteProjection").GetBoolean());
        Assert.DoesNotContain(parentForReader.Root.GetProperty("record").GetProperty("values").EnumerateArray(), v => v.GetProperty("fieldKey").GetString() == "date_of_birth");
        Assert.True((await s.ProjectionAsync(s.Reader, home)).Status is HttpStatusCode.Forbidden or HttpStatusCode.NotFound);

        // A forged link row written by the hostile DBA never surfaces: the list is driven by the authority's accepted links.
        var forgedChild = await s.CreateAccusedAsync(s.Clerk, StageBScenario.AccusedValues("Other", "Record"));
        var inserted = fixture.Stack.AttackerSql($"INSERT INTO form_record_relationships (form_record_relationship_id, tenant_id, form_relationship_definition_id, parent_form_record_id, child_form_record_id, list_sort_order, link_lifecycle_state, authority_command_id, link_created_on, link_created_by_user_id, link_removed_on, link_removed_by_user_id) SELECT gen_random_uuid(), tenant_id, form_relationship_definition_id, parent_form_record_id, '{forgedChild:D}', 99, link_lifecycle_state, NULL, now(), link_created_by_user_id, NULL, NULL FROM form_record_relationships WHERE child_form_record_id = '{home:D}' LIMIT 1; SELECT count(*) FROM form_record_relationships WHERE child_form_record_id = '{forgedChild:D}';", throwOnError: false);
        var listAfter = await s.ChildrenAsync(s.Clerk, accused);
        if (listAfter.IsSuccess)
        {
            Assert.DoesNotContain(listAfter.Root.GetProperty("items").EnumerateArray(), i => i.GetProperty("formRecordId").GetGuid() == forgedChild);
        }
        else
        {
            // Fail-closed is also acceptable: a tampered link table must never widen the list.
            Assert.True(listAfter.Status is HttpStatusCode.Locked or HttpStatusCode.Conflict, listAfter.Body + " / insert: " + inserted);
        }
    }
}
