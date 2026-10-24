namespace AuthorizationAuthority.Domain.Features.FieldSearch;

/// <summary>
/// OD-3: the Authority's immutable record of one published form version. <see cref="FieldManifestJson"/> holds the
/// canonical field entries (id, type, classification, protection mode, label, options) the designer published; the
/// layout digest binds the layout bytes. Written only by the RegisterPublishedFormVersion command; never backfilled.
/// </summary>
public sealed class PublishedFormVersion
{
    public Guid PublishedFormVersionId { get; set; }

    public Guid OrganizationId { get; set; }

    public Guid FormDefinitionId { get; set; }

    public int FormVersionNumber { get; set; }

    /// <summary>SHA-256 of the canonical layout JSON (32 bytes).</summary>
    public byte[] LayoutDigest { get; set; } = [];

    public string LayoutDigestScheme { get; set; } = FieldSearchVocabulary.LayoutDigestScheme;

    public string FieldManifestJson { get; set; } = "[]";

    public Guid RegisteredBySecurityCommandId { get; set; }

    public DateTimeOffset RegisteredOn { get; set; }
}

/// <summary>
/// AC-6: one field's search approval, bound to the registered version it was approved against and to that version's
/// recorded type, classification and protection mode, with the label and options frozen at approval. Mutable only by
/// security commands (approve, withdraw, invalidate); every change advances the authorization revision.
/// </summary>
public sealed class FieldSearchApproval
{
    public Guid FieldSearchApprovalId { get; set; }

    public Guid OrganizationId { get; set; }

    public Guid FormDefinitionId { get; set; }

    public Guid FieldDefinitionId { get; set; }

    public int ApprovedFormVersionNumber { get; set; }

    public byte[] ApprovedLayoutDigest { get; set; } = [];

    public string ApprovedFieldDataType { get; set; } = string.Empty;

    public string ApprovedFieldClassification { get; set; } = string.Empty;

    public string ApprovedProtectionMode { get; set; } = string.Empty;

    public string ApprovedOperatorsJson { get; set; } = "[]";

    public string ApprovedFieldLabelSnapshot { get; set; } = string.Empty;

    public string ApprovedOptionSnapshotJson { get; set; } = "[]";

    public string ApprovalReferenceText { get; set; } = string.Empty;

    public string ApprovalStateName { get; set; } = FieldSearchApprovalStates.Active;

    public string? InvalidationReasonCode { get; set; }

    public Guid ApprovedBySecurityCommandId { get; set; }

    public Guid? ChangedBySecurityCommandId { get; set; }

    public DateTimeOffset ApprovedOn { get; set; }

    public DateTimeOffset? StateChangedOn { get; set; }
}

public static class FieldSearchApprovalStates
{
    public const string Active = "Active";
    public const string Withdrawn = "Withdrawn";
    public const string Invalidated = "Invalidated";
}

public static class FieldSearchVocabulary
{
    public const string LayoutDigestScheme = "layout-digest/v1";

    /// <summary>The M0 operator vocabulary per storage type (docs/architecture/PHASE_C7_ADVANCED_FIELD_SEARCH.md §6.4).</summary>
    public static readonly IReadOnlyDictionary<string, IReadOnlyList<string>> OperatorsByType = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
    {
        ["String"] = ["equals", "startsWith", "contains"],
        ["Integer"] = ["equals", "greaterThan", "greaterThanOrEqual", "lessThan", "lessThanOrEqual", "between"],
        ["Decimal"] = ["equals", "greaterThan", "greaterThanOrEqual", "lessThan", "lessThanOrEqual", "between"],
        ["Date"] = ["equals", "before", "after", "between"],
        ["DateTime"] = ["before", "after", "between"],
        ["Boolean"] = ["isTrue", "isFalse"],
        ["Option"] = ["is", "includes"],
    };

    public static readonly IReadOnlySet<string> DataTypes = new HashSet<string>(["String", "Integer", "Decimal", "Date", "DateTime", "Boolean", "Option", "Guid", "File"], StringComparer.Ordinal);

    public static readonly IReadOnlySet<string> Classifications = new HashSet<string>(["Public", "Internal", "Confidential", "Restricted"], StringComparer.Ordinal);

    public static readonly IReadOnlySet<string> ProtectionModes = new HashSet<string>(["Standard", "ApplicationEncrypted"], StringComparer.Ordinal);
}

/// <summary>Why a field cannot be approved (configuration view; the rejection reason of an approval command).</summary>
public static class FieldSearchEligibility
{
    public const string Eligible = "Eligible";
    public const string Encrypted = "Encrypted";
    public const string ClassificationNotPermitted = "ClassificationNotPermitted";
    public const string OrganizationSearchDisabled = "OrganizationSearchDisabled";
    public const string OrganizationPolicyMissing = "OrganizationPolicyMissing";
    public const string ProtectionPending = "ProtectionPending";
    public const string TypeNotSearchable = "TypeNotSearchable";
}

public static class FieldSearchInvalidationReasons
{
    public const string FieldStrengthened = "FIELD_STRENGTHENED";
    public const string FormVersionChanged = "FORM_VERSION_CHANGED";
    public const string OrganizationPolicyChanged = "ORGANIZATION_POLICY_CHANGED";
}
