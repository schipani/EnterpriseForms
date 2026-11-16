// E3-M2e-2 isolation guard (owner authorization §3). Every PostgreSQL connection string the harness opens, hands to a child process or
// to a frozen-build agent passes Target.Require: host 127.0.0.1, the run's own published port, and a database this run created and
// verified (the per-run nonce in its database comment, and the cluster's system identifier recorded by the script from inside the
// container). The Vault address must be http://127.0.0.1:<the run's Vault port>. Any mismatch throws IsolationViolation, which the
// harness treats as an immediate stop: no further scenario runs and acceptance is never enabled.
using Npgsql;

internal sealed class IsolationViolation(string detail) : Exception("ISOLATION VIOLATION (stop condition): " + detail);

internal static class Target
{
    private static readonly HashSet<string> VerifiedDatabases = new(StringComparer.Ordinal);
    private static readonly object Gate = new();

    public static string Host => "127.0.0.1";

    public static int Port { get; private set; }

    public static string Nonce { get; private set; } = string.Empty;

    public static string SystemIdentifier { get; private set; } = string.Empty;

    public static int VaultPort { get; private set; }

    public static string PrimaryDatabase { get; private set; } = string.Empty;

    public static IReadOnlyCollection<string> Databases
    {
        get
        {
            lock (Gate)
            {
                return [.. VerifiedDatabases];
            }
        }
    }

    public static void Configure(int port, string nonce, string systemIdentifier, int vaultPort, string primaryDatabase)
    {
        if (port is < 1024 or > 65535 || string.IsNullOrWhiteSpace(nonce) || nonce.Length < 16 || string.IsNullOrWhiteSpace(systemIdentifier) || vaultPort is < 1024 or > 65535)
        {
            throw new IsolationViolation("the target description from the script is incomplete");
        }

        Port = port;
        Nonce = nonce;
        SystemIdentifier = systemIdentifier;
        VaultPort = vaultPort;
        PrimaryDatabase = primaryDatabase;
    }

    /// <summary>The connection string, only if it names this run's loopback port and a database this run created and verified.</summary>
    public static string Require(string connectionString)
    {
        var builder = new NpgsqlConnectionStringBuilder(connectionString);
        string[] hosts = builder.Host is null ? [] : builder.Host.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (hosts.Length != 1 || hosts[0] != Host || builder.Port != Port || builder.Database is null)
        {
            throw new IsolationViolation($"connection outside the disposable environment (host '{builder.Host}', port {builder.Port})");
        }

        lock (Gate)
        {
            if (!VerifiedDatabases.Contains(builder.Database))
            {
                throw new IsolationViolation($"database '{builder.Database}' was not created and verified by this run");
            }
        }

        return connectionString;
    }

    /// <summary>Verifies the nonce comment and the cluster identity of <paramref name="database"/> and admits it.</summary>
    public static async Task AdmitAsync(string adminConnectionString, string database)
    {
        var builder = new NpgsqlConnectionStringBuilder(adminConnectionString) { Database = database, Pooling = false };
        if (builder.Host != Host || builder.Port != Port)
        {
            throw new IsolationViolation("admission requested outside the disposable environment");
        }

        await using var connection = new NpgsqlConnection(builder.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("SELECT current_database(), coalesce(shobj_description((SELECT oid FROM pg_database WHERE datname = current_database()), 'pg_database'), ''), (SELECT system_identifier::text FROM pg_control_system()), current_setting('port')", connection);
        await using var reader = await command.ExecuteReaderAsync();
        await reader.ReadAsync();
        var (name, comment, system, port) = (reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3));
        if (name != database || comment != "m2e2-nonce:" + Nonce || system != SystemIdentifier || port != "5432")
        {
            throw new IsolationViolation($"target identity mismatch for '{database}' (name {name}, nonce {(comment == "m2e2-nonce:" + Nonce ? "ok" : "MISMATCH")}, system identifier {(system == SystemIdentifier ? "ok" : "MISMATCH")}, port {port})");
        }

        lock (Gate)
        {
            VerifiedDatabases.Add(database);
        }
    }

    /// <summary>Re-verifies every admitted database (before acceptance is enabled and at the end).</summary>
    public static async Task ReverifyAllAsync(string adminConnectionString)
    {
        foreach (var database in Databases)
        {
            await AdmitAsync(adminConnectionString, database);
        }
    }

    public static string RequireVault(string address)
    {
        if (!Uri.TryCreate(address, UriKind.Absolute, out var uri) || uri.Scheme != "http" || uri.Host != Host || uri.Port != VaultPort)
        {
            throw new IsolationViolation("Vault address outside the disposable environment");
        }

        return address;
    }

    /// <summary>
    /// Removes inherited settings that could redirect a child process (the Authority host, the migration and floor commands, the
    /// frozen-build agents) away from this run's containers or enable a feature by inheritance. Returns the names removed.
    /// </summary>
    public static IReadOnlyList<string> ScrubProcessEnvironment()
    {
        string[] prefixes = ["ConnectionStrings__", "Content__", "ContentAcceptance__", "AZURE_", "Azure__", "ASPNETCORE_", "DOTNET_ENVIRONMENT", "SecurityArchive__", "Signing__", "SpiceDb__", "Identity__", "SearchIndex__", "SearchAccess__", "FaultInjection__", "Encryption__", "E2_VAULT", "E4_VAULT", "VAULT_", "PGHOST", "PGPORT", "PGDATABASE", "PGUSER", "PGPASSWORD", "PGSERVICE"];
        var removed = new List<string>();
        foreach (var key in Environment.GetEnvironmentVariables().Keys.Cast<string>().Order(StringComparer.Ordinal))
        {
            if (prefixes.Any(p => key.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
            {
                Environment.SetEnvironmentVariable(key, null);
                removed.Add(key);
            }
        }

        return removed;
    }

    public static IReadOnlyList<string> ForbiddenEnvironmentRemaining()
    {
        string[] prefixes = ["ConnectionStrings__", "Content__", "ContentAcceptance__", "AZURE_", "Azure__", "ASPNETCORE_", "DOTNET_ENVIRONMENT"];
        return [.. Environment.GetEnvironmentVariables().Keys.Cast<string>().Where(k => prefixes.Any(p => k.StartsWith(p, StringComparison.OrdinalIgnoreCase))).Order(StringComparer.Ordinal)];
    }
}
