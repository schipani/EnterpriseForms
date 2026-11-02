using EnterpriseForms.Application.Common;
using EnterpriseForms.Application.Common.Persistence;

namespace EnterpriseForms.Application.Features.Identity.EnsureProjection;

/// <summary>Maintains the local tenant/user display projection for an authenticated request. Confers nothing; the authority binds identities independently.</summary>
public sealed class EnsureIdentityProjectionHandler(IBusinessStore store, TimeProvider clock)
{
    /// <summary>A completed sign-in: the projection plus the last sign-in time shown in the people directory (display only). Every sign-in path must call this.</summary>
    public async Task RecordSignInAsync(RequestContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        await using var scope = await store.BeginScopeAsync(cancellationToken).ConfigureAwait(false);
        await scope.EnsureTenantAndUserProjectionAsync(context.OrganizationId, context.LocalUserId, context.IssuerUri, context.SubjectIdentifier, context.DisplayName, cancellationToken).ConfigureAwait(false);
        await scope.RecordUserSignInAsync(context.OrganizationId, context.LocalUserId, clock.GetUtcNow(), cancellationToken).ConfigureAwait(false);
        await scope.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task HandleAsync(RequestContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        await using var scope = await store.BeginScopeAsync(cancellationToken).ConfigureAwait(false);
        await scope.EnsureTenantAndUserProjectionAsync(context.OrganizationId, context.LocalUserId, context.IssuerUri, context.SubjectIdentifier, context.DisplayName, cancellationToken).ConfigureAwait(false);
        await scope.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Projects several known principals of one organization at once (development personas); display data only, no permission.</summary>
    public async Task HandleManyAsync(Guid organizationId, IReadOnlyList<(Guid UserId, string IssuerUri, string SubjectIdentifier, string DisplayName)> principals, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(principals);
        await using var scope = await store.BeginScopeAsync(cancellationToken).ConfigureAwait(false);
        foreach (var principal in principals)
        {
            await scope.EnsureTenantAndUserProjectionAsync(organizationId, principal.UserId, principal.IssuerUri, principal.SubjectIdentifier, principal.DisplayName, cancellationToken).ConfigureAwait(false);
        }

        await scope.CommitAsync(cancellationToken).ConfigureAwait(false);
    }
}
