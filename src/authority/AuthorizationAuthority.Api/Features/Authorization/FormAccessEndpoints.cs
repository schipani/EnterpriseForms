using AuthorizationAuthority.Api.Composition;
using AuthorizationAuthority.Application.Features.Authorization;
using AuthorizationAuthority.Contracts.V1;
using Microsoft.AspNetCore.Http.HttpResults;

namespace AuthorizationAuthority.Api.Features.Authorization;

public static class FormAccessEndpoints
{
    public static IEndpointRouteBuilder MapFormAccessEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup(InternalApi.Prefix + "/form-definitions").WithTags("Authorization").RequireAuthorization(InternalAuthentication.WorkloadPolicy);

        group.MapGet("/{organizationId:guid}/{formDefinitionId:guid}/access", static async Task<Ok<FormAccessState>> (Guid organizationId, Guid formDefinitionId, HttpContext context, FormAccessService service, DelegatedUserAssertionValidator validator, CancellationToken cancellationToken) =>
            {
                var actor = InternalAuthentication.RequireActor(context, validator);
                return TypedResults.Ok(await service.GetAsync(actor, organizationId, formDefinitionId, cancellationToken).ConfigureAwait(false));
            })
            .WithName("GetFormAccess")
            .WithSummary("Authoritative designers and create-record principals of a form; fails closed while the organization gate is closed.");

        return endpoints;
    }
}
