using AuthorizationAuthority.Application.Common;
using AuthorizationAuthority.Application.Common.Ports;
using AuthorizationAuthority.Contracts.V1;
using AuthorizationAuthority.Domain.Features.Organizations;

namespace AuthorizationAuthority.Application.Features.SecurityCommands;

/// <summary>
/// Phase C7 (owner message 7): appointment and removal of Security Administrators inside an organization. "Security
/// Administrator" is the existing organization relation <c>system_administrator</c>, whose only SpiceDB permission is
/// <c>administer_security</c>; no second role system. These commands only ever touch that relation.
/// <list type="bullet">
/// <item>Only an existing Security Administrator of the same organization may appoint or remove one — an organization owner
/// cannot appoint anyone (or themselves) through ownership.</item>
/// <item>The appointee must be an enabled identity binding of this organization (verified issuer/subject); never an email
/// address, a business row or a client flag.</item>
/// <item>Fresh step-up is required (the Authority's PB-08 gate: unavailable in staging and production, so these
/// commands are refused there until PB-08 exists; the synthetic development path elsewhere).</item>
/// <item>The last Security Administrator who can still sign in (enabled identity) cannot be removed (the organization would
/// need the operator-plane recovery path, DR-C7-3). An administrator whose identity was disabled can still be removed.</item>
/// </list>
/// The first Security Administrator of an organization without one comes from the operator plane (organization bootstrap
/// today; post-bootstrap appointment and recovery is decision DR-C7-3).
/// </summary>
public static partial class CommandPlanner
{
    private static async Task<CommandPlan> SecurityAdministratorAsync(IAuthorityScope scope, Guid organizationId, BoundActor actor, SecurityCommandRequest request, DesiredStateGraph graph, FieldSearchApprovalGate gate, bool appoint, CancellationToken cancellationToken)
    {
        var payload = CanonicalJson.Deserialize<SecurityAdministratorPayload>(request.CommandPayload);
        RequireResource(request, ResourceKind.Organization, organizationId);
        if (!graph.HasOrganizationRole(organizationId, actor.AuthorityUserId, OrganizationRoleNames.SystemAdministrator))
        {
            throw AuthorityException.Forbidden("appointing or removing a Security Administrator requires administer_security in this organization");
        }

        if (!string.Equals(payload.RoleName, OrganizationRoleNames.SystemAdministrator, StringComparison.Ordinal))
        {
            throw AuthorityException.Invalid("only the system_administrator (Security Administrator) role is managed by these commands");
        }

        if (payload.AuthorityUserId == Guid.Empty || string.IsNullOrWhiteSpace(payload.Reason) || payload.Reason.Length > 500)
        {
            throw AuthorityException.Invalid("authority_user_id and a reason (1..500 characters) are required");
        }

        gate.RequireStepUp(payload.StepUpEvidence);
        var holders = graph.EdgesOf(ResourceTypeNames.Organization, organizationId, OrganizationRoleNames.SystemAdministrator)
            .Where(e => e.SubjectTypeName == ResourceTypeNames.User).ToList();
        var existing = holders.FirstOrDefault(e => e.SubjectEntityId == payload.AuthorityUserId);
        var plan = new CommandPlan { NormalizedPayloadJson = CanonicalJson.Serialize(payload with { StepUpEvidence = null }) };
        if (appoint)
        {
            if (existing is not null)
            {
                throw AuthorityException.Invalid("the user is already a Security Administrator");
            }

            await RequireUserBindingAsync(scope, organizationId, payload.AuthorityUserId, cancellationToken).ConfigureAwait(false);
            plan.AddEdges.Add((ResourceTypeNames.Organization, organizationId, OrganizationRoleNames.SystemAdministrator, ResourceTypeNames.User, payload.AuthorityUserId, string.Empty));
            return plan;
        }

        if (existing is null)
        {
            throw AuthorityException.Invalid("the user is not a Security Administrator");
        }

        // Removal works for a disabled identity too; what must remain is another administrator who can still sign in.
        var enabled = (await scope.GetIdentityBindingsAsync(organizationId, cancellationToken).ConfigureAwait(false)).Where(b => b.IdentityBindingEnabled).Select(b => b.AuthorityUserId).ToHashSet();
        if (!holders.Any(e => e.SubjectEntityId != payload.AuthorityUserId && enabled.Contains(e.SubjectEntityId)))
        {
            throw new AuthorityException(AuthorityErrorCodes.LastSecurityAdministrator, 409, false, "the last Security Administrator cannot be removed; appoint another one first");
        }

        plan.RemoveEdges.Add(existing);
        return plan;
    }
}
