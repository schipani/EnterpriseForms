using AuthorizationAuthority.Application.Common;
using AuthorizationAuthority.Application.Features.Sharing;
using AuthorizationAuthority.Contracts.V1;
using AuthorizationAuthority.Domain.Features.Organizations;
using AuthorizationAuthority.Domain.Features.SecurityCommands;
using AuthorizationAuthority.Domain.Features.Sharing;
using Xunit;

namespace AuthorizationAuthority.Tests;

/// <summary>
/// C10 M2 (WP-0 Section 3.5): PG-066 "my access" lists only the caller's own sources (owner, owning team, direct and team shares) with
/// expired shares omitted and expiring-soon flagged; PG-023 effective scope is Active only while the share and its lineage are, and
/// confers nothing otherwise; both fail closed while the gate is closed and conceal others' shares as share details does.
/// </summary>
public sealed class MyAccessAndEffectiveScopeTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 26, 12, 0, 0, TimeSpan.Zero);
    private readonly Guid org = Guid.NewGuid();
    private readonly Guid user = Guid.NewGuid();
    private readonly Guid owner = Guid.NewGuid();
    private readonly Guid stranger = Guid.NewGuid();
    private readonly Guid team = Guid.NewGuid();
    private readonly Guid item = Guid.NewGuid();
    private readonly InMemoryAuthorityStore store = new();

    public MyAccessAndEffectiveScopeTests()
    {
        store.Organizations.Add(new OrganizationAuthorizationState { OrganizationId = org, AuthorizationRevision = 9, ProjectionAppliedRevision = 9, ProjectionZedToken = "zed-9" });
        foreach (var person in new[] { user, owner, stranger })
        {
            store.Bindings.Add(new IdentityBinding { IdentityBindingId = Guid.NewGuid(), OrganizationId = org, AuthorityUserId = person, IdentityIssuerUri = "https://idp", IdentitySubjectIdentifier = person.ToString("N"), IdentityBindingEnabled = true });
        }

        store.Resources.Add(new AuthorityResource { OrganizationId = org, ResourceEntityId = team, ResourceTypeName = ResourceTypeNames.Team, ResourceLifecycleState = ResourceLifecycleStates.Active });
        Edge(ResourceTypeNames.Team, team, "member", ResourceTypeNames.User, user);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private void Edge(string type, Guid id, string relation, string subjectType, Guid subject, string subjectRelation = "") =>
        store.Edges.Add(new AuthorityRelationshipEdge { AuthorityRelationshipEdgeId = Guid.NewGuid(), OrganizationId = org, ResourceTypeName = type, ResourceEntityId = id, RelationshipName = relation, SubjectTypeName = subjectType, SubjectEntityId = subject, SubjectRelationName = subjectRelation });

    private Guid Record(Guid? ownedBy = null, Guid? ownedByTeam = null)
    {
        var id = Guid.NewGuid();
        store.Resources.Add(new AuthorityResource { OrganizationId = org, ResourceEntityId = id, ResourceTypeName = ResourceTypeNames.FormRecord, ResourceLifecycleState = ResourceLifecycleStates.Active });
        if (ownedByTeam is { } t)
        {
            Edge(ResourceTypeNames.FormRecord, id, "owner", ResourceTypeNames.Team, t, "participant");
        }
        else
        {
            Edge(ResourceTypeNames.FormRecord, id, "owner", ResourceTypeNames.User, ownedBy ?? owner);
        }

        return id;
    }

    private Guid Share(Guid record, string role, DateTimeOffset? expires, bool viaTeam = false, Guid? parent = null)
    {
        var id = Guid.NewGuid();
        store.Commands.Add(new SecurityCommand { SecurityCommandId = id, OrganizationId = org, ActorIdentityBindingId = store.Bindings.Single(b => b.AuthorityUserId == owner).IdentityBindingId, CommandKindName = SecurityCommandKinds.CreateShare, CommandStateName = SecurityCommandStates.Effective, CommandIdempotencyKey = "seed-" + id.ToString("N") });
        store.Resources.Add(new AuthorityResource { OrganizationId = org, ResourceEntityId = id, ResourceTypeName = ResourceTypeNames.RecordShare, ParentResourceEntityId = record, ResourceLifecycleState = ResourceLifecycleStates.Active, RegisteredBySecurityCommandId = id });
        var depth = parent is { } p ? store.ShareMetadata.Single(m => m.RecordShareId == p).DelegationDepth + 1 : 1;
        store.ShareMetadata.Add(new ShareResourceMetadata { OrganizationId = org, RecordShareId = id, ParentRecordShareId = parent, DelegationDepth = depth, ShareExpiresOn = expires });
        if (viaTeam)
        {
            Edge(ResourceTypeNames.RecordShare, id, role, ResourceTypeNames.Team, team, "participant");
        }
        else
        {
            Edge(ResourceTypeNames.RecordShare, id, role, ResourceTypeNames.User, user);
        }

        Edge(ResourceTypeNames.FormRecord, record, "shared_via", ResourceTypeNames.RecordShare, id);
        Edge(ResourceTypeNames.FieldItem, item, "shared_via", ResourceTypeNames.RecordShare, id);
        return id;
    }

    private Task<MyAccess> MyAccessAsync(Guid actor) => new MyAccessService(store, new ManualClock(Now)).GetAsync(new VerifiedActor("https://idp", actor.ToString("N"), "test"), org, Ct);

    private Task<ShareEffectiveScope> ScopeAsync(Guid actor, Guid record, Guid share) =>
        new ShareDetailsService(store, new ManualClock(Now)).GetEffectiveScopeAsync(new VerifiedActor("https://idp", actor.ToString("N"), "test"), org, record, share, Ct);

    [Fact]
    public async Task MyAccessListsOnlyTheCallersOwnLiveSourcesAndFlagsExpiringShares()
    {
        var owned = Record(ownedBy: user);
        var teamOwned = Record(ownedByTeam: team);
        var shared = Record();
        var direct = Share(shared, "contributor", Now.AddDays(3));
        var viaTeam = Share(shared, "reader", null, viaTeam: true);
        var expired = Share(Record(), "reader", Now.AddMinutes(-1));
        var expiredParent = Share(Record(), "manager", Now.AddMinutes(-1));
        Share(store.Resources.Single(r => r.ResourceEntityId == expiredParent).ParentResourceEntityId!.Value, "reader", null, parent: expiredParent);
        Record(ownedBy: stranger); // someone else's record

        var access = await MyAccessAsync(user);

        Assert.Contains(access.Entries, e => e is { AccessSource: "Owner" } && e.FormRecordId == owned);
        Assert.Contains(access.Entries, e => e is { AccessSource: "OwningTeam" } && e.FormRecordId == teamOwned && e.TeamId == team);
        var directEntry = Assert.Single(access.Entries, e => e.RecordShareId == direct);
        Assert.Equal(("DirectShare", RecipientRoles.Contributor, true), (directEntry.AccessSource, directEntry.RecipientRole, directEntry.ExpiringSoon));
        Assert.Equal("TeamShare", Assert.Single(access.Entries, e => e.RecordShareId == viaTeam).AccessSource);
        Assert.DoesNotContain(access.Entries, e => e.RecordShareId == expired || e.RecordShareId == expiredParent);
        Assert.Equal(4, access.Entries.Count); // nothing through expired lineage, nothing of other principals
        Assert.DoesNotContain((await MyAccessAsync(stranger)).Entries, e => e.AccessSource != "Owner");
    }

    [Fact]
    public async Task EffectiveScopeIsActiveOnlyWhileTheLineageIsAndOthersAreConcealed()
    {
        var record = Record();
        var parent = Share(record, "manager", Now.AddDays(10));
        var child = Share(record, "reader", Now.AddDays(5), parent: parent);

        var scope = await ScopeAsync(owner, record, child);
        Assert.Equal("Active", scope.Status);
        Assert.Equal([item], scope.FieldItemIds);
        Assert.Equal([child, parent], scope.Lineage.Select(l => l.RecordShareId));
        Assert.Equal(Now.AddDays(5), scope.EffectiveExpiresOn);
        Assert.False(scope.CanDelegate);
        Assert.True((await ScopeAsync(owner, record, parent)).CanDelegate);

        store.ShareMetadata.Single(m => m.RecordShareId == parent).ShareExpiresOn = Now.AddMinutes(-1);
        var expired = await ScopeAsync(owner, record, child);
        Assert.Equal("Expired", expired.Status);
        Assert.Empty(expired.FieldItemIds);
        Assert.Null(expired.RecipientRole);

        Assert.Equal(AuthorityErrorCodes.ResourceNotRegistered, (await Assert.ThrowsAsync<AuthorityException>(() => ScopeAsync(user, record, child))).ErrorCode); // the recipient
    }

    [Fact]
    public async Task OnlySecurityAdministratorsListAMembersDirectlyOwnedActiveRecords()
    {
        var mine = Record(ownedBy: user);
        var archived = Record(ownedBy: user);
        store.Resources.Single(r => r.ResourceEntityId == archived).ResourceLifecycleState = "Archived";
        Record(ownedByTeam: team); // team-owned records are not the member's to reassign
        var service = new OwnedRecordsService(store);

        // C10 independent review F-3: the Organization Owner role confers no record discovery; only the transfer override's role does.
        Edge(ResourceTypeNames.Organization, org, OrganizationRoleNames.OrganizationOwner, ResourceTypeNames.User, owner);
        Assert.Equal(AuthorityErrorCodes.ResourceNotRegistered, (await Assert.ThrowsAsync<AuthorityException>(() =>
            service.GetAsync(new VerifiedActor("https://idp", owner.ToString("N"), "test"), org, user, Ct))).ErrorCode);

        Edge(ResourceTypeNames.Organization, org, OrganizationRoleNames.SystemAdministrator, ResourceTypeNames.User, owner);
        var owned = await service.GetAsync(new VerifiedActor("https://idp", owner.ToString("N"), "test"), org, user, Ct);

        Assert.Equal([mine], owned.FormRecordIds);
        Assert.Equal(AuthorityErrorCodes.ResourceNotRegistered, (await Assert.ThrowsAsync<AuthorityException>(() =>
            service.GetAsync(new VerifiedActor("https://idp", stranger.ToString("N"), "test"), org, user, Ct))).ErrorCode);
    }

    [Fact]
    public async Task BothReadsFailClosedWhileTheGateIsClosed()
    {
        var record = Record();
        var share = Share(record, "reader", null);
        store.Organizations.Single().AuthorizationRevision = 10;

        Assert.NotEqual(AuthorityErrorCodes.ResourceNotRegistered, (await Assert.ThrowsAsync<AuthorityException>(() => MyAccessAsync(user))).ErrorCode);
        Assert.NotEqual(AuthorityErrorCodes.ResourceNotRegistered, (await Assert.ThrowsAsync<AuthorityException>(() => ScopeAsync(owner, record, share))).ErrorCode);
    }
}
