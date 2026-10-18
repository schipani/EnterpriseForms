using EnterpriseForms.Application.Common;
using EnterpriseForms.Application.Common.Authority;
using EnterpriseForms.Application.Common.Persistence;
using EnterpriseForms.Contracts.Features;

namespace EnterpriseForms.Application.Features.Sharing.ListShares;

/// <summary>
/// Shares of one record for its access managers (CLAUDE.md §4H "who has access"). The rows are the local projection of
/// authority share commands: they are shown only after the authority confirms <c>manage_access</c> on the record for the
/// caller, and they confer nothing — every recipient's actual access is decided by the authority on each request.
/// </summary>
public sealed class ListSharesHandler(IBusinessStore store, IAuthorizationAuthorityClient authority)
{
    public async Task<RecordShareListResponse> HandleAsync(RequestContext context, Guid formRecordId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var decision = await authority.CheckPermissionAsync(context.ToDelegatedUser(), new PermissionCheckRequest(context.OrganizationId, new ResourceReference(ProtectedResourceKind.FormRecord, formRecordId), ProtectedAction.ManageAccess), cancellationToken).ConfigureAwait(false);
        if (!decision.Allowed)
        {
            // Concealment-safe: a caller who cannot manage access learns nothing about the record's existence.
            throw new BusinessException("RECORD_NOT_FOUND", 404, "record not found");
        }

        await using var scope = await store.BeginScopeAsync(cancellationToken).ConfigureAwait(false);
        var record = await scope.FindFormRecordAsync(context.OrganizationId, formRecordId, cancellationToken).ConfigureAwait(false)
                     ?? throw new BusinessException("RECORD_NOT_FOUND", 404, "record not found");
        var shares = await scope.GetSharesOfRecordAsync(context.OrganizationId, formRecordId, cancellationToken).ConfigureAwait(false);
        var shareFields = await scope.GetShareFieldsOfRecordAsync(context.OrganizationId, formRecordId, cancellationToken).ConfigureAwait(false);
        var fieldKeys = (await scope.GetFieldDefinitionsAsync(record.FormDefinitionId, cancellationToken).ConfigureAwait(false)).ToDictionary(f => f.FieldDefinitionId, f => f.FieldKey);
        var users = (await scope.GetUsersAsync(context.OrganizationId, cancellationToken).ConfigureAwait(false)).ToDictionary(u => u.UserId, u => u.DisplayName);
        var fieldsByShare = shareFields.GroupBy(f => f.RecordShareId).ToDictionary(g => g.Key, g => g.Select(f => fieldKeys.GetValueOrDefault(f.FieldDefinitionId, f.FieldDefinitionId.ToString("D"))).OrderBy(k => k, StringComparer.Ordinal).ToList());

        var items = shares.Select(s => new RecordShareSummaryResponse(
                s.RecordShareId,
                s.FormRecordId,
                s.SharedByUserId,
                users.GetValueOrDefault(s.SharedByUserId),
                s.RecipientUserId,
                s.RecipientUserId is { } ru ? users.GetValueOrDefault(ru) : null,
                s.RecipientTeamId,
                s.RecipientRole,
                s.ShareScope,
                s.ShareStatus,
                s.ShareRevision,
                s.ShareCreatedOn,
                s.ShareRevokedOn,
                fieldsByShare.GetValueOrDefault(s.RecordShareId, [])))
            .ToList();
        return new RecordShareListResponse(items, decision.AuthorizationRevision);
    }
}
