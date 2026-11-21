using EnterpriseForms.Api.Composition;
using EnterpriseForms.Application.Features.Audit.Overview;
using EnterpriseForms.Contracts.Features;
using Microsoft.AspNetCore.Http.HttpResults;

namespace EnterpriseForms.Api.Features.Audit;

/// <summary>
/// C8 M4-4 (AC-8.4, AC-8.5; contract §6.1, owner feature Audit; PG-052 is owned by SecurityOperations in the web app). One
/// independently refreshable section per request. 403 SECURITY_ADMINISTRATION_FORBIDDEN, 404 for an unknown section, 503
/// AUTHORITY_UNAVAILABLE. Responses are no-store (every response of this API). Incidents and search approvals are not served here:
/// they keep their own endpoints and authorization (OD-6).
/// </summary>
public static class SecurityOverviewEndpoints
{
    public static IEndpointRouteBuilder MapSecurityOverviewEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup(ApiVersions.V1Prefix + "/security/overview").WithTags(AuditExplorerEndpoints.OwnerName).WithFeatureOwner(AuditExplorerEndpoints.OwnerName).RequireAuthorization(BusinessAuthentication.Policy);

        group.MapGet("/{section}", static async Task<Ok<SecurityOverviewSectionResponse>> (string section, HttpContext http, SecurityOverviewHandler handler, CancellationToken cancellationToken) =>
                TypedResults.Ok(await handler.ReadAsync(BusinessAuthentication.RequireContext(http), SecurityOverviewHandler.Normalize(section), cancellationToken).ConfigureAwait(false)))
            .WithName("ReadSecurityOverviewSection")
            .WithSummary("PG-052 / AC-8.4 / AC-8.5: one Security Home section — gate, commands or archive (security overview) or coverage (field protection coverage). Authorized by the Authorization Authority from its committed state (Effective-edge rule, one read-only snapshot), so it is available while the organization gate is closed: gate, commands and coverage need Security Administrator; archive needs Security Administrator or Security Auditor. Counts, codes, kinds, revisions, times and identifiers only. Coverage staleness is an organization-wide comparison, not a per-form verification; this is operational information, not a compliance conclusion.");

        return endpoints;
    }
}
