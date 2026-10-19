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
        var policy = await Organizations.SecurityPolicyHandlers.EnsureAsync(scope, context.OrganizationId, cancellationToken).ConfigureAwait(false);
        var (classification, protectionMode) = FieldProtectionPolicy.Resolve(request.FieldDataType!, request.FieldClassification, request.FieldProtectionMode, policy);
        if (protectionMode == FieldProtectionModes.ApplicationEncrypted && request.FieldDataType != FieldDataTypes.File && !encryption.IsAvailable)
        {
            throw new Documents.Ports.EncryptionUnavailableException("no permitted encryption key provider is configured; an encrypted field cannot be created");
        }

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
            FieldClassification = classification,
            FieldProtectionMode = protectionMode,
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

        AuditRecorder.Record(scope, context, clock, AuditCategories.Form, AuditActions.FormDraftUpdated, "field definition added", formDefinitionId: formDefinitionId, fieldDefinitionId: field.FieldDefinitionId, metadata: new { fieldKey = field.FieldKey, dataType = field.FieldDataType, classification = field.FieldClassification, protectionMode = field.FieldProtectionMode });
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
    /// plaintext to application encryption. Never the reverse. Future writes are encrypted immediately; existing plaintext
    /// copies are rewritten as ciphertext when each record is next committed by an authorized user (reads flag them as
    /// legacy plaintext until then) — no background process holds a user's signing authority.
    /// </summary>
    public async Task<FieldProtectionResponse> StrengthenProtectionAsync(RequestContext context, Guid formDefinitionId, Guid fieldDefinitionId, StrengthenFieldProtectionRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(request);
        await RequireDesignAsync(context, formDefinitionId, cancellationToken).ConfigureAwait(false);
        var reason = request.ChangeReason?.Trim() ?? string.Empty;
        if (reason.Length is < 3 or > 500)
        {
            throw new BusinessException("INVALID_REQUEST", 422, "a change reason of 3..500 characters is required");
        }

        await using var scope = await store.BeginScopeAsync(cancellationToken).ConfigureAwait(false);
        var field = await scope.FindFieldDefinitionAsync(formDefinitionId, fieldDefinitionId, cancellationToken).ConfigureAwait(false)
                    ?? throw new BusinessException("NOT_FOUND", 404, "field not found");
        if (request.ExpectedProtectionRevision != field.ProtectionPolicyRevision)
        {
            throw new BusinessException("FIELD_REVISION_CONFLICT", 409, "the field's protection was changed by someone else; reload and retry");
        }

        var policy = await Organizations.SecurityPolicyHandlers.EnsureAsync(scope, context.OrganizationId, cancellationToken).ConfigureAwait(false);
        var (classification, mode) = FieldProtectionPolicy.ResolveStrengthening(field, request.TargetClassification, request.TargetProtectionMode, policy);
        if (mode == FieldProtectionModes.ApplicationEncrypted && field.FieldDataType != FieldDataTypes.File && !encryption.IsAvailable)
        {
            throw new Documents.Ports.EncryptionUnavailableException("no permitted encryption key provider is configured; the field cannot be switched to encryption");
        }

        var previous = new { field.FieldClassification, field.FieldProtectionMode, field.ProtectionPolicyRevision };
        var now = clock.GetUtcNow();
        field.FieldClassification = classification;
        field.FieldProtectionMode = mode;
        field.ProtectionPolicyRevision++;
        field.ProtectionChangedOn = now;
        field.ProtectionChangedByUserId = context.LocalUserId;
        AuditRecorder.Record(scope, context, clock, AuditCategories.Security, AuditActions.FieldProtectionStrengthened, "field classification/protection strengthened (audited migration; existing plaintext rewritten as ciphertext on each record's next commit)", formDefinitionId: formDefinitionId, fieldDefinitionId: fieldDefinitionId, metadata: new { fieldKey = field.FieldKey, previous, classification, protectionMode = mode, protectionPolicyRevision = field.ProtectionPolicyRevision, changeReason = reason });
        await scope.CommitAsync(cancellationToken).ConfigureAwait(false);
        return await ProtectionResponseAsync(scope, field, policy, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<FieldProtectionResponse> ProtectionResponseAsync(IBusinessScope scope, FieldDefinition field, Domain.Features.Organizations.OrganizationSecurityPolicy policy, CancellationToken cancellationToken)
    {
        var encrypted = field.FieldProtectionMode == FieldProtectionModes.ApplicationEncrypted;
        var legacy = encrypted && field.FieldDataType != FieldDataTypes.File ? await scope.CountLegacyPlaintextRecordsAsync(field.FieldDefinitionId, cancellationToken).ConfigureAwait(false) : 0;
        var state = !encrypted ? "Plaintext" : legacy == 0 ? "Encrypted" : "EncryptionPendingRewrite";
        return new FieldProtectionResponse(field.FieldDefinitionId, field.FieldKey, field.FieldClassification, field.FieldProtectionMode, field.ProtectionPolicyRevision, field.ProtectionChangedOn, state, legacy,
            encrypted ? "Encrypted for privacy — not searchable in V1 (ADR-PB-004). Authorized users see the value on the record; no plaintext copy exists in the business database." : "Plaintext: searchable within authorization, and readable by a database administrator.",
            policy.MinimumFieldClassification, policy.PlaintextInternalFieldsPermitted);
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

        // Snapshot: an element without its own label takes the catalog name as it is now, so the immutable version never
        // changes when the catalog name is edited later.
        var catalogFields = (await scope.GetFieldDefinitionsAsync(formDefinitionId, cancellationToken).ConfigureAwait(false)).ToDictionary(f => f.FieldDefinitionId);
        var catalogRelationships = (await scope.GetRelationshipDefinitionsAsync(context.OrganizationId, formDefinitionId, cancellationToken).ConfigureAwait(false)).ToDictionary(r => r.FormRelationshipDefinitionId);
        layout = SnapshotLabels(layout, catalogFields, catalogRelationships);
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

                ValidateRuleCompatibility(element.Validation, field);
            }

            if (element.FormRelationshipDefinitionId is { } relId && !relationships.ContainsKey(relId))
            {
                throw new BusinessException("LAYOUT_INVALID", 422, "layout references a relationship that does not belong to this form");
            }
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

    internal static FormLayout SnapshotLabels(FormLayout layout, IReadOnlyDictionary<Guid, FieldDefinition> fields, IReadOnlyDictionary<Guid, FormRelationshipDefinition> relationships) =>
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
                    return element.FieldDefinitionId is { } fieldId && fields.TryGetValue(fieldId, out var field)
                        ? element with { Protection = new LayoutProtection(field.FieldClassification, field.FieldProtectionMode) }
                        : element;
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
        var byField = options.GroupBy(o => o.FieldDefinitionId).ToDictionary(g => g.Key, g => g.OrderBy(o => o.OptionSortOrder).ThenBy(o => o.OptionKey, StringComparer.Ordinal).Select(o => new FieldOptionResponse(o.FieldOptionId, o.OptionKey, o.OptionDisplayText, o.OptionSortOrder, o.IsActive)).ToList());
        return fields.OrderBy(f => f.FieldKey, StringComparer.Ordinal).Select(f => new FieldDefinitionResponse(f.FieldDefinitionId, f.FieldKey, f.FieldDisplayName, f.FieldDescription, f.FieldDataType, f.FieldClassification, f.LifecycleStatus, byField.GetValueOrDefault(f.FieldDefinitionId, []), f.FieldDefinitionRevision, f.FieldProtectionMode, f.ProtectionPolicyRevision)).ToList();
    }
}
