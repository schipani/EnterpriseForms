using AuthorizationAuthority.Api.Composition;
using AuthorizationAuthority.Application.Features.Sessions;
using AuthorizationAuthority.Contracts.V1;
using Microsoft.AspNetCore.Http.HttpResults;

namespace AuthorizationAuthority.Api.Features.Sessions;

/// <summary>G23: server-side browser session state. Business workload credential plus the session user's delegated assertion.</summary>
public static class SessionEndpoints
{
    public static IEndpointRouteBuilder MapSessionEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup(InternalApi.Prefix).WithTags("Sessions").RequireAuthorization(InternalAuthentication.WorkloadPolicy);

        group.MapPost("/sessions", static async Task<Ok<SessionStatus>> (CreateSessionRequest request, HttpContext context, SessionService service, DelegatedUserAssertionValidator validator, CancellationToken cancellationToken) =>
            {
                var actor = InternalAuthentication.RequireActor(context, validator);
                return TypedResults.Ok(await service.CreateAsync(actor, request, InternalAuthentication.Correlation(context), cancellationToken).ConfigureAwait(false));
            })
            .WithName("CreateSession")
            .WithSummary("Registers the SHA-256 digest of a new session secret for the delegated user (bound identity required) and revokes the session it replaces.");

        group.MapPost("/sessions/validate", static async Task<Ok<SessionStatus>> (SessionDigestRequest request, HttpContext context, SessionService service, DelegatedUserAssertionValidator validator, CancellationToken cancellationToken) =>
            {
                var actor = InternalAuthentication.RequireActor(context, validator);
                return TypedResults.Ok(await service.ValidateAsync(actor, request, InternalAuthentication.Correlation(context), cancellationToken).ConfigureAwait(false));
            })
            .WithName("ValidateSession")
            .WithSummary("Valid only when not revoked, within absolute and idle expiry, and bound to the same enabled identity. Never extends the absolute expiry.");

        group.MapPost("/sessions/revoke", static async Task<Ok<SessionStatus>> (SessionDigestRequest request, HttpContext context, SessionService service, DelegatedUserAssertionValidator validator, CancellationToken cancellationToken) =>
            {
                var actor = InternalAuthentication.RequireActor(context, validator);
                return TypedResults.Ok(await service.RevokeAsync(actor, request, InternalAuthentication.Correlation(context), cancellationToken).ConfigureAwait(false));
            })
            .WithName("RevokeSession")
            .WithSummary("Idempotent revocation by the session's own identity (sign-out).");

        return endpoints;
    }
}
