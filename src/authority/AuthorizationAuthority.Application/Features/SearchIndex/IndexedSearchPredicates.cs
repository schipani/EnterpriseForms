using System.Globalization;
using System.Text;
using AuthorizationAuthority.Application.Common;
using AuthorizationAuthority.Contracts.V1;
using EnterpriseForms.Integrity.Search;

namespace AuthorizationAuthority.Application.Features.SearchIndex;

/// <summary>IX-3 configuration, section <c>SearchIndex:Query</c>. Provisional engineering limits (design §8.3, §17.3), not capacity promises.</summary>
public sealed class IndexedSearchOptions
{
    public const string SectionName = "SearchIndex:Query";

    /// <summary>Off by default; Development/Testing only, and only with the index maintained (Dark) and a reader connection.</summary>
    public bool QueryEnabled { get; set; }

    /// <summary>IX-6: My Records from verified index entries (requires <see cref="QueryEnabled"/>). Off by default; Development/Testing only.</summary>
    public bool RecordListEnabled { get; set; }

    public int MaximumConditions { get; set; } = 5;

    public int MaximumTextValueLength { get; set; } = 200;

    public int MinimumPartialTextLength { get; set; } = 3;

    /// <summary>R1 §E.3: predicate-field items of share-only records checked per search (caller-owned quantity).</summary>
    public int MaximumShareOnlyItems { get; set; } = 20_000;

    /// <summary>§8.3, §17.3: authorized matches per snapshot.</summary>
    public int MaximumResults { get; set; } = 10_000;

    /// <summary>§17.3: absolute snapshot lifetime (default 5 minutes, at most 15; paging never extends it).</summary>
    public int SnapshotLifetimeSeconds { get; set; } = 300;

    public int MaximumSnapshotsPerSession { get; set; } = 3;

    public int MaximumLiveEntriesPerInstance { get; set; } = 500_000;

    /// <summary>PostgreSQL statement_timeout of the reader transaction; a timeout is SEARCH_VERIFICATION_LIMIT (time).</summary>
    public int StatementTimeoutSeconds { get; set; } = 10;

    public int PurgeIntervalSeconds { get; set; } = 60;

    public int PurgeBatchSize { get; set; } = 500;

    public static readonly int[] PageSizes = [10, 25, 50, 100];

    public static void Validate(IndexedSearchOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.MaximumConditions is < 1 or > 5 || options.MaximumTextValueLength < 1 || options.MinimumPartialTextLength < 1 || options.MinimumPartialTextLength > options.MaximumTextValueLength
            || options.MaximumShareOnlyItems is < 1 or > 20_000 || options.MaximumResults is < 1 or > 10_000 || options.SnapshotLifetimeSeconds is < 1 or > 900
            || options.MaximumSnapshotsPerSession is < 1 or > 3 || options.MaximumLiveEntriesPerInstance < 1 || options.StatementTimeoutSeconds is < 1 or > 60
            || options.PurgeIntervalSeconds is < 1 or > 60 || options.PurgeBatchSize is < 1 or > 5000)
        {
            throw new InvalidOperationException("SearchIndex:Query limits are out of range (conditions 1..5, items <= 20000, results <= 10000, lifetime 1..900 s, snapshots per session 1..3, timeout 1..60 s, purge 1..60 s).");
        }
    }
}

/// <summary>The V1 operator vocabulary (Phase C7 architecture §5.3), mirrored exactly: which operators can apply to a value type at all.</summary>
public static class IndexedSearchVocabulary
{
    public const string EqualsTo = "equals";
    public const string StartsWith = "startsWith";
    public const string Contains = "contains";
    public const string GreaterThan = "greaterThan";
    public const string GreaterThanOrEqual = "greaterThanOrEqual";
    public const string LessThan = "lessThan";
    public const string LessThanOrEqual = "lessThanOrEqual";
    public const string Between = "between";
    public const string Before = "before";
    public const string After = "after";
    public const string Is = "is";
    public const string Includes = "includes";
    public const string IsTrue = "isTrue";
    public const string IsFalse = "isFalse";

    private static readonly Dictionary<string, string[]> ByValueType = new(StringComparer.Ordinal)
    {
        ["String"] = [EqualsTo, StartsWith, Contains],
        ["Integer"] = [EqualsTo, GreaterThan, GreaterThanOrEqual, LessThan, LessThanOrEqual, Between],
        ["Decimal"] = [EqualsTo, GreaterThan, GreaterThanOrEqual, LessThan, LessThanOrEqual, Between],
        ["Date"] = [EqualsTo, Before, After, Between],
        ["DateTime"] = [Before, After, Between],
        ["Boolean"] = [IsTrue, IsFalse],
        ["Option"] = [Is, Includes],
    };

    public static bool AppliesTo(string valueType, string op) => ByValueType.TryGetValue(valueType, out var list) && list.Contains(op, StringComparer.Ordinal);

    public static bool IsKnownValueType(string? valueType) => valueType is not null && ByValueType.ContainsKey(valueType);

    public static bool IsNullary(string op) => op is IsTrue or IsFalse;

    public static bool IsRange(string op) => op is Between;

    public static bool IsPartialText(string op) => op is StartsWith or Contains;
}

/// <summary>A validated condition. Values never leave the Authority, are never stored and never logged.</summary>
public sealed record PreparedIndexedCondition(int Number, Guid FieldDefinitionId, string Operator, string ValueType, object? Value, object? Lower, object? Upper);

/// <summary>
/// IX-3: request validation and predicate evaluation with exactly the V1 semantics (C7 <c>FieldSearchEvaluator</c> and
/// <c>FieldSearchRequestValidator</c>): a missing value never matches; text compares NFC-normalized, trimmed and
/// case-insensitive — here as ordinal comparison of the <see cref="SearchValueNormalizationV1"/> form, which is how the
/// postings are stored; numbers, dates and instants by value (<c>between</c> inclusive, <c>before</c>/<c>after</c> strict);
/// option <c>is</c> = the selection is exactly that option, <c>includes</c> = it contains it; booleans <c>isTrue</c>/<c>isFalse</c>.
/// The SQL candidate query is only a candidate filter; every match is re-evaluated here on values decoded from verified source
/// bytes (design §18.1.4 step 4), so a SQL/semantics difference can only fail closed.
/// </summary>
public static class IndexedSearchPredicates
{
    public static IReadOnlyList<PreparedIndexedCondition> Prepare(IReadOnlyList<IndexedSearchCondition>? conditions, IndexedSearchOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (conditions is null || conditions.Count == 0 || conditions.Count > options.MaximumConditions)
        {
            throw Invalid($"1..{options.MaximumConditions} conditions are required");
        }

        var prepared = new List<PreparedIndexedCondition>(conditions.Count);
        for (var index = 0; index < conditions.Count; index++)
        {
            var number = index + 1;
            var c = conditions[index] ?? throw Invalid($"condition {number} is empty");
            if (c.FieldDefinitionId == Guid.Empty || !IndexedSearchVocabulary.IsKnownValueType(c.ValueType))
            {
                throw Invalid($"condition {number}: field and value type are required");
            }

            if (c.Operator is null || !IndexedSearchVocabulary.AppliesTo(c.ValueType, c.Operator))
            {
                throw new AuthorityException(IndexedSearchErrorCodes.OperatorNotSupported, 422, false, $"condition {number}: operator not supported");
            }

            if (IndexedSearchVocabulary.IsNullary(c.Operator))
            {
                if (c.Value is not null || c.LowerBound is not null || c.UpperBound is not null)
                {
                    throw Invalid($"condition {number}: this condition takes no value");
                }

                prepared.Add(new PreparedIndexedCondition(number, c.FieldDefinitionId, c.Operator, c.ValueType, null, null, null));
                continue;
            }

            if (IndexedSearchVocabulary.IsRange(c.Operator))
            {
                if (c.Value is not null || c.LowerBound is null || c.UpperBound is null)
                {
                    throw Invalid($"condition {number}: both bounds are required");
                }

                var lower = Parse(c.ValueType, c.LowerBound, number, options);
                var upper = Parse(c.ValueType, c.UpperBound, number, options);
                if (Comparer<IComparable>.Default.Compare(lower, upper) > 0)
                {
                    throw Invalid($"condition {number}: the lower bound is after the upper bound");
                }

                prepared.Add(new PreparedIndexedCondition(number, c.FieldDefinitionId, c.Operator, c.ValueType, null, NormalizeText(lower), NormalizeText(upper)));
                continue;
            }

            if (c.Value is null || c.LowerBound is not null || c.UpperBound is not null)
            {
                throw Invalid($"condition {number}: a value is required");
            }

            var value = Parse(c.ValueType, c.Value, number, options);
            if (IndexedSearchVocabulary.IsPartialText(c.Operator) && c.Value.Trim().Length < options.MinimumPartialTextLength)
            {
                throw Invalid($"condition {number}: at least {options.MinimumPartialTextLength} characters");
            }

            prepared.Add(new PreparedIndexedCondition(number, c.FieldDefinitionId, c.Operator, c.ValueType, NormalizeText(value), null, null));
        }

        return prepared;
    }

    /// <summary>All conditions hold (AND) on the record's decoded predicate-field values (top-level values only, by construction of the index).</summary>
    public static bool Matches(IReadOnlyList<PreparedIndexedCondition> conditions, IReadOnlyDictionary<Guid, IReadOnlyList<SearchSealValue>> valuesByField)
    {
        ArgumentNullException.ThrowIfNull(conditions);
        ArgumentNullException.ThrowIfNull(valuesByField);
        foreach (var condition in conditions)
        {
            if (!valuesByField.TryGetValue(condition.FieldDefinitionId, out var values) || !Holds(condition, values))
            {
                return false;
            }
        }

        return true;
    }

    public static bool Holds(PreparedIndexedCondition condition, IReadOnlyList<SearchSealValue> values)
    {
        ArgumentNullException.ThrowIfNull(condition);
        ArgumentNullException.ThrowIfNull(values);
        if (values.Count == 0)
        {
            return false;
        }

        if (condition.ValueType == "Option")
        {
            var selected = values.OfType<SearchSealValue.OptionValue>().Select(v => v.FieldOptionId).ToHashSet();
            var wanted = (Guid)condition.Value!;
            return condition.Operator switch
            {
                IndexedSearchVocabulary.Is => selected.Count == 1 && selected.Contains(wanted),
                IndexedSearchVocabulary.Includes => selected.Contains(wanted),
                _ => false,
            };
        }

        return values.Any(v => (condition.ValueType, v) switch
        {
            ("String", SearchSealValue.NormalizedString s) => Text(condition, s.Value),
            ("Integer", SearchSealValue.IntegerValue i) => Compare(condition, i.Value),
            ("Decimal", SearchSealValue.DecimalParts d) => Compare(condition, SearchIndexValues.ToDecimal(d)),
            ("Date", SearchSealValue.DateValue d) => Compare(condition, d.Value),
            ("DateTime", SearchSealValue.Timestamp t) => Compare(condition, t.Value.ToUniversalTime()),
            ("Boolean", SearchSealValue.BooleanValue b) => condition.Operator == IndexedSearchVocabulary.IsTrue ? b.Value : condition.Operator == IndexedSearchVocabulary.IsFalse && !b.Value,
            _ => false,
        });
    }

    /// <summary>The normalized string form of a text predicate value (identical to how string postings are stored).</summary>
    public static string NormalizedText(PreparedIndexedCondition condition) => (string)condition.Value!;

    private static bool Text(PreparedIndexedCondition condition, string stored)
    {
        var wanted = (string)condition.Value!;
        return condition.Operator switch
        {
            IndexedSearchVocabulary.EqualsTo => string.Equals(stored, wanted, StringComparison.Ordinal),
            IndexedSearchVocabulary.StartsWith => stored.StartsWith(wanted, StringComparison.Ordinal),
            IndexedSearchVocabulary.Contains => stored.Contains(wanted, StringComparison.Ordinal),
            _ => false,
        };
    }

    private static bool Compare<T>(PreparedIndexedCondition condition, T stored)
        where T : IComparable<T>
    {
        int Against(object? bound) => stored.CompareTo((T)bound!);
        return condition.Operator switch
        {
            IndexedSearchVocabulary.EqualsTo => Against(condition.Value) == 0,
            IndexedSearchVocabulary.GreaterThan or IndexedSearchVocabulary.After => Against(condition.Value) > 0,
            IndexedSearchVocabulary.GreaterThanOrEqual => Against(condition.Value) >= 0,
            IndexedSearchVocabulary.LessThan or IndexedSearchVocabulary.Before => Against(condition.Value) < 0,
            IndexedSearchVocabulary.LessThanOrEqual => Against(condition.Value) <= 0,
            IndexedSearchVocabulary.Between => Against(condition.Lower) >= 0 && Against(condition.Upper) <= 0,
            _ => false,
        };
    }

    private static object NormalizeText(IComparable parsed) => parsed is string text ? SearchValueNormalizationV1.NormalizeString(text) : parsed;

    /// <summary>Strict invariant parsing, identical to the V1 validator (C7 <c>FieldSearchRequestValidator.Parse</c>).</summary>
    private static IComparable Parse(string valueType, string text, int number, IndexedSearchOptions options)
    {
        var invalid = Invalid($"condition {number}: the value is not a valid {valueType.ToLowerInvariant()}");
        switch (valueType)
        {
            case "String":
                if (text.Length == 0 || text.Length > options.MaximumTextValueLength || text.Trim().Length == 0 || !text.IsNormalized(NormalizationForm.FormC) || text.Any(char.IsControl))
                {
                    throw invalid;
                }

                return text;
            case "Integer":
                return long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var integer) ? integer : throw invalid;
            case "Decimal":
                return text.Length <= 40 && decimal.TryParse(text, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var number2) ? number2 : throw invalid;
            case "Date":
                return DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ? date : throw invalid;
            case "DateTime":
                return text.Length <= 40 && (text.EndsWith('Z') || (text.Length > 6 && text[^6] is '+' or '-'))
                       && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var instant)
                    ? instant.ToUniversalTime()
                    : throw invalid;
            case "Option":
                return Guid.TryParseExact(text, "D", out var option) && option != Guid.Empty ? option : throw invalid;
            default:
                throw invalid;
        }
    }

    private static AuthorityException Invalid(string detail) => new(AuthorityErrorCodes.InvalidRequest, 422, false, detail);
}
