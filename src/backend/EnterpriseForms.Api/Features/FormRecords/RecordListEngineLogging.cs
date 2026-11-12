using EnterpriseForms.Application.Features.FormRecords.ListRecords;

namespace EnterpriseForms.Api.Features.FormRecords;

/// <summary>IX-6: which engine answered a My Records listing and, for a fallback, the Authority's refusal code (codes only; no ids, no filters).</summary>
public sealed partial class RecordListEngineLogging(ILogger<RecordListEngineLogging> logger) : IRecordListEngineObserver
{
    public void Listed(string engine, string? fallbackReason) => LogListed(logger, engine, fallbackReason ?? "-");

    [LoggerMessage(EventId = 9335, Level = LogLevel.Information, Message = "IX6-RECORD-LIST engine={Engine} fallback_reason={FallbackReason}")]
    private static partial void LogListed(ILogger logger, string engine, string fallbackReason);
}
