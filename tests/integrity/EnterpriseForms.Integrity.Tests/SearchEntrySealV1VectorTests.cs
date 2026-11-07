using System.Globalization;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EnterpriseForms.Integrity.Search;
using Xunit;

namespace EnterpriseForms.Integrity.Tests;

/// <summary>
/// IX-1 (C-126): the C# seal encoder must reproduce trust_authority/search-entry-seal-v1.test-vectors.json byte for byte;
/// ES256 verification must accept exactly the positive signature vectors and refuse the negative ones; the pin import must
/// refuse keys that are not P-256 public keys. The vector key is a synthetic test key.
/// </summary>
public sealed class SearchEntrySealV1VectorTests
{
    private static readonly JsonDocument Vectors = JsonDocument.Parse(File.ReadAllText(RepositoryPaths.TrustAuthority("search-entry-seal-v1.test-vectors.json")));

    public static TheoryData<string> MessageVectors() => Names("message_vectors");

    public static TheoryData<string> SignatureVectors() => Names("signature_vectors");

    [Theory]
    [MemberData(nameof(MessageVectors))]
    public void EncoderReproducesTheMessageVector(string name)
    {
        var vector = Find("message_vectors", name);
        var bytes = SearchEntrySealV1.Encode(Input(vector.GetProperty("input")));
        Assert.Equal(vector.GetProperty("message_hex").GetString(), Convert.ToHexStringLower(bytes));
        Assert.Equal(vector.GetProperty("message_sha256").GetString(), Convert.ToHexStringLower(SHA256.HashData(bytes)));
    }

    [Fact]
    public void EncodingDoesNotDependOnInputOrder()
    {
        Assert.Equal(Find("message_vectors", "v1-string-integer-multiselect-anomaly").GetProperty("message_hex").GetString(), Find("message_vectors", "v3-v1-reversed-input-order").GetProperty("message_hex").GetString());
    }

    [Theory]
    [MemberData(nameof(SignatureVectors))]
    public void VerificationMatchesTheSignatureVector(string name)
    {
        var vector = Find("signature_vectors", name);
        var keyNode = vector.GetProperty("key").GetString() == "other" ? "other_key" : "test_key";
        using var key = SearchSealSignatureV1.ImportPinnedPublicKey(Convert.FromBase64String(Vectors.RootElement.GetProperty(keyNode).GetProperty("spki_base64").GetString()!));
        var valid = SearchSealSignatureV1.Verify(key, Convert.FromHexString(vector.GetProperty("message_hex").GetString()!), Convert.FromHexString(vector.GetProperty("signature_hex").GetString()!));
        Assert.Equal(vector.GetProperty("expected_valid").GetBoolean(), valid);
    }

    [Fact]
    public void TheVectorKeyThumbprintIsTheRecordedPin()
    {
        var spki = Convert.FromBase64String(Vectors.RootElement.GetProperty("test_key").GetProperty("spki_base64").GetString()!);
        Assert.Equal(Vectors.RootElement.GetProperty("test_key").GetProperty("spki_sha256").GetString(), SearchSealSignatureV1.Thumbprint(spki));
    }

    [Fact]
    public void EncodingFailuresHaveTheirRecordedCodes()
    {
        var v1 = Input(Find("message_vectors", "v1-string-integer-multiselect-anomaly").GetProperty("input"));
        var v2 = Input(Find("message_vectors", "v2-decimal-date-datetime-boolean").GetProperty("input"));
        var mutations = new Dictionary<string, Func<SearchEntrySealInput>>(StringComparer.Ordinal)
        {
            ["duplicate-posting"] = () => v1 with { Postings = [.. v1.Postings, v1.Postings[0]] },
            ["posting-without-coverage"] = () => v1 with { Coverages = v1.Coverages.Where(c => c.FieldDefinitionId != v1.Postings[0].FieldDefinitionId).ToList() },
            ["short-manifest-root"] = () => v1 with { CommittedManifestRoot = new byte[31] },
            ["key-version-zero"] = () => v1 with { KeyVersion = 0 },
            ["content-revision-zero"] = () => v1 with { CommittedContentRevision = 0 },
            ["duplicate-anomaly"] = () => v1 with { Anomalies = [.. v1.Anomalies, v1.Anomalies[0]] },
            ["sub-microsecond-timestamp"] = () => v2 with { Postings = [v2.Postings[0] with { Value = new SearchSealValue.Timestamp(DateTimeOffset.UnixEpoch.AddTicks(1)) }] },
        };
        foreach (var failure in Vectors.RootElement.GetProperty("encoding_failures").EnumerateArray())
        {
            var name = failure.GetProperty("name").GetString()!;
            var ex = Assert.Throws<IntegrityFormatException>(() => SearchEntrySealV1.Encode(mutations[name]()));
            Assert.Equal(failure.GetProperty("expected_error").GetString(), ex.Code.ToString());
        }

        Assert.Equal(mutations.Count, Vectors.RootElement.GetProperty("encoding_failures").GetArrayLength());
    }

    [Fact]
    public void PinImportRefusesKeysThatAreNotP256PublicKeys()
    {
        using var p384 = ECDsa.Create(ECCurve.NamedCurves.nistP384);
        Assert.ThrowsAny<CryptographicException>(() => SearchSealSignatureV1.ImportPinnedPublicKey(p384.ExportSubjectPublicKeyInfo()).Dispose());
        using var rsa = RSA.Create(2048);
        Assert.ThrowsAny<CryptographicException>(() => SearchSealSignatureV1.ImportPinnedPublicKey(rsa.ExportSubjectPublicKeyInfo()).Dispose());
        var spki = Convert.FromBase64String(Vectors.RootElement.GetProperty("test_key").GetProperty("spki_base64").GetString()!);
        Assert.ThrowsAny<CryptographicException>(() => SearchSealSignatureV1.ImportPinnedPublicKey([.. spki, 0x00]).Dispose());
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        Assert.ThrowsAny<CryptographicException>(() => SearchSealSignatureV1.ImportPinnedPublicKey(key.ExportPkcs8PrivateKey()).Dispose());
    }

    [Theory]
    [InlineData("Case Ref", "case ref")]
    [InlineData("  Café ", "CAFÉ")]
    [InlineData("Café", "Café")]
    [InlineData("straße", "STRASSE")]
    [InlineData("ı", "I")]
    [InlineData("İstanbul", "istanbul")]
    [InlineData("ΣΊΣΥΦΟΣ", "σίσυφος")]
    [InlineData("ǅ", "ǆ")]
    [InlineData("ﬁle", "FILE")]
    [InlineData("a b", "A B")]
    public void NormalizationEqualityMatchesTheC7EvaluatorEquality(string left, string right)
    {
        // The C7 evaluator: NFC + Trim, then OrdinalIgnoreCase (FieldSearchEvaluator). The index: NormalizeString, then ordinal.
        static string Evaluator(string s) => s.Normalize(NormalizationForm.FormC).Trim();
        var evaluatorEqual = string.Equals(Evaluator(left), Evaluator(right), StringComparison.OrdinalIgnoreCase);
        var indexEqual = string.Equals(SearchValueNormalizationV1.NormalizeString(left), SearchValueNormalizationV1.NormalizeString(right), StringComparison.Ordinal);
        Assert.Equal(evaluatorEqual, indexEqual);
        var evaluatorStarts = Evaluator(left).StartsWith(Evaluator(right), StringComparison.OrdinalIgnoreCase);
        var indexStarts = SearchValueNormalizationV1.NormalizeString(left).StartsWith(SearchValueNormalizationV1.NormalizeString(right), StringComparison.Ordinal);
        Assert.Equal(evaluatorStarts, indexStarts);
        var evaluatorContains = Evaluator(left).Contains(Evaluator(right), StringComparison.OrdinalIgnoreCase);
        var indexContains = SearchValueNormalizationV1.NormalizeString(left).Contains(SearchValueNormalizationV1.NormalizeString(right), StringComparison.Ordinal);
        Assert.Equal(evaluatorContains, indexContains);
    }

    private static TheoryData<string> Names(string list)
    {
        var data = new TheoryData<string>();
        foreach (var v in Vectors.RootElement.GetProperty(list).EnumerateArray())
        {
            data.Add(v.GetProperty("name").GetString()!);
        }

        return data;
    }

    private static JsonElement Find(string list, string name) => Vectors.RootElement.GetProperty(list).EnumerateArray().Single(v => v.GetProperty("name").GetString() == name);

    private static SearchEntrySealInput Input(JsonElement i) => new(
        i.GetProperty("key_name").GetString()!,
        i.GetProperty("key_version").GetInt32(),
        Guid.Parse(i.GetProperty("organization_id").GetString()!),
        Guid.Parse(i.GetProperty("form_definition_id").GetString()!),
        Guid.Parse(i.GetProperty("form_record_id").GetString()!),
        Guid.Parse(i.GetProperty("committed_operation_id").GetString()!),
        long.Parse(i.GetProperty("committed_content_revision").GetString()!, CultureInfo.InvariantCulture),
        Convert.FromHexString(i.GetProperty("committed_manifest_root_hex").GetString()!),
        i.GetProperty("record_status_name").GetString()!,
        i.GetProperty("coverages").EnumerateArray().Select(c => new SearchSealCoverage(Guid.Parse(c.GetProperty("field_definition_id").GetString()!), Guid.Parse(c.GetProperty("field_search_approval_id").GetString()!),
            c.GetProperty("value_type").GetString()!, c.GetProperty("classification").GetString()!, c.GetProperty("protection_mode").GetString()!, c.GetProperty("operators").EnumerateArray().Select(o => o.GetString()!).ToList())).ToList(),
        i.GetProperty("postings").EnumerateArray().Select(p => new SearchSealPosting(Guid.Parse(p.GetProperty("field_definition_id").GetString()!), Guid.Parse(p.GetProperty("field_search_approval_id").GetString()!),
            Convert.FromHexString(p.GetProperty("manifest_item_key_hex").GetString()!), Guid.Parse(p.GetProperty("manifest_item_id").GetString()!), p.GetProperty("entity_kind").GetString()!, p.GetProperty("value_type").GetString()!,
            Convert.FromHexString(p.GetProperty("source_digest_hex").GetString()!), Value(p.GetProperty("value")))).ToList(),
        i.GetProperty("anomalies").EnumerateArray().Select(a => new SearchSealAnomaly(Guid.Parse(a.GetProperty("field_definition_id").GetString()!), a.GetProperty("anomaly_kind").GetString()!)).ToList());

    private static SearchSealValue Value(JsonElement v) => v.GetProperty("tag").GetString() switch
    {
        "Text" => new SearchSealValue.NormalizedString(v.GetProperty("text").GetString()!),
        "Int64" => new SearchSealValue.IntegerValue(long.Parse(v.GetProperty("integer").GetString()!, CultureInfo.InvariantCulture)),
        "Decimal" => new SearchSealValue.DecimalParts(v.GetProperty("scale").GetInt32(), BigInteger.Parse(v.GetProperty("coefficient").GetString()!, CultureInfo.InvariantCulture)),
        "Date" => new SearchSealValue.DateValue(DateOnly.ParseExact(v.GetProperty("date").GetString()!, "yyyy-MM-dd", CultureInfo.InvariantCulture)),
        "TimestampUtc" => new SearchSealValue.Timestamp(DateTimeOffset.UnixEpoch.AddTicks(long.Parse(v.GetProperty("timestamp_micros").GetString()!, CultureInfo.InvariantCulture) * 10)),
        "Boolean" => new SearchSealValue.BooleanValue(v.GetProperty("boolean").GetBoolean()),
        "Uuid" => new SearchSealValue.OptionValue(Guid.Parse(v.GetProperty("uuid").GetString()!)),
        var tag => throw new InvalidOperationException("unknown tag " + tag),
    };
}
