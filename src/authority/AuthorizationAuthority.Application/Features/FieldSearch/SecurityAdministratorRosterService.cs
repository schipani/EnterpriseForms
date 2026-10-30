using AuthorizationAuthority.Application.Common;
using AuthorizationAuthority.Application.Common.Ports;
using AuthorizationAuthority.Application.Features.Authorization;
using AuthorizationAuthority.Application.Features.SecurityCommands;
using AuthorizationAuthority.Contracts.V1;
using AuthorizationAuthority.Domain.Features.Organizations;

namespace AuthorizationAuthority.Application.Features.FieldSearch;

/// <summary>
/// Owner message 7: the organization's Security Administrators (relation <c>system_administrator</c>) from the Authority's own
/// desired state, read behind the organization gate. Only that organization's Security Administrators and organization
/// owners may read it (owners see the roster, not the candidates, and cannot change it); anyone else — another
/// organization included — gets the not-found answer. Display names are added by the business directory.
/// </summary>
public sealed class SecurityAdministratorRosterService(IAuthorityStore store, FieldSearchApprovalGate gate)
{
    public async Task<SecurityAdministratorRoster> GetAsync(VerifiedActor actor, SecurityAdministratorRosterRequest request, Guid correlationId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(request);
        await using var scope = await store.BeginScopeAsync(cancellationToken).ConfigureAwait(false);
        var state = await scope.ReadOrganizationAsync(request.OrganizationId, cancellationToken).ConfigureAwait(false)
                    ?? throw new AuthorityException(AuthorityErrorCodes.NotFound, 404, false);
        var bound = await ActorResolver.BindAsync(scope, state.OrganizationId, actor, cancellationToken).ConfigureAwait(false);
        OrganizationGate.RequireOpen(await OrganizationGate.EvaluateAsync(scope, state, cancellationToken).ConfigureAwait(false));
        var graph = new DesiredStateGraph(await scope.GetActiveEdgesAsync(state.OrganizationId, cancellationToken).ConfigureAwait(false));
        var canManage = graph.HasOrganizationRole(state.OrganizationId, bound.AuthorityUserId, OrganizationRoleNames.SystemAdministrator);
        if (!canManage && !graph.HasOrganizationRole(state.OrganizationId, bound.AuthorityUserId, OrganizationRoleNames.OrganizationOwner))
        {
            throw new AuthorityException(AuthorityErrorCodes.NotFound, 404, false);
        }

        var bindings = (await scope.GetIdentityBindingsAsync(state.OrganizationId, cancellationToken).ConfigureAwait(false)).ToDictionary(b => b.AuthorityUserId);
        var administrators = graph.EdgesOf(ResourceTypeNames.Organization, state.OrganizationId, OrganizationRoleNames.SystemAdministrator)
            .Where(e => e.SubjectTypeName == ResourceTypeNames.User && bindings.ContainsKey(e.SubjectEntityId))
            .Select(e => new SecurityAdministratorEntry(e.SubjectEntityId, bindings[e.SubjectEntityId].IdentityIssuerUri, bindings[e.SubjectEntityId].IdentitySubjectIdentifier, e.RelationshipCreatedOn, e.CreatedBySecurityCommandId))
            .OrderBy(a => a.AppointedOn).ThenBy(a => a.AuthorityUserId).ToList();
        var holders = administrators.Select(a => a.AuthorityUserId).ToHashSet();
        var candidates = canManage
            ? bindings.Values.Where(b => b.IdentityBindingEnabled && !holders.Contains(b.AuthorityUserId))
                .Select(b => new SecurityAdministratorCandidate(b.AuthorityUserId, b.IdentityIssuerUri, b.IdentitySubjectIdentifier)).OrderBy(c => c.AuthorityUserId).ToList()
            : null;
        return new SecurityAdministratorRoster(canManage, gate.ApprovalsOperational, gate.UnavailableReason, administrators, candidates, state.AuthorizationRevision, correlationId);
    }
}
