// E3-M2b-2 business gate, legacy writer: compiled against a PRE-E3-M2b-2 Business build (its EF model maps no Envelope v2
// column). Writes one v1 field row and one v1 document row to the migrated database through that build's own EF model, reads
// them back and records that build's canonical bytes, so the gate can show (1) the old binary still writes v1 rows that satisfy
// the new CHECKs and (2) both builds encode the same stored v1 row identically. FKs are disabled by the replica session
// (session_replication_role); every CHECK still applies.
using System.Text.Json;
using EnterpriseForms.Application.Features.FormRecords.Integrity;
using EnterpriseForms.Domain.Features.Documents;
using EnterpriseForms.Domain.Features.FormRecords;
using EnterpriseForms.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;

var admin = Environment.GetEnvironmentVariable("GATE_ADMIN") ?? throw new InvalidOperationException("GATE_ADMIN");
var output = Environment.GetEnvironmentVariable("GATE_LEGACY_OUTPUT") ?? throw new InvalidOperationException("GATE_LEGACY_OUTPUT");
var label = Environment.GetEnvironmentVariable("GATE_LEGACY_LABEL") ?? "legacy business build";
var replica = new NpgsqlConnectionStringBuilder(admin) { Options = "-c session_replication_role=replica" }.ConnectionString;

BusinessDbContext Context() => new(new DbContextOptionsBuilder<BusinessDbContext>().UseNpgsql(replica).Options);

var field = new FieldEncryptedValue
{
    FieldEncryptedValueId = Guid.CreateVersion7(), TenantId = Guid.CreateVersion7(), FormRecordId = Guid.CreateVersion7(), FieldDefinitionId = Guid.CreateVersion7(),
    FieldValueItemId = Guid.CreateVersion7(), ContentChangeOperationId = Guid.CreateVersion7(), ContentRevision = 1, RowRevision = 1, RowSignature = Enumerable.Repeat((byte)0x21, 32).ToArray(),
    RowSignatureKeyVersion = 1, RowSignatureSchemaVersion = 1, CreatedOn = new DateTimeOffset(2026, 11, 15, 12, 0, 0, TimeSpan.Zero), CreatedByUserId = Guid.CreateVersion7(),
    EncryptedValueBytes = [9, 8, 7], EncryptionNonceBytes = Enumerable.Repeat((byte)0x0c, 12).ToArray(), EncryptionAuthTagBytes = Enumerable.Repeat((byte)0xab, 16).ToArray(),
    WrappedDataKeyBytes = Enumerable.Repeat((byte)0xcd, 60).ToArray(), EncryptionAlgorithmName = "AES-256-GCM", EncryptionKeyReference = "local-dev-kek-v1", EncryptionEnvelopeVersion = 1,
    EncryptedLogicalType = "Integer",
};
var attachment = new FileAttachmentVersion
{
    FileAttachmentVersionId = Guid.CreateVersion7(), TenantId = Guid.CreateVersion7(), FormRecordId = Guid.CreateVersion7(), FileAttachmentId = Guid.CreateVersion7(),
    FieldDefinitionId = Guid.CreateVersion7(), VersionNumber = 1, OriginalFileName = "legacy.pdf", DetectedContentType = "application/pdf", PlaintextSizeBytes = 10,
    PlaintextSha256 = Enumerable.Repeat((byte)0x31, 32).ToArray(), CiphertextSizeBytes = 38, CiphertextSha256 = Enumerable.Repeat((byte)0x13, 32).ToArray(), StorageProviderName = "azurite",
    StorageObjectKey = "documents/legacy/" + Guid.CreateVersion7().ToString("N"), EncryptionAlgorithmName = "AES-256-GCM", EncryptionKeyReference = "local-dev-kek-v1", EncryptionEnvelopeVersion = 1,
    WrappedDataKey = Enumerable.Repeat((byte)0x41, 60).ToArray(), EncryptionNoncePrefix = Enumerable.Repeat((byte)0x23, 8).ToArray(), EncryptionSegmentSizeBytes = 1048576,
    ScanVerdict = "Clean", ScannerName = "clamd", ScannedOn = new DateTimeOffset(2026, 11, 15, 12, 0, 0, TimeSpan.Zero), UploadedByUserId = Guid.CreateVersion7(),
    UploadedOn = new DateTimeOffset(2026, 11, 15, 11, 0, 0, TimeSpan.Zero), ContentChangeOperationId = Guid.CreateVersion7(), ContentRevision = 1, RowRevision = 1,
    RowSignature = Enumerable.Repeat((byte)0x21, 32).ToArray(), RowSignatureKeyVersion = 1, RowSignatureSchemaVersion = 1, VersionState = "Committed",
};

await using (var context = Context())
{
    context.Add(field);
    context.Add(attachment);
    await context.SaveChangesAsync();
}

await using var read = Context();
var fieldRead = await read.Set<FieldEncryptedValue>().AsNoTracking().SingleAsync(v => v.FieldEncryptedValueId == field.FieldEncryptedValueId);
var attachmentRead = await read.Set<FileAttachmentVersion>().AsNoTracking().SingleAsync(v => v.FileAttachmentVersionId == attachment.FileAttachmentVersionId);
var modelHasV2 = typeof(FieldEncryptedValue).GetProperty("DataKeyIdentifier") is not null || typeof(FileAttachmentVersion).GetProperty("DataKeyIdentifier") is not null
    || read.Model.FindEntityType(typeof(FieldEncryptedValue))!.GetProperties().Any(p => p.Name.StartsWith("DataKey", StringComparison.Ordinal) || p.Name == "KeyWrapAlgorithmCode");

File.WriteAllText(output, JsonSerializer.Serialize(new
{
    legacyBuild = label,
    modelHasEnvelopeV2Properties = modelHasV2,
    rows = new[]
    {
        new { entityKind = "field_encrypted_values", primaryKey = field.FieldEncryptedValueId.ToString("D"), legacyCanonicalHex = Convert.ToHexStringLower(BusinessSignedSchemas.Encode(fieldRead, 1)) },
        new { entityKind = "file_attachment_versions", primaryKey = attachment.FileAttachmentVersionId.ToString("D"), legacyCanonicalHex = Convert.ToHexStringLower(BusinessSignedSchemas.Encode(attachmentRead, 1)) },
    },
}, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine("legacy writer: 2 v1 rows written and read back");
return 0;
