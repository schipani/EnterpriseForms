namespace EnterpriseForms.Domain.Features.Identity;

/// <summary>Canonical tenant-isolation entity (CLAUDE.md §2.1). An organization is a tenant; the id equals the authority organization id.</summary>
public sealed class Tenant
{
    public Guid TenantId { get; set; }

    public string TenantName { get; set; } = string.Empty;

    public string TenantLifecycleStatus { get; set; } = "Active";

    public DateTimeOffset CreatedOn { get; set; }

    public DateTimeOffset ModifiedOn { get; set; }
}

/// <summary>
/// Local user projection (display/reporting only). Authorization identity is the authority's (issuer, subject)
/// binding; this row never confers permission (TRUST_AUTHORITY_ARCHITECTURE.md §2) and is what SEC-P tests forge.
/// </summary>
public sealed class User
{
    public Guid UserId { get; set; }

    public Guid TenantId { get; set; }

    public string ExternalIssuerUri { get; set; } = string.Empty;

    public string ExternalSubjectIdentifier { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;

    public string EmailAddress { get; set; } = string.Empty;

    public bool IsActive { get; set; } = true;

    public DateTimeOffset CreatedOn { get; set; }

    public DateTimeOffset ModifiedOn { get; set; }
}
