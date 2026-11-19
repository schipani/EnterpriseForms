using System.Text.Json;
using AuthorizationAuthority.Application.Common;
using AuthorizationAuthority.Application.Features.Journal.Recovery;
using AuthorizationAuthority.Domain.Features.Journal;

namespace AuthorizationAuthority.Api.Composition;

/// <summary>
/// SEC-BL-01 G14-R2 (contract amendment A-4, §24): the Development/Testing command line of the controlled audit-stream recovery, run on the
/// composed host, then exit. There is <b>no HTTP endpoint</b>. The Authority holds recovery approver <b>public</b> keys only: approvals are
/// produced outside it, by the two approvers, and handed in with <c>decide</c>.
/// <code>
/// --journal-recovery propose --organization &lt;uuid&gt; [--stream audit|content|authorization] --strategy SR-A|SR-C1 --requester &lt;label&gt; --reason-file &lt;file&gt;
///     --investigation-file &lt;json&gt; --window-minutes &lt;1..1440&gt; [--restore-file &lt;file&gt;] [--source-commit &lt;sha&gt;] --output &lt;new file&gt;
/// --journal-recovery decide  --organization &lt;uuid&gt; --recovery-id &lt;uuid&gt; --token-file &lt;compact JWS&gt;
/// --journal-recovery execute --organization &lt;uuid&gt; --recovery-id &lt;uuid&gt; --executor &lt;label&gt;
/// --journal-recovery outcome --organization &lt;uuid&gt; --recovery-id &lt;uuid&gt;
/// --journal-recovery status  --organization &lt;uuid&gt; --recovery-id &lt;uuid&gt;
/// </code>
/// <c>--stream</c> (every verb) names a D+-enabled stream; the default is <c>audit</c>. SEC-BL-01 WP-5b (amendment A-6, §26) extends SR-A and SR-C1 to the
/// D+-enabled <c>content</c> and <c>authorization</c> streams in Development/Testing only. Exit codes: 0 done, 2 refused environment or invalid arguments,
/// 3 not composed, 4 refused or failed, 5 unavailable.
/// </summary>
public static class JournalRecoveryCommand
{
    public const string Flag = "--journal-recovery";

    private static readonly HashSet<string> Verbs = new(StringComparer.Ordinal) { "propose", "decide", "execute", "outcome", "status" };

    private static readonly HashSet<string> ValueOptions = new(StringComparer.Ordinal)
    {
        "--organization", "--recovery-id", "--strategy", "--requester", "--reason-file", "--investigation-file", "--window-minutes", "--restore-file", "--source-commit", "--output",
        "--token-file", "--executor", "--stream",
    };

    public sealed record Arguments(string Verb, IReadOnlyDictionary<string, string> Values);

    /// <summary>The investigation input (snake_case JSON). The narrative and note stay outside the records; only their digests are bound.</summary>
    public sealed record InvestigationInput(string CauseClassification, bool ContainmentAttested, string? ContainmentNote, string AnalystLabel, string Narrative);

    public static bool Requested(string[] args, out Arguments? arguments, out string[] hostArguments)
    {
        ArgumentNullException.ThrowIfNull(args);
        arguments = null;
        hostArguments = args;
        var index = Array.IndexOf(args, Flag);
        if (index < 0)
        {
            return false;
        }

        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        var remaining = new List<string>();
        string? verb = null;
        for (var i = 0; i < args.Length; i++)
        {
            if (i == index)
            {
                if (i + 1 < args.Length && Verbs.Contains(args[i + 1]))
                {
                    verb = args[++i];
                }

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

            remaining.Add(args[i]);
        }

        hostArguments = [.. remaining];
        arguments = verb is null ? null : new Arguments(verb, values);
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

        if (arguments is null || !Guid.TryParseExact(arguments.Values.GetValueOrDefault("--organization"), "D", out var organization))
        {
            Console.Error.WriteLine($"{Flag} requires a verb (propose, decide, execute, outcome, status) and --organization <uuid>.");
            return 2;
        }

        if (services.GetService<DPlusStreamRecoveryService>() is not { } recovery)
        {
            Console.Error.WriteLine("journal recovery is not composed (configure JournalVerificationCheckpointStore).");
            return 3;
        }

        var stream = arguments.Values.GetValueOrDefault("--stream") ?? JournalStreamNames.Audit;
        if (!AuthorizationAuthority.Application.Features.Journal.AuditDeliveryEnforcement.IsEnforced(stream))
        {
            Console.Error.WriteLine($"{Flag}: --stream must name a D+-enabled stream ({string.Join(", ", AuthorizationAuthority.Application.Features.Journal.AuditDeliveryEnforcement.EnforcedStreams)}).");
            return 2;
        }

        var ct = CancellationToken.None;
        Guid recoveryId = Guid.Empty;
        if (arguments.Verb != "propose" && !Guid.TryParseExact(arguments.Values.GetValueOrDefault("--recovery-id"), "D", out recoveryId))
        {
            Console.Error.WriteLine($"{Flag} {arguments.Verb} requires --recovery-id <uuid>.");
            return 2;
        }

        try
        {
            switch (arguments.Verb)
            {
                case "propose":
                    return Propose(recovery, organization, stream, arguments.Values);
                case "decide" when arguments.Values.GetValueOrDefault("--token-file") is { Length: > 0 } tokenFile:
                    return Report(recovery.RecordDecisionAsync(organization, stream, recoveryId, File.ReadAllText(tokenFile).Trim(), ct).GetAwaiter().GetResult());
                case "execute" when arguments.Values.GetValueOrDefault("--executor") is { Length: > 0 } executor:
                    return Report(recovery.ExecuteAsync(organization, stream, recoveryId, executor, ct).GetAwaiter().GetResult());
                case "outcome":
                    return Report(recovery.RecordOutcomeAsync(organization, stream, recoveryId, ct).GetAwaiter().GetResult());
                case "status":
                    var status = recovery.StatusAsync(organization, stream, recoveryId, ct).GetAwaiter().GetResult();
                    Console.WriteLine(status is null ? "unknown recovery (or records unavailable)" : CanonicalJson.Serialize(status));
                    return status is null ? 4 : 0;
                default:
                    Console.Error.WriteLine($"{Flag} {arguments.Verb}: missing arguments.");
                    return 2;
            }
        }
        catch (IOException ex)
        {
            Console.Error.WriteLine("an input file cannot be read: " + ex.Message);
            return 2;
        }
    }

    private static int Propose(DPlusStreamRecoveryService recovery, Guid organization, string stream, IReadOnlyDictionary<string, string> values)
    {
        if (values.GetValueOrDefault("--strategy") is not { } strategy || values.GetValueOrDefault("--requester") is not { } requester
            || values.GetValueOrDefault("--reason-file") is not { } reasonFile || values.GetValueOrDefault("--investigation-file") is not { } investigationFile
            || !int.TryParse(values.GetValueOrDefault("--window-minutes"), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var minutes)
            || minutes is < 1 or > 1440 || values.GetValueOrDefault("--output") is not { } output)
        {
            Console.Error.WriteLine($"{Flag} propose requires --strategy, --requester, --reason-file, --investigation-file, --window-minutes (1..1440) and --output.");
            return 2;
        }

        if (File.Exists(output))
        {
            Console.Error.WriteLine("the output file already exists; it is never overwritten.");
            return 2;
        }

        InvestigationInput? investigation;
        try
        {
            investigation = JsonSerializer.Deserialize<InvestigationInput>(File.ReadAllBytes(investigationFile), CanonicalJson.Options);
        }
        catch (JsonException ex)
        {
            Console.Error.WriteLine("the investigation file is not valid: " + ex.Message);
            return 2;
        }

        if (investigation is null)
        {
            Console.Error.WriteLine("the investigation file is empty.");
            return 2;
        }

        var restore = values.GetValueOrDefault("--restore-file") is { Length: > 0 } restoreFile ? File.ReadAllBytes(restoreFile) : null;
        var result = recovery.ProposeAsync(new DPlusRecoveryProposal(organization, stream, strategy, requester, File.ReadAllText(reasonFile), investigation.CauseClassification,
            investigation.ContainmentAttested, investigation.ContainmentNote, investigation.AnalystLabel, investigation.Narrative, TimeSpan.FromMinutes(minutes), restore,
            values.GetValueOrDefault("--source-commit")), CancellationToken.None).GetAwaiter().GetResult();
        if (result.Outcome == DPlusRecoveryCommandOutcome.Done)
        {
            using var file = new FileStream(output, FileMode.CreateNew, FileAccess.Write);
            file.Write(CanonicalJson.SerializeBytes(new { result.RecoveryId, result.StatementSha256Hex, StatementObject = "recovery/" + DPlusRecoveryNames.Statement(organization, stream, result.RecoveryId!.Value) }));
        }

        return Report(result);
    }

    private static int Report(DPlusRecoveryCommandResult result)
    {
        Console.WriteLine($"journal recovery {result.Outcome} {result.Code}{(result.RecoveryId is { } id ? " recovery " + id.ToString("D") : string.Empty)}"
            + $"{(result.StatementSha256Hex is { } sha ? " statement_sha256 " + sha : string.Empty)}: {result.Detail}");
        return result.Outcome switch
        {
            DPlusRecoveryCommandOutcome.Done => 0,
            DPlusRecoveryCommandOutcome.Unavailable => 5,
            _ => 4,
        };
    }
}
