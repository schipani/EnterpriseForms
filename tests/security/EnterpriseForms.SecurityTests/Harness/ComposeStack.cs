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
        return Run("docker", $"compose -f compose.yaml -f compose.security-test.yaml --env-file {envFile} {arguments}", timeoutSeconds, throwOnError);
    }

    /// <summary>Executes SQL as the hostile BusinessDb administrator from the attacker container (never from the host).</summary>
    public string AttackerSql(string sql, bool throwOnError = true)
    {
        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(sql));
        return Compose($"exec -T attacker-sql sh -c \"echo {encoded} | base64 -d | psql -v ON_ERROR_STOP=1 -qAt\"", 60, throwOnError);
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
