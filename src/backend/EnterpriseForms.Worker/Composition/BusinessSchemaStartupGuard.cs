using EnterpriseForms.Infrastructure.Persistence;

namespace EnterpriseForms.Worker.Composition;

/// <summary>
/// E3-M2b-2 (D-5): the Worker has no readiness endpoint, so it enforces the business required-migration guard at start: the
/// first read runs before any processing service starts (hosted services start in registration order), and anything but a
/// verified schema (a missing migration, or a history that cannot be read) refuses the start. The Worker runs after the
/// migration job in every stack. Not a transactional write fence.
/// </summary>
public sealed partial class BusinessSchemaStartupGuard(BusinessSchemaGuard guard, IHostApplicationLifetime lifetime, ILogger<BusinessSchemaStartupGuard> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var state = await guard.RefreshAsync(cancellationToken).ConfigureAwait(false);
        if (state != BusinessSchemaState.Ready)
        {
            LogRefused(logger, guard.Current.Detail);
            lifetime.StopApplication();
            throw new InvalidOperationException($"Refusing to start: the business database schema is not verified ({guard.Current.Detail}).");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    [LoggerMessage(Level = LogLevel.Critical, Message = "Business schema guard: {Detail}; the Worker refuses to start.")]
    private static partial void LogRefused(ILogger logger, string detail);
}

/// <summary>E3-M2b-2 (D-5): re-reads the guard every 30 seconds after start; a definitive missing migration stops the Worker.</summary>
public sealed class BusinessSchemaMonitor(BusinessSchemaGuard guard, IHostApplicationLifetime lifetime) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken) => guard.MonitorAsync(lifetime.StopApplication, stoppingToken);
}
