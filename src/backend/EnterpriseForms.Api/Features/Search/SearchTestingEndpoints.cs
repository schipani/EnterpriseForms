using EnterpriseForms.Api.Composition;
using EnterpriseForms.Application.Features.Search.Verification;
using EnterpriseForms.Contracts.Features;
using Microsoft.AspNetCore.Http.HttpResults;

namespace EnterpriseForms.Api.Features.Search;

/// <summary>
/// Phase C7 M1 — mapped ONLY when the host environment is Testing (the isolated Docker security-test stack): runs the
/// trusted scope verification of the AC-5 path for the caller and reports counts and costs. It evaluates no predicate,
/// returns no field values and is not field-value search; the public search endpoints stay gated. It exists so the
/// hostile-business-database and limit tests can exercise the real Authority, PostgreSQL, SpiceDB and Vault path.
/// </summary>
public static class SearchTestingEndpoints
{
    public static IEndpointRouteBuilder MapSearchTestingEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var testing = endpoints.MapGroup(ApiVersions.V1Prefix + "/testing").WithTags(SearchEndpoints.OwnerName).WithFeatureOwner(SearchEndpoints.OwnerName).RequireAuthorization(BusinessAuthentication.Policy);
        testing.MapPost("/search-scope-verification", static async Task<Ok<SearchScopeVerificationResponse>> (SearchScopeVerificationRequest request, HttpContext http, TrustedScopeVerifier verifier, CancellationToken cancellationToken) =>
            {
                var scope = await verifier.VerifyAsync(BusinessAuthentication.RequireContext(http), request.FormDefinitionId, cancellationToken).ConfigureAwait(false);
                http.Response.Headers.CacheControl = "no-store";
                return TypedResults.Ok(new SearchScopeVerificationResponse(scope.SearchCatalogueSnapshotId, scope.FormDefinitionId, scope.AuthorizationRevision, scope.ContentIntegrityRevision, scope.CatalogueEntryCount, scope.RecordsWithoutContent,
                    scope.Records.Count, scope.Records.Count(r => r.IsCompleteProjection), scope.Records.Select(r => r.FormRecordId).ToList(),
                    scope.Metrics.AuthorityCalls, scope.Metrics.VerificationBatches, scope.Metrics.VerifiedBytes, scope.Metrics.Elapsed.TotalMilliseconds));
            })
            .WithName("TestingVerifySearchScope");
        return endpoints;
    }
}
