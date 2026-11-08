using AuthorizationAuthority.Application.Common;
using AuthorizationAuthority.Application.Common.Ports;
using AuthorizationAuthority.Domain.Features.Journal;
using AuthorizationAuthority.Domain.Features.Organizations;

namespace AuthorizationAuthority.Application.Features.Projection;

/// <summary>
/// C8 M4 S1 (Design B §2.3 transition, §2.8 periodic check). Both inspect every organization's projection marker with a
/// <c>fully_consistent</c> read and quarantine through <see cref="ProjectionIntegrityVerifier"/>; neither ever clears a
/// quarantine or rewrites a mismatching marker. The periodic check complements the decision-time protocol, never replaces it.
/// </summary>
public sealed class ProjectionIntegrityMonitor(IAuthorityStore store, ISpiceDbProjection spiceDb, ProjectionIntegrityVerifier verifier, ProjectionMarkerTransitionState transition)
{
    /// <summary>
    /// One-time upgrade of pre-S1 (v1, revision-only) markers to the history-bound v2 form, under each organization's lock,
    /// before any decision is pinned or any projection is written. A single v1 marker at a revision between the applied and the
    /// committed revision is rewritten (CAS: must match the v1 marker); a marker already in the expected v2 form is kept;
    /// anything else is quarantined. An organization never projected (no token, applied revision 0, no marker) is skipped.
    /// Throws <see cref="SpiceDbUnavailableException"/> while SpiceDB is unavailable (the caller retries).
    /// </summary>
    public async Task<int> RunTransitionAsync(CancellationToken cancellationToken)
    {
        var upgraded = 0;
        foreach (var organizationId in await store.GetAllOrganizationIdsAsync(cancellationToken).ConfigureAwait(false))
        {
            await using var scope = await store.BeginScopeAsync(cancellationToken).ConfigureAwait(false);
            var state = await scope.LockOrganizationAsync(organizationId, cancellationToken).ConfigureAwait(false);
            if (state is null || state.IntegrityQuarantineEnabled)
            {
                continue;
            }

            IReadOnlyList<string> observed;
            try
            {
                observed = (await spiceDb.ReadMarkerAsync(organizationId, MarkerReadConsistency.FullyConsistent, null, cancellationToken).ConfigureAwait(false)).MarkerSubjectIds;
            }
            catch (SpiceDbSchemaMissingException)
            {
                if (string.IsNullOrEmpty(state.ProjectionZedToken) && state.ProjectionAppliedRevision == 0)
                {
                    continue;
                }

                var schemaCorrelation = Guid.CreateVersion7();
                verifier.RecordQuarantine(scope, state, ProjectionIntegrityReasons.SchemaMissing, [], [], "transition", schemaCorrelation);
                await verifier.CommitQuarantineAsync(scope, organizationId, ProjectionIntegrityReasons.SchemaMissing, "transition", schemaCorrelation, cancellationToken).ConfigureAwait(false);
                continue;
            }

            if (observed.Count == 0 && string.IsNullOrEmpty(state.ProjectionZedToken) && state.ProjectionAppliedRevision == 0)
            {
                continue; // not projected yet: the projector initializes the v2 step-0 marker before revision 1
            }

            var outcome = await ClassifyForTransitionAsync(scope, state, observed, cancellationToken).ConfigureAwait(false);
            if (outcome.Upgrade is { } upgrade)
            {
                await spiceDb.ApplyDeltaAsync(organizationId, upgrade.From, upgrade.To, [], cancellationToken).ConfigureAwait(false);
                upgraded++;
                continue;
            }

            if (outcome.QuarantineReason is { } reason)
            {
                var correlationId = Guid.CreateVersion7();
                verifier.RecordQuarantine(scope, state, reason, observed, observed, "transition", correlationId);
                await verifier.CommitQuarantineAsync(scope, organizationId, reason, "transition", correlationId, cancellationToken).ConfigureAwait(false);
            }
        }

        transition.MarkCompleted();
        return upgraded;
    }

    /// <summary>
    /// S2: one pass over every organization whose projection is current and that is not quarantined. A mismatch is confirmed
    /// under the organization lock (state unchanged) and quarantined. Returns false when SpiceDB was unavailable for some read.
    /// </summary>
    public async Task<bool> RunPeriodicCheckAsync(CancellationToken cancellationToken)
    {
        if (!transition.Completed)
        {
            return true;
        }

        var allReachable = true;
        foreach (var organizationId in await store.GetAllOrganizationIdsAsync(cancellationToken).ConfigureAwait(false))
        {
            OrganizationAuthorizationState? state;
            await using (var scope = await store.BeginScopeAsync(cancellationToken).ConfigureAwait(false))
            {
                state = await scope.ReadOrganizationAsync(organizationId, cancellationToken).ConfigureAwait(false);
            }

            if (state is null || state.IntegrityQuarantineEnabled || state.AuthorizationRevision != state.ProjectionAppliedRevision || string.IsNullOrEmpty(state.ProjectionZedToken)
                || (state.ProjectionAppliedRevision > 0 && state.LatestSecurityJournalDigest is not { Length: 32 }))
            {
                continue;
            }

            string? reason;
            IReadOnlyList<string> observed = [];
            try
            {
                observed = (await spiceDb.ReadMarkerAsync(organizationId, MarkerReadConsistency.FullyConsistent, null, cancellationToken).ConfigureAwait(false)).MarkerSubjectIds;
                reason = ProjectionMarker.Classify(organizationId, state.ProjectionAppliedRevision, ProjectionMarker.ExpectedFor(state), observed);
            }
            catch (SpiceDbSchemaMissingException)
            {
                reason = ProjectionIntegrityReasons.SchemaMissing;
            }
            catch (Exception ex) when (ex is SpiceDbUnavailableException or SpiceDbSnapshotException)
            {
                allReachable = false;
                continue;
            }

            if (reason is not null)
            {
                await verifier.QuarantineIfUnchangedAsync(state, reason, observed, "periodic", Guid.CreateVersion7(), cancellationToken).ConfigureAwait(false);
            }
        }

        return allReachable;
    }

    private static async Task<(string? QuarantineReason, (string From, string To)? Upgrade)> ClassifyForTransitionAsync(IAuthorityScope scope, OrganizationAuthorizationState state, IReadOnlyList<string> observed, CancellationToken cancellationToken)
    {
        var organizationId = state.OrganizationId;
        var applied = state.ProjectionAppliedRevision;
        if (observed.Count != 1 || !ProjectionMarker.TryParse(organizationId, observed[0], out var revision, out var digestHex))
        {
            return (observed.Count == 0 ? ProjectionIntegrityReasons.MarkerMissing : ProjectionIntegrityReasons.MarkerInvalid, null);
        }

        // A marker may legitimately be one step ahead of the acknowledged revision (crash after the SpiceDB write, before the
        // Authority commit); it is never ahead of the committed revision or behind the applied one.
        if (revision < applied)
        {
            return (ProjectionIntegrityReasons.MarkerBehind, null);
        }

        if (revision > state.AuthorizationRevision)
        {
            return (ProjectionIntegrityReasons.MarkerAhead, null);
        }

        byte[]? digest = null;
        if (revision > 0)
        {
            var entry = await scope.FindJournalEntryAsync(organizationId, JournalStreamNames.Authorization, revision, cancellationToken).ConfigureAwait(false);
            if (entry?.JournalEventDigest is not { Length: 32 } eventDigest)
            {
                return (ProjectionIntegrityReasons.MarkerInvalid, null);
            }

            digest = eventDigest;
        }

        var expected = ProjectionMarker.SubjectId(organizationId, revision, digest);
        if (digestHex is null)
        {
            return (null, (observed[0], expected)); // v1 at a valid revision: upgrade by CAS
        }

        return string.Equals(observed[0], expected, StringComparison.Ordinal) ? (null, null) : (ProjectionIntegrityReasons.ForeignHistory, null);
    }
}
