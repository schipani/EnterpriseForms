using System.Diagnostics;
using AuthorizationAuthority.Application.Common.Ports;
using AuthorizationAuthority.Application.Features.Journal;
using AuthorizationAuthority.Domain.Features.Organizations;

namespace AuthorizationAuthority.Application.Common;

/// <summary>
/// C8 M4 S1 (Design B revision 2, C-116/C-117): every protected SpiceDB decision runs "pin, decide, verify".
/// <list type="number">
/// <item>R1: the caller evaluated the organization gate (A = P, archive current, not quarantined).</item>
/// <item>M1 (<see cref="PinAsync"/>): read the projection marker at least as fresh as the Authority-held token; it must be
/// exactly the expected history-bound marker (<see cref="ProjectionMarker"/>); its <c>read_at</c> becomes the pin S.</item>
/// <item>D: every check and lookup of the request runs <c>at_exact_snapshot(S)</c> through this class and proves it via
/// <c>checked_at</c>/<c>looked_up_at</c> = S.</item>
/// <item>M2 (<see cref="CompleteAsync"/>): a <c>fully_consistent</c> marker read (bypasses SpiceDB caches) after the decision
/// must again be the expected marker; this catches a restore that landed between the pin and the decision (E2/E3).</item>
/// <item>R2: the Authority state is re-read and must be unchanged.</item>
/// </list>
/// Any marker mismatch observed while the Authority state is unchanged has no legitimate cause and quarantines the
/// organization permanently (B-Q6): the quarantine, an audit-stream event and a Critical alert, confirmed under the
/// organization lock that the projector holds for its whole SpiceDB write. A moved state is retryable, never a quarantine.
/// The ZedToken is a snapshot selector only; integrity rests on the marker (Design B §2.2, P4/P5).
/// </summary>
public sealed class ProjectionIntegrityVerifier(IAuthorityStore store, ISpiceDbProjection spiceDb, ProjectionMarkerTransitionState transition, ISecurityAlertSink alerts, ProjectionIntegrityMetrics metrics, TimeProvider clock)
{
    public const string QuarantineEventKind = "ProjectionIntegrityQuarantined";

    public const string QuarantineAlertKind = "PROJECTION_INTEGRITY_QUARANTINE";

    /// <summary>C-133 PD-3: a confirmed mismatch whose quarantine could not be committed (the request is refused; the next decision or check retries).</summary>
    public const string QuarantineNotCommittedAlertKind = "PROJECTION_INTEGRITY_QUARANTINE_NOT_COMMITTED";

    /// <summary>
    /// M1. Precondition: the caller evaluated the gate on <paramref name="state"/>. <paramref name="organizationLocked"/> is true
    /// when the caller holds the organization row lock (then <paramref name="state"/> is the locked, tracked row and a confirmed
    /// mismatch is quarantined and committed in the caller's scope). Throws 503 when not provable, 423 when quarantined.
    /// </summary>
    public async Task<VerifiedProjectionSnapshot> PinAsync(IAuthorityScope scope, OrganizationAuthorizationState state, bool organizationLocked, Guid correlationId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(state);
        if (!transition.Completed)
        {
            throw AuthorityException.NotReady("ProjectionMarkerTransitionPending");
        }

        if (state.IntegrityQuarantineEnabled)
        {
            throw AuthorityException.Quarantined("organization quarantined");
        }

        if (state.AuthorizationRevision != state.ProjectionAppliedRevision || string.IsNullOrEmpty(state.ProjectionZedToken))
        {
            throw AuthorityException.NotReady("AwaitingProjection");
        }

        if (state.ProjectionAppliedRevision > 0 && state.LatestSecurityJournalDigest is not { Length: 32 })
        {
            // An Authority invariant is broken (every revision has its journal entry): never decide, never guess a marker.
            throw AuthorityException.NotReady("authorization journal digest unavailable");
        }

        var expected = ProjectionMarker.ExpectedFor(state);
        var observedState = Copy(state);
        var callerLocked = organizationLocked ? state : null;
        var started = Stopwatch.GetTimestamp();
        MarkerObservation observed;
        try
        {
            observed = await spiceDb.ReadMarkerAsync(state.OrganizationId, MarkerReadConsistency.AtLeastAsFresh, state.ProjectionZedToken, cancellationToken).ConfigureAwait(false);
        }
        catch (SpiceDbSchemaMissingException)
        {
            await QuarantineOrMovedAsync(scope, callerLocked, observedState, ProjectionIntegrityReasons.SchemaMissing, [], "pin", correlationId, cancellationToken).ConfigureAwait(false);
            throw; // unreachable: the call above always throws
        }
        catch (SpiceDbUnavailableException ex)
        {
            metrics.Outcome(ProjectionIntegrityOutcomes.Unavailable);
            throw AuthorityException.NotReady("SpiceDB unavailable: " + ex.Message);
        }
        catch (SpiceDbSnapshotException ex)
        {
            throw SnapshotFailure(state.OrganizationId, ex, correlationId);
        }

        metrics.MarkerRead("pin", Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        var reason = ProjectionMarker.Classify(state.OrganizationId, state.ProjectionAppliedRevision, expected, observed.MarkerSubjectIds);
        if (reason is not null || string.IsNullOrEmpty(observed.ReadAt))
        {
            await QuarantineOrMovedAsync(scope, callerLocked, observedState, reason ?? ProjectionIntegrityReasons.MarkerMissing, observed.MarkerSubjectIds, "pin", correlationId, cancellationToken).ConfigureAwait(false);
        }

        return new VerifiedProjectionSnapshot(scope, state, organizationLocked, expected, observed.ReadAt!, correlationId) { DecisionInstant = clock.GetUtcNow() };
    }

    public async Task<SpiceDbCheckOutcome> CheckAsync(VerifiedProjectionSnapshot snapshot, SpiceDbCheck check, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        snapshot.RequireOpen();
        try
        {
            var outcome = await spiceDb.CheckAsync(check, snapshot, cancellationToken).ConfigureAwait(false);
            return (await ApplyShareExpiryAsync(snapshot, [check], [outcome], strict: false, cancellationToken).ConfigureAwait(false))[0];
        }
        catch (Exception ex) when (ex is SpiceDbSnapshotException or SpiceDbSchemaMissingException or SpiceDbUnavailableException)
        {
            await DecisionReadFailedAsync(snapshot, ex, cancellationToken).ConfigureAwait(false);
            throw; // unreachable
        }
    }

    public async Task<IReadOnlyList<SpiceDbCheckOutcome>> CheckBulkAsync(VerifiedProjectionSnapshot snapshot, IReadOnlyList<SpiceDbCheck> checks, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        snapshot.RequireOpen();
        try
        {
            var outcomes = await spiceDb.CheckBulkAsync(checks, snapshot, cancellationToken).ConfigureAwait(false);
            return await ApplyShareExpiryAsync(snapshot, checks, outcomes, strict: false, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is SpiceDbSnapshotException or SpiceDbSchemaMissingException or SpiceDbUnavailableException)
        {
            await DecisionReadFailedAsync(snapshot, ex, cancellationToken).ConfigureAwait(false);
            throw; // unreachable
        }
    }

    /// <summary>
    /// C10 M1-WP2: SpiceDB's answers as projected, without the access-time share expiry overlay. Only for comparing the projection
    /// with another representation of the same committed graph (the search access projection's verification), never for releasing
    /// an access decision.
    /// </summary>
    public async Task<IReadOnlyList<SpiceDbCheckOutcome>> CheckBulkForProjectionComparisonAsync(VerifiedProjectionSnapshot snapshot, IReadOnlyList<SpiceDbCheck> checks, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        snapshot.RequireOpen();
        try
        {
            return await spiceDb.CheckBulkAsync(checks, snapshot, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is SpiceDbSnapshotException or SpiceDbSchemaMissingException or SpiceDbUnavailableException)
        {
            await DecisionReadFailedAsync(snapshot, ex, cancellationToken).ConfigureAwait(false);
            throw; // unreachable
        }
    }

    /// <summary>IX-2 O-1 strict bulk decision (V2, IX-3): a per-item error fails the call like an outage, with the same S1 handling.</summary>
    public async Task<IReadOnlyList<SpiceDbCheckOutcome>> CheckBulkStrictAsync(VerifiedProjectionSnapshot snapshot, IReadOnlyList<SpiceDbCheck> checks, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        snapshot.RequireOpen();
        try
        {
            var outcomes = await spiceDb.CheckBulkStrictAsync(checks, snapshot, cancellationToken).ConfigureAwait(false);
            return await ApplyShareExpiryAsync(snapshot, checks, outcomes, strict: true, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is SpiceDbSnapshotException or SpiceDbSchemaMissingException or SpiceDbUnavailableException)
        {
            await DecisionReadFailedAsync(snapshot, ex, cancellationToken).ConfigureAwait(false);
            throw; // unreachable
        }
    }

    public async Task<IReadOnlyList<string>> LookupResourcesAsync(VerifiedProjectionSnapshot snapshot, string resourceType, string permission, string subjectType, string subjectId, int maximumResults, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        snapshot.RequireOpen();
        try
        {
            var found = await spiceDb.LookupResourcesAsync(resourceType, permission, subjectType, subjectId, snapshot, maximumResults, cancellationToken).ConfigureAwait(false);
            return await FilterShareExpiryAsync(snapshot, resourceType, permission, subjectType, subjectId, found, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is SpiceDbSnapshotException or SpiceDbSchemaMissingException or SpiceDbUnavailableException)
        {
            await DecisionReadFailedAsync(snapshot, ex, cancellationToken).ConfigureAwait(false);
            throw; // unreachable
        }
    }

    /// <summary>C10 M1-WP2: the access-time share expiry overlay of the snapshot (loaded once, in the decision's scope, at its instant).</summary>
    internal static async Task<ShareExpiryOverlay> ShareExpiryAsync(VerifiedProjectionSnapshot snapshot, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        snapshot.RequireOpen();
        return snapshot.ShareExpiry ??= await ShareExpiryOverlay.LoadAsync(snapshot.Scope, snapshot.OrganizationId, snapshot.DecisionInstant, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// C10 M1-WP2: an Allow that only an expired share could have produced stands only if the subject is an effective owner of the
    /// record, checked at the same pinned snapshot. Never turns a Deny or Unknown into an Allow.
    /// </summary>
    private async Task<IReadOnlyList<SpiceDbCheckOutcome>> ApplyShareExpiryAsync(VerifiedProjectionSnapshot snapshot, IReadOnlyList<SpiceDbCheck> checks,
        IReadOnlyList<SpiceDbCheckOutcome> outcomes, bool strict, CancellationToken cancellationToken)
    {
        var overlay = await ShareExpiryAsync(snapshot, cancellationToken).ConfigureAwait(false);
        if (overlay.IsEmpty)
        {
            return outcomes;
        }

        var result = outcomes.ToArray();
        var owners = new List<(int Index, SpiceDbCheck Check)>();
        for (var i = 0; i < result.Length && i < checks.Count; i++)
        {
            var check = checks[i];
            if (result[i] != SpiceDbCheckOutcome.Allowed)
            {
                continue;
            }

            if (!Guid.TryParse(check.ResourceId, out var resourceId) || !Guid.TryParse(check.SubjectId, out var subjectId))
            {
                result[i] = SpiceDbCheckOutcome.Denied; // an Allow the overlay cannot evaluate is not released
                continue;
            }

            var (verdict, record) = overlay.Evaluate(check.ResourceType, resourceId, check.Permission, check.SubjectType, subjectId, check.SubjectRelation);
            if (verdict == ShareExpiryOverlay.Verdict.OwnerPathRequired)
            {
                owners.Add((i, new SpiceDbCheck(Domain.Features.Organizations.ResourceTypeNames.FormRecord, record.ToString("D"), "effective_owner", check.SubjectType, check.SubjectId, check.SubjectRelation)));
            }
        }

        if (owners.Count > 0)
        {
            var ownerOutcomes = strict
                ? await spiceDb.CheckBulkStrictAsync([.. owners.Select(o => o.Check)], snapshot, cancellationToken).ConfigureAwait(false)
                : await spiceDb.CheckBulkAsync([.. owners.Select(o => o.Check)], snapshot, cancellationToken).ConfigureAwait(false);
            for (var j = 0; j < owners.Count; j++)
            {
                result[owners[j].Index] = ownerOutcomes[j] == SpiceDbCheckOutcome.Allowed ? SpiceDbCheckOutcome.Allowed : SpiceDbCheckOutcome.Denied;
            }
        }

        return result;
    }

    /// <summary>C10 M1-WP2: removes from a lookup every resource the subject reaches only through an expired share (owner paths kept).</summary>
    private async Task<IReadOnlyList<string>> FilterShareExpiryAsync(VerifiedProjectionSnapshot snapshot, string resourceType, string permission, string subjectType, string subjectId,
        IReadOnlyList<string> found, CancellationToken cancellationToken)
    {
        var overlay = await ShareExpiryAsync(snapshot, cancellationToken).ConfigureAwait(false);
        if (overlay.IsEmpty || found.Count == 0)
        {
            return found;
        }

        if (!Guid.TryParse(subjectId, out var subject))
        {
            return [];
        }

        var kept = new List<string>(found.Count);
        var owners = new List<(string Resource, SpiceDbCheck Check)>();
        foreach (var value in found)
        {
            if (!Guid.TryParse(value, out var resource))
            {
                continue; // a value that is not an id is never released (callers drop it too)
            }

            var (verdict, record) = overlay.Evaluate(resourceType, resource, permission, subjectType, subject, string.Empty);
            if (verdict == ShareExpiryOverlay.Verdict.OwnerPathRequired)
            {
                owners.Add((value, new SpiceDbCheck(Domain.Features.Organizations.ResourceTypeNames.FormRecord, record.ToString("D"), "effective_owner", subjectType, subjectId)));
            }
            else
            {
                kept.Add(value);
            }
        }

        if (owners.Count > 0)
        {
            var ownerOutcomes = await spiceDb.CheckBulkAsync([.. owners.Select(o => o.Check)], snapshot, cancellationToken).ConfigureAwait(false);
            var stillOwned = owners.Where((o, j) => ownerOutcomes[j] == SpiceDbCheckOutcome.Allowed).Select(o => o.Resource).ToHashSet(StringComparer.Ordinal);
            kept = [.. found.Where(v => kept.Contains(v) || stillOwned.Contains(v))];
        }

        return kept;
    }

    /// <summary>IX-5 M1: <see cref="ISpiceDbProjection.LookupSubjectsAsync"/> at the snapshot's pin; a snapshot or datastore failure is a failed decision read (fail closed).</summary>
    public async Task<IReadOnlyList<string>> LookupSubjectsAsync(VerifiedProjectionSnapshot snapshot, string resourceType, string resourceId, string permission, string subjectType, string subjectRelation, int maximumSubjects, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        snapshot.RequireOpen();
        try
        {
            return await spiceDb.LookupSubjectsAsync(resourceType, resourceId, permission, subjectType, subjectRelation, snapshot, maximumSubjects, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is SpiceDbSnapshotException or SpiceDbSchemaMissingException or SpiceDbUnavailableException)
        {
            await DecisionReadFailedAsync(snapshot, ex, cancellationToken).ConfigureAwait(false);
            throw; // unreachable
        }
    }

    /// <summary>
    /// M2 + R2. Returns true (snapshot Completed) when the head marker is still the expected marker after the decision and the
    /// Authority state did not move; false (snapshot Failed) when the state moved, so the caller retries or fails closed. A
    /// marker mismatch quarantines (423); an unavailable SpiceDB fails closed (503). R2 also runs when the caller holds the
    /// organization lock (the state cannot differ then; defence in depth).
    /// </summary>
    public async Task<bool> CompleteAsync(VerifiedProjectionSnapshot snapshot, string path, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        snapshot.RequireOpen();
        var started = Stopwatch.GetTimestamp();
        MarkerObservation observed;
        try
        {
            observed = await spiceDb.ReadMarkerAsync(snapshot.OrganizationId, MarkerReadConsistency.FullyConsistent, null, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is SpiceDbSnapshotException or SpiceDbSchemaMissingException or SpiceDbUnavailableException)
        {
            await DecisionReadFailedAsync(snapshot, ex, cancellationToken).ConfigureAwait(false);
            throw; // unreachable
        }

        metrics.MarkerRead("verify", Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        var reason = ProjectionMarker.Classify(snapshot.OrganizationId, snapshot.ExpectedRevision, snapshot.ExpectedMarkerSubjectId, observed.MarkerSubjectIds);
        if (reason is not null)
        {
            snapshot.MarkFailed();
            await QuarantineOrMovedAsync(snapshot, reason, observed.MarkerSubjectIds, "verify", cancellationToken).ConfigureAwait(false);
        }

        var now = await snapshot.Scope.ReadOrganizationAsync(snapshot.OrganizationId, cancellationToken).ConfigureAwait(false);
        if (now is { IntegrityQuarantineEnabled: true })
        {
            snapshot.MarkFailed();
            throw AuthorityException.Quarantined("organization quarantined");
        }

        if (snapshot.StateMoved(now))
        {
            snapshot.MarkFailed();
            metrics.Outcome(ProjectionIntegrityOutcomes.Moved);
            return false;
        }

        snapshot.MarkCompleted();
        metrics.Outcome(ProjectionIntegrityOutcomes.Ok);
        metrics.Decision(path, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        return true;
    }

    /// <summary>
    /// S2: an observed mismatch for an organization that is not in a decision. Quarantines only when, under the organization
    /// lock, the state still equals what the observer saw (<paramref name="observedState"/>).
    /// </summary>
    public async Task QuarantineIfUnchangedAsync(OrganizationAuthorizationState observedState, string reason, IReadOnlyList<string> observedMarkers, string phase, Guid correlationId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(observedState);
        await using var scope = await store.BeginScopeAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await QuarantineOrMovedAsync(scope, null, Copy(observedState), reason, observedMarkers, phase, correlationId, cancellationToken).ConfigureAwait(false);
        }
        catch (AuthorityException)
        {
            // Either quarantined (the desired effect) or the state moved (nothing to do); the background caller just continues.
        }
    }

    /// <summary>
    /// Records the quarantine on an already locked, tracked organization row in <paramref name="scope"/> (the caller commits):
    /// used by lock-confirm and by the startup transition, which inspects every organization under its lock.
    /// </summary>
    public void RecordQuarantine(IAuthorityScope scope, OrganizationAuthorizationState locked, string reason, IReadOnlyList<string> observedMarkers, IReadOnlyList<string> confirmedMarkers, string phase, Guid correlationId)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(locked);
        OrganizationQuarantine.Set(locked, reason);
        locked.StateModifiedOn = clock.GetUtcNow();
        var expectedKnown = locked.AuthorizationRevision == locked.ProjectionAppliedRevision && (locked.ProjectionAppliedRevision == 0 || locked.LatestSecurityJournalDigest is { Length: 32 });
        AuditJournal.Append(scope, locked, QuarantineEventKind, new ProjectionIntegrityQuarantinedBody(
            locked.OrganizationId,
            reason,
            locked.ProjectionAppliedRevision,
            expectedKnown ? ProjectionMarker.ExpectedFor(locked) : null,
            Bounded(observedMarkers),
            Bounded(confirmedMarkers),
            phase,
            correlationId), clock);
    }

    /// <summary>
    /// Commits a quarantine recorded with <see cref="RecordQuarantine"/> in <paramref name="scope"/> and raises its alert. C-133 PD-3:
    /// the commit also deletes the organization's stored search results (FD-2 X2, trigger), so it can fail. A failed commit leaves
    /// nothing committed (the organization is not quarantined, its stored results remain) and is never reported as success: a
    /// Critical alert and a metric are raised, and the exception is rethrown unchanged, so the caller's request is refused (503) and
    /// releases nothing. The mismatch is persistent state (SpiceDB against AuthorityDb), so the next decision on the organization,
    /// or the periodic check, observes it again and retries the quarantine; until one commits, every decision is refused.
    /// </summary>
    public async Task CommitQuarantineAsync(IAuthorityScope scope, Guid organizationId, string reason, string phase, Guid correlationId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scope);
        try
        {
            await scope.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            metrics.Outcome(ProjectionIntegrityOutcomes.QuarantineNotCommitted);
            alerts.Raise(new SecurityAlert(QuarantineNotCommittedAlertKind, "Critical", organizationId, correlationId, null,
                "a confirmed SpiceDB projection mismatch (" + reason + ", phase " + phase + ") could not be committed as an organization quarantine (" + ex.GetType().Name
                + "); the request was refused, nothing was released, and the next decision or periodic check retries the quarantine", correlationId));
            throw;
        }

        RaiseQuarantineAlert(organizationId, reason, correlationId);
    }

    /// <summary>Raised after the quarantine commit; fixed summary, no subject, resource or permission identifiers.</summary>
    public void RaiseQuarantineAlert(Guid organizationId, string reason, Guid correlationId)
    {
        metrics.Outcome(ProjectionIntegrityOutcomes.MismatchQuarantined);
        alerts.Raise(new SecurityAlert(QuarantineAlertKind, "Critical", organizationId, correlationId, null, "SpiceDB projection does not match the Authority's committed revision (" + reason + "); organization quarantined", correlationId));
    }

    private async Task DecisionReadFailedAsync(VerifiedProjectionSnapshot snapshot, Exception failure, CancellationToken cancellationToken)
    {
        snapshot.MarkFailed();
        switch (failure)
        {
            case SpiceDbSchemaMissingException:
                await QuarantineOrMovedAsync(snapshot, ProjectionIntegrityReasons.SchemaMissing, [], "decide", cancellationToken).ConfigureAwait(false);
                break;
            case SpiceDbSnapshotException { Failure: SpiceDbSnapshotFailure.DatastoreChanged }:
                // The datastore changed mid-request: the lock-confirm read decides (a datastore whose head still carries the
                // expected marker is this history's state; anything else is quarantined).
                await QuarantineOrMovedAsync(snapshot, null, [], "snapshot_error", cancellationToken).ConfigureAwait(false);
                break;
            case SpiceDbSnapshotException snapshotFailure:
                throw SnapshotFailure(snapshot.OrganizationId, snapshotFailure, snapshot.CorrelationId);
            default:
                metrics.Outcome(ProjectionIntegrityOutcomes.Unavailable);
                throw AuthorityException.NotReady("SpiceDB unavailable: " + failure.Message);
        }
    }

    private AuthorityException SnapshotFailure(Guid organizationId, SpiceDbSnapshotException failure, Guid correlationId)
    {
        if (failure.Failure == SpiceDbSnapshotFailure.TokenMismatch)
        {
            metrics.Outcome(ProjectionIntegrityOutcomes.TokenMismatch);
            alerts.Raise(new SecurityAlert("PROJECTION_SNAPSHOT_MISMATCH", "Critical", organizationId, correlationId, null, "a SpiceDB decision could not be proven to run at its pinned snapshot; denied", correlationId));
        }
        else
        {
            metrics.Outcome(ProjectionIntegrityOutcomes.SnapshotError);
            alerts.Raise(new SecurityAlert("PROJECTION_SNAPSHOT_UNAVAILABLE", "Warning", organizationId, correlationId, null, "the pinned SpiceDB snapshot is unavailable; denied", correlationId));
        }

        return AuthorityException.NotReady("projection snapshot not provable");
    }

    private Task QuarantineOrMovedAsync(VerifiedProjectionSnapshot snapshot, string? reason, IReadOnlyList<string> observedMarkers, string phase, CancellationToken cancellationToken) =>
        QuarantineOrMovedAsync(snapshot.Scope, snapshot.LockedState, Observed(snapshot), reason, observedMarkers, phase, snapshot.CorrelationId, cancellationToken);

    /// <summary>
    /// Lock-confirm (Design B §2.5). Always throws: 423 once quarantined, 503 when the Authority state moved (a legitimate
    /// cause) or when a mid-request datastore change (<paramref name="reason"/> null) turns out to carry the expected marker.
    /// <paramref name="callerLocked"/> is the caller's locked, tracked row when the caller holds the organization lock (the
    /// quarantine is then committed in the caller's scope); otherwise the lock is taken in a new scope.
    /// <paramref name="observedState"/> carries the revision, applied revision and digest the observation was made against.
    /// </summary>
    private async Task QuarantineOrMovedAsync(IAuthorityScope callerScope, OrganizationAuthorizationState? callerLocked, OrganizationAuthorizationState observedState, string? reason, IReadOnlyList<string> observedMarkers, string phase, Guid correlationId, CancellationToken cancellationToken)
    {
        IAuthorityScope? ownScope = null;
        try
        {
            OrganizationAuthorizationState locked;
            if (callerLocked is not null)
            {
                // Nobody can move the row while the caller holds its lock; the committed values are re-read all the same.
                var current = await callerScope.ReadOrganizationAsync(observedState.OrganizationId, cancellationToken).ConfigureAwait(false);
                if (current is { IntegrityQuarantineEnabled: true })
                {
                    throw AuthorityException.Quarantined("organization quarantined");
                }

                if (current is null || Moved(current, observedState))
                {
                    metrics.Outcome(ProjectionIntegrityOutcomes.Moved);
                    throw AuthorityException.NotReady("authorization state moved during verification");
                }

                locked = callerLocked;
            }
            else
            {
                ownScope = await store.BeginScopeAsync(cancellationToken).ConfigureAwait(false);
                locked = await ownScope.LockOrganizationAsync(observedState.OrganizationId, cancellationToken).ConfigureAwait(false)
                         ?? throw AuthorityException.NotReady("organization state unavailable");
                if (locked.IntegrityQuarantineEnabled)
                {
                    throw AuthorityException.Quarantined("organization quarantined");
                }

                if (Moved(locked, observedState))
                {
                    metrics.Outcome(ProjectionIntegrityOutcomes.Moved);
                    throw AuthorityException.NotReady("authorization state moved during verification");
                }
            }

            // Evidence read under the lock (no projection write can run). It does not veto an observed mismatch (B-Q6).
            IReadOnlyList<string> confirmed = [];
            string? confirmedReason = ProjectionIntegrityReasons.MarkerMissing;
            try
            {
                confirmed = (await spiceDb.ReadMarkerAsync(locked.OrganizationId, MarkerReadConsistency.FullyConsistent, null, cancellationToken).ConfigureAwait(false)).MarkerSubjectIds;
                confirmedReason = ProjectionMarker.Classify(locked.OrganizationId, observedState.ProjectionAppliedRevision, ProjectionMarker.ExpectedFor(observedState), confirmed);
            }
            catch (SpiceDbSchemaMissingException)
            {
                confirmedReason = ProjectionIntegrityReasons.SchemaMissing;
            }
            catch (Exception ex) when (ex is SpiceDbUnavailableException or SpiceDbSnapshotException)
            {
                if (reason is null)
                {
                    metrics.Outcome(ProjectionIntegrityOutcomes.Unavailable);
                    throw AuthorityException.NotReady("SpiceDB unavailable during integrity confirmation");
                }
            }

            reason ??= confirmedReason;
            if (reason is null)
            {
                // A mid-request datastore change whose head carries this history's expected marker: deny this request only.
                metrics.Outcome(ProjectionIntegrityOutcomes.SnapshotError);
                throw AuthorityException.NotReady("projection snapshot not provable");
            }

            var scope = ownScope ?? callerScope;
            RecordQuarantine(scope, locked, reason, observedMarkers, confirmed, phase, correlationId);
            await CommitQuarantineAsync(scope, locked.OrganizationId, reason, phase, correlationId, cancellationToken).ConfigureAwait(false);
            throw AuthorityException.Quarantined("organization quarantined");
        }
        finally
        {
            if (ownScope is not null)
            {
                await ownScope.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private static bool Moved(OrganizationAuthorizationState now, OrganizationAuthorizationState observed) =>
        now.AuthorizationRevision != now.ProjectionAppliedRevision
        || now.AuthorizationRevision != observed.AuthorizationRevision
        || now.ProjectionAppliedRevision != observed.ProjectionAppliedRevision
        || !VerifiedProjectionSnapshot.DigestsEqual(now.LatestSecurityJournalDigest, observed.LatestSecurityJournalDigest);

    private static OrganizationAuthorizationState Copy(OrganizationAuthorizationState state) => new()
    {
        OrganizationId = state.OrganizationId,
        AuthorizationRevision = state.AuthorizationRevision,
        ProjectionAppliedRevision = state.ProjectionAppliedRevision,
        LatestSecurityJournalDigest = state.LatestSecurityJournalDigest is null ? null : (byte[])state.LatestSecurityJournalDigest.Clone(),
    };

    private static OrganizationAuthorizationState Observed(VerifiedProjectionSnapshot snapshot) => new()
    {
        OrganizationId = snapshot.OrganizationId,
        AuthorizationRevision = snapshot.AuthorizationRevision,
        ProjectionAppliedRevision = snapshot.ExpectedRevision,
        LatestSecurityJournalDigest = snapshot.JournalDigest,
    };

    private static List<string> Bounded(IReadOnlyList<string> markers) => markers.Take(4).Select(m => m.Length <= 200 ? m : m[..200]).ToList();
}

/// <summary>Audit-stream body of a projection-integrity quarantine: identifiers of the organization and markers only.</summary>
public sealed record ProjectionIntegrityQuarantinedBody(
    Guid OrganizationId,
    string ReasonCode,
    long ExpectedProjectionRevision,
    string? ExpectedMarker,
    IReadOnlyList<string> ObservedMarkers,
    IReadOnlyList<string> ConfirmedMarkers,
    string DetectionPhase,
    Guid CorrelationId);
