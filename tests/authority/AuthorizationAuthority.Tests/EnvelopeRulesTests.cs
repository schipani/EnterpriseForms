using AuthorizationAuthority.Application.Common;
using AuthorizationAuthority.Application.Features.Content;
using AuthorizationAuthority.Contracts.V1;
using EnterpriseForms.Integrity.Canonical;
using Xunit;
using static AuthorizationAuthority.Tests.ContributorWriteFixtures;

namespace AuthorizationAuthority.Tests;

/// <summary>
/// E3-M2a: the central Envelope v1 rules (<see cref="EnvelopeRules"/>), decided from canonical bytes alone. Every rule has a
/// refusing case and the shapes the business actually writes (legacy reference, both scoped families) are accepted. The write
/// paths that apply these rules are covered in ContributorWriteProtocolTests (owner, share editor, commit) and
/// ProtectionReconciliationTests (protection migration, census).
/// </summary>
public sealed class EnvelopeRulesTests
{
    private static readonly Guid Org = Guid.CreateVersion7();
    private static readonly Guid Record = Guid.CreateVersion7();
    private static readonly int[] OnlyVersionOne = [1];

    private static RowCanonicalDocument Doc(Row row, RowSchemaDefinition? schema = null) =>
        RowCanonicalDecoder.Decode(Encode(schema is null ? row : row with { Schema = schema }, Org, Guid.CreateVersion7(), 1).Bytes);

    private static Row Field(Func<Row, Row>? change = null)
    {
        var row = Sealed(Org, Record, Guid.CreateVersion7(), Guid.CreateVersion7(), 10);
        return change is null ? row : change(row);
    }

    private static Row Document(Func<Row, Row>? change = null)
    {
        var row = Attachment(Org, Record, Guid.CreateVersion7());
        return change is null ? row : change(row);
    }

    private static string Scoped(string family, Guid organization, string purpose, string version = "1") => $"{family}:{organization:D}:{purpose}:{version}";

    [Fact]
    public void TheShapesTheBusinessWritesAreAccepted()
    {
        Assert.Null(EnvelopeRules.Validate(Doc(Field())));
        Assert.Null(EnvelopeRules.Validate(Doc(Document())));
        foreach (var family in new[] { "localdev", "vault-transit" })
        {
            Assert.Null(EnvelopeRules.Validate(Doc(Field(r => r.With("encryption_key_reference", RowValue.Text(Scoped(family, Org, "field-value")))))));
            Assert.Null(EnvelopeRules.Validate(Doc(Document(r => r.With("encryption_key_reference", RowValue.Text(Scoped(family, Org, "document")))))));
        }

        Assert.Null(EnvelopeRules.Validate(Doc(Field(r => r.With("encryption_key_reference", RowValue.Text(Scoped("localdev", Org, "field-value", "2147483647")))))));
        foreach (var type in EnvelopeRules.LogicalTypes)
        {
            Assert.Null(EnvelopeRules.Validate(Doc(Field(r => r.With("encrypted_logical_type", RowValue.Text(type))))));
        }
    }

    [Fact]
    public void RowsWithoutAnEnvelopeAreNotJudged()
    {
        Assert.False(EnvelopeRules.IsEnvelopeRow(Doc(Text(Org, Record, Guid.CreateVersion7(), Guid.CreateVersion7(), "plain"))));
        Assert.Null(EnvelopeRules.Validate(Doc(Text(Org, Record, Guid.CreateVersion7(), Guid.CreateVersion7(), "plain"))));
        Assert.Null(EnvelopeRules.Validate(Doc(Header(Org, Record))));
    }

    public static TheoryData<string, string> MalformedFieldEnvelopes => new()
    {
        { "version 0", EnvelopeRules.Reasons.VersionUnsupported },
        { "version 2", EnvelopeRules.Reasons.VersionUnsupported },
        { "algorithm", EnvelopeRules.Reasons.AlgorithmUnsupported },
        { "algorithm case", EnvelopeRules.Reasons.AlgorithmUnsupported },
        { "nonce 11", EnvelopeRules.Reasons.NonceLength },
        { "nonce 13", EnvelopeRules.Reasons.NonceLength },
        { "tag 15", EnvelopeRules.Reasons.TagLength },
        { "ciphertext empty", EnvelopeRules.Reasons.CiphertextEmpty },
        { "wrapped 0", EnvelopeRules.Reasons.WrappedKeyLength },
        { "wrapped 59", EnvelopeRules.Reasons.WrappedKeyLength },
        { "wrapped 61", EnvelopeRules.Reasons.WrappedKeyLength },
        { "logical type", EnvelopeRules.Reasons.LogicalType },
        { "document purpose", EnvelopeRules.Reasons.KeyReferencePurpose },
        { "idempotency purpose", EnvelopeRules.Reasons.KeyReferencePurpose },
        { "other organization", EnvelopeRules.Reasons.KeyReferenceOrganization },
    };

    [Theory]
    [MemberData(nameof(MalformedFieldEnvelopes))]
    public void EachMalformedFieldEnvelopeIsRefusedWithItsReason(string variant, string reason)
    {
        Row change(Row r) => variant switch
        {
            "version 0" => r.With("encryption_envelope_version", RowValue.Int32(0)),
            "version 2" => r.With("encryption_envelope_version", RowValue.Int32(2)),
            "algorithm" => r.With("encryption_algorithm_name", RowValue.Text("AES-128-CBC")),
            "algorithm case" => r.With("encryption_algorithm_name", RowValue.Text("aes-256-gcm")),
            "nonce 11" => r.With("encryption_nonce_bytes", RowValue.Bytes(Fill(11, 1))),
            "nonce 13" => r.With("encryption_nonce_bytes", RowValue.Bytes(Fill(13, 1))),
            "tag 15" => r.With("encryption_auth_tag_bytes", RowValue.Bytes(Fill(15, 1))),
            "ciphertext empty" => r.With("encrypted_value_bytes", RowValue.Bytes(Array.Empty<byte>())),
            "wrapped 0" => r.With("wrapped_data_key_bytes", RowValue.Bytes(Array.Empty<byte>())),
            "wrapped 59" => r.With("wrapped_data_key_bytes", RowValue.Bytes(Fill(59, 1))),
            "wrapped 61" => r.With("wrapped_data_key_bytes", RowValue.Bytes(Fill(61, 1))),
            "logical type" => r.With("encrypted_logical_type", RowValue.Text("Binary")),
            "document purpose" => r.With("encryption_key_reference", RowValue.Text(Scoped("localdev", Org, "document"))),
            "idempotency purpose" => r.With("encryption_key_reference", RowValue.Text(Scoped("vault-transit", Org, "idempotency-protection"))),
            "other organization" => r.With("encryption_key_reference", RowValue.Text(Scoped("localdev", Guid.CreateVersion7(), "field-value"))),
            _ => throw new ArgumentOutOfRangeException(nameof(variant)),
        };

        Assert.Equal(reason, EnvelopeRules.Validate(Doc(Field(change))));
    }

    public static TheoryData<string, string> MalformedDocumentEnvelopes => new()
    {
        { "version 2", EnvelopeRules.Reasons.VersionUnsupported },
        { "algorithm", EnvelopeRules.Reasons.AlgorithmUnsupported },
        { "prefix 12", EnvelopeRules.Reasons.NoncePrefixLength },
        { "prefix 7", EnvelopeRules.Reasons.NoncePrefixLength },
        { "segment", EnvelopeRules.Reasons.SegmentSize },
        { "wrapped 32", EnvelopeRules.Reasons.WrappedKeyLength },
        { "field purpose", EnvelopeRules.Reasons.KeyReferencePurpose },
        { "other organization", EnvelopeRules.Reasons.KeyReferenceOrganization },
        { "reference", EnvelopeRules.Reasons.KeyReferenceInvalid },
    };

    [Theory]
    [MemberData(nameof(MalformedDocumentEnvelopes))]
    public void EachMalformedDocumentEnvelopeIsRefusedWithItsReason(string variant, string reason)
    {
        Row change(Row r) => variant switch
        {
            "version 2" => r.With("encryption_envelope_version", RowValue.Int32(2)),
            "algorithm" => r.With("encryption_algorithm_name", RowValue.Text("CHACHA20-POLY1305")),
            "prefix 12" => r.With("encryption_nonce_prefix", RowValue.Bytes(Fill(12, 1))),
            "prefix 7" => r.With("encryption_nonce_prefix", RowValue.Bytes(Fill(7, 1))),
            "segment" => r.With("encryption_segment_size_bytes", RowValue.Int32(65536)),
            "wrapped 32" => r.With("wrapped_data_key", RowValue.Bytes(Fill(32, 1))),
            "field purpose" => r.With("encryption_key_reference", RowValue.Text(Scoped("localdev", Org, "field-value"))),
            "other organization" => r.With("encryption_key_reference", RowValue.Text(Scoped("vault-transit", Guid.CreateVersion7(), "document"))),
            "reference" => r.With("encryption_key_reference", RowValue.Text("local-dev-kek-v2")),
            _ => throw new ArgumentOutOfRangeException(nameof(variant)),
        };

        Assert.Equal(reason, EnvelopeRules.Validate(Doc(Document(change))));
    }

    [Theory]
    [InlineData("")]
    [InlineData("local-dev-kek-v1 ")]
    [InlineData("LOCAL-DEV-KEK-V1")]
    [InlineData("localdev")]
    [InlineData("localdev:{org}:field-value")]
    [InlineData("localdev:{org}:field-value:1:extra")]
    [InlineData("hsm:{org}:field-value:1")]
    [InlineData("LocalDev:{org}:field-value:1")]
    [InlineData("localdev:{ORG}:field-value:1")]
    [InlineData("localdev:{N}:field-value:1")]
    [InlineData("localdev:00000000-0000-0000-0000-000000000000:field-value:1")]
    [InlineData("localdev:{org}:field-value:0")]
    [InlineData("localdev:{org}:field-value:01")]
    [InlineData("localdev:{org}:field-value:+1")]
    [InlineData("localdev:{org}:field-value:-1")]
    [InlineData("localdev:{org}:field-value:2147483648")]
    [InlineData("localdev:{org}:field-value: 1")]
    [InlineData("localdev:{org}:Field-Value:1")]
    public void NonCanonicalKeyReferencesAreRefused(string template)
    {
        var reference = template.Replace("{org}", Org.ToString("D"), StringComparison.Ordinal).Replace("{ORG}", Org.ToString("D").ToUpperInvariant(), StringComparison.Ordinal).Replace("{N}", Org.ToString("N"), StringComparison.Ordinal);
        var reason = EnvelopeRules.Validate(Doc(Field(r => r.With("encryption_key_reference", RowValue.Text(reference)))));
        Assert.NotNull(reason);
        Assert.StartsWith("ENVELOPE_KEY_REFERENCE_", reason, StringComparison.Ordinal);
    }

    [Fact]
    public void AnEnvelopeUnderAnUnregisteredSchemaVersionIsRefused()
    {
        var schema2 = new RowSchemaDefinition(EncryptedValues.EntityKind, 2, EncryptedValues.Columns);
        Assert.Equal(EnvelopeRules.Reasons.SchemaUnsupported, EnvelopeRules.Validate(Doc(Field(), schema2)));
        var attachments2 = new RowSchemaDefinition(Attachments.EntityKind, 2, Attachments.Columns);
        Assert.Equal(EnvelopeRules.Reasons.SchemaUnsupported, EnvelopeRules.Validate(Doc(Document(), attachments2)));
    }

    [Fact]
    public void ARepeatedWrappedKeyAndNonceAmongFieldRowsIsRefusedButDistinctPairsAndDocumentsAreNot()
    {
        var a = Doc(Sealed(Org, Record, Guid.CreateVersion7(), Guid.CreateVersion7(), 10));
        var b = Doc(Sealed(Org, Record, Guid.CreateVersion7(), Guid.CreateVersion7(), 20));
        var twinOfA = Doc(Sealed(Org, Record, Guid.CreateVersion7(), Guid.CreateVersion7(), 10)); // another item, same key and nonce
        EnvelopeRules.Enforce([a, b, Doc(Header(Org, Record))]);

        var refused = Assert.Throws<AuthorityException>(() => EnvelopeRules.Enforce([a, b, twinOfA]));
        Assert.Equal(AuthorityErrorCodes.InvalidCommand, refused.ErrorCode);
        Assert.Contains(EnvelopeRules.Reasons.NonceReused, refused.Message, StringComparison.Ordinal);

        // E3-M2a remediation: one v1 data key per value, so the same wrapped key under a different nonce is refused too.
        var keyOfAOtherNonce = Doc(Sealed(Org, Record, Guid.CreateVersion7(), Guid.CreateVersion7(), 10).With("encryption_nonce_bytes", RowValue.Bytes(Fill(12, 99))));
        Assert.Equal("ENVELOPE_INVALID: " + EnvelopeRules.Reasons.WrappedKeyReused, Assert.Throws<AuthorityException>(() => EnvelopeRules.Enforce([a, keyOfAOtherNonce])).Message);

        // Documents have no per-revision wrapped-key rule in v1 (unchanged): two attachment rows with equal wraps are not refused here.
        EnvelopeRules.Enforce([Doc(Attachment(Org, Record, Guid.CreateVersion7())), Doc(Attachment(Org, Record, Guid.CreateVersion7()))]);
    }

    // ---- E3-M2a remediation: candidate field rows against the verified current revision ----

    private static (Guid ItemId, RowCanonicalDocument Document) Item(Row row) => (row.ItemId, Doc(row));

    private static string? AgainstCurrent(Row current, params Row[] candidate)
    {
        try
        {
            EnvelopeRules.EnforceAgainstCurrent(candidate.Select(Item), [Item(current)]);
            return null;
        }
        catch (AuthorityException ex)
        {
            return ex.Message;
        }
    }

    [Fact]
    public void AgainstTheCurrentRevisionOnlyAByteIdenticalCarryOfTheSameItemMayKeepItsKey()
    {
        var field = Guid.CreateVersion7();
        var item = Guid.CreateVersion7();
        var current = Sealed(Org, Record, field, item, 10);
        string Refused(string reason) => "ENVELOPE_INVALID: " + reason;

        // Allowed: the same item byte-identical (re-encoded under a new operation and storage key), a fresh envelope, no envelope.
        Assert.Null(AgainstCurrent(current, current));
        Assert.Null(AgainstCurrent(current, Sealed(Org, Record, field, item, 20)));
        Assert.Null(AgainstCurrent(current, Text(Org, Record, field, item, "now plaintext")));
        Assert.Null(AgainstCurrent(current, Sealed(Org, Record, Guid.CreateVersion7(), Guid.CreateVersion7(), 30), current));

        // Refused: the key and nonce for changed ciphertext or a changed tag, for another item, or the key under another nonce.
        Assert.Equal(Refused(EnvelopeRules.Reasons.NonceReused), AgainstCurrent(current, current.With("encrypted_value_bytes", RowValue.Bytes(Fill(24, 99)))));
        Assert.Equal(Refused(EnvelopeRules.Reasons.NonceReused), AgainstCurrent(current, current.With("encryption_auth_tag_bytes", RowValue.Bytes(Fill(16, 99)))));
        Assert.Equal(Refused(EnvelopeRules.Reasons.NonceReused), AgainstCurrent(current, Sealed(Org, Record, Guid.CreateVersion7(), Guid.CreateVersion7(), 10)));
        Assert.Equal(Refused(EnvelopeRules.Reasons.WrappedKeyReused), AgainstCurrent(current, current.With("encryption_nonce_bytes", RowValue.Bytes(Fill(12, 99)))));
        Assert.Equal(Refused(EnvelopeRules.Reasons.WrappedKeyReused), AgainstCurrent(current, Sealed(Org, Record, Guid.CreateVersion7(), Guid.CreateVersion7(), 10).With("encryption_nonce_bytes", RowValue.Bytes(Fill(12, 98)))));

        // Refused: an otherwise identical row that is not the same item in place (moved to another field or group occurrence,
        // relabelled to another logical type) is not a carry — the comparison covers every signed column except the operation.
        Assert.Equal(Refused(EnvelopeRules.Reasons.NonceReused), AgainstCurrent(current, current.With("field_definition_id", RowValue.Uuid(Guid.CreateVersion7()))));
        Assert.Equal(Refused(EnvelopeRules.Reasons.NonceReused), AgainstCurrent(current, current.With("form_record_group_id", RowValue.Uuid(Guid.CreateVersion7()))));
        Assert.Equal(Refused(EnvelopeRules.Reasons.NonceReused), AgainstCurrent(current, current.With("encrypted_logical_type", RowValue.Text("Integer"))));
    }

    [Fact]
    public void ARecordWithoutAcceptedContentAndDocumentRowsAreNotComparedAcrossRevisions()
    {
        EnvelopeRules.EnforceAgainstCurrent([Item(Sealed(Org, Record, Guid.CreateVersion7(), Guid.CreateVersion7(), 10))], []);

        // v1 documents keep their own per-version rules (unchanged): a carried attachment row keeps its wrapped key.
        var attachment = Attachment(Org, Record, Guid.CreateVersion7());
        EnvelopeRules.EnforceAgainstCurrent([Item(attachment), Item(Attachment(Org, Record, Guid.CreateVersion7()))], [Item(attachment)]);
    }

    [Fact]
    public void EnforceNamesTheReasonButNeverAnIdentifierOrBytes()
    {
        var item = Guid.CreateVersion7();
        var bad = Doc(Sealed(Org, Record, Guid.CreateVersion7(), item, 10).With("encryption_key_reference", RowValue.Text(Scoped("localdev", Guid.CreateVersion7(), "field-value"))));
        var refused = Assert.Throws<AuthorityException>(() => EnvelopeRules.Enforce([bad]));
        Assert.Equal(422, refused.HttpStatusCode);
        Assert.Equal("ENVELOPE_INVALID: " + EnvelopeRules.Reasons.KeyReferenceOrganization, refused.Message);
        Assert.DoesNotContain(Org.ToString("D"), refused.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(item.ToString("D"), refused.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TheShareEditorAllowListsAreTheCentralOnes()
    {
        Assert.Same(EnvelopeRules.AllowedEnvelopeVersions, ContentDeltaRules.AllowedEnvelopeVersions);
        Assert.Same(EnvelopeRules.AllowedAlgorithms, ContentDeltaRules.AllowedEnvelopeAlgorithms);
        Assert.Equal(OnlyVersionOne, EnvelopeRules.AllowedEnvelopeVersions.Order());
    }
}
