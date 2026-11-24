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

    /// <summary>Phase C7 M3: the published layout title as registered (display for security administrators); null for versions registered before M3.</summary>
    public string? FormTitleSnapshot { get; set; }

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

    /// <summary>
    /// C-188: <see cref="FieldSearchApprovalScopes.CurrentVersion"/> (the field is in the latest registered version) or
    /// <see cref="FieldSearchApprovalScopes.RetainedField"/> (an explicit approval to search the latest retained values of a field
    /// that is no longer in the form). A retained approval may become CurrentVersion only when the field returns unchanged and
    /// still eligible; never the other way round.
    /// </summary>
    public string ApprovalScopeName { get; set; } = FieldSearchApprovalScopes.CurrentVersion;

    /// <summary>C-188: for a RetainedField approval, the most recent registered version that placed the field (whose entry the approval binds); null otherwise.</summary>
    public int? SourceFormVersionNumber { get; set; }
}

/// <summary>C-188 (D-H2): the scope of a field search approval.</summary>
public static class FieldSearchApprovalScopes
{
    public const string CurrentVersion = "CurrentVersion";
    public const string RetainedField = "RetainedField";

    public static bool IsKnown(string? scope) => scope is CurrentVersion or RetainedField;
}

/// <summary>
/// C-188 (D-H7, D-H13): the one form a field definition belongs to in this organization's Authority. Written the first time a
/// version registration or a protection command names the field (or by the C-188 migration from existing records); afterwards
/// any command naming the field for another form is refused (FIELD_BOUND_TO_ANOTHER_FORM). Never changed or deleted by the runtime.
/// </summary>
public sealed class FieldFormBinding
{
    public Guid OrganizationId { get; set; }

    public Guid FieldDefinitionId { get; set; }

    public Guid FormDefinitionId { get; set; }

    /// <summary><see cref="FieldFormBindingSources"/>: Backfill (the C-188 migration), VersionRegistration or ProtectionCommand.</summary>
    public string BindingSourceName { get; set; } = FieldFormBindingSources.VersionRegistration;

    /// <summary>The security command that created the binding; null only for <see cref="FieldFormBindingSources.Backfill"/>.</summary>
    public Guid? BoundBySecurityCommandId { get; set; }

    public DateTimeOffset BoundOn { get; set; }
}

public static class FieldFormBindingSources
{
    public const string Backfill = "Backfill";
    public const string VersionRegistration = "VersionRegistration";
    public const string ProtectionCommand = "ProtectionCommand";
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

    /// <summary>Phase C7 M3: fields inside repeating sections are not searchable in V1.</summary>
    public const string RepeatingGroupNotSearchable = "RepeatingGroupNotSearchable";
}

public static class FieldSearchInvalidationReasons
{
    public const string FieldStrengthened = "FIELD_STRENGTHENED";
    public const string FormVersionChanged = "FORM_VERSION_CHANGED";
    public const string OrganizationPolicyChanged = "ORGANIZATION_POLICY_CHANGED";
}
