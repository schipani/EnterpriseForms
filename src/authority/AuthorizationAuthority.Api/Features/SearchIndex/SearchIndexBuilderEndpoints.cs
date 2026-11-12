using System.Security.Claims;
using AuthorizationAuthority.Api.Composition;
using AuthorizationAuthority.Application.Features.SearchIndex;
using AuthorizationAuthority.Contracts.V1;
using Microsoft.AspNetCore.Http.HttpResults;

namespace AuthorizationAuthority.Api.Features.SearchIndex;

/// <summary>
/// IX-2: the search-index builder plane (its own workload credential only, IX-D6, OD-1 pattern) and the SearchProjectionRebuild
/// command (business workload credential plus a delegated Security Administrator). The builder submits bytes; every decision
/// about what the index holds is the Authority's. Never routed to the browser-facing API except the rebuild command.
/// </summary>
public static class SearchIndexBuilderEndpoints
{
    public static IEndpointRouteBuilder MapSearchIndexBuilderEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var builder = endpoints.MapGroup(InternalApi.Prefix + "/search-index-builder").WithTags("SearchIndex").RequireAuthorization(InternalAuthentication.SearchIndexBuilderPolicy);

        builder.MapPost("/work", static async Task<Ok<SearchIndexBuilderWorkResponse>> (HttpContext context, SearchIndexBuilderService service, CancellationToken cancellationToken) =>
                TypedResults.Ok(await service.WorkAsync(InternalAuthentication.Correlation(context), cancellationToken).ConfigureAwait(false)))
            .WithName("GetSearchIndexBuilderWork")
            .WithSummary("IX-2 (builder workload only): forms with a Building coverage whose search index is not quarantined, and the organizations that have a search index (deep-sweep scheduling). Identifiers and counts only.");

        builder.MapPost("/population", static async Task<Ok<SearchIndexPopulationPage>> (SearchIndexPopulationRequest request, HttpContext context, SearchIndexBuilderService service, CancellationToken cancellationToken) =>
                TypedResults.Ok(await service.PopulationAsync(WorkloadName(context), request, InternalAuthentication.Correlation(context), cancellationToken).ConfigureAwait(false)))
            .WithName("ReadSearchIndexPopulation")
            .WithSummary("IX-2 (builder workload only): a page (record id order, at most 500) of the form's registered population with accepted heads, quarantine flags and whether the record still needs an entry under the current coverages. The first page of a round is journaled on the audit stream (counts only). 409 SEARCH_INDEX_QUARANTINED while quarantined.");

        builder.MapPost("/backfill", static async Task<Ok<SearchIndexBackfillResponse>> (SearchIndexBackfillRequest request, HttpContext context, SearchIndexBuilderService service, CancellationToken cancellationToken) =>
                TypedResults.Ok(await service.BackfillAsync(WorkloadName(context), request, InternalAuthentication.Correlation(context), cancellationToken).ConfigureAwait(false)))
            .WithName("BackfillSearchIndex")
            .WithSummary("IX-2 (builder workload only): 1..50 records' observed business bytes. Each is verified like a G19 census against the head the builder expects (complete leaf set, every HMAC, root, archive receipt; tampering quarantines the record), then postings are derived from the verified bytes and sealed, under the organization lock and conditional on the head (HEAD_CHANGED otherwise). Idempotent per record, head and coverage set. Provider outages fail the whole call (503, nothing written).");

        builder.MapPost("/attestation", static async Task<Ok<SearchIndexAttestation>> (SearchIndexAttestationRequest request, HttpContext context, SearchIndexBuilderService service, CancellationToken cancellationToken) =>
                TypedResults.Ok(await service.AttestAsync(WorkloadName(context), request, InternalAuthentication.Correlation(context), cancellationToken).ConfigureAwait(false)))
            .WithName("AttestSearchIndexCoverage")
            .WithSummary("IX-2 (builder workload only): the Authority attests the form's Building coverages from its own registry, heads and entries under the organization lock, journaled before return. Complete only when every contentful registered record has an entry at its head under every coverage and none is quarantined.");

        builder.MapPost("/deep-sweep/work", static async Task<Ok<SearchIndexDeepSweepWorkResponse>> (SearchIndexDeepSweepWorkRequest request, HttpContext context, SearchIndexBuilderService service, CancellationToken cancellationToken) =>
                TypedResults.Ok(await service.DeepSweepWorkAsync(request, InternalAuthentication.Correlation(context), cancellationToken).ConfigureAwait(false)))
            .WithName("GetSearchIndexDeepSweepWork")
            .WithSummary("IX-2 S-C (builder workload only): entries chosen by the Authority for deep comparison — oldest deep verification first plus the Authority's own random sample.");

        builder.MapPost("/deep-sweep/verify", static async Task<Ok<SearchIndexDeepVerifyResponse>> (SearchIndexDeepVerifyRequest request, HttpContext context, SearchIndexBuilderService service, CancellationToken cancellationToken) =>
                TypedResults.Ok(await service.DeepVerifyAsync(WorkloadName(context), request, InternalAuthentication.Correlation(context), cancellationToken).ConfigureAwait(false)))
            .WithName("DeepVerifySearchIndex")
            .WithSummary("IX-2 S-C (builder workload only): 1..50 entries; the submitted bytes are verified against the head, postings re-derived and compared with the stored rows. Pass writes the entry's S-C timestamp; a posting mismatch quarantines the search index; unverifiable business bytes quarantine the record and the unit is Inconclusive.");

        var workload = endpoints.MapGroup(InternalApi.Prefix + "/search-index").WithTags("SearchIndex").RequireAuthorization(InternalAuthentication.WorkloadPolicy);
        workload.MapPost("/rebuild", static async Task<Ok<SearchProjectionRebuildResponse>> (SearchProjectionRebuildRequest request, HttpContext context, SearchProjectionRebuildService service, DelegatedUserAssertionValidator validator, CancellationToken cancellationToken) =>
            {
                var actor = InternalAuthentication.RequireActor(context, validator);
                return TypedResults.Ok(await service.RebuildAsync(actor, request, InternalAuthentication.Correlation(context), cancellationToken).ConfigureAwait(false));
            })
            .WithName("RebuildSearchProjection")
            .WithSummary("IX-2 SearchProjectionRebuild (delegated Security Administrator, administer_security on the S1 path): purges the organization's search index, returns every coverage to Building and sets Rebuilding, journaled on the audit stream. The only way a search-index quarantine is cleared; refused (423) while the organization is quarantined. Step-up (PB-08) not yet available.");

        return endpoints;
    }

    private static string WorkloadName(HttpContext context) => context.User.FindFirstValue("workload_name") is { Length: > 0 and <= 100 } name ? name : InternalAuthentication.SearchIndexBuilderRole;
}
