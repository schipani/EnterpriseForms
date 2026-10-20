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

    /// <summary>
    /// Local projection of the operation the authority accepted as this record's head at <see cref="RecordRevision"/>.
    /// Staged candidates are insert-only and stay in the business tables even when the authority rejects them (a
    /// superseded reprotection, a revoked permission), so "the highest staged revision" is not the record's state; local
    /// estimates (legacy plaintext/encrypted counts) read the field rows of this operation instead. It is an untrusted
    /// hint like every business row — protected reads and migration post-checks still verify against the authority head.
    /// </summary>
    public Guid? AcceptedContentChangeOperationId { get; set; }

    /// <summary>Records an authority-accepted head; never moves the projection backwards when commits finish out of order.</summary>
    public bool RecordAcceptedContent(long contentRevision, Guid operationId)
    {
        if (contentRevision < RecordRevision || (contentRevision == RecordRevision && AcceptedContentChangeOperationId is not null))
        {
            return false;
        }

        RecordRevision = contentRevision;
        AcceptedContentChangeOperationId = operationId;
        return true;
    }

    /// <summary>Local projection of the authority parent edge (child records); the signed header binds the link (schema v3).</summary>
    public Guid? ParentFormRecordId { get; set; }

    public Guid? FormRelationshipDefinitionId { get; set; }
}

/// <summary>
/// Immutable staged header candidate keyed by (record, operation) — the RecordHeader Merkle item (MERKLE §2).
/// Signed under RowHmac v1 entity kind <c>form_records</c>, schema version 3 (v2 + parent/relationship binding, B-04),
/// primary key = form_record_id.
/// </summary>
public sealed class FormRecordRevision : ISignedContentRow
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

    /// <summary>Parent record of a child record (signed; null for top-level records).</summary>
    public Guid? ParentFormRecordId { get; set; }

    public Guid? FormRelationshipDefinitionId { get; set; }

    public long? RelationshipSortOrder { get; set; }

    public long RowRevision { get; set; }

    public byte[] RowSignature { get; set; } = [];

    public int RowSignatureKeyVersion { get; set; }

    public int RowSignatureSchemaVersion { get; set; }

    /// <summary>Local, untrusted: Staged | Committed | Aborted. The authority head decides visibility (invariant A8).</summary>
    public string CandidateState { get; set; } = "Staged";

    public DateTimeOffset StagedOn { get; set; }

    public string EntityKind => "form_records";

    public byte ItemKindCode => ContentItemKindCodes.RecordHeader;

    public Guid ItemId => FormRecordId;

    public Guid RowPrimaryKey => FormRecordId;
}

/// <summary>Common shape of the typed value tables (CLAUDE.md §2.4): one logical item identity, staged immutably per operation.</summary>
public abstract class FieldValueRowBase : ISignedContentRow
{
    public Guid TenantId { get; set; }

    public Guid FormRecordId { get; set; }

    public Guid FieldDefinitionId { get; set; }

    public Guid? FormRecordGroupId { get; set; }

    public Guid FieldValueItemId { get; set; }

    public Guid ContentChangeOperationId { get; set; }

    public long ContentRevision { get; set; }

    public long RowRevision { get; set; }

    public byte[] RowSignature { get; set; } = [];

    public int RowSignatureKeyVersion { get; set; }

    public int RowSignatureSchemaVersion { get; set; }

    public DateTimeOffset CreatedOn { get; set; }

    public Guid CreatedByUserId { get; set; }

    public abstract string EntityKind { get; }

    public virtual byte ItemKindCode => ContentItemKindCodes.FieldValue;

    public Guid ItemId => FieldValueItemId;

    public abstract Guid RowPrimaryKey { get; }
}

/// <summary>Typed string value row; <c>FieldValueItemId</c> is the stable logical item identity (Merkle FieldValue item id).</summary>
public sealed class FieldStringValue : FieldValueRowBase
{
    public Guid FieldStringValueId { get; set; }

    public string StringValue { get; set; } = string.Empty;

    public override string EntityKind => "field_string_values";

    public override Guid RowPrimaryKey => FieldStringValueId;
}

public sealed class FieldIntegerValue : FieldValueRowBase
{
    public Guid FieldIntegerValueId { get; set; }

    public long IntegerValue { get; set; }

    public override string EntityKind => "field_integer_values";

    public override Guid RowPrimaryKey => FieldIntegerValueId;
}

public sealed class FieldDecimalValue : FieldValueRowBase
{
    public Guid FieldDecimalValueId { get; set; }

    public decimal DecimalValue { get; set; }

    public override string EntityKind => "field_decimal_values";

    public override Guid RowPrimaryKey => FieldDecimalValueId;
}

public sealed class FieldDateValue : FieldValueRowBase
{
    public Guid FieldDateValueId { get; set; }

    public DateOnly DateValue { get; set; }

    public override string EntityKind => "field_date_values";

    public override Guid RowPrimaryKey => FieldDateValueId;
}

public sealed class FieldDateTimeValue : FieldValueRowBase
{
    public Guid FieldDateTimeValueId { get; set; }

    /// <summary>Exact instant, UTC, second precision (the canonical timestamp encoding is second-exact).</summary>
    public DateTimeOffset DateTimeValue { get; set; }

    public override string EntityKind => "field_date_time_values";

    public override Guid RowPrimaryKey => FieldDateTimeValueId;
}

public sealed class FieldGuidValue : FieldValueRowBase
{
    public Guid FieldGuidValueId { get; set; }

    public Guid GuidValue { get; set; }

    public override string EntityKind => "field_guid_values";

    public override Guid RowPrimaryKey => FieldGuidValueId;
}

/// <summary>
/// Application-encrypted value of any logical type (CLAUDE.md §4C.3): AES-256-GCM ciphertext of the canonical wire string,
/// nonce, tag, the per-value DEK wrapped by the organization KEK, algorithm/key/envelope identifiers and the logical
/// type. No plaintext copy exists in any typed table; the row HMAC binds the whole envelope.
/// </summary>
public sealed class FieldEncryptedValue : FieldValueRowBase
{
    public Guid FieldEncryptedValueId { get; set; }

    public byte[] EncryptedValueBytes { get; set; } = [];

    public byte[] EncryptionNonceBytes { get; set; } = [];

    public byte[] EncryptionAuthTagBytes { get; set; } = [];

    public byte[] WrappedDataKeyBytes { get; set; } = [];

    public string EncryptionAlgorithmName { get; set; } = string.Empty;

    public string EncryptionKeyReference { get; set; } = string.Empty;

    public int EncryptionEnvelopeVersion { get; set; }

    /// <summary>The data type the plaintext was serialized as (String, Integer, Date, …); needed to render and validate after decryption.</summary>
    public string EncryptedLogicalType { get; set; } = string.Empty;

    public override string EntityKind => "field_encrypted_values";

    public override Guid RowPrimaryKey => FieldEncryptedValueId;
}

public sealed class FieldBooleanValue : FieldValueRowBase
{
    public Guid FieldBooleanValueId { get; set; }

    public bool BooleanValue { get; set; }

    public override string EntityKind => "field_boolean_values";

    public override Guid RowPrimaryKey => FieldBooleanValueId;
}

/// <summary>Option selection row (single-select in Stage B): references the stable option id, never the label (CLAUDE.md §2.2).</summary>
public sealed class FieldOptionValue : FieldValueRowBase
{
    public Guid FieldOptionValueId { get; set; }

    public Guid FieldOptionId { get; set; }

    public override string EntityKind => "field_option_values";

    public override byte ItemKindCode => ContentItemKindCodes.FieldSelection;

    public override Guid RowPrimaryKey => FieldOptionValueId;
}
