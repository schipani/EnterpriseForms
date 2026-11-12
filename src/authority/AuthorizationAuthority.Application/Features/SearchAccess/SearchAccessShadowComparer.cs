using System.Collections.Concurrent;
using System.Threading.Channels;
using AuthorizationAuthority.Application.Common.Ports;
using AuthorizationAuthority.Application.Features.SearchIndex;

namespace AuthorizationAuthority.Application.Features.SearchAccess;

/// <summary>C-150 IX-5c: where comparison results go besides the counters (the host logs them; counts and codes only).</summary>
public interface ISearchAccessShadowObserver
{
    void Compared(SearchAccessShadowResult result);
}

/// <summary>C-150 IX-5c: totals since start (or the last Testing reset) and the most recent results (counts and codes only).</summary>
public sealed record SearchAccessShadowSnapshot(bool Enabled, long Offered, long Dropped, long Matched, long Mismatched, long NotComparable, long Cancelled, long Failed, IReadOnlyList<SearchAccessShadowResult> Recent, long EvidenceFailures = 0);

/// <summary>
/// C-150 IX-5c shadow preparation: receives committed Tier 1 answers (<see cref="IIndexedSearchShadowSink"/>) into a bounded in-memory
/// queue and compares each against Tier 2 on one background worker, off the search path:
/// <list type="bullet">
/// <item><see cref="Offer"/> never waits: a full queue drops the observation and counts it (Tier 1 latency and results are untouched);</item>
/// <item>each comparison has its own time budget (<see cref="SearchAccessOptions.ShadowMaximumMilliseconds"/>) and the host's stop token;</item>
/// <item>a mismatch is counted, logged by the observer and raised as a <c>SEARCH_ACCESS_SHADOW_MISMATCH</c> alert with counts only
/// (never record ids, values or predicates); nothing is quarantined, repaired or served from here;</item>
/// <item>observations live in memory only and are lost on restart (a shadow comparison is evidence, not state).</item>
/// </list>
/// Registered only when <see cref="SearchAccessOptions.ShadowComparisonEnabled"/> is set (off by default; Development/Testing only).
/// </summary>
public sealed class SearchAccessShadowComparer(SearchAccessProjectionService service, SearchAccessOptions options, ISecurityAlertSink alerts, ISearchAccessShadowObserver? observer = null) : IIndexedSearchShadowSink
{
    private const int RecentCapacity = 200;

    private readonly Channel<IndexedSearchShadowObservation> queue = Channel.CreateBounded<IndexedSearchShadowObservation>(new BoundedChannelOptions(options.ShadowQueueCapacity)
    {
        FullMode = BoundedChannelFullMode.Wait, // TryWrite then fails at once instead of waiting
        SingleReader = true,
    });

    private readonly ConcurrentQueue<SearchAccessShadowResult> recent = new();
    private long offered;
    private long dropped;
    private long matched;
    private long mismatched;
    private long notComparable;
    private long cancelled;
    private long failed;
    private long evidenceFailures;

    public bool Offer(IndexedSearchShadowObservation observation)
    {
        ArgumentNullException.ThrowIfNull(observation);
        Interlocked.Increment(ref offered);
        if (queue.Writer.TryWrite(observation))
        {
            return true;
        }

        Interlocked.Increment(ref dropped);
        return false;
    }

    /// <summary>The worker: compares queued observations one at a time until the host stops.</summary>
    public async Task RunAsync(CancellationToken stoppingToken)
    {
        await foreach (var observation in queue.Reader.ReadAllAsync(stoppingToken).ConfigureAwait(false))
        {
            await CompareAsync(observation, stoppingToken).ConfigureAwait(false);
        }
    }

    /// <summary>One comparison within its time budget; never throws except for the host's own cancellation.</summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "C-150: a failing comparison is counted as Failed; it must not stop the worker.")]
    public async Task<SearchAccessShadowResult> CompareAsync(IndexedSearchShadowObservation observation, CancellationToken stoppingToken)
    {
        ArgumentNullException.ThrowIfNull(observation);
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        budget.CancelAfter(options.ShadowMaximumMilliseconds);
        SearchAccessShadowResult result;
        try
        {
            result = await service.CompareShadowAsync(observation, budget.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
        {
            result = Outcome(observation, SearchAccessShadowClassifications.Cancelled, SearchAccessShadowReasonCodes.TimeBudget);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            result = Outcome(observation, SearchAccessShadowClassifications.Failed, ex.GetType().Name);
        }

        Record(result);
        try
        {
            await service.RecordShadowSampleAsync(observation, result, stoppingToken).ConfigureAwait(false); // sampling evidence (§6)
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Interlocked.Increment(ref evidenceFailures); // evidence loss is counted, never hidden; the comparison stands
        }

        if (result.Classification == SearchAccessShadowClassifications.Mismatch)
        {
            alerts.Raise(new SecurityAlert("SEARCH_ACCESS_SHADOW_MISMATCH", "High", observation.OrganizationId, Guid.Empty, null,
                $"IX-5c shadow: Tier 2 differs from Tier 1 at the same snapshot (tier1={result.Tier1Count}, tier2={result.Tier2Count}, only_tier1={result.OnlyInTier1Count}, only_tier2={result.OnlyInTier2Count}, conditions={result.ConditionCount})",
                observation.CorrelationId));
        }

        return result;
    }

    public SearchAccessShadowSnapshot Snapshot() => new(true, Interlocked.Read(ref offered), Interlocked.Read(ref dropped), Interlocked.Read(ref matched), Interlocked.Read(ref mismatched),
        Interlocked.Read(ref notComparable), Interlocked.Read(ref cancelled), Interlocked.Read(ref failed), recent.ToArray(), Interlocked.Read(ref evidenceFailures));

    /// <summary>Testing only: clears the counters and recent results (queued observations are still compared).</summary>
    public void Reset()
    {
        Interlocked.Exchange(ref offered, 0);
        Interlocked.Exchange(ref dropped, 0);
        Interlocked.Exchange(ref matched, 0);
        Interlocked.Exchange(ref mismatched, 0);
        Interlocked.Exchange(ref notComparable, 0);
        Interlocked.Exchange(ref cancelled, 0);
        Interlocked.Exchange(ref failed, 0);
        Interlocked.Exchange(ref evidenceFailures, 0);
        recent.Clear();
    }

    private static SearchAccessShadowResult Outcome(IndexedSearchShadowObservation o, string classification, string reason) =>
        new(classification, reason, o.Tier1Matches.Count, null, 0, 0, 0, o.Conditions.Count, o.CorrelationId, 0, DateTimeOffset.UtcNow);

    private void Record(SearchAccessShadowResult result)
    {
        switch (result.Classification)
        {
            case SearchAccessShadowClassifications.Match:
                Interlocked.Increment(ref matched);
                break;
            case SearchAccessShadowClassifications.Mismatch:
                Interlocked.Increment(ref mismatched);
                break;
            case SearchAccessShadowClassifications.NotComparable:
                Interlocked.Increment(ref notComparable);
                break;
            case SearchAccessShadowClassifications.Cancelled:
                Interlocked.Increment(ref cancelled);
                break;
            default:
                Interlocked.Increment(ref failed);
                break;
        }

        recent.Enqueue(result);
        while (recent.Count > RecentCapacity && recent.TryDequeue(out _))
        {
        }

        observer?.Compared(result);
    }
}
