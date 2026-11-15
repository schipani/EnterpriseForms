using AuthorizationAuthority.Application.Features.Content;
using AuthorizationAuthority.Infrastructure.Persistence;

namespace AuthorizationAuthority.Api.Composition;

/// <summary>
/// E3-M2b-1 CARV startup and readiness guard (contract §19.8, OD-M2-20, OD-M2-30). Not ready unless the CARV migration, both
/// stamp columns, the commit CHECK and an enabled state guard exist and no installed floor exceeds this build's rule version.
/// The decision is re-read at <see cref="ContentAcceptanceRules.ReadinessRecheckInterval"/>, so a running instance that a newer
/// floor overtakes becomes not ready. This is operational hygiene only: the database itself refuses every crossing write,
/// whatever the guard says, and enforcement never depends on this interval.
/// </summary>
public sealed partial class ContentAcceptanceRuleGuard(IContentAcceptanceRuleSchemaReader reader, TimeProvider clock, ILogger<ContentAcceptanceRuleGuard> logger)
{
    /// <summary>A decision older than this (the monitor stopped or keeps failing) is treated as unknown: not ready.</summary>
    public static readonly TimeSpan MaximumDecisionAge = ContentAcceptanceRules.ReadinessRecheckInterval * 3;

    private sealed record Decision(bool Ready, string Detail, DateTimeOffset EvaluatedOn);

    private Decision? latest;

    /// <summary>The latest decision; never-evaluated or stale decisions are not ready.</summary>
    public (bool Ready, string Detail) Current
    {
        get
        {
            var decision = Volatile.Read(ref latest);
            if (decision is null)
            {
                return (false, "ContentAcceptanceRulesUnverified");
            }

            return clock.GetUtcNow() - decision.EvaluatedOn > MaximumDecisionAge ? (false, "ContentAcceptanceRulesStale") : (decision.Ready, decision.Detail);
        }
    }

    /// <summary>Reads the fence now and records the decision. Any failure to read it is a not-ready decision (fail closed).</summary>
    public async Task<(bool Ready, string Detail)> RefreshAsync(CancellationToken cancellationToken)
    {
        ContentAcceptanceRules.SchemaFacts? facts = null;
        try
        {
            facts = await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogReadFailure(logger, ex.GetType().Name);
        }

        var (ready, detail) = ContentAcceptanceRules.Evaluate(facts);
        var previous = Volatile.Read(ref latest);
        Volatile.Write(ref latest, new Decision(ready, detail, clock.GetUtcNow()));
        if (previous is null || previous.Ready != ready || !string.Equals(previous.Detail, detail, StringComparison.Ordinal))
        {
            if (ready)
            {
                LogReady(logger, detail);
            }
            else
            {
                LogNotReady(logger, detail, ContentAcceptanceRules.CurrentVersion);
            }
        }

        return (ready, detail);
    }

    [LoggerMessage(EventId = 9210, Level = LogLevel.Information, Message = "Content acceptance rule guard: ready ({Detail})")]
    private static partial void LogReady(ILogger logger, string detail);

    [LoggerMessage(EventId = 9211, Level = LogLevel.Critical, Message = "Content acceptance rule guard: NOT READY ({Detail}); this build applies rule version {BuildVersion}")]
    private static partial void LogNotReady(ILogger logger, string detail, int buildVersion);

    [LoggerMessage(EventId = 9212, Level = LogLevel.Warning, Message = "Content acceptance rule guard could not read the database fence: {ExceptionType}")]
    private static partial void LogReadFailure(ILogger logger, string exceptionType);
}

/// <summary>Evaluates the guard at host start and then every <see cref="ContentAcceptanceRules.ReadinessRecheckInterval"/>.</summary>
public sealed class ContentAcceptanceRuleMonitor(ContentAcceptanceRuleGuard guard, TimeProvider clock) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (HostExecutionMode.IsOpenApiDocumentGeneration)
        {
            return;
        }

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                await guard.RefreshAsync(stoppingToken).ConfigureAwait(false);
                await Task.Delay(ContentAcceptanceRules.ReadinessRecheckInterval, clock, stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // host stopping
        }
    }
}
