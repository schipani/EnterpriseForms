namespace EnterpriseForms.PlatformConsole.Composition;

/// <summary>
/// Operational log events. Identifiers and codes only: never a cookie, session id, CSRF token, assertion, evidence, Vault token,
/// justification text or request reason. The Authority's journals, not these logs, are the security evidence.
/// </summary>
public static partial class ConsoleLog
{
    [LoggerMessage(EventId = 3101, Level = LogLevel.Information, Message = "Platform operator {PlatformOperatorId} signed in (correlation {CorrelationId})")]
    public static partial void SignedIn(ILogger logger, Guid platformOperatorId, Guid correlationId);

    [LoggerMessage(EventId = 3102, Level = LogLevel.Information, Message = "Platform operator {PlatformOperatorId} signed out")]
    public static partial void SignedOut(ILogger logger, Guid platformOperatorId);

    [LoggerMessage(EventId = 3103, Level = LogLevel.Warning, Message = "Sessions of platform operator {PlatformOperatorId} ended ({ErrorCode})")]
    public static partial void SessionEnded(ILogger logger, Guid platformOperatorId, string errorCode);

    [LoggerMessage(EventId = 3201, Level = LogLevel.Information, Message = "Operator {PlatformOperatorId} {Decision} on request {RequestId}: {Outcome} (correlation {CorrelationId})")]
    public static partial void Decided(ILogger logger, Guid platformOperatorId, string decision, Guid requestId, string outcome, Guid correlationId);

    [LoggerMessage(EventId = 3202, Level = LogLevel.Warning, Message = "Operator {PlatformOperatorId} {Decision} on request {RequestId} refused: {ErrorCode} (correlation {CorrelationId})")]
    public static partial void DecisionRefused(ILogger logger, Guid platformOperatorId, string decision, Guid requestId, string errorCode, Guid correlationId);
}
