using AuthorizationAuthority.Api.Composition;
using AuthorizationAuthority.Application.Features.Membership;
using AuthorizationAuthority.Contracts.V1;
using Microsoft.AspNetCore.Http.HttpResults;

namespace AuthorizationAuthority.Api.Features.Membership;

/// <summary>
/// C8 M2 (AC-7.3): the organization roster for the delegated user. Membership and role changes are security commands
/// (GrantOrganizationRole, RevokeOrganizationRole, ProposeOrganizationOwnerChange, DisableIdentityBinding,
/// EnableIdentityBinding, RequestOrganizationMembership); membership requests are decided on the operator plane.
/// </summary>
public static class MembershipEndpoints
{
    public static IEndpointRouteBuilder MapMembershipEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup(InternalApi.Prefix).WithTags("Membership").RequireAuthorization(InternalAuthentication.WorkloadPolicy);

        group.MapPost("/organizations/roster", static async Task<Ok<OrganizationRosterResponse>> (OrganizationRosterRequest request, HttpContext context, OrganizationRosterService service, DelegatedUserAssertionValidator validator, CancellationToken cancellationToken) =>
            {
                var actor = InternalAuthentication.RequireActor(context, validator);
                return TypedResults.Ok(await service.GetAsync(actor, request, InternalAuthentication.Correlation(context), cancellationToken).ConfigureAwait(false));
            })
            .WithName("GetOrganizationRoster")
            .WithSummary("AC-7.3: every identity binding of the caller's organization with its enabled state and organization roles, two-owner proposals and membership requests of the last 30 days. Organization owners and Security Administrators only; anyone else gets 404. No record content or counts.");

        return endpoints;
    }
}
