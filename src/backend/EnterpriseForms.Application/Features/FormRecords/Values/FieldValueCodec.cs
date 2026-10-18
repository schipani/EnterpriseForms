#pragma warning disable CA1720 // Identifiers mirror the FieldDataType vocabulary of CLAUDE.md §2.2 (String, Integer, Decimal...).
using System.Globalization;
using System.Text.RegularExpressions;
using EnterpriseForms.Application.Common;
using EnterpriseForms.Application.Features.FormDefinitions.Layout;
using EnterpriseForms.Domain.Features.FormDefinitions;
using EnterpriseForms.Domain.Features.FormRecords;

namespace EnterpriseForms.Application.Features.FormRecords.Values;

public static class FieldDataTypes
{
    public const string String = "String";
    public const string Integer = "Integer";
    public const string Decimal = "Decimal";
    public const string Date = "Date";
    public const string DateTime = "DateTime";
    public const string Guid = "Guid";
    public const string Boolean = "Boolean";
    public const string Option = "Option";
    public const string File = "File";

    public static readonly IReadOnlySet<string> StageB = new HashSet<string>(StringComparer.Ordinal) { String, Integer, Decimal, Date, DateTime, Guid, Boolean, Option, File };
}

/// <summary>
/// Wire representation ↔ typed row. The API carries every value as an invariant string (ISO date, invariant decimal,
/// "true"/"false", option key); the codec validates the layout rules and produces the typed signed row. Invalid
/// values fail the whole write atomically (acceptance test 10).
/// </summary>
public static class FieldValueCodec
{
    public static FieldValueRowBase CreateRow(FieldDefinition field, LayoutElement? element, IReadOnlyList<FieldOption> options, string wireValue, Guid tenantId, Guid formRecordId, Guid fieldValueItemId, DateTimeOffset now, Guid actorUserId)
    {
        ArgumentNullException.ThrowIfNull(field);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(wireValue);
        var validation = element?.Validation ?? new LayoutValidation(false, null, null, null, null, null, null, null);
        FieldValueRowBase row = field.FieldDataType switch
        {
            FieldDataTypes.String => new FieldStringValue { FieldStringValueId = Guid.CreateVersion7(), StringValue = ValidateString(field, validation, wireValue) },
            FieldDataTypes.Integer => new FieldIntegerValue { FieldIntegerValueId = Guid.CreateVersion7(), IntegerValue = ValidateInteger(field, validation, wireValue) },
            FieldDataTypes.Decimal => new FieldDecimalValue { FieldDecimalValueId = Guid.CreateVersion7(), DecimalValue = ValidateDecimal(field, validation, wireValue) },
            FieldDataTypes.Date => new FieldDateValue { FieldDateValueId = Guid.CreateVersion7(), DateValue = ValidateDate(field, validation, wireValue) },
            FieldDataTypes.DateTime => new FieldDateTimeValue { FieldDateTimeValueId = Guid.CreateVersion7(), DateTimeValue = ValidateDateTime(field, validation, wireValue) },
            FieldDataTypes.Guid => new FieldGuidValue { FieldGuidValueId = Guid.CreateVersion7(), GuidValue = ValidateGuid(field, wireValue) },
            FieldDataTypes.Boolean => new FieldBooleanValue { FieldBooleanValueId = Guid.CreateVersion7(), BooleanValue = ValidateBoolean(field, wireValue) },
            FieldDataTypes.Option => new FieldOptionValue { FieldOptionValueId = Guid.CreateVersion7(), FieldOptionId = ValidateOption(field, options, wireValue) },
            _ => throw new BusinessException("INVALID_REQUEST", 422, $"field '{field.FieldKey}' of type {field.FieldDataType} does not accept values"),
        };
        row.TenantId = tenantId;
        row.FormRecordId = formRecordId;
        row.FieldDefinitionId = field.FieldDefinitionId;
        row.FormRecordGroupId = null;
        row.FieldValueItemId = fieldValueItemId;
        row.CreatedOn = now;
        row.CreatedByUserId = actorUserId;
        return row;
    }

    /// <summary>Invariant wire string of a stored row.</summary>
    public static string Format(ISignedContentRow row, IReadOnlyDictionary<Guid, FieldOption> optionsById)
    {
        ArgumentNullException.ThrowIfNull(optionsById);
        return row switch
        {
            FieldStringValue v => v.StringValue,
            FieldIntegerValue v => v.IntegerValue.ToString(CultureInfo.InvariantCulture),
            FieldDecimalValue v => v.DecimalValue.ToString(CultureInfo.InvariantCulture),
            FieldDateValue v => v.DateValue.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            FieldDateTimeValue v => v.DateTimeValue.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture),
            FieldGuidValue v => v.GuidValue.ToString("D"),
            FieldBooleanValue v => v.BooleanValue ? "true" : "false",
            FieldOptionValue v => optionsById.TryGetValue(v.FieldOptionId, out var option) ? option.OptionKey : v.FieldOptionId.ToString("D"),
            _ => string.Empty,
        };
    }

    /// <summary>Required-field check after all values of a write are known (create and full-snapshot update).</summary>
    public static void RequireMandatoryFields(FormLayout layout, IReadOnlyDictionary<Guid, FieldDefinition> fieldsById, IReadOnlySet<Guid> presentFieldIds)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(fieldsById);
        ArgumentNullException.ThrowIfNull(presentFieldIds);
        var missing = layout.Elements
            .Where(e => e.Kind == LayoutElementKinds.Field && e.Validation.Required && e.FieldDefinitionId is { } id && fieldsById.ContainsKey(id) && !presentFieldIds.Contains(id))
            .Select(e => fieldsById[e.FieldDefinitionId!.Value].FieldKey)
            .ToList();
        if (missing.Count > 0)
        {
            throw new BusinessException("VALIDATION_FAILED", 422, "required fields missing: " + string.Join(", ", missing));
        }
    }

    private static string ValidateString(FieldDefinition field, LayoutValidation v, string value)
    {
        var max = v.MaxLength ?? 4000;
        if (value.Length > max || value.Length < (v.MinLength ?? 0))
        {
            throw new BusinessException("VALIDATION_FAILED", 422, $"field '{field.FieldKey}' length must be {v.MinLength ?? 0}..{max}");
        }

        if (v.Pattern is not null && !Regex.IsMatch(value, "^(?:" + v.Pattern + ")$", RegexOptions.None, TimeSpan.FromMilliseconds(100)))
        {
            throw new BusinessException("VALIDATION_FAILED", 422, $"field '{field.FieldKey}' does not match the required pattern");
        }

        return value;
    }

    private static long ValidateInteger(FieldDefinition field, LayoutValidation v, string value)
    {
        if (!long.TryParse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var parsed))
        {
            throw new BusinessException("VALIDATION_FAILED", 422, $"field '{field.FieldKey}' must be an integer");
        }

        if (parsed < (v.Minimum ?? long.MinValue) || parsed > (v.Maximum ?? long.MaxValue))
        {
            throw new BusinessException("VALIDATION_FAILED", 422, $"field '{field.FieldKey}' is outside {v.Minimum}..{v.Maximum}");
        }

        return parsed;
    }

    private static decimal ValidateDecimal(FieldDefinition field, LayoutValidation v, string value)
    {
        if (!decimal.TryParse(value, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var parsed) || decimal.Round(parsed, 4) != parsed || Math.Abs(parsed) >= 1_000_000_000_000_000m)
        {
            throw new BusinessException("VALIDATION_FAILED", 422, $"field '{field.FieldKey}' must be a decimal with at most 4 fraction digits");
        }

        if (parsed < (v.Minimum ?? decimal.MinValue) || parsed > (v.Maximum ?? decimal.MaxValue))
        {
            throw new BusinessException("VALIDATION_FAILED", 422, $"field '{field.FieldKey}' is outside {v.Minimum}..{v.Maximum}");
        }

        return parsed;
    }

    private static DateOnly ValidateDate(FieldDefinition field, LayoutValidation v, string value)
    {
        if (!DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
        {
            throw new BusinessException("VALIDATION_FAILED", 422, $"field '{field.FieldKey}' must be an ISO date (yyyy-MM-dd)");
        }

        if (parsed < (v.MinDate ?? DateOnly.MinValue) || parsed > (v.MaxDate ?? DateOnly.MaxValue))
        {
            throw new BusinessException("VALIDATION_FAILED", 422, $"field '{field.FieldKey}' is outside the permitted date range");
        }

        return parsed;
    }

    /// <summary>ISO 8601 instant with an explicit offset or Z; stored and signed as UTC at second precision (the canonical timestamp encoding is second-exact, so sub-second input is rejected rather than silently truncated).</summary>
    private static DateTimeOffset ValidateDateTime(FieldDefinition field, LayoutValidation v, string value)
    {
        if (!DateTimeOffset.TryParseExact(value, ["yyyy-MM-dd'T'HH:mm:ssK", "yyyy-MM-dd'T'HH:mmK"], CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var parsed))
        {
            throw new BusinessException("VALIDATION_FAILED", 422, $"field '{field.FieldKey}' must be an ISO 8601 instant with offset (yyyy-MM-ddTHH:mm:ssZ)");
        }

        var date = DateOnly.FromDateTime(parsed.UtcDateTime);
        if (date < (v.MinDate ?? DateOnly.MinValue) || date > (v.MaxDate ?? DateOnly.MaxValue))
        {
            throw new BusinessException("VALIDATION_FAILED", 422, $"field '{field.FieldKey}' is outside the permitted date range");
        }

        return new DateTimeOffset(parsed.UtcDateTime, TimeSpan.Zero);
    }

    private static Guid ValidateGuid(FieldDefinition field, string value)
    {
        if (!Guid.TryParseExact(value, "D", out var parsed) || parsed == Guid.Empty)
        {
            throw new BusinessException("VALIDATION_FAILED", 422, $"field '{field.FieldKey}' must be a non-empty UUID in canonical form");
        }

        return parsed;
    }

    private static bool ValidateBoolean(FieldDefinition field, string value) => value switch
    {
        "true" => true,
        "false" => false,
        _ => throw new BusinessException("VALIDATION_FAILED", 422, $"field '{field.FieldKey}' must be true or false"),
    };

    private static Guid ValidateOption(FieldDefinition field, IReadOnlyList<FieldOption> options, string value)
    {
        var option = options.FirstOrDefault(o => o.FieldDefinitionId == field.FieldDefinitionId && o.OptionKey == value);
        if (option is null)
        {
            throw new BusinessException("VALIDATION_FAILED", 422, $"field '{field.FieldKey}' has no option '{value}'");
        }

        if (!option.IsActive)
        {
            throw new BusinessException("ORGANIZATION_OPTION_DISABLED", 422, $"option '{value}' of field '{field.FieldKey}' is no longer available");
        }

        return option.FieldOptionId;
    }
}
