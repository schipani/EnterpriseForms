using System.Diagnostics.Metrics;

namespace AuthorizationAuthority.Application.Features.Audit;

/// <summary>
/// C8 M4-2 (AC-8.3 §5.3) audit-access telemetry. Counters and histograms only; no actor, filter or organization values are tagged.
/// Names follow the contract's indicative list (the organization-lock wait of security commands is measured by the store).
/// </summary>
public sealed class AuditAccessTelemetry : IDisposable
{
    public const string MeterName = "AuthorizationAuthority.AuditAccess";

    private readonly Meter meter = new(MeterName);
    private readonly Counter<long> authorized;
    private readonly Counter<long> lockBusy;
    private readonly Counter<long> stateMoved;
    private readonly Counter<long> refused;
    private readonly Counter<long> deniedUnrecorded;
    private readonly Histogram<long> attempts;
    private readonly Histogram<double> hold;

    public AuditAccessTelemetry()
    {
        authorized = meter.CreateCounter<long>("authority_audit_access_authorized_total", description: "Audit reads authorized with a committed AuditAccessAuthorized entry");
        lockBusy = meter.CreateCounter<long>("authority_audit_access_lock_busy_total", description: "Audit-access attempts that found the organization lock held (NOWAIT)");
        stateMoved = meter.CreateCounter<long>("authority_audit_access_state_moved_total", description: "Audit-access attempts whose authorization state moved between check and lock");
        refused = meter.CreateCounter<long>("authority_audit_access_refused_total", description: "Audit-access requests refused, by reason");
        deniedUnrecorded = meter.CreateCounter<long>("authority_audit_access_denied_unrecorded_total", description: "Denied audit reads whose best-effort AuditAccessDenied entry was skipped");
        attempts = meter.CreateHistogram<long>("authority_audit_access_attempts", description: "Attempts needed for an authorized audit read");
        hold = meter.CreateHistogram<double>("authority_audit_access_hold_ms", unit: "ms", description: "Organization-lock hold of an audit-access transaction (lock to commit)");
    }

    public long AuthorizedCount { get; private set; }

    public long LockBusyCount { get; private set; }

    public long DeniedUnrecordedCount { get; private set; }

    public void Authorized(int attemptCount, TimeSpan lockHold)
    {
        AuthorizedCount++;
        authorized.Add(1);
        attempts.Record(attemptCount);
        hold.Record(lockHold.TotalMilliseconds);
    }

    public void AttemptFailed(string reason)
    {
        if (reason == "lock_busy")
        {
            LockBusyCount++;
            lockBusy.Add(1);
        }
        else
        {
            stateMoved.Add(1);
        }
    }

    public void Refused(string reason) => refused.Add(1, new KeyValuePair<string, object?>("reason", reason));

    public void DeniedUnrecorded()
    {
        DeniedUnrecordedCount++;
        deniedUnrecorded.Add(1);
    }

    public void Dispose() => meter.Dispose();
}
