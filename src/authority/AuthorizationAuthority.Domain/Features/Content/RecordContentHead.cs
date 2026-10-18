namespace AuthorizationAuthority.Domain.Features.Content;

/// <summary>The single trusted current content commitment of a registered record (A8). Revision 0 means "no content accepted yet".</summary>
public sealed class RecordContentHead
{
    public Guid RecordContentHeadId { get; set; }

    public Guid OrganizationId { get; set; }

    public Guid FormRecordId { get; set; }

    public long CommittedContentRevision { get; set; }

    public Guid? CommittedOperationId { get; set; }

    public Guid? CurrentRecordManifestCommitId { get; set; }

    public byte[] CommittedManifestRoot { get; set; } = [];

    public int SignatureKeyVersion { get; set; }

    public int ManifestSchemaVersion { get; set; } = 1;

    public bool ContentHeadQuarantined { get; set; }

    public string? QuarantineReasonCode { get; set; }

    public DateTimeOffset ContentHeadModifiedOn { get; set; }
}
