using AuthorizationAuthority.Application.Common.Ports;
using AuthorizationAuthority.Application.Features.Journal.Reconciliation;
using AuthorizationAuthority.Application.Features.Journal.Verification;
using AuthorizationAuthority.Domain.Features.Journal;

namespace AuthorizationAuthority.Application.Features.Journal;

/// <summary>
/// SEC-BL-01 WP-5a: delivery-time chain enforcement D+ (<c>SEC_BL_01_DR_CONTRACT_CLOSURE.md</c> §5.1 DP-1..DP-9, phase P1). It decides,
/// before <c>AppendAsync</c>, whether one pending entry of a D+-enabled stream may be delivered. The rules are WP-1's
/// <see cref="JournalChainRules"/> (no duplicated chain logic). D+ reads only the entry's own row and the archive; AuthorityDb rows,
/// receipts and delivery states are never evidence about the archive (DP-9).
/// <para>
/// Phase P1 enabled D+ for the <c>audit</c> stream (§5.6). Phase P2 (WP-5b, owner decision C-157, amendment A-6 §26) adds the <c>content</c>
/// stream first and then the <c>authorization</c> stream (P2-D4). There is no configuration that enables or disables it for any stream
/// (§5.7); the set below is fixed in code.
/// </para>
/// <para>
/// G14-R2 (contract amendment A-4, §24): the evidence is first gathered (sentinel, pinned archive instance, predecessor), then handed to the
/// caller's <c>beforeEvaluation</c> step, which establishes the durable W2-S evidence commitment (and, for a recovered revision, compares
/// the evidence with the recovery statement's expectation). Only then are the chain rules evaluated, on exactly the gathered evidence.
/// The sentinel's instance must equal the trusted expected instance (archive-instance pinning).
/// </para>
/// </summary>
public static class AuditDeliveryEnforcement
{
    /// <summary>
    /// The D+-enabled streams (§5.6 P1, and P2 per amendment A-6 §26), in delivery order. Fixed in code: no partial, sampled, warn-only or
    /// bypass mode (§5.7).
    /// </summary>
    public static IReadOnlyList<string> EnforcedStreams { get; } = [JournalStreamNames.Audit, JournalStreamNames.Authorization, JournalStreamNames.Content];

    /// <summary>Every journal stream of an organization (<see cref="JournalStreamNames"/>), in the pre-WP-5a ordinal delivery order.</summary>
    public static IReadOnlyList<string> KnownStreams { get; } = [JournalStreamNames.Audit, JournalStreamNames.Authorization, JournalStreamNames.Content];

    public static bool IsEnforced(string streamName) => EnforcedStreams.Contains(streamName, StringComparer.Ordinal);

    /// <summary>
    /// DP-2..DP-6 for <paramref name="entry"/>. R-1 of the row is checked first by <c>ArchiveProcessor</c> with its existing response
    /// (DP-10), so a <see cref="AuditDeliveryVerdictKind.Refuse"/> here never carries <c>BODY_DIGEST_MISMATCH</c> for the row itself.
    /// Archive errors other than "absent" are <see cref="AuditDeliveryVerdictKind.Unavailable"/>, never "absent" (DP-7).
    /// <para>
    /// Order (A-4 §24): sentinel and instance pin, predecessor read, then <paramref name="beforeEvaluation"/> (a non-null verdict there is
    /// returned unchanged, so a failed or contradicting commitment means no evaluation), then R-2..R-7 on the gathered evidence. Archive-level
    /// refusals (sentinel, container, unpinned instance) are returned before any commitment, because nothing of the chain is evaluated.
    /// </para>
    /// </summary>
    public static async Task<AuditDeliveryVerdict> EvaluateAsync(SecurityJournalEntry entry, ISecurityEventArchiveInventory archive, AuditDeliveryPass pass, CancellationToken cancellationToken,
        Func<AuditDeliveryEvidence, CancellationToken, Task<AuditDeliveryVerdict?>>? beforeEvaluation = null)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(archive);
        ArgumentNullException.ThrowIfNull(pass);

        // DP-3, DP-5: the sentinel is confirmed before any "absent" is interpreted (once per pass).
        if (pass.SentinelPresent is null)
        {
            try
            {
                var identity = await archive.ReadIdentityAsync(cancellationToken).ConfigureAwait(false);
                pass.SentinelPresent = identity.Status == StoredObjectStatus.Present && identity.Identity is not null;
                pass.ArchiveInstanceId = identity.Identity?.ArchiveInstanceId;
                pass.SentinelBytes = identity.Bytes;
                pass.SentinelSubtype = identity.Status == StoredObjectStatus.Present ? "SENTINEL_INVALID" : "SENTINEL_ABSENT";
                pass.SentinelDetail = identity.Status == StoredObjectStatus.Present ? "the archive identity sentinel is not a canonical v1 sentinel" : "the archive identity sentinel is " + identity.Status;
            }
            catch (ArchiveUnavailableException ex)
            {
                return AuditDeliveryVerdict.Unavailable(ex.Message);
            }
        }

        if (pass.SentinelPresent is false)
        {
            // An archive-level condition (wiped, replaced or not yet provisioned archive): refused and re-derived on every pass, without the
            // terminal row state, so that provisioning the sentinel through a baseline lets delivery resume (A-2).
            return AuditDeliveryVerdict.Refuse(JournalFindingCodes.ArchiveRegression, pass.SentinelSubtype, pass.SentinelDetail!, marksEntry: false);
        }

        // A-4 §24 archive-instance pinning: the sentinel must name the trusted expected instance (provisioning record or checkpoint 1).
        switch (pass.Pin)
        {
            case AuditArchivePinStatus.Pinned when string.Equals(pass.ExpectedArchiveInstanceId, pass.ArchiveInstanceId, StringComparison.Ordinal):
                break;
            case AuditArchivePinStatus.Pinned:
                return AuditDeliveryVerdict.Refuse(JournalFindingCodes.ArchiveRegression, JournalFindingCodes.InstanceChangedSubtype,
                    "the archive sentinel names another instance than the trusted expected instance", marksEntry: true);
            case AuditArchivePinStatus.Contradictory:
                return AuditDeliveryVerdict.Refuse(JournalFindingCodes.ArchiveRegression, JournalFindingCodes.InstanceAnchorContradictorySubtype,
                    "the trusted instance anchors (provisioning record, first checkpoint) are invalid or disagree", marksEntry: true);
            default:
                return AuditDeliveryVerdict.Refuse(JournalFindingCodes.ArchiveRegression, JournalFindingCodes.InstanceUnpinnedSubtype,
                    "no trusted expected archive instance (no provisioning record and no first checkpoint)", marksEntry: false);
        }

        // DP-3, DP-4: genesis needs no predecessor; otherwise the bytes just appended in this pass, or the archived object at k - 1.
        var revision = entry.JournalStreamRevision;
        AuditDeliveryEvidence evidence;
        if (revision == 1)
        {
            evidence = new AuditDeliveryEvidence(entry, pass.ArchiveInstanceId!, AuditPredecessorState.Genesis, null);
        }
        else if (pass.LastDeliveredRevision == revision - 1 && pass.LastDeliveredBytes is { } justAppended)
        {
            evidence = new AuditDeliveryEvidence(entry, pass.ArchiveInstanceId!, AuditPredecessorState.Present, justAppended);
        }
        else
        {
            StoredObjectRead read;
            try
            {
                read = await archive.ReadEntryAsync(entry.OrganizationId, entry.JournalStreamName, revision - 1, cancellationToken).ConfigureAwait(false);
            }
            catch (ArchiveUnavailableException ex)
            {
                return AuditDeliveryVerdict.Unavailable(ex.Message);
            }

            if (read.Status == StoredObjectStatus.ContainerAbsent)
            {
                return AuditDeliveryVerdict.Refuse(JournalFindingCodes.ArchiveRegression, "CONTAINER_ABSENT", "the archive container is absent although the sentinel was read", marksEntry: false);
            }

            evidence = read.Status == StoredObjectStatus.Absent
                ? new AuditDeliveryEvidence(entry, pass.ArchiveInstanceId!, AuditPredecessorState.Absent, null)
                : new AuditDeliveryEvidence(entry, pass.ArchiveInstanceId!, AuditPredecessorState.Present, read.Bytes);
        }

        // A-4 §24 W2-S: the caller commits to exactly this evidence before anything is evaluated.
        if (beforeEvaluation is not null && await beforeEvaluation(evidence, cancellationToken).ConfigureAwait(false) is { } preempted)
        {
            return preempted;
        }

        // DP-2: R-1..R-5 against the entry's own row.
        var inspection = JournalChainRules.InspectEntry(JournalEvidence.FromRow(entry));
        if (!inspection.IsValid)
        {
            return AuditDeliveryVerdict.Refuse(JournalFindingCodes.ChainIntegrityFailure, inspection.FailureCode, "journal row: " + inspection.FailureDetail, marksEntry: true);
        }

        switch (evidence.PredecessorState)
        {
            case AuditPredecessorState.Genesis:
                return AuditDeliveryVerdict.Deliver; // R-5 already holds for revision 1
            case AuditPredecessorState.Absent:
                return AuditDeliveryVerdict.Refuse(JournalFindingCodes.PredecessorMissing, null, "no archived object at the predecessor revision (sentinel present)", marksEntry: true);
        }

        // DP-6: the archived predecessor's own R-1..R-5 (identity, envelope, genesis), then R-6/R-7 (consecutive revision, link digest).
        var predecessor = JournalChainRules.InspectEntry(JournalEvidence.FromArchive(new JournalStorageKey(entry.OrganizationId, entry.JournalStreamName, revision - 1), evidence.PredecessorBytes!));
        if (!predecessor.IsValid)
        {
            return AuditDeliveryVerdict.Refuse(JournalFindingCodes.ChainIntegrityFailure, predecessor.FailureCode, "archived predecessor: " + predecessor.FailureDetail, marksEntry: true);
        }

        var link = JournalChainRules.CheckLink(inspection, predecessor);
        if (!link.IsValid)
        {
            return string.Equals(link.FailureCode, JournalFailureCodes.LinkMismatch, StringComparison.Ordinal)
                ? AuditDeliveryVerdict.Refuse(JournalFindingCodes.DeliveryLinkMismatch, null, "previous digest differs from SHA-256 of the archived predecessor", marksEntry: true)
                : AuditDeliveryVerdict.Refuse(JournalFindingCodes.ChainIntegrityFailure, link.FailureCode, "link: " + link.FailureDetail, marksEntry: true);
        }

        return AuditDeliveryVerdict.Deliver;
    }
}

public enum AuditDeliveryVerdictKind
{
    Deliver,

    /// <summary>A confirmed refusal: no append; a finding and a Critical alert; the stream stops (§5.3).</summary>
    Refuse,

    /// <summary>The archive could not be read (DP-7): the entry stays Pending and the pass ends; never a finding.</summary>
    Unavailable,

    /// <summary>G14-R2 (A-4 §24): the W2-S commitment could not be established durably; no evaluation, the audit stream waits (operational).</summary>
    Wait,
}

/// <summary>
/// A D+ decision. <see cref="MarksEntry"/>: the refusal concerns this entry's chain (marked <c>VerificationFailed</c>, §5.3); false for an
/// archive-level refusal (sentinel or container), which is re-derived every pass without a terminal row state.
/// </summary>
public sealed record AuditDeliveryVerdict(AuditDeliveryVerdictKind Kind, string? FindingCode, string? Subtype, string? Detail, bool MarksEntry)
{
    public static AuditDeliveryVerdict Deliver { get; } = new(AuditDeliveryVerdictKind.Deliver, null, null, null, false);

    public static AuditDeliveryVerdict Refuse(string findingCode, string? subtype, string detail, bool marksEntry) => new(AuditDeliveryVerdictKind.Refuse, findingCode, subtype, detail, marksEntry);

    public static AuditDeliveryVerdict Unavailable(string detail) => new(AuditDeliveryVerdictKind.Unavailable, null, null, detail, false);

    /// <summary>G14-R2: the audit stream waits with row code <paramref name="rowCode"/> (no finding, no state change).</summary>
    public static AuditDeliveryVerdict Wait(string rowCode, string detail) => new(AuditDeliveryVerdictKind.Wait, rowCode, null, detail, false);

    /// <summary>The <c>last_archive_error_code</c> text: <c>DPLUS_{code}[:{subtype}]</c>.</summary>
    public string ErrorCode => "DPLUS_" + FindingCode + (Subtype is null ? string.Empty : ":" + Subtype);
}

/// <summary>G14-R2 (A-4 §24): the predecessor evidence D+ evaluates.</summary>
public enum AuditPredecessorState
{
    Genesis,
    Present,
    Absent,
}

/// <summary>G14-R2 (A-4 §24): the evidence gathered for one entry, committed (W2-S) before evaluation. Bytes are never logged.</summary>
public sealed record AuditDeliveryEvidence(SecurityJournalEntry Entry, string ArchiveInstanceId, AuditPredecessorState PredecessorState, byte[]? PredecessorBytes);

/// <summary>G14-R2 (A-4 §24): the trusted expected archive instance, resolved once per pass from the provisioning record or checkpoint 1.</summary>
public enum AuditArchivePinStatus
{
    /// <summary>Not resolved: D+ refuses (fail closed).</summary>
    Unpinned,

    Pinned,

    /// <summary>An anchor is invalid, or the two anchors disagree (stream-stopping).</summary>
    Contradictory,
}

/// <summary>Per-pass D+ state for one (organization, stream): the sentinel result and the bytes most recently appended (DP-4).</summary>
public sealed class AuditDeliveryPass
{
    public bool? SentinelPresent { get; set; }

    public string? SentinelSubtype { get; set; }

    /// <summary>WP-5a.2: the archive instance named by the sentinel (written into stop markers).</summary>
    public string? ArchiveInstanceId { get; set; }

    /// <summary>G14-R2: the sentinel bytes as read (bound by digest into recovery evidence).</summary>
    public byte[]? SentinelBytes { get; set; }

    public string? SentinelDetail { get; set; }

    /// <summary>G14-R2 (A-4 §24): the instance pin for this pass (set by <c>ArchiveProcessor</c> before any evaluation).</summary>
    public AuditArchivePinStatus Pin { get; set; }

    public string? ExpectedArchiveInstanceId { get; set; }

    public long LastDeliveredRevision { get; set; }

    public byte[]? LastDeliveredBytes { get; set; }
}
