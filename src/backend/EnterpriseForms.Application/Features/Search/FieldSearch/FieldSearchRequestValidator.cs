using System.Globalization;
using System.Text;
using EnterpriseForms.Contracts.Features;

namespace EnterpriseForms.Application.Features.Search.FieldSearch;

/// <summary>
/// Request-shape validation of an advanced field search (Phase C7 M0). It checks only what the request says about itself —
/// condition count, operator vocabulary, operator against the declared value type, value presence and strict invariant
/// parsing, metadata filters and page size. It deliberately does NOT decide whether a field may be searched: that needs the
/// server capability list (AC-6 policy ∩ published version ∩ field authorization) and happens before any record is touched.
/// Messages name a condition by position only and never repeat a submitted value.
/// </summary>
public static class FieldSearchRequestValidator
{
    private static readonly string[] RecordStatuses = ["Draft", "Submitted", "Archived"];
    private static readonly string[] SortFields = ["modifiedOn", "createdOn", "formName", "recordStatus"];
    private static readonly string[] SortDirections = ["asc", "desc"];

    public static void Validate(FieldSearchRequest? request, FieldSearchLimits limits)
    {
        ArgumentNullException.ThrowIfNull(limits);
        if (request is null)
        {
            throw SearchException.RequestInvalid("request body required");
        }

        if (request.FormDefinitionId == Guid.Empty)
        {
            throw SearchException.RequestInvalid("select a form: advanced field search runs over one form");
        }

        var conditions = request.Conditions ?? [];
        if (conditions.Count == 0)
        {
            throw SearchException.RequestInvalid("at least one condition is required");
        }

        if (conditions.Count > limits.MaximumConditions)
        {
            throw SearchException.RequestInvalid($"at most {limits.MaximumConditions} conditions are allowed (combined with AND)");
        }

        for (var i = 0; i < conditions.Count; i++)
        {
            ValidateCondition(conditions[i], i + 1, limits);
        }

        if (request.RecordStatus is not null && !RecordStatuses.Contains(request.RecordStatus, StringComparer.Ordinal))
        {
            throw SearchException.RequestInvalid("recordStatus must be Draft, Submitted or Archived");
        }

        if (request.SortBy is not null && !SortFields.Contains(request.SortBy, StringComparer.Ordinal))
        {
            throw SearchException.RequestInvalid("sortBy must be modifiedOn, createdOn, formName or recordStatus (sorting by field values is not supported in V1)");
        }

        if (request.SortDirection is not null && !SortDirections.Contains(request.SortDirection, StringComparer.Ordinal))
        {
            throw SearchException.RequestInvalid("sortDirection must be asc or desc");
        }

        if (!FieldSearchLimits.PageSizes.Contains(request.PageSize))
        {
            throw SearchException.RequestInvalid("pageSize must be 10, 25, 50 or 100");
        }
    }

    public static void ValidatePage(FieldSearchPageRequest? request)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.Cursor) || request.Cursor.Length > 4096)
        {
            throw SearchException.RequestInvalid("cursor required");
        }
    }

    private static void ValidateCondition(FieldSearchCondition? condition, int number, FieldSearchLimits limits)
    {
        if (condition is null)
        {
            throw SearchException.RequestInvalid($"condition {number} is empty");
        }

        if (condition.FieldDefinitionId == Guid.Empty)
        {
            throw SearchException.RequestInvalid($"condition {number}: choose a field");
        }

        if (!FieldSearchOperators.IsKnownOperator(condition.Operator))
        {
            throw SearchException.OperatorNotSupported(number);
        }

        if (!FieldSearchOperators.IsKnownValueType(condition.ValueType))
        {
            throw SearchException.RequestInvalid($"condition {number}: unknown value type");
        }

        if (!FieldSearchOperators.AppliesTo(condition.ValueType, condition.Operator))
        {
            throw SearchException.OperatorNotSupported(number);
        }

        if (FieldSearchOperators.IsNullary(condition.Operator))
        {
            if (condition.Value is not null || condition.LowerBound is not null || condition.UpperBound is not null)
            {
                throw SearchException.RequestInvalid($"condition {number}: this condition takes no value");
            }

            return;
        }

        if (FieldSearchOperators.IsRange(condition.Operator))
        {
            if (condition.Value is not null || condition.LowerBound is null || condition.UpperBound is null)
            {
                throw SearchException.RequestInvalid($"condition {number}: enter both the lower and the upper bound");
            }

            var lower = Parse(condition.ValueType, condition.LowerBound, number, limits);
            var upper = Parse(condition.ValueType, condition.UpperBound, number, limits);
            if (Comparer<IComparable>.Default.Compare(lower, upper) > 0)
            {
                throw SearchException.RequestInvalid($"condition {number}: the lower bound is after the upper bound");
            }

            return;
        }

        if (condition.Value is null || condition.LowerBound is not null || condition.UpperBound is not null)
        {
            throw SearchException.RequestInvalid($"condition {number}: enter a value");
        }

        Parse(condition.ValueType, condition.Value, number, limits);
        if (FieldSearchOperators.IsPartialText(condition.Operator) && condition.Value.Trim().Length < limits.MinimumPartialTextLength)
        {
            throw SearchException.RequestInvalid($"condition {number}: enter at least {limits.MinimumPartialTextLength} characters");
        }
    }

    /// <summary>Strict invariant parsing; the parsed value is only compared here (bounds order) and discarded.</summary>
    private static IComparable Parse(string valueType, string text, int number, FieldSearchLimits limits)
    {
        var invalid = SearchException.RequestInvalid($"condition {number}: the value is not a valid {valueType.ToLowerInvariant()}");
        switch (valueType)
        {
            case FieldSearchValueTypes.StringType:
                if (text.Length == 0 || text.Length > limits.MaximumTextValueLength || text.Trim().Length == 0)
                {
                    throw SearchException.RequestInvalid($"condition {number}: enter 1 to {limits.MaximumTextValueLength} characters");
                }

                if (!text.IsNormalized(NormalizationForm.FormC) || text.Any(char.IsControl))
                {
                    throw invalid;
                }

                return text;
            case FieldSearchValueTypes.IntegerType:
                return long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var integer) ? integer : throw invalid;
            case FieldSearchValueTypes.DecimalType:
                return text.Length <= 40 && decimal.TryParse(text, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var number2) ? number2 : throw invalid;
            case FieldSearchValueTypes.DateType:
                return DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ? date : throw invalid;
            case FieldSearchValueTypes.DateTimeType:
                // An instant must carry its offset (UTC "Z" or ±hh:mm): a local wall-clock time is ambiguous across zones.
                return text.Length <= 40 && (text.EndsWith('Z') || (text.Length > 6 && text[^6] is '+' or '-'))
                       && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var instant)
                    ? instant.ToUniversalTime()
                    : throw invalid;
            case FieldSearchValueTypes.OptionType:
                return Guid.TryParseExact(text, "D", out var option) && option != Guid.Empty ? option : throw invalid;
            default:
                throw invalid;
        }
    }
}
