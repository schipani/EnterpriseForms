using EnterpriseForms.Application.Common.Authority;
using EnterpriseForms.Application.Features.Search.FieldSearch;
using Xunit;

namespace EnterpriseForms.Api.ContractTests;

/// <summary>
/// C-150 R56-A: the bounded jittered backoff itself — which refusals qualify, the delay steps and their jitter, and the three bounds
/// (attempts, total time, the caller's remaining deadline). Engine and handler behaviour is in IndexedFieldSearchEngineTests and
/// PhaseC7FieldSearchExecutionTests.
/// </summary>
public sealed class FieldSearchRetryPolicyTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class Observer : IFieldSearchEngineObserver
    {
        public List<double> Delays { get; } = [];

        public void FallbackToVerifiedScan(string indexReason, string outcome)
        {
        }

        public void Retrying(Guid correlationId, string stage, string code, int retryNumber, double delayMilliseconds) => Delays.Add(delayMilliseconds);
    }

    /// <summary>
    /// A clock that advances by exactly each requested delay and completes it at once, so the step and cap assertions do not
    /// depend on wall-clock scheduling (on a loaded machine a real 10 ms delay can take seconds and exhaust the total budget).
    /// </summary>
    private sealed class SteppingClock : TimeProvider
    {
        private long ticks;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => Interlocked.Read(ref ticks);

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            Interlocked.Add(ref ticks, dueTime.Ticks);
            ThreadPool.UnsafeQueueUserWorkItem(_ => callback(state), null);
            return new Fired();
        }

        private sealed class Fired : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period) => false;

            public void Dispose()
            {
            }

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    private static FieldSearchLimits Limits(int attempts = 4, int first = 10, int largest = 40, int total = 2000) =>
        new() { RetryMaximumAttempts = attempts, RetryInitialDelayMilliseconds = first, RetryMaximumDelayMilliseconds = largest, RetryMaximumTotalMilliseconds = total };

    [Theory]
    [InlineData("AUTHORITY_NOT_READY", 503, true, true)]
    [InlineData("SEARCH_SNAPSHOT_CHANGED", 409, true, true)]
    [InlineData("AUTHORITY_NOT_READY", 503, false, false)]
    [InlineData("SEARCH_SNAPSHOT_CHANGED", 409, false, false)]
    [InlineData("AUTHORITY_NOT_READY", 409, true, false)]
    [InlineData("SEARCH_SNAPSHOT_CHANGED", 503, true, false)]
    [InlineData("AUTHORITY_UNAVAILABLE", 503, true, false)]
    [InlineData("INTEGRITY_QUARANTINE", 423, true, false)]
    [InlineData("SEARCH_CAPACITY_EXCEEDED", 503, true, false)]
    [InlineData("FORBIDDEN", 403, false, false)]
    public void OnlyTheTwoExplicitlyRetryableRefusalsQualify(string code, int status, bool retryable, bool expected) =>
        Assert.Equal(expected, FieldSearchRetryPolicy.IsRetryable(new AuthorityClientException(code, status, retryable)));

    [Theory]
    [InlineData(0.0, new[] { 5.0, 10.0, 20.0 })]
    [InlineData(1.0, new[] { 10.0, 20.0, 40.0 })]
    public async Task StepsDoubleUpToTheLargestDelayWithinTheirJitterRange(double jitter, double[] expected)
    {
        var observer = new Observer();
        var policy = new FieldSearchRetryPolicy(Limits(attempts: 4, first: 10, largest: 40), new SteppingClock(), observer, () => jitter);
        var budget = policy.Begin();
        for (var i = 0; i < 3; i++)
        {
            Assert.True(await policy.WaitBeforeRetryAsync(budget, "test", "SEARCH_SNAPSHOT_CHANGED", Guid.Empty, null, Ct));
        }

        Assert.False(await policy.WaitBeforeRetryAsync(budget, "test", "SEARCH_SNAPSHOT_CHANGED", Guid.Empty, null, Ct)); // 4 attempts = 3 retries
        Assert.Equal(expected, observer.Delays);
        Assert.Equal(3, budget.Retries);

        var capped = new Observer();
        var many = new FieldSearchRetryPolicy(Limits(attempts: 6, first: 10, largest: 20), new SteppingClock(), capped, () => 1.0);
        var b = many.Begin();
        while (await many.WaitBeforeRetryAsync(b, "test", "AUTHORITY_NOT_READY", Guid.Empty, null, Ct))
        {
        }

        Assert.Equal([10.0, 20.0, 20.0, 20.0, 20.0], capped.Delays); // never above the largest step
    }

    [Fact]
    public async Task OneAttemptMeansNoRetryAndAnExhaustedTotalBudgetStopsRetrying()
    {
        var none = new FieldSearchRetryPolicy(Limits(attempts: 1), TimeProvider.System);
        Assert.False(await none.WaitBeforeRetryAsync(none.Begin(), "test", "AUTHORITY_NOT_READY", Guid.Empty, null, Ct));

        var noTime = new FieldSearchRetryPolicy(Limits(total: 0), TimeProvider.System);
        Assert.False(await noTime.WaitBeforeRetryAsync(noTime.Begin(), "test", "AUTHORITY_NOT_READY", Guid.Empty, null, Ct));
    }

    [Fact]
    public async Task ARetryNeverStartsWhenItsWaitWouldExhaustTheCallersDeadline()
    {
        var policy = new FieldSearchRetryPolicy(Limits(first: 100, largest: 100), new SteppingClock(), null, () => 1.0);
        var budget = policy.Begin();
        Assert.False(await policy.WaitBeforeRetryAsync(budget, "test", "SEARCH_SNAPSHOT_CHANGED", Guid.Empty, TimeSpan.FromMilliseconds(50), Ct));
        Assert.Equal(0, budget.Retries);
        Assert.True(await policy.WaitBeforeRetryAsync(budget, "test", "SEARCH_SNAPSHOT_CHANGED", Guid.Empty, TimeSpan.FromSeconds(1), Ct));
    }

    [Fact]
    public async Task ConcurrentlyRefusedSearchesDoNotRetryTogether()
    {
        // Equal jitter over [1/2, 1] of the step: 200 budgets' first delays spread over the range instead of one instant.
        var observer = new Observer();
        var policy = new FieldSearchRetryPolicy(Limits(first: 10, largest: 10), TimeProvider.System, observer);
        await Task.WhenAll(Enumerable.Range(0, 200).Select(_ => policy.WaitBeforeRetryAsync(policy.Begin(), "test", "AUTHORITY_NOT_READY", Guid.Empty, null, Ct)));
        Assert.All(observer.Delays, d => Assert.InRange(d, 5.0, 10.0));
        Assert.True(observer.Delays.Distinct().Count() > 150, "jittered delays must differ");
        Assert.True(observer.Delays.Max() - observer.Delays.Min() > 3.0);
    }

    [Fact]
    public async Task TheWaitHonoursCancellation()
    {
        var policy = new FieldSearchRetryPolicy(Limits(first: 1000, largest: 1000, total: 5000), TimeProvider.System);
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => policy.WaitBeforeRetryAsync(policy.Begin(), "test", "AUTHORITY_NOT_READY", Guid.Empty, null, cancel.Token));
    }

    [Fact]
    public void RetryLimitsOutsideTheirRangesFailAtStartup()
    {
        foreach (var bad in new[] { Limits(attempts: 0), Limits(attempts: 7), Limits(first: 5), Limits(first: 100, largest: 50), Limits(largest: 3000), Limits(total: 6000) })
        {
            bad.ExecutionEnabled = false;
            Assert.Throws<InvalidOperationException>(bad.Validate);
        }

        Limits().Validate();
    }
}
