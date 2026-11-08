using System.Text.RegularExpressions;
using AuthorizationAuthority.Application.Common;
using AuthorizationAuthority.Application.Common.Ports;
using AuthorizationAuthority.Application.Features.Authorization;
using AuthorizationAuthority.Application.Features.Journal;
using AuthorizationAuthority.Contracts.V1;
using AuthorizationAuthority.Domain.Features.SearchIndex;

namespace AuthorizationAuthority.Application.Features.SearchIndex;

/// <summary>
/// IX-2 <c>SearchProjectionRebuild</c> (design §12.2, IX-D7, §17.2, §17.8 IX-45): the only way a search-index quarantine is
/// cleared. A Security Administrator (<c>administer_security</c>, decided on the S1 path at a pinned snapshot) asks for it; the
/// organization's authorization gate must be open, so an S1 organization quarantine refuses it (423) and is never downgraded.
/// In one transaction under the organization lock: every entry, posting and anomaly of the organization is deleted, every
/// non-withdrawn coverage returns to Building, and the projection becomes Rebuilding (journaled on the audit stream with the
/// actor). The index serves nothing until the builder has re-derived it and the Authority has attested every coverage again.
/// Step-up assurance (PB-08) is not available and is not claimed (design §12.2 "step-up when PB-08 exists").
/// </summary>
public sealed partial class SearchProjectionRebuildService(IAuthorityStore store, PermissionCheckService permissions, SearchIndexOptions options, TimeProvider clock)
{
    [GeneratedRegex("^[A-Z][A-Z0-9_]{2,63}$")]
    private static partial Regex ReasonCode();

    public async Task<SearchProjectionRebuildResponse> RebuildAsync(VerifiedActor actor, SearchProjectionRebuildRequest request, Guid correlationId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(request);
        if (options.Mode != SearchIndexMode.Dark)
        {
            throw new AuthorityException(AuthorityErrorCodes.SearchIndexNotPresent, 404, false, "the search index is not maintained in this deployment");
        }

        if (string.IsNullOrEmpty(request.RebuildReasonCode) || !ReasonCode().IsMatch(request.RebuildReasonCode))
        {
            throw AuthorityException.Invalid("rebuild_reason_code must be an upper-case code (3..64 characters, A-Z, 0-9, _)");
        }

        // S1 decision (pin, decide, verify); refuses with 423 while the organization is quarantined.
        var decisions = await permissions.CheckBatchAsync(actor, [new PermissionCheckRequest(request.OrganizationId, new ResourceRef(ResourceKind.Organization, request.OrganizationId), RequestedAction.AdministerSecurity)], correlationId, cancellationToken).ConfigureAwait(false);
        if (!decisions[0].Allowed)
        {
            throw AuthorityException.Forbidden("SearchProjectionRebuild requires administer_security");
        }

        await using var scope = await store.BeginScopeAsync(cancellationToken).ConfigureAwait(false);
        var state = await scope.LockOrganizationAsync(request.OrganizationId, cancellationToken).ConfigureAwait(false)
                    ?? throw AuthorityException.NotRegistered("organization is not registered");
        OrganizationGate.RequireOpen(await OrganizationGate.EvaluateAsync(scope, state, cancellationToken).ConfigureAwait(false));
        if (state.AuthorizationRevision != decisions[0].AuthorizationRevision)
        {
            throw AuthorityException.NotReady("authorization state moved after the decision; retry");
        }

        var bound = await ActorResolver.BindAsync(scope, state.OrganizationId, actor, cancellationToken).ConfigureAwait(false);
        var index = scope.SearchIndex;
        var projection = await index.FindProjectionStateAsync(state.OrganizationId, cancellationToken).ConfigureAwait(false)
                         ?? throw new AuthorityException(AuthorityErrorCodes.SearchIndexNotPresent, 404, false, "the organization has no search index");

        var now = clock.GetUtcNow();
        await index.BindOrganizationAsync(state.OrganizationId, cancellationToken).ConfigureAwait(false);
        var purged = await index.PurgeOrganizationIndexAsync(state.OrganizationId, cancellationToken).ConfigureAwait(false);
        var coverages = (await index.GetCoveragesAsync(state.OrganizationId, null, cancellationToken).ConfigureAwait(false)).Where(c => c.CoverageStateName != SearchCoverageStates.Withdrawn).ToList();
        foreach (var coverage in coverages)
        {
            coverage.CoverageStateName = SearchCoverageStates.Building;
            coverage.CoverageCompletedOn = null;
            coverage.CoverageAttestedJournalEntryId = null;
        }

        foreach (var form in coverages.Select(c => c.FormDefinitionId).Distinct())
        {
            await index.BumpFormStateAsync(state.OrganizationId, form, now, cancellationToken).ConfigureAwait(false);
        }

        var previousStatus = projection.ProjectionStatusName;
        var previousReason = projection.ProjectionStatusReasonCode;
        projection.ProjectionStatusName = coverages.Count > 0 ? SearchProjectionStatuses.Rebuilding : SearchProjectionStatuses.Active;
        projection.ProjectionStatusReasonCode = coverages.Count > 0 ? request.RebuildReasonCode : null;
        projection.ReconciliationFromRevision = null;
        projection.ProjectionContentIntegrityRevision = state.ContentIntegrityRevision; // nothing derived is left to be stale
        projection.ProjectionModifiedOn = now;
        AuditJournal.Append(scope, state, "SearchProjectionRebuildRequested", new
        {
            organization_id = state.OrganizationId, requested_by_authority_user_id = bound.AuthorityUserId, rebuild_reason_code = request.RebuildReasonCode,
            previous_status = previousStatus, previous_reason_code = previousReason, entries_purged = purged, coverages_building = coverages.Count,
            authorization_revision = state.AuthorizationRevision, content_integrity_revision = state.ContentIntegrityRevision, requested_on = now, correlation_id = correlationId,
        }, clock);
        await scope.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new SearchProjectionRebuildResponse(state.OrganizationId, previousStatus, previousReason, projection.ProjectionStatusName, purged, coverages.Count, state.AuthorizationRevision, correlationId);
    }
}
