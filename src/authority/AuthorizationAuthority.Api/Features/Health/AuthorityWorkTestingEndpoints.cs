using AuthorizationAuthority.Api.Composition;
using AuthorizationAuthority.Application.Common;
using Microsoft.AspNetCore.Http.HttpResults;

namespace AuthorizationAuthority.Api.Features.Health;

/// <summary>
/// C-150 R56-B, Testing environment only: the work loop's wake-up counters (signals by source, wake-ups against polls, whether the
/// notification listener is connected). Workload-authenticated; counts only.
/// </summary>
public static class AuthorityWorkTestingEndpoints
{
    public sealed record AuthorityWorkCounters(bool CommitWakeupEnabled, bool ListenerConnected, long CommitSignals, long NotificationSignals, long ReconnectSignals, long Wakeups, long Polls);

    public static IEndpointRouteBuilder MapAuthorityWorkTestingEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet(InternalApi.Prefix + "/testing/authority-work", static Ok<AuthorityWorkCounters> (AuthorityWorkSignal signal, AuthorityWorkOptions options) =>
                TypedResults.Ok(new AuthorityWorkCounters(options.CommitWakeupEnabled, signal.ListenerConnected, signal.CommitSignals, signal.NotificationSignals, signal.ReconnectSignals, signal.Wakeups, signal.Polls)))
            .WithTags("Diagnostics").RequireAuthorization(InternalAuthentication.WorkloadPolicy);
        return endpoints;
    }
}
