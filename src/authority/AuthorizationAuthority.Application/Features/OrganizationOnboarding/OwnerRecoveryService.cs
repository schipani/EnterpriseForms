using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AuthorizationAuthority.Application.Common;
using AuthorizationAuthority.Application.Common.Ports;
using AuthorizationAuthority.Application.Features.Journal;
using AuthorizationAuthority.Application.Features.PlatformOperators;
using AuthorizationAuthority.Application.Features.SecurityCommands;
using AuthorizationAuthority.Contracts.V1;
using AuthorizationAuthority.Domain.Features.Journal;
using AuthorizationAuthority.Domain.Features.Organizations;
using AuthorizationAuthority.Domain.Features.PlatformOperators;
using AuthorizationAuthority.Domain.Features.SecurityCommands;

namespace AuthorizationAuthority.Application.Features.OrganizationOnboarding;

/// <summary>
/// C8 owner recovery (C-105; D8-54…D8-57 as directed). Restores the owner quorum of an existing organization that has fewer than two
/// eligible owners, without bypassing anything else.
/// <list type="bullet">
/// <item><b>Eligibility</b> from the Authority's own state: enabled owners minus owners declared unavailable must be below two. A disabled
/// owner is ineligible by state; a declared-unavailable owner needs a reason, an evidence reference and an evidence summary inside the
/// signed statement, and execution is refused if that owner has signed in since the case was opened (not an operator's word alone).</item>
/// <item><b>Open</b> (any active operator who does not belong to the organization; grants nothing): incident reference, justification,
/// declared owners and 1–3 replacement owners (existing enabled members or new identities from the organization's issuer, never an
/// operator identity). The case is immutable; any change of the organization's owner set invalidates it. Waiting period from opening.</item>
/// <item><b>Three approvals</b> by three distinct active operators, each with their own evidence over the identical statement.</item>
/// <item><b>Objection</b> by any enabled owner or Security Administrator (business plane, step-up) ends the case until execution.</item>
/// <item><b>Execute</b> after three approvals, the waiting period (trusted clock) and no objection: one AuthorityDb transaction adds the
/// replacement owners' owner role (and bindings for new identities) as revision N+1 and records probations. Nothing is removed or
/// disabled; no team, record, share, designer or Security Administrator change.</item>
/// </list>
/// Every change is a platform journal event (replayable) with an organization audit-stream mirror; every decision first checks the
/// independent archive and fails closed. Lock order: organization row, platform journal head, case row.
/// </summary>
public sealed partial class OwnerRecoveryService(
    IAuthorityStore store,
    PlatformOperatorPolicy platformPolicy,
    OrganizationOnboardingPolicy policy,
    PlatformOperatorEvidenceVerifier evidenceVerifier,
    PlatformJournal journal,
    FieldSearchApprovalGate stepUp,
    ISecurityAlertSink alerts,
    TimeProvider clock)
{
    public const int StatementVersion = 1;
    public const int RequiredApprovals = 3;
    public const int MaximumReplacements = 3;
    public const string RecoverCommandKind = "RecoverOrganizationOwners";
    private static readonly string[] DetailDisclosure = ["recovery_case", "owner_identities", "unavailability_evidence", "approvals"];
    private static readonly string[] MemberDisclosure = ["member_identities"];

    // ---- open ----

    public async Task<OwnerRecoveryCreateResponse> CreateAsync(VerifiedActor actor, OwnerRecoveryCreateRequest input, Guid correlationId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        RequireAvailable();
        var key = input.IdempotencyKey?.Trim() ?? string.Empty;
        if (!input.DryRun && !IdempotencyKeyPattern().IsMatch(key))
        {
            throw AuthorityException.Invalid("idempotency_key must be 8..128 characters (letters, digits, '.', '_', ':', '-')");
        }

        await using var scope = await store.BeginScopeAsync(cancellationToken).ConfigureAwait(false);
        var state = await scope.LockOrganizationAsync(input.OrganizationId, cancellationToken).ConfigureAwait(false)
                    ?? throw new AuthorityException(AuthorityErrorCodes.NotFound, 404, false, "no such organization");
        var head = await PlatformJournal.LockHeadAsync(scope, clock, cancellationToken).ConfigureAwait(false);
        await journal.EnsureCurrentAsync(scope, head, requireAnchored: false, correlationId, cancellationToken).ConfigureAwait(false);
        var me = await PlatformOperatorAccess.ResolveActiveAsync(scope, actor, cancellationToken).ConfigureAwait(false);
        var exact = clock.GetUtcNow();
        var now = new DateTimeOffset(exact.UtcTicks - (exact.UtcTicks % 10), TimeSpan.Zero); // the statement freezes these instants (PostgreSQL microseconds)
        var warnings = new List<string>();
        try
        {
            var org = await OrganizationViewAsync(scope, state, cancellationToken).ConfigureAwait(false);
            RequireNotQuarantined(state);
            RequireNoConflict(me, org.Bindings);
            var draft = await ValidateAsync(scope, input, org, me, cancellationToken).ConfigureAwait(false);
            var digest = CanonicalJson.Digest(CanonicalJson.Serialize(draft));
            if (!input.DryRun && await scope.FindOwnerRecoveryCaseByIdempotencyKeyAsync(me.PlatformOperatorId, key, cancellationToken).ConfigureAwait(false) is { } previous)
            {
                if (!previous.CreateRequestDigest.AsSpan().SequenceEqual(digest))
                {
                    throw new AuthorityException(AuthorityErrorCodes.IdempotencyConflict, 409, false, "this idempotency key was already used for a different recovery case");
                }

                return new OwnerRecoveryCreateResponse(false, true, null, null, [], await DetailAsync(scope, previous, me, now, correlationId, cancellationToken).ConfigureAwait(false), true, correlationId);
            }

            var existing = await scope.FindOpenOwnerRecoveryCaseAsync(state.OrganizationId, cancellationToken).ConfigureAwait(false);
            if (existing is not null && existing.CaseExpiresOn > now)
            {
                throw new AuthorityException(AuthorityErrorCodes.OwnerRecoveryCaseExists, 409, false, "the organization already has an open recovery case");
            }

            if (draft.EligibleAfter < 3)
            {
                warnings.Add($"The case leaves {draft.EligibleAfter} eligible owners: three are recommended, because with two owners neither can later be removed or disabled.");
            }

            if (input.DryRun)
            {
                return new OwnerRecoveryCreateResponse(true, true, null, null, warnings, null, false, correlationId);
            }

            var recoveryCase = new OrganizationOwnerRecoveryCase
            {
                OrganizationOwnerRecoveryCaseId = Guid.CreateVersion7(),
                OrganizationId = state.OrganizationId,
                IncidentReferenceText = draft.Incident,
                RecoveryReasonText = draft.Justification,
                OpenedByPlatformOperatorId = me.PlatformOperatorId,
                CreateIdempotencyKey = key,
                CreateRequestDigest = digest,
                OwnerSetSha256 = OwnerSetDigest(org),
                CaseOpenedOn = now,
                WaitingPeriodEndsOn = now + policy.RecoveryWaitingPeriod,
                CaseExpiresOn = now + policy.RecoveryWaitingPeriod + policy.RecoveryCaseLifetime,
            };
            var subjects = draft.Declared.Select((d, i) => new OrganizationOwnerRecoverySubject
            {
                OrganizationOwnerRecoverySubjectId = Guid.CreateVersion7(),
                OrganizationOwnerRecoveryCaseId = recoveryCase.OrganizationOwnerRecoveryCaseId,
                RecoverySubjectRoleName = OwnerRecoverySubjectRoles.DeclaredUnavailable,
                SubjectOrdinalNumber = i + 1,
                SubjectAuthorityUserId = d.Binding.AuthorityUserId,
                SubjectIssuerUri = d.Binding.IdentityIssuerUri,
                SubjectIdentifier = d.Binding.IdentitySubjectIdentifier,
                SubjectDisplayNameHint = d.Binding.IdentitySubjectIdentifier,
                UnavailabilityReasonName = d.Reason,
                EvidenceReferenceText = d.EvidenceReference,
                EvidenceSummaryText = d.EvidenceSummary,
            }).Concat(draft.Replacements.Select((r, i) => new OrganizationOwnerRecoverySubject
            {
                OrganizationOwnerRecoverySubjectId = Guid.CreateVersion7(),
                OrganizationOwnerRecoveryCaseId = recoveryCase.OrganizationOwnerRecoveryCaseId,
                RecoverySubjectRoleName = OwnerRecoverySubjectRoles.Replacement,
                SubjectOrdinalNumber = i + 1,
                SubjectAuthorityUserId = r.ExistingUserId ?? Guid.CreateVersion7(),
                SubjectIssuerUri = r.IssuerUri,
                SubjectIdentifier = r.SubjectIdentifier,
                SubjectDisplayNameHint = r.DisplayNameHint,
                IsNewIdentity = r.ExistingUserId is null,
                VerificationMethodName = r.VerificationMethod,
                VerificationReferenceText = r.VerificationReference,
            })).ToList();
            var (statementJson, statementDigest) = Statement(recoveryCase, subjects, org, state.OrganizationDisplayName);
            recoveryCase.RecoveryStatementJson = statementJson;
            recoveryCase.RecoveryStatementSha256 = statementDigest;

            var closedExpired = new List<OrganizationOwnerRecoveryCase>();
            if (existing is not null)
            {
                Close(existing, OwnerRecoveryStates.Expired, OwnerRecoveryClosureReasons.Expired, null, null, now);
                closedExpired.Add(existing);
                await scope.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            scope.AddOwnerRecoveryCase(recoveryCase);
            foreach (var subject in subjects)
            {
                scope.AddOwnerRecoverySubject(subject);
            }

            var sha = Convert.ToHexStringLower(statementDigest);
            PlatformJournal.Append(scope, head, "OwnerRecoveryOpened", new
            {
                organization_owner_recovery_case_id = recoveryCase.OrganizationOwnerRecoveryCaseId,
                organization_id = state.OrganizationId,
                opened_by_platform_operator_id = me.PlatformOperatorId,
                incident_reference = draft.Incident,
                statement_sha256 = sha,
                waiting_period_ends_on = recoveryCase.WaitingPeriodEndsOn,
                expired_case_ids = closedExpired.Select(c => c.OrganizationOwnerRecoveryCaseId),
                correlation_id = correlationId,
            }, PlatformStateDelta.Recovery([.. closedExpired, recoveryCase], subjects), now);
            // The organization's notice: its owners and Security Administrators see the case (and may object) on the business side.
            AuditJournal.Append(scope, state, "OwnerRecoveryOpened", new
            {
                organization_owner_recovery_case_id = recoveryCase.OrganizationOwnerRecoveryCaseId,
                incident_reference = draft.Incident,
                declared_unavailable_user_ids = draft.Declared.Select(d => d.Binding.AuthorityUserId),
                replacement_user_ids = subjects.Where(s => s.RecoverySubjectRoleName == OwnerRecoverySubjectRoles.Replacement).Select(s => s.SubjectAuthorityUserId),
                waiting_period_ends_on = recoveryCase.WaitingPeriodEndsOn,
                statement_sha256 = sha,
                notification = "in-app notice to enabled owners and Security Administrators; no out-of-band delivery exists",
                correlation_id = correlationId,
            }, clock);
            await scope.CommitAsync(cancellationToken).ConfigureAwait(false);
            alerts.Raise(new SecurityAlert("OwnerRecoveryOpened", "Critical", state.OrganizationId, recoveryCase.OrganizationOwnerRecoveryCaseId, null,
                $"owner recovery case opened for organization {state.OrganizationId} (incident {draft.Incident}) by operator {me.PlatformOperatorId}", correlationId));
            var detail = await DetailAsync(scope, recoveryCase, me, now, correlationId, cancellationToken).ConfigureAwait(false);
            await TryDeliverAsync(cancellationToken).ConfigureAwait(false);
            return new OwnerRecoveryCreateResponse(false, true, null, null, warnings, detail, false, correlationId);
        }
        catch (AuthorityException ex) when (input.DryRun)
        {
            return new OwnerRecoveryCreateResponse(true, false, ex.ErrorCode, ex.Detail, warnings, null, false, correlationId);
        }
        catch (AuthorityException ex)
        {
            await PlatformOperatorRegistryService.RecordRefusalAsync(scope, head, me, PlatformDecisions.Create, PlatformOperationKinds.OrganizationOwnerRecovery, Guid.Empty, ex, correlationId, now, cancellationToken).ConfigureAwait(false);
            throw;
        }
    }

    // ---- decisions ----

    public Task<OwnerRecoveryDecisionResponse> ApproveAsync(VerifiedActor actor, Guid caseId, OwnerRecoveryDecisionRequest input, Guid correlationId, CancellationToken cancellationToken) =>
        DecideAsync(actor, caseId, input, PlatformDecisions.Approve, grantDirection: true, correlationId, ApproveBodyAsync, cancellationToken);

    public Task<OwnerRecoveryDecisionResponse> WithdrawAsync(VerifiedActor actor, Guid caseId, OwnerRecoveryDecisionRequest input, Guid correlationId, CancellationToken cancellationToken) =>
        DecideAsync(actor, caseId, input, PlatformDecisions.Withdraw, grantDirection: false, correlationId, WithdrawBodyAsync, cancellationToken);

    public Task<OwnerRecoveryDecisionResponse> CancelAsync(VerifiedActor actor, Guid caseId, OwnerRecoveryDecisionRequest input, Guid correlationId, CancellationToken cancellationToken) =>
        DecideAsync(actor, caseId, input, PlatformDecisions.Cancel, grantDirection: false, correlationId, CancelBodyAsync, cancellationToken);

    public Task<OwnerRecoveryDecisionResponse> ExecuteAsync(VerifiedActor actor, Guid caseId, OwnerRecoveryDecisionRequest input, Guid correlationId, CancellationToken cancellationToken) =>
        DecideAsync(actor, caseId, input, PlatformDecisions.Execute, grantDirection: true, correlationId, ExecuteBodyAsync, cancellationToken);

    private sealed record DecisionContext(IAuthorityScope Scope, PlatformJournalHead Head, OrganizationAuthorizationState State, OrganizationView Org, PlatformOperator Me, OrganizationOwnerRecoveryCase Case,
        List<OrganizationOwnerRecoverySubject> Subjects, List<OrganizationOwnerRecoveryApproval> Approvals, string StatementJson, string StatementSha256, string Justification, OwnerRecoveryDecisionRequest Input,
        string Decision, DateTimeOffset Now, Guid CorrelationId);

    /// <summary>A decision's effect. With a refusal it is a recorded invalidation (committed) after which the refusal is thrown.</summary>
    private sealed record DecisionOutcome(string Outcome, string EventKind, object Detail, PlatformStateDelta Delta, Guid? SecurityCommandId, SecurityAlert? Alert = null, object? OrganizationAuditDetail = null);

    private async Task<OwnerRecoveryDecisionResponse> DecideAsync(VerifiedActor actor, Guid caseId, OwnerRecoveryDecisionRequest input, string decision, bool grantDirection, Guid correlationId,
        Func<DecisionContext, CancellationToken, Task<(AuthorityException? Refusal, DecisionOutcome? Outcome)>> body, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        RequireAvailable();
        var justification = PlatformOperatorAccess.RequireJustification(input.Justification);
        if (grantDirection)
        {
            await TryDeliverAsync(cancellationToken).ConfigureAwait(false);
        }

        OwnerRecoveryDecisionResponse response;
        SecurityAlert? alert = null;
        await using (var scope = await store.BeginScopeAsync(cancellationToken).ConfigureAwait(false))
        {
            var located = await scope.FindOwnerRecoveryCaseAsync(caseId, lockForUpdate: false, cancellationToken).ConfigureAwait(false)
                          ?? throw new AuthorityException(AuthorityErrorCodes.NotFound, 404, false, "no such recovery case");
            var state = await scope.LockOrganizationAsync(located.OrganizationId, cancellationToken).ConfigureAwait(false)
                        ?? throw new AuthorityException(AuthorityErrorCodes.NotFound, 404, false, "no such organization");
            var head = await PlatformJournal.LockHeadAsync(scope, clock, cancellationToken).ConfigureAwait(false);
            await journal.EnsureCurrentAsync(scope, head, grantDirection, correlationId, cancellationToken).ConfigureAwait(false);
            var me = await PlatformOperatorAccess.ResolveActiveAsync(scope, actor, cancellationToken).ConfigureAwait(false);
            var now = clock.GetUtcNow();
            var recoveryCase = await scope.FindOwnerRecoveryCaseAsync(caseId, lockForUpdate: true, cancellationToken).ConfigureAwait(false) ?? throw new AuthorityException(AuthorityErrorCodes.NotFound, 404, false);
            var subjects = await scope.GetOwnerRecoverySubjectsAsync(caseId, cancellationToken).ConfigureAwait(false);
            var approvals = await scope.GetOwnerRecoveryApprovalsAsync(caseId, cancellationToken).ConfigureAwait(false);
            var org = await OrganizationViewAsync(scope, state, cancellationToken).ConfigureAwait(false);
            var (statementJson, statementDigest) = StatementFromRows(recoveryCase, subjects, state.OrganizationDisplayName);
            var statementSha256 = Convert.ToHexStringLower(statementDigest);
            var context = new DecisionContext(scope, head, state, org, me, recoveryCase, subjects, approvals, statementJson, statementSha256, justification, input, decision, now, correlationId);
            AuthorityException? refusal;
            DecisionOutcome? outcome;
            try
            {
                if (!statementDigest.AsSpan().SequenceEqual(recoveryCase.RecoveryStatementSha256))
                {
                    alerts.Raise(new SecurityAlert("OwnerRecoveryStatementMismatch", "Critical", state.OrganizationId, caseId, null, "recovery case rows do not reproduce the frozen statement", correlationId));
                    throw new AuthorityException(AuthorityErrorCodes.ApprovalStatementChanged, 409, false, "the recovery case no longer matches its frozen statement");
                }

                if (!input.DryRun && !string.Equals(input.ExpectedStatementSha256?.Trim(), statementSha256, StringComparison.OrdinalIgnoreCase))
                {
                    throw new AuthorityException(AuthorityErrorCodes.ApprovalStatementChanged, 409, false, "the case is not the one that was reviewed; review it again");
                }

                (refusal, outcome) = await body(context, cancellationToken).ConfigureAwait(false);
                if (refusal is not null && outcome is not null)
                {
                    // A recorded invalidation: committed, alerted, then refused.
                    PlatformJournal.Append(scope, head, outcome.EventKind, outcome.Detail, outcome.Delta, now);
                    if (outcome.OrganizationAuditDetail is not null)
                    {
                        AuditJournal.Append(scope, state, outcome.EventKind, outcome.OrganizationAuditDetail, clock);
                    }

                    await scope.CommitAsync(cancellationToken).ConfigureAwait(false);
                    if (outcome.Alert is not null)
                    {
                        alerts.Raise(outcome.Alert);
                    }

                    await TryDeliverAsync(cancellationToken).ConfigureAwait(false);
                    throw refusal;
                }

                if (refusal is not null && !input.DryRun)
                {
                    throw refusal;
                }

                if (input.DryRun)
                {
                    return Response(true, refusal, recoveryCase, decision, statementJson, statementSha256, refusal is null ? "Eligible" : "Refused", approvals, null, false, now, correlationId);
                }
            }
            catch (AuthorityException ex) when (recoveryCase.RecoveryStateName == OwnerRecoveryStates.Open)
            {
                await RecordRefusalAsync(context, decision, ex, cancellationToken).ConfigureAwait(false);
                throw;
            }

            if (outcome!.Outcome == "Replayed")
            {
                return Response(false, null, recoveryCase, decision, statementJson, statementSha256, "Replayed", approvals, recoveryCase.ExecutedBySecurityCommandId, true, now, correlationId);
            }

            PlatformJournal.Append(scope, head, outcome.EventKind, outcome.Detail, outcome.Delta, now);
            if (outcome.OrganizationAuditDetail is not null)
            {
                AuditJournal.Append(scope, state, outcome.EventKind, outcome.OrganizationAuditDetail, clock);
            }

            await scope.CommitAsync(cancellationToken).ConfigureAwait(false);
            alert = outcome.Alert;
            var current = await scope.GetOwnerRecoveryApprovalsAsync(caseId, cancellationToken).ConfigureAwait(false);
            response = Response(false, null, recoveryCase, decision, statementJson, statementSha256, outcome.Outcome, current, outcome.SecurityCommandId, false, now, correlationId);
        }

        if (alert is not null)
        {
            alerts.Raise(alert);
        }

        var archiveState = await TryDeliverAsync(cancellationToken).ConfigureAwait(false);
        return response with { ArchiveState = archiveState };

    }

    private async Task<(AuthorityException?, DecisionOutcome?)> ApproveBodyAsync(DecisionContext c, CancellationToken cancellationToken)
    {
        var refusal = CommonRefusal(c);
        if (refusal is not null)
        {
            return refusal.Value;
        }

        if (c.Approvals.Any(a => a.PlatformOperatorId == c.Me.PlatformOperatorId && a.ApprovalStateName is OnboardingApprovalStates.Valid or OnboardingApprovalStates.Consumed))
        {
            return (new AuthorityException(AuthorityErrorCodes.OperatorAlreadyApproved, 409, false, "you already approved this case; two further different operators must approve it"), null);
        }

        if (c.Input.DryRun)
        {
            return (null, null);
        }

        var evidence = VerifyEvidence(c, PlatformDecisions.Approve);
        await PlatformOperatorAccess.RequireUnusedAsync(c.Scope, evidence, cancellationToken).ConfigureAwait(false);
        var stale = await StaleAsync(c, cancellationToken).ConfigureAwait(false);
        var approval = new OrganizationOwnerRecoveryApproval
        {
            OrganizationOwnerRecoveryApprovalId = Guid.CreateVersion7(),
            OrganizationOwnerRecoveryCaseId = c.Case.OrganizationOwnerRecoveryCaseId,
            PlatformOperatorId = c.Me.PlatformOperatorId,
            ApprovalStatementSha256 = Convert.FromHexString(c.StatementSha256),
            ApprovalJustificationText = c.Justification,
            ApprovalEvidenceKindName = evidence.EvidenceKind,
            ApprovalEvidenceSha256 = evidence.EvidenceSha256,
            ApprovalEvidenceIdentifier = evidence.EvidenceIdentifier,
            ApprovalRecordedOn = c.Now,
        };
        await c.Scope.FlushAsync(cancellationToken).ConfigureAwait(false);
        c.Scope.AddOwnerRecoveryApproval(approval);
        var use = PlatformOperatorAccess.EvidenceUse(evidence, c.Me.PlatformOperatorId, PlatformDecisions.Approve, c.Now);
        c.Scope.AddPlatformEvidenceUse(use);
        c.Case.CaseRowRevision++;
        var valid = c.Approvals.Count(a => a.ApprovalStateName == OnboardingApprovalStates.Valid) + 1;
        var outcome = valid >= RequiredApprovals ? "ApprovalsComplete" : "ApprovalRecorded";
        return (null, new DecisionOutcome(outcome, outcome == "ApprovalsComplete" ? "OwnerRecoveryApprovalsComplete" : "OwnerRecoveryApprovalRecorded",
            Detail(c, PlatformDecisions.Approve, evidence, new { valid_approvals = valid, invalidated_approvals = stale.Select(a => a.OrganizationOwnerRecoveryApprovalId) }),
            PlatformStateDelta.Recovery([c.Case], approvals: [.. stale, approval], evidenceUses: [use]), null, null,
            new { organization_owner_recovery_case_id = c.Case.OrganizationOwnerRecoveryCaseId, valid_approvals = valid, required_approvals = RequiredApprovals, correlation_id = c.CorrelationId }));
    }

    private async Task<(AuthorityException?, DecisionOutcome?)> WithdrawBodyAsync(DecisionContext c, CancellationToken cancellationToken)
    {
        var mine = c.Approvals.FirstOrDefault(a => a.PlatformOperatorId == c.Me.PlatformOperatorId && a.ApprovalStateName == OnboardingApprovalStates.Valid);
        AuthorityException? refusal = c.Case.RecoveryStateName != OwnerRecoveryStates.Open
            ? NotOpen(c.Case)
            : mine is null ? new AuthorityException(AuthorityErrorCodes.NoPendingApproval, 409, false, "you have no approval of this case to withdraw") : null;
        if (refusal is not null || c.Input.DryRun)
        {
            return (refusal, null);
        }

        var evidence = VerifyEvidence(c, PlatformDecisions.Withdraw);
        await PlatformOperatorAccess.RequireUnusedAsync(c.Scope, evidence, cancellationToken).ConfigureAwait(false);
        CloseApproval(mine!, OnboardingApprovalStates.Withdrawn, PlatformApprovalClosureReasons.WithdrawnByOperator, c.Now);
        var use = PlatformOperatorAccess.EvidenceUse(evidence, c.Me.PlatformOperatorId, PlatformDecisions.Withdraw, c.Now);
        c.Scope.AddPlatformEvidenceUse(use);
        c.Case.CaseRowRevision++;
        return (null, new DecisionOutcome("ApprovalWithdrawn", "OwnerRecoveryApprovalWithdrawn", Detail(c, PlatformDecisions.Withdraw, evidence, new { withdrawn_approval_id = mine!.OrganizationOwnerRecoveryApprovalId }),
            PlatformStateDelta.Recovery([c.Case], approvals: [mine], evidenceUses: [use]), null));
    }

    private async Task<(AuthorityException?, DecisionOutcome?)> CancelBodyAsync(DecisionContext c, CancellationToken cancellationToken)
    {
        AuthorityException? refusal = c.Case.RecoveryStateName != OwnerRecoveryStates.Open ? NotOpen(c.Case) : Conflict(c);
        if (refusal is not null || c.Input.DryRun)
        {
            return (refusal, null);
        }

        var evidence = VerifyEvidence(c, PlatformDecisions.Cancel);
        await PlatformOperatorAccess.RequireUnusedAsync(c.Scope, evidence, cancellationToken).ConfigureAwait(false);
        var closing = CloseOpenApprovals(c, PlatformApprovalClosureReasons.RequestCancelled);
        Close(c.Case, OwnerRecoveryStates.Cancelled, OwnerRecoveryClosureReasons.Cancelled, c.Me.PlatformOperatorId, c.Justification, c.Now);
        var use = PlatformOperatorAccess.EvidenceUse(evidence, c.Me.PlatformOperatorId, PlatformDecisions.Cancel, c.Now);
        c.Scope.AddPlatformEvidenceUse(use);
        return (null, new DecisionOutcome("Cancelled", "OwnerRecoveryCancelled", Detail(c, PlatformDecisions.Cancel, evidence, new { invalidated_approvals = closing.Select(a => a.OrganizationOwnerRecoveryApprovalId) }),
            PlatformStateDelta.Recovery([c.Case], approvals: closing, evidenceUses: [use]), null, null,
            new { organization_owner_recovery_case_id = c.Case.OrganizationOwnerRecoveryCaseId, closure = "Cancelled", correlation_id = c.CorrelationId }));
    }

    private async Task<(AuthorityException?, DecisionOutcome?)> ExecuteBodyAsync(DecisionContext c, CancellationToken cancellationToken)
    {
        if (c.Case.RecoveryStateName == OwnerRecoveryStates.Recovered)
        {
            return (null, new DecisionOutcome("Replayed", string.Empty, new { }, PlatformStateDelta.Recovery(), c.Case.ExecutedBySecurityCommandId));
        }

        var refusal = CommonRefusal(c);
        if (refusal is not null)
        {
            return refusal.Value;
        }

        var active = await ActiveOperatorIdsAsync(c.Scope, cancellationToken).ConfigureAwait(false);
        var valid = c.Approvals.Where(a => a.ApprovalStateName == OnboardingApprovalStates.Valid && active.Contains(a.PlatformOperatorId)).Select(a => a.PlatformOperatorId).Distinct().Count();
        if (valid < RequiredApprovals)
        {
            return (new AuthorityException(AuthorityErrorCodes.OwnerRecoveryApprovalsIncomplete, 409, false, $"{valid} of {RequiredApprovals} approvals by distinct active operators"), null);
        }

        if (c.Now < c.Case.WaitingPeriodEndsOn)
        {
            return (new AuthorityException(AuthorityErrorCodes.OwnerRecoveryWaitingPeriod, 409, false, $"the waiting period ends at {c.Case.WaitingPeriodEndsOn:O} (Authority clock); recovery cannot be executed before then"), null);
        }

        // Evidence that a declared-unavailable owner is in fact active: a session since the case was opened.
        foreach (var declared in c.Subjects.Where(s => s.RecoverySubjectRoleName == OwnerRecoverySubjectRoles.DeclaredUnavailable))
        {
            var binding = c.Org.Bindings.Single(b => b.AuthorityUserId == declared.SubjectAuthorityUserId);
            if (await c.Scope.CountSessionsCreatedSinceAsync(binding.IdentityBindingId, c.Case.CaseOpenedOn, cancellationToken).ConfigureAwait(false) > 0)
            {
                return Invalidate(c, OwnerRecoveryClosureReasons.DeclaredOwnerActive, new AuthorityException(AuthorityErrorCodes.OwnerRecoveryDeclaredOwnerActive, 409, false,
                    "an owner declared unavailable has signed in since the case was opened; the case is invalidated"));
            }
        }

        foreach (var replacement in c.Subjects.Where(s => s.RecoverySubjectRoleName == OwnerRecoverySubjectRoles.Replacement))
        {
            var bound = c.Org.Bindings.FirstOrDefault(b => b.IdentityIssuerUri == replacement.SubjectIssuerUri && b.IdentitySubjectIdentifier == replacement.SubjectIdentifier);
            var conflict = replacement.IsNewIdentity
                ? bound is not null || c.Org.Bindings.Any(b => b.AuthorityUserId == replacement.SubjectAuthorityUserId)
                : bound is null || bound.AuthorityUserId != replacement.SubjectAuthorityUserId || !bound.IdentityBindingEnabled;
            if (conflict)
            {
                return Invalidate(c, OwnerRecoveryClosureReasons.IdentityConflict, new AuthorityException(AuthorityErrorCodes.OwnerRecoveryIdentityConflict, 409, false,
                    "a replacement identity is no longer what the case recorded (bound, unbound or disabled since); the case is invalidated"));
            }

            if (await c.Scope.FindPlatformOperatorByIdentityAsync(replacement.SubjectIssuerUri, replacement.SubjectIdentifier, cancellationToken).ConfigureAwait(false) is not null)
            {
                return (new AuthorityException(AuthorityErrorCodes.OrganizationOwnerIsOperator, 403, false, "a Platform Operator identity can never become an owner"), null);
            }
        }

        if (c.Input.DryRun)
        {
            return (null, null);
        }

        var evidence = VerifyEvidence(c, PlatformDecisions.Execute);
        await PlatformOperatorAccess.RequireUnusedAsync(c.Scope, evidence, cancellationToken).ConfigureAwait(false);
        var stale = await StaleAsync(c, cancellationToken).ConfigureAwait(false);
        var now = c.Now;
        var organizationId = c.State.OrganizationId;
        var replacements = c.Subjects.Where(s => s.RecoverySubjectRoleName == OwnerRecoverySubjectRoles.Replacement).ToList();
        var newBindings = replacements.Where(r => r.IsNewIdentity).Select(r => new IdentityBinding
        {
            IdentityBindingId = Guid.CreateVersion7(),
            OrganizationId = organizationId,
            AuthorityUserId = r.SubjectAuthorityUserId,
            IdentityIssuerUri = r.SubjectIssuerUri,
            IdentitySubjectIdentifier = r.SubjectIdentifier,
            IdentityBindingEnabled = true,
            IdentityCreatedOn = now,
            OrganizationOwnerRecoveryCaseId = c.Case.OrganizationOwnerRecoveryCaseId,
        }).ToList();
        foreach (var binding in newBindings)
        {
            c.Scope.AddIdentityBinding(binding);
        }

        var actorBinding = newBindings.FirstOrDefault() ?? c.Org.Bindings.Single(b => b.AuthorityUserId == replacements[0].SubjectAuthorityUserId);
        var consumed = c.Approvals.Where(a => a.ApprovalStateName == OnboardingApprovalStates.Valid).ToList();
        foreach (var approval in consumed)
        {
            CloseApproval(approval, OnboardingApprovalStates.Consumed, null, now);
        }

        var payloadJson = CanonicalJson.Serialize(new RecoverPayload(c.Case.OrganizationOwnerRecoveryCaseId, c.Case.IncidentReferenceText, c.StatementSha256,
            replacements.Select(r => new RecoverOwner(r.SubjectAuthorityUserId, r.SubjectIssuerUri, r.SubjectIdentifier, r.IsNewIdentity)).ToList(),
            consumed.Select(a => new RecoverApproval(a.PlatformOperatorId, Convert.ToHexStringLower(a.ApprovalEvidenceSha256))).ToList(), c.Me.PlatformOperatorId, platformPolicy.Assurance));
        var command = new SecurityCommand
        {
            SecurityCommandId = Guid.CreateVersion7(),
            OrganizationId = organizationId,
            ActorIdentityBindingId = actorBinding.IdentityBindingId,
            CommandKindName = RecoverCommandKind,
            CommandIdempotencyKey = "owner-recovery:" + c.Case.OrganizationOwnerRecoveryCaseId.ToString("N"),
            CommandRequestDigest = Convert.FromHexString(c.StatementSha256),
            ExpectedAuthorizationRevision = c.State.AuthorizationRevision,
            CommandPayloadJson = payloadJson,
            CommandReasonText = "owner recovery " + c.Case.IncidentReferenceText,
            CorrelationIdentifier = c.CorrelationId,
            CommandCreatedOn = now,
            CommandModifiedOn = now,
        };
        // Add-only: the owner role for each replacement and nothing else (no removal, no team, record, share or other role change).
        var plan = new CommandPlan { NormalizedPayloadJson = payloadJson };
        foreach (var replacement in replacements)
        {
            plan.AddEdges.Add((ResourceTypeNames.Organization, organizationId, OrganizationRoleNames.OrganizationOwner, ResourceTypeNames.User, replacement.SubjectAuthorityUserId, string.Empty));
        }

        var actor = new BoundActor(new VerifiedActor(c.Me.OperatorIssuerUri, c.Me.OperatorSubjectIdentifier, "platform-console"), actorBinding);
        var commandRequest = new SecurityCommandRequest(organizationId, SecurityCommandKind.GrantOrganizationRole, new ResourceRef(ResourceKind.Organization, organizationId), c.State.AuthorizationRevision,
            JsonDocument.Parse(payloadJson).RootElement, command.CommandReasonText);
        SecurityCommandService.ApplyPlan(c.Scope, c.State, command, plan, actor, commandRequest, cancellationToken);

        var probations = replacements.Select(r => new OrganizationRecoveredOwnerProbation
        {
            OrganizationRecoveredOwnerProbationId = Guid.CreateVersion7(),
            OrganizationId = organizationId,
            AuthorityUserId = r.SubjectAuthorityUserId,
            OrganizationOwnerRecoveryCaseId = c.Case.OrganizationOwnerRecoveryCaseId,
            ProbationCreatedOn = now,
            ProbationEndsOn = now + policy.RecoveredOwnerProbation,
        }).ToList();
        foreach (var probation in probations)
        {
            c.Scope.AddRecoveredOwnerProbation(probation);
        }

        c.Case.RecoveryStateName = OwnerRecoveryStates.Recovered;
        c.Case.RecoveredOn = now;
        c.Case.ExecutedByPlatformOperatorId = c.Me.PlatformOperatorId;
        c.Case.ExecutedBySecurityCommandId = command.SecurityCommandId;
        c.Case.CaseClosedOn = now;
        c.Case.LastExecutionFailureCode = null;
        c.Case.LastExecutionFailureOn = null;
        c.Case.CaseRowRevision++;
        var use = PlatformOperatorAccess.EvidenceUse(evidence, c.Me.PlatformOperatorId, PlatformDecisions.Execute, now);
        c.Scope.AddPlatformEvidenceUse(use);
        var alert = new SecurityAlert("OwnerRecoveryExecuted", "Critical", organizationId, c.Case.OrganizationOwnerRecoveryCaseId, command.SecurityCommandId,
            $"owner recovery executed for organization {organizationId}: {replacements.Count} owner(s) added ({platformPolicy.Assurance})", c.CorrelationId);
        return (null, new DecisionOutcome("Recovered", "OwnerRecoveryExecuted",
            Detail(c, PlatformDecisions.Execute, evidence, new { security_command_id = command.SecurityCommandId, replacement_user_ids = replacements.Select(r => r.SubjectAuthorityUserId), invalidated_approvals = stale.Select(a => a.OrganizationOwnerRecoveryApprovalId) }),
            PlatformStateDelta.Recovery([c.Case], approvals: [.. stale, .. consumed], probations: probations, evidenceUses: [use]), command.SecurityCommandId, alert,
            new
            {
                organization_owner_recovery_case_id = c.Case.OrganizationOwnerRecoveryCaseId,
                security_command_id = command.SecurityCommandId,
                incident_reference = c.Case.IncidentReferenceText,
                replacement_user_ids = replacements.Select(r => r.SubjectAuthorityUserId),
                new_binding_user_ids = newBindings.Select(b => b.AuthorityUserId),
                approved_by_platform_operator_ids = consumed.Select(a => a.PlatformOperatorId),
                executed_by_platform_operator_id = c.Me.PlatformOperatorId,
                probation_ends_on = now + policy.RecoveredOwnerProbation,
                statement_sha256 = c.StatementSha256,
                approval_assurance = platformPolicy.Assurance,
                correlation_id = c.CorrelationId,
            }));
    }

    /// <summary>Open, not expired, no conflict of interest, owner set unchanged since opening (else the case is invalidated) and still applicable.</summary>
    private static (AuthorityException?, DecisionOutcome?)? CommonRefusal(DecisionContext c)
    {
        if (c.Case.RecoveryStateName != OwnerRecoveryStates.Open)
        {
            return (NotOpen(c.Case), null);
        }

        if (c.Case.CaseExpiresOn <= c.Now)
        {
            return (new AuthorityException(AuthorityErrorCodes.OwnerRecoveryExpired, 409, false, "the case expired; open a new case"), null);
        }

        if (c.State.IntegrityQuarantineEnabled)
        {
            return (AuthorityException.Quarantined("organization quarantined"), null);
        }

        if (Conflict(c) is { } conflict)
        {
            return (conflict, null);
        }

        if (!OwnerSetDigest(c.Org).AsSpan().SequenceEqual(c.Case.OwnerSetSha256))
        {
            return Invalidate(c, OwnerRecoveryClosureReasons.OwnerSetChanged, new AuthorityException(AuthorityErrorCodes.OwnerRecoveryOwnerSetChanged, 409, false,
                "the organization's owners changed since the case was opened; the case is invalidated"));
        }

        var declared = c.Subjects.Where(s => s.RecoverySubjectRoleName == OwnerRecoverySubjectRoles.DeclaredUnavailable).Select(s => s.SubjectAuthorityUserId).ToHashSet();
        if (c.Org.EnabledOwners.Count(o => !declared.Contains(o)) >= 2)
        {
            return Invalidate(c, OwnerRecoveryClosureReasons.NoLongerApplicable, new AuthorityException(AuthorityErrorCodes.OwnerRecoveryNotApplicable, 409, false,
                "the organization has two eligible owners; use the normal two-owner administration"));
        }

        return null;
    }

    private static (AuthorityException?, DecisionOutcome?) Invalidate(DecisionContext c, string reason, AuthorityException refusal)
    {
        if (c.Input.DryRun)
        {
            return (refusal, null);
        }

        var closing = CloseOpenApprovals(c, PlatformApprovalClosureReasons.RequestChanged);
        Close(c.Case, OwnerRecoveryStates.Invalidated, reason, null, null, c.Now);
        var alert = new SecurityAlert("OwnerRecoveryInvalidated", "High", c.State.OrganizationId, c.Case.OrganizationOwnerRecoveryCaseId, null, $"owner recovery case invalidated ({reason})", c.CorrelationId);
        return (refusal, new DecisionOutcome("Invalidated", "OwnerRecoveryInvalidated",
            new { organization_owner_recovery_case_id = c.Case.OrganizationOwnerRecoveryCaseId, organization_id = c.State.OrganizationId, platform_operator_id = c.Me.PlatformOperatorId, decision = c.Decision, reason, correlation_id = c.CorrelationId },
            PlatformStateDelta.Recovery([c.Case], approvals: closing), null, alert,
            new { organization_owner_recovery_case_id = c.Case.OrganizationOwnerRecoveryCaseId, closure = "Invalidated", reason, correlation_id = c.CorrelationId }));
    }

    // ---- business plane: notice and objection ----

    public async Task<OwnerRecoveryNotice> GetNoticeAsync(VerifiedActor actor, OwnerRecoveryNoticeRequest input, Guid correlationId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        await using var scope = await store.BeginScopeAsync(cancellationToken).ConfigureAwait(false);
        var state = await scope.ReadOrganizationAsync(input.OrganizationId, cancellationToken).ConfigureAwait(false) ?? throw new AuthorityException(AuthorityErrorCodes.NotFound, 404, false);
        await RequireObjectorAsync(scope, state, actor, cancellationToken).ConfigureAwait(false);
        var now = clock.GetUtcNow();
        var open = await scope.FindOpenOwnerRecoveryCaseAsync(state.OrganizationId, cancellationToken).ConfigureAwait(false);
        if (open is null || open.CaseExpiresOn <= now)
        {
            return new OwnerRecoveryNotice(null, false, correlationId);
        }

        var subjects = await scope.GetOwnerRecoverySubjectsAsync(open.OrganizationOwnerRecoveryCaseId, cancellationToken).ConfigureAwait(false);
        var approvals = await scope.GetOwnerRecoveryApprovalsAsync(open.OrganizationOwnerRecoveryCaseId, cancellationToken).ConfigureAwait(false);
        return new OwnerRecoveryNotice(new OwnerRecoveryNoticeCase(open.OrganizationOwnerRecoveryCaseId, open.IncidentReferenceText, open.RecoveryReasonText, open.CaseOpenedOn, open.WaitingPeriodEndsOn,
            approvals.Count(a => a.ApprovalStateName == OnboardingApprovalStates.Valid), RequiredApprovals,
            subjects.Select(s => new OwnerRecoveryNoticeSubject(s.RecoverySubjectRoleName, s.SubjectAuthorityUserId, s.SubjectIdentifier, s.SubjectDisplayNameHint, s.IsNewIdentity, s.UnavailabilityReasonName)).ToList()),
            stepUp.ApprovalsOperational, correlationId);
    }

    public async Task<OwnerRecoveryObjectionResponse> ObjectAsync(VerifiedActor actor, OwnerRecoveryObjectionRequest input, Guid correlationId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        var reason = input.Reason?.Trim() ?? string.Empty;
        if (reason.Length is < 10 or > 500 || reason.Any(ch => char.IsControl(ch) && ch is not ('\n' or '\r' or '\t')))
        {
            throw AuthorityException.Invalid("an explanation of 10 to 500 characters is required");
        }

        OwnerRecoveryObjectionResponse response;
        SecurityAlert alert;
        await using (var scope = await store.BeginScopeAsync(cancellationToken).ConfigureAwait(false))
        {
            var state = await scope.LockOrganizationAsync(input.OrganizationId, cancellationToken).ConfigureAwait(false) ?? throw new AuthorityException(AuthorityErrorCodes.NotFound, 404, false);
            var bound = await RequireObjectorAsync(scope, state, actor, cancellationToken).ConfigureAwait(false);
            stepUp.RequireStepUp(input.StepUpEvidence);
            var head = await PlatformJournal.LockHeadAsync(scope, clock, cancellationToken).ConfigureAwait(false);
            await journal.EnsureCurrentAsync(scope, head, requireAnchored: false, correlationId, cancellationToken).ConfigureAwait(false);
            var now = clock.GetUtcNow();
            var recoveryCase = await scope.FindOwnerRecoveryCaseAsync(input.CaseId, lockForUpdate: true, cancellationToken).ConfigureAwait(false);
            if (recoveryCase is null || recoveryCase.OrganizationId != state.OrganizationId)
            {
                throw new AuthorityException(AuthorityErrorCodes.NotFound, 404, false);
            }

            if (recoveryCase.RecoveryStateName != OwnerRecoveryStates.Open || recoveryCase.CaseExpiresOn <= now)
            {
                throw NotOpen(recoveryCase);
            }

            var approvals = await scope.GetOwnerRecoveryApprovalsAsync(recoveryCase.OrganizationOwnerRecoveryCaseId, cancellationToken).ConfigureAwait(false);
            var closing = approvals.Where(a => a.ApprovalStateName == OnboardingApprovalStates.Valid).ToList();
            foreach (var approval in closing)
            {
                CloseApproval(approval, OnboardingApprovalStates.Invalidated, "Objected", now);
            }

            recoveryCase.RecoveryStateName = OwnerRecoveryStates.Objected;
            recoveryCase.ObjectedOn = now;
            recoveryCase.ObjectedByAuthorityUserId = bound.AuthorityUserId;
            recoveryCase.ObjectionReasonText = reason;
            recoveryCase.CaseClosedOn = now;
            recoveryCase.CaseClosureReasonName = OwnerRecoveryClosureReasons.Objected;
            recoveryCase.CaseRowRevision++;
            PlatformJournal.Append(scope, head, "OwnerRecoveryObjected", new
            {
                organization_owner_recovery_case_id = recoveryCase.OrganizationOwnerRecoveryCaseId,
                organization_id = state.OrganizationId,
                objected_by_authority_user_id = bound.AuthorityUserId,
                objector_issuer_uri = bound.IdentityIssuerUri,
                objector_subject_identifier = bound.IdentitySubjectIdentifier,
                reason,
                invalidated_approvals = closing.Select(a => a.OrganizationOwnerRecoveryApprovalId),
                correlation_id = correlationId,
            }, PlatformStateDelta.Recovery([recoveryCase], approvals: closing), now);
            AuditJournal.Append(scope, state, "OwnerRecoveryObjected", new
            {
                organization_owner_recovery_case_id = recoveryCase.OrganizationOwnerRecoveryCaseId,
                objected_by_authority_user_id = bound.AuthorityUserId,
                reason,
                correlation_id = correlationId,
            }, clock);
            await scope.CommitAsync(cancellationToken).ConfigureAwait(false);
            alert = new SecurityAlert("OwnerRecoveryObjected", "Critical", state.OrganizationId, recoveryCase.OrganizationOwnerRecoveryCaseId, null, $"owner recovery case objected by {bound.AuthorityUserId}; execution is blocked", correlationId);
            response = new OwnerRecoveryObjectionResponse(recoveryCase.OrganizationOwnerRecoveryCaseId, recoveryCase.RecoveryStateName, now, correlationId);
        }

        alerts.Raise(alert);
        await TryDeliverAsync(cancellationToken).ConfigureAwait(false);
        return response;
    }

    /// <summary>An enabled owner or Security Administrator of the organization; anyone else (another organization included) gets the not-found answer.</summary>
    private static async Task<IdentityBinding> RequireObjectorAsync(IAuthorityScope scope, OrganizationAuthorizationState state, VerifiedActor actor, CancellationToken cancellationToken)
    {
        BoundActor bound;
        try
        {
            bound = await ActorResolver.BindAsync(scope, state.OrganizationId, actor, cancellationToken).ConfigureAwait(false);
        }
        catch (AuthorityException ex) when (ex.ErrorCode == AuthorityErrorCodes.ActorNotBound)
        {
            throw new AuthorityException(AuthorityErrorCodes.NotFound, 404, false);
        }

        var graph = new DesiredStateGraph(await scope.GetActiveEdgesAsync(state.OrganizationId, cancellationToken).ConfigureAwait(false));
        if (!graph.HasOrganizationRole(state.OrganizationId, bound.AuthorityUserId, OrganizationRoleNames.OrganizationOwner) && !graph.HasOrganizationRole(state.OrganizationId, bound.AuthorityUserId, OrganizationRoleNames.SystemAdministrator))
        {
            throw new AuthorityException(AuthorityErrorCodes.NotFound, 404, false);
        }

        return bound.Binding;
    }

    // ---- replacement-owner member picker (C-106) ----

    /// <summary>
    /// The organization members who could be proposed as replacement owners: enabled, not already an owner, not a registered Platform
    /// Operator identity, and never reachable by a caller who is bound in the organization (refused, as for cases). Filtering, ordering and
    /// paging happen here; the browser never receives the rest. Discloses member identities, so each search is audited on the
    /// organization audit stream and committed before it is returned (fail closed). No roles, teams, records or documents.
    /// </summary>
    public async Task<OwnerRecoveryMemberPage> SearchMembersAsync(VerifiedActor actor, Guid organizationId, OwnerRecoveryMemberSearchRequest input, Guid correlationId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        RequireAvailable();
        var text = input.Query?.Trim();
        if (text is { Length: > 200 })
        {
            throw AuthorityException.Invalid("the search text is at most 200 characters");
        }

        var limit = input.Limit ?? OrganizationOnboardingService.DefaultPageSize;
        if (limit is < 1 or > OrganizationOnboardingService.MaximumPageSize)
        {
            throw AuthorityException.Invalid($"limit must be 1..{OrganizationOnboardingService.MaximumPageSize}");
        }

        OwnerRecoveryMemberPage page;
        await using (var scope = await store.BeginScopeAsync(cancellationToken).ConfigureAwait(false))
        {
            var state = await scope.LockOrganizationAsync(organizationId, cancellationToken).ConfigureAwait(false)
                        ?? throw new AuthorityException(AuthorityErrorCodes.NotFound, 404, false, "no such organization");
            var head = await scope.ReadPlatformJournalAsync(cancellationToken).ConfigureAwait(false) ?? new PlatformJournalHead();
            await journal.EnsureCurrentAsync(scope, head, requireAnchored: false, correlationId, cancellationToken).ConfigureAwait(false);
            var me = await PlatformOperatorAccess.ResolveActiveAsync(scope, actor, cancellationToken).ConfigureAwait(false);
            var org = await OrganizationViewAsync(scope, state, cancellationToken).ConfigureAwait(false);
            RequireNoConflict(me, org.Bindings);
            var operators = await scope.GetPlatformOperatorsAsync(cancellationToken).ConfigureAwait(false);
            var requests = await scope.GetMembershipRequestsAsync(state.OrganizationId, DateTimeOffset.MinValue, cancellationToken).ConfigureAwait(false);
            var hints = requests.Where(r => r.RequestStateName == MembershipRequestStates.Approved).GroupBy(r => r.RequestedAuthorityUserId).ToDictionary(g => g.Key, g => g.First().RequestedDisplayNameHint);
            if (state.OrganizationOnboardingRequestId is { } onboardingId)
            {
                foreach (var owner in await scope.GetOnboardingOwnersAsync(onboardingId, cancellationToken).ConfigureAwait(false))
                {
                    hints.TryAdd(owner.ReservedAuthorityUserId, owner.OwnerDisplayNameHint);
                }
            }

            var binding = OrganizationOnboardingService.Binding("recovery-members:" + organizationId.ToString("N"), me, string.Empty, text, limit);
            var candidates = org.Bindings
                .Where(b => b.IdentityBindingEnabled && !org.Owners.Contains(b.AuthorityUserId))
                .Where(b => !operators.Any(o => o.OperatorIssuerUri == b.IdentityIssuerUri && o.OperatorSubjectIdentifier == b.IdentitySubjectIdentifier))
                .Select(b => (Binding: b, Name: hints.TryGetValue(b.AuthorityUserId, out var hint) && !string.IsNullOrWhiteSpace(hint) ? hint : b.IdentitySubjectIdentifier))
                .Where(x => string.IsNullOrEmpty(text) || x.Name.Contains(text, StringComparison.OrdinalIgnoreCase) || x.Binding.IdentitySubjectIdentifier.Contains(text, StringComparison.OrdinalIgnoreCase))
                .Select(x => (x.Binding, x.Name, Key: x.Name.ToLowerInvariant()))
                .OrderBy(x => x.Key, StringComparer.Ordinal).ThenBy(x => x.Binding.AuthorityUserId)
                .ToList();
            var total = candidates.Count;
            if (input.Cursor is { Length: > 0 } cursor)
            {
                var (key, id) = OrganizationOnboardingService.ReadCursorParts(cursor, binding);
                candidates = candidates.Where(x => string.CompareOrdinal(x.Key, key) > 0 || (x.Key == key && x.Binding.AuthorityUserId.CompareTo(id) > 0)).ToList();
            }

            var slice = candidates.Take(limit).ToList();
            var next = candidates.Count > limit ? OrganizationOnboardingService.WriteCursor(slice[^1].Key, slice[^1].Binding.AuthorityUserId, binding) : null;
            page = new OwnerRecoveryMemberPage(slice.Select(x => new OwnerRecoveryMemberItem(x.Binding.AuthorityUserId, x.Binding.IdentityIssuerUri, x.Binding.IdentitySubjectIdentifier, x.Name, x.Binding.IdentityCreatedOn)).ToList(),
                total, next, limit, correlationId);
            AuditJournal.Append(scope, state, "PlatformOwnerRecoveryMembersViewed", new
            {
                platform_operator_id = me.PlatformOperatorId,
                operator_issuer_uri = me.OperatorIssuerUri,
                operator_subject_identifier = me.OperatorSubjectIdentifier,
                disclosed = MemberDisclosure,
                returned_count = slice.Count,
                correlation_id = correlationId,
            }, clock);
            await scope.CommitAsync(cancellationToken).ConfigureAwait(false); // fail closed: no committed audit event, no member list
        }

        return page;
    }

    // ---- reads ----

    public async Task<OwnerRecoveryPage> SearchAsync(VerifiedActor actor, OwnerRecoverySearchRequest input, Guid correlationId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        RequireAvailable();
        var status = string.IsNullOrWhiteSpace(input.Status) ? OwnerRecoveryFilters.All : input.Status.Trim();
        if (!OwnerRecoveryFilters.Values.Contains(status))
        {
            throw AuthorityException.Invalid("unknown status filter");
        }

        var text = input.Query?.Trim();
        if (text is { Length: > 200 })
        {
            throw AuthorityException.Invalid("the search text is at most 200 characters");
        }

        var limit = input.Limit ?? OrganizationOnboardingService.DefaultPageSize;
        if (limit is < 1 or > OrganizationOnboardingService.MaximumPageSize)
        {
            throw AuthorityException.Invalid($"limit must be 1..{OrganizationOnboardingService.MaximumPageSize}");
        }

        await using var scope = await store.BeginScopeAsync(cancellationToken).ConfigureAwait(false);
        var head = await scope.ReadPlatformJournalAsync(cancellationToken).ConfigureAwait(false) ?? new PlatformJournalHead();
        await journal.EnsureCurrentAsync(scope, head, requireAnchored: false, correlationId, cancellationToken).ConfigureAwait(false);
        var me = await PlatformOperatorAccess.ResolveActiveAsync(scope, actor, cancellationToken).ConfigureAwait(false);
        var binding = OrganizationOnboardingService.Binding("recovery", me, status, text, limit);
        (DateTimeOffset, Guid)? after = null;
        if (input.Cursor is { Length: > 0 } cursor)
        {
            var (sortKey, id) = OrganizationOnboardingService.ReadCursorParts(cursor, binding);
            after = (new DateTimeOffset(long.Parse(sortKey, CultureInfo.InvariantCulture), TimeSpan.Zero), id);
        }

        var now = clock.GetUtcNow();
        var page = await scope.SearchOwnerRecoveryCasesAsync(new OwnerRecoveryQuery(text, status, now, after, limit + 1), cancellationToken).ConfigureAwait(false);
        var rows = page.Rows.Take(limit).ToList();
        var approvals = await scope.GetOwnerRecoveryApprovalsForCasesAsync(rows.Select(r => r.Case.OrganizationOwnerRecoveryCaseId).ToList(), cancellationToken).ConfigureAwait(false);
        var operators = (await scope.GetPlatformOperatorsAsync(cancellationToken).ConfigureAwait(false)).ToDictionary(o => o.PlatformOperatorId);
        var items = new List<OwnerRecoveryListItem>();
        foreach (var r in rows)
        {
            var mine = approvals.Where(a => a.OrganizationOwnerRecoveryCaseId == r.Case.OrganizationOwnerRecoveryCaseId).ToList();
            var subjects = await scope.GetOwnerRecoverySubjectsAsync(r.Case.OrganizationOwnerRecoveryCaseId, cancellationToken).ConfigureAwait(false);
            items.Add(new OwnerRecoveryListItem(r.Case.OrganizationOwnerRecoveryCaseId, r.Case.OrganizationId, r.OrganizationDisplayName, r.Case.IncidentReferenceText, r.Case.RecoveryStateName,
                await DisplayStateAsync(scope, r.Case, Recorded(mine), now, cancellationToken).ConfigureAwait(false), r.Case.CaseOpenedOn, r.Case.WaitingPeriodEndsOn, r.Case.CaseExpiresOn, Recorded(mine), RequiredApprovals,
                mine.Any(a => a.PlatformOperatorId == me.PlatformOperatorId && a.ApprovalStateName is OnboardingApprovalStates.Valid or OnboardingApprovalStates.Consumed),
                subjects.Count(s => s.RecoverySubjectRoleName == OwnerRecoverySubjectRoles.Replacement),
                operators.TryGetValue(r.Case.OpenedByPlatformOperatorId, out var opener) ? opener.OperatorDisplayName : "Unknown operator"));
        }

        var next = page.Rows.Count > limit ? OrganizationOnboardingService.WriteCursor(rows[^1].Case.CaseOpenedOn.UtcTicks.ToString(CultureInfo.InvariantCulture), rows[^1].Case.OrganizationOwnerRecoveryCaseId, binding) : null;
        return new OwnerRecoveryPage(items, page.TotalCount, next, limit, status, (long)policy.RecoveryWaitingPeriod.TotalSeconds, platformPolicy.Assurance, correlationId);
    }

    /// <summary>The case, its frozen statement, owners, evidence and approvals. Discloses identities and evidence, so the view is audited on the organization's audit stream before it is returned.</summary>
    public async Task<OwnerRecoveryCaseDetail> GetDetailAsync(VerifiedActor actor, Guid caseId, Guid correlationId, CancellationToken cancellationToken)
    {
        RequireAvailable();
        OwnerRecoveryCaseDetail detail;
        await using (var scope = await store.BeginScopeAsync(cancellationToken).ConfigureAwait(false))
        {
            var head = await scope.ReadPlatformJournalAsync(cancellationToken).ConfigureAwait(false) ?? new PlatformJournalHead();
            await journal.EnsureCurrentAsync(scope, head, requireAnchored: false, correlationId, cancellationToken).ConfigureAwait(false);
            var me = await PlatformOperatorAccess.ResolveActiveAsync(scope, actor, cancellationToken).ConfigureAwait(false);
            var located = await scope.FindOwnerRecoveryCaseAsync(caseId, lockForUpdate: false, cancellationToken).ConfigureAwait(false) ?? throw new AuthorityException(AuthorityErrorCodes.NotFound, 404, false, "no such recovery case");
            var state = await scope.LockOrganizationAsync(located.OrganizationId, cancellationToken).ConfigureAwait(false) ?? throw new AuthorityException(AuthorityErrorCodes.NotFound, 404, false);
            detail = await DetailAsync(scope, located, me, clock.GetUtcNow(), correlationId, cancellationToken).ConfigureAwait(false);
            AuditJournal.Append(scope, state, "PlatformOwnerRecoveryCaseViewed", new
            {
                organization_owner_recovery_case_id = caseId,
                platform_operator_id = me.PlatformOperatorId,
                operator_issuer_uri = me.OperatorIssuerUri,
                operator_subject_identifier = me.OperatorSubjectIdentifier,
                disclosed = DetailDisclosure,
                correlation_id = correlationId,
            }, clock);
            await scope.CommitAsync(cancellationToken).ConfigureAwait(false); // fail closed: no committed audit event, no detail
        }

        return detail;
    }

    private async Task<OwnerRecoveryCaseDetail> DetailAsync(IAuthorityScope scope, OrganizationOwnerRecoveryCase recoveryCase, PlatformOperator me, DateTimeOffset now, Guid correlationId, CancellationToken cancellationToken)
    {
        var state = await scope.ReadOrganizationAsync(recoveryCase.OrganizationId, cancellationToken).ConfigureAwait(false) ?? throw new AuthorityException(AuthorityErrorCodes.NotFound, 404, false);
        var org = await OrganizationViewAsync(scope, state, cancellationToken).ConfigureAwait(false);
        var subjects = await scope.GetOwnerRecoverySubjectsAsync(recoveryCase.OrganizationOwnerRecoveryCaseId, cancellationToken).ConfigureAwait(false);
        var approvals = await scope.GetOwnerRecoveryApprovalsAsync(recoveryCase.OrganizationOwnerRecoveryCaseId, cancellationToken).ConfigureAwait(false);
        var operators = (await scope.GetPlatformOperatorsAsync(cancellationToken).ConfigureAwait(false)).ToDictionary(o => o.PlatformOperatorId);
        var declared = subjects.Where(s => s.RecoverySubjectRoleName == OwnerRecoverySubjectRoles.DeclaredUnavailable).Select(s => s.SubjectAuthorityUserId).ToHashSet();
        var owners = new List<OwnerRecoveryOwnerView>();
        foreach (var b in org.Bindings.Where(b => org.Owners.Contains(b.AuthorityUserId)).OrderBy(b => b.IdentitySubjectIdentifier, StringComparer.Ordinal))
        {
            var activeSince = declared.Contains(b.AuthorityUserId) && await scope.CountSessionsCreatedSinceAsync(b.IdentityBindingId, recoveryCase.CaseOpenedOn, cancellationToken).ConfigureAwait(false) > 0;
            owners.Add(new OwnerRecoveryOwnerView(b.AuthorityUserId, b.IdentityIssuerUri, b.IdentitySubjectIdentifier, b.IdentityBindingEnabled, declared.Contains(b.AuthorityUserId), activeSince));
        }

        var replacements = subjects.Count(s => s.RecoverySubjectRoleName == OwnerRecoverySubjectRoles.Replacement);
        var eligibleBefore = org.EnabledOwners.Count(o => !declared.Contains(o));
        var conflict = Conflict(me, org.Bindings, subjects);
        var open = recoveryCase.RecoveryStateName == OwnerRecoveryStates.Open && recoveryCase.CaseExpiresOn > now;
        var recorded = Recorded(approvals);
        string? approveRefusal = !open ? NotOpen(recoveryCase).ErrorCode
            : conflict?.ErrorCode ?? (approvals.Any(a => a.PlatformOperatorId == me.PlatformOperatorId && a.ApprovalStateName is OnboardingApprovalStates.Valid or OnboardingApprovalStates.Consumed) ? AuthorityErrorCodes.OperatorAlreadyApproved : null);
        string? executeRefusal = !open ? NotOpen(recoveryCase).ErrorCode
            : conflict?.ErrorCode ?? (recorded < RequiredApprovals ? AuthorityErrorCodes.OwnerRecoveryApprovalsIncomplete : now < recoveryCase.WaitingPeriodEndsOn ? AuthorityErrorCodes.OwnerRecoveryWaitingPeriod : null);
        var eligibility = new OwnerRecoveryEligibility(approveRefusal is null, open && approvals.Any(a => a.PlatformOperatorId == me.PlatformOperatorId && a.ApprovalStateName == OnboardingApprovalStates.Valid), open && conflict is null,
            executeRefusal is null, approveRefusal, approveRefusal is null ? null : RefusalText(approveRefusal), executeRefusal, executeRefusal is null ? null : RefusalText(executeRefusal));
        OwnerRecoveryObjectionView? objection = null;
        if (recoveryCase.ObjectedOn is { } objectedOn && recoveryCase.ObjectedByAuthorityUserId is { } objector)
        {
            objection = new OwnerRecoveryObjectionView(objectedOn, objector, org.Bindings.FirstOrDefault(b => b.AuthorityUserId == objector)?.IdentitySubjectIdentifier ?? objector.ToString("D"), recoveryCase.ObjectionReasonText ?? string.Empty);
        }

        var activation = await ActivationAsync(scope, state, recoveryCase, cancellationToken).ConfigureAwait(false);
        return new OwnerRecoveryCaseDetail(recoveryCase.OrganizationOwnerRecoveryCaseId, recoveryCase.OrganizationId, state.OrganizationDisplayName, recoveryCase.RecoveryStateName,
            DisplayState(recoveryCase, recorded, now, activation), recoveryCase.IncidentReferenceText, recoveryCase.RecoveryReasonText, recoveryCase.OpenedByPlatformOperatorId,
            operators.TryGetValue(recoveryCase.OpenedByPlatformOperatorId, out var opener) ? opener.OperatorDisplayName : "Unknown operator", recoveryCase.CaseOpenedOn, recoveryCase.WaitingPeriodEndsOn,
            Math.Max(0, (long)Math.Ceiling((recoveryCase.WaitingPeriodEndsOn - now).TotalSeconds)), recoveryCase.CaseExpiresOn, recoveryCase.RecoveredOn, recoveryCase.CaseClosedOn, recoveryCase.CaseClosureReasonName,
            recoveryCase.ClosureJustificationText, recoveryCase.LastExecutionFailureCode, recoveryCase.LastExecutionFailureOn, eligibleBefore, recoveryCase.RecoveryStateName == OwnerRecoveryStates.Recovered ? org.EnabledOwners.Count(o => !declared.Contains(o)) : eligibleBefore + replacements,
            owners,
            subjects.Select(s => new OwnerRecoverySubjectView(s.RecoverySubjectRoleName, s.SubjectAuthorityUserId, s.SubjectIssuerUri, s.SubjectIdentifier, s.SubjectDisplayNameHint, s.IsNewIdentity, s.VerificationMethodName,
                s.VerificationReferenceText, s.UnavailabilityReasonName, s.EvidenceReferenceText, s.EvidenceSummaryText)).ToList(),
            approvals.Select(a => new OwnerRecoveryApprovalView(a.OrganizationOwnerRecoveryApprovalId, a.PlatformOperatorId, operators.TryGetValue(a.PlatformOperatorId, out var o) ? o.OperatorDisplayName : "Unknown operator",
                a.PlatformOperatorId == me.PlatformOperatorId, a.ApprovalJustificationText, a.ApprovalRecordedOn, a.ApprovalStateName, a.ApprovalClosureReasonName)).ToList(),
            recorded, RequiredApprovals, objection, recoveryCase.RecoveryStatementJson, Convert.ToHexStringLower(recoveryCase.RecoveryStatementSha256), activation, eligibility, recoveryCase.CaseRowRevision,
            platformPolicy.Assurance, correlationId);
    }

    // ---- validation and shared rules ----

    private sealed record OrganizationView(List<IdentityBinding> Bindings, HashSet<Guid> Owners, List<Guid> EnabledOwners, string? OwnerIssuer);

    private static async Task<OrganizationView> OrganizationViewAsync(IAuthorityScope scope, OrganizationAuthorizationState state, CancellationToken cancellationToken)
    {
        var graph = new DesiredStateGraph(await scope.GetActiveEdgesAsync(state.OrganizationId, cancellationToken).ConfigureAwait(false));
        var bindings = await scope.GetIdentityBindingsAsync(state.OrganizationId, cancellationToken).ConfigureAwait(false);
        var owners = bindings.Where(b => graph.HasOrganizationRole(state.OrganizationId, b.AuthorityUserId, OrganizationRoleNames.OrganizationOwner)).Select(b => b.AuthorityUserId).ToHashSet();
        var enabled = bindings.Where(b => owners.Contains(b.AuthorityUserId) && b.IdentityBindingEnabled).Select(b => b.AuthorityUserId).Order().ToList();
        var issuers = bindings.Where(b => owners.Contains(b.AuthorityUserId)).Select(b => b.IdentityIssuerUri).Distinct(StringComparer.Ordinal).ToList();
        if (issuers.Count == 0)
        {
            issuers = bindings.Select(b => b.IdentityIssuerUri).Distinct(StringComparer.Ordinal).ToList();
        }

        return new OrganizationView(bindings, owners, enabled, issuers.Count == 1 ? issuers[0] : null);
    }

    /// <summary>The owner set (user id + enabled state, ordered): any grant, revocation, disable or enable of an owner changes it.</summary>
    private static byte[] OwnerSetDigest(OrganizationView org) =>
        SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', org.Bindings.Where(b => org.Owners.Contains(b.AuthorityUserId)).OrderBy(b => b.AuthorityUserId).Select(b => $"{b.AuthorityUserId:D}|{b.IdentityBindingEnabled}"))));

    private sealed record DeclaredDraft(IdentityBinding Binding, string Reason, string EvidenceReference, string EvidenceSummary);

    private sealed record ReplacementDraft(Guid? ExistingUserId, string IssuerUri, string SubjectIdentifier, string DisplayNameHint, string VerificationMethod, string VerificationReference);

    private sealed record Draft(Guid OrganizationId, string Incident, string Justification, IReadOnlyList<DeclaredDraft> Declared, IReadOnlyList<ReplacementDraft> Replacements, int EligibleBefore, int EligibleAfter);

    private async Task<Draft> ValidateAsync(IAuthorityScope scope, OwnerRecoveryCreateRequest input, OrganizationView org, PlatformOperator me, CancellationToken cancellationToken)
    {
        var incident = input.IncidentReference?.Trim() ?? string.Empty;
        if (!IncidentPattern().IsMatch(incident))
        {
            throw AuthorityException.Invalid("an incident reference of 3 to 64 characters is required (letters, digits and . _ : / -)");
        }

        var justification = PlatformOperatorAccess.RequireJustification(input.Justification);
        var declared = new List<DeclaredDraft>();
        foreach (var d in input.DeclaredUnavailable ?? [])
        {
            var binding = org.Bindings.FirstOrDefault(b => b.AuthorityUserId == d.AuthorityUserId);
            if (binding is null || !org.Owners.Contains(binding.AuthorityUserId))
            {
                throw AuthorityException.Invalid("an owner declared unavailable must be an owner of this organization");
            }

            if (!binding.IdentityBindingEnabled)
            {
                throw AuthorityException.Invalid("a disabled owner is already ineligible; declare only enabled owners unavailable");
            }

            var reason = d.Reason?.Trim() ?? string.Empty;
            var reference = d.EvidenceReference?.Trim() ?? string.Empty;
            var summary = d.EvidenceSummary?.Trim() ?? string.Empty;
            if (!OwnerUnavailabilityReasons.All.Contains(reason, StringComparer.Ordinal) || !EvidenceReferencePattern().IsMatch(reference) || summary.Length is < 10 or > 500 || summary.Any(char.IsControl))
            {
                throw new AuthorityException(AuthorityErrorCodes.OwnerRecoveryEvidenceRequired, 400, false,
                    "each owner declared unavailable needs a reason, a documented evidence reference (3 to 100 characters) and an evidence summary (10 to 500 characters); an assertion alone is not accepted");
            }

            declared.Add(new DeclaredDraft(binding, reason, reference, summary));
        }

        if (declared.Select(d => d.Binding.AuthorityUserId).Distinct().Count() != declared.Count)
        {
            throw AuthorityException.Invalid("each owner can be declared unavailable once");
        }

        var declaredIds = declared.Select(d => d.Binding.AuthorityUserId).ToHashSet();
        var eligibleBefore = org.EnabledOwners.Count(o => !declaredIds.Contains(o));
        if (eligibleBefore >= 2)
        {
            throw new AuthorityException(AuthorityErrorCodes.OwnerRecoveryNotApplicable, 409, false, $"the organization has {eligibleBefore} eligible owners; recovery applies only below two (use the normal two-owner administration)");
        }

        if (org.OwnerIssuer is null)
        {
            throw new AuthorityException(AuthorityErrorCodes.OwnerRecoveryNotApplicable, 409, false, "the organization's owners do not share one identity issuer; this case needs manual handling");
        }

        var raw = input.Replacements ?? [];
        if (raw.Count is < 1 or > MaximumReplacements)
        {
            throw AuthorityException.Invalid($"1 to {MaximumReplacements} replacement owners are required");
        }

        var replacements = new List<ReplacementDraft>();
        foreach (var r in raw)
        {
            var method = r.VerificationMethod?.Trim() ?? string.Empty;
            var reference = r.VerificationReference?.Trim() ?? string.Empty;
            if (!OnboardingVerificationMethods.All.Contains(method, StringComparer.Ordinal) || !EvidenceReferencePattern().IsMatch(reference))
            {
                throw new AuthorityException(AuthorityErrorCodes.OrganizationOwnerIdentityInvalid, 400, false, "every replacement owner needs a verification method and a verification reference of 3 to 100 characters");
            }

            if (r.ExistingAuthorityUserId is { } existingId)
            {
                var binding = org.Bindings.FirstOrDefault(b => b.AuthorityUserId == existingId);
                if (binding is null || !binding.IdentityBindingEnabled || org.Owners.Contains(existingId))
                {
                    throw new AuthorityException(AuthorityErrorCodes.OwnerRecoveryIdentityConflict, 409, false, "an existing replacement must be an enabled member of this organization who is not yet an owner");
                }

                replacements.Add(new ReplacementDraft(existingId, binding.IdentityIssuerUri, binding.IdentitySubjectIdentifier, binding.IdentitySubjectIdentifier, method, reference));
                continue;
            }

            var issuer = r.IssuerUri?.Trim() ?? string.Empty;
            var subject = r.SubjectIdentifier?.Trim() ?? string.Empty;
            var hint = r.DisplayNameHint?.Trim() ?? string.Empty;
            if (!policy.ApprovedIdentityIssuers.Contains(issuer, StringComparer.Ordinal) || issuer != org.OwnerIssuer)
            {
                throw new AuthorityException(AuthorityErrorCodes.OrganizationOwnerIssuerMismatch, 400, false, "a new replacement owner must come from the organization's identity issuer (an approved issuer)");
            }

            if (subject.Length > 512 || subject.Contains('@', StringComparison.Ordinal) || !CommandPlanner.IsValidSubject(issuer, subject) || hint.Length is < 1 or > 200 || hint.Any(char.IsControl))
            {
                throw new AuthorityException(AuthorityErrorCodes.OrganizationOwnerIdentityInvalid, 400, false, "a new replacement owner needs the identity provider's stable subject (never an email address) and a display name");
            }

            if (org.Bindings.Any(b => b.IdentityIssuerUri == issuer && b.IdentitySubjectIdentifier == subject))
            {
                throw new AuthorityException(AuthorityErrorCodes.OwnerRecoveryIdentityConflict, 409, false, "this identity is already bound in the organization; name the existing member instead");
            }

            replacements.Add(new ReplacementDraft(null, issuer, subject, hint, method, reference));
        }

        if (replacements.Select(r => (r.IssuerUri, r.SubjectIdentifier)).Distinct().Count() != replacements.Count)
        {
            throw AuthorityException.Invalid("each replacement owner must be a different identity");
        }

        foreach (var r in replacements)
        {
            if (await scope.FindPlatformOperatorByIdentityAsync(r.IssuerUri, r.SubjectIdentifier, cancellationToken).ConfigureAwait(false) is not null
                || (r.IssuerUri == me.OperatorIssuerUri && r.SubjectIdentifier == me.OperatorSubjectIdentifier))
            {
                throw new AuthorityException(AuthorityErrorCodes.OrganizationOwnerIsOperator, 403, false, "a Platform Operator identity can never become an owner through recovery");
            }
        }

        var eligibleAfter = eligibleBefore + replacements.Count;
        if (eligibleAfter < 2)
        {
            throw new AuthorityException(AuthorityErrorCodes.OwnerRecoveryNotApplicable, 409, false, "the case must restore at least two eligible owners");
        }

        return new Draft(input.OrganizationId, incident, justification, declared, replacements, eligibleBefore, eligibleAfter);
    }

    private static void RequireNotQuarantined(OrganizationAuthorizationState state)
    {
        if (state.IntegrityQuarantineEnabled)
        {
            throw AuthorityException.Quarantined("organization quarantined");
        }
    }

    /// <summary>An operator bound in the organization (any binding) cannot open or decide its case.</summary>
    private static void RequireNoConflict(PlatformOperator me, List<IdentityBinding> bindings)
    {
        if (bindings.Any(b => b.IdentityIssuerUri == me.OperatorIssuerUri && b.IdentitySubjectIdentifier == me.OperatorSubjectIdentifier))
        {
            throw new AuthorityException(AuthorityErrorCodes.OperatorConflictOfInterest, 403, false, "you belong to this organization, so other operators must handle its recovery");
        }
    }

    private static AuthorityException? Conflict(DecisionContext c) => Conflict(c.Me, c.Org.Bindings, c.Subjects);

    private static AuthorityException? Conflict(PlatformOperator me, List<IdentityBinding> bindings, List<OrganizationOwnerRecoverySubject> subjects)
    {
        if (bindings.Any(b => b.IdentityIssuerUri == me.OperatorIssuerUri && b.IdentitySubjectIdentifier == me.OperatorSubjectIdentifier)
            || subjects.Any(s => s.SubjectIssuerUri == me.OperatorIssuerUri && s.SubjectIdentifier == me.OperatorSubjectIdentifier))
        {
            return new AuthorityException(AuthorityErrorCodes.OperatorConflictOfInterest, 403, false, "you belong to this organization or are named in the case, so other operators must decide it");
        }

        return null;
    }

    private void RequireAvailable()
    {
        platformPolicy.RequireOperational();
        if (!policy.Operational)
        {
            throw new AuthorityException(AuthorityErrorCodes.OwnerRecoveryUnavailable, 403, false, "owner recovery is not available in this environment (production needs verified owner identity control, PB-08, and an independent review)");
        }
    }

    private static async Task<List<OrganizationOwnerRecoveryApproval>> StaleAsync(DecisionContext c, CancellationToken cancellationToken)
    {
        var active = await ActiveOperatorIdsAsync(c.Scope, cancellationToken).ConfigureAwait(false);
        var stale = c.Approvals.Where(a => a.ApprovalStateName == OnboardingApprovalStates.Valid && a.PlatformOperatorId != c.Me.PlatformOperatorId && !active.Contains(a.PlatformOperatorId)).ToList();
        foreach (var approval in stale)
        {
            CloseApproval(approval, OnboardingApprovalStates.Invalidated, PlatformApprovalClosureReasons.OperatorIneligible, c.Now);
        }

        return stale;
    }

    private static List<OrganizationOwnerRecoveryApproval> CloseOpenApprovals(DecisionContext c, string reason)
    {
        var closing = c.Approvals.Where(a => a.ApprovalStateName == OnboardingApprovalStates.Valid).ToList();
        foreach (var approval in closing)
        {
            CloseApproval(approval, OnboardingApprovalStates.Invalidated, reason, c.Now);
        }

        return closing;
    }

    private VerifiedOperatorEvidence VerifyEvidence(DecisionContext c, string decision) =>
        evidenceVerifier.Verify(c.Me, c.Input.EvidenceKind, c.Input.Evidence, new OperatorEvidenceBinding(decision, PlatformOperationKinds.OrganizationOwnerRecovery, c.Case.OrganizationOwnerRecoveryCaseId,
            c.Case.OrganizationId, c.StatementSha256, PlatformOperatorEvidenceVerifier.JustificationSha256(c.Justification)), c.Now);

    private static async Task RecordRefusalAsync(DecisionContext c, string decision, AuthorityException refusal, CancellationToken cancellationToken)
    {
        // Nothing has been changed when a refusal is thrown; an execution refusal also records the failure on the case.
        if (decision == PlatformDecisions.Execute && c.Case.RecoveryStateName == OwnerRecoveryStates.Open)
        {
            c.Case.LastExecutionFailureCode = refusal.ErrorCode;
            c.Case.LastExecutionFailureOn = c.Now;
            c.Case.CaseRowRevision++;
            PlatformJournal.Append(c.Scope, c.Head, "OwnerRecoveryExecutionRefused", new
            {
                organization_owner_recovery_case_id = c.Case.OrganizationOwnerRecoveryCaseId,
                platform_operator_id = c.Me.PlatformOperatorId,
                refusal_code = refusal.ErrorCode,
                correlation_id = c.CorrelationId,
            }, PlatformStateDelta.Recovery([c.Case]), c.Now);
            await c.Scope.CommitAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        await PlatformOperatorRegistryService.RecordRefusalAsync(c.Scope, c.Head, c.Me, decision, PlatformOperationKinds.OrganizationOwnerRecovery, c.Case.OrganizationOwnerRecoveryCaseId, refusal, c.CorrelationId, c.Now, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<OrganizationActivation?> ActivationAsync(IAuthorityScope scope, OrganizationAuthorizationState state, OrganizationOwnerRecoveryCase recoveryCase, CancellationToken cancellationToken)
    {
        if (recoveryCase.ExecutedBySecurityCommandId is not { } commandId)
        {
            return null;
        }

        var command = await scope.FindCommandAsync(commandId, cancellationToken).ConfigureAwait(false);
        var status = state.IntegrityQuarantineEnabled || command is null ? "Quarantined" : command.CommandStateName == nameof(CommandStatus.Effective) ? "Active" : "Activating";
        return new OrganizationActivation(status, state.AuthorizationRevision, state.ProjectionAppliedRevision);
    }

    private static async Task<string> DisplayStateAsync(IAuthorityScope scope, OrganizationOwnerRecoveryCase recoveryCase, int recorded, DateTimeOffset now, CancellationToken cancellationToken)
    {
        OrganizationActivation? activation = null;
        if (recoveryCase.RecoveryStateName == OwnerRecoveryStates.Recovered && await scope.ReadOrganizationAsync(recoveryCase.OrganizationId, cancellationToken).ConfigureAwait(false) is { } state)
        {
            activation = await ActivationAsync(scope, state, recoveryCase, cancellationToken).ConfigureAwait(false);
        }

        return DisplayState(recoveryCase, recorded, now, activation);
    }

    private static string DisplayState(OrganizationOwnerRecoveryCase recoveryCase, int recorded, DateTimeOffset now, OrganizationActivation? activation) => recoveryCase.RecoveryStateName switch
    {
        OwnerRecoveryStates.Open when recoveryCase.CaseExpiresOn <= now => OwnerRecoveryDisplayStates.Expired,
        OwnerRecoveryStates.Open when recorded < RequiredApprovals => OwnerRecoveryDisplayStates.AwaitingApprovals,
        OwnerRecoveryStates.Open when now < recoveryCase.WaitingPeriodEndsOn => OwnerRecoveryDisplayStates.WaitingPeriod,
        OwnerRecoveryStates.Open => OwnerRecoveryDisplayStates.ReadyForRecovery,
        OwnerRecoveryStates.Objected => OwnerRecoveryDisplayStates.BlockedByObjection,
        OwnerRecoveryStates.Recovered => activation?.Status == "Active" ? OwnerRecoveryDisplayStates.Recovered : OwnerRecoveryDisplayStates.Recovering,
        OwnerRecoveryStates.Cancelled => OwnerRecoveryDisplayStates.Cancelled,
        OwnerRecoveryStates.Invalidated => OwnerRecoveryDisplayStates.Invalidated,
        _ => OwnerRecoveryDisplayStates.Expired,
    };

    private OwnerRecoveryDecisionResponse Response(bool dryRun, AuthorityException? refusal, OrganizationOwnerRecoveryCase recoveryCase, string decision, string statementJson, string statementSha256, string outcome,
        List<OrganizationOwnerRecoveryApproval> approvals, Guid? commandId, bool replayed, DateTimeOffset now, Guid correlationId) =>
        new(dryRun, refusal is null, refusal?.ErrorCode, refusal?.Detail, recoveryCase.OrganizationOwnerRecoveryCaseId, recoveryCase.OrganizationId, decision, statementJson, statementSha256, outcome, Recorded(approvals),
            RequiredApprovals, recoveryCase.RecoveryStateName, DisplayState(recoveryCase, Recorded(approvals), now, null), commandId, null, replayed, platformPolicy.Assurance, correlationId);

    private static object Detail(DecisionContext c, string decision, VerifiedOperatorEvidence evidence, object extra) => new
    {
        organization_owner_recovery_case_id = c.Case.OrganizationOwnerRecoveryCaseId,
        organization_id = c.Case.OrganizationId,
        platform_operator_id = c.Me.PlatformOperatorId,
        decision,
        statement_sha256 = c.StatementSha256,
        justification = c.Justification,
        evidence_kind = evidence.EvidenceKind,
        evidence_identifier = evidence.EvidenceIdentifier,
        evidence_sha256 = Convert.ToHexStringLower(evidence.EvidenceSha256),
        evidence_key_identifier = evidence.KeyIdentifier,
        resulting_state = c.Case.RecoveryStateName,
        extra,
        correlation_id = c.CorrelationId,
    };

    private static string RefusalText(string code) => code switch
    {
        AuthorityErrorCodes.OperatorAlreadyApproved => "You already approved this case; two further different operators must approve it.",
        AuthorityErrorCodes.OperatorConflictOfInterest => "You belong to this organization or are named in the case, so other operators must decide it.",
        AuthorityErrorCodes.OwnerRecoveryApprovalsIncomplete => "Three approvals by different operators are required first.",
        AuthorityErrorCodes.OwnerRecoveryWaitingPeriod => "The waiting period has not ended.",
        _ => "The case is not open.",
    };

    private static int Recorded(IEnumerable<OrganizationOwnerRecoveryApproval> approvals) =>
        approvals.Where(a => a.ApprovalStateName is OnboardingApprovalStates.Valid or OnboardingApprovalStates.Consumed).Select(a => a.PlatformOperatorId).Distinct().Count();

    private static async Task<HashSet<Guid>> ActiveOperatorIdsAsync(IAuthorityScope scope, CancellationToken cancellationToken) =>
        (await scope.GetPlatformOperatorsAsync(cancellationToken).ConfigureAwait(false)).Where(o => o.OperatorStatusName == PlatformOperatorStates.Active).Select(o => o.PlatformOperatorId).ToHashSet();

    private static void CloseApproval(OrganizationOwnerRecoveryApproval approval, string state, string? reason, DateTimeOffset now)
    {
        approval.ApprovalStateName = state;
        if (state is OnboardingApprovalStates.Withdrawn or OnboardingApprovalStates.Invalidated)
        {
            approval.ApprovalClosedOn = now;
            approval.ApprovalClosureReasonName = reason;
        }
    }

    private static void Close(OrganizationOwnerRecoveryCase recoveryCase, string state, string reason, Guid? closedBy, string? justification, DateTimeOffset now)
    {
        recoveryCase.RecoveryStateName = state;
        recoveryCase.CaseClosedOn = now;
        recoveryCase.CaseClosureReasonName = reason;
        recoveryCase.ClosedByPlatformOperatorId = closedBy;
        recoveryCase.ClosureJustificationText = justification;
        recoveryCase.CaseRowRevision++;
    }

    private static AuthorityException NotOpen(OrganizationOwnerRecoveryCase recoveryCase) =>
        recoveryCase.RecoveryStateName == OwnerRecoveryStates.Objected
            ? new(AuthorityErrorCodes.OwnerRecoveryObjected, 409, false, "the case was objected to by the organization; recovery is blocked and a separately authorized resolution is needed")
            : new(AuthorityErrorCodes.OwnerRecoveryNotOpen, 409, false, $"the case is {recoveryCase.RecoveryStateName}; this decision does not apply");

    private static (string Json, byte[] Digest) Statement(OrganizationOwnerRecoveryCase recoveryCase, List<OrganizationOwnerRecoverySubject> subjects, OrganizationView org, string organizationName) =>
        StatementCore(recoveryCase, subjects, organizationName,
            org.Bindings.Where(b => org.Owners.Contains(b.AuthorityUserId)).OrderBy(b => b.AuthorityUserId).Select(b => new OwnerRecoveryOwnerStatement(b.AuthorityUserId, b.IdentityIssuerUri, b.IdentitySubjectIdentifier, b.IdentityBindingEnabled)).ToList());

    /// <summary>From stored rows: the owner snapshot is part of the frozen statement JSON, so it is read back from it.</summary>
    private static (string Json, byte[] Digest) StatementFromRows(OrganizationOwnerRecoveryCase recoveryCase, List<OrganizationOwnerRecoverySubject> subjects, string organizationName)
    {
        List<OwnerRecoveryOwnerStatement> owners;
        try
        {
            owners = CanonicalJson.Deserialize<OwnerRecoveryStatement>(JsonDocument.Parse(recoveryCase.RecoveryStatementJson).RootElement).CurrentOwners.ToList();
        }
        catch (Exception ex) when (ex is JsonException or AuthorityException)
        {
            owners = [];
        }

        return StatementCore(recoveryCase, subjects, organizationName, owners);
    }

    private static (string Json, byte[] Digest) StatementCore(OrganizationOwnerRecoveryCase recoveryCase, List<OrganizationOwnerRecoverySubject> subjects, string organizationName, List<OwnerRecoveryOwnerStatement> owners)
    {
        var statement = new OwnerRecoveryStatement(StatementVersion, PlatformOperationKinds.OrganizationOwnerRecovery, recoveryCase.OrganizationOwnerRecoveryCaseId, recoveryCase.OrganizationId, organizationName,
            recoveryCase.IncidentReferenceText, Sha(recoveryCase.RecoveryReasonText), owners,
            subjects.Where(s => s.RecoverySubjectRoleName == OwnerRecoverySubjectRoles.DeclaredUnavailable).OrderBy(s => s.SubjectOrdinalNumber)
                .Select(s => new OwnerRecoveryDeclaredStatement(s.SubjectAuthorityUserId, s.SubjectIssuerUri, s.SubjectIdentifier, s.UnavailabilityReasonName ?? string.Empty, s.EvidenceReferenceText ?? string.Empty, Sha(s.EvidenceSummaryText ?? string.Empty))).ToList(),
            subjects.Where(s => s.RecoverySubjectRoleName == OwnerRecoverySubjectRoles.Replacement).OrderBy(s => s.SubjectOrdinalNumber)
                .Select(s => new OwnerRecoveryReplacementStatement(s.SubjectAuthorityUserId, s.SubjectIssuerUri, s.SubjectIdentifier, s.SubjectDisplayNameHint, s.IsNewIdentity, s.VerificationMethodName ?? string.Empty, s.VerificationReferenceText ?? string.Empty)).ToList(),
            RequiredApprovals, recoveryCase.OpenedByPlatformOperatorId, recoveryCase.CaseOpenedOn, recoveryCase.WaitingPeriodEndsOn, recoveryCase.CaseExpiresOn);
        var json = CanonicalJson.Serialize(statement);
        return (json, CanonicalJson.Digest(json));
    }

    private static string Sha(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    private async Task<string> TryDeliverAsync(CancellationToken cancellationToken)
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

    private sealed record RecoverOwner(Guid AuthorityUserId, string IssuerUri, string SubjectIdentifier, bool IsNewIdentity);

    private sealed record RecoverApproval(Guid PlatformOperatorId, string EvidenceSha256);

    private sealed record RecoverPayload(Guid OrganizationOwnerRecoveryCaseId, string IncidentReference, string StatementSha256, IReadOnlyList<RecoverOwner> Owners, IReadOnlyList<RecoverApproval> Approvals, Guid ExecutedByPlatformOperatorId, string ApprovalAssurance);

    [GeneratedRegex("^[A-Za-z0-9._:-]{8,128}$")]
    private static partial Regex IdempotencyKeyPattern();

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._:/-]{2,63}$")]
    private static partial Regex IncidentPattern();

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9 ._:/#-]{2,99}$")]
    private static partial Regex EvidenceReferencePattern();
}
