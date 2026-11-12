using EnterpriseForms.Application.Common;
using EnterpriseForms.Application.Common.Authority;
using EnterpriseForms.Application.Common.Persistence;
using EnterpriseForms.Contracts.Features;

namespace EnterpriseForms.Application.Features.FormRecords.ListRecords;

/// <summary>
/// IX-6 configuration, section <c>RecordList</c>: <c>Engine=VerifiedScan</c> (default; the C-29 listing) or <c>Indexed</c> (IX-6: the
/// Authority lists from verified index entries when it can, with the unchanged listing as the fallback). Indexed is a Development/Testing
/// capability, like the other indexed engines; the host refuses it elsewhere.
/// </summary>
public sealed class RecordListOptions
{
    public const string SectionName = "RecordList";

    public const string EngineVerifiedScan = "VerifiedScan";

    public const string EngineIndexed = "Indexed";

    public string Engine { get; set; } = EngineVerifiedScan;

    public void Validate(string environmentName)
    {
        if (Engine is not (EngineVerifiedScan or EngineIndexed))
        {
            throw new InvalidOperationException("RecordList:Engine must be VerifiedScan or Indexed");
        }

        if (Engine == EngineIndexed && environmentName is not ("Development" or "Testing"))
        {
            throw new InvalidOperationException($"RecordList:Engine=Indexed is a development/test capability (IX-6) and must not be set in the {environmentName} environment. Refusing to start.");
        }
    }
}

/// <summary>IX-6 observability: which engine answered a listing and why the indexed one did not (codes only).</summary>
public interface IRecordListEngineObserver
{
    void Listed(string engine, string? fallbackReason);
}

/// <summary>
/// IX-6 (IX-D12, design R1 §E.6): the indexed engine of My Records. It answers only what the Authority can answer from verified index
/// entries: no record-reference lookup (<c>q</c>), no form-name sort (form names are cosmetic business data), no <c>Deleted</c> filter.
/// Everything else is the Authority's decision, including the discoverable set and the order; this class adds display names, the record
/// reference and the cursor. When the Authority's index cannot answer (an entry missing or outdated, the projection not servable, a limit),
/// <see cref="TryListAsync"/> returns null and the unchanged listing answers instead; one listing never combines both, because the cursor
/// records the engine and a page of the other engine's listing is refused (<c>LIST_CHANGED</c>, the client restarts).
/// </summary>
public sealed class RecordListIndexedEngine(RecordListOptions options, IIndexedRecordListAuthority authority, IBusinessStore store, IOpaqueCursorProtector cursors, IRecordListEngineObserver? observer = null)
{
    public const string CursorEngine = "Indexed";

    /// <summary>Authority refusals meaning "the index cannot list this caller now": the unchanged listing answers.</summary>
    public static readonly IReadOnlySet<string> FallbackCodes = new HashSet<string>(StringComparer.Ordinal)
    {
        "RECORD_LIST_INDEX_INCOMPLETE", "SEARCH_INDEX_NOT_READY", "SEARCH_INDEX_INCONSISTENT", "SEARCH_INDEX_VERIFICATION_OVERDUE", "SEARCH_VERIFICATION_LIMIT",
    };

    public bool Enabled => options.Engine == RecordListOptions.EngineIndexed;

    public static bool Supports(RecordListQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        return query.Search is null && query.SortBy != "formName" && query.RecordStatus is null or "Draft" or "Submitted" or "Archived";
    }

    /// <summary>
    /// The indexed listing, or null when the unchanged listing must answer (unsupported query, or the index cannot list now). The caller
    /// (<see cref="ListRecordsHandler"/>) decides that the indexed engine is selected.
    /// </summary>
    public async Task<RecordListResponse?> TryListAsync(RequestContext context, RecordListQuery query, RecordListCursor? cursor, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(query);
        if (!Supports(query))
        {
            return null;
        }

        AuthorityIndexedRecordListResult result;
        try
        {
            result = await authority.ListRecordsAsync(context.ToDelegatedUser(),
                new AuthorityIndexedRecordListQuery(query.FormDefinitionId, query.RecordStatus, query.IncludeChildren, query.IncludeArchived, query.SortBy, query.SortDirection, cursor?.Offset ?? 0, query.PageSize,
                    cursor?.AuthorizationRevision), cancellationToken).ConfigureAwait(false);
        }
        catch (AuthorityClientException ex) when (FallbackCodes.Contains(ex.ErrorCode))
        {
            observer?.Listed(RecordListOptions.EngineVerifiedScan, ex.ErrorCode);
            if (cursor is not null)
            {
                // A later page of an indexed listing never continues on the other engine (its order differs).
                throw new BusinessException("LIST_CHANGED", 409, "the list changed since this page was loaded; reload the list from the first page");
            }

            return null;
        }
        catch (AuthorityClientException ex) when (ex.ErrorCode == "SEARCH_SNAPSHOT_CHANGED")
        {
            throw new BusinessException("LIST_CHANGED", 409, "access changed since this page was loaded; reload the list from the first page");
        }

        observer?.Listed(RecordListOptions.EngineIndexed, null);
        Dictionary<Guid, string> formNames = [];
        Dictionary<Guid, string> userNames = [];
        await using (var scope = await store.BeginScopeAsync(cancellationToken).ConfigureAwait(false))
        {
            formNames = (await scope.GetFormDefinitionsAsync(context.OrganizationId, cancellationToken).ConfigureAwait(false)).ToDictionary(d => d.FormDefinitionId, d => d.FormDefinitionName);
            userNames = (await scope.GetUsersAsync(context.OrganizationId, cancellationToken).ConfigureAwait(false)).ToDictionary(u => u.UserId, u => u.DisplayName);
        }

        // ProjectionVerified stays false: the business copy was not read; the listed facts are the Authority's verified index header facts.
        var items = result.Items.Select(i => new RecordListItemResponse(i.FormRecordId, i.FormDefinitionId, formNames.GetValueOrDefault(i.FormDefinitionId, string.Empty), i.RecordStatusName,
                i.RecordRegisteredOn, i.ContentHeadModifiedOn, i.ParentFormRecordId, false, RecordListQuery.ReferenceFor(i.FormRecordId), userNames.GetValueOrDefault(i.CreatedByBusinessUserId),
                i.CreatedByBusinessUserId == context.LocalUserId, i.Editable))
            .ToList();
        var availableForms = result.AvailableFormDefinitionIds.Select(id => new RecordListFormOption(id, formNames.GetValueOrDefault(id, string.Empty)))
            .OrderBy(f => f.FormDefinitionName, StringComparer.OrdinalIgnoreCase).ThenBy(f => f.FormDefinitionId).ToList();
        var digest = query.ScopeDigest();
        var offset = result.Offset;
        var next = offset + items.Count < result.TotalCount ? new RecordListCursor(context.OrganizationId, context.LocalUserId, result.AuthorizationRevision, digest, offset + items.Count, CursorEngine).Encode(cursors) : null;
        var previous = offset > 0 ? new RecordListCursor(context.OrganizationId, context.LocalUserId, result.AuthorizationRevision, digest, Math.Max(0, offset - query.PageSize), CursorEngine).Encode(cursors) : null;
        return new RecordListResponse(items, result.AuthorizationRevision, next, result.TotalCount, result.TotalCount == 0 ? 0 : offset + 1, query.PageSize, previous, availableForms,
            result.WithoutContentCount, result.WithoutContentRecordIds, query.SortBy, query.SortDirection);
    }
}
