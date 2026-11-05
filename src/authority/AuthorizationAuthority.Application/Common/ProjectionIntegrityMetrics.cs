using System.Diagnostics.Metrics;

namespace AuthorizationAuthority.Application.Common;

/// <summary>
/// C8 M4 S1 (B-Q3): Authority integrity metrics, meter <c>AuthorizationAuthority.ProjectionIntegrity</c>. Tags carry outcomes,
/// phases and paths only, never organization, user, resource or permission identifiers. Locally visible with
/// <c>dotnet-counters</c>; production export is a deployment item.
/// </summary>
public sealed class ProjectionIntegrityMetrics : IDisposable
{
    public const string MeterName = "AuthorizationAuthority.ProjectionIntegrity";

    private readonly Meter meter = new(MeterName);
    private readonly Counter<long> verifications;
    private readonly Histogram<double> markerReads;
    private readonly Histogram<double> decisions;

    public ProjectionIntegrityMetrics()
    {
        verifications = meter.CreateCounter<long>("projection_integrity_verifications", description: "Pin/verify outcomes of protected SpiceDB decisions and periodic checks");
        markerReads = meter.CreateHistogram<double>("projection_integrity_marker_read_ms", unit: "ms", description: "Projection-marker read latency");
        decisions = meter.CreateHistogram<double>("projection_integrity_decision_ms", unit: "ms", description: "Pin to verify latency of one protected decision");
    }

    public void Outcome(string outcome) => verifications.Add(1, new KeyValuePair<string, object?>("outcome", outcome));

    public void MarkerRead(string phase, double milliseconds) => markerReads.Record(milliseconds, new KeyValuePair<string, object?>("phase", phase));

    public void Decision(string path, double milliseconds) => decisions.Record(milliseconds, new KeyValuePair<string, object?>("path", path));

    public void Dispose() => meter.Dispose();
}

/// <summary>Outcome tag values of <see cref="ProjectionIntegrityMetrics.Outcome"/>.</summary>
public static class ProjectionIntegrityOutcomes
{
    public const string Ok = "ok";
    public const string Moved = "moved";
    public const string MismatchQuarantined = "mismatch_quarantined";
    public const string SnapshotError = "snapshot_error";
    public const string TokenMismatch = "token_mismatch";
    public const string Unavailable = "unavailable";
}

/// <summary>
/// C8 M4 S1 (Design B §2.3): set once the startup upgrade of pre-S1 (v1) markers has run for every organization. Until then
/// no decision is pinned, the projector does not write and readiness is false.
/// </summary>
public sealed class ProjectionMarkerTransitionState
{
    private volatile bool completed;

    public bool Completed => completed;

    public void MarkCompleted() => completed = true;
}
