using System.Diagnostics.Metrics;
using AuthorizationAuthority.Application.Common.Ports;
using AuthorizationAuthority.Application.Features.Content;

namespace AuthorizationAuthority.Api.Composition;

/// <summary>
/// E3-M2f-3b (docs/security/E3_M2F3_CONTRACT_CLOSURE.md §6, §7): composes the capability-refusal evidence recorder, its endpoint filter
/// on exactly the seven capability routes, its bounded in-memory retry service and its operational signal (logs 9253/9254, counters).
/// No endpoint, permission, configuration key or read path is added.
/// </summary>
public static class CapabilityRefusalEvidenceComposition
{
    public static IServiceCollection AddCapabilityRefusalEvidence(this IServiceCollection services)
    {
        services.AddSingleton<ICapabilityRefusalEvidenceObserver, CapabilityRefusalEvidenceObserver>();
        services.AddSingleton(provider => new CapabilityRefusalEvidenceRecorder(
            provider.GetRequiredService<IAuthorityStore>(), provider.GetRequiredService<TimeProvider>(), provider.GetRequiredService<ICapabilityRefusalEvidenceObserver>()));
        services.AddHostedService<CapabilityRefusalEvidenceRetryService>();
        return services;
    }

    /// <summary>
    /// Attaches the evidence filter to one of the seven capability routes. The entry point is fixed by the route, and so is the workload
    /// (closure §5.1: derived from the route's authentication policy, never from a claim).
    /// </summary>
    public static RouteHandlerBuilder WithCapabilityRefusalEvidence(this RouteHandlerBuilder builder, string entryPoint)
    {
        ArgumentNullException.ThrowIfNull(builder);
        if (!CapabilityRefusalEvidence.EntryPoints.All.Contains(entryPoint, StringComparer.Ordinal))
        {
            throw new ArgumentOutOfRangeException(nameof(entryPoint), entryPoint, "not one of the seven capability entry points");
        }

        return builder.WithMetadata(new CapabilityRefusalEvidenceEndpoint(entryPoint)).AddEndpointFilter(new CapabilityRefusalEvidenceFilter(entryPoint));
    }
}

/// <summary>Endpoint metadata naming the capability entry point of a route that records refusal evidence (host and architecture tests).</summary>
public sealed record CapabilityRefusalEvidenceEndpoint(string EntryPoint);

/// <summary>
/// Closure §6.1. The filter wraps the endpoint handler. The handler awaits the entry point, whose refused transaction T1 is declared
/// with <c>await using</c> in the method that throws, so T1 has been disposed (rolled back, locks released) before the refusal reaches
/// this catch. The filter then runs T2 through the recorder and rethrows the SAME exception instance, which the exception handler maps
/// exactly as before (status, code, retryable, detail). Only a capability refusal carrying its context is caught; every other
/// exception, and every success, passes through untouched.
/// </summary>
public sealed class CapabilityRefusalEvidenceFilter(string entryPoint) : IEndpointFilter
{
    public string EntryPoint { get; } = entryPoint;

    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);
        try
        {
            return await next(context).ConfigureAwait(false);
        }
        catch (Exception refused) when (CapabilityRefusalContext.From(refused) is not null)
        {
            var refusal = CapabilityRefusalContext.From(refused)!;
            var services = context.HttpContext.RequestServices;
            try
            {
                // T2-5: the host's lifetime, not the request's abort token: a client disconnect does not skip evidence.
                await services.GetRequiredService<CapabilityRefusalEvidenceRecorder>()
                    .RecordAsync(refusal, EntryPoint, services.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // Evidence never changes the refusal (F-1); the recorder reports its own failures.
            }

            throw;
        }
    }
}

/// <summary>Runs the due retries of the recorder (D-4: in-memory, best effort, bounded). Not part of OpenAPI document generation.</summary>
public sealed partial class CapabilityRefusalEvidenceRetryService(CapabilityRefusalEvidenceRecorder recorder, ILogger<CapabilityRefusalEvidenceRetryService> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (HostExecutionMode.IsOpenApiDocumentGeneration)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await recorder.RetryDueAsync(stoppingToken).ConfigureAwait(false);
                await Task.Delay(Interval, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                LogRetryLoopFailure(logger, ex.GetType().Name);
                await Task.Delay(Interval, stoppingToken).ConfigureAwait(false);
            }
        }
    }

    [LoggerMessage(EventId = 9256, Level = LogLevel.Warning, Message = "Capability refusal evidence retry loop failure {ExceptionType}")]
    private static partial void LogRetryLoopFailure(ILogger logger, string exceptionType);
}

/// <summary>
/// The recorder's operational signal (never durable evidence): counter <c>capability_refusal_evidence</c> on meter
/// <see cref="EnvelopeCapabilityObserver.MeterName"/> (tags outcome, reason, entry point), log 9253 when a retry is scheduled
/// (deferred, ambiguous commit), log 9254 when the refusal will not be recorded by this attempt (inconclusive lookup, attempt limit,
/// abandoned retry, unapproved key). Organization identifier, entry point, outcome and reason only.
/// </summary>
public sealed partial class CapabilityRefusalEvidenceObserver : ICapabilityRefusalEvidenceObserver, IDisposable
{
    private readonly Meter meter = new(EnvelopeCapabilityObserver.MeterName);
    private readonly Counter<long> outcomes;
    private readonly ILogger<CapabilityRefusalEvidenceObserver> logger;

    public CapabilityRefusalEvidenceObserver(ILogger<CapabilityRefusalEvidenceObserver> logger)
    {
        this.logger = logger;
        outcomes = meter.CreateCounter<long>("capability_refusal_evidence", description: "E3-M2f-3b capability-refusal evidence outcomes");
    }

    public void Observed(Guid organizationId, string entryPoint, CapabilityRefusalEvidenceOutcome outcome, CapabilityRefusalEvidenceReason reason)
    {
        var reasonName = CapabilityRefusalEvidenceLookup.Name(reason);
        outcomes.Add(1,
            new KeyValuePair<string, object?>("outcome", outcome.ToString()),
            new KeyValuePair<string, object?>("reason", reasonName),
            new KeyValuePair<string, object?>("entry_point", entryPoint));
        switch (outcome)
        {
            case CapabilityRefusalEvidenceOutcome.Deferred or CapabilityRefusalEvidenceOutcome.Ambiguous:
                LogDeferred(logger, organizationId, entryPoint, outcome.ToString(), reasonName);
                break;
            case CapabilityRefusalEvidenceOutcome.Inconclusive or CapabilityRefusalEvidenceOutcome.QuotaSuppressed
                or CapabilityRefusalEvidenceOutcome.Abandoned or CapabilityRefusalEvidenceOutcome.NotAttempted:
                LogNotRecorded(logger, organizationId, entryPoint, outcome.ToString(), reasonName);
                break;
        }
    }

    public void Dispose() => meter.Dispose();

    [LoggerMessage(EventId = 9253, Level = LogLevel.Warning, Message = "Capability refusal evidence deferred: organization={OrganizationId} entry_point={EntryPoint} outcome={Outcome} reason={Reason}; a bounded retry is scheduled")]
    private static partial void LogDeferred(ILogger logger, Guid organizationId, string entryPoint, string outcome, string reason);

    [LoggerMessage(EventId = 9254, Level = LogLevel.Error, Message = "Capability refusal evidence not recorded: organization={OrganizationId} entry_point={EntryPoint} outcome={Outcome} reason={Reason}")]
    private static partial void LogNotRecorded(ILogger logger, Guid organizationId, string entryPoint, string outcome, string reason);
}
