using AuthorizationAuthority.Application.Common.Ports;
using AuthorizationAuthority.Domain.Features.PlatformOperators;

namespace AuthorizationAuthority.Application.Features.PlatformOperators;

/// <summary>
/// Closing pending operator approvals (D8-36…D8-38). Every path that moves a membership request out of Pending — owner cancel or
/// replace, recorded expiry, rejection, an approval on the legacy operator plane — closes the request's pending approval in the
/// same transaction, so an approval never outlives the exact request it was given for. Completion also re-checks state, so this
/// is defence in depth, not the only barrier.
/// </summary>
public static class PlatformApprovalLedger
{
    public static async Task<List<PlatformOperatorApproval>> PendingForRequestAsync(IAuthorityScope scope, Guid organizationMembershipRequestId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scope);
        return (await scope.GetPlatformApprovalsForRequestAsync(organizationMembershipRequestId, cancellationToken).ConfigureAwait(false))
            .Where(a => a.ApprovalStateName == PlatformApprovalStates.Pending).ToList();
    }

    public static void Close(PlatformOperatorApproval approval, string state, string reason, DateTimeOffset now, Guid? consumedBySecurityCommandId = null)
    {
        ArgumentNullException.ThrowIfNull(approval);
        approval.ApprovalStateName = state;
        approval.ApprovalClosureReasonName = reason;
        approval.ApprovalClosedOn = now;
        approval.ConsumedBySecurityCommandId = consumedBySecurityCommandId;
    }

    /// <summary>Closes (Invalidated) every pending approval of the request and returns them.</summary>
    public static async Task<List<PlatformOperatorApproval>> InvalidatePendingAsync(IAuthorityScope scope, Guid organizationMembershipRequestId, string reason, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var pending = await PendingForRequestAsync(scope, organizationMembershipRequestId, cancellationToken).ConfigureAwait(false);
        foreach (var approval in pending)
        {
            Close(approval, PlatformApprovalStates.Invalidated, reason, now);
        }

        return pending;
    }
}
