using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EnterpriseForms.Application.Common;
using EnterpriseForms.Application.Common.Authority;
using EnterpriseForms.Application.Features.FormRecords.Ports;
using EnterpriseForms.Application.Features.Search.Verification;
using EnterpriseForms.Contracts.Features;

namespace EnterpriseForms.Application.Features.Search.FieldSearch;

/// <summary>
/// Phase C7 Advanced Field Search (docs/architecture/PHASE_C7_ADVANCED_FIELD_SEARCH.md, decisions C-56, C-60, C-67, C-69).
/// <para>
/// Capabilities and predicate authorization (M2): the fields a caller may search, their operators, labels and option
/// choices come only from the Authority (AC-6). A request is authorized condition by condition against that list — forged
/// field ids, value types, operators and injected options get the single indistinguishable denial.
/// </para>
/// <para>
/// Execution (M3), behind the fail-closed <see cref="FieldSearchLimits.ExecutionEnabled"/> gate: admission → the Authority
/// catalogue of the caller's discoverable records of the form (AC-5) → every contentful record read and verified in batches
/// against its accepted head → snapshot re-validated → predicates evaluated on the verified values the caller may read →
/// metadata filters and sort on verified facts (signed header status/parent, Authority-held times) → a cursor bound to the
/// snapshot. One failed verification fails the whole search; a changed snapshot is retried at most once within the same
/// budget when capacity allows, otherwise reported (never as "no records"). Execution evidence (fields, operators, counts,
/// outcome — never values) is journaled by the Authority; a search whose evidence cannot be recorded returns no result.
/// There is no business-store dependency (architecture test): nothing here can fall back to filtering business rows.
/// </para>
/// </summary>
public sealed class FieldSearchHandler(FieldSearchLimits limits, IFieldSearchPolicyAuthority policy, TrustedScopeVerifier verifier, FieldSearchAdmission admission,
    IAuthorizationAuthorityClient authority, IOpaqueCursorProtector cursors, IRecordDisplayDirectory directory, TimeProvider clock, FieldSearchShadowComparer? shadow = null,
    FieldSearchIndexedEngine? indexedEngine = null, IFieldSearchEngineObserver? engineObserver = null, FieldSearchRetryPolicy? retryPolicy = null)
{
    private readonly FieldSearchRetryPolicy retry = retryPolicy ?? new FieldSearchRetryPolicy(limits, clock, engineObserver);

    public const string ConditionJoinAnd = "And";

    public const string CompletenessComplete = "Complete";

    /// <summary>
    /// Searchable fields only with an active approval and the caller's query permission; the not-searchable list only for
    /// callers who may see the form's search configuration (designers, security administrators). A form the caller may not
    /// know about yields exactly the answer of a form that does not exist.
    /// </summary>
    public async Task<FieldSearchCapabilitiesResponse> CapabilitiesAsync(RequestContext context, Guid formDefinitionId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (formDefinitionId == Guid.Empty)
        {
            throw SearchException.RequestInvalid("select a form: advanced field search runs over one form");
        }

        var capabilities = await policy.GetFieldSearchCapabilitiesAsync(context.ToDelegatedUser(), new FieldSearchCapabilitiesQuery(context.OrganizationId, formDefinitionId), cancellationToken).ConfigureAwait(false);
        // Operators in the vocabulary's natural order (equals before ranges), not the Authority's sorted storage order.
        static IReadOnlyList<string> Ordered(AuthoritySearchableField field)
        {
            var vocabulary = FieldSearchOperators.ForValueType(field.FieldDataType);
            return field.Operators.OrderBy(o => vocabulary.Contains(o) ? vocabulary.ToList().IndexOf(o) : int.MaxValue).ThenBy(o => o, StringComparer.Ordinal).ToList();
        }

        var fields = capabilities.Fields.Select(f => new FieldSearchCapabilityField(f.FieldDefinitionId, f.FieldLabel, f.FieldDataType, Ordered(f),
            f.Options.Select(o => new FieldSearchOptionChoice(o.FieldOptionId, o.OptionLabel)).ToList(),
            f.FieldDataType == FieldSearchValueTypes.StringType ? limits.MinimumPartialTextLength : null,
            f.FieldDataType == FieldSearchValueTypes.StringType ? limits.MaximumTextValueLength : null, f.InCurrentVersion)).ToList();
        // C-188: the not-searchable hint lists fields of the current version only; removed fields appear only when approved (above).
        var notSearchable = (capabilities.Configuration?.Fields ?? [])
            .Where(f => f.InCurrentVersion && (f.ApprovalState != "Active" || !capabilities.PlaintextSearchPermitted))
            .Select(f => new FieldSearchNotSearchableField(f.FieldDefinitionId, f.FieldLabel, f.Eligibility == "Encrypted" ? "ENCRYPTED" : "NOT_APPROVED"))
            .ToList();
        // C-188 BF-1: while a publication of the form awaits registration the Authority returns no searchable field; say why.
        var unavailable = !limits.ExecutionEnabled ? SearchUnavailableReasons.TrustedSearchNotEnabled : capabilities.PublicationPending ? SearchUnavailableReasons.FormPublicationPending : null;
        return new FieldSearchCapabilitiesResponse(formDefinitionId, limits.ExecutionEnabled && !capabilities.PublicationPending, unavailable,
            limits.MaximumConditions, ConditionJoinAnd, fields, capabilities.PublicationPending ? [] : notSearchable, capabilities.SearchPolicyRevision);
    }

    /// <summary>Authorizes every condition against the Authority's capabilities, then (gate on) runs the verified search.</summary>
    public async Task<FieldSearchResponse> SearchAsync(RequestContext context, FieldSearchRequest? request, CancellationToken cancellationToken)
    {
        var (response, failure, _) = await ExecuteAsync(context, request, forceShadow: false, cancellationToken).ConfigureAwait(false);
        if (failure is not null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(failure);
        }

        return response!;
    }

    /// <summary>
    /// IX-3: the V1 search exactly as before; with <c>FieldSearch:Engine=Shadow</c> the indexed engine is then compared (R1 §G.3).
    /// V1's answer or failure is returned unchanged; the comparison never alters it. Validation and predicate-authorization
    /// refusals are thrown directly (nothing executed, nothing to compare).
    /// <para>
    /// IX-4: with <c>FieldSearch:Engine=Indexed</c> and a browser session, the Authority's indexed engine answers
    /// (<see cref="FieldSearchIndexedEngine"/>). When the index cannot answer (IX-D8), the complete, unmodified V1 path runs instead,
    /// only within V1's limits (a V1 limit refusal returns the index's reason) and the response says so (<c>ResultBasis</c>
    /// <c>VerifiedScan</c>); one search never combines engines. Without a browser session (bearer API callers) the result snapshot
    /// cannot be bound to a session (§18.4), so V1 answers (D-IX4-2). <paramref name="forceShadow"/> (Testing endpoint) always runs
    /// V1 with the shadow comparison, under either engine.
    /// </para>
    /// </summary>
    public async Task<(FieldSearchResponse? Response, SearchException? Failure, FieldSearchShadowComparison? Shadow)> ExecuteAsync(RequestContext context, FieldSearchRequest? request, bool forceShadow, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        FieldSearchRequestValidator.Validate(request, limits);
        var conditions = request!.Conditions ?? [];
        // C-150 R56-A: one retry budget for the whole request (capability check and search attempts together).
        var retries = retry.Begin();
        AuthorityFieldSearchCapabilities capabilities;
        while (true)
        {
            try
            {
                capabilities = await policy.GetFieldSearchCapabilitiesAsync(context.ToDelegatedUser(), new FieldSearchCapabilitiesQuery(context.OrganizationId, request.FormDefinitionId), cancellationToken).ConfigureAwait(false);
                break;
            }
            catch (AuthorityClientException ex) when (FieldSearchRetryPolicy.IsRetryable(ex))
            {
                // A closed organization gate refuses the capability check too (503 AUTHORITY_NOT_READY): the same bounded backoff, a fresh call.
                if (!await retry.WaitBeforeRetryAsync(retries, "capabilities", ex.ErrorCode, context.CorrelationId, null, cancellationToken).ConfigureAwait(false))
                {
                    throw;
                }
            }
        }

        if (capabilities.PublicationPending)
        {
            // C-188 BF-1: the Authority already returned no searchable field (fail closed); refuse with the reason rather than per condition.
            throw SearchException.FormPublicationPending();
        }

        var allowed = capabilities.PlaintextSearchPermitted ? capabilities.Fields.ToDictionary(f => f.FieldDefinitionId) : [];
        for (var index = 0; index < conditions.Count; index++)
        {
            var condition = conditions[index];
            if (!allowed.TryGetValue(condition.FieldDefinitionId, out var field) || field.FieldDataType != condition.ValueType)
            {
                throw SearchException.FieldNotAllowed(index + 1);
            }

            if (!field.Operators.Contains(condition.Operator, StringComparer.Ordinal))
            {
                throw SearchException.OperatorNotSupported(index + 1);
            }

            if (field.FieldDataType == FieldSearchValueTypes.OptionType && !field.Options.Any(o => string.Equals(o.FieldOptionId.ToString("D"), condition.Value, StringComparison.OrdinalIgnoreCase)))
            {
                // An option the approval did not freeze (e.g. injected into business metadata, G21) is never searchable.
                throw SearchException.OperatorNotSupported(index + 1);
            }
        }

        if (!limits.ExecutionEnabled || conditions.Count == 0)
        {
            throw SearchException.TrustedSearchNotEnabled();
        }

        if (!forceShadow && indexedEngine is { Enabled: true } && context.SessionDigestBase64 is not null)
        {
            var (indexedResponse, indexedFailure) = await ExecuteIndexedAsync(context, request!, conditions, retries, cancellationToken).ConfigureAwait(false);
            return (indexedResponse, indexedFailure, null);
        }

        FieldSearchResponse? response = null;
        SearchException? failure = null;
        List<Guid>? orderedIds = null;
        try
        {
            (response, orderedIds) = await ExecuteVerifiedAsync(context, request!, conditions, null, retries, cancellationToken).ConfigureAwait(false);
        }
        catch (SearchException ex)
        {
            failure = ex;
        }

        FieldSearchShadowComparison? comparison = null;
        if (shadow is not null && shadow.CanCompare(forceShadow))
        {
            comparison = await shadow.CompareAsync(context, request!, failure is null ? FieldSearchV1Outcome.Success(orderedIds!) : FieldSearchV1Outcome.Failure(failure), forceShadow, cancellationToken).ConfigureAwait(false);
        }

        return (response, failure, comparison);
    }

    /// <summary>
    /// IX-4: the indexed engine, then — only when the index cannot answer — the complete V1 path within V1's limits (IX-D8, §17.2 Q2).
    /// The indexed admission lease is released before V1 takes its own. S1, record quarantine, limits and authorization refusals of the
    /// indexed engine are final: they never fall back.
    /// </summary>
    private async Task<(FieldSearchResponse? Response, SearchException? Failure)> ExecuteIndexedAsync(RequestContext context, FieldSearchRequest request, IReadOnlyList<FieldSearchCondition> conditions, FieldSearchRetryPolicy.Budget retries, CancellationToken cancellationToken)
    {
        IndexedSearchUnavailableException unavailable;
        try
        {
            return (await indexedEngine!.SearchAsync(context, request, retries, cancellationToken).ConfigureAwait(false), null);
        }
        catch (SearchException ex)
        {
            return (null, ex);
        }
        catch (IndexedSearchUnavailableException ex)
        {
            unavailable = ex;
        }

        try
        {
            var (response, _) = await ExecuteVerifiedAsync(context, request, conditions,
                " The search index could not answer this search, so every record in scope was verified instead.", retries, cancellationToken).ConfigureAwait(false);
            engineObserver?.FallbackToVerifiedScan(unavailable.IndexReason, "COMPLETED");
            return (response, null);
        }
        catch (SearchException ex) when (ex.ErrorCode == SearchErrorCodes.VerificationLimit)
        {
            // Outside V1's limits the fallback does not apply: the index's own reason is returned (R1 §G.2).
            engineObserver?.FallbackToVerifiedScan(unavailable.IndexReason, "V1_LIMIT");
            return (null, SearchException.VerificationUnavailable(unavailable.IndexReason, unavailable.Retryable));
        }
        catch (SearchException ex)
        {
            engineObserver?.FallbackToVerifiedScan(unavailable.IndexReason, ex.ErrorCode);
            return (null, ex);
        }
    }

    /// <summary>The V1 verified scan (Phase C7 M3), unchanged; the admission slot is released before any shadow comparison.</summary>
    private async Task<(FieldSearchResponse Response, List<Guid> OrderedIds)> ExecuteVerifiedAsync(RequestContext context, FieldSearchRequest request, IReadOnlyList<FieldSearchCondition> conditions, string? statementNote, FieldSearchRetryPolicy.Budget retries, CancellationToken cancellationToken)
    {
        var prepared = FieldSearchEvaluator.Prepare(conditions, limits);
        using var lease = await admission.AdmitAsync(context.LocalUserId, cancellationToken).ConfigureAwait(false);
        var started = clock.GetTimestamp();
        var budget = TimeSpan.FromSeconds(limits.MaximumExecutionSeconds);
        var scope = await VerifyWithRetriesAsync(context, request.FormDefinitionId, conditions, started, budget, retries, cancellationToken).ConfigureAwait(false);

        var evaluated = 0;
        var matches = new List<FieldSearchMatch>();
        foreach (var record in scope.Records)
        {
            var header = record.Content?.Header;
            if (header is null || header.FormDefinitionId != request.FormDefinitionId)
            {
                // A verified record must carry its signed header for the catalogue's form; anything else is not trustworthy.
                await RecordAsync(context, scope.SearchCatalogueSnapshotId, conditions, "SEARCH_VERIFICATION_UNAVAILABLE", null, null, started, cancellationToken).ConfigureAwait(false);
                throw SearchException.VerificationUnavailable("RECORD_VERIFICATION_FAILED", retryable: false);
            }

            if (!MetadataMatches(request, header) || !FieldSearchEvaluator.Evaluable(record, prepared))
            {
                continue;
            }

            evaluated++;
            if (FieldSearchEvaluator.Matches(record, prepared))
            {
                matches.Add(new FieldSearchMatch(record.FormRecordId, record.RegisteredOn ?? DateTimeOffset.UnixEpoch, record.HeadModifiedOn ?? record.RegisteredOn ?? DateTimeOffset.UnixEpoch,
                    header.RecordStatus, header.CreatedByUserId, header.ParentFormRecordId));
            }
        }

        if (clock.GetElapsedTime(started) > budget)
        {
            await RecordAsync(context, scope.SearchCatalogueSnapshotId, conditions, "SEARCH_VERIFICATION_LIMIT", evaluated, null, started, cancellationToken).ConfigureAwait(false);
            throw SearchException.VerificationLimit("time");
        }

        var ordered = Sort(matches, request.SortBy, request.SortDirection);
        // Execution evidence is mandatory (OD-8): without it no result leaves the server.
        await RecordAsync(context, scope.SearchCatalogueSnapshotId, conditions, "COMPLETED", evaluated, ordered.Count, started, cancellationToken).ConfigureAwait(false);
        var statement = $"Complete: all {scope.Records.Count} records of this form that you can find were verified at one Authority snapshot"
                        + (scope.RecordsWithoutContent > 0 ? $"; {scope.RecordsWithoutContent} pending creation(s) without accepted content cannot match" : string.Empty)
                        + ". Conditions are evaluated only on values you can read; repeating sections, related records, documents and encrypted fields are not searched."
                        + statementNote;
        var cursor = new FieldSearchCursor(context.OrganizationId, context.LocalUserId, request.FormDefinitionId, scope.SearchCatalogueSnapshotId, scope.AuthorizationRevision, Digest(request), request.PageSize,
            scope.SnapshotExpiresOn, statement, ordered);
        return (await PageResponseAsync(context, cursor, 0, cancellationToken).ConfigureAwait(false), ordered.Select(m => m.FormRecordId).ToList());
    }

    /// <summary>
    /// Another page of a verified result: the Authority snapshot it was verified at must still be valid (any authorization
    /// change, a revocation included, or a change to a record in scope invalidates it). Nothing is re-evaluated.
    /// </summary>
    public async Task<FieldSearchResponse> PageAsync(RequestContext context, FieldSearchPageRequest? request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        FieldSearchRequestValidator.ValidatePage(request);
        if (!limits.ExecutionEnabled)
        {
            throw SearchException.TrustedSearchNotEnabled();
        }

        if (IndexedFieldSearchCursor.IsIndexedToken(request!.Cursor))
        {
            // IX-4: a page of an indexed result is served by the Authority, which re-authorizes it (§17.3); never by V1 (no silent
            // downgrade of an indexed paging request). With the engine switched off, an indexed cursor is refused (§12.3 rollback).
            return indexedEngine is not { Enabled: true } ? throw SearchException.SnapshotChanged() : await indexedEngine.PageAsync(context, request.Cursor, cancellationToken).ConfigureAwait(false);
        }

        var (cursor, offset) = FieldSearchCursor.Decode(cursors, request!.Cursor, context, clock.GetUtcNow());
        CatalogueSnapshotValidation validation;
        try
        {
            validation = await authority.ValidateSearchCatalogueAsync(context.ToDelegatedUser(), cursor.SearchCatalogueSnapshotId, cancellationToken).ConfigureAwait(false);
        }
        catch (AuthorityClientException ex)
        {
            throw ex.HttpStatusCode >= 500 || ex.Retryable ? SearchException.VerificationUnavailable("AUTHORITY_UNAVAILABLE", retryable: true) : SearchException.SnapshotChanged();
        }

        if (!validation.Valid || validation.CurrentAuthorizationRevision != cursor.AuthorizationRevision)
        {
            throw validation.InvalidationReason == "ORGANIZATION_GATE_CLOSED" ? SearchException.VerificationUnavailable("AUTHORITY_GATE_CLOSED", retryable: true) : SearchException.SnapshotChanged();
        }

        return await PageResponseAsync(context, cursor, offset, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The V1 verification with C-150 R56-A bounded backoff: only a changed snapshot (which includes a closed organization gate) is
    /// retried, only with half the execution budget left and a free slot, and only within the request's retry budget. Each attempt
    /// asks the Authority for a new catalogue snapshot; evidence of each failed attempt is recorded as before.
    /// </summary>
    private async Task<VerifiedScope> VerifyWithRetriesAsync(RequestContext context, Guid formDefinitionId, IReadOnlyList<FieldSearchCondition> conditions, long started, TimeSpan budget, FieldSearchRetryPolicy.Budget retries, CancellationToken cancellationToken)
    {
        while (true)
        {
            Guid? issued = null;
            try
            {
                return await verifier.VerifyAsync(context, formDefinitionId, budget - clock.GetElapsedTime(started), id => issued = id, cancellationToken).ConfigureAwait(false);
            }
            catch (SearchException ex)
            {
                if (issued is { } snapshot)
                {
                    await RecordAsync(context, snapshot, conditions, ex.ErrorCode, null, null, started, cancellationToken).ConfigureAwait(false);
                }

                var elapsed = clock.GetElapsedTime(started);
                if (ex.ErrorCode != SearchErrorCodes.SnapshotChanged || elapsed > budget / 2 || !admission.HasFreeSlot
                    || !await retry.WaitBeforeRetryAsync(retries, "verified-scan", ex.ErrorCode, context.CorrelationId, (budget / 2) - elapsed, cancellationToken).ConfigureAwait(false))
                {
                    throw;
                }
            }
        }
    }

    private async Task RecordAsync(RequestContext context, Guid snapshotId, IReadOnlyList<FieldSearchCondition> conditions, string outcome, int? evaluated, int? matched, long started, CancellationToken cancellationToken)
    {
        try
        {
            await policy.RecordSearchExecutionAsync(context.ToDelegatedUser(), snapshotId,
                new SearchExecutionEvidence(conditions.Select(c => c.FieldDefinitionId).Distinct().ToList(), conditions.Select(c => c.Operator).ToList(), conditions.Count, outcome, evaluated, matched,
                    (long)clock.GetElapsedTime(started).TotalMilliseconds), cancellationToken).ConfigureAwait(false);
        }
        catch (AuthorityClientException) when (outcome != "COMPLETED")
        {
            // Evidence of a failed search is best effort (the failure is returned regardless); a completed search without
            // evidence is refused below.
        }
        catch (AuthorityClientException)
        {
            throw SearchException.VerificationUnavailable("EXECUTION_EVIDENCE_UNAVAILABLE", retryable: true);
        }
    }

    private static bool MetadataMatches(FieldSearchRequest request, AcceptedRecordHeaderFacts header) =>
        (request.IncludeChildren || header.ParentFormRecordId is null)
        && (request.RecordStatus is null || string.Equals(header.RecordStatus, request.RecordStatus, StringComparison.Ordinal))
        // As in My Records: archived records only when asked for (a status filter or includeArchived).
        && (request.RecordStatus is not null || request.IncludeArchived || !string.Equals(header.RecordStatus, "Archived", StringComparison.Ordinal));

    /// <summary>Metadata sort on verified facts (no field-value sort, D6); last change then record id make it total.</summary>
    private static List<FieldSearchMatch> Sort(List<FieldSearchMatch> matches, string? sortBy, string? sortDirection)
    {
        var sort = string.IsNullOrEmpty(sortBy) ? "modifiedOn" : sortBy;
        var descending = (string.IsNullOrEmpty(sortDirection) ? (sort is "formName" or "recordStatus" ? "asc" : "desc") : sortDirection) == "desc";
        IOrderedEnumerable<FieldSearchMatch> ordered = sort switch
        {
            "createdOn" => descending ? matches.OrderByDescending(m => m.CreatedOn) : matches.OrderBy(m => m.CreatedOn),
            "recordStatus" => descending ? matches.OrderByDescending(m => m.RecordStatus, StringComparer.Ordinal) : matches.OrderBy(m => m.RecordStatus, StringComparer.Ordinal),
            "formName" => matches.OrderBy(_ => 0), // one form per search
            _ => descending ? matches.OrderByDescending(m => m.ModifiedOn) : matches.OrderBy(m => m.ModifiedOn),
        };
        return (descending ? ordered.ThenByDescending(m => m.ModifiedOn).ThenByDescending(m => m.FormRecordId) : ordered.ThenBy(m => m.ModifiedOn).ThenBy(m => m.FormRecordId)).ToList();
    }

    private async Task<FieldSearchResponse> PageResponseAsync(RequestContext context, FieldSearchCursor cursor, int offset, CancellationToken cancellationToken)
    {
        var page = cursor.Matches.Skip(offset).Take(cursor.PageSize).ToList();
        var editable = new HashSet<Guid>();
        if (page.Count > 0)
        {
            var decisions = await authority.CheckPermissionsBatchAsync(context.ToDelegatedUser(),
                page.Select(m => new PermissionCheckRequest(context.OrganizationId, new ResourceReference(ProtectedResourceKind.FormRecord, m.FormRecordId), ProtectedAction.Edit)).ToList(),
                cancellationToken).ConfigureAwait(false);
            for (var i = 0; i < page.Count; i++)
            {
                if (decisions[i].Allowed)
                {
                    editable.Add(page[i].FormRecordId);
                }
            }
        }

        var names = await directory.GetNamesAsync(context.OrganizationId, cancellationToken).ConfigureAwait(false);
        var formName = names.FormNames.GetValueOrDefault(cursor.FormDefinitionId, string.Empty);
        var items = page.Select(m => new RecordListItemResponse(m.FormRecordId, cursor.FormDefinitionId, formName, m.RecordStatus, m.CreatedOn, m.ModifiedOn, m.ParentFormRecordId, true,
                directory.ReferenceFor(m.FormRecordId), names.UserNames.GetValueOrDefault(m.CreatedByUserId), m.CreatedByUserId == context.LocalUserId, editable.Contains(m.FormRecordId)))
            .ToList();
        var now = clock.GetUtcNow();
        var next = offset + page.Count < cursor.Matches.Count ? cursor.Encode(cursors, offset + page.Count, now) : null;
        var previous = offset > 0 ? cursor.Encode(cursors, Math.Max(0, offset - cursor.PageSize), now) : null;
        return new FieldSearchResponse(items, cursor.Matches.Count, cursor.Matches.Count == 0 ? 0 : offset + 1, cursor.PageSize, cursor.SearchCatalogueSnapshotId.ToString("D"), cursor.AuthorizationRevision,
            CompletenessComplete, cursor.ScopeStatement, next, previous, FieldSearchResultBases.VerifiedScan);
    }

    /// <summary>Digest of everything that shaped the result (kept inside the protected cursor; never logged).</summary>
    private static string Digest(FieldSearchRequest request) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(request))));
}
