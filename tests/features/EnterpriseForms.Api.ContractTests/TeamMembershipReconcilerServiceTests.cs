using System.Reflection;
using EnterpriseForms.Application.Common.Authority;
using EnterpriseForms.Application.Common.Persistence;
using EnterpriseForms.Application.Features.Teams.MembershipReconciliation;
using EnterpriseForms.Domain.Features.Audit;
using EnterpriseForms.Domain.Features.Teams;
using Xunit;

namespace EnterpriseForms.Api.ContractTests;

/// <summary>
/// Phase C8, the reconciler service end to end against a transactional in-memory store and a scripted AC-9 port (no stack):
/// projection rule §4.2 with R-6 (RC-10, RC-11 benign stale answer, RC-23), drift (Q-5), forged answers through the whole
/// service (RC-22), quarantine (RC-24), credential refusal (RC-25), rate limiting (RC-27), NotCommitted and the late-commit
/// sweep (RC-07), rejection and FailedReconciliation (RC-08, RC-09). Every scope is a transaction: nothing staged in a scope
/// that is disposed without commit becomes visible (the D8-21 all-or-nothing property at this level).
/// </summary>
public sealed class TeamMembershipReconcilerServiceTests
{
    private const string Worker = "test-worker";
    private static readonly string[] AllowedAuditOutcomes = ["Success", "Denied", "Failed"];
    private static readonly Guid Tenant = Guid.CreateVersion7();
    private static readonly Guid Team = Guid.CreateVersion7();
    private static readonly Guid Member = Guid.CreateVersion7();
    private static readonly Guid Initiator = Guid.CreateVersion7();

    private readonly FakeStore store = new();
    private readonly ScriptedAuthority authority = new();
    private readonly MutableClock clock = new(new DateTimeOffset(2026, 10, 30, 12, 0, 0, TimeSpan.Zero));
    private readonly TeamMembershipReconciliationOptions options = new() { InFlightWindowSeconds = 60, BackoffSeconds = 5, BackoffCapSeconds = 300 };
    private readonly TeamMembershipReconciliationService service;

    public TeamMembershipReconcilerServiceTests()
    {
        store.Clock = clock;
        service = new TeamMembershipReconciliationService(store, authority, options, clock);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private AuthorityCommandOperation Due(string kind = TeamMembershipCommandKinds.AddTeamMember, Guid? command = null, long? revision = null, TimeSpan? age = null)
    {
        var operation = new AuthorityCommandOperation
        {
            AuthorityCommandOperationId = Guid.CreateVersion7(),
            TenantId = Tenant,
            InitiatedByUserId = Initiator,
            CommandKindName = kind,
            TeamId = Team,
            MemberUserId = Member,
            AuthorityIdempotencyKey = TeamMembershipCommandKinds.KeyPrefix(kind) + Guid.NewGuid().ToString("D"),
            RequestCorrelationIdentifier = Guid.CreateVersion7(),
            OperationCreatedOn = clock.GetUtcNow() - (age ?? TimeSpan.Zero),
            OperationStateName = command is null ? AuthorityCommandOperationStates.AwaitingCommit : AuthorityCommandOperationStates.AwaitingEffective,
            AuthorityCommandId = command,
            CommittedAuthorizationRevision = revision,
            NextAttemptOn = clock.GetUtcNow(),
        };
        store.Operations.Add(operation);
        return operation;
    }

    private static Func<Guid, SecurityCommandOutcomeAnswer> Answer(AuthorityCommandOperation operation, Guid command, string state, long? committed, long applied, long authorization, bool? member, long e, bool quarantined = false) =>
        correlation => new SecurityCommandOutcomeAnswer(true, correlation, Tenant, command, operation.CommandKindName, state, committed, state == "Rejected" ? "FORBIDDEN" : null, Team, Member,
            new SecurityCommandOutcomeOrganizationAnswer(authorization, applied, quarantined ? "Quarantined" : "Ready", quarantined),
            member is null ? null : new SecurityCommandOutcomeMembershipAnswer(applied, member.Value, e, authorization > applied));

    private AuthorityCommandOperation Stored(AuthorityCommandOperation operation) => store.Operations.Single(o => o.AuthorityCommandOperationId == operation.AuthorityCommandOperationId);

    private TeamMembershipProjectionState? Watermark() => store.Watermarks.SingleOrDefault(w => w.TeamId == Team && w.MemberUserId == Member);

    private bool RosterPresent() => store.Roster.Any(r => r.TeamId == Team && r.UserId == Member && r.RemovedOn is null);

    private Task<ReconciliationStep> Step() => service.ProcessOnceAsync(Worker, Ct);

    [Fact]
    public async Task AnEffectiveAddProjectsTheAuthorityStateWithTheWatermarkAndCompletionTogether()
    {
        var command = Guid.CreateVersion7();
        var add = Due(command: command, revision: 4);
        authority.Script.Enqueue(Answer(add, command, "Effective", 4, 4, 4, true, 4));

        var step = await Step();

        Assert.Equal(ReconciliationStepKind.Projected, step.Kind);
        Assert.True(RosterPresent());
        Assert.Equal(command, store.Roster.Single().AuthorityCommandId);
        Assert.Equal((true, 4L, add.AuthorityCommandOperationId), (Watermark()!.ProjectedMembershipPresent, Watermark()!.ProjectedMembershipRevision, Watermark()!.LastAuthorityCommandOperationId!.Value));
        var stored = Stored(add);
        Assert.Equal((AuthorityCommandOperationStates.Projected, AuthorityCommandOperationOutcomes.MembershipPresentProjected, 4L, 4L, true, false), (stored.OperationStateName, stored.OperationOutcomeCode, stored.EvaluatedProjectionRevision!.Value, stored.ProjectedMembershipRevision!.Value, stored.ProjectedMembershipPresent!.Value, stored.SupersededByLaterRevision!.Value));
        Assert.Null(stored.LeaseOwnerName);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RC10AddAtR1AndRemoveAtR2ConvergeOnAbsenceInEitherProcessingOrder(bool removeFirst)
    {
        var addCommand = Guid.CreateVersion7();
        var removeCommand = Guid.CreateVersion7();
        var add = Due(command: addCommand, revision: 1);
        var remove = Due(TeamMembershipCommandKinds.RemoveTeamMember, removeCommand, 2);
        (removeFirst ? add : remove).NextAttemptOn = clock.GetUtcNow().AddMinutes(1);
        // Both are Effective: the Authority reports the pair absent at P = 2 with E = r2 for either command.
        authority.Script.Enqueue(removeFirst ? Answer(remove, removeCommand, "Effective", 2, 2, 2, false, 2) : Answer(add, addCommand, "Effective", 1, 2, 2, false, 2));
        Assert.Equal(ReconciliationStepKind.Projected, (await Step()).Kind);
        (removeFirst ? add : remove).NextAttemptOn = clock.GetUtcNow();
        authority.Script.Enqueue(removeFirst ? Answer(add, addCommand, "Effective", 1, 2, 2, false, 2) : Answer(remove, removeCommand, "Effective", 2, 2, 2, false, 2));
        Assert.Equal(ReconciliationStepKind.Projected, (await Step()).Kind);

        Assert.False(RosterPresent());
        Assert.Equal(2, Watermark()!.ProjectedMembershipRevision);
        Assert.True(Stored(add).SupersededByLaterRevision);
        Assert.False(Stored(remove).SupersededByLaterRevision);
        Assert.Equal(AuthorityCommandOperationOutcomes.ProjectionUnchanged, Stored(removeFirst ? add : remove).OperationOutcomeCode);
    }

    [Fact]
    public async Task RC11AStaleAnswerBehindTheWatermarkIsDiscardedAndReLookedUpOnceWithoutAnAlert()
    {
        // Another writer already projected the removal at r2 (W = 2, absent) after this worker's lookup returned r1.
        store.Watermarks.Add(new TeamMembershipProjectionState { TeamMembershipProjectionStateId = Guid.CreateVersion7(), TenantId = Tenant, TeamId = Team, MemberUserId = Member, ProjectedMembershipPresent = false, ProjectedMembershipRevision = 2 });
        var command = Guid.CreateVersion7();
        var add = Due(command: command, revision: 1);
        authority.Script.Enqueue(Answer(add, command, "Effective", 1, 1, 2, true, 1)); // stale: P = 1
        authority.Script.Enqueue(Answer(add, command, "Effective", 1, 2, 2, false, 2)); // fresh: P = 2

        var step = await Step();

        Assert.Equal(ReconciliationStepKind.Projected, step.Kind);
        Assert.Equal(AuthorityCommandOperationOutcomes.ProjectionUnchanged, step.Code);
        Assert.Equal(2, authority.Calls);
        Assert.False(RosterPresent()); // the stale "present" was never written
        Assert.Equal(2, Watermark()!.ProjectedMembershipRevision);
        Assert.DoesNotContain(store.Audit, a => a.EventAction == AuditActions.TeamMembershipReconciliationFailed);
    }

    [Fact]
    public async Task RC23AWatermarkAheadOfTheAuthorityIsAnAnomalyAfterTheFreshLookupAndNothingIsProjected()
    {
        store.Watermarks.Add(new TeamMembershipProjectionState { TeamMembershipProjectionStateId = Guid.CreateVersion7(), TenantId = Tenant, TeamId = Team, MemberUserId = Member, ProjectedMembershipPresent = false, ProjectedMembershipRevision = 999 });
        var command = Guid.CreateVersion7();
        var add = Due(command: command, revision: 3);
        authority.Script.Enqueue(Answer(add, command, "Effective", 3, 3, 3, true, 3));
        authority.Script.Enqueue(Answer(add, command, "Effective", 3, 3, 3, true, 3));

        var step = await Step();

        Assert.Equal((ReconciliationStepKind.Anomaly, TeamMembershipReconciliationErrors.LocalWatermarkAhead), (step.Kind, step.Code));
        Assert.Equal(2, authority.Calls);
        Assert.False(RosterPresent());
        Assert.Equal(999, Watermark()!.ProjectedMembershipRevision);
        Assert.Equal(AuthorityCommandOperationStates.ReconciliationError, Stored(add).OperationStateName);
        Assert.NotNull(Stored(add).NeedsAttentionFlaggedOn);
        Assert.Contains(store.Audit, a => a.EventAction == AuditActions.TeamMembershipReconciliationFailed && a.ActorType == "Service" && a.ActorUserId is null && a.EventMetadataJson.Contains(TeamMembershipReconciliationErrors.LocalWatermarkAhead, StringComparison.Ordinal));
        // ck_audit_events_outcome allows only Success, Denied and Failed (found on the live stack: the fake store has no constraint).
        Assert.All(store.Audit, a => Assert.Contains(a.EventOutcome, AllowedAuditOutcomes));
    }

    [Fact]
    public async Task Q5DriftAtTheSameRevisionIsCorrectedToTheAuthorityStateAndAudited()
    {
        // W says present at r3, but the roster row was removed outside the reconciler.
        store.Watermarks.Add(new TeamMembershipProjectionState { TeamMembershipProjectionStateId = Guid.CreateVersion7(), TenantId = Tenant, TeamId = Team, MemberUserId = Member, ProjectedMembershipPresent = true, ProjectedMembershipRevision = 3 });
        var command = Guid.CreateVersion7();
        var add = Due(command: command, revision: 3);
        authority.Script.Enqueue(Answer(add, command, "Effective", 3, 3, 3, true, 3));

        var step = await Step();

        Assert.Equal(AuthorityCommandOperationOutcomes.DriftCorrected, step.Code);
        Assert.True(RosterPresent());
        Assert.Contains(store.Audit, a => a.EventAction == AuditActions.TeamMembershipProjectionDrift);
    }

    public static TheoryData<string> ForgedVariants() => ["member", "team", "organization", "correlation", "evaluated", "effective-above-applied", "membership-while-quarantined"];

    [Theory]
    [MemberData(nameof(ForgedVariants))]
    public async Task RC22AForgedAnswerThroughTheWholeServiceProjectsNothing(string variant)
    {
        var command = Guid.CreateVersion7();
        var add = Due(command: command, revision: 3);
        var genuine = Answer(add, command, "Effective", 3, 3, 3, true, 3);
        authority.Script.Enqueue(correlation =>
        {
            var answer = genuine(correlation);
            return variant switch
            {
                "member" => answer with { MemberUserId = Guid.CreateVersion7() },
                "team" => answer with { TeamId = Guid.CreateVersion7() },
                "organization" => answer with { OrganizationId = Guid.CreateVersion7() },
                "correlation" => answer with { CorrelationId = Guid.CreateVersion7() },
                "evaluated" => answer with { Membership = answer.Membership! with { EvaluatedAtRevision = 2 } },
                "effective-above-applied" => answer with { OrganizationState = answer.OrganizationState! with { ProjectionAppliedRevision = 2 }, Membership = answer.Membership! with { EvaluatedAtRevision = 2, EffectiveMembershipRevision = 2 } },
                _ => answer with { OrganizationState = answer.OrganizationState! with { IntegrityQuarantined = true } },
            };
        });

        var step = await Step();

        Assert.Equal(ReconciliationStepKind.Anomaly, step.Kind);
        Assert.Contains(step.Code, new[] { TeamMembershipReconciliationErrors.OperationMismatch, TeamMembershipReconciliationErrors.InvalidAuthorityResponse });
        Assert.False(RosterPresent());
        Assert.Null(Watermark());
        Assert.Equal(AuthorityCommandOperationStates.ReconciliationError, Stored(add).OperationStateName);
    }

    [Fact]
    public async Task RC24QuarantineProjectsNothingAndResumesAfterTheAuthoritysClearance()
    {
        var command = Guid.CreateVersion7();
        var add = Due(command: command, revision: 3);
        authority.Script.Enqueue(Answer(add, command, "Effective", 3, 3, 3, null, 0, quarantined: true));

        Assert.Equal(AuthorityCommandOperationStates.AwaitingQuarantineClearance, (await Step()).Code);
        Assert.False(RosterPresent());
        Assert.Null(Watermark());
        Assert.Equal(AuthorityCommandOperationStates.AwaitingQuarantineClearance, Stored(add).OperationStateName);
        Assert.Equal(clock.GetUtcNow().AddSeconds(options.BackoffCapSeconds), Stored(add).NextAttemptOn);

        clock.Advance(TimeSpan.FromSeconds(options.BackoffCapSeconds));
        authority.Script.Enqueue(Answer(add, command, "Effective", 3, 3, 3, true, 3));
        Assert.Equal(ReconciliationStepKind.Projected, (await Step()).Kind);
        Assert.True(RosterPresent());
    }

    [Fact]
    public async Task RC25ARefusedCredentialLeavesTheOperationUntouchedAndMarksTheReconcilerUnavailable()
    {
        var add = Due(command: Guid.CreateVersion7(), revision: 3);
        authority.Failures.Enqueue(new ReconcilerLookupException(ReconcilerLookupFailure.CredentialRefused, "UNAUTHORIZED"));

        var step = await Step();

        Assert.Equal(ReconciliationStepKind.CredentialRefused, step.Kind);
        Assert.True(service.CredentialUnavailable);
        var stored = Stored(add);
        Assert.Equal((AuthorityCommandOperationStates.AwaitingEffective, 0, 0, (string?)null), (stored.OperationStateName, stored.AttemptCount, stored.ConsecutiveErrorCount, stored.LastReconciliationErrorCode));
        Assert.False(RosterPresent());
        Assert.Empty(store.Audit);
    }

    [Fact]
    public async Task RC27ARateLimitHonoursRetryAfterAndConsumesNoErrorBudget()
    {
        var add = Due(command: Guid.CreateVersion7(), revision: 3);
        authority.Failures.Enqueue(new ReconcilerLookupException(ReconcilerLookupFailure.RateLimited, "RATE_LIMITED", TimeSpan.FromSeconds(7)));

        Assert.Equal(ReconciliationStepKind.RateLimited, (await Step()).Kind);
        var stored = Stored(add);
        Assert.Equal(clock.GetUtcNow().AddSeconds(7), stored.NextAttemptOn);
        Assert.Equal((0, 0), (stored.AttemptCount, stored.ConsecutiveErrorCount));
    }

    [Fact]
    public async Task AnUnreachableAuthorityBacksOffCountsErrorsAndFlagsAfterTheBudget()
    {
        var add = Due(command: Guid.CreateVersion7(), revision: 3);
        for (var i = 1; i <= options.ConsecutiveErrorBudget; i++)
        {
            Stored(add).NextAttemptOn = clock.GetUtcNow();
            authority.Failures.Enqueue(new ReconcilerLookupException(ReconcilerLookupFailure.Unavailable, "AUTHORITY_UNAVAILABLE"));
            Assert.Equal(ReconciliationStepKind.TransientError, (await Step()).Kind);
            Assert.Equal(i, Stored(add).ConsecutiveErrorCount);
            Assert.Equal(clock.GetUtcNow().Add(options.Backoff(i)), Stored(add).NextAttemptOn);
        }

        Assert.NotNull(Stored(add).NeedsAttentionFlaggedOn);
        Assert.Equal(AuthorityCommandOperationStates.AwaitingEffective, Stored(add).OperationStateName); // stays open; completes on the Authority's return
        Assert.False(RosterPresent());
    }

    [Fact]
    public async Task RC07NotCommittedNeedsTheWindowAndTwoNotFoundAnswersAWindowApartThenTheSweepDetectsALateCommit()
    {
        var operation = Due();
        Func<Guid, SecurityCommandOutcomeAnswer> notFound = correlation => new SecurityCommandOutcomeAnswer(false, correlation, Tenant, null, null, null, null, null, null, null, null, null);

        authority.Script.Enqueue(notFound);
        Assert.Equal("IN_FLIGHT", (await Step()).Code);
        Assert.Equal(operation.OperationCreatedOn.AddSeconds(60), Stored(operation).NextAttemptOn);

        clock.Advance(TimeSpan.FromSeconds(60));
        authority.Script.Enqueue(notFound);
        Assert.Equal("FIRST_NOT_FOUND", (await Step()).Code);

        clock.Advance(TimeSpan.FromSeconds(30));
        Stored(operation).NextAttemptOn = clock.GetUtcNow();
        authority.Script.Enqueue(notFound);
        Assert.Equal("AWAITING_SECOND_NOT_FOUND", (await Step()).Code);

        clock.Advance(TimeSpan.FromSeconds(30));
        authority.Script.Enqueue(notFound);
        Assert.Equal(AuthorityCommandOperationStates.NotCommitted, (await Step()).Code);
        Assert.Equal(AuthorityCommandOperationStates.NotCommitted, Stored(operation).OperationStateName);
        Assert.Contains(store.Audit, a => a.EventAction == AuditActions.TeamMembershipReconciliationFailed && a.EventMetadataJson.Contains("NotCommitted", StringComparison.Ordinal));

        // The daily sweep: a late commit is recorded and audited; the terminal state never re-opens (I-9) and nothing is projected.
        clock.Advance(TimeSpan.FromSeconds(options.LateCommitSweepIntervalSeconds));
        var late = Guid.CreateVersion7();
        authority.Script.Enqueue(Answer(operation, late, "Effective", 9, 9, 9, true, 9));
        Assert.Equal(TeamMembershipReconciliationErrors.LateCommitDetected, (await Step()).Code);
        Assert.Equal(AuthorityCommandOperationStates.NotCommitted, Stored(operation).OperationStateName);
        Assert.NotNull(Stored(operation).LateCommitDetectedOn);
        Assert.False(RosterPresent());
    }

    [Fact]
    public async Task RC08RC09RejectionAndFailedReconciliationAreTerminalAndProjectNothing()
    {
        var rejected = Due();
        authority.Script.Enqueue(Answer(rejected, Guid.CreateVersion7(), "Rejected", null, 5, 5, null, 0));
        Assert.Equal(AuthorityCommandOperationStates.Rejected, (await Step()).Code);
        Assert.Equal(("Rejected", "FORBIDDEN"), (Stored(rejected).OperationStateName, Stored(rejected).RejectionReasonCode));

        var failed = Due(command: Guid.CreateVersion7(), revision: 6);
        authority.Script.Enqueue(Answer(failed, failed.AuthorityCommandId!.Value, "FailedReconciliation", 6, 5, 6, null, 0, quarantined: true));
        Assert.Equal(AuthorityCommandOperationStates.AuthorityFailed, (await Step()).Code);
        Assert.Equal(AuthorityCommandOperationStates.AuthorityFailed, Stored(failed).OperationStateName);
        Assert.False(RosterPresent());
        Assert.Null(Watermark());
    }

    [Fact]
    public async Task D820AStoredLocalStateIsNeverEvidenceOnlyTheCurrentAnswerIs()
    {
        // The row claims a projection already happened; the Authority says the command still awaits projection.
        var command = Guid.CreateVersion7();
        var add = Due(command: command, revision: 3);
        add.ProjectedMembershipPresent = true;
        add.ProjectedMembershipRevision = 3;
        authority.Script.Enqueue(Answer(add, command, "CommittedAwaitingProjection", 3, 2, 3, false, 0));

        Assert.Equal(ReconciliationStepKind.Waiting, (await Step()).Kind);
        Assert.False(RosterPresent());
        Assert.Null(Watermark());
        Assert.Equal(clock.GetUtcNow().AddSeconds(1), Stored(add).NextAttemptOn);
    }

    [Fact]
    public async Task AMismatchedKeyPrefixIsRefusedBeforeAnyLookup()
    {
        var operation = Due();
        operation.AuthorityIdempotencyKey = "team-remove:" + Guid.NewGuid();
        Assert.Equal(TeamMembershipReconciliationErrors.OperationMismatch, (await Step()).Code);
        Assert.Equal(0, authority.Calls);
    }

    private sealed class MutableClock(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset now = start;

        public override DateTimeOffset GetUtcNow() => now;

        public void Advance(TimeSpan by) => now = now.Add(by);
    }

    private sealed class ScriptedAuthority : ITeamMembershipOutcomeAuthority
    {
        public Queue<Func<Guid, SecurityCommandOutcomeAnswer>> Script { get; } = new();

        public Queue<ReconcilerLookupException> Failures { get; } = new();

        public int Calls { get; private set; }

        public bool IsConfigured => true;

        public Task<SecurityCommandOutcomeAnswer> LookupAsync(Guid organizationId, string idempotencyKey, string expectedCommandKind, Guid correlationId, CancellationToken cancellationToken)
        {
            Calls++;
            if (Failures.TryDequeue(out var failure))
            {
                throw failure;
            }

            return Task.FromResult(Script.Dequeue()(correlationId));
        }
    }

    /// <summary>Transactional in-memory store: a scope works on copies and publishes them only on commit.</summary>
    private sealed class FakeStore : IBusinessStore
    {
        public List<AuthorityCommandOperation> Operations { get; } = [];

        public List<TeamMembershipProjectionState> Watermarks { get; } = [];

        public List<TeamMembership> Roster { get; } = [];

        public List<AuditEvent> Audit { get; } = [];

        public TimeProvider Clock { get; set; } = TimeProvider.System;

        public Task<IBusinessScope> BeginScopeAsync(CancellationToken cancellationToken)
        {
            var scope = DispatchProxy.Create<IBusinessScope, ScopeProxy>();
            ((ScopeProxy)(object)scope).Store = this;
            return Task.FromResult(scope);
        }
    }

    public class ScopeProxy : DispatchProxy
    {
        private readonly Dictionary<Guid, AuthorityCommandOperation> operations = [];
        private readonly Dictionary<Guid, TeamMembershipProjectionState> watermarks = [];
        private readonly Dictionary<Guid, TeamMembership> roster = [];
        private readonly List<AuditEvent> audit = [];

        internal object Store { get; set; } = null!;

        private FakeStore S => (FakeStore)Store;

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            ArgumentNullException.ThrowIfNull(targetMethod);
            args ??= [];
            switch (targetMethod.Name)
            {
                case nameof(IBusinessScope.LeaseNextAuthorityCommandOperationAsync):
                {
                    var now = DateTimeOffset.MaxValue;
                    var due = S.Operations.Where(o => (AuthorityCommandOperationStates.Due.Contains(o.OperationStateName) || (o.OperationStateName == AuthorityCommandOperationStates.NotCommitted && o.LateCommitDetectedOn is null && o.LateCommitSweepUntil is not null)) && o.LeaseOwnerName is null)
                        .OrderBy(o => o.NextAttemptOn).FirstOrDefault();
                    var leased = due is null ? null : Track(due);
                    if (leased is not null)
                    {
                        if (leased.NextAttemptOn > Clock().GetUtcNow())
                        {
                            operations.Remove(leased.AuthorityCommandOperationId);
                            return Task.FromResult<AuthorityCommandOperation?>(null);
                        }

                        leased.LeaseOwnerName = (string)args[0]!;
                        leased.LeaseExpiresOn = now;
                    }

                    return Task.FromResult(leased);
                }

                case nameof(IBusinessScope.FindAuthorityCommandOperationAsync):
                {
                    var found = S.Operations.FirstOrDefault(o => o.AuthorityCommandOperationId == (Guid)args[0]!);
                    return Task.FromResult(found is null ? null : Track(found));
                }

                case nameof(IBusinessScope.LockTeamMembershipProjectionStateAsync):
                {
                    var (tenant, team, member) = ((Guid)args[0]!, (Guid)args[1]!, (Guid)args[2]!);
                    var existing = S.Watermarks.FirstOrDefault(w => w.TenantId == tenant && w.TeamId == team && w.MemberUserId == member)
                                   ?? new TeamMembershipProjectionState { TeamMembershipProjectionStateId = Guid.CreateVersion7(), TenantId = tenant, TeamId = team, MemberUserId = member };
                    var copy = Copy(existing);
                    watermarks[copy.TeamMembershipProjectionStateId] = copy;
                    return Task.FromResult(copy);
                }

                case nameof(IBusinessScope.FindActiveTeamMembershipProjectionAsync):
                {
                    var (tenant, team, member) = ((Guid)args[0]!, (Guid)args[1]!, (Guid)args[2]!);
                    var row = S.Roster.FirstOrDefault(r => r.TenantId == tenant && r.TeamId == team && r.UserId == member && r.RemovedOn is null);
                    if (row is null)
                    {
                        return Task.FromResult<TeamMembership?>(null);
                    }

                    var copy = Copy(row);
                    roster[copy.TeamMembershipId] = copy;
                    return Task.FromResult<TeamMembership?>(copy);
                }

                case nameof(IBusinessScope.AddTeamMembershipProjection):
                    roster[((TeamMembership)args[0]!).TeamMembershipId] = (TeamMembership)args[0]!;
                    return null;
                case nameof(IBusinessScope.AddAuditEvent):
                    audit.Add((AuditEvent)args[0]!);
                    return null;
                case nameof(IBusinessScope.CommitAsync):
                    Publish();
                    return Task.CompletedTask;
                case nameof(IAsyncDisposable.DisposeAsync):
                    return ValueTask.CompletedTask;
                default:
                    throw new NotSupportedException("FakeStore does not implement " + targetMethod.Name);
            }
        }

        private TimeProvider Clock() => S.Clock;

        private AuthorityCommandOperation Track(AuthorityCommandOperation source)
        {
            if (!operations.TryGetValue(source.AuthorityCommandOperationId, out var copy))
            {
                copy = Copy(source);
                operations[copy.AuthorityCommandOperationId] = copy;
            }

            return copy;
        }

        private void Publish()
        {
            Replace(S.Operations, operations.Values, o => o.AuthorityCommandOperationId);
            Replace(S.Watermarks, watermarks.Values, w => w.TeamMembershipProjectionStateId);
            Replace(S.Roster, roster.Values, r => r.TeamMembershipId);
            S.Audit.AddRange(audit);
        }

        private static void Replace<T>(List<T> target, IEnumerable<T> changed, Func<T, Guid> key)
        {
            foreach (var item in changed)
            {
                var index = target.FindIndex(t => key(t) == key(item));
                if (index >= 0)
                {
                    target[index] = item;
                }
                else
                {
                    target.Add(item);
                }
            }
        }

        private static T Copy<T>(T source)
            where T : class => (T)typeof(T).GetMethod("MemberwiseClone", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(source, null)!;
    }
}
