using System.Text.Json;
using AuthorizationAuthority.Api.Features.Audit;
using AuthorizationAuthority.Application.Common;
using AuthorizationAuthority.Application.Features.Audit;
using AuthorizationAuthority.Contracts.V1;
using AuthorizationAuthority.Domain.Features.Journal;
using AuthorizationAuthority.Domain.Features.Organizations;
using Xunit;

namespace AuthorizationAuthority.Tests;

/// <summary>
/// C8 M4-2 (AC-8.3, contract §5.3) through the real service over the in-memory store: an allow exists only with a committed
/// AuditAccessAuthorized entry (T10, T11), the entry states authorization only and carries no business counts (AC-M4-2.2/2.3),
/// the decision is recorded only under the revision it was taken at (T20), a busy organization lock is never waited on and is
/// bounded (T21), denials are immediate, and the TC-3 limits are validated and enforced (T23). The view_audit decision itself
/// (SpiceDB, gate) is the M4-1 permission check; here it is a stand-in reading the store, so these tests cover the protocol.
/// PostgreSQL NOWAIT and timeouts, the real lock contention (T21/T22 under load) and the business path run in Stage B
/// (SecurityAuditAccessTests, AuditExplorerTests), which is outstanding.
/// </summary>
public sealed class AuditAccessServiceTests : IDisposable
{
    private static readonly Guid Org = Guid.CreateVersion7();
    private static readonly Guid Auditor = Guid.CreateVersion7();
    private static readonly Guid Member = Guid.CreateVersion7();
    private const string Issuer = "https://idp.test.invalid";
    private static readonly string Digest = new('a', 64);

    private readonly InMemoryAuthorityStore store = new();
    private readonly HashSet<Guid> auditors = [Auditor];
    private readonly AuditAccessTelemetry telemetry = new();

    public AuditAccessServiceTests()
    {
        store.Organizations.Add(new OrganizationAuthorizationState { OrganizationId = Org, OrganizationDisplayName = "Audit Org", AuthorizationRevision = 7, ProjectionAppliedRevision = 7 });
        foreach (var user in new[] { Auditor, Member })
        {
            store.Bindings.Add(new IdentityBinding { IdentityBindingId = Guid.CreateVersion7(), OrganizationId = Org, AuthorityUserId = user, IdentityIssuerUri = Issuer, IdentitySubjectIdentifier = Subject(user), IdentityBindingEnabled = true });
        }
    }

    public void Dispose() => telemetry.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Subject(Guid user) => "subject-" + user.ToString("N");

    private static VerifiedActor Actor(Guid user) => new(Issuer, Subject(user), "enterpriseforms-api");

    private static AuditAccessRequest Page(int pageSize = 25) => new(Org, AuditAccessKind.ExplorerPage, Digest, pageSize, null, Guid.CreateVersion7());

    private static AuditAccessOptions FastOptions() => new() { RetryBackoffMinimumMilliseconds = 1, RetryBackoffMaximumMilliseconds = 2 };

    private AuditAccessService Service(AuditAccessOptions? options = null, IAuditAccessRateLimiter? limiter = null, StoreCheck? check = null) =>
        new(store, check ?? new StoreCheck(store, auditors), limiter ?? new Unlimited(), options ?? FastOptions(), telemetry, TimeProvider.System);

    private List<SecurityJournalEntry> Entries(string kind) => store.Journal.Where(j => j.JournalStreamName == JournalStreamNames.Audit && j.JournalEventKind == kind).ToList();

    /// <summary>The event body inside the journal envelope.</summary>
    private static JsonElement Body(JsonDocument document) => document.RootElement.GetProperty("body");

    private static async Task<AuthorityException> RefusedAsync(Func<Task> action) => await Assert.ThrowsAsync<AuthorityException>(action);

    [Fact]
    public async Task AnAuditorIsAuthorizedOnlyWithACommittedEntryThatStatesAuthorizationNotDelivery()
    {
        var request = Page(30);
        var commitsBefore = store.ScopeCommitCount;
        var decision = await Service().AuthorizeAsync(Actor(Auditor), request, Ct);

        var entry = Assert.Single(Entries(AuditAccessService.AuthorizedEventKind));
        Assert.True(decision.Allowed);
        Assert.Equal(entry.JournalStreamRevision, decision.AuditJournalRevision);
        Assert.Equal(7, decision.AuthorizationRevision);
        Assert.Equal("not_attested", decision.Delivery);
        Assert.Equal(request.CorrelationId, decision.CorrelationId);
        Assert.Equal(commitsBefore + 1, store.ScopeCommitCount);

        using var body = JsonDocument.Parse(entry.JournalEventBodyJson);
        var root = Body(body);
        Assert.Equal("ExplorerPage", root.GetProperty("access_kind").GetString());
        Assert.Equal(Auditor, root.GetProperty("actor_authority_user_id").GetGuid());
        Assert.Equal(7, root.GetProperty("authorization_revision").GetInt64());
        Assert.Equal(7, root.GetProperty("projection_revision").GetInt64());
        Assert.Equal(Digest, root.GetProperty("filter_digest").GetString());
        Assert.Equal(30, root.GetProperty("page_size").GetInt32());
        Assert.Equal(request.CorrelationId, root.GetProperty("correlation_id").GetGuid());
        Assert.Equal("not_attested", root.GetProperty("delivery").GetString());

        // AC-M4-2.3 (Q-2): no result count of any kind comes from the business or enters the entry.
        var names = root.EnumerateObject().Select(p => p.Name).ToList();
        Assert.DoesNotContain(names, n => n.Contains("count", StringComparison.OrdinalIgnoreCase) || n.Contains("result", StringComparison.OrdinalIgnoreCase) || n.Contains("returned", StringComparison.OrdinalIgnoreCase) || n.Contains("matched", StringComparison.OrdinalIgnoreCase));

        // Step 3: the transaction was bounded with the approved TC-3 defaults.
        Assert.Equal((200, 500), Assert.Single(store.AppliedAuditAccessTimeouts));
    }

    [Fact]
    public async Task AnEventDetailRecordsTheEventIdAndNeedsPageSizeOne()
    {
        var eventId = Guid.CreateVersion7();
        await Service().AuthorizeAsync(Actor(Auditor), new AuditAccessRequest(Org, AuditAccessKind.EventDetail, Digest, 1, eventId, Guid.CreateVersion7()), Ct);
        using var body = JsonDocument.Parse(Assert.Single(Entries(AuditAccessService.AuthorizedEventKind)).JournalEventBodyJson);
        Assert.Equal("EventDetail", Body(body).GetProperty("access_kind").GetString());
        Assert.Equal(eventId, Body(body).GetProperty("audit_event_id").GetGuid());
    }

    [Fact]
    public async Task ANonAuditorIsDeniedAtOnceWithABestEffortDenialEntryAndNoAuthorization()
    {
        var refused = await RefusedAsync(() => Service().AuthorizeAsync(Actor(Member), Page(), Ct));
        Assert.Equal(AuthorityErrorCodes.AuditViewForbidden, refused.ErrorCode);
        Assert.Equal(403, refused.HttpStatusCode);
        Assert.Empty(Entries(AuditAccessService.AuthorizedEventKind));
        using var denied = JsonDocument.Parse(Assert.Single(Entries(AuditAccessService.DeniedEventKind)).JournalEventBodyJson);
        Assert.Equal(Member, Body(denied).GetProperty("actor_authority_user_id").GetGuid());
    }

    [Fact]
    public async Task ADenialNeverWaitsForItsEntryWhenTheLockIsBusy()
    {
        store.NoWaitLockBusyCount = 1;
        var refused = await RefusedAsync(() => Service().AuthorizeAsync(Actor(Member), Page(), Ct));
        Assert.Equal(AuthorityErrorCodes.AuditViewForbidden, refused.ErrorCode);
        Assert.Empty(store.Journal);
        Assert.Equal(1, telemetry.DeniedUnrecordedCount);
    }

    /// <summary>T20: a revocation commits between the check and the lock; nothing is recorded under the old revision and the re-check denies.</summary>
    [Fact]
    public async Task ARevocationBetweenCheckAndLockIsNotRecordedUnderTheOldRevisionAndTheRecheckDenies()
    {
        var revoked = false;
        store.BeforeNoWaitLock = () =>
        {
            if (!revoked)
            {
                revoked = true;
                auditors.Remove(Auditor);
                store.Organizations.Single().AuthorizationRevision++;
                store.Organizations.Single().ProjectionAppliedRevision++;
            }
        };
        var check = new StoreCheck(store, auditors);

        var refused = await RefusedAsync(() => Service(check: check).AuthorizeAsync(Actor(Auditor), Page(), Ct));

        Assert.Equal(AuthorityErrorCodes.AuditViewForbidden, refused.ErrorCode);
        Assert.Equal(2, check.Calls);
        Assert.Empty(Entries(AuditAccessService.AuthorizedEventKind));
        Assert.DoesNotContain(store.Journal, j => j.JournalEventBodyJson.Contains("\"authorization_revision\":7", StringComparison.Ordinal) && j.JournalEventKind == AuditAccessService.AuthorizedEventKind);
    }

    /// <summary>T21: the lock stays held (a long security command); the audit read is refused after exactly the bounded attempts.</summary>
    [Fact]
    public async Task ALockHeldForEveryAttemptIsRefusedAsBusyAfterTheBoundedAttemptsWithoutWaiting()
    {
        store.NoWaitLockBusyCount = int.MaxValue;
        var refused = await RefusedAsync(() => Service().AuthorizeAsync(Actor(Auditor), Page(), Ct));
        Assert.Equal(AuthorityErrorCodes.AuditEvidenceBusy, refused.ErrorCode);
        Assert.Equal(503, refused.HttpStatusCode);
        Assert.True(refused.Retryable);
        Assert.Equal(3, store.NoWaitLockAttempts);
        Assert.Equal(3, telemetry.LockBusyCount);
        Assert.Empty(store.Journal);
    }

    [Fact]
    public async Task AShortlyBusyLockSucceedsOnALaterAttempt()
    {
        store.NoWaitLockBusyCount = 2;
        var decision = await Service().AuthorizeAsync(Actor(Auditor), Page(), Ct);
        Assert.True(decision.Allowed);
        Assert.Equal(3, store.NoWaitLockAttempts);
        Assert.Single(Entries(AuditAccessService.AuthorizedEventKind));
    }

    [Fact]
    public async Task TheConfiguredAttemptCountBoundsTheRetries()
    {
        store.NoWaitLockBusyCount = int.MaxValue;
        var options = FastOptions();
        options.MaximumAttempts = 1;
        await RefusedAsync(() => Service(options).AuthorizeAsync(Actor(Auditor), Page(), Ct));
        Assert.Equal(1, store.NoWaitLockAttempts);
    }

    /// <summary>T10: a failed (or unknown) commit is never an allow.</summary>
    [Fact]
    public async Task ACommitFailureIsNeverAnAllow()
    {
        store.FailNextCommit = new InvalidOperationException("simulated commit failure");
        var refused = await RefusedAsync(() => Service().AuthorizeAsync(Actor(Auditor), Page(), Ct));
        Assert.Equal(AuthorityErrorCodes.AuditEvidenceUnavailable, refused.ErrorCode);
        Assert.Equal(503, refused.HttpStatusCode);
        Assert.Equal(0, telemetry.AuthorizedCount);
    }

    [Fact]
    public async Task ADeadlineExpiryInsideTheTransactionIsNeverAnAllow()
    {
        store.BeforeNoWaitLock = () => throw new OperationCanceledException("deadline");
        var refused = await RefusedAsync(() => Service().AuthorizeAsync(Actor(Auditor), Page(), Ct));
        Assert.Equal(AuthorityErrorCodes.AuditEvidenceUnavailable, refused.ErrorCode);
        Assert.Empty(store.Journal);
    }

    [Fact]
    public async Task AStoreThatCannotBoundTheTransactionFailsClosed()
    {
        store.SupportsAuditAccess = false;
        var refused = await RefusedAsync(() => Service().AuthorizeAsync(Actor(Auditor), Page(), Ct));
        Assert.Equal(AuthorityErrorCodes.AuditEvidenceUnavailable, refused.ErrorCode);
        Assert.Empty(store.Journal);
    }

    [Fact]
    public async Task ABindingDisabledBetweenCheckAndLockIsRefusedAndNothingIsRecorded()
    {
        store.BeforeNoWaitLock = () => store.Bindings.Single(b => b.AuthorityUserId == Auditor).IdentityBindingEnabled = false;
        var refused = await RefusedAsync(() => Service().AuthorizeAsync(Actor(Auditor), Page(), Ct));
        Assert.Equal(AuthorityErrorCodes.ActorNotBound, refused.ErrorCode);
        Assert.Empty(Entries(AuditAccessService.AuthorizedEventKind));
    }

    [Fact]
    public async Task AQuarantineSetBeforeTheLockIsNeverRecordedAndTheRecheckFailsClosed()
    {
        store.BeforeNoWaitLock = () => store.Organizations.Single().IntegrityQuarantineEnabled = true;
        var refused = await RefusedAsync(() => Service().AuthorizeAsync(Actor(Auditor), Page(), Ct));
        Assert.Equal(AuthorityErrorCodes.IntegrityQuarantine, refused.ErrorCode);
        Assert.Empty(store.Journal);
    }

    [Theory]
    [InlineData("short", 25, false)]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA", 25, false)]
    [InlineData(null, 51, false)]
    [InlineData(null, 0, false)]
    [InlineData(null, 1, true)]
    public async Task MalformedRequestsAreRefusedBeforeAnyCheck(string? digest, int pageSize, bool withEventId)
    {
        var check = new StoreCheck(store, auditors);
        var request = new AuditAccessRequest(Org, AuditAccessKind.ExplorerPage, digest ?? Digest, pageSize, withEventId ? Guid.CreateVersion7() : null, Guid.CreateVersion7());
        var refused = await RefusedAsync(() => Service(check: check).AuthorizeAsync(Actor(Auditor), request, Ct));
        Assert.Equal(AuthorityErrorCodes.InvalidCommand, refused.ErrorCode);
        Assert.Equal(0, check.Calls);
        Assert.Empty(store.Journal);
    }

    [Fact]
    public async Task AnEventDetailWithoutAnEventIdIsRefused()
    {
        var refused = await RefusedAsync(() => Service().AuthorizeAsync(Actor(Auditor), new AuditAccessRequest(Org, AuditAccessKind.EventDetail, Digest, 1, null, Guid.CreateVersion7()), Ct));
        Assert.Equal(AuthorityErrorCodes.InvalidCommand, refused.ErrorCode);
    }

    /// <summary>T23: per-user token bucket (5/s, burst 10) and per-organization bucket (20/s, burst 20).</summary>
    [Fact]
    public async Task TheUserAndOrganizationRateLimitsRefuseWith429()
    {
        var options = new AuditAccessOptions();
        using var limiter = new AuditAccessRateLimiter(options);
        var userAllowed = Enumerable.Range(0, 11).Count(_ => limiter.TryAcquire(Org, Actor(Auditor)));
        Assert.Equal(10, userAllowed);

        var others = Enumerable.Range(0, 30).Select(_ => Actor(Guid.CreateVersion7())).ToList();
        var organizationAllowed = others.Count(a => limiter.TryAcquire(Org, a));
        Assert.Equal(10, organizationAllowed); // the organization bucket (20) already gave 10 tokens to the first user
        Assert.True(limiter.TryAcquire(Guid.CreateVersion7(), Actor(Auditor)), "another organization has its own buckets");

        var refused = await RefusedAsync(() => Service(limiter: new Exhausted()).AuthorizeAsync(Actor(Auditor), Page(), Ct));
        Assert.Equal(AuthorityErrorCodes.RateLimited, refused.ErrorCode);
        Assert.Equal(429, refused.HttpStatusCode);
        Assert.Empty(store.Journal);
    }

    [Fact]
    public void TheDefaultsAreTheApprovedTc3ValuesAndOutOfBoundsSettingsFailStartup()
    {
        var defaults = new AuditAccessOptions();
        defaults.Validate();
        Assert.Equal((200, 500, 20, 80, 3, 5, 10, 20), (defaults.StatementTimeoutMilliseconds, defaults.TransactionTimeoutMilliseconds, defaults.RetryBackoffMinimumMilliseconds, defaults.RetryBackoffMaximumMilliseconds,
            defaults.MaximumAttempts, defaults.UserRequestsPerSecond, defaults.UserBurstLimit, defaults.OrganizationRequestsPerSecond));

        var invalid = new Action<AuditAccessOptions>[]
        {
            o => o.StatementTimeoutMilliseconds = 10,
            o => o.TransactionTimeoutMilliseconds = 100,
            o => o.TransactionTimeoutMilliseconds = 60_000,
            o => o.RetryBackoffMinimumMilliseconds = 0,
            o => o.RetryBackoffMaximumMilliseconds = 10,
            o => o.MaximumAttempts = 0,
            o => o.MaximumAttempts = 9,
            o => o.UserRequestsPerSecond = 0,
            o => o.UserBurstLimit = 2,
            o => o.OrganizationRequestsPerSecond = 1000,
            o => o.OrganizationBurstLimit = 5,
        };
        foreach (var change in invalid)
        {
            var options = new AuditAccessOptions();
            change(options);
            Assert.Throws<InvalidOperationException>(options.Validate);
        }
    }

    /// <summary>Stand-in for the M4-1 view_audit check: binding and quarantine as ActorResolver and the gate, decision from the auditor set at the store's revision.</summary>
    private sealed class StoreCheck(InMemoryAuthorityStore store, HashSet<Guid> auditors) : IOrganizationPermissionCheck
    {
        public int Calls { get; private set; }

        public Task<PermissionDecision> CheckAsync(VerifiedActor actor, PermissionCheckRequest request, Guid correlationId, CancellationToken cancellationToken)
        {
            Calls++;
            Assert.Equal(RequestedAction.ViewAudit, request.RequestedAction);
            var state = store.Organizations.Single(o => o.OrganizationId == request.OrganizationId);
            if (state.IntegrityQuarantineEnabled)
            {
                throw AuthorityException.Quarantined("organization quarantined");
            }

            var binding = store.Bindings.SingleOrDefault(b => b.IdentityIssuerUri == actor.IssuerUri && b.IdentitySubjectIdentifier == actor.SubjectIdentifier);
            if (binding is not { IdentityBindingEnabled: true })
            {
                throw new AuthorityException(AuthorityErrorCodes.ActorNotBound, 403, false);
            }

            var allowed = auditors.Contains(binding.AuthorityUserId);
            return Task.FromResult(new PermissionDecision(allowed, allowed ? null : AuthorityErrorCodes.Forbidden, state.AuthorizationRevision, state.ProjectionAppliedRevision, null, correlationId));
        }
    }

    private sealed class Unlimited : IAuditAccessRateLimiter
    {
        public bool TryAcquire(Guid organizationId, VerifiedActor actor) => true;
    }

    private sealed class Exhausted : IAuditAccessRateLimiter
    {
        public bool TryAcquire(Guid organizationId, VerifiedActor actor) => false;
    }
}
