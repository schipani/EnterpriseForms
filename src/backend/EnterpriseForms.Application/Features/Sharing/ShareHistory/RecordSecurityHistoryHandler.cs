using EnterpriseForms.Application.Common;
using EnterpriseForms.Application.Common.Authority;
using EnterpriseForms.Application.Common.Persistence;
using EnterpriseForms.Contracts.Features;

namespace EnterpriseForms.Application.Features.Sharing.ShareHistory;

/// <summary>
/// C10 M2 (WP-0 Section 3.5, D10-11): the record's sharing history (<c>shares/history</c>) and ownership history
/// (<c>ownership-history</c>), both from the Authority's committed state; each change keeps its command's own state, so a pending
/// change is never shown as completed. The Authority decides who may read it (owner or ManageAccess); everyone else gets the same
/// concealing 404 as the share list. Local rows contribute display names only.
/// </summary>
public sealed class RecordSecurityHistoryHandler(IBusinessStore store, IShareDetailsAuthority authority)
{
    public async Task<RecordSecurityHistoryResponse> HandleAsync(RequestContext context, Guid formRecordId, bool ownershipOnly, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        AuthorityRecordSecurityHistory history;
        try
        {
            history = await authority.GetRecordSecurityHistoryAsync(context.ToDelegatedUser(), context.OrganizationId, formRecordId, cancellationToken).ConfigureAwait(false);
        }
        catch (AuthorityClientException ex) when (ex.HttpStatusCode == 404)
        {
            throw new BusinessException("RECORD_NOT_FOUND", 404, "record not found");
        }

        if (history.FormRecordId != formRecordId)
        {
            throw new BusinessException("RECORD_NOT_FOUND", 404, "record not found");
        }

        Dictionary<Guid, string> users;
        await using (var scope = await store.BeginScopeAsync(cancellationToken).ConfigureAwait(false))
        {
            users = (await scope.GetUsersAsync(context.OrganizationId, cancellationToken).ConfigureAwait(false)).ToDictionary(u => u.UserId, u => u.DisplayName);
        }

        var items = new List<RecordSecurityHistoryItemResponse>();
        foreach (var item in history.Items)
        {
            var changes = item.Changes.Where(c => !ownershipOnly || c.ChangeKind is "OwnerAdded" or "OwnerRemoved")
                .Select(c => new RecordSecurityChangeResponse(c.ChangeKind, c.RecordShareId, c.ShareEndState, c.InvalidationReasonCode, c.SubjectKind, c.SubjectId,
                    c.SubjectKind == "User" && c.SubjectId is { } subject ? users.GetValueOrDefault(subject) : null))
                .ToList();
            if (changes.Count == 0)
            {
                continue;
            }

            items.Add(new RecordSecurityHistoryItemResponse(item.SecurityCommandId, item.CommandKind, item.CommandState, item.OccurredOn, item.EffectiveOn, item.CommittedAuthorizationRevision,
                item.ActorUserId, item.ActorUserId is { } actor ? users.GetValueOrDefault(actor) : null, changes));
        }

        return new RecordSecurityHistoryResponse(formRecordId, items, history.Truncated, history.AuthorizationRevision);
    }
}
