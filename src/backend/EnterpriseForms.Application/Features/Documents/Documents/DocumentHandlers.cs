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

namespace EnterpriseForms.Application.Features.Documents.Documents;

/// <summary>Authorized document projection, version history, download (server-side decrypt) and removal (new content revision without the leaf).</summary>
public sealed class DocumentHandlers(IBusinessStore store, IFileStorageProvider storage, IFileEncryptionService encryption, VerifiedRecordReader reader, RecordCandidateStager stager, IAuthorizationAuthorityClient authority, TimeProvider clock)
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

        // History shows one row per version number; the committed copy wins over the processed copy.
        var history = versions
            .GroupBy(v => v.VersionNumber)
            .Select(g => g.OrderByDescending(v => v.ContentChangeOperationId != Guid.Empty).ThenByDescending(v => v.ContentRevision).First())
            .OrderByDescending(v => v.VersionNumber)
            .Select(v => new DocumentVersionResponse(v.FileAttachmentVersionId, v.VersionNumber, v.OriginalFileName, v.DetectedContentType, v.PlaintextSizeBytes, v.ScanVerdict, v.ScannerName, v.UploadedOn, v.UploadedByUserId,
                v.VersionNumber == current.VersionNumber ? "Current" : v.ContentChangeOperationId == Guid.Empty ? "Processed" : "Superseded", v.ContentChangeOperationId == Guid.Empty ? null : v.ContentRevision))
            .ToList();
        return new DocumentDetailResponse(Summary(current, "Ready"), history);
    }

    public sealed record DownloadContent(string FileName, string ContentType, long Length, Func<Stream, CancellationToken, Task> WriteTo);

    /// <summary>Download of the accepted current version (or an earlier committed version): verified projection → fetch ciphertext → decrypt stream → audit.</summary>
    public async Task<DownloadContent> DownloadAsync(RequestContext context, Guid formRecordId, Guid fileAttachmentId, int? versionNumber, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var verified = await reader.ReadAsync(context, formRecordId, ProtectedAction.Read, cancellationToken).ConfigureAwait(false);
        var current = verified.AuthorizedAttachments.FirstOrDefault(a => a.FileAttachmentId == fileAttachmentId)
                      ?? throw new BusinessException("NOT_FOUND", 404, "document not found");
        var target = current;
        if (versionNumber is { } requested && requested != current.VersionNumber)
        {
            if (!verified.IsComplete)
            {
                throw new BusinessException("NOT_FOUND", 404, "document version not found"); // partial readers see the current version only
            }

            target = await VerifiedHistoricVersionAsync(context, formRecordId, fileAttachmentId, requested, cancellationToken).ConfigureAwait(false);
        }

        var envelope = new FileEncryptionEnvelope(target.EncryptionAlgorithmName, target.EncryptionEnvelopeVersion, target.EncryptionKeyReference, target.WrappedDataKey, target.EncryptionNoncePrefix, target.EncryptionSegmentSizeBytes);
        var aad = new FileEncryptionContext(target.TenantId, target.FormRecordId, target.FileAttachmentId, target.VersionNumber);
        await AuditRecorder.RecordAsync(store, context, clock, AuditCategories.Document, AuditActions.DocumentDownloaded, "authorized document download released", formRecordId, "Success", new { fileAttachmentId, versionNumber = target.VersionNumber }, cancellationToken).ConfigureAwait(false);
        var captured = target;
        return new DownloadContent(captured.OriginalFileName, captured.DetectedContentType, captured.PlaintextSizeBytes, async (output, ct) =>
        {
            await using var ciphertext = await storage.OpenReadAsync(StorageArea.Documents, captured.StorageObjectKey, ct).ConfigureAwait(false);
            await encryption.DecryptAsync(ciphertext, output, envelope, aad, ct).ConfigureAwait(false);
        });
    }

    /// <summary>
    /// A historic version is released only from an earlier record snapshot the authority accepted: the business rows of that
    /// operation are verified against its stored manifest (complete leaf set, every row HMAC, root) before the envelope,
    /// object key or file name are used. Business rows alone (which a database writer could alter or promote) are never trusted.
    /// </summary>
    private async Task<FileAttachmentVersion> VerifiedHistoricVersionAsync(RequestContext context, Guid formRecordId, Guid fileAttachmentId, int requested, CancellationToken cancellationToken)
    {
        List<Guid> candidateOperations;
        await using (var scope = await store.BeginScopeAsync(cancellationToken).ConfigureAwait(false))
        {
            var versions = await scope.GetAttachmentVersionsAsync(fileAttachmentId, cancellationToken).ConfigureAwait(false);
            candidateOperations = versions.Where(v => v.VersionNumber == requested && v.ContentChangeOperationId != Guid.Empty && v.FormRecordId == formRecordId)
                .OrderByDescending(v => v.ContentRevision).Select(v => v.ContentChangeOperationId).Distinct().Take(1).ToList();
        }

        if (candidateOperations.Count == 0)
        {
            throw new BusinessException("NOT_FOUND", 404, "document version not found");
        }

        var operationId = candidateOperations[0];
        var snapshot = await stager.ReadStagedAsync(formRecordId, operationId, cancellationToken).ConfigureAwait(false);
        var verification = await authority.VerifyRecordProjectionAsync(context.ToDelegatedUser(), new VerifyRecordProjectionRequest(context.OrganizationId, formRecordId, ProtectedAction.Read, snapshot.Items, operationId), cancellationToken).ConfigureAwait(false);
        if (!verification.Verified || !verification.IsCompleteProjection || verification.CommittedOperationId != operationId)
        {
            throw new BusinessException("INTEGRITY_VERIFICATION_FAILED", 423, "historic document version failed integrity verification: " + verification.FailureReasonCode);
        }

        return snapshot.Rows.OfType<FileAttachmentVersion>().SingleOrDefault(v => v.FileAttachmentId == fileAttachmentId && v.VersionNumber == requested)
               ?? throw new BusinessException("NOT_FOUND", 404, "document version not found");
    }

    public async Task<FormRecordResponse> RemoveAsync(RequestContext context, Guid formRecordId, Guid fileAttachmentId, RemoveDocumentRequest request, string idempotencyKey, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(request);
        var verified = await reader.ReadAsync(context, formRecordId, ProtectedAction.Edit, cancellationToken).ConfigureAwait(false);
        if (!verified.IsComplete)
        {
            throw new BusinessException("FORBIDDEN", 403, "removing documents requires record ownership");
        }

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
            if (record is not null && accepted.CommittedOperationId is { } acceptedOperation && record.RecordAcceptedContent(accepted.CommittedContentRevision, acceptedOperation))
            {
                record.ModifiedOn = clock.GetUtcNow();
                record.ModifiedByUserId = context.LocalUserId;
            }

            AuditRecorder.Record(scope, context, clock, AuditCategories.Document, AuditActions.DocumentRemoved, "document removed from the accepted content (bytes retained)", formRecordId, fileAttachmentId: fileAttachmentId, metadata: new { reason = request.RemovalReason, contentRevision = accepted.CommittedContentRevision });
            await scope.CommitAsync(cancellationToken).ConfigureAwait(false);
        }

        return new FormRecordResponse(formRecordId, context.OrganizationId, header.FormDefinitionId, header.RecordStatus, accepted.CommittedContentRevision, accepted.CommittedOperationId, accepted.AuthorizationRevision, true, verified.ToValueResponses(), "Committed(" + accepted.ArchiveState + ")");
    }

    private static DocumentSummaryResponse Summary(FileAttachmentVersion a, string lifecycle) =>
        new(a.FileAttachmentId, a.FieldDefinitionId, a.OriginalFileName, a.VersionNumber, a.DetectedContentType, a.PlaintextSizeBytes, a.ScanVerdict, lifecycle, a.UploadedOn, a.UploadedByUserId, true);
}
