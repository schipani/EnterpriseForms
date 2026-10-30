using System.Text.Json;
using EnterpriseForms.Application.Common;
using EnterpriseForms.Application.Common.Authority;
using EnterpriseForms.Application.Common.Persistence;
using EnterpriseForms.Application.Features.FormDefinitions.CreateFormDefinition;
using EnterpriseForms.Contracts.Features;
using EnterpriseForms.Domain.Features.Teams;

namespace EnterpriseForms.Application.Features.Teams.ManageTeam;

/// <summary>Team registration and membership are authority commands (organization owner/system administrator); local rows are a directory projection.</summary>
public sealed class CreateTeamHandler(IBusinessStore store, IAuthorizationAuthorityClient authority, AuthorityCommandAwaiter awaiter, TimeProvider clock)
{
    public async Task<TeamResponse> HandleAsync(RequestContext context, CreateTeamRequest request, string idempotencyKey, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.TeamName) || request.TeamName.Length > 200)
        {
            throw new BusinessException("INVALID_REQUEST", 422, "team_name is required (max 200)");
        }

        var user = context.ToDelegatedUser();
        var decision = await authority.CheckPermissionAsync(user, new PermissionCheckRequest(context.OrganizationId, new ResourceReference(ProtectedResourceKind.Organization, context.OrganizationId), ProtectedAction.ManageOrganization), cancellationToken).ConfigureAwait(false);
        var teamId = CreateFormDefinitionHandler.DeterministicId(idempotencyKey, "team");
        var payload = JsonSerializer.SerializeToElement(new { team_id = teamId });
        var command = await awaiter.SubmitAndAwaitAsync(user, new SecurityCommandRequest(context.OrganizationId, SecurityCommandKind.RegisterTeam, new ResourceReference(ProtectedResourceKind.Team, teamId), decision.AuthorizationRevision, payload, "register team " + request.TeamName), "team:" + idempotencyKey, cancellationToken).ConfigureAwait(false);
        var outcome = new CommandOutcome(command.CommandId, command.CommandStatus.ToString(), command.AuthorizationRevision, command.CommandStatus == SecurityCommandStatus.Effective);
        if (!outcome.Effective)
        {
            throw new PendingCommandException(outcome);
        }

        await using var scope = await store.BeginScopeAsync(cancellationToken).ConfigureAwait(false);
        if (await scope.FindTeamProjectionAsync(context.OrganizationId, teamId, cancellationToken).ConfigureAwait(false) is null)
        {
            var now = clock.GetUtcNow();
            scope.AddTeamProjection(new Team { TeamId = teamId, TenantId = context.OrganizationId, TeamName = request.TeamName, IsActive = true, CreatedOn = now, CreatedByUserId = context.LocalUserId, ModifiedOn = now });
            await scope.CommitAsync(cancellationToken).ConfigureAwait(false);
        }

        return new TeamResponse(teamId, request.TeamName, outcome);
    }
}
