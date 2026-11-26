using AuthorizationAuthority.Api.Composition;
using AuthorizationAuthority.Application.Features.Sharing;
using AuthorizationAuthority.Contracts.V1;
using Microsoft.AspNetCore.Http.HttpResults;

namespace AuthorizationAuthority.Api.Features.Authorization;

public static class ShareDetailsEndpoints
{
    public static IEndpointRouteBuilder MapShareDetailsEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup(InternalApi.Prefix + "/records").WithTags("Authorization").RequireAuthorization(InternalAuthentication.WorkloadPolicy);

        group.MapGet("/{organizationId:guid}/{formRecordId:guid}/shares/{recordShareId:guid}", static async Task<Ok<ShareDetails>> (Guid organizationId, Guid formRecordId, Guid recordShareId, HttpContext context, ShareDetailsService service, DelegatedUserAssertionValidator validator, CancellationToken cancellationToken) =>
            {
                var actor = InternalAuthentication.RequireActor(context, validator);
                return TypedResults.Ok(await service.GetAsync(actor, organizationId, formRecordId, recordShareId, cancellationToken).ConfigureAwait(false));
            })
            .WithName("GetShareDetails")
            .WithSummary("Authoritative committed state and share revision of one share (owner, or a Manager on its committed lineage); fails closed while the organization gate is closed.");

        group.MapGet("/{organizationId:guid}/{formRecordId:guid}/security-history", static async Task<Ok<RecordSecurityHistory>> (Guid organizationId, Guid formRecordId, HttpContext context, RecordSecurityHistoryService service, DelegatedUserAssertionValidator validator, CancellationToken cancellationToken) =>
            {
                var actor = InternalAuthentication.RequireActor(context, validator);
                return TypedResults.Ok(await service.GetAsync(actor, organizationId, formRecordId, cancellationToken).ConfigureAwait(false));
            })
            .WithName("GetRecordSecurityHistory")
            .WithSummary("Record-scoped sharing and ownership history from committed Authority edges, each command with its own state (owner or ManageAccess; others 404).");

        return endpoints;
    }
}
