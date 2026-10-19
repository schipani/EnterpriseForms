namespace EnterpriseForms.Domain.Features.FormDefinitions;

/// <summary>Form template (CLAUDE.md §2.1). Stage A registers a minimal published version; the designer workflow is Phase 2.</summary>
public sealed class FormDefinition
{
    public Guid FormDefinitionId { get; set; }

    public Guid TenantId { get; set; }

    public string FormDefinitionName { get; set; } = string.Empty;

    public string FormDefinitionDescription { get; set; } = string.Empty;

    public Guid? CurrentFormVersionId { get; set; }

    public string VersionBehavior { get; set; } = "Latest";

    public string LifecycleStatus { get; set; } = "Active";

    public DateTimeOffset CreatedOn { get; set; }

    public Guid CreatedByUserId { get; set; }

    public DateTimeOffset ModifiedOn { get; set; }

    public Guid ModifiedByUserId { get; set; }

    /// <summary>Authority command that registered the form; without it the form is not a registered resource.</summary>
    public Guid? AuthorityCommandId { get; set; }
}

/// <summary>Immutable published snapshot. Stage A layouts are schema v1 text fields only.</summary>
public sealed class FormVersion
{
    public Guid FormVersionId { get; set; }

    public Guid FormDefinitionId { get; set; }

    public int VersionNumber { get; set; }

    public int LayoutSchemaVersion { get; set; } = 1;

    public string LayoutJson { get; set; } = "{}";

    public DateTimeOffset CreatedOn { get; set; }

    public Guid CreatedByUserId { get; set; }

    public string ChangeReason { get; set; } = string.Empty;

    public Guid? RestoredFromFormVersionId { get; set; }

    public DateTimeOffset PublishedOn { get; set; }

    public Guid PublishedByUserId { get; set; }
}

/// <summary>Stable field identity; storage type immutable after creation. Stage A supports String fields.</summary>
public static class FieldClassifications
{
    public const string Public = "Public";
    public const string Internal = "Internal";
    public const string Confidential = "Confidential";
    public const string Restricted = "Restricted";
}

public static class FieldProtectionModes
{
    public const string Standard = "Standard";
    public const string ApplicationEncrypted = "ApplicationEncrypted";
}

public sealed class FieldDefinition
{
    public Guid FieldDefinitionId { get; set; }

    public Guid FormDefinitionId { get; set; }

    public string FieldKey { get; set; } = string.Empty;

    public string FieldDisplayName { get; set; } = string.Empty;

    public string FieldDescription { get; set; } = string.Empty;

    public string FieldDataType { get; set; } = "String";

    public string FieldClassification { get; set; } = "Internal";

    public bool IsSearchable { get; set; }

    public string LifecycleStatus { get; set; } = "Available";

    public DateTimeOffset CreatedOn { get; set; }

    public Guid CreatedByUserId { get; set; }

    /// <summary>Storage protection (ADR-PB-004): Standard (typed plaintext, readable by a database administrator) or ApplicationEncrypted (AES-256-GCM envelope per value). Strengthened only, never weakened.</summary>
    public string FieldProtectionMode { get; set; } = FieldProtectionModes.Standard;

    /// <summary>Increments on every classification/protection strengthening (audited migration step).</summary>
    public long ProtectionPolicyRevision { get; set; } = 1;

    public DateTimeOffset? ProtectionChangedOn { get; set; }

    public Guid? ProtectionChangedByUserId { get; set; }

    /// <summary>Optimistic-concurrency revision of the editable catalog metadata (display name, description). Key, type and id never change.</summary>
    public long FieldDefinitionRevision { get; set; } = 1;

    public DateTimeOffset? ModifiedOn { get; set; }

    public Guid? ModifiedByUserId { get; set; }
}
