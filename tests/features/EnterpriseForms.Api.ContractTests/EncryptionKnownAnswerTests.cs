using System.Buffers.Binary;
using System.Security.Cryptography;
using EnterpriseForms.Application.Features.Documents.Ports;
using EnterpriseForms.Application.Features.FormRecords.Encryption;
using EnterpriseForms.Domain.Features.FormRecords;
using EnterpriseForms.Infrastructure.Features.Documents;
using EnterpriseForms.Infrastructure.Features.FormRecords;
using Microsoft.Extensions.Options;
using Xunit;

namespace EnterpriseForms.Api.ContractTests;

/// <summary>
/// E0-6: deterministic known-answer vectors pinning the existing AES-256-GCM v1 field-value and document envelopes, for
/// encryption and decryption, AAD binding and authentication failures. Every expected value was produced independently
/// with Python <c>cryptography</c> AESGCM from the documented layouts (docs/evidence/e0-encryption-remediation/
/// e0-kat-crosscheck.py.txt and e0-kat-output-python.txt), not by this code. A failure here means the v1 wire format changed.
/// E-1: the vectors' wrapped DEKs use the legacy reference <c>local-dev-kek-v1</c>, so decrypting them is the legacy-compatibility
/// regression (E1-5); new encryptions carry the organization- and purpose-scoped reference, and the local keyring's own wrap is
/// pinned by the K1–K4 vectors (docs/evidence/e1-key-provider-v2/).
/// </summary>
public sealed class EncryptionKnownAnswerTests
{
    private const string KeyReference = "local-dev-kek-v1";
    private const int Segment = AesGcmFileEncryptionService.SegmentSize;

    private static readonly FieldEncryptionContext FieldContextString = new(
        Guid.Parse("019a5b2c-e001-7000-8000-000000000001"), Guid.Parse("019a5b2c-e002-7000-8000-000000000002"),
        Guid.Parse("019a5b2c-e003-7000-8000-000000000003"), Guid.Parse("019a5b2c-e004-7000-8000-000000000004"), "String");

    private static readonly FileEncryptionContext DocumentContext = new(
        Guid.Parse("019a5b2c-d001-7000-8000-000000000001"), Guid.Parse("019a5b2c-d002-7000-8000-000000000002"),
        Guid.Parse("019a5b2c-d003-7000-8000-000000000003"), 3);

    private sealed record FieldVector(string Name, FieldEncryptionContext Context, string Plaintext, int DekStart, int NonceStart, string AadHex, string CiphertextHex, string TagHex, string WrappedDataKeyHex);

    private static readonly FieldVector[] FieldVectors =
    [
        new("F1", FieldContextString, "Avery Synthetic — Léa 42", 0x20, 0x50,
            "44796e616d6963466f726d732f4669656c6456616c75652f763100000001019a5b2ce00170008000000000000001019a5b2ce00270008000000000000002019a5b2ce00370008000000000000003019a5b2ce0047000800000000000000400000006537472696e67",
            "f0d6cd60480f24745a7b627f1960e08f3e6cdb1f3435d06182c8ea",
            "d82e6790db93b540e70daa1bd08e423b",
            "404142434445464748494a4bc2988c000219a124e5ed3d1db7493d7449f7606f830039144f7e74deab2ed3e405e2a68bd43d7c6116749b14f108ed35"),
        new("F2", FieldContextString with { LogicalType = "Date" }, "1990-02-28", 0x80, 0xA0,
            "44796e616d6963466f726d732f4669656c6456616c75652f763100000001019a5b2ce00170008000000000000001019a5b2ce00270008000000000000002019a5b2ce00370008000000000000003019a5b2ce004700080000000000000040000000444617465",
            "313649007190a11d4c57",
            "eaf322fc2903790d2f47e23e759ef23c",
            "b0b1b2b3b4b5b6b7b8b9babb19d4d82868483dd8cf711d2941d0064d14addb4181bb19a2c657186ac01f6c891671f4677521bec5757d9321d456aa20"),
    ];

    private sealed record DocumentVector(string Name, int PlaintextLength, string PlaintextSha256, long CiphertextLength, string CiphertextSha256, string[] SegmentTags, string? CiphertextHex);

    private const string DocumentWrappedDataKeyHex = "f0f1f2f3f4f5f6f7f8f9fafba9c781c3b8ff14b3573e3d514287ac79c0308b11ecd2994f8e8fe97b85ee0cd80dc7df0f8b620baf258883066896df02";

    private static readonly DocumentVector[] DocumentVectors =
    [
        new("D0", 0, "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855", 20, "e92f5c7e669f1d946cea47fcfe3decbf030b87cc092f42adccddcb2e574b7dc2", ["2f4de779aba88fa8f6723e51dda43864"], "000000002f4de779aba88fa8f6723e51dda43864"),
        new("D1", 37, "b11e919ce284b7e028be56562412b1eda22aeabd890995754fa3a9847c69a20c", 57, "c7674533ccac9b2d2575ff6edd500fd2245449d5883e05b840e9f56908a16757", ["077381cdc051dc1a65202346d6aa7225"],
            "00000025f7ef82aaceba466d5330bf1cb7958b4f3d41b678495d080fe5209390be1ea5ef1746d55822077381cdc051dc1a65202346d6aa7225"),
        new("D2", Segment, "172c15dc2e12b50e523d8e657cbe7fbb11c1053252bbf1e1431077d57d8128fd", 1048596, "687300a6772c328c8e84d8cad44c06b3aecf7fd42fa40263a88535067d25c0b3", ["63d7c35b27eff5ac158e574919d2ff21"], null),
        new("D3", Segment + 5, "925197be3ab2011bae3afd2c8bd521a918aaf9971c56aaabaa6a5b4bdd57043a", 1048621, "818f47b4d6a7d6cecbb8b37da25411d0fb74415bc6fc85e442de0ada8c3ef4cf", ["3e6d4570dbe79439054ed5ad8c765268", "c28f886885b42c165d96bd9ca4db2852"], null),
        new("D4", 2 * Segment, "c1b153e61d7d7835c625cc3077b85c18808a8c0f6e6b157b9bfc4546b5a34abb", 2097192, "a95d039c2268518d96cd61171208e5020b2b24e466ad288f8b808f83e4e76ba8", ["3e6d4570dbe79439054ed5ad8c765268", "1ace5ce15320126d4c89fe0111315737"], null),
    ];

    private static byte[] Seq(int start, int count) => Enumerable.Range(0, count).Select(i => (byte)((start + i) % 256)).ToArray();

    private static byte[] DocumentPlaintext(int length) => Enumerable.Range(0, length).Select(i => (byte)(((i * 7) + 3) % 256)).ToArray();

    private static string Hex(ReadOnlySpan<byte> bytes) => Convert.ToHexStringLower(bytes);

    private static LocalDevelopmentKeyProvider Keys() =>
        new(Options.Create(new EncryptionOptions { KeyProvider = EncryptionOptions.LocalDevelopmentProviderName, LocalDevelopmentKekBase64 = Convert.ToBase64String(Seq(0x00, 32)), LocalDevelopmentKeyReference = KeyReference }));

    private static FieldEncryptedValue FromVector(FieldVector v) => new()
    {
        FieldEncryptedValueId = Guid.Parse("019a5b2c-e005-7000-8000-000000000005"),
        TenantId = v.Context.TenantId,
        FormRecordId = v.Context.FormRecordId,
        FieldDefinitionId = v.Context.FieldDefinitionId,
        FieldValueItemId = v.Context.FieldValueItemId,
        EncryptedValueBytes = Convert.FromHexString(v.CiphertextHex),
        EncryptionNonceBytes = Seq(v.NonceStart, 12),
        EncryptionAuthTagBytes = Convert.FromHexString(v.TagHex),
        WrappedDataKeyBytes = Convert.FromHexString(v.WrappedDataKeyHex),
        EncryptionAlgorithmName = "AES-256-GCM",
        EncryptionKeyReference = KeyReference,
        EncryptionEnvelopeVersion = 1,
        EncryptedLogicalType = v.Context.LogicalType,
    };

    /// <summary>Splits a v1 document ciphertext into its segment tags ([UInt32BE length][ciphertext][16-byte tag] repeated).</summary>
    private static List<string> SegmentTags(byte[] ciphertext)
    {
        var tags = new List<string>();
        var offset = 0;
        while (offset < ciphertext.Length)
        {
            var length = (int)BinaryPrimitives.ReadUInt32BigEndian(ciphertext.AsSpan(offset, 4));
            tags.Add(Hex(ciphertext.AsSpan(offset + 4 + length, 16)));
            offset += 4 + length + 16;
        }

        return tags;
    }

    [Fact]
    public async Task FieldEnvelopeV1EncryptionReproducesTheVectorsExactly()
    {
        var keys = Keys();
        var service = new AesGcmFieldEncryptionService(keys);
        foreach (var v in FieldVectors)
        {
            Assert.Equal(v.AadHex, Hex(AesGcmFieldEncryptionService.Aad(v.Context)));
            var sealedRow = await service.EncryptAsync(v.Context, v.Plaintext, EncryptionKeyPurpose.FieldValue, Seq(v.DekStart, 32), Seq(v.NonceStart, 12), TestContext.Current.CancellationToken);
            Assert.Equal(v.CiphertextHex, Hex(sealedRow.EncryptedValueBytes));
            Assert.Equal(v.TagHex, Hex(sealedRow.EncryptionAuthTagBytes));
            Assert.Equal(Seq(v.NonceStart, 12), sealedRow.EncryptionNonceBytes);
            Assert.Equal(("AES-256-GCM", 1, "localdev:019a5b2c-e001-7000-8000-000000000001:field-value:1", v.Context.LogicalType), (sealedRow.EncryptionAlgorithmName, sealedRow.EncryptionEnvelopeVersion, sealedRow.EncryptionKeyReference, sealedRow.EncryptedLogicalType));
            Assert.Equal((v.Context.TenantId, v.Context.FormRecordId, v.Context.FieldDefinitionId, v.Context.FieldValueItemId), (sealedRow.TenantId, sealedRow.FormRecordId, sealedRow.FieldDefinitionId, sealedRow.FieldValueItemId));
            // The wrap nonce is random in the product; the wrapped key must still open to the vector's DEK under the scoped key, and
            // the vector's own legacy-wrapped key (fixed wrap nonce) must still open through the legacy reference.
            var scope = new DataKeyScope(v.Context.TenantId, EncryptionKeyPurpose.FieldValue);
            Assert.Equal(Seq(v.DekStart, 32), await keys.UnwrapDataKeyAsync(scope, sealedRow.EncryptionKeyReference, sealedRow.WrappedDataKeyBytes, TestContext.Current.CancellationToken));
            Assert.Equal(Seq(v.DekStart, 32), await keys.UnwrapDataKeyAsync(scope, KeyReference, Convert.FromHexString(v.WrappedDataKeyHex), TestContext.Current.CancellationToken));
            Assert.Equal(v.Plaintext, await service.DecryptAsync(sealedRow, EncryptionKeyPurpose.FieldValue, TestContext.Current.CancellationToken));
        }
    }

    [Fact]
    public async Task FieldEnvelopeV1DecryptionOpensTheIndependentlyProducedVectors()
    {
        var service = new AesGcmFieldEncryptionService(Keys());
        foreach (var v in FieldVectors)
        {
            Assert.Equal(v.Plaintext, await service.DecryptAsync(FromVector(v), EncryptionKeyPurpose.FieldValue, TestContext.Current.CancellationToken)); // legacy reference (E1-5)
        }
    }

    [Fact]
    public async Task FieldEnvelopeV1BindsEveryAadComponentAndRejectsTamperedBytes()
    {
        var service = new AesGcmFieldEncryptionService(Keys());
        var v = FieldVectors[0];
        var mutations = new (string Name, Action<FieldEncryptedValue> Mutate)[]
        {
            ("tenant", e => e.TenantId = Guid.Parse("019a5b2c-e001-7000-8000-0000000000ff")),
            ("record", e => e.FormRecordId = Guid.Parse("019a5b2c-e002-7000-8000-0000000000ff")),
            ("field", e => e.FieldDefinitionId = Guid.Parse("019a5b2c-e003-7000-8000-0000000000ff")),
            ("item", e => e.FieldValueItemId = Guid.Parse("019a5b2c-e004-7000-8000-0000000000ff")),
            ("logical type", e => e.EncryptedLogicalType = "Integer"),
            ("ciphertext", e => e.EncryptedValueBytes[0] ^= 0x01),
            ("tag", e => e.EncryptionAuthTagBytes[15] ^= 0x80),
            ("nonce", e => e.EncryptionNonceBytes[11] ^= 0x01),
            ("truncated ciphertext", e => e.EncryptedValueBytes = e.EncryptedValueBytes[..^1]),
        };
        foreach (var (name, mutate) in mutations)
        {
            var tampered = FromVector(v);
            mutate(tampered);
            var thrown = await Assert.ThrowsAnyAsync<CryptographicException>(() => service.DecryptAsync(tampered, EncryptionKeyPurpose.FieldValue, TestContext.Current.CancellationToken));
            Assert.True(DecryptionFailure.Find(thrown) is { ReasonCode: DecryptionFailureReasons.AuthenticationFailed }, name);
        }

        // The wrapped DEK is itself authenticated (key wrap AAD binds the key reference): a damaged wrapped key fails at unwrap.
        var rewrapped = FromVector(v);
        rewrapped.WrappedDataKeyBytes[20] ^= 0x01;
        Assert.Equal(DecryptionFailureReasons.KeyMismatch, DecryptionFailure.Find(await Assert.ThrowsAnyAsync<CryptographicException>(() => service.DecryptAsync(rewrapped, EncryptionKeyPurpose.FieldValue, TestContext.Current.CancellationToken)))!.ReasonCode);
    }

    [Fact]
    public async Task DocumentEnvelopeV1EncryptionReproducesTheVectorsExactly()
    {
        var ct = TestContext.Current.CancellationToken;
        var keys = Keys();
        var service = new AesGcmFileEncryptionService(keys);
        Assert.Equal("00000001019a5b2cd00170008000000000000001019a5b2cd00270008000000000000002019a5b2cd00370008000000000000003000000030000000001", Hex(AesGcmFileEncryptionService.Aad(DocumentContext, 0, true)));
        Assert.Equal("00000001019a5b2cd00170008000000000000001019a5b2cd00270008000000000000002019a5b2cd00370008000000000000003000000030000000100", Hex(AesGcmFileEncryptionService.Aad(DocumentContext, 1, false)));
        var documentScope = new DataKeyScope(DocumentContext.TenantId, EncryptionKeyPurpose.Document);
        Assert.Equal(Seq(0xC0, 32), await keys.UnwrapDataKeyAsync(documentScope, KeyReference, Convert.FromHexString(DocumentWrappedDataKeyHex), TestContext.Current.CancellationToken)); // legacy wrap (E1-5)
        foreach (var v in DocumentVectors)
        {
            var plaintext = DocumentPlaintext(v.PlaintextLength);
            using var sink = new MemoryStream();
            var outcome = await service.EncryptAsync(new MemoryStream(plaintext), sink, DocumentContext, Seq(0xC0, 32), Seq(0xE0, 8), ct);
            var ciphertext = sink.ToArray();
            Assert.Equal(v.PlaintextSha256, Hex(SHA256.HashData(plaintext)));
            Assert.Equal(v.CiphertextLength, ciphertext.Length);
            Assert.Equal(v.CiphertextSha256, Hex(SHA256.HashData(ciphertext)));
            Assert.Equal(v.SegmentTags, SegmentTags(ciphertext));
            if (v.CiphertextHex is not null)
            {
                Assert.Equal(v.CiphertextHex, Hex(ciphertext));
            }

            Assert.Equal((v.PlaintextLength, v.PlaintextSha256, v.CiphertextLength, v.CiphertextSha256), (outcome.PlaintextSizeBytes, Hex(outcome.PlaintextSha256), outcome.CiphertextSizeBytes, Hex(outcome.CiphertextSha256)));
            Assert.Equal(("AES-256-GCM", 1, "localdev:019a5b2c-d001-7000-8000-000000000001:document:1", Segment), (outcome.Envelope.AlgorithmName, outcome.Envelope.EnvelopeVersion, outcome.Envelope.KeyReference, outcome.Envelope.SegmentSizeBytes));
            Assert.Equal(Seq(0xE0, 8), outcome.Envelope.NoncePrefix);
            Assert.Equal(Seq(0xC0, 32), await keys.UnwrapDataKeyAsync(documentScope, outcome.Envelope.KeyReference, outcome.Envelope.WrappedDataKey, TestContext.Current.CancellationToken));

            // Decrypting the vector-anchored bytes under the vector's own wrapped DEK reproduces the plaintext.
            using var opened = new MemoryStream();
            await service.DecryptAsync(new MemoryStream(ciphertext), opened, Envelope(), DocumentContext, ct);
            Assert.Equal(plaintext, opened.ToArray());
        }
    }

    [Fact]
    public async Task DocumentEnvelopeV1DecryptionOpensTheIndependentlyProducedVectors()
    {
        var ct = TestContext.Current.CancellationToken;
        var service = new AesGcmFileEncryptionService(Keys());
        foreach (var v in DocumentVectors.Where(d => d.CiphertextHex is not null))
        {
            using var opened = new MemoryStream();
            await service.DecryptAsync(new MemoryStream(Convert.FromHexString(v.CiphertextHex!)), opened, Envelope(), DocumentContext, ct);
            Assert.Equal(DocumentPlaintext(v.PlaintextLength), opened.ToArray());
        }
    }

    [Fact]
    public async Task DocumentEnvelopeV1BindsEveryAadComponentAndRejectsTamperedOrRestructuredBytes()
    {
        var ct = TestContext.Current.CancellationToken;
        var service = new AesGcmFileEncryptionService(Keys());
        var d1 = Convert.FromHexString(DocumentVectors[1].CiphertextHex!);

        async Task<CryptographicException> RejectedAsync(byte[] ciphertext, FileEncryptionContext context, FileEncryptionEnvelope? envelope = null) =>
            await Assert.ThrowsAnyAsync<CryptographicException>(() => service.DecryptAsync(new MemoryStream(ciphertext), new MemoryStream(), envelope ?? Envelope(), context, ct));

        foreach (var context in new[]
                 {
                     DocumentContext with { TenantId = Guid.Parse("019a5b2c-d001-7000-8000-0000000000ff") },
                     DocumentContext with { FormRecordId = Guid.Parse("019a5b2c-d002-7000-8000-0000000000ff") },
                     DocumentContext with { FileAttachmentId = Guid.Parse("019a5b2c-d003-7000-8000-0000000000ff") },
                     DocumentContext with { VersionNumber = 4 },
                 })
        {
            Assert.Equal(DecryptionFailureReasons.AuthenticationFailed, DecryptionFailure.Find(await RejectedAsync(d1, context))!.ReasonCode);
        }

        var tag = (byte[])d1.Clone();
        tag[^1] ^= 0x01;
        await RejectedAsync(tag, DocumentContext);
        var body = (byte[])d1.Clone();
        body[10] ^= 0x01;
        await RejectedAsync(body, DocumentContext);
        await RejectedAsync(d1, DocumentContext, Envelope() with { NoncePrefix = Seq(0xE1, 8) }); // nonce prefix is part of every segment nonce

        // Final-segment flag: four trailing bytes make the sealed final segment look non-final, and dropping the final
        // segment of a two-segment document leaves a non-final segment last; both fail authentication.
        await RejectedAsync([.. d1, 0, 0, 0, 0], DocumentContext);
        using var sink = new MemoryStream();
        await service.EncryptAsync(new MemoryStream(DocumentPlaintext(Segment + 5)), sink, DocumentContext, Seq(0xC0, 32), Seq(0xE0, 8), ct);
        var twoSegments = sink.ToArray();
        await RejectedAsync(twoSegments[..(4 + Segment + 16)], DocumentContext);
    }

    private sealed record KeyringVector(string Name, Guid Organization, EncryptionKeyPurpose Purpose, int Version, string Reference, string WrapAadHex, string WrappedDataKeyHex);

    private static readonly KeyringVector[] KeyringVectors =
    [
        new("K1", FieldContextString.TenantId, EncryptionKeyPurpose.FieldValue, 1, "localdev:019a5b2c-e001-7000-8000-000000000001:field-value:1",
            "44796e616d6963466f726d732f4c6f63616c4b657972696e672f44656b577261702f7631019a5b2ce001700080000000000000010000000b6669656c642d76616c756500000001",
            "404142434445464748494a4bfcb6ccdcb29bb56ea249d05067c447a30a5a569aacdeab687ce42cd5441c2f9666dd71777becbe2ec79e6b54fcd93c70"),
        new("K2", FieldContextString.TenantId, EncryptionKeyPurpose.FieldValue, 2, "localdev:019a5b2c-e001-7000-8000-000000000001:field-value:2",
            "44796e616d6963466f726d732f4c6f63616c4b657972696e672f44656b577261702f7631019a5b2ce001700080000000000000010000000b6669656c642d76616c756500000002",
            "404142434445464748494a4bece52a983cfe34917e110f92fd82d700eba76b15708dd8d8d6b25b74b7a34e4e42445943ebb5c1f6ccc628ec21177897"),
        new("K3", FieldContextString.TenantId, EncryptionKeyPurpose.Document, 1, "localdev:019a5b2c-e001-7000-8000-000000000001:document:1",
            "44796e616d6963466f726d732f4c6f63616c4b657972696e672f44656b577261702f7631019a5b2ce0017000800000000000000100000008646f63756d656e7400000001",
            "404142434445464748494a4b8bd287a3d8776a520d4b863f7de32f9499aebeaf8562af73ba90d07af79433022e2dcb97c166bb0b4f2c8cd5e9584e68"),
        new("K4", Guid.Parse("019a5b2c-e0ff-7000-8000-0000000000ff"), EncryptionKeyPurpose.FieldValue, 1, "localdev:019a5b2c-e0ff-7000-8000-0000000000ff:field-value:1",
            "44796e616d6963466f726d732f4c6f63616c4b657972696e672f44656b577261702f7631019a5b2ce0ff700080000000000000ff0000000b6669656c642d76616c756500000001",
            "404142434445464748494a4be2ac503f923c921963046298ddf3abff76613a447fbce4600bccc706d427308497f6e10eca707a6907a79201d9b2cfdc"),
    ];

    /// <summary>E-1: the local keyring's derivation and scoped wrap (organization, purpose, version) reproduce the Python vectors exactly.</summary>
    [Fact]
    public async Task TheLocalKeyringWrapReproducesTheVectorsExactly()
    {
        foreach (var v in KeyringVectors)
        {
            // Version 2 is reached by making it current; version 1 stays retained (decrypt only).
            var options = new EncryptionOptions { KeyProvider = EncryptionOptions.LocalDevelopmentProviderName, LocalDevelopmentKekBase64 = Convert.ToBase64String(Seq(0x00, 32)), LocalDevelopmentKeyReference = KeyReference };
            if (v.Version == 2)
            {
                options.LocalDevelopmentKeyring.Purposes["FieldValue"] = new LocalKeyVersionOptions { ProvisionedVersions = [1, 2], CurrentVersion = 2, RetainedVersions = [1] };
            }

            var keys = new LocalDevelopmentKeyProvider(Options.Create(options));
            var scope = new DataKeyScope(v.Organization, v.Purpose);
            Assert.Equal(v.WrapAadHex, Hex(LocalDevelopmentKeyProvider.ScopedWrapAad(scope, v.Version)));
            var wrapped = await keys.WrapDataKeyAsync(scope, Seq(0x20, 32), Seq(0x40, 12), TestContext.Current.CancellationToken);
            Assert.Equal((v.Reference, v.WrappedDataKeyHex), (wrapped.KeyReference, Hex(wrapped.WrappedKeyBytes)));
            Assert.Equal(Seq(0x20, 32), await keys.UnwrapDataKeyAsync(scope, v.Reference, Convert.FromHexString(v.WrappedDataKeyHex), TestContext.Current.CancellationToken));
        }
    }

    private static FileEncryptionEnvelope Envelope() =>
        new("AES-256-GCM", 1, KeyReference, Convert.FromHexString(DocumentWrappedDataKeyHex), Seq(0xE0, 8), Segment);
}
