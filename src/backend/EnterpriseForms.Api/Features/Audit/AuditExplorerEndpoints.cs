using EnterpriseForms.Api.Composition;
using EnterpriseForms.Application.Features.Audit.Explorer;
using EnterpriseForms.Contracts.Features;
using Microsoft.AspNetCore.Http.HttpResults;

namespace EnterpriseForms.Api.Features.Audit;

/// <summary>
/// C8 M4-2: PG-054 audit explorer and PG-055 audit event detail (feature Audit). Security Auditors only (Authority view_audit);
/// each request is authorized and recorded by the Authority before any row is read (AC-8.3). 403 AUDIT_VIEW_FORBIDDEN, 404,
/// 400 CURSOR_INVALID, 422, 429 RATE_LIMITED, 503 AUTHORITY_UNAVAILABLE / AUTHORITY_GATE_CLOSED / AUDIT_EVIDENCE_BUSY /
/// AUDIT_EVIDENCE_UNAVAILABLE / AUDIT_DELIVERY_FAILED. Responses are no-store (every response of this API).
/// </summary>
public static class AuditExplorerEndpoints
{
    public const string OwnerName = "Audit";

    public static IEndpointRouteBuilder MapAuditExplorerEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup(ApiVersions.V1Prefix + "/security/audit-events").WithTags(OwnerName).WithFeatureOwner(OwnerName).RequireAuthorization(BusinessAuthentication.Policy);

        group.MapGet("/", static async Task<Ok<AuditExplorerPageResponse>> (DateTimeOffset? from, DateTimeOffset? to, Guid? actorUserId, string? action, string? category, string? outcome, string? entityType, Guid? entityId,
                Guid? formRecordId, Guid? correlationId, bool? excludeAuditAccess, int? pageSize, string? cursor, HttpContext http, AuditExplorerHandlers handlers, CancellationToken cancellationToken) =>
                TypedResults.Ok(await handlers.SearchAsync(BusinessAuthentication.RequireContext(http),
                    AuditExplorerHandlers.Normalize(from, to, actorUserId, action, category, outcome, entityType, entityId, formRecordId, correlationId, excludeAuditAccess, pageSize), cursor, cancellationToken).ConfigureAwait(false)))
            .WithName("SearchAuditEvents")
            .WithSummary("PG-054: a page (at most 50) of the organization's audit events, newest first by event time with the sequence as tie-break (not commit order). Filters are ids, codes and dates only. Envelopes only: action label, actor, target, form, record and field ids. Business projection, not independently verified (AUD-C7-01/G24).");

        group.MapGet("/{auditEventId:guid}", static async Task<Ok<AuditEventDetailResponse>> (Guid auditEventId, HttpContext http, AuditExplorerHandlers handlers, CancellationToken cancellationToken) =>
                TypedResults.Ok(await handlers.GetAsync(BusinessAuthentication.RequireContext(http), auditEventId, cancellationToken).ConfigureAwait(false)))
            .WithName("GetAuditEvent")
            .WithSummary("PG-055: one audit event with the redaction catalog's description and allowlisted metadata; free text, field keys and labels, option text, design diffs and record scopes are withheld (label only). Another organization's id and an unknown id are the same 404.");

        // C8 M4-3 (AC-8.2): the security-command timeline, read and served by the Authority (no business delivery row, R-5).
        var timeline = endpoints.MapGroup(ApiVersions.V1Prefix + "/security/audit").WithTags(OwnerName).WithFeatureOwner(OwnerName).RequireAuthorization(BusinessAuthentication.Policy);
        timeline.MapGet("/security-commands", static async Task<Ok<SecurityCommandTimelinePageResponse>> (string? kind, string? state, DateTimeOffset? from, DateTimeOffset? to, int? pageSize, string? cursor,
                HttpContext http, SecurityCommandTimelineHandler handler, CancellationToken cancellationToken) =>
                TypedResults.Ok(await handler.ReadAsync(BusinessAuthentication.RequireContext(http), SecurityCommandTimelineHandler.Normalize(kind, state, from, to, pageSize), cursor, cancellationToken).ConfigureAwait(false)))
            .WithName("ReadSecurityCommandTimeline")
            .WithSummary("AC-8.2: a page (at most 50) of the organization's security-command timeline for Security Auditors: command rows in every state, including Rejected and FailedReconciliation, and allowlisted audit-stream entries, newest first. Filters are one kind code, one state code and dates. Read and served by the Authorization Authority after it recorded the access (AuditAccessAuthorized, served_by authority); envelopes carry identifiers and codes only, with withheld fields named but never shown. Journal-backed items show their journal evidence; command-row-only items are not cryptographically verified.");

        return endpoints;
    }
}
