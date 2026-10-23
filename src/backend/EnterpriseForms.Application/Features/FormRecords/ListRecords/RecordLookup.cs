using System.Text.RegularExpressions;
using EnterpriseForms.Application.Common;

namespace EnterpriseForms.Application.Features.FormRecords.ListRecords;

/// <summary>What a quick lookup searches for: one exact record id, or one exact short reference.</summary>
public enum RecordLookupKind
{
    RecordId,
    Reference,
}

/// <summary>
/// Quick record lookup input (Phase C6). Accepts exactly two forms and nothing else, so no field value, name or partial
/// text is ever searched: a full record id (with or without hyphens or braces) or a short reference <c>REC-XXXXXXXX</c>.
/// A short reference is not stored anywhere — it is <see cref="RecordListQuery.ReferenceFor"/> of the id (its last eight
/// hexadecimal digits) — so matching needs nothing but record ids. It is also not unique: several records can share one.
/// </summary>
public sealed partial record RecordLookupQuery(RecordLookupKind Kind, Guid? FormRecordId, string? Reference)
{
    public const int MaximumInputLength = 64;

    public string Normalized => Kind == RecordLookupKind.RecordId ? FormRecordId!.Value.ToString("D") : Reference!;

    public static RecordLookupQuery Parse(string? input)
    {
        var text = (input ?? string.Empty).Trim();
        if (text.Length is > 0 and <= MaximumInputLength)
        {
            var unbraced = text.StartsWith('{') && text.EndsWith('}') ? text[1..^1] : text;
            if ((Guid.TryParseExact(unbraced, "D", out var id) || Guid.TryParseExact(unbraced, "N", out id)) && id != Guid.Empty)
            {
                return new RecordLookupQuery(RecordLookupKind.RecordId, id, null);
            }

            if (ReferencePattern().IsMatch(text))
            {
                return new RecordLookupQuery(RecordLookupKind.Reference, null, text.ToUpperInvariant());
            }
        }

        throw new BusinessException("LOOKUP_INVALID", 422, "enter a record reference such as REC-1A2B3C4D or a full record ID");
    }

    /// <summary>
    /// The authorized ids that match, in the order given. Only ids the authority listed for the caller are considered,
    /// so the result never reveals that an unauthorized record exists, or how many do.
    /// </summary>
    public IReadOnlyList<Guid> Match(IEnumerable<Guid> authorizedRecordIds)
    {
        ArgumentNullException.ThrowIfNull(authorizedRecordIds);
        return Kind == RecordLookupKind.RecordId
            ? authorizedRecordIds.Where(id => id == FormRecordId).Distinct().ToList()
            : authorizedRecordIds.Where(id => string.Equals(RecordListQuery.ReferenceFor(id), Reference, StringComparison.Ordinal)).Distinct().ToList();
    }

    [GeneratedRegex("^REC-[0-9A-F]{8}$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ReferencePattern();
}
