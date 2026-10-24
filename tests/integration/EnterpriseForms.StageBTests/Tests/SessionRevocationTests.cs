using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using EnterpriseForms.SecurityTests.Harness;
using EnterpriseForms.StageBTests.Harness;
using Xunit;

namespace EnterpriseForms.StageBTests.Tests;

/// <summary>
/// G23 (C-63) on the real stack: sessions from real development logins, two business API instances sharing one key ring
/// (5180 and 5181), the real Authority session registry in AuthorityDb. A copied session cookie works until sign-out and
/// never after, on either instance; a new sign-in replaces the session the browser presented; a session whose state the
/// Authority cannot read fails closed; the state is out of the business database administrator's reach.
/// </summary>
[Collection(StageBStackCollectionDefinition.Name)]
public sealed class SessionRevocationTests(StageBFixture fixture)
{
    private const string SecondaryApiBaseAddress = "http://127.0.0.1:5181";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>A browser: cookie jar plus the CSRF token its session was issued with.</summary>
    private sealed class Browser : IDisposable
    {
        private readonly HttpClientHandler handler = new() { CookieContainer = new CookieContainer(), UseCookies = true, AllowAutoRedirect = false };

        public Browser(string baseAddress = StageBFixture.ApiBaseAddress)
        {
            BaseAddress = baseAddress;
            Client = new HttpClient(handler) { BaseAddress = new Uri(baseAddress), Timeout = TimeSpan.FromMinutes(2) };
        }

        public string BaseAddress { get; }

        public HttpClient Client { get; }

        public string CsrfToken { get; set; } = string.Empty;

        /// <summary>What an attacker who copied the cookie (and the CSRF token) holds.</summary>
        public CopiedSession Copy() => new(handler.CookieContainer.GetCookieHeader(new Uri(BaseAddress)), CsrfToken);

        public void Dispose()
        {
            Client.Dispose();
            handler.Dispose();
        }
    }

    private sealed record CopiedSession(string CookieHeader, string CsrfToken);

    private static async Task<Browser> SignInAsync(string persona, string baseAddress = StageBFixture.ApiBaseAddress)
    {
        var browser = new Browser(baseAddress);
        await SignInAgainAsync(browser, persona);
        return browser;
    }

    private static async Task SignInAgainAsync(Browser browser, string persona)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/session/development-login") { Content = JsonContent.Create(new { personaKey = persona }) };
        if (browser.CsrfToken.Length > 0)
        {
            request.Headers.Add("X-CSRF-Token", browser.CsrfToken); // signing in over a live session needs that session's token (G22)
        }

        var login = await browser.Client.SendAsync(request, Ct);
        Assert.True(login.IsSuccessStatusCode, await login.Content.ReadAsStringAsync(Ct));
        browser.CsrfToken = (await login.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("csrfToken").GetString()!;
    }

    private static async Task<HttpResponseMessage> SignOutAsync(Browser browser)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/session/logout");
        request.Headers.Add("X-CSRF-Token", browser.CsrfToken);
        return await browser.Client.SendAsync(request, Ct);
    }

    /// <summary>Replays a copied cookie (and its CSRF token) from a separate client with no cookie jar of its own.</summary>
    private static async Task<HttpResponseMessage> ReplayAsync(CopiedSession copy, HttpMethod method, string path, object? body = null, string baseAddress = StageBFixture.ApiBaseAddress)
    {
        using var client = new HttpClient(new HttpClientHandler { UseCookies = false, AllowAutoRedirect = false }) { BaseAddress = new Uri(baseAddress), Timeout = TimeSpan.FromMinutes(2) };
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Add("Cookie", copy.CookieHeader);
        request.Headers.Add("X-CSRF-Token", copy.CsrfToken);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        return await client.SendAsync(request, Ct);
    }

    private static async Task<bool> AuthenticatedAsync(CopiedSession copy, string baseAddress = StageBFixture.ApiBaseAddress)
    {
        var probe = await ReplayAsync(copy, HttpMethod.Get, "/api/v1/session", baseAddress: baseAddress);
        Assert.Equal(HttpStatusCode.OK, probe.StatusCode);
        return (await probe.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("authenticated").GetBoolean();
    }

    private static async Task<long> PreferenceRevisionAsync(Browser browser) =>
        (await browser.Client.GetFromJsonAsync<JsonElement>("/api/v1/me/ui-preferences", Ct)).GetProperty("preferenceRevision").GetInt64();

    private static object Preference(decimal scale) => new { textScalePreferenceMode = "Explicit", explicitTextScalePercent = scale };

    private static object ResetPreference => new { textScalePreferenceMode = "OrganizationDefault", explicitTextScalePercent = (decimal?)null };

    [Fact]
    public async Task ACopiedCookieWorksBeforeSignOutAndNeverAfterEvenWithItsCsrfToken()
    {
        using var browser = await SignInAsync("owner");
        var copy = browser.Copy();

        // 1. Before sign-out the copy is as good as the original: it reads and (with the copied CSRF token) mutates.
        Assert.True(await AuthenticatedAsync(copy));
        Assert.Equal(HttpStatusCode.OK, (await ReplayAsync(copy, HttpMethod.Put, "/api/v1/me/ui-preferences", Preference(125.0m))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await ReplayAsync(copy, HttpMethod.Put, "/api/v1/me/ui-preferences", ResetPreference)).StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await SignOutAsync(browser)).StatusCode);

        // 1/2. After sign-out: no read, no bootstrap, no mutation — not even with the old CSRF token — and nothing changed.
        Assert.False(await AuthenticatedAsync(copy));
        Assert.Equal(HttpStatusCode.Unauthorized, (await ReplayAsync(copy, HttpMethod.Get, "/api/v1/bootstrap")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await ReplayAsync(copy, HttpMethod.Get, "/api/v1/me/ui-preferences")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await ReplayAsync(copy, HttpMethod.Put, "/api/v1/me/ui-preferences", Preference(150.0m))).StatusCode);
        using var verifier = await SignInAsync("owner");
        Assert.NotEqual("Explicit", (await verifier.Client.GetFromJsonAsync<JsonElement>("/api/v1/me/ui-preferences", Ct)).GetProperty("textScalePreferenceMode").GetString());

        // Signing out again with the copy is a harmless no-op and does not revive anything.
        Assert.Equal(HttpStatusCode.NoContent, (await ReplayAsync(copy, HttpMethod.Post, "/api/v1/session/logout")).StatusCode);
        Assert.False(await AuthenticatedAsync(copy));
    }

    [Fact]
    public async Task AFreshSignInWorksAfterSignOutWhileTheOldCookieStaysDead()
    {
        using var browser = await SignInAsync("owner");
        var old = browser.Copy();
        Assert.Equal(HttpStatusCode.NoContent, (await SignOutAsync(browser)).StatusCode);

        browser.CsrfToken = string.Empty;
        await SignInAgainAsync(browser, "owner");
        var fresh = browser.Copy();
        Assert.NotEqual(old.CookieHeader, fresh.CookieHeader);
        Assert.NotEqual(old.CsrfToken, fresh.CsrfToken);
        Assert.True(await AuthenticatedAsync(fresh));
        var before = await PreferenceRevisionAsync(browser);
        using (var update = new HttpRequestMessage(HttpMethod.Put, "/api/v1/me/ui-preferences") { Content = JsonContent.Create(Preference(before % 2 == 0 ? 125.0m : 112.5m)) })
        {
            update.Headers.Add("X-CSRF-Token", browser.CsrfToken);
            Assert.Equal(HttpStatusCode.OK, (await browser.Client.SendAsync(update, Ct)).StatusCode);
        }

        Assert.Equal(before + 1, await PreferenceRevisionAsync(browser));
        Assert.False(await AuthenticatedAsync(old));
    }

    [Fact]
    public async Task SigningInAgainOrAsSomeoneElseEndsThePresentedSession()
    {
        using var browser = await SignInAsync("owner");
        var ownerSession = browser.Copy();

        await SignInAgainAsync(browser, "reader"); // identity switch in the same browser
        Assert.False(await AuthenticatedAsync(ownerSession));
        var readerSession = browser.Copy();
        Assert.True(await AuthenticatedAsync(readerSession));

        await SignInAgainAsync(browser, "reader"); // plain re-sign-in rotates the session too
        Assert.False(await AuthenticatedAsync(readerSession));
        Assert.True(await AuthenticatedAsync(browser.Copy()));

        // A dead session presented at sign-in revives nothing: the new session is a different one.
        var relogin = await ReplayAsync(ownerSession, HttpMethod.Post, "/api/v1/session/development-login", new { personaKey = "owner" });
        Assert.True(relogin.IsSuccessStatusCode, await relogin.Content.ReadAsStringAsync(Ct));
        Assert.False(await AuthenticatedAsync(ownerSession));
    }

    [Fact]
    public async Task ParallelApiInstancesObserveTheSameRevocation()
    {
        await RequireSecondaryAsync();

        // Signed in through instance A, signed out through instance B: refused on both.
        using var throughA = await SignInAsync("owner");
        var first = throughA.Copy();
        Assert.True(await AuthenticatedAsync(first, SecondaryApiBaseAddress)); // one key ring, one session registry
        Assert.Equal(HttpStatusCode.NoContent, (await ReplayAsync(first, HttpMethod.Post, "/api/v1/session/logout", baseAddress: SecondaryApiBaseAddress)).StatusCode);
        Assert.False(await AuthenticatedAsync(first));
        Assert.False(await AuthenticatedAsync(first, SecondaryApiBaseAddress));

        // Signed in through B, signed out through A.
        using var throughB = await SignInAsync("reader", SecondaryApiBaseAddress);
        var second = throughB.Copy();
        Assert.True(await AuthenticatedAsync(second));
        Assert.Equal(HttpStatusCode.NoContent, (await ReplayAsync(second, HttpMethod.Post, "/api/v1/session/logout")).StatusCode);
        Assert.False(await AuthenticatedAsync(second, SecondaryApiBaseAddress));
        Assert.Equal(HttpStatusCode.Unauthorized, (await ReplayAsync(second, HttpMethod.Put, "/api/v1/me/ui-preferences", Preference(125.0m), SecondaryApiBaseAddress)).StatusCode);
    }

    [Fact]
    public async Task WhenTheSessionStoreIsUnavailableCookieRequestsFailClosedAndTheSessionSurvives()
    {
        using var browser = await SignInAsync("owner");
        var copy = browser.Copy();
        var before = await PreferenceRevisionAsync(browser);
        fixture.Stack.Pause("authority-db");
        try
        {
            var probe = await ReplayAsync(copy, HttpMethod.Get, "/api/v1/session");
            await AssertUnavailableAsync(probe);
            await AssertUnavailableAsync(await ReplayAsync(copy, HttpMethod.Put, "/api/v1/me/ui-preferences", Preference(125.0m)));
            await AssertUnavailableAsync(await ReplayAsync(copy, HttpMethod.Post, "/api/v1/session/logout"));
        }
        finally
        {
            fixture.Stack.Unpause("authority-db");
        }

        await fixture.Authority.WaitReadyAsync(TimeSpan.FromSeconds(90));
        Assert.True(await AuthenticatedAsync(copy)); // the outage refused requests; it did not end the session
        Assert.Equal(before, await PreferenceRevisionAsync(browser)); // and nothing was written while it was refused
        Assert.Equal(HttpStatusCode.NoContent, (await SignOutAsync(browser)).StatusCode);
        Assert.False(await AuthenticatedAsync(copy));
    }

    [Fact]
    public async Task TheAuthorityEnforcesAbsoluteExpiryAndRevocationIsPermanent()
    {
        var s = new StageBScenario(fixture, "g23-session-expiry");
        await s.BootstrapAsync();
        var digest = Convert.ToBase64String(SHA256.HashData(RandomNumberGenerator.GetBytes(32)));
        var assertion = s.Token(s.Clerk);

        var created = await AuthorityAsync("/internal/v1/sessions", new { organization_id = s.OrganizationId, session_digest_base64 = digest, absolute_expires_on = DateTimeOffset.UtcNow.AddSeconds(4) }, assertion);
        Assert.True(created.IsSuccess, created.Body);
        Assert.True((await ValidateAsync(s, digest, assertion)).Root.GetProperty("valid").GetBoolean());

        await Task.Delay(TimeSpan.FromSeconds(5), Ct);
        var expired = await ValidateAsync(s, digest, assertion);
        Assert.False(expired.Root.GetProperty("valid").GetBoolean());
        Assert.Equal("SESSION_EXPIRED", expired.Root.GetProperty("invalid_reason").GetString());

        // Revoked: never valid again, and the digest cannot be registered a second time to "renew" it.
        var other = Convert.ToBase64String(SHA256.HashData(RandomNumberGenerator.GetBytes(32)));
        Assert.True((await AuthorityAsync("/internal/v1/sessions", new { organization_id = s.OrganizationId, session_digest_base64 = other, absolute_expires_on = DateTimeOffset.UtcNow.AddHours(1) }, assertion)).IsSuccess);
        Assert.True((await AuthorityAsync("/internal/v1/sessions/revoke", new { organization_id = s.OrganizationId, session_digest_base64 = other }, assertion)).IsSuccess);
        Assert.Equal("SESSION_REVOKED", (await ValidateAsync(s, other, assertion)).Root.GetProperty("invalid_reason").GetString());
        var renew = await AuthorityAsync("/internal/v1/sessions", new { organization_id = s.OrganizationId, session_digest_base64 = other, absolute_expires_on = DateTimeOffset.UtcNow.AddHours(1) }, assertion);
        Assert.False(renew.IsSuccess);
        Assert.Equal("SESSION_REVOKED", (await ValidateAsync(s, other, assertion)).Root.GetProperty("invalid_reason").GetString());

        // Another user of the same organization cannot validate or revoke someone else's session.
        Assert.Equal("SESSION_IDENTITY_MISMATCH", (await ValidateAsync(s, digest, s.Token(s.Reader))).Root.GetProperty("invalid_reason").GetString());
    }

    [Fact]
    public async Task SessionStateLivesOnlyInTheAuthorityAsDigests()
    {
        using var browser = await SignInAsync("owner");
        var since = DateTimeOffset.UtcNow.AddMinutes(-2).ToString("O");

        // AuthorityDb holds 32-byte digests and timestamps only (no assertion, no CSRF token, no raw secret column).
        var columns = fixture.Stack.AuthorityObserverSql("SELECT string_agg(column_name, ',' ORDER BY column_name) FROM information_schema.columns WHERE table_schema = 'authority' AND table_name = 'authenticated_sessions';").Trim();
        Assert.Equal("authenticated_session_id,created_by_workload_name,identity_binding_id,organization_id,session_created_on,session_expires_on,session_idle_timeout_seconds,session_last_seen_on,session_revocation_reason,session_revoked_on,session_secret_digest", columns);
        var recent = fixture.Stack.AuthorityObserverSql($"SELECT count(*) FROM authenticated_sessions WHERE session_created_on > '{since}' AND octet_length(session_secret_digest) = 32;").Trim();
        Assert.True(long.Parse(recent, System.Globalization.CultureInfo.InvariantCulture) >= 1);

        // The business database administrator (the hostile DBA of the threat model) has no session state to alter.
        var inBusinessDb = fixture.Stack.AttackerSql("SELECT count(*) FROM pg_class WHERE relname = 'authenticated_sessions';").Trim();
        Assert.Equal("0", inBusinessDb);
        Assert.True(await AuthenticatedAsync(browser.Copy()));
    }

    private static async Task AssertUnavailableAsync(HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync(Ct);
        Assert.True(response.StatusCode == HttpStatusCode.ServiceUnavailable, $"{(int)response.StatusCode}: {text}");
        using var body = JsonDocument.Parse(text);
        Assert.Equal("SESSION_STATE_UNAVAILABLE", body.RootElement.GetProperty("title").GetString());
        Assert.DoesNotContain(response.Headers.TryGetValues("Set-Cookie", out var set) ? set : [], c => c.StartsWith("ef-session", StringComparison.Ordinal));
    }

    private Task<HttpResult> AuthorityAsync(string path, object body, string assertion) =>
        fixture.Authority.SendAsync(HttpMethod.Post, path, TestIdentity.WorkloadToken(), body, assertion);

    private Task<HttpResult> ValidateAsync(StageBScenario s, string digest, string assertion) =>
        AuthorityAsync("/internal/v1/sessions/validate", new { organization_id = s.OrganizationId, session_digest_base64 = digest }, assertion);

    private static async Task RequireSecondaryAsync()
    {
        using var client = new HttpClient { BaseAddress = new Uri(SecondaryApiBaseAddress), Timeout = TimeSpan.FromSeconds(10) };
        try
        {
            Assert.True((await client.GetAsync("/health/live", Ct)).IsSuccessStatusCode, "api-secondary is not healthy");
        }
        catch (HttpRequestException ex)
        {
            throw new InvalidOperationException("The G23 parallel-instance test needs the api-secondary service of the security-test stack (127.0.0.1:5181). Start the whole profile: docker compose -f compose.yaml -f compose.security-test.yaml --env-file .env up -d --build --wait", ex);
        }
    }
}
