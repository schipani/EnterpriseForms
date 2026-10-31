using System.Reflection;
using System.Text.Json;
using EnterpriseForms.Application.Common;
using EnterpriseForms.Application.Common.Authority;
using EnterpriseForms.Application.Common.Persistence;
using EnterpriseForms.Application.Features.FormDefinitions.CreateFormDefinition;
using EnterpriseForms.Application.Features.Identity.MemberAdministration;
using EnterpriseForms.Application.Features.Identity.MemberDirectory;
using EnterpriseForms.Contracts.Features;
using EnterpriseForms.Domain.Features.Audit;
using EnterpriseForms.Domain.Features.Identity;
using EnterpriseForms.Domain.Features.Teams;
using Xunit;

namespace EnterpriseForms.Api.ContractTests;

/// <summary>
/// C8 M2 people directory and membership commands (business side; the Authority decides). Directory: owners/Security
/// Administrators only (decided before any read); the Authority roster is the member list (disabled members and people who
/// never signed in included); 15-row pages with cursors bound to caller and filters; role, sign-in and membership filters;
/// action hints mirror the Authority rules for the caller; an unavailable roster falls back to names only with no actions.
/// Commands: payloads and kinds sent to the Authority, the organization issuer taken from the Authority, owner proposals
/// dispatched to the right command, pending reported as pending, invalid input refused before the Authority is called.
/// </summary>
public sealed class MemberDirectoryTests
{
    private static readonly Guid Org = Guid.CreateVersion7();
    private static readonly Guid Me = Guid.CreateVersion7();
    private static readonly RequestContext Context = new(Org, Me, "https://issuer.invalid", "subject", "Tester", "assertion", Guid.CreateVersion7());
    private static readonly DateTimeOffset Now = new(2026, 10, 31, 9, 0, 0, TimeSpan.Zero);

    private readonly List<User> users = [];
    private readonly List<AuthorityRosterMember> members = [];
    private readonly List<AuthorityOwnerProposal> proposals = [];
    private readonly Guid teamId = Guid.CreateVersion7();
    private readonly List<TeamMembership> memberships = [];
    private readonly List<SecurityCommandRequest> submitted = [];
    private bool ownerAllowed = true;
    private bool securityAllowed;
    private bool rosterUnavailable;
    private SecurityCommandStatus commandStatus = SecurityCommandStatus.Effective;
    private int scopes;

    public MemberDirectoryTests()
    {
        Add(Me, "Tess Tester", ["organization_owner"], signedIn: true);
        for (var i = 0; i < 20; i++)
        {
            Add(Guid.CreateVersion7(), $"Person {i:00}", i == 4 ? ["designer"] : [], signedIn: i % 2 == 0, enabled: i != 6);
        }

        members.Add(new AuthorityRosterMember(Guid.CreateVersion7(), "https://issuer.invalid", "never-signed-in", true, Now, null, [], "Nia Newcomer"));
        memberships.Add(new TeamMembership { TeamMembershipId = Guid.CreateVersion7(), TeamId = teamId, UserId = members[3].AuthorityUserId, CreatedOn = Now });
    }

    private void Add(Guid id, string name, IReadOnlyList<string> roles, bool signedIn, bool enabled = true)
    {
        users.Add(new User { UserId = id, TenantId = Org, DisplayName = name, ExternalIssuerUri = "https://issuer.invalid", ExternalSubjectIdentifier = "sub-" + name.Replace(' ', '-'), CreatedOn = Now.AddDays(-3), LastSignedInOn = signedIn ? Now : null });
        members.Add(new AuthorityRosterMember(id, "https://issuer.invalid", "sub-" + name.Replace(' ', '-'), enabled, Now.AddDays(-5), enabled ? null : Now.AddDays(-1), roles, null));
    }

    private AuthorityOrganizationRoster Roster() => rosterUnavailable
        ? throw new AuthorityClientException("AUTHORITY_UNAVAILABLE", 503, true)
        : new AuthorityOrganizationRoster(ownerAllowed, securityAllowed, true, string.Empty, true, "https://issuer.invalid", members, proposals, [], 9, Guid.Empty);

    private IAuthorizationAuthorityClient Authority() => Stubs.Create<IAuthorizationAuthorityClient>((method, args) => method.Name switch
    {
        nameof(IAuthorizationAuthorityClient.CheckPermissionsBatchAsync) => Task.FromResult<IReadOnlyList<PermissionDecision>>(((IReadOnlyList<PermissionCheckRequest>)args[1]!)
            .Select(r => new PermissionDecision(r.RequestedAction == ProtectedAction.ManageOrganization ? ownerAllowed : securityAllowed, null, 9, 9, null, Guid.Empty)).ToList()),
        nameof(IAuthorizationAuthorityClient.CheckPermissionAsync) => Task.FromResult(new PermissionDecision(ownerAllowed, null, 9, 9, null, Guid.Empty)),
        nameof(IAuthorizationAuthorityClient.SubmitSecurityCommandAsync) => Submit((SecurityCommandRequest)args[1]!),
        _ => throw new NotSupportedException(method.Name),
    });

    private Task<SecurityCommandResult> Submit(SecurityCommandRequest request)
    {
        submitted.Add(request);
        return Task.FromResult(new SecurityCommandResult(Guid.CreateVersion7(), commandStatus, 10, 10, "Ready", null, Guid.Empty));
    }

    private IOrganizationRosterAuthority RosterAuthority() => Stubs.Create<IOrganizationRosterAuthority>((_, _) => Task.FromResult(Roster()));

    private IBusinessStore Store() => Stubs.Create<IBusinessStore>((_, _) =>
    {
        scopes++;
        return Task.FromResult(Stubs.Create<IBusinessScope>((method, _) => method.Name switch
        {
            nameof(IBusinessScope.GetUsersAsync) => (object?)Task.FromResult(users),
            nameof(IBusinessScope.GetTeamsAsync) => Task.FromResult(new List<Team> { new() { TeamId = teamId, TenantId = Org, TeamName = "Intake desk", IsActive = true } }),
            nameof(IBusinessScope.GetActiveTeamMembershipsAsync) => Task.FromResult(memberships),
            nameof(IBusinessScope.AddAuditEvent) => null,
            nameof(IBusinessScope.GetTargetAuditEventsAsync) => Task.FromResult(new List<EnterpriseForms.Domain.Features.Audit.AuditEvent>()),
            nameof(IBusinessScope.CommitAsync) => Task.CompletedTask,
            nameof(IAsyncDisposable.DisposeAsync) => ValueTask.CompletedTask,
            _ => throw new NotSupportedException(method.Name),
        }));
    });

    private GetMemberDirectoryHandler Directory() => new(Store(), Authority(), RosterAuthority(), new TestProtector());

    private MemberAdministrationHandlers Commands()
    {
        var authority = Authority();
        return new MemberAdministrationHandlers(Store(), authority, RosterAuthority(), new AuthorityCommandAwaiter(authority, new JumpingClock()), TimeProvider.System);
    }

    private static MemberDirectoryQuery Query(string? search = null, string? role = null, string? status = null, string? token = null, string? membership = null) => new(search, role, status, null, token, membership);

    // ---- directory ----

    [Fact]
    public async Task TheAuthorityRosterIsTheMemberListInFifteenRowPages()
    {
        var handler = Directory();
        var first = await handler.GetAsync(Context, Query(), CancellationToken.None);
        Assert.Equal(22, first.TotalCount);
        Assert.Equal(15, first.Members.Count);
        Assert.Equal(GetMemberDirectoryHandler.CompleteCoverage, first.CoverageStatement);
        var second = await handler.GetAsync(Context, Query(token: first.NextToken), CancellationToken.None);
        Assert.Equal(7, second.Members.Count);
        var all = first.Members.Concat(second.Members).ToList();
        Assert.Equal(22, all.Select(m => m.UserId).Distinct().Count());
        var newcomer = Assert.Single(all, m => m.SignInStatus == "NeverSignedIn");
        Assert.Equal("Nia Newcomer", newcomer.DisplayName); // the approved request's display hint until they sign in
        Assert.Equal("Disabled", Assert.Single(all, m => m.DisplayName == "Person 06").MembershipState);
        Assert.Equal(["OrganizationOwner"], all.Single(m => m.UserId == Me).AuthorityRoles);
    }

    [Fact]
    public async Task SomeoneWhoIsNeitherOwnerNorSecurityAdministratorIsRefusedBeforeAnythingIsRead()
    {
        ownerAllowed = false;
        var error = await Assert.ThrowsAsync<BusinessException>(() => Directory().GetAsync(Context, Query(), CancellationToken.None));
        Assert.Equal(403, error.HttpStatusCode);
        Assert.Equal(0, scopes);
    }

    [Fact]
    public async Task FiltersCoverRolesSignInAndMembershipAndUnknownValuesAreRefused()
    {
        var handler = Directory();
        Assert.Equal(["Person 02"], (await handler.GetAsync(Context, Query(search: "intake"), CancellationToken.None)).Members.Select(m => m.DisplayName));
        Assert.Equal(["Person 04"], (await handler.GetAsync(Context, Query(role: "Designer"), CancellationToken.None)).Members.Select(m => m.DisplayName));
        Assert.Equal([Me], (await handler.GetAsync(Context, Query(role: "OrganizationOwner"), CancellationToken.None)).Members.Select(m => m.UserId));
        Assert.Equal(["Person 06"], (await handler.GetAsync(Context, Query(membership: "Disabled"), CancellationToken.None)).Members.Select(m => m.DisplayName));
        Assert.Equal(1, (await handler.GetAsync(Context, Query(status: "NeverSignedIn"), CancellationToken.None)).TotalCount);
        await Assert.ThrowsAsync<BusinessException>(() => handler.GetAsync(Context, Query(membership: "Deleted"), CancellationToken.None));
        await Assert.ThrowsAsync<BusinessException>(() => handler.GetAsync(Context, Query(role: "Root"), CancellationToken.None));
    }

    [Fact]
    public async Task ACursorIsBoundToItsFiltersAndCaller()
    {
        var handler = Directory();
        var first = await handler.GetAsync(Context, Query(), CancellationToken.None);
        await Assert.ThrowsAsync<BusinessException>(() => handler.GetAsync(Context, Query(search: "person", token: first.NextToken), CancellationToken.None));
        await Assert.ThrowsAsync<BusinessException>(() => handler.GetAsync(Context with { LocalUserId = Guid.CreateVersion7() }, Query(token: first.NextToken), CancellationToken.None));
    }

    [Fact]
    public async Task OwnerActionHintsMirrorTheAuthorityRulesAndNeverOfferChangesToOneself()
    {
        proposals.Add(new AuthorityOwnerProposal(Guid.CreateVersion7(), members[2].AuthorityUserId, "GrantOwner", Guid.CreateVersion7(), "x", Now, Now.AddHours(24), "Pending", null));
        var all = (await Directory().GetAsync(Context, Query(), CancellationToken.None)).Members.Concat((await Directory().GetAsync(Context, Query(search: "Tess"), CancellationToken.None)).Members).ToList();
        Assert.Empty(all.Single(m => m.UserId == Me).AvailableActions!);
        Assert.Equal(["GrantDesigner", "ProposeOwnerGrant", "DisableMembership"], all.Single(m => m.DisplayName == "Person 00").AvailableActions);
        Assert.Equal(["GrantDesigner", "DisableMembership"], all.Single(m => m.DisplayName == "Person 01").AvailableActions); // a GrantOwner proposal is already pending
        Assert.Contains("RevokeDesigner", all.Single(m => m.DisplayName == "Person 04").AvailableActions!);
        Assert.Equal(["EnableMembership"], all.Single(m => m.DisplayName == "Person 06").AvailableActions);
    }

    [Fact]
    public async Task ASecurityAdministratorWhoIsNotAnOwnerGetsOnlyTheSecurityAdministratorHints()
    {
        ownerAllowed = false;
        securityAllowed = true;
        var page = await Directory().GetAsync(Context, Query(search: "Person 00"), CancellationToken.None);
        Assert.Equal(["AppointSecurityAdministrator"], Assert.Single(page.Members).AvailableActions);
        Assert.False(page.CanManageMembers);
    }

    [Fact]
    public async Task AnUnavailableRosterFallsBackToNamesWithoutRolesOrActions()
    {
        rosterUnavailable = true;
        var page = await Directory().GetAsync(Context, Query(), CancellationToken.None);
        Assert.False(page.RolesAvailable);
        Assert.Equal(GetMemberDirectoryHandler.FallbackCoverage, page.CoverageStatement);
        Assert.Equal(21, page.TotalCount);
        Assert.All(page.Members, m => Assert.Empty(m.AvailableActions!));
        Assert.All(page.Members, m => Assert.Equal("Unknown", m.MembershipState));
    }

    // ---- commands ----

    [Fact]
    public async Task DesignerChangesAreSentAsOneOwnerCommandsWithTheDevelopmentStepUp()
    {
        var target = members[5].AuthorityUserId;
        var outcome = await Commands().GrantDesignerAsync(Context, target, new MemberRoleChangeRequest("Designer", "covers forms", UseDevelopmentStepUp: true), "key-0001", CancellationToken.None);
        Assert.Equal("Effective", outcome.CommandStatus);
        var command = Assert.Single(submitted);
        Assert.Equal(SecurityCommandKind.GrantOrganizationRole, command.CommandKind);
        Assert.Equal("designer", command.CommandPayload.GetProperty("role_name").GetString());
        Assert.Equal(target, command.CommandPayload.GetProperty("authority_user_id").GetGuid());
        Assert.Equal(MemberAdministrationHandlers.SyntheticStepUpMarker, command.CommandPayload.GetProperty("step_up_evidence").GetString());
        await Assert.ThrowsAsync<BusinessException>(() => Commands().GrantDesignerAsync(Context, target, new MemberRoleChangeRequest("OrganizationOwner", "x y z"), "key-0002", CancellationToken.None));
        await Assert.ThrowsAsync<BusinessException>(() => Commands().GrantDesignerAsync(Context, target, new MemberRoleChangeRequest("Designer", " "), "key-0003", CancellationToken.None));
        Assert.Single(submitted); // refused before the Authority is called
    }

    [Theory]
    [InlineData("GrantOwner", SecurityCommandKind.GrantOrganizationRole)]
    [InlineData("RevokeOwner", SecurityCommandKind.RevokeOrganizationRole)]
    [InlineData("DisableOwner", SecurityCommandKind.DisableIdentityBinding)]
    [InlineData("EnableOwner", SecurityCommandKind.EnableIdentityBinding)]
    public async Task ApprovingAProposalSendsTheMatchingCommandWithTheProposalId(string change, SecurityCommandKind expected)
    {
        var proposalId = Guid.CreateVersion7();
        var target = members[7].AuthorityUserId;
        proposals.Add(new AuthorityOwnerProposal(proposalId, target, change, Guid.CreateVersion7(), "board", Now, Now.AddHours(24), "Pending", null));
        await Commands().ApproveProposalAsync(Context, proposalId, new MemberCommandRequest("second owner agrees", true), "key-0004", CancellationToken.None);
        var command = Assert.Single(submitted);
        Assert.Equal(expected, command.CommandKind);
        Assert.Equal(proposalId, command.CommandPayload.GetProperty("proposal_id").GetGuid());
        Assert.Equal(target, command.CommandPayload.GetProperty("authority_user_id").GetGuid());
        await Assert.ThrowsAsync<BusinessException>(() => Commands().ApproveProposalAsync(Context, Guid.CreateVersion7(), new MemberCommandRequest("x y z"), "key-0005", CancellationToken.None));
    }

    [Fact]
    public async Task AMembershipRequestUsesTheOrganizationIssuerFromTheAuthorityAndReturnsTheRequestId()
    {
        var outcome = await Commands().RequestMembershipAsync(Context, new CreateMembershipRequestRequest(" 0f1e2d3c-4b5a-6978-8796-a5b4c3d2e1f0 ", "Nia Newcomer", "Designer", "joins intake", true), "key-0006", CancellationToken.None);
        var command = Assert.Single(submitted);
        Assert.Equal(SecurityCommandKind.RequestOrganizationMembership, command.CommandKind);
        Assert.Equal("https://issuer.invalid", command.CommandPayload.GetProperty("issuer_uri").GetString());
        Assert.Equal("0f1e2d3c-4b5a-6978-8796-a5b4c3d2e1f0", command.CommandPayload.GetProperty("subject_identifier").GetString());
        Assert.Equal("designer", command.CommandPayload.GetProperty("initial_role_name").GetString());
        Assert.Equal(outcome.CommandId, outcome.RequestId);
        await Assert.ThrowsAsync<BusinessException>(() => Commands().RequestMembershipAsync(Context, new CreateMembershipRequestRequest("x", "y", "OrganizationOwner", "a b c"), "key-0007", CancellationToken.None));
    }

    [Fact]
    public async Task APendingCommandIsReportedAsPendingNeverAsDone()
    {
        commandStatus = SecurityCommandStatus.CommittedAwaitingProjection;
        await Assert.ThrowsAsync<PendingCommandException>(() => Commands().SetMembershipAsync(Context, members[9].AuthorityUserId, enable: false, new MemberCommandRequest("left the unit", true), "key-0008", CancellationToken.None));
    }

    /// <summary>Every reading is a minute later, so the awaiter's budget is spent at once (no real waiting in a unit test).</summary>
    private sealed class JumpingClock : TimeProvider
    {
        private DateTimeOffset now = Now;

        public override DateTimeOffset GetUtcNow() => now = now.AddMinutes(1);
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
