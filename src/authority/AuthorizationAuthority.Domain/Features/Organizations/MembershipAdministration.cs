namespace AuthorizationAuthority.Domain.Features.Organizations;

/// <summary>
/// AC-7 (C8 M2): the first half of a two-owner change. Owner A proposes; a different owner B executes the change with this
/// proposal's id; the Authority re-checks every precondition at execution. Single use, expires after 24 hours. Its id is
/// the id of the security command that created it.
/// </summary>
public sealed class OrganizationRoleChangeProposal
{
    public Guid OrganizationRoleChangeProposalId { get; set; }

    public Guid OrganizationId { get; set; }

    public Guid TargetAuthorityUserId { get; set; }

    /// <summary>One of <see cref="OwnerChangeKinds"/>.</summary>
    public string ProposedChangeName { get; set; } = string.Empty;

    public Guid ProposedByAuthorityUserId { get; set; }

    public string ProposalReasonText { get; set; } = string.Empty;

    public DateTimeOffset ProposalCreatedOn { get; set; }

    public DateTimeOffset ProposalExpiresOn { get; set; }

    /// <summary>Pending or Consumed (expiry is a time comparison, never a stored transition).</summary>
    public string ProposalStateName { get; set; } = ProposalStates.Pending;

    public Guid? ConsumedBySecurityCommandId { get; set; }

    public DateTimeOffset? ProposalConsumedOn { get; set; }
}

public static class OwnerChangeKinds
{
    public const string GrantOwner = "GrantOwner";
    public const string RevokeOwner = "RevokeOwner";
    public const string DisableOwner = "DisableOwner";
    public const string EnableOwner = "EnableOwner";

    public static readonly IReadOnlyList<string> All = [GrantOwner, RevokeOwner, DisableOwner, EnableOwner];
}

public static class ProposalStates
{
    public const string Pending = "Pending";
    public const string Consumed = "Consumed";
}

/// <summary>
/// AC-7.4 (C8 M2): an owner's request to add a person to the organization. It grants nothing. Only the operator plane, with
/// two independent approvals, turns it into an identity binding (Development/Testing until production PIM exists). The
/// Authority user id is allocated at request time so the approvals bind to it. Expires after 7 days.
/// </summary>
public sealed class OrganizationMembershipRequest
{
    public Guid OrganizationMembershipRequestId { get; set; }

    public Guid OrganizationId { get; set; }

    public Guid RequestedAuthorityUserId { get; set; }

    public string RequestedIssuerUri { get; set; } = string.Empty;

    public string RequestedSubjectIdentifier { get; set; } = string.Empty;

    /// <summary>Display only, never a key.</summary>
    public string RequestedDisplayNameHint { get; set; } = string.Empty;

    /// <summary>Null or <c>designer</c>; never <c>organization_owner</c> or <c>system_administrator</c>.</summary>
    public string? RequestedInitialRoleName { get; set; }

    public Guid RequestedByAuthorityUserId { get; set; }

    public string RequestReasonText { get; set; } = string.Empty;

    public DateTimeOffset RequestCreatedOn { get; set; }

    public DateTimeOffset RequestExpiresOn { get; set; }

    /// <summary>Pending, Approved, Rejected, Expired (recorded when an approval finds the request too old), Cancelled or Replaced (an owner's cancel or edit).</summary>
    public string RequestStateName { get; set; } = MembershipRequestStates.Pending;

    public DateTimeOffset? RequestDecidedOn { get; set; }

    public string? RequestDecidedByOperatorName { get; set; }

    public string? RequestDecisionReasonText { get; set; }

    public Guid? DecidedBySecurityCommandId { get; set; }

    /// <summary>C8 M2 Stage 2: the Platform Operator who completed the approval (second approval) or rejected the request; null for legacy operator-plane decisions.</summary>
    public Guid? RequestDecidedByPlatformOperatorId { get; set; }

    /// <summary>AC-7.5: when, by which owner and why an owner cancelled or replaced this request (set exactly for Cancelled and Replaced).</summary>
    public DateTimeOffset? RequestCancelledOn { get; set; }

    public Guid? RequestCancelledByAuthorityUserId { get; set; }

    public string? RequestCancellationReasonText { get; set; }

    /// <summary>AC-7.5 lineage: the request this one replaced. The replaced original's "replaced by" is derived (the request whose replaces id is the original's id), so the link can never disagree with itself.</summary>
    public Guid? ReplacesMembershipRequestId { get; set; }
}

public static class MembershipRequestStates
{
    public const string Pending = "Pending";
    public const string Approved = "Approved";
    public const string Rejected = "Rejected";
    public const string Expired = "Expired";
    public const string Cancelled = "Cancelled";
    public const string Replaced = "Replaced";
}
