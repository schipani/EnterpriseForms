using System.Text.Json;
using EnterpriseForms.Application.Common;
using EnterpriseForms.Application.Common.Authority;
using EnterpriseForms.Application.Common.Persistence;
using EnterpriseForms.Application.Features.FormDefinitions.CreateFormDefinition;
using EnterpriseForms.Application.Features.FormRecords.Staging;
using EnterpriseForms.Contracts.Features;
using EnterpriseForms.Domain.Features.FormRecords;

namespace EnterpriseForms.Application.Features.FormRecords.CreateFormRecord;

/// <summary>Create: authority RegisterRecord (owner = creator or an eligible team) → Effective → signed candidate v1 → staged → committed head.</summary>
public sealed class CreateFormRecordHandler(IBusinessStore store, IAuthorizationAuthorityClient authority, AuthorityCommandAwaiter awaiter, RecordCandidateStager stager, TimeProvider clock)
{
    public async Task<FormRecordResponse> HandleAsync(RequestContext context, Guid formDefinitionId, CreateFormRecordRequest request, string idempotencyKey, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(request);

        Domain.Features.FormDefinitions.FormDefinition definition;
        List<Domain.Features.FormDefinitions.FieldDefinition> fields;
        await using (var scope = await store.BeginScopeAsync(cancellationToken).ConfigureAwait(false))
        {
            definition = await scope.FindFormDefinitionAsync(context.OrganizationId, formDefinitionId, cancellationToken).ConfigureAwait(false)
                         ?? throw new BusinessException("NOT_FOUND", 404, "form definition not found");
            fields = await scope.GetFieldDefinitionsAsync(formDefinitionId, cancellationToken).ConfigureAwait(false);
        }

        var fieldByKey = fields.ToDictionary(f => f.FieldKey, StringComparer.Ordinal);
        if (request.Values.Count == 0 || request.Values.Keys.Any(k => !fieldByKey.ContainsKey(k)) || request.Values.Values.Any(v => v is null || v.Length > 4000))
        {
            throw new BusinessException("INVALID_REQUEST", 422, "values must reference known field keys (max 4000 chars each)");
        }

        var createDecision = await authority.CheckPermissionAsync(context.ToDelegatedUser(), new PermissionCheckRequest(context.OrganizationId, new ResourceReference(ProtectedResourceKind.FormDefinition, formDefinitionId), ProtectedAction.CreateRecord), cancellationToken).ConfigureAwait(false);
        if (!createDecision.Allowed)
        {
            throw new BusinessException("FORBIDDEN", 403, "not permitted to create records for this form");
        }

        var formRecordId = CreateFormDefinitionHandler.DeterministicId(idempotencyKey, "record");
        var payload = JsonSerializer.SerializeToElement(new { form_record_id = formRecordId, form_definition_id = formDefinitionId, owner_team_id = request.OwnerTeamId });
        var command = await awaiter.SubmitAndAwaitAsync(context.ToDelegatedUser(), new SecurityCommandRequest(
            context.OrganizationId, SecurityCommandKind.RegisterRecord, new ResourceReference(ProtectedResourceKind.FormRecord, formRecordId), createDecision.AuthorizationRevision, payload, "create record"),
            "record:" + idempotencyKey, cancellationToken).ConfigureAwait(false);
        if (command.CommandStatus != SecurityCommandStatus.Effective)
        {
            throw new PendingCommandException(new CommandOutcome(command.CommandId, command.CommandStatus.ToString(), command.AuthorizationRevision, false));
        }

        var now = clock.GetUtcNow();
        await using (var scope = await store.BeginScopeAsync(cancellationToken).ConfigureAwait(false))
        {
            if (await scope.FindFormRecordAsync(context.OrganizationId, formRecordId, cancellationToken).ConfigureAwait(false) is null)
            {
                scope.AddFormRecord(new FormRecord
                {
                    FormRecordId = formRecordId,
                    TenantId = context.OrganizationId,
                    FormDefinitionId = formDefinitionId,
                    CreatedFormVersionId = definition.CurrentFormVersionId ?? Guid.Empty,
                    RecordStatus = "Draft",
                    CreatedByUserId = context.LocalUserId,
                    OwnerUserId = request.OwnerTeamId is null ? context.LocalUserId : null,
                    OwnerTeamId = request.OwnerTeamId,
                    CreatedOn = now,
                    ModifiedOn = now,
                    ModifiedByUserId = context.LocalUserId,
                    RecordRevision = 1,
                    AuthorityCommandId = command.CommandId,
                });
                await scope.CommitAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        var head = await authority.GetCommittedContentRevisionAsync(context.ToDelegatedUser(), context.OrganizationId, formRecordId, cancellationToken).ConfigureAwait(false);
        var header = new FormRecordRevision
        {
            FormRecordRevisionId = Guid.CreateVersion7(),
            TenantId = context.OrganizationId,
            FormRecordId = formRecordId,
            FormDefinitionId = formDefinitionId,
            CreatedFormVersionId = definition.CurrentFormVersionId ?? Guid.Empty,
            CreatedByUserId = context.LocalUserId,
            RecordStatus = "Draft",
            SubmittedOn = null,
        };
        var values = request.Values.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => new FieldStringValue
        {
            FieldStringValueId = Guid.CreateVersion7(),
            TenantId = context.OrganizationId,
            FormRecordId = formRecordId,
            FieldDefinitionId = fieldByKey[kv.Key].FieldDefinitionId,
            FormRecordGroupId = null,
            FieldValueItemId = Guid.CreateVersion7(),
            StringValue = kv.Value,
            CreatedOn = now,
            CreatedByUserId = context.LocalUserId,
        }).ToList();

        var accepted = await stager.StageAndCommitAsync(context, head, new RecordCandidateStager.CandidateInput(header, values), idempotencyKey, cancellationToken).ConfigureAwait(false);
        return new FormRecordResponse(formRecordId, context.OrganizationId, formDefinitionId, header.RecordStatus, accepted.CommittedContentRevision, accepted.CommittedOperationId, accepted.AuthorizationRevision, true,
            values.Select(v => new FieldValueResponse(v.FieldDefinitionId, fields.First(f => f.FieldDefinitionId == v.FieldDefinitionId).FieldKey, v.FieldValueItemId, v.StringValue)).ToList(),
            "Committed(" + accepted.ArchiveState + ")");
    }
}
