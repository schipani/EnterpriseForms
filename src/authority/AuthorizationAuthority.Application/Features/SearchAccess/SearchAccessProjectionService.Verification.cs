using AuthorizationAuthority.Application.Common;
using AuthorizationAuthority.Application.Common.Ports;
using AuthorizationAuthority.Domain.Features.Journal;
using AuthorizationAuthority.Domain.Features.SearchAccess;

namespace AuthorizationAuthority.Application.Features.SearchAccess;

/// <summary>Outcome codes of one IX-5 verification step (M1 fence and M2 sweep).</summary>
public static class SearchAccessVerificationOutcomes
{
    public const string Verified = "VERIFIED";
    public const string NotAtHead = "NOT_AT_HEAD";
    public const string SpiceDbNotReady = "SPICEDB_NOT_READY";
    public const string Moved = "MOVED";
    public const string Diverged = "DIVERGED";
    public const string FenceLimit = "FENCE_LIMIT";
    public const string LookupLimit = "LOOKUP_LIMIT";
    public const string Initialized = "INITIALIZED";
    public const string Contended = "CONTENDED";
}

/// <summary>One verification step: the outcome, what the fence and the M2-S window covered, and the resulting state's servability.</summary>
public sealed record SearchAccessVerificationStep(string OutcomeCode, long AccessVerifiedRevision, int FencedRecords, int SweptRecords, bool SweepCompleted, bool FullVerificationRan, string? FullVerificationOutcome,
    int SubjectLookups, bool Verified, string? FindingDetail);

/// <summary>The M2 currency of an organization's Tier 2 rows (status and the shadow/serving gate).</summary>
public sealed record SearchAccessVerificationStatus(bool Verified, string? ReasonCode, long AccessVerifiedRevision, DateTimeOffset? FullVerificationCompletedOn, DateTimeOffset? SpiceDbSweepCompletedOn, long SpiceDbSweepRecordsChecked, string? LastOutcomeCode);

public sealed partial class SearchAccessProjectionService
{
    /// <summary>
    /// IX-5 verification gate: Tier 2 rows count as <i>verified</i> (comparable in the shadow, and a precondition of any future serving)
    /// only when, in addition to exact readiness (A-3):
    /// <list type="bullet">
    /// <item><b>M1 fence:</b> every record changed up to the current authorization revision was compared with SpiceDB <c>LookupSubjects</c>
    /// at a pinned snapshot (<see cref="SearchAccessVerificationState.AccessVerifiedRevision"/> equals the revision);</item>
    /// <item><b>M2-E and M2-V:</b> the full equivalence check (mirror = canonical edges; rows and value rows = their derivation) was clean
    /// within <see cref="SearchAccessOptions.FullVerificationDeadlineSeconds"/>;</item>
    /// <item><b>M2-S:</b> a complete <c>LookupSubjects</c> pass over every record ended within <see cref="SearchAccessOptions.SpiceDbSweepDeadlineSeconds"/>,
    /// started after the rows were last (re)built (rebuild and quarantine delete the state).</item>
    /// </list>
    /// No grace: an overdue deadline makes the rows unverified until the sweep catches up (sampling plan, S-D without grace).
    /// </summary>
    public static SearchAccessVerificationStatus VerificationStatus(SearchAccessVerificationState? state, long authorizationRevision, SearchAccessOptions options, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (state is null)
        {
            return new SearchAccessVerificationStatus(false, "M2_NOT_STARTED", 0, null, null, 0, null);
        }

        string? reason = state.AccessVerifiedRevision != authorizationRevision ? "M1_FENCE_OPEN"
            : state.FullVerificationCompletedOn is not { } full || now - full > TimeSpan.FromSeconds(options.FullVerificationDeadlineSeconds) ? "M2_EQUIVALENCE_OVERDUE"
            : state.SpiceDbSweepCompletedOn is not { } sweep || now - sweep > TimeSpan.FromSeconds(options.SpiceDbSweepDeadlineSeconds) ? "M2_SPICEDB_SWEEP_OVERDUE"
            : null;
        return new SearchAccessVerificationStatus(reason is null, reason, state.AccessVerifiedRevision, state.FullVerificationCompletedOn, state.SpiceDbSweepCompletedOn, state.SpiceDbSweepRecordsChecked, state.LastVerificationOutcomeCode);
    }

    public async Task<SearchAccessVerificationStatus> VerificationStatusAsync(Guid organizationId, CancellationToken cancellationToken)
    {
        await using var scope = await store.BeginReadOnlySnapshotScopeAsync(cancellationToken).ConfigureAwait(false);
        var state = await scope.ReadOrganizationAsync(organizationId, cancellationToken).ConfigureAwait(false) ?? throw new AuthorityException(AuthorityErrorCodes.NotFound, 404, false);
        await scope.SearchAccess.BindOrganizationAsync(organizationId, cancellationToken).ConfigureAwait(false);
        return VerificationStatus(await scope.SearchAccess.FindVerificationStateAsync(organizationId, cancellationToken).ConfigureAwait(false), state.AuthorizationRevision, options, clock.GetUtcNow());
    }

    /// <summary>
    /// One IX-5 verification step for an organization (the verification loop, or the Testing endpoint):
    /// <list type="number">
    /// <item>only on rows exactly at the head (A-3), of the current derivation, with the S1 gate Ready; otherwise nothing changes;</item>
    /// <item>a missing state is created at the current revision with an empty M2-S pass (the pass then covers every record);</item>
    /// <item><b>M1 fence:</b> the records affected by every authorization event after the verified revision (chain-verified from its digest)
    /// and the next <see cref="SearchAccessOptions.SpiceDbSweepBatchSize"/> records of the M2-S pass are compared with SpiceDB at one pin:
    /// for each record, the user and team subjects of <c>effective_owner</c> and <c>discover</c>, and of <c>read</c> on each of its items Tier 2
    /// holds, must equal exactly what the stored rows give (teams expanded to their members only at user level);</item>
    /// <item>the pin must complete (nothing moved), or nothing is recorded;</item>
    /// <item><b>M2-E/V</b> runs when due (<see cref="VerifyAsync"/>);</item>
    /// <item>the state is saved under the checkpoint lock, only if it is unchanged since it was read (a concurrent rebuild wins).</item>
    /// </list>
    /// A difference quarantines Tier 2 (never repairs). A fence or lookup limit fails closed: the state is reset, so the rows stay unverified
    /// until a complete new pass.
    /// </summary>
    public async Task<SearchAccessVerificationStep> VerifyStepAsync(Guid organizationId, Guid correlationId, CancellationToken cancellationToken, bool forceFullVerification = false)
    {
        RequireEnabled();
        var now = clock.GetUtcNow();
        SearchAccessVerificationState? read;
        SearchAccessVerificationState next;
        int fenced, swept, lookups;
        bool completedPass;
        long revision;
        long observedCheckpointRevision;
        await using (var scope = await store.BeginScopeAsync(cancellationToken).ConfigureAwait(false))
        {
            var state = await scope.ReadOrganizationAsync(organizationId, cancellationToken).ConfigureAwait(false) ?? throw new AuthorityException(AuthorityErrorCodes.NotFound, 404, false);
            var access = scope.SearchAccess;
            await access.BindOrganizationAsync(organizationId, cancellationToken).ConfigureAwait(false);
            var checkpoint = await access.FindCheckpointAsync(organizationId, cancellationToken).ConfigureAwait(false);
            if (checkpoint is null || !IsReady(checkpoint, await ReadinessInputsAsync(access, state, cancellationToken).ConfigureAwait(false)))
            {
                return Step(SearchAccessVerificationOutcomes.NotAtHead, null);
            }

            observedCheckpointRevision = checkpoint.CheckpointRevision;
            revision = state.AuthorizationRevision;
            read = await access.FindVerificationStateAsync(organizationId, cancellationToken).ConfigureAwait(false);
            if (read is null)
            {
                // A new pass: nothing is verified yet; the fence starts at the current revision because the pass covers every record after it.
                var initial = new SearchAccessVerificationState
                {
                    SearchAccessVerificationStateId = Guid.CreateVersion7(),
                    OrganizationId = organizationId,
                    AccessVerifiedRevision = revision,
                    AccessVerifiedJournalDigest = revision == 0 ? null : state.LatestSecurityJournalDigest,
                    FullVerificationAuthorizationRevision = 0,
                    SpiceDbSweepStartedOn = now,
                    LastVerificationOutcomeCode = SearchAccessVerificationOutcomes.Initialized,
                    VerificationStateModifiedOn = now,
                };
                return await SaveGuardedAsync(organizationId, observedCheckpointRevision, null, initial, cancellationToken).ConfigureAwait(false)
                    ? Step(SearchAccessVerificationOutcomes.Initialized, initial)
                    : Step(SearchAccessVerificationOutcomes.Contended, null);
            }

            var canonical = await CanonicalAccessEdgesAsync(scope, organizationId, cancellationToken).ConfigureAwait(false);
            // M1: the records the events after the verified revision can have changed (empty for membership-only events: F-1).
            var affected = new HashSet<Guid>();
            var previousDigest = read.AccessVerifiedJournalDigest;
            for (var r = read.AccessVerifiedRevision + 1; r <= revision; r++)
            {
                var entry = await scope.FindJournalEntryAsync(organizationId, JournalStreamNames.Authorization, r, cancellationToken).ConfigureAwait(false);
                VerifiedAccessEvent verified;
                try
                {
                    verified = SearchAccessJournalVerifier.Verify(entry, organizationId, r, previousDigest);
                }
                catch (SearchAccessIntegrityException ex)
                {
                    await QuarantineAsync(organizationId, observedCheckpointRevision, ex.ReasonCode, ex.Message, cancellationToken).ConfigureAwait(false);
                    return Step(SearchAccessVerificationOutcomes.Diverged, null, ex.Message);
                }

                previousDigest = verified.Digest;
                affected.UnionWith(SearchAccessDerivation.AffectedRecords(verified.Delta.Where(d => d.Edge is not null).Select(d => d.Edge!), canonical, options.MaximumParentDepth));
            }

            if (affected.Count > options.FenceMaximumRecords)
            {
                // Fail closed: the fence cannot be closed record by record; a complete new M2-S pass is required instead.
                await ResetVerificationAsync(organizationId, observedCheckpointRevision, read, cancellationToken).ConfigureAwait(false);
                return Step(SearchAccessVerificationOutcomes.FenceLimit, null, $"{affected.Count} affected records exceed the fence maximum {options.FenceMaximumRecords}");
            }

            // M2-S: the next window of every record that has Tier 2 rows, in id order.
            var recordGrants = await access.GetRecordGrantsAsync(organizationId, null, cancellationToken).ConfigureAwait(false);
            var allRecords = recordGrants.Select(g => g.FormRecordId).Distinct().Order().ToList();
            // A pass in progress continues; a completed pass restarts only after SpiceDbSweepIntervalSeconds (rolling, well inside the deadline).
            var sweeping = read.SpiceDbSweepCursorRecordId is not null || read.SpiceDbSweepCompletedOn is not { } completedOn || now - completedOn >= TimeSpan.FromSeconds(options.SpiceDbSweepIntervalSeconds);
            var window = sweeping
                ? allRecords.Where(id => read.SpiceDbSweepCursorRecordId is not { } cursor || id.CompareTo(cursor) > 0).Take(options.SpiceDbSweepBatchSize).ToList()
                : [];
            completedPass = sweeping && window.Count < options.SpiceDbSweepBatchSize;
            var compare = affected.Union(window).ToList();
            var itemGrants = await access.GetFieldItemGrantsAsync(organizationId, compare, cancellationToken).ConfigureAwait(false);
            var valueItems = (await access.GetValuePostingsAsync(organizationId, compare, cancellationToken).ConfigureAwait(false)).GroupBy(v => v.FormRecordId).ToDictionary(g => g.Key, g => g.Select(v => v.ManifestItemId).ToHashSet());
            fenced = affected.Count;
            swept = window.Count;
            lookups = 0;
            if (compare.Count > 0)
            {
                VerifiedProjectionSnapshot pin;
                try
                {
                    pin = await integrity.PinAsync(scope, state, organizationLocked: false, correlationId, cancellationToken).ConfigureAwait(false);
                }
                catch (AuthorityException ex) when (ex.ErrorCode == AuthorityErrorCodes.AuthorityNotReady)
                {
                    return Step(SearchAccessVerificationOutcomes.SpiceDbNotReady, read);
                }

                if (pin.ExpectedRevision != revision)
                {
                    return Step(SearchAccessVerificationOutcomes.SpiceDbNotReady, read);
                }

                string? finding;
                try
                {
                    // field_item.read = parent_record->effective_owner + shared_via->read: owners read an item only through a parent_record
                    // tuple, which is not mirrored into Tier 2; read it from the canonical edges (in this same scope).
                    var itemParents = (await scope.GetActiveEdgesAsync(organizationId, cancellationToken).ConfigureAwait(false))
                        .Where(e => e.ResourceTypeName == "field_item" && e.RelationshipName == "parent_record" && e.SubjectTypeName == "form_record")
                        .GroupBy(e => e.ResourceEntityId).ToDictionary(g => g.Key, g => g.Select(e => e.SubjectEntityId).ToHashSet());
                    (finding, lookups) = await CompareSubjectsAsync(pin, canonical, recordGrants, itemGrants, valueItems, itemParents, compare, cancellationToken).ConfigureAwait(false);
                }
                catch (SpiceDbLookupLimitExceededException ex)
                {
                    await ResetVerificationAsync(organizationId, observedCheckpointRevision, read, cancellationToken).ConfigureAwait(false);
                    return Step(SearchAccessVerificationOutcomes.LookupLimit, null, ex.Message);
                }

                if (!await integrity.CompleteAsync(pin, "search-access-m1", cancellationToken).ConfigureAwait(false))
                {
                    return Step(SearchAccessVerificationOutcomes.Moved, read);
                }

                if (finding is not null)
                {
                    await QuarantineAsync(organizationId, observedCheckpointRevision, SearchAccessReasonCodes.SpiceDbDiverged, finding, cancellationToken).ConfigureAwait(false);
                    return Step(SearchAccessVerificationOutcomes.Diverged, null, finding);
                }
            }

            next = new SearchAccessVerificationState
            {
                SearchAccessVerificationStateId = read.SearchAccessVerificationStateId,
                OrganizationId = organizationId,
                AccessVerifiedRevision = revision,
                AccessVerifiedJournalDigest = revision == 0 ? null : state.LatestSecurityJournalDigest,
                FullVerificationCompletedOn = read.FullVerificationCompletedOn,
                FullVerificationAuthorizationRevision = read.FullVerificationAuthorizationRevision,
                SpiceDbSweepCursorRecordId = completedPass || window.Count == 0 ? (completedPass ? null : read.SpiceDbSweepCursorRecordId) : window[^1],
                SpiceDbSweepStartedOn = read.SpiceDbSweepCursorRecordId is null && sweeping ? now : read.SpiceDbSweepStartedOn,
                SpiceDbSweepCompletedOn = completedPass ? now : read.SpiceDbSweepCompletedOn,
                SpiceDbSweepRecordsChecked = read.SpiceDbSweepRecordsChecked + window.Count,
                LastVerificationOutcomeCode = SearchAccessVerificationOutcomes.Verified,
                VerificationStateModifiedOn = now,
            };
        }

        // M2-E/V when due: the full equivalence check in its own read-only snapshot (it quarantines itself on a difference).
        string? fullOutcome = null;
        if (forceFullVerification || next.FullVerificationCompletedOn is not { } last || now - last >= TimeSpan.FromSeconds(options.FullVerificationIntervalSeconds))
        {
            var full = await VerifyAsync(organizationId, correlationId, cancellationToken).ConfigureAwait(false);
            fullOutcome = full.OutcomeCode;
            if (full.OutcomeCode == "CLEAN")
            {
                next.FullVerificationCompletedOn = clock.GetUtcNow();
                next.FullVerificationAuthorizationRevision = full.VerifiedRevision;
            }
            else if (full.OutcomeCode is not ("MOVED" or "NOT_AT_HEAD" or "SPICEDB_NOT_READY"))
            {
                return Step(SearchAccessVerificationOutcomes.Diverged, null, full.FindingDetail) with { FullVerificationRan = true, FullVerificationOutcome = fullOutcome };
            }
        }

        if (!await SaveGuardedAsync(organizationId, observedCheckpointRevision, read, next, cancellationToken).ConfigureAwait(false))
        {
            return Step(SearchAccessVerificationOutcomes.Contended, null) with { FullVerificationRan = fullOutcome is not null, FullVerificationOutcome = fullOutcome };
        }

        var verifiedNow = VerificationStatus(next, revision, options, clock.GetUtcNow()).Verified;
        return new SearchAccessVerificationStep(SearchAccessVerificationOutcomes.Verified, next.AccessVerifiedRevision, fenced, swept, completedPass, fullOutcome is not null, fullOutcome, lookups, verifiedNow, null);

        SearchAccessVerificationStep Step(string code, SearchAccessVerificationState? s, string? detail = null) =>
            new(code, s?.AccessVerifiedRevision ?? 0, 0, 0, false, false, null, 0, false, detail);
    }

    /// <summary>
    /// For each record: the SpiceDB user and team subjects of <c>effective_owner</c> and <c>discover</c>, and of <c>read</c> on each item Tier 2
    /// holds rows for, against the stored rows (decomposed lookups with team-scoped subjects, IX5-CT-11, at the exact pin, IX5-CT-12).
    /// </summary>
    private async Task<(string? Finding, int Lookups)> CompareSubjectsAsync(VerifiedProjectionSnapshot pin, AccessEdgeSet canonical, List<SearchAccessRecordGrant> recordGrants, List<SearchAccessFieldItemGrant> itemGrants,
        Dictionary<Guid, HashSet<Guid>> valueItems, Dictionary<Guid, HashSet<Guid>> itemParents, IReadOnlyList<Guid> records, CancellationToken cancellationToken)
    {
        var lookups = 0;
        var members = canonical.Edges.Where(e => e.ResourceType == "team" && e.Relation == "member" && e.SubjectType == "user" && e.SubjectRelation.Length == 0)
            .GroupBy(e => e.ResourceId).ToDictionary(g => g.Key, g => g.Select(e => e.SubjectId).ToHashSet());
        (HashSet<Guid> Users, HashSet<Guid> Teams) Expand(IEnumerable<(string Type, Guid Id)> principals)
        {
            var list = principals.ToList();
            var teams = list.Where(p => p.Type == "team").Select(p => p.Id).ToHashSet();
            var users = list.Where(p => p.Type == "user").Select(p => p.Id).ToHashSet();
            foreach (var team in teams)
            {
                users.UnionWith(members.GetValueOrDefault(team) ?? []);
            }

            return (users, teams);
        }

        async Task<string?> CompareAsync(string type, Guid id, string permission, (HashSet<Guid> Users, HashSet<Guid> Teams) expected)
        {
            var users = await integrity.LookupSubjectsAsync(pin, type, id.ToString("D"), permission, "user", string.Empty, options.MaximumLookupSubjects, cancellationToken).ConfigureAwait(false);
            var teams = await integrity.LookupSubjectsAsync(pin, type, id.ToString("D"), permission, "team", "participant", options.MaximumLookupSubjects, cancellationToken).ConfigureAwait(false);
            lookups += 2;
            var actualUsers = users.Select(Guid.Parse).ToHashSet();
            var actualTeams = teams.Select(Guid.Parse).ToHashSet();
            return actualUsers.SetEquals(expected.Users) && actualTeams.SetEquals(expected.Teams)
                ? null
                : $"{type} {permission}: SpiceDB users {actualUsers.Count} / Tier 2 {expected.Users.Count} (missing {expected.Users.Count(u => !actualUsers.Contains(u))}, extra {actualUsers.Count(u => !expected.Users.Contains(u))}); teams {actualTeams.Count} / {expected.Teams.Count}";
        }

        foreach (var record in records)
        {
            var grants = recordGrants.Where(g => g.FormRecordId == record).ToList();
            var owners = grants.Where(g => g.AccessKindName == SearchAccessKinds.EffectiveOwner).Select(g => (g.AccessPrincipalTypeName, g.AccessPrincipalEntityId)).ToList();
            var finding = await CompareAsync("form_record", record, "effective_owner", Expand(owners)).ConfigureAwait(false)
                          ?? await CompareAsync("form_record", record, "discover", Expand(grants.Select(g => (g.AccessPrincipalTypeName, g.AccessPrincipalEntityId)))).ConfigureAwait(false);
            if (finding is not null)
            {
                return (finding, lookups);
            }

            var shared = itemGrants.Where(g => g.FormRecordId == record).GroupBy(g => g.ManifestItemId).ToDictionary(g => g.Key, g => g.Select(x => (x.AccessPrincipalTypeName, x.AccessPrincipalEntityId)).ToList());
            foreach (var item in shared.Keys.Union(valueItems.GetValueOrDefault(record) ?? []).Order())
            {
                IEnumerable<(string, Guid)> viaParent = itemParents.TryGetValue(item, out var parents) && parents.Contains(record) ? owners : [];
                finding = await CompareAsync("field_item", item, "read", Expand(viaParent.Concat(shared.GetValueOrDefault(item) ?? []))).ConfigureAwait(false);
                if (finding is not null)
                {
                    return (finding, lookups);
                }
            }
        }

        return (null, lookups);
    }

    /// <summary>Saves the state under the checkpoint lock, only if the checkpoint is unchanged and the stored state is still the one read.</summary>
    private async Task<bool> SaveGuardedAsync(Guid organizationId, long observedCheckpointRevision, SearchAccessVerificationState? read, SearchAccessVerificationState next, CancellationToken cancellationToken)
    {
        await using var scope = await store.BeginScopeAsync(cancellationToken).ConfigureAwait(false);
        var access = scope.SearchAccess;
        await access.BindOrganizationAsync(organizationId, cancellationToken).ConfigureAwait(false);
        var checkpoint = await access.FindCheckpointForUpdateAsync(organizationId, cancellationToken).ConfigureAwait(false);
        var current = await access.FindVerificationStateAsync(organizationId, cancellationToken).ConfigureAwait(false);
        var unchanged = read is null ? current is null : current is not null && current.VerificationStateModifiedOn == read.VerificationStateModifiedOn && current.SearchAccessVerificationStateId == read.SearchAccessVerificationStateId;
        if (checkpoint is null || checkpoint.AccessProjectionStateName != SearchAccessProjectionStates.Maintaining || !SearchAccessSchemaCoupling.CurrentDerivation(checkpoint) || !unchanged
            || (read is not null && checkpoint.CheckpointRevision < observedCheckpointRevision))
        {
            return false;
        }

        await access.SaveVerificationStateAsync(next, cancellationToken).ConfigureAwait(false);
        await scope.CommitAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    private async Task ResetVerificationAsync(Guid organizationId, long observedCheckpointRevision, SearchAccessVerificationState read, CancellationToken cancellationToken)
    {
        await using var scope = await store.BeginScopeAsync(cancellationToken).ConfigureAwait(false);
        var access = scope.SearchAccess;
        await access.BindOrganizationAsync(organizationId, cancellationToken).ConfigureAwait(false);
        _ = await access.FindCheckpointForUpdateAsync(organizationId, cancellationToken).ConfigureAwait(false);
        var current = await access.FindVerificationStateAsync(organizationId, cancellationToken).ConfigureAwait(false);
        if (current is not null && current.SearchAccessVerificationStateId == read.SearchAccessVerificationStateId)
        {
            await access.DeleteVerificationStateAsync(organizationId, cancellationToken).ConfigureAwait(false);
            await scope.CommitAsync(cancellationToken).ConfigureAwait(false);
        }

        _ = observedCheckpointRevision;
    }
}
