using System.Buffers.Text;
using System.Net;
using System.Text;
using System.Text.Json;
using EnterpriseForms.PlatformConsole.Composition;
using EnterpriseForms.PlatformConsole.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EnterpriseForms.PlatformConsole.Tests;

/// <summary>
/// C8 M2 Stage 3 decisions through the console: first and second approval from separate sessions, evidence produced for the
/// signed-in operator only (verified by the Authority's real verifier), no browser-supplied identity or evidence, no signing for a
/// statement the operator did not review, single-use evidence ids, withdrawal and rejection, and Authority refusals (archive outage,
/// rollback detection, credential rejection) passed through without signing.
/// </summary>
public sealed class PlatformConsoleDecisionTests
{
    private const string Justification = "Verified with the requesting owner by phone (OPS-77).";

    private static string RequestPath(Guid requestId, string action = "") => $"/console-api/v1/organizations/{FakeAuthority.Organization}/membership-requests/{requestId}{action}";

    private static object Input(ConsoleFactory factory, Guid requestId, string justification = Justification) =>
        new { expectedStatementSha256 = factory.Authority.Requests[requestId].StatementSha256, justification };

    [Fact]
    public async Task TwoOperatorsInSeparateSessionsApproveAndEachEvidenceIsTheirOwn()
    {
        using var factory = new ConsoleFactory();
        using var avery = factory.Browser();
        using var blair = factory.Browser();
        await avery.SignInAsync(ConsoleFactory.Operators[0].Subject);
        await blair.SignInAsync(ConsoleFactory.Operators[1].Subject);
        var requestId = factory.Authority.NewRequest();

        var (status, first, firstText) = await avery.SendAsync(HttpMethod.Post, RequestPath(requestId, "/approvals"), Input(factory, requestId));
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("AwaitingSecondApproval", first.GetProperty("outcome").GetString());
        Assert.Equal(1, first.GetProperty("approvalsRecorded").GetInt32());

        var (_, statusView, _) = await blair.SendAsync(HttpMethod.Get, RequestPath(requestId, "/approval-status"));
        Assert.Equal(1, statusView.GetProperty("approvalsRecorded").GetInt32());
        Assert.Equal(2, statusView.GetProperty("approvalsRequired").GetInt32());

        // The same operator again (a second session of Avery as well): the Authority refuses; nothing new is recorded.
        using var averyAgain = factory.Browser();
        await averyAgain.SignInAsync(ConsoleFactory.Operators[0].Subject);
        Assert.Equal("OPERATOR_ALREADY_APPROVED", (await averyAgain.SendAsync(HttpMethod.Post, RequestPath(requestId, "/approvals"), Input(factory, requestId))).Body.Code());
        Assert.Equal("OPERATOR_ALREADY_APPROVED", (await avery.SendAsync(HttpMethod.Post, RequestPath(requestId, "/approvals"), Input(factory, requestId))).Body.Code());

        var (status2, second, secondText) = await blair.SendAsync(HttpMethod.Post, RequestPath(requestId, "/approvals"), Input(factory, requestId));
        Assert.Equal(HttpStatusCode.OK, status2);
        Assert.Equal("Approved", second.GetProperty("outcome").GetString());
        Assert.Equal("Effective", second.GetProperty("commandStatus").GetString());

        // Each piece of evidence was signed by its own operator's key, names its own operator, and never reached the browser.
        Assert.Equal([ConsoleFactory.Operators[0].Key, ConsoleFactory.Operators[0].Key, ConsoleFactory.Operators[0].Key, ConsoleFactory.Operators[1].Key], factory.Vault.SignedWith.ToArray());
        var evidence = factory.Authority.Evidence.ToArray();
        Assert.Equal(4, evidence.Length);
        var claims = evidence.Select(Claims).ToArray();
        Assert.Equal(factory.OperatorId(0).ToString("D"), claims[0].GetProperty("platform_operator_id").GetString());
        Assert.Equal(factory.OperatorId(1).ToString("D"), claims[3].GetProperty("platform_operator_id").GetString());
        Assert.Equal(ConsoleFactory.Operators[1].Key, claims[3].GetProperty("sub").GetString());
        Assert.Equal(4, claims.Select(c => c.GetProperty("jti").GetString()).Distinct().Count()); // single-use ids, never reused
        Assert.All(claims, c => Assert.Equal("approve", c.GetProperty("decision").GetString()));
        foreach (var text in new[] { firstText, secondText })
        {
            Assert.DoesNotContain(evidence[0].Split('.')[2], text, StringComparison.Ordinal);
            Assert.DoesNotContain("\"evidence\"", text, StringComparison.Ordinal);
        }

        // Every Authority call named the session's operator (assertion subject), never another.
        var subjects = factory.Authority.Calls.Where(c => c.Path.EndsWith("/approvals", StringComparison.Ordinal)).Select(c => c.Subject).Distinct().ToArray();
        Assert.Equal([ConsoleFactory.Operators[0].Subject, ConsoleFactory.Operators[1].Subject], subjects);
    }

    [Fact]
    public async Task TheBrowserCannotSupplyAnOperatorIdentityEvidenceOrKey()
    {
        using var factory = new ConsoleFactory();
        using var avery = factory.Browser();
        await avery.SignInAsync(ConsoleFactory.Operators[0].Subject);
        var requestId = factory.Authority.NewRequest();
        var sha = factory.Authority.Requests[requestId].StatementSha256;
        var smuggled = new object[]
        {
            new { expectedStatementSha256 = sha, justification = Justification, platformOperatorId = factory.OperatorId(1) },
            new { expectedStatementSha256 = sha, justification = Justification, subjectIdentifier = ConsoleFactory.Operators[1].Subject },
            new { expectedStatementSha256 = sha, justification = Justification, evidence = "eyJhbGciOiJFUzI1NiJ9.e30.AAAA", evidenceKind = "SyntheticDevelopmentJws" },
            new { expectedStatementSha256 = sha, justification = Justification, keyName = ConsoleFactory.Operators[1].Key },
            new { expectedStatementSha256 = sha, justification = Justification, dryRun = false },
        };
        foreach (var body in smuggled)
        {
            var (status, problem, _) = await avery.SendAsync(HttpMethod.Post, RequestPath(requestId, "/approvals"), body);
            Assert.Equal(HttpStatusCode.BadRequest, status);
            Assert.Equal("INVALID_REQUEST", problem.Code());
        }

        Assert.Empty(factory.Vault.SignedWith);
        Assert.Empty(factory.Authority.Evidence);
        Assert.DoesNotContain(factory.Authority.Calls, c => c.Path.EndsWith("/approvals", StringComparison.Ordinal));
    }

    [Fact]
    public async Task NothingIsSignedForAStatementTheOperatorDidNotReviewOrForMalformedInput()
    {
        using var factory = new ConsoleFactory();
        using var avery = factory.Browser();
        await avery.SignInAsync(ConsoleFactory.Operators[0].Subject);
        var requestId = factory.Authority.NewRequest();

        var stale = new { expectedStatementSha256 = new string('c', 64), justification = Justification };
        var (status, problem, _) = await avery.SendAsync(HttpMethod.Post, RequestPath(requestId, "/approvals"), stale);
        Assert.Equal(HttpStatusCode.Conflict, status);
        Assert.Equal("APPROVAL_STATEMENT_CHANGED", problem.Code());

        foreach (var bad in new object[]
        {
            new { expectedStatementSha256 = "not-a-digest", justification = Justification },
            new { expectedStatementSha256 = factory.Authority.Requests[requestId].StatementSha256, justification = "too short" },
            new { expectedStatementSha256 = factory.Authority.Requests[requestId].StatementSha256, justification = new string('x', 501) },
        })
        {
            Assert.Equal("INVALID_REQUEST", (await avery.SendAsync(HttpMethod.Post, RequestPath(requestId, "/approvals"), bad)).Body.Code());
        }

        Assert.Equal("NOT_FOUND", (await avery.SendAsync(HttpMethod.Post, $"/console-api/v1/organizations/{Guid.CreateVersion7()}/membership-requests/{requestId}/approvals", Input(factory, requestId))).Body.Code());
        Assert.Empty(factory.Vault.SignedWith);

        // There is no generic signing route.
        foreach (var path in new[] { "/console-api/v1/sign", "/console-api/v1/evidence", "/console-api/v1/operator/sign", RequestPath(requestId, "/evidence"), RequestPath(requestId, "/approve-as") })
        {
            Assert.Contains((await avery.SendAsync(HttpMethod.Post, path, new { input = "AAAA" })).Status, new[] { HttpStatusCode.NotFound, HttpStatusCode.MethodNotAllowed });
        }

        Assert.Empty(factory.Vault.SignedWith);
    }

    [Fact]
    public async Task ASigningKeyThatIsNotTheSessionOperatorsOwnIsNeverUsed()
    {
        using var factory = new ConsoleFactory();
        using var avery = factory.Browser();
        await avery.SignInAsync(ConsoleFactory.Operators[0].Subject);
        var requestId = factory.Authority.NewRequest();

        // The registry now names Blair's key for Avery (misregistration or tampering): the console refuses and ends the session.
        factory.Authority.Registry[ConsoleFactory.Operators[0].Subject].SyntheticApprovalKeyIdentifier = ConsoleFactory.Operators[1].Key;
        var (status, problem, _) = await avery.SendAsync(HttpMethod.Post, RequestPath(requestId, "/approvals"), Input(factory, requestId));
        Assert.Equal(HttpStatusCode.Forbidden, status);
        Assert.Equal("OPERATOR_SIGNING_KEY_MISMATCH", problem.Code());
        Assert.Empty(factory.Vault.SignedWith);
        Assert.Equal(HttpStatusCode.Unauthorized, (await avery.SendAsync(HttpMethod.Get, "/console-api/v1/operator/me")).Status);

        // The signer itself refuses any key that is not a configured operator key.
        using var scope = factory.Services.CreateScope();
        var signer = scope.ServiceProvider.GetRequiredService<IOperatorEvidenceSigner>();
        foreach (var key in new[] { "org-0199aaaa0000700080000000000000aa-row-integrity-v1", "devstack-persistence-probe", "../../sys/raw", "unit-operator-key-dana" })
        {
            var refused = await Assert.ThrowsAsync<ConsoleException>(() => signer.SignAsync(key, Encoding.ASCII.GetBytes("a.b"), TestContext.Current.CancellationToken));
            Assert.Equal("OPERATOR_SIGNING_KEY_MISMATCH", refused.ErrorCode);
        }

        // And Vault's own policy refuses a configured key the token may not use (defence in depth).
        factory.Vault.TokenMaySign.Remove(ConsoleFactory.Operators[2].Key);
        var denied = await Assert.ThrowsAsync<ConsoleException>(() => signer.SignAsync(ConsoleFactory.Operators[2].Key, Encoding.ASCII.GetBytes("a.b"), TestContext.Current.CancellationToken));
        Assert.Equal("OPERATOR_SIGNING_UNAVAILABLE", denied.ErrorCode);
        Assert.DoesNotContain("permission denied", denied.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WithdrawAndRejectAreTheSessionOperatorsOwnDecisions()
    {
        using var factory = new ConsoleFactory();
        using var avery = factory.Browser();
        using var blair = factory.Browser();
        await avery.SignInAsync(ConsoleFactory.Operators[0].Subject);
        await blair.SignInAsync(ConsoleFactory.Operators[1].Subject);
        var requestId = factory.Authority.NewRequest();

        Assert.Equal("AwaitingSecondApproval", (await avery.SendAsync(HttpMethod.Post, RequestPath(requestId, "/approvals"), Input(factory, requestId))).Body.GetProperty("outcome").GetString());
        Assert.Equal("NO_PENDING_APPROVAL", (await blair.SendAsync(HttpMethod.Post, RequestPath(requestId, "/approvals/withdraw"), Input(factory, requestId))).Body.Code()); // not Blair's approval
        Assert.Equal("Withdrawn", (await avery.SendAsync(HttpMethod.Post, RequestPath(requestId, "/approvals/withdraw"), Input(factory, requestId, "Approved the wrong request by mistake."))).Body.GetProperty("outcome").GetString());
        Assert.Empty(factory.Authority.Requests[requestId].Approvals);

        var (status, rejected, _) = await blair.SendAsync(HttpMethod.Post, RequestPath(requestId, "/reject"), Input(factory, requestId, "The owner could not confirm this person."));
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("Rejected", rejected.GetProperty("outcome").GetString());
        Assert.Equal("MEMBERSHIP_REQUEST_NOT_PENDING", (await avery.SendAsync(HttpMethod.Post, RequestPath(requestId, "/approvals"), Input(factory, requestId))).Body.Code());

        var decisions = factory.Authority.Evidence.Select(Claims).Select(c => (c.GetProperty("decision").GetString(), c.GetProperty("sub").GetString())).ToArray();
        Assert.Equal(("approve", ConsoleFactory.Operators[0].Key), decisions[0]);
        Assert.Equal(("withdraw", ConsoleFactory.Operators[1].Key), decisions[1]);
        Assert.Equal(("withdraw", ConsoleFactory.Operators[0].Key), decisions[2]);
        Assert.Equal(("reject", ConsoleFactory.Operators[1].Key), decisions[3]);
    }

    [Theory]
    [InlineData(HttpStatusCode.ServiceUnavailable, "PLATFORM_JOURNAL_UNVERIFIABLE", true)]
    [InlineData((HttpStatusCode)423, "PLATFORM_JOURNAL_BEHIND_ARCHIVE", false)]
    [InlineData((HttpStatusCode)423, "PLATFORM_JOURNAL_QUARANTINED", false)]
    [InlineData(HttpStatusCode.ServiceUnavailable, "PLATFORM_JOURNAL_ARCHIVE_PENDING", true)]
    public async Task ArchiveOutageAndRollbackRefusalsPassThroughWithoutSigningAndKeepTheSession(HttpStatusCode status, string code, bool retryable)
    {
        using var factory = new ConsoleFactory();
        using var avery = factory.Browser();
        await avery.SignInAsync(ConsoleFactory.Operators[0].Subject);
        var requestId = factory.Authority.NewRequest();
        factory.Authority.Failure = (status, code, retryable);

        foreach (var (method, path) in new[] { (HttpMethod.Post, RequestPath(requestId, "/approvals")), (HttpMethod.Get, RequestPath(requestId)), (HttpMethod.Get, "/console-api/v1/membership-requests") })
        {
            var (actual, problem, _) = await avery.SendAsync(method, path, method == HttpMethod.Post ? Input(factory, requestId) : null);
            Assert.Equal(status, actual);
            Assert.Equal(code, problem.Code());
            Assert.Equal(retryable, problem.GetProperty("retryable").GetBoolean());
        }

        Assert.Empty(factory.Vault.SignedWith);
        factory.Authority.Failure = null;
        Assert.Equal(HttpStatusCode.OK, (await avery.SendAsync(HttpMethod.Get, "/console-api/v1/operator/me")).Status); // the session survives an outage
    }

    [Fact]
    public async Task ARejectedConsoleCredentialIsADeploymentFaultNotASignOut()
    {
        using var factory = new ConsoleFactory();
        using var avery = factory.Browser();
        await avery.SignInAsync(ConsoleFactory.Operators[0].Subject);
        factory.Authority.Failure = (HttpStatusCode.Unauthorized, "UNAUTHORIZED", false);
        var (status, problem, _) = await avery.SendAsync(HttpMethod.Get, "/console-api/v1/membership-requests");
        Assert.Equal(HttpStatusCode.BadGateway, status);
        Assert.Equal("PLATFORM_CONSOLE_CREDENTIAL_REJECTED", problem.Code());
        factory.Authority.Failure = null;
        Assert.Equal(HttpStatusCode.OK, (await avery.SendAsync(HttpMethod.Get, "/console-api/v1/membership-requests")).Status);
    }

    [Fact]
    public async Task VaultOutageFailsClosedWithoutSubmittingADecision()
    {
        using var factory = new ConsoleFactory();
        using var avery = factory.Browser();
        await avery.SignInAsync(ConsoleFactory.Operators[0].Subject);
        var requestId = factory.Authority.NewRequest();
        factory.Vault.Unavailable = true;
        var (status, problem, _) = await avery.SendAsync(HttpMethod.Post, RequestPath(requestId, "/approvals"), Input(factory, requestId));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, status);
        Assert.Equal("OPERATOR_SIGNING_UNAVAILABLE", problem.Code());
        Assert.Empty(factory.Authority.Evidence);
        Assert.Empty(factory.Authority.Requests[requestId].Approvals);
    }

    [Fact]
    public async Task TheConsoleExposesNoBusinessOrOrganizationRecordRoutes()
    {
        using var factory = new ConsoleFactory();
        using var avery = factory.Browser();
        await avery.SignInAsync(ConsoleFactory.Operators[0].Subject);
        foreach (var path in new[] { "/api/v1/form-records", "/api/v1/organization/members", "/api/v1/session", "/console-api/v1/form-records", "/console-api/v1/organizations/" + FakeAuthority.Organization + "/members", "/console-api/v1/documents" })
        {
            Assert.Equal(HttpStatusCode.NotFound, (await avery.SendAsync(HttpMethod.Get, path)).Status);
        }

        Assert.All(factory.Authority.Calls, c => Assert.StartsWith("/internal/v1/platform/", c.Path, StringComparison.Ordinal));
    }

    [Fact]
    public void StartupRefusesOtherEnvironmentsMissingOrSharedCredentialsAndWeakenedSessions()
    {
        var valid = Options();
        PlatformConsoleOptionsValidation.Validate(valid, "Development");
        PlatformConsoleOptionsValidation.Validate(valid, "Testing");
        foreach (var environment in new[] { "Production", "Staging", "Prod" })
        {
            Assert.Throws<InvalidOperationException>(() => PlatformConsoleOptionsValidation.Validate(valid, environment));
        }

        var cases = new Action<PlatformConsoleOptions>[]
        {
            o => o.Mode = "Entra",
            o => o.Vault.Token = null,
            o => o.Authority.OperatorAssertion.SharedKeyBase64 = o.Authority.ConsoleCredential.SharedKeyBase64,
            o => o.Authority.OperatorAssertion.Issuer = o.Authority.ConsoleCredential.Issuer,
            o => o.Authority.ConsoleCredential.SharedKeyBase64 = Convert.ToBase64String(new byte[16]),
            o => o.Session.IdleTimeoutMinutes = 16,
            o => o.Session.AbsoluteLifetimeMinutes = 121,
            o => o.Operators[1].VaultKeyName = o.Operators[0].VaultKeyName,
            o => o.Operators[1].SubjectIdentifier = "someone@example.com",
            o => o.AllowedOrigins.Clear(),
            o => o.AllowedOrigins[0] = "https://localhost/path",
        };
        foreach (var change in cases)
        {
            var options = Options();
            change(options);
            Assert.Throws<InvalidOperationException>(() => PlatformConsoleOptionsValidation.Validate(options, "Testing"));
        }

        using var production = new ConsoleFactory { Environment = "Production" };
        Assert.ThrowsAny<Exception>(() => production.CreateClient());
    }

    private static PlatformConsoleOptions Options()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(ConsoleFactory.BaseSettings()).Build();
        return configuration.GetSection(PlatformConsoleOptions.SectionName).Get<PlatformConsoleOptions>()!;
    }

    private static JsonElement Claims(string jws) => JsonDocument.Parse(Base64Url.DecodeFromChars(jws.Split('.')[1])).RootElement.Clone();
}
