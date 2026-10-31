using EnterpriseForms.Application.Common;
using EnterpriseForms.Application.Common.Authority;
using EnterpriseForms.Application.Common.Paging;
using EnterpriseForms.Application.Common.Persistence;
using EnterpriseForms.Contracts.Features;

namespace EnterpriseForms.Application.Features.Identity.MemberDirectory;

public sealed record ApprovalQuery(string? Search, string? Status, int? PageSize, string? ContinuationToken);

/// <summary>
/// Owner proposals and membership requests as two independent server-paged lists (default 5 rows, newest first, stable tie-break on
/// the id), each with its own opaque cursor bound to the caller and the filters. The Authority roster (a bounded 30-day window) is
/// read per request and only one page of it leaves the server. Same access as the people directory (owners and Security
/// Administrators); every approval still goes through the Authority.
/// </summary>
public sealed class GetApprovalsHandler(IBusinessStore store, IAuthorizationAuthorityClient authority, IOrganizationRosterAuthority roster, IOpaqueCursorProtector cursors)
{
    public const int DefaultPageSize = 5;

    public static readonly IReadOnlyList<string> ProposalStatuses = ["Pending", "Approved", "Rejected", "Expired"];

    public static readonly IReadOnlyList<string> RequestStatuses = ["Pending", "Approved", "Rejected", "Expired"];

    public Task<OwnerProposalPageResponse> GetProposalsAsync(RequestContext context, ApprovalQuery query, CancellationToken cancellationToken) =>
        PageAsync(context, query, "owner-proposals", ProposalStatuses, (r, users, me) => Proposals(r, users, me),
            (items, total, start, size, next, previous) => new OwnerProposalPageResponse(items, total, start, size, next, previous),
            p => p.ProposedOn, p => p.ProposalId, p => p.ProposalState == "Consumed" ? "Approved" : p.ProposalState,
            (p, text) => Has(p.TargetDisplayName, text) || Has(p.ProposedByDisplayName, text) || Has(p.Reason, text), cancellationToken);

    public Task<MembershipRequestPageResponse> GetRequestsAsync(RequestContext context, ApprovalQuery query, CancellationToken cancellationToken) =>
        PageAsync(context, query, "membership-requests", RequestStatuses, (r, users, me) => Requests(r, users),
            (items, total, start, size, next, previous) => new MembershipRequestPageResponse(items, total, start, size, next, previous),
            q => q.RequestedOn, q => q.RequestId, q => q.RequestState,
            (q, text) => Has(q.DisplayNameHint, text) || Has(q.SubjectIdentifier, text) || Has(q.RequestedByDisplayName, text) || Has(q.Reason, text), cancellationToken);

    private async Task<TPage> PageAsync<TItem, TPage>(RequestContext context, ApprovalQuery query, string list, IReadOnlyList<string> statuses,
        Func<AuthorityOrganizationRoster, IReadOnlyDictionary<Guid, string>, Guid, IReadOnlyList<TItem>> map,
        Func<IReadOnlyList<TItem>, int, int, int, string?, string?, TPage> build, Func<TItem, DateTimeOffset> when, Func<TItem, Guid> id, Func<TItem, string> state, Func<TItem, string, bool> matches, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(query);
        var text = string.IsNullOrWhiteSpace(query.Search) ? null : query.Search.Trim();
        if (text is { Length: > 120 })
        {
            throw new BusinessException("INVALID_REQUEST", 422, "search text is too long");
        }

        var status = string.IsNullOrWhiteSpace(query.Status) ? null : statuses.Contains(query.Status.Trim()) ? query.Status.Trim() : throw new BusinessException("INVALID_REQUEST", 422, "unknown status filter");
        var size = Math.Clamp(query.PageSize ?? DefaultPageSize, 1, OffsetPageCursor.MaximumPageSize);
        var digest = OffsetPageCursor.Digest(list, text, status, size);
        await AdministrationAccess.RequireAdministratorAsync(authority, context, cancellationToken).ConfigureAwait(false);
        var offset = OffsetPageCursor.DecodeOffset(cursors, query.ContinuationToken, context, digest);

        AuthorityOrganizationRoster current;
        try
        {
            current = await roster.GetRosterAsync(context.ToDelegatedUser(), new OrganizationRosterQuery(context.OrganizationId), cancellationToken).ConfigureAwait(false);
        }
        catch (AuthorityClientException ex) when (ex.HttpStatusCode == 404)
        {
            throw new BusinessException("FORBIDDEN", 403, "organization owner or Security Administrator role required");
        }

        await using var scope = await store.BeginScopeAsync(cancellationToken).ConfigureAwait(false);
        var names = (await scope.GetUsersAsync(context.OrganizationId, cancellationToken).ConfigureAwait(false)).Where(u => u.DisplayName.Length > 0).ToDictionary(u => u.UserId, u => u.DisplayName);
        foreach (var member in current.Members.Where(m => !names.ContainsKey(m.AuthorityUserId)))
        {
            names[member.AuthorityUserId] = member.DisplayNameHint is { Length: > 0 } hint ? hint : member.SubjectIdentifier;
        }

        var matching = map(current, names, context.LocalUserId)
            .Where(item => status is null || state(item) == status)
            .Where(item => text is null || matches(item, text))
            .OrderByDescending(when).ThenByDescending(id)
            .ToList();
        offset = Math.Min(offset, matching.Count);
        var items = matching.Skip(offset).Take(size).ToList();
        var (next, previous) = OffsetPageCursor.Links(cursors, context, digest, offset, items.Count, size, matching.Count);
        return build(items, matching.Count, matching.Count == 0 ? 0 : offset + 1, size, next, previous);
    }

    private static List<OwnerProposalResponse> Proposals(AuthorityOrganizationRoster roster, IReadOnlyDictionary<Guid, string> names, Guid me) =>
        roster.OwnerProposals.Select(p => new OwnerProposalResponse(p.ProposalId, p.TargetAuthorityUserId, Name(names, p.TargetAuthorityUserId), p.ProposedChange, p.ProposedByAuthorityUserId, Name(names, p.ProposedByAuthorityUserId),
            p.Reason, p.ProposedOn, p.ExpiresOn, p.ProposalState, roster.CanManageMembers && p.ProposalState == "Pending" && p.ProposedByAuthorityUserId != me && p.TargetAuthorityUserId != me)).ToList();

    private static List<MembershipRequestResponse> Requests(AuthorityOrganizationRoster roster, IReadOnlyDictionary<Guid, string> names) =>
        roster.MembershipRequests.Select(r => new MembershipRequestResponse(r.RequestId, r.RequestedAuthorityUserId, r.IssuerUri, r.SubjectIdentifier, r.DisplayNameHint, r.InitialRoleName is null ? null : GetMemberDirectoryHandler.DesignerRole,
            r.RequestedByAuthorityUserId, Name(names, r.RequestedByAuthorityUserId), r.Reason, r.RequestedOn, r.ExpiresOn, r.RequestState, r.DecidedOn, r.DecisionReason)).ToList();

    private static string Name(IReadOnlyDictionary<Guid, string> names, Guid id) => names.TryGetValue(id, out var name) ? name : id.ToString("D");

    private static bool Has(string? value, string text) => value is not null && value.Contains(text, StringComparison.OrdinalIgnoreCase);
}
