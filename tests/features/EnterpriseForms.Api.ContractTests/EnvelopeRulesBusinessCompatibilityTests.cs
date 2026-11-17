using System.Security.Cryptography;
using System.Text;
using AuthorizationAuthority.Application.Features.Content;
using EnterpriseForms.Application.Features.Documents.Ports;
using EnterpriseForms.Application.Features.FormRecords.Encryption;
using EnterpriseForms.Application.Features.FormRecords.Integrity;
using EnterpriseForms.Domain.Features.Documents;
using EnterpriseForms.Domain.Features.FormRecords;
using EnterpriseForms.Infrastructure.Features.Documents;
using EnterpriseForms.Infrastructure.Features.FormRecords;
using EnterpriseForms.Integrity.Canonical;
using Microsoft.Extensions.Options;
using Xunit;

namespace EnterpriseForms.Api.ContractTests;

/// <summary>
/// E3-M2a compatibility across the trust boundary: the Envelope v1 rows the business actually produces — through the real
/// field and document encryption services and key providers, encoded by the business signed-column registry exactly as they
/// are sent to the Authority — satisfy the Authority's central <see cref="EnvelopeRules"/>, and the Authority's copy of the
/// constants and the key-reference grammar equals the business definitions. A business change that the Authority would refuse
/// fails here, not in a deployed stack.
/// </summary>
public sealed class EnvelopeRulesBusinessCompatibilityTests
{
    private static readonly Guid Org = Guid.Parse("019a5b2c-c000-7000-8000-00000000000c");
    private static readonly byte[] Kek = RandomNumberGenerator.GetBytes(32);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static LocalDevelopmentKeyProvider Keys() => new(Options.Create(new EncryptionOptions
    {
        KeyProvider = EncryptionOptions.LocalDevelopmentProviderName,
        LocalDevelopmentKekBase64 = Convert.ToBase64String(Kek),
        LocalDevelopmentKeyReference = EnvelopeRules.LegacyKeyReference,
    }));

    private static RowCanonicalDocument AsSentToTheAuthority(ISignedContentRow row) => RowCanonicalDecoder.Decode(BusinessSignedSchemas.Encode(row, 1));

    private static async Task<FieldEncryptedValue> SealAsync(string logicalType, string plaintext, EncryptionKeyPurpose purpose)
    {
        var value = await new AesGcmFieldEncryptionService(Keys()).EncryptAsync(new FieldEncryptionContext(Org, Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), logicalType), plaintext, purpose, Ct);
        value.ContentChangeOperationId = Guid.CreateVersion7();
        value.RowRevision = 1;
        value.RowSignatureSchemaVersion = BusinessSignedSchemas.FieldEncryptedValuesV1.SchemaVersion;
        return value;
    }

    [Theory]
    [InlineData("String", "Dupont")]
    [InlineData("String", "a")]
    [InlineData("Integer", "42")]
    [InlineData("Decimal", "-12.5000")]
    [InlineData("Date", "2026-11-14")]
    [InlineData("DateTime", "2026-11-14T10:00:00.000000Z")]
    [InlineData("Boolean", "true")]
    [InlineData("Guid", "019a5b2c-d000-7000-8000-00000000000d")]
    [InlineData("Option", "019a5b2c-e000-7000-8000-00000000000e")]
    public async Task AFieldValueSealedByTheBusinessIsAWellFormedEnvelopeForTheAuthority(string logicalType, string plaintext)
    {
        var value = await SealAsync(logicalType, plaintext, EncryptionKeyPurpose.FieldValue);
        Assert.Equal($"{LocalDevelopmentKeyProvider.ScopedReferencePrefix}:{Org:D}:{EnvelopeRules.FieldValuePurposeToken}:1", value.EncryptionKeyReference);
        Assert.Null(EnvelopeRules.Validate(AsSentToTheAuthority(value)));
    }

    [Fact]
    public async Task ACarriedRowStillNamingTheLegacyReferenceIsAccepted()
    {
        // Every row stored before E-1 names the legacy literal (E3-M2a compatibility assessment); carry-forward keeps it.
        var value = await SealAsync("String", "pre-E-1 value", EncryptionKeyPurpose.FieldValue);
        value.EncryptionKeyReference = EnvelopeRules.LegacyKeyReference;
        Assert.Null(EnvelopeRules.Validate(AsSentToTheAuthority(value)));
    }

    [Fact]
    public async Task AValueSealedUnderAnotherPurposeCannotBeStoredAsAFieldValue()
    {
        // The idempotency key purpose is legitimate for the creation fingerprint (never sent to the Authority), not for a value row.
        var value = await SealAsync("String", "mis-sealed", EncryptionKeyPurpose.IdempotencyProtection);
        Assert.Equal(EnvelopeRules.Reasons.KeyReferencePurpose, EnvelopeRules.Validate(AsSentToTheAuthority(value)));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(37)]
    [InlineData(AesGcmFileEncryptionService.SegmentSize + 5)]
    public async Task ADocumentEncryptedByTheBusinessIsAWellFormedEnvelopeForTheAuthority(int size)
    {
        var version = await EncryptDocumentAsync(size);
        Assert.Equal($"{LocalDevelopmentKeyProvider.ScopedReferencePrefix}:{Org:D}:{EnvelopeRules.DocumentPurposeToken}:1", version.EncryptionKeyReference);
        Assert.Null(EnvelopeRules.Validate(AsSentToTheAuthority(version)));
    }

    /// <summary>A document version row exactly as the business stages it after encrypting <paramref name="size"/> bytes.</summary>
    private static async Task<FileAttachmentVersion> EncryptDocumentAsync(int size)
    {
        var plaintext = new MemoryStream(Encoding.ASCII.GetBytes(new string('x', size)));
        var ciphertext = new MemoryStream();
        var outcome = await new AesGcmFileEncryptionService(Keys()).EncryptAsync(plaintext, ciphertext, new FileEncryptionContext(Org, Guid.CreateVersion7(), Guid.CreateVersion7(), 1), Ct);
        return new FileAttachmentVersion
        {
            FileAttachmentVersionId = Guid.CreateVersion7(),
            TenantId = Org,
            FormRecordId = Guid.CreateVersion7(),
            FileAttachmentId = Guid.CreateVersion7(),
            FieldDefinitionId = Guid.CreateVersion7(),
            VersionNumber = 1,
            OriginalFileName = "statement.pdf",
            DetectedContentType = "application/pdf",
            PlaintextSizeBytes = outcome.PlaintextSizeBytes,
            PlaintextSha256 = outcome.PlaintextSha256,
            CiphertextSizeBytes = outcome.CiphertextSizeBytes,
            CiphertextSha256 = outcome.CiphertextSha256,
            StorageProviderName = "azurite",
            StorageObjectKey = "objects/one",
            EncryptionAlgorithmName = outcome.Envelope.AlgorithmName,
            EncryptionKeyReference = outcome.Envelope.KeyReference,
            EncryptionEnvelopeVersion = outcome.Envelope.EnvelopeVersion,
            WrappedDataKey = outcome.Envelope.WrappedDataKey,
            EncryptionNoncePrefix = outcome.Envelope.NoncePrefix,
            EncryptionSegmentSizeBytes = outcome.Envelope.SegmentSizeBytes,
            ScanVerdict = ScanVerdicts.Clean,
            ScannerName = "clamav",
            ScannedOn = DateTimeOffset.UnixEpoch,
            UploadedByUserId = Guid.CreateVersion7(),
            UploadedOn = DateTimeOffset.UnixEpoch,
            ContentChangeOperationId = Guid.CreateVersion7(),
            RowRevision = 1,
            RowSignatureSchemaVersion = BusinessSignedSchemas.FileAttachmentVersionsV1.SchemaVersion,
        };
    }

    [Fact]
    public void TheAuthorityRulesMatchTheBusinessDefinitions()
    {
        Assert.Equal(AesGcmFieldEncryptionService.AlgorithmName, EnvelopeRules.ApprovedAlgorithm);
        Assert.Equal(AesGcmFileEncryptionService.AlgorithmName, EnvelopeRules.ApprovedAlgorithm);
        Assert.Equal(AesGcmFieldEncryptionService.EnvelopeVersion, EnvelopeRules.EnvelopeVersionV1);
        Assert.Equal(AesGcmFileEncryptionService.EnvelopeVersion, EnvelopeRules.EnvelopeVersionV1);
        Assert.Equal(AesGcmFileEncryptionService.SegmentSize, EnvelopeRules.DocumentSegmentSize);
        Assert.Equal(EnvelopeRules.FieldValuePurposeToken, EncryptionKeyPurpose.FieldValue.Token);
        Assert.Equal(EnvelopeRules.DocumentPurposeToken, EncryptionKeyPurpose.Document.Token);
        Assert.Equal(EnvelopeRules.LegacyKeyReference, new EncryptionOptions().LocalDevelopmentKeyReference);
        Assert.True(EnvelopeRules.ScopedReferenceFamilies.SetEquals([LocalDevelopmentKeyProvider.ScopedReferencePrefix, VaultTransitKeyProvider.ReferencePrefix]));
        Assert.True(EnvelopeRules.LogicalTypes.SetEquals(["String", "Integer", "Decimal", "Date", "DateTime", "Boolean", "Guid", "Option"]));
    }

    [Fact]
    public async Task EveryReferenceTheBusinessGrammarProducesForAValueOrDocumentIsAcceptedByTheAuthority()
    {
        // Through whole rows (the reference rule itself is private to the Authority): each family and version the business
        // grammar can produce, on a field row and on a document row of the matching purpose.
        var field = await SealAsync("String", "value", EncryptionKeyPurpose.FieldValue);
        var document = await EncryptDocumentAsync(37);
        foreach (var family in new[] { LocalDevelopmentKeyProvider.ScopedReferencePrefix, VaultTransitKeyProvider.ReferencePrefix })
        {
            foreach (var version in new[] { 1, 2, 10, int.MaxValue })
            {
                field.EncryptionKeyReference = KeyScopeEncoding.Reference(family, new DataKeyScope(Org, EncryptionKeyPurpose.FieldValue), version);
                document.EncryptionKeyReference = KeyScopeEncoding.Reference(family, new DataKeyScope(Org, EncryptionKeyPurpose.Document), version);
                Assert.True(KeyScopeEncoding.TryParseReference(field.EncryptionKeyReference, family, out _, out _, out _));
                Assert.True(KeyScopeEncoding.TryParseReference(document.EncryptionKeyReference, family, out _, out _, out _));
                Assert.Null(EnvelopeRules.Validate(AsSentToTheAuthority(field)));
                Assert.Null(EnvelopeRules.Validate(AsSentToTheAuthority(document)));
            }
        }
    }
}
