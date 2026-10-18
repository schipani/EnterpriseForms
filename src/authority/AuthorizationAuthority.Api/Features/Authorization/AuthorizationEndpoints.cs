using AuthorizationAuthority.Api.Composition;
using AuthorizationAuthority.Application.Features.Authorization;
using AuthorizationAuthority.Contracts.V1;
using Microsoft.AspNetCore.Http.HttpResults;

namespace AuthorizationAuthority.Api.Features.Authorization;

public static class AuthorizationEndpoints
{
    public static IEndpointRouteBuilder MapAuthorizationEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup(InternalApi.Prefix + "/authorization").WithTags("Authorization").RequireAuthorization(InternalAuthentication.WorkloadPolicy);

        group.MapPost("/check", static async Task<Ok<PermissionDecision>> (PermissionCheckRequest request, HttpContext context, PermissionCheckService service, DelegatedUserAssertionValidator validator, CancellationToken cancellationToken) =>
            {
                var actor = InternalAuthentication.RequireActor(context, validator);
                return TypedResults.Ok(await service.CheckAsync(actor, request, InternalAuthentication.Correlation(context), cancellationToken).ConfigureAwait(false));
            })
            .WithName("CheckPermission")
            .WithSummary("Authoritative permission check at the authority-held projection token; fails closed while the organization gate is closed.");

        group.MapPost("/check-batch", static async Task<Ok<PermissionCheckBatchResponse>> (PermissionCheckBatchRequest request, HttpContext context, PermissionCheckService service, DelegatedUserAssertionValidator validator, CancellationToken cancellationToken) =>
            {
                var actor = InternalAuthentication.RequireActor(context, validator);
                var decisions = await service.CheckBatchAsync(actor, request.Checks, InternalAuthentication.Correlation(context), cancellationToken).ConfigureAwait(false);
                return TypedResults.Ok(new PermissionCheckBatchResponse(decisions));
            })
            .WithName("CheckPermissionsBatch");

        group.MapPost("/discover", static async Task<Ok<DiscoverResponse>> (DiscoverRecordsRequest request, HttpContext context, PermissionCheckService service, DelegatedUserAssertionValidator validator, CancellationToken cancellationToken) =>
            {
                var actor = InternalAuthentication.RequireActor(context, validator);
                return TypedResults.Ok(await service.DiscoverAsync(actor, request, cancellationToken).ConfigureAwait(false));
            })
            .WithName("DiscoverRecords")
            .WithSummary("Authority-sourced discoverable record ids (registered + discover permission); never derived from business tables.");

        return endpoints;
    }
}
