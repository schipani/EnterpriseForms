using AuthorizationAuthority.Api.Composition;
using AuthorizationAuthority.Application.Common.Ports;
using AuthorizationAuthority.Application.Features.SearchAccess;
using Microsoft.AspNetCore.Http.HttpResults;

namespace AuthorizationAuthority.Api.Features.SearchIndex;

/// <summary>
/// IX-5a, Testing environment only (C-147): drives and observes the dark Tier 2 access projection for the focused stack tests.
/// Workload-authenticated; never mapped outside Testing; no search path exists. Returns identifiers and counts only.
/// </summary>
public static class SearchAccessTestingEndpoints
{
    public sealed record CatchUpRequest(int MaximumEvents = 1000);

    public sealed record CatchUpResponse(string OutcomeName, int Advanced, SearchAccessStatus Status);

    public sealed record RebuildRequest(string RebuildReasonCode);

    public sealed record MaintenanceRequest(bool Paused);

    public sealed record PrincipalRow(string PrincipalTypeName, Guid PrincipalEntityId, string AccessKindName, Guid? RecordShareId, string? ShareRoleName);

    public sealed record ItemRow(string PrincipalTypeName, Guid PrincipalEntityId, Guid ManifestItemId, Guid RecordShareId, string ShareRoleName);

    public sealed record RecordRowsResponse(Guid FormRecordId, IReadOnlyList<PrincipalRow> RecordGrants, IReadOnlyList<ItemRow> FieldItemGrants);

    public static IEndpointRouteBuilder MapSearchAccessTestingEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup(InternalApi.Prefix + "/testing/search-access").WithTags("SearchAccess").RequireAuthorization(InternalAuthentication.WorkloadPolicy);

        group.MapGet("/{organizationId:guid}/status", static async Task<Ok<SearchAccessStatus>> (Guid organizationId, SearchAccessProjectionService service, CancellationToken cancellationToken) =>
            TypedResults.Ok(await service.StatusAsync(organizationId, cancellationToken).ConfigureAwait(false)));

        group.MapPost("/{organizationId:guid}/catch-up", static async Task<Ok<CatchUpResponse>> (Guid organizationId, CatchUpRequest request, SearchAccessProjectionService service, CancellationToken cancellationToken) =>
        {
            var (outcome, advanced) = await service.CatchUpAsync(organizationId, Math.Clamp(request.MaximumEvents, 1, 100_000), cancellationToken).ConfigureAwait(false);
            return TypedResults.Ok(new CatchUpResponse(outcome.ToString(), advanced, await service.StatusAsync(organizationId, cancellationToken).ConfigureAwait(false)));
        });

        group.MapPost("/{organizationId:guid}/rebuild", static async Task<Ok<SearchAccessStatus>> (Guid organizationId, RebuildRequest request, SearchAccessProjectionService service, CancellationToken cancellationToken) =>
            TypedResults.Ok(await service.RebuildAsync(organizationId, request.RebuildReasonCode, Guid.NewGuid(), cancellationToken).ConfigureAwait(false)));

        group.MapPost("/{organizationId:guid}/verify", static async Task<Ok<SearchAccessVerification>> (Guid organizationId, SearchAccessProjectionService service, CancellationToken cancellationToken) =>
            TypedResults.Ok(await service.VerifyAsync(organizationId, Guid.NewGuid(), cancellationToken).ConfigureAwait(false)));

        group.MapGet("/{organizationId:guid}/journal-replay", static async Task<Ok<SearchAccessReplayCheck>> (Guid organizationId, SearchAccessProjectionService service, CancellationToken cancellationToken) =>
            TypedResults.Ok(await service.VerifyJournalReplayAsync(organizationId, cancellationToken).ConfigureAwait(false)));

        group.MapGet("/{organizationId:guid}/records/{formRecordId:guid}", static async Task<Ok<RecordRowsResponse>> (Guid organizationId, Guid formRecordId, IAuthorityStore store, CancellationToken cancellationToken) =>
        {
            await using var scope = await store.BeginReadOnlySnapshotScopeAsync(cancellationToken).ConfigureAwait(false);
            await scope.SearchAccess.BindOrganizationAsync(organizationId, cancellationToken).ConfigureAwait(false);
            var records = await scope.SearchAccess.GetRecordGrantsAsync(organizationId, [formRecordId], cancellationToken).ConfigureAwait(false);
            var items = await scope.SearchAccess.GetFieldItemGrantsAsync(organizationId, [formRecordId], cancellationToken).ConfigureAwait(false);
            return TypedResults.Ok(new RecordRowsResponse(
                formRecordId,
                records.Select(r => new PrincipalRow(r.AccessPrincipalTypeName, r.AccessPrincipalEntityId, r.AccessKindName, r.RecordShareId, r.ShareRoleName)).ToList(),
                items.Select(i => new ItemRow(i.AccessPrincipalTypeName, i.AccessPrincipalEntityId, i.ManifestItemId, i.RecordShareId, i.ShareRoleName)).ToList()));
        });

        group.MapPost("/maintenance", static Ok<MaintenanceRequest> (MaintenanceRequest request, SearchAccessMaintenanceSwitch maintenanceSwitch) =>
        {
            maintenanceSwitch.Paused = request.Paused;
            return TypedResults.Ok(new MaintenanceRequest(maintenanceSwitch.Paused));
        });

        return endpoints;
    }
}
