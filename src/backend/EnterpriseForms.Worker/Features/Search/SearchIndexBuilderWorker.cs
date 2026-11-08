using EnterpriseForms.Application.Features.Search.IndexBuilder;

namespace EnterpriseForms.Worker.Features.Search;

/// <summary>
/// IX-2 (IX-D6): hosts the search-index builder loop under its own workload credential (registered in this host only). The loop
/// itself — scheduling, failure classification, bounded backoff, fail-closed stop on a refused credential, cancellation — is
/// <see cref="SearchIndexBuilderRunner"/> (C-129 §10, unit-tested). Unconfigured => idle. Nothing escapes except cancellation,
/// so a builder failure never stops the Worker host and its other services.
/// </summary>
public sealed partial class SearchIndexBuilderWorker(SearchIndexBuilderWorkflow workflow, SearchIndexBuilderOptions options, TimeProvider clock, ILogger<SearchIndexBuilderWorker> logger) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        new SearchIndexBuilderRunner(workflow, options, clock, new LoggingObserver(logger)).RunAsync(stoppingToken);

    /// <summary>Counts, exception type names and error codes only; never record contents, values, names or keys.</summary>
    private sealed partial class LoggingObserver(ILogger logger) : ISearchIndexBuilderObserver
    {
        public void Idle() => LogIdle(logger);

        public void Started(int backfillIntervalSeconds, int deepSweepIntervalSeconds, int recordsPerSecond) => LogStarted(logger, backfillIntervalSeconds, deepSweepIntervalSeconds, recordsPerSecond);

        public void BackfillCompleted(SearchIndexBuilderPassResult result)
        {
            if (result.FormsVisited > 0)
            {
                LogBackfill(logger, result.FormsVisited, result.RecordsSubmitted, result.RecordsIndexed, result.RecordsAlreadyIndexed, result.HeadChanged, result.Refused, result.RecordsQuarantined, result.CoveragesCompleted, result.Elapsed.TotalMilliseconds);
            }
        }

        public void DeepSweepCompleted(SearchIndexDeepSweepPassResult result) =>
            LogDeep(logger, result.OrganizationsVisited, result.EntriesSubmitted, result.Passed, result.Failed, result.Inconclusive, result.RecordsQuarantined, result.Elapsed.TotalMilliseconds);

        public void PassFailed(string pass, SearchIndexBuilderFailureKind kind, string exceptionType, string errorCode, int consecutiveFailures, TimeSpan retryIn)
        {
            if (kind == SearchIndexBuilderFailureKind.Transient)
            {
                LogTransient(logger, pass, exceptionType, errorCode, consecutiveFailures, retryIn.TotalSeconds);
            }
            else
            {
                LogUnexpected(logger, pass, exceptionType, errorCode, consecutiveFailures, retryIn.TotalSeconds);
            }
        }

        public void CredentialRefused(string pass, string errorCode) => LogCredentialRefused(logger, pass, errorCode);

        [LoggerMessage(Level = LogLevel.Information, Message = "Search index builder not configured in this host; idle (fail closed).")]
        private static partial void LogIdle(ILogger logger);

        [LoggerMessage(Level = LogLevel.Information, Message = "Search index builder started: backfill every {Backfill} s, deep sweep every {Deep} s, at most {Rate} records/s.")]
        private static partial void LogStarted(ILogger logger, int backfill, int deep, int rate);

        [LoggerMessage(EventId = 9310, Level = LogLevel.Information, Message = "IX2-BACKFILL forms={Forms} submitted={Submitted} indexed={Indexed} already={Already} head_changed={HeadChanged} refused={Refused} quarantined={Quarantined} coverages_completed={Completed} elapsed_ms={Elapsed}")]
        private static partial void LogBackfill(ILogger logger, int forms, int submitted, int indexed, int already, int headChanged, int refused, int quarantined, int completed, double elapsed);

        [LoggerMessage(EventId = 9311, Level = LogLevel.Information, Message = "IX2-DEEP-SWEEP organizations={Organizations} submitted={Submitted} pass={Passed} fail={Failed} inconclusive={Inconclusive} record_quarantines={Quarantined} elapsed_ms={Elapsed}")]
        private static partial void LogDeep(ILogger logger, int organizations, int submitted, int passed, int failed, int inconclusive, int quarantined, double elapsed);

        [LoggerMessage(EventId = 9312, Level = LogLevel.Warning, Message = "Search index builder {Pass} pass failed transiently ({ExceptionType} {ErrorCode}); failure {Failures}, retry in {RetrySeconds} s.")]
        private static partial void LogTransient(ILogger logger, string pass, string exceptionType, string errorCode, int failures, double retrySeconds);

        [LoggerMessage(EventId = 9313, Level = LogLevel.Error, Message = "Search index builder {Pass} pass failed unexpectedly ({ExceptionType} {ErrorCode}); failure {Failures}, retry in {RetrySeconds} s.")]
        private static partial void LogUnexpected(ILogger logger, string pass, string exceptionType, string errorCode, int failures, double retrySeconds);

        [LoggerMessage(EventId = 9314, Level = LogLevel.Critical, Message = "Search index builder stopped: the Authority refuses the builder credential during the {Pass} pass ({ErrorCode}). Fail closed; restart the Worker with a valid credential.")]
        private static partial void LogCredentialRefused(ILogger logger, string pass, string errorCode);
    }
}
