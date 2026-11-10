using AuthorizationAuthority.Api.Composition;
using AuthorizationAuthority.Application.Features.SearchIndex;
using AuthorizationAuthority.Contracts.V1;
using Microsoft.AspNetCore.Http.HttpResults;

namespace AuthorizationAuthority.Api.Features.SearchIndex;

/// <summary>
/// IX-3 (design §8, §17.3, R1 §E): the Tier 1 indexed field search. Private contract, business workload credential plus the
/// delegated user assertion (the same plane as the AC-5 catalogue); off unless <c>SearchIndex:Query:QueryEnabled</c> is set in
/// Development/Testing. Responses carry record ids, verified header facts, counts and revisions only.
/// </summary>
public static class IndexedSearchEndpoints
{
    public static IEndpointRouteBuilder MapIndexedSearchEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup(InternalApi.Prefix + "/search/indexed-query").WithTags("SearchIndex").RequireAuthorization(InternalAuthentication.WorkloadPolicy);

        group.MapPost("/", static async Task<Ok<IndexedSearchResponse>> (IndexedSearchRequest request, HttpContext context, IndexedSearchService service, DelegatedUserAssertionValidator validator, CancellationToken cancellationToken) =>
            {
                var actor = InternalAuthentication.RequireActor(context, validator);
                return TypedResults.Ok(await service.SearchAsync(actor, request, InternalAuthentication.Correlation(context), cancellationToken).ConfigureAwait(false));
            })
            .WithName("IndexedFieldSearch")
            .WithSummary("IX-3 Tier 1: one indexed field search over the caller's discoverable records of one form, at one S1-pinned snapshot. The scope guard (I-1), value-independent field-item checks for share-only records, a scope-driven candidate statement, the per-match binding (I-2) and the seal check of the first page run before anything is released; the ordered result is held by the Authority as a session-bound snapshot (record ids only) committed with its execution evidence. 409 SEARCH_SNAPSHOT_CHANGED, 422 SEARCH_VERIFICATION_LIMIT / SEARCH_FIELD_NOT_ALLOWED / SEARCH_OPERATOR_NOT_SUPPORTED, 423 INTEGRITY_QUARANTINE (organization), 503 SEARCH_INDEX_NOT_READY / SEARCH_INDEX_INCONSISTENT / SEARCH_INDEX_VERIFICATION_OVERDUE / RECORD_QUARANTINED. Results reflect Authority-accepted content, not a verification of the business copy.");

        group.MapPost("/{snapshotId:guid}/page", static async Task<Ok<IndexedSearchResponse>> (Guid snapshotId, IndexedSearchPageRequest request, HttpContext context, IndexedSearchService service, DelegatedUserAssertionValidator validator, CancellationToken cancellationToken) =>
            {
                var actor = InternalAuthentication.RequireActor(context, validator);
                return TypedResults.Ok(await service.PageAsync(actor, snapshotId, request, InternalAuthentication.Correlation(context), cancellationToken).ConfigureAwait(false));
            })
            .WithName("IndexedFieldSearchPage")
            .WithSummary("IX-3: another page of the caller's own snapshot, re-authorized completely (§17.3 checks 1-9: snapshot, user, session and query digest; gate and S1; authorization revision; approvals, operators, coverage and query_fields; projection and form state; a fresh SpiceDB decision for every page record; I-2 and the seal; edit flags; S1 verify). Any failure returns no page; non-retryable failures delete the snapshot.");

        return endpoints;
    }

    /// <summary>Testing only (plan-shape test IX-3, §8.2): the candidate statement's plan for a scope; no data is returned.</summary>
    public static IEndpointRouteBuilder MapIndexedSearchTestingEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost(InternalApi.Prefix + "/testing/search/candidate-plan", static async Task<Ok<CandidatePlanResponse>> (CandidatePlanRequest request, IndexedSearchService service, CancellationToken cancellationToken) =>
                TypedResults.Ok(new CandidatePlanResponse(await service.ExplainCandidatePlanAsync(request.OrganizationId, request.ScopeRecordIds, request.Query, cancellationToken, request.Analyze).ConfigureAwait(false))))
            .WithTags("SearchIndex")
            .RequireAuthorization(InternalAuthentication.WorkloadPolicy)
            .WithName("TestingIndexedCandidatePlan");

        // C-141 (RQ-2): the share-only step of a search, by the correlation id the caller sent (counts and a code only).
        endpoints.MapGet(InternalApi.Prefix + "/testing/search/share-only-probe/{correlationId:guid}", static Results<Ok<IReadOnlyList<ShareOnlyProbeObservation>>, NotFound> (Guid correlationId, IndexedSearchTestingProbe probe) =>
                probe.Find(correlationId) is { Count: > 0 } observations ? TypedResults.Ok(observations) : TypedResults.NotFound())
            .WithTags("SearchIndex")
            .RequireAuthorization(InternalAuthentication.WorkloadPolicy)
            .WithName("TestingIndexedShareOnlyProbe");
        return endpoints;
    }

    /// <param name="Analyze">C-135 (PD-9): EXPLAIN (ANALYZE, BUFFERS) instead of the plan only.</param>
    public sealed record CandidatePlanRequest(Guid OrganizationId, IReadOnlyList<Guid> ScopeRecordIds, IndexedSearchQuery Query, bool Analyze = false);

    public sealed record CandidatePlanResponse(string PlanJson);
}
