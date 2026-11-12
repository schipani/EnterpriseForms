using EnterpriseForms.Application.Common;
using EnterpriseForms.Application.Common.Authority;
using EnterpriseForms.Contracts.Features;

namespace EnterpriseForms.Application.Features.Search.IndexBuilder;

/// <summary>
/// IX-2 SearchProjectionRebuild for a Security Administrator. The business API only forwards the request with the caller's
/// delegated assertion; the Authority decides <c>administer_security</c> on the S1 path, refuses while the organization is
/// quarantined, purges and journals. Nothing here can clear a quarantine or mark anything complete.
/// </summary>
public sealed class SearchIndexAdministrationHandler(ISearchIndexAdministrationAuthority authority)
{
    public async Task<SearchProjectionRebuildResponse> RebuildAsync(RequestContext context, SearchProjectionRebuildRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(request);
        AuthoritySearchProjectionRebuild rebuilt;
        try
        {
            rebuilt = await authority.RebuildSearchProjectionAsync(context.ToDelegatedUser(), new SearchProjectionRebuildCommand(context.OrganizationId, request.RebuildReasonCode ?? string.Empty), cancellationToken).ConfigureAwait(false);
        }
        catch (AuthorityClientException ex) when (ex.HttpStatusCode == 403)
        {
            throw new BusinessException("FORBIDDEN", 403, "the search index is rebuilt by security administrators");
        }

        return new SearchProjectionRebuildResponse(rebuilt.PreviousProjectionStatusName, rebuilt.PreviousStatusReasonCode, rebuilt.ProjectionStatusName, rebuilt.EntriesPurged, rebuilt.CoveragesReturnedToBuilding);
    }
}
