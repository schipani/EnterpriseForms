using System.Text.Json;
using AuthorizationAuthority.Application.Common;
using AuthorizationAuthority.Application.Common.Ports;
using AuthorizationAuthority.Application.Features.Journal;
using AuthorizationAuthority.Application.Features.OperatorPlane;
using AuthorizationAuthority.Application.Features.PlatformOperators;
using AuthorizationAuthority.Application.Features.SecurityCommands;
using AuthorizationAuthority.Contracts.V1;
using AuthorizationAuthority.Domain.Features.Organizations;
using AuthorizationAuthority.Domain.Features.PlatformOperators;
using AuthorizationAuthority.Domain.Features.SecurityCommands;

namespace AuthorizationAuthority.Application.Features.Membership;

/// <summary>
/// C8 M2 (AC-7.4, D8-29): the operator plane decides owner membership requests. Approval follows the DR-C7-3 dual-control
/// pattern — dry run first, then two ES256 approvals from distinct registered approvers over the exact statement, Development/
/// Testing only until production PIM exists — and re-checks everything inside the organization lock at execution: the
/// request is still pending and unexpired, the requesting owner is still an enabled owner, the identity is not yet bound and
/// still belongs to the organization's identity issuer. A late approval therefore never bypasses current state. Approval
/// commits one journaled security command creating the binding (plus the optional designer edge). The request id is the
/// single-use operation id; repeating it returns the recorded outcome. Rejection by a single operator grants nothing and is
/// recorded on the audit stream. Never reachable from the business API.
/// </summary>
public sealed class MembershipProvisioningService(IAuthorityStore store, OperatorApprovalVerifier verifier, TimeProvider clock)
{
    public const string ApprovalKind = "MembershipApproval";
    private const int StatementVersion = 1;

    public async Task<OperatorMembershipRequests> ListAsync(Guid organizationId, Guid correlationId, CancellationToken cancellationToken)
    {
        await using var scope = await store.BeginScopeAsync(cancellationToken).ConfigureAwait(false);
        var state = await scope.ReadOrganizationAsync(organizationId, cancellationToken).ConfigureAwait(false)
                    ?? throw new AuthorityException(AuthorityErrorCodes.NotFound, 404, false);
        var now = clock.GetUtcNow();
        var requests = (await scope.GetMembershipRequestsAsync(organizationId, now - OrganizationRosterService.HistoryWindow, cancellationToken).ConfigureAwait(false)).Select(r => OrganizationRosterService.ToEntry(r, now)).ToList();
        return new OperatorMembershipRequests(organizationId, state.OrganizationDisplayName, verifier.Mode, verifier.Operational, requests, correlationId);
    }

    public async Task<MembershipApprovalResponse> ApproveAsync(Guid organizationId, Guid requestId, MembershipApprovalRequest input, string operatorName, Guid correlationId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (input.ConfirmOrganizationId != organizationId)
        {
            throw AuthorityException.Invalid("confirm_organization_id must equal the organization in the path");
        }

        var justification = input.Justification?.Trim() ?? string.Empty;
        if (justification.Length is < 10 or > 500)
        {
            throw AuthorityException.Invalid("a justification of 10 to 500 characters is required");
        }

        var idempotencyKey = "membership-approve:" + requestId.ToString("N");
        await using var scope = await store.BeginScopeAsync(cancellationToken).ConfigureAwait(false);
        var state = (input.DryRun
                        ? await scope.ReadOrganizationAsync(organizationId, cancellationToken).ConfigureAwait(false)
                        : await scope.LockOrganizationAsync(organizationId, cancellationToken).ConfigureAwait(false))
                    ?? throw new AuthorityException(AuthorityErrorCodes.NotFound, 404, false, "organization is not registered with the Authority");
        var request = await scope.FindMembershipRequestAsync(organizationId, requestId, cancellationToken).ConfigureAwait(false)
                      ?? throw new AuthorityException(AuthorityErrorCodes.NotFound, 404, false, "no such membership request in this organization");
        var statement = new MembershipApprovalStatement(StatementVersion, requestId, ApprovalKind, organizationId, request.RequestedAuthorityUserId, request.RequestedIssuerUri, request.RequestedSubjectIdentifier,
            request.RequestedInitialRoleName, request.RequestedByAuthorityUserId, request.RequestExpiresOn, justification);
        var statementJson = CanonicalJson.Serialize(statement);
        var statementDigest = CanonicalJson.Digest(statementJson);
        var statementSha256 = Convert.ToHexStringLower(statementDigest);

        if (!input.DryRun && await scope.FindCommandByIdempotencyKeyAsync(organizationId, idempotencyKey, cancellationToken).ConfigureAwait(false) is { } existing)
        {
            if (!existing.CommandRequestDigest.AsSpan().SequenceEqual(statementDigest))
            {
                throw new AuthorityException(AuthorityErrorCodes.IdempotencyConflict, 409, false, "this request was already decided with a different statement");
            }

            var replay = await SecurityCommandService.ToStatusAsync(scope, state, existing, cancellationToken).ConfigureAwait(false);
            return Response(input.DryRun, state, request, statementJson, statementSha256, null, existing.SecurityCommandId, replay.CommandStatus.ToString(), replayed: true, correlationId);
        }

        var graph = new DesiredStateGraph(await scope.GetActiveEdgesAsync(organizationId, cancellationToken).ConfigureAwait(false));
        var bindings = await scope.GetIdentityBindingsAsync(organizationId, cancellationToken).ConfigureAwait(false);
        var now = clock.GetUtcNow();
        var refusal = Evaluate(state, request, graph, bindings, now);
        if (input.DryRun)
        {
            return Response(true, state, request, statementJson, statementSha256, refusal, null, null, replayed: false, correlationId);
        }

        IReadOnlyList<VerifiedOperatorApproval> approvals = [];
        try
        {
            approvals = verifier.Verify(input.Approvals, statement, statementSha256, now);
            if (refusal is not null)
            {
                throw refusal;
            }

            var command = CommitApproval(scope, state, request, idempotencyKey, statementDigest, "operator membership approval: " + justification,
                binding => CanonicalJson.Serialize(new MembershipApprovalPayload(requestId, binding.AuthorityUserId, binding.IdentityIssuerUri, binding.IdentitySubjectIdentifier, request.RequestedInitialRoleName,
                    request.RequestedByAuthorityUserId, justification, operatorName, verifier.Assurance, statementSha256, approvals.Select(a => new ApprovalRecord(a.ApproverId, a.ApprovalSha256, a.IssuedOn, a.ExpiresOn)).ToList())),
                new VerifiedActor("authority-operator-plane", operatorName, operatorName), operatorName, verifier.Assurance, approvals.Select(a => new { approver_id = a.ApproverId, approval_sha256 = a.ApprovalSha256 }).ToList(), statementSha256,
                correlationId, now, clock, cancellationToken);
            // C8 M2 Stage 2: a pending Platform Operator approval of this request can never complete it any more.
            var superseded = await PlatformApprovalLedger.InvalidatePendingAsync(scope, requestId, PlatformApprovalClosureReasons.RequestDecidedElsewhere, now, cancellationToken).ConfigureAwait(false);
            AppendInvalidations(scope, state, superseded, PlatformApprovalClosureReasons.RequestDecidedElsewhere, correlationId, clock);
            await scope.CommitAsync(cancellationToken).ConfigureAwait(false);
            var status = await SecurityCommandService.ToStatusAsync(scope, state, command, cancellationToken).ConfigureAwait(false);
            return Response(false, state, request, statementJson, statementSha256, null, command.SecurityCommandId, status.CommandStatus.ToString(), replayed: false, correlationId);
        }
        catch (AuthorityException ex)
        {
            if (ex.ErrorCode == AuthorityErrorCodes.MembershipRequestExpired && request.RequestStateName == MembershipRequestStates.Pending)
            {
                await RecordExpiredAsync(scope, state, request, now, correlationId, clock, cancellationToken).ConfigureAwait(false);
            }

            AuditJournal.Append(scope, state, "OrganizationMembershipApprovalRejected", new
            {
                membership_request_id = requestId,
                organization_id = organizationId,
                operator_workload = operatorName,
                approval_assurance = verifier.Assurance,
                verified_approvers = approvals.Select(a => a.ApproverId),
                rejection_reason = ex.ErrorCode,
                correlation_id = correlationId,
            }, clock);
            await scope.CommitAsync(cancellationToken).ConfigureAwait(false);
            throw;
        }
    }

    public async Task<OrganizationMembershipRequestEntry> RejectAsync(Guid organizationId, Guid requestId, MembershipRejectionRequest input, string operatorName, Guid correlationId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (input.ConfirmOrganizationId != organizationId)
        {
            throw AuthorityException.Invalid("confirm_organization_id must equal the organization in the path");
        }

        var reason = input.Reason?.Trim() ?? string.Empty;
        if (reason.Length is < 3 or > 500)
        {
            throw AuthorityException.Invalid("a reason of 3 to 500 characters is required");
        }

        await using var scope = await store.BeginScopeAsync(cancellationToken).ConfigureAwait(false);
        var state = await scope.LockOrganizationAsync(organizationId, cancellationToken).ConfigureAwait(false)
                    ?? throw new AuthorityException(AuthorityErrorCodes.NotFound, 404, false);
        var request = await scope.FindMembershipRequestAsync(organizationId, requestId, cancellationToken).ConfigureAwait(false)
                      ?? throw new AuthorityException(AuthorityErrorCodes.NotFound, 404, false);
        if (request.RequestStateName != MembershipRequestStates.Pending)
        {
            throw new AuthorityException(AuthorityErrorCodes.MembershipRequestNotPending, 409, false, "the request was already decided");
        }

        var now = clock.GetUtcNow();
        request.RequestStateName = MembershipRequestStates.Rejected;
        request.RequestDecidedOn = now;
        request.RequestDecidedByOperatorName = operatorName;
        request.RequestDecisionReasonText = reason;
        AuditJournal.Append(scope, state, "OrganizationMembershipRejected", new { membership_request_id = requestId, organization_id = organizationId, operator_workload = operatorName, reason, correlation_id = correlationId }, clock);
        var superseded = await PlatformApprovalLedger.InvalidatePendingAsync(scope, requestId, PlatformApprovalClosureReasons.RequestRejected, now, cancellationToken).ConfigureAwait(false);
        AppendInvalidations(scope, state, superseded, PlatformApprovalClosureReasons.RequestRejected, correlationId, clock);
        await scope.CommitAsync(cancellationToken).ConfigureAwait(false);
        return OrganizationRosterService.ToEntry(request, now);
    }

    /// <summary>Preconditions on the Authority's current state (inside the organization lock when executing). Null when eligible.</summary>
    internal static AuthorityException? Evaluate(Domain.Features.Organizations.OrganizationAuthorizationState state, OrganizationMembershipRequest request, DesiredStateGraph graph, IReadOnlyList<IdentityBinding> bindings, DateTimeOffset now)
    {
        if (state.IntegrityQuarantineEnabled)
        {
            return AuthorityException.Quarantined("organization quarantined");
        }

        if (request.RequestStateName != MembershipRequestStates.Pending)
        {
            return new AuthorityException(AuthorityErrorCodes.MembershipRequestNotPending, 409, false, $"the request is {request.RequestStateName}");
        }

        if (request.RequestExpiresOn <= now)
        {
            return new AuthorityException(AuthorityErrorCodes.MembershipRequestExpired, 409, false, "the request expired; the owner must request the membership again");
        }

        var requester = bindings.FirstOrDefault(b => b.AuthorityUserId == request.RequestedByAuthorityUserId);
        if (requester is null || !requester.IdentityBindingEnabled || !graph.HasOrganizationRole(state.OrganizationId, requester.AuthorityUserId, OrganizationRoleNames.OrganizationOwner))
        {
            return new AuthorityException(AuthorityErrorCodes.OwnerApprovalRequired, 403, false, "the requesting owner is no longer an enabled owner");
        }

        if (bindings.Any(b => (b.IdentityIssuerUri == request.RequestedIssuerUri && b.IdentitySubjectIdentifier == request.RequestedSubjectIdentifier) || b.AuthorityUserId == request.RequestedAuthorityUserId))
        {
            return new AuthorityException(AuthorityErrorCodes.IdentityAlreadyBound, 409, false, "this identity is already a member of the organization");
        }

        if (!string.Equals(CommandPlanner.OrganizationIdentityIssuer(graph, state.OrganizationId, bindings), request.RequestedIssuerUri, StringComparison.Ordinal))
        {
            return new AuthorityException(AuthorityErrorCodes.IdentityNotAllowed, 422, false, "the identity no longer matches the organization's configured identity provider tenant");
        }

        return null;
    }

    /// <summary>
    /// Creates the membership (identity binding plus the optional designer edge) as one journaled security command and records
    /// <c>OrganizationMembershipApproved</c>. Shared by the legacy operator-plane approval and the two-operator platform approval
    /// (C8 M2 Stage 2): the idempotency key <c>membership-approve:{request}</c> is the same for both, so a request is approved at most
    /// once. The caller has verified the approvals and <see cref="Evaluate"/> inside the organization lock, and commits the scope.
    /// </summary>
    internal static SecurityCommand CommitApproval(IAuthorityScope scope, Domain.Features.Organizations.OrganizationAuthorizationState state, OrganizationMembershipRequest request, string idempotencyKey, byte[] requestDigest,
        string reasonText, Func<IdentityBinding, string> payloadJson, VerifiedActor actorIdentity, string operatorName, string assurance, object approversForAudit, string statementSha256, Guid correlationId, DateTimeOffset now,
        TimeProvider clock, CancellationToken cancellationToken)
    {
        var binding = new IdentityBinding
        {
            IdentityBindingId = Guid.CreateVersion7(),
            OrganizationId = state.OrganizationId,
            AuthorityUserId = request.RequestedAuthorityUserId,
            IdentityIssuerUri = request.RequestedIssuerUri,
            IdentitySubjectIdentifier = request.RequestedSubjectIdentifier,
            IdentityBindingEnabled = true,
            IdentityCreatedOn = now,
            OrganizationMembershipRequestId = request.OrganizationMembershipRequestId,
        };
        var plan = new CommandPlan { NewBinding = binding, ApprovedMembershipRequest = request };
        if (request.RequestedInitialRoleName == OrganizationRoleNames.Designer)
        {
            plan.AddEdges.Add((ResourceTypeNames.Organization, state.OrganizationId, OrganizationRoleNames.Designer, ResourceTypeNames.User, binding.AuthorityUserId, string.Empty));
        }

        plan.NormalizedPayloadJson = payloadJson(binding);
        var command = new SecurityCommand
        {
            SecurityCommandId = Guid.CreateVersion7(),
            OrganizationId = state.OrganizationId,
            // As for operator appointments: the operator plane has no organization identity; the actor binding is the new member's.
            ActorIdentityBindingId = binding.IdentityBindingId,
            CommandKindName = "ApproveOrganizationMembership",
            CommandIdempotencyKey = idempotencyKey,
            CommandRequestDigest = requestDigest,
            ExpectedAuthorizationRevision = state.AuthorizationRevision,
            CommandPayloadJson = plan.NormalizedPayloadJson,
            CommandReasonText = reasonText.Length <= 1000 ? reasonText : reasonText[..1000],
            CorrelationIdentifier = correlationId,
            CommandCreatedOn = now,
            CommandModifiedOn = now,
        };
        var actor = new BoundActor(actorIdentity, binding);
        var commandRequest = new SecurityCommandRequest(state.OrganizationId, SecurityCommandKind.RequestOrganizationMembership, new ResourceRef(ResourceKind.Organization, state.OrganizationId), state.AuthorizationRevision,
            JsonDocument.Parse(plan.NormalizedPayloadJson).RootElement, command.CommandReasonText);
        SecurityCommandService.ApplyPlan(scope, state, command, plan, actor, commandRequest, cancellationToken);
        AuditJournal.Append(scope, state, "OrganizationMembershipApproved", new
        {
            security_command_id = command.SecurityCommandId,
            membership_request_id = request.OrganizationMembershipRequestId,
            organization_id = state.OrganizationId,
            new_authority_user_id = binding.AuthorityUserId,
            new_identity_binding_id = binding.IdentityBindingId,
            issuer_uri = binding.IdentityIssuerUri,
            subject_identifier = binding.IdentitySubjectIdentifier,
            initial_role_name = request.RequestedInitialRoleName,
            requested_by_authority_user_id = request.RequestedByAuthorityUserId,
            operator_workload = operatorName,
            approval_assurance = assurance,
            approvers = approversForAudit,
            statement_sha256 = statementSha256,
            authorization_revision = state.AuthorizationRevision,
            correlation_id = correlationId,
        }, clock);
        return command;
    }

    /// <summary>Records a Pending request as Expired (found too old by a decision) and closes its pending operator approvals.</summary>
    internal static async Task RecordExpiredAsync(IAuthorityScope scope, Domain.Features.Organizations.OrganizationAuthorizationState state, OrganizationMembershipRequest request, DateTimeOffset now, Guid correlationId, TimeProvider clock, CancellationToken cancellationToken)
    {
        request.RequestStateName = MembershipRequestStates.Expired;
        request.RequestDecidedOn = now;
        request.RequestDecisionReasonText = "expired before operator approval";
        var closed = await PlatformApprovalLedger.InvalidatePendingAsync(scope, request.OrganizationMembershipRequestId, PlatformApprovalClosureReasons.RequestExpired, now, cancellationToken).ConfigureAwait(false);
        AppendInvalidations(scope, state, closed, PlatformApprovalClosureReasons.RequestExpired, correlationId, clock);
    }

    internal static void AppendInvalidations(IAuthorityScope scope, Domain.Features.Organizations.OrganizationAuthorizationState state, IReadOnlyList<PlatformOperatorApproval> closed, string reason, Guid correlationId, TimeProvider clock)
    {
        if (closed.Count == 0)
        {
            return;
        }

        AuditJournal.Append(scope, state, "PlatformOperatorApprovalsInvalidated", new
        {
            approvals = closed.Select(a => new { platform_operator_approval_id = a.PlatformOperatorApprovalId, membership_request_id = a.OrganizationMembershipRequestId, platform_operator_id = a.PlatformOperatorId, closure_reason = reason }),
            correlation_id = correlationId,
        }, clock);
    }

    /// <summary>The consequences shown before any approval (legacy dry run and platform detail).</summary>
    internal static List<string> Consequences(Domain.Features.Organizations.OrganizationAuthorizationState state, OrganizationMembershipRequest request, string approvalRequirement) =>
    [
        $"{request.RequestedSubjectIdentifier} ({request.RequestedIssuerUri}) becomes a member of \"{state.OrganizationDisplayName}\" with {(request.RequestedInitialRoleName is null ? "no organization role" : "the designer role")}.",
        "Membership is not sign-in: the person can sign in only through the organization's identity provider once the Entra integration (PB-08) exists.",
        "No organization owner, Security Administrator, record, share or team access is granted.",
        $"Requested by owner {request.RequestedByAuthorityUserId}; expires {request.RequestExpiresOn:O}.",
        approvalRequirement,
    ];

    private MembershipApprovalResponse Response(bool dryRun, Domain.Features.Organizations.OrganizationAuthorizationState state, OrganizationMembershipRequest request, string statementJson, string statementSha256,
        AuthorityException? refusal, Guid? commandId, string? commandStatus, bool replayed, Guid correlationId)
    {
        var target = new OperatorOrganizationPrincipal(request.RequestedAuthorityUserId, request.RequestedIssuerUri, request.RequestedSubjectIdentifier, commandId is not null, false, null);
        var consequences = Consequences(state, request, $"Requires {OperatorApprovalVerifier.RequiredApprovals} approvals from distinct registered approvers ({verifier.Assurance}).");
        return new MembershipApprovalResponse(dryRun, refusal is null, refusal?.ErrorCode, refusal?.Detail, request.OrganizationMembershipRequestId, ApprovalKind, state.OrganizationId, state.OrganizationDisplayName, target,
            request.RequestedInitialRoleName, request.RequestStateName, consequences, statementJson, statementSha256, verifier.Mode, verifier.Assurance, OperatorApprovalVerifier.RequiredApprovals, commandId, commandStatus, replayed,
            state.AuthorizationRevision, correlationId);
    }

    private sealed record ApprovalRecord(string ApproverId, string ApprovalSha256, DateTimeOffset IssuedOn, DateTimeOffset ExpiresOn);

    private sealed record MembershipApprovalPayload(Guid MembershipRequestId, Guid NewAuthorityUserId, string IssuerUri, string SubjectIdentifier, string? InitialRoleName, Guid RequestedByAuthorityUserId,
        string Justification, string OperatorWorkload, string ApprovalAssurance, string StatementSha256, IReadOnlyList<ApprovalRecord> Approvals);
}
