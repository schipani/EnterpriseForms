using EnterpriseForms.Api.Composition;
using EnterpriseForms.Application.Features.Search.FieldSearch;
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

        search.MapGet("/search-capabilities", static Ok<FieldSearchCapabilitiesResponse> (Guid formDefinitionId, HttpContext http, FieldSearchHandler handler) =>
            {
                var response = handler.Capabilities(BusinessAuthentication.RequireContext(http), formDefinitionId);
                http.Response.Headers.CacheControl = "no-store";
                return TypedResults.Ok(response);
            })
            .WithName("GetFieldSearchCapabilities")
            .WithSummary("Server-authoritative Advanced Search capabilities for one form and the caller: the fields that may be used as predicates, their value types and allowed operators, and fields the caller may know about that cannot be searched (Encrypted for privacy — not searchable). A field the caller may not know about is in neither list. Phase C7 M0: fieldValueSearchAvailable is false (TRUSTED_SEARCH_NOT_ENABLED) and no fields are offered, for every form.")
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        search.MapPost("/field-search", static Ok<FieldSearchResponse> (FieldSearchRequest request, HttpContext http, FieldSearchHandler handler) =>
            {
                http.Response.Headers.CacheControl = "no-store";
                return TypedResults.Ok(handler.Search(BusinessAuthentication.RequireContext(http), request));
            })
            .WithName("SearchFormRecordsByFieldValues")
            .WithSummary("Advanced field search over one selected form: up to five conditions combined with AND, each on a field from the capability list with an allowed operator and a typed invariant value, plus the My Records metadata filters. A result is returned only when every eligible record was independently enumerated and verified at one snapshot; otherwise a typed failure (SEARCH_FIELD_NOT_ALLOWED, SEARCH_OPERATOR_NOT_SUPPORTED, SEARCH_VERIFICATION_LIMIT, SEARCH_SNAPSHOT_CHANGED, SEARCH_VERIFICATION_UNAVAILABLE), never a partial page. Phase C7 M0: every well-formed request is refused with 503 SEARCH_VERIFICATION_UNAVAILABLE (unavailable_reason TRUSTED_SEARCH_NOT_ENABLED, retryable false); malformed requests get 422 SEARCH_REQUEST_INVALID or SEARCH_OPERATOR_NOT_SUPPORTED.")
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        search.MapPost("/field-search/page", static Ok<FieldSearchResponse> (FieldSearchPageRequest request, HttpContext http) =>
            {
                http.Response.Headers.CacheControl = "no-store";
                return TypedResults.Ok(FieldSearchHandler.Page(BusinessAuthentication.RequireContext(http), request));
            })
            .WithName("PageFieldSearchResults")
            .WithSummary("Another page of a verified advanced-search result: the opaque cursor is bound to the caller, session, query digest and verified snapshot; a changed authorization or content snapshot returns 409 SEARCH_SNAPSHOT_CHANGED (restart from page one). Phase C7 M0: refused with 503 SEARCH_VERIFICATION_UNAVAILABLE / TRUSTED_SEARCH_NOT_ENABLED.")
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        return endpoints;
    }
}
