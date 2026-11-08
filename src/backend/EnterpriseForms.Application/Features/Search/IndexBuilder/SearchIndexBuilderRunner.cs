using EnterpriseForms.Application.Common.Authority;

namespace EnterpriseForms.Application.Features.Search.IndexBuilder;

/// <summary>How a failed builder pass is handled (C-129 §10).</summary>
public enum SearchIndexBuilderFailureKind
{
    /// <summary>Infrastructure that may recover (network, timeout, database, Authority 5xx/retryable, 409/423/429): bounded exponential backoff.</summary>
    Transient = 1,

    /// <summary>The Authority refuses the builder credential (401/403): fail closed — the builder stops until the Worker is restarted with a valid credential.</summary>
    CredentialRefused = 2,

    /// <summary>Anything else (a defect, an unexpected response): logged as an error with its type and code, backed off, never allowed to stop the Worker host.</summary>
    Unexpected = 3,
}

/// <summary>Why <see cref="SearchIndexBuilderRunner.RunAsync"/> returned.</summary>
public enum SearchIndexBuilderStopReason
{
    NotConfigured = 1,
    Cancelled = 2,
    CredentialRefused = 3,
}

/// <summary>The two passes the runner schedules (implemented by <see cref="SearchIndexBuilderWorkflow"/>).</summary>
public interface ISearchIndexBuilderPasses
{
    bool IsConfigured { get; }

    Task<SearchIndexBuilderPassResult> RunBackfillRoundAsync(CancellationToken cancellationToken);

    Task<SearchIndexDeepSweepPassResult> RunDeepSweepPassAsync(CancellationToken cancellationToken);
}

/// <summary>Observability of the runner (counts, exception type names and error codes only; never content, values or keys).</summary>
public interface ISearchIndexBuilderObserver
{
    void Idle();

    void Started(int backfillIntervalSeconds, int deepSweepIntervalSeconds, int recordsPerSecond);

    void BackfillCompleted(SearchIndexBuilderPassResult result);

    void DeepSweepCompleted(SearchIndexDeepSweepPassResult result);

    void PassFailed(string pass, SearchIndexBuilderFailureKind kind, string exceptionType, string errorCode, int consecutiveFailures, TimeSpan retryIn);

    void CredentialRefused(string pass, string errorCode);
}

public static class SearchIndexBuilderFailurePolicy
{
    public static SearchIndexBuilderFailureKind Classify(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return exception switch
        {
            AuthorityClientException { HttpStatusCode: 401 or 403 } => SearchIndexBuilderFailureKind.CredentialRefused,
            AuthorityClientException a when a.Retryable || a.HttpStatusCode >= 500 || a.HttpStatusCode is 409 or 423 or 429 => SearchIndexBuilderFailureKind.Transient,
            HttpRequestException or IOException or TimeoutException or TaskCanceledException or System.Data.Common.DbException => SearchIndexBuilderFailureKind.Transient,
            _ => SearchIndexBuilderFailureKind.Unexpected,
        };
    }

    /// <summary>Exponential backoff from <paramref name="baseDelay"/>, doubling per consecutive failure, never above <paramref name="cap"/>.</summary>
    public static TimeSpan Backoff(int consecutiveFailures, TimeSpan baseDelay, TimeSpan cap)
    {
        var exponent = Math.Clamp(consecutiveFailures - 1, 0, 20);
        var delay = TimeSpan.FromTicks(Math.Min(cap.Ticks, baseDelay.Ticks * (1L << exponent)));
        return delay < baseDelay ? baseDelay : delay;
    }
}

/// <summary>
/// IX-2 builder loop (C-129 §10): schedules the backfill round and the S-C deep sweep, classifies every failure
/// (<see cref="SearchIndexBuilderFailurePolicy"/>), backs off transient and unexpected failures with a bounded exponential delay,
/// stops (fail closed) when the Authority refuses the builder credential, and returns promptly on cancellation. No exception
/// escapes except through cancellation, so a builder failure never stops the Worker host. A failed pass never completes
/// anything: Complete is set only by the Authority's attestation, which a failed backfill round never reaches for its form.
/// </summary>
public sealed class SearchIndexBuilderRunner(ISearchIndexBuilderPasses passes, SearchIndexBuilderOptions options, TimeProvider clock, ISearchIndexBuilderObserver observer)
{
    public TimeSpan Tick { get; init; } = TimeSpan.FromSeconds(1);

    public TimeSpan BackoffBase { get; init; } = TimeSpan.FromSeconds(2);

    public TimeSpan BackoffCap { get; init; } = TimeSpan.FromMinutes(5);

    public async Task<SearchIndexBuilderStopReason> RunAsync(CancellationToken stoppingToken)
    {
        if (!passes.IsConfigured)
        {
            observer.Idle();
            return SearchIndexBuilderStopReason.NotConfigured;
        }

        observer.Started(options.BackfillIntervalSeconds, options.DeepSweepIntervalSeconds, options.MaximumRecordsPerSecond);
        var nextBackfill = clock.GetUtcNow();
        var nextDeep = clock.GetUtcNow().AddSeconds(options.DeepSweepIntervalSeconds); // first pass one interval after start (D-IX2-19)
        int backfillFailures = 0, deepFailures = 0;
        while (!stoppingToken.IsCancellationRequested)
        {
            if (clock.GetUtcNow() >= nextBackfill)
            {
                var outcome = await RunPassAsync("backfill", async ct => observer.BackfillCompleted(await passes.RunBackfillRoundAsync(ct).ConfigureAwait(false)), backfillFailures, stoppingToken).ConfigureAwait(false);
                if (outcome.Stop is { } stop)
                {
                    return stop;
                }

                backfillFailures = outcome.Failures;
                nextBackfill = clock.GetUtcNow() + (outcome.RetryIn ?? TimeSpan.FromSeconds(options.BackfillIntervalSeconds));
            }

            if (clock.GetUtcNow() >= nextDeep)
            {
                var outcome = await RunPassAsync("deep-sweep", async ct => observer.DeepSweepCompleted(await passes.RunDeepSweepPassAsync(ct).ConfigureAwait(false)), deepFailures, stoppingToken).ConfigureAwait(false);
                if (outcome.Stop is { } stop)
                {
                    return stop;
                }

                deepFailures = outcome.Failures;
                nextDeep = clock.GetUtcNow() + (outcome.RetryIn ?? TimeSpan.FromSeconds(options.DeepSweepIntervalSeconds));
            }

            try
            {
                await Task.Delay(Tick, clock, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return SearchIndexBuilderStopReason.Cancelled;
            }
        }

        return SearchIndexBuilderStopReason.Cancelled;
    }

    private sealed record PassOutcome(int Failures, TimeSpan? RetryIn, SearchIndexBuilderStopReason? Stop);

    private async Task<PassOutcome> RunPassAsync(string pass, Func<CancellationToken, Task> run, int failures, CancellationToken stoppingToken)
    {
        try
        {
            await run(stoppingToken).ConfigureAwait(false);
            return new PassOutcome(0, null, null);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            return new PassOutcome(failures, null, SearchIndexBuilderStopReason.Cancelled);
        }
        catch (Exception ex)
        {
            var code = (ex as AuthorityClientException)?.ErrorCode ?? string.Empty;
            var kind = SearchIndexBuilderFailurePolicy.Classify(ex);
            if (kind == SearchIndexBuilderFailureKind.CredentialRefused)
            {
                observer.CredentialRefused(pass, code);
                return new PassOutcome(failures + 1, null, SearchIndexBuilderStopReason.CredentialRefused);
            }

            var retryIn = SearchIndexBuilderFailurePolicy.Backoff(failures + 1, BackoffBase, BackoffCap);
            observer.PassFailed(pass, kind, ex.GetType().Name, code, failures + 1, retryIn);
            return new PassOutcome(failures + 1, retryIn, null);
        }
    }
}
