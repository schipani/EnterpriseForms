namespace AuthorizationAuthority.Domain.Features.Organizations;

/// <summary>
/// The organization's data-protection policy as the authority holds it (Amendment A1): written only by the
/// UpdateMandatoryPolicy command of an organization owner or system administrator, read by the planners that decide
/// whether a field may be reclassified or released from encryption. The business row is a projection of this.
/// </summary>
public sealed class OrganizationProtectionPolicy
{
    public Guid OrganizationProtectionPolicyId { get; set; }

    public Guid OrganizationId { get; set; }

    public string MinimumFieldClassification { get; set; } = "Internal";

    public bool PlaintextInternalFieldsPermitted { get; set; }

    /// <summary>Explicit permission for encrypted → plaintext migrations of Public/Internal fields (never implied by plaintext permission alone).</summary>
    public bool PlaintextDowngradePermitted { get; set; }

    public long PolicyRevision { get; set; } = 1;

    public Guid UpdatedBySecurityCommandId { get; set; }

    public DateTimeOffset PolicyUpdatedOn { get; set; }
}
