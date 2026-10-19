using EnterpriseForms.Api.Composition;
using EnterpriseForms.Api.Features.FormDefinitions;
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
