using EnterpriseForms.Api.Composition;
using EnterpriseForms.Api.Features.FormDefinitions;
using EnterpriseForms.Application.Features.FormRecords.CreateFormRecord;
using EnterpriseForms.Application.Features.FormRecords.GetFormRecord;
using EnterpriseForms.Application.Features.FormRecords.History;
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

        records.MapGet("/", static async Task<Ok<RecordListResponse>> (Guid? formDefinitionId, string? recordStatus, bool? includeChildren, bool? includeArchived, string? sortBy, string? sortDirection, int? pageSize, string? continuationToken, HttpContext http, ListRecordsHandler handler, CancellationToken cancellationToken) =>
            {
                var query = RecordListQuery.Create(formDefinitionId, recordStatus, includeChildren ?? false, sortBy, sortDirection, pageSize ?? 25, includeArchived ?? false);
                return TypedResults.Ok(await handler.HandleAsync(BusinessAuthentication.RequireContext(http), query, continuationToken, cancellationToken).ConfigureAwait(false));
            })
            .WithName("ListFormRecords")
            .WithSummary("My Records: every authority-discoverable record of the caller (all discover pages at one authorization revision), filtered (form, status, child records), sorted (modifiedOn, createdOn, formName, recordStatus; record id tie-break) and paged with opaque cursors bound to caller, filters, sort and authorization revision. Rows carry unverified local metadata only; open a record for the verified projection.");

        records.MapGet("/lookup", static async Task<Ok<RecordLookupResponse>> (string? q, HttpContext http, LookupRecordHandler handler, CancellationToken cancellationToken) =>
                TypedResults.Ok(await handler.HandleAsync(BusinessAuthentication.RequireContext(http), RecordLookupQuery.Parse(q), cancellationToken).ConfigureAwait(false)))
            .WithName("LookupFormRecord")
            .WithSummary("Quick lookup by an exact record id or short reference (REC-XXXXXXXX). Matches come only from the authority (a Discover check of the id, or the caller's complete discoverable set for a reference); a missing record and an undiscoverable one give the same empty result. Archived and related records are included. Rows carry unverified local metadata only. Not a field-value search.");

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

        records.MapGet("/{formRecordId:guid}/versions", static async Task<Ok<RecordVersionListResponse>> (Guid formRecordId, long? after, int? pageSize, HttpContext http, RecordHistoryHandler handler, CancellationToken cancellationToken) =>
                TypedResults.Ok(await handler.ListAsync(BusinessAuthentication.RequireContext(http), formRecordId, after, pageSize, cancellationToken).ConfigureAwait(false)))
            .WithName("ListRecordVersions")
            .WithSummary("Phase C6 M2: accepted versions of a record in chronological order, from the Authorization Authority (AC-1): version number, accepted time, actor, current marker. Action labels carry their source (Authority or the unverified activity log). Only a person who may read the whole record now; anyone else gets 404.");

        records.MapGet("/{formRecordId:guid}/versions/{contentRevision:long}", static async Task<Ok<RecordVersionViewResponse>> (Guid formRecordId, long contentRevision, HttpContext http, RecordHistoryHandler handler, CancellationToken cancellationToken) =>
                TypedResults.Ok(await handler.ViewAsync(BusinessAuthentication.RequireContext(http), formRecordId, contentRevision, cancellationToken).ConfigureAwait(false)))
            .WithName("GetRecordVersion")
            .WithSummary("Phase C6 M2: one earlier version, read-only, after the Authority verified its exact rows against the manifest it accepted for that version (any mismatch fails closed: 423, the record is quarantined as for the current version). Current form layout and labels; the version's values, documents and status.");

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

        foreach (var transition in new[] { Application.Features.FormRecords.Lifecycle.RecordLifecycle.Submit, Application.Features.FormRecords.Lifecycle.RecordLifecycle.Reopen, Application.Features.FormRecords.Lifecycle.RecordLifecycle.Archive, Application.Features.FormRecords.Lifecycle.RecordLifecycle.Unarchive })
        {
            var captured = transition;
            records.MapPost("/{formRecordId:guid}/" + captured.ToLowerInvariant(), async Task<Ok<RecordLifecycleResponse>> (Guid formRecordId, RecordLifecycleRequest request, HttpContext http, Application.Features.FormRecords.Lifecycle.RecordLifecycleHandler handler, CancellationToken cancellationToken) =>
                {
                    var context = BusinessAuthentication.RequireContext(http);
                    var effective = RequireIfMatchRevision(http, request);
                    var response = await handler.HandleAsync(context, formRecordId, captured, effective, FormDefinitionEndpoints.RequireIdempotencyKey(http), cancellationToken).ConfigureAwait(false);
                    http.Response.Headers.ETag = response.ETag;
                    return TypedResults.Ok(response);
                })
                .WithName(captured + "FormRecord")
                .WithSummary($"Lifecycle transition '{captured}' (owner only): optimistic by If-Match/expected revision and expected status, committed as a new accepted revision through the authority; the verified signed header carries the status.");
        }

        records.MapPost("/{formRecordId:guid}/transfer-ownership", static async Task<Ok<CommandStatusResponse>> (Guid formRecordId, TransferOwnershipRequest request, HttpContext http, TransferOwnershipHandler handler, CancellationToken cancellationToken) =>
            {
                var context = BusinessAuthentication.RequireContext(http);
                return TypedResults.Ok(await handler.HandleAsync(context, formRecordId, request, FormDefinitionEndpoints.RequireIdempotencyKey(http), cancellationToken).ConfigureAwait(false));
            })
            .WithName("TransferRecordOwnership");

        return endpoints;
    }

    /// <summary>Lifecycle transitions: the same If-Match rule as updates (the header may carry the content-revision ETag).</summary>
    internal static RecordLifecycleRequest RequireIfMatchRevision(HttpContext http, RecordLifecycleRequest request)
    {
        var carrier = RequireIfMatchRevision(http, new UpdateFormRecordRequest(request.ExpectedContentRevision, new Dictionary<string, string>()));
        return request with { ExpectedContentRevision = carrier.ExpectedContentRevision };
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
