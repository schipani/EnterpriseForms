using AuthorizationAuthority.Application.Common;
using AuthorizationAuthority.Application.Features.Sharing;
using AuthorizationAuthority.Contracts.V1;
using AuthorizationAuthority.Domain.Features.Organizations;
using AuthorizationAuthority.Domain.Features.SecurityCommands;
using AuthorizationAuthority.Domain.Features.Sharing;
using Xunit;

namespace AuthorizationAuthority.Tests;

/// <summary>
/// C10 M2 (WP-0 Section 3.5, D10-3): the authoritative share-details read. Owner and a Manager on the committed lineage read it;
/// the share's own recipient, unrelated users, a former owner and readers of legacy lineage other than the owner get the same
/// answer as for an unknown share; it fails closed while the gate is closed; the share revision is the committed one and stays
/// distinct from the organization revision.
/// </summary>
public sealed class ShareDetailsServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 26, 12, 0, 0, TimeSpan.Zero);
    private readonly Guid org = Guid.NewGuid();
    private readonly Guid record = Guid.NewGuid();
    private readonly Guid owner = Guid.NewGuid();
    private readonly Guid manager = Guid.NewGuid();
    private readonly Guid lead = Guid.NewGuid();
    private readonly Guid recipient = Guid.NewGuid();
    private readonly Guid stranger = Guid.NewGuid();
    private readonly Guid first = Guid.NewGuid();
    private readonly Guid second = Guid.NewGuid();
    private readonly InMemoryAuthorityStore store = new();
    private readonly Dictionary<Guid, Guid> bindings = [];

    public ShareDetailsServiceTests()
    {
        store.Organizations.Add(new OrganizationAuthorizationState { OrganizationId = org, AuthorizationRevision = 9, ProjectionAppliedRevision = 9, ProjectionZedToken = "zed-9" });
        foreach (var user in new[] { owner, manager, lead, recipient, stranger })
        {
            bindings[user] = Guid.NewGuid();
            store.Bindings.Add(new IdentityBinding { IdentityBindingId = bindings[user], OrganizationId = org, AuthorityUserId = user, IdentityIssuerUri = "https://idp", IdentitySubjectIdentifier = user.ToString("N"), IdentityBindingEnabled = true });
        }

        store.Resources.Add(new AuthorityResource { OrganizationId = org, ResourceEntityId = record, ResourceTypeName = ResourceTypeNames.FormRecord, ResourceLifecycleState = ResourceLifecycleStates.Active });
        Edge(ResourceTypeNames.FormRecord, record, "owner", ResourceTypeNames.User, owner);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private void Edge(string type, Guid id, string relation, string subjectType, Guid subject) =>
        store.Edges.Add(new AuthorityRelationshipEdge { AuthorityRelationshipEdgeId = Guid.NewGuid(), OrganizationId = org, ResourceTypeName = type, ResourceEntityId = id, RelationshipName = relation, SubjectTypeName = subjectType, SubjectEntityId = subject });

    private Guid Share(Guid grantor, string role, Guid holder, Guid[] items, Guid? parent = null, DateTimeOffset? expires = null, bool legacy = false, long revision = 1)
    {
        var id = Guid.NewGuid();
        var command = Guid.NewGuid();
        store.Commands.Add(new SecurityCommand { SecurityCommandId = command, OrganizationId = org, ActorIdentityBindingId = bindings[grantor], CommandKindName = SecurityCommandKinds.CreateShare, CommandStateName = SecurityCommandStates.Effective, CommandIdempotencyKey = "seed-" + id.ToString("N") });
        store.Resources.Add(new AuthorityResource { OrganizationId = org, ResourceEntityId = id, ResourceTypeName = ResourceTypeNames.RecordShare, ParentResourceEntityId = record, ResourceLifecycleState = ResourceLifecycleStates.Active, RegisteredBySecurityCommandId = command });
        var depth = parent is { } p ? store.ShareMetadata.Single(m => m.RecordShareId == p).DelegationDepth + 1 : 1;
        store.ShareMetadata.Add(new ShareResourceMetadata { OrganizationId = org, RecordShareId = id, ParentRecordShareId = parent, DelegationDepth = depth, ShareExpiresOn = expires, ShareRevision = revision, LegacyLineageUnresolved = legacy });
        Edge(ResourceTypeNames.RecordShare, id, role, ResourceTypeNames.User, holder);
        Edge(ResourceTypeNames.FormRecord, record, "shared_via", ResourceTypeNames.RecordShare, id);
        foreach (var item in items)
        {
            Edge(ResourceTypeNames.FieldItem, item, "shared_via", ResourceTypeNames.RecordShare, id);
        }

        return id;
    }

    private Task<ShareDetails> ReadAsync(Guid actor, Guid share, Guid? onRecord = null) =>
        new ShareDetailsService(store, new ManualClock(Now)).GetAsync(new VerifiedActor("https://idp", actor.ToString("N"), "test"), org, onRecord ?? record, share, Ct);

    private async Task AssertUnavailableAsync(Guid actor, Guid share, Guid? onRecord = null) =>
        Assert.Equal(AuthorityErrorCodes.ResourceNotRegistered, (await Assert.ThrowsAsync<AuthorityException>(() => ReadAsync(actor, share, onRecord))).ErrorCode);

    [Fact]
    public async Task TheOwnerReadsTheCommittedRevisionScopeLineageAndGrantor()
    {
        var parent = Share(owner, "manager", manager, [first, second], expires: Now.AddDays(10), revision: 4);
        var child = Share(manager, "reader", recipient, [second], parent, expires: Now.AddDays(30), revision: 2);

        var details = await ReadAsync(owner, child);

        Assert.Equal(ResourceLifecycleStates.Active, details.LifecycleState);
        Assert.Equal(("User", recipient, RecipientRoles.Reader), (details.RecipientKind, details.RecipientId!.Value, details.RecipientRole));
        Assert.Equal([second], details.FieldItemIds);
        Assert.Equal(2, details.ShareRevision);
        Assert.Equal(9, details.AuthorizationRevision);
        Assert.Equal(parent, details.ParentRecordShareId);
        Assert.Equal(2, details.DelegationDepth);
        Assert.Equal(Now.AddDays(30), details.ShareExpiresOn);
        Assert.Equal(Now.AddDays(10), details.EffectiveExpiresOn); // lineage minimum
        Assert.Equal(manager, details.GrantorUserId);
    }

    [Fact]
    public async Task AManagerOnAnyCommittedAncestorReadsButTheRecipientAndOthersDoNot()
    {
        var root = Share(owner, "manager", lead, [first]);
        var middle = Share(lead, "manager", manager, [first], root);
        var leaf = Share(manager, "reader", recipient, [first], middle);

        Assert.Equal(3, (await ReadAsync(lead, leaf)).DelegationDepth);
        Assert.Equal(leaf, (await ReadAsync(manager, leaf)).RecordShareId);
        await AssertUnavailableAsync(recipient, leaf);
        await AssertUnavailableAsync(stranger, leaf);
        await AssertUnavailableAsync(manager, middle); // its own recipient, not a holder of its parent
        await AssertUnavailableAsync(lead, root);
    }

    [Fact]
    public async Task AnExpiredAncestorConfersNoReadAuthority()
    {
        var parent = Share(owner, "manager", manager, [first], expires: Now.AddMinutes(-1));
        var child = Share(manager, "reader", recipient, [first], parent);

        await AssertUnavailableAsync(manager, child);
        Assert.Equal(Now.AddMinutes(-1), (await ReadAsync(owner, child)).EffectiveExpiresOn);
    }

    [Fact]
    public async Task UnresolvedLegacyLineageIsReadableOnlyByTheOwner()
    {
        var managerShare = Share(owner, "manager", manager, [first]);
        var legacy = Share(manager, "reader", recipient, [first], legacy: true);

        var details = await ReadAsync(owner, legacy);

        Assert.True(details.LegacyLineageUnresolved);
        Assert.Null(details.ParentRecordShareId);
        Assert.Null(details.EffectiveExpiresOn);
        await AssertUnavailableAsync(manager, legacy);
        Assert.NotNull(managerShare.ToString());
    }

    [Fact]
    public async Task AFormerOwnerLosesTheRead()
    {
        var share = Share(owner, "reader", recipient, [first]);
        store.Edges.Single(e => e.ResourceTypeName == ResourceTypeNames.FormRecord && e.RelationshipName == "owner").RemovedBySecurityCommandId = Guid.NewGuid();
        Edge(ResourceTypeNames.FormRecord, record, "owner", ResourceTypeNames.User, stranger);

        await AssertUnavailableAsync(owner, share);
        Assert.Equal(owner, (await ReadAsync(stranger, share)).GrantorUserId);
    }

    [Fact]
    public async Task ARevokedShareShowsItsStateWithoutRecipientOrItems()
    {
        var share = Share(owner, "reader", recipient, [first]);
        store.Resources.Single(r => r.ResourceEntityId == share).ResourceLifecycleState = ResourceLifecycleStates.Revoked;
        foreach (var edge in store.Edges.Where(e => e.ResourceEntityId == share || e.SubjectEntityId == share))
        {
            edge.RemovedBySecurityCommandId = Guid.NewGuid();
        }

        var details = await ReadAsync(owner, share);

        Assert.Equal(ResourceLifecycleStates.Revoked, details.LifecycleState);
        Assert.Null(details.RecipientId);
        Assert.Null(details.RecipientRole);
        Assert.Empty(details.FieldItemIds);
    }

    [Fact]
    public async Task UnknownSharesAndSharesOfAnotherRecordGetTheSameAnswer()
    {
        var share = Share(owner, "reader", recipient, [first]);
        var otherRecord = Guid.NewGuid();
        store.Resources.Add(new AuthorityResource { OrganizationId = org, ResourceEntityId = otherRecord, ResourceTypeName = ResourceTypeNames.FormRecord, ResourceLifecycleState = ResourceLifecycleStates.Active });
        Edge(ResourceTypeNames.FormRecord, otherRecord, "owner", ResourceTypeNames.User, owner);

        await AssertUnavailableAsync(owner, Guid.NewGuid());
        await AssertUnavailableAsync(owner, share, otherRecord);
    }

    [Fact]
    public async Task DocumentsWithALiveEdgeAppearWithTheirFlagsAndBoundary()
    {
        var share = Share(owner, "reader", recipient, [first]);
        var shared = Guid.NewGuid();
        var dropped = Guid.NewGuid();
        Edge(ResourceTypeNames.DocumentItem, shared, "shared_via", ResourceTypeNames.RecordShare, share);
        store.DocumentBindings.Add(new ShareDocumentBinding { ShareDocumentBindingId = Guid.NewGuid(), OrganizationId = org, RecordShareId = share, FileAttachmentId = shared, CanViewDocument = true, CanDownloadDocument = true, SharedThroughVersionNumber = 4 });
        store.DocumentBindings.Add(new ShareDocumentBinding { ShareDocumentBindingId = Guid.NewGuid(), OrganizationId = org, RecordShareId = share, FileAttachmentId = dropped, CanViewDocument = true, SharedThroughVersionNumber = 1 }); // no live edge

        var document = Assert.Single((await ReadAsync(owner, share)).Documents!);

        Assert.Equal((shared, true, true, false, 4), (document.FileAttachmentId, document.CanViewDocument, document.CanDownloadDocument, document.CanReplaceDocument, document.SharedThroughVersionNumber));
    }

    [Fact]
    public async Task AClosedGateFailsClosed()
    {
        var share = Share(owner, "reader", recipient, [first]);
        store.Organizations.Single().AuthorizationRevision = 10; // committed, not yet projected

        var refused = await Assert.ThrowsAsync<AuthorityException>(() => ReadAsync(owner, share));

        Assert.NotEqual(AuthorityErrorCodes.ResourceNotRegistered, refused.ErrorCode);
    }
}
