using EnterpriseForms.Application.Common;
using EnterpriseForms.Application.Common.Authority;
using EnterpriseForms.Application.Common.Persistence;
using EnterpriseForms.Contracts.Features;

namespace EnterpriseForms.Application.Features.FormRecords.ListRecords;

/// <summary>
/// Authorized discovery (B-07): record ids come exclusively from the authority's discover lookup; rows carry local
/// projection metadata only (form, status, timestamps) and are labelled unverified — opening a record performs the
/// full verified read. Child records are listed only when requested.
/// </summary>
public sealed class ListRecordsHandler(IBusinessStore store, IAuthorizationAuthorityClient authority)
{
    public async Task<RecordListResponse> HandleAsync(RequestContext context, Guid? formDefinitionId, bool includeChildren, int pageSize, string? continuationToken, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var size = Math.Clamp(pageSize, 1, 100);
        var page = await authority.DiscoverAuthorizedRecordsAsync(context.ToDelegatedUser(), context.OrganizationId, size, continuationToken, cancellationToken).ConfigureAwait(false);
        if (page.FormRecordIds.Count == 0)
        {
            return new RecordListResponse([], page.AuthorizationRevision, null);
        }

        await using var scope = await store.BeginScopeAsync(cancellationToken).ConfigureAwait(false);
        var records = await scope.GetFormRecordsAsync(context.OrganizationId, page.FormRecordIds, cancellationToken).ConfigureAwait(false);
        var names = (await scope.GetFormDefinitionsAsync(context.OrganizationId, cancellationToken).ConfigureAwait(false)).ToDictionary(d => d.FormDefinitionId, d => d.FormDefinitionName);
        var items = records
            .Where(r => formDefinitionId is null || r.FormDefinitionId == formDefinitionId)
            .Where(r => includeChildren || r.ParentFormRecordId is null)
            .Where(r => r.RecordStatus != "Deleted")
            .OrderByDescending(r => r.ModifiedOn)
            .Select(r => new RecordListItemResponse(r.FormRecordId, r.FormDefinitionId, names.GetValueOrDefault(r.FormDefinitionId, string.Empty), r.RecordStatus, r.CreatedOn, r.ModifiedOn, r.ParentFormRecordId, false))
            .ToList();
        return new RecordListResponse(items, page.AuthorizationRevision, page.ContinuationToken);
    }
}
