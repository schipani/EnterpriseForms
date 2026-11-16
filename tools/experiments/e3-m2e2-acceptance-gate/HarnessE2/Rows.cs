// E3-M2e-2 fixtures. Envelope v2 rows come ONLY from the real E3-M1 core (FieldSealBatchV2 for field batches, DocumentEnvelopeV2Cipher
// for document versions) with a real key provider: LocalDevelopmentKeyProvider (wrap code 1, random per-run KEK) or VaultTransitKeyProvider
// against the run's throwaway Vault (wrap code 2). Every value and attachment row is encoded by the business signed-column registry
// (BusinessSignedSchemas.Encode), exactly as the business stages it for the Authority. Only the record header uses the M2e-1 harness
// schema (form_records schema 3), and v1 envelopes keep the M2e-1 synthetic v1 shape (the Authority checks v1 shape, not v1 bytes).
// The harness never decrypts and never holds a raw data key.
using System.Security.Cryptography;
using AuthorizationAuthority.Application.Features.Content;
using AuthorizationAuthority.Contracts.V1;
using EnterpriseForms.Application.Features.Documents.Ports;
using EnterpriseForms.Application.Features.FormRecords.Integrity;
using EnterpriseForms.Domain.Features.Documents;
using EnterpriseForms.Domain.Features.FormRecords;
using EnterpriseForms.Infrastructure.Features.Documents;
using EnterpriseForms.Integrity.Canonical;
using Microsoft.Extensions.Options;

internal sealed record Staged(object Source, Guid StorageKey, byte[] Bytes, ManifestItemKind Kind, Guid ItemId)
{
    public string Base64 => Convert.ToBase64String(Bytes);
}

/// <summary>A header row in the M2e-1 harness schema (form_records schema 3), identity primary key.</summary>
internal sealed record HeaderSource(Guid OrganizationId, Guid FormRecordId);

/// <summary>A row whose canonical bytes are produced by a test-chosen encoder (S-4 / S-9 negatives only).</summary>
internal sealed record RawSource(Guid ItemId, Func<Guid, long, Staged> Encode);

internal static class Rows
{
    public static readonly Guid Form = Guid.CreateVersion7();
    private static readonly Guid Creator = Guid.CreateVersion7();
    private static readonly Guid Version = Guid.CreateVersion7();
    private static readonly DateTimeOffset Fixed = new(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);

    private static readonly RowSchemaDefinition HeaderV3 = new("form_records", 3,
    [
        new("content_change_operation_id", RowColumnType.Uuid, false), new("created_by_user_id", RowColumnType.Uuid, false), new("created_form_version_id", RowColumnType.Uuid, false),
        new("form_definition_id", RowColumnType.Uuid, false), new("form_relationship_definition_id", RowColumnType.Uuid, true), new("parent_form_record_id", RowColumnType.Uuid, true),
        new("record_status", RowColumnType.Text, false), new("relationship_sort_order", RowColumnType.Int64, true), new("submitted_on", RowColumnType.TimestampUtc, true), new("tenant_id", RowColumnType.Uuid, false),
    ]);

    // ------------------------------------------------------------------------------------------------ key providers (real business code)
    public static IEncryptionKeyProvider LocalKeys(byte[] kek) => new LocalDevelopmentKeyProvider(Options.Create(new EncryptionOptions
    {
        KeyProvider = EncryptionOptions.LocalDevelopmentProviderName,
        LocalDevelopmentKekBase64 = Convert.ToBase64String(kek),
        LocalDevelopmentKeyReference = EnvelopeRules.LegacyKeyReference,
    }));

    public static IEncryptionKeyProvider VaultKeys(string address, string token) => new VaultTransitKeyProvider(new PlainHttpFactory(), Options.Create(new EncryptionOptions
    {
        KeyProvider = EncryptionOptions.VaultTransitProviderName,
        VaultTransit = new VaultTransitEncryptionOptions { Address = Target.RequireVault(address), Token = token, MountPath = "enterpriseforms-encryption" },
    }));

    private sealed class PlainHttpFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new SocketsHttpHandler { AllowAutoRedirect = false, UseProxy = false });
    }

    // ------------------------------------------------------------------------------------------------ sources
    public static FieldStringValue Text(Guid org, Guid record, Guid field, Guid item, string value) => new()
    {
        TenantId = org, FormRecordId = record, FieldDefinitionId = field, FieldValueItemId = item, StringValue = value, RowSignatureSchemaVersion = 1, CreatedByUserId = Creator, CreatedOn = Fixed,
    };

    /// <summary>A well-formed v1 field envelope in the M2e-1 synthetic shape (fresh nonce and wrapped key per seed).</summary>
    public static FieldEncryptedValue V1Field(Guid org, Guid record, Guid field, Guid item, byte seed) => new()
    {
        TenantId = org, FormRecordId = record, FieldDefinitionId = field, FieldValueItemId = item, EncryptedLogicalType = "String",
        EncryptedValueBytes = Fill(24, seed), EncryptionAuthTagBytes = Fill(16, (byte)(seed + 1)), EncryptionNonceBytes = Fill(12, (byte)(seed + 2)), WrappedDataKeyBytes = Fill(60, (byte)(seed + 3)),
        EncryptionAlgorithmName = "AES-256-GCM", EncryptionKeyReference = EnvelopeRules.LegacyKeyReference, EncryptionEnvelopeVersion = 1, RowSignatureSchemaVersion = 1,
        CreatedByUserId = Creator, CreatedOn = Fixed,
    };

    /// <summary>A v2 field row sealed by the real M1 core into <paramref name="batch"/> (signed schema 2; every v2 column from the envelope).</summary>
    public static FieldEncryptedValue V2Field(M1Core.Batch batch, Guid field, Guid item, string logicalType, string plaintext)
    {
        var e = batch.Seal(field, item, logicalType, plaintext);
        return new FieldEncryptedValue
        {
            TenantId = e.OrganizationId, FormRecordId = e.FormRecordId, FieldDefinitionId = e.FieldDefinitionId, FieldValueItemId = e.FieldValueItemId, EncryptedLogicalType = e.LogicalType,
            EncryptedValueBytes = e.Ciphertext, EncryptionNonceBytes = e.Nonce, EncryptionAuthTagBytes = e.Tag, WrappedDataKeyBytes = e.WrappedSeed,
            EncryptionAlgorithmName = "AES-256-GCM", EncryptionKeyReference = e.KekReference, EncryptionEnvelopeVersion = 2, RowSignatureSchemaVersion = 2,
            DataKeyIdentifier = e.DataKeyIdentifier, DataKeyCommitmentBytes = e.Commitment, KeyWrapAlgorithmCode = e.WrapCode,
            CreatedByUserId = Creator, CreatedOn = Fixed,
        };
    }

    /// <summary>A v1 document version in the M2e-1 synthetic shape.</summary>
    public static FileAttachmentVersion V1Document(Guid org, Guid record, Guid field, Guid attachment) => new()
    {
        TenantId = org, FormRecordId = record, FileAttachmentId = attachment, FieldDefinitionId = field, VersionNumber = 1, OriginalFileName = "statement.pdf", DetectedContentType = "application/pdf",
        PlaintextSizeBytes = 1000, PlaintextSha256 = Fill(32, 9), CiphertextSizeBytes = 1024, CiphertextSha256 = Fill(32, 7), StorageProviderName = "azurite", StorageObjectKey = "objects/" + attachment.ToString("N"),
        EncryptionAlgorithmName = "AES-256-GCM", EncryptionKeyReference = EnvelopeRules.LegacyKeyReference, EncryptionEnvelopeVersion = 1, WrappedDataKey = Fill(60, 5), EncryptionNoncePrefix = Fill(8, 3),
        EncryptionSegmentSizeBytes = 1048576, ScanVerdict = ScanVerdicts.Clean, ScannerName = "clamav", ScanSignatureName = null, ScannedOn = Fixed, UploadedByUserId = Creator, UploadedOn = Fixed,
        RowSignatureSchemaVersion = 1,
    };

    /// <summary>A v2 document version encrypted by the real M1 document cipher (random plaintext; the ciphertext itself is discarded).</summary>
    public static async Task<FileAttachmentVersion> V2DocumentAsync(IEncryptionKeyProvider keys, Guid org, Guid record, Guid field, Guid attachment, int versionNumber, int plaintextSize)
    {
        using var sink = new MemoryStream();
        var e = await M1Core.EncryptDocumentAsync(new MemoryStream(RandomNumberGenerator.GetBytes(plaintextSize)), sink, keys, org, record, attachment, versionNumber);
        if (sink.Length != e.CiphertextSizeBytes)
        {
            throw new InvalidOperationException("the M1 document cipher wrote a different ciphertext size than it reported");
        }

        return new FileAttachmentVersion
        {
            TenantId = org, FormRecordId = record, FileAttachmentId = attachment, FieldDefinitionId = field, VersionNumber = versionNumber,
            OriginalFileName = "statement.pdf", DetectedContentType = "application/pdf",
            PlaintextSizeBytes = e.PlaintextSizeBytes, PlaintextSha256 = e.PlaintextSha256, CiphertextSizeBytes = e.CiphertextSizeBytes, CiphertextSha256 = e.CiphertextSha256,
            StorageProviderName = "azurite", StorageObjectKey = "objects/v2/" + attachment.ToString("N"), EncryptionAlgorithmName = "AES-256-GCM", EncryptionKeyReference = e.KekReference,
            EncryptionEnvelopeVersion = 2, WrappedDataKey = e.WrappedSeed, EncryptionNoncePrefix = e.NoncePrefix, EncryptionSegmentSizeBytes = e.SegmentSizeBytes,
            DataKeyIdentifier = e.DataKeyIdentifier, DataKeyCommitmentBytes = e.Commitment, KeyWrapAlgorithmCode = e.WrapCode,
            ScanVerdict = ScanVerdicts.Clean, ScannerName = "clamav", ScannedOn = Fixed, UploadedByUserId = Creator, UploadedOn = Fixed, RowSignatureSchemaVersion = 2,
        };
    }

    // ------------------------------------------------------------------------------------------------ encoding (the business registry)
    /// <summary>
    /// The canonical bytes of <paramref name="source"/> staged by operation <paramref name="operation"/> at <paramref name="revision"/>:
    /// a fresh storage primary key, the operation binding and the row revision change; every Identity and Content column is unchanged.
    /// That is exactly a carry-forward when the source is a current accepted row.
    /// </summary>
    public static Staged Encode(object source, Guid operation, long revision)
    {
        switch (source)
        {
            case HeaderSource h:
            {
                var values = new Dictionary<string, RowValue?>(StringComparer.Ordinal)
                {
                    ["content_change_operation_id"] = RowValue.Uuid(operation), ["created_by_user_id"] = RowValue.Uuid(Creator), ["created_form_version_id"] = RowValue.Uuid(Version),
                    ["form_definition_id"] = RowValue.Uuid(Form), ["form_relationship_definition_id"] = null, ["parent_form_record_id"] = null, ["record_status"] = RowValue.Text("Draft"),
                    ["relationship_sort_order"] = null, ["submitted_on"] = null, ["tenant_id"] = RowValue.Uuid(h.OrganizationId),
                };
                var bytes = RowCanonicalizerV1.Encode(HeaderV3, new RowSigningContext(1, h.OrganizationId, h.FormRecordId, (ulong)revision), values);
                return new Staged(source, h.FormRecordId, bytes, ManifestItemKind.RecordHeader, h.FormRecordId);
            }

            case FieldStringValue v:
            {
                var copy = new FieldStringValue
                {
                    FieldStringValueId = Guid.CreateVersion7(), TenantId = v.TenantId, FormRecordId = v.FormRecordId, FieldDefinitionId = v.FieldDefinitionId, FormRecordGroupId = v.FormRecordGroupId,
                    FieldValueItemId = v.FieldValueItemId, StringValue = v.StringValue, ContentChangeOperationId = operation, ContentRevision = revision, RowRevision = revision,
                    RowSignatureSchemaVersion = v.RowSignatureSchemaVersion, CreatedByUserId = v.CreatedByUserId, CreatedOn = v.CreatedOn,
                };
                return new Staged(source, copy.FieldStringValueId, BusinessSignedSchemas.Encode(copy, 1), ManifestItemKind.FieldValue, v.FieldValueItemId);
            }

            case FieldEncryptedValue v:
            {
                var copy = CloneEncrypted(v);
                copy.FieldEncryptedValueId = Guid.CreateVersion7();
                copy.ContentChangeOperationId = operation;
                copy.ContentRevision = revision;
                copy.RowRevision = revision;
                return new Staged(source, copy.FieldEncryptedValueId, BusinessSignedSchemas.Encode(copy, 1), ManifestItemKind.FieldValue, v.FieldValueItemId);
            }

            case FileAttachmentVersion a:
            {
                var copy = CloneAttachment(a);
                copy.FileAttachmentVersionId = Guid.CreateVersion7();
                copy.ContentChangeOperationId = operation;
                copy.ContentRevision = revision;
                copy.RowRevision = revision;
                return new Staged(source, copy.FileAttachmentVersionId, BusinessSignedSchemas.Encode(copy, 1), ManifestItemKind.Attachment, a.FileAttachmentId);
            }

            case RawSource raw:
                return raw.Encode(operation, revision);

            default:
                throw new ArgumentOutOfRangeException(nameof(source), source.GetType().Name, "unknown fixture source");
        }
    }

    // ------------------------------------------------------------------------------------------------ raw rows (S-4 / S-9 negatives only)
    /// <summary>
    /// A field_encrypted_values row encoded directly (not through the business registry, which refuses these shapes): the schema version,
    /// the envelope version, the presence of the v2 columns and the signed type of key_wrap_algorithm_code are chosen by the test.
    /// </summary>
    public static RawSource RawEncrypted(FieldEncryptedValue v, int schemaVersion, int envelopeVersion, bool v2Columns, RowColumnType wrapCodeType = RowColumnType.Int32)
    {
        return new RawSource(v.FieldValueItemId, (operation, revision) =>
        {
            var columns = new List<RowSignedColumn>
            {
                new("content_change_operation_id", RowColumnType.Uuid, false), new("encrypted_logical_type", RowColumnType.Text, false), new("encrypted_value_bytes", RowColumnType.Bytes, false),
                new("encryption_algorithm_name", RowColumnType.Text, false), new("encryption_auth_tag_bytes", RowColumnType.Bytes, false), new("encryption_envelope_version", RowColumnType.Int32, false),
                new("encryption_key_reference", RowColumnType.Text, false), new("encryption_nonce_bytes", RowColumnType.Bytes, false), new("field_definition_id", RowColumnType.Uuid, false),
                new("field_value_item_id", RowColumnType.Uuid, false), new("form_record_group_id", RowColumnType.Uuid, true), new("form_record_id", RowColumnType.Uuid, false),
                new("tenant_id", RowColumnType.Uuid, false), new("wrapped_data_key_bytes", RowColumnType.Bytes, false),
            };
            var values = new Dictionary<string, RowValue?>(StringComparer.Ordinal)
            {
                ["content_change_operation_id"] = RowValue.Uuid(operation), ["encrypted_logical_type"] = RowValue.Text(v.EncryptedLogicalType), ["encrypted_value_bytes"] = RowValue.Bytes(v.EncryptedValueBytes),
                ["encryption_algorithm_name"] = RowValue.Text(v.EncryptionAlgorithmName), ["encryption_auth_tag_bytes"] = RowValue.Bytes(v.EncryptionAuthTagBytes), ["encryption_envelope_version"] = RowValue.Int32(envelopeVersion),
                ["encryption_key_reference"] = RowValue.Text(v.EncryptionKeyReference), ["encryption_nonce_bytes"] = RowValue.Bytes(v.EncryptionNonceBytes), ["field_definition_id"] = RowValue.Uuid(v.FieldDefinitionId),
                ["field_value_item_id"] = RowValue.Uuid(v.FieldValueItemId), ["form_record_group_id"] = null, ["form_record_id"] = RowValue.Uuid(v.FormRecordId),
                ["tenant_id"] = RowValue.Uuid(v.TenantId), ["wrapped_data_key_bytes"] = RowValue.Bytes(v.WrappedDataKeyBytes),
            };
            if (v2Columns)
            {
                columns.Add(new("data_key_commitment_bytes", RowColumnType.Bytes, false));
                columns.Add(new("data_key_identifier", RowColumnType.Uuid, false));
                columns.Add(new("key_wrap_algorithm_code", wrapCodeType, false));
                values["data_key_commitment_bytes"] = RowValue.Bytes(v.DataKeyCommitmentBytes!);
                values["data_key_identifier"] = RowValue.Uuid(v.DataKeyIdentifier!.Value);
                values["key_wrap_algorithm_code"] = wrapCodeType == RowColumnType.Int64 ? RowValue.Int64(v.KeyWrapAlgorithmCode!.Value) : RowValue.Int32(v.KeyWrapAlgorithmCode!.Value);
            }

            var schema = new RowSchemaDefinition("field_encrypted_values", schemaVersion, [.. columns.OrderBy(c => c.ColumnName, StringComparer.Ordinal)]);
            var primaryKey = Guid.CreateVersion7();
            var bytes = RowCanonicalizerV1.Encode(schema, new RowSigningContext(1, v.TenantId, primaryKey, (ulong)revision), values);
            return new Staged(v, primaryKey, bytes, ManifestItemKind.FieldValue, v.FieldValueItemId);
        });
    }

    public static FieldEncryptedValue CloneEncrypted(FieldEncryptedValue v) => new()
    {
        FieldEncryptedValueId = v.FieldEncryptedValueId, TenantId = v.TenantId, FormRecordId = v.FormRecordId, FieldDefinitionId = v.FieldDefinitionId, FormRecordGroupId = v.FormRecordGroupId,
        FieldValueItemId = v.FieldValueItemId, EncryptedLogicalType = v.EncryptedLogicalType, EncryptedValueBytes = [.. v.EncryptedValueBytes], EncryptionNonceBytes = [.. v.EncryptionNonceBytes],
        EncryptionAuthTagBytes = [.. v.EncryptionAuthTagBytes], WrappedDataKeyBytes = [.. v.WrappedDataKeyBytes], EncryptionAlgorithmName = v.EncryptionAlgorithmName,
        EncryptionKeyReference = v.EncryptionKeyReference, EncryptionEnvelopeVersion = v.EncryptionEnvelopeVersion, RowSignatureSchemaVersion = v.RowSignatureSchemaVersion,
        DataKeyIdentifier = v.DataKeyIdentifier, DataKeyCommitmentBytes = v.DataKeyCommitmentBytes is null ? null : [.. v.DataKeyCommitmentBytes], KeyWrapAlgorithmCode = v.KeyWrapAlgorithmCode,
        ContentChangeOperationId = v.ContentChangeOperationId, ContentRevision = v.ContentRevision, RowRevision = v.RowRevision, CreatedByUserId = v.CreatedByUserId, CreatedOn = v.CreatedOn,
    };

    public static FileAttachmentVersion CloneAttachment(FileAttachmentVersion a) => new()
    {
        FileAttachmentVersionId = a.FileAttachmentVersionId, TenantId = a.TenantId, FormRecordId = a.FormRecordId, FileAttachmentId = a.FileAttachmentId, FieldDefinitionId = a.FieldDefinitionId,
        VersionNumber = a.VersionNumber, OriginalFileName = a.OriginalFileName, DetectedContentType = a.DetectedContentType, PlaintextSizeBytes = a.PlaintextSizeBytes,
        PlaintextSha256 = [.. a.PlaintextSha256], CiphertextSizeBytes = a.CiphertextSizeBytes, CiphertextSha256 = [.. a.CiphertextSha256], StorageProviderName = a.StorageProviderName,
        StorageObjectKey = a.StorageObjectKey, EncryptionAlgorithmName = a.EncryptionAlgorithmName, EncryptionKeyReference = a.EncryptionKeyReference, EncryptionEnvelopeVersion = a.EncryptionEnvelopeVersion,
        WrappedDataKey = [.. a.WrappedDataKey], EncryptionNoncePrefix = [.. a.EncryptionNoncePrefix], EncryptionSegmentSizeBytes = a.EncryptionSegmentSizeBytes,
        DataKeyIdentifier = a.DataKeyIdentifier, DataKeyCommitmentBytes = a.DataKeyCommitmentBytes is null ? null : [.. a.DataKeyCommitmentBytes], KeyWrapAlgorithmCode = a.KeyWrapAlgorithmCode,
        ScanVerdict = a.ScanVerdict, ScannerName = a.ScannerName, ScanSignatureName = a.ScanSignatureName, ScannedOn = a.ScannedOn, UploadedByUserId = a.UploadedByUserId, UploadedOn = a.UploadedOn,
        ContentChangeOperationId = a.ContentChangeOperationId, ContentRevision = a.ContentRevision, RowRevision = a.RowRevision, RowSignatureSchemaVersion = a.RowSignatureSchemaVersion,
    };

    /// <summary>A copy of a v2 field row moved to another item (and optionally record/organization), keeping every envelope column: a forged reuse.</summary>
    public static FieldEncryptedValue Transplant(FieldEncryptedValue v, Guid organization, Guid record, Guid field, Guid item)
    {
        var copy = CloneEncrypted(v);
        copy.TenantId = organization;
        copy.FormRecordId = record;
        copy.FieldDefinitionId = field;
        copy.FieldValueItemId = item;
        if (organization != v.TenantId)
        {
            copy.EncryptionKeyReference = v.EncryptionKeyReference.Replace(v.TenantId.ToString("D"), organization.ToString("D"), StringComparison.Ordinal);
        }

        return copy;
    }

    public static byte[] Fill(int length, byte value) => Enumerable.Repeat(value, length).ToArray();

    /// <summary>The v2 data-key identifier of a source row, or null.</summary>
    public static Guid? DataKeyOf(object source) => source switch
    {
        FieldEncryptedValue { EncryptionEnvelopeVersion: 2 } v => v.DataKeyIdentifier,
        FileAttachmentVersion { EncryptionEnvelopeVersion: 2 } a => a.DataKeyIdentifier,
        _ => null,
    };

    public static byte[]? CommitmentOf(object source) => source switch
    {
        FieldEncryptedValue { EncryptionEnvelopeVersion: 2 } v => v.DataKeyCommitmentBytes,
        FileAttachmentVersion { EncryptionEnvelopeVersion: 2 } a => a.DataKeyCommitmentBytes,
        _ => null,
    };
}
