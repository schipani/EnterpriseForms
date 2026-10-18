using AuthorizationAuthority.Application.Common.Ports;

namespace AuthorizationAuthority.Application.Common;

public static class ActorResolver
{
    /// <summary>Maps the independently verified (issuer, subject) to the organization's identity binding. Unbound or disabled ⇒ 403 without revealing resources.</summary>
    public static async Task<BoundActor> BindAsync(IAuthorityScope scope, Guid organizationId, VerifiedActor actor, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);
        var binding = await scope.FindIdentityBindingAsync(organizationId, actor.IssuerUri, actor.SubjectIdentifier, cancellationToken).ConfigureAwait(false);
        if (binding is null || !binding.IdentityBindingEnabled)
        {
            throw new AuthorityException(AuthorityErrorCodes.ActorNotBound, 403, false, "actor is not bound to the organization");
        }

        return new BoundActor(actor, binding);
    }
}
