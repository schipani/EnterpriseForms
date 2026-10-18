using System.Text.RegularExpressions;

namespace EnterpriseForms.Infrastructure.Persistence.Conventions;

public sealed record NamingViolation(string ObjectName, string RuleCode, string Detail);

/// <summary>
/// CLAUDE.md §0 rules 4 and 5: unquoted lowercase snake_case; every non-key column has at least two meaningful
/// words; no single-word columns; surrogate primary keys are <c>&lt;singular table&gt;_id</c>; foreign keys reuse the
/// referenced primary key name. Enforced over the EF model by the architecture tests after every migration.
/// </summary>
public static partial class DatabaseColumnNamingRules
{
    /// <summary>Words that are never acceptable as a whole column name (the normative examples plus obvious generics).</summary>
    public static readonly IReadOnlySet<string> ForbiddenSingleWords = new HashSet<string>(StringComparer.Ordinal)
    {
        "name", "email", "value", "status", "type", "label", "description", "revision", "action", "reason", "title", "key",
        "data", "code", "text", "date", "time", "count", "order", "note", "notes", "number", "state", "kind", "version", "id",
    };

    [GeneratedRegex("^[a-z][a-z0-9]*(_[a-z0-9]+)*$")]
    private static partial Regex SnakeCasePattern();

    public static bool IsSnakeCase(string identifier) => !string.IsNullOrEmpty(identifier) && SnakeCasePattern().IsMatch(identifier);

    /// <summary>Returns null when the column name is permitted.</summary>
    public static NamingViolation? EvaluateColumn(string tableName, string columnName, bool isPrimaryOrForeignKey)
    {
        if (!IsSnakeCase(columnName))
        {
            return new NamingViolation($"{tableName}.{columnName}", "NAME-SNAKE", "column must be lowercase snake_case without quoting");
        }

        var words = columnName.Split('_');
        if (isPrimaryOrForeignKey)
        {
            return words.Length >= 2 && words[^1] == "id"
                ? null
                : new NamingViolation($"{tableName}.{columnName}", "KEY-SUFFIX", "key columns must be <entity>_id");
        }

        if (words.Length < 2)
        {
            return new NamingViolation($"{tableName}.{columnName}", "NAME-SINGLE-WORD", "non-key columns need at least two meaningful words");
        }

        if (ForbiddenSingleWords.Contains(columnName))
        {
            return new NamingViolation($"{tableName}.{columnName}", "NAME-FORBIDDEN", "generic column name");
        }

        return null;
    }

    /// <summary>Surrogate PK name must equal the singular table name plus <c>_id</c>.</summary>
    public static NamingViolation? EvaluatePrimaryKey(string tableName, string primaryKeyColumn)
    {
        var expected = Singularize(tableName) + "_id";
        return string.Equals(expected, primaryKeyColumn, StringComparison.Ordinal)
            ? null
            : new NamingViolation($"{tableName}.{primaryKeyColumn}", "PK-NAME", $"expected {expected}");
    }

    /// <summary>Deterministic English singularization for table names (ies→y, *sses/*xes/*ches/*shes → drop es, trailing s dropped).</summary>
    public static string Singularize(string pluralSnakeCase)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluralSnakeCase);
        if (pluralSnakeCase.EndsWith("ies", StringComparison.Ordinal))
        {
            return pluralSnakeCase[..^3] + "y";
        }

        if (pluralSnakeCase.EndsWith("sses", StringComparison.Ordinal) || pluralSnakeCase.EndsWith("xes", StringComparison.Ordinal)
            || pluralSnakeCase.EndsWith("ches", StringComparison.Ordinal) || pluralSnakeCase.EndsWith("shes", StringComparison.Ordinal))
        {
            return pluralSnakeCase[..^2];
        }

        if (pluralSnakeCase.EndsWith('s') && !pluralSnakeCase.EndsWith("ss", StringComparison.Ordinal))
        {
            return pluralSnakeCase[..^1];
        }

        return pluralSnakeCase;
    }
}
