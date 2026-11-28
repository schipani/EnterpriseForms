using AuthorizationAuthority.Api.Composition;
using AuthorizationAuthority.Application.Features.Sharing;
using AuthorizationAuthority.Contracts.V1;
using Microsoft.AspNetCore.Http.HttpResults;

namespace AuthorizationAuthority.Api.Features.Authorization;

public static class ShareDetailsEndpoints
{
    public static IEndpointRouteBuilder MapShareDetailsEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup(InternalApi.Prefix + "/records").WithTags("Authorization").RequireAuthorization(InternalAuthentication.WorkloadPolicy);

        group.MapGet("/{organizationId:guid}/{formRecordId:guid}/shares/{recordShareId:guid}", static async Task<Ok<ShareDetails>> (Guid organizationId, Guid formRecordId, Guid recordShareId, HttpContext context, ShareDetailsService service, DelegatedUserAssertionValidator validator, CancellationToken cancellationToken) =>
            {
                var actor = InternalAuthentication.RequireActor(context, validator);
                return TypedResults.Ok(await service.GetAsync(actor, organizationId, formRecordId, recordShareId, cancellationToken).ConfigureAwait(false));
            })
            .WithName("GetShareDetails")
            .WithSummary("Authoritative committed state and share revision of one share (owner, or a Manager on its committed lineage); fails closed while the organization gate is closed.");

        group.MapGet("/{organizationId:guid}/{formRecordId:guid}/shares/{recordShareId:guid}/effective-scope", static async Task<Ok<ShareEffectiveScope>> (Guid organizationId, Guid formRecordId, Guid recordShareId, HttpContext context, ShareDetailsService service, DelegatedUserAssertionValidator validator, CancellationToken cancellationToken) =>
            {
                var actor = InternalAuthentication.RequireActor(context, validator);
                return TypedResults.Ok(await service.GetEffectiveScopeAsync(actor, organizationId, formRecordId, recordShareId, cancellationToken).ConfigureAwait(false));
            })
            .WithName("GetShareEffectiveScope")
            .WithSummary("What a share's recipient can do through it now and why (same readers as GetShareDetails; fails closed while the gate is closed).");

        endpoints.MapGroup(InternalApi.Prefix + "/me").WithTags("Authorization").RequireAuthorization(InternalAuthentication.WorkloadPolicy)
            .MapGet("/{organizationId:guid}/access", static async Task<Ok<MyAccess>> (Guid organizationId, HttpContext context, MyAccessService service, DelegatedUserAssertionValidator validator, CancellationToken cancellationToken) =>
            {
                var actor = InternalAuthentication.RequireActor(context, validator);
                return TypedResults.Ok(await service.GetAsync(actor, organizationId, cancellationToken).ConfigureAwait(false));
            })
            .WithName("GetMyAccess")
            .WithSummary("The caller's own owned, team-owned, direct-share and team-share access from committed Authority state; fails closed while the gate is closed.");

        endpoints.MapGroup(InternalApi.Prefix + "/members").WithTags("Authorization").RequireAuthorization(InternalAuthentication.WorkloadPolicy)
            .MapGet("/{organizationId:guid}/{memberUserId:guid}/owned-records", static async Task<Ok<MemberOwnedRecords>> (Guid organizationId, Guid memberUserId, HttpContext context, OwnedRecordsService service, DelegatedUserAssertionValidator validator, CancellationToken cancellationToken) =>
            {
                var actor = InternalAuthentication.RequireActor(context, validator);
                return TypedResults.Ok(await service.GetAsync(actor, organizationId, memberUserId, cancellationToken).ConfigureAwait(false));
            })
            .WithName("GetMemberOwnedRecords")
            .WithSummary("Active records a member directly owns (ids only) for administrator reassignment; Security Administrators only.");

        group.MapGet("/{organizationId:guid}/{formRecordId:guid}/security-history", static async Task<Ok<RecordSecurityHistory>> (Guid organizationId, Guid formRecordId, string? view, HttpContext context, RecordSecurityHistoryService service, DelegatedUserAssertionValidator validator, CancellationToken cancellationToken) =>
            {
                var actor = InternalAuthentication.RequireActor(context, validator);
                return TypedResults.Ok(await service.GetAsync(actor, organizationId, formRecordId, string.Equals(view, "ownership", StringComparison.Ordinal), cancellationToken).ConfigureAwait(false));
            })
            .WithName("GetRecordSecurityHistory")
            .WithSummary("Record-scoped history from committed Authority edges, each command with its own state: sharing (owner or ManageAccess; a non-owner sees no owner changes) or, with view=ownership, ownership changes (owner or Security Auditor); others 404; refused while the organization is quarantined.");

        return endpoints;
    }
}
