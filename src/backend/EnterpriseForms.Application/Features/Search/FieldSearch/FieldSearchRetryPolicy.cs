using EnterpriseForms.Application.Common.Authority;

namespace EnterpriseForms.Application.Features.Search.FieldSearch;

/// <summary>
/// C-150 R56-A: bounded, jittered backoff for the two transient Authority search refusals, replacing the single immediate retry.
/// <para>
/// <b>What is retried:</b> only an Authority refusal that is explicitly retryable <i>and</i> is 503 <c>AUTHORITY_NOT_READY</c> (the
/// organization gate is closed: the S1 projection or the archive receipt has not caught up after a committed change) or 409
/// <c>SEARCH_SNAPSHOT_CHANGED</c> (authorization or content moved during the search). Authorization, integrity, quarantine, limit,
/// session and capacity refusals are never retried; nothing about them changes here.
/// </para>
/// <para>
/// <b>How:</b> every retry is a complete new call — a new nonce and therefore a new Authority snapshot; nothing of a failed attempt
/// is reused, and the Authority re-authorizes everything. Delays grow 1, 2, 4 … × <see cref="FieldSearchLimits.RetryInitialDelayMilliseconds"/>
/// up to <see cref="FieldSearchLimits.RetryMaximumDelayMilliseconds"/>, each drawn uniformly from [½, 1] of that step (equal jitter), so
/// searches refused together do not retry together. The number of attempts, the time since the search started and the caller's
/// remaining deadline all bound it; the request's cancellation token cancels the wait. When no retry is allowed the last refusal is
/// returned exactly as before (same code, status and audit evidence).
/// </para>
/// </summary>
public sealed class FieldSearchRetryPolicy(FieldSearchLimits limits, TimeProvider clock, IFieldSearchEngineObserver? observer = null, Func<double>? jitter = null)
{
    public const string AuthorityNotReady = "AUTHORITY_NOT_READY";

    public const string SearchSnapshotChanged = "SEARCH_SNAPSHOT_CHANGED";

    private readonly Func<double> next = jitter ?? Random.Shared.NextDouble;

    /// <summary>Whether this Authority refusal is one of the two transient refusals R56-A may retry.</summary>
    public static bool IsRetryable(AuthorityClientException ex)
    {
        ArgumentNullException.ThrowIfNull(ex);
        return ex.Retryable && ((ex.ErrorCode == AuthorityNotReady && ex.HttpStatusCode == 503) || (ex.ErrorCode == SearchSnapshotChanged && ex.HttpStatusCode == 409));
    }

    /// <summary>Starts the retry budget of one search request (shared by its capability check and its search attempts).</summary>
    public Budget Begin() => new(clock.GetTimestamp());

    /// <summary>
    /// When another attempt is allowed, waits the jittered delay and returns true; otherwise returns false at once.
    /// <paramref name="remaining"/>, when given, is the caller's own deadline left (a retry never starts if its wait would exhaust it).
    /// </summary>
    public async Task<bool> WaitBeforeRetryAsync(Budget budget, string stage, string code, Guid correlationId, TimeSpan? remaining, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(budget);
        if (budget.Retries + 1 >= limits.RetryMaximumAttempts)
        {
            return false;
        }

        var step = Math.Min(limits.RetryMaximumDelayMilliseconds, limits.RetryInitialDelayMilliseconds * Math.Pow(2, budget.Retries));
        var delay = TimeSpan.FromMilliseconds(step * (0.5 + (0.5 * Math.Clamp(next(), 0, 1))));
        var elapsed = clock.GetElapsedTime(budget.Started);
        if (elapsed + delay > TimeSpan.FromMilliseconds(limits.RetryMaximumTotalMilliseconds) || (remaining is { } left && delay >= left))
        {
            return false;
        }

        budget.Retries++;
        budget.DelayedMilliseconds += delay.TotalMilliseconds;
        observer?.Retrying(correlationId, stage, code, budget.Retries, delay.TotalMilliseconds);
        await Task.Delay(delay, clock, cancellationToken).ConfigureAwait(false);
        return true;
    }

    /// <summary>The retries one search request has used (attempts = retries + 1).</summary>
    public sealed class Budget(long started)
    {
        public long Started { get; } = started;

        public int Retries { get; internal set; }

        public double DelayedMilliseconds { get; internal set; }
    }
}
