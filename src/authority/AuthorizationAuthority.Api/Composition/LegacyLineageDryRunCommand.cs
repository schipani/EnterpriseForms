using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using AuthorizationAuthority.Application.Features.Lineage;
using AuthorizationAuthority.Domain.Features.Lineage;
using AuthorizationAuthority.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace AuthorizationAuthority.Api.Composition;

/// <summary>
/// C10 M1-WP10 (WP-0 Section 5.1; preparation approved by C-194, execution not):
/// <c>dotnet AuthorizationAuthority.Api.dll --legacy-lineage-dry-run --organization &lt;uuid&gt; --output &lt;new file&gt;</c>.
/// <para>
/// Read-only operator report of the legacy lineage classification. Runs before the host is composed, on one explicit connection
/// (<c>ConnectionStrings__AuthorityDbReadOnly</c>; no fallback to the runtime or migration connection), in one
/// <c>REPEATABLE READ, READ ONLY</c> transaction that is verified on the server and rolled back. It has no write path: it submits no
/// command, applies no invalidation or lineage, and takes no organization lock. Development/Testing only. The report holds opaque
/// ids, counts, enums and per-run keyed pseudonyms (a fresh random key each run, never stored), is written only to a new file and is
/// never printed; standard output receives the summary counts only.
/// </para>
/// Exit codes: 0 report written, evidence complete; 6 report written, evidence incomplete (refusals listed, Section 5.1 execution must
/// not proceed from it); 2 usage, configuration or environment refused; 3 database failure; 4 the transaction was not read-only;
/// 5 unknown organization; 7 the output file exists; 8 inconsistent evidence (no report).
/// </summary>
public static class LegacyLineageDryRunCommand
{
    public const string Flag = "--legacy-lineage-dry-run";
    public const string OrganizationOption = "--organization";
    public const string OutputOption = "--output";
    public const string ConnectionName = "AuthorityDbReadOnly";
    public const string ApplicationName = "authority-legacy-lineage-dry-run";

    public const int Succeeded = 0;
    public const int Usage = 2;
    public const int DatabaseFailure = 3;
    public const int NotReadOnly = 4;
    public const int UnknownOrganization = 5;
    public const int EvidenceIncomplete = 6;
    public const int OutputExists = 7;
    public const int InconsistentEvidence = 8;

    private static readonly JsonSerializerOptions ReportJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) },
    };

    public static bool TryRun(string[] args, out int exitCode) => TryRun(args, null, null, out exitCode);

    /// <summary>As <see cref="TryRun(string[], out int)"/>; <paramref name="configuration"/> and <paramref name="environmentName"/> replace the process values (tests).</summary>
    public static bool TryRun(string[] args, IConfiguration? configuration, string? environmentName, out int exitCode)
    {
        ArgumentNullException.ThrowIfNull(args);
        exitCode = Succeeded;
        var index = Array.IndexOf(args, Flag);
        if (index < 0)
        {
            return false;
        }

        if (!TryParse(args, out var organization, out var output))
        {
            Console.Error.WriteLine($"usage: {Flag} {OrganizationOption} <uuid> {OutputOption} <new file>; it cannot be combined with another command");
            exitCode = Usage;
            return true;
        }

        environmentName ??= Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT") ?? Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") ?? "Production";
        if (environmentName is not ("Development" or "Testing"))
        {
            Console.Error.WriteLine($"{Flag} is a Development/Testing command (disposable stacks); refused in {environmentName}.");
            exitCode = Usage;
            return true;
        }

        configuration ??= new ConfigurationBuilder().AddEnvironmentVariables().Build();
        var connectionString = configuration.GetConnectionString(ConnectionName);
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            Console.Error.WriteLine($"ConnectionStrings__{ConnectionName} is required for {Flag}; the runtime and migration connections are never used");
            exitCode = Usage;
            return true;
        }

        if (File.Exists(output) || Directory.Exists(output))
        {
            Console.Error.WriteLine($"{Flag}: the output path already exists; a report is only written to a new file");
            exitCode = OutputExists;
            return true;
        }

        try
        {
            exitCode = RunAsync(connectionString, organization, output, TimeProvider.System, CancellationToken.None).GetAwaiter().GetResult();
        }
        catch (LineageReadOnlyTransactionException ex)
        {
            Console.Error.WriteLine($"{Flag}: {ex.Message}");
            exitCode = NotReadOnly;
        }
        catch (InvalidOperationException ex) when (ex is not LineageReadOnlyTransactionException && ex.InnerException is null)
        {
            Console.Error.WriteLine($"{Flag}: inconsistent evidence, no report written ({ex.Message})");
            exitCode = InconsistentEvidence;
        }
        catch (Exception ex) when (ex is NpgsqlException or DbUpdateException or TimeoutException || ex.InnerException is NpgsqlException)
        {
            Console.Error.WriteLine($"{Flag}: database failure, no report written ({ex.GetType().Name})");
            exitCode = DatabaseFailure;
        }

        return true;
    }

    /// <summary>Reads, classifies and writes the report. Internal so tests can drive it on a disposable database.</summary>
    internal static async Task<int> RunAsync(string connectionString, Guid organization, string output, TimeProvider clock, CancellationToken cancellationToken)
    {
        var builder = new NpgsqlConnectionStringBuilder(connectionString) { ApplicationName = ApplicationName, Pooling = false };
        var options = new DbContextOptionsBuilder<AuthorityDbContext>().UseNpgsql(builder.ConnectionString).Options;
        LineageStoreRows? rows;
        await using (var context = new AuthorityDbContext(options))
        {
            rows = await new LegacyLineageEvidenceReader(context, clock).ReadAsync(organization, cancellationToken).ConfigureAwait(false);
        }

        if (rows is null)
        {
            Console.Error.WriteLine($"{Flag}: unknown organization; nothing was read");
            return UnknownOrganization;
        }

        var key = RandomNumberGenerator.GetBytes(32);
        LegacyLineageClassificationReport report;
        try
        {
            report = LegacyLineageDryRun.Run(LineageEvidenceAssembler.Assemble(rows), new LineagePseudonymizer(organization, key));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }

        var validation = LegacyLineageReportValidator.Validate(report);
        if (!validation.IsValid)
        {
            throw new InvalidOperationException("the report failed validation: " + string.Join("; ", validation.Issues));
        }

        await using (var file = new FileStream(output, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            await JsonSerializer.SerializeAsync(file, report, ReportJson, cancellationToken).ConfigureAwait(false);
        }

        var summary = report.Summaries.SingleOrDefault();
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            authorization_revision_read = report.AuthorizationRevisionRead,
            tool_version = report.ToolVersion,
            evidence_complete = report.IsEvidenceComplete,
            shares_evaluated = summary?.TotalSharesEvaluated ?? 0,
            owner_granted = summary?.OwnerGrantedCount ?? 0,
            unambiguous_parent = summary?.UnambiguousParentCount ?? 0,
            multiple_candidate_parents = summary?.MultipleCandidateParentsCount ?? 0,
            no_covering_parent = summary?.NoCoveringParentCount ?? 0,
            exceeds_depth = summary?.ExceedsDepthCount ?? 0,
            insufficient_evidence = summary?.InsufficientEvidenceCount ?? 0,
            independently_covered = summary?.IndependentlyCoveredCount ?? 0,
        }));
        return report.IsEvidenceComplete ? Succeeded : EvidenceIncomplete;
    }

    private static bool TryParse(string[] args, out Guid organization, out string output)
    {
        organization = Guid.Empty;
        output = string.Empty;
        string? organizationText = null;
        string? outputText = null;
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case Flag:
                    break;
                case OrganizationOption when i + 1 < args.Length && organizationText is null:
                    organizationText = args[++i];
                    break;
                case OutputOption when i + 1 < args.Length && outputText is null:
                    outputText = args[++i];
                    break;
                default:
                    return false; // no other command, option or host argument is accepted alongside the dry run
            }
        }

        if (!Guid.TryParseExact(organizationText, "D", out organization) || organization == Guid.Empty || string.IsNullOrWhiteSpace(outputText))
        {
            return false;
        }

        output = Path.GetFullPath(outputText);
        return true;
    }
}
