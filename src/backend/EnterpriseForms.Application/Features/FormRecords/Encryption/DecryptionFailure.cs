using System.Security.Cryptography;
using EnterpriseForms.Application.Features.Documents.Ports;

namespace EnterpriseForms.Application.Features.FormRecords.Encryption;

/// <summary>Why an encrypted field value or document could not be decrypted (E0-4). Stable codes; they carry no key or content detail.</summary>
public static class DecryptionFailureReasons
{
    /// <summary>The AEAD tag did not verify (tampered or foreign ciphertext, wrong AAD, truncated or reordered segments).</summary>
    public const string AuthenticationFailed = "AUTHENTICATION_FAILED";

    /// <summary>The wrapped data key did not open under the key the reference names (different or replaced KEK, damaged wrapped key).</summary>
    public const string KeyMismatch = "KEY_MISMATCH";

    /// <summary>No permitted key provider is configured, or the key reference is unknown to it.</summary>
    public const string KeyUnavailable = "KEY_UNAVAILABLE";

    /// <summary>The envelope names an algorithm, version or parameter this build does not decrypt.</summary>
    public const string UnsupportedEnvelope = "UNSUPPORTED_ENVELOPE";
}

public static class ProtectedItemKinds
{
    public const string FieldValue = "FieldValue";
    public const string Document = "Document";
}

/// <summary>Where in a decryption the failure happened; decides the reason code.</summary>
public enum DecryptionStage
{
    Envelope,
    KeyUnwrap,
    Payload,
}

/// <summary>
/// E0-4: a non-sensitive description of one failed decryption, attached by the decrypting service to the exception it
/// throws (through <see cref="Exception.Data"/>, so the exception type — and therefore every existing catch and the public
/// error contract — is unchanged). Identifiers and a reason code only: never plaintext, keys, wrapped keys, nonces or
/// ciphertext. A boundary that has a request context records it once (<see cref="TryClaimForRecording"/>).
/// </summary>
public sealed record DecryptionFailure(
    string ReasonCode,
    string ProtectedItemKind,
    Guid TenantId,
    Guid FormRecordId,
    Guid? FieldDefinitionId,
    Guid? FieldValueItemId,
    Guid? FileAttachmentId,
    int? VersionNumber)
{
    private const string FailureKey = "EnterpriseForms.DecryptionFailure";
    private const string RecordedKey = "EnterpriseForms.DecryptionFailure.Recorded";

    public static DecryptionFailure ForFieldValue(string reasonCode, Guid tenantId, Guid formRecordId, Guid fieldDefinitionId, Guid fieldValueItemId) =>
        new(reasonCode, ProtectedItemKinds.FieldValue, tenantId, formRecordId, fieldDefinitionId, fieldValueItemId, null, null);

    public static DecryptionFailure ForDocument(string reasonCode, Guid tenantId, Guid formRecordId, Guid fileAttachmentId, int versionNumber) =>
        new(reasonCode, ProtectedItemKinds.Document, tenantId, formRecordId, null, null, fileAttachmentId, versionNumber);

    /// <summary>The reason for a decryption exception at <paramref name="stage"/>, or null when the exception is not a decryption failure (for example an I/O error of the sink).</summary>
    public static string? ReasonFor(Exception exception, DecryptionStage stage) => exception switch
    {
        EncryptionUnavailableException => DecryptionFailureReasons.KeyUnavailable,
        CryptographicException when stage == DecryptionStage.Envelope => DecryptionFailureReasons.UnsupportedEnvelope,
        CryptographicException when stage == DecryptionStage.KeyUnwrap => DecryptionFailureReasons.KeyMismatch,
        CryptographicException => DecryptionFailureReasons.AuthenticationFailed,
        _ => null,
    };

    /// <summary>Attaches this description to <paramref name="exception"/> unless one is already attached (the innermost decryption wins).</summary>
    public void AttachTo(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        if (!exception.Data.Contains(FailureKey))
        {
            exception.Data[FailureKey] = this;
        }
    }

    /// <summary>The description attached to <paramref name="exception"/> or one of its inner exceptions, if any.</summary>
    public static DecryptionFailure? Find(Exception? exception) => Holder(exception)?.Data[FailureKey] as DecryptionFailure;

    /// <summary>Carries the description (and whether it was recorded) onto an exception that replaces the original, such as a typed business refusal.</summary>
    public static void CarryOver(Exception from, Exception to)
    {
        ArgumentNullException.ThrowIfNull(from);
        ArgumentNullException.ThrowIfNull(to);
        if (Holder(from) is { } holder)
        {
            to.Data[FailureKey] = holder.Data[FailureKey];
            if (holder.Data.Contains(RecordedKey))
            {
                to.Data[RecordedKey] = true;
            }
        }
    }

    /// <summary>
    /// True exactly once per failure: the first boundary to see it records the event, any later one (an outer handler that
    /// sees the same exception, or the replacement it was carried onto) does not. False when nothing is attached.
    /// </summary>
    public static bool TryClaimForRecording(Exception exception, out DecryptionFailure failure)
    {
        ArgumentNullException.ThrowIfNull(exception);
        failure = null!;
        if (Holder(exception) is not { } holder || holder.Data.Contains(RecordedKey))
        {
            return false;
        }

        holder.Data[RecordedKey] = true;
        failure = (DecryptionFailure)holder.Data[FailureKey]!;
        return true;
    }

    private static Exception? Holder(Exception? exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current.Data.Contains(FailureKey))
            {
                return current;
            }
        }

        return null;
    }
}
