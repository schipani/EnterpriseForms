using Npgsql;

namespace AuthorizationAuthority.Infrastructure.Persistence;

/// <summary>
/// C-188 compatibility fence (D-H11): <c>authority.field_search_approvals</c> carries a row-level security policy that hides
/// RetainedField approvals from any session that has not declared <c>authority.field_search_scope_aware = on</c>. This build declares
/// it on every connection it opens to read or change approvals (the runtime and the search reader); an Authority binary built before
/// C-188 never does, so it cannot see — and therefore never lists, honours, indexes, converts or updates — a retained approval: it
/// fails closed. The setting is declared per connection (startup option), never as a role default, so it cannot leak to an older
/// binary that uses the same login. It is a compatibility fence between Authority builds, not a defence against a SQL-capable
/// attacker (who could set it), which the existing Authority trust boundary (G08) covers.
/// </summary>
public static class FieldSearchScopeConnection
{
    public const string SettingName = "authority.field_search_scope_aware";

    private const string Option = "-c " + SettingName + "=on";

    /// <summary>Returns the connection string with the startup option added (kept once); null or blank stays as it is.</summary>
    public static string? WithScopeAwareness(string? connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return connectionString;
        }

        var builder = new NpgsqlConnectionStringBuilder(connectionString);
        var current = builder.Options;
        if (!string.IsNullOrWhiteSpace(current) && current.Contains(SettingName, StringComparison.Ordinal))
        {
            return builder.ConnectionString;
        }

        builder.Options = string.IsNullOrWhiteSpace(current) ? Option : current + " " + Option;
        return builder.ConnectionString;
    }
}
