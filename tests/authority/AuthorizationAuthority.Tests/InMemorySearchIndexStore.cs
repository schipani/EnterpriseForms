using AuthorizationAuthority.Application.Common.Ports;
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

    public Task<List<SearchRecordEntry>> GetEntriesForSealCheckAsync(Guid organizationId, int limit, CancellationToken cancellationToken) =>
        Task.FromResult(Entries.Where(e => e.OrganizationId == organizationId).OrderBy(e => e.LastSealVerifiedOn).ThenBy(e => e.SearchRecordEntryId).Take(limit).ToList());

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
        return Task.FromResult(new CheapSweepFindings(notAtHead, inactive, unbound, withdrawn, noCoverage, crossOrganization, unsealed));
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

    public Task<SearchScopeTimestamps> GetScopeTimestampsAsync(Guid organizationId, CancellationToken cancellationToken)
    {
        var entries = Entries.Where(e => e.OrganizationId == organizationId).ToList();
        return Task.FromResult(entries.Count == 0
            ? new SearchScopeTimestamps(0, null, null, null, null)
            : new SearchScopeTimestamps(entries.Count, entries.Min(e => e.LastSealVerifiedOn), entries.Max(e => e.LastSealVerifiedOn), entries.Min(e => e.LastDeepVerifiedOn), entries.Max(e => e.LastDeepVerifiedOn)));
    }
}
