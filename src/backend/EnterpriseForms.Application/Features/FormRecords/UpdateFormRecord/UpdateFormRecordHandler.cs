using EnterpriseForms.Application.Common;
using EnterpriseForms.Application.Common.Authority;
using EnterpriseForms.Application.Common.Persistence;
using EnterpriseForms.Application.Features.FormRecords.Staging;
using EnterpriseForms.Contracts.Features;
using EnterpriseForms.Domain.Features.FormRecords;

namespace EnterpriseForms.Application.Features.FormRecords.UpdateFormRecord;

/// <summary>
/// Update: verified read of the accepted revision → full candidate snapshot (unchanged items keep their item identity and
/// are re-signed per the v1 policy; changed items keep identity with new content; new keys create new items) → prepare →
/// stage → commit. Scope limits (owner vs share contributor) are enforced by the authority at prepare and commit.
/// </summary>
public sealed class UpdateFormRecordHandler(IBusinessStore store, IAuthorizationAuthorityClient authority, RecordCandidateStager stager, TimeProvider clock)
{
    public async Task<FormRecordResponse> HandleAsync(RequestContext context, Guid formRecordId, UpdateFormRecordRequest request, string idempotencyKey, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(request);
        var user = context.ToDelegatedUser();
        var head = await authority.GetCommittedContentRevisionAsync(user, context.OrganizationId, formRecordId, cancellationToken).ConfigureAwait(false);
        if (head.CommittedContentRevision == 0 || head.CommittedOperationId is null)
        {
            throw new BusinessException("NO_COMMITTED_CONTENT", 404, "record has no accepted content");
        }

        if (request.ExpectedContentRevision != head.CommittedContentRevision)
        {
            throw new BusinessException("CONTENT_REVISION_CONFLICT", 409, "expected_content_revision is stale");
        }

        // The current accepted content must verify before it is used as the base of a new candidate (no building on tampered rows).
        var observed = await stager.ReadStagedAsync(formRecordId, head.CommittedOperationId.Value, (uint)head.IntegrityKeyVersion, cancellationToken).ConfigureAwait(false);
        var verification = await authority.VerifyRecordProjectionAsync(user, new VerifyRecordProjectionRequest(context.OrganizationId, formRecordId, ProtectedAction.Edit, observed), cancellationToken).ConfigureAwait(false);
        if (!verification.Verified)
        {
            throw new BusinessException("INTEGRITY_VERIFICATION_FAILED", 423, "record content failed integrity verification: " + verification.FailureReasonCode);
        }

        FormRecordRevision current;
        List<FieldStringValue> currentValues;
        List<Domain.Features.FormDefinitions.FieldDefinition> fields;
        await using (var scope = await store.BeginScopeAsync(cancellationToken).ConfigureAwait(false))
        {
            current = await scope.FindStagedHeaderAsync(formRecordId, head.CommittedOperationId.Value, cancellationToken).ConfigureAwait(false)
                      ?? throw new BusinessException("INTEGRITY_VERIFICATION_FAILED", 423, "committed header missing");
            currentValues = await scope.GetStagedValuesAsync(formRecordId, head.CommittedOperationId.Value, cancellationToken).ConfigureAwait(false);
            fields = await scope.GetFieldDefinitionsAsync(current.FormDefinitionId, cancellationToken).ConfigureAwait(false);
        }

        var fieldByKey = fields.ToDictionary(f => f.FieldKey, StringComparer.Ordinal);
        if (request.Values.Count == 0 || request.Values.Keys.Any(k => !fieldByKey.ContainsKey(k)) || request.Values.Values.Any(v => v is null || v.Length > 4000))
        {
            throw new BusinessException("INVALID_REQUEST", 422, "values must reference known field keys (max 4000 chars each)");
        }

        var editable = verification.IsCompleteProjection ? null : verification.AuthorizedItems.Select(i => i.ItemId).ToHashSet();
        var now = clock.GetUtcNow();
        var header = new FormRecordRevision
        {
            FormRecordRevisionId = Guid.CreateVersion7(),
            TenantId = current.TenantId,
            FormRecordId = formRecordId,
            FormDefinitionId = current.FormDefinitionId,
            CreatedFormVersionId = current.CreatedFormVersionId,
            CreatedByUserId = current.CreatedByUserId,
            RecordStatus = current.RecordStatus,
            SubmittedOn = current.SubmittedOn,
        };

        var byField = currentValues.ToDictionary(v => v.FieldDefinitionId);
        var candidateValues = new List<FieldStringValue>();
        foreach (var existing in currentValues)
        {
            var key = fields.First(f => f.FieldDefinitionId == existing.FieldDefinitionId).FieldKey;
            var changed = request.Values.TryGetValue(key, out var newValue) && !string.Equals(newValue, existing.StringValue, StringComparison.Ordinal);
            if (changed && editable is not null && !editable.Contains(existing.FieldValueItemId))
            {
                throw new BusinessException("FORBIDDEN", 403, $"field '{key}' is outside the caller's edit scope");
            }

            candidateValues.Add(new FieldStringValue
            {
                FieldStringValueId = Guid.CreateVersion7(),
                TenantId = existing.TenantId,
                FormRecordId = formRecordId,
                FieldDefinitionId = existing.FieldDefinitionId,
                FormRecordGroupId = existing.FormRecordGroupId,
                FieldValueItemId = existing.FieldValueItemId,
                StringValue = changed ? newValue! : existing.StringValue,
                CreatedOn = changed ? now : existing.CreatedOn,
                CreatedByUserId = changed ? context.LocalUserId : existing.CreatedByUserId,
            });
        }

        foreach (var (key, value) in request.Values.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            var field = fieldByKey[key];
            if (byField.ContainsKey(field.FieldDefinitionId))
            {
                continue;
            }

            if (editable is not null)
            {
                throw new BusinessException("FORBIDDEN", 403, "adding items requires record ownership");
            }

            candidateValues.Add(new FieldStringValue
            {
                FieldStringValueId = Guid.CreateVersion7(),
                TenantId = current.TenantId,
                FormRecordId = formRecordId,
                FieldDefinitionId = field.FieldDefinitionId,
                FormRecordGroupId = null,
                FieldValueItemId = Guid.CreateVersion7(),
                StringValue = value,
                CreatedOn = now,
                CreatedByUserId = context.LocalUserId,
            });
        }

        var accepted = await stager.StageAndCommitAsync(context, head, new RecordCandidateStager.CandidateInput(header, candidateValues), idempotencyKey, cancellationToken).ConfigureAwait(false);
        var fieldKeys = fields.ToDictionary(f => f.FieldDefinitionId, f => f.FieldKey);
        var released = editable is null ? candidateValues : candidateValues.Where(v => editable.Contains(v.FieldValueItemId)).ToList();
        return new FormRecordResponse(formRecordId, context.OrganizationId, header.FormDefinitionId, header.RecordStatus, accepted.CommittedContentRevision, accepted.CommittedOperationId, accepted.AuthorizationRevision, editable is null,
            released.OrderBy(v => fieldKeys[v.FieldDefinitionId], StringComparer.Ordinal).Select(v => new FieldValueResponse(v.FieldDefinitionId, fieldKeys[v.FieldDefinitionId], v.FieldValueItemId, v.StringValue)).ToList(),
            "Committed(" + accepted.ArchiveState + ")");
    }
}
