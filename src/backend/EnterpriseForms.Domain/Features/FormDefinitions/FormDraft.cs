namespace EnterpriseForms.Domain.Features.FormDefinitions;

/// <summary>Mutable designer draft (CLAUDE.md §2.1): one active draft per form; publishing requires the expected draft revision.</summary>
public sealed class FormDraft
{
    public Guid FormDraftId { get; set; }

    public Guid FormDefinitionId { get; set; }

    public Guid? BasedOnFormVersionId { get; set; }

    public int LayoutSchemaVersion { get; set; } = 2;

    public string LayoutJson { get; set; } = "{}";

    public long DraftRevision { get; set; } = 1;

    public DateTimeOffset CreatedOn { get; set; }

    public Guid CreatedByUserId { get; set; }

    public DateTimeOffset UpdatedOn { get; set; }

    public Guid UpdatedByUserId { get; set; }
}

/// <summary>Stable dropdown option identity (CLAUDE.md §2.2). Deactivation is not deletion; values reference the id.</summary>
public sealed class FieldOption
{
    public Guid FieldOptionId { get; set; }

    public Guid FieldDefinitionId { get; set; }

    public string OptionKey { get; set; } = string.Empty;

    public string OptionDisplayText { get; set; } = string.Empty;

    public int OptionSortOrder { get; set; }

    public bool IsActive { get; set; } = true;

    public DateTimeOffset CreatedOn { get; set; }

    public Guid CreatedByUserId { get; set; }

    public DateTimeOffset ModifiedOn { get; set; }

    public Guid ModifiedByUserId { get; set; }

    /// <summary>The shared option-list item this option was copied from (null for options defined on the field itself). Values reference <see cref="FieldOptionId"/>, never the list item.</summary>
    public Guid? OptionListItemId { get; set; }
}

#pragma warning disable CA1720 // Single/Multiple are the stored selection-mode vocabulary, not type names.
public static class OptionSelectionModes
{
    public const string Single = "Single";
    public const string Multiple = "Multiple";
}
#pragma warning restore CA1720

/// <summary>
/// Organization-scoped reusable option list (Phase C2 priority 1). A list is a template: binding it to a field copies its
/// items into that field's own <see cref="FieldOption"/> rows (stable ids), so published versions and stored values
/// never change when the list is edited; a designer pulls list changes into a field explicitly (sync).
/// </summary>
public sealed class OptionList
{
    public Guid OptionListId { get; set; }

    public Guid TenantId { get; set; }

    public string OptionListName { get; set; } = string.Empty;

    public string OptionListDescription { get; set; } = string.Empty;

    public string LifecycleStatus { get; set; } = "Active";

    /// <summary>Optimistic-concurrency revision of the list and its items.</summary>
    public long OptionListRevision { get; set; } = 1;

    public DateTimeOffset CreatedOn { get; set; }

    public Guid CreatedByUserId { get; set; }

    public DateTimeOffset ModifiedOn { get; set; }

    public Guid ModifiedByUserId { get; set; }
}

/// <summary>An item of a shared option list: immutable key, editable display text/order, deactivated instead of deleted.</summary>
public sealed class OptionListItem
{
    public Guid OptionListItemId { get; set; }

    public Guid OptionListId { get; set; }

    public string OptionKey { get; set; } = string.Empty;

    public string OptionDisplayText { get; set; } = string.Empty;

    public int OptionSortOrder { get; set; }

    public bool IsActive { get; set; } = true;

    public DateTimeOffset CreatedOn { get; set; }

    public Guid CreatedByUserId { get; set; }

    public DateTimeOffset ModifiedOn { get; set; }

    public Guid ModifiedByUserId { get; set; }
}
