using AuthorizationAuthority.Application.Common;
using AuthorizationAuthority.Application.Common.Ports;
using AuthorizationAuthority.Domain.Features.Organizations;
using AuthorizationAuthority.Domain.Features.Sharing;

namespace AuthorizationAuthority.Application.Features.SecurityCommands;

public static partial class CommandPlanner
{
    public const string ShareExpiryWorkloadName = "share-expiry-worker";

    /// <summary>
    /// C10 M1-WP6 (WP-0 Section 4): the plan of an admitted ExpireShare command. It only removes access: the share's own edges and
    /// those of its Active descendants are removed, the share becomes <c>Expired</c> and each descendant <c>Invalidated</c>
    /// (<see cref="ShareInvalidationReasons.UpstreamExpired"/>). <see cref="ExpireSharePlanGuard"/> refuses anything else before
    /// the plan is applied.
    /// </summary>
    internal static async Task<CommandPlan> PlanExpireShareAsync(IAuthorityScope scope, Guid organizationId, AuthorityResource share, ShareResourceMetadata metadata,
        DesiredStateGraph graph, DateTimeOffset evaluatedOn, Guid expirySourceCommandId, CancellationToken cancellationToken)
    {
        var record = share.ParentResourceEntityId ?? throw AuthorityException.Invalid("share is not bound to a record");
        var descendants = await ReadRevokedShareDescendantsAsync(scope, organizationId, record, share.ResourceEntityId, cancellationToken).ConfigureAwait(false);
        var expiresOn = metadata.ShareExpiresOn ?? throw AuthorityException.Invalid("share has no expiry");
        var plan = new CommandPlan
        {
            NormalizedPayloadJson = CanonicalJson.Serialize(new
            {
                RecordShareId = share.ResourceEntityId,
                FormRecordId = record,
                ShareExpiresOn = expiresOn,
                EvaluatedOn = evaluatedOn,
                metadata.ShareRevision,
                ExpirySourceSecurityCommandId = expirySourceCommandId,
                WorkloadName = ShareExpiryWorkloadName,
                InvalidatedDescendantShareIds = descendants.Select(d => d.Metadata.RecordShareId).Order().ToArray(),
                InvalidationReasonCode = descendants.Count == 0 ? null : ShareInvalidationReasons.UpstreamExpired,
            }),
            CommandReasonOverride = "share expired (scheduled at grant)",
            ShareExpiry = new ShareExpiryEvidence(share.ResourceEntityId, record, expiresOn, evaluatedOn, metadata.ShareRevision, expirySourceCommandId),
            InvalidationSourceShareId = share.ResourceEntityId,
        };

        var affected = descendants.Select(d => d.Metadata.RecordShareId).Append(share.ResourceEntityId);
        plan.RemoveEdges.AddRange(affected.SelectMany(id => graph.EdgesWithSubject(ResourceTypeNames.RecordShare, id).Concat(graph.EdgesOf(ResourceTypeNames.RecordShare, id))).Distinct());
        plan.InvalidatedDescendantShares.AddRange(descendants.Select(d => (d.Resource, d.Metadata, ShareInvalidationReasons.UpstreamExpired)));
        plan.RevokeResources.Add(share);
        return plan;
    }
}

/// <summary>
/// C10 M1-WP6 (WP-0 Section 4 "Narrow capability", tests 5 and 18): an ExpireShare plan may contain only the removal of the share's
/// own edges and its descendants' edges, the share's transition to <c>Expired</c> and the descendants' transitions to
/// <c>Invalidated</c> (<c>UpstreamExpired</c>). Any added edge or resource, role, scope, policy, approval, publication, membership,
/// binding or session change, owner or team edge, or any other resource kind is refused before the plan is applied.
/// </summary>
internal static class ExpireSharePlanGuard
{
    public static void Require(CommandPlan plan, Guid recordShareId, Guid formRecordId)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var descendants = plan.InvalidatedDescendantShares.Select(d => d.Resource.ResourceEntityId).ToHashSet();
        var allowed = descendants.Append(recordShareId).ToHashSet();
        var violations = new List<string>();
        void Refuse(bool condition, string what)
        {
            if (condition)
            {
                violations.Add(what);
            }
        }

        Refuse(plan.AddEdges.Count > 0, "added edges");
        Refuse(plan.RegisterResources.Count > 0 || plan.ReactivateResources.Count > 0, "added or reactivated resources");
        Refuse(plan.RevokeResources.Count != 1 || plan.RevokeResources[0].ResourceEntityId != recordShareId ||
               plan.RevokeResources[0].ResourceTypeName != ResourceTypeNames.RecordShare || plan.RevokeResources[0].ParentResourceEntityId != formRecordId ||
               plan.RevokeResources[0].ResourceLifecycleState != ResourceLifecycleStates.Active, "a transition other than the share itself leaving Active");
        Refuse(plan.InvalidatedDescendantShares.Any(d => d.Reason != ShareInvalidationReasons.UpstreamExpired ||
                                                          d.Resource.ResourceTypeName != ResourceTypeNames.RecordShare ||
                                                          d.Resource.ParentResourceEntityId != formRecordId ||
                                                          d.Resource.ResourceLifecycleState != ResourceLifecycleStates.Active ||
                                                          d.Resource.ResourceEntityId == recordShareId), "a descendant transition other than UpstreamExpired");
        Refuse(plan.InvalidationSourceShareId != recordShareId, "another invalidation source");
        Refuse(plan.RemoveEdges.Any(e => !(e.ResourceTypeName == ResourceTypeNames.RecordShare && allowed.Contains(e.ResourceEntityId)) &&
                                         !(e.SubjectTypeName == ResourceTypeNames.RecordShare && allowed.Contains(e.SubjectEntityId))), "removal of an edge of another resource");
        Refuse(plan.RemoveEdges.Any(e => e.RelationshipName is "owner" or "member"), "an owner or team membership edge");
        Refuse(plan.ShareMetadata is not null || plan.ShareMetadataUpdate is not null || plan.ShareScopeChange is not null, "a share scope, role or revision change");
        Refuse(plan.AddDocumentBindings.Count > 0 || plan.UpdatedDocumentBindings.Count > 0 || plan.RemovedDocumentBindings.Count > 0, "a document binding change");
        Refuse(plan.ProtectionPolicy is not null || plan.SearchPolicyToAdvance is not null, "a policy change");
        Refuse(plan.PublishedVersion is not null || plan.NewPublication is not null || plan.ResolvedPublications.Count > 0 || plan.NewFieldFormBindings.Count > 0, "a form publication change");
        Refuse(plan.NewApprovals.Count > 0 || plan.ChangedApprovals.Count > 0, "a search approval change");
        Refuse(plan.NewProposal is not null || plan.ConsumedProposal is not null || plan.BindingToDisable is not null || plan.BindingToEnable is not null ||
               plan.SessionsToRevoke.Count > 0 || plan.NewBinding is not null, "an identity, proposal or session change");
        Refuse(plan.NewMembershipRequest is not null || plan.ExpiredMembershipRequests.Count > 0 || plan.CancelledMembershipRequest is not null ||
               plan.ApprovedMembershipRequest is not null || plan.InvalidatedPlatformApprovals.Count > 0, "a membership request or approval change");
        Refuse(plan.ShareExpiry is not { } evidence || evidence.RecordShareId != recordShareId || evidence.FormRecordId != formRecordId, "missing or mismatched expiry evidence");
        if (violations.Count > 0)
        {
            throw new InvalidOperationException("ExpireShare plan refused by the plan-shape guard: " + string.Join(", ", violations));
        }
    }
}
