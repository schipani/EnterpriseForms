using System.Text.Json;
using System.Text.RegularExpressions;
using EnterpriseForms.Application.Common;
using EnterpriseForms.Application.Common.Authority;
using EnterpriseForms.Application.Common.Persistence;
using EnterpriseForms.Application.Features.Audit;
using EnterpriseForms.Application.Features.FormDefinitions.CreateFormDefinition;
using EnterpriseForms.Application.Features.FormDefinitions.Layout;
using EnterpriseForms.Application.Features.FormRecords.Values;
using EnterpriseForms.Contracts.Features;
using EnterpriseForms.Domain.Features.Audit;
using EnterpriseForms.Domain.Features.FormDefinitions;
using EnterpriseForms.Domain.Features.RelatedForms;

namespace EnterpriseForms.Application.Features.FormDesigner;

/// <summary>
/// Designer use cases (CLAUDE.md §5, B-03): catalog with authority-derived capabilities, create (authority
/// RegisterFormDefinition), fields and stable options, draft save with revision CAS, atomic publish of an immutable
/// version, restore-to-draft, relationship definitions. Every write requires the authority <c>design</c> permission on
/// the form; nothing here reads submitted record values.
/// </summary>
public sealed partial class FormDesignerHandlers(IBusinessStore store, IAuthorizationAuthorityClient authority, AuthorityCommandAwaiter awaiter, TimeProvider clock)
{
    [GeneratedRegex("^[a-z][a-z0-9_]{0,63}$")]
    private static partial Regex KeyPattern();

    public async Task<IReadOnlyList<FormDefinitionSummaryResponse>> ListAsync(RequestContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        List<FormDefinition> definitions;
        Dictionary<Guid, FormVersion> currentVersions = [];
        HashSet<Guid> drafts = [];
        await using (var scope = await store.BeginScopeAsync(cancellationToken).ConfigureAwait(false))
        {
            definitions = await scope.GetFormDefinitionsAsync(context.OrganizationId, cancellationToken).ConfigureAwait(false);
            foreach (var d in definitions)
            {
                if (d.CurrentFormVersionId is { } vid && await scope.FindFormVersionAsync(vid, cancellationToken).ConfigureAwait(false) is { } v)
                {
                    currentVersions[d.FormDefinitionId] = v;
                }

                if (await scope.FindDraftAsync(d.FormDefinitionId, cancellationToken).ConfigureAwait(false) is not null)
                {
                    drafts.Add(d.FormDefinitionId);
                }
            }
        }

        if (definitions.Count == 0)
        {
            return [];
        }

        var checks = new List<PermissionCheckRequest>();
        foreach (var d in definitions)
        {
            checks.Add(new PermissionCheckRequest(context.OrganizationId, new ResourceReference(ProtectedResourceKind.FormDefinition, d.FormDefinitionId), ProtectedAction.DesignForm));
            checks.Add(new PermissionCheckRequest(context.OrganizationId, new ResourceReference(ProtectedResourceKind.FormDefinition, d.FormDefinitionId), ProtectedAction.CreateRecord));
        }

        var decisions = await authority.CheckPermissionsBatchAsync(context.ToDelegatedUser(), checks, cancellationToken).ConfigureAwait(false);
        var result = new List<FormDefinitionSummaryResponse>();
        for (var i = 0; i < definitions.Count; i++)
        {
            var canDesign = decisions[2 * i].Allowed;
            var canCreate = decisions[(2 * i) + 1].Allowed;
            if (!canDesign && !canCreate)
            {
                continue; // no existence disclosure of forms the caller may neither design nor use
            }

            result.Add(Summary(definitions[i], currentVersions.GetValueOrDefault(definitions[i].FormDefinitionId), drafts.Contains(definitions[i].FormDefinitionId), canDesign, canCreate));
        }

        return result;
    }

    public async Task<FormDefinitionDetailResponse> GetAsync(RequestContext context, Guid formDefinitionId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var decisions = await authority.CheckPermissionsBatchAsync(context.ToDelegatedUser(),
        [
            new PermissionCheckRequest(context.OrganizationId, new ResourceReference(ProtectedResourceKind.FormDefinition, formDefinitionId), ProtectedAction.DesignForm),
            new PermissionCheckRequest(context.OrganizationId, new ResourceReference(ProtectedResourceKind.FormDefinition, formDefinitionId), ProtectedAction.CreateRecord),
        ], cancellationToken).ConfigureAwait(false);
        if (!decisions[0].Allowed && !decisions[1].Allowed)
        {
            throw new BusinessException("NOT_FOUND", 404, "form definition not found");
        }

        await using var scope = await store.BeginScopeAsync(cancellationToken).ConfigureAwait(false);
        var definition = await scope.FindFormDefinitionAsync(context.OrganizationId, formDefinitionId, cancellationToken).ConfigureAwait(false)
                         ?? throw new BusinessException("NOT_FOUND", 404, "form definition not found");
        var fields = await scope.GetFieldDefinitionsAsync(formDefinitionId, cancellationToken).ConfigureAwait(false);
        var options = await scope.GetFieldOptionsAsync(formDefinitionId, cancellationToken).ConfigureAwait(false);
        var version = definition.CurrentFormVersionId is { } vid ? await scope.FindFormVersionAsync(vid, cancellationToken).ConfigureAwait(false) : null;
        var draft = decisions[0].Allowed ? await scope.FindDraftAsync(formDefinitionId, cancellationToken).ConfigureAwait(false) : null;
        var relationships = await scope.GetRelationshipDefinitionsAsync(context.OrganizationId, formDefinitionId, cancellationToken).ConfigureAwait(false);
        var names = (await scope.GetFormDefinitionsAsync(context.OrganizationId, cancellationToken).ConfigureAwait(false)).ToDictionary(d => d.FormDefinitionId, d => d.FormDefinitionName);
        return new FormDefinitionDetailResponse(
            Summary(definition, version, draft is not null, decisions[0].Allowed, decisions[1].Allowed),
            ToFieldResponses(fields, options),
            version is null ? null : ToVersion(version),
            draft is null ? null : new FormDraftResponse(draft.FormDraftId, draft.BasedOnFormVersionId, draft.LayoutSchemaVersion, draft.LayoutJson, draft.DraftRevision, draft.UpdatedOn),
            relationships.Select(r => ToRelationship(r, names.GetValueOrDefault(r.ChildFormDefinitionId, string.Empty))).ToList());
    }

    public async Task<FormDefinitionDetailResponse> CreateAsync(RequestContext context, CreateFormDefinitionV2Request request, string idempotencyKey, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.FormDefinitionName) || request.FormDefinitionName.Length > 200 || (request.FormDefinitionDescription?.Length ?? 0) > 2000)
        {
            throw new BusinessException("INVALID_REQUEST", 422, "form_definition_name is required (max 200)");
        }

        var decision = await authority.CheckPermissionAsync(context.ToDelegatedUser(), new PermissionCheckRequest(context.OrganizationId, new ResourceReference(ProtectedResourceKind.Organization, context.OrganizationId), ProtectedAction.DesignForm), cancellationToken).ConfigureAwait(false);
        if (!decision.Allowed)
        {
            throw new BusinessException("FORBIDDEN", 403, "designer role required");
        }

        var formDefinitionId = CreateFormDefinitionHandler.DeterministicId(idempotencyKey, "form");
        var payload = JsonSerializer.SerializeToElement(new { form_definition_id = formDefinitionId, form_user_user_ids = request.FormUserUserIds ?? [], form_user_team_ids = request.FormUserTeamIds ?? [] });
        var command = await awaiter.SubmitAndAwaitAsync(context.ToDelegatedUser(), new SecurityCommandRequest(context.OrganizationId, SecurityCommandKind.RegisterFormDefinition, new ResourceReference(ProtectedResourceKind.FormDefinition, formDefinitionId), decision.AuthorizationRevision, payload, "register form " + request.FormDefinitionName), "form:" + idempotencyKey, cancellationToken).ConfigureAwait(false);
        if (command.CommandStatus != SecurityCommandStatus.Effective)
        {
            throw new PendingCommandException(new CommandOutcome(command.CommandId, command.CommandStatus.ToString(), command.AuthorizationRevision, false));
        }

        var now = clock.GetUtcNow();
        await using (var scope = await store.BeginScopeAsync(cancellationToken).ConfigureAwait(false))
        {
            if (await scope.FindFormDefinitionAsync(context.OrganizationId, formDefinitionId, cancellationToken).ConfigureAwait(false) is null)
            {
                var definition = new FormDefinition
                {
                    FormDefinitionId = formDefinitionId,
                    TenantId = context.OrganizationId,
                    FormDefinitionName = request.FormDefinitionName.Trim(),
                    FormDefinitionDescription = request.FormDefinitionDescription?.Trim() ?? string.Empty,
                    CurrentFormVersionId = null,
                    VersionBehavior = "Latest",
                    LifecycleStatus = "Active",
                    CreatedOn = now,
                    CreatedByUserId = context.LocalUserId,
                    ModifiedOn = now,
                    ModifiedByUserId = context.LocalUserId,
                    AuthorityCommandId = command.CommandId,
                };
                scope.AddFormDefinition(definition, null, []);
                scope.AddDraft(new FormDraft
                {
                    FormDraftId = Guid.CreateVersion7(),
                    FormDefinitionId = formDefinitionId,
                    LayoutSchemaVersion = FormLayout.CurrentSchemaVersion,
                    LayoutJson = FormLayoutCodec.Serialize(new FormLayout(FormLayout.CurrentSchemaVersion, definition.FormDefinitionName, [new LayoutSection("main", definition.FormDefinitionName, 4, [])])),
                    DraftRevision = 1,
                    CreatedOn = now,
                    CreatedByUserId = context.LocalUserId,
                    UpdatedOn = now,
                    UpdatedByUserId = context.LocalUserId,
                });
                AuditRecorder.Record(scope, context, clock, AuditCategories.Form, AuditActions.FormCreated, "form definition registered with the authority", formDefinitionId: formDefinitionId, metadata: new { commandId = command.CommandId });
                await scope.CommitAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        return await GetAsync(context, formDefinitionId, cancellationToken).ConfigureAwait(false);
    }

    public async Task<FieldDefinitionResponse> AddFieldAsync(RequestContext context, Guid formDefinitionId, AddFieldDefinitionRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(request);
        await RequireDesignAsync(context, formDefinitionId, cancellationToken).ConfigureAwait(false);
        if (!KeyPattern().IsMatch(request.FieldKey ?? string.Empty) || string.IsNullOrWhiteSpace(request.FieldDisplayName) || request.FieldDisplayName.Length > 200 || !FieldDataTypes.StageB.Contains(request.FieldDataType ?? string.Empty))
        {
            throw new BusinessException("INVALID_REQUEST", 422, "snake_case field key, display name (max 200) and a supported data type (String, Integer, Decimal, Date, Boolean, Option, File) are required");
        }

        var now = clock.GetUtcNow();
        await using var scope = await store.BeginScopeAsync(cancellationToken).ConfigureAwait(false);
        var fields = await scope.GetFieldDefinitionsAsync(formDefinitionId, cancellationToken).ConfigureAwait(false);
        if (fields.Any(f => f.FieldKey == request.FieldKey))
        {
            throw new BusinessException("FIELD_KEY_EXISTS", 409, "field key already exists; re-add the existing field instead of creating a new identity");
        }

        if (fields.Count >= 250)
        {
            throw new BusinessException("LIMIT_EXCEEDED", 422, "a form may define at most 250 fields");
        }

        var field = new FieldDefinition
        {
            FieldDefinitionId = Guid.CreateVersion7(),
            FormDefinitionId = formDefinitionId,
            FieldKey = request.FieldKey!,
            FieldDisplayName = request.FieldDisplayName.Trim(),
            FieldDescription = request.FieldDescription?.Trim() ?? string.Empty,
            FieldDataType = request.FieldDataType!,
            FieldClassification = "Internal",
            IsSearchable = false,
            LifecycleStatus = "Available",
            CreatedOn = now,
            CreatedByUserId = context.LocalUserId,
        };
        scope.AddFieldDefinition(field);
        var options = new List<FieldOption>();
        if (request.FieldDataType == FieldDataTypes.Option)
        {
            var requested = request.Options ?? [];
            if (requested.Count is 0 or > 500 || requested.Select(o => o.OptionKey).Distinct(StringComparer.Ordinal).Count() != requested.Count || requested.Any(o => !KeyPattern().IsMatch(o.OptionKey ?? string.Empty) || string.IsNullOrWhiteSpace(o.OptionDisplayText) || o.OptionDisplayText.Length > 200))
            {
                throw new BusinessException("INVALID_REQUEST", 422, "option fields need 1..500 options with unique snake_case keys and display text");
            }

            var sort = 1;
            foreach (var o in requested)
            {
                var option = new FieldOption { FieldOptionId = Guid.CreateVersion7(), FieldDefinitionId = field.FieldDefinitionId, OptionKey = o.OptionKey!, OptionDisplayText = o.OptionDisplayText.Trim(), OptionSortOrder = sort++, IsActive = true, CreatedOn = now, CreatedByUserId = context.LocalUserId, ModifiedOn = now, ModifiedByUserId = context.LocalUserId };
                scope.AddFieldOption(option);
                options.Add(option);
            }
        }

        AuditRecorder.Record(scope, context, clock, AuditCategories.Form, AuditActions.FormDraftUpdated, "field definition added", formDefinitionId: formDefinitionId, fieldDefinitionId: field.FieldDefinitionId, metadata: new { fieldKey = field.FieldKey, dataType = field.FieldDataType });
        await scope.CommitAsync(cancellationToken).ConfigureAwait(false);
        return ToFieldResponses([field], options)[0];
    }

    public async Task<FieldDefinitionResponse> UpdateOptionsAsync(RequestContext context, Guid formDefinitionId, Guid fieldDefinitionId, UpdateFieldOptionsRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(request);
        await RequireDesignAsync(context, formDefinitionId, cancellationToken).ConfigureAwait(false);
        var now = clock.GetUtcNow();
        await using var scope = await store.BeginScopeAsync(cancellationToken).ConfigureAwait(false);
        var field = (await scope.GetFieldDefinitionsAsync(formDefinitionId, cancellationToken).ConfigureAwait(false)).FirstOrDefault(f => f.FieldDefinitionId == fieldDefinitionId)
                    ?? throw new BusinessException("NOT_FOUND", 404, "field not found");
        if (field.FieldDataType != FieldDataTypes.Option)
        {
            throw new BusinessException("INVALID_REQUEST", 422, "only option fields have options");
        }

        var existing = (await scope.GetFieldOptionsAsync(formDefinitionId, cancellationToken).ConfigureAwait(false)).Where(o => o.FieldDefinitionId == fieldDefinitionId).ToList();
        foreach (var change in request.Changes ?? [])
        {
            if (!KeyPattern().IsMatch(change.OptionKey ?? string.Empty) || string.IsNullOrWhiteSpace(change.OptionDisplayText) || change.OptionDisplayText.Length > 200)
            {
                throw new BusinessException("INVALID_REQUEST", 422, "option key must be snake_case and display text 1..200 chars");
            }

            var option = change.FieldOptionId is { } id ? existing.FirstOrDefault(o => o.FieldOptionId == id) : null;
            if (option is null)
            {
                if (existing.Any(o => o.OptionKey == change.OptionKey))
                {
                    throw new BusinessException("INVALID_REQUEST", 422, $"option key '{change.OptionKey}' already exists (keys are immutable)");
                }

                option = new FieldOption { FieldOptionId = Guid.CreateVersion7(), FieldDefinitionId = fieldDefinitionId, OptionKey = change.OptionKey!, CreatedOn = now, CreatedByUserId = context.LocalUserId };
                scope.AddFieldOption(option);
                existing.Add(option);
            }
            else if (option.OptionKey != change.OptionKey)
            {
                throw new BusinessException("INVALID_REQUEST", 422, "option keys are immutable; deactivate and add a new option instead");
            }

            option.OptionDisplayText = change.OptionDisplayText.Trim();
            option.OptionSortOrder = change.OptionSortOrder;
            option.IsActive = change.IsActive;
            option.ModifiedOn = now;
            option.ModifiedByUserId = context.LocalUserId;
        }

        AuditRecorder.Record(scope, context, clock, AuditCategories.Form, AuditActions.FormDraftUpdated, "field options updated", formDefinitionId: formDefinitionId, fieldDefinitionId: fieldDefinitionId, metadata: new { changes = (request.Changes ?? []).Count });
        await scope.CommitAsync(cancellationToken).ConfigureAwait(false);
        return ToFieldResponses([field], existing)[0];
    }

    public async Task<FormDraftResponse> SaveDraftAsync(RequestContext context, Guid formDefinitionId, SaveDraftRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(request);
        await RequireDesignAsync(context, formDefinitionId, cancellationToken).ConfigureAwait(false);
        var layout = FormLayoutCodec.Parse(request.LayoutJson);
        await using var scope = await store.BeginScopeAsync(cancellationToken).ConfigureAwait(false);
        await ValidateLayoutReferencesAsync(scope, context, formDefinitionId, layout, cancellationToken).ConfigureAwait(false);
        var draft = await scope.FindDraftAsync(formDefinitionId, cancellationToken).ConfigureAwait(false);
        var now = clock.GetUtcNow();
        if (draft is null)
        {
            draft = new FormDraft { FormDraftId = Guid.CreateVersion7(), FormDefinitionId = formDefinitionId, DraftRevision = 0, CreatedOn = now, CreatedByUserId = context.LocalUserId };
            scope.AddDraft(draft);
        }

        if (draft.DraftRevision != request.ExpectedDraftRevision)
        {
            throw new BusinessException("DRAFT_REVISION_CONFLICT", 409, "draft was changed by someone else; reload and compare");
        }

        draft.LayoutSchemaVersion = FormLayout.CurrentSchemaVersion;
        draft.LayoutJson = FormLayoutCodec.Serialize(layout with { LayoutSchemaVersion = FormLayout.CurrentSchemaVersion });
        draft.DraftRevision++;
        draft.UpdatedOn = now;
        draft.UpdatedByUserId = context.LocalUserId;
        AuditRecorder.Record(scope, context, clock, AuditCategories.Form, AuditActions.FormDraftUpdated, "draft layout saved", formDefinitionId: formDefinitionId, metadata: new { draftRevision = draft.DraftRevision, elements = layout.Elements.Count() });
        await scope.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new FormDraftResponse(draft.FormDraftId, draft.BasedOnFormVersionId, draft.LayoutSchemaVersion, draft.LayoutJson, draft.DraftRevision, draft.UpdatedOn);
    }

    public async Task<FormVersionResponse> PublishAsync(RequestContext context, Guid formDefinitionId, PublishFormRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(request);
        await RequireDesignAsync(context, formDefinitionId, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(request.ChangeReason) || request.ChangeReason.Length > 1000)
        {
            throw new BusinessException("INVALID_REQUEST", 422, "a change reason (max 1000) is required");
        }

        await using var scope = await store.BeginScopeAsync(cancellationToken).ConfigureAwait(false);
        var definition = await scope.FindFormDefinitionAsync(context.OrganizationId, formDefinitionId, cancellationToken).ConfigureAwait(false)
                         ?? throw new BusinessException("NOT_FOUND", 404, "form definition not found");
        var draft = await scope.FindDraftAsync(formDefinitionId, cancellationToken).ConfigureAwait(false)
                    ?? throw new BusinessException("NO_DRAFT", 409, "there is no draft to publish");
        if (draft.DraftRevision != request.ExpectedDraftRevision)
        {
            throw new BusinessException("DRAFT_REVISION_CONFLICT", 409, "draft revision is stale");
        }

        var versions = await scope.GetFormVersionsAsync(formDefinitionId, cancellationToken).ConfigureAwait(false);
        var currentNumber = versions.Count == 0 ? 0 : versions.Max(v => v.VersionNumber);
        if (currentNumber != request.ExpectedCurrentVersionNumber)
        {
            throw new BusinessException("VERSION_CONFLICT", 409, "another version was published meanwhile");
        }

        var layout = FormLayoutCodec.Parse(draft.LayoutJson);
        await ValidateLayoutReferencesAsync(scope, context, formDefinitionId, layout, cancellationToken).ConfigureAwait(false);
        if (!layout.Elements.Any())
        {
            throw new BusinessException("LAYOUT_INVALID", 422, "a published version needs at least one element");
        }

        var canonical = FormLayoutCodec.Serialize(layout with { LayoutSchemaVersion = FormLayout.CurrentSchemaVersion });
        var currentVersion = versions.FirstOrDefault(v => v.VersionNumber == currentNumber);
        if (currentVersion is not null && FormLayoutCodec.Serialize(FormLayoutCodec.Parse(currentVersion.LayoutJson)) == canonical)
        {
            throw new BusinessException("NO_DRAFT_CHANGES", 409, "the draft is identical to the current published version; nothing to publish");
        }

        var now = clock.GetUtcNow();
        var version = new FormVersion
        {
            FormVersionId = Guid.CreateVersion7(),
            FormDefinitionId = formDefinitionId,
            VersionNumber = currentNumber + 1,
            LayoutSchemaVersion = FormLayout.CurrentSchemaVersion,
            LayoutJson = canonical,
            CreatedOn = now,
            CreatedByUserId = context.LocalUserId,
            ChangeReason = request.ChangeReason.Trim(),
            RestoredFromFormVersionId = draft.BasedOnFormVersionId is { } based && versions.Any(v => v.FormVersionId == based && v.VersionNumber != currentNumber) ? based : null,
            PublishedOn = now,
            PublishedByUserId = context.LocalUserId,
        };
        scope.AddFormVersion(version);
        definition.CurrentFormVersionId = version.FormVersionId;
        definition.ModifiedOn = now;
        definition.ModifiedByUserId = context.LocalUserId;
        draft.BasedOnFormVersionId = version.FormVersionId;
        draft.DraftRevision++;
        draft.UpdatedOn = now;
        draft.UpdatedByUserId = context.LocalUserId;
        AuditRecorder.Record(scope, context, clock, AuditCategories.Form, AuditActions.FormVersionPublished, "immutable form version published", formDefinitionId: formDefinitionId, targetEntityType: "form_version", targetEntityId: version.FormVersionId, metadata: new { versionNumber = version.VersionNumber, changeReason = version.ChangeReason, elements = layout.Elements.Count() });
        await scope.CommitAsync(cancellationToken).ConfigureAwait(false);
        return ToVersion(version);
    }

    public async Task<FormDraftResponse> RestoreToDraftAsync(RequestContext context, Guid formDefinitionId, RestoreVersionRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(request);
        await RequireDesignAsync(context, formDefinitionId, cancellationToken).ConfigureAwait(false);
        await using var scope = await store.BeginScopeAsync(cancellationToken).ConfigureAwait(false);
        var source = await scope.FindFormVersionAsync(request.FromFormVersionId, cancellationToken).ConfigureAwait(false);
        if (source is null || source.FormDefinitionId != formDefinitionId)
        {
            throw new BusinessException("NOT_FOUND", 404, "version not found");
        }

        var draft = await scope.FindDraftAsync(formDefinitionId, cancellationToken).ConfigureAwait(false);
        var now = clock.GetUtcNow();
        if (draft is null)
        {
            draft = new FormDraft { FormDraftId = Guid.CreateVersion7(), FormDefinitionId = formDefinitionId, DraftRevision = 0, CreatedOn = now, CreatedByUserId = context.LocalUserId };
            scope.AddDraft(draft);
        }

        draft.LayoutJson = FormLayoutCodec.Serialize(FormLayoutCodec.Parse(source.LayoutJson) with { LayoutSchemaVersion = FormLayout.CurrentSchemaVersion });
        draft.LayoutSchemaVersion = FormLayout.CurrentSchemaVersion;
        draft.BasedOnFormVersionId = source.FormVersionId;
        draft.DraftRevision++;
        draft.UpdatedOn = now;
        draft.UpdatedByUserId = context.LocalUserId;
        AuditRecorder.Record(scope, context, clock, AuditCategories.Form, AuditActions.FormDraftUpdated, "draft restored from a published version", formDefinitionId: formDefinitionId, targetEntityType: "form_version", targetEntityId: source.FormVersionId, metadata: new { fromVersionNumber = source.VersionNumber });
        await scope.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new FormDraftResponse(draft.FormDraftId, draft.BasedOnFormVersionId, draft.LayoutSchemaVersion, draft.LayoutJson, draft.DraftRevision, draft.UpdatedOn);
    }

    public async Task<IReadOnlyList<FormVersionResponse>> ListVersionsAsync(RequestContext context, Guid formDefinitionId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        await RequireDesignAsync(context, formDefinitionId, cancellationToken).ConfigureAwait(false);
        await using var scope = await store.BeginScopeAsync(cancellationToken).ConfigureAwait(false);
        return (await scope.GetFormVersionsAsync(formDefinitionId, cancellationToken).ConfigureAwait(false)).Select(ToVersion).ToList();
    }

    public async Task<RelationshipDefinitionResponse> CreateRelationshipAsync(RequestContext context, Guid parentFormDefinitionId, CreateRelationshipDefinitionRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(request);
        await RequireDesignAsync(context, parentFormDefinitionId, cancellationToken).ConfigureAwait(false);
        await RequireDesignAsync(context, request.ChildFormDefinitionId, cancellationToken).ConfigureAwait(false);
        if (!KeyPattern().IsMatch(request.RelationshipDefinitionKey ?? string.Empty) || string.IsNullOrWhiteSpace(request.RelationshipDisplayName) || request.RelationshipDisplayName.Length > 200
            || request.MinimumChildCount < 0 || request.MaximumChildCount is < 1 or > 250 || request.MinimumChildCount > request.MaximumChildCount || request.ChildFormDefinitionId == parentFormDefinitionId)
        {
            throw new BusinessException("INVALID_REQUEST", 422, "snake_case key, display name, 0 <= min <= max <= 250 and a different child form are required");
        }

        await using var scope = await store.BeginScopeAsync(cancellationToken).ConfigureAwait(false);
        var child = await scope.FindFormDefinitionAsync(context.OrganizationId, request.ChildFormDefinitionId, cancellationToken).ConfigureAwait(false)
                    ?? throw new BusinessException("NOT_FOUND", 404, "child form not found");
        var all = await scope.GetRelationshipDefinitionsAsync(context.OrganizationId, null, cancellationToken).ConfigureAwait(false);
        if (all.Any(r => r.ParentFormDefinitionId == parentFormDefinitionId && r.RelationshipDefinitionKey == request.RelationshipDefinitionKey))
        {
            throw new BusinessException("RELATIONSHIP_KEY_EXISTS", 409, "relationship key already exists on this parent form");
        }

        // Cycle freedom (RELATED_FORM_COLLECTIONS.md §2): the child form must not reach the parent form through existing relationships.
        if (Reaches(all, request.ChildFormDefinitionId, parentFormDefinitionId))
        {
            throw new BusinessException("RELATIONSHIP_CYCLE", 422, "relationship would create a cycle");
        }

        var now = clock.GetUtcNow();
        var definition = new FormRelationshipDefinition
        {
            FormRelationshipDefinitionId = Guid.CreateVersion7(),
            TenantId = context.OrganizationId,
            ParentFormDefinitionId = parentFormDefinitionId,
            ChildFormDefinitionId = request.ChildFormDefinitionId,
            RelationshipDefinitionKey = request.RelationshipDefinitionKey!,
            RelationshipDisplayName = request.RelationshipDisplayName.Trim(),
            MinimumChildCount = request.MinimumChildCount,
            MaximumChildCount = request.MaximumChildCount,
            LifecycleStatus = "Active",
            DefinitionRevision = 1,
            CreatedOn = now,
            CreatedByUserId = context.LocalUserId,
            ModifiedOn = now,
            ModifiedByUserId = context.LocalUserId,
        };
        scope.AddRelationshipDefinition(definition);
        AuditRecorder.Record(scope, context, clock, AuditCategories.Form, AuditActions.FormDraftUpdated, "related form list defined", formDefinitionId: parentFormDefinitionId, targetEntityType: "form_relationship_definition", targetEntityId: definition.FormRelationshipDefinitionId, metadata: new { key = definition.RelationshipDefinitionKey, childFormDefinitionId = child.FormDefinitionId });
        await scope.CommitAsync(cancellationToken).ConfigureAwait(false);
        return ToRelationship(definition, child.FormDefinitionName);
    }

    private static bool Reaches(IReadOnlyList<FormRelationshipDefinition> all, Guid from, Guid target)
    {
        var visited = new HashSet<Guid>();
        var stack = new Stack<Guid>([from]);
        while (stack.Count > 0)
        {
            var current = stack.Pop();
            if (current == target)
            {
                return true;
            }

            if (!visited.Add(current))
            {
                continue;
            }

            foreach (var next in all.Where(r => r.ParentFormDefinitionId == current).Select(r => r.ChildFormDefinitionId))
            {
                stack.Push(next);
            }
        }

        return false;
    }

    private async Task RequireDesignAsync(RequestContext context, Guid formDefinitionId, CancellationToken cancellationToken)
    {
        var decision = await authority.CheckPermissionAsync(context.ToDelegatedUser(), new PermissionCheckRequest(context.OrganizationId, new ResourceReference(ProtectedResourceKind.FormDefinition, formDefinitionId), ProtectedAction.DesignForm), cancellationToken).ConfigureAwait(false);
        if (!decision.Allowed)
        {
            throw new BusinessException("NOT_FOUND", 404, "form definition not found");
        }
    }

    private static async Task ValidateLayoutReferencesAsync(IBusinessScope scope, RequestContext context, Guid formDefinitionId, FormLayout layout, CancellationToken cancellationToken)
    {
        var fields = (await scope.GetFieldDefinitionsAsync(formDefinitionId, cancellationToken).ConfigureAwait(false)).ToDictionary(f => f.FieldDefinitionId);
        var relationships = (await scope.GetRelationshipDefinitionsAsync(context.OrganizationId, formDefinitionId, cancellationToken).ConfigureAwait(false)).ToDictionary(r => r.FormRelationshipDefinitionId);
        foreach (var element in layout.Elements)
        {
            if (element.FieldDefinitionId is { } fieldId)
            {
                if (!fields.TryGetValue(fieldId, out var field))
                {
                    throw new BusinessException("LAYOUT_INVALID", 422, "layout references a field that does not belong to this form");
                }

                if (element.Kind == LayoutElementKinds.DocumentCollection != (field.FieldDataType == FieldDataTypes.File))
                {
                    throw new BusinessException("LAYOUT_INVALID", 422, $"field '{field.FieldKey}' control does not match its data type");
                }

                var expectedControls = field.FieldDataType switch
                {
                    FieldDataTypes.String => new[] { "text", "textarea" },
                    FieldDataTypes.Integer => ["number"],
                    FieldDataTypes.Decimal => ["decimal"],
                    FieldDataTypes.Date => ["date"],
                    FieldDataTypes.DateTime => ["dateTime"],
                    FieldDataTypes.Guid => ["guid"],
                    FieldDataTypes.Boolean => ["checkbox"],
                    FieldDataTypes.Option => ["select"],
                    _ => ["fileUpload"],
                };
                if (!expectedControls.Contains(element.Control, StringComparer.Ordinal))
                {
                    throw new BusinessException("LAYOUT_INVALID", 422, $"control '{element.Control}' is incompatible with {field.FieldDataType} field '{field.FieldKey}'");
                }
            }

            if (element.FormRelationshipDefinitionId is { } relId && !relationships.ContainsKey(relId))
            {
                throw new BusinessException("LAYOUT_INVALID", 422, "layout references a relationship that does not belong to this form");
            }
        }
    }

    private static FormDefinitionSummaryResponse Summary(FormDefinition d, FormVersion? current, bool hasDraft, bool canDesign, bool canCreate) =>
        new(d.FormDefinitionId, d.FormDefinitionName, d.FormDefinitionDescription, d.LifecycleStatus, current?.VersionNumber, current?.FormVersionId, hasDraft, canDesign, canCreate, d.ModifiedOn);

    private static FormVersionResponse ToVersion(FormVersion v) => new(v.FormVersionId, v.VersionNumber, v.LayoutSchemaVersion, v.LayoutJson, v.ChangeReason, v.RestoredFromFormVersionId, v.PublishedOn, v.PublishedByUserId);

    private static RelationshipDefinitionResponse ToRelationship(FormRelationshipDefinition r, string childName) =>
        new(r.FormRelationshipDefinitionId, r.ParentFormDefinitionId, r.ChildFormDefinitionId, childName, r.RelationshipDefinitionKey, r.RelationshipDisplayName, r.MinimumChildCount, r.MaximumChildCount, r.LifecycleStatus);

    public static List<FieldDefinitionResponse> ToFieldResponses(IReadOnlyList<FieldDefinition> fields, IReadOnlyList<FieldOption> options)
    {
        ArgumentNullException.ThrowIfNull(fields);
        ArgumentNullException.ThrowIfNull(options);
        var byField = options.GroupBy(o => o.FieldDefinitionId).ToDictionary(g => g.Key, g => g.OrderBy(o => o.OptionSortOrder).ThenBy(o => o.OptionKey, StringComparer.Ordinal).Select(o => new FieldOptionResponse(o.FieldOptionId, o.OptionKey, o.OptionDisplayText, o.OptionSortOrder, o.IsActive)).ToList());
        return fields.OrderBy(f => f.FieldKey, StringComparer.Ordinal).Select(f => new FieldDefinitionResponse(f.FieldDefinitionId, f.FieldKey, f.FieldDisplayName, f.FieldDescription, f.FieldDataType, f.FieldClassification, f.LifecycleStatus, byField.GetValueOrDefault(f.FieldDefinitionId, []))).ToList();
    }
}
