using EnterpriseForms.Application.Features.FormDefinitions.Layout;
using EnterpriseForms.Application.Features.FormRecords.Values;
using EnterpriseForms.Domain.Features.FormDefinitions;
using EnterpriseForms.Domain.Features.FormRecords;

namespace EnterpriseForms.Application.Features.FormRecords.SharedEdits;

/// <summary>Why a shared field is view-only for a share editor (never names or describes a field the viewer cannot read).</summary>
public static class SharedEditReasons
{
    public const string ViewOnlyShare = "ViewOnlyShare";
    public const string ReadOnly = "ReadOnly";
    public const string RuleDependency = "RuleDependency";
    public const string Restricted = "Restricted";
    public const string MultiSelect = "MultiSelect";
    public const string FileField = "FileField";
    public const string NotInForm = "NotInForm";
    public const string MultipleValues = "MultipleValues";
    public const string ProtectionMigrationPending = "ProtectionMigrationPending";
}

/// <summary>Eligibility of one readable top-level field for a share editor.</summary>
public sealed record SharedFieldAccess(FieldDefinition Field, LayoutElement? Element, FieldValueRowBase CurrentRow, bool Editable, string? NotEditableReason);

/// <summary>
/// Phase C5 V1 eligibility (decisions D2, D3, D5, D6). A shared field is editable by a share editor only when every one of
/// these holds: the item is in the Authority's edit set; it is a top-level single-item value (not multi-select, not a
/// file, not in a repeating section); it is placed in the edit layout and not Read-only; its classification is not
/// Restricted (until PB-08 step-up); its stored protection matches the field's protection (no pending migration);
/// R1 — every rule or option filter affecting it reads only fields the editor can read (transitively through visibility);
/// R2 — every field whose rules or option filter it drives is itself editable by the editor and has a fully readable rule
/// closure. Computed from the published layout and the verified projection only; the Authority decides independently.
/// </summary>
public static class SharedFieldEligibility
{
    public static IReadOnlyDictionary<Guid, SharedFieldAccess> Compute(FormLayout layout, IReadOnlyDictionary<Guid, FieldDefinition> fieldsById, IReadOnlyList<FieldValueRowBase> readableRows, IReadOnlySet<Guid> editableItemIds)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(fieldsById);
        ArgumentNullException.ThrowIfNull(readableRows);
        ArgumentNullException.ThrowIfNull(editableItemIds);
        var topLevel = readableRows.Where(r => r.FormRecordGroupId is null && fieldsById.ContainsKey(r.FieldDefinitionId)).GroupBy(r => r.FieldDefinitionId).ToDictionary(g => g.Key, g => g.ToList());
        var readableFields = topLevel.Keys.ToHashSet();
        var editableFields = topLevel.Where(p => p.Value.All(r => editableItemIds.Contains(r.FieldValueItemId))).Select(p => p.Key).ToHashSet();
        var elements = layout.Elements.Where(e => e.Kind == LayoutElementKinds.Field && e.FieldDefinitionId is not null).GroupBy(e => e.FieldDefinitionId!.Value).ToDictionary(g => g.Key, g => g.First());
        var closures = elements.Keys.ToDictionary(id => id, id => Closure(id, elements));
        var dependents = elements.Keys.ToDictionary(id => id, id => closures.Where(c => c.Key != id && c.Value.Contains(id)).Select(c => c.Key).ToList());

        var result = new Dictionary<Guid, SharedFieldAccess>();
        foreach (var (fieldId, rows) in topLevel)
        {
            var field = fieldsById[fieldId];
            var element = elements.GetValueOrDefault(fieldId);
            var reason = ReasonFor(field, element, rows, editableFields, readableFields, closures, dependents);
            result[fieldId] = new SharedFieldAccess(field, element, rows[0], reason is null, reason);
        }

        return result;
    }

    /// <summary>Fields whose values decide the visibility, requiredness or allowed options of a field (transitively through the inputs' visibility).</summary>
    public static IReadOnlySet<Guid> Closure(Guid fieldId, IReadOnlyDictionary<Guid, LayoutElement> elements)
    {
        ArgumentNullException.ThrowIfNull(elements);
        var result = new HashSet<Guid>();
        var pending = new Queue<Guid>();
        void Inputs(Guid id, bool includeRequiredAndFilter)
        {
            if (!elements.TryGetValue(id, out var element))
            {
                return;
            }

            var inputs = FormRulesCodec.Comparisons(element.VisibleWhen).Select(c => c.FieldDefinitionId);
            if (includeRequiredAndFilter)
            {
                inputs = inputs.Concat(FormRulesCodec.Comparisons(element.RequiredWhen).Select(c => c.FieldDefinitionId));
                if (element.OptionFilter is { } filter)
                {
                    inputs = inputs.Append(filter.ParentFieldDefinitionId);
                }
            }

            foreach (var input in inputs)
            {
                if (input != fieldId && result.Add(input))
                {
                    pending.Enqueue(input);
                }
            }
        }

        Inputs(fieldId, includeRequiredAndFilter: true);
        while (pending.Count > 0)
        {
            Inputs(pending.Dequeue(), includeRequiredAndFilter: false); // an input's own visibility decides whether its value counts
        }

        return result;
    }

    private static string? ReasonFor(FieldDefinition field, LayoutElement? element, List<FieldValueRowBase> rows, HashSet<Guid> editableFields, HashSet<Guid> readableFields,
        Dictionary<Guid, IReadOnlySet<Guid>> closures, Dictionary<Guid, List<Guid>> dependents)
    {
        if (!editableFields.Contains(field.FieldDefinitionId))
        {
            return SharedEditReasons.ViewOnlyShare;
        }

        if (element is null)
        {
            return SharedEditReasons.NotInForm;
        }

        if (element.ReadOnly)
        {
            return SharedEditReasons.ReadOnly;
        }

        if (string.Equals(field.FieldClassification, "Restricted", StringComparison.Ordinal))
        {
            return SharedEditReasons.Restricted;
        }

        if (field.FieldDataType == FieldDataTypes.File)
        {
            return SharedEditReasons.FileField;
        }

        if (FieldValueCodec.IsMultiSelect(field))
        {
            return SharedEditReasons.MultiSelect;
        }

        if (rows.Count != 1)
        {
            return SharedEditReasons.MultipleValues;
        }

        var encrypted = rows[0] is FieldEncryptedValue;
        if (encrypted != (field.FieldProtectionMode == FieldProtectionModes.ApplicationEncrypted))
        {
            return SharedEditReasons.ProtectionMigrationPending;
        }

        // R1: everything that decides this field's visibility, requiredness or options is readable by the editor.
        if (!closures.TryGetValue(field.FieldDefinitionId, out var closure) || !closure.All(readableFields.Contains))
        {
            return SharedEditReasons.RuleDependency;
        }

        // R2: every field this field drives is editable by the editor and fully evaluable from readable values.
        foreach (var dependent in dependents.GetValueOrDefault(field.FieldDefinitionId) ?? [])
        {
            if (!editableFields.Contains(dependent) || !closures[dependent].All(readableFields.Contains))
            {
                return SharedEditReasons.RuleDependency;
            }
        }

        return null;
    }
}
