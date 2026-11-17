using AuthorizationAuthority.Application.Common.Ports;
using AuthorizationAuthority.Domain.Features.Organizations;
using AuthorizationAuthority.Domain.Features.SearchIndex;

namespace AuthorizationAuthority.Application.Features.SearchIndex;

/// <summary>
/// IX-1, INV-R (design §18.2.2): the only code that advances <see cref="OrganizationAuthorizationState.ContentIntegrityRevision"/>
/// (architecture test <c>ContentIntegrityRevisionIsAdvancedOnlyByTheCoordinator</c>). Every advance, in the same transaction,
/// either brings the search projection to the new revision (with the derived index changes, or none) or marks it
/// ReconciliationRequired from the previous revision. A revision mismatch is never classified as tampering. The database
/// enforces the same invariant with a deferred constraint trigger.
/// <para>Call sites (W1 to W4, <c>ContentService</c>): the commit (368), the two verified-read quarantines (735, 901) and the
/// share-editor current-snapshot mismatch (1235).</para>
/// </summary>
public sealed class ContentRevisionCoordinator(SearchIndexWriter writer, TimeProvider clock)
{
    /// <summary>A coordinator for hosts without a search index (unit tests, Disabled hosts): it still keeps INV-R for any existing projection.</summary>
    public static ContentRevisionCoordinator WithoutIndex { get; } = new(new SearchIndexWriter(new SearchIndexOptions(), null), TimeProvider.System);

    /// <summary>W1: a content commit. Derives the record's index at the new head, then advances the revision and the projection.</summary>
    public async Task<long> AdvanceForCommitAsync(IAuthorityScope scope, OrganizationAuthorizationState state, CommitIndexInput input, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(state);
        var projection = await scope.SearchIndex.FindProjectionStateAsync(state.OrganizationId, cancellationToken).ConfigureAwait(false);
        if (projection is null)
        {
            return state.AdvanceContentIntegrityRevision(ContentRevisionProjectionAction.AdvanceWithDerivation);
        }

        if (writer.Mode == SearchIndexMode.Disabled)
        {
            return MarkReconciliationRequired(state, projection, "INDEX_DISABLED_AT_COMMIT");
        }

        await writer.DeriveOnCommitAsync(scope, state, input, cancellationToken).ConfigureAwait(false);
        var revision = state.AdvanceContentIntegrityRevision(ContentRevisionProjectionAction.AdvanceWithDerivation);
        Synchronize(projection, revision);
        return revision;
    }

    /// <summary>W2-W4: a content-integrity event that changes no indexed content (quarantine incidents, snapshot mismatch).</summary>
    public async Task<long> AdvanceWithoutIndexChangeAsync(IAuthorityScope scope, OrganizationAuthorizationState state, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(state);
        var projection = await scope.SearchIndex.FindProjectionStateAsync(state.OrganizationId, cancellationToken).ConfigureAwait(false);
        var revision = state.AdvanceContentIntegrityRevision(ContentRevisionProjectionAction.AdvanceWithoutPostingChange);
        if (projection is not null)
        {
            Synchronize(projection, revision);
        }

        return revision;
    }

    private long MarkReconciliationRequired(OrganizationAuthorizationState state, SearchProjectionState projection, string reason)
    {
        var previous = state.ContentIntegrityRevision;
        var revision = state.AdvanceContentIntegrityRevision(ContentRevisionProjectionAction.ReconciliationRequired);
        if (projection.ProjectionStatusName == SearchProjectionStatuses.Active)
        {
            projection.ProjectionStatusName = SearchProjectionStatuses.ReconciliationRequired;
            projection.ProjectionStatusReasonCode = reason;
            projection.ReconciliationFromRevision = previous;
        }

        projection.ProjectionModifiedOn = clock.GetUtcNow();
        return revision;
    }

    private void Synchronize(SearchProjectionState projection, long revision)
    {
        // A projection already awaiting reconciliation stays so (its from-revision is kept); otherwise it follows the revision.
        if (projection.ProjectionStatusName != SearchProjectionStatuses.ReconciliationRequired)
        {
            projection.ProjectionContentIntegrityRevision = revision;
        }

        projection.ProjectionModifiedOn = clock.GetUtcNow();
    }
}
