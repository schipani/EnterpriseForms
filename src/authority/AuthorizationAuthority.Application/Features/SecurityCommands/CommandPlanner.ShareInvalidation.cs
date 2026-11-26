using AuthorizationAuthority.Application.Common;
using AuthorizationAuthority.Application.Common.Ports;
using AuthorizationAuthority.Domain.Features.Organizations;
using AuthorizationAuthority.Domain.Features.Sharing;

namespace AuthorizationAuthority.Application.Features.SecurityCommands;

public static partial class CommandPlanner
{
    private static async Task<List<(AuthorityResource Resource, ShareResourceMetadata Metadata)>> ReadRevokedShareDescendantsAsync(
        IAuthorityScope scope, Guid organizationId, Guid formRecordId, Guid rootId, CancellationToken cancellationToken)
    {
        var root = await scope.FindShareMetadataAsync(organizationId, rootId, cancellationToken).ConfigureAwait(false);
        int? rootDepth = root is { LegacyLineageUnresolved: false, DelegationDepth: >= 1 and <= MaximumShareDelegationDepth } &&
            root.OrganizationId == organizationId && root.RecordShareId == rootId ? root.DelegationDepth : null;

        var pending = new Queue<(Guid Id, int? Depth)>();
        pending.Enqueue((rootId, rootDepth));
        var visited = new HashSet<Guid> { rootId };
        var result = new List<(AuthorityResource Resource, ShareResourceMetadata Metadata)>();
        while (pending.TryDequeue(out var parent))
        {
            var children = await scope.FindShareMetadataByParentAsync(organizationId, parent.Id, cancellationToken).ConfigureAwait(false);
            foreach (var child in children)
            {
                if (parent.Depth is not { } depth || depth >= MaximumShareDelegationDepth ||
                    child.OrganizationId != organizationId || child.RecordShareId == Guid.Empty ||
                    child.ParentRecordShareId != parent.Id || child.DelegationDepth != depth + 1 ||
                    child.ResourceTypeName != ResourceTypeNames.RecordShare || child.LegacyLineageUnresolved ||
                    !visited.Add(child.RecordShareId))
                {
                    throw AuthorityException.Invalid("share descendants have inconsistent lineage");
                }

                var resource = await scope.FindResourceAsync(organizationId, ResourceTypeNames.RecordShare,
                    child.RecordShareId, cancellationToken).ConfigureAwait(false);
                if (resource is null || resource.OrganizationId != organizationId || resource.ParentResourceEntityId != formRecordId ||
                    resource.ResourceEntityId != child.RecordShareId || resource.ResourceTypeName != ResourceTypeNames.RecordShare)
                {
                    throw AuthorityException.Invalid("share descendants have inconsistent registry bindings");
                }

                if (resource.ResourceLifecycleState == ResourceLifecycleStates.Active)
                {
                    result.Add((resource, child));
                }
                else if (resource.ResourceLifecycleState is not
                    (ResourceLifecycleStates.Revoked or ResourceLifecycleStates.Expired or ResourceLifecycleStates.Invalidated))
                {
                    throw AuthorityException.Invalid("share descendant state cannot be determined");
                }

                pending.Enqueue((child.RecordShareId, child.DelegationDepth));
            }
        }

        return result;
    }
}
