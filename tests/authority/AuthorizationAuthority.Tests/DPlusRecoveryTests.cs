using System.Security.Cryptography;
using System.Text;
using AuthorizationAuthority.Application.Common.Ports;
using AuthorizationAuthority.Application.Features.Journal;
using AuthorizationAuthority.Application.Features.Journal.Reconciliation;
using AuthorizationAuthority.Application.Features.Journal.Recovery;
using AuthorizationAuthority.Application.Features.Journal.Verification;
using AuthorizationAuthority.Domain.Features.Journal;
using AuthorizationAuthority.Infrastructure.Archive;
using Xunit;

namespace AuthorizationAuthority.Tests;

/// <summary>
/// SEC-BL-01 G14-R2 (contract amendment A-4, §24): controlled audit-stream recovery in Development/Testing — signed two-approver records outside
/// AuthorityDb, the D+ lift on every pass, stop generations, SR-A and SR-C1, archive-instance pinning and the W2-S evidence pre-commitment.
/// Test identifiers SRT-xx refer to <c>docs/security/G14_R1_TEST_AND_ACCEPTANCE_MATRIX.md</c>. In-process layer (IP): synthetic approver keys
/// generated per test, not two-human evidence.
/// </summary>
public abstract class DPlusRecoveryTests
{
    /// <summary>SEC-BL-01 WP-5b (A-6 §26): the D+-enabled stream this run of the suite exercises (one concrete class per stream).</summary>
    protected abstract string Stream { get; }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string Audit => Stream;

    private const string Requester = "requester-r";

    private const string Analyst = "analyst-q";

    /// <summary>One organization with genuine audit history at revision 40, an in-memory archive, the verification stores and three synthetic approvers.</summary>
    private sealed class Rig : IDisposable
    {
        public Rig(string stream, bool archivePredecessor = false, bool approversOperational = true)
        {
            S = stream;
            World = new CapabilityRefusalEvidenceWorld();
            Org = World.AddOrganization();
            World.AddStreamHistory(Org, S); // WP-5b: genuine revision-40 history of the stream under test (audit: unchanged)
            Approvers = new SyntheticRecoveryApprovers("approver-a", "approver-b", "approver-c");
            if (approversOperational)
            {
                World.DPlus.Approvals = Approvers.Verifier;
            }

            Instance = World.DPlus.ProvisionAsync(Archive, World.Clock.GetUtcNow()).GetAwaiter().GetResult();
            if (archivePredecessor)
            {
                RestorePredecessorAsync().GetAwaiter().GetResult();
            }

            Processor = NewProcessor();
        }

        /// <summary>SEC-BL-01 WP-5b: the D+-enabled stream under test.</summary>
        public string S { get; }

        public CapabilityRefusalEvidenceWorld World { get; }

        public Guid Org { get; }

        public string Instance { get; }

        public InMemorySecurityEventArchive Archive { get; } = new();

        public ToggleFindingStore Findings { get; } = new();

        public RecordingAlerts Alerts { get; } = new();

        public SyntheticRecoveryApprovers Approvers { get; }

        public ArchiveProcessor Processor { get; private set; }

        public DPlusStreamRecoveryService Service => new(World.Store, Archive, Findings, World.DPlus.Durability, World.Clock);

        public SecurityJournalEntry Predecessor => World.Predecessor(Org, S);

        public byte[] PredecessorBytes => Encoding.UTF8.GetBytes(Predecessor.JournalEventBodyJson);

        public ArchiveProcessor NewProcessor() => new(World.Store, Archive, World.Clock, Alerts, Findings, World.DPlus.Durability);

        /// <summary>A restart: a new processor (its in-memory state is gone).</summary>
        public void Restart() => Processor = NewProcessor();

        public Task<int> PassAsync() => Processor.ProcessOrganizationAsync(Org, Ct);

        public SecurityJournalEntry Append(string note = "event")
        {
            World.Clock.Advance(TimeSpan.FromSeconds(1));
            return World.SeedStream(Org, S, "TestAuditEvent", new { note });
        }

        public Task<ArchiveReceipt> RestorePredecessorAsync() => Archive.AppendAsync(Org, S, 40, PredecessorBytes, Predecessor.JournalEventDigest, Ct);

        public string ArchiveKey(long revision) => $"{Org:D}/{S}/{revision}";

        public string MarkerName(long revision, int generation = 0) => DPlusStreamStops.MarkerObjectName(Org, S, revision, generation);

        public IReadOnlyList<string> Markers() => [.. DPlusRecordInspection.ArchiveNames(Archive, "meta/dplus-stops/")];

        /// <summary>A missing-predecessor stop at revision 41 (generation 0): marker, finding and W2-S commitment.</summary>
        public async Task<SecurityJournalEntry> StopAtMissingPredecessorAsync()
        {
            var entry = Append();
            Assert.Equal(0, await PassAsync());
            Assert.Equal((ArchiveDeliveryStates.VerificationFailed, "DPLUS_" + JournalFindingCodes.PredecessorMissing), (entry.ArchiveDeliveryState, entry.LastArchiveErrorCode));
            return entry;
        }

        /// <summary>A trusted checkpoint chain whose head verifies revision 40 with <paramref name="digest"/> in <paramref name="instance"/>.</summary>
        public Task<CheckpointCreateResult> AnchorAsync(byte[]? digest = null, long verifiedRevision = 40, string? instance = null)
        {
            var checkpoint = DPlusRecordInspection.FirstCheckpoint(Org, S, instance ?? Instance, verifiedRevision, digest ?? SHA256.HashData(PredecessorBytes), World.Clock.GetUtcNow());
            return World.DPlus.Checkpoints.CreateAsync(Org, S, 1, JournalVerificationCheckpoints.Serialize(checkpoint), Ct);
        }

        public async Task<DPlusRecoveryCommandResult> ProposeAsync(string strategy = DPlusRecoveryStrategies.FalseAlarm, string? cause = null, byte[]? restore = null, string requester = Requester,
            string analyst = Analyst, bool containment = false, TimeSpan? window = null)
        {
            cause ??= strategy == DPlusRecoveryStrategies.FalseAlarm ? "DevelopmentReset" : DPlusRecoveryStrategies.PredecessorLostCause;
            return await Service.ProposeAsync(new DPlusRecoveryProposal(Org, S, strategy, requester, "reason: test recovery", cause, containment, containment ? "credentials rotated" : null,
                analyst, "investigation narrative", window ?? TimeSpan.FromHours(1), restore, "test-commit"), Ct);
        }

        public async Task<Guid> ProposedAsync(string strategy = DPlusRecoveryStrategies.FalseAlarm, byte[]? restore = null)
        {
            var result = await ProposeAsync(strategy, restore: restore);
            Assert.True(result.Outcome == DPlusRecoveryCommandOutcome.Done, result.Code + ": " + result.Detail);
            return result.RecoveryId!.Value;
        }

        public DPlusRecoveryStatement Statement(Guid recoveryId, out string sha)
        {
            var bytes = World.DPlus.Records.Records[DPlusRecoveryNames.StoreRecoveryPrefix + DPlusRecoveryNames.Statement(Org, S, recoveryId)];
            sha = DPlusRecoveryCodec.Sha256Hex(bytes);
            return DPlusRecoveryCodec.TryParse<DPlusRecoveryStatement>(bytes)!;
        }

        public string Sign(Guid recoveryId, string approver, string decision = DPlusRecoveryClaims.Approve, string kind = DPlusRecoveryClaims.RecoveryKind, DateTimeOffset? at = null,
            TimeSpan? lifetime = null, Guid? jti = null, IReadOnlyDictionary<string, object?>? overrides = null)
        {
            var statement = Statement(recoveryId, out var sha);
            return Approvers.Sign(approver, SyntheticRecoveryApprovers.Binding(statement, sha), at ?? World.Clock.GetUtcNow(), decision, kind, lifetime, jti, overrides);
        }

        public async Task<DPlusRecoveryCommandResult> DecideAsync(Guid recoveryId, string token) => await Service.RecordDecisionAsync(Org, S, recoveryId, token, Ct);

        public async Task ApproveAsync(Guid recoveryId, params string[] approvers)
        {
            foreach (var approver in approvers)
            {
                var result = await DecideAsync(recoveryId, Sign(recoveryId, approver));
                Assert.True(result.Outcome == DPlusRecoveryCommandOutcome.Done, result.Code + ": " + result.Detail);
            }
        }

        public async Task<DPlusRecoveryCommandResult> ExecuteAsync(Guid recoveryId) => await Service.ExecuteAsync(Org, S, recoveryId, "executor-e", Ct);

        public async Task<Guid> ExecutedAsync(string strategy = DPlusRecoveryStrategies.FalseAlarm, byte[]? restore = null)
        {
            var id = await ProposedAsync(strategy, restore);
            await ApproveAsync(id, "approver-a", "approver-b");
            var executed = await ExecuteAsync(id);
            Assert.True(executed.Outcome == DPlusRecoveryCommandOutcome.Done, executed.Code + ": " + executed.Detail);
            return id;
        }

        public bool FindingExists(string code) => Findings.Records.Values.Any(r => Encoding.UTF8.GetString(r).Contains("\"finding_code\":\"" + code + "\"", StringComparison.Ordinal));

        public void Dispose() => Approvers.Dispose();
    }

    /// <summary>An in-memory finding store whose writes can be failed (a store outage).</summary>
    private sealed class ToggleFindingStore : IJournalVerificationFindingStore
    {
        private readonly InMemoryJournalVerificationFindingStore inner = new();

        public bool RecordAvailable { get; set; } = true;

        public IReadOnlyDictionary<string, byte[]> Records => inner.Records;

        public bool RemoveForTesting(Guid organizationId, string streamName, string findingIdentity) => inner.RemoveForTesting(organizationId, streamName, findingIdentity);

        public Task<FindingRecordStatus> RecordAsync(Guid organizationId, string streamName, string findingIdentity, ReadOnlyMemory<byte> findingBytes, CancellationToken cancellationToken) =>
            RecordAvailable ? inner.RecordAsync(organizationId, streamName, findingIdentity, findingBytes, cancellationToken) : throw new CheckpointStoreUnavailableException("finding store down");

        public Task<IReadOnlyList<byte[]>> ListAsync(Guid organizationId, string streamName, CancellationToken cancellationToken) => inner.ListAsync(organizationId, streamName, cancellationToken);
    }

    private static Dictionary<string, byte[]> Snapshot(Rig rig) =>
        rig.Archive.MetaForTesting.Concat(rig.Findings.Records).Concat(rig.World.DPlus.Records.Records).ToDictionary(p => p.Key, p => p.Value.ToArray(), StringComparer.Ordinal);

    private static void AssertPreserved(Dictionary<string, byte[]> before, Rig rig)
    {
        var after = Snapshot(rig);
        foreach (var (name, bytes) in before)
        {
            Assert.True(after.TryGetValue(name, out var now), "removed: " + name);
            Assert.True(bytes.AsSpan().SequenceEqual(now), "changed: " + name);
        }
    }

    // ================================================================ Authorization

    /// <summary>SRT-01 (SR-A): a missing-predecessor stop whose predecessor later reappears unchanged (a triaged Development reset). D+ alone keeps the stop; an authorized SR-A lifts it.</summary>
    [Fact]
    public async Task Srt01AnAuthorizedFalseAlarmRecoveryDeliversTheRevisionAndPreservesTheStopRecords()
    {
        using var rig = new Rig(Stream);
        var entry = await rig.StopAtMissingPredecessorAsync();
        await rig.RestorePredecessorAsync(); // the evidence is now fine, but A-3 never resumes by itself
        Assert.Equal(0, await rig.PassAsync());
        var before = Snapshot(rig);

        var id = await rig.ExecutedAsync();
        Assert.Equal(ArchiveDeliveryStates.Pending, entry.ArchiveDeliveryState); // the scheduling hint
        Assert.Equal(1, await rig.PassAsync());
        Assert.Equal(ArchiveDeliveryStates.Anchored, entry.ArchiveDeliveryState);
        Assert.Equal("Executed", (await rig.Service.StatusAsync(rig.Org, Audit, id, Ct))!.State);
        Assert.Equal("COMPLETED", (await rig.Service.RecordOutcomeAsync(rig.Org, Audit, id, Ct)).Code);
        AssertPreserved(before, rig); // every original marker, finding and record is byte-identical
        Assert.Contains(rig.MarkerName(41), rig.Markers());
        Assert.True(rig.Archive.MetaForTesting.ContainsKey(DPlusRecoveryNames.ArchiveRecoveryPrefix + DPlusRecoveryNames.RowCopy(rig.Org, Audit, id)));

        var next = rig.Append();
        Assert.Equal(1, await rig.PassAsync()); // normal D+ continues after the recovered revision
        Assert.Equal(ArchiveDeliveryStates.Anchored, next.ArchiveDeliveryState);
    }

    /// <summary>SRT-02 (SR-C1): the absent predecessor is restored create-only from a copy equal to the trusted checkpoint digest at v = k − 1.</summary>
    [Fact]
    public async Task Srt02AnAuthorizedAnchoredRestoreCreatesThePredecessorAndDeliversTheRevision()
    {
        using var rig = new Rig(Stream);
        await rig.AnchorAsync();
        var entry = await rig.StopAtMissingPredecessorAsync();
        var id = await rig.ExecutedAsync(DPlusRecoveryStrategies.RestorePredecessor, rig.PredecessorBytes);
        Assert.Equal(rig.PredecessorBytes, await rig.Archive.ReadAsync(rig.Org, Audit, 40, Ct)); // created create-only
        Assert.Equal(1, await rig.PassAsync());
        Assert.Equal(ArchiveDeliveryStates.Anchored, entry.ArchiveDeliveryState);
        Assert.Equal("COMPLETED", (await rig.Service.RecordOutcomeAsync(rig.Org, Audit, id, Ct)).Code);
        Assert.NotNull(DPlusRecordInspection.Parse<DPlusRecoveryAction>(rig.World.DPlus.Records.Records[DPlusRecoveryNames.StoreRecoveryPrefix + DPlusRecoveryNames.Action(rig.Org, Audit, id)]));
    }

    /// <summary>SRT-03: an execution record without a valid statement, and a statement without approvals, lift nothing.</summary>
    [Fact]
    public async Task Srt03AnExecutionRecordOrAStatementAloneLiftsNothing()
    {
        using var rig = new Rig(Stream);
        var entry = await rig.StopAtMissingPredecessorAsync();
        await rig.RestorePredecessorAsync();
        var id = await rig.ProposedAsync(); // statement, no approvals
        var statement = rig.Statement(id, out var sha);
        var forged = new DPlusRecoveryExecution(DPlusRecoveryExecution.SchemaV1, id, rig.Org, Audit, 41, 0, sha, statement.StopSetSha256Hex, "forger", "x");
        rig.World.DPlus.Records.PutForTesting(DPlusRecoveryNames.StoreRecoveryPrefix + DPlusRecoveryNames.Execution(rig.Org, Audit, statement.StopSetSha256Hex), DPlusRecoveryCodec.Serialize(forged));
        entry.ArchiveDeliveryState = ArchiveDeliveryStates.Pending; // even with a forged hint
        Assert.Equal(0, await rig.PassAsync());
        Assert.Null(await rig.Archive.ReadAsync(rig.Org, Audit, 41, Ct));

        // An execution record naming no statement at all is reported as invalid.
        rig.Append(); // pending behind the stop, so discovery runs
        rig.World.DPlus.Records.PutForTesting(DPlusRecoveryNames.StoreRecoveryPrefix + DPlusRecoveryNames.Execution(rig.Org, Audit, new string('a', 64)),
            DPlusRecoveryCodec.Serialize(forged with { RecoveryId = Guid.CreateVersion7(), StopSetSha256Hex = new string('a', 64) }));
        Assert.Equal(0, await rig.PassAsync());
        Assert.True(rig.FindingExists(JournalFindingCodes.RecoveryRecordInvalid));
        Assert.Null(await rig.Archive.ReadAsync(rig.Org, Audit, 41, Ct));
    }

    /// <summary>SRT-04: one approval; two approvals with the same kid; an approval by an unconfigured key.</summary>
    [Fact]
    public async Task Srt04OneApprovalOrTwoFromTheSameApproverOrAnUnconfiguredKeyIsNotAuthorized()
    {
        using var rig = new Rig(Stream);
        await rig.StopAtMissingPredecessorAsync();
        await rig.RestorePredecessorAsync();
        var id = await rig.ProposedAsync();
        await rig.ApproveAsync(id, "approver-a");
        Assert.Equal(DPlusRecoveryRefusals.NotAuthorized, (await rig.ExecuteAsync(id)).Code);
        await rig.ApproveAsync(id, "approver-a"); // a second, different jti from the same approver
        Assert.Equal(DPlusRecoveryRefusals.NotAuthorized, (await rig.ExecuteAsync(id)).Code);

        using var stranger = new SyntheticRecoveryApprovers("approver-x", "approver-y");
        var statement = rig.Statement(id, out var sha);
        var foreign = stranger.Sign("approver-x", SyntheticRecoveryApprovers.Binding(statement, sha), rig.World.Clock.GetUtcNow());
        Assert.Equal(DPlusRecoveryRefusals.DecisionInvalid, (await rig.DecideAsync(id, foreign)).Code);
        var impostor = rig.Approvers.Sign("approver-b", SyntheticRecoveryApprovers.Binding(statement, sha), rig.World.Clock.GetUtcNow(), signWithKeyOf: "approver-a"); // kid b, key a
        Assert.Equal(DPlusRecoveryRefusals.DecisionInvalid, (await rig.DecideAsync(id, impostor)).Code);
        Assert.Equal(DPlusRecoveryRefusals.NotAuthorized, (await rig.ExecuteAsync(id)).Code);
        Assert.Equal(0, await rig.PassAsync());
    }

    /// <summary>SRT-05: an approval over a different statement digest (the statement edited after signing) does not authorize; the edited statement is invalid.</summary>
    [Fact]
    public async Task Srt05AnEditedStatementOrAnApprovalOverAnotherDigestIsNotAuthorized()
    {
        using var rig = new Rig(Stream);
        await rig.StopAtMissingPredecessorAsync();
        await rig.RestorePredecessorAsync();
        var id = await rig.ProposedAsync();
        Assert.Equal(DPlusRecoveryRefusals.DecisionInvalid, (await rig.DecideAsync(id, rig.Sign(id, "approver-a", overrides: new Dictionary<string, object?> { ["statement_sha256"] = new string('0', 64) }))).Code);
        await rig.ApproveAsync(id, "approver-a", "approver-b");

        // J6 edits the statement in both stores after signing (a wider window): the approvals no longer bind it.
        var statement = rig.Statement(id, out _);
        var edited = DPlusRecoveryCodec.Serialize(statement with { NotAfterUtc = statement.NotAfterUtc.AddHours(1) });
        var name = DPlusRecoveryNames.Statement(rig.Org, Audit, id);
        rig.World.DPlus.Records.PutForTesting(DPlusRecoveryNames.StoreRecoveryPrefix + name, edited);
        rig.Archive.PutMetaForTesting(DPlusRecoveryNames.ArchiveRecoveryPrefix + name, edited);
        Assert.Equal(DPlusRecoveryRefusals.NotAuthorized, (await rig.ExecuteAsync(id)).Code);
        rig.Append(); // pending behind the stop, so discovery runs
        Assert.Equal(0, await rig.PassAsync());
        Assert.True(rig.FindingExists(JournalFindingCodes.RecoveryRecordInvalid)); // the stored decisions no longer verify against it
    }

    /// <summary>SRT-06: a lifetime above the bound is refused; execution after the approvals expired, or after the window, is refused.</summary>
    [Fact]
    public async Task Srt06ExpiredApprovalsAndAClosedWindowAreRefused()
    {
        using var rig = new Rig(Stream);
        await rig.StopAtMissingPredecessorAsync();
        await rig.RestorePredecessorAsync();
        var id = await rig.ProposedAsync();
        Assert.Equal(DPlusRecoveryRefusals.DecisionInvalid, (await rig.DecideAsync(id, rig.Sign(id, "approver-a", lifetime: TimeSpan.FromSeconds(901)))).Code);
        await rig.ApproveAsync(id, "approver-a", "approver-b");
        rig.World.Clock.Advance(TimeSpan.FromMinutes(20)); // approvals (10 min) expired; window (1 h) still open
        Assert.Equal(DPlusRecoveryRefusals.ApprovalsNotFresh, (await rig.ExecuteAsync(id)).Code);
        await rig.ApproveAsync(id, "approver-a", "approver-c"); // fresh approvals within the window
        rig.World.Clock.Advance(TimeSpan.FromMinutes(45)); // the window has closed
        Assert.Equal(DPlusRecoveryRefusals.WindowExpired, (await rig.ExecuteAsync(id)).Code);
        Assert.Equal("Failed", (await rig.Service.StatusAsync(rig.Org, Audit, id, Ct))!.State);
        Assert.Equal(DPlusRecoveryRefusals.Terminal, (await rig.ExecuteAsync(id)).Code); // terminal
        Assert.Equal(0, await rig.PassAsync());
    }

    /// <summary>SRT-07: a signed rejection, or a signed cancellation, before execution makes the statement permanently non-executable.</summary>
    [Theory]
    [InlineData(DPlusRecoveryClaims.Reject, DPlusRecoveryClaims.RecoveryKind, DPlusRecoveryRefusals.Rejected)]
    [InlineData(DPlusRecoveryClaims.Cancel, DPlusRecoveryClaims.CancellationKind, DPlusRecoveryRefusals.Cancelled)]
    public async Task Srt07ARejectionOrCancellationIsFinal(string decision, string kind, string code)
    {
        using var rig = new Rig(Stream);
        await rig.StopAtMissingPredecessorAsync();
        await rig.RestorePredecessorAsync();
        var id = await rig.ProposedAsync();
        await rig.ApproveAsync(id, "approver-a", "approver-b");
        Assert.Equal(DPlusRecoveryCommandOutcome.Done, (await rig.DecideAsync(id, rig.Sign(id, "approver-c", decision, kind))).Outcome);
        Assert.Equal(code, (await rig.ExecuteAsync(id)).Code);
        Assert.Equal(0, await rig.PassAsync());

        // Even an execution record written by a store actor lifts nothing while the decision survives (SR-MV-13).
        var statement = rig.Statement(id, out var sha);
        rig.World.DPlus.Records.PutForTesting(DPlusRecoveryNames.StoreRecoveryPrefix + DPlusRecoveryNames.Execution(rig.Org, Audit, statement.StopSetSha256Hex),
            DPlusRecoveryCodec.Serialize(new DPlusRecoveryExecution(DPlusRecoveryExecution.SchemaV1, id, rig.Org, Audit, 41, 0, sha, statement.StopSetSha256Hex, "forger", "x")));
        Assert.Equal(0, await rig.PassAsync());
        Assert.Null(await rig.Archive.ReadAsync(rig.Org, Audit, 41, Ct));
    }

    /// <summary>SRT-08: the requester or the analyst cannot approve; a requester that is an approver id cannot propose.</summary>
    [Fact]
    public async Task Srt08SeparationOfDutiesIsEnforced()
    {
        using var rig = new Rig(Stream);
        await rig.StopAtMissingPredecessorAsync();
        await rig.RestorePredecessorAsync();
        Assert.Equal(DPlusRecoveryRefusals.SeparationOfDuties, (await rig.ProposeAsync(requester: "approver-a")).Code);
        Assert.Equal(DPlusRecoveryRefusals.SeparationOfDuties, (await rig.ProposeAsync(analyst: "approver-b")).Code);
        var id = await rig.ProposedAsync();
        Assert.Empty(DPlusRecoveryAuthorization.Evaluate([], Requester, Analyst).Approvals);

        // A decision whose approver id equals the requester label is never counted (statement crafted to name an approver as requester).
        var decisions = new[]
        {
            new DPlusVerifiedDecision("approver-a", Guid.CreateVersion7(), DPlusRecoveryClaims.RecoveryKind, DPlusRecoveryClaims.Approve, rig.World.Clock.GetUtcNow(), rig.World.Clock.GetUtcNow(), rig.World.Clock.GetUtcNow().AddMinutes(5), "x"),
            new DPlusVerifiedDecision("approver-b", Guid.CreateVersion7(), DPlusRecoveryClaims.RecoveryKind, DPlusRecoveryClaims.Approve, rig.World.Clock.GetUtcNow(), rig.World.Clock.GetUtcNow(), rig.World.Clock.GetUtcNow().AddMinutes(5), "y"),
        };
        Assert.False(DPlusRecoveryAuthorization.Evaluate(decisions, "approver-a", Analyst).Authorized);
        Assert.False(DPlusRecoveryAuthorization.Evaluate(decisions, Requester, "approver-b").Authorized);
        Assert.True(DPlusRecoveryAuthorization.Evaluate(decisions, Requester, Analyst).Authorized);
        Assert.NotEqual(Guid.Empty, id);
    }

    /// <summary>SRT-09 / SR-MV-15: Unavailable (default) refuses every proposal and execution; the synthetic mode is refused outside Development/Testing.</summary>
    [Fact]
    public async Task Srt09UnavailableModeRefusesAndSyntheticModeIsRefusedOutsideDevelopment()
    {
        using var rig = new Rig(Stream, approversOperational: false);
        await rig.StopAtMissingPredecessorAsync();
        await rig.RestorePredecessorAsync();
        Assert.Equal(DPlusRecoveryRefusals.DualControlUnavailable, (await rig.ProposeAsync()).Code);
        Assert.Equal(DPlusRecoveryRefusals.DualControlUnavailable, (await rig.Service.ExecuteAsync(rig.Org, Audit, Guid.CreateVersion7(), "e", Ct)).Code);
        Assert.Same(DPlusRecoveryApprovalVerifier.Unavailable, DPlusRecoveryApprovalVerifier.Create(null, "Production"));
        foreach (var environment in new[] { "Production", "Staging" })
        {
            Assert.Throws<InvalidOperationException>(() => DPlusRecoveryApprovalVerifier.Create(rig.Approvers.Options, environment));
        }

        Assert.Throws<InvalidOperationException>(() => DPlusRecoveryApprovalVerifier.Create(new JournalRecoveryOptions { Mode = "Production" }, "Development"));
        Assert.Throws<InvalidOperationException>(() => DPlusRecoveryApprovalVerifier.Create(rig.Approvers.Options, "Testing", [rig.Approvers.Options.Approvers[0].PublicKeyBase64!])); // shared with OperatorApprovals
        Assert.Equal(0, await rig.PassAsync());
    }

    /// <summary>SRT-10 / SR-MV-02: removing an approver key after a completed recovery re-stops the stream (signatures are re-verified on every pass).</summary>
    [Fact]
    public async Task Srt10RemovingAnApproverKeyAfterRecoveryReStopsTheStream()
    {
        using var rig = new Rig(Stream);
        await rig.StopAtMissingPredecessorAsync();
        await rig.RestorePredecessorAsync();
        await rig.ExecutedAsync();
        Assert.Equal(1, await rig.PassAsync());
        var next = rig.Append();
        rig.World.DPlus.Approvals = rig.Approvers.VerifierWith(["approver-a", "approver-c"]); // approver-b removed (compromise response)
        rig.Restart();
        Assert.Equal(0, await rig.PassAsync());
        Assert.Equal(ArchiveDeliveryStates.Pending, next.ArchiveDeliveryState);
        Assert.Contains(rig.Alerts.Raised, a => a.Summary.Contains(JournalFindingCodes.RecoveryRecordInvalid, StringComparison.Ordinal));
    }

    /// <summary>SRT-11: a retired key keeps its earlier approvals valid; a new approval by it is refused.</summary>
    [Fact]
    public async Task Srt11ARetiredKeyStillVerifiesEarlierApprovalsButNoNewOnes()
    {
        using var rig = new Rig(Stream);
        await rig.StopAtMissingPredecessorAsync();
        await rig.RestorePredecessorAsync();
        await rig.ExecutedAsync();
        var retiredAt = rig.World.Clock.GetUtcNow().AddMinutes(1);
        rig.World.Clock.Advance(TimeSpan.FromMinutes(2));
        var retired = new Dictionary<string, DateTimeOffset> { ["approver-b"] = DateTimeOffset.FromUnixTimeSeconds(retiredAt.ToUnixTimeSeconds()) };
        rig.World.DPlus.Approvals = rig.Approvers.VerifierWith(["approver-a", "approver-b", "approver-c"], retired);
        rig.Restart();
        Assert.Equal(1, await rig.PassAsync()); // the lift stays

        // A new stop and a new proposal: approver-b's new approval is refused.
        var next = rig.Append();
        Assert.Equal(1, await rig.PassAsync());
        Assert.Equal(ArchiveDeliveryStates.Anchored, next.ArchiveDeliveryState);
        var late = rig.Approvers.Sign("approver-b", new DPlusRecoveryBinding(Guid.CreateVersion7(), rig.Org, Audit, 43, 1, new string('1', 64)), rig.World.Clock.GetUtcNow());
        Assert.Null(rig.World.DPlus.Approvals.Verify(late, BindingOf(late), out var reason));
        Assert.Contains("retired", reason, StringComparison.Ordinal);
        var early = rig.Approvers.Sign("approver-b", BindingOf(late), retiredAt.AddMinutes(-1));
        Assert.NotNull(rig.World.DPlus.Approvals.Verify(early, BindingOf(late), out _));
    }

    private static DPlusRecoveryBinding BindingOf(string token)
    {
        var claims = System.Text.Json.JsonDocument.Parse(System.Buffers.Text.Base64Url.DecodeFromChars(token.Split('.')[1])).RootElement;
        return new DPlusRecoveryBinding(Guid.Parse(claims.GetProperty("recovery_id").GetString()!), Guid.Parse(claims.GetProperty("organization_id").GetString()!),
            claims.GetProperty("stream_name").GetString()!, claims.GetProperty("failed_revision").GetInt64(), claims.GetProperty("stop_generation").GetInt32(), claims.GetProperty("statement_sha256").GetString()!);
    }

    // ================================================================ Replay and scope

    /// <summary>SRT-12 / SR-MV-04: records and approvals of organization A placed under organization B (or bound to another revision) resolve nothing there.</summary>
    [Fact]
    public async Task Srt12RecordsCopiedToAnotherOrganizationOrRevisionResolveNothing()
    {
        using var rig = new Rig(Stream);
        var other = rig.World.AddOrganization();
        rig.World.AddStreamHistory(other, Audit);
        await rig.StopAtMissingPredecessorAsync();
        var otherEntry = rig.World.SeedStream(other, Audit, "TestAuditEvent", new { note = "b" });
        Assert.Equal(0, await rig.Processor.ProcessOrganizationAsync(other, Ct)); // B is stopped too (its predecessor 40 is not archived)
        await rig.RestorePredecessorAsync();
        var id = await rig.ExecutedAsync();

        // Copy every recovery record of A under B's prefix in both stores.
        foreach (var (name, bytes) in rig.World.DPlus.Records.Records.Where(r => r.Key.StartsWith(DPlusRecoveryNames.StoreRecoveryPrefix + rig.Org.ToString("D"), StringComparison.Ordinal)).ToList())
        {
            rig.World.DPlus.Records.PutForTesting(name.Replace(rig.Org.ToString("D"), other.ToString("D"), StringComparison.Ordinal), bytes);
        }

        var predecessor = rig.World.Predecessor(other, Audit);
        await rig.Archive.AppendAsync(other, Audit, 40, Encoding.UTF8.GetBytes(predecessor.JournalEventBodyJson), predecessor.JournalEventDigest, Ct);
        rig.World.SeedStream(other, Audit, "TestAuditEvent", new { note = "b2" }); // pending behind B's stop, so discovery runs
        Assert.Equal(0, await rig.Processor.ProcessOrganizationAsync(other, Ct));
        Assert.NotEqual(ArchiveDeliveryStates.Anchored, otherEntry.ArchiveDeliveryState);
        Assert.Contains(rig.Findings.Records.Keys, k => k.StartsWith(other.ToString("D"), StringComparison.Ordinal) && Encoding.UTF8.GetString(rig.Findings.Records[k]).Contains(JournalFindingCodes.RecoveryRecordInvalid, StringComparison.Ordinal));

        // A's own recovery still works.
        Assert.Equal(1, await rig.PassAsync());
        Assert.NotEqual(Guid.Empty, id);
    }

    /// <summary>SRT-13: a decision cannot be stored twice under its jti, nor under another name.</summary>
    [Fact]
    public async Task Srt13ADecisionIsSingleUseAndBoundToItsIdentifier()
    {
        using var rig = new Rig(Stream);
        await rig.StopAtMissingPredecessorAsync();
        await rig.RestorePredecessorAsync();
        var id = await rig.ProposedAsync();
        var jti = Guid.CreateVersion7();
        Assert.Equal(DPlusRecoveryCommandOutcome.Done, (await rig.DecideAsync(id, rig.Sign(id, "approver-a", jti: jti))).Outcome);
        Assert.Equal(DPlusRecoveryRefusals.DecisionInvalid, (await rig.DecideAsync(id, rig.Sign(id, "approver-b", jti: jti))).Code); // jti reused

        // A store actor places approver-a's token under another name: it does not count twice and is reported.
        var token = rig.World.DPlus.Records.Records[DPlusRecoveryNames.StoreRecoveryPrefix + DPlusRecoveryNames.Decision(rig.Org, Audit, id, jti)];
        rig.World.DPlus.Records.PutForTesting(DPlusRecoveryNames.StoreRecoveryPrefix + DPlusRecoveryNames.Decision(rig.Org, Audit, id, Guid.CreateVersion7()), token);
        Assert.Equal(DPlusRecoveryRefusals.NotAuthorized, (await rig.ExecuteAsync(id)).Code);
    }

    /// <summary>SRT-14: a second recovery of the same stop set after the first executed conflicts and fails.</summary>
    [Fact]
    public async Task Srt14ASecondRecoveryOfTheSameStopSetFails()
    {
        using var rig = new Rig(Stream);
        await rig.StopAtMissingPredecessorAsync();
        await rig.RestorePredecessorAsync();
        var first = await rig.ProposedAsync();
        var second = await rig.ProposedAsync();
        await rig.ApproveAsync(first, "approver-a", "approver-b");
        await rig.ApproveAsync(second, "approver-b", "approver-c");
        Assert.Equal(DPlusRecoveryCommandOutcome.Done, (await rig.ExecuteAsync(first)).Outcome);
        Assert.Equal(DPlusRecoveryRefusals.ExecutionConflict, (await rig.ExecuteAsync(second)).Code);
        Assert.Equal("Failed", (await rig.Service.StatusAsync(rig.Org, Audit, second, Ct))!.State);
        Assert.Equal(1, await rig.PassAsync());
    }

    /// <summary>SRT-15 / SR-MV-10: a new failure at the recovered revision gets a generation-1 marker and finding; the old authorization does not cover it.</summary>
    [Fact]
    public async Task Srt15ANewFailureAfterRecoveryIsAGenerationOneStop()
    {
        using var rig = new Rig(Stream);
        await rig.AnchorAsync();
        var entry = await rig.StopAtMissingPredecessorAsync();
        await rig.ExecutedAsync(DPlusRecoveryStrategies.RestorePredecessor, rig.PredecessorBytes);
        Assert.True(rig.Archive.RemoveObjectForTesting(rig.ArchiveKey(40))); // J4 deletes the restored predecessor before k is delivered
        Assert.Equal(0, await rig.PassAsync());
        Assert.Contains(rig.MarkerName(41, 1), rig.Markers());
        Assert.Contains(rig.MarkerName(41, 0), rig.Markers());
        var generationOne = DPlusStreamStops.TryParse(rig.Archive.MetaForTesting[rig.MarkerName(41, 1)], rig.Org, Audit, 41, 1)!;
        Assert.Equal(DPlusStreamStop.SchemaV2, generationOne.MarkerSchema);
        Assert.Equal(1, generationOne.StopGeneration);
        Assert.Equal(ArchiveDeliveryStates.VerificationFailed, entry.ArchiveDeliveryState);
        await rig.RestorePredecessorAsync();
        Assert.Equal(0, await rig.PassAsync()); // still stopped: generation 1 is unresolved
    }

    /// <summary>SRT-16: a recovery in organization A does not affect organization B or the other streams.</summary>
    [Fact]
    public async Task Srt16RecoveryIsIsolatedByOrganizationAndStream()
    {
        using var rig = new Rig(Stream);
        var other = rig.World.AddOrganization();
        var predecessor = rig.World.AddStreamHistory(other, Audit);
        await rig.Archive.AppendAsync(other, Audit, 40, Encoding.UTF8.GetBytes(predecessor.JournalEventBodyJson), predecessor.JournalEventDigest, Ct);
        await rig.StopAtMissingPredecessorAsync();
        var otherEntry = rig.World.SeedStream(other, Audit, "TestAuditEvent", new { note = "b" });
        Assert.Equal(1, await rig.Processor.ProcessOrganizationAsync(other, Ct)); // B unaffected by A's stop
        await rig.RestorePredecessorAsync();
        await rig.ExecutedAsync();
        Assert.Equal(1, await rig.PassAsync());
        Assert.Equal(ArchiveDeliveryStates.Anchored, otherEntry.ArchiveDeliveryState);
        Assert.DoesNotContain(rig.World.DPlus.Records.Records.Keys, k => k.StartsWith(DPlusRecoveryNames.StoreRecoveryPrefix + other.ToString("D"), StringComparison.Ordinal));
    }

    // ================================================================ Evidence

    /// <summary>SRT-17 / SR-MV-07: evidence changed between the statement and execution makes the executor fail before any write.</summary>
    [Theory]
    [InlineData("row")]
    [InlineData("predecessor")]
    [InlineData("sentinel")]
    public async Task Srt17EvidenceChangedBeforeExecutionFailsTheRecovery(string change)
    {
        using var rig = new Rig(Stream);
        var entry = await rig.StopAtMissingPredecessorAsync();
        await rig.RestorePredecessorAsync();
        var id = await rig.ProposedAsync();
        await rig.ApproveAsync(id, "approver-a", "approver-b");
        switch (change)
        {
            case "row":
                entry.JournalEventBodyJson = entry.JournalEventBodyJson.Replace("event", "evil", StringComparison.Ordinal); // J1 rewrites row k and its digest
                entry.JournalEventDigest = SHA256.HashData(Encoding.UTF8.GetBytes(entry.JournalEventBodyJson));
                break;
            case "predecessor":
                Assert.True(rig.Archive.RemoveObjectForTesting(rig.ArchiveKey(40)));
                break;
            default:
                rig.Archive.RemoveIdentityForTesting(); // the sentinel replaced by a different instance
                await rig.Archive.CreateIdentityAsync(ArchiveIdentityCodec.NewIdentity(rig.World.Clock.GetUtcNow()), Ct);
                break;
        }

        var result = await rig.ExecuteAsync(id);
        Assert.Equal(DPlusRecoveryRefusals.EvidenceChanged, result.Code);
        Assert.DoesNotContain(rig.World.DPlus.Records.Records.Keys, k => k.Contains("/executions/", StringComparison.Ordinal));
        Assert.Equal(0, await rig.PassAsync());
    }

    /// <summary>SRT-18 / SR-MV-08: evidence changed after execution, before delivery: a generation-1 mismatch stop; the outcome is Failed.</summary>
    [Fact]
    public async Task Srt18EvidenceChangedAfterExecutionIsAGenerationOneMismatch()
    {
        using var rig = new Rig(Stream);
        var entry = await rig.StopAtMissingPredecessorAsync();
        await rig.RestorePredecessorAsync();
        var id = await rig.ExecutedAsync();
        entry.JournalEventBodyJson = entry.JournalEventBodyJson.Replace("event", "evil", StringComparison.Ordinal);
        entry.JournalEventDigest = SHA256.HashData(Encoding.UTF8.GetBytes(entry.JournalEventBodyJson));
        Assert.Equal(0, await rig.PassAsync());
        Assert.Equal("DPLUS_" + JournalFindingCodes.DPlusRecoveryEvidenceMismatch, entry.LastArchiveErrorCode);
        Assert.Contains(rig.MarkerName(41, 1), rig.Markers());
        Assert.Equal("FAILED", (await rig.Service.RecordOutcomeAsync(rig.Org, Audit, id, Ct)).Code);
    }

    /// <summary>SRT-19 / SR-PB-8: SR-C1 is refused without a valid anchor at exactly v = k − 1 with the copy's digest.</summary>
    [Theory]
    [InlineData("digest")]
    [InlineData("revision")]
    [InlineData("chain")]
    [InlineData("none")]
    public async Task Srt19AnchoredRestoreNeedsATrustedCheckpointAtThePredecessor(string defect)
    {
        using var rig = new Rig(Stream);
        switch (defect)
        {
            case "digest":
                await rig.AnchorAsync(digest: SHA256.HashData("other bytes"u8));
                break;
            case "revision":
                await rig.AnchorAsync(verifiedRevision: 39);
                break;
            case "chain":
                await rig.AnchorAsync();
                rig.World.DPlus.Checkpoints.PutObjectForTesting(InMemoryVerificationCheckpointStore.KeyForTesting(rig.Org, Audit, 3), "{}"u8.ToArray()); // a gap: the chain is invalid
                break;
        }

        await rig.StopAtMissingPredecessorAsync();
        var result = await rig.ProposeAsync(DPlusRecoveryStrategies.RestorePredecessor, restore: rig.PredecessorBytes);
        Assert.Equal(DPlusRecoveryCommandOutcome.Refused, result.Outcome);
        Assert.Contains(result.Code, new[] { DPlusRecoveryRefusals.AnchorUnavailable, DPlusRecoveryRefusals.StrategyPreconditionFailed });
        Assert.DoesNotContain(rig.World.DPlus.Records.Records.Keys, k => k.StartsWith(DPlusRecoveryNames.StoreRecoveryPrefix, StringComparison.Ordinal));
    }

    /// <summary>SRT-20 / SR-MV-14: SR-C1 with a present (corrupt) predecessor is refused; nothing is overwritten.</summary>
    [Fact]
    public async Task Srt20AnchoredRestoreNeverOverwritesAPresentPredecessor()
    {
        using var rig = new Rig(Stream);
        await rig.AnchorAsync();
        rig.Archive.PutObjectForTesting(rig.ArchiveKey(40), "{\"corrupt\":true}"u8.ToArray());
        rig.Append();
        Assert.Equal(0, await rig.PassAsync()); // the stop is a chain-integrity failure of the archived predecessor here
        var result = await rig.ProposeAsync(DPlusRecoveryStrategies.RestorePredecessor, restore: rig.PredecessorBytes);
        Assert.Equal(DPlusRecoveryCommandOutcome.Refused, result.Outcome);
        Assert.Equal("{\"corrupt\":true}", Encoding.UTF8.GetString((await rig.Archive.ReadAsync(rig.Org, Audit, 40, Ct))!));

        // A missing-predecessor stop where an object appears at k - 1 afterwards (J4): SR-C1 is refused, nothing is overwritten (SR-MV-14).
        using var appeared = new Rig(Stream);
        await appeared.AnchorAsync();
        await appeared.StopAtMissingPredecessorAsync();
        appeared.Archive.PutObjectForTesting(appeared.ArchiveKey(40), "{\"planted\":true}"u8.ToArray());
        var refused = await appeared.ProposeAsync(DPlusRecoveryStrategies.RestorePredecessor, restore: appeared.PredecessorBytes);
        Assert.Equal((DPlusRecoveryCommandOutcome.Refused, DPlusRecoveryRefusals.StrategyPreconditionFailed), (refused.Outcome, refused.Code));
        Assert.Equal("{\"planted\":true}", Encoding.UTF8.GetString((await appeared.Archive.ReadAsync(appeared.Org, Audit, 40, Ct))!));
    }

    /// <summary>SRT-22: contradictory evidence-commitment copies refuse the proposal; an unavailable store refuses it as unavailable.</summary>
    [Fact]
    public async Task Srt22ContradictoryOrUnavailableMandatoryEvidenceRefusesTheProposal()
    {
        using var rig = new Rig(Stream);
        await rig.StopAtMissingPredecessorAsync();
        await rig.RestorePredecessorAsync();
        var name = DPlusRecoveryNames.Commitment(rig.Org, Audit, 41, 0);
        rig.World.DPlus.Records.PutForTesting(DPlusRecoveryNames.StoreCommitmentPrefix + name, "{\"other\":1}"u8.ToArray());
        Assert.Equal(DPlusRecoveryRefusals.EvidenceContradictory, (await rig.ProposeAsync()).Code);
        rig.World.DPlus.Records.Unavailable = true;
        Assert.Equal(DPlusRecoveryCommandOutcome.Unavailable, (await rig.ProposeAsync()).Outcome);
    }

    /// <summary>SRT-23 / SR-PB-5: a stop-record integrity finding requires a containment attestation.</summary>
    [Fact]
    public async Task Srt23AStopRecordIntegrityFindingRequiresContainment()
    {
        using var rig = new Rig(Stream);
        await rig.StopAtMissingPredecessorAsync();
        Assert.True(rig.Archive.RemoveMetaForTesting(rig.MarkerName(41))); // J4 removes the marker; discovery reports and recreates it
        rig.Append(); // pending behind the stop, so discovery runs
        Assert.Equal(0, await rig.PassAsync());
        Assert.True(rig.FindingExists(JournalFindingCodes.DPlusStopRecordIncomplete));
        await rig.RestorePredecessorAsync();
        Assert.Equal(DPlusRecoveryRefusals.ContainmentRequired, (await rig.ProposeAsync()).Code);
        Assert.Equal(DPlusRecoveryCommandOutcome.Done, (await rig.ProposeAsync(containment: true)).Outcome);
    }

    /// <summary>SRT-24: an invalid object under the marker prefix that appears after the statement keeps the stream stopped after execution.</summary>
    [Fact]
    public async Task Srt24AStopRecordNotInTheStatementKeepsTheStreamStopped()
    {
        using var rig = new Rig(Stream);
        await rig.StopAtMissingPredecessorAsync();
        await rig.RestorePredecessorAsync();
        var id = await rig.ProposedAsync();
        await rig.ApproveAsync(id, "approver-a", "approver-b");
        rig.Archive.PutMetaForTesting(rig.MarkerName(41).Replace(".json", ".bak", StringComparison.Ordinal), "{}"u8.ToArray());
        Assert.Equal(DPlusRecoveryRefusals.StopSetChanged, (await rig.ExecuteAsync(id)).Code);

        // Placed after execution, the same object still stops the stream (it is not covered).
        using var second = new Rig(Stream);
        await second.StopAtMissingPredecessorAsync();
        await second.RestorePredecessorAsync();
        await second.ExecutedAsync();
        second.Archive.PutMetaForTesting(second.MarkerName(41).Replace(".json", ".bak", StringComparison.Ordinal), "{}"u8.ToArray());
        Assert.Equal(0, await second.PassAsync());
        Assert.True(second.FindingExists(JournalFindingCodes.DPlusStopMarkerInvalid));
    }

    // ================================================================ Concurrency, interruption, restart

    /// <summary>SRT-25: the same recovery executed twice concurrently is idempotent.</summary>
    [Fact]
    public async Task Srt25ConcurrentExecutionOfTheSameRecoveryIsIdempotent()
    {
        using var rig = new Rig(Stream);
        await rig.StopAtMissingPredecessorAsync();
        await rig.RestorePredecessorAsync();
        var id = await rig.ProposedAsync();
        await rig.ApproveAsync(id, "approver-a", "approver-b");
        var results = await Task.WhenAll(rig.ExecuteAsync(id), rig.ExecuteAsync(id));
        Assert.All(results, r => Assert.Equal(DPlusRecoveryCommandOutcome.Done, r.Outcome));
        Assert.Single(rig.World.DPlus.Records.Records.Keys, k => k.Contains("/executions/", StringComparison.Ordinal));
        Assert.Equal(1, await rig.PassAsync());
    }

    /// <summary>SRT-26 / SR-MV-09: an executor terminated before the execution record leaves the stream stopped; the re-run completes idempotently.</summary>
    [Fact]
    public async Task Srt26AnInterruptedExecutionLiftsNothingUntilTheExecutionRecordExists()
    {
        using var rig = new Rig(Stream);
        await rig.AnchorAsync();
        var entry = await rig.StopAtMissingPredecessorAsync();
        var id = await rig.ProposedAsync(DPlusRecoveryStrategies.RestorePredecessor, rig.PredecessorBytes);
        await rig.ApproveAsync(id, "approver-a", "approver-b");

        // Terminate at the hint (after the action and the predecessor write): the commit of the hint fails.
        rig.World.FailOn[nameof(IAuthorityScope.CommitAsync)] = new InvalidOperationException("terminated");
        await Assert.ThrowsAsync<InvalidOperationException>(() => rig.ExecuteAsync(id));
        rig.World.FailOn.Clear();
        Assert.NotNull(await rig.Archive.ReadAsync(rig.Org, Audit, 40, Ct)); // the action happened
        Assert.DoesNotContain(rig.World.DPlus.Records.Records.Keys, k => k.Contains("/executions/", StringComparison.Ordinal));
        entry.ArchiveDeliveryState = ArchiveDeliveryStates.Pending; // even if the hint is somehow present
        Assert.Equal(0, await rig.PassAsync()); // no execution record: no lift
        Assert.Equal(ArchiveDeliveryStates.VerificationFailed, entry.ArchiveDeliveryState); // re-marked from the unresolved records

        // The re-run sees its own completed predecessor write as the approved absent state and completes.
        Assert.Equal(DPlusRecoveryCommandOutcome.Done, (await rig.ExecuteAsync(id)).Outcome);
        Assert.Equal(1, await rig.PassAsync());
        Assert.Equal(ArchiveDeliveryStates.Anchored, entry.ArchiveDeliveryState);
    }

    /// <summary>SRT-27: the derived state is identical before and after a restart, at each recovery state.</summary>
    [Fact]
    public async Task Srt27DerivedRecoveryStateSurvivesARestart()
    {
        using var rig = new Rig(Stream);
        await rig.StopAtMissingPredecessorAsync();
        await rig.RestorePredecessorAsync();
        var id = await rig.ProposedAsync();
        async Task<string> StateAsync() => (await rig.Service.StatusAsync(rig.Org, Audit, id, Ct))!.State;
        Assert.Equal("Proposed", await StateAsync());
        rig.Restart();
        Assert.Equal("Proposed", await StateAsync());
        await rig.ApproveAsync(id, "approver-a", "approver-b");
        rig.Restart();
        Assert.Equal("Approved", await StateAsync());
        Assert.Equal(0, await rig.PassAsync());
        await rig.ExecuteAsync(id);
        rig.Restart();
        Assert.Equal("Executed", await StateAsync());
        Assert.Equal(1, await rig.PassAsync());
        await rig.Service.RecordOutcomeAsync(rig.Org, Audit, id, Ct);
        rig.Restart();
        Assert.Equal(DPlusRecoveryOutcome.Completed, await StateAsync());
    }

    /// <summary>SRT-28: with a lift in effect, an entry above the recovered revision is held (SR-DL-9) and no spurious stop is created.</summary>
    [Fact]
    public async Task Srt28AnEntryAboveALiftedRevisionIsHeldUntilTheRevisionIsDelivered()
    {
        using var rig = new Rig(Stream);
        var entry = await rig.StopAtMissingPredecessorAsync();
        var next = rig.Append();
        await rig.RestorePredecessorAsync();
        var id = await rig.ExecutedAsync();
        entry.ArchiveDeliveryState = ArchiveDeliveryStates.VerificationFailed; // J1 reverts the hint: the work loop fetches 42 first
        var markers = rig.Markers().Count;
        Assert.Equal(0, await rig.PassAsync());
        Assert.Equal((ArchiveDeliveryStates.Pending, ArchiveProcessor.StreamStoppedCode), (next.ArchiveDeliveryState, next.LastArchiveErrorCode));
        Assert.Equal(markers, rig.Markers().Count); // no spurious PREDECESSOR_MISSING at 42
        Assert.Equal("EXECUTION_RESUMED", (await rig.ExecuteAsync(id)).Code);
        Assert.Equal(2, await rig.PassAsync()); // the resumed run restores the hint; 41 then 42
    }

    // ================================================================ Outages

    /// <summary>SRT-29: an archive outage during proposal and execution writes nothing; during the pass it ends the pass (DP-7) without a finding.</summary>
    [Fact]
    public async Task Srt29ArchiveOutagesWriteNothingAndFabricateNoFinding()
    {
        using var rig = new Rig(Stream);
        await rig.StopAtMissingPredecessorAsync();
        await rig.RestorePredecessorAsync();
        var outage = new UnavailableSecurityEventArchive("archive down");
        var service = new DPlusStreamRecoveryService(rig.World.Store, outage, rig.Findings, rig.World.DPlus.Durability, rig.World.Clock);
        var before = Snapshot(rig);
        Assert.Equal(DPlusRecoveryCommandOutcome.Unavailable, (await service.ProposeAsync(new DPlusRecoveryProposal(rig.Org, Audit, DPlusRecoveryStrategies.FalseAlarm, Requester, "r", "DevelopmentReset",
            false, null, Analyst, "n", TimeSpan.FromHours(1), null, null), Ct)).Outcome);
        Assert.Equal(before.Count, Snapshot(rig).Count);
        var id = await rig.ProposedAsync();
        await rig.ApproveAsync(id, "approver-a", "approver-b");
        Assert.Equal(DPlusRecoveryCommandOutcome.Unavailable, (await service.ExecuteAsync(rig.Org, Audit, id, "e", Ct)).Outcome);
        Assert.DoesNotContain(rig.World.DPlus.Records.Records.Keys, k => k.Contains("/executions/", StringComparison.Ordinal));
        var findings = rig.Findings.Records.Count;
        Assert.Equal(0, await new ArchiveProcessor(rig.World.Store, outage, rig.World.Clock, rig.Alerts, rig.Findings, rig.World.DPlus.Durability).ProcessOrganizationAsync(rig.Org, Ct));
        Assert.Equal(findings, rig.Findings.Records.Count);
    }

    /// <summary>SRT-30: a verification-store outage: the proposal and execution are unavailable; discovery is inconclusive and the audit stream waits.</summary>
    [Fact]
    public async Task Srt30VerificationStoreOutagesAreInconclusiveNeverALift()
    {
        using var rig = new Rig(Stream);
        var entry = await rig.StopAtMissingPredecessorAsync();
        await rig.RestorePredecessorAsync();
        var id = await rig.ProposedAsync();
        await rig.ApproveAsync(id, "approver-a", "approver-b");
        rig.World.DPlus.Records.Unavailable = true;
        Assert.Equal(DPlusRecoveryCommandOutcome.Unavailable, (await rig.ExecuteAsync(id)).Outcome);
        Assert.Equal(DPlusRecoveryCommandOutcome.Unavailable, (await rig.ProposeAsync()).Outcome);
        entry.ArchiveDeliveryState = ArchiveDeliveryStates.Pending;
        Assert.Equal(0, await rig.PassAsync());
        Assert.Equal(ArchiveProcessor.RecoveryRecordsUnavailableCode, entry.LastArchiveErrorCode);
        rig.World.DPlus.Records.Unavailable = false;
        Assert.Equal(DPlusRecoveryCommandOutcome.Done, (await rig.ExecuteAsync(id)).Outcome);
        Assert.Equal(1, await rig.PassAsync());
    }

    /// <summary>SRT-31 / SR-MV-14: a predecessor write conflict fails the recovery; nothing after it is written and nothing is overwritten.</summary>
    [Fact]
    public async Task Srt31ARestoreConflictFailsWithoutOverwritingOrLifting()
    {
        using var rig = new Rig(Stream);
        await rig.AnchorAsync();
        await rig.StopAtMissingPredecessorAsync();
        var id = await rig.ProposedAsync(DPlusRecoveryStrategies.RestorePredecessor, rig.PredecessorBytes);
        await rig.ApproveAsync(id, "approver-a", "approver-b");

        // Between the evidence check and the write, an object appears at the predecessor key: simulate by a conflicting archive.
        var conflicting = new ConflictingArchive(rig.Archive);
        var service = new DPlusStreamRecoveryService(rig.World.Store, conflicting, rig.Findings, rig.World.DPlus.Durability, rig.World.Clock);
        Assert.Equal(DPlusRecoveryRefusals.RestoreConflict, (await service.ExecuteAsync(rig.Org, Audit, id, "e", Ct)).Code);
        Assert.DoesNotContain(rig.World.DPlus.Records.Records.Keys, k => k.Contains("/executions/", StringComparison.Ordinal));
        Assert.Equal("Failed", (await rig.Service.StatusAsync(rig.Org, Audit, id, Ct))!.State);
        Assert.Equal(0, await rig.PassAsync());
    }

    /// <summary>An archive whose predecessor append always conflicts (an object appeared at the key).</summary>
    private sealed class ConflictingArchive(InMemorySecurityEventArchive inner) : ISecurityEventArchive, ISecurityEventArchiveInventory, IArchiveMetaRecords
    {
        public string ArchiveName => "Conflicting";

        public Task<ArchiveReceipt> AppendAsync(Guid organizationId, string streamName, long streamRevision, ReadOnlyMemory<byte> eventBody, byte[] expectedDigest, CancellationToken cancellationToken) =>
            throw new ArchiveConflictException("an object exists at the key");

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

        public Task<CheckpointCreateResult> CreateMetaAsync(string metaObjectName, ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken) => inner.CreateMetaAsync(metaObjectName, bytes, cancellationToken);

        public Task<StoredObjectRead> ReadMetaAsync(string metaObjectName, CancellationToken cancellationToken) => inner.ReadMetaAsync(metaObjectName, cancellationToken);

        public Task<IReadOnlyList<ArchiveMetaObject>> ListMetaAsync(string metaPrefix, CancellationToken cancellationToken) => inner.ListMetaAsync(metaPrefix, cancellationToken);
    }

    // ================================================================ Attacker actions

    /// <summary>SRT-32 / SR-MV-05: AuthorityDb reset, a forged hint code and a forged Anchored row never lift the stop.</summary>
    [Fact]
    public async Task Srt32AuthorityDbStateNeverLiftsAStop()
    {
        using var rig = new Rig(Stream);
        var entry = await rig.StopAtMissingPredecessorAsync();
        var next = rig.Append();
        await rig.RestorePredecessorAsync();
        entry.ArchiveDeliveryState = ArchiveDeliveryStates.Pending;
        entry.LastArchiveErrorCode = ArchiveProcessor.RecoveryAuthorizedCodePrefix + Guid.CreateVersion7().ToString("D");
        Assert.Equal(0, await rig.PassAsync());
        entry.ArchiveDeliveryState = ArchiveDeliveryStates.Anchored;
        entry.ArchiveReceiptReference = "forged";
        Assert.Equal(0, await rig.PassAsync());
        Assert.NotEqual(ArchiveDeliveryStates.Anchored, next.ArchiveDeliveryState);
        Assert.Null(await rig.Archive.ReadAsync(rig.Org, Audit, 41, Ct));
    }

    /// <summary>SRT-33 / SR-MV-06: archive k created directly without recovery does not resolve the stop; k + 1 is not delivered.</summary>
    [Fact]
    public async Task Srt33ArchivePresenceOfTheFailedRevisionResolvesNothing()
    {
        using var rig = new Rig(Stream);
        var entry = await rig.StopAtMissingPredecessorAsync();
        var next = rig.Append();
        await rig.RestorePredecessorAsync();
        await rig.Archive.AppendAsync(rig.Org, Audit, 41, Encoding.UTF8.GetBytes(entry.JournalEventBodyJson), entry.JournalEventDigest, Ct); // J4
        Assert.Equal(0, await rig.PassAsync());
        Assert.Equal(ArchiveDeliveryStates.Pending, next.ArchiveDeliveryState);
        Assert.Null(await rig.Archive.ReadAsync(rig.Org, Audit, 42, Ct));
    }

    /// <summary>SRT-34 / SR-MV-01: deleting recovery records from one store keeps the lift; from both stores re-stops the stream.</summary>
    [Theory]
    [InlineData("statement.json")]
    [InlineData("decisions")]
    [InlineData("execution")]
    public async Task Srt34DeletingRecoveryRecordsFromBothStoresReStopsTheStream(string record)
    {
        using var rig = new Rig(Stream);
        await rig.StopAtMissingPredecessorAsync();
        await rig.RestorePredecessorAsync();
        var id = await rig.ExecutedAsync();
        var store = rig.World.DPlus.Records;
        bool Matches(string key) => record switch
        {
            "decisions" => key.Contains(id.ToString("D") + "/decisions/", StringComparison.Ordinal),
            "execution" => key.Contains("/executions/", StringComparison.Ordinal),
            _ => key.EndsWith(id.ToString("D") + "/" + record, StringComparison.Ordinal),
        };

        foreach (var key in store.Records.Keys.Where(Matches).ToList())
        {
            store.RemoveForTesting(key); // one store only: the archive copy remains
        }

        rig.Restart();
        var next = rig.Append();
        Assert.Equal(2, await rig.PassAsync()); // lift kept (valid copy in the archive)
        Assert.Equal(ArchiveDeliveryStates.Anchored, next.ArchiveDeliveryState);

        foreach (var key in rig.Archive.MetaForTesting.Keys.Where(Matches).ToList())
        {
            rig.Archive.RemoveMetaForTesting(key); // both stores
        }

        var later = rig.Append();
        rig.Restart();
        Assert.Equal(0, await rig.PassAsync()); // fail closed: the generation-0 stop is unresolved again
        Assert.Equal(ArchiveDeliveryStates.Pending, later.ArchiveDeliveryState);
    }

    /// <summary>
    /// SRT-35 — ACCEPTED RESIDUAL R-SR-6 (pinned): a store administrator who deletes a cancellation from both stores and forges an execution
    /// record executes an approved-but-cancelled statement. Production needs immutable stores and ledger-anchored execution times (WP-8).
    /// </summary>
    [Fact]
    public async Task Srt35ResidualDeletingACancellationFromBothStoresAndForgingAnExecutionLifts()
    {
        using var rig = new Rig(Stream);
        var entry = await rig.StopAtMissingPredecessorAsync();
        await rig.RestorePredecessorAsync();
        var id = await rig.ProposedAsync();
        await rig.ApproveAsync(id, "approver-a", "approver-b");
        var cancellationJti = Guid.CreateVersion7();
        await rig.DecideAsync(id, rig.Sign(id, "approver-c", DPlusRecoveryClaims.Cancel, DPlusRecoveryClaims.CancellationKind, jti: cancellationJti));
        var name = DPlusRecoveryNames.Decision(rig.Org, Audit, id, cancellationJti);
        rig.World.DPlus.Records.RemoveForTesting(DPlusRecoveryNames.StoreRecoveryPrefix + name);
        rig.Archive.RemoveMetaForTesting(DPlusRecoveryNames.ArchiveRecoveryPrefix + name);
        var statement = rig.Statement(id, out var sha);
        rig.World.DPlus.Records.PutForTesting(DPlusRecoveryNames.StoreRecoveryPrefix + DPlusRecoveryNames.Execution(rig.Org, Audit, statement.StopSetSha256Hex),
            DPlusRecoveryCodec.Serialize(new DPlusRecoveryExecution(DPlusRecoveryExecution.SchemaV1, id, rig.Org, Audit, 41, 0, sha, statement.StopSetSha256Hex, "forger", "x")));
        entry.ArchiveDeliveryState = ArchiveDeliveryStates.Pending; // J1 sets the hint
        Assert.Equal(1, await rig.PassAsync()); // RESIDUAL: delivered
    }

    /// <summary>SRT-36: archive instance substitution (a new sentinel) is a durable INSTANCE_CHANGED stop that no G14-R2 recovery can lift.</summary>
    [Fact]
    public async Task Srt36ArchiveInstanceSubstitutionIsADurableNonRecoverableStop()
    {
        using var rig = new Rig(Stream, archivePredecessor: true);
        rig.Archive.WipeForTesting();
        await rig.Archive.CreateIdentityAsync(ArchiveIdentityCodec.NewIdentity(rig.World.Clock.GetUtcNow()), Ct);
        await rig.RestorePredecessorAsync(); // a consistent forged copy of history in the new instance
        var entry = rig.Append();
        Assert.Equal(0, await rig.PassAsync());
        Assert.Equal("DPLUS_" + JournalFindingCodes.ArchiveRegression + ":" + JournalFindingCodes.InstanceChangedSubtype, entry.LastArchiveErrorCode);
        Assert.Equal(DPlusRecoveryRefusals.ArchiveInstanceBlocked, (await rig.ProposeAsync()).Code);

        // Restoring the original instance's sentinel does not resume: the stop is durable (finding), and SR-D is not operational.
        rig.Archive.WipeForTesting();
        await rig.Archive.CreateIdentityAsync(new ArchiveIdentity(ArchiveIdentity.SchemaV1, rig.Instance, rig.World.Clock.GetUtcNow()), Ct);
        await rig.RestorePredecessorAsync();
        Assert.Equal(0, await rig.PassAsync());
        Assert.NotEqual(ArchiveDeliveryStates.Anchored, entry.ArchiveDeliveryState);
    }

    /// <summary>SRT-37 / SR-PB-1: no statement under an organization quarantine.</summary>
    [Fact]
    public async Task Srt37NoRecoveryUnderOrganizationQuarantine()
    {
        using var rig = new Rig(Stream);
        await rig.StopAtMissingPredecessorAsync();
        await rig.RestorePredecessorAsync();
        Application.Common.OrganizationQuarantine.Set(rig.World.Organizations[rig.Org], "JOURNAL_BODY_DIGEST_MISMATCH");
        Assert.Equal(DPlusRecoveryRefusals.OrganizationQuarantined, (await rig.ProposeAsync()).Code);
    }

    /// <summary>
    /// SRT-38 / SR-PB-2 (as amended by A-6 §26): statements for a stream that is not D+-enabled, or not a journal stream, are refused; deferred
    /// strategies are refused.
    /// </summary>
    [Fact]
    public async Task Srt38OtherStreamsAndDeferredStrategiesAreRefused()
    {
        using var rig = new Rig(Stream);
        foreach (var stream in new[] { "platform-operators", "not-a-stream" })
        {
            Assert.Equal(DPlusRecoveryRefusals.StreamNotRecoverable, (await rig.Service.ProposeAsync(new DPlusRecoveryProposal(rig.Org, stream, DPlusRecoveryStrategies.FalseAlarm, Requester, "r",
                "DevelopmentReset", false, null, Analyst, "n", TimeSpan.FromHours(1), null, null), Ct)).Code);
        }

        foreach (var strategy in DPlusRecoveryStrategies.Deferred)
        {
            Assert.Equal(DPlusRecoveryRefusals.StrategyNotOperational, (await rig.ProposeAsync(strategy, cause: "DevelopmentReset")).Code);
        }

        // A hand-made statement naming a deferred strategy is structurally invalid (never authorizes).
        var statement = new DPlusRecoveryStatement(DPlusRecoveryStatement.SchemaV1, Guid.CreateVersion7(), rig.Org, Audit, 41, 0,
            [new DPlusStopRecordReference(DPlusStopRecordReference.Marker, "m", new string('a', 64), 41, 0)], new string('b', 64), "SR-B", "x", new string('c', 64),
            new DPlusRecoveryExpectation("i", new string('d', 64), "Present", null), new string('e', 64), Requester, new string('f', 64), rig.World.Clock.GetUtcNow(), rig.World.Clock.GetUtcNow().AddHours(1), "t", null);
        Assert.Contains("not operational", DPlusRecoveryLedger.ValidateStatement(statement, rig.Org, Audit, statement.RecoveryId), StringComparison.Ordinal);
    }

    // ================================================================ W2-S evidence pre-commitment

    /// <summary>SRT-39: normal delivery writes one byte-identical commitment per revision in each store; a repeated pass is idempotent.</summary>
    [Fact]
    public async Task Srt39NormalDeliveryWritesOneCommitmentPerRevisionPerStore()
    {
        using var rig = new Rig(Stream, archivePredecessor: true);
        rig.Append();
        rig.Append();
        Assert.Equal(2, await rig.PassAsync());
        foreach (var revision in new long[] { 41, 42 })
        {
            var name = DPlusRecoveryNames.Commitment(rig.Org, Audit, revision, 0);
            var archived = rig.Archive.MetaForTesting[DPlusRecoveryNames.ArchiveCommitmentPrefix + name];
            var stored = rig.World.DPlus.Records.Records[DPlusRecoveryNames.StoreCommitmentPrefix + name];
            Assert.Equal(archived, stored);
            var commitment = DPlusRecordInspection.Parse<DPlusEvidenceCommitment>(archived)!;
            Assert.Equal((revision, 0, rig.Instance, "Present"), (commitment.StreamRevision, commitment.StopGeneration, commitment.ArchiveInstanceId, commitment.PredecessorState));
        }

        Assert.Equal(2, DPlusRecordInspection.ArchiveNames(rig.Archive, DPlusRecoveryNames.ArchiveCommitmentPrefix).Count());
        Assert.Equal(0, await rig.PassAsync());
        Assert.Equal(2, DPlusRecordInspection.ArchiveNames(rig.Archive, DPlusRecoveryNames.ArchiveCommitmentPrefix).Count());
    }

    /// <summary>SRT-41: a termination with unchanged evidence re-evaluates to the same verdict; no recovery is needed.</summary>
    [Fact]
    public async Task Srt41ARestartWithUnchangedEvidenceReEvaluatesWithoutRecovery()
    {
        using var rig = new Rig(Stream, archivePredecessor: true);
        var entry = rig.Append();
        rig.World.FailOn[nameof(IAuthorityScope.CommitAsync)] = new InvalidOperationException("terminated after append, before the row commit");
        await Assert.ThrowsAsync<InvalidOperationException>(() => rig.PassAsync());
        rig.World.FailOn.Clear();
        entry.ArchiveDeliveryState = ArchiveDeliveryStates.Pending; // the row commit was lost
        rig.Restart();
        Assert.Equal(1, await rig.PassAsync()); // same commitment, same evidence: delivered (idempotent append)
        Assert.Equal(ArchiveDeliveryStates.Anchored, entry.ArchiveDeliveryState);

        using var refused = new Rig(Stream);
        var stopped = refused.Append();
        refused.Archive.FailStopMarkerWritesForTesting = true;
        refused.Findings.RecordAvailable = false;
        Assert.Equal(0, await refused.PassAsync()); // detected; no durable stop record (held in memory only)
        Assert.Equal(ArchiveProcessor.DurableStopNotEstablishedCode, stopped.LastArchiveErrorCode);
        refused.Archive.FailStopMarkerWritesForTesting = false;
        refused.Findings.RecordAvailable = true;
        refused.Restart();
        Assert.Equal(0, await refused.PassAsync()); // the same refusal is re-derived from unchanged evidence
        Assert.Equal("DPLUS_" + JournalFindingCodes.PredecessorMissing, stopped.LastArchiveErrorCode);
    }

    /// <summary>SRT-42 / SR-MV-11: with the commitment store unavailable nothing is evaluated; the audit stream waits.</summary>
    [Fact]
    public async Task Srt42NoEvaluationWithoutADurableCommitment()
    {
        using var rig = new Rig(Stream, archivePredecessor: true);
        var entry = rig.Append();
        rig.World.DPlus.Records.Unavailable = true;
        Assert.Equal(0, await rig.PassAsync());
        Assert.Equal(ArchiveDeliveryStates.Pending, entry.ArchiveDeliveryState);
        Assert.Contains(entry.LastArchiveErrorCode, new[] { ArchiveProcessor.RecoveryRecordsUnavailableCode, ArchiveProcessor.CommitmentUnavailableCode });
        Assert.Empty(rig.Findings.Records);

        // Only the commitment area unavailable (recovery records readable): the evaluation does not happen, nothing is delivered (SR-MV-11).
        rig.World.DPlus.Records.Unavailable = false;
        rig.World.DPlus.Records.UnavailablePrefix = DPlusRecoveryNames.StoreCommitmentPrefix;
        Assert.Equal(0, await rig.PassAsync());
        Assert.Equal((ArchiveDeliveryStates.Pending, ArchiveProcessor.CommitmentUnavailableCode), (entry.ArchiveDeliveryState, entry.LastArchiveErrorCode));
        Assert.Null(await rig.Archive.ReadAsync(rig.Org, Audit, 41, Ct));
        rig.World.DPlus.Records.UnavailablePrefix = null;
        Assert.Equal(1, await rig.PassAsync());
    }

    /// <summary>SRT-43 / SR-MV-12: commitment copies that differ between the stores stop the stream; nothing is overwritten.</summary>
    [Fact]
    public async Task Srt43ContradictoryCommitmentCopiesStopTheStream()
    {
        using var rig = new Rig(Stream, archivePredecessor: true);
        var entry = rig.Append();
        var name = DPlusRecoveryNames.Commitment(rig.Org, Audit, 41, 0);
        rig.World.DPlus.Records.PutForTesting(DPlusRecoveryNames.StoreCommitmentPrefix + name, "{\"planted\":true}"u8.ToArray());
        Assert.Equal(0, await rig.PassAsync());
        Assert.Equal("DPLUS_" + JournalFindingCodes.DPlusEvidenceChangedAfterCommitment, entry.LastArchiveErrorCode);
        Assert.Equal("{\"planted\":true}", Encoding.UTF8.GetString(rig.World.DPlus.Records.Records[DPlusRecoveryNames.StoreCommitmentPrefix + name]));

        rig.World.DPlus.Records.PutForTesting(DPlusRecoveryNames.StoreCommitmentPrefix + name, "{\"other\":true}"u8.ToArray());
        rig.Archive.PutMetaForTesting(DPlusRecoveryNames.ArchiveCommitmentPrefix + name, "{\"planted\":true}"u8.ToArray());
        Assert.Equal(0, await rig.PassAsync()); // both copies present and different: contradictory
    }

    /// <summary>SRT-44 — KNOWN LIMIT R-SR-2 (pinned): removing the commitment from both stores, then replacing the evidence, resumes delivery.</summary>
    [Fact]
    public async Task Srt44KnownLimitRemovingTheCommitmentFromBothStoresBeforeEvaluation()
    {
        using var rig = new Rig(Stream);
        var entry = rig.Append();
        rig.Archive.FailStopMarkerWritesForTesting = true; // terminate before any stop record (see SRT-40)
        rig.Findings.RecordAvailable = false;
        Assert.Equal(0, await rig.PassAsync());
        Assert.Empty(rig.Markers());
        rig.Archive.FailStopMarkerWritesForTesting = false;
        rig.Findings.RecordAvailable = true;

        var commitment = DPlusRecoveryNames.Commitment(rig.Org, Audit, 41, 0);
        rig.Archive.RemoveMetaForTesting(DPlusRecoveryNames.ArchiveCommitmentPrefix + commitment);
        rig.World.DPlus.Records.RemoveForTesting(DPlusRecoveryNames.StoreCommitmentPrefix + commitment);
        await rig.RestorePredecessorAsync();
        entry.ArchiveDeliveryState = ArchiveDeliveryStates.Pending;
        rig.Restart();
        Assert.Equal(1, await rig.PassAsync()); // KNOWN LIMIT: delivered
    }

    /// <summary>SRT-45 (adapted; SR-OD-15 unreviewed): a commitment with another rule-set version is treated as a change (restrictive default).</summary>
    [Fact]
    public async Task Srt45ARuleSetVersionDifferenceIsTreatedAsAChange()
    {
        using var rig = new Rig(Stream, archivePredecessor: true);
        var entry = rig.Append();
        var evidence = new AuditDeliveryEvidence(entry, rig.Instance, AuditPredecessorState.Present, rig.PredecessorBytes);
        var older = DPlusEvidenceCommitments.Build(evidence, 0) with { RuleSetVersion = "DPlus/R1-R7/v0" };
        var name = DPlusRecoveryNames.Commitment(rig.Org, Audit, 41, 0);
        await rig.Archive.CreateMetaAsync(DPlusRecoveryNames.ArchiveCommitmentPrefix + name, DPlusRecoveryCodec.Serialize(older), Ct);
        await rig.World.DPlus.Records.CreateAsync(DPlusRecoveryNames.StoreCommitmentPrefix + name, DPlusRecoveryCodec.Serialize(older), Ct);
        Assert.Equal(0, await rig.PassAsync());
        Assert.Equal("DPLUS_" + JournalFindingCodes.DPlusEvidenceChangedAfterCommitment, entry.LastArchiveErrorCode);
        Assert.Contains(rig.Findings.Records.Values, r => Encoding.UTF8.GetString(r).Contains("rule_set_version", StringComparison.Ordinal));
    }

    /// <summary>W2-S: removing both stop records (without the commitment) no longer resumes delivery when the evidence changed (A-3 known limit narrowed).</summary>
    [Fact]
    public async Task RemovingBothStopRecordsNoLongerResumesDeliveryWhenTheEvidenceChanged()
    {
        using var rig = new Rig(Stream);
        var entry = await rig.StopAtMissingPredecessorAsync();
        rig.Archive.RemoveMetaForTesting(rig.MarkerName(41));
        foreach (var key in rig.Findings.Records.Keys.ToList())
        {
            rig.Findings.RemoveForTesting(rig.Org, Audit, key[(key.LastIndexOf('/') + 1)..]);
        }

        entry.ArchiveDeliveryState = ArchiveDeliveryStates.Pending;
        await rig.RestorePredecessorAsync();
        rig.Restart();
        Assert.Equal(0, await rig.PassAsync());
        Assert.Equal("DPLUS_" + JournalFindingCodes.DPlusEvidenceChangedAfterCommitment, entry.LastArchiveErrorCode);
    }

    // ================================================================ Archive-instance pinning

    /// <summary>Pinning: missing anchors fail closed (no marker); contradictory anchors are a durable stop.</summary>
    [Fact]
    public async Task PinningFailsClosedOnMissingOrContradictoryAnchors()
    {
        using var rig = new Rig(Stream, archivePredecessor: true);
        rig.World.DPlus.Provisioning.RemoveRecordForTesting();
        var entry = rig.Append();
        Assert.Equal(0, await rig.PassAsync());
        Assert.Equal((ArchiveDeliveryStates.Pending, "DPLUS_" + JournalFindingCodes.ArchiveRegression + ":" + JournalFindingCodes.InstanceUnpinnedSubtype), (entry.ArchiveDeliveryState, entry.LastArchiveErrorCode));
        Assert.Empty(rig.Markers());

        await rig.AnchorAsync(instance: rig.Instance); // a first checkpoint pins the instance (baselined archive)
        Assert.Equal(1, await rig.PassAsync());

        using var contradictory = new Rig(Stream, archivePredecessor: true);
        await contradictory.AnchorAsync(instance: "another-instance");
        var refused = contradictory.Append();
        Assert.Equal(0, await contradictory.PassAsync());
        Assert.Equal("DPLUS_" + JournalFindingCodes.ArchiveRegression + ":" + JournalFindingCodes.InstanceAnchorContradictorySubtype, refused.LastArchiveErrorCode);
        Assert.Single(contradictory.Markers());
    }

    /// <summary>Without D+ durability composed (production until WP-8), the audit stream is never evaluated; the other streams are unaffected.</summary>
    [Fact]
    public async Task WithoutDurabilityTheAuditStreamIsNeverEvaluated()
    {
        using var rig = new Rig(Stream, archivePredecessor: true);
        var entry = rig.Append();
        var processor = new ArchiveProcessor(rig.World.Store, rig.Archive, rig.World.Clock, rig.Alerts, rig.Findings);
        Assert.Equal(0, await processor.ProcessOrganizationAsync(rig.Org, Ct));
        Assert.Equal((ArchiveDeliveryStates.Pending, ArchiveProcessor.DurabilityNotComposedCode), (entry.ArchiveDeliveryState, entry.LastArchiveErrorCode));
        Assert.Empty(DPlusRecordInspection.ArchiveNames(rig.Archive, DPlusRecoveryNames.ArchiveCommitmentPrefix));
    }

    // ================================================================ Verifier

    /// <summary>SRT-46: the verifier's full sweep recognizes recovery records, valid commitments and generation markers; a malformed commitment is reported.</summary>
    [Fact]
    public async Task Srt46TheVerifierRecognizesRecoveryRecordsCommitmentsAndGenerationMarkers()
    {
        var world = new JournalVerificationWorld();
        var org = world.AddOrganization();
        foreach (var stream in new[] { Audit, JournalStreamNames.Audit }.Distinct())
        {
            world.Append(org, stream, 3);
            await world.DeliverAsync(org, stream);
            Assert.Equal(nameof(JournalBaselineOutcome.Clean), (await world.Baseline().RunAsync(JournalVerificationWorld.BaselineRequest(org, stream), Ct)).Outcome);
        }

        var instance = (await world.Archive.ReadIdentityAsync(Ct)).Identity!.ArchiveInstanceId;
        var marker = new DPlusStreamStop(DPlusStreamStop.SchemaV2, org, Audit, 4, JournalFindingCodes.PredecessorMissing, null, "identity", Guid.CreateVersion7(), instance,
            JournalVerificationWorld.Start, "test", 1);
        await world.Archive.CreateMetaAsync(DPlusStreamStops.MarkerObjectName(org, Audit, 4, 1), DPlusStreamStops.Serialize(marker), Ct);
        await world.Archive.CreateMetaAsync(DPlusRecoveryNames.ArchiveRecoveryPrefix + DPlusRecoveryNames.Statement(org, Audit, Guid.CreateVersion7()), "{}"u8.ToArray(), Ct);
        var commitment = new DPlusEvidenceCommitment(DPlusEvidenceCommitment.SchemaV1, org, Audit, 4, 0, instance, Guid.CreateVersion7(), new string('a', 64), null, "p", "Absent", null,
            DPlusEvidenceCommitment.CurrentRuleSetVersion);
        await world.Archive.CreateMetaAsync(DPlusRecoveryNames.ArchiveCommitmentPrefix + DPlusRecoveryNames.Commitment(org, Audit, 4, 0), DPlusRecoveryCodec.Serialize(commitment), Ct);
        await world.Archive.CreateMetaAsync(DPlusRecoveryNames.ArchiveCommitmentPrefix + DPlusRecoveryNames.Commitment(org, Audit, 9, 0), "{}"u8.ToArray(), Ct);
        // The organization's meta/ objects (every stream's markers, records and commitments) are inspected by the audit stream's full sweep.
        var run = await world.Verifier().SweepAsync(org, JournalStreamNames.Audit, JournalSweepKind.Full, Ct);
        Assert.DoesNotContain(run.Findings, f => f.FindingCode is JournalFindingCodes.ArchiveUnexpectedObject or JournalFindingCodes.DPlusStopMarkerInvalid);
        Assert.Single(run.Findings, f => f.FindingCode == JournalFindingCodes.RecoveryRecordInvalid && f.FirstRevision == 9);
    }

    // ================================================================ Codec, names and stop generations

    [Fact]
    public void MarkerNamesAndGenerationsParseCanonicallyOnly()
    {
        Assert.Equal((41L, 0), DPlusStreamStops.ParseMarkerFileName("000000000041.json"));
        Assert.Equal((41L, 12), DPlusStreamStops.ParseMarkerFileName("000000000041.r0012.json"));
        foreach (var bad in new[] { "000000000041.r0000.json", "000000000000.json", "41.json", "000000000041.r12.json", "000000000041.x0001.json", "000000000041.json.bak", "00000000004a.json" })
        {
            Assert.Null(DPlusStreamStops.ParseMarkerFileName(bad));
        }

        var finding = new JournalVerificationFinding(Guid.CreateVersion7(), Audit, JournalFindingCodes.PredecessorMissing, null, JournalFindingSeverity.Security, 41, 41, Guid.CreateVersion7(),
            DateTimeOffset.UnixEpoch, [], "d", "DeliveryRefused", null);
        Assert.NotEqual(finding.Identity, (finding with { StopGeneration = 1 }).Identity);
        Assert.Equal(finding.Identity, (finding with { StopGeneration = null }).Identity);
        Assert.DoesNotContain("stop_generation", Encoding.UTF8.GetString(finding.ToCanonicalBytes()), StringComparison.Ordinal); // generation 0 bytes unchanged (A-3)
    }

    [Fact]
    public void RecordsAreCanonicalAndStrict()
    {
        var commitment = new DPlusEvidenceCommitment(DPlusEvidenceCommitment.SchemaV1, Guid.CreateVersion7(), Audit, 41, 0, "i", Guid.CreateVersion7(), new string('a', 64), null, "p", "Absent", null, "v");
        var bytes = DPlusRecoveryCodec.Serialize(commitment);
        Assert.Equal(commitment, DPlusRecoveryCodec.TryParse<DPlusEvidenceCommitment>(bytes));
        Assert.Null(DPlusRecoveryCodec.TryParse<DPlusEvidenceCommitment>([.. bytes, (byte)' ']));
        Assert.Null(DPlusRecoveryCodec.TryParse<DPlusEvidenceCommitment>(Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(bytes).Replace("{", "{\"extra\":1,", StringComparison.Ordinal))));
        Assert.DoesNotContain("time", Encoding.UTF8.GetString(bytes), StringComparison.OrdinalIgnoreCase); // no time: retries are byte-identical
    }
}

/// <summary>SEC-BL-01 G14-R2 recovery suite on the audit stream (unchanged scope).</summary>
public sealed class DPlusRecoveryAuditTests : DPlusRecoveryTests
{
    protected override string Stream => JournalStreamNames.Audit;
}

/// <summary>SEC-BL-01 WP-5b (C-157 P2-D4, A-6 §26): the same recovery suite on the content stream.</summary>
public sealed class DPlusRecoveryContentTests : DPlusRecoveryTests
{
    protected override string Stream => JournalStreamNames.Content;
}

/// <summary>SEC-BL-01 WP-5b (C-157 P2-D4, A-6 §26): the same recovery suite on the authorization stream.</summary>
public sealed class DPlusRecoveryAuthorizationTests : DPlusRecoveryTests
{
    protected override string Stream => JournalStreamNames.Authorization;
}
