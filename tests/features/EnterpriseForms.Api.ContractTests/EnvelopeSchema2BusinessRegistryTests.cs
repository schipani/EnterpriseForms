using System.Globalization;
using System.Text.Json;
using EnterpriseForms.Application.Features.FormRecords.Integrity;
using EnterpriseForms.Domain.Features.Documents;
using EnterpriseForms.Domain.Features.FormRecords;
using EnterpriseForms.Integrity;
using EnterpriseForms.Integrity.Canonical;
using Xunit;

namespace EnterpriseForms.Api.ContractTests;

/// <summary>
/// E3-M2b-2 signed schema 2 groundwork (contract §4, §23): the business registry equals the cross-language vector schemas, the
/// business encoder reproduces every vector case from domain rows (M2-SIG-01..03, -12), the schema follows the envelope
/// version (S-1, M2-SIG-08), encoding follows the stored schema with no fallback (S-2), a schema/envelope disagreement is
/// refused before anything is sent (S-10, M2-SIG-10), and a schema-2 row without its metadata cannot be encoded (M2-SIG-05).
/// No product path writes envelope 2; these rows are fixtures.
/// </summary>
public sealed class EnvelopeSchema2BusinessRegistryTests
{
    private static readonly JsonDocument Vectors = JsonDocument.Parse(File.ReadAllText(Path.Combine(RepositoryPaths.Root, "trust_authority", "row-integrity-v1-envelope-schema2.test-vectors.json")));
    private static readonly byte[] TestKey = Convert.FromHexString(Vectors.RootElement.GetProperty("testHmacKeyHex").GetString()!);

    public static TheoryData<string> CaseIds()
    {
        var data = new TheoryData<string>();
        foreach (var c in Vectors.RootElement.GetProperty("cases").EnumerateArray())
        {
            data.Add(c.GetProperty("caseId").GetString()!);
        }

        return data;
    }

    [Fact]
    public void TheBusinessRegistryEqualsTheCrossLanguageVectorSchemas()
    {
        var registry = new Dictionary<(string, int), RowSchemaDefinition>
        {
            [("field_encrypted_values", 1)] = BusinessSignedSchemas.FieldEncryptedValuesV1,
            [("field_encrypted_values", 2)] = BusinessSignedSchemas.FieldEncryptedValuesV2,
            [("file_attachment_versions", 1)] = BusinessSignedSchemas.FileAttachmentVersionsV1,
            [("file_attachment_versions", 2)] = BusinessSignedSchemas.FileAttachmentVersionsV2,
        };
        foreach (var s in Vectors.RootElement.GetProperty("schemas").EnumerateArray())
        {
            var schema = registry[(s.GetProperty("entityKind").GetString()!, s.GetProperty("schemaVersion").GetInt32())];
            var expected = s.GetProperty("signedColumns").EnumerateArray()
                .Select(c => (c.GetProperty("columnName").GetString()!, c.GetProperty("type").GetString()!, c.GetProperty("nullable").GetBoolean())).ToList();
            Assert.Equal(expected, schema.Columns.Select(c => (c.ColumnName, c.Type.ToString(), c.Nullable)).ToList());
        }

        Assert.Contains(BusinessSignedSchemas.All, s => s == BusinessSignedSchemas.FieldEncryptedValuesV2);
        Assert.Contains(BusinessSignedSchemas.All, s => s == BusinessSignedSchemas.FileAttachmentVersionsV2);
        Assert.Contains(BusinessSignedSchemas.All, s => s == BusinessSignedSchemas.FieldEncryptedValuesV1); // schema 1 stays registered (S-7)
        Assert.Contains(BusinessSignedSchemas.All, s => s == BusinessSignedSchemas.FileAttachmentVersionsV1);
    }

    [Theory]
    [MemberData(nameof(CaseIds))]
    public void TheBusinessEncoderReproducesEveryVectorCaseFromDomainRows(string caseId)
    {
        var c = Case(caseId);
        var row = Row(c);
        var canonical = BusinessSignedSchemas.Encode(row, c.GetProperty("keyVersion").GetUInt32());
        var expected = c.GetProperty("expected");
        Assert.Equal(expected.GetProperty("canonicalBytesHex").GetString(), Convert.ToHexStringLower(canonical));
        Assert.Equal(expected.GetProperty("rowSignatureHexWithTestKey").GetString(), Convert.ToHexStringLower(RowIntegrityHmac.ComputeTag(TestKey, canonical)));
        Assert.Equal(c.GetProperty("schemaVersion").GetInt32(), BusinessSignedSchemas.CurrentSchemaVersion(row)); // S-1: the stager would choose exactly this schema
    }

    [Fact]
    public void TheSignedSchemaFollowsTheEnvelopeVersionNotTheType()
    {
        Assert.Equal(1, BusinessSignedSchemas.CurrentSchemaVersion(new FieldEncryptedValue { EncryptionEnvelopeVersion = 1 }));
        Assert.Equal(2, BusinessSignedSchemas.CurrentSchemaVersion(new FieldEncryptedValue { EncryptionEnvelopeVersion = 2 }));
        Assert.Equal(1, BusinessSignedSchemas.CurrentSchemaVersion(new FileAttachmentVersion { EncryptionEnvelopeVersion = 1 }));
        Assert.Equal(2, BusinessSignedSchemas.CurrentSchemaVersion(new FileAttachmentVersion { EncryptionEnvelopeVersion = 2 }));
        foreach (var unsupported in new[] { 0, 3, -1, int.MaxValue })
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => BusinessSignedSchemas.CurrentSchemaVersion(new FieldEncryptedValue { EncryptionEnvelopeVersion = unsupported }));
            Assert.Throws<ArgumentOutOfRangeException>(() => BusinessSignedSchemas.CurrentSchemaVersion(new FileAttachmentVersion { EncryptionEnvelopeVersion = unsupported }));
        }
    }

    [Theory]
    [InlineData("ENVS2-FIELD-S1-REFERENCE")]
    [InlineData("ENVS2-ATTACHMENT-S1")]
    [InlineData("ENVS2-FIELD-S2-LOCAL-WRAP")]
    [InlineData("ENVS2-ATTACHMENT-S2")]
    public void EncodingUsesTheStoredSchemaVersionWithNoFallbackAndRefusesADisagreement(string caseId)
    {
        var c = Case(caseId);
        var schema = c.GetProperty("schemaVersion").GetInt32();
        foreach (var unknown in new[] { 0, 3 })
        {
            var row = Row(c);
            row.RowSignatureSchemaVersion = unknown;
            Assert.Throws<ArgumentOutOfRangeException>(() => BusinessSignedSchemas.Encode(row, 1)); // S-2: no fallback to any schema
        }

        var mismatched = Row(c);
        mismatched.RowSignatureSchemaVersion = schema == 1 ? 2 : 1; // S-10: schema 1 / envelope 2 and schema 2 / envelope 1
        Assert.Throws<InvalidOperationException>(() => BusinessSignedSchemas.Encode(mismatched, 1));
    }

    [Theory]
    [InlineData("data_key_identifier")]
    [InlineData("data_key_commitment_bytes")]
    [InlineData("key_wrap_algorithm_code")]
    public void ASchemaTwoRowWithoutEachMetadataColumnCannotBeEncoded(string column)
    {
        foreach (var caseId in new[] { "ENVS2-FIELD-S2-LOCAL-WRAP", "ENVS2-ATTACHMENT-S2" })
        {
            var row = Row(Case(caseId));
            switch (row, column)
            {
                case (FieldEncryptedValue f, "data_key_identifier"): f.DataKeyIdentifier = null; break;
                case (FieldEncryptedValue f, "data_key_commitment_bytes"): f.DataKeyCommitmentBytes = null; break;
                case (FieldEncryptedValue f, _): f.KeyWrapAlgorithmCode = null; break;
                case (FileAttachmentVersion a, "data_key_identifier"): a.DataKeyIdentifier = null; break;
                case (FileAttachmentVersion a, "data_key_commitment_bytes"): a.DataKeyCommitmentBytes = null; break;
                case (FileAttachmentVersion a, _): a.KeyWrapAlgorithmCode = null; break;
            }

            var ex = Assert.Throws<IntegrityFormatException>(() => BusinessSignedSchemas.Encode(row, 1));
            Assert.Equal(IntegrityFormatErrorCode.NullNotPermitted, ex.Code);
        }
    }

    [Fact]
    public void SchemaOneBytesIgnoreAnyV2MetadataSoV1RowsAreByteIdentical()
    {
        // A schema-1 row never signs the v2 columns: even metadata present by mistake cannot change v1 bytes (the database CHECK refuses such a row).
        var c = Case("ENVS2-FIELD-S1-REFERENCE");
        var row = (FieldEncryptedValue)Row(c);
        row.DataKeyIdentifier = Guid.CreateVersion7();
        row.KeyWrapAlgorithmCode = 1;
        Assert.Equal(c.GetProperty("expected").GetProperty("canonicalBytesHex").GetString(), Convert.ToHexStringLower(BusinessSignedSchemas.Encode(row, 1)));
    }

    internal static JsonElement Case(string caseId) =>
        Vectors.RootElement.GetProperty("cases").EnumerateArray().First(c => c.GetProperty("caseId").GetString() == caseId);

    /// <summary>A domain row equal to the vector case (fixture: no product writer produces envelope 2).</summary>
    internal static ISignedContentRow Row(JsonElement c)
    {
        var v = c.GetProperty("values");
        var schema = c.GetProperty("schemaVersion").GetInt32();
        var pk = Guid.Parse(c.GetProperty("primaryKeyId").GetString()!);
        var revision = c.GetProperty("rowRevision").GetInt64();
        if (c.GetProperty("entityKind").GetString() == "field_encrypted_values")
        {
            return new FieldEncryptedValue
            {
                FieldEncryptedValueId = pk,
                TenantId = Uuid(v, "tenant_id"),
                FormRecordId = Uuid(v, "form_record_id"),
                FieldDefinitionId = Uuid(v, "field_definition_id"),
                FieldValueItemId = Uuid(v, "field_value_item_id"),
                FormRecordGroupId = v.GetProperty("form_record_group_id").ValueKind == JsonValueKind.Null ? null : Uuid(v, "form_record_group_id"),
                ContentChangeOperationId = Uuid(v, "content_change_operation_id"),
                RowRevision = revision,
                ContentRevision = revision,
                RowSignatureSchemaVersion = schema,
                EncryptedLogicalType = v.GetProperty("encrypted_logical_type").GetString()!,
                EncryptedValueBytes = Hex(v, "encrypted_value_bytes"),
                EncryptionAlgorithmName = v.GetProperty("encryption_algorithm_name").GetString()!,
                EncryptionAuthTagBytes = Hex(v, "encryption_auth_tag_bytes"),
                EncryptionEnvelopeVersion = v.GetProperty("encryption_envelope_version").GetInt32(),
                EncryptionKeyReference = v.GetProperty("encryption_key_reference").GetString()!,
                EncryptionNonceBytes = Hex(v, "encryption_nonce_bytes"),
                WrappedDataKeyBytes = Hex(v, "wrapped_data_key_bytes"),
                DataKeyIdentifier = schema == 2 ? Uuid(v, "data_key_identifier") : null,
                DataKeyCommitmentBytes = schema == 2 ? Hex(v, "data_key_commitment_bytes") : null,
                KeyWrapAlgorithmCode = schema == 2 ? v.GetProperty("key_wrap_algorithm_code").GetInt32() : null,
            };
        }

        return new FileAttachmentVersion
        {
            FileAttachmentVersionId = pk,
            TenantId = Uuid(v, "tenant_id"),
            FormRecordId = Uuid(v, "form_record_id"),
            FileAttachmentId = Uuid(v, "file_attachment_id"),
            FieldDefinitionId = Uuid(v, "field_definition_id"),
            VersionNumber = v.GetProperty("version_number").GetInt32(),
            OriginalFileName = v.GetProperty("original_file_name").GetString()!,
            DetectedContentType = v.GetProperty("detected_content_type").GetString()!,
            PlaintextSizeBytes = v.GetProperty("plaintext_size_bytes").GetInt64(),
            PlaintextSha256 = Hex(v, "plaintext_sha256"),
            CiphertextSizeBytes = v.GetProperty("ciphertext_size_bytes").GetInt64(),
            CiphertextSha256 = Hex(v, "ciphertext_sha256"),
            StorageProviderName = v.GetProperty("storage_provider_name").GetString()!,
            StorageObjectKey = v.GetProperty("storage_object_key").GetString()!,
            EncryptionAlgorithmName = v.GetProperty("encryption_algorithm_name").GetString()!,
            EncryptionKeyReference = v.GetProperty("encryption_key_reference").GetString()!,
            EncryptionEnvelopeVersion = v.GetProperty("encryption_envelope_version").GetInt32(),
            WrappedDataKey = Hex(v, "wrapped_data_key"),
            EncryptionNoncePrefix = Hex(v, "encryption_nonce_prefix"),
            EncryptionSegmentSizeBytes = v.GetProperty("encryption_segment_size_bytes").GetInt32(),
            ScanVerdict = v.GetProperty("scan_verdict").GetString()!,
            ScannerName = v.GetProperty("scanner_name").GetString()!,
            ScanSignatureName = v.GetProperty("scan_signature_name").ValueKind == JsonValueKind.Null ? null : v.GetProperty("scan_signature_name").GetString(),
            ScannedOn = Timestamp(v, "scanned_on"),
            UploadedByUserId = Uuid(v, "uploaded_by_user_id"),
            UploadedOn = Timestamp(v, "uploaded_on"),
            ContentChangeOperationId = Uuid(v, "content_change_operation_id"),
            RowRevision = revision,
            ContentRevision = revision,
            RowSignatureSchemaVersion = schema,
            DataKeyIdentifier = schema == 2 ? Uuid(v, "data_key_identifier") : null,
            DataKeyCommitmentBytes = schema == 2 ? Hex(v, "data_key_commitment_bytes") : null,
            KeyWrapAlgorithmCode = schema == 2 ? v.GetProperty("key_wrap_algorithm_code").GetInt32() : null,
        };
    }

    private static Guid Uuid(JsonElement v, string name) => Guid.Parse(v.GetProperty(name).GetString()!);

    private static byte[] Hex(JsonElement v, string name) => Convert.FromHexString(v.GetProperty(name).GetString()!);

    private static DateTimeOffset Timestamp(JsonElement v, string name) => DateTimeOffset.Parse(v.GetProperty(name).GetString()!, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
}
