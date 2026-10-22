using System.Reflection;
using AuthorizationAuthority.Application.Common;
using AuthorizationAuthority.Application.Common.Ports;
using AuthorizationAuthority.Application.Features.Authorization;
using AuthorizationAuthority.Contracts.V1;
using AuthorizationAuthority.Domain.Features.Organizations;
using Xunit;

namespace AuthorizationAuthority.Tests;

/// <summary>
/// C-28: discovery returns every discoverable record (no 1,000-result cap), pages it deterministically, fails closed above
/// the ceiling or when the authorization state moved during the lookup, and invalidates stale continuations distinctly.
/// </summary>
public sealed class DiscoveryCompletenessTests
{
    private static readonly Guid Org = Guid.CreateVersion7();
    private static readonly Guid User = Guid.CreateVersion7();
    private static readonly VerifiedActor Actor = new("https://issuer.invalid", "subject-1", "enterpriseforms-api");

    /// <summary>Interface stand-in: calls are answered by name; anything unexpected fails the test loudly.</summary>
    public class Fake<T> : DispatchProxy
        where T : class
    {
        public Dictionary<string, Func<object?[]?, object?>> Handlers { get; } = new(StringComparer.Ordinal);

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
            targetMethod is not null && Handlers.TryGetValue(targetMethod.Name, out var handler) ? handler(args) : throw new NotSupportedException("unexpected call " + targetMethod?.Name);
    }

    private static (T Instance, Fake<T> Control) CreateFake<T>()
        where T : class
    {
        var instance = DispatchProxy.Create<T, Fake<T>>();
        return (instance, (Fake<T>)(object)instance);
    }

    private sealed class Harness
    {
        public OrganizationAuthorizationState State { get; } = new() { OrganizationId = Org, AuthorizationRevision = 41, ProjectionAppliedRevision = 41, ProjectionZedToken = "zed-41", CheckpointEnforcementModeName = CheckpointEnforcementModes.Asynchronous };

        public List<Guid> Registered { get; } = [];

        public List<string> Discoverable { get; } = [];

        public int ReadCount { get; private set; }

        /// <summary>Applied after the first organization read (i.e. while the lookup runs).</summary>
        public Action<OrganizationAuthorizationState>? DuringLookup { get; set; }

        public Exception? LookupFailure { get; set; }

        public int? MaximumRequested { get; private set; }

        public PermissionCheckService Build()
        {
            var (scope, scopeControl) = CreateFake<IAuthorityScope>();
            scopeControl.Handlers["FindIdentityBindingAsync"] = _ => Task.FromResult<IdentityBinding?>(new IdentityBinding { OrganizationId = Org, AuthorityUserId = User, IdentityIssuerUri = Actor.IssuerUri, IdentitySubjectIdentifier = Actor.SubjectIdentifier, IdentityBindingEnabled = true });
            scopeControl.Handlers["ReadOrganizationAsync"] = _ =>
            {
                if (ReadCount++ == 1)
                {
                    DuringLookup?.Invoke(State);
                }

                return Task.FromResult<OrganizationAuthorizationState?>(new OrganizationAuthorizationState
                {
                    OrganizationId = Org, AuthorizationRevision = State.AuthorizationRevision, ProjectionAppliedRevision = State.ProjectionAppliedRevision, ProjectionZedToken = State.ProjectionZedToken,
                    CheckpointEnforcementModeName = State.CheckpointEnforcementModeName, IntegrityQuarantineEnabled = State.IntegrityQuarantineEnabled,
                });
            };
            scopeControl.Handlers["GetActiveRecordIdsAsync"] = _ => Task.FromResult(Registered.ToList());
            scopeControl.Handlers["DisposeAsync"] = _ => ValueTask.CompletedTask;
            var (store, storeControl) = CreateFake<IAuthorityStore>();
            storeControl.Handlers["BeginScopeAsync"] = _ => Task.FromResult(scope);
            var (spiceDb, spiceControl) = CreateFake<ISpiceDbProjection>();
            spiceControl.Handlers["LookupResourcesAsync"] = args =>
            {
                Assert.Equal("discover", args![1]);
                Assert.Equal(User.ToString("D"), args[3]);
                Assert.Equal("zed-41", args[4]);
                MaximumRequested = (int)args[5]!;
                return LookupFailure is null ? Task.FromResult<IReadOnlyList<string>>(Discoverable.ToList()) : Task.FromException<IReadOnlyList<string>>(LookupFailure);
            };
            return new PermissionCheckService(store, spiceDb);
        }
    }

    private static Harness WithRecords(int count)
    {
        var harness = new Harness();
        for (var i = 0; i < count; i++)
        {
            var id = Guid.CreateVersion7();
            harness.Registered.Add(id);
            harness.Discoverable.Add(id.ToString("D"));
        }

        harness.Discoverable.Reverse(); // SpiceDB order is not id order
        return harness;
    }

    [Fact]
    public async Task EveryDiscoverableRecordIsReturnedInIdOrderBeyondTheOldThousandCap()
    {
        var harness = WithRecords(1_500);
        var service = harness.Build();
        var seen = new List<Guid>();
        string? token = null;
        do
        {
            var page = await service.DiscoverAsync(Actor, new DiscoverRecordsRequest(Org, 250, token), CancellationToken.None);
            Assert.Equal(41, page.AuthorizationRevision);
            seen.AddRange(page.FormRecordIds);
            token = page.ContinuationToken;
        }
        while (token is not null);

        Assert.Equal(harness.Registered.Order().ToList(), seen);
        Assert.Equal(PermissionCheckService.MaximumDiscoverableRecords, harness.MaximumRequested);
    }

    [Fact]
    public async Task UnregisteredOrMalformedLookupResultsAreNeverListed()
    {
        var harness = WithRecords(3);
        harness.Discoverable.Add(Guid.CreateVersion7().ToString("D")); // projected but not registered (or retired)
        harness.Discoverable.Add("not-a-record-id");
        var page = await harness.Build().DiscoverAsync(Actor, new DiscoverRecordsRequest(Org, 250, null), CancellationToken.None);
        Assert.Equal(harness.Registered.Order().ToList(), page.FormRecordIds);
        Assert.Null(page.ContinuationToken);
    }

    [Fact]
    public async Task MoreThanTheCeilingFailsClosedWithoutAPartialPage()
    {
        var harness = WithRecords(10);
        harness.LookupFailure = new SpiceDbLookupLimitExceededException("over");
        var failure = await Assert.ThrowsAsync<AuthorityException>(() => harness.Build().DiscoverAsync(Actor, new DiscoverRecordsRequest(Org, 250, null), CancellationToken.None));
        Assert.Equal((AuthorityErrorCodes.LimitExceeded, 422), (failure.ErrorCode, failure.HttpStatusCode));
    }

    [Fact]
    public async Task AuthorizationStateMovingDuringTheLookupFailsClosed()
    {
        var harness = WithRecords(10);
        harness.DuringLookup = state => state.AuthorizationRevision = 42; // a revoke committed while SpiceDB was read
        var failure = await Assert.ThrowsAsync<AuthorityException>(() => harness.Build().DiscoverAsync(Actor, new DiscoverRecordsRequest(Org, 250, null), CancellationToken.None));
        Assert.Equal(AuthorityErrorCodes.AuthorityNotReady, failure.ErrorCode);
        Assert.True(failure.Retryable);

        var quarantined = WithRecords(10);
        quarantined.DuringLookup = state => state.IntegrityQuarantineEnabled = true;
        Assert.Equal(AuthorityErrorCodes.AuthorityNotReady, (await Assert.ThrowsAsync<AuthorityException>(() => quarantined.Build().DiscoverAsync(Actor, new DiscoverRecordsRequest(Org, 250, null), CancellationToken.None))).ErrorCode);
    }

    [Fact]
    public async Task ContinuationsFromAnEarlierRevisionAreInvalidatedDistinctly()
    {
        var harness = WithRecords(300);
        var service = harness.Build();
        var stale = await Assert.ThrowsAsync<AuthorityException>(() => service.DiscoverAsync(Actor, new DiscoverRecordsRequest(Org, 250, "40:250"), CancellationToken.None));
        Assert.Equal((AuthorityErrorCodes.ContinuationInvalidated, 409, true), (stale.ErrorCode, stale.HttpStatusCode, stale.Retryable));

        var malformed = await Assert.ThrowsAsync<AuthorityException>(() => WithRecords(1).Build().DiscoverAsync(Actor, new DiscoverRecordsRequest(Org, 250, "41:-3"), CancellationToken.None));
        Assert.Equal(AuthorityErrorCodes.InvalidCommand, malformed.ErrorCode);
    }

    [Fact]
    public async Task AClosedGateRefusesDiscoveryBeforeAnyLookup()
    {
        var harness = WithRecords(5);
        harness.State.ProjectionAppliedRevision = 40; // committed revision 41 not yet projected
        var failure = await Assert.ThrowsAsync<AuthorityException>(() => harness.Build().DiscoverAsync(Actor, new DiscoverRecordsRequest(Org, 250, null), CancellationToken.None));
        Assert.Equal(AuthorityErrorCodes.AuthorityNotReady, failure.ErrorCode);
        Assert.Null(harness.MaximumRequested);
    }
}
