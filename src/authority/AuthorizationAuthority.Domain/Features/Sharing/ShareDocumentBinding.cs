namespace AuthorizationAuthority.Domain.Features.Sharing;

/// <summary>
/// C10 M1-WP1 / WP8: tracks the version visibility of a document item within a share. A document share covers the
/// attachment's versions up to <see cref="SharedThroughVersionNumber"/> at grant time. A replacement made through
/// this share extends it to the new version in the same command; versions added by anyone else stay invisible until
/// an <c>UpdateShareScope</c>. Replacement never widens access.
/// </summary>
public sealed class ShareDocumentBinding
{
    /// <summary>Surrogate primary key (UUID v7).</summary>
    public Guid ShareDocumentBindingId { get; set; }

    /// <summary>Organization to which the share belongs (FK to organization_authorization_states).</summary>
    public Guid OrganizationId { get; set; }

    /// <summary>The share to which this document is bound (FK to share_resource_metadata).</summary>
    public Guid RecordShareId { get; set; }

    /// <summary>The file attachment id being shared.</summary>
    public Guid FileAttachmentId { get; set; }

    /// <summary>Whether the recipient can view the document metadata.</summary>
    public bool CanViewDocument { get; set; }

    /// <summary>Whether the recipient can download the document content (implies CanViewDocument).</summary>
    public bool CanDownloadDocument { get; set; }

    /// <summary>
    /// Whether the recipient can replace the document with a new version (implies CanViewDocument). A replacement made
    /// through this share extends <see cref="SharedThroughVersionNumber"/> to the new version. Replacement never
    /// exposes a distinct attachment or versions by others.
    /// </summary>
    public bool CanReplaceDocument { get; set; }

    /// <summary>
    /// The highest attachment version number visible through this share. At grant time this equals the attachment's
    /// current version. When the sharing recipient adds a new version through this share, it is extended in the same
    /// command. Versions added by others remain invisible until an UpdateShareScope explicitly extends it.
    /// </summary>
    public int SharedThroughVersionNumber { get; set; }

    /// <summary>The security command that created or last updated this document binding.</summary>
    public Guid BoundBySecurityCommandId { get; set; }

    /// <summary>When this document binding was created.</summary>
    public DateTimeOffset DocumentBoundOn { get; set; }
}
