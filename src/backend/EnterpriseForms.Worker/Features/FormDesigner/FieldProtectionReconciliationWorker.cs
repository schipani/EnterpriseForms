using EnterpriseForms.Application.Features.FormDesigner.ProtectionMigration;

namespace EnterpriseForms.Worker.Features.FormDesigner;

/// <summary>
/// Phase C7 M4 (G19): durable field-protection reconciliation loop under the separate field-protection verifier identity
/// (registered in this host only, OD-1); one leased step (round start, one catalogue page, or the attestation) at a time.
/// </summary>
public sealed partial class FieldProtectionReconciliationWorker(FieldProtectionReconciliationService reconciliation, ILogger<FieldProtectionReconciliationWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var worker = "worker-reconciliation:" + Environment.MachineName + ":" + Environment.ProcessId;
        LogStarted(logger, worker);
        while (!stoppingToken.IsCancellationRequested)
        {
            var didWork = false;
            try
            {
                didWork = await reconciliation.ProcessOnceAsync(worker, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // The service has already parked the reconciliation as Blocked; never stop the host. No record data is logged.
                LogLoopError(logger, ex.GetType().Name);
            }

            await Task.Delay(didWork ? 50 : 1000, stoppingToken).ConfigureAwait(false);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Field protection reconciliation worker started as {Worker}.")]
    private static partial void LogStarted(ILogger logger, string worker);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Field protection reconciliation worker transient failure {ExceptionType}.")]
    private static partial void LogLoopError(ILogger logger, string exceptionType);
}
