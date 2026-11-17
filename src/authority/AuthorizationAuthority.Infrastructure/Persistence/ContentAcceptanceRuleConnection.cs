using AuthorizationAuthority.Application.Features.Content;
using Npgsql;

namespace AuthorizationAuthority.Infrastructure.Persistence;

/// <summary>
/// E3-M2b-1 (§19.9 step 3): Authority store connections carry the build's content acceptance rule version in
/// <c>application_name</c> (for example <c>authority carv-v1</c>), so <c>pg_stat_activity</c> shows whether any pre-CARV instance
/// (no marker) is still connected before the floor migration. Informational for the operator; never an enforcement input.
/// </summary>
public static partial class ContentAcceptanceRuleConnection
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

        // E3-M2d: a configured name may carry another build's marker (for example a stale "carv-v1" under a version-2 build). It is
        // replaced, never kept beside this build's marker, so the floor-activation precondition reads the marker of the running build.
        builder.ApplicationName = string.IsNullOrWhiteSpace(current) ? "authority " + Marker
            : MarkerPattern().IsMatch(current) ? MarkerPattern().Replace(current, Marker) : current + " " + Marker;
        return builder.ConnectionString;
    }

    [System.Text.RegularExpressions.GeneratedRegex("carv-v[0-9]+")]
    private static partial System.Text.RegularExpressions.Regex MarkerPattern();
}
