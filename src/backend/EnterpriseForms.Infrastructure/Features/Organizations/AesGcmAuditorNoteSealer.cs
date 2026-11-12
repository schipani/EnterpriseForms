using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using EnterpriseForms.Application.Features.Documents.Ports;
using EnterpriseForms.Application.Features.Organizations.ReasonNotes;

namespace EnterpriseForms.Infrastructure.Features.Organizations;

/// <summary>
/// C8 M4 M1 (Design A revision 2, A-Q2 approved in C-117): seals a Security Auditor reason note. The identical construction as
/// <c>AesGcmFieldEncryptionService</c> — AES-256-GCM, a random 256-bit single-use DEK wrapped by the configured
/// <see cref="IEncryptionKeyProvider"/>, a random 96-bit nonce, a 128-bit tag, DEK and plaintext buffers zeroed — with its own
/// authenticated context:
/// AAD = "DynamicForms/AuditorReasonNote/v1" ‖ Int32BE(1) ‖ organization ‖ target ‖ note id (16-byte big-endian UUIDs) ‖
/// Int32BE(len) ‖ role ‖ Int32BE(len) ‖ direction, over the fixed 2,048-byte padded plaintext UInt16BE(L) ‖ UTF-8 ‖ zeros.
/// Seal only: nothing in the product decrypts a note (D8-73). Not production-ready until production key providers and
/// rotation exist (D8-76 item 8).
/// </summary>
public sealed class AesGcmAuditorNoteSealer(IEncryptionKeyProvider keys) : IAuditorNoteSealer
{
    private const int TagSize = 16;
    private const int NonceSize = 12;
    private static readonly byte[] Domain = Encoding.ASCII.GetBytes(AuditorReasonNoteFormat.AadDomain);

    public bool IsAvailable => keys.IsConfigured;

    public AuditorReasonNoteEnvelope Seal(AuditorReasonNoteContext context, string normalizedNote)
    {
        var dek = RandomNumberGenerator.GetBytes(32);
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        try
        {
            return Seal(context, normalizedNote, dek, nonce);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(dek);
        }
    }

    /// <summary>Deterministic core for the known-answer vector KAT-M1-01 (tests only through InternalsVisibleTo).</summary>
    internal AuditorReasonNoteEnvelope Seal(AuditorReasonNoteContext context, string normalizedNote, byte[] dek, byte[] nonce)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(normalizedNote);
        if (!keys.IsConfigured)
        {
            throw new EncryptionUnavailableException("no encryption key provider is configured; reason notes cannot be sealed");
        }

        var plaintext = AuditorReasonNoteFormat.Pad(normalizedNote);
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[TagSize];
        try
        {
            var keyReference = keys.CurrentKeyReference;
            var wrapped = keys.WrapDataKey(dek, keyReference);
            using (var aes = new AesGcm(dek, TagSize))
            {
                aes.Encrypt(nonce, plaintext, ciphertext, tag, Aad(context));
            }

            return new AuditorReasonNoteEnvelope(
                AuditorReasonNoteFormat.EnvelopeFormat,
                AuditorReasonNoteFormat.EnvelopeVersion,
                AuditorReasonNoteFormat.AlgorithmName,
                keyReference,
                context.NoteId.ToString("D"),
                Convert.ToBase64String(nonce),
                Convert.ToBase64String(ciphertext),
                Convert.ToBase64String(tag),
                Convert.ToBase64String(wrapped));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    /// <summary>The exact §1.2 AAD (public to tests through InternalsVisibleTo for the known-answer vector).</summary>
    internal static byte[] Aad(AuditorReasonNoteContext c)
    {
        var role = Encoding.ASCII.GetBytes(c.RoleName);
        var direction = Encoding.ASCII.GetBytes(c.Direction);
        var aad = new byte[Domain.Length + 4 + (16 * 3) + 4 + role.Length + 4 + direction.Length];
        var offset = 0;
        Domain.CopyTo(aad, offset);
        offset += Domain.Length;
        BinaryPrimitives.WriteInt32BigEndian(aad.AsSpan(offset, 4), AuditorReasonNoteFormat.EnvelopeVersion);
        offset += 4;
        foreach (var id in new[] { c.OrganizationId, c.TargetAuthorityUserId, c.NoteId })
        {
            id.TryWriteBytes(aad.AsSpan(offset, 16), bigEndian: true, out _);
            offset += 16;
        }

        BinaryPrimitives.WriteInt32BigEndian(aad.AsSpan(offset, 4), role.Length);
        offset += 4;
        role.CopyTo(aad, offset);
        offset += role.Length;
        BinaryPrimitives.WriteInt32BigEndian(aad.AsSpan(offset, 4), direction.Length);
        offset += 4;
        direction.CopyTo(aad, offset);
        return aad;
    }
}
