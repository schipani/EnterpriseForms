using AuthorizationAuthority.Application.Features.Content;
using AuthorizationAuthority.Infrastructure.Persistence;

namespace AuthorizationAuthority.Api.Composition;

/// <summary>
/// E3-M2e (docs/security/E3_M2E_CONTRACT_CLOSURE.md OD-M2e-07, OD-M2e-13, AM-5): composes the Envelope v2 acceptance switch from the host's
/// final deployment configuration (resolved once; a configuration reload never changes it), its startup guard and the policy
/// observation loop. The switch is default off; E3-M2e-1 never enables it.
/// </summary>
public static class EnvelopeV2AcceptanceComposition
{
    public static IServiceCollection AddEnvelopeV2Acceptance(this IServiceCollection services)
    {
        services.AddSingleton(provider =>
        {
            var configuration = provider.GetRequiredService<IConfiguration>();
            var environment = provider.GetRequiredService<IHostEnvironment>();
            return EnvelopeV2AcceptanceSwitch.Create(configuration[EnvelopeV2AcceptanceSwitch.ConfigurationKey], environment.EnvironmentName, provider.GetRequiredService<ContentAcceptancePolicy>());
        });
        services.AddSingleton<EnvelopeAcceptancePolicyObserver>();
        services.AddHostedService<EnvelopeV2AcceptanceStartupGuard>();
        services.AddHostedService<EnvelopeAcceptancePolicyObservationLoop>();
        return services;
    }
}

/// <summary>
/// E3-M2e (AM-5): resolves the switch at host start (an environment or content-acceptance-policy violation throws there, so the host
/// refuses to start) and, when acceptance is configured on, refuses the start unless the data-key ledger migration is recorded. With the
/// switch off it only logs the resolved state.
/// </summary>
public sealed partial class EnvelopeV2AcceptanceStartupGuard(IServiceProvider services, IContentAcceptanceRuleSchemaReader reader, ILogger<EnvelopeV2AcceptanceStartupGuard> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (HostExecutionMode.IsOpenApiDocumentGeneration)
        {
            return;
        }

        var acceptance = services.GetRequiredService<EnvelopeV2AcceptanceSwitch>(); // throws on a refused configuration
        if (acceptance.ConfiguredEnabled)
        {
            var facts = await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            if (facts.DataKeyLedgerApplied is not true)
            {
                LogRefused(logger);
                throw new InvalidOperationException(EnvelopeV2AcceptanceSwitch.ConfigurationKey + " is enabled but the data-key ledger migration is not recorded; the Authorization Authority refuses to start.");
            }
        }

        LogResolved(logger, acceptance.ConfiguredEnabled, acceptance.EnvironmentClass, acceptance.ContentAcceptancePolicyDescription);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    [LoggerMessage(EventId = 9241, Level = LogLevel.Information, Message = "Envelope v2 acceptance configured={Configured} environment={EnvironmentClass} {Policy}; acceptance stays off until the policy is recorded on the platform journal")]
    private static partial void LogResolved(ILogger logger, bool configured, string environmentClass, string policy);

    [LoggerMessage(EventId = 9242, Level = LogLevel.Critical, Message = "Envelope v2 acceptance is configured on without the data-key ledger migration; the host refuses to start")]
    private static partial void LogRefused(ILogger logger);
}

/// <summary>
/// E3-M2e (OD-M2e-13): records the effective acceptance policy on the platform journal at start and whenever it changes (a Testing
/// override), retrying until it is recorded. Until then the switch keeps acceptance off. Failures are logged, never fatal: an
/// unrecorded policy can only keep acceptance off.
/// </summary>
public sealed partial class EnvelopeAcceptancePolicyObservationLoop(EnvelopeAcceptancePolicyObserver observer, EnvelopeV2AcceptanceSwitch acceptance, ILogger<EnvelopeAcceptancePolicyObservationLoop> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(30);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (HostExecutionMode.IsOpenApiDocumentGeneration)
        {
            return;
        }

        string? recorded = null;
        while (!stoppingToken.IsCancellationRequested)
        {
            var digest = acceptance.Describe().PolicyDigestHex;
            if (!string.Equals(digest, recorded, StringComparison.Ordinal))
            {
                try
                {
                    var outcome = await observer.RecordAsync(Guid.CreateVersion7(), stoppingToken).ConfigureAwait(false);
                    recorded = digest;
                    LogRecorded(logger, outcome, digest);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    LogFailed(logger, ex.GetType().Name);
                }
            }

            try
            {
                await Task.Delay(Interval, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    [LoggerMessage(EventId = 9243, Level = LogLevel.Information, Message = "Envelope v2 acceptance policy {Outcome} on the platform journal (policy digest {Digest})")]
    private static partial void LogRecorded(ILogger logger, string outcome, string digest);

    [LoggerMessage(EventId = 9244, Level = LogLevel.Warning, Message = "Envelope v2 acceptance policy could not be recorded ({Failure}); acceptance stays off; retrying")]
    private static partial void LogFailed(ILogger logger, string failure);
}
