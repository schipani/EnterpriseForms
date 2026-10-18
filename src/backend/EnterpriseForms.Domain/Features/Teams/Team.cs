namespace EnterpriseForms.Domain.Features.Teams;

/// <summary>Team directory projection. Membership that confers access lives only in the Authorization Authority; these rows are untrusted.</summary>
public sealed class Team
{
    public Guid TeamId { get; set; }

    public Guid TenantId { get; set; }

    public string TeamName { get; set; } = string.Empty;

    public bool IsActive { get; set; } = true;

    public DateTimeOffset CreatedOn { get; set; }

    public Guid CreatedByUserId { get; set; }

    public DateTimeOffset ModifiedOn { get; set; }
}

public sealed class TeamMembership
{
    public Guid TeamMembershipId { get; set; }

    public Guid TenantId { get; set; }

    public Guid TeamId { get; set; }

    public Guid UserId { get; set; }

    public string MembershipRole { get; set; } = "Member";

    /// <summary>Authority security command that made this membership effective; a forged row has none that the authority recognizes.</summary>
    public Guid? AuthorityCommandId { get; set; }

    public DateTimeOffset CreatedOn { get; set; }

    public Guid CreatedByUserId { get; set; }

    public DateTimeOffset? RemovedOn { get; set; }
}
