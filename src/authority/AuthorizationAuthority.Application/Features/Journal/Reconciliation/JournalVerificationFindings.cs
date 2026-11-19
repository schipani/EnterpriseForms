using System.Security.Cryptography;
using System.Text;
using AuthorizationAuthority.Application.Common;

namespace AuthorizationAuthority.Application.Features.Journal.Reconciliation;

/// <summary>Finding codes of <c>docs/security/SEC_BL_01_DR_CONTRACT_CLOSURE.md</c> §9 plus the WP-3/WP-4 additions recorded in the implementation record.</summary>
public static class JournalFindingCodes
{
    // Security (§9.1)
    public const string ChainIntegrityFailure = "CHAIN_INTEGRITY_FAILURE";
    public const string AnchorMismatch = "ANCHOR_MISMATCH";
    public const string FakeReceipt = "FAKE_RECEIPT";
    public const string ArchiveRegression = "ARCHIVE_REGRESSION";
    public const string ArchiveDiscontinuity = "ARCHIVE_DISCONTINUITY";
    public const string ArchiveUnexpectedObject = "ARCHIVE_UNEXPECTED_OBJECT";
    public const string ArchiveDuplicate = "ARCHIVE_DUPLICATE";
    public const string BehindArchive = "BEHIND_ARCHIVE";
    public const string CheckpointStoreRegression = "CHECKPOINT_STORE_REGRESSION";
    public const string CheckpointChainInvalid = "CHECKPOINT_CHAIN_INVALID";

    /// <summary>WP-4A (amendment A-1): a checkpoint names an accepted exception whose record is missing, invalid or for another scope.</summary>
    public const string AcceptedExceptionRecordInvalid = "ACCEPTED_EXCEPTION_RECORD_INVALID";

    /// <summary>WP-4A (amendment A-1): the rows or archive objects of an accepted exception's scope changed since acceptance.</summary>
    public const string AcceptedExceptionEvidenceChanged = "ACCEPTED_EXCEPTION_EVIDENCE_CHANGED";

    /// <summary>D+ (§5.1 DP-6, WP-5a): the archive holds no object at the predecessor key while the sentinel is present.</summary>
    public const string PredecessorMissing = "PREDECESSOR_MISSING";

    /// <summary>D+ (§5.1 DP-6, WP-5a): the entry's previous digest differs from SHA-256 of the archived predecessor (same text as the R-7 subtype).</summary>
    public const string DeliveryLinkMismatch = "LINK_MISMATCH";

    /// <summary>WP-5a.2 (A-3 SM-6): a malformed stop marker, one for another scope, or an unexpected name under the stop-marker prefix.</summary>
    public const string DPlusStopMarkerInvalid = "DPLUS_STOP_MARKER_INVALID";

    /// <summary>WP-5a.2 (A-3 SM-6): a stop marker without its D+ finding, or a D+ finding without its stop marker (one was lost or removed).</summary>
    public const string DPlusStopRecordIncomplete = "DPLUS_STOP_RECORD_INCOMPLETE";

    /// <summary>WP-5a.2 (A-3 SM-3): another marker with different bytes already exists at the key of a new refusal.</summary>
    public const string DPlusStopMarkerConflict = "DPLUS_STOP_MARKER_CONFLICT";

    /// <summary>G14-R2 (A-4 §24, W2-S): the evidence of an undelivered entry differs from its durable pre-commitment (stream-stopping, security).</summary>
    public const string DPlusEvidenceChangedAfterCommitment = "DPLUS_EVIDENCE_CHANGED_AFTER_COMMITMENT";

    /// <summary>G14-R2 (A-4 §24): after an executed recovery, the evidence of the recovered revision differs from the statement's expectation (stream-stopping, security).</summary>
    public const string DPlusRecoveryEvidenceMismatch = "DPLUS_RECOVERY_EVIDENCE_MISMATCH";

    /// <summary>G14-R2 (A-4 §24): a recovery record is malformed, not canonical, for another scope, or carries an invalid signature.</summary>
    public const string RecoveryRecordInvalid = "SR_RECORD_INVALID";

    /// <summary>
    /// SEC-BL-01 WP-5b (A-6 §26.7, EP-5): the verifier found a W2-S evidence commitment that is malformed, contradictory, single-copy, for another
    /// instance or generation, beyond the head, missing for an archived revision, or that names other row or predecessor bytes than the archive
    /// holds (subtypes in <see cref="DPlusCommitmentInspection"/>). Security finding; alert-only (the verifier never stops delivery).
    /// </summary>
    public const string DPlusCommitmentMismatch = "DPLUS_COMMITMENT_MISMATCH";

    /// <summary>G14-R2 (A-4 §24): two copies of one recovery record or evidence commitment differ between the two stores.</summary>
    public const string RecoveryRecordContradictory = "SR_RECORD_CONTRADICTORY";

    /// <summary>G14-R2 (A-4 §24, archive-instance pinning): subtypes of <see cref="ArchiveRegression"/>.</summary>
    public const string InstanceChangedSubtype = "INSTANCE_CHANGED";

    public const string InstanceAnchorContradictorySubtype = "INSTANCE_ANCHOR_CONTRADICTORY";

    public const string InstanceUnpinnedSubtype = "INSTANCE_UNPINNED";

    // Operational (§9.2)
    public const string DeliveryLag = "DELIVERY_LAG";
    public const string ArchiveUnavailable = "ARCHIVE_UNAVAILABLE";
    public const string CheckpointStoreUnavailable = "CHECKPOINT_STORE_UNAVAILABLE";
    public const string VerificationStale = "VERIFICATION_STALE";

    /// <summary>WP-4 addition (VF-8 "reference the existing finding"): a row the delivery path already refused (<c>VerificationFailed</c>).</summary>
    public const string DeliveryRefused = "DELIVERY_REFUSED";

    /// <summary>Subtype for an organization head column that disagrees with the journal rows (WP-3/WP-4 addition under CHAIN_INTEGRITY_FAILURE).</summary>
    public const string OrganizationHeadMismatch = "ORGANIZATION_HEAD_MISMATCH";

    public static JournalFindingSeverity SeverityOf(string code) => code switch
    {
        DeliveryLag or ArchiveUnavailable or CheckpointStoreUnavailable or VerificationStale or DeliveryRefused => JournalFindingSeverity.Operational,
        _ => JournalFindingSeverity.Security,
    };
}

public enum JournalFindingSeverity
{
    Security,
    Operational,
}

/// <summary>Inconclusive reasons: the sweep or baseline decided nothing (never verified, never a checkpoint).</summary>
public static class JournalInconclusiveReasons
{
    public const string NotBaselined = "NOT_BASELINED";
    public const string OrganizationUnknown = "ORGANIZATION_UNKNOWN";
    public const string ArchiveUnavailable = "ARCHIVE_UNAVAILABLE";
    public const string CheckpointStoreUnavailable = "CHECKPOINT_STORE_UNAVAILABLE";
    public const string ListedObjectMissing = "ARCHIVE_LISTED_OBJECT_MISSING";
    public const string EnvelopeUnsupported = "ENVELOPE_UNSUPPORTED";
    public const string EnvelopeMalformed = "ENVELOPE_MALFORMED";
    public const string SentinelAbsent = "ARCHIVE_SENTINEL_ABSENT";
    public const string CheckpointSchemaUnsupported = "CHECKPOINT_SCHEMA_UNSUPPORTED";
    public const string ReadLimitExceeded = "READ_LIMIT_EXCEEDED";
    public const string ArchiveChangedDuringSweep = "ARCHIVE_CHANGED_DURING_SWEEP";
    public const string SweepLockHeld = "SWEEP_LOCK_HELD";
    public const string ArchiveIdentityMismatch = "ARCHIVE_IDENTITY_MISMATCH";

    /// <summary>WP-4A: an owner disposition is malformed (identifier, duplicate, missing reference or time, or an out-of-bounds scope).</summary>
    public const string DispositionInvalid = "DISPOSITION_INVALID";
}

/// <summary>Reference to a checkpoint: sequence, verified revision and SHA-256 of its stored bytes.</summary>
public sealed record JournalCheckpointReference(long CheckpointSequence, long VerifiedRevision, string CheckpointDigestHex);

/// <summary>
/// One structured finding (owner WP-4 §6). <see cref="EvidenceReferences"/> name archive objects (<c>archive:</c> key), journal rows
/// (<c>row:</c> entry id) or checkpoints (<c>checkpoint:</c> sequence); never bodies, digests of bodies are not included either.
/// <see cref="SweepOutcome"/> and <see cref="Checkpoint"/> describe the run that observed it.
/// </summary>
public sealed record JournalVerificationFinding(Guid OrganizationId, string StreamName, string FindingCode, string? Subtype, JournalFindingSeverity Severity,
    long? FirstRevision, long? LastRevision, Guid RunId, DateTimeOffset ObservedAtUtc, IReadOnlyList<string> EvidenceReferences, string Detail,
    string SweepOutcome, JournalCheckpointReference? Checkpoint, int? StopGeneration = null)
{
    /// <summary>
    /// Identity for create-only storage and alert de-duplication: organization, stream, code, subtype and revision range. G14-R2 (A-4 §24,
    /// stop generations): a D+ stop raised after an executed recovery carries its generation <c>g ≥ 1</c> in the identity, so a later stop at
    /// the same revision is a new record and never collides with the resolved one. Generation 0 (and null) keeps the A-3 identity unchanged.
    /// </summary>
    public string Identity
    {
        get
        {
            var text = string.Join('|', OrganizationId.ToString("D"), StreamName, FindingCode, Subtype ?? "-", FirstRevision?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "-",
                LastRevision?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "-");
            if (StopGeneration is >= 1)
            {
                text += "|g" + StopGeneration.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
            }

            return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..32];
        }
    }

    /// <summary>
    /// Canonical record bytes (schema <c>DynamicForms/JournalVerificationFinding/v1</c>). A generation ≥ 1 adds the <c>stop_generation</c>
    /// member; records without it are byte-identical to the A-3 form.
    /// </summary>
    public byte[] ToCanonicalBytes() => StopGeneration is >= 1 ? CanonicalJson.SerializeBytes(new
    {
        FindingSchema = "DynamicForms/JournalVerificationFinding/v1",
        OrganizationId,
        StreamName,
        FindingCode,
        Subtype,
        Severity = Severity.ToString(),
        FirstRevision,
        LastRevision,
        RunId,
        ObservedAtUtc,
        EvidenceReferences,
        Detail,
        SweepOutcome,
        Checkpoint,
        StopGeneration,
    }) : CanonicalJson.SerializeBytes(new
    {
        FindingSchema = "DynamicForms/JournalVerificationFinding/v1",
        OrganizationId,
        StreamName,
        FindingCode,
        Subtype,
        Severity = Severity.ToString(),
        FirstRevision,
        LastRevision,
        RunId,
        ObservedAtUtc,
        EvidenceReferences,
        Detail,
        SweepOutcome,
        Checkpoint,
    });
}

/// <summary>Counters for one run (owner WP-4 §10).</summary>
public sealed record JournalSweepMetrics(long DatabaseRowsInspected, long ArchiveObjectsListed, long ArchiveObjectsRead, int RowBatches, long ElapsedMilliseconds);
