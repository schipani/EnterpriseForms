using AuthorizationAuthority.Application.Common;
using AuthorizationAuthority.Contracts.V1;
using AuthorizationAuthority.Domain.Features.Content;
using EnterpriseForms.Integrity.Canonical;

namespace AuthorizationAuthority.Application.Features.Content;

/// <summary>
/// C10 WP8-B (WP-0 Sections 3.6 and 3.6.1, owner decision 2026-11-27): a share editor's replacement of exactly one attachment with its
/// next accepted version (a number above the accepted one), under the existing content acceptance rule version. The version, record,
/// organization and scan are re-derived here at Commit from the verified staged bytes and the accepted leaves; the field and the other
/// identity columns are re-derived at Commit by the item identity binding (<c>ContentItemIdentityRules.BindAtCommitAsync</c>), and the
/// staged bytes are digest-bound to the candidate recorded at Prepare; the schema, envelope and data-key rules are the owner path's own,
/// which Commit already re-derives — so no check is Prepare-only and the CARV fence is unchanged
/// (docs/security/E3_M2B_IMPLEMENTATION_CONTRACT.md §3, §19). A build without WP8-B refuses such an operation at Commit
/// (<c>AuthorizeMutations</c> rejects non-owner attachment mutations), so a rolling deployment fails closed.
/// </summary>
internal static class ShareDocumentReplacement
{
    private const string VersionColumn = "version_number";

    /// <summary>The one replaced attachment of an operation's mutations, or null.</summary>
    public static Guid? ReplacedItem(IReadOnlyDictionary<string, string> mutations)
    {
        var updated = mutations.Where(m => m.Value == ContentMutationKinds.Update).Select(m => Convert.FromHexString(m.Key)).Where(k => k[0] == (byte)ManifestItemKind.Attachment).ToList();
        return updated.Count == 1 ? new Guid(updated[0].AsSpan(1, 16), bigEndian: true) : null;
    }

    /// <summary>Commit: re-derives the replacement rules from the verified staged bytes and the accepted leaves; returns the new version.</summary>
    public static int RequireAtCommit(Guid item, IReadOnlyList<(ManifestItemKind Kind, Guid ItemId, RowCanonicalDocument Document)> staged,
        IEnumerable<(Guid ItemId, byte KindCode, int? Version)> candidateLeaves, IEnumerable<(Guid ItemId, byte KindCode, int? Version)> currentLeaves, Guid organizationId, Guid formRecordId)
    {
        var document = staged.Single(s => s.Kind == ManifestItemKind.Attachment && s.ItemId == item).Document;
        var current = currentLeaves.SingleOrDefault(l => l.KindCode == (byte)ManifestItemKind.Attachment && l.ItemId == item).Version;
        var candidate = candidateLeaves.SingleOrDefault(l => l.KindCode == (byte)ManifestItemKind.Attachment && l.ItemId == item).Version;
        RequireReplacement(document, item, current, organizationId, formRecordId);
        if (candidate != document.Column(VersionColumn)?.AsInt32())
        {
            throw Refused("the recorded candidate version differs from the staged row");
        }

        return candidate!.Value;
    }

    private static void RequireReplacement(RowCanonicalDocument candidate, Guid item, int? currentVersion, Guid organizationId, Guid formRecordId)
    {
        if (currentVersion is not { } trusted)
        {
            throw Refused("the attachment has no verified version evidence yet");
        }

        if (candidate.Column(VersionColumn)?.AsInt32() is not { } next || next <= trusted)
        {
            throw Refused("a replacement must carry a version above the accepted one");
        }

        if (candidate.Column("file_attachment_id")?.AsUuid() != item || candidate.Column("form_record_id")?.AsUuid() != formRecordId ||
            candidate.Column("tenant_id")?.AsUuid() != organizationId)
        {
            throw Refused("a replacement keeps the attachment, record and organization");
        }

        if (candidate.Column("scan_verdict")?.AsText() != "Clean")
        {
            throw Refused("only a version that scanned clean can replace a document");
        }
    }

    private static AuthorityException Refused(string detail) => AuthorityException.Forbidden("SHARE_DOCUMENT_REPLACEMENT_REFUSED: " + detail);
}
