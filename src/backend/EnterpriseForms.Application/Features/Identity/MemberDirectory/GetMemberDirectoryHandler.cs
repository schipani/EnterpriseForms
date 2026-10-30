using EnterpriseForms.Application.Common;
using EnterpriseForms.Application.Common.Authority;
using EnterpriseForms.Application.Common.Persistence;
using EnterpriseForms.Contracts.Features;

namespace EnterpriseForms.Application.Features.Identity.MemberDirectory;

/// <summary>
/// People directory (PG-050, Phase C8 M1, read-only). Rows are the business <c>users</c> projection: identities the
/// Authority has already bound and that have signed in at least once (labelled as such in the response). Role badges come
/// only from the Authority's Security Administrator roster (C-70); owner and designer badges wait for the AC-7 roster read
/// (M2). Team affiliations are the membership projection. Nothing here confers or reveals record access. Visible to
/// organization owners and Security Administrators only.
/// </summary>
public sealed class GetMemberDirectoryHandler(IBusinessStore store, IAuthorizationAuthorityClient authority, ISecurityAdministratorAuthority roster)
{
    public const string Source = "business projection: identities bound by the Authorization Authority that have signed in to this organization; membership state and owner/designer roles are not shown until the Authority roster read (M2)";

    public const string SecurityAdministratorRole = "SecurityAdministrator";

    public async Task<OrganizationMemberDirectoryResponse> GetAsync(RequestContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        await AdministrationAccess.RequireAdministratorAsync(authority, context, cancellationToken).ConfigureAwait(false);

        HashSet<Guid> administrators = [];
        var rolesAvailable = true;
        string? rolesUnavailableReason = null;
        try
        {
            var result = await roster.GetSecurityAdministratorsAsync(context.ToDelegatedUser(), new SecurityAdministratorRosterQuery(context.OrganizationId), cancellationToken).ConfigureAwait(false);
            administrators = result.Administrators.Select(a => a.AuthorityUserId).ToHashSet();
        }
        catch (AuthorityClientException)
        {
            rolesAvailable = false;
            rolesUnavailableReason = "the Authorization Authority roster is unavailable; no role is shown";
        }

        await using var scope = await store.BeginScopeAsync(cancellationToken).ConfigureAwait(false);
        var users = await scope.GetUsersAsync(context.OrganizationId, cancellationToken).ConfigureAwait(false);
        var teams = (await scope.GetTeamsAsync(context.OrganizationId, cancellationToken).ConfigureAwait(false)).Where(t => t.IsActive).ToDictionary(t => t.TeamId, t => t.TeamName);
        var memberships = await scope.GetActiveTeamMembershipsAsync(context.OrganizationId, null, cancellationToken).ConfigureAwait(false);
        var teamsOfUser = memberships.Where(m => teams.ContainsKey(m.TeamId)).GroupBy(m => m.UserId)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<DirectoryTeamResponse>)g.Select(m => new DirectoryTeamResponse(m.TeamId, teams[m.TeamId])).OrderBy(t => t.TeamName, StringComparer.OrdinalIgnoreCase).ToList());

        var members = users
            .Select(u => new OrganizationMemberResponse(u.UserId, u.DisplayName, u.ExternalIssuerUri, u.ExternalSubjectIdentifier, u.CreatedOn,
                rolesAvailable && administrators.Contains(u.UserId) ? [SecurityAdministratorRole] : [],
                teamsOfUser.GetValueOrDefault(u.UserId) ?? [], u.UserId == context.LocalUserId))
            .OrderBy(m => m.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return new OrganizationMemberDirectoryResponse(Source, members, rolesAvailable, rolesUnavailableReason);
    }
}
