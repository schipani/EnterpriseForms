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
        IReadOnlyDictionary<Guid, FieldOption> optionsById,
        Encryption.IFieldEncryptionService encryption,
        RecordWriteOrigin origin = RecordWriteOrigin.User,
        bool creating = false) =>
        ApplyValuesDetailed(currentValues, values, layout, fields, options, editableItems, tenantId, formRecordId, now, actorUserId, optionsById, encryption, origin, creating).Rows;

    /// <summary>Rows of the new candidate plus the keys of fields whose value a visibility rule cleared (explicit opt-in per field; audited by the caller).</summary>
    public sealed record ApplyResult(List<FieldValueRowBase> Rows, IReadOnlyList<string> ClearedByRuleFieldKeys);

    /// <summary>
    /// The write rules of a record, in order: (1) every supplied key names a known field; (2) each changed value validates
    /// against the layout and option catalog and is protected per the field's policy; (3) a read-only field cannot be
    /// changed by a user write (create may only receive its configured default); (4) conditional rules are evaluated on the
    /// resulting values — a field hidden by a rule cannot be changed (its stored value is retained, or cleared only when the
    /// designer opted in with clear-when-hidden), and a visible field that is required (statically or by a required-if rule)
    /// must have a value. Rules and requiredness are enforced for full-scope (owner) writes; a shared contributor writes
    /// only the items its share grants, cannot see the fields a rule may read, and is never told anything about them.
    /// </summary>
    public static ApplyResult ApplyValuesDetailed(
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
        IReadOnlyDictionary<Guid, FieldOption> optionsById,
        Encryption.IFieldEncryptionService encryption,
        RecordWriteOrigin origin = RecordWriteOrigin.User,
        bool creating = false)
    {
        ArgumentNullException.ThrowIfNull(encryption);
        ArgumentNullException.ThrowIfNull(currentValues);
        ArgumentNullException.ThrowIfNull(values);
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(fields);
        var fieldByKey = fields.ToDictionary(f => f.FieldKey, StringComparer.Ordinal);
        var fieldById = fields.ToDictionary(f => f.FieldDefinitionId);
        var elementByField = layout.Elements.Where(e => e.FieldDefinitionId is not null).ToDictionary(e => e.FieldDefinitionId!.Value);
        if (values.Keys.Any(k => !fieldByKey.ContainsKey(k)) || values.Values.Any(v => v is null || v.Length > 4000))
        {
            throw new BusinessException("INVALID_REQUEST", 422, "values must reference known field keys (max 4000 chars each)");
        }

        // A read-only dropdown with a default receives it on create even when the client omitted it: nobody can type it.
        var effective = new Dictionary<string, string>(values, StringComparer.Ordinal);
        if (creating)
        {
            foreach (var element in layout.Elements.Where(e => e is { Kind: LayoutElementKinds.Field, ReadOnly: true, DefaultOptionKeys.Count: > 0 }))
            {
                var field = fieldById.GetValueOrDefault(element.FieldDefinitionId!.Value);
                if (field is { FieldDataType: FieldDataTypes.Option } && (!effective.TryGetValue(field.FieldKey, out var given) || given.Length == 0))
                {
                    var activeDefaults = ActiveDefaults(element, field, options);
                    if (activeDefaults.Count > 0)
                    {
                        effective[field.FieldKey] = string.Join(',', activeDefaults);
                    }
                }
            }
        }

        var result = new List<FieldValueRowBase>();
        var wireParts = new Dictionary<Guid, List<string>>();
        var changed = new HashSet<Guid>();
        var existingFieldIds = currentValues.Select(v => v.FieldDefinitionId).ToHashSet();

        void Emit(FieldDefinition field, FieldValueRowBase typed, string wire) => Add(field, FieldValueCodec.Protect(typed, field, optionsById, encryption), wire);

        void Add(FieldDefinition field, FieldValueRowBase row, string wire)
        {
            result.Add(row);
            if (!wireParts.TryGetValue(field.FieldDefinitionId, out var parts))
            {
                wireParts[field.FieldDefinitionId] = parts = [];
            }

            parts.Add(wire);
        }

        // An unchanged value is carried forward under the field's current protection (a legacy plaintext value of a field
        // strengthened since the last save is re-sealed here), still validated against the current layout and option catalog:
        // a deactivated option or a tightened rule blocks the save.
        void Carry(FieldDefinition field, LayoutElement? element, FieldValueRowBase existing, string wire) =>
            Emit(field, FieldValueCodec.CreateRow(field, element, options, wire, tenantId, formRecordId, existing.FieldValueItemId, existing.CreatedOn, existing.CreatedByUserId), wire);

        foreach (var group in currentValues.GroupBy(v => v.FieldDefinitionId))
        {
            var field = fieldById.GetValueOrDefault(group.Key);
            if (field is null)
            {
                continue; // field retired from the definition entirely; value no longer carried (never for Stage B data)
            }

            var element = elementByField.GetValueOrDefault(field.FieldDefinitionId);
            var rows = group.ToList();
            // Carry-forward re-creates the row under the field's *current* protection: a field strengthened to encryption
            // since the last save rewrites its legacy plaintext as ciphertext here (the audited migration's backfill step).
            var currentWires = rows.Select(r => FieldValueCodec.Format(r, optionsById, encryption)).ToList();
            var hasNew = values.TryGetValue(field.FieldKey, out var wire);

            if (FieldValueCodec.IsMultiSelect(field))
            {
                var currentKeys = currentWires.ToHashSet(StringComparer.Ordinal);
                var newKeys = hasNew ? FormRules.SplitSelection(wire!) : currentKeys.ToList();
                var itemByKey = rows.Zip(currentWires).GroupBy(p => p.Second, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First().First, StringComparer.Ordinal);
                if (hasNew && !currentKeys.SetEquals(newKeys))
                {
                    changed.Add(field.FieldDefinitionId);
                    if (editableItems is not null)
                    {
                        throw new BusinessException("FORBIDDEN", 403, $"changing the selection of '{field.FieldKey}' requires record ownership");
                    }
                }

                foreach (var key in newKeys)
                {
                    var existing = itemByKey.GetValueOrDefault(key);
                    if (existing is not null)
                    {
                        Carry(field, element, existing, key);
                        continue;
                    }

                    Emit(field, FieldValueCodec.CreateRow(field, element, options, key, tenantId, formRecordId, Guid.CreateVersion7(), now, actorUserId), key);
                }

                continue;
            }

            var current = rows[0];
            var currentWire = currentWires[0];
            if (!hasNew)
            {
                Carry(field, element, current, currentWire);
                continue;
            }

            if (string.Equals(wire, currentWire, StringComparison.Ordinal))
            {
                Carry(field, element, current, wire!);
                continue;
            }

            if (editableItems is not null && !editableItems.Contains(current.FieldValueItemId))
            {
                throw new BusinessException("FORBIDDEN", 403, $"field '{field.FieldKey}' is outside the caller's edit scope");
            }

            changed.Add(field.FieldDefinitionId);
            if (wire!.Length == 0)
            {
                if (editableItems is not null)
                {
                    throw new BusinessException("FORBIDDEN", 403, "clearing items requires record ownership");
                }

                continue; // explicit clear: missing value means no row (CLAUDE.md §2.4)
            }

            Emit(field, FieldValueCodec.CreateRow(field, element, options, wire, tenantId, formRecordId, current.FieldValueItemId, now, actorUserId), wire);
        }

        foreach (var (key, wire) in effective.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            var field = fieldByKey[key];
            if (existingFieldIds.Contains(field.FieldDefinitionId) || wire.Length == 0)
            {
                continue;
            }

            if (editableItems is not null)
            {
                throw new BusinessException("FORBIDDEN", 403, "adding items requires record ownership");
            }

            changed.Add(field.FieldDefinitionId);
            var element = elementByField.GetValueOrDefault(field.FieldDefinitionId);
            if (FieldValueCodec.IsMultiSelect(field))
            {
                foreach (var typed in FieldValueCodec.CreateSelectionRows(field, element, options, FormRules.SplitSelection(wire), tenantId, formRecordId, now, actorUserId))
                {
                    Emit(field, typed, FieldValueCodec.Format(typed, optionsById));
                }

                continue;
            }

            Emit(field, FieldValueCodec.CreateRow(field, element, options, wire, tenantId, formRecordId, Guid.CreateVersion7(), now, actorUserId), wire);
        }

        var wireById = wireParts.ToDictionary(p => p.Key, p => FieldValueCodec.IsMultiSelect(fieldById[p.Key]) ? CanonicalSelection(fieldById[p.Key], p.Value, options) : p.Value[0]);

        // Read-only (user writes only): the server, not the form, is what keeps the value fixed.
        if (origin == RecordWriteOrigin.User)
        {
            foreach (var element in layout.Elements.Where(e => e is { Kind: LayoutElementKinds.Field, ReadOnly: true }))
            {
                var field = fieldById.GetValueOrDefault(element.FieldDefinitionId!.Value);
                if (field is null || !changed.Contains(field.FieldDefinitionId))
                {
                    continue;
                }

                var equalsDefault = creating && element.DefaultOptionKeys is { Count: > 0 }
                                    && string.Equals(wireById.GetValueOrDefault(field.FieldDefinitionId) ?? string.Empty, CanonicalSelection(field, ActiveDefaults(element, field, options), options), StringComparison.Ordinal);
                if (!equalsDefault)
                {
                    throw new BusinessException("FIELD_READ_ONLY", 403, $"field '{field.FieldKey}' is read-only and cannot be changed");
                }
            }
        }

        var cleared = new List<string>();
        if (editableItems is null)
        {
            var states = FormRules.Evaluate(layout, fieldById, wireById);
            var missing = new List<string>();
            foreach (var element in layout.Elements.Where(e => e.Kind == LayoutElementKinds.Field && e.FieldDefinitionId is { } id && fieldById.ContainsKey(id)))
            {
                var field = fieldById[element.FieldDefinitionId!.Value];
                var state = states[field.FieldDefinitionId];
                var present = wireById.TryGetValue(field.FieldDefinitionId, out var held) && held.Length > 0;
                if (!state.Visible)
                {
                    if (changed.Contains(field.FieldDefinitionId) && origin == RecordWriteOrigin.User)
                    {
                        throw new BusinessException("FIELD_HIDDEN_BY_RULE", 422, $"field '{field.FieldKey}' is hidden by a rule and cannot be changed");
                    }

                    if (element.ClearWhenHidden && present)
                    {
                        result.RemoveAll(r => r.FieldDefinitionId == field.FieldDefinitionId);
                        wireById.Remove(field.FieldDefinitionId);
                        cleared.Add(field.FieldKey);
                    }
                }
                else if (state.Required && !present)
                {
                    missing.Add(field.FieldKey);
                }
            }

            if (missing.Count > 0)
            {
                throw new BusinessException("VALIDATION_FAILED", 422, "required fields missing: " + string.Join(", ", missing));
            }
        }

        return new ApplyResult(result, cleared);
    }

    private static List<string> ActiveDefaults(LayoutElement element, FieldDefinition field, IReadOnlyList<FieldOption> options) =>
        (element.DefaultOptionKeys ?? []).Where(k => options.Any(o => o.FieldDefinitionId == field.FieldDefinitionId && o.OptionKey == k && o.IsActive)).ToList();

    private static string CanonicalSelection(FieldDefinition field, List<string> keys, IReadOnlyList<FieldOption> options)
    {
        if (!FieldValueCodec.IsMultiSelect(field))
        {
            return keys.Count > 0 ? keys[0] : string.Empty;
        }

        var order = options.Where(o => o.FieldDefinitionId == field.FieldDefinitionId).ToDictionary(o => o.OptionKey, o => o.OptionSortOrder, StringComparer.Ordinal);
        return string.Join(',', keys.Distinct(StringComparer.Ordinal).OrderBy(k => order.GetValueOrDefault(k, int.MaxValue)).ThenBy(k => k, StringComparer.Ordinal));
    }
}

/// <summary>Who is writing: ordinary users are bound by read-only and hidden-field rules; explicitly authorized system operations (e.g. a governed import) are not bound by read-only.</summary>
public enum RecordWriteOrigin
{
    User,
    System,
}
