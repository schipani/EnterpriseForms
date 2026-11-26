using AuthorizationAuthority.Application.Common;
using AuthorizationAuthority.Application.Common.Ports;
using AuthorizationAuthority.Application.Features.Authorization;
using AuthorizationAuthority.Application.Features.SecurityCommands;
using AuthorizationAuthority.Contracts.V1;
using AuthorizationAuthority.Domain.Features.Organizations;
using AuthorizationAuthority.Domain.Features.SecurityCommands;

namespace AuthorizationAuthority.Application.Features.Sharing;

/// <summary>
/// C10 M1-WP9 (WP-0 Section 3.5, D10-11): the record's sharing and ownership history from the Authority's own committed state. Every
/// owner, role and item edge records the command that created it and the command that removed it, so the commands that changed the
/// record's owners or shares — and what each changed — are derived without trusting the business database or scanning the journal.
/// Each item carries its command's actual state (A11). Limitation: an expiry-only update changes no edge, so only a share's latest
/// expiry-setting command is listed (from its committed metadata); an earlier expiry-only change superseded by it is not. Readers: the record's current owner (or an owning-team member) and a holder of
/// an Active Manager share on the record with Active lineage (ManageAccess); everyone else gets the answer for an unknown record.
/// The read uses committed desired state, which is never behind the projection, so a revoked manager is refused at once.
/// </summary>
public sealed class RecordSecurityHistoryService(IAuthorityStore store, TimeProvider clock)
{
    private static readonly HashSet<string> Roles = ["reader", "contributor", "manager"];

    public async Task<RecordSecurityHistory> GetAsync(VerifiedActor actor, Guid organizationId, Guid formRecordId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);
        await using var scope = await store.BeginScopeAsync(cancellationToken).ConfigureAwait(false);
        var bound = await ActorResolver.BindAsync(scope, organizationId, actor, cancellationToken).ConfigureAwait(false);
        var state = await scope.ReadOrganizationAsync(organizationId, cancellationToken).ConfigureAwait(false)
                    ?? throw AuthorityException.NotRegistered("organization is not registered");
        var record = await scope.FindResourceAsync(organizationId, ResourceTypeNames.FormRecord, formRecordId, cancellationToken).ConfigureAwait(false);
        if (record is not { ResourceLifecycleState: ResourceLifecycleStates.Active } || record.OrganizationId != organizationId)
        {
            throw Unavailable();
        }

        var graph = new DesiredStateGraph(await scope.GetActiveEdgesAsync(organizationId, cancellationToken).ConfigureAwait(false));
        if (!graph.IsRecordOwner(bound.AuthorityUserId, formRecordId) && !await ManagesAccessAsync(scope, graph, organizationId, formRecordId, bound.AuthorityUserId, clock.GetUtcNow(), cancellationToken).ConfigureAwait(false))
        {
            throw Unavailable();
        }

        var edges = (await scope.GetRecordSecurityEdgeHistoryAsync(organizationId, formRecordId, cancellationToken).ConfigureAwait(false))
            .Where(e => e.OrganizationId == organizationId).ToList();
        var shareIds = edges.Where(e => e.ResourceTypeName == ResourceTypeNames.FormRecord && e.RelationshipName == "shared_via" && e.SubjectTypeName == ResourceTypeNames.RecordShare)
            .Select(e => e.SubjectEntityId).ToHashSet();
        var shares = (await scope.GetResourcesAsync(organizationId, ResourceTypeNames.RecordShare, [.. shareIds], cancellationToken).ConfigureAwait(false))
            .Where(r => r.OrganizationId == organizationId && r.ParentResourceEntityId == formRecordId).ToDictionary(r => r.ResourceEntityId);
        var metadata = new Dictionary<Guid, Domain.Features.Sharing.ShareResourceMetadata>();
        foreach (var id in shares.Keys)
        {
            if (await scope.FindShareMetadataAsync(organizationId, id, cancellationToken).ConfigureAwait(false) is { } m && m.OrganizationId == organizationId)
            {
                metadata[id] = m;
            }
        }

        // An expiry-only update changes no edge: the share's latest expiry-setting command is taken from its committed metadata. An
        // earlier expiry-only update that a later one superseded is not listed (no command-to-record index exists yet).
        var commandIds = edges.Select(e => e.CreatedBySecurityCommandId).Concat(edges.Where(e => e.RemovedBySecurityCommandId is not null).Select(e => e.RemovedBySecurityCommandId!.Value))
            .Concat(metadata.Values.Where(m => m.ExpirySourceSecurityCommandId is not null).Select(m => m.ExpirySourceSecurityCommandId!.Value))
            .Where(id => id != Guid.Empty).ToHashSet();
        var commands = new List<SecurityCommand>();
        foreach (var id in commandIds)
        {
            if (await scope.FindCommandAsync(id, cancellationToken).ConfigureAwait(false) is { } command && command.OrganizationId == organizationId)
            {
                commands.Add(command);
            }
        }

        var newest = commands.OrderByDescending(c => c.CommandCreatedOn).ThenByDescending(c => c.SecurityCommandId).ToList();
        var truncated = newest.Count > RecordSecurityHistoryLimits.MaximumCommands;
        var users = new Dictionary<Guid, Guid?>();
        var items = new List<RecordSecurityHistoryItem>();
        foreach (var command in newest.Take(RecordSecurityHistoryLimits.MaximumCommands))
        {
            if (!users.TryGetValue(command.ActorIdentityBindingId, out var user))
            {
                var binding = await scope.FindIdentityBindingByIdAsync(command.ActorIdentityBindingId, cancellationToken).ConfigureAwait(false);
                users[command.ActorIdentityBindingId] = user = binding is { } b && b.OrganizationId == organizationId ? b.AuthorityUserId : null;
            }

            items.Add(new RecordSecurityHistoryItem(command.SecurityCommandId, command.CommandKindName, command.CommandStateName, command.CommandCreatedOn, command.CommandEffectiveOn,
                command.CommittedAuthorizationRevision, user, ChangesOf(command.SecurityCommandId, edges, shares, metadata, formRecordId)));
        }

        return new RecordSecurityHistory(formRecordId, items, truncated, state.AuthorizationRevision);
    }

    /// <summary>What one command changed, from the edges it created and removed. Deterministic order: owner changes, then shares by id.</summary>
    internal static IReadOnlyList<RecordSecurityChange> ChangesOf(Guid commandId, IReadOnlyList<AuthorityRelationshipEdge> edges,
        IReadOnlyDictionary<Guid, AuthorityResource> shares, IReadOnlyDictionary<Guid, Domain.Features.Sharing.ShareResourceMetadata> metadata, Guid formRecordId)
    {
        var changes = new List<RecordSecurityChange>();
        foreach (var owner in edges.Where(e => e.ResourceTypeName == ResourceTypeNames.FormRecord && e.ResourceEntityId == formRecordId && e.RelationshipName == "owner")
                     .OrderBy(e => e.SubjectEntityId))
        {
            var kind = owner.SubjectTypeName == ResourceTypeNames.Team ? "Team" : "User";
            if (owner.CreatedBySecurityCommandId == commandId)
            {
                changes.Add(new RecordSecurityChange("OwnerAdded", null, null, null, kind, owner.SubjectEntityId));
            }

            if (owner.RemovedBySecurityCommandId == commandId)
            {
                changes.Add(new RecordSecurityChange("OwnerRemoved", null, null, null, kind, owner.SubjectEntityId));
            }
        }

        foreach (var (shareId, share) in shares.OrderBy(s => s.Key))
        {
            var own = edges.Where(e => (e.ResourceTypeName == ResourceTypeNames.RecordShare && e.ResourceEntityId == shareId) ||
                                       (e.SubjectTypeName == ResourceTypeNames.RecordShare && e.SubjectEntityId == shareId)).ToList();
            var created = own.Any(e => e.CreatedBySecurityCommandId == commandId);
            var removed = own.Any(e => e.RemovedBySecurityCommandId == commandId);
            if (!created && !removed)
            {
                if (metadata.GetValueOrDefault(shareId)?.ExpirySourceSecurityCommandId == commandId && share.RegisteredBySecurityCommandId != commandId)
                {
                    changes.Add(new RecordSecurityChange("ShareScopeChanged", shareId, null, null, null, null)); // the latest expiry change
                }

                continue;
            }

            var roleRemoved = own.Any(e => e.ResourceTypeName == ResourceTypeNames.RecordShare && Roles.Contains(e.RelationshipName) && e.RemovedBySecurityCommandId == commandId);
            var stillHeld = own.Any(e => e.ResourceTypeName == ResourceTypeNames.RecordShare && Roles.Contains(e.RelationshipName) && e.RemovedBySecurityCommandId is null);
            if (share.RegisteredBySecurityCommandId == commandId)
            {
                changes.Add(new RecordSecurityChange("ShareCreated", shareId, null, null, null, null));
            }
            else if (roleRemoved && !stillHeld && share.ResourceLifecycleState != ResourceLifecycleStates.Active)
            {
                var reason = share.ResourceLifecycleState == ResourceLifecycleStates.Invalidated ? metadata.GetValueOrDefault(shareId)?.ShareInvalidationReasonCode : null;
                changes.Add(new RecordSecurityChange("ShareEnded", shareId, share.ResourceLifecycleState, reason, null, null));
            }
            else
            {
                changes.Add(new RecordSecurityChange("ShareScopeChanged", shareId, null, null, null, null));
            }
        }

        return changes;
    }

    private static AuthorityException Unavailable() => AuthorityException.NotRegistered("record history is not available to this actor");

    /// <summary>ManageAccess through sharing: an Active Manager role on an Active share of the record whose lineage is Active now.</summary>
    private static async Task<bool> ManagesAccessAsync(IAuthorityScope scope, DesiredStateGraph graph, Guid organizationId, Guid formRecordId, Guid userId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        foreach (var shareId in graph.ShareIdsOfRecord(formRecordId).Distinct().Order())
        {
            var held = false;
            foreach (var edge in graph.EdgesOf(ResourceTypeNames.RecordShare, shareId, "manager"))
            {
                held |= (edge.SubjectTypeName == ResourceTypeNames.User && edge.SubjectRelationName.Length == 0 && edge.SubjectEntityId == userId) ||
                        (edge.SubjectTypeName == ResourceTypeNames.Team && edge.SubjectRelationName == "participant" && graph.IsTeamMember(userId, edge.SubjectEntityId) &&
                         await scope.FindResourceAsync(organizationId, ResourceTypeNames.Team, edge.SubjectEntityId, cancellationToken).ConfigureAwait(false) is { ResourceLifecycleState: ResourceLifecycleStates.Active });
            }

            if (!held)
            {
                continue;
            }

            var chain = new Dictionary<Guid, ShareLineageNode>();
            var cursor = (Guid?)shareId;
            while (cursor is { } id && chain.Count < 3 && !chain.ContainsKey(id))
            {
                if (await scope.FindResourceAsync(organizationId, ResourceTypeNames.RecordShare, id, cancellationToken).ConfigureAwait(false) is not { } resource)
                {
                    break;
                }

                var metadata = await scope.FindShareMetadataAsync(organizationId, id, cancellationToken).ConfigureAwait(false);
                chain.Add(id, new ShareLineageNode(resource, metadata));
                cursor = metadata?.ParentRecordShareId;
            }

            if (ShareLineageExpiry.Evaluate(chain, organizationId, formRecordId, shareId, now).Status == ShareLineageStatus.Active)
            {
                return true;
            }
        }

        return false;
    }
}
