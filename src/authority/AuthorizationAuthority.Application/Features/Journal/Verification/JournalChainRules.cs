using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AuthorizationAuthority.Domain.Features.Journal;

namespace AuthorizationAuthority.Application.Features.Journal.Verification;

/// <summary>
/// SEC-BL-01 WP-1 (C-155): chain rules R-1..R-7 of <c>docs/security/SEC_BL_01_DR_CONTRACT_CLOSURE.md</c> §4 over the security journal's
/// canonical envelope (<see cref="JournalChain"/>, format <see cref="JournalEnvelopeFormats.SecurityJournalV1"/>). One shared, pure
/// implementation for the future baseline tool, scheduled verifier and D+ delivery enforcement: no I/O, no AuthorityDb access, no
/// keys, no repair. Unknown envelope formats are never valid (R-2).
/// <para>
/// The journal digest is unkeyed SHA-256: these rules detect inconsistency, but an actor who can rewrite AuthorityDb can recompute a
/// consistent chain. Only comparison with an independently controlled reference (archive, trusted checkpoint) detects that (contract §2.1).
/// </para>
/// </summary>
public static class JournalChainRules
{
    /// <summary>Upper bound on findings kept by <see cref="VerifySequence"/>; further findings only set <see cref="JournalSequenceResult.FindingsTruncated"/>.</summary>
    public const int MaximumReportedFindings = 256;

    private static readonly string[] EnvelopeMembers =
        ["envelope_format", "organization_id", "stream_name", "stream_revision", "event_kind", "previous_digest_hex", "created_on_utc", "body"];

    /// <summary>R-1..R-5 for one entry (an AuthorityDb row or archived bytes). R-6 and R-7 need neighbours: <see cref="CheckLink(JournalEntryInspection, JournalEntryInspection)"/>, <see cref="VerifySequence"/>.</summary>
    public static JournalEntryInspection InspectEntry(JournalEvidence evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        if (evidence.Body is not { } body)
        {
            return Inspection(evidence.Key, JournalCheckOutcome.Unavailable, JournalFailureCodes.EvidenceUnavailable, "the evidence could not be read", null, null);
        }

        var digest = SHA256.HashData(body.Span);

        // R-1: the stored digest (row column) binds these exact bytes. Checked first: a body that no longer matches is tampering
        // (or a jsonb-era row, E-12) whatever its content.
        if (evidence.Columns is { } columns && (columns.EventDigest is not { Length: 32 } || !digest.AsSpan().SequenceEqual(columns.EventDigest)))
        {
            return Inspection(evidence.Key, JournalCheckOutcome.IntegrityFailure, JournalFailureCodes.BodyDigestMismatch, "SHA-256 of the stored body differs from the stored digest", null, digest);
        }

        var parsed = ParseHeader(body.Span);
        if (parsed.Header is not { } header)
        {
            return Inspection(evidence.Key, parsed.Outcome, parsed.Code, parsed.Detail, null, digest);
        }

        // R-3: the envelope identity equals the storage key and (rows) the columns.
        if (header.OrganizationId != evidence.Key.OrganizationId || !string.Equals(header.StreamName, evidence.Key.StreamName, StringComparison.Ordinal) || header.StreamRevision != evidence.Key.StreamRevision)
        {
            return Inspection(evidence.Key, JournalCheckOutcome.IntegrityFailure, JournalFailureCodes.EnvelopeColumnMismatch, "envelope identity differs from the storage key", header, digest);
        }

        if (evidence.Columns is { } row)
        {
            if (row.OrganizationId != header.OrganizationId || !string.Equals(row.StreamName, header.StreamName, StringComparison.Ordinal)
                || row.StreamRevision != header.StreamRevision || !string.Equals(row.EventKind, header.EventKind, StringComparison.Ordinal))
            {
                return Inspection(evidence.Key, JournalCheckOutcome.IntegrityFailure, JournalFailureCodes.EnvelopeColumnMismatch, "envelope identity or kind differs from the row columns", header, digest);
            }

            // R-4: the envelope's previous digest equals the previous-digest column.
            if (!SameOptionalDigest(row.PreviousDigest, header.PreviousDigest))
            {
                return Inspection(evidence.Key, JournalCheckOutcome.IntegrityFailure, JournalFailureCodes.EnvelopeColumnMismatch, "envelope previous digest differs from the previous-digest column", header, digest);
            }
        }

        // R-5: genesis. Revision 1 and only revision 1 has no previous digest; revisions start at 1.
        if (header.StreamRevision < 1)
        {
            return Inspection(evidence.Key, JournalCheckOutcome.IntegrityFailure, JournalFailureCodes.GenesisViolation, "stream revisions start at 1", header, digest);
        }

        if ((header.StreamRevision == 1) != (header.PreviousDigest is null))
        {
            return Inspection(evidence.Key, JournalCheckOutcome.IntegrityFailure, JournalFailureCodes.GenesisViolation,
                header.StreamRevision == 1 ? "revision 1 carries a previous digest" : "a revision above 1 has no previous digest", header, digest);
        }

        return Inspection(evidence.Key, JournalCheckOutcome.Valid, null, null, header, digest);
    }

    /// <summary>R-7 (with the identity part of R-6) between two inspected entries: same organization and stream, consecutive revisions, and the successor's previous digest equals SHA-256 of the predecessor's bytes.</summary>
    public static JournalLinkCheck CheckLink(JournalEntryInspection successor, JournalEntryInspection predecessor)
    {
        ArgumentNullException.ThrowIfNull(successor);
        ArgumentNullException.ThrowIfNull(predecessor);
        if (!successor.IsValid || !predecessor.IsValid)
        {
            return new JournalLinkCheck(Worst(successor.Outcome, predecessor.Outcome), JournalFailureCodes.LinkNotEvaluable, "both entries must be valid before their link is evaluated");
        }

        if (successor.Key.OrganizationId != predecessor.Key.OrganizationId || !string.Equals(successor.Key.StreamName, predecessor.Key.StreamName, StringComparison.Ordinal))
        {
            return new JournalLinkCheck(JournalCheckOutcome.IntegrityFailure, JournalFailureCodes.EnvelopeColumnMismatch, "predecessor belongs to another organization or stream");
        }

        if (predecessor.Key.StreamRevision != successor.Key.StreamRevision - 1)
        {
            return new JournalLinkCheck(JournalCheckOutcome.IntegrityFailure, JournalFailureCodes.RevisionGap, "predecessor is not the immediately preceding revision");
        }

        return CheckLink(successor, predecessor.BodyDigest!);
    }

    /// <summary>R-7 against a trusted predecessor digest (for example a verification checkpoint's verified digest).</summary>
    public static JournalLinkCheck CheckLink(JournalEntryInspection successor, ReadOnlySpan<byte> predecessorDigest)
    {
        ArgumentNullException.ThrowIfNull(successor);
        if (!successor.IsValid)
        {
            return new JournalLinkCheck(successor.Outcome, JournalFailureCodes.LinkNotEvaluable, "the successor must be valid before its link is evaluated");
        }

        if (successor.Header!.PreviousDigest is not { } previous || predecessorDigest.Length != 32 || !predecessorDigest.SequenceEqual(previous))
        {
            return new JournalLinkCheck(JournalCheckOutcome.IntegrityFailure, JournalFailureCodes.LinkMismatch, "previous digest differs from SHA-256 of the predecessor");
        }

        return new JournalLinkCheck(JournalCheckOutcome.Valid, null, null);
    }

    /// <summary>
    /// R-1..R-7 over an ordered run of one (organization, stream). Entries may arrive in any order; duplicates, gaps, a wrong first or
    /// last revision, foreign organizations or streams, unavailable evidence and broken links are all reported. Findings are collected
    /// (bounded) rather than stopping at the first, for forensics; nothing is repaired.
    /// </summary>
    public static JournalSequenceResult VerifySequence(IEnumerable<JournalEvidence> entries, JournalSequenceExpectation expectation, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(expectation);
        if (expectation.FirstRevision < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(expectation), "the first revision must be at least 1");
        }

        var findings = new FindingList();
        var inspected = new List<JournalEntryInspection>();
        foreach (var evidence in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ArgumentNullException.ThrowIfNull(evidence);
            if (evidence.Key.OrganizationId != expectation.OrganizationId || !string.Equals(evidence.Key.StreamName, expectation.StreamName, StringComparison.Ordinal))
            {
                findings.Add(new JournalFinding(evidence.Key.StreamRevision, JournalCheckOutcome.IntegrityFailure, JournalFailureCodes.EnvelopeColumnMismatch, "entry belongs to another organization or stream"));
                continue;
            }

            inspected.Add(InspectEntry(evidence));
        }

        inspected.Sort((a, b) => a.Key.StreamRevision.CompareTo(b.Key.StreamRevision));
        long? lastValid = null;
        byte[]? lastValidDigest = null;
        var contiguous = true;
        var expectedNext = expectation.FirstRevision;
        JournalEntryInspection? previous = null;
        foreach (var entry in inspected)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var revision = entry.Key.StreamRevision;
            if (previous is not null && revision == previous.Key.StreamRevision)
            {
                findings.Add(new JournalFinding(revision, JournalCheckOutcome.IntegrityFailure, JournalFailureCodes.DuplicateRevision, "more than one entry claims this revision"));
                contiguous = false;
                continue;
            }

            if (revision < expectation.FirstRevision || (expectation.LastRevision is { } last && revision > last))
            {
                findings.Add(new JournalFinding(revision, JournalCheckOutcome.IntegrityFailure, JournalFailureCodes.RevisionGap, "entry outside the expected revision range"));
                contiguous = false;
                previous = entry;
                continue;
            }

            if (revision != expectedNext)
            {
                findings.Add(new JournalFinding(expectedNext, JournalCheckOutcome.IntegrityFailure, JournalFailureCodes.RevisionGap,
                    string.Create(CultureInfo.InvariantCulture, $"revisions {expectedNext}..{revision - 1} are missing")));
                contiguous = false;
            }

            expectedNext = revision + 1;
            if (!entry.IsValid)
            {
                findings.Add(new JournalFinding(revision, entry.Outcome, entry.FailureCode!, entry.FailureDetail!));
                contiguous = false;
                previous = entry;
                continue;
            }

            var linked = LinkWithinRun(entry, previous, expectation);
            if (linked is { } failure)
            {
                findings.Add(new JournalFinding(revision, failure.Outcome, failure.FailureCode!, failure.FailureDetail!));
                contiguous = false;
            }

            if (revision == expectation.FirstRevision && expectation.FirstEntryDigest is { } anchor && !anchor.AsSpan().SequenceEqual(entry.BodyDigest))
            {
                findings.Add(new JournalFinding(revision, JournalCheckOutcome.IntegrityFailure, JournalFailureCodes.AnchorMismatch, "first entry differs from the trusted anchor digest"));
                contiguous = false;
            }

            if (contiguous)
            {
                lastValid = revision;
                lastValidDigest = entry.BodyDigest;
            }

            previous = entry;
        }

        if (expectation.LastRevision is { } expectedLast && expectedNext <= expectedLast)
        {
            findings.Add(new JournalFinding(expectedNext, JournalCheckOutcome.IntegrityFailure, JournalFailureCodes.RevisionGap,
                string.Create(CultureInfo.InvariantCulture, $"revisions {expectedNext}..{expectedLast} are missing")));
        }

        var outcome = findings.Items.Count == 0 ? JournalCheckOutcome.Valid : findings.Items.Select(f => f.Outcome).Aggregate(JournalCheckOutcome.Valid, Worst);
        return new JournalSequenceResult(outcome, findings.Items, findings.Truncated, lastValid, lastValidDigest, inspected.Count);
    }

    /// <summary>Strict header parse of the v1 envelope (R-2): exactly the eight members, no duplicates, canonical value shapes.</summary>
    public static JournalHeaderParse ParseHeader(ReadOnlySpan<byte> body)
    {
        JsonDocument document;
        try
        {
            var reader = new Utf8JsonReader(body, new JsonReaderOptions { CommentHandling = JsonCommentHandling.Disallow, AllowTrailingCommas = false });
            document = JsonDocument.ParseValue(ref reader);
            if (reader.BytesConsumed != body.Length)
            {
                document.Dispose();
                return Malformed("trailing bytes after the envelope");
            }
        }
        catch (JsonException)
        {
            return Malformed("not a single JSON value");
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return Malformed("envelope is not a JSON object");
            }

            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var member in root.EnumerateObject())
            {
                if (!seen.Add(member.Name))
                {
                    return Malformed("duplicate envelope member '" + member.Name + "'");
                }
            }

            if (!root.TryGetProperty("envelope_format", out var format) || format.ValueKind != JsonValueKind.String)
            {
                return Malformed("envelope_format missing or not a string");
            }

            var formatText = format.GetString()!;
            if (!string.Equals(formatText, JournalEnvelopeFormats.SecurityJournalV1, StringComparison.Ordinal))
            {
                return new JournalHeaderParse(JournalCheckOutcome.Unsupported, JournalFailureCodes.EnvelopeUnsupported, "envelope format '" + Truncate(formatText) + "' is not supported", null);
            }

            if (seen.Count != EnvelopeMembers.Length || !EnvelopeMembers.All(seen.Contains))
            {
                return Malformed("envelope members differ from the v1 member set");
            }

            if (root.GetProperty("organization_id") is not { ValueKind: JsonValueKind.String } organizationElement
                || !Guid.TryParseExact(organizationElement.GetString(), "D", out var organization)
                || !string.Equals(organization.ToString("D"), organizationElement.GetString(), StringComparison.Ordinal))
            {
                return Malformed("organization_id is not a canonical identifier");
            }

            if (root.GetProperty("stream_name") is not { ValueKind: JsonValueKind.String } streamElement || string.IsNullOrEmpty(streamElement.GetString()))
            {
                return Malformed("stream_name missing");
            }

            if (root.GetProperty("stream_revision") is not { ValueKind: JsonValueKind.Number } revisionElement || !revisionElement.TryGetInt64(out var revision))
            {
                return Malformed("stream_revision is not an integer");
            }

            if (root.GetProperty("event_kind") is not { ValueKind: JsonValueKind.String } kindElement || string.IsNullOrEmpty(kindElement.GetString()))
            {
                return Malformed("event_kind missing");
            }

            byte[]? previousDigest = null;
            var previousElement = root.GetProperty("previous_digest_hex");
            if (previousElement.ValueKind == JsonValueKind.String)
            {
                var hex = previousElement.GetString()!;
                if (hex.Length != 64 || !hex.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f'))
                {
                    return Malformed("previous_digest_hex is not 64 lower-case hexadecimal characters");
                }

                previousDigest = Convert.FromHexString(hex);
            }
            else if (previousElement.ValueKind != JsonValueKind.Null)
            {
                return Malformed("previous_digest_hex is neither null nor a string");
            }

            if (root.GetProperty("created_on_utc") is not { ValueKind: JsonValueKind.String } createdElement || !createdElement.TryGetDateTimeOffset(out var created))
            {
                return Malformed("created_on_utc is not a timestamp");
            }

            if (root.GetProperty("body").ValueKind != JsonValueKind.Object)
            {
                return Malformed("body is not a JSON object");
            }

            return new JournalHeaderParse(JournalCheckOutcome.Valid, null, null,
                new JournalEnvelopeHeader(formatText, organization, streamElement.GetString()!, revision, kindElement.GetString()!, previousDigest, created));
        }
    }

    /// <summary>Precedence for combined outcomes: an integrity failure dominates, then malformed, then unavailable, then unsupported; valid only when nothing else applies.</summary>
    public static JournalCheckOutcome Worst(JournalCheckOutcome a, JournalCheckOutcome b) => Rank(a) >= Rank(b) ? a : b;

    private static int Rank(JournalCheckOutcome outcome) => outcome switch
    {
        JournalCheckOutcome.IntegrityFailure => 4,
        JournalCheckOutcome.Malformed => 3,
        JournalCheckOutcome.Unavailable => 2,
        JournalCheckOutcome.Unsupported => 1,
        _ => 0,
    };

    private static JournalLinkCheck? LinkWithinRun(JournalEntryInspection entry, JournalEntryInspection? previous, JournalSequenceExpectation expectation)
    {
        var revision = entry.Key.StreamRevision;
        if (revision == 1)
        {
            return null; // genesis: R-5 already checked the null previous digest
        }

        if (previous is not null && previous.Key.StreamRevision == revision - 1)
        {
            if (!previous.IsValid)
            {
                return null; // the predecessor's own failure is reported; its link cannot be evaluated
            }

            var link = CheckLink(entry, previous);
            return link.IsValid ? null : link;
        }

        if (revision == expectation.FirstRevision && expectation.PredecessorDigest is { } predecessorDigest)
        {
            var link = CheckLink(entry, predecessorDigest);
            return link.IsValid ? null : link;
        }

        return null; // no predecessor in this run (gap already reported, or an unanchored start)
    }

    private static bool SameOptionalDigest(byte[]? a, byte[]? b) => a is null ? b is null : b is not null && a.AsSpan().SequenceEqual(b);

    private static JournalHeaderParse Malformed(string detail) => new(JournalCheckOutcome.Malformed, JournalFailureCodes.EnvelopeMalformed, detail, null);

    private static JournalEntryInspection Inspection(JournalStorageKey key, JournalCheckOutcome outcome, string? code, string? detail, JournalEnvelopeHeader? header, byte[]? digest) =>
        new(key, outcome, code, detail, header, digest);

    private static string Truncate(string text) => text.Length <= 80 ? text : text[..80];

    private sealed class FindingList
    {
        public List<JournalFinding> Items { get; } = [];

        public bool Truncated { get; private set; }

        public void Add(JournalFinding finding)
        {
            if (Items.Count < MaximumReportedFindings)
            {
                Items.Add(finding);
            }
            else
            {
                Truncated = true;
            }
        }
    }
}

/// <summary>Envelope format literals the rules accept (R-2). Only v1 exists; anything else is <see cref="JournalCheckOutcome.Unsupported"/>.</summary>
public static class JournalEnvelopeFormats
{
    /// <summary>Written by <see cref="JournalChain"/>; pinned by the known-answer vectors <c>trust_authority/journal-envelope-v1.test-vectors.json</c>.</summary>
    public const string SecurityJournalV1 = "DynamicForms/SecurityJournal/v1";
}

/// <summary>Outcome classes (WP-1): an integrity failure is a security-relevant contradiction; malformed and unsupported content is never valid; unavailable evidence is never valid.</summary>
public enum JournalCheckOutcome
{
    Valid,
    IntegrityFailure,
    Malformed,
    Unsupported,
    Unavailable,
}

/// <summary>Failure codes. The first five are the contract's <c>CHAIN_INTEGRITY_FAILURE</c> subtypes (§4, §9.1).</summary>
public static class JournalFailureCodes
{
    public const string BodyDigestMismatch = "BODY_DIGEST_MISMATCH";
    public const string EnvelopeColumnMismatch = "ENVELOPE_COLUMN_MISMATCH";
    public const string GenesisViolation = "GENESIS_VIOLATION";
    public const string RevisionGap = "REVISION_GAP";
    public const string LinkMismatch = "LINK_MISMATCH";

    /// <summary>Two entries claim one revision (contract AE-5 for archived objects).</summary>
    public const string DuplicateRevision = "DUPLICATE_REVISION";

    /// <summary>The first entry of a run differs from the trusted anchor digest (contract VF-6, <c>ANCHOR_MISMATCH</c>).</summary>
    public const string AnchorMismatch = "ANCHOR_MISMATCH";

    public const string EnvelopeMalformed = "ENVELOPE_MALFORMED";
    public const string EnvelopeUnsupported = "ENVELOPE_UNSUPPORTED";
    public const string EvidenceUnavailable = "EVIDENCE_UNAVAILABLE";
    public const string LinkNotEvaluable = "LINK_NOT_EVALUABLE";
}

public sealed record JournalStorageKey(Guid OrganizationId, string StreamName, long StreamRevision);

/// <summary>The row columns that R-1, R-3 and R-4 compare with the envelope.</summary>
public sealed record JournalRowColumns(Guid OrganizationId, string StreamName, long StreamRevision, string EventKind, byte[]? PreviousDigest, byte[] EventDigest);

/// <summary>One entry to check: an AuthorityDb row (bytes + columns), archived bytes (key + bytes), or evidence that could not be read.</summary>
public sealed record JournalEvidence(JournalStorageKey Key, ReadOnlyMemory<byte>? Body, JournalRowColumns? Columns)
{
    /// <summary>A journal row. The bytes are the UTF-8 encoding of the stored body text, exactly as <c>ArchiveProcessor</c> computes them.</summary>
    public static JournalEvidence FromRow(SecurityJournalEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var columns = new JournalRowColumns(entry.OrganizationId, entry.JournalStreamName, entry.JournalStreamRevision, entry.JournalEventKind, entry.JournalPreviousDigest, entry.JournalEventDigest);
        return new JournalEvidence(new JournalStorageKey(entry.OrganizationId, entry.JournalStreamName, entry.JournalStreamRevision), Encoding.UTF8.GetBytes(entry.JournalEventBodyJson), columns);
    }

    /// <summary>Bytes read from the archive at <paramref name="key"/>. No stored digest exists for them; R-1 is applied through comparisons.</summary>
    public static JournalEvidence FromArchive(JournalStorageKey key, byte[] bytes) => new(key, bytes ?? throw new ArgumentNullException(nameof(bytes)), null);

    /// <summary>Evidence for <paramref name="key"/> exists but could not be read (store unavailable): never valid.</summary>
    public static JournalEvidence Unavailable(JournalStorageKey key) => new(key, null, null);
}

public sealed record JournalEnvelopeHeader(string EnvelopeFormat, Guid OrganizationId, string StreamName, long StreamRevision, string EventKind, byte[]? PreviousDigest, DateTimeOffset CreatedOnUtc);

public sealed record JournalHeaderParse(JournalCheckOutcome Outcome, string? Code, string? Detail, JournalEnvelopeHeader? Header);

/// <summary>Result of R-1..R-5 on one entry; <see cref="BodyDigest"/> is SHA-256 of the bytes whenever they were available.</summary>
public sealed record JournalEntryInspection(JournalStorageKey Key, JournalCheckOutcome Outcome, string? FailureCode, string? FailureDetail, JournalEnvelopeHeader? Header, byte[]? BodyDigest)
{
    public bool IsValid => Outcome == JournalCheckOutcome.Valid;
}

public sealed record JournalLinkCheck(JournalCheckOutcome Outcome, string? FailureCode, string? FailureDetail)
{
    public bool IsValid => Outcome == JournalCheckOutcome.Valid;
}

/// <summary>
/// The run to verify. <paramref name="PredecessorDigest"/>: trusted digest of revision <c>FirstRevision − 1</c> (R-7 for the first entry);
/// <paramref name="FirstEntryDigest"/>: trusted digest the first entry itself must have (anchor); <paramref name="LastRevision"/>: the
/// revision the run must reach (missing tail entries are a gap). Each is optional; an absent anchor means the run is checked only for
/// internal consistency.
/// </summary>
public sealed record JournalSequenceExpectation(Guid OrganizationId, string StreamName, long FirstRevision, byte[]? PredecessorDigest = null, byte[]? FirstEntryDigest = null, long? LastRevision = null);

public sealed record JournalFinding(long StreamRevision, JournalCheckOutcome Outcome, string Code, string Detail);

/// <summary>
/// <see cref="LastContiguousValidRevision"/>/<see cref="LastContiguousValidDigest"/>: the highest revision up to which every entry from
/// the first was present, valid, linked and (when given) anchored; null when the first entry already failed.
/// </summary>
public sealed record JournalSequenceResult(JournalCheckOutcome Outcome, IReadOnlyList<JournalFinding> Findings, bool FindingsTruncated, long? LastContiguousValidRevision, byte[]? LastContiguousValidDigest, int EntriesInspected)
{
    public bool IsValid => Outcome == JournalCheckOutcome.Valid;
}
