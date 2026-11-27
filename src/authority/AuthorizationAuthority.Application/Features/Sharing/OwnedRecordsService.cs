using AuthorizationAuthority.Application.Common;
using AuthorizationAuthority.Application.Common.Ports;
using AuthorizationAuthority.Application.Features.SecurityCommands;
using AuthorizationAuthority.Contracts.V1;
using AuthorizationAuthority.Domain.Features.Organizations;

namespace AuthorizationAuthority.Application.Features.Sharing;

/// <summary>
/// C10 M2 (WP-0 Section 3.7, D8-4, PG-053): the Active records a member directly owns — ids only, no content — so an administrator
/// can reassign them before the member is disabled. Readers: an enabled Security Administrator or organization owner of the
/// organization; anyone else is refused as for an unknown member. The reassignment itself is one TransferOwnership command per
/// record, under the Authority's existing override and G25 rules. Read behind the open gate.
/// </summary>
public sealed class OwnedRecordsService(IAuthorityStore store)
{
    public const int MaximumRecords = 500;

    public async Task<MemberOwnedRecords> GetAsync(VerifiedActor actor, Guid organizationId, Guid memberUserId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);
        await using var scope = await store.BeginScopeAsync(cancellationToken).ConfigureAwait(false);
        var bound = await ActorResolver.BindAsync(scope, organizationId, actor, cancellationToken).ConfigureAwait(false);
        var state = await scope.ReadOrganizationAsync(organizationId, cancellationToken).ConfigureAwait(false)
                    ?? throw AuthorityException.NotRegistered("organization is not registered");
        OrganizationGate.RequireOpen(await OrganizationGate.EvaluateAsync(scope, state, cancellationToken).ConfigureAwait(false));
        var graph = new DesiredStateGraph(await scope.GetActiveEdgesAsync(organizationId, cancellationToken).ConfigureAwait(false));
        var administrator = graph.HasOrganizationRole(organizationId, bound.AuthorityUserId, OrganizationRoleNames.SystemAdministrator)
                            || graph.HasOrganizationRole(organizationId, bound.AuthorityUserId, OrganizationRoleNames.OrganizationOwner);
        if (!administrator || await scope.FindIdentityBindingByUserAsync(organizationId, memberUserId, cancellationToken).ConfigureAwait(false) is null)
        {
            throw AuthorityException.NotRegistered("member is not available to this actor");
        }

        var owned = graph.EdgesWithSubject(ResourceTypeNames.User, memberUserId)
            .Where(e => e.ResourceTypeName == ResourceTypeNames.FormRecord && e.RelationshipName == "owner" && e.SubjectRelationName.Length == 0)
            .Select(e => e.ResourceEntityId).Distinct().ToList();
        var active = (await scope.GetResourcesAsync(organizationId, ResourceTypeNames.FormRecord, owned, cancellationToken).ConfigureAwait(false))
            .Where(r => r.ResourceLifecycleState == ResourceLifecycleStates.Active).Select(r => r.ResourceEntityId).Order().ToList();
        return new MemberOwnedRecords(memberUserId, [.. active.Take(MaximumRecords)], active.Count > MaximumRecords, state.AuthorizationRevision);
    }
}
