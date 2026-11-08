using AuthorizationAuthority.Application.Common;

namespace AuthorizationAuthority.Api.Composition;

/// <summary>
/// C-137 (R-37): optional diagnostics of security-command and content-operation phase durations, for bounded synthetic
/// measurements. Off unless <see cref="ConfigurationKey"/> is <c>true</c>; accepted in Development and Testing only (any other
/// environment refuses to start). Logs durations, counts, kinds and outcome codes only.
/// </summary>
public static class OperationTimingComposition
{
    public const string ConfigurationKey = "Diagnostics:OperationTiming";

    public static IServiceCollection AddOperationTiming(this IServiceCollection services, IConfiguration configuration, IHostEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(environment);
        if (!configuration.GetValue<bool>(ConfigurationKey))
        {
            return services;
        }

        if (!environment.IsDevelopment() && !environment.IsEnvironment("Testing"))
        {
            throw new InvalidOperationException($"{ConfigurationKey} is a Development/Testing diagnostic and must not be set in the {environment.EnvironmentName} environment. Refusing to start.");
        }

        return services.AddSingleton<IOperationTimingObserver, LoggingOperationTimingObserver>();
    }
}

internal sealed partial class LoggingOperationTimingObserver(ILogger<LoggingOperationTimingObserver> logger) : IOperationTimingObserver
{
    public void SecurityCommand(SecurityCommandTiming timing)
    {
        ArgumentNullException.ThrowIfNull(timing);
        LogCommand(logger, timing.OrganizationId, timing.CommandKind, timing.Outcome, timing.LockWaitMilliseconds, timing.GraphLoadMilliseconds, timing.GraphEdgeCount, timing.PlanMilliseconds, timing.CommitMilliseconds, timing.TotalMilliseconds);
    }

    public void ContentOperation(ContentOperationTiming timing)
    {
        ArgumentNullException.ThrowIfNull(timing);
        LogContent(logger, timing.OrganizationId, timing.Operation, timing.Outcome, timing.LockWaitMilliseconds, timing.TotalMilliseconds);
    }

    [LoggerMessage(EventId = 9306, Level = LogLevel.Information, Message = "R37-COMMAND organization={OrganizationId} kind={Kind} outcome={Outcome} lock_wait_ms={LockWaitMs} graph_ms={GraphMs} edges={Edges} plan_ms={PlanMs} commit_ms={CommitMs} total_ms={TotalMs}")]
    private static partial void LogCommand(ILogger logger, Guid organizationId, string kind, string outcome, double lockWaitMs, double graphMs, int edges, double planMs, double commitMs, double totalMs);

    [LoggerMessage(EventId = 9307, Level = LogLevel.Information, Message = "R37-CONTENT organization={OrganizationId} operation={Operation} outcome={Outcome} lock_wait_ms={LockWaitMs} total_ms={TotalMs}")]
    private static partial void LogContent(ILogger logger, Guid organizationId, string operation, string outcome, double lockWaitMs, double totalMs);
}
