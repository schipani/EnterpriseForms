using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using AuthorizationAuthority.Application.Common.Ports;
using AuthorizationAuthority.Application.Features.Journal.Verification;
using AuthorizationAuthority.Domain.Features.Journal;

namespace AuthorizationAuthority.Application.Features.Journal.Reconciliation;

/// <summary>
/// SEC-BL-01 WP-3/WP-4 (C-155): the one comparison of an AuthorityDb journal stream with the archive, shared by the baseline (§10), the
/// incremental sweep (§8.2) and the full sweep (§8.3). Read-only on both sides; bounded batches; findings and inconclusive reasons are
/// collected, nothing is repaired. Chain rules come from <see cref="JournalChainRules"/> (WP-1); listing and completeness from
/// <see cref="ArchiveCompleteness"/> (WP-2).
/// </summary>
public static class JournalStreamComparer
{
    public static async Task<JournalComparisonResult> CompareAsync(IJournalVerificationSnapshot database, ISecurityEventArchiveInventory archive, JournalComparisonRequest request,
        JournalVerificationLimits limits, TimeProvider clock, CancellationToken cancellationToken, IJournalVerificationRecordStore? commitmentStore = null)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(archive);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(limits);
        ArgumentNullException.ThrowIfNull(clock);
        var started = clock.GetTimestamp();
        var result = new JournalComparisonResult();
        var org = request.OrganizationId;
        var stream = request.StreamName;

        var head = await database.ReadStreamHeadAsync(org, stream, cancellationToken).ConfigureAwait(false);
        if (head is null)
        {
            result.Inconclusive(JournalInconclusiveReasons.OrganizationUnknown, null);
            return Finish(result, clock, started);
        }

        var databaseHead = head.MaximumRowRevision;
        result.DatabaseHead = databaseHead;
        if (head.HeadRevisionColumn != databaseHead)
        {
            result.Add(JournalFindingCodes.ChainIntegrityFailure, JournalFindingCodes.OrganizationHeadMismatch, Math.Min(head.HeadRevisionColumn, databaseHead) + 1,
                Math.Max(head.HeadRevisionColumn, databaseHead), [], "the organization head revision column differs from the highest journal row");
        }

        // ---- Archive: complete listing (AE-1, AE-2), never probing. WP-4A (A-1): an incremental sweep lists only above the trusted revision. ----
        var trusted = request.TrustedRevision ?? 0;
        var bounded = request.BoundedListing && trusted > 0;
        ArchiveListing listing;
        ArchiveListing? organizationListing = null;
        try
        {
            listing = bounded
                ? await ArchiveCompleteness.CollectAboveAsync(archive, org, stream, trusted, limits.ListingPageSize, cancellationToken).ConfigureAwait(false)
                : await ArchiveCompleteness.CollectAsync(archive, org, stream, limits.ListingPageSize, cancellationToken).ConfigureAwait(false);
            if (request.ReportOrganizationLevelObjects)
            {
                organizationListing = await ArchiveCompleteness.CollectAsync(archive, org, null, limits.ListingPageSize, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (ArchiveUnavailableException)
        {
            result.Unavailable(JournalFindingCodes.ArchiveUnavailable, JournalInconclusiveReasons.ArchiveUnavailable, "archive listing unavailable");
            return Finish(result, clock, started);
        }

        // WP-5b (A-6 §26.7, EP-5): the stream's W2-S evidence commitments, both copies, compared below with the archived entries this sweep reads.
        DPlusCommitmentInspection.StreamCommitments? commitments;
        IReadOnlySet<int> markerGenerations;
        string? sentinelInstance;
        try
        {
            commitments = await DPlusCommitmentInspection.LoadAsync(archive, commitmentStore, org, stream, cancellationToken).ConfigureAwait(false);
            markerGenerations = commitments is { IsEmpty: false }
                ? (await archive.ReadStopMarkersAsync(org, stream, cancellationToken).ConfigureAwait(false))
                    .Select(m => DPlusStreamStops.ParseMarkerObjectName(m.ObjectName, org, stream)?.Generation).OfType<int>().ToHashSet()
                : new HashSet<int>();
            sentinelInstance = commitments is { IsEmpty: false } ? (await archive.ReadIdentityAsync(cancellationToken).ConfigureAwait(false)).Identity?.ArchiveInstanceId : null;
        }
        catch (ArchiveUnavailableException)
        {
            result.Unavailable(JournalFindingCodes.ArchiveUnavailable, JournalInconclusiveReasons.ArchiveUnavailable, "archive commitment listing unavailable");
            return Finish(result, clock, started);
        }
        catch (CheckpointStoreUnavailableException)
        {
            result.Unavailable(JournalFindingCodes.CheckpointStoreUnavailable, JournalInconclusiveReasons.CheckpointStoreUnavailable, "journal-verification store commitment listing unavailable");
            return Finish(result, clock, started);
        }

        var archivedDigests = new Dictionary<long, string>();
        var verifiedRowDigests = new Dictionary<long, string>();
        var rowIds = new Dictionary<long, Guid>();
        result.ArchiveObjectsListed = listing.Objects.Count + (organizationListing?.Objects.Count ?? 0);
        var completeness = ArchiveCompleteness.Analyze(listing, new Dictionary<string, long>(StringComparer.Ordinal) { [stream] = databaseHead });
        var streamReport = completeness.Streams.Single();
        var listedRevisions = new HashSet<long>(listing.Objects
            .Where(o => o.IsCanonicalEntryKey && o.StreamRevision is not null && ArchiveCompleteness.KnownStreams.Contains(o.StreamName!, StringComparer.Ordinal))
            .Select(o => o.StreamRevision!.Value));
        var contiguity = Contiguity(listedRevisions, bounded ? trusted : 0, request.AcceptedArchiveGaps ?? []);
        var archiveHead = contiguity.Head;
        result.ArchiveContiguousHead = archiveHead;
        result.ArchiveHighest = Math.Max(streamReport.HighestRevision, bounded ? trusted : 0);
        result.BoundedListing = bounded;
        if (listing.ContainerAbsent && request.TrustedRevision is > 0)
        {
            result.Add(JournalFindingCodes.ArchiveRegression, null, 1, request.TrustedRevision, [], "the archive container is absent although revisions were verified");
        }

        foreach (var (gapFirst, gapLast) in contiguity.Gaps)
        {
            result.Add(JournalFindingCodes.ArchiveDiscontinuity, null, gapFirst, gapLast, [], "archived revisions are missing below the highest archived revision");
        }

        foreach (var duplicate in streamReport.DuplicateRevisions)
        {
            result.Add(JournalFindingCodes.ArchiveDuplicate, null, duplicate, duplicate, [], "more than one archived object claims this revision");
        }

        foreach (var unexpected in completeness.UnexpectedObjects)
        {
            result.Add(JournalFindingCodes.ArchiveUnexpectedObject, null, null, null, ["archive:" + unexpected.ObjectName], "object under the stream prefix that is not a canonical entry");
        }

        if (organizationListing is not null)
        {
            var streamNames = new HashSet<string>(completeness.UnexpectedObjects.Select(o => o.ObjectName), StringComparer.Ordinal);
            foreach (var unexpected in ArchiveCompleteness.Analyze(organizationListing).UnexpectedObjects.Where(o => !streamNames.Contains(o.ObjectName)))
            {
                result.Add(JournalFindingCodes.ArchiveUnexpectedObject, null, null, null, ["archive:" + unexpected.ObjectName], "object under the organization prefix that is not a canonical entry of a known stream");
            }
        }

        // WP-5a.2 (A-3 SM-7): the reserved meta/ prefix is enumerated completely with the organization-level objects. The sentinel and
        // canonical stop markers are recognized (this organization's markers are validated); anything else there is unexpected. Never ignored.
        if (request.ReportOrganizationLevelObjects)
        {
            try
            {
                await InspectMetaAsync(archive, org, result, cancellationToken).ConfigureAwait(false);
            }
            catch (ArchiveUnavailableException)
            {
                result.Unavailable(JournalFindingCodes.ArchiveUnavailable, JournalInconclusiveReasons.ArchiveUnavailable, "archive meta/ listing unavailable");
                return Finish(result, clock, started);
            }
        }

        if (archiveHead > databaseHead)
        {
            result.Add(JournalFindingCodes.BehindArchive, null, databaseHead + 1, archiveHead, [], "the archive holds revisions above the AuthorityDb head (restore, rollback or tail deletion)");
        }

        // ---- Trusted checkpoint anchor in the archive (VF-3) ----
        if (trusted > 0)
        {
            var anchorRead = await ReadArchiveAsync(archive, org, stream, trusted, result, cancellationToken).ConfigureAwait(false);
            if (result.IsInconclusive)
            {
                return Finish(result, clock, started);
            }

            if (anchorRead.Status != StoredObjectStatus.Present || !SHA256.HashData(anchorRead.Bytes!).AsSpan().SequenceEqual(request.TrustedDigest))
            {
                result.Add(JournalFindingCodes.ArchiveRegression, null, trusted, trusted, ["archive-revision:" + trusted.ToString(CultureInfo.InvariantCulture)],
                    "the archived object at the verified revision is absent or differs from the checkpoint digest");
            }

            if (databaseHead < trusted)
            {
                result.Add(JournalFindingCodes.ChainIntegrityFailure, JournalFailureCodes.RevisionGap, databaseHead + 1, trusted, [],
                    "verified revisions are missing from AuthorityDb (rollback or deletion below the checkpoint)");
            }
        }

        // ---- AuthorityDb walk in bounded batches, archive comparison alongside ----
        byte[]? previousRowDigest = null;
        byte[]? previousArchivedDigest = request.ByteCompareFromRevision > 1 && request.ByteCompareFromRevision - 1 == trusted ? request.TrustedDigest : null;
        byte[]? archiveHeadDigest = archiveHead == trusted && trusted > 0 ? request.TrustedDigest : null;
        var lag = new RangeCollector();
        var refused = new RangeCollector();
        for (var first = Math.Max(1, request.WalkFromRevision); first <= databaseHead; first += limits.RowBatchSize)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (result.DatabaseRowsInspected >= limits.MaximumRowsPerRun)
            {
                result.Inconclusive(JournalInconclusiveReasons.ReadLimitExceeded, first);
                break;
            }

            var last = Math.Min(databaseHead, first + limits.RowBatchSize - 1);
            var rows = await database.ReadRowsAsync(org, stream, first, last, limits.RowBatchSize, cancellationToken).ConfigureAwait(false);
            result.DatabaseRowsInspected += rows.Count;
            result.RowBatches++;

            var anchorFirst = first == request.WalkFromRevision && trusted > 0 && request.WalkFromRevision == trusted ? request.TrustedDigest : null;
            var sequence = JournalChainRules.VerifySequence(rows.Select(Evidence),
                new JournalSequenceExpectation(org, stream, first, PredecessorDigest: first > request.WalkFromRevision ? previousRowDigest : null, FirstEntryDigest: anchorFirst, LastRevision: last),
                cancellationToken);
            foreach (var finding in sequence.Findings)
            {
                result.AddChainFinding(finding, rows.FirstOrDefault(r => r.StreamRevision == finding.StreamRevision), "AuthorityDb row");
            }

            if (trusted >= first && trusted <= last && request.WalkFromRevision < trusted)
            {
                var row = rows.FirstOrDefault(r => r.StreamRevision == trusted);
                if (row is not null && !SHA256.HashData(Encoding.UTF8.GetBytes(row.BodyJson)).AsSpan().SequenceEqual(request.TrustedDigest))
                {
                    result.Add(JournalFindingCodes.AnchorMismatch, null, trusted, trusted, ["row:" + row.SecurityJournalEntryId.ToString("D")], "the AuthorityDb row at the verified revision differs from the checkpoint digest");
                }
            }

            // WP-5b (EP-5): when rows at or below the trusted checkpoint are not re-read from the archive (incremental sweeps), their chain-verified body
            // digests stand for the archived bytes when commitments are compared. WP-7A (VF-15): the full sweep reads them, and the archived digests win.
            if (commitments is { IsEmpty: false })
            {
                foreach (var verified in rows.Where(r => r.StreamRevision <= trusted))
                {
                    verifiedRowDigests[verified.StreamRevision] = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(verified.BodyJson)));
                    rowIds[verified.StreamRevision] = verified.SecurityJournalEntryId;
                }
            }

            var lastRow = rows.Count > 0 && rows[^1].StreamRevision == last ? rows[^1] : null;
            previousRowDigest = lastRow is null ? null : SHA256.HashData(Encoding.UTF8.GetBytes(lastRow.BodyJson));

            // Byte comparison and archive-side chain for archived revisions in this batch (VF-5, VF-7).
            var compareFirst = Math.Max(first, request.ByteCompareFromRevision);
            var compareLast = Math.Min(last, archiveHead);
            if (compareFirst <= compareLast)
            {
                // WP-7A: the batch's listed objects are read ahead with bounded concurrency (read-only; processed below strictly in revision order,
                // so the outcome and every finding are those of the sequential walk).
                var prefetched = await PrefetchAsync(archive, org, stream, compareFirst, compareLast, listedRevisions, limits.ArchiveReadConcurrency, cancellationToken).ConfigureAwait(false);
                var archived = new List<JournalEvidence>();
                var segmentFirst = compareFirst;
                for (var revision = compareFirst; revision <= compareLast; revision++)
                {
                    if (!listedRevisions.Contains(revision))
                    {
                        // Only an accepted archive gap can lie below the contiguous head (A-1): verify the segment before it, then continue the
                        // archive-side chain from the AuthorityDb row's stored digest, which the accepted exception's evidence digest pins.
                        VerifyArchivedSegment(archived, org, stream, segmentFirst, revision - 1, previousArchivedDigest, result, cancellationToken);
                        archived.Clear();
                        previousArchivedDigest = rows.FirstOrDefault(r => r.StreamRevision == revision)?.EventDigest;
                        segmentFirst = revision + 1;
                        continue;
                    }

                    var read = prefetched[revision];
                    if (read is null)
                    {
                        result.Unavailable(JournalFindingCodes.ArchiveUnavailable, JournalInconclusiveReasons.ArchiveUnavailable, "archive read unavailable");
                        return Finish(result, clock, started);
                    }

                    result.ArchiveObjectsRead++;
                    if (read.Status != StoredObjectStatus.Present)
                    {
                        result.Inconclusive(JournalInconclusiveReasons.ListedObjectMissing, revision);
                        return Finish(result, clock, started);
                    }

                    var bytes = read.Bytes!;
                    archived.Add(JournalEvidence.FromArchive(new JournalStorageKey(org, stream, revision), bytes));
                    if (commitments is { IsEmpty: false })
                    {
                        archivedDigests[revision] = Convert.ToHexStringLower(SHA256.HashData(bytes));
                    }

                    if (revision == archiveHead)
                    {
                        archiveHeadDigest = SHA256.HashData(bytes);
                    }

                    var row = rows.FirstOrDefault(r => r.StreamRevision == revision);
                    if (row is not null && commitments is { IsEmpty: false })
                    {
                        rowIds[revision] = row.SecurityJournalEntryId;
                    }

                    if (row is not null && !Encoding.UTF8.GetBytes(row.BodyJson).AsSpan().SequenceEqual(bytes))
                    {
                        result.Add(JournalFindingCodes.AnchorMismatch, null, revision, revision, ["row:" + row.SecurityJournalEntryId.ToString("D"), "archive-revision:" + revision.ToString(CultureInfo.InvariantCulture)],
                            "the AuthorityDb row differs from the archived bytes");
                    }
                }

                VerifyArchivedSegment(archived, org, stream, segmentFirst, compareLast, previousArchivedDigest, result, cancellationToken);
                if (archived.Count > 0)
                {
                    previousArchivedDigest = SHA256.HashData(archived[^1].Body!.Value.Span);
                }
            }

            // Rows above the contiguous archive head: delivery state is never evidence of archival (VF-8, VF-10).
            // Rows archived beyond a gap are not receipts to doubt: the gap is already an ARCHIVE_DISCONTINUITY finding.
            foreach (var row in rows.Where(r => r.StreamRevision > archiveHead && !listedRevisions.Contains(r.StreamRevision)))
            {
                if (string.Equals(row.ArchiveDeliveryState, ArchiveDeliveryStates.Anchored, StringComparison.Ordinal) || row.HasArchiveReceipt)
                {
                    var second = await ReadArchiveAsync(archive, org, stream, row.StreamRevision, result, cancellationToken).ConfigureAwait(false);
                    if (result.IsInconclusive)
                    {
                        return Finish(result, clock, started);
                    }

                    if (second.Status == StoredObjectStatus.Present)
                    {
                        result.Inconclusive(JournalInconclusiveReasons.ArchiveChangedDuringSweep, row.StreamRevision);
                        return Finish(result, clock, started);
                    }

                    result.Add(JournalFindingCodes.FakeReceipt, null, row.StreamRevision, row.StreamRevision, ["row:" + row.SecurityJournalEntryId.ToString("D")],
                        "the row claims archival but the archive has no object at its key");
                }
                else if (string.Equals(row.ArchiveDeliveryState, ArchiveDeliveryStates.VerificationFailed, StringComparison.Ordinal))
                {
                    refused.Add(row.StreamRevision);
                }
                else if (request.Now - row.JournalCreatedOn > request.DeliveryLagThreshold)
                {
                    lag.Add(row.StreamRevision);
                }
            }
        }

        foreach (var (firstRevision, lastRevision) in lag.Ranges)
        {
            result.Add(JournalFindingCodes.DeliveryLag, null, firstRevision, lastRevision, [], "rows pending archival for longer than the delivery-lag threshold");
        }

        foreach (var (firstRevision, lastRevision) in refused.Ranges)
        {
            result.Add(JournalFindingCodes.DeliveryRefused, null, firstRevision, lastRevision, [], "rows the delivery path already refused (VerificationFailed)");
        }

        if (commitments is { IsEmpty: false })
        {
            foreach (var (revision, digest) in verifiedRowDigests)
            {
                archivedDigests.TryAdd(revision, digest);
            }

            if (request.TrustedRevision is > 0 && request.TrustedDigest is { } trustedDigest)
            {
                archivedDigests[trusted] = Convert.ToHexStringLower(trustedDigest); // the checkpoint's own digest of the verified revision
            }

            DPlusCommitmentInspection.Inspect(commitments, org, stream, sentinelInstance, markerGenerations, archivedDigests, rowIds, databaseHead, result);
        }

        // The organization head digest column must name the last row (checked only when the walk reached the head).
        if (databaseHead >= Math.Max(1, request.WalkFromRevision) && previousRowDigest is not null && head.HeadDigestColumn is not null
            && !head.HeadDigestColumn.AsSpan().SequenceEqual(previousRowDigest))
        {
            result.Add(JournalFindingCodes.ChainIntegrityFailure, JournalFindingCodes.OrganizationHeadMismatch, databaseHead, databaseHead, [], "the organization head digest column differs from the last journal row");
        }

        result.ArchiveHeadDigest = archiveHead == 0 ? null : archiveHeadDigest;
        return Finish(result, clock, started);
    }

    /// <summary>Reserved names of the archive's <c>meta/</c> prefix (identical in the Blob and in-memory adapters).</summary>
    internal const string SentinelObjectName = "meta/archive-identity.json";

    internal const string StopMarkerPrefix = "meta/dplus-stops/";

    /// <summary>G14-R2 (A-4 §24): reserved prefixes of recovery records and W2-S evidence commitments.</summary>
    internal const string RecoveryPrefix = "meta/dplus-recoveries/";

    internal const string CommitmentPrefix = "meta/dplus-intents/";

    private static async Task InspectMetaAsync(ISecurityEventArchiveInventory archive, Guid org, JournalComparisonResult result, CancellationToken cancellationToken)
    {
        var markerStreams = new SortedSet<string>(StringComparer.Ordinal);
        var commitments = new List<(string Name, string Stream, long Revision, int Generation)>();
        foreach (var name in await archive.ListMetaObjectNamesAsync(cancellationToken).ConfigureAwait(false))
        {
            if (string.Equals(name, SentinelObjectName, StringComparison.Ordinal))
            {
                continue;
            }

            var parts = name.StartsWith(StopMarkerPrefix, StringComparison.Ordinal) ? name[StopMarkerPrefix.Length..].Split('/') : [];
            if (parts.Length == 3 && Guid.TryParseExact(parts[0], "D", out var markerOrganization) && ArchiveCompleteness.KnownStreams.Contains(parts[1], StringComparer.Ordinal)
                && DPlusStreamStops.ParseMarkerFileName(parts[2]) is not null)
            {
                if (markerOrganization == org)
                {
                    markerStreams.Add(parts[1]);
                }

                continue; // another organization's marker is validated by that organization's sweep
            }

            // G14-R2: recovery records (validated with their signatures by D+ on every pass) and evidence commitments (validated here).
            var recovery = name.StartsWith(RecoveryPrefix, StringComparison.Ordinal) ? name[RecoveryPrefix.Length..].Split('/') : [];
            if (recovery.Length >= 3 && Guid.TryParseExact(recovery[0], "D", out _) && ArchiveCompleteness.KnownStreams.Contains(recovery[1], StringComparer.Ordinal))
            {
                continue;
            }

            var intent = name.StartsWith(CommitmentPrefix, StringComparison.Ordinal) ? name[CommitmentPrefix.Length..].Split('/') : [];
            if (intent.Length == 3 && Guid.TryParseExact(intent[0], "D", out var intentOrganization) && ArchiveCompleteness.KnownStreams.Contains(intent[1], StringComparer.Ordinal)
                && DPlusStreamStops.ParseMarkerFileName(intent[2]) is { } key)
            {
                if (intentOrganization == org)
                {
                    commitments.Add((name, intent[1], key.Revision, key.Generation));
                }

                continue;
            }

            result.Add(JournalFindingCodes.ArchiveUnexpectedObject, null, null, null, ["archive:" + name],
                "object under the reserved meta/ prefix that is neither the sentinel, a canonical stop marker, a recovery record nor an evidence commitment");
        }

        foreach (var stream in markerStreams)
        {
            foreach (var item in await archive.ReadStopMarkersAsync(org, stream, cancellationToken).ConfigureAwait(false))
            {
                var key = DPlusStreamStops.ParseMarkerObjectName(item.ObjectName, org, stream);
                if (key is null || DPlusStreamStops.TryParse(item.Bytes, org, stream, key.Value.Revision, key.Value.Generation) is null)
                {
                    result.Add(JournalFindingCodes.DPlusStopMarkerInvalid, null, key?.Revision, key?.Revision, ["archive:" + item.ObjectName],
                        "a D+ stop marker is malformed, not canonical, or for another scope");
                }
            }
        }

        if (commitments.Count > 0 && archive is IArchiveMetaRecords meta)
        {
            foreach (var (name, stream, revision, generation) in commitments)
            {
                var read = await meta.ReadMetaAsync(name, cancellationToken).ConfigureAwait(false);
                var commitment = read.Status == StoredObjectStatus.Present ? Recovery.DPlusRecoveryCodec.TryParse<Recovery.DPlusEvidenceCommitment>(read.Bytes) : null;
                if (commitment is null || !string.Equals(commitment.CommitmentSchema, Recovery.DPlusEvidenceCommitment.SchemaV1, StringComparison.Ordinal) || commitment.OrganizationId != org
                    || !string.Equals(commitment.StreamName, stream, StringComparison.Ordinal) || commitment.StreamRevision != revision || commitment.StopGeneration != generation)
                {
                    result.Add(JournalFindingCodes.RecoveryRecordInvalid, "EVIDENCE_COMMITMENT", revision, revision, ["archive:" + name], "an evidence commitment is malformed, not canonical, or for another scope");
                }
            }
        }
    }

    private static void VerifyArchivedSegment(List<JournalEvidence> archived, Guid org, string stream, long first, long last, byte[]? predecessorDigest, JournalComparisonResult result,
        CancellationToken cancellationToken)
    {
        if (archived.Count == 0 || first > last)
        {
            return;
        }

        var sequence = JournalChainRules.VerifySequence(archived,
            new JournalSequenceExpectation(org, stream, first, PredecessorDigest: first > 1 ? predecessorDigest : null, LastRevision: last), cancellationToken);
        foreach (var finding in sequence.Findings)
        {
            result.AddChainFinding(finding, null, "archived object");
        }
    }

    /// <summary>
    /// Contiguity from <paramref name="startAfter"/> (0: from revision 1; the trusted revision for a bounded listing): every missing range
    /// below the highest listed revision is a gap; the head advances across a gap only when an accepted archive gap covers it entirely.
    /// </summary>
    internal static (long Head, IReadOnlyList<(long First, long Last)> Gaps) Contiguity(IReadOnlySet<long> listed, long startAfter, IReadOnlyList<(long First, long Last)> acceptedGaps)
    {
        var ordered = listed.Where(r => r > startAfter).Order().ToList();
        var gaps = new List<(long First, long Last)>();
        var head = startAfter;
        var blocked = false;
        var expected = startAfter + 1;
        foreach (var revision in ordered)
        {
            if (revision > expected)
            {
                var gapFirst = expected;
                var gapLast = revision - 1;
                gaps.Add((gapFirst, gapLast));
                blocked |= !acceptedGaps.Any(a => a.First <= gapFirst && gapLast <= a.Last);
            }

            if (!blocked)
            {
                head = revision;
            }

            expected = revision + 1;
        }

        return (head, gaps);
    }

    /// <summary>
    /// Reads the listed revisions of <paramref name="first"/>..<paramref name="last"/> with at most <paramref name="concurrency"/> reads in flight.
    /// A <c>null</c> value is an unavailable read (the caller reports it at that revision, in order).
    /// </summary>
    private static async Task<Dictionary<long, StoredObjectRead?>> PrefetchAsync(ISecurityEventArchiveInventory archive, Guid org, string stream, long first, long last,
        HashSet<long> listed, int concurrency, CancellationToken cancellationToken)
    {
        var revisions = new List<long>();
        for (var revision = first; revision <= last; revision++)
        {
            if (listed.Contains(revision))
            {
                revisions.Add(revision);
            }
        }

        var reads = new StoredObjectRead?[revisions.Count];
        await Parallel.ForAsync(0, revisions.Count, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, concurrency), CancellationToken = cancellationToken }, async (i, token) =>
        {
            try
            {
                reads[i] = await archive.ReadEntryAsync(org, stream, revisions[i], token).ConfigureAwait(false);
            }
            catch (ArchiveUnavailableException)
            {
                reads[i] = null;
            }
        }).ConfigureAwait(false);
        var byRevision = new Dictionary<long, StoredObjectRead?>(revisions.Count);
        for (var i = 0; i < revisions.Count; i++)
        {
            byRevision[revisions[i]] = reads[i];
        }

        return byRevision;
    }

    private static async Task<StoredObjectRead> ReadArchiveAsync(ISecurityEventArchiveInventory archive, Guid org, string stream, long revision, JournalComparisonResult result, CancellationToken cancellationToken)
    {
        try
        {
            var read = await archive.ReadEntryAsync(org, stream, revision, cancellationToken).ConfigureAwait(false);
            result.ArchiveObjectsRead++;
            return read;
        }
        catch (ArchiveUnavailableException)
        {
            result.Unavailable(JournalFindingCodes.ArchiveUnavailable, JournalInconclusiveReasons.ArchiveUnavailable, "archive read unavailable");
            return StoredObjectRead.Absent;
        }
    }

    internal static JournalEvidence Evidence(JournalRowSnapshot row) => new(
        new JournalStorageKey(row.OrganizationId, row.StreamName, row.StreamRevision), Encoding.UTF8.GetBytes(row.BodyJson),
        new JournalRowColumns(row.OrganizationId, row.StreamName, row.StreamRevision, row.EventKind, row.PreviousDigest, row.EventDigest));

    private static JournalComparisonResult Finish(JournalComparisonResult result, TimeProvider clock, long started)
    {
        result.ElapsedMilliseconds = (long)clock.GetElapsedTime(started).TotalMilliseconds;
        return result;
    }

    private sealed class RangeCollector
    {
        public List<(long First, long Last)> Ranges { get; } = [];

        public void Add(long revision)
        {
            if (Ranges.Count > 0 && Ranges[^1].Last == revision - 1)
            {
                Ranges[^1] = (Ranges[^1].First, revision);
            }
            else
            {
                Ranges.Add((revision, revision));
            }
        }
    }
}

/// <summary>
/// What to compare. <see cref="WalkFromRevision"/>: first AuthorityDb row read (1 for baseline and full, the checkpoint revision for an
/// incremental sweep). <see cref="TrustedRevision"/>/<see cref="TrustedDigest"/>: the checkpoint anchor (absent for a baseline).
/// <see cref="ByteCompareFromRevision"/>: first revision whose archived bytes are read and compared (1 for a baseline and, since WP-7A
/// (A-9 VF-15), for a full sweep; checkpoint + 1 for an incremental sweep). <see cref="ReportOrganizationLevelObjects"/>: also list the organization prefix for unexpected objects outside the
/// known streams (the audit stream's full sweep and every baseline of the audit stream carry this).
/// <para>WP-4A (A-1): <see cref="AcceptedArchiveGaps"/> are archive gaps covered by verified accepted exceptions (the head may cross them);
/// <see cref="BoundedListing"/> lists only above the trusted revision (incremental sweeps; gaps and objects below it wait for the full sweep).</para>
/// </summary>
public sealed record JournalComparisonRequest(Guid OrganizationId, string StreamName, long WalkFromRevision, long? TrustedRevision, byte[]? TrustedDigest,
    long ByteCompareFromRevision, bool ReportOrganizationLevelObjects, DateTimeOffset Now, TimeSpan DeliveryLagThreshold,
    IReadOnlyList<(long First, long Last)>? AcceptedArchiveGaps = null, bool BoundedListing = false);

/// <summary>Bounds (owner WP-4 §10): row batch size, listing page size, and the most rows one run may read (beyond it the run is inconclusive).
/// WP-7A: <see cref="ArchiveReadConcurrency"/> bounds the archive reads in flight within one row batch (read-only prefetch; results are
/// processed in revision order).</summary>
public sealed record JournalVerificationLimits(int RowBatchSize = 1000, int ListingPageSize = 1000, long MaximumRowsPerRun = 5_000_000, int ArchiveReadConcurrency = 8);

/// <summary>A finding before the run outcome and checkpoint are known.</summary>
public sealed record JournalPendingFinding(string FindingCode, string? Subtype, long? FirstRevision, long? LastRevision, IReadOnlyList<string> EvidenceReferences, string Detail);

public sealed class JournalComparisonResult
{
    private readonly List<JournalPendingFinding> findings = [];
    private readonly List<(string Reason, long? Revision)> inconclusive = [];

    public IReadOnlyList<JournalPendingFinding> Findings => findings;

    public IReadOnlyList<(string Reason, long? Revision)> InconclusiveReasons => inconclusive;

    public bool IsInconclusive => inconclusive.Count > 0;

    public bool HasSecurityFinding => findings.Any(f => JournalFindingCodes.SeverityOf(f.FindingCode) == JournalFindingSeverity.Security);

    public long DatabaseHead { get; internal set; }

    public long ArchiveContiguousHead { get; internal set; }

    public long ArchiveHighest { get; internal set; }

    /// <summary>True when the archive was listed only above the trusted revision (incremental, A-1); never a full-history verification.</summary>
    public bool BoundedListing { get; internal set; }

    /// <summary>SHA-256 of the archived object at <see cref="ArchiveContiguousHead"/>, when it was read or is the trusted anchor.</summary>
    public byte[]? ArchiveHeadDigest { get; internal set; }

    public long DatabaseRowsInspected { get; internal set; }

    public long ArchiveObjectsListed { get; internal set; }

    public long ArchiveObjectsRead { get; internal set; }

    public int RowBatches { get; internal set; }

    public long ElapsedMilliseconds { get; internal set; }

    internal void Add(string code, string? subtype, long? first, long? last, IReadOnlyList<string> evidence, string detail) =>
        findings.Add(new JournalPendingFinding(code, subtype, first, last, evidence, detail));

    internal void Inconclusive(string reason, long? revision) => inconclusive.Add((reason, revision));

    internal void Unavailable(string findingCode, string reason, string detail)
    {
        findings.Add(new JournalPendingFinding(findingCode, null, null, null, [], detail));
        inconclusive.Add((reason, null));
    }

    internal void AddChainFinding(JournalFinding finding, JournalRowSnapshot? row, string source)
    {
        switch (finding.Outcome)
        {
            case JournalCheckOutcome.Unsupported:
                Inconclusive(JournalInconclusiveReasons.EnvelopeUnsupported, finding.StreamRevision);
                return;
            case JournalCheckOutcome.Malformed:
                Inconclusive(JournalInconclusiveReasons.EnvelopeMalformed, finding.StreamRevision);
                return;
            case JournalCheckOutcome.Unavailable:
                Inconclusive(JournalInconclusiveReasons.ArchiveUnavailable, finding.StreamRevision);
                return;
        }

        IReadOnlyList<string> evidence = row is null ? [] : ["row:" + row.SecurityJournalEntryId.ToString("D")];
        if (string.Equals(finding.Code, JournalFailureCodes.AnchorMismatch, StringComparison.Ordinal))
        {
            Add(JournalFindingCodes.AnchorMismatch, null, finding.StreamRevision, finding.StreamRevision, evidence, source + ": " + finding.Detail);
        }
        else
        {
            Add(JournalFindingCodes.ChainIntegrityFailure, finding.Code, finding.StreamRevision, finding.StreamRevision, evidence, source + ": " + finding.Detail);
        }
    }
}
