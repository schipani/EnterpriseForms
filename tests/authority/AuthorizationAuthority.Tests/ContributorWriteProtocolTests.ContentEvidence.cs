using AuthorizationAuthority.Application.Common;
using AuthorizationAuthority.Application.Features.Content;
using AuthorizationAuthority.Contracts.V1;
using AuthorizationAuthority.Domain.Features.Content;
using EnterpriseForms.Integrity.Canonical;
using Xunit;
using static AuthorizationAuthority.Tests.ContributorWriteFixtures;

namespace AuthorizationAuthority.Tests;

/// <summary>
/// C10 owner decision 1 (independent review D-1) through the real Prepare and Commit: an attachment that stays in the record is either the
/// same version with unchanged verified content or exactly the next version, on the owner path as well as the share path. Commit
/// re-derives the rule from the verified staged rows and the accepted evidence, so an operation prepared without it (another build)
/// cannot accept new bytes under an accepted number. A head accepted before the evidence existed is compared with what Prepare verified
/// in the current snapshot; without that, a carried row fails closed.
/// </summary>
public sealed partial class ContributorWriteProtocolTests
{
    private static List<Row> NewBytesUnderTheAcceptedNumber(Harness h) =>
        Change(h.Rows(), h.Document, r => r.With("storage_object_key", RowValue.Text("objects/substituted")));

    private static void ForgetAcceptedContentEvidence(Harness h)
    {
        foreach (var leaf in h.Store.ManifestLeaves.Where(l => l.ManifestItemId == h.Document))
        {
            leaf.AttachmentContentFingerprint = null; // accepted before this evidence existed (no backfill)
        }
    }

    private static AuthorityException RefusedWith(string code, AuthorityException refused)
    {
        Assert.Contains(code, refused.Message, StringComparison.Ordinal);
        return refused;
    }

    [Fact]
    public async Task AnOwnerWriteWithNewDocumentBytesUnderTheAcceptedNumberIsRefusedAtPrepare()
    {
        var h = await new Harness().CreatedAsync();
        var before = StateOf(h);

        RefusedWith(AttachmentVersionRules.ContentChangedCode, await Assert.ThrowsAsync<AuthorityException>(() => h.WriteAsync(h.Owner, NewBytesUnderTheAcceptedNumber(h), withCurrent: true)));
        RefusedWith(AttachmentVersionRules.NotNextCode, await Assert.ThrowsAsync<AuthorityException>(() => h.WriteAsync(h.Owner, NextVersion(h, version: 3), withCurrent: true)));
        Assert.Equal(before, StateOf(h));

        Assert.Equal(2, (await h.WriteAsync(h.Owner, NextVersion(h, version: 2), withCurrent: true)).CommittedContentRevision); // exactly the next version
        Assert.Equal(3, (await h.WriteAsync(h.Owner, NextVersion(h, version: 2), withCurrent: true)).CommittedContentRevision); // and carried unchanged
    }

    [Fact]
    public async Task CommitRefusesNewBytesUnderTheAcceptedNumberWhenPrepareDidNotApplyTheRule()
    {
        var h = await new Harness().CreatedAsync();
        var before = StateOf(h);
        var operation = Guid.CreateVersion7();
        var staged = await SeedPreparedAsync(h, operation, h.Stage(NewBytesUnderTheAcceptedNumber(h), operation), ContentAcceptanceRules.CurrentVersion);

        RefusedWith(AttachmentVersionRules.ContentChangedCode, await Assert.ThrowsAsync<AuthorityException>(() => CommitStagedAsync(h, operation, staged)));
        Assert.Equal((ContentOperationStates.Aborted, "ATTACHMENT_VERSION_INVALID_AT_COMMIT"), (Operation(h, operation).OperationStateName, Operation(h, operation).OperationQuarantineReasonCode));
        Assert.Equal((before.Revision, before.Root, before.Commits, before.Leaves), (h.Head.CommittedContentRevision, Convert.ToHexString(h.Head.CommittedManifestRoot), h.Store.Commits.Count, h.Store.ManifestLeaves.Count));
    }

    [Fact]
    public async Task CommitRefusesAnUnchangedCarriedRowWhoseAcceptedContentIsUnknown()
    {
        // A head accepted before the evidence existed, committed by an operation whose Prepare recorded nothing: fail closed.
        var h = await new Harness().CreatedAsync();
        ForgetAcceptedContentEvidence(h);
        var operation = Guid.CreateVersion7();
        var staged = await SeedPreparedAsync(h, operation, h.Stage(Change(h.Rows(), h.Other, r => r.With("string_value", RowValue.Text("edited"))), operation), ContentAcceptanceRules.CurrentVersion);

        RefusedWith(AttachmentVersionRules.EvidenceMissingCode, await Assert.ThrowsAsync<AuthorityException>(() => CommitStagedAsync(h, operation, staged)));
        Assert.Equal(1, h.Head.CommittedContentRevision);
    }

    [Fact]
    public async Task AHeadWithoutContentEvidenceIsComparedWithTheVerifiedSnapshotAndGainsEvidenceOnItsNextCommit()
    {
        var h = await new Harness().CreatedAsync();
        ForgetAcceptedContentEvidence(h);

        // Prepare compares with the verified current row, so new bytes under the number are refused even without stored evidence.
        RefusedWith(AttachmentVersionRules.ContentChangedCode, await Assert.ThrowsAsync<AuthorityException>(() => h.WriteAsync(h.Owner, NewBytesUnderTheAcceptedNumber(h), withCurrent: true)));

        // An unchanged carried row commits on what Prepare verified, and the accepted leaf now carries the evidence.
        var operation = Guid.CreateVersion7();
        var candidate = h.Stage(Change(h.Rows(), h.Other, r => r.With("string_value", RowValue.Text("edited"))), operation);
        var prepared = await h.PrepareAsync(h.Owner, operation, candidate, withCurrent: true);
        var observed = h.Store.CandidateLeaves.Single(l => l.ContentChangeOperationId == operation && l.ManifestItemId == h.Document);
        Assert.Equal(1, observed.AcceptedAttachmentVersionNumber);
        Assert.Equal(32, observed.AcceptedAttachmentContentFingerprint!.Length);
        Assert.All(h.Store.CandidateLeaves.Where(l => l.ContentChangeOperationId == operation && l.ManifestItemId != h.Document), l => Assert.Null(l.AcceptedAttachmentContentFingerprint));

        Assert.Equal(2, (await h.CommitAsync(h.Owner, prepared, candidate)).CommittedContentRevision);
        var accepted = h.Store.ManifestLeaves.Single(l => l.RecordManifestCommitId == h.Head.CurrentRecordManifestCommitId && l.ManifestItemId == h.Document);
        Assert.Equal(observed.AcceptedAttachmentContentFingerprint, accepted.AttachmentContentFingerprint);
    }

    [Fact]
    public async Task DisagreeingAcceptedEvidenceFailsClosedAtCommit()
    {
        var h = await new Harness().CreatedAsync();
        var operation = Guid.CreateVersion7();
        var candidate = h.Stage(Change(h.Rows(), h.Other, r => r.With("string_value", RowValue.Text("edited"))), operation);
        var prepared = await h.PrepareAsync(h.Owner, operation, candidate, withCurrent: true);
        h.Store.CandidateLeaves.Single(l => l.ContentChangeOperationId == operation && l.ManifestItemId == h.Document).AcceptedAttachmentContentFingerprint = [.. Enumerable.Repeat((byte)1, 32)];

        RefusedWith(AttachmentVersionRules.EvidenceMissingCode, await Assert.ThrowsAsync<AuthorityException>(() => h.CommitAsync(h.Owner, prepared, candidate)));
        Assert.Equal(1, h.Head.CommittedContentRevision);
    }

    [Fact]
    public async Task AShareReplacementStillNeedsExactlyTheNextVersionAndTheOwnersCarriedRowsAreUnaffected()
    {
        var h = await new Harness().CreatedAsync();
        var binding = ReplaceShare(h);
        await Assert.ThrowsAsync<AuthorityException>(() => PrepareReplacementAsync(h, NewBytesUnderTheAcceptedNumber(h)));
        var (prepared, candidate) = await PrepareReplacementAsync(h, NextVersion(h));
        await h.CommitAsync(h.Contributor, prepared, candidate);
        Assert.Equal(2, binding.SharedThroughVersionNumber);

        Assert.Equal(3, (await h.WriteAsync(h.Owner, Change(NextVersion(h), h.Other, r => r.With("string_value", RowValue.Text("edited"))), withCurrent: true)).CommittedContentRevision);
    }
}
