using AuthorizationAuthority.Application.Common;
using AuthorizationAuthority.Application.Features.Content;
using AuthorizationAuthority.Contracts.V1;
using EnterpriseForms.Integrity.Canonical;
using Xunit;
using static AuthorizationAuthority.Tests.ContributorWriteFixtures;

namespace AuthorizationAuthority.Tests;

/// <summary>
/// E3-M2d: the Authority Envelope v2 rules as pure functions (docs/security/E3_M2D_IMPLEMENTATION_RECORD.md): the per-row v2 shape
/// (package §2.3, with the codec-derived field bound D-8 and the document size formula pinned against E3-M1 D0–D4), batch
/// consistency and v1/v2 wrapped-key separation, the Prepare-only carried/new classification with DK-3, the new-v2 refusal and the
/// downgrade guard, the Commit refusal of every v2 row and the census. The v1 rules (EnvelopeRulesTests) are unchanged.
/// </summary>
public sealed class EnvelopeV2RulesTests
{
    private static readonly int[] WrapCodes = [1, 2];

    private static readonly Guid Org = Guid.CreateVersion7();
    private static readonly Guid Record = Guid.CreateVersion7();
    private static readonly Guid Field = Guid.CreateVersion7();

    private static readonly RowSchemaDefinition FieldsV2 = V2(EncryptedValues);
    private static readonly RowSchemaDefinition DocumentsV2 = V2(Attachments);

    private static RowSchemaDefinition V2(RowSchemaDefinition schema1) => new(schema1.EntityKind, 2,
        schema1.Columns.Concat([
            new RowSignedColumn("data_key_commitment_bytes", RowColumnType.Bytes, false),
            new RowSignedColumn("data_key_identifier", RowColumnType.Uuid, false),
            new RowSignedColumn("key_wrap_algorithm_code", RowColumnType.Int32, false),
        ]).OrderBy(c => c.ColumnName, StringComparer.Ordinal).ToList());

    private static Row FieldV2(Guid item, Guid batch, byte nonce = 1, byte wrapped = 0xaa, byte commitment = 0xdd, int wrapCode = 1, string family = "localdev") =>
        (Sealed(Org, Record, Field, item, 10) with { Schema = FieldsV2 })
            .With("encryption_envelope_version", RowValue.Int32(2))
            .With("encryption_key_reference", RowValue.Text($"{family}:{Org:D}:field-value:1"))
            .With("encryption_nonce_bytes", RowValue.Bytes(Fill(12, nonce)))
            .With("wrapped_data_key_bytes", RowValue.Bytes(Fill(60, wrapped)))
            .With("data_key_commitment_bytes", RowValue.Bytes(Fill(32, commitment)))
            .With("data_key_identifier", RowValue.Uuid(batch))
            .With("key_wrap_algorithm_code", RowValue.Int32(wrapCode));

    private static Row DocumentV2(Guid attachment, Guid batch, long plaintext = 1000, long? ciphertext = null, byte wrapped = 0x55) =>
        (Attachment(Org, Record, attachment) with { Schema = DocumentsV2 })
            .With("encryption_envelope_version", RowValue.Int32(2))
            .With("encryption_key_reference", RowValue.Text($"localdev:{Org:D}:document:1"))
            .With("plaintext_size_bytes", RowValue.Int64(plaintext))
            .With("ciphertext_size_bytes", RowValue.Int64(ciphertext ?? EnvelopeV2Rules.ExpectedDocumentCiphertextSize(plaintext)!.Value))
            .With("wrapped_data_key", RowValue.Bytes(Fill(60, wrapped)))
            .With("data_key_commitment_bytes", RowValue.Bytes(Fill(32, 0xee)))
            .With("data_key_identifier", RowValue.Uuid(batch))
            .With("key_wrap_algorithm_code", RowValue.Int32(1));

    private static Row FieldV1(Guid item, byte seed) => Sealed(Org, Record, Field, item, seed);

    private static RowCanonicalDocument Doc(Row row, Guid? operation = null) => RowCanonicalDecoder.Decode(Encode(row, Org, operation ?? Guid.CreateVersion7(), 1).Bytes);

    private static string Refusal(Action action) => Assert.Throws<AuthorityException>(action).Message;

    // ---- per-row shape (package §2.3) ---------------------------------------------------------------------------------------

    [Fact]
    public void WellFormedV2FieldAndDocumentRowsHaveAValidShape()
    {
        Assert.Null(EnvelopeV2Rules.Validate(Doc(FieldV2(Guid.CreateVersion7(), Guid.CreateVersion7()))));
        Assert.Null(EnvelopeV2Rules.Validate(Doc(FieldV2(Guid.CreateVersion7(), Guid.CreateVersion7(), wrapCode: 2, family: "vault-transit"))));
        Assert.Null(EnvelopeV2Rules.Validate(Doc(DocumentV2(Guid.CreateVersion7(), Guid.CreateVersion7()))));
        Assert.True(EnvelopeV2Rules.IsV2(Doc(FieldV2(Guid.CreateVersion7(), Guid.CreateVersion7()))));
        Assert.False(EnvelopeV2Rules.IsV2(Doc(FieldV1(Guid.CreateVersion7(), 10))));
    }

    public static TheoryData<string, string> MalformedFields => new()
    {
        { "envelope version 1", EnvelopeRules.Reasons.VersionUnsupported },
        { "algorithm", EnvelopeRules.Reasons.AlgorithmUnsupported },
        { "wrap code 0", EnvelopeV2Rules.Reasons.WrapAlgorithmUnsupported },
        { "wrap code 3 (reserved)", EnvelopeV2Rules.Reasons.WrapAlgorithmUnsupported },
        { "wrap code 4 (reserved)", EnvelopeV2Rules.Reasons.WrapAlgorithmUnsupported },
        { "family differs from the wrap code", EnvelopeV2Rules.Reasons.KeyReferenceFamily },
        { "legacy v1 reference", EnvelopeRules.Reasons.KeyReferenceInvalid },
        { "unknown family", EnvelopeRules.Reasons.KeyReferenceInvalid },
        { "foreign organization", EnvelopeRules.Reasons.KeyReferenceOrganization },
        { "uppercase organization", EnvelopeRules.Reasons.KeyReferenceInvalid },
        { "document purpose", EnvelopeRules.Reasons.KeyReferencePurpose },
        { "version with a leading zero", EnvelopeRules.Reasons.KeyReferenceInvalid },
        { "version 0", EnvelopeRules.Reasons.KeyReferenceInvalid },
        { "reference too long", EnvelopeRules.Reasons.KeyReferenceInvalid },
        { "reference with a space", EnvelopeRules.Reasons.KeyReferenceInvalid },
        { "data-key id version 4", EnvelopeV2Rules.Reasons.DataKeyIdentifierInvalid },
        { "data-key id all zero", EnvelopeV2Rules.Reasons.DataKeyIdentifierInvalid },
        { "data-key id wrong variant", EnvelopeV2Rules.Reasons.DataKeyIdentifierInvalid },
        { "commitment 31 bytes", EnvelopeV2Rules.Reasons.CommitmentLength },
        { "commitment 33 bytes", EnvelopeV2Rules.Reasons.CommitmentLength },
        { "wrapped key 59 bytes", EnvelopeRules.Reasons.WrappedKeyLength },
        { "wrapped key 61 bytes", EnvelopeRules.Reasons.WrappedKeyLength },
        { "nonce 11 bytes", EnvelopeRules.Reasons.NonceLength },
        { "tag 15 bytes", EnvelopeRules.Reasons.TagLength },
        { "ciphertext 12,001 bytes", EnvelopeV2Rules.Reasons.CiphertextLength },
        { "unknown logical type", EnvelopeRules.Reasons.LogicalType },
    };

    private static Row MalformedField(string variant)
    {
        var row = FieldV2(Guid.CreateVersion7(), Guid.CreateVersion7());
        static Guid WithBytes(Guid source, Action<byte[]> change)
        {
            var bytes = source.ToByteArray(bigEndian: true);
            change(bytes);
            return new Guid(bytes, bigEndian: true);
        }

        return variant switch
        {
            "envelope version 1" => row.With("encryption_envelope_version", RowValue.Int32(1)),
            "algorithm" => row.With("encryption_algorithm_name", RowValue.Text("AES-128-GCM")),
            "wrap code 0" => row.With("key_wrap_algorithm_code", RowValue.Int32(0)),
            "wrap code 3 (reserved)" => row.With("key_wrap_algorithm_code", RowValue.Int32(3)),
            "wrap code 4 (reserved)" => row.With("key_wrap_algorithm_code", RowValue.Int32(4)),
            "family differs from the wrap code" => row.With("encryption_key_reference", RowValue.Text($"vault-transit:{Org:D}:field-value:1")),
            "legacy v1 reference" => row.With("encryption_key_reference", RowValue.Text(EnvelopeRules.LegacyKeyReference)),
            "unknown family" => row.With("encryption_key_reference", RowValue.Text($"azure-hsm:{Org:D}:field-value:1")),
            "foreign organization" => row.With("encryption_key_reference", RowValue.Text($"localdev:{Guid.CreateVersion7():D}:field-value:1")),
            "uppercase organization" => row.With("encryption_key_reference", RowValue.Text($"localdev:{Org.ToString("D").ToUpperInvariant()}:field-value:1")),
            "document purpose" => row.With("encryption_key_reference", RowValue.Text($"localdev:{Org:D}:document:1")),
            "version with a leading zero" => row.With("encryption_key_reference", RowValue.Text($"localdev:{Org:D}:field-value:01")),
            "version 0" => row.With("encryption_key_reference", RowValue.Text($"localdev:{Org:D}:field-value:0")),
            "reference too long" => row.With("encryption_key_reference", RowValue.Text($"localdev:{Org:D}:field-value:1" + new string('1', 90))),
            "reference with a space" => row.With("encryption_key_reference", RowValue.Text($"localdev:{Org:D}:field-value: 1")),
            "data-key id version 4" => row.With("data_key_identifier", RowValue.Uuid(Guid.NewGuid())),
            "data-key id all zero" => row.With("data_key_identifier", RowValue.Uuid(Guid.Empty)),
            "data-key id wrong variant" => row.With("data_key_identifier", RowValue.Uuid(WithBytes(Guid.CreateVersion7(), b => b[8] = (byte)(b[8] & 0x3F)))),
            "commitment 31 bytes" => row.With("data_key_commitment_bytes", RowValue.Bytes(Fill(31, 1))),
            "commitment 33 bytes" => row.With("data_key_commitment_bytes", RowValue.Bytes(Fill(33, 1))),
            "wrapped key 59 bytes" => row.With("wrapped_data_key_bytes", RowValue.Bytes(Fill(59, 1))),
            "wrapped key 61 bytes" => row.With("wrapped_data_key_bytes", RowValue.Bytes(Fill(61, 1))),
            "nonce 11 bytes" => row.With("encryption_nonce_bytes", RowValue.Bytes(Fill(11, 1))),
            "tag 15 bytes" => row.With("encryption_auth_tag_bytes", RowValue.Bytes(Fill(15, 1))),
            "ciphertext 12,001 bytes" => row.With("encrypted_value_bytes", RowValue.Bytes(Fill(12_001, 1))),
            "unknown logical type" => row.With("encrypted_logical_type", RowValue.Text("Binary")),
            _ => throw new ArgumentOutOfRangeException(nameof(variant)),
        };
    }

    [Theory]
    [MemberData(nameof(MalformedFields))]
    public void EveryMalformedV2FieldColumnIsRefusedWithItsReason(string variant, string reason) =>
        Assert.Equal(reason, EnvelopeV2Rules.Validate(Doc(MalformedField(variant))));

    /// <summary>
    /// A NULL v2 column cannot be encoded under the registered schema 2 (the canonicalizer refuses it, and S-9 refuses it at the
    /// Authority); encoded under a deliberately nullable schema, the v2 shape rule still fails closed on it.
    /// </summary>
    [Theory]
    [InlineData("data_key_identifier")]
    [InlineData("key_wrap_algorithm_code")]
    [InlineData("data_key_commitment_bytes")]
    public void ANullV2ColumnFailsClosed(string column)
    {
        var nullable = new RowSchemaDefinition(FieldsV2.EntityKind, 2, FieldsV2.Columns.Select(c => c.ColumnName == column ? c with { Nullable = true } : c).ToList());
        var row = FieldV2(Guid.CreateVersion7(), Guid.CreateVersion7()) with { Schema = nullable };
        Assert.Equal(EnvelopeRules.Reasons.ColumnMissing, EnvelopeV2Rules.Validate(Doc(row.With(column, null))));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(4_000)]
    [InlineData(12_000)]
    public void TheFieldCiphertextBoundIsTheCodecMaximum(int length)
    {
        // D-8: 4,000 UTF-16 code units at most 3 UTF-8 bytes each; 0 bytes is AES-GCM of the empty string (allowed in v2).
        Assert.Equal(12_000, EnvelopeV2Rules.MaximumFieldCiphertextBytes);
        Assert.Null(EnvelopeV2Rules.Validate(Doc(FieldV2(Guid.CreateVersion7(), Guid.CreateVersion7()).With("encrypted_value_bytes", RowValue.Bytes(Fill(length, 7))))));
    }

    [Theory]
    [InlineData(0, 20)]                  // D0
    [InlineData(37, 57)]                 // D1
    [InlineData(1_048_576, 1_048_596)]   // D2: exactly one full segment
    [InlineData(1_048_581, 1_048_621)]   // D3: one full segment and five bytes
    [InlineData(2_097_152, 2_097_192)]   // D4: exactly two full segments
    [InlineData(1, 21)]
    [InlineData(1_048_577, 1_048_617)]
    public void TheDocumentCiphertextSizeFormulaMatchesTheE3M1Vectors(long plaintext, long ciphertext)
    {
        Assert.Equal(ciphertext, EnvelopeV2Rules.ExpectedDocumentCiphertextSize(plaintext));
        Assert.Null(EnvelopeV2Rules.Validate(Doc(DocumentV2(Guid.CreateVersion7(), Guid.CreateVersion7(), plaintext, ciphertext))));
        Assert.Equal(EnvelopeV2Rules.Reasons.CiphertextSize, EnvelopeV2Rules.Validate(Doc(DocumentV2(Guid.CreateVersion7(), Guid.CreateVersion7(), plaintext, ciphertext - 1))));
        Assert.Equal(EnvelopeV2Rules.Reasons.CiphertextSize, EnvelopeV2Rules.Validate(Doc(DocumentV2(Guid.CreateVersion7(), Guid.CreateVersion7(), plaintext, ciphertext + 1))));
    }

    [Fact]
    public void MalformedV2DocumentColumnsAreRefused()
    {
        Assert.Null(EnvelopeV2Rules.ExpectedDocumentCiphertextSize(-1));
        Assert.Null(EnvelopeV2Rules.ExpectedDocumentCiphertextSize(long.MaxValue));
        Assert.Equal(EnvelopeV2Rules.Reasons.CiphertextSize, EnvelopeV2Rules.Validate(Doc(DocumentV2(Guid.CreateVersion7(), Guid.CreateVersion7(), -1, 19))));
        Assert.Equal(EnvelopeRules.Reasons.NoncePrefixLength, EnvelopeV2Rules.Validate(Doc(DocumentV2(Guid.CreateVersion7(), Guid.CreateVersion7()).With("encryption_nonce_prefix", RowValue.Bytes(Fill(7, 1))))));
        Assert.Equal(EnvelopeRules.Reasons.SegmentSize, EnvelopeV2Rules.Validate(Doc(DocumentV2(Guid.CreateVersion7(), Guid.CreateVersion7()).With("encryption_segment_size_bytes", RowValue.Int32(65_536)))));
        Assert.Equal(EnvelopeRules.Reasons.KeyReferencePurpose, EnvelopeV2Rules.Validate(Doc(DocumentV2(Guid.CreateVersion7(), Guid.CreateVersion7()).With("encryption_key_reference", RowValue.Text($"localdev:{Org:D}:field-value:1")))));
        Assert.Equal(EnvelopeRules.Reasons.WrappedKeyLength, EnvelopeV2Rules.Validate(Doc(DocumentV2(Guid.CreateVersion7(), Guid.CreateVersion7()).With("wrapped_data_key", RowValue.Bytes(Fill(48, 1))))));
    }

    // ---- batch consistency and cross-version separation (re-derivable: Prepare, Commit) ---------------------------------------

    [Fact]
    public void AConsistentMultiItemFieldBatchAndOneDocumentPerIdPass()
    {
        var batch = Guid.CreateVersion7();
        EnvelopeV2Rules.Enforce([Doc(FieldV2(Guid.CreateVersion7(), batch, nonce: 1)), Doc(FieldV2(Guid.CreateVersion7(), batch, nonce: 2)), Doc(FieldV2(Guid.CreateVersion7(), batch, nonce: 3)),
            Doc(DocumentV2(Guid.CreateVersion7(), Guid.CreateVersion7())), Doc(FieldV1(Guid.CreateVersion7(), 40))]);
    }

    public static TheoryData<string, string> InconsistentBatches => new()
    {
        { "different commitment", EnvelopeV2Rules.Reasons.BatchInconsistent },
        { "different wrap code", EnvelopeV2Rules.Reasons.BatchInconsistent },
        { "different reference", EnvelopeV2Rules.Reasons.BatchInconsistent },
        { "different wrapped key under one id", EnvelopeV2Rules.Reasons.BatchInconsistent },
        { "one wrapped key under two ids", EnvelopeRules.Reasons.WrappedKeyReused },
        { "repeated nonce", EnvelopeRules.Reasons.NonceReused },
        { "repeated item", EnvelopeV2Rules.Reasons.BatchInconsistent },
        { "two documents under one id", EnvelopeV2Rules.Reasons.BatchInconsistent },
        { "a field and a document under one id", EnvelopeV2Rules.Reasons.BatchInconsistent },
        { "a v2 wrapped key equal to a v1 wrapped key", EnvelopeRules.Reasons.WrappedKeyReused },
        { "a v2 document wrapped key equal to a v1 document wrapped key", EnvelopeRules.Reasons.WrappedKeyReused },
    };

    [Theory]
    [MemberData(nameof(InconsistentBatches))]
    public void AnInconsistentBatchOrACrossVersionWrappedKeyIsRefused(string variant, string reason)
    {
        var batch = Guid.CreateVersion7();
        var item = Guid.CreateVersion7();
        var first = FieldV2(Guid.CreateVersion7(), batch, nonce: 1);
        var second = variant switch
        {
            "different commitment" => FieldV2(item, batch, nonce: 2, commitment: 0x01),
            "different wrap code" => FieldV2(item, batch, nonce: 2, wrapCode: 2, family: "vault-transit"),
            "different reference" => FieldV2(item, batch, nonce: 2).With("encryption_key_reference", RowValue.Text($"localdev:{Org:D}:field-value:2")),
            "different wrapped key under one id" => FieldV2(item, batch, nonce: 2, wrapped: 0xab),
            "one wrapped key under two ids" => FieldV2(item, Guid.CreateVersion7(), nonce: 2),
            "repeated nonce" => FieldV2(item, batch, nonce: 1),
            "repeated item" => first.With("encryption_nonce_bytes", RowValue.Bytes(Fill(12, 9))),
            "two documents under one id" => DocumentV2(Guid.CreateVersion7(), batch, wrapped: 0xaa),
            "a field and a document under one id" => DocumentV2(Guid.CreateVersion7(), batch, wrapped: 0xaa),
            "a v2 wrapped key equal to a v1 wrapped key" => FieldV1(item, 40).With("wrapped_data_key_bytes", RowValue.Bytes(Fill(60, 0xaa))),
            "a v2 document wrapped key equal to a v1 document wrapped key" => Attachment(Org, Record, item).With("wrapped_data_key", RowValue.Bytes(Fill(60, 0x55))),
            _ => throw new ArgumentOutOfRangeException(nameof(variant)),
        };

        var rows = variant switch
        {
            "two documents under one id" => new[] { DocumentV2(Guid.CreateVersion7(), batch, wrapped: 0xaa), second },
            "a v2 document wrapped key equal to a v1 document wrapped key" => [DocumentV2(Guid.CreateVersion7(), Guid.CreateVersion7(), wrapped: 0x55), second],
            _ => [first, second],
        };

        Assert.Equal("ENVELOPE_INVALID: " + reason, Refusal(() => EnvelopeV2Rules.Enforce(rows.Select(r => Doc(r)).ToList())));
        Assert.Equal("ENVELOPE_INVALID: " + reason, Refusal(() => EnvelopeRules.Enforce(rows.Select(r => Doc(r)).ToList()))); // the dispatching entry point
    }

    [Fact]
    public void TheV1RulesStillJudgeV1RowsWhenV2RowsArePresent()
    {
        var batch = Guid.CreateVersion7();
        var v1Shared = Fill(60, 0x40);
        var rows = new[] { FieldV2(Guid.CreateVersion7(), batch, nonce: 1), FieldV1(Guid.CreateVersion7(), 40).With("wrapped_data_key_bytes", RowValue.Bytes(v1Shared)), FieldV1(Guid.CreateVersion7(), 50).With("wrapped_data_key_bytes", RowValue.Bytes(v1Shared)) };
        Assert.Equal("ENVELOPE_INVALID: " + EnvelopeRules.Reasons.WrappedKeyReused, Refusal(() => EnvelopeRules.Enforce(rows.Select(r => Doc(r)).ToList())));
        Assert.Equal("ENVELOPE_INVALID: " + EnvelopeRules.Reasons.KeyReferenceInvalid, Refusal(() => EnvelopeRules.Enforce([Doc(FieldV2(Guid.CreateVersion7(), batch)), Doc(FieldV1(Guid.CreateVersion7(), 60).With("encryption_key_reference", RowValue.Text("bogus")))])));
    }

    // ---- Prepare-only classification (CARV 2) ---------------------------------------------------------------------------------

    private static (Guid, RowCanonicalDocument) Item(Row row, Guid? operation = null) => (row.ItemId, Doc(row, operation));

    [Fact]
    public void ByteIdenticalCarriesOfTheCurrentHeadPassAndRemovalsArePermitted()
    {
        var batch = Guid.CreateVersion7();
        var (a, b, d) = (FieldV2(Guid.CreateVersion7(), batch, nonce: 1), FieldV2(Guid.CreateVersion7(), batch, nonce: 2), DocumentV2(Guid.CreateVersion7(), Guid.CreateVersion7()));
        List<(Guid, RowCanonicalDocument)> current = [Item(a), Item(b), Item(d)];
        EnvelopeV2Rules.EnforceAgainstCurrent([Item(a), Item(b), Item(d)], current); // carried under a new operation binding and storage key
        EnvelopeV2Rules.EnforceAgainstCurrent([Item(a)], current);                     // b and the document removed
        EnvelopeV2Rules.EnforceAgainstCurrent([], current);
    }

    public static TheoryData<string, string> Transitions => new()
    {
        { "none -> v2 (creation)", EnvelopeV2Rules.Reasons.VersionNotEnabled },
        { "v1 -> v2", EnvelopeV2Rules.Reasons.VersionNotEnabled },
        { "v2 -> v2 re-sealed under a fresh id", EnvelopeV2Rules.Reasons.VersionNotEnabled },
        { "v2 -> v2 content changed under the same id", EnvelopeV2Rules.Reasons.DataKeyIdentifierReused },
        { "v2 -> v2 identity changed under the same id", EnvelopeV2Rules.Reasons.DataKeyIdentifierReused },
        { "a new item joining a carried batch", EnvelopeV2Rules.Reasons.DataKeyIdentifierReused },
        { "a carry from an older revision", EnvelopeV2Rules.Reasons.VersionNotEnabled },
        { "v2 -> v1 field", EnvelopeV2Rules.Reasons.Downgrade },
        { "v2 -> v1 document", EnvelopeV2Rules.Reasons.Downgrade },
        { "v1 reusing a current v2 wrapped key", EnvelopeRules.Reasons.WrappedKeyReused },
    };

    [Theory]
    [MemberData(nameof(Transitions))]
    public void PerItemTransitionsFollowTheM2dRules(string transition, string reason)
    {
        var batch = Guid.CreateVersion7();
        var item = Guid.CreateVersion7();
        var a = FieldV2(item, batch, nonce: 1);
        var b = FieldV2(Guid.CreateVersion7(), batch, nonce: 2);
        var d = DocumentV2(Guid.CreateVersion7(), Guid.CreateVersion7());
        List<(Guid, RowCanonicalDocument)> current = [Item(a), Item(b), Item(d)];
        List<(Guid, RowCanonicalDocument)> candidate = transition switch
        {
            "none -> v2 (creation)" => [Item(a)],
            "v1 -> v2" => [Item(FieldV2(item, Guid.CreateVersion7(), nonce: 1, wrapped: 0xab)), Item(b), Item(d)], // a fresh batch: not DK-3
            "v2 -> v2 re-sealed under a fresh id" => [Item(FieldV2(item, Guid.CreateVersion7(), nonce: 1, wrapped: 0xab)), Item(b), Item(d)],
            "v2 -> v2 content changed under the same id" => [Item(a.With("encrypted_value_bytes", RowValue.Bytes(Fill(24, 0x33)))), Item(b), Item(d)],
            "v2 -> v2 identity changed under the same id" => [Item(a.With("form_record_group_id", RowValue.Uuid(Guid.CreateVersion7()))), Item(b), Item(d)],
            "a new item joining a carried batch" => [Item(a), Item(b), Item(d), Item(FieldV2(Guid.CreateVersion7(), batch, nonce: 3))],
            "a carry from an older revision" => [Item(a), Item(b), Item(d), Item(FieldV2(Guid.CreateVersion7(), Guid.CreateVersion7(), nonce: 4, wrapped: 0xac))],
            "v2 -> v1 field" => [Item(FieldV1(item, 40)), Item(b), Item(d)],
            "v2 -> v1 document" => [Item(a), Item(b), Item(Attachment(Org, Record, d.ItemId))],
            "v1 reusing a current v2 wrapped key" => [Item(FieldV1(Guid.CreateVersion7(), 40).With("wrapped_data_key_bytes", RowValue.Bytes(Fill(60, 0xaa))))],
            _ => throw new ArgumentOutOfRangeException(nameof(transition)),
        };

        if (transition == "none -> v2 (creation)")
        {
            current = [];
        }
        else if (transition == "v1 -> v2")
        {
            current = [Item(FieldV1(item, 40)), Item(b), Item(d)];
        }

        Assert.Equal("ENVELOPE_INVALID: " + reason, Refusal(() => EnvelopeV2Rules.EnforceAgainstCurrent(candidate, current)));
    }

    [Fact]
    public void TheV1AgainstCurrentRuleIgnoresV2RowsAndKeepsItsV1Behaviour()
    {
        // A v2 batch in the current revision shares one wrapped key; the v1 rule must neither index it nor judge carried v2 rows by it.
        var batch = Guid.CreateVersion7();
        var (a, b) = (FieldV2(Guid.CreateVersion7(), batch, nonce: 1), FieldV2(Guid.CreateVersion7(), batch, nonce: 2));
        EnvelopeRules.EnforceAgainstCurrent([Item(a), Item(b)], [Item(a), Item(b)]);

        var v1 = FieldV1(Guid.CreateVersion7(), 40);
        var changed = v1.With("encrypted_value_bytes", RowValue.Bytes(Fill(24, 0x99)));
        Assert.Equal("ENVELOPE_INVALID: " + EnvelopeRules.Reasons.NonceReused, Refusal(() => EnvelopeRules.EnforceAgainstCurrent([Item(changed), Item(a)], [Item(v1), Item(a), Item(b)])));
    }

    // ---- Commit and census -------------------------------------------------------------------------------------------------

    [Fact]
    public void CommitRefusesEveryV2RowAndNothingElse()
    {
        Assert.Equal(EnvelopeV2Rules.Reasons.VersionNotEnabled, EnvelopeV2Rules.CommitViolation([Doc(FieldV1(Guid.CreateVersion7(), 40)), Doc(FieldV2(Guid.CreateVersion7(), Guid.CreateVersion7()))]));
        Assert.Equal(EnvelopeV2Rules.Reasons.VersionNotEnabled, EnvelopeV2Rules.CommitViolation([Doc(DocumentV2(Guid.CreateVersion7(), Guid.CreateVersion7()))]));
        Assert.Null(EnvelopeV2Rules.CommitViolation([Doc(FieldV1(Guid.CreateVersion7(), 40)), Doc(Attachment(Org, Record, Guid.CreateVersion7())), Doc(Header(Org, Record))]));
    }

    [Fact]
    public void TheCensusJudgesV1RowsAsBeforeAndV2RowsByTheV2Rules()
    {
        var v1Only = new EnvelopeV2Rules.CensusState();
        var shared = FieldV1(Guid.CreateVersion7(), 40);
        Assert.True(v1Only.Accepts(Doc(shared)));
        Assert.False(v1Only.Accepts(Doc(FieldV1(Guid.CreateVersion7(), 50).With("wrapped_data_key_bytes", RowValue.Bytes(Fill(60, 43)))))); // E3-M2a: one value per v1 key

        var batch = Guid.CreateVersion7();
        var v2 = new EnvelopeV2Rules.CensusState();
        Assert.True(v2.Accepts(Doc(FieldV2(Guid.CreateVersion7(), batch, nonce: 1))));
        Assert.True(v2.Accepts(Doc(FieldV2(Guid.CreateVersion7(), batch, nonce: 2)))); // F-14: a v2 batch is not an anomaly
        Assert.False(v2.Accepts(Doc(FieldV2(Guid.CreateVersion7(), batch, nonce: 3, commitment: 0x01)))); // inconsistent batch
        Assert.False(v2.Accepts(Doc(FieldV2(Guid.CreateVersion7(), batch, nonce: 2)))); // repeated nonce
        Assert.False(v2.Accepts(Doc(FieldV2(Guid.CreateVersion7(), Guid.CreateVersion7(), nonce: 4)))); // the batch's wrapped key under another id
        Assert.False(v2.Accepts(Doc(FieldV1(Guid.CreateVersion7(), 60).With("wrapped_data_key_bytes", RowValue.Bytes(Fill(60, 0xaa)))))); // v1 reusing a v2 key
        Assert.False(v2.Accepts(Doc(MalformedField("commitment 31 bytes"))));
    }

    // ---- registry --------------------------------------------------------------------------------------------------------

    [Fact]
    public void TheAuthorityRegistryAndConstantsAreTheApprovedValues()
    {
        Assert.Equal(WrapCodes, EnvelopeV2Rules.WrapAlgorithms.Keys.Order());
        Assert.Equal(("localdev", 60), EnvelopeV2Rules.WrapAlgorithms[1]);
        Assert.Equal(("vault-transit", 60), EnvelopeV2Rules.WrapAlgorithms[2]);
        Assert.Equal(32, EnvelopeV2Rules.CommitmentLength);
        Assert.Equal(20, EnvelopeV2Rules.DocumentSegmentOverheadBytes);
        Assert.Equal(new HashSet<int> { 1 }, EnvelopeRules.AllowedEnvelopeVersions); // D-12: the v1 allow-list is not widened
        Assert.Same(EnvelopeRules.AllowedEnvelopeVersions, ContentDeltaRules.AllowedEnvelopeVersions); // the share check still sees {1} only
        Assert.All(EnvelopeV2Rules.WrapAlgorithms.Values, w => Assert.Contains(w.Family, EnvelopeRules.ScopedReferenceFamilies));
    }
}
