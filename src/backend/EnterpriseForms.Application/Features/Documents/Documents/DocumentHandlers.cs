using EnterpriseForms.Application.Common;
using EnterpriseForms.Application.Common.Authority;
using EnterpriseForms.Application.Common.Persistence;
using EnterpriseForms.Application.Features.Audit;
using EnterpriseForms.Application.Features.Documents.Ports;
using EnterpriseForms.Application.Features.Documents.UploadSessions;
using EnterpriseForms.Application.Features.FormRecords.Projection;
using EnterpriseForms.Application.Features.FormRecords.Staging;
using EnterpriseForms.Contracts.Features;
using EnterpriseForms.Domain.Features.Audit;
using EnterpriseForms.Domain.Features.Documents;
using EnterpriseForms.Domain.Features.FormRecords;

using EnterpriseForms.Application.Features.SecurityOperations.Incidents;
using EnterpriseForms.Domain.Features.SecurityOperations;
namespace EnterpriseForms.Application.Features.Documents.Documents;

/// <summary>Authorized document projection, version history, download (server-side decrypt) and removal (new content revision without the leaf).</summary>
public sealed class DocumentHandlers(IBusinessStore store, IFileStorageProvider storage, IFileEncryptionService encryption, VerifiedRecordReader reader, RecordCandidateStager stager, IAuthorizationAuthorityClient authority, TimeProvider clock, ISecurityIncidentRecorder incidents, IDecryptionFailureRecorder decryptionFailures, DocumentStreamOptions streamOptions, IBrowserSessionStateAuthority sessions)
{
    public async Task<IReadOnlyList<DocumentSummaryResponse>> ListAsync(RequestContext context, Guid formRecordId, CancellationToken cancellationToken)
    {
        var verified = await reader.ReadAsync(context, formRecordId, ProtectedAction.Read, cancellationToken).ConfigureAwait(false);
        return verified.AuthorizedAttachments.OrderBy(a => a.UploadedOn).Select(a => Summary(a, "Ready")).ToList();
    }

    public async Task<DocumentDetailResponse> GetAsync(RequestContext context, Guid formRecordId, Guid fileAttachmentId, CancellationToken cancellationToken)
    {
        var verified = await reader.ReadAsync(context, formRecordId, ProtectedAction.Read, cancellationToken).ConfigureAwait(false);
        var current = verified.AuthorizedAttachments.FirstOrDefault(a => a.FileAttachmentId == fileAttachmentId)
                      ?? throw new BusinessException("NOT_FOUND", 404, "document not found");
        List<FileAttachmentVersion> versions;
        await using (var scope = await store.BeginScopeAsync(cancellationToken).ConfigureAwait(false))
        {
            versions = await scope.GetAttachmentVersionsAsync(fileAttachmentId, cancellationToken).ConfigureAwait(false);
        }

        // C10 (independent review D-3, WP-0 Section 3.6): a share recipient sees only the version the Authority released to them — never
        // other versions, uploads still processing or versions of aborted operations (their names, sizes and uploaders included).
        if (!verified.IsComplete)
        {
            return new DocumentDetailResponse(Summary(current, "Ready"),
                [new DocumentVersionResponse(current.FileAttachmentVersionId, current.VersionNumber, current.OriginalFileName, current.DetectedContentType, current.PlaintextSizeBytes, current.ScanVerdict,
                    current.ScannerName, current.UploadedOn, current.UploadedByUserId, "Current", current.ContentRevision)]);
        }

        // History shows one row per version number; the committed copy wins over the processed copy. C10 owner decision 2: a number can
        // also carry the staged copy of an operation the Authority refused; the current number shows the verified current row, and a tie
        // operations is settled by the Authority's accepted history.
        var history = new List<FileAttachmentVersion>();
        var committedByRevision = new Dictionary<long, Guid?>();
        foreach (var group in versions.GroupBy(v => v.VersionNumber))
        {
            history.Add(group.Key == current.VersionNumber ? current
                : await AcceptedCopyAsync(authority, context, formRecordId, group.ToList(), committedByRevision, cancellationToken).ConfigureAwait(false)
                  ?? group.OrderByDescending(v => v.ContentChangeOperationId != Guid.Empty).ThenByDescending(v => v.ContentRevision).ThenBy(v => v.FileAttachmentVersionId).First());
        }

        var listed = history
            .OrderByDescending(v => v.VersionNumber)
            .Select(v => new DocumentVersionResponse(v.FileAttachmentVersionId, v.VersionNumber, v.OriginalFileName, v.DetectedContentType, v.PlaintextSizeBytes, v.ScanVerdict, v.ScannerName, v.UploadedOn, v.UploadedByUserId,
                v.VersionNumber == current.VersionNumber ? "Current" : v.ContentChangeOperationId == Guid.Empty ? "Processed" : "Superseded", v.ContentChangeOperationId == Guid.Empty ? null : v.ContentRevision))
            .ToList();
        return new DocumentDetailResponse(Summary(current, "Ready"), listed);
    }

    public sealed record DownloadContent(string FileName, string ContentType, long Length, Func<Stream, CancellationToken, Task> WriteTo);

    /// <summary>
    /// Download of the accepted current version (or an earlier committed version): verified projection → fetch ciphertext → decrypt
    /// stream → audit. CONS-23: while the plaintext streams, the same verified read is repeated at most every
    /// <see cref="DocumentStreamOptions.RecheckInterval"/> before more plaintext is written (<see cref="RecheckedDownloadStream"/>), and
    /// the stream stops, audited, as soon as the document is no longer released (<see cref="DocumentStreamAdmission.Evaluate"/>).
    /// </summary>
    public async Task<DownloadContent> DownloadAsync(RequestContext context, Guid formRecordId, Guid fileAttachmentId, int? versionNumber, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var admittedAt = clock.GetTimestamp(); // the admission check below is the stream's first check
        var verified = await reader.ReadAsync(context, formRecordId, ProtectedAction.Read, cancellationToken).ConfigureAwait(false);
        // B-05: a document that is not in the accepted current content (removed, or not released to this caller) releases no version at
        // all, current or historic (Stage B C-200: the C10 nullable form let a full reader download an earlier version of a removed one).
        var current = verified.AuthorizedAttachments.FirstOrDefault(a => a.FileAttachmentId == fileAttachmentId)
                      ?? throw new BusinessException("NOT_FOUND", 404, "document not found");
        FileAttachmentVersion target;
        if (versionNumber is { } requested && requested != current.VersionNumber)
        {
            // C10 WP8-A (independent review D-4): a share recipient downloads only the current version the Authority released to them;
            // historic verification needs full read at the Authority, so an earlier version is not offered. A full reader may download
            // any accepted version, as before.
            if (!verified.IsComplete)
            {
                throw new BusinessException("NOT_FOUND", 404, "document version not found");
            }

            target = await VerifiedHistoricVersionAsync(context, formRecordId, fileAttachmentId, requested, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            target = current;
            // C10 WP8-A: a share recipient may view a shared document but download it only when the Authority says its binding allows it.
            if (!verified.IsComplete && !Downloadable(verified.Verification, fileAttachmentId))
            {
                throw new BusinessException("NOT_FOUND", 404, "document not found");
            }
        }

        // E3-M2c: the stored envelope with its signed schema and Envelope v2 columns; the verified reader dispatches on them (S-8).
        var envelope = VerifiedDocumentEnvelope.Of(target);
        var aad = new FileEncryptionContext(target.TenantId, target.FormRecordId, target.FileAttachmentId, target.VersionNumber);
        await AuditRecorder.RecordAsync(store, context, clock, AuditCategories.Document, AuditActions.DocumentDownloaded, "authorized document download released", formRecordId, "Success", new { fileAttachmentId, versionNumber = target.VersionNumber }, cancellationToken).ConfigureAwait(false);
        var captured = target;
        var admission = DocumentStreamAdmission.Of(verified, target, historic: !ReferenceEquals(target, current));
        return new DownloadContent(captured.OriginalFileName, captured.DetectedContentType, captured.PlaintextSizeBytes, async (output, ct) =>
        {
            await using var ciphertext = await storage.OpenReadAsync(StorageArea.Documents, captured.StorageObjectKey, ct).ConfigureAwait(false);
            await using var rechecked = new RecheckedDownloadStream(output, recheckCt => RecheckAsync(context, formRecordId, admission, recheckCt), streamOptions.RecheckInterval, clock, admittedAt);
            try
            {
                await encryption.DecryptVerifiedAsync(ciphertext, rechecked, envelope, aad, ct).ConfigureAwait(false);
            }
            catch (DocumentStreamInterruptedException interrupted)
            {
                // CONS-23: no further plaintext is written; the response is aborted (incomplete body) and the stop is audited.
                // An unavailable Authority is a failure to decide, not a denial (independent review finding 7).
                var outcome = interrupted.ReasonCode == DocumentStreamRechecks.AuthorityUnavailable ? "Failed" : "Denied";
                await AuditRecorder.RecordAsync(store, context, clock, AuditCategories.Document, AuditActions.DocumentDownloadFailed, "document download stopped by the Authority re-check", formRecordId, outcome,
                    new { fileAttachmentId, versionNumber = captured.VersionNumber, reasonCode = interrupted.ReasonCode }, CancellationToken.None).ConfigureAwait(false);
                throw;
            }
            catch (Exception ex) when (FormRecords.Encryption.DecryptionFailure.Find(ex) is not null)
            {
                // E0-4: the response may already be streaming, so this is the boundary that records the failure (once); the
                // exception is rethrown unchanged and the download still fails closed.
                await decryptionFailures.RecordOnceAsync(context, ex).ConfigureAwait(false);
                throw;
            }
        });
    }

    /// <summary>
    /// CONS-23: one re-check of a streaming download. The browser session must still be valid at the Authority (G23), and a fresh
    /// verified read (Authority permission, accepted head, integrity, share scope and expiry at access time) must still release the
    /// admitted document version; a denial, an Authority or verification failure and any other error stop the stream. A cancelled
    /// request is a client disconnect, not an interruption.
    /// </summary>
    private async Task RecheckAsync(RequestContext context, Guid formRecordId, DocumentStreamAdmission admission, CancellationToken cancellationToken)
    {
        VerifiedRecord fresh;
        try
        {
            await DocumentStreamRechecks.RequireValidSessionAsync(sessions, context, cancellationToken).ConfigureAwait(false);
            fresh = await reader.ReadAsync(context, formRecordId, ProtectedAction.Read, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (DocumentStreamRechecks.Classify(ex, cancellationToken) is { } interrupted && !ReferenceEquals(interrupted, ex))
        {
            throw interrupted;
        }

        if (admission.Evaluate(fresh) is { } reason)
        {
            throw new DocumentStreamInterruptedException(reason);
        }
    }

    /// <summary>
    /// A historic version is released only from an earlier record snapshot the authority accepted: the business rows of that
    /// operation are verified against its stored manifest (complete leaf set, every row HMAC, root) before the envelope,
    /// object key or file name are used. Business rows alone (which a database writer could alter or promote) are never trusted.
    /// </summary>
    private static bool Downloadable(VerifyRecordProjectionResult verification, Guid fileAttachmentId) =>
        verification.DownloadableItems?.Any(i => i.ItemKind == ManifestItemKind.Attachment && i.ItemId == fileAttachmentId) == true;

    private async Task<FileAttachmentVersion> VerifiedHistoricVersionAsync(RequestContext context, Guid formRecordId, Guid fileAttachmentId, int requested, CancellationToken cancellationToken)
    {
        List<FileAttachmentVersion> numbered;
        await using (var scope = await store.BeginScopeAsync(cancellationToken).ConfigureAwait(false))
        {
            var versions = await scope.GetAttachmentVersionsAsync(fileAttachmentId, cancellationToken).ConfigureAwait(false);
            numbered = versions.Where(v => v.VersionNumber == requested && v.ContentChangeOperationId != Guid.Empty && v.FormRecordId == formRecordId).ToList();
        }

        // C10 owner decision 2: the copy of the latest operation that staged this number, the accepted one when two tie at one revision.
        var chosen = await AcceptedCopyAsync(authority, context, formRecordId, numbered, [], cancellationToken).ConfigureAwait(false)
                     ?? throw new BusinessException("NOT_FOUND", 404, "document version not found");
        var operationId = chosen.ContentChangeOperationId;
        var snapshot = await stager.ReadStagedAsync(formRecordId, operationId, cancellationToken).ConfigureAwait(false);
        VerifyRecordProjectionResult verification;
        try
        {
            verification = await authority.VerifyRecordProjectionAsync(context.ToDelegatedUser(), new VerifyRecordProjectionRequest(context.OrganizationId, formRecordId, ProtectedAction.Read, snapshot.Items, operationId), cancellationToken).ConfigureAwait(false);
        }
        catch (AuthorityClientException ex) when (SecurityIncidentClassifier.FromAuthorityError(ex) is not null)
        {
            await incidents.RecordAsync(SecurityIncidentObservation.For(context, SecurityIncidentClassifier.FromAuthorityError(ex)!, formRecordId, SecurityIncidentDetectionSources.DocumentVersionDownload, ex.ErrorCode), cancellationToken).ConfigureAwait(false);
            throw;
        }

        if (!verification.Verified && SecurityIncidentClassifier.FromVerificationFailure(verification.FailureReasonCode) is { } incidentType)
        {
            await incidents.RecordAsync(SecurityIncidentObservation.For(context, incidentType, formRecordId, SecurityIncidentDetectionSources.DocumentVersionDownload, verification.FailureReasonCode ?? string.Empty, verification.CommittedContentRevision), cancellationToken).ConfigureAwait(false);
        }

        if (!verification.Verified || verification.CommittedOperationId != operationId || !verification.IsCompleteProjection)
        {
            throw new BusinessException("INTEGRITY_VERIFICATION_FAILED", 423, "historic document version failed integrity verification: " + verification.FailureReasonCode);
        }

        return snapshot.Rows.OfType<FileAttachmentVersion>().SingleOrDefault(v => v.FileAttachmentId == fileAttachmentId && v.VersionNumber == requested)
               ?? throw new BusinessException("NOT_FOUND", 404, "document version not found");
    }

    /// <summary>
    /// C10 owner decision 2 (re-review): the accepted copy of one version number. A number can carry copies staged by operations the
    /// Authority refused (an aborted replacement, or a carried row of an aborted edit), at any revision, so the copies are walked newest
    /// revision first and the first whose operation the Authority's accepted history names for that revision is returned (at most
    /// <see cref="AcceptedCopyLookups"/> revisions; none found means no accepted copy). Answers are cached per revision for one request.
    /// Read-only, full readers only (the history read requires full read at the Authority).
    /// </summary>
    internal static async Task<FileAttachmentVersion?> AcceptedCopyAsync(IAuthorizationAuthorityClient authority, RequestContext context, Guid formRecordId, IReadOnlyList<FileAttachmentVersion> copies,
        Dictionary<long, Guid?> committedByRevision, CancellationToken cancellationToken)
    {
        foreach (var atRevision in copies.Where(v => v.ContentChangeOperationId != Guid.Empty).GroupBy(v => v.ContentRevision).OrderByDescending(g => g.Key).Take(AcceptedCopyLookups))
        {
            if (!committedByRevision.TryGetValue(atRevision.Key, out var committed))
            {
                var page = await authority.GetContentHistoryAsync(context.ToDelegatedUser(), context.OrganizationId, formRecordId, atRevision.Key - 1, 1, cancellationToken).ConfigureAwait(false);
                committedByRevision[atRevision.Key] = committed = page.Entries.FirstOrDefault(e => e.ContentRevision == atRevision.Key)?.CommittedOperationId;
            }

            if (atRevision.FirstOrDefault(v => v.ContentChangeOperationId == committed) is { } accepted)
            {
                return accepted;
            }
        }

        return null;
    }

    /// <summary>The most revisions <see cref="AcceptedCopyAsync"/> asks the Authority about for one version number.</summary>
    private const int AcceptedCopyLookups = 8;

    public async Task<FormRecordResponse> RemoveAsync(RequestContext context, Guid formRecordId, Guid fileAttachmentId, RemoveDocumentRequest request, string idempotencyKey, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(request);
        var verified = await reader.ReadAsync(context, formRecordId, ProtectedAction.Edit, cancellationToken).ConfigureAwait(false);
        if (!verified.IsComplete)
        {
            throw new BusinessException("FORBIDDEN", 403, "removing documents requires record ownership");
        }

        FormRecords.Lifecycle.RecordLifecycle.RequireContentEditable(verified);

        if (request.ExpectedContentRevision != verified.Head.CommittedContentRevision)
        {
            throw new BusinessException("CONTENT_REVISION_CONFLICT", 409, "expected_content_revision is stale");
        }

        if (verified.AuthorizedAttachments.All(a => a.FileAttachmentId != fileAttachmentId))
        {
            throw new BusinessException("NOT_FOUND", 404, "document not found");
        }

        var header = CandidateBuilder.CloneHeader(verified.Header);
        var rows = new List<ISignedContentRow>();
        rows.AddRange(verified.AllRows.OfType<FieldValueRowBase>().Select(v => UploadSessionHandlers.CloneValue(v)));
        rows.AddRange(verified.AllRows.OfType<FormRecordGroupRevision>().Select(g => CandidateBuilder.CloneOccurrence(g)));
        rows.AddRange(verified.AllRows.OfType<FileAttachmentVersion>().Where(a => a.FileAttachmentId != fileAttachmentId).Select(a => CandidateBuilder.CloneAttachment(a)));
        var accepted = await stager.StageAndCommitAsync(context, verified.Head, new RecordCandidateStager.CandidateInput(header, rows), "doc-remove:" + idempotencyKey, cancellationToken).ConfigureAwait(false);

        await using (var scope = await store.BeginScopeAsync(cancellationToken).ConfigureAwait(false))
        {
            var attachment = await scope.FindAttachmentAsync(context.OrganizationId, fileAttachmentId, cancellationToken).ConfigureAwait(false);
            if (attachment is not null)
            {
                attachment.AttachmentLifecycleState = AttachmentLifecycleStates.Removed;
                attachment.AttachmentModifiedOn = clock.GetUtcNow();
                attachment.AttachmentModifiedByUserId = context.LocalUserId;
            }

            var record = await scope.FindFormRecordAsync(context.OrganizationId, formRecordId, cancellationToken).ConfigureAwait(false);
            if (record is not null && accepted.CommittedOperationId is not null && await scope.ProjectAcceptedHeadAsync(context.OrganizationId, formRecordId, accepted.CommittedContentRevision, accepted.CommittedOperationId, cancellationToken).ConfigureAwait(false))
            {
                record.ModifiedOn = clock.GetUtcNow();
                record.ModifiedByUserId = context.LocalUserId;
            }

            AuditRecorder.Record(scope, context, clock, AuditCategories.Document, AuditActions.DocumentRemoved, "document removed from the accepted content (bytes retained)", formRecordId, fileAttachmentId: fileAttachmentId, metadata: new { reason = request.RemovalReason, contentRevision = accepted.CommittedContentRevision });
            await scope.CommitAsync(cancellationToken).ConfigureAwait(false);
        }

        return new FormRecordResponse(formRecordId, context.OrganizationId, header.FormDefinitionId, header.RecordStatus, accepted.CommittedContentRevision, accepted.CommittedOperationId, accepted.AuthorizationRevision, true, await verified.ToValueResponsesAsync(cancellationToken).ConfigureAwait(false), "Committed(" + accepted.ArchiveState + ")", RecordGroupProjection.Build(verified.Groups, verified.Layout, verified.Occurrences));
    }

    private static DocumentSummaryResponse Summary(FileAttachmentVersion a, string lifecycle) =>
        new(a.FileAttachmentId, a.FieldDefinitionId, a.OriginalFileName, a.VersionNumber, a.DetectedContentType, a.PlaintextSizeBytes, a.ScanVerdict, lifecycle, a.UploadedOn, a.UploadedByUserId, true);
}
