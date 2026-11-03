using System.Net;
using Xunit;

namespace EnterpriseForms.PlatformConsole.Tests;

/// <summary>
/// C8 M2 Stage 3 (PO-T25 and the Stage 3 session checklist): development sign-in bound to one registered operator, server-side idle
/// and absolute expiry, sign-out invalidation, session rotation, CSRF and origin checks, cookie attributes, unauthorized access, and
/// the end of a session when the Authority reports the operator inactive.
/// </summary>
public sealed class PlatformConsoleSessionTests
{
    private const string Session = "/console-api/v1/session";
    private const string Me = "/console-api/v1/operator/me";

    [Fact]
    public async Task SignInBindsTheSessionToTheSelectedRegisteredOperatorWithSecureHttpOnlyStrictCookie()
    {
        using var factory = new ConsoleFactory();
        using var browser = factory.Browser();
        var signedIn = await browser.SignInAsync(ConsoleFactory.Operators[0].Subject);

        Assert.Equal(ConsoleFactory.Operators[0].Subject, signedIn.GetProperty("subjectIdentifier").GetString());
        Assert.Equal(FakeAuthority.AssertionIssuer, signedIn.GetProperty("issuerUri").GetString());
        Assert.Equal(factory.OperatorId(0), signedIn.GetProperty("platformOperatorId").GetGuid());
        Assert.StartsWith("SyntheticDevelopment", signedIn.GetProperty("authenticationMode").GetString(), StringComparison.Ordinal);
        Assert.Contains("not two-person evidence", signedIn.GetProperty("environmentNotice").GetString(), StringComparison.Ordinal);
        Assert.Equal(4, signedIn.GetProperty("permissions").GetArrayLength());
        var start = factory.Clock.GetUtcNow();
        Assert.Equal(start.AddMinutes(15), signedIn.GetProperty("idleExpiresOn").GetDateTimeOffset());
        Assert.Equal(start.AddHours(2), signedIn.GetProperty("absoluteExpiresOn").GetDateTimeOffset());

        var cookie = browser.LastSetCookie!.ToLowerInvariant();
        Assert.StartsWith("ef-platform-console=", cookie, StringComparison.Ordinal);
        Assert.Contains("httponly", cookie, StringComparison.Ordinal);
        Assert.Contains("secure", cookie, StringComparison.Ordinal);
        Assert.Contains("samesite=strict", cookie, StringComparison.Ordinal);
        Assert.Contains("path=/console-api", cookie, StringComparison.Ordinal);
        Assert.DoesNotContain("expires", cookie, StringComparison.Ordinal);

        // The Authority was asked about exactly that identity, on the platform plane only.
        Assert.All(factory.Authority.Calls, c => Assert.StartsWith("/internal/v1/platform/", c.Path, StringComparison.Ordinal));
        Assert.Contains(factory.Authority.Calls, c => c.Path == "/internal/v1/platform/operator/me" && c.Subject == ConsoleFactory.Operators[0].Subject);

        var (status, me, _) = await browser.SendAsync(HttpMethod.Get, Me);
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(ConsoleFactory.Operators[0].Subject, me.GetProperty("operator").GetProperty("subjectIdentifier").GetString());
    }

    [Fact]
    public async Task SignInIsRefusedForUnknownInactiveOrMiskeyedOperatorsAndIssuesNoSession()
    {
        using var factory = new ConsoleFactory();
        using var browser = factory.Browser();

        var (status, body, _) = await browser.SendAsync(HttpMethod.Post, Session + "/development-login", new { subjectIdentifier = "dev-owner" });
        Assert.Equal(HttpStatusCode.Forbidden, status);
        Assert.Equal("UNKNOWN_SYNTHETIC_OPERATOR", body.Code());
        Assert.True(browser.LastSetCookie is null || !browser.LastSetCookie.Contains("ef-platform-console=e", StringComparison.Ordinal));

        factory.Authority.Registry[ConsoleFactory.Operators[1].Subject].OperatorStatusName = "Suspended";
        (status, body, _) = await browser.SendAsync(HttpMethod.Post, Session + "/development-login", new { subjectIdentifier = ConsoleFactory.Operators[1].Subject });
        Assert.Equal(HttpStatusCode.Forbidden, status);
        Assert.Equal("PLATFORM_OPERATOR_NOT_ACTIVE", body.Code());

        // The registry names another operator's key for this identity: never signed in, never signed with.
        factory.Authority.Registry[ConsoleFactory.Operators[2].Subject].SyntheticApprovalKeyIdentifier = ConsoleFactory.Operators[0].Key;
        (status, body, _) = await browser.SendAsync(HttpMethod.Post, Session + "/development-login", new { subjectIdentifier = ConsoleFactory.Operators[2].Subject });
        Assert.Equal(HttpStatusCode.Forbidden, status);
        Assert.Equal("OPERATOR_SIGNING_KEY_MISMATCH", body.Code());

        (status, body, _) = await browser.SendAsync(HttpMethod.Get, Me);
        Assert.Equal(HttpStatusCode.Unauthorized, status);
        Assert.Equal("SESSION_REQUIRED", body.Code());
    }

    [Fact]
    public async Task UnauthenticatedAndForgedCookiesGetNoOperatorData()
    {
        using var factory = new ConsoleFactory();
        using var browser = factory.Browser();
        foreach (var path in new[] { Me, Session, "/console-api/v1/membership-requests", $"/console-api/v1/organizations/{FakeAuthority.Organization}/membership-requests/{factory.Authority.NewRequest()}" })
        {
            browser.Cookie = null;
            Assert.Equal(HttpStatusCode.Unauthorized, (await browser.SendAsync(HttpMethod.Get, path)).Status);
            browser.Cookie = "ef-platform-console=" + new string('A', 43);
            Assert.Equal(HttpStatusCode.Unauthorized, (await browser.SendAsync(HttpMethod.Get, path)).Status);
            browser.Cookie = "ef-session=business-cookie-value"; // the business application's cookie is not a console session
            Assert.Equal(HttpStatusCode.Unauthorized, (await browser.SendAsync(HttpMethod.Get, path)).Status);
        }

        Assert.DoesNotContain(factory.Authority.Calls, c => c.Path != "/internal/v1/platform/operator/me");
        Assert.Empty(factory.Authority.Calls);
    }

    [Fact]
    public async Task IdleTimeoutEndsTheSessionAfterFifteenMinutesWithoutActivity()
    {
        using var factory = new ConsoleFactory();
        using var browser = factory.Browser();
        await browser.SignInAsync(ConsoleFactory.Operators[0].Subject);

        factory.Clock.Advance(TimeSpan.FromMinutes(14));
        Assert.Equal(HttpStatusCode.OK, (await browser.SendAsync(HttpMethod.Get, Me)).Status); // activity resets the idle clock
        factory.Clock.Advance(TimeSpan.FromMinutes(14));
        Assert.Equal(HttpStatusCode.OK, (await browser.SendAsync(HttpMethod.Get, Me)).Status);
        factory.Clock.Advance(TimeSpan.FromMinutes(15));
        var (status, body, _) = await browser.SendAsync(HttpMethod.Get, Me);
        Assert.Equal(HttpStatusCode.Unauthorized, status);
        Assert.Equal("SESSION_REQUIRED", body.Code());
        Assert.Contains("ef-platform-console=;", browser.LastSetCookie!, StringComparison.Ordinal); // the stale cookie is deleted
        factory.Clock.Advance(TimeSpan.FromMinutes(-10));
        Assert.Equal(HttpStatusCode.Unauthorized, (await browser.SendAsync(HttpMethod.Get, Me)).Status); // removed server-side, not just expired
    }

    [Fact]
    public async Task AbsoluteLifetimeEndsTheSessionAfterTwoHoursEvenWhenActive()
    {
        using var factory = new ConsoleFactory();
        using var browser = factory.Browser();
        await browser.SignInAsync(ConsoleFactory.Operators[0].Subject);
        for (var minute = 10; minute < 120; minute += 10)
        {
            factory.Clock.Advance(TimeSpan.FromMinutes(10));
            Assert.Equal(HttpStatusCode.OK, (await browser.SendAsync(HttpMethod.Get, Me)).Status);
        }

        factory.Clock.Advance(TimeSpan.FromMinutes(10)); // 120 minutes after sign-in
        Assert.Equal(HttpStatusCode.Unauthorized, (await browser.SendAsync(HttpMethod.Get, Me)).Status);
    }

    [Fact]
    public async Task SignOutInvalidatesTheSessionOnTheServerSoACopiedCookieStopsWorking()
    {
        using var factory = new ConsoleFactory();
        using var browser = factory.Browser();
        using var copy = factory.Browser();
        await browser.SignInAsync(ConsoleFactory.Operators[0].Subject);
        copy.Cookie = browser.Cookie;
        copy.Csrf = browser.Csrf;
        Assert.Equal(HttpStatusCode.OK, (await copy.SendAsync(HttpMethod.Get, Me)).Status);

        // Sign-out needs the CSRF token too.
        Assert.Equal("CSRF_TOKEN_INVALID", (await browser.SendAsync(HttpMethod.Post, Session + "/sign-out", csrf: "wrong")).Body.Code());
        var (status, _, _) = await browser.SendAsync(HttpMethod.Post, Session + "/sign-out");
        Assert.Equal(HttpStatusCode.NoContent, status);
        Assert.Contains("ef-platform-console=;", browser.LastSetCookie!, StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.Unauthorized, (await copy.SendAsync(HttpMethod.Get, Me)).Status);
        Assert.Equal(HttpStatusCode.Unauthorized, (await browser.SendAsync(HttpMethod.Get, Session)).Status);
    }

    [Fact]
    public async Task ANewSignInEndsThePresentedSession()
    {
        using var factory = new ConsoleFactory();
        using var browser = factory.Browser();
        await browser.SignInAsync(ConsoleFactory.Operators[0].Subject);
        var first = browser.Cookie;
        await browser.SignInAsync(ConsoleFactory.Operators[1].Subject); // presents the first cookie
        Assert.NotEqual(first, browser.Cookie);

        using var replay = factory.Browser();
        replay.Cookie = first;
        Assert.Equal(HttpStatusCode.Unauthorized, (await replay.SendAsync(HttpMethod.Get, Me)).Status);
        var (_, me, _) = await browser.SendAsync(HttpMethod.Get, Me);
        Assert.Equal(ConsoleFactory.Operators[1].Subject, me.GetProperty("operator").GetProperty("subjectIdentifier").GetString());
    }

    [Fact]
    public async Task StateChangingRequestsNeedTheSessionCsrfTokenAndAnAllowedOrigin()
    {
        using var factory = new ConsoleFactory();
        using var avery = factory.Browser();
        using var blair = factory.Browser();
        await avery.SignInAsync(ConsoleFactory.Operators[0].Subject);
        await blair.SignInAsync(ConsoleFactory.Operators[1].Subject);
        var requestId = factory.Authority.NewRequest();
        var path = $"/console-api/v1/organizations/{FakeAuthority.Organization}/membership-requests/{requestId}/approvals";
        var input = new { expectedStatementSha256 = factory.Authority.Requests[requestId].StatementSha256, justification = "Verified with the requesting owner by phone." };

        avery.Csrf = null;
        Assert.Equal("CSRF_TOKEN_INVALID", (await avery.SendAsync(HttpMethod.Post, path, input)).Body.Code());
        Assert.Equal("CSRF_TOKEN_INVALID", (await avery.SendAsync(HttpMethod.Post, path, input, csrf: blair.Csrf)).Body.Code()); // another session's token
        Assert.Equal("CSRF_TOKEN_INVALID", (await avery.SendAsync(HttpMethod.Post, path, input, csrf: "x")).Body.Code());

        var (_, session, _) = await avery.SendAsync(HttpMethod.Get, Session);
        avery.Csrf = session.GetProperty("csrfToken").GetString(); // a reloaded page gets its token from the session
        avery.Origin = "http://127.0.0.1:8091"; // the business application's origin
        Assert.Equal("ORIGIN_NOT_ALLOWED", (await avery.SendAsync(HttpMethod.Post, path, input)).Body.Code());
        Assert.Equal("ORIGIN_NOT_ALLOWED", (await avery.SendAsync(HttpMethod.Get, Me)).Body.Code());
        avery.Origin = null;
        Assert.Equal("ORIGIN_NOT_ALLOWED", (await avery.SendAsync(HttpMethod.Post, path, input)).Body.Code());
        avery.Origin = ConsoleFactory.Origin;
        Assert.Equal("ORIGIN_NOT_ALLOWED", (await avery.SendAsync(HttpMethod.Get, Me, tweak: r => r.Headers.Add("Sec-Fetch-Site", "cross-site"))).Body.Code());

        using var anonymous = factory.Browser();
        anonymous.Origin = "https://evil.example";
        Assert.Equal("ORIGIN_NOT_ALLOWED", (await anonymous.SendAsync(HttpMethod.Post, Session + "/development-login", new { subjectIdentifier = ConsoleFactory.Operators[0].Subject })).Body.Code());
        Assert.Empty(factory.Vault.SignedWith); // nothing was signed by any refused request

        var (status, ok, _) = await avery.SendAsync(HttpMethod.Post, path, input);
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("AwaitingSecondApproval", ok.GetProperty("outcome").GetString());
    }

    [Fact]
    public async Task AnOperatorSuspendedDuringTheSessionLosesEverySessionAtTheNextCall()
    {
        using var factory = new ConsoleFactory();
        using var first = factory.Browser();
        using var second = factory.Browser();
        await first.SignInAsync(ConsoleFactory.Operators[0].Subject);
        await second.SignInAsync(ConsoleFactory.Operators[0].Subject);

        factory.Authority.Registry[ConsoleFactory.Operators[0].Subject].OperatorStatusName = "Suspended";
        var (status, body, _) = await first.SendAsync(HttpMethod.Get, Session);
        Assert.Equal(HttpStatusCode.Forbidden, status);
        Assert.Equal("PLATFORM_OPERATOR_NOT_ACTIVE", body.Code());

        factory.Authority.Registry[ConsoleFactory.Operators[0].Subject].OperatorStatusName = "Active"; // even if reinstated later
        Assert.Equal(HttpStatusCode.Unauthorized, (await first.SendAsync(HttpMethod.Get, Me)).Status);
        Assert.Equal(HttpStatusCode.Unauthorized, (await second.SendAsync(HttpMethod.Get, Me)).Status);
    }

    [Fact]
    public async Task ResponsesAreNotCachedNotFramedAndCarryNoTokens()
    {
        using var factory = new ConsoleFactory();
        using var browser = factory.Browser();
        var (_, body, text) = await browser.SendAsync(HttpMethod.Get, "/console-api/v1/session/development-operators");
        Assert.Equal(3, body.GetProperty("operators").GetArrayLength());
        Assert.DoesNotContain(FakeVault.Token, text, StringComparison.Ordinal);
        Assert.DoesNotContain("unit-operator-key", text, StringComparison.Ordinal); // key names are not offered to the browser

        using var raw = factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions { BaseAddress = new Uri(ConsoleFactory.Origin) });
        using var response = await raw.GetAsync(new Uri("/console-api/health/live", UriKind.Relative), TestContext.Current.CancellationToken);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        Assert.Equal("DENY", response.Headers.GetValues("X-Frame-Options").Single());
        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.False(response.Headers.Contains("Access-Control-Allow-Origin"));
    }
}
