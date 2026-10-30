using EnterpriseForms.Application.Common;
using EnterpriseForms.Application.Common.Authority;
using EnterpriseForms.Application.Common.Persistence;
using EnterpriseForms.Application.Features.Audit;
using EnterpriseForms.Contracts.Features;
using EnterpriseForms.Domain.Features.Audit;

namespace EnterpriseForms.Application.Features.Teams.ManageTeam;

/// <summary>
/// Team directory and detail (PG-048/049, Phase C8 M1) over the business projection, with the display-only rename of
/// D8-8. Membership that confers access lives only in the Authorization Authority; the projection rows shown here are
/// labelled as such and carry the Authority command that made each membership effective. Reads, management hints and
/// the rename follow the Authority's existing team rule (CommandPlanner: organization owner or Security Administrator;
/// a Security Administrator may never add themself, G25). Counts and names are returned only to those callers; another
/// organization's team id is the same generic 404 as an unknown id.
/// </summary>
public sealed class TeamDirectoryHandlers(IBusinessStore store, IAuthorizationAuthorityClient authority, TimeProvider clock)
{
    public const string Source = "business projection of Authority team commands (RegisterTeam, AddTeamMember, RemoveTeamMember); confers nothing";

    public async Task<TeamDirectoryResponse> GetDirectoryAsync(RequestContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var decision = await AdministrationAccess.RequireAdministratorAsync(authority, context, cancellationToken).ConfigureAwait(false);
        await using var scope = await store.BeginScopeAsync(cancellationToken).ConfigureAwait(false);
        var teams = await scope.GetTeamsAsync(context.OrganizationId, cancellationToken).ConfigureAwait(false);
        var counts = (await scope.GetActiveTeamMembershipsAsync(context.OrganizationId, null, cancellationToken).ConfigureAwait(false)).GroupBy(m => m.TeamId).ToDictionary(g => g.Key, g => g.Count());
        return new TeamDirectoryResponse(Source, CanManageTeams(decision),
            teams.Select(t => new TeamSummaryResponse(t.TeamId, t.TeamName, t.IsActive, counts.GetValueOrDefault(t.TeamId), t.CreatedOn, t.ModifiedOn)).OrderBy(t => t.TeamName, StringComparer.OrdinalIgnoreCase).ToList());
    }

    public async Task<TeamDetailResponse> GetDetailAsync(RequestContext context, Guid teamId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var decision = await AdministrationAccess.RequireAdministratorAsync(authority, context, cancellationToken).ConfigureAwait(false);
        await using var scope = await store.BeginScopeAsync(cancellationToken).ConfigureAwait(false);
        var team = await scope.FindTeamProjectionAsync(context.OrganizationId, teamId, cancellationToken).ConfigureAwait(false) ?? throw NotFound();
        var users = (await scope.GetUsersAsync(context.OrganizationId, cancellationToken).ConfigureAwait(false)).ToDictionary(u => u.UserId, u => u.DisplayName);
        var memberships = await scope.GetActiveTeamMembershipsAsync(context.OrganizationId, teamId, cancellationToken).ConfigureAwait(false);
        var members = memberships.Select(m => new TeamMemberResponse(m.UserId, users.GetValueOrDefault(m.UserId) ?? m.UserId.ToString("D"), m.CreatedOn, m.AuthorityCommandId)).OrderBy(m => m.DisplayName, StringComparer.OrdinalIgnoreCase).ToList();
        var memberIds = memberships.Select(m => m.UserId).ToHashSet();
        // G25 mirrored as a hint: a Security Administrator who is not an owner is never offered themself (the Authority refuses it anyway).
        var available = CanManageTeams(decision)
            ? users.Where(u => !memberIds.Contains(u.Key) && (decision.CanManageConfiguration || u.Key != context.LocalUserId)).Select(u => new DirectoryUserResponse(u.Key, u.Value)).OrderBy(u => u.DisplayName, StringComparer.OrdinalIgnoreCase).ToList()
            : [];
        return new TeamDetailResponse(team.TeamId, team.TeamName, team.IsActive, team.CreatedOn, team.ModifiedOn, Source, CanManageTeams(decision), members, available);
    }

    /// <summary>D8-8: rename is a display-only change of the projection label (no Authority state names a team); same principals as the Authority's team commands (owner or Security Administrator), audited, concurrency on the detail's modifiedOn.</summary>
    public async Task<TeamDetailResponse> RenameAsync(RequestContext context, Guid teamId, RenameTeamRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(request);
        var name = request.TeamName?.Trim() ?? string.Empty;
        if (name.Length is 0 or > 200)
        {
            throw new BusinessException("INVALID_REQUEST", 422, "team_name is required (max 200)");
        }

        await AdministrationAccess.RequireAdministratorAsync(authority, context, cancellationToken).ConfigureAwait(false);
        await using (var scope = await store.BeginScopeAsync(cancellationToken).ConfigureAwait(false))
        {
            var team = await scope.FindTeamProjectionAsync(context.OrganizationId, teamId, cancellationToken).ConfigureAwait(false) ?? throw NotFound();
            if (Math.Abs((team.ModifiedOn - request.ExpectedModifiedOn).TotalMilliseconds) >= 1)
            {
                throw new BusinessException("TEAM_MODIFIED_CONCURRENTLY", 409, "the team was changed by someone else; reload");
            }

            if (!string.Equals(team.TeamName, name, StringComparison.Ordinal))
            {
                var previous = team.TeamName;
                team.TeamName = name;
                team.ModifiedOn = clock.GetUtcNow();
                AuditRecorder.Record(scope, context, clock, AuditCategories.Security, AuditActions.TeamRenamed, "team renamed (display label only; Authority membership unchanged)", targetEntityType: "team", targetEntityId: teamId, metadata: new { previousTeamName = previous, teamName = name });
                await scope.CommitAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        return await GetDetailAsync(context, teamId, cancellationToken).ConfigureAwait(false);
    }

    private static bool CanManageTeams(AdministrationAccess.AdministrationDecision decision) => decision.CanManageConfiguration || decision.CanAdministerSecurity;

    private static BusinessException NotFound() => new("NOT_FOUND", 404, "team not found");
}
