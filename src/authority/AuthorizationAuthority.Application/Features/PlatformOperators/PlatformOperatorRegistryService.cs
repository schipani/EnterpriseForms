using AuthorizationAuthority.Application.Common;
using AuthorizationAuthority.Application.Common.Ports;
using AuthorizationAuthority.Application.Features.OperatorPlane;
using AuthorizationAuthority.Contracts.V1;
using AuthorizationAuthority.Domain.Features.Journal;
using AuthorizationAuthority.Domain.Features.PlatformOperators;

namespace AuthorizationAuthority.Application.Features.PlatformOperators;

/// <summary>
/// C8 M2 Stage 1 (D8-34, D8-39): the Platform Operator registry.
/// <list type="bullet">
/// <item>Provision, reinstate and remove need two distinct active operators: A proposes (evidence over the change statement),
/// B — neither A nor the target — confirms the identical statement with their own evidence. Single use, 24-hour expiry; the
/// proposer must still be active and the target unchanged (row revision) at confirmation.</item>
/// <item>Suspension is one operator (self included): immediate, invalidates the target's pending approvals, raises an alert.</item>
/// <item>The recovery ceremony (operator plane, two DR-C7-3 custodian approvals) registers new identities only while fewer than
/// two operators are active.</item>
/// </list>
/// Every change is a platform journal event carrying the resulting rows, delivered to the independent archive before the
/// answer when possible. Nobody can grant themself anything: no self-provision, self-reinstatement or self-removal, and no
/// operator status confers any organization permission.
/// </summary>
public sealed class PlatformOperatorRegistryService(IAuthorityStore store, PlatformOperatorPolicy policy, PlatformOperatorEvidenceVerifier evidenceVerifier, PlatformJournal journal, OperatorApprovalVerifier custodians, ISecurityAlertSink alerts, TimeProvider clock)
{
    private const int StatementVersion = 1;
    public const string ProposalDetailKind = "PlatformOperatorChangeProposed";

    // ---- platform plane: reads ----

    public async Task<PlatformOperatorProfile> GetProfileAsync(VerifiedActor actor, Guid correlationId, CancellationToken cancellationToken)
    {
        policy.RequireOperational();
        await using var scope = await store.BeginScopeAsync(cancellationToken).ConfigureAwait(false);
        var head = await HeadForReadAsync(scope, correlationId, cancellationToken).ConfigureAwait(false);
        var me = await PlatformOperatorAccess.ResolveActiveAsync(scope, actor, cancellationToken).ConfigureAwait(false);
        var operators = await scope.GetPlatformOperatorsAsync(cancellationToken).ConfigureAwait(false);
        return new PlatformOperatorProfile(PlatformOperatorAccess.Summary(me), policy.Mode, policy.Assurance, PlatformPermissions.All, operators.Count(o => o.OperatorStatusName == PlatformOperatorStates.Active),
            PlatformJournal.Status(head, await scope.CountPendingPlatformArchiveAsync(cancellationToken).ConfigureAwait(false)), correlationId);
    }

    public async Task<PlatformOperatorRegistryView> GetRegistryAsync(VerifiedActor actor, Guid correlationId, CancellationToken cancellationToken)
    {
        policy.RequireOperational();
        await using var scope = await store.BeginScopeAsync(cancellationToken).ConfigureAwait(false);
        var head = await HeadForReadAsync(scope, correlationId, cancellationToken).ConfigureAwait(false);
        _ = await PlatformOperatorAccess.ResolveActiveAsync(scope, actor, cancellationToken).ConfigureAwait(false);
        var operators = await scope.GetPlatformOperatorsAsync(cancellationToken).ConfigureAwait(false);
        var now = clock.GetUtcNow();
        var proposals = (await scope.GetPlatformOperatorProposalsAsync(now - policy.ProposalLifetime, cancellationToken).ConfigureAwait(false))
            .Where(p => p.ProposalStateName == PlatformProposalStates.Pending && p.ProposalExpiresOn > now).Select(ToView).ToList();
        var active = operators.Count(o => o.OperatorStatusName == PlatformOperatorStates.Active);
        return new PlatformOperatorRegistryView(operators.Select(PlatformOperatorAccess.Summary).ToList(), proposals, active, active >= 2,
            PlatformJournal.Status(head, await scope.CountPendingPlatformArchiveAsync(cancellationToken).ConfigureAwait(false)), correlationId);
    }

    // ---- platform plane: two-operator changes ----

    public async Task<PlatformOperatorDecisionResponse> ProposeAsync(VerifiedActor actor, PlatformOperatorProposalRequest input, Guid correlationId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        var justification = PlatformOperatorAccess.RequireJustification(input.Justification);
        if (input.OperationId == Guid.Empty || input.OperationId.Version != 7)
        {
            throw AuthorityException.Invalid("operation_id must be a UUIDv7 chosen for this proposal");
        }

        return await DecideAsync(actor, grantDirection: true, PlatformDecisions.Propose, PlatformOperationKinds.PlatformOperatorChange, input.OperationId, correlationId, async (scope, head, me, now) =>
        {
            var operators = await scope.GetPlatformOperatorsAsync(cancellationToken).ConfigureAwait(false);
            var pending = (await scope.GetPlatformOperatorProposalsAsync(now - policy.ProposalLifetime, cancellationToken).ConfigureAwait(false)).Where(p => p.ProposalStateName == PlatformProposalStates.Pending && p.ProposalExpiresOn > now).ToList();
            var (statement, refusal, target) = await PlanChangeAsync(scope, input, me, operators, pending, cancellationToken).ConfigureAwait(false);
            var statementJson = CanonicalJson.Serialize(statement);
            var statementSha256 = Convert.ToHexStringLower(CanonicalJson.Digest(statementJson));
            if (input.DryRun)
            {
                return Preview(input.OperationId, PlatformOperationKinds.PlatformOperatorChange, PlatformDecisions.Propose, statementJson, statementSha256, refusal, target, head);
            }

            if (refusal is not null)
            {
                throw refusal;
            }

            var evidence = evidenceVerifier.Verify(me, input.EvidenceKind, input.Evidence, new OperatorEvidenceBinding(PlatformDecisions.Propose, PlatformOperationKinds.PlatformOperatorChange, input.OperationId, Guid.Empty, statementSha256,
                PlatformOperatorEvidenceVerifier.JustificationSha256(justification)), now);
            await PlatformOperatorAccess.RequireUnusedAsync(scope, evidence, cancellationToken).ConfigureAwait(false);
            var proposal = new PlatformOperatorChangeProposal
            {
                PlatformOperatorChangeProposalId = input.OperationId,
                ProposedChangeName = statement.Change,
                TargetPlatformOperatorId = statement.TargetPlatformOperatorId,
                TargetIssuerUri = statement.TargetIssuerUri,
                TargetSubjectIdentifier = statement.TargetSubjectIdentifier,
                TargetDisplayName = statement.TargetDisplayName,
                TargetEvidenceKindName = statement.TargetEvidenceKind,
                TargetSyntheticKeyIdentifier = statement.TargetSyntheticKeyIdentifier,
                TargetSyntheticPublicKey = statement.Change == PlatformOperatorChanges.Provision ? PlatformOperatorAccess.ValidateCandidate(input.Candidate).PublicKey : target?.SyntheticApprovalPublicKey,
                TargetRowRevision = statement.TargetRowRevision,
                ProposedByPlatformOperatorId = me.PlatformOperatorId,
                ProposalJustificationText = justification,
                ProposalStatementSha256 = Convert.FromHexString(statementSha256),
                ProposalCreatedOn = now,
                ProposalExpiresOn = now + policy.ProposalLifetime,
            };
            scope.AddPlatformOperatorProposal(proposal);
            var use = PlatformOperatorAccess.EvidenceUse(evidence, me.PlatformOperatorId, PlatformDecisions.Propose, now);
            scope.AddPlatformEvidenceUse(use);
            PlatformJournal.Append(scope, head, ProposalDetailKind, Detail(me, PlatformDecisions.Propose, input.OperationId, statementSha256, justification, evidence, new { change = statement.Change, target_platform_operator_id = statement.TargetPlatformOperatorId }),
                PlatformStateDelta.Of(proposals: [proposal], evidenceUses: [use]), now);
            return (Func<string, PlatformOperatorDecisionResponse>)(state => new PlatformOperatorDecisionResponse(false, true, null, null, PlatformOperationKinds.PlatformOperatorChange, input.OperationId, PlatformDecisions.Propose, statementJson, statementSha256, "ProposalRecorded",
                target is null ? null : PlatformOperatorAccess.Summary(target), proposal.PlatformOperatorChangeProposalId, head.PlatformJournalRevision, state, policy.Assurance, correlationId));
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<PlatformOperatorDecisionResponse> ConfirmAsync(VerifiedActor actor, Guid proposalId, PlatformOperatorConfirmationRequest input, Guid correlationId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        var justification = PlatformOperatorAccess.RequireJustification(input.Justification);
        return await DecideAsync(actor, grantDirection: true, PlatformDecisions.Confirm, PlatformOperationKinds.PlatformOperatorChange, proposalId, correlationId, async (scope, head, me, now) =>
        {
            var proposal = await scope.FindPlatformOperatorProposalAsync(proposalId, cancellationToken).ConfigureAwait(false)
                           ?? throw new AuthorityException(AuthorityErrorCodes.NotFound, 404, false, "no such proposal");
            var target = proposal.ProposedChangeName == PlatformOperatorChanges.Provision ? null : await scope.FindPlatformOperatorAsync(proposal.TargetPlatformOperatorId, cancellationToken).ConfigureAwait(false);
            var proposer = await scope.FindPlatformOperatorAsync(proposal.ProposedByPlatformOperatorId, cancellationToken).ConfigureAwait(false);
            var statement = StatementOf(proposal, target?.OperatorRowRevision ?? 0);
            var statementJson = CanonicalJson.Serialize(statement);
            var statementSha256 = Convert.ToHexStringLower(CanonicalJson.Digest(statementJson));
            var operators = await scope.GetPlatformOperatorsAsync(cancellationToken).ConfigureAwait(false);
            var refusal = EvaluateConfirmation(proposal, proposer, target, me, operators, statementSha256, input.ExpectedStatementSha256, now);
            if (input.DryRun)
            {
                return Preview(proposalId, PlatformOperationKinds.PlatformOperatorChange, PlatformDecisions.Confirm, statementJson, statementSha256, refusal, target, head);
            }

            if (refusal is not null)
            {
                throw refusal;
            }

            var evidence = evidenceVerifier.Verify(me, input.EvidenceKind, input.Evidence, new OperatorEvidenceBinding(PlatformDecisions.Confirm, PlatformOperationKinds.PlatformOperatorChange, proposalId, Guid.Empty, statementSha256,
                PlatformOperatorEvidenceVerifier.JustificationSha256(justification)), now);
            await PlatformOperatorAccess.RequireUnusedAsync(scope, evidence, cancellationToken).ConfigureAwait(false);

            var closed = new List<PlatformOperatorApproval>();
            string outcome;
            PlatformOperator changed;
            if (proposal.ProposedChangeName == PlatformOperatorChanges.Provision)
            {
                changed = PlatformOperatorAccess.NewOperator(proposal.TargetPlatformOperatorId, new PlatformOperatorAccess.ValidatedCandidate(proposal.TargetIssuerUri, proposal.TargetSubjectIdentifier, proposal.TargetDisplayName,
                    proposal.TargetEvidenceKindName, proposal.TargetSyntheticKeyIdentifier, proposal.TargetSyntheticPublicKey), proposal.ProposedByPlatformOperatorId, justification, now);
                scope.AddPlatformOperator(changed);
                outcome = "Provisioned";
            }
            else
            {
                changed = target!;
                changed.OperatorStatusName = proposal.ProposedChangeName == PlatformOperatorChanges.Reinstate ? PlatformOperatorStates.Active : PlatformOperatorStates.Removed;
                changed.OperatorStatusChangedOn = now;
                changed.OperatorStatusReasonText = justification;
                changed.OperatorRowRevision++;
                outcome = proposal.ProposedChangeName == PlatformOperatorChanges.Reinstate ? "Reinstated" : "Removed";
                if (proposal.ProposedChangeName == PlatformOperatorChanges.Remove)
                {
                    closed.AddRange(await CloseApprovalsOfAsync(scope, changed.PlatformOperatorId, PlatformApprovalClosureReasons.OperatorRemoved, now, cancellationToken).ConfigureAwait(false));
                }
            }

            proposal.ProposalStateName = PlatformProposalStates.Consumed;
            proposal.ConfirmedByPlatformOperatorId = me.PlatformOperatorId;
            proposal.ProposalConsumedOn = now;
            var use = PlatformOperatorAccess.EvidenceUse(evidence, me.PlatformOperatorId, PlatformDecisions.Confirm, now);
            scope.AddPlatformEvidenceUse(use);
            PlatformJournal.Append(scope, head, "PlatformOperator" + outcome, Detail(me, PlatformDecisions.Confirm, proposalId, statementSha256, justification, evidence,
                new { change = proposal.ProposedChangeName, target_platform_operator_id = changed.PlatformOperatorId, proposed_by_platform_operator_id = proposal.ProposedByPlatformOperatorId, invalidated_approvals = closed.Select(a => a.PlatformOperatorApprovalId) }),
                PlatformStateDelta.Of([changed], [proposal], closed, [use]), now);
            alerts.Raise(new SecurityAlert("PlatformOperator" + outcome, "High", PlatformJournal.ArchiveScopeId, proposalId, null, $"Platform Operator {changed.PlatformOperatorId} {outcome.ToLowerInvariant()} (proposed by {proposal.ProposedByPlatformOperatorId}, confirmed by {me.PlatformOperatorId})", correlationId));
            return (Func<string, PlatformOperatorDecisionResponse>)(state => new PlatformOperatorDecisionResponse(false, true, null, null, PlatformOperationKinds.PlatformOperatorChange, proposalId, PlatformDecisions.Confirm, statementJson, statementSha256, outcome,
                PlatformOperatorAccess.Summary(changed), proposalId, head.PlatformJournalRevision, state, policy.Assurance, correlationId));
        }, cancellationToken).ConfigureAwait(false);
    }

    // ---- platform plane: emergency suspension (one operator) ----

    public async Task<PlatformOperatorDecisionResponse> SuspendAsync(VerifiedActor actor, Guid targetPlatformOperatorId, PlatformOperatorSuspensionRequest input, Guid correlationId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        var justification = PlatformOperatorAccess.RequireJustification(input.Justification);
        if (input.OperationId == Guid.Empty || input.OperationId.Version != 7)
        {
            throw AuthorityException.Invalid("operation_id must be a UUIDv7 chosen for this suspension");
        }

        // Restrictive: allowed while earlier events still wait for the archive (it never needs the archive to be caught up), but
        // never while the database is behind the archive or the journal is quarantined.
        return await DecideAsync(actor, grantDirection: false, PlatformDecisions.Suspend, PlatformOperationKinds.PlatformOperatorSuspension, input.OperationId, correlationId, async (scope, head, me, now) =>
        {
            var target = await scope.FindPlatformOperatorAsync(targetPlatformOperatorId, cancellationToken).ConfigureAwait(false)
                         ?? throw new AuthorityException(AuthorityErrorCodes.NotFound, 404, false, "no such operator");
            var statement = new PlatformOperatorSuspensionStatement(StatementVersion, PlatformOperationKinds.PlatformOperatorSuspension, input.OperationId, target.PlatformOperatorId, target.OperatorIssuerUri, target.OperatorSubjectIdentifier,
                target.OperatorRowRevision, me.PlatformOperatorId);
            var statementJson = CanonicalJson.Serialize(statement);
            var statementSha256 = Convert.ToHexStringLower(CanonicalJson.Digest(statementJson));
            var refusal = target.OperatorStatusName != PlatformOperatorStates.Active ? new AuthorityException(AuthorityErrorCodes.OperatorStateConflict, 409, false, $"the operator is {target.OperatorStatusName}") : null;
            if (input.DryRun)
            {
                return Preview(input.OperationId, PlatformOperationKinds.PlatformOperatorSuspension, PlatformDecisions.Suspend, statementJson, statementSha256, refusal, target, head);
            }

            if (refusal is not null)
            {
                throw refusal;
            }

            var evidence = evidenceVerifier.Verify(me, input.EvidenceKind, input.Evidence, new OperatorEvidenceBinding(PlatformDecisions.Suspend, PlatformOperationKinds.PlatformOperatorSuspension, input.OperationId, Guid.Empty, statementSha256,
                PlatformOperatorEvidenceVerifier.JustificationSha256(justification)), now);
            await PlatformOperatorAccess.RequireUnusedAsync(scope, evidence, cancellationToken).ConfigureAwait(false);
            target.OperatorStatusName = PlatformOperatorStates.Suspended;
            target.OperatorStatusChangedOn = now;
            target.OperatorStatusReasonText = justification;
            target.OperatorRowRevision++;
            var closed = await CloseApprovalsOfAsync(scope, target.PlatformOperatorId, PlatformApprovalClosureReasons.OperatorSuspended, now, cancellationToken).ConfigureAwait(false);
            var use = PlatformOperatorAccess.EvidenceUse(evidence, me.PlatformOperatorId, PlatformDecisions.Suspend, now);
            scope.AddPlatformEvidenceUse(use);
            PlatformJournal.Append(scope, head, "PlatformOperatorSuspended", Detail(me, PlatformDecisions.Suspend, input.OperationId, statementSha256, justification, evidence,
                new { target_platform_operator_id = target.PlatformOperatorId, invalidated_approvals = closed.Select(a => a.PlatformOperatorApprovalId) }), PlatformStateDelta.Of([target], approvals: closed, evidenceUses: [use]), now);
            alerts.Raise(new SecurityAlert("PlatformOperatorSuspended", "High", PlatformJournal.ArchiveScopeId, input.OperationId, null, $"Platform Operator {target.PlatformOperatorId} suspended by {me.PlatformOperatorId}; {closed.Count} pending approvals invalidated", correlationId));
            return (Func<string, PlatformOperatorDecisionResponse>)(state => new PlatformOperatorDecisionResponse(false, true, null, null, PlatformOperationKinds.PlatformOperatorSuspension, input.OperationId, PlatformDecisions.Suspend, statementJson, statementSha256, "Suspended",
                PlatformOperatorAccess.Summary(target), null, head.PlatformJournalRevision, state, policy.Assurance, correlationId));
        }, cancellationToken).ConfigureAwait(false);
    }

    // ---- operator plane: status and recovery ceremony ----

    public async Task<PlatformOperatorPlaneStatus> GetPlaneStatusAsync(Guid correlationId, CancellationToken cancellationToken)
    {
        await using var scope = await store.BeginScopeAsync(cancellationToken).ConfigureAwait(false);
        var head = await scope.ReadPlatformJournalAsync(cancellationToken).ConfigureAwait(false) ?? new PlatformJournalHead();
        var refusal = await journal.RefusalAsync(scope, head, requireAnchored: false, correlationId, cancellationToken).ConfigureAwait(false);
        var operators = await scope.GetPlatformOperatorsAsync(cancellationToken).ConfigureAwait(false);
        var active = operators.Count(o => o.OperatorStatusName == PlatformOperatorStates.Active);
        return new PlatformOperatorPlaneStatus(policy.Mode, policy.Operational, policy.Assurance, active, policy.Operational && custodians.Operational && active < 2, operators.Select(PlatformOperatorAccess.Summary).ToList(),
            PlatformJournal.Status(head, await scope.CountPendingPlatformArchiveAsync(cancellationToken).ConfigureAwait(false)), refusal?.ErrorCode, correlationId);
    }

    public async Task<PlatformOperatorBootstrapResponse> BootstrapAsync(PlatformOperatorBootstrapRequest input, string operatorName, Guid correlationId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        policy.RequireOperational();
        var justification = PlatformOperatorAccess.RequireJustification(input.Justification);
        var incident = input.IncidentReference?.Trim();
        if (incident is { Length: > 100 })
        {
            throw AuthorityException.Invalid("incident_reference is at most 100 characters");
        }

        if (input.OperationId == Guid.Empty || input.OperationId.Version != 7)
        {
            throw AuthorityException.Invalid("operation_id must be a UUIDv7 chosen for this ceremony");
        }

        if (input.Operators is not { Count: >= 1 and <= 10 })
        {
            throw AuthorityException.Invalid("one to ten operator identities are required");
        }

        var candidates = input.Operators.Select(PlatformOperatorAccess.ValidateCandidate).ToList();
        if (candidates.Select(c => (c.IssuerUri, c.SubjectIdentifier)).Distinct().Count() != candidates.Count || candidates.Select(c => c.KeyIdentifier).Distinct(StringComparer.Ordinal).Count() != candidates.Count
            || candidates.Select(c => c.PublicKeySha256).Distinct(StringComparer.Ordinal).Count() != candidates.Count)
        {
            throw AuthorityException.Invalid("every operator needs its own identity and its own approval key");
        }

        await TryDeliverAsync(cancellationToken).ConfigureAwait(false);
        string archiveState;
        PlatformOperatorBootstrapResponse response;
        await using (var scope = await store.BeginScopeAsync(cancellationToken).ConfigureAwait(false))
        {
            var head = await PlatformJournal.LockHeadAsync(scope, clock, cancellationToken).ConfigureAwait(false);
            await journal.EnsureCurrentAsync(scope, head, requireAnchored: true, correlationId, cancellationToken).ConfigureAwait(false);
            var operators = await scope.GetPlatformOperatorsAsync(cancellationToken).ConfigureAwait(false);
            var active = operators.Count(o => o.OperatorStatusName == PlatformOperatorStates.Active);
            var statement = new PlatformOperatorBootstrapStatement(StatementVersion, PlatformOperationKinds.PlatformOperatorBootstrap, input.OperationId,
                candidates.Select(c => new PlatformOperatorBootstrapCandidateStatement(c.IssuerUri, c.SubjectIdentifier, c.DisplayName, c.EvidenceKind, c.KeyIdentifier, c.PublicKeySha256)).ToList(), justification, incident, active);
            var statementJson = CanonicalJson.Serialize(statement);
            var statementSha256 = Convert.ToHexStringLower(CanonicalJson.Digest(statementJson));
            var operationMarker = "operation:" + input.OperationId.ToString("D");
            AuthorityException? refusal = null;
            if (active >= 2)
            {
                refusal = new AuthorityException(AuthorityErrorCodes.OperatorQuorumPresent, 409, false, $"{active} operators are active: the recovery ceremony applies only below two; use the two-operator Provision change");
            }
            else if (active + candidates.Count < 2)
            {
                refusal = AuthorityException.Invalid("the ceremony must leave at least two active operators");
            }
            else if (await scope.IsPlatformEvidenceUsedAsync(operationMarker, CanonicalJson.Digest(statementJson), cancellationToken).ConfigureAwait(false))
            {
                refusal = new AuthorityException(AuthorityErrorCodes.PlatformOperationReplayed, 409, false, "this ceremony operation id was already used");
            }
            else
            {
                refusal = candidates.Select(c => PlatformOperatorAccess.Conflicts(c, operators)).FirstOrDefault(r => r is not null);
            }

            if (input.DryRun)
            {
                return Ceremony(true, input.OperationId, refusal, statementJson, statementSha256, operators, head, ArchiveDeliveryStates.Anchored, correlationId);
            }

            var now = clock.GetUtcNow();
            IReadOnlyList<VerifiedOperatorApproval> approvals;
            try
            {
                if (refusal is not null)
                {
                    throw refusal; // a state refusal is reported before (and independently of) the custodian approvals
                }

                approvals = custodians.VerifyPlatformCeremony(input.Approvals, input.OperationId, PlatformOperationKinds.PlatformOperatorBootstrap, statementSha256, now);
            }
            catch (AuthorityException ex)
            {
                PlatformJournal.Append(scope, head, "PlatformOperatorBootstrapRefused", new { operation_id = input.OperationId, operator_workload = operatorName, refusal_code = ex.ErrorCode, correlation_id = correlationId }, PlatformStateDelta.Of(), now);
                await scope.CommitAsync(cancellationToken).ConfigureAwait(false);
                throw;
            }

            var added = candidates.Select(c => PlatformOperatorAccess.NewOperator(Guid.CreateVersion7(), c, null, "recovery ceremony: " + justification, now)).ToList();
            foreach (var o in added)
            {
                scope.AddPlatformOperator(o);
            }

            var uses = new List<PlatformOperatorEvidenceUse>
            {
                new() { PlatformOperatorEvidenceUseId = Guid.CreateVersion7(), EvidenceUseIdentifier = operationMarker, EvidenceUseSha256 = CanonicalJson.Digest(statementJson), EvidenceDecisionName = PlatformDecisions.Ceremony, EvidenceUsedOn = now },
            };
            uses.AddRange(approvals.Select(a => new PlatformOperatorEvidenceUse
            {
                PlatformOperatorEvidenceUseId = Guid.CreateVersion7(),
                EvidenceUseIdentifier = "custodian-approval:" + a.ApprovalSha256[..32],
                EvidenceUseSha256 = Convert.FromHexString(a.ApprovalSha256),
                EvidenceDecisionName = PlatformDecisions.Ceremony,
                EvidenceUsedOn = now,
            }));
            foreach (var use in uses)
            {
                scope.AddPlatformEvidenceUse(use);
            }

            PlatformJournal.Append(scope, head, "PlatformOperatorsBootstrapped", new
            {
                operation_id = input.OperationId,
                operator_workload = operatorName,
                justification,
                incident_reference = incident,
                active_operator_count_before = active,
                registered_platform_operator_ids = added.Select(o => o.PlatformOperatorId),
                custodian_approvals = approvals.Select(a => new { approver_id = a.ApproverId, approval_sha256 = a.ApprovalSha256 }),
                custodian_assurance = custodians.Assurance,
                statement_sha256 = statementSha256,
                correlation_id = correlationId,
            }, PlatformStateDelta.Of(added, evidenceUses: uses), now);
            await scope.CommitAsync(cancellationToken).ConfigureAwait(false);
            alerts.Raise(new SecurityAlert("PlatformOperatorsBootstrapped", "Critical", PlatformJournal.ArchiveScopeId, input.OperationId, null, $"recovery ceremony by {operatorName} registered {added.Count} Platform Operators ({custodians.Assurance})", correlationId));
            archiveState = await TryDeliverAsync(cancellationToken).ConfigureAwait(false);
            response = Ceremony(false, input.OperationId, null, statementJson, statementSha256, [.. operators, .. added], head, archiveState, correlationId);
        }

        return response;
    }

    // ---- internals ----

    /// <summary>
    /// One platform decision: deliver anything pending (grant direction), lock the platform head, fail closed unless the journal is
    /// current, resolve the active operator, run the decision, journal a refusal by an authenticated operator, commit, then anchor.
    /// </summary>
    private async Task<PlatformOperatorDecisionResponse> DecideAsync(VerifiedActor actor, bool grantDirection, string decision, string operationKind, Guid operationId, Guid correlationId,
        Func<IAuthorityScope, PlatformJournalHead, PlatformOperator, DateTimeOffset, Task<object>> body, CancellationToken cancellationToken)
    {
        policy.RequireOperational();
        if (grantDirection)
        {
            await TryDeliverAsync(cancellationToken).ConfigureAwait(false);
        }

        object outcome;
        await using (var scope = await store.BeginScopeAsync(cancellationToken).ConfigureAwait(false))
        {
            var head = await PlatformJournal.LockHeadAsync(scope, clock, cancellationToken).ConfigureAwait(false);
            await journal.EnsureCurrentAsync(scope, head, grantDirection, correlationId, cancellationToken).ConfigureAwait(false);
            var me = await PlatformOperatorAccess.ResolveActiveAsync(scope, actor, cancellationToken).ConfigureAwait(false);
            var now = clock.GetUtcNow();
            try
            {
                outcome = await body(scope, head, me, now).ConfigureAwait(false);
            }
            catch (AuthorityException ex)
            {
                await RecordRefusalAsync(scope, head, me, decision, operationKind, operationId, ex, correlationId, now, cancellationToken).ConfigureAwait(false);
                throw;
            }

            if (outcome is PlatformOperatorDecisionResponse preview)
            {
                return preview; // dry run: nothing staged, nothing committed
            }

            await scope.CommitAsync(cancellationToken).ConfigureAwait(false);
        }

        var archiveState = await TryDeliverAsync(cancellationToken).ConfigureAwait(false);
        return ((Func<string, PlatformOperatorDecisionResponse>)outcome)(archiveState);
    }

    /// <summary>Journals a refusal of an authenticated active operator (tracked rows are untouched until a decision succeeds).</summary>
    internal static async Task RecordRefusalAsync(IAuthorityScope scope, PlatformJournalHead head, PlatformOperator me, string decision, string operationKind, Guid operationId, AuthorityException refusal, Guid correlationId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        PlatformJournal.Append(scope, head, "PlatformOperatorDecisionRefused", new
        {
            platform_operator_id = me.PlatformOperatorId,
            decision,
            operation_kind = operationKind,
            operation_id = operationId,
            refusal_code = refusal.ErrorCode,
            correlation_id = correlationId,
        }, PlatformStateDelta.Of(), now);
        await scope.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Anchors pending platform events; an unreachable archive leaves them Pending (the background loop retries).</summary>
    internal async Task<string> TryDeliverAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await journal.DeliverAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (ArchiveUnavailableException)
        {
            return ArchiveDeliveryStates.Pending;
        }
    }

    private async Task<PlatformJournalHead> HeadForReadAsync(IAuthorityScope scope, Guid correlationId, CancellationToken cancellationToken)
    {
        var head = await scope.ReadPlatformJournalAsync(cancellationToken).ConfigureAwait(false) ?? new PlatformJournalHead();
        await journal.EnsureCurrentAsync(scope, head, requireAnchored: false, correlationId, cancellationToken).ConfigureAwait(false);
        return head;
    }

    private static async Task<List<PlatformOperatorApproval>> CloseApprovalsOfAsync(IAuthorityScope scope, Guid platformOperatorId, string reason, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var pending = await scope.GetPendingPlatformApprovalsForOperatorAsync(platformOperatorId, cancellationToken).ConfigureAwait(false);
        foreach (var approval in pending)
        {
            PlatformApprovalLedger.Close(approval, PlatformApprovalStates.Invalidated, reason, now);
        }

        return pending;
    }

    private static async Task<(PlatformOperatorChangeStatement Statement, AuthorityException? Refusal, PlatformOperator? Target)> PlanChangeAsync(IAuthorityScope scope, PlatformOperatorProposalRequest input, PlatformOperator me,
        List<PlatformOperator> operators, List<PlatformOperatorChangeProposal> pending, CancellationToken cancellationToken)
    {
        var active = operators.Count(o => o.OperatorStatusName == PlatformOperatorStates.Active);
        if (await scope.FindPlatformOperatorProposalAsync(input.OperationId, cancellationToken).ConfigureAwait(false) is not null || operators.Any(o => o.PlatformOperatorId == input.OperationId))
        {
            throw new AuthorityException(AuthorityErrorCodes.PlatformOperationReplayed, 409, false, "this operation id was already used");
        }

        if (input.Change == PlatformOperatorChanges.Provision)
        {
            var candidate = PlatformOperatorAccess.ValidateCandidate(input.Candidate);
            var statement = new PlatformOperatorChangeStatement(StatementVersion, PlatformOperationKinds.PlatformOperatorChange, input.OperationId, PlatformOperatorChanges.Provision, input.OperationId, candidate.IssuerUri, candidate.SubjectIdentifier,
                candidate.DisplayName, candidate.EvidenceKind, candidate.KeyIdentifier, candidate.PublicKeySha256, 0, me.PlatformOperatorId);
            var refusal = candidate.IssuerUri == me.OperatorIssuerUri && candidate.SubjectIdentifier == me.OperatorSubjectIdentifier
                ? new AuthorityException(AuthorityErrorCodes.SelfChangeForbidden, 403, false, "an operator cannot provision their own identity")
                : PlatformOperatorAccess.Conflicts(candidate, operators)
                  ?? (pending.Any(p => p.ProposedChangeName == PlatformOperatorChanges.Provision && p.TargetIssuerUri == candidate.IssuerUri && p.TargetSubjectIdentifier == candidate.SubjectIdentifier)
                      ? new AuthorityException(AuthorityErrorCodes.ProposalExists, 409, false, "a pending proposal already provisions this identity")
                      : QuorumRefusal(active));
            return (statement, refusal, null);
        }

        if (input.Change is not (PlatformOperatorChanges.Reinstate or PlatformOperatorChanges.Remove))
        {
            throw AuthorityException.Invalid("change must be Provision, Reinstate or Remove (suspension is its own one-operator action)");
        }

        var target = operators.FirstOrDefault(o => o.PlatformOperatorId == input.TargetPlatformOperatorId) ?? throw new AuthorityException(AuthorityErrorCodes.NotFound, 404, false, "no such operator");
        var change = new PlatformOperatorChangeStatement(StatementVersion, PlatformOperationKinds.PlatformOperatorChange, input.OperationId, input.Change, target.PlatformOperatorId, target.OperatorIssuerUri, target.OperatorSubjectIdentifier,
            target.OperatorDisplayName, target.OperatorEvidenceKindName, target.SyntheticApprovalKeyIdentifier, target.SyntheticApprovalPublicKey is null ? null : Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(target.SyntheticApprovalPublicKey)),
            target.OperatorRowRevision, me.PlatformOperatorId);
        AuthorityException? stateRefusal = target.PlatformOperatorId == me.PlatformOperatorId
            ? new AuthorityException(AuthorityErrorCodes.SelfChangeForbidden, 403, false, "an operator cannot reinstate or remove themself")
            : input.Change == PlatformOperatorChanges.Reinstate && target.OperatorStatusName != PlatformOperatorStates.Suspended
                ? new AuthorityException(AuthorityErrorCodes.OperatorStateConflict, 409, false, "only a suspended operator can be reinstated")
                : input.Change == PlatformOperatorChanges.Remove && target.OperatorStatusName == PlatformOperatorStates.Removed
                    ? new AuthorityException(AuthorityErrorCodes.OperatorStateConflict, 409, false, "the operator is already removed")
                    : pending.Any(p => p.TargetPlatformOperatorId == target.PlatformOperatorId && p.ProposedChangeName == input.Change)
                        ? new AuthorityException(AuthorityErrorCodes.ProposalExists, 409, false, "an identical change is already pending")
                        : QuorumRefusal(active);
        return (change, stateRefusal, target);
    }

    private static AuthorityException? QuorumRefusal(int active) =>
        active < 2 ? new AuthorityException(AuthorityErrorCodes.OperatorQuorumUnavailable, 409, false, "fewer than two operators are active, so no change can be confirmed; use the recovery ceremony") : null;

    private static AuthorityException? EvaluateConfirmation(PlatformOperatorChangeProposal proposal, PlatformOperator? proposer, PlatformOperator? target, PlatformOperator me, List<PlatformOperator> operators, string statementSha256, string? expected, DateTimeOffset now)
    {
        if (proposal.ProposalStateName != PlatformProposalStates.Pending)
        {
            return new AuthorityException(AuthorityErrorCodes.ProposalNotPending, 409, false, "the proposal was already confirmed");
        }

        if (proposal.ProposalExpiresOn <= now)
        {
            return new AuthorityException(AuthorityErrorCodes.ProposalExpired, 409, false, "the proposal expired");
        }

        if (proposal.ProposedByPlatformOperatorId == me.PlatformOperatorId)
        {
            return new AuthorityException(AuthorityErrorCodes.OperatorApprovalsNotIndependent, 403, false, "the confirming operator must be a different operator from the proposer");
        }

        if (proposal.TargetPlatformOperatorId == me.PlatformOperatorId || (proposal.TargetIssuerUri == me.OperatorIssuerUri && proposal.TargetSubjectIdentifier == me.OperatorSubjectIdentifier))
        {
            return new AuthorityException(AuthorityErrorCodes.SelfChangeForbidden, 403, false, "the target operator cannot confirm a change to themself");
        }

        if (proposer is null || proposer.OperatorStatusName != PlatformOperatorStates.Active)
        {
            return new AuthorityException(AuthorityErrorCodes.PlatformOperatorNotActive, 403, false, "the proposing operator is no longer active");
        }

        if (!statementSha256.AsSpan().SequenceEqual(Convert.ToHexStringLower(proposal.ProposalStatementSha256)) || !string.Equals(expected?.Trim(), statementSha256, StringComparison.OrdinalIgnoreCase))
        {
            return new AuthorityException(AuthorityErrorCodes.ApprovalStatementChanged, 409, false, "the operator or the proposal changed since it was reviewed");
        }

        return proposal.ProposedChangeName switch
        {
            PlatformOperatorChanges.Provision => PlatformOperatorAccess.Conflicts(new PlatformOperatorAccess.ValidatedCandidate(proposal.TargetIssuerUri, proposal.TargetSubjectIdentifier, proposal.TargetDisplayName, proposal.TargetEvidenceKindName,
                proposal.TargetSyntheticKeyIdentifier, proposal.TargetSyntheticPublicKey), operators),
            PlatformOperatorChanges.Reinstate when target?.OperatorStatusName != PlatformOperatorStates.Suspended => new AuthorityException(AuthorityErrorCodes.OperatorStateConflict, 409, false, "the operator is no longer suspended"),
            PlatformOperatorChanges.Remove when target is null || target.OperatorStatusName == PlatformOperatorStates.Removed => new AuthorityException(AuthorityErrorCodes.OperatorStateConflict, 409, false, "the operator is already removed"),
            _ => null,
        };
    }

    private static PlatformOperatorChangeStatement StatementOf(PlatformOperatorChangeProposal p, long targetRowRevision) =>
        new(StatementVersion, PlatformOperationKinds.PlatformOperatorChange, p.PlatformOperatorChangeProposalId, p.ProposedChangeName, p.TargetPlatformOperatorId, p.TargetIssuerUri, p.TargetSubjectIdentifier, p.TargetDisplayName,
            p.TargetEvidenceKindName, p.TargetSyntheticKeyIdentifier, p.TargetSyntheticPublicKey is null ? null : Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(p.TargetSyntheticPublicKey)),
            p.ProposedChangeName == PlatformOperatorChanges.Provision ? 0 : targetRowRevision, p.ProposedByPlatformOperatorId);

    private PlatformOperatorDecisionResponse Preview(Guid operationId, string kind, string decision, string statementJson, string statementSha256, AuthorityException? refusal, PlatformOperator? target, PlatformJournalHead head) =>
        new(true, refusal is null, refusal?.ErrorCode, refusal?.Detail, kind, operationId, decision, statementJson, statementSha256, "Preview", target is null ? null : PlatformOperatorAccess.Summary(target), null,
            head.PlatformJournalRevision, ArchiveDeliveryStates.Anchored, policy.Assurance, Guid.Empty);

    private PlatformOperatorBootstrapResponse Ceremony(bool dryRun, Guid operationId, AuthorityException? refusal, string statementJson, string statementSha256, IEnumerable<PlatformOperator> operators, PlatformJournalHead head, string archiveState, Guid correlationId) =>
        new(dryRun, refusal is null, refusal?.ErrorCode, refusal?.Detail, operationId, PlatformOperationKinds.PlatformOperatorBootstrap, Guid.Empty, new PlatformCeremonyTarget(Guid.Empty), statementJson, statementSha256, custodians.Mode, custodians.Assurance,
            OperatorApprovalVerifier.RequiredApprovals, operators.Select(PlatformOperatorAccess.Summary).ToList(), PlatformJournal.Status(head, 0), archiveState, correlationId);

    private static PlatformOperatorProposalView ToView(PlatformOperatorChangeProposal p) =>
        new(p.PlatformOperatorChangeProposalId, p.ProposedChangeName, p.TargetPlatformOperatorId, p.TargetIssuerUri, p.TargetSubjectIdentifier, p.TargetDisplayName, p.ProposedByPlatformOperatorId, p.ProposalJustificationText,
            p.ProposalCreatedOn, p.ProposalExpiresOn, p.ProposalStateName, Convert.ToHexStringLower(p.ProposalStatementSha256));

    internal static object Detail(PlatformOperator me, string decision, Guid operationId, string statementSha256, string justification, VerifiedOperatorEvidence evidence, object specifics) => new
    {
        platform_operator_id = me.PlatformOperatorId,
        operator_issuer_uri = me.OperatorIssuerUri,
        operator_subject_identifier = me.OperatorSubjectIdentifier,
        decision,
        operation_id = operationId,
        statement_sha256 = statementSha256,
        justification,
        evidence_kind = evidence.EvidenceKind,
        evidence_key_identifier = evidence.KeyIdentifier,
        evidence_identifier = evidence.EvidenceIdentifier,
        evidence_sha256 = Convert.ToHexStringLower(evidence.EvidenceSha256),
        evidence_issued_on = evidence.IssuedOn,
        evidence_expires_on = evidence.ExpiresOn,
        details = specifics,
    };
}
