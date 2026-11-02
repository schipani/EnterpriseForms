using System.Globalization;
using System.Net;
using System.Text.Json;
using EnterpriseForms.SecurityTests.Harness;
using EnterpriseForms.StageBTests.Harness;
using Xunit;

namespace EnterpriseForms.StageBTests.Tests;

/// <summary>
/// Phase C8 M1 on the real stack (business API, Authorization Authority, SpiceDB, both PostgreSQL servers; nothing mocked):
/// the administration shell's reads (overview, people, team directory and detail), the display-only team rename (D8-8)
/// and the F-C7-03 logo audit. Every read is scoped to the session's organization and gated by the Authority's
/// manage_configuration or administer_security decision; everyone else gets a 403 without counts; another organization's
/// team id is the same generic 404 as an unknown id; team commands stay owner-only Authority commands.
/// </summary>
[Collection(StageBStackCollectionDefinition.Name)]
public sealed class AdministrationTests(StageBFixture fixture)
{
    private static readonly byte[] OnePixelPng = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNkYPhfDwAChwGA60e6kgAAAABJRU5ErkJggg==");

    private static readonly byte[] TwoPixelPng = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAIAAAABCAYAAACsXeyTAAAAD0lEQVR42mNk+M/wn4GBAQAIAwIBK2n2HAAAAABJRU5ErkJggg==");

    private async Task<StageBScenario> ScenarioAsync(string name)
    {
        var s = new StageBScenario(fixture, name);
        await s.BootstrapAsync();
        // Sign everyone in once so the users projection (the people directory's source) knows them.
        foreach (var user in new[] { s.Designer, s.Clerk, s.Reader, s.Administrator, s.DesignerOnly })
        {
            Assert.True((await fixture.Api.GetAsync("/api/v1/users/me", s.Token(user))).IsSuccess);
        }

        return s;
    }

    private static void AssertOk(HttpResult result) => Assert.True(result.IsSuccess, result.Body);

    private static void AssertForbiddenWithoutCounts(HttpResult result)
    {
        Assert.Equal(HttpStatusCode.Forbidden, result.Status);
        Assert.DoesNotContain("Count", result.Body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("members", result.Body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task OverviewIsForOwnersAndSecurityAdministratorsOnlyAndReportsEachPartIndependently()
    {
        var s = await ScenarioAsync("c8-overview");

        var owner = await fixture.Api.GetAsync("/api/v1/organization/overview", s.Token(s.Designer));
        AssertOk(owner);
        Assert.True(owner.Root.GetProperty("canManageConfiguration").GetBoolean());
        Assert.False(owner.Root.GetProperty("canAdministerSecurity").GetBoolean());
        Assert.True(owner.Root.GetProperty("members").GetProperty("status").GetProperty("available").GetBoolean());
        Assert.Equal(5, owner.Root.GetProperty("members").GetProperty("projectedMemberCount").GetInt32());
        Assert.Equal(1, owner.Root.GetProperty("members").GetProperty("securityAdministratorCount").GetInt32());
        Assert.False(owner.Root.GetProperty("members").GetProperty("securityAdministratorsCanManage").GetBoolean());
        Assert.Equal(0, owner.Root.GetProperty("teams").GetProperty("teamCount").GetInt32());
        Assert.Equal("Internal", owner.Root.GetProperty("dataProtection").GetProperty("minimumFieldClassification").GetString());
        Assert.True(owner.Root.GetProperty("dataProtection").GetProperty("isUnreviewedDefault").GetBoolean());
        Assert.Equal("NotEnforced", owner.Root.GetProperty("authentication").GetProperty("authenticationRolloutStatus").GetString());

        var security = await fixture.Api.GetAsync("/api/v1/organization/overview", s.Token(s.Administrator));
        AssertOk(security);
        Assert.False(security.Root.GetProperty("canManageConfiguration").GetBoolean());
        Assert.True(security.Root.GetProperty("canAdministerSecurity").GetBoolean());
        Assert.True(security.Root.GetProperty("members").GetProperty("securityAdministratorsCanManage").GetBoolean());

        AssertForbiddenWithoutCounts(await fixture.Api.GetAsync("/api/v1/organization/overview", s.Token(s.Clerk)));
        AssertForbiddenWithoutCounts(await fixture.Api.GetAsync("/api/v1/organization/overview", s.Token(s.DesignerOnly)));
        Assert.Equal(HttpStatusCode.Unauthorized, (await fixture.Api.GetAsync("/api/v1/organization/overview", string.Empty)).Status);
    }

    [Fact]
    public async Task PeopleDirectoryIsReadOnlyLabelledAndShowsOnlyAuthorityRoleBadges()
    {
        var s = await ScenarioAsync("c8-people");

        var owner = await fixture.Api.GetAsync("/api/v1/organization/members", s.Token(s.Designer));
        AssertOk(owner);
        Assert.StartsWith("Authorization Authority roster", owner.Root.GetProperty("source").GetString(), StringComparison.Ordinal);
        Assert.True(owner.Root.GetProperty("rolesAvailable").GetBoolean());
        var members = owner.Root.GetProperty("members").EnumerateArray().ToList();
        Assert.Equal(5, members.Count);
        var administrator = Assert.Single(members, m => m.GetProperty("userId").GetGuid() == s.Administrator.AuthorityUserId);
        Assert.Equal(["SecurityAdministrator"], administrator.GetProperty("authorityRoles").EnumerateArray().Select(r => r.GetString()).ToList());
        var self = Assert.Single(members, m => m.GetProperty("userId").GetGuid() == s.Designer.AuthorityUserId);
        Assert.True(self.GetProperty("isCurrentUser").GetBoolean());
        Assert.Equal(["OrganizationOwner", "Designer"], self.GetProperty("authorityRoles").EnumerateArray().Select(r => r.GetString()).ToList()); // AC-7.3 roster (C8 M2)
        Assert.Equal(TestIdentity.Issuer, self.GetProperty("identityIssuer").GetString());
        Assert.DoesNotContain("email", owner.Body, StringComparison.OrdinalIgnoreCase);

        AssertOk(await fixture.Api.GetAsync("/api/v1/organization/members", s.Token(s.Administrator)));
        AssertForbiddenWithoutCounts(await fixture.Api.GetAsync("/api/v1/organization/members", s.Token(s.Clerk)));
        AssertForbiddenWithoutCounts(await fixture.Api.GetAsync("/api/v1/organization/members", s.Token(s.Reader)));

        // Another organization's owner sees their own organization only: the session binds the organization, never a parameter.
        var other = await ScenarioAsync("c8-people-other");
        var otherView = await fixture.Api.GetAsync("/api/v1/organization/members", other.Token(other.Designer));
        AssertOk(otherView);
        Assert.DoesNotContain(otherView.Root.GetProperty("members").EnumerateArray(), m => m.GetProperty("userId").GetGuid() == s.Designer.AuthorityUserId);
    }

    [Fact]
    public async Task TeamDirectoryAndDetailFollowTheAuthorityCommandsAndStayOrganizationScoped()
    {
        var s = await ScenarioAsync("c8-teams");
        var other = await ScenarioAsync("c8-teams-other");

        var created = await fixture.Api.PostAsync("/api/v1/teams", s.Token(s.Designer), new { teamName = "Intake " + s.Name });
        Assert.Equal(HttpStatusCode.Created, created.Status);
        var teamId = created.Root.GetProperty("teamId").GetGuid();
        Assert.True(created.Root.GetProperty("command").GetProperty("effective").GetBoolean());

        var directory = await fixture.Api.GetAsync("/api/v1/organization/teams", s.Token(s.Designer));
        AssertOk(directory);
        Assert.True(directory.Root.GetProperty("canManage").GetBoolean());
        var row = Assert.Single(directory.Root.GetProperty("teams").EnumerateArray(), t => t.GetProperty("teamId").GetGuid() == teamId);
        Assert.Equal(0, row.GetProperty("memberCount").GetInt32());

        var added = await fixture.Api.PostAsync($"/api/v1/teams/{teamId:D}/members", s.Token(s.Designer), new { userId = s.Clerk.AuthorityUserId });
        AssertOk(added);
        Assert.True(added.Root.GetProperty("command").GetProperty("effective").GetBoolean());
        var commandId = added.Root.GetProperty("command").GetProperty("commandId").GetGuid();

        var detail = await fixture.Api.GetAsync($"/api/v1/teams/{teamId:D}", s.Token(s.Designer));
        AssertOk(detail);
        var member = Assert.Single(detail.Root.GetProperty("members").EnumerateArray());
        Assert.Equal(s.Clerk.AuthorityUserId, member.GetProperty("userId").GetGuid());
        Assert.Equal(commandId, member.GetProperty("authorityCommandId").GetGuid());
        Assert.DoesNotContain(detail.Root.GetProperty("availableMembers").EnumerateArray(), u => u.GetProperty("userId").GetGuid() == s.Clerk.AuthorityUserId);
        Assert.Equal(1, (await fixture.Api.GetAsync("/api/v1/organization/teams", s.Token(s.Designer))).Root.GetProperty("teams").EnumerateArray().Single(t => t.GetProperty("teamId").GetGuid() == teamId).GetProperty("memberCount").GetInt32());

        // The Authority's existing team rule: a Security Administrator may administer teams (add others) but never add themself (G25).
        var securityDetail = await fixture.Api.GetAsync($"/api/v1/teams/{teamId:D}", s.Token(s.Administrator));
        AssertOk(securityDetail);
        Assert.True(securityDetail.Root.GetProperty("canManage").GetBoolean());
        Assert.DoesNotContain(securityDetail.Root.GetProperty("availableMembers").EnumerateArray(), u => u.GetProperty("userId").GetGuid() == s.Administrator.AuthorityUserId);
        Assert.Contains(securityDetail.Root.GetProperty("availableMembers").EnumerateArray(), u => u.GetProperty("userId").GetGuid() == s.Reader.AuthorityUserId);
        Assert.Equal(HttpStatusCode.Forbidden, (await fixture.Api.PostAsync($"/api/v1/teams/{teamId:D}/members", s.Token(s.Administrator), new { userId = s.Administrator.AuthorityUserId })).Status);
        var byAdministrator = await fixture.Api.PostAsync($"/api/v1/teams/{teamId:D}/members", s.Token(s.Administrator), new { userId = s.Reader.AuthorityUserId });
        AssertOk(byAdministrator);
        Assert.Equal(2, (await fixture.Api.GetAsync($"/api/v1/teams/{teamId:D}", s.Token(s.Designer))).Root.GetProperty("members").GetArrayLength());
        AssertOk(await fixture.Api.PostAsync($"/api/v1/teams/{teamId:D}/members/{s.Reader.AuthorityUserId:D}/remove", s.Token(s.Administrator), new { }));

        // Members, readers and designer-only members get 403 without names or counts.
        AssertForbiddenWithoutCounts(await fixture.Api.GetAsync("/api/v1/organization/teams", s.Token(s.Clerk)));
        AssertForbiddenWithoutCounts(await fixture.Api.GetAsync($"/api/v1/teams/{teamId:D}", s.Token(s.Clerk)));
        AssertForbiddenWithoutCounts(await fixture.Api.GetAsync($"/api/v1/teams/{teamId:D}", s.Token(s.DesignerOnly)));

        // Another organization's owner: the team id is the same generic 404 as an unknown id; their own directory does not list it.
        var crossTenant = await fixture.Api.GetAsync($"/api/v1/teams/{teamId:D}", other.Token(other.Designer));
        Assert.Equal(HttpStatusCode.NotFound, crossTenant.Status);
        var unknown = await fixture.Api.GetAsync($"/api/v1/teams/{Guid.CreateVersion7():D}", other.Token(other.Designer));
        Assert.Equal(HttpStatusCode.NotFound, unknown.Status);
        Assert.Equal(unknown.Root.GetProperty("title").GetString(), crossTenant.Root.GetProperty("title").GetString());
        Assert.DoesNotContain("Intake", crossTenant.Body, StringComparison.Ordinal);
        Assert.DoesNotContain((await fixture.Api.GetAsync("/api/v1/organization/teams", other.Token(other.Designer))).Root.GetProperty("teams").EnumerateArray(), t => t.GetProperty("teamId").GetGuid() == teamId);
        Assert.Equal(HttpStatusCode.NotFound, (await fixture.Api.PatchAsync($"/api/v1/teams/{teamId:D}", other.Token(other.Designer), new { teamName = "Hijacked", expectedModifiedOn = detail.Root.GetProperty("modifiedOn").GetDateTimeOffset() })).Status);

        // Removal through the Authority command; the projection follows.
        var removed = await fixture.Api.PostAsync($"/api/v1/teams/{teamId:D}/members/{s.Clerk.AuthorityUserId:D}/remove", s.Token(s.Designer), new { });
        AssertOk(removed);
        Assert.Empty((await fixture.Api.GetAsync($"/api/v1/teams/{teamId:D}", s.Token(s.Designer))).Root.GetProperty("members").EnumerateArray());
    }

    [Fact]
    public async Task TeamRenameIsAdministratorOnlyAuditedDisplayOnlyAndConcurrencySafe()
    {
        var s = await ScenarioAsync("c8-rename");
        var created = await fixture.Api.PostAsync("/api/v1/teams", s.Token(s.Designer), new { teamName = "Old name " + s.Name });
        Assert.Equal(HttpStatusCode.Created, created.Status);
        var teamId = created.Root.GetProperty("teamId").GetGuid();
        AssertOk(await fixture.Api.PostAsync($"/api/v1/teams/{teamId:D}/members", s.Token(s.Designer), new { userId = s.Reader.AuthorityUserId }));
        var before = await fixture.Api.GetAsync($"/api/v1/teams/{teamId:D}", s.Token(s.Designer));
        AssertOk(before);
        var modifiedOn = before.Root.GetProperty("modifiedOn").GetDateTimeOffset();
        var edgesBefore = TeamMemberEdges(s.OrganizationId, teamId);
        Assert.Contains(s.Reader.AuthorityUserId.ToString("D"), edgesBefore, StringComparison.Ordinal);

        // Neither owner nor Security Administrator: members and designer-only members are refused; nothing changes.
        Assert.Equal(HttpStatusCode.Forbidden, (await fixture.Api.PatchAsync($"/api/v1/teams/{teamId:D}", s.Token(s.Clerk), new { teamName = "Not allowed", expectedModifiedOn = modifiedOn })).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await fixture.Api.PatchAsync($"/api/v1/teams/{teamId:D}", s.Token(s.DesignerOnly), new { teamName = "Not allowed", expectedModifiedOn = modifiedOn })).Status);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await fixture.Api.PatchAsync($"/api/v1/teams/{teamId:D}", s.Token(s.Designer), new { teamName = "", expectedModifiedOn = modifiedOn })).Status);

        var renamed = await fixture.Api.PatchAsync($"/api/v1/teams/{teamId:D}", s.Token(s.Designer), new { teamName = "New name " + s.Name, expectedModifiedOn = modifiedOn });
        AssertOk(renamed);
        Assert.Equal("New name " + s.Name, renamed.Root.GetProperty("teamName").GetString());
        Assert.NotEqual(modifiedOn, renamed.Root.GetProperty("modifiedOn").GetDateTimeOffset());
        Assert.Single(renamed.Root.GetProperty("members").EnumerateArray());

        // Stale expectation after the rename: 409, nothing changes.
        var stale = await fixture.Api.PatchAsync($"/api/v1/teams/{teamId:D}", s.Token(s.Designer), new { teamName = "Third name", expectedModifiedOn = modifiedOn });
        Assert.Equal(HttpStatusCode.Conflict, stale.Status);
        Assert.Equal("TEAM_MODIFIED_CONCURRENTLY", stale.Root.GetProperty("title").GetString());
        Assert.Equal("New name " + s.Name, (await fixture.Api.GetAsync($"/api/v1/teams/{teamId:D}", s.Token(s.Designer))).Root.GetProperty("teamName").GetString());

        // Display-only: the Authority's team membership edges are untouched; the rename is in the business audit projection.
        Assert.Equal(edgesBefore, TeamMemberEdges(s.OrganizationId, teamId));
        var audit = fixture.Stack.AttackerSql($"SELECT event_metadata_json::text FROM audit_events WHERE tenant_id = '{s.OrganizationId:D}' AND event_action = 'TEAM_RENAMED' AND target_entity_id = '{teamId:D}';").Trim();
        Assert.Contains("\"previousTeamName\"", audit, StringComparison.Ordinal);
        Assert.Contains("Old name " + s.Name, audit, StringComparison.Ordinal);
        Assert.Single(audit.Split("previousTeamName", StringSplitOptions.None).Skip(1));
    }

    /// <summary>F-C7-03: the logo upload and its replacement are audited in the same transaction as the branding projection write.</summary>
    [Fact]
    public async Task LogoUploadAndReplacementAreAudited()
    {
        var s = await ScenarioAsync("c8-logo-audit");
        var uploaded = await fixture.SendBytesAsync(HttpMethod.Post, "/api/v1/organization/branding/logo", s.Token(s.Designer), OnePixelPng, "image/png");
        AssertOk(uploaded);
        var replaced = await fixture.SendBytesAsync(HttpMethod.Post, "/api/v1/organization/branding/logo", s.Token(s.Designer), TwoPixelPng, "image/png");
        AssertOk(replaced);
        var refused = await fixture.SendBytesAsync(HttpMethod.Post, "/api/v1/organization/branding/logo", s.Token(s.Clerk), OnePixelPng, "image/png");
        Assert.Equal(HttpStatusCode.Forbidden, refused.Status);

        var events = fixture.Stack.AttackerSql($"SELECT event_action || '|' || actor_user_id || '|' || event_metadata_json::text FROM audit_events WHERE tenant_id = '{s.OrganizationId:D}' AND event_action IN ('ORGANIZATION_LOGO_UPLOADED', 'ORGANIZATION_LOGO_REPLACED') ORDER BY audit_sequence;")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        Assert.Equal(2, events.Length);
        Assert.StartsWith("ORGANIZATION_LOGO_UPLOADED|" + s.Designer.AuthorityUserId.ToString("D"), events[0], StringComparison.Ordinal);
        Assert.StartsWith("ORGANIZATION_LOGO_REPLACED|" + s.Designer.AuthorityUserId.ToString("D"), events[1], StringComparison.Ordinal);
        Assert.Contains("\"logoSizeBytes\":" + OnePixelPng.Length.ToString(CultureInfo.InvariantCulture), events[0].Replace(" ", string.Empty, StringComparison.Ordinal), StringComparison.Ordinal);
        Assert.Contains("\"logoSha256\":\"" + Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(TwoPixelPng)), events[1].Replace(" ", string.Empty, StringComparison.Ordinal), StringComparison.Ordinal);
        // The refused upload wrote no logo event for the clerk and the served logo is the replacement.
        Assert.DoesNotContain(s.Clerk.AuthorityUserId.ToString("D"), string.Join('\n', events), StringComparison.Ordinal);
        var served = await fixture.DownloadAsync("/api/v1/organization/branding/logo", s.Token(s.Clerk));
        Assert.Equal(TwoPixelPng, served.Body);
    }

    private string TeamMemberEdges(Guid organizationId, Guid teamId) =>
        fixture.Stack.AuthorityObserverSql($"SELECT coalesce(string_agg(subject_entity_id::text, ',' ORDER BY subject_entity_id), '') FROM authority_relationship_edges WHERE organization_id = '{organizationId:D}' AND resource_type_name = 'team' AND resource_entity_id = '{teamId:D}' AND relationship_name = 'member' AND removed_by_security_command_id IS NULL;").Trim();
}
