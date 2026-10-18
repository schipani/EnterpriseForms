namespace AuthorizationAuthority.Application.Common.Ports;

/// <summary>
/// Deterministic crash points for SEC-P09 / CONS fault injection. The production implementation is a no-op; the
/// Testing implementation terminates the process at the named point exactly once (FaultInjection:Enabled only).
/// </summary>
public interface IFaultInjector
{
    void CrashIfArmed(string faultName);
}
