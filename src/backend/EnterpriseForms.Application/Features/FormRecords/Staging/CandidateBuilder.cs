using EnterpriseForms.Application.Common;
using EnterpriseForms.Application.Features.FormDefinitions.Layout;
using EnterpriseForms.Application.Features.FormRecords.Values;
using EnterpriseForms.Domain.Features.Documents;
using EnterpriseForms.Domain.Features.FormDefinitions;
using EnterpriseForms.Domain.Features.FormRecords;

namespace EnterpriseForms.Application.Features.FormRecords.Staging;

/// <summary>
/// Builds the full candidate snapshot for a write (v1 policy: every leaf re-signed under the candidate operation).
/// Unchanged items keep their item identity; changed items keep identity with new content; new keys create new items;
/// attachments are carried forward as fresh per-operation copies of the committed version rows.
/// </summary>
public static class CandidateBuilder
{
    public static FormRecordRevision CloneHeader(FormRecordRevision current) => new()
    {
        FormRecordRevisionId = Guid.CreateVersion7(),
        TenantId = current.TenantId,
        FormRecordId = current.FormRecordId,
        FormDefinitionId = current.FormDefinitionId,
        CreatedFormVersionId = current.CreatedFormVersionId,
        CreatedByUserId = current.CreatedByUserId,
        RecordStatus = current.RecordStatus,
        SubmittedOn = current.SubmittedOn,
        ParentFormRecordId = current.ParentFormRecordId,
        FormRelationshipDefinitionId = current.FormRelationshipDefinitionId,
        RelationshipSortOrder = current.RelationshipSortOrder,
    };

    /// <summary>Per-operation copy of a committed or processed attachment version row (same object key and envelope, new physical row).</summary>
    public static FileAttachmentVersion CloneAttachment(FileAttachmentVersion v, string versionState = "Committed") => new()
    {
        FileAttachmentVersionId = Guid.CreateVersion7(),
        TenantId = v.TenantId,
        FormRecordId = v.FormRecordId,
        FileAttachmentId = v.FileAttachmentId,
        FieldDefinitionId = v.FieldDefinitionId,
        VersionNumber = v.VersionNumber,
        OriginalFileName = v.OriginalFileName,
        DetectedContentType = v.DetectedContentType,
        PlaintextSizeBytes = v.PlaintextSizeBytes,
        PlaintextSha256 = v.PlaintextSha256,
        CiphertextSizeBytes = v.CiphertextSizeBytes,
        CiphertextSha256 = v.CiphertextSha256,
        StorageProviderName = v.StorageProviderName,
        StorageObjectKey = v.StorageObjectKey,
        EncryptionAlgorithmName = v.EncryptionAlgorithmName,
        EncryptionKeyReference = v.EncryptionKeyReference,
        EncryptionEnvelopeVersion = v.EncryptionEnvelopeVersion,
        WrappedDataKey = v.WrappedDataKey,
        EncryptionNoncePrefix = v.EncryptionNoncePrefix,
        EncryptionSegmentSizeBytes = v.EncryptionSegmentSizeBytes,
        ScanVerdict = v.ScanVerdict,
        ScannerName = v.ScannerName,
        ScanSignatureName = v.ScanSignatureName,
        ScannedOn = v.ScannedOn,
        UploadedByUserId = v.UploadedByUserId,
        UploadedOn = v.UploadedOn,
        VersionState = versionState,
    };

    /// <summary>
    /// Applies a values dictionary (field key → invariant wire value; empty string = clear) to the current typed rows.
    /// <paramref name="editableItems"/> null = owner (any mutation); otherwise only listed items may change and nothing may be added or removed.
    /// </summary>
    public static List<FieldValueRowBase> ApplyValues(
        IReadOnlyList<FieldValueRowBase> currentValues,
        IReadOnlyDictionary<string, string> values,
        FormLayout layout,
        IReadOnlyList<FieldDefinition> fields,
        IReadOnlyList<FieldOption> options,
        IReadOnlySet<Guid>? editableItems,
        Guid tenantId,
        Guid formRecordId,
        DateTimeOffset now,
        Guid actorUserId,
        IReadOnlyDictionary<Guid, FieldOption> optionsById)
    {
        ArgumentNullException.ThrowIfNull(currentValues);
        ArgumentNullException.ThrowIfNull(values);
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(fields);
        var fieldByKey = fields.ToDictionary(f => f.FieldKey, StringComparer.Ordinal);
        var elementByField = layout.Elements.Where(e => e.FieldDefinitionId is not null).ToDictionary(e => e.FieldDefinitionId!.Value);
        if (values.Keys.Any(k => !fieldByKey.ContainsKey(k)) || values.Values.Any(v => v is null || v.Length > 4000))
        {
            throw new BusinessException("INVALID_REQUEST", 422, "values must reference known field keys (max 4000 chars each)");
        }

        var result = new List<FieldValueRowBase>();
        var byField = currentValues.ToDictionary(v => v.FieldDefinitionId);
        foreach (var existing in currentValues)
        {
            var field = fields.FirstOrDefault(f => f.FieldDefinitionId == existing.FieldDefinitionId);
            if (field is null)
            {
                continue; // field retired from the definition entirely; value no longer carried (never for Stage B data)
            }

            if (!values.TryGetValue(field.FieldKey, out var wire))
            {
                result.Add(FieldValueCodec.CreateRow(field, elementByField.GetValueOrDefault(field.FieldDefinitionId), options, FieldValueCodec.Format(existing, optionsById), tenantId, formRecordId, existing.FieldValueItemId, existing.CreatedOn, existing.CreatedByUserId));
                continue;
            }

            var unchanged = string.Equals(wire, FieldValueCodec.Format(existing, optionsById), StringComparison.Ordinal);
            if (unchanged)
            {
                result.Add(FieldValueCodec.CreateRow(field, elementByField.GetValueOrDefault(field.FieldDefinitionId), options, wire, tenantId, formRecordId, existing.FieldValueItemId, existing.CreatedOn, existing.CreatedByUserId));
                continue;
            }

            if (editableItems is not null && !editableItems.Contains(existing.FieldValueItemId))
            {
                throw new BusinessException("FORBIDDEN", 403, $"field '{field.FieldKey}' is outside the caller's edit scope");
            }

            if (wire.Length == 0)
            {
                if (editableItems is not null)
                {
                    throw new BusinessException("FORBIDDEN", 403, "clearing items requires record ownership");
                }

                continue; // explicit clear: missing value means no row (CLAUDE.md §2.4)
            }

            result.Add(FieldValueCodec.CreateRow(field, elementByField.GetValueOrDefault(field.FieldDefinitionId), options, wire, tenantId, formRecordId, existing.FieldValueItemId, now, actorUserId));
        }

        foreach (var (key, wire) in values.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            var field = fieldByKey[key];
            if (byField.ContainsKey(field.FieldDefinitionId) || wire.Length == 0)
            {
                continue;
            }

            if (editableItems is not null)
            {
                throw new BusinessException("FORBIDDEN", 403, "adding items requires record ownership");
            }

            result.Add(FieldValueCodec.CreateRow(field, elementByField.GetValueOrDefault(field.FieldDefinitionId), options, wire, tenantId, formRecordId, Guid.CreateVersion7(), now, actorUserId));
        }

        if (editableItems is null)
        {
            FieldValueCodec.RequireMandatoryFields(layout, fields.ToDictionary(f => f.FieldDefinitionId), result.Select(r => r.FieldDefinitionId).ToHashSet());
        }

        return result;
    }
}
