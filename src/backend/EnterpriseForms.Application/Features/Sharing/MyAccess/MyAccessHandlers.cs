using EnterpriseForms.Application.Common;
using EnterpriseForms.Application.Common.Authority;
using EnterpriseForms.Application.Common.Persistence;
using EnterpriseForms.Contracts.Features;

namespace EnterpriseForms.Application.Features.Sharing.MyAccess;

/// <summary>
/// C10 M2 (WP-0 Section 3.5): PG-066 "my access" and PG-023 effective scope, decided by the Authority from its committed state. Local
/// rows add form names only; an Authority refusal of the effective scope is the share list's concealing 404.
/// </summary>
public sealed class MyAccessHandlers(IBusinessStore store, IShareDetailsAuthority authority)
{
    public async Task<MyAccessResponse> GetMyAccessAsync(RequestContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var access = await authority.GetMyAccessAsync(context.ToDelegatedUser(), context.OrganizationId, cancellationToken).ConfigureAwait(false);
        var names = new Dictionary<Guid, string?>();
        await using (var scope = await store.BeginScopeAsync(cancellationToken).ConfigureAwait(false))
        {
            foreach (var recordId in access.Entries.Select(e => e.FormRecordId).Distinct())
            {
                var record = await scope.FindFormRecordAsync(context.OrganizationId, recordId, cancellationToken).ConfigureAwait(false);
                names[recordId] = record is null ? null : (await scope.FindFormDefinitionAsync(context.OrganizationId, record.FormDefinitionId, cancellationToken).ConfigureAwait(false))?.FormDefinitionName;
            }
        }

        return new MyAccessResponse(
            access.Entries.Select(e => new MyAccessEntryResponse(e.FormRecordId, names.GetValueOrDefault(e.FormRecordId), e.AccessSource, e.RecordShareId, e.TeamId, e.RecipientRole, e.EffectiveExpiresOn, e.ExpiringSoon)).ToList(),
            access.Truncated, access.ExpiringWithinDays, access.AuthorizationRevision);
    }

    public async Task<ShareEffectiveScopeResponse> GetEffectiveScopeAsync(RequestContext context, Guid formRecordId, Guid recordShareId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        AuthorityShareEffectiveScope scope;
        try
        {
            scope = await authority.GetShareEffectiveScopeAsync(context.ToDelegatedUser(), context.OrganizationId, formRecordId, recordShareId, cancellationToken).ConfigureAwait(false);
        }
        catch (AuthorityClientException ex) when (ex.HttpStatusCode == 404)
        {
            throw new BusinessException("RECORD_NOT_FOUND", 404, "record not found");
        }

        if (scope.RecordShareId != recordShareId || scope.FormRecordId != formRecordId)
        {
            throw new BusinessException("RECORD_NOT_FOUND", 404, "record not found");
        }

        return new ShareEffectiveScopeResponse(scope.RecordShareId, scope.FormRecordId, scope.Status, scope.RecipientRole, scope.EffectiveExpiresOn, scope.FieldItemIds,
            scope.Documents.Select(d => new ShareDocumentDetailResponse(d.FileAttachmentId, d.CanViewDocument, d.CanDownloadDocument, d.CanReplaceDocument, d.SharedThroughVersionNumber)).ToList(),
            scope.CanDelegate, scope.Lineage.Select(l => new ShareLineageStepResponse(l.RecordShareId, l.DelegationDepth, l.ShareExpiresOn)).ToList(), scope.AuthorizationRevision);
    }
}
