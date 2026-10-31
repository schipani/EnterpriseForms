using AuthorizationAuthority.Application.Common;
using AuthorizationAuthority.Application.Common.Ports;
using AuthorizationAuthority.Application.Features.Membership;
using AuthorizationAuthority.Contracts.V1;
using AuthorizationAuthority.Domain.Features.Organizations;

namespace AuthorizationAuthority.Application.Features.SecurityCommands;

/// <summary>
/// C8 M2 (AC-7.5): an owner cancels, or edits by replacing, a Pending membership request. Edit never mutates a request in place: the
/// original becomes Replaced and a new request is created in the same transaction (new id, fresh expiry, identity copied from the
/// original by the Authority, never supplied), so no operator approval can carry over; both commands change no binding, role or
/// session. The two-operator approval is unchanged and only ever applies to a Pending request.
/// </summary>
public static partial class CommandPlanner
{
    private static async Task<CommandPlan> CancelMembershipRequestAsync(IAuthorityScope scope, Guid organizationId, BoundActor actor, SecurityCommandRequest request, DesiredStateGraph graph, FieldSearchApprovalGate gate, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var payload = CanonicalJson.Deserialize<CancelMembershipRequestPayload>(request.CommandPayload);
        RequireResource(request, ResourceKind.Organization, organizationId);
        RequireOwnerActor(graph, organizationId, actor);
        RequireReason(payload.Reason);
        gate.RequireStepUp(payload.StepUpEvidence);
        var original = await FindRequestForChangeAsync(scope, organizationId, payload.MembershipRequestId, payload.ExpectedRequestDigest, now, cancellationToken).ConfigureAwait(false);
        return new CommandPlan
        {
            NormalizedPayloadJson = CanonicalJson.Serialize(payload with { ExpectedRequestDigest = payload.ExpectedRequestDigest.Trim().ToLowerInvariant(), Reason = payload.Reason.Trim(), StepUpEvidence = null }),
            CancelledMembershipRequest = original,
            CancelledByAuthorityUserId = actor.AuthorityUserId,
            CancellationReason = payload.Reason.Trim(),
        };
    }

    private static async Task<CommandPlan> ReplaceMembershipRequestAsync(IAuthorityScope scope, Guid organizationId, BoundActor actor, SecurityCommandRequest request, DesiredStateGraph graph, FieldSearchApprovalGate gate, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var payload = CanonicalJson.Deserialize<ReplaceMembershipRequestPayload>(request.CommandPayload);
        RequireResource(request, ResourceKind.Organization, organizationId);
        RequireOwnerActor(graph, organizationId, actor);
        RequireReason(payload.Reason);
        if (payload.InitialRoleName is not (null or OrganizationRoleNames.Designer))
        {
            throw AuthorityException.Invalid("a new member may start with no role or designer; owners and Security Administrators are appointed afterwards");
        }

        var hint = payload.DisplayNameHint?.Trim() ?? string.Empty;
        if (hint.Length is 0 or > 200 || hint.Any(char.IsControl))
        {
            throw AuthorityException.Invalid("a display name of 1 to 200 characters is required (display only, never an identifier)");
        }

        gate.RequireStepUp(payload.StepUpEvidence);
        var original = await FindRequestForChangeAsync(scope, organizationId, payload.MembershipRequestId, payload.ExpectedRequestDigest, now, cancellationToken).ConfigureAwait(false);
        var reason = payload.Reason.Trim();
        if (hint == original.RequestedDisplayNameHint && payload.InitialRoleName == original.RequestedInitialRoleName && reason == original.RequestReasonText)
        {
            throw AuthorityException.Invalid("nothing was changed; edit the display name, initial role or reason, or cancel the request");
        }

        // The identity is the original's, never the caller's: it must still be valid for the organization and still not a member.
        var bindings = await scope.GetIdentityBindingsAsync(organizationId, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(OrganizationIdentityIssuer(graph, organizationId, bindings), original.RequestedIssuerUri, StringComparison.Ordinal))
        {
            throw new AuthorityException(AuthorityErrorCodes.IdentityNotAllowed, 422, false, "the identity no longer matches the organization's configured identity provider tenant");
        }

        if (bindings.Any(b => b.IdentityIssuerUri == original.RequestedIssuerUri && b.IdentitySubjectIdentifier == original.RequestedSubjectIdentifier))
        {
            throw new AuthorityException(AuthorityErrorCodes.IdentityAlreadyBound, 409, false, "this identity is already a member of the organization");
        }

        return new CommandPlan
        {
            NormalizedPayloadJson = CanonicalJson.Serialize(payload with { ExpectedRequestDigest = payload.ExpectedRequestDigest.Trim().ToLowerInvariant(), DisplayNameHint = hint, Reason = reason, StepUpEvidence = null }),
            CancelledMembershipRequest = original,
            CancelledByAuthorityUserId = actor.AuthorityUserId,
            CancellationReason = "replaced by an edited request",
            NewMembershipRequest = new OrganizationMembershipRequest
            {
                OrganizationId = organizationId,
                RequestedAuthorityUserId = Guid.CreateVersion7(),
                RequestedIssuerUri = original.RequestedIssuerUri,
                RequestedSubjectIdentifier = original.RequestedSubjectIdentifier,
                RequestedDisplayNameHint = hint,
                RequestedInitialRoleName = payload.InitialRoleName,
                RequestedByAuthorityUserId = actor.AuthorityUserId,
                RequestReasonText = reason,
                RequestCreatedOn = now,
                RequestExpiresOn = now + MembershipRequestLifetime,
                RequestStateName = MembershipRequestStates.Pending,
                ReplacesMembershipRequestId = original.OrganizationMembershipRequestId,
            },
        };
    }

    /// <summary>
    /// The request of this organization (another organization's is simply not found), unchanged since the caller read it (digest) and still
    /// Pending and unexpired. A stale digest is MEMBERSHIP_REQUEST_CHANGED whatever happened meanwhile (cancelled, replaced, edited, decided).
    /// </summary>
    private static async Task<OrganizationMembershipRequest> FindRequestForChangeAsync(IAuthorityScope scope, Guid organizationId, Guid requestId, string? expectedDigest, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (requestId == Guid.Empty || string.IsNullOrWhiteSpace(expectedDigest))
        {
            throw AuthorityException.Invalid("membership_request_id and expected_request_digest are required");
        }

        var original = await scope.FindMembershipRequestAsync(organizationId, requestId, cancellationToken).ConfigureAwait(false)
                       ?? throw new AuthorityException(AuthorityErrorCodes.NotFound, 404, false, "no such membership request in this organization");
        if (!string.Equals(MembershipRequestDigest.Compute(original), expectedDigest.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            throw new AuthorityException(AuthorityErrorCodes.MembershipRequestChanged, 409, false, "the request changed since it was read; reload it and try again");
        }

        if (original.RequestStateName != MembershipRequestStates.Pending)
        {
            throw new AuthorityException(AuthorityErrorCodes.MembershipRequestNotPending, 409, false, $"the request is {original.RequestStateName}; only a pending request can be changed");
        }

        if (original.RequestExpiresOn <= now)
        {
            throw new AuthorityException(AuthorityErrorCodes.MembershipRequestExpired, 409, false, "the request expired; request the membership again");
        }

        return original;
    }
}
