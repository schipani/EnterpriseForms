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

    /// <summary>AC-7.2: set while the membership is disabled (identity_binding_enabled = false); cleared on re-enable.</summary>
    public DateTimeOffset? IdentityDisabledOn { get; set; }

    public Guid? IdentityDisabledBySecurityCommandId { get; set; }

    public Guid? IdentityEnabledBySecurityCommandId { get; set; }

    /// <summary>AC-7.4: the operator-approved membership request that created this binding (null for bootstrap bindings).</summary>
    public Guid? OrganizationMembershipRequestId { get; set; }
}
