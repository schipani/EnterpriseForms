using EnterpriseForms.Application.Features.FormDesigner.ProtectionMigration;

namespace EnterpriseForms.Worker.Features.FormDesigner;

/// <summary>Durable field protection migration loop (plaintext → encrypted through the authority) hosted by the Worker image; one leased migration batch at a time.</summary>
public sealed partial class FieldProtectionMigrationWorker(FieldProtectionMigrationService migration, ILogger<FieldProtectionMigrationWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var worker = "worker-migration:" + Environment.MachineName + ":" + Environment.ProcessId;
        LogStarted(logger, worker);
        while (!stoppingToken.IsCancellationRequested)
        {
            var didWork = false;
            try
            {
                didWork = await migration.ProcessOnceAsync(worker, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // One record's failure (the service has already parked the migration as Blocked) must never stop the host: log and keep polling.
                LogLoopError(logger, ex.GetType().Name, ex.Message);
            }

            await Task.Delay(didWork ? 50 : 1000, stoppingToken).ConfigureAwait(false);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Field protection migration worker started as {Worker}.")]
    private static partial void LogStarted(ILogger logger, string worker);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Field protection migration worker transient failure {ExceptionType}: {Detail}")]
    private static partial void LogLoopError(ILogger logger, string exceptionType, string detail);
}
