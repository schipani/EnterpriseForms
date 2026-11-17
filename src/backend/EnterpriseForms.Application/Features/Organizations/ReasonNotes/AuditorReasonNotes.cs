using System.Buffers.Binary;
using System.Text;

namespace EnterpriseForms.Application.Features.Organizations.ReasonNotes;

/// <summary>C8 M4 M1 (Design A §1.2): the authenticated context of one Security Auditor reason note. Never taken from an envelope.</summary>
public sealed record AuditorReasonNoteContext(Guid OrganizationId, Guid TargetAuthorityUserId, string RoleName, string Direction, Guid NoteId);

/// <summary>Design A §1.4: the sealed note as the Authority receives and stores it (ciphertext only; the Authority holds no key).</summary>
public sealed record AuditorReasonNoteEnvelope(
    string EnvelopeFormat,
    int EnvelopeVersion,
    string AlgorithmName,
    string KeyReference,
    string NoteId,
    string NonceB64,
    string CiphertextB64,
    string TagB64,
    string WrappedDataKeyB64);

/// <summary>
/// Seal-only port (D8-73: there is no decrypt operation anywhere in the product). <see cref="IsAvailable"/> is false when no
/// permitted key provider is configured: a note is then refused and nothing is sent.
/// </summary>
public interface IAuditorNoteSealer
{
    bool IsAvailable { get; }

    Task<AuditorReasonNoteEnvelope> SealAsync(AuditorReasonNoteContext context, string normalizedNote, CancellationToken cancellationToken);
}

/// <summary>Design A §1.2–§1.3, §1.6 B2: constants, note validation and the fixed, authenticated padded plaintext encoding.</summary>
public static class AuditorReasonNoteFormat
{
    public const string AadDomain = "DynamicForms/AuditorReasonNote/v1";
    public const string EnvelopeFormat = "DynamicForms/AuditorReasonNote";
    public const int EnvelopeVersion = 1;
    public const string AlgorithmName = "AES-256-GCM";
    public const string RoleName = "security_auditor";
    public const string Appoint = "appoint";
    public const string Remove = "remove";
    public const int MaximumUtf16Units = 500;
    public const int MaximumUtf8Bytes = 1500;
    public const int PaddedLength = 2048;

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>
    /// B2: <c>Trim()</c>, then NFC; 1–500 UTF-16 units; strict UTF-8 (no lone surrogates); no C0/C1 control characters except LF.
    /// Returns null for a note that is absent or blank; throws <see cref="ArgumentException"/> (never echoing the value) otherwise.
    /// </summary>
    public static string? Normalize(string? note)
    {
        if (string.IsNullOrWhiteSpace(note))
        {
            return null;
        }

        string normalized;
        try
        {
            normalized = note.Trim().Normalize(NormalizationForm.FormC);
        }
        catch (ArgumentException)
        {
            throw new ArgumentException("a reason note must be valid Unicode text");
        }

        if (normalized.Length > MaximumUtf16Units)
        {
            throw new ArgumentException($"a reason note is at most {MaximumUtf16Units} characters");
        }

        foreach (var ch in normalized)
        {
            if (ch != '\n' && char.IsControl(ch)) // C0 and C1 (U+0000–U+001F, U+007F–U+009F)
            {
                throw new ArgumentException("a reason note may not contain control characters other than line breaks");
            }
        }

        try
        {
            if (StrictUtf8.GetByteCount(normalized) > MaximumUtf8Bytes)
            {
                throw new ArgumentException($"a reason note is at most {MaximumUtf8Bytes} UTF-8 bytes");
            }
        }
        catch (EncoderFallbackException)
        {
            throw new ArgumentException("a reason note must be valid Unicode text");
        }

        return normalized;
    }

    /// <summary>§1.3: <c>UInt16BE(L) ‖ UTF-8 ‖ 0x00 × (2,046 − L)</c>, exactly 2,048 bytes; authenticated as the GCM plaintext.</summary>
    public static byte[] Pad(string normalizedNote)
    {
        ArgumentNullException.ThrowIfNull(normalizedNote);
        var utf8 = StrictUtf8.GetBytes(normalizedNote);
        try
        {
            if (utf8.Length is < 1 or > MaximumUtf8Bytes)
            {
                throw new ArgumentException("a reason note must be 1..1500 UTF-8 bytes");
            }

            var padded = new byte[PaddedLength];
            BinaryPrimitives.WriteUInt16BigEndian(padded.AsSpan(0, 2), (ushort)utf8.Length);
            utf8.CopyTo(padded, 2);
            return padded;
        }
        finally
        {
            Array.Clear(utf8);
        }
    }
}
