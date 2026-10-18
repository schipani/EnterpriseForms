using EnterpriseForms.Api.Composition;
using EnterpriseForms.Api.Features.FormDefinitions;
using EnterpriseForms.Application.Features.Documents.Documents;
using EnterpriseForms.Application.Features.Documents.UploadSessions;
using EnterpriseForms.Contracts.Features;
using EnterpriseForms.Domain.Features.Documents;
using Microsoft.AspNetCore.Http.HttpResults;

namespace EnterpriseForms.Api.Features.Documents;

/// <summary>Upload sessions (CLAUDE-DESIGN §6.3), document projection, versions, download and removal (PG-010/020/021).</summary>
public static class DocumentEndpoints
{
    public const string OwnerName = "Documents";

    public static IEndpointRouteBuilder MapDocumentEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var sessions = endpoints.MapGroup(ApiVersions.V1Prefix + "/upload-sessions").WithTags(OwnerName).WithFeatureOwner(OwnerName).RequireAuthorization(BusinessAuthentication.Policy);

        sessions.MapPost("/", static async Task<Created<UploadSessionResponse>> (BeginUploadRequest request, HttpContext http, UploadSessionHandlers handlers, CancellationToken cancellationToken) =>
            {
                var response = await handlers.BeginAsync(BusinessAuthentication.RequireContext(http), request, FormDefinitionEndpoints.RequireIdempotencyKey(http), cancellationToken).ConfigureAwait(false);
                return TypedResults.Created($"{ApiVersions.V1Prefix}/upload-sessions/{response.UploadSessionId:D}", response);
            })
            .WithName("BeginUploadSession")
            .WithSummary("Opens an authorized, idempotent chunked upload session bound to a record's document collection field.");

        sessions.MapPut("/{uploadSessionId:guid}/chunks/{chunkIndex:int}", static async Task<Ok<UploadChunkReceiptResponse>> (Guid uploadSessionId, int chunkIndex, HttpContext http, UploadSessionHandlers handlers, CancellationToken cancellationToken) =>
            {
                var length = http.Request.ContentLength;
                return TypedResults.Ok(await handlers.AcceptChunkAsync(BusinessAuthentication.RequireContext(http), uploadSessionId, chunkIndex, http.Request.Body, length, cancellationToken).ConfigureAwait(false));
            })
            .Accepts<Stream>("application/octet-stream")
            .DisableAntiforgery()
            .WithName("UploadChunk")
            .WithSummary("Streams one exact-size chunk (4 MiB; last chunk remainder) into quarantine; acknowledged ranges are server truth.");

        sessions.MapGet("/{uploadSessionId:guid}", static async Task<Ok<UploadSessionResponse>> (Guid uploadSessionId, HttpContext http, UploadSessionHandlers handlers, CancellationToken cancellationToken) =>
                TypedResults.Ok(await handlers.GetAsync(BusinessAuthentication.RequireContext(http), uploadSessionId, cancellationToken).ConfigureAwait(false)))
            .WithName("GetUploadSession");

        sessions.MapPost("/{uploadSessionId:guid}/complete", static async Task<Accepted<UploadSessionResponse>> (Guid uploadSessionId, HttpContext http, UploadSessionHandlers handlers, CancellationToken cancellationToken) =>
            {
                var response = await handlers.CompleteAsync(BusinessAuthentication.RequireContext(http), uploadSessionId, cancellationToken).ConfigureAwait(false);
                return TypedResults.Accepted($"{ApiVersions.V1Prefix}/upload-sessions/{uploadSessionId:D}", response);
            })
            .WithName("CompleteUploadSession")
            .WithSummary("Idempotent finalize: assembles quarantined chunks and queues durable scan/encrypt processing. 202 = queued, not Ready.");

        sessions.MapPost("/{uploadSessionId:guid}/commit", static async Task<Ok<UploadSessionResponse>> (Guid uploadSessionId, HttpContext http, UploadSessionHandlers handlers, CancellationToken cancellationToken) =>
                TypedResults.Ok(await handlers.CommitAsync(BusinessAuthentication.RequireContext(http), uploadSessionId, FormDefinitionEndpoints.RequireIdempotencyKey(http), cancellationToken).ConfigureAwait(false)))
            .WithName("CommitUploadSession")
            .WithSummary("Binds the processed (clean, encrypted) version into the record's signed manifest through the caller's authority context; the document becomes Ready.");

        sessions.MapDelete("/{uploadSessionId:guid}", static async Task<Ok<UploadSessionResponse>> (Guid uploadSessionId, HttpContext http, UploadSessionHandlers handlers, CancellationToken cancellationToken) =>
                TypedResults.Ok(await handlers.CancelAsync(BusinessAuthentication.RequireContext(http), uploadSessionId, cancellationToken).ConfigureAwait(false)))
            .WithName("CancelUploadSession");

        var jobs = endpoints.MapGroup(ApiVersions.V1Prefix + "/my-upload-jobs").WithTags(OwnerName).WithFeatureOwner(OwnerName).RequireAuthorization(BusinessAuthentication.Policy);
        jobs.MapGet("/", static async Task<Ok<IReadOnlyList<UploadSessionResponse>>> (string? states, HttpContext http, UploadSessionHandlers handlers, CancellationToken cancellationToken) =>
            {
                var wanted = string.IsNullOrWhiteSpace(states)
                    ? new[] { UploadSessionStates.Open, UploadSessionStates.Completed, UploadSessionStates.Processing, UploadSessionStates.Processed, UploadSessionStates.Committed, UploadSessionStates.Rejected, UploadSessionStates.Failed }
                    : states.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                return TypedResults.Ok(await handlers.ListMineAsync(BusinessAuthentication.RequireContext(http), wanted, cancellationToken).ConfigureAwait(false));
            })
            .WithName("ListMyUploadJobs")
            .WithSummary("The caller's own upload sessions and their authoritative processing stage.");

        var documents = endpoints.MapGroup(ApiVersions.V1Prefix + "/form-records/{formRecordId:guid}/documents").WithTags(OwnerName).WithFeatureOwner(OwnerName).RequireAuthorization(BusinessAuthentication.Policy);

        documents.MapGet("/", static async Task<Ok<IReadOnlyList<DocumentSummaryResponse>>> (Guid formRecordId, HttpContext http, DocumentHandlers handlers, CancellationToken cancellationToken) =>
                TypedResults.Ok(await handlers.ListAsync(BusinessAuthentication.RequireContext(http), formRecordId, cancellationToken).ConfigureAwait(false)))
            .WithName("ListRecordDocuments")
            .WithSummary("Documents whose version leaf is in the accepted manifest and authorized for the caller.");

        documents.MapGet("/{fileAttachmentId:guid}", static async Task<Ok<DocumentDetailResponse>> (Guid formRecordId, Guid fileAttachmentId, HttpContext http, DocumentHandlers handlers, CancellationToken cancellationToken) =>
                TypedResults.Ok(await handlers.GetAsync(BusinessAuthentication.RequireContext(http), formRecordId, fileAttachmentId, cancellationToken).ConfigureAwait(false)))
            .WithName("GetRecordDocument");

        documents.MapGet("/{fileAttachmentId:guid}/download", static async Task (Guid formRecordId, Guid fileAttachmentId, int? versionNumber, HttpContext http, DocumentHandlers handlers, CancellationToken cancellationToken) =>
            {
                var content = await handlers.DownloadAsync(BusinessAuthentication.RequireContext(http), formRecordId, fileAttachmentId, versionNumber, cancellationToken).ConfigureAwait(false);
                http.Response.StatusCode = StatusCodes.Status200OK;
                http.Response.ContentType = content.ContentType;
                http.Response.ContentLength = content.Length;
                http.Response.Headers.CacheControl = "no-store";
                http.Response.Headers.ContentDisposition = new System.Net.Mime.ContentDisposition { DispositionType = "attachment", FileName = content.FileName }.ToString();
                http.Response.Headers.XContentTypeOptions = "nosniff";
                await content.WriteTo(http.Response.Body, cancellationToken).ConfigureAwait(false);
            })
            .Produces(StatusCodes.Status200OK, contentType: "application/octet-stream")
            .WithName("DownloadRecordDocument")
            .WithSummary("Authorized download: re-verifies the record projection, decrypts server-side, streams with no-store and attachment disposition.");

        documents.MapPost("/{fileAttachmentId:guid}/remove", static async Task<Ok<FormRecordResponse>> (Guid formRecordId, Guid fileAttachmentId, RemoveDocumentRequest request, HttpContext http, DocumentHandlers handlers, CancellationToken cancellationToken) =>
                TypedResults.Ok(await handlers.RemoveAsync(BusinessAuthentication.RequireContext(http), formRecordId, fileAttachmentId, request, FormDefinitionEndpoints.RequireIdempotencyKey(http), cancellationToken).ConfigureAwait(false)))
            .WithName("RemoveRecordDocument")
            .WithSummary("Removes the document from the accepted content by committing a new revision without its leaf; bytes and history are retained.");

        return endpoints;
    }
}
