using EnterpriseForms.Application.Features.Documents.Processing;
using Microsoft.Extensions.Options;

namespace EnterpriseForms.Api.Composition;

public sealed class DocumentProcessingHostOptions
{
    public const string SectionName = "DocumentProcessing";

    /// <summary>Run the durable processing loop inside this host (Development convenience; the Worker image is the production host).</summary>
    public bool HostInProcess { get; set; }

    public int PollMilliseconds { get; set; } = 1000;
}

/// <summary>Leased job loop around <see cref="DocumentProcessingService"/>; one job at a time per host, bounded backoff.</summary>
public sealed partial class DocumentProcessingHostedService(DocumentProcessingService processing, IOptions<DocumentProcessingHostOptions> options, ILogger<DocumentProcessingHostedService> logger) : BackgroundService
{
    private static readonly TimeSpan StagingSweepInterval = TimeSpan.FromSeconds(30);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (HostExecutionMode.IsOpenApiDocumentGeneration || !options.Value.HostInProcess)
        {
            return;
        }

        var worker = Environment.MachineName + ":" + Environment.ProcessId;
        LogStarted(logger, worker);
        var nextSweep = DateTimeOffset.MinValue;
        while (!stoppingToken.IsCancellationRequested)
        {
            var didWork = false;
            try
            {
                if (DateTimeOffset.UtcNow >= nextSweep)
                {
                    nextSweep = DateTimeOffset.UtcNow.Add(StagingSweepInterval);
                    await processing.SweepStagingAsync(100, stoppingToken).ConfigureAwait(false);
                }

                didWork = await processing.ProcessOnceAsync(worker, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex) when (ex is Npgsql.NpgsqlException or Microsoft.EntityFrameworkCore.DbUpdateException or IOException or InvalidOperationException)
            {
                LogLoopError(logger, ex.GetType().Name, ex.Message);
            }

            await Task.Delay(didWork ? 50 : Math.Max(250, options.Value.PollMilliseconds), stoppingToken).ConfigureAwait(false);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Document processing loop started in-process as {Worker}.")]
    private static partial void LogStarted(ILogger logger, string worker);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Document processing loop transient failure {ExceptionType}: {Detail}")]
    private static partial void LogLoopError(ILogger logger, string exceptionType, string detail);
}

/// <summary>In-process host of the field protection migration loop (Development convenience, same switch as document processing; the Worker image is the production host).</summary>
public sealed partial class FieldProtectionMigrationHostedService(EnterpriseForms.Application.Features.FormDesigner.ProtectionMigration.FieldProtectionMigrationService migration, IOptions<DocumentProcessingHostOptions> options, ILogger<FieldProtectionMigrationHostedService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (HostExecutionMode.IsOpenApiDocumentGeneration || !options.Value.HostInProcess)
        {
            return;
        }

        var worker = "api-migration:" + Environment.MachineName + ":" + Environment.ProcessId;
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

            await Task.Delay(didWork ? 50 : Math.Max(250, options.Value.PollMilliseconds), stoppingToken).ConfigureAwait(false);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Field protection migration loop transient failure {ExceptionType}: {Detail}")]
    private static partial void LogLoopError(ILogger logger, string exceptionType, string detail);
}

/// <summary>Fail-closed environment policy for document providers: the local KEK provider is Development/Testing only.</summary>
public sealed class DocumentProviderStartupGuard(IConfiguration configuration, IHostEnvironment environment) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (HostExecutionMode.IsOpenApiDocumentGeneration)
        {
            return Task.CompletedTask;
        }

        var keyProvider = configuration["Encryption:KeyProvider"] ?? "Unconfigured";
        var testAllowed = environment.IsDevelopment() || environment.IsEnvironment("Testing");
        if (keyProvider == "LocalDevelopment" && !testAllowed)
        {
            throw new InvalidOperationException($"Encryption:KeyProvider 'LocalDevelopment' is forbidden in {environment.EnvironmentName}. Azure Key Vault/Managed HSM providers are required (not implemented in Stage B). Refusing to start.");
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
