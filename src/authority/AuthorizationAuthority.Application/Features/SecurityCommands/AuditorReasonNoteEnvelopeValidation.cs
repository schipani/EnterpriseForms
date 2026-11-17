using System.Text.Json;
using System.Text.RegularExpressions;
using AuthorizationAuthority.Application.Common;
using AuthorizationAuthority.Contracts.V1;

namespace AuthorizationAuthority.Application.Features.SecurityCommands;

/// <summary>
/// C8 M4 M1 (Design A revision 2 §1.5, A-Q2 approved in C-117): structural validation of a sealed Security Auditor reason note,
/// before anything is persisted. The Authority holds no key and cannot decrypt; the authenticated context (organization, target,
/// role, direction) is taken from the command, never from the envelope. Plaintext notes stay refused permanently (V2).
/// Every refusal is <c>INVALID_COMMAND</c> with a fixed detail that never echoes envelope values.
/// </summary>
public static partial class AuditorReasonNoteEnvelopeValidation
{
    public const string EnvelopeFormat = "DynamicForms/AuditorReasonNote";
    public const int EnvelopeVersion = 1;
    public const string AlgorithmName = "AES-256-GCM";
    public const int CiphertextBytes = 2048;
    public const int MaximumEnvelopeJsonBytes = 4096;
    public const int MaximumPayloadJsonBytes = 8192;

    [GeneratedRegex("^[A-Za-z0-9._:/-]{1,128}$")]
    private static partial Regex KeyReference();

    [GeneratedRegex("^[0-9a-f]{8}-[0-9a-f]{4}-7[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$")]
    private static partial Regex CanonicalUuidV7();

    /// <summary>
    /// V2 on the raw body: a plaintext note is refused permanently (D8-72). V3 (amended in C-117, approved by the owner in C-118): <c>reason_note_envelope</c> is
    /// absent, null or an object; an explicit null means no note, exactly like a null <c>reason_note</c>, because the Authority
    /// contract serializer writes nulls (refusing them would refuse every noteless command built from the contract record).
    /// </summary>
    public static void RequireNoPlaintextNote(JsonElement payload)
    {
        if (payload.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        if (payload.TryGetProperty("reason_note", out var plaintext) && plaintext.ValueKind != JsonValueKind.Null)
        {
            throw new AuthorityException(AuthorityErrorCodes.ReasonNoteNotAccepted, 422, false, "plaintext Security Auditor reason notes are never accepted (D8-72); notes must be sealed by the business API");
        }

        if (payload.TryGetProperty("reason_note_envelope", out var envelope) && envelope.ValueKind is not (JsonValueKind.Null or JsonValueKind.Object))
        {
            throw AuthorityException.Invalid("reason_note_envelope must be absent, null or an envelope object");
        }

        if (payload.GetRawText().Length > MaximumPayloadJsonBytes)
        {
            throw AuthorityException.Invalid("the command payload is too large");
        }
    }

    /// <summary>V4–V9: the exact envelope shape and sizes. Returns the validated note id.</summary>
    public static Guid Validate(AuditorReasonNoteEnvelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        if (CanonicalJson.Serialize(envelope).Length > MaximumEnvelopeJsonBytes)
        {
            throw Refused();
        }

        if (!string.Equals(envelope.EnvelopeFormat, EnvelopeFormat, StringComparison.Ordinal) || envelope.EnvelopeVersion != EnvelopeVersion || !string.Equals(envelope.AlgorithmName, AlgorithmName, StringComparison.Ordinal))
        {
            throw Refused();
        }

        if (envelope.KeyReference is null || !KeyReference().IsMatch(envelope.KeyReference))
        {
            throw Refused();
        }

        if (envelope.NoteId is null || !CanonicalUuidV7().IsMatch(envelope.NoteId) || !Guid.TryParse(envelope.NoteId, out var noteId))
        {
            throw Refused();
        }

        if (Base64Length(envelope.NonceB64) != 12 || Base64Length(envelope.TagB64) != 16 || Base64Length(envelope.CiphertextB64) != CiphertextBytes || Base64Length(envelope.WrappedDataKeyB64) is < 29 or > 512)
        {
            throw Refused();
        }

        return noteId;
    }

    /// <summary>V10: the note id has not been used by any committed or pending Security Auditor command of the organization.</summary>
    public static void RequireUnusedNoteId(Guid noteId, IEnumerable<string> committedPayloads)
    {
        ArgumentNullException.ThrowIfNull(committedPayloads);
        var wanted = noteId.ToString("D");
        foreach (var json in committedPayloads)
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("reason_note_envelope", out var envelope)
                && envelope.ValueKind == JsonValueKind.Object
                && envelope.TryGetProperty("note_id", out var id)
                && string.Equals(id.GetString(), wanted, StringComparison.Ordinal))
            {
                throw AuthorityException.Invalid("the reason note was already used by another command");
            }
        }
    }

    /// <summary>Strict RFC 4648 §4 base64 (standard alphabet, padding, no whitespace, canonical); -1 when not.</summary>
    private static int Base64Length(string? value)
    {
        if (string.IsNullOrEmpty(value) || value.Length % 4 != 0 || value.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '+' or '/' or '=')))
        {
            return -1;
        }

        try
        {
            var bytes = Convert.FromBase64String(value);
            return string.Equals(Convert.ToBase64String(bytes), value, StringComparison.Ordinal) ? bytes.Length : -1;
        }
        catch (FormatException)
        {
            return -1;
        }
    }

    private static AuthorityException Refused() => AuthorityException.Invalid("reason_note_envelope is not a valid sealed reason note");
}
