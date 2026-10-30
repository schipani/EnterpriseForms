using System.Security.Claims;
using AuthorizationAuthority.Api.Composition;
using AuthorizationAuthority.Application.Features.Catalogue;
using AuthorizationAuthority.Contracts.V1;
using Microsoft.AspNetCore.Http.HttpResults;

namespace AuthorizationAuthority.Api.Features.Catalogue;

/// <summary>
/// Phase C7 M1 — AC-5 record catalogue. Two separately authorized route groups: the user-scoped search catalogue (business
/// workload credential + delegated user assertion) and the privileged migration catalogue (verifier workload credential
/// only, OD-1). Neither returns record contents.
/// </summary>
public static class CatalogueEndpoints
{
    public static IEndpointRouteBuilder MapCatalogueEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var user = endpoints.MapGroup(InternalApi.Prefix + "/search-catalogue").WithTags("Catalogue").RequireAuthorization(InternalAuthentication.WorkloadPolicy);

        user.MapPost("/", static async Task<Ok<SearchCatalogueResponse>> (SearchCatalogueRequest request, HttpContext context, SearchCatalogueService service, DelegatedUserAssertionValidator validator, CancellationToken cancellationToken) =>
            {
                var actor = InternalAuthentication.RequireActor(context, validator);
                return TypedResults.Ok(await service.IssueUserCatalogueAsync(actor, request, InternalAuthentication.Correlation(context), cancellationToken).ConfigureAwait(false));
            })
            .WithName("IssueSearchCatalogue")
            .WithSummary("AC-5: the registered records of one form (registry parent) that the delegated user may discover, each with its accepted head, fixed as an Authority-held snapshot at one authorization, projection and content revision. More eligible records than maximum_entries ⇒ 422 LIMIT_EXCEEDED with no entries. Quarantined heads and resources are listed with their flag (a trusted search over them must fail).");

        user.MapPost("/{snapshotId:guid}/validate", static async Task<Ok<CatalogueSnapshotValidation>> (Guid snapshotId, HttpContext context, SearchCatalogueService service, DelegatedUserAssertionValidator validator, CancellationToken cancellationToken) =>
            {
                var actor = InternalAuthentication.RequireActor(context, validator);
                return TypedResults.Ok(await service.ValidateUserSnapshotAsync(actor, snapshotId, InternalAuthentication.Correlation(context), cancellationToken).ConfigureAwait(false));
            })
            .WithName("ValidateSearchCatalogue")
            .WithSummary("Validates the caller's own snapshot against current Authority state: expiry, gate, authorization and projection revision (any change invalidates, OD-5), and — when the organization's content revision moved — every entry's head, quarantine and registry state. The id alone proves nothing.");

        user.MapPost("/{snapshotId:guid}/executions", static async Task<Ok<SearchExecutionRecorded>> (Guid snapshotId, SearchExecutionEvidence evidence, HttpContext context, SearchCatalogueService service, DelegatedUserAssertionValidator validator, CancellationToken cancellationToken) =>
            {
                var actor = InternalAuthentication.RequireActor(context, validator);
                return TypedResults.Ok(await service.RecordExecutionAsync(actor, snapshotId, evidence, InternalAuthentication.Correlation(context), cancellationToken).ConfigureAwait(false));
            })
            .WithName("RecordSearchExecution")
            .WithSummary("Phase C7 M3 (OD-8): journals one field-search execution over the caller's own snapshot on the audit stream (field ids, operators, counts, outcome, duration — never predicate values or record ids).");

        var verifier = endpoints.MapGroup(InternalApi.Prefix + "/verifier/migration-catalogue").WithTags("Catalogue").RequireAuthorization(InternalAuthentication.VerifierPolicy);

        verifier.MapPost("/", static async Task<Ok<MigrationCataloguePage>> (MigrationCatalogueRequest request, HttpContext context, SearchCatalogueService service, CancellationToken cancellationToken) =>
                TypedResults.Ok(await service.IssueMigrationCatalogueAsync(WorkloadName(context), request, InternalAuthentication.Correlation(context), cancellationToken).ConfigureAwait(false)))
            .WithName("IssueMigrationCatalogue")
            .WithSummary("OD-1 (verifier workload only): the complete registered population of the form scoped by an effective PublishFormSecurityRevision command — records no user can discover included — with heads, quarantine and registry state, as a paged Authority-held snapshot. Issuance and denials are journaled on the audit stream. Never routed through the business API.");

        verifier.MapGet("/{snapshotId:guid}/entries", static async Task<Ok<MigrationCataloguePage>> (Guid snapshotId, int? offset, HttpContext context, SearchCatalogueService service, CancellationToken cancellationToken) =>
                TypedResults.Ok(await service.ReadMigrationCatalogueAsync(WorkloadName(context), snapshotId, offset ?? 0, InternalAuthentication.Correlation(context), cancellationToken).ConfigureAwait(false)))
            .WithName("ReadMigrationCatalogue")
            .WithSummary("A further page of the verifier's own migration catalogue snapshot (409 after expiry).");

        verifier.MapPost("/{snapshotId:guid}/validate", static async Task<Ok<CatalogueSnapshotValidation>> (Guid snapshotId, HttpContext context, SearchCatalogueService service, CancellationToken cancellationToken) =>
                TypedResults.Ok(await service.ValidateMigrationSnapshotAsync(WorkloadName(context), snapshotId, InternalAuthentication.Correlation(context), cancellationToken).ConfigureAwait(false)))
            .WithName("ValidateMigrationCatalogue")
            .WithSummary("Validates the verifier's snapshot like the user snapshot, plus that the scoping command is still the field's current protection decision.");

        verifier.MapPost("/{snapshotId:guid}/census", static async Task<Ok<ProtectionCensusResponse>> (Guid snapshotId, ProtectionCensusRequest request, HttpContext context, ProtectionReconciliationService service, CancellationToken cancellationToken) =>
                TypedResults.Ok(await service.CensusAsync(WorkloadName(context), snapshotId, request, InternalAuthentication.Correlation(context), cancellationToken).ConfigureAwait(false)))
            .WithName("TakeProtectionCensus")
            .WithSummary("Phase C7 M4 (G19, verifier only): verifies 1..50 entries of the verifier's own snapshot against their accepted manifests (complete leaf set, every HMAC, root, archive receipt; tampering quarantines the head) and counts the decision's field rows in the old and the target form from the verified bytes. Counts only — never contents. A census held for the same accepted operation is reused only when it was verified since reuseSinceSnapshotId (this reconciliation's first snapshot) was issued; anything else is re-verified.");

        verifier.MapPost("/{snapshotId:guid}/attestation", static async Task<Ok<ProtectionAttestation>> (Guid snapshotId, HttpContext context, ProtectionReconciliationService service, CancellationToken cancellationToken) =>
                TypedResults.Ok(await service.AttestAsync(WorkloadName(context), snapshotId, InternalAuthentication.Correlation(context), cancellationToken).ConfigureAwait(false)))
            .WithName("AttestProtectionReconciliation")
            .WithSummary("Phase C7 M4 (G19, verifier only): the Authority's reconciliation report for the snapshot's protection decision, computed now under the organization lock from the registry population, current heads and census results (nothing from the business database), journaled on the audit stream before it is returned. 409 when the decision was superseded.");

        endpoints.MapPost(InternalApi.Prefix + "/verifier/protection-coverage", static async Task<Ok<ProtectionCoverageResponse>> (ProtectionCoverageRequest request, HttpContext context, ProtectionReconciliationService service, CancellationToken cancellationToken) =>
                TypedResults.Ok(await service.CoverageAsync(request, InternalAuthentication.Correlation(context), cancellationToken).ConfigureAwait(false)))
            .WithTags("Catalogue")
            .RequireAuthorization(InternalAuthentication.VerifierPolicy)
            .WithName("GetProtectionCoverage")
            .WithSummary("Phase C7 M4 (verifier only): every current field protection decision of the organization (Authority protection resources, never the business form list) with its latest attestation; organization-complete only when each decision has a complete attestation under that decision.");

        return endpoints;
    }

    private static string WorkloadName(HttpContext context) => context.User.FindFirstValue("workload_name") is { Length: > 0 and <= 100 } name ? name : InternalAuthentication.VerifierRole;
}
