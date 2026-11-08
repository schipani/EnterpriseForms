using System.Security.Cryptography;
using EnterpriseForms.Application.Common;
using EnterpriseForms.Application.Common.Authority;
using EnterpriseForms.Contracts.Features;

namespace EnterpriseForms.Application.Features.Search.FieldSearch;

/// <summary>What V1 answered: the ordered match ids, or the error code (and unavailable reason / limit kind) of a failed search.</summary>
public sealed record FieldSearchV1Outcome(IReadOnlyList<Guid>? OrderedRecordIds, string? ErrorCode, string? Detail)
{
    public static FieldSearchV1Outcome Success(IReadOnlyList<Guid> ids) => new(ids, null, null);

    public static FieldSearchV1Outcome Failure(SearchException ex) => new(null, ex.ErrorCode, ex.UnavailableReason ?? ex.LimitKind);
}

/// <summary>
/// One shadow comparison (ids and counts only, never values). <c>Classification</c>:
/// <list type="bullet">
/// <item><c>Match</c>: same ordered ids and count, or the same error;</item>
/// <item><c>Difference</c>: anything else that is not one of the documented legitimate differences — an unexplained difference;</item>
/// <item><c>LegitimateV1ScopeLimit</c>: V1 refused a scope above its 500-record limit and the index answered (R1 §G.3);</item>
/// <item><c>LegitimateBusinessCopyNotVerified</c>: V1 failed because business bytes did not verify and the index answered from
/// Authority-accepted content (IX-D3, R-A; ENC-SR-08/09 scenarios);</item>
/// <item><c>NotComparable</c>: the index could not answer (still building, not ready, capacity) — no comparison was possible;</item>
/// <item><c>Skipped</c>: not sampled, no browser session, or no free shadow slot.</item>
/// </list>
/// </summary>
public sealed record FieldSearchShadowComparison(string Classification, string? SkippedReason, int? VerifiedScanCount, int? IndexedCount, string? VerifiedScanError, string? IndexedError,
    int? FirstDifferingPosition, Guid? SearchResultSnapshotId, long ElapsedMilliseconds);

/// <summary>Logging of comparisons (implemented by the API host): classifications and counts only.</summary>
public interface IFieldSearchShadowObserver
{
    void Compared(FieldSearchShadowComparison comparison);
}

/// <summary>
/// IX-3 business <c>Shadow</c> engine (design R1 §G.3, §16 IX-3). V1 remains authoritative for the user: its result is computed and
/// returned unchanged; for a bounded sample the Authority's indexed query also runs, on its own admission slot and time budget,
/// all of its pages are read, and the ordered id lists, totals and error codes are compared. A shadow failure never changes or
/// delays the user's answer beyond <see cref="FieldSearchLimits.ShadowMaximumSeconds"/>; nothing from the indexed result is
/// returned to the user. One search never combines engines.
/// </summary>
public sealed class FieldSearchShadowComparer(FieldSearchLimits limits, IIndexedSearchAuthority indexed, IFieldSearchShadowObserver observer, TimeProvider clock) : IDisposable
{
    private const int ShadowPageSize = 100;

    private readonly SemaphoreSlim slots = new(limits.MaximumConcurrentShadowSearches, limits.MaximumConcurrentShadowSearches);

    public bool Enabled => limits.Engine == FieldSearchLimits.EngineShadow;

    /// <param name="force">Testing endpoint: compare even when the sample would skip this search.</param>
    public async Task<FieldSearchShadowComparison> CompareAsync(RequestContext context, FieldSearchRequest request, FieldSearchV1Outcome v1, bool force, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(v1);
        var started = clock.GetTimestamp();
        if (!Enabled)
        {
            return Report(Skipped("ENGINE_NOT_SHADOW", started));
        }

        if (context.SessionDigestBase64 is null)
        {
            return Report(Skipped("NO_BROWSER_SESSION", started)); // the Authority binds snapshots to a session (§18.4)
        }

        if (!force && Random.Shared.Next(100) >= limits.ShadowSamplePercent)
        {
            return Report(Skipped("NOT_SAMPLED", started));
        }

        if (!await slots.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            return Report(Skipped("NO_SHADOW_SLOT", started));
        }

        try
        {
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            budget.CancelAfter(TimeSpan.FromSeconds(limits.ShadowMaximumSeconds));
            var (ids, total, snapshot, error) = await RunIndexedAsync(context, request, budget.Token).ConfigureAwait(false);
            return Report(Classify(v1, ids, total, snapshot, error, started));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Report(new FieldSearchShadowComparison("NotComparable", null, v1.OrderedRecordIds?.Count, null, v1.ErrorCode, "SHADOW_TIME_BUDGET", null, null, (long)clock.GetElapsedTime(started).TotalMilliseconds));
        }
        finally
        {
            slots.Release();
        }
    }

    private async Task<(List<Guid>? Ids, int? Total, Guid? Snapshot, string? Error)> RunIndexedAsync(RequestContext context, FieldSearchRequest request, CancellationToken cancellationToken)
    {
        var query = new AuthorityIndexedSearchQuery(request.FormDefinitionId,
            (request.Conditions ?? []).Select(c => new AuthorityIndexedSearchCondition(c.FieldDefinitionId, c.Operator, c.ValueType, c.Value, c.LowerBound, c.UpperBound)).ToList(),
            request.RecordStatus, request.IncludeChildren, request.IncludeArchived, request.SortBy, request.SortDirection, ShadowPageSize);
        var nonce = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var user = context.ToDelegatedUser();
        try
        {
            var first = await indexed.SearchAsync(user, context.SessionDigestBase64!, nonce, query, cancellationToken).ConfigureAwait(false);
            var ids = first.Items.Select(i => i.FormRecordId).ToList();
            while (ids.Count < first.TotalCount)
            {
                var page = await indexed.PageAsync(user, first.SearchResultSnapshotId, context.SessionDigestBase64!, nonce, query, ids.Count, cancellationToken).ConfigureAwait(false);
                if (page.Items.Count == 0)
                {
                    return (null, null, first.SearchResultSnapshotId, "SHADOW_PAGE_EMPTY");
                }

                ids.AddRange(page.Items.Select(i => i.FormRecordId));
            }

            return (ids, first.TotalCount, first.SearchResultSnapshotId, null);
        }
        catch (AuthorityClientException ex)
        {
            return (null, null, null, ex.ErrorCode);
        }
    }

    private FieldSearchShadowComparison Classify(FieldSearchV1Outcome v1, List<Guid>? ids, int? total, Guid? snapshot, string? indexedError, long started)
    {
        var elapsed = (long)clock.GetElapsedTime(started).TotalMilliseconds;
        FieldSearchShadowComparison Result(string classification, int? firstDiff = null) =>
            new(classification, null, v1.OrderedRecordIds?.Count, total, v1.ErrorCode is null ? null : v1.ErrorCode + (v1.Detail is null ? string.Empty : ":" + v1.Detail), indexedError, firstDiff, snapshot, elapsed);

        if (indexedError is "SEARCH_INDEX_NOT_READY" or "SEARCH_CAPACITY_EXCEEDED" or "SEARCH_INDEX_VERIFICATION_OVERDUE" or "AUTHORITY_UNAVAILABLE" or "AUTHORITY_NOT_READY")
        {
            return Result("NotComparable");
        }

        if (v1.OrderedRecordIds is { } expected && ids is not null)
        {
            var first = FirstDifference(expected, ids);
            return first is null && total == expected.Count ? Result("Match") : Result("Difference", first ?? Math.Min(expected.Count, ids.Count));
        }

        if (v1.ErrorCode == SearchErrorCodes.VerificationUnavailable && v1.Detail is "RECORD_VERIFICATION_FAILED" or "RECORD_NOT_VERIFIED" && indexedError == "RECORD_QUARANTINED")
        {
            // V1's verification found the business-copy damage and quarantined the record (the open path); the indexed engine, run
            // afterwards, then correctly refuses the scope with that quarantined record (D3 parity). Same root cause as below.
            return Result("LegitimateBusinessCopyNotVerified");
        }

        if (v1.ErrorCode is not null && indexedError is not null)
        {
            return string.Equals(BusinessCode(indexedError), v1.ErrorCode + (v1.Detail is null ? string.Empty : ":" + v1.Detail), StringComparison.Ordinal)
                   || (v1.ErrorCode == SearchErrorCodes.VerificationLimit && indexedError == "SEARCH_VERIFICATION_LIMIT")
                ? Result("Match")
                : Result("Difference");
        }

        if (v1.ErrorCode == SearchErrorCodes.VerificationLimit && v1.Detail is "records" && ids is not null)
        {
            return Result("LegitimateV1ScopeLimit");
        }

        if (v1.ErrorCode == SearchErrorCodes.VerificationUnavailable && v1.Detail is "RECORD_VERIFICATION_FAILED" or "RECORD_NOT_VERIFIED" && ids is not null)
        {
            return Result("LegitimateBusinessCopyNotVerified");
        }

        return Result("Difference");
    }

    /// <summary>The business error code (with reason) that an indexed error corresponds to, for comparing failures.</summary>
    private static string BusinessCode(string indexedError) => indexedError switch
    {
        "SEARCH_SNAPSHOT_CHANGED" => SearchErrorCodes.SnapshotChanged,
        "SEARCH_FIELD_NOT_ALLOWED" => SearchErrorCodes.FieldNotAllowed,
        "SEARCH_OPERATOR_NOT_SUPPORTED" => SearchErrorCodes.OperatorNotSupported,
        "RECORD_QUARANTINED" => SearchErrorCodes.VerificationUnavailable + ":RECORD_QUARANTINED",
        "INTEGRITY_QUARANTINE" => SearchErrorCodes.VerificationUnavailable + ":INTEGRITY_QUARANTINE",
        _ => indexedError,
    };

    public void Dispose() => slots.Dispose();

    private static int? FirstDifference(IReadOnlyList<Guid> expected, List<Guid> actual)
    {
        var shared = Math.Min(expected.Count, actual.Count);
        for (var i = 0; i < shared; i++)
        {
            if (expected[i] != actual[i])
            {
                return i;
            }
        }

        return expected.Count == actual.Count ? null : shared;
    }

    private FieldSearchShadowComparison Skipped(string reason, long started) => new("Skipped", reason, null, null, null, null, null, null, (long)clock.GetElapsedTime(started).TotalMilliseconds);

    private FieldSearchShadowComparison Report(FieldSearchShadowComparison comparison)
    {
        observer.Compared(comparison);
        return comparison;
    }
}
