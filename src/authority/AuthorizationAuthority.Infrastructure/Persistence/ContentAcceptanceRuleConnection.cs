using AuthorizationAuthority.Application.Features.Content;
using Npgsql;

namespace AuthorizationAuthority.Infrastructure.Persistence;

/// <summary>
/// E3-M2b-1 (§19.9 step 3): Authority store connections carry the build's content acceptance rule version in
/// <c>application_name</c> (for example <c>authority carv-v1</c>), so <c>pg_stat_activity</c> shows whether any pre-CARV instance
/// (no marker) is still connected before the floor migration. Informational for the operator; never an enforcement input.
/// </summary>
public static class ContentAcceptanceRuleConnection
{
    public static string Marker => "carv-v" + ContentAcceptanceRules.CurrentVersion.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Returns the connection string with the marker appended to (or set as) its application name; null stays null.</summary>
    public static string? WithVersionedApplicationName(string? connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return connectionString;
        }

        var builder = new NpgsqlConnectionStringBuilder(connectionString);
        var current = builder.ApplicationName;
        builder.ApplicationName = string.IsNullOrWhiteSpace(current) ? "authority " + Marker : current.Contains(Marker, StringComparison.Ordinal) ? current : current + " " + Marker;
        return builder.ConnectionString;
    }
}
