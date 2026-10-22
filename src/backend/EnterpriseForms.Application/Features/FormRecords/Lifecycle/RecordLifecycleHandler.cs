using EnterpriseForms.Application.Common;
using EnterpriseForms.Application.Common.Authority;
using EnterpriseForms.Application.Common.Persistence;
using EnterpriseForms.Application.Features.Audit;
using EnterpriseForms.Application.Features.Documents.UploadSessions;
using EnterpriseForms.Application.Features.FormRecords.Encryption;
using EnterpriseForms.Application.Features.FormRecords.Projection;
using EnterpriseForms.Application.Features.FormRecords.Staging;
using EnterpriseForms.Contracts.Features;
using EnterpriseForms.Domain.Features.Audit;
using EnterpriseForms.Domain.Features.Documents;
using EnterpriseForms.Domain.Features.FormRecords;

namespace EnterpriseForms.Application.Features.FormRecords.Lifecycle;

/// <summary>
/// Submit, Reopen, Archive and Unarchive (Phase C6 M1). Every transition is one owner content operation through the
/// ordinary prepare → stage → read-back → commit protocol: a verified complete read of the accepted head (Edit action, so
/// only an effective owner), an optimistic revision and status check, then a candidate whose header carries the new
/// status and whose every other row — sealed ciphertext, document versions, entries — is copied verbatim. A transition
/// therefore never decrypts or re-encrypts anything (except Submit, which decrypts to validate, never to write), and is
/// never blocked by what a form or option catalog looks like today. The Authority commits it with the same compare-and-swap
/// on the head as any other write, so a transition and a concurrent edit can never both win.
/// </summary>
public sealed class RecordLifecycleHandler(VerifiedRecordReader reader, IBusinessStore store, RecordCandidateStager stager, IFieldEncryptionService encryption, TimeProvider clock)
{
    public async Task<RecordLifecycleResponse> HandleAsync(RequestContext context, Guid formRecordId, string transition, RecordLifecycleRequest request, string idempotencyKey, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(request);
        if (!RecordLifecycle.Transitions.Contains(transition, StringComparer.Ordinal))
        {
            throw new BusinessException("INVALID_REQUEST", 422, "unknown lifecycle transition");
        }

        // Verified read with the Edit action: the Authority's own read/edit decision and a complete-projection verification.
        VerifiedRecord verified;
        try
        {
            verified = await reader.ReadAsync(context, formRecordId, ProtectedAction.Edit, cancellationToken).ConfigureAwait(false);
        }
        catch (BusinessException ex) when (ex.HttpStatusCode is 404 or 403)
        {
            await DeniedAsync(context, formRecordId, transition, ex.ErrorCode, cancellationToken).ConfigureAwait(false);
            throw;
        }

        if (!verified.IsComplete)
        {
            // A share recipient can see that the record exists, so a plain refusal discloses nothing; contributors cannot submit or archive (OD-03, FR-C6-03).
            await DeniedAsync(context, formRecordId, transition, "NOT_OWNER", cancellationToken).ConfigureAwait(false);
            throw new BusinessException("FORBIDDEN", 403, "only the record's owner can change its status");
        }

        // Authorization comes before input validation: a caller who may not change the status learns nothing about what a valid request looks like.
        var reason = NormalizeReason(transition, request.Reason);
        var current = verified.Header.RecordStatus;
        if (request.ExpectedContentRevision != verified.Head.CommittedContentRevision)
        {
            throw new BusinessException("CONTENT_REVISION_CONFLICT", 409, "the record changed since you loaded it; reload and try again");
        }

        if (request.ExpectedStatus is { } expected && !string.Equals(expected, current, StringComparison.Ordinal))
        {
            throw new BusinessException("LIFECYCLE_STATE_CONFLICT", 409, $"the record is now {current}; reload and try again");
        }

        var now = clock.GetUtcNow();
        var plan = RecordLifecycle.Plan(current, verified.Header.SubmittedOn, transition, now)
                   ?? throw new BusinessException("LIFECYCLE_TRANSITION_NOT_ALLOWED", 409, $"{transition} is not available for a record that is {current}");

        if (transition == RecordLifecycle.Submit)
        {
            await ValidateForSubmissionAsync(context, formRecordId, verified, now, cancellationToken).ConfigureAwait(false);
        }

        // Candidate: new status in the header, every other row copied verbatim (same rows the document-removal path copies).
        var header = CandidateBuilder.CloneHeader(verified.Header);
        header.RecordStatus = plan.Status;
        header.SubmittedOn = plan.SubmittedOn;
        var rows = new List<ISignedContentRow>();
        rows.AddRange(verified.AllRows.OfType<FieldValueRowBase>().Select(v => UploadSessionHandlers.CloneValue(v)));
        rows.AddRange(verified.AllRows.OfType<FormRecordGroupRevision>().Select(g => CandidateBuilder.CloneOccurrence(g)));
        rows.AddRange(verified.AllRows.OfType<FileAttachmentVersion>().Select(a => CandidateBuilder.CloneAttachment(a)));
        CandidateBuilder.EnsureWithinCeiling(rows.Count + 1);

        var accepted = await stager.StageAndCommitAsync(context, verified.Head, new RecordCandidateStager.CandidateInput(header, rows), "lifecycle:" + transition + ":" + idempotencyKey, cancellationToken).ConfigureAwait(false);

        await using (var scope = await store.BeginScopeAsync(cancellationToken).ConfigureAwait(false))
        {
            if (accepted.CommittedOperationId is not null)
            {
                await scope.ProjectAcceptedHeadAsync(context.OrganizationId, formRecordId, accepted.CommittedContentRevision, accepted.CommittedOperationId, cancellationToken).ConfigureAwait(false);
                await scope.ProjectLifecycleAsync(context.OrganizationId, formRecordId, accepted.CommittedContentRevision, plan.Status, plan.SubmittedOn, cancellationToken).ConfigureAwait(false);
            }

            var record = await scope.FindFormRecordAsync(context.OrganizationId, formRecordId, cancellationToken).ConfigureAwait(false);
            if (record is not null)
            {
                record.ModifiedOn = now;
                record.ModifiedByUserId = context.LocalUserId;
            }

            AuditRecorder.Record(scope, context, clock, AuditCategories.Record, RecordLifecycle.AuditActionFor(transition), $"record status changed from {current} to {plan.Status}", formRecordId, verified.Definition.FormDefinitionId,
                metadata: new { transition, fromStatus = current, toStatus = plan.Status, contentRevision = accepted.CommittedContentRevision, operationId = accepted.CommittedOperationId, reason });
            await scope.CommitAsync(cancellationToken).ConfigureAwait(false);
        }

        var etag = "\"" + accepted.CommittedContentRevision.ToString(System.Globalization.CultureInfo.InvariantCulture) + "-" + (accepted.CommittedOperationId?.ToString("N") ?? string.Empty) + "\"";
        return new RecordLifecycleResponse(formRecordId, transition, current, plan.Status, plan.SubmittedOn, accepted.CommittedContentRevision, accepted.CommittedOperationId, etag, RecordLifecycle.AllowedFor(plan.Status));
    }

    /// <summary>
    /// Submission validation (architecture §2.3): the whole current content must satisfy the current effective layout —
    /// types, required and required-if, visibility rules, active and organization-enabled options, dependent dropdowns,
    /// repeating-section bounds — and no document upload may still be in flight (OD-04). Decrypts to evaluate; writes nothing.
    /// </summary>
    private async Task ValidateForSubmissionAsync(RequestContext context, Guid formRecordId, VerifiedRecord verified, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var inFlight = await CountInFlightAsync(context, formRecordId, now, cancellationToken).ConfigureAwait(false);
        if (inFlight > 0)
        {
            throw new BusinessException("UPLOADS_IN_PROGRESS", 409, "documents are still being uploaded or processed; wait for them to finish before submitting");
        }

        var currentValues = verified.AllRows.OfType<FieldValueRowBase>().ToList();
        var groupInput = new CandidateBuilder.GroupInput(verified.Groups, verified.AllRows.OfType<FormRecordGroupRevision>().ToList(), null);
        _ = CandidateBuilder.ApplyValuesDetailed(currentValues, new Dictionary<string, string>(), verified.Layout, verified.Fields, verified.Options, null, context.OrganizationId, formRecordId, now, context.LocalUserId, verified.OptionsById, encryption, groups: groupInput);
    }

    private async Task<int> CountInFlightAsync(RequestContext context, Guid formRecordId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var scope = await store.BeginScopeAsync(cancellationToken).ConfigureAwait(false);
        return await scope.CountInFlightUploadSessionsAsync(context.OrganizationId, formRecordId, now - UploadLimits.SessionLifetime, cancellationToken).ConfigureAwait(false);
    }

    private static string? NormalizeReason(string transition, string? reason)
    {
        var text = reason?.Trim();
        if (transition == RecordLifecycle.Reopen && string.IsNullOrEmpty(text))
        {
            throw new BusinessException("REASON_REQUIRED", 422, "a reason is required to reopen a submitted record");
        }

        if (text is { Length: > RecordLifecycle.MaximumReasonLength })
        {
            throw new BusinessException("INVALID_REQUEST", 422, $"the reason can be at most {RecordLifecycle.MaximumReasonLength} characters");
        }

        return string.IsNullOrEmpty(text) ? null : text;
    }

    private Task DeniedAsync(RequestContext context, Guid formRecordId, string transition, string code, CancellationToken cancellationToken) =>
        AuditRecorder.RecordAsync(store, context, clock, AuditCategories.Record, AuditActions.RecordLifecycleDenied, "record status change refused", formRecordId, "Denied", new { transition, code }, cancellationToken);
}
