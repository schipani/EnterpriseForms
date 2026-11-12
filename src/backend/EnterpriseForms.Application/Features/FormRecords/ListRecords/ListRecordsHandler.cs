using EnterpriseForms.Application.Common;
using EnterpriseForms.Application.Common.Authority;
using EnterpriseForms.Application.Common.Persistence;
using EnterpriseForms.Contracts.Features;

namespace EnterpriseForms.Application.Features.FormRecords.ListRecords;

/// <summary>
/// My Records (PG-015, C-29). Authorized discovery (B-07): record ids come exclusively from the authority's discover
/// lookup — every page of it, at one authorization revision — and rows carry allowlisted local projection metadata
/// only (form, reference, status, timestamps, creator), labelled unverified; opening a record performs the full
/// verified read. Filtering, sorting and paging run over that complete set, so totals and page boundaries are exact for
/// the listed metadata. A record the authority lists without business metadata is reported, never silently dropped.
/// <para>
/// Limits of this guarantee (G06 stays open): the filters and sort keys are business-database metadata, which a hostile
/// business-database administrator can alter (for example to move a record out of a filter). The list never shows a
/// record the caller may not discover and never shows field values, but it does not independently verify metadata.
/// </para>
/// </summary>
/// <para>
/// IX-6 (IX-D12): with <c>RecordList:Engine=Indexed</c> (Development/Testing), the Authority answers a supported listing from verified
/// index entries (<see cref="RecordListIndexedEngine"/>); this listing answers otherwise, unchanged. The first page decides the engine
/// and its cursor keeps it, so one listing never combines both.
/// </para>
public sealed class ListRecordsHandler(IBusinessStore store, IAuthorizationAuthorityClient authority, IOpaqueCursorProtector cursors, RecordListIndexedEngine? indexed = null)
{
    public const int MaximumUnavailableIdsReported = 50;

    /// <param name="engineOverride">Testing only (the endpoint passes it only in the Testing environment): <c>Indexed</c> or <c>VerifiedScan</c> instead of the configured engine.</param>
    public async Task<RecordListResponse> HandleAsync(RequestContext context, RecordListQuery query, string? continuationToken, CancellationToken cancellationToken, string? engineOverride = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(query);
        var cursor = string.IsNullOrEmpty(continuationToken) ? null : RecordListCursor.DecodeAny(cursors, continuationToken, context, query);
        var useIndexed = indexed is not null && (engineOverride is null ? indexed.Enabled : engineOverride == RecordListOptions.EngineIndexed);
        if (indexed is not null && (cursor is null || cursor.Engine == RecordListIndexedEngine.CursorEngine))
        {
            if (cursor is not null && !useIndexed)
            {
                throw new BusinessException("LIST_CHANGED", 409, "the list changed since this page was loaded; reload the list from the first page");
            }

            if (useIndexed && await indexed.TryListAsync(context, query, cursor, cancellationToken).ConfigureAwait(false) is { } answered)
            {
                return answered;
            }
        }

        var user = context.ToDelegatedUser();
        var authorized = await AuthorizedRecordEnumerator.EnumerateAsync(authority, user, context.OrganizationId, cancellationToken).ConfigureAwait(false);
        // Unified exact search: the ids that match are decided from the authority's complete discoverable set (never from the
        // business database), so an unauthorized or missing record is simply absent. Everything below then filters, sorts and
        // pages these matches like any other listing (AND semantics).
        var search = query.SearchQuery;
        var searchIds = search is null ? authorized.FormRecordIds : search.Match(authorized.FormRecordIds);
        var offset = string.IsNullOrEmpty(continuationToken) ? 0 : RecordListCursor.Decode(cursors, continuationToken, context, query, authorized.AuthorizationRevision).Offset;

        List<Domain.Features.FormRecords.FormRecord> records = [];
        Dictionary<Guid, string> formNames = [];
        Dictionary<Guid, string> userNames = [];
        if (searchIds.Count > 0)
        {
            await using var scope = await store.BeginScopeAsync(cancellationToken).ConfigureAwait(false);
            records = await scope.GetFormRecordsAsync(context.OrganizationId, searchIds, cancellationToken).ConfigureAwait(false);
            formNames = (await scope.GetFormDefinitionsAsync(context.OrganizationId, cancellationToken).ConfigureAwait(false)).ToDictionary(d => d.FormDefinitionId, d => d.FormDefinitionName);
            userNames = (await scope.GetUsersAsync(context.OrganizationId, cancellationToken).ConfigureAwait(false)).ToDictionary(u => u.UserId, u => u.DisplayName);
        }

        // The authority's set is the boundary: a business row whose id the authority did not list is never shown (the
        // query is by those ids), and an authorized id without a business row is reported instead of disappearing.
        var described = records.Select(r => r.FormRecordId).ToHashSet();
        var unavailable = searchIds.Where(id => !described.Contains(id)).ToList();
        var scopeRecords = records.Where(r => search is not null || query.IncludeChildren || r.ParentFormRecordId is null).ToList();
        var availableForms = scopeRecords.Select(r => r.FormDefinitionId).Distinct()
            .Select(id => new RecordListFormOption(id, formNames.GetValueOrDefault(id, string.Empty)))
            .OrderBy(f => f.FormDefinitionName, StringComparer.OrdinalIgnoreCase).ThenBy(f => f.FormDefinitionId)
            .ToList();

        var ordered = query.Apply(records, formNames);
        if (offset > ordered.Count)
        {
            offset = ordered.Count;
        }

        var page = ordered.Skip(offset).Take(query.PageSize).ToList();
        var editable = new HashSet<Guid>();
        if (page.Count > 0)
        {
            var decisions = await authority.CheckPermissionsBatchAsync(user,
                page.Select(r => new PermissionCheckRequest(context.OrganizationId, new ResourceReference(ProtectedResourceKind.FormRecord, r.FormRecordId), ProtectedAction.Edit)).ToList(),
                cancellationToken).ConfigureAwait(false);
            for (var i = 0; i < page.Count; i++)
            {
                if (decisions[i].Allowed)
                {
                    editable.Add(page[i].FormRecordId);
                }
            }
        }

        var items = page.Select(r => new RecordListItemResponse(
                r.FormRecordId, r.FormDefinitionId, formNames.GetValueOrDefault(r.FormDefinitionId, string.Empty), r.RecordStatus, r.CreatedOn, r.ModifiedOn, r.ParentFormRecordId, false,
                RecordListQuery.ReferenceFor(r.FormRecordId), userNames.GetValueOrDefault(r.CreatedByUserId), r.CreatedByUserId == context.LocalUserId, editable.Contains(r.FormRecordId)))
            .ToList();
        var digest = query.ScopeDigest();
        var next = offset + page.Count < ordered.Count ? new RecordListCursor(context.OrganizationId, context.LocalUserId, authorized.AuthorizationRevision, digest, offset + page.Count).Encode(cursors) : null;
        var previous = offset > 0 ? new RecordListCursor(context.OrganizationId, context.LocalUserId, authorized.AuthorizationRevision, digest, Math.Max(0, offset - query.PageSize)).Encode(cursors) : null;
        return new RecordListResponse(items, authorized.AuthorizationRevision, next,
            ordered.Count, ordered.Count == 0 ? 0 : offset + 1, query.PageSize, previous, availableForms,
            unavailable.Count, unavailable.Take(MaximumUnavailableIdsReported).ToList(), query.SortBy, query.SortDirection);
    }
}
