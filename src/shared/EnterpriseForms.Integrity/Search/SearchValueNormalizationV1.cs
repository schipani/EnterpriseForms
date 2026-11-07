using System.Text;

namespace EnterpriseForms.Integrity.Search;

/// <summary>
/// IX-1 (indexed search, Option C): the one string normalization used to derive indexed string values. The Phase C7
/// evaluator compares <c>Normalize(NFC).Trim()</c> strings with <see cref="StringComparison.OrdinalIgnoreCase"/>; storing the
/// invariant upper-case of the same NFC-trimmed string lets the index compare byte-exact under PostgreSQL <c>COLLATE "C"</c>
/// with the same equality, prefix and substring semantics (design R1 §B.4). Computed in .NET only, never by PostgreSQL case
/// functions. Versioned: any change is a new derivation version, never a silent edit.
/// </summary>
public static class SearchValueNormalizationV1
{
    public const string Version = "SearchValueNormalization/v1";

    public static string NormalizeString(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return value.Normalize(NormalizationForm.FormC).Trim().ToUpperInvariant();
    }
}
