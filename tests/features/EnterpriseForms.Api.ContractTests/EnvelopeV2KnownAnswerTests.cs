using System.Security.Cryptography;
using EnterpriseForms.Application.Features.Documents.Ports;
using EnterpriseForms.Infrastructure.Features.Documents.EnvelopeV2;
using Xunit;
using static EnterpriseForms.Api.ContractTests.EnvelopeV2TestSupport;

namespace EnterpriseForms.Api.ContractTests;

/// <summary>
/// E3-M1 known-answer vectors for Envelope v2 (V2-KAT-01 to V2-KAT-11). Every expected value was produced independently with Python
/// <c>cryptography</c> 50.0.2 (HKDF, AESGCM) from the specification in docs/security/E3_ENVELOPE_V2_KEY_HIERARCHY_DESIGN.md §3,
/// not by this code (docs/evidence/e3-envelope-v2/e3m1-kat-crosscheck.py.txt and e3m1-kat-output-python.txt). A failure here means
/// the v2 format or key schedule changed. Seeds, nonces and data-key identifiers are fixed here only; production code generates them.
/// The wrapped seeds are synthetic 60-byte values: the wrap is provider-native and outside the v2 format (E1 K1–K4 pin the keyring wrap).
/// </summary>
public sealed class EnvelopeV2KnownAnswerTests
{
    private static readonly Guid Organization = Guid.Parse("019a5b2c-a001-7000-8000-00000000000a");
    private static readonly Guid Record = Guid.Parse("019a5b2c-a002-7000-8000-000000000002");
    private static readonly Guid Attachment = Guid.Parse("019a5b2c-a0f1-7000-8000-0000000000f1");

    private const string B1H0 = "4546453200020101010200019a5b2ca0017000800000000000000a0b6669656c642d76616c756500407661756c742d7472616e7369743a30313961356232632d613030312d373030302d383030302d3030303030303030303030613a6669656c642d76616c75653a33019a5b2ca0d1700080000000000000d1";
    private const string B1Commitment = "0c32321426ec2fe6fbcc7a9d5e1d039cd799a1488c1d1b3eba4b3abe0f2c2d28";

    private sealed record ItemVector(string Name, Guid Field, Guid Item, string LogicalType, string Plaintext, int NonceStart, string ItemKeyHex, string ContextHex, string CiphertextHex, string TagHex, string PortableSha256, int PortableLength);

    private static readonly ItemVector[] B1 =
    [
        new("B1.I1", Guid.Parse("019a5b2c-a003-7000-8000-000000000003"), Guid.Parse("019a5b2c-a013-7000-8000-000000000013"), "String", "Avery Synthetic", 0x90,
            "a06b0d3541233ae77447ebb1f8891543d97f90b4af4597cfd32e15342be807f6",
            "019a5b2ca00270008000000000000002019a5b2ca00370008000000000000003019a5b2ca0137000800000000000001306537472696e67",
            "f7622f7894240afc0abe2ad8dc5215", "77309d3e4a6e92dc5e9f14de33357441", "26b889d17776b5a459bc89934aac1a296f8078c01b53d7575c32ce40444d3ff9", 317),
        new("B1.I2", Guid.Parse("019a5b2c-a004-7000-8000-000000000004"), Guid.Parse("019a5b2c-a014-7000-8000-000000000014"), "Decimal", "12345.6789", 0xA0,
            "52ecbf0f6076be690162e60e6e1ec629f999df1fb8d40aa319a251415ee25d3d",
            "019a5b2ca00270008000000000000002019a5b2ca00470008000000000000004019a5b2ca0147000800000000000001407446563696d616c",
            "8307f357145d57d39727", "1c0cf42c4fcbad0c9f1e70284c3bfa54", "75c9f805e2981808d133a228fd7787eb3a3bb48b66543e1639f047ecc3bd886e", 313),
        new("B1.I3", Guid.Parse("019a5b2c-a005-7000-8000-000000000005"), Guid.Parse("019a5b2c-a015-7000-8000-000000000015"), "String", "Léa — 東京 ✓", 0xB0,
            "31dfa9127e6ea17b531b4b3df9bbc60ed6b0e5fc29a38ae23bb4aa5e325f0362",
            "019a5b2ca00270008000000000000002019a5b2ca00570008000000000000005019a5b2ca0157000800000000000001506537472696e67",
            "de74ec167204c69c095ce0d8c92a822838d2a0", "edd711166656736ab64d9063fb629d94", "afc66e5dc4710f04b157af354b9f4bc824b4231fea3c6d2928a5d22b9125cfee", 321),
    ];

    private static string Hex(ReadOnlySpan<byte> bytes) => Convert.ToHexStringLower(bytes);

    private static string Reference(EncryptionKeyPurpose purpose, int version) => EnvelopeV2KeyDouble.Reference("vault-transit", Organization, purpose, version);

    private static FieldSealBatchV2 B1Batch() =>
        FieldSealBatchV2.CreateDeterministic(Organization, Record, EncryptionKeyPurpose.FieldValue, Seq(0x60, 32), Guid.Parse("019a5b2c-a0d1-7000-8000-0000000000d1"), new WrappedDataKey(Reference(EncryptionKeyPurpose.FieldValue, 3), Seq(0xC0, 60)));

    [Fact]
    public async Task FieldBatchVectorsReproduceExactlyAndOpen()
    {
        using var batch = B1Batch();
        Assert.Equal(B1H0, Hex(batch.Header.Prefix.Encode()));
        Assert.Equal(B1Commitment, Hex(batch.Header.KeyCommitment));
        using var schedule = new EnvelopeV2KeySchedule(Seq(0x60, 32));
        Assert.Equal(B1Commitment, Hex(schedule.Commitment(Convert.FromHexString(B1H0))));

        var envelopes = new List<FieldEnvelopeV2>();
        foreach (var v in B1)
        {
            var envelope = batch.Seal(v.Field, v.Item, v.LogicalType, v.Plaintext, Seq(v.NonceStart, 12));
            Assert.Equal(v.ContextHex, Hex(envelope.Identity.Encode()));
            Assert.Equal(v.ItemKeyHex, Hex(schedule.FieldItemKey(Convert.FromHexString(B1H0), Convert.FromHexString(v.ContextHex))));
            Assert.Equal(B1H0 + B1Commitment + v.ContextHex, Hex(envelope.AssociatedData()));
            Assert.Equal(v.CiphertextHex, Hex(envelope.Ciphertext));
            Assert.Equal(v.TagHex, Hex(envelope.Tag));
            var portable = envelope.ToPortableBytes();
            Assert.Equal(v.PortableLength, portable.Length);
            Assert.Equal(v.PortableSha256, Hex(SHA256.HashData(portable)));
            Assert.Equal(portable, FieldEnvelopeV2.ParsePortable(portable).ToPortableBytes());
            envelopes.Add(FieldEnvelopeV2.ParsePortable(portable));
        }

        var keys = new EnvelopeV2KeyDouble();
        keys.Map(Organization, EncryptionKeyPurpose.FieldValue, Reference(EncryptionKeyPurpose.FieldValue, 3), Seq(0xC0, 60), Seq(0x60, 32));
        var opened = await FieldEnvelopeV2Opener.OpenAsync(keys, Organization, EncryptionKeyPurpose.FieldValue, envelopes.Select(e => new FieldOpenRequestV2(e, e.Identity)).ToList(), TestContext.Current.CancellationToken);
        Assert.Equal(B1.Select(v => v.Plaintext), opened);
        Assert.Equal(1, keys.UnwrapCalls);
    }

    [Fact]
    public async Task SealedKeyVectorReproducesExactly()
    {
        var reference = Reference(EncryptionKeyPurpose.IdempotencyProtection, 1);
        using var batch = FieldSealBatchV2.CreateDeterministic(Organization, Record, EncryptionKeyPurpose.IdempotencyProtection, Seq(0x10, 32), Guid.Parse("019a5b2c-a0d9-7000-8000-0000000000d9"), new WrappedDataKey(reference, Seq(0x08, 60)));
        Assert.Equal(EnvelopeV2PayloadKind.SealedKey, batch.Header.Prefix.Kind);
        Assert.Equal("4546453200020301010200019a5b2ca0017000800000000000000a166964656d706f74656e63792d70726f74656374696f6e004b7661756c742d7472616e7369743a30313961356232632d613030312d373030302d383030302d3030303030303030303030613a6964656d706f74656e63792d70726f74656374696f6e3a31019a5b2ca0d9700080000000000000d9", Hex(batch.Header.Prefix.Encode()));
        Assert.Equal("710d7ae753d6cfe6968c673fb6ec1bd141091f53b20f16aaf7fece20cf11267d", Hex(batch.Header.KeyCommitment));
        var plaintext = Convert.ToBase64String(Seq(0x00, 32));
        var envelope = batch.Seal(Guid.Parse("f0c70100-0000-8000-8000-000000000001"), Guid.Parse("019a5b2c-a0e1-7000-8000-0000000000e1"), EnvelopeV2Format.SealedKeyLogicalType, plaintext, Seq(0xD0, 12));
        Assert.Equal("cae8685e706324796dbc6077a9ffc3c3a565f3557229836ac87f0c9c4bcd6b80f50da4c8a2920c2ff8ffcc9a", Hex(envelope.Ciphertext));
        Assert.Equal("720e7cda766e2aac5b9970aee143bc81", Hex(envelope.Tag));
        var portable = envelope.ToPortableBytes();
        Assert.Equal(387, portable.Length);
        Assert.Equal("aec7e2d4a3b31deabf3ae29720b80b3526b02b9db36dd864fa5679c3b03e9b62", Hex(SHA256.HashData(portable)));

        var keys = new EnvelopeV2KeyDouble();
        keys.Map(Organization, EncryptionKeyPurpose.IdempotencyProtection, reference, Seq(0x08, 60), Seq(0x10, 32));
        var opened = await FieldEnvelopeV2Opener.OpenAsync(keys, Organization, EncryptionKeyPurpose.IdempotencyProtection, [new FieldOpenRequestV2(envelope, envelope.Identity)], TestContext.Current.CancellationToken);
        Assert.Equal(plaintext, Assert.Single(opened));
    }

    private const string DocumentH0 = "4546453200020201010200019a5b2ca0017000800000000000000a08646f63756d656e74003d7661756c742d7472616e7369743a30313961356232632d613030312d373030302d383030302d3030303030303030303030613a646f63756d656e743a32019a5b2ca0d2700080000000000000d2";
    private const string DocumentCommitment = "191701dbb8438210699dfa2ba2199c902ac4f0e7f1a10bfcb5adc5751f27ee34";
    private const string DocumentPortableHeader = DocumentH0 + DocumentCommitment
        + "019a5b2ca00270008000000000000002019a5b2ca0f1700080000000000000f100000002001000007071727374757677003c202122232425262728292a2b2c2d2e2f303132333435363738393a3b3c3d3e3f404142434445464748494a4b4c4d4e4f505152535455565758595a5b";

    private const int Segment = EnvelopeV2Format.SegmentSize;

    private sealed record DocumentVector(string Name, int Length, string PlaintextSha256, long CiphertextLength, string CiphertextSha256, string[] Tags, string? CiphertextHex);

    private static readonly DocumentVector[] Documents =
    [
        new("D0", 0, "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855", 20, "50f4917a5ef68b8702c0b577915d360502a53441e0162ac9e588f767ed8433bf", ["638b629ad193931d26963b7cc796eec1"], "00000000638b629ad193931d26963b7cc796eec1"),
        new("D1", 37, "b11e919ce284b7e028be56562412b1eda22aeabd890995754fa3a9847c69a20c", 57, "2b1a78c04623663f236308b4dec1ae9f856136f443e0e989f7fa2faf51604a2a", ["178d1577b4378f900fb09f61ef9c7f7b"],
            "0000002564d9cfa43702049728e52de5c06407eec9c5cabdce4575701054e415f401578d2bc2e25497178d1577b4378f900fb09f61ef9c7f7b"),
        new("D2", Segment, "172c15dc2e12b50e523d8e657cbe7fbb11c1053252bbf1e1431077d57d8128fd", 1048596, "362eb1668ede49f2e19647a182e011fa944da5e7e25202739d6851f11c5c841e", ["d5281e8bfdec44c662b8b9c5b43b7bab"], null),
        new("D3", Segment + 5, "925197be3ab2011bae3afd2c8bd521a918aaf9971c56aaabaa6a5b4bdd57043a", 1048621, "62b44d9fe06881ce628884d4b9eee20e48f999a2583173a96ca6acee1dc0a2b9", ["5f55ec32248d1914f6446858b2905ea5", "cac73183400a5624d99e111a91c306fe"], null),
        new("D4", 2 * Segment, "c1b153e61d7d7835c625cc3077b85c18808a8c0f6e6b157b9bfc4546b5a34abb", 2097192, "5176494a7340cc9e5fdc0c3af485a6c0dc9307745a8a3e1f0e1b57494c54e60b", ["5f55ec32248d1914f6446858b2905ea5", "bbe686c602c52e2bb55d897fe4b24173"], null),
    ];

    private static byte[] DocumentPlaintext(int length) => Enumerable.Range(0, length).Select(i => (byte)(((i * 7) + 3) % 256)).ToArray();

    private static readonly DocumentVersionIdentity DocumentIdentity = new(Record, Attachment, 2);

    [Fact]
    public async Task DocumentVectorsReproduceExactlyAndDecrypt()
    {
        var reference = Reference(EncryptionKeyPurpose.Document, 2);
        using (var schedule = new EnvelopeV2KeySchedule(Seq(0x30, 32)))
        {
            Assert.Equal(DocumentCommitment, Hex(schedule.Commitment(Convert.FromHexString(DocumentH0))));
            Assert.Equal("d2a7ad89b8ef85e2e7d778b0366f7ba1daac1115f587c4f4ee9c7b8413eb790c", Hex(schedule.DocumentKey(Convert.FromHexString(DocumentH0), Convert.FromHexString(DocumentPortableHeader[(DocumentH0.Length + DocumentCommitment.Length)..][..96]))));
        }

        var keys = new EnvelopeV2KeyDouble();
        keys.Map(Organization, EncryptionKeyPurpose.Document, reference, Seq(0x20, 60), Seq(0x30, 32));
        foreach (var v in Documents)
        {
            var plaintext = DocumentPlaintext(v.Length);
            Assert.Equal(v.PlaintextSha256, Hex(SHA256.HashData(plaintext)));
            using var sink = new MemoryStream();
            var outcome = await DocumentEnvelopeV2Cipher.EncryptAsync(new MemoryStream(plaintext), sink, Organization, DocumentIdentity, Seq(0x30, 32), Guid.Parse("019a5b2c-a0d2-7000-8000-0000000000d2"), Seq(0x70, 8), new WrappedDataKey(reference, Seq(0x20, 60)), TestContext.Current.CancellationToken);
            var ciphertext = sink.ToArray();
            Assert.Equal(DocumentPortableHeader, Hex(outcome.Envelope.ToPortableHeaderBytes()));
            Assert.Equal(v.CiphertextLength, ciphertext.Length);
            Assert.Equal(v.CiphertextLength, outcome.CiphertextSizeBytes);
            Assert.Equal(v.CiphertextSha256, Hex(SHA256.HashData(ciphertext)));
            Assert.Equal(v.CiphertextSha256, Hex(outcome.CiphertextSha256));
            Assert.Equal(v.PlaintextSha256, Hex(outcome.PlaintextSha256));
            Assert.Equal(v.Length, outcome.PlaintextSizeBytes);
            Assert.Equal(v.Tags, SegmentTags(ciphertext));
            if (v.CiphertextHex is { } expectedHex)
            {
                Assert.Equal(expectedHex, Hex(ciphertext));
            }

            var parsed = DocumentEnvelopeV2.ParsePortableHeader(Convert.FromHexString(DocumentPortableHeader));
            using var plain = new MemoryStream();
            await DocumentEnvelopeV2Cipher.DecryptAsync(new MemoryStream(ciphertext), plain, parsed, Organization, DocumentIdentity, keys, TestContext.Current.CancellationToken);
            Assert.Equal(plaintext, plain.ToArray());
        }
    }

    [Fact]
    public void TheHeaderLayoutMatchesTheSpecificationOffsets()
    {
        // V2-KAT-11: field-by-field layout of B1's H0 (§3.3).
        var h0 = Convert.FromHexString(B1H0);
        Assert.Equal("EFE2"u8.ToArray(), h0[..4]);
        Assert.Equal(new byte[] { 0x00, 0x02 }, h0[4..6]);
        Assert.Equal(1, h0[6]); // FieldValueItem
        Assert.Equal(1, h0[7]); // AES-256-GCM
        Assert.Equal(1, h0[8]); // HKDF-SHA256
        Assert.Equal(2, h0[9]); // vault-transit wrap
        Assert.Equal(0, h0[10]); // reserved flags
        Assert.Equal(Organization, new Guid(h0.AsSpan(11, 16), bigEndian: true));
        Assert.Equal(11, h0[27]);
        Assert.Equal("field-value"u8.ToArray(), h0[28..39]);
        Assert.Equal(64, (h0[39] << 8) | h0[40]);
        Assert.Equal(System.Text.Encoding.ASCII.GetBytes(Reference(EncryptionKeyPurpose.FieldValue, 3)), h0[41..105]);
        Assert.Equal(Guid.Parse("019a5b2c-a0d1-7000-8000-0000000000d1"), new Guid(h0.AsSpan(105, 16), bigEndian: true));
        Assert.Equal(121, h0.Length);
    }
}
