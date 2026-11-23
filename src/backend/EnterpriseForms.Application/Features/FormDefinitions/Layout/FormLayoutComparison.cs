using System.Globalization;

namespace EnterpriseForms.Application.Features.FormDefinitions.Layout;

/// <summary>What a change touches (Phase C9 WP-3).</summary>
public static class LayoutChangeTargets
{
    public const string Form = "Form";
    public const string Section = "Section";
    public const string Field = "Field";
    public const string DocumentCollection = "DocumentCollection";
    public const string RelatedFormList = "RelatedFormList";
}

public static class LayoutChangeTypes
{
    public const string Added = "Added";
    public const string Removed = "Removed";
    public const string Modified = "Modified";
    public const string Moved = "Moved";
}

/// <summary>
/// How much a change matters. <c>Presentation</c>: wording, help, width, position or order only — nothing a person enters is
/// validated differently. <c>Behaviour</c>: validation, rules, read-only, availability of options, limits or protection.
/// <c>Structure</c>: an element is added to or removed from the layout. No category means data is deleted: publishing never
/// deletes or changes stored values.
/// </summary>
public static class LayoutChangeSignificance
{
    public const string Presentation = "Presentation";
    public const string Behaviour = "Behaviour";
    public const string Structure = "Structure";

    public static int Rank(string significance) => significance switch
    {
        Structure => 3,
        Behaviour => 2,
        _ => 1,
    };
}

/// <summary>Stable codes of the changed aspects (the UI words them; the publish impact derives notices from them).</summary>
public static class LayoutChangeAspects
{
    public const string Title = "Title";
    public const string Description = "Description";
    public const string Label = "Label";
    public const string HelpText = "HelpText";
    public const string Placeholder = "Placeholder";
    public const string Control = "Control";
    public const string Placement = "Placement";
    public const string Position = "Position";
    public const string Section = "Section";
    public const string Required = "Required";
    public const string MinLength = "MinLength";
    public const string MaxLength = "MaxLength";
    public const string Pattern = "Pattern";
    public const string Minimum = "Minimum";
    public const string Maximum = "Maximum";
    public const string MinDate = "MinDate";
    public const string MaxDate = "MaxDate";
    public const string ReadOnly = "ReadOnly";
    public const string ShowInForm = "ShowInForm";
    public const string ClearWhenHidden = "ClearWhenHidden";
    public const string VisibleWhen = "VisibleWhen";
    public const string RequiredWhen = "RequiredWhen";
    public const string OptionFilter = "OptionFilter";
    public const string DefaultOptions = "DefaultOptions";
    public const string Protection = "Protection";
    public const string OptionAdded = "OptionAdded";
    public const string OptionRemoved = "OptionRemoved";
    public const string OptionRelabelled = "OptionRelabelled";
    public const string OptionDeactivated = "OptionDeactivated";
    public const string OptionReactivated = "OptionReactivated";
    public const string OptionOrder = "OptionOrder";
    public const string UploadPolicy = "UploadPolicy";
    public const string SummaryFields = "SummaryFields";
    public const string Repeat = "Repeat";
    public const string RepeatLimits = "RepeatLimits";
    public const string RepeatGroup = "RepeatGroup";
    public const string Kind = "Kind";
    public const string Element = "Element";
}

/// <summary>One changed aspect; <see cref="Tightened"/> marks a validation or limit that can refuse input it accepted before.</summary>
public sealed record LayoutChangeDetail(string Aspect, string Significance, string? Before = null, string? After = null, string? Subject = null, bool Tightened = false);

/// <summary>One changed element (identified by its stable id, never by its label or position).</summary>
public sealed record LayoutChange(string Target, string ElementId, string ChangeType, string Significance, string Label, string? SectionTitle, IReadOnlyList<LayoutChangeDetail> Details);

public sealed record LayoutComparisonResult(IReadOnlyList<LayoutChange> Changes)
{
    public int AddedCount => Changes.Count(c => c.ChangeType == LayoutChangeTypes.Added);

    public int RemovedCount => Changes.Count(c => c.ChangeType == LayoutChangeTypes.Removed);

    public int BehaviourChangeCount => Changes.Count(c => c.ChangeType is LayoutChangeTypes.Modified or LayoutChangeTypes.Moved && c.Significance == LayoutChangeSignificance.Behaviour);

    public int PresentationChangeCount => Changes.Count(c => c.ChangeType is LayoutChangeTypes.Modified or LayoutChangeTypes.Moved && c.Significance == LayoutChangeSignificance.Presentation);

    public bool HasChanges => Changes.Count > 0;
}

/// <summary>
/// Phase C9 WP-3: semantic comparison of two layouts. Elements are matched by stable identity — fields and document
/// collections by <c>FieldDefinitionId</c>, related lists by <c>FormRelationshipDefinitionId</c>, sections by
/// <c>SectionKey</c>, options by <c>FieldOptionId</c> — never by label or position. Order and width changes are reported as
/// presentation changes, not as removals. Deterministic: changes follow the order of the later layout, then removed items
/// in the order of the earlier one. Pure: no store, no record data.
/// </summary>
public static class FormLayoutComparison
{
    private static readonly IReadOnlyList<LayoutSection> NoSections = [];

    /// <summary>Compares <paramref name="from"/> (null: nothing published yet) with <paramref name="to"/>.</summary>
    public static LayoutComparisonResult Compare(FormLayout? from, FormLayout to)
    {
        ArgumentNullException.ThrowIfNull(to);
        var changes = new List<LayoutChange>();
        var fromSections = from?.Sections ?? NoSections;
        if (from is not null && !string.Equals(from.Title, to.Title, StringComparison.Ordinal))
        {
            changes.Add(new LayoutChange(LayoutChangeTargets.Form, "form", LayoutChangeTypes.Modified, LayoutChangeSignificance.Presentation, to.Title, null,
                [new LayoutChangeDetail(LayoutChangeAspects.Title, LayoutChangeSignificance.Presentation, from.Title, to.Title)]));
        }

        CompareSections(fromSections, to.Sections, changes);
        CompareElements(fromSections, to.Sections, changes);
        return new LayoutComparisonResult(changes);
    }

    private static void CompareSections(IReadOnlyList<LayoutSection> fromSections, IReadOnlyList<LayoutSection> toSections, List<LayoutChange> changes)
    {
        var fromByKey = Distinct(fromSections, s => s.SectionKey);
        var toByKey = Distinct(toSections, s => s.SectionKey);
        var fromOrder = CommonOrder(fromSections.Select(s => s.SectionKey), toByKey.Keys);
        var toOrder = CommonOrder(toSections.Select(s => s.SectionKey), fromByKey.Keys);
        foreach (var section in toByKey.Values)
        {
            if (!fromByKey.TryGetValue(section.SectionKey, out var before))
            {
                changes.Add(new LayoutChange(LayoutChangeTargets.Section, section.SectionKey, LayoutChangeTypes.Added, LayoutChangeSignificance.Structure, section.Title, null,
                    section.Repeat is null ? [] : [new LayoutChangeDetail(LayoutChangeAspects.Repeat, LayoutChangeSignificance.Behaviour, null, RepeatText(section.Repeat))]));
                continue;
            }

            var details = new List<LayoutChangeDetail>();
            Text(details, LayoutChangeAspects.Title, before.Title, section.Title, LayoutChangeSignificance.Presentation);
            Text(details, LayoutChangeAspects.Description, before.Description, section.Description, LayoutChangeSignificance.Presentation);
            if (fromOrder[section.SectionKey] != toOrder[section.SectionKey])
            {
                details.Add(new LayoutChangeDetail(LayoutChangeAspects.Position, LayoutChangeSignificance.Presentation));
            }

            if ((before.Repeat is null) != (section.Repeat is null))
            {
                details.Add(new LayoutChangeDetail(LayoutChangeAspects.Repeat, LayoutChangeSignificance.Behaviour, RepeatText(before.Repeat), RepeatText(section.Repeat)));
            }
            else if (before.Repeat is { } g1 && section.Repeat is { } g2 && g1.GroupDefinitionId != g2.GroupDefinitionId)
            {
                // The same section now places a different repeating group: the previous group's entries are kept, but no longer shown or edited here.
                details.Add(new LayoutChangeDetail(LayoutChangeAspects.RepeatGroup, LayoutChangeSignificance.Behaviour, g1.GroupDefinitionId.ToString("D"), g2.GroupDefinitionId.ToString("D")));
            }

            if (before.Repeat is { } r1 && section.Repeat is { } r2 && RepeatText(r1) != RepeatText(r2))
            {
                details.Add(new LayoutChangeDetail(LayoutChangeAspects.RepeatLimits, LayoutChangeSignificance.Behaviour, RepeatText(r1), RepeatText(r2), Tightened: r2.MinOccurrences > r1.MinOccurrences || r2.MaxOccurrences < r1.MaxOccurrences || (r1.AllowAdd && !r2.AllowAdd) || (r1.AllowRemove && !r2.AllowRemove)));
            }

            if (details.Count > 0)
            {
                changes.Add(Change(LayoutChangeTargets.Section, section.SectionKey, section.Title, null, details));
            }
        }

        foreach (var section in fromByKey.Values.Where(s => !toByKey.ContainsKey(s.SectionKey)))
        {
            changes.Add(new LayoutChange(LayoutChangeTargets.Section, section.SectionKey, LayoutChangeTypes.Removed, LayoutChangeSignificance.Structure, section.Title, null, []));
        }
    }

    private static void CompareElements(IReadOnlyList<LayoutSection> fromSections, IReadOnlyList<LayoutSection> toSections, List<LayoutChange> changes)
    {
        var fromPlaced = Placed(fromSections);
        var toPlaced = Placed(toSections);
        var fromIndex = fromPlaced.ToDictionary(p => p.Key);
        var toIndex = toPlaced.ToDictionary(p => p.Key);
        // Order among the elements that stay in the same section in both layouts: an insertion, a removal or a move to another
        // section is not a move of the neighbours.
        var stayed = fromPlaced.Where(p => toIndex.TryGetValue(p.Key, out var t) && t.Section.SectionKey == p.Section.SectionKey).Select(p => p.Key).ToHashSet(StringComparer.Ordinal);
        var fromOrder = SectionOrder(fromPlaced, stayed);
        var toOrder = SectionOrder(toPlaced, stayed);
        foreach (var placed in toPlaced)
        {
            if (!fromIndex.TryGetValue(placed.Key, out var before))
            {
                changes.Add(new LayoutChange(Target(placed.Element), placed.Key, LayoutChangeTypes.Added, LayoutChangeSignificance.Structure, Label(placed.Element), placed.Section.Title, AddedDetails(placed.Element)));
                continue;
            }

            var details = new List<LayoutChangeDetail>();
            if (before.Section.SectionKey != placed.Section.SectionKey)
            {
                details.Add(new LayoutChangeDetail(LayoutChangeAspects.Section, LayoutChangeSignificance.Presentation, before.Section.Title, placed.Section.Title));
            }
            else if (fromOrder[placed.Key] != toOrder[placed.Key])
            {
                details.Add(new LayoutChangeDetail(LayoutChangeAspects.Position, LayoutChangeSignificance.Presentation));
            }

            CompareElement(before.Element, placed.Element, details);
            if (details.Count > 0)
            {
                changes.Add(Change(Target(placed.Element), placed.Key, Label(placed.Element), placed.Section.Title, details));
            }
        }

        foreach (var placed in fromPlaced.Where(p => !toIndex.ContainsKey(p.Key)))
        {
            changes.Add(new LayoutChange(Target(placed.Element), placed.Key, LayoutChangeTypes.Removed, LayoutChangeSignificance.Structure, Label(placed.Element), placed.Section.Title, []));
        }
    }

    private static void CompareElement(LayoutElement a, LayoutElement b, List<LayoutChangeDetail> details)
    {
        const string P = LayoutChangeSignificance.Presentation;
        const string B = LayoutChangeSignificance.Behaviour;
        if (!string.Equals(a.Kind, b.Kind, StringComparison.Ordinal))
        {
            details.Add(new LayoutChangeDetail(LayoutChangeAspects.Kind, B, a.Kind, b.Kind));
        }

        Text(details, LayoutChangeAspects.Label, a.Label, b.Label, P);
        Text(details, LayoutChangeAspects.HelpText, a.HelpText, b.HelpText, P);
        Text(details, LayoutChangeAspects.Placeholder, a.Placeholder, b.Placeholder, P);
        Text(details, LayoutChangeAspects.Control, a.Control, b.Control, P);
        Text(details, LayoutChangeAspects.Placement, PlacementText(a.Layout), PlacementText(b.Layout), P);

        var va = a.Validation;
        var vb = b.Validation;
        if (va.Required != vb.Required)
        {
            details.Add(new LayoutChangeDetail(LayoutChangeAspects.Required, B, YesNo(va.Required), YesNo(vb.Required), Tightened: vb.Required));
        }

        Limit(details, LayoutChangeAspects.MinLength, va.MinLength, vb.MinLength, tighterWhenHigher: true);
        Limit(details, LayoutChangeAspects.MaxLength, va.MaxLength, vb.MaxLength, tighterWhenHigher: false);
        Limit(details, LayoutChangeAspects.Minimum, va.Minimum, vb.Minimum, tighterWhenHigher: true);
        Limit(details, LayoutChangeAspects.Maximum, va.Maximum, vb.Maximum, tighterWhenHigher: false);
        Limit(details, LayoutChangeAspects.MinDate, va.MinDate?.DayNumber, vb.MinDate?.DayNumber, tighterWhenHigher: true, va.MinDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), vb.MinDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        Limit(details, LayoutChangeAspects.MaxDate, va.MaxDate?.DayNumber, vb.MaxDate?.DayNumber, tighterWhenHigher: false, va.MaxDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), vb.MaxDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        if (!string.Equals(va.Pattern, vb.Pattern, StringComparison.Ordinal))
        {
            details.Add(new LayoutChangeDetail(LayoutChangeAspects.Pattern, B, va.Pattern, vb.Pattern, Tightened: va.Pattern is null && vb.Pattern is not null));
        }

        if (a.ReadOnly != b.ReadOnly)
        {
            details.Add(new LayoutChangeDetail(LayoutChangeAspects.ReadOnly, B, YesNo(a.ReadOnly), YesNo(b.ReadOnly)));
        }

        if (a.ShowInForm != b.ShowInForm)
        {
            details.Add(new LayoutChangeDetail(LayoutChangeAspects.ShowInForm, B, YesNo(a.ShowInForm), YesNo(b.ShowInForm)));
        }

        if (a.ClearWhenHidden != b.ClearWhenHidden)
        {
            details.Add(new LayoutChangeDetail(LayoutChangeAspects.ClearWhenHidden, B, YesNo(a.ClearWhenHidden), YesNo(b.ClearWhenHidden)));
        }

        Rule(details, LayoutChangeAspects.VisibleWhen, a.VisibleWhen, b.VisibleWhen, tightened: false);
        // A conditional requirement is a proven tightening only when it is added to a field that is not otherwise required
        // (before and after). Replacing a condition changes when the field is required in a direction that cannot be proven
        // statically; removing one, or adding one while dropping an unconditional requirement, relaxes it.
        Rule(details, LayoutChangeAspects.RequiredWhen, a.RequiredWhen, b.RequiredWhen, tightened: a.RequiredWhen is null && b.RequiredWhen is not null && !va.Required && !vb.Required);
        if (FilterKey(a.OptionFilter) != FilterKey(b.OptionFilter))
        {
            details.Add(new LayoutChangeDetail(LayoutChangeAspects.OptionFilter, B, a.OptionFilter is null ? "none" : "set", b.OptionFilter is null ? "none" : "set"));
        }

        var defaultsA = string.Join(",", (a.DefaultOptionKeys ?? []).Order(StringComparer.Ordinal));
        var defaultsB = string.Join(",", (b.DefaultOptionKeys ?? []).Order(StringComparer.Ordinal));
        if (defaultsA != defaultsB)
        {
            details.Add(new LayoutChangeDetail(LayoutChangeAspects.DefaultOptions, B, defaultsA.Length == 0 ? null : defaultsA, defaultsB.Length == 0 ? null : defaultsB));
        }

        var protectionA = a.Protection is null ? null : a.Protection.Classification + " / " + a.Protection.ProtectionMode;
        var protectionB = b.Protection is null ? null : b.Protection.Classification + " / " + b.Protection.ProtectionMode;
        if (!string.Equals(protectionA, protectionB, StringComparison.Ordinal))
        {
            details.Add(new LayoutChangeDetail(LayoutChangeAspects.Protection, B, protectionA, protectionB));
        }

        CompareOptions(a.Options ?? [], b.Options ?? [], details);
        if ((a.Upload is null) != (b.Upload is null))
        {
            details.Add(new LayoutChangeDetail(LayoutChangeAspects.UploadPolicy, B, a.Upload is null ? null : UploadText(a.Upload), b.Upload is null ? null : UploadText(b.Upload)));
        }
        else if (a.Upload is { } ua && b.Upload is { } ub)
        {
            var before = UploadText(ua);
            var after = UploadText(ub);
            if (before != after)
            {
                details.Add(new LayoutChangeDetail(LayoutChangeAspects.UploadPolicy, B, before, after,
                    Tightened: ub.MaxFiles < ua.MaxFiles || ub.MaxFileSizeBytes < ua.MaxFileSizeBytes || (ua.AllowMultiple && !ub.AllowMultiple) || ua.AllowedContentTypes.Except(ub.AllowedContentTypes, StringComparer.Ordinal).Any()));
            }
        }

        var summaryA = string.Join(", ", a.SummaryFieldKeys ?? []);
        var summaryB = string.Join(", ", b.SummaryFieldKeys ?? []);
        if (summaryA != summaryB)
        {
            details.Add(new LayoutChangeDetail(LayoutChangeAspects.SummaryFields, P, summaryA, summaryB));
        }
    }

    private static void CompareOptions(IReadOnlyList<LayoutOption> a, IReadOnlyList<LayoutOption> b, List<LayoutChangeDetail> details)
    {
        if (a.Count == 0 && b.Count == 0)
        {
            return;
        }

        var byIdA = Distinct(a, o => o.FieldOptionId.ToString("D"));
        var byIdB = Distinct(b, o => o.FieldOptionId.ToString("D"));
        foreach (var option in b)
        {
            if (!byIdA.TryGetValue(option.FieldOptionId.ToString("D"), out var before))
            {
                details.Add(new LayoutChangeDetail(LayoutChangeAspects.OptionAdded, LayoutChangeSignificance.Behaviour, null, option.Label, option.Label));
                continue;
            }

            if (!string.Equals(before.Label, option.Label, StringComparison.Ordinal))
            {
                details.Add(new LayoutChangeDetail(LayoutChangeAspects.OptionRelabelled, LayoutChangeSignificance.Presentation, before.Label, option.Label, option.Label));
            }

            if (before.IsActive && !option.IsActive)
            {
                details.Add(new LayoutChangeDetail(LayoutChangeAspects.OptionDeactivated, LayoutChangeSignificance.Behaviour, "available", "no longer available", option.Label, Tightened: true));
            }
            else if (!before.IsActive && option.IsActive)
            {
                details.Add(new LayoutChangeDetail(LayoutChangeAspects.OptionReactivated, LayoutChangeSignificance.Behaviour, "not available", "available", option.Label));
            }
        }

        foreach (var option in a.Where(o => !byIdB.ContainsKey(o.FieldOptionId.ToString("D"))))
        {
            details.Add(new LayoutChangeDetail(LayoutChangeAspects.OptionRemoved, LayoutChangeSignificance.Behaviour, option.Label, null, option.Label, Tightened: true));
        }

        var common = b.Where(o => byIdA.ContainsKey(o.FieldOptionId.ToString("D"))).Select(o => o.FieldOptionId).ToList();
        var commonBefore = a.OrderBy(o => o.SortOrder).ThenBy(o => o.OptionKey, StringComparer.Ordinal).Select(o => o.FieldOptionId).Where(id => byIdB.ContainsKey(id.ToString("D"))).ToList();
        var commonAfter = b.OrderBy(o => o.SortOrder).ThenBy(o => o.OptionKey, StringComparer.Ordinal).Select(o => o.FieldOptionId).Where(id => byIdA.ContainsKey(id.ToString("D"))).ToList();
        if (common.Count > 1 && !commonBefore.SequenceEqual(commonAfter))
        {
            details.Add(new LayoutChangeDetail(LayoutChangeAspects.OptionOrder, LayoutChangeSignificance.Presentation));
        }
    }

    private static List<LayoutChangeDetail> AddedDetails(LayoutElement element)
    {
        var details = new List<LayoutChangeDetail>();
        if (element.Validation.Required)
        {
            details.Add(new LayoutChangeDetail(LayoutChangeAspects.Required, LayoutChangeSignificance.Behaviour, null, YesNo(true), Tightened: true));
        }

        if (element.RequiredWhen is not null)
        {
            details.Add(new LayoutChangeDetail(LayoutChangeAspects.RequiredWhen, LayoutChangeSignificance.Behaviour, "none", "set", Tightened: !element.Validation.Required));
        }

        return details;
    }

    private static LayoutChange Change(string target, string id, string label, string? section, List<LayoutChangeDetail> details)
    {
        var significance = details.MaxBy(d => LayoutChangeSignificance.Rank(d.Significance))!.Significance;
        var type = details.All(d => d.Aspect is LayoutChangeAspects.Position or LayoutChangeAspects.Section) ? LayoutChangeTypes.Moved : LayoutChangeTypes.Modified;
        return new LayoutChange(target, id, type, significance, label, section, details);
    }

    private sealed record PlacedElement(string Key, LayoutElement Element, LayoutSection Section);

    private static List<PlacedElement> Placed(IReadOnlyList<LayoutSection> sections)
    {
        var result = new List<PlacedElement>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var section in sections)
        {
            foreach (var element in section.Elements)
            {
                var key = Key(element);
                if (key is not null && seen.Add(key))
                {
                    result.Add(new PlacedElement(key, element, section));
                }
            }
        }

        return result;
    }

    private static string? Key(LayoutElement element) => element.Kind switch
    {
        LayoutElementKinds.RelatedFormList => element.FormRelationshipDefinitionId?.ToString("D"),
        _ => element.FieldDefinitionId?.ToString("D"),
    };

    private static string Target(LayoutElement element) => element.Kind switch
    {
        LayoutElementKinds.RelatedFormList => LayoutChangeTargets.RelatedFormList,
        LayoutElementKinds.DocumentCollection => LayoutChangeTargets.DocumentCollection,
        _ => LayoutChangeTargets.Field,
    };

    private static string Label(LayoutElement element) => string.IsNullOrWhiteSpace(element.Label) ? "(no label)" : element.Label;

    private static Dictionary<string, T> Distinct<T>(IEnumerable<T> items, Func<T, string> key)
    {
        var result = new Dictionary<string, T>(StringComparer.Ordinal);
        foreach (var item in items)
        {
            result.TryAdd(key(item), item);
        }

        return result;
    }

    /// <summary>Index of each kept element among the kept elements of its own section.</summary>
    private static Dictionary<string, int> SectionOrder(IEnumerable<PlacedElement> placed, HashSet<string> kept)
    {
        var result = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var section in placed.Where(p => kept.Contains(p.Key)).GroupBy(p => p.Section.SectionKey, StringComparer.Ordinal))
        {
            var index = 0;
            foreach (var item in section)
            {
                result[item.Key] = index++;
            }
        }

        return result;
    }

    /// <summary>Index of each key among the keys present in both layouts (so insertions and removals are not moves).</summary>
    private static Dictionary<string, int> CommonOrder(IEnumerable<string> keys, IEnumerable<string> otherKeys)
    {
        var other = otherKeys.ToHashSet(StringComparer.Ordinal);
        var result = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var key in keys.Where(other.Contains))
        {
            result.TryAdd(key, result.Count);
        }

        return result;
    }

    private static void Text(List<LayoutChangeDetail> details, string aspect, string? before, string? after, string significance)
    {
        var a = string.IsNullOrEmpty(before) ? null : before;
        var b = string.IsNullOrEmpty(after) ? null : after;
        if (!string.Equals(a, b, StringComparison.Ordinal))
        {
            details.Add(new LayoutChangeDetail(aspect, significance, a, b));
        }
    }

    private static void Limit(List<LayoutChangeDetail> details, string aspect, decimal? before, decimal? after, bool tighterWhenHigher, string? beforeText = null, string? afterText = null)
    {
        if (before == after)
        {
            return;
        }

        var tightened = after is not null && (before is null || (tighterWhenHigher ? after > before : after < before));
        details.Add(new LayoutChangeDetail(aspect, LayoutChangeSignificance.Behaviour, beforeText ?? before?.ToString(CultureInfo.InvariantCulture), afterText ?? after?.ToString(CultureInfo.InvariantCulture), Tightened: tightened));
    }

    private static void Rule(List<LayoutChangeDetail> details, string aspect, RuleCondition? before, RuleCondition? after, bool tightened)
    {
        var a = RuleKey(before);
        var b = RuleKey(after);
        if (a != b)
        {
            details.Add(new LayoutChangeDetail(aspect, LayoutChangeSignificance.Behaviour, before is null ? "none" : "set", after is null ? "none" : "set", Tightened: tightened));
        }
    }

    private static string RuleKey(RuleCondition? condition) => condition switch
    {
        null => string.Empty,
        RuleGroup g => g.Operator + "(" + string.Join(";", g.Conditions.Select(RuleKey)) + ")",
        RuleComparison c => c.Operator + "|" + c.FieldDefinitionId.ToString("D") + "|" + c.Value + "|" + string.Join(",", c.Values ?? []),
        _ => condition.GetType().Name,
    };

    private static string FilterKey(LayoutOptionFilter? filter) =>
        filter is null ? string.Empty : filter.ParentFieldDefinitionId.ToString("D") + ":" + string.Join(";", filter.ParentOptionKeysByOption.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => p.Key + "=" + string.Join(",", p.Value.Order(StringComparer.Ordinal))));

    private static string PlacementText(LayoutPlacement placement) =>
        string.Create(CultureInfo.InvariantCulture, $"{placement.ColumnSpan} of 4 columns{(placement.StartOnNewRow ? ", new row" : string.Empty)}{(placement.ColumnStart is { } start ? $", column {start}" : string.Empty)}");

    private static string? RepeatText(LayoutRepeat? repeat) => repeat is null
        ? null
        : string.Create(CultureInfo.InvariantCulture, $"{repeat.MinOccurrences}–{repeat.MaxOccurrences} entries{(repeat.AllowAdd ? ", add" : string.Empty)}{(repeat.AllowRemove ? ", remove" : string.Empty)}{(repeat.AllowReorder ? ", reorder" : string.Empty)}");

    private static string UploadText(LayoutUploadPolicy policy) =>
        string.Create(CultureInfo.InvariantCulture, $"{(policy.AllowMultiple ? $"up to {policy.MaxFiles} files" : "one file")}, up to {policy.MaxFileSizeBytes} bytes each, types {string.Join(" ", policy.AllowedContentTypes.Order(StringComparer.Ordinal))}");

    private static string YesNo(bool value) => value ? "yes" : "no";
}
