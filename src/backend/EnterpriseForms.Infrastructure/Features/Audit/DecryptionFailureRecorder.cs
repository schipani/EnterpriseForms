using EnterpriseForms.Application.Common;
using EnterpriseForms.Application.Common.Persistence;
using EnterpriseForms.Application.Features.Audit;
using EnterpriseForms.Application.Features.FormRecords.Encryption;
using EnterpriseForms.Domain.Features.Audit;
using Microsoft.Extensions.Logging;

namespace EnterpriseForms.Infrastructure.Features.Audit;

/// <summary>
/// E0-4: the Security audit event for a failed decryption, in its own short transaction, independent of the request's
/// cancellation (a client that disconnects mid-download must not suppress the event). Metadata is the reason code, the
/// item kind and identifiers; the event never contains plaintext, keys, key references, wrapped keys, nonces or ciphertext.
/// </summary>
public sealed partial class DecryptionFailureRecorder(IBusinessStore store, TimeProvider clock, ILogger<DecryptionFailureRecorder> logger) : IDecryptionFailureRecorder
{
    private static readonly TimeSpan RecordingTimeout = TimeSpan.FromSeconds(5);

    public async Task<bool> RecordOnceAsync(RequestContext context, Exception exception)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(exception);
        if (!DecryptionFailure.TryClaimForRecording(exception, out var failure))
        {
            return false;
        }

        using var timeout = new CancellationTokenSource(RecordingTimeout);
        try
        {
            // Rows are tenant-verified before they are decrypted; should an identifier of another tenant ever appear, it is not copied into this tenant's timeline.
            var sameTenant = failure.TenantId == context.OrganizationId;
            var document = failure.ProtectedItemKind == ProtectedItemKinds.Document;
            await using var scope = await store.BeginScopeAsync(timeout.Token).ConfigureAwait(false);
            AuditRecorder.Record(
                scope,
                context,
                clock,
                AuditCategories.Security,
                AuditActions.DecryptionFailed,
                $"a protected {(document ? "document" : "field value")} could not be decrypted ({failure.ReasonCode}); nothing was released",
                formRecordId: sameTenant ? failure.FormRecordId : null,
                fieldDefinitionId: sameTenant ? failure.FieldDefinitionId : null,
                fileAttachmentId: sameTenant ? failure.FileAttachmentId : null,
                metadata: new
                {
                    reasonCode = failure.ReasonCode,
                    protectedItemKind = failure.ProtectedItemKind,
                    fieldValueItemId = sameTenant ? failure.FieldValueItemId : null,
                    versionNumber = failure.VersionNumber,
                },
                outcome: "Failed");
            await scope.CommitAsync(timeout.Token).ConfigureAwait(false);
            return true;
        }
#pragma warning disable CA1031 // deliberately broad: recording is best effort and must never alter the fail-closed outcome
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogRecordingFailed(logger, ex, failure.ReasonCode, context.OrganizationId, context.CorrelationId);
            return false;
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Decryption-failure audit event could not be recorded (reason {ReasonCode}, tenant {TenantId}, correlation {CorrelationId}); the request still failed closed.")]
    private static partial void LogRecordingFailed(ILogger logger, Exception exception, string reasonCode, Guid tenantId, Guid correlationId);
}
