namespace EnterpriseForms.Domain.Features.Organizations;

/// <summary>
/// Organization data-protection policy (ADR-PB-004; SECURITY_SIMPLIFICATION Decision 2): the minimum classification any
/// field may carry and whether Public/Internal fields may be stored as plaintext at all. Business-side projection of an
/// organization decision: it informs what designers may configure; it never grants access to any value. Until the
/// organization owner reviews it, the row carries the documented development default and says so.
/// </summary>
public sealed class OrganizationSecurityPolicy
{
    public Guid OrganizationSecurityPolicyId { get; set; }

    public Guid TenantId { get; set; }

    /// <summary>Public, Internal, Confidential or Restricted; designers cannot classify below it.</summary>
    public string MinimumFieldClassification { get; set; } = "Internal";

    /// <summary>When false, every field (whatever its classification) must be application-encrypted.</summary>
    public bool PlaintextInternalFieldsPermitted { get; set; } = true;

    public string PolicyStatementText { get; set; } = string.Empty;

    /// <summary>Null until an organization owner has explicitly reviewed and saved the policy.</summary>
    public DateTimeOffset? PolicyReviewedOn { get; set; }

    public Guid? PolicyReviewedByUserId { get; set; }

    public long SecurityPolicyRevision { get; set; } = 1;

    public DateTimeOffset PolicyModifiedOn { get; set; }

    public Guid? PolicyModifiedByUserId { get; set; }
}
