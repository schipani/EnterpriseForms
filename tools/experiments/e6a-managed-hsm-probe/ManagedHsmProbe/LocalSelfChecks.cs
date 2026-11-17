using System.Security.Cryptography;
using System.Text;

namespace EnterpriseForms.Experiments.ManagedHsmProbe;

/// <summary>
/// Local-only mode. Verifies the HMAC primitive (RFC 4231), every embedded reference vector (two separately computed MACs
/// per vector and key, recomputed with two implementations), the published RowHmac/v1 signatures and the explicit-version
/// guard. Constructs no credential, HTTP client or transport.
/// </summary>
internal static class LocalSelfChecks
{
    private const string Experiment = "LocalReference";

    public static ProbeReport Run(ReferenceVectorSet vectors)
    {
        ArgumentNullException.ThrowIfNull(vectors);
        var report = new ProbeReport { Command = "local" };
        CheckRfc4231(report);
        CheckVectorSet(report, vectors);
        CheckVersionGuard(report);
        report.Notes.Add("Local mode makes no network call. These results say nothing about Managed HSM behaviour; see Experiments A/B/V.");
        return report;
    }

    /// <summary>RFC 4231 HMAC-SHA-256 known answers (test cases 1, 2, 3, 4, 6 and 7), through HMACSHA256 and the independent RFC 2104 construction.</summary>
    internal static void CheckRfc4231(ProbeReport report)
    {
        var cases = new (string Id, byte[] Key, byte[] Data, string Mac)[]
        {
            ("1", Enumerable.Repeat((byte)0x0b, 20).ToArray(), Encoding.ASCII.GetBytes("Hi There"), "b0344c61d8db38535ca8afceaf0bf12b881dc200c9833da726e9376c2e32cff7"),
            ("2", Encoding.ASCII.GetBytes("Jefe"), Encoding.ASCII.GetBytes("what do ya want for nothing?"), "5bdcc146bf60754e6a042426089575c75a003f089d2739839dec58b964ec3843"),
            ("3", Enumerable.Repeat((byte)0xaa, 20).ToArray(), Enumerable.Repeat((byte)0xdd, 50).ToArray(), "773ea91e36800e46854db8ebd09181a72959098b3ef8c122d9635514ced565fe"),
            ("4", Codec.FromHex("0102030405060708090a0b0c0d0e0f10111213141516171819"), Enumerable.Repeat((byte)0xcd, 50).ToArray(), "82558a389a443c0ea4cc819899f2083a85f0faa3e578f8077a2e3ff46729665b"),
            ("6", Enumerable.Repeat((byte)0xaa, 131).ToArray(), Encoding.ASCII.GetBytes("Test Using Larger Than Block-Size Key - Hash Key First"), "60e431591ee0b67f0d8a26aacbf5b77f8e0bc6213728c5140546040f0ee37f54"),
            ("7", Enumerable.Repeat((byte)0xaa, 131).ToArray(), Encoding.ASCII.GetBytes("This is a test using a larger than block-size key and a larger than block-size data. The key needs to be hashed before being used by the HMAC algorithm."), "9b09ffa71b942fcb27635fbcd5b0e944bfdc63644f0713938a7f51535c3a35e2"),
        };
        foreach (var (id, key, data, mac) in cases)
        {
            var platform = Codec.ToHex(HMACSHA256.HashData(key, data));
            var independent = Codec.ToHex(Rfc2104HmacSha256.Compute(key, data));
            report.Add("rfc4231-" + id, Experiment, platform == mac && independent == mac ? Verdict.Pass : Verdict.Fail,
                platform == mac && independent == mac ? "HMACSHA256 and the RFC 2104 construction match the RFC 4231 answer" : "HMAC primitive disagrees with RFC 4231");
        }
    }

    internal static void CheckVectorSet(ProbeReport report, ReferenceVectorSet set)
    {
        if (set.Schema != ReferenceVectorSet.SchemaName || set.LengthInput != ReferenceVectorSet.LengthInputRule)
        {
            report.Add("vector-schema", Experiment, Verdict.Fail, "unexpected vector schema or input rule");
            return;
        }

        var lengths = set.Vectors.Where(v => v.VectorKind == "Length").Select(v => v.InputLength).ToArray();
        report.Add("vector-lengths", Experiment, lengths.SequenceEqual(ReferenceVectorSet.RequiredLengths) ? Verdict.Pass : Verdict.Fail,
            "length vectors: " + string.Join(", ", lengths));
        var rowCount = set.Vectors.Count(v => v.VectorKind == "RowHmacV1");
        report.Add("vector-row-cases", Experiment, rowCount > 0 ? Verdict.Pass : Verdict.Fail, $"{rowCount} representative RowHmac/v1 canonical payloads (synthetic, published)");

        foreach (var key in set.TestKeys)
        {
            var matches = TestKeys.KeyHexById.TryGetValue(key.TestKeyId, out var hex) && hex == key.KeyHex;
            report.Add("test-key-" + key.TestKeyId, Experiment, matches ? Verdict.Pass : Verdict.Fail, matches ? "embedded test key matches the published value" : "embedded test key differs");
        }

        foreach (var vector in set.Vectors)
        {
            var input = vector.Input();
            var digest = SHA256.HashData(input);
            var problems = new List<string>();
            if (input.Length != vector.InputLength || Codec.ToHex(digest) != vector.InputSha256Hex)
            {
                problems.Add("input length or SHA-256 differs");
            }

            foreach (var mac in vector.Macs)
            {
                var key = TestKeys.Get(mac.TestKeyId);
                var raw = HMACSHA256.HashData(key, input);
                var overDigest = HMACSHA256.HashData(key, digest);
                if (Codec.ToHex(raw) != mac.RawMessageHmacSha256Hex || Codec.ToHex(Rfc2104HmacSha256.Compute(key, input)) != mac.RawMessageHmacSha256Hex)
                {
                    problems.Add($"raw-message MAC ({mac.TestKeyId}) not reproduced");
                }

                if (Codec.ToHex(overDigest) != mac.DigestMessageHmacSha256Hex || Codec.ToHex(Rfc2104HmacSha256.Compute(key, digest)) != mac.DigestMessageHmacSha256Hex)
                {
                    problems.Add($"digest-message MAC ({mac.TestKeyId}) not reproduced");
                }

                if (mac.RawEqualsDigest || mac.RawMessageHmacSha256Hex == mac.DigestMessageHmacSha256Hex)
                {
                    problems.Add($"raw and digest MACs coincide ({mac.TestKeyId}); the experiments could not tell them apart");
                }
            }

            if (vector.VectorKind == "RowHmacV1" && vector.PublishedRowSignatureHex != vector.MacFor(TestKeys.TestKey1Id).RawMessageHmacSha256Hex)
            {
                problems.Add("raw-message MAC under e6a-test-key-1 differs from the published RowHmac/v1 rowSignatureHexWithTestKey");
            }

            report.Add("vector-" + vector.VectorId, Experiment, problems.Count == 0 ? Verdict.Pass : Verdict.Fail,
                problems.Count == 0 ? $"{vector.InputLength} bytes: raw-message and digest MACs reproduced and distinct" + (vector.VectorKind == "RowHmacV1" ? "; equals published RowHmac/v1 signature" : string.Empty) : string.Join("; ", problems));
        }
    }

    internal static void CheckVersionGuard(ProbeReport report)
    {
        var endpoint = new Uri("https://probe-guard.managedhsm.azure.net/");
        var refused = new[] { string.Empty, " ", "../x", "a/b", "v1?x=1" }.All(v =>
        {
            try
            {
                _ = ManagedHsmUrls.Sign(endpoint, "e6a-probe-guard", v, "2025-07-01");
                return false;
            }
            catch (ArgumentException)
            {
                return true;
            }
        });
        report.Add("explicit-version-guard", Experiment, refused ? Verdict.Pass : Verdict.Fail,
            refused ? "sign/verify/get URLs cannot be built without an explicit, valid version identifier" : "a request without an explicit version could be built");
    }
}
