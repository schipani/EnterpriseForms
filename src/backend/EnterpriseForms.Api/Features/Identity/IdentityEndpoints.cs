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
        members.MapGet("/", static async Task<Ok<OrganizationMemberDirectoryResponse>> (string? q, string? role, string? signInStatus, int? pageSize, string? continuationToken, HttpContext http, GetMemberDirectoryHandler handler, CancellationToken cancellationToken) =>
            {
                http.Response.Headers.CacheControl = "no-store";
                return TypedResults.Ok(await handler.GetAsync(BusinessAuthentication.RequireContext(http), new MemberDirectoryQuery(q, role, signInStatus, pageSize, continuationToken), cancellationToken).ConfigureAwait(false));
            })
            .WithName("ListOrganizationMembers")
            .WithSummary("People directory (PG-050, C8 M2): one server page (default 15) of the organization's people, searchable (q: name, identity, team) and filterable (role: SecurityAdministrator|None; signInStatus: SignedIn|NotRecorded|NeverSignedIn), with an opaque cursor bound to the caller and filters. Rows: business directory plus the Authority's Security Administrator roster; for Security Administrators also every enabled Authority binding (including people who never signed in). Last sign-in, teams, Security Administrator badges and per-row Security Administrator action hints. Organization owners and Security Administrators only; 403 otherwise. Never an email key; never record content.")
            .ProducesProblem(StatusCodes.Status403Forbidden);

        return endpoints;
    }
}
