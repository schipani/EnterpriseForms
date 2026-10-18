using AuthorizationAuthority.Api.Composition;
using Microsoft.AspNetCore.Http.HttpResults;

namespace AuthorizationAuthority.Api.Features.Health;

public static class HealthEndpoints
{
    public static IEndpointRouteBuilder MapHealthEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup(InternalApi.Prefix + "/health").WithTags("Health");

        group.MapGet("/live", static () => TypedResults.Ok(new AuthorityProbeResponse("Live", "AuthorizationAuthority.Api", null)))
            .WithName("GetAuthorityLiveness");

        group.MapGet("/ready", static async Task<Results<Ok<AuthorityProbeResponse>, ProblemHttpResult>> (AuthorityReadiness readiness, CancellationToken cancellationToken) =>
            {
                var (ready, detail) = await readiness.CheckAsync(cancellationToken).ConfigureAwait(false);
                return ready
                    ? TypedResults.Ok(new AuthorityProbeResponse("Ready", "AuthorizationAuthority.Api", detail))
                    : TypedResults.Problem(title: "Not ready", detail: detail, statusCode: StatusCodes.Status503ServiceUnavailable);
            })
            .WithName("GetAuthorityReadiness")
            .WithSummary("Ready only when the signing provider, authority store, SpiceDB schema and independent archive are all usable.");

        return endpoints;
    }
}

public sealed record AuthorityProbeResponse(string ProbeStatus, string ServiceName, string? Detail);
