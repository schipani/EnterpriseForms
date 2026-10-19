using System.Security.Cryptography;
using EnterpriseForms.Application.Common.Persistence;
using EnterpriseForms.Application.Features.Documents.Ports;
using EnterpriseForms.Application.Features.FormRecords.Integrity;
using EnterpriseForms.Domain.Features.Audit;
using EnterpriseForms.Domain.Features.Documents;

namespace EnterpriseForms.Application.Features.Documents.Processing;

/// <summary>
/// Durable processing of a completed upload (CLAUDE.md §4A, CLAUDE-DESIGN §5.5): lease job → read quarantined bytes →
/// malware scan (real scanner result required; unavailable ⇒ ScanPending and retry, never Ready) → detect content type
/// → encrypt (segmented AES-256-GCM, per-version DEK) → store ciphertext → write the processed version row (not yet in
/// any manifest; the user's commit binds it). Hosted by the Worker image and, in Development, by the API.
/// </summary>
public sealed class DocumentProcessingService(IBusinessStore store, IFileStorageProvider storage, IFileMalwareScanner scanner, IFileEncryptionService encryption, TimeProvider clock)
{
    public static readonly TimeSpan LeaseDuration = TimeSpan.FromMinutes(10);

    /// <summary>Terminal sessions are purged again after this grace period (covers best-effort deletes and chunks still in flight at cancel).</summary>
    public static readonly TimeSpan StagingPurgeGrace = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Staging sweep (CLAUDE.md §4A "uncommitted staged uploads expire and are cleaned by a retryable scheduled job"): expires
    /// open/received sessions past their lifetime (the job can no longer release them), then deletes the plaintext staging
    /// object — committed blob and uncommitted blocks — of every expired or terminal session and records the purge. A storage
    /// failure leaves the session unpurged so the next sweep retries. Returns the number of sessions purged.
    /// </summary>
    public async Task<int> SweepStagingAsync(int batchSize, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        List<UploadSession> due;
        await using (var scope = await store.BeginScopeAsync(cancellationToken).ConfigureAwait(false))
        {
            due = await scope.GetSessionsDueForStagingPurgeAsync(now, now - StagingPurgeGrace, batchSize, cancellationToken).ConfigureAwait(false);
            var expiring = new List<UploadSession>();
            foreach (var session in due.Where(s => s.SessionState is UploadSessionStates.Open or UploadSessionStates.Completed))
            {
                var job = await scope.FindProcessingJobAsync(session.UploadSessionId, cancellationToken).ConfigureAwait(false);
                if (job is not null && job.JobState == ProcessingJobStates.Leased && job.LeaseExpiresOn > now)
                {
                    continue; // a worker holds it right now; the next sweep re-evaluates
                }

                if (job is not null)
                {
                    job.JobState = ProcessingJobStates.Failed;
                    job.LastErrorCode = "SESSION_EXPIRED";
                    job.LeaseOwnerName = null;
                    job.LeaseExpiresOn = null;
                    job.JobModifiedOn = now;
                }

                session.SessionState = UploadSessionStates.Expired;
                session.FailureReasonCode = "SESSION_EXPIRED";
                session.SessionModifiedOn = now;
                expiring.Add(session);
                scope.AddAuditEvent(new AuditEvent
                {
                    AuditEventId = Guid.CreateVersion7(),
                    TenantId = session.TenantId,
                    EventOccurredOn = now,
                    EventCategory = "Document",
                    EventAction = AuditActions.DocumentRejected,
                    EventOutcome = "Failed",
                    ActorUserId = session.OwnerUserId,
                    ActorType = "Service",
                    FormRecordId = session.FormRecordId,
                    FieldDefinitionId = session.FieldDefinitionId,
                    TargetEntityType = "upload_session",
                    TargetEntityId = session.UploadSessionId,
                    CorrelationIdentifier = Guid.CreateVersion7(),
                    EventDescription = "upload session expired before it was released; staged bytes purged",
                    EventMetadataJson = System.Text.Json.JsonSerializer.Serialize(new { reason = "SESSION_EXPIRED", receivedBytes = session.ReceivedBytes }),
                });
            }

            due = due.Where(s => s.SessionState is not (UploadSessionStates.Open or UploadSessionStates.Completed)).ToList();
            await scope.CommitAsync(cancellationToken).ConfigureAwait(false);
        }

        var purged = 0;
        foreach (var session in due)
        {
            try
            {
                await storage.DeleteAsync(StorageArea.Staging, session.StagingObjectKey, cancellationToken).ConfigureAwait(false);
            }
            catch (IOException)
            {
                continue; // storage unavailable: stays unpurged, retried by the next sweep
            }

            await using var scope = await store.BeginScopeAsync(cancellationToken).ConfigureAwait(false);
            var tracked = await scope.FindUploadSessionAsync(session.TenantId, session.UploadSessionId, cancellationToken).ConfigureAwait(false);
            if (tracked is not null && tracked.StagingObjectPurgedOn is null)
            {
                tracked.StagingObjectPurgedOn = clock.GetUtcNow();
                await scope.CommitAsync(cancellationToken).ConfigureAwait(false);
                purged++;
            }
        }

        return purged;
    }

    /// <summary>Processes at most one job; returns false when no job was due.</summary>
    public async Task<bool> ProcessOnceAsync(string workerName, CancellationToken cancellationToken)
    {
        DocumentProcessingJob job;
        UploadSession session;
        await using (var scope = await store.BeginScopeAsync(cancellationToken).ConfigureAwait(false))
        {
            var leased = await scope.LeaseNextProcessingJobAsync(workerName, LeaseDuration, cancellationToken).ConfigureAwait(false);
            if (leased is null)
            {
                return false;
            }

            job = leased;
            session = await scope.FindUploadSessionAsync(job.TenantId, job.UploadSessionId, cancellationToken).ConfigureAwait(false)
                      ?? throw new InvalidOperationException("upload session missing for job " + job.DocumentProcessingJobId);
            if (session.SessionState == UploadSessionStates.Completed)
            {
                session.SessionState = UploadSessionStates.Processing;
                session.SessionModifiedOn = clock.GetUtcNow();
            }

            await scope.CommitAsync(cancellationToken).ConfigureAwait(false);
        }

        try
        {
            await ProcessAsync(job, session, cancellationToken).ConfigureAwait(false);
        }
        catch (ScannerUnavailableException ex)
        {
            await MarkAsync(job.UploadSessionId, ProcessingJobStates.ScanPending, UploadSessionStates.Completed, "SCANNER_UNAVAILABLE:" + Truncate(ex.Message), TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
        }
        catch (EncryptionUnavailableException ex)
        {
            await MarkAsync(job.UploadSessionId, ProcessingJobStates.ScanPending, UploadSessionStates.Completed, "ENCRYPTION_UNAVAILABLE:" + Truncate(ex.Message), TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or TimeoutException or System.Net.Http.HttpRequestException)
        {
            var permanent = job.AttemptCount >= 5;
            await MarkAsync(job.UploadSessionId, permanent ? ProcessingJobStates.Failed : ProcessingJobStates.Queued, permanent ? UploadSessionStates.Failed : UploadSessionStates.Completed, "PROCESSING_ERROR:" + Truncate(ex.Message), TimeSpan.FromSeconds(Math.Min(300, 10 * Math.Pow(2, job.AttemptCount))), cancellationToken).ConfigureAwait(false);
            if (permanent)
            {
                await SafeDeleteStagingAsync(session, cancellationToken).ConfigureAwait(false); // never released: drop the plaintext now (the sweep confirms)
            }
        }

        return true;
    }

    private async Task ProcessAsync(DocumentProcessingJob job, UploadSession session, CancellationToken cancellationToken)
    {
        // 1. Scan the quarantined bytes with a real scanner; no verdict ⇒ no release.
        ScanResult scan;
        await using (var quarantined = await storage.OpenReadAsync(StorageArea.Staging, session.StagingObjectKey, cancellationToken).ConfigureAwait(false))
        {
            scan = await scanner.ScanAsync(quarantined, session.DeclaredSizeBytes, cancellationToken).ConfigureAwait(false);
        }

        if (scan.Verdict != ScanVerdicts.Clean)
        {
            await using var scope = await store.BeginScopeAsync(cancellationToken).ConfigureAwait(false);
            var trackedJob = await scope.FindProcessingJobAsync(job.UploadSessionId, cancellationToken).ConfigureAwait(false);
            var trackedSession = await scope.FindUploadSessionAsync(job.TenantId, job.UploadSessionId, cancellationToken).ConfigureAwait(false);
            if (trackedJob is not null && trackedSession is not null)
            {
                trackedJob.JobState = ProcessingJobStates.Rejected;
                trackedJob.LastErrorCode = scan.Verdict == ScanVerdicts.Infected ? "MALWARE_DETECTED" : "SCAN_ERROR";
                trackedJob.JobModifiedOn = clock.GetUtcNow();
                trackedSession.SessionState = UploadSessionStates.Rejected;
                trackedSession.FailureReasonCode = trackedJob.LastErrorCode;
                trackedSession.SessionModifiedOn = clock.GetUtcNow();
                scope.AddAuditEvent(new AuditEvent
                {
                    AuditEventId = Guid.CreateVersion7(),
                    TenantId = job.TenantId,
                    EventOccurredOn = clock.GetUtcNow(),
                    EventCategory = "Document",
                    EventAction = AuditActions.DocumentRejected,
                    EventOutcome = "Failed",
                    ActorUserId = session.OwnerUserId,
                    ActorType = "Service",
                    FormRecordId = session.FormRecordId,
                    FieldDefinitionId = session.FieldDefinitionId,
                    TargetEntityType = "upload_session",
                    TargetEntityId = session.UploadSessionId,
                    CorrelationIdentifier = Guid.CreateVersion7(),
                    EventDescription = "document rejected by the malware scanner",
                    EventMetadataJson = System.Text.Json.JsonSerializer.Serialize(new { verdict = scan.Verdict, scanner = scan.ScannerName, signature = scan.SignatureName }),
                });
                await scope.CommitAsync(cancellationToken).ConfigureAwait(false);
            }

            await SafeDeleteStagingAsync(session, cancellationToken).ConfigureAwait(false);
            return;
        }

        // 2. Content type from magic bytes (client type is never trusted).
        string detected;
        await using (var sniff = await storage.OpenReadAsync(StorageArea.Staging, session.StagingObjectKey, cancellationToken).ConfigureAwait(false))
        {
            detected = await ContentTypeSniffer.DetectAsync(sniff, session.DeclaredContentType, cancellationToken).ConfigureAwait(false);
        }

        // 3. Encrypt into the documents container under a fresh version identity.
        Guid attachmentId;
        int versionNumber;
        await using (var scope = await store.BeginScopeAsync(cancellationToken).ConfigureAwait(false))
        {
            if (session.ReplacesFileAttachmentId is { } replaces)
            {
                var versions = await scope.GetAttachmentVersionsAsync(replaces, cancellationToken).ConfigureAwait(false);
                attachmentId = replaces;
                versionNumber = (versions.Count == 0 ? 0 : versions.Max(v => v.VersionNumber)) + 1;
            }
            else
            {
                attachmentId = Guid.CreateVersion7();
                versionNumber = 1;
            }
        }

        var objectKey = $"documents/{session.TenantId:N}/{session.FormRecordId:N}/{attachmentId:N}/v{versionNumber}";
        var context = new FileEncryptionContext(session.TenantId, session.FormRecordId, attachmentId, versionNumber);
        EncryptionOutcome outcome;
        var temp = Path.Combine(Path.GetTempPath(), "ef-doc-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using (var plaintext = await storage.OpenReadAsync(StorageArea.Staging, session.StagingObjectKey, cancellationToken).ConfigureAwait(false))
            await using (var ciphertext = new FileStream(temp, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 81920, FileOptions.DeleteOnClose | FileOptions.Asynchronous))
            {
                outcome = await encryption.EncryptAsync(plaintext, ciphertext, context, cancellationToken).ConfigureAwait(false);
                ciphertext.Position = 0;
                await storage.UploadAsync(StorageArea.Documents, objectKey, ciphertext, "application/octet-stream", cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            if (File.Exists(temp))
            {
                File.Delete(temp);
            }
        }

        if (outcome.PlaintextSizeBytes != session.DeclaredSizeBytes)
        {
            throw new InvalidOperationException("declared size differs from received bytes");
        }

        // 4. Record the processed version (operation 0 / no signature until the user's commit) and the attachment identity.
        var now = SignedTimestamp.Truncate(clock.GetUtcNow());
        await using (var scope = await store.BeginScopeAsync(cancellationToken).ConfigureAwait(false))
        {
            var trackedJob = await scope.FindProcessingJobAsync(job.UploadSessionId, cancellationToken).ConfigureAwait(false);
            var trackedSession = await scope.FindUploadSessionAsync(job.TenantId, job.UploadSessionId, cancellationToken).ConfigureAwait(false);
            if (trackedJob is null || trackedSession is null || trackedSession.SessionState != UploadSessionStates.Processing)
            {
                return; // another worker finished or the session was cancelled
            }

            if (await scope.FindAttachmentAsync(session.TenantId, attachmentId, cancellationToken).ConfigureAwait(false) is null)
            {
                scope.AddAttachment(new FileAttachment
                {
                    FileAttachmentId = attachmentId,
                    TenantId = session.TenantId,
                    FormRecordId = session.FormRecordId,
                    FieldDefinitionId = session.FieldDefinitionId,
                    OriginalFileName = session.OriginalFileName,
                    CurrentVersionNumber = 0,
                    AttachmentLifecycleState = AttachmentLifecycleStates.Ready,
                    AttachmentCreatedOn = now,
                    AttachmentCreatedByUserId = session.OwnerUserId,
                    AttachmentModifiedOn = now,
                    AttachmentModifiedByUserId = session.OwnerUserId,
                });
            }

            var version = new FileAttachmentVersion
            {
                FileAttachmentVersionId = Guid.CreateVersion7(),
                TenantId = session.TenantId,
                FormRecordId = session.FormRecordId,
                FileAttachmentId = attachmentId,
                FieldDefinitionId = session.FieldDefinitionId,
                VersionNumber = versionNumber,
                OriginalFileName = session.OriginalFileName,
                DetectedContentType = detected,
                PlaintextSizeBytes = outcome.PlaintextSizeBytes,
                PlaintextSha256 = outcome.PlaintextSha256,
                CiphertextSizeBytes = outcome.CiphertextSizeBytes,
                CiphertextSha256 = outcome.CiphertextSha256,
                StorageProviderName = storage.ProviderName,
                StorageObjectKey = objectKey,
                EncryptionAlgorithmName = outcome.Envelope.AlgorithmName,
                EncryptionKeyReference = outcome.Envelope.KeyReference,
                EncryptionEnvelopeVersion = outcome.Envelope.EnvelopeVersion,
                WrappedDataKey = outcome.Envelope.WrappedDataKey,
                EncryptionNoncePrefix = outcome.Envelope.NoncePrefix,
                EncryptionSegmentSizeBytes = outcome.Envelope.SegmentSizeBytes,
                ScanVerdict = scan.Verdict,
                ScannerName = scan.ScannerName,
                ScanSignatureName = scan.SignatureName,
                ScannedOn = now,
                UploadedByUserId = session.OwnerUserId,
                UploadedOn = SignedTimestamp.Truncate(session.SessionCompletedOn ?? now),
                ContentChangeOperationId = Guid.Empty,
                ContentRevision = 1,
                RowRevision = 1,
                RowSignature = [],
                RowSignatureKeyVersion = 1,
                RowSignatureSchemaVersion = 1,
                VersionState = "Processed",
            };
            scope.AddAttachmentVersion(version);
            trackedSession.SessionState = UploadSessionStates.Processed;
            trackedSession.ProducedFileAttachmentVersionId = version.FileAttachmentVersionId;
            trackedSession.SessionModifiedOn = now;
            trackedJob.JobState = ProcessingJobStates.Processed;
            trackedJob.LastErrorCode = null;
            trackedJob.JobModifiedOn = now;
            scope.AddAuditEvent(new AuditEvent
            {
                AuditEventId = Guid.CreateVersion7(),
                TenantId = job.TenantId,
                EventOccurredOn = now,
                EventCategory = "Document",
                EventAction = AuditActions.DocumentProcessed,
                EventOutcome = "Success",
                ActorUserId = session.OwnerUserId,
                ActorType = "Service",
                FormRecordId = session.FormRecordId,
                FieldDefinitionId = session.FieldDefinitionId,
                FileAttachmentId = attachmentId,
                TargetEntityType = "upload_session",
                TargetEntityId = session.UploadSessionId,
                CorrelationIdentifier = Guid.CreateVersion7(),
                EventDescription = "document scanned clean and encrypted; awaiting manifest commit",
                EventMetadataJson = System.Text.Json.JsonSerializer.Serialize(new { scanner = scan.ScannerName, versionNumber, detectedContentType = detected, plaintextSizeBytes = outcome.PlaintextSizeBytes }),
            });
            await scope.CommitAsync(cancellationToken).ConfigureAwait(false);
        }

        await SafeDeleteStagingAsync(session, cancellationToken).ConfigureAwait(false);
    }

    private async Task MarkAsync(Guid uploadSessionId, string jobState, string sessionState, string error, TimeSpan retryAfter, CancellationToken cancellationToken)
    {
        await using var scope = await store.BeginScopeAsync(cancellationToken).ConfigureAwait(false);
        var job = await scope.FindProcessingJobAsync(uploadSessionId, cancellationToken).ConfigureAwait(false);
        if (job is null)
        {
            return;
        }

        var session = await scope.FindUploadSessionAsync(job.TenantId, uploadSessionId, cancellationToken).ConfigureAwait(false);
        var now = clock.GetUtcNow();
        job.JobState = jobState;
        job.LastErrorCode = error;
        job.NextAttemptOn = now.Add(retryAfter);
        job.LeaseOwnerName = null;
        job.LeaseExpiresOn = null;
        job.JobModifiedOn = now;
        if (session is not null)
        {
            session.SessionState = sessionState;
            session.FailureReasonCode = jobState == ProcessingJobStates.Failed ? error : null;
            session.SessionModifiedOn = now;
        }

        await scope.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task SafeDeleteStagingAsync(UploadSession session, CancellationToken cancellationToken)
    {
        try
        {
            await storage.DeleteAsync(StorageArea.Staging, session.StagingObjectKey, cancellationToken).ConfigureAwait(false);
        }
        catch (IOException)
        {
            // reclaimed by the staging sweep (SweepStagingAsync)
        }
    }

    private static string Truncate(string text) => text.Length <= 150 ? text : text[..150];
}

/// <summary>Magic-byte content detection for the Stage B allow-list; falls back to application/octet-stream, never to the client type when they disagree.</summary>
public static class ContentTypeSniffer
{
    public static async Task<string> DetectAsync(Stream content, string declared, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);
        var head = new byte[16];
        var read = 0;
        while (read < head.Length)
        {
            var n = await content.ReadAsync(head.AsMemory(read, head.Length - read), cancellationToken).ConfigureAwait(false);
            if (n == 0)
            {
                break;
            }

            read += n;
        }

        var bytes = head.AsSpan(0, read);
        if (bytes.Length >= 5 && bytes[..5].SequenceEqual("%PDF-"u8))
        {
            return "application/pdf";
        }

        if (bytes.Length >= 8 && bytes[..8].SequenceEqual(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }))
        {
            return "image/png";
        }

        if (bytes.Length >= 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF)
        {
            return "image/jpeg";
        }

        if (bytes.Length >= 4 && bytes[..4].SequenceEqual(new byte[] { 0x50, 0x4B, 0x03, 0x04 }))
        {
            return declared is "application/vnd.openxmlformats-officedocument.wordprocessingml.document" or "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet" ? declared : "application/zip";
        }

        var textual = bytes.Length == 0 || bytes.ToArray().All(b => b is >= 0x20 or 0x09 or 0x0A or 0x0D);
        if (textual && declared is "text/plain" or "text/csv" or "application/json")
        {
            return declared;
        }

        return textual ? "text/plain" : "application/octet-stream";
    }

    public static byte[] Sha256(Stream stream) => SHA256.HashData(stream);
}
