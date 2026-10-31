using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AuthorizationAuthority.Application.Common;
using AuthorizationAuthority.Application.Common.Ports;
using AuthorizationAuthority.Application.Features.Journal;
using AuthorizationAuthority.Application.Features.Membership;
using AuthorizationAuthority.Application.Features.SecurityCommands;
using AuthorizationAuthority.Contracts.V1;
using AuthorizationAuthority.Domain.Features.Journal;
using AuthorizationAuthority.Domain.Features.Organizations;
using AuthorizationAuthority.Domain.Features.PlatformOperators;

namespace AuthorizationAuthority.Application.Features.PlatformOperators;

/// <summary>
/// C8 M2 Stage 2 (D8-36…D8-41): two-person membership approval by independently authenticated Platform Operators.
/// <para>
/// Each operator submits <b>one</b> approval: their own evidence over the identical statement v2 and their own justification.
/// The first valid approval is stored as pending evidence (grants nothing; 24 hours, never beyond the request's expiry). The
/// membership is created — atomically, as the same journaled security command the legacy path uses — only when a second valid
/// approval arrives from a <b>different</b> operator while the first is still valid and its operator still active, inside the
/// organization lock and the platform journal lock (in that order). Same-operator double approval, another operator's evidence,
/// replayed or expired evidence, a changed request, conflict of interest and every Evaluate precondition are refused, and every
/// refusal by an authenticated operator is journaled. Rejection is one operator; withdrawal is the approval's own operator only.
/// </para>
/// </summary>
public sealed class PlatformMembershipApprovalService(IAuthorityStore store, PlatformOperatorPolicy policy, PlatformOperatorEvidenceVerifier evidenceVerifier, PlatformJournal journal, PlatformOperatorRegistryService registry, TimeProvider clock)
{
    public const int StatementVersion = 2;
    public const int DefaultPageSize = 15;
    public const int MaximumPageSize = 50;
    private static readonly byte[] CursorKey = RandomNumberGenerator.GetBytes(32); // per process: a cursor never outlives the process that issued it

    // ---- reads ----

    public async Task<Contracts.V1.PlatformMembershipRequestPage> SearchAsync(VerifiedActor actor, PlatformMembershipRequestSearchRequest input, Guid correlationId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        policy.RequireOperational();
        var status = string.IsNullOrWhiteSpace(input.Status) ? PlatformRequestFilters.NeedsDecision : input.Status.Trim();
        if (!PlatformRequestFilters.Values.Contains(status))
        {
            throw AuthorityException.Invalid("unknown status filter");
        }

        var text = input.Query?.Trim();
        if (text is { Length: > 200 })
        {
            throw AuthorityException.Invalid("the search text is at most 200 characters");
        }

        var limit = input.Limit ?? DefaultPageSize;
        if (limit is < 1 or > MaximumPageSize)
        {
            throw AuthorityException.Invalid($"limit must be 1..{MaximumPageSize}");
        }

        await using var scope = await store.BeginScopeAsync(cancellationToken).ConfigureAwait(false);
        var head = await scope.ReadPlatformJournalAsync(cancellationToken).ConfigureAwait(false) ?? new PlatformJournalHead();
        await journal.EnsureCurrentAsync(scope, head, requireAnchored: false, correlationId, cancellationToken).ConfigureAwait(false);
        var me = await PlatformOperatorAccess.ResolveActiveAsync(scope, actor, cancellationToken).ConfigureAwait(false);
        var filterBinding = FilterBinding(me, status, text, limit);
        var after = input.Cursor is { Length: > 0 } cursor ? ReadCursor(cursor, filterBinding) : ((DateTimeOffset, Guid)?)null;
        var now = clock.GetUtcNow();
        var page = await scope.SearchMembershipRequestsAsync(new PlatformMembershipRequestQuery(text, status, now, after, limit + 1), cancellationToken).ConfigureAwait(false);
        var rows = page.Rows.Take(limit).ToList();
        var approvals = await scope.GetPlatformApprovalsForRequestsAsync(rows.Select(r => r.Request.OrganizationMembershipRequestId).ToList(), cancellationToken).ConfigureAwait(false);
        var operators = (await scope.GetPlatformOperatorsAsync(cancellationToken).ConfigureAwait(false)).ToDictionary(o => o.PlatformOperatorId);
        var items = new List<PlatformMembershipRequestItem>();
        foreach (var row in rows)
        {
            var requester = await scope.FindIdentityBindingByUserAsync(row.Request.OrganizationId, row.Request.RequestedByAuthorityUserId, cancellationToken).ConfigureAwait(false);
            items.Add(Item(row.Request, row.OrganizationDisplayName, requester, approvals.Where(a => a.OrganizationMembershipRequestId == row.Request.OrganizationMembershipRequestId), operators, me, now));
        }

        var next = page.Rows.Count > limit ? WriteCursor(rows[^1].Request, filterBinding) : null;
        return new Contracts.V1.PlatformMembershipRequestPage(items, page.TotalCount, next, limit, status, PlatformJournal.Status(head, await scope.CountPendingPlatformArchiveAsync(cancellationToken).ConfigureAwait(false)), policy.Assurance, correlationId);
    }

    /// <summary>Request metadata, the exact statement, recorded approvals and the caller's eligibility. The view itself is journaled (operator access is auditable).</summary>
    public async Task<PlatformMembershipRequestDetail> GetDetailAsync(VerifiedActor actor, Guid organizationId, Guid requestId, Guid correlationId, CancellationToken cancellationToken)
    {
        policy.RequireOperational();
        PlatformMembershipRequestDetail detail;
        await using (var scope = await store.BeginScopeAsync(cancellationToken).ConfigureAwait(false))
        {
            var head = await PlatformJournal.LockHeadAsync(scope, clock, cancellationToken).ConfigureAwait(false);
            await journal.EnsureCurrentAsync(scope, head, requireAnchored: false, correlationId, cancellationToken).ConfigureAwait(false);
            var me = await PlatformOperatorAccess.ResolveActiveAsync(scope, actor, cancellationToken).ConfigureAwait(false);
            var state = await scope.ReadOrganizationAsync(organizationId, cancellationToken).ConfigureAwait(false) ?? throw NotFound();
            var request = await scope.FindMembershipRequestAsync(organizationId, requestId, cancellationToken).ConfigureAwait(false) ?? throw NotFound();
            var now = clock.GetUtcNow();
            var context = await ContextAsync(scope, state, request, me, now, cancellationToken).ConfigureAwait(false);
            var operators = (await scope.GetPlatformOperatorsAsync(cancellationToken).ConfigureAwait(false)).ToDictionary(o => o.PlatformOperatorId);
            var eligibility = new PlatformEligibility(
                context.ApproveRefusal is null,
                context.RejectRefusal is null,
                context.Approvals.Any(a => a.PlatformOperatorId == me.PlatformOperatorId && a.ApprovalStateName == PlatformApprovalStates.Pending),
                context.ApproveRefusal?.ErrorCode,
                context.ApproveRefusal?.Detail);
            detail = new PlatformMembershipRequestDetail(
                Item(request, state.OrganizationDisplayName, context.Requester, context.Approvals, operators, me, now),
                request.RequestReasonText,
                request.ReplacesMembershipRequestId,
                context.StatementJson,
                context.StatementSha256,
                MembershipProvisioningService.Consequences(state, request, $"Requires {PlatformOperatorPolicy.RequiredMembershipApprovals} approvals from two different Platform Operators, each with their own evidence ({policy.Assurance})."),
                context.Approvals.Select(a => View(a, operators, me)).ToList(),
                eligibility,
                policy.Assurance,
                correlationId);
            PlatformJournal.Append(scope, head, "PlatformMembershipRequestViewed", new { platform_operator_id = me.PlatformOperatorId, organization_id = organizationId, membership_request_id = requestId, correlation_id = correlationId }, PlatformStateDelta.Of(), now);
            await scope.CommitAsync(cancellationToken).ConfigureAwait(false);
        }

        await registry.TryDeliverAsync(cancellationToken).ConfigureAwait(false);
        return detail;
    }

    // ---- decisions ----

    public Task<PlatformMembershipDecisionResponse> ApproveAsync(VerifiedActor actor, Guid organizationId, Guid requestId, PlatformMembershipDecisionRequest input, Guid correlationId, CancellationToken cancellationToken) =>
        DecideAsync(actor, organizationId, requestId, input, PlatformDecisions.Approve, correlationId, cancellationToken);

    public Task<PlatformMembershipDecisionResponse> RejectAsync(VerifiedActor actor, Guid organizationId, Guid requestId, PlatformMembershipDecisionRequest input, Guid correlationId, CancellationToken cancellationToken) =>
        DecideAsync(actor, organizationId, requestId, input, PlatformDecisions.Reject, correlationId, cancellationToken);

    public Task<PlatformMembershipDecisionResponse> WithdrawAsync(VerifiedActor actor, Guid organizationId, Guid requestId, PlatformMembershipDecisionRequest input, Guid correlationId, CancellationToken cancellationToken) =>
        DecideAsync(actor, organizationId, requestId, input, PlatformDecisions.Withdraw, correlationId, cancellationToken);

    private async Task<PlatformMembershipDecisionResponse> DecideAsync(VerifiedActor actor, Guid organizationId, Guid requestId, PlatformMembershipDecisionRequest input, string decision, Guid correlationId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        policy.RequireOperational();
        var justification = PlatformOperatorAccess.RequireJustification(input.Justification);
        var grantDirection = decision == PlatformDecisions.Approve;
        if (grantDirection)
        {
            await registry.TryDeliverAsync(cancellationToken).ConfigureAwait(false);
        }

        Func<string, PlatformMembershipDecisionResponse> respond;
        await using (var scope = await store.BeginScopeAsync(cancellationToken).ConfigureAwait(false))
        {
            // Lock order: organization state row, then the platform journal head (every path that takes both uses this order).
            var state = await scope.LockOrganizationAsync(organizationId, cancellationToken).ConfigureAwait(false) ?? throw NotFound();
            var head = await PlatformJournal.LockHeadAsync(scope, clock, cancellationToken).ConfigureAwait(false);
            await journal.EnsureCurrentAsync(scope, head, grantDirection, correlationId, cancellationToken).ConfigureAwait(false);
            var me = await PlatformOperatorAccess.ResolveActiveAsync(scope, actor, cancellationToken).ConfigureAwait(false);
            var request = await scope.FindMembershipRequestAsync(organizationId, requestId, cancellationToken).ConfigureAwait(false) ?? throw NotFound();
            var now = clock.GetUtcNow();
            var context = await ContextAsync(scope, state, request, me, now, cancellationToken).ConfigureAwait(false);
            try
            {
                var refusal = decision switch
                {
                    PlatformDecisions.Approve => context.ApproveRefusal,
                    PlatformDecisions.Reject => context.RejectRefusal,
                    _ => context.Approvals.Any(a => a.PlatformOperatorId == me.PlatformOperatorId && a.ApprovalStateName == PlatformApprovalStates.Pending)
                        ? null
                        : new AuthorityException(AuthorityErrorCodes.NoPendingApproval, 409, false, "you have no pending approval of this request"),
                };
                if (input.DryRun)
                {
                    return Response(true, request, decision, context, refusal, "Preview", context.ValidPending.Count, null, null, null, state.AuthorizationRevision, head, ArchiveDeliveryStates.Anchored, correlationId);
                }

                if (!string.Equals(input.ExpectedStatementSha256?.Trim(), context.StatementSha256, StringComparison.OrdinalIgnoreCase))
                {
                    throw new AuthorityException(AuthorityErrorCodes.ApprovalStatementChanged, 409, false, "the request changed since it was reviewed; review it again");
                }

                if (refusal is not null)
                {
                    if (refusal.ErrorCode == AuthorityErrorCodes.MembershipRequestExpired && request.RequestStateName == MembershipRequestStates.Pending)
                    {
                        await MembershipProvisioningService.RecordExpiredAsync(scope, state, request, now, correlationId, clock, cancellationToken).ConfigureAwait(false);
                    }

                    throw refusal;
                }

                var evidence = evidenceVerifier.Verify(me, input.EvidenceKind, input.Evidence, new OperatorEvidenceBinding(decision, PlatformOperationKinds.MembershipApproval, requestId, organizationId, context.StatementSha256,
                    PlatformOperatorEvidenceVerifier.JustificationSha256(justification)), now);
                await PlatformOperatorAccess.RequireUnusedAsync(scope, evidence, cancellationToken).ConfigureAwait(false);
                var use = PlatformOperatorAccess.EvidenceUse(evidence, me.PlatformOperatorId, decision, now);
                scope.AddPlatformEvidenceUse(use);
                CloseStale(context, now);
                if (context.StaleWithReason.Count > 0)
                {
                    await scope.FlushAsync(cancellationToken).ConfigureAwait(false); // a stale Pending row leaves Pending before a new one is inserted
                }

                respond = decision switch
                {
                    PlatformDecisions.Approve => await ApproveCoreAsync(scope, state, head, request, me, context, evidence, use, justification, correlationId, now, cancellationToken).ConfigureAwait(false),
                    PlatformDecisions.Reject => Reject(scope, state, head, request, me, context, evidence, use, justification, correlationId, now),
                    _ => Withdraw(scope, state, head, request, me, context, evidence, use, justification, correlationId, now),
                };
            }
            catch (AuthorityException ex)
            {
                CloseStale(context, now);
                AuditJournal.Append(scope, state, "PlatformOperatorDecisionRefused", new
                {
                    platform_operator_id = me.PlatformOperatorId,
                    decision,
                    membership_request_id = requestId,
                    refusal_code = ex.ErrorCode,
                    correlation_id = correlationId,
                }, clock);
                PlatformJournal.Append(scope, head, "PlatformOperatorDecisionRefused", new
                {
                    platform_operator_id = me.PlatformOperatorId,
                    decision,
                    operation_kind = PlatformOperationKinds.MembershipApproval,
                    operation_id = requestId,
                    organization_id = organizationId,
                    refusal_code = ex.ErrorCode,
                    correlation_id = correlationId,
                }, PlatformStateDelta.Of(approvals: context.Stale), now);
                await scope.CommitAsync(cancellationToken).ConfigureAwait(false);
                throw;
            }

            await scope.CommitAsync(cancellationToken).ConfigureAwait(false);
        }

        return respond(await registry.TryDeliverAsync(cancellationToken).ConfigureAwait(false));
    }

    private async Task<Func<string, PlatformMembershipDecisionResponse>> ApproveCoreAsync(IAuthorityScope scope, OrganizationAuthorizationState state, PlatformJournalHead head, OrganizationMembershipRequest request, PlatformOperator me,
        DecisionContext context, VerifiedOperatorEvidence evidence, PlatformOperatorEvidenceUse use, string justification, Guid correlationId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var first = context.ValidPending.SingleOrDefault();
        var firstOperator = first is null ? null : await scope.FindPlatformOperatorAsync(first.PlatformOperatorId, cancellationToken).ConfigureAwait(false) ?? throw AuthorityException.NotReady("approving operator vanished");
        var mine = new PlatformOperatorApproval
        {
            PlatformOperatorApprovalId = Guid.CreateVersion7(),
            OrganizationId = state.OrganizationId,
            OrganizationMembershipRequestId = request.OrganizationMembershipRequestId,
            PlatformOperatorId = me.PlatformOperatorId,
            ApprovalStatementSha256 = Convert.FromHexString(context.StatementSha256),
            ApprovalStatementVersion = StatementVersion,
            ApprovalJustificationText = justification,
            ApprovalEvidenceKindName = evidence.EvidenceKind,
            ApprovalEvidenceSha256 = evidence.EvidenceSha256,
            ApprovalRecordedOn = now,
            ApprovalValidUntil = Min(now + policy.PendingApprovalWindow, request.RequestExpiresOn),
            ApprovalStateName = PlatformApprovalStates.Pending,
        };
        if (first is null || firstOperator is null)
        {
            scope.AddPlatformApproval(mine);
            PlatformJournal.Append(scope, head, "PlatformMembershipApprovalRecorded", Detail(me, PlatformDecisions.Approve, request, context, justification, evidence, new { platform_operator_approval_id = mine.PlatformOperatorApprovalId, approval_valid_until = mine.ApprovalValidUntil }),
                PlatformStateDelta.Of(approvals: [mine, .. context.Stale], evidenceUses: [use]), now);
            AuditJournal.Append(scope, state, "PlatformOperatorApprovalRecorded", new { platform_operator_approval_id = mine.PlatformOperatorApprovalId, membership_request_id = request.OrganizationMembershipRequestId, platform_operator_id = me.PlatformOperatorId, correlation_id = correlationId }, clock);
            return archive => Response(false, request, PlatformDecisions.Approve, context, null, "AwaitingSecondApproval", 1, mine.PlatformOperatorApprovalId, null, null, state.AuthorizationRevision, head, archive, correlationId);
        }

        // Second, different operator: create the membership now, as one security command, and consume both approvals. The completing
        // approval is never stored as Pending (one Pending approval per request is a unique index).
        var records = new[] { (Approval: first, Operator: firstOperator), (Approval: mine, Operator: me) }
            .Select(r => new PlatformApprovalRecord(r.Approval.PlatformOperatorApprovalId, r.Operator.PlatformOperatorId, r.Operator.OperatorIssuerUri, r.Operator.OperatorSubjectIdentifier, r.Approval.ApprovalJustificationText,
                r.Approval.ApprovalEvidenceKindName, Convert.ToHexStringLower(r.Approval.ApprovalEvidenceSha256), r.Approval.ApprovalRecordedOn)).ToList();
        var command = MembershipProvisioningService.CommitApproval(scope, state, request, "membership-approve:" + request.OrganizationMembershipRequestId.ToString("N"), Convert.FromHexString(context.StatementSha256),
            $"two platform operator approvals: {firstOperator.OperatorDisplayName} and {me.OperatorDisplayName}",
            binding => CanonicalJson.Serialize(new PlatformMembershipApprovalPayload(request.OrganizationMembershipRequestId, binding.AuthorityUserId, binding.IdentityIssuerUri, binding.IdentitySubjectIdentifier, request.RequestedInitialRoleName,
                request.RequestedByAuthorityUserId, StatementVersion, context.StatementSha256, policy.Assurance, records)),
            new VerifiedActor(me.OperatorIssuerUri, me.OperatorSubjectIdentifier, "platform-console"), "platform-operators", policy.Assurance,
            records.Select(r => new { platform_operator_id = r.PlatformOperatorId, platform_operator_approval_id = r.PlatformOperatorApprovalId, evidence_sha256 = r.EvidenceSha256 }).ToList(), context.StatementSha256, correlationId, now, clock, cancellationToken);
        PlatformApprovalLedger.Close(first, PlatformApprovalStates.Consumed, PlatformApprovalClosureReasons.ConsumedByMembership, now, command.SecurityCommandId);
        PlatformApprovalLedger.Close(mine, PlatformApprovalStates.Consumed, PlatformApprovalClosureReasons.ConsumedByMembership, now, command.SecurityCommandId);
        scope.AddPlatformApproval(mine);
        request.RequestDecidedByPlatformOperatorId = me.PlatformOperatorId;
        request.RequestDecidedByOperatorName = "platform-operator:" + me.PlatformOperatorId.ToString("D");
        PlatformJournal.Append(scope, head, "PlatformMembershipApprovalCompleted", Detail(me, PlatformDecisions.Approve, request, context, justification, evidence,
                new { security_command_id = command.SecurityCommandId, first_platform_operator_id = first.PlatformOperatorId, platform_operator_approval_ids = records.Select(r => r.PlatformOperatorApprovalId) }),
            PlatformStateDelta.Of(approvals: [first, mine, .. context.Stale], evidenceUses: [use]), now);
        var status = await SecurityCommandService.ToStatusAsync(scope, state, command, cancellationToken).ConfigureAwait(false);
        return archive => Response(false, request, PlatformDecisions.Approve, context, null, "Approved", 2, mine.PlatformOperatorApprovalId, command.SecurityCommandId, status.CommandStatus.ToString(), state.AuthorizationRevision, head, archive, correlationId);
    }

    private Func<string, PlatformMembershipDecisionResponse> Reject(IAuthorityScope scope, OrganizationAuthorizationState state, PlatformJournalHead head, OrganizationMembershipRequest request, PlatformOperator me, DecisionContext context,
        VerifiedOperatorEvidence evidence, PlatformOperatorEvidenceUse use, string justification, Guid correlationId, DateTimeOffset now)
    {
        request.RequestStateName = MembershipRequestStates.Rejected;
        request.RequestDecidedOn = now;
        request.RequestDecidedByPlatformOperatorId = me.PlatformOperatorId;
        request.RequestDecidedByOperatorName = "platform-operator:" + me.PlatformOperatorId.ToString("D");
        request.RequestDecisionReasonText = justification;
        var closed = context.ValidPending.ToList();
        foreach (var approval in closed)
        {
            PlatformApprovalLedger.Close(approval, PlatformApprovalStates.Invalidated, PlatformApprovalClosureReasons.RequestRejected, now);
        }

        AuditJournal.Append(scope, state, "OrganizationMembershipRejected", new { membership_request_id = request.OrganizationMembershipRequestId, organization_id = state.OrganizationId, platform_operator_id = me.PlatformOperatorId, reason = justification, evidence_sha256 = Convert.ToHexStringLower(evidence.EvidenceSha256), correlation_id = correlationId }, clock);
        MembershipProvisioningService.AppendInvalidations(scope, state, closed, PlatformApprovalClosureReasons.RequestRejected, correlationId, clock);
        PlatformJournal.Append(scope, head, "PlatformMembershipRequestRejected", Detail(me, PlatformDecisions.Reject, request, context, justification, evidence, new { invalidated_approvals = closed.Select(a => a.PlatformOperatorApprovalId) }),
            PlatformStateDelta.Of(approvals: [.. closed, .. context.Stale], evidenceUses: [use]), now);
        return archive => Response(false, request, PlatformDecisions.Reject, context, null, "Rejected", 0, null, null, null, state.AuthorizationRevision, head, archive, correlationId);
    }

    private Func<string, PlatformMembershipDecisionResponse> Withdraw(IAuthorityScope scope, OrganizationAuthorizationState state, PlatformJournalHead head, OrganizationMembershipRequest request, PlatformOperator me, DecisionContext context,
        VerifiedOperatorEvidence evidence, PlatformOperatorEvidenceUse use, string justification, Guid correlationId, DateTimeOffset now)
    {
        var mine = context.Approvals.Where(a => a.PlatformOperatorId == me.PlatformOperatorId && a.ApprovalStateName == PlatformApprovalStates.Pending).ToList();
        foreach (var approval in mine)
        {
            PlatformApprovalLedger.Close(approval, PlatformApprovalStates.Withdrawn, PlatformApprovalClosureReasons.WithdrawnByOperator, now);
        }

        AuditJournal.Append(scope, state, "PlatformOperatorApprovalWithdrawn", new { membership_request_id = request.OrganizationMembershipRequestId, platform_operator_id = me.PlatformOperatorId, approvals = mine.Select(a => a.PlatformOperatorApprovalId), correlation_id = correlationId }, clock);
        PlatformJournal.Append(scope, head, "PlatformMembershipApprovalWithdrawn", Detail(me, PlatformDecisions.Withdraw, request, context, justification, evidence, new { withdrawn_approvals = mine.Select(a => a.PlatformOperatorApprovalId) }),
            PlatformStateDelta.Of(approvals: [.. mine, .. context.Stale.Except(mine)], evidenceUses: [use]), now);
        var remaining = context.ValidPending.Count(a => a.PlatformOperatorId != me.PlatformOperatorId);
        return archive => Response(false, request, PlatformDecisions.Withdraw, context, null, "Withdrawn", remaining, mine.FirstOrDefault()?.PlatformOperatorApprovalId, null, null, state.AuthorizationRevision, head, archive, correlationId);
    }

    // ---- shared evaluation ----

    /// <summary>Everything a decision on one request depends on, read inside the caller's locks; nothing here mutates.</summary>
    private sealed record DecisionContext(
        string StatementJson,
        string StatementSha256,
        IdentityBinding? Requester,
        List<PlatformOperatorApproval> Approvals,
        List<PlatformOperatorApproval> ValidPending,
        List<(PlatformOperatorApproval Approval, string Reason)> StaleWithReason,
        AuthorityException? ApproveRefusal,
        AuthorityException? RejectRefusal)
    {
        public List<PlatformOperatorApproval> Stale => StaleWithReason.Select(s => s.Approval).ToList();
    }

    private static async Task<DecisionContext> ContextAsync(IAuthorityScope scope, OrganizationAuthorizationState state, OrganizationMembershipRequest request, PlatformOperator me, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var requester = await scope.FindIdentityBindingByUserAsync(state.OrganizationId, request.RequestedByAuthorityUserId, cancellationToken).ConfigureAwait(false);
        var statement = new MembershipApprovalStatementV2(StatementVersion, PlatformOperationKinds.MembershipApproval, request.OrganizationMembershipRequestId, state.OrganizationId, state.OrganizationDisplayName,
            request.RequestedAuthorityUserId, request.RequestedIssuerUri, request.RequestedSubjectIdentifier, request.RequestedDisplayNameHint, request.RequestedInitialRoleName, request.RequestedByAuthorityUserId,
            requester?.IdentityIssuerUri ?? string.Empty, requester?.IdentitySubjectIdentifier ?? string.Empty, Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(request.RequestReasonText))),
            request.RequestCreatedOn, request.RequestExpiresOn, MembershipRequestDigest.Compute(request), request.ReplacesMembershipRequestId);
        var statementJson = CanonicalJson.Serialize(statement);
        var statementSha256 = Convert.ToHexStringLower(CanonicalJson.Digest(statementJson));

        var approvals = await scope.GetPlatformApprovalsForRequestAsync(request.OrganizationMembershipRequestId, cancellationToken).ConfigureAwait(false);
        var operators = (await scope.GetPlatformOperatorsAsync(cancellationToken).ConfigureAwait(false)).ToDictionary(o => o.PlatformOperatorId);
        var valid = new List<PlatformOperatorApproval>();
        var stale = new List<(PlatformOperatorApproval, string)>();
        foreach (var approval in approvals.Where(a => a.ApprovalStateName == PlatformApprovalStates.Pending))
        {
            var approver = operators.GetValueOrDefault(approval.PlatformOperatorId);
            if (approval.ApprovalValidUntil <= now)
            {
                stale.Add((approval, PlatformApprovalClosureReasons.PendingApprovalExpired));
            }
            else if (approver is null || approver.OperatorStatusName != PlatformOperatorStates.Active)
            {
                stale.Add((approval, approver?.OperatorStatusName == PlatformOperatorStates.Removed ? PlatformApprovalClosureReasons.OperatorRemoved : PlatformApprovalClosureReasons.OperatorSuspended));
            }
            else if (!approval.ApprovalStatementSha256.AsSpan().SequenceEqual(Convert.FromHexString(statementSha256)))
            {
                stale.Add((approval, PlatformApprovalClosureReasons.RequestChanged));
            }
            else
            {
                valid.Add(approval);
            }
        }

        var graph = new DesiredStateGraph(await scope.GetActiveEdgesAsync(state.OrganizationId, cancellationToken).ConfigureAwait(false));
        var bindings = await scope.GetIdentityBindingsAsync(state.OrganizationId, cancellationToken).ConfigureAwait(false);
        var conflict = ConflictOfInterest(me, request, bindings);
        var requesterMissing = requester is null ? new AuthorityException(AuthorityErrorCodes.OwnerApprovalRequired, 403, false, "the requesting owner is not a member of the organization") : null;
        var approveRefusal = conflict
                             ?? requesterMissing
                             ?? MembershipProvisioningService.Evaluate(state, request, graph, bindings, now)
                             ?? (valid.Any(a => a.PlatformOperatorId == me.PlatformOperatorId) ? new AuthorityException(AuthorityErrorCodes.OperatorAlreadyApproved, 409, false, "you already approved this request; a different operator must give the second approval") : null);
        var rejectRefusal = conflict
                            ?? (state.IntegrityQuarantineEnabled ? AuthorityException.Quarantined("organization quarantined") : null)
                            ?? (request.RequestStateName != MembershipRequestStates.Pending ? new AuthorityException(AuthorityErrorCodes.MembershipRequestNotPending, 409, false, $"the request is {request.RequestStateName}") : null)
                            ?? (request.RequestExpiresOn <= now ? new AuthorityException(AuthorityErrorCodes.MembershipRequestExpired, 409, false, "the request expired") : null);
        return new DecisionContext(statementJson, statementSha256, requester, approvals, valid, stale, approveRefusal, rejectRefusal);
    }

    /// <summary>
    /// D8-40: an operator never decides a request of an organization in which their own identity is bound (any binding, enabled or
    /// not), that they requested, or for which they are the proposed member.
    /// </summary>
    private static AuthorityException? ConflictOfInterest(PlatformOperator me, OrganizationMembershipRequest request, IReadOnlyList<IdentityBinding> bindings)
    {
        var bound = bindings.FirstOrDefault(b => b.IdentityIssuerUri == me.OperatorIssuerUri && b.IdentitySubjectIdentifier == me.OperatorSubjectIdentifier);
        if (bound is not null)
        {
            return new AuthorityException(AuthorityErrorCodes.OperatorConflictOfInterest, 403, false, bound.AuthorityUserId == request.RequestedByAuthorityUserId
                ? "you requested this membership"
                : "you belong to this organization, so another operator must decide its requests");
        }

        return request.RequestedIssuerUri == me.OperatorIssuerUri && request.RequestedSubjectIdentifier == me.OperatorSubjectIdentifier
            ? new AuthorityException(AuthorityErrorCodes.OperatorConflictOfInterest, 403, false, "you are the proposed member")
            : null;
    }

    private static void CloseStale(DecisionContext context, DateTimeOffset now)
    {
        foreach (var (approval, reason) in context.StaleWithReason.Where(s => s.Approval.ApprovalStateName == PlatformApprovalStates.Pending))
        {
            PlatformApprovalLedger.Close(approval, PlatformApprovalStates.Invalidated, reason, now);
        }
    }

    private PlatformMembershipDecisionResponse Response(bool dryRun, OrganizationMembershipRequest request, string decision, DecisionContext context, AuthorityException? refusal, string outcome, int recorded, Guid? myApprovalId,
        Guid? commandId, string? commandStatus, long authorizationRevision, PlatformJournalHead head, string archiveState, Guid correlationId) =>
        new(dryRun, refusal is null, refusal?.ErrorCode, refusal?.Detail, request.OrganizationMembershipRequestId, request.OrganizationId, decision, context.StatementJson, context.StatementSha256, outcome, recorded,
            PlatformOperatorPolicy.RequiredMembershipApprovals, myApprovalId, commandId, commandStatus, authorizationRevision, head.PlatformJournalRevision, archiveState, policy.Assurance, correlationId);

    private static PlatformMembershipRequestItem Item(OrganizationMembershipRequest r, string organizationName, IdentityBinding? requester, IEnumerable<PlatformOperatorApproval> approvals, Dictionary<Guid, PlatformOperator> operators,
        PlatformOperator me, DateTimeOffset now)
    {
        var valid = approvals.Where(a => a.ApprovalStateName == PlatformApprovalStates.Pending && a.ApprovalValidUntil > now
                                         && operators.TryGetValue(a.PlatformOperatorId, out var o) && o.OperatorStatusName == PlatformOperatorStates.Active).ToList();
        var consumed = approvals.Count(a => a.ApprovalStateName == PlatformApprovalStates.Consumed);
        var mine = valid.Any(a => a.PlatformOperatorId == me.PlatformOperatorId);
        var open = r.RequestStateName == MembershipRequestStates.Pending && r.RequestExpiresOn > now;
        var state = r.RequestStateName == MembershipRequestStates.Pending && !open ? MembershipRequestStates.Expired : r.RequestStateName;
        var actions = new List<string> { "review_details" };
        if (open && !mine)
        {
            actions.AddRange(["approve", "reject"]);
        }
        else if (open)
        {
            actions.AddRange(["withdraw_approval", "reject"]);
        }

        return new PlatformMembershipRequestItem(r.OrganizationMembershipRequestId, r.OrganizationId, organizationName, r.RequestedDisplayNameHint, new PlatformIdentity(r.RequestedIssuerUri, r.RequestedSubjectIdentifier), r.RequestedInitialRoleName,
            r.RequestedByAuthorityUserId, requester is null ? null : new PlatformIdentity(requester.IdentityIssuerUri, requester.IdentitySubjectIdentifier), r.RequestCreatedOn, r.RequestExpiresOn, state,
            new PlatformApprovalStatus(r.RequestStateName == MembershipRequestStates.Approved ? Math.Max(consumed, PlatformOperatorPolicy.RequiredMembershipApprovals) : open ? valid.Count : 0, PlatformOperatorPolicy.RequiredMembershipApprovals, mine,
                open && valid.Count == 1 && mine, valid.MinBy(a => a.ApprovalValidUntil)?.ApprovalValidUntil),
            actions);
    }

    private static PlatformApprovalView View(PlatformOperatorApproval a, Dictionary<Guid, PlatformOperator> operators, PlatformOperator me)
    {
        var o = operators.GetValueOrDefault(a.PlatformOperatorId);
        return new PlatformApprovalView(a.PlatformOperatorApprovalId, a.PlatformOperatorId, o?.OperatorDisplayName ?? "(unknown operator)", new PlatformIdentity(o?.OperatorIssuerUri ?? string.Empty, o?.OperatorSubjectIdentifier ?? string.Empty),
            a.ApprovalRecordedOn, a.ApprovalValidUntil, a.ApprovalJustificationText, a.ApprovalStateName, a.ApprovalClosureReasonName, a.PlatformOperatorId == me.PlatformOperatorId);
    }

    private static object Detail(PlatformOperator me, string decision, OrganizationMembershipRequest request, DecisionContext context, string justification, VerifiedOperatorEvidence evidence, object specifics) =>
        PlatformOperatorRegistryService.Detail(me, decision, request.OrganizationMembershipRequestId, context.StatementSha256, justification, evidence, new
        {
            organization_id = request.OrganizationId,
            membership_request_id = request.OrganizationMembershipRequestId,
            statement_version = StatementVersion,
            stale_approvals_closed = context.StaleWithReason.Select(s => new { platform_operator_approval_id = s.Approval.PlatformOperatorApprovalId, closure_reason = s.Reason }),
            details = specifics,
        });

    private static DateTimeOffset Min(DateTimeOffset a, DateTimeOffset b) => a <= b ? a : b;

    private static AuthorityException NotFound() => new(AuthorityErrorCodes.NotFound, 404, false, "no such membership request");

    // ---- cursors: keyset position, bound to the operator and filter, integrity-protected with a per-process key ----

    private static string FilterBinding(PlatformOperator me, string status, string? text, int limit) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"{me.PlatformOperatorId:D}\n{status}\n{text}\n{limit}")))[..32];

    private static string WriteCursor(OrganizationMembershipRequest last, string binding)
    {
        var payload = $"v1|{last.RequestCreatedOn.UtcTicks}|{last.OrganizationMembershipRequestId:N}|{binding}";
        var mac = Convert.ToHexStringLower(HMACSHA256.HashData(CursorKey, Encoding.UTF8.GetBytes(payload)));
        return System.Buffers.Text.Base64Url.EncodeToString(Encoding.UTF8.GetBytes(payload + "|" + mac));
    }

    private static (DateTimeOffset, Guid) ReadCursor(string cursor, string binding)
    {
        try
        {
            var text = Encoding.UTF8.GetString(System.Buffers.Text.Base64Url.DecodeFromChars(cursor));
            var parts = text.Split('|');
            var payload = string.Join('|', parts.Take(4));
            var expected = Convert.ToHexStringLower(HMACSHA256.HashData(CursorKey, Encoding.UTF8.GetBytes(payload)));
            if (parts.Length == 5 && parts[0] == "v1" && CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(parts[4]), Encoding.ASCII.GetBytes(expected)) && parts[3] == binding)
            {
                return (new DateTimeOffset(long.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture), TimeSpan.Zero), Guid.ParseExact(parts[2], "N"));
            }
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException or OverflowException)
        {
        }

        throw new AuthorityException(AuthorityErrorCodes.CursorInvalid, 422, false, "the cursor is invalid, expired or belongs to another operator or filter");
    }

    private sealed record PlatformApprovalRecord(Guid PlatformOperatorApprovalId, Guid PlatformOperatorId, string OperatorIssuerUri, string OperatorSubjectIdentifier, string Justification, string EvidenceKind, string EvidenceSha256, DateTimeOffset RecordedOn);

    private sealed record PlatformMembershipApprovalPayload(Guid MembershipRequestId, Guid NewAuthorityUserId, string IssuerUri, string SubjectIdentifier, string? InitialRoleName, Guid RequestedByAuthorityUserId,
        int StatementVersion, string StatementSha256, string ApprovalAssurance, IReadOnlyList<PlatformApprovalRecord> Approvals);
}
