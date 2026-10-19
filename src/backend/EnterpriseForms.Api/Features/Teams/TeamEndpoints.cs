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
