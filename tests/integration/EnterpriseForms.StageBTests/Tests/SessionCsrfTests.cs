using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using EnterpriseForms.StageBTests.Harness;
using Xunit;

namespace EnterpriseForms.StageBTests.Tests;

/// <summary>
/// G22 (C-58) on the real stack: browser sessions created by real development logins (BFF cookie + per-session CSRF claim),
/// the real API pipeline, the Authority and both databases. Unsafe cookie-session requests need that session's own
/// X-CSRF-Token; rejected requests change nothing; bearer requests keep their own contract; adding a bearer never switches
/// the check off.
/// </summary>
[Collection(StageBStackCollectionDefinition.Name)]
public sealed class SessionCsrfTests(StageBFixture fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class BrowserSession : IDisposable
    {
        private readonly HttpClientHandler handler = new() { CookieContainer = new CookieContainer(), UseCookies = true, AllowAutoRedirect = false };

        public BrowserSession()
        {
            Client = new HttpClient(handler) { BaseAddress = new Uri(StageBFixture.ApiBaseAddress), Timeout = TimeSpan.FromMinutes(2) };
        }

        public HttpClient Client { get; }

        public string CsrfToken { get; set; } = string.Empty;

        public string CookieHeader => handler.CookieContainer.GetCookieHeader(new Uri(StageBFixture.ApiBaseAddress));

        public void Dispose()
        {
            Client.Dispose();
            handler.Dispose();
        }
    }

    private static async Task<BrowserSession> SignInAsync(string persona)
    {
        var session = new BrowserSession();
        var login = await session.Client.PostAsJsonAsync("/api/v1/session/development-login", new { personaKey = persona }, Ct);
        Assert.True(login.IsSuccessStatusCode, await login.Content.ReadAsStringAsync(Ct));
        session.CsrfToken = (await login.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("csrfToken").GetString()!;
        Assert.False(string.IsNullOrEmpty(session.CsrfToken));
        Assert.NotEmpty(session.CookieHeader);
        return session;
    }

    private static async Task<HttpResponseMessage> SendAsync(BrowserSession session, HttpMethod method, string path, string? csrf, object? body = null, string? bearer = null, Action<HttpRequestMessage>? headers = null)
    {
        using var request = new HttpRequestMessage(method, path);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        if (csrf is not null)
        {
            request.Headers.Add("X-CSRF-Token", csrf);
        }

        if (bearer is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        }

        headers?.Invoke(request);
        return await session.Client.SendAsync(request, Ct);
    }

    private static async Task<long> PreferenceRevisionAsync(BrowserSession session)
    {
        var current = await session.Client.GetFromJsonAsync<JsonElement>("/api/v1/me/ui-preferences", Ct);
        return current.GetProperty("preferenceRevision").GetInt64();
    }

    private static async Task AssertCsrfRejectedAsync(HttpResponseMessage response, BrowserSession session)
    {
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var text = await response.Content.ReadAsStringAsync(Ct);
        using var body = JsonDocument.Parse(text);
        Assert.Equal("CSRF_TOKEN_INVALID", body.RootElement.GetProperty("title").GetString());
        Assert.Equal(["detail", "status", "title", "type"], body.RootElement.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
        Assert.DoesNotContain(session.CsrfToken, text, StringComparison.Ordinal);
        Assert.False(response.Headers.Contains("Set-Cookie"));
    }

    private static object Preference(decimal scale) => new { textScalePreferenceMode = "Explicit", explicitTextScalePercent = scale };

    [Fact]
    public async Task UnsafeCookieRequestsWithoutAValidTokenAreRejectedAndChangeNothing()
    {
        using var owner = await SignInAsync("owner");
        using var other = await SignInAsync("reader");
        var before = await PreferenceRevisionAsync(owner);

        // Missing, wrong, empty, truncated and another session's token: all rejected, preference untouched.
        foreach (var token in new string?[] { null, "not-the-token", string.Empty, owner.CsrfToken[..^2], other.CsrfToken })
        {
            await AssertCsrfRejectedAsync(await SendAsync(owner, HttpMethod.Put, "/api/v1/me/ui-preferences", token, Preference(125.0m)), owner);
        }

        Assert.Equal(before, await PreferenceRevisionAsync(owner));

        // Every unsafe method is covered, before routing to the endpoint (ids that do not exist: the check answers first).
        await AssertCsrfRejectedAsync(await SendAsync(owner, HttpMethod.Patch, $"/api/v1/form-records/{Guid.CreateVersion7():D}", null, new { expectedContentRevision = 1, values = new Dictionary<string, string>() }), owner);
        await AssertCsrfRejectedAsync(await SendAsync(owner, HttpMethod.Delete, $"/api/v1/upload-sessions/{Guid.CreateVersion7():D}", null), owner);
        await AssertCsrfRejectedAsync(await SendAsync(owner, HttpMethod.Post, "/api/v1/form-definitions/designer", null, new { formDefinitionName = "CSRF probe" }), owner);

        // A rejected logout leaves the session working.
        await AssertCsrfRejectedAsync(await SendAsync(owner, HttpMethod.Post, "/api/v1/session/logout", null), owner);
        Assert.True((await owner.Client.GetFromJsonAsync<JsonElement>("/api/v1/session", Ct)).GetProperty("authenticated").GetBoolean());
    }

    [Fact]
    public async Task TheSessionsOwnTokenSucceedsAndTheMutationHappens()
    {
        using var owner = await SignInAsync("owner");
        var before = await PreferenceRevisionAsync(owner);
        var update = await SendAsync(owner, HttpMethod.Put, "/api/v1/me/ui-preferences", owner.CsrfToken, Preference(before % 2 == 0 ? 125.0m : 112.5m));
        Assert.Equal(HttpStatusCode.OK, update.StatusCode);
        Assert.Equal(before + 1, await PreferenceRevisionAsync(owner));
        var reset = await SendAsync(owner, HttpMethod.Put, "/api/v1/me/ui-preferences", owner.CsrfToken, new { textScalePreferenceMode = "OrganizationDefault", explicitTextScalePercent = (decimal?)null });
        Assert.Equal(HttpStatusCode.OK, reset.StatusCode);
    }

    [Fact]
    public async Task AfterLogoutAndANewSignInTheEarlierTokenIsUseless()
    {
        using var first = await SignInAsync("owner");
        var oldToken = first.CsrfToken;
        Assert.Equal(HttpStatusCode.NoContent, (await SendAsync(first, HttpMethod.Post, "/api/v1/session/logout", oldToken)).StatusCode);
        Assert.False((await first.Client.GetFromJsonAsync<JsonElement>("/api/v1/session", Ct)).GetProperty("authenticated").GetBoolean());

        var login = await first.Client.PostAsJsonAsync("/api/v1/session/development-login", new { personaKey = "owner" }, Ct);
        Assert.True(login.IsSuccessStatusCode);
        first.CsrfToken = (await login.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("csrfToken").GetString()!;
        Assert.NotEqual(oldToken, first.CsrfToken);
        var replay = await SendAsync(first, HttpMethod.Put, "/api/v1/me/ui-preferences", oldToken, Preference(125.0m));
        await AssertCsrfRejectedAsync(replay, first);
    }

    [Fact]
    public async Task SigningInAgainOverALiveSessionRequiresThatSessionsToken()
    {
        using var session = await SignInAsync("owner");
        var without = await session.Client.PostAsJsonAsync("/api/v1/session/development-login", new { personaKey = "reader" }, Ct);
        await AssertCsrfRejectedAsync(without, session);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/session/development-login") { Content = JsonContent.Create(new { personaKey = "reader" }) };
        request.Headers.Add("X-CSRF-Token", session.CsrfToken);
        Assert.Equal(HttpStatusCode.OK, (await session.Client.SendAsync(request, Ct)).StatusCode);
    }

    [Fact]
    public async Task OriginAndFetchMetadataCannotStandInForTheToken()
    {
        using var owner = await SignInAsync("owner");
        foreach (var (origin, site) in new[] { ("https://attacker.example", "cross-site"), (StageBFixture.ApiBaseAddress, "same-origin"), ("http://sibling.127.0.0.1.nip.io", "same-site") })
        {
            var response = await SendAsync(owner, HttpMethod.Put, "/api/v1/me/ui-preferences", null, Preference(125.0m), headers: r =>
            {
                r.Headers.Add("Origin", origin);
                r.Headers.Add("Sec-Fetch-Site", site);
            });
            await AssertCsrfRejectedAsync(response, owner);
        }
    }

    [Fact]
    public async Task BearerRequestsKeepTheirContractAndABearerCannotSwitchTheCookieCheckOff()
    {
        var s = new StageBScenario(fixture, "csrf-bearer");
        await s.BootstrapAsync();
        using var bearerOnly = new BrowserSession();
        var direct = await SendAsync(bearerOnly, HttpMethod.Put, "/api/v1/me/ui-preferences", null, Preference(125.0m), bearer: s.Token(s.Clerk));
        Assert.Equal(HttpStatusCode.OK, direct.StatusCode);

        using var cookie = await SignInAsync("owner");
        var mixed = await SendAsync(cookie, HttpMethod.Put, "/api/v1/me/ui-preferences", null, Preference(125.0m), bearer: s.Token(s.Clerk));
        await AssertCsrfRejectedAsync(mixed, cookie);
    }
}
