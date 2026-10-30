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

        group.MapPost("/{teamId:guid}/members", static async Task<Ok<CommandStatusResponse>> (Guid teamId, TeamMemberRequest request, HttpContext http, ChangeTeamMembershipHandler handler, CancellationToken cancellationToken) =>
            {
                var context = BusinessAuthentication.RequireContext(http);
                return TypedResults.Ok(await handler.HandleAsync(context, teamId, request.UserId, add: true, FormDefinitionEndpoints.RequireIdempotencyKey(http), cancellationToken).ConfigureAwait(false));
            })
            .WithName("AddTeamMember");

        group.MapPost("/{teamId:guid}/members/{userId:guid}/remove", static async Task<Ok<CommandStatusResponse>> (Guid teamId, Guid userId, HttpContext http, ChangeTeamMembershipHandler handler, CancellationToken cancellationToken) =>
            {
                var context = BusinessAuthentication.RequireContext(http);
                return TypedResults.Ok(await handler.HandleAsync(context, teamId, userId, add: false, FormDefinitionEndpoints.RequireIdempotencyKey(http), cancellationToken).ConfigureAwait(false));
            })
            .WithName("RemoveTeamMember");

        return endpoints;
    }
}
