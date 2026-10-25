using System.Text.Json;
using EnterpriseForms.Application.Common;
using EnterpriseForms.Application.Common.Authority;
using EnterpriseForms.Application.Common.Persistence;
using EnterpriseForms.Application.Features.Audit;
using EnterpriseForms.Application.Features.FormDefinitions.CreateFormDefinition;
using EnterpriseForms.Contracts.Features;
using EnterpriseForms.Domain.Features.Audit;

namespace EnterpriseForms.Application.Features.Organizations;

/// <summary>
/// Security Administrators of the organization (owner message 7). "Security Administrator" is the Authority's organization
/// relation <c>system_administrator</c> (permission <c>administer_security</c>). Every decision is the Authority's: the
/// roster comes from its desired state, appointments and removals are GrantSystemRole / RevokeSystemRole security commands
/// that only an existing Security Administrator may submit, with step-up. Organization owners see the roster but cannot
/// change it (no self-elevation through ownership). Display names come from the local directory (labels only).
/// </summary>
public sealed class SecurityAdministratorsHandlers(IBusinessStore store, IAuthorizationAuthorityClient authority, ISecurityAdministratorAuthority roster, AuthorityCommandAwaiter awaiter, TimeProvider clock)
{
    public const string SyntheticStepUpMarker = "synthetic-development-step-up";

    public async Task<SecurityAdministratorsResponse> GetAsync(RequestContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        AuthoritySecurityAdministratorRoster result;
        try
        {
            result = await roster.GetSecurityAdministratorsAsync(context.ToDelegatedUser(), new SecurityAdministratorRosterQuery(context.OrganizationId), cancellationToken).ConfigureAwait(false);
        }
        catch (AuthorityClientException ex) when (ex.HttpStatusCode == 404)
        {
            throw new BusinessException("FORBIDDEN", 403, "Security Administrators are visible to the organization's owners and Security Administrators");
        }

        await using var scope = await store.BeginScopeAsync(cancellationToken).ConfigureAwait(false);
        var names = (await scope.GetUsersAsync(context.OrganizationId, cancellationToken).ConfigureAwait(false)).ToDictionary(u => u.UserId, u => u.DisplayName);
        string Name(Guid id) => names.GetValueOrDefault(id) ?? id.ToString("D");
        return new SecurityAdministratorsResponse(result.CanManage, result.AppointmentOperational, result.AppointmentUnavailableReason,
            result.Administrators.Select(a => new SecurityAdministratorResponse(a.AuthorityUserId, Name(a.AuthorityUserId), a.IssuerUri, a.SubjectIdentifier, a.AppointedOn, a.AuthorityUserId == context.LocalUserId)).ToList(),
            (result.Candidates ?? []).Select(c => new SecurityAdministratorCandidateResponse(c.AuthorityUserId, Name(c.AuthorityUserId), c.IssuerUri, c.SubjectIdentifier)).OrderBy(c => c.DisplayName, StringComparer.OrdinalIgnoreCase).ToList());
    }

    /// <summary>Returns once the Authority reports the command Effective. No roster is returned: a caller who removed themself may no longer see it.</summary>
    public async Task AppointAsync(RequestContext context, AppointSecurityAdministratorRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(request);
        await SubmitAsync(context, SecurityCommandKind.GrantSystemRole, request.UserId, request.Reason, request.UseDevelopmentStepUp, $"security-admin-appoint:{request.UserId:N}:{Guid.CreateVersion7():N}", cancellationToken).ConfigureAwait(false);
        await AuditAsync(context, AuditActions.SecurityAdministratorAppointed, "Security Administrator appointed", request.UserId, request.Reason, cancellationToken).ConfigureAwait(false);
    }

    public async Task RemoveAsync(RequestContext context, Guid userId, RemoveSecurityAdministratorRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(request);
        await SubmitAsync(context, SecurityCommandKind.RevokeSystemRole, userId, request.Reason, request.UseDevelopmentStepUp, $"security-admin-remove:{userId:N}:{Guid.CreateVersion7():N}", cancellationToken).ConfigureAwait(false);
        await AuditAsync(context, AuditActions.SecurityAdministratorRemoved, "Security Administrator removed", userId, request.Reason, cancellationToken).ConfigureAwait(false);
    }

    private async Task SubmitAsync(RequestContext context, SecurityCommandKind kind, Guid userId, string? reason, bool stepUp, string idempotencyKey, CancellationToken cancellationToken)
    {
        if (userId == Guid.Empty || string.IsNullOrWhiteSpace(reason) || reason.Trim().Length < 3 || reason.Length > 500)
        {
            throw new BusinessException("INVALID_REQUEST", 422, "choose a person and give a reason (3 to 500 characters)");
        }

        // No business pre-check: every attempt, including an owner's or another member's, goes to the Authority, which alone
        // decides (only an existing Security Administrator, with step-up) and records each refusal on its own audit stream.
        var decision = await authority.CheckPermissionAsync(context.ToDelegatedUser(), new PermissionCheckRequest(context.OrganizationId, new ResourceReference(ProtectedResourceKind.Organization, context.OrganizationId), ProtectedAction.AdministerSecurity), cancellationToken).ConfigureAwait(false);
        var payload = JsonSerializer.SerializeToElement(new { authority_user_id = userId, role_name = "system_administrator", reason = reason.Trim(), step_up_evidence = stepUp ? SyntheticStepUpMarker : null });
        var command = await awaiter.SubmitAndAwaitAsync(context.ToDelegatedUser(), new SecurityCommandRequest(context.OrganizationId, kind, new ResourceReference(ProtectedResourceKind.Organization, context.OrganizationId), decision.AuthorizationRevision, payload, reason.Trim()), idempotencyKey, cancellationToken).ConfigureAwait(false);
        if (command.CommandStatus != SecurityCommandStatus.Effective)
        {
            throw new PendingCommandException(new CommandOutcome(command.CommandId, command.CommandStatus.ToString(), command.AuthorizationRevision, false));
        }
    }

    private async Task AuditAsync(RequestContext context, string action, string description, Guid userId, string? reason, CancellationToken cancellationToken)
    {
        await using var scope = await store.BeginScopeAsync(cancellationToken).ConfigureAwait(false);
        AuditRecorder.Record(scope, context, clock, AuditCategories.Security, action, description, targetEntityType: "user", targetEntityId: userId, metadata: new { reason });
        await scope.CommitAsync(cancellationToken).ConfigureAwait(false);
    }
}
