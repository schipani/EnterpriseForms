using EnterpriseForms.Application.Common;

namespace EnterpriseForms.Application.Features.Audit;

/// <summary>
/// E0-4: writes one <c>DECRYPTION_FAILED</c> Security audit event for an exception that carries a
/// <see cref="FormRecords.Encryption.DecryptionFailure"/>, at most once per failure. Implementations MUST NOT throw: the
/// caller rethrows the original exception and fails closed exactly as before, whether or not the event was written.
/// </summary>
public interface IDecryptionFailureRecorder
{
    /// <returns>True when this call wrote the event; false when nothing was attached, it was already recorded, or writing failed.</returns>
    Task<bool> RecordOnceAsync(RequestContext context, Exception exception);
}
