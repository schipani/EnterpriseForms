namespace AuthorizationAuthority.Application.Features.SearchIndex;

/// <summary>
/// C-150 IX-5c shadow preparation: where a completed Tier 1 search hands what it answered, for an off-path comparison. Tier 1 knows
/// only this interface; it never reads, waits for or is told anything by the comparison. <see cref="Offer"/> must not block and must not
/// throw into the search: it is called after the search's result snapshot and evidence committed, and its outcome is ignored.
/// Registered only when the shadow comparison is enabled (off by default, Development/Testing only).
/// </summary>
public interface IIndexedSearchShadowSink
{
    /// <summary>Queues the observation if there is room; false when it was dropped (bounded, never queued without limit).</summary>
    bool Offer(IndexedSearchShadowObservation observation);
}

/// <summary>
/// What one committed Tier 1 search answered, at which Authority snapshot. Held in memory only, never stored, logged or journaled: the
/// condition values stay inside the Authority like every Tier 1 predicate value. <paramref name="Tier1Matches"/> is the complete matched
/// record set (every page), already authorized and metadata-filtered by Tier 1.
/// </summary>
public sealed record IndexedSearchShadowObservation(
    Guid OrganizationId,
    Guid FormDefinitionId,
    Guid AuthorityUserId,
    IReadOnlyList<PreparedIndexedCondition> Conditions,
    IReadOnlyList<Guid> PredicateFieldSearchApprovalIds,
    string? RecordStatus,
    bool IncludeChildren,
    bool IncludeArchived,
    IReadOnlyList<Guid> Tier1Matches,
    long AuthorizationRevision,
    long ContentIntegrityRevision,
    long FormContentRevision,
    Guid CorrelationId);
