using System.Globalization;
using System.Text.Json;
using EnterpriseForms.Application.Common;
using EnterpriseForms.Application.Features.FormRecords.Values;
using EnterpriseForms.Domain.Features.FormDefinitions;

namespace EnterpriseForms.Application.Features.FormDefinitions.Layout;

/// <summary>
/// Declarative conditional rules (CLAUDE.md §4 "Rule engine", Phase C2): a JSON condition tree over stable field ids with
/// an allow-listed operator set. No expressions, scripts, SQL or URLs exist in the grammar. The same tree is evaluated by
/// the server (authoritative, on every write) and by the form renderer (immediate feedback); the shared vectors in
/// <c>rule-vectors.json</c> keep both implementations identical.
/// </summary>
public abstract record RuleCondition;

public sealed record RuleGroup(string Operator, IReadOnlyList<RuleCondition> Conditions) : RuleCondition;

public sealed record RuleComparison(string Operator, Guid FieldDefinitionId, string? Value, IReadOnlyList<string>? Values) : RuleCondition;

public static class RuleOperators
{
    public const string And = "and";
    public const string Or = "or";
    public const string Not = "not";
    public const string Equal = "equals";
    public const string NotEqual = "notEquals";
    public const string Contains = "contains";
    public const string GreaterThan = "greaterThan";
    public const string LessThan = "lessThan";
    public const string In = "in";
    public const string NotIn = "notIn";
    public const string IsEmpty = "isEmpty";
    public const string IsNotEmpty = "isNotEmpty";

    public static readonly IReadOnlySet<string> Groups = new HashSet<string>(StringComparer.Ordinal) { And, Or, Not };
    public static readonly IReadOnlySet<string> ValueOperators = new HashSet<string>(StringComparer.Ordinal) { Equal, NotEqual, Contains, GreaterThan, LessThan };
    public static readonly IReadOnlySet<string> SetOperators = new HashSet<string>(StringComparer.Ordinal) { In, NotIn };
    public static readonly IReadOnlySet<string> PresenceOperators = new HashSet<string>(StringComparer.Ordinal) { IsEmpty, IsNotEmpty };
}

public static class FormRulesCodec
{
    public const int MaxDepth = 4;
    public const int MaxNodes = 25;
    public const int MaxChildren = 10;
    public const int MaxSetValues = 50;
    public const int MaxValueLength = 200;

    public static RuleCondition Parse(JsonElement element)
    {
        var nodes = 0;
        return ParseNode(element, 1, ref nodes);
    }

    private static RuleCondition ParseNode(JsonElement element, int depth, ref int nodes)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw Invalid("a rule condition must be an object");
        }

        if (depth > MaxDepth)
        {
            throw Invalid($"rule conditions may be nested at most {MaxDepth} levels deep");
        }

        if (++nodes > MaxNodes)
        {
            throw Invalid($"a rule may contain at most {MaxNodes} conditions");
        }

        var op = element.TryGetProperty("op", out var opElement) && opElement.ValueKind == JsonValueKind.String ? opElement.GetString()! : string.Empty;
        if (RuleOperators.Groups.Contains(op))
        {
            if (!element.TryGetProperty("conditions", out var children) || children.ValueKind != JsonValueKind.Array)
            {
                throw Invalid($"'{op}' needs a conditions array");
            }

            var count = children.GetArrayLength();
            if (count is 0 or > MaxChildren || (op == RuleOperators.Not && count != 1))
            {
                throw Invalid(op == RuleOperators.Not ? "'not' takes exactly one condition" : $"'{op}' takes 1 to {MaxChildren} conditions");
            }

            var list = new List<RuleCondition>(count);
            foreach (var child in children.EnumerateArray())
            {
                list.Add(ParseNode(child, depth + 1, ref nodes));
            }

            return new RuleGroup(op, list);
        }

        if (!RuleOperators.ValueOperators.Contains(op) && !RuleOperators.SetOperators.Contains(op) && !RuleOperators.PresenceOperators.Contains(op))
        {
            throw Invalid($"unknown rule operator '{op}'");
        }

        if (!element.TryGetProperty("fieldDefinitionId", out var idElement) || idElement.ValueKind != JsonValueKind.String || !Guid.TryParse(idElement.GetString(), out var fieldId) || fieldId == Guid.Empty)
        {
            throw Invalid("a rule comparison needs the fieldDefinitionId of the field it reads");
        }

        string? value = null;
        List<string>? values = null;
        if (RuleOperators.ValueOperators.Contains(op))
        {
            value = ReadValue(element, "value");
        }
        else if (RuleOperators.SetOperators.Contains(op))
        {
            if (!element.TryGetProperty("values", out var valuesElement) || valuesElement.ValueKind != JsonValueKind.Array || valuesElement.GetArrayLength() is 0 or > MaxSetValues)
            {
                throw Invalid($"'{op}' needs 1 to {MaxSetValues} values");
            }

            values = [];
            foreach (var item in valuesElement.EnumerateArray())
            {
                values.Add(ReadScalar(item, "values"));
            }
        }

        return new RuleComparison(op, fieldId, value, values);
    }

    private static string ReadValue(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) ? ReadScalar(value, name) : throw Invalid($"the rule needs a '{name}'");

    private static string ReadScalar(JsonElement value, string name)
    {
        if (value.ValueKind != JsonValueKind.String)
        {
            throw Invalid($"rule {name} must be strings (numbers, dates and booleans use their invariant text form)");
        }

        var text = value.GetString()!;
        if (text.Length > MaxValueLength || text.Contains('<', StringComparison.Ordinal))
        {
            throw Invalid($"rule {name} must be plain text of at most {MaxValueLength} characters");
        }

        return text;
    }

    /// <summary>Plain object graph for deterministic serialization (null-omitted properties).</summary>
    public static object ToJson(RuleCondition condition) => condition switch
    {
        RuleGroup g => new { op = g.Operator, conditions = g.Conditions.Select(ToJson).ToArray() },
        RuleComparison c => new { op = c.Operator, fieldDefinitionId = c.FieldDefinitionId, value = c.Value, values = c.Values },
        _ => throw new InvalidOperationException("unknown rule condition"),
    };

    public static IEnumerable<RuleComparison> Comparisons(RuleCondition? condition)
    {
        switch (condition)
        {
            case RuleComparison c:
                yield return c;
                break;
            case RuleGroup g:
                foreach (var child in g.Conditions)
                {
                    foreach (var c in Comparisons(child))
                    {
                        yield return c;
                    }
                }

                break;
        }
    }

    private static BusinessException Invalid(string message) => new("LAYOUT_INVALID", 422, message);
}

/// <summary>Result of evaluating one field's rules against a value set.</summary>
public sealed record FieldRuleState(bool Visible, bool Required);

public static class FormRules
{
    private static bool IsRuleSource(FieldDefinition field) => field.FieldDataType != FieldDataTypes.File;

    /// <summary>Operators that make sense for a storage type (and, for options, the selection mode).</summary>
    public static IReadOnlySet<string> AllowedOperators(FieldDefinition field)
    {
        var set = new HashSet<string>(RuleOperators.PresenceOperators, StringComparer.Ordinal);
        switch (field.FieldDataType)
        {
            case FieldDataTypes.String:
                set.UnionWith([RuleOperators.Equal, RuleOperators.NotEqual, RuleOperators.Contains, RuleOperators.In, RuleOperators.NotIn]);
                break;
            case FieldDataTypes.Integer:
            case FieldDataTypes.Decimal:
            case FieldDataTypes.Date:
            case FieldDataTypes.DateTime:
                set.UnionWith([RuleOperators.Equal, RuleOperators.NotEqual, RuleOperators.GreaterThan, RuleOperators.LessThan]);
                break;
            case FieldDataTypes.Boolean:
                set.UnionWith([RuleOperators.Equal, RuleOperators.NotEqual]);
                set.Remove(RuleOperators.IsEmpty);
                set.Remove(RuleOperators.IsNotEmpty);
                break;
            case FieldDataTypes.Guid:
                set.UnionWith([RuleOperators.Equal, RuleOperators.NotEqual, RuleOperators.In, RuleOperators.NotIn]);
                break;
            case FieldDataTypes.Option:
                set.UnionWith([RuleOperators.Equal, RuleOperators.NotEqual, RuleOperators.In, RuleOperators.NotIn]);
                if (field.OptionSelectionMode == OptionSelectionModes.Multiple)
                {
                    set.Add(RuleOperators.Contains);
                }

                break;
        }

        return set;
    }

    /// <summary>
    /// Design-time validation of every rule and behaviour flag of a layout: references resolve to placed fields of this
    /// form, operators and values fit the source type, no field depends on itself (directly or through a chain), and the
    /// behaviour flags are consistent. Throws LAYOUT_INVALID with an explanation the designer can act on.
    /// </summary>
    public static void ValidateLayout(FormLayout layout, IReadOnlyDictionary<Guid, FieldDefinition> fieldsById, IReadOnlyDictionary<Guid, List<FieldOption>> optionsByField, IReadOnlySet<Guid>? groupDefinitionIds = null)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(fieldsById);
        ArgumentNullException.ThrowIfNull(optionsByField);
        ValidateGroups(layout, fieldsById, groupDefinitionIds ?? new HashSet<Guid>());
        var placedFields = layout.Elements.Where(e => e.Kind == LayoutElementKinds.Field && e.FieldDefinitionId is not null).ToDictionary(e => e.FieldDefinitionId!.Value);
        var dependencies = new Dictionary<Guid, HashSet<Guid>>();
        foreach (var element in layout.Elements)
        {
            var hasBehaviour = element.VisibleWhen is not null || element.RequiredWhen is not null || element.ReadOnly || !element.ShowInForm || element.ClearWhenHidden;
            if (element.Kind != LayoutElementKinds.Field)
            {
                if (hasBehaviour)
                {
                    throw Invalid("rules, Read-only and Show in form apply to fields only");
                }

                continue;
            }

            var field = fieldsById[element.FieldDefinitionId!.Value];
            var name = field.FieldKey;
            if (element.ClearWhenHidden && element.VisibleWhen is null)
            {
                throw Invalid($"'{name}': clear-when-hidden needs a visibility rule");
            }

            if (!element.ShowInForm && (element.Validation.Required || element.RequiredWhen is not null))
            {
                throw Invalid($"'{name}': a field that is not shown in the form cannot be required (the user could never fill it in)");
            }

            if (element.ReadOnly && (element.Validation.Required || element.RequiredWhen is not null) && (field.FieldDataType != FieldDataTypes.Option || element.DefaultOptionKeys is not { Count: > 0 }))
            {
                throw Invalid($"'{name}': a read-only field can be required only if it is a dropdown with a default selection (nobody could otherwise enter the value)");
            }

            var sources = new HashSet<Guid>();
            foreach (var (label, condition) in new[] { ("visibility", element.VisibleWhen), ("required-if", element.RequiredWhen) })
            {
                foreach (var comparison in FormRulesCodec.Comparisons(condition))
                {
                    if (!fieldsById.TryGetValue(comparison.FieldDefinitionId, out var source))
                    {
                        throw Invalid($"'{name}': the {label} rule reads a field that does not belong to this form");
                    }

                    if (!IsRuleSource(source) || !placedFields.ContainsKey(source.FieldDefinitionId))
                    {
                        throw Invalid($"'{name}': the {label} rule reads '{source.FieldKey}', which must be a field placed in this layout (document collections cannot be used)");
                    }

                    if (source.FieldDefinitionId == field.FieldDefinitionId)
                    {
                        throw Invalid($"'{name}': a rule cannot read the field it controls");
                    }

                    if (source.GroupDefinitionId is { } sourceGroup && sourceGroup != field.GroupDefinitionId)
                    {
                        throw Invalid(field.GroupDefinitionId is null
                            ? $"'{name}': a rule of a field outside a repeating section cannot read '{source.FieldKey}', which has one value per entry"
                            : $"'{name}': a rule inside a repeating section can read fields of the same section or fields outside repeating sections, not '{source.FieldKey}'");
                    }

                    if (!AllowedOperators(source).Contains(comparison.Operator))
                    {
                        throw Invalid($"'{name}': operator '{comparison.Operator}' cannot be used on '{source.FieldKey}' ({source.FieldDataType})");
                    }

                    ValidateOperands(name, source, comparison, optionsByField.GetValueOrDefault(source.FieldDefinitionId) ?? []);
                    sources.Add(source.FieldDefinitionId);
                }
            }

            if (sources.Count > 0)
            {
                dependencies[field.FieldDefinitionId] = sources;
            }
        }

        ValidateOptionFilters(layout, fieldsById, optionsByField, placedFields);

        // Circular dependencies: A reads B, B reads A (or any longer chain) can never be evaluated.
        var state = new Dictionary<Guid, int>();
        foreach (var start in dependencies.Keys)
        {
            Visit(start, [], fieldsById, dependencies, state);
        }
    }

    /// <summary>
    /// Repeating sections (Phase C3): each group is placed by at most one section; that section holds only data fields of the
    /// group; a grouped field is placed only in its group's section and an ungrouped field never in one.
    /// </summary>
    private static void ValidateGroups(FormLayout layout, IReadOnlyDictionary<Guid, FieldDefinition> fieldsById, IReadOnlySet<Guid> groupDefinitionIds)
    {
        var placedGroups = new HashSet<Guid>();
        foreach (var section in layout.Sections)
        {
            var group = section.Repeat?.GroupDefinitionId;
            if (group is { } g)
            {
                if (!groupDefinitionIds.Contains(g))
                {
                    throw Invalid($"section '{section.Title}': the repeating group does not belong to this form");
                }

                if (!placedGroups.Add(g))
                {
                    throw Invalid("a repeating group may be placed by one section only");
                }
            }

            foreach (var element in section.Elements)
            {
                if (group is not null && element.Kind != LayoutElementKinds.Field)
                {
                    throw Invalid($"section '{section.Title}' allows multiple entries; document collections and related-form lists cannot be placed in it");
                }

                if (element.FieldDefinitionId is not { } fieldId || !fieldsById.TryGetValue(fieldId, out var field))
                {
                    continue;
                }

                if (field.GroupDefinitionId != group)
                {
                    throw Invalid(field.GroupDefinitionId is null
                        ? $"'{field.FieldKey}' has one value per record and cannot be placed in the repeating section '{section.Title}'; create the field inside that section instead"
                        : $"'{field.FieldKey}' belongs to a repeating section and can only be placed in that section");
                }
            }
        }
    }

    /// <summary>
    /// Dependent dropdowns (Phase C3): the parent is a placed dropdown in the same scope (same repeating section, or outside
    /// repeating sections), every mapped key exists in the two option catalogs, and no chain of filters loops back.
    /// </summary>
    private static void ValidateOptionFilters(FormLayout layout, IReadOnlyDictionary<Guid, FieldDefinition> fieldsById, IReadOnlyDictionary<Guid, List<FieldOption>> optionsByField, Dictionary<Guid, LayoutElement> placedFields)
    {
        var parents = new Dictionary<Guid, Guid>();
        foreach (var element in layout.Elements.Where(e => e.OptionFilter is not null))
        {
            var filter = element.OptionFilter!;
            if (element.Kind != LayoutElementKinds.Field || element.FieldDefinitionId is not { } childId || !fieldsById.TryGetValue(childId, out var child) || child.FieldDataType != FieldDataTypes.Option)
            {
                throw Invalid("only dropdowns can filter their choices by another dropdown");
            }

            if (filter.ParentFieldDefinitionId == childId)
            {
                throw Invalid($"'{child.FieldKey}' cannot filter its choices by itself");
            }

            if (!fieldsById.TryGetValue(filter.ParentFieldDefinitionId, out var parent) || parent.FieldDataType != FieldDataTypes.Option || !placedFields.ContainsKey(parent.FieldDefinitionId))
            {
                throw Invalid($"'{child.FieldKey}': the field its choices depend on must be a dropdown placed in this layout");
            }

            if (parent.GroupDefinitionId is { } parentGroup && parentGroup != child.GroupDefinitionId)
            {
                throw Invalid($"'{child.FieldKey}': it can depend on a dropdown of the same repeating section or outside repeating sections, not on '{parent.FieldKey}'");
            }

            var childKeys = (optionsByField.GetValueOrDefault(childId) ?? []).Select(o => o.OptionKey).ToHashSet(StringComparer.Ordinal);
            var parentKeys = (optionsByField.GetValueOrDefault(parent.FieldDefinitionId) ?? []).Select(o => o.OptionKey).ToHashSet(StringComparer.Ordinal);
            foreach (var (childKey, mapped) in filter.ParentOptionKeysByOption)
            {
                if (!childKeys.Contains(childKey))
                {
                    throw Invalid($"'{child.FieldKey}': '{childKey}' is not an option of this dropdown");
                }

                var unknown = mapped.FirstOrDefault(k => !parentKeys.Contains(k));
                if (unknown is not null)
                {
                    throw Invalid($"'{child.FieldKey}': '{unknown}' is not an option of '{parent.FieldKey}'");
                }
            }

            parents[childId] = parent.FieldDefinitionId;
        }

        foreach (var start in parents.Keys)
        {
            var seen = new List<Guid> { start };
            var current = start;
            while (parents.TryGetValue(current, out var next))
            {
                if (seen.Contains(next))
                {
                    throw Invalid("circular dropdown dependency: " + string.Join(" → ", seen.Append(next).Select(id => fieldsById[id].FieldKey)));
                }

                seen.Add(next);
                current = next;
            }
        }
    }

    /// <summary>
    /// The first selected option key of a dependent dropdown that is not valid for the parent's current selection, or null.
    /// A child option without a mapping is valid for any parent value; a mapped option needs one of its parents selected.
    /// </summary>
    public static string? DependentOptionViolation(LayoutOptionFilter filter, FieldDefinition child, string childWire, string parentWire)
    {
        ArgumentNullException.ThrowIfNull(filter);
        ArgumentNullException.ThrowIfNull(child);
        var parentKeys = SplitSelection(parentWire ?? string.Empty).ToHashSet(StringComparer.Ordinal);
        var childKeys = child.OptionSelectionMode == OptionSelectionModes.Multiple ? SplitSelection(childWire ?? string.Empty) : string.IsNullOrEmpty(childWire) ? [] : [childWire];
        return childKeys.FirstOrDefault(key => filter.ParentOptionKeysByOption.TryGetValue(key, out var allowed) && !allowed.Any(parentKeys.Contains));
    }

    private static void Visit(Guid node, List<Guid> path, IReadOnlyDictionary<Guid, FieldDefinition> fieldsById, Dictionary<Guid, HashSet<Guid>> dependencies, Dictionary<Guid, int> state)
    {
        if (state.GetValueOrDefault(node) == 2)
        {
            return;
        }

        if (state.GetValueOrDefault(node) == 1)
        {
            var cycleStart = path.IndexOf(node);
            var cycle = path.Skip(cycleStart).Append(node).Select(id => fieldsById[id].FieldKey);
            throw Invalid("circular rule dependency: " + string.Join(" → ", cycle));
        }

        state[node] = 1;
        path.Add(node);
        if (dependencies.TryGetValue(node, out var sources))
        {
            foreach (var source in sources.OrderBy(id => fieldsById[id].FieldKey, StringComparer.Ordinal))
            {
                Visit(source, path, fieldsById, dependencies, state);
            }
        }

        path.RemoveAt(path.Count - 1);
        state[node] = 2;
    }

    private static void ValidateOperands(string target, FieldDefinition source, RuleComparison comparison, List<FieldOption> options)
    {
        IEnumerable<string> operands = comparison.Value is not null ? [comparison.Value] : comparison.Values ?? [];
        foreach (var operand in operands)
        {
            var problem = source.FieldDataType switch
            {
                FieldDataTypes.Integer => long.TryParse(operand, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out _) ? null : "a whole number",
                FieldDataTypes.Decimal => decimal.TryParse(operand, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out _) ? null : "a decimal number",
                FieldDataTypes.Date => DateOnly.TryParseExact(operand, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _) ? null : "an ISO date (yyyy-MM-dd)",
                FieldDataTypes.DateTime => DateTimeOffset.TryParseExact(operand, ["yyyy-MM-dd'T'HH:mm:ssK", "yyyy-MM-dd'T'HH:mmK"], CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out _) ? null : "an ISO 8601 instant with offset",
                FieldDataTypes.Boolean => operand is "true" or "false" ? null : "true or false",
                FieldDataTypes.Guid => Guid.TryParseExact(operand, "D", out _) ? null : "a UUID",
                FieldDataTypes.Option => SplitSelection(operand).All(k => options.Any(o => o.OptionKey == k)) && SplitSelection(operand).Count > 0 ? null : "the key of an existing option of that field",
                _ => null,
            };
            if (problem is not null)
            {
                throw Invalid($"'{target}': the value '{operand}' compared with '{source.FieldKey}' must be {problem}");
            }
        }
    }

    /// <summary>Wire value of a multi-select field is its option keys joined by comma (keys are snake_case, so the comma is unambiguous).</summary>
    public static List<string> SplitSelection(string wire) =>
        string.IsNullOrEmpty(wire) ? [] : wire.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct(StringComparer.Ordinal).ToList();

    /// <summary>
    /// Evaluates visibility and requiredness of every field element against the given values (field id → wire value, missing
    /// = empty). A field hidden by a rule counts as empty for the rules that read it, so hiding propagates through chains.
    /// </summary>
    public static IReadOnlyDictionary<Guid, FieldRuleState> Evaluate(FormLayout layout, IReadOnlyDictionary<Guid, FieldDefinition> fieldsById, IReadOnlyDictionary<Guid, string> values)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(fieldsById);
        ArgumentNullException.ThrowIfNull(values);
        var elements = layout.Elements.Where(e => e.Kind == LayoutElementKinds.Field && e.FieldDefinitionId is { } id && fieldsById.ContainsKey(id)).ToDictionary(e => e.FieldDefinitionId!.Value);
        var visible = new Dictionary<Guid, bool>();
        var inProgress = new HashSet<Guid>();

        bool IsVisible(Guid id)
        {
            if (visible.TryGetValue(id, out var known))
            {
                return known;
            }

            if (!elements.TryGetValue(id, out var element) || element.VisibleWhen is null)
            {
                return visible[id] = true;
            }

            if (!inProgress.Add(id))
            {
                return false; // a cycle cannot be saved; fail closed (hidden) if one ever reaches the evaluator
            }

            var result = Test(element.VisibleWhen, fieldsById, Effective);
            inProgress.Remove(id);
            return visible[id] = result;
        }

        string Effective(Guid id) => IsVisible(id) ? values.GetValueOrDefault(id) ?? string.Empty : string.Empty;

        var result = new Dictionary<Guid, FieldRuleState>();
        foreach (var (id, element) in elements)
        {
            var isVisible = IsVisible(id);
            var required = isVisible && (element.Validation.Required || (element.RequiredWhen is not null && Test(element.RequiredWhen, fieldsById, Effective)));
            result[id] = new FieldRuleState(isVisible, required);
        }

        return result;
    }

    public static bool Test(RuleCondition condition, IReadOnlyDictionary<Guid, FieldDefinition> fieldsById, Func<Guid, string> read)
    {
        ArgumentNullException.ThrowIfNull(condition);
        ArgumentNullException.ThrowIfNull(fieldsById);
        ArgumentNullException.ThrowIfNull(read);
        return condition switch
        {
            RuleGroup { Operator: RuleOperators.And } g => g.Conditions.All(c => Test(c, fieldsById, read)),
            RuleGroup { Operator: RuleOperators.Or } g => g.Conditions.Any(c => Test(c, fieldsById, read)),
            RuleGroup { Operator: RuleOperators.Not } g => !Test(g.Conditions[0], fieldsById, read),
            RuleComparison c when fieldsById.TryGetValue(c.FieldDefinitionId, out var field) => Compare(field, c, read(c.FieldDefinitionId)),
            _ => false,
        };
    }

    private static bool Compare(FieldDefinition field, RuleComparison c, string actual)
    {
        var isEmpty = actual.Length == 0;
        if (c.Operator == RuleOperators.IsEmpty)
        {
            return isEmpty;
        }

        if (c.Operator == RuleOperators.IsNotEmpty)
        {
            return !isEmpty;
        }

        var negative = c.Operator is RuleOperators.NotEqual or RuleOperators.NotIn;
        if (isEmpty && field.FieldDataType != FieldDataTypes.Boolean)
        {
            return negative; // an empty source satisfies only the negative operators
        }

        var multi = field.FieldDataType == FieldDataTypes.Option && field.OptionSelectionMode == OptionSelectionModes.Multiple;
        if (multi)
        {
            var selected = SplitSelection(actual).ToHashSet(StringComparer.Ordinal);
            return c.Operator switch
            {
                RuleOperators.Equal => selected.SetEquals(SplitSelection(c.Value ?? string.Empty)),
                RuleOperators.NotEqual => !selected.SetEquals(SplitSelection(c.Value ?? string.Empty)),
                RuleOperators.Contains => SplitSelection(c.Value ?? string.Empty).All(selected.Contains),
                RuleOperators.In => (c.Values ?? []).Any(selected.Contains),
                RuleOperators.NotIn => !(c.Values ?? []).Any(selected.Contains),
                _ => false,
            };
        }

        switch (c.Operator)
        {
            case RuleOperators.Equal:
                return Compare3(field, actual, c.Value ?? string.Empty) == 0;
            case RuleOperators.NotEqual:
                return Compare3(field, actual, c.Value ?? string.Empty) != 0;
            case RuleOperators.GreaterThan:
                return Compare3(field, actual, c.Value ?? string.Empty) is > 0;
            case RuleOperators.LessThan:
                return Compare3(field, actual, c.Value ?? string.Empty) is < 0;
            case RuleOperators.Contains:
                return actual.Contains(c.Value ?? string.Empty, StringComparison.OrdinalIgnoreCase);
            case RuleOperators.In:
                return (c.Values ?? []).Any(v => Compare3(field, actual, v) == 0);
            case RuleOperators.NotIn:
                return !(c.Values ?? []).Any(v => Compare3(field, actual, v) == 0);
            default:
                return false;
        }
    }

    /// <summary>Three-way comparison in the field's own type; null when either side does not parse (such values never equal and are unordered).</summary>
    private static int? Compare3(FieldDefinition field, string left, string right)
    {
        var a = ParseComparable(field, left);
        var b = ParseComparable(field, right);
        if (a is null || b is null)
        {
            return null;
        }

        return a is string sa && b is string sb ? Math.Sign(string.CompareOrdinal(sa, sb)) : Math.Sign(a.CompareTo(b));
    }

    /// <summary>Orderable key of a wire value: decimal for numbers, day number / UTC ticks for dates and instants, 0/1 for booleans; strings, GUIDs and option keys compare as ordinal text.</summary>
    private static IComparable? ParseComparable(FieldDefinition field, string wire) => field.FieldDataType switch
    {
        FieldDataTypes.Integer or FieldDataTypes.Decimal => decimal.TryParse(wire, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var d) ? d : null,
        FieldDataTypes.Date => DateOnly.TryParseExact(wire, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ? (long)date.DayNumber : null,
        FieldDataTypes.DateTime => DateTimeOffset.TryParseExact(wire, ["yyyy-MM-dd'T'HH:mm:ssK", "yyyy-MM-dd'T'HH:mmK"], CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var instant) ? instant.UtcTicks : null,
        FieldDataTypes.Boolean => wire switch { "true" => 1L, "false" or "" => 0L, _ => null },
        FieldDataTypes.Guid => Guid.TryParseExact(wire, "D", out var guid) ? guid.ToString("D") : null,
        _ => wire,
    };

    private static BusinessException Invalid(string message) => new("LAYOUT_INVALID", 422, message);
}
