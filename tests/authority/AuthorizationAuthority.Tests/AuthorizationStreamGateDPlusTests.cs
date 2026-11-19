using System.Security.Cryptography;
using System.Text;
using AuthorizationAuthority.Application.Common;
using AuthorizationAuthority.Application.Common.Ports;
using AuthorizationAuthority.Application.Features.Journal;
using AuthorizationAuthority.Application.Features.Journal.Reconciliation;
using AuthorizationAuthority.Application.Features.Journal.Recovery;
using AuthorizationAuthority.Application.Features.Journal.Verification;
using AuthorizationAuthority.Contracts.V1;
using AuthorizationAuthority.Domain.Features.Journal;
using AuthorizationAuthority.Infrastructure.Archive;
using Xunit;

namespace AuthorizationAuthority.Tests;

/// <summary>
/// SEC-BL-01 WP-5b (C-157, P2-D4 authorization stage; RD-4, RD-6, RD-7, RD-8): authorization-stream D+ and recovery against the real
/// organization gate (<see cref="OrganizationGate"/>, mode <c>RequiredBeforeEffective</c>). Protected Allow is possible only when every
/// authorization journal entry is delivered; a stop holds the stopped entry and every later one (revocations included) Pending, so the gate stays
/// closed until an authorized recovery and delivery; recovery writes no authorization state and never lifts a quarantine. The pre-existing E-6
/// limit (the gate trusts the rows' delivery state) is characterized, not claimed closed.
/// </summary>
public sealed class AuthorizationStreamGateDPlusTests : IDisposable
{
    private const string Authorization = JournalStreamNames.Authorization;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly CapabilityRefusalEvidenceWorld world = new();
    private readonly InMemorySecurityEventArchive archive = new();
    private readonly InMemoryJournalVerificationFindingStore findings = new();
    private readonly SyntheticRecoveryApprovers approvers = new("approver-a", "approver-b", "approver-c");
    private readonly Guid org;
    private readonly string instance;
    private readonly SecurityJournalEntry history;

    public AuthorizationStreamGateDPlusTests()
    {
        org = world.AddOrganization();
        history = world.AddStreamHistory(org, Authorization);
        world.DPlus.Approvals = approvers.Verifier;
        instance = world.DPlus.ProvisionAsync(archive, world.Clock.GetUtcNow()).GetAwaiter().GetResult();
        world.JournalReceipts = new TrustedJournalReceipts(archive, world.DPlus.Durability); // E-6: the gate needs the trusted receipt
        archive.AppendAsync(org, Authorization, 40, Encoding.UTF8.GetBytes(history.JournalEventBodyJson), history.JournalEventDigest, CancellationToken.None).GetAwaiter().GetResult();
    }

    public void Dispose() => approvers.Dispose();

    private ArchiveProcessor Processor(ISecurityEventArchive? over = null) => new(world.Store, over ?? archive, world.Clock, new RecordingAlerts(), findings, world.DPlus.Durability);

    private DPlusStreamRecoveryService Recovery() => new(world.Store, archive, findings, world.DPlus.Durability, world.Clock);

    private Task<int> PassAsync(ISecurityEventArchive? over = null) => Processor(over).ProcessOrganizationAsync(org, Ct);

    private SecurityJournalEntry Command(string kind, string subject)
    {
        world.Clock.Advance(TimeSpan.FromSeconds(1));
        return world.SeedStream(org, Authorization, kind, new { subject });
    }

    private async Task<ProjectionGateState> GateAsync(Guid? organization = null)
    {
        await using var scope = await world.Store.BeginScopeAsync(Ct);
        var state = (await scope.LockOrganizationAsync(organization ?? org, Ct))!;
        return await OrganizationGate.EvaluateAsync(scope, state, Ct);
    }

    private List<SecurityJournalEntry> Rows(Guid? organization = null)
    {
        lock (world.Journal)
        {
            return [.. world.Journal.Where(e => e.OrganizationId == (organization ?? org) && e.JournalStreamName == Authorization).OrderBy(e => e.JournalStreamRevision)];
        }
    }

    private (long Revision, long Applied, string? Zed, bool Quarantined) StateSnapshot()
    {
        var state = world.Organizations[org];
        return (state.AuthorizationRevision, state.ProjectionAppliedRevision, state.ProjectionZedToken, state.IntegrityQuarantineEnabled);
    }

    /// <summary>A grant at 41 delivered; its archived object removed (J4); a revocation at 42 refused (PREDECESSOR_MISSING).</summary>
    private async Task<(SecurityJournalEntry Grant, SecurityJournalEntry Revocation)> StoppedAfterGrantAsync()
    {
        var grant = Command("RelationshipGranted", "bob");
        Assert.Equal(ProjectionGateState.AwaitingArchive, await GateAsync()); // committed but not delivered: no protected Allow yet
        Assert.Equal(1, await PassAsync());
        Assert.Equal(ProjectionGateState.Ready, await GateAsync());

        Assert.True(archive.RemoveObjectForTesting($"{org:D}/{Authorization}/41"));
        var revocation = Command("RelationshipRevoked", "bob");
        Assert.Equal(ProjectionGateState.AwaitingArchive, await GateAsync()); // the revocation is effective at once: no Allow for anyone
        Assert.Equal(0, await PassAsync());
        Assert.Equal((ArchiveDeliveryStates.VerificationFailed, "DPLUS_" + JournalFindingCodes.PredecessorMissing), (revocation.ArchiveDeliveryState, revocation.LastArchiveErrorCode));
        Assert.Equal(ProjectionGateState.AwaitingArchive, await GateAsync());
        return (grant, revocation);
    }

    private async Task<Guid> ExecutedRestoreAsync(SecurityJournalEntry predecessor)
    {
        var bytes = Encoding.UTF8.GetBytes(predecessor.JournalEventBodyJson);
        var checkpoint = DPlusRecordInspection.FirstCheckpoint(org, Authorization, instance, predecessor.JournalStreamRevision, SHA256.HashData(bytes), world.Clock.GetUtcNow());
        Assert.Equal(CheckpointCreateStatus.Created, (await world.DPlus.Checkpoints.CreateAsync(org, Authorization, 1, JournalVerificationCheckpoints.Serialize(checkpoint), Ct)).Status);
        var proposed = await Recovery().ProposeAsync(new DPlusRecoveryProposal(org, Authorization, DPlusRecoveryStrategies.RestorePredecessor, "requester-r", "authorization stop",
            DPlusRecoveryStrategies.PredecessorLostCause, false, null, "analyst-q", "narrative", TimeSpan.FromHours(1), bytes, "test"), Ct);
        Assert.True(proposed.Outcome == DPlusRecoveryCommandOutcome.Done, proposed.Code + ": " + proposed.Detail);
        var id = proposed.RecoveryId!.Value;
        var statementBytes = world.DPlus.Records.Records[DPlusRecoveryNames.StoreRecoveryPrefix + DPlusRecoveryNames.Statement(org, Authorization, id)];
        var binding = SyntheticRecoveryApprovers.Binding(DPlusRecoveryCodec.TryParse<DPlusRecoveryStatement>(statementBytes)!, DPlusRecoveryCodec.Sha256Hex(statementBytes));
        foreach (var approver in new[] { "approver-a", "approver-b" })
        {
            Assert.Equal(DPlusRecoveryCommandOutcome.Done, (await Recovery().RecordDecisionAsync(org, Authorization, id, approvers.Sign(approver, binding, world.Clock.GetUtcNow()), Ct)).Outcome);
        }

        var executed = await Recovery().ExecuteAsync(org, Authorization, id, "executor-e", Ct);
        Assert.True(executed.Outcome == DPlusRecoveryCommandOutcome.Done, executed.Code + ": " + executed.Detail);
        return id;
    }

    /// <summary>
    /// RD-4: revocations committed during containment are effective immediately (no protected Allow at all) and stay effective through recovery;
    /// recovery writes no authorization state; protected Allow returns only once the stopped entry AND every later entry (the revocations) are
    /// verified and delivered — never in between.
    /// </summary>
    [Fact]
    public async Task RevocationsStayEffectiveThroughContainmentAndRecoveryAndAccessReturnsOnlyAfterTheyAreDelivered()
    {
        var (grant, revocation) = await StoppedAfterGrantAsync();
        var second = Command("RelationshipRevoked", "carol"); // committed during containment
        Assert.Equal(0, await PassAsync());
        Assert.Equal((ArchiveDeliveryStates.Pending, ArchiveProcessor.StreamStoppedCode), (second.ArchiveDeliveryState, second.LastArchiveErrorCode));
        Assert.Equal(0, await PassAsync()); // a restart rediscovers the durable stop
        Assert.Equal(ProjectionGateState.AwaitingArchive, await GateAsync());

        var before = StateSnapshot();
        await ExecutedRestoreAsync(grant);
        Assert.Equal(before, StateSnapshot()); // recovery wrote no authorization, projection or quarantine state
        Assert.Equal(ArchiveDeliveryStates.Pending, revocation.ArchiveDeliveryState); // the scheduling hint only
        Assert.Equal(ProjectionGateState.AwaitingArchive, await GateAsync());

        // The stopped revocation is delivered, the later one fails on an outage: still no protected Allow.
        var flaky = FlakyArchive.Wrap(archive, failAppendAt: second.JournalStreamRevision);
        Assert.Equal(1, await PassAsync(flaky));
        Assert.Equal((ArchiveDeliveryStates.Anchored, ArchiveDeliveryStates.Pending), (revocation.ArchiveDeliveryState, second.ArchiveDeliveryState));
        Assert.Equal(ProjectionGateState.AwaitingArchive, await GateAsync());

        Assert.Equal(1, await PassAsync());
        Assert.All(Rows(), r => Assert.Equal(ArchiveDeliveryStates.Anchored, r.ArchiveDeliveryState));
        Assert.Equal(ProjectionGateState.Ready, await GateAsync());
        Assert.Equal(Encoding.UTF8.GetBytes(grant.JournalEventBodyJson), await archive.ReadAsync(org, Authorization, grant.JournalStreamRevision, Ct)); // exactly the anchored bytes
        foreach (var row in Rows())
        {
            Assert.Equal(Encoding.UTF8.GetBytes(row.JournalEventBodyJson), await archive.ReadAsync(org, Authorization, row.JournalStreamRevision, Ct));
        }

        Assert.Equal(before, StateSnapshot());
    }

    /// <summary>RD-8: under integrity quarantine the gate stays Quarantined; recovery is refused and never lifts it.</summary>
    [Fact]
    public async Task RecoveryNeverLiftsAQuarantine()
    {
        var (grant, _) = await StoppedAfterGrantAsync();
        OrganizationQuarantine.Set(world.Organizations[org], "JOURNAL_BODY_DIGEST_MISMATCH");
        Assert.Equal(ProjectionGateState.Quarantined, await GateAsync());
        var bytes = Encoding.UTF8.GetBytes(grant.JournalEventBodyJson);
        var refused = await Recovery().ProposeAsync(new DPlusRecoveryProposal(org, Authorization, DPlusRecoveryStrategies.RestorePredecessor, "requester-r", "r",
            DPlusRecoveryStrategies.PredecessorLostCause, false, null, "analyst-q", "n", TimeSpan.FromHours(1), bytes, "test"), Ct);
        Assert.Equal(DPlusRecoveryRefusals.OrganizationQuarantined, refused.Code);
        Assert.Equal(0, await PassAsync());
        Assert.Equal(ProjectionGateState.Quarantined, await GateAsync());
        Assert.True(world.Organizations[org].IntegrityQuarantineEnabled);
    }

    /// <summary>RD-6: another organization's gate and delivery are unaffected by this organization's stop and recovery; its records lift nothing here.</summary>
    [Fact]
    public async Task AnotherOrganizationIsUnaffected()
    {
        var other = world.AddOrganization();
        var otherHistory = world.AddStreamHistory(other, Authorization);
        await archive.AppendAsync(other, Authorization, 40, Encoding.UTF8.GetBytes(otherHistory.JournalEventBodyJson), otherHistory.JournalEventDigest, Ct);
        var (grant, _) = await StoppedAfterGrantAsync();
        world.Clock.Advance(TimeSpan.FromSeconds(1));
        world.SeedStream(other, Authorization, "RelationshipGranted", new { subject = "dave" });
        Assert.Equal(1, await Processor().ProcessOrganizationAsync(other, Ct));
        Assert.Equal(ProjectionGateState.Ready, await GateAsync(other));
        Assert.Equal(ProjectionGateState.AwaitingArchive, await GateAsync());
        await ExecutedRestoreAsync(grant);
        Assert.DoesNotContain(world.DPlus.Records.Records.Keys, k => k.StartsWith(DPlusRecoveryNames.StoreRecoveryPrefix + other.ToString("D"), StringComparison.Ordinal)); // no recovery record there
        Assert.Equal(ProjectionGateState.Ready, await GateAsync(other));
    }

    /// <summary>
    /// Concurrency: permission changes committed while two processors deliver and an authorized recovery executes. Every entry is delivered exactly
    /// once and in order (contiguous archive, identical bytes, no conflict quarantine), and whenever the gate is observed Ready no authorization
    /// entry was undelivered at that moment.
    /// </summary>
    [Fact]
    public async Task ConcurrentPermissionChangesRecoveryAndDeliveryIntroduceNoUnauthorizedAccess()
    {
        var (grant, _) = await StoppedAfterGrantAsync();
        using var stop = new CancellationTokenSource();
        var violations = 0;
        var observer = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                await using var scope = await world.Store.BeginScopeAsync(Ct);
                var state = (await scope.LockOrganizationAsync(org, Ct))!;
                if (await OrganizationGate.EvaluateAsync(scope, state, Ct) == ProjectionGateState.Ready && Rows().Any(r => r.ArchiveDeliveryState != ArchiveDeliveryStates.Anchored))
                {
                    Interlocked.Increment(ref violations);
                }
            }
        }, Ct);

        var writers = Task.Run(() =>
        {
            for (var i = 0; i < 20; i++)
            {
                Command(i % 2 == 0 ? "RelationshipRevoked" : "RelationshipGranted", "user-" + i);
            }
        }, Ct);
        var deliveries = Enumerable.Range(0, 2).Select(_ => Task.Run(async () =>
        {
            for (var i = 0; i < 15; i++)
            {
                await PassAsync();
            }
        }, Ct)).ToList();
        var recoveryId = await ExecutedRestoreAsync(grant);
        await Task.WhenAll([writers, .. deliveries]);
        for (var i = 0; i < 5 && Rows().Any(r => r.ArchiveDeliveryState != ArchiveDeliveryStates.Anchored); i++)
        {
            await PassAsync();
        }

        await stop.CancelAsync();
        await observer;
        Assert.Equal(0, violations);
        Assert.False(world.Organizations[org].IntegrityQuarantineEnabled);

        // WP-7A finding F-7A-4 (pre-existing G14-R2 race, owner disposition): a pass whose stop discovery ran between the row hint (EX-1 step 5)
        // and the execution record (step 6) re-marks the hinted row (SM-4), so the executed lift holds the stream (EX-6): fail closed. The
        // contract's remedy is a re-run of the executor (EX-1: a re-run resumes idempotently); it is applied here only when the race occurred,
        // and reported, never silently. The security assertions above and below are unchanged.
        if (Rows().Single(r => r.JournalStreamRevision == grant.JournalStreamRevision + 1) is { ArchiveDeliveryState: ArchiveDeliveryStates.VerificationFailed } reverted)
        {
            TestContext.Current.TestOutputHelper?.WriteLine("F-7A-4 race occurred: the hint of revision " + reverted.JournalStreamRevision + " was re-marked (" + reverted.LastArchiveErrorCode + "); executor re-run");
            Assert.Equal(ProjectionGateState.AwaitingArchive, await GateAsync()); // held, fail closed
            Assert.Equal(0, await PassAsync());
            var resumed = await Recovery().ExecuteAsync(org, Authorization, recoveryId, "executor-e", Ct);
            Assert.True(resumed.Outcome == DPlusRecoveryCommandOutcome.Done && resumed.Code == "EXECUTION_RESUMED", resumed.Code + ": " + resumed.Detail);
            for (var i = 0; i < 5 && Rows().Any(r => r.ArchiveDeliveryState != ArchiveDeliveryStates.Anchored); i++)
            {
                await PassAsync();
            }
        }

        var rows = Rows();
        TestContext.Current.TestOutputHelper?.WriteLine(string.Join("; ", rows.Select(r => r.JournalStreamRevision + ":" + r.ArchiveDeliveryState + ":" + r.LastArchiveErrorCode + ":" + r.ArchiveAttemptCount)));
        TestContext.Current.TestOutputHelper?.WriteLine("meta: " + string.Join(", ", archive.MetaForTesting.Keys.Where(k => !k.StartsWith("meta/dplus-intents/", StringComparison.Ordinal)).Order(StringComparer.Ordinal)));
        TestContext.Current.TestOutputHelper?.WriteLine("store: " + string.Join(", ", world.DPlus.Records.Records.Keys.Where(k => !k.StartsWith("intents/", StringComparison.Ordinal)).Order(StringComparer.Ordinal)));
        TestContext.Current.TestOutputHelper?.WriteLine("findings: " + string.Join(", ", findings.Records.Keys));
        Assert.All(rows, r => Assert.Equal(ArchiveDeliveryStates.Anchored, r.ArchiveDeliveryState));
        Assert.Equal(Enumerable.Range(41, rows.Count).Select(r => (long)r), rows.Select(r => r.JournalStreamRevision));
        foreach (var row in rows)
        {
            Assert.Equal(Encoding.UTF8.GetBytes(row.JournalEventBodyJson), await archive.ReadAsync(org, Authorization, row.JournalStreamRevision, Ct));
        }

        Assert.Equal(ProjectionGateState.Ready, await GateAsync());
    }

    /// <summary>
    /// WP-7A (A-9 RV-4, GT-2): the gate's head receipt is a stream-head receipt. J2 removes the delivered newest entry (a revocation) from
    /// AuthorityDb; the previous entry, genuinely delivered, becomes the head, but its successor is archived outside AuthorityDb: the gate stays
    /// closed. J4 planting an object above the head has the same effect.
    /// </summary>
    [Fact]
    public async Task TheGateRefusesAHeadWhoseSuccessorIsArchived()
    {
        world.JournalReceipts = new TrustedJournalReceipts(archive, world.DPlus.Durability, new TrustedJournalReceiptOptions { CurrentStateMaxAge = TimeSpan.Zero }); // every evaluation re-establishes
        var grant = Command("RelationshipGranted", "bob");
        var revocation = Command("RelationshipRevoked", "bob");
        Assert.Equal(2, await PassAsync());
        Assert.Equal(ProjectionGateState.Ready, await GateAsync());
        lock (world.Journal)
        {
            world.Journal.Remove(revocation); // J2: the delivered revocation is deleted
        }

        world.Organizations[org].AuthorizationRevision = grant.JournalStreamRevision;
        world.Organizations[org].ProjectionAppliedRevision = grant.JournalStreamRevision;
        Assert.Equal(ProjectionGateState.AwaitingArchive, await GateAsync());
        Assert.True(archive.RemoveObjectForTesting($"{org:D}/{Authorization}/{revocation.JournalStreamRevision}"));
        archive.RemoveMetaForTesting(DPlusRecoveryNames.ArchiveCommitmentPrefix + DPlusRecoveryNames.Commitment(org, Authorization, revocation.JournalStreamRevision, 0));
        world.DPlus.Records.RemoveForTesting(DPlusRecoveryNames.StoreCommitmentPrefix + DPlusRecoveryNames.Commitment(org, Authorization, revocation.JournalStreamRevision, 0));
        Assert.Equal(ProjectionGateState.Ready, await GateAsync()); // positive control: J2 + J4 + J6 removing every trace (out of scope) reopens it
        archive.PutObjectForTesting($"{org:D}/{Authorization}/{revocation.JournalStreamRevision}", "{}"u8.ToArray()); // J4 plants a successor
        Assert.Equal(ProjectionGateState.AwaitingArchive, await GateAsync());
    }

    /// <summary>
    /// WP-7A finding F-7A-4 (pre-existing G14-R2 race; characterized, not changed: remediation would change EX-1 or SM-4 and needs an owner
    /// decision). A D+ pass whose stop discovery runs after the executor's row hint (EX-1 step 5) and before its execution record (step 6) still
    /// sees the stop unresolved and re-marks the hinted row with its recorded classification (SM-4). The executor then records the execution and
    /// reports Done, but the lifted revision is not pending, so EX-6 holds the stream: fail closed (no protected Allow, no delivery, no quarantine).
    /// The documented remedy, a re-run of the executor (EX-1), resumes idempotently and delivery completes.
    /// </summary>
    [Fact]
    public async Task F7A4ADeliveryPassBetweenTheRowHintAndTheExecutionRecordHoldsTheStreamUntilTheExecutorIsReRun()
    {
        var (grant, revocation) = await StoppedAfterGrantAsync();
        var racing = new PassBeforeExecutionArchive(archive, () => PassAsync());
        var service = new DPlusStreamRecoveryService(world.Store, racing, findings, world.DPlus.Durability, world.Clock);
        var bytes = Encoding.UTF8.GetBytes(grant.JournalEventBodyJson);
        var checkpoint = DPlusRecordInspection.FirstCheckpoint(org, Authorization, instance, grant.JournalStreamRevision, SHA256.HashData(bytes), world.Clock.GetUtcNow());
        Assert.Equal(CheckpointCreateStatus.Created, (await world.DPlus.Checkpoints.CreateAsync(org, Authorization, 1, JournalVerificationCheckpoints.Serialize(checkpoint), Ct)).Status);
        var proposed = await service.ProposeAsync(new DPlusRecoveryProposal(org, Authorization, DPlusRecoveryStrategies.RestorePredecessor, "requester-r", "authorization stop",
            DPlusRecoveryStrategies.PredecessorLostCause, false, null, "analyst-q", "narrative", TimeSpan.FromHours(1), bytes, "test"), Ct);
        Assert.True(proposed.Outcome == DPlusRecoveryCommandOutcome.Done, proposed.Code + ": " + proposed.Detail);
        var id = proposed.RecoveryId!.Value;
        var statementBytes = world.DPlus.Records.Records[DPlusRecoveryNames.StoreRecoveryPrefix + DPlusRecoveryNames.Statement(org, Authorization, id)];
        var binding = SyntheticRecoveryApprovers.Binding(DPlusRecoveryCodec.TryParse<DPlusRecoveryStatement>(statementBytes)!, DPlusRecoveryCodec.Sha256Hex(statementBytes));
        foreach (var approver in new[] { "approver-a", "approver-b" })
        {
            Assert.Equal(DPlusRecoveryCommandOutcome.Done, (await service.RecordDecisionAsync(org, Authorization, id, approvers.Sign(approver, binding, world.Clock.GetUtcNow()), Ct)).Outcome);
        }

        var executed = await service.ExecuteAsync(org, Authorization, id, "executor-e", Ct);
        Assert.Equal(1, racing.PassesRun); // the concurrent pass ran exactly between the hint and the execution record
        Assert.Equal((DPlusRecoveryCommandOutcome.Done, "EXECUTED"), (executed.Outcome, executed.Code));
        Assert.Equal((ArchiveDeliveryStates.VerificationFailed, "DPLUS_" + JournalFindingCodes.PredecessorMissing), (revocation.ArchiveDeliveryState, revocation.LastArchiveErrorCode)); // hint re-marked
        Assert.Equal(0, await PassAsync()); // EX-6: held
        Assert.Equal(0, await PassAsync());
        Assert.Equal(ProjectionGateState.AwaitingArchive, await GateAsync()); // fail closed
        Assert.False(world.Organizations[org].IntegrityQuarantineEnabled);

        var resumed = await Recovery().ExecuteAsync(org, Authorization, id, "executor-e", Ct); // EX-1 remedy: a re-run resumes
        Assert.Equal((DPlusRecoveryCommandOutcome.Done, "EXECUTION_RESUMED"), (resumed.Outcome, resumed.Code));
        Assert.Equal(1, await PassAsync());
        Assert.Equal(ArchiveDeliveryStates.Anchored, revocation.ArchiveDeliveryState);
        Assert.Equal(ProjectionGateState.Ready, await GateAsync());
    }

    /// <summary>Runs one D+ pass immediately before the first execution-record write (the step-6 boundary of EX-1); everything else is delegated.</summary>
    private sealed class PassBeforeExecutionArchive(InMemorySecurityEventArchive inner, Func<Task<int>> pass) : ISecurityEventArchive, ISecurityEventArchiveInventory, IArchiveMetaRecords
    {
        public int PassesRun { get; private set; }

        public string ArchiveName => inner.ArchiveName;

        public Task<ArchiveReceipt> AppendAsync(Guid organizationId, string streamName, long streamRevision, ReadOnlyMemory<byte> eventBody, byte[] expectedDigest, CancellationToken cancellationToken) =>
            inner.AppendAsync(organizationId, streamName, streamRevision, eventBody, expectedDigest, cancellationToken);

        public Task<(bool Ready, string Detail)> ProbeAsync(CancellationToken cancellationToken) => inner.ProbeAsync(cancellationToken);

        public Task<byte[]?> ReadAsync(Guid organizationId, string streamName, long streamRevision, CancellationToken cancellationToken) => inner.ReadAsync(organizationId, streamName, streamRevision, cancellationToken);

        public Task<ArchiveIdentityRead> ReadIdentityAsync(CancellationToken cancellationToken) => inner.ReadIdentityAsync(cancellationToken);

        public Task<ArchiveIdentityCreateResult> CreateIdentityAsync(ArchiveIdentity identity, CancellationToken cancellationToken) => inner.CreateIdentityAsync(identity, cancellationToken);

        public Task<StoredObjectRead> ReadEntryAsync(Guid organizationId, string streamName, long streamRevision, CancellationToken cancellationToken) => inner.ReadEntryAsync(organizationId, streamName, streamRevision, cancellationToken);

        public Task<ArchiveListingPage> ListEntriesAsync(Guid organizationId, string? streamName, string? continuationToken, int pageSizeHint, CancellationToken cancellationToken) =>
            inner.ListEntriesAsync(organizationId, streamName, continuationToken, pageSizeHint, cancellationToken);

        public Task<ArchiveListingPage> ListEntriesAboveAsync(Guid organizationId, string streamName, long afterRevision, string? continuationToken, int pageSizeHint, CancellationToken cancellationToken) =>
            inner.ListEntriesAboveAsync(organizationId, streamName, afterRevision, continuationToken, pageSizeHint, cancellationToken);

        public Task<ArchiveContentSummary> SummarizeAsync(CancellationToken cancellationToken) => inner.SummarizeAsync(cancellationToken);

        public Task<CheckpointCreateResult> CreateStopMarkerAsync(Guid organizationId, string streamName, long failedRevision, ReadOnlyMemory<byte> markerBytes, CancellationToken cancellationToken) =>
            inner.CreateStopMarkerAsync(organizationId, streamName, failedRevision, markerBytes, cancellationToken);

        public Task<IReadOnlyList<ArchiveMetaObject>> ReadStopMarkersAsync(Guid organizationId, string streamName, CancellationToken cancellationToken) => inner.ReadStopMarkersAsync(organizationId, streamName, cancellationToken);

        public Task<IReadOnlyList<string>> ListMetaObjectNamesAsync(CancellationToken cancellationToken) => inner.ListMetaObjectNamesAsync(cancellationToken);

        public async Task<CheckpointCreateResult> CreateMetaAsync(string metaObjectName, ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken)
        {
            if (metaObjectName.Contains("/executions/", StringComparison.Ordinal) && PassesRun == 0)
            {
                PassesRun++;
                await pass();
            }

            return await inner.CreateMetaAsync(metaObjectName, bytes, cancellationToken);
        }

        public Task<StoredObjectRead> ReadMetaAsync(string metaObjectName, CancellationToken cancellationToken) => inner.ReadMetaAsync(metaObjectName, cancellationToken);

        public Task<IReadOnlyList<ArchiveMetaObject>> ListMetaAsync(string metaPrefix, CancellationToken cancellationToken) => inner.ListMetaAsync(metaPrefix, cancellationToken);
    }

    /// <summary>
    /// E-6 remediated (C-158, A-7 §27 GT-1..GT-5): forged AuthorityDb delivery state never reopens the gate. Before G08 the same attack reopened it
    /// (`AuthorizationStreamGateDPlusTests.KnownLimitE6…`, evidence in docs/evidence/sec-bl-01-g08/before/). Forged: the Anchored state, the receipt
    /// reference and digest, the archived-on time, the enforcement mode, and the organization's revision and head digest (a forged contiguous
    /// position). The gate reads the head from the journal and needs a trusted receipt, which only the archive and the two stores can provide.
    /// </summary>
    [Fact]
    public async Task E6ForgedDeliveryStateReceiptFieldsModeAndPositionNeverReopenTheGate()
    {
        var (grant, revocation) = await StoppedAfterGrantAsync();
        var second = Command("RelationshipRevoked", "carol");
        foreach (var row in Rows().Where(r => r.ArchiveDeliveryState != ArchiveDeliveryStates.Anchored))
        {
            row.ArchiveDeliveryState = ArchiveDeliveryStates.Anchored; // J1 forges delivery
            row.ArchiveReceiptReference = "forged-receipt-" + row.JournalStreamRevision;
            row.ArchiveReceiptDigest = System.Security.Cryptography.SHA256.HashData("forged"u8);
            row.JournalArchivedOn = world.Clock.GetUtcNow();
            row.LastArchiveErrorCode = null;
        }

        Assert.Equal(0, Rows().Count(r => r.ArchiveDeliveryState != ArchiveDeliveryStates.Anchored)); // nothing "pending" any more
        Assert.Equal(ProjectionGateState.AwaitingArchive, await GateAsync());

        var state = world.Organizations[org];
        state.CheckpointEnforcementModeName = Domain.Features.Organizations.CheckpointEnforcementModes.Asynchronous; // J1 tries to waive the archive
        Assert.Equal(ProjectionGateState.AwaitingArchive, await GateAsync());

        state.AuthorizationRevision = grant.JournalStreamRevision; // J1 forges the contiguous position back to the delivered grant
        state.ProjectionAppliedRevision = grant.JournalStreamRevision;
        state.LatestSecurityJournalDigest = grant.JournalEventDigest;
        Assert.Equal(ProjectionGateState.AwaitingArchive, await GateAsync()); // the head comes from the journal, not the head columns

        Assert.Equal(0, await PassAsync()); // and D+ delivers nothing either
        Assert.Null(await archive.ReadAsync(org, Authorization, revocation.JournalStreamRevision, Ct));
        Assert.Null(await archive.ReadAsync(org, Authorization, second.JournalStreamRevision, Ct));
    }

    /// <summary>An archive that fails the append of one revision once (an outage), forwarding everything else.</summary>
    public class FlakyArchive : System.Reflection.DispatchProxy
    {
        private InMemorySecurityEventArchive inner = null!;
        private long failAt;
        private int failed;

        public static ICountedArchive Wrap(InMemorySecurityEventArchive inner, long failAppendAt)
        {
            var proxy = Create<ICountedArchive, FlakyArchive>();
            var self = (FlakyArchive)(object)proxy;
            self.inner = inner;
            self.failAt = failAppendAt;
            return proxy;
        }

        protected override object? Invoke(System.Reflection.MethodInfo? targetMethod, object?[]? args)
        {
            ArgumentNullException.ThrowIfNull(targetMethod);
            if (targetMethod.Name == nameof(ISecurityEventArchive.AppendAsync) && (long)args![2]! == failAt && Interlocked.Exchange(ref failed, 1) == 0)
            {
                return Task.FromException<ArchiveReceipt>(new ArchiveUnavailableException("injected append outage"));
            }

            try
            {
                return targetMethod.Invoke(inner, args);
            }
            catch (System.Reflection.TargetInvocationException ex) when (ex.InnerException is not null)
            {
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
                throw;
            }
        }
    }
}
