namespace AuthorizationAuthority.Domain.Features.SearchIndex;

/// <summary>
/// IX-1 (indexed search, Option C): one row per organization whose search index exists. Its revision is kept equal to the
/// organization's content-integrity revision by every transaction that advances that revision (INV-R, design §18.2), or the
/// state is ReconciliationRequired. Holds no customer values.
/// </summary>
public sealed class SearchProjectionState
{
    public Guid SearchProjectionStateId { get; set; }

    public Guid OrganizationId { get; set; }

    public int ProjectionSchemaVersion { get; set; } = 1;

    /// <summary><see cref="SearchProjectionStatuses"/>: Active, ReconciliationRequired or Quarantined.</summary>
    public string ProjectionStatusName { get; set; } = SearchProjectionStatuses.Active;

    public string? ProjectionStatusReasonCode { get; set; }

    public long ProjectionContentIntegrityRevision { get; set; }

    /// <summary>ReconciliationRequired only: the last organization content revision the index was synchronized with.</summary>
    public long? ReconciliationFromRevision { get; set; }

    /// <summary>§17.10 D3: an overdue verification scope suspends serving; independent of <see cref="ProjectionStatusName"/>.</summary>
    public bool VerificationSuspended { get; set; }

    /// <summary>Comma-separated overdue scope kinds (for example "S-A,S-B") while suspended; null otherwise.</summary>
    public string? SuspendedScopeNames { get; set; }

    /// <summary>§17.10 D1: written only when every S-A check passed for the whole organization.</summary>
    public DateTimeOffset? LastSuccessfulCheapPassOn { get; set; }

    /// <summary>§17.10.5 K9: the highest evaluation time observed (monotonic); a backward clock step never makes a scope current.</summary>
    public DateTimeOffset? LastEvaluationOn { get; set; }

    public DateTimeOffset ProjectionCreatedOn { get; set; }

    public DateTimeOffset ProjectionModifiedOn { get; set; }
}

public static class SearchProjectionStatuses
{
    public const string Active = "Active";
    public const string ReconciliationRequired = "ReconciliationRequired";
    public const string Quarantined = "Quarantined";
}

/// <summary>IX-1: one row per searchability approval the index derives postings for. Created Building; Complete is set only by Authority attestation (IX-2).</summary>
public sealed class SearchFieldCoverage
{
    public Guid SearchFieldCoverageId { get; set; }

    public Guid OrganizationId { get; set; }

    public Guid FormDefinitionId { get; set; }

    public Guid FieldDefinitionId { get; set; }

    public Guid FieldSearchApprovalId { get; set; }

    public string IndexedValueTypeName { get; set; } = string.Empty;

    /// <summary><see cref="SearchCoverageStates"/>.</summary>
    public string CoverageStateName { get; set; } = SearchCoverageStates.Building;

    /// <summary>Public or Internal only (database CHECK).</summary>
    public string CoveredFieldClassification { get; set; } = string.Empty;

    public DateTimeOffset CoverageStartedOn { get; set; }

    public DateTimeOffset? CoverageCompletedOn { get; set; }

    public DateTimeOffset? CoverageWithdrawnOn { get; set; }

    public string? CoverageWithdrawalReasonCode { get; set; }
}

public static class SearchCoverageStates
{
    public const string Building = "Building";
    public const string Complete = "Complete";
    public const string Withdrawn = "Withdrawn";
}

/// <summary>
/// IX-1: one row per record of a form with at least one non-withdrawn coverage, derived at the content commit that accepted
/// the record's current head. The ES256 seal binds the entry, its postings and anomalies (DynamicForms/SearchEntrySeal/v1).
/// </summary>
public sealed class SearchRecordEntry
{
    public Guid SearchRecordEntryId { get; set; }

    public Guid OrganizationId { get; set; }

    public Guid FormDefinitionId { get; set; }

    public Guid FormRecordId { get; set; }

    public Guid IndexedOperationId { get; set; }

    public long IndexedContentRevision { get; set; }

    public byte[] IndexedManifestRoot { get; set; } = [];

    /// <summary>The record status decoded from the verified header leaf.</summary>
    public string RecordStatusName { get; set; } = string.Empty;

    /// <summary>The searchability approvals (coverages) the entry was derived under; the seal binds their approval-time bindings.</summary>
    public Guid[] DerivedFieldSearchApprovalIds { get; set; } = [];

    public int IndexedPostingCount { get; set; }

    public int IndexedAnomalyCount { get; set; }

    public int SealSchemaVersion { get; set; } = 1;

    public string SealKeyName { get; set; } = string.Empty;

    public int SealKeyVersion { get; set; }

    /// <summary>64-byte ES256 signature (P1363 r || s).</summary>
    public byte[] EntrySealSignature { get; set; } = [];

    public DateTimeOffset EntryWrittenOn { get; set; }

    /// <summary>§17.10 S-B scope timestamp; written at a successful seal verification, or at sealing (K7).</summary>
    public DateTimeOffset LastSealVerifiedOn { get; set; }

    /// <summary>§17.10 S-C scope timestamp; written when postings were derived from verified bytes (commit, or IX-2 deep sweep).</summary>
    public DateTimeOffset LastDeepVerifiedOn { get; set; }

    /// <summary>§17.10 R3: observability only; never counts as completion.</summary>
    public DateTimeOffset? LastSealAttemptedOn { get; set; }

    public string? LastSealAttemptOutcomeCode { get; set; }
}

/// <summary>Columns shared by the typed posting tables. A posting is one indexed value of one manifest item.</summary>
public abstract class SearchPostingBase
{
    public Guid OrganizationId { get; set; }

    public Guid FormDefinitionId { get; set; }

    public Guid FieldDefinitionId { get; set; }

    public Guid FormRecordId { get; set; }

    public Guid ManifestItemId { get; set; }

    /// <summary>17-byte Merkle item key of the leaf the value was derived from.</summary>
    public byte[] ManifestItemKey { get; set; } = [];

    public string SourceEntityKind { get; set; } = string.Empty;

    public Guid IndexedOperationId { get; set; }

    public Guid FieldSearchApprovalId { get; set; }

    /// <summary>The exact verified canonical bytes of the leaf (design §4 item 1): SHA-256 of these equals the accepted leaf digest.</summary>
    public byte[] SourceCanonicalBytes { get; set; } = [];
}

public sealed class SearchStringPosting : SearchPostingBase
{
    public Guid SearchStringPostingId { get; set; }

    /// <summary><c>SearchValueNormalizationV1</c>; only the normalized form is stored as a column (minimization).</summary>
    public string NormalizedStringValue { get; set; } = string.Empty;
}

public sealed class SearchIntegerPosting : SearchPostingBase
{
    public Guid SearchIntegerPostingId { get; set; }

    public long IntegerValue { get; set; }
}

public sealed class SearchDecimalPosting : SearchPostingBase
{
    public Guid SearchDecimalPostingId { get; set; }

    public decimal DecimalValue { get; set; }
}

public sealed class SearchDatePosting : SearchPostingBase
{
    public Guid SearchDatePostingId { get; set; }

    public DateOnly DateValue { get; set; }
}

public sealed class SearchDateTimePosting : SearchPostingBase
{
    public Guid SearchDateTimePostingId { get; set; }

    public DateTimeOffset DateTimeValue { get; set; }
}

public sealed class SearchBooleanPosting : SearchPostingBase
{
    public Guid SearchBooleanPostingId { get; set; }

    public bool BooleanValue { get; set; }
}

public sealed class SearchOptionPosting : SearchPostingBase
{
    public Guid SearchOptionPostingId { get; set; }

    public Guid FieldOptionId { get; set; }
}

/// <summary>An approved field holding something that must never be indexed on this record (EncryptedValuePresent). Never a value.</summary>
public sealed class SearchRecordFieldAnomaly
{
    public Guid SearchRecordFieldAnomalyId { get; set; }

    public Guid OrganizationId { get; set; }

    public Guid FormDefinitionId { get; set; }

    public Guid FormRecordId { get; set; }

    public Guid FieldDefinitionId { get; set; }

    public Guid IndexedOperationId { get; set; }

    public string AnomalyKindName { get; set; } = string.Empty;
}

public static class SearchAnomalyKinds
{
    public const string EncryptedValuePresent = "EncryptedValuePresent";
}

/// <summary>One row per (organization, form): incremented on every index change of a record of that form (paging validation, IX-3).</summary>
public sealed class SearchFormState
{
    public Guid SearchFormStateId { get; set; }

    public Guid OrganizationId { get; set; }

    public Guid FormDefinitionId { get; set; }

    public long FormContentRevision { get; set; }

    public DateTimeOffset FormStateModifiedOn { get; set; }
}
