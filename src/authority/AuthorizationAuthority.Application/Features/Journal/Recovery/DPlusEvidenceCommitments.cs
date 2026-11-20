using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using AuthorizationAuthority.Application.Common.Ports;
using AuthorizationAuthority.Application.Features.Journal.Verification;

namespace AuthorizationAuthority.Application.Features.Journal.Recovery;

/// <summary>
/// SEC-BL-01 G14-R2 (A-4 §24): what D+ needs beyond the archive and the finding store, composed in Development/Testing with the
/// journal-verification store. Without it (production until WP-8) the audit stream is never evaluated: W2-S commitments and the archive
/// instance pin cannot be established, so nothing is delivered (fail closed).
/// </summary>
public sealed class DPlusDurability(IJournalVerificationRecordStore records, IArchiveProvisioningStore provisioning, IVerificationCheckpointStore checkpoints, DPlusRecoveryApprovalVerifier approvals)
{
    public IJournalVerificationRecordStore Records { get; } = records ?? throw new ArgumentNullException(nameof(records));

    public IArchiveProvisioningStore Provisioning { get; } = provisioning ?? throw new ArgumentNullException(nameof(provisioning));

    public IVerificationCheckpointStore Checkpoints { get; } = checkpoints ?? throw new ArgumentNullException(nameof(checkpoints));

    public DPlusRecoveryApprovalVerifier Approvals { get; } = approvals ?? throw new ArgumentNullException(nameof(approvals));
}

public enum DPlusCommitmentOutcome
{
    /// <summary>Both copies hold exactly the freshly gathered evidence (created now or earlier).</summary>
    Committed,

    /// <summary>A durable commitment holds different evidence: the evidence changed after it was committed (stream-stopping).</summary>
    Changed,

    /// <summary>The two copies of the commitment differ from each other (stream-stopping).</summary>
    Contradictory,

    /// <summary>A store could not be read or written: no evaluation, the audit stream waits.</summary>
    Unavailable,
}

/// <summary>The commitment result; <see cref="Existing"/> is the durable commitment found (parsed) when it differs from the candidate.</summary>
public sealed record DPlusCommitmentResult(DPlusCommitmentOutcome Outcome, string Detail, DPlusEvidenceCommitment Candidate, string ObjectName, DPlusEvidenceCommitment? Existing = null);

/// <summary>
/// G14-R2 (A-4 §24.7, W2-S): the durable evidence pre-commitment. Before D+ evaluates an audit revision, the gathered evidence is committed
/// create-only to <b>both</b> the archive (<c>meta/dplus-intents/</c>) and the journal-verification store (<c>intents/</c>) at the key of
/// (revision, stop generation). Evaluation proceeds only when both copies hold exactly that evidence. An existing commitment that differs from
/// the freshly gathered evidence (after a restart, a concurrent writer, or a replacement) is a change that D+ never resolves by itself.
/// </summary>
public static class DPlusEvidenceCommitments
{
    public static DPlusEvidenceCommitment Build(AuditDeliveryEvidence evidence, int stopGeneration)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        var entry = evidence.Entry;
        var revision = entry.JournalStreamRevision;
        return new DPlusEvidenceCommitment(DPlusEvidenceCommitment.SchemaV1, entry.OrganizationId, entry.JournalStreamName, revision, stopGeneration, evidence.ArchiveInstanceId,
            entry.SecurityJournalEntryId, Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(entry.JournalEventBodyJson))), DPlusRecoveryCodec.Hex(entry.JournalPreviousDigest),
            revision == 1 ? null : PredecessorObjectName(entry.OrganizationId, entry.JournalStreamName, revision - 1), evidence.PredecessorState.ToString(),
            evidence.PredecessorBytes is { } bytes ? Convert.ToHexStringLower(SHA256.HashData(bytes)) : null, DPlusEvidenceCommitment.CurrentRuleSetVersion);
    }

    /// <summary>The logical identity of an archived journal entry (adapter-independent).</summary>
    public static string PredecessorObjectName(Guid organizationId, string streamName, long revision) =>
        "archive-entry:" + organizationId.ToString("D") + "/" + streamName + "/" + revision.ToString("D12", CultureInfo.InvariantCulture);

    public static async Task<DPlusCommitmentResult> CommitAsync(AuditDeliveryEvidence evidence, int stopGeneration, IArchiveMetaRecords archive, IJournalVerificationRecordStore store,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(archive);
        ArgumentNullException.ThrowIfNull(store);
        var candidate = Build(evidence, stopGeneration);
        var bytes = DPlusRecoveryCodec.Serialize(candidate);
        var name = DPlusRecoveryNames.Commitment(candidate.OrganizationId, candidate.StreamName, candidate.StreamRevision, stopGeneration);
        DPlusCommitmentResult Result(DPlusCommitmentOutcome outcome, string detail) => new(outcome, detail, candidate, name);

        StoredObjectRead archived;
        StoredObjectRead stored;
        try
        {
            archived = await archive.ReadMetaAsync(DPlusRecoveryNames.ArchiveCommitmentPrefix + name, cancellationToken).ConfigureAwait(false);
        }
        catch (ArchiveUnavailableException ex)
        {
            return Result(DPlusCommitmentOutcome.Unavailable, "archive: " + ex.Message);
        }

        try
        {
            stored = await store.ReadAsync(DPlusRecoveryNames.StoreCommitmentPrefix + name, cancellationToken).ConfigureAwait(false);
        }
        catch (CheckpointStoreUnavailableException ex)
        {
            return Result(DPlusCommitmentOutcome.Unavailable, "journal-verification store: " + ex.Message);
        }

        var a = archived.Status == StoredObjectStatus.Present ? archived.Bytes : null;
        var s = stored.Status == StoredObjectStatus.Present ? stored.Bytes : null;
        if (a is not null && s is not null && !a.AsSpan().SequenceEqual(s))
        {
            return Result(DPlusCommitmentOutcome.Contradictory, "the two copies of the evidence commitment differ");
        }

        if ((a ?? s) is { } existing && !existing.AsSpan().SequenceEqual(bytes))
        {
            return Result(DPlusCommitmentOutcome.Changed, "the evidence differs from its durable commitment: " + Difference(existing, candidate)) with
            {
                Existing = DPlusRecoveryCodec.TryParse<DPlusEvidenceCommitment>(existing),
            };
        }

        // Create whatever copy is missing (create-only); a conflict means a concurrent writer committed other evidence.
        try
        {
            if (a is null && await archive.CreateMetaAsync(DPlusRecoveryNames.ArchiveCommitmentPrefix + name, bytes, cancellationToken).ConfigureAwait(false) is { Status: CheckpointCreateStatus.Conflict } ac)
            {
                return Result(DPlusCommitmentOutcome.Changed, "a concurrently written commitment holds other evidence: " + Difference(ac.ExistingBytes!, candidate));
            }
        }
        catch (ArchiveUnavailableException ex)
        {
            return Result(DPlusCommitmentOutcome.Unavailable, "archive: " + ex.Message);
        }

        try
        {
            if (s is null && await store.CreateAsync(DPlusRecoveryNames.StoreCommitmentPrefix + name, bytes, cancellationToken).ConfigureAwait(false) is { Status: CheckpointCreateStatus.Conflict } sc)
            {
                return Result(DPlusCommitmentOutcome.Changed, "a concurrently written commitment holds other evidence: " + Difference(sc.ExistingBytes!, candidate));
            }
        }
        catch (CheckpointStoreUnavailableException ex)
        {
            return Result(DPlusCommitmentOutcome.Unavailable, "journal-verification store: " + ex.Message);
        }

        return Result(DPlusCommitmentOutcome.Committed, "committed");
    }

    /// <summary>Which committed item differs (names of members only; never digests or bodies).</summary>
    private static string Difference(byte[] existingBytes, DPlusEvidenceCommitment candidate)
    {
        if (DPlusRecoveryCodec.TryParse<DPlusEvidenceCommitment>(existingBytes) is not { } existing)
        {
            return "the stored commitment is not canonical";
        }

        var members = new List<string>();
        if (!string.Equals(existing.ArchiveInstanceId, candidate.ArchiveInstanceId, StringComparison.Ordinal))
        {
            members.Add("archive_instance_id");
        }

        if (existing.RowEntryId != candidate.RowEntryId || !string.Equals(existing.RowBodySha256Hex, candidate.RowBodySha256Hex, StringComparison.Ordinal)
            || !string.Equals(existing.RowPreviousDigestHex, candidate.RowPreviousDigestHex, StringComparison.Ordinal))
        {
            members.Add("row");
        }

        if (!string.Equals(existing.PredecessorState, candidate.PredecessorState, StringComparison.Ordinal) || !string.Equals(existing.PredecessorSha256Hex, candidate.PredecessorSha256Hex, StringComparison.Ordinal))
        {
            members.Add("predecessor");
        }

        if (!string.Equals(existing.RuleSetVersion, candidate.RuleSetVersion, StringComparison.Ordinal))
        {
            members.Add("rule_set_version");
        }

        return members.Count == 0 ? "scope or schema" : string.Join(",", members);
    }
}

public enum DPlusPinStatus
{
    Pinned,
    Unpinned,
    Contradictory,
    Unavailable,
}

public sealed record DPlusArchivePin(DPlusPinStatus Status, string? ExpectedArchiveInstanceId, string? ProvisioningRecordSha256Hex, string Detail);

/// <summary>
/// G14-R2 (A-4 §24, archive-instance pinning): the trusted expected archive instance of (organization, stream), from the journal-verification
/// store only: the provisioning record (disposable stacks) and the first checkpoint of the stream (baselined archives; CheckSuccessor keeps
/// the instance fixed along a valid chain). Neither present ⇒ unpinned; an invalid anchor or two different instances ⇒ contradictory. The
/// archive itself is never its own anchor.
/// </summary>
public static class DPlusArchivePins
{
    public static async Task<DPlusArchivePin> ResolveAsync(DPlusDurability durability, Guid organizationId, string streamName, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(durability);
        StoredObjectRead record;
        StoredObjectRead first;
        try
        {
            record = await durability.Provisioning.ReadRecordAsync(cancellationToken).ConfigureAwait(false);
            first = await durability.Checkpoints.ReadAsync(organizationId, streamName, 1, cancellationToken).ConfigureAwait(false);
        }
        catch (CheckpointStoreUnavailableException ex)
        {
            return new DPlusArchivePin(DPlusPinStatus.Unavailable, null, null, ex.Message);
        }

        string? fromRecord = null;
        string? recordSha = null;
        if (record.Status == StoredObjectStatus.Present)
        {
            recordSha = DPlusRecoveryCodec.Sha256Hex(record.Bytes!);
            fromRecord = Reconciliation.ArchiveProvisioningRecords.TryParse(record.Bytes)?.ArchiveInstanceId;
            if (fromRecord is null)
            {
                return new DPlusArchivePin(DPlusPinStatus.Contradictory, null, recordSha, "the provisioning record is not canonical");
            }
        }

        string? fromCheckpoint = null;
        if (first.Status == StoredObjectStatus.Present)
        {
            var parsed = JournalVerificationCheckpoints.Parse(first.Bytes);
            if (parsed.Checkpoint is not { } checkpoint || checkpoint.OrganizationId != organizationId || !string.Equals(checkpoint.StreamName, streamName, StringComparison.Ordinal)
                || checkpoint.CheckpointSequence != 1)
            {
                return new DPlusArchivePin(DPlusPinStatus.Contradictory, fromRecord, recordSha, "the first checkpoint is not a valid checkpoint of this stream");
            }

            fromCheckpoint = checkpoint.ArchiveInstanceId;
        }

        if (fromRecord is not null && fromCheckpoint is not null && !string.Equals(fromRecord, fromCheckpoint, StringComparison.Ordinal))
        {
            return new DPlusArchivePin(DPlusPinStatus.Contradictory, null, recordSha, "the provisioning record and the first checkpoint name different instances");
        }

        return (fromRecord ?? fromCheckpoint) is { } expected
            ? new DPlusArchivePin(DPlusPinStatus.Pinned, expected, recordSha, fromRecord is not null ? "provisioning record" : "first checkpoint")
            : new DPlusArchivePin(DPlusPinStatus.Unpinned, null, null, "no provisioning record and no first checkpoint");
    }
}
