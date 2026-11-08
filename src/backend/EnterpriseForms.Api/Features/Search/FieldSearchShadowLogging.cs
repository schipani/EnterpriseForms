using EnterpriseForms.Application.Features.Search.FieldSearch;

namespace EnterpriseForms.Api.Features.Search;

/// <summary>
/// IX-3 Shadow evidence (R1 §G.3): every comparison is logged with its classification, counts, error codes and duration only —
/// never record ids, values or predicates. A Difference is a Warning (the alert of R1 §G.3); a legitimate or non-comparable
/// outcome is Information; skips are Debug.
/// </summary>
public sealed partial class FieldSearchShadowLogging(ILogger<FieldSearchShadowLogging> logger) : IFieldSearchShadowObserver
{
    public void Compared(FieldSearchShadowComparison comparison)
    {
        ArgumentNullException.ThrowIfNull(comparison);
        switch (comparison.Classification)
        {
            case "Difference":
                LogDifference(logger, comparison.VerifiedScanCount, comparison.IndexedCount, comparison.VerifiedScanError ?? "-", comparison.IndexedError ?? "-", comparison.FirstDifferingPosition, comparison.ElapsedMilliseconds);
                break;
            case "Skipped":
                LogSkipped(logger, comparison.SkippedReason ?? "-");
                break;
            default:
                LogCompared(logger, comparison.Classification, comparison.VerifiedScanCount, comparison.IndexedCount, comparison.VerifiedScanError ?? "-", comparison.IndexedError ?? "-", comparison.ElapsedMilliseconds);
                break;
        }
    }

    [LoggerMessage(EventId = 9330, Level = LogLevel.Warning, Message = "IX3-SHADOW Difference v1_count={VerifiedCount} indexed_count={IndexedCount} v1_error={VerifiedError} indexed_error={IndexedError} first_differing_position={Position} elapsed_ms={Elapsed}")]
    private static partial void LogDifference(ILogger logger, int? verifiedCount, int? indexedCount, string verifiedError, string indexedError, int? position, long elapsed);

    [LoggerMessage(EventId = 9331, Level = LogLevel.Information, Message = "IX3-SHADOW {Classification} v1_count={VerifiedCount} indexed_count={IndexedCount} v1_error={VerifiedError} indexed_error={IndexedError} elapsed_ms={Elapsed}")]
    private static partial void LogCompared(ILogger logger, string classification, int? verifiedCount, int? indexedCount, string verifiedError, string indexedError, long elapsed);

    [LoggerMessage(EventId = 9332, Level = LogLevel.Debug, Message = "IX3-SHADOW Skipped reason={Reason}")]
    private static partial void LogSkipped(ILogger logger, string reason);
}

/// <summary>IX-4 engine evidence: a V1 fallback (IX-D8) is logged with the index's reason and the V1 outcome only — no ids, values or predicates.</summary>
public sealed partial class FieldSearchEngineLogging(ILogger<FieldSearchEngineLogging> logger) : IFieldSearchEngineObserver
{
    public void FallbackToVerifiedScan(string indexReason, string outcome) => LogFallback(logger, indexReason, outcome);

    [LoggerMessage(EventId = 9333, Level = LogLevel.Information, Message = "IX4-ENGINE fallback index_reason={IndexReason} verified_scan_outcome={Outcome}")]
    private static partial void LogFallback(ILogger logger, string indexReason, string outcome);
}
