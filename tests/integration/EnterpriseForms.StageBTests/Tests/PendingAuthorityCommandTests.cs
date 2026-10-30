using System.Net;
using EnterpriseForms.SecurityTests.Harness;
using EnterpriseForms.StageBTests.Harness;
using Xunit;

namespace EnterpriseForms.StageBTests.Tests;

/// <summary>
/// Phase C8 M1 acceptance (C-80/C-81), updated for C8-M1-01 Design R (C-87): a team command the Authority has committed but
/// cannot yet make Effective must never be reported as completed, and the roster is never written while it is pending. Once the
/// Authority makes it effective, the Worker projects it without any further request. The non-effective state is forced deterministically by pausing the mandatory audit archive
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

        // 4. C8-M1-01 (Design R): with no further request from this client, the Worker projects the membership once the Authority
        //    has made the command effective — the roster gains exactly the member of the one committed command.
        Assert.Equal(1, await WaitActiveProjectionRowsAsync(s, teamId, 1));
        var final = await fixture.Api.GetAsync($"/api/v1/teams/{teamId:D}", s.Token(s.Designer));
        Assert.True(final.IsSuccess, final.Body);
        var member = Assert.Single(final.Root.GetProperty("members").EnumerateArray());
        Assert.Equal(s.Clerk.AuthorityUserId, member.GetProperty("userId").GetGuid());

        // 5. Re-sending the same idempotency key resolves to the one committed command (now projected) and duplicates nothing.
        var retried = await fixture.Api.PostAsync($"/api/v1/teams/{teamId:D}/members", s.Token(s.Designer), new { userId = s.Clerk.AuthorityUserId }, key);
        Assert.True(retried.IsSuccess, retried.Body);
        Assert.True(retried.Root.GetProperty("command").GetProperty("effective").GetBoolean(), retried.Body);
        Assert.Equal("Projected", retried.Root.GetProperty("operation").GetProperty("operationState").GetString());
        Assert.Equal(retried.Root.GetProperty("command").GetProperty("commandId").GetGuid(), member.GetProperty("authorityCommandId").GetGuid());
        Assert.Equal(1, ActiveProjectionRows(s, teamId));
    }

    /// <summary>Polls the business roster (hostile-DBA SQL, works while the Authority gate is closed) until it holds <paramref name="expected"/> active rows.</summary>
    private async Task<int> WaitActiveProjectionRowsAsync(StageBScenario s, Guid teamId, int expected, int seconds = 90)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        int rows;
        while ((rows = ActiveProjectionRows(s, teamId)) != expected && DateTime.UtcNow < deadline)
        {
            await Task.Delay(500, TestContext.Current.CancellationToken);
        }

        return rows;
    }

    private async Task<(StageBScenario S, Guid TeamId, string Key)> PendingAddAsync(string name, bool leaveArchivePaused = false)
    {
        var s = new StageBScenario(fixture, name);
        await s.BootstrapAsync();
        var created = await fixture.Api.PostAsync("/api/v1/teams", s.Token(s.Designer), new { teamName = name + " " + s.Name });
        Assert.Equal(HttpStatusCode.Created, created.Status);
        var teamId = created.Root.GetProperty("teamId").GetGuid();
        await s.WaitGateReadyAsync();
        var key = name + "-" + Guid.NewGuid().ToString("N");
        fixture.Stack.Pause("archive-store");
        try
        {
            var during = await fixture.Api.PostAsync($"/api/v1/teams/{teamId:D}/members", s.Token(s.Designer), new { userId = s.Clerk.AuthorityUserId }, key);
            Assert.False(during.IsSuccess && during.Root.GetProperty("command").GetProperty("effective").GetBoolean(), during.Body);
            Assert.Equal(0, ActiveProjectionRows(s, teamId)); // nothing written on the strength of a pending response
        }
        finally
        {
            if (!leaveArchivePaused)
            {
                fixture.Stack.Unpause("archive-store");
            }
        }

        if (!leaveArchivePaused)
        {
            await s.WaitGateReadyAsync(TimeSpan.FromSeconds(90));
        }

        return (s, teamId, key);
    }

    private async Task<int> MemberCountAsync(StageBScenario s, Guid teamId)
    {
        var detail = await fixture.Api.GetAsync($"/api/v1/teams/{teamId:D}", s.Token(s.Designer));
        Assert.True(detail.IsSuccess, detail.Body);
        return detail.Root.GetProperty("members").GetArrayLength();
    }

    private int ActiveProjectionRows(StageBScenario s, Guid teamId) =>
        int.Parse(fixture.Stack.AttackerSql($"SELECT count(*) FROM team_memberships WHERE tenant_id = '{s.OrganizationId:D}' AND team_id = '{teamId:D}' AND removed_on IS NULL").Trim(), System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>C8-M1-01: repeated and concurrent same-key checks converge on exactly one membership and one projection row.</summary>
    [Fact]
    public async Task RepeatedAndConcurrentSameKeyChecksNeverDuplicateTheMembership()
    {
        var (s, teamId, key) = await PendingAddAsync("c8-reconcile-idem");

        var results = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => fixture.Api.PostAsync($"/api/v1/teams/{teamId:D}/members", s.Token(s.Designer), new { userId = s.Clerk.AuthorityUserId }, key)));
        // Every outcome is either a confirmed Effective command or an explicit non-success; never a second membership.
        Assert.All(results, r => Assert.True(!r.IsSuccess || r.Root.GetProperty("command").GetProperty("effective").GetBoolean(), r.Body));
        Assert.All(results, r => Assert.True((int)r.Status < 500, "concurrent same-key checks must not fail with a server error: " + r.Body));
        Assert.Contains(results, r => r.IsSuccess && r.Root.GetProperty("command").GetProperty("effective").GetBoolean());
        for (var i = 0; i < 3; i++)
        {
            var again = await fixture.Api.PostAsync($"/api/v1/teams/{teamId:D}/members", s.Token(s.Designer), new { userId = s.Clerk.AuthorityUserId }, key);
            Assert.True(again.IsSuccess && again.Root.GetProperty("command").GetProperty("effective").GetBoolean(), again.Body);
        }

        Assert.Equal(1, await MemberCountAsync(s, teamId));
        Assert.Equal(1, ActiveProjectionRows(s, teamId));
        Assert.Single(results.Where(r => r.IsSuccess).Select(r => r.Root.GetProperty("command").GetProperty("commandId").GetGuid()).Distinct());
    }

    /// <summary>C8-M1-01: a pending removal keeps the member on the roster until the Authority confirms it; the same-key check then removes them.</summary>
    [Fact]
    public async Task APendingRemovalKeepsTheMemberUntilTheAuthorityConfirmsIt()
    {
        var s = new StageBScenario(fixture, "c8-pending-remove");
        await s.BootstrapAsync();
        var teamId = (await fixture.Api.PostAsync("/api/v1/teams", s.Token(s.Designer), new { teamName = "Removal " + s.Name })).Root.GetProperty("teamId").GetGuid();
        Assert.True((await fixture.Api.PostAsync($"/api/v1/teams/{teamId:D}/members", s.Token(s.Designer), new { userId = s.Clerk.AuthorityUserId })).IsSuccess);
        await s.WaitGateReadyAsync();
        var key = "c8-remove-" + Guid.NewGuid().ToString("N");
        fixture.Stack.Pause("archive-store");
        try
        {
            var during = await fixture.Api.PostAsync($"/api/v1/teams/{teamId:D}/members/{s.Clerk.AuthorityUserId:D}/remove", s.Token(s.Designer), new { }, key);
            Assert.False(during.IsSuccess && during.Root.GetProperty("command").GetProperty("effective").GetBoolean(), during.Body);
            Assert.Equal(1, ActiveProjectionRows(s, teamId)); // not removed on the strength of a pending response
        }
        finally
        {
            fixture.Stack.Unpause("archive-store");
        }

        await s.WaitGateReadyAsync(TimeSpan.FromSeconds(90));
        // The Worker removes the member once the Authority confirms it, with no further request from this client.
        Assert.Equal(0, await WaitActiveProjectionRowsAsync(s, teamId, 0));
        var confirmed = await fixture.Api.PostAsync($"/api/v1/teams/{teamId:D}/members/{s.Clerk.AuthorityUserId:D}/remove", s.Token(s.Designer), new { }, key);
        Assert.True(confirmed.IsSuccess && confirmed.Root.GetProperty("command").GetProperty("effective").GetBoolean(), confirmed.Body);
        Assert.Equal(0, await MemberCountAsync(s, teamId));
        Assert.Equal(0, ActiveProjectionRows(s, teamId));
    }

    /// <summary>C8-M1-01: another organization, another actor or a different request cannot reconcile or hijack the pending command.</summary>
    [Fact]
    public async Task APendingCommandCannotBeReconciledByAnotherOrganizationActorOrRequest()
    {
        var (s, teamId, key) = await PendingAddAsync("c8-reconcile-iso");
        var other = new StageBScenario(fixture, "c8-reconcile-iso-other");
        await other.BootstrapAsync();

        // Another organization's owner replays the key, team and user: nothing of this organization is reached and nothing is created anywhere.
        var foreign = await fixture.Api.PostAsync($"/api/v1/teams/{teamId:D}/members", other.Token(other.Designer), new { userId = s.Clerk.AuthorityUserId }, key);
        Assert.False(foreign.IsSuccess, foreign.Body);
        Assert.Equal(HttpStatusCode.NotFound, (await fixture.Api.GetAsync($"/api/v1/teams/{teamId:D}", other.Token(other.Designer))).Status);

        // Same organization: a different actor reusing the key, and the owner reusing it for a different person, are refused by the Authority's idempotency binding.
        var otherActor = await fixture.Api.PostAsync($"/api/v1/teams/{teamId:D}/members", s.Token(s.Administrator), new { userId = s.Clerk.AuthorityUserId }, key);
        Assert.False(otherActor.IsSuccess, otherActor.Body);
        var otherRequest = await fixture.Api.PostAsync($"/api/v1/teams/{teamId:D}/members", s.Token(s.Designer), new { userId = s.Reader.AuthorityUserId }, key);
        Assert.False(otherRequest.IsSuccess, otherRequest.Body);

        // Only the legitimate pending command is projected (by the Worker): the replayed member never appears; the other organization gets nothing.
        Assert.Equal(1, await WaitActiveProjectionRowsAsync(s, teamId, 1));
        Assert.Equal("0", fixture.Stack.AttackerSql($"SELECT count(*) FROM team_memberships WHERE team_id = '{teamId:D}' AND user_id = '{s.Reader.AuthorityUserId:D}'").Trim());
        Assert.Equal(0, ActiveProjectionRows(other, teamId));

        var owner = await fixture.Api.PostAsync($"/api/v1/teams/{teamId:D}/members", s.Token(s.Designer), new { userId = s.Clerk.AuthorityUserId }, key);
        Assert.True(owner.IsSuccess && owner.Root.GetProperty("command").GetProperty("effective").GetBoolean(), owner.Body);
        Assert.Equal(1, await MemberCountAsync(s, teamId));
    }

    /// <summary>C8-M1-01: a business API and Authority restart between the Pending response and the check loses nothing and duplicates nothing.</summary>
    [Fact]
    public async Task ARestartOfTheApiAndAuthorityBetweenPendingAndTheCheckIsSafe()
    {
        var (s, teamId, key) = await PendingAddAsync("c8-reconcile-restart", leaveArchivePaused: true);
        try
        {
            fixture.Stack.Compose("restart api authority", 180); // restarted while the command is still committed-but-not-effective
        }
        finally
        {
            fixture.Stack.Unpause("archive-store");
        }

        await fixture.Authority.WaitReadyAsync(TimeSpan.FromSeconds(120));
        var deadline = DateTime.UtcNow.AddSeconds(120);
        while (true)
        {
            try
            {
                if ((await fixture.Api.GetAsync("/api/v1/users/me", s.Token(s.Designer))).IsSuccess)
                {
                    break;
                }
            }
            catch (HttpRequestException)
            {
            }

            Assert.True(DateTime.UtcNow < deadline, "the business API did not come back");
            await Task.Delay(500, TestContext.Current.CancellationToken);
        }

        await s.WaitGateReadyAsync(TimeSpan.FromSeconds(120));
        // Nothing was lost with the Pending response: the Worker completes the operation recorded before submit.
        Assert.Equal(1, await WaitActiveProjectionRowsAsync(s, teamId, 1, 120));
        HttpResult confirmed;
        var attempts = 0;
        do
        {
            confirmed = await fixture.Api.PostAsync($"/api/v1/teams/{teamId:D}/members", s.Token(s.Designer), new { userId = s.Clerk.AuthorityUserId }, key);
            if (confirmed.IsSuccess && confirmed.Root.GetProperty("command").GetProperty("effective").GetBoolean())
            {
                break;
            }

            await Task.Delay(1000, TestContext.Current.CancellationToken);
        }
        while (++attempts < 30);
        Assert.True(confirmed.IsSuccess && confirmed.Root.GetProperty("command").GetProperty("effective").GetBoolean(), confirmed.Body);
        Assert.Equal(1, await MemberCountAsync(s, teamId));
        Assert.Equal(1, ActiveProjectionRows(s, teamId));
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
