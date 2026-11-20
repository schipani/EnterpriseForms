using System.Diagnostics.Metrics;

namespace AuthorizationAuthority.Infrastructure.Persistence;

/// <summary>
/// C8 M4-2 (AC-8.3 §5.3, AC-M4-2.6): wait time of the ordinary (waiting) organization-row lock taken by security commands and
/// every other organization mutation. Audit-access transactions use NOWAIT and are not recorded here, so this histogram shows
/// the impact of audit reads on commands (T22 compares it with and without an audit-read load).
/// </summary>
public static class OrganizationLockMetrics
{
    public const string MeterName = "AuthorizationAuthority.OrganizationLock";

    private static readonly Meter Meter = new(MeterName);

    private static readonly Histogram<double> Wait = Meter.CreateHistogram<double>("authority_security_command_lock_wait_ms", unit: "ms", description: "Wait for the organization-row lock (FOR UPDATE) of a mutating Authority transaction");

    public static void RecordWait(TimeSpan elapsed) => Wait.Record(elapsed.TotalMilliseconds);
}
