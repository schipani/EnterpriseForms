using AuthorizationAuthority.Application.Common.Ports;
using AuthorizationAuthority.Application.Features.SearchIndex;
using AuthorizationAuthority.Domain.Features.FieldSearch;
using AuthorizationAuthority.Domain.Features.SearchIndex;

namespace AuthorizationAuthority.Tests;

/// <summary>
/// IX-1: in-memory double of the Authority search index for unit tests. It reproduces the data contract (replace, purge,
/// set checks, timestamps), not PostgreSQL: transactional atomicity, RLS, grants and the INV-R trigger are proven against
/// real PostgreSQL in the Docker integration tests (SearchIndexFoundationTests).
/// </summary>
internal sealed class InMemorySearchIndexStore(InMemoryAuthorityStore s) : ISearchIndexStore
{
    public List<SearchProjectionState> Projections { get; } = [];
    public List<SearchFieldCoverage> Coverages { get; } = [];
    public List<SearchRecordEntry> Entries { get; } = [];
    public List<SearchPostingBase> Postings { get; } = [];
    public List<SearchRecordFieldAnomaly> Anomalies { get; } = [];
    public List<SearchFormState> FormStates { get; } = [];
    public List<Guid> BoundOrganizations { get; } = [];
    public List<SearchResultSnapshot> ResultSnapshots { get; } = [];
    public List<SearchResultSnapshotEntry> ResultSnapshotEntries { get; } = [];

    public Task BindOrganizationAsync(Guid organizationId, CancellationToken cancellationToken)
    {
        BoundOrganizations.Add(organizationId);
        return Task.CompletedTask;
    }

    public Task<SearchProjectionState?> FindProjectionStateAsync(Guid organizationId, CancellationToken cancellationToken) => Task.FromResult(Projections.FirstOrDefault(p => p.OrganizationId == organizationId));

    public void AddProjectionState(SearchProjectionState state) => Projections.Add(state);

    public Task<List<Guid>> GetIndexedOrganizationIdsAsync(CancellationToken cancellationToken) => Task.FromResult(Projections.Select(p => p.OrganizationId).ToList());

    public Task<List<SearchFieldCoverage>> GetCoveragesAsync(Guid organizationId, Guid? formDefinitionId, CancellationToken cancellationToken) =>
        Task.FromResult(Coverages.Where(c => c.OrganizationId == organizationId && (formDefinitionId == null || c.FormDefinitionId == formDefinitionId)).ToList());

    public void AddCoverage(SearchFieldCoverage coverage) => Coverages.Add(coverage);

    public async Task ReplaceRecordIndexAsync(DerivedRecordIndex derived, CancellationToken cancellationToken)
    {
        await DeleteRecordIndexAsync(derived.Entry.OrganizationId, derived.Entry.FormRecordId, cancellationToken).ConfigureAwait(false);
        Entries.Add(derived.Entry);
        Postings.AddRange(derived.AllPostings);
        Anomalies.AddRange(derived.Anomalies);
    }

    public Task<List<FieldSearchApproval>> GetCoveredActiveApprovalsAsync(Guid organizationId, Guid formDefinitionId, CancellationToken cancellationToken) =>
        Task.FromResult(s.Approvals.Where(a => a.OrganizationId == organizationId && a.FormDefinitionId == formDefinitionId && a.ApprovalStateName == FieldSearchApprovalStates.Active
                                               && Coverages.Any(c => c.OrganizationId == organizationId && c.FormDefinitionId == formDefinitionId && c.FieldSearchApprovalId == a.FieldSearchApprovalId && c.CoverageStateName != SearchCoverageStates.Withdrawn))
            .OrderBy(a => a.FieldSearchApprovalId).ToList());

    /// <summary>Calls recorded by <see cref="ResetRecordIndexAsync"/> (organization, record, always-advance flag).</summary>
    public List<(Guid OrganizationId, Guid FormRecordId, bool AlwaysAdvance)> Resets { get; } = [];

    public async Task<int> ResetRecordIndexAsync(Guid organizationId, Guid formDefinitionId, Guid formRecordId, bool alwaysAdvanceFormState, DateTimeOffset now, CancellationToken cancellationToken)
    {
        Resets.Add((organizationId, formRecordId, alwaysAdvanceFormState));
        await BindOrganizationAsync(organizationId, cancellationToken).ConfigureAwait(false);
        var deleted = await DeleteRecordIndexAsync(organizationId, formRecordId, cancellationToken).ConfigureAwait(false);
        if (alwaysAdvanceFormState || deleted > 0)
        {
            await BumpFormStateAsync(organizationId, formDefinitionId, now, cancellationToken).ConfigureAwait(false);
        }

        return deleted;
    }

    public void AddRecordIndex(DerivedRecordIndex derived)
    {
        ArgumentNullException.ThrowIfNull(derived);
        Entries.Add(derived.Entry);
        Postings.AddRange(derived.AllPostings);
        Anomalies.AddRange(derived.Anomalies);
    }

    public Task<int> DeleteRecordIndexAsync(Guid organizationId, Guid formRecordId, CancellationToken cancellationToken)
    {
        Postings.RemoveAll(p => p.OrganizationId == organizationId && p.FormRecordId == formRecordId);
        Anomalies.RemoveAll(a => a.OrganizationId == organizationId && a.FormRecordId == formRecordId);
        return Task.FromResult(Entries.RemoveAll(e => e.OrganizationId == organizationId && e.FormRecordId == formRecordId));
    }

    public Task<int> PurgeFormIndexAsync(Guid organizationId, Guid formDefinitionId, CancellationToken cancellationToken)
    {
        Postings.RemoveAll(p => p.OrganizationId == organizationId && p.FormDefinitionId == formDefinitionId);
        Anomalies.RemoveAll(a => a.OrganizationId == organizationId && a.FormDefinitionId == formDefinitionId);
        return Task.FromResult(Entries.RemoveAll(e => e.OrganizationId == organizationId && e.FormDefinitionId == formDefinitionId));
    }

    public Task BumpFormStateAsync(Guid organizationId, Guid formDefinitionId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var state = FormStates.FirstOrDefault(f => f.OrganizationId == organizationId && f.FormDefinitionId == formDefinitionId);
        if (state is null)
        {
            FormStates.Add(new SearchFormState { SearchFormStateId = Guid.CreateVersion7(), OrganizationId = organizationId, FormDefinitionId = formDefinitionId, FormContentRevision = 1, FormStateModifiedOn = now });
        }
        else
        {
            state.FormContentRevision++;
            state.FormStateModifiedOn = now;
        }

        return Task.CompletedTask;
    }

    public Task<SearchFormState?> FindFormStateAsync(Guid organizationId, Guid formDefinitionId, CancellationToken cancellationToken) =>
        Task.FromResult(FormStates.FirstOrDefault(f => f.OrganizationId == organizationId && f.FormDefinitionId == formDefinitionId));

    /// <summary>C-136 test hook: invoked before each seal-check selection (one per S-B transaction), e.g. to change state between batches.</summary>
    public Action<int>? BeforeSealCheckSelection { get; set; }

    public int SealCheckSelections { get; private set; }

    public Task<List<SearchRecordEntry>> GetEntriesForSealCheckAsync(Guid organizationId, int limit, IReadOnlyCollection<Guid> excluding, CancellationToken cancellationToken)
    {
        BeforeSealCheckSelection?.Invoke(SealCheckSelections);
        SealCheckSelections++;
        return Task.FromResult(Entries.Where(e => e.OrganizationId == organizationId && !excluding.Contains(e.SearchRecordEntryId)).OrderBy(e => e.LastSealVerifiedOn).ThenBy(e => e.SearchRecordEntryId).Take(limit).ToList());
    }

    public Task<StoredRecordIndex?> GetStoredRecordIndexAsync(Guid organizationId, Guid formRecordId, CancellationToken cancellationToken)
    {
        var entry = Entries.FirstOrDefault(e => e.OrganizationId == organizationId && e.FormRecordId == formRecordId);
        return Task.FromResult(entry is null ? null : new StoredRecordIndex(entry, Postings.Where(p => p.OrganizationId == organizationId && p.FormRecordId == formRecordId).ToList(), Anomalies.Where(a => a.OrganizationId == organizationId && a.FormRecordId == formRecordId).ToList()));
    }

    public Task RecordSealCheckAsync(Guid searchRecordEntryId, DateTimeOffset? verifiedOn, DateTimeOffset attemptedOn, string outcomeCode, CancellationToken cancellationToken)
    {
        var entry = Entries.Single(e => e.SearchRecordEntryId == searchRecordEntryId);
        if (verifiedOn is { } verified)
        {
            entry.LastSealVerifiedOn = verified;
        }

        entry.LastSealAttemptedOn = attemptedOn;
        entry.LastSealAttemptOutcomeCode = outcomeCode;
        return Task.CompletedTask;
    }

    public Task<CheapSweepFindings> RunCheapChecksAsync(Guid organizationId, CancellationToken cancellationToken)
    {
        var entries = Entries.Where(e => e.OrganizationId == organizationId).ToList();
        var rows = Postings.Where(p => p.OrganizationId == organizationId).Select(p => (p.FormRecordId, p.IndexedOperationId, p.FormDefinitionId, (Guid?)p.FieldSearchApprovalId, p.OrganizationId))
            .Concat(Anomalies.Where(a => a.OrganizationId == organizationId).Select(a => (a.FormRecordId, a.IndexedOperationId, a.FormDefinitionId, (Guid?)null, a.OrganizationId))).ToList();
        bool LiveCoverage(Guid approval) => Coverages.Any(c => c.OrganizationId == organizationId && c.FieldSearchApprovalId == approval && c.CoverageStateName != SearchCoverageStates.Withdrawn);
        var notAtHead = entries.Count(e => s.Heads.FirstOrDefault(h => h.OrganizationId == e.OrganizationId && h.FormRecordId == e.FormRecordId) is not { } h || h.CommittedOperationId != e.IndexedOperationId || h.CommittedContentRevision != e.IndexedContentRevision);
        var inactive = entries.Count(e => s.Resources.FirstOrDefault(r => r.OrganizationId == e.OrganizationId && r.ResourceTypeName == "form_record" && r.ResourceEntityId == e.FormRecordId) is not { } r
                                           || r.ResourceLifecycleState is not ("Active" or "Quarantined") || r.ParentResourceEntityId != e.FormDefinitionId);
        var unbound = rows.Count(p => !entries.Any(e => e.FormRecordId == p.FormRecordId && e.IndexedOperationId == p.IndexedOperationId && e.FormDefinitionId == p.FormDefinitionId));
        var withdrawn = rows.Count(p => p.Item4 is { } approval && !LiveCoverage(approval));
        var noCoverage = entries.Count(e => e.DerivedFieldSearchApprovalIds.Any(a => !Coverages.Any(c => c.OrganizationId == organizationId && c.FormDefinitionId == e.FormDefinitionId && c.FieldSearchApprovalId == a && c.CoverageStateName != SearchCoverageStates.Withdrawn)));
        var crossOrganization = rows.Count(p => p.Item4 is { } approvalId && (s.Approvals.FirstOrDefault(a => a.FieldSearchApprovalId == approvalId) is not { } found || found.OrganizationId != p.OrganizationId || found.FormDefinitionId != p.FormDefinitionId));
        var unsealed = entries.Count(e => e.EntrySealSignature.Length != 64);
        var missing = Coverages.Where(c => c.OrganizationId == organizationId && c.CoverageStateName == SearchCoverageStates.Complete)
            .Sum(c => s.Resources.Where(r => r.OrganizationId == organizationId && r.ResourceTypeName == "form_record" && r.ParentResourceEntityId == c.FormDefinitionId && r.ResourceLifecycleState is "Active" or "Quarantined")
                .Count(r => s.Heads.FirstOrDefault(h => h.OrganizationId == organizationId && h.FormRecordId == r.ResourceEntityId) is { CommittedContentRevision: > 0, CommittedOperationId: { } operation }
                            && !entries.Any(e => e.FormRecordId == r.ResourceEntityId && e.IndexedOperationId == operation && e.DerivedFieldSearchApprovalIds.Contains(c.FieldSearchApprovalId))));
        var countMismatch = entries.Count(e => !SearchFieldItemCounts.MatchPostings(e, Postings.Where(p => p.OrganizationId == organizationId && p.FormRecordId == e.FormRecordId && p.IndexedOperationId == e.IndexedOperationId).Select(p => p.FieldSearchApprovalId)));
        return Task.FromResult(new CheapSweepFindings(notAtHead, inactive, unbound, withdrawn, noCoverage, crossOrganization, unsealed, missing, countMismatch));
    }

    public Task<IReadOnlyList<(Guid FormDefinitionId, bool HasUpgradableEntry)>> GetFormsWithOutdatedEntriesAsync(Guid organizationId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<(Guid, bool)>>(Entries.Where(e => e.OrganizationId == organizationId && e.EntryDerivationVersion < 3).GroupBy(e => e.FormDefinitionId).OrderBy(g => g.Key)
            .Select(g => (g.Key, g.Any(e => e.EntryDerivationVersion == 2 && e.DerivedFieldItemCounts is null))).ToList());

    public Task<IReadOnlySet<Guid>> GetFormsAttestedAtDerivationVersionAsync(Guid organizationId, int minimumDerivationVersion, CancellationToken cancellationToken)
    {
        var forms = new HashSet<Guid>();
        foreach (var entry in s.Journal.Where(j => j.OrganizationId == organizationId && j.JournalEventKind == "SearchIndexCoverageAttested"))
        {
            using var body = System.Text.Json.JsonDocument.Parse(entry.JournalEventBodyJson);
            var root = body.RootElement.GetProperty("body"); // the hash-chained envelope (JournalChain) carries the event under "body"
            if (root.TryGetProperty("coverage_complete", out var complete) && complete.ValueKind == System.Text.Json.JsonValueKind.True
                && root.TryGetProperty("attesting_entry_derivation_version", out var version) && version.ValueKind == System.Text.Json.JsonValueKind.Number && version.GetInt32() >= minimumDerivationVersion)
            {
                forms.Add(root.GetProperty("form_definition_id").GetGuid());
            }
        }

        return Task.FromResult<IReadOnlySet<Guid>>(forms);
    }

    public async Task<IReadOnlyList<Guid>> DeleteEntriesNotAtHeadAsync(Guid organizationId, CancellationToken cancellationToken)
    {
        var stale = Entries.Where(e => e.OrganizationId == organizationId && s.Heads.FirstOrDefault(h => h.OrganizationId == organizationId && h.FormRecordId == e.FormRecordId)?.CommittedOperationId != e.IndexedOperationId).ToList();
        foreach (var entry in stale)
        {
            await DeleteRecordIndexAsync(organizationId, entry.FormRecordId, cancellationToken).ConfigureAwait(false);
        }

        return stale.Select(e => e.FormDefinitionId).Distinct().ToList();
    }

    public Task<List<SearchBuildingForm>> GetBuildingFormsAsync(CancellationToken cancellationToken) =>
        Task.FromResult(Coverages.Where(c => c.CoverageStateName == SearchCoverageStates.Building)
            .Join(Projections.Where(p => p.ProjectionStatusName != SearchProjectionStatuses.Quarantined), c => c.OrganizationId, p => p.OrganizationId, (c, p) => (c, p))
            .GroupBy(x => (x.c.OrganizationId, x.c.FormDefinitionId, x.p.ProjectionStatusName))
            .OrderBy(g => g.Key.OrganizationId).ThenBy(g => g.Key.FormDefinitionId)
            .Select(g => new SearchBuildingForm(g.Key.OrganizationId, g.Key.FormDefinitionId, g.Count(), g.Key.ProjectionStatusName)).ToList());

    public Task<List<SearchRecordEntry>> GetFormEntriesAsync(Guid organizationId, Guid formDefinitionId, IReadOnlyCollection<Guid>? formRecordIds, CancellationToken cancellationToken) =>
        Task.FromResult(Entries.Where(e => e.OrganizationId == organizationId && e.FormDefinitionId == formDefinitionId && (formRecordIds is null || formRecordIds.Contains(e.FormRecordId))).OrderBy(e => e.FormRecordId).ToList());

    public Task<int> PurgeOrganizationIndexAsync(Guid organizationId, CancellationToken cancellationToken)
    {
        Postings.RemoveAll(p => p.OrganizationId == organizationId);
        Anomalies.RemoveAll(a => a.OrganizationId == organizationId);
        return Task.FromResult(Entries.RemoveAll(e => e.OrganizationId == organizationId));
    }

    private bool RecordQuarantined(SearchRecordEntry e) =>
        s.Heads.Any(h => h.OrganizationId == e.OrganizationId && h.FormRecordId == e.FormRecordId && h.ContentHeadQuarantined)
        || s.Resources.Any(r => r.OrganizationId == e.OrganizationId && r.ResourceTypeName == "form_record" && r.ResourceEntityId == e.FormRecordId && r.ResourceLifecycleState == "Quarantined");

    public Task<List<SearchRecordEntry>> GetEntriesForDeepCheckAsync(Guid organizationId, int limit, CancellationToken cancellationToken) =>
        Task.FromResult(Entries.Where(e => e.OrganizationId == organizationId && !RecordQuarantined(e)).OrderBy(e => e.LastDeepVerifiedOn).ThenBy(e => e.SearchRecordEntryId).Take(limit).ToList());

    public Task<List<SearchRecordEntry>> GetRandomEntriesAsync(Guid organizationId, int limit, CancellationToken cancellationToken) =>
        Task.FromResult(Entries.Where(e => e.OrganizationId == organizationId && !RecordQuarantined(e)).OrderBy(_ => Random.Shared.Next()).Take(limit).ToList());

    public void AddResultSnapshot(SearchResultSnapshot snapshot, IReadOnlyList<SearchResultSnapshotEntry> entries)
    {
        ResultSnapshots.Add(snapshot);
        ResultSnapshotEntries.AddRange(entries);
    }

    public Task<SearchResultSnapshot?> FindResultSnapshotAsync(Guid searchResultSnapshotId, CancellationToken cancellationToken) =>
        Task.FromResult(ResultSnapshots.FirstOrDefault(r => r.SearchResultSnapshotId == searchResultSnapshotId));

    public Task<SearchResultSnapshot?> MarkResultSnapshotServedAsync(Guid searchResultSnapshotId, DateTimeOffset servedOn, CancellationToken cancellationToken)
    {
        var snapshot = ResultSnapshots.FirstOrDefault(r => r.SearchResultSnapshotId == searchResultSnapshotId);
        if (snapshot is not null)
        {
            snapshot.LastPageServedOn = servedOn;
        }

        return Task.FromResult(snapshot);
    }

    public Task<List<SearchResultSnapshotEntry>> GetResultSnapshotEntriesAsync(Guid searchResultSnapshotId, int offset, int count, CancellationToken cancellationToken) =>
        Task.FromResult(ResultSnapshotEntries.Where(e => e.SearchResultSnapshotId == searchResultSnapshotId).OrderBy(e => e.ResultEntryPosition).Skip(offset).Take(count).ToList());

    public Task<int> DeleteResultSnapshotAsync(Guid searchResultSnapshotId, CancellationToken cancellationToken)
    {
        ResultSnapshotEntries.RemoveAll(e => e.SearchResultSnapshotId == searchResultSnapshotId);
        return Task.FromResult(ResultSnapshots.RemoveAll(r => r.SearchResultSnapshotId == searchResultSnapshotId));
    }

    public Task<int> EvictSessionSnapshotsAsync(Guid authenticatedSessionId, int keep, CancellationToken cancellationToken)
    {
        var evicted = ResultSnapshots.Where(r => r.AuthenticatedSessionId == authenticatedSessionId).OrderByDescending(r => r.SnapshotCreatedOn).ThenByDescending(r => r.SearchResultSnapshotId).Skip(Math.Max(0, keep)).Select(r => r.SearchResultSnapshotId).ToList();
        foreach (var id in evicted)
        {
            ResultSnapshotEntries.RemoveAll(e => e.SearchResultSnapshotId == id);
            ResultSnapshots.RemoveAll(r => r.SearchResultSnapshotId == id);
        }

        return Task.FromResult(evicted.Count);
    }

    public Task<long> CountLiveResultEntriesAsync(string creatingInstanceName, DateTimeOffset now, CancellationToken cancellationToken) =>
        Task.FromResult(ResultSnapshots.Where(r => r.CreatingInstanceName == creatingInstanceName && r.SnapshotExpiresOn > now).Sum(r => (long)r.ResultEntryCount));

    public Task<int> PurgeResultSnapshotsAsync(DateTimeOffset now, int batchSize, CancellationToken cancellationToken)
    {
        var purged = ResultSnapshots.Where(r => r.SnapshotExpiresOn <= now).Take(batchSize).Select(r => r.SearchResultSnapshotId).ToList();
        foreach (var id in purged)
        {
            ResultSnapshotEntries.RemoveAll(e => e.SearchResultSnapshotId == id);
            ResultSnapshots.RemoveAll(r => r.SearchResultSnapshotId == id);
        }

        return Task.FromResult(purged.Count);
    }

    public Task RecordDeepCheckAsync(Guid searchRecordEntryId, DateTimeOffset? verifiedOn, DateTimeOffset attemptedOn, string outcomeCode, CancellationToken cancellationToken)
    {
        var entry = Entries.Single(e => e.SearchRecordEntryId == searchRecordEntryId);
        if (verifiedOn is { } verified)
        {
            entry.LastDeepVerifiedOn = verified;
        }

        entry.LastDeepAttemptedOn = attemptedOn;
        entry.LastDeepAttemptOutcomeCode = outcomeCode;
        return Task.CompletedTask;
    }

    public Task<SearchScopeTimestamps> GetScopeTimestampsAsync(Guid organizationId, CancellationToken cancellationToken)
    {
        var entries = Entries.Where(e => e.OrganizationId == organizationId).ToList();
        var counted = entries.Where(e => !RecordQuarantined(e)).ToList();
        return Task.FromResult(entries.Count == 0
            ? new SearchScopeTimestamps(0, null, null, null, null, 0, 0)
            : new SearchScopeTimestamps(entries.Count, entries.Min(e => e.LastSealVerifiedOn), entries.Max(e => e.LastSealVerifiedOn),
                counted.Count == 0 ? null : counted.Min(e => e.LastDeepVerifiedOn), counted.Count == 0 ? null : counted.Max(e => e.LastDeepVerifiedOn), counted.Count, entries.Count - counted.Count));
    }
}
