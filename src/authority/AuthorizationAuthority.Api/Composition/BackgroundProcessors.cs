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

/// <summary>Single ordered projection + archive loop. One instance per authority process; the SpiceDB marker CAS fences any duplicate.</summary>
public sealed partial class AuthorityWorkLoop(IAuthorityStore store, ProjectionProcessor projection, ArchiveProcessor archive, AuthorizationAuthority.Application.Features.PlatformOperators.PlatformJournal platformJournal, SpiceDbSchemaInitializer schemaInitializer, ILogger<AuthorityWorkLoop> logger) : BackgroundService
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
                if (schemaInitializer.SchemaReady)
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
            catch (Exception ex) when (ex is Npgsql.NpgsqlException or Microsoft.EntityFrameworkCore.DbUpdateException or SpiceDbUnavailableException or ArchiveUnavailableException or TimeoutException)
            {
                LogLoopError(logger, ex.GetType().Name, ex.Message);
            }

            await Task.Delay(didWork ? TimeSpan.FromMilliseconds(50) : TimeSpan.FromMilliseconds(250), stoppingToken).ConfigureAwait(false);
        }
    }

    [LoggerMessage(Level = LogLevel.Critical, Message = "Organization {OrganizationId} quarantined by the projection processor; manual reconciliation required.")]
    private static partial void LogQuarantine(ILogger logger, Guid organizationId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Authority work loop transient failure {ExceptionType}: {Detail}")]
    private static partial void LogLoopError(ILogger logger, string exceptionType, string detail);
}
