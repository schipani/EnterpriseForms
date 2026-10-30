using EnterpriseForms.Api.Composition;
using EnterpriseForms.Application.Features.Identity.EnsureProjection;
using EnterpriseForms.Application.Features.Identity.MemberDirectory;
using EnterpriseForms.Contracts.Features;
using Microsoft.AspNetCore.Http.HttpResults;

namespace EnterpriseForms.Api.Features.Identity;

public static class IdentityEndpoints
{
    public const string OwnerName = "Identity";

    public static IEndpointRouteBuilder MapIdentityEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup(ApiVersions.V1Prefix + "/users").WithTags(OwnerName).WithFeatureOwner(OwnerName).RequireAuthorization(BusinessAuthentication.Policy);

        group.MapGet("/me", static async Task<Ok<CurrentUserResponse>> (HttpContext http, EnsureIdentityProjectionHandler handler, CancellationToken cancellationToken) =>
            {
                var context = BusinessAuthentication.RequireContext(http);
                await handler.HandleAsync(context, cancellationToken).ConfigureAwait(false);
                return TypedResults.Ok(new CurrentUserResponse(context.OrganizationId, context.IssuerUri, context.SubjectIdentifier, context.DisplayName));
            })
            .WithName("GetCurrentUser")
            .WithSummary("Verified principal of the current assertion (projection maintenance only; confers no permission).");

        // Phase C8 M1 (PG-050, read-only): people directory for organization owners and Security Administrators.
        var members = endpoints.MapGroup(ApiVersions.V1Prefix + "/organization/members").WithTags(OwnerName).WithFeatureOwner(OwnerName).RequireAuthorization(BusinessAuthentication.Policy);
        members.MapGet("/", static async Task<Ok<OrganizationMemberDirectoryResponse>> (HttpContext http, GetMemberDirectoryHandler handler, CancellationToken cancellationToken) =>
            {
                http.Response.Headers.CacheControl = "no-store";
                return TypedResults.Ok(await handler.GetAsync(BusinessAuthentication.RequireContext(http), cancellationToken).ConfigureAwait(false));
            })
            .WithName("ListOrganizationMembers")
            .WithSummary("People directory (PG-050, read-only): identities the Authority has bound that have signed in (business projection, labelled), their teams, and Security Administrator badges from the Authority roster. Organization owners and Security Administrators only; 403 otherwise. Never an email key; never record content.")
            .ProducesProblem(StatusCodes.Status403Forbidden);

        return endpoints;
    }
}
