using EnterpriseForms.Application.Common;
using EnterpriseForms.Application.Common.Authority;
using EnterpriseForms.Application.Features.FormRecords.Projection;
using EnterpriseForms.Domain.Features.Documents;

namespace EnterpriseForms.Application.Features.Documents.Documents;

/// <summary>
/// CONS-23 (trust_authority/CROSS_SYSTEM_CONSISTENCY_PROTOCOL.md, "Long streams and strict revocation"; owner decision 1, C-201):
/// the documented maximum interval between independent Authority re-checks while a document download is streaming. Configuration
/// <c>Documents:StreamRecheckIntervalSeconds</c>, default 5, accepted 1 to 5; any other value refuses to start.
/// </summary>
public sealed class DocumentStreamOptions
{
    public const string SectionName = "Documents";

    public const int MaximumRecheckIntervalSeconds = 5;

    /// <summary>Plaintext is written in slices of at most this many bytes, each preceded by the interval check.</summary>
    public const int MaximumSliceBytes = 64 * 1024;

    public int StreamRecheckIntervalSeconds { get; set; } = MaximumRecheckIntervalSeconds;

    public TimeSpan RecheckInterval => TimeSpan.FromSeconds(StreamRecheckIntervalSeconds);

    public void Validate()
    {
        if (StreamRecheckIntervalSeconds is < 1 or > MaximumRecheckIntervalSeconds)
        {
            throw new InvalidOperationException($"Documents:StreamRecheckIntervalSeconds must be between 1 and {MaximumRecheckIntervalSeconds} (CONS-23); refusing to start.");
        }
    }
}

/// <summary>
/// CONS-23: the parts of one re-check that do not need the verified reader. The browser session (G23) is re-validated at the
/// Authority, so a sign-out, an administrative session revocation, a replaced session or session expiry stops the stream; and every
/// failure of a re-check is classified into the interruption reason (a cancelled request is a disconnect, not an interruption).
/// </summary>
public static class DocumentStreamRechecks
{
    public const string AuthorityUnavailable = "AUTHORITY_UNAVAILABLE";

    public const string SessionInvalid = "SESSION_INVALID";

    /// <summary>A bearer-token request has no browser session; a cookie session must still be valid at the Authority.</summary>
    public static async Task RequireValidSessionAsync(IBrowserSessionStateAuthority sessions, RequestContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sessions);
        ArgumentNullException.ThrowIfNull(context);
        if (context.SessionDigestBase64 is not { Length: > 0 } digest)
        {
            return;
        }

        var state = await sessions.ValidateSessionAsync(context.ToDelegatedUser(), digest, cancellationToken).ConfigureAwait(false);
        if (!state.Valid)
        {
            throw new DocumentStreamInterruptedException(SessionInvalid);
        }
    }

    /// <summary>The interruption for a failed re-check, or null for a cancellation of the request itself (rethrown unchanged).</summary>
    public static DocumentStreamInterruptedException? Classify(Exception exception, CancellationToken cancellationToken) => exception switch
    {
        OperationCanceledException when cancellationToken.IsCancellationRequested => null,
        DocumentStreamInterruptedException interrupted => interrupted,
        BusinessException business => new DocumentStreamInterruptedException(business.ErrorCode, business) { AuthorityFailure = business.HttpStatusCode >= 500 },
        AuthorityClientException authority => new DocumentStreamInterruptedException(authority.ErrorCode, authority) { AuthorityFailure = authority.Retryable || authority.HttpStatusCode >= 500 },
        _ => new DocumentStreamInterruptedException(AuthorityUnavailable, exception) { AuthorityFailure = true },
    };
}

/// <summary>A streaming download stopped because the Authority re-check no longer releases the document (fail closed).</summary>
public sealed class DocumentStreamInterruptedException(string reasonCode, Exception? inner = null)
    : Exception("document download interrupted: " + reasonCode, inner)
{
    public string ReasonCode { get; } = reasonCode;

    /// <summary>True when the Authority (or a dependency) failed to decide, rather than deciding to deny; audited as Failed.</summary>
    public bool AuthorityFailure { get; init; }
}

/// <summary>
/// CONS-23: what the admission released, and the rule each re-check applies to a fresh verified read of the record. The stream
/// continues only if the same document version is still released to the caller: Read is still allowed at the Authority (a revoked
/// or expired share, a removed role or a quarantined record makes the fresh read fail), the authorization revision has not gone
/// backwards, the document is still in the accepted current content (B-05), the streamed version is still the accepted current
/// version (a replacement is a change of the relevant content revision), and, for a share recipient, the Authority still lists the
/// document as downloadable; an earlier version needs full read. Anything else stops the stream.
/// </summary>
public sealed record DocumentStreamAdmission(Guid FileAttachmentId, Guid FileAttachmentVersionId, bool HistoricVersion, long AuthorizationRevision)
{
    public string? Evaluate(VerifiedRecord fresh)
    {
        ArgumentNullException.ThrowIfNull(fresh);
        if (fresh.Verification.AuthorizationRevision < AuthorizationRevision)
        {
            return "AUTHORIZATION_REVISION_REGRESSED";
        }

        var current = fresh.AuthorizedAttachments.FirstOrDefault(a => a.FileAttachmentId == FileAttachmentId);
        if (current is null)
        {
            return "DOCUMENT_NOT_RELEASED";
        }

        if (HistoricVersion)
        {
            return fresh.IsComplete ? null : "FULL_READ_REQUIRED";
        }

        if (current.FileAttachmentVersionId != FileAttachmentVersionId)
        {
            return "DOCUMENT_VERSION_CHANGED";
        }

        return fresh.IsComplete || Downloadable(fresh.Verification, FileAttachmentId) ? null : "DOWNLOAD_NOT_PERMITTED";
    }

    internal static bool Downloadable(VerifyRecordProjectionResult verification, Guid fileAttachmentId) =>
        verification.DownloadableItems?.Any(i => i.ItemKind == ManifestItemKind.Attachment && i.ItemId == fileAttachmentId) == true;

    public static DocumentStreamAdmission Of(VerifiedRecord admitted, FileAttachmentVersion target, bool historic) =>
        new(target.FileAttachmentId, target.FileAttachmentVersionId, historic, admitted.Verification.AuthorizationRevision);
}

/// <summary>
/// CONS-23: the write side of a document download. Every write is cut into slices of at most
/// <see cref="DocumentStreamOptions.MaximumSliceBytes"/>, and before any slice is written, once the interval has passed since the
/// last check began, the Authority re-check runs and must succeed. The check therefore sits in front of every byte handed to the
/// response, however the decryptor sizes its writes: a slow client blocks the current slice's write, and the next slice is checked
/// again. A failed check or any exception from it ends the stream (the response is aborted, the caller discards the sink); nothing
/// is written after it. Only asynchronous writes are supported (the response body forbids synchronous I/O).
/// </summary>
public sealed class RecheckedDownloadStream(Stream inner, Func<CancellationToken, Task> recheck, TimeSpan interval, TimeProvider clock, long admittedTimestamp) : Stream
{
    private long lastCheckStarted = admittedTimestamp;
    private bool interrupted;

    /// <summary>Number of re-checks that ran (diagnostics and tests).</summary>
    public int RecheckCount { get; private set; }

    public override bool CanRead => false;

    public override bool CanSeek => false;

    public override bool CanWrite => !interrupted;

    public override long Length => throw new NotSupportedException();

    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public override void Flush() => throw new NotSupportedException("synchronous I/O is not supported");

    public override Task FlushAsync(CancellationToken cancellationToken) => interrupted ? Task.FromException(new DocumentStreamInterruptedException("STREAM_INTERRUPTED")) : inner.FlushAsync(cancellationToken);

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException("synchronous I/O is not supported");

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        for (var offset = 0; offset < buffer.Length; offset += DocumentStreamOptions.MaximumSliceBytes)
        {
            if (interrupted)
            {
                throw new DocumentStreamInterruptedException("STREAM_INTERRUPTED");
            }

            if (clock.GetElapsedTime(lastCheckStarted) >= interval)
            {
                var started = clock.GetTimestamp();
                try
                {
                    await recheck(cancellationToken).ConfigureAwait(false);
                }
                catch
                {
                    interrupted = true;
                    throw;
                }

                RecheckCount++;
                lastCheckStarted = started;
            }

            await inner.WriteAsync(buffer.Slice(offset, Math.Min(DocumentStreamOptions.MaximumSliceBytes, buffer.Length - offset)), cancellationToken).ConfigureAwait(false);
        }
    }
}
