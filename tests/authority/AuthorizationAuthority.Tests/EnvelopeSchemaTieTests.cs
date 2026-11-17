using AuthorizationAuthority.Application.Common;
using AuthorizationAuthority.Application.Features.Content;
using AuthorizationAuthority.Contracts.V1;
using EnterpriseForms.Integrity.Canonical;
using Xunit;
using static AuthorizationAuthority.Tests.ContributorWriteFixtures;

namespace AuthorizationAuthority.Tests;

/// <summary>
/// E3-M2b-2 R-M2b2-11: the S-4 schema/envelope tie (<see cref="EnvelopeSchemaTie"/>), decided from canonical bytes alone and
/// independent of <see cref="EnvelopeRules"/>. Only schema 1 / envelope 1 and schema 2 / envelope 2 pass; every other pair of
/// both envelope kinds, and an envelope version the tie cannot read, is refused. The tie accepts nothing by itself: schema 2 /
/// envelope 2 passes the tie and is still refused by the E3-M2a schema refusal. The write paths are covered in
/// ContributorWriteProtocolTests.EnvelopeSchemaTie and ProtectionReconciliationTests.
/// </summary>
public sealed class EnvelopeSchemaTieTests
{
    private static readonly Guid Org = Guid.CreateVersion7();
    private static readonly Guid Record = Guid.CreateVersion7();

    private static RowSchemaDefinition V2(RowSchemaDefinition schema1) => new(schema1.EntityKind, 2,
        schema1.Columns.Concat([
            new RowSignedColumn("data_key_commitment_bytes", RowColumnType.Bytes, false),
            new RowSignedColumn("data_key_identifier", RowColumnType.Uuid, false),
            new RowSignedColumn("key_wrap_algorithm_code", RowColumnType.Int32, false),
        ]).OrderBy(c => c.ColumnName, StringComparer.Ordinal).ToList());

    private static Row Base(string kind) => kind == "field"
        ? Sealed(Org, Record, Guid.CreateVersion7(), Guid.CreateVersion7(), 10)
        : Attachment(Org, Record, Guid.CreateVersion7());

    /// <summary>A row of <paramref name="kind"/> encoded under <paramref name="schemaVersion"/> with the given envelope version (well-formed v2 metadata on schema 2).</summary>
    private static RowCanonicalDocument Pair(string kind, int schemaVersion, int envelopeVersion)
    {
        var row = Base(kind).With("encryption_envelope_version", RowValue.Int32(envelopeVersion));
        if (schemaVersion == 2)
        {
            row = (row with { Schema = V2(row.Schema) })
                .With("data_key_commitment_bytes", RowValue.Bytes(Fill(32, 0xdd)))
                .With("data_key_identifier", RowValue.Uuid(Guid.CreateVersion7()))
                .With("key_wrap_algorithm_code", RowValue.Int32(1));
        }

        return RowCanonicalDecoder.Decode(Encode(row, Org, Guid.CreateVersion7(), 1).Bytes);
    }

    /// <summary>Encodes <paramref name="row"/> under a registry that declares the envelope version with the value's own type (or nullable for NULL): a non-conforming encoder.</summary>
    private static RowCanonicalDocument Loose(Row row)
    {
        var values = new Dictionary<string, RowValue?>(row.Values, StringComparer.Ordinal) { ["content_change_operation_id"] = RowValue.Uuid(Guid.CreateVersion7()) };
        var schema = new RowSchemaDefinition(row.Schema.EntityKind, row.Schema.SchemaVersion,
            row.Schema.Columns.Where(c => values.ContainsKey(c.ColumnName)).Select(c => values[c.ColumnName] is { } v ? new RowSignedColumn(c.ColumnName, v.Type, c.Nullable) : new RowSignedColumn(c.ColumnName, c.Type, true)).ToList());
        values = values.Where(kv => schema.Columns.Any(c => c.ColumnName == kv.Key)).ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
        return RowCanonicalDecoder.Decode(RowCanonicalizerV1.Encode(schema, new RowSigningContext(1, Org, row.IdentityPrimaryKey ? row.ItemId : Guid.CreateVersion7(), 1), values));
    }

    public static TheoryData<string, int, int, bool> Pairs()
    {
        var data = new TheoryData<string, int, int, bool>();
        foreach (var kind in new[] { "field", "document" })
        {
            data.Add(kind, 1, 1, true);
            data.Add(kind, 2, 2, true);   // tied; still refused by EnvelopeRules (Envelope v2 not authorized)
            data.Add(kind, 2, 1, false);  // R-M2b2-11
            data.Add(kind, 1, 2, false);
            data.Add(kind, 1, 0, false);
            data.Add(kind, 1, 3, false);
            data.Add(kind, 2, 3, false);
            data.Add(kind, 2, 0, false);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Pairs))]
    public void OnlyTheTwoTiedPairsPassForBothEnvelopeKinds(string kind, int schemaVersion, int envelopeVersion, bool tied)
    {
        var document = Pair(kind, schemaVersion, envelopeVersion);
        Assert.Equal(tied ? null : EnvelopeSchemaTie.MismatchReason, EnvelopeSchemaTie.Violation(document));
        if (tied)
        {
            EnvelopeSchemaTie.Enforce([document]);
        }
        else
        {
            var ex = Assert.Throws<AuthorityException>(() => EnvelopeSchemaTie.Enforce([Pair(kind, 1, 1), document]));
            Assert.Equal(AuthorityErrorCodes.InvalidCommand, ex.ErrorCode);
            Assert.Equal(422, ex.HttpStatusCode);
            Assert.Equal("ENVELOPE_INVALID: ENVELOPE_SCHEMA_MISMATCH", ex.Message);
        }
    }

    [Fact]
    public void TheTieAndTheEnvelopeRulesAreIndependentBarriers()
    {
        foreach (var kind in new[] { "field", "document" })
        {
            // Schema 1 / envelope 1 passes both; schema 2 / envelope 2 passes the tie only and stays refused by the E3-M2a rule.
            Assert.Null(EnvelopeSchemaTie.Violation(Pair(kind, 1, 1)));
            Assert.Null(EnvelopeRules.Validate(Pair(kind, 1, 1)));
            Assert.Equal(EnvelopeRules.Reasons.SchemaUnsupported, EnvelopeRules.Validate(Pair(kind, 2, 2)));

            // Each mismatched pair is refused by both, by different rules: either alone suffices.
            Assert.Equal(EnvelopeSchemaTie.MismatchReason, EnvelopeSchemaTie.Violation(Pair(kind, 2, 1)));
            Assert.Equal(EnvelopeRules.Reasons.SchemaUnsupported, EnvelopeRules.Validate(Pair(kind, 2, 1)));
            Assert.Equal(EnvelopeSchemaTie.MismatchReason, EnvelopeSchemaTie.Violation(Pair(kind, 1, 2)));
            Assert.Equal(EnvelopeRules.Reasons.VersionUnsupported, EnvelopeRules.Validate(Pair(kind, 1, 2)));
        }

        Assert.Equal(new HashSet<(int, int)> { (1, 1), (2, 2) }, EnvelopeSchemaTie.TiedPairs);
        Assert.Equal(new HashSet<int> { 1 }, EnvelopeRules.AllowedEnvelopeVersions); // the v2 prohibition is unchanged
    }

    [Theory]
    [InlineData("field", "NULL")]
    [InlineData("field", "text")]
    [InlineData("field", "int64")]
    [InlineData("field", "missing")]
    [InlineData("document", "NULL")]
    [InlineData("document", "text")]
    [InlineData("document", "missing")]
    public void AnEnvelopeVersionTheTieCannotReadFailsClosed(string kind, string variant)
    {
        var row = Base(kind);
        row = variant switch
        {
            "NULL" => row.With("encryption_envelope_version", null),
            "text" => row.With("encryption_envelope_version", RowValue.Text("1")),
            "int64" => row.With("encryption_envelope_version", RowValue.Int64(1)),
            "missing" => row with { Values = row.Values.Where(kv => kv.Key != "encryption_envelope_version").ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal) },
            _ => throw new ArgumentOutOfRangeException(nameof(variant)),
        };

        Assert.Equal(EnvelopeSchemaTie.MismatchReason, EnvelopeSchemaTie.Violation(Loose(row)));
    }

    [Fact]
    public void RowsWithoutAnEnvelopeAreNotJudged()
    {
        Assert.Null(EnvelopeSchemaTie.Violation(RowCanonicalDecoder.Decode(Encode(Text(Org, Record, Guid.CreateVersion7(), Guid.CreateVersion7(), "plain"), Org, Guid.CreateVersion7(), 1).Bytes)));
        Assert.Null(EnvelopeSchemaTie.Violation(RowCanonicalDecoder.Decode(Encode(Header(Org, Record), Org, Guid.CreateVersion7(), 1).Bytes)));
    }
}
