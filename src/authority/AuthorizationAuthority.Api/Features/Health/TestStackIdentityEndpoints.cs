using AuthorizationAuthority.Api.Composition;
using Microsoft.AspNetCore.Http.HttpResults;

namespace AuthorizationAuthority.Api.Features.Health;

/// <summary>
/// C7 M4 incident correction (docs/PHASE_C7_STATUS.md §31): lets the business API prove to browser-test tooling that it is
/// connected to an isolated test Authority. Mapped ONLY in the Testing environment, and answers only when the deployment
/// configured <c>Testing:StackIdentity</c> (the isolated security-test stack sets it; the persistent development stack runs
/// Development and never maps the route). Business workload credential required; returns no organization data.
/// </summary>
public static class TestStackIdentityEndpoints
{
    public const string ConfigurationKey = "Testing:StackIdentity";

    public static IEndpointRouteBuilder MapTestStackIdentityEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet(InternalApi.Prefix + "/testing/stack-identity", static Results<Ok<AuthorityStackIdentityResponse>, ProblemHttpResult> (IConfiguration configuration, IHostEnvironment environment) =>
                configuration[ConfigurationKey] is { Length: > 0 and <= 100 } identity
                    ? TypedResults.Ok(new AuthorityStackIdentityResponse(environment.EnvironmentName, identity))
                    : TypedResults.Problem(title: "Stack not identified", detail: "Testing:StackIdentity is not configured for this Authority", statusCode: StatusCodes.Status503ServiceUnavailable))
            .WithTags("Health")
            .RequireAuthorization(InternalAuthentication.WorkloadPolicy)
            .WithName("GetAuthorityTestStackIdentity")
            .WithSummary("Testing environment only: the configured identity of this isolated test stack (browser-test isolation guard).");
        return endpoints;
    }

    /// <summary>A stack identity outside Testing would let an ordinary deployment impersonate a disposable test stack: refuse to start.</summary>
    public static void RefuseOutsideTesting(IConfiguration configuration, IHostEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(environment);
        if (!string.IsNullOrEmpty(configuration[ConfigurationKey]) && !environment.IsEnvironment("Testing"))
        {
            throw new InvalidOperationException("Testing:StackIdentity is permitted only in the Testing environment (isolated test stacks).");
        }
    }
}

public sealed record AuthorityStackIdentityResponse(string EnvironmentName, string StackIdentity);
