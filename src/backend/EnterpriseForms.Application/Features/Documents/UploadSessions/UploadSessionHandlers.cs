using System.Globalization;
using EnterpriseForms.Application.Common;
using EnterpriseForms.Application.Common.Authority;
using EnterpriseForms.Application.Common.Persistence;
using EnterpriseForms.Application.Features.Audit;
using EnterpriseForms.Application.Features.Documents.Ports;
using EnterpriseForms.Application.Features.FormDefinitions.Layout;
using EnterpriseForms.Application.Features.FormRecords.Projection;
using EnterpriseForms.Application.Features.FormRecords.Staging;
using EnterpriseForms.Contracts.Features;
using EnterpriseForms.Domain.Features.Audit;
using EnterpriseForms.Domain.Features.Documents;
using EnterpriseForms.Domain.Features.FormRecords;

namespace EnterpriseForms.Application.Features.Documents.UploadSessions;

public static class UploadLimits
{
    public const int ChunkSizeBytes = 4 * 1024 * 1024;
    public const long MaxSizeBytes = 500L * 1024 * 1024;
    public static readonly TimeSpan SessionLifetime = TimeSpan.FromHours(24);
}

/// <summary>
/// Upload session lifecycle (CLAUDE-DESIGN §6.2): begin (authorized, idempotent) → chunks (bounded, acknowledged
/// server-side) → complete (durable job) → [worker: scan, encrypt, store] → commit (user session binds the signed
/// attachment version into the record manifest; only then is the document Ready).
/// </summary>
public sealed class UploadSessionHandlers(IBusinessStore store, IAuthorizationAuthorityClient authority, IFileStorageProvider storage, VerifiedRecordReader reader, RecordCandidateStager stager, TimeProvider clock)
{
    public async Task<UploadSessionResponse> BeginAsync(RequestContext context, BeginUploadRequest request, string idempotencyKey, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(request);
        var fileName = SanitizeFileName(request.OriginalFileName);
        if (request.SizeBytes is < 1 or > UploadLimits.MaxSizeBytes || string.IsNullOrWhiteSpace(request.ContentType) || request.ContentType.Length > 127)
        {
            throw new BusinessException("INVALID_REQUEST", 422, "size must be 1..500 MiB and a content type is required");
        }

        await using (var scope = await store.BeginScopeAsync(cancellationToken).ConfigureAwait(false))
        {
            var existing = await scope.FindUploadSessionByIdempotencyAsync(context.OrganizationId, context.LocalUserId, idempotencyKey, cancellationToken).ConfigureAwait(false);
            if (existing is not null)
            {
                return await ToResponseAsync(scope, existing, cancellationToken).ConfigureAwait(false);
            }
        }

        // Authorization: the record must verify and the caller must hold Edit (owner or parent owner); document fields come from the layout.
        var verified = await reader.ReadAsync(context, request.FormRecordId, ProtectedAction.Edit, cancellationToken).ConfigureAwait(false);
        var edit = await authority.CheckPermissionAsync(context.ToDelegatedUser(), new PermissionCheckRequest(context.OrganizationId, new ResourceReference(ProtectedResourceKind.FormRecord, request.FormRecordId), ProtectedAction.Edit), cancellationToken).ConfigureAwait(false);
        if (!edit.Allowed)
        {
            throw new BusinessException("FORBIDDEN", 403, "uploading documents requires edit permission on the record");
        }

        var element = verified.Layout.Elements.FirstOrDefault(e => e.Kind == LayoutElementKinds.DocumentCollection && e.FieldDefinitionId == request.FieldDefinitionId)
                      ?? throw new BusinessException("INVALID_REQUEST", 422, "field is not a document collection of this form version");
        var policy = element.Upload!;
        if (request.SizeBytes > policy.MaxFileSizeBytes)
        {
            throw new BusinessException("FILE_TOO_LARGE", 422, $"this field accepts files up to {policy.MaxFileSizeBytes} bytes");
        }

        if (!policy.AllowedContentTypes.Contains(request.ContentType.ToLowerInvariant(), StringComparer.Ordinal))
        {
            throw new BusinessException("CONTENT_TYPE_NOT_ALLOWED", 422, "content type is not permitted for this field");
        }

        var currentCount = verified.AuthorizedAttachments.Count(a => a.FieldDefinitionId == request.FieldDefinitionId);
        if (request.ReplacesFileAttachmentId is null && currentCount >= policy.MaxFiles)
        {
            throw new BusinessException("FILE_COUNT_EXCEEDED", 422, $"this field accepts at most {policy.MaxFiles} documents");
        }

        if (request.ReplacesFileAttachmentId is { } replaces && verified.AuthorizedAttachments.All(a => a.FileAttachmentId != replaces))
        {
            throw new BusinessException("NOT_FOUND", 404, "attachment to replace is not part of the accepted record content");
        }

        var now = clock.GetUtcNow();
        var sessionId = Guid.CreateVersion7();
        var chunkCount = (int)Math.Ceiling(request.SizeBytes / (double)UploadLimits.ChunkSizeBytes);
        var session = new UploadSession
        {
            UploadSessionId = sessionId,
            TenantId = context.OrganizationId,
            FormRecordId = request.FormRecordId,
            FieldDefinitionId = request.FieldDefinitionId,
            ReplacesFileAttachmentId = request.ReplacesFileAttachmentId,
            OwnerUserId = context.LocalUserId,
            SessionIdempotencyKey = idempotencyKey,
            OriginalFileName = fileName,
            DeclaredContentType = request.ContentType.ToLowerInvariant(),
            DeclaredSizeBytes = request.SizeBytes,
            ChunkSizeBytes = UploadLimits.ChunkSizeBytes,
            ExpectedChunkCount = chunkCount,
            ReceivedChunkBitmap = new byte[chunkCount],
            StagingObjectKey = $"staging/{context.OrganizationId:N}/{request.FormRecordId:N}/{sessionId:N}",
            SessionState = UploadSessionStates.Open,
            SessionCreatedOn = now,
            SessionExpiresOn = now.Add(UploadLimits.SessionLifetime),
            SessionModifiedOn = now,
        };

        await using (var scope = await store.BeginScopeAsync(cancellationToken).ConfigureAwait(false))
        {
            scope.AddUploadSession(session);
            AuditRecorder.Record(scope, context, clock, AuditCategories.Document, AuditActions.DocumentUploadStarted, "upload session opened", request.FormRecordId, fieldDefinitionId: request.FieldDefinitionId, targetEntityType: "upload_session", targetEntityId: sessionId, metadata: new { sizeBytes = request.SizeBytes, chunkCount });
            await scope.CommitAsync(cancellationToken).ConfigureAwait(false);
            return await ToResponseAsync(scope, session, cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task<UploadChunkReceiptResponse> AcceptChunkAsync(RequestContext context, Guid uploadSessionId, int chunkIndex, Stream content, long? declaredLength, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(content);
        UploadSession session;
        await using (var scope = await store.BeginScopeAsync(cancellationToken).ConfigureAwait(false))
        {
            session = await RequireOwnedSessionAsync(scope, context, uploadSessionId, cancellationToken).ConfigureAwait(false);
        }

        if (session.SessionState != UploadSessionStates.Open)
        {
            throw new BusinessException("UPLOAD_SESSION_NOT_OPEN", 409, "session is " + session.SessionState);
        }

        RequireUnexpired(session);
        if (chunkIndex < 0 || chunkIndex >= session.ExpectedChunkCount)
        {
            throw new BusinessException("INVALID_REQUEST", 422, "chunk index out of range");
        }

        // Re-check live authorization per chunk (revocation mid-upload stops the transfer, CLAUDE-DESIGN §6.1).
        var edit = await authority.CheckPermissionAsync(context.ToDelegatedUser(), new PermissionCheckRequest(context.OrganizationId, new ResourceReference(ProtectedResourceKind.FormRecord, session.FormRecordId), ProtectedAction.Edit), cancellationToken).ConfigureAwait(false);
        if (!edit.Allowed)
        {
            throw new BusinessException("FORBIDDEN", 403, "edit permission on the record was revoked");
        }

        var expectedLength = chunkIndex == session.ExpectedChunkCount - 1 ? session.DeclaredSizeBytes - (long)chunkIndex * session.ChunkSizeBytes : session.ChunkSizeBytes;
        if (declaredLength is { } declared && declared != expectedLength)
        {
            throw new BusinessException("INVALID_REQUEST", 422, $"chunk {chunkIndex} must be exactly {expectedLength} bytes");
        }

        using var buffered = new MemoryStream(capacity: (int)Math.Min(expectedLength, int.MaxValue));
        await CopyBoundedAsync(content, buffered, expectedLength, cancellationToken).ConfigureAwait(false);
        if (buffered.Length != expectedLength)
        {
            throw new BusinessException("INVALID_REQUEST", 422, $"chunk {chunkIndex} must be exactly {expectedLength} bytes");
        }

        buffered.Position = 0;
        await storage.StageChunkAsync(session.StagingObjectKey, chunkIndex, buffered, expectedLength, cancellationToken).ConfigureAwait(false);

        await using (var scope = await store.BeginScopeAsync(cancellationToken).ConfigureAwait(false))
        {
            var tracked = await RequireOwnedSessionAsync(scope, context, uploadSessionId, cancellationToken).ConfigureAwait(false);
            if (tracked.ReceivedChunkBitmap[chunkIndex] == 0)
            {
                var bitmap = (byte[])tracked.ReceivedChunkBitmap.Clone();
                bitmap[chunkIndex] = 1;
                tracked.ReceivedChunkBitmap = bitmap;
                tracked.ReceivedBytes += expectedLength;
            }

            tracked.SessionModifiedOn = clock.GetUtcNow();
            await scope.CommitAsync(cancellationToken).ConfigureAwait(false);
            var received = ReceivedIndexes(tracked.ReceivedChunkBitmap);
            return new UploadChunkReceiptResponse(uploadSessionId, chunkIndex, tracked.ReceivedBytes, received, received.Count == tracked.ExpectedChunkCount);
        }
    }

    public async Task<UploadSessionResponse> CompleteAsync(RequestContext context, Guid uploadSessionId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        await using var scope = await store.BeginScopeAsync(cancellationToken).ConfigureAwait(false);
        var session = await RequireOwnedSessionAsync(scope, context, uploadSessionId, cancellationToken).ConfigureAwait(false);
        if (session.SessionState != UploadSessionStates.Open)
        {
            return await ToResponseAsync(scope, session, cancellationToken).ConfigureAwait(false); // idempotent
        }

        RequireUnexpired(session);
        if (ReceivedIndexes(session.ReceivedChunkBitmap).Count != session.ExpectedChunkCount)
        {
            throw new BusinessException("UPLOAD_INCOMPLETE", 409, "not every chunk has been received");
        }

        var edit = await authority.CheckPermissionAsync(context.ToDelegatedUser(), new PermissionCheckRequest(context.OrganizationId, new ResourceReference(ProtectedResourceKind.FormRecord, session.FormRecordId), ProtectedAction.Edit), cancellationToken).ConfigureAwait(false);
        if (!edit.Allowed)
        {
            throw new BusinessException("FORBIDDEN", 403, "edit permission on the record was revoked");
        }

        await storage.CommitStagedChunksAsync(session.StagingObjectKey, session.ExpectedChunkCount, cancellationToken).ConfigureAwait(false);
        var now = clock.GetUtcNow();
        session.SessionState = UploadSessionStates.Completed;
        session.SessionCompletedOn = now;
        session.SessionModifiedOn = now;
        scope.AddProcessingJob(new DocumentProcessingJob
        {
            DocumentProcessingJobId = Guid.CreateVersion7(),
            TenantId = context.OrganizationId,
            UploadSessionId = session.UploadSessionId,
            JobState = ProcessingJobStates.Queued,
            NextAttemptOn = now,
            JobCreatedOn = now,
            JobModifiedOn = now,
        });
        await scope.CommitAsync(cancellationToken).ConfigureAwait(false);
        return await ToResponseAsync(scope, session, cancellationToken).ConfigureAwait(false);
    }

    public async Task<UploadSessionResponse> GetAsync(RequestContext context, Guid uploadSessionId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        await using var scope = await store.BeginScopeAsync(cancellationToken).ConfigureAwait(false);
        var session = await RequireOwnedSessionAsync(scope, context, uploadSessionId, cancellationToken).ConfigureAwait(false);
        return await ToResponseAsync(scope, session, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<UploadSessionResponse>> ListMineAsync(RequestContext context, IReadOnlyCollection<string> states, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        await using var scope = await store.BeginScopeAsync(cancellationToken).ConfigureAwait(false);
        var sessions = await scope.GetUploadSessionsForUserAsync(context.OrganizationId, context.LocalUserId, states, cancellationToken).ConfigureAwait(false);
        var result = new List<UploadSessionResponse>();
        foreach (var session in sessions)
        {
            result.Add(await ToResponseAsync(scope, session, cancellationToken).ConfigureAwait(false));
        }

        return result;
    }

    public async Task<UploadSessionResponse> CancelAsync(RequestContext context, Guid uploadSessionId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        await using var scope = await store.BeginScopeAsync(cancellationToken).ConfigureAwait(false);
        var session = await RequireOwnedSessionAsync(scope, context, uploadSessionId, cancellationToken).ConfigureAwait(false);
        if (session.SessionState is UploadSessionStates.Open or UploadSessionStates.Completed)
        {
            session.SessionState = UploadSessionStates.Cancelled;
            session.SessionModifiedOn = clock.GetUtcNow();
            await scope.CommitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await storage.DeleteAsync(StorageArea.Staging, session.StagingObjectKey, cancellationToken).ConfigureAwait(false);
            }
            catch (IOException)
            {
                // staged bytes are reclaimed by the staging sweep (DocumentProcessingService.SweepStagingAsync)
            }
        }

        return await ToResponseAsync(scope, session, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Final step (B-05): binds the processed version into the record manifest through the caller's authenticated
    /// context (full candidate = current rows + the new attachment leaf, replacing a prior version of the same attachment).
    /// </summary>
    public async Task<UploadSessionResponse> CommitAsync(RequestContext context, Guid uploadSessionId, string idempotencyKey, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        UploadSession session;
        FileAttachmentVersion processed;
        await using (var scope = await store.BeginScopeAsync(cancellationToken).ConfigureAwait(false))
        {
            session = await RequireOwnedSessionAsync(scope, context, uploadSessionId, cancellationToken).ConfigureAwait(false);
            if (session.SessionState == UploadSessionStates.Committed)
            {
                return await ToResponseAsync(scope, session, cancellationToken).ConfigureAwait(false);
            }

            if (session.SessionState != UploadSessionStates.Processed || session.ProducedFileAttachmentVersionId is null)
            {
                throw new BusinessException("UPLOAD_NOT_PROCESSED", 409, "document is still being processed (scan/encryption) or was rejected");
            }

            processed = await scope.FindAttachmentVersionAsync(session.ProducedFileAttachmentVersionId.Value, cancellationToken).ConfigureAwait(false)
                        ?? throw new BusinessException("UPLOAD_NOT_PROCESSED", 409, "processed version missing");
        }

        if (processed.ScanVerdict != ScanVerdicts.Clean)
        {
            throw new BusinessException("DOCUMENT_REJECTED", 422, "a document that did not scan clean cannot be committed");
        }

        var verified = await reader.ReadAsync(context, session.FormRecordId, ProtectedAction.Edit, cancellationToken).ConfigureAwait(false);
        if (!verified.IsComplete)
        {
            throw new BusinessException("FORBIDDEN", 403, "committing documents requires record ownership");
        }

        var header = CandidateBuilder.CloneHeader(verified.Header);
        var rows = new List<ISignedContentRow>();
        rows.AddRange(verified.AllRows.OfType<FieldValueRowBase>().Select(v => CloneValue(v)));
        rows.AddRange(verified.AllRows.OfType<FormRecordGroupRevision>().Select(g => CandidateBuilder.CloneOccurrence(g)));
        rows.AddRange(verified.AllRows.OfType<FileAttachmentVersion>().Where(a => a.FileAttachmentId != processed.FileAttachmentId).Select(a => CandidateBuilder.CloneAttachment(a)));
        rows.Add(CandidateBuilder.CloneAttachment(processed));
        CandidateBuilder.EnsureWithinCeiling(rows.Count + 1);

        var accepted = await stager.StageAndCommitAsync(context, verified.Head, new RecordCandidateStager.CandidateInput(header, rows), "doc-commit:" + idempotencyKey, cancellationToken).ConfigureAwait(false);
        await using (var scope = await store.BeginScopeAsync(cancellationToken).ConfigureAwait(false))
        {
            var tracked = await RequireOwnedSessionAsync(scope, context, uploadSessionId, cancellationToken).ConfigureAwait(false);
            if (tracked.SessionState != UploadSessionStates.Committed)
            {
                tracked.SessionState = UploadSessionStates.Committed;
                tracked.SessionModifiedOn = clock.GetUtcNow();
                var attachment = await scope.FindAttachmentAsync(context.OrganizationId, processed.FileAttachmentId, cancellationToken).ConfigureAwait(false);
                if (attachment is not null)
                {
                    attachment.CurrentVersionNumber = processed.VersionNumber;
                    attachment.AttachmentLifecycleState = AttachmentLifecycleStates.Ready;
                    attachment.AttachmentModifiedOn = clock.GetUtcNow();
                    attachment.AttachmentModifiedByUserId = context.LocalUserId;
                }

                var record = await scope.FindFormRecordAsync(context.OrganizationId, session.FormRecordId, cancellationToken).ConfigureAwait(false);
                if (record is not null && accepted.CommittedOperationId is not null && await scope.ProjectAcceptedHeadAsync(context.OrganizationId, session.FormRecordId, accepted.CommittedContentRevision, accepted.CommittedOperationId, cancellationToken).ConfigureAwait(false))
                {
                    record.ModifiedOn = clock.GetUtcNow();
                    record.ModifiedByUserId = context.LocalUserId;
                }

                AuditRecorder.Record(scope, context, clock, AuditCategories.Document, AuditActions.DocumentReady, "document version committed into the record manifest", session.FormRecordId, fieldDefinitionId: session.FieldDefinitionId, fileAttachmentId: processed.FileAttachmentId, metadata: new { versionNumber = processed.VersionNumber, contentRevision = accepted.CommittedContentRevision });
                await scope.CommitAsync(cancellationToken).ConfigureAwait(false);
            }

            return await ToResponseAsync(scope, tracked, cancellationToken).ConfigureAwait(false);
        }
    }

    internal static FieldValueRowBase CloneValue(FieldValueRowBase v)
    {
        FieldValueRowBase clone = v switch
        {
            FieldStringValue s => new FieldStringValue { FieldStringValueId = Guid.CreateVersion7(), StringValue = s.StringValue },
            FieldIntegerValue i => new FieldIntegerValue { FieldIntegerValueId = Guid.CreateVersion7(), IntegerValue = i.IntegerValue },
            FieldDecimalValue d => new FieldDecimalValue { FieldDecimalValueId = Guid.CreateVersion7(), DecimalValue = d.DecimalValue },
            FieldDateValue d => new FieldDateValue { FieldDateValueId = Guid.CreateVersion7(), DateValue = d.DateValue },
            FieldDateTimeValue dt => new FieldDateTimeValue { FieldDateTimeValueId = Guid.CreateVersion7(), DateTimeValue = dt.DateTimeValue },
            FieldGuidValue g => new FieldGuidValue { FieldGuidValueId = Guid.CreateVersion7(), GuidValue = g.GuidValue },
            FieldBooleanValue b => new FieldBooleanValue { FieldBooleanValueId = Guid.CreateVersion7(), BooleanValue = b.BooleanValue },
            FieldOptionValue o => new FieldOptionValue { FieldOptionValueId = Guid.CreateVersion7(), FieldOptionId = o.FieldOptionId },
            // Ciphertext is carried unchanged: the AAD binds tenant/record/field/item, none of which change in a copy.
            FieldEncryptedValue e => new FieldEncryptedValue { FieldEncryptedValueId = Guid.CreateVersion7(), EncryptedValueBytes = e.EncryptedValueBytes, EncryptionNonceBytes = e.EncryptionNonceBytes, EncryptionAuthTagBytes = e.EncryptionAuthTagBytes, WrappedDataKeyBytes = e.WrappedDataKeyBytes, EncryptionAlgorithmName = e.EncryptionAlgorithmName, EncryptionKeyReference = e.EncryptionKeyReference, EncryptionEnvelopeVersion = e.EncryptionEnvelopeVersion, EncryptedLogicalType = e.EncryptedLogicalType },
            _ => throw new InvalidOperationException("unsupported value row " + v.GetType().Name),
        };
        clone.TenantId = v.TenantId;
        clone.FormRecordId = v.FormRecordId;
        clone.FieldDefinitionId = v.FieldDefinitionId;
        clone.FormRecordGroupId = v.FormRecordGroupId;
        clone.FieldValueItemId = v.FieldValueItemId;
        clone.CreatedOn = v.CreatedOn;
        clone.CreatedByUserId = v.CreatedByUserId;
        return clone;
    }

    /// <summary>An expired session accepts no more plaintext and cannot be released; the staging sweep purges what it holds.</summary>
    private void RequireUnexpired(UploadSession session)
    {
        if (session.SessionExpiresOn <= clock.GetUtcNow())
        {
            throw new BusinessException("UPLOAD_SESSION_EXPIRED", 409, "the upload session expired; start a new upload");
        }
    }

    private static async Task<UploadSession> RequireOwnedSessionAsync(IBusinessScope scope, RequestContext context, Guid uploadSessionId, CancellationToken cancellationToken)
    {
        var session = await scope.FindUploadSessionAsync(context.OrganizationId, uploadSessionId, cancellationToken).ConfigureAwait(false);
        if (session is null || session.OwnerUserId != context.LocalUserId)
        {
            throw new BusinessException("NOT_FOUND", 404, "upload session not found"); // concealment-safe: no existence leak
        }

        return session;
    }

    private static async Task<UploadSessionResponse> ToResponseAsync(IBusinessScope scope, UploadSession s, CancellationToken cancellationToken)
    {
        var job = s.SessionState is UploadSessionStates.Open or UploadSessionStates.Cancelled ? null : await scope.FindProcessingJobAsync(s.UploadSessionId, cancellationToken).ConfigureAwait(false);
        FileAttachmentVersion? version = s.ProducedFileAttachmentVersionId is { } id ? await scope.FindAttachmentVersionAsync(id, cancellationToken).ConfigureAwait(false) : null;
        var stage = s.SessionState switch
        {
            UploadSessionStates.Open => "Transferring",
            UploadSessionStates.Completed => job?.JobState == ProcessingJobStates.ScanPending ? "ScanPending" : "Received",
            UploadSessionStates.Processing => "Scanning",
            UploadSessionStates.Processed => "AwaitingCommit",
            UploadSessionStates.Committed => "Ready",
            UploadSessionStates.Rejected => "Rejected",
            UploadSessionStates.Failed => "Failed",
            UploadSessionStates.Cancelled => "Cancelled",
            _ => s.SessionState,
        };
        // A rejected session has no version row; its verdict is the one the worker recorded from the real scanner result
        // (MALWARE_DETECTED only ever follows an Infected verdict, SCAN_ERROR a scanner error). Never synthesized otherwise.
        var failureCode = s.FailureReasonCode ?? job?.LastErrorCode;
        var verdict = version?.ScanVerdict ?? failureCode switch
        {
            "MALWARE_DETECTED" => ScanVerdicts.Infected,
            "SCAN_ERROR" => ScanVerdicts.ScanError,
            _ => null,
        };
        return new UploadSessionResponse(s.UploadSessionId, s.FormRecordId, s.FieldDefinitionId, s.OriginalFileName, s.DeclaredSizeBytes, s.ChunkSizeBytes, s.ExpectedChunkCount, ReceivedIndexes(s.ReceivedChunkBitmap), s.ReceivedBytes, s.SessionState, failureCode, stage, s.SessionExpiresOn, version?.FileAttachmentId, version?.VersionNumber, verdict);
    }

    private static List<int> ReceivedIndexes(byte[] bitmap) => bitmap.Select((b, i) => (b, i)).Where(x => x.b != 0).Select(x => x.i).ToList();

    private static string SanitizeFileName(string? name)
    {
        var trimmed = (name ?? string.Empty).Trim();
        trimmed = trimmed.Replace('\\', '/');
        trimmed = trimmed[(trimmed.LastIndexOf('/') + 1)..];
        var cleaned = new string(trimmed.Where(c => !char.IsControl(c)).ToArray());
        if (cleaned.Length is 0 or > 255 || cleaned is "." or "..")
        {
            throw new BusinessException("INVALID_REQUEST", 422, "a plain file name (1..255 chars) is required");
        }

        return cleaned;
    }

    private static async Task CopyBoundedAsync(Stream source, Stream destination, long maxBytes, CancellationToken cancellationToken)
    {
        var buffer = new byte[81920];
        long total = 0;
        int read;
        while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            total += read;
            if (total > maxBytes)
            {
                throw new BusinessException("INVALID_REQUEST", 422, "chunk exceeds the declared length");
            }

            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }

        _ = CultureInfo.InvariantCulture;
    }
}
