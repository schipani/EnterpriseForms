using System.Security.Cryptography;
using System.Text;
using EnterpriseForms.PlatformConsole.Composition;
using Microsoft.Extensions.Options;

namespace EnterpriseForms.PlatformConsole.Features.Session;

/// <summary>The console's session cookie: never shared with the business application (own name, own path, own origin).</summary>
public static class ConsoleSessionCookie
{
    /// <summary>Production name (<c>__Host-</c>: Secure, Path=/, no Domain). Not reachable until PB-08.</summary>
    public const string ProductionName = "__Host-ef-platform-console";

    /// <summary>
    /// Development/Testing name. Still Secure, HttpOnly and SameSite=Strict (browsers accept Secure cookies from http://127.0.0.1 and
    /// http://localhost), but scoped to <see cref="ApiPath"/> because cookies ignore ports: the business app on another loopback port
    /// must not receive it.
    /// </summary>
    public const string DevelopmentName = "ef-platform-console";

    public const string ApiPath = "/console-api";

    public static string Name(IHostEnvironment environment) => environment.IsProduction() ? ProductionName : DevelopmentName;

    public static CookieOptions Options(IHostEnvironment environment) => new()
    {
        HttpOnly = true,
        Secure = true,
        SameSite = SameSiteMode.Strict,
        Path = environment.IsProduction() ? "/" : ApiPath,
        IsEssential = true,
        // No Expires/Max-Age: a browser-session cookie. Idle and absolute expiry are enforced server-side.
    };

    public static string? Read(HttpContext context, IHostEnvironment environment) => context.Request.Cookies[Name(environment)];

    public static void Write(HttpContext context, IHostEnvironment environment, string sessionId) => context.Response.Cookies.Append(Name(environment), sessionId, Options(environment));

    /// <summary>
    /// Deletes the cookie in the response being produced, including an error response: the deletion is applied when the response
    /// starts, after the exception handler has reset the headers of a refused request.
    /// </summary>
    public static void Delete(HttpContext context, IHostEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(context);
        var path = Options(environment).Path;
        var name = Name(environment);
        context.Response.OnStarting(() =>
        {
            context.Response.Cookies.Delete(name, new CookieOptions { HttpOnly = true, Secure = true, SameSite = SameSiteMode.Strict, Path = path });
            return Task.CompletedTask;
        });
    }
}

/// <summary>
/// Origin policy for every request: a request that names an origin (browsers send <c>Origin</c> on cross-origin and on state-changing
/// requests) must name an allowed one, and a browser request marked <c>Sec-Fetch-Site: cross-site</c> is refused. State-changing
/// methods must carry an allowed <c>Origin</c>. There is no CORS policy, so no other origin can read a response.
/// </summary>
public static class ConsoleOriginPolicy
{
    public static IApplicationBuilder UseConsoleOriginPolicy(this IApplicationBuilder app)
    {
        return app.Use(static async (context, next) =>
        {
            var allowed = context.RequestServices.GetRequiredService<IOptions<PlatformConsoleOptions>>().Value.AllowedOrigins;
            var origin = context.Request.Headers.Origin.ToString();
            var unsafeMethod = !(HttpMethods.IsGet(context.Request.Method) || HttpMethods.IsHead(context.Request.Method));
            var originAllowed = origin.Length > 0 && allowed.Contains(origin, StringComparer.Ordinal);
            var crossSite = string.Equals(context.Request.Headers["Sec-Fetch-Site"].ToString(), "cross-site", StringComparison.OrdinalIgnoreCase);
            if ((origin.Length > 0 && !originAllowed) || crossSite || (unsafeMethod && !originAllowed))
            {
                await ConsoleProblems.WriteAsync(context, new ConsoleException(ConsoleErrorCodes.OriginNotAllowed, 403, "requests must come from the Platform Console's own origin")).ConfigureAwait(false);
                return;
            }

            await next(context).ConfigureAwait(false);
        });
    }
}

/// <summary>
/// Endpoint filter for every operator endpoint: a live server-side session is required (401 <c>SESSION_REQUIRED</c> otherwise, and the
/// stale cookie is deleted); state-changing requests must also carry <c>X-CSRF-Token</c> equal to the session's token (constant-time).
/// The session is placed in <see cref="HttpContext.Items"/> for the handler; handlers never read an operator from the request.
/// </summary>
public sealed class RequireOperatorSession(OperatorSessionStore store, IHostEnvironment environment) : IEndpointFilter
{
    public const string CsrfHeader = "X-CSRF-Token";
    private const string SessionItem = "EnterpriseForms.PlatformConsole.Session";

    public static OperatorSession Current(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return context.Items[SessionItem] as OperatorSession ?? throw new ConsoleException(ConsoleErrorCodes.SessionRequired, 401, "sign in to the Platform Console");
    }

    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);
        var http = context.HttpContext;
        var session = store.Touch(ConsoleSessionCookie.Read(http, environment));
        if (session is null)
        {
            ConsoleSessionCookie.Delete(http, environment);
            throw new ConsoleException(ConsoleErrorCodes.SessionRequired, 401, "sign in to the Platform Console (no session, or it expired)");
        }

        var method = http.Request.Method;
        if (!(HttpMethods.IsGet(method) || HttpMethods.IsHead(method)))
        {
            var provided = http.Request.Headers[CsrfHeader].ToString();
            if (!CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(session.CsrfToken), Encoding.UTF8.GetBytes(provided)))
            {
                throw new ConsoleException(ConsoleErrorCodes.CsrfTokenInvalid, 403, "X-CSRF-Token header missing or stale");
            }
        }

        http.Items[SessionItem] = session;
        return await next(context).ConfigureAwait(false);
    }
}
