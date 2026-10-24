namespace EnterpriseForms.Contracts.Features;

// Phase C7 Advanced Field Search wire contracts (/api/v1, ADR-PB-004, docs/architecture/PHASE_C7_ADVANCED_FIELD_SEARCH.md §5.2).
// Predicate values travel in request bodies only (never in URLs) as invariant text per value type, like record values.

/// <summary>
/// Server-authoritative search capabilities for one form and the calling user. The browser never infers searchability
/// from a field's type or classification: only <c>Fields</c> may be offered as predicates. <c>NotSearchableFields</c> lists
/// fields the caller may know about that cannot be searched (for example "ENCRYPTED"); a field the caller may not know
/// about appears in neither list. Phase C7 M0: <c>FieldValueSearchAvailable</c> is false (reason
/// <c>TRUSTED_SEARCH_NOT_ENABLED</c>) and both lists are empty, because trusted execution does not exist yet.
/// </summary>
public sealed record FieldSearchCapabilitiesResponse(
    Guid FormDefinitionId,
    bool FieldValueSearchAvailable,
    string? UnavailableReasonCode,
    int MaximumConditions,
    string ConditionJoin,
    IReadOnlyList<FieldSearchCapabilityField> Fields,
    IReadOnlyList<FieldSearchNotSearchableField> NotSearchableFields,
    long? PolicyRevision);

/// <summary>One searchable field: its value type, the operators the server allows for it, and (Option) the selectable option ids.</summary>
public sealed record FieldSearchCapabilityField(
    Guid FieldDefinitionId,
    string DisplayLabel,
    string ValueType,
    IReadOnlyList<string> Operators,
    IReadOnlyList<FieldSearchOptionChoice> Options,
    int? MinimumTextLength,
    int? MaximumTextLength);

public sealed record FieldSearchOptionChoice(Guid FieldOptionId, string DisplayText);

/// <summary>A field the caller may know about but cannot search; <c>ReasonCode</c> is <c>ENCRYPTED</c> or <c>NOT_APPROVED</c>.</summary>
public sealed record FieldSearchNotSearchableField(Guid FieldDefinitionId, string DisplayLabel, string ReasonCode);

/// <summary>
/// One AND-combined condition. <c>ValueType</c> is String, Integer, Decimal, Date (yyyy-MM-dd), DateTime (ISO 8601 with
/// offset), Boolean or Option (a field option id). <c>between</c> takes <c>LowerBound</c> and <c>UpperBound</c> (inclusive);
/// <c>isTrue</c>/<c>isFalse</c> take no value; every other operator takes <c>Value</c>.
/// </summary>
public sealed record FieldSearchCondition(
    Guid FieldDefinitionId,
    string Operator,
    string ValueType,
    string? Value = null,
    string? LowerBound = null,
    string? UpperBound = null);

/// <summary>Advanced field search over one selected form; the metadata filters, sort and page size mean what they mean in My Records.</summary>
public sealed record FieldSearchRequest(
    Guid FormDefinitionId,
    IReadOnlyList<FieldSearchCondition>? Conditions,
    string? RecordStatus = null,
    bool IncludeArchived = false,
    bool IncludeChildren = false,
    string? SortBy = null,
    string? SortDirection = null,
    int PageSize = 25);

/// <summary>Another page of a verified result: the opaque cursor is bound to the caller, session, query and verified snapshot.</summary>
public sealed record FieldSearchPageRequest(string? Cursor);

/// <summary>
/// A complete, verified result page (returned only when every eligible record was authenticated at one snapshot; anything
/// else is a typed failure, never a partial page). Rows reuse the My Records item so results render in the same table.
/// </summary>
public sealed record FieldSearchResponse(
    IReadOnlyList<RecordListItemResponse> Items,
    int TotalCount,
    int PageStart,
    int PageSize,
    string SnapshotReference,
    long AuthorizationRevision,
    string Completeness,
    string ScopeStatement,
    string? ContinuationToken = null,
    string? PreviousToken = null);

/// <summary>
/// Testing environment only (Phase C7 M1 proof, never part of the published API): outcome and cost of verifying the caller's
/// complete scope of one form through the Authority catalogue and batch verification. Counts and identifiers only.
/// </summary>
public sealed record SearchScopeVerificationResponse(
    Guid SearchCatalogueSnapshotId,
    Guid FormDefinitionId,
    long AuthorizationRevision,
    long ContentIntegrityRevision,
    int CatalogueEntryCount,
    int RecordsWithoutContent,
    int VerifiedRecordCount,
    int CompleteProjectionCount,
    IReadOnlyList<Guid> VerifiedRecordIds,
    int AuthorityCalls,
    int VerificationBatches,
    long VerifiedBytes,
    double ElapsedMilliseconds);

public sealed record SearchScopeVerificationRequest(Guid FormDefinitionId);
