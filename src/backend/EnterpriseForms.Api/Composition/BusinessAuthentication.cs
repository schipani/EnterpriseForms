using System.Security.Claims;
using EnterpriseForms.Application.Common;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace EnterpriseForms.Api.Composition;

/// <summary>
/// End-user authentication (PB-08 BFF, B-01). Two schemes accept the same TestIdentityProvider assertion format:
/// <list type="bullet">
/// <item>Bearer (service tests, Stage A harness): HS256 assertion in the Authorization header.</item>
/// <item>Session cookie (browser): the assertion lives inside an encrypted, HttpOnly auth ticket issued by the
/// development login (or, later, the Entra OIDC callback); no token ever reaches browser JavaScript. Mutations must
/// carry the <c>X-CSRF-Token</c> header matching the ticket's CSRF claim (double-submit bound to the session).</item>
/// </list>
/// The startup guard refuses the test provider outside Development/Testing; the raw assertion is forwarded to the
/// authority, which validates it independently — the business API never re-asserts identity claims.
/// </summary>
public static class BusinessAuthentication
{
    public const string Scheme = JwtBearerDefaults.AuthenticationScheme;
    public const string SessionScheme = "Session";
    public const string Policy = "AuthenticatedUser";
    public const string CorrelationHeader = "X-Correlation-Id";
    public const string CsrfHeader = "X-CSRF-Token";
    public const string CsrfClaim = "ef:csrf";
    public const string AssertionClaim = "ef:assertion";
    public const string SessionCookieName = "__Host-ef-session";
    public const string DevelopmentSessionCookieName = "ef-session";
    public const string RawAssertionItem = "EnterpriseForms.RawAssertion";

    public static IServiceCollection AddBusinessAuthentication(this IServiceCollection services, IConfiguration configuration, IHostEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(environment);
        services.Configure<UserAuthenticationOptions>(configuration.GetSection(UserAuthenticationOptions.SectionName));
        services.Configure<DevelopmentIdentityOptions>(configuration.GetSection(DevelopmentIdentityOptions.SectionName));
        services.AddHostedService<UserAuthenticationStartupGuard>();
        services.AddSingleton<DevelopmentIdentityProvider>();
        var settings = configuration.GetSection(UserAuthenticationOptions.SectionName).Get<UserAuthenticationOptions>() ?? new UserAuthenticationOptions();
        var isDevelopment = environment.IsDevelopment() || environment.IsEnvironment("Testing");
        services.AddAuthentication(Scheme)
            .AddJwtBearer(Scheme, options =>
            {
                options.MapInboundClaims = false;
                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidIssuer = settings.Issuer ?? "unconfigured",
                    ValidAudience = settings.Audience ?? "unconfigured",
                    IssuerSigningKey = settings.SymmetricKey(),
                    ValidAlgorithms = [SecurityAlgorithms.HmacSha256],
                    ValidateIssuerSigningKey = true,
                    ValidateLifetime = true,
                    ClockSkew = TimeSpan.FromSeconds(30),
                    RequireSignedTokens = true,
                    RequireExpirationTime = true,
                };
                options.Events = new JwtBearerEvents
                {
                    OnMessageReceived = context =>
                    {
                        // Keep the raw assertion for forwarding; it is never persisted or logged.
                        if (context.Request.Headers.Authorization.ToString() is { Length: > 7 } header && header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
                        {
                            context.HttpContext.Items[RawAssertionItem] = header[7..].Trim();
                        }

                        return Task.CompletedTask;
                    },
                };
            })
            .AddCookie(SessionScheme, options =>
            {
                options.Cookie.Name = isDevelopment ? DevelopmentSessionCookieName : SessionCookieName;
                options.Cookie.HttpOnly = true;
                options.Cookie.SameSite = SameSiteMode.Lax;
                options.Cookie.SecurePolicy = isDevelopment ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always;
                options.Cookie.IsEssential = true;
                options.Cookie.Path = "/";
                options.SlidingExpiration = false;
                options.ExpireTimeSpan = TimeSpan.FromHours(8);
                options.Events = new CookieAuthenticationEvents
                {
                    // API client: never redirect to a login page; respond 401/403 with Problem Details.
                    OnRedirectToLogin = context => { context.Response.StatusCode = StatusCodes.Status401Unauthorized; return Task.CompletedTask; },
                    OnRedirectToAccessDenied = context => { context.Response.StatusCode = StatusCodes.Status403Forbidden; return Task.CompletedTask; },
                    OnValidatePrincipal = context =>
                    {
                        // The embedded assertion must still be valid; an expired assertion ends the session (session-expired UX).
                        var assertion = context.Principal?.FindFirstValue(AssertionClaim);
                        var expires = context.Properties.ExpiresUtc;
                        if (string.IsNullOrEmpty(assertion) || expires is null || expires <= DateTimeOffset.UtcNow)
                        {
                            context.RejectPrincipal();
                        }

                        return Task.CompletedTask;
                    },
                };
            });
        services.AddAuthorizationBuilder().AddPolicy(Policy, policy => policy.AddAuthenticationSchemes(Scheme, SessionScheme).RequireAuthenticatedUser());
        return services;
    }

    /// <summary>
    /// Double-submit CSRF for cookie sessions: every non-safe method that presents a valid session cookie must carry the
    /// header matching that session's ticket claim — whether or not a bearer assertion is also present, so adding a bearer
    /// can never switch the check off.
    /// <para>
    /// G22 (C-58): the default authentication scheme is the bearer, so <c>UseAuthentication</c> does not authenticate the
    /// session cookie; the authorization policy does that later, at the endpoint. The check therefore authenticates the
    /// session scheme itself (the handler caches the result for the rest of the request) instead of reading
    /// <c>HttpContext.User</c>, which before this fix was always the anonymous principal for cookie requests.
    /// </para>
    /// </summary>
    public static IApplicationBuilder UseSessionCsrfProtection(this IApplicationBuilder app)
    {
        return app.Use(static async (context, next) =>
        {
            var method = context.Request.Method;
            var isSafe = HttpMethods.IsGet(method) || HttpMethods.IsHead(method) || HttpMethods.IsOptions(method);
            var session = isSafe ? null : await context.AuthenticateAsync(SessionScheme).ConfigureAwait(false);
            if (session is { Succeeded: true, Principal: { } principal })
            {
                var expected = principal.FindFirstValue(CsrfClaim);
                var provided = context.Request.Headers[CsrfHeader].ToString();
                if (string.IsNullOrEmpty(expected) || !System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(System.Text.Encoding.UTF8.GetBytes(expected), System.Text.Encoding.UTF8.GetBytes(provided)))
                {
                    context.Response.StatusCode = StatusCodes.Status403Forbidden;
                    await context.Response.WriteAsJsonAsync(new { type = "https://enterpriseforms.invalid/problems/csrf-token-invalid", title = "CSRF_TOKEN_INVALID", status = 403, detail = "X-CSRF-Token header missing or stale" }).ConfigureAwait(false);
                    return;
                }
            }

            await next(context).ConfigureAwait(false);
        });
    }

    /// <summary>Builds the request context from the validated principal. organization_id is an assertion that the authority checks on every call.</summary>
    public static RequestContext RequireContext(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var user = context.User;
        var issuer = user.FindFirstValue("iss");
        var subject = user.FindFirstValue("sub");
        var organization = user.FindFirstValue("org");
        var authorityUser = user.FindFirstValue("authority_user_id");
        var raw = context.Items[RawAssertionItem] as string ?? user.FindFirstValue(AssertionClaim);
        if (string.IsNullOrWhiteSpace(issuer) || string.IsNullOrWhiteSpace(subject) || !Guid.TryParse(organization, out var organizationId) || !Guid.TryParse(authorityUser, out var localUserId) || string.IsNullOrWhiteSpace(raw))
        {
            throw new BusinessException("UNAUTHORIZED", 401, "assertion lacks iss/sub/org/authority_user_id");
        }

        var correlation = Guid.TryParse(context.Request.Headers[CorrelationHeader].ToString(), out var id) ? id : Guid.CreateVersion7();
        return new RequestContext(organizationId, localUserId, issuer, subject, user.FindFirstValue("name") ?? subject, raw, correlation);
    }

    /// <summary>Creates the browser session: the principal carries the assertion claims plus the raw assertion and a per-session CSRF token.</summary>
    public static async Task<string> StartSessionAsync(HttpContext context, string assertion, IReadOnlyDictionary<string, string> claims, DateTimeOffset expiresOn)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(claims);
        var csrf = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
        var identity = new ClaimsIdentity(SessionScheme);
        foreach (var (type, value) in claims)
        {
            identity.AddClaim(new Claim(type, value));
        }

        identity.AddClaim(new Claim(AssertionClaim, assertion));
        identity.AddClaim(new Claim(CsrfClaim, csrf));
        await context.SignInAsync(SessionScheme, new ClaimsPrincipal(identity), new AuthenticationProperties { IsPersistent = false, ExpiresUtc = expiresOn, AllowRefresh = false }).ConfigureAwait(false);
        return csrf;
    }
}

public sealed class UserAuthenticationOptions
{
    public const string SectionName = "UserAuthentication";

    /// <summary>TestIdentityProvider (Development/Testing only) or EntraId (not operable in Stage B: fails closed at startup).</summary>
    public string ProviderKind { get; set; } = "Unconfigured";

    public string? Issuer { get; set; }

    public string? Audience { get; set; }

    public string? SharedKeyBase64 { get; set; }

    public SymmetricSecurityKey SymmetricKey()
    {
        var bytes = string.IsNullOrWhiteSpace(SharedKeyBase64) ? new byte[32] : Convert.FromBase64String(SharedKeyBase64);
        return new SymmetricSecurityKey(bytes.Length >= 32 ? bytes : new byte[32]);
    }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(Issuer) && !string.IsNullOrWhiteSpace(Audience) && !string.IsNullOrWhiteSpace(SharedKeyBase64) && Convert.FromBase64String(SharedKeyBase64).Length >= 32;
}

/// <summary>Fail closed: the TestIdentityProvider is permitted only in Development/Testing; Entra BFF (PB-08) is required before Staging/Production.</summary>
public sealed class UserAuthenticationStartupGuard(IOptions<UserAuthenticationOptions> options, IHostEnvironment environment) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (HostExecutionMode.IsOpenApiDocumentGeneration)
        {
            return Task.CompletedTask;
        }

        var settings = options.Value;
        var testAllowed = environment.IsDevelopment() || environment.IsEnvironment("Testing");
        if (settings.ProviderKind == "TestIdentityProvider" && testAllowed && settings.IsConfigured)
        {
            return Task.CompletedTask;
        }

        if (settings.ProviderKind == "EntraId")
        {
            throw new InvalidOperationException("UserAuthentication:ProviderKind 'EntraId' is registered but not operable in Stage B: the Entra OIDC BFF handler and the authority's Entra assertion validator are PB-08 deliverables (A-F7). Refusing to start rather than running without verified identity.");
        }

        throw new InvalidOperationException($"UserAuthentication:ProviderKind '{settings.ProviderKind}' is not permitted in {environment.EnvironmentName} or is not configured. Stage A supports TestIdentityProvider in Development/Testing only; Entra ID BFF (PB-08) is required otherwise. Refusing to start.");
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
