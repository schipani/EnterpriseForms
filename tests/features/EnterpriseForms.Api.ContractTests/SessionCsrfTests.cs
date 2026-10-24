using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using EnterpriseForms.Api.Composition;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using EnterpriseForms.Application.Common.Authority;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Xunit;

namespace EnterpriseForms.Api.ContractTests;

/// <summary>
/// G22 (C-58) regression: session CSRF double-submit through the real ASP.NET Core pipeline (authentication handlers,
/// <c>UseSessionCsrfProtection</c>, authorization policy, endpoints). Session cookies are issued in the host's own ticket
/// format (its Data Protection key ring), exactly as <c>StartSessionAsync</c> produces them; the business store and the
/// Authority are pointed at closed ports, so a request that passes the check fails at the store and can never mutate data.
/// The end-to-end path with real development logins runs in the Stage B integration suite (<c>SessionCsrfTests</c>).
/// G23: the Authority's session state is a <see cref="FakeSessionStateAuthority"/> here (every issued ticket registered as
/// active), so these tests keep isolating the CSRF check; revocation is covered by <c>SessionRevocationContractTests</c>.
/// </summary>
public sealed class SessionCsrfTests : IDisposable
{
    private const string CookieName = BusinessAuthentication.DevelopmentSessionCookieName;
    private readonly CapturingLoggerProvider logs = new();
    private readonly FakeSessionStateAuthority sessions = new();
    private readonly WebApplicationFactory<Program> api;

    public SessionCsrfTests()
    {
        api = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseEnvironment("Development");
            // UseSetting (not ConfigureAppConfiguration) so values read while the host is being built see the closed ports too.
            b.UseSetting("ConnectionStrings:BusinessDb", "Host=127.0.0.1;Port=9;Database=none;Username=none;Password=none;Timeout=2");
            b.UseSetting("TrustAuthority:BaseAddress", "http://127.0.0.1:9");
            b.ConfigureLogging(l => l.SetMinimumLevel(LogLevel.Trace).AddProvider(logs));
            b.ConfigureTestServices(s => s.AddSingleton<IBrowserSessionStateAuthority>(sessions));
        });
    }

    public void Dispose() => api.Dispose();

    private sealed record Session(string Cookie, string CsrfToken);

    public static TheoryData<string, string> UnsafeRoutes => new()
    {
        { "POST", "/api/v1/session/logout" },
        { "PUT", "/api/v1/me/ui-preferences" },
        { "PATCH", $"/api/v1/form-records/{Guid.CreateVersion7():D}" },
        { "DELETE", $"/api/v1/upload-sessions/{Guid.CreateVersion7():D}" },
        { "PUT", $"/api/v1/upload-sessions/{Guid.CreateVersion7():D}/chunks/0" },
        { "POST", "/api/v1/form-definitions/designer" },
    };

    [Theory]
    [MemberData(nameof(UnsafeRoutes))]
    public async Task ACookieSessionUnsafeRequestWithoutTheTokenIsRejectedBeforeTheEndpoint(string method, string path)
    {
        var session = NewSession();
        var response = await SendAsync(method, path, session.Cookie, csrf: null);
        await AssertRejectedAsync(response, session);
    }

    [Theory]
    [MemberData(nameof(UnsafeRoutes))]
    public async Task AWrongOrForeignTokenIsRejected(string method, string path)
    {
        var mine = NewSession();
        var other = NewSession();
        await AssertRejectedAsync(await SendAsync(method, path, mine.Cookie, "not-the-token"), mine);
        await AssertRejectedAsync(await SendAsync(method, path, mine.Cookie, other.CsrfToken), mine);
        await AssertRejectedAsync(await SendAsync(method, path, mine.Cookie, string.Empty), mine);
        await AssertRejectedAsync(await SendAsync(method, path, mine.Cookie, mine.CsrfToken[..^2]), mine);
    }

    [Fact]
    public async Task TheCorrectTokenPassesTheCheckAndTheRequestReachesItsEndpoint()
    {
        var session = NewSession();
        // Logout with the right token executes: the session cookie is cleared.
        var logout = await SendAsync("POST", "/api/v1/session/logout", session.Cookie, session.CsrfToken);
        Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);
        Assert.Contains(logout.Headers.TryGetValues("Set-Cookie", out var cleared) ? cleared : [], c => c.StartsWith(CookieName + "=;", StringComparison.Ordinal));
        // A business mutation with the right token passes the check and the authorization policy; it then fails only at
        // the (deliberately unreachable) store — proof that the check did not reject it. (A new session: since G23 the
        // signed-out one is revoked and no longer authenticates.)
        var active = NewSession();
        var update = await SendAsync("PUT", "/api/v1/me/ui-preferences", active.Cookie, active.CsrfToken, new { textScalePreferenceMode = "OrganizationDefault", explicitTextScalePercent = (decimal?)null });
        Assert.NotEqual(HttpStatusCode.Forbidden, update.StatusCode);
        Assert.NotEqual("CSRF_TOKEN_INVALID", await TitleAsync(update));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, update.StatusCode);
    }

    [Fact]
    public async Task ARejectedLogoutDoesNotEndTheSession()
    {
        var session = NewSession();
        var rejected = await SendAsync("POST", "/api/v1/session/logout", session.Cookie, csrf: null);
        await AssertRejectedAsync(rejected, session);
        Assert.False(rejected.Headers.Contains("Set-Cookie"));
        var probe = await SendAsync("GET", "/api/v1/session", session.Cookie, csrf: null);
        var body = await probe.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        Assert.True(body.GetProperty("authenticated").GetBoolean());
    }

    [Fact]
    public async Task ATokenFromAnEarlierSessionDoesNotWorkForANewSession()
    {
        var before = NewSession();
        Assert.Equal(HttpStatusCode.NoContent, (await SendAsync("POST", "/api/v1/session/logout", before.Cookie, before.CsrfToken)).StatusCode);
        var after = NewSession(); // a new sign-in mints a new random per-session token
        Assert.NotEqual(before.CsrfToken, after.CsrfToken);
        await AssertRejectedAsync(await SendAsync("POST", "/api/v1/session/logout", after.Cookie, before.CsrfToken), after);
    }

    [Fact]
    public async Task SafeMethodsNeedNoToken()
    {
        var session = NewSession();
        var probe = await SendAsync("GET", "/api/v1/session", session.Cookie, csrf: null);
        Assert.Equal(HttpStatusCode.OK, probe.StatusCode);
        Assert.True((await probe.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken)).GetProperty("authenticated").GetBoolean());
        Assert.NotEqual(HttpStatusCode.Forbidden, (await SendAsync("HEAD", "/api/v1/session", session.Cookie, csrf: null)).StatusCode);
    }

    [Fact]
    public async Task BearerOnlyRequestsFollowTheBearerContractWithoutACsrfToken()
    {
        using var client = api.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        using var request = new HttpRequestMessage(HttpMethod.Put, "/api/v1/me/ui-preferences") { Content = JsonContent.Create(new { textScalePreferenceMode = "OrganizationDefault", explicitTextScalePercent = (decimal?)null }) };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", MintAssertion());
        var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        Assert.NotEqual("CSRF_TOKEN_INVALID", await TitleAsync(response));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode); // authenticated, then the unreachable store
    }

    [Fact]
    public async Task AddingABearerToACookieRequestDoesNotSwitchTheCheckOff()
    {
        var session = NewSession();
        var response = await SendAsync("PUT", "/api/v1/me/ui-preferences", session.Cookie, csrf: null, new { textScalePreferenceMode = "OrganizationDefault", explicitTextScalePercent = (decimal?)null }, bearer: MintAssertion());
        await AssertRejectedAsync(response, session);
    }

    [Fact]
    public async Task AnExpiredOrForgedSessionCookieIsNotASessionAndIsRefusedByAuthentication()
    {
        var expired = NewSession(expiresIn: TimeSpan.FromMinutes(-5));
        var stale = await SendAsync("PUT", "/api/v1/me/ui-preferences", expired.Cookie, expired.CsrfToken, new { textScalePreferenceMode = "OrganizationDefault", explicitTextScalePercent = (decimal?)null });
        Assert.Equal(HttpStatusCode.Unauthorized, stale.StatusCode);
        var forged = await SendAsync("PUT", "/api/v1/me/ui-preferences", CookieName + "=forged-ticket-value", "anything", new { textScalePreferenceMode = "OrganizationDefault", explicitTextScalePercent = (decimal?)null });
        Assert.Equal(HttpStatusCode.Unauthorized, forged.StatusCode);
    }

    [Theory]
    [InlineData("https://attacker.example", "cross-site")]
    [InlineData("http://localhost", "same-origin")]
    [InlineData(null, "same-site")]
    public async Task OriginAndFetchMetadataCannotStandInForTheToken(string? origin, string fetchSite)
    {
        var session = NewSession();
        var response = await SendAsync("POST", "/api/v1/session/logout", session.Cookie, csrf: null, extraHeaders: request =>
        {
            if (origin is not null)
            {
                request.Headers.Add("Origin", origin);
            }

            request.Headers.Add("Sec-Fetch-Site", fetchSite);
            request.Headers.Add("Sec-Fetch-Mode", "cors");
        });
        await AssertRejectedAsync(response, session);
    }

    private async Task AssertRejectedAsync(HttpResponseMessage response, Session session)
    {
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var text = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        using var body = JsonDocument.Parse(text);
        Assert.Equal("CSRF_TOKEN_INVALID", body.RootElement.GetProperty("title").GetString());
        // Only the fixed problem fields: no token, assertion, claim or session detail.
        Assert.Equal(["detail", "status", "title", "type"], body.RootElement.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
        Assert.DoesNotContain(session.CsrfToken, text, StringComparison.Ordinal);
        Assert.DoesNotContain(logs.Messages, m => m.Contains(session.CsrfToken, StringComparison.Ordinal));
    }

    private static async Task<string?> TitleAsync(HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        try
        {
            using var body = JsonDocument.Parse(text);
            return body.RootElement.TryGetProperty("title", out var title) ? title.GetString() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private async Task<HttpResponseMessage> SendAsync(string method, string path, string? cookie, string? csrf, object? body = null, string? bearer = null, Action<HttpRequestMessage>? extraHeaders = null)
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

        if (bearer is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        }

        extraHeaders?.Invoke(request);
        return await client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    /// <summary>A session cookie exactly as StartSessionAsync issues it, protected with the host's own ticket format.</summary>
    private Session NewSession(TimeSpan? expiresIn = null)
    {
        var issued = SessionTestSupport.Issue(api, sessions, expiresIn);
        return new Session(issued.Cookie, issued.CsrfToken);
    }

    private string MintAssertion() => SessionTestSupport.MintAssertion(api);

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
