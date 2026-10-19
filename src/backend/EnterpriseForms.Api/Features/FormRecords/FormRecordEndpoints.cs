using EnterpriseForms.Api.Composition;
using EnterpriseForms.Api.Features.FormDefinitions;
using EnterpriseForms.Application.Features.FormRecords.CreateFormRecord;
using EnterpriseForms.Application.Features.FormRecords.GetFormRecord;
using EnterpriseForms.Application.Features.FormRecords.TransferOwnership;
using EnterpriseForms.Application.Features.FormRecords.UpdateFormRecord;
using EnterpriseForms.Contracts.Features;
using Microsoft.AspNetCore.Http.HttpResults;

namespace EnterpriseForms.Api.Features.FormRecords;

public static class FormRecordEndpoints
{
    public const string OwnerName = "FormRecords";

    public static IEndpointRouteBuilder MapFormRecordEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var definitions = endpoints.MapGroup(ApiVersions.V1Prefix + "/form-definitions").WithTags(OwnerName).WithFeatureOwner(OwnerName).RequireAuthorization(BusinessAuthentication.Policy);
        definitions.MapPost("/{formDefinitionId:guid}/records", static async Task<Created<FormRecordResponse>> (Guid formDefinitionId, CreateFormRecordRequest request, HttpContext http, CreateFormRecordHandler handler, CancellationToken cancellationToken) =>
            {
                var context = BusinessAuthentication.RequireContext(http);
                var response = await handler.HandleAsync(context, formDefinitionId, request, FormDefinitionEndpoints.RequireIdempotencyKey(http), cancellationToken).ConfigureAwait(false);
                return TypedResults.Created($"{ApiVersions.V1Prefix}/form-records/{response.FormRecordId:D}", response);
            })
            .WithName("CreateFormRecord")
            .WithSummary("Registers the record with the authority, stages the signed candidate and commits the first accepted content revision.");

        var records = endpoints.MapGroup(ApiVersions.V1Prefix + "/form-records").WithTags(OwnerName).WithFeatureOwner(OwnerName).RequireAuthorization(BusinessAuthentication.Policy);

        records.MapGet("/{formRecordId:guid}", static async Task<Ok<FormRecordResponse>> (Guid formRecordId, HttpContext http, GetFormRecordHandler handler, CancellationToken cancellationToken) =>
            {
                var context = BusinessAuthentication.RequireContext(http);
                return TypedResults.Ok(await handler.HandleAsync(context, formRecordId, cancellationToken).ConfigureAwait(false));
            })
            .WithName("GetFormRecord")
            .WithSummary("Verified read: authority head → exact staged rows → complete manifest verification → authorized subset only.");

        records.MapPatch("/{formRecordId:guid}", static async Task<Ok<FormRecordResponse>> (Guid formRecordId, UpdateFormRecordRequest request, HttpContext http, UpdateFormRecordHandler handler, CancellationToken cancellationToken) =>
            {
                var context = BusinessAuthentication.RequireContext(http);
                return TypedResults.Ok(await handler.HandleAsync(context, formRecordId, request, FormDefinitionEndpoints.RequireIdempotencyKey(http), cancellationToken).ConfigureAwait(false));
            })
            .WithName("UpdateFormRecord")
            .WithSummary("Optimistic (expected_content_revision) full-snapshot update staged as a new immutable candidate and committed by the authority.");

        records.MapPost("/{formRecordId:guid}/transfer-ownership", static async Task<Ok<CommandStatusResponse>> (Guid formRecordId, TransferOwnershipRequest request, HttpContext http, TransferOwnershipHandler handler, CancellationToken cancellationToken) =>
            {
                var context = BusinessAuthentication.RequireContext(http);
                return TypedResults.Ok(await handler.HandleAsync(context, formRecordId, request, FormDefinitionEndpoints.RequireIdempotencyKey(http), cancellationToken).ConfigureAwait(false));
            })
            .WithName("TransferRecordOwnership");

        return endpoints;
    }
}
