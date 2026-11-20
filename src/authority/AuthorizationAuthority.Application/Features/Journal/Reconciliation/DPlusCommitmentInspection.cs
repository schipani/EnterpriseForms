using System.Globalization;
using AuthorizationAuthority.Application.Common.Ports;
using AuthorizationAuthority.Application.Features.Journal.Recovery;

namespace AuthorizationAuthority.Application.Features.Journal.Reconciliation;

/// <summary>
/// SEC-BL-01 WP-5b (C-157 P2-D5, amendment A-6 §26.7, closes the EP-5 gap of A-4 §24.7): the verifier's independent inspection of the W2-S
/// evidence commitments of one stream against the archived entries it reads in the same sweep. Read-only; findings only (alert-only verifier,
/// OD-DV-3): nothing is repaired, re-committed or lifted. Rules VC-1..VC-9:
/// <list type="bullet">
/// <item>VC-1 <c>MALFORMED</c>: a copy that is not a canonical v1 commitment for exactly its key (organization, stream, revision, generation).</item>
/// <item>VC-2 <c>CONFLICTING_COPIES</c>: the archive copy and the journal-verification store copy differ.</item>
/// <item>VC-3 <c>COPY_MISSING</c>: only one store holds a commitment (W2-S writes both before any evaluation).</item>
/// <item>VC-4 <c>WRONG_INSTANCE</c>: the commitment names another archive instance than the archive's sentinel.</item>
/// <item>VC-5 <c>INVALID_GENERATION</c>: a generation g ≥ 1 without any stop marker of generation g − 1 in the stream.</item>
/// <item>VC-6 <c>ROW_DIGEST</c> / <c>WRONG_ROW</c>: for an archived revision, its highest-generation commitment names other row bytes than the
/// archived object, or another row identity than the AuthorityDb row.</item>
/// <item>VC-7 <c>PREDECESSOR_DIGEST</c>: that commitment's predecessor state and digest differ from the archived predecessor (genesis for 1).</item>
/// <item>VC-8 <c>MISSING</c>: an archived revision at or above the stream's first committed revision has no commitment.</item>
/// <item>VC-9 <c>BEYOND_HEAD</c>: a commitment for a revision above the AuthorityDb head.</item>
/// </list>
/// Commitments are loaded by listing both stores under the stream's prefix (one listing per store per sweep). A stream without any commitment
/// (history delivered before D+ was enabled for it) is not inspected. Lower-generation commitments of a revision are superseded by a
/// higher generation (an executed recovery); only the highest is compared with the archive.
/// </summary>
public static class DPlusCommitmentInspection
{
    public const string ConflictingCopies = "CONFLICTING_COPIES";
    public const string CopyMissing = "COPY_MISSING";
    public const string Malformed = "MALFORMED";
    public const string WrongInstance = "WRONG_INSTANCE";
    public const string InvalidGeneration = "INVALID_GENERATION";
    public const string RowDigest = "ROW_DIGEST";
    public const string WrongRow = "WRONG_ROW";
    public const string PredecessorDigest = "PREDECESSOR_DIGEST";
    public const string Missing = "MISSING";
    public const string BeyondHead = "BEYOND_HEAD";

    /// <summary>The commitments of one stream: per (revision, generation), the archive and store copies (null when absent).</summary>
    public sealed class StreamCommitments
    {
        public SortedDictionary<(long Revision, int Generation), (byte[]? Archive, byte[]? Store)> Copies { get; } = [];

        /// <summary>Objects under the stream's commitment prefix whose names are not canonical commitment keys (VC-1).</summary>
        public List<string> NonCanonicalNames { get; } = [];

        /// <summary>False when no journal-verification store is composed (only the archive copies are inspected; VC-3 not applied).</summary>
        public bool StoreInspected { get; init; }

        public bool IsEmpty => Copies.Count == 0 && NonCanonicalNames.Count == 0;
    }

    /// <summary>Lists both copies of every commitment of (organization, stream). Archive unavailability and store unavailability propagate.</summary>
    public static async Task<StreamCommitments?> LoadAsync(ISecurityEventArchiveInventory archive, IJournalVerificationRecordStore? store, Guid organizationId, string streamName,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(archive);
        if (archive is not IArchiveMetaRecords meta)
        {
            return null;
        }

        var root = organizationId.ToString("D") + "/" + streamName + "/";
        var result = new StreamCommitments { StoreInspected = store is not null };
        foreach (var item in await meta.ListMetaAsync(DPlusRecoveryNames.ArchiveCommitmentPrefix + root, cancellationToken).ConfigureAwait(false))
        {
            if (Key(item.ObjectName, DPlusRecoveryNames.ArchiveCommitmentPrefix + root) is { } key)
            {
                var (_, s) = result.Copies.GetValueOrDefault(key);
                result.Copies[key] = (item.Bytes, s);
            }
            else
            {
                result.NonCanonicalNames.Add("archive:" + item.ObjectName);
            }
        }

        if (store is not null)
        {
            foreach (var item in await store.ListAsync(DPlusRecoveryNames.StoreCommitmentPrefix + root, cancellationToken).ConfigureAwait(false))
            {
                if (Key(item.ObjectName, DPlusRecoveryNames.StoreCommitmentPrefix + root) is { } key)
                {
                    var (a, _) = result.Copies.GetValueOrDefault(key);
                    result.Copies[key] = (a, item.Bytes);
                }
                else
                {
                    result.NonCanonicalNames.Add("store:" + item.ObjectName);
                }
            }
        }

        return result;
    }

    /// <summary>The (revision, generation) of an object name under the stream root; null for a name that is not a canonical commitment key.</summary>
    private static (long Revision, int Generation)? Key(string objectName, string prefix)
    {
        var name = objectName.StartsWith(prefix, StringComparison.Ordinal) ? objectName[prefix.Length..] : objectName;
        return DPlusStreamStops.ParseMarkerFileName(name) is { } parsed ? (parsed.Revision, parsed.Generation) : null;
    }

    /// <summary>VC-1..VC-9 for the revisions this sweep read. <paramref name="archivedDigests"/>: SHA-256 (hex) of each archived object read.</summary>
    public static void Inspect(StreamCommitments commitments, Guid organizationId, string streamName, string? archiveInstanceId, IReadOnlySet<int> markerGenerations,
        IReadOnlyDictionary<long, string> archivedDigests, IReadOnlyDictionary<long, Guid> rowIds, long databaseHead, JournalComparisonResult result)
    {
        ArgumentNullException.ThrowIfNull(commitments);
        ArgumentNullException.ThrowIfNull(markerGenerations);
        ArgumentNullException.ThrowIfNull(archivedDigests);
        ArgumentNullException.ThrowIfNull(rowIds);
        ArgumentNullException.ThrowIfNull(result);
        if (commitments.IsEmpty)
        {
            return;
        }

        void Add(string subtype, long? revision, string evidence, string detail) =>
            result.Add(JournalFindingCodes.DPlusCommitmentMismatch, subtype, revision, revision, [evidence], detail);

        foreach (var name in commitments.NonCanonicalNames)
        {
            Add(Malformed, null, name, "an object under the stream's evidence-commitment prefix is not a canonical commitment key");
        }

        var valid = new Dictionary<(long Revision, int Generation), DPlusEvidenceCommitment>();
        foreach (var ((revision, generation), (archived, stored)) in commitments.Copies)
        {
            var name = DPlusRecoveryNames.Commitment(organizationId, streamName, revision, generation);
            if (archived is not null && stored is not null && !archived.AsSpan().SequenceEqual(stored))
            {
                Add(ConflictingCopies, revision, "commitment:" + name, "the two copies of an evidence commitment differ");
                continue;
            }

            if (commitments.StoreInspected && (archived is null || stored is null))
            {
                Add(CopyMissing, revision, "commitment:" + name, "an evidence commitment exists in only one of the two stores");
            }

            var bytes = archived ?? stored!;
            var parsed = DPlusRecoveryCodec.TryParse<DPlusEvidenceCommitment>(bytes);
            if (parsed is null || !string.Equals(parsed.CommitmentSchema, DPlusEvidenceCommitment.SchemaV1, StringComparison.Ordinal) || parsed.OrganizationId != organizationId
                || !string.Equals(parsed.StreamName, streamName, StringComparison.Ordinal) || parsed.StreamRevision != revision || parsed.StopGeneration != generation)
            {
                Add(Malformed, revision, "commitment:" + name, "an evidence commitment is malformed, not canonical, or for another key");
                continue;
            }

            if (archiveInstanceId is not null && !string.Equals(parsed.ArchiveInstanceId, archiveInstanceId, StringComparison.Ordinal))
            {
                Add(WrongInstance, revision, "commitment:" + name, "an evidence commitment names another archive instance than the sentinel");
            }

            if (generation > 0 && !markerGenerations.Contains(generation - 1))
            {
                Add(InvalidGeneration, revision, "commitment:" + name, "an evidence commitment of generation g has no stop marker of generation g - 1 in the stream");
            }

            if (revision > databaseHead)
            {
                Add(BeyondHead, revision, "commitment:" + name, "an evidence commitment names a revision above the AuthorityDb head");
            }

            valid[(revision, generation)] = parsed;
        }

        var latest = valid.GroupBy(v => v.Key.Revision).ToDictionary(g => g.Key, g => g.OrderByDescending(v => v.Key.Generation).First().Value);
        var firstCommitted = commitments.Copies.Keys.Select(k => k.Revision).DefaultIfEmpty(long.MaxValue).Min();
        foreach (var (revision, digest) in archivedDigests.OrderBy(a => a.Key))
        {
            if (!latest.TryGetValue(revision, out var commitment))
            {
                if (revision >= firstCommitted && !commitments.Copies.Keys.Any(k => k.Revision == revision))
                {
                    Add(Missing, revision, "archive-revision:" + revision.ToString(CultureInfo.InvariantCulture),
                        "an archived revision delivered after W2-S was enabled for the stream has no evidence commitment");
                }

                continue;
            }

            var name = "commitment:" + DPlusRecoveryNames.Commitment(organizationId, streamName, revision, commitment.StopGeneration);
            if (!string.Equals(commitment.RowBodySha256Hex, digest, StringComparison.Ordinal))
            {
                Add(RowDigest, revision, name, "the archived entry differs from the row bytes its evidence commitment bound");
            }

            if (rowIds.TryGetValue(revision, out var rowId) && commitment.RowEntryId != rowId)
            {
                Add(WrongRow, revision, name, "the evidence commitment names another journal row than the AuthorityDb row at this revision");
            }

            if (revision == 1)
            {
                if (!string.Equals(commitment.PredecessorState, nameof(AuditPredecessorState.Genesis), StringComparison.Ordinal) || commitment.PredecessorSha256Hex is not null)
                {
                    Add(PredecessorDigest, revision, name, "the genesis entry's commitment names a predecessor");
                }
            }
            else if (archivedDigests.TryGetValue(revision - 1, out var predecessor)
                     && (!string.Equals(commitment.PredecessorState, nameof(AuditPredecessorState.Present), StringComparison.Ordinal)
                         || !string.Equals(commitment.PredecessorSha256Hex, predecessor, StringComparison.Ordinal)))
            {
                Add(PredecessorDigest, revision, name, "the archived predecessor differs from the predecessor the delivered entry's commitment bound");
            }
        }
    }
}
