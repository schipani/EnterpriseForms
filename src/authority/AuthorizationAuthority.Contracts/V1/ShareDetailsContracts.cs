namespace AuthorizationAuthority.Contracts.V1;

/// <summary>
/// C10 M2 (WP-0 Section 3.5, D10-3): the Authority's committed state of one share, read behind the organization gate (every
/// committed revision archived and applied to the projection), so <see cref="ShareRevision"/> is the revision
/// <c>UpdateShareScope</c> compares against and the business ETag <c>"{recordShareId}:{shareRevision}"</c> is authoritative.
/// </summary>
/// <param name="LifecycleState">Active, Revoked, Expired or Invalidated.</param>
/// <param name="RecipientKind">"User" or "Team"; null once the share holds no role edge (it is no longer Active).</param>
/// <param name="RecipientRole">Reader, Contributor or Manager; null once the share holds no role edge.</param>
/// <param name="FieldItemIds">The share's current field items, ascending; empty once it is no longer Active.</param>
/// <param name="EffectiveExpiresOn">The lineage minimum of the expiries (WP-0 Section 3.1); null when no share in the lineage expires.</param>
/// <param name="GrantorUserId">The Authority user whose committed command registered the share; null when unknown.</param>
/// <param name="AuthorizationRevision">The organization revision the read was taken at (distinct from <see cref="ShareRevision"/>).</param>
public sealed record ShareDetails(
    Guid RecordShareId,
    Guid FormRecordId,
    string LifecycleState,
    string? RecipientKind,
    Guid? RecipientId,
    string? RecipientRole,
    IReadOnlyList<Guid> FieldItemIds,
    DateTimeOffset? ShareExpiresOn,
    DateTimeOffset? EffectiveExpiresOn,
    long ShareRevision,
    Guid? ParentRecordShareId,
    int DelegationDepth,
    bool LegacyLineageUnresolved,
    string? InvalidationReasonCode,
    Guid? GrantorUserId,
    long AuthorizationRevision,
    IReadOnlyList<ShareDocumentDetail>? Documents = null);

/// <summary>C10 WP8: one document of a share: its flags and the version boundary the Authority bound from verified evidence.</summary>
public sealed record ShareDocumentDetail(Guid FileAttachmentId, bool CanViewDocument, bool CanDownloadDocument, bool CanReplaceDocument, int SharedThroughVersionNumber);
