using AuthorizationAuthority.Domain.Features.SecurityCommands;

namespace AuthorizationAuthority.Application.Common.Ports;

/// <summary>
/// C8 M4-4 (AC-8.4, contract §5.4): the aggregate reads of the security overview, run in the read-only snapshot that also
/// evaluated the Effective-edge rule. Every read filters by organization, aggregates and limits in the database; no payload,
/// reason text or target is read for the overview. A scope that does not implement this interface cannot serve the overview
/// (fail closed).
/// </summary>
public interface ISecurityOverviewScope
{
    /// <summary>Number of commands and the oldest creation time per state, for the given states only (absent states have no row).</summary>
    Task<List<CommandStateTally>> CountCommandsByStateAsync(Guid organizationId, IReadOnlyCollection<string> states, CancellationToken cancellationToken);

    /// <summary>The oldest commands in the given states, oldest first (creation time, then id), at most <paramref name="limit"/>.</summary>
    Task<List<SecurityCommand>> GetOldestCommandsInStatesAsync(Guid organizationId, IReadOnlyCollection<string> states, int limit, CancellationToken cancellationToken);

    /// <summary>Journal entries per (stream, archive delivery state): count, oldest creation time and latest archive time.</summary>
    Task<List<ArchiveDeliveryTally>> CountJournalArchiveStatesAsync(Guid organizationId, CancellationToken cancellationToken);
}

public sealed record CommandStateTally(string CommandStateName, int CommandCount, DateTimeOffset OldestCreatedOn);

public sealed record ArchiveDeliveryTally(string StreamName, string ArchiveDeliveryState, int EntryCount, DateTimeOffset OldestCreatedOn, DateTimeOffset? LatestArchivedOn);
