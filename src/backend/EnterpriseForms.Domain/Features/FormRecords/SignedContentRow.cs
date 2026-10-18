namespace EnterpriseForms.Domain.Features.FormRecords;

/// <summary>Merkle item kind codes (MERKLE_RECORD_MANIFEST_PROTOCOL.md §2.1). Mirrors the authority contract without referencing it.</summary>
public static class ContentItemKindCodes
{
    public const byte RecordHeader = 1;
    public const byte RepeatedGroup = 2;
    public const byte FieldValue = 3;
    public const byte FieldSelection = 4;
    public const byte Attachment = 5;
    public const byte AttachmentLifecycle = 6;
}

/// <summary>
/// A row that is staged immutably per content operation and signed under RowHmac/v1 (CLAUDE.md §4L.1). The envelope
/// columns are set by the stager; the canonical bytes are produced by the Application registry for the row's entity kind.
/// </summary>
public interface ISignedContentRow
{
    /// <summary>Registered entity kind (table name) of the signed schema.</summary>
    string EntityKind { get; }

    byte ItemKindCode { get; }

    /// <summary>Stable logical item identity used as the Merkle item id.</summary>
    Guid ItemId { get; }

    /// <summary>Physical primary key of the staged copy (canonical primary key for value rows).</summary>
    Guid RowPrimaryKey { get; }

    Guid TenantId { get; }

    Guid FormRecordId { get; }

    Guid ContentChangeOperationId { get; set; }

    long ContentRevision { get; set; }

    long RowRevision { get; set; }

    byte[] RowSignature { get; set; }

    int RowSignatureKeyVersion { get; set; }

    int RowSignatureSchemaVersion { get; set; }
}
