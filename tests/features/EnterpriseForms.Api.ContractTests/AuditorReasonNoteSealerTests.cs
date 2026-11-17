using System.Buffers.Binary;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using EnterpriseForms.Application.Features.Documents.Ports;
using EnterpriseForms.Application.Features.FormRecords.Encryption;
using EnterpriseForms.Application.Features.Organizations.ReasonNotes;
using EnterpriseForms.Infrastructure.Features.Documents;
using EnterpriseForms.Infrastructure.Features.FormRecords;
using EnterpriseForms.Infrastructure.Features.Organizations;
using Microsoft.Extensions.Options;
using Xunit;

namespace EnterpriseForms.Api.ContractTests;

/// <summary>
/// C8 M4 M1 (Design A revision 2, A-Q2 approved in C-117) tests A-T01…A-T06 and A-T12 for the seal-only Security Auditor
/// reason-note sealer. Decryption exists only in <see cref="TestOnlyOpener"/> here, to prove the confidentiality and binding
/// properties; the product has no opener (D8-73). KAT-M1-01 was independently reproduced with Python <c>cryptography</c>
/// (docs/evidence/phase-c8/m4-1-protocol-verification/m1kat-*).
/// </summary>
public sealed class AuditorReasonNoteSealerTests
{
    private static readonly Guid Org = Guid.Parse("019a5b2c-1111-7000-8000-000000000001");
    private static readonly Guid Target = Guid.Parse("019a5b2c-2222-7000-8000-000000000002");
    private static readonly Guid NoteId = Guid.Parse("019a5b2c-3333-7000-8000-000000000003");
    private const string KatNote = "Covers the Q4 audit cycle.\nContact: Léa";

    private static byte[] Seq(int start, int count) => Enumerable.Range(start, count).Select(i => (byte)i).ToArray();

    private static LocalDevelopmentKeyProvider Keys(byte[]? kek = null) =>
        new(Options.Create(new EncryptionOptions { KeyProvider = "LocalDevelopment", LocalDevelopmentKekBase64 = Convert.ToBase64String(kek ?? RandomNumberGenerator.GetBytes(32)), LocalDevelopmentKeyReference = "local-dev-kek-v1" }));

    private static AuditorReasonNoteContext Context(Guid? org = null, Guid? target = null, string role = AuditorReasonNoteFormat.RoleName, string direction = AuditorReasonNoteFormat.Appoint, Guid? noteId = null) =>
        new(org ?? Org, target ?? Target, role, direction, noteId ?? NoteId);

    /// <summary>Test-only decryption with every Design A §1.3 decoding rule; never part of the product.</summary>
    private static class TestOnlyOpener
    {
        public static async Task<string> OpenAsync(LocalDevelopmentKeyProvider keys, AuditorReasonNoteContext context, AuditorReasonNoteEnvelope envelope)
        {
            var dek = await keys.UnwrapDataKeyAsync(new DataKeyScope(context.OrganizationId, EncryptionKeyPurpose.AuditorReasonNote), envelope.KeyReference, Convert.FromBase64String(envelope.WrappedDataKeyB64), TestContext.Current.CancellationToken);
            var ciphertext = Convert.FromBase64String(envelope.CiphertextB64);
            var plaintext = new byte[ciphertext.Length];
            using (var aes = new AesGcm(dek, 16))
            {
                aes.Decrypt(Convert.FromBase64String(envelope.NonceB64), ciphertext, Convert.FromBase64String(envelope.TagB64), plaintext, AesGcmAuditorNoteSealer.Aad(context));
            }

            return Unpad(plaintext);
        }

        public static string Unpad(byte[] padded)
        {
            if (padded.Length != AuditorReasonNoteFormat.PaddedLength)
            {
                throw new FormatException("padded length");
            }

            var length = BinaryPrimitives.ReadUInt16BigEndian(padded.AsSpan(0, 2));
            if (length is < 1 or > AuditorReasonNoteFormat.MaximumUtf8Bytes || padded.AsSpan(2 + length).ContainsAnyExcept((byte)0))
            {
                throw new FormatException("length or padding");
            }

            var note = new UTF8Encoding(false, true).GetString(padded, 2, length);
            if (!string.Equals(note, AuditorReasonNoteFormat.Normalize(note), StringComparison.Ordinal))
            {
                throw new FormatException("not normalized");
            }

            return note;
        }
    }

    /// <summary>
    /// A-T01: KAT-M1-01 reproduced exactly (AAD, ciphertext, tag), and the key provider unwraps the vector's wrapped DEK. E-1: a
    /// new note's DEK is wrapped under the organization's AuditorReasonNote key (scoped reference); the vector's legacy-wrapped
    /// DEK still opens through the legacy reference (E1-5).
    /// </summary>
    [Fact]
    public async Task TheKnownAnswerVectorIsReproducedExactly()
    {
        var kek = Seq(0x00, 32);
        var dek = Seq(0x20, 32);
        var nonce = Seq(0x50, 12);
        var keys = Keys(kek);
        var note = AuditorReasonNoteFormat.Normalize(KatNote)!;
        Assert.Equal("436f7665727320746865205134206175646974206379636c652e0a436f6e746163743a204cc3a961", Convert.ToHexStringLower(Encoding.UTF8.GetBytes(note)));
        Assert.Equal(
            "44796e616d6963466f726d732f41756469746f72526561736f6e4e6f74652f763100000001019a5b2c111170008000000000000001019a5b2c222270008000000000000002019a5b2c3333700080000000000000030000001073656375726974795f61756469746f72000000076170706f696e74",
            Convert.ToHexStringLower(AesGcmAuditorNoteSealer.Aad(Context())));
        Assert.Equal("53e210f6271cb59265dc59875cd3b07859def1770cb3b0e0d26cdf7cc80c38a6", Convert.ToHexStringLower(SHA256.HashData(AuditorReasonNoteFormat.Pad(note))));

        var envelope = await new AesGcmAuditorNoteSealer(keys).SealAsync(Context(), note, (byte[])dek.Clone(), nonce, TestContext.Current.CancellationToken);
        Assert.Equal("d3df0ab93fbf1a159fb9df7debf956863bbdf9c7181390379989552871841d11", Convert.ToHexStringLower(SHA256.HashData(Convert.FromBase64String(envelope.CiphertextB64))));
        Assert.Equal("c88d22db2a5e2bb5434a1c52d30de413", Convert.ToHexStringLower(Convert.FromBase64String(envelope.TagB64)));
        Assert.Equal(("DynamicForms/AuditorReasonNote", 1, "AES-256-GCM", "localdev:019a5b2c-1111-7000-8000-000000000001:auditor-reason-note:1", NoteId.ToString("D")), (envelope.EnvelopeFormat, envelope.EnvelopeVersion, envelope.AlgorithmName, envelope.KeyReference, envelope.NoteId));
        var scope = new DataKeyScope(Org, EncryptionKeyPurpose.AuditorReasonNote);
        Assert.Equal(dek, await keys.UnwrapDataKeyAsync(scope, envelope.KeyReference, Convert.FromBase64String(envelope.WrappedDataKeyB64), TestContext.Current.CancellationToken));
        var katWrapped = Convert.FromHexString("404142434445464748494a4bc2988c000219a124e5ed3d1db7493d7449f7606f830039144f7e74deab2ed3e405e2a68bd43d7c6116749b14f108ed35");
        Assert.Equal(dek, await keys.UnwrapDataKeyAsync(scope, "local-dev-kek-v1", katWrapped, TestContext.Current.CancellationToken)); // the vector's legacy wrap still opens (E1-5)
        Assert.Equal(note, await TestOnlyOpener.OpenAsync(keys, Context(), envelope));
    }

    /// <summary>A-T02/A-T03: random round trip; every changed context value fails the tag.</summary>
    [Fact]
    public async Task ANoteOpensOnlyUnderItsExactContext()
    {
        var keys = Keys();
        var sealer = new AesGcmAuditorNoteSealer(keys);
        var envelope = await sealer.SealAsync(Context(), "Covering the Q4 review", TestContext.Current.CancellationToken);
        Assert.Equal(2048, Convert.FromBase64String(envelope.CiphertextB64).Length); // length hidden
        Assert.Equal("Covering the Q4 review", await TestOnlyOpener.OpenAsync(keys, Context(), envelope));
        foreach (var other in new[]
                 {
                     Context(org: Guid.CreateVersion7()), Context(target: Guid.CreateVersion7()), Context(role: "system_administrator"),
                     Context(direction: AuditorReasonNoteFormat.Remove), Context(noteId: Guid.CreateVersion7()),
                 })
        {
            await Assert.ThrowsAnyAsync<CryptographicException>(() => TestOnlyOpener.OpenAsync(keys, other, envelope));
        }

        var second = await sealer.SealAsync(Context(), "Covering the Q4 review", TestContext.Current.CancellationToken);
        Assert.NotEqual(envelope.NonceB64, second.NonceB64);
        Assert.NotEqual(envelope.CiphertextB64, second.CiphertextB64);
    }

    /// <summary>A-T04: tampered ciphertext, tag, nonce or wrapped DEK, and truncation, all fail.</summary>
    [Fact]
    public async Task TamperingFails()
    {
        var keys = Keys();
        var envelope = await new AesGcmAuditorNoteSealer(keys).SealAsync(Context(), "tamper target", TestContext.Current.CancellationToken);
        static string Flip(string b64, int index = 0)
        {
            var bytes = Convert.FromBase64String(b64);
            bytes[index] ^= 0x01;
            return Convert.ToBase64String(bytes);
        }

        foreach (var tampered in new[]
                 {
                     envelope with { CiphertextB64 = Flip(envelope.CiphertextB64, 100) },
                     envelope with { TagB64 = Flip(envelope.TagB64) },
                     envelope with { NonceB64 = Flip(envelope.NonceB64) },
                     envelope with { CiphertextB64 = Convert.ToBase64String(Convert.FromBase64String(envelope.CiphertextB64)[..2047]) },
                 })
        {
            await Assert.ThrowsAnyAsync<CryptographicException>(() => TestOnlyOpener.OpenAsync(keys, Context(), tampered));
        }

        await Assert.ThrowsAnyAsync<CryptographicException>(() => TestOnlyOpener.OpenAsync(keys, Context(), envelope with { WrappedDataKeyB64 = Flip(envelope.WrappedDataKeyB64, 20) }));
    }

    /// <summary>A-T05: field-value and note ciphertexts cannot be opened as one another (same KEK, identifiers mapped across). E-1: their keys are also purpose-separated.</summary>
    [Fact]
    public async Task FieldValueAndNoteDomainsAreSeparate()
    {
        var keys = Keys();
        var field = new AesGcmFieldEncryptionService(keys);
        var fieldContext = new FieldEncryptionContext(Org, Target, NoteId, Guid.Empty, "AuditorReasonNote:security_auditor:appoint");
        var fieldValue = await field.EncryptAsync(fieldContext, "Covering the Q4 review", EncryptionKeyPurpose.FieldValue, TestContext.Current.CancellationToken);
        var asNote = new AuditorReasonNoteEnvelope("DynamicForms/AuditorReasonNote", 1, "AES-256-GCM", fieldValue.EncryptionKeyReference, NoteId.ToString("D"),
            Convert.ToBase64String(fieldValue.EncryptionNonceBytes), Convert.ToBase64String(fieldValue.EncryptedValueBytes), Convert.ToBase64String(fieldValue.EncryptionAuthTagBytes), Convert.ToBase64String(fieldValue.WrappedDataKeyBytes));
        await Assert.ThrowsAnyAsync<CryptographicException>(() => TestOnlyOpener.OpenAsync(keys, Context(), asNote));

        var note = await new AesGcmAuditorNoteSealer(keys).SealAsync(Context(), "Covering the Q4 review", TestContext.Current.CancellationToken);
        fieldValue.EncryptedValueBytes = Convert.FromBase64String(note.CiphertextB64);
        fieldValue.EncryptionNonceBytes = Convert.FromBase64String(note.NonceB64);
        fieldValue.EncryptionAuthTagBytes = Convert.FromBase64String(note.TagB64);
        fieldValue.WrappedDataKeyBytes = Convert.FromBase64String(note.WrappedDataKeyB64);
        await Assert.ThrowsAnyAsync<CryptographicException>(() => field.DecryptAsync(fieldValue, EncryptionKeyPurpose.FieldValue, TestContext.Current.CancellationToken));
    }

    /// <summary>A-T02 decoding rules and A-T06 validation rules.</summary>
    [Fact]
    public void PaddingAndNoteRulesAreExact()
    {
        var padded = AuditorReasonNoteFormat.Pad("abc");
        Assert.Equal(2048, padded.Length);
        Assert.Equal("abc", TestOnlyOpener.Unpad(padded));
        var nonZeroPad = (byte[])padded.Clone();
        nonZeroPad[2047] = 1;
        Assert.Throws<FormatException>(() => TestOnlyOpener.Unpad(nonZeroPad));
        var badLength = (byte[])padded.Clone();
        BinaryPrimitives.WriteUInt16BigEndian(badLength, 1501);
        Assert.Throws<FormatException>(() => TestOnlyOpener.Unpad(badLength));
        var notNfc = AuditorReasonNoteFormat.Pad("x");
        var decomposed = Encoding.UTF8.GetBytes("Léa");
        BinaryPrimitives.WriteUInt16BigEndian(notNfc, (ushort)decomposed.Length);
        decomposed.CopyTo(notNfc, 2);
        Assert.Throws<FormatException>(() => TestOnlyOpener.Unpad(notNfc));

        Assert.Null(AuditorReasonNoteFormat.Normalize(null));
        Assert.Null(AuditorReasonNoteFormat.Normalize("   "));
        Assert.Equal("Léa", AuditorReasonNoteFormat.Normalize("  Léa \n"));
        Assert.Equal("line one\nline two", AuditorReasonNoteFormat.Normalize("line one\nline two"));
        var maximal = new string('€', 500); // 500 three-byte characters = 1,500 UTF-8 bytes
        Assert.Equal(1500, Encoding.UTF8.GetByteCount(AuditorReasonNoteFormat.Normalize(maximal)!));
        Assert.Equal(maximal, TestOnlyOpener.Unpad(AuditorReasonNoteFormat.Pad(maximal)));
        foreach (var invalid in new[] { new string('a', 501), "tab\there", "bell\u0007", "c1\u0085inside", "lone \ud800 surrogate" })
        {
            var refused = Assert.Throws<ArgumentException>(() => AuditorReasonNoteFormat.Normalize(invalid));
            Assert.DoesNotContain(invalid.Trim(), refused.Message, StringComparison.Ordinal); // never echoed
        }
    }

    /// <summary>B3: without a key provider a note cannot be sealed.</summary>
    [Fact]
    public async Task WithoutAKeyProviderNothingIsSealed()
    {
        var sealer = new AesGcmAuditorNoteSealer(new UnavailableKeyProvider("test: no provider"));
        Assert.False(sealer.IsAvailable);
        await Assert.ThrowsAsync<EncryptionUnavailableException>(() => sealer.SealAsync(Context(), "note", TestContext.Current.CancellationToken));
    }

    /// <summary>A-T12: the port is seal-only and no product type opens a note.</summary>
    [Fact]
    public void ThereIsNoNoteDecryptionInTheProduct()
    {
        Assert.Equal(["SealAsync", "get_IsAvailable"], typeof(IAuditorNoteSealer).GetMethods().Select(m => m.Name).Order(StringComparer.Ordinal).ToArray());
        foreach (var assembly in new[] { typeof(IAuditorNoteSealer).Assembly, typeof(AesGcmAuditorNoteSealer).Assembly })
        {
            foreach (var type in assembly.GetTypes().Where(t => t.Name.Contains("ReasonNote", StringComparison.Ordinal) || t.Name.Contains("NoteSealer", StringComparison.Ordinal)))
            {
                Assert.DoesNotContain(type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly), m => m.Name.Contains("Open", StringComparison.Ordinal) || m.Name.Contains("Decrypt", StringComparison.Ordinal) || m.Name.Contains("Unpad", StringComparison.Ordinal));
            }
        }
    }
}
