using System.Text.Json;
using EnterpriseForms.Application.Common;
using EnterpriseForms.Application.Common.Authority;
using EnterpriseForms.Application.Common.Persistence;
using EnterpriseForms.Application.Features.Audit;
using EnterpriseForms.Application.Features.FormDefinitions.CreateFormDefinition;
using EnterpriseForms.Application.Features.Organizations.ReasonNotes;
using EnterpriseForms.Contracts.Features;
using EnterpriseForms.Domain.Features.Audit;

namespace EnterpriseForms.Application.Features.Organizations;

/// <summary>
/// C8 M4 (AC-8.1, C-110): Security Auditors of the organization — the Authority's organization relation
/// <c>security_auditor</c> (permission <c>view_audit</c>, no record content). Appointment and removal are GrantSystemRole /
/// RevokeSystemRole security commands that only an existing Security Administrator may submit, never for themself, with
/// step-up and an owner-approved reason code. Every decision is the Authority's: this handler sends every attempt and never
/// pre-judges the actor, the target or the code (the Authority validates the code exactly and records each refusal).
/// An optional reason note (C8 M4 M1, Design A revision 2) is validated, then sealed exactly once with
/// <see cref="IAuditorNoteSealer"/> before anything is sent: the Authority receives ciphertext only, and the note is never
/// stored, logged or audited here (the business audit keeps the reason code only, B8). Without a configured key provider a
/// request with a note is refused (503) and nothing is sent; a request without a note is unaffected.
/// </summary>
public sealed class SecurityAuditorsHandlers(IBusinessStore store, IAuthorizationAuthorityClient authority, AuthorityCommandAwaiter awaiter, IAuditorNoteSealer sealer, TimeProvider clock)
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
        string? note;
        try
        {
            note = AuditorReasonNoteFormat.Normalize(reasonNote);
        }
        catch (ArgumentException ex)
        {
            throw new BusinessException("INVALID_REQUEST", 422, ex.Message); // fixed rule text; the note is never echoed
        }

        if (userId == Guid.Empty || code.Length is 0 or > 64)
        {
            throw new BusinessException("INVALID_REQUEST", 422, "choose a person and a reason");
        }

        // B3/B4/B5: sealed exactly once, before anything is sent; the same envelope is reused on every retry of this request.
        object? envelope = null;
        if (note is not null)
        {
            if (!sealer.IsAvailable)
            {
                throw new BusinessException("ENCRYPTION_UNAVAILABLE", 503, "reason notes cannot be encrypted in this environment; submit without a note");
            }

            AuditorReasonNoteEnvelope sealedNote;
            try
            {
                var direction = kind == SecurityCommandKind.GrantSystemRole ? AuditorReasonNoteFormat.Appoint : AuditorReasonNoteFormat.Remove;
                sealedNote = sealer.Seal(new AuditorReasonNoteContext(context.OrganizationId, userId, RoleName, direction, Guid.CreateVersion7()), note);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                throw new BusinessException("ENCRYPTION_UNAVAILABLE", 503, "the reason note could not be encrypted; nothing was sent");
            }

            envelope = new
            {
                envelope_format = sealedNote.EnvelopeFormat,
                envelope_version = sealedNote.EnvelopeVersion,
                algorithm_name = sealedNote.AlgorithmName,
                key_reference = sealedNote.KeyReference,
                note_id = sealedNote.NoteId,
                nonce_b64 = sealedNote.NonceB64,
                ciphertext_b64 = sealedNote.CiphertextB64,
                tag_b64 = sealedNote.TagB64,
                wrapped_data_key_b64 = sealedNote.WrappedDataKeyB64,
            };
        }

        // The decision only supplies the expected revision; the Authority alone decides (no business pre-check, as for administrators).
        var decision = await authority.CheckPermissionAsync(context.ToDelegatedUser(), new PermissionCheckRequest(context.OrganizationId, new ResourceReference(ProtectedResourceKind.Organization, context.OrganizationId), ProtectedAction.AdministerSecurity), cancellationToken).ConfigureAwait(false);
        var payload = JsonSerializer.SerializeToElement(new { authority_user_id = userId, role_name = RoleName, reason_code = code, step_up_evidence = stepUp ? SecurityAdministratorsHandlers.SyntheticStepUpMarker : null, reason_note_envelope = envelope });
        // change_reason carries the structured code only.
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
