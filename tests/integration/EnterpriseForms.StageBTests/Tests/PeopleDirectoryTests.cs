using System.Net;
using System.Text.Json;
using EnterpriseForms.SecurityTests.Harness;
using EnterpriseForms.StageBTests.Harness;
using Xunit;

namespace EnterpriseForms.StageBTests.Tests;

/// <summary>
/// C8 M2 people directory on the real stack, with no Authority change: Security Administrators see every enabled Authority
/// identity binding (including a member who never signed in) through the existing roster read; organization owners see
/// only people the business directory knows plus the Security Administrators; clerks and readers are refused without
/// counts; pages are server-side with cursors bound to caller and filters; search and filters work; action hints mirror
/// the Authority's Security Administrator rules (never the last one removed); another organization is never visible.
/// </summary>
[Collection(StageBStackCollectionDefinition.Name)]
public sealed class PeopleDirectoryTests(StageBFixture fixture)
{
    private static List<JsonElement> Members(HttpResult result) => result.Root.GetProperty("members").EnumerateArray().Select(m => m.Clone()).ToList();

    private static List<string?> Actions(JsonElement member) => member.GetProperty("availableActions").EnumerateArray().Select(a => a.GetString()).ToList();

    [Fact]
    public async Task SecurityAdministratorsSeeEveryBoundMemberAndOwnersSeeKnownPeople()
    {
        var s = new StageBScenario(fixture, "c8-m2-people");
        await s.BootstrapAsync(withNeverSignedInMember: true); // Newcomer is bound by the Authority but never calls the API

        // Security Administrator: the complete enabled binding set, Newcomer included with no name yet.
        var asAdministrator = await fixture.Api.GetAsync("/api/v1/organization/members", s.Token(s.Administrator));
        Assert.True(asAdministrator.IsSuccess, asAdministrator.Body);
        Assert.True(asAdministrator.Root.GetProperty("includesMembersWhoNeverSignedIn").GetBoolean());
        Assert.Equal(6, asAdministrator.Root.GetProperty("totalCount").GetInt32());
        Assert.Equal(15, asAdministrator.Root.GetProperty("pageSize").GetInt32());
        var members = Members(asAdministrator);
        var newcomer = Assert.Single(members, m => m.GetProperty("userId").GetGuid() == s.Newcomer.AuthorityUserId);
        Assert.Equal("NeverSignedIn", newcomer.GetProperty("signInStatus").GetString());
        Assert.Equal(string.Empty, newcomer.GetProperty("displayName").GetString());
        Assert.Equal(s.Newcomer.Subject, newcomer.GetProperty("identitySubject").GetString());
        Assert.Equal(["AppointSecurityAdministrator"], Actions(newcomer));
        var self = Assert.Single(members, m => m.GetProperty("userId").GetGuid() == s.Administrator.AuthorityUserId);
        Assert.Equal(["SecurityAdministrator"], self.GetProperty("authorityRoles").EnumerateArray().Select(r => r.GetString()).ToList());
        Assert.Empty(Actions(self)); // the last Security Administrator cannot be removed
        Assert.DoesNotContain("email", asAdministrator.Body, StringComparison.OrdinalIgnoreCase);

        // Organization owner (not a Security Administrator): known people and administrators only, no action hints.
        var asOwner = await fixture.Api.GetAsync("/api/v1/organization/members", s.Token(s.Designer));
        Assert.True(asOwner.IsSuccess, asOwner.Body);
        Assert.False(asOwner.Root.GetProperty("includesMembersWhoNeverSignedIn").GetBoolean());
        Assert.Equal(5, asOwner.Root.GetProperty("totalCount").GetInt32());
        Assert.DoesNotContain(Members(asOwner), m => m.GetProperty("userId").GetGuid() == s.Newcomer.AuthorityUserId);
        Assert.All(Members(asOwner), m => Assert.Empty(Actions(m)));

        // Everyone else is refused without counts or names.
        foreach (var user in new[] { s.Clerk, s.Reader, s.DesignerOnly })
        {
            var refused = await fixture.Api.GetAsync("/api/v1/organization/members", s.Token(user));
            Assert.Equal(HttpStatusCode.Forbidden, refused.Status);
            Assert.DoesNotContain("totalCount", refused.Body, StringComparison.OrdinalIgnoreCase);
        }

        // Server paging with cursors bound to caller and filters.
        var first = await fixture.Api.GetAsync("/api/v1/organization/members?pageSize=4", s.Token(s.Administrator));
        Assert.Equal(4, Members(first).Count);
        var next = first.Root.GetProperty("nextToken").GetString()!;
        var second = await fixture.Api.GetAsync($"/api/v1/organization/members?pageSize=4&continuationToken={Uri.EscapeDataString(next)}", s.Token(s.Administrator));
        Assert.True(second.IsSuccess, second.Body);
        Assert.Equal(2, Members(second).Count);
        Assert.Equal(5, second.Root.GetProperty("pageStart").GetInt32());
        Assert.Empty(Members(first).Select(m => m.GetProperty("userId").GetGuid()).Intersect(Members(second).Select(m => m.GetProperty("userId").GetGuid())));
        Assert.False((await fixture.Api.GetAsync($"/api/v1/organization/members?pageSize=4&continuationToken={Uri.EscapeDataString(next)}", s.Token(s.Designer))).IsSuccess);
        Assert.False((await fixture.Api.GetAsync($"/api/v1/organization/members?pageSize=4&q=x&continuationToken={Uri.EscapeDataString(next)}", s.Token(s.Administrator))).IsSuccess);

        // Search and filters.
        var bySubject = await fixture.Api.GetAsync($"/api/v1/organization/members?q={Uri.EscapeDataString(s.Newcomer.Subject)}", s.Token(s.Administrator));
        Assert.Equal([s.Newcomer.AuthorityUserId], Members(bySubject).Select(m => m.GetProperty("userId").GetGuid()));
        var administrators = await fixture.Api.GetAsync("/api/v1/organization/members?role=SecurityAdministrator", s.Token(s.Administrator));
        Assert.Equal([s.Administrator.AuthorityUserId], Members(administrators).Select(m => m.GetProperty("userId").GetGuid()));
        var never = await fixture.Api.GetAsync("/api/v1/organization/members?signInStatus=NeverSignedIn", s.Token(s.Administrator));
        Assert.Equal(1, never.Root.GetProperty("totalCount").GetInt32());
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await fixture.Api.GetAsync("/api/v1/organization/members?role=OrganizationOwner", s.Token(s.Administrator))).Status);

        // Another organization: its Security Administrator never sees these people.
        var other = new StageBScenario(fixture, "c8-m2-people-other");
        await other.BootstrapAsync();
        var otherView = await fixture.Api.GetAsync("/api/v1/organization/members", other.Token(other.Administrator));
        Assert.True(otherView.IsSuccess, otherView.Body);
        Assert.DoesNotContain(Members(otherView), m => m.GetProperty("userId").GetGuid() == s.Newcomer.AuthorityUserId || m.GetProperty("userId").GetGuid() == s.Designer.AuthorityUserId);
    }
}
