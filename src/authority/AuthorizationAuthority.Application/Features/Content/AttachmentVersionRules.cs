using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using AuthorizationAuthority.Application.Common;
using EnterpriseForms.Integrity.Canonical;

namespace AuthorizationAuthority.Application.Features.Content;

/// <summary>
/// C10 (independent review D-1, owner decision 2026-11-27; WP-0 Section 3.6): the attachment version number is the evidence a document
/// share's boundary is compared with, so on every content write path except protection migration (which keeps the row's version) each
/// attachment leaf of a candidate that is also in the accepted head must be either
/// <list type="bullet">
/// <item>the same version with unchanged verified content (a carried row), or</item>
/// <item>exactly the next version (new content).</item>
/// </list>
/// A lower version, a skipped number and new content under the accepted number are refused; so is a carried row whose accepted content
/// evidence is unknown (fail closed). An attachment not in the accepted head needs a positive version number.
/// <para>
/// The content identity is <see cref="Fingerprint"/>: SHA-256 over the row's own Content-class column frames, byte for byte as they
/// appear in the signed canonical row. Every carried row is re-staged with a new operation binding (an OperationBinding-class column), so
/// its canonical digest changes while its fingerprint does not. Prepare takes the accepted values from the verified current snapshot;
/// Commit re-derives the candidate values from the verified staged rows and takes the accepted ones from the accepted leaves'
/// evidence (or, for a head committed before this evidence existed, from what this operation's Prepare observed in the verified
/// snapshot, against the same head). With neither, the carried row is refused, so the rule is never Prepare-only and the content
/// acceptance rule version is unchanged (docs/security/E3_M2B_IMPLEMENTATION_CONTRACT.md §3.3).
/// </para>
/// </summary>
internal static class AttachmentVersionRules
{
    public const string RefusalCode = "ATTACHMENT_VERSION_NOT_INCREASING";

    public const string ContentChangedCode = "ATTACHMENT_CONTENT_CHANGED_WITHOUT_NEW_VERSION";

    public const string EvidenceMissingCode = "ATTACHMENT_CONTENT_EVIDENCE_MISSING";

    public const string NotNextCode = "ATTACHMENT_VERSION_NOT_NEXT";

    /// <summary>The accepted evidence of one attachment: its version number and content fingerprint (either may be unknown).</summary>
    public readonly record struct Evidence(int? Version, byte[]? Fingerprint);

    /// <param name="staged">Each attachment leaf of the candidate with its staged version number and content fingerprint.</param>
    /// <param name="accepted">The accepted evidence of each attachment of the current head (absent when it is not in the head).</param>
    public static void Require(IEnumerable<(Guid ItemId, int? Version, byte[] Fingerprint)> staged, IReadOnlyDictionary<Guid, Evidence> accepted)
    {
        foreach (var (itemId, version, fingerprint) in staged)
        {
            if (version is not { } number || number <= 0)
            {
                throw Refused(RefusalCode, "an attachment version needs a positive version number");
            }

            if (!accepted.TryGetValue(itemId, out var current))
            {
                continue; // not in the accepted head: a new attachment (a removed item id can never return, ContentItemIdentityRules I-2)
            }

            if (current.Version is not { } was)
            {
                throw Refused(EvidenceMissingCode, "the accepted version of this attachment is unknown");
            }

            if (number < was)
            {
                throw Refused(RefusalCode, "an attachment's version number can never decrease");
            }

            if (number == was)
            {
                if (current.Fingerprint is not { Length: 32 } known)
                {
                    throw Refused(EvidenceMissingCode, "the accepted content of this attachment version is unknown");
                }

                if (!CryptographicOperations.FixedTimeEquals(known, fingerprint))
                {
                    throw Refused(ContentChangedCode, "new attachment content needs the next version number");
                }

                continue;
            }

            if (number != was + 1)
            {
                throw Refused(NotNextCode, "a new attachment version must be exactly the next version number");
            }
        }
    }

    /// <summary>
    /// The content identity of a verified attachment row: SHA-256 over its Content-class columns (registered rule) in canonical column
    /// order, each as its exact canonical frame (16-bit big-endian name length, name, framed value). These are the bytes the row signature
    /// already covers; nothing is re-encoded. Identity and operation-binding columns are excluded (the version number is compared on its own).
    /// </summary>
    public static byte[] Fingerprint(RowCanonicalDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var rule = ContentDeltaRules.FindRule(document.Header.EntityKind, document.Header.SchemaVersion)
                   ?? throw AuthorityException.Invalid("UNSUPPORTED_SIGNED_SCHEMA: entity kind or schema version has no column-class assignment");
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Span<byte> length = stackalloc byte[2];
        foreach (var column in document.Columns)
        {
            if (!rule.Columns.TryGetValue(column.Name, out var columnClass) || columnClass != SignedColumnClass.Content)
            {
                continue;
            }

            var name = Encoding.ASCII.GetBytes(column.Name);
            BinaryPrimitives.WriteUInt16BigEndian(length, checked((ushort)name.Length));
            hash.AppendData(length);
            hash.AppendData(name);
            hash.AppendData(column.FramedValue.Span);
        }

        return hash.GetHashAndReset();
    }

    private static AuthorityException Refused(string code, string detail) => AuthorityException.Invalid(code + ": " + detail);
}
