using EnterpriseForms.Api.Composition;
using EnterpriseForms.Application.Features.Identity.EnsureProjection;
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

        return endpoints;
    }
}
