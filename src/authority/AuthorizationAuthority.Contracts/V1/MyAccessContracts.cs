namespace AuthorizationAuthority.Contracts.V1;

/// <summary>
/// C10 M2 (WP-0 Section 3.5, PG-066): the caller's own access, from the Authority's committed state behind the open gate. Names no
/// other principal. A record appears once per source; a share-derived entry counts only when the share and its lineage are Active
/// now. Expiring-soon entries are live share entries whose effective expiry falls within <see cref="ExpiringWithinDays"/>.
/// </summary>
/// <param name="Truncated">True when more than <see cref="MaximumEntries"/> entries exist; only the first are returned.</param>
public sealed record MyAccess(IReadOnlyList<MyAccessEntry> Entries, bool Truncated, int ExpiringWithinDays, long AuthorizationRevision)
{
    public const int MaximumEntries = 500;
}

/// <param name="AccessSource">Owner, OwningTeam, DirectShare or TeamShare.</param>
/// <param name="TeamId">For OwningTeam and TeamShare: the caller's team that confers the access.</param>
/// <param name="RecipientRole">For shares: Reader, Contributor or Manager.</param>
/// <param name="EffectiveExpiresOn">For shares: the lineage minimum; null when it does not expire.</param>
public sealed record MyAccessEntry(Guid FormRecordId, string AccessSource, Guid? RecordShareId, Guid? TeamId, string? RecipientRole, DateTimeOffset? EffectiveExpiresOn, bool ExpiringSoon);

/// <summary>
/// C10 M2 (WP-0 Section 3.5, PG-023): what a share's recipient can do through it now and why, naming no other principal. Status is
/// Active only when the share and its lineage are Active at the Authority's clock; otherwise it confers nothing.
/// </summary>
/// <param name="Status">Active, Expired, Invalid (inactive or broken lineage) or LegacyUnresolved.</param>
/// <param name="Lineage">The share and its committed ancestors, nearest first: id, depth, own expiry.</param>
public sealed record ShareEffectiveScope(Guid RecordShareId, Guid FormRecordId, string Status, string? RecipientKind, string? RecipientRole, DateTimeOffset? EffectiveExpiresOn,
    IReadOnlyList<Guid> FieldItemIds, IReadOnlyList<ShareDocumentDetail> Documents, bool CanDelegate, IReadOnlyList<ShareLineageStep> Lineage, long AuthorizationRevision);

public sealed record ShareLineageStep(Guid RecordShareId, int DelegationDepth, DateTimeOffset? ShareExpiresOn);

/// <summary>C10 M2 (D8-4, PG-053): the Active records a member directly owns (ids only), for administrator reassignment.</summary>
public sealed record MemberOwnedRecords(Guid MemberUserId, IReadOnlyList<Guid> FormRecordIds, bool Truncated, long AuthorizationRevision);
