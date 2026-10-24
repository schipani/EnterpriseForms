using System.Text.Json;
using System.Text.RegularExpressions;
using EnterpriseForms.Application.Common;
using EnterpriseForms.Application.Common.Authority;
using EnterpriseForms.Application.Common.Persistence;
using EnterpriseForms.Application.Features.Audit;
using EnterpriseForms.Application.Features.FormDefinitions.CreateFormDefinition;
using EnterpriseForms.Application.Features.FormDefinitions.Layout;
using EnterpriseForms.Application.Features.FormDefinitions.Protection;
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
public sealed partial class FormDesignerHandlers(IBusinessStore store, IAuthorizationAuthorityClient authority, AuthorityCommandAwaiter awaiter, TimeProvider clock, FormRecords.Encryption.IFieldEncryptionService encryption)
{
    [GeneratedRegex("^[a-z][a-z0-9_]{0,63}$")]
    private static partial Regex KeyPattern();

    public async Task<IReadOnlyList<FormDefinitionSummaryResponse>> ListAsync(RequestContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        List<FormDefinition> definitions;
        Dictionary<Guid, FormVersion> currentVersions;
        HashSet<Guid> drafts;
        await using (var scope = await store.BeginScopeAsync(cancellationToken).ConfigureAwait(false))
        {
            definitions = await scope.GetFormDefinitionsAsync(context.OrganizationId, cancellationToken).ConfigureAwait(false);
            var expected = definitions.Where(d => d.CurrentFormVersionId is not null).ToDictionary(d => d.CurrentFormVersionId!.Value, d => d.FormDefinitionId);
            var versions = await scope.GetFormVersionSummariesAsync(expected.Keys.ToList(), cancellationToken).ConfigureAwait(false);
            currentVersions = versions.Where(v => expected.TryGetValue(v.FormVersionId, out var owner) && owner == v.FormDefinitionId).ToDictionary(v => v.FormDefinitionId);
            drafts = await scope.GetFormDefinitionIdsWithDraftAsync(definitions.Select(d => d.FormDefinitionId).ToList(), cancellationToken).ConfigureAwait(false);
        }

        if (definitions.Count == 0)
        {
            return [];
        }

        // Two checks per form; the authority client splits batches above its per-call limit (C-27), so a catalog of any
        // size is evaluated completely at one authorization revision or the request fails closed.
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
        var groups = await scope.GetGroupDefinitionsAsync(formDefinitionId, cancellationToken).ConfigureAwait(false);
        return new FormDefinitionDetailResponse(
            Summary(definition, version, draft is not null, decisions[0].Allowed, decisions[1].Allowed),
            ToFieldResponses(fields, options),
            version is null ? null : ToVersion(version),
            draft is null ? null : new FormDraftResponse(draft.FormDraftId, draft.BasedOnFormVersionId, draft.LayoutSchemaVersion, draft.LayoutJson, draft.DraftRevision, draft.UpdatedOn),
            relationships.Select(r => ToRelationship(r, names.GetValueOrDefault(r.ChildFormDefinitionId, string.Empty))).ToList(),
            groups.Select(ToGroup).ToList());
    }

    public static GroupDefinitionResponse ToGroup(GroupDefinition g) => new(g.GroupDefinitionId, g.GroupKey, g.GroupDisplayName, g.LifecycleStatus);

    /// <summary>
    /// Creates the stable repeating group behind a section that allows multiple entries (Phase C3). The group key is
    /// permanent; fields created for the group belong to it for life. Placing it in a section is a layout (draft) change.
    /// </summary>
    public async Task<GroupDefinitionResponse> CreateGroupAsync(RequestContext context, Guid formDefinitionId, CreateGroupDefinitionRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(request);
        await RequireDesignAsync(context, formDefinitionId, cancellationToken).ConfigureAwait(false);
        if (!KeyPattern().IsMatch(request.GroupKey ?? string.Empty) || string.IsNullOrWhiteSpace(request.GroupDisplayName) || request.GroupDisplayName.Length > 200 || request.GroupDisplayName.Contains('<', StringComparison.Ordinal))
        {
            throw new BusinessException("INVALID_REQUEST", 422, "a snake_case group key and a display name (max 200) are required");
        }

        var now = clock.GetUtcNow();
        await using var scope = await store.BeginScopeAsync(cancellationToken).ConfigureAwait(false);
        if (await scope.FindFormDefinitionAsync(context.OrganizationId, formDefinitionId, cancellationToken).ConfigureAwait(false) is null)
        {
            throw new BusinessException("NOT_FOUND", 404, "form definition not found");
        }

        var existing = await scope.GetGroupDefinitionsAsync(formDefinitionId, cancellationToken).ConfigureAwait(false);
        if (existing.Any(g => g.GroupKey == request.GroupKey))
        {
            throw new BusinessException("GROUP_KEY_EXISTS", 409, "a repeating group with this key already exists on the form");
        }

        if (existing.Count >= 25)
        {
            throw new BusinessException("LIMIT_EXCEEDED", 422, "a form may define at most 25 repeating groups");
        }

        var group = new GroupDefinition
        {
            GroupDefinitionId = Guid.CreateVersion7(),
            FormDefinitionId = formDefinitionId,
            GroupKey = request.GroupKey!,
            GroupDisplayName = request.GroupDisplayName.Trim(),
            LifecycleStatus = "Available",
            CreatedOn = now,
            CreatedByUserId = context.LocalUserId,
        };
        scope.AddGroupDefinition(group);
        AuditRecorder.Record(scope, context, clock, AuditCategories.Form, AuditActions.FormDraftUpdated, "repeating group defined", formDefinitionId: formDefinitionId, metadata: new { groupKey = group.GroupKey, groupDefinitionId = group.GroupDefinitionId });
        await scope.CommitAsync(cancellationToken).ConfigureAwait(false);
        return ToGroup(group);
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
        var decision = await authority.CheckPermissionAsync(context.ToDelegatedUser(), new PermissionCheckRequest(context.OrganizationId, new ResourceReference(ProtectedResourceKind.FormDefinition, formDefinitionId), ProtectedAction.DesignForm), cancellationToken).ConfigureAwait(false);
        if (!decision.Allowed)
        {
            throw new BusinessException("NOT_FOUND", 404, "form definition not found");
        }

        if (!KeyPattern().IsMatch(request.FieldKey ?? string.Empty) || string.IsNullOrWhiteSpace(request.FieldDisplayName) || request.FieldDisplayName.Length > 200 || !FieldDataTypes.StageB.Contains(request.FieldDataType ?? string.Empty))
        {
            throw new BusinessException("INVALID_REQUEST", 422, "snake_case field key, display name (max 200) and a supported data type (String, Integer, Decimal, Date, Boolean, Option, File) are required");
        }

        var selectionMode = string.IsNullOrWhiteSpace(request.OptionSelectionMode) ? OptionSelectionModes.Single : request.OptionSelectionMode.Trim();
        if (selectionMode is not (OptionSelectionModes.Single or OptionSelectionModes.Multiple) || (request.FieldDataType != FieldDataTypes.Option && (selectionMode != OptionSelectionModes.Single || request.OptionListId is not null)))
        {
            throw new BusinessException("INVALID_REQUEST", 422, "option selection (Single or Multiple) and a shared option list apply to dropdown (Option) fields only");
        }

        string classification;
        string protectionMode;
        await using (var check = await store.BeginScopeAsync(cancellationToken).ConfigureAwait(false))
        {
            var policy = await Organizations.SecurityPolicyHandlers.EnsureAsync(check, context.OrganizationId, cancellationToken).ConfigureAwait(false);
            (classification, protectionMode) = FieldProtectionPolicy.Resolve(request.FieldDataType!, request.FieldClassification, request.FieldProtectionMode, policy);
            if (protectionMode == FieldProtectionModes.ApplicationEncrypted && request.FieldDataType != FieldDataTypes.File && !encryption.IsAvailable)
            {
                throw new Documents.Ports.EncryptionUnavailableException("no permitted encryption key provider is configured; an encrypted field cannot be created");
            }

            var existing = await check.GetFieldDefinitionsAsync(formDefinitionId, cancellationToken).ConfigureAwait(false);
            if (existing.Any(f => f.FieldKey == request.FieldKey))
            {
                throw new BusinessException("FIELD_KEY_EXISTS", 409, "field key already exists; re-add the existing field instead of creating a new identity");
            }

            if (existing.Count >= 250)
            {
                throw new BusinessException("LIMIT_EXCEEDED", 422, "a form may define at most 250 fields");
            }

            if (request.GroupDefinitionId is { } groupId)
            {
                if (request.FieldDataType == FieldDataTypes.File)
                {
                    throw new BusinessException("INVALID_REQUEST", 422, "document collections cannot be placed in a section that allows multiple entries");
                }

                if ((await check.GetGroupDefinitionsAsync(formDefinitionId, cancellationToken).ConfigureAwait(false)).All(g => g.GroupDefinitionId != groupId))
                {
                    throw new BusinessException("NOT_FOUND", 404, "repeating group not found on this form");
                }
            }

            await check.CommitAsync(cancellationToken).ConfigureAwait(false);
        }

        // The authority learns every encryption requirement at birth (Amendment A1): the requirement, with its classification,
        // is what later decides whether — and by whom — encryption may ever be removed. Documents are always encrypted and never released.
        var fieldDefinitionId = Guid.CreateVersion7();
        Guid? requirementCommandId = null;
        if (protectionMode == FieldProtectionModes.ApplicationEncrypted && request.FieldDataType != FieldDataTypes.File)
        {
            requirementCommandId = await PublishRequirementAsync(context, decision.AuthorizationRevision, formDefinitionId, fieldDefinitionId, classification, 1, "field created with application encryption", cancellationToken).ConfigureAwait(false);
        }

        var now = clock.GetUtcNow();
        await using var scope = await store.BeginScopeAsync(cancellationToken).ConfigureAwait(false);
        var fields = await scope.GetFieldDefinitionsAsync(formDefinitionId, cancellationToken).ConfigureAwait(false);
        if (fields.Any(f => f.FieldKey == request.FieldKey))
        {
            throw new BusinessException("FIELD_KEY_EXISTS", 409, "field key already exists; re-add the existing field instead of creating a new identity");
        }

        var field = new FieldDefinition
        {
            FieldDefinitionId = fieldDefinitionId,
            ProtectionAuthorityCommandId = requirementCommandId,
            FormDefinitionId = formDefinitionId,
            FieldKey = request.FieldKey!,
            FieldDisplayName = request.FieldDisplayName.Trim(),
            FieldDescription = request.FieldDescription?.Trim() ?? string.Empty,
            FieldDataType = request.FieldDataType!,
            FieldClassification = classification,
            FieldProtectionMode = protectionMode,
            IsSearchable = false,
            LifecycleStatus = "Available",
            CreatedOn = now,
            CreatedByUserId = context.LocalUserId,
            OptionSelectionMode = selectionMode,
            // Permanent: a value is either top-level or per entry for the life of the field (C-16).
            GroupDefinitionId = request.GroupDefinitionId,
        };
        var options = new List<FieldOption>();
        if (request.FieldDataType == FieldDataTypes.Option && request.OptionListId is { } optionListId)
        {
            // Bind a shared list: copy its items (stable ids of their own) so published versions and stored values never depend on later list edits.
            if (await scope.FindOptionListAsync(context.OrganizationId, optionListId, cancellationToken).ConfigureAwait(false) is not { LifecycleStatus: "Active" })
            {
                throw new BusinessException("NOT_FOUND", 404, "option list not found");
            }

            var items = await scope.GetOptionListItemsAsync(optionListId, cancellationToken).ConfigureAwait(false);
            if (items.Count == 0)
            {
                throw new BusinessException("INVALID_REQUEST", 422, "the option list has no items");
            }

            field.OptionListId = optionListId;
            scope.AddFieldDefinition(field);
            foreach (var item in items)
            {
                var option = new FieldOption { FieldOptionId = Guid.CreateVersion7(), FieldDefinitionId = field.FieldDefinitionId, OptionKey = item.OptionKey, OptionDisplayText = item.OptionDisplayText, OptionSortOrder = item.OptionSortOrder, IsActive = item.IsActive, OptionListItemId = item.OptionListItemId, CreatedOn = now, CreatedByUserId = context.LocalUserId, ModifiedOn = now, ModifiedByUserId = context.LocalUserId };
                scope.AddFieldOption(option);
                options.Add(option);
            }
        }
        else
        {
            scope.AddFieldDefinition(field);
        }

        if (request.FieldDataType == FieldDataTypes.Option && request.OptionListId is null)
        {
            var requested = request.Options ?? [];
            if (requested.Count is 0 or > 500 || requested.Select(o => o.OptionKey).Distinct(StringComparer.Ordinal).Count() != requested.Count || requested.Any(o => !KeyPattern().IsMatch(o.OptionKey ?? string.Empty) || string.IsNullOrWhiteSpace(o.OptionDisplayText) || o.OptionDisplayText.Length > 200 || o.OptionDisplayText.Contains('<', StringComparison.Ordinal)))
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

        AuditRecorder.Record(scope, context, clock, AuditCategories.Form, AuditActions.FormDraftUpdated, "field definition added", formDefinitionId: formDefinitionId, fieldDefinitionId: field.FieldDefinitionId, metadata: new { fieldKey = field.FieldKey, dataType = field.FieldDataType, classification = field.FieldClassification, protectionMode = field.FieldProtectionMode, authorityCommandId = requirementCommandId, optionSelectionMode = field.OptionSelectionMode, optionListId = field.OptionListId, groupDefinitionId = field.GroupDefinitionId });
        await scope.CommitAsync(cancellationToken).ConfigureAwait(false);
        return ToFieldResponses([field], options)[0];
    }

    /// <summary>
    /// Edits the catalog metadata of an existing field (display name, description). Identity (id, key, storage type,
    /// classification) is permanent: a request that names a different key or type is refused with an explanation, never
    /// silently ignored and never turned into a re-keyed field. Stored values and attachments reference the field id, so
    /// they are untouched. Published form versions are immutable snapshots (publish copies the catalog name into any
    /// element without its own label), so this edit only affects drafts and later versions. Per-version label, help text
    /// and validation are edited in the layout draft.
    /// </summary>
    public async Task<FieldDefinitionResponse> UpdateFieldAsync(RequestContext context, Guid formDefinitionId, Guid fieldDefinitionId, UpdateFieldDefinitionRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(request);
        await RequireDesignAsync(context, formDefinitionId, cancellationToken).ConfigureAwait(false);
        var displayName = request.FieldDisplayName?.Trim() ?? string.Empty;
        var description = request.FieldDescription?.Trim() ?? string.Empty;
        if (displayName.Length is 0 or > 200 || description.Length > 1000)
        {
            throw new BusinessException("INVALID_REQUEST", 422, "display name (1..200) is required and the description may be at most 1000 characters");
        }

        await using var scope = await store.BeginScopeAsync(cancellationToken).ConfigureAwait(false);
        var field = await scope.FindFieldDefinitionAsync(formDefinitionId, fieldDefinitionId, cancellationToken).ConfigureAwait(false)
                    ?? throw new BusinessException("NOT_FOUND", 404, "field not found");
        if (request.FieldKey is not null && !string.Equals(request.FieldKey, field.FieldKey, StringComparison.Ordinal))
        {
            throw new BusinessException("FIELD_KEY_IMMUTABLE", 422, $"the field key '{field.FieldKey}' is permanent (it is the stable name used by records, searches and integrations); change the display label instead, or add a new field and migrate deliberately");
        }

        if (request.FieldDataType is not null && !string.Equals(request.FieldDataType, field.FieldDataType, StringComparison.Ordinal))
        {
            throw new BusinessException("FIELD_TYPE_IMMUTABLE", 422, $"the storage type {field.FieldDataType} cannot change because stored values would be reinterpreted; add a new field of the new type and migrate values explicitly");
        }

        if (request.ExpectedFieldRevision != field.FieldDefinitionRevision)
        {
            throw new BusinessException("FIELD_REVISION_CONFLICT", 409, "the field was changed by someone else; reload and retry");
        }

        var nameChanged = !string.Equals(field.FieldDisplayName, displayName, StringComparison.Ordinal);
        var descriptionChanged = !string.Equals(field.FieldDescription, description, StringComparison.Ordinal);
        var previousName = field.FieldDisplayName;
        if (nameChanged || descriptionChanged)
        {
            var now = clock.GetUtcNow();
            field.FieldDisplayName = displayName;
            field.FieldDescription = description;
            field.FieldDefinitionRevision++;
            field.ModifiedOn = now;
            field.ModifiedByUserId = context.LocalUserId;
            AuditRecorder.Record(scope, context, clock, AuditCategories.Form, AuditActions.FieldConfigurationChanged, "field catalog metadata changed (identity, key and type unchanged)", formDefinitionId: formDefinitionId, fieldDefinitionId: fieldDefinitionId, metadata: new { fieldKey = field.FieldKey, dataType = field.FieldDataType, previousDisplayName = previousName, newDisplayName = displayName, descriptionChanged, fieldDefinitionRevision = field.FieldDefinitionRevision });
            await scope.CommitAsync(cancellationToken).ConfigureAwait(false);
        }

        var options = (await scope.GetFieldOptionsAsync(formDefinitionId, cancellationToken).ConfigureAwait(false)).Where(o => o.FieldDefinitionId == fieldDefinitionId).ToList();
        return ToFieldResponses([field], options)[0];
    }

    /// <summary>Current protection of a field plus the migration state (how many records still carry a legacy plaintext copy after a strengthening).</summary>
    public async Task<FieldProtectionResponse> GetProtectionAsync(RequestContext context, Guid formDefinitionId, Guid fieldDefinitionId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        await RequireDesignAsync(context, formDefinitionId, cancellationToken).ConfigureAwait(false);
        await using var scope = await store.BeginScopeAsync(cancellationToken).ConfigureAwait(false);
        var field = await scope.FindFieldDefinitionAsync(formDefinitionId, fieldDefinitionId, cancellationToken).ConfigureAwait(false)
                    ?? throw new BusinessException("NOT_FOUND", 404, "field not found");
        var policy = await Organizations.SecurityPolicyHandlers.EnsureAsync(scope, context.OrganizationId, cancellationToken).ConfigureAwait(false);
        var response = await ProtectionResponseAsync(scope, field, policy, cancellationToken).ConfigureAwait(false);
        await scope.CommitAsync(cancellationToken).ConfigureAwait(false);
        return response;
    }

    /// <summary>
    /// Explicit, audited migration step (ADR-PB-004 §Versioning): raise a field's classification and/or switch it from
    /// plaintext to application encryption. Never the reverse through this route. Future writes are encrypted immediately.
    /// Existing plaintext copies are re-protected by the background migration (B-28): the authority first accepts a
    /// PublishFormSecurityRevision command for the field (designer-authorized, journaled, revision-bound, carrying the
    /// classification), which also raises the authority's barrier against plaintext rows of the field; every record is then
    /// re-sealed through the ordinary content workflow as a reprotection operation. Until the migration completes and is
    /// verified, the field is reported as not fully encrypted.
    /// </summary>
    public async Task<FieldProtectionResponse> StrengthenProtectionAsync(RequestContext context, Guid formDefinitionId, Guid fieldDefinitionId, StrengthenFieldProtectionRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(request);
        var decision = await authority.CheckPermissionAsync(context.ToDelegatedUser(), new PermissionCheckRequest(context.OrganizationId, new ResourceReference(ProtectedResourceKind.FormDefinition, formDefinitionId), ProtectedAction.DesignForm), cancellationToken).ConfigureAwait(false);
        if (!decision.Allowed)
        {
            throw new BusinessException("NOT_FOUND", 404, "form definition not found");
        }

        var reason = RequireReason(request.ChangeReason);
        FieldDefinition field;
        Domain.Features.Organizations.OrganizationSecurityPolicy policy;
        string classification;
        string mode;
        List<LegacyPlaintextRecord> legacyRecords = [];
        await using (var scope = await store.BeginScopeAsync(cancellationToken).ConfigureAwait(false))
        {
            field = await scope.FindFieldDefinitionAsync(formDefinitionId, fieldDefinitionId, cancellationToken).ConfigureAwait(false)
                    ?? throw new BusinessException("NOT_FOUND", 404, "field not found");
            if (request.ExpectedProtectionRevision != field.ProtectionPolicyRevision)
            {
                throw new BusinessException("FIELD_REVISION_CONFLICT", 409, "the field's protection was changed by someone else; reload and retry");
            }

            policy = await Organizations.SecurityPolicyHandlers.EnsureAsync(scope, context.OrganizationId, cancellationToken).ConfigureAwait(false);
            (classification, mode) = FieldProtectionPolicy.ResolveStrengthening(field, request.TargetClassification, request.TargetProtectionMode, policy);
            await RequireNoRunningMigrationAsync(scope, fieldDefinitionId, cancellationToken).ConfigureAwait(false);
            if (mode == FieldProtectionModes.ApplicationEncrypted && field.FieldProtectionMode != FieldProtectionModes.ApplicationEncrypted && field.FieldDataType != FieldDataTypes.File)
            {
                legacyRecords = (await scope.EstimateOldFormRecordsAsync(fieldDefinitionId, encryptedForm: false, cancellationToken).ConfigureAwait(false)).RecordsToVisit();
            }

            await scope.CommitAsync(cancellationToken).ConfigureAwait(false);
        }

        var switchingToEncryption = mode == FieldProtectionModes.ApplicationEncrypted && field.FieldProtectionMode != FieldProtectionModes.ApplicationEncrypted && field.FieldDataType != FieldDataTypes.File;
        if (switchingToEncryption && !encryption.IsAvailable)
        {
            throw new Documents.Ports.EncryptionUnavailableException("no permitted encryption key provider is configured; the field cannot be switched to encryption");
        }

        var newRevision = field.ProtectionPolicyRevision + 1;
        Guid? commandId = null;
        // The authority learns every encryption requirement and the classification it carries: a new requirement, or a raised classification of an encrypted field.
        if (mode == FieldProtectionModes.ApplicationEncrypted && field.FieldDataType != FieldDataTypes.File && (switchingToEncryption || classification != field.FieldClassification))
        {
            commandId = await PublishRequirementAsync(context, decision.AuthorizationRevision, formDefinitionId, fieldDefinitionId, classification, newRevision, reason, cancellationToken).ConfigureAwait(false);
        }

        var now = clock.GetUtcNow();
        await using (var scope = await store.BeginScopeAsync(cancellationToken).ConfigureAwait(false))
        {
            var tracked = await scope.FindFieldDefinitionAsync(formDefinitionId, fieldDefinitionId, cancellationToken).ConfigureAwait(false)
                          ?? throw new BusinessException("NOT_FOUND", 404, "field not found");
            if (tracked.ProtectionPolicyRevision != field.ProtectionPolicyRevision)
            {
                throw new BusinessException("FIELD_REVISION_CONFLICT", 409, "the field's protection was changed concurrently; reload and retry");
            }

            var previous = new { tracked.FieldClassification, tracked.FieldProtectionMode, tracked.ProtectionPolicyRevision };
            tracked.FieldClassification = classification;
            tracked.FieldProtectionMode = mode;
            tracked.ProtectionPolicyRevision = newRevision;
            tracked.ProtectionChangedOn = now;
            tracked.ProtectionChangedByUserId = context.LocalUserId;
            if (commandId is { } authorizedBy)
            {
                tracked.ProtectionAuthorityCommandId = authorizedBy;
            }

            if (switchingToEncryption && commandId is { } migrationCommand)
            {
                QueueMigration(scope, context, formDefinitionId, fieldDefinitionId, newRevision, migrationCommand, FieldProtectionMigrationDirections.Encrypt, legacyRecords, null, now);
            }

            AuditRecorder.Record(scope, context, clock, AuditCategories.Security, AuditActions.FieldProtectionStrengthened, "field classification/protection strengthened (audited migration)", formDefinitionId: formDefinitionId, fieldDefinitionId: fieldDefinitionId, metadata: new { fieldKey = tracked.FieldKey, previous, classification, protectionMode = mode, protectionPolicyRevision = newRevision, changeReason = reason, authorityCommandId = commandId });
            await scope.CommitAsync(cancellationToken).ConfigureAwait(false);
            return await ProtectionResponseAsync(scope, tracked, policy, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Amendment A1 step 1: declassify an encrypted Confidential/Restricted field to Public or Internal. Organization owner or
    /// system administrator only; the authority's ReclassifyField command journals the governance approval reference and
    /// registers the pending declassification that only a <em>different</em> principal's downgrade may consume. The field stays
    /// encrypted; nothing about record access changes.
    /// </summary>
    public async Task<FieldProtectionResponse> ReclassifyFieldAsync(RequestContext context, Guid formDefinitionId, Guid fieldDefinitionId, ReclassifyFieldRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(request);
        var decision = await RequireOrganizationAdministratorAsync(context, formDefinitionId, cancellationToken).ConfigureAwait(false);
        var reason = RequireReason(request.ChangeReason);
        var approval = request.ApprovalReference?.Trim() ?? string.Empty;
        if (approval.Length is < 1 or > 200)
        {
            throw new BusinessException("INVALID_REQUEST", 422, "a governance approval reference (1..200 characters) is required");
        }

        var target = request.TargetClassification?.Trim() ?? string.Empty;
        if (target is not (FieldClassifications.Public or FieldClassifications.Internal))
        {
            throw new BusinessException("INVALID_REQUEST", 422, "a field can only be declassified to Public or Internal");
        }

        FieldDefinition field;
        Domain.Features.Organizations.OrganizationSecurityPolicy policy;
        await using (var scope = await store.BeginScopeAsync(cancellationToken).ConfigureAwait(false))
        {
            field = await scope.FindFieldDefinitionAsync(formDefinitionId, fieldDefinitionId, cancellationToken).ConfigureAwait(false)
                    ?? throw new BusinessException("NOT_FOUND", 404, "field not found");
            policy = await Organizations.SecurityPolicyHandlers.EnsureAsync(scope, context.OrganizationId, cancellationToken).ConfigureAwait(false);
            if (field.FieldProtectionMode != FieldProtectionModes.ApplicationEncrypted || field.FieldDataType == FieldDataTypes.File)
            {
                throw new BusinessException("INVALID_REQUEST", 422, "declassification applies to an application-encrypted field (documents are always encrypted)");
            }

            if (FieldProtectionPolicy.Rank(target) >= FieldProtectionPolicy.Rank(field.FieldClassification))
            {
                throw new BusinessException("INVALID_REQUEST", 422, $"the field is already {field.FieldClassification}; declassification lowers the classification");
            }

            if (!policy.PlaintextDowngradePermitted || FieldProtectionPolicy.Rank(target) < FieldProtectionPolicy.Rank(policy.MinimumFieldClassification))
            {
                throw new BusinessException("PLAINTEXT_DOWNGRADE_NOT_PERMITTED", 422, "the organization's data-protection policy does not permit removing encryption; the organization owner must enable it first");
            }

            await RequireNoRunningMigrationAsync(scope, fieldDefinitionId, cancellationToken).ConfigureAwait(false);
            await scope.CommitAsync(cancellationToken).ConfigureAwait(false);
        }

        var newRevision = field.ProtectionPolicyRevision + 1;
        var payload = JsonSerializer.SerializeToElement(new { form_definition_id = formDefinitionId, field_definition_id = fieldDefinitionId, target_classification = target, approval_reference = approval });
        var command = await awaiter.SubmitAndAwaitAsync(context.ToDelegatedUser(), new SecurityCommandRequest(context.OrganizationId, SecurityCommandKind.ReclassifyField, new ResourceReference(ProtectedResourceKind.FormDefinition, formDefinitionId), decision.AuthorizationRevision, payload, reason), $"reclassify:{fieldDefinitionId:N}:{newRevision}:{target}:{context.LocalUserId:N}", cancellationToken).ConfigureAwait(false);
        if (command.CommandStatus != SecurityCommandStatus.Effective)
        {
            throw new PendingCommandException(new CommandOutcome(command.CommandId, command.CommandStatus.ToString(), command.AuthorizationRevision, false));
        }

        var now = clock.GetUtcNow();
        await using (var scope = await store.BeginScopeAsync(cancellationToken).ConfigureAwait(false))
        {
            var tracked = await scope.FindFieldDefinitionAsync(formDefinitionId, fieldDefinitionId, cancellationToken).ConfigureAwait(false)
                          ?? throw new BusinessException("NOT_FOUND", 404, "field not found");
            var previous = tracked.FieldClassification;
            tracked.FieldClassification = target;
            tracked.ProtectionPolicyRevision = newRevision;
            tracked.ProtectionChangedOn = now;
            tracked.ProtectionChangedByUserId = context.LocalUserId;
            AuditRecorder.Record(scope, context, clock, AuditCategories.Security, AuditActions.FieldDeclassified, "field declassified by an organization administrator (governance approval journaled by the authority); the field stays encrypted until a distinct administrator releases it", formDefinitionId: formDefinitionId, fieldDefinitionId: fieldDefinitionId, metadata: new { fieldKey = tracked.FieldKey, previousClassification = previous, targetClassification = target, approvalReference = approval, changeReason = reason, authorityCommandId = command.CommandId, protectionPolicyRevision = newRevision });
            await scope.CommitAsync(cancellationToken).ConfigureAwait(false);
            return await ProtectionResponseAsync(scope, tracked, policy, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Amendment A1 step 2: release a Public/Internal field from application encryption. Organization owner or system
    /// administrator only (the authority additionally requires a distinct principal and a fresh declassification for fields it
    /// recorded as Confidential/Restricted, and the authority-held policy must permit downgrades). The command is the atomic
    /// cutover: as soon as it is effective no user write may commit an encrypted row of the field, new writes are plaintext,
    /// and the background migration decrypts the remaining active values. "Fully migrated to plaintext" is claimed only after
    /// every record completed and a post-check against the authority-accepted heads finds no encrypted row.
    /// </summary>
    public async Task<FieldProtectionResponse> DowngradeProtectionAsync(RequestContext context, Guid formDefinitionId, Guid fieldDefinitionId, DowngradeFieldProtectionRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(request);
        var decision = await RequireOrganizationAdministratorAsync(context, formDefinitionId, cancellationToken).ConfigureAwait(false);
        var reason = RequireReason(request.ChangeReason);
        FieldDefinition field;
        Domain.Features.Organizations.OrganizationSecurityPolicy policy;
        List<LegacyPlaintextRecord> encryptedRecords;
        await using (var scope = await store.BeginScopeAsync(cancellationToken).ConfigureAwait(false))
        {
            field = await scope.FindFieldDefinitionAsync(formDefinitionId, fieldDefinitionId, cancellationToken).ConfigureAwait(false)
                    ?? throw new BusinessException("NOT_FOUND", 404, "field not found");
            if (request.ExpectedProtectionRevision != field.ProtectionPolicyRevision)
            {
                throw new BusinessException("FIELD_REVISION_CONFLICT", 409, "the field's protection was changed by someone else; reload and retry");
            }

            policy = await Organizations.SecurityPolicyHandlers.EnsureAsync(scope, context.OrganizationId, cancellationToken).ConfigureAwait(false);
            if (field.FieldProtectionMode != FieldProtectionModes.ApplicationEncrypted || field.FieldDataType == FieldDataTypes.File)
            {
                throw new BusinessException("INVALID_REQUEST", 422, "the field is not application-encrypted (documents are always encrypted)");
            }

            if (field.FieldClassification is not (FieldClassifications.Public or FieldClassifications.Internal))
            {
                throw new BusinessException("DECLASSIFICATION_REQUIRED", 422, $"the field is {field.FieldClassification}; an organization administrator must declassify it (with a governance approval) before a different administrator can remove encryption");
            }

            if (!policy.PlaintextDowngradePermitted || !policy.PlaintextInternalFieldsPermitted || FieldProtectionPolicy.Rank(field.FieldClassification) < FieldProtectionPolicy.Rank(policy.MinimumFieldClassification))
            {
                throw new BusinessException("PLAINTEXT_DOWNGRADE_NOT_PERMITTED", 422, "the organization's data-protection policy does not permit plaintext for this field");
            }

            await RequireNoRunningMigrationAsync(scope, fieldDefinitionId, cancellationToken).ConfigureAwait(false);
            encryptedRecords = (await scope.EstimateOldFormRecordsAsync(fieldDefinitionId, encryptedForm: true, cancellationToken).ConfigureAwait(false)).RecordsToVisit();
            await scope.CommitAsync(cancellationToken).ConfigureAwait(false);
        }

        if (!encryption.IsAvailable)
        {
            throw new Documents.Ports.EncryptionUnavailableException("no permitted encryption key provider is configured; stored values cannot be decrypted for the migration");
        }

        var newRevision = field.ProtectionPolicyRevision + 1;
        var payload = JsonSerializer.SerializeToElement(new { form_definition_id = formDefinitionId, field_definition_id = fieldDefinitionId, required_protection_mode = FieldProtectionModes.Standard, protection_policy_revision = newRevision, field_classification = field.FieldClassification });
        var command = await awaiter.SubmitAndAwaitAsync(context.ToDelegatedUser(), new SecurityCommandRequest(context.OrganizationId, SecurityCommandKind.PublishFormSecurityRevision, new ResourceReference(ProtectedResourceKind.FormDefinition, formDefinitionId), decision.AuthorizationRevision, payload, reason), $"field-release:{fieldDefinitionId:N}:{newRevision}:{context.LocalUserId:N}", cancellationToken).ConfigureAwait(false);
        if (command.CommandStatus != SecurityCommandStatus.Effective)
        {
            throw new PendingCommandException(new CommandOutcome(command.CommandId, command.CommandStatus.ToString(), command.AuthorizationRevision, false));
        }

        var now = clock.GetUtcNow();
        await using (var scope = await store.BeginScopeAsync(cancellationToken).ConfigureAwait(false))
        {
            var tracked = await scope.FindFieldDefinitionAsync(formDefinitionId, fieldDefinitionId, cancellationToken).ConfigureAwait(false)
                          ?? throw new BusinessException("NOT_FOUND", 404, "field not found");
            if (tracked.ProtectionPolicyRevision != field.ProtectionPolicyRevision)
            {
                throw new BusinessException("FIELD_REVISION_CONFLICT", 409, "the field's protection was changed concurrently; reload and retry");
            }

            tracked.FieldProtectionMode = FieldProtectionModes.Standard; // new writes are plaintext from here (the authority refuses encrypted rows of the field from the command on)
            tracked.ProtectionPolicyRevision = newRevision;
            tracked.ProtectionChangedOn = now;
            tracked.ProtectionChangedByUserId = context.LocalUserId;
            tracked.ProtectionAuthorityCommandId = command.CommandId;
            var migrationId = QueueMigration(scope, context, formDefinitionId, fieldDefinitionId, newRevision, command.CommandId, FieldProtectionMigrationDirections.Decrypt, encryptedRecords, null, now);
            AuditRecorder.Record(scope, context, clock, AuditCategories.Security, AuditActions.FieldProtectionDowngraded, "field released from application encryption by an organization administrator (Amendment A1); background decryption migration queued; plaintext exposure of historical revisions, logs, WAL and backups is not reversible", formDefinitionId: formDefinitionId, fieldDefinitionId: fieldDefinitionId, targetEntityType: "field_protection_migration", targetEntityId: migrationId, metadata: new { fieldKey = tracked.FieldKey, classification = tracked.FieldClassification, changeReason = reason, authorityCommandId = command.CommandId, protectionPolicyRevision = newRevision, totalRecordCount = encryptedRecords.Count, totalValueCount = encryptedRecords.Sum(r => r.PlaintextValueCount) });
            await scope.CommitAsync(cancellationToken).ConfigureAwait(false);
            return await ProtectionResponseAsync(scope, tracked, policy, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Amendment A1 rollback: re-require encryption. The authority command flips the barrier back (no user write may commit a
    /// plaintext row of the field from then on); the running or finished decrypt migration is marked Cancelled and a new
    /// Encrypt migration re-protects every active value already converted. Historical plaintext revisions, logs, WAL and
    /// backups are not reversible; the exposure window is recorded in the audit event.
    /// </summary>
    public async Task<FieldProtectionResponse> CancelDowngradeAsync(RequestContext context, Guid formDefinitionId, Guid fieldDefinitionId, CancelFieldProtectionDowngradeRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(request);
        var decision = await RequireOrganizationAdministratorAsync(context, formDefinitionId, cancellationToken).ConfigureAwait(false);
        var reason = RequireReason(request.ChangeReason);
        FieldDefinition field;
        FieldProtectionMigration decrypting;
        Domain.Features.Organizations.OrganizationSecurityPolicy policy;
        List<LegacyPlaintextRecord> plaintextRecords;
        await using (var scope = await store.BeginScopeAsync(cancellationToken).ConfigureAwait(false))
        {
            field = await scope.FindFieldDefinitionAsync(formDefinitionId, fieldDefinitionId, cancellationToken).ConfigureAwait(false)
                    ?? throw new BusinessException("NOT_FOUND", 404, "field not found");
            policy = await Organizations.SecurityPolicyHandlers.EnsureAsync(scope, context.OrganizationId, cancellationToken).ConfigureAwait(false);
            decrypting = await scope.FindLatestProtectionMigrationAsync(fieldDefinitionId, cancellationToken).ConfigureAwait(false)
                         ?? throw new BusinessException("NOT_FOUND", 404, "the field has no encryption downgrade to cancel");
            if (decrypting.MigrationDirection != FieldProtectionMigrationDirections.Decrypt || decrypting.MigrationState == FieldProtectionMigrationStates.Cancelled)
            {
                throw new BusinessException("NO_DOWNGRADE_TO_CANCEL", 409, "the field's latest protection change is not an active or completed downgrade");
            }

            plaintextRecords = (await scope.EstimateOldFormRecordsAsync(fieldDefinitionId, encryptedForm: false, cancellationToken).ConfigureAwait(false)).RecordsToVisit();
            await scope.CommitAsync(cancellationToken).ConfigureAwait(false);
        }

        if (!encryption.IsAvailable)
        {
            throw new Documents.Ports.EncryptionUnavailableException("no permitted encryption key provider is configured; values cannot be re-encrypted");
        }

        var newRevision = field.ProtectionPolicyRevision + 1;
        var commandId = await PublishRequirementAsync(context, decision.AuthorizationRevision, formDefinitionId, fieldDefinitionId, field.FieldClassification, newRevision, reason, cancellationToken).ConfigureAwait(false);
        var now = clock.GetUtcNow();
        await using (var scope = await store.BeginScopeAsync(cancellationToken).ConfigureAwait(false))
        {
            var tracked = await scope.FindFieldDefinitionAsync(formDefinitionId, fieldDefinitionId, cancellationToken).ConfigureAwait(false)
                          ?? throw new BusinessException("NOT_FOUND", 404, "field not found");
            var migration = await scope.FindProtectionMigrationAsync(decrypting.FieldProtectionMigrationId, cancellationToken).ConfigureAwait(false);
            tracked.FieldProtectionMode = FieldProtectionModes.ApplicationEncrypted;
            tracked.ProtectionPolicyRevision = newRevision;
            tracked.ProtectionChangedOn = now;
            tracked.ProtectionChangedByUserId = context.LocalUserId;
            tracked.ProtectionAuthorityCommandId = commandId;
            if (migration is not null && migration.MigrationState != FieldProtectionMigrationStates.Cancelled)
            {
                migration.MigrationState = FieldProtectionMigrationStates.Cancelled;
                migration.MigrationCancelledOn = now;
                migration.LeaseOwnerName = null;
                migration.LeaseExpiresOn = null;
                migration.NextAttemptOn = DateTimeOffset.MaxValue;
                migration.MigrationModifiedOn = now;
            }

            var rollbackId = QueueMigration(scope, context, formDefinitionId, fieldDefinitionId, newRevision, commandId, FieldProtectionMigrationDirections.Encrypt, plaintextRecords, decrypting.FieldProtectionMigrationId, now);
            AuditRecorder.Record(scope, context, clock, AuditCategories.Security, AuditActions.FieldProtectionDowngradeCancelled, "encryption downgrade cancelled by an organization administrator; active values already decrypted are re-encrypted by a rollback migration. Plaintext held in historical revisions, logs, WAL or backups during the exposure window is not reversible", formDefinitionId: formDefinitionId, fieldDefinitionId: fieldDefinitionId, targetEntityType: "field_protection_migration", targetEntityId: rollbackId, metadata: new { fieldKey = tracked.FieldKey, changeReason = reason, authorityCommandId = commandId, cancelledMigrationId = decrypting.FieldProtectionMigrationId, exposureWindowStart = decrypting.MigrationCreatedOn, exposureWindowEnd = now, decryptedRecordsToReencrypt = plaintextRecords.Count });
            await scope.CommitAsync(cancellationToken).ConfigureAwait(false);
            return await ProtectionResponseAsync(scope, tracked, policy, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Re-queues the failed records of a field's protection migration (design permission for encrypt migrations, organization administration for decrypt ones; audited). Nothing is re-signed here; the worker runs the authority workflow again.</summary>
    public async Task<FieldProtectionResponse> RetryProtectionMigrationAsync(RequestContext context, Guid formDefinitionId, Guid fieldDefinitionId, RetryFieldProtectionMigrationRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(request);
        var reason = RequireReason(request.RetryReason);
        // Encrypt migrations are retried by a designer of the form; decrypt migrations (Amendment A1) only by an organization administrator.
        // The direction is read before authorizing, but the form must exist in the caller's organization either way (same 404 otherwise).
        string direction;
        await using (var peek = await store.BeginScopeAsync(cancellationToken).ConfigureAwait(false))
        {
            _ = await peek.FindFormDefinitionAsync(context.OrganizationId, formDefinitionId, cancellationToken).ConfigureAwait(false)
                ?? throw new BusinessException("NOT_FOUND", 404, "form definition not found");
            direction = (await peek.FindLatestProtectionMigrationAsync(fieldDefinitionId, cancellationToken).ConfigureAwait(false))?.MigrationDirection ?? FieldProtectionMigrationDirections.Encrypt;
        }

        if (direction == FieldProtectionMigrationDirections.Decrypt)
        {
            await RequireOrganizationAdministratorAsync(context, formDefinitionId, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await RequireDesignAsync(context, formDefinitionId, cancellationToken).ConfigureAwait(false);
        }

        await using var scope = await store.BeginScopeAsync(cancellationToken).ConfigureAwait(false);
        var field = await scope.FindFieldDefinitionAsync(formDefinitionId, fieldDefinitionId, cancellationToken).ConfigureAwait(false)
                    ?? throw new BusinessException("NOT_FOUND", 404, "field not found");
        var migration = await scope.FindLatestProtectionMigrationAsync(fieldDefinitionId, cancellationToken).ConfigureAwait(false)
                        ?? throw new BusinessException("NOT_FOUND", 404, "the field has no protection migration");
        if (migration.MigrationState is not (FieldProtectionMigrationStates.Failed or FieldProtectionMigrationStates.Blocked))
        {
            throw new BusinessException("MIGRATION_NOT_RETRYABLE", 409, "only a failed or blocked migration can be retried");
        }

        var now = clock.GetUtcNow();
        var failed = await scope.GetFailedProtectionMigrationItemsAsync(migration.FieldProtectionMigrationId, cancellationToken).ConfigureAwait(false);
        foreach (var item in failed)
        {
            item.ItemState = FieldProtectionMigrationItemStates.Pending;
            item.AttemptCount = 0;
            item.LastErrorCode = null;
            item.ItemModifiedOn = now;
        }

        migration.FailedRecordCount = 0;
        migration.MigrationState = FieldProtectionMigrationStates.Queued;
        migration.LastErrorCode = null;
        migration.NextAttemptOn = now;
        migration.LeaseOwnerName = null;
        migration.LeaseExpiresOn = null;
        migration.MigrationModifiedOn = now;
        var policy = await Organizations.SecurityPolicyHandlers.EnsureAsync(scope, context.OrganizationId, cancellationToken).ConfigureAwait(false);
        AuditRecorder.Record(scope, context, clock, AuditCategories.Security, AuditActions.FieldProtectionMigrationRetried, "failed records of the protection migration re-queued", formDefinitionId: formDefinitionId, fieldDefinitionId: fieldDefinitionId, targetEntityType: "field_protection_migration", targetEntityId: migration.FieldProtectionMigrationId, metadata: new { requeued = failed.Count, retryReason = reason, direction = migration.MigrationDirection });
        await scope.CommitAsync(cancellationToken).ConfigureAwait(false);
        return await ProtectionResponseAsync(scope, field, policy, cancellationToken).ConfigureAwait(false);
    }

    private static string RequireReason(string? text)
    {
        var reason = text?.Trim() ?? string.Empty;
        if (reason.Length is < 3 or > 500)
        {
            throw new BusinessException("INVALID_REQUEST", 422, "a reason of 3..500 characters is required");
        }

        return reason;
    }

    /// <summary>Organization owner (authority ManageOrganization) or system administrator (authority AdministerSecurity) on the organization, and the form must exist in this organization; designers alone are refused (Amendment A1 rule 5). The authority's planner re-checks the same roles when the command is submitted.</summary>
    private async Task<PermissionDecision> RequireOrganizationAdministratorAsync(RequestContext context, Guid formDefinitionId, CancellationToken cancellationToken)
    {
        var decision = await authority.CheckPermissionAsync(context.ToDelegatedUser(), new PermissionCheckRequest(context.OrganizationId, new ResourceReference(ProtectedResourceKind.Organization, context.OrganizationId), ProtectedAction.ManageOrganization), cancellationToken).ConfigureAwait(false);
        if (!decision.Allowed)
        {
            decision = await authority.CheckPermissionAsync(context.ToDelegatedUser(), new PermissionCheckRequest(context.OrganizationId, new ResourceReference(ProtectedResourceKind.Organization, context.OrganizationId), ProtectedAction.AdministerSecurity), cancellationToken).ConfigureAwait(false);
        }

        if (!decision.Allowed)
        {
            throw new BusinessException("FORBIDDEN", 403, "changing a field's encryption downward requires the organization owner or a system administrator");
        }

        await using var scope = await store.BeginScopeAsync(cancellationToken).ConfigureAwait(false);
        _ = await scope.FindFormDefinitionAsync(context.OrganizationId, formDefinitionId, cancellationToken).ConfigureAwait(false)
            ?? throw new BusinessException("NOT_FOUND", 404, "form definition not found");
        return decision;
    }

    private static async Task RequireNoRunningMigrationAsync(IBusinessScope scope, Guid fieldDefinitionId, CancellationToken cancellationToken)
    {
        var running = await scope.FindLatestProtectionMigrationAsync(fieldDefinitionId, cancellationToken).ConfigureAwait(false);
        if (running is not null && running.MigrationState is not (FieldProtectionMigrationStates.Completed or FieldProtectionMigrationStates.Cancelled))
        {
            throw new BusinessException("MIGRATION_IN_PROGRESS", 409, "the field's protection migration has not completed; wait for it (retry a failed one, or cancel a downgrade) before changing the protection again");
        }
    }

    private async Task<Guid> PublishRequirementAsync(RequestContext context, long authorizationRevision, Guid formDefinitionId, Guid fieldDefinitionId, string classification, long newRevision, string reason, CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.SerializeToElement(new { form_definition_id = formDefinitionId, field_definition_id = fieldDefinitionId, required_protection_mode = FieldProtectionModes.ApplicationEncrypted, protection_policy_revision = newRevision, field_classification = classification });
        var command = await awaiter.SubmitAndAwaitAsync(context.ToDelegatedUser(), new SecurityCommandRequest(context.OrganizationId, SecurityCommandKind.PublishFormSecurityRevision, new ResourceReference(ProtectedResourceKind.FormDefinition, formDefinitionId), authorizationRevision, payload, reason), $"field-protection:{fieldDefinitionId:N}:{newRevision}:{context.LocalUserId:N}", cancellationToken).ConfigureAwait(false);
        if (command.CommandStatus != SecurityCommandStatus.Effective)
        {
            throw new PendingCommandException(new CommandOutcome(command.CommandId, command.CommandStatus.ToString(), command.AuthorizationRevision, false));
        }

        return command.CommandId;
    }

    private Guid QueueMigration(IBusinessScope scope, RequestContext context, Guid formDefinitionId, Guid fieldDefinitionId, long revision, Guid commandId, string direction, List<LegacyPlaintextRecord> records, Guid? rollbackOf, DateTimeOffset now)
    {
        var migration = new FieldProtectionMigration
        {
            FieldProtectionMigrationId = Guid.CreateVersion7(),
            TenantId = context.OrganizationId,
            FormDefinitionId = formDefinitionId,
            FieldDefinitionId = fieldDefinitionId,
            ProtectionPolicyRevision = revision,
            AuthorityCommandId = commandId,
            MigrationDirection = direction,
            RollbackOfMigrationId = rollbackOf,
            // Phase C6 M0: always queued, even when the local estimate finds nothing to convert. Completion (and the
            // "all listed records" claim) comes only from the worker's post-check against the Authority-accepted heads; a
            // stale local projection must never be able to mark a migration verified on its own.
            MigrationState = FieldProtectionMigrationStates.Queued,
            TotalRecordCount = records.Count,
            TotalValueCount = records.Sum(r => r.PlaintextValueCount),
            NextAttemptOn = now,
            StartedByUserId = context.LocalUserId,
            MigrationCreatedOn = now,
            MigrationModifiedOn = now,
        };
        scope.AddProtectionMigration(migration);
        foreach (var record in records)
        {
            scope.AddProtectionMigrationItem(new FieldProtectionMigrationItem { FieldProtectionMigrationItemId = Guid.CreateVersion7(), FieldProtectionMigrationId = migration.FieldProtectionMigrationId, TenantId = context.OrganizationId, FormRecordId = record.FormRecordId, ItemModifiedOn = now });
        }

        AuditRecorder.Record(scope, context, clock, AuditCategories.Security, AuditActions.FieldProtectionMigrationStarted, records.Count == 0 ? "no existing value in the old form found locally; the migration worker verifies every listed record against the Authorization Authority" : $"background {direction.ToLowerInvariant()} migration of existing values queued (authority-governed operations)", formDefinitionId: formDefinitionId, fieldDefinitionId: fieldDefinitionId, targetEntityType: "field_protection_migration", targetEntityId: migration.FieldProtectionMigrationId, metadata: new { authorityCommandId = commandId, direction, totalRecordCount = migration.TotalRecordCount, totalValueCount = migration.TotalValueCount, rollbackOfMigrationId = rollbackOf });
        return migration.FieldProtectionMigrationId;
    }

    /// <summary>
    /// Status (Phase C6 M0): the worker-verified result of the latest migration and the local estimate are reported separately
    /// and combined by <see cref="ProtectionMigration.FieldProtectionStatusEvaluator"/>. <c>FullyEncrypted</c> /
    /// <c>FullyPlaintext</c> need a completed migration whose stored post-check (Authority-accepted heads of every listed
    /// record) found nothing remaining and nothing unreadable, <b>and</b> a local estimate that agrees with no unresolved
    /// record. A forged "Completed" row alone cannot make either claim, but everything here rests on business-database data:
    /// an administrator who writes several tables consistently, or removes records from form_records, can make it wrong.
    /// Display/status flags only — never used for authorization, decryption, reads or search — and NOT an independently
    /// verified completeness guarantee (G19, SECURITY_GAP_ASSESSMENT.md).
    /// </summary>
    private static async Task<FieldProtectionResponse> ProtectionResponseAsync(IBusinessScope scope, FieldDefinition field, Domain.Features.Organizations.OrganizationSecurityPolicy policy, CancellationToken cancellationToken)
    {
        var encrypted = field.FieldProtectionMode == FieldProtectionModes.ApplicationEncrypted;
        var isFile = field.FieldDataType == FieldDataTypes.File;
        var estimate = isFile ? new LegacyRecordEstimate([], []) : await scope.EstimateOldFormRecordsAsync(field.FieldDefinitionId, encryptedForm: !encrypted, cancellationToken).ConfigureAwait(false);
        var migration = isFile ? null : await scope.FindLatestProtectionMigrationAsync(field.FieldDefinitionId, cancellationToken).ConfigureAwait(false);
        FieldProtectionMigrationResponse? progress = null;
        FieldProtectionVerificationResponse? verification = null;
        if (migration is not null)
        {
            var pending = await scope.CountPendingProtectionMigrationItemsAsync(migration.FieldProtectionMigrationId, cancellationToken).ConfigureAwait(false);
            progress = new FieldProtectionMigrationResponse(migration.FieldProtectionMigrationId, migration.MigrationState, migration.TotalRecordCount, migration.TotalValueCount, migration.CompletedRecordCount, migration.FailedRecordCount, pending, migration.LastErrorCode, migration.MigrationCreatedOn, migration.MigrationCompletedOn, migration.MigrationVerifiedOn, migration.AuthorityCommandId, migration.MigrationDirection, migration.MigrationCancelledOn, migration.RollbackOfMigrationId);
            if (migration.VerificationCheckedOn is { } checkedOn)
            {
                verification = new FieldProtectionVerificationResponse(checkedOn, migration.VerifiedRecordCount, migration.VerifiedRemainingRecordCount ?? 0, migration.VerifiedRemainingValueCount ?? 0, migration.UnverifiedRecordCount ?? 0, VerificationBasis);
            }
        }

        var status = ProtectionMigration.FieldProtectionStatusEvaluator.Evaluate(encrypted, isFile, estimate, migration);
        var legacyPlaintext = encrypted && !isFile ? estimate.HolderValueCount : 0;
        var legacyEncrypted = !encrypted && !isFile ? estimate.HolderRecordCount : 0;
        return new FieldProtectionResponse(field.FieldDefinitionId, field.FieldKey, field.FieldClassification, field.FieldProtectionMode, field.ProtectionPolicyRevision, field.ProtectionChangedOn, status.State, legacyPlaintext,
            encrypted ? "Encrypted for privacy — not searchable in V1 (ADR-PB-004). Authorized users see the value on the record; no plaintext copy exists in the business database." : "Plaintext: searchable within authorization, and readable by a database administrator.",
            policy.MinimumFieldClassification, policy.PlaintextInternalFieldsPermitted, status.FullyEncrypted, progress, status.FullyPlaintext, legacyEncrypted,
            isFile ? 0 : estimate.UnresolvedRecordIds.Count, verification);
    }

    /// <summary>What the stored post-check result does and does not establish (shown with it).</summary>
    public const string VerificationBasis = "Checked by the migration worker against the Authorization Authority's accepted version of each record the business database lists. The result is stored in the business database: it is not protected against a database administrator, and records missing from the business database are not covered (security finding G19).";

    /// <summary>
    /// Adds, relabels, reorders or deactivates options of a dropdown field. Option ids and keys are permanent and nothing is
    /// ever deleted, so stored selections keep pointing at the same option; a deactivated option stays visible (marked) on
    /// the records that hold it and cannot be chosen for new saves. An option copied from a shared list keeps the list's
    /// label (use the list to relabel); order and activation remain local to the field.
    /// </summary>
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

        var changes = request.Changes ?? [];
        if (changes.Count > 500 || changes.GroupBy(c => c.OptionKey, StringComparer.Ordinal).Any(g => g.Count() > 1))
        {
            throw new BusinessException("INVALID_REQUEST", 422, "at most 500 changes, one per option key");
        }

        var existing = (await scope.GetFieldOptionsAsync(formDefinitionId, cancellationToken).ConfigureAwait(false)).Where(o => o.FieldDefinitionId == fieldDefinitionId).ToList();
        var added = new List<string>();
        var relabeled = new List<string>();
        var deactivated = new List<string>();
        var reactivated = new List<string>();
        foreach (var change in changes)
        {
            if (!KeyPattern().IsMatch(change.OptionKey ?? string.Empty) || string.IsNullOrWhiteSpace(change.OptionDisplayText) || change.OptionDisplayText.Length > 200 || change.OptionDisplayText.Contains('<', StringComparison.Ordinal) || change.OptionSortOrder is < 0 or > 100_000)
            {
                throw new BusinessException("INVALID_REQUEST", 422, "option key must be snake_case, display text 1..200 chars without markup and sort order 0..100000");
            }

            var option = change.FieldOptionId is { } id ? existing.FirstOrDefault(o => o.FieldOptionId == id) : null;
            if (option is null)
            {
                if (existing.Any(o => o.OptionKey == change.OptionKey))
                {
                    throw new BusinessException("INVALID_REQUEST", 422, $"option key '{change.OptionKey}' already exists (keys are immutable)");
                }

                if (existing.Count >= 500)
                {
                    throw new BusinessException("LIMIT_EXCEEDED", 422, "a dropdown may have at most 500 options");
                }

                option = new FieldOption { FieldOptionId = Guid.CreateVersion7(), FieldDefinitionId = fieldDefinitionId, OptionKey = change.OptionKey!, CreatedOn = now, CreatedByUserId = context.LocalUserId };
                scope.AddFieldOption(option);
                existing.Add(option);
                added.Add(option.OptionKey);
            }
            else if (option.OptionKey != change.OptionKey)
            {
                throw new BusinessException("INVALID_REQUEST", 422, "option keys are immutable; deactivate and add a new option instead");
            }
            else
            {
                var newText = change.OptionDisplayText.Trim();
                if (option.OptionListItemId is not null && !string.Equals(option.OptionDisplayText, newText, StringComparison.Ordinal))
                {
                    throw new BusinessException("OPTION_MANAGED_BY_LIST", 422, $"option '{option.OptionKey}' comes from a shared option list; change its label in the list and sync it into the field");
                }

                if (!string.Equals(option.OptionDisplayText, newText, StringComparison.Ordinal))
                {
                    relabeled.Add(option.OptionKey);
                }

                if (option.IsActive && !change.IsActive)
                {
                    deactivated.Add(option.OptionKey);
                }
                else if (!option.IsActive && change.IsActive)
                {
                    reactivated.Add(option.OptionKey);
                }
            }

            option.OptionDisplayText = change.OptionDisplayText.Trim();
            option.OptionSortOrder = change.OptionSortOrder;
            option.IsActive = change.IsActive;
            option.ModifiedOn = now;
            option.ModifiedByUserId = context.LocalUserId;
        }

        AuditRecorder.Record(scope, context, clock, AuditCategories.Form, AuditActions.FieldOptionsChanged, "dropdown options changed (ids and keys unchanged; nothing deleted; stored selections untouched)", formDefinitionId: formDefinitionId, fieldDefinitionId: fieldDefinitionId, metadata: new { fieldKey = field.FieldKey, changes = changes.Count, added, relabeled, deactivated, reactivated });
        await scope.CommitAsync(cancellationToken).ConfigureAwait(false);
        return ToFieldResponses([field], existing)[0];
    }

    /// <summary>
    /// Pulls the current state of the field's shared option list into the field: new items are added, labels follow the
    /// list, and items deactivated in the list are deactivated here. Keys and ids never change, nothing is deleted or
    /// reactivated, and stored selections are untouched; published versions keep their own option snapshot.
    /// </summary>
    public async Task<FieldDefinitionResponse> SyncOptionsFromListAsync(RequestContext context, Guid formDefinitionId, Guid fieldDefinitionId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        await RequireDesignAsync(context, formDefinitionId, cancellationToken).ConfigureAwait(false);
        var now = clock.GetUtcNow();
        await using var scope = await store.BeginScopeAsync(cancellationToken).ConfigureAwait(false);
        var field = await scope.FindFieldDefinitionAsync(formDefinitionId, fieldDefinitionId, cancellationToken).ConfigureAwait(false) ?? throw new BusinessException("NOT_FOUND", 404, "field not found");
        if (field.OptionListId is not { } listId)
        {
            throw new BusinessException("INVALID_REQUEST", 422, "this field does not use a shared option list");
        }

        var list = await scope.FindOptionListAsync(context.OrganizationId, listId, cancellationToken).ConfigureAwait(false) ?? throw new BusinessException("NOT_FOUND", 404, "option list not found");
        var items = await scope.GetOptionListItemsAsync(list.OptionListId, cancellationToken).ConfigureAwait(false);
        var existing = (await scope.GetFieldOptionsAsync(formDefinitionId, cancellationToken).ConfigureAwait(false)).Where(o => o.FieldDefinitionId == fieldDefinitionId).ToList();
        var added = new List<string>();
        var relabeled = new List<string>();
        var deactivated = new List<string>();
        var nextSort = existing.Count == 0 ? 0 : existing.Max(o => o.OptionSortOrder) + 1;
        foreach (var item in items)
        {
            var option = existing.FirstOrDefault(o => o.OptionListItemId == item.OptionListItemId);
            if (option is null)
            {
                option = new FieldOption { FieldOptionId = Guid.CreateVersion7(), FieldDefinitionId = fieldDefinitionId, OptionKey = item.OptionKey, OptionDisplayText = item.OptionDisplayText, OptionSortOrder = nextSort++, IsActive = item.IsActive, OptionListItemId = item.OptionListItemId, CreatedOn = now, CreatedByUserId = context.LocalUserId, ModifiedOn = now, ModifiedByUserId = context.LocalUserId };
                scope.AddFieldOption(option);
                existing.Add(option);
                added.Add(option.OptionKey);
                continue;
            }

            var touched = false;
            if (!string.Equals(option.OptionDisplayText, item.OptionDisplayText, StringComparison.Ordinal))
            {
                option.OptionDisplayText = item.OptionDisplayText;
                relabeled.Add(option.OptionKey);
                touched = true;
            }

            if (option.IsActive && !item.IsActive)
            {
                option.IsActive = false;
                deactivated.Add(option.OptionKey);
                touched = true;
            }

            if (touched)
            {
                option.ModifiedOn = now;
                option.ModifiedByUserId = context.LocalUserId;
            }
        }

        AuditRecorder.Record(scope, context, clock, AuditCategories.Form, AuditActions.FieldOptionsChanged, "dropdown options synchronized from the shared option list", formDefinitionId: formDefinitionId, fieldDefinitionId: fieldDefinitionId, metadata: new { fieldKey = field.FieldKey, optionListId = listId, optionListRevision = list.OptionListRevision, added, relabeled, deactivated });
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

        // Snapshot: an element without its own label takes the catalog name as it is now, so the immutable version never
        // changes when the catalog name is edited later.
        var catalogFields = (await scope.GetFieldDefinitionsAsync(formDefinitionId, cancellationToken).ConfigureAwait(false)).ToDictionary(f => f.FieldDefinitionId);
        var catalogRelationships = (await scope.GetRelationshipDefinitionsAsync(context.OrganizationId, formDefinitionId, cancellationToken).ConfigureAwait(false)).ToDictionary(r => r.FormRelationshipDefinitionId);
        var catalogOptions = (await scope.GetFieldOptionsAsync(formDefinitionId, cancellationToken).ConfigureAwait(false)).GroupBy(o => o.FieldDefinitionId).ToDictionary(g => g.Key, g => g.ToList());
        layout = SnapshotLabels(layout, catalogFields, catalogRelationships, catalogOptions);
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
        var registered = await RegisterPublishedVersionAsync(context, formDefinitionId, version, layout, catalogFields, canonical, cancellationToken).ConfigureAwait(false);
        return ToVersion(version) with { AuthorityRegistered = registered };
    }

    /// <summary>
    /// Phase C7 M2 (OD-3): records the version just published with the Authority — its number, the SHA-256 of the canonical
    /// layout and, per field, the type, classification and protection the catalog holds now plus the label and option
    /// snapshot this version froze. Search approvals bind to that record only, never to later business rows. A version
    /// whose registration fails stays published but cannot be approved for search (it is never registered later from
    /// business data); the designer publishes again. Returns whether the Authority accepted the registration.
    /// </summary>
    private async Task<bool> RegisterPublishedVersionAsync(RequestContext context, Guid formDefinitionId, FormVersion version, FormLayout layout, Dictionary<Guid, FieldDefinition> catalogFields, string canonicalLayout, CancellationToken cancellationToken)
    {
        var fields = layout.FieldElements
            .Where(e => e.FieldDefinitionId is { } id && catalogFields.ContainsKey(id))
            .GroupBy(e => e.FieldDefinitionId!.Value)
            .Select(g =>
            {
                var element = g.First();
                var field = catalogFields[g.Key];
                return new
                {
                    field_definition_id = field.FieldDefinitionId,
                    field_data_type = field.FieldDataType,
                    field_classification = field.FieldClassification,
                    protection_mode = field.FieldProtectionMode,
                    field_label = element.Label.Length > 300 ? element.Label[..300] : element.Label,
                    options = field.FieldDataType == "Option" ? (element.Options ?? []).Select(o => new { field_option_id = o.FieldOptionId, option_label = o.Label.Length > 300 ? o.Label[..300] : o.Label }).ToList() : null,
                };
            })
            .ToList();
        var payload = JsonSerializer.SerializeToElement(new
        {
            form_definition_id = formDefinitionId,
            form_version_number = version.VersionNumber,
            layout_digest_hex = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(canonicalLayout))),
            fields,
        });
        string? failure = null;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                var decision = await authority.CheckPermissionAsync(context.ToDelegatedUser(), new PermissionCheckRequest(context.OrganizationId, new ResourceReference(ProtectedResourceKind.FormDefinition, formDefinitionId), ProtectedAction.DesignForm), cancellationToken).ConfigureAwait(false);
                var command = await awaiter.SubmitAndAwaitAsync(context.ToDelegatedUser(), new SecurityCommandRequest(context.OrganizationId, SecurityCommandKind.RegisterPublishedFormVersion, new ResourceReference(ProtectedResourceKind.FormDefinition, formDefinitionId), decision.AuthorizationRevision, payload, $"register published version {version.VersionNumber}"), $"form-version:{version.FormVersionId:N}", cancellationToken).ConfigureAwait(false);
                failure = command.CommandStatus == SecurityCommandStatus.Effective ? null : "COMMAND_" + command.CommandStatus.ToString().ToUpperInvariant();
                break;
            }
            catch (AuthorityClientException ex) when (ex.ErrorCode == "STALE_AUTHORIZATION_REVISION" && attempt < 2)
            {
                // another security change committed meanwhile: the same key resubmits at the new revision
            }
            catch (AuthorityClientException ex)
            {
                failure = ex.ErrorCode;
                break;
            }
        }

        await using var scope = await store.BeginScopeAsync(cancellationToken).ConfigureAwait(false);
        AuditRecorder.Record(scope, context, clock, AuditCategories.Form, failure is null ? AuditActions.FormVersionAuthorityRegistered : AuditActions.FormVersionAuthorityRegistrationFailed,
            failure is null ? "published version registered with the Authority" : "published version could not be registered with the Authority", formDefinitionId: formDefinitionId, targetEntityType: "form_version", targetEntityId: version.FormVersionId,
            metadata: new { versionNumber = version.VersionNumber, fields = fields.Count, reason = failure }, outcome: failure is null ? "Success" : "Failure");
        await scope.CommitAsync(cancellationToken).ConfigureAwait(false);
        return failure is null;
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
        var optionsByField = (await scope.GetFieldOptionsAsync(formDefinitionId, cancellationToken).ConfigureAwait(false)).GroupBy(o => o.FieldDefinitionId).ToDictionary(g => g.Key, g => g.ToList());
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
                    FieldDataTypes.Option => field.OptionSelectionMode == OptionSelectionModes.Multiple ? ["multiselect"] : ["select"],
                    _ => ["fileUpload"],
                };
                if (!expectedControls.Contains(element.Control, StringComparer.Ordinal))
                {
                    throw new BusinessException("LAYOUT_INVALID", 422, $"control '{element.Control}' is incompatible with {field.FieldDataType} field '{field.FieldKey}'");
                }

                ValidateRuleCompatibility(element.Validation, field);
                ValidateOptionSettings(element, field, optionsByField.GetValueOrDefault(field.FieldDefinitionId) ?? []);
            }

            if (element.FormRelationshipDefinitionId is { } relId && !relationships.ContainsKey(relId))
            {
                throw new BusinessException("LAYOUT_INVALID", 422, "layout references a relationship that does not belong to this form");
            }
        }

        var groupIds = (await scope.GetGroupDefinitionsAsync(formDefinitionId, cancellationToken).ConfigureAwait(false)).Select(g => g.GroupDefinitionId).ToHashSet();
        FormRules.ValidateLayout(layout, fields, optionsByField, groupIds);
    }

    /// <summary>Default selections and the options snapshot only make sense for a dropdown: defaults must name active options of the field (at most one for a single-select), and a required dropdown needs something to choose.</summary>
    public static void ValidateOptionSettings(LayoutElement element, FieldDefinition field, List<FieldOption> options)
    {
        if (field.FieldDataType != FieldDataTypes.Option)
        {
            if (element.DefaultOptionKeys is { Count: > 0 } || element.Options is not null)
            {
                throw new BusinessException("LAYOUT_INVALID", 422, $"'{field.FieldKey}': default selections and option snapshots apply to dropdown fields only");
            }

            return;
        }

        var defaults = element.DefaultOptionKeys ?? [];
        if (defaults.Count > 1 && field.OptionSelectionMode != OptionSelectionModes.Multiple)
        {
            throw new BusinessException("LAYOUT_INVALID", 422, $"'{field.FieldKey}' is a single-select dropdown; it can have only one default selection");
        }

        foreach (var key in defaults)
        {
            var option = options.FirstOrDefault(o => o.OptionKey == key);
            if (option is null)
            {
                throw new BusinessException("LAYOUT_INVALID", 422, $"'{field.FieldKey}': the default '{key}' is not an option of this field");
            }

            if (!option.IsActive)
            {
                throw new BusinessException("LAYOUT_INVALID", 422, $"'{field.FieldKey}': the default '{key}' is deactivated; choose an active option");
            }
        }

        if (element.Validation.Required && !options.Any(o => o.IsActive))
        {
            throw new BusinessException("LAYOUT_INVALID", 422, $"'{field.FieldKey}' is required but has no active option to choose");
        }
    }

    /// <summary>
    /// Validation rules must fit the field's storage type: a length rule on a number, or a range on text, would be
    /// silently ignored by the server and mislead the designer, so such a layout is refused with an explanation.
    /// Inverted bounds are refused earlier by the layout parser. Rules apply to new saves only; stored values are never rewritten.
    /// </summary>
    internal static void ValidateRuleCompatibility(LayoutValidation rules, FieldDefinition field)
    {
        var isText = field.FieldDataType == FieldDataTypes.String;
        var isNumber = field.FieldDataType is FieldDataTypes.Integer or FieldDataTypes.Decimal;
        var isDate = field.FieldDataType is FieldDataTypes.Date or FieldDataTypes.DateTime;
        if (!isText && (rules.MinLength is not null || rules.MaxLength is not null || rules.Pattern is not null))
        {
            throw new BusinessException("LAYOUT_INVALID", 422, $"length and pattern rules apply only to String fields; '{field.FieldKey}' is {field.FieldDataType}");
        }

        if (!isNumber && (rules.Minimum is not null || rules.Maximum is not null))
        {
            throw new BusinessException("LAYOUT_INVALID", 422, $"minimum and maximum rules apply only to Integer and Decimal fields; '{field.FieldKey}' is {field.FieldDataType}");
        }

        if (!isDate && (rules.MinDate is not null || rules.MaxDate is not null))
        {
            throw new BusinessException("LAYOUT_INVALID", 422, $"earliest and latest date rules apply only to Date and DateTime fields; '{field.FieldKey}' is {field.FieldDataType}");
        }
    }

    public static FormLayout SnapshotLabels(FormLayout layout, IReadOnlyDictionary<Guid, FieldDefinition> fields, IReadOnlyDictionary<Guid, FormRelationshipDefinition> relationships, IReadOnlyDictionary<Guid, List<FieldOption>>? options = null) =>
        layout with
        {
            Sections = layout.Sections.Select(section => section with
            {
                Elements = section.Elements.Select(element =>
                {
                    if (!string.IsNullOrWhiteSpace(element.Label))
                    {
                        return element;
                    }

                    if (element.FieldDefinitionId is { } fieldId && fields.TryGetValue(fieldId, out var field))
                    {
                        return element with { Label = field.FieldDisplayName };
                    }

                    return element.FormRelationshipDefinitionId is { } relId && relationships.TryGetValue(relId, out var relationship) ? element with { Label = relationship.RelationshipDisplayName } : element;
                }).Select(element =>
                {
                    // The published version declares each field's classification/protection as of publish (ADR-PB-004).
                    if (element.FieldDefinitionId is { } fieldId && fields.TryGetValue(fieldId, out var field))
                    {
                        var snapshot = element with { Protection = new LayoutProtection(field.FieldClassification, field.FieldProtectionMode) };
                        // A dropdown's published version carries the options as they were at publish (ids, keys, labels, order,
                        // availability), so the version renders the same way after the catalog changes.
                        return field.FieldDataType == FieldDataTypes.Option
                            ? snapshot with { Options = (options?.GetValueOrDefault(fieldId) ?? []).OrderBy(o => o.OptionSortOrder).ThenBy(o => o.OptionKey, StringComparer.Ordinal).Select(o => new LayoutOption(o.FieldOptionId, o.OptionKey, o.OptionDisplayText.Replace("<", string.Empty, StringComparison.Ordinal).Replace(">", string.Empty, StringComparison.Ordinal), o.OptionSortOrder, o.IsActive)).ToList() }
                            : snapshot;
                    }

                    return element;
                }).ToList(),
            }).ToList(),
        };

    private static FormDefinitionSummaryResponse Summary(FormDefinition d, FormVersion? current, bool hasDraft, bool canDesign, bool canCreate) =>
        new(d.FormDefinitionId, d.FormDefinitionName, d.FormDefinitionDescription, d.LifecycleStatus, current?.VersionNumber, current?.FormVersionId, hasDraft, canDesign, canCreate, d.ModifiedOn);

    private static FormVersionResponse ToVersion(FormVersion v) => new(v.FormVersionId, v.VersionNumber, v.LayoutSchemaVersion, v.LayoutJson, v.ChangeReason, v.RestoredFromFormVersionId, v.PublishedOn, v.PublishedByUserId);

    private static RelationshipDefinitionResponse ToRelationship(FormRelationshipDefinition r, string childName) =>
        new(r.FormRelationshipDefinitionId, r.ParentFormDefinitionId, r.ChildFormDefinitionId, childName, r.RelationshipDefinitionKey, r.RelationshipDisplayName, r.MinimumChildCount, r.MaximumChildCount, r.LifecycleStatus);

    public static List<FieldDefinitionResponse> ToFieldResponses(IReadOnlyList<FieldDefinition> fields, IReadOnlyList<FieldOption> options)
    {
        ArgumentNullException.ThrowIfNull(fields);
        ArgumentNullException.ThrowIfNull(options);
        var byField = options.GroupBy(o => o.FieldDefinitionId).ToDictionary(g => g.Key, g => g.OrderBy(o => o.OptionSortOrder).ThenBy(o => o.OptionKey, StringComparer.Ordinal).Select(o => new FieldOptionResponse(o.FieldOptionId, o.OptionKey, o.OptionDisplayText, o.OptionSortOrder, o.IsActive, o.OptionListItemId)).ToList());
        return fields.OrderBy(f => f.FieldKey, StringComparer.Ordinal).Select(f => new FieldDefinitionResponse(f.FieldDefinitionId, f.FieldKey, f.FieldDisplayName, f.FieldDescription, f.FieldDataType, f.FieldClassification, f.LifecycleStatus, byField.GetValueOrDefault(f.FieldDefinitionId, []), f.FieldDefinitionRevision, f.FieldProtectionMode, f.ProtectionPolicyRevision, f.OptionSelectionMode, f.OptionListId, f.GroupDefinitionId)).ToList();
    }
}
