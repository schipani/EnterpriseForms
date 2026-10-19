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
}
