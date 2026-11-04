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
/// C8 M4 (AC-8.1, C-110): Security Auditors of the organization — the Authority's organization relation
/// <c>security_auditor</c> (permission <c>view_audit</c>, no record content). Appointment and removal are GrantSystemRole /
/// RevokeSystemRole security commands that only an existing Security Administrator may submit, never for themself, with
/// step-up and an owner-approved reason code. Every decision is the Authority's: this handler sends every attempt and never
/// pre-judges the actor, the target or the code (the Authority validates the code exactly and records each refusal).
/// The optional note goes to the Authority only: it is never written to this application's database, audit metadata or logs.
/// </summary>
public sealed class SecurityAuditorsHandlers(IBusinessStore store, IAuthorizationAuthorityClient authority, AuthorityCommandAwaiter awaiter, TimeProvider clock)
{
    public const string RoleName = "security_auditor";

    /// <summary>The owner-approved codes and labels (C-110), in the approved order. The Authority holds the same closed lists.</summary>
    public static readonly IReadOnlyList<SecurityAuditorReasonCodeResponse> AppointmentReasons =
    [
        new("AuditCoverageAssignment", "Assigned to audit coverage"),
        new("InvestigationSupport", "Supporting an investigation"),
        new("ReplacementOfDepartingAuditor", "Replacing a departing auditor"),
        new("ComplianceReview", "Periodic compliance review"),
    ];

    public static readonly IReadOnlyList<SecurityAuditorReasonCodeResponse> RemovalReasons =
    [
        new("AssignmentEnded", "Assignment ended"),
        new("RoleChange", "Changed role or responsibilities"),
        new("MembershipEnding", "Leaving the organization"),
        new("ConflictOfInterest", "Conflict of interest"),
        new("SecurityConcern", "Security concern"),
    ];

    public static SecurityAuditorReasonCodesResponse ReasonCodes() => new(AppointmentReasons, RemovalReasons);

    /// <summary>Returns once the Authority reports the command Effective.</summary>
    public async Task AppointAsync(RequestContext context, AppointSecurityAuditorRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(request);
        var code = await SubmitAsync(context, SecurityCommandKind.GrantSystemRole, request.UserId, request.ReasonCode, request.ReasonNote, request.UseDevelopmentStepUp, $"security-auditor-appoint:{request.UserId:N}:{Guid.CreateVersion7():N}", cancellationToken).ConfigureAwait(false);
        await AuditAsync(context, AuditActions.SecurityAuditorAppointed, "Security Auditor appointed", request.UserId, code, cancellationToken).ConfigureAwait(false);
    }

    public async Task RemoveAsync(RequestContext context, Guid userId, RemoveSecurityAuditorRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(request);
        var code = await SubmitAsync(context, SecurityCommandKind.RevokeSystemRole, userId, request.ReasonCode, request.ReasonNote, request.UseDevelopmentStepUp, $"security-auditor-remove:{userId:N}:{Guid.CreateVersion7():N}", cancellationToken).ConfigureAwait(false);
        await AuditAsync(context, AuditActions.SecurityAuditorRemoved, "Security Auditor removed", userId, code, cancellationToken).ConfigureAwait(false);
    }

    private async Task<string> SubmitAsync(RequestContext context, SecurityCommandKind kind, Guid userId, string? reasonCode, string? reasonNote, bool stepUp, string idempotencyKey, CancellationToken cancellationToken)
    {
        var code = reasonCode?.Trim() ?? string.Empty;
        var note = string.IsNullOrWhiteSpace(reasonNote) ? null : reasonNote.Trim();
        if (userId == Guid.Empty || code.Length is 0 or > 64 || note is { Length: > 500 })
        {
            throw new BusinessException("INVALID_REQUEST", 422, "choose a person and a reason; an optional note may have up to 500 characters");
        }

        // The decision only supplies the expected revision; the Authority alone decides (no business pre-check, as for administrators).
        var decision = await authority.CheckPermissionAsync(context.ToDelegatedUser(), new PermissionCheckRequest(context.OrganizationId, new ResourceReference(ProtectedResourceKind.Organization, context.OrganizationId), ProtectedAction.AdministerSecurity), cancellationToken).ConfigureAwait(false);
        var payload = JsonSerializer.SerializeToElement(new { authority_user_id = userId, role_name = RoleName, reason_code = code, reason_note = note, step_up_evidence = stepUp ? SecurityAdministratorsHandlers.SyntheticStepUpMarker : null });
        // change_reason carries the structured code only, never the note.
        var command = await awaiter.SubmitAndAwaitAsync(context.ToDelegatedUser(), new SecurityCommandRequest(context.OrganizationId, kind, new ResourceReference(ProtectedResourceKind.Organization, context.OrganizationId), decision.AuthorizationRevision, payload, code), idempotencyKey, cancellationToken).ConfigureAwait(false);
        if (command.CommandStatus != SecurityCommandStatus.Effective)
        {
            throw new PendingCommandException(new CommandOutcome(command.CommandId, command.CommandStatus.ToString(), command.AuthorizationRevision, false));
        }

        return code;
    }

    /// <summary>Business timeline (projection): actor, target and the reason code only; the Authority journal is the evidence.</summary>
    private async Task AuditAsync(RequestContext context, string action, string description, Guid userId, string reasonCode, CancellationToken cancellationToken)
    {
        await using var scope = await store.BeginScopeAsync(cancellationToken).ConfigureAwait(false);
        AuditRecorder.Record(scope, context, clock, AuditCategories.Security, action, description, targetEntityType: "user", targetEntityId: userId, metadata: new { reasonCode });
        await scope.CommitAsync(cancellationToken).ConfigureAwait(false);
    }
}
