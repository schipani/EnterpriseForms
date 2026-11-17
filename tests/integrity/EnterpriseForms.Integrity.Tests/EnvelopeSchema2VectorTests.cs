using System.Globalization;
using System.Text.Json;
using EnterpriseForms.Integrity.Canonical;
using Xunit;

namespace EnterpriseForms.Integrity.Tests;

/// <summary>
/// E3-M2b-2 (M2-SIG-01, M2-SIG-12): the C# canonicalizer reproduces trust_authority/row-integrity-v1-envelope-schema2.test-vectors.json
/// byte for byte. RowHmac v1 framing is unchanged; only the registered schema of the two envelope tables gains version 2. The
/// frozen v1 vector files are checked separately (RowIntegrityV1VectorTests, unchanged).
/// </summary>
public sealed class EnvelopeSchema2VectorTests
{
    private static readonly JsonDocument Vectors = JsonDocument.Parse(File.ReadAllText(RepositoryPaths.TrustAuthority("row-integrity-v1-envelope-schema2.test-vectors.json")));
    private static readonly byte[] TestKey = Convert.FromHexString(Vectors.RootElement.GetProperty("testHmacKeyHex").GetString()!);

    public static TheoryData<string> CaseIds() => Ids("cases");

    public static TheoryData<string> NegativeCaseIds() => Ids("negativeCases");

    [Theory]
    [MemberData(nameof(CaseIds))]
    public void PositiveCaseReproducesExactly(string caseId)
    {
        var c = Find("cases", caseId);
        var schema = LoadSchema(c.GetProperty("entityKind").GetString()!, c.GetProperty("schemaVersion").GetInt32());
        var canonical = RowCanonicalizerV1.Encode(schema, Context(c), ParseValues(schema, c.GetProperty("values")));
        var expected = c.GetProperty("expected");
        Assert.Equal(expected.GetProperty("canonicalBytesHex").GetString(), Convert.ToHexStringLower(canonical));
        Assert.Equal(expected.GetProperty("canonicalByteLength").GetInt32(), canonical.Length);
        Assert.Equal(expected.GetProperty("canonicalPayloadDigestHex").GetString(), Convert.ToHexStringLower(RowCanonicalizerV1.CanonicalPayloadDigest(canonical)));
        Assert.Equal(expected.GetProperty("rowSignatureHexWithTestKey").GetString(), Convert.ToHexStringLower(RowIntegrityHmac.ComputeTag(TestKey, canonical)));
        Assert.Equal(c.GetProperty("schemaVersion").GetInt32(), RowCanonicalHeader.Parse(canonical).SchemaVersion);
    }

    [Theory]
    [MemberData(nameof(NegativeCaseIds))]
    public void NegativeCaseIsRejected(string caseId)
    {
        var n = Find("negativeCases", caseId);
        var b = Find("cases", n.GetProperty("baseCaseId").GetString()!);
        var schema = LoadSchema(b.GetProperty("entityKind").GetString()!, b.GetProperty("schemaVersion").GetInt32());
        var values = ParseValues(schema, b.GetProperty("values"));
        if (n.TryGetProperty("set", out var set))
        {
            foreach (var property in set.EnumerateObject())
            {
                var column = schema.Columns.FirstOrDefault(c => c.ColumnName == property.Name);
                values[property.Name] = property.Value.ValueKind == JsonValueKind.Null ? null
                    : column is null ? RowValue.Uuid(Guid.Parse(property.Value.GetString()!)) // a column absent from the registry (UnregisteredColumn)
                    : property.Value.ValueKind == JsonValueKind.String && column.Type == RowColumnType.Int32 ? RowValue.Text(property.Value.GetString()!) // wrong type (TypeMismatch)
                    : Parse(column, property.Value);
            }
        }

        if (n.TryGetProperty("remove", out var remove))
        {
            foreach (var name in remove.EnumerateArray())
            {
                values.Remove(name.GetString()!);
            }
        }

        var ex = Assert.Throws<IntegrityFormatException>(() => RowCanonicalizerV1.Encode(schema, Context(b), values));
        Assert.Equal(Enum.Parse<IntegrityFormatErrorCode>(n.GetProperty("expectedError").GetString()!), ex.Code);
    }

    [Fact]
    public void SchemaTwoIsSchemaOnePlusThreeNonNullableColumnsAndTheBytesDiffer()
    {
        foreach (var kind in new[] { "field_encrypted_values", "file_attachment_versions" })
        {
            var v1 = LoadSchema(kind, 1).Columns;
            var v2 = LoadSchema(kind, 2).Columns;
            var added = v2.Where(c => v1.All(o => o.ColumnName != c.ColumnName)).ToList();
            Assert.Equal(["data_key_commitment_bytes", "data_key_identifier", "key_wrap_algorithm_code"], added.Select(c => c.ColumnName));
            Assert.All(added, c => Assert.False(c.Nullable));
            Assert.All(v1, c => Assert.Contains(v2, o => o == c));
        }

        Assert.NotEqual(
            Find("cases", "ENVS2-FIELD-S1-REFERENCE").GetProperty("expected").GetProperty("canonicalBytesHex").GetString(),
            Find("cases", "ENVS2-FIELD-S2-LOCAL-WRAP").GetProperty("expected").GetProperty("canonicalBytesHex").GetString());
    }

    private static TheoryData<string> Ids(string section)
    {
        var data = new TheoryData<string>();
        foreach (var c in Vectors.RootElement.GetProperty(section).EnumerateArray())
        {
            data.Add(c.GetProperty("caseId").GetString()!);
        }

        return data;
    }

    private static JsonElement Find(string section, string caseId) =>
        Vectors.RootElement.GetProperty(section).EnumerateArray().First(c => c.GetProperty("caseId").GetString() == caseId);

    private static RowSigningContext Context(JsonElement c) => new(
        c.GetProperty("keyVersion").GetUInt32(), Guid.Parse(c.GetProperty("organizationId").GetString()!), Guid.Parse(c.GetProperty("primaryKeyId").GetString()!), c.GetProperty("rowRevision").GetUInt64());

    private static RowSchemaDefinition LoadSchema(string entityKind, int version)
    {
        var s = Vectors.RootElement.GetProperty("schemas").EnumerateArray()
            .First(x => x.GetProperty("entityKind").GetString() == entityKind && x.GetProperty("schemaVersion").GetInt32() == version);
        return new RowSchemaDefinition(entityKind, version, s.GetProperty("signedColumns").EnumerateArray()
            .Select(c => new RowSignedColumn(c.GetProperty("columnName").GetString()!, Enum.Parse<RowColumnType>(c.GetProperty("type").GetString()!), c.GetProperty("nullable").GetBoolean()))
            .ToList());
    }

    private static Dictionary<string, RowValue?> ParseValues(RowSchemaDefinition schema, JsonElement values)
    {
        var result = new Dictionary<string, RowValue?>(StringComparer.Ordinal);
        foreach (var column in schema.Columns)
        {
            var element = values.GetProperty(column.ColumnName);
            result[column.ColumnName] = element.ValueKind == JsonValueKind.Null ? null : Parse(column, element);
        }

        return result;
    }

    private static RowValue Parse(RowSignedColumn column, JsonElement element) => column.Type switch
    {
        RowColumnType.Int32 => RowValue.Int32(element.GetInt32()),
        RowColumnType.Int64 => RowValue.Int64(element.GetInt64()),
        RowColumnType.TimestampUtc => RowValue.Timestamp(DateTimeOffset.Parse(element.GetString()!, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind)),
        RowColumnType.Uuid => RowValue.Uuid(Guid.Parse(element.GetString()!)),
        RowColumnType.Text => RowValue.Text(element.GetString()!),
        RowColumnType.Bytes => RowValue.Bytes(Convert.FromHexString(element.GetString()!)),
        _ => throw new InvalidOperationException(column.Type.ToString()),
    };
}
