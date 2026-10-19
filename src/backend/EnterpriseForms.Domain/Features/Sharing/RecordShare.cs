namespace EnterpriseForms.Domain.Features.Sharing;

/// <summary>Local projection of an authority share command (CLAUDE.md §4H). Never a permission source; SEC-P02 forges these rows.</summary>
public sealed class RecordShare
{
    public Guid RecordShareId { get; set; }

    public Guid TenantId { get; set; }

    public Guid FormRecordId { get; set; }

    public Guid SharedByUserId { get; set; }

    public Guid? RecipientUserId { get; set; }

    public Guid? RecipientTeamId { get; set; }

    public string RecipientRole { get; set; } = "Reader";

    public string ShareScope { get; set; } = "SelectedItems";

    public string ShareStatus { get; set; } = "Active";

    public long ShareRevision { get; set; } = 1;

    public Guid? AuthorityCommandId { get; set; }

    public DateTimeOffset ShareCreatedOn { get; set; }

    public DateTimeOffset? ShareRevokedOn { get; set; }

    public Guid? ShareRevokedByUserId { get; set; }
}

public sealed class RecordShareField
{
    public Guid RecordShareFieldId { get; set; }

    public Guid TenantId { get; set; }

    public Guid RecordShareId { get; set; }

    public Guid FieldDefinitionId { get; set; }

    public Guid FieldValueItemId { get; set; }

    public bool CanViewField { get; set; } = true;

    public bool CanEditField { get; set; }
}
