using System.Net;
using System.Text.Json;
using EnterpriseForms.SecurityTests.Harness;
using EnterpriseForms.StageBTests.Harness;
using Xunit;

namespace EnterpriseForms.StageBTests.Tests;

/// <summary>
/// People profiles, presence and approval paging on the real stack. Profiles: only non-security fields, the person or an organization
/// owner of the same organization, revision-checked, audited, another organization simply not found, sign-in never overwrites an edited
/// name. Presence: informational, the identity always comes from the session, multi-tab/device semantics, expiry, other organizations
/// and unknown people are Unknown, bounded queries. Approvals: server pages of five, newest first, independent lists, filters and a
/// cursor bound to its filters.
/// </summary>
[Collection(StageBStackCollectionDefinition.Name)]
public sealed class PeopleProfilesPresenceTests(StageBFixture fixture)
{
    private Task<HttpResult> PutProfileAsync(StageBScenario s, TestUser actor, Guid target, object body) =>
        fixture.Api.SendAsync(HttpMethod.Put, $"/api/v1/organization/members/{target:D}/profile", s.Token(actor), body, null);

    private Task<HttpResult> GetProfileAsync(StageBScenario s, TestUser actor, Guid target) => fixture.Api.GetAsync($"/api/v1/organization/members/{target:D}/profile", s.Token(actor));

    private static object Edit(long revision, string? name = null, string? preferred = null, string? title = null, bool reset = false) =>
        new { displayName = name, preferredName = preferred, jobTitle = title, expectedRevision = revision, resetDisplayName = reset };

    private Task<HttpResult> HeartbeatAsync(StageBScenario s, TestUser user, int ageSeconds = 0) => fixture.Api.PostAsync("/api/v1/presence/heartbeat", s.Token(user), new { interactionAgeSeconds = ageSeconds });

    private async Task<Dictionary<Guid, string>> PresenceAsync(StageBScenario s, TestUser viewer, params Guid[] ids)
    {
        var result = await fixture.Api.GetAsync("/api/v1/presence?userIds=" + string.Join(',', ids.Select(i => i.ToString("D"))), s.Token(viewer));
        Assert.True(result.IsSuccess, result.Body);
        Assert.True(result.Root.GetProperty("available").GetBoolean());
        return result.Root.GetProperty("presence").EnumerateArray().ToDictionary(p => p.GetProperty("userId").GetGuid(), p => p.GetProperty("state").GetString()!);
    }

    private static void Refused(HttpResult result, string code)
    {
        Assert.False(result.IsSuccess, result.Body);
        Assert.Equal(code, result.ErrorCode);
    }

    /// <summary>Makes sure the person has a business directory row (a first authenticated call projects it).</summary>
    private async Task EnsureProjectedAsync(StageBScenario s, params TestUser[] users)
    {
        foreach (var user in users)
        {
            Assert.True((await fixture.Api.GetAsync("/api/v1/users/me", s.Token(user))).IsSuccess);
        }
    }

    [Fact]
    public async Task ProfilesAreEditedByThePersonOrAnOwnerOnlyAndNeverChangeIdentityRolesOrMembership()
    {
        var s = new StageBScenario(fixture, "profiles");
        await s.BootstrapAsync();
        var other = new StageBScenario(fixture, "profiles-other");
        await other.BootstrapAsync();
        await EnsureProjectedAsync(s, s.Designer, s.Clerk, s.Reader);

        var before = await GetProfileAsync(s, s.Designer, s.Clerk.AuthorityUserId);
        Assert.True(before.IsSuccess, before.Body);
        Assert.True(before.Root.GetProperty("canEdit").GetBoolean());
        var revision = before.Root.GetProperty("profileRevision").GetInt64();
        var issuer = before.Root.GetProperty("identityIssuer").GetString();
        var subject = before.Root.GetProperty("identitySubject").GetString();

        // Authorization: a person edits their own; a non-owner cannot edit anyone else's; another organization's owner finds nothing.
        Refused(await PutProfileAsync(s, s.Reader, s.Clerk.AuthorityUserId, Edit(revision, "Hacked Name")), "FORBIDDEN");
        Refused(await GetProfileAsync(s, s.Reader, s.Clerk.AuthorityUserId), "FORBIDDEN");
        Refused(await PutProfileAsync(other, other.Designer, s.Clerk.AuthorityUserId, Edit(revision, "Cross Organization")), "PROFILE_NOT_AVAILABLE");

        // Validation: no email as a display name, no control characters, bounded lengths.
        Refused(await PutProfileAsync(s, s.Designer, s.Clerk.AuthorityUserId, Edit(revision, "clerk@example.com")), "INVALID_REQUEST");
        Refused(await PutProfileAsync(s, s.Designer, s.Clerk.AuthorityUserId, Edit(revision, "Bad\u0007Name")), "INVALID_REQUEST");
        Refused(await PutProfileAsync(s, s.Designer, s.Clerk.AuthorityUserId, Edit(revision, new string('x', 201))), "INVALID_REQUEST");
        Refused(await PutProfileAsync(s, s.Designer, s.Clerk.AuthorityUserId, Edit(revision, "Casey", new string('p', 101))), "INVALID_REQUEST");

        // An owner edits; the profile appears in the directory and the revision moves.
        var edited = await PutProfileAsync(s, s.Designer, s.Clerk.AuthorityUserId, Edit(revision, "  Casey   Q.  Clerk ", "Case", "Intake clerk"));
        Assert.True(edited.IsSuccess, edited.Body);
        Assert.Equal("Casey Q. Clerk", edited.Root.GetProperty("displayName").GetString());
        Assert.True(edited.Root.GetProperty("displayNameOverridden").GetBoolean());
        Assert.Equal(revision + 1, edited.Root.GetProperty("profileRevision").GetInt64());
        Assert.Equal(issuer, edited.Root.GetProperty("identityIssuer").GetString()); // identity is untouched
        Assert.Equal(subject, edited.Root.GetProperty("identitySubject").GetString());
        var directory = await fixture.Api.GetAsync("/api/v1/organization/members?pageSize=50", s.Token(s.Designer));
        var row = directory.Root.GetProperty("members").EnumerateArray().Single(m => m.GetProperty("userId").GetGuid() == s.Clerk.AuthorityUserId);
        Assert.Equal("Casey Q. Clerk", row.GetProperty("displayName").GetString());
        Assert.Equal(issuer, row.GetProperty("identityIssuer").GetString());
        Assert.Equal(subject, row.GetProperty("identitySubject").GetString());
        Assert.Empty(row.GetProperty("authorityRoles").EnumerateArray()); // a profile edit confers no role

        // Optimistic concurrency: the stale revision is refused with 409 and nothing changes.
        var stale = await PutProfileAsync(s, s.Designer, s.Clerk.AuthorityUserId, Edit(revision, "Stale Writer"));
        Assert.Equal(HttpStatusCode.Conflict, stale.Status);
        Assert.Equal("PROFILE_REVISION_CONFLICT", stale.ErrorCode);

        // Sign-in refreshes the identity provider's name but never overwrites an edited display name.
        Assert.True((await fixture.Api.GetAsync("/api/v1/users/me", s.Token(s.Clerk))).IsSuccess);
        var me = await fixture.Api.GetAsync("/api/v1/users/me/profile", s.Token(s.Clerk));
        Assert.Equal("Casey Q. Clerk", me.Root.GetProperty("displayName").GetString());
        Assert.False(string.IsNullOrEmpty(me.Root.GetProperty("identityDisplayName").GetString()));
        Assert.Equal("Case", me.Root.GetProperty("preferredName").GetString());

        // The person edits their own profile (through the self route) and can restore the identity provider's name.
        var own = await fixture.Api.SendAsync(HttpMethod.Put, "/api/v1/users/me/profile", s.Token(s.Clerk), Edit(me.Root.GetProperty("profileRevision").GetInt64(), null, "Casey", null, reset: true), null);
        Assert.True(own.IsSuccess, own.Body);
        Assert.False(own.Root.GetProperty("displayNameOverridden").GetBoolean());
        Assert.Equal(own.Root.GetProperty("identityDisplayName").GetString(), own.Root.GetProperty("displayName").GetString());
        Assert.Equal(JsonValueKind.Null, own.Root.GetProperty("jobTitle").ValueKind);

        // Audit: one event per effective change, field names only (never values), no event for a no-op.
        var ownRevision = own.Root.GetProperty("profileRevision").GetInt64();
        Assert.True((await fixture.Api.SendAsync(HttpMethod.Put, "/api/v1/users/me/profile", s.Token(s.Clerk), Edit(ownRevision, null, "Casey", null), null)).IsSuccess);
        var audit = fixture.Stack.AttackerSql($"SELECT string_agg(event_description, '|' ORDER BY audit_sequence) FROM audit_events WHERE tenant_id = '{s.OrganizationId:D}' AND event_action = 'USER_PROFILE_UPDATED';").Trim();
        Assert.Equal("profile updated by an organization owner|own profile updated", audit);
        var metadata = fixture.Stack.AttackerSql($"SELECT string_agg(event_metadata_json::text, ' ') FROM audit_events WHERE tenant_id = '{s.OrganizationId:D}' AND event_action = 'USER_PROFILE_UPDATED';");
        Assert.DoesNotContain("Intake clerk", metadata, StringComparison.Ordinal);
        Assert.DoesNotContain("Casey Q.", metadata, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PresenceIsInformationalDerivedFromTheSessionAndLimitedToTheOrganization()
    {
        var s = new StageBScenario(fixture, "presence");
        await s.BootstrapAsync();
        var other = new StageBScenario(fixture, "presence-other");
        await other.BootstrapAsync();
        await EnsureProjectedAsync(s, s.Designer, s.Clerk, s.Reader);
        await EnsureProjectedAsync(other, other.Designer);
        var clerk = s.Clerk.AuthorityUserId;
        var reader = s.Reader.AuthorityUserId;

        // Nobody has sent a heartbeat: Offline (known to the organization), never Available.
        Assert.Equal("Offline", (await PresenceAsync(s, s.Designer, clerk))[clerk]);

        // A heartbeat marks only the caller: the request carries no user id, so one person cannot update another's presence.
        Assert.Equal(HttpStatusCode.NoContent, (await HeartbeatAsync(s, s.Clerk)).Status);
        var states = await PresenceAsync(s, s.Designer, clerk, reader);
        Assert.Equal("Available", states[clerk]);
        Assert.Equal("Offline", states[reader]);
        Refused(await fixture.Api.PostAsync("/api/v1/presence/heartbeat", s.Token(s.Clerk), new { interactionAgeSeconds = -1 }), "INVALID_REQUEST");
        Refused(await fixture.Api.PostAsync("/api/v1/presence/heartbeat", s.Token(s.Clerk), new { interactionAgeSeconds = 90_000 }), "INVALID_REQUEST");
        Assert.Equal("Offline", (await PresenceAsync(s, s.Designer, reader))[reader]);

        // Away after five minutes without interaction while the heartbeat stays fresh; multiple tabs/devices: the most recent interaction wins.
        SetPresence(s, clerk, heartbeatSecondsAgo: 5, interactionSecondsAgo: 400);
        Assert.Equal("Away", (await PresenceAsync(s, s.Designer, clerk))[clerk]);
        Assert.Equal(HttpStatusCode.NoContent, (await HeartbeatAsync(s, s.Clerk, ageSeconds: 3)).Status); // another tab is being used right now
        Assert.Equal("Available", (await PresenceAsync(s, s.Designer, clerk))[clerk]);
        Assert.Equal(HttpStatusCode.NoContent, (await HeartbeatAsync(s, s.Clerk, ageSeconds: 600)).Status); // an idle tab never moves the interaction time backwards
        SetPresence(s, clerk, heartbeatSecondsAgo: 30, interactionSecondsAgo: 3);
        Assert.Equal("Available", (await PresenceAsync(s, s.Designer, clerk))[clerk]);

        // A silent network failure cannot leave anyone Available: the heartbeat expires; so does an explicit sign-out (cleared heartbeat).
        SetPresence(s, clerk, heartbeatSecondsAgo: 200, interactionSecondsAgo: 3);
        Assert.Equal("Offline", (await PresenceAsync(s, s.Designer, clerk))[clerk]);
        Assert.Equal(HttpStatusCode.NoContent, (await HeartbeatAsync(s, s.Clerk)).Status);
        fixture.Stack.AttackerSql($"UPDATE user_presences SET last_heartbeat_on = NULL WHERE user_id = '{clerk:D}';");
        Assert.Equal("Offline", (await PresenceAsync(s, s.Designer, clerk))[clerk]);

        // Other organizations and unknown people are Unknown, indistinguishably; the query is bounded.
        var stranger = other.Designer.AuthorityUserId;
        var states2 = await PresenceAsync(s, s.Designer, stranger, Guid.NewGuid());
        Assert.All(states2.Values, state => Assert.Equal("Unknown", state));
        var foreign = await PresenceAsync(other, other.Designer, clerk);
        Assert.Equal("Unknown", foreign[clerk]);
        Refused(await fixture.Api.GetAsync("/api/v1/presence?userIds=" + string.Join(',', Enumerable.Range(0, 101).Select(_ => Guid.NewGuid().ToString("D"))), s.Token(s.Designer)), "INVALID_REQUEST");
        Refused(await fixture.Api.GetAsync("/api/v1/presence?userIds=not-a-guid", s.Token(s.Designer)), "INVALID_REQUEST");

        // Unauthenticated callers neither send nor read presence; presence does not take part in access decisions.
        Assert.Equal(HttpStatusCode.Unauthorized, (await fixture.Api.PostAsync("/api/v1/presence/heartbeat", string.Empty, new { interactionAgeSeconds = 0 })).Status);
        Assert.Equal(HttpStatusCode.Unauthorized, (await fixture.Api.GetAsync("/api/v1/presence?userIds=" + clerk.ToString("D"), string.Empty)).Status);
        Assert.True((await fixture.Api.GetAsync("/api/v1/dashboard", s.Token(s.Reader))).IsSuccess); // offline Reader is still fully authorized
    }

    private void SetPresence(StageBScenario s, Guid userId, int heartbeatSecondsAgo, int interactionSecondsAgo) =>
        fixture.Stack.AttackerSql($"UPDATE user_presences SET last_heartbeat_on = now() - interval '{heartbeatSecondsAgo} seconds', last_interaction_on = now() - interval '{interactionSecondsAgo} seconds' WHERE user_id = '{userId:D}' AND tenant_id = '{s.OrganizationId:D}';");

    [Fact]
    public async Task ApprovalsArePagedFiveAtATimeNewestFirstWithIndependentFilteredLists()
    {
        var s = new StageBScenario(fixture, "approvals-paging");
        await s.BootstrapAsync();
        var run = Guid.NewGuid().ToString("N")[..6];
        var requestIds = new List<Guid>();
        for (var i = 0; i < 7; i++)
        {
            var requested = await fixture.Api.PostAsync("/api/v1/organization/membership-requests", s.Token(s.Designer),
                new { subjectIdentifier = $"paging-{run}-{i}", displayNameHint = $"Pat Paging {i}", initialRoleName = (string?)null, reason = "paging check", useDevelopmentStepUp = true });
            Assert.True(requested.IsSuccess, requested.Body);
            requestIds.Add(requested.Root.GetProperty("requestId").GetGuid());
            await Task.Delay(20, TestContext.Current.CancellationToken);
        }

        var first = await fixture.Api.GetAsync("/api/v1/organization/membership-requests", s.Token(s.Designer));
        Assert.True(first.IsSuccess, first.Body);
        Assert.Equal(7, first.Root.GetProperty("totalCount").GetInt32());
        Assert.Equal(5, first.Root.GetProperty("pageSize").GetInt32());
        Assert.Equal(5, first.Root.GetProperty("items").GetArrayLength());
        Assert.Equal(requestIds.AsEnumerable().Reverse().Take(5), first.Root.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("requestId").GetGuid())); // newest first
        Assert.Equal(JsonValueKind.Null, first.Root.GetProperty("previousToken").ValueKind);
        var next = first.Root.GetProperty("nextToken").GetString();
        var second = await fixture.Api.GetAsync("/api/v1/organization/membership-requests?continuationToken=" + Uri.EscapeDataString(next!), s.Token(s.Designer));
        Assert.True(second.IsSuccess, second.Body);
        Assert.Equal(2, second.Root.GetProperty("items").GetArrayLength());
        Assert.Equal(6, second.Root.GetProperty("pageStart").GetInt32());
        Assert.Equal(requestIds.Take(2).Reverse(), second.Root.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("requestId").GetGuid()));
        Assert.Equal(JsonValueKind.Null, second.Root.GetProperty("nextToken").ValueKind);

        // Search and status filters narrow the server page; a cursor from other filters is refused (never silently reused).
        var searched = await fixture.Api.GetAsync($"/api/v1/organization/membership-requests?q={Uri.EscapeDataString($"Pat Paging 3")}", s.Token(s.Designer));
        Assert.Equal(1, searched.Root.GetProperty("totalCount").GetInt32());
        var approved = await fixture.Api.GetAsync("/api/v1/organization/membership-requests?status=Approved", s.Token(s.Designer));
        Assert.Equal(0, approved.Root.GetProperty("totalCount").GetInt32());
        Assert.Empty(approved.Root.GetProperty("items").EnumerateArray());
        var mismatch = await fixture.Api.GetAsync("/api/v1/organization/membership-requests?status=Pending&continuationToken=" + Uri.EscapeDataString(next!), s.Token(s.Designer));
        Refused(mismatch, "CURSOR_SCOPE_MISMATCH");
        Refused(await fixture.Api.GetAsync("/api/v1/organization/membership-requests?status=Bogus", s.Token(s.Designer)), "INVALID_REQUEST");
        Refused(await fixture.Api.GetAsync("/api/v1/organization/membership-requests?continuationToken=tampered", s.Token(s.Designer)), "CURSOR_INVALID");

        // The proposals list is independent (its own total and cursor); both lists are for owners and Security Administrators only.
        var proposals = await fixture.Api.GetAsync("/api/v1/organization/owner-proposals", s.Token(s.Designer));
        Assert.True(proposals.IsSuccess, proposals.Body);
        Assert.Equal(0, proposals.Root.GetProperty("totalCount").GetInt32());
        Refused(await fixture.Api.GetAsync("/api/v1/organization/membership-requests", s.Token(s.Clerk)), "FORBIDDEN");
        Refused(await fixture.Api.GetAsync("/api/v1/organization/owner-proposals", s.Token(s.Reader)), "FORBIDDEN");
    }
}
