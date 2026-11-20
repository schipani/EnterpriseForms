using AuthorizationAuthority.Application.Common.Ports;
using AuthorizationAuthority.Domain.Features.Journal;

namespace AuthorizationAuthority.Application.Features.Journal.Verification;

/// <summary>
/// SEC-BL-01 WP-2 (C-155, contract §6.2 AE-2..AE-6): classifies a <b>complete</b> listing of one organization's archive prefix. Probing
/// guessed keys never establishes completeness; only a listing gathered with <see cref="CollectAsync"/> (every page) may be analysed.
/// Pure: no store access, no repair. A listing that could not be completed is unavailability, never an empty archive.
/// </summary>
public static class ArchiveCompleteness
{
    /// <summary>The organization streams the Authority writes (<see cref="JournalStreamNames"/>); anything else under an organization prefix is unexpected (AE-3).</summary>
    public static IReadOnlyList<string> KnownStreams { get; } = [JournalStreamNames.Authorization, JournalStreamNames.Content, JournalStreamNames.Audit];

    /// <summary>Upper bound on pages gathered by <see cref="CollectAsync"/>; reaching it is unavailability (the listing is not complete).</summary>
    public const int MaximumPages = 100_000;

    /// <summary>Gathers every page of <see cref="ISecurityEventArchiveInventory.ListEntriesAsync"/>. Throws <see cref="ArchiveUnavailableException"/> when a page fails or the bound is reached.</summary>
    public static async Task<ArchiveListing> CollectAsync(ISecurityEventArchiveInventory inventory, Guid organizationId, string? streamName, int pageSizeHint, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(inventory);
        var objects = new List<ArchiveListedObject>();
        string? continuation = null;
        var containerAbsent = false;
        for (var page = 0; ; page++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (page >= MaximumPages)
            {
                throw new ArchiveUnavailableException("archive listing exceeded the page bound; the listing is not complete");
            }

            var result = await inventory.ListEntriesAsync(organizationId, streamName, continuation, pageSizeHint, cancellationToken).ConfigureAwait(false);
            containerAbsent |= result.ContainerAbsent;
            objects.AddRange(result.Objects);
            continuation = result.ContinuationToken;
            if (string.IsNullOrEmpty(continuation))
            {
                return new ArchiveListing(organizationId, streamName, objects, containerAbsent);
            }
        }
    }

    /// <summary>WP-4A: gathers every page of <see cref="ISecurityEventArchiveInventory.ListEntriesAboveAsync"/> (bounded incremental listing).</summary>
    public static async Task<ArchiveListing> CollectAboveAsync(ISecurityEventArchiveInventory inventory, Guid organizationId, string streamName, long afterRevision, int pageSizeHint, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(inventory);
        var objects = new List<ArchiveListedObject>();
        string? continuation = null;
        var containerAbsent = false;
        for (var page = 0; ; page++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (page >= MaximumPages)
            {
                throw new ArchiveUnavailableException("archive listing exceeded the page bound; the listing is not complete");
            }

            var result = await inventory.ListEntriesAboveAsync(organizationId, streamName, afterRevision, continuation, pageSizeHint, cancellationToken).ConfigureAwait(false);
            containerAbsent |= result.ContainerAbsent;
            objects.AddRange(result.Objects);
            continuation = result.ContinuationToken;
            if (string.IsNullOrEmpty(continuation))
            {
                return new ArchiveListing(organizationId, streamName, objects, containerAbsent);
            }
        }
    }

    /// <summary>
    /// Per-stream analysis of a complete listing. <paramref name="assumedHeads"/> (for example the AuthorityDb heads) marks archived
    /// revisions above them (AE-6). The contiguity walk always starts at revision 1, so a gap anywhere is reported.
    /// </summary>
    public static ArchiveCompletenessReport Analyze(ArchiveListing listing, IReadOnlyDictionary<string, long>? assumedHeads = null)
    {
        ArgumentNullException.ThrowIfNull(listing);
        var unexpected = new List<ArchiveListedObject>();
        var byStream = new Dictionary<string, List<long>>(StringComparer.Ordinal);
        foreach (var item in listing.Objects)
        {
            var known = item.IsCanonicalEntryKey && item.StreamName is { } stream && KnownStreams.Contains(stream, StringComparer.Ordinal)
                && (listing.StreamName is null || string.Equals(listing.StreamName, stream, StringComparison.Ordinal))
                && item.StreamRevision is >= 1;
            if (!known)
            {
                unexpected.Add(item);
                continue;
            }

            if (!byStream.TryGetValue(item.StreamName!, out var revisions))
            {
                byStream[item.StreamName!] = revisions = [];
            }

            revisions.Add(item.StreamRevision!.Value);
        }

        var streams = new List<ArchiveStreamCompleteness>();
        IReadOnlyList<string> names = listing.StreamName is { } only ? [only] : KnownStreams;
        foreach (var name in names)
        {
            var revisions = byStream.TryGetValue(name, out var found) ? found : [];
            revisions.Sort();
            var duplicates = revisions.GroupBy(r => r).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
            var distinct = revisions.Distinct().ToList();
            var gaps = new List<ArchiveRevisionRange>();
            long expected = 1;
            long contiguousHead = 0;
            var contiguous = true;
            foreach (var revision in distinct)
            {
                if (revision != expected)
                {
                    gaps.Add(new ArchiveRevisionRange(expected, revision - 1));
                    contiguous = false;
                }

                if (contiguous)
                {
                    contiguousHead = revision;
                }

                expected = revision + 1;
            }

            var highest = distinct.Count == 0 ? 0 : distinct[^1];
            long? assumedHead = assumedHeads is not null && assumedHeads.TryGetValue(name, out var head) ? head : null;
            var beyond = assumedHead is { } h ? distinct.Where(r => r > h).ToList() : [];
            streams.Add(new ArchiveStreamCompleteness(name, distinct.Count, contiguousHead, highest, gaps, duplicates, assumedHead, beyond));
        }

        return new ArchiveCompletenessReport(listing.OrganizationId, listing.ContainerAbsent, streams, unexpected);
    }
}

public sealed record ArchiveListing(Guid OrganizationId, string? StreamName, IReadOnlyList<ArchiveListedObject> Objects, bool ContainerAbsent);

public sealed record ArchiveRevisionRange(long FirstRevision, long LastRevision);

/// <summary>
/// One stream. <see cref="HighestContiguousRevision"/>: every revision 1..it is archived (0 when revision 1 is missing);
/// <see cref="Gaps"/>: missing revisions below <see cref="HighestRevision"/> (AE-4); <see cref="DuplicateRevisions"/>: revisions listed
/// more than once (AE-5); <see cref="RevisionsBeyondAssumedHead"/>: archived above the assumed head (AE-6: contiguous ⇒ the database is
/// behind the archive; after a gap ⇒ discontinuity).
/// </summary>
public sealed record ArchiveStreamCompleteness(string StreamName, int ArchivedRevisionCount, long HighestContiguousRevision, long HighestRevision,
    IReadOnlyList<ArchiveRevisionRange> Gaps, IReadOnlyList<long> DuplicateRevisions, long? AssumedHead, IReadOnlyList<long> RevisionsBeyondAssumedHead)
{
    public bool IsContiguous => Gaps.Count == 0 && DuplicateRevisions.Count == 0;
}

public sealed record ArchiveCompletenessReport(Guid OrganizationId, bool ContainerAbsent, IReadOnlyList<ArchiveStreamCompleteness> Streams, IReadOnlyList<ArchiveListedObject> UnexpectedObjects)
{
    /// <summary>True only when the container exists, nothing unexpected was listed and every stream is contiguous with no duplicates.</summary>
    public bool IsClean => !ContainerAbsent && UnexpectedObjects.Count == 0 && Streams.All(s => s.IsContiguous);
}
