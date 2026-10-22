using EnterpriseForms.Application.Common;
using EnterpriseForms.Application.Common.Authority;
using EnterpriseForms.Application.Common.Persistence;
using EnterpriseForms.Application.Features.Audit;
using EnterpriseForms.Application.Features.FormRecords.Projection;
using EnterpriseForms.Application.Features.FormRecords.SharedEdits;
using EnterpriseForms.Application.Features.FormRecords.Staging;
using EnterpriseForms.Contracts.Features;
using EnterpriseForms.Domain.Features.Audit;
using EnterpriseForms.Domain.Features.FormRecords;

namespace EnterpriseForms.Application.Features.FormRecords.UpdateFormRecord;

/// <summary>
/// Update: verified read of the accepted revision (Edit action) → candidate → prepare → stage → commit, with optimistic
/// concurrency by expected content revision (ETag). Owners build the full typed candidate (unchanged items keep identity
/// and are re-signed per the v1 policy). Share editors (Phase C5) build a verbatim copy of the verified snapshot with only
/// their edited items replaced and send that snapshot as proof; the Authority decides the share scope at prepare and again
/// at commit. A partial projection never runs the owner candidate builder (C5-F2: nothing outside the scope is decrypted).
/// </summary>
public sealed class UpdateFormRecordHandler(VerifiedRecordReader reader, IBusinessStore store, RecordCandidateStager stager, TimeProvider clock)
{
    /// <summary>Audit description of a share editor's committed change (Phase C5 collaboration history).</summary>
    public const string SharedEditAuditDescription = "shared fields changed by a person with shared edit access";

    public async Task<FormRecordResponse> HandleAsync(RequestContext context, Guid formRecordId, UpdateFormRecordRequest request, string idempotencyKey, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(request);
        if (request.Values is null || (request.Values.Count == 0 && (request.Groups is null || request.Groups.Count == 0)))
        {
            throw new BusinessException("INVALID_REQUEST", 422, "at least one value or repeating-section change is required");
        }

        // The current accepted content must verify before it is used as the base of a new candidate (no building on tampered rows).
        var verified = await reader.ReadAsync(context, formRecordId, ProtectedAction.Edit, cancellationToken).ConfigureAwait(false);
        if (request.ExpectedContentRevision != verified.Head.CommittedContentRevision)
        {
            throw new BusinessException("CONTENT_REVISION_CONFLICT", 409, "expected_content_revision is stale");
        }

        return verified.IsComplete
            ? await OwnerUpdateAsync(context, formRecordId, request, idempotencyKey, verified, cancellationToken).ConfigureAwait(false)
            : await SharedUpdateAsync(context, formRecordId, request, idempotencyKey, verified, cancellationToken).ConfigureAwait(false);
    }

    private async Task<FormRecordResponse> OwnerUpdateAsync(RequestContext context, Guid formRecordId, UpdateFormRecordRequest request, string idempotencyKey, VerifiedRecord verified, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        var header = CandidateBuilder.CloneHeader(verified.Header);
        var currentValues = verified.AllRows.OfType<FieldValueRowBase>().ToList();
        var groupInput = new CandidateBuilder.GroupInput(verified.Groups, verified.AllRows.OfType<FormRecordGroupRevision>().ToList(), RecordGroupWrites.From(request.Groups));
        var applied = CandidateBuilder.ApplyValuesDetailed(currentValues, request.Values, verified.Layout, verified.Fields, verified.Options, null, context.OrganizationId, formRecordId, now, context.LocalUserId, verified.OptionsById, verified.Encryption, groups: groupInput);
        var candidateValues = applied.Rows;
        var rows = new List<ISignedContentRow>(candidateValues);
        rows.AddRange(applied.Occurrences);
        rows.AddRange(verified.AllRows.OfType<Domain.Features.Documents.FileAttachmentVersion>().Select(a => CandidateBuilder.CloneAttachment(a)));
        CandidateBuilder.EnsureWithinCeiling(rows.Count + 1);

        var accepted = await stager.StageAndCommitAsync(context, verified.Head, new RecordCandidateStager.CandidateInput(header, rows), idempotencyKey, cancellationToken).ConfigureAwait(false);
        await RecordAcceptedAsync(context, formRecordId, verified, accepted, now, request.Values.Keys.OrderBy(k => k, StringComparer.Ordinal).ToList(), "owner",
            new { clearedByRule = applied.ClearedByRuleFieldKeys, groupChanges = RecordGroupWrites.AuditOf(applied.GroupChanges) }, cancellationToken).ConfigureAwait(false);

        var fieldsById = verified.FieldsById;
        var optionsById = verified.OptionsById;
        return new FormRecordResponse(formRecordId, context.OrganizationId, header.FormDefinitionId, header.RecordStatus, accepted.CommittedContentRevision, accepted.CommittedOperationId, accepted.AuthorizationRevision, true,
            candidateValues.OrderBy(v => fieldsById[v.FieldDefinitionId].FieldKey, StringComparer.Ordinal).Select(v => new FieldValueResponse(v.FieldDefinitionId, fieldsById[v.FieldDefinitionId].FieldKey, v.FieldValueItemId, Values.FieldValueCodec.Format(v, optionsById, verified.Encryption), fieldsById[v.FieldDefinitionId].FieldDataType, ApplicationEncrypted: v is FieldEncryptedValue, FormRecordGroupId: v.FormRecordGroupId)).ToList(),
            "Committed(" + accepted.ArchiveState + ")",
            RecordGroupProjection.Build(verified.Groups, verified.Layout, applied.Occurrences));
    }

    private async Task<FormRecordResponse> SharedUpdateAsync(RequestContext context, Guid formRecordId, UpdateFormRecordRequest request, string idempotencyKey, VerifiedRecord verified, CancellationToken cancellationToken)
    {
        // D10: the Authority's switch, reported by the verification; checked before any candidate is built or anything decrypted.
        if (!verified.Verification.ContributorWritesEnabled)
        {
            throw new BusinessException("CONTRIBUTOR_WRITES_DISABLED", 403, "changing shared fields is not enabled");
        }

        var now = clock.GetUtcNow();
        var built = SharedFieldWriteBuilder.Build(verified, request.Values, request.Groups, context.OrganizationId, formRecordId, now, context.LocalUserId);
        AcceptedContentRevision accepted;
        try
        {
            accepted = await stager.StageAndCommitAsync(context, verified.Head, new RecordCandidateStager.CandidateInput(built.Header, built.Rows), idempotencyKey, cancellationToken,
                currentItems: verified.ObservedItems, preserveSchemaVersions: true).ConfigureAwait(false);
        }
        catch (AuthorityClientException ex) when (ex.ErrorCode == "CONTRIBUTOR_WRITES_DISABLED")
        {
            throw new BusinessException("CONTRIBUTOR_WRITES_DISABLED", 403, "changing shared fields is not enabled");
        }
        catch (AuthorityClientException ex) when (ex.HttpStatusCode is 403 or 422 && ex.ErrorCode is "FORBIDDEN" or "INVALID_COMMAND")
        {
            // The Authority's scope decision is final; its reason codes stay in the Authority journal and logs, not in the response.
            throw new BusinessException("CHANGE_NOT_PERMITTED", 403, "this change is not permitted with your access");
        }

        await RecordAcceptedAsync(context, formRecordId, verified, accepted, now, built.EditedFieldKeys.OrderBy(k => k, StringComparer.Ordinal).ToList(), "share", null, cancellationToken).ConfigureAwait(false);

        // Release only what the editor may read: the readable items of the new revision (unchanged copies + edited rows).
        var fieldsById = verified.FieldsById;
        var readableItems = SharedFieldWriteBuilder.ReadableRows(verified).Select(r => r.FieldValueItemId).ToHashSet();
        var released = built.Rows.OfType<FieldValueRowBase>().Where(v => v.FormRecordGroupId is null && readableItems.Contains(v.FieldValueItemId) && fieldsById.ContainsKey(v.FieldDefinitionId)).ToList();
        return new FormRecordResponse(formRecordId, context.OrganizationId, built.Header.FormDefinitionId, "(partial)", accepted.CommittedContentRevision, accepted.CommittedOperationId, accepted.AuthorizationRevision, false,
            released.OrderBy(v => fieldsById[v.FieldDefinitionId].FieldKey, StringComparer.Ordinal).Select(v => new FieldValueResponse(v.FieldDefinitionId, fieldsById[v.FieldDefinitionId].FieldKey, v.FieldValueItemId, Values.FieldValueCodec.Format(v, verified.OptionsById, verified.Encryption), fieldsById[v.FieldDefinitionId].FieldDataType, ApplicationEncrypted: v is FieldEncryptedValue)).ToList(),
            "Committed(" + accepted.ArchiveState + ")",
            []);
    }

    private async Task RecordAcceptedAsync(RequestContext context, Guid formRecordId, VerifiedRecord verified, AcceptedContentRevision accepted, DateTimeOffset now, IReadOnlyList<string> changedFieldKeys, string editBasis, object? extra, CancellationToken cancellationToken)
    {
        await using var scope = await store.BeginScopeAsync(cancellationToken).ConfigureAwait(false);
        var record = await scope.FindFormRecordAsync(context.OrganizationId, formRecordId, cancellationToken).ConfigureAwait(false);
        if (record is not null && accepted.CommittedOperationId is not null && await scope.ProjectAcceptedHeadAsync(context.OrganizationId, formRecordId, accepted.CommittedContentRevision, accepted.CommittedOperationId, cancellationToken).ConfigureAwait(false))
        {
            record.ModifiedOn = now;
            record.ModifiedByUserId = context.LocalUserId;
            record.LastEditedFormVersionId = verified.LayoutVersion.FormVersionId;
        }

        // Shared edits are described distinctly so owners can see collaboration history (values and unshared fields are never logged).
        var description = editBasis == "share" ? SharedEditAuditDescription : "record content revision committed";
        AuditRecorder.Record(scope, context, clock, AuditCategories.Record, AuditActions.RecordUpdated, description, formRecordId, verified.Definition.FormDefinitionId,
            metadata: new { contentRevision = accepted.CommittedContentRevision, changedFieldKeys, complete = verified.IsComplete, editBasis, detail = extra });
        await scope.CommitAsync(cancellationToken).ConfigureAwait(false);
    }
}
