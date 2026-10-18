using EnterpriseForms.Api.Composition;
using EnterpriseForms.Api.Features.FormDefinitions;
using EnterpriseForms.Application.Features.Sharing.CreateShare;
using EnterpriseForms.Application.Features.Sharing.ListShares;
using EnterpriseForms.Application.Features.Sharing.RevokeShare;
using EnterpriseForms.Contracts.Features;
using Microsoft.AspNetCore.Http.HttpResults;

namespace EnterpriseForms.Api.Features.Sharing;

public static class SharingEndpoints
{
    public const string OwnerName = "Sharing";

    public static IEndpointRouteBuilder MapSharingEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup(ApiVersions.V1Prefix + "/form-records/{formRecordId:guid}/shares").WithTags(OwnerName).WithFeatureOwner(OwnerName).RequireAuthorization(BusinessAuthentication.Policy);

        group.MapPost("/", static async Task<Created<ShareResponse>> (Guid formRecordId, CreateShareRequest request, HttpContext http, CreateShareHandler handler, CancellationToken cancellationToken) =>
            {
                var context = BusinessAuthentication.RequireContext(http);
                var response = await handler.HandleAsync(context, formRecordId, request, FormDefinitionEndpoints.RequireIdempotencyKey(http), cancellationToken).ConfigureAwait(false);
                return TypedResults.Created($"{ApiVersions.V1Prefix}/form-records/{formRecordId:D}/shares/{response.RecordShareId:D}", response);
            })
            .WithName("CreateRecordShare")
            .WithSummary("Explicit field allow-list share committed by the authority; no wildcard or future items.");

        group.MapGet("/", static async Task<Ok<RecordShareListResponse>> (Guid formRecordId, HttpContext http, ListSharesHandler handler, CancellationToken cancellationToken) =>
            {
                var context = BusinessAuthentication.RequireContext(http);
                return TypedResults.Ok(await handler.HandleAsync(context, formRecordId, cancellationToken).ConfigureAwait(false));
            })
            .WithName("ListRecordShares")
            .WithSummary("Shares of a record for its access managers (local projection; the authority decides every recipient's access).");

        group.MapPost("/{recordShareId:guid}/revoke", static async Task<Ok<CommandStatusResponse>> (Guid formRecordId, Guid recordShareId, HttpContext http, RevokeShareHandler handler, CancellationToken cancellationToken) =>
            {
                var context = BusinessAuthentication.RequireContext(http);
                return TypedResults.Ok(await handler.HandleAsync(context, formRecordId, recordShareId, FormDefinitionEndpoints.RequireIdempotencyKey(http), cancellationToken).ConfigureAwait(false));
            })
            .WithName("RevokeRecordShare");

        return endpoints;
    }
}
