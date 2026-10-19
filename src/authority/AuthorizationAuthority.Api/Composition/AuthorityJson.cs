using System.Text.Json;
using System.Text.Json.Serialization;
using AuthorizationAuthority.Application.Common;
using AuthorizationAuthority.Contracts.V1;
using Microsoft.AspNetCore.Diagnostics;

namespace AuthorizationAuthority.Api.Composition;

public static class AuthorityJson
{
    /// <summary>snake_case wire format for the private contract (trust-authority.openapi.yaml).</summary>
    public static void Configure(JsonSerializerOptions options)
    {
        options.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower;
        options.DictionaryKeyPolicy = JsonNamingPolicy.SnakeCaseLower;
        options.Converters.Add(new JsonStringEnumConverter());
        options.DefaultIgnoreCondition = JsonIgnoreCondition.Never;
    }
}

/// <summary>Maps application errors to the stable ErrorResponse family; never leaks stack traces, keys or protected values.</summary>
public sealed class AuthorityExceptionHandler : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        var correlation = InternalAuthentication.Correlation(httpContext);
        ErrorResponse body;
        int status;
        switch (exception)
        {
            case AuthorityException ex:
                status = ex.HttpStatusCode;
                body = new ErrorResponse(ex.ErrorCode, correlation, ex.Retryable, ex.Detail);
                break;
            case Application.Common.Ports.SpiceDbUnavailableException:
                status = 503;
                body = new ErrorResponse(AuthorityErrorCodes.AuthorityNotReady, correlation, true, "projection engine unavailable");
                break;
            case Application.Common.Ports.IntegrityProviderUnavailableException:
                status = 503;
                body = new ErrorResponse(AuthorityErrorCodes.SigningUnavailable, correlation, true, "signing provider unavailable");
                break;
            case Application.Common.Ports.ArchiveUnavailableException:
                status = 503;
                body = new ErrorResponse(AuthorityErrorCodes.ArchiveUnavailable, correlation, true, "archive unavailable");
                break;
            case BadHttpRequestException:
            case JsonException:
                status = 422;
                body = new ErrorResponse(AuthorityErrorCodes.InvalidCommand, correlation, false, "request body invalid");
                break;
            case Npgsql.NpgsqlException:
            case Microsoft.EntityFrameworkCore.DbUpdateException:
                status = 503;
                body = new ErrorResponse(AuthorityErrorCodes.AuthorityNotReady, correlation, true, "authority store unavailable");
                break;
            default:
                return false;
        }

        httpContext.Response.StatusCode = status;
        httpContext.Response.Headers[InternalAuthentication.CorrelationHeader] = correlation.ToString();
        await httpContext.Response.WriteAsJsonAsync(body, cancellationToken).ConfigureAwait(false);
        return true;
    }
}
