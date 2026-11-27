using EnterpriseForms.Application.Common;
using EnterpriseForms.Application.Common.Authority;
using EnterpriseForms.Application.Common.Persistence;
using EnterpriseForms.Contracts.Features;

namespace EnterpriseForms.Application.Features.Sharing.GetShare;

/// <summary>
/// C10 M2 (WP-0 Section 3.5, D10-3): one share's details from the Authority's committed state, so the returned share revision —
/// and the ETag the endpoint serves — is the revision <c>UpdateShareScope</c> compares against, not a lagging local projection.
/// The Authority decides who may read it (owner, or a Manager on the share's committed lineage) and answers everyone else as for
/// an unknown share; this handler maps that to the same concealing 404 as the share list. Local rows contribute display names and
/// field keys only.
/// </summary>
public sealed class GetShareHandler(IBusinessStore store, IShareDetailsAuthority authority)
{
    public async Task<ShareDetailsResponse> HandleAsync(RequestContext context, Guid formRecordId, Guid recordShareId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        AuthorityShareDetails details;
        try
        {
            details = await authority.GetShareDetailsAsync(context.ToDelegatedUser(), context.OrganizationId, formRecordId, recordShareId, cancellationToken).ConfigureAwait(false);
        }
        catch (AuthorityClientException ex) when (ex.HttpStatusCode == 404)
        {
            throw new BusinessException("RECORD_NOT_FOUND", 404, "record not found");
        }

        if (details.RecordShareId != recordShareId || details.FormRecordId != formRecordId)
        {
            throw new BusinessException("RECORD_NOT_FOUND", 404, "record not found");
        }

        await using var scope = await store.BeginScopeAsync(cancellationToken).ConfigureAwait(false);
        var users = (await scope.GetUsersAsync(context.OrganizationId, cancellationToken).ConfigureAwait(false)).ToDictionary(u => u.UserId, u => u.DisplayName);
        var keys = new List<string>();
        if (details.FieldItemIds.Count > 0 && await scope.FindFormRecordAsync(context.OrganizationId, formRecordId, cancellationToken).ConfigureAwait(false) is { } record)
        {
            var fieldKeys = (await scope.GetFieldDefinitionsAsync(record.FormDefinitionId, cancellationToken).ConfigureAwait(false)).ToDictionary(f => f.FieldDefinitionId, f => f.FieldKey);
            var authoritative = details.FieldItemIds.ToHashSet();
            keys = (await scope.GetShareFieldsOfShareAsync(context.OrganizationId, recordShareId, cancellationToken).ConfigureAwait(false))
                .Where(f => authoritative.Contains(f.FieldValueItemId) && fieldKeys.ContainsKey(f.FieldDefinitionId))
                .Select(f => fieldKeys[f.FieldDefinitionId]).Distinct().Order(StringComparer.Ordinal).ToList();
        }

        var recipientUser = details.RecipientKind == "User" ? details.RecipientId : null;
        var recipientTeam = details.RecipientKind == "Team" ? details.RecipientId : null;
        return new ShareDetailsResponse(
            details.RecordShareId,
            details.FormRecordId,
            details.LifecycleState,
            recipientUser,
            recipientTeam,
            recipientUser is { } u ? users.GetValueOrDefault(u) : null,
            details.RecipientRole,
            details.FieldItemIds,
            keys,
            details.ShareExpiresOn,
            details.EffectiveExpiresOn,
            details.ShareRevision,
            details.ParentRecordShareId,
            details.DelegationDepth,
            details.LegacyLineageUnresolved,
            details.InvalidationReasonCode,
            details.GrantorUserId,
            details.GrantorUserId is { } g ? users.GetValueOrDefault(g) : null,
            details.AuthorizationRevision,
            (details.Documents ?? []).Select(d => new ShareDocumentDetailResponse(d.FileAttachmentId, d.CanViewDocument, d.CanDownloadDocument, d.CanReplaceDocument, d.SharedThroughVersionNumber)).ToList());
    }
}
