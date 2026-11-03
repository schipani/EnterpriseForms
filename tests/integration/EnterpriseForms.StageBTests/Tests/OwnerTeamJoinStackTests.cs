using System.Net;
using EnterpriseForms.StageBTests.Harness;
using Xunit;

namespace EnterpriseForms.StageBTests.Tests;

/// <summary>
/// OB-F8 (C-104) on the real stack (business API → Authority → SpiceDB): a record owned by a team is not readable by an organization
/// owner who is not a member; the owner cannot add themself to that team (nor can a Security Administrator add them), so the indirect
/// path stays closed; only a second organization owner can add them, after which access follows from that independent decision.
/// </summary>
[Collection(StageBStackCollectionDefinition.Name)]
public sealed class OwnerTeamJoinStackTests(StageBFixture fixture)
{
    [Fact]
    public async Task AnOwnerCannotReachTeamOwnedRecordsBySelfJoiningTheTeam()
    {
        var s = new StageBScenario(fixture, "obf8-" + Guid.NewGuid().ToString("N")[..8]);
        await s.BootstrapAsync(withAdditionalOwners: true);
        await s.BuildAccusedWorkspaceAsync();
        var record = await s.CreateAccusedAsync(s.Clerk);

        var team = await fixture.Api.PostAsync("/api/v1/teams", s.Token(s.SecondOwner), new { teamName = "Records team " + s.Name });
        Assert.Equal(HttpStatusCode.Created, team.Status);
        var teamId = team.Root.GetProperty("teamId").GetGuid();
        var transfer = await fixture.Api.PostAsync($"/api/v1/form-records/{record:D}/transfer-ownership", s.Token(s.Clerk), new { newOwnerUserId = (Guid?)null, newOwnerTeamId = teamId, transferReason = "the records team owns it" });
        Assert.True(transfer.IsSuccess, transfer.Body);
        await s.WaitGateReadyAsync(TimeSpan.FromSeconds(40));

        // The owner (Designer persona) is not a member: the team-owned record is not available to them.
        Assert.False((await s.ProjectionAsync(s.Designer, record)).IsSuccess);

        // Self-join is refused by the Authority, whatever the client sends; a Security Administrator cannot add the owner either.
        var self = await fixture.Api.PostAsync($"/api/v1/teams/{teamId:D}/members", s.Token(s.Designer), new { userId = s.Designer.AuthorityUserId });
        Assert.Equal(HttpStatusCode.Forbidden, self.Status);
        var byAdministrator = await fixture.Api.PostAsync($"/api/v1/teams/{teamId:D}/members", s.Token(s.Administrator), new { userId = s.Designer.AuthorityUserId });
        Assert.Equal(HttpStatusCode.Forbidden, byAdministrator.Status);
        await s.WaitGateReadyAsync(TimeSpan.FromSeconds(40));
        Assert.False((await s.ProjectionAsync(s.Designer, record)).IsSuccess);
        var detail = await fixture.Api.GetAsync($"/api/v1/teams/{teamId:D}", s.Token(s.Designer));
        Assert.DoesNotContain(detail.Root.GetProperty("members").EnumerateArray(), m => m.GetProperty("userId").GetGuid() == s.Designer.AuthorityUserId);
        Assert.DoesNotContain(detail.Root.GetProperty("availableMembers").EnumerateArray(), m => m.GetProperty("userId").GetGuid() == s.Designer.AuthorityUserId); // the hint no longer offers self

        // A second owner's independent decision adds them; access then follows from that decision.
        var bySecondOwner = await fixture.Api.PostAsync($"/api/v1/teams/{teamId:D}/members", s.Token(s.SecondOwner), new { userId = s.Designer.AuthorityUserId });
        Assert.True(bySecondOwner.IsSuccess, bySecondOwner.Body);
        await s.WaitGateReadyAsync(TimeSpan.FromSeconds(40));
        Assert.True((await s.WaitProjectionAsync(s.Designer, record, TimeSpan.FromSeconds(40))).IsSuccess);
    }
}
