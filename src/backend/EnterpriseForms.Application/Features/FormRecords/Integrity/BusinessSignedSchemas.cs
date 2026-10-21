using EnterpriseForms.Domain.Features.Documents;
using EnterpriseForms.Domain.Features.FormRecords;
using EnterpriseForms.Integrity.Canonical;

namespace EnterpriseForms.Application.Features.FormRecords.Integrity;

/// <summary>
/// Signed-column registry for the business tables (row-integrity-format.md §3.1, §7; CLAUDE.md §4L.1). Entries are
/// immutable per (entity_kind, schema_version); a new column requires a new schema version. The architecture tests
/// check that every protected table's EF columns are registered here (SEC-42 / acceptance 82).
/// </summary>
public static class BusinessSignedSchemas
{
    private static readonly RowSignedColumn[] ValueEnvelope =
    [
        new("content_change_operation_id", RowColumnType.Uuid, Nullable: false),
        new("field_definition_id", RowColumnType.Uuid, Nullable: false),
        new("field_value_item_id", RowColumnType.Uuid, Nullable: false),
        new("form_record_group_id", RowColumnType.Uuid, Nullable: true),
        new("form_record_id", RowColumnType.Uuid, Nullable: false),
    ];

    /// <summary>form_records v2: Stage A header snapshot (kept for historical verification of Stage A rows).</summary>
    public static readonly RowSchemaDefinition FormRecordsV2 = new("form_records", 2,
    [
        new RowSignedColumn("content_change_operation_id", RowColumnType.Uuid, Nullable: false),
        new RowSignedColumn("created_by_user_id", RowColumnType.Uuid, Nullable: false),
        new RowSignedColumn("created_form_version_id", RowColumnType.Uuid, Nullable: false),
        new RowSignedColumn("form_definition_id", RowColumnType.Uuid, Nullable: false),
        new RowSignedColumn("record_status", RowColumnType.Text, Nullable: false),
        new RowSignedColumn("submitted_on", RowColumnType.TimestampUtc, Nullable: true),
        new RowSignedColumn("tenant_id", RowColumnType.Uuid, Nullable: false),
    ]);

    /// <summary>form_records v3 (Stage B, B-04): v2 plus the parent/relationship binding of child records. Owner projection columns are deliberately NOT signed (authority-only).</summary>
    public static readonly RowSchemaDefinition FormRecordsV3 = new("form_records", 3,
    [
        new RowSignedColumn("content_change_operation_id", RowColumnType.Uuid, Nullable: false),
        new RowSignedColumn("created_by_user_id", RowColumnType.Uuid, Nullable: false),
        new RowSignedColumn("created_form_version_id", RowColumnType.Uuid, Nullable: false),
        new RowSignedColumn("form_definition_id", RowColumnType.Uuid, Nullable: false),
        new RowSignedColumn("form_relationship_definition_id", RowColumnType.Uuid, Nullable: true),
        new RowSignedColumn("parent_form_record_id", RowColumnType.Uuid, Nullable: true),
        new RowSignedColumn("record_status", RowColumnType.Text, Nullable: false),
        new RowSignedColumn("relationship_sort_order", RowColumnType.Int64, Nullable: true),
        new RowSignedColumn("submitted_on", RowColumnType.TimestampUtc, Nullable: true),
        new RowSignedColumn("tenant_id", RowColumnType.Uuid, Nullable: false),
    ]);

    public static readonly RowSchemaDefinition FieldStringValuesV1 = ValueSchema("field_string_values", new RowSignedColumn("string_value", RowColumnType.Text, Nullable: false));

    public static readonly RowSchemaDefinition FieldIntegerValuesV1 = ValueSchema("field_integer_values", new RowSignedColumn("integer_value", RowColumnType.Int64, Nullable: false));

    public static readonly RowSchemaDefinition FieldDecimalValuesV1 = ValueSchema("field_decimal_values", new RowSignedColumn("decimal_value", RowColumnType.Decimal, Nullable: false));

    public static readonly RowSchemaDefinition FieldDateValuesV1 = ValueSchema("field_date_values", new RowSignedColumn("date_value", RowColumnType.Date, Nullable: false));

    public static readonly RowSchemaDefinition FieldDateTimeValuesV1 = ValueSchema("field_date_time_values", new RowSignedColumn("date_time_value", RowColumnType.TimestampUtc, Nullable: false));

    public static readonly RowSchemaDefinition FieldGuidValuesV1 = ValueSchema("field_guid_values", new RowSignedColumn("guid_value", RowColumnType.Uuid, Nullable: false));

    public static readonly RowSchemaDefinition FieldBooleanValuesV1 = ValueSchema("field_boolean_values", new RowSignedColumn("boolean_value", RowColumnType.Boolean, Nullable: false));

    /// <summary>field_encrypted_values v1: the HMAC binds the exact ciphertext envelope, never a reconstructed plaintext (row-integrity-format.md §3.7).</summary>
    public static readonly RowSchemaDefinition FieldEncryptedValuesV1 = ValueSchema("field_encrypted_values",
        new RowSignedColumn("encrypted_logical_type", RowColumnType.Text, Nullable: false),
        new RowSignedColumn("encrypted_value_bytes", RowColumnType.Bytes, Nullable: false),
        new RowSignedColumn("encryption_algorithm_name", RowColumnType.Text, Nullable: false),
        new RowSignedColumn("encryption_auth_tag_bytes", RowColumnType.Bytes, Nullable: false),
        new RowSignedColumn("encryption_envelope_version", RowColumnType.Int32, Nullable: false),
        new RowSignedColumn("encryption_key_reference", RowColumnType.Text, Nullable: false),
        new RowSignedColumn("encryption_nonce_bytes", RowColumnType.Bytes, Nullable: false),
        new RowSignedColumn("wrapped_data_key_bytes", RowColumnType.Bytes, Nullable: false));

    public static readonly RowSchemaDefinition FieldOptionValuesV1 = ValueSchema("field_option_values", new RowSignedColumn("field_option_id", RowColumnType.Uuid, Nullable: false));

    /// <summary>file_attachment_versions v1: binds ciphertext object, both digests, detected type, scanner verdict and the AES-GCM envelope (MERKLE §4.2). Primary key = version row id; item id = attachment id.</summary>
    public static readonly RowSchemaDefinition FileAttachmentVersionsV1 = new("file_attachment_versions", 1,
    [
        new RowSignedColumn("ciphertext_sha256", RowColumnType.Bytes, Nullable: false),
        new RowSignedColumn("ciphertext_size_bytes", RowColumnType.Int64, Nullable: false),
        new RowSignedColumn("content_change_operation_id", RowColumnType.Uuid, Nullable: false),
        new RowSignedColumn("detected_content_type", RowColumnType.Text, Nullable: false),
        new RowSignedColumn("encryption_algorithm_name", RowColumnType.Text, Nullable: false),
        new RowSignedColumn("encryption_envelope_version", RowColumnType.Int32, Nullable: false),
        new RowSignedColumn("encryption_key_reference", RowColumnType.Text, Nullable: false),
        new RowSignedColumn("encryption_nonce_prefix", RowColumnType.Bytes, Nullable: false),
        new RowSignedColumn("encryption_segment_size_bytes", RowColumnType.Int32, Nullable: false),
        new RowSignedColumn("field_definition_id", RowColumnType.Uuid, Nullable: false),
        new RowSignedColumn("file_attachment_id", RowColumnType.Uuid, Nullable: false),
        new RowSignedColumn("form_record_id", RowColumnType.Uuid, Nullable: false),
        new RowSignedColumn("original_file_name", RowColumnType.Text, Nullable: false),
        new RowSignedColumn("plaintext_sha256", RowColumnType.Bytes, Nullable: false),
        new RowSignedColumn("plaintext_size_bytes", RowColumnType.Int64, Nullable: false),
        new RowSignedColumn("scan_signature_name", RowColumnType.Text, Nullable: true),
        new RowSignedColumn("scan_verdict", RowColumnType.Text, Nullable: false),
        new RowSignedColumn("scanned_on", RowColumnType.TimestampUtc, Nullable: false),
        new RowSignedColumn("scanner_name", RowColumnType.Text, Nullable: false),
        new RowSignedColumn("storage_object_key", RowColumnType.Text, Nullable: false),
        new RowSignedColumn("storage_provider_name", RowColumnType.Text, Nullable: false),
        new RowSignedColumn("tenant_id", RowColumnType.Uuid, Nullable: false),
        new RowSignedColumn("uploaded_by_user_id", RowColumnType.Uuid, Nullable: false),
        new RowSignedColumn("uploaded_on", RowColumnType.TimestampUtc, Nullable: false),
        new RowSignedColumn("version_number", RowColumnType.Int32, Nullable: false),
        new RowSignedColumn("wrapped_data_key", RowColumnType.Bytes, Nullable: false),
    ]);

    /// <summary>form_record_groups v1 (Phase C3): one occurrence of a repeating group; canonical pk = the stable occurrence id.</summary>
    public static readonly RowSchemaDefinition FormRecordGroupsV1 = new("form_record_groups", 1,
    [
        new RowSignedColumn("content_change_operation_id", RowColumnType.Uuid, Nullable: false),
        new RowSignedColumn("form_record_id", RowColumnType.Uuid, Nullable: false),
        new RowSignedColumn("group_definition_id", RowColumnType.Uuid, Nullable: false),
        new RowSignedColumn("group_sort_order", RowColumnType.Int64, Nullable: false),
        new RowSignedColumn("parent_form_record_group_id", RowColumnType.Uuid, Nullable: true),
        new RowSignedColumn("tenant_id", RowColumnType.Uuid, Nullable: false),
    ]);

    /// <summary>Current schema per entity kind (used for new candidates).</summary>
    public static IReadOnlyList<RowSchemaDefinition> All { get; } =
    [
        FormRecordsV2, FormRecordsV3, FieldStringValuesV1, FieldIntegerValuesV1, FieldDecimalValuesV1, FieldDateValuesV1, FieldDateTimeValuesV1, FieldGuidValuesV1, FieldBooleanValuesV1, FieldOptionValuesV1, FieldEncryptedValuesV1, FileAttachmentVersionsV1, FormRecordGroupsV1,
    ];

    public static int CurrentSchemaVersion(ISignedContentRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        return row switch
        {
            FormRecordRevision => FormRecordsV3.SchemaVersion,
            FieldStringValue => FieldStringValuesV1.SchemaVersion,
            FieldIntegerValue => FieldIntegerValuesV1.SchemaVersion,
            FieldDecimalValue => FieldDecimalValuesV1.SchemaVersion,
            FieldDateValue => FieldDateValuesV1.SchemaVersion,
            FieldDateTimeValue => FieldDateTimeValuesV1.SchemaVersion,
            FieldGuidValue => FieldGuidValuesV1.SchemaVersion,
            FieldBooleanValue => FieldBooleanValuesV1.SchemaVersion,
            FieldEncryptedValue => FieldEncryptedValuesV1.SchemaVersion,
            FieldOptionValue => FieldOptionValuesV1.SchemaVersion,
            FileAttachmentVersion => FileAttachmentVersionsV1.SchemaVersion,
            FormRecordGroupRevision => FormRecordGroupsV1.SchemaVersion,
            _ => throw new ArgumentOutOfRangeException(nameof(row), row.GetType().Name, "unregistered signed row type"),
        };
    }

    /// <summary>Canonical bytes for any registered row exactly as stored (schema version taken from the row's own envelope).</summary>
    public static byte[] Encode(ISignedContentRow row, uint keyVersion)
    {
        ArgumentNullException.ThrowIfNull(row);
        var context = new RowSigningContext(keyVersion, row.TenantId, row.RowPrimaryKey, (ulong)row.RowRevision);
        return row switch
        {
            FormRecordRevision header => EncodeHeader(header, context),
            FieldStringValue v => RowCanonicalizerV1.Encode(FieldStringValuesV1, context, ValueColumns(v, "string_value", RowValue.Text(v.StringValue))),
            FieldIntegerValue v => RowCanonicalizerV1.Encode(FieldIntegerValuesV1, context, ValueColumns(v, "integer_value", RowValue.Int64(v.IntegerValue))),
            FieldDecimalValue v => RowCanonicalizerV1.Encode(FieldDecimalValuesV1, context, ValueColumns(v, "decimal_value", RowValue.Decimal(v.DecimalValue))),
            FieldDateValue v => RowCanonicalizerV1.Encode(FieldDateValuesV1, context, ValueColumns(v, "date_value", RowValue.Date(v.DateValue))),
            FieldDateTimeValue v => RowCanonicalizerV1.Encode(FieldDateTimeValuesV1, context, ValueColumns(v, "date_time_value", RowValue.Timestamp(v.DateTimeValue))),
            FieldGuidValue v => RowCanonicalizerV1.Encode(FieldGuidValuesV1, context, ValueColumns(v, "guid_value", RowValue.Uuid(v.GuidValue))),
            FieldBooleanValue v => RowCanonicalizerV1.Encode(FieldBooleanValuesV1, context, ValueColumns(v, "boolean_value", RowValue.Boolean(v.BooleanValue))),
            FieldEncryptedValue v => RowCanonicalizerV1.Encode(FieldEncryptedValuesV1, context, EncryptedColumns(v)),
            FieldOptionValue v => RowCanonicalizerV1.Encode(FieldOptionValuesV1, context, ValueColumns(v, "field_option_id", RowValue.Uuid(v.FieldOptionId))),
            FileAttachmentVersion v => RowCanonicalizerV1.Encode(FileAttachmentVersionsV1, context, AttachmentColumns(v)),
            FormRecordGroupRevision g => RowCanonicalizerV1.Encode(FormRecordGroupsV1, context, new Dictionary<string, RowValue?>(StringComparer.Ordinal)
            {
                ["content_change_operation_id"] = RowValue.Uuid(g.ContentChangeOperationId),
                ["form_record_id"] = RowValue.Uuid(g.FormRecordId),
                ["group_definition_id"] = RowValue.Uuid(g.GroupDefinitionId),
                ["group_sort_order"] = RowValue.Int64(g.GroupSortOrder),
                ["parent_form_record_group_id"] = g.ParentFormRecordGroupId is { } parent ? RowValue.Uuid(parent) : null,
                ["tenant_id"] = RowValue.Uuid(g.TenantId),
            }),
            _ => throw new ArgumentOutOfRangeException(nameof(row), row.GetType().Name, "unregistered signed row type"),
        };
    }

    private static byte[] EncodeHeader(FormRecordRevision header, RowSigningContext context)
    {
        var values = new Dictionary<string, RowValue?>(StringComparer.Ordinal)
        {
            ["content_change_operation_id"] = RowValue.Uuid(header.ContentChangeOperationId),
            ["created_by_user_id"] = RowValue.Uuid(header.CreatedByUserId),
            ["created_form_version_id"] = RowValue.Uuid(header.CreatedFormVersionId),
            ["form_definition_id"] = RowValue.Uuid(header.FormDefinitionId),
            ["record_status"] = RowValue.Text(header.RecordStatus),
            ["submitted_on"] = header.SubmittedOn is { } submitted ? RowValue.Timestamp(submitted) : null,
            ["tenant_id"] = RowValue.Uuid(header.TenantId),
        };
        if (header.RowSignatureSchemaVersion == FormRecordsV2.SchemaVersion)
        {
            return RowCanonicalizerV1.Encode(FormRecordsV2, context, values);
        }

        values["form_relationship_definition_id"] = header.FormRelationshipDefinitionId is { } rel ? RowValue.Uuid(rel) : null;
        values["parent_form_record_id"] = header.ParentFormRecordId is { } parent ? RowValue.Uuid(parent) : null;
        values["relationship_sort_order"] = header.RelationshipSortOrder is { } order ? RowValue.Int64(order) : null;
        return RowCanonicalizerV1.Encode(FormRecordsV3, context, values);
    }

    private static Dictionary<string, RowValue?> EncryptedColumns(FieldEncryptedValue v)
    {
        var columns = ValueColumns(v, "encrypted_value_bytes", RowValue.Bytes(v.EncryptedValueBytes));
        columns["encrypted_logical_type"] = RowValue.Text(v.EncryptedLogicalType);
        columns["encryption_algorithm_name"] = RowValue.Text(v.EncryptionAlgorithmName);
        columns["encryption_auth_tag_bytes"] = RowValue.Bytes(v.EncryptionAuthTagBytes);
        columns["encryption_envelope_version"] = RowValue.Int32(v.EncryptionEnvelopeVersion);
        columns["encryption_key_reference"] = RowValue.Text(v.EncryptionKeyReference);
        columns["encryption_nonce_bytes"] = RowValue.Bytes(v.EncryptionNonceBytes);
        columns["wrapped_data_key_bytes"] = RowValue.Bytes(v.WrappedDataKeyBytes);
        return columns;
    }

    private static Dictionary<string, RowValue?> ValueColumns(FieldValueRowBase row, string valueColumn, RowValue value) => new(StringComparer.Ordinal)
    {
        ["content_change_operation_id"] = RowValue.Uuid(row.ContentChangeOperationId),
        ["field_definition_id"] = RowValue.Uuid(row.FieldDefinitionId),
        ["field_value_item_id"] = RowValue.Uuid(row.FieldValueItemId),
        ["form_record_group_id"] = row.FormRecordGroupId is { } group ? RowValue.Uuid(group) : null,
        ["form_record_id"] = RowValue.Uuid(row.FormRecordId),
        ["tenant_id"] = RowValue.Uuid(row.TenantId),
        [valueColumn] = value,
    };

    private static Dictionary<string, RowValue?> AttachmentColumns(FileAttachmentVersion v) => new(StringComparer.Ordinal)
    {
        ["ciphertext_sha256"] = RowValue.Bytes(v.CiphertextSha256),
        ["ciphertext_size_bytes"] = RowValue.Int64(v.CiphertextSizeBytes),
        ["content_change_operation_id"] = RowValue.Uuid(v.ContentChangeOperationId),
        ["detected_content_type"] = RowValue.Text(v.DetectedContentType),
        ["encryption_algorithm_name"] = RowValue.Text(v.EncryptionAlgorithmName),
        ["encryption_envelope_version"] = RowValue.Int32(v.EncryptionEnvelopeVersion),
        ["encryption_key_reference"] = RowValue.Text(v.EncryptionKeyReference),
        ["encryption_nonce_prefix"] = RowValue.Bytes(v.EncryptionNoncePrefix),
        ["encryption_segment_size_bytes"] = RowValue.Int32(v.EncryptionSegmentSizeBytes),
        ["field_definition_id"] = RowValue.Uuid(v.FieldDefinitionId),
        ["file_attachment_id"] = RowValue.Uuid(v.FileAttachmentId),
        ["form_record_id"] = RowValue.Uuid(v.FormRecordId),
        ["original_file_name"] = RowValue.Text(v.OriginalFileName),
        ["plaintext_sha256"] = RowValue.Bytes(v.PlaintextSha256),
        ["plaintext_size_bytes"] = RowValue.Int64(v.PlaintextSizeBytes),
        ["scan_signature_name"] = v.ScanSignatureName is { } signature ? RowValue.Text(signature) : null,
        ["scan_verdict"] = RowValue.Text(v.ScanVerdict),
        ["scanned_on"] = RowValue.Timestamp(v.ScannedOn),
        ["scanner_name"] = RowValue.Text(v.ScannerName),
        ["storage_object_key"] = RowValue.Text(v.StorageObjectKey),
        ["storage_provider_name"] = RowValue.Text(v.StorageProviderName),
        ["tenant_id"] = RowValue.Uuid(v.TenantId),
        ["uploaded_by_user_id"] = RowValue.Uuid(v.UploadedByUserId),
        ["uploaded_on"] = RowValue.Timestamp(v.UploadedOn),
        ["version_number"] = RowValue.Int32(v.VersionNumber),
        ["wrapped_data_key"] = RowValue.Bytes(v.WrappedDataKey),
    };

    private static RowSchemaDefinition ValueSchema(string entityKind, params RowSignedColumn[] valueColumns)
    {
        var columns = ValueEnvelope.Concat(valueColumns).Append(new RowSignedColumn("tenant_id", RowColumnType.Uuid, Nullable: false)).OrderBy(c => c.ColumnName, StringComparer.Ordinal).ToArray();
        return new RowSchemaDefinition(entityKind, 1, columns);
    }
}
