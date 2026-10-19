using EnterpriseForms.Application.Common;
using EnterpriseForms.Application.Common.Persistence;

namespace EnterpriseForms.Application.Features.Identity.EnsureProjection;

/// <summary>Maintains the local tenant/user display projection for an authenticated request. Confers nothing; the authority binds identities independently.</summary>
public sealed class EnsureIdentityProjectionHandler(IBusinessStore store)
{
    public async Task HandleAsync(RequestContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        await using var scope = await store.BeginScopeAsync(cancellationToken).ConfigureAwait(false);
        await scope.EnsureTenantAndUserProjectionAsync(context.OrganizationId, context.LocalUserId, context.IssuerUri, context.SubjectIdentifier, context.DisplayName, cancellationToken).ConfigureAwait(false);
        await scope.CommitAsync(cancellationToken).ConfigureAwait(false);
    }
}
