using System.Text;
using AuthorizationAuthority.Application.Common.Ports;
using AuthorizationAuthority.Application.Features.Journal;
using AuthorizationAuthority.Application.Features.Journal.Verification;
using AuthorizationAuthority.Domain.Features.Journal;
using AuthorizationAuthority.Domain.Features.Organizations;
using AuthorizationAuthority.Infrastructure.Archive;
using Azure.Storage;
using Azure.Storage.Blobs;
using Microsoft.Extensions.Options;
using Xunit;

namespace AuthorizationAuthority.Tests;

/// <summary>Settings of the two throwaway Azurite instances started by <c>scripts/dev/sec-bl-01-wp2-azurite-integration.ps1</c>.</summary>
internal static class Wp2Azurite
{
    public static string? ArchiveUri => Environment.GetEnvironmentVariable("SECBL01_ARCHIVE_BLOB_URI");

    public static string? ArchiveAccount => Environment.GetEnvironmentVariable("SECBL01_ARCHIVE_ACCOUNT");

    public static string? ArchiveKey => Environment.GetEnvironmentVariable("SECBL01_ARCHIVE_KEY");

    public static string? CheckpointUri => Environment.GetEnvironmentVariable("SECBL01_CHECKPOINT_BLOB_URI");

    public static string? CheckpointAccount => Environment.GetEnvironmentVariable("SECBL01_CHECKPOINT_ACCOUNT");

    public static string? CheckpointKey => Environment.GetEnvironmentVariable("SECBL01_CHECKPOINT_KEY");

    public static string? Phase => Environment.GetEnvironmentVariable("SECBL01_PHASE");

    public const string PersistenceContainer = "wp2-persistence";

    public static readonly Guid PersistenceOrganization = Guid.Parse("019a5b2c-5ec1-7b01-8000-0000000000f9");

    public static void Require() => Assert.SkipWhen(
        new[] { ArchiveUri, ArchiveAccount, ArchiveKey, CheckpointUri, CheckpointAccount, CheckpointKey }.Any(string.IsNullOrEmpty),
        "SECBL01_* not set (run scripts/dev/sec-bl-01-wp2-azurite-integration.ps1)");

    public static ArchiveOptions Archive(string container, string? uri = null, string? key = null) =>
        new() { Provider = "Blob", BlobServiceUri = uri ?? ArchiveUri, AccountName = ArchiveAccount, AccountKey = key ?? ArchiveKey, ContainerName = container, TimeoutSeconds = 3 };

    public static VerificationCheckpointStoreOptions Checkpoints(string container) =>
        new() { Provider = "Blob", BlobServiceUri = CheckpointUri, AccountName = CheckpointAccount, AccountKey = CheckpointKey, ContainerName = container, TimeoutSeconds = 3 };

    public static BlobSecurityEventArchive NewArchive(string container) => new(Options.Create(Archive(container)));

    public static BlobVerificationCheckpointStore NewCheckpointStore(string container) =>
        new(Options.Create(Checkpoints(container)), Options.Create(Archive("security-journal")));

    public static BlobArchiveProvisioningStore NewProvisioningStore(string container) =>
        new(Options.Create(Checkpoints(container)), Options.Create(Archive("security-journal")));

    public static BlobJournalVerificationFindingStore NewFindingStore(string container, string? uri = null)
    {
        var options = Checkpoints(container);
        options.BlobServiceUri = uri ?? options.BlobServiceUri;
        return new(Options.Create(options), Options.Create(Archive("security-journal")));
    }

    public static BlobContainerClient RawCheckpointContainer(string container) =>
        new BlobServiceClient(new Uri(CheckpointUri!), new StorageSharedKeyCredential(CheckpointAccount, CheckpointKey)).GetBlobContainerClient(container);

    /// <summary>G14-R2: recovery records and W2-S commitments in the verification store's container.</summary>
    public static BlobJournalVerificationRecordStore NewRecordStore(string container, string? uri = null)
    {
        var options = Checkpoints(container);
        options.BlobServiceUri = uri ?? options.BlobServiceUri;
        return new(Options.Create(options), Options.Create(Archive("security-journal")));
    }

    public static BlobJournalAcceptedExceptionStore NewExceptionStore(string container) =>
        new(Options.Create(Checkpoints(container)), Options.Create(Archive("security-journal")));

    public static BlobContainerClient RawArchiveContainer(string container) =>
        new BlobServiceClient(new Uri(ArchiveUri!), new StorageSharedKeyCredential(ArchiveAccount, ArchiveKey)).GetBlobContainerClient(container);

    public static string NewContainerName() => "t" + Guid.NewGuid().ToString("N");

    public sealed record TestBody(string NoteText);

    public static List<SecurityJournalEntry> Entries(Guid organization, string stream, int count)
    {
        var state = new OrganizationAuthorizationState { OrganizationId = organization };
        var clock = new ManualClock(new DateTimeOffset(2026, 11, 17, 13, 0, 0, TimeSpan.Zero));
        return [.. Enumerable.Range(1, count).Select(r => JournalChain.Append(state, stream, r, "TestEvent", new TestBody("entry " + r), null, null, clock))];
    }

    public static Task DeliverAsync(ISecurityEventArchive archive, SecurityJournalEntry entry, CancellationToken cancellationToken) =>
        archive.AppendAsync(entry.OrganizationId, entry.JournalStreamName, entry.JournalStreamRevision, Encoding.UTF8.GetBytes(entry.JournalEventBodyJson), entry.JournalEventDigest, cancellationToken);
}

/// <summary>
/// SEC-BL-01 WP-2 (C-155): the Blob archive inventory and the Blob checkpoint store against two real, separately keyed, throwaway Azurite
/// instances (Development/Testing only; not independent, not production evidence). Skips unless the script sets <c>SECBL01_*</c>.
/// </summary>
public sealed class JournalVerificationAzuriteIntegrationTests
{
    private static readonly Guid Org = Guid.Parse("019a5b2c-5ec1-7b01-8000-0000000000f1");
    private static readonly Guid OtherOrg = Guid.Parse("019a5b2c-5ec1-7b01-8000-0000000000f2");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task AContainerThatDoesNotExistIsReportedAsContainerAbsentAndNeverAsAnAbsentObject()
    {
        Wp2Azurite.Require();
        var archive = Wp2Azurite.NewArchive(Wp2Azurite.NewContainerName());
        Assert.Equal(StoredObjectStatus.ContainerAbsent, (await archive.ReadEntryAsync(Org, JournalStreamNames.Audit, 1, Ct)).Status);
        Assert.Equal(StoredObjectStatus.ContainerAbsent, (await archive.ReadIdentityAsync(Ct)).Status);
        var page = await archive.ListEntriesAsync(Org, null, null, 10, Ct);
        Assert.True(page.ContainerAbsent);
        Assert.Empty(page.Objects);
        Assert.True(ArchiveCompleteness.Analyze(await ArchiveCompleteness.CollectAsync(archive, Org, null, 10, Ct)).ContainerAbsent);
    }

    [Fact]
    public async Task TheInventoryReadsExactlyTheObjectsTheUnchangedDeliveryPathWrites()
    {
        Wp2Azurite.Require();
        var archive = Wp2Azurite.NewArchive(Wp2Azurite.NewContainerName());
        var entries = Wp2Azurite.Entries(Org, JournalStreamNames.Audit, 3);
        foreach (var entry in entries)
        {
            await Wp2Azurite.DeliverAsync(archive, entry, Ct);
        }

        var read = await archive.ReadEntryAsync(Org, JournalStreamNames.Audit, 2, Ct);
        Assert.Equal(StoredObjectStatus.Present, read.Status);
        Assert.Equal(Encoding.UTF8.GetBytes(entries[1].JournalEventBodyJson), read.Bytes);
        Assert.Equal(await archive.ReadAsync(Org, JournalStreamNames.Audit, 2, Ct), read.Bytes);
        Assert.Equal(StoredObjectStatus.Absent, (await archive.ReadEntryAsync(Org, JournalStreamNames.Audit, 4, Ct)).Status); // container exists now

        // The archived bytes satisfy the chain rules as a run (R-1..R-7 via archived evidence).
        var evidence = new List<JournalEvidence>();
        for (var revision = 1; revision <= 3; revision++)
        {
            evidence.Add(JournalEvidence.FromArchive(new JournalStorageKey(Org, JournalStreamNames.Audit, revision), (await archive.ReadEntryAsync(Org, JournalStreamNames.Audit, revision, Ct)).Bytes!));
        }

        Assert.True(JournalChainRules.VerifySequence(evidence, new JournalSequenceExpectation(Org, JournalStreamNames.Audit, 1, LastRevision: 3), Ct).IsValid);

        // Delivery behaviour is unchanged: identical retry idempotent, other bytes a conflict.
        await Wp2Azurite.DeliverAsync(archive, entries[0], Ct);
        await Assert.ThrowsAsync<ArchiveConflictException>(() =>
            archive.AppendAsync(Org, JournalStreamNames.Audit, 1, "{}"u8.ToArray(), Application.Common.CanonicalJson.Digest("{}"), Ct));
    }

    [Fact]
    public async Task TheSentinelIsCreateOnlyOnBlob()
    {
        Wp2Azurite.Require();
        var archive = Wp2Azurite.NewArchive(Wp2Azurite.NewContainerName());
        var first = ArchiveIdentityCodec.NewIdentity(DateTimeOffset.UtcNow);
        Assert.Equal(ArchiveIdentityCreateStatus.Created, (await archive.CreateIdentityAsync(first, Ct)).Status);
        var again = await archive.CreateIdentityAsync(ArchiveIdentityCodec.NewIdentity(DateTimeOffset.UtcNow.AddMinutes(1)), Ct);
        Assert.Equal((ArchiveIdentityCreateStatus.AlreadyExists, first), (again.Status, again.Existing));
        Assert.Equal(first, (await archive.ReadIdentityAsync(Ct)).Identity);
        Assert.Empty((await ArchiveCompleteness.CollectAsync(archive, Org, null, 10, Ct)).Objects); // the sentinel is never listed as an entry
    }

    [Fact]
    public async Task EnumerationPagesEveryObjectAndDetectsGapsUnexpectedObjectsAndObjectsBeyondTheHead()
    {
        Wp2Azurite.Require();
        var name = Wp2Azurite.NewContainerName();
        var archive = Wp2Azurite.NewArchive(name);
        foreach (var entry in Wp2Azurite.Entries(Org, JournalStreamNames.Audit, 5).Concat(Wp2Azurite.Entries(OtherOrg, JournalStreamNames.Audit, 2)))
        {
            await Wp2Azurite.DeliverAsync(archive, entry, Ct);
        }

        var raw = Wp2Azurite.RawArchiveContainer(name);
        await raw.DeleteBlobAsync($"org/{Org:D}/audit/000000000003.json", cancellationToken: Ct); // archive deletion (J4 in Development)
        await raw.UploadBlobAsync($"org/{Org:D}/audit/7.json", BinaryData.FromString("{}"), Ct); // non-canonical name
        await raw.UploadBlobAsync($"org/{Org:D}/unknown/000000000001.json", BinaryData.FromString("{}"), Ct); // unknown stream

        var listing = await ArchiveCompleteness.CollectAsync(archive, Org, null, pageSizeHint: 2, Ct);
        Assert.Equal(6, listing.Objects.Count); // 4 genuine + 2 unexpected; nothing of the other organization
        var report = ArchiveCompleteness.Analyze(listing, new Dictionary<string, long> { [JournalStreamNames.Audit] = 4 });
        var audit = report.Streams.Single(s => s.StreamName == JournalStreamNames.Audit);
        Assert.Equal(2, report.UnexpectedObjects.Count);
        Assert.Equal(new ArchiveRevisionRange(3, 3), Assert.Single(audit.Gaps));
        Assert.Equal(2, audit.HighestContiguousRevision);
        Assert.Equal(5, Assert.Single(audit.RevisionsBeyondAssumedHead));
        Assert.False(report.IsClean);
    }

    [Fact]
    public async Task AnUnreachableStoreOrARejectedCredentialIsUnavailabilityNeverAbsence()
    {
        Wp2Azurite.Require();
        var closed = new BlobSecurityEventArchive(Options.Create(Wp2Azurite.Archive("security-journal", uri: "http://127.0.0.1:9/" + Wp2Azurite.ArchiveAccount)));
        await Assert.ThrowsAsync<ArchiveUnavailableException>(() => closed.ReadEntryAsync(Org, JournalStreamNames.Audit, 1, Ct));
        await Assert.ThrowsAsync<ArchiveUnavailableException>(() => closed.ListEntriesAsync(Org, null, null, 10, Ct));

        var wrongKey = new BlobSecurityEventArchive(Options.Create(Wp2Azurite.Archive("security-journal", key: Convert.ToBase64String(new byte[64]))));
        await Assert.ThrowsAsync<ArchiveUnavailableException>(() => wrongKey.ReadIdentityAsync(Ct));
        await Assert.ThrowsAsync<ArchiveUnavailableException>(() => wrongKey.ListEntriesAsync(Org, null, null, 10, Ct));
    }

    [Fact]
    public async Task CallerCancellationPropagatesAndIsNotReportedAsUnavailability()
    {
        Wp2Azurite.Require();
        var archive = Wp2Azurite.NewArchive(Wp2Azurite.NewContainerName());
        var store = Wp2Azurite.NewCheckpointStore(Wp2Azurite.NewContainerName());
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => archive.ReadEntryAsync(Org, JournalStreamNames.Audit, 1, cancelled.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => archive.ListEntriesAsync(Org, null, null, 10, cancelled.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.ReadAsync(Org, JournalStreamNames.Audit, 1, cancelled.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.CreateAsync(Org, JournalStreamNames.Audit, 1, "{}"u8.ToArray(), cancelled.Token));
    }

    [Fact]
    public async Task TheCheckpointStoreOnItsSeparateInstanceIsCreateOnlyChainedAndDiscoverable()
    {
        Wp2Azurite.Require();
        var name = Wp2Azurite.NewContainerName();
        var store = Wp2Azurite.NewCheckpointStore(name);
        var empty = await JournalVerificationCheckpoints.DiscoverAsync(store, Org, JournalStreamNames.Audit, CheckpointDiscoveryMode.FullChain, 0, 2, Ct);
        Assert.Equal(CheckpointDiscoveryOutcome.Empty, empty.Outcome);
        Assert.True(empty.ContainerAbsent);

        var at = new DateTimeOffset(2026, 11, 17, 14, 0, 0, TimeSpan.Zero);
        var baseline = new JournalVerificationCheckpoint(Org, JournalStreamNames.Audit, 1, 2, Enumerable.Repeat((byte)0x21, 32).ToArray(), null, "archive-azurite-it",
            CheckpointWalkKind.Baseline, CheckpointAssuranceLevel.Full, [], "verifier-azurite-it", at);
        Assert.Equal(CheckpointAppendOutcome.Created, (await JournalVerificationCheckpoints.CreateNextAsync(store, empty, baseline, Ct)).Outcome);
        var head = await JournalVerificationCheckpoints.DiscoverAsync(store, Org, JournalStreamNames.Audit, CheckpointDiscoveryMode.FullChain, 0, 2, Ct);
        for (var i = 0; i < 3; i++)
        {
            var next = head.Head! with
            {
                CheckpointSequence = head.Head.CheckpointSequence + 1, VerifiedRevision = head.Head.VerifiedRevision + 1, VerifiedDigest = Enumerable.Repeat((byte)(0x40 + i), 32).ToArray(),
                PreviousCheckpointDigest = head.HeadDigest, WalkKind = CheckpointWalkKind.Incremental, VerifiedAtUtc = at.AddMinutes(5 * (i + 1)),
            };
            Assert.Equal(CheckpointAppendOutcome.Created, (await JournalVerificationCheckpoints.CreateNextAsync(store, head, next, Ct)).Outcome);
            Assert.Equal(CheckpointAppendOutcome.AlreadyStored, (await JournalVerificationCheckpoints.CreateNextAsync(store, head, next, Ct)).Outcome);
            Assert.Equal(CheckpointAppendOutcome.Conflict, (await JournalVerificationCheckpoints.CreateNextAsync(store, head, next with { VerifiedRevision = next.VerifiedRevision + 7 }, Ct)).Outcome);
            head = await JournalVerificationCheckpoints.DiscoverAsync(store, Org, JournalStreamNames.Audit, CheckpointDiscoveryMode.FullChain, 0, 2, Ct);
        }

        Assert.Equal((CheckpointDiscoveryOutcome.Head, 4L), (head.Outcome, head.Head!.CheckpointSequence));
        Assert.Equal(CheckpointDiscoveryOutcome.Regression, (await JournalVerificationCheckpoints.DiscoverAsync(store, Org, JournalStreamNames.Audit, CheckpointDiscoveryMode.HeadAndPredecessor, 5, 2, Ct)).Outcome);

        // An unexpected object in the checkpoint prefix makes discovery Invalid.
        var raw = new BlobServiceClient(new Uri(Wp2Azurite.CheckpointUri!), new StorageSharedKeyCredential(Wp2Azurite.CheckpointAccount, Wp2Azurite.CheckpointKey)).GetBlobContainerClient(name);
        await raw.UploadBlobAsync($"verification/{Org:D}/audit/5.json", BinaryData.FromString("{}"), Ct);
        Assert.Equal(CheckpointFindingCodes.UnexpectedObject, (await JournalVerificationCheckpoints.DiscoverAsync(store, Org, JournalStreamNames.Audit, CheckpointDiscoveryMode.FullChain, 0, 2, Ct)).FindingCode);
    }

    [Fact]
    public void TheCheckpointStoreRefusesToBePointedAtTheArchiveInstance()
    {
        Wp2Azurite.Require();
        var pointedAtArchive = new VerificationCheckpointStoreOptions
        {
            Provider = "Blob", BlobServiceUri = Wp2Azurite.ArchiveUri, AccountName = Wp2Azurite.ArchiveAccount, AccountKey = Wp2Azurite.ArchiveKey, ContainerName = "journal-verification",
        };
        Assert.Throws<InvalidOperationException>(() => new BlobVerificationCheckpointStore(Options.Create(pointedAtArchive), Options.Create(Wp2Azurite.Archive("security-journal"))));
        Assert.Equal("Blob(journal-verification)", Wp2Azurite.NewCheckpointStore("journal-verification").StoreName);
    }

    /// <summary>Phase 1 of the restart check: writes a fixed fixture that <see cref="JournalVerificationAzuritePersistenceTests"/> reads after the containers restart.</summary>
    [Fact]
    public async Task PersistenceFixtureIsWrittenBeforeTheRestart()
    {
        Wp2Azurite.Require();
        Assert.SkipWhen(Wp2Azurite.Phase != "before-restart", "SECBL01_PHASE is not before-restart");
        var archive = Wp2Azurite.NewArchive(Wp2Azurite.PersistenceContainer);
        await archive.CreateIdentityAsync(ArchiveIdentityCodec.NewIdentity(DateTimeOffset.UtcNow), Ct);
        foreach (var entry in Wp2Azurite.Entries(Wp2Azurite.PersistenceOrganization, JournalStreamNames.Audit, 2))
        {
            await Wp2Azurite.DeliverAsync(archive, entry, Ct);
        }

        var store = Wp2Azurite.NewCheckpointStore(Wp2Azurite.PersistenceContainer);
        var empty = await JournalVerificationCheckpoints.DiscoverAsync(store, Wp2Azurite.PersistenceOrganization, JournalStreamNames.Audit, CheckpointDiscoveryMode.FullChain, 0, 10, Ct);
        var archived = (await archive.ReadEntryAsync(Wp2Azurite.PersistenceOrganization, JournalStreamNames.Audit, 2, Ct)).Bytes!;
        var baseline = new JournalVerificationCheckpoint(Wp2Azurite.PersistenceOrganization, JournalStreamNames.Audit, 1, 2, JournalVerificationCheckpoints.Digest(archived), null,
            (await archive.ReadIdentityAsync(Ct)).Identity!.ArchiveInstanceId, CheckpointWalkKind.Baseline, CheckpointAssuranceLevel.Full, [], "verifier-azurite-it",
            new DateTimeOffset(2026, 11, 17, 15, 0, 0, TimeSpan.Zero));
        Assert.Equal(CheckpointAppendOutcome.Created, (await JournalVerificationCheckpoints.CreateNextAsync(store, empty, baseline, Ct)).Outcome);
    }
}

/// <summary>Phase 2 of the restart check: after <c>docker restart</c> of both Azurite instances, the fixture is still there and still consistent.</summary>
public sealed class JournalVerificationAzuritePersistenceTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task TheArchiveSentinelEntriesAndCheckpointSurviveARestartOfTheStores()
    {
        Wp2Azurite.Require();
        Assert.SkipWhen(Wp2Azurite.Phase != "after-restart", "SECBL01_PHASE is not after-restart");
        var archive = Wp2Azurite.NewArchive(Wp2Azurite.PersistenceContainer);
        var identity = await archive.ReadIdentityAsync(Ct);
        Assert.Equal(StoredObjectStatus.Present, identity.Status);

        var listing = await ArchiveCompleteness.CollectAsync(archive, Wp2Azurite.PersistenceOrganization, null, 10, Ct);
        var audit = ArchiveCompleteness.Analyze(listing).Streams.Single(s => s.StreamName == JournalStreamNames.Audit);
        Assert.Equal(2, audit.HighestContiguousRevision);

        var head = await JournalVerificationCheckpoints.DiscoverAsync(Wp2Azurite.NewCheckpointStore(Wp2Azurite.PersistenceContainer), Wp2Azurite.PersistenceOrganization,
            JournalStreamNames.Audit, CheckpointDiscoveryMode.FullChain, 1, 10, Ct);
        Assert.Equal(CheckpointDiscoveryOutcome.Head, head.Outcome);
        Assert.Equal(identity.Identity!.ArchiveInstanceId, head.Head!.ArchiveInstanceId);
        var archived = (await archive.ReadEntryAsync(Wp2Azurite.PersistenceOrganization, JournalStreamNames.Audit, head.Head.VerifiedRevision, Ct)).Bytes!;
        Assert.Equal(head.Head.VerifiedDigest, JournalVerificationCheckpoints.Digest(archived));
    }
}
