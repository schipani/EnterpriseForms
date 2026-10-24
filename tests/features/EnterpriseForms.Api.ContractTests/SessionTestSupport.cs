using System.Collections.Concurrent;
using System.Security.Claims;
using System.Security.Cryptography;
using EnterpriseForms.Api.Composition;
using EnterpriseForms.Application.Common.Authority;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace EnterpriseForms.Api.ContractTests;

/// <summary>
/// Session tickets exactly as <c>StartSessionAsync</c> issues them (the host's own ticket format and key ring), for the
/// G22/G23 pipeline tests. The Authority's session state is replaced by <see cref="FakeSessionStateAuthority"/>; the real
/// Authority protocol is proven by the Docker integration tests.
/// </summary>
internal static class SessionTestSupport
{
    public const string CookieName = BusinessAuthentication.DevelopmentSessionCookieName;

    public sealed record IssuedSession(string Cookie, string CsrfToken, string Secret, string Digest);

    public static IssuedSession Issue(WebApplicationFactory<Program> api, FakeSessionStateAuthority? sessions, TimeSpan? expiresIn = null, bool withSessionSecret = true, string subject = "g22-csrf-test")
    {
        ArgumentNullException.ThrowIfNull(api);
        var csrf = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var secret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var settings = api.Services.GetRequiredService<IOptions<UserAuthenticationOptions>>().Value;
        var organization = Guid.CreateVersion7();
        var identity = new ClaimsIdentity(BusinessAuthentication.SessionScheme);
        identity.AddClaim(new Claim("iss", settings.Issuer ?? string.Empty));
        identity.AddClaim(new Claim("sub", subject));
        identity.AddClaim(new Claim("org", organization.ToString("D")));
        identity.AddClaim(new Claim("authority_user_id", Guid.CreateVersion7().ToString("D")));
        identity.AddClaim(new Claim("name", "Session test"));
        identity.AddClaim(new Claim(BusinessAuthentication.AssertionClaim, MintAssertion(api, subject, organization)));
        identity.AddClaim(new Claim(BusinessAuthentication.CsrfClaim, csrf));
        if (withSessionSecret)
        {
            identity.AddClaim(new Claim(BusinessAuthentication.SessionSecretClaim, secret));
        }

        var now = DateTimeOffset.UtcNow;
        var properties = new AuthenticationProperties { IsPersistent = false, IssuedUtc = now, ExpiresUtc = now + (expiresIn ?? TimeSpan.FromHours(1)), AllowRefresh = false };
        var options = api.Services.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>().Get(BusinessAuthentication.SessionScheme);
        var value = options.TicketDataFormat.Protect(new AuthenticationTicket(new ClaimsPrincipal(identity), properties, BusinessAuthentication.SessionScheme));
        var digest = FakeSessionStateAuthority.DigestOf(secret);
        sessions?.Register(digest);
        return new IssuedSession(ChunkedCookie(value), csrf, secret, digest);
    }

    public static string MintAssertion(WebApplicationFactory<Program> api, string subject = "g22-csrf-test", Guid? organization = null)
    {
        ArgumentNullException.ThrowIfNull(api);
        var settings = api.Services.GetRequiredService<IOptions<UserAuthenticationOptions>>().Value;
        var now = DateTime.UtcNow;
        return new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = settings.Issuer,
            Audience = settings.Audience,
            IssuedAt = now,
            NotBefore = now.AddSeconds(-5),
            Expires = now.AddMinutes(30),
            Claims = new Dictionary<string, object> { ["sub"] = subject, ["org"] = (organization ?? Guid.CreateVersion7()).ToString("D"), ["authority_user_id"] = Guid.CreateVersion7().ToString("D"), ["name"] = "Session test" },
            SigningCredentials = new SigningCredentials(settings.SymmetricKey(), SecurityAlgorithms.HmacSha256),
        });
    }

    /// <summary>The cookie manager splits values above 4050 characters into "chunks-N" plus C1..CN cookies.</summary>
    public static string ChunkedCookie(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        const int chunk = 4000;
        if (value.Length <= chunk)
        {
            return $"{CookieName}={value}";
        }

        var parts = Enumerable.Range(0, (value.Length + chunk - 1) / chunk).Select(i => value.Substring(i * chunk, Math.Min(chunk, value.Length - (i * chunk)))).ToList();
        return $"{CookieName}=chunks-{parts.Count}; " + string.Join("; ", parts.Select((p, i) => $"{CookieName}C{i + 1}={p}"));
    }
}

/// <summary>Stand-in for the Authority's session registry (digests only), with switchable unavailability.</summary>
internal sealed class FakeSessionStateAuthority : IBrowserSessionStateAuthority
{
    private readonly ConcurrentDictionary<string, string?> sessions = new(StringComparer.Ordinal); // digest -> revocation reason (null = active)

    public bool Unavailable { get; set; }

    public ConcurrentQueue<(string Digest, string? ReplacedDigest)> Created { get; } = new();

    public int ValidateCalls => validateCalls;

    private int validateCalls;

    public static string DigestOf(string secret) => Convert.ToBase64String(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(secret)));

    public void Register(string digest) => sessions[digest] = null;

    public void Revoke(string digest) => sessions[digest] = "SESSION_REVOKED";

    public bool IsActive(string digest) => sessions.TryGetValue(digest, out var reason) && reason is null;

    public Task<BrowserSessionState> CreateSessionAsync(DelegatedUserContext user, string sessionDigestBase64, DateTimeOffset absoluteExpiresOn, string? replacedSessionDigestBase64, CancellationToken cancellationToken)
    {
        ThrowIfUnavailable();
        if (replacedSessionDigestBase64 is not null && sessions.ContainsKey(replacedSessionDigestBase64))
        {
            sessions[replacedSessionDigestBase64] = "REPLACED_BY_NEW_SIGN_IN";
        }

        sessions[sessionDigestBase64] = null;
        Created.Enqueue((sessionDigestBase64, replacedSessionDigestBase64));
        return Task.FromResult(new BrowserSessionState(true, null, absoluteExpiresOn, Guid.CreateVersion7()));
    }

    public Task<BrowserSessionState> ValidateSessionAsync(DelegatedUserContext user, string sessionDigestBase64, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref validateCalls);
        ThrowIfUnavailable();
        return Task.FromResult(sessions.TryGetValue(sessionDigestBase64, out var reason)
            ? new BrowserSessionState(reason is null, reason, null, Guid.CreateVersion7())
            : new BrowserSessionState(false, "SESSION_UNKNOWN", null, Guid.CreateVersion7()));
    }

    public Task<BrowserSessionState> RevokeSessionAsync(DelegatedUserContext user, string sessionDigestBase64, CancellationToken cancellationToken)
    {
        ThrowIfUnavailable();
        if (sessions.ContainsKey(sessionDigestBase64))
        {
            sessions[sessionDigestBase64] = "SESSION_REVOKED";
        }

        return Task.FromResult(new BrowserSessionState(false, "SESSION_REVOKED", null, Guid.CreateVersion7()));
    }

    private void ThrowIfUnavailable()
    {
        if (Unavailable)
        {
            throw new AuthorityClientException("AUTHORITY_UNAVAILABLE", 503, true, "session store unavailable (test)");
        }
    }
}
