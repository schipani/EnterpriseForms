namespace EnterpriseForms.Application.Common.Authority;

/// <summary>
/// Phase C8 M1: who may open the organization's administrative reads (overview, people, team directory). Decided by the
/// Authority on every call: <c>manage_configuration</c> (organization owner) or <c>administer_security</c> (Security
/// Administrator). Everyone else gets a 403 without counts. Team commands and renames keep requiring
/// <c>manage_configuration</c> alone (architecture §4 M1).
/// </summary>
public static class AdministrationAccess
{
    public sealed record AdministrationDecision(bool CanManageConfiguration, bool CanAdministerSecurity, long AuthorizationRevision);

    public static async Task<AdministrationDecision> DecideAsync(IAuthorizationAuthorityClient authority, RequestContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(authority);
        ArgumentNullException.ThrowIfNull(context);
        var organization = new ResourceReference(ProtectedResourceKind.Organization, context.OrganizationId);
        var decisions = await authority.CheckPermissionsBatchAsync(context.ToDelegatedUser(),
        [
            new PermissionCheckRequest(context.OrganizationId, organization, ProtectedAction.ManageOrganization),
            new PermissionCheckRequest(context.OrganizationId, organization, ProtectedAction.AdministerSecurity),
        ], cancellationToken).ConfigureAwait(false);
        return new AdministrationDecision(decisions[0].Allowed, decisions[1].Allowed, decisions[0].AuthorizationRevision);
    }

    /// <summary>Owner or Security Administrator; otherwise 403 FORBIDDEN (no counts, no names).</summary>
    public static async Task<AdministrationDecision> RequireAdministratorAsync(IAuthorizationAuthorityClient authority, RequestContext context, CancellationToken cancellationToken)
    {
        var decision = await DecideAsync(authority, context, cancellationToken).ConfigureAwait(false);
        if (!decision.CanManageConfiguration && !decision.CanAdministerSecurity)
        {
            throw new BusinessException("FORBIDDEN", 403, "organization owner or Security Administrator role required");
        }

        return decision;
    }

    /// <summary>Owner only (manage_configuration); otherwise 403.</summary>
    public static async Task<AdministrationDecision> RequireOwnerAsync(IAuthorizationAuthorityClient authority, RequestContext context, CancellationToken cancellationToken)
    {
        var decision = await DecideAsync(authority, context, cancellationToken).ConfigureAwait(false);
        if (!decision.CanManageConfiguration)
        {
            throw new BusinessException("FORBIDDEN", 403, "organization owner role required");
        }

        return decision;
    }
}
