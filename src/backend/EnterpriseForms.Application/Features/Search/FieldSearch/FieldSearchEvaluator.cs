using System.Text;
using EnterpriseForms.Application.Features.FormRecords.Ports;
using EnterpriseForms.Application.Features.Search.Verification;
using EnterpriseForms.Contracts.Features;

namespace EnterpriseForms.Application.Features.Search.FieldSearch;

/// <summary>
/// Phase C7 M3 — predicate evaluation over a verified scope (architecture §4.5, §6C). Only values the Authority verified are
/// read (the decoded content of each verified record), only top-level values (V1 excludes repeating groups), and a record
/// is evaluated only if the caller may read every value item of every predicate field on it; otherwise it is left out of
/// the evaluation entirely, independently of its values, so nothing about an unreadable value can be inferred.
/// <para>
/// Semantics: a missing value never matches. Text compares NFC-normalized, trimmed, case-insensitive (ordinal ignore
/// case): <c>equals</c> is whole-value equality, <c>startsWith</c>/<c>contains</c> are prefix/substring. Numbers, dates and
/// instants compare by value; <c>between</c> is inclusive; <c>before</c>/<c>after</c> are strict. Option <c>is</c>: the
/// selection is exactly that option; <c>includes</c>: the selection contains it. Booleans: <c>isTrue</c>/<c>isFalse</c>.
/// </para>
/// </summary>
public static class FieldSearchEvaluator
{
    /// <summary>A validated, parsed condition (values never leave the server or appear in logs).</summary>
    public sealed record PreparedCondition(Guid FieldDefinitionId, string Operator, string ValueType, IComparable? Value, IComparable? Lower, IComparable? Upper);

    public static IReadOnlyList<PreparedCondition> Prepare(IReadOnlyList<FieldSearchCondition> conditions, FieldSearchLimits limits)
    {
        ArgumentNullException.ThrowIfNull(conditions);
        return conditions.Select((c, index) => new PreparedCondition(c.FieldDefinitionId, c.Operator, c.ValueType,
            c.Value is null ? null : Normalize(FieldSearchRequestValidator.Parse(c.ValueType, c.Value, index + 1, limits)),
            c.LowerBound is null ? null : Normalize(FieldSearchRequestValidator.Parse(c.ValueType, c.LowerBound, index + 1, limits)),
            c.UpperBound is null ? null : Normalize(FieldSearchRequestValidator.Parse(c.ValueType, c.UpperBound, index + 1, limits)))).ToList();
    }

    /// <summary>Whether the caller may read every top-level value item of every predicate field of this verified record.</summary>
    public static bool Evaluable(VerifiedScopeRecord record, IReadOnlyList<PreparedCondition> conditions)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(conditions);
        if (record.Content is null)
        {
            return false;
        }

        if (record.IsCompleteProjection)
        {
            return true;
        }

        var readable = record.ReadableItems.Select(i => i.ItemId).ToHashSet();
        var fields = conditions.Select(c => c.FieldDefinitionId).ToHashSet();
        return record.Content.Values.Where(v => v.FormRecordGroupId is null && fields.Contains(v.FieldDefinitionId)).All(v => readable.Contains(v.ItemId));
    }

    /// <summary>All conditions hold (AND). Call only for an evaluable record.</summary>
    public static bool Matches(VerifiedScopeRecord record, IReadOnlyList<PreparedCondition> conditions)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(conditions);
        var values = record.Content?.Values ?? [];
        foreach (var condition in conditions)
        {
            var own = values.Where(v => v.FormRecordGroupId is null && v.FieldDefinitionId == condition.FieldDefinitionId && v.ValueType == condition.ValueType).ToList();
            if (!Holds(condition, own))
            {
                return false;
            }
        }

        return true;
    }

    private static bool Holds(PreparedCondition condition, List<AcceptedFieldValue> values)
    {
        if (values.Count == 0)
        {
            return false;
        }

        if (condition.ValueType == FieldSearchValueTypes.OptionType)
        {
            var selected = values.Select(v => v.Identifier).OfType<Guid>().ToHashSet();
            var wanted = (Guid)condition.Value!;
            return condition.Operator switch
            {
                FieldSearchOperators.Is => selected.Count == 1 && selected.Contains(wanted),
                FieldSearchOperators.Includes => selected.Contains(wanted),
                _ => false,
            };
        }

        return values.Any(v => condition.ValueType switch
        {
            FieldSearchValueTypes.StringType => v.Text is { } text && Text(condition, Normalize(text)),
            FieldSearchValueTypes.IntegerType => v.IntegerValue is { } i && Compare(condition, i),
            FieldSearchValueTypes.DecimalType => v.DecimalValue is { } d && Compare(condition, d),
            FieldSearchValueTypes.DateType => v.Date is { } date && Compare(condition, date),
            FieldSearchValueTypes.DateTimeType => v.Instant is { } instant && Compare(condition, instant.ToUniversalTime()),
            FieldSearchValueTypes.BooleanType => v.Boolean is { } b && (condition.Operator == FieldSearchOperators.IsTrue ? b : condition.Operator == FieldSearchOperators.IsFalse && !b),
            _ => false,
        });
    }

    private static bool Text(PreparedCondition condition, string stored)
    {
        var wanted = (string)condition.Value!;
        return condition.Operator switch
        {
            FieldSearchOperators.EqualsTo => string.Equals(stored, wanted, StringComparison.OrdinalIgnoreCase),
            FieldSearchOperators.StartsWith => stored.StartsWith(wanted, StringComparison.OrdinalIgnoreCase),
            FieldSearchOperators.Contains => stored.Contains(wanted, StringComparison.OrdinalIgnoreCase),
            _ => false,
        };
    }

    private static bool Compare<T>(PreparedCondition condition, T stored)
        where T : IComparable<T>
    {
        int Against(IComparable? bound) => stored.CompareTo((T)bound!);
        return condition.Operator switch
        {
            FieldSearchOperators.EqualsTo => Against(condition.Value) == 0,
            FieldSearchOperators.GreaterThan or FieldSearchOperators.After => Against(condition.Value) > 0,
            FieldSearchOperators.GreaterThanOrEqual => Against(condition.Value) >= 0,
            FieldSearchOperators.LessThan or FieldSearchOperators.Before => Against(condition.Value) < 0,
            FieldSearchOperators.LessThanOrEqual => Against(condition.Value) <= 0,
            FieldSearchOperators.Between => Against(condition.Lower) >= 0 && Against(condition.Upper) <= 0,
            _ => false,
        };
    }

    private static IComparable Normalize(IComparable parsed) => parsed is string text ? Normalize(text) : parsed;

    private static string Normalize(string text) => text.Normalize(NormalizationForm.FormC).Trim();
}
