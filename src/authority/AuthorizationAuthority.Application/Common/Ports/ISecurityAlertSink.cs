namespace AuthorizationAuthority.Application.Common.Ports;

/// <summary>DR-C7-3: a security alert for the operations team (no secrets, no protected values, no tokens).</summary>
public sealed record SecurityAlert(string AlertKind, string Severity, Guid OrganizationId, Guid OperationId, Guid? SecurityCommandId, string Summary, Guid CorrelationId);

/// <summary>
/// Raises security alerts. The Authority journals every alert on its audit stream (archived independently) before raising
/// it; this sink is the operational notification path (structured critical log locally; a monitored SIEM rule in production).
/// </summary>
public interface ISecurityAlertSink
{
    void Raise(SecurityAlert alert);
}
