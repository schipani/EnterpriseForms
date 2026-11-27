using AuthorizationAuthority.Application.Common;
using AuthorizationAuthority.Application.Features.Content;
using AuthorizationAuthority.Contracts.V1;
using AuthorizationAuthority.Domain.Features.Content;
using AuthorizationAuthority.Domain.Features.Organizations;
using AuthorizationAuthority.Domain.Features.Sharing;
using EnterpriseForms.Integrity.Canonical;
using Xunit;
using static AuthorizationAuthority.Tests.ContributorWriteFixtures;

namespace AuthorizationAuthority.Tests;

/// <summary>
/// C10 independent review corrections through the real Prepare and Commit:
/// F1 — an expired share (its own expiry or an ancestor's) confers no edit scope, whether or not convergence has run, at Prepare and at Commit;
/// D-2 — a replacement is refused when the accepted version lies outside the authorizing share's boundary (Prepare and Commit);
/// the replacement is exactly the next version (owner decision C-196): a skipped number is refused (the business now numbers from the
/// accepted version, owner decision 2 of 2026-11-27);
/// D-1 — see ContributorWriteProtocolTests.ContentEvidence (same version with unchanged content, or exactly the next version);
/// E-3/E-4/E-5 — narrowing, team removal between Prepare and Commit, and an attachment without version evidence.
/// </summary>
public sealed partial class ContributorWriteProtocolTests
{
    private static ShareResourceMetadata RegisterShare(Harness h, Guid shareId, int depth = 1, Guid? parent = null, DateTimeOffset? expires = null)
    {
        h.Store.Resources.Add(new AuthorityResource { AuthorityResourceId = Guid.CreateVersion7(), OrganizationId = h.Org, ResourceTypeName = ResourceTypeNames.RecordShare, ResourceEntityId = shareId, ParentResourceEntityId = h.Record, ResourceLifecycleState = ResourceLifecycleStates.Active });
        var metadata = new ShareResourceMetadata { OrganizationId = h.Org, RecordShareId = shareId, DelegationDepth = depth, ParentRecordShareId = parent, ShareExpiresOn = expires };
        h.Store.ShareMetadata.Add(metadata);
        return metadata;
    }

    [Fact]
    public async Task AnExpiredShareConfersNoEditScopeBeforeConvergence()
    {
        var h = await new Harness().CreatedAsync();
        h.ShareWithContributor();
        RegisterShare(h, h.ShareId, expires: DateTimeOffset.UtcNow.AddMinutes(-1)); // expired; its edges are still present (no convergence)

        await Assert.ThrowsAsync<AuthorityException>(() => h.WriteAsync(h.Contributor, h.Rows("after"), withCurrent: true));
        Assert.Equal(1, h.Head.CommittedContentRevision);
    }

    [Fact]
    public async Task AnAncestorsExpiryDeniesADelegatedEditAndAnUnexpiredShareStillEdits()
    {
        var expired = await new Harness().CreatedAsync();
        var parent = Guid.CreateVersion7();
        RegisterShare(expired, parent, expires: DateTimeOffset.UtcNow.AddMinutes(-1));
        expired.ShareWithContributor();
        RegisterShare(expired, expired.ShareId, depth: 2, parent: parent); // no own expiry: the lineage minimum is the parent's
        await Assert.ThrowsAsync<AuthorityException>(() => expired.WriteAsync(expired.Contributor, expired.Rows("after"), withCurrent: true));

        var live = await new Harness().CreatedAsync();
        live.ShareWithContributor();
        RegisterShare(live, live.ShareId, expires: DateTimeOffset.UtcNow.AddHours(1));
        Assert.Equal(2, (await live.WriteAsync(live.Contributor, live.Rows("after"), withCurrent: true)).CommittedContentRevision);
    }

    [Fact]
    public async Task ExpiryBetweenPrepareAndCommitAbortsTheEdit()
    {
        var h = await new Harness().CreatedAsync();
        h.ShareWithContributor();
        var metadata = RegisterShare(h, h.ShareId, expires: DateTimeOffset.UtcNow.AddHours(1));
        var operation = Guid.CreateVersion7();
        var candidate = h.Stage(h.Rows("after"), operation);
        var prepared = await h.PrepareAsync(h.Contributor, operation, candidate, withCurrent: true);

        metadata.ShareExpiresOn = DateTimeOffset.UtcNow.AddMinutes(-1);
        await Assert.ThrowsAsync<AuthorityException>(() => h.CommitAsync(h.Contributor, prepared, candidate));
        Assert.Equal(1, h.Head.CommittedContentRevision);
        Assert.Equal(ContentOperationStates.Aborted, h.Store.Operations.Single(o => o.ContentChangeOperationId == operation).OperationStateName);
    }

    [Fact]
    public async Task OnlyTheExactNextCleanVersionOfTheSharedDocumentAloneIsAccepted()
    {
        var h = await new Harness().CreatedAsync();
        var binding = ReplaceShare(h);

        await Assert.ThrowsAsync<AuthorityException>(() => PrepareReplacementAsync(h, NextVersion(h, version: 1)));          // the same number with new bytes
        await Assert.ThrowsAsync<AuthorityException>(() => PrepareReplacementAsync(h, NextVersion(h, version: 0)));          // a lower number
        await Assert.ThrowsAsync<AuthorityException>(() => PrepareReplacementAsync(h, NextVersion(h, version: 3)));          // a skipped number
        await Assert.ThrowsAsync<AuthorityException>(() => PrepareReplacementAsync(h, NextVersion(h, verdict: "Infected")));  // not clean
        var mixed = Change(NextVersion(h), h.Other, r => r.With("string_value", RowValue.Text("also changed")));
        await Assert.ThrowsAsync<AuthorityException>(() => PrepareReplacementAsync(h, mixed));                               // another change in the same operation
        Assert.Equal(1, binding.SharedThroughVersionNumber);
        Assert.Equal(1, h.Head.CommittedContentRevision);
    }

    [Fact]
    public async Task AVersionNumberSkippedByAnUnacceptedUploadIsRefusedAndMovesNoBoundary()
    {
        // The Authority still refuses a skipped number (exactly the next version); owner decision 2 makes the business number from the
        // accepted version, so an unaccepted upload no longer causes the skip.
        var h = await new Harness().CreatedAsync();
        var binding = ReplaceShare(h);

        await Assert.ThrowsAsync<AuthorityException>(() => PrepareReplacementAsync(h, NextVersion(h, version: 3))); // version 2 was processed but never accepted

        Assert.Equal(1, binding.SharedThroughVersionNumber);
        Assert.Equal(1, h.Head.CommittedContentRevision);
    }

    [Fact]
    public async Task AReplacementIsRefusedWhenTheAcceptedVersionIsOutsideTheShareBoundary()
    {
        var h = await new Harness().CreatedAsync();
        var binding = ReplaceShare(h);
        await h.WriteAsync(h.Owner, NextVersion(h, version: 2), withCurrent: true); // the owner's v2: outside the share's boundary (1)

        await Assert.ThrowsAsync<AuthorityException>(() => PrepareReplacementAsync(h, NextVersion(h, version: 3)));
        Assert.Equal(1, binding.SharedThroughVersionNumber);
        Assert.Equal(2, h.Head.CommittedContentRevision);
    }

    [Fact]
    public async Task ABoundaryNarrowedOrReplaceRemovedOrTeamLeftBetweenPrepareAndCommitAborts()
    {
        var narrowed = await new Harness().CreatedAsync();
        var lowered = ReplaceShare(narrowed);
        var (prepared, candidate) = await PrepareReplacementAsync(narrowed, NextVersion(narrowed));
        lowered.SharedThroughVersionNumber = 0;
        await Assert.ThrowsAsync<AuthorityException>(() => narrowed.CommitAsync(narrowed.Contributor, prepared, candidate));
        Assert.Equal(1, narrowed.Head.CommittedContentRevision);

        var removed = await new Harness().CreatedAsync();
        var withoutReplace = ReplaceShare(removed);
        var (prepared2, candidate2) = await PrepareReplacementAsync(removed, NextVersion(removed));
        withoutReplace.CanReplaceDocument = false;
        await Assert.ThrowsAsync<AuthorityException>(() => removed.CommitAsync(removed.Contributor, prepared2, candidate2));
        Assert.Equal(1, withoutReplace.SharedThroughVersionNumber);

        var team = await new Harness().CreatedAsync();
        var teamShare = ReplaceShare(team);
        team.Store.Edges.RemoveAll(e => e.ResourceEntityId == teamShare.RecordShareId && e.RelationshipName == "contributor");
        team.Edge(ResourceTypeNames.RecordShare, teamShare.RecordShareId, "contributor", ResourceTypeNames.Team, team.Team, "participant");
        var membership = team.Edge(ResourceTypeNames.Team, team.Team, "member", ResourceTypeNames.User, team.Contributor);
        var (prepared3, candidate3) = await PrepareReplacementAsync(team, NextVersion(team)); // held through the team
        team.Remove(membership);
        await Assert.ThrowsAsync<AuthorityException>(() => team.CommitAsync(team.Contributor, prepared3, candidate3));
        Assert.Equal(1, teamShare.SharedThroughVersionNumber);
        Assert.Equal(1, team.Head.CommittedContentRevision);
    }

    [Fact]
    public async Task AnAttachmentWithoutVersionEvidenceCannotBeReplacedThroughAShare()
    {
        var h = await new Harness().CreatedAsync();
        var binding = ReplaceShare(h);
        foreach (var leaf in h.Store.ManifestLeaves.Where(l => l.ManifestItemId == h.Document))
        {
            leaf.AttachmentVersionNumber = null; // accepted before C10 WP8 (Option A): no evidence, no backfill
        }

        await Assert.ThrowsAsync<AuthorityException>(() => PrepareReplacementAsync(h, NextVersion(h)));
        Assert.Equal(1, binding.SharedThroughVersionNumber);
    }

    [Fact]
    public async Task AnOwnerCannotLowerAnAttachmentsVersionNumber()
    {
        var h = await new Harness().CreatedAsync();
        await h.WriteAsync(h.Owner, NextVersion(h, version: 2), withCurrent: true);

        var refused = await Assert.ThrowsAsync<AuthorityException>(() => h.WriteAsync(h.Owner, NextVersion(h, version: 1), withCurrent: true));
        Assert.Contains(AttachmentVersionRules.RefusalCode, refused.Message, StringComparison.Ordinal);
        Assert.Equal(2, h.Head.CommittedContentRevision);
    }

    [Fact]
    public void TheAttachmentVersionRuleAcceptsOnlyUnchangedContentUnderTheAcceptedNumberOrExactlyTheNextNumber()
    {
        // C10 owner decision 1 (review D-1): same version + unchanged content, or exactly the next version; everything else fails closed.
        var item = Guid.CreateVersion7();
        byte[] content = [.. Enumerable.Repeat((byte)7, 32)];
        byte[] other = [.. Enumerable.Repeat((byte)8, 32)];
        var accepted = new Dictionary<Guid, AttachmentVersionRules.Evidence> { [item] = new(3, content) };
        AttachmentVersionRules.Require([(item, 3, content)], accepted);                // a carried, unchanged row
        AttachmentVersionRules.Require([(item, 4, other)], accepted);                  // exactly the next version
        AttachmentVersionRules.Require([(Guid.CreateVersion7(), 1, other)], accepted); // a new attachment

        Refused(AttachmentVersionRules.ContentChangedCode, [(item, 3, other)], accepted);  // new bytes under the accepted number
        Refused(AttachmentVersionRules.RefusalCode, [(item, 2, other)], accepted);         // a lower number
        Refused(AttachmentVersionRules.NotNextCode, [(item, 5, other)], accepted);         // a skipped number
        Refused(AttachmentVersionRules.RefusalCode, [(item, null, other)], accepted);
        Refused(AttachmentVersionRules.RefusalCode, [(item, 0, other)], accepted);
        Refused(AttachmentVersionRules.EvidenceMissingCode, [(item, 3, content)], new Dictionary<Guid, AttachmentVersionRules.Evidence> { [item] = new(3, null) });
        Refused(AttachmentVersionRules.EvidenceMissingCode, [(item, 4, content)], new Dictionary<Guid, AttachmentVersionRules.Evidence> { [item] = new(null, null) });
        AttachmentVersionRules.Require([(item, 4, other)], new Dictionary<Guid, AttachmentVersionRules.Evidence> { [item] = new(3, null) }); // the next version needs no content evidence
    }

    private static void Refused(string code, (Guid, int?, byte[])[] staged, Dictionary<Guid, AttachmentVersionRules.Evidence> accepted) =>
        Assert.StartsWith(code, Assert.Throws<AuthorityException>(() => AttachmentVersionRules.Require(staged, accepted)).Message, StringComparison.Ordinal);
}
