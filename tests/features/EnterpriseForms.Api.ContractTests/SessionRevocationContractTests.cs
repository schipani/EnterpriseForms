using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using EnterpriseForms.Api.Composition;
using EnterpriseForms.Application.Common.Authority;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace EnterpriseForms.Api.ContractTests;

/// <summary>
/// G23 (C-63) through the real ASP.NET Core pipeline: a session cookie authenticates only while the Authority reports
/// the session valid; sign-out revokes it there before the cookie is deleted; an unreadable session state fails closed
/// (503, cookie kept); the secret never leaves the process except as a digest and never reaches a log. The Authority's
/// registry is a fake here — the real copied-cookie, multi-instance and outage proofs run against Docker
/// (<c>SessionRevocationTests</c> in the integration suite).
/// </summary>
public sealed class SessionRevocationContractTests : IDisposable
{
    private static readonly object Preferences = new { textScalePreferenceMode = "OrganizationDefault", explicitTextScalePercent = (decimal?)null };
    private readonly CapturingLoggerProvider logs = new();
    private readonly FakeSessionStateAuthority sessions = new();
    private readonly WebApplicationFactory<Program> api;

    public SessionRevocationContractTests()
    {
        api = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseEnvironment("Development");
            b.UseSetting("ConnectionStrings:BusinessDb", "Host=127.0.0.1;Port=9;Database=none;Username=none;Password=none;Timeout=2");
            b.UseSetting("TrustAuthority:BaseAddress", "http://127.0.0.1:9");
            b.UseSetting("DevelopmentIdentity:Enabled", "true");
            b.UseSetting("DevelopmentIdentity:PersonasFilePath", string.Empty);
            b.UseSetting("DevelopmentIdentity:Personas:0:PersonaKey", "g23-persona");
            b.UseSetting("DevelopmentIdentity:Personas:0:DisplayName", "G23 Persona");
            b.UseSetting("DevelopmentIdentity:Personas:0:AuthorityUserId", Guid.CreateVersion7().ToString("D"));
            b.UseSetting("DevelopmentIdentity:Personas:0:SubjectIdentifier", "g23-persona");
            b.UseSetting("DevelopmentIdentity:Personas:0:OrganizationId", Guid.CreateVersion7().ToString("D"));
            b.UseSetting("DevelopmentIdentity:Personas:0:OrganizationDisplayName", "G23 Org");
            b.ConfigureLogging(l => l.SetMinimumLevel(LogLevel.Trace).AddProvider(logs));
            b.ConfigureTestServices(s => s.AddSingleton<IBrowserSessionStateAuthority>(sessions));
        });
    }

    public void Dispose() => api.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task AnActiveSessionAuthenticatesAndIsCheckedWithTheAuthorityOnEveryRequest()
    {
        var session = SessionTestSupport.Issue(api, sessions);
        var before = sessions.ValidateCalls;
        Assert.True(await AuthenticatedAsync(session.Cookie));
        Assert.True(await AuthenticatedAsync(session.Cookie));
        Assert.Equal(before + 2, sessions.ValidateCalls);
    }

    [Fact]
    public async Task ACopiedCookieStopsWorkingOnceTheSessionIsRevoked()
    {
        var session = SessionTestSupport.Issue(api, sessions);
        Assert.NotEqual(HttpStatusCode.Unauthorized, (await SendAsync("PUT", "/api/v1/me/ui-preferences", session.Cookie, session.CsrfToken, Preferences)).StatusCode);

        sessions.Revoke(session.Digest); // e.g. signed out in another tab, or on another API instance

        var copied = await SendAsync("PUT", "/api/v1/me/ui-preferences", session.Cookie, session.CsrfToken, Preferences);
        Assert.Equal(HttpStatusCode.Unauthorized, copied.StatusCode); // not 403 CSRF: there is no session left to check against
        var probe = await SendAsync("GET", "/api/v1/session", session.Cookie, csrf: null);
        Assert.False((await probe.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("authenticated").GetBoolean());
        Assert.Contains(probe.Headers.TryGetValues("Set-Cookie", out var cleared) ? cleared : [], c => c.StartsWith(SessionTestSupport.CookieName + "=;", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SignOutRevokesAtTheAuthorityBeforeDeletingTheCookie()
    {
        var session = SessionTestSupport.Issue(api, sessions);
        var logout = await SendAsync("POST", "/api/v1/session/logout", session.Cookie, session.CsrfToken);
        Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);
        Assert.False(sessions.IsActive(session.Digest));

        // The same cookie and CSRF token, replayed after sign-out, authenticate nothing.
        Assert.Equal(HttpStatusCode.Unauthorized, (await SendAsync("PUT", "/api/v1/me/ui-preferences", session.Cookie, session.CsrfToken, Preferences)).StatusCode);
        Assert.False(await AuthenticatedAsync(session.Cookie));
    }

    [Fact]
    public async Task WhenTheSessionStateCannotBeReadEveryCookieRequestFailsClosedWithoutEndingTheSession()
    {
        var session = SessionTestSupport.Issue(api, sessions);
        sessions.Unavailable = true;

        var probe = await SendAsync("GET", "/api/v1/session", session.Cookie, csrf: null);
        await AssertUnavailableAsync(probe);
        var mutation = await SendAsync("PUT", "/api/v1/me/ui-preferences", session.Cookie, session.CsrfToken, Preferences);
        await AssertUnavailableAsync(mutation);
        var read = await SendAsync("GET", "/api/v1/bootstrap", session.Cookie, csrf: null);
        await AssertUnavailableAsync(read);
        var logout = await SendAsync("POST", "/api/v1/session/logout", session.Cookie, session.CsrfToken);
        await AssertUnavailableAsync(logout);

        sessions.Unavailable = false;
        Assert.True(sessions.IsActive(session.Digest));
        Assert.True(await AuthenticatedAsync(session.Cookie)); // the outage did not sign the user out
    }

    [Fact]
    public async Task ATicketWithoutASessionSecretIsNotASession()
    {
        var legacy = SessionTestSupport.Issue(api, sessions, withSessionSecret: false); // e.g. a cookie issued before G23
        var before = sessions.ValidateCalls;
        Assert.Equal(HttpStatusCode.Unauthorized, (await SendAsync("PUT", "/api/v1/me/ui-preferences", legacy.Cookie, legacy.CsrfToken, Preferences)).StatusCode);
        Assert.False(await AuthenticatedAsync(legacy.Cookie));
        Assert.Equal(before, sessions.ValidateCalls);
    }

    [Fact]
    public async Task AnUnregisteredSessionSecretIsNotASession()
    {
        var unknown = SessionTestSupport.Issue(api, sessions: null); // genuine ticket format, never registered with the Authority
        Assert.Equal(HttpStatusCode.Unauthorized, (await SendAsync("PUT", "/api/v1/me/ui-preferences", unknown.Cookie, unknown.CsrfToken, Preferences)).StatusCode);
    }

    [Fact]
    public async Task SignInRegistersAFreshSessionAndRevokesTheOneTheBrowserPresented()
    {
        var planted = SessionTestSupport.Issue(api, sessions, subject: "someone-else");
        var login = await SendAsync("POST", "/api/v1/session/development-login", planted.Cookie, planted.CsrfToken, new { personaKey = "g23-persona" });

        // The login itself completes at the Authority; the rest of the endpoint then fails at the deliberately closed business store.
        var (digest, replaced) = Assert.Single(sessions.Created);
        Assert.Equal(planted.Digest, replaced);
        Assert.False(sessions.IsActive(planted.Digest));
        Assert.True(sessions.IsActive(digest));
        Assert.NotEqual(planted.Digest, digest);
        Assert.NotEqual(HttpStatusCode.Forbidden, login.StatusCode);
    }

    [Fact]
    public async Task NoCookieIsIssuedWhenTheSessionCannotBeRegistered()
    {
        sessions.Unavailable = true;
        var login = await SendAsync("POST", "/api/v1/session/development-login", cookie: null, csrf: null, new { personaKey = "g23-persona" });
        Assert.Equal(HttpStatusCode.ServiceUnavailable, login.StatusCode);
        Assert.DoesNotContain(login.Headers.TryGetValues("Set-Cookie", out var set) ? set : [], c => c.StartsWith(SessionTestSupport.CookieName + "=", StringComparison.Ordinal) && !c.StartsWith(SessionTestSupport.CookieName + "=;", StringComparison.Ordinal));
        Assert.Empty(sessions.Created);
    }

    [Fact]
    public async Task TheSessionSecretAndItsDigestNeverReachTheLogsOrTheBrowserBody()
    {
        var session = SessionTestSupport.Issue(api, sessions);
        var probe = await SendAsync("GET", "/api/v1/session", session.Cookie, csrf: null);
        var text = await probe.Content.ReadAsStringAsync(Ct);
        sessions.Revoke(session.Digest);
        await SendAsync("GET", "/api/v1/session", session.Cookie, csrf: null);
        sessions.Unavailable = true;
        await SendAsync("GET", "/api/v1/session", session.Cookie, csrf: null);

        Assert.DoesNotContain(session.Secret, text, StringComparison.Ordinal);
        Assert.DoesNotContain(session.Digest, text, StringComparison.Ordinal);
        Assert.DoesNotContain(logs.Messages, m => m.Contains(session.Secret, StringComparison.Ordinal) || m.Contains(session.Digest, StringComparison.Ordinal));
        Assert.Contains(logs.Messages, m => m.Contains("Session no longer valid (SESSION_REVOKED)", StringComparison.Ordinal));
    }

    private static async Task AssertUnavailableAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        Assert.Equal(BusinessAuthentication.SessionStateUnavailableCode, body.RootElement.TryGetProperty("title", out var title) ? title.GetString() : body.RootElement.GetProperty("error_code").GetString());
        Assert.DoesNotContain(response.Headers.TryGetValues("Set-Cookie", out var set) ? set : [], c => c.StartsWith(SessionTestSupport.CookieName, StringComparison.Ordinal));
    }

    private async Task<bool> AuthenticatedAsync(string cookie)
    {
        var probe = await SendAsync("GET", "/api/v1/session", cookie, csrf: null);
        Assert.Equal(HttpStatusCode.OK, probe.StatusCode);
        return (await probe.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("authenticated").GetBoolean();
    }

    private async Task<HttpResponseMessage> SendAsync(string method, string path, string? cookie, string? csrf, object? body = null)
    {
        using var client = api.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false, AllowAutoRedirect = false });
        using var request = new HttpRequestMessage(new HttpMethod(method), path);
        if (body is not null || method is "POST" or "PUT" or "PATCH")
        {
            request.Content = JsonContent.Create(body ?? new { });
        }

        if (cookie is not null)
        {
            request.Headers.Add("Cookie", cookie);
        }

        if (csrf is not null)
        {
            request.Headers.Add(BusinessAuthentication.CsrfHeader, csrf);
        }

        return await client.SendAsync(request, Ct);
    }

    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        public ConcurrentQueue<string> Messages { get; } = new();

        public ILogger CreateLogger(string categoryName) => new Capturing(this);

        public void Dispose()
        {
        }

        private sealed class Capturing(CapturingLoggerProvider owner) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
                owner.Messages.Enqueue(formatter(state, exception) + " " + exception);
        }
    }
}
