using System.Reflection;
using EnterpriseForms.Application.Common;
using EnterpriseForms.Application.Common.Authority;
using EnterpriseForms.Application.Common.Persistence;
using EnterpriseForms.Application.Features.Teams.ManageTeam;
using EnterpriseForms.Application.Features.Teams.MembershipReconciliation;
using EnterpriseForms.Domain.Features.Teams;
using Xunit;

namespace EnterpriseForms.Api.ContractTests;

/// <summary>
/// Phase C8 AC-9 review correction N-2: a team-membership change is authorized by a preliminary Authority check BEFORE a durable
/// operation is persisted, so an unauthorized or foreign-organization caller cannot fill the operations table with rejected
/// commands. The check widens nothing (it asks for the same two principals the Authority's team-command rule accepts) and the
/// Authority still decides the command itself: a change of authorization after the check is refused at submit and recorded as
/// Rejected. Fast, deterministic stand-ins; the live-stack behaviour stays in the Stage B suite.
/// </summary>
public sealed class TeamMembershipPreliminaryAuthorizationTests
{
    private static readonly Guid OrganizationA = Guid.CreateVersion7();
    private static readonly Guid OrganizationB = Guid.CreateVersion7();

    private sealed class Harness
    {
        public Harness(Func<Guid, ProtectedAction, bool> allow, Exception? submitFailure = null)
        {
            Authority = Stubs.Create<IAuthorizationAuthorityClient>((method, args) =>
            {
                switch (method.Name)
                {
                    case nameof(IAuthorizationAuthorityClient.CheckPermissionsBatchAsync):
                        Checks++;
                        return Task.FromResult<IReadOnlyList<PermissionDecision>>(((IReadOnlyList<PermissionCheckRequest>)args[1]!)
                            .Select(r => new PermissionDecision(allow(r.OrganizationId, r.RequestedAction), null, 7, 7, null, Guid.Empty)).ToList());
                    case nameof(IAuthorizationAuthorityClient.SubmitSecurityCommandAsync):
                        Submits++;
                        return submitFailure is null
                            ? throw new NotSupportedException("the success path is covered by the live-stack suite")
                            : Task.FromException<SecurityCommandResult>(submitFailure);
                    default:
                        throw new NotSupportedException(method.Name);
                }
            });
            Store = Stubs.Create<IBusinessStore>((method, _) =>
            {
                if (method.Name != nameof(IBusinessStore.BeginScopeAsync))
                {
                    throw new NotSupportedException(method.Name);
                }

                Scopes++;
                return Task.FromResult(Stubs.Create<IBusinessScope>((m, a) =>
                {
                    switch (m.Name)
                    {
                        case nameof(IBusinessScope.FindAuthorityCommandOperationByKeyAsync):
                            return Task.FromResult<AuthorityCommandOperation?>(Persisted);
                        case nameof(IBusinessScope.TryAddAuthorityCommandOperationAsync):
                            Persisted = (AuthorityCommandOperation)a[0]!;
                            return Task.FromResult(true);
                        case nameof(IBusinessScope.FindAuthorityCommandOperationAsync):
                            return Task.FromResult<AuthorityCommandOperation?>(Persisted);
                        case nameof(IBusinessScope.CommitAsync):
                            return Task.CompletedTask;
                        case nameof(IAsyncDisposable.DisposeAsync):
                            return default(ValueTask);
                        default:
                            throw new NotSupportedException(m.Name);
                    }
                }));
            });
            Handler = new ChangeTeamMembershipHandler(Store, Authority, new AuthorityCommandAwaiter(Authority, TimeProvider.System), new TeamMembershipReconciliationOptions(), TimeProvider.System);
        }

        public IAuthorizationAuthorityClient Authority { get; }

        public IBusinessStore Store { get; }

        public ChangeTeamMembershipHandler Handler { get; }

        public int Checks { get; private set; }

        public int Submits { get; private set; }

        public int Scopes { get; private set; }

        public AuthorityCommandOperation? Persisted { get; private set; }

        public Task<EnterpriseForms.Contracts.Features.TeamMembershipChangeResponse> RunAsync(Guid organization, bool add = true) =>
            Handler.HandleAsync(new RequestContext(organization, Guid.CreateVersion7(), "https://issuer.test", "subject", "Caller", "assertion", Guid.CreateVersion7()), Guid.CreateVersion7(), Guid.CreateVersion7(), add, "key-" + Guid.NewGuid().ToString("N"), CancellationToken.None);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task N2ACallerThatIsNeitherOwnerNorSecurityAdministratorPersistsNothingAndSubmitsNothing(bool add)
    {
        var harness = new Harness((_, _) => false);

        var refused = await Assert.ThrowsAsync<BusinessException>(() => harness.RunAsync(OrganizationA, add));

        Assert.Equal(403, refused.HttpStatusCode);
        Assert.Equal(1, harness.Checks);
        Assert.Equal(0, harness.Scopes);
        Assert.Null(harness.Persisted);
        Assert.Equal(0, harness.Submits);
    }

    [Fact]
    public async Task N2AnAdministratorOfAnotherOrganizationPersistsNothingInThisOne()
    {
        // Authority decisions are organization-bound: allowed in A, denied in B.
        var harness = new Harness((organization, _) => organization == OrganizationA);

        var refused = await Assert.ThrowsAsync<BusinessException>(() => harness.RunAsync(OrganizationB));

        Assert.Equal(403, refused.HttpStatusCode);
        Assert.Equal(0, harness.Scopes);
        Assert.Null(harness.Persisted);
        Assert.Equal(0, harness.Submits);
    }

    [Theory]
    [InlineData(ProtectedAction.ManageOrganization)]
    [InlineData(ProtectedAction.AdministerSecurity)]
    public async Task N2EitherAdministrativePrincipalPassesTheGateAndTheAuthorityStillDecides(ProtectedAction allowed)
    {
        // The Authority refuses at submit (for example a Security Administrator adding themself, G25, or access removed after the
        // preliminary check): the operation was recorded, then closed as Rejected; nothing is projected or re-sent.
        var harness = new Harness((_, action) => action == allowed, new AuthorityClientException("FORBIDDEN", 403, false, "refused"));

        await Assert.ThrowsAsync<AuthorityClientException>(() => harness.RunAsync(OrganizationA));

        Assert.Equal(1, harness.Submits);
        Assert.NotNull(harness.Persisted);
        Assert.Equal(AuthorityCommandOperationStates.Rejected, harness.Persisted.OperationStateName);
        Assert.Equal("FORBIDDEN", harness.Persisted.RejectionReasonCode);
        Assert.Equal(OrganizationA, harness.Persisted.TenantId);
    }

    [Fact]
    public async Task N2AuthorizationRevokedBetweenTheCheckAndTheCommandIsRefusedByTheAuthorityAndRecordedRejected()
    {
        var harness = new Harness((_, _) => true, new AuthorityClientException("REVISION_CONFLICT", 409, false, "authorization changed"));

        var refused = await Assert.ThrowsAsync<AuthorityClientException>(() => harness.RunAsync(OrganizationA, add: false));

        Assert.Equal(409, refused.HttpStatusCode);
        Assert.Equal(AuthorityCommandOperationStates.Rejected, harness.Persisted!.OperationStateName);
        Assert.Equal(1, harness.Submits);
    }

    // ---- stand-ins (DispatchProxy) ----

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
