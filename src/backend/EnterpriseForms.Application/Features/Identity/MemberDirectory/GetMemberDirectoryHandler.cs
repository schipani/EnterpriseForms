using EnterpriseForms.Application.Common;
using EnterpriseForms.Application.Common.Authority;
using EnterpriseForms.Application.Common.Paging;
using EnterpriseForms.Application.Common.Persistence;
using EnterpriseForms.Contracts.Features;

namespace EnterpriseForms.Application.Features.Identity.MemberDirectory;

public sealed record MemberDirectoryQuery(string? Search, string? Role, string? SignInStatus, int? PageSize, string? ContinuationToken);

/// <summary>
/// People directory (PG-050; C8 M2 within the existing Authority). Rows merge the business <c>users</c> directory (names,
/// first seen, last sign-in) with the Authority's Security Administrator roster: its administrators for every caller, and
/// — only when the caller is a Security Administrator, to whom the Authority returns them as appointment candidates —
/// every other enabled identity binding, so people who have never signed in are listed too. Owner and designer roles,
/// disabled membership and adding members need the AC-7 commands and roster read, which are not approved; nothing here
/// infers them. Paged on the server (default 15) with an opaque cursor bound to the caller and filters. Visible to
/// organization owners and Security Administrators only. Nothing here confers or reveals record access.
/// </summary>
public sealed class GetMemberDirectoryHandler(IBusinessStore store, IAuthorizationAuthorityClient authority, ISecurityAdministratorAuthority roster, IOpaqueCursorProtector cursors)
{
    public const string Source = "business directory of people seen in this organization, plus the Authorization Authority's Security Administrator roster (Security Administrators also receive every enabled Authority identity binding)";

    public const string SecurityAdministratorRole = "SecurityAdministrator";

    public const string NoRole = "None";

    public const string AppointSecurityAdministrator = "AppointSecurityAdministrator";

    public const string RemoveSecurityAdministrator = "RemoveSecurityAdministrator";

    public static readonly IReadOnlyList<string> RoleFilters = [SecurityAdministratorRole, NoRole];

    public static readonly IReadOnlyList<string> SignInStatuses = ["SignedIn", "NotRecorded", "NeverSignedIn"];

    public const string CompleteCoverage = "Lists every identity the Authorization Authority has bound to this organization and enabled, including people who have never signed in. Disabled memberships and organization owner or designer roles cannot be shown until the Authority roster read (AC-7) is approved.";

    public const string PartialCoverage = "Lists people who have used this organization and its Security Administrators. People who have never signed in are listed for Security Administrators only, and organization owner or designer roles cannot be shown, until the Authority roster read (AC-7) is approved.";

    public async Task<OrganizationMemberDirectoryResponse> GetAsync(RequestContext context, MemberDirectoryQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(query);
        var text = Normalize(query.Search);
        var role = Allowed(query.Role, RoleFilters, "role filter");
        var signIn = Allowed(query.SignInStatus, SignInStatuses, "sign-in status filter");
        var size = OffsetPageCursor.ClampPageSize(query.PageSize);
        var digest = OffsetPageCursor.Digest("organization-members", text, role, signIn, size);
        await AdministrationAccess.RequireAdministratorAsync(authority, context, cancellationToken).ConfigureAwait(false);
        var offset = OffsetPageCursor.DecodeOffset(cursors, query.ContinuationToken, context, digest);

        AuthoritySecurityAdministratorRoster? authorityRoster = null;
        string? rolesUnavailableReason = null;
        try
        {
            authorityRoster = await roster.GetSecurityAdministratorsAsync(context.ToDelegatedUser(), new SecurityAdministratorRosterQuery(context.OrganizationId), cancellationToken).ConfigureAwait(false);
        }
        catch (AuthorityClientException)
        {
            rolesUnavailableReason = "the Authorization Authority roster is unavailable; no role is shown and people who have never signed in are not listed";
        }

        await using var scope = await store.BeginScopeAsync(cancellationToken).ConfigureAwait(false);
        var users = await scope.GetUsersAsync(context.OrganizationId, cancellationToken).ConfigureAwait(false);
        var teams = (await scope.GetTeamsAsync(context.OrganizationId, cancellationToken).ConfigureAwait(false)).Where(t => t.IsActive).ToDictionary(t => t.TeamId, t => t.TeamName);
        var memberships = await scope.GetActiveTeamMembershipsAsync(context.OrganizationId, null, cancellationToken).ConfigureAwait(false);
        var teamsOfUser = memberships.Where(m => teams.ContainsKey(m.TeamId)).GroupBy(m => m.UserId)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<DirectoryTeamResponse>)g.Select(m => new DirectoryTeamResponse(m.TeamId, teams[m.TeamId])).OrderBy(t => t.TeamName, StringComparer.OrdinalIgnoreCase).ToList());

        var administrators = authorityRoster?.Administrators.ToDictionary(a => a.AuthorityUserId) ?? [];
        var candidates = authorityRoster?.Candidates?.ToDictionary(c => c.AuthorityUserId) ?? [];
        var canChangeAdministrators = authorityRoster?.CanManage == true;
        IReadOnlyList<string> ActionsOf(Guid userId) => !canChangeAdministrators ? []
            : administrators.ContainsKey(userId) ? (administrators.Count > 1 ? [RemoveSecurityAdministrator] : [])
            : candidates.ContainsKey(userId) ? [AppointSecurityAdministrator] : [];
        IReadOnlyList<string> RolesOf(Guid userId) => administrators.ContainsKey(userId) ? [SecurityAdministratorRole] : [];

        var rows = users.Select(u => new OrganizationMemberResponse(u.UserId, u.DisplayName, u.ExternalIssuerUri, u.ExternalSubjectIdentifier, u.CreatedOn, RolesOf(u.UserId),
            teamsOfUser.GetValueOrDefault(u.UserId) ?? [], u.UserId == context.LocalUserId, u.LastSignedInOn, u.LastSignedInOn is null ? "NotRecorded" : "SignedIn", ActionsOf(u.UserId))).ToList();
        var known = rows.Select(r => r.UserId).ToHashSet();
        var bound = administrators.Values.Select(a => (a.AuthorityUserId, a.IssuerUri, a.SubjectIdentifier))
            .Concat(candidates.Values.Select(c => (c.AuthorityUserId, c.IssuerUri, c.SubjectIdentifier)));
        foreach (var (userId, issuer, subject) in bound)
        {
            if (known.Add(userId))
            {
                rows.Add(new OrganizationMemberResponse(userId, string.Empty, issuer, subject, null, RolesOf(userId), teamsOfUser.GetValueOrDefault(userId) ?? [], userId == context.LocalUserId, null, "NeverSignedIn", ActionsOf(userId)));
            }
        }

        var matching = rows
            .Where(r => text is null || r.DisplayName.Contains(text, StringComparison.OrdinalIgnoreCase) || r.IdentitySubject.Contains(text, StringComparison.OrdinalIgnoreCase)
                        || r.IdentityIssuer.Contains(text, StringComparison.OrdinalIgnoreCase) || r.Teams.Any(t => t.TeamName.Contains(text, StringComparison.OrdinalIgnoreCase)))
            .Where(r => role switch { SecurityAdministratorRole => r.AuthorityRoles.Contains(SecurityAdministratorRole), NoRole => r.AuthorityRoles.Count == 0, _ => true })
            .Where(r => signIn is null || r.SignInStatus == signIn)
            .OrderBy(r => r.DisplayName.Length == 0 ? 1 : 0).ThenBy(r => r.DisplayName.Length == 0 ? r.IdentitySubject : r.DisplayName, StringComparer.OrdinalIgnoreCase).ThenBy(r => r.UserId)
            .ToList();
        offset = Math.Min(offset, matching.Count);
        var items = matching.Skip(offset).Take(size).ToList();
        var (next, previous) = OffsetPageCursor.Links(cursors, context, digest, offset, items.Count, size, matching.Count);
        var complete = authorityRoster?.Candidates is not null;
        return new OrganizationMemberDirectoryResponse(Source, items, authorityRoster is not null, rolesUnavailableReason, matching.Count, matching.Count == 0 ? 0 : offset + 1, size, next, previous,
            complete, complete ? CompleteCoverage : PartialCoverage,
            canChangeAdministrators && authorityRoster!.AppointmentOperational, canChangeAdministrators && !authorityRoster!.AppointmentOperational ? authorityRoster.AppointmentUnavailableReason : null);
    }

    private static string? Normalize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var trimmed = text.Trim();
        return trimmed.Length <= 120 ? trimmed : throw new BusinessException("INVALID_REQUEST", 422, "search text is too long");
    }

    private static string? Allowed(string? value, IReadOnlyList<string> allowed, string name) =>
        string.IsNullOrWhiteSpace(value) ? null : allowed.Contains(value.Trim()) ? value.Trim() : throw new BusinessException("INVALID_REQUEST", 422, "unknown " + name);
}
