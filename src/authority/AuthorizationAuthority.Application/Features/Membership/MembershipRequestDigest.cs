using AuthorizationAuthority.Application.Common;
using AuthorizationAuthority.Domain.Features.Organizations;

namespace AuthorizationAuthority.Application.Features.Membership;

/// <summary>
/// Optimistic-concurrency token of a membership request (C8 M2, AC-7.5): SHA-256 over the canonical request id, stored state and the
/// editable content (display name, initial role, reason). Any cancel, replace, decision or edit changes it, so a command made on
/// an older reading of the request is refused. It is not a secret and not an approval.
/// </summary>
public static class MembershipRequestDigest
{
    public static string Compute(OrganizationMembershipRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var canonical = CanonicalJson.Serialize(new
        {
            version = 1,
            membership_request_id = request.OrganizationMembershipRequestId,
            request_state = request.RequestStateName,
            display_name_hint = request.RequestedDisplayNameHint,
            initial_role_name = request.RequestedInitialRoleName,
            request_reason = request.RequestReasonText,
        });
        return Convert.ToHexStringLower(CanonicalJson.Digest(canonical));
    }
}
