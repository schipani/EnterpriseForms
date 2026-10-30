using EnterpriseForms.Api.Composition;
using EnterpriseForms.Api.Features.FormDefinitions;
using EnterpriseForms.Application.Common.Persistence;
using EnterpriseForms.Application.Features.Teams.ManageTeam;
using EnterpriseForms.Contracts.Features;
using Microsoft.AspNetCore.Http.HttpResults;

namespace EnterpriseForms.Api.Features.Teams;

public static class TeamEndpoints
{
    public const string OwnerName = "Teams";

    public static IEndpointRouteBuilder MapTeamEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup(ApiVersions.V1Prefix + "/teams").WithTags(OwnerName).WithFeatureOwner(OwnerName).RequireAuthorization(BusinessAuthentication.Policy);

        group.MapPost("/", static async Task<Created<TeamResponse>> (CreateTeamRequest request, HttpContext http, CreateTeamHandler handler, CancellationToken cancellationToken) =>
            {
                var context = BusinessAuthentication.RequireContext(http);
                var response = await handler.HandleAsync(context, request, FormDefinitionEndpoints.RequireIdempotencyKey(http), cancellationToken).ConfigureAwait(false);
                return TypedResults.Created($"{ApiVersions.V1Prefix}/teams/{response.TeamId:D}", response);
            })
            .WithName("CreateTeam");

        group.MapGet("/", static async Task<Ok<IReadOnlyList<DirectoryTeamResponse>>> (HttpContext http, IBusinessStore store, CancellationToken cancellationToken) =>
            {
                var context = BusinessAuthentication.RequireContext(http);
                await using var scope = await store.BeginScopeAsync(cancellationToken).ConfigureAwait(false);
                var teams = await scope.GetTeamsAsync(context.OrganizationId, cancellationToken).ConfigureAwait(false);
                return TypedResults.Ok<IReadOnlyList<DirectoryTeamResponse>>(teams.Select(t => new DirectoryTeamResponse(t.TeamId, t.TeamName)).ToList());
            })
            .WithName("ListOrganizationTeams")
            .WithSummary("Display-name directory of the organization's teams (projection; confers nothing).");

        // Phase C8 M1 (PG-048/049): administrative directory and detail over the projection; owners and Security Administrators read, owners rename (D8-8).
        var administration = endpoints.MapGroup(ApiVersions.V1Prefix + "/organization/teams").WithTags(OwnerName).WithFeatureOwner(OwnerName).RequireAuthorization(BusinessAuthentication.Policy);
        administration.MapGet("/", static async Task<Ok<TeamDirectoryResponse>> (HttpContext http, TeamDirectoryHandlers handlers, CancellationToken cancellationToken) =>
            {
                http.Response.Headers.CacheControl = "no-store";
                return TypedResults.Ok(await handlers.GetDirectoryAsync(BusinessAuthentication.RequireContext(http), cancellationToken).ConfigureAwait(false));
            })
            .WithName("GetTeamDirectory")
            .WithSummary("Team directory with member counts (PG-048). Organization owners and Security Administrators only; 403 otherwise, without counts.")
            .ProducesProblem(StatusCodes.Status403Forbidden);

        group.MapGet("/{teamId:guid}", static async Task<Ok<TeamDetailResponse>> (Guid teamId, HttpContext http, TeamDirectoryHandlers handlers, CancellationToken cancellationToken) =>
            {
                http.Response.Headers.CacheControl = "no-store";
                return TypedResults.Ok(await handlers.GetDetailAsync(BusinessAuthentication.RequireContext(http), teamId, cancellationToken).ConfigureAwait(false));
            })
            .WithName("GetTeam")
            .WithSummary("Team detail and roster (PG-049) from the projection, each membership carrying the Authority command that made it effective. Owners and Security Administrators only; another organization's team is the same generic 404 as an unknown id.")
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapPatch("/{teamId:guid}", static async Task<Ok<TeamDetailResponse>> (Guid teamId, RenameTeamRequest request, HttpContext http, TeamDirectoryHandlers handlers, CancellationToken cancellationToken) =>
                TypedResults.Ok(await handlers.RenameAsync(BusinessAuthentication.RequireContext(http), teamId, request, cancellationToken).ConfigureAwait(false)))
            .WithName("RenameTeam")
            .WithSummary("Display-only rename of the team projection (D8-8; the Authority names no team). Organization owners and Security Administrators (the Authority's team-command rule); audited (TEAM_RENAMED); 409 TEAM_MODIFIED_CONCURRENTLY when expectedModifiedOn is stale.")
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        // Phase C8 (C8-M1-01, R-3): the operation is recorded before submit; the Worker projects the confirmed Authority state.
        group.MapPost("/{teamId:guid}/members", static async Task<Ok<TeamMembershipChangeResponse>> (Guid teamId, TeamMemberRequest request, HttpContext http, ChangeTeamMembershipHandler handler, CancellationToken cancellationToken) =>
            {
                var context = BusinessAuthentication.RequireContext(http);
                return TypedResults.Ok(await handler.HandleAsync(context, teamId, request.UserId, add: true, FormDefinitionEndpoints.RequireIdempotencyKey(http), cancellationToken).ConfigureAwait(false));
            })
            .WithName("AddTeamMember")
            .WithSummary("Submits AddTeamMember to the Authority (organization owner or Security Administrator; never oneself as a Security Administrator, G25). The durable operation is recorded first; the roster changes only when the Worker projects the Authority-confirmed state (never written by this request). 409 IDEMPOTENCY_KEY_REUSED for the same key with another request; a completed operation is never re-sent.");

        group.MapPost("/{teamId:guid}/members/{userId:guid}/remove", static async Task<Ok<TeamMembershipChangeResponse>> (Guid teamId, Guid userId, HttpContext http, ChangeTeamMembershipHandler handler, CancellationToken cancellationToken) =>
            {
                var context = BusinessAuthentication.RequireContext(http);
                return TypedResults.Ok(await handler.HandleAsync(context, teamId, userId, add: false, FormDefinitionEndpoints.RequireIdempotencyKey(http), cancellationToken).ConfigureAwait(false));
            })
            .WithName("RemoveTeamMember")
            .WithSummary("Submits RemoveTeamMember to the Authority; same rules as AddTeamMember. The member stays on the roster until the Worker projects the Authority-confirmed removal.");

        // Phase C8 (R-4, RC-16, RC-17): durable operation status (browsers poll this, never re-POST) and audited operator actions.
        group.MapGet("/{teamId:guid}/membership-operations/{operationId:guid}", static async Task<Ok<TeamMembershipOperationResponse>> (Guid teamId, Guid operationId, HttpContext http, TeamMembershipOperationHandlers handlers, CancellationToken cancellationToken) =>
            {
                http.Response.Headers.CacheControl = "no-store";
                return TypedResults.Ok(await handlers.GetAsync(BusinessAuthentication.RequireContext(http), teamId, operationId, cancellationToken).ConfigureAwait(false));
            })
            .WithName("GetTeamMembershipOperation")
            .WithSummary("One add/remove operation of the team (organization owners and Security Administrators). Another organization's or team's operation is the same 404 as an unknown id.")
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapGet("/{teamId:guid}/membership-operations/by-key/{clientKey}", static async Task<Ok<TeamMembershipOperationResponse>> (Guid teamId, string clientKey, HttpContext http, TeamMembershipOperationHandlers handlers, CancellationToken cancellationToken) =>
            {
                http.Response.Headers.CacheControl = "no-store";
                return TypedResults.Ok(await handlers.GetByClientKeyAsync(BusinessAuthentication.RequireContext(http), teamId, clientKey, cancellationToken).ConfigureAwait(false));
            })
            .WithName("GetTeamMembershipOperationByKey")
            .WithSummary("The caller's own operation by the Idempotency-Key it sent (after a lost response). Only the initiating administrator finds it; the key is never returned.")
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapPost("/{teamId:guid}/membership-operations/{operationId:guid}/recheck", static async Task<Ok<TeamMembershipOperationResponse>> (Guid teamId, Guid operationId, HttpContext http, TeamMembershipOperationHandlers handlers, CancellationToken cancellationToken) =>
                TypedResults.Ok(await handlers.RecheckAsync(BusinessAuthentication.RequireContext(http), teamId, operationId, cancellationToken).ConfigureAwait(false)))
            .WithName("RecheckTeamMembershipOperation")
            .WithSummary("Asks the Worker to look the operation up again at once (audited TEAM_MEMBERSHIP_OPERATION_RECHECKED). Never submits or replays a command; a ReconciliationError re-opens and still projects only a validated Authority answer. 409 when the operation is complete.")
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        group.MapPost("/{teamId:guid}/membership-operations/{operationId:guid}/review", static async Task<Ok<TeamMembershipOperationResponse>> (Guid teamId, Guid operationId, HttpContext http, TeamMembershipOperationHandlers handlers, CancellationToken cancellationToken) =>
                TypedResults.Ok(await handlers.MarkReviewedAsync(BusinessAuthentication.RequireContext(http), teamId, operationId, cancellationToken).ConfigureAwait(false)))
            .WithName("MarkTeamMembershipOperationReviewed")
            .WithSummary("Records that an administrator reviewed a failed or anomalous operation (audited TEAM_MEMBERSHIP_OPERATION_REVIEWED). Changes no roster and no Authority state.")
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        return endpoints;
    }
}
