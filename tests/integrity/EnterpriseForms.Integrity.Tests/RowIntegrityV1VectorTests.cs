using System.Globalization;
using System.Text.Json;
using EnterpriseForms.Integrity.Canonical;
using Xunit;

namespace EnterpriseForms.Integrity.Tests;

/// <summary>C# canonicalizer must reproduce trust_authority/row-integrity-v1.test-vectors.json byte for byte (SEC-38, acceptance test 72).</summary>
public sealed class RowIntegrityV1VectorTests
{
    private static readonly JsonDocument Vectors = JsonDocument.Parse(File.ReadAllText(RepositoryPaths.TrustAuthority("row-integrity-v1.test-vectors.json")));
    private static readonly byte[] TestKey = Convert.FromHexString(Vectors.RootElement.GetProperty("testHmacKeyHex").GetString()!);

    public static TheoryData<string> CaseIds()
    {
        var data = new TheoryData<string>();
        foreach (var c in Vectors.RootElement.GetProperty("cases").EnumerateArray())
        {
            data.Add(c.GetProperty("caseId").GetString()!);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(CaseIds))]
    public void PositiveCaseReproducesExactly(string caseId)
    {
        var c = FindCase(caseId);
        var schema = LoadSchema(c.GetProperty("entityKind").GetString()!, c.GetProperty("schemaVersion").GetInt32());
        var context = new RowSigningContext(
            c.GetProperty("keyVersion").GetUInt32(),
            Guid.Parse(c.GetProperty("organizationId").GetString()!),
            Guid.Parse(c.GetProperty("primaryKeyId").GetString()!),
            c.GetProperty("rowRevision").GetUInt64());
        var values = ParseValues(schema, c.GetProperty("values"));
        var expected = c.GetProperty("expected");

        var canonical = RowCanonicalizerV1.Encode(schema, context, values);

        Assert.Equal(expected.GetProperty("canonicalBytesHex").GetString(), Convert.ToHexStringLower(canonical));
        Assert.Equal(expected.GetProperty("canonicalByteLength").GetInt32(), canonical.Length);
        Assert.Equal(expected.GetProperty("canonicalPayloadDigestHex").GetString(), Convert.ToHexStringLower(RowCanonicalizerV1.CanonicalPayloadDigest(canonical)));
        var tag = RowIntegrityHmac.ComputeTag(TestKey, canonical);
        Assert.Equal(expected.GetProperty("rowSignatureHexWithTestKey").GetString(), Convert.ToHexStringLower(tag));
        Assert.True(RowIntegrityHmac.VerifyTag(TestKey, canonical, tag));
    }

    [Fact]
    public void DecimalExamplesMatch()
    {
        foreach (var ex in Vectors.RootElement.GetProperty("decimalEncodingExamples").EnumerateArray())
        {
            var value = (RowValue.DecimalValue)RowValue.Decimal(ex.GetProperty("input").GetString()!);
            var schema = new RowSchemaDefinition("decimal_probe", 1, [new RowSignedColumn("probe_value", RowColumnType.Decimal, false)]);
            var canonical = RowCanonicalizerV1.Encode(schema, new RowSigningContext(1, Guid.Empty, Guid.Empty, 1), new Dictionary<string, RowValue?> { ["probe_value"] = value });
            // Column encoding is the trailing bytes after the 0x04 tag: Int32 scale, UInt16 length, coefficient.
            var expectedHex = ex.GetProperty("encodedHex").GetString()!;
            Assert.EndsWith(expectedHex, Convert.ToHexStringLower(canonical), StringComparison.Ordinal);
        }
    }

    [Fact]
    public void DecimalFromSystemDecimalMatchesLiteral()
    {
        Assert.Equal(RowValue.Decimal("1234.56"), RowValue.Decimal(1234.5600m));
        Assert.Equal(RowValue.Decimal("-0.0001"), RowValue.Decimal(-0.000100m));
        Assert.Equal(RowValue.Decimal("0"), RowValue.Decimal(0.000m));
    }

    [Fact]
    public void NfcEquivalenceHolds()
    {
        Assert.Equal(
            FindCase("ROWV1-TYPES-ALL-PRESENT").GetProperty("expected").GetProperty("canonicalBytesHex").GetString(),
            FindCase("ROWV1-TYPES-NFC-EQUIVALENCE").GetProperty("expected").GetProperty("canonicalBytesHex").GetString());
    }

    [Fact]
    public void EmptyStringDiffersFromNullAndOwnerChangeAltersBytes()
    {
        Assert.NotEqual(
            FindCase("ROWV1-TYPES-ALL-NULL").GetProperty("expected").GetProperty("canonicalBytesHex").GetString(),
            FindCase("ROWV1-EMPTY-STRING-NOT-NULL").GetProperty("expected").GetProperty("canonicalBytesHex").GetString());
        Assert.NotEqual(
            FindCase("ROWV1-FORM-RECORD-USER-OWNED").GetProperty("expected").GetProperty("canonicalBytesHex").GetString(),
            FindCase("ROWV1-FORM-RECORD-TEAM-OWNED").GetProperty("expected").GetProperty("canonicalBytesHex").GetString());
    }

    [Fact]
    public void TamperedTagFailsVerification()
    {
        var c = FindCase("ROWV1-FORM-RECORD-USER-OWNED");
        var canonical = Convert.FromHexString(c.GetProperty("expected").GetProperty("canonicalBytesHex").GetString()!);
        var tag = Convert.FromHexString(c.GetProperty("expected").GetProperty("rowSignatureHexWithTestKey").GetString()!);
        tag[5] ^= 0x80;
        Assert.False(RowIntegrityHmac.VerifyTag(TestKey, canonical, tag));
        Assert.False(RowIntegrityHmac.VerifyTag(TestKey, canonical, tag.AsSpan(0, 31)));
        var otherKey = new byte[32];
        otherKey[0] = 1;
        Assert.False(RowIntegrityHmac.VerifyTag(otherKey, canonical, Convert.FromHexString(c.GetProperty("expected").GetProperty("rowSignatureHexWithTestKey").GetString()!)));
    }

    [Theory]
    [InlineData("ROWV1-NEG-NULL-IN-NON-NULLABLE", IntegrityFormatErrorCode.NullNotPermitted)]
    [InlineData("ROWV1-NEG-JSON-FLOAT", IntegrityFormatErrorCode.JsonFloatNotPermitted)]
    [InlineData("ROWV1-NEG-JSON-NON-ASCII-KEY", IntegrityFormatErrorCode.JsonKeyNotAscii)]
    [InlineData("ROWV1-NEG-UNSORTED-REGISTRY", IntegrityFormatErrorCode.RegistryNotSorted)]
    [InlineData("ROWV1-NEG-EXTRA-COLUMN", IntegrityFormatErrorCode.UnregisteredColumn)]
    [InlineData("ROWV1-NEG-MISSING-COLUMN", IntegrityFormatErrorCode.MissingColumn)]
    [InlineData("ROWV1-NEG-ROW-REVISION-ZERO", IntegrityFormatErrorCode.RowRevisionRange)]
    public void NegativeCasesAreRejected(string caseId, IntegrityFormatErrorCode expectedCode)
    {
        Assert.Contains(Vectors.RootElement.GetProperty("negativeCases").EnumerateArray(), n => n.GetProperty("caseId").GetString() == caseId);
        var schema = LoadSchema("integrity_type_coverage", 1);
        var baseValues = ParseValues(schema, FindCase("ROWV1-TYPES-ALL-NULL").GetProperty("values"));
        var context = new RowSigningContext(1, Guid.NewGuid(), Guid.NewGuid(), 1);

        Action act = caseId switch
        {
            "ROWV1-NEG-NULL-IN-NON-NULLABLE" => () => RowCanonicalizerV1.Encode(schema, context, With(baseValues, "boolean_flag", null)),
            "ROWV1-NEG-JSON-FLOAT" => () => RowCanonicalizerV1.Encode(schema, context, With(baseValues, "json_value", RowValue.Json(JsonDocument.Parse("{\"a\":1.5}").RootElement))),
            "ROWV1-NEG-JSON-NON-ASCII-KEY" => () => RowCanonicalizerV1.Encode(schema, context, With(baseValues, "json_value", RowValue.Json(JsonDocument.Parse("{\"é\":1}").RootElement))),
            "ROWV1-NEG-UNSORTED-REGISTRY" => () => _ = new RowSchemaDefinition("x", 1, [new RowSignedColumn("b_col", RowColumnType.Text, true), new RowSignedColumn("a_col", RowColumnType.Text, true)]),
            "ROWV1-NEG-EXTRA-COLUMN" => () => RowCanonicalizerV1.Encode(schema, context, With(baseValues, "rogue_column", RowValue.Int32(1))),
            "ROWV1-NEG-MISSING-COLUMN" => () => RowCanonicalizerV1.Encode(schema, context, Without(baseValues, "text_value")),
            "ROWV1-NEG-ROW-REVISION-ZERO" => () => RowCanonicalizerV1.Encode(schema, context with { RowRevision = 0 }, baseValues),
            _ => throw new InvalidOperationException(caseId),
        };

        var ex = Assert.Throws<IntegrityFormatException>(act);
        Assert.Equal(expectedCode, ex.Code);
    }

    [Fact]
    public void SubMicrosecondTimestampIsRejected()
    {
        var schema = new RowSchemaDefinition("probe_rows", 1, [new RowSignedColumn("probe_timestamp", RowColumnType.TimestampUtc, false)]);
        var value = RowValue.Timestamp(DateTimeOffset.UnixEpoch.AddTicks(1));
        var ex = Assert.Throws<IntegrityFormatException>(() => RowCanonicalizerV1.Encode(schema, new RowSigningContext(1, Guid.Empty, Guid.Empty, 1), new Dictionary<string, RowValue?> { ["probe_timestamp"] = value }));
        Assert.Equal(IntegrityFormatErrorCode.TimestampPrecision, ex.Code);
    }

    [Fact]
    public void TypeMismatchIsRejected()
    {
        var schema = new RowSchemaDefinition("probe_rows", 1, [new RowSignedColumn("probe_flag", RowColumnType.Boolean, false)]);
        var ex = Assert.Throws<IntegrityFormatException>(() => RowCanonicalizerV1.Encode(schema, new RowSigningContext(1, Guid.Empty, Guid.Empty, 1), new Dictionary<string, RowValue?> { ["probe_flag"] = RowValue.Int32(1) }));
        Assert.Equal(IntegrityFormatErrorCode.TypeMismatch, ex.Code);
    }

    private static Dictionary<string, RowValue?> With(IReadOnlyDictionary<string, RowValue?> source, string key, RowValue? value)
    {
        var copy = new Dictionary<string, RowValue?>(source, StringComparer.Ordinal) { [key] = value };
        return copy;
    }

    private static Dictionary<string, RowValue?> Without(IReadOnlyDictionary<string, RowValue?> source, string key)
    {
        var copy = new Dictionary<string, RowValue?>(source, StringComparer.Ordinal);
        copy.Remove(key);
        return copy;
    }

    private static JsonElement FindCase(string caseId) =>
        Vectors.RootElement.GetProperty("cases").EnumerateArray().First(c => c.GetProperty("caseId").GetString() == caseId);

    private static RowSchemaDefinition LoadSchema(string entityKind, int version)
    {
        var s = Vectors.RootElement.GetProperty("schemas").EnumerateArray()
            .First(x => x.GetProperty("entityKind").GetString() == entityKind && x.GetProperty("schemaVersion").GetInt32() == version);
        var columns = s.GetProperty("signedColumns").EnumerateArray()
            .Select(c => new RowSignedColumn(c.GetProperty("columnName").GetString()!, Enum.Parse<RowColumnType>(c.GetProperty("type").GetString()!), c.GetProperty("nullable").GetBoolean()))
            .ToList();
        return new RowSchemaDefinition(entityKind, version, columns);
    }

    private static Dictionary<string, RowValue?> ParseValues(RowSchemaDefinition schema, JsonElement values)
    {
        var result = new Dictionary<string, RowValue?>(StringComparer.Ordinal);
        foreach (var column in schema.Columns)
        {
            var element = values.GetProperty(column.ColumnName);
            result[column.ColumnName] = element.ValueKind == JsonValueKind.Null ? null : column.Type switch
            {
                RowColumnType.Boolean => RowValue.Boolean(element.GetBoolean()),
                RowColumnType.Int32 => RowValue.Int32(element.GetInt32()),
                RowColumnType.Int64 => RowValue.Int64(element.GetInt64()),
                RowColumnType.Decimal => RowValue.Decimal(element.GetString()!),
                RowColumnType.Date => RowValue.Date(DateOnly.ParseExact(element.GetString()!, "yyyy-MM-dd", CultureInfo.InvariantCulture)),
                RowColumnType.TimestampUtc => RowValue.Timestamp(DateTimeOffset.Parse(element.GetString()!, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind)),
                RowColumnType.Uuid => RowValue.Uuid(Guid.Parse(element.GetString()!)),
                RowColumnType.Text => RowValue.Text(element.GetString()!),
                RowColumnType.Bytes => RowValue.Bytes(Convert.FromHexString(element.GetString()!)),
                RowColumnType.Json => RowValue.Json(element),
                _ => throw new InvalidOperationException(column.Type.ToString()),
            };
        }

        return result;
    }
}
