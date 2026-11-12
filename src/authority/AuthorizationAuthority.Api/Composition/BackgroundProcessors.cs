using AuthorizationAuthority.Application.Common;
using AuthorizationAuthority.Application.Common.Ports;
using AuthorizationAuthority.Application.Features.Journal;
using AuthorizationAuthority.Application.Features.Projection;

namespace AuthorizationAuthority.Api.Composition;

/// <summary>Writes the embedded SpiceDB schema at startup (the authority is the only SpiceDB administrator) with bounded retries.</summary>
public sealed partial class SpiceDbSchemaInitializer(ISpiceDbProjection spiceDb, ILogger<SpiceDbSchemaInitializer> logger) : BackgroundService
{
    public bool SchemaReady { get; private set; }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (HostExecutionMode.IsOpenApiDocumentGeneration)
        {
            return;
        }

        var delay = TimeSpan.FromSeconds(1);
        while (!stoppingToken.IsCancellationRequested && !SchemaReady)
        {
            try
            {
                await spiceDb.EnsureSchemaAsync(stoppingToken).ConfigureAwait(false);
                SchemaReady = true;
                LogSchemaWritten(logger);
            }
            catch (SpiceDbUnavailableException ex)
            {
                LogSchemaRetry(logger, ex.Message);
                await Task.Delay(delay, stoppingToken).ConfigureAwait(false);
                delay = TimeSpan.FromSeconds(Math.Min(10, delay.TotalSeconds * 2));
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "SpiceDB schema written by the authority.")]
    private static partial void LogSchemaWritten(ILogger logger);

    [LoggerMessage(Level = LogLevel.Warning, Message = "SpiceDB schema write failed, retrying: {Detail}")]
    private static partial void LogSchemaRetry(ILogger logger, string detail);
}

/// <summary>
/// Single ordered projection + archive loop. One instance per authority process; the SpiceDB marker CAS fences any duplicate.
/// C8 M4 S1: before any projection it runs the one-time projection-marker transition (v1 → history-bound v2), retried until
/// SpiceDB answers; decisions and readiness wait for it.
/// C-150 R56-B: between passes it waits for a wake-up (<see cref="AuthorityWorkSignal"/>: a committed authorization change on any
/// instance) or its poll (250 ms idle, 50 ms after work), whichever comes first. A wake-up only starts the same durable scan sooner;
/// the archive-before-projection order, the processors and the organization gate are unchanged.
/// </summary>
public sealed partial class AuthorityWorkLoop(IAuthorityStore store, ProjectionProcessor projection, ArchiveProcessor archive, AuthorizationAuthority.Application.Features.PlatformOperators.PlatformJournal platformJournal, SpiceDbSchemaInitializer schemaInitializer, ProjectionIntegrityMonitor integrityMonitor, ProjectionMarkerTransitionState transition, AuthorityWorkSignal signal, ILogger<AuthorityWorkLoop> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (HostExecutionMode.IsOpenApiDocumentGeneration)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            var didWork = false;
            try
            {
                if (schemaInitializer.SchemaReady && !transition.Completed)
                {
                    var upgraded = await integrityMonitor.RunTransitionAsync(stoppingToken).ConfigureAwait(false);
                    LogTransition(logger, upgraded);
                }

                if (schemaInitializer.SchemaReady && transition.Completed)
                {
                    // C8 M2: re-deliver platform journal events whose synchronous delivery failed (ordered; quarantines on mismatch).
                    await platformJournal.DeliverAsync(stoppingToken).ConfigureAwait(false);
                    foreach (var organizationId in await store.GetOrganizationsWithPendingWorkAsync(stoppingToken).ConfigureAwait(false))
                    {
                        didWork |= await archive.ProcessOrganizationAsync(organizationId, stoppingToken).ConfigureAwait(false) > 0;
                        var result = await projection.ProcessOrganizationAsync(organizationId, stoppingToken).ConfigureAwait(false);
                        didWork |= result == ProjectionStepResult.Applied;
                        if (result is ProjectionStepResult.Quarantined)
                        {
                            LogQuarantine(logger, organizationId);
                        }
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex) when (ex is Npgsql.NpgsqlException or Microsoft.EntityFrameworkCore.DbUpdateException or SpiceDbUnavailableException or SpiceDbSnapshotException or SpiceDbPreconditionFailedException or ArchiveUnavailableException or TimeoutException)
            {
                LogLoopError(logger, ex.GetType().Name, ex.Message);
            }

            try
            {
                await signal.WaitAsync(didWork ? TimeSpan.FromMilliseconds(50) : TimeSpan.FromMilliseconds(250), stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Critical, Message = "Organization {OrganizationId} quarantined by the projection processor; manual reconciliation required.")]
    private static partial void LogQuarantine(ILogger logger, Guid organizationId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Authority work loop transient failure {ExceptionType}: {Detail}")]
    private static partial void LogLoopError(ILogger logger, string exceptionType, string detail);

    [LoggerMessage(Level = LogLevel.Information, Message = "Projection-marker transition completed; {Upgraded} organization markers upgraded to the history-bound form.")]
    private static partial void LogTransition(ILogger logger, int upgraded);
}

/// <summary>C8 M4 S1 (Design B §2.8): periodic check interval, 10–600 s (default 60); configuration section <c>ProjectionIntegrity</c>.</summary>
public sealed class ProjectionIntegrityOptions
{
    public const string SectionName = "ProjectionIntegrity";

    public int PeriodicCheckSeconds { get; set; } = 60;

    public TimeSpan Interval => TimeSpan.FromSeconds(Math.Clamp(PeriodicCheckSeconds, 10, 600));
}

/// <summary>
/// C8 M4 S2 complement to the decision-time protocol: every interval (and 5 s after a pass that could not reach SpiceDB, so
/// a datastore that comes back is inspected promptly) every current, non-quarantined organization's marker is read fully
/// consistently; a mismatch with an unchanged Authority state quarantines. It never clears anything.
/// </summary>
public sealed partial class ProjectionIntegrityMonitorLoop(ProjectionIntegrityMonitor monitor, ProjectionIntegrityOptions options, ILogger<ProjectionIntegrityMonitorLoop> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (HostExecutionMode.IsOpenApiDocumentGeneration)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            var reachable = true;
            try
            {
                reachable = await monitor.RunPeriodicCheckAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex) when (ex is Npgsql.NpgsqlException or Microsoft.EntityFrameworkCore.DbUpdateException or TimeoutException or AuthorityException)
            {
                reachable = false;
                LogCheckError(logger, ex.GetType().Name, ex.Message);
            }

            await Task.Delay(reachable ? options.Interval : TimeSpan.FromSeconds(5), stoppingToken).ConfigureAwait(false);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Projection integrity periodic check failure {ExceptionType}: {Detail}")]
    private static partial void LogCheckError(ILogger logger, string exceptionType, string detail);
}
