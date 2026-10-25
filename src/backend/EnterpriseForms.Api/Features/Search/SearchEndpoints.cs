using EnterpriseForms.Api.Composition;
using EnterpriseForms.Application.Features.Search.FieldSearch;
using EnterpriseForms.Application.Features.Search.FieldSearchPolicy;
using EnterpriseForms.Contracts.Features;
using Microsoft.AspNetCore.Http.HttpResults;

namespace EnterpriseForms.Api.Features.Search;

/// <summary>
/// Phase C7 Advanced Field Search (`Search` feature, PG-015). Predicate values travel only in POST bodies, never in URLs;
/// responses are no-store. M0: execution is refused server-side until the Authority's AC-5 catalogue and AC-6 policy exist.
/// </summary>
public static class SearchEndpoints
{
    public const string OwnerName = "Search";

    public static IEndpointRouteBuilder MapSearchEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var search = endpoints.MapGroup(ApiVersions.V1Prefix + "/form-records").WithTags(OwnerName).WithFeatureOwner(OwnerName).RequireAuthorization(BusinessAuthentication.Policy);

        search.MapGet("/search-capabilities", static async Task<Ok<FieldSearchCapabilitiesResponse>> (Guid formDefinitionId, HttpContext http, FieldSearchHandler handler, CancellationToken cancellationToken) =>
            {
                var response = await handler.CapabilitiesAsync(BusinessAuthentication.RequireContext(http), formDefinitionId, cancellationToken).ConfigureAwait(false);
                http.Response.Headers.CacheControl = "no-store";
                return TypedResults.Ok(response);
            })
            .WithName("GetFieldSearchCapabilities")
            .WithSummary("Server-authoritative Advanced Search capabilities for one form and the caller: the fields that may be used as predicates, their value types and allowed operators, and fields the caller may know about that cannot be searched (Encrypted for privacy — not searchable). A field the caller may not know about is in neither list. Phase C7 M2: fields come from the Authority (AC-6 approvals and the caller's field_searcher permission, with the approval's frozen labels and options); fieldValueSearchAvailable stays false (TRUSTED_SEARCH_NOT_ENABLED) because execution (M3) is not enabled.")
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        search.MapPost("/field-search", static async Task<Ok<FieldSearchResponse>> (FieldSearchRequest request, HttpContext http, FieldSearchHandler handler, CancellationToken cancellationToken) =>
            {
                http.Response.Headers.CacheControl = "no-store";
                return TypedResults.Ok(await handler.SearchAsync(BusinessAuthentication.RequireContext(http), request, cancellationToken).ConfigureAwait(false));
            })
            .WithName("SearchFormRecordsByFieldValues")
            .WithSummary("Advanced field search over one selected form: up to five conditions combined with AND, each on a field from the capability list with an allowed operator and a typed invariant value, plus the My Records metadata filters. A result is returned only when every eligible record was independently enumerated and verified at one snapshot; otherwise a typed failure (SEARCH_FIELD_NOT_ALLOWED, SEARCH_OPERATOR_NOT_SUPPORTED, SEARCH_VERIFICATION_LIMIT, SEARCH_SNAPSHOT_CHANGED, SEARCH_VERIFICATION_UNAVAILABLE), never a partial page. Each condition is first authorized against the Authority's capabilities (422 SEARCH_FIELD_NOT_ALLOWED / SEARCH_OPERATOR_NOT_SUPPORTED). Phase C7 M3: executed only where the fail-closed execution gate is on (isolated Development/Testing); otherwise 503 SEARCH_VERIFICATION_UNAVAILABLE (TRUSTED_SEARCH_NOT_ENABLED). Overload: 503 SEARCH_VERIFICATION_UNAVAILABLE (SEARCH_CAPACITY_EXCEEDED, retryable).")
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        search.MapPost("/field-search/page", static async Task<Ok<FieldSearchResponse>> (FieldSearchPageRequest request, HttpContext http, FieldSearchHandler handler, CancellationToken cancellationToken) =>
            {
                http.Response.Headers.CacheControl = "no-store";
                return TypedResults.Ok(await handler.PageAsync(BusinessAuthentication.RequireContext(http), request, cancellationToken).ConfigureAwait(false));
            })
            .WithName("PageFieldSearchResults")
            .WithSummary("Another page of a verified advanced-search result: the opaque cursor is bound to the caller, session, query digest and verified snapshot; a changed authorization or content snapshot returns 409 SEARCH_SNAPSHOT_CHANGED (restart from page one). Nothing is re-evaluated: the page comes from the verified result the cursor carries, after the Authority re-validates its snapshot.")
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        // ---- Phase C7 M2 (AC-6): search approval configuration; every decision is the Authority's.
        var designer = endpoints.MapGroup(ApiVersions.V1Prefix + "/form-definitions/{formDefinitionId:guid}").WithTags(OwnerName).WithFeatureOwner(OwnerName).RequireAuthorization(BusinessAuthentication.Policy);

        var organization = endpoints.MapGroup(ApiVersions.V1Prefix + "/organization").WithTags(OwnerName).WithFeatureOwner(OwnerName).RequireAuthorization(BusinessAuthentication.Policy);
        organization.MapGet("/search-approvals", static async Task<Ok<FieldSearchAdministrationResponse>> (HttpContext http, FieldSearchPolicyHandlers handlers, CancellationToken cancellationToken) =>
            {
                http.Response.Headers.CacheControl = "no-store";
                return TypedResults.Ok(await handlers.GetAdministrationAsync(BusinessAuthentication.RequireContext(http), cancellationToken).ConfigureAwait(false));
            })
            .WithName("GetSearchApprovalAdministration")
            .WithSummary("Phase C7 M3: for security administrators (administer_security) only — the organization's forms with an Authority-registered published version, their registered title and version, field and approval counts. Search approval administration without Designer or record access; 403 for everyone else.")
            .ProducesProblem(StatusCodes.Status403Forbidden);

        designer.MapGet("/field-search", static async Task<Ok<FieldSearchConfigurationResponse>> (Guid formDefinitionId, HttpContext http, FieldSearchPolicyHandlers handlers, CancellationToken cancellationToken) =>
            {
                http.Response.Headers.CacheControl = "no-store";
                return TypedResults.Ok(await handlers.GetConfigurationAsync(BusinessAuthentication.RequireContext(http), formDefinitionId, cancellationToken).ConfigureAwait(false));
            })
            .WithName("GetFieldSearchConfiguration")
            .WithSummary("Search configuration of the form's latest Authority-registered published version: per field its classification, protection, eligibility, approval state and operators. Designers of the form and security administrators only (others: 404). Encrypted fields are reported as Encrypted (Encrypted for privacy — not searchable).");

        designer.MapPost("/fields/{fieldDefinitionId:guid}/search-approval", static async Task<Ok<FieldSearchConfigurationResponse>> (Guid formDefinitionId, Guid fieldDefinitionId, ApproveFieldSearchRequest request, HttpContext http, FieldSearchPolicyHandlers handlers, CancellationToken cancellationToken) =>
                TypedResults.Ok(await handlers.ApproveAsync(BusinessAuthentication.RequireContext(http), formDefinitionId, fieldDefinitionId, request, cancellationToken).ConfigureAwait(false)))
            .WithName("ApproveFieldSearch")
            .WithSummary("Asks the Authority to approve plaintext value search of a field for the given operators (security administrators only; governance reference required). The Authority refuses encrypted, Confidential/Restricted and below-floor fields, unregistered versions and unsupported operators, and refuses every approval while step-up assurance (PB-08) is unavailable — in production and staging always.")
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        designer.MapPost("/fields/{fieldDefinitionId:guid}/search-approval/withdraw", static async Task<Ok<FieldSearchConfigurationResponse>> (Guid formDefinitionId, Guid fieldDefinitionId, WithdrawFieldSearchRequest request, HttpContext http, FieldSearchPolicyHandlers handlers, CancellationToken cancellationToken) =>
                TypedResults.Ok(await handlers.WithdrawAsync(BusinessAuthentication.RequireContext(http), formDefinitionId, fieldDefinitionId, request, cancellationToken).ConfigureAwait(false)))
            .WithName("WithdrawFieldSearch")
            .WithSummary("Withdraws a field's search approval (security administrators only, reason required).");

        designer.MapPut("/field-searchers", static async Task<NoContent> (Guid formDefinitionId, UpdateFieldSearchersRequest request, HttpContext http, FieldSearchPolicyHandlers handlers, CancellationToken cancellationToken) =>
            {
                await handlers.UpdateSearchersAsync(BusinessAuthentication.RequireContext(http), formDefinitionId, request, cancellationToken).ConfigureAwait(false);
                return TypedResults.NoContent();
            })
            .WithName("UpdateFieldSearchers")
            .WithSummary("Grants or revokes the form's field_searcher relation (query permission for its approved fields) for users or teams. Security administrators only. Form access, record ownership and design access never imply it.");

        return endpoints;
    }
}
