using Microsoft.Extensions.Options;

namespace EnterpriseForms.Worker.Composition;

public sealed class WorkerStartupOptions
{
    public const string SectionName = "Worker";

    /// <summary>Private base address of the Authorization Authority; required outside Development.</summary>
    public string? TrustAuthorityBaseAddress { get; set; }
}

/// <summary>
/// Fails the host at startup when mandatory trust configuration is absent outside Development.
/// A worker that cannot reach the Authority must not start processing protected work with local-only decisions.
/// </summary>
public sealed partial class StartupConfigurationGuard(
    IOptions<WorkerStartupOptions> options,
    IHostEnvironment environment,
    IHostApplicationLifetime lifetime,
    ILogger<StartupConfigurationGuard> logger) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        var configured = !string.IsNullOrWhiteSpace(options.Value.TrustAuthorityBaseAddress);
        if (!configured && !environment.IsDevelopment())
        {
            LogMissingAuthority(logger, environment.EnvironmentName);
            lifetime.StopApplication();
            throw new InvalidOperationException("Worker:TrustAuthorityBaseAddress is required outside Development (fail closed).");
        }

        LogStarted(logger, environment.EnvironmentName, configured);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    [LoggerMessage(Level = LogLevel.Critical, Message = "Authorization Authority address missing in environment {EnvironmentName}; refusing to start.")]
    private static partial void LogMissingAuthority(ILogger logger, string environmentName);

    [LoggerMessage(Level = LogLevel.Information, Message = "EnterpriseForms.Worker started in {EnvironmentName}; authority configured: {AuthorityConfigured}. No job handlers are registered in Phase 0.")]
    private static partial void LogStarted(ILogger logger, string environmentName, bool authorityConfigured);
}
