using System.Text.Json;
using EnterpriseForms.Application.Common;
using EnterpriseForms.Application.Common.Authority;
using EnterpriseForms.Application.Common.Persistence;
using EnterpriseForms.Application.Features.Audit;
using EnterpriseForms.Application.Features.FormDefinitions.CreateFormDefinition;
using EnterpriseForms.Contracts.Features;
using EnterpriseForms.Domain.Features.Audit;

namespace EnterpriseForms.Application.Features.Search.FieldSearchPolicy;

/// <summary>
/// Phase C7 M2 — AC-6 search approval as seen from the business API. Every decision is the Authorization Authority's:
/// this handler only relays the request as a security command (approve, withdraw, grant query access) and shows the
/// Authority's configuration view. It never reads searchability from business rows, and it has no approve-anything path:
/// without the Authority's step-up gate every approval is refused there (OD-7).
/// </summary>
public sealed class FieldSearchPolicyHandlers(IBusinessStore store, IAuthorizationAuthorityClient authority, IFieldSearchPolicyAuthority policy, AuthorityCommandAwaiter awaiter, TimeProvider clock)
{
    public const string SyntheticStepUpMarker = "synthetic-development-step-up";

    /// <summary>Configuration of a form's fields (designers of the form and security administrators; others get 404 like an unknown form).</summary>
    public async Task<FieldSearchConfigurationResponse> GetConfigurationAsync(RequestContext context, Guid formDefinitionId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var capabilities = await policy.GetFieldSearchCapabilitiesAsync(context.ToDelegatedUser(), new FieldSearchCapabilitiesQuery(context.OrganizationId, formDefinitionId), cancellationToken).ConfigureAwait(false);
        var configuration = capabilities.Configuration ?? throw new BusinessException("NOT_FOUND", 404, "form definition not found");
        List<FormAccessSubjectResponse>? searchers = null;
        if (configuration.FieldSearchers is { } subjects)
        {
            // Display names are the local directory projection; who holds the permission is the Authority's answer.
            await using var scope = await store.BeginScopeAsync(cancellationToken).ConfigureAwait(false);
            var users = (await scope.GetUsersAsync(context.OrganizationId, cancellationToken).ConfigureAwait(false)).ToDictionary(u => u.UserId, u => u.DisplayName);
            var teams = (await scope.GetTeamsAsync(context.OrganizationId, cancellationToken).ConfigureAwait(false)).ToDictionary(t => t.TeamId, t => t.TeamName);
            searchers = subjects.Select(s => new FormAccessSubjectResponse(s.SubjectKind, s.SubjectId,
                (s.SubjectKind == "Team" ? teams.GetValueOrDefault(s.SubjectId) : users.GetValueOrDefault(s.SubjectId)) ?? s.SubjectId.ToString("D"))).ToList();
        }

        return new FieldSearchConfigurationResponse(formDefinitionId, capabilities.PlaintextSearchPermitted, capabilities.SearchPolicyRevision, configuration.CanApprove, configuration.ApprovalOperational,
            configuration.ApprovalUnavailableReason, configuration.RegisteredFormVersionNumber,
            configuration.Fields.Select(f => new FieldSearchStatusResponse(f.FieldDefinitionId, f.FieldLabel, f.FieldDataType, f.FieldClassification, f.ProtectionMode, f.Eligibility, f.ApprovalState, f.InvalidationReason, f.ApprovedOperators, f.PermittedOperators)).ToList(),
            searchers);
    }

    /// <summary>
    /// Phase C7 M3: search-approval administration for security administrators. The form list, titles and counts are the
    /// Authority's (registered published versions); no Designer permission and no record access are needed or granted.
    /// </summary>
    public async Task<FieldSearchAdministrationResponse> GetAdministrationAsync(RequestContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        AuthorityFieldSearchAdministration administration;
        try
        {
            administration = await policy.GetFieldSearchAdministrationAsync(context.ToDelegatedUser(), new FieldSearchAdministrationQuery(context.OrganizationId), cancellationToken).ConfigureAwait(false);
        }
        catch (AuthorityClientException ex) when (ex.HttpStatusCode is 403 or 404)
        {
            throw new BusinessException("FORBIDDEN", 403, "search approvals are administered by security administrators");
        }

        return new FieldSearchAdministrationResponse(administration.PlaintextSearchPermitted, administration.ApprovalOperational, administration.ApprovalUnavailableReason,
            administration.Forms.Select(f => new FieldSearchAdministrationFormResponse(f.FormDefinitionId, f.FormTitle ?? "Untitled form (publish again to register its title)", f.FormTitle is not null,
                f.RegisteredFormVersionNumber, f.RegisteredOn, f.FieldCount, f.EligibleFieldCount, f.ActiveApprovalCount, f.InvalidatedApprovalCount)).ToList());
    }

    public async Task<FieldSearchConfigurationResponse> ApproveAsync(RequestContext context, Guid formDefinitionId, Guid fieldDefinitionId, ApproveFieldSearchRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(request);
        var payload = JsonSerializer.SerializeToElement(new
        {
            form_definition_id = formDefinitionId,
            field_definition_id = fieldDefinitionId,
            operators = request.Operators ?? [],
            approval_reference = request.ApprovalReference ?? string.Empty,
            step_up_evidence = request.UseDevelopmentStepUp ? SyntheticStepUpMarker : null,
        });
        await SubmitAsync(context, SecurityCommandKind.ApproveFieldSearch, formDefinitionId, payload, "approve field search", $"field-search-approve:{fieldDefinitionId:N}:{Guid.CreateVersion7():N}", cancellationToken).ConfigureAwait(false);
        await AuditAsync(context, AuditActions.FieldSearchApproved, "plaintext value search approved for a field", formDefinitionId, fieldDefinitionId, new { operators = request.Operators, approvalReference = request.ApprovalReference }, cancellationToken).ConfigureAwait(false);
        return await GetConfigurationAsync(context, formDefinitionId, cancellationToken).ConfigureAwait(false);
    }

    public async Task<FieldSearchConfigurationResponse> WithdrawAsync(RequestContext context, Guid formDefinitionId, Guid fieldDefinitionId, WithdrawFieldSearchRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(request);
        var payload = JsonSerializer.SerializeToElement(new { form_definition_id = formDefinitionId, field_definition_id = fieldDefinitionId, withdrawal_reason = request.Reason ?? string.Empty });
        await SubmitAsync(context, SecurityCommandKind.WithdrawFieldSearch, formDefinitionId, payload, "withdraw field search", $"field-search-withdraw:{fieldDefinitionId:N}:{Guid.CreateVersion7():N}", cancellationToken).ConfigureAwait(false);
        await AuditAsync(context, AuditActions.FieldSearchWithdrawn, "plaintext value search approval withdrawn for a field", formDefinitionId, fieldDefinitionId, new { reason = request.Reason }, cancellationToken).ConfigureAwait(false);
        return await GetConfigurationAsync(context, formDefinitionId, cancellationToken).ConfigureAwait(false);
    }

    public async Task UpdateSearchersAsync(RequestContext context, Guid formDefinitionId, UpdateFieldSearchersRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(request);
        var payload = JsonSerializer.SerializeToElement(new
        {
            form_definition_id = formDefinitionId,
            grant_user_ids = request.GrantUserIds ?? [],
            grant_team_ids = request.GrantTeamIds ?? [],
            revoke_user_ids = request.RevokeUserIds ?? [],
            revoke_team_ids = request.RevokeTeamIds ?? [],
        });
        await SubmitAsync(context, SecurityCommandKind.UpdateFieldSearchers, formDefinitionId, payload, "update field search access", $"field-searchers:{formDefinitionId:N}:{Guid.CreateVersion7():N}", cancellationToken).ConfigureAwait(false);
        await AuditAsync(context, AuditActions.FieldSearchersChanged, "field search access of a form changed", formDefinitionId, null, new { grantUsers = request.GrantUserIds?.Count ?? 0, grantTeams = request.GrantTeamIds?.Count ?? 0, revokeUsers = request.RevokeUserIds?.Count ?? 0, revokeTeams = request.RevokeTeamIds?.Count ?? 0 }, cancellationToken).ConfigureAwait(false);
    }

    private async Task SubmitAsync(RequestContext context, SecurityCommandKind kind, Guid formDefinitionId, JsonElement payload, string reason, string idempotencyKey, CancellationToken cancellationToken)
    {
        var decision = await authority.CheckPermissionAsync(context.ToDelegatedUser(), new PermissionCheckRequest(context.OrganizationId, new ResourceReference(ProtectedResourceKind.Organization, context.OrganizationId), ProtectedAction.AdministerSecurity), cancellationToken).ConfigureAwait(false);
        // The Authority decides (OD-2: administer_security only); the business check only spares a pointless command.
        if (!decision.Allowed)
        {
            throw new BusinessException("FORBIDDEN", 403, "field search approval and access are managed by security administrators");
        }

        var command = await awaiter.SubmitAndAwaitAsync(context.ToDelegatedUser(), new SecurityCommandRequest(context.OrganizationId, kind, new ResourceReference(ProtectedResourceKind.FormDefinition, formDefinitionId), decision.AuthorizationRevision, payload, reason), idempotencyKey, cancellationToken).ConfigureAwait(false);
        if (command.CommandStatus != SecurityCommandStatus.Effective)
        {
            throw new PendingCommandException(new CommandOutcome(command.CommandId, command.CommandStatus.ToString(), command.AuthorizationRevision, false));
        }
    }

    private async Task AuditAsync(RequestContext context, string action, string description, Guid formDefinitionId, Guid? fieldDefinitionId, object metadata, CancellationToken cancellationToken)
    {
        await using var scope = await store.BeginScopeAsync(cancellationToken).ConfigureAwait(false);
        AuditRecorder.Record(scope, context, clock, AuditCategories.Security, action, description, formDefinitionId: formDefinitionId, fieldDefinitionId: fieldDefinitionId, metadata: metadata);
        await scope.CommitAsync(cancellationToken).ConfigureAwait(false);
    }
}
