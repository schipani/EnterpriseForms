using System.Globalization;
using System.Reflection;
using System.Threading.RateLimiting;
using EnterpriseForms.PlatformConsole.Features.Session;
using Microsoft.AspNetCore.RateLimiting;

namespace EnterpriseForms.PlatformConsole.Composition;

/// <summary>Build-time OpenAPI generation starts the host inside the GetDocument.Insider tool; the startup validation skips only there.</summary>
public static class HostExecutionMode
{
    public static bool IsOpenApiDocumentGeneration { get; } =
        string.Equals(Assembly.GetEntryAssembly()?.GetName().Name, "GetDocument.Insider", StringComparison.Ordinal);
}

/// <summary>Rate limits: sign-in per client address, decisions per session.</summary>
public static class ConsoleRateLimits
{
    public const string LoginPolicy = "console-login";
    public const string DecisionPolicy = "console-decision";

    public static IServiceCollection AddConsoleRateLimits(this IServiceCollection services)
    {
        return services.AddRateLimiter(limiter =>
        {
            limiter.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            limiter.OnRejected = static (context, _) =>
                new ValueTask(ConsoleProblems.WriteAsync(context.HttpContext, new ConsoleException(ConsoleErrorCodes.RateLimited, 429, "too many requests; wait a minute and try again", retryable: true)));
            limiter.AddPolicy(LoginPolicy, static context => RateLimitPartition.GetFixedWindowLimiter(
                context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                static _ => new FixedWindowRateLimiterOptions { PermitLimit = 20, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
            // Partitioned by a digest of the session cookie (the raw session id is never used as a key).
            limiter.AddPolicy(DecisionPolicy, static context => RateLimitPartition.GetFixedWindowLimiter(
                (context.Request.Cookies[ConsoleSessionCookie.DevelopmentName] ?? context.Request.Cookies[ConsoleSessionCookie.ProductionName]) is { Length: > 0 } cookie
                    ? Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(cookie)))
                    : context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                static _ => new FixedWindowRateLimiterOptions { PermitLimit = 30, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
        });
    }
}

public static class ConsoleSecurityHeaders
{
    public static IApplicationBuilder UseConsoleSecurityHeaders(this IApplicationBuilder app)
    {
        return app.Use(static (context, next) =>
        {
            var headers = context.Response.Headers;
            headers["X-Content-Type-Options"] = "nosniff";
            headers["X-Frame-Options"] = "DENY";
            headers["Content-Security-Policy"] = "default-src 'none'; frame-ancestors 'none'; base-uri 'none'; form-action 'none'";
            headers["Referrer-Policy"] = "no-referrer";
            headers["Cache-Control"] = "no-store";
            headers["Cross-Origin-Opener-Policy"] = "same-origin";
            headers["Cross-Origin-Resource-Policy"] = "same-origin";
            return next(context);
        });
    }
}

/// <summary>
/// Container health probe for shell-less (chiseled) images: <c>dotnet EnterpriseForms.PlatformConsole.dll --probe http://127.0.0.1:8080/...</c>.
/// Loopback only; exit 0 on 2xx, 1 on failure, 2 on an invalid argument.
/// </summary>
public static class ProbeCommand
{
    public static bool TryRun(string[] args, out int exitCode)
    {
        ArgumentNullException.ThrowIfNull(args);
        exitCode = 0;
        var index = Array.IndexOf(args, "--probe");
        if (index < 0)
        {
            return false;
        }

        if (index + 1 >= args.Length || !Uri.TryCreate(args[index + 1], UriKind.Absolute, out var target) || !target.IsLoopback)
        {
            exitCode = 2;
            return true;
        }

        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
            using var response = client.GetAsync(target).GetAwaiter().GetResult();
            exitCode = response.IsSuccessStatusCode ? 0 : 1;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            exitCode = 1;
        }

        return true;
    }
}

/// <summary>Liveness (process up) and readiness (the Authority and Vault answer their health endpoints). No secret is used or shown.</summary>
public static class HealthEndpoints
{
    public sealed record HealthView(string Status, IReadOnlyDictionary<string, string> Dependencies);

    public static IEndpointRouteBuilder MapConsoleHealth(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup(ConsoleSessionCookie.ApiPath + "/health").WithTags("Health");
        group.MapGet("/live", static () => TypedResults.Ok(new HealthView("live", new Dictionary<string, string>()))).WithName("ConsoleLive");
        group.MapGet("/ready", static async (IHttpClientFactory factory, Microsoft.Extensions.Options.IOptions<PlatformConsoleOptions> options, CancellationToken cancellationToken) =>
            {
                var authority = await ProbeAsync(factory, options.Value.Authority.BaseAddress, "internal/v1/health/ready", cancellationToken).ConfigureAwait(false);
                var vault = await ProbeAsync(factory, options.Value.Vault.Address, "v1/sys/health", cancellationToken).ConfigureAwait(false);
                var dependencies = new Dictionary<string, string> { ["authority"] = authority, ["vault"] = vault };
                var ready = authority == "ready" && vault == "ready";
                return ready ? Results.Ok(new HealthView("ready", dependencies)) : Results.Json(new HealthView("not_ready", dependencies), statusCode: StatusCodes.Status503ServiceUnavailable);
            })
            .WithName("ConsoleReady");
        return endpoints;
    }

    private static async Task<string> ProbeAsync(IHttpClientFactory factory, string? baseAddress, string path, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(baseAddress, UriKind.Absolute, out var root))
        {
            return "unconfigured";
        }

        try
        {
            using var client = factory.CreateClient("health");
            client.Timeout = TimeSpan.FromSeconds(3);
            using var response = await client.GetAsync(new Uri(root, path), cancellationToken).ConfigureAwait(false);
            // Vault answers 200 only when initialized, unsealed and active (503 sealed, 501 uninitialized).
            return response.IsSuccessStatusCode ? "ready" : "http_" + ((int)response.StatusCode).ToString(CultureInfo.InvariantCulture);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return "unreachable";
        }
    }
}
