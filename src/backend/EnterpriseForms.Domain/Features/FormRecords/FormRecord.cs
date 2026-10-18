namespace EnterpriseForms.Domain.Features.FormRecords;

/// <summary>
/// Persistent business record identity (CLAUDE.md §2.3). Ownership columns are an UNTRUSTED local projection of the
/// authority's owner edge; they are deliberately excluded from the signed header (row-integrity-format.md §7, schema v2)
/// and never consulted for authorization.
/// </summary>
public sealed class FormRecord
{
    public Guid FormRecordId { get; set; }

    public Guid TenantId { get; set; }

    public Guid FormDefinitionId { get; set; }

    public Guid CreatedFormVersionId { get; set; }

    public Guid? LastEditedFormVersionId { get; set; }

    public string RecordStatus { get; set; } = "Draft";

    public Guid CreatedByUserId { get; set; }

    public Guid? OwnerUserId { get; set; }

    public Guid? OwnerTeamId { get; set; }

    public DateTimeOffset CreatedOn { get; set; }

    public DateTimeOffset ModifiedOn { get; set; }

    public Guid ModifiedByUserId { get; set; }

    public DateTimeOffset? SubmittedOn { get; set; }

    /// <summary>Local hint of the latest locally staged/committed content revision; the authority head is authoritative.</summary>
    public long RecordRevision { get; set; } = 1;

    public Guid? AuthorityCommandId { get; set; }
}

/// <summary>
/// Immutable staged header candidate keyed by (record, operation) — the RecordHeader Merkle item (MERKLE §2).
/// Signed under RowHmac v1 entity kind <c>form_records</c>, schema version 2, primary key = form_record_id.
/// </summary>
public sealed class FormRecordRevision
{
    public Guid FormRecordRevisionId { get; set; }

    public Guid TenantId { get; set; }

    public Guid FormRecordId { get; set; }

    public Guid FormDefinitionId { get; set; }

    public Guid CreatedFormVersionId { get; set; }

    public Guid CreatedByUserId { get; set; }

    public Guid ContentChangeOperationId { get; set; }

    public long ContentRevision { get; set; }

    public string RecordStatus { get; set; } = "Draft";

    public DateTimeOffset? SubmittedOn { get; set; }

    public long RowRevision { get; set; }

    public byte[] RowSignature { get; set; } = [];

    public int RowSignatureKeyVersion { get; set; }

    public int RowSignatureSchemaVersion { get; set; }

    /// <summary>Local, untrusted: Staged | Committed | Aborted. The authority head decides visibility (invariant A8).</summary>
    public string CandidateState { get; set; } = "Staged";

    public DateTimeOffset StagedOn { get; set; }
}

/// <summary>
/// Typed string value row (CLAUDE.md §2.4), staged immutably per content operation. <c>FieldValueItemId</c> is the stable
/// logical item identity (Merkle FieldValue item id); the surrogate key identifies the physical staged copy.
/// </summary>
public sealed class FieldStringValue
{
    public Guid FieldStringValueId { get; set; }

    public Guid TenantId { get; set; }

    public Guid FormRecordId { get; set; }

    public Guid FieldDefinitionId { get; set; }

    public Guid? FormRecordGroupId { get; set; }

    public Guid FieldValueItemId { get; set; }

    public Guid ContentChangeOperationId { get; set; }

    public long ContentRevision { get; set; }

    public string StringValue { get; set; } = string.Empty;

    public long RowRevision { get; set; }

    public byte[] RowSignature { get; set; } = [];

    public int RowSignatureKeyVersion { get; set; }

    public int RowSignatureSchemaVersion { get; set; }

    public DateTimeOffset CreatedOn { get; set; }

    public Guid CreatedByUserId { get; set; }
}
