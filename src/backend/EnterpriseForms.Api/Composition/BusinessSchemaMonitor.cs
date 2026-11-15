using EnterpriseForms.Infrastructure.Persistence;

namespace EnterpriseForms.Api.Composition;

/// <summary>
/// E3-M2b-2 (D-5): evaluates the business required-migration guard at API start and every 30 seconds; /health/ready reports
/// the decision (never evaluated, stale, unreadable or missing ⇒ 503). A definitive missing migration stops the host. The API
/// does not block its start on the first read (its readiness stays "unverified" until then), so a database that is not yet
/// reachable keeps the instance out of rotation instead of crashing it. Not a transactional write fence.
/// </summary>
public sealed class BusinessSchemaMonitor(BusinessSchemaGuard guard, IHostApplicationLifetime lifetime) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        HostExecutionMode.IsOpenApiDocumentGeneration ? Task.CompletedTask : guard.MonitorAsync(lifetime.StopApplication, stoppingToken);
}
