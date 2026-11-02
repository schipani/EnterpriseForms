using AuthorizationAuthority.Contracts.V1;
using EnterpriseForms.PlatformConsole.Composition;
using EnterpriseForms.PlatformConsole.Infrastructure;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Options;

namespace EnterpriseForms.PlatformConsole.Features.Session;

public sealed record DevelopmentOperatorOption(string SubjectIdentifier, string DisplayName, string IssuerUri);

public sealed record DevelopmentOperatorList(IReadOnlyList<DevelopmentOperatorOption> Operators, string AuthenticationMode, string EnvironmentNotice);

public sealed record DevelopmentLoginRequest(string SubjectIdentifier);

/// <summary>The signed-in operator as the console shows it. Identity and permissions come from the Authority registry at this moment.</summary>
public sealed record ConsoleSessionView(
    Guid PlatformOperatorId,
    string DisplayName,
    string IssuerUri,
    string SubjectIdentifier,
    string OperatorStatus,
    IReadOnlyList<string> Permissions,
    int ActiveOperatorCount,
    string AuthenticationMode,
    string EvidenceAssurance,
    string EnvironmentNotice,
    string CsrfToken,
    DateTimeOffset IdleExpiresOn,
    DateTimeOffset AbsoluteExpiresOn);

/// <summary>
/// Development authentication (Development/Testing only; the host refuses other environments). The browser names ONE configured
/// synthetic operator; the console asks the Authority whether that identity is an active registered operator whose registered
/// signing key is the one configured for it, and only then creates a session bound to that identity. No password, no organization
/// membership, no business session; the business application's cookie is never read or issued here.
/// </summary>
public static class SessionEndpoints
{
    public const string AuthenticationMode = "SyntheticDevelopment (development operator selection; not production authentication)";
    public const string EnvironmentNotice = "Development operators: approvals made here are synthetic and are not two-person evidence.";

    public static IEndpointRouteBuilder MapSessionEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup(ConsoleSessionCookie.ApiPath + "/v1/session").WithTags("Session");

        group.MapGet("/development-operators", static Ok<DevelopmentOperatorList> (IOptions<PlatformConsoleOptions> options) =>
                TypedResults.Ok(new DevelopmentOperatorList(
                    options.Value.Operators.Select(o => new DevelopmentOperatorOption(o.SubjectIdentifier!, o.DisplayName!, options.Value.Authority.OperatorAssertion.Issuer!)).ToList(),
                    AuthenticationMode,
                    EnvironmentNotice)))
            .WithName("ListDevelopmentOperators")
            .WithSummary("The synthetic Platform Operators this development console can sign in (Development/Testing only).");

        group.MapPost("/development-login", static async Task<Ok<ConsoleSessionView>> (DevelopmentLoginRequest request, HttpContext http, IOptions<PlatformConsoleOptions> options, OperatorSessionStore store, AuthorityPlatformClient authority, IHostEnvironment environment, ILoggerFactory loggers, CancellationToken cancellationToken) =>
            {
                ArgumentNullException.ThrowIfNull(request);
                var configured = options.Value.Operators.SingleOrDefault(o => string.Equals(o.SubjectIdentifier, request.SubjectIdentifier, StringComparison.Ordinal))
                    ?? throw new ConsoleException(ConsoleErrorCodes.UnknownOperator, 403, "that identity is not one of this console's synthetic operators");

                // Rotation / fixation: whatever session this browser presented ends now, before a new one is issued (its cookie is
                // overwritten below, or answered 401 and deleted at its next use if this sign-in is refused).
                store.Revoke(ConsoleSessionCookie.Read(http, environment));

                var correlation = Correlation(http);
                var profile = await authority.GetProfileAsync(configured.SubjectIdentifier!, correlation, cancellationToken).ConfigureAwait(false);
                var registered = profile.Operator;
                RequireSameOperator(registered, options.Value.Authority.OperatorAssertion.Issuer!, configured);
                var (sessionId, session) = store.Create(registered.PlatformOperatorId, registered.IssuerUri, registered.SubjectIdentifier, registered.DisplayName, configured.VaultKeyName!);
                ConsoleSessionCookie.Write(http, environment, sessionId);
                var logger = loggers.CreateLogger(typeof(SessionEndpoints));
                ConsoleLog.SignedIn(logger, registered.PlatformOperatorId, correlation);
                return TypedResults.Ok(View(session, profile, store.IdleTimeout));
            })
            .RequireRateLimiting(Composition.ConsoleRateLimits.LoginPolicy)
            .WithName("DevelopmentLogin")
            .WithSummary("Signs in one configured synthetic operator after the Authority confirms it is active and its registered key is its own configured key.");

        group.MapGet(string.Empty, static async Task<Ok<ConsoleSessionView>> (HttpContext http, OperatorSessionStore store, AuthorityPlatformClient authority, IOptions<PlatformConsoleOptions> options, IHostEnvironment environment, CancellationToken cancellationToken) =>
            {
                var session = RequireOperatorSession.Current(http);
                var profile = await OperatorProfiles.CurrentAsync(http, session, authority, store, options.Value, environment, cancellationToken).ConfigureAwait(false);
                return TypedResults.Ok(View(session, profile, store.IdleTimeout));
            })
            .AddEndpointFilter<RequireOperatorSession>()
            .WithName("GetConsoleSession")
            .WithSummary("The current session, re-validated against the Authority registry (a suspended or removed operator's session ends here).");

        group.MapPost("/sign-out", static NoContent (HttpContext http, OperatorSessionStore store, IHostEnvironment environment, ILoggerFactory loggers) =>
            {
                var session = RequireOperatorSession.Current(http);
                store.Revoke(ConsoleSessionCookie.Read(http, environment));
                ConsoleSessionCookie.Delete(http, environment);
                var logger = loggers.CreateLogger(typeof(SessionEndpoints));
                ConsoleLog.SignedOut(logger, session.PlatformOperatorId);
                return TypedResults.NoContent();
            })
            .AddEndpointFilter<RequireOperatorSession>()
            .WithName("SignOut")
            .WithSummary("Ends the session on the server (a copied cookie stops working) and deletes the cookie.");

        return endpoints;
    }

    internal static Guid Correlation(HttpContext http) =>
        Guid.TryParse(http.Request.Headers[AuthorityPlatformClient.CorrelationHeader].ToString(), out var id) ? id : Guid.CreateVersion7();

    /// <summary>The registry entry must be the configured identity, active, with synthetic evidence whose key is exactly the configured key.</summary>
    internal static void RequireSameOperator(PlatformOperatorSummary registered, string issuer, SyntheticOperatorOptions configured)
    {
        if (registered.IssuerUri != issuer || registered.SubjectIdentifier != configured.SubjectIdentifier || registered.Status != "Active")
        {
            throw new ConsoleException(ConsoleErrorCodes.OperatorIdentityMismatch, 403, "the Authority answered for a different or inactive operator");
        }

        if (registered.EvidenceKind != "SyntheticDevelopmentJws" || !string.Equals(registered.SyntheticKeyIdentifier, configured.VaultKeyName, StringComparison.Ordinal))
        {
            throw new ConsoleException(ConsoleErrorCodes.SigningKeyMismatch, 403, "the operator's registered signing key is not the key configured for this operator; signing is refused");
        }
    }

    private static ConsoleSessionView View(OperatorSession session, PlatformOperatorProfile profile, TimeSpan idle) =>
        new(session.PlatformOperatorId, session.DisplayName, session.IssuerUri, session.SubjectIdentifier, profile.Operator.Status, profile.Permissions, profile.ActiveOperatorCount,
            AuthenticationMode, profile.EvidenceAssurance, EnvironmentNotice, session.CsrfToken, session.IdleExpiresOn(idle), session.AbsoluteExpiresOn);
}

/// <summary>Fresh registry view of the session's operator; ends the session when the operator is no longer the one signed in.</summary>
public static class OperatorProfiles
{
    public static async Task<PlatformOperatorProfile> CurrentAsync(HttpContext http, OperatorSession session, AuthorityPlatformClient authority, OperatorSessionStore store, PlatformConsoleOptions options, IHostEnvironment environment, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(options);
        return await OperatorSessionGuard.RunAsync(http, session, store, environment, async () =>
        {
            var profile = await authority.GetProfileAsync(session.SubjectIdentifier, SessionEndpoints.Correlation(http), cancellationToken).ConfigureAwait(false);
            var configured = options.Operators.Single(o => o.SubjectIdentifier == session.SubjectIdentifier);
            if (profile.Operator.PlatformOperatorId != session.PlatformOperatorId)
            {
                throw new ConsoleException(ConsoleErrorCodes.OperatorIdentityMismatch, 403, "the registry entry for this identity changed since sign-in");
            }

            SessionEndpoints.RequireSameOperator(profile.Operator, session.IssuerUri, configured);
            return profile;
        }).ConfigureAwait(false);
    }
}

/// <summary>Ends the operator's sessions when the Authority (or the identity check) says the signed-in operator may no longer act.</summary>
public static class OperatorSessionGuard
{
    private static readonly HashSet<string> EndSessionCodes = new(StringComparer.Ordinal)
    {
        ConsoleErrorCodes.PlatformOperatorNotActive,
        ConsoleErrorCodes.OperatorIdentityMismatch,
        ConsoleErrorCodes.SigningKeyMismatch,
    };

    public static async Task<T> RunAsync<T>(HttpContext http, OperatorSession session, OperatorSessionStore store, IHostEnvironment environment, Func<Task<T>> action)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(action);
        try
        {
            return await action().ConfigureAwait(false);
        }
        catch (ConsoleException ex) when (EndSessionCodes.Contains(ex.ErrorCode))
        {
            store.RevokeOperator(session.PlatformOperatorId);
            ConsoleSessionCookie.Delete(http, environment);
            var logger = http.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(OperatorSessionGuard));
            ConsoleLog.SessionEnded(logger, session.PlatformOperatorId, ex.ErrorCode);
            throw;
        }
    }
}
