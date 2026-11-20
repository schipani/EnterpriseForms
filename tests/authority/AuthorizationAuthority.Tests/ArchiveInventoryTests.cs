using System.Text;
using AuthorizationAuthority.Application.Common.Ports;
using AuthorizationAuthority.Application.Features.Journal;
using AuthorizationAuthority.Application.Features.Journal.Verification;
using AuthorizationAuthority.Domain.Features.Journal;
using AuthorizationAuthority.Domain.Features.Organizations;
using AuthorizationAuthority.Infrastructure.Archive;
using Xunit;

namespace AuthorizationAuthority.Tests;

/// <summary>
/// SEC-BL-01 WP-2 (C-155, contract §6): archive identity sentinel, complete enumeration, completeness analysis (gaps, duplicates,
/// unexpected objects, objects beyond an assumed head), absent versus unavailable, organization and stream isolation, cancellation.
/// In-memory and unavailable adapters; the Blob adapter's store behaviour is covered by <c>JournalVerificationAzuriteIntegrationTests</c>.
/// </summary>
public sealed class ArchiveInventoryTests
{
    private static readonly Guid Org = Guid.Parse("019a5b2c-5ec1-7b01-8000-0000000000d1");
    private static readonly Guid OtherOrg = Guid.Parse("019a5b2c-5ec1-7b01-8000-0000000000d2");
    private static readonly DateTimeOffset Created = new(2026, 11, 17, 10, 0, 0, TimeSpan.Zero);

    private static readonly long[] BeyondHeadRevisions = [3, 5];
    private static readonly long[] DuplicatedRevision = [2];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed record TestBody(string NoteText);

    /// <summary>Appends <paramref name="count"/> genuine entries through the unchanged delivery path (<see cref="ISecurityEventArchive.AppendAsync"/>).</summary>
    private static async Task<List<SecurityJournalEntry>> DeliverAsync(InMemorySecurityEventArchive archive, Guid organization, string stream, int count)
    {
        var state = new OrganizationAuthorizationState { OrganizationId = organization };
        var clock = new ManualClock(Created);
        var entries = new List<SecurityJournalEntry>();
        for (var revision = 1; revision <= count; revision++)
        {
            var entry = JournalChain.Append(state, stream, revision, "TestEvent", new TestBody("entry " + revision), null, null, clock);
            await archive.AppendAsync(organization, stream, revision, Encoding.UTF8.GetBytes(entry.JournalEventBodyJson), entry.JournalEventDigest, Ct);
            entries.Add(entry);
        }

        return entries;
    }

    private static string Key(Guid organization, string stream, long revision) => $"{organization:D}/{stream}/{revision}";

    // ---- Sentinel (AS-1..AS-4) ----

    [Fact]
    public async Task TheSentinelIsAbsentUntilCreatedAndIsCreateOnly()
    {
        var archive = new InMemorySecurityEventArchive();
        Assert.Equal(StoredObjectStatus.Absent, (await archive.ReadIdentityAsync(Ct)).Status);

        var first = ArchiveIdentityCodec.NewIdentity(Created);
        var created = await archive.CreateIdentityAsync(first, Ct);
        Assert.Equal(ArchiveIdentityCreateStatus.Created, created.Status);

        var second = await archive.CreateIdentityAsync(ArchiveIdentityCodec.NewIdentity(Created.AddMinutes(1)), Ct);
        Assert.Equal(ArchiveIdentityCreateStatus.AlreadyExists, second.Status);
        Assert.Equal(first, second.Existing);

        var read = await archive.ReadIdentityAsync(Ct);
        Assert.Equal((StoredObjectStatus.Present, first), (read.Status, read.Identity));
        Assert.Equal(ArchiveIdentityCodec.Serialize(first), read.Bytes);
    }

    [Fact]
    public async Task WipingTheArchiveRemovesTheSentinelSoAWipeIsNeverReadAsMerelyMissingObjects()
    {
        var archive = new InMemorySecurityEventArchive();
        await archive.CreateIdentityAsync(ArchiveIdentityCodec.NewIdentity(Created), Ct);
        await DeliverAsync(archive, Org, JournalStreamNames.Audit, 2);
        archive.WipeForTesting();

        Assert.Equal(StoredObjectStatus.Absent, (await archive.ReadIdentityAsync(Ct)).Status);
        Assert.Empty((await ArchiveCompleteness.CollectAsync(archive, Org, null, 100, Ct)).Objects);
    }

    [Fact]
    public void TheSentinelCodecIsCanonicalAndStrict()
    {
        var identity = ArchiveIdentityCodec.NewIdentity(Created);
        var bytes = ArchiveIdentityCodec.Serialize(identity);
        Assert.Equal(identity, ArchiveIdentityCodec.TryParse(bytes));
        Assert.StartsWith("{\"identity_schema\":\"DynamicForms/ArchiveIdentity/v1\",\"archive_instance_id\":\"archive-", Encoding.UTF8.GetString(bytes), StringComparison.Ordinal);

        Assert.Null(ArchiveIdentityCodec.TryParse("not json"u8));
        Assert.Null(ArchiveIdentityCodec.TryParse(Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(bytes).Replace("/v1", "/v2", StringComparison.Ordinal))));
        Assert.Null(ArchiveIdentityCodec.TryParse(Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(bytes).Replace("{", "{ ", StringComparison.Ordinal))));
        Assert.Null(ArchiveIdentityCodec.TryParse(Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(bytes).TrimEnd('}') + ",\"extra\":1}")));
        Assert.Throws<ArgumentException>(() => ArchiveIdentityCodec.Serialize(identity with { ArchiveInstanceId = "has space" }));
        Assert.Throws<ArgumentException>(() => ArchiveIdentityCodec.Serialize(identity with { IdentityCreatedOnUtc = new DateTimeOffset(2026, 11, 17, 10, 0, 0, TimeSpan.FromHours(1)) }));
    }

    // ---- Entry reads: present / absent; the delivery path is unchanged ----

    [Fact]
    public async Task ReadEntryReturnsTheBytesTheUnchangedDeliveryPathStoredAndAbsentOtherwise()
    {
        var archive = new InMemorySecurityEventArchive();
        var entries = await DeliverAsync(archive, Org, JournalStreamNames.Audit, 2);

        var read = await archive.ReadEntryAsync(Org, JournalStreamNames.Audit, 2, Ct);
        Assert.Equal(StoredObjectStatus.Present, read.Status);
        Assert.Equal(Encoding.UTF8.GetBytes(entries[1].JournalEventBodyJson), read.Bytes);
        Assert.Equal(await archive.ReadAsync(Org, JournalStreamNames.Audit, 2, Ct), read.Bytes);

        Assert.Equal(StoredObjectStatus.Absent, (await archive.ReadEntryAsync(Org, JournalStreamNames.Audit, 3, Ct)).Status);
        Assert.Equal(StoredObjectStatus.Absent, (await archive.ReadEntryAsync(OtherOrg, JournalStreamNames.Audit, 1, Ct)).Status);

        // The delivery path is unchanged: an append of other bytes at a delivered key is still a conflict.
        await Assert.ThrowsAsync<ArchiveConflictException>(() => archive.AppendAsync(Org, JournalStreamNames.Audit, 1, "{}"u8.ToArray(), Application.Common.CanonicalJson.Digest("{}"), Ct));
    }

    [Fact]
    public async Task AnUnavailableArchiveThrowsForEveryInventoryOperationAndIsNeverAnEmptyListing()
    {
        var archive = new UnavailableSecurityEventArchive("test outage");
        await Assert.ThrowsAsync<ArchiveUnavailableException>(() => archive.ReadIdentityAsync(Ct));
        await Assert.ThrowsAsync<ArchiveUnavailableException>(() => archive.CreateIdentityAsync(ArchiveIdentityCodec.NewIdentity(Created), Ct));
        await Assert.ThrowsAsync<ArchiveUnavailableException>(() => archive.ReadEntryAsync(Org, JournalStreamNames.Audit, 1, Ct));
        await Assert.ThrowsAsync<ArchiveUnavailableException>(() => archive.ListEntriesAsync(Org, null, null, 10, Ct));
        await Assert.ThrowsAsync<ArchiveUnavailableException>(() => ArchiveCompleteness.CollectAsync(archive, Org, null, 10, Ct));
    }

    [Fact]
    public async Task CancellationPropagatesAndIsNotReportedAsUnavailability()
    {
        var archive = new InMemorySecurityEventArchive();
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => archive.ReadIdentityAsync(cancelled.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => archive.ReadEntryAsync(Org, JournalStreamNames.Audit, 1, cancelled.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => archive.ListEntriesAsync(Org, null, null, 10, cancelled.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ArchiveCompleteness.CollectAsync(archive, Org, null, 10, cancelled.Token));
    }

    // ---- Enumeration and completeness (AE-1..AE-6) ----

    [Fact]
    public async Task CollectingPagesEveryObjectAndAContiguousArchiveIsClean()
    {
        var archive = new InMemorySecurityEventArchive();
        await DeliverAsync(archive, Org, JournalStreamNames.Audit, 5);
        await DeliverAsync(archive, Org, JournalStreamNames.Authorization, 2);

        var listing = await ArchiveCompleteness.CollectAsync(archive, Org, null, pageSizeHint: 2, Ct);
        Assert.Equal(7, listing.Objects.Count);
        var report = ArchiveCompleteness.Analyze(listing, new Dictionary<string, long> { [JournalStreamNames.Audit] = 5, [JournalStreamNames.Authorization] = 2 });

        Assert.True(report.IsClean);
        var audit = report.Streams.Single(s => s.StreamName == JournalStreamNames.Audit);
        Assert.Equal((5, 5L, 5L), (audit.ArchivedRevisionCount, audit.HighestContiguousRevision, audit.HighestRevision));
        Assert.Empty(audit.RevisionsBeyondAssumedHead);
        Assert.Equal(0, report.Streams.Single(s => s.StreamName == JournalStreamNames.Content).ArchivedRevisionCount);
    }

    [Fact]
    public async Task AMissingRevisionIsAGapAndTheContiguousHeadStopsBelowIt()
    {
        var archive = new InMemorySecurityEventArchive();
        await DeliverAsync(archive, Org, JournalStreamNames.Audit, 5);
        Assert.True(archive.RemoveObjectForTesting(Key(Org, JournalStreamNames.Audit, 2)));
        Assert.True(archive.RemoveObjectForTesting(Key(Org, JournalStreamNames.Audit, 3)));

        var audit = ArchiveCompleteness.Analyze(await ArchiveCompleteness.CollectAsync(archive, Org, JournalStreamNames.Audit, 100, Ct)).Streams.Single();
        Assert.Equal(new[] { new ArchiveRevisionRange(2, 3) }, audit.Gaps);
        Assert.Equal((1L, 5L), (audit.HighestContiguousRevision, audit.HighestRevision));
        Assert.False(audit.IsContiguous);
    }

    [Fact]
    public async Task AMissingFirstRevisionIsAGapFromOne()
    {
        var archive = new InMemorySecurityEventArchive();
        await DeliverAsync(archive, Org, JournalStreamNames.Audit, 3);
        archive.RemoveObjectForTesting(Key(Org, JournalStreamNames.Audit, 1));
        var audit = ArchiveCompleteness.Analyze(await ArchiveCompleteness.CollectAsync(archive, Org, JournalStreamNames.Audit, 100, Ct)).Streams.Single();
        Assert.Equal(new[] { new ArchiveRevisionRange(1, 1) }, audit.Gaps);
        Assert.Equal(0, audit.HighestContiguousRevision);
    }

    [Fact]
    public async Task ObjectsBeyondTheAssumedHeadAreReportedBothContiguousAndAfterAGap()
    {
        var archive = new InMemorySecurityEventArchive();
        await DeliverAsync(archive, Org, JournalStreamNames.Audit, 5);
        archive.RemoveObjectForTesting(Key(Org, JournalStreamNames.Audit, 4));

        // The database claims head 2 (for example after a restore or truncation): revision 3 is beyond it contiguously, 5 after a gap.
        var audit = ArchiveCompleteness.Analyze(await ArchiveCompleteness.CollectAsync(archive, Org, null, 100, Ct), new Dictionary<string, long> { [JournalStreamNames.Audit] = 2 })
            .Streams.Single(s => s.StreamName == JournalStreamNames.Audit);
        Assert.Equal(BeyondHeadRevisions, audit.RevisionsBeyondAssumedHead);
        Assert.Equal(new[] { new ArchiveRevisionRange(4, 4) }, audit.Gaps);
        Assert.Equal(3, audit.HighestContiguousRevision);
    }

    [Fact]
    public async Task UnexpectedObjectsUnderTheOrganizationPrefixAreListedAndMakeTheArchiveUnclean()
    {
        var archive = new InMemorySecurityEventArchive();
        await DeliverAsync(archive, Org, JournalStreamNames.Audit, 1);
        archive.PutObjectForTesting($"{Org:D}/unknown-stream/1", "{}"u8.ToArray()); // unknown stream
        archive.PutObjectForTesting($"{Org:D}/audit/01", "{}"u8.ToArray()); // non-canonical revision encoding
        archive.PutObjectForTesting($"{Org:D}/audit/0", "{}"u8.ToArray()); // revisions start at 1
        archive.PutObjectForTesting($"{Org:D}/audit/2/extra", "{}"u8.ToArray()); // extra path segment
        archive.PutObjectForTesting($"{Org:D}/stray", "{}"u8.ToArray()); // no stream segment

        var report = ArchiveCompleteness.Analyze(await ArchiveCompleteness.CollectAsync(archive, Org, null, 2, Ct));
        Assert.Equal(5, report.UnexpectedObjects.Count);
        Assert.False(report.IsClean);
        Assert.True(report.Streams.Single(s => s.StreamName == JournalStreamNames.Audit).IsContiguous);
    }

    [Fact]
    public void DuplicateListedRevisionsAreReported()
    {
        var listing = new ArchiveListing(Org, JournalStreamNames.Audit,
        [
            new ArchiveListedObject("a", true, JournalStreamNames.Audit, 1),
            new ArchiveListedObject("b", true, JournalStreamNames.Audit, 2),
            new ArchiveListedObject("c", true, JournalStreamNames.Audit, 2),
        ], false);
        var audit = ArchiveCompleteness.Analyze(listing).Streams.Single();
        Assert.Equal(DuplicatedRevision, audit.DuplicateRevisions);
        Assert.False(audit.IsContiguous);
    }

    [Fact]
    public void AnAbsentContainerIsNeverClean()
    {
        var report = ArchiveCompleteness.Analyze(new ArchiveListing(Org, null, [], ContainerAbsent: true));
        Assert.True(report.ContainerAbsent);
        Assert.False(report.IsClean);
    }

    [Fact]
    public async Task ListingIsIsolatedByOrganizationAndStream()
    {
        var archive = new InMemorySecurityEventArchive();
        await DeliverAsync(archive, Org, JournalStreamNames.Audit, 2);
        await DeliverAsync(archive, Org, JournalStreamNames.Content, 3);
        await DeliverAsync(archive, OtherOrg, JournalStreamNames.Audit, 4);

        var org = await ArchiveCompleteness.CollectAsync(archive, Org, null, 100, Ct);
        Assert.Equal(5, org.Objects.Count);
        Assert.All(org.Objects, o => Assert.StartsWith($"{Org:D}/", o.ObjectName, StringComparison.Ordinal));

        var content = await ArchiveCompleteness.CollectAsync(archive, Org, JournalStreamNames.Content, 100, Ct);
        Assert.Equal(3, content.Objects.Count);
        Assert.All(content.Objects, o => Assert.Equal(JournalStreamNames.Content, o.StreamName));

        var other = ArchiveCompleteness.Analyze(await ArchiveCompleteness.CollectAsync(archive, OtherOrg, null, 100, Ct));
        Assert.Equal(4, other.Streams.Single(s => s.StreamName == JournalStreamNames.Audit).HighestContiguousRevision);
    }

    // ---- Blob name classification (pure; the store itself is covered by the Azurite integration test) ----

    [Theory]
    [InlineData("org/019a5b2c-5ec1-7b01-8000-0000000000d1/audit/000000000001.json", null, true, "audit", 1L)]
    [InlineData("org/019a5b2c-5ec1-7b01-8000-0000000000d1/content/000000004096.json", "content", true, "content", 4096L)]
    [InlineData("org/019a5b2c-5ec1-7b01-8000-0000000000d1/audit/000000000000.json", null, false, null, null)]
    [InlineData("org/019a5b2c-5ec1-7b01-8000-0000000000d1/audit/1.json", null, false, null, null)]
    [InlineData("org/019a5b2c-5ec1-7b01-8000-0000000000d1/audit/0000000000001.json", null, false, null, null)]
    [InlineData("org/019a5b2c-5ec1-7b01-8000-0000000000d1/audit/000000000001.txt", null, false, null, null)]
    [InlineData("org/019a5b2c-5ec1-7b01-8000-0000000000d1/audit/x/000000000001.json", null, false, null, null)]
    [InlineData("org/019a5b2c-5ec1-7b01-8000-0000000000d1/audit/000000000001.json", "content", false, null, null)]
    [InlineData("org/019a5b2c-5ec1-7b01-8000-0000000000d2/audit/000000000001.json", null, false, null, null)]
    public void BlobEntryNamesAreClassifiedStrictly(string name, string? requestedStream, bool canonical, string? stream, long? revision)
    {
        var listed = BlobSecurityEventArchive.Classify(name, $"org/{Org:D}/", requestedStream);
        Assert.Equal((canonical, stream, revision), (listed.IsCanonicalEntryKey, listed.StreamName, listed.StreamRevision));
    }

    [Fact]
    public void TheBlobEntryNameIsTheOneTheDeliveryPathWrites()
    {
        // AppendAsync and ReadAsync format the name inline; the inventory must address exactly the same object (also checked on Azurite).
        Assert.Equal("org/019a5b2c-5ec1-7b01-8000-0000000000d1/audit/000000000042.json", BlobSecurityEventArchive.EntryBlobName(Org, JournalStreamNames.Audit, 42));
        Assert.DoesNotContain("org/", BlobSecurityEventArchive.IdentityBlobName, StringComparison.Ordinal);
    }
}
