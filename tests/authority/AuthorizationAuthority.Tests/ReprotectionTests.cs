using AuthorizationAuthority.Application.Common;
using AuthorizationAuthority.Application.Features.Content;
using AuthorizationAuthority.Contracts.V1;
using AuthorizationAuthority.Domain.Features.Content;
using EnterpriseForms.Integrity.Canonical;
using EnterpriseForms.Integrity.Merkle;
using Xunit;

namespace AuthorizationAuthority.Tests;

/// <summary>
/// Reprotection is decided from canonical bytes alone (B-28): the only accepted difference between the verified current
/// snapshot and the candidate is plaintext rows of the named field becoming encrypted rows of the same items. Everything
/// else — other fields, the header, attachments, added or removed items, a remaining plaintext row — is refused.
/// </summary>
public sealed class ReprotectionTests
{
    private static readonly Guid Org = Guid.CreateVersion7();
    private static readonly Guid Record = Guid.CreateVersion7();
    private static readonly Guid Field = Guid.CreateVersion7();
    private static readonly Guid OtherField = Guid.CreateVersion7();
    private static readonly Guid OpA = Guid.CreateVersion7();
    private static readonly Guid OpB = Guid.CreateVersion7();

    private static readonly RowSchemaDefinition Header = new("form_records", 3,
    [
        new("content_change_operation_id", RowColumnType.Uuid, false), new("created_by_user_id", RowColumnType.Uuid, false), new("created_form_version_id", RowColumnType.Uuid, false),
        new("form_definition_id", RowColumnType.Uuid, false), new("form_relationship_definition_id", RowColumnType.Uuid, true), new("parent_form_record_id", RowColumnType.Uuid, true),
        new("record_status", RowColumnType.Text, false), new("relationship_sort_order", RowColumnType.Int64, true), new("submitted_on", RowColumnType.TimestampUtc, true), new("tenant_id", RowColumnType.Uuid, false),
    ]);

    private static readonly RowSchemaDefinition StringValues = new("field_string_values", 1,
    [
        new("content_change_operation_id", RowColumnType.Uuid, false), new("field_definition_id", RowColumnType.Uuid, false), new("field_value_item_id", RowColumnType.Uuid, false),
        new("form_record_group_id", RowColumnType.Uuid, true), new("form_record_id", RowColumnType.Uuid, false), new("string_value", RowColumnType.Text, false), new("tenant_id", RowColumnType.Uuid, false),
    ]);

    private static readonly RowSchemaDefinition EncryptedValues = new("field_encrypted_values", 1,
    [
        new("content_change_operation_id", RowColumnType.Uuid, false), new("encrypted_logical_type", RowColumnType.Text, false), new("encrypted_value_bytes", RowColumnType.Bytes, false),
        new("encryption_algorithm_name", RowColumnType.Text, false), new("encryption_auth_tag_bytes", RowColumnType.Bytes, false), new("encryption_envelope_version", RowColumnType.Int32, false),
        new("encryption_key_reference", RowColumnType.Text, false), new("encryption_nonce_bytes", RowColumnType.Bytes, false), new("field_definition_id", RowColumnType.Uuid, false),
        new("field_value_item_id", RowColumnType.Uuid, false), new("form_record_group_id", RowColumnType.Uuid, true), new("form_record_id", RowColumnType.Uuid, false),
        new("tenant_id", RowColumnType.Uuid, false), new("wrapped_data_key_bytes", RowColumnType.Bytes, false),
    ]);

    private static readonly Guid Creator = Guid.CreateVersion7();
    private static readonly Guid Version = Guid.CreateVersion7();
    private static readonly Guid Form = Guid.CreateVersion7();

    private static ReprotectionRules.SnapshotItem HeaderItem(Guid operation, string status = "Draft")
    {
        var bytes = RowCanonicalizerV1.Encode(Header, new RowSigningContext(1, Org, Record, 1), new Dictionary<string, RowValue?>
        {
            ["content_change_operation_id"] = RowValue.Uuid(operation), ["created_by_user_id"] = RowValue.Uuid(Creator), ["created_form_version_id"] = RowValue.Uuid(Version), ["form_definition_id"] = RowValue.Uuid(Form),
            ["form_relationship_definition_id"] = null, ["parent_form_record_id"] = null, ["record_status"] = RowValue.Text(status), ["relationship_sort_order"] = null, ["submitted_on"] = null, ["tenant_id"] = RowValue.Uuid(Org),
        });
        return Item(ManifestItemKind.RecordHeader, Record, bytes);
    }

    private static ReprotectionRules.SnapshotItem Plain(Guid operation, Guid field, Guid item, string value, Guid? org = null)
    {
        // Every staged copy has its own surrogate row id (the framing primary key) and the candidate's row revision: neither may matter.
        var bytes = RowCanonicalizerV1.Encode(StringValues, new RowSigningContext(1, org ?? Org, Guid.CreateVersion7(), operation == OpA ? 1u : 2u), new Dictionary<string, RowValue?>
        {
            ["content_change_operation_id"] = RowValue.Uuid(operation), ["field_definition_id"] = RowValue.Uuid(field), ["field_value_item_id"] = RowValue.Uuid(item), ["form_record_group_id"] = null, ["form_record_id"] = RowValue.Uuid(Record), ["string_value"] = RowValue.Text(value), ["tenant_id"] = RowValue.Uuid(org ?? Org),
        });
        return Item(ManifestItemKind.FieldValue, item, bytes);
    }

    private static ReprotectionRules.SnapshotItem Sealed(Guid operation, Guid field, Guid item, Guid? recordId = null, Guid? group = null)
    {
        var bytes = RowCanonicalizerV1.Encode(EncryptedValues, new RowSigningContext(1, Org, Guid.CreateVersion7(), 2), new Dictionary<string, RowValue?>
        {
            ["content_change_operation_id"] = RowValue.Uuid(operation), ["encrypted_logical_type"] = RowValue.Text("String"), ["encrypted_value_bytes"] = RowValue.Bytes(new byte[] { 9, 9, 9 }), ["encryption_algorithm_name"] = RowValue.Text("AES-256-GCM"),
            ["encryption_auth_tag_bytes"] = RowValue.Bytes(new byte[16]), ["encryption_envelope_version"] = RowValue.Int32(1), ["encryption_key_reference"] = RowValue.Text("local-dev-kek-v1"), ["encryption_nonce_bytes"] = RowValue.Bytes(new byte[12]),
            ["field_definition_id"] = RowValue.Uuid(field), ["field_value_item_id"] = RowValue.Uuid(item), ["form_record_group_id"] = group is null ? null : RowValue.Uuid(group.Value), ["form_record_id"] = RowValue.Uuid(recordId ?? Record), ["tenant_id"] = RowValue.Uuid(Org), ["wrapped_data_key_bytes"] = RowValue.Bytes(new byte[60]),
        });
        return Item(ManifestItemKind.FieldValue, item, bytes);
    }

    private static ReprotectionRules.SnapshotItem Item(ManifestItemKind kind, Guid id, byte[] bytes) =>
        new(kind, id, MerkleManifestV1.ItemKey((MerkleItemKind)(byte)kind, id), RowCanonicalDecoder.Decode(bytes), System.Security.Cryptography.SHA256.HashData(bytes));

    [Fact]
    public void DecoderRoundTripsEveryColumnAndExposesIdentityColumns()
    {
        var item = Guid.CreateVersion7();
        var plain = Plain(OpA, Field, item, "héllo wörld");
        Assert.Equal("field_string_values", plain.Document.Header.EntityKind);
        Assert.Equal(7u, plain.Document.Header.ColumnCount);
        Assert.Equal(Field, plain.Document.Column("field_definition_id")!.AsUuid());
        Assert.Equal("héllo wörld", plain.Document.Column("string_value")!.AsText());
        Assert.True(plain.Document.Column("form_record_group_id")!.IsNull);
        Assert.Throws<EnterpriseForms.Integrity.IntegrityFormatException>(() => RowCanonicalDecoder.Decode(Convert.FromBase64String(Convert.ToBase64String(new byte[] { 0x10, 0, 3, 65, 66, 67 }))));
    }

    [Fact]
    public void AcceptsExactlyThePlaintextToCiphertextReplacementOfTheField()
    {
        var item1 = Guid.CreateVersion7();
        var item2 = Guid.CreateVersion7();
        var other = Guid.CreateVersion7();
        var current = new[] { HeaderItem(OpA), Plain(OpA, Field, item1, "a"), Plain(OpA, Field, item2, "b"), Plain(OpA, OtherField, other, "keep") };
        var candidate = new[] { HeaderItem(OpB), Sealed(OpB, Field, item1), Sealed(OpB, Field, item2), Plain(OpB, OtherField, other, "keep") };

        var mutations = ReprotectionRules.Validate(current, candidate, Field);
        Assert.Equal(ContentMutationKinds.Reprotect, mutations[Convert.ToHexStringLower(candidate[1].Key)]);
        Assert.Equal(ContentMutationKinds.Reprotect, mutations[Convert.ToHexStringLower(candidate[2].Key)]);
        Assert.Equal(ContentMutationKinds.Unchanged, mutations[Convert.ToHexStringLower(candidate[0].Key)]);
        Assert.Equal(ContentMutationKinds.Unchanged, mutations[Convert.ToHexStringLower(candidate[3].Key)]);
    }

    [Fact]
    public void RefusesEveryOtherChange()
    {
        var item = Guid.CreateVersion7();
        var other = Guid.CreateVersion7();
        var current = new[] { HeaderItem(OpA), Plain(OpA, Field, item, "a"), Plain(OpA, OtherField, other, "keep") };

        static string Code(Func<Dictionary<string, string>> act) => Assert.Throws<AuthorityException>(act).Message.Split(':')[0];

        // Another field's value changed alongside.
        Assert.Equal("REPROTECT_ROW_CHANGED", Code(() => ReprotectionRules.Validate(current, [HeaderItem(OpB), Sealed(OpB, Field, item), Plain(OpB, OtherField, other, "changed")], Field)));
        // Header status changed alongside.
        Assert.Equal("REPROTECT_ROW_CHANGED", Code(() => ReprotectionRules.Validate(current, [HeaderItem(OpB, "Submitted"), Sealed(OpB, Field, item), Plain(OpB, OtherField, other, "keep")], Field)));
        // A value added.
        Assert.Equal("REPROTECT_ITEM_ADDED", Code(() => ReprotectionRules.Validate(current, [HeaderItem(OpB), Sealed(OpB, Field, item), Plain(OpB, OtherField, other, "keep"), Plain(OpB, OtherField, Guid.CreateVersion7(), "new")], Field)));
        // A value removed.
        Assert.Equal("REPROTECT_ITEM_REMOVED", Code(() => ReprotectionRules.Validate(current, [HeaderItem(OpB), Sealed(OpB, Field, item)], Field)));
        // The field's plaintext row kept (nothing re-protected) is incomplete; a snapshot without any row of the field has nothing to do.
        Assert.Equal("REPROTECT_INCOMPLETE", Code(() => ReprotectionRules.Validate(current, [HeaderItem(OpB), Plain(OpB, Field, item, "a"), Plain(OpB, OtherField, other, "keep")], Field)));
        var none = new[] { HeaderItem(OpA), Plain(OpA, OtherField, other, "keep") };
        Assert.Equal("REPROTECT_NOTHING_TO_DO", Code(() => ReprotectionRules.Validate(none, [HeaderItem(OpB), Plain(OpB, OtherField, other, "keep")], Field)));
        // Encrypting a different field than the command names.
        Assert.Equal("REPROTECT_ROW_CHANGED", Code(() => ReprotectionRules.Validate(current, [HeaderItem(OpB), Plain(OpB, Field, item, "a"), Sealed(OpB, OtherField, other)], Field)));
        // The encrypted row rebinds the item to another record.
        Assert.Equal("REPROTECT_ROW_CHANGED", Code(() => ReprotectionRules.Validate(current, [HeaderItem(OpB), Sealed(OpB, Field, item, recordId: Guid.CreateVersion7()), Plain(OpB, OtherField, other, "keep")], Field)));
        // One of two plaintext rows of the field left behind.
        var item2 = Guid.CreateVersion7();
        var two = new[] { HeaderItem(OpA), Plain(OpA, Field, item, "a"), Plain(OpA, Field, item2, "b") };
        Assert.Equal("REPROTECT_INCOMPLETE", Code(() => ReprotectionRules.Validate(two, [HeaderItem(OpB), Sealed(OpB, Field, item), Plain(OpB, Field, item2, "b")], Field)));
    }
}
