using AuthorizationAuthority.Application.Common;
using AuthorizationAuthority.Application.Common.Ports;
using AuthorizationAuthority.Application.Features.Authorization;
using AuthorizationAuthority.Application.Features.SecurityCommands;
using AuthorizationAuthority.Contracts.V1;
using AuthorizationAuthority.Domain.Features.Organizations;

namespace AuthorizationAuthority.Application.Features.Sharing;

/// <summary>
/// C10 M2 (WP-0 Section 3.5): the authoritative details of one share, from the Authority's own committed state and returned only
/// while the organization gate is open, so a committed change is never reported before it is projected (A11) and the share
/// revision is the one <c>UpdateShareScope</c> compares against. Readers: the record's current owner (or an owning-team member),
/// and a holder of an Active Manager role on a share in the target's committed lineage whose own lineage is Active now (this
/// includes the share's delegated grantor while they still hold its parent). A share's own recipient, a former owner and every
/// other caller get the same answer as for an unknown share. Unresolved legacy lineage names no ancestor, so only the owner reads it.
/// </summary>
public sealed class ShareDetailsService(IAuthorityStore store, TimeProvider clock)
{
    private const int MaximumLineageDepth = 3;

    public async Task<ShareDetails> GetAsync(VerifiedActor actor, Guid organizationId, Guid formRecordId, Guid recordShareId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);
        await using var scope = await store.BeginScopeAsync(cancellationToken).ConfigureAwait(false);
        var bound = await ActorResolver.BindAsync(scope, organizationId, actor, cancellationToken).ConfigureAwait(false);
        var state = await scope.ReadOrganizationAsync(organizationId, cancellationToken).ConfigureAwait(false)
                    ?? throw AuthorityException.NotRegistered("organization is not registered");
        OrganizationGate.RequireOpen(await OrganizationGate.EvaluateAsync(scope, state, cancellationToken).ConfigureAwait(false));
        var now = clock.GetUtcNow();

        var record = await scope.FindResourceAsync(organizationId, ResourceTypeNames.FormRecord, formRecordId, cancellationToken).ConfigureAwait(false);
        var share = await scope.FindResourceAsync(organizationId, ResourceTypeNames.RecordShare, recordShareId, cancellationToken).ConfigureAwait(false);
        var metadata = await scope.FindShareMetadataAsync(organizationId, recordShareId, cancellationToken).ConfigureAwait(false);
        if (record is not { ResourceLifecycleState: ResourceLifecycleStates.Active } || share is null || share.OrganizationId != organizationId ||
            share.ParentResourceEntityId != formRecordId || metadata is null || metadata.OrganizationId != organizationId || metadata.RecordShareId != recordShareId)
        {
            throw Unavailable();
        }

        var graph = new DesiredStateGraph(await scope.GetActiveEdgesAsync(organizationId, cancellationToken).ConfigureAwait(false));
        var chain = await ReadLineageAsync(scope, organizationId, graph, formRecordId, recordShareId, cancellationToken).ConfigureAwait(false);
        if (!graph.IsRecordOwner(bound.AuthorityUserId, formRecordId) && !await ManagesAnAncestorAsync(scope, graph, organizationId, formRecordId, bound.AuthorityUserId, metadata, chain, now, cancellationToken).ConfigureAwait(false))
        {
            throw Unavailable();
        }

        var roleEdge = graph.EdgesOf(ResourceTypeNames.RecordShare, recordShareId).Where(e => e.RelationshipName is "reader" or "contributor" or "manager").Take(2).ToArray();
        var holder = share.ResourceLifecycleState == ResourceLifecycleStates.Active && roleEdge.Length == 1 ? roleEdge[0] : null;
        var effective = metadata.LegacyLineageUnresolved ? null : ShareLineageExpiry.Evaluate(chain, organizationId, formRecordId, recordShareId, now).EffectiveExpiry;
        // C10 WP8: documents with a live edge and a binding (an inactive share holds none).
        var documents = holder is null ? [] : (await scope.GetShareDocumentBindingsAsync(organizationId, [recordShareId], cancellationToken).ConfigureAwait(false))
            .Where(b => b.OrganizationId == organizationId && b.RecordShareId == recordShareId &&
                        graph.EdgesOf(ResourceTypeNames.DocumentItem, b.FileAttachmentId, "shared_via").Any(e => e.SubjectTypeName == ResourceTypeNames.RecordShare && e.SubjectEntityId == recordShareId))
            .OrderBy(b => b.FileAttachmentId)
            .Select(b => new ShareDocumentDetail(b.FileAttachmentId, b.CanViewDocument, b.CanDownloadDocument, b.CanReplaceDocument, b.SharedThroughVersionNumber))
            .ToList();
        var creator = await scope.FindCommandAsync(share.RegisteredBySecurityCommandId, cancellationToken).ConfigureAwait(false);
        var grantor = creator is { } command && command.OrganizationId == organizationId
            ? (await scope.FindIdentityBindingByIdAsync(command.ActorIdentityBindingId, cancellationToken).ConfigureAwait(false)) is { } binding && binding.OrganizationId == organizationId ? binding.AuthorityUserId : (Guid?)null
            : null;

        return new ShareDetails(
            recordShareId,
            formRecordId,
            share.ResourceLifecycleState,
            holder is null ? null : holder.SubjectTypeName == ResourceTypeNames.Team ? "Team" : "User",
            holder?.SubjectEntityId,
            holder?.RelationshipName switch { "reader" => RecipientRoles.Reader, "contributor" => RecipientRoles.Contributor, "manager" => RecipientRoles.Manager, _ => null },
            holder is null ? [] : [.. graph.FieldItemsOfShare(recordShareId).Order()],
            metadata.ShareExpiresOn,
            effective,
            metadata.ShareRevision,
            metadata.ParentRecordShareId,
            metadata.DelegationDepth,
            metadata.LegacyLineageUnresolved,
            metadata.ShareInvalidationReasonCode,
            grantor,
            state.AuthorizationRevision,
            documents);
    }

    private static AuthorityException Unavailable() => AuthorityException.NotRegistered("share is not available to this actor");

    /// <summary>The target and its committed ancestors (at most three shares), each registered on the same record.</summary>
    private static async Task<Dictionary<Guid, ShareLineageNode>> ReadLineageAsync(IAuthorityScope scope, Guid organizationId, DesiredStateGraph graph, Guid formRecordId, Guid recordShareId, CancellationToken cancellationToken)
    {
        var sharesOnRecord = graph.ShareIdsOfRecord(formRecordId).ToHashSet();
        var chain = new Dictionary<Guid, ShareLineageNode>();
        var cursor = (Guid?)recordShareId;
        while (cursor is { } id && chain.Count < MaximumLineageDepth && !chain.ContainsKey(id))
        {
            var resource = await scope.FindResourceAsync(organizationId, ResourceTypeNames.RecordShare, id, cancellationToken).ConfigureAwait(false);
            if (resource is null || (id != recordShareId && !sharesOnRecord.Contains(id)))
            {
                break;
            }

            var metadata = await scope.FindShareMetadataAsync(organizationId, id, cancellationToken).ConfigureAwait(false);
            chain.Add(id, new ShareLineageNode(resource, metadata));
            cursor = metadata?.ParentRecordShareId;
        }

        return chain;
    }

    private static async Task<bool> ManagesAnAncestorAsync(IAuthorityScope scope, DesiredStateGraph graph, Guid organizationId, Guid formRecordId, Guid userId,
        Domain.Features.Sharing.ShareResourceMetadata target, Dictionary<Guid, ShareLineageNode> chain, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (target.LegacyLineageUnresolved)
        {
            return false;
        }

        for (var ancestor = target.ParentRecordShareId; ancestor is { } id && chain.TryGetValue(id, out var node); ancestor = node.Metadata?.ParentRecordShareId)
        {
            var held = false;
            foreach (var edge in graph.EdgesOf(ResourceTypeNames.RecordShare, id, "manager"))
            {
                held |= (edge.SubjectTypeName == ResourceTypeNames.User && edge.SubjectRelationName.Length == 0 && edge.SubjectEntityId == userId) ||
                        (edge.SubjectTypeName == ResourceTypeNames.Team && edge.SubjectRelationName == "participant" && graph.IsTeamMember(userId, edge.SubjectEntityId) &&
                         await scope.FindResourceAsync(organizationId, ResourceTypeNames.Team, edge.SubjectEntityId, cancellationToken).ConfigureAwait(false) is { ResourceLifecycleState: ResourceLifecycleStates.Active });
            }

            if (held && ShareLineageExpiry.Evaluate(chain, organizationId, formRecordId, id, now).Status == ShareLineageStatus.Active)
            {
                return true;
            }
        }

        return false;
    }
}
