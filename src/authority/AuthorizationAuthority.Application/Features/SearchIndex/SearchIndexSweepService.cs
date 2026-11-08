using System.Security.Cryptography;
using AuthorizationAuthority.Application.Common;
using AuthorizationAuthority.Application.Common.Ports;
using AuthorizationAuthority.Application.Features.Journal;
using AuthorizationAuthority.Domain.Features.FieldSearch;
using AuthorizationAuthority.Domain.Features.Organizations;
using AuthorizationAuthority.Domain.Features.SearchIndex;
using EnterpriseForms.Integrity;
using EnterpriseForms.Integrity.Search;

namespace AuthorizationAuthority.Application.Features.SearchIndex;

/// <summary>The three outcome classes of a verification unit (design §17.10.2).</summary>
public enum SweepUnitOutcome
{
    Pass = 1,
    Fail = 2,
    Inconclusive = 3,
}

public sealed record SweepPassResult(string Sweep, SweepUnitOutcome Outcome, int UnitsPassed, int UnitsInconclusive, string? FindingCode);

public sealed record DeadlineEvaluationResult(bool Suspended, IReadOnlyList<string> OverdueScopes, bool Changed);

/// <summary>
/// IX-1: the cheap sweep and the deadline evaluation of the Authority search index (design §6.5, §17.5, §17.9, §17.10).
/// <list type="bullet">
/// <item><b>S-A</b> (per organization, under its lock, no HSM, no BusinessDb): coverage maintenance, INV-R reconciliation and
/// the set checks; a finding quarantines the index (never cleared automatically); a clean pass writes the organization scope
/// timestamp, and only a clean pass does (D1).</item>
/// <item><b>S-B</b> (per entry): re-derives every posting from its stored source bytes, compares the typed columns, checks each
/// posting against the accepted manifest leaf, rebuilds the seal message and verifies ES256 locally with the pinned key.
/// Pass writes that entry's timestamp; Fail quarantines; Inconclusive writes only the attempt.</item>
/// <item><b>Deadlines</b>: an overdue scope suspends indexed search; suspension clears only when every overdue scope has
/// passed again; clock anomalies never extend a deadline or clear a suspension (K1 to K9).</item>
/// </list>
/// S-C (deep comparison with BusinessDb bytes) is IX-2; its deadline is enforced from the derivation timestamps.
/// </summary>
public sealed class SearchIndexSweepService(IAuthorityStore store, SearchIndexWriter writer, SearchSealVerifier verifier, SearchIndexOptions options, TimeProvider clock, ISecurityAlertSink alerts)
{
    public const string CheapSweep = "S-A";
    public const string SealSweep = "S-B";
    public const string DeepSweep = "S-C";

    public async Task<IReadOnlyList<Guid>> IndexedOrganizationsAsync(CancellationToken cancellationToken)
    {
        await using var scope = await store.BeginScopeAsync(cancellationToken).ConfigureAwait(false);
        return await scope.SearchIndex.GetIndexedOrganizationIdsAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>S-A for one organization.</summary>
    public async Task<SweepPassResult> RunCheapPassAsync(Guid organizationId, CancellationToken cancellationToken)
    {
        await using var scope = await store.BeginScopeAsync(cancellationToken).ConfigureAwait(false);
        var state = await scope.LockOrganizationAsync(organizationId, cancellationToken).ConfigureAwait(false);
        var index = scope.SearchIndex;
        var projection = state is null ? null : await index.FindProjectionStateAsync(organizationId, cancellationToken).ConfigureAwait(false);
        if (state is null || projection is null || projection.ProjectionStatusName == SearchProjectionStatuses.Quarantined)
        {
            return new SweepPassResult(CheapSweep, SweepUnitOutcome.Inconclusive, 0, 0, projection is null ? "NOT_INDEXED" : "QUARANTINED");
        }

        var now = clock.GetUtcNow();
        await index.BindOrganizationAsync(organizationId, cancellationToken).ConfigureAwait(false);
        await writer.ReconcileCoveragesAsync(scope, state, now, cancellationToken).ConfigureAwait(false);

        if (projection.ProjectionStatusName == SearchProjectionStatuses.ReconciliationRequired)
        {
            // INV-R option (b): bring the index back to the current revision. Entries whose head moved without derivation are
            // deleted (nothing is re-derived here; no bytes are held) and their forms' coverages return to Building.
            var forms = await index.DeleteEntriesNotAtHeadAsync(organizationId, cancellationToken).ConfigureAwait(false);
            // IX-2: every Complete coverage returns to Building, not only those with a deleted entry: a record committed while
            // derivation was bypassed may have no entry at all, and Complete may be re-established only by a fresh attestation.
            foreach (var coverage in (await index.GetCoveragesAsync(organizationId, null, cancellationToken).ConfigureAwait(false)).Where(c => c.CoverageStateName == SearchCoverageStates.Complete))
            {
                coverage.CoverageStateName = SearchCoverageStates.Building;
                coverage.CoverageCompletedOn = null;
                coverage.CoverageAttestedJournalEntryId = null;
            }

            foreach (var form in forms)
            {
                await index.BumpFormStateAsync(organizationId, form, now, cancellationToken).ConfigureAwait(false);
            }

            AuditJournal.Append(scope, state, "SearchIndexReconciled", new { organization_id = organizationId, from_revision = projection.ReconciliationFromRevision, to_revision = state.ContentIntegrityRevision, stale_forms = forms.Count }, clock);
            projection.ProjectionStatusName = SearchProjectionStatuses.Active;
            projection.ProjectionStatusReasonCode = null;
            projection.ReconciliationFromRevision = null;
            projection.ProjectionContentIntegrityRevision = state.ContentIntegrityRevision;
            projection.ProjectionModifiedOn = now;
        }
        else if (projection.ProjectionContentIntegrityRevision != state.ContentIntegrityRevision)
        {
            // INV-R was bypassed (an old Authority image, out-of-band SQL, a defect): reconciliation, never quarantine (§18.2.2).
            projection.ProjectionStatusName = SearchProjectionStatuses.ReconciliationRequired;
            projection.ProjectionStatusReasonCode = "REVISION_MISMATCH";
            projection.ReconciliationFromRevision = projection.ProjectionContentIntegrityRevision;
            projection.ProjectionModifiedOn = now;
            AuditJournal.Append(scope, state, "SearchIndexReconciliationRequired", new { organization_id = organizationId, projection_revision = projection.ReconciliationFromRevision, content_revision = state.ContentIntegrityRevision }, clock);
            await scope.CommitAsync(cancellationToken).ConfigureAwait(false);
            alerts.Raise(new SecurityAlert("SEARCH_INDEX_RECONCILIATION_REQUIRED", "High", organizationId, Guid.Empty, null, "Search index revision behind the content revision; reconciliation scheduled.", Guid.NewGuid()));
            return new SweepPassResult(CheapSweep, SweepUnitOutcome.Inconclusive, 0, 0, "RECONCILIATION_REQUIRED");
        }

        var findings = await index.RunCheapChecksAsync(organizationId, cancellationToken).ConfigureAwait(false);
        if (!findings.Clean)
        {
            var code = findings.FirstFindingCode();
            Quarantine(scope, state, projection, code, now);
            await scope.CommitAsync(cancellationToken).ConfigureAwait(false);
            RaiseQuarantine(organizationId, code);
            return new SweepPassResult(CheapSweep, SweepUnitOutcome.Fail, 0, 0, code);
        }

        projection.LastSuccessfulCheapPassOn = now; // D1: only a fully clean pass for the whole organization writes this
        projection.ProjectionModifiedOn = now;
        await scope.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new SweepPassResult(CheapSweep, SweepUnitOutcome.Pass, 1, 0, null);
    }

    /// <summary>S-B for one organization: at most <see cref="SearchIndexOptions.SealCheckBatchSize"/> entries, oldest verification first.</summary>
    public async Task<SweepPassResult> RunSealPassAsync(Guid organizationId, CancellationToken cancellationToken)
    {
        await using var scope = await store.BeginScopeAsync(cancellationToken).ConfigureAwait(false);
        var state = await scope.LockOrganizationAsync(organizationId, cancellationToken).ConfigureAwait(false);
        var index = scope.SearchIndex;
        var projection = state is null ? null : await index.FindProjectionStateAsync(organizationId, cancellationToken).ConfigureAwait(false);
        if (state is null || projection is null || projection.ProjectionStatusName == SearchProjectionStatuses.Quarantined)
        {
            return new SweepPassResult(SealSweep, SweepUnitOutcome.Inconclusive, 0, 0, projection is null ? "NOT_INDEXED" : "QUARANTINED");
        }

        await index.BindOrganizationAsync(organizationId, cancellationToken).ConfigureAwait(false);
        var approvals = (await scope.GetFieldSearchApprovalsAsync(organizationId, null, cancellationToken).ConfigureAwait(false)).ToDictionary(a => a.FieldSearchApprovalId);
        var entries = await index.GetEntriesForSealCheckAsync(organizationId, Math.Max(1, options.SealCheckBatchSize), cancellationToken).ConfigureAwait(false);
        int passed = 0, inconclusive = 0;
        foreach (var entry in entries)
        {
            var now = clock.GetUtcNow();
            var (outcome, code) = await VerifyEntryAsync(scope, entry, approvals, cancellationToken).ConfigureAwait(false);
            switch (outcome)
            {
                case SweepUnitOutcome.Pass:
                    await index.RecordSealCheckAsync(entry.SearchRecordEntryId, now, now, "VALID", cancellationToken).ConfigureAwait(false);
                    passed++;
                    break;
                case SweepUnitOutcome.Inconclusive:
                    await index.RecordSealCheckAsync(entry.SearchRecordEntryId, null, now, code, cancellationToken).ConfigureAwait(false);
                    inconclusive++;
                    break;
                default:
                    await index.RecordSealCheckAsync(entry.SearchRecordEntryId, null, now, code, cancellationToken).ConfigureAwait(false);
                    Quarantine(scope, state, projection, code, now);
                    await scope.CommitAsync(cancellationToken).ConfigureAwait(false);
                    RaiseQuarantine(organizationId, code);
                    return new SweepPassResult(SealSweep, SweepUnitOutcome.Fail, passed, inconclusive, code);
            }
        }

        await scope.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new SweepPassResult(SealSweep, inconclusive > 0 ? SweepUnitOutcome.Inconclusive : SweepUnitOutcome.Pass, passed, inconclusive, inconclusive > 0 ? "SOME_UNITS_INCONCLUSIVE" : null);
    }

    /// <summary>Verifies one stored entry exactly (S-B). Never repairs anything.</summary>
    public async Task<(SweepUnitOutcome Outcome, string Code)> VerifyEntryAsync(IAuthorityScope scope, SearchRecordEntry entry, IReadOnlyDictionary<Guid, FieldSearchApproval> approvals, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(approvals);
        var stored = await scope.SearchIndex.GetStoredRecordIndexAsync(entry.OrganizationId, entry.FormRecordId, cancellationToken).ConfigureAwait(false);
        if (stored is null || stored.Entry.SearchRecordEntryId != entry.SearchRecordEntryId)
        {
            return (SweepUnitOutcome.Fail, "ENTRY_CHANGED_DURING_CHECK");
        }

        var head = await scope.FindContentHeadAsync(entry.OrganizationId, entry.FormRecordId, false, cancellationToken).ConfigureAwait(false);
        if (head is null || head.CommittedOperationId != entry.IndexedOperationId || head.CommittedContentRevision != entry.IndexedContentRevision
            || !head.CommittedManifestRoot.AsSpan().SequenceEqual(entry.IndexedManifestRoot) || head.CurrentRecordManifestCommitId is not { } commitId)
        {
            return (SweepUnitOutcome.Fail, "ENTRY_NOT_AT_HEAD");
        }

        var leaves = (await scope.GetManifestLeavesAsync(commitId, cancellationToken).ConfigureAwait(false)).ToDictionary(l => Convert.ToHexStringLower(l.ManifestItemKey), StringComparer.Ordinal);
        var bound = new List<FieldSearchApproval>();
        foreach (var id in entry.DerivedFieldSearchApprovalIds)
        {
            if (!approvals.TryGetValue(id, out var approval) || approval.OrganizationId != entry.OrganizationId || approval.FormDefinitionId != entry.FormDefinitionId)
            {
                return (SweepUnitOutcome.Fail, "APPROVAL_OF_ENTRY_MISSING");
            }

            bound.Add(approval);
        }

        var postings = new List<DerivedPosting>(stored.Postings.Count);
        try
        {
            foreach (var posting in stored.Postings)
            {
                var approval = bound.FirstOrDefault(a => a.FieldSearchApprovalId == posting.FieldSearchApprovalId && a.FieldDefinitionId == posting.FieldDefinitionId);
                if (approval is null || posting.IndexedOperationId != entry.IndexedOperationId || posting.OrganizationId != entry.OrganizationId || posting.FormDefinitionId != entry.FormDefinitionId)
                {
                    return (SweepUnitOutcome.Fail, "POSTING_BINDING_MISMATCH");
                }

                var (value, document) = SearchPostingDerivationV1.ValueFromSource(posting.SourceCanonicalBytes, approval.ApprovedFieldDataType);
                if (document.Header.OrganizationId != entry.OrganizationId || SearchPostingDerivationV1.FieldOf(document) != posting.FieldDefinitionId
                    || SearchPostingDerivationV1.RecordOf(document) != entry.FormRecordId || SearchPostingDerivationV1.ItemOf(document) != posting.ManifestItemId
                    || document.Header.EntityKind != posting.SourceEntityKind || !TypedColumnMatches(posting, value))
                {
                    return (SweepUnitOutcome.Fail, "POSTING_VALUE_MISMATCH");
                }

                var digest = SHA256.HashData(posting.SourceCanonicalBytes);
                if (!leaves.TryGetValue(Convert.ToHexStringLower(posting.ManifestItemKey), out var leaf) || !leaf.CanonicalPayloadDigest.AsSpan().SequenceEqual(digest))
                {
                    return (SweepUnitOutcome.Fail, "POSTING_NOT_IN_ACCEPTED_MANIFEST");
                }

                postings.Add(new DerivedPosting(posting.FieldDefinitionId, posting.FieldSearchApprovalId, posting.ManifestItemKey, posting.ManifestItemId, posting.SourceEntityKind, approval.ApprovedFieldDataType, posting.SourceCanonicalBytes, digest, value));
            }

            var anomalies = stored.Anomalies.Select(a => new SearchSealAnomaly(a.FieldDefinitionId, a.AnomalyKindName)).ToList();
            if (postings.Count != entry.IndexedPostingCount || anomalies.Count != entry.IndexedAnomalyCount)
            {
                return (SweepUnitOutcome.Fail, "ENTRY_COUNT_MISMATCH");
            }

            var message = SearchEntrySealBuilder.Message(entry, bound, postings, anomalies);
            return verifier.Verify(entry.SealKeyName, entry.SealKeyVersion, message, entry.EntrySealSignature) switch
            {
                SealVerificationOutcome.Valid => (SweepUnitOutcome.Pass, "VALID"),
                SealVerificationOutcome.UnknownKey => (SweepUnitOutcome.Inconclusive, "SEAL_KEY_NOT_PINNED"),
                _ => (SweepUnitOutcome.Fail, "SEAL_INVALID"),
            };
        }
        catch (AuthorityException ex) when (ex.ErrorCode == AuthorityErrorCodes.SearchIndexDerivationFailed)
        {
            return (SweepUnitOutcome.Fail, "POSTING_SOURCE_INVALID");
        }
        catch (IntegrityFormatException)
        {
            return (SweepUnitOutcome.Fail, "SEAL_MESSAGE_INVALID");
        }
        catch (System.Text.Json.JsonException)
        {
            return (SweepUnitOutcome.Fail, "APPROVAL_OPERATORS_INVALID");
        }
    }

    /// <summary>Deadline evaluation for one organization (§17.10 D2-D4, K1-K9). Writes only the projection's suspension state.</summary>
    public async Task<DeadlineEvaluationResult> EvaluateDeadlinesAsync(Guid organizationId, CancellationToken cancellationToken)
    {
        await using var scope = await store.BeginScopeAsync(cancellationToken).ConfigureAwait(false);
        var state = await scope.LockOrganizationAsync(organizationId, cancellationToken).ConfigureAwait(false);
        var index = scope.SearchIndex;
        var projection = state is null ? null : await index.FindProjectionStateAsync(organizationId, cancellationToken).ConfigureAwait(false);
        if (state is null || projection is null)
        {
            return new DeadlineEvaluationResult(false, [], false);
        }

        await index.BindOrganizationAsync(organizationId, cancellationToken).ConfigureAwait(false);
        var timestamps = await index.GetScopeTimestampsAsync(organizationId, cancellationToken).ConfigureAwait(false);
        var now = clock.GetUtcNow();
        var previousEvaluation = projection.LastEvaluationOn;
        var evaluationTime = previousEvaluation is { } last && last > now ? last : now; // K9 (a): monotonic
        var overdue = Overdue(projection, timestamps, now, evaluationTime);

        projection.LastEvaluationOn = evaluationTime;
        var changed = false;
        if (timestamps.DeepDeadlineExcludedEntryCount != projection.DeepDeadlineExcludedEntryCount)
        {
            // D-IX2-7 rule 6: any change of the set excluded because of record quarantine is evidence-bearing (journal and alert).
            AuditJournal.Append(scope, state, "SearchIndexDeepDeadlineExclusionChanged", new { organization_id = organizationId, previous_excluded_entries = projection.DeepDeadlineExcludedEntryCount, excluded_entries = timestamps.DeepDeadlineExcludedEntryCount, evaluated_on = evaluationTime }, clock);
            alerts.Raise(new SecurityAlert("SEARCH_INDEX_DEEP_EXCLUSION_CHANGED", "Warning", organizationId, Guid.Empty, null,
                $"Entries excluded from the deep-sweep deadline because their record is quarantined changed from {projection.DeepDeadlineExcludedEntryCount} to {timestamps.DeepDeadlineExcludedEntryCount}.", Guid.NewGuid()));
            projection.DeepDeadlineExcludedEntryCount = timestamps.DeepDeadlineExcludedEntryCount;
        }
        if (overdue.Count > 0)
        {
            var names = string.Join(",", overdue);
            if (!projection.VerificationSuspended)
            {
                projection.VerificationSuspended = true;
                changed = true;
                AuditJournal.Append(scope, state, "SearchIndexVerificationSuspended", new { organization_id = organizationId, overdue_scopes = overdue, evaluated_on = evaluationTime }, clock);
            }

            projection.SuspendedScopeNames = names;
        }
        else if (projection.VerificationSuspended && (previousEvaluation is null || now >= previousEvaluation))
        {
            // D4 / K5: every overdue scope has passed again (recomputed from unit timestamps). K9 (d): never on a clock that went back.
            projection.VerificationSuspended = false;
            projection.SuspendedScopeNames = null;
            changed = true;
            AuditJournal.Append(scope, state, "SearchIndexVerificationResumed", new { organization_id = organizationId, evaluated_on = evaluationTime }, clock);
        }

        projection.ProjectionModifiedOn = now;
        await scope.CommitAsync(cancellationToken).ConfigureAwait(false);
        if (changed && projection.VerificationSuspended)
        {
            alerts.Raise(new SecurityAlert("SEARCH_INDEX_VERIFICATION_OVERDUE", "High", organizationId, Guid.Empty, null, "Search index verification overdue (" + projection.SuspendedScopeNames + "); indexed search suspended.", Guid.NewGuid()));
        }

        return new DeadlineEvaluationResult(projection.VerificationSuspended, overdue, changed);
    }

    /// <summary>The overdue scope kinds (D2, K8): a deadline is exceeded, or a timestamp lies too far in the future (Inconclusive).</summary>
    public IReadOnlyList<string> Overdue(SearchProjectionState projection, SearchScopeTimestamps timestamps, DateTimeOffset now, DateTimeOffset evaluationTime)
    {
        ArgumentNullException.ThrowIfNull(projection);
        ArgumentNullException.ThrowIfNull(timestamps);
        var tolerance = TimeSpan.FromSeconds(Math.Max(0, options.FutureTimestampToleranceSeconds));
        var overdue = new List<string>();
        bool Exceeded(DateTimeOffset? oldest, DateTimeOffset? newest, int deadlineSeconds) =>
            oldest is null || (newest ?? oldest) > now + tolerance || oldest > now + tolerance || evaluationTime - oldest.Value > TimeSpan.FromSeconds(deadlineSeconds);

        var cheapBaseline = projection.LastSuccessfulCheapPassOn ?? projection.ProjectionCreatedOn;
        if (Exceeded(cheapBaseline, cheapBaseline, options.CheapPassDeadlineSeconds))
        {
            overdue.Add(CheapSweep);
        }

        if (timestamps.EntryCount > 0)
        {
            if (Exceeded(timestamps.OldestSealVerifiedOn, timestamps.NewestSealVerifiedOn, options.SealVerificationDeadlineSeconds))
            {
                overdue.Add(SealSweep);
            }

            // D-IX2-7 (C-129): entries of Authority-quarantined records are not counted for the S-C deadline. When every entry is
            // excluded there is no S-C unit to be overdue. S-A and S-B still cover every entry (exclusion is never verification).
            if (timestamps.DeepEntriesCounted > 0 && Exceeded(timestamps.OldestDeepVerifiedOn, timestamps.NewestDeepVerifiedOn, options.DeepVerificationDeadlineSeconds))
            {
                overdue.Add(DeepSweep);
            }
        }

        return overdue;
    }

    private static bool TypedColumnMatches(SearchPostingBase posting, SearchSealValue value) => (posting, value) switch
    {
        (SearchStringPosting p, SearchSealValue.NormalizedString v) => string.Equals(p.NormalizedStringValue, v.Value, StringComparison.Ordinal),
        (SearchIntegerPosting p, SearchSealValue.IntegerValue v) => p.IntegerValue == v.Value,
        (SearchDecimalPosting p, SearchSealValue.DecimalParts v) => p.DecimalValue == SearchIndexValues.ToDecimal(v),
        (SearchDatePosting p, SearchSealValue.DateValue v) => p.DateValue == v.Value,
        (SearchDateTimePosting p, SearchSealValue.Timestamp v) => p.DateTimeValue.UtcTicks == v.Value.UtcTicks,
        (SearchBooleanPosting p, SearchSealValue.BooleanValue v) => p.BooleanValue == v.Value,
        (SearchOptionPosting p, SearchSealValue.OptionValue v) => p.FieldOptionId == v.FieldOptionId,
        _ => false,
    };

    // A Fail (§17.10.2): the search index only, never the organization (S1 is untouched); cleared only by SearchProjectionRebuild.
    private void Quarantine(IAuthorityScope scope, OrganizationAuthorizationState state, SearchProjectionState projection, string code, DateTimeOffset now) =>
        SearchIndexStateTransitions.Quarantine(scope, state, projection, code, now, clock);

    private void RaiseQuarantine(Guid organizationId, string code) => SearchIndexStateTransitions.RaiseQuarantine(alerts, organizationId, code);
}
