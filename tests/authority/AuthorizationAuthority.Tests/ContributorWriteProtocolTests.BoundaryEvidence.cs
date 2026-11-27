using System.Text.Json;
using AuthorizationAuthority.Application.Common;
using AuthorizationAuthority.Application.Features.Content;
using AuthorizationAuthority.Domain.Features.Content;
using AuthorizationAuthority.Domain.Features.Journal;
using Xunit;
using static AuthorizationAuthority.Tests.ContributorWriteFixtures;

namespace AuthorizationAuthority.Tests;

/// <summary>
/// C10 owner decision 3 (independent review D-5) through the real Prepare and Commit: a replacement through a share that extends the
/// share's boundary advances that share's revision (its ETag) and appends one <c>ShareDocumentBoundaryExtended</c> entry to the
/// hash-chained, archived audit stream, in the transaction that accepts the content. A refused or aborted commit, and the idempotent
/// replay of an accepted one, change neither. The stale-revision refusal itself is the existing UpdateShareScope rule
/// (<c>STALE_SHARE_REVISION</c>, ShareLifecycleJournalTests / ShareNarrowingCascadeTests).
/// </summary>
public sealed partial class ContributorWriteProtocolTests
{
    private static List<SecurityJournalEntry> BoundaryEntries(Harness h) =>
        h.Store.Journal.Where(j => j.JournalEventKind == ContentService.ShareDocumentBoundaryExtendedEvent).ToList();

    [Fact]
    public async Task AnAcceptedReplacementAdvancesTheShareRevisionAndJournalsTheExtensionOnce()
    {
        var h = await new Harness().CreatedAsync();
        var binding = ReplaceShare(h);
        var share = h.Store.ShareMetadata.Single(m => m.RecordShareId == binding.RecordShareId);
        var other = ReplaceShare(h, attachment: Guid.CreateVersion7(), replace: false); // another share of the record: untouched
        var otherShare = h.Store.ShareMetadata.Single(m => m.RecordShareId == other.RecordShareId);
        var auditBefore = h.State.AuditJournalRevision;

        var (prepared, candidate) = await PrepareReplacementAsync(h, NextVersion(h));
        var accepted = await h.CommitAsync(h.Contributor, prepared, candidate);

        Assert.Equal((2, 2L, 1L), (binding.SharedThroughVersionNumber, share.ShareRevision, otherShare.ShareRevision));
        var entry = Assert.Single(BoundaryEntries(h));
        Assert.Equal((JournalStreamNames.Audit, auditBefore + 1), (entry.JournalStreamName, entry.JournalStreamRevision));
        using var body = JsonDocument.Parse(entry.JournalEventBodyJson);
        var root = body.RootElement.GetProperty("body");
        Assert.Equal(prepared.OperationId, root.GetProperty("content_change_operation_id").GetGuid());
        Assert.Equal(h.Record, root.GetProperty("form_record_id").GetGuid());
        Assert.Equal(binding.RecordShareId, root.GetProperty("record_share_id").GetGuid());
        Assert.Equal(binding.ShareDocumentBindingId, root.GetProperty("share_document_binding_id").GetGuid());
        Assert.Equal((1L, 2L), (root.GetProperty("previous_share_revision").GetInt64(), root.GetProperty("share_revision").GetInt64()));
        Assert.Equal((1, 2), (root.GetProperty("previous_shared_through_version").GetInt32(), root.GetProperty("shared_through_version").GetInt32()));
        Assert.Equal(accepted.CommittedContentRevision, root.GetProperty("committed_content_revision").GetInt64());
        Assert.DoesNotContain(h.Document.ToString(), entry.JournalEventBodyJson, StringComparison.OrdinalIgnoreCase); // no document id
        var timeline = AuthorizationAuthority.Application.Features.Audit.SecurityCommandTimelineCatalog.BuildAuditEnvelope(entry.JournalEventKind, entry.JournalEventBodyJson);
        Assert.Empty(timeline.WithheldFields); // every field is catalogued for the Security Auditor timeline
        Assert.Equal(11, timeline.Details.Count);

        // The idempotent replay of the accepted commit extends nothing again.
        await h.CommitAsync(h.Contributor, prepared, candidate);
        Assert.Equal((2, 2L), (binding.SharedThroughVersionNumber, share.ShareRevision));
        Assert.Single(BoundaryEntries(h));
    }

    [Fact]
    public async Task ARefusedOrAbortedReplacementLeavesTheShareRevisionAndJournalUnchanged()
    {
        var h = await new Harness().CreatedAsync();
        var binding = ReplaceShare(h);
        var share = h.Store.ShareMetadata.Single(m => m.RecordShareId == binding.RecordShareId);

        await Assert.ThrowsAsync<AuthorityException>(() => PrepareReplacementAsync(h, NextVersion(h, version: 3))); // refused at Prepare
        var (prepared, candidate) = await PrepareReplacementAsync(h, NextVersion(h));
        binding.CanReplaceDocument = false; // the owner narrows before Commit: aborted
        await Assert.ThrowsAsync<AuthorityException>(() => h.CommitAsync(h.Contributor, prepared, candidate));

        Assert.Equal(ContentOperationStates.Aborted, h.Store.Operations.Single(o => o.ContentChangeOperationId == prepared.OperationId).OperationStateName);
        Assert.Equal((1, 1L), (binding.SharedThroughVersionNumber, share.ShareRevision));
        Assert.Empty(BoundaryEntries(h));
    }

    [Fact]
    public async Task AnOwnersNewVersionExtendsNoShareAndJournalsNoExtension()
    {
        var h = await new Harness().CreatedAsync();
        var binding = ReplaceShare(h);
        var share = h.Store.ShareMetadata.Single(m => m.RecordShareId == binding.RecordShareId);

        await h.WriteAsync(h.Owner, NextVersion(h), withCurrent: true);

        Assert.Equal((1, 1L), (binding.SharedThroughVersionNumber, share.ShareRevision));
        Assert.Empty(BoundaryEntries(h));
    }
}
