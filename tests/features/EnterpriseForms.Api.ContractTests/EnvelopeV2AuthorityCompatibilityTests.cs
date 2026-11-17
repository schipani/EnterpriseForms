using System.Security.Cryptography;
using AuthorizationAuthority.Application.Features.Content;
using EnterpriseForms.Application.Features.Documents.Ports;
using EnterpriseForms.Application.Features.FormRecords.Encryption;
using EnterpriseForms.Application.Features.FormRecords.Integrity;
using EnterpriseForms.Domain.Features.Documents;
using EnterpriseForms.Domain.Features.FormRecords;
using EnterpriseForms.Infrastructure.Features.Documents;
using EnterpriseForms.Infrastructure.Features.Documents.EnvelopeV2;
using EnterpriseForms.Integrity.Canonical;
using Microsoft.Extensions.Options;
using Xunit;

namespace EnterpriseForms.Api.ContractTests;

/// <summary>
/// E3-M2d compatibility across the trust boundary: Envelope v2 rows produced by the real E3-M1 core (the only v2 producer; no product
/// v2 writer exists) with the real local key provider, encoded by the business signed-column registry exactly as they would be
/// sent to the Authority, satisfy the Authority's <see cref="EnvelopeV2Rules"/> shape and batch rules; the codec-derived field bound
/// (D-8) admits the largest legitimate value and refuses one byte more; the document size formula equals the cipher's output; and
/// the Authority's copy of the M1 registry equals the business definitions. (Acceptance is still refused: that is the Prepare-only
/// rule, covered in the Authority tests.)
/// </summary>
public sealed class EnvelopeV2AuthorityCompatibilityTests
{
    private static readonly Guid Org = Guid.Parse("019a5b2c-c000-7000-8000-0000000000d2");
    private static readonly Guid Record = Guid.Parse("019a5b2c-c000-7000-8000-0000000000d3");
    private static readonly Guid Field = Guid.Parse("019a5b2c-c000-7000-8000-0000000000d4");
    private static readonly byte[] Kek = RandomNumberGenerator.GetBytes(32);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static LocalDevelopmentKeyProvider Keys() => new(Options.Create(new EncryptionOptions
    {
        KeyProvider = EncryptionOptions.LocalDevelopmentProviderName,
        LocalDevelopmentKekBase64 = Convert.ToBase64String(Kek),
        LocalDevelopmentKeyReference = EnvelopeRules.LegacyKeyReference,
    }));

    private static RowCanonicalDocument AsSentToTheAuthority(ISignedContentRow row) => RowCanonicalDecoder.Decode(BusinessSignedSchemas.Encode(row, 1));

    /// <summary>A v2 field row as the business would stage it (signed schema 2, every v2 column from the M1 envelope).</summary>
    private static FieldEncryptedValue V2Row(FieldSealBatchV2 batch, string logicalType, string plaintext)
    {
        var envelope = batch.Seal(Field, Guid.CreateVersion7(), logicalType, plaintext);
        return new FieldEncryptedValue
        {
            FieldEncryptedValueId = Guid.CreateVersion7(), TenantId = envelope.Header.Prefix.OrganizationId, FormRecordId = envelope.Identity.FormRecordId,
            FieldDefinitionId = envelope.Identity.FieldDefinitionId, FieldValueItemId = envelope.Identity.FieldValueItemId, EncryptedLogicalType = envelope.Identity.LogicalType,
            EncryptedValueBytes = envelope.Ciphertext.ToArray(), EncryptionNonceBytes = envelope.Nonce.ToArray(), EncryptionAuthTagBytes = envelope.Tag.ToArray(),
            WrappedDataKeyBytes = envelope.WrappedSeed.ToArray(), EncryptionAlgorithmName = "AES-256-GCM", EncryptionKeyReference = envelope.Header.Prefix.KekReference,
            EncryptionEnvelopeVersion = 2, RowSignatureSchemaVersion = 2,
            DataKeyIdentifier = envelope.Header.Prefix.DataKeyIdentifier, DataKeyCommitmentBytes = envelope.Header.KeyCommitment.ToArray(), KeyWrapAlgorithmCode = (int)envelope.Header.Prefix.WrapAlgorithm,
            ContentChangeOperationId = Guid.CreateVersion7(), RowRevision = 1,
        };
    }

    [Fact]
    public async Task AMultiItemFieldBatchSealedByTheM1CoreIsAWellFormedV2BatchForTheAuthority()
    {
        using var batch = await FieldSealBatchV2.CreateAsync(Keys(), Org, Record, EncryptionKeyPurpose.FieldValue, Ct);
        var rows = new[]
        {
            V2Row(batch, "String", "Dupont"),
            V2Row(batch, "String", string.Empty), // AES-GCM of the empty string: 0 ciphertext bytes, legitimate in v2
            V2Row(batch, "String", new string('€', 4_000)), // D-8 worst case: 4,000 code units × 3 UTF-8 bytes = 12,000
            V2Row(batch, "Integer", "-9223372036854775808"),
            V2Row(batch, "Decimal", "-999999999999999999.9999"),
            V2Row(batch, "Date", "2026-11-15"),
            V2Row(batch, "DateTime", "2026-11-15T10:00:00Z"),
            V2Row(batch, "Boolean", "false"),
            V2Row(batch, "Guid", "019a5b2c-d000-7000-8000-00000000000d"),
            V2Row(batch, "Option", new string('z', 64)),
        };

        Assert.Equal(12_000, rows[2].EncryptedValueBytes.Length);
        Assert.Empty(rows[1].EncryptedValueBytes);
        var documents = rows.Select(AsSentToTheAuthority).ToList();
        Assert.All(documents, d => Assert.True(EnvelopeV2Rules.IsV2(d)));
        Assert.All(documents, d => Assert.Null(EnvelopeV2Rules.Validate(d)));
        EnvelopeRules.Enforce(documents); // the dispatching entry point: one batch, one wrapped key, distinct items and nonces
        Assert.Single(rows.Select(r => r.DataKeyIdentifier).Distinct());
        Assert.Equal($"{LocalDevelopmentKeyProvider.ScopedReferencePrefix}:{Org:D}:{EnvelopeRules.FieldValuePurposeToken}:1", rows[0].EncryptionKeyReference);
    }

    [Fact]
    public async Task OneByteAboveTheCodecBoundIsRefusedByTheAuthority()
    {
        // The codec never produces it (FieldValueCodec caps String values at 4,000 code units); the M1 core would seal it, the Authority refuses it.
        using var batch = await FieldSealBatchV2.CreateAsync(Keys(), Org, Record, EncryptionKeyPurpose.FieldValue, Ct);
        var row = V2Row(batch, "String", new string('€', 4_000) + "x");
        Assert.Equal(12_001, row.EncryptedValueBytes.Length);
        Assert.Equal(EnvelopeV2Rules.Reasons.CiphertextLength, EnvelopeV2Rules.Validate(AsSentToTheAuthority(row)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(37)]
    [InlineData(EnvelopeV2Format.SegmentSize)]
    [InlineData(EnvelopeV2Format.SegmentSize + 5)]
    public async Task ADocumentEncryptedByTheM1CoreHasTheSizeTheAuthorityExpects(int size)
    {
        var plaintext = RandomNumberGenerator.GetBytes(size);
        using var sink = new MemoryStream();
        var attachment = Guid.CreateVersion7();
        var outcome = await DocumentEnvelopeV2Cipher.EncryptAsync(new MemoryStream(plaintext), sink, Keys(), Org, new DocumentVersionIdentity(Record, attachment, 1), Ct);
        Assert.Equal(outcome.CiphertextSizeBytes, sink.Length);
        Assert.Equal(outcome.CiphertextSizeBytes, EnvelopeV2Rules.ExpectedDocumentCiphertextSize(outcome.PlaintextSizeBytes));

        var e = outcome.Envelope;
        var version = new FileAttachmentVersion
        {
            FileAttachmentVersionId = Guid.CreateVersion7(), TenantId = Org, FormRecordId = Record, FileAttachmentId = attachment, FieldDefinitionId = Field, VersionNumber = 1,
            OriginalFileName = "statement.pdf", DetectedContentType = "application/pdf",
            PlaintextSizeBytes = outcome.PlaintextSizeBytes, PlaintextSha256 = outcome.PlaintextSha256, CiphertextSizeBytes = outcome.CiphertextSizeBytes, CiphertextSha256 = outcome.CiphertextSha256,
            StorageProviderName = "azurite", StorageObjectKey = "objects/v2", EncryptionAlgorithmName = "AES-256-GCM", EncryptionKeyReference = e.Header.Prefix.KekReference,
            EncryptionEnvelopeVersion = 2, WrappedDataKey = e.WrappedSeed.ToArray(), EncryptionNoncePrefix = e.Context.NoncePrefix.ToArray(), EncryptionSegmentSizeBytes = e.Context.SegmentSizeBytes,
            DataKeyIdentifier = e.Header.Prefix.DataKeyIdentifier, DataKeyCommitmentBytes = e.Header.KeyCommitment.ToArray(), KeyWrapAlgorithmCode = (int)e.Header.Prefix.WrapAlgorithm,
            ScanVerdict = ScanVerdicts.Clean, ScannerName = "clamav", ScannedOn = DateTimeOffset.UnixEpoch, UploadedByUserId = Guid.CreateVersion7(), UploadedOn = DateTimeOffset.UnixEpoch,
            ContentChangeOperationId = Guid.CreateVersion7(), RowRevision = 1, RowSignatureSchemaVersion = 2,
        };

        var document = AsSentToTheAuthority(version);
        Assert.Null(EnvelopeV2Rules.Validate(document));
        EnvelopeRules.Enforce([document]);
    }

    [Fact]
    public void TheAuthorityCopyOfTheM1RegistryEqualsTheBusinessDefinitions()
    {
        Assert.Equal(EnvelopeV2Format.CommitmentSize, EnvelopeV2Rules.CommitmentLength);
        Assert.Equal(EnvelopeV2Format.SegmentSize, EnvelopeRules.DocumentSegmentSize);
        Assert.Equal(EnvelopeV2Format.NoncePrefixSize, EnvelopeRules.DocumentNoncePrefixLength);
        Assert.Equal(EnvelopeV2Format.NonceSize, EnvelopeRules.FieldNonceLength);
        Assert.Equal(EnvelopeV2Format.TagSize, EnvelopeRules.FieldTagLength);
        Assert.Equal(EnvelopeV2Format.FormatVersion, EnvelopeV2Rules.EnvelopeVersionV2);
        Assert.True(EnvelopeV2Rules.MaximumFieldCiphertextBytes <= EnvelopeV2Format.MaximumFieldCiphertextBytes); // the Authority is the tighter bound
        Assert.Equal(EnvelopeV2Format.FieldLogicalTypes.Order(StringComparer.Ordinal), EnvelopeRules.LogicalTypes.Order(StringComparer.Ordinal));

        var codes = Enum.GetValues<EnvelopeV2WrapAlgorithm>().Select(w => (int)w).Order().ToList();
        Assert.Equal(codes, EnvelopeV2Rules.WrapAlgorithms.Keys.Order().ToList());
        foreach (var wrap in Enum.GetValues<EnvelopeV2WrapAlgorithm>())
        {
            var authority = EnvelopeV2Rules.WrapAlgorithms[(int)wrap];
            Assert.Equal(EnvelopeV2Format.ReferencePrefixOf(wrap), authority.Family);
            Assert.Equal(EnvelopeV2Format.WrappedSeedLengthOf(wrap), authority.WrappedLength);
        }
    }
}
