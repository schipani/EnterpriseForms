using System.Text.Json;
using EnterpriseForms.Integrity.Merkle;
using Xunit;

namespace EnterpriseForms.Integrity.Tests;

/// <summary>C# implementation must reproduce trust_authority/merkle-v1.test-vectors.json byte for byte (MRK-23).</summary>
public sealed class MerkleV1VectorTests
{
    private static readonly JsonDocument Vectors = JsonDocument.Parse(File.ReadAllText(RepositoryPaths.TrustAuthority("merkle-v1.test-vectors.json")));

    public static TheoryData<string> CaseIds()
    {
        var data = new TheoryData<string>();
        foreach (var c in Vectors.RootElement.GetProperty("cases").EnumerateArray())
        {
            data.Add(c.GetProperty("caseId").GetString()!);
        }

        return data;
    }

    [Fact]
    public void EmptyRootMatchesVector()
    {
        Assert.Equal(Vectors.RootElement.GetProperty("emptyRootHex").GetString(), Convert.ToHexStringLower(MerkleManifestV1.EmptyRoot()));
    }

    [Theory]
    [MemberData(nameof(CaseIds))]
    public void PositiveCaseReproducesExactly(string caseId)
    {
        var c = FindCase(caseId);
        var inputs = c.GetProperty("inputLeavesInSubmissionOrder").EnumerateArray().Select(ParseLeaf).ToList();
        var expected = c.GetProperty("expected");

        var result = MerkleManifestV1.Build(inputs);

        Assert.Equal(Hexes(expected, "sortedItemKeysHex"), result.SortedLeaves.Select(l => Convert.ToHexStringLower(l.ItemKey)));
        Assert.Equal(Hexes(expected, "leafBytesHex"), result.SortedLeaves.Select(l => Convert.ToHexStringLower(MerkleManifestV1.LeafBytes(l))));
        Assert.Equal(Hexes(expected, "leafHashesHex"), result.LeafHashes.Select(Convert.ToHexStringLower));
        var expectedLevels = expected.GetProperty("levelsBottomUpHex").EnumerateArray().Select(l => l.EnumerateArray().Select(h => h.GetString()!).ToList()).ToList();
        Assert.Equal(expectedLevels, result.LevelsBottomUp.Select(l => l.Select(Convert.ToHexStringLower).ToList()).ToList());
        Assert.Equal(expected.GetProperty("merkleRootHex").GetString(), Convert.ToHexStringLower(result.MerkleRoot));
        Assert.Equal(expected.GetProperty("leafCount").GetUInt64(), result.LeafCount);
        Assert.Equal(expected.GetProperty("leafIndexDigestHex").GetString(), Convert.ToHexStringLower(result.LeafIndexDigest));
    }

    [Fact]
    public void FixturePayloadDigestsAndKeysMatch()
    {
        foreach (var c in Vectors.RootElement.GetProperty("cases").EnumerateArray())
        {
            foreach (var d in c.GetProperty("inputLeavesInSubmissionOrder").EnumerateArray())
            {
                var payload = System.Text.Encoding.ASCII.GetBytes(d.GetProperty("fixturePayloadAscii").GetString()!);
                Assert.Equal(d.GetProperty("canonicalPayloadDigestHex").GetString(), Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(payload)));
                Assert.Equal(d.GetProperty("itemKeyHex").GetString(), Convert.ToHexStringLower(MerkleManifestV1.ItemKey((MerkleItemKind)d.GetProperty("itemKindCode").GetByte(), Guid.Parse(d.GetProperty("itemId").GetString()!))));
            }
        }
    }

    [Fact]
    public void UnsortedInputProducesIdenticalManifest()
    {
        var normative = FindCase("MRKV1-3LEAF-NORMATIVE").GetProperty("expected").GetRawText();
        var unsorted = FindCase("MRKV1-3LEAF-UNSORTED-INPUT").GetProperty("expected").GetRawText();
        Assert.Equal(normative, unsorted);
    }

    [Fact]
    public void DuplicateKeyIsRejected()
    {
        var leaf = ParseLeaf(FindCase("MRKV1-3LEAF-NORMATIVE").GetProperty("inputLeavesInSubmissionOrder")[1]);
        var ex = Assert.Throws<IntegrityFormatException>(() => MerkleManifestV1.Build([leaf, leaf]));
        Assert.Equal(IntegrityFormatErrorCode.DuplicateItemKey, ex.Code);
    }

    [Fact]
    public void ShortSignatureIsRejected()
    {
        var leaf = ParseLeaf(FindCase("MRKV1-3LEAF-NORMATIVE").GetProperty("inputLeavesInSubmissionOrder")[1]);
        var bad = leaf with { RowSignature = new byte[31] };
        var ex = Assert.Throws<IntegrityFormatException>(() => MerkleManifestV1.LeafBytes(bad));
        Assert.Equal(IntegrityFormatErrorCode.InvalidSignatureLength, ex.Code);
    }

    [Fact]
    public void MixedRevisionIsRejected()
    {
        var leaves = FindCase("MRKV1-3LEAF-NORMATIVE").GetProperty("inputLeavesInSubmissionOrder").EnumerateArray().Select(ParseLeaf).ToList();
        leaves[1] = leaves[1] with { CommittedContentRevision = leaves[1].CommittedContentRevision + 1 };
        var ex = Assert.Throws<IntegrityFormatException>(() => MerkleManifestV1.Build(leaves));
        Assert.Equal(IntegrityFormatErrorCode.MixedRevisionOrOperation, ex.Code);
    }

    [Fact]
    public void UnknownKindCodeIsRejected()
    {
        var ex = Assert.Throws<IntegrityFormatException>(() => MerkleManifestV1.ItemKey((MerkleItemKind)7, Guid.NewGuid()));
        Assert.Equal(IntegrityFormatErrorCode.UnknownItemKind, ex.Code);
    }

    [Fact]
    public void TamperedLeafChangesRoot()
    {
        var leaves = FindCase("MRKV1-3LEAF-NORMATIVE").GetProperty("inputLeavesInSubmissionOrder").EnumerateArray().Select(ParseLeaf).ToList();
        var original = MerkleManifestV1.Build(leaves).MerkleRoot;
        var digest = leaves[1].CanonicalPayloadDigest.ToArray();
        digest[0] ^= 0x01;
        leaves[1] = leaves[1] with { CanonicalPayloadDigest = digest };
        Assert.False(original.AsSpan().SequenceEqual(MerkleManifestV1.Build(leaves).MerkleRoot));
    }

    private static JsonElement FindCase(string caseId) =>
        Vectors.RootElement.GetProperty("cases").EnumerateArray().First(c => c.GetProperty("caseId").GetString() == caseId);

    private static List<string> Hexes(JsonElement parent, string property) =>
        parent.GetProperty(property).EnumerateArray().Select(e => e.GetString()!).ToList();

    private static MerkleLeafInput ParseLeaf(JsonElement d) => new(
        (MerkleItemKind)d.GetProperty("itemKindCode").GetByte(),
        Guid.Parse(d.GetProperty("itemId").GetString()!),
        Convert.FromHexString(d.GetProperty("rowSignatureHex").GetString()!),
        Convert.FromHexString(d.GetProperty("canonicalPayloadDigestHex").GetString()!),
        d.GetProperty("committedContentRevision").GetUInt64(),
        Guid.Parse(d.GetProperty("committedOperationId").GetString()!));
}
