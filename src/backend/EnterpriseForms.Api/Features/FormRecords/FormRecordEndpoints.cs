using EnterpriseForms.Api.Composition;
using EnterpriseForms.Api.Features.FormDefinitions;
using EnterpriseForms.Application.Features.FormRecords.CreateFormRecord;
using EnterpriseForms.Application.Features.FormRecords.GetFormRecord;
using EnterpriseForms.Application.Features.FormRecords.ListRecords;
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
            .WithSummary("Registers the record with the authority, validates typed values against the published layout, stages the signed candidate and commits the first accepted content revision.");

        var records = endpoints.MapGroup(ApiVersions.V1Prefix + "/form-records").WithTags(OwnerName).WithFeatureOwner(OwnerName).RequireAuthorization(BusinessAuthentication.Policy);

        records.MapGet("/", static async Task<Ok<RecordListResponse>> (Guid? formDefinitionId, bool? includeChildren, int? pageSize, string? continuationToken, HttpContext http, ListRecordsHandler handler, CancellationToken cancellationToken) =>
                TypedResults.Ok(await handler.HandleAsync(BusinessAuthentication.RequireContext(http), formDefinitionId, includeChildren ?? false, pageSize ?? 25, continuationToken, cancellationToken).ConfigureAwait(false)))
            .WithName("ListFormRecords")
            .WithSummary("Authority-discoverable records with unverified local metadata; open a record for the verified projection.");

        records.MapGet("/{formRecordId:guid}", static async Task<Ok<FormRecordResponse>> (Guid formRecordId, HttpContext http, GetFormRecordHandler handler, CancellationToken cancellationToken) =>
            {
                var context = BusinessAuthentication.RequireContext(http);
                var response = await handler.HandleAsync(context, formRecordId, cancellationToken).ConfigureAwait(false);
                http.Response.Headers.ETag = "\"" + response.ContentRevision + "\"";
                return TypedResults.Ok(response);
            })
            .WithName("GetFormRecord")
            .WithSummary("Verified read: authority head → exact staged rows → complete manifest verification → authorized subset only.");

        records.MapGet("/{formRecordId:guid}/projection", static async Task<Ok<RecordProjectionResponse>> (Guid formRecordId, HttpContext http, GetRecordProjectionHandler handler, CancellationToken cancellationToken) =>
            {
                var response = await handler.HandleAsync(BusinessAuthentication.RequireContext(http), formRecordId, cancellationToken).ConfigureAwait(false);
                http.Response.Headers.ETag = response.ETag;
                return TypedResults.Ok(response);
            })
            .WithName("GetRecordProjection")
            .WithSummary("Verified projection for the UI: layout version, field metadata, authorized values and documents, permitted actions, ETag.");

        records.MapPatch("/{formRecordId:guid}", static async Task<Ok<FormRecordResponse>> (Guid formRecordId, UpdateFormRecordRequest request, HttpContext http, UpdateFormRecordHandler handler, CancellationToken cancellationToken) =>
            {
                var context = BusinessAuthentication.RequireContext(http);
                var effective = RequireIfMatchRevision(http, request);
                var response = await handler.HandleAsync(context, formRecordId, effective, FormDefinitionEndpoints.RequireIdempotencyKey(http), cancellationToken).ConfigureAwait(false);
                http.Response.Headers.ETag = "\"" + response.ContentRevision + "\"";
                return TypedResults.Ok(response);
            })
            .WithName("UpdateFormRecord")
            .WithSummary("Optimistic (expected_content_revision or If-Match) full-snapshot typed update staged as a new immutable candidate and committed by the authority.");

        records.MapPost("/{formRecordId:guid}/transfer-ownership", static async Task<Ok<CommandStatusResponse>> (Guid formRecordId, TransferOwnershipRequest request, HttpContext http, TransferOwnershipHandler handler, CancellationToken cancellationToken) =>
            {
                var context = BusinessAuthentication.RequireContext(http);
                return TypedResults.Ok(await handler.HandleAsync(context, formRecordId, request, FormDefinitionEndpoints.RequireIdempotencyKey(http), cancellationToken).ConfigureAwait(false));
            })
            .WithName("TransferRecordOwnership");

        return endpoints;
    }

    /// <summary>PB-06: `If-Match: "revision"` overrides/validates the body revision; 412 on mismatch between the two.</summary>
    internal static UpdateFormRecordRequest RequireIfMatchRevision(HttpContext http, UpdateFormRecordRequest request)
    {
        var header = http.Request.Headers.IfMatch.ToString().Trim().Trim('"');
        if (header.Length == 0)
        {
            return request;
        }

        var revisionText = header.Contains('-', StringComparison.Ordinal) ? header[..header.IndexOf('-', StringComparison.Ordinal)] : header;
        if (!long.TryParse(revisionText, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var revision))
        {
            throw new Application.Common.BusinessException("PRECONDITION_FAILED", 412, "If-Match must carry the content revision ETag");
        }

        if (request.ExpectedContentRevision != 0 && request.ExpectedContentRevision != revision)
        {
            throw new Application.Common.BusinessException("PRECONDITION_FAILED", 412, "If-Match and expected_content_revision disagree");
        }

        return request with { ExpectedContentRevision = revision };
    }
}
