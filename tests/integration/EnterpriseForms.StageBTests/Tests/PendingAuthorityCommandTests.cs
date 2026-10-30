using System.Net;
using EnterpriseForms.SecurityTests.Harness;
using EnterpriseForms.StageBTests.Harness;
using Xunit;

namespace EnterpriseForms.StageBTests.Tests;

/// <summary>
/// Phase C8 M1 acceptance (C-80/C-81): a team command the Authority has committed but cannot yet make Effective must never be
/// reported as completed. The non-effective state is forced deterministically by pausing the mandatory audit archive
/// (the SEC-P12 mechanism): the Authority journals the command but keeps the organization's projection gate closed until the
/// archive receipt exists. Real stack, nothing mocked; the archive is always unpaused in <c>finally</c>.
/// </summary>
[Collection(StageBStackCollectionDefinition.Name)]
public sealed class PendingAuthorityCommandTests(StageBFixture fixture)
{
    [Fact]
    public async Task ACommittedButNonEffectiveMembershipCommandIsNeverReportedCompleteAndTheProjectionFollowsOnlyTheEffectiveState()
    {
        var s = new StageBScenario(fixture, "c8-pending");
        await s.BootstrapAsync();
        var created = await fixture.Api.PostAsync("/api/v1/teams", s.Token(s.Designer), new { teamName = "Pending " + s.Name });
        Assert.Equal(HttpStatusCode.Created, created.Status);
        var teamId = created.Root.GetProperty("teamId").GetGuid();
        await s.WaitGateReadyAsync();

        var key = "c8-pending-" + Guid.NewGuid().ToString("N");
        HttpResult during;
        fixture.Stack.Pause("archive-store");
        try
        {
            during = await fixture.Api.PostAsync($"/api/v1/teams/{teamId:D}/members", s.Token(s.Designer), new { userId = s.Clerk.AuthorityUserId }, key);

            // 1. The API never reports completion: no 201, and either no effective command or an explicit non-success status.
            Assert.NotEqual(HttpStatusCode.Created, during.Status);
            if (during.IsSuccess)
            {
                Assert.False(during.Root.GetProperty("command").GetProperty("effective").GetBoolean(), during.Body);
                Assert.NotEqual("Effective", during.Root.GetProperty("command").GetProperty("commandStatus").GetString());
            }
            else
            {
                Assert.Equal(HttpStatusCode.Accepted, during.Status);
                Assert.Equal("COMMAND_PENDING", during.Root.GetProperty("title").GetString());
            }

            // 2. The Authority gate is closed (fail closed) and the command is committed, not effective.
            var gate = await fixture.Authority.GateAsync(s.OrganizationId);
            Assert.NotEqual("Ready", gate.GetProperty("projection_gate_state").GetString());

            // 3. No optimistic roster update: while pending, the roster read either fails closed or lacks the member.
            var roster = await fixture.Api.GetAsync($"/api/v1/teams/{teamId:D}", s.Token(s.Designer));
            if (roster.IsSuccess)
            {
                Assert.Empty(roster.Root.GetProperty("members").EnumerateArray());
            }
            else
            {
                Assert.Equal(HttpStatusCode.ServiceUnavailable, roster.Status);
            }
        }
        finally
        {
            fixture.Stack.Unpause("archive-store");
        }

        await s.WaitGateReadyAsync(TimeSpan.FromSeconds(90));

        // 4. The business projection was not written by the pending request (no optimistic or phantom membership), even though the Authority
        //    has now made the command effective: the roster reads only what an effective, observed command wrote.
        var afterRecovery = await fixture.Api.GetAsync($"/api/v1/teams/{teamId:D}", s.Token(s.Designer));
        Assert.True(afterRecovery.IsSuccess, afterRecovery.Body);
        Assert.Empty(afterRecovery.Root.GetProperty("members").EnumerateArray());

        // 5. Re-reading the effective Authority state: the same idempotency key resolves to the one committed command, now Effective, and only then
        //    does the projection gain the member, exactly once.
        var retried = await fixture.Api.PostAsync($"/api/v1/teams/{teamId:D}/members", s.Token(s.Designer), new { userId = s.Clerk.AuthorityUserId }, key);
        Assert.True(retried.IsSuccess, retried.Body);
        Assert.True(retried.Root.GetProperty("command").GetProperty("effective").GetBoolean(), retried.Body);
        var final = await fixture.Api.GetAsync($"/api/v1/teams/{teamId:D}", s.Token(s.Designer));
        Assert.True(final.IsSuccess, final.Body);
        var member = Assert.Single(final.Root.GetProperty("members").EnumerateArray());
        Assert.Equal(s.Clerk.AuthorityUserId, member.GetProperty("userId").GetGuid());
        Assert.Equal(retried.Root.GetProperty("command").GetProperty("commandId").GetGuid(), member.GetProperty("authorityCommandId").GetGuid());
    }

    [Fact]
    public async Task ARejectedMembershipCommandNeverAppearsSuccessfulAndLeavesTheRosterUnchanged()
    {
        var s = new StageBScenario(fixture, "c8-rejected");
        await s.BootstrapAsync();
        var created = await fixture.Api.PostAsync("/api/v1/teams", s.Token(s.Designer), new { teamName = "Rejected " + s.Name });
        Assert.Equal(HttpStatusCode.Created, created.Status);
        var teamId = created.Root.GetProperty("teamId").GetGuid();

        // G25: a Security Administrator adding themself is refused by the Authority; an unknown principal is refused as well.
        var self = await fixture.Api.PostAsync($"/api/v1/teams/{teamId:D}/members", s.Token(s.Administrator), new { userId = s.Administrator.AuthorityUserId });
        Assert.False(self.IsSuccess, self.Body);
        var unknown = await fixture.Api.PostAsync($"/api/v1/teams/{teamId:D}/members", s.Token(s.Designer), new { userId = Guid.CreateVersion7() });
        Assert.False(unknown.IsSuccess, unknown.Body);

        var roster = await fixture.Api.GetAsync($"/api/v1/teams/{teamId:D}", s.Token(s.Designer));
        Assert.True(roster.IsSuccess, roster.Body);
        Assert.Empty(roster.Root.GetProperty("members").EnumerateArray());
    }
}
