using AuthorizationAuthority.Application.Common;
using AuthorizationAuthority.Application.Common.Ports;
using AuthorizationAuthority.Domain.Features.Organizations;
using AuthorizationAuthority.Domain.Features.SecurityCommands;

namespace AuthorizationAuthority.Application.Features.SecurityOverview;

/// <summary>
/// C8 M4-4 — the Effective-edge rule (PHASE_C8_M4_AC8_SECURITY_CONTRACT.md §5.4; owner Q-3 and D8-65). Read from the caller's
/// one <c>REPEATABLE READ READ ONLY</c> AuthorityDb snapshot, the actor holds role <i>X</i> only if <b>all</b> hold:
/// <list type="number">
/// <item>the actor's identity binding in the organization is enabled;</item>
/// <item>an edge <c>organization#X@user:actor</c> exists with <c>RemovedBySecurityCommandId IS NULL</c>;</item>
/// <item>that edge's creating command belongs to the organization and is in state <c>Effective</c>.</item>
/// </list>
/// A pending or committed-but-unprojected grant confers nothing; a committed revoke applies at once (the edge is removed when the
/// revoke commits). No SpiceDB call is made, so the rule answers while the organization gate is closed.
/// <para>
/// <b>Restricted use (AC-M4-4.1):</b> only <see cref="SecurityOverviewService"/> (AC-8.4) and <see cref="SecurityCoverageService"/>
/// (AC-8.5) may call it, enforced by <c>EffectiveEdgeRuleGuardTests</c>. It never authorizes record content, the audit explorer,
/// event detail, the timeline, incidents or search approvals (OD-6).
/// </para>
/// </summary>
internal static class EffectiveEdgeRule
{
    /// <summary>Whether the actor holds at least one of <paramref name="roleNames"/> under the rule, in <paramref name="snapshot"/>.</summary>
    internal static async Task<bool> HoldsAnyAsync(IAuthorityScope snapshot, Guid organizationId, VerifiedActor actor, IReadOnlyCollection<string> roleNames, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(roleNames);
        var binding = await snapshot.FindIdentityBindingAsync(organizationId, actor.IssuerUri, actor.SubjectIdentifier, cancellationToken).ConfigureAwait(false);
        if (binding is null || !binding.IdentityBindingEnabled || binding.OrganizationId != organizationId)
        {
            return false; // condition 1
        }

        foreach (var role in roleNames)
        {
            var edges = await snapshot.GetRelationshipEdgeHistoryAsync(organizationId, ResourceTypeNames.Organization, organizationId, role, ResourceTypeNames.User, binding.AuthorityUserId, cancellationToken).ConfigureAwait(false);
            foreach (var edge in edges.Where(e => e.RemovedBySecurityCommandId is null && e.OrganizationId == organizationId)) // condition 2
            {
                var creating = await snapshot.FindCommandAsync(edge.CreatedBySecurityCommandId, cancellationToken).ConfigureAwait(false);
                if (creating is not null && creating.OrganizationId == organizationId
                    && string.Equals(creating.CommandStateName, SecurityCommandStates.Effective, StringComparison.Ordinal)) // condition 3
                {
                    return true;
                }
            }
        }

        return false;
    }
}
