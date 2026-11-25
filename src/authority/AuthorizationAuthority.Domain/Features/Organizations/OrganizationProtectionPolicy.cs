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

    /// <summary>Phase C7 M2 (AC-6 §4.2): plaintext value search may be approved at all. Default false; turning it off invalidates every approval.</summary>
    public bool PlaintextSearchPermitted { get; set; }

    /// <summary>Advances whenever field search policy (organization flag or any approval) changes.</summary>
    public long SearchPolicyRevision { get; set; }

    /// <summary>
    /// C10 M1-WP1: maximum delegation depth for shares in this organization. Platform maximum is 3; organizations may
    /// configure 1 or 2 but never more. Depth 1 = owner-granted. A Manager at depth 3 is refused. Default 3.
    /// </summary>
    public int MaximumDelegationDepth { get; set; } = 3;

    public Guid UpdatedBySecurityCommandId { get; set; }

    public DateTimeOffset PolicyUpdatedOn { get; set; }
}
