using System.Security.Claims;
using System.Security.Cryptography;
using EnterpriseForms.Application.Common;
using EnterpriseForms.Application.Common.Authority;
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
/// <para>
/// G23 (C-63): a cookie is not a session by itself. Each ticket carries a random 256-bit session secret (<c>ef:sid</c>);
/// the Authority holds the session state under the secret's SHA-256 digest and is asked on every cookie-authenticated
/// request whether the session is still valid (not revoked, within absolute and idle expiry, same enabled identity).
/// Sign-out revokes it there, so a copied cookie stops working on every API instance at once. When the state cannot be
/// read the request fails closed with 503 <c>SESSION_STATE_UNAVAILABLE</c> (the cookie is kept so the user can retry).
/// </para>
/// </summary>
public static partial class BusinessAuthentication
{
    public const string Scheme = JwtBearerDefaults.AuthenticationScheme;
    public const string SessionScheme = "Session";
    public const string Policy = "AuthenticatedUser";
    public const string CorrelationHeader = "X-Correlation-Id";
    public const string CsrfHeader = "X-CSRF-Token";
    public const string CsrfClaim = "ef:csrf";
    public const string AssertionClaim = "ef:assertion";
    public const string SessionSecretClaim = "ef:sid";
    public const string SessionStateUnavailableCode = "SESSION_STATE_UNAVAILABLE";
    public const string SessionCookieName = "__Host-ef-session";
    public const string DevelopmentSessionCookieName = "ef-session";
    public const string RawAssertionItem = "EnterpriseForms.RawAssertion";
    private const string SessionStateItem = "EnterpriseForms.SessionState";
    private const string SessionStateValid = "Valid";
    private const string SessionStateInvalid = "Invalid";
    private const string SessionStateUnavailable = "Unavailable";

    /// <summary>Upper bound of a session's absolute lifetime (the Authority enforces its own maximum as well).</summary>
    private static readonly TimeSpan MaximumSessionLifetime = TimeSpan.FromHours(12) - TimeSpan.FromMinutes(1);

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
                    // API client: never redirect to a login page; respond 401/403 with Problem Details. A session whose state
                    // could not be read answers 503 instead of 401, so the client does not mistake an outage for a sign-out.
                    OnRedirectToLogin = context => IsSessionStateUnavailable(context.HttpContext) ? WriteSessionStateUnavailableAsync(context.HttpContext) : SetStatus(context.Response, StatusCodes.Status401Unauthorized),
                    OnRedirectToAccessDenied = context => SetStatus(context.Response, StatusCodes.Status403Forbidden),
                    OnValidatePrincipal = ValidateSessionAsync,
                };
            });
        services.AddAuthorizationBuilder().AddPolicy(Policy, policy => policy.AddAuthenticationSchemes(Scheme, SessionScheme).RequireAuthenticatedUser());
        return services;
    }

    /// <summary>
    /// G23: the ticket is only a carrier. The session is valid when the ticket is intact and unexpired, carries a session
    /// secret and an assertion, and the Authority confirms the session state. Invalid: rejected and the cookie deleted.
    /// State unreadable: rejected for this request only (fail closed, 503 at the challenge), cookie kept.
    /// </summary>
    private static async Task ValidateSessionAsync(CookieValidatePrincipalContext context)
    {
        var http = context.HttpContext;
        var principal = context.Principal;
        var secret = principal?.FindFirstValue(SessionSecretClaim);
        var expires = context.Properties.ExpiresUtc;
        if (principal is null || string.IsNullOrEmpty(secret) || expires is null || expires <= DateTimeOffset.UtcNow || DelegatedUser(principal) is not { } user)
        {
            await RejectAsync(context).ConfigureAwait(false);
            return;
        }

        var logger = http.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(BusinessAuthentication));
        BrowserSessionState state;
        try
        {
            state = await http.RequestServices.GetRequiredService<IBrowserSessionStateAuthority>().ValidateSessionAsync(user, SessionDigest(secret), http.RequestAborted).ConfigureAwait(false);
        }
        catch (AuthorityClientException ex) when (ex.Retryable || ex.HttpStatusCode >= 500)
        {
            LogSessionStateUnavailable(logger, ex.ErrorCode);
            context.RejectPrincipal();
            http.Items[SessionStateItem] = SessionStateUnavailable;
            return;
        }
        catch (AuthorityClientException ex)
        {
            LogSessionRefused(logger, ex.ErrorCode);
            await RejectAsync(context).ConfigureAwait(false);
            return;
        }

        if (!state.Valid)
        {
            LogSessionInvalid(logger, state.InvalidReason ?? "unspecified");
            await RejectAsync(context).ConfigureAwait(false);
            return;
        }

        http.Items[SessionStateItem] = SessionStateValid;
    }

    // Reason codes only: never the session secret, its digest, the assertion or the CSRF token.
    [LoggerMessage(EventId = 2301, Level = LogLevel.Warning, Message = "Session state unavailable ({ErrorCode}); request refused")]
    private static partial void LogSessionStateUnavailable(ILogger logger, string errorCode);

    [LoggerMessage(EventId = 2302, Level = LogLevel.Information, Message = "Session refused by the Authority ({ErrorCode})")]
    private static partial void LogSessionRefused(ILogger logger, string errorCode);

    [LoggerMessage(EventId = 2303, Level = LogLevel.Information, Message = "Session no longer valid ({InvalidReason})")]
    private static partial void LogSessionInvalid(ILogger logger, string invalidReason);

    private static async Task RejectAsync(CookieValidatePrincipalContext context)
    {
        context.RejectPrincipal();
        context.HttpContext.Items[SessionStateItem] = SessionStateInvalid;
        await context.HttpContext.SignOutAsync(SessionScheme).ConfigureAwait(false);
    }

    /// <summary>True when this request presented a session cookie whose state the Authority could not report.</summary>
    public static bool IsSessionStateUnavailable(HttpContext context) =>
        context is not null && context.Items.TryGetValue(SessionStateItem, out var value) && Equals(value, SessionStateUnavailable);

    private static Task SetStatus(HttpResponse response, int status)
    {
        response.StatusCode = status;
        return Task.CompletedTask;
    }

    private static Task WriteSessionStateUnavailableAsync(HttpContext context)
    {
        context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
        context.Response.Headers.RetryAfter = "5";
        return context.Response.WriteAsJsonAsync(new { type = "https://enterpriseforms.invalid/problems/session-state-unavailable", title = SessionStateUnavailableCode, status = 503, detail = "The session could not be verified. Try again shortly." });
    }

    /// <summary>The Authority identity of a session ticket (its own assertion; the Authority re-validates it).</summary>
    private static DelegatedUserContext? DelegatedUser(ClaimsPrincipal principal)
    {
        var assertion = principal.FindFirstValue(AssertionClaim);
        var issuer = principal.FindFirstValue("iss");
        var subject = principal.FindFirstValue("sub");
        return string.IsNullOrEmpty(assertion) || string.IsNullOrEmpty(issuer) || string.IsNullOrEmpty(subject) || !Guid.TryParse(principal.FindFirstValue("org"), out var organizationId)
            ? null
            : new DelegatedUserContext(issuer, subject, organizationId, assertion);
    }

    /// <summary>SHA-256 of the session secret, base64: the only form in which the secret leaves this process.</summary>
    private static string SessionDigest(string secret) => Convert.ToBase64String(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(secret)));

    /// <summary>
    /// Reads the presented session ticket without running validation (the ticket is still authenticated by the Data
    /// Protection key ring). Used only to find the session a new sign-in replaces; never to authenticate a request.
    /// </summary>
    private static ClaimsPrincipal? ReadPresentedTicket(HttpContext context)
    {
        var options = context.RequestServices.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>().Get(SessionScheme);
        var value = options.CookieManager.GetRequestCookie(context, options.Cookie.Name!);
        return string.IsNullOrEmpty(value) ? null : options.TicketDataFormat.Unprotect(value)?.Principal;
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

    /// <summary>
    /// Creates the browser session: the principal carries the assertion claims plus the raw assertion, a per-session CSRF
    /// token and a fresh random session secret. G23: the session is registered with the Authority first (no cookie is
    /// issued if that fails), in the same Authority transaction that revokes the session this browser presented, if any,
    /// so neither a planted (fixation) nor a previous session survives a sign-in.
    /// </summary>
    public static async Task<(string CsrfToken, DateTimeOffset ExpiresOn)> StartSessionAsync(HttpContext context, string assertion, IReadOnlyDictionary<string, string> claims, DateTimeOffset expiresOn, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(claims);
        var latest = DateTimeOffset.UtcNow + MaximumSessionLifetime;
        var absolute = expiresOn < latest ? expiresOn : latest;
        var csrf = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var secret = Base64Url(RandomNumberGenerator.GetBytes(32));
        var identity = new ClaimsIdentity(SessionScheme);
        foreach (var (type, value) in claims)
        {
            identity.AddClaim(new Claim(type, value));
        }

        identity.AddClaim(new Claim(AssertionClaim, assertion));
        identity.AddClaim(new Claim(CsrfClaim, csrf));
        identity.AddClaim(new Claim(SessionSecretClaim, secret));
        var principal = new ClaimsPrincipal(identity);
        var user = DelegatedUser(principal) ?? throw new BusinessException("UNAUTHORIZED", 401, "assertion lacks iss/sub/org");
        var replaced = ReadPresentedTicket(context)?.FindFirstValue(SessionSecretClaim) is { Length: > 0 } previous ? SessionDigest(previous) : null;
        var state = await context.RequestServices.GetRequiredService<IBrowserSessionStateAuthority>().CreateSessionAsync(user, SessionDigest(secret), absolute, replaced, cancellationToken).ConfigureAwait(false);
        if (!state.Valid)
        {
            throw new BusinessException(SessionStateUnavailableCode, 503, "the session could not be registered");
        }

        await context.SignInAsync(SessionScheme, principal, new AuthenticationProperties { IsPersistent = false, ExpiresUtc = absolute, AllowRefresh = false }).ConfigureAwait(false);
        return (csrf, absolute);
    }

    /// <summary>
    /// G23 sign-out. A valid session (CSRF already enforced by <see cref="UseSessionCsrfProtection"/>) is revoked at the
    /// Authority before the cookie is deleted. If revocation cannot be confirmed the cookie is kept and false returned
    /// (503) so the sign-out can be retried. An invalid or absent session only has its cookie deleted.
    /// </summary>
    public static async Task<bool> EndSessionAsync(HttpContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var result = await context.AuthenticateAsync(SessionScheme).ConfigureAwait(false);
        if (IsSessionStateUnavailable(context))
        {
            return false;
        }

        if (result is { Succeeded: true, Principal: { } principal } && principal.FindFirstValue(SessionSecretClaim) is { Length: > 0 } secret && DelegatedUser(principal) is { } user)
        {
            try
            {
                await context.RequestServices.GetRequiredService<IBrowserSessionStateAuthority>().RevokeSessionAsync(user, SessionDigest(secret), cancellationToken).ConfigureAwait(false);
            }
            catch (AuthorityClientException ex) when (ex.Retryable || ex.HttpStatusCode >= 500)
            {
                return false;
            }
        }

        await context.SignOutAsync(SessionScheme).ConfigureAwait(false);
        return true;
    }

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
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
