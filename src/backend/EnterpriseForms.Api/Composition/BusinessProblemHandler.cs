using EnterpriseForms.Application.Common;
using EnterpriseForms.Application.Common.Authority;
using EnterpriseForms.Application.Features.FormDefinitions.CreateFormDefinition;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace EnterpriseForms.Api.Composition;

/// <summary>RFC 9457 Problem Details with stable, non-leaking error codes (PB-06). Authority failures surface as fail-closed 503/403/423, never as allow.</summary>
public sealed class BusinessProblemHandler : IExceptionHandler
{
    private const string TypeBase = "https://enterpriseforms.invalid/problems/";

    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        ProblemDetails problem;
        switch (exception)
        {
            case Application.Features.Search.FieldSearch.SearchException ex:
                // ADR-PB-004 typed search failure: retry guidance and the reason, never a predicate value or field existence.
                problem = Build(ex.HttpStatusCode, ex.ErrorCode, ex.Message);
                problem.Extensions["retryable"] = ex.Retryable;
                if (ex.UnavailableReason is not null)
                {
                    problem.Extensions["unavailable_reason"] = ex.UnavailableReason;
                }

                if (ex.LimitKind is not null)
                {
                    problem.Extensions["limit_kind"] = ex.LimitKind;
                }

                break;
            case BusinessException ex:
                problem = Build(ex.HttpStatusCode, ex.ErrorCode, ex.Message);
                if (ex.FieldKeys.Count > 0)
                {
                    // Field keys of the caller's own submission (never values): lets the form mark the exact fields (C-31).
                    problem.Extensions["field_keys"] = ex.FieldKeys;
                }

                break;
            case AuthorityClientException ex:
                problem = Build(ex.HttpStatusCode, ex.ErrorCode, ex.Message);
                problem.Extensions["retryable"] = ex.Retryable;
                break;
            case PendingCommandException ex:
                problem = Build(StatusCodes.Status202Accepted, "COMMAND_PENDING", "security command committed but not yet effective; poll the command");
                problem.Extensions["command_id"] = ex.Outcome.CommandId;
                problem.Extensions["command_status"] = ex.Outcome.CommandStatus;
                break;
            case Application.Features.Documents.Ports.EncryptionUnavailableException:
                // Fail closed, generic: no key material, provider detail or value reaches the client.
                problem = Build(StatusCodes.Status503ServiceUnavailable, "ENCRYPTION_UNAVAILABLE", "encryption keys are unavailable; protected values cannot be written or read right now");
                break;
            case System.Security.Cryptography.CryptographicException:
                problem = Build(StatusCodes.Status423Locked, "FIELD_DECRYPTION_FAILED", "a protected value failed authenticated decryption; nothing was released");
                break;
            case BadHttpRequestException:
            case System.Text.Json.JsonException:
                problem = Build(StatusCodes.Status422UnprocessableEntity, "INVALID_REQUEST", "request body invalid");
                break;
            case Npgsql.NpgsqlException:
            case Microsoft.EntityFrameworkCore.DbUpdateException:
                problem = Build(StatusCodes.Status503ServiceUnavailable, "STORE_UNAVAILABLE", "business store unavailable");
                break;
            default:
                return false;
        }

        var correlation = Guid.TryParse(httpContext.Request.Headers[BusinessAuthentication.CorrelationHeader].ToString(), out var id) ? id : Guid.CreateVersion7();
        problem.Extensions["correlation_id"] = correlation;
        httpContext.Response.StatusCode = problem.Status ?? 500;
        httpContext.Response.Headers[BusinessAuthentication.CorrelationHeader] = correlation.ToString();
        await httpContext.Response.WriteAsJsonAsync(problem, cancellationToken: cancellationToken).ConfigureAwait(false);
        return true;
    }

    private static ProblemDetails Build(int status, string code, string detail) => new()
    {
        Status = status,
        Title = code,
        Type = TypeBase + code.ToLowerInvariant().Replace('_', '-'),
        Detail = detail,
    };
}
