using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using EnterpriseForms.Application.Common;
using EnterpriseForms.Application.Common.Authority;
using EnterpriseForms.Application.Features.FormRecords.Ports;
using EnterpriseForms.Contracts.Features;

namespace EnterpriseForms.Application.Features.Search.FieldSearch;

/// <summary>IX-4 (design §6.6): which engine produced a field-search result (<c>FieldSearchResponse.ResultBasis</c>).</summary>
public static class FieldSearchResultBases
{
    /// <summary>The Authority's indexed engine: evaluated on the content the Authority last accepted (IX-D3); not a verification of the business copy.</summary>
    public const string AuthorityAcceptedContent = "AuthorityAcceptedContent";

    /// <summary>The V1 verified scan: every record in scope read and verified against its accepted head at search time.</summary>
    public const string VerifiedScan = "VerifiedScan";
}

/// <summary>IX-4 observability of the engine choice (codes and counts only; implemented by the API host).</summary>
public interface IFieldSearchEngineObserver
{
    /// <summary>The index could not answer (<paramref name="indexReason"/>); the complete V1 path ran instead (<paramref name="outcome"/>).</summary>
    void FallbackToVerifiedScan(string indexReason, string outcome);
}

/// <summary>IX-D8: the Authority's index cannot answer this search now; the caller may run the complete V1 path.</summary>
public sealed class IndexedSearchUnavailableException(string indexReason, bool retryable) : Exception(indexReason)
{
    public string IndexReason { get; } = indexReason;

    public bool Retryable { get; } = retryable;
}

/// <summary>
/// IX-4 result cursor of an indexed search (R1 §E.5, design §17.3): the business API keeps only what the Authority needs to serve
/// another page — the result snapshot id, the per-snapshot nonce (held nowhere else, so a database reader cannot test predicate
/// values against the stored digest), the query (the Authority recomputes the digest from nonce and query on every page) — bound to
/// the organization, user and browser session, and protected by ASP.NET Core Data Protection (authenticated encryption). The result
/// itself stays in the Authority. Tokens are <c>i.&lt;offset&gt;.&lt;protected&gt;</c>; changing the offset only selects another page of
/// the caller's own result, which the Authority re-authorizes. The token travels in request bodies only, never in URLs or logs.
/// </summary>
public sealed record IndexedFieldSearchCursor(Guid OrganizationId, Guid UserId, string SessionDigestBase64, Guid FormDefinitionId, Guid SearchResultSnapshotId, string QueryNonceBase64,
    AuthorityIndexedSearchQuery Query, int TotalCount, long AuthorizationRevision, DateTimeOffset ExpiresOn, string ScopeStatement)
{
    public const string TokenPrefix = "i.";

    public const int Version = 1;

    public const int MaximumTokenLength = 16_000;

    public string Encode(IOpaqueCursorProtector protector, int offset, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(protector);
        var payload = new Payload("field-search-indexed", Version, OrganizationId, UserId, SessionDigestBase64, FormDefinitionId, SearchResultSnapshotId, QueryNonceBase64, Query, TotalCount, AuthorizationRevision, ExpiresOn, ScopeStatement);
        var lifetime = ExpiresOn - now;
        return TokenPrefix + offset.ToString(CultureInfo.InvariantCulture) + "." + protector.Protect(JsonSerializer.Serialize(payload), lifetime > TimeSpan.Zero ? lifetime : TimeSpan.FromSeconds(1));
    }

    public static bool IsIndexedToken(string? token) => token is not null && token.StartsWith(TokenPrefix, StringComparison.Ordinal);

    /// <summary>Decodes a client token for this caller and session, or throws the typed refusal (never another caller's result).</summary>
    public static (IndexedFieldSearchCursor Cursor, int Offset) Decode(IOpaqueCursorProtector protector, string? token, RequestContext context, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(protector);
        ArgumentNullException.ThrowIfNull(context);
        var body = IsIndexedToken(token) ? token![TokenPrefix.Length..] : null;
        var dot = body?.IndexOf('.', StringComparison.Ordinal) ?? -1;
        if (body is null || token!.Length > MaximumTokenLength || dot < 1 || !int.TryParse(body.AsSpan(0, dot), NumberStyles.None, CultureInfo.InvariantCulture, out var offset))
        {
            throw SearchException.RequestInvalid("this result page is not valid; run the search again");
        }

        Payload? payload = null;
        if (protector.Unprotect(body[(dot + 1)..]) is { } json)
        {
            try
            {
                payload = JsonSerializer.Deserialize<Payload>(json);
            }
            catch (JsonException)
            {
                payload = null;
            }
        }

        if (payload is null || payload.K != "field-search-indexed" || payload.V != Version || payload.O != context.OrganizationId || payload.U != context.LocalUserId || payload.X <= now
            || context.SessionDigestBase64 is null || !string.Equals(payload.D, context.SessionDigestBase64, StringComparison.Ordinal) || payload.Q is null)
        {
            // Malformed, altered, expired, another caller's or another session's: the result cannot be paged.
            throw SearchException.SnapshotChanged();
        }

        if (offset > payload.C)
        {
            throw SearchException.RequestInvalid("this result page is not valid; run the search again");
        }

        return (new IndexedFieldSearchCursor(payload.O, payload.U, payload.D, payload.F, payload.S, payload.N, payload.Q, payload.C, payload.R, payload.X, payload.T), offset);
    }

    /// <summary>K kind, V version, O organization, U user, D session digest, F form, S snapshot, N nonce, Q query, C total, R authorization revision, X expiry, T scope statement.</summary>
    private sealed record Payload(string K, int V, Guid O, Guid U, string D, Guid F, Guid S, string N, AuthorityIndexedSearchQuery? Q, int C, long R, DateTimeOffset X, string T);
}

/// <summary>
/// IX-4 business <c>Indexed</c> engine for Advanced Field Search (design §16 IX-4, R1 §E.1, §G.1, §G.2, §6.6, §17.2). The business
/// API keeps validation, the capability check and admission; the Authority decides everything else in one call (population,
/// readability, candidates, I-1, I-2, page seals, edit flags, result snapshot and execution evidence). Results are presented as
/// evaluated on Authority-accepted content, never as a verification of the business copy (<c>ProjectionVerified=false</c>,
/// <c>ResultBasis=AuthorityAcceptedContent</c>). When the index cannot answer (a field still building, the projection rebuilding,
/// quarantined or suspended), <see cref="IndexedSearchUnavailableException"/> lets the handler run the complete V1 path instead
/// (IX-D8); every other refusal (organization quarantine, quarantined record, limits, authorization) is final — the fallback
/// never bypasses an S1 or record gate (Q2).
/// </summary>
public sealed class FieldSearchIndexedEngine(FieldSearchLimits limits, IIndexedSearchAuthority indexed, FieldSearchAdmission admission, IOpaqueCursorProtector cursors, IRecordDisplayDirectory directory, TimeProvider clock)
{
    /// <summary>The design §6.2 statement (IX-D3), shown with every indexed result.</summary>
    public const string AcceptedContentStatement = "Results reflect the content the Authorization Authority last accepted for each record, at one Authority snapshot. Each returned record's searched values were checked against that accepted content. The business database copy is not checked by search; it is fully verified when you open a record.";

    /// <summary>Authority errors meaning "the index cannot answer now" (§17.2: building, rebuilding, quarantined, suspended): the V1 fallback may run.</summary>
    public static readonly IReadOnlySet<string> FallbackCodes = new HashSet<string>(StringComparer.Ordinal) { "SEARCH_INDEX_NOT_READY", "SEARCH_INDEX_INCONSISTENT", "SEARCH_INDEX_VERIFICATION_OVERDUE" };

    public bool Enabled => limits.Engine == FieldSearchLimits.EngineIndexed;

    /// <summary>Runs the indexed search (one automatic retry on a changed snapshot, as V1). Throws <see cref="IndexedSearchUnavailableException"/> for the fallback, or the typed <see cref="SearchException"/>.</summary>
    public async Task<FieldSearchResponse> SearchAsync(RequestContext context, FieldSearchRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(request);
        var session = context.SessionDigestBase64 ?? throw new InvalidOperationException("the indexed engine requires a browser session");
        var query = new AuthorityIndexedSearchQuery(request.FormDefinitionId,
            (request.Conditions ?? []).Select(c => new AuthorityIndexedSearchCondition(c.FieldDefinitionId, c.Operator, c.ValueType, c.Value, c.LowerBound, c.UpperBound)).ToList(),
            request.RecordStatus, request.IncludeChildren, request.IncludeArchived, request.SortBy, request.SortDirection, request.PageSize);
        using var lease = await admission.AdmitAsync(context.LocalUserId, cancellationToken).ConfigureAwait(false);
        var user = context.ToDelegatedUser();
        for (var attempt = 1; ; attempt++)
        {
            var nonce = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
            AuthorityIndexedSearchResult result;
            try
            {
                result = await indexed.SearchAsync(user, session, nonce, query, cancellationToken).ConfigureAwait(false);
            }
            catch (AuthorityClientException ex) when (FallbackCodes.Contains(ex.ErrorCode))
            {
                throw new IndexedSearchUnavailableException(ex.ErrorCode, ex.Retryable);
            }
            catch (AuthorityClientException ex) when (attempt == 1 && ex.ErrorCode is "SEARCH_SNAPSHOT_CHANGED" or "AUTHORITY_NOT_READY" && admission.HasFreeSlot)
            {
                continue; // the business retries a changed snapshot once, as V1 does
            }
            catch (AuthorityClientException ex)
            {
                throw MapSearchFailure(ex);
            }

            var statement = $"Complete: all {result.ScopeRecordCount} records of this form that you can find were searched at one Authority snapshot"
                            + (result.RecordsWithoutContent > 0 ? $"; {result.RecordsWithoutContent} pending creation(s) without accepted content cannot match" : string.Empty)
                            + ". Conditions are evaluated only on values you can read; repeating sections, related records, documents and encrypted fields are not searched. "
                            + AcceptedContentStatement;
            var cursor = new IndexedFieldSearchCursor(context.OrganizationId, context.LocalUserId, session, request.FormDefinitionId, result.SearchResultSnapshotId, nonce, query,
                result.TotalCount, result.AuthorizationRevision, result.SnapshotExpiresOn, statement);
            return await ResponseAsync(context, cursor, result, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Another page of an indexed result: the Authority re-authorizes every page (§17.3); nothing is cached here.</summary>
    public async Task<FieldSearchResponse> PageAsync(RequestContext context, string? token, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var (cursor, offset) = IndexedFieldSearchCursor.Decode(cursors, token, context, clock.GetUtcNow());
        AuthorityIndexedSearchResult result;
        try
        {
            result = await indexed.PageAsync(context.ToDelegatedUser(), cursor.SearchResultSnapshotId, cursor.SessionDigestBase64, cursor.QueryNonceBase64, cursor.Query, offset, cancellationToken).ConfigureAwait(false);
        }
        catch (AuthorityClientException ex)
        {
            throw MapPageFailure(ex);
        }

        if (result.SearchResultSnapshotId != cursor.SearchResultSnapshotId || result.TotalCount != cursor.TotalCount || result.Offset != offset)
        {
            throw SearchException.SnapshotChanged(); // never a page of another result
        }

        return await ResponseAsync(context, cursor, result, cancellationToken).ConfigureAwait(false);
    }

    private async Task<FieldSearchResponse> ResponseAsync(RequestContext context, IndexedFieldSearchCursor cursor, AuthorityIndexedSearchResult result, CancellationToken cancellationToken)
    {
        var names = await directory.GetNamesAsync(context.OrganizationId, cancellationToken).ConfigureAwait(false);
        var formName = names.FormNames.GetValueOrDefault(cursor.FormDefinitionId, string.Empty);
        // ProjectionVerified is false: the business copy of these records was not read or verified by this search (§6.6).
        var items = result.Items.Select(i => new RecordListItemResponse(i.FormRecordId, cursor.FormDefinitionId, formName, i.RecordStatusName, i.RecordRegisteredOn, i.ContentHeadModifiedOn, i.ParentFormRecordId, false,
                directory.ReferenceFor(i.FormRecordId), names.UserNames.GetValueOrDefault(i.CreatedByBusinessUserId), i.CreatedByBusinessUserId == context.LocalUserId, i.Editable))
            .ToList();
        var offset = result.Offset;
        var now = clock.GetUtcNow();
        var next = offset + items.Count < cursor.TotalCount ? cursor.Encode(cursors, offset + items.Count, now) : null;
        var previous = offset > 0 ? cursor.Encode(cursors, Math.Max(0, offset - cursor.Query.PageSize), now) : null;
        return new FieldSearchResponse(items, cursor.TotalCount, cursor.TotalCount == 0 ? 0 : offset + 1, cursor.Query.PageSize, cursor.SearchResultSnapshotId.ToString("D"), cursor.AuthorizationRevision,
            FieldSearchHandler.CompletenessComplete, cursor.ScopeStatement, next, previous, FieldSearchResultBases.AuthorityAcceptedContent);
    }

    /// <summary>The business refusal for an indexed-search failure; codes and reasons match V1's for the same cause (one search never combines engines).</summary>
    public static SearchException MapSearchFailure(AuthorityClientException ex)
    {
        ArgumentNullException.ThrowIfNull(ex);
        return ex.ErrorCode switch
        {
            "AUTHORITY_NOT_READY" => SearchException.SnapshotChanged(), // V1 parity (the catalogue maps it the same way)
            _ => MapCommon(ex),
        };
    }

    public static SearchException MapPageFailure(AuthorityClientException ex)
    {
        ArgumentNullException.ThrowIfNull(ex);
        return ex.ErrorCode switch
        {
            "AUTHORITY_NOT_READY" => SearchException.VerificationUnavailable("AUTHORITY_GATE_CLOSED", retryable: true), // V1 page parity
            "SEARCH_SNAPSHOT_NOT_FOUND" => SearchException.SnapshotChanged(),
            "SEARCH_INDEX_NOT_READY" or "SEARCH_INDEX_INCONSISTENT" or "SEARCH_INDEX_VERIFICATION_OVERDUE" => SearchException.VerificationUnavailable(ex.ErrorCode, ex.Retryable),
            _ => MapCommon(ex),
        };
    }

    private static SearchException MapCommon(AuthorityClientException ex) => ex.ErrorCode switch
    {
        "SEARCH_FIELD_NOT_ALLOWED" => SearchException.FieldNotAllowed(ConditionNumber(ex.Message)),
        "SEARCH_OPERATOR_NOT_SUPPORTED" => SearchException.OperatorNotSupported(ConditionNumber(ex.Message)),
        "SEARCH_VERIFICATION_LIMIT" => SearchException.VerificationLimit(ex.Message is "discoverable" or "items" or "results" or "time" ? ex.Message : "records"),
        "SEARCH_SNAPSHOT_CHANGED" => SearchException.SnapshotChanged(),
        "RECORD_QUARANTINED" => SearchException.VerificationUnavailable("RECORD_QUARANTINED", retryable: false),
        "INTEGRITY_QUARANTINE" => SearchException.VerificationUnavailable("RECORD_QUARANTINED", retryable: false), // V1 parity (TrustedScopeVerifier)
        "PREDICATE_VALUE_ENCRYPTED" => SearchException.VerificationUnavailable("PREDICATE_VALUE_ENCRYPTED", retryable: false),
        "SEARCH_CAPACITY_EXCEEDED" => SearchException.VerificationUnavailable(FieldSearchAdmission.CapacityExceeded, retryable: true),
        "SEARCH_SESSION_INVALID" => new SearchException("UNAUTHORIZED", 401, "the session is no longer valid; sign in again", retryable: false),
        _ => SearchException.VerificationUnavailable("AUTHORITY_UNAVAILABLE", retryable: ex.HttpStatusCode >= 500 || ex.Retryable),
    };

    /// <summary>"condition N: …" → N (1 when absent); only the position is ever echoed, never a value.</summary>
    private static int ConditionNumber(string detail)
    {
        const string Prefix = "condition ";
        if (detail.StartsWith(Prefix, StringComparison.Ordinal))
        {
            var end = detail.IndexOf(':', Prefix.Length);
            if (end > Prefix.Length && int.TryParse(detail.AsSpan(Prefix.Length, end - Prefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n is >= 1 and <= FieldSearchLimits.ApprovedMaximumConditions)
            {
                return n;
            }
        }

        return 1;
    }
}
