using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace EnterpriseForms.Infrastructure.Persistence;

/// <summary>
/// E3-M2b-2 business required-migration guard (contract §10 and §23; owner decision D-5). This build maps the Envelope v2
/// metadata columns, so it must not be treated as ready against a BusinessDb that lacks the migration that adds them and their
/// CHECKs (a partial restore, an unapplied migration). The decision is read at host start and re-read every
/// <see cref="ReadinessRecheckInterval"/>. It is operational hygiene, not a transactional write fence: a write racing a restore
/// is stopped only by the database itself (the missing columns make every envelope-table statement fail) and by the Authority.
/// </summary>
public static class BusinessSchemaRequirements
{
    /// <summary>Migrations this build requires, by EF migration id (compiled in; a newer build adds its own).</summary>
    public static readonly IReadOnlyList<string> RequiredMigrationIds = ["20261115125042_EnvelopeV2SignedColumns"];

    /// <summary>D-5: how often a running host re-reads the migration history (same interval as the Authority's CARV guard).</summary>
    public static readonly TimeSpan ReadinessRecheckInterval = TimeSpan.FromSeconds(30);

    /// <summary>A decision older than this (the monitor stopped or keeps failing) is treated as unknown: not ready.</summary>
    public static readonly TimeSpan MaximumDecisionAge = ReadinessRecheckInterval * 3;

    public const string ReadyDetail = "BusinessSchemaReady";
    public const string UnverifiedDetail = "BusinessSchemaUnverified";
    public const string StaleDetail = "BusinessSchemaStale";
    public const string UnreadableDetail = "BusinessSchemaUnreadable";
    public const string MigrationMissingDetail = "BusinessSchemaMigrationMissing";
}

/// <summary>The outcome of one read of the migration history.</summary>
public enum BusinessSchemaState
{
    /// <summary>Every required migration id is recorded.</summary>
    Ready,

    /// <summary>The history was read and at least one required id is absent: a definitive mismatch.</summary>
    MigrationMissing,

    /// <summary>The history could not be read (database unreachable, table absent, permission denied): unknown, not ready.</summary>
    Unreadable,
}

/// <summary>Reads which of the given migration ids the business migration history records (the runtime role has SELECT on it).</summary>
public interface IBusinessMigrationHistoryReader
{
    Task<IReadOnlySet<string>> ReadRecordedAsync(IReadOnlyList<string> migrationIds, CancellationToken cancellationToken);
}

public sealed class BusinessMigrationHistoryReader(IDbContextFactory<BusinessDbContext> contexts) : IBusinessMigrationHistoryReader
{
    public async Task<IReadOnlySet<string>> ReadRecordedAsync(IReadOnlyList<string> migrationIds, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(migrationIds);
        await using var context = await contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var connection = (NpgsqlConnection)context.Database.GetDbConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand("SELECT \"MigrationId\" FROM public.\"__EFMigrationsHistory\" WHERE \"MigrationId\" = ANY(@ids)", connection);
        command.Parameters.AddWithValue("ids", migrationIds.ToArray());
        var recorded = new HashSet<string>(StringComparer.Ordinal);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            recorded.Add(reader.GetString(0));
        }

        return recorded;
    }
}

/// <summary>Holds the latest decision. Never evaluated, stale, unreadable or missing ⇒ not ready (fail closed).</summary>
public sealed partial class BusinessSchemaGuard(IBusinessMigrationHistoryReader reader, TimeProvider clock, ILogger<BusinessSchemaGuard> logger)
{
    private sealed record Decision(BusinessSchemaState State, DateTimeOffset EvaluatedOn);

    private Decision? latest;

    /// <summary>The readiness view of the latest decision.</summary>
    public (bool Ready, string Detail) Current
    {
        get
        {
            var decision = Volatile.Read(ref latest);
            if (decision is null)
            {
                return (false, BusinessSchemaRequirements.UnverifiedDetail);
            }

            if (clock.GetUtcNow() - decision.EvaluatedOn > BusinessSchemaRequirements.MaximumDecisionAge)
            {
                return (false, BusinessSchemaRequirements.StaleDetail);
            }

            return (decision.State == BusinessSchemaState.Ready, Detail(decision.State));
        }
    }

    /// <summary>Reads the history now and records the decision; a read failure is <see cref="BusinessSchemaState.Unreadable"/>.</summary>
    public async Task<BusinessSchemaState> RefreshAsync(CancellationToken cancellationToken)
    {
        BusinessSchemaState state;
        try
        {
            var recorded = await reader.ReadRecordedAsync(BusinessSchemaRequirements.RequiredMigrationIds, cancellationToken).ConfigureAwait(false);
            state = BusinessSchemaRequirements.RequiredMigrationIds.All(recorded.Contains) ? BusinessSchemaState.Ready : BusinessSchemaState.MigrationMissing;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            LogReadFailure(logger, ex.GetType().Name);
            state = BusinessSchemaState.Unreadable;
        }

        var previous = Volatile.Read(ref latest);
        Volatile.Write(ref latest, new Decision(state, clock.GetUtcNow()));
        if (previous is null || previous.State != state)
        {
            if (state == BusinessSchemaState.Ready)
            {
                LogReady(logger);
            }
            else
            {
                var detail = Detail(state);
                LogNotReady(logger, detail);
            }
        }

        return state;
    }

    /// <summary>
    /// The monitor loop of both hosts: evaluates now and then every <see cref="BusinessSchemaRequirements.ReadinessRecheckInterval"/>.
    /// A definitive <see cref="BusinessSchemaState.MigrationMissing"/> calls <paramref name="stopHost"/> and ends the loop; an
    /// unreadable history only keeps the host not ready, so a transient database outage does not stop it.
    /// </summary>
    public async Task MonitorAsync(Action stopHost, CancellationToken stoppingToken)
    {
        ArgumentNullException.ThrowIfNull(stopHost);
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                if (await RefreshAsync(stoppingToken).ConfigureAwait(false) == BusinessSchemaState.MigrationMissing)
                {
                    LogStopping(logger);
                    stopHost();
                    return;
                }

                await Task.Delay(BusinessSchemaRequirements.ReadinessRecheckInterval, clock, stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // host stopping
        }
    }

    private static string Detail(BusinessSchemaState state) => state switch
    {
        BusinessSchemaState.Ready => BusinessSchemaRequirements.ReadyDetail,
        BusinessSchemaState.MigrationMissing => BusinessSchemaRequirements.MigrationMissingDetail,
        _ => BusinessSchemaRequirements.UnreadableDetail,
    };

    [LoggerMessage(EventId = 9310, Level = LogLevel.Information, Message = "Business schema guard: ready (required migrations recorded)")]
    private static partial void LogReady(ILogger logger);

    [LoggerMessage(EventId = 9311, Level = LogLevel.Critical, Message = "Business schema guard: NOT READY ({Detail})")]
    private static partial void LogNotReady(ILogger logger, string detail);

    [LoggerMessage(EventId = 9312, Level = LogLevel.Warning, Message = "Business schema guard could not read the migration history: {ExceptionType}")]
    private static partial void LogReadFailure(ILogger logger, string exceptionType);

    [LoggerMessage(EventId = 9313, Level = LogLevel.Critical, Message = "Business schema guard: a required migration is missing from the business database; the host stops")]
    private static partial void LogStopping(ILogger logger);
}

public static class BusinessSchemaGuardRegistration
{
    /// <summary>Registers the reader and the guard (both business hosts). Each host adds its own hosted monitor around <see cref="BusinessSchemaGuard.MonitorAsync"/>.</summary>
    public static IServiceCollection AddBusinessSchemaGuard(this IServiceCollection services)
    {
        services.AddSingleton<IBusinessMigrationHistoryReader, BusinessMigrationHistoryReader>();
        services.AddSingleton<BusinessSchemaGuard>();
        return services;
    }
}
