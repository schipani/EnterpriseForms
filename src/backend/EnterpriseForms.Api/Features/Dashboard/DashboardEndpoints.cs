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
