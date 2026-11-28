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
/// C10 ownership-transfer decision (owner, 2026-11-26), reconciled with WP-0 Section 3.4 and CLAUDE.md Section 4B: a transfer
/// removes only the former owner's owner rights. A share is either a <b>durable grant</b> — owner-granted, depth 1, no parent,
/// authorized once by its committed <c>CreateShare</c> — or a <b>delegated grant</b> that depends on its committed parent
/// Manager share continuing to be held. Neither depends on the grantor's continuing ownership, so a transfer invalidates no
/// share; it does end the former owner's ability to create, update or revoke shares under owner rights. Legacy shares with
/// unresolved lineage are neither reparented nor invalidated, and only the current owner revokes them. Explicit revocations
/// stay revoked. Driven through <see cref="SecurityCommandService"/> on the in-memory store.
/// </summary>
public sealed class OwnershipTransferShareTests
{
    private static readonly HumanCommandStepUpGate Gate = HumanCommandStepUpGate.Create(HumanCommandStepUpGate.SyntheticMode, "Testing", "TestIdentityProvider");
    private readonly Guid org = Guid.NewGuid();
    private readonly Guid record = Guid.NewGuid();
    private readonly Guid formerOwner = Guid.NewGuid();
    private readonly Guid newOwner = Guid.NewGuid();
    private readonly Guid manager = Guid.NewGuid();
    private readonly Guid recipient = Guid.NewGuid();
    private readonly Guid teamMember = Guid.NewGuid();
    private readonly Guid team = Guid.NewGuid();
    private readonly Guid item = Guid.NewGuid();
    private readonly InMemoryAuthorityStore store = new();
    private readonly Dictionary<Guid, Guid> bindings = [];

    public OwnershipTransferShareTests()
    {
        store.Organizations.Add(new OrganizationAuthorizationState { OrganizationId = org, AuthorizationRevision = 5, ProjectionAppliedRevision = 5 });
        foreach (var user in new[] { formerOwner, newOwner, manager, recipient, teamMember })
        {
            bindings[user] = Guid.NewGuid();
            store.Bindings.Add(new IdentityBinding { IdentityBindingId = bindings[user], OrganizationId = org, AuthorityUserId = user, IdentityIssuerUri = "https://idp", IdentitySubjectIdentifier = user.ToString("N"), IdentityBindingEnabled = true });
        }

        store.Resources.Add(new AuthorityResource { OrganizationId = org, ResourceEntityId = record, ResourceTypeName = ResourceTypeNames.FormRecord, ResourceLifecycleState = ResourceLifecycleStates.Active });
        store.Resources.Add(new AuthorityResource { OrganizationId = org, ResourceEntityId = team, ResourceTypeName = ResourceTypeNames.Team, ResourceLifecycleState = ResourceLifecycleStates.Active });
        Edge(ResourceTypeNames.Team, team, "member", ResourceTypeNames.User, teamMember);
        var commit = Guid.NewGuid();
        store.Heads.Add(new RecordContentHead { OrganizationId = org, FormRecordId = record, CurrentRecordManifestCommitId = commit });
        store.ManifestLeaves.Add(new RecordManifestLeaf { RecordManifestCommitId = commit, ManifestItemId = item, ManifestItemKindCode = (byte)EnterpriseForms.Integrity.Merkle.MerkleItemKind.FieldValue });
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private long Revision => store.Organizations.Single().AuthorizationRevision;

    private void Edge(string type, Guid id, string relation, string subjectType, Guid subject, string subjectRelation = "") =>
        store.Edges.Add(new AuthorityRelationshipEdge { AuthorityRelationshipEdgeId = Guid.NewGuid(), OrganizationId = org, ResourceTypeName = type, ResourceEntityId = id, RelationshipName = relation, SubjectTypeName = subjectType, SubjectEntityId = subject, SubjectRelationName = subjectRelation });

    private void OwnedByUser(Guid user) => Edge(ResourceTypeNames.FormRecord, record, "owner", ResourceTypeNames.User, user);

    private void OwnedByTeam() => Edge(ResourceTypeNames.FormRecord, record, "owner", ResourceTypeNames.Team, team, "participant");

    /// <summary>A committed share as the planner would have registered it; <paramref name="legacy"/> leaves its lineage unresolved.</summary>
    private Guid Share(Guid grantor, string role, Guid recipientUser, Guid? parent = null, bool legacy = false)
    {
        var id = Guid.NewGuid();
        var command = Guid.NewGuid();
        store.Commands.Add(new SecurityCommand { SecurityCommandId = command, OrganizationId = org, ActorIdentityBindingId = bindings[grantor], CommandKindName = SecurityCommandKinds.CreateShare, CommandStateName = SecurityCommandStates.Effective, CommandIdempotencyKey = "seed-" + id.ToString("N") });
        store.Resources.Add(new AuthorityResource { OrganizationId = org, ResourceEntityId = id, ResourceTypeName = ResourceTypeNames.RecordShare, ParentResourceEntityId = record, ResourceLifecycleState = ResourceLifecycleStates.Active, RegisteredBySecurityCommandId = command });
        var depth = parent is { } p ? store.ShareMetadata.Single(m => m.RecordShareId == p).DelegationDepth + 1 : 1;
        store.ShareMetadata.Add(new ShareResourceMetadata { OrganizationId = org, RecordShareId = id, ParentRecordShareId = parent, DelegationDepth = depth, ShareRevision = 1, LegacyLineageUnresolved = legacy });
        Edge(ResourceTypeNames.RecordShare, id, role, ResourceTypeNames.User, recipientUser);
        Edge(ResourceTypeNames.FormRecord, record, "shared_via", ResourceTypeNames.RecordShare, id);
        Edge(ResourceTypeNames.FieldItem, item, "shared_via", ResourceTypeNames.RecordShare, id);
        return id;
    }

    private SecurityCommandService Service() => new(store, new NoOpFaultInjector(), TimeProvider.System, FieldSearchApprovalGate.Unavailable, humanCommandGate: Gate);

    private Task<OperationStatus> Submit(Guid actor, SecurityCommandKind kind, object payload, string key) =>
        Service().SubmitAsync(new VerifiedActor("https://idp", actor.ToString("N"), "test"),
            new SecurityCommandRequest(org, kind, new ResourceRef(ResourceKind.FormRecord, record), Revision, JsonSerializer.SerializeToElement(payload, CanonicalJson.Options)),
            key, Guid.NewGuid(), Ct);

    private Task<OperationStatus> TransferAsync(Guid actor, Guid? toUser, Guid? toTeam = null) =>
        Submit(actor, SecurityCommandKind.TransferOwnership, new TransferOwnershipPayload(record, toUser, toTeam, HumanCommandStepUpGate.SyntheticStepUpEvidence), "transfer-" + Guid.NewGuid().ToString("N")[..12]);

    private Task<OperationStatus> RevokeAsync(Guid actor, Guid share) =>
        Submit(actor, SecurityCommandKind.RevokeShare, new RevokeSharePayload(share, record, HumanCommandStepUpGate.SyntheticStepUpEvidence), "revoke-" + Guid.NewGuid().ToString("N")[..12]);

    private Task<OperationStatus> UpdateAsync(Guid actor, Guid share, Guid? parent = null) =>
        Submit(actor, SecurityCommandKind.UpdateShareScope, new UpdateShareScopePayload(share, record, RecipientRoles.Reader, [item], store.ShareMetadata.Single(m => m.RecordShareId == share).ShareRevision, HumanCommandStepUpGate.SyntheticStepUpEvidence, parent),
            "update-" + Guid.NewGuid().ToString("N")[..12]);

    private Task<OperationStatus> CreateAsync(Guid actor, Guid recipientUser, Guid? parent = null) =>
        Submit(actor, SecurityCommandKind.CreateShare, new CreateSharePayload(Guid.NewGuid(), record, recipientUser, null, RecipientRoles.Reader, [item], HumanCommandStepUpGate.SyntheticStepUpEvidence, parent),
            "create-" + Guid.NewGuid().ToString("N")[..12]);

    private string State(Guid share) => store.Resources.Single(r => r.ResourceEntityId == share).ResourceLifecycleState;

    private bool HasLiveEdges(Guid share) => store.Edges.Any(e => (e.ResourceEntityId == share || e.SubjectEntityId == share) && e.RemovedBySecurityCommandId is null);

    private bool IsOwner(string subjectType, Guid subject) =>
        store.Edges.Any(e => e.ResourceTypeName == ResourceTypeNames.FormRecord && e.ResourceEntityId == record && e.RelationshipName == "owner" &&
                             e.SubjectTypeName == subjectType && e.SubjectEntityId == subject && e.RemovedBySecurityCommandId is null);

    private bool AnyInvalidationJournalled() => store.Journal.Any(j => j.JournalEventKind == ShareLifecycleJournal.ShareInvalidated);

    [Fact]
    public async Task DurableAndDelegatedGrantsSurviveATransferWhileOwnerRightsMove()
    {
        OwnedByUser(formerOwner);
        var durable = Share(formerOwner, "reader", recipient);
        var managerShare = Share(formerOwner, "manager", manager);
        var delegated = Share(manager, "reader", recipient, managerShare);

        await TransferAsync(formerOwner, newOwner);

        Assert.False(IsOwner(ResourceTypeNames.User, formerOwner));
        Assert.True(IsOwner(ResourceTypeNames.User, newOwner));
        foreach (var share in new[] { durable, managerShare, delegated })
        {
            Assert.Equal(ResourceLifecycleStates.Active, State(share));
            Assert.True(HasLiveEdges(share));
            Assert.Null(store.ShareMetadata.Single(m => m.RecordShareId == share).ShareInvalidationReasonCode);
        }

        Assert.False(AnyInvalidationJournalled());
        Assert.Equal(managerShare, store.ShareMetadata.Single(m => m.RecordShareId == delegated).ParentRecordShareId);
    }

    [Fact]
    public async Task TheFormerOwnerCannotRevokeUpdateOrGrantUnderOwnerRightsAfterTheTransfer()
    {
        OwnedByUser(formerOwner);
        var durable = Share(formerOwner, "reader", recipient);
        await TransferAsync(formerOwner, newOwner);

        Assert.Equal(AuthorityErrorCodes.Forbidden, (await Assert.ThrowsAsync<AuthorityException>(() => RevokeAsync(formerOwner, durable))).ErrorCode);
        Assert.Equal(AuthorityErrorCodes.Forbidden, (await Assert.ThrowsAsync<AuthorityException>(() => UpdateAsync(formerOwner, durable))).ErrorCode);
        await Assert.ThrowsAsync<AuthorityException>(() => CreateAsync(formerOwner, recipient));
        await Assert.ThrowsAsync<AuthorityException>(() => TransferAsync(formerOwner, formerOwner));
        Assert.Equal(ResourceLifecycleStates.Active, State(durable));
        Assert.True(HasLiveEdges(durable));
    }

    [Fact]
    public async Task TheNewOwnerManagesTheFormerOwnersGrantsAndRevocationCascades()
    {
        OwnedByUser(formerOwner);
        var managerShare = Share(formerOwner, "manager", manager);
        var delegated = Share(manager, "reader", recipient, managerShare);
        await TransferAsync(formerOwner, newOwner);

        await RevokeAsync(newOwner, managerShare);

        Assert.Equal(ResourceLifecycleStates.Revoked, State(managerShare));
        Assert.Equal(ResourceLifecycleStates.Invalidated, State(delegated));
        Assert.Equal(ShareInvalidationReasons.UpstreamRevoked, store.ShareMetadata.Single(m => m.RecordShareId == delegated).ShareInvalidationReasonCode);
    }

    [Fact]
    public async Task ADelegatedGrantorKeepsAuthorityThatDoesNotDependOnOwnership()
    {
        OwnedByUser(formerOwner);
        var managerShare = Share(formerOwner, "manager", manager);
        var delegated = Share(manager, "reader", recipient, managerShare);
        await TransferAsync(formerOwner, newOwner);

        await RevokeAsync(manager, delegated);

        Assert.Equal(ResourceLifecycleStates.Revoked, State(delegated));
        Assert.Equal(ResourceLifecycleStates.Active, State(managerShare));
    }

    [Fact]
    public async Task AGrantorWhoNoLongerHoldsTheCommittedParentCannotRevokeUnderIt()
    {
        OwnedByUser(formerOwner);
        var managerShare = Share(formerOwner, "manager", manager);
        var delegated = Share(manager, "reader", recipient, managerShare);
        // The Manager role is removed from the parent out of band of the child (e.g. by an earlier command); the child remains.
        foreach (var edge in store.Edges.Where(e => e.ResourceEntityId == managerShare && e.RelationshipName == "manager"))
        {
            edge.RemovedBySecurityCommandId = Guid.NewGuid();
        }

        var refused = await Assert.ThrowsAsync<AuthorityException>(() => RevokeAsync(manager, delegated));

        Assert.Equal(AuthorityErrorCodes.Forbidden, refused.ErrorCode);
        Assert.Equal(ResourceLifecycleStates.Active, State(delegated));
    }

    [Fact]
    public async Task ADelegatedGrantorCannotRevokeAnotherGrantorsShareUnderTheSameParent()
    {
        OwnedByUser(formerOwner);
        var managerShare = Share(formerOwner, "manager", manager);
        Edge(ResourceTypeNames.RecordShare, managerShare, "manager", ResourceTypeNames.User, teamMember);
        var others = Share(teamMember, "reader", recipient, managerShare);

        var refused = await Assert.ThrowsAsync<AuthorityException>(() => RevokeAsync(manager, others));

        Assert.Equal(AuthorityErrorCodes.Forbidden, refused.ErrorCode);
        Assert.Equal(ResourceLifecycleStates.Active, State(others));
    }

    [Fact]
    public async Task UnresolvedLegacyLineageIsNeitherReparentedNorInvalidatedAndOnlyTheOwnerRevokesIt()
    {
        OwnedByUser(formerOwner);
        var legacyByOwner = Share(formerOwner, "reader", recipient, legacy: true);
        var managerShare = Share(formerOwner, "manager", manager);
        var legacyByManager = Share(manager, "reader", recipient, legacy: true);

        await TransferAsync(formerOwner, newOwner);

        foreach (var share in new[] { legacyByOwner, legacyByManager })
        {
            var metadata = store.ShareMetadata.Single(m => m.RecordShareId == share);
            Assert.Equal(ResourceLifecycleStates.Active, State(share));
            Assert.True(metadata.LegacyLineageUnresolved);
            Assert.Null(metadata.ParentRecordShareId);
            Assert.Null(metadata.ShareInvalidationReasonCode);
        }

        Assert.False(AnyInvalidationJournalled());
        await Assert.ThrowsAsync<AuthorityException>(() => RevokeAsync(formerOwner, legacyByOwner));
        await Assert.ThrowsAsync<AuthorityException>(() => RevokeAsync(manager, legacyByManager)); // holds a Manager share, but no committed parent links them
        Assert.Equal(ResourceLifecycleStates.Active, State(legacyByManager));
        Assert.Equal(ResourceLifecycleStates.Active, State(managerShare));

        await RevokeAsync(newOwner, legacyByOwner);
        await RevokeAsync(newOwner, legacyByManager);
        Assert.Equal(ResourceLifecycleStates.Revoked, State(legacyByOwner));
        Assert.Equal(ResourceLifecycleStates.Revoked, State(legacyByManager));
    }

    [Fact]
    public async Task AnExplicitRevocationStaysRevokedAcrossTransfersBackAndForth()
    {
        OwnedByUser(formerOwner);
        var revoked = Share(formerOwner, "reader", recipient);
        await RevokeAsync(formerOwner, revoked);
        var edgesAfterRevoke = store.Edges.Count(e => (e.ResourceEntityId == revoked || e.SubjectEntityId == revoked) && e.RemovedBySecurityCommandId is null);

        await TransferAsync(formerOwner, newOwner);
        await TransferAsync(newOwner, formerOwner);

        Assert.Equal(0, edgesAfterRevoke);
        Assert.Equal(ResourceLifecycleStates.Revoked, State(revoked));
        Assert.False(HasLiveEdges(revoked));
        Assert.True(IsOwner(ResourceTypeNames.User, formerOwner));
    }

    [Fact]
    public async Task AFormerOwnerKeepsAccessOnlyThroughAnIndependentShareTheyHold()
    {
        OwnedByUser(formerOwner);
        var heldByFormerOwner = Share(manager, "reader", formerOwner, Share(formerOwner, "manager", manager));

        await TransferAsync(formerOwner, newOwner);

        Assert.False(IsOwner(ResourceTypeNames.User, formerOwner));
        Assert.Equal(ResourceLifecycleStates.Active, State(heldByFormerOwner));
        Assert.Contains(store.Edges, e => e.ResourceEntityId == heldByFormerOwner && e.RelationshipName == "reader" && e.SubjectEntityId == formerOwner && e.RemovedBySecurityCommandId is null);
    }

    [Fact]
    public async Task ATeamOwnedRecordsMemberGrantsSurviveButTheMemberLosesOwnerAuthority()
    {
        OwnedByTeam();
        var byMember = Share(teamMember, "reader", recipient);

        await TransferAsync(teamMember, newOwner);

        Assert.False(IsOwner(ResourceTypeNames.Team, team));
        Assert.Equal(ResourceLifecycleStates.Active, State(byMember));
        Assert.True(HasLiveEdges(byMember));
        Assert.Equal(AuthorityErrorCodes.Forbidden, (await Assert.ThrowsAsync<AuthorityException>(() => RevokeAsync(teamMember, byMember))).ErrorCode);
    }

    [Fact]
    public async Task TheTransferIsOneCommandOneRevisionAndNamesNoShare()
    {
        OwnedByUser(formerOwner);
        Share(formerOwner, "reader", recipient);

        var status = await TransferAsync(formerOwner, newOwner);

        Assert.Equal(6, Revision);
        var command = store.Commands.Single(c => c.SecurityCommandId == status.CommandId);
        Assert.Equal(SecurityCommandKinds.TransferOwnership, command.CommandKindName);
        Assert.DoesNotContain("invalidated_descendant_share_ids", command.CommandPayloadJson, StringComparison.Ordinal);
        Assert.DoesNotContain(HumanCommandStepUpGate.SyntheticStepUpEvidence, command.CommandPayloadJson, StringComparison.Ordinal);
        Assert.DoesNotContain(store.Journal, j => j.JournalStreamName == JournalStreamNames.Audit && j.JournalEventKind.StartsWith("Share", StringComparison.Ordinal));
    }
}
