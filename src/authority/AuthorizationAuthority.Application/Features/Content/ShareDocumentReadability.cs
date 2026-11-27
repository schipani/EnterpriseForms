using AuthorizationAuthority.Application.Common;
using AuthorizationAuthority.Application.Common.Ports;
using AuthorizationAuthority.Application.Features.Authorization;
using AuthorizationAuthority.Contracts.V1;
using AuthorizationAuthority.Domain.Features.Content;
using AuthorizationAuthority.Domain.Features.Organizations;

namespace AuthorizationAuthority.Application.Features.Content;

/// <summary>
/// C10 WP8-A (WP-0 Section 3.6): which attachments of a verified projection a share recipient may view and download. An attachment
/// leaf is released only when all hold: it carries Authority-verified version evidence; the pinned SpiceDB decision allows
/// <c>document_item#read</c> (with the access-time expiry overlay); and a share of the record that the subject holds — Active, its
/// lineage Active at the decision instant, its <c>shared_via</c> edge on that document live — has a binding for the attachment that
/// allows viewing and whose version boundary covers the leaf's version. Download additionally needs that binding's download flag.
/// A grant for one attachment or version never covers another; nothing here can widen a field scope.
/// </summary>
internal static class ShareDocumentReadability
{
    /// <param name="decide">The pinned decision of the caller's verification (ContentService pins and completes it; this helper never reads SpiceDB).</param>
    /// <param name="decisionInstant">The instant of that pinned decision (lineage expiry is evaluated at it).</param>
    public static async Task<(List<VerifiedProjectionItem> Viewable, List<VerifiedProjectionItem> Downloadable)> EvaluateAsync(
        Func<IReadOnlyList<SpiceDbCheck>, Task<IReadOnlyList<SpiceDbCheckOutcome>>> decide, DateTimeOffset decisionInstant, IAuthorityScope scope, Guid organizationId, Guid formRecordId, Guid userId,
        IReadOnlyList<RecordManifestLeaf> expected, CancellationToken cancellationToken)
    {
        var attachments = expected.Where(l => l.ManifestItemKindCode == (byte)ManifestItemKind.Attachment && l.AttachmentVersionNumber is > 0).ToList();
        if (attachments.Count == 0)
        {
            return ([], []);
        }

        var subject = userId.ToString("D");
        var checks = attachments.Select(l => new SpiceDbCheck(ResourceTypeNames.DocumentItem, l.ManifestItemId.ToString("D"), "read", ResourceTypeNames.User, subject)).ToList();
        var outcomes = await decide(checks).ConfigureAwait(false);
        var allowed = attachments.Where((_, i) => outcomes[i] == SpiceDbCheckOutcome.Allowed).ToList();
        if (allowed.Count == 0)
        {
            return ([], []);
        }

        var shares = (await scope.GetActiveResourcesByParentAsync(organizationId, formRecordId, [ResourceTypeNames.RecordShare], cancellationToken).ConfigureAwait(false))
            .Where(r => r.OrganizationId == organizationId && r.ResourceLifecycleState == ResourceLifecycleStates.Active).ToDictionary(r => r.ResourceEntityId);
        var edges = (await scope.GetActiveShareEdgesAsync(organizationId, [.. shares.Keys], cancellationToken).ConfigureAwait(false))
            .Where(e => e.OrganizationId == organizationId && e.RemovedBySecurityCommandId is null).ToList();
        var teams = edges.Where(e => e.ResourceTypeName == ResourceTypeNames.RecordShare && e.SubjectTypeName == ResourceTypeNames.Team).Select(e => e.SubjectEntityId).Distinct().ToList();
        var memberOf = teams.Count == 0 ? [] : (await scope.GetActiveTeamMemberEdgesAsync(organizationId, teams, cancellationToken).ConfigureAwait(false))
            .Where(e => e.OrganizationId == organizationId && e.RemovedBySecurityCommandId is null && e.SubjectTypeName == ResourceTypeNames.User && e.SubjectEntityId == userId)
            .Select(e => e.ResourceEntityId).ToHashSet();
        var held = edges.Where(e => e.ResourceTypeName == ResourceTypeNames.RecordShare && e.RelationshipName is "reader" or "contributor" or "manager" &&
                                    ((e.SubjectTypeName == ResourceTypeNames.User && e.SubjectRelationName.Length == 0 && e.SubjectEntityId == userId) ||
                                     (e.SubjectTypeName == ResourceTypeNames.Team && e.SubjectRelationName == "participant" && memberOf.Contains(e.SubjectEntityId))))
            .Select(e => e.ResourceEntityId).Where(shares.ContainsKey).ToHashSet();

        var live = new HashSet<Guid>();
        foreach (var shareId in held)
        {
            if (ShareLineageExpiry.Evaluate(await LineageAsync(scope, organizationId, shareId, cancellationToken).ConfigureAwait(false), organizationId, formRecordId, shareId, decisionInstant).Status == ShareLineageStatus.Active)
            {
                live.Add(shareId);
            }
        }

        var bindings = live.Count == 0 ? [] : (await scope.GetShareDocumentBindingsAsync(organizationId, [.. live], cancellationToken).ConfigureAwait(false))
            .Where(b => b.OrganizationId == organizationId && live.Contains(b.RecordShareId) &&
                        edges.Any(e => e.ResourceTypeName == ResourceTypeNames.DocumentItem && e.ResourceEntityId == b.FileAttachmentId && e.RelationshipName == "shared_via" &&
                                       e.SubjectTypeName == ResourceTypeNames.RecordShare && e.SubjectEntityId == b.RecordShareId))
            .ToList();

        var viewable = new List<VerifiedProjectionItem>();
        var downloadable = new List<VerifiedProjectionItem>();
        foreach (var leaf in allowed)
        {
            var covering = bindings.Where(b => b.FileAttachmentId == leaf.ManifestItemId && b.CanViewDocument && leaf.AttachmentVersionNumber <= b.SharedThroughVersionNumber).ToList();
            if (covering.Count == 0)
            {
                continue;
            }

            var item = new VerifiedProjectionItem(ManifestItemKind.Attachment, leaf.ManifestItemId);
            viewable.Add(item);
            if (covering.Any(b => b.CanDownloadDocument))
            {
                downloadable.Add(item);
            }
        }

        return (viewable, downloadable);
    }

    private static async Task<Dictionary<Guid, ShareLineageNode>> LineageAsync(IAuthorityScope scope, Guid organizationId, Guid shareId, CancellationToken cancellationToken)
    {
        var chain = new Dictionary<Guid, ShareLineageNode>();
        var cursor = (Guid?)shareId;
        while (cursor is { } id && chain.Count < 3 && !chain.ContainsKey(id))
        {
            if (await scope.FindResourceAsync(organizationId, ResourceTypeNames.RecordShare, id, cancellationToken).ConfigureAwait(false) is not { } resource)
            {
                break;
            }

            var metadata = await scope.FindShareMetadataAsync(organizationId, id, cancellationToken).ConfigureAwait(false);
            chain.Add(id, new ShareLineageNode(resource, metadata));
            cursor = metadata?.ParentRecordShareId;
        }

        return chain;
    }
}
