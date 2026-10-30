namespace AuthorizationAuthority.Domain.Features.Catalogue;

/// <summary>
/// AC-5 (Phase C7 M1): an Authority-issued catalogue of the registered records of one form, fixed at one authorization,
/// projection and content-integrity revision, together with the exact accepted head of every entry. The id is only a
/// reference: a snapshot is valid only when current Authority state still matches it (see the catalogue service); holding
/// the id proves nothing. Rows hold no record contents.
/// </summary>
public sealed class SearchCatalogueSnapshot
{
    public Guid SearchCatalogueSnapshotId { get; set; }

    public Guid OrganizationId { get; set; }

    /// <summary><see cref="CatalogueScopeKinds"/>: a user's discoverable records, or the verifier's complete population.</summary>
    public string SnapshotScopeKind { get; set; } = CatalogueScopeKinds.UserSearch;

    /// <summary>UserSearch: the Authority user the delegated assertion was bound to. Null for MigrationVerification.</summary>
    public Guid? RequestingAuthorityUserId { get; set; }

    /// <summary>MigrationVerification: the verifier workload name from its credential. Null for UserSearch.</summary>
    public string? RequestingWorkloadName { get; set; }

    /// <summary>MigrationVerification: the accepted protection command that scopes the request.</summary>
    public Guid? ScopingSecurityCommandId { get; set; }

    public Guid FormDefinitionId { get; set; }

    public long SnapshotAuthorizationRevision { get; set; }

    public long SnapshotProjectionRevision { get; set; }

    public long SnapshotContentIntegrityRevision { get; set; }

    public int CatalogueEntryCount { get; set; }

    /// <summary>SHA-256 of the canonical, sorted entry list (evidence in the audit journal; never used to validate).</summary>
    public byte[] CatalogueDigest { get; set; } = [];

    public DateTimeOffset SnapshotIssuedOn { get; set; }

    public DateTimeOffset SnapshotExpiresOn { get; set; }

    public DateTimeOffset? SnapshotInvalidatedOn { get; set; }

    public string? SnapshotInvalidationReason { get; set; }
}

/// <summary>One record of a snapshot with the accepted head it was fixed at (revision 0 = registered without content yet).</summary>
public sealed class SearchCatalogueSnapshotEntry
{
    public Guid SearchCatalogueSnapshotEntryId { get; set; }

    public Guid SearchCatalogueSnapshotId { get; set; }

    public Guid OrganizationId { get; set; }

    public Guid FormRecordId { get; set; }

    /// <summary>Position in the sorted catalogue (form_record_id order), for deterministic paging.</summary>
    public int CatalogueEntryPosition { get; set; }

    public long CommittedContentRevision { get; set; }

    public Guid? CommittedOperationId { get; set; }

    public bool ContentHeadQuarantined { get; set; }

    /// <summary>Registry lifecycle at issue (Active, or Quarantined — a quarantined resource stays in scope and fails verification).</summary>
    public string ResourceLifecycleState { get; set; } = string.Empty;
}

public static class CatalogueScopeKinds
{
    public const string UserSearch = "UserSearch";
    public const string MigrationVerification = "MigrationVerification";
}

/// <summary>Why a snapshot is no longer valid (returned to the trusted caller only; never record contents).</summary>
public static class CatalogueInvalidationReasons
{
    public const string NotFound = "SNAPSHOT_NOT_FOUND";
    public const string Expired = "SNAPSHOT_EXPIRED";
    public const string AuthorizationChanged = "AUTHORIZATION_REVISION_CHANGED";
    public const string ProjectionChanged = "PROJECTION_REVISION_CHANGED";
    public const string ContentChanged = "CONTENT_HEAD_CHANGED";
    public const string RecordUnavailable = "RECORD_UNAVAILABLE";
    public const string GateClosed = "ORGANIZATION_GATE_CLOSED";
    public const string CommandSuperseded = "SCOPING_COMMAND_SUPERSEDED";
}
