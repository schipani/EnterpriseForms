using AuthorizationAuthority.Application.Common.Ports;

namespace AuthorizationAuthority.Application.Features.SearchAccess;

/// <summary>What the maintenance loop does for one organization in one pass (C-149, R-55).</summary>
public enum SearchAccessMaintenanceAction
{
    /// <summary>A marker moved since the organization was last settled: consume its journals (the steps, one transaction each).</summary>
    CatchUp,

    /// <summary>No evidence of change (reconciliation pass, or never seen since start): one read-only probe, then the steps only if needed.</summary>
    Probe,
}

public sealed record SearchAccessMaintenanceItem(OrganizationChangeMarker Marker, SearchAccessMaintenanceAction Action);

/// <summary>
/// C-149 (R-55): change-aware work discovery for the Tier 2 maintenance loop, replacing three transactions per organization per pass.
/// Discovery reads the organizations' committed journal revisions (<see cref="OrganizationChangeMarker"/>) in one statement and
/// compares them with the markers last seen <em>settled</em> (fully consumed, or quarantined). It is a memo of completed work, never
/// a notification of pending work: an organization is settled only after a pass found nothing to consume at markers read before
/// that pass started, so a change committed at any later moment moves a marker and makes it due again. After a restart the memo is
/// empty and every organization is probed; a full reconciliation pass (every
/// <see cref="SearchAccessOptions.ReconciliationIntervalSeconds"/>) probes every organization again, which bounds changes that
/// advance no marker. Nothing here decides readiness: it is computed from the live state on every read, so a delayed pass only
/// delays availability (fail closed), never serves a stale row. Single-threaded: owned by one loop.
/// </summary>
public sealed class SearchAccessMaintenanceSchedule(SearchAccessOptions options, TimeProvider clock)
{
    private readonly Dictionary<Guid, OrganizationChangeMarker> settled = [];
    private long? lastReconciliation;

    public int SettledCount => settled.Count;

    /// <summary>Plans one pass. A reconciliation pass is due on the first pass and every reconciliation interval afterwards.</summary>
    public (IReadOnlyList<SearchAccessMaintenanceItem> Items, bool Reconciliation, int Skipped) Plan(IReadOnlyList<OrganizationChangeMarker> markers)
    {
        ArgumentNullException.ThrowIfNull(markers);
        var now = clock.GetTimestamp();
        var reconciliation = lastReconciliation is not { } last || clock.GetElapsedTime(last, now) >= TimeSpan.FromSeconds(options.ReconciliationIntervalSeconds);
        if (reconciliation)
        {
            lastReconciliation = now;
        }

        var present = markers.Select(m => m.OrganizationId).ToHashSet();
        foreach (var gone in settled.Keys.Where(k => !present.Contains(k)).ToList())
        {
            settled.Remove(gone);
        }

        var items = new List<SearchAccessMaintenanceItem>();
        var skipped = 0;
        foreach (var marker in markers)
        {
            if (!settled.TryGetValue(marker.OrganizationId, out var previous))
            {
                items.Add(new(marker, SearchAccessMaintenanceAction.Probe));
            }
            else if (previous != marker)
            {
                items.Add(new(marker, SearchAccessMaintenanceAction.CatchUp));
            }
            else if (reconciliation)
            {
                items.Add(new(marker, SearchAccessMaintenanceAction.Probe));
            }
            else
            {
                skipped++;
            }
        }

        return (items, reconciliation, skipped);
    }

    /// <summary>Records that nothing was left to consume at <paramref name="marker"/> (read before the pass began).</summary>
    public void Settle(OrganizationChangeMarker marker)
    {
        ArgumentNullException.ThrowIfNull(marker);
        settled[marker.OrganizationId] = marker;
    }

    /// <summary>Work remains or the outcome is unknown: the organization is due again on the next pass.</summary>
    public void Unsettle(Guid organizationId) => settled.Remove(organizationId);

    /// <summary>
    /// Whether a catch-up outcome leaves nothing to do until a marker moves: consumed to the head, quarantined (only a journaled
    /// rebuild leaves quarantine), or fenced by a newer binary's rows. Everything else is due again next pass.
    /// </summary>
    public static bool Settles(SearchAccessStepOutcome outcome) =>
        outcome is SearchAccessStepOutcome.Idle or SearchAccessStepOutcome.Quarantined or SearchAccessStepOutcome.Fenced;
}
