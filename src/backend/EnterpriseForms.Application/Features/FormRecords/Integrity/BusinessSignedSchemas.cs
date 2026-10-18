using EnterpriseForms.Domain.Features.FormRecords;
using EnterpriseForms.Integrity.Canonical;

namespace EnterpriseForms.Application.Features.FormRecords.Integrity;

/// <summary>
/// Signed-column registry for Stage A production tables (row-integrity-format.md §3.1, §7; CLAUDE.md §4L.1).
/// Entries are immutable per (entity_kind, schema_version); a new column requires a new schema version.
/// The architecture tests check that every protected table's EF columns are registered here (SEC-42 / acceptance 82).
/// </summary>
public static class BusinessSignedSchemas
{
    /// <summary>form_records v2: Stage A header snapshot. Owner projection columns are deliberately NOT signed (authority-only).</summary>
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

    public static readonly RowSchemaDefinition FieldStringValuesV1 = new("field_string_values", 1,
    [
        new RowSignedColumn("content_change_operation_id", RowColumnType.Uuid, Nullable: false),
        new RowSignedColumn("field_definition_id", RowColumnType.Uuid, Nullable: false),
        new RowSignedColumn("field_value_item_id", RowColumnType.Uuid, Nullable: false),
        new RowSignedColumn("form_record_group_id", RowColumnType.Uuid, Nullable: true),
        new RowSignedColumn("form_record_id", RowColumnType.Uuid, Nullable: false),
        new RowSignedColumn("string_value", RowColumnType.Text, Nullable: false),
        new RowSignedColumn("tenant_id", RowColumnType.Uuid, Nullable: false),
    ]);

    public static IReadOnlyList<RowSchemaDefinition> All { get; } = [FormRecordsV2, FieldStringValuesV1];

    /// <summary>Canonical bytes for a header candidate. Primary key = form_record_id; row_revision = content_revision.</summary>
    public static byte[] EncodeHeader(FormRecordRevision header, uint keyVersion)
    {
        ArgumentNullException.ThrowIfNull(header);
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
        return RowCanonicalizerV1.Encode(FormRecordsV2, new RowSigningContext(keyVersion, header.TenantId, header.FormRecordId, (ulong)header.RowRevision), values);
    }

    /// <summary>Canonical bytes for a string value candidate. Primary key = the physical staged row id; row_revision = content_revision.</summary>
    public static byte[] EncodeStringValue(FieldStringValue value, uint keyVersion)
    {
        ArgumentNullException.ThrowIfNull(value);
        var values = new Dictionary<string, RowValue?>(StringComparer.Ordinal)
        {
            ["content_change_operation_id"] = RowValue.Uuid(value.ContentChangeOperationId),
            ["field_definition_id"] = RowValue.Uuid(value.FieldDefinitionId),
            ["field_value_item_id"] = RowValue.Uuid(value.FieldValueItemId),
            ["form_record_group_id"] = value.FormRecordGroupId is { } group ? RowValue.Uuid(group) : null,
            ["form_record_id"] = RowValue.Uuid(value.FormRecordId),
            ["string_value"] = RowValue.Text(value.StringValue),
            ["tenant_id"] = RowValue.Uuid(value.TenantId),
        };
        return RowCanonicalizerV1.Encode(FieldStringValuesV1, new RowSigningContext(keyVersion, value.TenantId, value.FieldStringValueId, (ulong)value.RowRevision), values);
    }
}
