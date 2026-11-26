using AuthorizationAuthority.Application.Common;
using AuthorizationAuthority.Application.Common.Ports;
using AuthorizationAuthority.Application.Features.Authorization;
using AuthorizationAuthority.Contracts.V1;
using AuthorizationAuthority.Domain.Features.Organizations;

namespace AuthorizationAuthority.Application.Features.SecurityCommands;

public static partial class CommandPlanner
{
    private const int MaximumShareDelegationDepth = 3;

    private static async Task<int> RequireShareDelegationAsync(
        IAuthorityScope scope,
        DesiredStateGraph graph,
        Guid organizationId,
        Guid authorityUserId,
        CreateSharePayload payload,
        DateTimeOffset? expiresOn,
        DateTimeOffset at,
        CancellationToken cancellationToken)
    {
        if (payload.ParentRecordShareId is not { } parentId)
        {
            throw AuthorityException.Forbidden("non-owner delegation requires a named Manager share");
        }

        var sharesOnRecord = graph.ShareIdsOfRecord(payload.FormRecordId).ToHashSet();
        if (!sharesOnRecord.Contains(parentId) ||
            !await HoldsActiveManagerShareAsync(scope, graph, organizationId, authorityUserId, parentId, cancellationToken).ConfigureAwait(false))
        {
            throw AuthorityException.Forbidden("actor does not hold Manager on the specified parent share");
        }

        var chain = new Dictionary<Guid, ShareLineageNode>();
        var cursor = (Guid?)parentId;
        while (cursor is { } shareId && chain.Count < MaximumShareDelegationDepth &&
               sharesOnRecord.Contains(shareId) && !chain.ContainsKey(shareId))
        {
            var resource = await scope.FindResourceAsync(organizationId, ResourceTypeNames.RecordShare, shareId, cancellationToken).ConfigureAwait(false);
            if (resource is null)
            {
                break;
            }

            var metadata = await scope.FindShareMetadataAsync(organizationId, shareId, cancellationToken).ConfigureAwait(false);
            chain.Add(shareId, new ShareLineageNode(resource, metadata));
            cursor = metadata?.ParentRecordShareId;
        }

        var parent = ShareLineageExpiry.Evaluate(chain, organizationId, payload.FormRecordId, parentId, at);
        if (parent.Status != ShareLineageStatus.Active)
        {
            throw AuthorityException.Invalid("parent share must have active, resolved lineage in the same record");
        }

        var policy = await scope.FindProtectionPolicyAsync(organizationId, cancellationToken).ConfigureAwait(false);
        var maximumDepth = policy?.MaximumDelegationDepth ?? MaximumShareDelegationDepth;
        var depth = chain[parentId].Metadata!.DelegationDepth + 1;
        if (depth > Math.Min(maximumDepth, MaximumShareDelegationDepth))
        {
            throw new AuthorityException(AuthorityErrorCodes.DelegationDepthExceeded, 422, false, "delegation would exceed the organization's maximum depth");
        }

        if (payload.RecipientRole == RecipientRoles.Manager && depth == MaximumShareDelegationDepth)
        {
            throw new AuthorityException(AuthorityErrorCodes.ManagerAtMaximumDepth, 422, false, "Manager shares cannot be granted at depth three");
        }

        if (parent.EffectiveExpiry is { } cap && (expiresOn is null || expiresOn > cap))
        {
            throw AuthorityException.Invalid("delegated expiry exceeds the named parent's effective expiry");
        }

        foreach (var shareId in chain.Keys)
        {
            if (!payload.FieldItemIds.All(graph.FieldItemsOfShare(shareId).Contains))
            {
                throw AuthorityException.Invalid("delegation exceeds the named parent's effective field scope");
            }
        }

        return depth;
    }

    private static async Task<bool> HoldsActiveManagerShareAsync(
        IAuthorityScope scope, DesiredStateGraph graph, Guid organizationId, Guid userId, Guid shareId, CancellationToken cancellationToken)
    {
        foreach (var relation in graph.EdgesOf(ResourceTypeNames.RecordShare, shareId, "manager"))
        {
            if (relation.SubjectTypeName == ResourceTypeNames.User && relation.SubjectEntityId == userId &&
                relation.SubjectRelationName.Length == 0)
            {
                return true;
            }

            if (relation.SubjectTypeName == ResourceTypeNames.Team && relation.SubjectRelationName == "participant" &&
                graph.IsTeamMember(userId, relation.SubjectEntityId) &&
                await scope.FindResourceAsync(organizationId, ResourceTypeNames.Team, relation.SubjectEntityId, cancellationToken).ConfigureAwait(false)
                    is { ResourceLifecycleState: ResourceLifecycleStates.Active } team &&
                team.OrganizationId == organizationId && team.ResourceEntityId == relation.SubjectEntityId)
            {
                return true;
            }
        }

        return false;
    }
}
