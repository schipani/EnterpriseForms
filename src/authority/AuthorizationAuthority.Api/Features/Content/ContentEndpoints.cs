using AuthorizationAuthority.Api.Composition;
using AuthorizationAuthority.Api.Features.SecurityCommands;
using AuthorizationAuthority.Application.Features.Content;
using AuthorizationAuthority.Contracts.V1;
using Microsoft.AspNetCore.Http.HttpResults;

namespace AuthorizationAuthority.Api.Features.Content;

public static class ContentEndpoints
{
    public static IEndpointRouteBuilder MapContentEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup(InternalApi.Prefix).WithTags("Content").RequireAuthorization(InternalAuthentication.WorkloadPolicy);

        group.MapPost("/content-changes/prepare", static async Task<Accepted<PreparedContentOperation>> (PrepareContentChangeRequest request, HttpContext context, ContentService service, DelegatedUserAssertionValidator validator, CancellationToken cancellationToken) =>
            {
                var actor = InternalAuthentication.TryActor(context, validator);
                var key = SecurityCommandEndpoints.RequireIdempotencyKey(context);
                return TypedResults.Accepted((string?)null, await service.PrepareAsync(actor, request, key, InternalAuthentication.Correlation(context), cancellationToken).ConfigureAwait(false));
            })
            .WithName("PrepareContentChange")
            .WithSummary("Validates the exact candidate snapshot against the accepted head and the actor's edit scope (or, without a user, the reprotection rules of an effective PublishFormSecurityRevision command), then signs it. Not a generic signing endpoint.");

        group.MapPost("/content-changes/{operationId:guid}/commit", static async Task<Ok<ContentRevision>> (Guid operationId, CommitContentChangeRequest request, HttpContext context, ContentService service, DelegatedUserAssertionValidator validator, CancellationToken cancellationToken) =>
            {
                var actor = InternalAuthentication.TryActor(context, validator);
                return TypedResults.Ok(await service.CommitAsync(actor, operationId, request, InternalAuthentication.Correlation(context), cancellationToken).ConfigureAwait(false));
            })
            .WithName("CommitContentChange")
            .WithSummary("Proves the staged bytes equal the approved candidate and CAS-advances the trusted content head.");

        group.MapGet("/content-revisions/{organizationId:guid}/{formRecordId:guid}", static async Task<Ok<ContentRevision>> (Guid organizationId, Guid formRecordId, Guid? reprotectionCommandId, HttpContext context, ContentService service, DelegatedUserAssertionValidator validator, CancellationToken cancellationToken) =>
            {
                var actor = InternalAuthentication.TryActor(context, validator);
                return TypedResults.Ok(await service.GetHeadAsync(actor, organizationId, formRecordId, reprotectionCommandId, InternalAuthentication.Correlation(context), cancellationToken).ConfigureAwait(false));
            })
            .WithName("GetCurrentContentRevision");

        group.MapPost("/integrity/verify-record-projection", static async Task<Ok<VerifyRecordProjectionResult>> (VerifyRecordProjectionRequest request, HttpContext context, ContentService service, DelegatedUserAssertionValidator validator, CancellationToken cancellationToken) =>
            {
                var actor = InternalAuthentication.RequireActor(context, validator);
                return TypedResults.Ok(await service.VerifyProjectionAsync(actor, request, InternalAuthentication.Correlation(context), cancellationToken).ConfigureAwait(false));
            })
            .WithName("VerifyRecordProjection")
            .WithSummary("Compares the observed staged items with the complete accepted leaf index, verifies every signature and the root, and returns the authorized subset.");

        return endpoints;
    }
}
