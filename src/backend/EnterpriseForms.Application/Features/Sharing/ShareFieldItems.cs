using EnterpriseForms.Application.Common;
using EnterpriseForms.Application.Common.Authority;
using EnterpriseForms.Application.Common.Persistence;

namespace EnterpriseForms.Application.Features.Sharing;

/// <summary>
/// The item ids a share of the named fields covers, bound to the record's authority-accepted content (CLAUDE.md §4H): each field
/// key resolves to the persisted value items of the committed operation the Authority reports as current. The Authority verifies
/// every item against its accepted manifest again when it plans the command; nothing here grants access.
/// </summary>
internal static class ShareFieldItems
{
    public sealed record Resolved(AcceptedContentRevision Head, IReadOnlyList<(Guid FieldDefinitionId, Guid ItemId)> Items);

    /// <summary>C10 WP8: the command form of the requested documents (view always; the Authority validates them against verified content).</summary>
    public static System.Text.Json.Nodes.JsonArray? Documents(IReadOnlyList<EnterpriseForms.Contracts.Features.ShareDocumentRequest>? documents) =>
        documents is not { Count: > 0 } ? null : new System.Text.Json.Nodes.JsonArray([.. documents.Select(d => (System.Text.Json.Nodes.JsonNode)new System.Text.Json.Nodes.JsonObject
        {
            ["file_attachment_id"] = d.FileAttachmentId,
            ["can_view_document"] = true,
            ["can_download_document"] = d.CanDownloadDocument,
            ["can_replace_document"] = d.CanReplaceDocument == true,
        })]);

    public static async Task<Resolved> ResolveAsync(IBusinessStore store, IAuthorizationAuthorityClient authority, DelegatedUserContext user, Guid organizationId,
        Guid formRecordId, IReadOnlyList<string> fieldKeys, CancellationToken cancellationToken)
    {
        var head = await authority.GetCommittedContentRevisionAsync(user, organizationId, formRecordId, cancellationToken).ConfigureAwait(false);
        if (head.CommittedOperationId is null)
        {
            throw new BusinessException("NO_COMMITTED_CONTENT", 404, "record has no accepted content");
        }

        List<Domain.Features.FormRecords.FieldValueRowBase> values;
        List<Domain.Features.FormDefinitions.FieldDefinition> fields;
        await using (var scope = await store.BeginScopeAsync(cancellationToken).ConfigureAwait(false))
        {
            var header = await scope.FindStagedHeaderAsync(formRecordId, head.CommittedOperationId.Value, cancellationToken).ConfigureAwait(false)
                         ?? throw new BusinessException("NO_COMMITTED_CONTENT", 404, "committed header missing");
            values = (await scope.GetStagedRowsAsync(formRecordId, head.CommittedOperationId.Value, cancellationToken).ConfigureAwait(false)).OfType<Domain.Features.FormRecords.FieldValueRowBase>().ToList();
            fields = await scope.GetFieldDefinitionsAsync(header.FormDefinitionId, cancellationToken).ConfigureAwait(false);
        }

        var fieldByKey = fields.ToDictionary(f => f.FieldKey, StringComparer.Ordinal);
        var items = new List<(Guid FieldDefinitionId, Guid ItemId)>();
        foreach (var key in fieldKeys.Distinct(StringComparer.Ordinal))
        {
            if (!fieldByKey.TryGetValue(key, out var field))
            {
                throw new BusinessException("INVALID_REQUEST", 422, $"unknown field key '{key}'");
            }

            // Repeating-section values are not field-shareable in V1 (C-17): a share would release values without the entry
            // structure the authority does not authorize for partial reads. Extending shares to entries is an authority change.
            if (field.GroupDefinitionId is not null || values.Any(v => v.FieldDefinitionId == field.FieldDefinitionId && v.FormRecordGroupId is not null))
            {
                throw new BusinessException("FIELD_IN_REPEATING_GROUP", 422, $"field '{key}' belongs to a repeating section and cannot be shared on its own");
            }

            // A multi-select dropdown holds one row (item) per selected option: sharing the field shares every current selection.
            var fieldRows = values.Where(v => v.FieldDefinitionId == field.FieldDefinitionId).ToList();
            if (fieldRows.Count == 0)
            {
                throw new BusinessException("INVALID_REQUEST", 422, $"field '{key}' has no persisted value to share");
            }

            items.AddRange(fieldRows.Select(r => (field.FieldDefinitionId, r.FieldValueItemId)));
        }

        return new Resolved(head, items);
    }
}
