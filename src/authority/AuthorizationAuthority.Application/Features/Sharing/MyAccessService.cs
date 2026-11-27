using AuthorizationAuthority.Application.Common;
using AuthorizationAuthority.Application.Common.Ports;
using AuthorizationAuthority.Application.Features.Authorization;
using AuthorizationAuthority.Application.Features.SecurityCommands;
using AuthorizationAuthority.Contracts.V1;
using AuthorizationAuthority.Domain.Features.Organizations;

namespace AuthorizationAuthority.Application.Features.Sharing;

/// <summary>
/// C10 M2 (WP-0 Section 3.5, PG-066): the caller's own access sources, read from the Authority's committed desired state while the
/// organization gate is open (every committed revision archived and projected, so this equals the effective permission set). Only
/// the caller's own owner edges, owning-team memberships and held shares are read; no other principal is named. A share counts only
/// while it and its lineage are Active at the Authority's clock, so an expired share is never listed as access, converged or not.
/// Records of other lifecycle states (not Active) are omitted.
/// </summary>
public sealed class MyAccessService(IAuthorityStore store, TimeProvider clock)
{
    public const int ExpiringWithinDays = 7;

    public async Task<MyAccess> GetAsync(VerifiedActor actor, Guid organizationId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);
        await using var scope = await store.BeginScopeAsync(cancellationToken).ConfigureAwait(false);
        var bound = await ActorResolver.BindAsync(scope, organizationId, actor, cancellationToken).ConfigureAwait(false);
        var state = await scope.ReadOrganizationAsync(organizationId, cancellationToken).ConfigureAwait(false)
                    ?? throw AuthorityException.NotRegistered("organization is not registered");
        OrganizationGate.RequireOpen(await OrganizationGate.EvaluateAsync(scope, state, cancellationToken).ConfigureAwait(false));
        var now = clock.GetUtcNow();
        var user = bound.AuthorityUserId;
        var graph = new DesiredStateGraph(await scope.GetActiveEdgesAsync(organizationId, cancellationToken).ConfigureAwait(false));
        var teams = graph.EdgesWithSubject(ResourceTypeNames.User, user).Where(e => e.ResourceTypeName == ResourceTypeNames.Team && e.RelationshipName == "member").Select(e => e.ResourceEntityId).ToHashSet();
        var activeTeams = (await scope.GetResourcesAsync(organizationId, ResourceTypeNames.Team, [.. teams], cancellationToken).ConfigureAwait(false))
            .Where(t => t.ResourceLifecycleState == ResourceLifecycleStates.Active).Select(t => t.ResourceEntityId).ToHashSet();

        var entries = new List<MyAccessEntry>();
        foreach (var owner in graph.EdgesWithSubject(ResourceTypeNames.User, user).Where(e => e.ResourceTypeName == ResourceTypeNames.FormRecord && e.RelationshipName == "owner"))
        {
            entries.Add(new MyAccessEntry(owner.ResourceEntityId, "Owner", null, null, null, null, false));
        }

        foreach (var team in activeTeams.Order())
        {
            foreach (var owner in graph.EdgesWithSubject(ResourceTypeNames.Team, team).Where(e => e.ResourceTypeName == ResourceTypeNames.FormRecord && e.RelationshipName == "owner" && e.SubjectRelationName == "participant"))
            {
                entries.Add(new MyAccessEntry(owner.ResourceEntityId, "OwningTeam", null, team, null, null, false));
            }
        }

        var held = graph.EdgesWithSubject(ResourceTypeNames.User, user).Where(e => e.SubjectRelationName.Length == 0).Select(e => (Edge: e, Team: (Guid?)null))
            .Concat(activeTeams.SelectMany(t => graph.EdgesWithSubject(ResourceTypeNames.Team, t).Where(e => e.SubjectRelationName == "participant").Select(e => (Edge: e, Team: (Guid?)t))))
            .Where(h => h.Edge.ResourceTypeName == ResourceTypeNames.RecordShare && h.Edge.RelationshipName is "reader" or "contributor" or "manager")
            .ToList();
        foreach (var (edge, team) in held.OrderBy(h => h.Edge.ResourceEntityId))
        {
            var share = await scope.FindResourceAsync(organizationId, ResourceTypeNames.RecordShare, edge.ResourceEntityId, cancellationToken).ConfigureAwait(false);
            if (share is not { ResourceLifecycleState: ResourceLifecycleStates.Active, ParentResourceEntityId: { } record })
            {
                continue;
            }

            var chain = new Dictionary<Guid, ShareLineageNode>();
            for (var cursor = (Guid?)share.ResourceEntityId; cursor is { } id && chain.Count < 3 && !chain.ContainsKey(id);)
            {
                if (await scope.FindResourceAsync(organizationId, ResourceTypeNames.RecordShare, id, cancellationToken).ConfigureAwait(false) is not { } resource)
                {
                    break;
                }

                var metadata = await scope.FindShareMetadataAsync(organizationId, id, cancellationToken).ConfigureAwait(false);
                chain.Add(id, new ShareLineageNode(resource, metadata));
                cursor = metadata?.ParentRecordShareId;
            }

            var lineage = ShareLineageExpiry.Evaluate(chain, organizationId, record, share.ResourceEntityId, now);
            if (lineage.Status != ShareLineageStatus.Active)
            {
                continue; // an expired or broken share confers nothing and is not listed as access
            }

            var role = edge.RelationshipName switch { "reader" => RecipientRoles.Reader, "contributor" => RecipientRoles.Contributor, _ => RecipientRoles.Manager };
            var soon = lineage.EffectiveExpiry is { } at && at <= now.AddDays(ExpiringWithinDays);
            entries.Add(new MyAccessEntry(record, team is null ? "DirectShare" : "TeamShare", share.ResourceEntityId, team, role, lineage.EffectiveExpiry, soon));
        }

        var records = (await scope.GetResourcesAsync(organizationId, ResourceTypeNames.FormRecord, [.. entries.Select(e => e.FormRecordId).Distinct()], cancellationToken).ConfigureAwait(false))
            .Where(r => r.ResourceLifecycleState == ResourceLifecycleStates.Active).Select(r => r.ResourceEntityId).ToHashSet();
        var visible = entries.Where(e => records.Contains(e.FormRecordId)).ToList();
        return new MyAccess([.. visible.Take(MyAccess.MaximumEntries)], visible.Count > MyAccess.MaximumEntries, ExpiringWithinDays, state.AuthorizationRevision);
    }
}
