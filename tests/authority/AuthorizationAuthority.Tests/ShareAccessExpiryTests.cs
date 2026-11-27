using System.Reflection;
using AuthorizationAuthority.Application.Common;
using AuthorizationAuthority.Application.Common.Ports;
using AuthorizationAuthority.Application.Features.Authorization;
using AuthorizationAuthority.Contracts.V1;
using AuthorizationAuthority.Domain.Features.Organizations;
using AuthorizationAuthority.Domain.Features.SecurityCommands;
using AuthorizationAuthority.Domain.Features.Sharing;
using Xunit;

namespace AuthorizationAuthority.Tests;

/// <summary>
/// C10 M1-WP2 (WP-0 Section 3.1, D10-1, D10-10; Section 4 test 9): a share confers nothing from its effective expiry instant, before
/// any convergence command removes its edges, on every decision the verifier releases: permission checks (records and field items)
/// and lookups (discover). Only an Allow can be turned into a Deny; owner paths and live shares keep access; projection comparisons
/// see the raw projection; continuations are bound to the earliest live-share expiry. SpiceDB is a stub that answers as a projection
/// without expiry would.
/// </summary>
public sealed class ShareAccessExpiryTests
{
    private const string Issuer = "https://issuer.test";
    private static readonly DateTimeOffset Now = new(2026, 11, 26, 12, 0, 0, TimeSpan.Zero);

    public class SpiceDb : DispatchProxy
    {
        /// <summary>What the projection (no expiry) answers.</summary>
        public Func<SpiceDbCheck, bool> Grants { get; set; } = _ => false;

        public List<string> LookupResult { get; set; } = [];

        public Func<Guid, MarkerObservation>? Markers { get; set; }

        public List<SpiceDbCheck> Asked { get; } = [];

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            switch (targetMethod?.Name)
            {
                case nameof(ISpiceDbProjection.ReadMarkerAsync):
                    return Task.FromResult(Markers!((Guid)args![0]!));
                case nameof(ISpiceDbProjection.CheckAsync):
                    return Task.FromResult(Answer((SpiceDbCheck)args![0]!));
                case nameof(ISpiceDbProjection.CheckBulkAsync):
                case nameof(ISpiceDbProjection.CheckBulkStrictAsync):
                    return Task.FromResult<IReadOnlyList<SpiceDbCheckOutcome>>([.. ((IReadOnlyList<SpiceDbCheck>)args![0]!).Select(Answer)]);
                case nameof(ISpiceDbProjection.LookupResourcesAsync):
                    return Task.FromResult<IReadOnlyList<string>>([.. LookupResult]);
                default:
                    throw new NotSupportedException("unexpected call " + targetMethod?.Name);
            }
        }

        private SpiceDbCheckOutcome Answer(SpiceDbCheck check)
        {
            Asked.Add(check);
            return Grants(check) ? SpiceDbCheckOutcome.Allowed : SpiceDbCheckOutcome.Denied;
        }
    }

    private sealed class SteppableClock(DateTimeOffset start) : TimeProvider
    {
        public DateTimeOffset Value { get; set; } = start;

        public override DateTimeOffset GetUtcNow() => Value;
    }

    private sealed class World
    {
        public Guid Org { get; } = Guid.CreateVersion7();
        public Guid Record { get; } = Guid.CreateVersion7();
        public Guid OtherRecord { get; } = Guid.CreateVersion7();
        public Guid User { get; } = Guid.CreateVersion7();
        public Guid Field { get; } = Guid.CreateVersion7();
        public InMemoryAuthorityStore Store { get; } = new();
        public SpiceDb Spice { get; }
        public SteppableClock Clock { get; } = new(Now);
        public PermissionCheckService Service { get; }
        public ProjectionIntegrityVerifier Verifier { get; }
        public VerifiedActor Actor => new(Issuer, User.ToString("N"), "test");

        public World()
        {
            var state = new OrganizationAuthorizationState { OrganizationId = Org, AuthorizationRevision = 7, ProjectionAppliedRevision = 7, ProjectionZedToken = "zed-7", CheckpointEnforcementModeName = CheckpointEnforcementModes.Asynchronous }.WithJournalDigest();
            Store.Organizations.Add(state);
            Store.Bindings.Add(new IdentityBinding { IdentityBindingId = Guid.CreateVersion7(), OrganizationId = Org, AuthorityUserId = User, IdentityIssuerUri = Issuer, IdentitySubjectIdentifier = User.ToString("N"), IdentityBindingEnabled = true });
            foreach (var record in new[] { Record, OtherRecord })
            {
                Store.Resources.Add(new AuthorityResource { OrganizationId = Org, ResourceTypeName = ResourceTypeNames.FormRecord, ResourceEntityId = record, ResourceLifecycleState = ResourceLifecycleStates.Active });
            }

            var proxy = DispatchProxy.Create<ISpiceDbProjection, SpiceDb>();
            Spice = (SpiceDb)(object)proxy;
            Spice.Markers = _ => TestProjection.MarkerFor(state);
            var transition = new ProjectionMarkerTransitionState();
            transition.MarkCompleted();
            Verifier = new ProjectionIntegrityVerifier(Store, proxy, transition, new TestProjection.RecordingAlerts(), new ProjectionIntegrityMetrics(), Clock);
            Service = new PermissionCheckService(Store, Verifier);
        }

        /// <summary>A share of <paramref name="record"/> with one recipient (a user, or a team's participants), reaching the record and the field.</summary>
        public Guid Share(string role, DateTimeOffset? expiresOn, Guid? record = null, Guid? team = null, Guid? parent = null, Guid? recipient = null)
        {
            var id = Guid.CreateVersion7();
            var on = record ?? Record;
            Store.Resources.Add(new AuthorityResource { OrganizationId = Org, ResourceTypeName = ResourceTypeNames.RecordShare, ResourceEntityId = id, ParentResourceEntityId = on, ResourceLifecycleState = ResourceLifecycleStates.Active });
            var depth = parent is { } p ? Store.ShareMetadata.Single(m => m.RecordShareId == p).DelegationDepth + 1 : 1;
            Store.ShareMetadata.Add(new ShareResourceMetadata { OrganizationId = Org, RecordShareId = id, ShareExpiresOn = expiresOn, ParentRecordShareId = parent, DelegationDepth = depth });
            Edge(ResourceTypeNames.RecordShare, id, role, team is null ? ResourceTypeNames.User : ResourceTypeNames.Team, team ?? recipient ?? User, team is null ? "" : "participant");
            Edge(ResourceTypeNames.FormRecord, on, "shared_via", ResourceTypeNames.RecordShare, id);
            Edge(ResourceTypeNames.FieldItem, Field, "shared_via", ResourceTypeNames.RecordShare, id);
            return id;
        }

        public void Edge(string type, Guid id, string relation, string subjectType, Guid subject, string subjectRelation = "") =>
            Store.Edges.Add(new AuthorityRelationshipEdge
            {
                AuthorityRelationshipEdgeId = Guid.CreateVersion7(), OrganizationId = Org, ResourceTypeName = type, ResourceEntityId = id,
                RelationshipName = relation, SubjectTypeName = subjectType, SubjectEntityId = subject, SubjectRelationName = subjectRelation,
            });

        public async Task<bool> AllowedAsync(ResourceKind kind, Guid resource, RequestedAction action) =>
            (await Service.CheckAsync(Actor, new PermissionCheckRequest(Org, new ResourceRef(kind, resource), action), Guid.CreateVersion7(), TestContext.Current.CancellationToken)).Allowed;

        /// <summary>The projection answers as if every share still conferred access, and the user owns nothing unless told.</summary>
        public void ProjectionGrantsThroughShares(bool owner = false) =>
            Spice.Grants = c => c.Permission == "effective_owner" ? owner : c.SubjectId == User.ToString("D");
    }

    [Fact]
    public async Task AnExpiredShareConfersNoFieldReadDiscoverOrManageAccessBeforeConvergence()
    {
        var w = new World();
        w.Share("manager", Now.AddMinutes(-1));
        w.ProjectionGrantsThroughShares();

        Assert.False(await w.AllowedAsync(ResourceKind.FieldItem, w.Field, RequestedAction.Read));
        Assert.False(await w.AllowedAsync(ResourceKind.FieldItem, w.Field, RequestedAction.Edit));
        Assert.False(await w.AllowedAsync(ResourceKind.FormRecord, w.Record, RequestedAction.Discover));
        Assert.False(await w.AllowedAsync(ResourceKind.FormRecord, w.Record, RequestedAction.ManageAccess));
        // Each refusal was decided by an owner-path check at the same pinned snapshot, not by a cached answer.
        Assert.Contains(w.Spice.Asked, c => c.Permission == "effective_owner" && c.ResourceId == w.Record.ToString("D") && c.SubjectId == w.User.ToString("D"));
    }

    [Fact]
    public async Task TheExpiryInstantItselfDeniesAndOneTickBeforeItAllows()
    {
        var w = new World();
        w.Share("reader", Now.AddTicks(1));
        w.ProjectionGrantsThroughShares();
        Assert.True(await w.AllowedAsync(ResourceKind.FieldItem, w.Field, RequestedAction.Read));

        w.Clock.Value = Now.AddTicks(1);
        Assert.False(await w.AllowedAsync(ResourceKind.FieldItem, w.Field, RequestedAction.Read));
    }

    [Fact]
    public async Task AnOwnerKeepsAccessAndALiveShareKeepsAccessDespiteAnExpiredOne()
    {
        var owner = new World();
        owner.Share("reader", Now.AddDays(-1));
        owner.ProjectionGrantsThroughShares(owner: true);
        Assert.True(await owner.AllowedAsync(ResourceKind.FieldItem, owner.Field, RequestedAction.Read));

        var live = new World();
        live.Share("reader", Now.AddDays(-1));
        live.Share("reader", Now.AddDays(5));
        live.ProjectionGrantsThroughShares();
        Assert.True(await live.AllowedAsync(ResourceKind.FieldItem, live.Field, RequestedAction.Read));
        Assert.DoesNotContain(live.Spice.Asked, c => c.Permission == "effective_owner"); // a live share decides without an owner check

        // A live reader share does not keep an edit that only the expired contributor share granted.
        var narrower = new World();
        narrower.Share("contributor", Now.AddDays(-1));
        narrower.Share("reader", null);
        narrower.ProjectionGrantsThroughShares();
        Assert.True(await narrower.AllowedAsync(ResourceKind.FieldItem, narrower.Field, RequestedAction.Read));
        Assert.False(await narrower.AllowedAsync(ResourceKind.FieldItem, narrower.Field, RequestedAction.Edit));
    }

    [Fact]
    public async Task ATeamShareExpiresForItsMembersAndADescendantExpiresWithItsAncestor()
    {
        var team = new World();
        var teamId = Guid.CreateVersion7();
        team.Edge(ResourceTypeNames.Team, teamId, "member", ResourceTypeNames.User, team.User);
        team.Share("reader", Now.AddHours(-2), team: teamId);
        team.ProjectionGrantsThroughShares();
        Assert.False(await team.AllowedAsync(ResourceKind.FieldItem, team.Field, RequestedAction.Read));

        var lineage = new World();
        var other = Guid.CreateVersion7();
        var parent = lineage.Share("manager", Now.AddHours(-1), recipient: other);
        lineage.Share("reader", null, parent: parent); // the descendant's own expiry is absent: the lineage minimum applies
        lineage.ProjectionGrantsThroughShares();
        Assert.False(await lineage.AllowedAsync(ResourceKind.FieldItem, lineage.Field, RequestedAction.Read));
    }

    [Fact]
    public async Task TheOverlayNeverTurnsADenyIntoAnAllowAndLeavesUnrelatedDecisionsAlone()
    {
        var w = new World();
        w.Share("reader", Now.AddDays(-1));
        w.Spice.Grants = c => c.Permission == "effective_owner"; // the user owns the record but the projection denies the field
        Assert.False(await w.AllowedAsync(ResourceKind.FieldItem, w.Field, RequestedAction.Read));

        // Another record, and owner-only permissions, are never affected by the expired share.
        w.ProjectionGrantsThroughShares();
        Assert.True(await w.AllowedAsync(ResourceKind.FormRecord, w.OtherRecord, RequestedAction.Discover));
        Assert.True(await w.AllowedAsync(ResourceKind.FormRecord, w.Record, RequestedAction.Read));
    }

    [Fact]
    public async Task WithoutAnyExpiringShareNoExtraDecisionIsMade()
    {
        var w = new World();
        w.Share("reader", null);
        w.ProjectionGrantsThroughShares();
        Assert.True(await w.AllowedAsync(ResourceKind.FieldItem, w.Field, RequestedAction.Read));
        Assert.Single(w.Spice.Asked);
    }

    [Fact]
    public async Task DiscoverDropsRecordsReachedOnlyThroughAnExpiredShare()
    {
        var w = new World();
        w.Share("reader", Now.AddMinutes(-5));
        w.Share("reader", null, record: w.OtherRecord);
        w.Spice.LookupResult = [w.Record.ToString("D"), w.OtherRecord.ToString("D")];
        w.ProjectionGrantsThroughShares();

        var page = await w.Service.DiscoverAsync(w.Actor, new DiscoverRecordsRequest(w.Org, 50, null), TestContext.Current.CancellationToken);
        Assert.Equal([w.OtherRecord], page.FormRecordIds);

        var owner = new World();
        owner.Share("reader", Now.AddMinutes(-5));
        owner.Spice.LookupResult = [owner.Record.ToString("D")];
        owner.ProjectionGrantsThroughShares(owner: true);
        Assert.Equal([owner.Record], (await owner.Service.DiscoverAsync(owner.Actor, new DiscoverRecordsRequest(owner.Org, 50, null), TestContext.Current.CancellationToken)).FormRecordIds);
    }

    [Fact]
    public async Task ADiscoverContinuationEndsAtTheEarliestLiveShareExpiry()
    {
        var w = new World();
        w.Share("reader", Now.AddHours(1));
        w.Share("reader", null, record: w.OtherRecord);
        w.Spice.LookupResult = [w.Record.ToString("D"), w.OtherRecord.ToString("D")];
        w.ProjectionGrantsThroughShares();
        var ct = TestContext.Current.CancellationToken;

        var first = await w.Service.DiscoverAsync(w.Actor, new DiscoverRecordsRequest(w.Org, 1, null), ct);
        Assert.Equal(3, first.ContinuationToken!.Split(':').Length);
        Assert.Single((await w.Service.DiscoverAsync(w.Actor, new DiscoverRecordsRequest(w.Org, 1, first.ContinuationToken), ct)).FormRecordIds);

        w.Clock.Value = Now.AddHours(1);
        var stale = await Assert.ThrowsAsync<AuthorityException>(() => w.Service.DiscoverAsync(w.Actor, new DiscoverRecordsRequest(w.Org, 1, first.ContinuationToken), ct));
        Assert.Equal(AuthorityErrorCodes.ContinuationInvalidated, stale.ErrorCode);
        Assert.Equal(409, stale.HttpStatusCode);

        var malformed = await Assert.ThrowsAsync<AuthorityException>(() => w.Service.DiscoverAsync(w.Actor, new DiscoverRecordsRequest(w.Org, 1, "7:1:not-ticks"), ct));
        Assert.Equal(AuthorityErrorCodes.InvalidCommand, malformed.ErrorCode);
    }

    [Fact]
    public async Task AProjectionComparisonSeesTheRawProjection()
    {
        var w = new World();
        w.Share("reader", Now.AddDays(-1));
        w.ProjectionGrantsThroughShares();
        await using var scope = await w.Store.BeginScopeAsync(TestContext.Current.CancellationToken);
        var state = (await scope.ReadOrganizationAsync(w.Org, TestContext.Current.CancellationToken))!;
        var pin = await w.Verifier.PinAsync(scope, state, organizationLocked: false, Guid.CreateVersion7(), TestContext.Current.CancellationToken);
        var check = new SpiceDbCheck(ResourceTypeNames.FieldItem, w.Field.ToString("D"), "read", ResourceTypeNames.User, w.User.ToString("D"));

        Assert.Equal(SpiceDbCheckOutcome.Allowed, (await w.Verifier.CheckBulkForProjectionComparisonAsync(pin, [check], TestContext.Current.CancellationToken))[0]);
        Assert.Equal(SpiceDbCheckOutcome.Denied, (await w.Verifier.CheckBulkAsync(pin, [check], TestContext.Current.CancellationToken))[0]);
        Assert.Equal(SpiceDbCheckOutcome.Denied, (await w.Verifier.CheckBulkStrictAsync(pin, [check], TestContext.Current.CancellationToken))[0]);
    }

    [Fact]
    public async Task TheOverlayReportsHoldersAndTheNextExpiry()
    {
        var w = new World();
        var teamId = Guid.CreateVersion7();
        var stranger = Guid.CreateVersion7();
        w.Edge(ResourceTypeNames.Team, teamId, "member", ResourceTypeNames.User, w.User);
        w.Share("reader", Now.AddMinutes(-1), team: teamId);
        w.Share("reader", Now.AddHours(3), record: w.OtherRecord);
        w.Share("reader", Now.AddHours(1), record: w.OtherRecord, recipient: stranger);
        await using var scope = await w.Store.BeginScopeAsync(TestContext.Current.CancellationToken);

        var overlay = await ShareExpiryOverlay.LoadAsync(scope, w.Org, Now, TestContext.Current.CancellationToken);

        Assert.False(overlay.IsEmpty);
        Assert.True(overlay.UserHoldsExpiredShare(w.User));
        Assert.False(overlay.UserHoldsExpiredShare(stranger));
        Assert.Equal(Now.AddHours(3), overlay.NextExpiryFor(w.User));
        Assert.Equal(Now.AddHours(1), overlay.NextExpiryFor(stranger));
        Assert.Equal(ShareExpiryOverlay.Verdict.OwnerPathRequired, overlay.Evaluate(ResourceTypeNames.FieldItem, w.Field, "read", ResourceTypeNames.Team, teamId, "participant").Verdict);
        Assert.Equal(ShareExpiryOverlay.Verdict.Unaffected, overlay.Evaluate(ResourceTypeNames.FormRecord, w.Record, "read", ResourceTypeNames.User, w.User, "").Verdict);

        var revoked = await ShareExpiryOverlay.LoadAsync(scope, Guid.CreateVersion7(), Now, TestContext.Current.CancellationToken);
        Assert.True(revoked.IsEmpty); // another organization's shares are never read
    }

    [Fact]
    public async Task TheOverlayNamesExpiredSharesAndTheirDescendantsButNoLiveShare()
    {
        var w = new World();
        var expiredParent = w.Share("manager", Now.AddMinutes(-5));
        var descendant = w.Share("reader", Now.AddDays(5), parent: expiredParent);
        var live = w.Share("reader", Now.AddDays(5));
        await using var scope = await w.Store.BeginScopeAsync(TestContext.Current.CancellationToken);
        var state = (await scope.ReadOrganizationAsync(w.Org, TestContext.Current.CancellationToken))!;
        var pin = await w.Verifier.PinAsync(scope, state, organizationLocked: false, Guid.CreateVersion7(), TestContext.Current.CancellationToken);

        var expired = (await ProjectionIntegrityVerifier.ShareExpiryAsync(pin, TestContext.Current.CancellationToken)).ExpiredShareIds;

        Assert.Contains(expiredParent, expired);
        Assert.Contains(descendant, expired); // the lineage minimum: a descendant confers nothing once its ancestor expired
        Assert.DoesNotContain(live, expired);
    }
}
