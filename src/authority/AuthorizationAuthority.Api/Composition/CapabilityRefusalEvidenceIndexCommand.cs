using System.Text.Json;
using AuthorizationAuthority.Infrastructure.Persistence;
using Npgsql;

namespace AuthorizationAuthority.Api.Composition;

/// <summary>
/// E3-M2f-3i (docs/security/E3_M2F3_CONTRACT_CLOSURE.md §5A; owner approval of OD-IX-1 with conditions, 2026-11-17):
/// <c>dotnet AuthorizationAuthority.Api.dll --provision-capability-refusal-evidence-index [--verify-only]</c>. The ONLY path that
/// creates the capability-refusal evidence index (<see cref="CapabilityRefusalEvidenceIndex"/>). It never runs the ordinary
/// <c>--migrate</c>, never uses EF migrations, never reads or locks the migration history, never re-applies runtime grants, never
/// locks an organization row, never drops, rebuilds or repairs anything, and never writes data.
/// <para>
/// One connection, the migration identity only (<c>ConnectionStrings__AuthorityDbMigration</c>; no fallback to the runtime
/// connection), autocommit throughout: <c>SET lock_timeout = '5s'</c>, a session advisory lock that serializes command
/// invocations (a second invocation exits 10 at once), a catalogue inspection, then, only if the index is absent, the single
/// statement <see cref="CapabilityRefusalEvidenceIndex.CreateSql"/> (<c>CREATE INDEX CONCURRENTLY</c>, which refuses to run in a
/// transaction block), then an independent re-inspection and a before/after catalogue comparison. <c>IF NOT EXISTS</c> is never
/// taken as proof: success requires the re-inspection to find the approved, valid index and the catalogue to differ by exactly that
/// index (or by nothing when it already existed).
/// </para>
/// Exit codes: 0 created or already valid and matching; 2 usage or configuration; 3 database or catalogue failure; 4 an invalid
/// index of that name exists (manual recovery, see the runbook); 5 a relation of that name has another definition; 6 another
/// session is building it; 7 the identity may not act as the table owner; 8 lock timeout (the state left behind is reported);
/// 9 post-creation verification failed; 10 another invocation holds the provisioning lock; 11 (<c>--verify-only</c>) absent.
/// </summary>
public static class CapabilityRefusalEvidenceIndexCommand
{
    public const string Flag = "--provision-capability-refusal-evidence-index";
    public const string VerifyOnlyFlag = "--verify-only";

    /// <summary>Bounded lock waiting, as the floor command (applies to the table lock and to the build's waits for other transactions).</summary>
    public const string SessionLockTimeoutSql = "SET lock_timeout = '5s'";

    /// <summary>The build itself is not time-limited by a role or database default; only lock waits are bounded.</summary>
    public const string SessionStatementTimeoutSql = "SET statement_timeout = 0";

    /// <summary>Serializes invocations of this command without touching any application table (session-level; released on close).</summary>
    public const string TryProvisioningLockSql = "SELECT pg_catalog.pg_try_advisory_lock(1161055538, 1177774385)";

    public const string ApplicationName = "authority-capability-evidence-index";

    public const int Succeeded = 0;
    public const int Usage = 2;
    public const int DatabaseFailure = 3;
    public const int IndexInvalid = 4;
    public const int DefinitionMismatch = 5;
    public const int BuildInProgress = 6;
    public const int NotOwner = 7;
    public const int LockTimeout = 8;
    public const int VerificationFailed = 9;
    public const int Busy = 10;
    public const int Absent = 11;

    private static readonly string[] OtherCommandFlags = ["--migrate", ContentAcceptanceFloorCommand.Flag, ContentAcceptanceFloorCommand.ReinstateFlag];

    public static bool TryRun(string[] args, out int exitCode) => TryRun(args, null, out exitCode);

    /// <summary>As <see cref="TryRun(string[], out int)"/>; <paramref name="configuration"/> replaces the environment and command line (tests).</summary>
    public static bool TryRun(string[] args, IConfiguration? configuration, out int exitCode)
    {
        ArgumentNullException.ThrowIfNull(args);
        exitCode = Succeeded;
        if (Array.IndexOf(args, Flag) < 0)
        {
            return false;
        }

        if (args.Any(a => OtherCommandFlags.Contains(a, StringComparer.Ordinal)))
        {
            Console.Error.WriteLine($"{Flag} cannot be combined with --migrate or the content acceptance floor command: it is a separate privileged step");
            exitCode = Usage;
            return true;
        }

        if (args.Any(a => a.StartsWith("--", StringComparison.Ordinal) && a != Flag && a != VerifyOnlyFlag && !a.StartsWith("--ConnectionStrings", StringComparison.OrdinalIgnoreCase)))
        {
            Console.Error.WriteLine($"usage: {Flag} [{VerifyOnlyFlag}]");
            exitCode = Usage;
            return true;
        }

        var verifyOnly = Array.IndexOf(args, VerifyOnlyFlag) >= 0;
        configuration ??= new ConfigurationBuilder().AddEnvironmentVariables().AddCommandLine(args.Where(a => a != Flag && a != VerifyOnlyFlag).ToArray()).Build();
        var connectionString = configuration.GetConnectionString("AuthorityDbMigration");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            Console.Error.WriteLine($"ConnectionStrings__AuthorityDbMigration (the migration identity) is required for {Flag}; the runtime connection is never used");
            exitCode = Usage;
            return true;
        }

        var report = new Report { Mode = verifyOnly ? "verify-only" : "provision" };
        try
        {
            exitCode = RunAsync(connectionString, verifyOnly, report, CancellationToken.None).GetAwaiter().GetResult();
        }
        catch (Exception ex) when (Failure(ex) is { } failure)
        {
            report.Outcome = "DatabaseFailure";
            report.SqlState = (failure as PostgresException)?.SqlState;
            report.Detail = failure is PostgresException pg ? pg.MessageText : failure.GetType().Name + ": " + failure.Message;
            exitCode = report.SqlState == PostgresErrorCodes.InsufficientPrivilege ? NotOwner : DatabaseFailure;
        }

        report.ExitCode = exitCode;
        var text = JsonSerializer.Serialize(report, ReportJson);
        if (exitCode == Succeeded)
        {
            Console.WriteLine(text);
        }
        else
        {
            Console.Error.WriteLine(text);
        }

        return true;
    }

    private static async Task<int> RunAsync(string connectionString, bool verifyOnly, Report report, CancellationToken cancellationToken)
    {
        var builder = new NpgsqlConnectionStringBuilder(connectionString) { ApplicationName = ApplicationName, Pooling = false };
        await using var connection = new NpgsqlConnection(builder.ConnectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(connection, SessionLockTimeoutSql, cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(connection, SessionStatementTimeoutSql, cancellationToken).ConfigureAwait(false);

        if (!await ScalarAsync<bool>(connection, TryProvisioningLockSql, cancellationToken).ConfigureAwait(false))
        {
            report.Outcome = "Busy";
            report.Detail = "another invocation of this command holds the provisioning lock; nothing was inspected or changed";
            return Busy;
        }

        var (tableExists, mayActAsOwner) = await TargetAsync(connection, cancellationToken).ConfigureAwait(false);
        if (!tableExists)
        {
            report.Outcome = "DatabaseFailure";
            report.Detail = CapabilityRefusalEvidenceIndex.SchemaName + "." + CapabilityRefusalEvidenceIndex.TableName + " does not exist (the Authority schema is not migrated)";
            return DatabaseFailure;
        }

        var before = await CapabilityRefusalEvidenceIndex.InspectAsync(connection, cancellationToken).ConfigureAwait(false);
        report.StateBefore = before.State.ToString();
        report.Record(before);
        if (!verifyOnly && !mayActAsOwner)
        {
            // Provisioning is refused to any identity that is not (a member of) the table owner, whatever the index state.
            report.Outcome = "NotOwner";
            report.Detail = "the connected identity may not act as the owner of the table; nothing was attempted (use the migration identity)";
            return NotOwner;
        }

        if (before.State != CapabilityRefusalEvidenceIndexState.Absent || verifyOnly)
        {
            return Conclude(before, verifyOnly, report);
        }

        var tableOwner = await ScalarAsync<string>(connection, "SELECT pg_catalog.pg_get_userbyid(relowner)::text FROM pg_catalog.pg_class WHERE oid = to_regclass('"
            + CapabilityRefusalEvidenceIndex.SchemaName + "." + CapabilityRefusalEvidenceIndex.TableName + "')", cancellationToken).ConfigureAwait(false);
        var snapshotBefore = await CapabilityRefusalEvidenceIndex.SnapshotAsync(connection, cancellationToken).ConfigureAwait(false);
        try
        {
            // IXP-1: exactly one statement in its own command, outside any transaction block; no command timeout on the build.
            await using var create = new NpgsqlCommand(CapabilityRefusalEvidenceIndex.CreateSql, connection) { CommandTimeout = 0 };
            await create.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (PostgresException ex)
        {
            report.SqlState = ex.SqlState;
            report.Detail = ex.MessageText;
            var left = await CapabilityRefusalEvidenceIndex.InspectAsync(connection, cancellationToken).ConfigureAwait(false);
            report.StateAfter = left.State.ToString();
            report.Record(left);
            switch (ex.SqlState)
            {
                case PostgresErrorCodes.LockNotAvailable:
                    report.Outcome = "LockTimeout";
                    return LockTimeout;
                case PostgresErrorCodes.InsufficientPrivilege:
                    report.Outcome = "NotOwner";
                    return NotOwner;
                default:
                    report.Outcome = left.State == CapabilityRefusalEvidenceIndexState.Invalid ? "IndexInvalid" : "DatabaseFailure";
                    return left.State == CapabilityRefusalEvidenceIndexState.Invalid ? IndexInvalid : DatabaseFailure;
            }
        }

        var after = await CapabilityRefusalEvidenceIndex.InspectAsync(connection, cancellationToken).ConfigureAwait(false);
        report.StateAfter = after.State.ToString();
        report.Record(after);
        if (after.State != CapabilityRefusalEvidenceIndexState.ValidMatching)
        {
            // IF NOT EXISTS succeeded without the approved index (a concurrent session created one first, or the build left it unusable).
            return Conclude(after, verifyOnly: false, report);
        }

        var snapshotAfter = await CapabilityRefusalEvidenceIndex.SnapshotAsync(connection, cancellationToken).ConfigureAwait(false);
        report.CatalogueAdded = [.. snapshotAfter.Except(snapshotBefore).Order(StringComparer.Ordinal)];
        report.CatalogueRemoved = [.. snapshotBefore.Except(snapshotAfter).Order(StringComparer.Ordinal)];
        if (!CapabilityRefusalEvidenceIndex.IsExactlyTheCreation(report.CatalogueAdded, report.CatalogueRemoved, tableOwner))
        {
            report.Outcome = "VerificationFailed";
            report.Detail = "the catalogue changed by more (or other) than the approved index during the build; investigate before relying on it";
            return VerificationFailed;
        }

        report.Outcome = "Created";
        return Succeeded;
    }

    private static int Conclude(CapabilityRefusalEvidenceIndexInspection inspection, bool verifyOnly, Report report)
    {
        switch (inspection.State)
        {
            case CapabilityRefusalEvidenceIndexState.ValidMatching:
                report.Outcome = "AlreadyValid";
                return Succeeded;
            case CapabilityRefusalEvidenceIndexState.Absent:
                report.Outcome = verifyOnly ? "Absent" : "DatabaseFailure";
                return verifyOnly ? Absent : DatabaseFailure;
            case CapabilityRefusalEvidenceIndexState.Invalid:
                report.Outcome = "IndexInvalid";
                report.Detail ??= "an invalid index of that name exists (an interrupted or failed build); it is not dropped or rebuilt automatically; see the runbook";
                return IndexInvalid;
            case CapabilityRefusalEvidenceIndexState.BuildInProgress:
                report.Outcome = "BuildInProgress";
                report.Detail ??= "another session is building an index of that name (or holds the table's build lock); re-run after it finishes";
                return BuildInProgress;
            default:
                report.Outcome = "DefinitionMismatch";
                report.Detail ??= "a relation of that name exists with another definition; it is not changed; see the runbook";
                return DefinitionMismatch;
        }
    }

    private static async Task<(bool TableExists, bool MayActAsOwner)> TargetAsync(NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(CapabilityRefusalEvidenceIndex.TargetSql, connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        return (reader.GetBoolean(0), reader.GetBoolean(1));
    }

    private static async Task ExecuteAsync(NpgsqlConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<T> ScalarAsync<T>(NpgsqlConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        return (T)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
    }

    private static NpgsqlException? Failure(Exception? exception)
    {
        for (var e = exception; e is not null; e = e.InnerException)
        {
            if (e is NpgsqlException npgsql)
            {
                return npgsql;
            }
        }

        return null;
    }

    private static readonly JsonSerializerOptions ReportJson = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    /// <summary>One machine-readable report line (no connection details, credentials or data).</summary>
    private sealed class Report
    {
        public string Command { get; } = "provision-capability-refusal-evidence-index";

        public string Index { get; } = CapabilityRefusalEvidenceIndex.SchemaName + "." + CapabilityRefusalEvidenceIndex.IndexName;

        public string Mode { get; set; } = "";

        public string Outcome { get; set; } = "";

        public int ExitCode { get; set; }

        public string? StateBefore { get; set; }

        public string? StateAfter { get; set; }

        public long? IndexOid { get; set; }

        public string? Definition { get; set; }

        public bool? IsValid { get; set; }

        public bool? IsReady { get; set; }

        public List<string> Mismatches { get; set; } = [];

        public List<string> CatalogueAdded { get; set; } = [];

        public List<string> CatalogueRemoved { get; set; } = [];

        public string? SqlState { get; set; }

        public string? Detail { get; set; }

        public void Record(CapabilityRefusalEvidenceIndexInspection inspection)
        {
            IndexOid = inspection.Facts?.RelationOid;
            Definition = inspection.Facts?.Definition;
            IsValid = inspection.Facts?.IsValid;
            IsReady = inspection.Facts?.IsReady;
            Mismatches = [.. inspection.Mismatches];
        }
    }
}
