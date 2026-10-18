using System.Security.Cryptography;
using EnterpriseForms.Application.Common;
using EnterpriseForms.Application.Features.Documents.Ports;
using EnterpriseForms.Application.Features.FormDefinitions.Layout;
using EnterpriseForms.Application.Features.FormRecords.Integrity;
using EnterpriseForms.Application.Features.FormRecords.Values;
using EnterpriseForms.Application.Features.Organizations;
using EnterpriseForms.Domain.Features.Documents;
using EnterpriseForms.Domain.Features.FormDefinitions;
using EnterpriseForms.Domain.Features.FormRecords;
using EnterpriseForms.Infrastructure.Features.Documents;
using EnterpriseForms.Integrity.Canonical;
using Microsoft.Extensions.Options;
using Xunit;

namespace EnterpriseForms.Api.ContractTests;

/// <summary>Stage B unit coverage: layout validation, typed value codec, signed-schema encoding, AES-GCM envelope, ClamAV reply parsing, theme contrast.</summary>
public sealed class StageBUnitTests
{
    private static readonly Guid Tenant = Guid.CreateVersion7();

    [Fact]
    public void LayoutParserAcceptsSchemaV2AndRejectsUnsafeContent()
    {
        var fieldId = Guid.CreateVersion7();
        var relId = Guid.CreateVersion7();
        var json = $$$"""{"layoutSchemaVersion":2,"title":"Accused","sections":[{"sectionKey":"main","title":"Main","gridColumns":4,"elements":[{"kind":"field","fieldDefinitionId":"{{{fieldId}}}","control":"text","label":"Given name","required":true,"layout":{"columnSpan":2,"startOnNewRow":false},"validation":{"maxLength":100}},{"kind":"relatedFormList","formRelationshipDefinitionId":"{{{relId}}}","label":"Addresses","layout":{"columnSpan":4,"startOnNewRow":true},"summaryFieldKeys":["street"]},{"kind":"documentCollection","fieldDefinitionId":"{{{Guid.CreateVersion7()}}}","label":"Documents","upload":{"maxFiles":5,"maxFileSizeBytes":1048576,"allowedContentTypes":["application/pdf"]}}]}]}""";
        var layout = FormLayoutCodec.Parse(json);
        Assert.Equal(3, layout.Elements.Count());
        Assert.True(layout.Elements.First().Validation.Required);
        Assert.Equal(2, layout.Elements.First().Layout.ColumnSpan);
        Assert.Equal(5, layout.Elements.Last().Upload!.MaxFiles);
        var roundTrip = FormLayoutCodec.Parse(FormLayoutCodec.Serialize(layout));
        Assert.Equal(FormLayoutCodec.Serialize(layout), FormLayoutCodec.Serialize(roundTrip));

        Assert.Throws<BusinessException>(() => FormLayoutCodec.Parse(json.Replace("\"columnSpan\":2", "\"columnSpan\":5", StringComparison.Ordinal)));
        Assert.Throws<BusinessException>(() => FormLayoutCodec.Parse(json.Replace("\"gridColumns\":4", "\"gridColumns\":3", StringComparison.Ordinal)));
        Assert.Throws<BusinessException>(() => FormLayoutCodec.Parse(json.Replace("Given name", "<script>alert(1)</script>", StringComparison.Ordinal)));
        Assert.Throws<BusinessException>(() => FormLayoutCodec.Parse(json.Replace("\"maxFiles\":5", "\"maxFiles\":500", StringComparison.Ordinal)));
    }

    [Fact]
    public void TypedValueCodecValidatesAndRoundTripsEveryStageBType()
    {
        var record = Guid.CreateVersion7();
        var now = DateTimeOffset.UtcNow;
        FieldDefinition Field(string key, string type) => new() { FieldDefinitionId = Guid.CreateVersion7(), FieldKey = key, FieldDataType = type };
        var option = Field("status", FieldDataTypes.Option);
        var options = new List<FieldOption> { new() { FieldOptionId = Guid.CreateVersion7(), FieldDefinitionId = option.FieldDefinitionId, OptionKey = "active", OptionDisplayText = "Active", IsActive = true }, new() { FieldOptionId = Guid.CreateVersion7(), FieldDefinitionId = option.FieldDefinitionId, OptionKey = "retired", OptionDisplayText = "Retired", IsActive = false } };
        var byId = options.ToDictionary(o => o.FieldOptionId);

        var s = FieldValueCodec.CreateRow(Field("given_name", FieldDataTypes.String), null, options, "Avery", Tenant, record, Guid.CreateVersion7(), now, Tenant);
        var i = FieldValueCodec.CreateRow(Field("age", FieldDataTypes.Integer), null, options, "42", Tenant, record, Guid.CreateVersion7(), now, Tenant);
        var d = FieldValueCodec.CreateRow(Field("fine", FieldDataTypes.Decimal), null, options, "1250.5000", Tenant, record, Guid.CreateVersion7(), now, Tenant);
        var dt = FieldValueCodec.CreateRow(Field("dob", FieldDataTypes.Date), null, options, "1990-02-28", Tenant, record, Guid.CreateVersion7(), now, Tenant);
        var b = FieldValueCodec.CreateRow(Field("in_custody", FieldDataTypes.Boolean), null, options, "true", Tenant, record, Guid.CreateVersion7(), now, Tenant);
        var o = FieldValueCodec.CreateRow(option, null, options, "active", Tenant, record, Guid.CreateVersion7(), now, Tenant);
        Assert.Equal("Avery", FieldValueCodec.Format(s, byId));
        Assert.Equal("42", FieldValueCodec.Format(i, byId));
        Assert.Equal("1250.5000", FieldValueCodec.Format(d, byId));
        Assert.Equal("1990-02-28", FieldValueCodec.Format(dt, byId));
        Assert.Equal("true", FieldValueCodec.Format(b, byId));
        Assert.Equal("active", FieldValueCodec.Format(o, byId));

        Assert.Throws<BusinessException>(() => FieldValueCodec.CreateRow(Field("age", FieldDataTypes.Integer), null, options, "forty", Tenant, record, Guid.CreateVersion7(), now, Tenant));
        Assert.Throws<BusinessException>(() => FieldValueCodec.CreateRow(Field("dob", FieldDataTypes.Date), null, options, "02/28/1990", Tenant, record, Guid.CreateVersion7(), now, Tenant));
        Assert.Throws<BusinessException>(() => FieldValueCodec.CreateRow(Field("fine", FieldDataTypes.Decimal), null, options, "1.23456", Tenant, record, Guid.CreateVersion7(), now, Tenant));
        var disabled = Assert.Throws<BusinessException>(() => FieldValueCodec.CreateRow(option, null, options, "retired", Tenant, record, Guid.CreateVersion7(), now, Tenant));
        Assert.Equal("ORGANIZATION_OPTION_DISABLED", disabled.ErrorCode);
        var validation = new LayoutValidation(true, 2, 5, "[A-Z][a-z]+", null, null, null, null);
        var element = new LayoutElement("field", Guid.CreateVersion7(), null, "text", "x", null, new LayoutPlacement(4, false), validation, null, null);
        Assert.Throws<BusinessException>(() => FieldValueCodec.CreateRow(Field("given_name", FieldDataTypes.String), element, options, "avery", Tenant, record, Guid.CreateVersion7(), now, Tenant));
    }

    [Fact]
    public void SignedSchemasEncodeEveryRowKindAndKeepStageAHeaderVersionVerifiable()
    {
        var record = Guid.CreateVersion7();
        var header = new FormRecordRevision { TenantId = Tenant, FormRecordId = record, FormDefinitionId = Guid.CreateVersion7(), CreatedFormVersionId = Guid.CreateVersion7(), CreatedByUserId = Guid.CreateVersion7(), ContentChangeOperationId = Guid.CreateVersion7(), ContentRevision = 1, RowRevision = 1, RowSignatureSchemaVersion = 3, ParentFormRecordId = Guid.CreateVersion7(), FormRelationshipDefinitionId = Guid.CreateVersion7(), RelationshipSortOrder = 2 };
        var v3 = RowCanonicalHeader.Parse(BusinessSignedSchemas.Encode(header, 1));
        Assert.Equal(3, v3.SchemaVersion);
        Assert.Equal("form_records", v3.EntityKind);
        header.RowSignatureSchemaVersion = 2;
        var v2 = RowCanonicalHeader.Parse(BusinessSignedSchemas.Encode(header, 1));
        Assert.Equal(2, v2.SchemaVersion);

        var version = new FileAttachmentVersion { TenantId = Tenant, FormRecordId = record, FileAttachmentId = Guid.CreateVersion7(), FieldDefinitionId = Guid.CreateVersion7(), VersionNumber = 1, OriginalFileName = "warrant.pdf", DetectedContentType = "application/pdf", PlaintextSizeBytes = 10, PlaintextSha256 = new byte[32], CiphertextSizeBytes = 42, CiphertextSha256 = new byte[32], StorageProviderName = "AzureBlob", StorageObjectKey = "documents/x", EncryptionKeyReference = "k", WrappedDataKey = new byte[60], EncryptionNoncePrefix = new byte[8], EncryptionSegmentSizeBytes = 1048576, ScanVerdict = "Clean", ScannerName = "ClamAV", ScannedOn = SignedTimestamp.Truncate(DateTimeOffset.UtcNow), UploadedByUserId = Guid.CreateVersion7(), UploadedOn = SignedTimestamp.Truncate(DateTimeOffset.UtcNow), ContentChangeOperationId = Guid.CreateVersion7(), ContentRevision = 2, RowRevision = 2, RowSignatureSchemaVersion = 1 };
        var attachment = RowCanonicalHeader.Parse(BusinessSignedSchemas.Encode(version, 1));
        Assert.Equal("file_attachment_versions", attachment.EntityKind);
        Assert.Throws<EnterpriseForms.Integrity.IntegrityFormatException>(() => BusinessSignedSchemas.Encode(new FileAttachmentVersion { TenantId = Tenant, FormRecordId = record, FileAttachmentId = Guid.CreateVersion7(), FieldDefinitionId = Guid.CreateVersion7(), VersionNumber = 1, OriginalFileName = "x", DetectedContentType = "a/b", PlaintextSha256 = new byte[32], CiphertextSha256 = new byte[32], StorageProviderName = "p", StorageObjectKey = "k", EncryptionKeyReference = "k", WrappedDataKey = new byte[1], EncryptionNoncePrefix = new byte[8], ScanVerdict = "Clean", ScannerName = "s", ScannedOn = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero).AddTicks(1), UploadedByUserId = Guid.CreateVersion7(), UploadedOn = DateTimeOffset.UnixEpoch, ContentChangeOperationId = Guid.CreateVersion7(), RowSignatureSchemaVersion = 1 }, 1));
        Assert.Equal(version.FileAttachmentId, version.ItemId);
        Assert.Equal(ContentItemKindCodes.Attachment, version.ItemKindCode);

        foreach (var schema in BusinessSignedSchemas.All)
        {
            Assert.True(schema.Columns.Select(c => c.ColumnName).SequenceEqual(schema.Columns.Select(c => c.ColumnName).Order(StringComparer.Ordinal)), schema.EntityKind);
        }
    }

    [Fact]
    public async Task AesGcmEnvelopeRoundTripsAcrossSegmentsAndDetectsTamperingOrTransplant()
    {
        var keys = new LocalDevelopmentKeyProvider(Options.Create(new EncryptionOptions { KeyProvider = "LocalDevelopment", LocalDevelopmentKekBase64 = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)) }));
        var service = new AesGcmFileEncryptionService(keys);
        var plaintext = RandomNumberGenerator.GetBytes((2 * 1024 * 1024) + 777); // three segments, last partial
        var context = new FileEncryptionContext(Tenant, Guid.CreateVersion7(), Guid.CreateVersion7(), 1);
        using var cipher = new MemoryStream();
        var outcome = await service.EncryptAsync(new MemoryStream(plaintext), cipher, context, TestContext.Current.CancellationToken);
        Assert.Equal(plaintext.Length, outcome.PlaintextSizeBytes);
        Assert.Equal(SHA256.HashData(plaintext), outcome.PlaintextSha256);
        Assert.Equal(cipher.Length, outcome.CiphertextSizeBytes);
        Assert.False(cipher.ToArray().AsSpan(4, 64).SequenceEqual(plaintext.AsSpan(0, 64)));

        using var plain = new MemoryStream();
        await service.DecryptAsync(new MemoryStream(cipher.ToArray()), plain, outcome.Envelope, context, TestContext.Current.CancellationToken);
        Assert.Equal(plaintext, plain.ToArray());

        var tampered = cipher.ToArray();
        tampered[4 + (1024 * 1024) + 100] ^= 0x01;
        await Assert.ThrowsAnyAsync<CryptographicException>(() => service.DecryptAsync(new MemoryStream(tampered), new MemoryStream(), outcome.Envelope, context, TestContext.Current.CancellationToken));

        var transplanted = context with { FormRecordId = Guid.CreateVersion7() };
        await Assert.ThrowsAnyAsync<CryptographicException>(() => service.DecryptAsync(new MemoryStream(cipher.ToArray()), new MemoryStream(), outcome.Envelope, transplanted, TestContext.Current.CancellationToken));

        var truncated = cipher.ToArray()[..(cipher.ToArray().Length - 20)];
        await Assert.ThrowsAnyAsync<CryptographicException>(() => service.DecryptAsync(new MemoryStream(truncated), new MemoryStream(), outcome.Envelope, context, TestContext.Current.CancellationToken));

        var emptyKeys = new LocalDevelopmentKeyProvider(Options.Create(new EncryptionOptions()));
        await Assert.ThrowsAsync<EncryptionUnavailableException>(() => new AesGcmFileEncryptionService(emptyKeys).EncryptAsync(new MemoryStream(plaintext), new MemoryStream(), context, TestContext.Current.CancellationToken));
    }

    [Fact]
    public void ClamAvRepliesMapToVerdictsAndUnknownRepliesFailClosed()
    {
        var scanner = new ClamAvTcpScanner(Options.Create(new MalwareScannerOptions()));
        Assert.Equal(ScanVerdicts.Clean, scanner.Parse("stream: OK").Verdict);
        var infected = scanner.Parse("stream: Win.Test.EICAR_HDB-1 FOUND");
        Assert.Equal(ScanVerdicts.Infected, infected.Verdict);
        Assert.Equal("Win.Test.EICAR_HDB-1", infected.SignatureName);
        Assert.Equal(ScanVerdicts.ScanError, scanner.Parse("INSTREAM size limit exceeded. ERROR").Verdict);
        Assert.Throws<ScannerUnavailableException>(() => scanner.Parse("garbage"));
    }

    [Fact]
    public void ThemeContrastRejectsInaccessiblePairsAndAcceptsTheDefault()
    {
        Assert.Empty(ThemeContrast.Validate("#111111", "#FFFFFF", "#FFFFFF", "#111111", "#111111", "Light"));
        var failures = ThemeContrast.Validate("#FFFF00", "#FFFFFF", "#FFFFFF", "#CCCCCC", "#EEEEEE", "Light");
        Assert.Contains(failures, f => f.StartsWith("primary button text", StringComparison.Ordinal));
        Assert.Contains(failures, f => f.StartsWith("secondary button border", StringComparison.Ordinal));
        Assert.Throws<BusinessException>(() => ThemeContrast.Normalize("red"));
    }
}
