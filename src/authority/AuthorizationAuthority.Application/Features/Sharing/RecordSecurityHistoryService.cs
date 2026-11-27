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
/// Each item carries its command's actual state (A11). C10 D6: commands are also found through their indexed target record, so every
/// committed expiry-only update is listed with the expiry it committed, including ones a later update superseded; an item is
/// journal-backed only when its authorization-journal entry exists. Readers (WP-0 Section 3.5, independent review F-4/F3): for share history, the
/// record's current owner (or an owning-team member) and a holder of an Active Manager share on the record with Active lineage (ManageAccess),
/// a non-owner seeing no owner changes; for ownership history, the current owner and a Security Auditor of the organization, seeing owner
/// changes only. Everyone else gets the answer for an unknown record. The read uses committed desired state, which is never behind the
/// projection, so a revoked manager is refused at once, and the reader's own authority comes only from grants whose commands are in
/// effect (A11); pending commands are listed with their own state (Section 3.5); an integrity quarantine refuses the read (review F-5).
/// </summary>
public sealed class RecordSecurityHistoryService(IAuthorityStore store, TimeProvider clock)
{
    private static readonly HashSet<string> Roles = ["reader", "contributor", "manager"];

    public Task<RecordSecurityHistory> GetAsync(VerifiedActor actor, Guid organizationId, Guid formRecordId, CancellationToken cancellationToken) =>
        GetAsync(actor, organizationId, formRecordId, ownershipView: false, cancellationToken);

    public async Task<RecordSecurityHistory> GetAsync(VerifiedActor actor, Guid organizationId, Guid formRecordId, bool ownershipView, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);
        await using var scope = await store.BeginScopeAsync(cancellationToken).ConfigureAwait(false);
        var bound = await ActorResolver.BindAsync(scope, organizationId, actor, cancellationToken).ConfigureAwait(false);
        var state = await scope.ReadOrganizationAsync(organizationId, cancellationToken).ConfigureAwait(false)
                    ?? throw AuthorityException.NotRegistered("organization is not registered");
        if (state.IntegrityQuarantineEnabled)
        {
            throw AuthorityException.Quarantined("organization quarantined");
        }

        var record = await scope.FindResourceAsync(organizationId, ResourceTypeNames.FormRecord, formRecordId, cancellationToken).ConfigureAwait(false);
        if (record is not { ResourceLifecycleState: ResourceLifecycleStates.Active } || record.OrganizationId != organizationId)
        {
            throw Unavailable();
        }

        var graph = new DesiredStateGraph(await scope.GetActiveEdgesAsync(organizationId, cancellationToken).ConfigureAwait(false));
        // C10 re-review (F-5/F3 residual): the reader's own authority comes only from grants whose commands are in effect (A11); a grant
        // still awaiting its projection or archive receipt confers no read. Pending commands are still listed with their own state.
        var readerGraph = await InEffectAsync(scope, state, graph, cancellationToken).ConfigureAwait(false);
        var isOwner = readerGraph.IsRecordOwner(bound.AuthorityUserId, formRecordId);
        var permitted = isOwner || (ownershipView
            ? readerGraph.HasOrganizationRole(organizationId, bound.AuthorityUserId, OrganizationRoleNames.SecurityAuditor)
            : await ManagesAccessAsync(scope, readerGraph, organizationId, formRecordId, bound.AuthorityUserId, clock.GetUtcNow(), cancellationToken).ConfigureAwait(false));
        if (!permitted)
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

        // C10 D6: every committed command that targeted the record (indexed, so superseded expiry-only updates are kept), plus the
        // commands that changed the record's edges from elsewhere (team removal, member deactivation). C10 review N-2: the ownership view
        // reads exactly the commands that added or removed one of the record's owner edges (the complete edge history), and both views
        // filter before they truncate, so newer share commands never push an owner change out of the ownership view.
        var commands = new Dictionary<Guid, SecurityCommand>();
        var readLimitReached = false;
        if (!ownershipView)
        {
            var targeted = await scope.GetRecordTargetedCommandsAsync(organizationId, formRecordId, RecordSecurityHistoryLimits.MaximumCommands + 1, cancellationToken).ConfigureAwait(false);
            readLimitReached = targeted.Count > RecordSecurityHistoryLimits.MaximumCommands;
            foreach (var command in targeted.Where(c => c.OrganizationId == organizationId))
            {
                commands[command.SecurityCommandId] = command;
            }
        }

        var edgeCommandIds = (ownershipView
                ? edges.Where(e => e.ResourceTypeName == ResourceTypeNames.FormRecord && e.ResourceEntityId == formRecordId && e.RelationshipName == "owner")
                : edges)
            .SelectMany(e => e.RemovedBySecurityCommandId is { } removed ? new[] { e.CreatedBySecurityCommandId, removed } : [e.CreatedBySecurityCommandId])
            .Where(id => id != Guid.Empty && !commands.ContainsKey(id)).ToHashSet();
        foreach (var id in edgeCommandIds)
        {
            if (await scope.FindCommandAsync(id, cancellationToken).ConfigureAwait(false) is { } command && command.OrganizationId == organizationId && command.CommandStateName != SecurityCommandStates.Rejected)
            {
                commands[id] = command;
            }
        }

        var visible = new List<(SecurityCommand Command, List<RecordSecurityChange> Changes)>();
        foreach (var command in commands.Values.OrderByDescending(c => c.CommandCreatedOn).ThenByDescending(c => c.SecurityCommandId))
        {
            var changes = ChangesOf(command, edges, shares, formRecordId, metadata)
                .Where(c => ownershipView ? IsOwnerChange(c) : isOwner || !IsOwnerChange(c)).ToList();
            if (changes.Count == 0 && (ownershipView || !isOwner))
            {
                continue; // nothing this reader may see
            }

            visible.Add((command, changes));
        }

        var truncated = readLimitReached || visible.Count > RecordSecurityHistoryLimits.MaximumCommands;
        var page = visible.Take(RecordSecurityHistoryLimits.MaximumCommands).ToList();
        var journalled = await scope.GetAuthorizationJournalCommandIdsAsync(organizationId, [.. page.Select(p => p.Command.SecurityCommandId)], cancellationToken).ConfigureAwait(false);
        var users = new Dictionary<Guid, Guid?>();
        var items = new List<RecordSecurityHistoryItem>();
        foreach (var (command, changes) in page)
        {
            if (!users.TryGetValue(command.ActorIdentityBindingId, out var user))
            {
                var binding = await scope.FindIdentityBindingByIdAsync(command.ActorIdentityBindingId, cancellationToken).ConfigureAwait(false);
                users[command.ActorIdentityBindingId] = user = binding is { } b && b.OrganizationId == organizationId ? b.AuthorityUserId : null;
            }

            items.Add(new RecordSecurityHistoryItem(command.SecurityCommandId, command.CommandKindName, command.CommandStateName, command.CommandCreatedOn, command.CommandEffectiveOn,
                command.CommittedAuthorizationRevision, user, changes, journalled.Contains(command.SecurityCommandId)));
        }

        return new RecordSecurityHistory(formRecordId, items, truncated, state.AuthorizationRevision);
    }

    private static bool IsOwnerChange(RecordSecurityChange change) => change.ChangeKind is "OwnerAdded" or "OwnerRemoved";

    /// <summary>What one command changed, from the edges it created and removed. Deterministic order: owner changes, then shares by id.</summary>
    internal static IReadOnlyList<RecordSecurityChange> ChangesOf(SecurityCommand command, IReadOnlyList<AuthorityRelationshipEdge> edges,
        IReadOnlyDictionary<Guid, AuthorityResource> shares, Guid formRecordId, IReadOnlyDictionary<Guid, Domain.Features.Sharing.ShareResourceMetadata> metadata)
    {
        var commandId = command.SecurityCommandId;
        var (payloadShare, payloadExpiry) = CommittedShareScope(command);
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
                if (payloadShare == shareId && command.CommandKindName == nameof(SecurityCommandKind.UpdateShareScope))
                {
                    changes.Add(new RecordSecurityChange("ShareScopeChanged", shareId, null, null, null, null, payloadExpiry)); // an expiry-only update
                }

                continue;
            }

            var roleRemoved = own.Any(e => e.ResourceTypeName == ResourceTypeNames.RecordShare && Roles.Contains(e.RelationshipName) && e.RemovedBySecurityCommandId == commandId);
            var stillHeld = own.Any(e => e.ResourceTypeName == ResourceTypeNames.RecordShare && Roles.Contains(e.RelationshipName) && e.RemovedBySecurityCommandId is null);
            if (share.RegisteredBySecurityCommandId == commandId)
            {
                changes.Add(new RecordSecurityChange("ShareCreated", shareId, null, null, null, null, payloadShare == shareId ? payloadExpiry : null));
            }
            else if (roleRemoved && !stillHeld && share.ResourceLifecycleState != ResourceLifecycleStates.Active)
            {
                var reason = share.ResourceLifecycleState == ResourceLifecycleStates.Invalidated ? metadata.GetValueOrDefault(shareId)?.ShareInvalidationReasonCode : null;
                changes.Add(new RecordSecurityChange("ShareEnded", shareId, share.ResourceLifecycleState, reason, null, null));
            }
            else
            {
                changes.Add(new RecordSecurityChange("ShareScopeChanged", shareId, null, null, null, null, payloadShare == shareId ? payloadExpiry : null));
            }
        }

        return changes;
    }

    /// <summary>The share and expiry a committed CreateShare or UpdateShareScope recorded in its normalized payload (step-up already stripped).</summary>
    private static (Guid? Share, DateTimeOffset? Expiry) CommittedShareScope(SecurityCommand command)
    {
        if (command.CommandKindName is not (nameof(SecurityCommandKind.CreateShare) or nameof(SecurityCommandKind.UpdateShareScope)))
        {
            return (null, null);
        }

        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(command.CommandPayloadJson);
            var root = document.RootElement;
            Guid? share = root.TryGetProperty("record_share_id", out var id) && id.TryGetGuid(out var parsed) ? parsed : null;
            DateTimeOffset? expiry = root.TryGetProperty("share_expires_on", out var e) && e.ValueKind == System.Text.Json.JsonValueKind.String && e.TryGetDateTimeOffset(out var at) ? at : null;
            return (share, expiry);
        }
        catch (System.Text.Json.JsonException)
        {
            return (null, null);
        }
    }

    private static AuthorityException Unavailable() => AuthorityException.NotRegistered("record history is not available to this actor");

    /// <summary>
    /// The committed edges whose creating command is in effect. While the gate is open every committed command is projected and archived,
    /// so the graph is unchanged; otherwise edges created by a command that is not Effective are left out (removals already apply).
    /// </summary>
    private static async Task<DesiredStateGraph> InEffectAsync(IAuthorityScope scope, OrganizationAuthorizationState state, DesiredStateGraph graph, CancellationToken cancellationToken)
    {
        if (await OrganizationGate.EvaluateAsync(scope, state, cancellationToken).ConfigureAwait(false) == ProjectionGateState.Ready)
        {
            return graph;
        }

        var pending = new HashSet<Guid>();
        foreach (var commandId in graph.Edges.Select(e => e.CreatedBySecurityCommandId).Where(id => id != Guid.Empty).Distinct())
        {
            if (await scope.FindCommandAsync(commandId, cancellationToken).ConfigureAwait(false) is { } command && command.CommandStateName != SecurityCommandStates.Effective)
            {
                pending.Add(commandId);
            }
        }

        return pending.Count == 0 ? graph : new DesiredStateGraph([.. graph.Edges.Where(e => !pending.Contains(e.CreatedBySecurityCommandId))]);
    }

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
