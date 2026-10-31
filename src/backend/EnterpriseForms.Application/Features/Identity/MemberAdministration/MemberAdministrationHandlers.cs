using System.Text.Json;
using EnterpriseForms.Application.Common;
using EnterpriseForms.Application.Common.Authority;
using EnterpriseForms.Application.Common.Persistence;
using EnterpriseForms.Application.Features.Audit;
using EnterpriseForms.Application.Features.FormDefinitions.CreateFormDefinition;
using EnterpriseForms.Contracts.Features;
using EnterpriseForms.Domain.Features.Audit;

namespace EnterpriseForms.Application.Features.Identity.MemberAdministration;

/// <summary>
/// C8 M2 (AC-7): membership and organization-role commands from the People page. Every change is an Authority security
/// command that the Authority alone decides (owners only, no self-change, step-up, two distinct owners for owner changes,
/// last-holder and record-ownership protections, 24-hour proposals, 7-day membership requests). There is no business
/// pre-check that could grant anything: refused attempts are recorded on the Authority's audit stream. A command returns
/// once it is Effective; a still-pending command is reported as pending, never as done. The business audit timeline records
/// each effective change (target, role or change, reason; never an email address).
/// </summary>
public sealed class MemberAdministrationHandlers(IBusinessStore store, IAuthorizationAuthorityClient authority, IOrganizationRosterAuthority roster, AuthorityCommandAwaiter awaiter, TimeProvider clock)
{
    public const string SyntheticStepUpMarker = "synthetic-development-step-up";

    public static readonly IReadOnlyList<string> ProposalChanges = ["GrantOwner", "RevokeOwner", "DisableOwner", "EnableOwner"];

    public async Task<MemberCommandResponse> GrantDesignerAsync(RequestContext context, Guid userId, MemberRoleChangeRequest request, string idempotencyKey, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        RequireDesignerRole(request.RoleName);
        var outcome = await SubmitAsync(context, SecurityCommandKind.GrantOrganizationRole, new { authority_user_id = userId, role_name = "designer", reason = Reason(request.Reason), proposal_id = (Guid?)null, step_up_evidence = StepUp(request.UseDevelopmentStepUp) },
            request.Reason, idempotencyKey, cancellationToken).ConfigureAwait(false);
        await AuditAsync(context, AuditActions.OrganizationRoleGranted, "Designer role granted", "user", userId, outcome.CommandId, new { roleName = "Designer", reason = request.Reason.Trim(), commandId = outcome.CommandId }, cancellationToken).ConfigureAwait(false);
        return outcome;
    }

    public async Task<MemberCommandResponse> RevokeDesignerAsync(RequestContext context, Guid userId, string roleName, MemberCommandRequest request, string idempotencyKey, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        RequireDesignerRole(roleName);
        var outcome = await SubmitAsync(context, SecurityCommandKind.RevokeOrganizationRole, new { authority_user_id = userId, role_name = "designer", reason = Reason(request.Reason), proposal_id = (Guid?)null, step_up_evidence = StepUp(request.UseDevelopmentStepUp) },
            request.Reason, idempotencyKey, cancellationToken).ConfigureAwait(false);
        await AuditAsync(context, AuditActions.OrganizationRoleRevoked, "Designer role revoked", "user", userId, outcome.CommandId, new { roleName = "Designer", reason = request.Reason.Trim(), commandId = outcome.CommandId }, cancellationToken).ConfigureAwait(false);
        return outcome;
    }

    /// <summary>First half of a two-owner change; grants nothing. The proposal id is the command id; it expires after 24 hours.</summary>
    public async Task<MemberCommandResponse> ProposeAsync(RequestContext context, Guid userId, OwnerProposalRequest request, string idempotencyKey, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!ProposalChanges.Contains(request.Change ?? string.Empty))
        {
            throw new BusinessException("INVALID_REQUEST", 422, "change must be GrantOwner, RevokeOwner, DisableOwner or EnableOwner");
        }

        var outcome = await SubmitAsync(context, SecurityCommandKind.ProposeOrganizationOwnerChange, new { authority_user_id = userId, proposed_change = request.Change, reason = Reason(request.Reason), step_up_evidence = StepUp(request.UseDevelopmentStepUp) },
            request.Reason, idempotencyKey, cancellationToken).ConfigureAwait(false);
        await AuditAsync(context, AuditActions.OrganizationOwnerChangeProposed, "owner change proposed (needs a second owner)", "user", userId, outcome.CommandId, new { change = request.Change, reason = request.Reason.Trim(), proposalId = outcome.CommandId, commandId = outcome.CommandId }, cancellationToken).ConfigureAwait(false);
        return outcome with { ProposalId = outcome.CommandId, ExpiresOn = clock.GetUtcNow().AddHours(24) };
    }

    /// <summary>Second half: another owner executes the proposed change. The proposal is read from the Authority; the Authority re-checks everything.</summary>
    public async Task<MemberCommandResponse> ApproveProposalAsync(RequestContext context, Guid proposalId, MemberCommandRequest request, string idempotencyKey, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(request);
        var current = await RosterAsync(context, cancellationToken).ConfigureAwait(false);
        var proposal = current.OwnerProposals.FirstOrDefault(p => p.ProposalId == proposalId) ?? throw new BusinessException("NOT_FOUND", 404, "no such proposal");
        var reason = Reason(request.Reason);
        var stepUp = StepUp(request.UseDevelopmentStepUp);
        var target = proposal.TargetAuthorityUserId;
        var (kind, payload, action, description) = proposal.ProposedChange switch
        {
            "GrantOwner" => (SecurityCommandKind.GrantOrganizationRole, (object)new { authority_user_id = target, role_name = "organization_owner", reason, proposal_id = proposalId, step_up_evidence = stepUp }, AuditActions.OrganizationRoleGranted, "Organization Owner role granted (second owner)"),
            "RevokeOwner" => (SecurityCommandKind.RevokeOrganizationRole, new { authority_user_id = target, role_name = "organization_owner", reason, proposal_id = proposalId, step_up_evidence = stepUp }, AuditActions.OrganizationRoleRevoked, "Organization Owner role revoked (second owner)"),
            "DisableOwner" => (SecurityCommandKind.DisableIdentityBinding, new { authority_user_id = target, reason, proposal_id = proposalId, step_up_evidence = stepUp }, AuditActions.MembershipDisabled, "owner membership disabled (second owner)"),
            "EnableOwner" => (SecurityCommandKind.EnableIdentityBinding, new { authority_user_id = target, reason, proposal_id = proposalId, step_up_evidence = stepUp }, AuditActions.MembershipEnabled, "owner membership re-enabled (second owner)"),
            _ => throw new BusinessException("INVALID_REQUEST", 422, "unknown proposal kind"),
        };
        var outcome = await SubmitAsync(context, kind, payload, request.Reason, idempotencyKey, cancellationToken).ConfigureAwait(false);
        await AuditAsync(context, action, description, "user", target, outcome.CommandId, new { change = proposal.ProposedChange, proposalId, proposedBy = proposal.ProposedByAuthorityUserId, reason, commandId = outcome.CommandId }, cancellationToken).ConfigureAwait(false);
        return outcome with { ProposalId = proposalId };
    }

    public async Task<MemberCommandResponse> SetMembershipAsync(RequestContext context, Guid userId, bool enable, MemberCommandRequest request, string idempotencyKey, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var outcome = await SubmitAsync(context, enable ? SecurityCommandKind.EnableIdentityBinding : SecurityCommandKind.DisableIdentityBinding,
            new { authority_user_id = userId, reason = Reason(request.Reason), proposal_id = (Guid?)null, step_up_evidence = StepUp(request.UseDevelopmentStepUp) }, request.Reason, idempotencyKey, cancellationToken).ConfigureAwait(false);
        await AuditAsync(context, enable ? AuditActions.MembershipEnabled : AuditActions.MembershipDisabled, enable ? "membership re-enabled" : "membership disabled (sessions revoked; roles and shares kept)", "user", userId, outcome.CommandId,
            new { reason = request.Reason.Trim(), commandId = outcome.CommandId }, cancellationToken).ConfigureAwait(false);
        return outcome;
    }

    /// <summary>Records a request only. The identity provider is the organization's configured tenant; two operator approvals create the membership.</summary>
    public async Task<MemberCommandResponse> RequestMembershipAsync(RequestContext context, CreateMembershipRequestRequest request, string idempotencyKey, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(request);
        if (request.InitialRoleName is not (null or "Designer"))
        {
            throw new BusinessException("INVALID_REQUEST", 422, "a new member may start with no role or the Designer role");
        }

        var current = await RosterAsync(context, cancellationToken).ConfigureAwait(false);
        var issuer = current.OrganizationIdentityIssuer ?? throw new BusinessException("IDENTITY_NOT_ALLOWED", 422, "the organization has no single configured identity provider tenant; members cannot be added");
        var outcome = await SubmitAsync(context, SecurityCommandKind.RequestOrganizationMembership,
            new { issuer_uri = issuer, subject_identifier = request.SubjectIdentifier?.Trim() ?? string.Empty, display_name_hint = request.DisplayNameHint?.Trim() ?? string.Empty, initial_role_name = request.InitialRoleName is null ? null : "designer", reason = Reason(request.Reason), step_up_evidence = StepUp(request.UseDevelopmentStepUp) },
            request.Reason, idempotencyKey, cancellationToken).ConfigureAwait(false);
        await AuditAsync(context, AuditActions.MembershipRequested, "membership requested (awaiting two operator approvals)", "membership_request", outcome.CommandId, outcome.CommandId,
            new { requestId = outcome.CommandId, subjectIdentifier = request.SubjectIdentifier?.Trim(), initialRole = request.InitialRoleName, reason = request.Reason.Trim(), commandId = outcome.CommandId }, cancellationToken).ConfigureAwait(false);
        return outcome with { RequestId = outcome.CommandId, ExpiresOn = clock.GetUtcNow().AddDays(7) };
    }

    /// <summary>AC-7.5: cancels a Pending request. The Authority decides (owners only, step-up, unchanged digest, still pending); a cancelled request can never be approved.</summary>
    public async Task<MemberCommandResponse> CancelRequestAsync(RequestContext context, Guid requestId, CancelMembershipRequestRequest request, string idempotencyKey, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var outcome = await SubmitAsync(context, SecurityCommandKind.CancelOrganizationMembershipRequest,
            new { membership_request_id = requestId, expected_request_digest = request.ExpectedRequestDigest?.Trim() ?? string.Empty, reason = Reason(request.Reason), step_up_evidence = StepUp(request.UseDevelopmentStepUp) },
            request.Reason, idempotencyKey, cancellationToken).ConfigureAwait(false);
        await AuditAsync(context, AuditActions.MembershipRequestCancelled, "membership request cancelled", "membership_request", requestId, outcome.CommandId,
            new { requestId, reason = request.Reason.Trim(), commandId = outcome.CommandId }, cancellationToken).ConfigureAwait(false);
        return outcome;
    }

    /// <summary>AC-7.5: edits a Pending request by replacement (new id, fresh expiry, lineage, no approval carried over). Identity is never sent: the Authority copies it from the original.</summary>
    public async Task<MemberCommandResponse> ReplaceRequestAsync(RequestContext context, Guid requestId, ReplaceMembershipRequestRequest request, string idempotencyKey, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.InitialRoleName is not (null or "Designer"))
        {
            throw new BusinessException("INVALID_REQUEST", 422, "a new member may start with no role or the Designer role");
        }

        var outcome = await SubmitAsync(context, SecurityCommandKind.ReplaceOrganizationMembershipRequest,
            new { membership_request_id = requestId, expected_request_digest = request.ExpectedRequestDigest?.Trim() ?? string.Empty, display_name_hint = request.DisplayNameHint?.Trim() ?? string.Empty, initial_role_name = request.InitialRoleName is null ? null : "designer", reason = Reason(request.Reason), step_up_evidence = StepUp(request.UseDevelopmentStepUp) },
            request.Reason, idempotencyKey, cancellationToken).ConfigureAwait(false);
        await AuditAsync(context, AuditActions.MembershipRequestReplaced, "membership request edited (replaced by a new request awaiting two operator approvals)", "membership_request", requestId, outcome.CommandId,
            new { originalRequestId = requestId, replacementRequestId = outcome.CommandId, initialRole = request.InitialRoleName, commandId = outcome.CommandId }, cancellationToken).ConfigureAwait(false);
        return outcome with { RequestId = outcome.CommandId, ExpiresOn = clock.GetUtcNow().AddDays(7) };
    }

    private async Task<AuthorityOrganizationRoster> RosterAsync(RequestContext context, CancellationToken cancellationToken)
    {
        try
        {
            return await roster.GetRosterAsync(context.ToDelegatedUser(), new OrganizationRosterQuery(context.OrganizationId), cancellationToken).ConfigureAwait(false);
        }
        catch (AuthorityClientException ex) when (ex.HttpStatusCode == 404)
        {
            throw new BusinessException("FORBIDDEN", 403, "organization owner role required");
        }
    }

    private async Task<MemberCommandResponse> SubmitAsync(RequestContext context, SecurityCommandKind kind, object payload, string? reason, string idempotencyKey, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        // The decision only supplies the current revision for the CAS; the Authority decides the command itself.
        var decision = await authority.CheckPermissionAsync(context.ToDelegatedUser(), new PermissionCheckRequest(context.OrganizationId, new ResourceReference(ProtectedResourceKind.Organization, context.OrganizationId), ProtectedAction.ManageOrganization), cancellationToken).ConfigureAwait(false);
        var command = await awaiter.SubmitAndAwaitAsync(context.ToDelegatedUser(), new SecurityCommandRequest(context.OrganizationId, kind, new ResourceReference(ProtectedResourceKind.Organization, context.OrganizationId), decision.AuthorizationRevision,
            JsonSerializer.SerializeToElement(payload), reason?.Trim()), "member:" + idempotencyKey, cancellationToken).ConfigureAwait(false);
        if (command.CommandStatus != SecurityCommandStatus.Effective)
        {
            throw new PendingCommandException(new CommandOutcome(command.CommandId, command.CommandStatus.ToString(), command.AuthorizationRevision, false));
        }

        return new MemberCommandResponse(command.CommandId, command.CommandStatus.ToString(), command.AuthorizationRevision);
    }

    /// <summary>One business audit event per effective Authority command: an idempotent retry returns the same command and records nothing new.</summary>
    private async Task AuditAsync(RequestContext context, string action, string description, string targetType, Guid targetId, Guid commandId, object metadata, CancellationToken cancellationToken)
    {
        await using var scope = await store.BeginScopeAsync(cancellationToken).ConfigureAwait(false);
        var marker = commandId.ToString("D");
        var earlier = await scope.GetTargetAuditEventsAsync(context.OrganizationId, targetType, targetId, 500, cancellationToken).ConfigureAwait(false);
        if (earlier.Any(e => e.EventAction == action && e.EventMetadataJson.Contains(marker, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        AuditRecorder.Record(scope, context, clock, AuditCategories.Security, action, description, targetEntityType: targetType, targetEntityId: targetId, metadata: metadata);
        await scope.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private static string Reason(string? reason)
    {
        var trimmed = reason?.Trim() ?? string.Empty;
        return trimmed.Length is < 3 or > 500 ? throw new BusinessException("INVALID_REQUEST", 422, "give a reason (3 to 500 characters)") : trimmed;
    }

    private static string? StepUp(bool requested) => requested ? SyntheticStepUpMarker : null;

    private static void RequireDesignerRole(string? roleName)
    {
        if (!string.Equals(roleName, "Designer", StringComparison.Ordinal))
        {
            throw new BusinessException("INVALID_REQUEST", 422, "only the Designer role changes with one owner; the Organization Owner role changes through a proposal approved by another owner");
        }
    }
}
