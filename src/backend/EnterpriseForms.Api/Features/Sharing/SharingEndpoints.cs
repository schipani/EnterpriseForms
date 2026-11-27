using EnterpriseForms.Api.Composition;
using EnterpriseForms.Api.Features.FormDefinitions;
using EnterpriseForms.Application.Features.Sharing.CreateShare;
using EnterpriseForms.Application.Features.Sharing.GetShare;
using EnterpriseForms.Application.Features.Sharing.ListShares;
using EnterpriseForms.Application.Features.Sharing.MyAccess;
using EnterpriseForms.Application.Features.Sharing.RevokeShare;
using EnterpriseForms.Application.Features.Sharing.ShareHistory;
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

        // C10 M2 (WP-0 §3.5, D10-11): record-scoped sharing history from the Authority; pending changes keep their state.
        group.MapGet("/history", static async Task<Ok<RecordSecurityHistoryResponse>> (Guid formRecordId, HttpContext http, RecordSecurityHistoryHandler handler, CancellationToken cancellationToken) =>
            {
                var context = BusinessAuthentication.RequireContext(http);
                http.Response.Headers.CacheControl = "no-store";
                return TypedResults.Ok(await handler.HandleAsync(context, formRecordId, ownershipOnly: false, cancellationToken).ConfigureAwait(false));
            })
            .WithName("GetRecordSharingHistory")
            .WithSummary("Sharing and ownership changes of a record from the authority's committed state, newest first, each with its command state (owner or access manager; others 404).");

        // C10 M2 (WP-0 §3.5, PG-023): what the share's recipient can do now and why, decided by the Authority.
        group.MapGet("/{recordShareId:guid}/effective-scope", static async Task<Ok<ShareEffectiveScopeResponse>> (Guid formRecordId, Guid recordShareId, HttpContext http, MyAccessHandlers handler, CancellationToken cancellationToken) =>
            {
                var context = BusinessAuthentication.RequireContext(http);
                http.Response.Headers.CacheControl = "no-store";
                return TypedResults.Ok(await handler.GetEffectiveScopeAsync(context, formRecordId, recordShareId, cancellationToken).ConfigureAwait(false));
            })
            .WithName("GetRecordShareEffectiveScope")
            .WithSummary("What the share's recipient can do through it now (Active only while the share and its lineage are), with its lineage; same readers as the share details.");

        var recordGroup = endpoints.MapGroup(ApiVersions.V1Prefix + "/form-records/{formRecordId:guid}").WithTags(OwnerName).WithFeatureOwner(OwnerName).RequireAuthorization(BusinessAuthentication.Policy);
        recordGroup.MapGet("/ownership-history", static async Task<Ok<RecordSecurityHistoryResponse>> (Guid formRecordId, HttpContext http, RecordSecurityHistoryHandler handler, CancellationToken cancellationToken) =>
            {
                var context = BusinessAuthentication.RequireContext(http);
                http.Response.Headers.CacheControl = "no-store";
                return TypedResults.Ok(await handler.HandleAsync(context, formRecordId, ownershipOnly: true, cancellationToken).ConfigureAwait(false));
            })
            .WithName("GetRecordOwnershipHistory")
            .WithSummary("Ownership changes of a record from the authority's committed state, newest first, each with its command state.");

        // C10 M2 (WP-0 §3.5, PG-066): the caller's own access sources from the Authority.
        var meGroup = endpoints.MapGroup(ApiVersions.V1Prefix + "/me").WithTags(OwnerName).WithFeatureOwner(OwnerName).RequireAuthorization(BusinessAuthentication.Policy);
        meGroup.MapGet("/access", static async Task<Ok<MyAccessResponse>> (HttpContext http, MyAccessHandlers handler, CancellationToken cancellationToken) =>
            {
                var context = BusinessAuthentication.RequireContext(http);
                http.Response.Headers.CacheControl = "no-store";
                return TypedResults.Ok(await handler.GetMyAccessAsync(context, cancellationToken).ConfigureAwait(false));
            })
            .WithName("GetMyAccess")
            .WithSummary("The caller's own owned, team-owned, direct-share and team-share access (expired shares omitted, expiring soon flagged), from the authority.");

        // C10 M2 (WP-0 §3.7, D8-4, PG-053): administrator reassignment of a member's records (one transfer command per record).
        var members = endpoints.MapGroup(ApiVersions.V1Prefix + "/members/{memberUserId:guid}").WithTags(OwnerName).WithFeatureOwner(OwnerName).RequireAuthorization(BusinessAuthentication.Policy);
        members.MapGet("/owned-records", static async Task<Ok<MemberOwnedRecordsResponse>> (Guid memberUserId, HttpContext http, ReassignRecordsHandler handler, CancellationToken cancellationToken) =>
            {
                var context = BusinessAuthentication.RequireContext(http);
                http.Response.Headers.CacheControl = "no-store";
                return TypedResults.Ok(await handler.ListAsync(context, memberUserId, cancellationToken).ConfigureAwait(false));
            })
            .WithName("GetMemberOwnedRecords")
            .WithSummary("Active records a member directly owns (ids only), for Security Administrators and organization owners.");
        members.MapPost("/reassign-records", static async Task<Ok<ReassignRecordsResponse>> (Guid memberUserId, ReassignRecordsRequest request, HttpContext http, ReassignRecordsHandler handler, CancellationToken cancellationToken) =>
            {
                var context = BusinessAuthentication.RequireContext(http);
                return TypedResults.Ok(await handler.ReassignAsync(context, memberUserId, request, FormDefinitionEndpoints.RequireIdempotencyKey(http), cancellationToken).ConfigureAwait(false));
            })
            .WithName("ReassignMemberRecords")
            .WithSummary("Transfers each record the member directly owns with its own TransferOwnership command (reason and step-up; administrator override rules apply); reports each record's outcome.");

        // C10 M2 (WP-0 §3.5, D10-3): the authoritative share details; the ETag is the Authority's committed share revision.
        group.MapGet("/{recordShareId:guid}", static async Task<Ok<ShareDetailsResponse>> (Guid formRecordId, Guid recordShareId, HttpContext http, GetShareHandler handler, CancellationToken cancellationToken) =>
            {
                var context = BusinessAuthentication.RequireContext(http);
                var response = await handler.HandleAsync(context, formRecordId, recordShareId, cancellationToken).ConfigureAwait(false);
                http.Response.Headers.ETag = ShareRevisionPrecondition.Format(recordShareId, response.ShareRevision);
                http.Response.Headers.CacheControl = "no-store";
                return TypedResults.Ok(response);
            })
            .WithName("GetRecordShare")
            .WithSummary("One share's committed state from the authority (owner, or a Manager on its committed lineage; others 404). The ETag carries the authoritative share revision for PATCH If-Match.");

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
