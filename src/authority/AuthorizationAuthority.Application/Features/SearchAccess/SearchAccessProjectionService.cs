using AuthorizationAuthority.Application.Common;
using AuthorizationAuthority.Application.Common.Ports;
using AuthorizationAuthority.Application.Features.Journal;
using AuthorizationAuthority.Contracts.V1;
using AuthorizationAuthority.Domain.Features.Journal;
using AuthorizationAuthority.Domain.Features.SearchAccess;

namespace AuthorizationAuthority.Application.Features.SearchAccess;

/// <summary>Observable state of an organization's Tier 2 access projection (UF-21).</summary>
public static class SearchAccessStatusNames
{
    public const string Disabled = "Disabled";
    public const string NotBuilt = "NotBuilt";
    public const string Building = "Building";
    public const string Ready = "Ready";
    public const string Quarantined = "Quarantined";
}

public sealed record SearchAccessStatus(Guid OrganizationId, string StatusName, long ConsumedAuthorizationRevision, long CurrentAuthorizationRevision, long CheckpointRevision, int ProtocolVersion, string? QuarantineReasonCode,
    long ConsumedContentIntegrityRevision = 0, long CurrentContentIntegrityRevision = 0, bool CoverageCurrent = false, string? Tier1StatusName = null);

/// <summary>IX-5b: the live authoritative state a checkpoint must equal exactly to be ready (A-3, C-148 Part 4).</summary>
public sealed record SearchAccessReadinessInputs(long AuthorizationRevision, byte[]? AuthorizationDigest, long ContentIntegrityRevision, byte[]? ContentDigest, byte[]? CoverageDigest, string? Tier1StatusName);

public enum SearchAccessStepOutcome
{
    Idle,
    Advanced,
    NotBuilt,
    Quarantined,
    Contended,
    Fenced,

    /// <summary>IX-5b: the rows were derived by an older protocol; a rebuild (not a repair) upgrades them.</summary>
    UpgradeRequired,
}

/// <summary>C-149 (R-55): the outcome of a read-only maintenance probe.</summary>
public enum SearchAccessProbeOutcome
{
    /// <summary>The checkpoint equals the live authorization, content and coverage state: nothing to consume.</summary>
    Settled,

    /// <summary>Quarantined: nothing is consumed until a journaled rebuild.</summary>
    Quarantined,

    /// <summary>Anything else: the steps run (and verify) under the checkpoint row lock.</summary>
    WorkPending,
}

/// <summary>C-149: the result of one organization's maintenance step (for logging and tests).</summary>
public sealed record SearchAccessMaintenanceResult(SearchAccessStepOutcome Outcome, int Advanced, bool SettledByProbe);

public sealed record SearchAccessVerification(string OutcomeCode, long VerifiedRevision, int MirrorEdges, int CanonicalEdges, int RecordGrants, int FieldItemGrants, int SampledRecords, int SpiceDbChecks, string? FindingDetail,
    int ValuePostings = 0, int SearchablePostings = 0, long VerifiedContentIntegrityRevision = 0)
{
    public bool Clean => OutcomeCode == "CLEAN";
}

public sealed record SearchAccessReplayCheck(string OutcomeCode, long RevisionsReplayed, long CurrentAuthorizationRevision, int ReplayedEdges, int CanonicalEdges, int MissingEdges, int ExtraEdges, long ContentRevisionsVerified = 0);

/// <summary>IX-5b, Testing only: one equality (or integer range) condition of an internal value-projection evaluation.</summary>
public sealed record SearchAccessEvaluationCondition(Guid FieldDefinitionId, string ValueTypeName, string? StringValue = null, long? MinimumInteger = null, long? MaximumInteger = null, Guid? FieldOptionId = null, bool? BooleanValue = null);

/// <summary>
/// IX-5a (C-147, FD-5-ALT): maintains the Tier 2 access projection asynchronously from the Authority's authorization journal,
/// outside every security-command and content transaction. One event per transaction: the event must be the checkpoint's
/// successor and chain to its digest; the mirror edges and the affected records' rows change, and the checkpoint is
/// compared-and-set, in that one transaction. Any integrity failure quarantines Tier 2 for the organization only (journaled,
/// alerted, never repaired silently); only <see cref="RebuildAsync"/> leaves quarantine. Nothing here authorizes access: no
/// search path reads these rows in IX-5a.
/// </summary>
public sealed class SearchAccessProjectionService(
    IAuthorityStore store,
    ProjectionIntegrityVerifier integrity,
    SearchAccessOptions options,
    ISecurityAlertSink alerts,
    IFaultInjector faultInjector,
    TimeProvider clock)
{
    public const string QuarantineEventKind = "SearchAccessProjectionQuarantined";

    public const string RebuildEventKind = "SearchAccessProjectionRebuilt";

    public async Task<SearchAccessStatus> StatusAsync(Guid organizationId, CancellationToken cancellationToken)
    {
        await using var scope = await store.BeginReadOnlySnapshotScopeAsync(cancellationToken).ConfigureAwait(false);
        var state = await scope.ReadOrganizationAsync(organizationId, cancellationToken).ConfigureAwait(false)
                    ?? throw new AuthorityException(AuthorityErrorCodes.NotFound, 404, false);
        await scope.SearchAccess.BindOrganizationAsync(organizationId, cancellationToken).ConfigureAwait(false);
        var checkpoint = await scope.SearchAccess.FindCheckpointAsync(organizationId, cancellationToken).ConfigureAwait(false);
        return Status(state.OrganizationId, await ReadinessInputsAsync(scope.SearchAccess, state, cancellationToken).ConfigureAwait(false), checkpoint);
    }

    /// <summary>The live authoritative state, read in the caller's snapshot.</summary>
    internal static async Task<SearchAccessReadinessInputs> ReadinessInputsAsync(ISearchAccessStore access, Domain.Features.Organizations.OrganizationAuthorizationState state, CancellationToken cancellationToken)
    {
        var coverages = await access.GetSearchableCoveragesAsync(state.OrganizationId, cancellationToken).ConfigureAwait(false);
        var tier1 = await access.GetTier1ProjectionStatusAsync(state.OrganizationId, cancellationToken).ConfigureAwait(false);
        return new SearchAccessReadinessInputs(state.AuthorizationRevision, state.LatestSecurityJournalDigest, state.ContentIntegrityRevision, state.LatestContentJournalDigest, SearchAccessValueDerivation.CoverageDigest(coverages), tier1);
    }

    internal SearchAccessStatus Status(Guid organizationId, SearchAccessReadinessInputs inputs, SearchAccessCheckpoint? checkpoint)
    {
        var coverageCurrent = checkpoint is not null && SearchAccessValueDerivation.DigestsEqual(checkpoint.ConsumedCoverageDigest, inputs.CoverageDigest);
        if (options.Mode == SearchAccessMode.Off)
        {
            return new SearchAccessStatus(organizationId, SearchAccessStatusNames.Disabled, checkpoint?.ConsumedAuthorizationRevision ?? 0, inputs.AuthorizationRevision, checkpoint?.CheckpointRevision ?? 0, checkpoint?.AccessProjectionProtocolVersion ?? 0, checkpoint?.QuarantineReasonCode,
                checkpoint?.ConsumedContentIntegrityRevision ?? 0, inputs.ContentIntegrityRevision, coverageCurrent, inputs.Tier1StatusName);
        }

        if (checkpoint is null)
        {
            return new SearchAccessStatus(organizationId, SearchAccessStatusNames.NotBuilt, 0, inputs.AuthorizationRevision, 0, 0, null, 0, inputs.ContentIntegrityRevision, false, inputs.Tier1StatusName);
        }

        var status = checkpoint.AccessProjectionStateName == SearchAccessProjectionStates.Quarantined ? SearchAccessStatusNames.Quarantined
            : IsReady(checkpoint, inputs) ? SearchAccessStatusNames.Ready
            : SearchAccessStatusNames.Building;
        return new SearchAccessStatus(organizationId, status, checkpoint.ConsumedAuthorizationRevision, inputs.AuthorizationRevision, checkpoint.CheckpointRevision, checkpoint.AccessProjectionProtocolVersion, checkpoint.QuarantineReasonCode,
            checkpoint.ConsumedContentIntegrityRevision, inputs.ContentIntegrityRevision, coverageCurrent, inputs.Tier1StatusName);
    }

    /// <summary>
    /// A-3 (IX-5b): ready only when the checkpoint equals the live state exactly: the authorization revision and journal head digest,
    /// the content-integrity revision and content journal head digest, the Complete Public/Internal coverage set, at the current
    /// protocol, with Tier 1 servable (Active) or absent. Any new command, commit or coverage change closes readiness at once,
    /// before the maintainer runs: a revocation is never servable from the old rows.
    /// </summary>
    public static bool IsReady(SearchAccessCheckpoint checkpoint, SearchAccessReadinessInputs inputs)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);
        ArgumentNullException.ThrowIfNull(inputs);
        return checkpoint.AccessProjectionStateName == SearchAccessProjectionStates.Maintaining
               && checkpoint.AccessProjectionProtocolVersion == SearchAccessDerivation.ProtocolVersion
               && AuthorizationAtHead(checkpoint, inputs.AuthorizationRevision, inputs.AuthorizationDigest)
               && ContentAtHead(checkpoint, inputs.ContentIntegrityRevision, inputs.ContentDigest)
               && SearchAccessValueDerivation.DigestsEqual(checkpoint.ConsumedCoverageDigest, inputs.CoverageDigest)
               && inputs.Tier1StatusName is null or Domain.Features.SearchIndex.SearchProjectionStatuses.Active;
    }

    public static bool AuthorizationAtHead(SearchAccessCheckpoint checkpoint, long currentRevision, byte[]? currentDigest) =>
        checkpoint.ConsumedAuthorizationRevision == currentRevision
        && (currentRevision == 0 ? checkpoint.ConsumedJournalDigest is null : checkpoint.ConsumedJournalDigest is { Length: 32 } d && currentDigest is not null && d.AsSpan().SequenceEqual(currentDigest));

    public static bool ContentAtHead(SearchAccessCheckpoint checkpoint, long currentRevision, byte[]? currentDigest) =>
        checkpoint.ConsumedContentIntegrityRevision == currentRevision
        && (currentRevision == 0 ? checkpoint.ConsumedContentJournalDigest is null : checkpoint.ConsumedContentJournalDigest is { Length: 32 } d && currentDigest is not null && d.AsSpan().SequenceEqual(currentDigest));

    /// <summary>
    /// C-149 (R-55): one read-only snapshot transaction, without the checkpoint row lock or any write. <see cref="SearchAccessProbeOutcome.Settled"/>
    /// only when the steps would all answer Idle: the checkpoint is Maintaining at the current protocol, at the authorization head
    /// (revision and digest), at the content head (revision and digest), and its coverage digest equals the live Complete
    /// Public/Internal coverage set. <see cref="SearchAccessProbeOutcome.Quarantined"/> when quarantined (only a rebuild leaves it).
    /// Anything else (including a digest that differs at an equal revision, a checkpoint ahead, a missing checkpoint or an older
    /// protocol) is <see cref="SearchAccessProbeOutcome.WorkPending"/>: the probe never decides an integrity failure; the steps verify
    /// it under the row lock and quarantine. Nothing is changed here, and readiness is not derived from it.
    /// </summary>
    public async Task<SearchAccessProbeOutcome> ProbeAsync(Guid organizationId, CancellationToken cancellationToken)
    {
        RequireEnabled();
        await using var scope = await store.BeginReadOnlySnapshotScopeAsync(cancellationToken).ConfigureAwait(false);
        var access = scope.SearchAccess;
        await access.BindOrganizationAsync(organizationId, cancellationToken).ConfigureAwait(false);
        var checkpoint = await access.FindCheckpointAsync(organizationId, cancellationToken).ConfigureAwait(false);
        if (checkpoint is null)
        {
            return SearchAccessProbeOutcome.WorkPending;
        }

        if (checkpoint.AccessProjectionStateName == SearchAccessProjectionStates.Quarantined)
        {
            return SearchAccessProbeOutcome.Quarantined;
        }

        if (checkpoint.AccessProjectionStateName != SearchAccessProjectionStates.Maintaining || checkpoint.AccessProjectionProtocolVersion != SearchAccessDerivation.ProtocolVersion)
        {
            return SearchAccessProbeOutcome.WorkPending;
        }

        var state = await scope.ReadOrganizationAsync(organizationId, cancellationToken).ConfigureAwait(false);
        if (state is null || !AuthorizationAtHead(checkpoint, state.AuthorizationRevision, state.LatestSecurityJournalDigest) || !ContentAtHead(checkpoint, state.ContentIntegrityRevision, state.LatestContentJournalDigest))
        {
            return SearchAccessProbeOutcome.WorkPending;
        }

        var coverage = SearchAccessValueDerivation.CoverageDigest(await access.GetSearchableCoveragesAsync(organizationId, cancellationToken).ConfigureAwait(false));
        return SearchAccessValueDerivation.DigestsEqual(coverage, checkpoint.ConsumedCoverageDigest) ? SearchAccessProbeOutcome.Settled : SearchAccessProbeOutcome.WorkPending;
    }

    /// <summary>
    /// C-149 (R-55): one organization's step of a maintenance pass. A probe item that finds nothing to do settles without any write
    /// transaction; otherwise the journals are consumed (<see cref="CatchUpAsync"/>, unchanged) and the organization is settled at the
    /// pass's markers only if the outcome leaves nothing to do. A missing checkpoint is built and an older protocol rebuilt, as
    /// before. Any exception leaves the organization due again (and is rethrown to the loop).
    /// </summary>
    public async Task<SearchAccessMaintenanceResult> MaintainAsync(SearchAccessMaintenanceSchedule schedule, SearchAccessMaintenanceItem item, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(schedule);
        ArgumentNullException.ThrowIfNull(item);
        var organization = item.Marker.OrganizationId;
        try
        {
            if (item.Action == SearchAccessMaintenanceAction.Probe)
            {
                var probe = await ProbeAsync(organization, cancellationToken).ConfigureAwait(false);
                if (probe != SearchAccessProbeOutcome.WorkPending)
                {
                    schedule.Settle(item.Marker);
                    return new(probe == SearchAccessProbeOutcome.Quarantined ? SearchAccessStepOutcome.Quarantined : SearchAccessStepOutcome.Idle, 0, SettledByProbe: true);
                }
            }

            var (outcome, advanced) = await CatchUpAsync(organization, options.EventsPerPass, cancellationToken).ConfigureAwait(false);
            if (SearchAccessMaintenanceSchedule.Settles(outcome))
            {
                schedule.Settle(item.Marker);
            }
            else
            {
                schedule.Unsettle(organization);
            }

            if (outcome == SearchAccessStepOutcome.NotBuilt)
            {
                await RebuildAsync(organization, "INITIAL_BUILD", Guid.NewGuid(), cancellationToken).ConfigureAwait(false);
            }
            else if (outcome == SearchAccessStepOutcome.UpgradeRequired)
            {
                // Rows of an older derivation protocol (not an integrity failure): rebuilt once from the canonical state. A
                // quarantined projection is never rebuilt here.
                await RebuildAsync(organization, "PROTOCOL_UPGRADE", Guid.NewGuid(), cancellationToken).ConfigureAwait(false);
            }

            return new(outcome, advanced, SettledByProbe: false);
        }
        catch
        {
            schedule.Unsettle(organization);
            throw;
        }
    }

    /// <summary>
    /// Consumes up to <paramref name="maximumEvents"/> journal events, one transaction each: the authorization stream, then the
    /// content stream, then the coverage set. Stops at the first non-advance that is not Idle.
    /// </summary>
    public async Task<(SearchAccessStepOutcome Outcome, int Advanced)> CatchUpAsync(Guid organizationId, int maximumEvents, CancellationToken cancellationToken)
    {
        var advanced = 0;
        foreach (var step in new Func<Guid, CancellationToken, Task<SearchAccessStepOutcome>>[] { StepAsync, StepContentAsync, CoverageStepAsync })
        {
            while (true)
            {
                if (advanced >= maximumEvents)
                {
                    return (SearchAccessStepOutcome.Advanced, advanced);
                }

                var outcome = await step(organizationId, cancellationToken).ConfigureAwait(false);
                if (outcome == SearchAccessStepOutcome.Advanced)
                {
                    advanced++;
                    continue;
                }

                if (outcome != SearchAccessStepOutcome.Idle)
                {
                    return (outcome, advanced);
                }

                break;
            }
        }

        return (SearchAccessStepOutcome.Idle, advanced);
    }

    /// <summary>Applies the checkpoint's successor event, if there is one.</summary>
    public async Task<SearchAccessStepOutcome> StepAsync(Guid organizationId, CancellationToken cancellationToken)
    {
        RequireEnabled();
        string? failure = null;
        string failureDetail = string.Empty;
        long observedCheckpointRevision;
        await using (var scope = await store.BeginScopeAsync(cancellationToken).ConfigureAwait(false))
        {
            var access = scope.SearchAccess;
            await access.BindOrganizationAsync(organizationId, cancellationToken).ConfigureAwait(false);
            var checkpoint = await access.FindCheckpointForUpdateAsync(organizationId, cancellationToken).ConfigureAwait(false);
            if (checkpoint is null)
            {
                return SearchAccessStepOutcome.NotBuilt;
            }

            if (checkpoint.AccessProjectionStateName == SearchAccessProjectionStates.Quarantined)
            {
                return SearchAccessStepOutcome.Quarantined;
            }

            if (checkpoint.AccessProjectionProtocolVersion > SearchAccessDerivation.ProtocolVersion)
            {
                return SearchAccessStepOutcome.Fenced; // rows of a newer derivation: an older binary never writes (§12.4)
            }

            observedCheckpointRevision = checkpoint.CheckpointRevision;
            var state = await scope.ReadOrganizationAsync(organizationId, cancellationToken).ConfigureAwait(false)
                        ?? throw new AuthorityException(AuthorityErrorCodes.NotFound, 404, false);
            try
            {
                if (checkpoint.ConsumedAuthorizationRevision > state.AuthorizationRevision)
                {
                    throw new SearchAccessIntegrityException(SearchAccessReasonCodes.CheckpointAhead, "the checkpoint is ahead of the organization (restore or rollback)");
                }

                if (checkpoint.AccessProjectionProtocolVersion < SearchAccessDerivation.ProtocolVersion)
                {
                    return SearchAccessStepOutcome.UpgradeRequired; // rows of an older derivation: only a rebuild upgrades them
                }

                if (checkpoint.ConsumedAuthorizationRevision == state.AuthorizationRevision)
                {
                    if (!AuthorizationAtHead(checkpoint, state.AuthorizationRevision, state.LatestSecurityJournalDigest))
                    {
                        throw new SearchAccessIntegrityException(SearchAccessReasonCodes.CheckpointDigestMismatch, "the checkpoint digest differs from the journal head at the same revision");
                    }

                    return SearchAccessStepOutcome.Idle;
                }

                var target = checkpoint.ConsumedAuthorizationRevision + 1;
                var entry = await scope.FindJournalEntryAsync(organizationId, JournalStreamNames.Authorization, target, cancellationToken).ConfigureAwait(false);
                var verified = SearchAccessJournalVerifier.Verify(entry, organizationId, target, checkpoint.ConsumedJournalDigest);
                await ApplyEventAsync(access, organizationId, verified, state.ContentIntegrityRevision, cancellationToken).ConfigureAwait(false);
                var next = SearchAccessCheckpointValues.Of(checkpoint) with { ConsumedAuthorizationRevision = target, ConsumedJournalDigest = verified.Digest };
                if (!await access.TryUpdateCheckpointAsync(organizationId, checkpoint.CheckpointRevision, next, clock.GetUtcNow(), cancellationToken).ConfigureAwait(false))
                {
                    return SearchAccessStepOutcome.Contended; // another writer advanced it: this transaction rolls back
                }

                faultInjector.CrashIfArmed(FaultNames.CrashBeforeAccessProjectionCommit);
                await scope.CommitAsync(cancellationToken).ConfigureAwait(false);
                return SearchAccessStepOutcome.Advanced;
            }
            catch (SearchAccessIntegrityException ex)
            {
                failure = ex.ReasonCode;
                failureDetail = ex.Message;
            }
        }

        await QuarantineAsync(organizationId, observedCheckpointRevision, failure!, failureDetail, cancellationToken).ConfigureAwait(false);
        return SearchAccessStepOutcome.Quarantined;
    }

    /// <summary>
    /// IX-5b: applies the content checkpoint's successor event. The event names a record; its value rows are re-derived from the
    /// accepted head's searchable postings and the record's current access rows, and the content checkpoint is compared-and-set,
    /// in one transaction. Values are never read from the event (it carries none).
    /// </summary>
    public async Task<SearchAccessStepOutcome> StepContentAsync(Guid organizationId, CancellationToken cancellationToken)
    {
        RequireEnabled();
        string? failure = null;
        string failureDetail = string.Empty;
        long observedCheckpointRevision;
        await using (var scope = await store.BeginScopeAsync(cancellationToken).ConfigureAwait(false))
        {
            var access = scope.SearchAccess;
            await access.BindOrganizationAsync(organizationId, cancellationToken).ConfigureAwait(false);
            var checkpoint = await access.FindCheckpointForUpdateAsync(organizationId, cancellationToken).ConfigureAwait(false);
            if (checkpoint is null)
            {
                return SearchAccessStepOutcome.NotBuilt;
            }

            if (checkpoint.AccessProjectionStateName == SearchAccessProjectionStates.Quarantined)
            {
                return SearchAccessStepOutcome.Quarantined;
            }

            if (checkpoint.AccessProjectionProtocolVersion != SearchAccessDerivation.ProtocolVersion)
            {
                return checkpoint.AccessProjectionProtocolVersion > SearchAccessDerivation.ProtocolVersion ? SearchAccessStepOutcome.Fenced : SearchAccessStepOutcome.UpgradeRequired;
            }

            observedCheckpointRevision = checkpoint.CheckpointRevision;
            var state = await scope.ReadOrganizationAsync(organizationId, cancellationToken).ConfigureAwait(false)
                        ?? throw new AuthorityException(AuthorityErrorCodes.NotFound, 404, false);
            try
            {
                if (checkpoint.ConsumedContentIntegrityRevision > state.ContentIntegrityRevision)
                {
                    throw new SearchAccessIntegrityException(SearchAccessReasonCodes.CheckpointAhead, "the content checkpoint is ahead of the organization (restore or rollback)");
                }

                if (checkpoint.ConsumedContentIntegrityRevision == state.ContentIntegrityRevision)
                {
                    if (!ContentAtHead(checkpoint, state.ContentIntegrityRevision, state.LatestContentJournalDigest))
                    {
                        throw new SearchAccessIntegrityException(SearchAccessReasonCodes.CheckpointDigestMismatch, "the content checkpoint digest differs from the content journal head at the same revision");
                    }

                    return SearchAccessStepOutcome.Idle;
                }

                var target = checkpoint.ConsumedContentIntegrityRevision + 1;
                var entry = await scope.FindJournalEntryAsync(organizationId, JournalStreamNames.Content, target, cancellationToken).ConfigureAwait(false);
                var verified = SearchAccessJournalVerifier.VerifyContent(entry, organizationId, target, checkpoint.ConsumedContentJournalDigest);
                await RederiveValuesAsync(access, organizationId, [verified.FormRecordId], null, target, cancellationToken).ConfigureAwait(false);
                var next = SearchAccessCheckpointValues.Of(checkpoint) with { ConsumedContentIntegrityRevision = target, ConsumedContentJournalDigest = verified.Digest };
                if (!await access.TryUpdateCheckpointAsync(organizationId, checkpoint.CheckpointRevision, next, clock.GetUtcNow(), cancellationToken).ConfigureAwait(false))
                {
                    return SearchAccessStepOutcome.Contended;
                }

                faultInjector.CrashIfArmed(FaultNames.CrashBeforeAccessProjectionCommit);
                await scope.CommitAsync(cancellationToken).ConfigureAwait(false);
                return SearchAccessStepOutcome.Advanced;
            }
            catch (SearchAccessIntegrityException ex)
            {
                failure = ex.ReasonCode;
                failureDetail = ex.Message;
            }
        }

        await QuarantineAsync(organizationId, observedCheckpointRevision, failure!, failureDetail, cancellationToken).ConfigureAwait(false);
        return SearchAccessStepOutcome.Quarantined;
    }

    /// <summary>
    /// IX-5b: a coverage change (a field becoming searchable, withdrawn, or a Tier 1 rebuild returning it to Building) changes which
    /// values are searchable without a content event. When the Complete Public/Internal coverage set differs from the checkpoint's,
    /// every value row of the organization is re-derived and the coverage digest compared-and-set, in one transaction.
    /// </summary>
    public async Task<SearchAccessStepOutcome> CoverageStepAsync(Guid organizationId, CancellationToken cancellationToken)
    {
        RequireEnabled();
        await using var scope = await store.BeginScopeAsync(cancellationToken).ConfigureAwait(false);
        var access = scope.SearchAccess;
        await access.BindOrganizationAsync(organizationId, cancellationToken).ConfigureAwait(false);
        var checkpoint = await access.FindCheckpointForUpdateAsync(organizationId, cancellationToken).ConfigureAwait(false);
        if (checkpoint is null)
        {
            return SearchAccessStepOutcome.NotBuilt;
        }

        if (checkpoint.AccessProjectionStateName == SearchAccessProjectionStates.Quarantined)
        {
            return SearchAccessStepOutcome.Quarantined;
        }

        if (checkpoint.AccessProjectionProtocolVersion != SearchAccessDerivation.ProtocolVersion)
        {
            return checkpoint.AccessProjectionProtocolVersion > SearchAccessDerivation.ProtocolVersion ? SearchAccessStepOutcome.Fenced : SearchAccessStepOutcome.UpgradeRequired;
        }

        var digest = SearchAccessValueDerivation.CoverageDigest(await access.GetSearchableCoveragesAsync(organizationId, cancellationToken).ConfigureAwait(false));
        if (SearchAccessValueDerivation.DigestsEqual(digest, checkpoint.ConsumedCoverageDigest))
        {
            return SearchAccessStepOutcome.Idle;
        }

        var state = await scope.ReadOrganizationAsync(organizationId, cancellationToken).ConfigureAwait(false)
                    ?? throw new AuthorityException(AuthorityErrorCodes.NotFound, 404, false);
        await RederiveValuesAsync(access, organizationId, null, null, state.ContentIntegrityRevision, cancellationToken).ConfigureAwait(false);
        if (!await access.TryUpdateCheckpointAsync(organizationId, checkpoint.CheckpointRevision, SearchAccessCheckpointValues.Of(checkpoint) with { ConsumedCoverageDigest = digest }, clock.GetUtcNow(), cancellationToken).ConfigureAwait(false))
        {
            return SearchAccessStepOutcome.Contended;
        }

        await scope.CommitAsync(cancellationToken).ConfigureAwait(false);
        return SearchAccessStepOutcome.Advanced;
    }

    /// <summary>
    /// Re-derives the value rows of <paramref name="records"/> (null: the whole organization) from the accepted heads' searchable
    /// postings and the access rows: <paramref name="derived"/> when the caller just derived them, else the stored rows.
    /// </summary>
    private static async Task RederiveValuesAsync(ISearchAccessStore access, Guid organizationId, IReadOnlyCollection<Guid>? records, DerivedAccessRows? derived, long contentRevision, CancellationToken cancellationToken)
    {
        var recordGrants = derived?.RecordGrants ?? await access.GetRecordGrantsAsync(organizationId, records, cancellationToken).ConfigureAwait(false);
        var itemGrants = derived?.FieldItemGrants ?? await access.GetFieldItemGrantsAsync(organizationId, records, cancellationToken).ConfigureAwait(false);
        var postings = await access.GetSearchablePostingsAsync(organizationId, records, cancellationToken).ConfigureAwait(false);
        await access.DeleteValuePostingsOfRecordsAsync(organizationId, records, cancellationToken).ConfigureAwait(false);
        access.AddValuePostings(SearchAccessValueDerivation.Derive(organizationId, recordGrants, itemGrants, postings, contentRevision));
    }

    /// <summary>
    /// Rebuilds from the Authority's canonical edges at the organization's current revision, under the organization lock (a pinned,
    /// consistent read), and sets the checkpoint to that revision and its journal digest. The only way out of quarantine; journaled.
    /// </summary>
    public async Task<SearchAccessStatus> RebuildAsync(Guid organizationId, string rebuildReasonCode, Guid correlationId, CancellationToken cancellationToken)
    {
        RequireEnabled();
        ArgumentException.ThrowIfNullOrWhiteSpace(rebuildReasonCode);
        await using var scope = await store.BeginScopeAsync(cancellationToken).ConfigureAwait(false);
        var state = await scope.LockOrganizationAsync(organizationId, cancellationToken).ConfigureAwait(false)
                    ?? throw new AuthorityException(AuthorityErrorCodes.NotFound, 404, false);
        var access = scope.SearchAccess;
        await access.BindOrganizationAsync(organizationId, cancellationToken).ConfigureAwait(false);
        var checkpoint = await access.FindCheckpointForUpdateAsync(organizationId, cancellationToken).ConfigureAwait(false);
        var canonical = await CanonicalAccessEdgesAsync(scope, organizationId, cancellationToken).ConfigureAwait(false);
        var revision = state.AuthorizationRevision;
        await access.PurgeOrganizationAsync(organizationId, cancellationToken).ConfigureAwait(false);
        foreach (var edge in canonical.Edges)
        {
            access.AddEdge(Row(organizationId, edge, revision));
        }

        var derived = SearchAccessDerivation.Derive(organizationId, canonical, canonical.Edges.Where(e => e.ResourceType == "form_record").Select(e => e.ResourceId), revision, options.MaximumParentDepth);
        access.AddRecordGrants(derived.RecordGrants);
        access.AddFieldItemGrants(derived.FieldItemGrants);
        var postings = await access.GetSearchablePostingsAsync(organizationId, null, cancellationToken).ConfigureAwait(false);
        var values = SearchAccessValueDerivation.Derive(organizationId, derived.RecordGrants, derived.FieldItemGrants, postings, state.ContentIntegrityRevision);
        access.AddValuePostings(values);
        var coverageDigest = SearchAccessValueDerivation.CoverageDigest(await access.GetSearchableCoveragesAsync(organizationId, cancellationToken).ConfigureAwait(false));
        var contentDigest = state.ContentIntegrityRevision == 0 ? null : state.LatestContentJournalDigest;
        var digest = revision == 0 ? null : state.LatestSecurityJournalDigest;
        var rebuilt = new SearchAccessCheckpointValues(revision, digest, state.ContentIntegrityRevision, contentDigest, coverageDigest, SearchAccessProjectionStates.Maintaining, null, SearchAccessDerivation.ProtocolVersion);
        if (checkpoint is null)
        {
            await access.InsertCheckpointAsync(new SearchAccessCheckpoint
            {
                SearchAccessCheckpointId = Guid.CreateVersion7(),
                OrganizationId = organizationId,
                AccessProjectionStateName = SearchAccessProjectionStates.Maintaining,
                ConsumedAuthorizationRevision = revision,
                ConsumedJournalDigest = digest,
                ConsumedContentIntegrityRevision = state.ContentIntegrityRevision,
                ConsumedContentJournalDigest = contentDigest,
                ConsumedCoverageDigest = coverageDigest,
                CheckpointRevision = 1,
                AccessProjectionProtocolVersion = SearchAccessDerivation.ProtocolVersion,
                CheckpointModifiedOn = clock.GetUtcNow(),
            }, cancellationToken).ConfigureAwait(false);
        }
        else if (!await access.TryUpdateCheckpointAsync(organizationId, checkpoint.CheckpointRevision, rebuilt, clock.GetUtcNow(), cancellationToken).ConfigureAwait(false))
        {
            throw new AuthorityException(AuthorityErrorCodes.ContentRevisionConflict, 409, true, "the access projection checkpoint changed during the rebuild");
        }

        AuditJournal.Append(scope, state, RebuildEventKind, new
        {
            organization_id = organizationId,
            authorization_revision = revision,
            previous_state = checkpoint?.AccessProjectionStateName,
            previous_reason_code = checkpoint?.QuarantineReasonCode,
            rebuild_reason_code = rebuildReasonCode,
            mirror_edges = canonical.Edges.Count,
            record_grants = derived.RecordGrants.Count,
            field_item_grants = derived.FieldItemGrants.Count,
            content_integrity_revision = state.ContentIntegrityRevision,
            value_postings = values.Count,
            searchable_postings = postings.Count,
            protocol_version = SearchAccessDerivation.ProtocolVersion,
            correlation_id = correlationId,
        }, clock);
        await scope.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new SearchAccessStatus(organizationId, SearchAccessStatusNames.Ready, revision, revision, (checkpoint?.CheckpointRevision ?? 0) + 1, SearchAccessDerivation.ProtocolVersion, null,
            state.ContentIntegrityRevision, state.ContentIntegrityRevision, true);
    }

    /// <summary>
    /// Full M1 comparison in one read-only snapshot: the mirror must equal the canonical access edges and the stored rows must equal
    /// the derivation of the canonical edges. Then a sampled SpiceDB comparison at a pinned S1 snapshot, team- and user-level. A
    /// difference quarantines Tier 2 (never repairs). Only runs when the checkpoint is exactly at the organization's revision.
    /// </summary>
    public async Task<SearchAccessVerification> VerifyAsync(Guid organizationId, Guid correlationId, CancellationToken cancellationToken)
    {
        RequireEnabled();
        string? finding = null;
        string detail = string.Empty;
        SearchAccessVerification result;
        long checkpointRevision;
        AccessEdgeSet? canonicalAtRevision = null;
        List<SearchAccessRecordGrant> storedRecordGrants = [];
        List<SearchAccessFieldItemGrant> storedItemGrants = [];
        long verifiedRevision;
        byte[]? verifiedDigest;
        long verifiedContentRevision;
        await using (var scope = await store.BeginReadOnlySnapshotScopeAsync(cancellationToken).ConfigureAwait(false))
        {
            var state = await scope.ReadOrganizationAsync(organizationId, cancellationToken).ConfigureAwait(false)
                        ?? throw new AuthorityException(AuthorityErrorCodes.NotFound, 404, false);
            var access = scope.SearchAccess;
            await access.BindOrganizationAsync(organizationId, cancellationToken).ConfigureAwait(false);
            var checkpoint = await access.FindCheckpointAsync(organizationId, cancellationToken).ConfigureAwait(false);
            var inputs = await ReadinessInputsAsync(access, state, cancellationToken).ConfigureAwait(false);
            if (checkpoint is null || checkpoint.AccessProjectionStateName == SearchAccessProjectionStates.Quarantined || !IsReady(checkpoint, inputs))
            {
                var code = checkpoint is null ? "NOT_BUILT" : checkpoint.AccessProjectionStateName == SearchAccessProjectionStates.Quarantined ? "QUARANTINED" : "NOT_AT_HEAD";
                return new SearchAccessVerification(code, state.AuthorizationRevision, 0, 0, 0, 0, 0, 0, null);
            }

            checkpointRevision = checkpoint.CheckpointRevision;
            verifiedRevision = state.AuthorizationRevision;
            verifiedDigest = state.LatestSecurityJournalDigest;
            verifiedContentRevision = state.ContentIntegrityRevision;
            var canonical = await CanonicalAccessEdgesAsync(scope, organizationId, cancellationToken).ConfigureAwait(false);
            var mirror = (await access.GetAllEdgesAsync(organizationId, cancellationToken).ConfigureAwait(false)).Select(AccessEdge.Of).ToHashSet();
            var recordGrants = await access.GetRecordGrantsAsync(organizationId, null, cancellationToken).ConfigureAwait(false);
            var itemGrants = await access.GetFieldItemGrantsAsync(organizationId, null, cancellationToken).ConfigureAwait(false);
            var records = canonical.Edges.Where(e => e.ResourceType == "form_record").Select(e => e.ResourceId).Distinct().ToList();
            var expected = SearchAccessDerivation.Derive(organizationId, canonical, records, state.AuthorizationRevision, options.MaximumParentDepth);
            var values = await access.GetValuePostingsAsync(organizationId, null, cancellationToken).ConfigureAwait(false);
            var searchable = await access.GetSearchablePostingsAsync(organizationId, null, cancellationToken).ConfigureAwait(false);
            var expectedValues = SearchAccessValueDerivation.Derive(organizationId, expected.RecordGrants, expected.FieldItemGrants, searchable, state.ContentIntegrityRevision);
            var checks = 0;
            var sampled = 0;
            if (!mirror.SetEquals(canonical.Edges))
            {
                finding = SearchAccessReasonCodes.MirrorDiverged;
                detail = $"mirror {mirror.Count} edges, canonical {canonical.Edges.Count}, missing {canonical.Edges.Count(e => !mirror.Contains(e))}, extra {mirror.Count(e => !canonical.Edges.Contains(e))}";
            }
            else if (!SameMultiset(recordGrants.Select(SearchAccessDerivation.Key), expected.RecordGrants.Select(SearchAccessDerivation.Key))
                     || !SameMultiset(itemGrants.Select(SearchAccessDerivation.Key), expected.FieldItemGrants.Select(SearchAccessDerivation.Key)))
            {
                finding = SearchAccessReasonCodes.RowsDiverged;
                detail = $"record rows {recordGrants.Count} expected {expected.RecordGrants.Count}; item rows {itemGrants.Count} expected {expected.FieldItemGrants.Count}";
            }
            else if (!SameMultiset(values.Select(SearchAccessValueDerivation.Key), expectedValues.Select(SearchAccessValueDerivation.Key)))
            {
                // IX-5b: the value rows must be exactly the accepted searchable values, each for exactly the principals that read it.
                finding = SearchAccessReasonCodes.ValuesDiverged;
                detail = $"value rows {values.Count} expected {expectedValues.Count}";
            }
            else
            {
                canonicalAtRevision = canonical;
                storedRecordGrants = recordGrants;
                storedItemGrants = itemGrants;
            }

            result = new SearchAccessVerification(finding ?? "CLEAN", state.AuthorizationRevision, mirror.Count, canonical.Edges.Count, recordGrants.Count, itemGrants.Count, sampled, checks, finding is null ? null : detail,
                values.Count, searchable.Count, state.ContentIntegrityRevision);
        }

        if (finding is null && canonicalAtRevision is not null)
        {
            // The SpiceDB comparison runs in an ordinary read-committed scope (the S1 decision pattern), so a concurrent command is
            // seen as "moved" and never mistaken for a marker mismatch. The rows compared above belong to exactly this revision.
            await using var scope = await store.BeginScopeAsync(cancellationToken).ConfigureAwait(false);
            var state = await scope.ReadOrganizationAsync(organizationId, cancellationToken).ConfigureAwait(false)
                        ?? throw new AuthorityException(AuthorityErrorCodes.NotFound, 404, false);
            if (state.AuthorizationRevision != verifiedRevision || state.ContentIntegrityRevision != verifiedContentRevision || !(verifiedDigest ?? []).AsSpan().SequenceEqual(state.LatestSecurityJournalDigest ?? []))
            {
                return result with { OutcomeCode = "MOVED" };
            }

            (int, int, string?, string) compared;
            try
            {
                compared = await CompareWithSpiceDbAsync(scope, state, canonicalAtRevision, storedRecordGrants, storedItemGrants, correlationId, cancellationToken).ConfigureAwait(false);
            }
            catch (AuthorityException ex) when (ex.ErrorCode == AuthorityErrorCodes.AuthorityNotReady)
            {
                // SpiceDB not (yet) provably at this revision (S1 gate): nothing is compared and nothing is quarantined; retry later.
                return result with { OutcomeCode = "SPICEDB_NOT_READY" };
            }

            var (checks, sampled, spiceFinding, spiceDetail) = compared;
            finding = spiceFinding;
            detail = spiceDetail;
            result = result with { OutcomeCode = finding ?? "CLEAN", SampledRecords = sampled, SpiceDbChecks = checks, FindingDetail = finding is null ? null : detail };
        }

        if (finding is not null and not "MOVED")
        {
            await QuarantineAsync(organizationId, checkpointRevision, finding, detail, cancellationToken).ConfigureAwait(false);
        }

        return result;
    }

    /// <summary>
    /// Journal completeness check (C-147 §3): replays the authorization stream from revision 1 to the organization's revision in
    /// memory, verifying every event and its chain, and compares the replayed access edges with the canonical edges. Read-only.
    /// </summary>
    public async Task<SearchAccessReplayCheck> VerifyJournalReplayAsync(Guid organizationId, CancellationToken cancellationToken)
    {
        await using var scope = await store.BeginReadOnlySnapshotScopeAsync(cancellationToken).ConfigureAwait(false);
        var state = await scope.ReadOrganizationAsync(organizationId, cancellationToken).ConfigureAwait(false)
                    ?? throw new AuthorityException(AuthorityErrorCodes.NotFound, 404, false);
        var replayed = new AccessEdgeSet([]);
        byte[]? previous = null;
        long revision = 0;
        try
        {
            for (revision = 1; revision <= state.AuthorizationRevision; revision++)
            {
                var entry = await scope.FindJournalEntryAsync(organizationId, JournalStreamNames.Authorization, revision, cancellationToken).ConfigureAwait(false);
                var verified = SearchAccessJournalVerifier.Verify(entry, organizationId, revision, previous);
                foreach (var change in verified.Delta.Where(d => d.Edge is not null))
                {
                    if (change.IsTouch)
                    {
                        replayed.Add(change.Edge!);
                    }
                    else if (!replayed.Remove(change.Edge!))
                    {
                        throw new SearchAccessIntegrityException(SearchAccessReasonCodes.MirrorDeleteAbsent, "a delete names an edge the replay does not hold");
                    }
                }

                previous = verified.Digest;
            }

            if (state.AuthorizationRevision > 0 && (state.LatestSecurityJournalDigest is null || !previous!.AsSpan().SequenceEqual(state.LatestSecurityJournalDigest)))
            {
                throw new SearchAccessIntegrityException(SearchAccessReasonCodes.CheckpointDigestMismatch, "the replayed chain does not end at the organization's journal head");
            }
        }
        catch (SearchAccessIntegrityException ex)
        {
            return new SearchAccessReplayCheck(ex.ReasonCode, revision - 1, state.AuthorizationRevision, replayed.Edges.Count, 0, 0, 0);
        }

        // IX-5b: the content stream must also be complete and chained to its head (it names every record whose values change).
        byte[]? previousContent = null;
        long content = 0;
        try
        {
            for (content = 1; content <= state.ContentIntegrityRevision; content++)
            {
                var entry = await scope.FindJournalEntryAsync(organizationId, JournalStreamNames.Content, content, cancellationToken).ConfigureAwait(false);
                previousContent = SearchAccessJournalVerifier.VerifyContent(entry, organizationId, content, previousContent).Digest;
            }

            if (state.ContentIntegrityRevision > 0 && (state.LatestContentJournalDigest is null || !previousContent!.AsSpan().SequenceEqual(state.LatestContentJournalDigest)))
            {
                throw new SearchAccessIntegrityException(SearchAccessReasonCodes.CheckpointDigestMismatch, "the content chain does not end at the organization's content journal head");
            }
        }
        catch (SearchAccessIntegrityException ex)
        {
            return new SearchAccessReplayCheck(ex.ReasonCode, state.AuthorizationRevision, state.AuthorizationRevision, replayed.Edges.Count, 0, 0, 0, content - 1);
        }

        var canonical = await CanonicalAccessEdgesAsync(scope, organizationId, cancellationToken).ConfigureAwait(false);
        var replayedSet = replayed.Edges.ToHashSet();
        var missing = canonical.Edges.Count(e => !replayedSet.Contains(e));
        var extra = replayedSet.Count(e => !canonical.Edges.Contains(e));
        return new SearchAccessReplayCheck(missing == 0 && extra == 0 ? "COMPLETE" : SearchAccessReasonCodes.MirrorDiverged, state.AuthorizationRevision, state.AuthorizationRevision, replayedSet.Count, canonical.Edges.Count, missing, extra, state.ContentIntegrityRevision);
    }

    private async Task ApplyEventAsync(ISearchAccessStore access, Guid organizationId, VerifiedAccessEvent verified, long contentRevision, CancellationToken cancellationToken)
    {
        var deltaEdges = new List<AccessEdge>();
        foreach (var change in verified.Delta.Where(d => d.Edge is not null))
        {
            var e = change.Edge!;
            var existing = await access.FindEdgeAsync(organizationId, e.ResourceType, e.ResourceId, e.Relation, e.SubjectType, e.SubjectId, e.SubjectRelation, cancellationToken).ConfigureAwait(false);
            if (change.IsTouch)
            {
                if (existing is null)
                {
                    access.AddEdge(Row(organizationId, e, verified.Revision));
                    await access.FlushAsync(cancellationToken).ConfigureAwait(false);
                }
            }
            else
            {
                if (existing is null)
                {
                    throw new SearchAccessIntegrityException(SearchAccessReasonCodes.MirrorDeleteAbsent, "a delete names an edge the mirror does not hold");
                }

                access.RemoveEdge(existing);
                await access.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            deltaEdges.Add(e);
        }

        if (deltaEdges.Count == 0)
        {
            return; // an event without access edges advances the checkpoint without row changes
        }

        var subgraph = await LoadSubgraphAsync(access, organizationId, deltaEdges, cancellationToken).ConfigureAwait(false);
        var affected = SearchAccessDerivation.AffectedRecords(deltaEdges, subgraph, options.MaximumParentDepth);
        if (affected.Count == 0)
        {
            return;
        }

        var derived = SearchAccessDerivation.Derive(organizationId, subgraph, affected, verified.Revision, options.MaximumParentDepth);
        await access.DeleteGrantsOfRecordsAsync(organizationId, affected, cancellationToken).ConfigureAwait(false);
        access.AddRecordGrants(derived.RecordGrants);
        access.AddFieldItemGrants(derived.FieldItemGrants);

        // IX-5b: the affected records' value rows follow their new access rows in the same transaction (no member expansion:
        // a membership change affects no record and rewrites no value row).
        await RederiveValuesAsync(access, organizationId, affected, derived, contentRevision, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Loads (post-delta) every mirror edge the affected records' derivation reads: the records of the shares named, all descendants,
    /// the records' and their ancestors' own edges, and the shares' role and item edges.
    /// </summary>
    private async Task<AccessEdgeSet> LoadSubgraphAsync(ISearchAccessStore access, Guid organizationId, IReadOnlyList<AccessEdge> deltaEdges, CancellationToken cancellationToken)
    {
        var set = new AccessEdgeSet([]);
        void AddAll(IEnumerable<SearchAccessEdge> rows)
        {
            foreach (var row in rows)
            {
                set.Add(AccessEdge.Of(row));
            }
        }

        var records = deltaEdges.Where(e => e.ResourceType == "form_record").Select(e => e.ResourceId).ToHashSet();
        var shares = deltaEdges.Where(e => e.ResourceType == "record_share").Select(e => e.ResourceId)
            .Concat(deltaEdges.Where(e => e.ResourceType == "field_item" && e.SubjectType == "record_share").Select(e => e.SubjectId)).ToHashSet();
        if (shares.Count > 0)
        {
            var links = await access.GetEdgesBySubjectAsync(organizationId, "record_share", shares, "form_record", "shared_via", cancellationToken).ConfigureAwait(false);
            AddAll(links);
            records.UnionWith(links.Select(l => l.ResourceEntityId));
        }

        // Descendants (children name their parent as subject).
        var frontier = records.ToList();
        for (var depth = 0; frontier.Count > 0; depth++)
        {
            if (depth > options.MaximumParentDepth)
            {
                throw new SearchAccessIntegrityException(SearchAccessReasonCodes.ParentChainTooDeep, "descendant chain exceeds the maximum depth");
            }

            var children = await access.GetEdgesBySubjectAsync(organizationId, "form_record", frontier, "form_record", "parent", cancellationToken).ConfigureAwait(false);
            AddAll(children);
            frontier = children.Select(c => c.ResourceEntityId).Where(records.Add).ToList();
        }

        // The records' own edges, then their ancestors' (effective owner through parent).
        var loaded = new HashSet<Guid>();
        frontier = records.ToList();
        for (var depth = 0; frontier.Count > 0; depth++)
        {
            if (depth > options.MaximumParentDepth + 1)
            {
                throw new SearchAccessIntegrityException(SearchAccessReasonCodes.ParentChainTooDeep, "parent chain exceeds the maximum depth");
            }

            var own = await access.GetEdgesByResourceAsync(organizationId, "form_record", frontier, cancellationToken).ConfigureAwait(false);
            AddAll(own);
            loaded.UnionWith(frontier);
            shares.UnionWith(own.Where(e => e.RelationshipName == "shared_via" && e.SubjectTypeName == "record_share").Select(e => e.SubjectEntityId));
            frontier = own.Where(e => e.RelationshipName == "parent" && e.SubjectTypeName == "form_record").Select(e => e.SubjectEntityId).Where(p => !loaded.Contains(p)).Distinct().ToList();
        }

        if (shares.Count > 0)
        {
            AddAll(await access.GetEdgesByResourceAsync(organizationId, "record_share", shares, cancellationToken).ConfigureAwait(false));
            AddAll(await access.GetEdgesBySubjectAsync(organizationId, "record_share", shares, "field_item", "shared_via", cancellationToken).ConfigureAwait(false));
        }

        return set;
    }

    private async Task<(int Checks, int Sampled, string? Finding, string Detail)> CompareWithSpiceDbAsync(IAuthorityScope scope, Domain.Features.Organizations.OrganizationAuthorizationState state, AccessEdgeSet canonical, List<SearchAccessRecordGrant> recordGrants, List<SearchAccessFieldItemGrant> itemGrants, Guid correlationId, CancellationToken cancellationToken)
    {
        var sample = recordGrants.Select(g => g.FormRecordId).Distinct().OrderBy(r => r).Take(Math.Max(0, options.VerificationSampleSize)).ToList();
        if (sample.Count == 0)
        {
            return (0, 0, null, string.Empty);
        }

        // Principals: every principal of the sampled records' rows (positive) plus every other sampled record's (negative controls);
        // user-level: the members of every team principal, which SpiceDB expands and Tier 2 does not (D-1).
        var principals = recordGrants.Where(g => sample.Contains(g.FormRecordId)).Select(g => (g.AccessPrincipalTypeName, g.AccessPrincipalEntityId)).ToHashSet();
        var members = principals.Where(p => p.AccessPrincipalTypeName == "team")
            .SelectMany(t => canonical.Of("team", t.AccessPrincipalEntityId, "member").Where(e => e.SubjectType == "user").Select(e => (t.AccessPrincipalEntityId, e.SubjectId))).ToList();
        var checks = new List<(SpiceDbCheck Check, bool Expected)>();
        foreach (var record in sample)
        {
            var owners = recordGrants.Where(g => g.FormRecordId == record && g.AccessKindName == SearchAccessKinds.EffectiveOwner).Select(g => (g.AccessPrincipalTypeName, g.AccessPrincipalEntityId)).ToHashSet();
            var discoverers = recordGrants.Where(g => g.FormRecordId == record).Select(g => (g.AccessPrincipalTypeName, g.AccessPrincipalEntityId)).ToHashSet();
            var items = itemGrants.Where(g => g.FormRecordId == record).GroupBy(g => g.ManifestItemId).ToDictionary(g => g.Key, g => g.Select(x => (x.AccessPrincipalTypeName, x.AccessPrincipalEntityId)).ToHashSet());
            foreach (var p in principals)
            {
                // SpiceDB answers for a user through every team the user belongs to: the expected answer is the distinct union over
                // the caller's principal set P = {user} ∪ teams (INV-T2-13), exactly as a Tier 2 query would take it.
                var set = PrincipalSet(canonical, p);
                checks.Add((Check("form_record", record, "effective_owner", p), set.Overlaps(owners)));
                checks.Add((Check("form_record", record, "discover", p), set.Overlaps(discoverers)));
                foreach (var (item, readers) in items)
                {
                    checks.Add((Check("field_item", item, "read", p), set.Overlaps(readers)));
                }
            }

            foreach (var (team, user) in members)
            {
                var viaTeam = items.Where(i => i.Value.Contains(("team", team))).Select(i => i.Key).ToList();
                foreach (var item in viaTeam)
                {
                    checks.Add((Check("field_item", item, "read", ("user", user)), true));
                }

                if (discoverers.Contains(("team", team)))
                {
                    checks.Add((Check("form_record", record, "discover", ("user", user)), true));
                }
            }
        }

        var snapshot = await integrity.PinAsync(scope, state, organizationLocked: false, correlationId, cancellationToken).ConfigureAwait(false);
        var outcomes = await integrity.CheckBulkAsync(snapshot, checks.Select(c => c.Check).ToList(), cancellationToken).ConfigureAwait(false);
        if (!await integrity.CompleteAsync(snapshot, "search-access-verify", cancellationToken).ConfigureAwait(false))
        {
            return (checks.Count, sample.Count, "MOVED", "the organization moved during the SpiceDB comparison");
        }

        var mismatches = checks.Select((c, i) => (c, outcome: outcomes[i])).Where(x => (x.outcome == SpiceDbCheckOutcome.Allowed) != x.c.Expected || x.outcome == SpiceDbCheckOutcome.Unknown).ToList();
        return mismatches.Count == 0
            ? (checks.Count, sample.Count, null, string.Empty)
            : (checks.Count, sample.Count, SearchAccessReasonCodes.SpiceDbDiverged, $"{mismatches.Count} of {checks.Count} checks differ (first: {mismatches[0].c.Check.ResourceType} {mismatches[0].c.Check.Permission} expected {mismatches[0].c.Expected})");

        static HashSet<(string, Guid)> PrincipalSet(AccessEdgeSet edges, (string Type, Guid Id) p)
        {
            var set = new HashSet<(string, Guid)> { p };
            if (p.Type == "user")
            {
                set.UnionWith(edges.WithSubject("user", p.Id, "team", "member").Where(e => e.SubjectRelation.Length == 0).Select(e => ("team", e.ResourceId)));
            }

            return set;
        }

        static SpiceDbCheck Check(string type, Guid id, string permission, (string Type, Guid Id) p) =>
            new(type, id.ToString("D"), permission, p.Type, p.Id.ToString("D"), p.Type == "team" ? "participant" : string.Empty);
    }

    private async Task QuarantineAsync(Guid organizationId, long observedCheckpointRevision, string reasonCode, string detail, CancellationToken cancellationToken)
    {
        await using var scope = await store.BeginScopeAsync(cancellationToken).ConfigureAwait(false);
        var state = await scope.LockOrganizationAsync(organizationId, cancellationToken).ConfigureAwait(false);
        if (state is null)
        {
            return;
        }

        var access = scope.SearchAccess;
        await access.BindOrganizationAsync(organizationId, cancellationToken).ConfigureAwait(false);
        var checkpoint = await access.FindCheckpointForUpdateAsync(organizationId, cancellationToken).ConfigureAwait(false);
        if (checkpoint is null || checkpoint.AccessProjectionStateName == SearchAccessProjectionStates.Quarantined || checkpoint.CheckpointRevision != observedCheckpointRevision)
        {
            return; // already quarantined, or the checkpoint moved since the failure was observed (re-evaluated next pass)
        }

        if (!await access.TryUpdateCheckpointAsync(organizationId, checkpoint.CheckpointRevision, SearchAccessCheckpointValues.Of(checkpoint) with { StateName = SearchAccessProjectionStates.Quarantined, QuarantineReasonCode = reasonCode }, clock.GetUtcNow(), cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        AuditJournal.Append(scope, state, QuarantineEventKind, new
        {
            organization_id = organizationId,
            reason_code = reasonCode,
            consumed_authorization_revision = checkpoint.ConsumedAuthorizationRevision,
            current_authorization_revision = state.AuthorizationRevision,
            quarantined_on = clock.GetUtcNow(),
        }, clock);
        await scope.CommitAsync(cancellationToken).ConfigureAwait(false);
        alerts.Raise(new SecurityAlert("SEARCH_ACCESS_PROJECTION_QUARANTINED", "High", organizationId, Guid.Empty, null, "Tier 2 access projection quarantined (" + reasonCode + "): " + detail, Guid.NewGuid()));
    }

    /// <summary>
    /// IX-5b, Testing only: evaluates AND-ed conditions over the value rows for one user, internally, to prove multi-condition
    /// correctness. Not a search path: no endpoint outside Testing calls it, nothing is disclosed, and it refuses unless Tier 2 is
    /// ready. P = {user} ∪ the user's teams (canonical membership in the same snapshot). A record owned through P matches if any
    /// readable value satisfies each condition; a share-only record additionally needs every item of each condition's field
    /// readable through P (the accepted per-field item count, Tier 1's UF-25 rule).
    /// </summary>
    public async Task<IReadOnlyList<Guid>> EvaluateForTestingAsync(Guid organizationId, Guid formDefinitionId, Guid authorityUserId, IReadOnlyList<SearchAccessEvaluationCondition> conditions, CancellationToken cancellationToken)
    {
        RequireEnabled();
        ArgumentNullException.ThrowIfNull(conditions);
        if (conditions.Count == 0)
        {
            throw AuthorityException.Invalid("at least one condition is required");
        }

        await using var scope = await store.BeginReadOnlySnapshotScopeAsync(cancellationToken).ConfigureAwait(false);
        var state = await scope.ReadOrganizationAsync(organizationId, cancellationToken).ConfigureAwait(false)
                    ?? throw new AuthorityException(AuthorityErrorCodes.NotFound, 404, false);
        var access = scope.SearchAccess;
        await access.BindOrganizationAsync(organizationId, cancellationToken).ConfigureAwait(false);
        var checkpoint = await access.FindCheckpointAsync(organizationId, cancellationToken).ConfigureAwait(false);
        if (checkpoint is null || !IsReady(checkpoint, await ReadinessInputsAsync(access, state, cancellationToken).ConfigureAwait(false)))
        {
            throw AuthorityException.NotReady("SEARCH_INDEX_NOT_READY");
        }

        var canonical = await CanonicalAccessEdgesAsync(scope, organizationId, cancellationToken).ConfigureAwait(false);
        var principals = canonical.WithSubject("user", authorityUserId, "team", "member").Where(e => e.SubjectRelation.Length == 0).Select(e => ("team", e.ResourceId)).Append(("user", authorityUserId)).ToHashSet();
        var values = (await access.GetValuePostingsAsync(organizationId, null, cancellationToken).ConfigureAwait(false))
            .Where(v => v.FormDefinitionId == formDefinitionId && principals.Contains((v.AccessPrincipalTypeName, v.AccessPrincipalEntityId))).ToList();
        var owned = (await access.GetRecordGrantsAsync(organizationId, null, cancellationToken).ConfigureAwait(false))
            .Where(g => g.AccessKindName == SearchAccessKinds.EffectiveOwner && principals.Contains((g.AccessPrincipalTypeName, g.AccessPrincipalEntityId))).Select(g => g.FormRecordId).ToHashSet();
        var candidates = values.Select(v => v.FormRecordId).ToHashSet();
        var counts = await access.GetFieldItemCountsAsync(organizationId, candidates, cancellationToken).ConfigureAwait(false);
        var result = new List<Guid>();
        foreach (var record in candidates.Order())
        {
            var rows = values.Where(v => v.FormRecordId == record).ToList();
            var all = conditions.All(condition =>
            {
                var field = rows.Where(v => v.FieldDefinitionId == condition.FieldDefinitionId).ToList();
                if (!field.Any(v => Matches(v, condition)))
                {
                    return false;
                }

                if (owned.Contains(record))
                {
                    return true;
                }

                var approval = field[0].FieldSearchApprovalId;
                return counts.TryGetValue((record, approval), out var count) && field.Select(v => v.ManifestItemId).Distinct().Count() == count;
            });
            if (all)
            {
                result.Add(record);
            }
        }

        return result;

        static bool Matches(SearchAccessValuePosting v, SearchAccessEvaluationCondition c) => c.ValueTypeName == v.ValueTypeName && c.ValueTypeName switch
        {
            "String" => string.Equals(v.NormalizedStringValue, c.StringValue, StringComparison.Ordinal),
            "Integer" => v.IntegerValue is { } i && (c.MinimumInteger is null || i >= c.MinimumInteger) && (c.MaximumInteger is null || i <= c.MaximumInteger),
            "Option" => v.FieldOptionId == c.FieldOptionId,
            "Boolean" => v.BooleanValue == c.BooleanValue,
            _ => false,
        };
    }

    private static async Task<AccessEdgeSet> CanonicalAccessEdgesAsync(IAuthorityScope scope, Guid organizationId, CancellationToken cancellationToken)
    {
        var edges = await scope.GetActiveEdgesAsync(organizationId, cancellationToken).ConfigureAwait(false);
        return new AccessEdgeSet(edges.Where(e => AccessRelationshipDelta.IsMirrored(e.ResourceTypeName, e.RelationshipName))
            .Select(e => new AccessEdge(e.ResourceTypeName, e.ResourceEntityId, e.RelationshipName, e.SubjectTypeName, e.SubjectEntityId, e.SubjectRelationName)));
    }

    private static SearchAccessEdge Row(Guid organizationId, AccessEdge edge, long revision) => new()
    {
        SearchAccessEdgeId = Guid.CreateVersion7(),
        OrganizationId = organizationId,
        ResourceTypeName = edge.ResourceType,
        ResourceEntityId = edge.ResourceId,
        RelationshipName = edge.Relation,
        SubjectTypeName = edge.SubjectType,
        SubjectEntityId = edge.SubjectId,
        SubjectRelationName = edge.SubjectRelation,
        MirroredAuthorizationRevision = revision,
    };

    private static bool SameMultiset(IEnumerable<string> left, IEnumerable<string> right) =>
        left.Order(StringComparer.Ordinal).SequenceEqual(right.Order(StringComparer.Ordinal), StringComparer.Ordinal);

    private void RequireEnabled()
    {
        if (options.Mode == SearchAccessMode.Off)
        {
            throw AuthorityException.NotReady("SearchAccessProjectionDisabled");
        }
    }
}
