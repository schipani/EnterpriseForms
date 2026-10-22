using EnterpriseForms.Application.Common;
using EnterpriseForms.Application.Features.Documents.UploadSessions;
using EnterpriseForms.Application.Features.FormDefinitions.Layout;
using EnterpriseForms.Application.Features.FormRecords.Projection;
using EnterpriseForms.Application.Features.FormRecords.Staging;
using EnterpriseForms.Application.Features.FormRecords.Values;
using EnterpriseForms.Contracts.Features;
using EnterpriseForms.Domain.Features.Documents;
using EnterpriseForms.Domain.Features.FormDefinitions;
using EnterpriseForms.Domain.Features.FormRecords;

namespace EnterpriseForms.Application.Features.FormRecords.SharedEdits;

/// <summary>
/// Phase C5 share-editor write (decisions D2, D3, D6, D9, D15; resolves C5-F2/F3/F4 on the business side). The candidate
/// is the verified current snapshot copied verbatim — header, every value row (sealed ciphertext byte-for-byte, never
/// decrypted), every repeating entry and every document version, each keeping its signed schema version — with exactly
/// the edited items replaced under their stable item ids. Nothing outside the editor's read scope is decrypted. Every
/// supplied key outside the editor's editable projection is refused with one indistinguishable response. The Authority
/// re-decides all of this independently from the canonical bytes; this class is defense in depth, not the boundary.
/// </summary>
public static class SharedFieldWriteBuilder
{
    public const string NotEditableCode = "FIELD_NOT_EDITABLE";
    public const string NotEditableMessage = "this field cannot be changed with your access";

    public sealed record Result(FormRecordRevision Header, List<ISignedContentRow> Rows, List<FieldValueRowBase> EditedRows, IReadOnlyList<string> EditedFieldKeys);

    public static Result Build(VerifiedRecord verified, IReadOnlyDictionary<string, string> values, IReadOnlyList<RecordGroupValuesRequest>? groups, Guid tenantId, Guid formRecordId, DateTimeOffset now, Guid actorUserId)
    {
        ArgumentNullException.ThrowIfNull(verified);
        ArgumentNullException.ThrowIfNull(values);
        if (groups is { Count: > 0 })
        {
            throw NotEditable(null); // repeating entries are owner-only in V1 (C-17, D2)
        }

        var fieldsById = verified.FieldsById;
        var readable = ReadableRows(verified);
        var editableItems = (verified.Verification.EditableItems ?? []).Select(i => i.ItemId).ToHashSet();
        var access = SharedFieldEligibility.Compute(verified.Layout, fieldsById, readable, editableItems);
        var accessByKey = access.Values.ToDictionary(a => a.Field.FieldKey, StringComparer.Ordinal);

        // D9 / C5-F3: every supplied key is checked before any per-field validation; unknown, unshared, read-only and
        // otherwise ineligible keys get the same response, so the endpoint reveals nothing about the form's other fields.
        foreach (var key in values.Keys.OrderBy(k => k, StringComparer.Ordinal))
        {
            if (!accessByKey.TryGetValue(key, out var candidateAccess) || !candidateAccess.Editable)
            {
                throw NotEditable(key);
            }
        }

        var readableWire = new Dictionary<Guid, string>();
        string Read(Guid fieldId)
        {
            // Decrypts only rows the editor is authorized to read (inputs of rules R1/R2 and the edited fields themselves).
            if (!readableWire.TryGetValue(fieldId, out var wire))
            {
                var rows = readable.Where(r => r.FieldDefinitionId == fieldId && r.FormRecordGroupId is null).ToList();
                wire = rows.Count == 0 ? string.Empty : FieldValueCodec.FormatField(fieldsById[fieldId], rows, verified.OptionsById, verified.Encryption);
                readableWire[fieldId] = wire;
            }

            return wire;
        }

        var edited = new Dictionary<Guid, FieldValueRowBase>();
        var editedKeys = new List<string>();
        var after = new Dictionary<Guid, string>();
        foreach (var (key, wire) in values.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            var fieldAccess = accessByKey[key];
            var field = fieldAccess.Field;
            if (wire is null || wire.Length > 4000)
            {
                throw new BusinessException("INVALID_REQUEST", 422, "values must be at most 4000 characters") { FieldKeys = [key] };
            }

            if (wire.Length == 0)
            {
                throw new BusinessException("CLEAR_NOT_PERMITTED", 422, "a shared value can be changed but not cleared; ask the record owner") { FieldKeys = [key] };
            }

            if (string.Equals(wire, Read(field.FieldDefinitionId), StringComparison.Ordinal))
            {
                continue; // unchanged value: carried verbatim, not re-encrypted
            }

            var current = fieldAccess.CurrentRow;
            var typed = FieldValueCodec.CreateRow(field, fieldAccess.Element, verified.Options, wire, tenantId, formRecordId, current.FieldValueItemId, now, actorUserId);
            var protectedRow = FieldValueCodec.Protect(typed, field, verified.OptionsById, verified.Encryption);
            if (protectedRow.EntityKind != current.EntityKind)
            {
                throw new BusinessException("FIELD_PROTECTION_MIGRATION_PENDING", 409, "this field's protection is being changed; try again later") { FieldKeys = [key] };
            }

            protectedRow.FormRecordGroupId = null;
            edited[current.FieldValueItemId] = protectedRow;
            editedKeys.Add(key);
            after[field.FieldDefinitionId] = FieldValueCodec.Format(typed, verified.OptionsById);
        }

        if (edited.Count == 0)
        {
            throw new BusinessException("NO_CHANGE", 422, "nothing to change: every supplied value equals the stored value");
        }

        EnforceRules(verified, access, edited, after, Read);

        // Candidate: verbatim copies (new physical row ids, same signed schema version) + the edited rows.
        var header = CandidateBuilder.CloneHeader(verified.Header);
        header.RowSignatureSchemaVersion = verified.Header.RowSignatureSchemaVersion;
        var rows = new List<ISignedContentRow>(verified.AllRows.Count);
        foreach (var row in verified.AllRows)
        {
            rows.Add(row switch
            {
                FieldValueRowBase value when edited.TryGetValue(value.FieldValueItemId, out var replacement) && value.FormRecordGroupId is null => replacement,
                FieldValueRowBase value => KeepSchema(UploadSessionHandlers.CloneValue(value), value),
                FormRecordGroupRevision occurrence => KeepSchema(CandidateBuilder.CloneOccurrence(occurrence), occurrence),
                FileAttachmentVersion attachment => KeepSchema(CandidateBuilder.CloneAttachment(attachment), attachment),
                _ => throw new InvalidOperationException("unsupported signed row type " + row.GetType().Name), // fail closed
            });
        }

        CandidateBuilder.EnsureWithinCeiling(rows.Count + 1);
        return new Result(header, rows, edited.Values.ToList(), editedKeys);
    }

    /// <summary>Top-level value rows the Authority reported readable for this editor (D7), from the verified snapshot.</summary>
    public static List<FieldValueRowBase> ReadableRows(VerifiedRecord verified)
    {
        ArgumentNullException.ThrowIfNull(verified);
        var readableKeys = (verified.Verification.ReadableItems ?? []).Select(i => ((byte)i.ItemKind, i.ItemId)).ToHashSet();
        return verified.AllRows.OfType<FieldValueRowBase>().Where(r => r.FormRecordGroupId is null && readableKeys.Contains((r.ItemKindCode, r.ItemId))).ToList();
    }

    /// <summary>R1/R2 evaluated over readable values only (the static eligibility guarantees every input is readable).</summary>
    private static void EnforceRules(VerifiedRecord verified, IReadOnlyDictionary<Guid, SharedFieldAccess> access, Dictionary<Guid, FieldValueRowBase> edited, Dictionary<Guid, string> after, Func<Guid, string> read)
    {
        var fieldsById = verified.FieldsById;
        var elements = verified.Layout.Elements.Where(e => e.Kind == LayoutElementKinds.Field && e.FieldDefinitionId is not null).GroupBy(e => e.FieldDefinitionId!.Value).ToDictionary(g => g.Key, g => g.First());
        var editedFields = edited.Values.Select(r => r.FieldDefinitionId).ToHashSet();
        var involved = new HashSet<Guid>(editedFields);
        foreach (var fieldId in editedFields)
        {
            involved.UnionWith(SharedFieldEligibility.Closure(fieldId, elements));
            foreach (var (dependent, _) in elements.Where(e => SharedFieldEligibility.Closure(e.Key, elements).Contains(fieldId)))
            {
                involved.Add(dependent);
                involved.UnionWith(SharedFieldEligibility.Closure(dependent, elements));
            }
        }

        var wires = new Dictionary<Guid, string>();
        foreach (var fieldId in involved.Where(access.ContainsKey))
        {
            wires[fieldId] = after.TryGetValue(fieldId, out var changed) ? changed : read(fieldId);
        }

        var states = FormRules.Evaluate(verified.Layout, fieldsById, wires);
        foreach (var fieldId in editedFields)
        {
            var key = fieldsById[fieldId].FieldKey;
            if (states.TryGetValue(fieldId, out var state) && !state.Visible)
            {
                throw new BusinessException("FIELD_HIDDEN_BY_RULE", 422, $"field '{key}' is hidden by a rule and cannot be changed") { FieldKeys = [key] };
            }

            if (elements.GetValueOrDefault(fieldId)?.OptionFilter is { } filter && FormRules.DependentOptionViolation(filter, fieldsById[fieldId], wires.GetValueOrDefault(fieldId) ?? string.Empty, wires.GetValueOrDefault(filter.ParentFieldDefinitionId) ?? string.Empty) is not null)
            {
                throw new BusinessException("DEPENDENT_OPTION_INVALID", 422, $"the selection of '{key}' is not allowed for the chosen parent value") { FieldKeys = [key] };
            }

            foreach (var (dependent, element) in elements.Where(e => e.Key != fieldId && SharedFieldEligibility.Closure(e.Key, elements).Contains(fieldId)))
            {
                var present = wires.GetValueOrDefault(dependent) is { Length: > 0 };
                var dependentState = states.GetValueOrDefault(dependent);
                var violation = dependentState is null
                    || (!dependentState.Visible && element.ClearWhenHidden && present)
                    || (dependentState.Required && !present)
                    || (present && element.OptionFilter is { } childFilter && FormRules.DependentOptionViolation(childFilter, fieldsById[dependent], wires[dependent], wires.GetValueOrDefault(childFilter.ParentFieldDefinitionId) ?? string.Empty) is not null);
                if (violation)
                {
                    // Generic: the editor can read every input here, but the refusal never describes other fields.
                    throw new BusinessException("CHANGE_AFFECTS_OTHER_FIELDS", 422, "this change affects fields you cannot change; ask the record owner") { FieldKeys = [key] };
                }
            }
        }
    }

    private static T KeepSchema<T>(T copy, ISignedContentRow source)
        where T : ISignedContentRow
    {
        copy.RowSignatureSchemaVersion = source.RowSignatureSchemaVersion;
        return copy;
    }

    private static BusinessException NotEditable(string? key) => new(NotEditableCode, 403, NotEditableMessage) { FieldKeys = key is null ? [] : [key] };
}
