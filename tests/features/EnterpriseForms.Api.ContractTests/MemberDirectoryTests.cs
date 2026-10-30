using System.Reflection;
using EnterpriseForms.Application.Common;
using EnterpriseForms.Application.Common.Authority;
using EnterpriseForms.Application.Common.Persistence;
using EnterpriseForms.Application.Features.Identity.MemberDirectory;
using EnterpriseForms.Domain.Features.Identity;
using EnterpriseForms.Domain.Features.Teams;
using Xunit;

namespace EnterpriseForms.Api.ContractTests;

/// <summary>
/// C8 M2 people directory within the existing Authority: owners/Security Administrators only (decided before any read);
/// Security Administrators also see Authority-bound people who never signed in (the roster's candidates), owners do not;
/// 15-row server pages with a cursor bound to caller and filters; search and filters; Security Administrator action hints
/// that mirror the Authority's rules (no removal of the last one); last sign-in and sign-in status.
/// </summary>
public sealed class MemberDirectoryTests
{
    private static readonly Guid Org = Guid.CreateVersion7();
    private static readonly Guid Me = Guid.CreateVersion7();
    private static readonly RequestContext Context = new(Org, Me, "https://issuer.invalid", "subject", "Tester", "assertion", Guid.CreateVersion7());
    private static readonly DateTimeOffset Now = new(2026, 10, 30, 15, 0, 0, TimeSpan.Zero);

    private readonly List<User> users = [];
    private readonly List<AuthoritySecurityAdministrator> administrators = [];
    private readonly List<AuthoritySecurityAdministratorCandidate> candidates = [];
    private readonly Guid teamId = Guid.CreateVersion7();
    private readonly List<TeamMembership> memberships = [];
    private bool ownerAllowed = true;
    private bool securityAllowed = true;
    private bool rosterCanManage = true;
    private int scopes;

    public MemberDirectoryTests()
    {
        users.Add(new User { UserId = Me, TenantId = Org, DisplayName = "Tess Tester", ExternalIssuerUri = "https://issuer.invalid", ExternalSubjectIdentifier = "subject", CreatedOn = Now.AddDays(-3), LastSignedInOn = Now });
        administrators.Add(new AuthoritySecurityAdministrator(Me, "https://issuer.invalid", "subject", Now, Guid.CreateVersion7()));
        for (var i = 0; i < 20; i++)
        {
            var id = Guid.CreateVersion7();
            users.Add(new User { UserId = id, TenantId = Org, DisplayName = $"Person {i:00}", ExternalIssuerUri = "https://issuer.invalid", ExternalSubjectIdentifier = $"sub-{i:00}", CreatedOn = Now.AddDays(-1), LastSignedInOn = i % 2 == 0 ? Now.AddHours(-i) : null });
            candidates.Add(new AuthoritySecurityAdministratorCandidate(id, "https://issuer.invalid", $"sub-{i:00}"));
        }

        candidates.Add(new AuthoritySecurityAdministratorCandidate(Guid.CreateVersion7(), "https://issuer.invalid", "never-signed-in"));
        memberships.Add(new TeamMembership { TeamMembershipId = Guid.CreateVersion7(), TeamId = teamId, UserId = users[3].UserId, CreatedOn = Now });
    }

    private GetMemberDirectoryHandler Handler(bool candidatesReturned = true)
    {
        var authority = Stubs.Create<IAuthorizationAuthorityClient>((method, args) => method.Name == nameof(IAuthorizationAuthorityClient.CheckPermissionsBatchAsync)
            ? Task.FromResult<IReadOnlyList<PermissionDecision>>(((IReadOnlyList<PermissionCheckRequest>)args[1]!).Select(r => new PermissionDecision(r.RequestedAction == ProtectedAction.ManageOrganization ? ownerAllowed : securityAllowed, null, 9, 9, null, Guid.Empty)).ToList())
            : throw new NotSupportedException(method.Name));
        var roster = Stubs.Create<ISecurityAdministratorAuthority>((_, _) => Task.FromResult(new AuthoritySecurityAdministratorRoster(rosterCanManage, true, string.Empty, administrators, candidatesReturned ? candidates : null, 9, Guid.Empty)));
        var scope = Stubs.Create<IBusinessScope>((method, _) => method.Name switch
        {
            nameof(IBusinessScope.GetUsersAsync) => (object?)Task.FromResult(users),
            nameof(IBusinessScope.GetTeamsAsync) => Task.FromResult(new List<Team> { new() { TeamId = teamId, TenantId = Org, TeamName = "Intake desk", IsActive = true } }),
            nameof(IBusinessScope.GetActiveTeamMembershipsAsync) => Task.FromResult(memberships),
            nameof(IAsyncDisposable.DisposeAsync) => ValueTask.CompletedTask,
            _ => throw new NotSupportedException(method.Name),
        });
        var store = Stubs.Create<IBusinessStore>((_, _) =>
        {
            scopes++;
            return Task.FromResult(scope);
        });
        return new GetMemberDirectoryHandler(store, authority, roster, new TestProtector());
    }

    private static MemberDirectoryQuery Query(string? search = null, string? role = null, string? status = null, string? token = null) => new(search, role, status, null, token);

    [Fact]
    public async Task ASecurityAdministratorSeesEveryEnabledBindingInFifteenRowPages()
    {
        var handler = Handler();
        var first = await handler.GetAsync(Context, Query(), CancellationToken.None);
        Assert.Equal(22, first.TotalCount);
        Assert.Equal(15, first.Members.Count);
        Assert.Equal(15, first.PageSize);
        Assert.True(first.IncludesMembersWhoNeverSignedIn);
        Assert.Equal(GetMemberDirectoryHandler.CompleteCoverage, first.CoverageStatement);
        var second = await handler.GetAsync(Context, Query(token: first.NextToken), CancellationToken.None);
        Assert.Equal(7, second.Members.Count);
        Assert.Equal(16, second.PageStart);
        var all = first.Members.Concat(second.Members).ToList();
        Assert.Equal(22, all.Select(m => m.UserId).Distinct().Count());
        var never = Assert.Single(all, m => m.SignInStatus == "NeverSignedIn");
        Assert.Equal(string.Empty, never.DisplayName);
        Assert.Equal("never-signed-in", never.IdentitySubject);
        Assert.Null(never.FirstSeenOn);
        Assert.Same(never, all[^1]); // nameless rows sort after named ones
    }

    [Fact]
    public async Task AnOwnerWhoIsNotASecurityAdministratorSeesOnlyKnownPeopleAndAdministrators()
    {
        securityAllowed = false;
        rosterCanManage = false;
        var page = await Handler(candidatesReturned: false).GetAsync(Context, Query(), CancellationToken.None);
        Assert.Equal(21, page.TotalCount);
        Assert.False(page.IncludesMembersWhoNeverSignedIn);
        Assert.Equal(GetMemberDirectoryHandler.PartialCoverage, page.CoverageStatement);
        Assert.All(page.Members, m => Assert.Empty(m.AvailableActions!));
        Assert.False(page.SecurityAdministratorChangesOperational);
    }

    [Fact]
    public async Task SomeoneWhoIsNeitherOwnerNorSecurityAdministratorIsRefusedBeforeAnythingIsRead()
    {
        ownerAllowed = false;
        securityAllowed = false;
        var error = await Assert.ThrowsAsync<BusinessException>(() => Handler().GetAsync(Context, Query(), CancellationToken.None));
        Assert.Equal(403, error.HttpStatusCode);
        Assert.Equal(0, scopes);
    }

    [Fact]
    public async Task SearchCoversNamesIdentitiesAndTeamsAndFiltersCombine()
    {
        var handler = Handler();
        Assert.Equal(["Person 02"], (await handler.GetAsync(Context, Query(search: "intake"), CancellationToken.None)).Members.Select(m => m.DisplayName));
        Assert.Equal(["Person 07"], (await handler.GetAsync(Context, Query(search: "SUB-07"), CancellationToken.None)).Members.Select(m => m.DisplayName));
        var administratorsOnly = await handler.GetAsync(Context, Query(role: "SecurityAdministrator"), CancellationToken.None);
        Assert.Equal([Me], administratorsOnly.Members.Select(m => m.UserId));
        var neverSignedIn = await handler.GetAsync(Context, Query(status: "NeverSignedIn"), CancellationToken.None);
        Assert.Equal(1, neverSignedIn.TotalCount);
        var signedIn = await handler.GetAsync(Context, Query(role: "None", status: "SignedIn"), CancellationToken.None);
        Assert.Equal(10, signedIn.TotalCount);
        Assert.All(signedIn.Members, m => Assert.NotNull(m.LastSignedInOn));
        await Assert.ThrowsAsync<BusinessException>(() => handler.GetAsync(Context, Query(role: "OrganizationOwner"), CancellationToken.None));
        await Assert.ThrowsAsync<BusinessException>(() => handler.GetAsync(Context, Query(status: "Disabled"), CancellationToken.None));
    }

    [Fact]
    public async Task ACursorIsBoundToItsFiltersAndCaller()
    {
        var handler = Handler();
        var first = await handler.GetAsync(Context, Query(), CancellationToken.None);
        Assert.NotNull(first.NextToken);
        await Assert.ThrowsAsync<BusinessException>(() => handler.GetAsync(Context, Query(search: "person", token: first.NextToken), CancellationToken.None));
        await Assert.ThrowsAsync<BusinessException>(() => handler.GetAsync(Context with { LocalUserId = Guid.CreateVersion7() }, Query(token: first.NextToken), CancellationToken.None));
    }

    [Fact]
    public async Task ActionHintsMirrorTheAuthorityRules()
    {
        var page = await Handler().GetAsync(Context, Query(search: "Tess"), CancellationToken.None);
        var self = Assert.Single(page.Members);
        Assert.Equal(["SecurityAdministrator"], self.AuthorityRoles);
        Assert.Empty(self.AvailableActions!); // the last Security Administrator cannot be removed
        var candidate = (await Handler().GetAsync(Context, Query(search: "Person 05"), CancellationToken.None)).Members.Single();
        Assert.Equal([GetMemberDirectoryHandler.AppointSecurityAdministrator], candidate.AvailableActions);

        administrators.Add(new AuthoritySecurityAdministrator(users[5].UserId, "https://issuer.invalid", "sub-04", Now, Guid.CreateVersion7()));
        candidates.RemoveAll(c => c.AuthorityUserId == users[5].UserId);
        var withTwo = await Handler().GetAsync(Context, Query(role: "SecurityAdministrator"), CancellationToken.None);
        Assert.All(withTwo.Members, m => Assert.Equal([GetMemberDirectoryHandler.RemoveSecurityAdministrator], m.AvailableActions));
        Assert.True(withTwo.SecurityAdministratorChangesOperational);
    }

    [Fact]
    public async Task AnUnavailableRosterShowsNoRolesAndNoActions()
    {
        var authority = Stubs.Create<IAuthorizationAuthorityClient>((_, args) => Task.FromResult<IReadOnlyList<PermissionDecision>>(((IReadOnlyList<PermissionCheckRequest>)args[1]!).Select(_ => new PermissionDecision(true, null, 9, 9, null, Guid.Empty)).ToList()));
        var roster = Stubs.Create<ISecurityAdministratorAuthority>((_, _) => throw new AuthorityClientException("AUTHORITY_UNAVAILABLE", 503, true));
        var scope = Stubs.Create<IBusinessScope>((method, _) => method.Name switch
        {
            nameof(IBusinessScope.GetUsersAsync) => (object?)Task.FromResult(users),
            nameof(IBusinessScope.GetTeamsAsync) => Task.FromResult(new List<Team>()),
            nameof(IBusinessScope.GetActiveTeamMembershipsAsync) => Task.FromResult(new List<TeamMembership>()),
            nameof(IAsyncDisposable.DisposeAsync) => ValueTask.CompletedTask,
            _ => throw new NotSupportedException(method.Name),
        });
        var handler = new GetMemberDirectoryHandler(Stubs.Create<IBusinessStore>((_, _) => Task.FromResult(scope)), authority, roster, new TestProtector());
        var page = await handler.GetAsync(Context, Query(), CancellationToken.None);
        Assert.False(page.RolesAvailable);
        Assert.NotNull(page.RolesUnavailableReason);
        Assert.Equal(21, page.TotalCount);
        Assert.All(page.Members, m => Assert.Empty(m.AuthorityRoles));
        Assert.All(page.Members, m => Assert.Empty(m.AvailableActions!));
    }

    private sealed class TestProtector : IOpaqueCursorProtector
    {
        public string Protect(string payload, TimeSpan lifetime) => "ok." + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(payload));

        public string? Unprotect(string token) => token.StartsWith("ok.", StringComparison.Ordinal) ? System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(token[3..])) : null;
    }

    public class Stub<T> : DispatchProxy
        where T : class
    {
        public Func<MethodInfo, object?[], object?> Handler { get; set; } = (method, _) => throw new NotSupportedException(method.Name);

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => Handler(targetMethod!, args ?? []);
    }

    private static class Stubs
    {
        public static T Create<T>(Func<MethodInfo, object?[], object?> handler)
            where T : class
        {
            var proxy = DispatchProxy.Create<T, Stub<T>>();
            ((Stub<T>)(object)proxy).Handler = handler;
            return proxy;
        }
    }
}
