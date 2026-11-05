using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace EnterpriseForms.SecurityTests.Harness;

/// <summary>
/// Drives the compose.security-test.yaml stack through the Docker CLI: hostile SQL via the attacker container (BusinessDb
/// admin credentials, business-net only), fault injection by pausing/stopping services, and image fingerprints for evidence.
/// </summary>
public sealed class ComposeStack
{
    public static readonly string RepositoryRoot = Locate();
    public static readonly string DockerDirectory = Path.Combine(RepositoryRoot, "infrastructure", "docker");

    public string Compose(string arguments, int timeoutSeconds = 180, bool throwOnError = true)
    {
        var envFile = File.Exists(Path.Combine(DockerDirectory, ".env")) ? ".env" : "env.example";
        // Local workstations only: an extra overlay the stack was started with (e.g. pinned subnets), so that recreating one
        // service does not fight the running stack's networks. Unset on hosted runners.
        var overlay = Environment.GetEnvironmentVariable("SECTEST_EXTRA_COMPOSE_FILE") is { Length: > 0 } extra ? $" -f \"{extra}\"" : string.Empty;
        return Run("docker", $"compose -f compose.yaml -f compose.security-test.yaml{overlay} --env-file {envFile} {arguments}", timeoutSeconds, throwOnError);
    }

    /// <summary>Executes SQL as the hostile BusinessDb administrator from the attacker container (never from the host).</summary>
    public string AttackerSql(string sql, bool throwOnError = true)
    {
        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(sql));
        return Compose($"exec -T attacker-sql sh -c \"echo {encoded} | base64 -d | psql -v ON_ERROR_STOP=1 -qAt\"", 60, throwOnError);
    }

    /// <summary>
    /// Test oracle only (Phase C6 M0): reads the independently administered AuthorityDb in a read-only transaction
    /// (<c>default_transaction_read_only</c>), inside the authority-db container, with the Authority's <c>authority</c> schema on the search path. It plays no attacker role and is never used
    /// by product code; it lets tests compare business-database estimates with what the Authority actually accepted, instead
    /// of checking an estimate against itself.
    /// </summary>
    public string AuthorityObserverSql(string sql)
    {
        var encodedSql = Convert.ToBase64String(Encoding.UTF8.GetBytes(sql));
        var shell = $"echo {encodedSql} | base64 -d | PGPASSWORD=\"$POSTGRES_PASSWORD\" PGOPTIONS='-c default_transaction_read_only=on -c search_path=authority' psql -U \"$POSTGRES_USER\" -d \"$POSTGRES_DB\" -v ON_ERROR_STOP=1 -qAt";
        var encodedShell = Convert.ToBase64String(Encoding.UTF8.GetBytes(shell));
        return Compose($"exec -T authority-db sh -c \"echo {encodedShell} | base64 -d | sh\"", 60);
    }

    /// <summary>
    /// C8 M2 restore simulation only: writes AuthorityDb as its administrator inside the authority-db container, to reproduce what a
    /// database restore from an earlier backup leaves behind (platform journal rows and registry state from before an event). Never
    /// used by product code; exists only on the isolated, disposable security-test stack.
    /// </summary>
    public string AuthorityRestoreSimulationSql(string sql)
    {
        var encodedSql = Convert.ToBase64String(Encoding.UTF8.GetBytes(sql));
        var shell = $"echo {encodedSql} | base64 -d | PGPASSWORD=\"$POSTGRES_PASSWORD\" PGOPTIONS='-c search_path=authority' psql -U \"$POSTGRES_USER\" -d \"$POSTGRES_DB\" -v ON_ERROR_STOP=1 -qAt";
        var encodedShell = Convert.ToBase64String(Encoding.UTF8.GetBytes(shell));
        return Compose($"exec -T authority-db sh -c \"echo {encodedShell} | base64 -d | sh\"", 60);
    }

    /// <summary>
    /// C8 M4 T16-S (C-113) restore simulation only: runs a shell inside the spicedb-db container as its administrator, to
    /// reproduce what restoring SpiceDB's datastore from an earlier backup leaves behind (pg_dump / pg_restore of the
    /// <c>spicedb</c> database). Never used by product code; exists only on the isolated, disposable security-test stack.
    /// </summary>
    public string SpiceDbDatastoreSimulationShell(string shell, int timeoutSeconds = 120)
    {
        var prefixed = "export PGUSER=\"$POSTGRES_USER\" PGPASSWORD=\"$POSTGRES_PASSWORD\" PGDATABASE=spicedb; " + shell;
        var encodedShell = Convert.ToBase64String(Encoding.UTF8.GetBytes(prefixed));
        return Compose($"exec -T spicedb-db sh -c \"echo {encodedShell} | base64 -d | sh\"", timeoutSeconds);
    }

    /// <summary>
    /// C8 M4 S1 restore simulation only: replaces SpiceDB's datastore by a fresh, migrated, empty one (SpiceDB stopped, database
    /// dropped and recreated, <c>spicedb-migrate</c> run, SpiceDB started), as an operator replacing the datastore would leave it.
    /// Security-test stack only.
    /// </summary>
    public void ReplaceSpiceDbDatastoreWithEmpty()
    {
        Stop("spicedb");
        SpiceDbAdministrationShell("psql -d postgres -v ON_ERROR_STOP=1 -qAt -c 'DROP DATABASE spicedb' -c 'CREATE DATABASE spicedb' && echo RECREATED");
        Compose("run --rm spicedb-migrate", 180);
        Compose("up -d --wait spicedb", 180);
    }

    /// <summary>
    /// C8 M4 S1 restore simulation only: restores <paramref name="dumpFile"/> (inside <c>spicedb-db</c>) into SpiceDB's datastore.
    /// <paramref name="mode"/>: "restart" stops SpiceDB around a <c>pg_restore --clean</c>; "live" restores under a running SpiceDB;
    /// "recreate" stops SpiceDB, drops and recreates the database and restores the whole dump into it.
    /// </summary>
    public void RestoreSpiceDbDatastore(string dumpFile, string mode)
    {
        switch (mode)
        {
            case "live":
                SpiceDbDatastoreSimulationShell($"pg_restore --clean --if-exists --no-owner -d spicedb {dumpFile} >/dev/null 2>&1; echo RESTORED", 180);
                return;
            case "restart":
            case "recreate":
                try
                {
                    Stop("spicedb");
                    if (mode == "recreate")
                    {
                        SpiceDbAdministrationShell("psql -d postgres -v ON_ERROR_STOP=1 -qAt -c 'DROP DATABASE spicedb' -c 'CREATE DATABASE spicedb' && echo RECREATED");
                        SpiceDbDatastoreSimulationShell($"pg_restore --no-owner -d spicedb {dumpFile} >/dev/null 2>&1; echo RESTORED", 180);
                    }
                    else
                    {
                        SpiceDbDatastoreSimulationShell($"pg_restore --clean --if-exists --no-owner -d spicedb {dumpFile} >/dev/null 2>&1; echo RESTORED", 180);
                    }
                }
                finally
                {
                    Compose("up -d --wait spicedb", 180);
                }

                return;
            default:
                throw new ArgumentOutOfRangeException(nameof(mode), mode, "live, restart or recreate");
        }
    }

    /// <summary>
    /// C8 M4 S1: runs the SpiceDB behavioural-contract probe (tests/integration/EnterpriseForms.SpiceDbContractProbe, built output in
    /// <paramref name="probeDirectory"/>) in a throwaway container on this stack's authority network, where SpiceDB is reachable.
    /// The probe writes only relationships of a random organization the Authority does not know. Security-test stack only.
    /// </summary>
    public string SpiceDbProbe(string probeDirectory, string arguments, int timeoutSeconds = 300, bool throwOnError = true)
    {
        var container = Compose("ps -q spicedb").Trim();
        var networks = Run("docker", $"inspect --format \"{{{{range $k, $v := .NetworkSettings.Networks}}}}{{{{$k}}}} {{{{end}}}}\" {container}", 30);
        var network = networks.Split(' ', StringSplitOptions.RemoveEmptyEntries).Single(n => n.EndsWith("authority-net", StringComparison.Ordinal));
        var key = EnvironmentFileValue("SPICEDB_PRESHARED_KEY");
        return Run("docker", $"run --rm --network {network} -e SPICEDB_KEY={key} -v \"{probeDirectory}:/probe:ro\" mcr.microsoft.com/dotnet/aspnet:10.0 dotnet /probe/EnterpriseForms.SpiceDbContractProbe.dll {arguments}", timeoutSeconds, throwOnError);
    }

    private string SpiceDbAdministrationShell(string shell)
    {
        var prefixed = "export PGUSER=\"$POSTGRES_USER\" PGPASSWORD=\"$POSTGRES_PASSWORD\"; " + shell;
        var encodedShell = Convert.ToBase64String(Encoding.UTF8.GetBytes(prefixed));
        return Compose($"exec -T spicedb-db sh -c \"echo {encodedShell} | base64 -d | sh\"", 120);
    }

    private static string EnvironmentFileValue(string name)
    {
        var file = File.Exists(Path.Combine(DockerDirectory, ".env")) ? ".env" : "env.example";
        var line = File.ReadAllLines(Path.Combine(DockerDirectory, file)).FirstOrDefault(l => l.StartsWith(name + "=", StringComparison.Ordinal))
                   ?? throw new InvalidOperationException(name + " is not set in " + file);
        return line[(name.Length + 1)..].Trim();
    }

    public string AttackerShell(string shell, bool throwOnError = true, int timeoutSeconds = 60)
    {
        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(shell));
        return Compose($"exec -T attacker-sql sh -c \"echo {encoded} | base64 -d | sh\"", timeoutSeconds, throwOnError);
    }

    public void Pause(string service) => Compose($"pause {service}");

    public void Unpause(string service) => Compose($"unpause {service}");

    public void Stop(string service) => Compose($"stop {service}");

    public void Start(string service) => Compose($"start {service}");

    public string Logs(string service, int tail = 200) => Compose($"logs --no-color --tail {tail.ToString(CultureInfo.InvariantCulture)} {service}", 60, throwOnError: false);

    public string ImageFingerprint()
    {
        var ids = new List<string>();
        foreach (var service in new[] { "api", "authority" })
        {
            var containerId = Compose($"ps -q {service}").Trim();
            if (containerId.Length == 0)
            {
                throw new InvalidOperationException($"service {service} is not running");
            }

            ids.Add(service + "=" + Run("docker", $"inspect --format \"{{{{.Image}}}}\" {containerId}", 30).Trim());
        }

        ids.Sort(StringComparer.Ordinal);
        var digest = System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(string.Join(";", ids)));
        return "sha256:" + Convert.ToHexStringLower(digest) + " (" + string.Join(";", ids) + ")";
    }

    public static string GitCommit() => Run("git", "rev-parse HEAD", 30).Trim();

    public static string GitStatusShort() => Run("git", "status --porcelain", 30);

    public static string Run(string fileName, string arguments, int timeoutSeconds, bool throwOnError = true)
    {
        var psi = new ProcessStartInfo(fileName, arguments)
        {
            WorkingDirectory = fileName == "git" ? RepositoryRoot : DockerDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var process = Process.Start(psi) ?? throw new InvalidOperationException("failed to start " + fileName);
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(TimeSpan.FromSeconds(timeoutSeconds)))
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException($"{fileName} {arguments} timed out after {timeoutSeconds}s");
        }

        var output = stdout.GetAwaiter().GetResult();
        var error = stderr.GetAwaiter().GetResult();
        if (process.ExitCode != 0 && throwOnError)
        {
            throw new InvalidOperationException($"{fileName} {arguments} failed ({process.ExitCode}): {error}\n{output}");
        }

        return output + (process.ExitCode != 0 ? "\n[stderr] " + error : string.Empty);
    }

    private static string Locate()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "global.json")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Repository root (global.json) not found above test output directory.");
    }
}
