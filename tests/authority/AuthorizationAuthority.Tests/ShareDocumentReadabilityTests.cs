using System.Reflection;
using AuthorizationAuthority.Application.Common;
using AuthorizationAuthority.Application.Common.Ports;
using AuthorizationAuthority.Application.Features.Content;
using AuthorizationAuthority.Contracts.V1;
using AuthorizationAuthority.Domain.Features.Content;
using AuthorizationAuthority.Domain.Features.Organizations;
using AuthorizationAuthority.Domain.Features.SecurityCommands;
using AuthorizationAuthority.Domain.Features.Sharing;
using Xunit;

namespace AuthorizationAuthority.Tests;

/// <summary>
/// C10 WP8-A (WP-0 Section 3.6): which attachments a share recipient's verified read releases. Only an attachment covered by a binding
/// of a live share the subject holds, at a verified version within the binding's boundary, is viewable; download needs the flag;
/// another attachment, a newer version, a legacy leaf without evidence, an expired or revoked share, a missing document edge or a
/// share held by someone else releases nothing; an independent live grant keeps access when another expires.
/// </summary>
public sealed class ShareDocumentReadabilityTests
{
    private const string Issuer = "https://issuer.test";
    private static readonly DateTimeOffset Now = new(2026, 11, 26, 12, 0, 0, TimeSpan.Zero);
    private readonly Guid org = Guid.CreateVersion7();
    private readonly Guid record = Guid.CreateVersion7();
    private readonly Guid user = Guid.CreateVersion7();
    private readonly Guid other = Guid.CreateVersion7();
    private readonly Guid team = Guid.CreateVersion7();
    private readonly Guid statement = Guid.CreateVersion7();
    private readonly Guid photo = Guid.CreateVersion7();
    private readonly InMemoryAuthorityStore store = new();
    private readonly ShareAccessExpiryTests.SpiceDb spice;
    private readonly ProjectionIntegrityVerifier verifier;
    private readonly OrganizationAuthorizationState state;

    public ShareDocumentReadabilityTests()
    {
        state = new OrganizationAuthorizationState { OrganizationId = org, AuthorizationRevision = 7, ProjectionAppliedRevision = 7, ProjectionZedToken = "zed-7", CheckpointEnforcementModeName = CheckpointEnforcementModes.Asynchronous }.WithJournalDigest();
        store.Organizations.Add(state);
        store.Resources.Add(new AuthorityResource { OrganizationId = org, ResourceTypeName = ResourceTypeNames.FormRecord, ResourceEntityId = record, ResourceLifecycleState = ResourceLifecycleStates.Active });
        store.Resources.Add(new AuthorityResource { OrganizationId = org, ResourceTypeName = ResourceTypeNames.Team, ResourceEntityId = team, ResourceLifecycleState = ResourceLifecycleStates.Active });
        var proxy = DispatchProxy.Create<ISpiceDbProjection, ShareAccessExpiryTests.SpiceDb>();
        spice = (ShareAccessExpiryTests.SpiceDb)(object)proxy;
        spice.Markers = _ => TestProjection.MarkerFor(state);
        spice.Grants = check => check.ResourceType == ResourceTypeNames.DocumentItem; // the projection allows; the bindings decide
        var transition = new ProjectionMarkerTransitionState();
        transition.MarkCompleted();
        verifier = new ProjectionIntegrityVerifier(store, proxy, transition, new TestProjection.RecordingAlerts(), new ProjectionIntegrityMetrics(), new FixedClock(Now));
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private void Edge(string type, Guid id, string relation, string subjectType, Guid subject, string subjectRelation = "") =>
        store.Edges.Add(new AuthorityRelationshipEdge { AuthorityRelationshipEdgeId = Guid.CreateVersion7(), OrganizationId = org, ResourceTypeName = type, ResourceEntityId = id, RelationshipName = relation, SubjectTypeName = subjectType, SubjectEntityId = subject, SubjectRelationName = subjectRelation });

    private Guid Share(Guid attachment, int through, bool download, Guid? holder = null, bool viaTeam = false, DateTimeOffset? expires = null, bool documentEdge = true)
    {
        var id = Guid.CreateVersion7();
        store.Resources.Add(new AuthorityResource { OrganizationId = org, ResourceTypeName = ResourceTypeNames.RecordShare, ResourceEntityId = id, ParentResourceEntityId = record, ResourceLifecycleState = ResourceLifecycleStates.Active });
        store.ShareMetadata.Add(new ShareResourceMetadata { OrganizationId = org, RecordShareId = id, DelegationDepth = 1, ShareExpiresOn = expires });
        if (viaTeam)
        {
            Edge(ResourceTypeNames.RecordShare, id, "reader", ResourceTypeNames.Team, team, "participant");
        }
        else
        {
            Edge(ResourceTypeNames.RecordShare, id, "reader", ResourceTypeNames.User, holder ?? user);
        }

        Edge(ResourceTypeNames.FormRecord, record, "shared_via", ResourceTypeNames.RecordShare, id);
        if (documentEdge)
        {
            Edge(ResourceTypeNames.DocumentItem, attachment, "shared_via", ResourceTypeNames.RecordShare, id);
        }

        store.DocumentBindings.Add(new ShareDocumentBinding { ShareDocumentBindingId = Guid.CreateVersion7(), OrganizationId = org, RecordShareId = id, FileAttachmentId = attachment, CanViewDocument = true, CanDownloadDocument = download, SharedThroughVersionNumber = through });
        return id;
    }

    private static RecordManifestLeaf Attachment(Guid id, int? version) => new() { ManifestItemId = id, ManifestItemKindCode = (byte)ManifestItemKind.Attachment, AttachmentVersionNumber = version };

    private async Task<(List<Guid> View, List<Guid> Download)> EvaluateAsync(params RecordManifestLeaf[] leaves)
    {
        await using var scope = await store.BeginScopeAsync(Ct);
        var pin = await verifier.PinAsync(scope, (await scope.ReadOrganizationAsync(org, Ct))!, organizationLocked: false, Guid.CreateVersion7(), Ct);
        var (view, download) = await ShareDocumentReadability.EvaluateAsync(verifier, pin, scope, org, record, user, leaves, Ct);
        return (view.Select(i => i.ItemId).ToList(), download.Select(i => i.ItemId).ToList());
    }

    [Fact]
    public async Task OnlyTheBoundAttachmentWithinItsVersionBoundaryIsReleased()
    {
        Share(statement, through: 2, download: true);

        var current = await EvaluateAsync(Attachment(statement, 2), Attachment(photo, 1));
        Assert.Equal([statement], current.View);
        Assert.Equal([statement], current.Download);

        var newer = await EvaluateAsync(Attachment(statement, 3)); // a version added by someone else after the grant
        Assert.Empty(newer.View);

        var earlier = await EvaluateAsync(Attachment(statement, 1)); // a historic version within the boundary
        Assert.Equal([statement], earlier.View);
    }

    [Fact]
    public async Task ViewWithoutTheDownloadFlagIsNotDownloadable()
    {
        Share(statement, through: 1, download: false);

        var result = await EvaluateAsync(Attachment(statement, 1));

        Assert.Equal([statement], result.View);
        Assert.Empty(result.Download);
    }

    [Fact]
    public async Task LegacyLeavesStaleEvidenceAndOtherPeoplesSharesReleaseNothing()
    {
        Share(statement, through: 5, download: true, holder: other);
        Share(photo, through: 5, download: true, documentEdge: false); // binding without a live document edge

        Assert.Empty((await EvaluateAsync(Attachment(statement, 1), Attachment(photo, 1))).View);
        Share(statement, through: 5, download: true);
        Assert.Empty((await EvaluateAsync(Attachment(statement, null))).View); // accepted before WP8: no evidence
    }

    [Fact]
    public async Task AnExpiredOrRevokedShareReleasesNothingButAnIndependentLiveGrantDoes()
    {
        var expired = Share(statement, through: 3, download: true, expires: Now.AddMinutes(-1));
        Assert.Empty((await EvaluateAsync(Attachment(statement, 2))).View);

        var revoked = Share(statement, through: 3, download: true);
        store.Resources.Single(r => r.ResourceEntityId == revoked).ResourceLifecycleState = ResourceLifecycleStates.Revoked;
        Assert.Empty((await EvaluateAsync(Attachment(statement, 2))).View);

        Share(statement, through: 2, download: false, viaTeam: true);
        Edge(ResourceTypeNames.Team, team, "member", ResourceTypeNames.User, user);
        var independent = await EvaluateAsync(Attachment(statement, 2));
        Assert.Equal([statement], independent.View);
        Assert.Empty(independent.Download); // the expired share's download flag does not carry over
        Assert.NotEqual(expired, revoked);
    }

    [Fact]
    public async Task ADeniedProjectionDecisionReleasesNothingWhateverTheBindings()
    {
        Share(statement, through: 3, download: true);
        spice.Grants = _ => false;

        Assert.Empty((await EvaluateAsync(Attachment(statement, 1))).View);
    }
}
