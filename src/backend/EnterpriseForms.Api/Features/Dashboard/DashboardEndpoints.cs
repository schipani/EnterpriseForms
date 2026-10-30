using EnterpriseForms.Api.Composition;
using EnterpriseForms.Application.Features.Audit;
using EnterpriseForms.Application.Features.Dashboard;
using EnterpriseForms.Contracts.Features;
using Microsoft.AspNetCore.Http.HttpResults;

namespace EnterpriseForms.Api.Features.Dashboard;

public static class DashboardEndpoints
{
    public const string OwnerName = "Dashboard";

    public static IEndpointRouteBuilder MapDashboardEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup(ApiVersions.V1Prefix + "/dashboard").WithTags(OwnerName).WithFeatureOwner(OwnerName).RequireAuthorization(BusinessAuthentication.Policy);

        group.MapGet("/", static async Task<Ok<DashboardResponse>> (HttpContext http, DashboardHandler handler, CancellationToken cancellationToken) =>
                TypedResults.Ok(await handler.GetAsync(BusinessAuthentication.RequireContext(http), cancellationToken).ConfigureAwait(false)))
            .WithName("GetDashboard")
            .WithSummary("Authorized widget descriptors from the static catalog; no inline data, no arbitrary endpoints.");

        group.MapGet("/widgets/{widgetIdentifier}/count", static async Task<Ok<DashboardCountResponse>> (string widgetIdentifier, HttpContext http, DashboardHandler handler, TimeProvider clock, CancellationToken cancellationToken) =>
                TypedResults.Ok(await handler.CountAsync(BusinessAuthentication.RequireContext(http), widgetIdentifier, clock, cancellationToken).ConfigureAwait(false)))
            .WithName("GetDashboardWidgetCount")
            .WithSummary("Counts computed only over resources discoverable by the caller.");

        // PG-006 redesign: one request per section so a failing section never blocks the others. Lists default to 15 rows.
        group.MapGet("/record-statistics", static async Task<Ok<DashboardRecordStatisticsResponse>> (int? periodDays, string? timeZone, HttpContext http, DashboardSectionHandlers handlers, CancellationToken cancellationToken) =>
                TypedResults.Ok(await handlers.RecordStatisticsAsync(BusinessAuthentication.RequireContext(http), periodDays, timeZone, cancellationToken).ConfigureAwait(false)))
            .WithName("GetDashboardRecordStatistics")
            .WithSummary("Status counts and created/submitted activity over the caller's complete Authority-discoverable top-level records (listing metadata, unverified). periodDays 7, 30 or 90; timeZone is an IANA id (UTC otherwise).");

        group.MapGet("/upload-statistics", static async Task<Ok<DashboardUploadStatisticsResponse>> (int? periodDays, string? timeZone, HttpContext http, DashboardSectionHandlers handlers, CancellationToken cancellationToken) =>
                TypedResults.Ok(await handlers.UploadStatisticsAsync(BusinessAuthentication.RequireContext(http), periodDays, timeZone, cancellationToken).ConfigureAwait(false)))
            .WithName("GetDashboardUploadStatistics")
            .WithSummary("The caller's own upload sessions in the period, by outcome group.");

        group.MapGet("/uploads", static async Task<Ok<DashboardUploadPageResponse>> (string? q, string? status, DateTimeOffset? from, DateTimeOffset? before, int? pageSize, string? continuationToken, HttpContext http, DashboardSectionHandlers handlers, CancellationToken cancellationToken) =>
                TypedResults.Ok(await handlers.UploadsAsync(BusinessAuthentication.RequireContext(http), q, status, from, before, pageSize, continuationToken, cancellationToken).ConfigureAwait(false)))
            .WithName("ListDashboardUploads")
            .WithSummary("A page of the caller's own upload sessions, newest first. q matches the file name (literal, case-insensitive); status is InProgress, Completed, Failed or Cancelled.");

        group.MapGet("/activity", static async Task<Ok<DashboardActivityPageResponse>> (string? category, string? outcome, DateTimeOffset? from, DateTimeOffset? before, int? pageSize, string? continuationToken, HttpContext http, DashboardSectionHandlers handlers, CancellationToken cancellationToken) =>
                TypedResults.Ok(await handlers.ActivityAsync(BusinessAuthentication.RequireContext(http), category, outcome, from, before, pageSize, continuationToken, cancellationToken).ConfigureAwait(false)))
            .WithName("ListDashboardActivity")
            .WithSummary("A page of the caller's own audit events, newest first, filtered by category, outcome and time range.");

        group.MapGet("/forms", static async Task<Ok<DashboardFormPageResponse>> (string? q, string? access, string? status, int? pageSize, string? continuationToken, HttpContext http, DashboardSectionHandlers handlers, CancellationToken cancellationToken) =>
                TypedResults.Ok(await handlers.FormsAsync(BusinessAuthentication.RequireContext(http), q, access, status, pageSize, continuationToken, cancellationToken).ConfigureAwait(false)))
            .WithName("ListDashboardForms")
            .WithSummary("A page of the forms the caller may use or design (Authority decisions), by name. access is create or design; status is published or unpublished.");

        var audit = endpoints.MapGroup(ApiVersions.V1Prefix + "/my-activity").WithTags("Audit").WithFeatureOwner("Audit").RequireAuthorization(BusinessAuthentication.Policy);
        audit.MapGet("/", static async Task<Ok<IReadOnlyList<AuditEventResponse>>> (int? limit, HttpContext http, GetRecordActivityHandler handler, CancellationToken cancellationToken) =>
                TypedResults.Ok(await handler.MyActivityAsync(BusinessAuthentication.RequireContext(http), limit ?? 25, cancellationToken).ConfigureAwait(false)))
            .WithName("GetMyActivity");

        var activity = endpoints.MapGroup(ApiVersions.V1Prefix + "/form-records/{formRecordId:guid}/activity").WithTags("Audit").WithFeatureOwner("Audit").RequireAuthorization(BusinessAuthentication.Policy);
        activity.MapGet("/", static async Task<Ok<RecordActivityResponse>> (Guid formRecordId, int? limit, HttpContext http, GetRecordActivityHandler handler, CancellationToken cancellationToken) =>
                TypedResults.Ok(await handler.HandleAsync(BusinessAuthentication.RequireContext(http), formRecordId, limit ?? 100, cancellationToken).ConfigureAwait(false)))
            .WithName("GetRecordActivity")
            .WithSummary("Record activity timeline (local audit projection; authority Read permission required).");

        return endpoints;
    }
}
