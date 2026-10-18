using AuthorizationAuthority.Application.Common.Ports;

namespace AuthorizationAuthority.Infrastructure.Faults;

public sealed class FaultInjectionOptions
{
    public const string SectionName = "FaultInjection";

    /// <summary>Only honoured in the Testing environment. Production/Staging always use <see cref="NoOpFaultInjector"/>.</summary>
    public bool Enabled { get; set; }
}

public sealed class NoOpFaultInjector : IFaultInjector
{
    public void CrashIfArmed(string faultName)
    {
    }
}

/// <summary>Testing only: one-shot process termination at the armed crash point (SEC-P09, CONS-25). The container restarts; recovery is reconciled by the processors.</summary>
public sealed class ProcessCrashFaultInjector : IFaultInjector
{
    private string armed = string.Empty;

    public string Armed => armed;

    public void Arm(string faultName) => armed = faultName ?? string.Empty;

    public void CrashIfArmed(string faultName)
    {
        if (armed.Length == 0 || !string.Equals(armed, faultName, StringComparison.Ordinal))
        {
            return;
        }

        armed = string.Empty;
        Console.Error.WriteLine($"FAULT-INJECTION: terminating process at '{faultName}' (Testing environment)");
        Console.Error.Flush();
        Environment.FailFast($"fault injection {faultName}");
    }
}
