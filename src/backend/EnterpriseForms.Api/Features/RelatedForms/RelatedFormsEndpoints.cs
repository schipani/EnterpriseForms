using EnterpriseForms.Api.Composition;
using EnterpriseForms.Api.Features.FormDefinitions;
using EnterpriseForms.Application.Features.FormRecords.CreateFormRecord;
using EnterpriseForms.Application.Features.RelatedForms;
using EnterpriseForms.Contracts.Features;
using Microsoft.AspNetCore.Http.HttpResults;

namespace EnterpriseForms.Api.Features.RelatedForms;

/// <summary>Child collections (PG-018/019): list, create-and-link, unlink. Every child is independently authorized and verified.</summary>
public static class RelatedFormsEndpoints
{
    public const string OwnerName = "RelatedForms";

    public static IEndpointRouteBuilder MapRelatedFormsEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup(ApiVersions.V1Prefix + "/form-records/{formRecordId:guid}/related").WithTags(OwnerName).WithFeatureOwner(OwnerName).RequireAuthorization(BusinessAuthentication.Policy);

        group.MapGet("/{formRelationshipDefinitionId:guid}", static async Task<Ok<ChildRecordListResponse>> (Guid formRecordId, Guid formRelationshipDefinitionId, HttpContext http, ChildRecordHandlers handlers, CancellationToken cancellationToken) =>
                TypedResults.Ok(await handlers.ListAsync(BusinessAuthentication.RequireContext(http), formRecordId, formRelationshipDefinitionId, cancellationToken).ConfigureAwait(false)))
            .WithName("ListChildRecords")
            .WithSummary("Authorized, verified child records of a related-form list (no hidden counts).");

        group.MapPost("/", static async Task<Created<FormRecordResponse>> (Guid formRecordId, CreateChildRecordRequest request, HttpContext http, CreateFormRecordHandler handler, CancellationToken cancellationToken) =>
            {
                var response = await handler.CreateChildAsync(BusinessAuthentication.RequireContext(http), formRecordId, request, FormDefinitionEndpoints.RequireIdempotencyKey(http), cancellationToken).ConfigureAwait(false);
                return TypedResults.Created($"{ApiVersions.V1Prefix}/form-records/{response.FormRecordId:D}", response);
            })
            .WithName("CreateChildRecord")
            .WithSummary("Creates a child record linked to the parent through the authority (parent edge, signed header binding). Requires Idempotency-Key.");

        group.MapPost("/{childFormRecordId:guid}/unlink", static async Task<Ok<CommandStatusResponse>> (Guid formRecordId, Guid childFormRecordId, UnlinkChildRecordRequest request, HttpContext http, ChildRecordHandlers handlers, CancellationToken cancellationToken) =>
                TypedResults.Ok(await handlers.UnlinkAsync(BusinessAuthentication.RequireContext(http), formRecordId, childFormRecordId, request, FormDefinitionEndpoints.RequireIdempotencyKey(http), cancellationToken).ConfigureAwait(false)))
            .WithName("UnlinkChildRecord")
            .WithSummary("Authority UnlinkChildRecord command: removes the parent edge, keeps the data, remover becomes owner.");

        return endpoints;
    }
}
