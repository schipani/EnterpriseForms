using AuthorizationAuthority.Application.Common;
using AuthorizationAuthority.Contracts.V1;
using AuthorizationAuthority.Domain.Features.Content;
using AuthorizationAuthority.Domain.Features.Organizations;
using AuthorizationAuthority.Domain.Features.Sharing;
using EnterpriseForms.Integrity.Canonical;
using Xunit;
using static AuthorizationAuthority.Tests.ContributorWriteFixtures;

namespace AuthorizationAuthority.Tests;

/// <summary>
/// C10 WP8-B (WP-0 Sections 3.6 and 3.6.1, owner decisions 2026-11-27) through the real Prepare and Commit: a contributor holding a
/// live share granted directly by the owner with replace for one attachment may commit exactly its next version, and only that share's
/// boundary advances, in the accepted commit. Everything else is refused or aborted without moving any boundary: a skipped version, a
/// mixed change, another attachment, a delegated share, an unclean scan, the switch off, revocation or expiry between Prepare and Commit,
/// and two authorizing shares. An ownership transfer between Prepare and Commit leaves the durable grant valid.
/// </summary>
public sealed partial class ContributorWriteProtocolTests
{
    private static ShareDocumentBinding ReplaceShare(Harness h, Guid? shareId = null, int depth = 1, Guid? parent = null, DateTimeOffset? expires = null, bool replace = true, Guid? attachment = null)
    {
        var id = shareId ?? Guid.CreateVersion7();
        h.Store.Resources.Add(new AuthorityResource { AuthorityResourceId = Guid.CreateVersion7(), OrganizationId = h.Org, ResourceTypeName = ResourceTypeNames.RecordShare, ResourceEntityId = id, ParentResourceEntityId = h.Record, ResourceLifecycleState = ResourceLifecycleStates.Active });
        h.Store.ShareMetadata.Add(new ShareResourceMetadata { OrganizationId = h.Org, RecordShareId = id, DelegationDepth = depth, ParentRecordShareId = parent, ShareExpiresOn = expires });
        h.Edge(ResourceTypeNames.FormRecord, h.Record, "shared_via", ResourceTypeNames.RecordShare, id);
        h.Edge(ResourceTypeNames.RecordShare, id, "contributor", ResourceTypeNames.User, h.Contributor);
        h.Edge(ResourceTypeNames.DocumentItem, attachment ?? h.Document, "shared_via", ResourceTypeNames.RecordShare, id);
        var binding = new ShareDocumentBinding { ShareDocumentBindingId = Guid.CreateVersion7(), OrganizationId = h.Org, RecordShareId = id, FileAttachmentId = attachment ?? h.Document, CanViewDocument = true, CanReplaceDocument = replace, SharedThroughVersionNumber = 1 };
        h.Store.DocumentBindings.Add(binding);
        return binding;
    }

    private static List<Row> NextVersion(Harness h, int version = 2, string verdict = "Clean") =>
        Change(h.Rows(), h.Document, r => r.With("version_number", RowValue.Int32(version)).With("storage_object_key", RowValue.Text("objects/v" + version)).With("scan_verdict", RowValue.Text(verdict)));

    private static async Task<(PreparedContentOperation Prepared, List<Staged> Candidate)> PrepareReplacementAsync(Harness h, List<Row> rows)
    {
        var operation = Guid.CreateVersion7();
        var candidate = h.Stage(rows, operation);
        return (await h.PrepareAsync(h.Contributor, operation, candidate, withCurrent: true), candidate);
    }

    [Fact]
    public async Task AContributorReplacesASharedDocumentAndOnlyItsShareBoundaryAdvances()
    {
        var h = await new Harness().CreatedAsync();
        var replacing = ReplaceShare(h);
        var viewOnly = ReplaceShare(h, replace: false); // another share of the same document: never extended

        var (prepared, candidate) = await PrepareReplacementAsync(h, NextVersion(h));
        var revision = await h.CommitAsync(h.Contributor, prepared, candidate);

        Assert.Equal(2, revision.CommittedContentRevision);
        Assert.Equal(2, replacing.SharedThroughVersionNumber);
        Assert.Equal(1, viewOnly.SharedThroughVersionNumber);
        var head = h.Store.Heads.Single().CurrentRecordManifestCommitId;
        Assert.Equal(2, h.Store.ManifestLeaves.Single(l => l.RecordManifestCommitId == head && l.ManifestItemId == h.Document).AttachmentVersionNumber);
        Assert.Equal(1, h.Store.ManifestLeaves.Where(l => l.RecordManifestCommitId != head && l.ManifestItemId == h.Document).Max(l => l.AttachmentVersionNumber)); // the earlier accepted version is untouched
    }

    [Fact]
    public async Task OnlyTheExactNextCleanVersionOfTheSharedDocumentAloneIsAccepted()
    {
        var h = await new Harness().CreatedAsync();
        var binding = ReplaceShare(h);

        await Assert.ThrowsAsync<AuthorityException>(() => PrepareReplacementAsync(h, NextVersion(h, version: 3)));          // skipped version
        await Assert.ThrowsAsync<AuthorityException>(() => PrepareReplacementAsync(h, NextVersion(h, verdict: "Infected")));  // not clean
        var mixed = Change(NextVersion(h), h.Other, r => r.With("string_value", RowValue.Text("also changed")));
        await Assert.ThrowsAsync<AuthorityException>(() => PrepareReplacementAsync(h, mixed));                               // another change in the same operation
        Assert.Equal(1, binding.SharedThroughVersionNumber);
        Assert.Equal(1, h.Head.CommittedContentRevision);
    }

    [Fact]
    public async Task ADelegatedShareAViewOnlyBindingOrTheSwitchOffNeverAuthorizeAReplacement()
    {
        var delegated = await new Harness().CreatedAsync();
        var parent = ReplaceShare(delegated);
        delegated.Store.Edges.RemoveAll(e => e.ResourceEntityId == parent.RecordShareId && e.RelationshipName == "contributor"); // the contributor holds only the child
        ReplaceShare(delegated, depth: 2, parent: parent.RecordShareId);
        Assert.Equal(AuthorityErrorCodes.Forbidden, (await Assert.ThrowsAsync<AuthorityException>(() => PrepareReplacementAsync(delegated, NextVersion(delegated)))).ErrorCode);

        var viewOnly = await new Harness().CreatedAsync();
        ReplaceShare(viewOnly, replace: false);
        await Assert.ThrowsAsync<AuthorityException>(() => PrepareReplacementAsync(viewOnly, NextVersion(viewOnly)));

        var switchedOff = await new Harness(contributorWritesEnabled: false).CreatedAsync();
        ReplaceShare(switchedOff);
        Assert.Equal(AuthorityErrorCodes.ContributorWritesDisabled, (await Assert.ThrowsAsync<AuthorityException>(() => PrepareReplacementAsync(switchedOff, NextVersion(switchedOff)))).ErrorCode);
    }

    [Fact]
    public async Task RevocationOrExpiryBetweenPrepareAndCommitAbortsAndMovesNoBoundary()
    {
        var revoked = await new Harness().CreatedAsync();
        var binding = ReplaceShare(revoked);
        var (prepared, candidate) = await PrepareReplacementAsync(revoked, NextVersion(revoked));
        foreach (var edge in revoked.Store.Edges.Where(e => e.ResourceEntityId == binding.RecordShareId && e.RelationshipName == "contributor"))
        {
            edge.RemovedBySecurityCommandId = Guid.CreateVersion7();
        }

        await Assert.ThrowsAsync<AuthorityException>(() => revoked.CommitAsync(revoked.Contributor, prepared, candidate));
        Assert.Equal(1, binding.SharedThroughVersionNumber);
        Assert.Equal(1, revoked.Head.CommittedContentRevision);

        var expired = await new Harness().CreatedAsync();
        var expiring = ReplaceShare(expired);
        var (prepared2, candidate2) = await PrepareReplacementAsync(expired, NextVersion(expired));
        expired.Store.ShareMetadata.Single(m => m.RecordShareId == expiring.RecordShareId).ShareExpiresOn = DateTimeOffset.UtcNow.AddMinutes(-1);

        await Assert.ThrowsAsync<AuthorityException>(() => expired.CommitAsync(expired.Contributor, prepared2, candidate2));
        Assert.Equal(1, expiring.SharedThroughVersionNumber);
    }

    [Fact]
    public async Task TwoAuthorizingSharesAreRefusedAsAmbiguous()
    {
        var h = await new Harness().CreatedAsync();
        var first = ReplaceShare(h);
        var second = ReplaceShare(h);

        var refused = await Assert.ThrowsAsync<AuthorityException>(() => PrepareReplacementAsync(h, NextVersion(h)));

        Assert.Equal(AuthorityErrorCodes.DocumentReplaceShareAmbiguous, refused.ErrorCode);
        Assert.Equal((1, 1), (first.SharedThroughVersionNumber, second.SharedThroughVersionNumber));
    }

    [Fact]
    public async Task AnOwnershipTransferBetweenPrepareAndCommitLeavesTheDurableGrantValid()
    {
        var h = await new Harness().CreatedAsync();
        var binding = ReplaceShare(h);
        var (prepared, candidate) = await PrepareReplacementAsync(h, NextVersion(h));
        var owner = h.Store.Edges.Single(e => e.ResourceTypeName == ResourceTypeNames.FormRecord && e.RelationshipName == "owner" && e.RemovedBySecurityCommandId is null);
        owner.RemovedBySecurityCommandId = Guid.CreateVersion7();
        h.Edge(ResourceTypeNames.FormRecord, h.Record, "owner", ResourceTypeNames.User, Guid.CreateVersion7());

        await h.CommitAsync(h.Contributor, prepared, candidate);

        Assert.Equal(2, binding.SharedThroughVersionNumber);
    }

    [Fact]
    public async Task AConcurrentContentChangeBetweenPrepareAndCommitFailsTheReplacementAndMovesNoBoundary()
    {
        var h = await new Harness().CreatedAsync();
        var binding = ReplaceShare(h);
        var (prepared, candidate) = await PrepareReplacementAsync(h, NextVersion(h));
        await h.WriteAsync(h.Owner, Change(h.Rows(), h.Other, r => r.With("string_value", RowValue.Text("owner edit"))), withCurrent: true);

        await Assert.ThrowsAsync<AuthorityException>(() => h.CommitAsync(h.Contributor, prepared, candidate));

        Assert.Equal(1, binding.SharedThroughVersionNumber);
        Assert.Equal(2, h.Head.CommittedContentRevision); // the owner's change only
    }
}
