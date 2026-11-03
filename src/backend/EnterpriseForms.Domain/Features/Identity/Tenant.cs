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

    /// <summary>Last successful sign-in to this organization through the BFF (C8 M2). Display only; recorded from this column's introduction on.</summary>
    public DateTimeOffset? LastSignedInOn { get; set; }

    /// <summary>Directory profile (non-security): how the person prefers to be addressed. Never an identity or permission attribute.</summary>
    public string? PreferredName { get; set; }

    public string? JobTitle { get; set; }

    /// <summary>The name the identity provider last asserted. <see cref="DisplayName"/> follows it unless <see cref="IsDisplayNameOverridden"/>; sign-in never overwrites an override.</summary>
    public string IdentityDisplayName { get; set; } = string.Empty;

    public bool IsDisplayNameOverridden { get; set; }

    public long ProfileRevision { get; set; } = 1;

    public DateTimeOffset? ProfileModifiedOn { get; set; }

    public Guid? ProfileModifiedByUserId { get; set; }
}

/// <summary>
/// Informational presence of a person (one row per user, bounded): the last authenticated heartbeat and the last user interaction.
/// Multiple tabs and devices share the row (heartbeat = latest, interaction = greatest), so closing one tab never marks the person
/// offline. Display only: it never takes part in authentication, authorization, membership or session decisions.
/// </summary>
public sealed class UserPresence
{
    public Guid UserPresenceId { get; set; }

    public Guid TenantId { get; set; }

    public Guid UserId { get; set; }

    public DateTimeOffset? LastHeartbeatOn { get; set; }

    public DateTimeOffset? LastInteractionOn { get; set; }

    public DateTimeOffset PresenceModifiedOn { get; set; }
}
