using System.Security.Cryptography;
using System.Text;
using AuthorizationAuthority.Application.Common.Ports;
using AuthorizationAuthority.Application.Features.Journal;
using AuthorizationAuthority.Application.Features.Journal.Reconciliation;
using AuthorizationAuthority.Application.Features.Journal.Recovery;
using AuthorizationAuthority.Application.Features.Journal.Verification;
using AuthorizationAuthority.Domain.Features.Journal;
using AuthorizationAuthority.Infrastructure.Archive;
using Azure.Storage.Blobs;
using Npgsql;
using Xunit;

namespace AuthorizationAuthority.Tests;

/// <summary>
/// SEC-BL-01 G14-R2 (contract amendment A-4, §24) on real stores: disposable PostgreSQL 18 (real migrations, runtime role), the Azurite archive
/// and the separately keyed Azurite journal-verification store, started by <c>scripts/dev/sec-bl-01-wp4-pg-azurite-gate.ps1</c>. Synthetic data
/// and synthetic approver keys only; SRT-xx refer to <c>docs/security/G14_R1_TEST_AND_ACCEPTANCE_MATRIX.md</c>. Development/Testing evidence.
/// </summary>
public sealed partial class JournalVerificationPostgresIntegrationTests
{
    private sealed record RecoveryRig(Guid Org, string Container, BlobSecurityEventArchive Archive, string Instance, string StoreContainer, BlobJournalVerificationFindingStore Findings,
        SyntheticRecoveryApprovers Approvers);

    private static async Task<RecoveryRig> SeedRecoverableAsync(int rows, SyntheticRecoveryApprovers approvers)
    {
        var (org, _, container, _) = await SeedAsync(rows, 0);
        var archive = Wp2Azurite.NewArchive(container);
        var identity = ArchiveIdentityCodec.NewIdentity(DateTimeOffset.UtcNow);
        Assert.Equal(ArchiveIdentityCreateStatus.Created, (await archive.CreateIdentityAsync(identity, Ct)).Status);
        var storeContainer = await PinAsync(container, identity.ArchiveInstanceId, approvers.Verifier);
        return new RecoveryRig(org, container, archive, identity.ArchiveInstanceId, storeContainer, Wp2Azurite.NewFindingStore(storeContainer), approvers);
    }

    private static ArchiveProcessor Processor(RecoveryRig rig, ISecurityEventArchive? archive = null, IJournalVerificationFindingStore? findings = null) =>
        new(RuntimeStore(), archive ?? rig.Archive, TimeProvider.System, new RecordingAlerts(), findings ?? rig.Findings, Durable(rig.Container));

    private static DPlusStreamRecoveryService Service(RecoveryRig rig, DPlusDurability? durability = null) =>
        new(RuntimeStore(), rig.Archive, rig.Findings, durability ?? Durable(rig.Container), TimeProvider.System);

    /// <summary>Delivers <paramref name="rows"/>, deletes the archived head (J4) and appends one more: a durable PREDECESSOR_MISSING stop at rows + 1.</summary>
    private static async Task<(RecoveryRig Rig, BinaryData Genuine)> StoppedAsync(int rows, SyntheticRecoveryApprovers approvers, bool anchor = false)
    {
        var rig = await SeedRecoverableAsync(rows, approvers);
        Assert.Equal(rows, await Processor(rig).ProcessOrganizationAsync(rig.Org, Ct));
        var raw = Wp2Azurite.RawArchiveContainer(rig.Container).GetBlobClient(Key(rig.Org, rows));
        var genuine = (await raw.DownloadContentAsync(Ct)).Value.Content;
        if (anchor)
        {
            var checkpoint = DPlusRecordInspection.FirstCheckpoint(rig.Org, JournalStreamNames.Audit, rig.Instance, rows, SHA256.HashData(genuine.ToArray()), DateTimeOffset.UtcNow);
            Assert.Equal(CheckpointCreateStatus.Created, (await Durable(rig.Container).Checkpoints.CreateAsync(rig.Org, JournalStreamNames.Audit, 1, JournalVerificationCheckpoints.Serialize(checkpoint), Ct)).Status);
        }

        await raw.DeleteAsync(cancellationToken: Ct); // J4/J5
        await AppendAsync(rig.Org, 1, 0, rig.Container);
        Assert.Equal(0, await Processor(rig).ProcessOrganizationAsync(rig.Org, Ct));
        Assert.Equal((ArchiveDeliveryStates.VerificationFailed, "DPLUS_PREDECESSOR_MISSING"), ((await AuditRowsAsync(rig.Org))[rows].State, (await AuditRowsAsync(rig.Org))[rows].Code));
        return (rig, genuine);
    }

    private static async Task<DPlusRecoveryCommandResult> ProposeAsync(RecoveryRig rig, string strategy = DPlusRecoveryStrategies.FalseAlarm, byte[]? restore = null) =>
        await Service(rig).ProposeAsync(new DPlusRecoveryProposal(rig.Org, JournalStreamNames.Audit, strategy, "gate-requester", "gate recovery reason",
            strategy == DPlusRecoveryStrategies.FalseAlarm ? "DevelopmentReset" : DPlusRecoveryStrategies.PredecessorLostCause, false, null, "gate-analyst", "gate narrative",
            TimeSpan.FromHours(1), restore, "gate"), Ct);

    private static async Task<DPlusRecoveryStatement> StatementAsync(RecoveryRig rig, Guid id)
    {
        var read = await Durable(rig.Container).Records.ReadAsync(DPlusRecoveryNames.StoreRecoveryPrefix + DPlusRecoveryNames.Statement(rig.Org, JournalStreamNames.Audit, id), Ct);
        return DPlusRecoveryCodec.TryParse<DPlusRecoveryStatement>(read.Bytes)!;
    }

    private static async Task DecideAsync(RecoveryRig rig, Guid id, string approver, string decision = DPlusRecoveryClaims.Approve)
    {
        var read = await Durable(rig.Container).Records.ReadAsync(DPlusRecoveryNames.StoreRecoveryPrefix + DPlusRecoveryNames.Statement(rig.Org, JournalStreamNames.Audit, id), Ct);
        var statement = DPlusRecoveryCodec.TryParse<DPlusRecoveryStatement>(read.Bytes)!;
        var token = rig.Approvers.Sign(approver, SyntheticRecoveryApprovers.Binding(statement, DPlusRecoveryCodec.Sha256Hex(read.Bytes!)), DateTimeOffset.UtcNow, decision);
        Assert.Equal(DPlusRecoveryCommandOutcome.Done, (await Service(rig).RecordDecisionAsync(rig.Org, JournalStreamNames.Audit, id, token, Ct)).Outcome);
    }

    private static async Task<Guid> ExecutedAsync(RecoveryRig rig, string strategy = DPlusRecoveryStrategies.FalseAlarm, byte[]? restore = null)
    {
        var proposed = await ProposeAsync(rig, strategy, restore);
        Assert.True(proposed.Outcome == DPlusRecoveryCommandOutcome.Done, proposed.Code + ": " + proposed.Detail);
        var id = proposed.RecoveryId!.Value;
        await DecideAsync(rig, id, "approver-a");
        await DecideAsync(rig, id, "approver-b");
        var executed = await Service(rig).ExecuteAsync(rig.Org, JournalStreamNames.Audit, id, "gate-executor", Ct);
        Assert.True(executed.Outcome == DPlusRecoveryCommandOutcome.Done, executed.Code + ": " + executed.Detail);
        return id;
    }

    private static async Task<List<(string Name, byte[] Bytes)>> RawAsync(BlobContainerClient container, string prefix)
    {
        var result = new List<(string, byte[])>();
        await foreach (var blob in container.GetBlobsAsync(Azure.Storage.Blobs.Models.BlobTraits.None, Azure.Storage.Blobs.Models.BlobStates.None, prefix, Ct))
        {
            result.Add((blob.Name, (await container.GetBlobClient(blob.Name).DownloadContentAsync(Ct)).Value.Content.ToArray()));
        }

        return result;
    }

    private static SyntheticRecoveryApprovers Approvers() => new("approver-a", "approver-b", "approver-c");

    /// <summary>SRT-01, SRT-21, SR-AC-5: an authorized SR-A on real stores; preserved stop records; records in both stores; AuthorityDb changed only by the hint and then by delivery.</summary>
    [Fact]
    public async Task G14R2AuthorizedFalseAlarmRecoveryOnRealStores()
    {
        Require();
        using var approvers = Approvers();
        var (rig, genuine) = await StoppedAsync(3, approvers);
        await Wp2Azurite.RawArchiveContainer(rig.Container).GetBlobClient(Key(rig.Org, 3)).UploadAsync(genuine, Ct); // the predecessor reappears unchanged
        Assert.Equal(0, await Processor(rig).ProcessOrganizationAsync(rig.Org, Ct)); // A-3: still stopped
        var markersBefore = await RawAsync(Wp2Azurite.RawArchiveContainer(rig.Container), "meta/dplus-stops/");
        var findingsBefore = await RawAsync(Wp2Azurite.RawCheckpointContainer(rig.StoreContainer), "findings/");
        var rowsBefore = await AuditRowsAsync(rig.Org);

        var proposed = await ProposeAsync(rig);
        var id = proposed.RecoveryId!.Value;
        await DecideAsync(rig, id, "approver-a");
        await DecideAsync(rig, id, "approver-b");
        Assert.Equal(DPlusRecoveryCommandOutcome.Done, (await Service(rig).ExecuteAsync(rig.Org, JournalStreamNames.Audit, id, "gate-executor", Ct)).Outcome);
        var rowsAfterExecute = await AuditRowsAsync(rig.Org);
        Assert.Equal(rowsBefore.Count, rowsAfterExecute.Count);
        for (var i = 0; i < rowsBefore.Count; i++)
        {
            Assert.Equal(i == 3 ? (rowsBefore[i].Revision, ArchiveDeliveryStates.Pending, ArchiveProcessor.RecoveryAuthorizedCodePrefix + id.ToString("D")) : rowsBefore[i], rowsAfterExecute[i]); // only the hint
        }

        Assert.Equal(1, await Processor(rig).ProcessOrganizationAsync(rig.Org, Ct));
        Assert.Equal(ArchiveDeliveryStates.Anchored, (await AuditRowsAsync(rig.Org))[3].State);
        Assert.Equal("COMPLETED", (await Service(rig).RecordOutcomeAsync(rig.Org, JournalStreamNames.Audit, id, Ct)).Code);

        // Preservation: every original marker and finding byte-identical; the records exist in both stores.
        var markersAfter = await RawAsync(Wp2Azurite.RawArchiveContainer(rig.Container), "meta/dplus-stops/");
        var findingsAfter = await RawAsync(Wp2Azurite.RawCheckpointContainer(rig.StoreContainer), "findings/");
        Assert.All(markersBefore, m => Assert.Contains(markersAfter, n => n.Name == m.Name && n.Bytes.AsSpan().SequenceEqual(m.Bytes)));
        Assert.All(findingsBefore, m => Assert.Contains(findingsAfter, n => n.Name == m.Name && n.Bytes.AsSpan().SequenceEqual(m.Bytes)));
        var archived = await RawAsync(Wp2Azurite.RawArchiveContainer(rig.Container), DPlusRecoveryNames.ArchiveRecoveryPrefix + rig.Org.ToString("D"));
        var stored = await RawAsync(Wp2Azurite.RawCheckpointContainer(rig.StoreContainer), DPlusRecoveryNames.StoreRecoveryPrefix + rig.Org.ToString("D"));
        foreach (var (name, bytes) in stored)
        {
            Assert.Contains(archived, x => x.Name == DPlusRecoveryNames.ArchiveRecoveryPrefix + name[DPlusRecoveryNames.StoreRecoveryPrefix.Length..] && x.Bytes.AsSpan().SequenceEqual(bytes));
        }

        Assert.Contains(archived, x => x.Name.EndsWith("/evidence/row.json", StringComparison.Ordinal)); // row k's body preserved (archive only)
        Assert.DoesNotContain(stored, x => x.Name.EndsWith("/evidence/row.json", StringComparison.Ordinal));
        Assert.False(await QuarantinedAsync(rig.Org));
    }

    /// <summary>SRT-02, SRT-15: SR-C1 restores the absent predecessor create-only from the anchored copy; a later deletion is a generation-1 stop.</summary>
    [Fact]
    public async Task G14R2AnchoredRestoreAndAGenerationOneStopOnRealStores()
    {
        Require();
        using var approvers = Approvers();
        var (rig, genuine) = await StoppedAsync(3, approvers, anchor: true);
        await ExecutedAsync(rig, DPlusRecoveryStrategies.RestorePredecessor, genuine.ToArray());
        var raw = Wp2Azurite.RawArchiveContainer(rig.Container);
        Assert.Equal(genuine.ToArray(), (await raw.GetBlobClient(Key(rig.Org, 3)).DownloadContentAsync(Ct)).Value.Content.ToArray());
        await raw.GetBlobClient(Key(rig.Org, 3)).DeleteAsync(cancellationToken: Ct); // J4 again, before k is delivered
        Assert.Equal(0, await Processor(rig).ProcessOrganizationAsync(rig.Org, Ct));
        Assert.True(await raw.GetBlobClient(DPlusStreamStops.MarkerObjectName(rig.Org, JournalStreamNames.Audit, 4, 1)).ExistsAsync(Ct));
        Assert.True(await raw.GetBlobClient(DPlusStreamStops.MarkerObjectName(rig.Org, JournalStreamNames.Audit, 4, 0)).ExistsAsync(Ct));
        Assert.False(await raw.GetBlobClient(Key(rig.Org, 4)).ExistsAsync(Ct));
        var generationOne = DPlusStreamStops.TryParse((await raw.GetBlobClient(DPlusStreamStops.MarkerObjectName(rig.Org, JournalStreamNames.Audit, 4, 1)).DownloadContentAsync(Ct)).Value.Content.ToArray(),
            rig.Org, JournalStreamNames.Audit, 4, 1)!;
        Assert.Equal(JournalFindingCodes.DPlusRecoveryEvidenceMismatch, generationOne.FindingCode); // the expectation (restored predecessor) no longer holds

        // A mismatch stop is not SR-C1-eligible (SR-C1 applies only to PREDECESSOR_MISSING). After the predecessor is restored by the
        // operator and investigated, the generation-1 stop is recovered by its own SR-A.
        Assert.Equal(DPlusRecoveryRefusals.StrategyPreconditionFailed, (await ProposeAsync(rig, DPlusRecoveryStrategies.RestorePredecessor, genuine.ToArray())).Code);
        await raw.GetBlobClient(Key(rig.Org, 3)).UploadAsync(genuine, Ct);
        await ExecutedAsync(rig);
        Assert.Equal(1, await Processor(rig).ProcessOrganizationAsync(rig.Org, Ct));
        Assert.Equal(ArchiveDeliveryStates.Anchored, (await AuditRowsAsync(rig.Org))[3].State);
    }

    /// <summary>SRT-07, SRT-34: a rejection is final; deleting the recovery records from both real stores re-stops the stream.</summary>
    [Fact]
    public async Task G14R2RejectionIsFinalAndDeletingRecordsFromBothStoresReStopsOnRealStores()
    {
        Require();
        using var approvers = Approvers();
        var (rejected, genuineRejected) = await StoppedAsync(2, approvers);
        await Wp2Azurite.RawArchiveContainer(rejected.Container).GetBlobClient(Key(rejected.Org, 2)).UploadAsync(genuineRejected, Ct);
        var proposal = (await ProposeAsync(rejected)).RecoveryId!.Value;
        await DecideAsync(rejected, proposal, "approver-a");
        await DecideAsync(rejected, proposal, "approver-b");
        await DecideAsync(rejected, proposal, "approver-c", DPlusRecoveryClaims.Reject);
        Assert.Equal(DPlusRecoveryRefusals.Rejected, (await Service(rejected).ExecuteAsync(rejected.Org, JournalStreamNames.Audit, proposal, "gate-executor", Ct)).Code);
        Assert.Equal(0, await Processor(rejected).ProcessOrganizationAsync(rejected.Org, Ct));

        var (rig, genuine) = await StoppedAsync(2, approvers);
        await Wp2Azurite.RawArchiveContainer(rig.Container).GetBlobClient(Key(rig.Org, 2)).UploadAsync(genuine, Ct);
        var id = await ExecutedAsync(rig);
        Assert.Equal(1, await Processor(rig).ProcessOrganizationAsync(rig.Org, Ct));
        var statement = DPlusRecoveryNames.Statement(rig.Org, JournalStreamNames.Audit, id);
        await Wp2Azurite.RawCheckpointContainer(rig.StoreContainer).GetBlobClient(DPlusRecoveryNames.StoreRecoveryPrefix + statement).DeleteAsync(cancellationToken: Ct); // J6
        await AppendAsync(rig.Org, 1, 0, rig.Container);
        Assert.Equal(1, await Processor(rig).ProcessOrganizationAsync(rig.Org, Ct)); // the archive copy keeps the lift
        await Wp2Azurite.RawArchiveContainer(rig.Container).GetBlobClient(DPlusRecoveryNames.ArchiveRecoveryPrefix + statement).DeleteAsync(cancellationToken: Ct); // J4 too
        await AppendAsync(rig.Org, 1, 0, rig.Container);
        Assert.Equal(0, await Processor(rig).ProcessOrganizationAsync(rig.Org, Ct)); // fail closed
        Assert.Equal(ArchiveDeliveryStates.Pending, (await AuditRowsAsync(rig.Org))[^1].State);
    }

    /// <summary>SRT-25: two concurrent executions of the same recovery on real stores produce exactly one execution record.</summary>
    [Fact]
    public async Task G14R2ConcurrentExecutionOnRealStoresIsIdempotent()
    {
        Require();
        using var approvers = Approvers();
        var (rig, genuine) = await StoppedAsync(2, approvers);
        await Wp2Azurite.RawArchiveContainer(rig.Container).GetBlobClient(Key(rig.Org, 2)).UploadAsync(genuine, Ct);
        var id = (await ProposeAsync(rig)).RecoveryId!.Value;
        await DecideAsync(rig, id, "approver-a");
        await DecideAsync(rig, id, "approver-c");
        var results = await Task.WhenAll(Service(rig).ExecuteAsync(rig.Org, JournalStreamNames.Audit, id, "e1", Ct), Service(rig).ExecuteAsync(rig.Org, JournalStreamNames.Audit, id, "e2", Ct));
        Assert.Contains(results, r => r.Outcome == DPlusRecoveryCommandOutcome.Done);
        Assert.Single(await RawAsync(Wp2Azurite.RawCheckpointContainer(rig.StoreContainer), DPlusRecoveryNames.StoreRecoveryPrefix + DPlusRecoveryNames.StreamRoot(rig.Org, JournalStreamNames.Audit) + "executions/"));
        Assert.Equal(1, await Processor(rig).ProcessOrganizationAsync(rig.Org, Ct));
    }

    /// <summary>SRT-32, SRT-33: J1 SQL (row reset, forged hint code, forged Anchored) and J4 (archive k created directly) never lift the stop on real stores.</summary>
    [Fact]
    public async Task G14R2AuthorityDbForgeryAndDirectArchivePresenceNeverLiftOnRealStores()
    {
        Require();
        using var approvers = Approvers();
        var (rig, genuine) = await StoppedAsync(2, approvers);
        await Wp2Azurite.RawArchiveContainer(rig.Container).GetBlobClient(Key(rig.Org, 2)).UploadAsync(genuine, Ct);
        await AppendAsync(rig.Org, 1, 0, rig.Container); // revision 4 behind the stop at 3
        await AdminSqlAsync("UPDATE authority.security_journal_entries SET archive_delivery_state = 'Pending', last_archive_error_code = 'DPLUS_RECOVERY_AUTHORIZED:" + Guid.CreateVersion7().ToString("D")
            + "' WHERE organization_id = @org AND journal_stream_name = 'audit' AND journal_stream_revision = 3", rig.Org);
        Assert.Equal(0, await Processor(rig).ProcessOrganizationAsync(rig.Org, Ct));
        await AdminSqlAsync("UPDATE authority.security_journal_entries SET archive_delivery_state = 'Anchored', archive_receipt_reference = 'forged' WHERE organization_id = @org AND journal_stream_name = 'audit' AND journal_stream_revision = 3", rig.Org);
        var body = (await AuditBodiesAsync(rig.Org))[2];
        await Wp2Azurite.RawArchiveContainer(rig.Container).GetBlobClient(Key(rig.Org, 3)).UploadAsync(BinaryData.FromString(body), Ct); // J4 creates archive k
        Assert.Equal(0, await Processor(rig).ProcessOrganizationAsync(rig.Org, Ct));
        Assert.False(await Wp2Azurite.RawArchiveContainer(rig.Container).GetBlobClient(Key(rig.Org, 4)).ExistsAsync(Ct));
        Assert.False(await QuarantinedAsync(rig.Org));
    }

    /// <summary>SRT-36: an archive-instance substitution on real stores is a durable INSTANCE_CHANGED stop; recovery across instances is refused.</summary>
    [Fact]
    public async Task G14R2ArchiveInstanceSubstitutionOnRealStores()
    {
        Require();
        using var approvers = Approvers();
        var rig = await SeedRecoverableAsync(2, approvers);
        Assert.Equal(2, await Processor(rig).ProcessOrganizationAsync(rig.Org, Ct));
        var raw = Wp2Azurite.RawArchiveContainer(rig.Container);
        await raw.GetBlobClient("meta/archive-identity.json").DeleteAsync(cancellationToken: Ct); // J5 substitutes the instance
        await rig.Archive.CreateIdentityAsync(ArchiveIdentityCodec.NewIdentity(DateTimeOffset.UtcNow), Ct);
        await AppendAsync(rig.Org, 1, 0, rig.Container);
        Assert.Equal(0, await Processor(rig).ProcessOrganizationAsync(rig.Org, Ct));
        Assert.Equal((ArchiveDeliveryStates.VerificationFailed, "DPLUS_ARCHIVE_REGRESSION:INSTANCE_CHANGED"), ((await AuditRowsAsync(rig.Org))[2].State, (await AuditRowsAsync(rig.Org))[2].Code));
        Assert.Equal(DPlusRecoveryRefusals.ArchiveInstanceBlocked, (await ProposeAsync(rig)).Code);
        Assert.False(await raw.GetBlobClient(Key(rig.Org, 3)).ExistsAsync(Ct));
    }

    /// <summary>SRT-39, SRT-40, SRT-41 on real stores: commitments in both stores; the DD-10 sequence is detected; unchanged evidence re-derives the refusal.</summary>
    [Fact]
    public async Task G14R2EvidenceCommitmentsAndTheDD10SequenceOnRealStores()
    {
        Require();
        using var approvers = Approvers();
        var rig = await SeedRecoverableAsync(3, approvers);
        Assert.Equal(3, await Processor(rig).ProcessOrganizationAsync(rig.Org, Ct));
        var archived = await RawAsync(Wp2Azurite.RawArchiveContainer(rig.Container), DPlusRecoveryNames.ArchiveCommitmentPrefix + rig.Org.ToString("D"));
        var stored = await RawAsync(Wp2Azurite.RawCheckpointContainer(rig.StoreContainer), DPlusRecoveryNames.StoreCommitmentPrefix + rig.Org.ToString("D"));
        Assert.Equal(3, archived.Count);
        Assert.Equal(archived.Select(a => a.Bytes), stored.Select(s => s.Bytes));

        // The DD-10 sequence: detection, no stop record (marker write fails, finding store unreachable), termination, evidence replaced, restart.
        var raw = Wp2Azurite.RawArchiveContainer(rig.Container).GetBlobClient(Key(rig.Org, 3));
        var genuine = (await raw.DownloadContentAsync(Ct)).Value.Content;
        await raw.DeleteAsync(cancellationToken: Ct);
        await AppendAsync(rig.Org, 1, 0, rig.Container);
        var unreachable = new RecordFailingFindingStore(rig.Findings); // listing works (discovery is conclusive), writes fail
        Assert.Equal(0, await Processor(rig, new MarkerFailingArchive(rig.Archive), unreachable).ProcessOrganizationAsync(rig.Org, Ct));
        Assert.Equal(ArchiveProcessor.DurableStopNotEstablishedCode, (await AuditRowsAsync(rig.Org))[3].Code);
        Assert.Empty(await RawAsync(Wp2Azurite.RawArchiveContainer(rig.Container), "meta/dplus-stops/" + rig.Org.ToString("D")));
        await raw.UploadAsync(genuine, Ct); // J4 replaces the evidence before the restart
        Assert.Equal(0, await Processor(rig).ProcessOrganizationAsync(rig.Org, Ct)); // restart with the real stores
        Assert.Equal((ArchiveDeliveryStates.VerificationFailed, "DPLUS_" + JournalFindingCodes.DPlusEvidenceChangedAfterCommitment),
            ((await AuditRowsAsync(rig.Org))[3].State, (await AuditRowsAsync(rig.Org))[3].Code));
        Assert.False(await Wp2Azurite.RawArchiveContainer(rig.Container).GetBlobClient(Key(rig.Org, 4)).ExistsAsync(Ct));

        // SRT-41: the same termination with unchanged evidence re-derives the original refusal (no recovery needed).
        using var other = Approvers();
        var second = await SeedRecoverableAsync(2, other);
        Assert.Equal(2, await Processor(second).ProcessOrganizationAsync(second.Org, Ct));
        await Wp2Azurite.RawArchiveContainer(second.Container).GetBlobClient(Key(second.Org, 2)).DeleteAsync(cancellationToken: Ct);
        await AppendAsync(second.Org, 1, 0, second.Container);
        var down = new RecordFailingFindingStore(second.Findings);
        Assert.Equal(0, await Processor(second, new MarkerFailingArchive(second.Archive), down).ProcessOrganizationAsync(second.Org, Ct));
        Assert.Equal(ArchiveProcessor.DurableStopNotEstablishedCode, (await AuditRowsAsync(second.Org))[2].Code);
        Assert.Equal(0, await Processor(second).ProcessOrganizationAsync(second.Org, Ct));
        Assert.Equal((ArchiveDeliveryStates.VerificationFailed, "DPLUS_PREDECESSOR_MISSING"), ((await AuditRowsAsync(second.Org))[2].State, (await AuditRowsAsync(second.Org))[2].Code));
    }

    /// <summary>SRT-30: with the verification-store record area unreachable, discovery is inconclusive and the proposal unavailable on real stores.</summary>
    [Fact]
    public async Task G14R2VerificationStoreOutageIsInconclusiveOnRealStores()
    {
        Require();
        using var approvers = Approvers();
        var (rig, genuine) = await StoppedAsync(2, approvers);
        await Wp2Azurite.RawArchiveContainer(rig.Container).GetBlobClient(Key(rig.Org, 2)).UploadAsync(genuine, Ct);
        var durable = Durable(rig.Container);
        var down = new DPlusDurability(Wp2Azurite.NewRecordStore(rig.StoreContainer, uri: "http://127.0.0.1:9/" + Wp2Azurite.CheckpointAccount), durable.Provisioning, durable.Checkpoints, durable.Approvals);
        Assert.Equal(DPlusRecoveryCommandOutcome.Unavailable, (await Service(rig, down).ProposeAsync(new DPlusRecoveryProposal(rig.Org, JournalStreamNames.Audit, DPlusRecoveryStrategies.FalseAlarm,
            "gate-requester", "r", "DevelopmentReset", false, null, "gate-analyst", "n", TimeSpan.FromHours(1), null, null), Ct)).Outcome);
        await AdminSqlAsync("UPDATE authority.security_journal_entries SET archive_delivery_state = 'Pending' WHERE organization_id = @org AND journal_stream_name = 'audit' AND journal_stream_revision = 3", rig.Org);
        Assert.Equal(0, await new ArchiveProcessor(RuntimeStore(), rig.Archive, TimeProvider.System, new RecordingAlerts(), rig.Findings, down).ProcessOrganizationAsync(rig.Org, Ct));
        Assert.Equal(ArchiveProcessor.RecoveryRecordsUnavailableCode, (await AuditRowsAsync(rig.Org))[2].Code);
        Assert.False(await Wp2Azurite.RawArchiveContainer(rig.Container).GetBlobClient(Key(rig.Org, 3)).ExistsAsync(Ct));
    }

    private static async Task<List<string>> AuditBodiesAsync(Guid org)
    {
        await using var connection = new NpgsqlConnection(Admin);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand("SELECT journal_event_body_json FROM authority.security_journal_entries WHERE organization_id = @org AND journal_stream_name = 'audit' ORDER BY journal_stream_revision", connection);
        command.Parameters.AddWithValue("org", org);
        var bodies = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(Ct);
        while (await reader.ReadAsync(Ct))
        {
            bodies.Add(reader.GetString(0));
        }

        return bodies;
    }

    /// <summary>The real Blob finding store whose writes fail (listing works), so a refusal is detected but no finding becomes durable.</summary>
    private sealed class RecordFailingFindingStore(BlobJournalVerificationFindingStore inner) : IJournalVerificationFindingStore
    {
        public Task<FindingRecordStatus> RecordAsync(Guid organizationId, string streamName, string findingIdentity, ReadOnlyMemory<byte> findingBytes, CancellationToken cancellationToken) =>
            throw new CheckpointStoreUnavailableException("finding write failed (gate)");

        public Task<IReadOnlyList<byte[]>> ListAsync(Guid organizationId, string streamName, CancellationToken cancellationToken) => inner.ListAsync(organizationId, streamName, cancellationToken);
    }

    /// <summary>The real Blob archive whose stop-marker writes fail (an outage of exactly that write), every other operation delegated.</summary>
    private sealed class MarkerFailingArchive(BlobSecurityEventArchive inner) : ISecurityEventArchive, ISecurityEventArchiveInventory, IArchiveMetaRecords
    {
        public string ArchiveName => "MarkerFailing";

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
            throw new ArchiveUnavailableException("stop-marker write failed (gate)");

        public Task<IReadOnlyList<ArchiveMetaObject>> ReadStopMarkersAsync(Guid organizationId, string streamName, CancellationToken cancellationToken) => inner.ReadStopMarkersAsync(organizationId, streamName, cancellationToken);

        public Task<IReadOnlyList<string>> ListMetaObjectNamesAsync(CancellationToken cancellationToken) => inner.ListMetaObjectNamesAsync(cancellationToken);

        public Task<CheckpointCreateResult> CreateMetaAsync(string metaObjectName, ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken) =>
            metaObjectName.StartsWith(DPlusStreamStops.MarkerPrefix, StringComparison.Ordinal) ? throw new ArchiveUnavailableException("stop-marker write failed (gate)") : inner.CreateMetaAsync(metaObjectName, bytes, cancellationToken);

        public Task<StoredObjectRead> ReadMetaAsync(string metaObjectName, CancellationToken cancellationToken) => inner.ReadMetaAsync(metaObjectName, cancellationToken);

        public Task<IReadOnlyList<ArchiveMetaObject>> ListMetaAsync(string metaPrefix, CancellationToken cancellationToken) => inner.ListMetaAsync(metaPrefix, cancellationToken);
    }
}
