using AuthorizationAuthority.Application.Common;
using AuthorizationAuthority.Application.Common.Ports;
using AuthorizationAuthority.Application.Features.SecurityCommands;
using AuthorizationAuthority.Contracts.V1;
using AuthorizationAuthority.Domain.Features.Organizations;

namespace AuthorizationAuthority.Application.Features.Authorization;

/// <summary>
/// Authoritative read of a form's access state (who may design, who may create records). The answer is the authority's
/// own desired state, returned only while the organization gate is open (every committed revision archived and applied
/// to the SpiceDB projection), so it equals the effective permission set — never a business-database projection. Only a
/// designer of the form or an organization administrator may read it; everyone else is refused without disclosure.
/// </summary>
public sealed class FormAccessService(IAuthorityStore store)
{
    public async Task<FormAccessState> GetAsync(VerifiedActor actor, Guid organizationId, Guid formDefinitionId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);
        await using var scope = await store.BeginScopeAsync(cancellationToken).ConfigureAwait(false);
        var bound = await ActorResolver.BindAsync(scope, organizationId, actor, cancellationToken).ConfigureAwait(false);
        var state = await scope.ReadOrganizationAsync(organizationId, cancellationToken).ConfigureAwait(false)
                    ?? throw AuthorityException.NotRegistered("organization is not registered");
        OrganizationGate.RequireOpen(await OrganizationGate.EvaluateAsync(scope, state, cancellationToken).ConfigureAwait(false));

        var resource = await scope.FindResourceAsync(organizationId, ResourceTypeNames.FormDefinition, formDefinitionId, cancellationToken).ConfigureAwait(false);
        var graph = new DesiredStateGraph(await scope.GetActiveEdgesAsync(organizationId, cancellationToken).ConfigureAwait(false));
        var administrator = graph.HasOrganizationRole(organizationId, bound.AuthorityUserId, OrganizationRoleNames.OrganizationOwner)
                            || graph.HasOrganizationRole(organizationId, bound.AuthorityUserId, OrganizationRoleNames.SystemAdministrator);
        if (resource is null || resource.ResourceLifecycleState != ResourceLifecycleStates.Active || (!graph.CanDesignForm(bound.AuthorityUserId, formDefinitionId) && !administrator))
        {
            // Same answer for an unknown form and an unauthorized caller.
            throw AuthorityException.NotRegistered("form definition is not available to this actor");
        }

        FormAccessSubject[] Subjects(string relation) => graph.EdgesOf(ResourceTypeNames.FormDefinition, formDefinitionId, relation)
            .Select(e => new FormAccessSubject(e.SubjectTypeName == ResourceTypeNames.Team ? "Team" : "User", e.SubjectEntityId))
            .OrderBy(s => s.SubjectKind, StringComparer.Ordinal)
            .ThenBy(s => s.SubjectId)
            .ToArray();

        return new FormAccessState(formDefinitionId, state.AuthorizationRevision, state.ProjectionAppliedRevision, Subjects("designer"), Subjects("form_user"));
    }
}
