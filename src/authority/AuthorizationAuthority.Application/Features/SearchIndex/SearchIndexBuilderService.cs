using AuthorizationAuthority.Application.Common;
using AuthorizationAuthority.Application.Common.Ports;
using AuthorizationAuthority.Application.Features.Content;
using AuthorizationAuthority.Application.Features.Journal;
using AuthorizationAuthority.Contracts.V1;
using AuthorizationAuthority.Domain.Features.Content;
using AuthorizationAuthority.Domain.Features.FieldSearch;
using AuthorizationAuthority.Domain.Features.Organizations;
using AuthorizationAuthority.Domain.Features.SearchIndex;
using EnterpriseForms.Integrity;

namespace AuthorizationAuthority.Application.Features.SearchIndex;

/// <summary>
/// IX-2 (design §7, §12, §17.10; R1 §C.3, §C.4, §C.6, §C.7; IX-D6): the Authority side of the <c>search-index-builder</c>
/// workload. The builder reads business bytes and submits them; everything that decides what the index contains happens here:
/// <list type="bullet">
/// <item><b>Backfill</b>: each submitted record is verified exactly like a G19 census (registry, quarantine, archive receipt,
/// key, complete leaf set, every HMAC, root, the head the builder expected); only then are postings derived from the verified
/// bytes and sealed, under the organization lock and conditional on the head being unchanged. Unverifiable bytes are refused and
/// quarantine the record (existing rule). A moved head is <c>HEAD_CHANGED</c> and never overwrites an inline entry.</item>
/// <item><b>Attestation</b>: <c>Complete</c> is set only here, from the Authority's own registry, heads and entries under the
/// organization lock. A withheld or quarantined record keeps the coverage Building.</item>
/// <item><b>Deep sweep (S-C)</b>: the Authority chooses the entries (oldest first plus its own random sample); the builder's
/// bytes are verified against the head, postings are re-derived and compared with the stored ones. Pass writes that entry's
/// S-C timestamp only; a posting mismatch is a Fail (index quarantine); anything that cannot reach a result is Inconclusive.</item>
/// </list>
/// Nothing here accepts a posting, an entry, a seal, a coverage state or a value from the builder.
/// </summary>
public sealed class SearchIndexBuilderService(IAuthorityStore store, ContentService content, SearchIndexWriter writer, SearchIndexOptions options, TimeProvider clock, ISecurityAlertSink alerts)
{
    public const int MaximumPopulationPage = 500;
    public const int MaximumDeepSweepSelection = 200;

    public const string Indexed = "Indexed";
    public const string AlreadyIndexed = "AlreadyIndexed";
    public const string NotCovered = "NOT_COVERED";

    private static readonly string[] PopulationStates = [ResourceLifecycleStates.Active, ResourceLifecycleStates.Quarantined, ResourceLifecycleStates.Registered];

    public async Task<SearchIndexBuilderWorkResponse> WorkAsync(Guid correlationId, CancellationToken cancellationToken)
    {
        RequireDark();
        await using var scope = await store.BeginScopeAsync(cancellationToken).ConfigureAwait(false);
        var forms = await scope.SearchIndex.GetBuildingFormsAsync(cancellationToken).ConfigureAwait(false);
        var organizations = await scope.SearchIndex.GetIndexedOrganizationIdsAsync(cancellationToken).ConfigureAwait(false);
        return new SearchIndexBuilderWorkResponse(forms.Select(f => new SearchIndexBuildingForm(f.OrganizationId, f.FormDefinitionId, f.BuildingCoverageCount, f.ProjectionStatusName)).ToList(), organizations, correlationId);
    }

    /// <summary>A page of the form's registered population (Active, Quarantined and pending records), record id order.</summary>
    public async Task<SearchIndexPopulationPage> PopulationAsync(string workloadName, SearchIndexPopulationRequest request, Guid correlationId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workloadName);
        ArgumentNullException.ThrowIfNull(request);
        RequireDark();
        if (request.Limit is < 1 or > MaximumPopulationPage)
        {
            throw AuthorityException.Invalid($"limit must be 1..{MaximumPopulationPage}");
        }

        await using var scope = await store.BeginScopeAsync(cancellationToken).ConfigureAwait(false);
        var state = await scope.LockOrganizationAsync(request.OrganizationId, cancellationToken).ConfigureAwait(false)
                    ?? throw AuthorityException.NotRegistered("organization is not registered");
        OrganizationGate.RequireOpen(await OrganizationGate.EvaluateAsync(scope, state, cancellationToken).ConfigureAwait(false));
        var index = scope.SearchIndex;
        await RequireServableProjectionAsync(index, state.OrganizationId, cancellationToken).ConfigureAwait(false);
        await index.BindOrganizationAsync(state.OrganizationId, cancellationToken).ConfigureAwait(false);

        var required = await RequiredApprovalIdsAsync(index, state.OrganizationId, request.FormDefinitionId, cancellationToken).ConfigureAwait(false);
        var population = (await scope.GetFormRecordResourcesAsync(state.OrganizationId, request.FormDefinitionId, PopulationStates, cancellationToken).ConfigureAwait(false))
            .OrderBy(r => r.ResourceEntityId).ToList();
        var page = population.Where(r => request.AfterFormRecordId is not { } after || r.ResourceEntityId.CompareTo(after) > 0).Take(request.Limit).ToList();
        var ids = page.Select(r => r.ResourceEntityId).ToList();
        var heads = ids.Count == 0 ? [] : (await scope.GetContentHeadsAsync(state.OrganizationId, ids, cancellationToken).ConfigureAwait(false)).ToDictionary(h => h.FormRecordId);
        var entries = ids.Count == 0 ? [] : (await index.GetFormEntriesAsync(state.OrganizationId, request.FormDefinitionId, ids, cancellationToken).ConfigureAwait(false)).ToDictionary(e => e.FormRecordId);

        var result = new List<SearchIndexPopulationEntry>(page.Count);
        foreach (var resource in page)
        {
            var head = heads.GetValueOrDefault(resource.ResourceEntityId);
            var contentful = head is { CommittedContentRevision: > 0, CommittedOperationId: not null } && resource.ResourceLifecycleState != ResourceLifecycleStates.Registered;
            var quarantined = resource.ResourceLifecycleState == ResourceLifecycleStates.Quarantined || head?.ContentHeadQuarantined == true;
            var indexed = contentful && IsIndexedAtHead(entries.GetValueOrDefault(resource.ResourceEntityId), head!, required);
            result.Add(new SearchIndexPopulationEntry(resource.ResourceEntityId, head?.CommittedContentRevision ?? 0, head?.CommittedOperationId, head?.ContentHeadQuarantined == true,
                resource.ResourceLifecycleState == ResourceLifecycleStates.Quarantined, contentful && !quarantined && !indexed && required.Count > 0));
        }

        var next = page.Count == request.Limit && population.Count > 0 && page[^1].ResourceEntityId != population[^1].ResourceEntityId ? page[^1].ResourceEntityId : (Guid?)null;
        if (request.AfterFormRecordId is null)
        {
            // The first page of a round is journaled (counts only), as the migration catalogue's issuance is (OD-1 pattern).
            AuditJournal.Append(scope, state, "SearchIndexPopulationIssued", new { organization_id = state.OrganizationId, form_definition_id = request.FormDefinitionId, registered_records = population.Count, building_requirement = required.Count, workload_name = workloadName, correlation_id = correlationId }, clock);
            await scope.CommitAsync(cancellationToken).ConfigureAwait(false);
        }

        return new SearchIndexPopulationPage(state.OrganizationId, request.FormDefinitionId, result, next, correlationId);
    }

    /// <summary>Verifies 1..50 submitted records and derives their entries from the verified bytes (one transaction, organization lock).</summary>
    public async Task<SearchIndexBackfillResponse> BackfillAsync(string workloadName, SearchIndexBackfillRequest request, Guid correlationId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workloadName);
        ArgumentNullException.ThrowIfNull(request);
        RequireDark();
        RequireBatch(request.Records);

        await using var scope = await store.BeginScopeAsync(cancellationToken).ConfigureAwait(false);
        var state = await scope.LockOrganizationAsync(request.OrganizationId, cancellationToken).ConfigureAwait(false)
                    ?? throw AuthorityException.NotRegistered("organization is not registered");
        OrganizationGate.RequireOpen(await OrganizationGate.EvaluateAsync(scope, state, cancellationToken).ConfigureAwait(false));
        var index = scope.SearchIndex;
        await RequireServableProjectionAsync(index, state.OrganizationId, cancellationToken).ConfigureAwait(false);
        await index.BindOrganizationAsync(state.OrganizationId, cancellationToken).ConfigureAwait(false);

        var required = await RequiredApprovalIdsAsync(index, state.OrganizationId, request.FormDefinitionId, cancellationToken).ConfigureAwait(false);
        var existing = (await index.GetFormEntriesAsync(state.OrganizationId, request.FormDefinitionId, request.Records.Select(r => r.FormRecordId).ToList(), cancellationToken).ConfigureAwait(false)).ToDictionary(e => e.FormRecordId);
        var context = new ContentService.WorkloadVerificationContext(workloadName, null, correlationId);
        var results = new List<SearchIndexBackfillResult>(request.Records.Count);
        var changed = false;
        foreach (var item in request.Records)
        {
            if (required.Count == 0)
            {
                results.Add(new SearchIndexBackfillResult(item.FormRecordId, NotCovered, 0, null, false));
                continue;
            }

            // Verification first, always: the builder's bytes are untrusted until they are exactly the accepted signed rows.
            var verification = await content.VerifyWorkloadObservedContentAsync(scope, state, request.FormDefinitionId, item.FormRecordId, item.ExpectedCommittedOperationId, item.ObservedItems ?? [], context, cancellationToken).ConfigureAwait(false);
            if (!verification.Verified)
            {
                changed |= verification.Quarantined;
                results.Add(new SearchIndexBackfillResult(item.FormRecordId, verification.Outcome, verification.CommittedContentRevision, verification.CommittedOperationId, verification.Quarantined));
                continue;
            }

            var head = verification.Head!;
            if (IsIndexedAtHead(existing.GetValueOrDefault(item.FormRecordId), head, required))
            {
                // Idempotent per (record, head operation, coverage set): a replay or an inline entry is never rewritten.
                results.Add(new SearchIndexBackfillResult(item.FormRecordId, AlreadyIndexed, head.CommittedContentRevision, head.CommittedOperationId, false));
                continue;
            }

            await writer.DeriveForBackfillAsync(scope, state, new CommitIndexInput(item.FormRecordId, head, item.ObservedItems!, clock.GetUtcNow(), request.FormDefinitionId), cancellationToken).ConfigureAwait(false);
            changed = true;
            results.Add(new SearchIndexBackfillResult(item.FormRecordId, Indexed, head.CommittedContentRevision, head.CommittedOperationId, false));
        }

        if (changed)
        {
            await scope.CommitAsync(cancellationToken).ConfigureAwait(false);
        }

        return new SearchIndexBackfillResponse(state.OrganizationId, request.FormDefinitionId, results, correlationId);
    }

    /// <summary>
    /// Attests the form's Building coverages from Authority state alone, under the organization lock (journaled before return).
    /// Complete requires every contentful registered record to have an entry at its head under every non-withdrawn coverage of
    /// the form, and no record of the form to be quarantined. A Rebuilding projection becomes Active when no coverage of the
    /// organization remains Building.
    /// </summary>
    public async Task<SearchIndexAttestation> AttestAsync(string workloadName, SearchIndexAttestationRequest request, Guid correlationId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workloadName);
        ArgumentNullException.ThrowIfNull(request);
        RequireDark();
        await using var scope = await store.BeginScopeAsync(cancellationToken).ConfigureAwait(false);
        var state = await scope.LockOrganizationAsync(request.OrganizationId, cancellationToken).ConfigureAwait(false)
                    ?? throw AuthorityException.NotRegistered("organization is not registered");
        OrganizationGate.RequireOpen(await OrganizationGate.EvaluateAsync(scope, state, cancellationToken).ConfigureAwait(false));
        var index = scope.SearchIndex;
        var projection = await RequireServableProjectionAsync(index, state.OrganizationId, cancellationToken).ConfigureAwait(false);
        await index.BindOrganizationAsync(state.OrganizationId, cancellationToken).ConfigureAwait(false);

        var coverages = await index.GetCoveragesAsync(state.OrganizationId, null, cancellationToken).ConfigureAwait(false);
        var formCoverages = coverages.Where(c => c.FormDefinitionId == request.FormDefinitionId && c.CoverageStateName != SearchCoverageStates.Withdrawn).ToList();
        var building = formCoverages.Where(c => c.CoverageStateName == SearchCoverageStates.Building).ToList();
        var required = formCoverages.Select(c => c.FieldSearchApprovalId).ToHashSet();

        var population = await scope.GetFormRecordResourcesAsync(state.OrganizationId, request.FormDefinitionId, PopulationStates, cancellationToken).ConfigureAwait(false);
        var ids = population.Select(r => r.ResourceEntityId).ToList();
        var heads = ids.Count == 0 ? [] : (await scope.GetContentHeadsAsync(state.OrganizationId, ids, cancellationToken).ConfigureAwait(false)).ToDictionary(h => h.FormRecordId);
        var entries = (await index.GetFormEntriesAsync(state.OrganizationId, request.FormDefinitionId, null, cancellationToken).ConfigureAwait(false)).ToDictionary(e => e.FormRecordId);
        int contentful = 0, indexed = 0, quarantined = 0, pending = 0;
        foreach (var resource in population)
        {
            var head = heads.GetValueOrDefault(resource.ResourceEntityId);
            if (resource.ResourceLifecycleState == ResourceLifecycleStates.Quarantined || head?.ContentHeadQuarantined == true)
            {
                quarantined++;
                continue;
            }

            if (resource.ResourceLifecycleState == ResourceLifecycleStates.Registered || head is not { CommittedContentRevision: > 0, CommittedOperationId: not null })
            {
                pending++; // no accepted content: its first commit derives inline (it can match nothing until then)
                continue;
            }

            contentful++;
            indexed += IsIndexedAtHead(entries.GetValueOrDefault(resource.ResourceEntityId), head, required) ? 1 : 0;
        }

        var missing = contentful - indexed;
        var reason = building.Count == 0 ? "NO_BUILDING_COVERAGE" : quarantined > 0 ? "RECORD_QUARANTINED" : missing > 0 ? "RECORDS_NOT_INDEXED" : null;
        var complete = building.Count > 0 && reason is null;
        Guid? attestedJournalEntryId = null;
        if (building.Count > 0)
        {
            var now = clock.GetUtcNow();
            var journal = AuditJournal.Append(scope, state, "SearchIndexCoverageAttested", new
            {
                organization_id = state.OrganizationId, form_definition_id = request.FormDefinitionId, coverage_complete = complete, incomplete_reason_code = reason,
                registered_records = population.Count, contentful_records = contentful, indexed_records = indexed, missing_records = missing, quarantined_records = quarantined, pending_records = pending,
                building_coverages = building.Select(c => c.SearchFieldCoverageId).Order().ToList(), authorization_revision = state.AuthorizationRevision, content_integrity_revision = state.ContentIntegrityRevision,
                workload_name = workloadName, correlation_id = correlationId,
            }, clock);
            attestedJournalEntryId = journal.SecurityJournalEntryId;
            if (complete)
            {
                foreach (var coverage in building)
                {
                    coverage.CoverageStateName = SearchCoverageStates.Complete;
                    coverage.CoverageCompletedOn = now;
                    coverage.CoverageAttestedJournalEntryId = journal.SecurityJournalEntryId;
                }

                if (projection.ProjectionStatusName == SearchProjectionStatuses.Rebuilding && !coverages.Any(c => c.CoverageStateName == SearchCoverageStates.Building))
                {
                    projection.ProjectionStatusName = SearchProjectionStatuses.Active;
                    projection.ProjectionStatusReasonCode = null;
                    projection.ProjectionModifiedOn = now;
                    AuditJournal.Append(scope, state, "SearchIndexRebuilt", new { organization_id = state.OrganizationId, completed_on = now, correlation_id = correlationId }, clock);
                }
            }

            await scope.CommitAsync(cancellationToken).ConfigureAwait(false);
        }

        return new SearchIndexAttestation(state.OrganizationId, request.FormDefinitionId, complete, reason, population.Count, contentful, indexed, missing, quarantined, pending, complete ? building.Count : 0,
            projection.ProjectionStatusName, attestedJournalEntryId, correlationId);
    }

    /// <summary>S-C selection: the organization's entries with the oldest deep verification, plus a random sample, chosen here.</summary>
    public async Task<SearchIndexDeepSweepWorkResponse> DeepSweepWorkAsync(SearchIndexDeepSweepWorkRequest request, Guid correlationId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        RequireDark();
        if (request.OldestLimit is < 1 or > MaximumDeepSweepSelection || request.SampleLimit is < 0 or > MaximumDeepSweepSelection)
        {
            throw AuthorityException.Invalid($"selection limits must be 1..{MaximumDeepSweepSelection} (oldest) and 0..{MaximumDeepSweepSelection} (sample)");
        }

        await using var scope = await store.BeginScopeAsync(cancellationToken).ConfigureAwait(false);
        var index = scope.SearchIndex;
        var projection = await index.FindProjectionStateAsync(request.OrganizationId, cancellationToken).ConfigureAwait(false);
        if (projection is null || projection.ProjectionStatusName == SearchProjectionStatuses.Quarantined)
        {
            return new SearchIndexDeepSweepWorkResponse(request.OrganizationId, [], correlationId); // a quarantine is cleared only by a rebuild
        }

        await index.BindOrganizationAsync(request.OrganizationId, cancellationToken).ConfigureAwait(false);
        var oldest = await index.GetEntriesForDeepCheckAsync(request.OrganizationId, request.OldestLimit, cancellationToken).ConfigureAwait(false);
        var sample = request.SampleLimit == 0 ? [] : await index.GetRandomEntriesAsync(request.OrganizationId, request.SampleLimit, cancellationToken).ConfigureAwait(false);
        var chosen = oldest.Concat(sample).DistinctBy(e => e.SearchRecordEntryId).Select(e => new SearchIndexDeepSweepWorkItem(e.FormRecordId, e.FormDefinitionId, e.IndexedOperationId)).ToList();
        return new SearchIndexDeepSweepWorkResponse(request.OrganizationId, chosen, correlationId);
    }

    /// <summary>S-C for 1..50 entries: verify the submitted business bytes against the head, re-derive, compare with the stored postings.</summary>
    public async Task<SearchIndexDeepVerifyResponse> DeepVerifyAsync(string workloadName, SearchIndexDeepVerifyRequest request, Guid correlationId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workloadName);
        ArgumentNullException.ThrowIfNull(request);
        RequireDark();
        RequireBatch(request.Records);

        await using var scope = await store.BeginScopeAsync(cancellationToken).ConfigureAwait(false);
        var state = await scope.LockOrganizationAsync(request.OrganizationId, cancellationToken).ConfigureAwait(false)
                    ?? throw AuthorityException.NotRegistered("organization is not registered");
        OrganizationGate.RequireOpen(await OrganizationGate.EvaluateAsync(scope, state, cancellationToken).ConfigureAwait(false));
        var index = scope.SearchIndex;
        var projection = await RequireServableProjectionAsync(index, state.OrganizationId, cancellationToken).ConfigureAwait(false);
        await index.BindOrganizationAsync(state.OrganizationId, cancellationToken).ConfigureAwait(false);
        var approvals = (await scope.GetFieldSearchApprovalsAsync(state.OrganizationId, null, cancellationToken).ConfigureAwait(false)).ToDictionary(a => a.FieldSearchApprovalId);
        var context = new ContentService.WorkloadVerificationContext(workloadName, null, correlationId);
        var results = new List<SearchIndexDeepVerifyResult>(request.Records.Count);
        foreach (var item in request.Records)
        {
            var now = clock.GetUtcNow();
            var stored = await index.GetStoredRecordIndexAsync(state.OrganizationId, item.FormRecordId, cancellationToken).ConfigureAwait(false);
            if (stored is null)
            {
                results.Add(new SearchIndexDeepVerifyResult(item.FormRecordId, nameof(SweepUnitOutcome.Inconclusive), "ENTRY_NOT_PRESENT", false));
                continue;
            }

            var entry = stored.Entry;
            if (entry.IndexedOperationId != item.ExpectedCommittedOperationId)
            {
                // The builder compared an operation the entry no longer holds (the head moved and was re-derived inline).
                await index.RecordDeepCheckAsync(entry.SearchRecordEntryId, null, now, "ENTRY_CHANGED", cancellationToken).ConfigureAwait(false);
                results.Add(new SearchIndexDeepVerifyResult(item.FormRecordId, nameof(SweepUnitOutcome.Inconclusive), "ENTRY_CHANGED", false));
                continue;
            }

            var verification = await content.VerifyWorkloadObservedContentAsync(scope, state, entry.FormDefinitionId, item.FormRecordId, item.ExpectedCommittedOperationId, item.ObservedItems ?? [], context, cancellationToken).ConfigureAwait(false);
            if (!verification.Verified)
            {
                // Business bytes that do not verify quarantine the record (existing rule, §6.5); the postings cannot be compared.
                var code = verification.Quarantined ? "BUSINESS_BYTES_MISMATCH" : verification.Outcome;
                await index.RecordDeepCheckAsync(entry.SearchRecordEntryId, null, now, code, cancellationToken).ConfigureAwait(false);
                results.Add(new SearchIndexDeepVerifyResult(item.FormRecordId, nameof(SweepUnitOutcome.Inconclusive), code, verification.Quarantined));
                continue;
            }

            var (outcome, finding) = CompareWithStored(stored, verification.Head!, item.ObservedItems!, approvals);
            if (outcome == SweepUnitOutcome.Pass)
            {
                await index.RecordDeepCheckAsync(entry.SearchRecordEntryId, now, now, finding, cancellationToken).ConfigureAwait(false);
                results.Add(new SearchIndexDeepVerifyResult(item.FormRecordId, nameof(SweepUnitOutcome.Pass), finding, false));
                continue;
            }

            await index.RecordDeepCheckAsync(entry.SearchRecordEntryId, null, now, finding, cancellationToken).ConfigureAwait(false);
            results.Add(new SearchIndexDeepVerifyResult(item.FormRecordId, nameof(SweepUnitOutcome.Fail), finding, false));
            SearchIndexStateTransitions.Quarantine(scope, state, projection, finding, now, clock);
            await scope.CommitAsync(cancellationToken).ConfigureAwait(false);
            SearchIndexStateTransitions.RaiseQuarantine(alerts, state.OrganizationId, finding);
            return new SearchIndexDeepVerifyResponse(state.OrganizationId, results, projection.ProjectionStatusName, correlationId);
        }

        await scope.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new SearchIndexDeepVerifyResponse(state.OrganizationId, results, projection.ProjectionStatusName, correlationId);
    }

    /// <summary>
    /// S-C comparison: postings, anomalies and header facts re-derived (<c>SearchPostingDerivation/v1</c>) from the verified bytes
    /// under the approvals the entry was derived under must equal the stored rows exactly. Never repairs anything.
    /// </summary>
    public static (SweepUnitOutcome Outcome, string Code) CompareWithStored(StoredRecordIndex stored, RecordContentHead head, IReadOnlyList<StagedContentItem> verifiedItems, IReadOnlyDictionary<Guid, FieldSearchApproval> approvals)
    {
        ArgumentNullException.ThrowIfNull(stored);
        ArgumentNullException.ThrowIfNull(head);
        ArgumentNullException.ThrowIfNull(approvals);
        var entry = stored.Entry;
        if (head.CommittedOperationId != entry.IndexedOperationId || head.CommittedContentRevision != entry.IndexedContentRevision || !head.CommittedManifestRoot.AsSpan().SequenceEqual(entry.IndexedManifestRoot))
        {
            return (SweepUnitOutcome.Fail, "ENTRY_NOT_AT_HEAD");
        }

        var bound = new List<FieldSearchApproval>();
        foreach (var id in entry.DerivedFieldSearchApprovalIds)
        {
            if (!approvals.TryGetValue(id, out var approval) || approval.OrganizationId != entry.OrganizationId || approval.FormDefinitionId != entry.FormDefinitionId)
            {
                return (SweepUnitOutcome.Fail, "APPROVAL_OF_ENTRY_MISSING");
            }

            bound.Add(approval);
        }

        DerivedRecordContent derived;
        try
        {
            derived = SearchPostingDerivationV1.Derive(entry.OrganizationId, entry.FormDefinitionId, entry.FormRecordId, verifiedItems, bound.OrderBy(a => a.FieldSearchApprovalId).ToList());
        }
        catch (AuthorityException ex) when (ex.ErrorCode == AuthorityErrorCodes.SearchIndexDerivationFailed)
        {
            return (SweepUnitOutcome.Fail, "DERIVATION_FAILED");
        }
        catch (IntegrityFormatException)
        {
            return (SweepUnitOutcome.Fail, "DERIVATION_FAILED");
        }

        if (!string.Equals(derived.RecordStatusName, entry.RecordStatusName, StringComparison.Ordinal))
        {
            return (SweepUnitOutcome.Fail, "HEADER_FACT_MISMATCH");
        }

        // IX-3 (D-IX3-1): a version 2 entry's header bytes and decoded facts are compared too.
        if (entry.EntryDerivationVersion >= 2
            && (entry.HeaderSourceCanonicalBytes is null || !entry.HeaderSourceCanonicalBytes.AsSpan().SequenceEqual(derived.HeaderSourceCanonicalBytes)
                || entry.ParentFormRecordId != derived.ParentFormRecordId || entry.CreatedByBusinessUserId != derived.CreatedByUserId))
        {
            return (SweepUnitOutcome.Fail, "HEADER_FACT_MISMATCH");
        }

        if (derived.Postings.Count != stored.Postings.Count || derived.Postings.Count != entry.IndexedPostingCount)
        {
            return (SweepUnitOutcome.Fail, "POSTING_SET_MISMATCH");
        }

        var expected = derived.Postings.ToDictionary(p => (Convert.ToHexStringLower(p.ManifestItemKey), p.FieldSearchApprovalId));
        foreach (var posting in stored.Postings)
        {
            if (!expected.Remove((Convert.ToHexStringLower(posting.ManifestItemKey), posting.FieldSearchApprovalId), out var want)
                || posting.FieldDefinitionId != want.FieldDefinitionId || posting.ManifestItemId != want.ManifestItemId || !string.Equals(posting.SourceEntityKind, want.EntityKind, StringComparison.Ordinal)
                || posting.IndexedOperationId != entry.IndexedOperationId || posting.FormRecordId != entry.FormRecordId || posting.FormDefinitionId != entry.FormDefinitionId || posting.OrganizationId != entry.OrganizationId
                || !posting.SourceCanonicalBytes.AsSpan().SequenceEqual(want.SourceCanonicalBytes) || !TypedValueEquals(posting, want.Value))
            {
                return (SweepUnitOutcome.Fail, "POSTING_MISMATCH");
            }
        }

        var storedAnomalies = stored.Anomalies.Select(a => (a.FieldDefinitionId, a.AnomalyKindName)).Order().ToList();
        var derivedAnomalies = derived.Anomalies.Select(a => (a.FieldDefinitionId, a.AnomalyKind)).Order().ToList();
        if (!storedAnomalies.SequenceEqual(derivedAnomalies) || storedAnomalies.Count != entry.IndexedAnomalyCount)
        {
            return (SweepUnitOutcome.Fail, "ANOMALY_MISMATCH");
        }

        return (SweepUnitOutcome.Pass, "MATCH");
    }

    private static bool TypedValueEquals(SearchPostingBase posting, EnterpriseForms.Integrity.Search.SearchSealValue value) => (posting, value) switch
    {
        (SearchStringPosting p, EnterpriseForms.Integrity.Search.SearchSealValue.NormalizedString v) => string.Equals(p.NormalizedStringValue, v.Value, StringComparison.Ordinal),
        (SearchIntegerPosting p, EnterpriseForms.Integrity.Search.SearchSealValue.IntegerValue v) => p.IntegerValue == v.Value,
        (SearchDecimalPosting p, EnterpriseForms.Integrity.Search.SearchSealValue.DecimalParts v) => p.DecimalValue == SearchIndexValues.ToDecimal(v),
        (SearchDatePosting p, EnterpriseForms.Integrity.Search.SearchSealValue.DateValue v) => p.DateValue == v.Value,
        (SearchDateTimePosting p, EnterpriseForms.Integrity.Search.SearchSealValue.Timestamp v) => p.DateTimeValue.UtcTicks == v.Value.UtcTicks,
        (SearchBooleanPosting p, EnterpriseForms.Integrity.Search.SearchSealValue.BooleanValue v) => p.BooleanValue == v.Value,
        (SearchOptionPosting p, EnterpriseForms.Integrity.Search.SearchSealValue.OptionValue v) => p.FieldOptionId == v.FieldOptionId,
        _ => false,
    };

    /// <summary>An entry counts as indexed when it is at the head and was derived under every required (non-withdrawn) coverage approval.</summary>
    public static bool IsIndexedAtHead(SearchRecordEntry? entry, RecordContentHead head, IReadOnlySet<Guid> requiredApprovalIds)
    {
        ArgumentNullException.ThrowIfNull(head);
        ArgumentNullException.ThrowIfNull(requiredApprovalIds);
        // IX-3 (D-IX3-1): an entry of an older derivation version is not "indexed": the builder re-derives it, attestation waits for it.
        return entry is not null && entry.EntryDerivationVersion >= SearchPostingDerivationV1.CurrentEntryDerivationVersion
               && head.CommittedOperationId == entry.IndexedOperationId && head.CommittedContentRevision == entry.IndexedContentRevision
               && head.CommittedManifestRoot.AsSpan().SequenceEqual(entry.IndexedManifestRoot) && requiredApprovalIds.All(entry.DerivedFieldSearchApprovalIds.Contains);
    }

    private static async Task<HashSet<Guid>> RequiredApprovalIdsAsync(ISearchIndexStore index, Guid organizationId, Guid formDefinitionId, CancellationToken cancellationToken) =>
        (await index.GetCoveragesAsync(organizationId, formDefinitionId, cancellationToken).ConfigureAwait(false))
        .Where(c => c.CoverageStateName != SearchCoverageStates.Withdrawn).Select(c => c.FieldSearchApprovalId).ToHashSet();

    private static async Task<SearchProjectionState> RequireServableProjectionAsync(ISearchIndexStore index, Guid organizationId, CancellationToken cancellationToken)
    {
        var projection = await index.FindProjectionStateAsync(organizationId, cancellationToken).ConfigureAwait(false)
                         ?? throw new AuthorityException(AuthorityErrorCodes.SearchIndexNotPresent, 404, false, "the organization has no search index");
        if (projection.ProjectionStatusName == SearchProjectionStatuses.Quarantined)
        {
            // IX-D7: a quarantined index is cleared only by SearchProjectionRebuild; nothing is built into it meanwhile.
            throw new AuthorityException(AuthorityErrorCodes.SearchIndexQuarantined, 409, false, "the search index is quarantined; a SearchProjectionRebuild is required");
        }

        return projection;
    }

    private void RequireDark()
    {
        if (options.Mode != SearchIndexMode.Dark)
        {
            throw new AuthorityException(AuthorityErrorCodes.SearchIndexNotPresent, 404, false, "the search index is not maintained in this deployment");
        }
    }

    private static void RequireBatch(IReadOnlyList<SearchIndexBackfillItem>? records)
    {
        if (records is null || records.Count is 0 or > ContentService.MaximumBatchRecords)
        {
            throw AuthorityException.Invalid($"records must contain 1..{ContentService.MaximumBatchRecords} items");
        }

        if (records.Select(r => r.FormRecordId).Distinct().Count() != records.Count)
        {
            throw AuthorityException.Invalid("each record may appear once per batch");
        }

        if (records.Any(r => r.ObservedItems is null))
        {
            // An empty list is accepted on purpose: rows deleted from the business database must reach verification (MISSING_ITEM).
            throw AuthorityException.Invalid("every record must carry its observed items (possibly none)");
        }
    }
}

/// <summary>IX-1/IX-2: the search-index quarantine transition (a Fail, §17.10.2), shared by the sweeps and the deep comparison.</summary>
public static class SearchIndexStateTransitions
{
    public static void Quarantine(IAuthorityScope scope, OrganizationAuthorizationState state, SearchProjectionState projection, string code, DateTimeOffset now, TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(projection);
        // The search index only, never the organization (S1 is untouched); cleared only by SearchProjectionRebuild (IX-D7).
        projection.ProjectionStatusName = SearchProjectionStatuses.Quarantined;
        projection.ProjectionStatusReasonCode = code.Length > 64 ? code[..64] : code;
        projection.ProjectionModifiedOn = now;
        AuditJournal.Append(scope, state, "SearchIndexQuarantined", new { organization_id = state.OrganizationId, reason_code = code, quarantined_on = now }, clock);
    }

    public static void RaiseQuarantine(ISecurityAlertSink alerts, Guid organizationId, string code)
    {
        ArgumentNullException.ThrowIfNull(alerts);
        alerts.Raise(new SecurityAlert("SEARCH_INDEX_QUARANTINE", "Critical", organizationId, Guid.Empty, null, "Search index integrity finding " + code + "; the index is quarantined (organization access unaffected).", Guid.NewGuid()));
    }
}
