using System.Text;
using AuthorizationAuthority.Application.Common.Ports;
using AuthorizationAuthority.Application.Features.Journal.Verification;
using AuthorizationAuthority.Domain.Features.Journal;
using Xunit;

namespace AuthorizationAuthority.Tests;

/// <summary>
/// SEC-BL-01 WP-4A (contract amendment A-1) against throwaway Azurite instances: the bounded listing above a revision pages completely and
/// in order across decimal prefix boundaries, reports non-canonical names inside the bounded ranges and a missing container; the Blob
/// accepted-exception store is create-only. Skips unless the gate script sets <c>SECBL01_*</c>. Development/Testing evidence only.
/// </summary>
public sealed class JournalBoundedListingAzuriteTests
{
    private static readonly Guid Org = Guid.Parse("019a5b2c-5ec1-7b01-8000-0000000000e1");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(0, 2)]
    [InlineData(7, 3)]
    [InlineData(9, 1000)]
    [InlineData(10, 4)]
    [InlineData(99, 5)]
    [InlineData(120, 2)]
    [InlineData(125, 2)]
    public async Task TheBoundedListingReturnsExactlyTheRevisionsAboveInOrderAcrossPages(long after, int pageSize)
    {
        Wp2Azurite.Require();
        var archive = Wp2Azurite.NewArchive(Wp2Azurite.NewContainerName());
        foreach (var entry in Wp2Azurite.Entries(Org, JournalStreamNames.Audit, 125))
        {
            await Wp2Azurite.DeliverAsync(archive, entry, Ct);
        }

        foreach (var entry in Wp2Azurite.Entries(Org, JournalStreamNames.Content, 3))
        {
            await Wp2Azurite.DeliverAsync(archive, entry, Ct); // another stream must never appear
        }

        var listing = await ArchiveCompleteness.CollectAboveAsync(archive, Org, JournalStreamNames.Audit, after, pageSize, Ct);
        var expected = Enumerable.Range(1, 125).Select(r => (long)r).Where(r => r > after).ToList();
        Assert.Equal(expected, listing.Objects.Select(o => o.StreamRevision!.Value).ToList());
        Assert.All(listing.Objects, o => Assert.True(o.IsCanonicalEntryKey && o.StreamName == JournalStreamNames.Audit));
        Assert.False(listing.ContainerAbsent);
    }

    [Fact]
    public async Task ANonCanonicalNameInsideABoundedRangeIsReportedAndAMissingContainerIsNotAnEmptyArchive()
    {
        Wp2Azurite.Require();
        var name = Wp2Azurite.NewContainerName();
        var archive = Wp2Azurite.NewArchive(name);
        var absent = await ArchiveCompleteness.CollectAboveAsync(archive, Org, JournalStreamNames.Audit, 5, 10, Ct);
        Assert.True(absent.ContainerAbsent);

        foreach (var entry in Wp2Azurite.Entries(Org, JournalStreamNames.Audit, 8))
        {
            await Wp2Azurite.DeliverAsync(archive, entry, Ct);
        }

        await Wp2Azurite.RawArchiveContainer(name).UploadBlobAsync($"org/{Org:D}/audit/000000000007.json.bak", new BinaryData("x"u8.ToArray()), Ct);
        await Wp2Azurite.RawArchiveContainer(name).UploadBlobAsync($"org/{Org:D}/audit/000000000002.json.bak", new BinaryData("x"u8.ToArray()), Ct); // below: not listed
        var listing = await ArchiveCompleteness.CollectAboveAsync(archive, Org, JournalStreamNames.Audit, 5, 2, Ct);
        Assert.Equal([6L, 7L, 8L], listing.Objects.Where(o => o.IsCanonicalEntryKey).Select(o => o.StreamRevision!.Value));
        Assert.Single(listing.Objects, o => !o.IsCanonicalEntryKey && o.ObjectName.EndsWith("000000000007.json.bak", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TheBlobAcceptedExceptionStoreIsCreateOnly()
    {
        Wp2Azurite.Require();
        var store = Wp2Azurite.NewExceptionStore(Wp2Azurite.NewContainerName());
        var first = Encoding.UTF8.GetBytes("{\"record\":1}");
        Assert.Equal(StoredObjectStatus.ContainerAbsent, (await store.ReadAsync(Org, JournalStreamNames.Audit, "BX-AZ-1", Ct)).Status);
        Assert.Equal(CheckpointCreateStatus.Created, (await store.CreateAsync(Org, JournalStreamNames.Audit, "BX-AZ-1", first, Ct)).Status);
        Assert.Equal(CheckpointCreateStatus.IdenticalExists, (await store.CreateAsync(Org, JournalStreamNames.Audit, "BX-AZ-1", first, Ct)).Status);
        var conflict = await store.CreateAsync(Org, JournalStreamNames.Audit, "BX-AZ-1", "{\"record\":2}"u8.ToArray(), Ct);
        Assert.Equal(CheckpointCreateStatus.Conflict, conflict.Status);
        Assert.Equal(first, conflict.ExistingBytes); // the stored bytes stay (compared by content, not reference)
        Assert.Equal(first, (await store.ReadAsync(Org, JournalStreamNames.Audit, "BX-AZ-1", Ct)).Bytes);
        Assert.Equal(StoredObjectStatus.Absent, (await store.ReadAsync(Org, JournalStreamNames.Audit, "BX-AZ-2", Ct)).Status);
        await Assert.ThrowsAsync<ArgumentException>(() => store.CreateAsync(Org, JournalStreamNames.Audit, "../escape", first, Ct));
    }
}
