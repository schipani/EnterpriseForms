using AuthorizationAuthority.Application.Common;
using AuthorizationAuthority.Application.Common.Ports;
using AuthorizationAuthority.Application.Features.Projection;
using AuthorizationAuthority.Domain.Features.Journal;
using AuthorizationAuthority.Domain.Features.Organizations;
using AuthorizationAuthority.Domain.Features.SecurityCommands;
using AuthorizationAuthority.Infrastructure.Faults;
using Xunit;

namespace AuthorizationAuthority.Tests;

/// <summary>
/// C8 M4 S1 (Design B revision 2, C-117) unit tests U-01…U-10: the history-bound marker, the pin/decide/verify state machine,
/// lock-confirmed permanent quarantine, failure classes, the projector's refusal to write for a quarantined organization,
/// the startup marker transition and the periodic check. SpiceDB is a programmable fake; the real-datastore behaviour is
/// covered by the Stage B contract (CT) and rollback (RB) tests.
/// </summary>
public sealed class ProjectionIntegrityTests
{
    private sealed class FakeSpiceDb : ISpiceDbProjection
    {
        public Func<MarkerReadConsistency, MarkerObservation>? Markers { get; set; }

        public Func<SpiceDbCheck, SpiceDbCheckOutcome> Outcome { get; set; } = _ => SpiceDbCheckOutcome.Allowed;

        public Exception? CheckFailure { get; set; }

        public Exception? MarkerFailure { get; set; }

        public Action? DuringCheck { get; set; }

        public List<(string From, string To)> MarkerWrites { get; } = [];

        public int DecisionReads { get; private set; }

        public Task EnsureSchemaAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<string> InitializeCursorAsync(Guid organizationId, CancellationToken cancellationToken) => Task.FromResult("init");

        public Task<string> ApplyDeltaAsync(Guid organizationId, string expectedMarkerSubjectId, string newMarkerSubjectId, IReadOnlyList<RelationshipDeltaEntry> delta, CancellationToken cancellationToken)
        {
            MarkerWrites.Add((expectedMarkerSubjectId, newMarkerSubjectId));
            return Task.FromResult("written-" + MarkerWrites.Count);
        }

        public Task<MarkerObservation> ReadMarkerAsync(Guid organizationId, MarkerReadConsistency consistency, string? atLeastAsFreshToken, CancellationToken cancellationToken) =>
            MarkerFailure is { } failure ? Task.FromException<MarkerObservation>(failure) : Task.FromResult(Markers!(consistency));

        public Task<bool> TupleExistsAsync(RelationshipTuple tuple, CancellationToken cancellationToken) => Task.FromResult(true);

        public async Task<SpiceDbCheckOutcome> CheckAsync(SpiceDbCheck check, VerifiedProjectionSnapshot snapshot, CancellationToken cancellationToken) =>
            (await CheckBulkAsync([check], snapshot, cancellationToken).ConfigureAwait(false))[0];

        public Task<IReadOnlyList<SpiceDbCheckOutcome>> CheckBulkAsync(IReadOnlyList<SpiceDbCheck> checks, VerifiedProjectionSnapshot snapshot, CancellationToken cancellationToken)
        {
            snapshot.RequireOpen();
            DecisionReads++;
            DuringCheck?.Invoke();
            return CheckFailure is { } failure ? Task.FromException<IReadOnlyList<SpiceDbCheckOutcome>>(failure) : Task.FromResult<IReadOnlyList<SpiceDbCheckOutcome>>(checks.Select(Outcome).ToList());
        }

        public Task<IReadOnlyList<string>> LookupResourcesAsync(string resourceType, string permission, string subjectType, string subjectId, VerifiedProjectionSnapshot snapshot, int maximumResults, CancellationToken cancellationToken)
        {
            snapshot.RequireOpen();
            DecisionReads++;
            return Task.FromResult<IReadOnlyList<string>>([]);
        }

        public Task<(bool Ready, string Detail)> ProbeAsync(CancellationToken cancellationToken) => Task.FromResult((true, "fake"));
    }

    private sealed class Harness
    {
        public Guid Org { get; } = Guid.CreateVersion7();

        public InMemoryAuthorityStore Store { get; } = new();

        public FakeSpiceDb Spice { get; } = new();

        public TestProjection.RecordingAlerts Alerts { get; } = new();

        public ProjectionMarkerTransitionState Transition { get; } = new();

        public ProjectionIntegrityVerifier Verifier { get; }

        public OrganizationAuthorizationState State { get; }

        public Harness(long revision = 7, bool transitionCompleted = true)
        {
            if (transitionCompleted)
            {
                Transition.MarkCompleted();
            }

            Verifier = new ProjectionIntegrityVerifier(Store, Spice, Transition, Alerts, new ProjectionIntegrityMetrics(), TimeProvider.System);
            State = new OrganizationAuthorizationState { OrganizationId = Org, AuthorizationRevision = revision, ProjectionAppliedRevision = revision, ProjectionZedToken = "zed-" + revision, CheckpointEnforcementModeName = CheckpointEnforcementModes.Asynchronous }.WithJournalDigest();
            Store.Organizations.Add(State);
            Spice.Markers = _ => Consistent();
        }

        public string Expected => ProjectionMarker.ExpectedFor(State);

        public MarkerObservation Consistent() => new([Expected], "pin-" + State.ProjectionAppliedRevision);

        public static MarkerObservation Of(params string[] markers) => new(markers, markers.Length == 0 ? null : "pin-x");

        public async Task<VerifiedProjectionSnapshot> PinAsync()
        {
            await using var scope = await Store.BeginScopeAsync(CancellationToken.None);
            return await Verifier.PinAsync(scope, State, organizationLocked: false, Guid.CreateVersion7(), CancellationToken.None);
        }

        public async Task<(bool Completed, IReadOnlyList<SpiceDbCheckOutcome> Outcomes)> DecideAsync()
        {
            await using var scope = await Store.BeginScopeAsync(CancellationToken.None);
            var snapshot = await Verifier.PinAsync(scope, State, organizationLocked: false, Guid.CreateVersion7(), CancellationToken.None);
            var outcomes = await Verifier.CheckBulkAsync(snapshot, [new SpiceDbCheck("organization", Org.ToString("D"), "view_audit", "user", "u")], CancellationToken.None);
            return (await Verifier.CompleteAsync(snapshot, "test", CancellationToken.None), outcomes);
        }

        public List<SecurityJournalEntry> QuarantineEvents => Store.Journal.Where(j => j.JournalEventKind == ProjectionIntegrityVerifier.QuarantineEventKind).ToList();
    }

    // ---- U-01 marker format ----

    [Fact]
    public void TheMarkerNamesTheRevisionAndTheChainedJournalDigest()
    {
        var org = Guid.Parse("019a5b2c-1111-7000-8000-000000000001");
        var digest = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();
        Assert.Equal("step-019a5b2c-1111-7000-8000-000000000001-0-" + ProjectionMarker.ZeroDigestHex, ProjectionMarker.SubjectId(org, 0, null));
        Assert.Equal("step-019a5b2c-1111-7000-8000-000000000001-12-000102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1e1f", ProjectionMarker.SubjectId(org, 12, digest));
        Assert.Throws<ArgumentException>(() => ProjectionMarker.SubjectId(org, 3, null));
        Assert.Throws<ArgumentException>(() => ProjectionMarker.SubjectId(org, 3, new byte[31]));

        Assert.True(ProjectionMarker.TryParse(org, ProjectionMarker.SubjectId(org, 12, digest), out var revision, out var hex));
        Assert.Equal((12L, Convert.ToHexStringLower(digest)), (revision, hex));
        Assert.True(ProjectionMarker.TryParse(org, "step-019a5b2c-1111-7000-8000-000000000001-12", out revision, out hex)); // v1
        Assert.Equal((12L, (string?)null), (revision, hex));
        foreach (var malformed in new[] { "step-019a5b2c-1111-7000-8000-000000000001-012", "step-019a5b2c-1111-7000-8000-000000000001--1", "step-019a5b2c-1111-7000-8000-000000000001-1-ABC", "step-" + Guid.CreateVersion7().ToString("D") + "-1", "step-019a5b2c-1111-7000-8000-000000000001-1-" + new string('A', 64) })
        {
            Assert.False(ProjectionMarker.TryParse(org, malformed, out _, out _), malformed);
        }
    }

    [Fact]
    public void ClassificationNamesEveryMismatch()
    {
        var org = Guid.CreateVersion7();
        var d7 = new byte[32];
        d7[0] = 7;
        var expected = ProjectionMarker.SubjectId(org, 7, d7);
        var other = new byte[32];
        other[0] = 9;
        Assert.Null(ProjectionMarker.Classify(org, 7, expected, [expected]));
        Assert.Equal(ProjectionIntegrityReasons.MarkerMissing, ProjectionMarker.Classify(org, 7, expected, []));
        Assert.Equal(ProjectionIntegrityReasons.MarkerInvalid, ProjectionMarker.Classify(org, 7, expected, [expected, expected]));
        Assert.Equal(ProjectionIntegrityReasons.MarkerInvalid, ProjectionMarker.Classify(org, 7, expected, ["garbage"]));
        Assert.Equal(ProjectionIntegrityReasons.MarkerBehind, ProjectionMarker.Classify(org, 7, expected, [ProjectionMarker.SubjectId(org, 6, other)]));
        Assert.Equal(ProjectionIntegrityReasons.MarkerAhead, ProjectionMarker.Classify(org, 7, expected, [ProjectionMarker.SubjectId(org, 8, other)]));
        Assert.Equal(ProjectionIntegrityReasons.ForeignHistory, ProjectionMarker.Classify(org, 7, expected, [ProjectionMarker.SubjectId(org, 7, other)]));
        Assert.Equal(ProjectionIntegrityReasons.MarkerInvalid, ProjectionMarker.Classify(org, 7, expected, ["step-" + org.ToString("D") + "-7"])); // legacy v1 at the revision
    }

    // ---- U-02/U-03 state machine ----

    [Fact]
    public async Task AConsistentProjectionDecidesAndTheSnapshotIsSingleUse()
    {
        var h = new Harness();
        await using var scope = await h.Store.BeginScopeAsync(CancellationToken.None);
        var snapshot = await h.Verifier.PinAsync(scope, h.State, organizationLocked: false, Guid.CreateVersion7(), CancellationToken.None);
        Assert.Equal(ProjectionSnapshotState.Open, snapshot.State);
        Assert.Equal("pin-7", snapshot.Pin);
        var outcome = await h.Verifier.CheckAsync(snapshot, new SpiceDbCheck("organization", h.Org.ToString("D"), "view_audit", "user", "u"), CancellationToken.None);
        Assert.Equal(SpiceDbCheckOutcome.Allowed, outcome);
        Assert.Throws<InvalidOperationException>(snapshot.RequireCompleted);
        Assert.True(await h.Verifier.CompleteAsync(snapshot, "test", CancellationToken.None));
        Assert.Equal(ProjectionSnapshotState.Completed, snapshot.State);
        snapshot.RequireCompleted();

        // After the verify step nothing accepts the snapshot: no check-complete-check-return path exists.
        await Assert.ThrowsAsync<InvalidOperationException>(() => h.Verifier.CheckAsync(snapshot, new SpiceDbCheck("organization", h.Org.ToString("D"), "view_audit", "user", "u"), CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() => h.Verifier.CompleteAsync(snapshot, "test", CancellationToken.None));
        Assert.False(h.State.IntegrityQuarantineEnabled);
        Assert.Empty(h.Alerts.Raised);
    }

    [Theory]
    [InlineData("behind", ProjectionIntegrityReasons.MarkerBehind)]
    [InlineData("ahead", ProjectionIntegrityReasons.MarkerAhead)]
    [InlineData("missing", ProjectionIntegrityReasons.MarkerMissing)]
    [InlineData("duplicated", ProjectionIntegrityReasons.MarkerInvalid)]
    [InlineData("foreign", ProjectionIntegrityReasons.ForeignHistory)]
    public async Task APinMismatchWithAnUnchangedStateQuarantinesPermanentlyWithEvidenceAndAnAlert(string kind, string reason)
    {
        var h = new Harness();
        var foreignDigest = new byte[32];
        foreignDigest[5] = 0x55;
        var observed = kind switch
        {
            "behind" => Harness.Of(ProjectionMarker.SubjectId(h.Org, 6, foreignDigest)),
            "ahead" => Harness.Of(ProjectionMarker.SubjectId(h.Org, 8, foreignDigest)),
            "missing" => Harness.Of(),
            "duplicated" => Harness.Of(h.Expected, h.Expected),
            _ => Harness.Of(ProjectionMarker.SubjectId(h.Org, 7, foreignDigest)),
        };
        h.Spice.Markers = _ => observed;

        var failure = await Assert.ThrowsAsync<AuthorityException>(h.PinAsync);
        Assert.Equal((AuthorityErrorCodes.IntegrityQuarantine, 423), (failure.ErrorCode, failure.HttpStatusCode));
        Assert.True(h.State.IntegrityQuarantineEnabled);
        Assert.Equal(reason, h.State.IntegrityQuarantineReasonCode);
        Assert.Equal(0, h.Spice.DecisionReads); // nothing was decided
        var evidence = Assert.Single(h.QuarantineEvents);
        Assert.Equal(JournalStreamNames.Audit, evidence.JournalStreamName);
        Assert.Contains(reason, evidence.JournalEventBodyJson, StringComparison.Ordinal);
        Assert.Contains("\"pin\"", evidence.JournalEventBodyJson, StringComparison.Ordinal);
        var alert = Assert.Single(h.Alerts.Raised);
        Assert.Equal((ProjectionIntegrityVerifier.QuarantineAlertKind, "Critical", h.Org), (alert.AlertKind, alert.Severity, alert.OrganizationId));

        // Permanent: every later decision is refused without consulting SpiceDB, and the first reason is kept.
        h.Spice.Markers = _ => h.Consistent();
        Assert.Equal(423, (await Assert.ThrowsAsync<AuthorityException>(h.PinAsync)).HttpStatusCode);
        OrganizationQuarantine.Set(h.State, "SOMETHING_ELSE");
        Assert.Equal(reason, h.State.IntegrityQuarantineReasonCode);
        Assert.Single(h.QuarantineEvents);
    }

    /// <summary>
    /// C-133 PD-3: the quarantine commit also deletes stored search results (trigger), so it can fail. A failed commit is never
    /// reported as a quarantine or as success: the original exception propagates (the request is refused), a Critical
    /// QUARANTINE_NOT_COMMITTED alert is raised instead of the quarantine alert, and the next decision observes the persistent
    /// mismatch again and commits the quarantine (retry).
    /// </summary>
    [Fact]
    public async Task AQuarantineThatCannotBeCommittedIsRefusedAlertedAndRetriedByTheNextDecision()
    {
        var h = new Harness();
        var foreignDigest = new byte[32];
        foreignDigest[3] = 0x33;
        h.Spice.Markers = _ => Harness.Of(ProjectionMarker.SubjectId(h.Org, 8, foreignDigest));
        h.Store.FailNextCommit = new InvalidOperationException("simulated failure of the stored-result deletion");

        var failed = await Assert.ThrowsAsync<InvalidOperationException>(h.PinAsync);
        Assert.Equal("simulated failure of the stored-result deletion", failed.Message); // never converted into a 423 or a success
        Assert.Equal(0, h.Spice.DecisionReads); // nothing was decided or released
        var alert = Assert.Single(h.Alerts.Raised);
        Assert.Equal((ProjectionIntegrityVerifier.QuarantineNotCommittedAlertKind, "Critical", h.Org), (alert.AlertKind, alert.Severity, alert.OrganizationId));
        Assert.Contains(ProjectionIntegrityReasons.MarkerAhead, alert.Summary, StringComparison.Ordinal);

        // Retry: the mismatch is persistent state; the next decision confirms it again and this time the quarantine commits.
        h.State.IntegrityQuarantineEnabled = false; // the in-memory store does not roll back; AuthorityDb does (Docker PD-3 test)
        h.State.IntegrityQuarantineReasonCode = null;
        var retried = await Assert.ThrowsAsync<AuthorityException>(h.PinAsync);
        Assert.Equal((AuthorityErrorCodes.IntegrityQuarantine, 423), (retried.ErrorCode, retried.HttpStatusCode));
        Assert.True(h.State.IntegrityQuarantineEnabled);
        Assert.Equal(ProjectionIntegrityReasons.MarkerAhead, h.State.IntegrityQuarantineReasonCode);
        Assert.Equal([ProjectionIntegrityVerifier.QuarantineNotCommittedAlertKind, ProjectionIntegrityVerifier.QuarantineAlertKind], h.Alerts.Raised.Select(a => a.AlertKind));
    }

    [Fact]
    public async Task ARestoreBetweenThePinAndTheDecisionIsCaughtByTheVerifyRead()
    {
        var h = new Harness();
        var behind = Harness.Of(ProjectionMarker.SubjectId(h.Org, 3, new byte[32]));
        h.Spice.Markers = consistency => consistency == MarkerReadConsistency.AtLeastAsFresh ? h.Consistent() : behind; // E2/E3
        var failure = await Assert.ThrowsAsync<AuthorityException>(h.DecideAsync);
        Assert.Equal(423, failure.HttpStatusCode);
        Assert.Equal(ProjectionIntegrityReasons.MarkerBehind, h.State.IntegrityQuarantineReasonCode);
        Assert.Contains("\"verify\"", Assert.Single(h.QuarantineEvents).JournalEventBodyJson, StringComparison.Ordinal);
        Assert.Equal(1, h.Spice.DecisionReads); // the check ran, but its result was never released
    }

    [Fact]
    public async Task AMismatchCausedByLegitimateProgressNeverQuarantines()
    {
        var h = new Harness();
        // A grant commits and is projected while the request runs: the head marker is revision 8 and the Authority is at 8.
        var advancedDigest = new byte[32];
        advancedDigest[0] = 8;
        h.Spice.DuringCheck = () =>
        {
            h.Store.Organizations.Remove(h.State);
            h.Store.Organizations.Add(new OrganizationAuthorizationState { OrganizationId = h.Org, AuthorizationRevision = 8, ProjectionAppliedRevision = 8, ProjectionZedToken = "zed-8", LatestSecurityJournalDigest = advancedDigest, CheckpointEnforcementModeName = CheckpointEnforcementModes.Asynchronous });
        };
        h.Spice.Markers = consistency => consistency == MarkerReadConsistency.AtLeastAsFresh ? h.Consistent() : Harness.Of(ProjectionMarker.SubjectId(h.Org, 8, advancedDigest));
        var failure = await Assert.ThrowsAsync<AuthorityException>(h.DecideAsync);
        Assert.Equal((AuthorityErrorCodes.AuthorityNotReady, 503, true), (failure.ErrorCode, failure.HttpStatusCode, failure.Retryable));
        Assert.All(h.Store.Organizations, o => Assert.False(o.IntegrityQuarantineEnabled));
        Assert.Empty(h.QuarantineEvents);
        Assert.Empty(h.Alerts.Raised);
    }

    [Fact]
    public async Task AStateThatMovedWithoutAMarkerChangeReturnsNotCompleted()
    {
        var h = new Harness();
        var marker = h.Consistent(); // SpiceDB still holds revision 7
        h.Spice.Markers = _ => marker;
        h.Spice.DuringCheck = () => h.State.AuthorizationRevision = 8; // a revoke committed, not yet projected
        var (completed, _) = await h.DecideAsync();
        Assert.False(completed);
        Assert.False(h.State.IntegrityQuarantineEnabled);
    }

    // ---- U-04…U-08 failure classes ----

    [Fact]
    public async Task AMissingSchemaQuarantines()
    {
        var h = new Harness();
        h.Spice.MarkerFailure = new SpiceDbSchemaMissingException("no schema");
        Assert.Equal(423, (await Assert.ThrowsAsync<AuthorityException>(h.PinAsync)).HttpStatusCode);
        Assert.Equal(ProjectionIntegrityReasons.SchemaMissing, h.State.IntegrityQuarantineReasonCode);
    }

    [Fact]
    public async Task AnUnavailableSpiceDbFailsClosedWithoutQuarantine()
    {
        var h = new Harness();
        h.Spice.MarkerFailure = new SpiceDbUnavailableException("down");
        Assert.Equal(503, (await Assert.ThrowsAsync<AuthorityException>(h.PinAsync)).HttpStatusCode);
        h.Spice.MarkerFailure = null;
        h.Spice.CheckFailure = new SpiceDbUnavailableException("down");
        Assert.Equal(503, (await Assert.ThrowsAsync<AuthorityException>(h.DecideAsync)).HttpStatusCode);
        Assert.False(h.State.IntegrityQuarantineEnabled);
        Assert.Empty(h.Alerts.Raised);
    }

    [Fact]
    public async Task AnUnprovableSnapshotTokenDeniesAndAlertsWithoutQuarantine()
    {
        var h = new Harness();
        h.Spice.CheckFailure = new SpiceDbSnapshotException(SpiceDbSnapshotFailure.TokenMismatch, "checked_at differs");
        Assert.Equal(503, (await Assert.ThrowsAsync<AuthorityException>(h.DecideAsync)).HttpStatusCode);
        Assert.False(h.State.IntegrityQuarantineEnabled);
        Assert.Equal(("PROJECTION_SNAPSHOT_MISMATCH", "Critical"), (Assert.Single(h.Alerts.Raised).AlertKind, h.Alerts.Raised[0].Severity));

        var expired = new Harness();
        expired.Spice.CheckFailure = new SpiceDbSnapshotException(SpiceDbSnapshotFailure.Unavailable, "expired");
        Assert.Equal(503, (await Assert.ThrowsAsync<AuthorityException>(expired.DecideAsync)).HttpStatusCode);
        Assert.False(expired.State.IntegrityQuarantineEnabled);
        Assert.Equal("Warning", Assert.Single(expired.Alerts.Raised).Severity);
    }

    [Fact]
    public async Task ADatastoreChangeMidRequestIsDecidedByTheLockConfirmRead()
    {
        // The head still carries this history's marker: deny this request only.
        var same = new Harness();
        same.Spice.CheckFailure = new SpiceDbSnapshotException(SpiceDbSnapshotFailure.DatastoreChanged, "different datastore instance");
        Assert.Equal(503, (await Assert.ThrowsAsync<AuthorityException>(same.DecideAsync)).HttpStatusCode);
        Assert.False(same.State.IntegrityQuarantineEnabled);

        // The head now carries an older marker: quarantine.
        var older = new Harness();
        older.Spice.CheckFailure = new SpiceDbSnapshotException(SpiceDbSnapshotFailure.DatastoreChanged, "different datastore instance");
        older.Spice.Markers = consistency => consistency == MarkerReadConsistency.AtLeastAsFresh ? older.Consistent() : Harness.Of(ProjectionMarker.SubjectId(older.Org, 2, new byte[32]));
        Assert.Equal(423, (await Assert.ThrowsAsync<AuthorityException>(older.DecideAsync)).HttpStatusCode);
        Assert.Equal(ProjectionIntegrityReasons.MarkerBehind, older.State.IntegrityQuarantineReasonCode);
        Assert.Contains("\"snapshot_error\"", Assert.Single(older.QuarantineEvents).JournalEventBodyJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NothingIsPinnedBeforeTheTransitionOrWhileQuarantinedOrUnprojected()
    {
        var pending = new Harness(transitionCompleted: false);
        Assert.Equal("ProjectionMarkerTransitionPending", (await Assert.ThrowsAsync<AuthorityException>(pending.PinAsync)).Message);

        var quarantined = new Harness();
        OrganizationQuarantine.Set(quarantined.State, ProjectionIntegrityReasons.MarkerBehind);
        quarantined.Spice.Markers = _ => throw new InvalidOperationException("SpiceDB must not be consulted");
        Assert.Equal(423, (await Assert.ThrowsAsync<AuthorityException>(quarantined.PinAsync)).HttpStatusCode);

        var unprojected = new Harness();
        unprojected.State.AuthorizationRevision = 8;
        Assert.Equal(503, (await Assert.ThrowsAsync<AuthorityException>(unprojected.PinAsync)).HttpStatusCode);

        var noDigest = new Harness();
        noDigest.State.LatestSecurityJournalDigest = null;
        Assert.Equal(503, (await Assert.ThrowsAsync<AuthorityException>(noDigest.PinAsync)).HttpStatusCode);
        Assert.False(noDigest.State.IntegrityQuarantineEnabled);
    }

    [Fact]
    public async Task ALockedCallerQuarantinesInItsOwnScope()
    {
        var h = new Harness();
        h.Spice.Markers = _ => Harness.Of(ProjectionMarker.SubjectId(h.Org, 1, new byte[32]));
        await using var scope = await h.Store.BeginScopeAsync(CancellationToken.None);
        var locked = await scope.LockOrganizationAsync(h.Org, CancellationToken.None);
        Assert.Equal(423, (await Assert.ThrowsAsync<AuthorityException>(() => h.Verifier.PinAsync(scope, locked!, organizationLocked: true, Guid.CreateVersion7(), CancellationToken.None))).HttpStatusCode);
        Assert.True(h.State.IntegrityQuarantineEnabled);
        Assert.Single(h.QuarantineEvents);
    }

    // ---- U-09 one authorization-stream entry per revision is asserted by the architecture test; U-10 projector, transition, S2 ----

    [Fact]
    public async Task TheProjectorNeverWritesForAQuarantinedOrPreTransitionOrganization()
    {
        foreach (var (quarantine, transitionDone) in new[] { (true, true), (false, false) })
        {
            var store = new InMemoryAuthorityStore();
            var spice = new FakeSpiceDb();
            var transition = new ProjectionMarkerTransitionState();
            if (transitionDone)
            {
                transition.MarkCompleted();
            }

            var org = Guid.CreateVersion7();
            var state = new OrganizationAuthorizationState { OrganizationId = org, AuthorizationRevision = 2, ProjectionAppliedRevision = 1, ProjectionZedToken = "z", CheckpointEnforcementModeName = CheckpointEnforcementModes.Asynchronous }.WithJournalDigest();
            if (quarantine)
            {
                OrganizationQuarantine.Set(state, ProjectionIntegrityReasons.MarkerBehind);
            }

            store.Organizations.Add(state);
            var processor = new ProjectionProcessor(store, spice, transition, new NoOpFaultInjector(), TimeProvider.System);
            Assert.Equal(ProjectionStepResult.Idle, await processor.ProcessOrganizationAsync(org, CancellationToken.None));
            Assert.Empty(spice.MarkerWrites);
            Assert.Equal(1, state.ProjectionAppliedRevision);
        }
    }

    [Fact]
    public async Task TheProjectorMovesTheHistoryBoundMarkerWithEveryRevision()
    {
        var store = new InMemoryAuthorityStore();
        var spice = new FakeSpiceDb();
        var transition = new ProjectionMarkerTransitionState();
        transition.MarkCompleted();
        var org = Guid.CreateVersion7();
        var previous = new byte[32];
        previous[0] = 1;
        var current = new byte[32];
        current[0] = 2;
        var command = Guid.CreateVersion7();
        store.Organizations.Add(new OrganizationAuthorizationState { OrganizationId = org, AuthorizationRevision = 2, ProjectionAppliedRevision = 1, ProjectionZedToken = "z1", LatestSecurityJournalDigest = current, CheckpointEnforcementModeName = CheckpointEnforcementModes.Asynchronous });
        store.Outbox.Add(new ProjectionOutboxEvent { ProjectionOutboxEventId = Guid.CreateVersion7(), OrganizationId = org, SecurityCommandId = command, TargetAuthorizationRevision = 2, RelationshipDeltaJson = "[]", ProjectionCreatedOn = DateTimeOffset.UtcNow });
        store.Journal.Add(new SecurityJournalEntry { SecurityJournalEntryId = Guid.CreateVersion7(), OrganizationId = org, JournalStreamName = JournalStreamNames.Authorization, JournalStreamRevision = 2, JournalEventKind = "SecurityCommandCommitted", JournalEventBodyJson = "{}", JournalEventDigest = current, JournalPreviousDigest = previous, ArchiveDeliveryState = ArchiveDeliveryStates.Anchored, JournalCreatedOn = DateTimeOffset.UtcNow });
        var processor = new ProjectionProcessor(store, spice, transition, new NoOpFaultInjector(), TimeProvider.System);
        Assert.Equal(ProjectionStepResult.Applied, await processor.ProcessOrganizationAsync(org, CancellationToken.None));
        Assert.Equal((ProjectionMarker.SubjectId(org, 1, previous), ProjectionMarker.SubjectId(org, 2, current)), Assert.Single(spice.MarkerWrites));
    }

    [Fact]
    public async Task TheTransitionUpgradesLegacyMarkersAndQuarantinesInconsistentOnes()
    {
        var h = new Harness(revision: 4);
        h.Store.Journal.Add(new SecurityJournalEntry { SecurityJournalEntryId = Guid.CreateVersion7(), OrganizationId = h.Org, JournalStreamName = JournalStreamNames.Authorization, JournalStreamRevision = 4, JournalEventKind = "SecurityCommandCommitted", JournalEventBodyJson = "{}", JournalEventDigest = h.State.LatestSecurityJournalDigest!, ArchiveDeliveryState = ArchiveDeliveryStates.Anchored, JournalCreatedOn = DateTimeOffset.UtcNow });
        var legacy = "step-" + h.Org.ToString("D") + "-4";
        h.Spice.Markers = _ => Harness.Of(legacy);
        var behind = new OrganizationAuthorizationState { OrganizationId = Guid.CreateVersion7(), AuthorizationRevision = 4, ProjectionAppliedRevision = 4, ProjectionZedToken = "z", CheckpointEnforcementModeName = CheckpointEnforcementModes.Asynchronous }.WithJournalDigest();
        var unprojected = new OrganizationAuthorizationState { OrganizationId = Guid.CreateVersion7(), AuthorizationRevision = 0, ProjectionAppliedRevision = 0, CheckpointEnforcementModeName = CheckpointEnforcementModes.Asynchronous };
        h.Store.Organizations.Add(behind);
        h.Store.Organizations.Add(unprojected);
        var spice = new ScriptedSpice(h.Spice, new Dictionary<Guid, MarkerObservation>
        {
            [h.Org] = Harness.Of(legacy),
            [behind.OrganizationId] = Harness.Of("step-" + behind.OrganizationId.ToString("D") + "-3"),
            [unprojected.OrganizationId] = Harness.Of(),
        });
        var verifier = new ProjectionIntegrityVerifier(h.Store, spice, h.Transition, h.Alerts, new ProjectionIntegrityMetrics(), TimeProvider.System);
        var transition = new ProjectionMarkerTransitionState();
        var monitor = new ProjectionIntegrityMonitor(h.Store, spice, verifier, transition);
        Assert.Equal(1, await monitor.RunTransitionAsync(CancellationToken.None));
        Assert.True(transition.Completed);
        Assert.Equal((legacy, h.Expected), Assert.Single(h.Spice.MarkerWrites)); // CAS from the exact legacy marker to the history-bound one
        Assert.False(h.State.IntegrityQuarantineEnabled);
        Assert.Equal(ProjectionIntegrityReasons.MarkerBehind, behind.IntegrityQuarantineReasonCode);
        Assert.False(unprojected.IntegrityQuarantineEnabled);
    }

    [Fact]
    public async Task ThePeriodicCheckQuarantinesAnIdleOrganizationAndNeverClearsAnything()
    {
        var h = new Harness();
        var monitor = new ProjectionIntegrityMonitor(h.Store, h.Spice, h.Verifier, h.Transition);
        Assert.True(await monitor.RunPeriodicCheckAsync(CancellationToken.None));
        Assert.False(h.State.IntegrityQuarantineEnabled);

        h.Spice.Markers = _ => Harness.Of(ProjectionMarker.SubjectId(h.Org, 5, new byte[32]));
        Assert.True(await monitor.RunPeriodicCheckAsync(CancellationToken.None));
        Assert.Equal(ProjectionIntegrityReasons.MarkerBehind, h.State.IntegrityQuarantineReasonCode);
        Assert.Contains("\"periodic\"", Assert.Single(h.QuarantineEvents).JournalEventBodyJson, StringComparison.Ordinal);

        h.Spice.Markers = _ => h.Consistent(); // even a datastore that is consistent again does not lift it
        Assert.True(await monitor.RunPeriodicCheckAsync(CancellationToken.None));
        Assert.True(h.State.IntegrityQuarantineEnabled);

        var down = new Harness();
        down.Spice.MarkerFailure = new SpiceDbUnavailableException("down");
        Assert.False(await new ProjectionIntegrityMonitor(down.Store, down.Spice, down.Verifier, down.Transition).RunPeriodicCheckAsync(CancellationToken.None));
        Assert.False(down.State.IntegrityQuarantineEnabled);
    }

    /// <summary>Per-organization marker observations over the fake (the transition inspects several organizations).</summary>
    private sealed class ScriptedSpice(FakeSpiceDb inner, Dictionary<Guid, MarkerObservation> markers) : ISpiceDbProjection
    {
        public Task EnsureSchemaAsync(CancellationToken cancellationToken) => inner.EnsureSchemaAsync(cancellationToken);

        public Task<string> InitializeCursorAsync(Guid organizationId, CancellationToken cancellationToken) => inner.InitializeCursorAsync(organizationId, cancellationToken);

        public Task<string> ApplyDeltaAsync(Guid organizationId, string expectedMarkerSubjectId, string newMarkerSubjectId, IReadOnlyList<RelationshipDeltaEntry> delta, CancellationToken cancellationToken) =>
            inner.ApplyDeltaAsync(organizationId, expectedMarkerSubjectId, newMarkerSubjectId, delta, cancellationToken);

        public Task<MarkerObservation> ReadMarkerAsync(Guid organizationId, MarkerReadConsistency consistency, string? atLeastAsFreshToken, CancellationToken cancellationToken) => Task.FromResult(markers[organizationId]);

        public Task<bool> TupleExistsAsync(RelationshipTuple tuple, CancellationToken cancellationToken) => inner.TupleExistsAsync(tuple, cancellationToken);

        public Task<SpiceDbCheckOutcome> CheckAsync(SpiceDbCheck check, VerifiedProjectionSnapshot snapshot, CancellationToken cancellationToken) => inner.CheckAsync(check, snapshot, cancellationToken);

        public Task<IReadOnlyList<SpiceDbCheckOutcome>> CheckBulkAsync(IReadOnlyList<SpiceDbCheck> checks, VerifiedProjectionSnapshot snapshot, CancellationToken cancellationToken) => inner.CheckBulkAsync(checks, snapshot, cancellationToken);

        public Task<IReadOnlyList<string>> LookupResourcesAsync(string resourceType, string permission, string subjectType, string subjectId, VerifiedProjectionSnapshot snapshot, int maximumResults, CancellationToken cancellationToken) =>
            inner.LookupResourcesAsync(resourceType, permission, subjectType, subjectId, snapshot, maximumResults, cancellationToken);

        public Task<(bool Ready, string Detail)> ProbeAsync(CancellationToken cancellationToken) => inner.ProbeAsync(cancellationToken);
    }
}
