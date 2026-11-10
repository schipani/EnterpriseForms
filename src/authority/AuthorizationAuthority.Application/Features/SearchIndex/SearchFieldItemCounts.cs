using AuthorizationAuthority.Domain.Features.SearchIndex;

namespace AuthorizationAuthority.Application.Features.SearchIndex;

/// <summary>
/// C-141 (UF-25, U-1b): the per-field item counts of an entry. One rule, used by the derivation (W1 and backfill), the seal check
/// (S-B and every served page record), the deep sweep (S-C), the cheap sweep's SQL equivalent (S-A) and the query path.
/// <para>
/// The count of an approval is the number of the entry's postings carrying that approval: the accepted total of indexed items of
/// the approval's field at the entry's indexed operation (top-level, non-encrypted values only, exactly what the share-only
/// readability rule of R1 §E.3 enumerated). It is not in the seal message: it is a pure function of the sealed posting list, so
/// any check that verifies the seal and recounts the verified postings detects a changed count (the IndexedPostingCount
/// precedent). It is positionally aligned with <see cref="SearchRecordEntry.DerivedFieldSearchApprovalIds"/>.
/// </para>
/// </summary>
public static class SearchFieldItemCounts
{
    /// <summary>The counts for <paramref name="derivedApprovalIds"/> (in that order) from the approval ids of the postings.</summary>
    public static int[] Derive(IReadOnlyList<Guid> derivedApprovalIds, IEnumerable<Guid> postingApprovalIds)
    {
        ArgumentNullException.ThrowIfNull(derivedApprovalIds);
        ArgumentNullException.ThrowIfNull(postingApprovalIds);
        var tally = postingApprovalIds.GroupBy(id => id).ToDictionary(g => g.Key, g => g.Count());
        return derivedApprovalIds.Select(id => tally.GetValueOrDefault(id)).ToArray();
    }

    /// <summary>
    /// Structural validity of the stored counts for the entry's derivation version: null below version 3; from version 3, present,
    /// the same length as the approval ids and non-negative.
    /// </summary>
    public static bool WellFormed(SearchRecordEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return entry.EntryDerivationVersion < SearchPostingDerivationV1.FieldItemCountEntryDerivationVersion
            ? entry.DerivedFieldItemCounts is null
            : entry.DerivedFieldItemCounts is { } counts && counts.Length == entry.DerivedFieldSearchApprovalIds.Length && counts.All(c => c >= 0);
    }

    /// <summary>The stored counts are well formed and equal the counts of <paramref name="postingApprovalIds"/> (version 3 and later).</summary>
    public static bool MatchPostings(SearchRecordEntry entry, IEnumerable<Guid> postingApprovalIds)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (!WellFormed(entry))
        {
            return false;
        }

        return entry.DerivedFieldItemCounts is not { } stored || stored.AsSpan().SequenceEqual(Derive(entry.DerivedFieldSearchApprovalIds, postingApprovalIds));
    }

    /// <summary>The stored count of <paramref name="approvalId"/>; false when the entry has no well-formed count for it.</summary>
    public static bool TryGet(Guid[]? approvalIds, int[]? counts, Guid approvalId, out int count)
    {
        count = 0;
        if (approvalIds is null || counts is null || counts.Length != approvalIds.Length)
        {
            return false;
        }

        var position = Array.IndexOf(approvalIds, approvalId);
        if (position < 0 || counts[position] < 0 || Array.IndexOf(approvalIds, approvalId, position + 1) >= 0)
        {
            return false;
        }

        count = counts[position];
        return true;
    }
}
