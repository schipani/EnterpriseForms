using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using AuthorizationAuthority.Application.Common;
using AuthorizationAuthority.Application.Common.Ports;
using AuthorizationAuthority.Application.Features.Journal;
using AuthorizationAuthority.Application.Features.Journal.Reconciliation;
using AuthorizationAuthority.Application.Features.Journal.Recovery;
using AuthorizationAuthority.Application.Features.Journal.Verification;
using AuthorizationAuthority.Domain.Features.Journal;
using AuthorizationAuthority.Infrastructure.Archive;
using AuthorizationAuthority.Infrastructure.Persistence;
using Npgsql;
using Xunit;

namespace AuthorizationAuthority.Tests;

/// <summary>
/// SEC-BL-01 WP-5b (C-157, amendment A-6 §26) on real stores: disposable PostgreSQL 18 (real migrations, runtime role), the Azurite archive and
/// the separately keyed Azurite journal-verification store. D+, durable stops, SR-C1 recovery, forged AuthorityDb delivery state, stream
/// isolation and the EP-5 commitment comparison for each D+-enabled stream beyond audit. Synthetic data and synthetic approver keys only.
/// </summary>
public sealed partial class JournalVerificationPostgresIntegrationTests
{
    /// <summary>The D+-enabled streams added by WP-5b (a stream not yet enabled is skipped, so the class runs at every phase).</summary>
    public static TheoryData<string> WP5BStreams => [JournalStreamNames.Content, JournalStreamNames.Authorization];

    private static void RequireEnforced(string stream) =>
        Assert.SkipUnless(AuditDeliveryEnforcement.IsEnforced(stream), stream + " is not D+-enabled in this phase");

    private static string StreamKey(Guid org, string stream, long revision) =>
        $"org/{org:D}/{stream}/{revision.ToString("D12", CultureInfo.InvariantCulture)}.json";

    private sealed record StreamRig(Guid Org, string Stream, string Container, BlobSecurityEventArchive Archive, string Instance, string StoreContainer, BlobJournalVerificationFindingStore Findings,
        SyntheticRecoveryApprovers Approvers)
    {
        public ArchiveProcessor Processor(ISecurityEventArchive? archive = null) => new(RuntimeStore(), archive ?? Archive, TimeProvider.System, new RecordingAlerts(), Findings, Durable(Container));

        public DPlusStreamRecoveryService Service() => new(RuntimeStore(), Archive, Findings, Durable(Container), TimeProvider.System);
    }

    private static async Task<StreamRig> StreamRigAsync(string stream, SyntheticRecoveryApprovers approvers)
    {
        var (org, _, container, _) = await SeedAsync(0, 0);
        var archive = Wp2Azurite.NewArchive(container);
        var identity = ArchiveIdentityCodec.NewIdentity(DateTimeOffset.UtcNow);
        Assert.Equal(ArchiveIdentityCreateStatus.Created, (await archive.CreateIdentityAsync(identity, Ct)).Status);
        var storeContainer = await PinAsync(container, identity.ArchiveInstanceId, approvers.Verifier);
        return new StreamRig(org, stream, container, archive, identity.ArchiveInstanceId, storeContainer, Wp2Azurite.NewFindingStore(storeContainer), approvers);
    }

    /// <summary>Delivers <paramref name="rows"/> genuine rows, deletes the archived head (J4) and appends one more: a durable PREDECESSOR_MISSING stop at rows + 1.</summary>
    private static async Task<(StreamRig Rig, byte[] Genuine)> StoppedStreamAsync(string stream, int rows, SyntheticRecoveryApprovers approvers)
    {
        var rig = await StreamRigAsync(stream, approvers);
        await AppendStreamAsync(rig.Org, stream, rows);
        Assert.Equal(rows, await rig.Processor().ProcessOrganizationAsync(rig.Org, Ct));
        var raw = Wp2Azurite.RawArchiveContainer(rig.Container).GetBlobClient(StreamKey(rig.Org, stream, rows));
        var genuine = (await raw.DownloadContentAsync(Ct)).Value.Content.ToArray();
        await raw.DeleteAsync(cancellationToken: Ct); // J4/J5
        await AppendStreamAsync(rig.Org, stream, 1);
        Assert.Equal(0, await rig.Processor().ProcessOrganizationAsync(rig.Org, Ct));
        Assert.Equal((ArchiveDeliveryStates.VerificationFailed, "DPLUS_PREDECESSOR_MISSING"), ((await AuditRowsAsync(rig.Org, stream))[rows].State, (await AuditRowsAsync(rig.Org, stream))[rows].Code));
        return (rig, genuine);
    }

    /// <summary>J2 with the explicit G08 trigger bypass (see the two-parameter overload).</summary>
    private static async Task AdminSqlAsync(string sql, Guid org, string stream)
    {
        await using var connection = new NpgsqlConnection(Admin);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand("SET session_replication_role = replica; " + sql, connection);
        command.Parameters.AddWithValue("org", org);
        command.Parameters.AddWithValue("stream", stream);
        await command.ExecuteNonQueryAsync(Ct);
    }

    /// <summary>WP5B-PG-01: genesis and predecessor delivery; the W2-S commitments exist in both stores; the other streams deliver alongside.</summary>
    [Theory]
    [MemberData(nameof(WP5BStreams))]
    public async Task WP5BAValidStreamDeliversWithCommitmentsInBothStores(string stream)
    {
        Require();
        RequireEnforced(stream);
        using var approvers = Approvers();
        var rig = await StreamRigAsync(stream, approvers);
        await AppendStreamAsync(rig.Org, stream, 4);
        foreach (var other in AuditDeliveryEnforcement.KnownStreams.Where(s => s != stream))
        {
            await AppendStreamAsync(rig.Org, other, 2);
        }

        Assert.Equal(8, await rig.Processor().ProcessOrganizationAsync(rig.Org, Ct));
        Assert.All(await AuditRowsAsync(rig.Org, stream), r => Assert.Equal(ArchiveDeliveryStates.Anchored, r.State));
        Assert.Equal(4, (await RawAsync(Wp2Azurite.RawArchiveContainer(rig.Container), $"meta/dplus-intents/{rig.Org:D}/{stream}/")).Count);
        Assert.Equal(4, (await RawAsync(Wp2Azurite.RawCheckpointContainer(rig.StoreContainer), $"intents/{rig.Org:D}/{stream}/")).Count);
    }

    /// <summary>
    /// WP5B-PG-02: a durable stop on real stores survives a restart and forged AuthorityDb delivery state (administrator SQL: reset to Pending, or
    /// Anchored with a fake receipt); later rows are held; the other streams continue; nothing is quarantined.
    /// </summary>
    [Theory]
    [MemberData(nameof(WP5BStreams))]
    public async Task WP5BAStopSurvivesRestartAndForgedDeliveryStateOnRealStores(string stream)
    {
        Require();
        RequireEnforced(stream);
        using var approvers = Approvers();
        var (rig, _) = await StoppedStreamAsync(stream, 3, approvers);
        await AppendStreamAsync(rig.Org, stream, 2);
        Assert.Equal(0, await rig.Processor().ProcessOrganizationAsync(rig.Org, Ct)); // restart: a new processor rediscovers the durable stop

        // J1/J2: the refused row reset to Pending, and every later row forged as Anchored.
        await AdminSqlAsync("UPDATE authority.security_journal_entries SET archive_delivery_state = 'Pending', last_archive_error_code = NULL " +
                            "WHERE organization_id = @org AND journal_stream_name = @stream AND journal_stream_revision = 4", rig.Org, stream);
        Assert.Equal(0, await rig.Processor().ProcessOrganizationAsync(rig.Org, Ct));
        Assert.Equal(ArchiveDeliveryStates.VerificationFailed, (await AuditRowsAsync(rig.Org, stream))[3].State); // re-marked from the durable records
        await AdminSqlAsync("UPDATE authority.security_journal_entries SET archive_delivery_state = 'Anchored', archive_receipt_reference = 'forged' " +
                            "WHERE organization_id = @org AND journal_stream_name = @stream AND journal_stream_revision > 4", rig.Org, stream);
        await AppendStreamAsync(rig.Org, stream, 1);
        Assert.Equal(0, await rig.Processor().ProcessOrganizationAsync(rig.Org, Ct));
        foreach (var revision in new long[] { 4, 5, 6, 7 })
        {
            Assert.False(await Wp2Azurite.RawArchiveContainer(rig.Container).GetBlobClient(StreamKey(rig.Org, stream, revision)).ExistsAsync(Ct)); // never delivered
        }

        Assert.NotEmpty(await RawAsync(Wp2Azurite.RawArchiveContainer(rig.Container), $"meta/dplus-stops/{rig.Org:D}/{stream}/"));
        Assert.False(await QuarantinedAsync(rig.Org)); // DP-11

        // Isolation: the other streams still deliver.
        foreach (var other in AuditDeliveryEnforcement.KnownStreams.Where(s => s != stream))
        {
            await AppendStreamAsync(rig.Org, other, 1);
        }

        Assert.Equal(AuditDeliveryEnforcement.KnownStreams.Count - 1, await rig.Processor().ProcessOrganizationAsync(rig.Org, Ct));
    }

    /// <summary>WP5B-PG-03: an authorized SR-C1 recovery on real stores restores exactly the anchored bytes and delivers the stopped revision and those held behind it.</summary>
    [Theory]
    [MemberData(nameof(WP5BStreams))]
    public async Task WP5BAnAuthorizedAnchoredRestoreDeliversTheStoppedStreamOnRealStores(string stream)
    {
        Require();
        RequireEnforced(stream);
        using var approvers = Approvers();
        var (rig, genuine) = await StoppedStreamAsync(stream, 3, approvers);
        await AppendStreamAsync(rig.Org, stream, 1); // held behind the stop
        var checkpoint = DPlusRecordInspection.FirstCheckpoint(rig.Org, stream, rig.Instance, 3, SHA256.HashData(genuine), DateTimeOffset.UtcNow);
        Assert.Equal(CheckpointCreateStatus.Created, (await Durable(rig.Container).Checkpoints.CreateAsync(rig.Org, stream, 1, JournalVerificationCheckpoints.Serialize(checkpoint), Ct)).Status);

        var proposed = await rig.Service().ProposeAsync(new DPlusRecoveryProposal(rig.Org, stream, DPlusRecoveryStrategies.RestorePredecessor, "gate-requester", "gate recovery reason",
            DPlusRecoveryStrategies.PredecessorLostCause, false, null, "gate-analyst", "gate narrative", TimeSpan.FromHours(1), genuine, "gate"), Ct);
        Assert.True(proposed.Outcome == DPlusRecoveryCommandOutcome.Done, proposed.Code + ": " + proposed.Detail);
        var id = proposed.RecoveryId!.Value;
        var read = await Durable(rig.Container).Records.ReadAsync(DPlusRecoveryNames.StoreRecoveryPrefix + DPlusRecoveryNames.Statement(rig.Org, stream, id), Ct);
        var binding = SyntheticRecoveryApprovers.Binding(DPlusRecoveryCodec.TryParse<DPlusRecoveryStatement>(read.Bytes)!, DPlusRecoveryCodec.Sha256Hex(read.Bytes!));
        Assert.Equal(DPlusRecoveryCommandOutcome.Done, (await rig.Service().RecordDecisionAsync(rig.Org, stream, id, approvers.Sign("approver-a", binding, DateTimeOffset.UtcNow), Ct)).Outcome);
        Assert.Equal(DPlusRecoveryRefusals.NotAuthorized, (await rig.Service().ExecuteAsync(rig.Org, stream, id, "gate-executor", Ct)).Code); // one approval
        Assert.Equal(DPlusRecoveryCommandOutcome.Done, (await rig.Service().RecordDecisionAsync(rig.Org, stream, id, approvers.Sign("approver-b", binding, DateTimeOffset.UtcNow), Ct)).Outcome);
        var executed = await rig.Service().ExecuteAsync(rig.Org, stream, id, "gate-executor", Ct);
        Assert.True(executed.Outcome == DPlusRecoveryCommandOutcome.Done, executed.Code + ": " + executed.Detail);

        Assert.Equal(2, await rig.Processor().ProcessOrganizationAsync(rig.Org, Ct));
        Assert.All(await AuditRowsAsync(rig.Org, stream), r => Assert.Equal(ArchiveDeliveryStates.Anchored, r.State));
        Assert.Equal(genuine, (await Wp2Azurite.RawArchiveContainer(rig.Container).GetBlobClient(StreamKey(rig.Org, stream, 3)).DownloadContentAsync(Ct)).Value.Content.ToArray());
        Assert.Equal("COMPLETED", (await rig.Service().RecordOutcomeAsync(rig.Org, stream, id, Ct)).Code);
        Assert.False(await QuarantinedAsync(rig.Org));
    }

    /// <summary>WP5B-PG-04 (EP-5): the verifier compares real D+ commitments with the archive: clean after genuine delivery; a replaced store copy (J6) is reported.</summary>
    [Theory]
    [MemberData(nameof(WP5BStreams))]
    public async Task WP5BTheVerifierComparesRealCommitmentsWithTheArchive(string stream)
    {
        Require();
        RequireEnforced(stream);
        using var approvers = Approvers();
        var rig = await StreamRigAsync(stream, approvers);
        await AppendStreamAsync(rig.Org, stream, 5);
        Assert.Equal(5, await rig.Processor().ProcessOrganizationAsync(rig.Org, Ct));
        var reader = SharedRuntimeReader.Value; // WP-7A: one pool for the run (per-test readers were never disposed)
        var checkpoints = Wp2Azurite.NewCheckpointStore(rig.StoreContainer);
        var records = Wp2Azurite.NewRecordStore(rig.StoreContainer);
        var verifier = new JournalVerifier(reader, rig.Archive, checkpoints, rig.Findings, Wp2Azurite.NewExceptionStore(rig.StoreContainer), new RecordingAlerts(), TimeProvider.System,
            new JournalVerifierOptions(), records);
        var baseline = await new JournalBaselineService(reader, rig.Archive, checkpoints, Wp2Azurite.NewExceptionStore(rig.StoreContainer), TimeProvider.System)
            .RunAsync(JournalVerificationWorld.BaselineRequest(rig.Org, stream, createSentinel: false), Ct);
        Assert.Equal(nameof(JournalBaselineOutcome.Clean), baseline.Outcome);

        await AppendStreamAsync(rig.Org, stream, 2);
        Assert.Equal(2, await rig.Processor().ProcessOrganizationAsync(rig.Org, Ct));
        var clean = await verifier.SweepAsync(rig.Org, stream, JournalSweepKind.Full, Ct);
        Assert.DoesNotContain(clean.Findings, f => f.FindingCode == JournalFindingCodes.DPlusCommitmentMismatch);

        // J6 replaces the store copy of revision 6's commitment (create-only is client-side; the raw container permits it).
        var name = DPlusRecoveryNames.StoreCommitmentPrefix + DPlusRecoveryNames.Commitment(rig.Org, stream, 6, 0);
        var original = DPlusRecoveryCodec.TryParse<DPlusEvidenceCommitment>((await records.ReadAsync(name, Ct)).Bytes)!;
        await Wp2Azurite.RawCheckpointContainer(rig.StoreContainer).GetBlobClient(name).UploadAsync(BinaryData.FromBytes(DPlusRecoveryCodec.Serialize(original with { RowBodySha256Hex = new string('f', 64) })),
            overwrite: true, Ct);
        var tampered = await verifier.SweepAsync(rig.Org, stream, JournalSweepKind.Full, Ct);
        Assert.Contains(tampered.Findings, f => f.FindingCode == JournalFindingCodes.DPlusCommitmentMismatch && f.Subtype == DPlusCommitmentInspection.ConflictingCopies && f.FirstRevision == 6);
    }

    /// <summary>The real organization gate over the real AuthorityStore (runtime role) with the real trusted receipts of the rig's archive and stores.</summary>
    private static async Task<AuthorizationAuthority.Contracts.V1.ProjectionGateState> RealGateAsync(StreamRig rig)
    {
        var org = rig.Org;
        await using var scope = await ReceiptStore(rig.Container, rig.Archive).BeginScopeAsync(Ct);
        var state = (await scope.LockOrganizationAsync(org, Ct))!;
        return await OrganizationGate.EvaluateAsync(scope, state, Ct);
    }

    /// <summary>
    /// WP5B-PG-05 (RD-4 on real stores): the real organization gate over the real AuthorityStore (runtime role) stays AwaitingArchive while an
    /// authorization-stream stop holds the refused entry and a later one; an authorized SR-C1 recovery changes no organization state; the gate
    /// reopens only when every authorization entry is delivered.
    /// </summary>
    [Fact]
    public async Task WP5BTheRealGateStaysClosedUntilEveryAuthorizationEntryIsDelivered()
    {
        Require();
        RequireEnforced(JournalStreamNames.Authorization);
        using var approvers = Approvers();
        const string stream = JournalStreamNames.Authorization;
        var (rig, genuine) = await StoppedStreamAsync(stream, 3, approvers);
        Assert.Equal(AuthorizationAuthority.Contracts.V1.ProjectionGateState.AwaitingArchive, await RealGateAsync(rig));
        await AppendStreamAsync(rig.Org, stream, 1); // a later change (e.g. a revocation) committed during containment
        Assert.Equal(0, await rig.Processor().ProcessOrganizationAsync(rig.Org, Ct));
        Assert.Equal(AuthorizationAuthority.Contracts.V1.ProjectionGateState.AwaitingArchive, await RealGateAsync(rig));

        await using (var context = AdminContext())
        {
            var before = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.SingleAsync(context.OrganizationStates, s => s.OrganizationId == rig.Org, Ct);
            var snapshot = (before.AuthorizationRevision, before.ProjectionAppliedRevision, before.ProjectionZedToken, before.IntegrityQuarantineEnabled);
            var checkpoint = DPlusRecordInspection.FirstCheckpoint(rig.Org, stream, rig.Instance, 3, SHA256.HashData(genuine), DateTimeOffset.UtcNow);
            Assert.Equal(CheckpointCreateStatus.Created, (await Durable(rig.Container).Checkpoints.CreateAsync(rig.Org, stream, 1, JournalVerificationCheckpoints.Serialize(checkpoint), Ct)).Status);
            var proposed = await rig.Service().ProposeAsync(new DPlusRecoveryProposal(rig.Org, stream, DPlusRecoveryStrategies.RestorePredecessor, "gate-requester", "gate recovery reason",
                DPlusRecoveryStrategies.PredecessorLostCause, false, null, "gate-analyst", "gate narrative", TimeSpan.FromHours(1), genuine, "gate"), Ct);
            Assert.True(proposed.Outcome == DPlusRecoveryCommandOutcome.Done, proposed.Code + ": " + proposed.Detail);
            var id = proposed.RecoveryId!.Value;
            var read = await Durable(rig.Container).Records.ReadAsync(DPlusRecoveryNames.StoreRecoveryPrefix + DPlusRecoveryNames.Statement(rig.Org, stream, id), Ct);
            var binding = SyntheticRecoveryApprovers.Binding(DPlusRecoveryCodec.TryParse<DPlusRecoveryStatement>(read.Bytes)!, DPlusRecoveryCodec.Sha256Hex(read.Bytes!));
            foreach (var approver in new[] { "approver-a", "approver-b" })
            {
                Assert.Equal(DPlusRecoveryCommandOutcome.Done, (await rig.Service().RecordDecisionAsync(rig.Org, stream, id, approvers.Sign(approver, binding, DateTimeOffset.UtcNow), Ct)).Outcome);
            }

            var executed = await rig.Service().ExecuteAsync(rig.Org, stream, id, "gate-executor", Ct);
            Assert.True(executed.Outcome == DPlusRecoveryCommandOutcome.Done, executed.Code + ": " + executed.Detail);
            Assert.Equal(AuthorizationAuthority.Contracts.V1.ProjectionGateState.AwaitingArchive, await RealGateAsync(rig));
            await using var after = AdminContext();
            var state = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.SingleAsync(after.OrganizationStates, s => s.OrganizationId == rig.Org, Ct);
            Assert.Equal(snapshot, (state.AuthorizationRevision, state.ProjectionAppliedRevision, state.ProjectionZedToken, state.IntegrityQuarantineEnabled));
        }

        Assert.Equal(2, await rig.Processor().ProcessOrganizationAsync(rig.Org, Ct));
        Assert.All(await AuditRowsAsync(rig.Org, stream), r => Assert.Equal(ArchiveDeliveryStates.Anchored, r.State));
        Assert.Equal(AuthorizationAuthority.Contracts.V1.ProjectionGateState.Ready, await RealGateAsync(rig));
    }
}
