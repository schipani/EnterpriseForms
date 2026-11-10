using AuthorizationAuthority.Application.Common;
using AuthorizationAuthority.Application.Common.Ports;
using AuthorizationAuthority.Contracts.V1;
using AuthorizationAuthority.Domain.Features.Content;
using AuthorizationAuthority.Domain.Features.FieldSearch;
using AuthorizationAuthority.Domain.Features.Organizations;
using AuthorizationAuthority.Domain.Features.SearchIndex;
using EnterpriseForms.Integrity.Search;

namespace AuthorizationAuthority.Application.Features.SearchIndex;

/// <summary>Builds the canonical seal message of an entry from its approvals and derived postings (shared by commit and sweep).</summary>
public static class SearchEntrySealBuilder
{
    public static byte[] Message(SearchRecordEntry entry, IReadOnlyCollection<FieldSearchApproval> approvals, IReadOnlyList<DerivedPosting> postings, IReadOnlyList<SearchSealAnomaly> anomalies)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(approvals);
        var coverages = approvals.Select(a => new SearchSealCoverage(a.FieldDefinitionId, a.FieldSearchApprovalId, a.ApprovedFieldDataType, a.ApprovedFieldClassification, a.ApprovedProtectionMode, ParseOperators(a.ApprovedOperatorsJson))).ToList();
        return SearchEntrySealV1.Encode(new SearchEntrySealInput(
            entry.SealKeyName, entry.SealKeyVersion, entry.OrganizationId, entry.FormDefinitionId, entry.FormRecordId, entry.IndexedOperationId, entry.IndexedContentRevision, entry.IndexedManifestRoot, entry.RecordStatusName, coverages,
            postings.Select(p => new SearchSealPosting(p.FieldDefinitionId, p.FieldSearchApprovalId, p.ManifestItemKey, p.ManifestItemId, p.EntityKind, p.ValueType, p.SourceCanonicalDigest, p.Value)).ToList(),
            anomalies));
    }

    public static IReadOnlyList<string> ParseOperators(string approvedOperatorsJson) =>
        System.Text.Json.JsonSerializer.Deserialize<List<string>>(approvedOperatorsJson) ?? throw new System.Text.Json.JsonException("approved operators missing");
}

/// <summary>What the commit hands the index: the record, its new head and the staged bytes just verified.</summary>
/// <param name="RegisteredFormDefinitionId">The record's form from the Authority registry when the commit already read it (IX-1
/// remediation: saves a second read of the same registry row); null makes the derivation read it.</param>
public sealed record CommitIndexInput(Guid FormRecordId, RecordContentHead Head, IReadOnlyList<StagedContentItem> VerifiedItems, DateTimeOffset Now, Guid? RegisteredFormDefinitionId = null);

/// <summary>
/// IX-1: writes the Authority-owned search index inside the transactions that change what it may contain.
/// <list type="bullet">
/// <item><b>Content commit</b> (W1): the record's entry, postings and anomalies are replaced from the verified bytes and
/// sealed ES256 with the pinned key; any failure aborts the commit (IX-D7).</item>
/// <item><b>Searchability approvals</b> (AC-6): a new Active approval starts a Building coverage; an approval leaving Active
/// withdraws its coverage and purges the whole form's index, because every entry's seal covers the withdrawn field.</item>
/// </list>
/// In <see cref="SearchIndexMode.Disabled"/> nothing is written here.
/// </summary>
public sealed class SearchIndexWriter(SearchIndexOptions options, SearchSealSigningGate? signingGate, Action<Guid, SearchDerivationStageTimings>? stageTimingSink = null)
{
    public SearchIndexMode Mode => options.Mode;

    /// <summary>Derives the record's index at its new head. Returns false when nothing applies (no projection, or no coverage for the form).</summary>
    public Task<bool> DeriveOnCommitAsync(IAuthorityScope scope, OrganizationAuthorizationState state, CommitIndexInput input, CancellationToken cancellationToken) =>
        DeriveAtHeadAsync(scope, state, input, stageTimingSink, cancellationToken);

    /// <summary>
    /// IX-2 backfill (IX-D1 (b)): derives the record's index at its current head from bytes the Authority has just verified
    /// against that head (<c>ContentService.VerifyWorkloadObservedContentAsync</c>, under the organization lock, in this scope).
    /// The same derivation, seal and write as a commit; the content revision does not change (nothing was committed).
    /// </summary>
    public Task<bool> DeriveForBackfillAsync(IAuthorityScope scope, OrganizationAuthorizationState state, CommitIndexInput input, CancellationToken cancellationToken) =>
        DeriveAtHeadAsync(scope, state, input, null, cancellationToken);

    private async Task<bool> DeriveAtHeadAsync(IAuthorityScope scope, OrganizationAuthorizationState state, CommitIndexInput input, Action<Guid, SearchDerivationStageTimings>? timingSink, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(input);
        var index = scope.SearchIndex;
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        // IX-1 remediation: the registry row the commit already read is reused (the same Authority source, not a business input).
        var formDefinitionId = input.RegisteredFormDefinitionId
                               ?? (await scope.FindResourceAsync(state.OrganizationId, ResourceTypeNames.FormRecord, input.FormRecordId, cancellationToken).ConfigureAwait(false))?.ParentResourceEntityId
                               ?? throw SearchPostingDerivationV1.Failure("the record has no registered form");

        // One read: the form's Active approvals that have a coverage other than Withdrawn (previously two reads).
        var activeApprovals = await index.GetCoveredActiveApprovalsAsync(state.OrganizationId, formDefinitionId, cancellationToken).ConfigureAwait(false);
        if (activeApprovals.Count == 0)
        {
            // One command: organization binding, removal of any entry, form-state increment only if an entry was removed.
            await index.ResetRecordIndexAsync(state.OrganizationId, formDefinitionId, input.FormRecordId, alwaysAdvanceFormState: false, input.Now, cancellationToken).ConfigureAwait(false);
            return false;
        }

        if (signingGate is null)
        {
            throw new AuthorityException(AuthorityErrorCodes.SearchSealUnavailable, 503, true, "no search seal signer is configured");
        }

        var readsDone = System.Diagnostics.Stopwatch.GetTimestamp();
        var content = SearchPostingDerivationV1.Derive(state.OrganizationId, formDefinitionId, input.FormRecordId, input.VerifiedItems, activeApprovals);
        var derivedApprovalIds = activeApprovals.Select(a => a.FieldSearchApprovalId).OrderBy(id => id).ToArray();
        var entry = new SearchRecordEntry
        {
            SearchRecordEntryId = Guid.CreateVersion7(),
            OrganizationId = state.OrganizationId,
            FormDefinitionId = formDefinitionId,
            FormRecordId = input.FormRecordId,
            IndexedOperationId = input.Head.CommittedOperationId ?? throw SearchPostingDerivationV1.Failure("the head has no committed operation"),
            IndexedContentRevision = input.Head.CommittedContentRevision,
            IndexedManifestRoot = (byte[])input.Head.CommittedManifestRoot.Clone(),
            RecordStatusName = content.RecordStatusName,
            HeaderSourceCanonicalBytes = content.HeaderSourceCanonicalBytes,
            ParentFormRecordId = content.ParentFormRecordId,
            CreatedByBusinessUserId = content.CreatedByUserId,
            EntryDerivationVersion = SearchPostingDerivationV1.CurrentEntryDerivationVersion,
            DerivedFieldSearchApprovalIds = derivedApprovalIds,
            // C-141 (UF-25): the accepted total item count of each derived field, from the same postings, in the same row.
            DerivedFieldItemCounts = SearchFieldItemCounts.Derive(derivedApprovalIds, content.Postings.Select(p => p.FieldSearchApprovalId)),
            IndexedPostingCount = content.Postings.Count,
            IndexedAnomalyCount = content.Anomalies.Count,
            SealKeyName = signingGate.KeyName,
            SealKeyVersion = signingGate.KeyVersion,
            EntryWrittenOn = input.Now,
            LastSealVerifiedOn = input.Now, // §17.10.5 K7: counts only because it is written in this transaction, after a successful seal
            LastDeepVerifiedOn = input.Now, // derived from bytes this commit verified (S-C baseline)
        };

        var message = SearchEntrySealBuilder.Message(entry, activeApprovals, content.Postings, content.Anomalies);
        var derived = System.Diagnostics.Stopwatch.GetTimestamp();
        SearchSealSignature signature;
        try
        {
            signature = await signingGate.SignAsync(message, cancellationToken).ConfigureAwait(false);
        }
        catch (SearchSealUnavailableException ex)
        {
            throw new AuthorityException(AuthorityErrorCodes.SearchSealUnavailable, 503, true, ex.Message);
        }

        var signed = System.Diagnostics.Stopwatch.GetTimestamp();
        entry.EntrySealSignature = signature.Signature;
        // One command: organization binding (RLS), removal of the previous entry, form-state increment. The new rows are queued
        // and written by the commit's final SaveChanges, in the same transaction (previously four round trips).
        await index.ResetRecordIndexAsync(state.OrganizationId, formDefinitionId, input.FormRecordId, alwaysAdvanceFormState: true, input.Now, cancellationToken).ConfigureAwait(false);
        index.AddRecordIndex(ToRows(entry, content));
        if (timingSink is not null)
        {
            var written = System.Diagnostics.Stopwatch.GetTimestamp();
            timingSink(input.FormRecordId, new SearchDerivationStageTimings(
                System.Diagnostics.Stopwatch.GetElapsedTime(started, readsDone).TotalMilliseconds,
                System.Diagnostics.Stopwatch.GetElapsedTime(readsDone, derived).TotalMilliseconds,
                System.Diagnostics.Stopwatch.GetElapsedTime(derived, signed).TotalMilliseconds,
                System.Diagnostics.Stopwatch.GetElapsedTime(signed, written).TotalMilliseconds));
        }

        return true;
    }

    /// <summary>
    /// AC-6 approval changes, in the security command's transaction (after the plan is applied). Disabled mode: nothing.
    /// </summary>
    public async Task ApplyApprovalChangesAsync(IAuthorityScope scope, OrganizationAuthorizationState state, IReadOnlyCollection<FieldSearchApproval> newApprovals, IReadOnlyCollection<FieldSearchApproval> changedApprovals, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(newApprovals);
        ArgumentNullException.ThrowIfNull(changedApprovals);
        if (options.Mode == SearchIndexMode.Disabled || (newApprovals.Count == 0 && changedApprovals.Count == 0))
        {
            return;
        }

        var index = scope.SearchIndex;
        await index.BindOrganizationAsync(state.OrganizationId, cancellationToken).ConfigureAwait(false);
        await EnsureProjectionAsync(index, state, now, cancellationToken).ConfigureAwait(false);
        var coverages = await index.GetCoveragesAsync(state.OrganizationId, null, cancellationToken).ConfigureAwait(false);
        foreach (var approval in changedApprovals.Where(a => a.ApprovalStateName != FieldSearchApprovalStates.Active))
        {
            foreach (var coverage in coverages.Where(c => c.FieldSearchApprovalId == approval.FieldSearchApprovalId && c.CoverageStateName != SearchCoverageStates.Withdrawn))
            {
                await WithdrawAsync(index, coverages, coverage, approval.InvalidationReasonCode ?? approval.ApprovalStateName.ToUpperInvariant(), now, cancellationToken).ConfigureAwait(false);
            }
        }

        foreach (var approval in newApprovals.Where(a => a.ApprovalStateName == FieldSearchApprovalStates.Active && SearchPostingDerivationV1.IsIndexableValueType(a.ApprovedFieldDataType)))
        {
            var coverage = NewCoverage(state.OrganizationId, approval, now);
            index.AddCoverage(coverage);
            coverages.Add(coverage);
        }
    }

    /// <summary>
    /// Maintenance (sweep, under the organization lock): every Active approval has a coverage, and every coverage whose
    /// approval is no longer Active is withdrawn with its form purged. Returns the number of corrections.
    /// </summary>
    public async Task<int> ReconcileCoveragesAsync(IAuthorityScope scope, OrganizationAuthorizationState state, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(state);
        var index = scope.SearchIndex;
        var approvals = await scope.GetFieldSearchApprovalsAsync(state.OrganizationId, null, cancellationToken).ConfigureAwait(false);
        var coverages = await index.GetCoveragesAsync(state.OrganizationId, null, cancellationToken).ConfigureAwait(false);
        var corrections = 0;
        foreach (var coverage in coverages.Where(c => c.CoverageStateName != SearchCoverageStates.Withdrawn).ToList())
        {
            var approval = approvals.FirstOrDefault(a => a.FieldSearchApprovalId == coverage.FieldSearchApprovalId);
            if (approval?.ApprovalStateName != FieldSearchApprovalStates.Active)
            {
                await WithdrawAsync(index, coverages, coverage, "APPROVAL_NOT_ACTIVE", now, cancellationToken).ConfigureAwait(false);
                corrections++;
            }
        }

        if (options.Mode == SearchIndexMode.Dark)
        {
            foreach (var approval in approvals.Where(a => a.ApprovalStateName == FieldSearchApprovalStates.Active && SearchPostingDerivationV1.IsIndexableValueType(a.ApprovedFieldDataType)
                                                         && !coverages.Any(c => c.FieldSearchApprovalId == a.FieldSearchApprovalId)))
            {
                var coverage = NewCoverage(state.OrganizationId, approval, now);
                index.AddCoverage(coverage);
                coverages.Add(coverage);
                corrections++;
            }
        }

        return corrections;
    }

    public static async Task<SearchProjectionState> EnsureProjectionAsync(ISearchIndexStore index, OrganizationAuthorizationState state, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(index);
        ArgumentNullException.ThrowIfNull(state);
        var projection = await index.FindProjectionStateAsync(state.OrganizationId, cancellationToken).ConfigureAwait(false);
        if (projection is not null)
        {
            return projection;
        }

        projection = new SearchProjectionState
        {
            SearchProjectionStateId = Guid.CreateVersion7(),
            OrganizationId = state.OrganizationId,
            ProjectionStatusName = SearchProjectionStatuses.Active,
            ProjectionContentIntegrityRevision = state.ContentIntegrityRevision,
            ProjectionCreatedOn = now,
            ProjectionModifiedOn = now,
        };
        index.AddProjectionState(projection);
        return projection;
    }

    private static async Task WithdrawAsync(ISearchIndexStore index, List<SearchFieldCoverage> coverages, SearchFieldCoverage coverage, string reason, DateTimeOffset now, CancellationToken cancellationToken)
    {
        coverage.CoverageStateName = SearchCoverageStates.Withdrawn;
        coverage.CoverageWithdrawnOn = now;
        coverage.CoverageWithdrawalReasonCode = reason.Length > 64 ? reason[..64] : reason;

        // Every entry of the form carries a seal over the withdrawn field's postings, so the whole form's projection is purged in
        // this transaction (a superset of the field purge of R1 §C.2) and its other coverages return to Building.
        await index.PurgeFormIndexAsync(coverage.OrganizationId, coverage.FormDefinitionId, cancellationToken).ConfigureAwait(false);
        foreach (var sibling in coverages.Where(c => c.FormDefinitionId == coverage.FormDefinitionId && c.CoverageStateName == SearchCoverageStates.Complete))
        {
            sibling.CoverageStateName = SearchCoverageStates.Building;
            sibling.CoverageCompletedOn = null;
            sibling.CoverageAttestedJournalEntryId = null;
        }

        await index.BumpFormStateAsync(coverage.OrganizationId, coverage.FormDefinitionId, now, cancellationToken).ConfigureAwait(false);
    }

    private static SearchFieldCoverage NewCoverage(Guid organizationId, FieldSearchApproval approval, DateTimeOffset now) => new()
    {
        SearchFieldCoverageId = Guid.CreateVersion7(),
        OrganizationId = organizationId,
        FormDefinitionId = approval.FormDefinitionId,
        FieldDefinitionId = approval.FieldDefinitionId,
        FieldSearchApprovalId = approval.FieldSearchApprovalId,
        IndexedValueTypeName = approval.ApprovedFieldDataType,
        CoverageStateName = SearchCoverageStates.Building,
        CoveredFieldClassification = approval.ApprovedFieldClassification,
        CoverageStartedOn = now,
    };

    public static DerivedRecordIndex ToRows(SearchRecordEntry entry, DerivedRecordContent content)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(content);
        T Base<T>(T posting, DerivedPosting p)
            where T : SearchPostingBase
        {
            posting.OrganizationId = entry.OrganizationId;
            posting.FormDefinitionId = entry.FormDefinitionId;
            posting.FieldDefinitionId = p.FieldDefinitionId;
            posting.FormRecordId = entry.FormRecordId;
            posting.ManifestItemId = p.ManifestItemId;
            posting.ManifestItemKey = p.ManifestItemKey;
            posting.SourceEntityKind = p.EntityKind;
            posting.IndexedOperationId = entry.IndexedOperationId;
            posting.FieldSearchApprovalId = p.FieldSearchApprovalId;
            posting.SourceCanonicalBytes = p.SourceCanonicalBytes;
            return posting;
        }

        var strings = new List<SearchStringPosting>();
        var integers = new List<SearchIntegerPosting>();
        var decimals = new List<SearchDecimalPosting>();
        var dates = new List<SearchDatePosting>();
        var dateTimes = new List<SearchDateTimePosting>();
        var booleans = new List<SearchBooleanPosting>();
        var options = new List<SearchOptionPosting>();
        foreach (var p in content.Postings)
        {
            switch (p.Value)
            {
                case SearchSealValue.NormalizedString s:
                    strings.Add(Base(new SearchStringPosting { SearchStringPostingId = Guid.CreateVersion7(), NormalizedStringValue = s.Value }, p));
                    break;
                case SearchSealValue.IntegerValue i:
                    integers.Add(Base(new SearchIntegerPosting { SearchIntegerPostingId = Guid.CreateVersion7(), IntegerValue = i.Value }, p));
                    break;
                case SearchSealValue.DecimalParts d:
                    decimals.Add(Base(new SearchDecimalPosting { SearchDecimalPostingId = Guid.CreateVersion7(), DecimalValue = SearchIndexValues.ToDecimal(d) }, p));
                    break;
                case SearchSealValue.DateValue date:
                    dates.Add(Base(new SearchDatePosting { SearchDatePostingId = Guid.CreateVersion7(), DateValue = date.Value }, p));
                    break;
                case SearchSealValue.Timestamp ts:
                    dateTimes.Add(Base(new SearchDateTimePosting { SearchDateTimePostingId = Guid.CreateVersion7(), DateTimeValue = ts.Value.ToUniversalTime() }, p));
                    break;
                case SearchSealValue.BooleanValue b:
                    booleans.Add(Base(new SearchBooleanPosting { SearchBooleanPostingId = Guid.CreateVersion7(), BooleanValue = b.Value }, p));
                    break;
                case SearchSealValue.OptionValue o:
                    options.Add(Base(new SearchOptionPosting { SearchOptionPostingId = Guid.CreateVersion7(), FieldOptionId = o.FieldOptionId }, p));
                    break;
                default:
                    throw SearchPostingDerivationV1.Failure("unsupported posting value");
            }
        }

        var anomalies = content.Anomalies.Select(a => new SearchRecordFieldAnomaly
        {
            SearchRecordFieldAnomalyId = Guid.CreateVersion7(),
            OrganizationId = entry.OrganizationId,
            FormDefinitionId = entry.FormDefinitionId,
            FormRecordId = entry.FormRecordId,
            FieldDefinitionId = a.FieldDefinitionId,
            IndexedOperationId = entry.IndexedOperationId,
            AnomalyKindName = a.AnomalyKind,
        }).ToList();
        return new DerivedRecordIndex(entry, strings, integers, decimals, dates, dateTimes, booleans, options, anomalies);
    }
}

/// <summary>Exact conversions between the bound canonical values and the typed index columns.</summary>
public static class SearchIndexValues
{
    public static decimal ToDecimal(SearchSealValue.DecimalParts parts)
    {
        ArgumentNullException.ThrowIfNull(parts);
        var magnitude = System.Numerics.BigInteger.Abs(parts.Coefficient);
        if (parts.Scale is < 0 or > 28 || magnitude > new System.Numerics.BigInteger(decimal.MaxValue))
        {
            throw SearchPostingDerivationV1.Failure("a decimal value is outside the indexed range");
        }

        var mask = new System.Numerics.BigInteger(uint.MaxValue);
        return new decimal(unchecked((int)(uint)(magnitude & mask)), unchecked((int)(uint)((magnitude >> 32) & mask)), unchecked((int)(uint)((magnitude >> 64) & mask)), parts.Coefficient.Sign < 0, (byte)parts.Scale);
    }
}

/// <summary>
/// IX-1 measurement only (Testing environment): where an indexed commit's extra time goes. Reads = organization binding,
/// record registration, coverages and approvals; derive = posting derivation and message encoding; sign = provider ES256
/// call plus local verification against the pin; writes = index replacement and form-state upsert (statements queued or sent
/// before the commit's final flush). Identifiers and durations only.
/// </summary>
public sealed record SearchDerivationStageTimings(double ReadsMilliseconds, double DeriveMilliseconds, double SignMilliseconds, double WritesMilliseconds);
