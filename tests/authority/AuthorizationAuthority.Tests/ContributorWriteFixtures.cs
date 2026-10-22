using System.Security.Cryptography;
using AuthorizationAuthority.Application.Features.Content;
using AuthorizationAuthority.Contracts.V1;
using EnterpriseForms.Integrity.Canonical;
using EnterpriseForms.Integrity.Merkle;

namespace AuthorizationAuthority.Tests;

/// <summary>
/// Canonical rows shaped exactly like the business registry (BusinessSignedSchemas; the architecture test keeps the
/// Authority rule table and that registry in step). A <see cref="Row"/> is a logical description; <see cref="Encode"/>
/// stages it under an operation/revision with a storage key, as the business stager does.
/// </summary>
internal static class ContributorWriteFixtures
{
    public static readonly RowSchemaDefinition HeaderV3 = new("form_records", 3,
    [
        new("content_change_operation_id", RowColumnType.Uuid, false), new("created_by_user_id", RowColumnType.Uuid, false), new("created_form_version_id", RowColumnType.Uuid, false),
        new("form_definition_id", RowColumnType.Uuid, false), new("form_relationship_definition_id", RowColumnType.Uuid, true), new("parent_form_record_id", RowColumnType.Uuid, true),
        new("record_status", RowColumnType.Text, false), new("relationship_sort_order", RowColumnType.Int64, true), new("submitted_on", RowColumnType.TimestampUtc, true), new("tenant_id", RowColumnType.Uuid, false),
    ]);

    public static readonly RowSchemaDefinition HeaderV2 = new("form_records", 2,
    [
        new("content_change_operation_id", RowColumnType.Uuid, false), new("created_by_user_id", RowColumnType.Uuid, false), new("created_form_version_id", RowColumnType.Uuid, false),
        new("form_definition_id", RowColumnType.Uuid, false), new("record_status", RowColumnType.Text, false), new("submitted_on", RowColumnType.TimestampUtc, true), new("tenant_id", RowColumnType.Uuid, false),
    ]);

    public static RowSchemaDefinition Plain(string entityKind, string valueColumn, RowColumnType type) => new(entityKind, 1,
        new[]
        {
            new RowSignedColumn("content_change_operation_id", RowColumnType.Uuid, false), new RowSignedColumn("field_definition_id", RowColumnType.Uuid, false),
            new RowSignedColumn("field_value_item_id", RowColumnType.Uuid, false), new RowSignedColumn("form_record_group_id", RowColumnType.Uuid, true),
            new RowSignedColumn("form_record_id", RowColumnType.Uuid, false), new RowSignedColumn("tenant_id", RowColumnType.Uuid, false), new RowSignedColumn(valueColumn, type, false),
        }.OrderBy(c => c.ColumnName, StringComparer.Ordinal).ToList());

    public static readonly RowSchemaDefinition StringValues = Plain("field_string_values", "string_value", RowColumnType.Text);

    public static readonly RowSchemaDefinition IntegerValues = Plain("field_integer_values", "integer_value", RowColumnType.Int64);

    public static readonly RowSchemaDefinition OptionValues = Plain("field_option_values", "field_option_id", RowColumnType.Uuid);

    public static readonly RowSchemaDefinition EncryptedValues = new("field_encrypted_values", 1,
    [
        new("content_change_operation_id", RowColumnType.Uuid, false), new("encrypted_logical_type", RowColumnType.Text, false), new("encrypted_value_bytes", RowColumnType.Bytes, false),
        new("encryption_algorithm_name", RowColumnType.Text, false), new("encryption_auth_tag_bytes", RowColumnType.Bytes, false), new("encryption_envelope_version", RowColumnType.Int32, false),
        new("encryption_key_reference", RowColumnType.Text, false), new("encryption_nonce_bytes", RowColumnType.Bytes, false), new("field_definition_id", RowColumnType.Uuid, false),
        new("field_value_item_id", RowColumnType.Uuid, false), new("form_record_group_id", RowColumnType.Uuid, true), new("form_record_id", RowColumnType.Uuid, false),
        new("tenant_id", RowColumnType.Uuid, false), new("wrapped_data_key_bytes", RowColumnType.Bytes, false),
    ]);

    public static readonly RowSchemaDefinition Groups = new("form_record_groups", 1,
    [
        new("content_change_operation_id", RowColumnType.Uuid, false), new("form_record_id", RowColumnType.Uuid, false), new("group_definition_id", RowColumnType.Uuid, false),
        new("group_sort_order", RowColumnType.Int64, false), new("parent_form_record_group_id", RowColumnType.Uuid, true), new("tenant_id", RowColumnType.Uuid, false),
    ]);

    public static readonly RowSchemaDefinition Attachments = new("file_attachment_versions", 1,
    [
        new("ciphertext_sha256", RowColumnType.Bytes, false), new("ciphertext_size_bytes", RowColumnType.Int64, false), new("content_change_operation_id", RowColumnType.Uuid, false),
        new("detected_content_type", RowColumnType.Text, false), new("encryption_algorithm_name", RowColumnType.Text, false), new("encryption_envelope_version", RowColumnType.Int32, false),
        new("encryption_key_reference", RowColumnType.Text, false), new("encryption_nonce_prefix", RowColumnType.Bytes, false), new("encryption_segment_size_bytes", RowColumnType.Int32, false),
        new("field_definition_id", RowColumnType.Uuid, false), new("file_attachment_id", RowColumnType.Uuid, false), new("form_record_id", RowColumnType.Uuid, false),
        new("original_file_name", RowColumnType.Text, false), new("plaintext_sha256", RowColumnType.Bytes, false), new("plaintext_size_bytes", RowColumnType.Int64, false),
        new("scan_signature_name", RowColumnType.Text, true), new("scan_verdict", RowColumnType.Text, false), new("scanned_on", RowColumnType.TimestampUtc, false),
        new("scanner_name", RowColumnType.Text, false), new("storage_object_key", RowColumnType.Text, false), new("storage_provider_name", RowColumnType.Text, false),
        new("tenant_id", RowColumnType.Uuid, false), new("uploaded_by_user_id", RowColumnType.Uuid, false), new("uploaded_on", RowColumnType.TimestampUtc, false),
        new("version_number", RowColumnType.Int32, false), new("wrapped_data_key", RowColumnType.Bytes, false),
    ]);

    /// <summary>One logical row: kind, item id, schema, the non-envelope column values, and whether its primary key is the item id.</summary>
    public sealed record Row(ManifestItemKind Kind, Guid ItemId, RowSchemaDefinition Schema, Dictionary<string, RowValue?> Values, bool IdentityPrimaryKey)
    {
        public Row With(string column, RowValue? value)
        {
            var copy = new Dictionary<string, RowValue?>(Values, StringComparer.Ordinal) { [column] = value };
            return this with { Values = copy };
        }
    }

    public sealed record Staged(Row Row, Guid StorageKey, byte[] Bytes, ManifestItemKind Kind, Guid ItemId)
    {
        public byte[] Digest => SHA256.HashData(Bytes);

        public byte[] Key => MerkleManifestV1.ItemKey((MerkleItemKind)(byte)Kind, ItemId);

        public ContentDeltaRules.DecodedItem Decoded => new(Kind, ItemId, Key, RowCanonicalDecoder.Decode(Bytes), Digest);

        public string Base64 => Convert.ToBase64String(Bytes);
    }

    public static Staged Encode(Row row, Guid organizationId, Guid operation, long revision, uint keyVersion = 1, Guid? storageKey = null)
    {
        var values = new Dictionary<string, RowValue?>(row.Values, StringComparer.Ordinal) { ["content_change_operation_id"] = RowValue.Uuid(operation) };
        var primaryKey = row.IdentityPrimaryKey ? row.ItemId : storageKey ?? Guid.CreateVersion7();
        var bytes = RowCanonicalizerV1.Encode(row.Schema, new RowSigningContext(keyVersion, organizationId, primaryKey, (ulong)revision), values);
        return new Staged(row, primaryKey, bytes, row.Kind, row.ItemId);
    }

    public static Row Header(Guid org, Guid record, string status = "Draft", Guid? parent = null) => new(ManifestItemKind.RecordHeader, record, HeaderV3, new Dictionary<string, RowValue?>(StringComparer.Ordinal)
    {
        ["created_by_user_id"] = RowValue.Uuid(Ids.Creator), ["created_form_version_id"] = RowValue.Uuid(Ids.Version), ["form_definition_id"] = RowValue.Uuid(Ids.Form),
        ["form_relationship_definition_id"] = null, ["parent_form_record_id"] = parent is null ? null : RowValue.Uuid(parent.Value), ["record_status"] = RowValue.Text(status),
        ["relationship_sort_order"] = null, ["submitted_on"] = null, ["tenant_id"] = RowValue.Uuid(org),
    }, IdentityPrimaryKey: true);

    public static Row Text(Guid org, Guid record, Guid field, Guid item, string value, Guid? group = null) => new(ManifestItemKind.FieldValue, item, StringValues, new Dictionary<string, RowValue?>(StringComparer.Ordinal)
    {
        ["field_definition_id"] = RowValue.Uuid(field), ["field_value_item_id"] = RowValue.Uuid(item), ["form_record_group_id"] = group is null ? null : RowValue.Uuid(group.Value),
        ["form_record_id"] = RowValue.Uuid(record), ["tenant_id"] = RowValue.Uuid(org), ["string_value"] = RowValue.Text(value),
    }, IdentityPrimaryKey: false);

    public static Row Integer(Guid org, Guid record, Guid field, Guid item, long value) => new(ManifestItemKind.FieldValue, item, IntegerValues, new Dictionary<string, RowValue?>(StringComparer.Ordinal)
    {
        ["field_definition_id"] = RowValue.Uuid(field), ["field_value_item_id"] = RowValue.Uuid(item), ["form_record_group_id"] = null,
        ["form_record_id"] = RowValue.Uuid(record), ["tenant_id"] = RowValue.Uuid(org), ["integer_value"] = RowValue.Int64(value),
    }, IdentityPrimaryKey: false);

    public static Row Option(Guid org, Guid record, Guid field, Guid item, Guid option) => new(ManifestItemKind.FieldSelection, item, OptionValues, new Dictionary<string, RowValue?>(StringComparer.Ordinal)
    {
        ["field_definition_id"] = RowValue.Uuid(field), ["field_value_item_id"] = RowValue.Uuid(item), ["form_record_group_id"] = null,
        ["form_record_id"] = RowValue.Uuid(record), ["tenant_id"] = RowValue.Uuid(org), ["field_option_id"] = RowValue.Uuid(option),
    }, IdentityPrimaryKey: false);

    public static Row Sealed(Guid org, Guid record, Guid field, Guid item, byte seed, string logicalType = "String", string algorithm = "AES-256-GCM", int envelope = 1) => new(ManifestItemKind.FieldValue, item, EncryptedValues, new Dictionary<string, RowValue?>(StringComparer.Ordinal)
    {
        ["encrypted_logical_type"] = RowValue.Text(logicalType), ["encrypted_value_bytes"] = RowValue.Bytes(Fill(24, seed)), ["encryption_algorithm_name"] = RowValue.Text(algorithm),
        ["encryption_auth_tag_bytes"] = RowValue.Bytes(Fill(16, (byte)(seed + 1))), ["encryption_envelope_version"] = RowValue.Int32(envelope), ["encryption_key_reference"] = RowValue.Text("local-dev-kek-v1"),
        ["encryption_nonce_bytes"] = RowValue.Bytes(Fill(12, (byte)(seed + 2))), ["field_definition_id"] = RowValue.Uuid(field), ["field_value_item_id"] = RowValue.Uuid(item),
        ["form_record_group_id"] = null, ["form_record_id"] = RowValue.Uuid(record), ["tenant_id"] = RowValue.Uuid(org), ["wrapped_data_key_bytes"] = RowValue.Bytes(Fill(60, (byte)(seed + 3))),
    }, IdentityPrimaryKey: false);

    public static Row Group(Guid org, Guid record, Guid occurrence, long sortOrder) => new(ManifestItemKind.RepeatedGroup, occurrence, Groups, new Dictionary<string, RowValue?>(StringComparer.Ordinal)
    {
        ["form_record_id"] = RowValue.Uuid(record), ["group_definition_id"] = RowValue.Uuid(Ids.GroupDefinition), ["group_sort_order"] = RowValue.Int64(sortOrder),
        ["parent_form_record_group_id"] = null, ["tenant_id"] = RowValue.Uuid(org),
    }, IdentityPrimaryKey: true);

    public static Row Attachment(Guid org, Guid record, Guid attachment, string objectKey = "objects/one") => new(ManifestItemKind.Attachment, attachment, Attachments, new Dictionary<string, RowValue?>(StringComparer.Ordinal)
    {
        ["ciphertext_sha256"] = RowValue.Bytes(Fill(32, 7)), ["ciphertext_size_bytes"] = RowValue.Int64(1024), ["detected_content_type"] = RowValue.Text("application/pdf"),
        ["encryption_algorithm_name"] = RowValue.Text("AES-256-GCM"), ["encryption_envelope_version"] = RowValue.Int32(1), ["encryption_key_reference"] = RowValue.Text("local-dev-kek-v1"),
        ["encryption_nonce_prefix"] = RowValue.Bytes(Fill(8, 3)), ["encryption_segment_size_bytes"] = RowValue.Int32(1048576), ["field_definition_id"] = RowValue.Uuid(Ids.DocumentField),
        ["file_attachment_id"] = RowValue.Uuid(attachment), ["form_record_id"] = RowValue.Uuid(record), ["original_file_name"] = RowValue.Text("statement.pdf"),
        ["plaintext_sha256"] = RowValue.Bytes(Fill(32, 9)), ["plaintext_size_bytes"] = RowValue.Int64(1000), ["scan_signature_name"] = null, ["scan_verdict"] = RowValue.Text("Clean"),
        ["scanned_on"] = RowValue.Timestamp(new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero)), ["scanner_name"] = RowValue.Text("clamav"), ["storage_object_key"] = RowValue.Text(objectKey),
        ["storage_provider_name"] = RowValue.Text("azurite"), ["tenant_id"] = RowValue.Uuid(org), ["uploaded_by_user_id"] = RowValue.Uuid(Ids.Creator),
        ["uploaded_on"] = RowValue.Timestamp(new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero)), ["version_number"] = RowValue.Int32(1), ["wrapped_data_key"] = RowValue.Bytes(Fill(60, 5)),
    }, IdentityPrimaryKey: false);

    public static byte[] Fill(int length, byte value) => Enumerable.Repeat(value, length).ToArray();

    internal static class Ids
    {
        public static readonly Guid Creator = Guid.CreateVersion7();
        public static readonly Guid Version = Guid.CreateVersion7();
        public static readonly Guid Form = Guid.CreateVersion7();
        public static readonly Guid GroupDefinition = Guid.CreateVersion7();
        public static readonly Guid DocumentField = Guid.CreateVersion7();
    }
}
