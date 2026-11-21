using AuthorizationAuthority.Api.Composition;
using AuthorizationAuthority.Application.Common;
using AuthorizationAuthority.Application.Features.Authorization;
using AuthorizationAuthority.Application.Features.SecurityOverview;
using AuthorizationAuthority.Contracts.V1;
using Microsoft.AspNetCore.Http.HttpResults;

namespace AuthorizationAuthority.Api.Features.SecurityOverview;

/// <summary>
/// C8 M4-4 (AC-8.4, AC-8.5; PHASE_C8_M4_AC8_SECURITY_CONTRACT.md §5.4, §5.5): the Security Home reads. Business workload plus
/// delegated user; the Authority binds the user to the organization and authorizes with the Effective-edge rule in one read-only
/// snapshot. Anyone not authorized for the section — unbound, disabled, another organization — gets the same 404. Responses are
/// no-store. Coverage is a separate user-plane route; the verifier route <c>/verifier/protection-coverage</c> is unchanged.
/// </summary>
public static class SecurityOverviewEndpoints
{
    public const string OverviewRoute = InternalApi.Prefix + "/organizations/security-overview";

    public const string CoverageRoute = InternalApi.Prefix + "/field-protection/coverage";

    public static IEndpointRouteBuilder MapSecurityOverviewEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost(OverviewRoute, static async Task<Ok<SecurityOverviewResponse>> (string? section, SecurityOverviewRequest request, HttpContext context, SecurityOverviewService service, DelegatedUserAssertionValidator validator, CancellationToken cancellationToken) =>
            {
                var actor = InternalAuthentication.RequireActor(context, validator);
                var response = await service.ReadAsync(actor, section, request, cancellationToken).ConfigureAwait(false);
                context.Response.Headers.CacheControl = "no-store";
                return TypedResults.Ok(response);
            })
            .WithTags("SecurityOverview")
            .Produces<ErrorResponse>(StatusCodes.Status404NotFound)
            .Produces<ErrorResponse>(StatusCodes.Status422UnprocessableEntity)
            .Produces<ErrorResponse>(StatusCodes.Status503ServiceUnavailable)
            .RequireAuthorization(InternalAuthentication.WorkloadPolicy)
            .WithName("ReadSecurityOverview")
            .WithSummary("AC-8.4: one section (gate, commands or archive) of the organization's security overview for the delegated user. Authorized by the Effective-edge rule (enabled binding, current organization role edge, Effective granting command) in one REPEATABLE READ READ ONLY snapshot, without SpiceDB, so it answers while the gate is closed: gate and commands need system_administrator; archive needs system_administrator or security_auditor. Counts, codes, kinds, revisions and times only; no payload, target or free text. Otherwise 404.");

        endpoints.MapPost(CoverageRoute, static async Task<Ok<SecurityCoverageResponse>> (SecurityCoverageRequest request, HttpContext context, SecurityCoverageService service, DelegatedUserAssertionValidator validator, CancellationToken cancellationToken) =>
            {
                var actor = InternalAuthentication.RequireActor(context, validator);
                var response = await service.ReadAsync(actor, request, cancellationToken).ConfigureAwait(false);
                context.Response.Headers.CacheControl = "no-store";
                return TypedResults.Ok(response);
            })
            .WithTags("SecurityOverview")
            .Produces<ErrorResponse>(StatusCodes.Status404NotFound)
            .Produces<ErrorResponse>(StatusCodes.Status422UnprocessableEntity)
            .Produces<ErrorResponse>(StatusCodes.Status503ServiceUnavailable)
            .RequireAuthorization(InternalAuthentication.WorkloadPolicy)
            .WithName("ReadProtectionCoverage")
            .WithSummary("AC-8.5 (DR-C7-5): every current field protection decision of the organization with its latest Authority attestation, as counts and identifiers, for a delegated Security Administrator (Effective-edge rule, system_administrator only; otherwise 404). Computed from Authority protection resources and attestations in one read-only snapshot: no record ids, no record enumeration, no SpiceDB call, no verifier route. Missing when a decision has no attestation; StaleOrganizationWide when the organization's content-integrity revision advanced after the attestation (an organization-wide comparison, not a per-form verification).");

        return endpoints;
    }

    public static IServiceCollection AddSecurityOverview(this IServiceCollection services)
    {
        services.AddSingleton<SecurityOverviewService>();
        services.AddSingleton<SecurityCoverageService>();
        return services;
    }
}
