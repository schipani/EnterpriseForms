using System.Net;
using System.Text.Json;
using EnterpriseForms.SecurityTests.Harness;
using EnterpriseForms.StageBTests.Harness;
using Xunit;

namespace EnterpriseForms.StageBTests.Tests;

/// <summary>
/// Owner acceptance defect: who may create records for a form can be changed after the form is saved. The independent
/// Authorization Authority is the only source of truth; a change needs no form version, is effective immediately, is
/// audited, and never grants (or removes) access to existing records.
/// </summary>
[Collection(StageBStackCollectionDefinition.Name)]
public sealed class FormAccessTests(StageBFixture fixture)
{
    private static readonly HttpStatusCode[] Denied = [HttpStatusCode.Forbidden, HttpStatusCode.NotFound];

    private async Task<JsonElement> GrantAsync(StageBScenario s, Guid formId, TestUser actor, object body, HttpStatusCode expected = HttpStatusCode.OK)
    {
        var result = await fixture.Api.PostAsync($"/api/v1/form-definitions/{formId:D}/access", s.Token(actor), body);
        Assert.True(result.Status == expected, $"expected {expected} but got {result.Status}: {result.Body}");
        return result.Json?.RootElement.Clone() ?? default;
    }

    private static object Change(Guid[]? grantUsers = null, Guid[]? grantTeams = null, Guid[]? revokeUsers = null, Guid[]? revokeTeams = null, string reason = "acceptance test") =>
        new { grantUserIds = grantUsers ?? [], grantTeamIds = grantTeams ?? [], revokeUserIds = revokeUsers ?? [], revokeTeamIds = revokeTeams ?? [], changeReason = reason };

    private static List<Guid> Subjects(JsonElement access, string property, string kind) =>
        access.GetProperty(property).EnumerateArray().Where(s => s.GetProperty("subjectKind").GetString() == kind).Select(s => s.GetProperty("subjectId").GetGuid()).ToList();

    [Fact]
    public async Task DraftOnlyFormPermissionsCanBeGrantedAndRevokedWithoutAnyVersion()
    {
        var s = new StageBScenario(fixture, "access-draft");
        await s.BootstrapAsync();
        var formId = await s.CreateFormAsync("Draft permissions " + s.Name); // no form users at creation, never published
        var field = await s.AddFieldAsync(formId, "note_text", "Note", "String");
        await s.SaveDraftAsync(formId, StageBScenario.Layout("Notes", StageBScenario.Section("main", "Main", StageBScenario.Field(field, "text", "Note", 4))));

        var initial = await fixture.Api.GetAsync($"/api/v1/form-definitions/{formId:D}/access", s.Token(s.Designer));
        Assert.True(initial.IsSuccess, initial.Body);
        Assert.Contains(s.Designer.AuthorityUserId, Subjects(initial.Root, "designers", "User"));
        Assert.Empty(initial.Root.GetProperty("formUsers").EnumerateArray());
        Assert.Contains("Authorization Authority", initial.Root.GetProperty("source").GetString(), StringComparison.Ordinal);

        // A form the caller can neither design nor use is not even listed, and its access state is refused without disclosure.
        var hidden = await fixture.Api.GetAsync("/api/v1/form-definitions", s.Token(s.Clerk));
        Assert.DoesNotContain(hidden.Root.EnumerateArray(), f => f.GetProperty("formDefinitionId").GetGuid() == formId);
        Assert.Contains((await fixture.Api.GetAsync($"/api/v1/form-definitions/{formId:D}/access", s.Token(s.Clerk))).Status, Denied);

        var granted = await GrantAsync(s, formId, s.Designer, Change(grantUsers: [s.Clerk.AuthorityUserId], reason: "clerk drafts notes"));
        Assert.Equal([s.Clerk.AuthorityUserId], Subjects(granted, "formUsers", "User"));
        Assert.Equal(s.Clerk.DisplayName, granted.GetProperty("formUsers")[0].GetProperty("displayName").GetString());
        var listed = await fixture.Api.GetAsync("/api/v1/form-definitions", s.Token(s.Clerk));
        var summary = listed.Root.EnumerateArray().Single(f => f.GetProperty("formDefinitionId").GetGuid() == formId);
        Assert.True(summary.GetProperty("canCreateRecord").GetBoolean());
        Assert.False(summary.GetProperty("canDesign").GetBoolean());
        Assert.True(summary.GetProperty("hasDraft").GetBoolean());
        Assert.True(!summary.TryGetProperty("currentVersionNumber", out var publishedVersion) || publishedVersion.ValueKind == JsonValueKind.Null, "permissions are not a version: the form is still unpublished");

        var revoked = await GrantAsync(s, formId, s.Designer, Change(revokeUsers: [s.Clerk.AuthorityUserId], reason: "no longer needed"));
        Assert.Empty(revoked.GetProperty("formUsers").EnumerateArray());
        var after = await fixture.Api.GetAsync("/api/v1/form-definitions", s.Token(s.Clerk));
        Assert.DoesNotContain(after.Root.EnumerateArray(), f => f.GetProperty("formDefinitionId").GetGuid() == formId);
    }

    [Fact]
    public async Task PublishedFormGrantAndRevokeAreImmediateAuditedAndNeverDiscloseExistingRecords()
    {
        var s = new StageBScenario(fixture, "access-published");
        await s.BootstrapAsync();
        await s.BuildAccusedWorkspaceAsync(); // published v1; the clerk is a form user from creation
        var clerkRecord = await s.CreateAccusedAsync(s.Clerk);

        async Task<(int Version, string Draft)> VersionState()
        {
            var detail = await s.GetFormAsync(s.AccusedFormId);
            var version = detail.GetProperty("summary").GetProperty("currentVersionNumber").GetInt32();
            var draft = detail.GetProperty("draft").ValueKind == JsonValueKind.Object ? detail.GetProperty("draft").GetProperty("draftRevision").GetRawText() : "none";
            return (version, draft);
        }

        var versionBefore = await VersionState();
        var current = await fixture.Api.GetAsync($"/api/v1/form-definitions/{s.AccusedFormId:D}/access", s.Token(s.Designer));
        Assert.Equal([s.Clerk.AuthorityUserId], Subjects(current.Root, "formUsers", "User"));
        var reader = await fixture.Api.PostAsync($"/api/v1/form-definitions/{s.AccusedFormId:D}/records", s.Token(s.Reader), new { values = StageBScenario.AccusedValues(), ownerTeamId = (Guid?)null });
        Assert.Contains(reader.Status, Denied);

        // Only a designer (or administrator) may change permissions: a form user, a stranger and another organization may not.
        foreach (var actor in new[] { s.Clerk, s.Reader })
        {
            await GrantAsync(s, s.AccusedFormId, actor, Change(grantUsers: [s.Reader.AuthorityUserId]), HttpStatusCode.NotFound);
        }

        var other = new StageBScenario(fixture, "access-published-other");
        await other.BootstrapAsync();
        var crossTenant = await fixture.Api.PostAsync($"/api/v1/form-definitions/{s.AccusedFormId:D}/access", other.Token(other.Designer), Change(grantUsers: [other.Reader.AuthorityUserId]));
        Assert.Contains(crossTenant.Status, Denied);

        // Grant: immediate, no new version or draft.
        var granted = await GrantAsync(s, s.AccusedFormId, s.Designer, Change(grantUsers: [s.Reader.AuthorityUserId], reason: "reader joins intake"));
        Assert.Equal(new[] { s.Clerk.AuthorityUserId, s.Reader.AuthorityUserId }.Order().ToList(), Subjects(granted, "formUsers", "User").Order().ToList());
        Assert.Equal(versionBefore, await VersionState());

        var readerRecordResult = await fixture.Api.PostAsync($"/api/v1/form-definitions/{s.AccusedFormId:D}/records", s.Token(s.Reader), new { values = StageBScenario.AccusedValues("Riley", "Reader"), ownerTeamId = (Guid?)null });
        Assert.Equal(HttpStatusCode.Created, readerRecordResult.Status);
        var readerRecord = readerRecordResult.Root.GetProperty("formRecordId").GetGuid();
        await s.WaitProjectionAsync(s.Reader, readerRecord);

        // Create permission is not read permission: nobody sees the other person's records, and the designer sees neither.
        Assert.Contains((await s.ProjectionAsync(s.Reader, clerkRecord)).Status, Denied);
        Assert.Contains((await s.ProjectionAsync(s.Clerk, readerRecord)).Status, Denied);
        Assert.Contains((await s.ProjectionAsync(s.Designer, clerkRecord)).Status, Denied);
        Assert.Contains((await s.ProjectionAsync(s.Designer, readerRecord)).Status, Denied);
        var readerList = await fixture.Api.GetAsync("/api/v1/form-records?pageSize=50", s.Token(s.Reader));
        var readerIds = readerList.Root.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("formRecordId").GetGuid()).ToList();
        Assert.Contains(readerRecord, readerIds);
        Assert.DoesNotContain(clerkRecord, readerIds);

        // Revoke: enforced on the very next request; records the principal owns stay theirs; others are unaffected.
        var revoked = await GrantAsync(s, s.AccusedFormId, s.Designer, Change(revokeUsers: [s.Reader.AuthorityUserId], reason: "intake finished"));
        Assert.Equal([s.Clerk.AuthorityUserId], Subjects(revoked, "formUsers", "User"));
        Assert.Equal(versionBefore, await VersionState());
        var denied = await fixture.Api.PostAsync($"/api/v1/form-definitions/{s.AccusedFormId:D}/records", s.Token(s.Reader), new { values = StageBScenario.AccusedValues("Too", "Late"), ownerTeamId = (Guid?)null });
        Assert.Contains(denied.Status, Denied);
        Assert.True((await s.ProjectionAsync(s.Reader, readerRecord)).IsSuccess);
        Assert.Contains((await s.ProjectionAsync(s.Reader, clerkRecord)).Status, Denied);
        await s.CreateAccusedAsync(s.Clerk, StageBScenario.AccusedValues("Still", "Allowed"));

        // The authority state, not any business table, is what the API reports; a hostile edit of business rows cannot change it.
        var reread = await fixture.Api.GetAsync($"/api/v1/form-definitions/{s.AccusedFormId:D}/access", s.Token(s.Designer));
        Assert.Equal([s.Clerk.AuthorityUserId], Subjects(reread.Root, "formUsers", "User"));

        // Every change is audited with its reason and no secrets.
        var activity = await fixture.Api.GetAsync("/api/v1/my-activity?limit=100", s.Token(s.Designer));
        var actions = activity.Root.EnumerateArray().Select(e => e.GetProperty("eventAction").GetString()).ToList();
        Assert.Contains("FORM_ACCESS_GRANTED", actions);
        Assert.Contains("FORM_ACCESS_REVOKED", actions);
    }

    [Fact]
    public async Task InvalidAndContradictoryChangesAreRejectedWithoutSideEffects()
    {
        var s = new StageBScenario(fixture, "access-invalid");
        await s.BootstrapAsync();
        await s.BuildAccusedWorkspaceAsync();
        var clerk = s.Clerk.AuthorityUserId;
        var reader = s.Reader.AuthorityUserId;

        await GrantAsync(s, s.AccusedFormId, s.Designer, Change(), HttpStatusCode.UnprocessableEntity); // nothing to change
        await GrantAsync(s, s.AccusedFormId, s.Designer, Change(grantUsers: [reader], reason: "x"), HttpStatusCode.UnprocessableEntity); // reason too short
        await GrantAsync(s, s.AccusedFormId, s.Designer, Change(grantUsers: [clerk]), HttpStatusCode.UnprocessableEntity); // already permitted
        await GrantAsync(s, s.AccusedFormId, s.Designer, Change(revokeUsers: [reader]), HttpStatusCode.UnprocessableEntity); // not currently permitted
        await GrantAsync(s, s.AccusedFormId, s.Designer, Change(grantUsers: [reader], revokeUsers: [reader]), HttpStatusCode.UnprocessableEntity); // contradictory
        await GrantAsync(s, s.AccusedFormId, s.Designer, Change(grantUsers: [Guid.CreateVersion7()]), HttpStatusCode.UnprocessableEntity); // not a principal of the organization
        await GrantAsync(s, s.AccusedFormId, s.Designer, Change(grantTeams: [Guid.CreateVersion7()]), HttpStatusCode.NotFound); // unknown team: not a registered resource
        await GrantAsync(s, Guid.CreateVersion7(), s.Designer, Change(grantUsers: [reader]), HttpStatusCode.NotFound); // unknown form

        var unchanged = await fixture.Api.GetAsync($"/api/v1/form-definitions/{s.AccusedFormId:D}/access", s.Token(s.Designer));
        Assert.Equal([clerk], Subjects(unchanged.Root, "formUsers", "User"));
    }

    [Fact]
    public async Task TeamsCanBeGrantedAndRevokedAndMembershipDecidesWhoMayCreate()
    {
        var s = new StageBScenario(fixture, "access-team");
        await s.BootstrapAsync();
        await s.BuildAccusedWorkspaceAsync();

        var team = await fixture.Api.PostAsync("/api/v1/teams", s.Token(s.Designer), new { teamName = "Intake team " + s.Name });
        Assert.Equal(HttpStatusCode.Created, team.Status);
        var teamId = team.Root.GetProperty("teamId").GetGuid();
        var member = await fixture.Api.PostAsync($"/api/v1/teams/{teamId:D}/members", s.Token(s.Designer), new { userId = s.Reader.AuthorityUserId });
        Assert.True(member.IsSuccess, member.Body);

        Assert.Contains((await fixture.Api.PostAsync($"/api/v1/form-definitions/{s.AccusedFormId:D}/records", s.Token(s.Reader), new { values = StageBScenario.AccusedValues(), ownerTeamId = (Guid?)null })).Status, Denied);

        var granted = await GrantAsync(s, s.AccusedFormId, s.Designer, Change(grantTeams: [teamId], reason: "intake team may create"));
        Assert.Equal([teamId], Subjects(granted, "formUsers", "Team"));
        Assert.StartsWith("Intake team", granted.GetProperty("formUsers").EnumerateArray().Single(f => f.GetProperty("subjectKind").GetString() == "Team").GetProperty("displayName").GetString(), StringComparison.Ordinal);
        Assert.Contains(granted.GetProperty("availableTeams").EnumerateArray(), t => t.GetProperty("teamId").GetGuid() == teamId);
        var viaTeam = await fixture.Api.PostAsync($"/api/v1/form-definitions/{s.AccusedFormId:D}/records", s.Token(s.Reader), new { values = StageBScenario.AccusedValues("Via", "Team"), ownerTeamId = (Guid?)null });
        Assert.Equal(HttpStatusCode.Created, viaTeam.Status);

        // The same team cannot be granted twice; removing the team revokes create permission for its members at once.
        await GrantAsync(s, s.AccusedFormId, s.Designer, Change(grantTeams: [teamId]), HttpStatusCode.UnprocessableEntity);
        var revoked = await GrantAsync(s, s.AccusedFormId, s.Designer, Change(revokeTeams: [teamId], reason: "team dissolved"));
        Assert.Empty(Subjects(revoked, "formUsers", "Team"));
        Assert.Contains((await fixture.Api.PostAsync($"/api/v1/form-definitions/{s.AccusedFormId:D}/records", s.Token(s.Reader), new { values = StageBScenario.AccusedValues("Gone", "Away"), ownerTeamId = (Guid?)null })).Status, Denied);
        Assert.Contains(await TeamIdsAsync(s), id => id == teamId);
    }

    private async Task<List<Guid>> TeamIdsAsync(StageBScenario s)
    {
        var teams = await fixture.Api.GetAsync("/api/v1/teams", s.Token(s.Designer));
        Assert.True(teams.IsSuccess, teams.Body);
        return teams.Root.EnumerateArray().Select(t => t.GetProperty("teamId").GetGuid()).ToList();
    }
}
