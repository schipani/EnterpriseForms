using AuthorizationAuthority.Domain.Features.Organizations;

namespace AuthorizationAuthority.Application.Common;

/// <summary>
/// The delegated end-user identity after the host validated the user assertion independently of the business API
/// (issuer, audience, signature, lifetime). Mapped to an <see cref="IdentityBinding"/> per organization by the authority;
/// never a client-supplied user id.
/// </summary>
public sealed record VerifiedActor(string IssuerUri, string SubjectIdentifier, string WorkloadName);

/// <summary>Resolved binding used inside a command/decision scope.</summary>
public sealed record BoundActor(VerifiedActor Actor, IdentityBinding Binding)
{
    public Guid AuthorityUserId => Binding.AuthorityUserId;
}
