using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AuthorizationAuthority.Application.Common;
using AuthorizationAuthority.Application.Common.Ports;

namespace AuthorizationAuthority.Application.Features.Journal.Reconciliation;

/// <summary>
/// SEC-BL-01 WP-4A (contract amendment A-1, §7.5): one accepted baseline exception, stored create-only beside the checkpoints. It fixes the
/// exact accepted scope (organization, stream, revision range, finding code and subtype), an evidence digest over the AuthorityDb rows and
/// archive objects of that range at acceptance, the provenance and the explicit owner disposition. Assurance is always Reduced.
/// </summary>
public sealed record JournalAcceptedExceptionRecord(string RecordSchema, string ExceptionId, string ExceptionClass, Guid OrganizationId, string StreamName, long FirstRevision,
    long LastRevision, string FindingCode, string? Subtype, string EvidenceDigestHex, string OwnerDispositionReference, DateTimeOffset DispositionRecordedAtUtc, Guid BaselineRunId,
    long BaselineCheckpointSequence, string? ArchiveInstanceId, string ToolVersion, string? SourceCommit, string OperatorLabel, DateTimeOffset AcceptedAtUtc, string AssuranceClassification)
{
    public const string SchemaV1 = "DynamicForms/JournalAcceptedException/v1";

    public const string ReducedAssurance = "Reduced";
}

public static class JournalAcceptedExceptions
{
    /// <summary>Most revisions one accepted scope may cover (evidence is recomputed on every sweep).</summary>
    public const long MaximumScopeRevisions = 10_000;

    private static readonly JsonSerializerOptions Strict = new(CanonicalJson.Options) { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };

    /// <summary>Exception identifiers are store keys: 1..64 of <c>A-Z a-z 0-9 . _ -</c>.</summary>
    public static bool IsValidIdentifier(string? id) => !string.IsNullOrEmpty(id) && id.Length <= 64 && id.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-');

    public static byte[] Serialize(JournalAcceptedExceptionRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (Validate(record) is { } problem)
        {
            throw new ArgumentException("invalid accepted-exception record: " + problem, nameof(record));
        }

        return CanonicalJson.SerializeBytes(record);
    }

    /// <summary>The record, or null when the bytes are not a canonical, valid v1 record (callers treat that as a security finding).</summary>
    public static JournalAcceptedExceptionRecord? TryParse(ReadOnlySpan<byte> bytes)
    {
        try
        {
            var record = JsonSerializer.Deserialize<JournalAcceptedExceptionRecord>(bytes, Strict);
            return record is not null && Validate(record) is null && CanonicalJson.SerializeBytes(record).AsSpan().SequenceEqual(bytes) ? record : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static string? Validate(JournalAcceptedExceptionRecord r)
    {
        ArgumentNullException.ThrowIfNull(r);
        if (!string.Equals(r.RecordSchema, JournalAcceptedExceptionRecord.SchemaV1, StringComparison.Ordinal))
        {
            return "unsupported schema";
        }

        if (!IsValidIdentifier(r.ExceptionId) || string.IsNullOrEmpty(r.ExceptionClass) || string.IsNullOrEmpty(r.FindingCode) || string.IsNullOrEmpty(r.OwnerDispositionReference))
        {
            return "identifier, class, finding code and owner disposition are required";
        }

        if (r.FirstRevision < 1 || r.LastRevision < r.FirstRevision || r.LastRevision - r.FirstRevision + 1 > MaximumScopeRevisions)
        {
            return "invalid revision scope";
        }

        if (r.EvidenceDigestHex is not { Length: 64 } || !r.EvidenceDigestHex.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f'))
        {
            return "evidence digest must be 64 lower-case hexadecimal characters";
        }

        if (r.BaselineCheckpointSequence < 1 || r.DispositionRecordedAtUtc.Offset != TimeSpan.Zero || r.AcceptedAtUtc.Offset != TimeSpan.Zero)
        {
            return "baseline checkpoint and UTC times are required";
        }

        return string.Equals(r.AssuranceClassification, JournalAcceptedExceptionRecord.ReducedAssurance, StringComparison.Ordinal) ? null : "accepted exceptions are always Reduced assurance";
    }

    /// <summary>
    /// Evidence digest of a revision range: for every revision, whether the AuthorityDb row exists, its stored digest and the SHA-256 of its
    /// body, and whether the archive object exists and its SHA-256. Any change to a row or object of the range, an added or removed row or
    /// object, changes the digest. Archive unavailability throws <see cref="ArchiveUnavailableException"/> (the caller is Inconclusive).
    /// </summary>
    public static async Task<string> ComputeEvidenceDigestAsync(IJournalVerificationSnapshot database, ISecurityEventArchiveInventory archive, Guid organizationId, string streamName,
        long firstRevision, long lastRevision, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(archive);
        if (firstRevision < 1 || lastRevision < firstRevision || lastRevision - firstRevision + 1 > MaximumScopeRevisions)
        {
            throw new ArgumentOutOfRangeException(nameof(lastRevision), "invalid evidence scope");
        }

        var rows = (await database.ReadRowsAsync(organizationId, streamName, firstRevision, lastRevision, (int)(lastRevision - firstRevision + 1), cancellationToken).ConfigureAwait(false))
            .ToDictionary(r => r.StreamRevision);
        var items = new List<EvidenceItem>();
        for (var revision = firstRevision; revision <= lastRevision; revision++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = await archive.ReadEntryAsync(organizationId, streamName, revision, cancellationToken).ConfigureAwait(false);
            rows.TryGetValue(revision, out var row);
            items.Add(new EvidenceItem(revision, row is not null, row is null ? null : Convert.ToHexStringLower(row.EventDigest),
                row is null ? null : Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(row.BodyJson))), read.Status.ToString(),
                read.Status == StoredObjectStatus.Present ? Convert.ToHexStringLower(SHA256.HashData(read.Bytes!)) : null));
        }

        var canonical = CanonicalJson.SerializeBytes(new EvidenceDocument("DynamicForms/JournalExceptionEvidence/v1", organizationId, streamName, firstRevision, lastRevision, items));
        return Convert.ToHexStringLower(SHA256.HashData(canonical));
    }

    private sealed record EvidenceDocument(string EvidenceSchema, Guid OrganizationId, string StreamName, long FirstRevision, long LastRevision, IReadOnlyList<EvidenceItem> Items);

    private sealed record EvidenceItem(long Revision, bool RowPresent, string? RowStoredDigestHex, string? RowBodySha256Hex, string ArchiveStatus, string? ArchiveSha256Hex);

    /// <summary>
    /// Loads and re-verifies the accepted exceptions a checkpoint names (scope, provenance and evidence digest must all match). Returns the verified records and the findings for missing, invalid,
    /// mismatched or changed ones; throws <see cref="CheckpointStoreUnavailableException"/> or <see cref="ArchiveUnavailableException"/> when a
    /// store cannot be read (the caller is Inconclusive).
    /// </summary>
    public static async Task<(IReadOnlyList<JournalAcceptedExceptionRecord> Verified, IReadOnlyList<JournalPendingFinding> Findings)> LoadAndVerifyAsync(IJournalAcceptedExceptionStore store,
        IJournalVerificationSnapshot database, ISecurityEventArchiveInventory archive, Guid organizationId, string streamName, IReadOnlyList<string> exceptionIds, string chainArchiveInstanceId,
        long chainHeadSequence, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(exceptionIds);
        var verified = new List<JournalAcceptedExceptionRecord>();
        var findings = new List<JournalPendingFinding>();
        foreach (var id in exceptionIds)
        {
            var read = IsValidIdentifier(id) ? await store.ReadAsync(organizationId, streamName, id, cancellationToken).ConfigureAwait(false) : StoredObjectRead.Absent;
            var record = read.Status == StoredObjectStatus.Present ? TryParse(read.Bytes) : null;
            // Provenance: the record must name this scope, the archive instance of the chain and a baseline at or before the head that names it.
            if (record is null || record.ExceptionId != id || record.OrganizationId != organizationId || !string.Equals(record.StreamName, streamName, StringComparison.Ordinal)
                || !string.Equals(record.ArchiveInstanceId, chainArchiveInstanceId, StringComparison.Ordinal) || record.BaselineCheckpointSequence > chainHeadSequence)
            {
                findings.Add(new JournalPendingFinding(JournalFindingCodes.AcceptedExceptionRecordInvalid, null, null, null, ["exception:" + id],
                    "a checkpoint names an accepted exception whose record is missing, invalid or for another scope"));
                continue;
            }

            var digest = await ComputeEvidenceDigestAsync(database, archive, organizationId, streamName, record.FirstRevision, record.LastRevision, cancellationToken).ConfigureAwait(false);
            if (!string.Equals(digest, record.EvidenceDigestHex, StringComparison.Ordinal))
            {
                findings.Add(new JournalPendingFinding(JournalFindingCodes.AcceptedExceptionEvidenceChanged, record.FindingCode, record.FirstRevision, record.LastRevision, ["exception:" + id],
                    "the evidence of an accepted exception changed since it was accepted"));
                continue;
            }

            verified.Add(record);
        }

        return (verified, findings);
    }

    /// <summary>The verified record that covers a finding exactly (same code and subtype, range inside the accepted range), or null.</summary>
    public static JournalAcceptedExceptionRecord? Covering(IReadOnlyList<JournalAcceptedExceptionRecord> verified, JournalPendingFinding finding)
    {
        ArgumentNullException.ThrowIfNull(verified);
        ArgumentNullException.ThrowIfNull(finding);
        return finding.FirstRevision is not { } first || finding.LastRevision is not { } last ? null
            : verified.FirstOrDefault(r => string.Equals(r.FindingCode, finding.FindingCode, StringComparison.Ordinal) && string.Equals(r.Subtype, finding.Subtype, StringComparison.Ordinal)
                && r.FirstRevision <= first && last <= r.LastRevision);
    }
}

/// <summary>A finding classified as an unchanged, previously accepted exception (reported, never a finding, never Clean on its own).</summary>
public sealed record JournalAcceptedObservation(string ExceptionId, string FindingCode, string? Subtype, long? FirstRevision, long? LastRevision);
