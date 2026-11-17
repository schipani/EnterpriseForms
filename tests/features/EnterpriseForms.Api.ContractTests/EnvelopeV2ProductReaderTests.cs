using System.Security.Cryptography;
using EnterpriseForms.Application.Features.Documents.Ports;
using EnterpriseForms.Application.Features.FormRecords.Encryption;
using EnterpriseForms.Application.Features.FormRecords.Values;
using EnterpriseForms.Domain.Features.FormRecords;
using EnterpriseForms.Infrastructure.Features.Documents;
using EnterpriseForms.Infrastructure.Features.Documents.EnvelopeV2;
using EnterpriseForms.Infrastructure.Features.FormRecords;
using Xunit;

namespace EnterpriseForms.Api.ContractTests;

/// <summary>
/// E3-M2c (OD-M2c-02a): the two field-decryption modes and the dark Envelope v2 product readers. Mode A
/// (<see cref="IFieldEncryptionService.DecryptVerifiedAsync"/>) dispatches strictly under S-8; Mode B
/// (<see cref="IFieldEncryptionService.DecryptAsync"/>) accepts only Envelope v1 of signed schema 0/1 without v2 columns. v2 rows are
/// produced only by the M1 core through InternalsVisibleTo (no product v2 writer exists). Key-provider calls are counted to prove
/// "refused before provider access" and "one unwrap per data-key group".
/// </summary>
public sealed class EnvelopeV2ProductReaderTests
{
    private static readonly Guid Org = Guid.Parse("019a5b2c-0c00-7000-8000-0000000000c1");
    private static readonly Guid OtherOrg = Guid.Parse("019a5b2c-0c00-7000-8000-0000000000c2");
    private static readonly Guid Record = Guid.Parse("019a5b2c-0c00-7000-8000-0000000000d1");
    private static readonly Guid Field = Guid.Parse("019a5b2c-0c00-7000-8000-0000000000e1");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string? Reason(Exception ex) => DecryptionFailure.Find(ex)?.ReasonCode;

    // ---- fixtures -----------------------------------------------------------------------------------------------------

    /// <summary>A stored v1 row (what the stager would persist: signed schema 1).</summary>
    private static FieldEncryptedValue V1Row(AesGcmFieldEncryptionService service, string plaintext, Guid? tenant = null, Guid? item = null)
    {
        var row = InProcessCompletion.Completed(service.EncryptAsync(new FieldEncryptionContext(tenant ?? Org, Record, Field, item ?? Guid.CreateVersion7(), "String"), plaintext, EncryptionKeyPurpose.FieldValue, Ct));
        row.RowSignatureSchemaVersion = 1;
        return row;
    }

    /// <summary>A stored v2 row sealed by the M1 core (signed schema 2, envelope 2, every v2 column set).</summary>
    private static FieldEncryptedValue V2Row(FieldSealBatchV2 batch, string plaintext, Guid? item = null, string logicalType = "String", Guid? organization = null)
    {
        var id = item ?? Guid.CreateVersion7();
        var envelope = batch.Seal(Field, id, logicalType, plaintext);
        return new FieldEncryptedValue
        {
            FieldEncryptedValueId = Guid.CreateVersion7(), TenantId = organization ?? envelope.Header.Prefix.OrganizationId, FormRecordId = envelope.Identity.FormRecordId,
            FieldDefinitionId = envelope.Identity.FieldDefinitionId, FieldValueItemId = envelope.Identity.FieldValueItemId, EncryptedLogicalType = envelope.Identity.LogicalType,
            EncryptedValueBytes = envelope.Ciphertext.ToArray(), EncryptionNonceBytes = envelope.Nonce.ToArray(), EncryptionAuthTagBytes = envelope.Tag.ToArray(),
            WrappedDataKeyBytes = envelope.WrappedSeed.ToArray(), EncryptionAlgorithmName = "AES-256-GCM", EncryptionKeyReference = envelope.Header.Prefix.KekReference,
            EncryptionEnvelopeVersion = 2, RowSignatureSchemaVersion = 2,
            DataKeyIdentifier = envelope.Header.Prefix.DataKeyIdentifier, DataKeyCommitmentBytes = envelope.Header.KeyCommitment.ToArray(), KeyWrapAlgorithmCode = (int)envelope.Header.Prefix.WrapAlgorithm,
        };
    }

    private static FieldSealBatchV2 Batch(IEncryptionKeyProvider keys, Guid? organization = null, EncryptionKeyPurpose? purpose = null) =>
        InProcessCompletion.Completed(FieldSealBatchV2.CreateAsync(keys, organization ?? Org, Record, purpose ?? EncryptionKeyPurpose.FieldValue, Ct));

    private static FieldEncryptedValue Copy(FieldEncryptedValue r) => new()
    {
        FieldEncryptedValueId = r.FieldEncryptedValueId, TenantId = r.TenantId, FormRecordId = r.FormRecordId, FieldDefinitionId = r.FieldDefinitionId, FieldValueItemId = r.FieldValueItemId, FormRecordGroupId = r.FormRecordGroupId,
        EncryptedLogicalType = r.EncryptedLogicalType, EncryptedValueBytes = r.EncryptedValueBytes.ToArray(), EncryptionNonceBytes = r.EncryptionNonceBytes.ToArray(), EncryptionAuthTagBytes = r.EncryptionAuthTagBytes.ToArray(),
        WrappedDataKeyBytes = r.WrappedDataKeyBytes.ToArray(), EncryptionAlgorithmName = r.EncryptionAlgorithmName, EncryptionKeyReference = r.EncryptionKeyReference, EncryptionEnvelopeVersion = r.EncryptionEnvelopeVersion,
        RowSignatureSchemaVersion = r.RowSignatureSchemaVersion, DataKeyIdentifier = r.DataKeyIdentifier, DataKeyCommitmentBytes = r.DataKeyCommitmentBytes?.ToArray(), KeyWrapAlgorithmCode = r.KeyWrapAlgorithmCode,
    };

    private static IReadOnlyList<string> Verified(AesGcmFieldEncryptionService service, params FieldEncryptedValue[] rows) =>
        InProcessCompletion.Completed(service.DecryptVerifiedAsync(Org, rows, Ct));

    private static Exception RefusedVerified(AesGcmFieldEncryptionService service, params FieldEncryptedValue[] rows) =>
        Assert.ThrowsAny<Exception>(() => InProcessCompletion.Completed(service.DecryptVerifiedAsync(Org, rows, Ct)));

    // ---- Mode A: verified ---------------------------------------------------------------------------------------------

    [Fact]
    public void VerifiedSchema1Envelope1OpensWithTheV1ReaderAndSchema0IsRefusedBeforeTheProvider()
    {
        var keys = new EnvelopeV2KeyDouble();
        var service = new AesGcmFieldEncryptionService(keys);
        var row = V1Row(service, "stored v1");
        Assert.Equal(["stored v1"], Verified(service, row));
        Assert.Equal(1, keys.UnwrapCalls);

        var unstaged = Copy(row);
        unstaged.RowSignatureSchemaVersion = 0; // a fresh, never-staged seal: never Authority-verified content
        var ex = RefusedVerified(service, unstaged);
        Assert.IsAssignableFrom<CryptographicException>(ex);
        Assert.Equal(DecryptionFailureReasons.UnsupportedEnvelope, Reason(ex));
        Assert.Equal(1, keys.UnwrapCalls); // unchanged: refused before provider access
    }

    [Fact]
    public void VerifiedSchema2Envelope2DispatchesToTheV2ReaderWithOneUnwrapPerDataKeyGroup()
    {
        var keys = new EnvelopeV2KeyDouble();
        var service = new AesGcmFieldEncryptionService(keys);
        using var first = Batch(keys);
        using var second = Batch(keys);
        var rows = new[]
        {
            V2Row(first, "a1"), V1Row(service, "v1-a"), V2Row(first, "a2"), V2Row(second, "b1"), V2Row(first, "a3"), V1Row(service, "v1-b"), V2Row(second, "b2"),
        };
        var unwrapsBefore = keys.UnwrapCalls;
        Assert.Equal(["a1", "v1-a", "a2", "b1", "a3", "v1-b", "b2"], Verified(service, rows));
        Assert.Equal(2 + 2, keys.UnwrapCalls - unwrapsBefore); // two v1 rows (one each) + two data-key groups (one each)

        // The application batch reader (Mode A) gives the same result through one verified call.
        var decrypted = InProcessCompletion.Completed(DecryptedFieldValues.DecryptAsync(service, rows, Ct));
        Assert.Equal("b2", decrypted.WireOf(rows[6]));
        Assert.Equal("b2", InProcessCompletion.Completed(FieldValueCodec.FormatAsync(rows[6], new Dictionary<Guid, Domain.Features.FormDefinitions.FieldOption>(), service, Ct)));
    }

    [Theory]
    [InlineData(1, 2)]
    [InlineData(2, 1)]
    [InlineData(0, 2)]
    [InlineData(0, 1)]
    [InlineData(3, 3)]
    [InlineData(2, 3)]
    [InlineData(1, 0)]
    public void EveryOtherSchemaAndEnvelopeCombinationIsRefusedBeforeTheProvider(int schema, int envelope)
    {
        var keys = new EnvelopeV2KeyDouble();
        var service = new AesGcmFieldEncryptionService(keys);
        using var batch = Batch(keys);
        var good = V1Row(service, "good");
        var mismatched = envelope == 2 ? V2Row(batch, "x") : V1Row(service, "x");
        mismatched.RowSignatureSchemaVersion = schema;
        mismatched.EncryptionEnvelopeVersion = envelope;
        var ex = RefusedVerified(service, good, mismatched); // the valid row first: still nothing is unwrapped
        Assert.Equal(DecryptionFailureReasons.UnsupportedEnvelope, Reason(ex));
        Assert.Equal(0, keys.UnwrapCalls);
    }

    public static TheoryData<string> V2MetadataDefects => ["no-identifier", "no-commitment", "no-wrap-code", "commitment-31", "wrap-code-3", "wrap-code-family-mismatch", "identifier-not-v7", "legacy-reference",
        "foreign-organization-reference", "document-purpose-reference", "sealed-key-type", "algorithm", "wrapped-59", "nonce-11", "tag-15"];

    [Theory]
    [MemberData(nameof(V2MetadataDefects))]
    public void IncompleteOrUnexpectedV2MetadataIsRefusedBeforeTheProvider(string defect)
    {
        var keys = new EnvelopeV2KeyDouble();
        var service = new AesGcmFieldEncryptionService(keys);
        using var batch = Batch(keys);
        var row = V2Row(batch, "content");
        var unwraps = keys.UnwrapCalls;
        switch (defect)
        {
            case "no-identifier": row.DataKeyIdentifier = null; break;
            case "no-commitment": row.DataKeyCommitmentBytes = null; break;
            case "no-wrap-code": row.KeyWrapAlgorithmCode = null; break;
            case "commitment-31": row.DataKeyCommitmentBytes = new byte[31]; break;
            case "wrap-code-3": row.KeyWrapAlgorithmCode = 3; break;
            case "wrap-code-family-mismatch": row.KeyWrapAlgorithmCode = 1; break; // reference is vault-transit (code 2)
            case "identifier-not-v7": row.DataKeyIdentifier = Guid.NewGuid(); break;
            case "legacy-reference": row.EncryptionKeyReference = "local-dev-kek-v1"; break;
            case "foreign-organization-reference": row.EncryptionKeyReference = EnvelopeV2KeyDouble.Reference("vault-transit", OtherOrg, EncryptionKeyPurpose.FieldValue, 1); break;
            case "document-purpose-reference": row.EncryptionKeyReference = EnvelopeV2KeyDouble.Reference("vault-transit", Org, EncryptionKeyPurpose.Document, 1); break;
            case "sealed-key-type": row.EncryptedLogicalType = "CreationFingerprintKey/v1"; break;
            case "algorithm": row.EncryptionAlgorithmName = "AES-128-GCM"; break;
            case "wrapped-59": row.WrappedDataKeyBytes = row.WrappedDataKeyBytes[..59]; break;
            case "nonce-11": row.EncryptionNonceBytes = row.EncryptionNonceBytes[..11]; break;
            case "tag-15": row.EncryptionAuthTagBytes = row.EncryptionAuthTagBytes[..15]; break;
        }

        var ex = RefusedVerified(service, V1Row(service, "valid neighbour"), row);
        Assert.Equal(DecryptionFailureReasons.UnsupportedEnvelope, Reason(ex));
        Assert.Equal(unwraps, keys.UnwrapCalls);
    }

    [Theory]
    [InlineData("identifier")]
    [InlineData("commitment")]
    [InlineData("wrap-code")]
    public void ASchema1RowCarryingAnyV2ColumnIsRefused(string column)
    {
        var keys = new EnvelopeV2KeyDouble();
        var service = new AesGcmFieldEncryptionService(keys);
        var row = V1Row(service, "v1");
        switch (column)
        {
            case "identifier": row.DataKeyIdentifier = Guid.CreateVersion7(); break;
            case "commitment": row.DataKeyCommitmentBytes = new byte[32]; break;
            default: row.KeyWrapAlgorithmCode = 1; break;
        }

        Assert.Equal(DecryptionFailureReasons.UnsupportedEnvelope, Reason(RefusedVerified(service, row)));
        Assert.Equal(0, keys.UnwrapCalls);
    }

    [Fact]
    public void ProviderFailuresOfAV2RowFailClosedWithoutFallback()
    {
        var keys = new EnvelopeV2KeyDouble();
        var service = new AesGcmFieldEncryptionService(keys);
        using var batch = Batch(keys);
        var row = V2Row(batch, "secret");

        keys.Unavailable = true;
        Assert.Equal(DecryptionFailureReasons.KeyUnavailable, Reason(Assert.IsType<EncryptionUnavailableException>(RefusedVerified(service, row))));
        keys.Unavailable = false;

        keys.Override = () => RandomNumberGenerator.GetBytes(32); // a provider that returns the wrong key
        var before = keys.UnwrapCalls;
        Assert.Equal(DecryptionFailureReasons.KeyMismatch, Reason(RefusedVerified(service, row)));
        Assert.Equal(1, keys.UnwrapCalls - before); // exactly one attempt: no retry, no other key, no v1 fallback
        keys.Override = null;

        var foreign = Copy(row);
        foreign.WrappedDataKeyBytes[^1] ^= 1; // unknown wrap: the provider refuses
        Assert.Equal(DecryptionFailureReasons.KeyMismatch, Reason(RefusedVerified(service, foreign)));
        Assert.Equal(["secret"], Verified(service, row));
    }

    [Fact]
    public void TamperedV2RowsAreRefusedByTheCommitmentAndTheAead()
    {
        var keys = new EnvelopeV2KeyDouble();
        var service = new AesGcmFieldEncryptionService(keys);
        using var batch = Batch(keys);
        var row = V2Row(batch, "integrity");

        var ciphertext = Copy(row);
        ciphertext.EncryptedValueBytes[0] ^= 1;
        Assert.Equal(DecryptionFailureReasons.AuthenticationFailed, Reason(RefusedVerified(service, ciphertext)));

        var commitment = Copy(row);
        commitment.DataKeyCommitmentBytes![0] ^= 1;
        Assert.Equal(DecryptionFailureReasons.KeyMismatch, Reason(RefusedVerified(service, commitment)));

        var moved = Copy(row);
        moved.FieldValueItemId = Guid.CreateVersion7(); // transplanted to another item: item key and AAD differ
        Assert.Equal(DecryptionFailureReasons.AuthenticationFailed, Reason(RefusedVerified(service, moved)));

        var renamed = Copy(row);
        renamed.DataKeyIdentifier = Guid.CreateVersion7(); // a different data-key id: H0 changes, the commitment no longer matches
        Assert.Equal(DecryptionFailureReasons.KeyMismatch, Reason(RefusedVerified(service, renamed)));
    }

    [Fact]
    public void TheExactKeyVersionNamedByTheReferenceIsUsedWithoutFallback()
    {
        var keys = new EnvelopeV2KeyDouble(version: 1);
        var service = new AesGcmFieldEncryptionService(keys);
        using var batch = Batch(keys);
        var row = V2Row(batch, "version-bound");
        var other = Copy(row);
        other.EncryptionKeyReference = EnvelopeV2KeyDouble.Reference("vault-transit", Org, EncryptionKeyPurpose.FieldValue, 2);
        var before = keys.UnwrapCalls;
        Assert.Equal(DecryptionFailureReasons.KeyMismatch, Reason(RefusedVerified(service, other))); // asked for version 2 exactly, once
        Assert.Equal(1, keys.UnwrapCalls - before);
        Assert.Equal(["version-bound"], Verified(service, row));
    }

    [Fact]
    public void OrganizationAndPurposeIsolationHoldsBeforeAnyKeyOperation()
    {
        var keys = new EnvelopeV2KeyDouble();
        var service = new AesGcmFieldEncryptionService(keys);
        using var own = Batch(keys);
        using var foreignBatch = Batch(keys, OtherOrg);
        using var sealedKeyBatch = Batch(keys, purpose: EncryptionKeyPurpose.IdempotencyProtection);
        var unwraps = keys.UnwrapCalls;

        // A row of another organization in this organization's batch, v1 or v2.
        Assert.Equal(DecryptionFailureReasons.UnsupportedEnvelope, Reason(RefusedVerified(service, V2Row(own, "mine"), V2Row(foreignBatch, "theirs"))));
        Assert.Equal(DecryptionFailureReasons.UnsupportedEnvelope, Reason(RefusedVerified(service, V1Row(service, "theirs", tenant: OtherOrg))));
        // An idempotency-key seal presented as a stored field value (purpose and kind differ).
        Assert.Equal(DecryptionFailureReasons.UnsupportedEnvelope, Reason(RefusedVerified(service, V2Row(sealedKeyBatch, Convert.ToBase64String(new byte[32]), logicalType: "CreationFingerprintKey/v1"))));
        Assert.Equal(unwraps, keys.UnwrapCalls);

        // The application step refuses a batch that spans organizations outright.
        Assert.Throws<InvalidOperationException>(() => InProcessCompletion.Completed(DecryptedFieldValues.DecryptAsync(service, [V1Row(service, "a"), V1Row(service, "b", tenant: OtherOrg)], Ct)));
    }

    [Fact]
    public void CancellationStopsTheVerifiedReaderWithoutAPartialResultOrAFailureRecord()
    {
        var keys = new EnvelopeV2KeyDouble();
        var service = new AesGcmFieldEncryptionService(keys);
        using var batch = Batch(keys);
        var rows = new[] { V2Row(batch, "a"), V2Row(batch, "b") };
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        var ex = Assert.ThrowsAny<OperationCanceledException>(() => InProcessCompletion.Completed(service.DecryptVerifiedAsync(Org, rows, cancelled.Token)));
        Assert.Null(DecryptionFailure.Find(ex));
    }

    [Fact]
    public void TheVerifiedBatchRefusesNullAndRepeatedRows()
    {
        var service = new AesGcmFieldEncryptionService(new EnvelopeV2KeyDouble());
        var row = V1Row(service, "once");
        Assert.Throws<ArgumentException>(() => InProcessCompletion.Completed(service.DecryptVerifiedAsync(Org, [row, row], Ct)));
        Assert.Throws<ArgumentException>(() => InProcessCompletion.Completed(service.DecryptVerifiedAsync(Org, [null!], Ct)));
        Assert.Throws<ArgumentException>(() => InProcessCompletion.Completed(service.DecryptVerifiedAsync(Guid.Empty, [row], Ct)));
        Assert.Empty(InProcessCompletion.Completed(service.DecryptVerifiedAsync(Org, [], Ct)));
    }

    [Fact]
    public void TheWorkerEntryPointReadsV2RowsSealedUnderTheRealLocalKeyring()
    {
        // FieldValueCodec.FormatAsync is the read the Worker's protection migration (Decrypt direction) and reconciliation use.
        var keys = EnvelopeV2TestSupport.LocalKeyring();
        var service = new AesGcmFieldEncryptionService(keys);
        using var batch = Batch(keys);
        var row = V2Row(batch, "local keyring v2");
        Assert.Equal(1, row.KeyWrapAlgorithmCode); // localdev family
        var options = new Dictionary<Guid, Domain.Features.FormDefinitions.FieldOption>();
        Assert.Equal("local keyring v2", InProcessCompletion.Completed(FieldValueCodec.FormatAsync(row, options, service, Ct)));
        Assert.Equal("stored v1", InProcessCompletion.Completed(FieldValueCodec.FormatAsync(V1Row(service, "stored v1"), options, service, Ct)));

        var relabelled = Copy(row);
        relabelled.KeyWrapAlgorithmCode = 2; // claims the vault-transit wrap for a localdev reference
        Assert.Equal(DecryptionFailureReasons.UnsupportedEnvelope, Reason(Assert.ThrowsAny<CryptographicException>(() => InProcessCompletion.Completed(FieldValueCodec.FormatAsync(relabelled, options, service, Ct)))));
        var unstaged = Copy(row);
        unstaged.RowSignatureSchemaVersion = 0;
        Assert.ThrowsAny<CryptographicException>(() => InProcessCompletion.Completed(FieldValueCodec.FormatAsync(unstaged, options, service, Ct)));
    }

    // ---- Mode B: self-sealed ------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void SelfSealedAcceptsEnvelope1OfSchema0Or1(int schema)
    {
        var keys = new EnvelopeV2KeyDouble();
        var service = new AesGcmFieldEncryptionService(keys);
        var row = V1Row(service, "own input");
        row.RowSignatureSchemaVersion = schema;
        Assert.Equal("own input", InProcessCompletion.Completed(service.DecryptAsync(row, EncryptionKeyPurpose.FieldValue, Ct)));
    }

    [Theory]
    [InlineData(2, 1)]
    [InlineData(2, 2)]
    [InlineData(0, 2)]
    [InlineData(1, 2)]
    [InlineData(3, 1)]
    public void SelfSealedRefusesSchema2AndEnvelope2BeforeTheProvider(int schema, int envelope)
    {
        var keys = new EnvelopeV2KeyDouble();
        var service = new AesGcmFieldEncryptionService(keys);
        using var batch = Batch(keys);
        var row = envelope == 2 ? V2Row(batch, "v2 content") : V1Row(service, "v1 content");
        row.RowSignatureSchemaVersion = schema;
        row.EncryptionEnvelopeVersion = envelope;
        var unwraps = keys.UnwrapCalls;
        var ex = Assert.ThrowsAny<CryptographicException>(() => InProcessCompletion.Completed(service.DecryptAsync(row, EncryptionKeyPurpose.FieldValue, Ct)));
        Assert.Equal(DecryptionFailureReasons.UnsupportedEnvelope, Reason(ex));
        Assert.Equal(unwraps, keys.UnwrapCalls); // the v2 opener (and the provider) are never reached
    }

    [Theory]
    [InlineData("identifier")]
    [InlineData("commitment")]
    [InlineData("wrap-code")]
    public void SelfSealedRefusesAnEnvelope1RowCarryingV2Metadata(string column)
    {
        var keys = new EnvelopeV2KeyDouble();
        var service = new AesGcmFieldEncryptionService(keys);
        var row = V1Row(service, "v1");
        row.RowSignatureSchemaVersion = 0;
        switch (column)
        {
            case "identifier": row.DataKeyIdentifier = Guid.CreateVersion7(); break;
            case "commitment": row.DataKeyCommitmentBytes = new byte[32]; break;
            default: row.KeyWrapAlgorithmCode = 2; break;
        }

        Assert.Equal(DecryptionFailureReasons.UnsupportedEnvelope, Reason(Assert.ThrowsAny<CryptographicException>(() => InProcessCompletion.Completed(service.DecryptAsync(row, EncryptionKeyPurpose.FieldValue, Ct)))));
        Assert.Equal(0, keys.UnwrapCalls);
    }

    [Fact]
    public void AGenuineV2RowNeverOpensThroughTheSelfSealedApiWhateverItsLabels()
    {
        // Relabelling a real v2 envelope as v1 cannot reach the v2 opener either: the v1 reader is used and fails closed.
        var keys = new EnvelopeV2KeyDouble();
        var service = new AesGcmFieldEncryptionService(keys);
        using var batch = Batch(keys);
        var relabelled = V2Row(batch, "v2 secret");
        relabelled.RowSignatureSchemaVersion = 1;
        relabelled.EncryptionEnvelopeVersion = 1;
        relabelled.DataKeyIdentifier = null;
        relabelled.DataKeyCommitmentBytes = null;
        relabelled.KeyWrapAlgorithmCode = null;
        Assert.ThrowsAny<CryptographicException>(() => InProcessCompletion.Completed(service.DecryptAsync(relabelled, EncryptionKeyPurpose.FieldValue, Ct)));
        Assert.ThrowsAny<CryptographicException>(() => InProcessCompletion.Completed(service.DecryptVerifiedAsync(Org, [relabelled], Ct)));
    }

    [Fact]
    public void TheNamedSelfSealedEntryPointsAcceptOnlyRowsThisProcessFreshlySealed()
    {
        var service = new AesGcmFieldEncryptionService(new EnvelopeV2KeyDouble());
        var field = new Domain.Features.FormDefinitions.FieldDefinition { FieldDefinitionId = Field, FieldKey = "secret", FieldDisplayName = "Secret", FieldDataType = "String", FieldProtectionMode = "ApplicationEncrypted", FieldClassification = "Confidential" };
        var options = new Dictionary<Guid, Domain.Features.FormDefinitions.FieldOption>();
        var typed = FieldValueCodec.CreateRow(field, null, [], "fresh", Org, Record, Guid.CreateVersion7(), DateTimeOffset.UtcNow, Guid.CreateVersion7());
        var sealedRow = (FieldEncryptedValue)InProcessCompletion.Completed(FieldValueCodec.ProtectAsync(typed, field, options, service, Ct));

        Assert.Equal("fresh", InProcessCompletion.Completed(FieldValueCodec.FormatSelfSealedAsync(sealedRow, options, service, Ct)));
        Assert.Equal("fresh", InProcessCompletion.Completed(DecryptedFieldValues.DecryptSelfSealedAsync(service, [sealedRow], Ct)).WireOf(sealedRow));
        sealedRow.RowSignatureSchemaVersion = 1; // staged in place (the stager mutates the same instance): still this operation's own seal
        Assert.Equal("fresh", InProcessCompletion.Completed(FieldValueCodec.FormatSelfSealedAsync(sealedRow, options, service, Ct)));

        // Same bytes, but not this operation's seal: a stored/materialized copy, or a direct EncryptAsync result (clones: PhaseC5 provenance tests).
        var materialized = Copy(sealedRow);
        var direct = V1Row(service, "not through ProtectAsync");
        foreach (var notOwn in new[] { materialized, direct })
        {
            Assert.Throws<InvalidOperationException>(() => InProcessCompletion.Completed(FieldValueCodec.FormatSelfSealedAsync(notOwn, options, service, Ct)));
            Assert.Throws<InvalidOperationException>(() => InProcessCompletion.Completed(DecryptedFieldValues.DecryptSelfSealedAsync(service, [notOwn], Ct)));
            Assert.Equal(direct == notOwn ? "not through ProtectAsync" : "fresh", Verified(service, notOwn)[0]); // stored content opens on the verified path
        }

        // An already-encrypted row passed through ProtectAsync is returned unchanged and does not become "own".
        Assert.Same(materialized, InProcessCompletion.Completed(FieldValueCodec.ProtectAsync(materialized, field, options, service, Ct)));
        Assert.Throws<InvalidOperationException>(() => InProcessCompletion.Completed(FieldValueCodec.FormatSelfSealedAsync(materialized, options, service, Ct)));
    }

    // ---- Documents ----------------------------------------------------------------------------------------------------

    private static FileEncryptionContext DocumentContext(int version = 1) => new(Org, Record, Guid.Parse("019a5b2c-0c00-7000-8000-0000000000f1"), version);

    private static async Task<(VerifiedDocumentEnvelope Envelope, byte[] Ciphertext)> V2DocumentAsync(IEncryptionKeyProvider keys, byte[] plaintext, FileEncryptionContext context)
    {
        using var sink = new MemoryStream();
        var outcome = await DocumentEnvelopeV2Cipher.EncryptAsync(new MemoryStream(plaintext), sink, keys, context.TenantId, new DocumentVersionIdentity(context.FormRecordId, context.FileAttachmentId, context.VersionNumber), Ct);
        var e = outcome.Envelope;
        return (new VerifiedDocumentEnvelope(2, "AES-256-GCM", 2, e.Header.Prefix.KekReference, e.WrappedSeed.ToArray(), e.Context.NoncePrefix.ToArray(), e.Context.SegmentSizeBytes,
            e.Header.Prefix.DataKeyIdentifier, e.Header.KeyCommitment.ToArray(), (int)e.Header.Prefix.WrapAlgorithm), sink.ToArray());
    }

    private static async Task<(VerifiedDocumentEnvelope Envelope, byte[] Ciphertext)> V1DocumentAsync(AesGcmFileEncryptionService service, byte[] plaintext, FileEncryptionContext context)
    {
        using var sink = new MemoryStream();
        var e = (await service.EncryptAsync(new MemoryStream(plaintext), sink, context, Ct)).Envelope;
        return (new VerifiedDocumentEnvelope(1, e.AlgorithmName, e.EnvelopeVersion, e.KeyReference, e.WrappedDataKey, e.NoncePrefix, e.SegmentSizeBytes, null, null, null), sink.ToArray());
    }

    private static async Task<byte[]> ReadAsync(AesGcmFileEncryptionService service, VerifiedDocumentEnvelope envelope, byte[] ciphertext, FileEncryptionContext context, RecordingSink? sink = null, CancellationToken? cancellationToken = null)
    {
        var output = sink ?? new RecordingSink();
        await service.DecryptVerifiedAsync(new MemoryStream(ciphertext), output, envelope, context, cancellationToken ?? Ct);
        return output.ToArray();
    }

    [Fact]
    public async Task TheVerifiedDocumentReaderOpensV1AndV2VersionsWithOneUnwrapEach()
    {
        var keys = new EnvelopeV2KeyDouble();
        var service = new AesGcmFileEncryptionService(keys);
        var context = DocumentContext();
        var plaintext = RandomNumberGenerator.GetBytes((2 * 1024 * 1024) + 17); // three segments
        var (v1, v1Cipher) = await V1DocumentAsync(service, plaintext, context);
        var (v2, v2Cipher) = await V2DocumentAsync(keys, plaintext, context);
        var before = keys.UnwrapCalls;
        Assert.Equal(plaintext, await ReadAsync(service, v1, v1Cipher, context));
        Assert.Equal(plaintext, await ReadAsync(service, v2, v2Cipher, context));
        Assert.Equal(2, keys.UnwrapCalls - before);
    }

    [Theory]
    [InlineData(0, 1, false)]
    [InlineData(0, 2, true)]
    [InlineData(1, 2, true)]
    [InlineData(2, 1, false)]
    [InlineData(1, 1, true)]  // schema 1 with v2 columns
    [InlineData(2, 2, false)] // schema 2 without v2 columns
    [InlineData(3, 3, true)]
    public async Task TheVerifiedDocumentReaderRefusesEveryOtherCombinationBeforeTheProvider(int schema, int envelopeVersion, bool withV2Columns)
    {
        var keys = new EnvelopeV2KeyDouble();
        var service = new AesGcmFileEncryptionService(keys);
        var context = DocumentContext();
        var (v2, cipher) = await V2DocumentAsync(keys, [1, 2, 3], context);
        var envelope = v2 with
        {
            SignedSchemaVersion = schema, EnvelopeVersion = envelopeVersion,
            DataKeyIdentifier = withV2Columns ? v2.DataKeyIdentifier : null, DataKeyCommitmentBytes = withV2Columns ? v2.DataKeyCommitmentBytes : null, KeyWrapAlgorithmCode = withV2Columns ? v2.KeyWrapAlgorithmCode : null,
        };
        var sink = new RecordingSink();
        var ex = await Assert.ThrowsAnyAsync<CryptographicException>(() => ReadAsync(service, envelope, cipher, context, sink));
        Assert.Equal(DecryptionFailureReasons.UnsupportedEnvelope, Reason(ex));
        Assert.Equal(0, keys.UnwrapCalls);
        Assert.Empty(sink.Writes);
    }

    [Fact]
    public async Task TheVerifiedDocumentReaderFailsClosedOnTamperIdentityAndKeyFailures()
    {
        var keys = new EnvelopeV2KeyDouble();
        var service = new AesGcmFileEncryptionService(keys);
        var context = DocumentContext();
        var plaintext = RandomNumberGenerator.GetBytes(4096);
        var (envelope, cipher) = await V2DocumentAsync(keys, plaintext, context);

        var commitment = envelope with { DataKeyCommitmentBytes = envelope.DataKeyCommitmentBytes!.Select((b, i) => i == 0 ? (byte)(b ^ 1) : b).ToArray() };
        var sink = new RecordingSink();
        Assert.Equal(DecryptionFailureReasons.KeyMismatch, Reason(await Assert.ThrowsAnyAsync<CryptographicException>(() => ReadAsync(service, commitment, cipher, context, sink))));
        Assert.Empty(sink.Writes); // nothing released before the key commitment matched

        var tampered = cipher.ToArray();
        tampered[10] ^= 1;
        sink = new RecordingSink();
        Assert.Equal(DecryptionFailureReasons.AuthenticationFailed, Reason(await Assert.ThrowsAnyAsync<CryptographicException>(() => ReadAsync(service, envelope, tampered, context, sink))));
        Assert.Empty(sink.Writes);

        sink = new RecordingSink();
        Assert.Equal(DecryptionFailureReasons.AuthenticationFailed, Reason(await Assert.ThrowsAnyAsync<CryptographicException>(() => ReadAsync(service, envelope, cipher, DocumentContext(version: 2), sink)))); // another version's identity
        Assert.Empty(sink.Writes);

        var foreignOrganization = context with { TenantId = OtherOrg };
        Assert.Equal(DecryptionFailureReasons.UnsupportedEnvelope, Reason(await Assert.ThrowsAnyAsync<CryptographicException>(() => ReadAsync(service, envelope, cipher, foreignOrganization))));

        keys.Unavailable = true;
        Assert.Equal(DecryptionFailureReasons.KeyUnavailable, Reason(await Assert.ThrowsAsync<EncryptionUnavailableException>(() => ReadAsync(service, envelope, cipher, context))));
        keys.Unavailable = false;

        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        var canceled = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ReadAsync(service, envelope, cipher, context, cancellationToken: cancelled.Token));
        Assert.Null(DecryptionFailure.Find(canceled));
        Assert.Equal(plaintext, await ReadAsync(service, envelope, cipher, context));
    }

    [Fact]
    public void TheVerifiedDocumentEnvelopeCarriesTheStoredColumnsExactly()
    {
        var version = new Domain.Features.Documents.FileAttachmentVersion
        {
            RowSignatureSchemaVersion = 2, EncryptionAlgorithmName = "AES-256-GCM", EncryptionEnvelopeVersion = 2, EncryptionKeyReference = "ref", WrappedDataKey = [1], EncryptionNoncePrefix = [2], EncryptionSegmentSizeBytes = 7,
            DataKeyIdentifier = Guid.CreateVersion7(), DataKeyCommitmentBytes = [3], KeyWrapAlgorithmCode = 2,
        };
        var envelope = VerifiedDocumentEnvelope.Of(version);
        Assert.Equal((2, 2, "ref", 7, version.DataKeyIdentifier, (int?)2), (envelope.SignedSchemaVersion, envelope.EnvelopeVersion, envelope.KeyReference, envelope.SegmentSizeBytes, envelope.DataKeyIdentifier, envelope.KeyWrapAlgorithmCode));
        Assert.Same(version.WrappedDataKey, envelope.WrappedDataKey);
        Assert.Same(version.DataKeyCommitmentBytes, envelope.DataKeyCommitmentBytes);
    }
}
