using AuthorizationAuthority.Api.Composition;
using AuthorizationAuthority.Infrastructure.SpiceDb;
using Microsoft.AspNetCore.Http.HttpResults;

namespace AuthorizationAuthority.Api.Features.Health;

/// <summary>
/// C-149 (P-2), Testing environment only: reads and resets the measurement-only SpiceDB call counts, and attributes the calls made
/// while serving a request to its route (method and pattern). Workload-authenticated; counts and RPC method names only.
/// </summary>
public static class SpiceDbCallCountingEndpoints
{
    public static IApplicationBuilder UseSpiceDbCallAttribution(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        var counter = app.ApplicationServices.GetRequiredService<SpiceDbCallCounter>();
        if (!counter.Enabled)
        {
            return app;
        }

        return app.Use(async (context, next) =>
        {
            var pattern = (context.GetEndpoint() as RouteEndpoint)?.RoutePattern.RawText;
            if (pattern is null || pattern.Contains("/testing/spicedb-calls", StringComparison.Ordinal))
            {
                await next(context).ConfigureAwait(false);
                return;
            }

            using (counter.Attribute(context.Request.Method + " " + pattern))
            {
                await next(context).ConfigureAwait(false);
            }
        });
    }

    public static IEndpointRouteBuilder MapSpiceDbCallCountingEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup(InternalApi.Prefix + "/testing/spicedb-calls").WithTags("Diagnostics").RequireAuthorization(InternalAuthentication.WorkloadPolicy);
        group.MapGet("/", static Ok<SpiceDbCallSnapshot> (SpiceDbCallCounter counter) => TypedResults.Ok(counter.Snapshot()));
        group.MapPost("/reset", static Ok<SpiceDbCallSnapshot> (SpiceDbCallCounter counter) =>
        {
            counter.Reset();
            return TypedResults.Ok(counter.Snapshot());
        });
        return endpoints;
    }
}
