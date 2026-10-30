using EnterpriseForms.Application.Common;
using EnterpriseForms.Application.Common.Authority;
using EnterpriseForms.Application.Common.Persistence;
using EnterpriseForms.Contracts.Features;

namespace EnterpriseForms.Application.Features.Organizations;

/// <summary>
/// Organization overview (PG-041, Phase C8 M1): an aggregate of independently computed parts. Each part either reports
/// its values or says it is unavailable, so a lagging projection or an unreachable Authority never fails the page and never
/// substitutes a guess. Visible to organization owners and Security Administrators only (the Authority decides); nothing
/// here reveals record content, audit payloads or key material.
/// </summary>
public sealed class OrganizationOverviewHandler(IBusinessStore store, IAuthorizationAuthorityClient authority, ISecurityAdministratorAuthority roster)
{
    public const string ProjectionSource = "business projection: identities that have signed in to this organization";

    public async Task<OrganizationOverviewResponse> GetAsync(RequestContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var decision = await AdministrationAccess.RequireAdministratorAsync(authority, context, cancellationToken).ConfigureAwait(false);

        OrganizationOverviewOrganization organization;
        OrganizationOverviewTeams teams;
        OrganizationOverviewDataProtection dataProtection;
        int? projectedMembers;
        string? projectionFailure = null;
        try
        {
            await using var scope = await store.BeginScopeAsync(cancellationToken).ConfigureAwait(false);
            var branding = await scope.FindBrandingAsync(context.OrganizationId, cancellationToken).ConfigureAwait(false);
            organization = new OrganizationOverviewOrganization(new OverviewPartStatus(true, null), branding?.OrganizationDisplayName ?? "Organization " + context.OrganizationId.ToString("N")[..8], branding?.OrganizationLogoPng is not null, branding?.BrandingRevision);
            var teamRows = await scope.GetTeamsAsync(context.OrganizationId, cancellationToken).ConfigureAwait(false);
            teams = new OrganizationOverviewTeams(new OverviewPartStatus(true, null), teamRows.Count(t => t.IsActive));
            var policy = await SecurityPolicyHandlers.EnsureAsync(scope, context.OrganizationId, cancellationToken).ConfigureAwait(false);
            dataProtection = new OrganizationOverviewDataProtection(new OverviewPartStatus(true, null), policy.MinimumFieldClassification, policy.PlaintextInternalFieldsPermitted, policy.PolicyReviewedOn is null, policy.SecurityPolicyRevision);
            projectedMembers = (await scope.GetUsersAsync(context.OrganizationId, cancellationToken).ConfigureAwait(false)).Count;
            await scope.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var unavailable = new OverviewPartStatus(false, "the business projection could not be read");
            organization = new OrganizationOverviewOrganization(unavailable, null, false, null);
            teams = new OrganizationOverviewTeams(unavailable, null);
            dataProtection = new OrganizationOverviewDataProtection(unavailable, null, null, null, null);
            projectedMembers = null;
            projectionFailure = unavailable.UnavailableReason;
        }

        int? administrators = null;
        var canManageAdministrators = false;
        string? rosterFailure = null;
        try
        {
            var result = await roster.GetSecurityAdministratorsAsync(context.ToDelegatedUser(), new SecurityAdministratorRosterQuery(context.OrganizationId), cancellationToken).ConfigureAwait(false);
            administrators = result.Administrators.Count;
            canManageAdministrators = result.CanManage;
        }
        catch (AuthorityClientException)
        {
            rosterFailure = "the Authorization Authority roster is unavailable";
        }

        var membersReason = projectionFailure is not null && rosterFailure is not null ? projectionFailure + "; " + rosterFailure : projectionFailure ?? rosterFailure;
        var members = new OrganizationOverviewMembers(new OverviewPartStatus(membersReason is null, membersReason), projectedMembers, ProjectionSource, administrators, canManageAdministrators);

        return new OrganizationOverviewResponse(decision.CanManageConfiguration, decision.CanAdministerSecurity, organization, members, teams, dataProtection, AuthenticationStatus());
    }

    /// <summary>Status only: delegated passwordless sign-in through the configured identity provider; FIDO2 enforcement (§4J) awaits PB-08 and is never claimed.</summary>
    public static OrganizationOverviewAuthentication AuthenticationStatus() =>
        new("PasswordlessFederated", "NotEnforced", "Sign-in is delegated to the organization's identity provider; no application password exists. FIDO2-only enforcement is not configured and cannot be enabled until PB-08 (verified provider policy and step-up) is delivered.");
}
