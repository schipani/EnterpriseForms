using EnterpriseForms.Application.Common;
using EnterpriseForms.Application.Common.Authority;
using EnterpriseForms.Application.Common.Persistence;
using EnterpriseForms.Application.Features.Audit;
using EnterpriseForms.Application.Features.FormDefinitions.Layout;
using EnterpriseForms.Application.Features.FormRecords.Projection;
using EnterpriseForms.Application.Features.FormRecords.Staging;
using EnterpriseForms.Contracts.Features;
using EnterpriseForms.Domain.Features.Audit;
using EnterpriseForms.Domain.Features.FormRecords;

namespace EnterpriseForms.Application.Features.FormRecords.History;

/// <summary>
/// Phase C6 M3 — restore an earlier version as a NEW revision (architecture §4.2; OD-11, OD-12, OD-13; I-R). Steps:
/// <list type="number">
/// <item>Verified complete read of the current head with the Edit action: anyone who is not an effective owner now
/// (including a field-share contributor) gets the same 404 as for a missing record.</item>
/// <item>The current status must be Draft (Submitted and Archived are reopened or unarchived first, M1).</item>
/// <item>The head must still be the one reviewed (expected revision; the Authority's compare-and-swap decides at commit).</item>
/// <item>The source version and its operation come from the Authority's history (AC-1), never from business rows; its
/// exact rows are verified against the manifest accepted for it (AC-4: tampering quarantines the record).</item>
/// <item>The plan (<see cref="RestorePlanner"/>) becomes one complete owner write: current form validation (all-or-nothing),
/// current protection policy (fresh envelopes; OD-13 acknowledgement for released fields), fresh identities for anything
/// that exists only in the source; documents and the header are carried from the current head.</item>
/// <item>Prepare → stage → read back → commit as an ordinary owner operation, then audit <c>RECORD_VERSION_RESTORED</c> with the
/// reason (never values). An idempotent retry returns the committed revision without a second commit.</item>
/// </list>
/// No Authority change (§5.1). AC-3 is deferred, so the restore provenance is recorded in the local activity log only.
/// </summary>
public sealed class RecordRestoreHandler(VerifiedRecordReader reader, IBusinessStore store, IAuthorizationAuthorityClient authority, RecordCandidateStager stager, TimeProvider clock)
{
    public const int MaximumReasonLength = 500;

    private sealed record Loaded(VerifiedRecord Current, VerifiedRecord Source, ContentHistoryEntry Entry, RestorePlanner.RestorePlan Plan);

    public async Task<RestorePreviewResponse> PreviewAsync(RequestContext context, Guid formRecordId, long contentRevision, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var current = await ReadCurrentAsync(context, formRecordId, cancellationToken).ConfigureAwait(false);
        var loaded = await LoadSourceAsync(context, formRecordId, contentRevision, null, current, cancellationToken).ConfigureAwait(false);
        var plan = loaded.Plan;
        string? blockedCode = null;
        string? blocked = null;
        RestoreValidationResponse? validation = null;
        if (!Lifecycle.RecordLifecycle.IsContentEditable(current.Header.RecordStatus))
        {
            blockedCode = "RECORD_NOT_EDITABLE";
            blocked = $"This record is {current.Header.RecordStatus.ToLowerInvariant()}. Reopen or unarchive it as a draft before restoring a version.";
        }
        else if (plan.Unsupported is not null)
        {
            blockedCode = "RESTORE_UNSUPPORTED";
            blocked = "This version can't be restored exactly: " + plan.Unsupported + ".";
        }
        else if (!plan.HasChanges)
        {
            blockedCode = "RESTORE_NO_CHANGES";
            blocked = "This version has the same values as the current record; restoring it would change nothing.";
        }
        else
        {
            // Dry run of the exact write the restore would make (nothing is staged): current form rules, all-or-nothing.
            try
            {
                Apply(context, formRecordId, current, plan);
            }
            catch (BusinessException ex) when (ex.HttpStatusCode is 403 or 422)
            {
                validation = new RestoreValidationResponse(ex.ErrorCode, ex.Message, ex.FieldKeys);
                blockedCode = "RESTORE_VALIDATION_FAILED";
                blocked = "The restored values would not pass the current form's rules. Nothing can be restored until this is resolved.";
            }
        }

        var names = await NamesAsync(context, cancellationToken).ConfigureAwait(false);
        return new RestorePreviewResponse(formRecordId, RecordHistoryHandler.ToItem(loaded.Entry, names, new Dictionary<long, (string, Guid?, string)>()), loaded.Entry.CommittedOperationId,
            current.Head.CommittedContentRevision, current.ETag, current.Header.RecordStatus, blockedCode is null, blockedCode, blocked, plan.HasChanges,
            plan.Fields, plan.Sections, current.AuthorizedAttachments.Count(), current.Layout.Elements.Any(e => e.Kind == LayoutElementKinds.RelatedFormList), validation, plan.PlaintextAcknowledgementFieldKeys);
    }

    public async Task<RecordRestoreResponse> RestoreAsync(RequestContext context, Guid formRecordId, long contentRevision, RecordRestoreRequest request, string idempotencyKey, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(request);

        // Authorization first: a caller who may not restore learns nothing about what a valid request looks like.
        var current = await ReadCurrentAsync(context, formRecordId, cancellationToken).ConfigureAwait(false);

        // Idempotent replay (a retry after a lost response): this request's operation is already the accepted head.
        var stagerKey = "restore:" + idempotencyKey;
        if (current.Head.CommittedOperationId is { } headOperation && headOperation == RecordCandidateStager.DeriveOperationId(context.OrganizationId, formRecordId, stagerKey))
        {
            return new RecordRestoreResponse(formRecordId, contentRevision, current.Head.CommittedContentRevision, headOperation, current.ETag, [], []);
        }

        var reason = request.RestoreReason?.Trim();
        if (string.IsNullOrEmpty(reason) || reason.Length > MaximumReasonLength)
        {
            throw new BusinessException("REASON_REQUIRED", 422, $"a reason (1–{MaximumReasonLength} characters) is required to restore a version") { FieldKeys = ["restoreReason"] };
        }

        Lifecycle.RecordLifecycle.RequireContentEditable(current);
        if (request.ExpectedCurrentContentRevision != current.Head.CommittedContentRevision)
        {
            throw new BusinessException("CONTENT_REVISION_CONFLICT", 409, "the record changed since you reviewed the restore; review it again");
        }

        var loaded = await LoadSourceAsync(context, formRecordId, contentRevision, request.SourceOperationId, current, cancellationToken).ConfigureAwait(false);
        var plan = loaded.Plan;
        if (plan.Unsupported is not null)
        {
            throw new BusinessException("RESTORE_UNSUPPORTED", 422, plan.Unsupported);
        }

        if (!plan.HasChanges)
        {
            throw new BusinessException("RESTORE_NO_CHANGES", 422, "this version has the same values as the current record");
        }

        if (plan.PlaintextAcknowledgementFieldKeys.Count > 0 && !request.AcknowledgePlaintext)
        {
            throw new BusinessException("PLAINTEXT_ACKNOWLEDGEMENT_REQUIRED", 422, "some restored values were encrypted in that version and would now be stored without application encryption under the field's current policy; confirm this explicitly") { FieldKeys = plan.PlaintextAcknowledgementFieldKeys };
        }

        CandidateBuilder.ApplyResult applied;
        try
        {
            applied = Apply(context, formRecordId, current, plan);
        }
        catch (BusinessException ex) when (ex.HttpStatusCode is 403 or 422)
        {
            throw new BusinessException("RESTORE_VALIDATION_FAILED", 422, "the restored values do not pass the current form's rules (" + ex.ErrorCode + "): " + ex.Message) { FieldKeys = ex.FieldKeys };
        }

        var header = CandidateBuilder.CloneHeader(current.Header); // status, creator and links stay as they are now
        var rows = new List<ISignedContentRow>(applied.Rows);
        rows.AddRange(applied.Occurrences);
        rows.AddRange(current.AllRows.OfType<Domain.Features.Documents.FileAttachmentVersion>().Select(a => CandidateBuilder.CloneAttachment(a))); // documents are never restored
        CandidateBuilder.EnsureWithinCeiling(rows.Count + 1);

        AcceptedContentRevision accepted;
        try
        {
            accepted = await stager.StageAndCommitAsync(context, current.Head, new RecordCandidateStager.CandidateInput(header, rows), stagerKey, cancellationToken).ConfigureAwait(false);
        }
        catch (AuthorityClientException ex) when (ex.ErrorCode == "FIELD_PROTECTION_CHANGED")
        {
            throw new BusinessException("FIELD_PROTECTION_MIGRATION_PENDING", 409, "a protection change is still being applied to this record; try again shortly");
        }

        var now = clock.GetUtcNow();
        await using (var scope = await store.BeginScopeAsync(cancellationToken).ConfigureAwait(false))
        {
            var record = await scope.FindFormRecordAsync(context.OrganizationId, formRecordId, cancellationToken).ConfigureAwait(false);
            if (record is not null && accepted.CommittedOperationId is not null && await scope.ProjectAcceptedHeadAsync(context.OrganizationId, formRecordId, accepted.CommittedContentRevision, accepted.CommittedOperationId, cancellationToken).ConfigureAwait(false))
            {
                record.ModifiedOn = now;
                record.ModifiedByUserId = context.LocalUserId;
                record.LastEditedFormVersionId = current.LayoutVersion.FormVersionId;
            }

            AuditRecorder.Record(scope, context, clock, AuditCategories.Record, AuditActions.RecordVersionRestored, $"earlier version {loaded.Entry.ContentRevision} restored as a new revision", formRecordId, current.Definition.FormDefinitionId,
                metadata: new
                {
                    contentRevision = accepted.CommittedContentRevision,
                    operationId = accepted.CommittedOperationId,
                    sourceContentRevision = loaded.Entry.ContentRevision,
                    sourceOperationId = loaded.Entry.CommittedOperationId,
                    changedFieldKeys = plan.ChangedFieldKeys,
                    clearedFieldKeys = plan.ClearedFieldKeys,
                    plaintextAcknowledged = plan.PlaintextAcknowledgementFieldKeys.Count > 0,
                    groupChanges = RecordGroupWrites.AuditOf(applied.GroupChanges),
                    reason,
                });
            await scope.CommitAsync(cancellationToken).ConfigureAwait(false);
        }

        var etag = "\"" + accepted.CommittedContentRevision.ToString(System.Globalization.CultureInfo.InvariantCulture) + "-" + (accepted.CommittedOperationId?.ToString("N") ?? string.Empty) + "\"";
        return new RecordRestoreResponse(formRecordId, loaded.Entry.ContentRevision, accepted.CommittedContentRevision, accepted.CommittedOperationId, etag, plan.ChangedFieldKeys, plan.ClearedFieldKeys);
    }

    private async Task<VerifiedRecord> ReadCurrentAsync(RequestContext context, Guid formRecordId, CancellationToken cancellationToken)
    {
        var current = await reader.ReadAsync(context, formRecordId, ProtectedAction.Edit, cancellationToken).ConfigureAwait(false);
        if (!current.IsComplete)
        {
            // A field-share reader or contributor can never restore; same answer as for a record that does not exist.
            throw new BusinessException("NOT_FOUND", 404, "record not found");
        }

        return current;
    }

    private async Task<Loaded> LoadSourceAsync(RequestContext context, Guid formRecordId, long contentRevision, Guid? expectedSourceOperation, VerifiedRecord current, CancellationToken cancellationToken)
    {
        if (contentRevision < 1 || contentRevision > current.Head.CommittedContentRevision)
        {
            throw new BusinessException("VERSION_NOT_FOUND", 404, "this version does not exist");
        }

        if (contentRevision == current.Head.CommittedContentRevision)
        {
            throw new BusinessException("VERSION_IS_CURRENT", 409, "this is the current version; there is nothing to restore");
        }

        // The version and its operation come from the Authority's accepted history (it also re-checks full read).
        var page = await authority.GetContentHistoryAsync(context.ToDelegatedUser(), context.OrganizationId, formRecordId, contentRevision - 1, 1, cancellationToken).ConfigureAwait(false);
        var entry = page.Entries.FirstOrDefault(e => e.ContentRevision == contentRevision)
                    ?? throw new BusinessException("VERSION_NOT_FOUND", 404, "this version does not exist");
        if (expectedSourceOperation is { } expected && expected != entry.CommittedOperationId)
        {
            throw new BusinessException("VERSION_CHANGED", 409, "the version you reviewed is not the version the Authority lists; review the restore again");
        }

        if (!entry.VerifiableNow)
        {
            throw new BusinessException("HISTORIC_VERSION_UNVERIFIABLE", 503, "this version can't be verified with the keys that are accepted now; nothing was restored");
        }

        var source = await reader.ReadHistoricAsync(context, formRecordId, entry.CommittedOperationId, cancellationToken).ConfigureAwait(false);
        if (!source.IsComplete || source.Verification.CommittedContentRevision != entry.ContentRevision || source.Verification.CommittedOperationId != entry.CommittedOperationId)
        {
            throw new BusinessException("INTEGRITY_VERIFICATION_FAILED", 423, "the verified version does not match the requested version; nothing was restored");
        }

        var plan = RestorePlanner.Build(
            new RestorePlanner.Snapshot(current.AllRows.OfType<FieldValueRowBase>().ToList(), current.Occurrences),
            new RestorePlanner.Snapshot(source.AllRows.OfType<FieldValueRowBase>().ToList(), source.Occurrences),
            current.Layout, current.Fields, current.Groups, current.OptionsById, current.Encryption);
        return new Loaded(current, source, entry, plan);
    }

    /// <summary>The complete owner write of the restore (validates; protects every value under the current policy; never stages).</summary>
    private CandidateBuilder.ApplyResult Apply(RequestContext context, Guid formRecordId, VerifiedRecord current, RestorePlanner.RestorePlan plan) =>
        CandidateBuilder.ApplyValuesDetailed(current.AllRows.OfType<FieldValueRowBase>().ToList(), plan.Values, current.Layout, current.Fields, current.Options, null, context.OrganizationId, formRecordId, clock.GetUtcNow(), context.LocalUserId, current.OptionsById, current.Encryption,
            groups: new CandidateBuilder.GroupInput(current.Groups, current.Occurrences, plan.GroupWrites.Count == 0 ? null : plan.GroupWrites));

    private async Task<Dictionary<Guid, string>> NamesAsync(RequestContext context, CancellationToken cancellationToken)
    {
        await using var scope = await store.BeginScopeAsync(cancellationToken).ConfigureAwait(false);
        return (await scope.GetUsersAsync(context.OrganizationId, cancellationToken).ConfigureAwait(false)).ToDictionary(u => u.UserId, u => u.DisplayName);
    }
}
