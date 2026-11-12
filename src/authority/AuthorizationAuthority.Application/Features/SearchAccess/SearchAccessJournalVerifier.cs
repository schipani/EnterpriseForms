using System.Text;
using System.Text.Json;
using AuthorizationAuthority.Application.Common;
using AuthorizationAuthority.Domain.Features.Journal;

namespace AuthorizationAuthority.Application.Features.SearchAccess;

/// <summary>One verified authorization-stream event: its revision, digest and access-relevant delta.</summary>
/// <summary>IX-5b: one verified content-stream event: its revision, digest, kind and the record it names.</summary>
public sealed record VerifiedContentEvent(long Revision, byte[] Digest, string EventKind, Guid FormRecordId);

public sealed record VerifiedAccessEvent(long Revision, byte[] Digest, IReadOnlyList<AccessDeltaEntry> Delta);

/// <summary>
/// IX-5a (FD-5-ALT): accepts authorization journal event n only if it is the checkpoint's successor. Checks the stored bytes against
/// the entry digest (SHA-256 of the canonical envelope), the envelope's format, organization, stream and revision, the chain link to
/// the checkpoint digest (in the envelope and in the column), the event kind and the committed revision, and parses the delta.
/// Any failure is typed and quarantines Tier 2; nothing is skipped or guessed.
/// </summary>
public static class SearchAccessJournalVerifier
{
    public const string EnvelopeFormat = "DynamicForms/SecurityJournal/v1";

    public const string CommandEventKind = "SecurityCommandCommitted";

    public static VerifiedAccessEvent Verify(SecurityJournalEntry? entry, Guid organizationId, long expectedRevision, byte[]? expectedPreviousDigest)
    {
        if (entry is null)
        {
            throw new SearchAccessIntegrityException(SearchAccessReasonCodes.JournalGap, "the next authorization journal event is missing");
        }

        if (entry.OrganizationId != organizationId || entry.JournalStreamName != JournalStreamNames.Authorization || entry.JournalStreamRevision != expectedRevision || entry.JournalEventKind != CommandEventKind)
        {
            throw new SearchAccessIntegrityException(SearchAccessReasonCodes.JournalRevisionMismatch, "the journal entry is not the expected authorization event");
        }

        var bytes = Encoding.UTF8.GetBytes(entry.JournalEventBodyJson);
        if (entry.JournalEventDigest is not { Length: 32 } || !CanonicalJson.Digest(bytes).AsSpan().SequenceEqual(entry.JournalEventDigest))
        {
            throw new SearchAccessIntegrityException(SearchAccessReasonCodes.JournalDigestMismatch, "the journal event bytes do not match their digest");
        }

        if (!DigestsEqual(entry.JournalPreviousDigest, expectedPreviousDigest))
        {
            throw new SearchAccessIntegrityException(SearchAccessReasonCodes.JournalChainBreak, "the journal event does not chain to the checkpoint");
        }

        List<AccessDeltaEntry> delta;
        try
        {
            using var document = JsonDocument.Parse(bytes);
            var root = document.RootElement;
            var previousHex = root.GetProperty("previous_digest_hex");
            var envelopePrevious = previousHex.ValueKind == JsonValueKind.Null ? null : Convert.FromHexString(previousHex.GetString()!);
            if (root.GetProperty("envelope_format").GetString() != EnvelopeFormat
                || root.GetProperty("organization_id").GetGuid() != organizationId
                || root.GetProperty("stream_name").GetString() != JournalStreamNames.Authorization
                || root.GetProperty("stream_revision").GetInt64() != expectedRevision
                || root.GetProperty("event_kind").GetString() != CommandEventKind)
            {
                throw new SearchAccessIntegrityException(SearchAccessReasonCodes.JournalEnvelopeInvalid, "the journal envelope does not describe this event");
            }

            if (!DigestsEqual(envelopePrevious, expectedPreviousDigest))
            {
                throw new SearchAccessIntegrityException(SearchAccessReasonCodes.JournalChainBreak, "the journal envelope does not chain to the checkpoint");
            }

            var body = root.GetProperty("body");
            if (body.GetProperty("committed_authorization_revision").GetInt64() != expectedRevision)
            {
                throw new SearchAccessIntegrityException(SearchAccessReasonCodes.JournalRevisionMismatch, "the committed revision differs from the stream revision");
            }

            delta = body.GetProperty("relationship_delta").EnumerateArray().Select(e => AccessRelationshipDelta.Parse(e.GetString()!)).ToList();
        }
        catch (SearchAccessIntegrityException)
        {
            throw;
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            throw new SearchAccessIntegrityException(SearchAccessReasonCodes.JournalEnvelopeInvalid, "the journal envelope is malformed");
        }

        return new VerifiedAccessEvent(expectedRevision, entry.JournalEventDigest, delta);
    }

    /// <summary>IX-5b: the content stream's event kinds (ContentService); any other kind on the stream fails closed.</summary>
    public static readonly IReadOnlySet<string> ContentEventKinds = new HashSet<string>(StringComparer.Ordinal) { "ContentHeadCommitted", "IntegrityVerificationFailed", "CurrentSnapshotMismatch" };

    /// <summary>
    /// IX-5b: accepts content-stream event n only if it is the content checkpoint's successor: the same byte, envelope and chain
    /// checks as the authorization stream, a known content event kind, and the record it names.
    /// </summary>
    public static VerifiedContentEvent VerifyContent(SecurityJournalEntry? entry, Guid organizationId, long expectedRevision, byte[]? expectedPreviousDigest)
    {
        if (entry is null)
        {
            throw new SearchAccessIntegrityException(SearchAccessReasonCodes.JournalGap, "the next content journal event is missing");
        }

        if (entry.OrganizationId != organizationId || entry.JournalStreamName != JournalStreamNames.Content || entry.JournalStreamRevision != expectedRevision || !ContentEventKinds.Contains(entry.JournalEventKind))
        {
            throw new SearchAccessIntegrityException(SearchAccessReasonCodes.JournalRevisionMismatch, "the journal entry is not the expected content event");
        }

        var bytes = Encoding.UTF8.GetBytes(entry.JournalEventBodyJson);
        if (entry.JournalEventDigest is not { Length: 32 } || !CanonicalJson.Digest(bytes).AsSpan().SequenceEqual(entry.JournalEventDigest))
        {
            throw new SearchAccessIntegrityException(SearchAccessReasonCodes.JournalDigestMismatch, "the content event bytes do not match their digest");
        }

        if (!DigestsEqual(entry.JournalPreviousDigest, expectedPreviousDigest))
        {
            throw new SearchAccessIntegrityException(SearchAccessReasonCodes.JournalChainBreak, "the content event does not chain to the checkpoint");
        }

        try
        {
            using var document = JsonDocument.Parse(bytes);
            var root = document.RootElement;
            var previousHex = root.GetProperty("previous_digest_hex");
            var envelopePrevious = previousHex.ValueKind == JsonValueKind.Null ? null : Convert.FromHexString(previousHex.GetString()!);
            if (root.GetProperty("envelope_format").GetString() != EnvelopeFormat
                || root.GetProperty("organization_id").GetGuid() != organizationId
                || root.GetProperty("stream_name").GetString() != JournalStreamNames.Content
                || root.GetProperty("stream_revision").GetInt64() != expectedRevision
                || root.GetProperty("event_kind").GetString() != entry.JournalEventKind)
            {
                throw new SearchAccessIntegrityException(SearchAccessReasonCodes.JournalEnvelopeInvalid, "the content envelope does not describe this event");
            }

            if (!DigestsEqual(envelopePrevious, expectedPreviousDigest))
            {
                throw new SearchAccessIntegrityException(SearchAccessReasonCodes.JournalChainBreak, "the content envelope does not chain to the checkpoint");
            }

            var record = root.GetProperty("body").GetProperty("form_record_id").GetGuid();
            return new VerifiedContentEvent(expectedRevision, entry.JournalEventDigest, entry.JournalEventKind, record);
        }
        catch (SearchAccessIntegrityException)
        {
            throw;
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            throw new SearchAccessIntegrityException(SearchAccessReasonCodes.JournalEnvelopeInvalid, "the content envelope is malformed");
        }
    }

    private static bool DigestsEqual(byte[]? left, byte[]? right) =>
        (left is null || left.Length == 0) ? (right is null || right.Length == 0) : right is not null && left.AsSpan().SequenceEqual(right);
}
