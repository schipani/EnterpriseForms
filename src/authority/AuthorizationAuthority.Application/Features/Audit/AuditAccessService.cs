using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using AuthorizationAuthority.Application.Common;
using AuthorizationAuthority.Application.Common.Ports;
using AuthorizationAuthority.Application.Features.Authorization;
using AuthorizationAuthority.Application.Features.Journal;
using AuthorizationAuthority.Contracts.V1;
using AuthorizationAuthority.Domain.Features.Organizations;

namespace AuthorizationAuthority.Application.Features.Audit;

/// <summary>
/// C8 M4-2 (AC-8.3, PHASE_C8_M4_AC8_SECURITY_CONTRACT.md §5.3): authorizes one read of the business audit projection and commits
/// the evidence before answering. Protocol, per attempt (at most <see cref="AuditAccessOptions.MaximumAttempts"/>):
/// <list type="number">
/// <item>the view_audit check as for every permission (gate, pinned SpiceDB decision, verify; it binds the actor and refuses a
/// disabled binding), capturing authorization revision R and projection revision P;</item>
/// <item>a new AuthorityDb transaction bounded by <c>statement_timeout</c>, <c>transaction_timeout</c> (PostgreSQL ≥ 17) and an
/// application deadline of the same length;</item>
/// <item><c>FOR UPDATE NOWAIT</c> on the organization row: a busy lock means rollback, jittered backoff and a new attempt, so an
/// audit read never queues in front of a security command;</item>
/// <item>under the lock: R and P unchanged, no integrity quarantine and the binding still enabled, otherwise a new attempt (a
/// revocation committed in between is then denied by the fresh check);</item>
/// <item><c>AuditAccessAuthorized</c> appended to the audit stream and committed. Only a successful commit returns an allow.</item>
/// </list>
/// A failed, timed-out or unknown commit is never an allow (503 <c>AUDIT_EVIDENCE_UNAVAILABLE</c>). The entry records
/// authorization, never delivery (<c>delivery: not_attested</c>), and no result count from the business (Q-2). There is no
/// atomicity across SpiceDB and AuthorityDb: the decision is bound to R only by the comparison under the lock. A denial returns
/// 403 at once; its <c>AuditAccessDenied</c> entry is best effort under the same no-wait rule and the denial never depends on it.
/// </summary>
public sealed partial class AuditAccessService(IAuthorityStore store, IOrganizationPermissionCheck permissions, IAuditAccessRateLimiter rateLimiter, AuditAccessOptions options, AuditAccessTelemetry telemetry, TimeProvider clock)
{
    public const string AuthorizedEventKind = "AuditAccessAuthorized";

    public const string DeniedEventKind = "AuditAccessDenied";

    public const string NotAttested = "not_attested";

    /// <summary>C8 M4-3 (AC-8.2): the timeline page is read and served by the Authority itself.</summary>
    public const string ServedByAuthority = "authority";

    public const int MaximumPageSize = 50;

    [GeneratedRegex("^[0-9a-f]{64}$", RegexOptions.CultureInvariant)]
    private static partial Regex FilterDigestPattern();

    /// <summary>
    /// AC-8.3: an explorer page or an event detail for the business audit explorer. The wire kind selects only between those two
    /// modes (<see cref="ExplorerScope"/>); no request value can select the timeline mode, which only
    /// <see cref="AuthorizeAndReadTimelineAsync{T}"/> sets.
    /// </summary>
    public async Task<AuditAccessDecision> AuthorizeAsync(VerifiedActor actor, AuditAccessRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);
        var scope = ExplorerScope(request);
        var (decision, _) = await AuthorizeCoreAsync<object?>(actor, scope, null, cancellationToken).ConfigureAwait(false);
        return decision;
    }

    /// <summary>
    /// C8 M4-3 (AC-8.2, contract §5.2): the §5.3 protocol with the page read inside the same AuthorityDb transaction, under the
    /// organization lock and the TC-3 bounds, after the revision comparison and before the evidence is appended. The page and the
    /// entry come from one AuthorityDb snapshot (not SpiceDB); a page is returned only when the entry committed. The mode is
    /// always <see cref="AuditAccessMode.CommandTimelinePage"/>, set here and never taken from the request.
    /// </summary>
    public async Task<(AuditAccessDecision Decision, T Page)> AuthorizeAndReadTimelineAsync<T>(VerifiedActor actor, AuditTimelineAccess request,
        Func<IAuthorityScope, OrganizationAuthorizationState, CancellationToken, Task<T>> read, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(read);
        var scope = TimelineScope(request);
        var (decision, page) = await AuthorizeCoreAsync(actor, scope, read, cancellationToken).ConfigureAwait(false);
        return (decision, page!);
    }

    private async Task<(AuditAccessDecision Decision, T? Page)> AuthorizeCoreAsync<T>(VerifiedActor actor, AccessScope request,
        Func<IAuthorityScope, OrganizationAuthorizationState, CancellationToken, Task<T>>? read, CancellationToken cancellationToken)
    {
        if (!rateLimiter.TryAcquire(request.OrganizationId, actor))
        {
            telemetry.Refused("rate_limited");
            throw new AuthorityException(AuthorityErrorCodes.RateLimited, 429, true, "audit access rate limit");
        }

        var check = new PermissionCheckRequest(request.OrganizationId, new ResourceRef(ResourceKind.Organization, request.OrganizationId), RequestedAction.ViewAudit);
        for (var attempt = 1; attempt <= options.MaximumAttempts; attempt++)
        {
            // Step 2: gate closed, quarantine, unbound or disabled actor all raise here, before any lock is taken.
            var decision = await permissions.CheckAsync(actor, check, request.CorrelationId, cancellationToken).ConfigureAwait(false);
            if (!decision.Allowed)
            {
                await RecordDeniedAsync(actor, request, decision, cancellationToken).ConfigureAwait(false);
                telemetry.Refused("forbidden");
                throw new AuthorityException(AuthorityErrorCodes.AuditViewForbidden, 403, false, "audit is available to Security Auditors");
            }

            var outcome = await TryRecordAsync(actor, request, decision, read, cancellationToken).ConfigureAwait(false);
            if (outcome.JournalRevision is { } journalRevision)
            {
                telemetry.Authorized(attempt, outcome.Hold);
                return (new AuditAccessDecision(true, journalRevision, decision.AuthorizationRevision, NotAttested, request.CorrelationId), outcome.Page);
            }

            telemetry.AttemptFailed(outcome.Reason!);
            if (attempt < options.MaximumAttempts)
            {
                var backoff = RandomNumberGenerator.GetInt32(options.RetryBackoffMinimumMilliseconds, options.RetryBackoffMaximumMilliseconds + 1);
                await Task.Delay(TimeSpan.FromMilliseconds(backoff), clock, cancellationToken).ConfigureAwait(false);
            }
        }

        telemetry.Refused("busy");
        throw new AuthorityException(AuthorityErrorCodes.AuditEvidenceBusy, 503, true, "audit evidence could not be recorded within the bounded attempts; nothing was served");
    }

    /// <summary>
    /// The audit-explorer route's mode: ExplorerPage or EventDetail only. Any other wire value (including an undefined numeric
    /// value) is refused before any check, so a caller can never reach the timeline mode, or a mode without its shape rules.
    /// </summary>
    private static AccessScope ExplorerScope(AuditAccessRequest? request)
    {
        if (request is null || request.FilterDigest is null)
        {
            throw AuthorityException.Invalid("audit access: organization, correlation id, a known access kind and a 64-character lower-case hex filter digest are required");
        }

        var mode = request.AccessKind switch
        {
            AuditAccessKind.ExplorerPage => AuditAccessMode.ExplorerPage,
            AuditAccessKind.EventDetail => AuditAccessMode.EventDetail,
            _ => throw AuthorityException.Invalid("audit access: the access kind must be ExplorerPage or EventDetail"),
        };
        var scope = new AccessScope(mode, request.OrganizationId, request.FilterDigest, request.PageSize, request.AuditEventId, request.CorrelationId);
        ValidateShape(scope);
        return scope;
    }

    private static AccessScope TimelineScope(AuditTimelineAccess? request)
    {
        if (request is null || request.FilterDigest is null)
        {
            throw AuthorityException.Invalid("audit access: organization, correlation id and a 64-character lower-case hex filter digest are required");
        }

        var scope = new AccessScope(AuditAccessMode.CommandTimelinePage, request.OrganizationId, request.FilterDigest, request.PageSize, null, request.CorrelationId);
        ValidateShape(scope);
        return scope;
    }

    private static void ValidateShape(AccessScope scope)
    {
        if (scope.OrganizationId == Guid.Empty || scope.CorrelationId == Guid.Empty || !FilterDigestPattern().IsMatch(scope.FilterDigest))
        {
            throw AuthorityException.Invalid("audit access: organization, correlation id, a known access kind and a 64-character lower-case hex filter digest are required");
        }

        var valid = scope.Mode switch
        {
            AuditAccessMode.ExplorerPage => scope.PageSize is >= 1 and <= MaximumPageSize && scope.AuditEventId is null,
            AuditAccessMode.EventDetail => scope.PageSize == 1 && scope.AuditEventId is { } id && id != Guid.Empty,
            AuditAccessMode.CommandTimelinePage => scope.PageSize is >= 1 and <= MaximumPageSize && scope.AuditEventId is null,
            _ => false,
        };
        if (!valid)
        {
            throw AuthorityException.Invalid("audit access: an explorer or timeline page has page_size 1..50 and no event id; an event detail has page_size 1 and an event id");
        }
    }

    private async Task<RecordOutcome<T>> TryRecordAsync<T>(VerifiedActor actor, AccessScope request, PermissionDecision decision,
        Func<IAuthorityScope, OrganizationAuthorizationState, CancellationToken, Task<T>>? read, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromMilliseconds(options.TransactionTimeoutMilliseconds));
        var token = deadline.Token;
        try
        {
            await using var scope = await store.BeginScopeAsync(token).ConfigureAwait(false);
            var access = scope as IAuditAccessScope ?? throw Unavailable("the store cannot bound an audit-access transaction");
            await access.ApplyAuditAccessTimeoutsAsync(options.StatementTimeoutMilliseconds, options.TransactionTimeoutMilliseconds, token).ConfigureAwait(false);
            var lockStarted = Stopwatch.GetTimestamp();
            var attempt = await access.TryLockOrganizationNoWaitAsync(request.OrganizationId, token).ConfigureAwait(false);
            if (attempt.Busy)
            {
                return RecordOutcome<T>.Failed("lock_busy");
            }

            var state = attempt.State ?? throw AuthorityException.NotRegistered("organization is not registered");

            // Step 5: the decision is only recorded under the revision it was taken at.
            if (state.AuthorizationRevision != decision.AuthorizationRevision || state.ProjectionAppliedRevision != decision.ProjectionRevision || state.IntegrityQuarantineEnabled)
            {
                return RecordOutcome<T>.Failed("state_moved");
            }

            var bound = await ActorResolver.BindAsync(scope, request.OrganizationId, actor, token).ConfigureAwait(false);

            // AC-8.2: the page is read here, in this transaction and under this lock, before the evidence; it is discarded unless the
            // entry below commits.
            var timeline = request.Mode == AuditAccessMode.CommandTimelinePage;
            if (timeline != (read is not null))
            {
                throw new InvalidOperationException("audit access: the timeline mode and the page reader go together");
            }

            var page = read is null ? default : await read(scope, state, token).ConfigureAwait(false);
            object body = !timeline ? new
            {
                access_kind = request.Mode.ToString(),
                actor_authority_user_id = bound.AuthorityUserId,
                actor_identity_binding_id = bound.Binding.IdentityBindingId,
                authorization_revision = decision.AuthorizationRevision,
                projection_revision = decision.ProjectionRevision,
                filter_digest = request.FilterDigest,
                page_size = request.PageSize,
                audit_event_id = request.AuditEventId,
                correlation_id = request.CorrelationId,
                delivery = NotAttested,
            }
            : new
            {
                access_kind = request.Mode.ToString(),
                actor_authority_user_id = bound.AuthorityUserId,
                actor_identity_binding_id = bound.Binding.IdentityBindingId,
                authorization_revision = decision.AuthorizationRevision,
                projection_revision = decision.ProjectionRevision,
                filter_digest = request.FilterDigest,
                page_size = request.PageSize,
                audit_event_id = request.AuditEventId,
                correlation_id = request.CorrelationId,
                served_by = ServedByAuthority,
                delivery = NotAttested, // R-5: the read is recorded, never the client's receipt
            };
            var entry = AuditJournal.Append(scope, state, AuthorizedEventKind, body, clock);
            await scope.CommitAsync(token).ConfigureAwait(false);
            return RecordOutcome<T>.Recorded(entry.JournalStreamRevision, Stopwatch.GetElapsedTime(lockStarted), page);
        }
        catch (AuthorityException)
        {
            throw;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            telemetry.Refused("deadline");
            throw Unavailable("the audit-access transaction exceeded its deadline; nothing was served");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A failed or unknown commit is never an allow (step 7).
            telemetry.Refused("commit_failed");
            throw Unavailable("audit access could not be recorded; nothing was served");
        }
    }

    /// <summary>Best effort: a busy lock, a moved state or any failure skips the entry (counted); the 403 never depends on it.</summary>
    private async Task RecordDeniedAsync(VerifiedActor actor, AccessScope request, PermissionDecision decision, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromMilliseconds(options.TransactionTimeoutMilliseconds));
        try
        {
            await using var scope = await store.BeginScopeAsync(deadline.Token).ConfigureAwait(false);
            if (scope is not IAuditAccessScope access)
            {
                telemetry.DeniedUnrecorded();
                return;
            }

            await access.ApplyAuditAccessTimeoutsAsync(options.StatementTimeoutMilliseconds, options.TransactionTimeoutMilliseconds, deadline.Token).ConfigureAwait(false);
            var attempt = await access.TryLockOrganizationNoWaitAsync(request.OrganizationId, deadline.Token).ConfigureAwait(false);
            if (attempt.State is not { } state)
            {
                telemetry.DeniedUnrecorded();
                return;
            }

            var bound = await ActorResolver.BindAsync(scope, request.OrganizationId, actor, deadline.Token).ConfigureAwait(false);
            AuditJournal.Append(scope, state, DeniedEventKind, new
            {
                access_kind = request.Mode.ToString(),
                actor_authority_user_id = bound.AuthorityUserId,
                authorization_revision = decision.AuthorizationRevision,
                denial_code = decision.DenialReasonCode ?? AuthorityErrorCodes.Forbidden,
                filter_digest = request.FilterDigest,
                audit_event_id = request.AuditEventId,
                correlation_id = request.CorrelationId,
            }, clock);
            await scope.CommitAsync(deadline.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            telemetry.DeniedUnrecorded();
        }
    }

    private static AuthorityException Unavailable(string detail) => new(AuthorityErrorCodes.AuditEvidenceUnavailable, 503, true, detail);

    /// <summary>The server-determined access mode and request shape; the mode is set by the entry point, never by a caller.</summary>
    private sealed record AccessScope(AuditAccessMode Mode, Guid OrganizationId, string FilterDigest, int PageSize, Guid? AuditEventId, Guid CorrelationId);

    private sealed record RecordOutcome<T>(long? JournalRevision, TimeSpan Hold, string? Reason, T? Page)
    {
        public static RecordOutcome<T> Recorded(long journalRevision, TimeSpan hold, T? page) => new(journalRevision, hold, null, page);

        public static RecordOutcome<T> Failed(string reason) => new(null, TimeSpan.Zero, reason, default);
    }
}

/// <summary>
/// The internal audit-access mode, recorded as <c>access_kind</c>. Not a wire type: the explorer route maps its two wire kinds onto
/// the first two members, and only the timeline entry point uses <see cref="CommandTimelinePage"/>.
/// </summary>
public enum AuditAccessMode
{
    ExplorerPage,

    EventDetail,

    CommandTimelinePage,
}

/// <summary>The timeline's audit-access request (no access kind: the mode is fixed by <see cref="AuditAccessService.AuthorizeAndReadTimelineAsync{T}"/>).</summary>
public sealed record AuditTimelineAccess(Guid OrganizationId, string FilterDigest, int PageSize, Guid CorrelationId);

/// <summary>The permission check the audit-access protocol runs (implemented by <see cref="PermissionCheckService"/>).</summary>
public interface IOrganizationPermissionCheck
{
    Task<PermissionDecision> CheckAsync(VerifiedActor actor, PermissionCheckRequest request, Guid correlationId, CancellationToken cancellationToken);
}

/// <summary>Per-user and per-organization token buckets for audit access (contract §5.3; per Authority instance, RR-10).</summary>
public interface IAuditAccessRateLimiter
{
    /// <summary>True when both the actor's and the organization's bucket had a token.</summary>
    bool TryAcquire(Guid organizationId, VerifiedActor actor);
}

/// <summary>
/// TC-3 (approved by the owner, M4-2): bounded, validated audit-access settings, section <c>AuditAccess</c>. Out-of-range values
/// fail start-up. Defaults: 200 ms statement timeout, 500 ms transaction timeout, 20–80 ms jittered backoff, 3 attempts, 5
/// requests/s per user (burst 10) and 20 requests/s per organization (burst 20), per Authority instance.
/// </summary>
public sealed class AuditAccessOptions
{
    public const string SectionName = "AuditAccess";

    public int StatementTimeoutMilliseconds { get; set; } = 200;

    public int TransactionTimeoutMilliseconds { get; set; } = 500;

    public int RetryBackoffMinimumMilliseconds { get; set; } = 20;

    public int RetryBackoffMaximumMilliseconds { get; set; } = 80;

    public int MaximumAttempts { get; set; } = 3;

    public int UserRequestsPerSecond { get; set; } = 5;

    public int UserBurstLimit { get; set; } = 10;

    public int OrganizationRequestsPerSecond { get; set; } = 20;

    public int OrganizationBurstLimit { get; set; } = 20;

    public void Validate()
    {
        static void Require(bool condition, string rule)
        {
            if (!condition)
            {
                throw new InvalidOperationException(string.Create(CultureInfo.InvariantCulture, $"{SectionName}: {rule}"));
            }
        }

        Require(StatementTimeoutMilliseconds is >= 50 and <= 2000, "StatementTimeoutMilliseconds must be 50..2000");
        Require(TransactionTimeoutMilliseconds >= StatementTimeoutMilliseconds && TransactionTimeoutMilliseconds <= 5000, "TransactionTimeoutMilliseconds must be StatementTimeoutMilliseconds..5000");
        Require(RetryBackoffMinimumMilliseconds is >= 1 and <= 1000, "RetryBackoffMinimumMilliseconds must be 1..1000");
        Require(RetryBackoffMaximumMilliseconds >= RetryBackoffMinimumMilliseconds && RetryBackoffMaximumMilliseconds <= 1000, "RetryBackoffMaximumMilliseconds must be RetryBackoffMinimumMilliseconds..1000");
        Require(MaximumAttempts is >= 1 and <= 5, "MaximumAttempts must be 1..5");
        Require(UserRequestsPerSecond is >= 1 and <= 50, "UserRequestsPerSecond must be 1..50");
        Require(UserBurstLimit >= UserRequestsPerSecond && UserBurstLimit <= 100, "UserBurstLimit must be UserRequestsPerSecond..100");
        Require(OrganizationRequestsPerSecond is >= 1 and <= 200, "OrganizationRequestsPerSecond must be 1..200");
        Require(OrganizationBurstLimit >= OrganizationRequestsPerSecond && OrganizationBurstLimit <= 400, "OrganizationBurstLimit must be OrganizationRequestsPerSecond..400");
        Require(OrganizationRequestsPerSecond >= UserRequestsPerSecond, "OrganizationRequestsPerSecond must be at least UserRequestsPerSecond");
    }
}
