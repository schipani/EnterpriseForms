using AuthorizationAuthority.Domain.Features.Organizations;
using AuthorizationAuthority.Domain.Features.Sharing;

namespace AuthorizationAuthority.Application.Features.Authorization;

internal enum ShareLineageStatus
{
    Active,
    Expired,
    Invalid,
    LegacyUnresolved,
}

internal sealed record ShareLineageNode(AuthorityResource Resource, ShareResourceMetadata? Metadata);

internal readonly record struct ShareLineageEvaluation(ShareLineageStatus Status, DateTimeOffset? EffectiveExpiry = null);

/// <summary>
/// Evaluates lifecycle and expiry after the caller has selected a share from the committed authorization graph.
/// The snapshot and the instant must belong to the same authorization decision.
/// </summary>
internal static class ShareLineageExpiry
{
    private const int MaximumDepth = 3;

    public static ShareLineageEvaluation Evaluate(
        IReadOnlyDictionary<Guid, ShareLineageNode> shares,
        Guid organizationId,
        Guid formRecordId,
        Guid recordShareId,
        DateTimeOffset now)
    {
        int? expectedDepth = null;
        DateTimeOffset? effectiveExpiry = null;
        for (var i = 0; i < MaximumDepth; i++)
        {
            if (!shares.TryGetValue(recordShareId, out var node) ||
                node.Resource.OrganizationId != organizationId ||
                node.Resource.ParentResourceEntityId != formRecordId ||
                node.Resource.ResourceEntityId != recordShareId ||
                node.Resource.ResourceTypeName != ResourceTypeNames.RecordShare ||
                node.Resource.ResourceLifecycleState != ResourceLifecycleStates.Active)
            {
                return new(ShareLineageStatus.Invalid);
            }

            var metadata = node.Metadata;
            if (metadata is not null &&
                (metadata.OrganizationId != organizationId || metadata.RecordShareId != recordShareId ||
                 metadata.ResourceTypeName != ResourceTypeNames.RecordShare))
            {
                return new(ShareLineageStatus.Invalid);
            }

            if (metadata is null || metadata.LegacyLineageUnresolved)
            {
                return new(i == 0 ? ShareLineageStatus.LegacyUnresolved : ShareLineageStatus.Invalid);
            }

            var depth = metadata.DelegationDepth;
            if (depth is < 1 or > MaximumDepth || (expectedDepth.HasValue && depth != expectedDepth.Value))
            {
                return new(ShareLineageStatus.Invalid);
            }

            if (metadata.ShareExpiresOn is { } expires && (effectiveExpiry is null || expires < effectiveExpiry))
            {
                effectiveExpiry = expires;
            }

            if (depth == 1)
            {
                if (metadata.ParentRecordShareId is not null)
                {
                    return new(ShareLineageStatus.Invalid);
                }

                var status = effectiveExpiry is { } cutoff && cutoff <= now
                    ? ShareLineageStatus.Expired
                    : ShareLineageStatus.Active;
                return new(status, effectiveExpiry);
            }

            if (metadata.ParentRecordShareId is not { } parent)
            {
                return new(ShareLineageStatus.Invalid);
            }

            // A parent must be exactly one level closer to the root, so cycles also fail closed.
            recordShareId = parent;
            expectedDepth = depth - 1;
        }

        return new(ShareLineageStatus.Invalid);
    }
}
