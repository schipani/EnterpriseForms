using System.Text.Json;
using AuthorizationAuthority.Application.Common;
using AuthorizationAuthority.Application.Features.SecurityCommands;
using AuthorizationAuthority.Contracts.V1;
using AuthorizationAuthority.Domain.Features.Content;
using AuthorizationAuthority.Domain.Features.Journal;
using AuthorizationAuthority.Domain.Features.Organizations;
using AuthorizationAuthority.Domain.Features.SecurityCommands;
using AuthorizationAuthority.Domain.Features.Sharing;
using AuthorizationAuthority.Infrastructure.Faults;
using Xunit;

namespace AuthorizationAuthority.Tests;

/// <summary>
/// C10 M1-WP5 (WP-0 Section 3.4, D10-7): narrowing a share's items or role invalidates, in the same accepted UpdateShareScope command,
/// every descendant its new scope no longer covers and everything below it; covered descendants, widening and independent grants are
/// untouched; the invalidations are journalled per share. Driven through SecurityCommandService on the in-memory store.
/// </summary>
public sealed class ShareNarrowingCascadeTests
{
    private static readonly HumanCommandStepUpGate Gate = HumanCommandStepUpGate.Create(HumanCommandStepUpGate.SyntheticMode, "Testing", "TestIdentityProvider");
    private readonly Guid org = Guid.NewGuid();
    private readonly Guid record = Guid.NewGuid();
    private readonly Guid owner = Guid.NewGuid();
    private readonly Guid recipient = Guid.NewGuid();
    private readonly Guid first = Guid.NewGuid();
    private readonly Guid second = Guid.NewGuid();
    private readonly InMemoryAuthorityStore store = new();

    public ShareNarrowingCascadeTests()
    {
        store.Organizations.Add(new OrganizationAuthorizationState { OrganizationId = org, AuthorizationRevision = 5, ProjectionAppliedRevision = 5 });
        store.Bindings.Add(new IdentityBinding { IdentityBindingId = Guid.NewGuid(), OrganizationId = org, AuthorityUserId = owner, IdentityIssuerUri = "https://idp", IdentitySubjectIdentifier = owner.ToString("N"), IdentityBindingEnabled = true });
        store.Resources.Add(new AuthorityResource { OrganizationId = org, ResourceEntityId = record, ResourceTypeName = ResourceTypeNames.FormRecord, ResourceLifecycleState = ResourceLifecycleStates.Active });
        var commit = Guid.NewGuid();
        store.Heads.Add(new RecordContentHead { OrganizationId = org, FormRecordId = record, CurrentRecordManifestCommitId = commit });
        foreach (var item in new[] { first, second })
        {
            store.ManifestLeaves.Add(new RecordManifestLeaf { RecordManifestCommitId = commit, ManifestItemId = item, ManifestItemKindCode = (byte)EnterpriseForms.Integrity.Merkle.MerkleItemKind.FieldValue });
        }

        Edge(ResourceTypeNames.FormRecord, record, "owner", ResourceTypeNames.User, owner);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private Guid Share(string role, Guid[] items, Guid? parent = null)
    {
        var id = Guid.NewGuid();
        store.Resources.Add(new AuthorityResource { OrganizationId = org, ResourceEntityId = id, ResourceTypeName = ResourceTypeNames.RecordShare, ParentResourceEntityId = record, ResourceLifecycleState = ResourceLifecycleStates.Active });
        var depth = parent is { } p ? store.ShareMetadata.Single(m => m.RecordShareId == p).DelegationDepth + 1 : 1;
        store.ShareMetadata.Add(new ShareResourceMetadata { OrganizationId = org, RecordShareId = id, ParentRecordShareId = parent, DelegationDepth = depth });
        Edge(ResourceTypeNames.RecordShare, id, role, ResourceTypeNames.User, recipient);
        Edge(ResourceTypeNames.FormRecord, record, "shared_via", ResourceTypeNames.RecordShare, id);
        foreach (var item in items)
        {
            Edge(ResourceTypeNames.FieldItem, item, "shared_via", ResourceTypeNames.RecordShare, id);
        }

        return id;
    }

    private void Edge(string type, Guid id, string relation, string subjectType, Guid subject) =>
        store.Edges.Add(new AuthorityRelationshipEdge { AuthorityRelationshipEdgeId = Guid.NewGuid(), OrganizationId = org, ResourceTypeName = type, ResourceEntityId = id, RelationshipName = relation, SubjectTypeName = subjectType, SubjectEntityId = subject });

    private Task<OperationStatus> UpdateAsync(Guid share, string role, Guid[] items, string key)
    {
        var payload = new UpdateShareScopePayload(share, record, role, items, store.ShareMetadata.Single(m => m.RecordShareId == share).ShareRevision, HumanCommandStepUpGate.SyntheticStepUpEvidence);
        var request = new SecurityCommandRequest(org, SecurityCommandKind.UpdateShareScope, new ResourceRef(ResourceKind.FormRecord, record), store.Organizations.Single().AuthorizationRevision,
            JsonSerializer.SerializeToElement(payload, CanonicalJson.Options));
        return new SecurityCommandService(store, new NoOpFaultInjector(), TimeProvider.System, FieldSearchApprovalGate.Unavailable, humanCommandGate: Gate)
            .SubmitAsync(new VerifiedActor("https://idp", owner.ToString("N"), "test"), request, key, Guid.NewGuid(), Ct);
    }

    private string State(Guid share) => store.Resources.Single(r => r.ResourceEntityId == share).ResourceLifecycleState;

    private bool HasLiveEdges(Guid share) => store.Edges.Any(e => (e.ResourceEntityId == share || e.SubjectEntityId == share) && e.RemovedBySecurityCommandId is null);

    [Fact]
    public async Task NarrowingItemsInvalidatesUncoveredDescendantsAndTheirSubtreeInOneCommand()
    {
        var parent = Share("manager", [first, second]);
        var uncovered = Share("manager", [second], parent);
        var below = Share("reader", [second], uncovered);
        var covered = Share("reader", [first], parent);
        var independent = Share("reader", [second]);

        var status = await UpdateAsync(parent, RecipientRoles.Manager, [first], "narrow-items-1");

        Assert.Equal(ResourceLifecycleStates.Invalidated, State(uncovered));
        Assert.Equal(ResourceLifecycleStates.Invalidated, State(below));
        Assert.Equal(ResourceLifecycleStates.Active, State(covered));
        Assert.Equal(ResourceLifecycleStates.Active, State(independent));
        Assert.Equal(ShareInvalidationReasons.UpstreamNarrowed, store.ShareMetadata.Single(m => m.RecordShareId == below).ShareInvalidationReasonCode);
        Assert.False(HasLiveEdges(uncovered) || HasLiveEdges(below));
        Assert.True(HasLiveEdges(covered) && HasLiveEdges(independent));

        var command = store.Commands.Single(c => c.SecurityCommandId == status.CommandId);
        Assert.Equal(SecurityCommandStates.CommittedAwaitingProjection, command.CommandStateName);
        Assert.Equal(6, Assert.Single(store.Organizations).AuthorizationRevision); // one command, one revision
        var invalidations = store.Journal.Where(j => j.JournalStreamName == JournalStreamNames.Audit && j.JournalEventKind == ShareLifecycleJournal.ShareInvalidated).ToList();
        Assert.Equal(new[] { uncovered, below }.Order(), invalidations.Select(j => JsonDocument.Parse(j.JournalEventBodyJson).RootElement.GetProperty("body").GetProperty("record_share_id").GetGuid()).Order());
        Assert.All(invalidations, j => Assert.Contains(ShareInvalidationReasons.UpstreamNarrowed, j.JournalEventBodyJson, StringComparison.Ordinal));
        Assert.Single(store.Journal, j => j.JournalEventKind == ShareLifecycleJournal.ShareScopeUpdated);
    }

    [Fact]
    public async Task ADemotionFromManagerInvalidatesEveryDelegatedChild()
    {
        var parent = Share("manager", [first]);
        var child = Share("reader", [first], parent);

        await UpdateAsync(parent, RecipientRoles.Contributor, [first], "demote-role-1");

        Assert.Equal(ResourceLifecycleStates.Invalidated, State(child));
        Assert.Equal(ResourceLifecycleStates.Active, State(parent));
    }

    [Fact]
    public async Task WideningAndAnExpiryOnlyChangeInvalidateNothing()
    {
        var parent = Share("manager", [first]);
        var child = Share("reader", [first], parent);

        await UpdateAsync(parent, RecipientRoles.Manager, [first, second], "widen-scope-1");

        Assert.Equal(ResourceLifecycleStates.Active, State(child));
        Assert.True(HasLiveEdges(child));
        Assert.DoesNotContain(store.Journal, j => j.JournalEventKind == ShareLifecycleJournal.ShareInvalidated);
        Assert.DoesNotContain("invalidated_descendant_share_ids", store.Commands.Single().CommandPayloadJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AStaleRevisionNarrowingInvalidatesNothing()
    {
        var parent = Share("manager", [first, second]);
        var child = Share("reader", [second], parent);
        store.ShareMetadata.Single(m => m.RecordShareId == parent).ShareRevision = 3;
        var payload = new UpdateShareScopePayload(parent, record, RecipientRoles.Manager, [first], 2, HumanCommandStepUpGate.SyntheticStepUpEvidence);
        var request = new SecurityCommandRequest(org, SecurityCommandKind.UpdateShareScope, new ResourceRef(ResourceKind.FormRecord, record), 5, JsonSerializer.SerializeToElement(payload, CanonicalJson.Options));

        var refused = await Assert.ThrowsAsync<AuthorityException>(() => new SecurityCommandService(store, new NoOpFaultInjector(), TimeProvider.System, FieldSearchApprovalGate.Unavailable, humanCommandGate: Gate)
            .SubmitAsync(new VerifiedActor("https://idp", owner.ToString("N"), "test"), request, "narrow-stale-1", Guid.NewGuid(), Ct));

        Assert.Equal(AuthorityErrorCodes.StaleShareRevision, refused.ErrorCode);
        Assert.Equal(ResourceLifecycleStates.Active, State(child));
        Assert.True(HasLiveEdges(child));
    }
}
