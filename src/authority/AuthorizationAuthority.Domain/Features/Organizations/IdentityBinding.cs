namespace AuthorizationAuthority.Domain.Features.Organizations;

/// <summary>Independent (issuer, subject) → authority user binding. Never derived from business-database user rows.</summary>
public sealed class IdentityBinding
{
    public Guid IdentityBindingId { get; set; }

    public Guid OrganizationId { get; set; }

    public Guid AuthorityUserId { get; set; }

    public string IdentityIssuerUri { get; set; } = string.Empty;

    public string IdentitySubjectIdentifier { get; set; } = string.Empty;

    public bool IdentityBindingEnabled { get; set; } = true;

    public DateTimeOffset IdentityCreatedOn { get; set; }
}
