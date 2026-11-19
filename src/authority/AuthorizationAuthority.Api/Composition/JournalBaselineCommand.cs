using System.Text.Json;
using AuthorizationAuthority.Application.Common;
using AuthorizationAuthority.Application.Features.Journal.Reconciliation;
using AuthorizationAuthority.Application.Features.Journal.Verification;

namespace AuthorizationAuthority.Api.Composition;

/// <summary>
/// SEC-BL-01 WP-3 (C-155): Development/Testing operator command for one baseline reconciliation, run on the composed host, then exit.
/// <code>
/// --journal-baseline --organization &lt;uuid&gt; --stream audit|authorization|content --report &lt;new file&gt;
///   [--operator &lt;label&gt;] [--source-commit &lt;sha&gt;] [--exceptions &lt;owner dispositions json&gt;] [--create-sentinel] [--create-checkpoint] [--rebaseline]
/// </code>
/// Read-only on AuthorityDb. The only writes are the ones explicitly requested: the create-only archive sentinel, create-only
/// accepted-exception records for owner dispositions (WP-4A, A-1) and checkpoint 1 of a first baseline or, with <c>--rebaseline</c>, the
/// next checkpoint of the existing chain (A-1: a re-baseline continues the chain; it never restarts or overwrites it). The report file must not exist (never overwritten). Exit codes: 0 CLEAN or EXCEPTION_ACCEPTED, 2 refused or invalid
/// arguments, 3 not composed (checkpoint store not configured), 4 FAILED, 5 INCONCLUSIVE.
/// </summary>
public static class JournalBaselineCommand
{
    public const string Flag = "--journal-baseline";

    private static readonly HashSet<string> ValueOptions = new(StringComparer.Ordinal) { "--organization", "--stream", "--report", "--operator", "--source-commit", "--exceptions" };
    private static readonly HashSet<string> SwitchOptions = new(StringComparer.Ordinal) { "--create-sentinel", "--create-checkpoint", "--rebaseline" };

    public sealed record Arguments(Guid OrganizationId, string StreamName, string ReportPath, string OperatorLabel, string? SourceCommit, string? ExceptionsPath, bool CreateSentinel, bool CreateCheckpoint,
        bool Rebaseline = false);

    public static bool Requested(string[] args, out Arguments? arguments, out string[] hostArguments)
    {
        ArgumentNullException.ThrowIfNull(args);
        arguments = null;
        hostArguments = args;
        if (Array.IndexOf(args, Flag) < 0)
        {
            return false;
        }

        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        var switches = new HashSet<string>(StringComparer.Ordinal);
        var remaining = new List<string>();
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] == Flag)
            {
                continue;
            }

            if (ValueOptions.Contains(args[i]))
            {
                if (i + 1 < args.Length)
                {
                    values[args[i]] = args[i + 1];
                }

                i++;
                continue;
            }

            if (SwitchOptions.Contains(args[i]))
            {
                switches.Add(args[i]);
                continue;
            }

            remaining.Add(args[i]);
        }

        hostArguments = [.. remaining];
        if (Guid.TryParseExact(values.GetValueOrDefault("--organization"), "D", out var organization)
            && values.GetValueOrDefault("--stream") is { } stream && ArchiveCompleteness.KnownStreams.Contains(stream, StringComparer.Ordinal)
            && values.GetValueOrDefault("--report") is { Length: > 0 } report)
        {
            arguments = new Arguments(organization, stream, report, values.GetValueOrDefault("--operator") ?? "operator-unspecified", values.GetValueOrDefault("--source-commit"),
                values.GetValueOrDefault("--exceptions"), switches.Contains("--create-sentinel"), switches.Contains("--create-checkpoint"), switches.Contains("--rebaseline"));
        }

        return true;
    }

    public static int Run(IServiceProvider services, IHostEnvironment environment, Arguments? arguments)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(environment);
        if (!environment.IsDevelopment() && !environment.IsEnvironment("Testing"))
        {
            Console.Error.WriteLine($"{Flag} is a Development/Testing command; refused in {environment.EnvironmentName}.");
            return 2;
        }

        if (arguments is null)
        {
            Console.Error.WriteLine($"{Flag} requires --organization <uuid>, --stream audit|authorization|content and --report <new file>.");
            return 2;
        }

        if (File.Exists(arguments.ReportPath))
        {
            Console.Error.WriteLine("the report file already exists; reports are never overwritten.");
            return 2;
        }

        if (services.GetService<JournalBaselineService>() is not { } baseline)
        {
            Console.Error.WriteLine("journal verification is not composed (configure JournalVerificationCheckpointStore).");
            return 3;
        }

        IReadOnlyList<JournalBaselineExceptionDisposition> dispositions = [];
        if (arguments.ExceptionsPath is { } path)
        {
            using var stream = File.OpenRead(path);
            dispositions = JsonSerializer.Deserialize<List<JournalBaselineExceptionDisposition>>(stream, CanonicalJson.Options) ?? [];
        }

        var options = services.GetRequiredService<JournalVerifierOptions>();
        var clock = services.GetRequiredService<TimeProvider>();
        var request = new JournalBaselineRequest(arguments.OrganizationId, arguments.StreamName, Guid.CreateVersion7(clock.GetUtcNow()), arguments.OperatorLabel, options.VerifierIdentityLabel,
            typeof(JournalBaselineService).Assembly.GetName().Version?.ToString() ?? "unknown", arguments.SourceCommit, arguments.CreateSentinel, arguments.CreateCheckpoint, dispositions,
            options.DeliveryLagThreshold, arguments.Rebaseline);
        var report = baseline.RunAsync(request, CancellationToken.None).GetAwaiter().GetResult();
        using (var file = new FileStream(arguments.ReportPath, FileMode.CreateNew, FileAccess.Write))
        {
            file.Write(JournalBaselineService.Serialize(report));
        }

        Console.WriteLine($"journal baseline {report.Outcome} ({report.StreamName}); checkpoint {report.CheckpointAction}; findings {report.Findings.Count}; report {arguments.ReportPath}");
        return report.Outcome switch
        {
            nameof(JournalBaselineOutcome.Clean) or nameof(JournalBaselineOutcome.ExceptionAccepted) => 0,
            nameof(JournalBaselineOutcome.Failed) => 4,
            _ => 5,
        };
    }
}
