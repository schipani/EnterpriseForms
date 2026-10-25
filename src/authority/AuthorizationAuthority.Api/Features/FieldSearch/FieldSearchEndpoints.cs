using AuthorizationAuthority.Api.Composition;
using AuthorizationAuthority.Application.Features.FieldSearch;
using AuthorizationAuthority.Contracts.V1;
using Microsoft.AspNetCore.Http.HttpResults;

namespace AuthorizationAuthority.Api.Features.FieldSearch;

/// <summary>Phase C7 M2 (AC-6): field search capabilities and configuration. Approvals, withdrawals and query grants are security commands.</summary>
public static class FieldSearchEndpoints
{
    public static IEndpointRouteBuilder MapFieldSearchEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup(InternalApi.Prefix).WithTags("FieldSearch").RequireAuthorization(InternalAuthentication.WorkloadPolicy);

        group.MapPost("/field-search/capabilities", static async Task<Ok<FieldSearchCapabilitiesResponse>> (FieldSearchCapabilitiesRequest request, HttpContext context, FieldSearchCapabilityService service, DelegatedUserAssertionValidator validator, CancellationToken cancellationToken) =>
            {
                var actor = InternalAuthentication.RequireActor(context, validator);
                return TypedResults.Ok(await service.GetAsync(actor, request, InternalAuthentication.Correlation(context), cancellationToken).ConfigureAwait(false));
            })
            .WithName("GetFieldSearchCapabilities")
            .WithSummary("Searchable fields of a form for the delegated user (query_fields + active AC-6 approvals; labels and options from the approval snapshot) and, for designers and security administrators only, the search configuration of the latest registered published version. Identical empty answer for no access and unknown forms.");

        group.MapPost("/field-search/administration", static async Task<Ok<FieldSearchAdministrationResponse>> (FieldSearchAdministrationRequest request, HttpContext context, FieldSearchCapabilityService service, DelegatedUserAssertionValidator validator, CancellationToken cancellationToken) =>
            {
                var actor = InternalAuthentication.RequireActor(context, validator);
                return TypedResults.Ok(await service.AdministerAsync(actor, request, InternalAuthentication.Correlation(context), cancellationToken).ConfigureAwait(false));
            })
            .WithName("GetFieldSearchAdministration")
            .WithSummary("Phase C7 M3: forms of the caller's organization with an Authority-registered published version (title, version, field and approval counts) for search-approval administration. administer_security only; no Designer or record access needed or granted.");

        return endpoints;
    }
}
