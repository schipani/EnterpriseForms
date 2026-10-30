using AuthorizationAuthority.Application.Common.Ports;
using Microsoft.Extensions.Logging;

namespace AuthorizationAuthority.Infrastructure.Alerts;

/// <summary>
/// DR-C7-3 operational alert path: a structured log entry with a stable event id (9100 "SecurityAlert") for a monitoring
/// rule. Locally it is visible in the container log; production must route it to a monitored SIEM rule (not configured
/// here). The durable evidence is the audit-stream event the Authority journals before raising the alert.
/// </summary>
public sealed partial class LoggingSecurityAlertSink(ILogger<LoggingSecurityAlertSink> logger) : ISecurityAlertSink
{
    public void Raise(SecurityAlert alert)
    {
        ArgumentNullException.ThrowIfNull(alert);
        Log(logger, alert.Severity == "Critical" ? LogLevel.Critical : LogLevel.Warning, alert.AlertKind, alert.OrganizationId, alert.OperationId, alert.SecurityCommandId, alert.Summary, alert.CorrelationId);
    }

    [LoggerMessage(EventId = 9100, EventName = "SecurityAlert", Message = "SECURITY ALERT {AlertKind} organization {OrganizationId} operation {OperationId} command {SecurityCommandId}: {Summary} (correlation {CorrelationId})")]
    private static partial void Log(ILogger logger, LogLevel level, string alertKind, Guid organizationId, Guid operationId, Guid? securityCommandId, string summary, Guid correlationId);
}
