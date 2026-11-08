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

        // IX-3 (R1 §G.3): runs one Advanced Field Search through the real handler with the Shadow comparison forced (the sample is
        // ignored) and returns V1's total or error and the comparison. Requires FieldSearch:Engine=Shadow. Counts and codes only.
        testing.MapPost("/field-search-shadow", static async Task<Results<Ok<FieldSearchShadowTestingResponse>, ProblemHttpResult>> (FieldSearchRequest request, HttpContext http,
                EnterpriseForms.Application.Features.Search.FieldSearch.FieldSearchHandler handler, CancellationToken cancellationToken) =>
            {
                // Bearer callers of the test harness have no cookie session; they name an Authority session they registered for the same
                // identity (the Authority refuses any session of another identity, so this cannot borrow one). Testing environment only.
                var context = SearchTestingSession.Context(http);

                var (response, failure, comparison) = await handler.ExecuteAsync(context, request, forceShadow: true, cancellationToken).ConfigureAwait(false);
                http.Response.Headers.CacheControl = "no-store";
                if (comparison is null)
                {
                    return TypedResults.Problem(title: "SHADOW_NOT_ENABLED", statusCode: 409, detail: "FieldSearch:Engine is neither Shadow nor Indexed");
                }

                return TypedResults.Ok(new FieldSearchShadowTestingResponse(response?.TotalCount, failure is null ? null : failure.ErrorCode + ((failure.UnavailableReason ?? failure.LimitKind) is { } detail ? ":" + detail : string.Empty),
                    comparison.Classification, comparison.SkippedReason, comparison.IndexedCount, comparison.IndexedError, comparison.FirstDifferingPosition, comparison.ElapsedMilliseconds));
            })
            .WithName("TestingFieldSearchShadow");

        // IX-2 O-2 equality test: the set-based reader must return exactly what the per-record reader returns (items, header facts,
        // values, order) for every contentful record of the caller's own Authority catalogue. Counts only; no content is returned.
        testing.MapPost("/accepted-content-reader-parity", static async Task<Ok<AcceptedContentReaderParityResponse>> (SearchScopeVerificationRequest request, HttpContext http, EnterpriseForms.Application.Common.Authority.IAuthorizationAuthorityClient authority,
                EnterpriseForms.Application.Features.FormRecords.Ports.IAcceptedRecordItemsReader reader, CancellationToken cancellationToken) =>
            {
                var context = BusinessAuthentication.RequireContext(http);
                var catalogue = await authority.IssueSearchCatalogueAsync(context.ToDelegatedUser(), new EnterpriseForms.Application.Common.Authority.SearchCatalogueRequest(context.OrganizationId, request.FormDefinitionId, 500), cancellationToken).ConfigureAwait(false);
                var records = catalogue.Entries.Where(e => e.CommittedContentRevision > 0 && e.CommittedOperationId is not null).Select(e => (e.FormRecordId, e.CommittedOperationId!.Value)).ToList();
                var clock = System.Diagnostics.Stopwatch.StartNew();
                var single = new Dictionary<Guid, EnterpriseForms.Application.Features.FormRecords.Ports.AcceptedRecordContent>();
                foreach (var (record, operation) in records)
                {
                    single[record] = await reader.ReadAcceptedContentAsync(record, operation, cancellationToken).ConfigureAwait(false);
                }

                var perRecord = clock.Elapsed.TotalMilliseconds;
                clock.Restart();
                var batched = new Dictionary<Guid, EnterpriseForms.Application.Features.FormRecords.Ports.AcceptedRecordContent>();
                foreach (var chunk in records.Chunk(50))
                {
                    foreach (var (key, value) in await reader.ReadAcceptedContentBatchAsync(chunk, cancellationToken).ConfigureAwait(false))
                    {
                        batched[key] = value;
                    }
                }

                var setBased = clock.Elapsed.TotalMilliseconds;
                int items = 0, values = 0, mismatches = 0;
                foreach (var (record, _) in records)
                {
                    var a = single[record];
                    var b = batched.GetValueOrDefault(record);
                    items += a.Items.Count;
                    values += a.Values.Count;
                    if (b is null || !a.Items.SequenceEqual(b.Items) || !a.Values.SequenceEqual(b.Values) || a.Header != b.Header)
                    {
                        mismatches++;
                    }
                }

                http.Response.Headers.CacheControl = "no-store";
                return TypedResults.Ok(new AcceptedContentReaderParityResponse(records.Count, items, values, mismatches, perRecord, setBased));
            })
            .WithName("TestingAcceptedContentReaderParity");
        return endpoints;
    }
}
