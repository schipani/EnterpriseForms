using System.Text.Json;
using System.Text.Json.Serialization;

namespace EnterpriseForms.PlatformConsole.Composition;

/// <summary>A refusal with a stable, safe error code (RFC 9457 body). Never carries tokens, evidence, keys or cookies.</summary>
public sealed class ConsoleException(string errorCode, int status, string detail, bool retryable = false, Guid? correlationId = null) : Exception(errorCode + ": " + detail)
{
    public string ErrorCode { get; } = errorCode;

    public int Status { get; } = status;

    public string Detail { get; } = detail;

    public bool Retryable { get; } = retryable;

    public Guid? CorrelationId { get; } = correlationId;
}

public static class ConsoleErrorCodes
{
    public const string SessionRequired = "SESSION_REQUIRED";
    public const string CsrfTokenInvalid = "CSRF_TOKEN_INVALID";
    public const string OriginNotAllowed = "ORIGIN_NOT_ALLOWED";
    public const string InvalidRequest = "INVALID_REQUEST";
    public const string UnknownOperator = "UNKNOWN_SYNTHETIC_OPERATOR";
    public const string OperatorIdentityMismatch = "OPERATOR_IDENTITY_MISMATCH";
    public const string SigningKeyMismatch = "OPERATOR_SIGNING_KEY_MISMATCH";
    public const string SigningUnavailable = "OPERATOR_SIGNING_UNAVAILABLE";
    public const string AuthorityUnavailable = "AUTHORITY_UNAVAILABLE";
    public const string AuthorityCredentialRejected = "PLATFORM_CONSOLE_CREDENTIAL_REJECTED";
    public const string RateLimited = "RATE_LIMITED";
    public const string NotFound = "NOT_FOUND";

    // Authority codes the console reacts to (mirrors AuthorityErrorCodes; pinned by the architecture tests).
    public const string PlatformOperatorNotActive = "PLATFORM_OPERATOR_NOT_ACTIVE";
    public const string ApprovalStatementChanged = "APPROVAL_STATEMENT_CHANGED";
}

/// <summary>Problem body: <c>title</c> and <c>errorCode</c> both carry the stable code (the same shape as the business API).</summary>
public sealed record ConsoleProblem(
    string Type,
    string Title,
    int Status,
    string Detail,
    string ErrorCode,
    bool Retryable,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] Guid? CorrelationId);

public static class ConsoleProblems
{
    public static ConsoleProblem From(ConsoleException ex)
    {
        ArgumentNullException.ThrowIfNull(ex);
        return new ConsoleProblem("https://enterpriseforms.invalid/problems/" + ex.ErrorCode.ToLowerInvariant().Replace('_', '-'), ex.ErrorCode, ex.Status, ex.Detail, ex.ErrorCode, ex.Retryable, ex.CorrelationId);
    }

    public static async Task WriteAsync(HttpContext context, ConsoleException ex)
    {
        ArgumentNullException.ThrowIfNull(context);
        context.Response.StatusCode = ex.Status;
        if (ex.Retryable && ex.Status == StatusCodes.Status503ServiceUnavailable)
        {
            context.Response.Headers.RetryAfter = "5";
        }

        await context.Response.WriteAsJsonAsync(From(ex), ConsoleJson.Browser, "application/problem+json").ConfigureAwait(false);
    }
}

/// <summary>Maps <see cref="ConsoleException"/> to a problem body; anything else is a generic 500 without internals.</summary>
public sealed partial class ConsoleExceptionHandler(ILogger<ConsoleExceptionHandler> logger) : Microsoft.AspNetCore.Diagnostics.IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        var refusal = exception switch
        {
            ConsoleException ex => ex,
            BadHttpRequestException => new ConsoleException(ConsoleErrorCodes.InvalidRequest, 400, "the request body is not valid for this operation (unknown or malformed fields are refused)"),
            JsonException => new ConsoleException(ConsoleErrorCodes.InvalidRequest, 400, "the request body is not valid JSON for this operation"),
            _ => null,
        };
        if (refusal is null)
        {
            LogUnhandled(logger, exception.GetType().Name);
            refusal = new ConsoleException("INTERNAL_ERROR", 500, "the request could not be completed");
        }

        await ConsoleProblems.WriteAsync(httpContext, refusal).ConfigureAwait(false);
        return true;
    }

    // Type name only: exception messages may contain request data.
    [LoggerMessage(EventId = 3901, Level = LogLevel.Error, Message = "Unhandled {ExceptionType} in the Platform Console")]
    private static partial void LogUnhandled(ILogger logger, string exceptionType);
}

public static class ConsoleJson
{
    /// <summary>Browser-facing JSON (camelCase, string enums). Request bodies with unknown members are refused, so a browser cannot smuggle an operator identity, evidence or key.</summary>
    public static JsonSerializerOptions Browser { get; } = CreateBrowser();

    /// <summary>The Authority's private wire format (snake_case).</summary>
    public static JsonSerializerOptions Authority { get; } = CreateAuthority();

    public static void ConfigureBrowser(JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
        options.PropertyNameCaseInsensitive = false;
        options.UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow;
        options.Converters.Add(new JsonStringEnumConverter());
    }

    private static JsonSerializerOptions CreateBrowser()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        ConfigureBrowser(options);
        return options;
    }

    private static JsonSerializerOptions CreateAuthority()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            DictionaryKeyPolicy = JsonNamingPolicy.SnakeCaseLower,
            DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        };
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }
}
