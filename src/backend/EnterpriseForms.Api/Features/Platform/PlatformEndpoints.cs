using EnterpriseForms.Api.Composition;
using Microsoft.AspNetCore.Http.HttpResults;

namespace EnterpriseForms.Api.Features.Platform;

/// <summary>
/// Container probes. Liveness excludes external dependencies; readiness requires the Authorization Authority
/// (CLAUDE-DESIGN.MD section 10). "Platform" is the composition-root owner, not a business feature.
/// </summary>
public static class PlatformEndpoints
{
    public const string OwnerName = "Platform";

    public static IEndpointRouteBuilder MapPlatformEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/health")
            .WithTags(OwnerName)
            .WithFeatureOwner(OwnerName);

        group.MapGet("/live", static () => TypedResults.Ok(new ProbeResponse("Live", "EnterpriseForms.Api")))
            .WithName("GetLiveness")
            .WithSummary("Process liveness. Never consults external dependencies.");

        group.MapGet("/ready", static async Task<Results<Ok<ProbeResponse>, ProblemHttpResult>> (TrustAuthorityReadinessProbe probe, CancellationToken cancellationToken) =>
            {
                var result = await probe.CheckAsync(cancellationToken).ConfigureAwait(false);
                return result.Ready
                    ? TypedResults.Ok(new ProbeResponse("Ready", "EnterpriseForms.Api"))
                    : TypedResults.Problem(
                        title: "Not ready",
                        detail: $"{result.Dependency}: {result.Detail}",
                        statusCode: StatusCodes.Status503ServiceUnavailable,
                        type: "https://enterpriseforms.invalid/problems/dependency-unavailable");
            })
            .WithName("GetReadiness")
            .WithSummary("Readiness. Fails closed when the Authorization Authority is unconfigured or unreachable.");

        return endpoints;
    }
}

public sealed record ProbeResponse(string ProbeStatus, string ServiceName);
