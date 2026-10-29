using EnterpriseForms.Api.Composition;
using EnterpriseForms.Application.Common.Authority;
using Microsoft.AspNetCore.Http.HttpResults;

namespace EnterpriseForms.Api.Features.Platform;

/// <summary>
/// C7 M4 incident correction (docs/PHASE_C7_STATUS.md §31): proof for browser-test tooling that this API and the Authority
/// it is connected to belong to an isolated test stack. Mapped ONLY in the Testing environment. Answers 200 only when this
/// API has a configured <c>Testing:StackIdentity</c> and the Authority, reached server-side with the workload credential,
/// reports the same identity from its own Testing configuration; otherwise 503. The persistent development stack runs
/// Development, so the route does not exist there (404). Read-only; no organization or user data.
/// </summary>
public static class TestStackIdentityEndpoints
{
    public const string ConfigurationKey = "Testing:StackIdentity";

    public static IEndpointRouteBuilder MapTestStackIdentityEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet(ApiVersions.V1Prefix + "/testing/stack-identity", static async Task<Results<Ok<TestStackIdentityResponse>, ProblemHttpResult>> (IConfiguration configuration, IHostEnvironment environment, ITestStackIdentityAuthority authority, HttpContext http, CancellationToken cancellationToken) =>
            {
                http.Response.Headers.CacheControl = "no-store";
                if (configuration[ConfigurationKey] is not { Length: > 0 and <= 100 } identity)
                {
                    return TypedResults.Problem(title: "Stack not identified", detail: "Testing:StackIdentity is not configured for this API", statusCode: StatusCodes.Status503ServiceUnavailable);
                }

                AuthorityStackIdentity authorityIdentity;
                try
                {
                    authorityIdentity = await authority.GetTestStackIdentityAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (AuthorityClientException ex)
                {
                    return TypedResults.Problem(title: "Authority not identified", detail: ex.ErrorCode, statusCode: StatusCodes.Status503ServiceUnavailable);
                }

                return TypedResults.Ok(new TestStackIdentityResponse(environment.EnvironmentName, identity, authorityIdentity.EnvironmentName, authorityIdentity.StackIdentity,
                    environment.IsEnvironment("Testing") && authorityIdentity.EnvironmentName == "Testing" && string.Equals(identity, authorityIdentity.StackIdentity, StringComparison.Ordinal)));
            })
            .WithTags(PlatformEndpoints.OwnerName)
            .WithFeatureOwner(PlatformEndpoints.OwnerName)
            .WithName("GetTestStackIdentity")
            .WithSummary("Testing environment only: identities of this API and its Authority for the browser-test isolation guard.");
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

public sealed record TestStackIdentityResponse(string ApiEnvironment, string ApiStackIdentity, string AuthorityEnvironment, string AuthorityStackIdentity, bool Isolated);
