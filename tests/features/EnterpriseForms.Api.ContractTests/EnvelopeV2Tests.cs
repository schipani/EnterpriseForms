using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using EnterpriseForms.Application.Features.Documents.Ports;
using EnterpriseForms.Application.Features.FormRecords.Encryption;
using EnterpriseForms.Domain.Features.FormRecords;
using EnterpriseForms.Infrastructure.Features.Documents.EnvelopeV2;
using EnterpriseForms.Infrastructure.Features.FormRecords;
using Xunit;
using static EnterpriseForms.Api.ContractTests.EnvelopeV2TestSupport;

namespace EnterpriseForms.Api.ContractTests;

/// <summary>
/// E3-M1: Envelope v2 round trips, identity binding, tampering, strict parsing, nonce discipline, domain separation and legacy
/// isolation (V2-RT, V2-ISO, V2-TMP, local V2-CMP, V2-PRF-01/02). The core is not wired into any product path in E3-M1; these
/// tests reach it through InternalsVisibleTo.
/// </summary>
public sealed class EnvelopeV2Tests
{
    private static readonly Guid OrganizationA = Guid.Parse("019a5b2c-a001-7000-8000-00000000000a");
    private static readonly Guid OrganizationB = Guid.Parse("019a5b2c-b001-7000-8000-00000000000b");
    private static readonly Guid RecordA = Guid.Parse("019a5b2c-a002-7000-8000-000000000002");
    private static readonly Guid RecordB = Guid.Parse("019a5b2c-a102-7000-8000-000000000102");
    private static readonly Guid FieldA = Guid.Parse("019a5b2c-a003-7000-8000-000000000003");
    private static readonly Guid FieldB = Guid.Parse("019a5b2c-a004-7000-8000-000000000004");
    private static readonly Guid ItemA = Guid.Parse("019a5b2c-a013-7000-8000-000000000013");
    private static readonly Guid ItemB = Guid.Parse("019a5b2c-a014-7000-8000-000000000014");
    private static readonly Guid Attachment = Guid.Parse("019a5b2c-a0f1-7000-8000-0000000000f1");
    private static readonly Guid DataKey = Guid.Parse("019a5b2c-a0d1-7000-8000-0000000000d1");
    private static readonly byte[] Seed = Seq(0x60, 32);
    private static readonly byte[] Wrapped = Seq(0xC0, 60);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string VaultReference(Guid organization, EncryptionKeyPurpose purpose, int version) => EnvelopeV2KeyDouble.Reference("vault-transit", organization, purpose, version);

    /// <summary>A batch with a known seed, and a key double that unwraps exactly its wrap.</summary>
    private static (EnvelopeV2KeyDouble Keys, FieldSealBatchV2 Batch) KnownBatch(EncryptionKeyPurpose? purpose = null)
    {
        var p = purpose ?? EncryptionKeyPurpose.FieldValue;
        var reference = VaultReference(OrganizationA, p, 1);
        var keys = new EnvelopeV2KeyDouble();
        keys.Map(OrganizationA, p, reference, Wrapped, Seed);
        return (keys, FieldSealBatchV2.CreateDeterministic(OrganizationA, RecordA, p, Seed, DataKey, new WrappedDataKey(reference, Wrapped)));
    }

    private static (EnvelopeV2KeyDouble Keys, FieldEnvelopeV2 Envelope) KnownEnvelope(string plaintext = "Avery Synthetic")
    {
        var (keys, batch) = KnownBatch();
        using (batch)
        {
            return (keys, batch.Seal(FieldA, ItemA, "String", plaintext, Seq(0x90, 12)));
        }
    }

    private static FieldEnvelopeV2 Rebuild(FieldEnvelopeV2 e, EnvelopeV2HeaderPrefix? prefix = null, byte[]? commitment = null, FieldItemIdentity? identity = null, byte[]? wrapped = null, byte[]? nonce = null, byte[]? tag = null, byte[]? ciphertext = null) =>
        new(new EnvelopeV2Header(prefix ?? e.Header.Prefix, commitment ?? e.Header.KeyCommitment.ToArray()), identity ?? e.Identity, wrapped ?? e.WrappedSeed.ToArray(), nonce ?? e.Nonce.ToArray(), tag ?? e.Tag.ToArray(), ciphertext ?? e.Ciphertext.ToArray());

    private static Task<IReadOnlyList<string>> Open(IEncryptionKeyProvider keys, FieldEnvelopeV2 envelope, Guid? organization = null, EncryptionKeyPurpose? purpose = null, FieldItemIdentity? expected = null) =>
        FieldEnvelopeV2Opener.OpenAsync(keys, organization ?? envelope.Header.Prefix.OrganizationId, purpose ?? EnvelopeV2Format.PurposeOf(envelope.Header.Prefix.Kind), [new FieldOpenRequestV2(envelope, expected ?? envelope.Identity)], Ct);

    private static async Task Refused(EnvelopeV2FailureReason reason, Func<Task> action) => Assert.Equal(reason, (await Assert.ThrowsAsync<EnvelopeV2Exception>(action)).Reason);

    private static void RefusedSync(EnvelopeV2FailureReason reason, Action action) => Assert.Equal(reason, Assert.Throws<EnvelopeV2Exception>(action).Reason);

    private static byte[] With(byte[] source, int offset, params byte[] replacement)
    {
        var copy = source.ToArray();
        replacement.CopyTo(copy, offset);
        return copy;
    }

    // B1-style portable field envelope offsets (H0 = 121 bytes for a vault-transit field-value reference of 64 characters).
    private const int OffsetVersion = 4, OffsetKind = 6, OffsetAead = 7, OffsetKdf = 8, OffsetWrap = 9, OffsetFlags = 10, OffsetOrganization = 11;
    private const int OffsetPurposeLength = 27, OffsetReference = 41, OffsetDataKey = 105, OffsetCommitment = 121, OffsetRecord = 153, OffsetLogicalType = 202;
    private const int OffsetWrappedLength = 208, OffsetWrapped = 210, OffsetNonce = 270, OffsetTag = 282, OffsetCiphertextLength = 298, OffsetCiphertext = 302;

    // ---------- Round trips (V2-RT) ----------

    [Fact]
    public async Task EveryLogicalTypeRoundTripsThroughTheRealDevelopmentKeyring()
    {
        var keys = LocalKeyring();
        var values = new (string Type, string Plaintext)[]
        {
            ("String", "Avery Synthetic"), ("String", string.Empty), ("String", "Léa — 東京 ✓ 𝄞"), ("String", new string('x', 64 * 1024)),
            ("Integer", "-9007199254740991"), ("Decimal", "12345.6789"), ("Date", "1990-02-28"), ("DateTime", "2026-11-13T15:04:05.123Z"),
            ("Boolean", "true"), ("Guid", "019a5b2c-a0aa-7000-8000-0000000000aa"), ("Option", "019a5b2c-a0ab-7000-8000-0000000000ab"),
        };
        using var batch = await FieldSealBatchV2.CreateAsync(keys, OrganizationA, RecordA, EncryptionKeyPurpose.FieldValue, Ct);
        Assert.Equal($"localdev:{OrganizationA:D}:field-value:1", batch.Header.Prefix.KekReference);
        Assert.Equal(EnvelopeV2WrapAlgorithm.LocalKeyringAes256Gcm, batch.Header.Prefix.WrapAlgorithm);
        Assert.True(EnvelopeV2Format.IsUuidVersion7(batch.Header.Prefix.DataKeyIdentifier));
        var requests = values.Select(v =>
        {
            var item = Guid.CreateVersion7();
            var envelope = FieldEnvelopeV2.ParsePortable(batch.Seal(FieldA, item, v.Type, v.Plaintext).ToPortableBytes());
            return new FieldOpenRequestV2(envelope, new FieldItemIdentity(RecordA, FieldA, item, v.Type));
        }).ToList();

        Assert.Equal(values.Select(v => v.Plaintext), await FieldEnvelopeV2Opener.OpenAsync(keys, OrganizationA, EncryptionKeyPurpose.FieldValue, requests, Ct));
    }

    [Fact]
    public async Task TheSealedKeyKindRoundTripsUnderItsOwnPurpose()
    {
        var keys = LocalKeyring();
        using var batch = await FieldSealBatchV2.CreateAsync(keys, OrganizationA, RecordA, EncryptionKeyPurpose.IdempotencyProtection, Ct);
        var envelope = batch.Seal(Guid.Parse("f0c70100-0000-8000-8000-000000000001"), ItemA, EnvelopeV2Format.SealedKeyLogicalType, "AAECAwQ=");
        Assert.Equal($"localdev:{OrganizationA:D}:idempotency-protection:1", envelope.Header.Prefix.KekReference);
        Assert.Equal("AAECAwQ=", Assert.Single(await Open(keys, envelope)));
        Assert.Throws<EnvelopeV2Exception>(() => batch.Seal(FieldA, ItemB, "String", "x")); // a SealedKey carries only the seal type
    }

    [Fact]
    public async Task DocumentsRoundTripThroughTheRealDevelopmentKeyringAtSegmentBoundaries()
    {
        var keys = LocalKeyring();
        foreach (var length in new[] { 0, 1, 37, EnvelopeV2Format.SegmentSize - 1, EnvelopeV2Format.SegmentSize, (2 * EnvelopeV2Format.SegmentSize) + 3 })
        {
            var plaintext = RandomNumberGenerator.GetBytes(length);
            var identity = new DocumentVersionIdentity(RecordA, Attachment, 4);
            using var sink = new MemoryStream();
            var outcome = await DocumentEnvelopeV2Cipher.EncryptAsync(new MemoryStream(plaintext), sink, keys, OrganizationA, identity, Ct);
            Assert.Equal(SHA256.HashData(plaintext), outcome.PlaintextSha256);
            Assert.Equal($"localdev:{OrganizationA:D}:document:1", outcome.Envelope.Header.Prefix.KekReference);
            var envelope = DocumentEnvelopeV2.ParsePortableHeader(outcome.Envelope.ToPortableHeaderBytes());
            using var plain = new MemoryStream();
            await DocumentEnvelopeV2Cipher.DecryptAsync(new MemoryStream(sink.ToArray()), plain, envelope, OrganizationA, identity, keys, Ct);
            Assert.Equal(plaintext, plain.ToArray());
        }
    }

    [Fact]
    public async Task OneUnwrapServesAWholeBatchAndEachDistinctBatchCostsOne()
    {
        // V2-PRF-01/02: a 250-item owner save needs one unwrap; a later single-item (shared) edit adds one more group.
        var keys = new EnvelopeV2KeyDouble();
        var requests = new List<FieldOpenRequestV2>();
        using (var batch = await FieldSealBatchV2.CreateAsync(keys, OrganizationA, RecordA, EncryptionKeyPurpose.FieldValue, Ct))
        {
            for (var i = 0; i < 250; i++)
            {
                var envelope = batch.Seal(FieldA, Guid.CreateVersion7(), "String", "value " + i);
                requests.Add(new FieldOpenRequestV2(envelope, envelope.Identity));
            }
        }

        await FieldEnvelopeV2Opener.OpenAsync(keys, OrganizationA, EncryptionKeyPurpose.FieldValue, requests, Ct);
        Assert.Equal(1, keys.UnwrapCalls);

        using (var edit = await FieldSealBatchV2.CreateAsync(keys, OrganizationA, RecordA, EncryptionKeyPurpose.FieldValue, Ct))
        {
            var envelope = edit.Seal(FieldB, ItemB, "String", "edited");
            requests.Add(new FieldOpenRequestV2(envelope, envelope.Identity));
        }

        var opened = await FieldEnvelopeV2Opener.OpenAsync(keys, OrganizationA, EncryptionKeyPurpose.FieldValue, requests, Ct);
        Assert.Equal("edited", opened[^1]);
        Assert.Equal(3, keys.UnwrapCalls);
        Assert.Equal(2, keys.WrapCalls);
    }

    // ---------- Incorrect keys ----------

    [Fact]
    public async Task AWrongDataKeyFailsTheCommitmentBeforeAnyPayloadIsDecrypted()
    {
        var (keys, envelope) = KnownEnvelope();
        keys.Override = () => RandomNumberGenerator.GetBytes(32);
        await Refused(EnvelopeV2FailureReason.KeyCommitmentMismatch, () => Open(keys, envelope));
        keys.Override = () => RandomNumberGenerator.GetBytes(16);
        await Refused(EnvelopeV2FailureReason.InvalidDataKey, () => Open(keys, envelope));
        keys.Override = () => Seed.ToArray();
        Assert.Equal("Avery Synthetic", Assert.Single(await Open(keys, envelope)));
    }

    [Fact]
    public async Task AnotherKeyringCannotUnwrapTheSeedAndTheProviderRefusalPropagatesUnchanged()
    {
        using var batch = await FieldSealBatchV2.CreateAsync(LocalKeyring(0x00), OrganizationA, RecordA, EncryptionKeyPurpose.FieldValue, Ct);
        var envelope = batch.Seal(FieldA, ItemA, "String", "x");
        var ex = await Assert.ThrowsAnyAsync<CryptographicException>(() => Open(LocalKeyring(0x40), envelope));
        Assert.IsNotType<EnvelopeV2Exception>(ex);
    }

    [Fact]
    public async Task ProviderOutageAndCancellationPropagateWithoutFallback()
    {
        var (keys, envelope) = KnownEnvelope();
        keys.Unavailable = true;
        await Assert.ThrowsAsync<EncryptionUnavailableException>(() => Open(keys, envelope));
        await Assert.ThrowsAsync<EncryptionUnavailableException>(() => FieldSealBatchV2.CreateAsync(keys, OrganizationA, RecordA, EncryptionKeyPurpose.FieldValue, Ct));
        keys.Unavailable = false;
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => FieldEnvelopeV2Opener.OpenAsync(keys, OrganizationA, EncryptionKeyPurpose.FieldValue, [new FieldOpenRequestV2(envelope, envelope.Identity)], cancelled.Token));
    }

    // ---------- Identity binding (V2-ISO) ----------

    [Fact]
    public async Task TheExpectedOrganizationPurposeRecordFieldItemAndTypeAreCheckedBeforeAnyKeyOperation()
    {
        var (keys, envelope) = KnownEnvelope();
        var id = envelope.Identity;
        await Refused(EnvelopeV2FailureReason.IdentityMismatch, () => Open(keys, envelope, organization: OrganizationB));
        await Refused(EnvelopeV2FailureReason.IdentityMismatch, () => Open(keys, envelope, purpose: EncryptionKeyPurpose.IdempotencyProtection));
        await Refused(EnvelopeV2FailureReason.IdentityMismatch, () => Open(keys, envelope, expected: id with { FormRecordId = RecordB }));
        await Refused(EnvelopeV2FailureReason.IdentityMismatch, () => Open(keys, envelope, expected: id with { FieldDefinitionId = FieldB }));
        await Refused(EnvelopeV2FailureReason.IdentityMismatch, () => Open(keys, envelope, expected: id with { FieldValueItemId = ItemB }));
        await Refused(EnvelopeV2FailureReason.IdentityMismatch, () => Open(keys, envelope, expected: id with { LogicalType = "Integer" }));
        Assert.Equal(0, keys.UnwrapCalls);
        await Assert.ThrowsAsync<ArgumentException>(() => Open(keys, envelope, purpose: EncryptionKeyPurpose.Document));
        await Assert.ThrowsAsync<ArgumentException>(() => Open(keys, envelope, purpose: EncryptionKeyPurpose.AuditorReasonNote));
    }

    [Fact]
    public async Task ARelabelledEnvelopeFailsCryptographicallyEvenWhenTheKeyProviderBindsNothing()
    {
        // The provider returns the true seed for ANY request, as a wrap without associated data would. Binding must then come
        // from the key commitment (header fields: organization, purpose, kind, reference, family, data key) and from the derived
        // key plus AAD (item context: record, field, item, logical type).
        var (keys, envelope) = KnownEnvelope();
        keys.Override = () => Seed.ToArray();
        Assert.Equal("Avery Synthetic", Assert.Single(await Open(keys, envelope)));

        var p = envelope.Header.Prefix;
        var relabelledHeaders = new[]
        {
            EnvelopeV2HeaderPrefix.Create(p.Kind, p.WrapAlgorithm, OrganizationB, VaultReference(OrganizationB, EncryptionKeyPurpose.FieldValue, 1), p.DataKeyIdentifier),
            EnvelopeV2HeaderPrefix.Create(p.Kind, p.WrapAlgorithm, OrganizationA, VaultReference(OrganizationA, EncryptionKeyPurpose.FieldValue, 2), p.DataKeyIdentifier),
            EnvelopeV2HeaderPrefix.Create(p.Kind, p.WrapAlgorithm, OrganizationA, p.KekReference, Guid.CreateVersion7()),
            EnvelopeV2HeaderPrefix.Create(p.Kind, EnvelopeV2WrapAlgorithm.LocalKeyringAes256Gcm, OrganizationA, EnvelopeV2KeyDouble.Reference("localdev", OrganizationA, EncryptionKeyPurpose.FieldValue, 1), p.DataKeyIdentifier),
        };
        foreach (var header in relabelledHeaders)
        {
            await Refused(EnvelopeV2FailureReason.KeyCommitmentMismatch, () => Open(keys, Rebuild(envelope, prefix: header)));
        }

        var sealedKind = EnvelopeV2HeaderPrefix.Create(EnvelopeV2PayloadKind.SealedKey, p.WrapAlgorithm, OrganizationA, VaultReference(OrganizationA, EncryptionKeyPurpose.IdempotencyProtection, 1), p.DataKeyIdentifier);
        await Refused(EnvelopeV2FailureReason.KeyCommitmentMismatch, () => Open(keys, Rebuild(envelope, prefix: sealedKind, identity: envelope.Identity with { LogicalType = EnvelopeV2Format.SealedKeyLogicalType })));

        // The item context is outside the commitment: relabelling it passes the commitment and fails AEAD (other key, other AAD).
        foreach (var relabelled in new[]
                 {
                     envelope.Identity with { FormRecordId = RecordB }, envelope.Identity with { FieldDefinitionId = FieldB },
                     envelope.Identity with { FieldValueItemId = ItemB }, envelope.Identity with { LogicalType = "Integer" },
                 })
        {
            await Refused(EnvelopeV2FailureReason.AuthenticationFailed, () => Open(keys, Rebuild(envelope, identity: relabelled)));
        }
    }

    [Fact]
    public async Task ACiphertextMovedToAnotherItemRecordOrOrganizationNeverOpens()
    {
        // Field copying / movement: an envelope cannot be reused for another item, field, record or organization; it must be re-sealed.
        var (keys, batch) = KnownBatch();
        using (batch)
        {
            var a = batch.Seal(FieldA, ItemA, "String", "secret A", Seq(0x90, 12));
            var b = batch.Seal(FieldB, ItemB, "String", "secret B", Seq(0xA0, 12));
            keys.Override = () => Seed.ToArray();

            // a's ciphertext/nonce/tag presented as item b (same batch, same seed): AEAD fails.
            var swapped = Rebuild(b, nonce: a.Nonce.ToArray(), tag: a.Tag.ToArray(), ciphertext: a.Ciphertext.ToArray());
            await Refused(EnvelopeV2FailureReason.AuthenticationFailed, () => Open(keys, swapped));

            // Two genuine envelopes of one batch still open together with one unwrap.
            keys.Override = null;
            var opened = await FieldEnvelopeV2Opener.OpenAsync(keys, OrganizationA, EncryptionKeyPurpose.FieldValue, [new FieldOpenRequestV2(a, a.Identity), new FieldOpenRequestV2(b, b.Identity)], Ct);
            Assert.Equal("secret A", opened[0]);
            Assert.Equal("secret B", opened[1]);
        }
    }

    [Fact]
    public async Task TheRealKeyringRefusesAWrapPresentedForAnotherOrganizationOrPurpose()
    {
        var keys = LocalKeyring();
        using var batch = await FieldSealBatchV2.CreateAsync(keys, OrganizationA, RecordA, EncryptionKeyPurpose.FieldValue, Ct);
        var envelope = batch.Seal(FieldA, ItemA, "String", "x");
        var p = envelope.Header.Prefix;
        var forB = Rebuild(envelope, prefix: EnvelopeV2HeaderPrefix.Create(p.Kind, p.WrapAlgorithm, OrganizationB, EnvelopeV2KeyDouble.Reference("localdev", OrganizationB, EncryptionKeyPurpose.FieldValue, 1), p.DataKeyIdentifier));
        await Assert.ThrowsAnyAsync<CryptographicException>(() => Open(keys, forB));
    }

    // ---------- Tampering (V2-TMP) ----------

    [Fact]
    public async Task EverySingleBitFlipOfAPortableEnvelopeIsRefused()
    {
        var (keys, envelope) = KnownEnvelope();
        var portable = envelope.ToPortableBytes();
        Assert.Equal(317, portable.Length);
        for (var offset = 0; offset < portable.Length; offset++)
        {
            for (var bit = 0; bit < 8; bit++)
            {
                var mutated = portable.ToArray();
                mutated[offset] ^= (byte)(1 << bit);
                await Assert.ThrowsAnyAsync<CryptographicException>(async () =>
                {
                    var parsed = FieldEnvelopeV2.ParsePortable(mutated);
                    await Open(keys, parsed, organization: OrganizationA, purpose: EncryptionKeyPurpose.FieldValue, expected: envelope.Identity);
                });
            }
        }

        Assert.Equal("Avery Synthetic", Assert.Single(await Open(keys, FieldEnvelopeV2.ParsePortable(portable))));
    }

    [Fact]
    public async Task EveryAuthenticatedByteIsBoundEvenAgainstAProviderThatReturnsTheTrueSeedForAnyRequest()
    {
        // Each bit of H, C_field, nonce, tag and ciphertext is authenticated by the commitment or the AEAD, independently of the
        // provider. The wrapped seed is not: its integrity is the provider's authenticated wrap and the commitment (a different
        // seed fails it). Flipping it under this permissive provider yields the same seed and so the same plaintext: no change.
        var (keys, envelope) = KnownEnvelope();
        keys.Override = () => Seed.ToArray();
        var portable = envelope.ToPortableBytes();
        for (var offset = 0; offset < portable.Length; offset++)
        {
            var mutated = portable.ToArray();
            mutated[offset] ^= 0x01;
            FieldEnvelopeV2 parsed;
            try
            {
                parsed = FieldEnvelopeV2.ParsePortable(mutated);
            }
            catch (EnvelopeV2Exception)
            {
                continue;
            }

            if (offset is >= OffsetWrapped and < OffsetNonce)
            {
                Assert.Equal("Avery Synthetic", Assert.Single(await Open(keys, parsed)));
                continue;
            }

            await Assert.ThrowsAsync<EnvelopeV2Exception>(() => Open(keys, parsed));
        }
    }

    [Fact]
    public async Task HeaderCommitmentCiphertextTagAndNonceTamperingAreEachRefusedWithTheRightReason()
    {
        var (keys, envelope) = KnownEnvelope();
        var commitment = envelope.Header.KeyCommitment.ToArray();
        commitment[0] ^= 0x80;
        await Refused(EnvelopeV2FailureReason.KeyCommitmentMismatch, () => Open(keys, Rebuild(envelope, commitment: commitment)));
        var ciphertext = envelope.Ciphertext.ToArray();
        ciphertext[^1] ^= 0x01;
        await Refused(EnvelopeV2FailureReason.AuthenticationFailed, () => Open(keys, Rebuild(envelope, ciphertext: ciphertext)));
        await Refused(EnvelopeV2FailureReason.AuthenticationFailed, () => Open(keys, Rebuild(envelope, ciphertext: envelope.Ciphertext[..^1].ToArray())));
        var tag = envelope.Tag.ToArray();
        tag[15] ^= 0x01;
        await Refused(EnvelopeV2FailureReason.AuthenticationFailed, () => Open(keys, Rebuild(envelope, tag: tag)));
        var nonce = envelope.Nonce.ToArray();
        nonce[0] ^= 0x01;
        await Refused(EnvelopeV2FailureReason.AuthenticationFailed, () => Open(keys, Rebuild(envelope, nonce: nonce)));
        var wrapped = envelope.WrappedSeed.ToArray();
        wrapped[30] ^= 0x01;
        await Assert.ThrowsAsync<CryptographicException>(() => Open(keys, Rebuild(envelope, wrapped: wrapped))); // the provider refuses the wrap
    }

    [Fact]
    public async Task EnvelopesSharingADataKeyMustAgreeAndMayNotRepeatAnItemOrNonce()
    {
        var (keys, batch) = KnownBatch();
        using (batch)
        {
            var a = batch.Seal(FieldA, ItemA, "String", "a", Seq(0x90, 12));
            var b = batch.Seal(FieldB, ItemB, "String", "b", Seq(0xA0, 12));
            var otherWrap = Rebuild(b, wrapped: Seq(0x01, 60));
            var commitment = b.Header.KeyCommitment.ToArray();
            commitment[5] ^= 0x10;
            var otherCommitment = Rebuild(b, commitment: commitment);
            var sameNonce = Rebuild(b, nonce: a.Nonce.ToArray());
            foreach (var second in new[] { otherWrap, otherCommitment, sameNonce, a })
            {
                await Refused(EnvelopeV2FailureReason.InconsistentDataKeyGroup, () => FieldEnvelopeV2Opener.OpenAsync(keys, OrganizationA, EncryptionKeyPurpose.FieldValue, [new FieldOpenRequestV2(a, a.Identity), new FieldOpenRequestV2(second, second.Identity)], Ct));
            }

            Assert.Equal(0, keys.UnwrapCalls);
        }
    }

    [Fact]
    public async Task DocumentTamperingTruncationReorderingAndRelabellingAreRefused()
    {
        var keys = LocalKeyring();
        var identity = new DocumentVersionIdentity(RecordA, Attachment, 3);
        var plaintext = RandomNumberGenerator.GetBytes((2 * EnvelopeV2Format.SegmentSize) + 11);
        using var sink = new MemoryStream();
        var envelope = (await DocumentEnvelopeV2Cipher.EncryptAsync(new MemoryStream(plaintext), sink, keys, OrganizationA, identity, Ct)).Envelope;
        var stream = sink.ToArray();
        var segment = 4 + EnvelopeV2Format.SegmentSize + 16;

        async Task Decrypt(byte[] ciphertext, DocumentEnvelopeV2? e = null, DocumentVersionIdentity? expected = null, Guid? organization = null)
        {
            using var output = new MemoryStream();
            await DocumentEnvelopeV2Cipher.DecryptAsync(new MemoryStream(ciphertext), output, e ?? envelope, organization ?? OrganizationA, expected ?? identity, keys, Ct);
        }

        await Decrypt(stream);
        await Refused(EnvelopeV2FailureReason.AuthenticationFailed, () => Decrypt(With(stream, 100, (byte)(stream[100] ^ 1))));
        await Refused(EnvelopeV2FailureReason.AuthenticationFailed, () => Decrypt(With(stream, segment - 1, (byte)(stream[segment - 1] ^ 1))));
        await Refused(EnvelopeV2FailureReason.AuthenticationFailed, () => Decrypt(stream[..(2 * segment)])); // last segment dropped: segment 2 was not final
        await Refused(EnvelopeV2FailureReason.MalformedEnvelope, () => Decrypt(stream[..^1]));
        await Refused(EnvelopeV2FailureReason.MalformedEnvelope, () => Decrypt([.. stream, 0, 0]));
        await Refused(EnvelopeV2FailureReason.MalformedEnvelope, () => Decrypt([]));
        var reordered = stream[segment..(2 * segment)].Concat(stream[..segment]).Concat(stream[(2 * segment)..]).ToArray();
        await Refused(EnvelopeV2FailureReason.AuthenticationFailed, () => Decrypt(reordered));
        var oversize = stream.ToArray();
        BinaryPrimitives.WriteUInt32BigEndian(oversize, EnvelopeV2Format.SegmentSize + 1);
        await Refused(EnvelopeV2FailureReason.MalformedEnvelope, () => Decrypt(oversize));

        await Refused(EnvelopeV2FailureReason.IdentityMismatch, () => Decrypt(stream, expected: identity with { VersionNumber = 4 }));
        await Refused(EnvelopeV2FailureReason.IdentityMismatch, () => Decrypt(stream, expected: identity with { FileAttachmentId = ItemA }));
        await Refused(EnvelopeV2FailureReason.IdentityMismatch, () => Decrypt(stream, organization: OrganizationB));

        // Relabelled context (version or nonce prefix) passes the commitment and fails the first segment's AEAD.
        var relabelled = new DocumentEnvelopeV2(envelope.Header, new DocumentContext(identity with { VersionNumber = 4 }, EnvelopeV2Format.SegmentSize, envelope.Context.NoncePrefix), envelope.WrappedSeed);
        await Refused(EnvelopeV2FailureReason.AuthenticationFailed, () => Decrypt(stream, relabelled, identity with { VersionNumber = 4 }));
        var otherPrefix = new DocumentEnvelopeV2(envelope.Header, new DocumentContext(identity, EnvelopeV2Format.SegmentSize, Seq(0x01, 8)), envelope.WrappedSeed);
        await Refused(EnvelopeV2FailureReason.AuthenticationFailed, () => Decrypt(stream, otherPrefix));
        var commitment = envelope.Header.KeyCommitment.ToArray();
        commitment[31] ^= 0x01;
        await Refused(EnvelopeV2FailureReason.KeyCommitmentMismatch, () => Decrypt(stream, new DocumentEnvelopeV2(new EnvelopeV2Header(envelope.Header.Prefix, commitment), envelope.Context, envelope.WrappedSeed)));
    }

    // ---------- E3-M1 acceptance gate: seed scope, concurrency and the document caller contract ----------

    [Fact]
    public async Task ADataKeyGroupSpanningTwoRecordsIsRefusedBeforeAnyUnwrap()
    {
        // A writer that broke the one-record batch rule: one seed, data key and wrap used for two records. H0 carries no record id,
        // so the headers are byte-identical; each item still opens on its own, but the opener refuses the group as a whole.
        var reference = VaultReference(OrganizationA, EncryptionKeyPurpose.FieldValue, 1);
        var keys = new EnvelopeV2KeyDouble();
        keys.Map(OrganizationA, EncryptionKeyPurpose.FieldValue, reference, Wrapped, Seed);
        using var forRecordA = FieldSealBatchV2.CreateDeterministic(OrganizationA, RecordA, EncryptionKeyPurpose.FieldValue, Seed, DataKey, new WrappedDataKey(reference, Wrapped));
        using var forRecordB = FieldSealBatchV2.CreateDeterministic(OrganizationA, RecordB, EncryptionKeyPurpose.FieldValue, Seed, DataKey, new WrappedDataKey(reference, Wrapped));
        var a = forRecordA.Seal(FieldA, ItemA, "String", "a", Seq(0x90, 12));
        var b = forRecordB.Seal(FieldB, ItemB, "String", "b", Seq(0xA0, 12));
        Assert.True(a.Header.SameBytesAs(b.Header));
        Assert.Equal("a", Assert.Single(await Open(keys, a)));
        Assert.Equal("b", Assert.Single(await Open(keys, b)));

        var unwraps = keys.UnwrapCalls;
        await Refused(EnvelopeV2FailureReason.InconsistentDataKeyGroup, () => FieldEnvelopeV2Opener.OpenAsync(keys, OrganizationA, EncryptionKeyPurpose.FieldValue, [new FieldOpenRequestV2(a, a.Identity), new FieldOpenRequestV2(b, b.Identity)], Ct));
        Assert.Equal(unwraps, keys.UnwrapCalls);

        // Moving record A's envelope into record B's request is refused by identity; relabelling it as record B fails the AEAD.
        await Refused(EnvelopeV2FailureReason.IdentityMismatch, () => Open(keys, a, expected: a.Identity with { FormRecordId = RecordB }));
        var relabelled = Rebuild(a, identity: a.Identity with { FormRecordId = RecordB });
        await Refused(EnvelopeV2FailureReason.AuthenticationFailed, () => Open(keys, relabelled));
    }

    [Fact]
    public async Task ConcurrentSealsNeverSealAnItemTwiceAndADisposeNeverRacesADerivation()
    {
        var keys = new EnvelopeV2KeyDouble();
        using var batch = await FieldSealBatchV2.CreateAsync(keys, OrganizationA, RecordA, EncryptionKeyPurpose.FieldValue, Ct);

        // 64 concurrent seals of one item: exactly one succeeds.
        var sameItem = await Task.WhenAll(Enumerable.Range(0, 64).Select(_ => Task.Run<FieldEnvelopeV2?>(() =>
        {
            try
            {
                return batch.Seal(FieldA, ItemA, "String", "once");
            }
            catch (InvalidOperationException)
            {
                return null;
            }
        })));
        var once = Assert.Single(sameItem, e => e is not null)!;

        // 256 concurrent seals of distinct items: all succeed, with distinct nonces.
        var many = await Task.WhenAll(Enumerable.Range(0, 256).Select(_ => Guid.CreateVersion7()).Select(item => Task.Run(() => batch.Seal(FieldB, item, "String", item.ToString("D")))));
        Assert.Equal(many.Length, many.Select(e => Convert.ToHexStringLower(e.Nonce)).Distinct(StringComparer.Ordinal).Count());

        // A dispose racing seals: every seal either completes under the intact key schedule (its envelope opens) or is refused.
        var racing = Enumerable.Range(0, 256).Select(_ => Guid.CreateVersion7()).Select(item => Task.Run<FieldEnvelopeV2?>(() =>
        {
            try
            {
                return batch.Seal(FieldA, item, "String", item.ToString("D"));
            }
            catch (ObjectDisposedException)
            {
                return null;
            }
        })).ToList();
        batch.Dispose();
        var raced = (await Task.WhenAll(racing)).OfType<FieldEnvelopeV2>().ToList();
        Assert.Throws<ObjectDisposedException>(() => batch.Seal(FieldA, Guid.CreateVersion7(), "String", "late"));

        var all = new[] { once }.Concat(many).Concat(raced).ToList();
        var opened = await FieldEnvelopeV2Opener.OpenAsync(keys, OrganizationA, EncryptionKeyPurpose.FieldValue, all.Select(e => new FieldOpenRequestV2(e, e.Identity)).ToList(), Ct);
        Assert.Equal(all.Select(e => e.Identity.FieldValueItemId == ItemA ? "once" : e.Identity.FieldValueItemId.ToString("D")), opened);
        Assert.Equal(1, keys.UnwrapCalls);
    }

    [Fact]
    public async Task ALaterSegmentFailureReleasesOnlyWholeAuthenticatedSegmentsAndNeverSignalsCompletion()
    {
        // Caller contract (DocumentEnvelopeV2Cipher, design §13.9): provisional output is an exact plaintext prefix of whole
        // authenticated segments, never a byte of a failing segment, and the sink is flushed only on success.
        var keys = LocalKeyring();
        var identity = new DocumentVersionIdentity(RecordA, Attachment, 5);
        const int size = EnvelopeV2Format.SegmentSize;
        var plaintext = RandomNumberGenerator.GetBytes((3 * size) + 9); // four segments; the last has 9 bytes
        using var sink = new MemoryStream();
        var envelope = (await DocumentEnvelopeV2Cipher.EncryptAsync(new MemoryStream(plaintext), sink, keys, OrganizationA, identity, Ct)).Envelope;
        var stream = sink.ToArray();
        const int frame = 4 + size + 16;

        async Task<(Exception Error, RecordingSink Output)> Fail(byte[] ciphertext, DocumentEnvelopeV2? e = null, DocumentVersionIdentity? expected = null, IEncryptionKeyProvider? provider = null)
        {
            var output = new RecordingSink();
            var error = await Assert.ThrowsAnyAsync<Exception>(() => DocumentEnvelopeV2Cipher.DecryptAsync(new MemoryStream(ciphertext), output, e ?? envelope, OrganizationA, expected ?? identity, provider ?? keys, Ct));
            Assert.Equal(0, output.Flushes);
            Assert.All(output.Writes, length => Assert.Equal(size, length)); // only whole, full segments precede a failure here
            return (error, output);
        }

        static EnvelopeV2FailureReason Reason(Exception error) => Assert.IsType<EnvelopeV2Exception>(error).Reason;

        // Tampering in segment k (ciphertext, then tag): exactly the first k segments were released.
        for (var k = 0; k < 4; k++)
        {
            foreach (var offset in new[] { (k * frame) + 4 + 2, (k == 3 ? stream.Length : (k + 1) * frame) - 1 })
            {
                var (error, output) = await Fail(With(stream, offset, (byte)(stream[offset] ^ 0x01)));
                Assert.Equal(EnvelopeV2FailureReason.AuthenticationFailed, Reason(error));
                Assert.Equal(plaintext[..(k * size)], output.ToArray());
            }
        }

        // The final segment dropped: segment 2 is authenticated as final and fails, after segments 0 and 1.
        var (dropped, droppedOutput) = await Fail(stream[..(3 * frame)]);
        Assert.Equal(EnvelopeV2FailureReason.AuthenticationFailed, Reason(dropped));
        Assert.Equal(plaintext[..(2 * size)], droppedOutput.ToArray());

        // Truncation inside segment 1 or 3: the segments before it only.
        var (cutOne, cutOneOutput) = await Fail(stream[..(frame + 4 + 100)]);
        Assert.Equal(EnvelopeV2FailureReason.MalformedEnvelope, Reason(cutOne));
        Assert.Equal(plaintext[..size], cutOneOutput.ToArray());
        var (cutThree, cutThreeOutput) = await Fail(stream[..^3]);
        Assert.Equal(EnvelopeV2FailureReason.MalformedEnvelope, Reason(cutThree));
        Assert.Equal(plaintext[..(3 * size)], cutThreeOutput.ToArray());

        // Failures before the first segment write nothing at all: identity, foreign keyring, commitment, empty and short streams.
        var commitment = envelope.Header.KeyCommitment.ToArray();
        commitment[0] ^= 0x01;
        var noOutput = new List<(Exception Error, RecordingSink Output)>
        {
            await Fail(stream, expected: identity with { VersionNumber = 6 }),
            await Fail(stream, provider: LocalKeyring(0x40)),
            await Fail(stream, new DocumentEnvelopeV2(new EnvelopeV2Header(envelope.Header.Prefix, commitment), envelope.Context, envelope.WrappedSeed)),
            await Fail([]),
            await Fail(stream[..3]),
        };
        Assert.All(noOutput, x => Assert.Empty(x.Output.Writes));
        Assert.IsAssignableFrom<CryptographicException>(noOutput[1].Error);

        // Success is the only completion signal: the whole plaintext, then exactly one flush.
        var complete = new RecordingSink();
        await DocumentEnvelopeV2Cipher.DecryptAsync(new MemoryStream(stream), complete, envelope, OrganizationA, identity, keys, Ct);
        Assert.Equal(plaintext, complete.ToArray());
        Assert.Equal(1, complete.Flushes);
    }

    // ---------- Malformed, truncated and unsupported envelopes ----------

    [Fact]
    public void EveryTruncationAndAnyTrailingByteIsRefused()
    {
        var portable = KnownEnvelope().Envelope.ToPortableBytes();
        for (var length = 0; length < portable.Length; length++)
        {
            Assert.Throws<EnvelopeV2Exception>(() => FieldEnvelopeV2.ParsePortable(portable.AsSpan(0, length)));
        }

        RefusedSync(EnvelopeV2FailureReason.MalformedEnvelope, () => FieldEnvelopeV2.ParsePortable([.. portable, 0]));
        RefusedSync(EnvelopeV2FailureReason.MalformedEnvelope, () => FieldEnvelopeV2.ParsePortable([]));
    }

    [Fact]
    public void UnsupportedVersionsKindsAndAlgorithmsAreRefusedAsUnsupported()
    {
        var portable = KnownEnvelope().Envelope.ToPortableBytes();
        RefusedSync(EnvelopeV2FailureReason.UnsupportedEnvelope, () => FieldEnvelopeV2.ParsePortable(With(portable, 0, (byte)'X')));
        RefusedSync(EnvelopeV2FailureReason.UnsupportedEnvelope, () => FieldEnvelopeV2.ParsePortable(With(portable, OffsetVersion, 0x00, 0x01)));
        RefusedSync(EnvelopeV2FailureReason.UnsupportedEnvelope, () => FieldEnvelopeV2.ParsePortable(With(portable, OffsetVersion, 0x00, 0x03)));
        RefusedSync(EnvelopeV2FailureReason.UnsupportedEnvelope, () => FieldEnvelopeV2.ParsePortable(With(portable, OffsetKind, 4)));
        RefusedSync(EnvelopeV2FailureReason.UnsupportedEnvelope, () => FieldEnvelopeV2.ParsePortable(With(portable, OffsetKind, 0)));
        RefusedSync(EnvelopeV2FailureReason.UnsupportedEnvelope, () => FieldEnvelopeV2.ParsePortable(With(portable, OffsetAead, 2)));
        RefusedSync(EnvelopeV2FailureReason.UnsupportedEnvelope, () => FieldEnvelopeV2.ParsePortable(With(portable, OffsetKdf, 2)));
        foreach (byte wrap in new byte[] { 0, 3, 4, 255 })
        {
            RefusedSync(EnvelopeV2FailureReason.UnsupportedEnvelope, () => FieldEnvelopeV2.ParsePortable(With(portable, OffsetWrap, wrap)));
        }

        // A document header is not a field envelope, and the reverse.
        var documentHeader = new DocumentEnvelopeV2(
            new EnvelopeV2Header(EnvelopeV2HeaderPrefix.Create(EnvelopeV2PayloadKind.DocumentVersion, EnvelopeV2WrapAlgorithm.VaultTransitAes256Gcm96, OrganizationA, VaultReference(OrganizationA, EncryptionKeyPurpose.Document, 1), DataKey), new byte[32]),
            new DocumentContext(new DocumentVersionIdentity(RecordA, Attachment, 1), EnvelopeV2Format.SegmentSize, Seq(0, 8)), Wrapped).ToPortableHeaderBytes();
        Assert.Throws<EnvelopeV2Exception>(() => FieldEnvelopeV2.ParsePortable(documentHeader));
        RefusedSync(EnvelopeV2FailureReason.UnsupportedEnvelope, () => DocumentEnvelopeV2.ParsePortableHeader(portable));
        var otherSegmentSize = documentHeader.ToArray();
        BinaryPrimitives.WriteUInt32BigEndian(otherSegmentSize.AsSpan(otherSegmentSize.Length - 2 - 60 - 8 - 4), 65536);
        RefusedSync(EnvelopeV2FailureReason.UnsupportedEnvelope, () => DocumentEnvelopeV2.ParsePortableHeader(otherSegmentSize));
        Assert.Equal(documentHeader, DocumentEnvelopeV2.ParsePortableHeader(documentHeader).ToPortableHeaderBytes());
    }

    [Fact]
    public void MalformedFieldsAreRefusedAsMalformed()
    {
        var portable = KnownEnvelope().Envelope.ToPortableBytes();
        var cases = new Dictionary<string, byte[]>
        {
            ["reserved flags"] = With(portable, OffsetFlags, 1),
            ["empty organization"] = With(portable, OffsetOrganization, new byte[16]),
            ["purpose length zero"] = With(portable, OffsetPurposeLength, 0),
            ["purpose length over bound"] = With(portable, OffsetPurposeLength, 33),
            ["purpose of another kind"] = With(portable, OffsetPurposeLength + 1, "document-xx"u8.ToArray()),
            ["uppercase purpose"] = With(portable, OffsetPurposeLength + 1, (byte)'F'),
            ["non-canonical reference (uppercase organization)"] = With(portable, OffsetReference + 14 + 4, (byte)'B'),
            ["reference separator replaced"] = With(portable, OffsetReference + 62, (byte)'-'),
            ["reference of another organization"] = With(portable, OffsetReference + 14 + 2, (byte)'b'),
            ["reference with a non-ASCII byte"] = With(portable, OffsetReference + 3, 0xC3),
            ["data key not UUIDv7"] = With(portable, OffsetDataKey + 6, 0x40),
            ["data key wrong variant"] = With(portable, OffsetDataKey + 8, 0x00),
            ["empty record"] = With(portable, OffsetRecord, new byte[16]),
            ["logical type outside the set"] = With(portable, OffsetLogicalType, "Strong"u8.ToArray()),
            ["wrapped seed too short"] = With(portable, OffsetWrappedLength, 0x00, 59),
            ["wrapped seed too long"] = With(portable, OffsetWrappedLength, 0x00, 61),
            ["ciphertext length past the end"] = With(portable, OffsetCiphertextLength, 0x00, 0x00, 0x00, 0x10),
            ["ciphertext length over the bound"] = With(portable, OffsetCiphertextLength, 0x00, 0x10, 0x00, 0x01),
        };
        foreach (var (name, bytes) in cases)
        {
            var ex = Assert.Throws<EnvelopeV2Exception>(() => FieldEnvelopeV2.ParsePortable(bytes));
            Assert.True(ex.Reason == EnvelopeV2FailureReason.MalformedEnvelope, name + ": " + ex.Reason);
        }

        Assert.Equal(OffsetCiphertext + 15, portable.Length);

        // Non-canonical references (the E-1/E-2 canonical form is reused unchanged).
        foreach (var reference in new[]
                 {
                     VaultReference(OrganizationA, EncryptionKeyPurpose.FieldValue, 1).Replace(":1", ":01", StringComparison.Ordinal),
                     VaultReference(OrganizationA, EncryptionKeyPurpose.FieldValue, 1).ToUpperInvariant(),
                     VaultReference(OrganizationA, EncryptionKeyPurpose.FieldValue, 1) + ":extra",
                     $"vault-transit:{OrganizationA:N}:field-value:1",
                     $"vault-transit:{OrganizationA:D}:field-value:0",
                     $"vault-transit:{OrganizationA:D}:field-value:" + new string('9', 120),
                 })
        {
            Assert.Throws<EnvelopeV2Exception>(() => EnvelopeV2HeaderPrefix.Create(EnvelopeV2PayloadKind.FieldValueItem, EnvelopeV2WrapAlgorithm.VaultTransitAes256Gcm96, OrganizationA, reference, DataKey));
        }
    }

    [Fact]
    public void ExceptionsNeverCarryEnvelopeContent()
    {
        foreach (var reason in Enum.GetValues<EnvelopeV2FailureReason>())
        {
            var message = new EnvelopeV2Exception(reason).Message;
            Assert.DoesNotContain(OrganizationA.ToString("D"), message, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotMatch("[0-9a-f]{8}", message);
        }
    }

    // ---------- Nonce and data-key uniqueness ----------

    [Fact]
    public async Task NoncesSeedsAndDataKeysAreFreshAndABatchRefusesRepeats()
    {
        var keys = new EnvelopeV2KeyDouble();
        var nonces = new HashSet<string>(StringComparer.Ordinal);
        using (var batch = await FieldSealBatchV2.CreateAsync(keys, OrganizationA, RecordA, EncryptionKeyPurpose.FieldValue, Ct))
        {
            for (var i = 0; i < 2000; i++)
            {
                Assert.True(nonces.Add(Convert.ToHexStringLower(batch.Seal(FieldA, Guid.CreateVersion7(), "String", "same").Nonce)));
            }

            batch.Seal(FieldA, ItemA, "String", "x", Seq(0x01, 12));
            Assert.Throws<InvalidOperationException>(() => batch.Seal(FieldB, ItemB, "String", "y", Seq(0x01, 12)));
        }

        var (_, known) = KnownBatch();
        using (known)
        {
            known.Seal(FieldA, ItemA, "String", "x", Seq(0x10, 12));
            Assert.Throws<InvalidOperationException>(() => known.Seal(FieldA, ItemA, "String", "again", Seq(0x20, 12)));
            Assert.Throws<ArgumentException>(() => known.Seal(FieldA, ItemB, "String", "x", Seq(0x30, 11)));
        }

        Assert.Throws<ObjectDisposedException>(() => known.Seal(FieldB, ItemB, "String", "late"));

        // Two batches for the same record: different data keys, seeds (commitments), wrapped seeds and ciphertexts of equal plaintexts.
        using var first = await FieldSealBatchV2.CreateAsync(keys, OrganizationA, RecordA, EncryptionKeyPurpose.FieldValue, Ct);
        using var second = await FieldSealBatchV2.CreateAsync(keys, OrganizationA, RecordA, EncryptionKeyPurpose.FieldValue, Ct);
        Assert.NotEqual(first.Header.Prefix.DataKeyIdentifier, second.Header.Prefix.DataKeyIdentifier);
        Assert.False(first.Header.KeyCommitment.SequenceEqual(second.Header.KeyCommitment));
        var x = first.Seal(FieldA, ItemA, "String", "equal plaintext");
        var y = second.Seal(FieldA, ItemA, "String", "equal plaintext");
        Assert.False(x.Ciphertext.SequenceEqual(y.Ciphertext));
        Assert.False(x.WrappedSeed.SequenceEqual(y.WrappedSeed));
    }

    [Fact]
    public async Task DocumentNoncePrefixesAndDataKeysAreFreshPerVersion()
    {
        var keys = new EnvelopeV2KeyDouble();
        var identity = new DocumentVersionIdentity(RecordA, Attachment, 1);
        var plaintext = Encoding.UTF8.GetBytes("same document");
        var a = await DocumentEnvelopeV2Cipher.EncryptAsync(new MemoryStream(plaintext), new MemoryStream(), keys, OrganizationA, identity, Ct);
        var b = await DocumentEnvelopeV2Cipher.EncryptAsync(new MemoryStream(plaintext), new MemoryStream(), keys, OrganizationA, identity, Ct);
        Assert.False(a.Envelope.Context.NoncePrefix.SequenceEqual(b.Envelope.Context.NoncePrefix));
        Assert.NotEqual(a.Envelope.Header.Prefix.DataKeyIdentifier, b.Envelope.Header.Prefix.DataKeyIdentifier);
        Assert.NotEqual(a.CiphertextSha256, b.CiphertextSha256);
    }

    // ---------- Domain separation ----------

    [Fact]
    public void KindsLabelsAndContextsDeriveDistinctKeys()
    {
        var p1 = EnvelopeV2HeaderPrefix.Create(EnvelopeV2PayloadKind.FieldValueItem, EnvelopeV2WrapAlgorithm.VaultTransitAes256Gcm96, OrganizationA, VaultReference(OrganizationA, EncryptionKeyPurpose.FieldValue, 1), DataKey).Encode();
        var p2 = EnvelopeV2HeaderPrefix.Create(EnvelopeV2PayloadKind.DocumentVersion, EnvelopeV2WrapAlgorithm.VaultTransitAes256Gcm96, OrganizationA, VaultReference(OrganizationA, EncryptionKeyPurpose.Document, 1), DataKey).Encode();
        var p3 = EnvelopeV2HeaderPrefix.Create(EnvelopeV2PayloadKind.SealedKey, EnvelopeV2WrapAlgorithm.VaultTransitAes256Gcm96, OrganizationA, VaultReference(OrganizationA, EncryptionKeyPurpose.IdempotencyProtection, 1), DataKey).Encode();
        var field = new FieldItemIdentity(RecordA, FieldA, ItemA, "String").Encode();
        var document = new DocumentContext(new DocumentVersionIdentity(RecordA, Attachment, 1), EnvelopeV2Format.SegmentSize, Seq(0, 8)).Encode();
        using var schedule = new EnvelopeV2KeySchedule(Seed);
        var derived = new[]
        {
            schedule.Commitment(p1), schedule.Commitment(p2), schedule.Commitment(p3),
            schedule.FieldItemKey(p1, field), schedule.FieldItemKey(p3, field), schedule.DocumentKey(p2, document),
            schedule.FieldItemKey(p1, document), schedule.DocumentKey(p1, field), schedule.FieldItemKey(p1, new FieldItemIdentity(RecordA, FieldA, ItemB, "String").Encode()),
        }.Select(Convert.ToHexStringLower).ToList();
        Assert.Equal(derived.Count, derived.Distinct(StringComparer.Ordinal).Count());
        Assert.DoesNotContain(Convert.ToHexStringLower(Seed), derived);

        // The labels are distinct and none is a prefix of another, so label ‖ H0 ‖ context is unambiguous.
        var labels = new[] { EnvelopeV2Format.ExtractSalt.ToArray(), EnvelopeV2Format.CommitmentLabel.ToArray(), EnvelopeV2Format.FieldItemLabel.ToArray(), EnvelopeV2Format.DocumentLabel.ToArray() };
        foreach (var a in labels)
        {
            foreach (var b in labels.Where(l => !ReferenceEquals(l, a)))
            {
                Assert.False(b.AsSpan().StartsWith(a), Encoding.ASCII.GetString(a) + " prefixes " + Encoding.ASCII.GetString(b));
            }
        }

        schedule.Dispose();
        Assert.Throws<ObjectDisposedException>(() => schedule.Commitment(p1));
    }

    [Fact]
    public async Task BatchesRefuseForeignPurposesRecordsAndReferences()
    {
        var keys = new EnvelopeV2KeyDouble();
        await Assert.ThrowsAsync<ArgumentException>(() => FieldSealBatchV2.CreateAsync(keys, OrganizationA, RecordA, EncryptionKeyPurpose.Document, Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => FieldSealBatchV2.CreateAsync(keys, OrganizationA, RecordA, EncryptionKeyPurpose.AuditorReasonNote, Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => FieldSealBatchV2.CreateAsync(keys, OrganizationA, Guid.Empty, EncryptionKeyPurpose.FieldValue, Ct));
        Assert.Equal(0, keys.WrapCalls);

        // A provider answering with a reference for another organization, purpose or family: refused before anything is sealed.
        foreach (var reference in new[]
                 {
                     VaultReference(OrganizationB, EncryptionKeyPurpose.FieldValue, 1), VaultReference(OrganizationA, EncryptionKeyPurpose.Document, 1),
                     "kms:" + OrganizationA.ToString("D") + ":field-value:1", "local-dev-kek-v1",
                 })
        {
            Assert.Throws<EnvelopeV2Exception>(() => FieldSealBatchV2.CreateDeterministic(OrganizationA, RecordA, EncryptionKeyPurpose.FieldValue, Seed, DataKey, new WrappedDataKey(reference, Wrapped)));
        }

        RefusedSync(EnvelopeV2FailureReason.MalformedEnvelope, () => FieldSealBatchV2.CreateDeterministic(OrganizationA, RecordA, EncryptionKeyPurpose.FieldValue, Seed, DataKey, new WrappedDataKey(VaultReference(OrganizationA, EncryptionKeyPurpose.FieldValue, 1), Seq(0, 59))));
        RefusedSync(EnvelopeV2FailureReason.MalformedEnvelope, () => FieldSealBatchV2.CreateDeterministic(OrganizationA, RecordA, EncryptionKeyPurpose.FieldValue, Seed, Guid.NewGuid(), new WrappedDataKey(VaultReference(OrganizationA, EncryptionKeyPurpose.FieldValue, 1), Wrapped)));
        RefusedSync(EnvelopeV2FailureReason.InvalidDataKey, () => FieldSealBatchV2.CreateDeterministic(OrganizationA, RecordA, EncryptionKeyPurpose.FieldValue, Seq(0, 16), DataKey, new WrappedDataKey(VaultReference(OrganizationA, EncryptionKeyPurpose.FieldValue, 1), Wrapped)));
    }

    // ---------- Legacy envelope isolation ----------

    [Fact]
    public async Task VersionOneAndVersionTwoNeverOpenEachOther()
    {
        // v1 never accepts a v2-sealed row: the v1 service refuses envelope version 2 (unchanged v1 code, read-only use).
        var keys = LocalKeyring();
        using var batch = await FieldSealBatchV2.CreateAsync(keys, OrganizationA, RecordA, EncryptionKeyPurpose.FieldValue, Ct);
        var v2 = batch.Seal(FieldA, ItemA, "String", "v2 secret");
        var asV1Row = new FieldEncryptedValue
        {
            FieldEncryptedValueId = Guid.CreateVersion7(),
            TenantId = OrganizationA,
            FormRecordId = RecordA,
            FieldDefinitionId = FieldA,
            FieldValueItemId = ItemA,
            EncryptedValueBytes = v2.Ciphertext.ToArray(),
            EncryptionNonceBytes = v2.Nonce.ToArray(),
            EncryptionAuthTagBytes = v2.Tag.ToArray(),
            WrappedDataKeyBytes = v2.WrappedSeed.ToArray(),
            EncryptionAlgorithmName = AesGcmFieldEncryptionService.AlgorithmName,
            EncryptionKeyReference = v2.Header.Prefix.KekReference,
            EncryptionEnvelopeVersion = 2,
            EncryptedLogicalType = "String",
        };
        var v1 = new AesGcmFieldEncryptionService(keys);
        await Assert.ThrowsAnyAsync<CryptographicException>(() => v1.DecryptAsync(asV1Row, EncryptionKeyPurpose.FieldValue, Ct));

        // Even relabelled as envelope version 1, the v1 decryptor unwraps the seed but its v1 AAD and DEK use cannot authenticate v2 ciphertext.
        asV1Row.EncryptionEnvelopeVersion = 1;
        await Assert.ThrowsAnyAsync<CryptographicException>(() => v1.DecryptAsync(asV1Row, EncryptionKeyPurpose.FieldValue, Ct));

        // v2 never accepts v1 material: a v1 envelope's bytes or AAD are not a v2 envelope, and the legacy reference names no v2 family.
        var sealedV1 = await v1.EncryptAsync(new FieldEncryptionContext(OrganizationA, RecordA, FieldA, ItemA, "String"), "v1 secret", EncryptionKeyPurpose.FieldValue, Ct);
        RefusedSync(EnvelopeV2FailureReason.UnsupportedEnvelope, () => FieldEnvelopeV2.ParsePortable(AesGcmFieldEncryptionService.Aad(new FieldEncryptionContext(OrganizationA, RecordA, FieldA, ItemA, "String"))));
        RefusedSync(EnvelopeV2FailureReason.UnsupportedEnvelope, () => FieldEnvelopeV2.ParsePortable([.. sealedV1.EncryptionNonceBytes, .. sealedV1.EncryptedValueBytes, .. sealedV1.EncryptionAuthTagBytes]));
        RefusedSync(EnvelopeV2FailureReason.UnsupportedEnvelope, () => EnvelopeV2Format.WrapAlgorithmOf("local-dev-kek-v1"));

        // A v1 row's wrapped per-value DEK, presented as a v2 batch seed with v1's ciphertext, fails the commitment.
        var p = v2.Header.Prefix;
        var forged = new FieldEnvelopeV2(new EnvelopeV2Header(EnvelopeV2HeaderPrefix.Create(p.Kind, p.WrapAlgorithm, OrganizationA, sealedV1.EncryptionKeyReference, p.DataKeyIdentifier), v2.Header.KeyCommitment),
            v2.Identity, sealedV1.WrappedDataKeyBytes, sealedV1.EncryptionNonceBytes, sealedV1.EncryptionAuthTagBytes, sealedV1.EncryptedValueBytes);
        await Refused(EnvelopeV2FailureReason.KeyCommitmentMismatch, () => Open(keys, forged));

        // The v1 service and its vectors are untouched by v2 (EncryptionKnownAnswerTests pins them); v1 still round-trips here.
        Assert.Equal("v1 secret", await v1.DecryptAsync(sealedV1, EncryptionKeyPurpose.FieldValue, Ct));
    }
}
