using AuthorizationAuthority.Application.Common.Ports;
using AuthorizationAuthority.Application.Features.Content;
using AuthorizationAuthority.Application.Features.Journal;
using AuthorizationAuthority.Domain.Features.Journal;
using AuthorizationAuthority.Infrastructure.Archive;
using Microsoft.Extensions.Options;
using Xunit;

namespace AuthorizationAuthority.Tests;

/// <summary>
/// OBS-1 (SEC-BL-01). Before WP-4A the delivery methods of <see cref="BlobSecurityEventArchive"/> (<c>AppendAsync</c>, <c>ReadAsync</c>) let the
/// <see cref="AggregateException"/> the Azure SDK throws when its retries end with mixed failures escape (executed 2026-11-17 at 3 s and 8 s
/// timeouts; record <c>SEC_BL_01_WP3_WP4_IMPLEMENTATION_RECORD.md</c> §7). WP-4A maps it to <see cref="ArchiveUnavailableException"/>; these
/// tests are the regression: against a real unreachable endpoint (opt-in, timing-dependent, <c>SECBL01_OBS1_CHARACTERIZE=1</c>) both methods
/// now report unavailability and <see cref="ArchiveProcessor"/> keeps the entry Pending without quarantine; the deterministic recovery test
/// shows the next pass delivers in order once the archive is reachable again.
/// </summary>
public sealed class ArchiveDeliveryOutageCharacterizationTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static void RequireOptIn() => Assert.SkipWhen(Environment.GetEnvironmentVariable("SECBL01_OBS1_CHARACTERIZE") != "1", "SECBL01_OBS1_CHARACTERIZE=1 not set (timing-dependent real-endpoint test)");

    private static BlobSecurityEventArchive Unreachable(int seconds) => new(Options.Create(new ArchiveOptions
    {
        Provider = "Blob", BlobServiceUri = "http://127.0.0.1:9/obs1account", AccountName = "obs1account", AccountKey = Convert.ToBase64String(new byte[64]), TimeoutSeconds = seconds,
    }));

    [Theory]
    [InlineData(3)]
    [InlineData(8)]
    [Trait("OBS-1", "Regression")]
    public async Task TheDeliveryReadAndAppendReportUnavailabilityWhereTheAggregateExceptionEscapedBefore(int timeoutSeconds)
    {
        RequireOptIn();
        var archive = Unreachable(timeoutSeconds);
        await Assert.ThrowsAsync<ArchiveUnavailableException>(() => archive.ReadAsync(Guid.NewGuid(), JournalStreamNames.Audit, 1, Ct));
        await Assert.ThrowsAsync<ArchiveUnavailableException>(() => archive.AppendAsync(Guid.NewGuid(), JournalStreamNames.Audit, 1, "{}"u8.ToArray(), Application.Common.CanonicalJson.Digest("{}"), Ct));
    }

    [Fact]
    [Trait("OBS-1", "Regression")]
    public async Task TheArchiveProcessorKeepsTheEntryPendingWithoutQuarantineAgainstAnUnreachableArchive()
    {
        RequireOptIn();
        var world = new CapabilityRefusalEvidenceWorld();
        var org = world.AddOrganization();
        Assert.Equal(CapabilityRefusalEvidenceOutcome.Committed,
            await new CapabilityRefusalEvidenceRecorder(world.Store, world.Clock).RecordAsync(new CapabilityRefusalContext(org, 1), CapabilityRefusalEvidence.EntryPoints.Prepare, CancellationToken.None));
        var entry = Assert.Single(world.Journal);

        Assert.Equal(0, await new ArchiveProcessor(world.Store, Unreachable(8), world.Clock, durability: world.DPlus.Durability).ProcessOrganizationAsync(org, Ct)); // no exception escapes
        Assert.Equal(ArchiveDeliveryStates.Pending, entry.ArchiveDeliveryState);
        Assert.NotNull(entry.LastArchiveErrorCode);
        Assert.False(world.Organizations[org].IntegrityQuarantineEnabled);
    }

    [Fact]
    [Trait("OBS-1", "Regression")]
    public async Task AfterAnOutageTheNextPassDeliversThePendingEntriesInOrderWithoutQuarantineOrFalseSuccess()
    {
        var world = new CapabilityRefusalEvidenceWorld();
        var org = world.AddOrganization();
        var recorder = new CapabilityRefusalEvidenceRecorder(world.Store, world.Clock);
        foreach (var entryPoint in new[] { CapabilityRefusalEvidence.EntryPoints.Prepare, CapabilityRefusalEvidence.EntryPoints.Commit })
        {
            world.Clock.Advance(TimeSpan.FromSeconds(1));
            Assert.Equal(CapabilityRefusalEvidenceOutcome.Committed, await recorder.RecordAsync(new CapabilityRefusalContext(org, 1), entryPoint, CancellationToken.None));
        }

        var archive = new SwitchingArchive();
        await world.ProvisionArchiveAsync(archive.Inner); // WP-5a: sentinel and the archived predecessor, provisioned behind the outage
        var processor = new ArchiveProcessor(world.Store, archive, world.Clock, durability: world.DPlus.Durability);
        Assert.Equal(0, await processor.ProcessOrganizationAsync(org, Ct)); // outage: nothing delivered, nothing claimed
        Assert.All(world.Journal, e => Assert.Equal(ArchiveDeliveryStates.Pending, e.ArchiveDeliveryState));
        Assert.Empty(archive.Appended);

        archive.Available = true;
        Assert.Equal(2, await processor.ProcessOrganizationAsync(org, Ct));
        Assert.All(world.Journal, e => Assert.Equal(ArchiveDeliveryStates.Anchored, e.ArchiveDeliveryState));
        var pending = world.Journal.OrderBy(e => e.JournalStreamRevision).Select(e => e.JournalStreamRevision).ToList();
        Assert.Equal(2, pending.Count);
        Assert.Equal(pending, archive.Appended); // delivery order preserved (ascending stream revision)
        Assert.False(world.Organizations[org].IntegrityQuarantineEnabled);
    }

    /// <summary>Reports unavailability exactly as the corrected Blob adapter does until <see cref="Available"/>, then delegates.</summary>
    private sealed class SwitchingArchive : ISecurityEventArchive, ISecurityEventArchiveInventory, IArchiveMetaRecords
    {
        private readonly InMemorySecurityEventArchive inner = new();

        public InMemorySecurityEventArchive Inner => inner;

        public bool Available { get; set; }

        public List<long> Appended { get; } = [];

        public string ArchiveName => "Switching";

        public async Task<ArchiveReceipt> AppendAsync(Guid organizationId, string streamName, long streamRevision, ReadOnlyMemory<byte> eventBody, byte[] expectedDigest, CancellationToken cancellationToken)
        {
            if (!Available)
            {
                throw new ArchiveUnavailableException("archive write failed after retries", new AggregateException(new InvalidOperationException("refused")));
            }

            Appended.Add(streamRevision);
            return await inner.AppendAsync(organizationId, streamName, streamRevision, eventBody, expectedDigest, cancellationToken);
        }

        public Task<(bool Ready, string Detail)> ProbeAsync(CancellationToken cancellationToken) => Task.FromResult((Available, "switching"));

        public Task<byte[]?> ReadAsync(Guid organizationId, string streamName, long streamRevision, CancellationToken cancellationToken) =>
            Available ? inner.ReadAsync(organizationId, streamName, streamRevision, cancellationToken) : throw new ArchiveUnavailableException("archive read failed after retries");

        // WP-5a: the read-side inventory D+ uses, unavailable during the outage exactly like the delivery methods.
        public Task<ArchiveIdentityRead> ReadIdentityAsync(CancellationToken cancellationToken) => Available ? inner.ReadIdentityAsync(cancellationToken) : throw new ArchiveUnavailableException("archive read failed after retries");

        public Task<ArchiveIdentityCreateResult> CreateIdentityAsync(ArchiveIdentity identity, CancellationToken cancellationToken) => Available ? inner.CreateIdentityAsync(identity, cancellationToken) : throw new ArchiveUnavailableException("archive write failed after retries");

        public Task<StoredObjectRead> ReadEntryAsync(Guid organizationId, string streamName, long streamRevision, CancellationToken cancellationToken) =>
            Available ? inner.ReadEntryAsync(organizationId, streamName, streamRevision, cancellationToken) : throw new ArchiveUnavailableException("archive read failed after retries");

        public Task<ArchiveListingPage> ListEntriesAsync(Guid organizationId, string? streamName, string? continuationToken, int pageSizeHint, CancellationToken cancellationToken) =>
            Available ? inner.ListEntriesAsync(organizationId, streamName, continuationToken, pageSizeHint, cancellationToken) : throw new ArchiveUnavailableException("archive listing failed after retries");

        public Task<ArchiveListingPage> ListEntriesAboveAsync(Guid organizationId, string streamName, long afterRevision, string? continuationToken, int pageSizeHint, CancellationToken cancellationToken) =>
            Available ? inner.ListEntriesAboveAsync(organizationId, streamName, afterRevision, continuationToken, pageSizeHint, cancellationToken) : throw new ArchiveUnavailableException("archive listing failed after retries");

        public Task<ArchiveContentSummary> SummarizeAsync(CancellationToken cancellationToken) => Available ? inner.SummarizeAsync(cancellationToken) : throw new ArchiveUnavailableException("archive listing failed after retries");

        public Task<CheckpointCreateResult> CreateStopMarkerAsync(Guid organizationId, string streamName, long failedRevision, ReadOnlyMemory<byte> markerBytes, CancellationToken cancellationToken) =>
            Available ? inner.CreateStopMarkerAsync(organizationId, streamName, failedRevision, markerBytes, cancellationToken) : throw new ArchiveUnavailableException("archive write failed after retries");

        public Task<IReadOnlyList<ArchiveMetaObject>> ReadStopMarkersAsync(Guid organizationId, string streamName, CancellationToken cancellationToken) =>
            Available ? inner.ReadStopMarkersAsync(organizationId, streamName, cancellationToken) : throw new ArchiveUnavailableException("archive listing failed after retries");

        public Task<IReadOnlyList<string>> ListMetaObjectNamesAsync(CancellationToken cancellationToken) =>
            Available ? inner.ListMetaObjectNamesAsync(cancellationToken) : throw new ArchiveUnavailableException("archive listing failed after retries");

        // G14-R2 (A-4 §24): the meta/ records (W2-S commitments, recovery records), unavailable during the outage like everything else.
        public Task<CheckpointCreateResult> CreateMetaAsync(string metaObjectName, ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken) =>
            Available ? inner.CreateMetaAsync(metaObjectName, bytes, cancellationToken) : throw new ArchiveUnavailableException("archive write failed after retries");

        public Task<StoredObjectRead> ReadMetaAsync(string metaObjectName, CancellationToken cancellationToken) =>
            Available ? inner.ReadMetaAsync(metaObjectName, cancellationToken) : throw new ArchiveUnavailableException("archive read failed after retries");

        public Task<IReadOnlyList<ArchiveMetaObject>> ListMetaAsync(string metaPrefix, CancellationToken cancellationToken) =>
            Available ? inner.ListMetaAsync(metaPrefix, cancellationToken) : throw new ArchiveUnavailableException("archive listing failed after retries");
    }
}
