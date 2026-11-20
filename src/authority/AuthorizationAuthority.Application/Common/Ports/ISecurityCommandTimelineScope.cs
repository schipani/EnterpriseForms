using AuthorizationAuthority.Domain.Features.Journal;
using AuthorizationAuthority.Domain.Features.SecurityCommands;

namespace AuthorizationAuthority.Application.Common.Ports;

/// <summary>
/// C8 M4-3 (AC-8.2, contract §5.2): the bounded reads of one security-command timeline page, run inside the audit-access
/// transaction (organization lock held NOWAIT, statement and transaction timeouts applied). Every read filters by organization,
/// kind, state, time range and keyset position, and is limited, in the database; nothing is filtered after an unbounded read.
/// A scope that does not implement this interface cannot serve the timeline (fail closed).
/// </summary>
public interface ISecurityCommandTimelineScope
{
    /// <summary>Command rows of the given kinds (and states, when given), newest first, after the position, at most <paramref name="limit"/>.</summary>
    Task<List<SecurityCommand>> GetTimelineCommandsAsync(Guid organizationId, TimelineRange range, IReadOnlyCollection<string> kinds, IReadOnlyCollection<string>? states, int limit, CancellationToken cancellationToken);

    /// <summary>Audit-stream entries of the given kinds, newest first, after the position, at most <paramref name="limit"/>.</summary>
    Task<List<SecurityJournalEntry>> GetTimelineAuditEntriesAsync(Guid organizationId, TimelineRange range, IReadOnlyCollection<string> kinds, int limit, CancellationToken cancellationToken);

    /// <summary>The authorization-stream entries at the given revisions (a committed command's entry has its committed revision).</summary>
    Task<List<SecurityJournalEntry>> GetAuthorizationEntriesAsync(Guid organizationId, IReadOnlyCollection<long> streamRevisions, CancellationToken cancellationToken);

    /// <summary>
    /// The audit-stream rejection entries (<paramref name="kinds"/>) whose body names one of <paramref name="commandIds"/>, within
    /// the inclusive time window (a rejection entry is written in the rejected command's own transaction).
    /// </summary>
    Task<List<SecurityJournalEntry>> GetRejectionEntriesAsync(Guid organizationId, IReadOnlyCollection<string> kinds, IReadOnlyCollection<Guid> commandIds, DateTimeOffset windowStart, DateTimeOffset windowEnd, CancellationToken cancellationToken);
}

/// <summary>
/// Time bounds and keyset position of a timeline page. Order is (event time, source rank, key) descending; command rows rank
/// above audit-stream entries at an equal time; the key is the command id or the audit-stream revision.
/// </summary>
/// <param name="From">Inclusive lower bound, or null.</param>
/// <param name="To">Exclusive upper bound, or null.</param>
/// <param name="After">The position of the last item of the previous page, or null for the first page.</param>
public sealed record TimelineRange(DateTimeOffset? From, DateTimeOffset? To, TimelinePosition? After);

/// <param name="OccurredOn">Event time of the last item served.</param>
/// <param name="IsCommand">Whether that item was a command row (rank 1) or an audit-stream entry (rank 0).</param>
/// <param name="CommandId">Its command id (command rows).</param>
/// <param name="StreamRevision">Its audit-stream revision (audit-stream entries).</param>
public sealed record TimelinePosition(DateTimeOffset OccurredOn, bool IsCommand, Guid CommandId, long StreamRevision);
