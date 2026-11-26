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

        var rows = Change(h.Rows(), h.Document, r => r.With("version_number", RowValue.Int32(3)).With("storage_object_key", RowValue.Text("objects/three")));
        await h.WriteAsync(h.Owner, rows, withCurrent: true);

        var commit = h.Store.Heads.Single().CurrentRecordManifestCommitId;
        var leaves = h.Store.ManifestLeaves.Where(l => l.RecordManifestCommitId == commit).ToList();
        Assert.Equal(3, leaves.Single(l => l.ManifestItemId == h.Document).AttachmentVersionNumber);
        Assert.All(leaves.Where(l => l.ManifestItemKindCode != (byte)ManifestItemKind.Attachment), l => Assert.Null(l.AttachmentVersionNumber));
    }
}
