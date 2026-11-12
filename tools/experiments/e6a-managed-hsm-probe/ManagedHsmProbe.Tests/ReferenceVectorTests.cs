using System.Security.Cryptography;
using Xunit;

namespace EnterpriseForms.Experiments.ManagedHsmProbe.Tests;

public sealed class ReferenceVectorTests
{
    [Fact]
    public void TheCommittedVectorFileIsExactlyWhatTheGeneratorProducesFromThePublishedRowVectors()
    {
        var source = File.ReadAllText(Path.Combine(TestSetup.RepositoryRoot(), "trust_authority", "row-integrity-v1.test-vectors.json"));
        var regenerated = ReferenceVectorGenerator.Serialize(ReferenceVectorGenerator.Generate(source, "trust_authority/row-integrity-v1.test-vectors.json"));
        var committed = File.ReadAllText(Path.Combine(TestSetup.ProbeRoot(), "vectors", "e6a-reference-vectors.json")).ReplaceLineEndings("\n");
        Assert.Equal(regenerated, committed);
    }

    [Fact]
    public void TheEmbeddedVectorsAreTheCommittedFile()
    {
        var committed = ReferenceVectorGenerator.Deserialize(File.ReadAllText(Path.Combine(TestSetup.ProbeRoot(), "vectors", "e6a-reference-vectors.json")));
        Assert.Equal(ReferenceVectorGenerator.Serialize(committed), ReferenceVectorGenerator.Serialize(ReferenceVectorGenerator.LoadEmbedded()));
    }

    [Fact]
    public void EveryRequiredLengthIsPresentAndRawAndDigestMacsAreComputedSeparatelyAndDiffer()
    {
        var set = ReferenceVectorGenerator.LoadEmbedded();
        Assert.Equal(ReferenceVectorSet.RequiredLengths, set.Vectors.Where(v => v.VectorKind == "Length").Select(v => v.InputLength));
        foreach (var vector in set.Vectors)
        {
            var input = vector.Input();
            Assert.Equal(vector.InputLength, input.Length);
            foreach (var mac in vector.Macs)
            {
                var key = TestKeys.Get(mac.TestKeyId);
                Assert.Equal(Codec.ToHex(Rfc2104HmacSha256.Compute(key, input)), mac.RawMessageHmacSha256Hex);
                Assert.Equal(Codec.ToHex(Rfc2104HmacSha256.Compute(key, SHA256.HashData(input))), mac.DigestMessageHmacSha256Hex);
                Assert.NotEqual(mac.RawMessageHmacSha256Hex, mac.DigestMessageHmacSha256Hex);
                Assert.False(mac.RawEqualsDigest);
            }
        }
    }

    [Fact]
    public void RowVectorsReproduceThePublishedRowHmacV1SignaturesAndTheRecordedVaultTransitOutputs()
    {
        var set = ReferenceVectorGenerator.LoadEmbedded();
        var vault = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(TestSetup.RepositoryRoot(), "docs", "security", "stage-a-vault-transit-vectors.json")));
        var vaultByCase = vault.RootElement.GetProperty("cases").EnumerateArray().ToDictionary(c => c.GetProperty("case_id").GetString()!, c => c.GetProperty("vault_hmac_hex").GetString());
        var rows = set.Vectors.Where(v => v.VectorKind == "RowHmacV1").ToList();
        Assert.Equal(7, rows.Count);
        foreach (var row in rows)
        {
            var raw = row.MacFor(TestKeys.TestKey1Id).RawMessageHmacSha256Hex;
            Assert.Equal(row.PublishedRowSignatureHex, raw);
            Assert.Equal(vaultByCase[row.VectorId["row-".Length..]], raw);
        }
    }

    [Fact]
    public void LocalSelfChecksPassOnTheEmbeddedVectorsAndFailOnACorruptedCopy()
    {
        Assert.Equal(Verdict.Pass, LocalSelfChecks.Run(ReferenceVectorGenerator.LoadEmbedded()).OverallVerdict);

        var corrupted = ReferenceVectorGenerator.LoadEmbedded();
        var mac = corrupted.Vectors.Single(v => v.InputLength == 32 && v.VectorKind == "Length").MacFor(TestKeys.TestKey1Id);
        mac.DigestMessageHmacSha256Hex = mac.RawMessageHmacSha256Hex;
        var report = new ProbeReport();
        LocalSelfChecks.CheckVectorSet(report, corrupted);
        Assert.Equal(Verdict.Fail, report.OverallVerdict);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(32, 33)]
    [InlineData(64, 1000)]
    [InlineData(65, 7)]
    [InlineData(200, 65536)]
    public void TheIndependentRfc2104ConstructionAgreesWithHmacSha256(int keyLength, int messageLength)
    {
        var key = RandomNumberGenerator.GetBytes(keyLength);
        var message = RandomNumberGenerator.GetBytes(messageLength);
        Assert.Equal(HMACSHA256.HashData(key, message), Rfc2104HmacSha256.Compute(key, message));
    }

    [Fact]
    public void TheTwoTestKeysArePublishedDistinctAndTheFirstIsTheRowHmacV1TestKey()
    {
        Assert.NotEqual(TestKeys.KeyHexById[TestKeys.TestKey1Id], TestKeys.KeyHexById[TestKeys.TestKey2Id]);
        var rowFile = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(TestSetup.RepositoryRoot(), "trust_authority", "row-integrity-v1.test-vectors.json")));
        Assert.Equal(rowFile.RootElement.GetProperty("testHmacKeyHex").GetString(), TestKeys.KeyHexById[TestKeys.TestKey1Id]);
    }
}
