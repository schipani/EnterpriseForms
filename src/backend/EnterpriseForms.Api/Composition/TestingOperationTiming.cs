using EnterpriseForms.Application.Common;

namespace EnterpriseForms.Api.Composition;

/// <summary>
/// C-137 (R-37): optional phase-duration diagnostics of record creation for bounded synthetic measurements. Off unless
/// <see cref="ConfigurationKey"/> is <c>true</c>; accepted in Development and Testing only (any other environment refuses to
/// start). Logs durations, the status-poll count and an outcome code only.
/// </summary>
public static partial class TestingOperationTiming
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

        return services.AddSingleton<IOperationTimingSink, LoggingOperationTimingSink>();
    }

    private sealed partial class LoggingOperationTimingSink(ILogger<LoggingOperationTimingSink> logger) : IOperationTimingSink
    {
        public bool Enabled => true;

        public void Record(OperationTimingRecord record)
        {
            ArgumentNullException.ThrowIfNull(record);
            var phases = string.Join(',', record.Phases.Select(p => p.Key + ":" + p.Value.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)));
            LogOperation(logger, record.Operation, record.OrganizationId, record.Outcome, record.StatusPolls, record.TotalMilliseconds, phases);
        }

        [LoggerMessage(EventId = 7310, Level = LogLevel.Information, Message = "R37-OPERATION operation={Operation} organization={OrganizationId} outcome={Outcome} polls={Polls} total_ms={TotalMs} phases={Phases}")]
        private static partial void LogOperation(ILogger logger, string operation, Guid organizationId, string outcome, int polls, double totalMs, string phases);
    }
}
