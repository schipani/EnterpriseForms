using System.Text.Json;
using EnterpriseForms.Application.Common.Authority;
using EnterpriseForms.Application.Common.Persistence;
using EnterpriseForms.Domain.Features.Audit;
using EnterpriseForms.Domain.Features.Teams;

namespace EnterpriseForms.Application.Features.Teams.MembershipReconciliation;

public enum ReconciliationStepKind
{
    Idle,
    Waiting,
    Projected,
    Terminal,
    TransientError,
    Anomaly,
    CredentialRefused,
    RateLimited,
}

/// <summary>What one reconciler step did; the Worker logs anomalies and credential refusals as alerts (no keys, no values).</summary>
public sealed record ReconciliationStep(ReconciliationStepKind Kind, Guid? AuthorityCommandOperationId = null, string? Code = null)
{
    public static readonly ReconciliationStep Idle = new(ReconciliationStepKind.Idle);
}

/// <summary>
/// Phase C8 — C8-M1-01 / C8-M1-02 (Design R, package §4–§6): the single logical writer of the team-membership roster
/// projection (D8-21, I-10). Each step leases one due operation (FOR UPDATE SKIP LOCKED), asks the Authority through the
/// read-only AC-9 lookup under the reconciler credential, validates the answer (§1.3), and projects the Authority's effective
/// membership of the operation's pair at revision P under the per-pair watermark W:
/// <list type="bullet">
/// <item>E &gt; W: roster := Authority state, W := E, operation completed — one transaction holding the watermark lock.</item>
/// <item>E = W: roster unchanged unless it drifted, which is corrected to the Authority state and audited (Q-5, proposed answer).</item>
/// <item>E &lt; W: the answer is older than another writer's projection: discarded, one fresh lookup (R-6); a fresh answer still
/// below W is LOCAL_WATERMARK_AHEAD (integrity anomaly, nothing projected).</item>
/// </list>
/// The reconciler never submits, replays or alters Authority state (I-5) and never uses a stored local state, an earlier
/// attempt's answer or a browser report as evidence (I-1, D8-20).
/// </summary>
public sealed class TeamMembershipReconciliationService(IBusinessStore store, ITeamMembershipOutcomeAuthority authority, TeamMembershipReconciliationOptions options, TimeProvider clock)
{
    private const int EarlyPollAttempts = 5;

    private DateTimeOffset? credentialRefusedSince;

    /// <summary>True while the Authority refuses the reconciler credential (RC-25); no operation state changes meanwhile.</summary>
    public bool CredentialUnavailable => credentialRefusedSince is not null;

    public async Task<ReconciliationStep> ProcessOnceAsync(string worker, CancellationToken cancellationToken)
    {
        AuthorityCommandOperation? operation;
        await using (var scope = await store.BeginScopeAsync(cancellationToken).ConfigureAwait(false))
        {
            operation = await scope.LeaseNextAuthorityCommandOperationAsync(worker, options.Lease, cancellationToken).ConfigureAwait(false);
            if (operation is null)
            {
                return ReconciliationStep.Idle;
            }

            await scope.CommitAsync(cancellationToken).ConfigureAwait(false);
        }

        try
        {
            return operation.OperationStateName == AuthorityCommandOperationStates.NotCommitted
                ? await SweepLateCommitAsync(operation, worker, cancellationToken).ConfigureAwait(false)
                : await ReconcileAsync(operation, worker, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Store failure (for example a roster write refused inside the apply transaction): everything of that transaction
            // rolled back; back off and try again with a fresh lookup. Exception text is never stored (no values, no keys).
            await TransientErrorAsync(operation.AuthorityCommandOperationId, worker, "RECONCILER_STEP_FAILED:" + ex.GetType().Name, cancellationToken).ConfigureAwait(false);
            return new ReconciliationStep(ReconciliationStepKind.TransientError, operation.AuthorityCommandOperationId, ex.GetType().Name);
        }
    }

    private async Task<ReconciliationStep> ReconcileAsync(AuthorityCommandOperation operation, string worker, CancellationToken cancellationToken)
    {
        var id = operation.AuthorityCommandOperationId;
        if (!TeamMembershipCommandKinds.PrefixMatches(operation.CommandKindName, operation.AuthorityIdempotencyKey))
        {
            return await FailAsync(operation, worker, TeamMembershipReconciliationErrors.OperationMismatch, "key prefix does not match the command kind", cancellationToken).ConfigureAwait(false);
        }

        var (answer, failure) = await LookupAsync(operation, worker, cancellationToken).ConfigureAwait(false);
        if (answer is null)
        {
            return failure!;
        }

        if (!answer.Found)
        {
            return await NotFoundAsync(operation, worker, cancellationToken).ConfigureAwait(false);
        }

        switch (answer.CommandState)
        {
            case "Rejected":
                await UpdateAsync(id, worker, o =>
                {
                    o.AuthorityCommandId ??= answer.SecurityCommandId;
                    o.OperationStateName = AuthorityCommandOperationStates.Rejected;
                    o.RejectionReasonCode = Truncate(answer.RejectionReasonCode ?? "COMMAND_REJECTED", 80);
                    o.OperationCompletedOn = clock.GetUtcNow();
                }, cancellationToken).ConfigureAwait(false);
                return new ReconciliationStep(ReconciliationStepKind.Terminal, id, AuthorityCommandOperationStates.Rejected);
            case "FailedReconciliation":
                await UpdateAsync(id, worker, o =>
                {
                    o.AuthorityCommandId ??= answer.SecurityCommandId;
                    o.CommittedAuthorizationRevision ??= answer.CommittedAuthorizationRevision;
                    o.OperationStateName = AuthorityCommandOperationStates.AuthorityFailed;
                    o.LastReconciliationErrorCode = "FAILED_RECONCILIATION";
                    o.OperationCompletedOn = clock.GetUtcNow();
                }, cancellationToken, o => FailureAudit(o, AuthorityCommandOperationStates.AuthorityFailed, "the Authority marked the command FailedReconciliation; nothing projected", answer.SecurityCommandId)).ConfigureAwait(false);
                return new ReconciliationStep(ReconciliationStepKind.Anomaly, id, AuthorityCommandOperationStates.AuthorityFailed);
        }

        if (answer.OrganizationState!.IntegrityQuarantined)
        {
            // §4.6 / I-7: status only, nothing projected, no operator action can project; resume after the Authority's clearance.
            await WaitAsync(id, worker, AuthorityCommandOperationStates.AwaitingQuarantineClearance, answer, options.Backoff(int.MaxValue), cancellationToken).ConfigureAwait(false);
            return new ReconciliationStep(ReconciliationStepKind.Waiting, id, AuthorityCommandOperationStates.AwaitingQuarantineClearance);
        }

        if (answer.CommandState == "CommittedAwaitingProjection")
        {
            // Committed commands usually become Effective within seconds: poll each second a few times, then back off (D8-16).
            var delay = operation.AttemptCount < EarlyPollAttempts ? TimeSpan.FromSeconds(1) : options.Backoff(operation.AttemptCount + 1 - EarlyPollAttempts);
            await WaitAsync(id, worker, AuthorityCommandOperationStates.AwaitingEffective, answer, delay, cancellationToken).ConfigureAwait(false);
            return new ReconciliationStep(ReconciliationStepKind.Waiting, id, AuthorityCommandOperationStates.AwaitingEffective);
        }

        // Effective; validation guarantees committed ≤ P = evaluated revision and a non-quarantined membership (D8-15).
        return await ApplyAsync(operation, worker, answer, allowFreshLookup: true, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Package §4.2 with R-6; roster, watermark and completion in one transaction under the pair lock (D8-21).</summary>
    private async Task<ReconciliationStep> ApplyAsync(AuthorityCommandOperation leased, string worker, SecurityCommandOutcomeAnswer answer, bool allowFreshLookup, CancellationToken cancellationToken)
    {
        var id = leased.AuthorityCommandOperationId;
        var membership = answer.Membership!;
        var staleAnswer = false;
        await using (var scope = await store.BeginScopeAsync(cancellationToken).ConfigureAwait(false))
        {
            var operation = await scope.FindAuthorityCommandOperationAsync(id, lockForUpdate: true, cancellationToken).ConfigureAwait(false);
            if (operation is null || operation.IsTerminal || !string.Equals(operation.LeaseOwnerName, worker, StringComparison.Ordinal))
            {
                return new ReconciliationStep(ReconciliationStepKind.Waiting, id, "LEASE_LOST");
            }

            var watermark = await scope.LockTeamMembershipProjectionStateAsync(operation.TenantId, operation.TeamId, operation.MemberUserId, cancellationToken).ConfigureAwait(false);
            var e = membership.EffectiveMembershipRevision;
            if (e < watermark.ProjectedMembershipRevision)
            {
                staleAnswer = true; // discarded: the scope is disposed without commit (I-11)
            }
            else
            {
                var now = clock.GetUtcNow();
                var roster = await scope.FindActiveTeamMembershipProjectionAsync(operation.TenantId, operation.TeamId, operation.MemberUserId, cancellationToken).ConfigureAwait(false);
                var present = membership.EffectiveMember;
                string outcome;
                var drift = false;
                if (e > watermark.ProjectedMembershipRevision)
                {
                    SetRoster(scope, operation, answer, roster, present, now);
                    outcome = present ? AuthorityCommandOperationOutcomes.MembershipPresentProjected : AuthorityCommandOperationOutcomes.MembershipAbsentProjected;
                }
                else if (watermark.ProjectedMembershipPresent != present || (roster is not null) != present)
                {
                    // Same revision, different local state: the roster or the recorded state was changed outside the reconciler.
                    // Display data only: corrected to the Authority state and audited (Q-5, proposed answer; reviewer decision pending).
                    SetRoster(scope, operation, answer, roster, present, now);
                    outcome = AuthorityCommandOperationOutcomes.DriftCorrected;
                    drift = true;
                }
                else
                {
                    outcome = AuthorityCommandOperationOutcomes.ProjectionUnchanged;
                }

                watermark.ProjectedMembershipPresent = present;
                watermark.ProjectedMembershipRevision = e;
                watermark.LastAuthorityCommandOperationId = operation.AuthorityCommandOperationId;
                watermark.ProjectionStateModifiedOn = now;

                operation.AuthorityCommandId ??= answer.SecurityCommandId;
                operation.CommittedAuthorizationRevision ??= answer.CommittedAuthorizationRevision;
                operation.OperationStateName = AuthorityCommandOperationStates.Projected;
                operation.OperationOutcomeCode = outcome;
                operation.EvaluatedProjectionRevision = membership.EvaluatedAtRevision;
                operation.ProjectedMembershipRevision = e;
                operation.ProjectedMembershipPresent = present;
                operation.SupersededByLaterRevision = e > answer.CommittedAuthorizationRevision;
                operation.OperationCompletedOn = now;
                operation.ConsecutiveErrorCount = 0;
                operation.LastReconciliationErrorCode = null;
                operation.LeaseOwnerName = null;
                operation.LeaseExpiresOn = null;
                operation.OperationModifiedOn = now;
                if (drift)
                {
                    scope.AddAuditEvent(ServiceAudit(operation, AuditActions.TeamMembershipProjectionDrift, "Success", "team roster differed from the Authority at the same membership revision; corrected to the Authority state", new { authorityCommandOperationId = operation.AuthorityCommandOperationId, initiatedByUserId = operation.InitiatedByUserId, memberUserId = operation.MemberUserId, effectiveMembershipRevision = e, effectiveMember = present }));
                }

                await scope.CommitAsync(cancellationToken).ConfigureAwait(false);
                return new ReconciliationStep(ReconciliationStepKind.Projected, id, outcome);
            }
        }

        if (!staleAnswer)
        {
            return new ReconciliationStep(ReconciliationStepKind.Waiting, id);
        }

        if (allowFreshLookup)
        {
            // R-6: a fresh lookup starts after W was committed; P never decreases (§1.2.6), so a genuine answer now has E ≥ W.
            var (fresh, failure) = await LookupAsync(leased, worker, cancellationToken).ConfigureAwait(false);
            if (fresh is null)
            {
                return failure!;
            }

            if (fresh.Found && fresh.CommandState == "Effective" && fresh.Membership is not null)
            {
                return await ApplyAsync(leased, worker, fresh, allowFreshLookup: false, cancellationToken).ConfigureAwait(false);
            }

            return await FailAsync(leased, worker, TeamMembershipReconciliationErrors.InvalidAuthorityResponse, "a fresh lookup no longer reports the command Effective with membership", cancellationToken).ConfigureAwait(false);
        }

        return await FailAsync(leased, worker, TeamMembershipReconciliationErrors.LocalWatermarkAhead, "the local watermark names a pair change the Authority does not report", cancellationToken).ConfigureAwait(false);
    }

    private static void SetRoster(IBusinessScope scope, AuthorityCommandOperation operation, SecurityCommandOutcomeAnswer answer, TeamMembership? roster, bool present, DateTimeOffset now)
    {
        if (present && roster is null)
        {
            scope.AddTeamMembershipProjection(new TeamMembership
            {
                TeamMembershipId = Guid.CreateVersion7(),
                TenantId = operation.TenantId,
                TeamId = operation.TeamId,
                UserId = operation.MemberUserId,
                MembershipRole = "Member",
                // The command that made the membership effective is known only when the pair's latest change is this command.
                AuthorityCommandId = answer.Membership!.EffectiveMembershipRevision == answer.CommittedAuthorizationRevision ? answer.SecurityCommandId : null,
                CreatedOn = now,
                CreatedByUserId = operation.InitiatedByUserId,
            });
        }
        else if (!present && roster is not null)
        {
            roster.RemovedOn = now;
        }
    }

    private async Task<(SecurityCommandOutcomeAnswer? Answer, ReconciliationStep? Failure)> LookupAsync(AuthorityCommandOperation operation, string worker, CancellationToken cancellationToken)
    {
        var id = operation.AuthorityCommandOperationId;
        var correlation = Guid.CreateVersion7();
        SecurityCommandOutcomeAnswer answer;
        try
        {
            answer = await authority.LookupAsync(operation.TenantId, operation.AuthorityIdempotencyKey, operation.CommandKindName, correlation, cancellationToken).ConfigureAwait(false);
        }
        catch (ReconcilerLookupException ex)
        {
            switch (ex.Failure)
            {
                case ReconcilerLookupFailure.NotConfigured:
                case ReconcilerLookupFailure.CredentialRefused:
                    // RC-25: the reconciler is Unavailable; operation state, attempts and error budget stay untouched.
                    credentialRefusedSince ??= clock.GetUtcNow();
                    await ReleaseAsync(id, worker, clock.GetUtcNow().Add(options.Backoff(int.MaxValue)), cancellationToken).ConfigureAwait(false);
                    return (null, new ReconciliationStep(ReconciliationStepKind.CredentialRefused, id, ex.ErrorCode));
                case ReconcilerLookupFailure.RateLimited:
                    // RC-27: Retry-After honoured; no error budget consumed.
                    await ReleaseAsync(id, worker, clock.GetUtcNow().Add(ex.RetryAfter ?? TimeSpan.FromSeconds(1)), cancellationToken).ConfigureAwait(false);
                    return (null, new ReconciliationStep(ReconciliationStepKind.RateLimited, id, ex.ErrorCode));
                case ReconcilerLookupFailure.Refused:
                    return (null, await FailAsync(operation, worker, TeamMembershipReconciliationErrors.InvalidAuthorityResponse, "the Authority refused or garbled the lookup: " + ex.ErrorCode, cancellationToken).ConfigureAwait(false));
                default:
                    await TransientErrorAsync(id, worker, ex.ErrorCode, cancellationToken).ConfigureAwait(false);
                    return (null, new ReconciliationStep(ReconciliationStepKind.TransientError, id, ex.ErrorCode));
            }
        }

        credentialRefusedSince = null;
        var validation = TeamMembershipOutcomeValidator.Validate(operation, correlation, answer);
        if (!validation.IsValid)
        {
            var code = validation.Verdict == OutcomeValidationVerdict.OperationMismatch ? TeamMembershipReconciliationErrors.OperationMismatch : TeamMembershipReconciliationErrors.InvalidAuthorityResponse;
            return (null, await FailAsync(operation, worker, code, validation.Reason, cancellationToken).ConfigureAwait(false));
        }

        return (answer, null);
    }

    /// <summary>Package §6.2: NotCommitted only after the in-flight window and two not-found answers at least one window apart; never re-sent (I-8).</summary>
    private async Task<ReconciliationStep> NotFoundAsync(AuthorityCommandOperation operation, string worker, CancellationToken cancellationToken)
    {
        var id = operation.AuthorityCommandOperationId;
        if (operation.AuthorityCommandId is not null)
        {
            return await FailAsync(operation, worker, TeamMembershipReconciliationErrors.CommandNotFound, "a command recorded as committed is unknown to the Authority", cancellationToken).ConfigureAwait(false);
        }

        var now = clock.GetUtcNow();
        var window = options.InFlightWindow;
        if (now - operation.OperationCreatedOn < window)
        {
            await UpdateAsync(id, worker, o => o.NextAttemptOn = o.OperationCreatedOn.Add(window), cancellationToken).ConfigureAwait(false);
            return new ReconciliationStep(ReconciliationStepKind.Waiting, id, "IN_FLIGHT");
        }

        if (operation.FirstNotFoundOn is not { } first)
        {
            await UpdateAsync(id, worker, o =>
            {
                o.FirstNotFoundOn = now;
                o.AttemptCount++;
                o.NextAttemptOn = now.Add(window);
            }, cancellationToken).ConfigureAwait(false);
            return new ReconciliationStep(ReconciliationStepKind.Waiting, id, "FIRST_NOT_FOUND");
        }

        if (now - first < window)
        {
            await UpdateAsync(id, worker, o => o.NextAttemptOn = first.Add(window), cancellationToken).ConfigureAwait(false);
            return new ReconciliationStep(ReconciliationStepKind.Waiting, id, "AWAITING_SECOND_NOT_FOUND");
        }

        await UpdateAsync(id, worker, o =>
        {
            o.OperationStateName = AuthorityCommandOperationStates.NotCommitted;
            o.OperationCompletedOn = now;
            o.AttemptCount++;
            o.LateCommitSweepUntil = now.AddDays(options.LateCommitSweepDays);
            o.NextAttemptOn = now.AddSeconds(options.LateCommitSweepIntervalSeconds);
        }, cancellationToken, o => FailureAudit(o, AuthorityCommandOperationStates.NotCommitted, "the Authority holds no command for this operation after the in-flight window; never re-sent", null)).ConfigureAwait(false);
        return new ReconciliationStep(ReconciliationStepKind.Terminal, id, AuthorityCommandOperationStates.NotCommitted);
    }

    /// <summary>Late-commit sweep of a NotCommitted operation: detection is recorded and audited; the terminal state never re-opens (I-9).</summary>
    private async Task<ReconciliationStep> SweepLateCommitAsync(AuthorityCommandOperation operation, string worker, CancellationToken cancellationToken)
    {
        var id = operation.AuthorityCommandOperationId;
        var correlation = Guid.CreateVersion7();
        SecurityCommandOutcomeAnswer answer;
        try
        {
            answer = await authority.LookupAsync(operation.TenantId, operation.AuthorityIdempotencyKey, operation.CommandKindName, correlation, cancellationToken).ConfigureAwait(false);
        }
        catch (ReconcilerLookupException ex)
        {
            await ReleaseAsync(id, worker, clock.GetUtcNow().Add(ex.RetryAfter ?? options.Backoff(int.MaxValue)), cancellationToken).ConfigureAwait(false);
            return new ReconciliationStep(ex.Failure == ReconcilerLookupFailure.CredentialRefused ? ReconciliationStepKind.CredentialRefused : ReconciliationStepKind.TransientError, id, ex.ErrorCode);
        }

        if (answer.Found && answer.CorrelationId == correlation && answer.OrganizationId == operation.TenantId)
        {
            await UpdateAsync(id, worker, o => o.LateCommitDetectedOn = clock.GetUtcNow(), cancellationToken, o => FailureAudit(o, TeamMembershipReconciliationErrors.LateCommitDetected, "the Authority committed a command after the operation was declared NotCommitted; operator review required", answer.SecurityCommandId)).ConfigureAwait(false);
            return new ReconciliationStep(ReconciliationStepKind.Anomaly, id, TeamMembershipReconciliationErrors.LateCommitDetected);
        }

        await UpdateAsync(id, worker, o => o.NextAttemptOn = clock.GetUtcNow().AddSeconds(options.LateCommitSweepIntervalSeconds), cancellationToken).ConfigureAwait(false);
        return new ReconciliationStep(ReconciliationStepKind.Waiting, id, "LATE_COMMIT_SWEEP");
    }

    private Task WaitAsync(Guid id, string worker, string state, SecurityCommandOutcomeAnswer answer, TimeSpan delay, CancellationToken cancellationToken) =>
        UpdateAsync(id, worker, o =>
        {
            var now = clock.GetUtcNow();
            o.AuthorityCommandId ??= answer.SecurityCommandId;
            o.CommittedAuthorizationRevision ??= answer.CommittedAuthorizationRevision;
            o.OperationStateName = state;
            o.AttemptCount++;
            o.ConsecutiveErrorCount = 0;
            o.NextAttemptOn = now.Add(delay);
            FlagIfOverdue(o, now);
        }, cancellationToken);

    private Task TransientErrorAsync(Guid id, string worker, string code, CancellationToken cancellationToken) =>
        UpdateAsync(id, worker, o =>
        {
            var now = clock.GetUtcNow();
            o.AttemptCount++;
            o.ConsecutiveErrorCount++;
            o.LastReconciliationErrorCode = Truncate(code, 80);
            o.NextAttemptOn = now.Add(options.Backoff(o.ConsecutiveErrorCount));
            FlagIfOverdue(o, now);
        }, cancellationToken);

    private void FlagIfOverdue(AuthorityCommandOperation operation, DateTimeOffset now)
    {
        if (operation.NeedsAttentionFlaggedOn is null && (now - operation.OperationCreatedOn >= options.NeedsAttention || operation.ConsecutiveErrorCount >= options.ConsecutiveErrorBudget))
        {
            operation.NeedsAttentionFlaggedOn = now;
        }
    }

    private Task ReleaseAsync(Guid id, string worker, DateTimeOffset nextAttemptOn, CancellationToken cancellationToken) =>
        UpdateAsync(id, worker, o => o.NextAttemptOn = nextAttemptOn, cancellationToken);

    private async Task<ReconciliationStep> FailAsync(AuthorityCommandOperation operation, string worker, string code, string reason, CancellationToken cancellationToken)
    {
        await UpdateAsync(operation.AuthorityCommandOperationId, worker, o =>
        {
            var now = clock.GetUtcNow();
            o.OperationStateName = AuthorityCommandOperationStates.ReconciliationError;
            o.LastReconciliationErrorCode = code;
            o.AttemptCount++;
            o.NeedsAttentionFlaggedOn ??= now;
            o.NextAttemptOn = now;
        }, cancellationToken, o => FailureAudit(o, code, reason, null)).ConfigureAwait(false);
        return new ReconciliationStep(ReconciliationStepKind.Anomaly, operation.AuthorityCommandOperationId, code);
    }

    /// <summary>Changes the leased operation under its row lock; skipped when another worker has taken it over (lease lost).</summary>
    private async Task UpdateAsync(Guid id, string worker, Action<AuthorityCommandOperation> change, CancellationToken cancellationToken, Func<AuthorityCommandOperation, AuditEvent>? audit = null)
    {
        await using var scope = await store.BeginScopeAsync(cancellationToken).ConfigureAwait(false);
        var operation = await scope.FindAuthorityCommandOperationAsync(id, lockForUpdate: true, cancellationToken).ConfigureAwait(false);
        if (operation is null || !string.Equals(operation.LeaseOwnerName, worker, StringComparison.Ordinal))
        {
            return;
        }

        change(operation);
        operation.LeaseOwnerName = null;
        operation.LeaseExpiresOn = null;
        operation.OperationModifiedOn = clock.GetUtcNow();
        if (audit is not null)
        {
            scope.AddAuditEvent(audit(operation));
        }

        await scope.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private AuditEvent FailureAudit(AuthorityCommandOperation operation, string code, string reason, Guid? securityCommandId) =>
        ServiceAudit(operation, AuditActions.TeamMembershipReconciliationFailed, "Failed", "team membership reconciliation: " + reason, new
        {
            authorityCommandOperationId = operation.AuthorityCommandOperationId,
            failureCode = code,
            commandKind = operation.CommandKindName,
            initiatedByUserId = operation.InitiatedByUserId,
            memberUserId = operation.MemberUserId,
            securityCommandId = securityCommandId ?? operation.AuthorityCommandId,
        });

    /// <summary>D8-17: service actor; the initiator appears in metadata only. Never the idempotency key.</summary>
    internal AuditEvent ServiceAudit(AuthorityCommandOperation operation, string action, string outcome, string description, object metadata) => new()
    {
        AuditEventId = Guid.CreateVersion7(),
        TenantId = operation.TenantId,
        EventOccurredOn = clock.GetUtcNow(),
        EventCategory = "Security",
        EventAction = action,
        EventOutcome = outcome,
        ActorUserId = null,
        ActorType = "Service",
        TargetEntityType = "team",
        TargetEntityId = operation.TeamId,
        CorrelationIdentifier = operation.RequestCorrelationIdentifier,
        EventDescription = Truncate(description, 500),
        EventMetadataJson = JsonSerializer.Serialize(metadata),
        EventSchemaVersion = 1,
    };

    private static string Truncate(string value, int length) => value.Length <= length ? value : value[..length];
}
