using System.Security.Cryptography;
using System.Text;
using AuthorizationAuthority.Application.Common;
using AuthorizationAuthority.Application.Common.Ports;
using AuthorizationAuthority.Application.Features.Content;
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
/// SEC-BL-01 WP-5b (C-157, P2-D4 content gate, RD-5): content-stream D+ end to end with the real <see cref="ContentService"/> (real canonical
/// encoding, in-process HMAC), the real <see cref="ArchiveProcessor"/> and the real recovery service over an in-memory store whose journal
/// delivery is real. A committed revision is released only once its checkpoint entry is delivered; a D+ stop keeps it unreleased across
/// restarts, forged row resets and archive outages; an authorized SR-C1 recovery delivers it without touching the content head or manifest.
/// The pre-existing E-7 limit (release trusts the row's delivery state) is characterized, not claimed closed.
/// </summary>
public sealed class ContentStreamDPlusReadabilityTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly ContentHistoryTests.Harness h = new() { AnchorAfterCommit = false };
    private readonly InMemorySecurityEventArchive archive = new();
    private readonly InMemoryJournalVerificationFindingStore findings = new();
    private readonly DPlusTestStores stores = new();
    private readonly SyntheticRecoveryApprovers approvers = new("approver-a", "approver-b", "approver-c");
    private readonly RecordingAlerts alerts = new();
    private readonly string instance;

    public ContentStreamDPlusReadabilityTests()
    {
        h.Store.RealJournalDelivery = true;
        stores.Approvals = approvers.Verifier;
        instance = stores.ProvisionAsync(archive, DateTimeOffset.UtcNow).GetAwaiter().GetResult();
        h.Store.ReceiptVerifier = new TrustedJournalReceipts(archive, stores.Durability); // E-7: release needs the trusted receipt
    }

    public void Dispose() => approvers.Dispose();

    private ArchiveProcessor Processor() => new(h.Store, archive, TimeProvider.System, alerts, findings, stores.Durability);

    private DPlusStreamRecoveryService Recovery() => new(h.Store, archive, findings, stores.Durability, TimeProvider.System);

    private List<SecurityJournalEntry> ContentRows => [.. h.Store.Journal.Where(j => j.OrganizationId == h.Org && j.JournalStreamName == JournalStreamNames.Content).OrderBy(j => j.JournalStreamRevision)];

    private Task<VerifyRecordProjectionResult> ReadHeadAsync() =>
        h.Service.VerifyProjectionAsync(ContentHistoryTests.Harness.Actor(h.Owner), new VerifyRecordProjectionRequest(h.Org, h.Record, RequestedAction.Read, h.Snapshots[h.Head.CommittedContentRevision], null),
            Guid.CreateVersion7(), Ct);

    private async Task AssertNotReleasedAsync(string because)
    {
        var refused = await Assert.ThrowsAsync<AuthorityException>(ReadHeadAsync);
        Assert.True(refused.ErrorCode == AuthorityErrorCodes.AuthorityNotReady && refused.Message == "AwaitingArchive", because + ": " + refused.ErrorCode + " " + refused.Message);
    }

    private async Task<long> PassAsync() => await Processor().ProcessOrganizationAsync(h.Org, Ct);

    private string ArchiveKey(long revision) => $"{h.Org:D}/{JournalStreamNames.Content}/{revision}";

    /// <summary>A content-stream stop at the first entry of revision 2: its predecessor (revision 1's last entry) was removed from the archive (J4).</summary>
    private async Task<(SecurityJournalEntry Predecessor, SecurityJournalEntry Failed)> StopAtRevisionTwoAsync()
    {
        await h.WriteAsync("value 1");
        await AssertNotReleasedAsync("before delivery");
        Assert.True(await PassAsync() > 0);
        Assert.True((await ReadHeadAsync()).Verified);
        var predecessor = ContentRows[^1];
        Assert.True(archive.RemoveObjectForTesting(ArchiveKey(predecessor.JournalStreamRevision))); // J4 deletes the archived entry

        await h.WriteAsync("value 2");
        Assert.Equal(0, await PassAsync());
        var failed = ContentRows.Single(r => r.JournalStreamRevision == predecessor.JournalStreamRevision + 1);
        Assert.Equal((ArchiveDeliveryStates.VerificationFailed, "DPLUS_" + JournalFindingCodes.PredecessorMissing), (failed.ArchiveDeliveryState, failed.LastArchiveErrorCode));
        return (predecessor, failed);
    }

    /// <summary>RD-5, RD-7: normal access, restart, forged row reset and archive outage never release a revision whose checkpoint D+ refused.</summary>
    [Fact]
    public async Task AStoppedContentRevisionIsNeverReleasedByRestartForgedResetOrOutage()
    {
        var (_, failed) = await StopAtRevisionTwoAsync();
        await AssertNotReleasedAsync("stopped");

        Assert.Equal(0, await PassAsync()); // a restart (new processor) rediscovers the durable stop
        await AssertNotReleasedAsync("after restart");

        failed.ArchiveDeliveryState = ArchiveDeliveryStates.Pending; // J1 resets the refused row
        failed.LastArchiveErrorCode = null;
        Assert.Equal(0, await PassAsync());
        Assert.Equal(ArchiveDeliveryStates.VerificationFailed, failed.ArchiveDeliveryState); // re-marked from the durable records
        await AssertNotReleasedAsync("after a forged reset");

        var outage = OutageArchive.Create();
        Assert.Equal(0, await new ArchiveProcessor(h.Store, outage, TimeProvider.System, alerts, findings, stores.Durability).ProcessOrganizationAsync(h.Org, Ct));
        await AssertNotReleasedAsync("during an archive outage");
        Assert.All(ContentRows.Where(r => r.JournalStreamRevision > failed.JournalStreamRevision), r => Assert.NotEqual(ArchiveDeliveryStates.Anchored, r.ArchiveDeliveryState));
        Assert.Null(await archive.ReadAsync(h.Org, JournalStreamNames.Content, failed.JournalStreamRevision, Ct));
    }

    /// <summary>RD-2, RD-3, RD-5: an authorized SR-C1 recovery delivers the stopped revision; the content head, manifest and quarantine are untouched.</summary>
    [Fact]
    public async Task AnAuthorizedAnchoredRestoreReleasesTheRevisionWithoutChangingTheContentHead()
    {
        var (predecessor, failed) = await StopAtRevisionTwoAsync();
        var predecessorBytes = Encoding.UTF8.GetBytes(predecessor.JournalEventBodyJson);
        var checkpoint = DPlusRecordInspection.FirstCheckpoint(h.Org, JournalStreamNames.Content, instance, predecessor.JournalStreamRevision, SHA256.HashData(predecessorBytes), DateTimeOffset.UtcNow);
        Assert.Equal(CheckpointCreateStatus.Created, (await stores.Checkpoints.CreateAsync(h.Org, JournalStreamNames.Content, 1, JournalVerificationCheckpoints.Serialize(checkpoint), Ct)).Status);
        var head = (h.Head.CommittedContentRevision, h.Head.CommittedOperationId, Root: Convert.ToHexString(h.Head.CommittedManifestRoot));
        var organization = (h.State.AuthorizationRevision, h.State.IntegrityQuarantineEnabled);

        // One approval, then an unconfigured key: not authorized; nothing released.
        var proposed = await Recovery().ProposeAsync(new DPlusRecoveryProposal(h.Org, JournalStreamNames.Content, DPlusRecoveryStrategies.RestorePredecessor, "requester-r", "content stop",
            DPlusRecoveryStrategies.PredecessorLostCause, false, null, "analyst-q", "narrative", TimeSpan.FromHours(1), predecessorBytes, "test"), Ct);
        Assert.True(proposed.Outcome == DPlusRecoveryCommandOutcome.Done, proposed.Code + ": " + proposed.Detail);
        var id = proposed.RecoveryId!.Value;
        var statementBytes = stores.Records.Records[DPlusRecoveryNames.StoreRecoveryPrefix + DPlusRecoveryNames.Statement(h.Org, JournalStreamNames.Content, id)];
        var binding = SyntheticRecoveryApprovers.Binding(DPlusRecoveryCodec.TryParse<DPlusRecoveryStatement>(statementBytes)!, DPlusRecoveryCodec.Sha256Hex(statementBytes));
        Assert.Equal(DPlusRecoveryCommandOutcome.Done, (await Recovery().RecordDecisionAsync(h.Org, JournalStreamNames.Content, id, approvers.Sign("approver-a", binding, DateTimeOffset.UtcNow), Ct)).Outcome);
        Assert.Equal(DPlusRecoveryRefusals.NotAuthorized, (await Recovery().ExecuteAsync(h.Org, JournalStreamNames.Content, id, "executor-e", Ct)).Code);
        Assert.Equal(0, await PassAsync());
        await AssertNotReleasedAsync("one approval");

        Assert.Equal(DPlusRecoveryCommandOutcome.Done, (await Recovery().RecordDecisionAsync(h.Org, JournalStreamNames.Content, id, approvers.Sign("approver-b", binding, DateTimeOffset.UtcNow), Ct)).Outcome);
        var executed = await Recovery().ExecuteAsync(h.Org, JournalStreamNames.Content, id, "executor-e", Ct);
        Assert.True(executed.Outcome == DPlusRecoveryCommandOutcome.Done, executed.Code + ": " + executed.Detail);
        Assert.Equal(head, (h.Head.CommittedContentRevision, h.Head.CommittedOperationId, Convert.ToHexString(h.Head.CommittedManifestRoot))); // recovery wrote no content state
        Assert.Equal(organization, (h.State.AuthorizationRevision, h.State.IntegrityQuarantineEnabled));

        Assert.True(await PassAsync() > 0);
        Assert.Equal(ArchiveDeliveryStates.Anchored, failed.ArchiveDeliveryState);
        Assert.All(ContentRows, r => Assert.Equal(ArchiveDeliveryStates.Anchored, r.ArchiveDeliveryState));
        Assert.True((await ReadHeadAsync()).Verified);
        Assert.Equal(head, (h.Head.CommittedContentRevision, h.Head.CommittedOperationId, Convert.ToHexString(h.Head.CommittedManifestRoot)));
        Assert.Equal(predecessorBytes, await archive.ReadAsync(h.Org, JournalStreamNames.Content, predecessor.JournalStreamRevision, Ct)); // exactly the anchored bytes

        // Replay: executing again changes nothing; the next revision is delivered normally.
        await Recovery().ExecuteAsync(h.Org, JournalStreamNames.Content, id, "executor-e", Ct);
        await h.WriteAsync("value 3");
        Assert.True(await PassAsync() > 0);
        Assert.True((await ReadHeadAsync()).Verified);
    }

    /// <summary>
    /// E-7 remediated (C-158, A-7 §27 CR-1..CR-3): forged delivery state never releases content. Before G08 the same attack released the revision
    /// (evidence in docs/evidence/sec-bl-01-g08/before/). The checkpoint must bind to the head and carry a trusted receipt.
    /// </summary>
    [Fact]
    public async Task E7ForgedDeliveryStateAndReceiptFieldsNeverReleaseTheRevision()
    {
        var (_, failed) = await StopAtRevisionTwoAsync();
        foreach (var row in ContentRows.Where(r => r.ArchiveDeliveryState != ArchiveDeliveryStates.Anchored))
        {
            row.ArchiveDeliveryState = ArchiveDeliveryStates.Anchored; // J1
            row.ArchiveReceiptReference = "forged-receipt";
            row.ArchiveReceiptDigest = SHA256.HashData("forged"u8);
            row.JournalArchivedOn = DateTimeOffset.UtcNow;
        }

        await AssertNotReleasedAsync("forged Anchored state");
        await h.WriteAsync("value 3");
        Assert.Equal(0, await PassAsync()); // D+ does not trust it either
        Assert.Null(await archive.ReadAsync(h.Org, JournalStreamNames.Content, failed.JournalStreamRevision, Ct));
        await AssertNotReleasedAsync("the next revision's checkpoint is refused again");
    }

    /// <summary>E-7 (CR-1): a genuinely delivered checkpoint of another record, or of an earlier revision of this record, never releases this head.</summary>
    [Fact]
    public async Task E7AReceiptOfAnotherHeadNeverReleasesThisHead()
    {
        await h.WriteAsync("value 1");
        Assert.True(await PassAsync() > 0);
        var first = ContentRows.Single(r => r.JournalEventKind == ContentCheckpointReceipts.CheckpointEventKind);
        Assert.True((await ReadHeadAsync()).Verified);

        await h.WriteAsync("value 2");
        var second = ContentRows.Last(r => r.JournalEventKind == ContentCheckpointReceipts.CheckpointEventKind);
        await AssertNotReleasedAsync("revision 2 is not delivered yet");

        // J1 points the new head's checkpoint lookup at the delivered revision-1 checkpoint (same operation column): the body still names revision 1.
        var (firstOperation, secondOperation) = (first.ContentChangeOperationId, second.ContentChangeOperationId);
        second.ContentChangeOperationId = Guid.CreateVersion7();
        first.ContentChangeOperationId = h.Head.CommittedOperationId;
        Assert.False(ContentCheckpointReceipts.Binds(first, h.Head)); // the delivered body names another revision and operation
        await AssertNotReleasedAsync("a receipt of the earlier revision");
        (first.ContentChangeOperationId, second.ContentChangeOperationId) = (firstOperation, secondOperation);
        Assert.True(await PassAsync() > 0);
        Assert.True((await ReadHeadAsync()).Verified);
    }

    /// <summary>An archive whose every operation is unavailable (an outage).</summary>
    public class OutageArchive : System.Reflection.DispatchProxy
    {
        public static ICountedArchive Create() => Create<ICountedArchive, OutageArchive>();

        protected override object? Invoke(System.Reflection.MethodInfo? targetMethod, object?[]? args)
        {
            ArgumentNullException.ThrowIfNull(targetMethod);
            var failure = new Application.Common.Ports.ArchiveUnavailableException("injected outage");
            if (targetMethod.ReturnType.IsGenericType && targetMethod.ReturnType.GetGenericTypeDefinition() == typeof(Task<>))
            {
                var fromException = typeof(Task).GetMethod(nameof(Task.FromException), 1, [typeof(Exception)])!.MakeGenericMethod(targetMethod.ReturnType.GetGenericArguments()[0]);
                return fromException.Invoke(null, [failure]);
            }

            return targetMethod.ReturnType == typeof(Task) ? Task.FromException(failure) : throw failure;
        }
    }
}
