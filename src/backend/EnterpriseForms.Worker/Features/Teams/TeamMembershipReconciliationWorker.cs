using EnterpriseForms.Application.Features.Teams.MembershipReconciliation;

namespace EnterpriseForms.Worker.Features.Teams;

/// <summary>
/// Phase C8 (C8-M1-01): durable, browser-independent team-membership reconciliation under the separate reconciler credential
/// (registered in this host only, D8-12). Several Worker instances may run this loop; they serialize per operation on the
/// SKIP LOCKED lease and per (team, member) pair on the watermark row lock. Anomalies and credential refusals are logged as
/// alerts without idempotency keys, team or member identifiers.
/// </summary>
public sealed partial class TeamMembershipReconciliationWorker(TeamMembershipReconciliationService reconciliation, ILogger<TeamMembershipReconciliationWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var worker = "worker-team-membership:" + Environment.MachineName + ":" + Environment.ProcessId + ":" + Guid.CreateVersion7().ToString("N")[..8];
        LogStarted(logger, worker);
        while (!stoppingToken.IsCancellationRequested)
        {
            var step = ReconciliationStep.Idle;
            try
            {
                step = await reconciliation.ProcessOnceAsync(worker, stoppingToken).ConfigureAwait(false);
                switch (step.Kind)
                {
                    case ReconciliationStepKind.Anomaly:
                        LogAnomaly(logger, step.AuthorityCommandOperationId, step.Code ?? "UNKNOWN");
                        break;
                    case ReconciliationStepKind.CredentialRefused:
                        LogCredentialRefused(logger, step.Code ?? "UNKNOWN");
                        break;
                    case ReconciliationStepKind.TransientError:
                        LogTransient(logger, step.AuthorityCommandOperationId, step.Code ?? "UNKNOWN");
                        break;
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // The lease expires and the operation is retried; never stop the host. No record data is logged.
                LogLoopError(logger, ex.GetType().Name);
            }

            var delay = step.Kind switch
            {
                ReconciliationStepKind.Idle => 500,
                ReconciliationStepKind.CredentialRefused => 5000,
                _ => 20,
            };
            await Task.Delay(delay, stoppingToken).ConfigureAwait(false);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Team membership reconciliation worker started as {Worker}.")]
    private static partial void LogStarted(ILogger logger, string worker);

    [LoggerMessage(Level = LogLevel.Error, Message = "ALERT team membership reconciliation anomaly {Code} on operation {AuthorityCommandOperationId}; nothing projected.")]
    private static partial void LogAnomaly(ILogger logger, Guid? authorityCommandOperationId, string code);

    [LoggerMessage(Level = LogLevel.Critical, Message = "ALERT the Authority refuses the team membership reconciler credential ({Code}); the reconciler is Unavailable and changes no operation.")]
    private static partial void LogCredentialRefused(ILogger logger, string code);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Team membership reconciliation transient failure {Code} on operation {AuthorityCommandOperationId}; backing off.")]
    private static partial void LogTransient(ILogger logger, Guid? authorityCommandOperationId, string code);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Team membership reconciliation loop failure {ExceptionType}.")]
    private static partial void LogLoopError(ILogger logger, string exceptionType);
}
