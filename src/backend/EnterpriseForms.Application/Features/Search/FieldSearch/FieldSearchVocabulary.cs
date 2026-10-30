namespace EnterpriseForms.Application.Features.Search.FieldSearch;

/// <summary>
/// ADR-PB-004 search error codes (Problem Details <c>title</c>). Denials never say whether a field exists: every field that
/// is not in the caller's server-computed capability list gets the same <see cref="FieldNotAllowed"/> response.
/// </summary>
public static class SearchErrorCodes
{
    public const string FieldNotAllowed = "SEARCH_FIELD_NOT_ALLOWED";
    public const string OperatorNotSupported = "SEARCH_OPERATOR_NOT_SUPPORTED";
    public const string VerificationLimit = "SEARCH_VERIFICATION_LIMIT";
    public const string SnapshotChanged = "SEARCH_SNAPSHOT_CHANGED";
    public const string VerificationUnavailable = "SEARCH_VERIFICATION_UNAVAILABLE";

    /// <summary>Malformed request shape (condition count, missing or unparseable value, page size); not one of the ADR codes because it says nothing about policy or verification.</summary>
    public const string RequestInvalid = "SEARCH_REQUEST_INVALID";
}

/// <summary>Reasons carried by <see cref="SearchErrorCodes.VerificationUnavailable"/> (problem extension <c>unavailable_reason</c>) and by the capability response.</summary>
public static class SearchUnavailableReasons
{
    /// <summary>Phase C7 M0: trusted execution (Authority AC-5 catalogue and AC-6 field search policy) does not exist yet, so no field-value search runs.</summary>
    public const string TrustedSearchNotEnabled = "TRUSTED_SEARCH_NOT_ENABLED";
}

/// <summary>Value types of a predicate; the value travels as invariant text and is parsed strictly by the server.</summary>
public static class FieldSearchValueTypes
{
    public const string StringType = "String";
    public const string IntegerType = "Integer";
    public const string DecimalType = "Decimal";
    public const string DateType = "Date";
    public const string DateTimeType = "DateTime";
    public const string BooleanType = "Boolean";

    /// <summary>The value is a stable field option id (never an option key or label: those are unsigned catalog metadata, G21).</summary>
    public const string OptionType = "Option";
}

/// <summary>V1 operators (architecture §5.3). Whether a field offers an operator is decided per field by the server capability list; this table only says which operators can apply to a value type at all.</summary>
public static class FieldSearchOperators
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
        [FieldSearchValueTypes.StringType] = [EqualsTo, StartsWith, Contains],
        [FieldSearchValueTypes.IntegerType] = [EqualsTo, GreaterThan, GreaterThanOrEqual, LessThan, LessThanOrEqual, Between],
        [FieldSearchValueTypes.DecimalType] = [EqualsTo, GreaterThan, GreaterThanOrEqual, LessThan, LessThanOrEqual, Between],
        [FieldSearchValueTypes.DateType] = [EqualsTo, Before, After, Between],
        [FieldSearchValueTypes.DateTimeType] = [Before, After, Between],
        [FieldSearchValueTypes.BooleanType] = [IsTrue, IsFalse],
        [FieldSearchValueTypes.OptionType] = [Is, Includes],
    };

    public static bool IsKnownValueType(string? valueType) => valueType is not null && ByValueType.ContainsKey(valueType);

    public static IReadOnlyList<string> ForValueType(string valueType) => ByValueType.TryGetValue(valueType, out var list) ? list : [];

    public static bool IsKnownOperator(string? op) => op is not null && ByValueType.Values.Any(list => list.Contains(op, StringComparer.Ordinal));

    public static bool AppliesTo(string valueType, string op) => ByValueType.TryGetValue(valueType, out var list) && list.Contains(op, StringComparer.Ordinal);

    /// <summary>Operators that take no value.</summary>
    public static bool IsNullary(string op) => op is IsTrue or IsFalse;

    public static bool IsRange(string op) => op is Between;

    public static bool IsPartialText(string op) => op is StartsWith or Contains;
}
