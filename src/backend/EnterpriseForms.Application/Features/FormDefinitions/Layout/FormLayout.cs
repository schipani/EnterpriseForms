using System.Text.Json;
using System.Text.Json.Serialization;
using EnterpriseForms.Application.Common;

namespace EnterpriseForms.Application.Features.FormDefinitions.Layout;

/// <summary>
/// Layout schema v2 (CLAUDE.md §4, §4I, §4S): sections of elements on a four-column logical grid. Elements are
/// <c>field</c> (stable field id, control, validation), <c>relatedFormList</c> (stable relationship id) or
/// <c>documentCollection</c> (File field, upload limits). Deterministic serialization; validated at the application
/// boundary — JSONB storage is not the contract. No executable content of any kind is accepted.
/// </summary>
public sealed record FormLayout(int LayoutSchemaVersion, string Title, IReadOnlyList<LayoutSection> Sections)
{
    public const int CurrentSchemaVersion = 2;

    public IEnumerable<LayoutElement> Elements => Sections.SelectMany(s => s.Elements);

    public IEnumerable<LayoutElement> FieldElements => Elements.Where(e => e.Kind is LayoutElementKinds.Field or LayoutElementKinds.DocumentCollection);
}

/// <summary>
/// A titled section ("row" in the designer, C1 designer rebuild) with its own four-column grid. <see cref="Description"/>
/// is optional presentation text, omitted from the serialized layout when absent so layouts written before it existed
/// serialize byte-for-byte as before.
/// </summary>
public sealed record LayoutSection(string SectionKey, string Title, int GridColumns, IReadOnlyList<LayoutElement> Elements, string? Description = null);

public static class LayoutElementKinds
{
    public const string Field = "field";
    public const string RelatedFormList = "relatedFormList";
    public const string DocumentCollection = "documentCollection";
}

public sealed record LayoutPlacement(int ColumnSpan, bool StartOnNewRow);

/// <summary>Validation rules carried in the immutable version (subset for Stage B). Server-enforced on every write.</summary>
public sealed record LayoutValidation(bool Required, int? MinLength, int? MaxLength, string? Pattern, decimal? Minimum, decimal? Maximum, DateOnly? MinDate, DateOnly? MaxDate);

/// <summary>Published declaration of a field's classification and storage protection at publish time (ADR-PB-004: immutable per version; the catalog may only be strengthened afterwards).</summary>
public sealed record LayoutProtection(string Classification, string ProtectionMode);

public sealed record LayoutUploadPolicy(bool AllowMultiple, int MaxFiles, long MaxFileSizeBytes, IReadOnlyList<string> AllowedContentTypes);

public sealed record LayoutElement(
    string Kind,
    Guid? FieldDefinitionId,
    Guid? FormRelationshipDefinitionId,
    string? Control,
    string Label,
    string? HelpText,
    LayoutPlacement Layout,
    LayoutValidation Validation,
    LayoutUploadPolicy? Upload,
    IReadOnlyList<string>? SummaryFieldKeys,
    LayoutProtection? Protection = null,
    string? Placeholder = null);

public static class FormLayoutCodec
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = false, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };
    private static readonly HashSet<string> Controls = new(StringComparer.Ordinal) { "text", "textarea", "number", "decimal", "date", "dateTime", "guid", "checkbox", "select", "fileUpload" };
    private static readonly HashSet<string> Spacing = new(StringComparer.Ordinal) { "none", "small", "medium", "large" };

    public static FormLayout Parse(string layoutJson)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(layoutJson);
        if (layoutJson.Length > 1_000_000)
        {
            throw new BusinessException("LAYOUT_INVALID", 422, "layout exceeds 1 MB");
        }

        using var document = JsonDocument.Parse(layoutJson);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new BusinessException("LAYOUT_INVALID", 422, "layout must be an object");
        }

        var schemaVersion = root.TryGetProperty("layoutSchemaVersion", out var sv) && sv.ValueKind == JsonValueKind.Number ? sv.GetInt32() : 0;
        if (schemaVersion is not (1 or 2))
        {
            throw new BusinessException("LAYOUT_INVALID", 422, "layoutSchemaVersion must be 1 or 2");
        }

        var title = root.TryGetProperty("title", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString()! : string.Empty;
        var sections = new List<LayoutSection>();
        var sectionKeys = new HashSet<string>(StringComparer.Ordinal);
        var placedFields = new HashSet<Guid>();
        var placedRelationships = new HashSet<Guid>();
        if (!root.TryGetProperty("sections", out var sectionsElement) || sectionsElement.ValueKind != JsonValueKind.Array || sectionsElement.GetArrayLength() is 0 or > 50)
        {
            throw new BusinessException("LAYOUT_INVALID", 422, "1..50 sections are required");
        }

        foreach (var section in sectionsElement.EnumerateArray())
        {
            var key = RequiredString(section, "sectionKey", 64);
            if (!sectionKeys.Add(key))
            {
                throw new BusinessException("LAYOUT_INVALID", 422, $"duplicate sectionKey '{key}'");
            }

            var gridColumns = section.TryGetProperty("gridColumns", out var gc) && gc.ValueKind == JsonValueKind.Number ? gc.GetInt32() : 4;
            if (gridColumns != 4)
            {
                throw new BusinessException("LAYOUT_INVALID", 422, "gridColumns must be 4 (CLAUDE.md §4I)");
            }

            if (section.TryGetProperty("layout", out var sectionLayout) && sectionLayout.ValueKind == JsonValueKind.Object)
            {
                foreach (var name in new[] { "columnGap", "rowGap" })
                {
                    if (sectionLayout.TryGetProperty(name, out var gap) && (gap.ValueKind != JsonValueKind.String || !Spacing.Contains(gap.GetString()!)))
                    {
                        throw new BusinessException("LAYOUT_INVALID", 422, $"unknown spacing token for {name}");
                    }
                }
            }

            var elements = new List<LayoutElement>();
            if (section.TryGetProperty("elements", out var elementsElement) && elementsElement.ValueKind == JsonValueKind.Array)
            {
                if (elementsElement.GetArrayLength() > 250)
                {
                    throw new BusinessException("LAYOUT_INVALID", 422, "a section may contain at most 250 elements");
                }

                foreach (var element in elementsElement.EnumerateArray())
                {
                    var parsed = ParseElement(element);
                    if (parsed.FieldDefinitionId is { } fieldId && !placedFields.Add(fieldId))
                    {
                        throw new BusinessException("LAYOUT_INVALID", 422, "a field may be placed only once");
                    }

                    if (parsed.FormRelationshipDefinitionId is { } relId && !placedRelationships.Add(relId))
                    {
                        throw new BusinessException("LAYOUT_INVALID", 422, "a related form list may be placed only once");
                    }

                    elements.Add(parsed);
                }
            }

            sections.Add(new LayoutSection(key, OptionalString(section, "title", 200) ?? key, gridColumns, elements, OptionalString(section, "description", 1000)));
        }

        return new FormLayout(schemaVersion, title, sections);
    }

    public static string Serialize(FormLayout layout)
    {
        ArgumentNullException.ThrowIfNull(layout);
        var model = new
        {
            layoutSchemaVersion = layout.LayoutSchemaVersion,
            title = layout.Title,
            sections = layout.Sections.Select(s => new
            {
                sectionKey = s.SectionKey,
                title = s.Title,
                description = s.Description,
                gridColumns = 4,
                elements = s.Elements.Select(e => new
                {
                    kind = e.Kind,
                    fieldDefinitionId = e.FieldDefinitionId,
                    formRelationshipDefinitionId = e.FormRelationshipDefinitionId,
                    control = e.Control,
                    label = e.Label,
                    helpText = e.HelpText,
                    placeholder = e.Placeholder,
                    layout = new { columnSpan = e.Layout.ColumnSpan, startOnNewRow = e.Layout.StartOnNewRow },
                    required = e.Validation.Required,
                    validation = new { minLength = e.Validation.MinLength, maxLength = e.Validation.MaxLength, pattern = e.Validation.Pattern, minimum = e.Validation.Minimum, maximum = e.Validation.Maximum, minDate = e.Validation.MinDate, maxDate = e.Validation.MaxDate },
                    upload = e.Upload is null ? null : new { allowMultiple = e.Upload.AllowMultiple, maxFiles = e.Upload.MaxFiles, maxFileSizeBytes = e.Upload.MaxFileSizeBytes, allowedContentTypes = e.Upload.AllowedContentTypes },
                    summaryFieldKeys = e.SummaryFieldKeys,
                    protection = e.Protection is null ? null : new { classification = e.Protection.Classification, protectionMode = e.Protection.ProtectionMode },
                }).ToArray(),
            }).ToArray(),
        };
        return JsonSerializer.Serialize(model, Json);
    }

    private static LayoutElement ParseElement(JsonElement element)
    {
        var kind = RequiredString(element, "kind", 40);
        var label = OptionalString(element, "label", 200) ?? string.Empty;
        var help = OptionalString(element, "helpText", 1000);
        var placement = ParsePlacement(element);
        var validation = ParseValidation(element);
        switch (kind)
        {
            case LayoutElementKinds.Field:
            {
                var fieldId = RequiredGuid(element, "fieldDefinitionId");
                var control = OptionalString(element, "control", 40) ?? "text";
                if (!Controls.Contains(control) || control == "fileUpload")
                {
                    throw new BusinessException("LAYOUT_INVALID", 422, $"unknown control '{control}'");
                }

                return new LayoutElement(kind, fieldId, null, control, label, help, placement, validation, null, null, ParseProtection(element), OptionalString(element, "placeholder", 200));
            }

            case LayoutElementKinds.DocumentCollection:
            {
                var fieldId = RequiredGuid(element, "fieldDefinitionId");
                var upload = ParseUpload(element);
                return new LayoutElement(kind, fieldId, null, "fileUpload", label, help, placement, validation, upload, null, ParseProtection(element));
            }

            case LayoutElementKinds.RelatedFormList:
            {
                var relationshipId = RequiredGuid(element, "formRelationshipDefinitionId");
                var summary = element.TryGetProperty("summaryFieldKeys", out var sf) && sf.ValueKind == JsonValueKind.Array
                    ? sf.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).Take(6).ToList()
                    : [];
                return new LayoutElement(kind, null, relationshipId, null, label, help, placement, validation, null, summary);
            }

            default:
                throw new BusinessException("LAYOUT_INVALID", 422, $"unknown element kind '{kind}'");
        }
    }

    private static LayoutPlacement ParsePlacement(JsonElement element)
    {
        var span = 4;
        var newRow = false;
        if (element.TryGetProperty("layout", out var layout) && layout.ValueKind == JsonValueKind.Object)
        {
            if (layout.TryGetProperty("columnSpan", out var cs))
            {
                if (cs.ValueKind != JsonValueKind.Number || !cs.TryGetInt32(out span) || span is < 1 or > 4)
                {
                    throw new BusinessException("LAYOUT_INVALID", 422, "columnSpan must be 1..4");
                }
            }

            if (layout.TryGetProperty("startOnNewRow", out var nr))
            {
                if (nr.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                {
                    throw new BusinessException("LAYOUT_INVALID", 422, "startOnNewRow must be boolean");
                }

                newRow = nr.GetBoolean();
            }
        }

        return new LayoutPlacement(span, newRow);
    }

    private static LayoutProtection? ParseProtection(JsonElement element)
    {
        if (!element.TryGetProperty("protection", out var protection) || protection.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var classification = OptionalString(protection, "classification", 20) ?? string.Empty;
        var mode = OptionalString(protection, "protectionMode", 24) ?? string.Empty;
        if (classification is not ("Public" or "Internal" or "Confidential" or "Restricted") || mode is not ("Standard" or "ApplicationEncrypted"))
        {
            throw new BusinessException("LAYOUT_INVALID", 422, "protection must name a classification (Public, Internal, Confidential, Restricted) and a protectionMode (Standard, ApplicationEncrypted)");
        }

        if (classification is "Confidential" or "Restricted" && mode != "ApplicationEncrypted")
        {
            throw new BusinessException("LAYOUT_INVALID", 422, "Confidential and Restricted fields must be ApplicationEncrypted");
        }

        return new LayoutProtection(classification, mode);
    }

    private static LayoutValidation ParseValidation(JsonElement element)
    {
        var required = element.TryGetProperty("required", out var r) && r.ValueKind == JsonValueKind.True;
        int? minLength = null, maxLength = null;
        string? pattern = null;
        decimal? minimum = null, maximum = null;
        DateOnly? minDate = null, maxDate = null;
        if (element.TryGetProperty("validation", out var v) && v.ValueKind == JsonValueKind.Object)
        {
            minLength = OptionalInt(v, "minLength");
            maxLength = OptionalInt(v, "maxLength");
            pattern = OptionalString(v, "pattern", 200);
            minimum = OptionalDecimal(v, "minimum");
            maximum = OptionalDecimal(v, "maximum");
            minDate = OptionalDate(v, "minDate");
            maxDate = OptionalDate(v, "maxDate");
            if (pattern is not null)
            {
                try
                {
                    _ = new System.Text.RegularExpressions.Regex(pattern, System.Text.RegularExpressions.RegexOptions.None, TimeSpan.FromMilliseconds(100));
                }
                catch (ArgumentException)
                {
                    throw new BusinessException("LAYOUT_INVALID", 422, "pattern is not a valid regular expression");
                }
            }

            if ((minLength is < 0) || (maxLength is < 1 or > 4000) || (minLength > maxLength) || (minimum > maximum) || (minDate > maxDate))
            {
                throw new BusinessException("LAYOUT_INVALID", 422, "validation bounds are inconsistent");
            }
        }

        return new LayoutValidation(required, minLength, maxLength, pattern, minimum, maximum, minDate, maxDate);
    }

    private static LayoutUploadPolicy ParseUpload(JsonElement element)
    {
        var allowMultiple = true;
        var maxFiles = 100;
        long maxSize = 500L * 1024 * 1024;
        var types = new List<string> { "application/pdf", "image/png", "image/jpeg", "text/plain" };
        if (element.TryGetProperty("upload", out var u) && u.ValueKind == JsonValueKind.Object)
        {
            if (u.TryGetProperty("allowMultiple", out var am) && am.ValueKind is JsonValueKind.True or JsonValueKind.False)
            {
                allowMultiple = am.GetBoolean();
            }

            maxFiles = OptionalInt(u, "maxFiles") ?? maxFiles;
            if (u.TryGetProperty("maxFileSizeBytes", out var ms) && ms.ValueKind == JsonValueKind.Number)
            {
                maxSize = ms.GetInt64();
            }

            if (u.TryGetProperty("allowedContentTypes", out var ct) && ct.ValueKind == JsonValueKind.Array)
            {
                types = ct.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!.ToLowerInvariant()).Distinct(StringComparer.Ordinal).ToList();
            }
        }

        // System ceilings a designer cannot exceed (ADR-PB-009: 500 MB standard uploads, 100 documents per record).
        if (maxFiles is < 1 or > 100 || maxSize is < 1 or > 500L * 1024 * 1024 || types.Count == 0 || types.Any(x => x.Length > 127 || !x.Contains('/', StringComparison.Ordinal)))
        {
            throw new BusinessException("LAYOUT_INVALID", 422, "upload policy outside the permitted ceilings");
        }

        return new LayoutUploadPolicy(allowMultiple, allowMultiple ? maxFiles : 1, maxSize, types);
    }

    private static string RequiredString(JsonElement element, string name, int maxLength)
    {
        var value = OptionalString(element, name, maxLength);
        return string.IsNullOrWhiteSpace(value) ? throw new BusinessException("LAYOUT_INVALID", 422, $"{name} is required") : value;
    }

    private static string? OptionalString(JsonElement element, string name, int maxLength)
    {
        if (!element.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.String || value.GetString()!.Length > maxLength || value.GetString()!.Contains('<', StringComparison.Ordinal))
        {
            throw new BusinessException("LAYOUT_INVALID", 422, $"{name} must be a plain string (max {maxLength}, no markup)");
        }

        return value.GetString();
    }

    private static Guid RequiredGuid(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && Guid.TryParse(value.GetString(), out var id) ? id : throw new BusinessException("LAYOUT_INVALID", 422, $"{name} must be a UUID");

    private static int? OptionalInt(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetInt32() : null;

    private static decimal? OptionalDecimal(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetDecimal() : null;

    private static DateOnly? OptionalDate(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && DateOnly.TryParseExact(value.GetString(), "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var date) ? date : null;
}
