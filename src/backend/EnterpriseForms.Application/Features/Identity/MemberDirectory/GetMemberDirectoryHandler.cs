using EnterpriseForms.Application.Common;
using EnterpriseForms.Application.Common.Authority;
using EnterpriseForms.Application.Common.Paging;
using EnterpriseForms.Application.Common.Persistence;
using EnterpriseForms.Contracts.Features;

namespace EnterpriseForms.Application.Features.Identity.MemberDirectory;

public sealed record MemberDirectoryQuery(string? Search, string? Role, string? SignInStatus, int? PageSize, string? ContinuationToken, string? MembershipState = null);

/// <summary>
/// People directory (PG-050; C8 M2). Rows are the Authority's complete roster (AC-7.3): every identity binding of the
/// organization with its enabled state and organization roles, for organization owners and Security Administrators. The
/// business <c>users</c> directory adds names, first seen, last sign-in and teams (labels only). Owner proposals and
/// membership requests come from the same read. Action hints mirror the Authority's rules for the caller; the Authority
/// decides every command. If the roster is unavailable the page falls back to the business directory with no roles and no
/// actions (never guessed). Paged on the server (default 15) with an opaque cursor bound to the caller and filters.
/// </summary>
public sealed class GetMemberDirectoryHandler(IBusinessStore store, IAuthorizationAuthorityClient authority, IOrganizationRosterAuthority roster, IOpaqueCursorProtector cursors)
{
    public const string Source = "Authorization Authority roster of identity bindings and organization roles, with names, sign-in times and teams from the business directory";

    public const string FallbackSource = "business directory only (the Authorization Authority roster is unavailable)";

    public const string SecurityAdministratorRole = "SecurityAdministrator";
    public const string OrganizationOwnerRole = "OrganizationOwner";
    public const string DesignerRole = "Designer";
    public const string NoRole = "None";

    public const string AppointSecurityAdministrator = "AppointSecurityAdministrator";
    public const string RemoveSecurityAdministrator = "RemoveSecurityAdministrator";
    public const string GrantDesigner = "GrantDesigner";
    public const string RevokeDesigner = "RevokeDesigner";
    public const string ProposeOwnerGrant = "ProposeOwnerGrant";
    public const string ProposeOwnerRevoke = "ProposeOwnerRevoke";
    public const string DisableMembership = "DisableMembership";
    public const string EnableMembership = "EnableMembership";
    public const string ProposeOwnerDisable = "ProposeOwnerDisable";
    public const string ProposeOwnerEnable = "ProposeOwnerEnable";
    public const string EditProfile = "EditProfile";

    public static readonly IReadOnlyList<string> RoleFilters = [OrganizationOwnerRole, DesignerRole, SecurityAdministratorRole, NoRole];

    public static readonly IReadOnlyList<string> SignInStatuses = ["SignedIn", "NotRecorded", "NeverSignedIn"];

    public static readonly IReadOnlyList<string> MembershipStates = ["Enabled", "Disabled"];

    public const string CompleteCoverage = "Lists every identity the Authorization Authority has bound to this organization, enabled or disabled, with its organization roles.";

    public const string FallbackCoverage = "The Authorization Authority roster is unavailable: only people known to this application are listed, without roles or membership state, and no change can be made.";

    private static readonly Dictionary<string, string> RoleNames = new(StringComparer.Ordinal)
    {
        ["organization_owner"] = OrganizationOwnerRole,
        ["designer"] = DesignerRole,
        ["system_administrator"] = SecurityAdministratorRole,
    };

    public async Task<OrganizationMemberDirectoryResponse> GetAsync(RequestContext context, MemberDirectoryQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(query);
        var text = Normalize(query.Search);
        var role = Allowed(query.Role, RoleFilters, "role filter");
        var signIn = Allowed(query.SignInStatus, SignInStatuses, "sign-in status filter");
        var membership = Allowed(query.MembershipState, MembershipStates, "membership filter");
        var size = OffsetPageCursor.ClampPageSize(query.PageSize);
        var digest = OffsetPageCursor.Digest("organization-members", text, role, signIn, membership, size);
        await AdministrationAccess.RequireAdministratorAsync(authority, context, cancellationToken).ConfigureAwait(false);
        var offset = OffsetPageCursor.DecodeOffset(cursors, query.ContinuationToken, context, digest);

        AuthorityOrganizationRoster? authorityRoster = null;
        string? rolesUnavailableReason = null;
        try
        {
            authorityRoster = await roster.GetRosterAsync(context.ToDelegatedUser(), new OrganizationRosterQuery(context.OrganizationId), cancellationToken).ConfigureAwait(false);
        }
        catch (AuthorityClientException ex) when (ex.HttpStatusCode != 404)
        {
            rolesUnavailableReason = "the Authorization Authority roster is unavailable; no role or membership state is shown and no change can be made";
        }

        await using var scope = await store.BeginScopeAsync(cancellationToken).ConfigureAwait(false);
        var users = (await scope.GetUsersAsync(context.OrganizationId, cancellationToken).ConfigureAwait(false)).ToDictionary(u => u.UserId);
        var teams = (await scope.GetTeamsAsync(context.OrganizationId, cancellationToken).ConfigureAwait(false)).Where(t => t.IsActive).ToDictionary(t => t.TeamId, t => t.TeamName);
        var memberships = await scope.GetActiveTeamMembershipsAsync(context.OrganizationId, null, cancellationToken).ConfigureAwait(false);
        var teamsOfUser = memberships.Where(m => teams.ContainsKey(m.TeamId)).GroupBy(m => m.UserId)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<DirectoryTeamResponse>)g.Select(m => new DirectoryTeamResponse(m.TeamId, teams[m.TeamId])).OrderBy(t => t.TeamName, StringComparer.OrdinalIgnoreCase).ToList());

        List<OrganizationMemberResponse> rows;
        IReadOnlyList<OwnerProposalResponse> proposals = [];
        IReadOnlyList<MembershipRequestResponse> requests = [];
        if (authorityRoster is null)
        {
            rows = users.Values.Select(u => new OrganizationMemberResponse(u.UserId, u.DisplayName, u.ExternalIssuerUri, u.ExternalSubjectIdentifier, u.CreatedOn, [], teamsOfUser.GetValueOrDefault(u.UserId) ?? [],
                u.UserId == context.LocalUserId, u.LastSignedInOn, u.LastSignedInOn is null ? "NotRecorded" : "SignedIn", [], "Unknown")).ToList();
        }
        else
        {
            var me = context.LocalUserId;
            string NameOf(Guid id) => users.TryGetValue(id, out var u) && u.DisplayName.Length > 0 ? u.DisplayName
                : authorityRoster.Members.FirstOrDefault(m => m.AuthorityUserId == id)?.DisplayNameHint ?? authorityRoster.Members.FirstOrDefault(m => m.AuthorityUserId == id)?.SubjectIdentifier ?? id.ToString("D");
            var pending = authorityRoster.OwnerProposals.Where(p => p.ProposalState == "Pending").ToList();
            var enabledAdministrators = authorityRoster.Members.Count(m => m.MembershipEnabled && m.OrganizationRoles.Contains("system_administrator"));
            rows = authorityRoster.Members.Select(m =>
            {
                users.TryGetValue(m.AuthorityUserId, out var user);
                var roles = m.OrganizationRoles.Select(r => RoleNames.GetValueOrDefault(r, r)).ToList();
                return new OrganizationMemberResponse(m.AuthorityUserId, user is { DisplayName.Length: > 0 } ? user.DisplayName : m.DisplayNameHint ?? string.Empty, m.IssuerUri, m.SubjectIdentifier, user?.CreatedOn, roles,
                    teamsOfUser.GetValueOrDefault(m.AuthorityUserId) ?? [], m.AuthorityUserId == me, user?.LastSignedInOn, user is null ? "NeverSignedIn" : user.LastSignedInOn is null ? "NotRecorded" : "SignedIn",
                    ActionsFor(authorityRoster, m, roles, me, pending, enabledAdministrators, user is not null), m.MembershipEnabled ? "Enabled" : "Disabled", m.DisabledOn);
            }).ToList();
            proposals = authorityRoster.OwnerProposals.Select(p => new OwnerProposalResponse(p.ProposalId, p.TargetAuthorityUserId, NameOf(p.TargetAuthorityUserId), p.ProposedChange, p.ProposedByAuthorityUserId, NameOf(p.ProposedByAuthorityUserId),
                p.Reason, p.ProposedOn, p.ExpiresOn, p.ProposalState, authorityRoster.CanManageMembers && p.ProposalState == "Pending" && p.ProposedByAuthorityUserId != me && p.TargetAuthorityUserId != me)).ToList();
            requests = authorityRoster.MembershipRequests.Select(r => new MembershipRequestResponse(r.RequestId, r.RequestedAuthorityUserId, r.IssuerUri, r.SubjectIdentifier, r.DisplayNameHint, r.InitialRoleName is null ? null : DesignerRole,
                r.RequestedByAuthorityUserId, NameOf(r.RequestedByAuthorityUserId), r.Reason, r.RequestedOn, r.ExpiresOn, r.RequestState, r.DecidedOn, r.DecisionReason)).ToList();
        }

        var matching = rows
            .Where(r => text is null || r.DisplayName.Contains(text, StringComparison.OrdinalIgnoreCase) || r.IdentitySubject.Contains(text, StringComparison.OrdinalIgnoreCase)
                        || r.IdentityIssuer.Contains(text, StringComparison.OrdinalIgnoreCase) || r.Teams.Any(t => t.TeamName.Contains(text, StringComparison.OrdinalIgnoreCase)))
            .Where(r => role is null || (role == NoRole ? r.AuthorityRoles.Count == 0 : r.AuthorityRoles.Contains(role)))
            .Where(r => signIn is null || r.SignInStatus == signIn)
            .Where(r => membership is null || r.MembershipState == membership)
            .OrderBy(r => r.DisplayName.Length == 0 ? 1 : 0).ThenBy(r => r.DisplayName.Length == 0 ? r.IdentitySubject : r.DisplayName, StringComparer.OrdinalIgnoreCase).ThenBy(r => r.UserId)
            .ToList();
        offset = Math.Min(offset, matching.Count);
        var items = matching.Skip(offset).Take(size).ToList();
        var (next, previous) = OffsetPageCursor.Links(cursors, context, digest, offset, items.Count, size, matching.Count);
        var available = authorityRoster is not null;
        var canAdministerSecurity = authorityRoster?.CanAdministerSecurity == true;
        return new OrganizationMemberDirectoryResponse(available ? Source : FallbackSource, items, available, rolesUnavailableReason, matching.Count, matching.Count == 0 ? 0 : offset + 1, size, next, previous,
            available, available ? CompleteCoverage : FallbackCoverage,
            canAdministerSecurity && authorityRoster!.CommandsOperational, canAdministerSecurity && !authorityRoster!.CommandsOperational ? authorityRoster.CommandsUnavailableReason : null,
            authorityRoster?.CanManageMembers == true, authorityRoster?.CommandsOperational == true, authorityRoster is { CommandsOperational: false } ? authorityRoster.CommandsUnavailableReason : null,
            authorityRoster?.OperatorApprovalOperational == true, authorityRoster?.OrganizationIdentityIssuer, proposals, requests);
    }

    /// <summary>Hints only, mirroring the Authority (owners for membership and owner/designer roles, Security Administrators for their role; never oneself).</summary>
    private static List<string> ActionsFor(AuthorityOrganizationRoster roster, AuthorityRosterMember member, List<string> roles, Guid me, List<AuthorityOwnerProposal> pending, int enabledAdministrators, bool hasProfile)
    {
        var actions = new List<string>();
        // Own profile: the My profile page (never offered on one's own People row, like every other owner action).
        if (hasProfile && roster.CanManageMembers && member.AuthorityUserId != me)
        {
            actions.Add(EditProfile);
        }

        var self = member.AuthorityUserId == me;
        var owner = roles.Contains(OrganizationOwnerRole);
        bool Pending(string change) => pending.Any(p => p.TargetAuthorityUserId == member.AuthorityUserId && p.ProposedChange == change);
        if (roster.CanManageMembers && !self)
        {
            if (roles.Contains(DesignerRole))
            {
                actions.Add(RevokeDesigner);
            }
            else if (member.MembershipEnabled)
            {
                actions.Add(GrantDesigner);
            }

            if (member.MembershipEnabled && !owner && !Pending("GrantOwner"))
            {
                actions.Add(ProposeOwnerGrant);
            }

            if (owner && !Pending("RevokeOwner"))
            {
                actions.Add(ProposeOwnerRevoke);
            }

            if (member.MembershipEnabled)
            {
                actions.Add(owner ? (Pending("DisableOwner") ? string.Empty : ProposeOwnerDisable) : DisableMembership);
            }
            else
            {
                actions.Add(owner ? (Pending("EnableOwner") ? string.Empty : ProposeOwnerEnable) : EnableMembership);
            }
        }

        if (roster.CanAdministerSecurity)
        {
            if (roles.Contains(SecurityAdministratorRole))
            {
                if (enabledAdministrators > 1 || !member.MembershipEnabled)
                {
                    actions.Add(RemoveSecurityAdministrator);
                }
            }
            else if (member.MembershipEnabled)
            {
                actions.Add(AppointSecurityAdministrator);
            }
        }

        actions.RemoveAll(a => a.Length == 0);
        return actions;
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
