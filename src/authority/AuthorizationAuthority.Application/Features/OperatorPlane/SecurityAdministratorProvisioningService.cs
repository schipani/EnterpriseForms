using System.Text.Json;
using System.Text.RegularExpressions;
using AuthorizationAuthority.Application.Common;
using AuthorizationAuthority.Application.Common.Ports;
using AuthorizationAuthority.Application.Features.Journal;
using AuthorizationAuthority.Application.Features.SecurityCommands;
using AuthorizationAuthority.Contracts.V1;
using AuthorizationAuthority.Domain.Features.Organizations;
using AuthorizationAuthority.Domain.Features.SecurityCommands;

namespace AuthorizationAuthority.Application.Features.OperatorPlane;

/// <summary>
/// DR-C7-3: the first Security Administrator of an existing organization, and recovery when its Security Administrators are
/// unavailable — two separate operator-plane operations, never reachable from the business API or a user session.
/// <list type="bullet">
/// <item>The organization is the Authority's own record (path id, confirmed in the body); the member is an existing enabled
/// identity binding named by user id, issuer and subject.</item>
/// <item>Dry run first: the preview shows the exact organization, member, role, current administrators and consequences,
/// and the statement the two approvers sign. Nothing changes and nothing is journaled.</item>
/// <item>Execution requires two independent approvals (<see cref="OperatorApprovalVerifier"/>), then commits one security
/// command (relation <c>system_administrator</c> only) through the normal journal, projection barrier and archive, with
/// an audit-stream event naming the operator workload, approvers, assurance, justification and incident. Refusals are
/// journaled too.</item>
/// <item>Single use and idempotent: the operation id is the command's idempotency key; the same statement again returns the
/// recorded outcome without a second change; a different statement under the same id is a conflict. Concurrent requests
/// serialize on the organization lock and re-check the preconditions inside it.</item>
/// <item>First appointment only when no Security Administrator with an enabled identity exists. Recovery only when one or more
/// exist; it requires an incident reference, may remove explicitly named administrators, and raises a security alert.</item>
/// </list>
/// The role confers no Designer rights, record ownership, record content or policy changes; nothing else is granted.
/// </summary>
public sealed partial class SecurityAdministratorProvisioningService(IAuthorityStore store, OperatorApprovalVerifier verifier, ISecurityAlertSink alerts, TimeProvider clock)
{
    private const int StatementVersion = 1;

    public async Task<OperatorSecurityAdministratorStatus> GetStatusAsync(Guid organizationId, Guid correlationId, CancellationToken cancellationToken)
    {
        await using var scope = await store.BeginScopeAsync(cancellationToken).ConfigureAwait(false);
        var state = await scope.ReadOrganizationAsync(organizationId, cancellationToken).ConfigureAwait(false)
                    ?? throw new AuthorityException(AuthorityErrorCodes.NotFound, 404, false);
        var (members, usable) = await MembersAsync(scope, organizationId, cancellationToken).ConfigureAwait(false);
        return new OperatorSecurityAdministratorStatus(organizationId, state.OrganizationDisplayName, state.AuthorizationRevision, usable, verifier.Mode, verifier.Operational, verifier.Assurance,
            OperatorApprovalVerifier.RequiredApprovals, verifier.MaxApprovalLifetimeSeconds, members, correlationId);
    }

    public Task<SecurityAdministratorProvisioningResponse> FirstAppointmentAsync(Guid organizationId, FirstSecurityAdministratorRequest request, string operatorName, Guid correlationId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return ProvisionAsync(new Input(SecurityAdministratorProvisioningKinds.FirstAppointment, organizationId, request.OperationId, request.ConfirmOrganizationId, request.TargetAuthorityUserId,
            request.TargetIssuerUri, request.TargetSubjectIdentifier, request.Justification, null, [], request.DryRun, request.Approvals), operatorName, correlationId, cancellationToken);
    }

    public Task<SecurityAdministratorProvisioningResponse> RecoverAsync(Guid organizationId, SecurityAdministratorRecoveryRequest request, string operatorName, Guid correlationId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return ProvisionAsync(new Input(SecurityAdministratorProvisioningKinds.Recovery, organizationId, request.OperationId, request.ConfirmOrganizationId, request.TargetAuthorityUserId,
            request.TargetIssuerUri, request.TargetSubjectIdentifier, request.Justification, request.IncidentReference, request.RemovedAdministratorIds ?? [], request.DryRun, request.Approvals), operatorName, correlationId, cancellationToken);
    }

    private async Task<SecurityAdministratorProvisioningResponse> ProvisionAsync(Input input, string operatorName, Guid correlationId, CancellationToken cancellationToken)
    {
        var recovery = input.Kind == SecurityAdministratorProvisioningKinds.Recovery;
        Validate(input, recovery);
        var removed = input.Removed.Distinct().Order().ToList();
        var statement = new SecurityAdministratorApprovalStatement(StatementVersion, input.OperationId, input.Kind, input.OrganizationId, input.TargetUserId, input.TargetIssuer.Trim(), input.TargetSubject.Trim(),
            OrganizationRoleNames.SystemAdministrator, input.Justification.Trim(), recovery ? input.IncidentReference!.Trim() : null, removed);
        var statementJson = CanonicalJson.Serialize(statement);
        var statementDigest = CanonicalJson.Digest(statementJson);
        var statementSha256 = Convert.ToHexStringLower(statementDigest);
        var idempotencyKey = (recovery ? "security-admin-recovery:" : "security-admin-first:") + input.OperationId.ToString("N");

        await using var scope = await store.BeginScopeAsync(cancellationToken).ConfigureAwait(false);
        var state = (input.DryRun
                        ? await scope.ReadOrganizationAsync(input.OrganizationId, cancellationToken).ConfigureAwait(false)
                        : await scope.LockOrganizationAsync(input.OrganizationId, cancellationToken).ConfigureAwait(false))
                    ?? throw new AuthorityException(AuthorityErrorCodes.NotFound, 404, false, "organization is not registered with the Authority");

        if (!input.DryRun)
        {
            var existing = await scope.FindCommandByIdempotencyKeyAsync(state.OrganizationId, idempotencyKey, cancellationToken).ConfigureAwait(false);
            if (existing is not null)
            {
                if (!existing.CommandRequestDigest.AsSpan().SequenceEqual(statementDigest))
                {
                    throw new AuthorityException(AuthorityErrorCodes.IdempotencyConflict, 409, false, "the operation id was already used for a different appointment");
                }

                var replay = await SecurityCommandService.ToStatusAsync(scope, state, existing, cancellationToken).ConfigureAwait(false);
                var (replayMembers, _) = await MembersAsync(scope, state.OrganizationId, cancellationToken).ConfigureAwait(false);
                return Response(input, state, replayMembers, removed, statementJson, statementSha256, eligible: true, refusal: null, existing.SecurityCommandId, replay.CommandStatus.ToString(), replayed: true, correlationId);
            }
        }

        var graph = new DesiredStateGraph(await scope.GetActiveEdgesAsync(state.OrganizationId, cancellationToken).ConfigureAwait(false));
        var bindings = (await scope.GetIdentityBindingsAsync(state.OrganizationId, cancellationToken).ConfigureAwait(false)).ToDictionary(b => b.AuthorityUserId);
        var (members, _) = await MembersAsync(scope, state.OrganizationId, cancellationToken).ConfigureAwait(false);
        var refusal = Evaluate(input, recovery, removed, graph, bindings, state);

        if (input.DryRun)
        {
            return Response(input, state, members, removed, statementJson, statementSha256, refusal is null, refusal, null, null, replayed: false, correlationId);
        }

        IReadOnlyList<VerifiedOperatorApproval> approvals = [];
        try
        {
            approvals = verifier.Verify(input.Approvals, statement, statementSha256, clock.GetUtcNow());
            if (refusal is not null)
            {
                throw refusal;
            }

            var target = bindings[input.TargetUserId];
            var now = clock.GetUtcNow();
            var plan = new CommandPlan();
            plan.AddEdges.Add((ResourceTypeNames.Organization, state.OrganizationId, OrganizationRoleNames.SystemAdministrator, ResourceTypeNames.User, target.AuthorityUserId, string.Empty));
            plan.RemoveEdges.AddRange(AdministratorEdges(graph, state.OrganizationId).Where(e => removed.Contains(e.SubjectEntityId)));
            var payloadJson = CanonicalJson.Serialize(new ProvisioningPayload(input.OperationId, input.Kind, OrganizationRoleNames.SystemAdministrator, target.AuthorityUserId, target.IdentityIssuerUri, target.IdentitySubjectIdentifier,
                statement.Justification, statement.IncidentReference, removed, operatorName, verifier.Assurance, statementSha256,
                approvals.Select(a => new ProvisioningApproval(a.ApproverId, a.ApprovalSha256, a.IssuedOn, a.ExpiresOn)).ToList()));
            plan.NormalizedPayloadJson = payloadJson;
            var command = new SecurityCommand
            {
                SecurityCommandId = Guid.CreateVersion7(),
                OrganizationId = state.OrganizationId,
                // Operator commands have no organization identity of their own (as in the bootstrap): the actor binding is
                // the appointee's; the operator workload, the approvers and the assurance are in the payload and audit event.
                ActorIdentityBindingId = target.IdentityBindingId,
                CommandKindName = recovery ? "RecoverSecurityAdministrator" : "ProvisionFirstSecurityAdministrator",
                CommandIdempotencyKey = idempotencyKey,
                CommandRequestDigest = statementDigest,
                ExpectedAuthorizationRevision = state.AuthorizationRevision,
                CommandPayloadJson = payloadJson,
                CommandReasonText = (recovery ? "operator recovery " + statement.IncidentReference + ": " : "operator first appointment: ") + statement.Justification,
                CorrelationIdentifier = correlationId,
                CommandCreatedOn = now,
                CommandModifiedOn = now,
            };
            var actor = new BoundActor(new VerifiedActor("authority-operator-plane", operatorName, operatorName), target);
            var commandRequest = new SecurityCommandRequest(state.OrganizationId, SecurityCommandKind.GrantSystemRole,
                new ResourceRef(ResourceKind.Organization, state.OrganizationId), state.AuthorizationRevision, JsonDocument.Parse(payloadJson).RootElement, command.CommandReasonText);
            SecurityCommandService.ApplyPlan(scope, state, command, plan, actor, commandRequest, cancellationToken);

            var eventKind = recovery ? "SecurityAdministratorRecovered" : "SecurityAdministratorFirstAppointed";
            AuditJournal.Append(scope, state, eventKind, new
            {
                security_command_id = command.SecurityCommandId,
                operation_id = input.OperationId,
                organization_id = state.OrganizationId,
                target_authority_user_id = target.AuthorityUserId,
                target_identity_binding_id = target.IdentityBindingId,
                target_issuer_uri = target.IdentityIssuerUri,
                target_subject_identifier = target.IdentitySubjectIdentifier,
                role_name = OrganizationRoleNames.SystemAdministrator,
                removed_administrator_ids = removed,
                justification = statement.Justification,
                incident_reference = statement.IncidentReference,
                operator_workload = operatorName,
                approval_assurance = verifier.Assurance,
                approvers = approvals.Select(a => new { approver_id = a.ApproverId, approval_sha256 = a.ApprovalSha256, issued_on = a.IssuedOn, expires_on = a.ExpiresOn }),
                statement_sha256 = statementSha256,
                authorization_revision = state.AuthorizationRevision,
                alert_raised = true,
                correlation_id = correlationId,
            }, clock);
            var alert = new SecurityAlert(eventKind, recovery ? "Critical" : "Warning", state.OrganizationId, input.OperationId, command.SecurityCommandId,
                recovery ? "Security Administrator recovery executed by the operator plane" : "First Security Administrator appointed by the operator plane", correlationId);
            AuditJournal.Append(scope, state, "SecurityAlertRaised", new { alert_kind = alert.AlertKind, severity = alert.Severity, operation_id = alert.OperationId, security_command_id = alert.SecurityCommandId, correlation_id = correlationId }, clock);
            await scope.CommitAsync(cancellationToken).ConfigureAwait(false);
            alerts.Raise(alert);

            var status = await SecurityCommandService.ToStatusAsync(scope, state, command, cancellationToken).ConfigureAwait(false);
            var (after, _) = await MembersAsync(scope, state.OrganizationId, cancellationToken).ConfigureAwait(false);
            return Response(input, state, after, removed, statementJson, statementSha256, eligible: true, refusal: null, command.SecurityCommandId, status.CommandStatus.ToString(), replayed: false, correlationId);
        }
        catch (AuthorityException ex)
        {
            // Refusals are independent, archived evidence too (approver ids only when their approvals verified).
            AuditJournal.Append(scope, state, "SecurityAdministratorProvisioningRejected", new
            {
                operation_id = input.OperationId,
                operation_kind = input.Kind,
                organization_id = state.OrganizationId,
                target_authority_user_id = input.TargetUserId,
                operator_workload = operatorName,
                approval_assurance = verifier.Assurance,
                verified_approvers = approvals.Select(a => a.ApproverId),
                rejection_reason = ex.ErrorCode,
                correlation_id = correlationId,
            }, clock);
            if (recovery)
            {
                AuditJournal.Append(scope, state, "SecurityAlertRaised", new { alert_kind = "SecurityAdministratorRecoveryRejected", severity = "Warning", operation_id = input.OperationId, security_command_id = (Guid?)null, correlation_id = correlationId }, clock);
            }

            await scope.CommitAsync(cancellationToken).ConfigureAwait(false);
            if (recovery)
            {
                alerts.Raise(new SecurityAlert("SecurityAdministratorRecoveryRejected", "Warning", state.OrganizationId, input.OperationId, null, "Security Administrator recovery refused: " + ex.ErrorCode, correlationId));
            }

            throw;
        }
    }

    private static void Validate(Input input, bool recovery)
    {
        if (input.OperationId == Guid.Empty || input.TargetUserId == Guid.Empty || string.IsNullOrWhiteSpace(input.TargetIssuer) || string.IsNullOrWhiteSpace(input.TargetSubject))
        {
            throw AuthorityException.Invalid("operation_id, target_authority_user_id, target_issuer_uri and target_subject_identifier are required");
        }

        if (input.ConfirmOrganizationId != input.OrganizationId)
        {
            throw AuthorityException.Invalid("confirm_organization_id must equal the organization in the path");
        }

        if (string.IsNullOrWhiteSpace(input.Justification) || input.Justification.Trim().Length is < 10 or > 500)
        {
            throw AuthorityException.Invalid("a justification of 10 to 500 characters is required");
        }

        if (recovery && (input.IncidentReference is null || !IncidentPattern().IsMatch(input.IncidentReference.Trim())))
        {
            throw AuthorityException.Invalid("recovery requires an incident reference (3 to 64 characters: letters, digits and . _ : / -)");
        }

        if (input.Removed.Count > 20 || input.Removed.Contains(input.TargetUserId))
        {
            throw AuthorityException.Invalid("the recovery scope may remove at most 20 administrators and never the appointee");
        }
    }

    /// <summary>Preconditions on the Authority's own state (inside the organization lock when executing). Null when eligible.</summary>
    private static AuthorityException? Evaluate(Input input, bool recovery, List<Guid> removed, DesiredStateGraph graph, Dictionary<Guid, IdentityBinding> bindings, OrganizationAuthorizationState state)
    {
        if (state.IntegrityQuarantineEnabled)
        {
            return AuthorityException.Quarantined("organization quarantined");
        }

        if (!bindings.TryGetValue(input.TargetUserId, out var target) || !target.IdentityBindingEnabled
            || !string.Equals(target.IdentityIssuerUri, input.TargetIssuer.Trim(), StringComparison.Ordinal) || !string.Equals(target.IdentitySubjectIdentifier, input.TargetSubject.Trim(), StringComparison.Ordinal))
        {
            return AuthorityException.Invalid("the target is not an enabled verified member of this organization with that identity");
        }

        var administrators = AdministratorEdges(graph, state.OrganizationId).Select(e => e.SubjectEntityId).ToHashSet();
        if (administrators.Contains(input.TargetUserId))
        {
            return recovery ? AuthorityException.Invalid("the target is already a Security Administrator") : new AuthorityException(AuthorityErrorCodes.SecurityAdministratorExists, 409, false, "the target is already a Security Administrator");
        }

        var usable = administrators.Count(id => bindings.TryGetValue(id, out var b) && b.IdentityBindingEnabled);
        if (!recovery && usable > 0)
        {
            return new AuthorityException(AuthorityErrorCodes.SecurityAdministratorExists, 409, false, "the organization already has a Security Administrator; appointments are made by Security Administrators (or use recovery)");
        }

        if (recovery && administrators.Count == 0)
        {
            return new AuthorityException(AuthorityErrorCodes.RecoveryNotApplicable, 409, false, "the organization has no Security Administrator; use a first appointment");
        }

        if (recovery && removed.Any(id => !administrators.Contains(id)))
        {
            return AuthorityException.Invalid("the recovery scope names someone who is not a Security Administrator");
        }

        return null;
    }

    private static IEnumerable<AuthorityRelationshipEdge> AdministratorEdges(DesiredStateGraph graph, Guid organizationId) =>
        graph.EdgesOf(ResourceTypeNames.Organization, organizationId, OrganizationRoleNames.SystemAdministrator).Where(e => e.SubjectTypeName == ResourceTypeNames.User);

    private static async Task<(List<OperatorOrganizationPrincipal> Members, int Usable)> MembersAsync(IAuthorityScope scope, Guid organizationId, CancellationToken cancellationToken)
    {
        var graph = new DesiredStateGraph(await scope.GetActiveEdgesAsync(organizationId, cancellationToken).ConfigureAwait(false));
        var appointed = AdministratorEdges(graph, organizationId).GroupBy(e => e.SubjectEntityId).ToDictionary(g => g.Key, g => g.Min(e => e.RelationshipCreatedOn));
        var members = (await scope.GetIdentityBindingsAsync(organizationId, cancellationToken).ConfigureAwait(false))
            .Select(b => new OperatorOrganizationPrincipal(b.AuthorityUserId, b.IdentityIssuerUri, b.IdentitySubjectIdentifier, b.IdentityBindingEnabled, appointed.ContainsKey(b.AuthorityUserId), appointed.TryGetValue(b.AuthorityUserId, out var on) ? on : null))
            .OrderByDescending(m => m.IsSecurityAdministrator).ThenBy(m => m.SubjectIdentifier, StringComparer.Ordinal).ToList();
        return (members, members.Count(m => m.IsSecurityAdministrator && m.IdentityEnabled));
    }

    private SecurityAdministratorProvisioningResponse Response(Input input, OrganizationAuthorizationState state, List<OperatorOrganizationPrincipal> members, List<Guid> removed, string statementJson, string statementSha256,
        bool eligible, AuthorityException? refusal, Guid? commandId, string? commandStatus, bool replayed, Guid correlationId)
    {
        var target = members.FirstOrDefault(m => m.AuthorityUserId == input.TargetUserId) ?? new OperatorOrganizationPrincipal(input.TargetUserId, input.TargetIssuer, input.TargetSubject, false, false, null);
        var consequences = new List<string>
        {
            $"{target.SubjectIdentifier} ({target.IssuerUri}) receives the organization relation system_administrator (Security Administrator) in \"{state.OrganizationDisplayName}\".",
            "Security Administrators approve plaintext field search and grant search access, and appoint and remove Security Administrators (with step-up).",
            "No Form Designer rights, record ownership, record content, encryption-policy change or access to another organization is granted.",
            input.Kind == SecurityAdministratorProvisioningKinds.Recovery
                ? $"Recovery under incident {input.IncidentReference}: {removed.Count} named administrator(s) lose the role; a critical security alert is raised."
                : "Allowed only while the organization has no Security Administrator with an enabled identity.",
            $"Requires {OperatorApprovalVerifier.RequiredApprovals} approvals from distinct registered approvers ({verifier.Assurance}).",
        };
        return new SecurityAdministratorProvisioningResponse(input.DryRun, eligible, refusal?.ErrorCode, refusal?.Detail, input.OperationId, input.Kind, state.OrganizationId, state.OrganizationDisplayName, target,
            OrganizationRoleNames.SystemAdministrator, members.Where(m => m.IsSecurityAdministrator).ToList(), removed, consequences, statementJson, statementSha256, verifier.Mode, verifier.Assurance,
            OperatorApprovalVerifier.RequiredApprovals, verifier.MaxApprovalLifetimeSeconds, commandId, commandStatus, replayed, state.AuthorizationRevision, correlationId);
    }

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._:/-]{2,63}$")]
    private static partial Regex IncidentPattern();

    private sealed record Input(string Kind, Guid OrganizationId, Guid OperationId, Guid ConfirmOrganizationId, Guid TargetUserId, string TargetIssuer, string TargetSubject, string Justification,
        string? IncidentReference, IReadOnlyList<Guid> Removed, bool DryRun, IReadOnlyList<string>? Approvals);

    private sealed record ProvisioningApproval(string ApproverId, string ApprovalSha256, DateTimeOffset IssuedOn, DateTimeOffset ExpiresOn);

    private sealed record ProvisioningPayload(Guid OperationId, string OperationKind, string RoleName, Guid TargetAuthorityUserId, string TargetIssuerUri, string TargetSubjectIdentifier, string Justification,
        string? IncidentReference, IReadOnlyList<Guid> RemovedAdministratorIds, string OperatorWorkload, string ApprovalAssurance, string StatementSha256, IReadOnlyList<ProvisioningApproval> Approvals);
}
