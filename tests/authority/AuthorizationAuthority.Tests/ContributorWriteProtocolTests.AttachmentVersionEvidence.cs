using AuthorizationAuthority.Contracts.V1;
using EnterpriseForms.Integrity.Canonical;
using Xunit;
using static AuthorizationAuthority.Tests.ContributorWriteFixtures;

namespace AuthorizationAuthority.Tests;

/// <summary>
/// C10 WP8 (Option A): an accepted commit records, on each attachment leaf, the version number read from the attachment row the
/// Authority verified for that commit, and on no other leaf.
/// </summary>
public sealed partial class ContributorWriteProtocolTests
{
    [Fact]
    public async Task AnAcceptedCommitRecordsTheVerifiedAttachmentVersionOnTheAttachmentLeafOnly()
    {
        var h = await new Harness().CreatedAsync();
        var seeded = h.Store.ManifestLeaves.Single(l => l.ManifestItemId == h.Document);
        Assert.Equal(1, seeded.AttachmentVersionNumber); // the creating commit already recorded the verified version

        Assert.Equal(32, seeded.AttachmentContentFingerprint!.Length); // and its content evidence (review D-1)

        var rows = Change(h.Rows(), h.Document, r => r.With("version_number", RowValue.Int32(2)).With("storage_object_key", RowValue.Text("objects/two")));
        await h.WriteAsync(h.Owner, rows, withCurrent: true);

        var commit = h.Store.Heads.Single().CurrentRecordManifestCommitId;
        var leaves = h.Store.ManifestLeaves.Where(l => l.RecordManifestCommitId == commit).ToList();
        var document = leaves.Single(l => l.ManifestItemId == h.Document);
        Assert.Equal(2, document.AttachmentVersionNumber);
        Assert.False(document.AttachmentContentFingerprint.AsSpan().SequenceEqual(seeded.AttachmentContentFingerprint), "new content, new fingerprint");
        Assert.All(leaves.Where(l => l.ManifestItemKindCode != (byte)ManifestItemKind.Attachment), l => Assert.Null(l.AttachmentVersionNumber));
        Assert.All(leaves.Where(l => l.ManifestItemKindCode != (byte)ManifestItemKind.Attachment), l => Assert.Null(l.AttachmentContentFingerprint));

        // A carried, unchanged row keeps its fingerprint although its operation binding (and so its canonical digest) changed.
        await h.WriteAsync(h.Owner, rows, withCurrent: true);
        var carried = h.Store.ManifestLeaves.Single(l => l.RecordManifestCommitId == h.Store.Heads.Single().CurrentRecordManifestCommitId && l.ManifestItemId == h.Document);
        Assert.Equal(document.AttachmentContentFingerprint, carried.AttachmentContentFingerprint);
        Assert.False(document.CanonicalPayloadDigest.AsSpan().SequenceEqual(carried.CanonicalPayloadDigest));
    }
}
