namespace AuthorizationAuthority.Domain.Features.SearchAccess;

/// <summary>
/// IX-5a (C-147): per-organization checkpoint of the Tier 2 access projection on the Authority's authorization journal stream
/// (FD-5-ALT). Advanced only by compare-and-set from revision n − 1 to n, in the same transaction as the rows derived from event
/// n. Readiness is exact equality with the organization's authorization revision and journal head digest (A-3).
/// </summary>
public sealed class SearchAccessCheckpoint
{
    public Guid SearchAccessCheckpointId { get; set; }

    public Guid OrganizationId { get; set; }

    /// <summary><see cref="SearchAccessProjectionStates"/>.</summary>
    public string AccessProjectionStateName { get; set; } = SearchAccessProjectionStates.Maintaining;

    /// <summary>The authorization revision whose event was consumed last (0 before any).</summary>
    public long ConsumedAuthorizationRevision { get; set; }

    /// <summary>Digest of the consumed journal event (the chain link the next event must name); null at revision 0.</summary>
    public byte[]? ConsumedJournalDigest { get; set; }

    /// <summary>Compare-and-set token: incremented by every advance, rebuild and quarantine.</summary>
    public long CheckpointRevision { get; set; }

    /// <summary>Derivation protocol of the rows (minimum-safe-version fencing, design §12.4): a maintainer of a lower version never writes.</summary>
    public int AccessProjectionProtocolVersion { get; set; }

    public string? QuarantineReasonCode { get; set; }

    public DateTimeOffset CheckpointModifiedOn { get; set; }
}

/// <summary>
/// The projection's own copy of the access-relevant relationship edges, built only from the authorization journal (never from
/// business data). Set semantics, as SpiceDB: one row per tuple.
/// </summary>
public sealed class SearchAccessEdge
{
    public Guid SearchAccessEdgeId { get; set; }

    public Guid OrganizationId { get; set; }

    public string ResourceTypeName { get; set; } = string.Empty;

    public Guid ResourceEntityId { get; set; }

    public string RelationshipName { get; set; } = string.Empty;

    public string SubjectTypeName { get; set; } = string.Empty;

    public Guid SubjectEntityId { get; set; }

    public string SubjectRelationName { get; set; } = string.Empty;

    public long MirroredAuthorizationRevision { get; set; }
}

/// <summary>Principal-first record-level access row (annex A §10): effective ownership, or share recipiency through one share.</summary>
public sealed class SearchAccessRecordGrant
{
    public Guid SearchAccessRecordGrantId { get; set; }

    public Guid OrganizationId { get; set; }

    /// <summary>"user" or "team" (teams are principals and are never expanded, D-1).</summary>
    public string AccessPrincipalTypeName { get; set; } = string.Empty;

    public Guid AccessPrincipalEntityId { get; set; }

    public Guid FormRecordId { get; set; }

    /// <summary><see cref="SearchAccessKinds"/>.</summary>
    public string AccessKindName { get; set; } = string.Empty;

    /// <summary>The share of a ShareRecipient row; null for EffectiveOwner.</summary>
    public Guid? RecordShareId { get; set; }

    /// <summary>reader, contributor or manager for ShareRecipient; null for EffectiveOwner.</summary>
    public string? ShareRoleName { get; set; }

    public long DerivedAuthorizationRevision { get; set; }
}

/// <summary>
/// Principal-first field-item read row: one per (principal, item, share path). The distinct-item union across a caller's paths
/// (INV-T2-13) is taken at query time over distinct items, so revoking one path leaves the others.
/// </summary>
public sealed class SearchAccessFieldItemGrant
{
    public Guid SearchAccessFieldItemGrantId { get; set; }

    public Guid OrganizationId { get; set; }

    public string AccessPrincipalTypeName { get; set; } = string.Empty;

    public Guid AccessPrincipalEntityId { get; set; }

    public Guid FormRecordId { get; set; }

    public Guid ManifestItemId { get; set; }

    public Guid RecordShareId { get; set; }

    public string ShareRoleName { get; set; } = string.Empty;

    public long DerivedAuthorizationRevision { get; set; }
}

public static class SearchAccessProjectionStates
{
    /// <summary>Rows and checkpoint are maintained from the journal; ready only at exact revision equality.</summary>
    public const string Maintaining = "Maintaining";

    /// <summary>An integrity or completeness failure: never used, never repaired silently; only an explicit rebuild leaves it.</summary>
    public const string Quarantined = "Quarantined";
}

public static class SearchAccessKinds
{
    public const string EffectiveOwner = "EffectiveOwner";

    public const string ShareRecipient = "ShareRecipient";
}
