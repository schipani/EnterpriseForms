using EnterpriseForms.Api.Composition;
using EnterpriseForms.Api.Features.FormDefinitions;
using EnterpriseForms.Application.Features.Sharing.CreateShare;
using EnterpriseForms.Application.Features.Sharing.ListShares;
using EnterpriseForms.Application.Features.Sharing.RevokeShare;
using EnterpriseForms.Application.Features.Sharing.UpdateShare;
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

        // C10 M2 (WP-0 §3.2, PB-06): replace a share's scope. If-Match carries the share ETag "{recordShareId}:{shareRevision}"
        // (428 when missing, 412 when malformed or stale); an organization-revision conflict is 409 and is not retried.
        group.MapPatch("/{recordShareId:guid}", static async Task<Ok<UpdateShareResponse>> (Guid formRecordId, Guid recordShareId, UpdateShareRequest request, HttpContext http, UpdateShareHandler handler, CancellationToken cancellationToken) =>
            {
                var context = BusinessAuthentication.RequireContext(http);
                var expected = ShareRevisionPrecondition.Require(http.Request.Headers.IfMatch, recordShareId);
                var response = await handler.HandleAsync(context, formRecordId, recordShareId, expected, request, FormDefinitionEndpoints.RequireIdempotencyKey(http), cancellationToken).ConfigureAwait(false);
                http.Response.Headers.ETag = ShareRevisionPrecondition.Format(recordShareId, response.ShareRevision);
                return TypedResults.Ok(response);
            })
            .WithName("UpdateRecordShare")
            .WithSummary("Replaces a share's role, field items and expiry via the authority's UpdateShareScope command. Requires If-Match with the share ETag and an Idempotency-Key; returns the new ETag. Exactly one of share_expires_on or no_expiry is required.");

        // G27: useDevelopmentStepUp is a bool query parameter (default false) since this endpoint has no body.
        group.MapPost("/{recordShareId:guid}/revoke", static async Task<Ok<CommandStatusResponse>> (Guid formRecordId, Guid recordShareId, HttpContext http, RevokeShareHandler handler, CancellationToken cancellationToken, bool useDevelopmentStepUp = false) =>
            {
                var context = BusinessAuthentication.RequireContext(http);
                return TypedResults.Ok(await handler.HandleAsync(context, formRecordId, recordShareId, FormDefinitionEndpoints.RequireIdempotencyKey(http), useDevelopmentStepUp, cancellationToken).ConfigureAwait(false));
            })
            .WithName("RevokeRecordShare")
            .WithSummary("Revokes a share via authority command; effective immediately at the authority barrier. G27: useDevelopmentStepUp (bool query, default false) for the synthetic test path only.");

        return endpoints;
    }
}
