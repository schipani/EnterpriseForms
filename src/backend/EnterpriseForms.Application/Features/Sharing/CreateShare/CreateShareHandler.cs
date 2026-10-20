using System.Text.Json;
using EnterpriseForms.Application.Common;
using EnterpriseForms.Application.Common.Authority;
using EnterpriseForms.Application.Common.Persistence;
using EnterpriseForms.Application.Features.FormDefinitions.CreateFormDefinition;
using EnterpriseForms.Contracts.Features;
using EnterpriseForms.Domain.Features.Sharing;

namespace EnterpriseForms.Application.Features.Sharing.CreateShare;

/// <summary>Explicit item-scoped share (CLAUDE.md §4H): field keys map to the accepted manifest's item ids; the authority validates scope, delegation and recipient.</summary>
public sealed class CreateShareHandler(IBusinessStore store, IAuthorizationAuthorityClient authority, AuthorityCommandAwaiter awaiter, TimeProvider clock)
{
    public async Task<ShareResponse> HandleAsync(RequestContext context, Guid formRecordId, CreateShareRequest request, string idempotencyKey, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(request);
        if ((request.RecipientUserId is null) == (request.RecipientTeamId is null) || request.FieldKeys.Count == 0 || request.RecipientRole is not ("Reader" or "Contributor" or "Manager"))
        {
            throw new BusinessException("INVALID_REQUEST", 422, "one recipient, a valid role and at least one field key are required");
        }

        var user = context.ToDelegatedUser();
        var head = await authority.GetCommittedContentRevisionAsync(user, context.OrganizationId, formRecordId, cancellationToken).ConfigureAwait(false);
        if (head.CommittedOperationId is null)
        {
            throw new BusinessException("NO_COMMITTED_CONTENT", 404, "record has no accepted content");
        }

        List<Domain.Features.FormRecords.FieldValueRowBase> values;
        List<Domain.Features.FormDefinitions.FieldDefinition> fields;
        await using (var scope = await store.BeginScopeAsync(cancellationToken).ConfigureAwait(false))
        {
            var header = await scope.FindStagedHeaderAsync(formRecordId, head.CommittedOperationId.Value, cancellationToken).ConfigureAwait(false)
                         ?? throw new BusinessException("NO_COMMITTED_CONTENT", 404, "committed header missing");
            values = (await scope.GetStagedRowsAsync(formRecordId, head.CommittedOperationId.Value, cancellationToken).ConfigureAwait(false)).OfType<Domain.Features.FormRecords.FieldValueRowBase>().ToList();
            fields = await scope.GetFieldDefinitionsAsync(header.FormDefinitionId, cancellationToken).ConfigureAwait(false);
        }

        var fieldByKey = fields.ToDictionary(f => f.FieldKey, StringComparer.Ordinal);
        var items = new List<(Guid FieldDefinitionId, Guid ItemId)>();
        foreach (var key in request.FieldKeys.Distinct(StringComparer.Ordinal))
        {
            if (!fieldByKey.TryGetValue(key, out var field))
            {
                throw new BusinessException("INVALID_REQUEST", 422, $"unknown field key '{key}'");
            }

            // A multi-select dropdown holds one row (item) per selected option: sharing the field shares every current selection.
            var fieldRows = values.Where(v => v.FieldDefinitionId == field.FieldDefinitionId).ToList();
            if (fieldRows.Count == 0)
            {
                throw new BusinessException("INVALID_REQUEST", 422, $"field '{key}' has no persisted value to share");
            }

            items.AddRange(fieldRows.Select(r => (field.FieldDefinitionId, r.FieldValueItemId)));
        }

        var recordShareId = CreateFormDefinitionHandler.DeterministicId(idempotencyKey, "share");
        var payload = JsonSerializer.SerializeToElement(new
        {
            record_share_id = recordShareId,
            form_record_id = formRecordId,
            recipient_user_id = request.RecipientUserId,
            recipient_team_id = request.RecipientTeamId,
            recipient_role = request.RecipientRole,
            field_item_ids = items.Select(i => i.ItemId).ToList(),
        });
        var command = await awaiter.SubmitAndAwaitAsync(user, new SecurityCommandRequest(context.OrganizationId, SecurityCommandKind.CreateShare, new ResourceReference(ProtectedResourceKind.FormRecord, formRecordId), head.AuthorizationRevision, payload, "share"), "share:" + idempotencyKey, cancellationToken).ConfigureAwait(false);
        var outcome = new CommandOutcome(command.CommandId, command.CommandStatus.ToString(), command.AuthorizationRevision, command.CommandStatus == SecurityCommandStatus.Effective);
        if (!outcome.Effective)
        {
            throw new PendingCommandException(outcome);
        }

        await using (var scope = await store.BeginScopeAsync(cancellationToken).ConfigureAwait(false))
        {
            if (await scope.FindShareProjectionAsync(context.OrganizationId, recordShareId, cancellationToken).ConfigureAwait(false) is null)
            {
                var now = clock.GetUtcNow();
                Audit.AuditRecorder.Record(scope, context, clock, Audit.AuditCategories.Access, Domain.Features.Audit.AuditActions.RecordAccessGranted, "field-scoped share committed by the authority", formRecordId, targetEntityType: "record_share", targetEntityId: recordShareId, metadata: new { role = request.RecipientRole, recipientUserId = request.RecipientUserId, recipientTeamId = request.RecipientTeamId, fieldKeys = request.FieldKeys, commandId = command.CommandId });
                scope.AddShareProjection(new RecordShare
                {
                    RecordShareId = recordShareId,
                    TenantId = context.OrganizationId,
                    FormRecordId = formRecordId,
                    SharedByUserId = context.LocalUserId,
                    RecipientUserId = request.RecipientUserId,
                    RecipientTeamId = request.RecipientTeamId,
                    RecipientRole = request.RecipientRole,
                    ShareScope = "SelectedItems",
                    ShareStatus = "Active",
                    ShareRevision = 1,
                    AuthorityCommandId = command.CommandId,
                    ShareCreatedOn = now,
                }, items.Select(i => new RecordShareField
                {
                    RecordShareFieldId = Guid.CreateVersion7(),
                    TenantId = context.OrganizationId,
                    RecordShareId = recordShareId,
                    FieldDefinitionId = i.FieldDefinitionId,
                    FieldValueItemId = i.ItemId,
                    CanViewField = true,
                    CanEditField = request.RecipientRole != "Reader",
                }));
                await scope.CommitAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        return new ShareResponse(recordShareId, formRecordId, request.RecipientRole, items.Select(i => i.ItemId).ToList(), "Active", outcome);
    }
}
