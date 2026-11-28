using AuthorizationAuthority.Domain.Features.Organizations;

namespace AuthorizationAuthority.Domain.Features.Sharing;

/// <summary>
/// C10 M1-WP1: share-specific metadata stored alongside the generic <see cref="Organizations.AuthorityResource"/> for
/// resources of type <c>record_share</c>. The separation keeps share columns out of the generic resource table and
/// preserves graph-digest compatibility for non-share resources. Joined by the resource entity id (record_share_id).
/// </summary>
public sealed class ShareResourceMetadata
{
    /// <summary>Surrogate primary key (UUID v7).</summary>
    public Guid ShareResourceMetadataId { get; set; }

    /// <summary>Organization to which the share belongs (FK to organization_authorization_states).</summary>
    public Guid OrganizationId { get; set; }

    /// <summary>The record share's entity id (matches authority_resources.resource_entity_id); unique per organization.</summary>
    public Guid RecordShareId { get; set; }

    public string ResourceTypeName { get; set; } = ResourceTypeNames.RecordShare;

    /// <summary>
    /// Optional expiry time (UTC). The share is denied at access time from this instant, using the Authority's TimeProvider.
    /// Convergence processing may later transition the resource to Expired, but access control never depends on that.
    /// </summary>
    public DateTimeOffset? ShareExpiresOn { get; set; }

    /// <summary>
    /// Monotonically increasing revision, starting at 1 on creation and incremented by every <c>UpdateShareScope</c> command.
    /// Used for optimistic concurrency (ETag) and idempotent expiry convergence.
    /// </summary>
    public long ShareRevision { get; set; } = 1;

    /// <summary>
    /// For delegated shares: the parent share from which this share's scope is derived and bounded. Null for owner-granted
    /// shares (depth 1). Must reference an Active share in the same organization and on the same record.
    /// </summary>
    public Guid? ParentRecordShareId { get; set; }

    /// <summary>
    /// Delegation depth: 1 for owner-granted shares; 2 for shares granted by a Manager holding a depth-1 share; 3 is the
    /// platform maximum. The organization's policy may lower the maximum. Manager shares at depth 3 are refused.
    /// </summary>
    public int DelegationDepth { get; set; } = 1;

    /// <summary>
    /// The security command that last set the share's expiry (CreateShare or UpdateShareScope). Used to derive the
    /// actor binding for the ExpireShare convergence command. Null if the share has no expiry.
    /// </summary>
    public Guid? ExpirySourceSecurityCommandId { get; set; }

    /// <summary>
    /// Set when the share transitions to Invalidated; records the reason (e.g., <c>UpstreamExpired</c>,
    /// <c>LineageUnresolvable</c>, <c>DelegationDepthExceeded</c>). Null while Active or Expired.
    /// </summary>
    public string? ShareInvalidationReasonCode { get; set; }

    /// <summary>
    /// C10 M1-WP1 / C-194 §5.1: marks unresolved legacy shares created before lineage tracking. Legacy shares can lack a metadata row or
    /// a recorded parent. Absence does not establish that a share was owner-granted. The dry-run tool classifies them and
    /// the migration (separately authorized) assigns lineage or invalidates them. The model/planner must distinguish
    /// unresolved legacy from new classified shares: a new share always has resolved lineage, never this flag.
    /// </summary>
    public bool LegacyLineageUnresolved { get; set; }

    /// <summary>When the share metadata row was created.</summary>
    public DateTimeOffset ShareMetadataCreatedOn { get; set; }

    /// <summary>When the share metadata was last modified (revision, expiry, invalidation, lineage resolution).</summary>
    public DateTimeOffset ShareMetadataModifiedOn { get; set; }
}

/// <summary>Reason codes for share invalidation (terminal state).</summary>
public static class ShareInvalidationReasons
{
    /// <summary>The parent share expired; this descendant is invalidated by cascade.</summary>
    public const string UpstreamExpired = "UpstreamExpired";

    /// <summary>The parent share was revoked; this descendant is invalidated.</summary>
    public const string UpstreamRevoked = "UpstreamRevoked";

    /// <summary>The parent share was narrowed and no longer covers this share's scope; this descendant is invalidated.</summary>
    public const string UpstreamNarrowed = "UpstreamNarrowed";

    /// <summary>The actor who created this share lost their path to the parent share (team removal, member deactivation).</summary>
    public const string GrantorPathLost = "GrantorPathLost";

    /// <summary>Ownership transfer ended the grantor's owner rights; descendants of their shares are invalidated.</summary>
    public const string OwnershipTransferred = "OwnershipTransferred";

    /// <summary>Legacy lineage migration: the grantor holds no covering Manager share.</summary>
    public const string LineageUnresolvable = "LineageUnresolvable";

    /// <summary>Legacy lineage migration: assignment would exceed depth 3 or place a Manager share at depth 3.</summary>
    public const string DelegationDepthExceeded = "DelegationDepthExceeded";
}
