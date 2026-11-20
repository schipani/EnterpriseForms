using System.Text.Json;
using System.Text.Json.Serialization;
using AuthorizationAuthority.Application.Common;
using AuthorizationAuthority.Application.Common.Ports;
using AuthorizationAuthority.Application.Features.Journal.Verification;

namespace AuthorizationAuthority.Application.Features.Journal.Reconciliation;

/// <summary>
/// SEC-BL-01 WP-5a.1 (amendment A-2 correction, §22.8): the explicit, create-only provisioning step of a <b>disposable</b>
/// Development/Testing security-event archive, run before any Authority journal activity. It replaces the WP-5a automatic bootstrap.
/// <para>
/// New-instance evidence (all required; none is sufficient alone): no provisioning record in the separately configured
/// journal-verification store; no archive sentinel; the archive container has never been created (complete listing); AuthorityDb holds
/// no security-journal or platform-journal row; the verification store holds no object at all. The record is written first
/// (create-only) and the sentinel then carries the record's instance identifier, so deleting archive objects cannot make an established
/// archive look new: the record outlives them and the next run reports <c>ARCHIVE_LOST_AFTER_PROVISIONING</c>. A concurrent loser converges
/// on the winner's record and succeeds only once the winner's sentinel exists. Every other state is refused and needs an explicit operator
/// baseline (§10). Unavailability is never treated as absence.
/// </para>
/// </summary>
public sealed class ArchiveProvisioningService(IJournalVerificationReader reader, ISecurityEventArchiveInventory archive, IArchiveProvisioningStore store, TimeProvider clock)
{
    public async Task<ArchiveProvisioningResult> ProvisionAsync(string toolVersion, CancellationToken cancellationToken)
    {
        try
        {
            long journalRows;
            await using (var snapshot = await reader.OpenSnapshotAsync(cancellationToken).ConfigureAwait(false))
            {
                journalRows = await snapshot.CountJournalRowsAsync(cancellationToken).ConfigureAwait(false);
            }

            var recordRead = await store.ReadRecordAsync(cancellationToken).ConfigureAwait(false);
            var sentinel = await archive.ReadIdentityAsync(cancellationToken).ConfigureAwait(false);
            var content = await archive.SummarizeAsync(cancellationToken).ConfigureAwait(false);

            if (recordRead.Status == StoredObjectStatus.Present)
            {
                return ArchiveProvisioningRecords.TryParse(recordRead.Bytes) is { } existing
                    ? await CompleteAsync(existing, journalRows, false, cancellationToken).ConfigureAwait(false)
                    : ArchiveProvisioningResult.Refused(ArchiveProvisioningReasons.RecordInvalid, "the provisioning record is not a canonical v1 record");
            }

            if (sentinel.Status == StoredObjectStatus.Present)
            {
                return ArchiveProvisioningResult.Refused(ArchiveProvisioningReasons.SentinelWithoutRecord,
                    "the archive has a sentinel but no provisioning record exists (record lost, or the archive was established by a baseline)");
            }

            if (!content.ContainerAbsent)
            {
                return ArchiveProvisioningResult.Refused(ArchiveProvisioningReasons.ArchiveContainerExists,
                    $"the archive container already exists ({content.ObjectCount} objects besides any sentinel): an established, partially or fully wiped archive is never stamped");
            }

            if (journalRows > 0)
            {
                return ArchiveProvisioningResult.Refused(ArchiveProvisioningReasons.JournalActivityPresent, $"AuthorityDb already holds {journalRows} journal rows (security or platform journal)");
            }

            if (await store.HoldsAnyObjectAsync(cancellationToken).ConfigureAwait(false))
            {
                return ArchiveProvisioningResult.Refused(ArchiveProvisioningReasons.VerificationStoreNotFresh,
                    "the journal-verification store already holds objects (checkpoints, findings or a lost provisioning record)");
            }

            var now = clock.GetUtcNow().ToUniversalTime();
            var candidate = new ArchiveProvisioningRecord(ArchiveProvisioningRecord.SchemaV1, ArchiveIdentityCodec.NewIdentity(now).ArchiveInstanceId, now, toolVersion);
            var created = await store.CreateRecordAsync(ArchiveProvisioningRecords.Serialize(candidate), cancellationToken).ConfigureAwait(false);
            if (created.Status == CheckpointCreateStatus.Conflict)
            {
                // A concurrent provisioner won: converge on its record (and its instance identifier).
                return ArchiveProvisioningRecords.TryParse(created.ExistingBytes) is { } winner
                    ? await CompleteAsync(winner, journalRows, false, cancellationToken).ConfigureAwait(false)
                    : ArchiveProvisioningResult.Refused(ArchiveProvisioningReasons.RecordInvalid, "a concurrently written provisioning record is not canonical");
            }

            return await CompleteAsync(candidate, journalRows, true, cancellationToken).ConfigureAwait(false);
        }
        catch (ArchiveUnavailableException ex)
        {
            return ArchiveProvisioningResult.Unavailable("archive: " + ex.Message);
        }
        catch (CheckpointStoreUnavailableException ex)
        {
            return ArchiveProvisioningResult.Unavailable("journal-verification store: " + ex.Message);
        }
    }

    /// <summary>
    /// With a record in hand: the sentinel must carry the record's instance identifier. Only the run that has just written the record
    /// creates the sentinel. Any other run that finds a record without a sentinel cannot tell a crash between the two writes, a concurrent
    /// provisioner still working, or the loss of an archive that held only its sentinel apart: it fails closed
    /// (<c>PROVISIONING_INCOMPLETE</c>, operator decision); with journal rows or an existing container it is archive loss.
    /// </summary>
    private async Task<ArchiveProvisioningResult> CompleteAsync(ArchiveProvisioningRecord record, long journalRows, bool newlyRecorded, CancellationToken cancellationToken)
    {
        var sentinel = await archive.ReadIdentityAsync(cancellationToken).ConfigureAwait(false);
        if (sentinel.Status == StoredObjectStatus.Present)
        {
            return SentinelMatches(sentinel.Identity, record);
        }

        var content = await archive.SummarizeAsync(cancellationToken).ConfigureAwait(false);
        if (!content.ContainerAbsent || journalRows > 0)
        {
            return ArchiveProvisioningResult.Refused(ArchiveProvisioningReasons.ArchiveLostAfterProvisioning,
                $"the provisioning record exists but the archive sentinel is absent (container {(content.ContainerAbsent ? "absent" : "present")}, {journalRows} journal rows): partial or full archive loss");
        }

        if (!newlyRecorded)
        {
            return ArchiveProvisioningResult.Refused(ArchiveProvisioningReasons.ProvisioningIncomplete,
                "a provisioning record exists without a sentinel and the archive was never created: an interrupted or concurrent provisioning, or a lost sentinel-only archive; operator decision required");
        }

        var created = await archive.CreateIdentityAsync(new ArchiveIdentity(ArchiveIdentity.SchemaV1, record.ArchiveInstanceId, record.ProvisionedAtUtc), cancellationToken).ConfigureAwait(false);
        return created.Status == ArchiveIdentityCreateStatus.AlreadyExists
            ? SentinelMatches(created.Existing, record)
            : new ArchiveProvisioningResult(ArchiveProvisioningOutcome.Provisioned, null, record.ArchiveInstanceId, "archive provisioned");
    }
    private static ArchiveProvisioningResult SentinelMatches(ArchiveIdentity? identity, ArchiveProvisioningRecord record) =>
        identity is null ? ArchiveProvisioningResult.Refused(ArchiveProvisioningReasons.SentinelInvalid, "the archive sentinel is not a canonical v1 sentinel")
        : string.Equals(identity.ArchiveInstanceId, record.ArchiveInstanceId, StringComparison.Ordinal)
            ? new ArchiveProvisioningResult(ArchiveProvisioningOutcome.AlreadyProvisioned, null, record.ArchiveInstanceId, "archive already provisioned (record and sentinel agree)")
            : ArchiveProvisioningResult.Refused(ArchiveProvisioningReasons.ArchiveIdentityMismatch, "the archive sentinel names another instance than the provisioning record");
}

public sealed record ArchiveProvisioningRecord(string RecordSchema, string ArchiveInstanceId, DateTimeOffset ProvisionedAtUtc, string ToolVersion)
{
    public const string SchemaV1 = "DynamicForms/ArchiveProvisioning/v1";
}

public static class ArchiveProvisioningRecords
{
    private static readonly JsonSerializerOptions Strict = new(CanonicalJson.Options) { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };

    public static byte[] Serialize(ArchiveProvisioningRecord record) => CanonicalJson.SerializeBytes(record);

    public static ArchiveProvisioningRecord? TryParse(ReadOnlySpan<byte> bytes)
    {
        try
        {
            var record = JsonSerializer.Deserialize<ArchiveProvisioningRecord>(bytes, Strict);
            return record is not null && string.Equals(record.RecordSchema, ArchiveProvisioningRecord.SchemaV1, StringComparison.Ordinal) && !string.IsNullOrEmpty(record.ArchiveInstanceId)
                && record.ProvisionedAtUtc.Offset == TimeSpan.Zero && CanonicalJson.SerializeBytes(record).AsSpan().SequenceEqual(bytes) ? record : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

public enum ArchiveProvisioningOutcome
{
    Provisioned,
    AlreadyProvisioned,
    Refused,
    Unavailable,
}

public static class ArchiveProvisioningReasons
{
    public const string SentinelWithoutRecord = "ARCHIVE_SENTINEL_WITHOUT_RECORD";
    public const string ArchiveContainerExists = "ARCHIVE_CONTAINER_EXISTS";
    public const string JournalActivityPresent = "JOURNAL_ACTIVITY_PRESENT";
    public const string VerificationStoreNotFresh = "VERIFICATION_STORE_NOT_FRESH";
    public const string ArchiveLostAfterProvisioning = "ARCHIVE_LOST_AFTER_PROVISIONING";
    public const string ArchiveIdentityMismatch = "ARCHIVE_IDENTITY_MISMATCH";
    public const string SentinelInvalid = "SENTINEL_INVALID";
    public const string RecordInvalid = "PROVISIONING_RECORD_INVALID";

    /// <summary>A record without a sentinel while the archive was never created: ambiguous, so never completed automatically.</summary>
    public const string ProvisioningIncomplete = "PROVISIONING_INCOMPLETE";
}

public sealed record ArchiveProvisioningResult(ArchiveProvisioningOutcome Outcome, string? ReasonCode, string? ArchiveInstanceId, string Detail)
{
    public static ArchiveProvisioningResult Refused(string reason, string detail) => new(ArchiveProvisioningOutcome.Refused, reason, null, detail);

    public static ArchiveProvisioningResult Unavailable(string detail) => new(ArchiveProvisioningOutcome.Unavailable, null, null, detail);
}
