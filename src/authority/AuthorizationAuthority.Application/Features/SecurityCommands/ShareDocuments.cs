using AuthorizationAuthority.Application.Common;
using AuthorizationAuthority.Application.Common.Ports;
using AuthorizationAuthority.Contracts.V1;
using AuthorizationAuthority.Domain.Features.Content;

namespace AuthorizationAuthority.Application.Features.SecurityCommands;

/// <summary>
/// C10 WP8 (WP-0 Section 3.6): the document entries of a share. Each names an attachment of the record's accepted content and takes
/// its version boundary only from the Authority's own verified evidence (<see cref="RecordManifestLeaf.AttachmentVersionNumber"/>);
/// no business-supplied version is accepted. A delegated entry never exceeds the named parent's entry for the same attachment.
/// </summary>
internal static class ShareDocuments
{
    public const int MaximumDocuments = 100;

    /// <summary>The verified current version of each named attachment, after the entry rules; refuses anything else.</summary>
    public static Dictionary<Guid, int> Resolve(IReadOnlyList<ShareDocumentEntry> documents, IReadOnlyList<RecordManifestLeaf> acceptedLeaves)
    {
        if (documents.Count > MaximumDocuments || documents.Select(d => d.FileAttachmentId).Distinct().Count() != documents.Count)
        {
            throw AuthorityException.Invalid("documents must be a distinct list of at most 100 attachments");
        }

        var attachments = acceptedLeaves.Where(l => l.ManifestItemKindCode == (byte)ManifestItemKind.Attachment).ToDictionary(l => l.ManifestItemId);
        var versions = new Dictionary<Guid, int>();
        foreach (var document in documents)
        {
            if (!document.CanViewDocument)
            {
                throw AuthorityException.Invalid("a shared document must allow viewing; downloading and replacing each require viewing");
            }

            if (document.CanReplaceDocument)
            {
                throw new AuthorityException(AuthorityErrorCodes.DocumentReplaceNotSupported, 422, false, "replacing a document through a share is not available");
            }

            if (!attachments.TryGetValue(document.FileAttachmentId, out var leaf))
            {
                throw AuthorityException.Invalid("documents must reference attachments in the accepted manifest");
            }

            versions[document.FileAttachmentId] = leaf.AttachmentVersionNumber
                ?? throw new AuthorityException(AuthorityErrorCodes.DocumentVersionEvidenceUnavailable, 422, false, "the attachment has no verified version evidence yet; it can be shared after the record's next accepted change");
        }

        return versions;
    }

    /// <summary>A delegated share's documents must each be in the parent share with at least the same flags and a version boundary it already covers.</summary>
    public static async Task RequireWithinParentAsync(IAuthorityScope scope, Guid organizationId, Guid parentRecordShareId, IReadOnlyList<ShareDocumentEntry> documents,
        IReadOnlyDictionary<Guid, int> versions, CancellationToken cancellationToken)
    {
        if (documents.Count == 0)
        {
            return;
        }

        var parent = (await scope.GetShareDocumentBindingsAsync(organizationId, [parentRecordShareId], cancellationToken).ConfigureAwait(false))
            .Where(b => b.OrganizationId == organizationId && b.RecordShareId == parentRecordShareId).ToDictionary(b => b.FileAttachmentId);
        foreach (var document in documents)
        {
            if (!parent.TryGetValue(document.FileAttachmentId, out var held) || !held.CanViewDocument ||
                (document.CanDownloadDocument && !held.CanDownloadDocument) || (document.CanReplaceDocument && !held.CanReplaceDocument) ||
                versions[document.FileAttachmentId] > held.SharedThroughVersionNumber)
            {
                throw AuthorityException.Invalid("delegation exceeds the named parent's document scope");
            }
        }
    }
}
