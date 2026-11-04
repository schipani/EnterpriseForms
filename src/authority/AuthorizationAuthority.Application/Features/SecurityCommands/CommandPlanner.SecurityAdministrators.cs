using System.Text.Json;
using AuthorizationAuthority.Application.Common;
using AuthorizationAuthority.Application.Common.Ports;
using AuthorizationAuthority.Contracts.V1;
using AuthorizationAuthority.Domain.Features.Organizations;

namespace AuthorizationAuthority.Application.Features.SecurityCommands;

/// <summary>
/// Phase C7 (owner message 7): appointment and removal of Security Administrators inside an organization. "Security
/// Administrator" is the existing organization relation <c>system_administrator</c>, whose only SpiceDB permission is
/// <c>administer_security</c>; no second role system. C8 M4 (AC-8.1, C-110) adds the Security Auditor
/// (<c>security_auditor</c>, permission <c>view_audit</c>) to the same two commands, selected by <c>role_name</c>.
/// <list type="bullet">
/// <item>Only an existing Security Administrator of the same organization may appoint or remove either role — an organization
/// owner, a designer or an auditor cannot appoint anyone (or themselves).</item>
/// <item>Nobody may appoint or remove themself, for either role (D8-62a: Security Administrator self-removal, allowed before
/// C-110, is refused).</item>
/// <item>The appointee must be an enabled identity binding of this organization (verified issuer/subject); never an email
/// address, a business row or a client flag.</item>
/// <item>Fresh step-up is required (the Authority's PB-08 gate: unavailable in staging and production, so these
/// commands are refused there until PB-08 exists; the synthetic development path elsewhere).</item>
/// <item>Security Administrator: free-text reason (1..500); the last Security Administrator who can still sign in (enabled
/// identity) cannot be removed. Security Auditor: an owner-approved reason code for the direction and an optional note
/// (never shown to auditors); there is no last-holder rule, an organization may have no auditor.</item>
/// </list>
/// The first Security Administrator of an organization without one comes from the operator plane (organization bootstrap
/// today; post-bootstrap appointment and recovery is decision DR-C7-3).
/// </summary>
public static partial class CommandPlanner
{
    private static async Task<CommandPlan> SecurityAdministratorAsync(IAuthorityScope scope, Guid organizationId, BoundActor actor, SecurityCommandRequest request, DesiredStateGraph graph, FieldSearchApprovalGate gate, bool appoint, CancellationToken cancellationToken)
    {
        if (string.Equals(RoleCommandEvidence.RoleNameOf(request.CommandPayload), OrganizationRoleNames.SecurityAuditor, StringComparison.Ordinal))
        {
            return await SecurityAuditorAsync(scope, organizationId, actor, request, graph, gate, appoint, cancellationToken).ConfigureAwait(false);
        }

        var payload = CanonicalJson.Deserialize<SecurityAdministratorPayload>(request.CommandPayload);
        RequireResource(request, ResourceKind.Organization, organizationId);
        RequireSecurityAdministrator(graph, organizationId, actor);
        if (!string.Equals(payload.RoleName, OrganizationRoleNames.SystemAdministrator, StringComparison.Ordinal))
        {
            throw AuthorityException.Invalid("only the system_administrator (Security Administrator) and security_auditor (Security Auditor) roles are managed by these commands");
        }

        if (payload.AuthorityUserId == Guid.Empty || string.IsNullOrWhiteSpace(payload.Reason) || payload.Reason.Length > 500)
        {
            throw AuthorityException.Invalid("authority_user_id and a reason (1..500 characters) are required");
        }

        RefuseSelfRoleChange(actor, payload.AuthorityUserId);
        gate.RequireStepUp(payload.StepUpEvidence);
        var holders = RoleHolders(graph, organizationId, OrganizationRoleNames.SystemAdministrator);
        var existing = holders.FirstOrDefault(e => e.SubjectEntityId == payload.AuthorityUserId);
        var plan = new CommandPlan { NormalizedPayloadJson = CanonicalJson.Serialize(payload with { StepUpEvidence = null }) };
        if (appoint)
        {
            if (existing is not null)
            {
                throw AuthorityException.Invalid("the user is already a Security Administrator");
            }

            await RequireUserBindingAsync(scope, organizationId, payload.AuthorityUserId, cancellationToken).ConfigureAwait(false);
            plan.AddEdges.Add((ResourceTypeNames.Organization, organizationId, OrganizationRoleNames.SystemAdministrator, ResourceTypeNames.User, payload.AuthorityUserId, string.Empty));
            return plan;
        }

        if (existing is null)
        {
            throw AuthorityException.Invalid("the user is not a Security Administrator");
        }

        // Removal works for a disabled identity too; what must remain is another administrator who can still sign in.
        var enabled = (await scope.GetIdentityBindingsAsync(organizationId, cancellationToken).ConfigureAwait(false)).Where(b => b.IdentityBindingEnabled).Select(b => b.AuthorityUserId).ToHashSet();
        if (!holders.Any(e => e.SubjectEntityId != payload.AuthorityUserId && enabled.Contains(e.SubjectEntityId)))
        {
            throw new AuthorityException(AuthorityErrorCodes.LastSecurityAdministrator, 409, false, "the last Security Administrator cannot be removed; appoint another one first");
        }

        plan.RemoveEdges.Add(existing);
        return plan;
    }

    /// <summary>C8 M4 (AC-8.1, rules A1–A9): Security Auditor appointment and removal.</summary>
    private static async Task<CommandPlan> SecurityAuditorAsync(IAuthorityScope scope, Guid organizationId, BoundActor actor, SecurityCommandRequest request, DesiredStateGraph graph, FieldSearchApprovalGate gate, bool appoint, CancellationToken cancellationToken)
    {
        // Strict binding: a free-text "reason" (the Security Administrator shape) or any other unknown member is rejected.
        var payload = CanonicalJson.Deserialize<SecurityAuditorPayload>(request.CommandPayload);
        RequireResource(request, ResourceKind.Organization, organizationId);
        RequireSecurityAdministrator(graph, organizationId, actor);
        if (payload.AuthorityUserId == Guid.Empty)
        {
            throw AuthorityException.Invalid("authority_user_id is required");
        }

        if (!SecurityAuditorReasonCodes.IsValid(payload.ReasonCode, appoint))
        {
            // The submitted value is never echoed: it is caller-supplied text that failed validation.
            throw AuthorityException.Invalid(appoint
                ? "reason_code must be one of the Security Auditor appointment codes: " + string.Join(", ", SecurityAuditorReasonCodes.Appointment)
                : "reason_code must be one of the Security Auditor removal codes: " + string.Join(", ", SecurityAuditorReasonCodes.Removal));
        }

        var note = string.IsNullOrWhiteSpace(payload.ReasonNote) ? null : payload.ReasonNote.Trim();
        if (note is { Length: > SecurityAuditorReasonCodes.MaximumNoteLength })
        {
            throw AuthorityException.Invalid($"reason_note is limited to {SecurityAuditorReasonCodes.MaximumNoteLength} characters");
        }

        RefuseSelfRoleChange(actor, payload.AuthorityUserId);
        gate.RequireStepUp(payload.StepUpEvidence);
        var existing = RoleHolders(graph, organizationId, OrganizationRoleNames.SecurityAuditor).FirstOrDefault(e => e.SubjectEntityId == payload.AuthorityUserId);
        var plan = new CommandPlan
        {
            NormalizedPayloadJson = CanonicalJson.Serialize(payload with { ReasonNote = note, StepUpEvidence = null }),
            CommandReasonOverride = payload.ReasonCode,
        };
        if (appoint)
        {
            if (existing is not null)
            {
                throw AuthorityException.Invalid("the user is already a Security Auditor");
            }

            await RequireUserBindingAsync(scope, organizationId, payload.AuthorityUserId, cancellationToken).ConfigureAwait(false);
            plan.AddEdges.Add((ResourceTypeNames.Organization, organizationId, OrganizationRoleNames.SecurityAuditor, ResourceTypeNames.User, payload.AuthorityUserId, string.Empty));
            return plan;
        }

        // No last-holder rule (A5); removal works for a disabled identity too (A3).
        plan.RemoveEdges.Add(existing ?? throw AuthorityException.Invalid("the user is not a Security Auditor"));
        return plan;
    }

    private static void RequireSecurityAdministrator(DesiredStateGraph graph, Guid organizationId, BoundActor actor)
    {
        if (!graph.HasOrganizationRole(organizationId, actor.AuthorityUserId, OrganizationRoleNames.SystemAdministrator))
        {
            throw AuthorityException.Forbidden("appointing or removing a Security Administrator or Security Auditor requires administer_security in this organization");
        }
    }

    /// <summary>D8-62a / A2: enforced here, in the Authority; UI hints are only a convenience.</summary>
    private static void RefuseSelfRoleChange(BoundActor actor, Guid targetUserId)
    {
        if (targetUserId == actor.AuthorityUserId)
        {
            throw new AuthorityException(AuthorityErrorCodes.SelfChangeForbidden, 403, false, "nobody may appoint or remove themself as Security Administrator or Security Auditor");
        }
    }

    private static List<Domain.Features.SecurityCommands.AuthorityRelationshipEdge> RoleHolders(DesiredStateGraph graph, Guid organizationId, string role) =>
        graph.EdgesOf(ResourceTypeNames.Organization, organizationId, role).Where(e => e.SubjectTypeName == ResourceTypeNames.User).ToList();
}

/// <summary>
/// C8 M4 (C-110): the only facts a refused GrantSystemRole / RevokeSystemRole may leave behind — on the rejected command row
/// and in its audit-stream entry. Validated identifiers only: never free text, a reason note, step-up evidence or an
/// invalid submitted reason value.
/// </summary>
public static class RoleCommandEvidence
{
    public sealed record Facts(Guid? TargetAuthorityUserId, string? RoleName, string? ReasonCode, bool? ReasonCodeValid);

    public static string? RoleNameOf(JsonElement payload) =>
        payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty("role_name", out var role) && role.ValueKind == JsonValueKind.String ? role.GetString() : null;

    public static Facts From(JsonElement payload, bool appointment)
    {
        var raw = RoleNameOf(payload);
        var role = raw is OrganizationRoleNames.SystemAdministrator or OrganizationRoleNames.SecurityAuditor ? raw : null;
        Guid? target = payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty("authority_user_id", out var id) && id.ValueKind == JsonValueKind.String && Guid.TryParse(id.GetString(), out var parsed) ? parsed : null;
        if (role != OrganizationRoleNames.SecurityAuditor)
        {
            return new Facts(target, role, null, null);
        }

        var code = payload.TryGetProperty("reason_code", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() : null;
        var valid = SecurityAuditorReasonCodes.IsValid(code, appointment);
        return new Facts(target, role, valid ? code : null, valid);
    }

    /// <summary>The payload stored on a rejected role command (replaces the raw request body).</summary>
    public static string SanitizedPayloadJson(Facts facts) =>
        CanonicalJson.Serialize(new { authority_user_id = facts.TargetAuthorityUserId, role_name = facts.RoleName, reason_code = facts.ReasonCode, reason_code_valid = facts.ReasonCodeValid });
}
