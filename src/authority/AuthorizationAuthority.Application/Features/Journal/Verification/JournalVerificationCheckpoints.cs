using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using AuthorizationAuthority.Application.Common;
using AuthorizationAuthority.Application.Common.Ports;

namespace AuthorizationAuthority.Application.Features.Journal.Verification;

/// <summary>
/// SEC-BL-01 WP-2 (C-155, contract §7): the journal-verification checkpoint record, its deterministic canonical serialization, its chain
/// rules (CK-2, CK-3, AS-3), discovery of the latest checkpoint (CK-4, CK-5, CK-7..CK-9) and create-next (CK-1, CK-2).
/// <para>
/// <b>Rollback limits (contract §7.3).</b> The checkpoint chain detects <b>corruption</b> (CKR-1). It does not prove that its newest
/// checkpoint still exists: deleting the newest checkpoints leaves a valid shorter chain. Such a rollback is detectable only against an
/// independently trusted monotonic head (CKR-2, production: Confidential Ledger, not implemented) or, within one process lifetime, a
/// caller-held minimum sequence; across restarts in Development/Testing it is <b>undetectable</b> (CKR-3).
/// </para>
/// </summary>
public static class JournalVerificationCheckpoints
{
    public const string SchemaV1 = "DynamicForms/JournalVerification/v1";

    /// <summary>Upper bound on listing pages read by discovery; reaching it is unavailability.</summary>
    public const int MaximumListingPages = 100_000;

    private const int MaximumTextLength = 128;

    private static readonly JsonSerializerOptions StrictWire = new(CanonicalJson.Options) { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };

    /// <summary>Canonical bytes of a valid checkpoint (validated first; invalid input throws <see cref="ArgumentException"/>).</summary>
    public static byte[] Serialize(JournalVerificationCheckpoint checkpoint)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);
        if (Validate(checkpoint) is { } problem)
        {
            throw new ArgumentException("invalid checkpoint: " + problem, nameof(checkpoint));
        }

        return CanonicalJson.SerializeBytes(ToWire(checkpoint));
    }

    public static byte[] Digest(ReadOnlySpan<byte> canonicalBytes) => SHA256.HashData(canonicalBytes);

    /// <summary>Strict parse: unknown schema ⇒ Unsupported; unknown or missing members, invalid values or non-canonical bytes ⇒ Malformed.</summary>
    public static CheckpointParse Parse(ReadOnlySpan<byte> bytes)
    {
        string? schema;
        try
        {
            using var document = JsonDocument.Parse(bytes.ToArray());
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return CheckpointParse.Malformed("checkpoint is not a JSON object");
            }

            schema = document.RootElement.TryGetProperty("checkpoint_schema", out var element) && element.ValueKind == JsonValueKind.String ? element.GetString() : null;
        }
        catch (JsonException)
        {
            return CheckpointParse.Malformed("checkpoint is not JSON");
        }

        if (schema is null)
        {
            return CheckpointParse.Malformed("checkpoint_schema missing");
        }

        if (!string.Equals(schema, SchemaV1, StringComparison.Ordinal))
        {
            return new CheckpointParse(CheckpointParseOutcome.Unsupported, "checkpoint schema '" + (schema.Length <= 80 ? schema : schema[..80]) + "' is not supported", null);
        }

        CheckpointWire? wire;
        try
        {
            wire = JsonSerializer.Deserialize<CheckpointWire>(bytes, StrictWire);
        }
        catch (JsonException ex)
        {
            return CheckpointParse.Malformed("checkpoint members invalid: " + ex.GetType().Name);
        }

        if (wire is null || FromWire(wire) is not { } checkpoint)
        {
            return CheckpointParse.Malformed("checkpoint values invalid");
        }

        if (Validate(checkpoint) is { } problem)
        {
            return CheckpointParse.Malformed(problem);
        }

        // Canonical form: exactly the bytes this serializer writes (also rejects duplicate members and reordered or reformatted content).
        if (!CanonicalJson.SerializeBytes(ToWire(checkpoint)).AsSpan().SequenceEqual(bytes))
        {
            return CheckpointParse.Malformed("checkpoint bytes are not in canonical form");
        }

        return new CheckpointParse(CheckpointParseOutcome.Valid, null, checkpoint);
    }

    /// <summary>Field rules of one checkpoint, or null when valid.</summary>
    public static string? Validate(JournalVerificationCheckpoint c)
    {
        ArgumentNullException.ThrowIfNull(c);
        if (!ArchiveCompleteness.KnownStreams.Contains(c.StreamName, StringComparer.Ordinal))
        {
            return "unknown stream";
        }

        if (c.CheckpointSequence < 1)
        {
            return "checkpoint sequences start at 1";
        }

        if (c.VerifiedRevision < 0)
        {
            return "verified revision is negative";
        }

        if ((c.VerifiedRevision == 0) != (c.VerifiedDigest is null) || c.VerifiedDigest is { Length: not 32 })
        {
            return "verified digest must be 32 bytes, and absent exactly when nothing is verified (revision 0)";
        }

        if ((c.CheckpointSequence == 1) != (c.PreviousCheckpointDigest is null) || c.PreviousCheckpointDigest is { Length: not 32 })
        {
            return "previous checkpoint digest must be 32 bytes, and absent exactly at sequence 1";
        }

        if (c.CheckpointSequence == 1 && c.WalkKind != CheckpointWalkKind.Baseline)
        {
            return "sequence 1 must be a baseline";
        }

        if (!IsLabel(c.ArchiveInstanceId) || !IsLabel(c.VerifierIdentityLabel))
        {
            return "archive instance id and verifier identity label must be 1..128 printable ASCII characters";
        }

        if (c.BaselineExceptionIds.Any(id => !IsLabel(id)) || !c.BaselineExceptionIds.SequenceEqual(c.BaselineExceptionIds.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)))
        {
            return "baseline exception ids must be distinct, ordinal-sorted labels";
        }

        if ((c.AssuranceLevel == CheckpointAssuranceLevel.Full) != (c.BaselineExceptionIds.Count == 0))
        {
            return "Full assurance carries no exception; Reduced assurance names at least one (BL-3, BL-4)";
        }

        if (c.VerifiedAtUtc.Offset != TimeSpan.Zero)
        {
            return "verified time must be UTC";
        }

        return null;
    }

    /// <summary>
    /// CK-2/CK-3/AS-3 between a stored checkpoint (with the digest of its stored bytes) and the next one, or null when the link holds.
    /// A <see cref="CheckpointWalkKind.Baseline"/> successor (re-baseline, BL-10 as amended by A-1) keeps the archive instance and may only add exception ids;
    /// any other successor carries them unchanged. Whether a re-baseline was owner-approved cannot be proven from the data (J6, contract §2.2).
    /// </summary>
    public static string? CheckSuccessor(JournalVerificationCheckpoint previous, ReadOnlySpan<byte> previousDigest, JournalVerificationCheckpoint next)
    {
        ArgumentNullException.ThrowIfNull(previous);
        ArgumentNullException.ThrowIfNull(next);
        if (previous.OrganizationId != next.OrganizationId || !string.Equals(previous.StreamName, next.StreamName, StringComparison.Ordinal))
        {
            return "successor belongs to another organization or stream";
        }

        if (next.CheckpointSequence != previous.CheckpointSequence + 1)
        {
            return "successor sequence is not the next sequence";
        }

        if (next.PreviousCheckpointDigest is not { } link || previousDigest.Length != 32 || !previousDigest.SequenceEqual(link))
        {
            return "successor does not link to the digest of its predecessor";
        }

        if (next.VerifiedRevision < previous.VerifiedRevision)
        {
            return "verified revision decreased";
        }

        if (next.VerifiedRevision == previous.VerifiedRevision && !SameOptional(next.VerifiedDigest, previous.VerifiedDigest))
        {
            return "same verified revision with a different verified digest";
        }

        if (next.WalkKind != CheckpointWalkKind.Baseline
            && (!string.Equals(next.ArchiveInstanceId, previous.ArchiveInstanceId, StringComparison.Ordinal) || next.AssuranceLevel != previous.AssuranceLevel
                || !next.BaselineExceptionIds.SequenceEqual(previous.BaselineExceptionIds, StringComparer.Ordinal)))
        {
            return "archive instance, assurance level and baseline exceptions change only at a baseline";
        }

        // A-1 (WP-4A, OBS-4): a re-baseline continues the chain. It keeps the archive instance (a changed instance is recovery, G14),
        // never drops an accepted exception and never upgrades assurance; it may only add exceptions.
        if (next.WalkKind == CheckpointWalkKind.Baseline)
        {
            if (!string.Equals(next.ArchiveInstanceId, previous.ArchiveInstanceId, StringComparison.Ordinal))
            {
                return "a re-baseline must keep the archive instance of the chain";
            }

            if (previous.BaselineExceptionIds.Except(next.BaselineExceptionIds, StringComparer.Ordinal).Any())
            {
                return "a re-baseline must carry every accepted exception of the chain";
            }

            if (previous.AssuranceLevel == CheckpointAssuranceLevel.Reduced && next.AssuranceLevel != CheckpointAssuranceLevel.Reduced)
            {
                return "a re-baseline never upgrades assurance";
            }
        }

        return null;
    }

    /// <summary>
    /// Finds and verifies the latest checkpoint of (organization, stream) from a complete listing. <paramref name="minimumKnownSequence"/>
    /// is a head the caller already trusts (a process-local cache in Development/Testing, CKR-3; an independently anchored head in
    /// production, CKR-2): a lower discovered head is <see cref="CheckpointDiscoveryOutcome.Regression"/>. AuthorityDb is never consulted (CK-11).
    /// </summary>
    public static async Task<CheckpointDiscoveryResult> DiscoverAsync(IVerificationCheckpointStore store, Guid organizationId, string streamName, CheckpointDiscoveryMode mode,
        long minimumKnownSequence, int pageSizeHint, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(store);
        var listed = new List<CheckpointListedObject>();
        var containerAbsent = false;
        try
        {
            string? continuation = null;
            for (var page = 0; ; page++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (page >= MaximumListingPages)
                {
                    return Inconclusive(CheckpointFindingCodes.StoreUnavailable, "checkpoint listing exceeded the page bound", listed.Count, containerAbsent);
                }

                var result = await store.ListAsync(organizationId, streamName, continuation, pageSizeHint, cancellationToken).ConfigureAwait(false);
                containerAbsent |= result.ContainerAbsent;
                listed.AddRange(result.Objects);
                continuation = result.ContinuationToken;
                if (string.IsNullOrEmpty(continuation))
                {
                    break;
                }
            }
        }
        catch (CheckpointStoreUnavailableException ex)
        {
            return Inconclusive(CheckpointFindingCodes.StoreUnavailable, ex.Message, listed.Count, containerAbsent);
        }

        if (listed.FirstOrDefault(o => !o.IsCanonicalCheckpointKey || o.CheckpointSequence is not >= 1) is { } unexpected)
        {
            return Invalid(CheckpointFindingCodes.UnexpectedObject, "non-canonical object in the checkpoint prefix: " + Truncate(unexpected.ObjectName), listed.Count, containerAbsent);
        }

        var sequences = listed.Select(o => o.CheckpointSequence!.Value).Order().ToList();
        if (sequences.Distinct().Count() != sequences.Count)
        {
            return Invalid(CheckpointFindingCodes.ChainInvalid, "a checkpoint sequence is listed more than once", listed.Count, containerAbsent);
        }

        for (var i = 0; i < sequences.Count; i++)
        {
            if (sequences[i] != i + 1)
            {
                return Invalid(CheckpointFindingCodes.ChainInvalid, "checkpoint sequences are not contiguous from 1", listed.Count, containerAbsent);
            }
        }

        var head = sequences.Count;
        if (head < minimumKnownSequence)
        {
            return new CheckpointDiscoveryResult(CheckpointDiscoveryOutcome.Regression, CheckpointFindingCodes.StoreRegression,
                "the store's newest checkpoint is below the trusted head", null, null, listed.Count, containerAbsent);
        }

        if (head == 0)
        {
            return new CheckpointDiscoveryResult(CheckpointDiscoveryOutcome.Empty, null, null, null, null, 0, containerAbsent);
        }

        var first = mode == CheckpointDiscoveryMode.FullChain ? 1 : Math.Max(1, head - 1);
        JournalVerificationCheckpoint? previous = null;
        byte[]? previousDigest = null;
        for (long sequence = first; sequence <= head; sequence++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            StoredObjectRead read;
            try
            {
                read = await store.ReadAsync(organizationId, streamName, sequence, cancellationToken).ConfigureAwait(false);
            }
            catch (CheckpointStoreUnavailableException ex)
            {
                return Inconclusive(CheckpointFindingCodes.StoreUnavailable, ex.Message, listed.Count, containerAbsent);
            }

            if (read.Status != StoredObjectStatus.Present)
            {
                return Inconclusive(CheckpointFindingCodes.ListedObjectMissing, "a listed checkpoint could not be read", listed.Count, containerAbsent);
            }

            var bytes = read.Bytes!;
            var parsed = Parse(bytes);
            if (parsed.Outcome == CheckpointParseOutcome.Unsupported)
            {
                return Inconclusive(CheckpointFindingCodes.SchemaUnsupported, parsed.Detail!, listed.Count, containerAbsent);
            }

            if (parsed.Checkpoint is not { } checkpoint)
            {
                return Invalid(CheckpointFindingCodes.ChainInvalid, parsed.Detail!, listed.Count, containerAbsent);
            }

            if (checkpoint.OrganizationId != organizationId || !string.Equals(checkpoint.StreamName, streamName, StringComparison.Ordinal) || checkpoint.CheckpointSequence != sequence)
            {
                return Invalid(CheckpointFindingCodes.ChainInvalid, "checkpoint content differs from its key", listed.Count, containerAbsent);
            }

            if (previous is not null && CheckSuccessor(previous, previousDigest!, checkpoint) is { } broken)
            {
                return Invalid(CheckpointFindingCodes.ChainInvalid, broken, listed.Count, containerAbsent);
            }

            previous = checkpoint;
            previousDigest = Digest(bytes);
        }

        return new CheckpointDiscoveryResult(CheckpointDiscoveryOutcome.Head, null, null, previous, previousDigest, listed.Count, containerAbsent);
    }

    /// <summary>
    /// Creates the checkpoint that follows <paramref name="current"/> (which must be Empty or Head). Create-only: identical bytes already
    /// stored is an idempotent retry; different bytes at the same sequence is a conflict and nothing is overwritten.
    /// </summary>
    public static async Task<CheckpointAppendResult> CreateNextAsync(IVerificationCheckpointStore store, CheckpointDiscoveryResult current, JournalVerificationCheckpoint next, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(next);
        if (current.Outcome == CheckpointDiscoveryOutcome.Empty)
        {
            if (next.CheckpointSequence != 1)
            {
                return CheckpointAppendResult.Refused("the first checkpoint must have sequence 1");
            }
        }
        else if (current.Outcome == CheckpointDiscoveryOutcome.Head)
        {
            if (CheckSuccessor(current.Head!, current.HeadDigest!, next) is { } problem)
            {
                return CheckpointAppendResult.Refused(problem);
            }
        }
        else
        {
            return CheckpointAppendResult.Refused("no checkpoint may be created after an " + current.Outcome + " discovery (CK-1)");
        }

        if (Validate(next) is { } invalid)
        {
            return CheckpointAppendResult.Refused(invalid);
        }

        var bytes = Serialize(next);
        try
        {
            var result = await store.CreateAsync(next.OrganizationId, next.StreamName, next.CheckpointSequence, bytes, cancellationToken).ConfigureAwait(false);
            return result.Status switch
            {
                CheckpointCreateStatus.Created => new CheckpointAppendResult(CheckpointAppendOutcome.Created, null, null, Digest(bytes)),
                CheckpointCreateStatus.IdenticalExists => new CheckpointAppendResult(CheckpointAppendOutcome.AlreadyStored, null, null, Digest(bytes)),
                _ => new CheckpointAppendResult(CheckpointAppendOutcome.Conflict, CheckpointFindingCodes.Conflict, "another checkpoint is stored at this sequence", null),
            };
        }
        catch (CheckpointStoreUnavailableException ex)
        {
            // The write may or may not have happened; the next discovery decides (an identical retry is idempotent).
            return new CheckpointAppendResult(CheckpointAppendOutcome.Unavailable, CheckpointFindingCodes.StoreUnavailable, ex.Message, null);
        }
    }

    private static CheckpointWire ToWire(JournalVerificationCheckpoint c) => new(
        SchemaV1, c.OrganizationId, c.StreamName, c.CheckpointSequence, c.VerifiedRevision, Hex(c.VerifiedDigest), Hex(c.PreviousCheckpointDigest),
        c.ArchiveInstanceId, c.WalkKind.ToString(), c.AssuranceLevel.ToString(), [.. c.BaselineExceptionIds], c.VerifierIdentityLabel, c.VerifiedAtUtc);

    private static JournalVerificationCheckpoint? FromWire(CheckpointWire w)
    {
        if (w.CheckpointSchema is null || w.StreamName is null || w.ArchiveInstanceId is null || w.WalkKind is null || w.AssuranceLevel is null
            || w.BaselineExceptionIds is null || w.VerifierIdentityLabel is null
            || !Enum.TryParse<CheckpointWalkKind>(w.WalkKind, ignoreCase: false, out var walk) || !string.Equals(walk.ToString(), w.WalkKind, StringComparison.Ordinal)
            || !Enum.TryParse<CheckpointAssuranceLevel>(w.AssuranceLevel, ignoreCase: false, out var assurance) || !string.Equals(assurance.ToString(), w.AssuranceLevel, StringComparison.Ordinal)
            || !TryHex(w.VerifiedDigestHex, out var verified) || !TryHex(w.PreviousCheckpointDigestHex, out var previous))
        {
            return null;
        }

        return new JournalVerificationCheckpoint(w.OrganizationId, w.StreamName, w.CheckpointSequence, w.VerifiedRevision, verified, previous, w.ArchiveInstanceId,
            walk, assurance, w.BaselineExceptionIds, w.VerifierIdentityLabel, w.VerifiedAtUtc);
    }

    private static string? Hex(byte[]? value) => value is null ? null : Convert.ToHexStringLower(value);

    private static bool TryHex(string? text, out byte[]? value)
    {
        value = null;
        if (text is null)
        {
            return true;
        }

        if (text.Length != 64 || !text.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f'))
        {
            return false;
        }

        value = Convert.FromHexString(text);
        return true;
    }

    private static bool IsLabel(string? text) => !string.IsNullOrEmpty(text) && text.Length <= MaximumTextLength && text.All(c => c is >= '!' and <= '~');

    private static bool SameOptional(byte[]? a, byte[]? b) => a is null ? b is null : b is not null && a.AsSpan().SequenceEqual(b);

    private static string Truncate(string text) => text.Length <= 120 ? text : text[..120];

    private static CheckpointDiscoveryResult Inconclusive(string code, string detail, long listed, bool containerAbsent) =>
        new(CheckpointDiscoveryOutcome.Inconclusive, code, detail, null, null, listed, containerAbsent);

    private static CheckpointDiscoveryResult Invalid(string code, string detail, long listed, bool containerAbsent) =>
        new(CheckpointDiscoveryOutcome.Invalid, code, detail, null, null, listed, containerAbsent);

    /// <summary>The wire shape (member order is the canonical order; snake_case by <see cref="CanonicalJson"/>).</summary>
    private sealed record CheckpointWire(string CheckpointSchema, Guid OrganizationId, string StreamName, long CheckpointSequence, long VerifiedRevision, string? VerifiedDigestHex,
        string? PreviousCheckpointDigestHex, string ArchiveInstanceId, string WalkKind, string AssuranceLevel, IReadOnlyList<string> BaselineExceptionIds, string VerifierIdentityLabel,
        DateTimeOffset VerifiedAtUtc);
}

public enum CheckpointWalkKind
{
    Baseline,
    Incremental,
    Full,
}

public enum CheckpointAssuranceLevel
{
    Full,
    Reduced,
}

/// <summary>One checkpoint (contract §7.1). <see cref="VerifiedDigest"/> is SHA-256 of the archived entry at <see cref="VerifiedRevision"/> (absent for revision 0: an empty stream).</summary>
public sealed record JournalVerificationCheckpoint(Guid OrganizationId, string StreamName, long CheckpointSequence, long VerifiedRevision, byte[]? VerifiedDigest,
    byte[]? PreviousCheckpointDigest, string ArchiveInstanceId, CheckpointWalkKind WalkKind, CheckpointAssuranceLevel AssuranceLevel, IReadOnlyList<string> BaselineExceptionIds,
    string VerifierIdentityLabel, DateTimeOffset VerifiedAtUtc);

public enum CheckpointParseOutcome
{
    Valid,
    Malformed,
    Unsupported,
}

public sealed record CheckpointParse(CheckpointParseOutcome Outcome, string? Detail, JournalVerificationCheckpoint? Checkpoint)
{
    public static CheckpointParse Malformed(string detail) => new(CheckpointParseOutcome.Malformed, detail, null);
}

public enum CheckpointDiscoveryMode
{
    /// <summary>Verify the newest checkpoint and its link to the one before (CK-5, every verifier start).</summary>
    HeadAndPredecessor,

    /// <summary>Verify the whole chain from sequence 1 (CK-5, every full sweep).</summary>
    FullChain,
}

public enum CheckpointDiscoveryOutcome
{
    /// <summary>No checkpoint exists (and no trusted head says one should).</summary>
    Empty,

    /// <summary>The verified newest checkpoint.</summary>
    Head,

    /// <summary>The stored chain or prefix is corrupt (CKR-1): a security finding.</summary>
    Invalid,

    /// <summary>The newest stored checkpoint is below a head the caller trusts: a security finding (detectable only with such a head).</summary>
    Regression,

    /// <summary>Not decidable (store unavailable, listed object missing, unsupported schema): never a head.</summary>
    Inconclusive,
}

public sealed record CheckpointDiscoveryResult(CheckpointDiscoveryOutcome Outcome, string? FindingCode, string? Detail, JournalVerificationCheckpoint? Head, byte[]? HeadDigest, long ListedObjectCount, bool ContainerAbsent);

public enum CheckpointAppendOutcome
{
    Created,

    /// <summary>The identical checkpoint was already stored (idempotent retry).</summary>
    AlreadyStored,

    /// <summary>Different bytes are stored at this sequence; nothing was written.</summary>
    Conflict,

    /// <summary>The store was unavailable; whether the write happened is decided by the next discovery.</summary>
    Unavailable,

    /// <summary>The candidate does not follow the discovered state; nothing was written.</summary>
    Refused,
}

public sealed record CheckpointAppendResult(CheckpointAppendOutcome Outcome, string? FindingCode, string? Detail, byte[]? CheckpointDigest)
{
    public static CheckpointAppendResult Refused(string detail) => new(CheckpointAppendOutcome.Refused, null, detail, null);
}

/// <summary>Checkpoint finding codes (contract §9.1, §9.2).</summary>
public static class CheckpointFindingCodes
{
    public const string ChainInvalid = "CHECKPOINT_CHAIN_INVALID";
    public const string StoreRegression = "CHECKPOINT_STORE_REGRESSION";
    public const string StoreUnavailable = "CHECKPOINT_STORE_UNAVAILABLE";
    public const string UnexpectedObject = "CHECKPOINT_UNEXPECTED_OBJECT";
    public const string ListedObjectMissing = "CHECKPOINT_LISTED_OBJECT_MISSING";
    public const string SchemaUnsupported = "CHECKPOINT_SCHEMA_UNSUPPORTED";
    public const string Conflict = "CHECKPOINT_CONFLICT";
}
