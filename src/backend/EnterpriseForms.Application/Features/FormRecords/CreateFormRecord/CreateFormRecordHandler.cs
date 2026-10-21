using System.Text.Json;
using EnterpriseForms.Application.Common;
using EnterpriseForms.Application.Common.Authority;
using EnterpriseForms.Application.Common.Persistence;
using EnterpriseForms.Application.Features.Audit;
using EnterpriseForms.Application.Features.FormDefinitions.CreateFormDefinition;
using EnterpriseForms.Application.Features.FormDefinitions.Layout;
using EnterpriseForms.Application.Features.FormRecords.Staging;
using EnterpriseForms.Application.Features.FormRecords.Values;
using EnterpriseForms.Contracts.Features;
using EnterpriseForms.Domain.Features.Audit;
using EnterpriseForms.Domain.Features.FormRecords;
using EnterpriseForms.Domain.Features.RelatedForms;

namespace EnterpriseForms.Application.Features.FormRecords.CreateFormRecord;

/// <summary>
/// Create: authority RegisterRecord (owner = creator or an eligible team; or a child of a parent the actor owns) →
/// Effective → typed, validated candidate v1 → staged → committed head. Local projections (record, link, audit) are
/// written only after the authority command is Effective.
/// </summary>
public sealed class CreateFormRecordHandler(IBusinessStore store, IAuthorizationAuthorityClient authority, AuthorityCommandAwaiter awaiter, RecordCandidateStager stager, TimeProvider clock, Encryption.IFieldEncryptionService encryption)
{
    public Task<FormRecordResponse> HandleAsync(RequestContext context, Guid formDefinitionId, CreateFormRecordRequest request, string idempotencyKey, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return CreateAsync(context, formDefinitionId, request.Values, request.Groups, request.OwnerTeamId, null, idempotencyKey, cancellationToken);
    }

    /// <summary>Child record creation (B-04): the parent must be owned by the actor; the relationship definition binds the child form.</summary>
    public async Task<FormRecordResponse> CreateChildAsync(RequestContext context, Guid parentFormRecordId, CreateChildRecordRequest request, string idempotencyKey, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(request);
        FormRelationshipDefinition relationship;
        Domain.Features.FormRecords.FormRecord parent;
        int existingCount;
        await using (var scope = await store.BeginScopeAsync(cancellationToken).ConfigureAwait(false))
        {
            relationship = await scope.FindRelationshipDefinitionAsync(context.OrganizationId, request.FormRelationshipDefinitionId, cancellationToken).ConfigureAwait(false)
                           ?? throw new BusinessException("NOT_FOUND", 404, "relationship definition not found");
            parent = await scope.FindFormRecordAsync(context.OrganizationId, parentFormRecordId, cancellationToken).ConfigureAwait(false)
                     ?? throw new BusinessException("NOT_FOUND", 404, "parent record not found");
            existingCount = (await scope.GetActiveChildLinksAsync(parentFormRecordId, relationship.FormRelationshipDefinitionId, cancellationToken).ConfigureAwait(false)).Count;
        }

        if (relationship.LifecycleStatus != "Active" || relationship.ParentFormDefinitionId != parent.FormDefinitionId)
        {
            throw new BusinessException("INVALID_REQUEST", 422, "relationship does not apply to this parent record");
        }

        if (existingCount >= relationship.MaximumChildCount)
        {
            throw new BusinessException("RELATIONSHIP_CAPACITY_EXCEEDED", 409, $"this list allows at most {relationship.MaximumChildCount} items");
        }

        // The authority is the only judge of parent ownership; the local parent row is used for form/relationship binding only.
        var parentEdit = await authority.CheckPermissionAsync(context.ToDelegatedUser(), new PermissionCheckRequest(context.OrganizationId, new ResourceReference(ProtectedResourceKind.FormRecord, parentFormRecordId), ProtectedAction.Edit), cancellationToken).ConfigureAwait(false);
        if (!parentEdit.Allowed)
        {
            throw new BusinessException("NOT_FOUND", 404, "parent record not found");
        }

        return await CreateAsync(context, relationship.ChildFormDefinitionId, request.Values, request.Groups, null, (parentFormRecordId, relationship, existingCount + 1), idempotencyKey, cancellationToken).ConfigureAwait(false);
    }

    private async Task<FormRecordResponse> CreateAsync(RequestContext context, Guid formDefinitionId, IReadOnlyDictionary<string, string> values, IReadOnlyList<RecordGroupValuesRequest>? groupValues, Guid? ownerTeamId, (Guid ParentId, FormRelationshipDefinition Relationship, int SortOrder)? child, string idempotencyKey, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(values);

        Domain.Features.FormDefinitions.FormDefinition definition;
        Domain.Features.FormDefinitions.FormVersion version;
        List<Domain.Features.FormDefinitions.FieldDefinition> fields;
        List<Domain.Features.FormDefinitions.FieldOption> options;
        List<Domain.Features.FormDefinitions.GroupDefinition> groups;
        await using (var scope = await store.BeginScopeAsync(cancellationToken).ConfigureAwait(false))
        {
            definition = await scope.FindFormDefinitionAsync(context.OrganizationId, formDefinitionId, cancellationToken).ConfigureAwait(false)
                         ?? throw new BusinessException("NOT_FOUND", 404, "form definition not found");
            if (definition.CurrentFormVersionId is null || definition.LifecycleStatus != "Active")
            {
                throw new BusinessException("FORM_NOT_PUBLISHED", 422, "the form has no published version");
            }

            version = await scope.FindFormVersionAsync(definition.CurrentFormVersionId.Value, cancellationToken).ConfigureAwait(false)
                      ?? throw new BusinessException("FORM_NOT_PUBLISHED", 422, "published version missing");
            fields = await scope.GetFieldDefinitionsAsync(formDefinitionId, cancellationToken).ConfigureAwait(false);
            options = await scope.GetFieldOptionsAsync(formDefinitionId, cancellationToken).ConfigureAwait(false);
            groups = await scope.GetGroupDefinitionsAsync(formDefinitionId, cancellationToken).ConfigureAwait(false);
        }

        var layout = FormLayoutCodec.Parse(version.LayoutJson);
        var now = clock.GetUtcNow();
        var formRecordId = CreateFormDefinitionHandler.DeterministicId(idempotencyKey, "record");
        // Validate before any authority command so a bad request never registers a phantom resource.
        var applied = CandidateBuilder.ApplyValuesDetailed([], values, layout, fields, options, null, context.OrganizationId, formRecordId, now, context.LocalUserId, options.ToDictionary(o => o.FieldOptionId), encryption, creating: true,
            groups: new CandidateBuilder.GroupInput(groups, [], RecordGroupWrites.From(groupValues)));
        var candidateValues = applied.Rows;
        CandidateBuilder.EnsureWithinCeiling(candidateValues.Count + applied.Occurrences.Count + 1);

        if (child is null)
        {
            var createDecision = await authority.CheckPermissionAsync(context.ToDelegatedUser(), new PermissionCheckRequest(context.OrganizationId, new ResourceReference(ProtectedResourceKind.FormDefinition, formDefinitionId), ProtectedAction.CreateRecord), cancellationToken).ConfigureAwait(false);
            if (!createDecision.Allowed)
            {
                throw new BusinessException("FORBIDDEN", 403, "not permitted to create records for this form");
            }
        }

        var revisionDecision = await authority.CheckPermissionAsync(context.ToDelegatedUser(), new PermissionCheckRequest(context.OrganizationId, new ResourceReference(ProtectedResourceKind.Organization, context.OrganizationId), ProtectedAction.ManageOrganization), cancellationToken).ConfigureAwait(false);
        var payload = JsonSerializer.SerializeToElement(new
        {
            form_record_id = formRecordId,
            form_definition_id = formDefinitionId,
            owner_team_id = ownerTeamId,
            parent_form_record_id = child?.ParentId,
            form_relationship_definition_id = child?.Relationship.FormRelationshipDefinitionId,
        });
        var command = await awaiter.SubmitAndAwaitAsync(context.ToDelegatedUser(), new SecurityCommandRequest(
            context.OrganizationId, SecurityCommandKind.RegisterRecord, new ResourceReference(ProtectedResourceKind.FormRecord, formRecordId), revisionDecision.AuthorizationRevision, payload, child is null ? "create record" : "create child record"),
            "record:" + idempotencyKey, cancellationToken).ConfigureAwait(false);
        if (command.CommandStatus != SecurityCommandStatus.Effective)
        {
            throw new PendingCommandException(new CommandOutcome(command.CommandId, command.CommandStatus.ToString(), command.AuthorizationRevision, false));
        }

        await using (var scope = await store.BeginScopeAsync(cancellationToken).ConfigureAwait(false))
        {
            if (await scope.FindFormRecordAsync(context.OrganizationId, formRecordId, cancellationToken).ConfigureAwait(false) is null)
            {
                scope.AddFormRecord(new FormRecord
                {
                    FormRecordId = formRecordId,
                    TenantId = context.OrganizationId,
                    FormDefinitionId = formDefinitionId,
                    CreatedFormVersionId = version.FormVersionId,
                    RecordStatus = "Draft",
                    CreatedByUserId = context.LocalUserId,
                    OwnerUserId = child is null && ownerTeamId is null ? context.LocalUserId : null,
                    OwnerTeamId = child is null ? ownerTeamId : null,
                    CreatedOn = now,
                    ModifiedOn = now,
                    ModifiedByUserId = context.LocalUserId,
                    RecordRevision = 1,
                    AuthorityCommandId = command.CommandId,
                    ParentFormRecordId = child?.ParentId,
                    FormRelationshipDefinitionId = child?.Relationship.FormRelationshipDefinitionId,
                });
                if (child is { } c)
                {
                    scope.AddChildLink(new FormRecordRelationship
                    {
                        FormRecordRelationshipId = Guid.CreateVersion7(),
                        TenantId = context.OrganizationId,
                        FormRelationshipDefinitionId = c.Relationship.FormRelationshipDefinitionId,
                        ParentFormRecordId = c.ParentId,
                        ChildFormRecordId = formRecordId,
                        ListSortOrder = c.SortOrder,
                        LinkLifecycleState = "Active",
                        AuthorityCommandId = command.CommandId,
                        LinkCreatedOn = now,
                        LinkCreatedByUserId = context.LocalUserId,
                    });
                    AuditRecorder.Record(scope, context, clock, AuditCategories.Relationship, AuditActions.ChildRecordLinked, "child record created and linked", formRecordId, formDefinitionId, targetEntityType: "form_record", targetEntityId: c.ParentId, metadata: new { relationship = c.Relationship.RelationshipDefinitionKey, commandId = command.CommandId });
                }

                AuditRecorder.Record(scope, context, clock, AuditCategories.Record, AuditActions.RecordCreated, "record registered with the authority", formRecordId, formDefinitionId, metadata: new { commandId = command.CommandId, authorizationRevision = command.AuthorizationRevision, groupChanges = RecordGroupWrites.AuditOf(applied.GroupChanges) });
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
            CreatedFormVersionId = version.FormVersionId,
            CreatedByUserId = context.LocalUserId,
            RecordStatus = "Draft",
            SubmittedOn = null,
            ParentFormRecordId = child?.ParentId,
            FormRelationshipDefinitionId = child?.Relationship.FormRelationshipDefinitionId,
            RelationshipSortOrder = child?.SortOrder,
        };

        var accepted = await stager.StageAndCommitAsync(context, head, new RecordCandidateStager.CandidateInput(header, candidateValues.Cast<ISignedContentRow>().Concat(applied.Occurrences).ToList()), idempotencyKey, cancellationToken).ConfigureAwait(false);
        if (accepted.CommittedOperationId is { } acceptedOperation)
        {
            await using var scope = await store.BeginScopeAsync(cancellationToken).ConfigureAwait(false);
            var record = await scope.FindFormRecordAsync(context.OrganizationId, formRecordId, cancellationToken).ConfigureAwait(false);
            if (record is not null && record.RecordAcceptedContent(accepted.CommittedContentRevision, acceptedOperation))
            {
                await scope.CommitAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        var fieldsById = fields.ToDictionary(f => f.FieldDefinitionId);
        var optionsById = options.ToDictionary(o => o.FieldOptionId);
        return new FormRecordResponse(formRecordId, context.OrganizationId, formDefinitionId, header.RecordStatus, accepted.CommittedContentRevision, accepted.CommittedOperationId, accepted.AuthorizationRevision, true,
            candidateValues.OrderBy(v => fieldsById[v.FieldDefinitionId].FieldKey, StringComparer.Ordinal).Select(v => new FieldValueResponse(v.FieldDefinitionId, fieldsById[v.FieldDefinitionId].FieldKey, v.FieldValueItemId, FieldValueCodec.Format(v, optionsById, encryption), fieldsById[v.FieldDefinitionId].FieldDataType, ApplicationEncrypted: v is FieldEncryptedValue, FormRecordGroupId: v.FormRecordGroupId)).ToList(),
            "Committed(" + accepted.ArchiveState + ")",
            Projection.RecordGroupProjection.Build(groups, layout, applied.Occurrences));
    }
}
