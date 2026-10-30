using EnterpriseForms.Api.Composition;
using EnterpriseForms.Application.Features.SecurityOperations.Incidents;
using EnterpriseForms.Contracts.Features;
using Microsoft.AspNetCore.Http.HttpResults;

namespace EnterpriseForms.Api.Features.SecurityOperations;

/// <summary>PG-056 security incidents. Every route requires the Authority's administer_security (Security Administrators); 403 otherwise.</summary>
public static class SecurityIncidentEndpoints
{
    public const string OwnerName = "SecurityOperations";

    public static IEndpointRouteBuilder MapSecurityIncidentEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup(ApiVersions.V1Prefix + "/security/incidents").WithTags(OwnerName).WithFeatureOwner(OwnerName).RequireAuthorization(BusinessAuthentication.Policy);

        group.MapGet("/", static async Task<Ok<SecurityIncidentPageResponse>> (string? q, string? type, string? severity, string? status, DateTimeOffset? from, DateTimeOffset? before, int? pageSize, string? continuationToken, HttpContext http, SecurityIncidentHandlers handlers, CancellationToken cancellationToken) =>
                TypedResults.Ok(await handlers.ListAsync(BusinessAuthentication.RequireContext(http), SecurityIncidentHandlers.Normalize(q, type, severity, status, from, before, pageSize), continuationToken, cancellationToken).ConfigureAwait(false)))
            .WithName("ListSecurityIncidents")
            .WithSummary("A page of the organization's security incidents, most recently detected first. q is an incident reference (INC-…), a record reference (REC-…, discoverable records only) or a full identifier.");

        group.MapGet("/summary", static async Task<Ok<SecurityIncidentSummaryResponse>> (int? periodDays, string? timeZone, HttpContext http, SecurityIncidentHandlers handlers, CancellationToken cancellationToken) =>
                TypedResults.Ok(await handlers.SummaryAsync(BusinessAuthentication.RequireContext(http), periodDays, timeZone, cancellationToken).ConfigureAwait(false)))
            .WithName("GetSecurityIncidentSummary")
            .WithSummary("Incident counts by status, severity and detection date. Zero incidents is not evidence that data was verified.");

        group.MapGet("/{securityIncidentId:guid}", static async Task<Ok<SecurityIncidentDetailResponse>> (Guid securityIncidentId, HttpContext http, SecurityIncidentHandlers handlers, CancellationToken cancellationToken) =>
                TypedResults.Ok(await handlers.GetAsync(BusinessAuthentication.RequireContext(http), securityIncidentId, cancellationToken).ConfigureAwait(false)))
            .WithName("GetSecurityIncident")
            .WithSummary("Incident metadata, detection history, notes and audit trail (audited: SECURITY_INCIDENT_VIEWED). No record content.");

        group.MapPatch("/{securityIncidentId:guid}", static async Task<Ok<SecurityIncidentDetailResponse>> (Guid securityIncidentId, UpdateSecurityIncidentRequest request, HttpContext http, SecurityIncidentHandlers handlers, CancellationToken cancellationToken) =>
                TypedResults.Ok(await handlers.UpdateAsync(BusinessAuthentication.RequireContext(http), securityIncidentId, request, cancellationToken).ConfigureAwait(false)))
            .WithName("UpdateSecurityIncident")
            .WithSummary("Change status or investigator (audited). 409 INCIDENT_MODIFIED_CONCURRENTLY when expectedIncidentRevision is stale. Closing never restores record visibility.");

        group.MapPost("/{securityIncidentId:guid}/notes", static async Task<Ok<SecurityIncidentDetailResponse>> (Guid securityIncidentId, AddSecurityIncidentNoteRequest request, HttpContext http, SecurityIncidentHandlers handlers, CancellationToken cancellationToken) =>
                TypedResults.Ok(await handlers.AddNoteAsync(BusinessAuthentication.RequireContext(http), securityIncidentId, request, cancellationToken).ConfigureAwait(false)))
            .WithName("AddSecurityIncidentNote")
            .WithSummary("Append an investigation note (audited without its text).");

        return endpoints;
    }
}
