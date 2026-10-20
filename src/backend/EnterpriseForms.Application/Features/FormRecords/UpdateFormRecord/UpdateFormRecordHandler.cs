using EnterpriseForms.Application.Common;
using EnterpriseForms.Application.Common.Authority;
using EnterpriseForms.Application.Common.Persistence;
using EnterpriseForms.Application.Features.Audit;
using EnterpriseForms.Application.Features.FormRecords.Projection;
using EnterpriseForms.Application.Features.FormRecords.Staging;
using EnterpriseForms.Contracts.Features;
using EnterpriseForms.Domain.Features.Audit;
using EnterpriseForms.Domain.Features.FormRecords;

namespace EnterpriseForms.Application.Features.FormRecords.UpdateFormRecord;

/// <summary>
/// Update: verified read of the accepted revision (Edit action) → full typed candidate snapshot (unchanged items keep
/// identity and are re-signed per the v1 policy; attachments carried forward) → prepare → stage → commit. Scope limits
/// (owner vs share contributor) are enforced by the authority at prepare and commit; optimistic concurrency by
/// expected content revision (ETag).
/// </summary>
public sealed class UpdateFormRecordHandler(VerifiedRecordReader reader, IBusinessStore store, RecordCandidateStager stager, TimeProvider clock)
{
    public async Task<FormRecordResponse> HandleAsync(RequestContext context, Guid formRecordId, UpdateFormRecordRequest request, string idempotencyKey, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(request);
        if (request.Values.Count == 0)
        {
            throw new BusinessException("INVALID_REQUEST", 422, "at least one value is required");
        }

        // The current accepted content must verify before it is used as the base of a new candidate (no building on tampered rows).
        var verified = await reader.ReadAsync(context, formRecordId, ProtectedAction.Edit, cancellationToken).ConfigureAwait(false);
        if (request.ExpectedContentRevision != verified.Head.CommittedContentRevision)
        {
            throw new BusinessException("CONTENT_REVISION_CONFLICT", 409, "expected_content_revision is stale");
        }

        var editable = verified.IsComplete ? null : verified.AuthorizedRows.Select(r => r.ItemId).ToHashSet();
        var now = clock.GetUtcNow();
        var header = CandidateBuilder.CloneHeader(verified.Header);
        var currentValues = verified.AllRows.OfType<FieldValueRowBase>().ToList();
        var applied = CandidateBuilder.ApplyValuesDetailed(currentValues, request.Values, verified.Layout, verified.Fields, verified.Options, editable, context.OrganizationId, formRecordId, now, context.LocalUserId, verified.OptionsById, verified.Encryption);
        var candidateValues = applied.Rows;
        var rows = new List<ISignedContentRow>(candidateValues);
        rows.AddRange(verified.AllRows.OfType<Domain.Features.Documents.FileAttachmentVersion>().Select(a => CandidateBuilder.CloneAttachment(a)));

        var accepted = await stager.StageAndCommitAsync(context, verified.Head, new RecordCandidateStager.CandidateInput(header, rows), idempotencyKey, cancellationToken).ConfigureAwait(false);
        await using (var scope = await store.BeginScopeAsync(cancellationToken).ConfigureAwait(false))
        {
            var record = await scope.FindFormRecordAsync(context.OrganizationId, formRecordId, cancellationToken).ConfigureAwait(false);
            if (record is not null && accepted.CommittedOperationId is { } acceptedOperation && record.RecordAcceptedContent(accepted.CommittedContentRevision, acceptedOperation))
            {
                record.ModifiedOn = now;
                record.ModifiedByUserId = context.LocalUserId;
                record.LastEditedFormVersionId = verified.LayoutVersion.FormVersionId;
            }

            AuditRecorder.Record(scope, context, clock, AuditCategories.Record, AuditActions.RecordUpdated, "record content revision committed", formRecordId, verified.Definition.FormDefinitionId,
                metadata: new { contentRevision = accepted.CommittedContentRevision, changedFieldKeys = request.Values.Keys.OrderBy(k => k, StringComparer.Ordinal).ToList(), complete = verified.IsComplete, clearedByRule = applied.ClearedByRuleFieldKeys });
            await scope.CommitAsync(cancellationToken).ConfigureAwait(false);
        }

        var fieldsById = verified.FieldsById;
        var optionsById = verified.OptionsById;
        var released = editable is null ? candidateValues : candidateValues.Where(v => editable.Contains(v.FieldValueItemId)).ToList();
        return new FormRecordResponse(formRecordId, context.OrganizationId, header.FormDefinitionId, header.RecordStatus, accepted.CommittedContentRevision, accepted.CommittedOperationId, accepted.AuthorizationRevision, editable is null,
            released.OrderBy(v => fieldsById[v.FieldDefinitionId].FieldKey, StringComparer.Ordinal).Select(v => new FieldValueResponse(v.FieldDefinitionId, fieldsById[v.FieldDefinitionId].FieldKey, v.FieldValueItemId, Values.FieldValueCodec.Format(v, optionsById, verified.Encryption), fieldsById[v.FieldDefinitionId].FieldDataType, ApplicationEncrypted: v is Domain.Features.FormRecords.FieldEncryptedValue)).ToList(),
            "Committed(" + accepted.ArchiveState + ")");
    }
}
