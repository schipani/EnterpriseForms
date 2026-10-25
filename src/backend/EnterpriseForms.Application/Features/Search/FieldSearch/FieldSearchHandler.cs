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
    IAuthorizationAuthorityClient authority, IOpaqueCursorProtector cursors, IRecordDisplayDirectory directory, TimeProvider clock)
{
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
            f.FieldDataType == FieldSearchValueTypes.StringType ? limits.MaximumTextValueLength : null)).ToList();
        var notSearchable = (capabilities.Configuration?.Fields ?? [])
            .Where(f => f.ApprovalState != "Active" || !capabilities.PlaintextSearchPermitted)
            .Select(f => new FieldSearchNotSearchableField(f.FieldDefinitionId, f.FieldLabel, f.Eligibility == "Encrypted" ? "ENCRYPTED" : "NOT_APPROVED"))
            .ToList();
        return new FieldSearchCapabilitiesResponse(formDefinitionId, limits.ExecutionEnabled, limits.ExecutionEnabled ? null : SearchUnavailableReasons.TrustedSearchNotEnabled,
            limits.MaximumConditions, ConditionJoinAnd, fields, notSearchable, capabilities.SearchPolicyRevision);
    }

    /// <summary>Authorizes every condition against the Authority's capabilities, then (gate on) runs the verified search.</summary>
    public async Task<FieldSearchResponse> SearchAsync(RequestContext context, FieldSearchRequest? request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        FieldSearchRequestValidator.Validate(request, limits);
        var conditions = request!.Conditions ?? [];
        var capabilities = await policy.GetFieldSearchCapabilitiesAsync(context.ToDelegatedUser(), new FieldSearchCapabilitiesQuery(context.OrganizationId, request.FormDefinitionId), cancellationToken).ConfigureAwait(false);
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

        var prepared = FieldSearchEvaluator.Prepare(conditions, limits);
        using var lease = await admission.AdmitAsync(context.LocalUserId, cancellationToken).ConfigureAwait(false);
        var started = clock.GetTimestamp();
        var budget = TimeSpan.FromSeconds(limits.MaximumExecutionSeconds);
        var scope = await VerifyWithOneRetryAsync(context, request.FormDefinitionId, conditions, started, budget, cancellationToken).ConfigureAwait(false);

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
                        + ". Conditions are evaluated only on values you can read; repeating sections, related records, documents and encrypted fields are not searched.";
        var cursor = new FieldSearchCursor(context.OrganizationId, context.LocalUserId, request.FormDefinitionId, scope.SearchCatalogueSnapshotId, scope.AuthorizationRevision, Digest(request), request.PageSize,
            scope.SnapshotExpiresOn, statement, ordered);
        return await PageResponseAsync(context, cursor, 0, cancellationToken).ConfigureAwait(false);
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

    private async Task<VerifiedScope> VerifyWithOneRetryAsync(RequestContext context, Guid formDefinitionId, IReadOnlyList<FieldSearchCondition> conditions, long started, TimeSpan budget, CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
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

                // At most one automatic retry, only for a changed snapshot, only with half the budget left and a free slot.
                if (attempt > 1 || ex.ErrorCode != SearchErrorCodes.SnapshotChanged || clock.GetElapsedTime(started) > budget / 2 || !admission.HasFreeSlot)
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
            CompletenessComplete, cursor.ScopeStatement, next, previous);
    }

    /// <summary>Digest of everything that shaped the result (kept inside the protected cursor; never logged).</summary>
    private static string Digest(FieldSearchRequest request) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(request))));
}
