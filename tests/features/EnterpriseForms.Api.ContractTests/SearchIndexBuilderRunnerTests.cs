using System.Diagnostics;
using EnterpriseForms.Application.Common.Authority;
using EnterpriseForms.Application.Features.FormRecords.Ports;
using EnterpriseForms.Application.Features.Search.IndexBuilder;
using Xunit;

namespace EnterpriseForms.Api.ContractTests;

/// <summary>
/// IX-2 (C-129 §10): the search-index builder's error handling and shutdown. Transient failures back off with a bounded
/// exponential delay; a refused credential stops the builder (fail closed) without throwing; unexpected failures are reported
/// and never escape; cancellation ends the loop promptly from a pass or a delay; a failed backfill never reaches the
/// attestation of its form.
/// </summary>
public sealed class SearchIndexBuilderRunnerTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly SearchIndexBuilderPassResult EmptyPass = new(0, 0, 0, 0, 0, 0, 0, 0, new Dictionary<string, int>(), TimeSpan.Zero);

    private sealed class ScriptedPasses(params Func<CancellationToken, Task<SearchIndexBuilderPassResult>>[] script) : ISearchIndexBuilderPasses
    {
        private int call;

        public bool IsConfigured { get; init; } = true;

        public int Calls => call;

        public Task<SearchIndexBuilderPassResult> RunBackfillRoundAsync(CancellationToken cancellationToken)
        {
            var index = Interlocked.Increment(ref call) - 1;
            return index < script.Length ? script[index](cancellationToken) : Task.FromResult(EmptyPass);
        }

        public Task<SearchIndexDeepSweepPassResult> RunDeepSweepPassAsync(CancellationToken cancellationToken) => Task.FromResult(new SearchIndexDeepSweepPassResult(0, 0, 0, 0, 0, 0, TimeSpan.Zero));
    }

    private sealed class RecordingObserver : ISearchIndexBuilderObserver
    {
        public List<(string Pass, SearchIndexBuilderFailureKind Kind, string Type, string Code, int Failures, TimeSpan RetryIn)> Failures { get; } = [];

        public List<string> Refusals { get; } = [];

        public int Completed { get; private set; }

        public bool IdleReported { get; private set; }

        public void Idle() => IdleReported = true;

        public void Started(int backfillIntervalSeconds, int deepSweepIntervalSeconds, int recordsPerSecond)
        {
        }

        public void BackfillCompleted(SearchIndexBuilderPassResult result) => Completed++;

        public void DeepSweepCompleted(SearchIndexDeepSweepPassResult result)
        {
        }

        public void PassFailed(string pass, SearchIndexBuilderFailureKind kind, string exceptionType, string errorCode, int consecutiveFailures, TimeSpan retryIn) =>
            Failures.Add((pass, kind, exceptionType, errorCode, consecutiveFailures, retryIn));

        public void CredentialRefused(string pass, string errorCode) => Refusals.Add(pass + ":" + errorCode);
    }

    private static SearchIndexBuilderOptions Options() => new() { BackfillIntervalSeconds = 1, DeepSweepIntervalSeconds = 86_400, MaximumRecordsPerSecond = 1000 };

    private static SearchIndexBuilderRunner Runner(ISearchIndexBuilderPasses passes, RecordingObserver observer) =>
        new(passes, Options(), TimeProvider.System, observer) { Tick = TimeSpan.FromMilliseconds(10), BackoffBase = TimeSpan.FromMilliseconds(20), BackoffCap = TimeSpan.FromMilliseconds(80) };

    private static Func<CancellationToken, Task<SearchIndexBuilderPassResult>> Throws(Exception exception) => _ => Task.FromException<SearchIndexBuilderPassResult>(exception);

    public static TheoryData<Exception, SearchIndexBuilderFailureKind> Classifications => new()
    {
        { new AuthorityClientException("UNAUTHORIZED", 401, false, null), SearchIndexBuilderFailureKind.CredentialRefused },
        { new AuthorityClientException("FORBIDDEN", 403, false, null), SearchIndexBuilderFailureKind.CredentialRefused },
        { new AuthorityClientException("AUTHORITY_UNAVAILABLE", 503, true, null), SearchIndexBuilderFailureKind.Transient },
        { new AuthorityClientException("SEARCH_SEAL_UNAVAILABLE", 503, true, null), SearchIndexBuilderFailureKind.Transient },
        { new AuthorityClientException("SEARCH_INDEX_QUARANTINED", 409, false, null), SearchIndexBuilderFailureKind.Transient },
        { new AuthorityClientException("INTEGRITY_QUARANTINE", 423, false, null), SearchIndexBuilderFailureKind.Transient },
        { new AuthorityClientException("INVALID_COMMAND", 422, false, null), SearchIndexBuilderFailureKind.Unexpected },
        { new HttpRequestException("network"), SearchIndexBuilderFailureKind.Transient },
        { new IOException("io"), SearchIndexBuilderFailureKind.Transient },
        { new TimeoutException("timeout"), SearchIndexBuilderFailureKind.Transient },
        { new System.Text.Json.JsonException("bad response"), SearchIndexBuilderFailureKind.Unexpected },
        { new KeyNotFoundException("defect"), SearchIndexBuilderFailureKind.Unexpected },
    };

    [Theory]
    [MemberData(nameof(Classifications))]
    public void EveryFailureIsClassified(Exception exception, SearchIndexBuilderFailureKind expected) => Assert.Equal(expected, SearchIndexBuilderFailurePolicy.Classify(exception));

    [Fact]
    public void BackoffDoublesAndIsBounded()
    {
        var baseDelay = TimeSpan.FromSeconds(2);
        var cap = TimeSpan.FromMinutes(5);
        Assert.Equal([2, 4, 8, 16, 32, 64, 128, 256, 300, 300], Enumerable.Range(1, 10).Select(n => SearchIndexBuilderFailurePolicy.Backoff(n, baseDelay, cap).TotalSeconds));
        Assert.Equal(cap, SearchIndexBuilderFailurePolicy.Backoff(int.MaxValue, baseDelay, cap));
    }

    [Fact]
    public async Task TransientFailuresAreRetriedWithGrowingDelaysAndAPassResetsTheCount()
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var observer = new RecordingObserver();
        var passes = new ScriptedPasses(
            Throws(new HttpRequestException("down")), Throws(new AuthorityClientException("AUTHORITY_UNAVAILABLE", 503, true, null)), Throws(new IOException("reset")),
            _ => { return Task.FromResult(EmptyPass); },
            _ => { stop.Cancel(); return Task.FromResult(EmptyPass); });
        Assert.Equal(SearchIndexBuilderStopReason.Cancelled, await Runner(passes, observer).RunAsync(stop.Token));
        Assert.Equal([1, 2, 3], observer.Failures.Select(f => f.Failures));
        Assert.All(observer.Failures, f => Assert.Equal(SearchIndexBuilderFailureKind.Transient, f.Kind));
        Assert.Equal([20, 40, 80], observer.Failures.Select(f => f.RetryIn.TotalMilliseconds));
        Assert.Equal("AUTHORITY_UNAVAILABLE", observer.Failures[1].Code);
        Assert.True(observer.Completed >= 1);
    }

    [Fact]
    public async Task ARefusedCredentialStopsTheBuilderFailClosedWithoutThrowing()
    {
        var observer = new RecordingObserver();
        var passes = new ScriptedPasses(Throws(new AuthorityClientException("UNAUTHORIZED", 401, false, null)));
        Assert.Equal(SearchIndexBuilderStopReason.CredentialRefused, await Runner(passes, observer).RunAsync(Ct));
        Assert.Equal(["backfill:UNAUTHORIZED"], observer.Refusals);
        Assert.Equal(1, passes.Calls); // never retried with a refused credential
        Assert.Empty(observer.Failures);
    }

    [Fact]
    public async Task AnUnexpectedFailureIsReportedAndNeverEscapes()
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var observer = new RecordingObserver();
        var passes = new ScriptedPasses(Throws(new System.Text.Json.JsonException("bad")), Throws(new InvalidOperationException("defect")), _ => { stop.Cancel(); return Task.FromResult(EmptyPass); });
        Assert.Equal(SearchIndexBuilderStopReason.Cancelled, await Runner(passes, observer).RunAsync(stop.Token));
        Assert.Equal(["JsonException", "InvalidOperationException"], observer.Failures.Select(f => f.Type));
        Assert.All(observer.Failures, f => Assert.Equal(SearchIndexBuilderFailureKind.Unexpected, f.Kind));
    }

    [Fact]
    public async Task CancellationDuringAPassEndsTheLoopPromptly()
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var observer = new RecordingObserver();
        var passes = new ScriptedPasses(async ct => { stop.CancelAfter(50); await Task.Delay(Timeout.Infinite, ct); return EmptyPass; });
        var clock = Stopwatch.StartNew();
        Assert.Equal(SearchIndexBuilderStopReason.Cancelled, await Runner(passes, observer).RunAsync(stop.Token));
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(5), "shutdown took " + clock.Elapsed);
        Assert.Empty(observer.Failures); // a shutdown is not a failure
    }

    [Fact]
    public async Task CancellationDuringTheDelayEndsTheLoopPromptly()
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var observer = new RecordingObserver();
        var passes = new ScriptedPasses(_ => { stop.CancelAfter(50); return Task.FromResult(EmptyPass); });
        var runner = new SearchIndexBuilderRunner(passes, new SearchIndexBuilderOptions { BackfillIntervalSeconds = 3600, DeepSweepIntervalSeconds = 86_400 }, TimeProvider.System, observer) { Tick = TimeSpan.FromSeconds(30) };
        var clock = Stopwatch.StartNew();
        Assert.Equal(SearchIndexBuilderStopReason.Cancelled, await runner.RunAsync(stop.Token));
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(5), "shutdown took " + clock.Elapsed);
    }

    [Fact]
    public async Task AnUnconfiguredBuilderIsIdle()
    {
        var observer = new RecordingObserver();
        Assert.Equal(SearchIndexBuilderStopReason.NotConfigured, await Runner(new ScriptedPasses { IsConfigured = false }, observer).RunAsync(Ct));
        Assert.True(observer.IdleReported);
    }

    // ---- workflow: a failed backfill never reaches the attestation of its form --------------------------------------

    private sealed class FailingBackfillAuthority : ISearchIndexBuilderAuthority
    {
        public int Attestations { get; private set; }

        public bool IsConfigured => true;

        public Task<SearchIndexBuilderWork> GetWorkAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new SearchIndexBuilderWork([new SearchIndexBuildingForm(Guid.NewGuid(), Guid.NewGuid(), 1, "Active")], [], Guid.NewGuid()));

        public Task<SearchIndexPopulationPage> ReadPopulationAsync(Guid organizationId, Guid formDefinitionId, Guid? afterFormRecordId, int limit, CancellationToken cancellationToken) =>
            Task.FromResult(new SearchIndexPopulationPage(organizationId, formDefinitionId, [new SearchIndexPopulationEntry(Guid.NewGuid(), 1, Guid.NewGuid(), false, false, true)], null, Guid.NewGuid()));

        public Task<SearchIndexBackfillResponse> BackfillAsync(Guid organizationId, Guid formDefinitionId, IReadOnlyList<SearchIndexBackfillItem> records, CancellationToken cancellationToken) =>
            throw new AuthorityClientException("SEARCH_SEAL_UNAVAILABLE", 503, true, "seal provider outage");

        public Task<SearchIndexAttestation> AttestAsync(Guid organizationId, Guid formDefinitionId, CancellationToken cancellationToken)
        {
            Attestations++;
            throw new InvalidOperationException("must not be reached");
        }

        public Task<SearchIndexDeepSweepWork> GetDeepSweepWorkAsync(Guid organizationId, int oldestLimit, int sampleLimit, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<SearchIndexDeepVerifyResponse> DeepVerifyAsync(Guid organizationId, IReadOnlyList<SearchIndexBackfillItem> records, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class EmptyReader : IAcceptedRecordItemsReader
    {
        public Task<IReadOnlyList<StagedContentItem>> ReadObservedItemsAsync(Guid formRecordId, Guid committedOperationId, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<StagedContentItem>>([]);

        public Task<AcceptedRecordContent> ReadAcceptedContentAsync(Guid formRecordId, Guid committedOperationId, CancellationToken cancellationToken) => Task.FromResult(new AcceptedRecordContent([], null, []));
    }

    [Fact]
    public async Task AFailedBackfillNeverReachesTheAttestationOfItsForm()
    {
        var authority = new FailingBackfillAuthority();
        var workflow = new SearchIndexBuilderWorkflow(authority, new EmptyReader(), Options(), TimeProvider.System);
        var ex = await Assert.ThrowsAsync<AuthorityClientException>(() => workflow.RunBackfillRoundAsync(Ct));
        Assert.Equal("SEARCH_SEAL_UNAVAILABLE", ex.ErrorCode);
        Assert.Equal(0, authority.Attestations);
        Assert.Equal(SearchIndexBuilderFailureKind.Transient, SearchIndexBuilderFailurePolicy.Classify(ex));
    }
}
