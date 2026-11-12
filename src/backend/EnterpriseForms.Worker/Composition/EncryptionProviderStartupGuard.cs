using EnterpriseForms.Infrastructure.Features.Documents;

namespace EnterpriseForms.Worker.Composition;

/// <summary>
/// E0-1: the Worker applies the Business API's startup rule for the encryption key provider
/// (<see cref="EncryptionProviderEnvironmentPolicy"/>): the local KEK provider is refused outside Development and Testing,
/// and the host fails to start instead of substituting another provider. Registered before every processing service, so
/// no document, migration or reconciliation work starts under a refused provider.
/// </summary>
public sealed partial class EncryptionProviderStartupGuard(
    IConfiguration configuration,
    IHostEnvironment environment,
    IHostApplicationLifetime lifetime,
    ILogger<EncryptionProviderStartupGuard> logger) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        var keyProvider = EncryptionProviderEnvironmentPolicy.ConfiguredKeyProvider(configuration);
        try
        {
            EncryptionProviderEnvironmentPolicy.EnsurePermitted(configuration, environment.EnvironmentName);
        }
        catch (InvalidOperationException)
        {
            LogProviderRefused(logger, keyProvider, environment.EnvironmentName);
            lifetime.StopApplication();
            throw;
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    [LoggerMessage(Level = LogLevel.Critical, Message = "Encryption key provider {KeyProvider} is not permitted in environment {EnvironmentName}; the Worker refuses to start.")]
    private static partial void LogProviderRefused(ILogger logger, string keyProvider, string environmentName);
}
