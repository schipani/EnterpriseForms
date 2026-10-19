namespace EnterpriseForms.Domain.Features.RelatedForms;

/// <summary>Stable, reusable parent-form → child-form list identity (form_model/RELATED_FORM_COLLECTIONS.md §2).</summary>
public sealed class FormRelationshipDefinition
{
    public Guid FormRelationshipDefinitionId { get; set; }

    public Guid TenantId { get; set; }

    public Guid ParentFormDefinitionId { get; set; }

    public Guid ChildFormDefinitionId { get; set; }

    public string RelationshipDefinitionKey { get; set; } = string.Empty;

    public string RelationshipDisplayName { get; set; } = string.Empty;

    public int MinimumChildCount { get; set; }

    public int MaximumChildCount { get; set; } = 250;

    public string LifecycleStatus { get; set; } = "Active";

    public long DefinitionRevision { get; set; } = 1;

    public DateTimeOffset CreatedOn { get; set; }

    public Guid CreatedByUserId { get; set; }

    public DateTimeOffset ModifiedOn { get; set; }

    public Guid ModifiedByUserId { get; set; }
}

/// <summary>
/// Local projection of one accepted parent-child link. The trusted link is the authority parent edge plus the child's
/// signed header (schema v3, B-04); this row is never consulted for authorization and SEC/REL tests forge it.
/// </summary>
public sealed class FormRecordRelationship
{
    public Guid FormRecordRelationshipId { get; set; }

    public Guid TenantId { get; set; }

    public Guid FormRelationshipDefinitionId { get; set; }

    public Guid ParentFormRecordId { get; set; }

    public Guid ChildFormRecordId { get; set; }

    public long ListSortOrder { get; set; }

    public string LinkLifecycleState { get; set; } = "Active";

    public Guid? AuthorityCommandId { get; set; }

    public DateTimeOffset LinkCreatedOn { get; set; }

    public Guid LinkCreatedByUserId { get; set; }

    public DateTimeOffset? LinkRemovedOn { get; set; }

    public Guid? LinkRemovedByUserId { get; set; }
}
