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
using EnterpriseForms.Infrastructure.Features.FormRecords;
using EnterpriseForms.Domain.Features.Organizations;
using EnterpriseForms.Application.Features.FormRecords.Encryption;
using EnterpriseForms.Application.Features.FormDefinitions.Protection;
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
    public void LayoutSectionsCarryOptionalDescriptionAndFieldPlaceholderWithoutChangingLegacySerialization()
    {
        var fieldId = Guid.CreateVersion7();
        var legacy = $$$"""{"layoutSchemaVersion":2,"title":"Accused","sections":[{"sectionKey":"main","title":"Main","gridColumns":4,"elements":[{"kind":"field","fieldDefinitionId":"{{{fieldId}}}","control":"text","label":"Given name","layout":{"columnSpan":2,"startOnNewRow":false}}]}]}""";
        var legacySerialized = FormLayoutCodec.Serialize(FormLayoutCodec.Parse(legacy));
        Assert.DoesNotContain("description", legacySerialized, StringComparison.Ordinal);
        Assert.DoesNotContain("placeholder", legacySerialized, StringComparison.Ordinal);

        // C1 designer rebuild: "+ Add Row" creates a titled section; several sections with their own fields round-trip.
        var otherId = Guid.CreateVersion7();
        var json = $$$"""{"layoutSchemaVersion":2,"title":"Accused","sections":[{"sectionKey":"main","title":"Personal Information","gridColumns":4,"elements":[{"kind":"field","fieldDefinitionId":"{{{fieldId}}}","control":"text","label":"Given name","placeholder":"As on the charge sheet","layout":{"columnSpan":1,"startOnNewRow":false}}]},{"sectionKey":"section_1a2b3c4d","title":"Contact Information","description":"Where the accused can be reached","gridColumns":4,"elements":[{"kind":"field","fieldDefinitionId":"{{{otherId}}}","control":"text","label":"City","layout":{"columnSpan":2,"startOnNewRow":false}}]},{"sectionKey":"section_empty","title":"New section","gridColumns":4,"elements":[]}]}""";
        var layout = FormLayoutCodec.Parse(json);
        Assert.Equal(3, layout.Sections.Count);
        Assert.Equal("Contact Information", layout.Sections[1].Title);
        Assert.Equal("Where the accused can be reached", layout.Sections[1].Description);
        Assert.Null(layout.Sections[0].Description);
        Assert.Equal("As on the charge sheet", layout.Elements.First().Placeholder);
        Assert.Equal(otherId, layout.Sections[1].Elements[0].FieldDefinitionId);
        var serialized = FormLayoutCodec.Serialize(layout);
        Assert.Equal(serialized, FormLayoutCodec.Serialize(FormLayoutCodec.Parse(serialized)));

        Assert.Throws<BusinessException>(() => FormLayoutCodec.Parse(json.Replace("Where the accused", "<b>Where</b> the accused", StringComparison.Ordinal)));
        Assert.Throws<BusinessException>(() => FormLayoutCodec.Parse(json.Replace("As on the charge sheet", new string('x', 201), StringComparison.Ordinal)));
        // A field may be placed in only one section.
        Assert.Throws<BusinessException>(() => FormLayoutCodec.Parse(json.Replace(otherId.ToString(), fieldId.ToString(), StringComparison.Ordinal)));
    }

    [Fact]
    public void AcceptedContentProjectionNeverMovesBackwardsWhenCommitsFinishOutOfOrder()
    {
        var record = new FormRecord { RecordRevision = 1 };
        var first = Guid.CreateVersion7();
        var third = Guid.CreateVersion7();
        Assert.True(record.RecordAcceptedContent(1, first)); // creation: revision 1, no projected operation yet
        Assert.Equal(first, record.AcceptedContentChangeOperationId);
        Assert.True(record.RecordAcceptedContent(3, third));
        Assert.False(record.RecordAcceptedContent(2, Guid.CreateVersion7())); // a slower commit of an older revision finishes last
        Assert.False(record.RecordAcceptedContent(3, Guid.CreateVersion7())); // the projected head is not replaced at the same revision
        Assert.Equal(3, record.RecordRevision);
        Assert.Equal(third, record.AcceptedContentChangeOperationId);
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
        var instant = FieldValueCodec.CreateRow(Field("arrest_time", FieldDataTypes.DateTime), null, options, "2026-03-08T19:30:00-05:00", Tenant, record, Guid.CreateVersion7(), now, Tenant);
        Assert.Equal("2026-03-09T00:30:00Z", FieldValueCodec.Format(instant, byId)); // stored and signed as UTC
        var reference = Guid.CreateVersion7();
        var g = FieldValueCodec.CreateRow(Field("external_reference", FieldDataTypes.Guid), null, options, reference.ToString("D").ToUpperInvariant(), Tenant, record, Guid.CreateVersion7(), now, Tenant);
        Assert.Equal(reference.ToString("D"), FieldValueCodec.Format(g, byId));
        Assert.Equal(BusinessSignedSchemas.FieldDateTimeValuesV1.SchemaVersion, BusinessSignedSchemas.CurrentSchemaVersion(instant));
        Assert.Equal(BusinessSignedSchemas.FieldGuidValuesV1.SchemaVersion, BusinessSignedSchemas.CurrentSchemaVersion(g));
        instant.RowRevision = 1;
        g.RowRevision = 1;
        Assert.Equal("field_date_time_values", RowCanonicalHeader.Parse(BusinessSignedSchemas.Encode(instant, 1)).EntityKind);
        Assert.Equal("field_guid_values", RowCanonicalHeader.Parse(BusinessSignedSchemas.Encode(g, 1)).EntityKind);
        Assert.Throws<BusinessException>(() => FieldValueCodec.CreateRow(Field("arrest_time", FieldDataTypes.DateTime), null, options, "2026-03-08 19:30", Tenant, record, Guid.CreateVersion7(), now, Tenant)); // no offset
        Assert.Throws<BusinessException>(() => FieldValueCodec.CreateRow(Field("arrest_time", FieldDataTypes.DateTime), null, options, "2026-03-08T19:30:00.123Z", Tenant, record, Guid.CreateVersion7(), now, Tenant)); // sub-second is not silently truncated
        Assert.Throws<BusinessException>(() => FieldValueCodec.CreateRow(Field("external_reference", FieldDataTypes.Guid), null, options, "{" + reference.ToString("D") + "}", Tenant, record, Guid.CreateVersion7(), now, Tenant));
        Assert.Throws<BusinessException>(() => FieldValueCodec.CreateRow(Field("external_reference", FieldDataTypes.Guid), null, options, Guid.Empty.ToString("D"), Tenant, record, Guid.CreateVersion7(), now, Tenant));

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
    public void AuthorizedLayoutKeepsOnlySharedFieldsAndDropsChildListsEmptySectionsAndUnsharedDocuments()
    {
        var shared = Guid.CreateVersion7();
        var hidden = Guid.CreateVersion7();
        var documents = Guid.CreateVersion7();
        var placement = new LayoutPlacement(4, false);
        var validation = new LayoutValidation(false, null, null, null, null, null, null, null);
        var layout = new FormLayout(2, "Accused",
        [
            new LayoutSection("identity", "Identity", 4, [new LayoutElement("field", shared, null, "text", "Given name", null, placement, validation, null, null), new LayoutElement("field", hidden, null, "date", "Date of birth", null, placement, validation, null, null)]),
            new LayoutSection("related", "Related", 4, [new LayoutElement("relatedFormList", null, Guid.CreateVersion7(), null, "Addresses", null, placement, validation, null, ["street_address"]), new LayoutElement("documentCollection", documents, null, "fileUpload", "Case documents", null, placement, validation, new LayoutUploadPolicy(true, 5, 1024, ["text/plain"]), null)]),
        ]);
        var reduced = EnterpriseForms.Application.Features.FormRecords.GetFormRecord.GetRecordProjectionHandler.AuthorizedLayout(layout, new HashSet<Guid> { shared }, new HashSet<Guid>());
        var element = Assert.Single(Assert.Single(reduced.Sections).Elements);
        Assert.Equal(shared, element.FieldDefinitionId);
        var json = FormLayoutCodec.Serialize(reduced);
        Assert.DoesNotContain("Date of birth", json, StringComparison.Ordinal);
        Assert.DoesNotContain("Addresses", json, StringComparison.Ordinal);
        var withDocuments = EnterpriseForms.Application.Features.FormRecords.GetFormRecord.GetRecordProjectionHandler.AuthorizedLayout(layout, new HashSet<Guid> { shared }, new HashSet<Guid> { documents });
        Assert.Equal(2, withDocuments.Sections.Count);
        Assert.Equal("documentCollection", Assert.Single(withDocuments.Sections[1].Elements).Kind);
    }

    [Fact]
    public void FieldEncryptionSealsCanonicalValuesBindsIdentityAndFailsClosed()
    {
        var keys = new LocalDevelopmentKeyProvider(Options.Create(new EncryptionOptions { KeyProvider = "LocalDevelopment", LocalDevelopmentKekBase64 = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)) }));
        var service = new AesGcmFieldEncryptionService(keys);
        var context = new FieldEncryptionContext(Tenant, Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), "String");
        var sealedRow = service.Encrypt(context, "Avery Synthetic");
        Assert.Equal("AES-256-GCM", sealedRow.EncryptionAlgorithmName);
        Assert.Equal(12, sealedRow.EncryptionNonceBytes.Length);
        Assert.Equal(16, sealedRow.EncryptionAuthTagBytes.Length);
        Assert.DoesNotContain("Avery", System.Text.Encoding.Latin1.GetString(sealedRow.EncryptedValueBytes), StringComparison.Ordinal);
        Assert.Equal("Avery Synthetic", service.Decrypt(sealedRow));

        // Two seals of the same plaintext never share nonce or ciphertext (per-value DEK, random nonce).
        var again = service.Encrypt(context, "Avery Synthetic");
        Assert.NotEqual(sealedRow.EncryptionNonceBytes, again.EncryptionNonceBytes);
        Assert.NotEqual(sealedRow.EncryptedValueBytes, again.EncryptedValueBytes);

        // AAD binds tenant/record/field/item/type: a transplanted envelope is rejected without a plaintext.
        foreach (var mutate in new Action<FieldEncryptedValue>[] { v => v.FormRecordId = Guid.CreateVersion7(), v => v.FieldDefinitionId = Guid.CreateVersion7(), v => v.FieldValueItemId = Guid.CreateVersion7(), v => v.TenantId = Guid.CreateVersion7(), v => v.EncryptedLogicalType = "Integer" })
        {
            var transplanted = Clone(sealedRow);
            mutate(transplanted);
            Assert.ThrowsAny<CryptographicException>(() => service.Decrypt(transplanted));
        }

        // Hostile byte flips in ciphertext, tag or wrapped key fail closed.
        var tampered = Clone(sealedRow);
        tampered.EncryptedValueBytes[0] ^= 0x01;
        Assert.ThrowsAny<CryptographicException>(() => service.Decrypt(tampered));
        var tagged = Clone(sealedRow);
        tagged.EncryptionAuthTagBytes[3] ^= 0x01;
        Assert.ThrowsAny<CryptographicException>(() => service.Decrypt(tagged));
        var rekeyed = Clone(sealedRow);
        rekeyed.WrappedDataKeyBytes[^1] ^= 0x01;
        Assert.ThrowsAny<CryptographicException>(() => service.Decrypt(rekeyed));

        // Unknown key reference and a missing/unconfigured provider fail closed (never a silent plaintext fallback).
        var unknownKey = Clone(sealedRow);
        unknownKey.EncryptionKeyReference = "some-other-kek";
        Assert.Throws<EncryptionUnavailableException>(() => service.Decrypt(unknownKey));
        var unavailable = new AesGcmFieldEncryptionService(new UnavailableKeyProvider("test: no provider"));
        Assert.False(unavailable.IsAvailable);
        Assert.Throws<EncryptionUnavailableException>(() => unavailable.Encrypt(context, "x"));
        Assert.Throws<EncryptionUnavailableException>(() => unavailable.Decrypt(sealedRow));

        // The signed schema binds the exact envelope (row-integrity-format §3.7).
        sealedRow.RowRevision = 1;
        Assert.Equal("field_encrypted_values", RowCanonicalHeader.Parse(BusinessSignedSchemas.Encode(sealedRow, 1)).EntityKind);
        Assert.Equal(BusinessSignedSchemas.FieldEncryptedValuesV1.SchemaVersion, BusinessSignedSchemas.CurrentSchemaVersion(sealedRow));
        var canonicalBefore = BusinessSignedSchemas.Encode(sealedRow, 1);
        sealedRow.EncryptedValueBytes[^1] ^= 0x01;
        Assert.NotEqual(canonicalBefore, BusinessSignedSchemas.Encode(sealedRow, 1));

        // Plaintext formatting paths refuse encrypted rows unless the encryption service is supplied.
        Assert.Throws<InvalidOperationException>(() => FieldValueCodec.Format(sealedRow, new Dictionary<Guid, FieldOption>()));
        Assert.Throws<InvalidOperationException>(() => FieldValueCodec.Format(sealedRow, new Dictionary<Guid, FieldOption>(), null));

        static FieldEncryptedValue Clone(FieldEncryptedValue v) => new()
        {
            FieldEncryptedValueId = v.FieldEncryptedValueId, TenantId = v.TenantId, FormRecordId = v.FormRecordId, FieldDefinitionId = v.FieldDefinitionId, FieldValueItemId = v.FieldValueItemId,
            EncryptedValueBytes = [.. v.EncryptedValueBytes], EncryptionNonceBytes = [.. v.EncryptionNonceBytes], EncryptionAuthTagBytes = [.. v.EncryptionAuthTagBytes], WrappedDataKeyBytes = [.. v.WrappedDataKeyBytes],
            EncryptionAlgorithmName = v.EncryptionAlgorithmName, EncryptionKeyReference = v.EncryptionKeyReference, EncryptionEnvelopeVersion = v.EncryptionEnvelopeVersion, EncryptedLogicalType = v.EncryptedLogicalType,
        };
    }

    [Fact]
    public void FieldProtectionPolicyDefaultsFreeTextToEncryptionEnforcesTheFloorAndNeverDowngrades()
    {
        var policy = new OrganizationSecurityPolicy { MinimumFieldClassification = "Internal", PlaintextInternalFieldsPermitted = true };
        Assert.Equal(("Confidential", "ApplicationEncrypted"), FieldProtectionPolicy.Resolve("String", null, null, policy));
        Assert.Equal(("Confidential", "ApplicationEncrypted"), FieldProtectionPolicy.Resolve("File", null, null, policy));
        Assert.Equal(("Internal", "Standard"), FieldProtectionPolicy.Resolve("Integer", null, null, policy));
        Assert.Equal(("Internal", "Standard"), FieldProtectionPolicy.Resolve("String", "Internal", "Standard", policy));
        Assert.Equal(("Internal", "ApplicationEncrypted"), FieldProtectionPolicy.Resolve("Integer", "Internal", "ApplicationEncrypted", policy));
        Assert.Equal(("Restricted", "ApplicationEncrypted"), FieldProtectionPolicy.Resolve("String", "Restricted", null, policy));
        Assert.Equal("FIELD_PROTECTION_REQUIRED", Assert.Throws<BusinessException>(() => FieldProtectionPolicy.Resolve("String", "Confidential", "Standard", policy)).ErrorCode);
        Assert.Equal("CLASSIFICATION_BELOW_FLOOR", Assert.Throws<BusinessException>(() => FieldProtectionPolicy.Resolve("String", "Public", "Standard", policy)).ErrorCode);
        Assert.Equal("INVALID_REQUEST", Assert.Throws<BusinessException>(() => FieldProtectionPolicy.Resolve("String", "Secret", null, policy)).ErrorCode);

        var strict = new OrganizationSecurityPolicy { MinimumFieldClassification = "Internal", PlaintextInternalFieldsPermitted = false };
        Assert.Equal("PLAINTEXT_NOT_PERMITTED", Assert.Throws<BusinessException>(() => FieldProtectionPolicy.Resolve("Integer", "Internal", "Standard", strict)).ErrorCode);
        Assert.Equal("PLAINTEXT_NOT_PERMITTED", Assert.Throws<BusinessException>(() => FieldProtectionPolicy.Resolve("Integer", null, null, strict)).ErrorCode);
        Assert.Equal(("Internal", "ApplicationEncrypted"), FieldProtectionPolicy.Resolve("Integer", "Internal", "ApplicationEncrypted", strict));
        var floorConfidential = new OrganizationSecurityPolicy { MinimumFieldClassification = "Confidential", PlaintextInternalFieldsPermitted = true };
        Assert.Equal("CLASSIFICATION_BELOW_FLOOR", Assert.Throws<BusinessException>(() => FieldProtectionPolicy.Resolve("Integer", "Internal", null, floorConfidential)).ErrorCode);

        var plaintext = new FieldDefinition { FieldKey = "note_text", FieldDataType = "String", FieldClassification = "Internal", FieldProtectionMode = "Standard" };
        Assert.Equal(("Confidential", "ApplicationEncrypted"), FieldProtectionPolicy.ResolveStrengthening(plaintext, "Confidential", null, policy));
        Assert.Equal(("Internal", "ApplicationEncrypted"), FieldProtectionPolicy.ResolveStrengthening(plaintext, null, "ApplicationEncrypted", policy));
        Assert.Equal("NO_PROTECTION_CHANGE", Assert.Throws<BusinessException>(() => FieldProtectionPolicy.ResolveStrengthening(plaintext, "Internal", "Standard", policy)).ErrorCode);
        var encrypted = new FieldDefinition { FieldKey = "secret_text", FieldDataType = "String", FieldClassification = "Confidential", FieldProtectionMode = "ApplicationEncrypted" };
        Assert.Equal("FIELD_PROTECTION_DOWNGRADE_REFUSED", Assert.Throws<BusinessException>(() => FieldProtectionPolicy.ResolveStrengthening(encrypted, "Internal", "Standard", policy)).ErrorCode);
        Assert.Equal("FIELD_PROTECTION_DOWNGRADE_REFUSED", Assert.Throws<BusinessException>(() => FieldProtectionPolicy.ResolveStrengthening(encrypted, "Confidential", "Standard", policy)).ErrorCode);
        Assert.Equal(("Restricted", "ApplicationEncrypted"), FieldProtectionPolicy.ResolveStrengthening(encrypted, "Restricted", null, policy));
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
    public async Task DocumentEncryptionUsesAFreshKeyPerVersionAndRejectsSegmentTruncationReorderingAndVersionTransplant()
    {
        var ct = TestContext.Current.CancellationToken;
        var keys = new LocalDevelopmentKeyProvider(Options.Create(new EncryptionOptions { KeyProvider = "LocalDevelopment", LocalDevelopmentKekBase64 = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)) }));
        var service = new AesGcmFileEncryptionService(keys);
        var context = new FileEncryptionContext(Tenant, Guid.CreateVersion7(), Guid.CreateVersion7(), 1);
        const int Record = 4 + AesGcmFileEncryptionService.SegmentSize + 16;

        async Task<(byte[] Cipher, EncryptionOutcome Outcome)> SealAsync(byte[] plaintext, FileEncryptionContext c)
        {
            using var sink = new MemoryStream();
            var outcome = await service.EncryptAsync(new MemoryStream(plaintext), sink, c, ct);
            return (sink.ToArray(), outcome);
        }

        async Task<byte[]> OpenAsync(byte[] cipher, FileEncryptionEnvelope envelope, FileEncryptionContext c, AesGcmFileEncryptionService? with = null)
        {
            using var sink = new MemoryStream();
            await (with ?? service).DecryptAsync(new MemoryStream(cipher), sink, envelope, c, ct);
            return sink.ToArray();
        }

        // Identical plaintext sealed twice (e.g. a re-uploaded version): new DEK, new nonce prefix, different ciphertext.
        var twoSegments = RandomNumberGenerator.GetBytes(2 * AesGcmFileEncryptionService.SegmentSize); // exact multiple of the segment size
        var (cipherA, outcomeA) = await SealAsync(twoSegments, context);
        var (cipherB, outcomeB) = await SealAsync(twoSegments, context);
        Assert.False(outcomeA.Envelope.WrappedDataKey.AsSpan().SequenceEqual(outcomeB.Envelope.WrappedDataKey));
        Assert.False(outcomeA.Envelope.NoncePrefix.AsSpan().SequenceEqual(outcomeB.Envelope.NoncePrefix));
        Assert.NotEqual(outcomeA.CiphertextSha256, outcomeB.CiphertextSha256);
        Assert.Equal(outcomeA.PlaintextSha256, outcomeB.PlaintextSha256);
        Assert.Equal(60, outcomeA.Envelope.WrappedDataKey.Length);
        Assert.Equal(8, outcomeA.Envelope.NoncePrefix.Length);
        Assert.Equal(2 * Record, cipherA.Length);
        Assert.Equal(twoSegments, await OpenAsync(cipherA, outcomeA.Envelope, context));

        // Dropping a whole trailing segment is detected (the remaining last segment was not sealed as final).
        await Assert.ThrowsAnyAsync<CryptographicException>(() => OpenAsync(cipherA[..Record], outcomeA.Envelope, context));

        // Reordering whole segments is detected (segment index is in the nonce and the AAD).
        var reordered = cipherA[Record..].Concat(cipherA[..Record]).ToArray();
        await Assert.ThrowsAnyAsync<CryptographicException>(() => OpenAsync(reordered, outcomeA.Envelope, context));

        // Splicing a segment from another version of the same document is detected.
        var spliced = cipherA[..Record].Concat(cipherB[Record..]).ToArray();
        await Assert.ThrowsAnyAsync<CryptographicException>(() => OpenAsync(spliced, outcomeA.Envelope, context));

        // A version's ciphertext cannot be served as another version or attachment, or under another tenant.
        await Assert.ThrowsAnyAsync<CryptographicException>(() => OpenAsync(cipherA, outcomeA.Envelope, context with { VersionNumber = 2 }));
        await Assert.ThrowsAnyAsync<CryptographicException>(() => OpenAsync(cipherA, outcomeA.Envelope, context with { FileAttachmentId = Guid.CreateVersion7() }));
        await Assert.ThrowsAnyAsync<CryptographicException>(() => OpenAsync(cipherA, outcomeA.Envelope, context with { TenantId = Guid.CreateVersion7() }));

        // Another document's wrapped key or nonce prefix does not open this ciphertext.
        await Assert.ThrowsAnyAsync<CryptographicException>(() => OpenAsync(cipherA, outcomeA.Envelope with { WrappedDataKey = outcomeB.Envelope.WrappedDataKey }, context));
        await Assert.ThrowsAnyAsync<CryptographicException>(() => OpenAsync(cipherA, outcomeA.Envelope with { NoncePrefix = outcomeB.Envelope.NoncePrefix }, context));

        // A different KEK cannot unwrap the DEK; an unknown key reference or no provider fails closed (never plaintext).
        var otherKeys = new LocalDevelopmentKeyProvider(Options.Create(new EncryptionOptions { KeyProvider = "LocalDevelopment", LocalDevelopmentKekBase64 = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)) }));
        await Assert.ThrowsAnyAsync<CryptographicException>(() => OpenAsync(cipherA, outcomeA.Envelope, context, new AesGcmFileEncryptionService(otherKeys)));
        await Assert.ThrowsAsync<EncryptionUnavailableException>(() => OpenAsync(cipherA, outcomeA.Envelope with { KeyReference = "rotated-kek-v2" }, context));
        await Assert.ThrowsAsync<EncryptionUnavailableException>(() => OpenAsync(cipherA, outcomeA.Envelope, context, new AesGcmFileEncryptionService(new UnavailableKeyProvider("no provider"))));
        await Assert.ThrowsAnyAsync<CryptographicException>(() => OpenAsync(cipherA, outcomeA.Envelope with { AlgorithmName = "AES-128-CBC" }, context));

        // Empty file: one authenticated zero-length final segment.
        var (emptyCipher, emptyOutcome) = await SealAsync([], context);
        Assert.Equal(20, emptyCipher.Length);
        Assert.Empty(await OpenAsync(emptyCipher, emptyOutcome.Envelope, context));
        await Assert.ThrowsAnyAsync<CryptographicException>(() => OpenAsync([], emptyOutcome.Envelope, context));
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
