using AuthorizationAuthority.Api.Composition;
using AuthorizationAuthority.Application.Common;
using AuthorizationAuthority.Application.Features.SecurityCommands;
using AuthorizationAuthority.Contracts.V1;
using Microsoft.AspNetCore.Http.HttpResults;

namespace AuthorizationAuthority.Api.Features.SecurityCommands;

public static class SecurityCommandEndpoints
{
    public const string IdempotencyHeader = "Idempotency-Key";

    public static IEndpointRouteBuilder MapSecurityCommandEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup(InternalApi.Prefix + "/security-commands").WithTags("SecurityCommands").RequireAuthorization(InternalAuthentication.WorkloadPolicy);

        group.MapPost("/", static async Task<Accepted<OperationStatus>> (SecurityCommandRequest request, HttpContext context, SecurityCommandService service, DelegatedUserAssertionValidator validator, CancellationToken cancellationToken) =>
            {
                var actor = InternalAuthentication.RequireActor(context, validator);
                var key = RequireIdempotencyKey(context);
                var status = await service.SubmitAsync(actor, request, key, InternalAuthentication.Correlation(context), cancellationToken).ConfigureAwait(false);
                return TypedResults.Accepted((string?)null, status);
            })
            .WithName("SubmitSecurityCommand")
            .WithSummary("Validated, idempotent, revision-bound security mutation. 202 means committed, not effective; poll until Effective.");

        group.MapGet("/{commandId:guid}", static async Task<Ok<OperationStatus>> (Guid commandId, HttpContext context, SecurityCommandService service, DelegatedUserAssertionValidator validator, CancellationToken cancellationToken) =>
            {
                var actor = InternalAuthentication.RequireActor(context, validator);
                return TypedResults.Ok(await service.GetStatusAsync(actor, commandId, InternalAuthentication.Correlation(context), cancellationToken).ConfigureAwait(false));
            })
            .WithName("GetSecurityCommandStatus");

        return endpoints;
    }

    public static string RequireIdempotencyKey(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var key = context.Request.Headers[IdempotencyHeader].ToString();
        if (string.IsNullOrWhiteSpace(key))
        {
            throw AuthorityException.Invalid("Idempotency-Key header required");
        }

        return key;
    }
}
