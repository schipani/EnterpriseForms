using System.Text.RegularExpressions;
using EnterpriseForms.Application.Common;
using EnterpriseForms.Application.Common.Authority;
using EnterpriseForms.Application.Common.Persistence;
using EnterpriseForms.Application.Features.Audit;
using EnterpriseForms.Contracts.Features;
using EnterpriseForms.Domain.Features.Audit;
using EnterpriseForms.Domain.Features.FormDefinitions;

namespace EnterpriseForms.Application.Features.FormDesigner.OptionLists;

/// <summary>
/// Shared option lists (Phase C2). A list is an organization-wide template for dropdown choices: designers create and
/// edit lists, then bind a list to a new dropdown field, which copies the items into the field's own options (stable
/// ids). Editing a list therefore never changes a published version or a stored record; a field pulls list changes in
/// explicitly (<see cref="FormDesignerHandlers.SyncOptionsFromListAsync"/>). Requires the organization-level design
/// permission; lists never contain record data.
/// </summary>
public sealed partial class OptionListHandlers(IBusinessStore store, IAuthorizationAuthorityClient authority, TimeProvider clock)
{
    [GeneratedRegex("^[a-z][a-z0-9_]{0,63}$")]
    private static partial Regex KeyPattern();

    public async Task<IReadOnlyList<OptionListResponse>> ListAsync(RequestContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        await RequireDesignerAsync(context, cancellationToken).ConfigureAwait(false);
        await using var scope = await store.BeginScopeAsync(cancellationToken).ConfigureAwait(false);
        var lists = await scope.GetOptionListsAsync(context.OrganizationId, cancellationToken).ConfigureAwait(false);
        var items = (await scope.GetOptionListItemsAsync(lists.Select(l => l.OptionListId).ToList(), cancellationToken).ConfigureAwait(false)).GroupBy(i => i.OptionListId).ToDictionary(g => g.Key, g => g.ToList());
        var bound = await scope.CountFieldsPerOptionListAsync(lists.Select(l => l.OptionListId).ToList(), cancellationToken).ConfigureAwait(false);
        return lists.Select(l => ToResponse(l, items.GetValueOrDefault(l.OptionListId) ?? [], bound.GetValueOrDefault(l.OptionListId))).ToList();
    }

    public async Task<OptionListResponse> CreateAsync(RequestContext context, CreateOptionListRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(request);
        await RequireDesignerAsync(context, cancellationToken).ConfigureAwait(false);
        var name = request.OptionListName?.Trim() ?? string.Empty;
        var description = request.OptionListDescription?.Trim() ?? string.Empty;
        var requested = request.Items ?? [];
        if (name.Length is 0 or > 200 || name.Contains('<', StringComparison.Ordinal) || description.Length > 1000 || description.Contains('<', StringComparison.Ordinal))
        {
            throw new BusinessException("INVALID_REQUEST", 422, "an option list needs a name (max 200) and an optional description (max 1000) without markup");
        }

        if (requested.Count is 0 or > 500 || requested.Select(o => o.OptionKey).Distinct(StringComparer.Ordinal).Count() != requested.Count
            || requested.Any(o => !KeyPattern().IsMatch(o.OptionKey ?? string.Empty) || string.IsNullOrWhiteSpace(o.OptionDisplayText) || o.OptionDisplayText.Length > 200 || o.OptionDisplayText.Contains('<', StringComparison.Ordinal)))
        {
            throw new BusinessException("INVALID_REQUEST", 422, "an option list needs 1..500 items with unique snake_case keys and display text (max 200, no markup)");
        }

        var now = clock.GetUtcNow();
        await using var scope = await store.BeginScopeAsync(cancellationToken).ConfigureAwait(false);
        if ((await scope.GetOptionListsAsync(context.OrganizationId, cancellationToken).ConfigureAwait(false)).Any(l => string.Equals(l.OptionListName, name, StringComparison.OrdinalIgnoreCase)))
        {
            throw new BusinessException("OPTION_LIST_EXISTS", 409, "an option list with that name already exists");
        }

        var list = new OptionList { OptionListId = Guid.CreateVersion7(), TenantId = context.OrganizationId, OptionListName = name, OptionListDescription = description, LifecycleStatus = "Active", OptionListRevision = 1, CreatedOn = now, CreatedByUserId = context.LocalUserId, ModifiedOn = now, ModifiedByUserId = context.LocalUserId };
        scope.AddOptionList(list);
        var items = new List<OptionListItem>();
        var sort = 0;
        foreach (var r in requested)
        {
            var item = new OptionListItem { OptionListItemId = Guid.CreateVersion7(), OptionListId = list.OptionListId, OptionKey = r.OptionKey!, OptionDisplayText = r.OptionDisplayText.Trim(), OptionSortOrder = sort++, IsActive = true, CreatedOn = now, CreatedByUserId = context.LocalUserId, ModifiedOn = now, ModifiedByUserId = context.LocalUserId };
            scope.AddOptionListItem(item);
            items.Add(item);
        }

        AuditRecorder.Record(scope, context, clock, AuditCategories.Form, AuditActions.OptionListChanged, "shared option list created", targetEntityType: "option_list", targetEntityId: list.OptionListId, metadata: new { items = items.Count });
        await scope.CommitAsync(cancellationToken).ConfigureAwait(false);
        return ToResponse(list, items, 0);
    }

    public async Task<OptionListResponse> UpdateAsync(RequestContext context, Guid optionListId, UpdateOptionListRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(request);
        await RequireDesignerAsync(context, cancellationToken).ConfigureAwait(false);
        var now = clock.GetUtcNow();
        await using var scope = await store.BeginScopeAsync(cancellationToken).ConfigureAwait(false);
        var list = await scope.FindOptionListAsync(context.OrganizationId, optionListId, cancellationToken).ConfigureAwait(false) ?? throw new BusinessException("NOT_FOUND", 404, "option list not found");
        if (request.ExpectedOptionListRevision != list.OptionListRevision)
        {
            throw new BusinessException("OPTION_LIST_REVISION_CONFLICT", 409, "the option list was changed by someone else; reload and retry");
        }

        var changes = request.Changes ?? [];
        if (changes.Count > 500 || changes.GroupBy(c => c.OptionKey, StringComparer.Ordinal).Any(g => g.Count() > 1))
        {
            throw new BusinessException("INVALID_REQUEST", 422, "at most 500 changes, one per option key");
        }

        if (request.OptionListName is not null)
        {
            var name = request.OptionListName.Trim();
            if (name.Length is 0 or > 200 || name.Contains('<', StringComparison.Ordinal))
            {
                throw new BusinessException("INVALID_REQUEST", 422, "the list name must be 1..200 characters without markup");
            }

            if ((await scope.GetOptionListsAsync(context.OrganizationId, cancellationToken).ConfigureAwait(false)).Any(l => l.OptionListId != optionListId && string.Equals(l.OptionListName, name, StringComparison.OrdinalIgnoreCase)))
            {
                throw new BusinessException("OPTION_LIST_EXISTS", 409, "an option list with that name already exists");
            }

            list.OptionListName = name;
        }

        if (request.OptionListDescription is not null)
        {
            var description = request.OptionListDescription.Trim();
            if (description.Length > 1000 || description.Contains('<', StringComparison.Ordinal))
            {
                throw new BusinessException("INVALID_REQUEST", 422, "the description must be at most 1000 characters without markup");
            }

            list.OptionListDescription = description;
        }

        var items = await scope.GetOptionListItemsAsync(optionListId, cancellationToken).ConfigureAwait(false);
        var added = new List<string>();
        var deactivated = new List<string>();
        foreach (var change in changes)
        {
            if (!KeyPattern().IsMatch(change.OptionKey ?? string.Empty) || string.IsNullOrWhiteSpace(change.OptionDisplayText) || change.OptionDisplayText.Length > 200 || change.OptionDisplayText.Contains('<', StringComparison.Ordinal) || change.OptionSortOrder is < 0 or > 100_000)
            {
                throw new BusinessException("INVALID_REQUEST", 422, "item key must be snake_case, display text 1..200 chars without markup and sort order 0..100000");
            }

            var item = change.OptionListItemId is { } id ? items.FirstOrDefault(i => i.OptionListItemId == id) : null;
            if (item is null)
            {
                if (items.Any(i => i.OptionKey == change.OptionKey))
                {
                    throw new BusinessException("INVALID_REQUEST", 422, $"item key '{change.OptionKey}' already exists (keys are immutable)");
                }

                if (items.Count >= 500)
                {
                    throw new BusinessException("LIMIT_EXCEEDED", 422, "a list may have at most 500 items");
                }

                item = new OptionListItem { OptionListItemId = Guid.CreateVersion7(), OptionListId = optionListId, OptionKey = change.OptionKey!, CreatedOn = now, CreatedByUserId = context.LocalUserId };
                scope.AddOptionListItem(item);
                items.Add(item);
                added.Add(item.OptionKey);
            }
            else if (item.OptionKey != change.OptionKey)
            {
                throw new BusinessException("INVALID_REQUEST", 422, "item keys are immutable; deactivate and add a new item instead");
            }
            else if (item.IsActive && !change.IsActive)
            {
                deactivated.Add(item.OptionKey);
            }

            item.OptionDisplayText = change.OptionDisplayText.Trim();
            item.OptionSortOrder = change.OptionSortOrder;
            item.IsActive = change.IsActive;
            item.ModifiedOn = now;
            item.ModifiedByUserId = context.LocalUserId;
        }

        list.OptionListRevision++;
        list.ModifiedOn = now;
        list.ModifiedByUserId = context.LocalUserId;
        AuditRecorder.Record(scope, context, clock, AuditCategories.Form, AuditActions.OptionListChanged, "shared option list changed (fields keep their own copies until they sync; published versions are unaffected)", targetEntityType: "option_list", targetEntityId: list.OptionListId, metadata: new { revision = list.OptionListRevision, changes = changes.Count, added, deactivated });
        await scope.CommitAsync(cancellationToken).ConfigureAwait(false);
        var bound = await scope.CountFieldsPerOptionListAsync([optionListId], cancellationToken).ConfigureAwait(false);
        return ToResponse(list, items, bound.GetValueOrDefault(optionListId));
    }

    private async Task RequireDesignerAsync(RequestContext context, CancellationToken cancellationToken)
    {
        var decision = await authority.CheckPermissionAsync(context.ToDelegatedUser(), new PermissionCheckRequest(context.OrganizationId, new ResourceReference(ProtectedResourceKind.Organization, context.OrganizationId), ProtectedAction.DesignForm), cancellationToken).ConfigureAwait(false);
        if (!decision.Allowed)
        {
            throw new BusinessException("FORBIDDEN", 403, "designer role required");
        }
    }

    private static OptionListResponse ToResponse(OptionList list, IEnumerable<OptionListItem> items, int boundFieldCount) =>
        new(list.OptionListId, list.OptionListName, list.OptionListDescription, list.LifecycleStatus, list.OptionListRevision,
            items.OrderBy(i => i.OptionSortOrder).ThenBy(i => i.OptionKey, StringComparer.Ordinal).Select(i => new OptionListItemResponse(i.OptionListItemId, i.OptionKey, i.OptionDisplayText, i.OptionSortOrder, i.IsActive)).ToList(), boundFieldCount);
}
