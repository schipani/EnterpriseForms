using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using EnterpriseForms.SecurityTests.Harness;
using EnterpriseForms.StageBTests.Harness;
using Xunit;

namespace EnterpriseForms.StageBTests.Tests;

/// <summary>
/// C8 M2 Stage 3 on the real stack: the separately deployed Platform Console BFF (port 5192) with its own credentials, the real
/// Authority platform plane, both PostgreSQL databases, SpiceDB, the archive and Vault Transit holding one non-exportable ES256 key
/// per synthetic console operator. Two operators approve in separate console sessions; evidence is signed in Vault with each
/// operator's own key and verified by the Authority; the console token cannot use any other Vault key; archive outage and a
/// simulated rollback fail closed through the console; a suspended operator's session ends; console and business sessions are
/// disjoint. Synthetic operators only: never two-human approval evidence.
/// </summary>
[Collection(StageBStackCollectionDefinition.Name)]
public sealed class PlatformConsoleStackTests(StageBFixture fixture)
{
    private const string Justification = "Verified with the requesting owner by phone (OPS-88).";
    private static readonly string[] ConsoleSubjects = ["sectest-console-operator-dana", "sectest-console-operator-ellis", "sectest-console-operator-frankie"];
    private static readonly string[] ConsoleKeys = ["sectest-console-key-dana", "sectest-console-key-ellis", "sectest-console-key-frankie"];

    private string Sql(string sql) => fixture.Stack.AuthorityObserverSql(sql).Trim();

    private int Bindings(Guid requestId) => int.Parse(Sql($"SELECT count(*) FROM identity_bindings WHERE organization_membership_request_id = '{requestId:D}';"), CultureInfo.InvariantCulture);

    private static string RequestPath(StageBScenario s, Guid requestId, string action = "") => $"/console-api/v1/organizations/{s.OrganizationId:D}/membership-requests/{requestId:D}{action}";

    private async Task<Guid> RequestAsync(StageBScenario s, string hint)
    {
        var created = await fixture.Api.PostAsync("/api/v1/organization/membership-requests", s.Token(s.Designer),
            new { subjectIdentifier = "pc-person-" + Guid.NewGuid().ToString("N")[..10], displayNameHint = hint, initialRoleName = "Designer", reason = "joins the intake team", useDevelopmentStepUp = true });
        Assert.True(created.IsSuccess, created.Body);
        return created.Root.GetProperty("requestId").GetGuid();
    }

    private static async Task<string> StatementAsync(ConsoleSession session, StageBScenario s, Guid requestId)
    {
        var detail = await session.SendAsync(HttpMethod.Get, RequestPath(s, requestId));
        Assert.True(detail.Status == HttpStatusCode.OK, detail.Text);
        return detail.Body.GetProperty("statementSha256").GetString()!;
    }

    private static async Task<ConsoleResult> DecideAsync(ConsoleSession session, StageBScenario s, Guid requestId, string action = "/approvals", string justification = Justification, string? sha = null) =>
        await session.SendAsync(HttpMethod.Post, RequestPath(s, requestId, action), new { expectedStatementSha256 = sha ?? await StatementAsync(session, s, requestId), justification });

    [Fact]
    public async Task TwoConsoleOperatorsApproveInSeparateSessionsWithTheirOwnVaultKeysAndTheMembershipIsCreated()
    {
        var ids = await PlatformConsoleHarness.EnsureConsoleOperatorsAsync(fixture);
        var s = new StageBScenario(fixture, "pc-two-operators");
        await s.BootstrapAsync();
        var hint = "Console Newcomer " + Guid.NewGuid().ToString("N")[..6];
        var requestId = await RequestAsync(s, hint);
        using var dana = await ConsoleSession.SignInAsync(ConsoleSubjects[0]);
        using var ellis = await ConsoleSession.SignInAsync(ConsoleSubjects[1]);
        Assert.Equal(ids[0], dana.PlatformOperatorId);
        Assert.Equal(ids[1], ellis.PlatformOperatorId);

        var queue = await dana.SendAsync(HttpMethod.Get, "/console-api/v1/membership-requests?q=" + Uri.EscapeDataString(hint) + "&status=needs_decision");
        Assert.True(queue.Status == HttpStatusCode.OK, queue.Text);
        var item = Assert.Single(queue.Body.GetProperty("items").EnumerateArray());
        Assert.Equal(requestId, item.GetProperty("requestId").GetGuid());
        Assert.Equal(0, item.GetProperty("approvalStatus").GetProperty("recorded").GetInt32());

        var viewsBefore = int.Parse(Sql($"SELECT count(*) FROM security_journal_entries WHERE organization_id = '{s.OrganizationId:D}' AND journal_stream_name = 'audit' AND journal_event_kind = 'PlatformMembershipRequestViewed';"), CultureInfo.InvariantCulture);
        var first = await DecideAsync(dana, s, requestId);
        Assert.True(first.Status == HttpStatusCode.OK, first.Text);
        Assert.Equal("AwaitingSecondApproval", first.Body.GetProperty("outcome").GetString());
        Assert.Equal(0, Bindings(requestId));
        var status = await ellis.SendAsync(HttpMethod.Get, RequestPath(s, requestId, "/approval-status"));
        Assert.Equal(1, status.Body.GetProperty("approvalsRecorded").GetInt32());
        Assert.True(status.Body.GetProperty("canApprove").GetBoolean());

        // The same operator again, also from a second console session: refused by the Authority, still one of two.
        using var danaAgain = await ConsoleSession.SignInAsync(ConsoleSubjects[0]);
        Assert.Equal("OPERATOR_ALREADY_APPROVED", (await DecideAsync(danaAgain, s, requestId)).Code);
        Assert.Equal(0, Bindings(requestId));

        var second = await DecideAsync(ellis, s, requestId);
        Assert.True(second.Status == HttpStatusCode.OK, second.Text);
        Assert.Equal("Approved", second.Body.GetProperty("outcome").GetString());
        Assert.Equal(1, Bindings(requestId));
        Assert.Equal($"{ids[0]:D}|{ids[1]:D}", Sql($"SELECT string_agg(platform_operator_id::text, '|' ORDER BY approval_recorded_on) FROM platform_operator_approvals WHERE organization_membership_request_id = '{requestId:D}' AND approval_state_name = 'Consumed';"));
        Assert.Equal(ids[1].ToString("D"), Sql($"SELECT request_decided_by_platform_operator_id FROM organization_membership_requests WHERE organization_membership_request_id = '{requestId:D}';"));
        Assert.Equal("MEMBERSHIP_REQUEST_NOT_PENDING", (await DecideAsync(dana, s, requestId)).Code);

        // PF-8 (C-99): each detail view is audited on the organization's audit stream, never as a platform command-journal event.
        var viewsAfter = int.Parse(Sql($"SELECT count(*) FROM security_journal_entries WHERE organization_id = '{s.OrganizationId:D}' AND journal_stream_name = 'audit' AND journal_event_kind = 'PlatformMembershipRequestViewed';"), CultureInfo.InvariantCulture);
        Assert.True(viewsAfter >= viewsBefore + 4, $"views before {viewsBefore}, after {viewsAfter}");
        Assert.Equal("0", Sql("SELECT count(*) FROM platform_journal_entries WHERE journal_event_kind = 'PlatformMembershipRequestViewed' AND journal_event_body_json LIKE '%" + requestId.ToString("D") + "%';"));
        Assert.Contains(ConsoleKeys[0], Sql($"SELECT string_agg(journal_event_body_json, ' ') FROM platform_journal_entries WHERE journal_event_kind = 'PlatformMembershipApprovalRecorded' AND journal_event_body_json LIKE '%{requestId:D}%';"), StringComparison.Ordinal);
        Assert.Contains(ConsoleKeys[1], Sql($"SELECT string_agg(journal_event_body_json, ' ') FROM platform_journal_entries WHERE journal_event_kind = 'PlatformMembershipApprovalCompleted' AND journal_event_body_json LIKE '%{requestId:D}%';"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task WithdrawAndRejectThroughTheConsoleAreEachOperatorsOwnDecision()
    {
        await PlatformConsoleHarness.EnsureConsoleOperatorsAsync(fixture);
        var s = new StageBScenario(fixture, "pc-withdraw-reject");
        await s.BootstrapAsync();
        var requestId = await RequestAsync(s, "Console Withdraw " + Guid.NewGuid().ToString("N")[..6]);
        using var dana = await ConsoleSession.SignInAsync(ConsoleSubjects[0]);
        using var ellis = await ConsoleSession.SignInAsync(ConsoleSubjects[1]);

        Assert.Equal("AwaitingSecondApproval", (await DecideAsync(dana, s, requestId)).Body.GetProperty("outcome").GetString());
        Assert.Equal("NO_PENDING_APPROVAL", (await DecideAsync(ellis, s, requestId, "/approvals/withdraw")).Code);
        var withdrawn = await DecideAsync(dana, s, requestId, "/approvals/withdraw", "Approved the wrong request by mistake.");
        Assert.True(withdrawn.Status == HttpStatusCode.OK, withdrawn.Text);
        Assert.Equal("Withdrawn", Sql($"SELECT approval_state_name FROM platform_operator_approvals WHERE organization_membership_request_id = '{requestId:D}';"));

        var rejected = await DecideAsync(ellis, s, requestId, "/reject", "The owner could not confirm this person.");
        Assert.True(rejected.Status == HttpStatusCode.OK, rejected.Text);
        Assert.Equal("Rejected", Sql($"SELECT request_state_name FROM organization_membership_requests WHERE organization_membership_request_id = '{requestId:D}';"));
        Assert.Equal(0, Bindings(requestId));
        Assert.Equal("MEMBERSHIP_REQUEST_NOT_PENDING", (await DecideAsync(dana, s, requestId)).Code);
    }

    [Fact]
    public async Task TheConsoleSignsNothingArbitraryAndItsVaultTokenReachesOnlyTheOperatorSignPaths()
    {
        await PlatformConsoleHarness.EnsureConsoleOperatorsAsync(fixture);
        var s = new StageBScenario(fixture, "pc-signing");
        await s.BootstrapAsync();
        var requestId = await RequestAsync(s, "Console Signing " + Guid.NewGuid().ToString("N")[..6]);
        using var dana = await ConsoleSession.SignInAsync(ConsoleSubjects[0]);
        var sha = await StatementAsync(dana, s, requestId);
        var usesBefore = Sql("SELECT count(*) FROM platform_operator_evidence_uses;");

        // Browser-supplied identity, evidence or key: refused before anything is signed.
        foreach (var body in new object[]
        {
            new { expectedStatementSha256 = sha, justification = Justification, platformOperatorId = Guid.CreateVersion7() },
            new { expectedStatementSha256 = sha, justification = Justification, evidence = "e30.e30.AAAA", evidenceKind = "SyntheticDevelopmentJws" },
            new { expectedStatementSha256 = sha, justification = Justification, subjectIdentifier = ConsoleSubjects[1] },
        })
        {
            Assert.Equal("INVALID_REQUEST", (await dana.SendAsync(HttpMethod.Post, RequestPath(s, requestId, "/approvals"), body)).Code);
        }

        Assert.Equal("APPROVAL_STATEMENT_CHANGED", (await DecideAsync(dana, s, requestId, sha: new string('d', 64))).Code); // a statement not reviewed is never signed
        foreach (var path in new[] { "/console-api/v1/sign", "/console-api/v1/evidence", RequestPath(s, requestId, "/evidence") })
        {
            Assert.Contains((await dana.SendAsync(HttpMethod.Post, path, new { input = "AAAA" })).Status, new[] { HttpStatusCode.NotFound, HttpStatusCode.MethodNotAllowed });
        }

        Assert.Equal(usesBefore, Sql("SELECT count(*) FROM platform_operator_evidence_uses;"));
        Assert.Equal("0", Sql($"SELECT count(*) FROM platform_operator_approvals WHERE organization_membership_request_id = '{requestId:D}';"));

        // Vault policy of the console token (the token the console container holds): sign with its three keys, nothing else.
        var probe = Convert.ToBase64String(Encoding.UTF8.GetBytes("platform console vault isolation probe"));
        Assert.Contains("vault:v1:", PlatformConsoleHarness.Vault($"vault write -field=signature transit/sign/{ConsoleKeys[2]}/sha2-256 input={probe} marshaling_algorithm=jws", PlatformConsoleHarness.ConsoleVaultToken), StringComparison.Ordinal);
        var organizationKey = "org-" + s.OrganizationId.ToString("N") + "-row-integrity-v1";
        foreach (var denied in new[]
        {
            $"vault write transit/hmac/{organizationKey}/sha2-256 input={probe}",
            $"vault read transit/keys/{ConsoleKeys[0]}",
            $"vault write -f transit/keys/{ConsoleKeys[0]}/rotate",
            $"vault write transit/keys/{ConsoleKeys[0]}/config deletion_allowed=true exportable=true",
            $"vault read transit/export/signing-key/{ConsoleKeys[0]}",
            "vault write transit/keys/console-planted-key type=ecdsa-p256",
            $"vault write transit/sign/{organizationKey}/sha2-256 input={probe}",
        })
        {
            Assert.Contains("permission denied", PlatformConsoleHarness.Vault(denied, PlatformConsoleHarness.ConsoleVaultToken, throwOnError: false), StringComparison.OrdinalIgnoreCase);
        }

        // The keys themselves are non-exportable ECDSA P-256 with no plaintext backup.
        var key = JsonDocument.Parse(PlatformConsoleHarness.Vault($"vault read -format=json transit/keys/{ConsoleKeys[0]}", PlatformConsoleHarness.RootToken())).RootElement.GetProperty("data");
        Assert.Equal("ecdsa-p256", key.GetProperty("type").GetString());
        Assert.False(key.GetProperty("exportable").GetBoolean());
        Assert.False(key.GetProperty("allow_plaintext_backup").GetBoolean());
    }

    [Fact]
    public async Task ConsoleAndBusinessSessionsAreDisjointAndTheConsoleReachesNoOrganizationData()
    {
        await PlatformConsoleHarness.EnsureConsoleOperatorsAsync(fixture);
        var s = new StageBScenario(fixture, "pc-isolation");
        await s.BootstrapAsync();
        using var dana = await ConsoleSession.SignInAsync(ConsoleSubjects[0]);

        // The console cookie is not a business session: the business API reports no session and refuses protected reads.
        using (var business = new HttpClient { BaseAddress = new Uri(StageBFixture.ApiBaseAddress) })
        {
            using var session = new HttpRequestMessage(HttpMethod.Get, "/api/v1/session");
            session.Headers.Add("Cookie", dana.Cookie);
            using var sessionResponse = await business.SendAsync(session, TestContext.Current.CancellationToken);
            Assert.False(JsonDocument.Parse(await sessionResponse.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).RootElement.GetProperty("authenticated").GetBoolean());
            foreach (var path in new[] { "/api/v1/form-definitions", "/api/v1/organization/members", "/api/v1/organization/membership-requests" })
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, path);
                request.Headers.Add("Cookie", dana.Cookie);
                using var response = await business.SendAsync(request, TestContext.Current.CancellationToken);
                Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            }
        }

        // A business session cookie is not a console session.
        using var anonymous = new ConsoleSession { Cookie = "ef-session=" + new string('A', 64) };
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.SendAsync(HttpMethod.Get, "/console-api/v1/operator/me")).Status);
        Assert.Equal("ORIGIN_NOT_ALLOWED", (await dana.SendAsync(HttpMethod.Get, "/console-api/v1/operator/me", origin: "http://127.0.0.1:8091")).Code);

        // The console has no route to records, documents, teams or members; and a console operator is no organization member.
        foreach (var path in new[] { "/api/v1/form-records", "/console-api/v1/form-records", $"/console-api/v1/organizations/{s.OrganizationId:D}/members", "/console-api/v1/documents" })
        {
            Assert.Equal(HttpStatusCode.NotFound, (await dana.SendAsync(HttpMethod.Get, path)).Status);
        }

        Assert.Equal("0", Sql($"SELECT count(*) FROM identity_bindings WHERE identity_subject_identifier IN ('{string.Join("','", ConsoleSubjects)}');"));
        var roster = await fixture.Authority.SendAsync(HttpMethod.Post, "/internal/v1/organizations/roster", TestIdentity.WorkloadToken(), new { organization_id = s.OrganizationId }, TestIdentity.PlatformOperatorAssertion(ConsoleSubjects[0]));
        Assert.Contains(roster.Status, new[] { HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden, HttpStatusCode.BadRequest });
    }

    [Fact]
    public async Task ASuspendedOperatorsConsoleSessionEndsAndReinstatementNeedsTwoOperators()
    {
        var ids = await PlatformConsoleHarness.EnsureConsoleOperatorsAsync(fixture);
        using var frankie = await ConsoleSession.SignInAsync(ConsoleSubjects[2]);
        Assert.Equal(HttpStatusCode.OK, (await frankie.SendAsync(HttpMethod.Get, "/console-api/v1/operator/me")).Status);
        await PlatformConsoleHarness.SuspendAsync(fixture, ids[2]);
        try
        {
            var refused = await frankie.SendAsync(HttpMethod.Get, "/console-api/v1/session");
            Assert.Equal(HttpStatusCode.Forbidden, refused.Status);
            Assert.Equal("PLATFORM_OPERATOR_NOT_ACTIVE", refused.Code);
            Assert.Equal(HttpStatusCode.Unauthorized, (await frankie.SendAsync(HttpMethod.Get, "/console-api/v1/operator/me")).Status);
            var signIn = await new ConsoleSession().SendAsync(HttpMethod.Post, "/console-api/v1/session/development-login", new { subjectIdentifier = ConsoleSubjects[2] });
            Assert.Equal("PLATFORM_OPERATOR_NOT_ACTIVE", signIn.Code);
        }
        finally
        {
            await PlatformConsoleHarness.ReinstateAsync(fixture, ids[2]);
        }

        using var again = await ConsoleSession.SignInAsync(ConsoleSubjects[2]);
        Assert.Equal(ids[2], again.PlatformOperatorId);
    }

    [Fact]
    public async Task ASuspendedOperatorsPendingApprovalIsInvalidAndTheyCannotDecideThroughTheConsoleOrComplete()
    {
        var ids = await PlatformConsoleHarness.EnsureConsoleOperatorsAsync(fixture);
        var s = new StageBScenario(fixture, "pc-suspended-decides");
        await s.BootstrapAsync();
        var requestId = await RequestAsync(s, "Console Suspended " + Guid.NewGuid().ToString("N")[..6]);
        using var frankie = await ConsoleSession.SignInAsync(ConsoleSubjects[2]);
        using var dana = await ConsoleSession.SignInAsync(ConsoleSubjects[0]);
        Assert.Equal("AwaitingSecondApproval", (await DecideAsync(frankie, s, requestId)).Body.GetProperty("outcome").GetString());

        await PlatformConsoleHarness.SuspendAsync(fixture, ids[2]);
        try
        {
            // Suspension invalidated Frankie's pending approval; Frankie's own session cannot approve or reject any more.
            Assert.Equal("Invalidated|OperatorSuspended", Sql($"SELECT approval_state_name || '|' || approval_closure_reason_name FROM platform_operator_approvals WHERE organization_membership_request_id = '{requestId:D}';"));
            var refusedApprove = await frankie.SendAsync(HttpMethod.Post, RequestPath(s, requestId, "/approvals"), new { expectedStatementSha256 = new string('a', 64), justification = Justification });
            Assert.Equal(HttpStatusCode.Forbidden, refusedApprove.Status);
            Assert.Equal("PLATFORM_OPERATOR_NOT_ACTIVE", refusedApprove.Code);
            Assert.Equal(HttpStatusCode.Unauthorized, (await frankie.SendAsync(HttpMethod.Post, RequestPath(s, requestId, "/reject"), new { expectedStatementSha256 = new string('a', 64), justification = Justification })).Status); // the session is gone
            // A second operator's approval does not complete the membership with the suspended operator's invalid approval.
            var second = await DecideAsync(dana, s, requestId);
            Assert.True(second.Status == HttpStatusCode.OK, second.Text);
            Assert.Equal("AwaitingSecondApproval", second.Body.GetProperty("outcome").GetString());
            Assert.Equal(0, Bindings(requestId));
        }
        finally
        {
            await PlatformConsoleHarness.ReinstateAsync(fixture, ids[2]);
        }
    }

    [Fact]
    public async Task ArchiveOutageAndADetectedRollbackFailClosedThroughTheConsoleAndReplayReopensIt()
    {
        await PlatformConsoleHarness.EnsureConsoleOperatorsAsync(fixture);
        var s = new StageBScenario(fixture, "pc-archive");
        await s.BootstrapAsync();
        var requestId = await RequestAsync(s, "Console Archive " + Guid.NewGuid().ToString("N")[..6]);
        using var dana = await ConsoleSession.SignInAsync(ConsoleSubjects[0]);
        var sha = await StatementAsync(dana, s, requestId);

        // Archive unreachable: the rollback check cannot run, so the platform plane fails closed (reads and decisions).
        fixture.Stack.Pause("archive-store");
        try
        {
            var me = await dana.SendAsync(HttpMethod.Get, "/console-api/v1/operator/me");
            Assert.Equal(HttpStatusCode.ServiceUnavailable, me.Status);
            Assert.Equal("PLATFORM_JOURNAL_UNVERIFIABLE", me.Code);
            Assert.Equal("PLATFORM_JOURNAL_UNVERIFIABLE", (await DecideAsync(dana, s, requestId, sha: sha)).Code);
        }
        finally
        {
            fixture.Stack.Unpause("archive-store");
        }

        Assert.Equal("0", Sql($"SELECT count(*) FROM platform_operator_approvals WHERE organization_membership_request_id = '{requestId:D}';"));
        await PlatformConsoleHarness.WaitAnchoredAsync(fixture);
        Assert.Equal(HttpStatusCode.OK, (await dana.SendAsync(HttpMethod.Get, "/console-api/v1/operator/me")).Status); // same session, still valid

        // A refused decision is journaled (no state change); then a simulated restore drops that event from AuthorityDb.
        Assert.Equal("AwaitingSecondApproval", (await DecideAsync(dana, s, requestId)).Body.GetProperty("outcome").GetString());
        Assert.Equal("OPERATOR_ALREADY_APPROVED", (await DecideAsync(dana, s, requestId)).Code);
        await PlatformConsoleHarness.WaitAnchoredAsync(fixture);
        var head = long.Parse(Sql("SELECT platform_journal_revision FROM platform_journal_heads;"), CultureInfo.InvariantCulture);
        Assert.Equal("PlatformOperatorDecisionRefused", Sql($"SELECT journal_event_kind FROM platform_journal_entries WHERE platform_journal_revision = {head};"));
        fixture.Stack.AuthorityRestoreSimulationSql($"""
            DELETE FROM platform_journal_entries WHERE platform_journal_revision = {head};
            UPDATE platform_journal_heads SET platform_journal_revision = {head - 1}, latest_platform_journal_digest = (SELECT journal_event_digest FROM platform_journal_entries WHERE platform_journal_revision = {head - 1});
            """);
        try
        {
            var behind = await dana.SendAsync(HttpMethod.Get, "/console-api/v1/membership-requests");
            Assert.Equal(HttpStatusCode.Locked, behind.Status);
            Assert.Equal("PLATFORM_JOURNAL_BEHIND_ARCHIVE", behind.Code);
            using var ellis = await ConsoleSession.TrySignInAsync(ConsoleSubjects[1]);
            Assert.Equal("PLATFORM_JOURNAL_BEHIND_ARCHIVE", ellis.LastCode);
            Assert.Equal(0, Bindings(requestId));
        }
        finally
        {
            var replay = await fixture.Authority.OperatorAsync(HttpMethod.Post, "/internal/v1/operator/platform-journal/replay", new { });
            Assert.True(replay.IsSuccess, replay.Body);
            Assert.True(replay.Root.GetProperty("gate_open").GetBoolean(), replay.Body);
        }

        Assert.Equal(head.ToString(CultureInfo.InvariantCulture), Sql("SELECT platform_journal_revision FROM platform_journal_heads;"));
        Assert.Equal(HttpStatusCode.OK, (await dana.SendAsync(HttpMethod.Get, "/console-api/v1/membership-requests")).Status);
    }
}

/// <summary>Result of one console call (status, parsed body, raw text, problem code).</summary>
public sealed record ConsoleResult(HttpStatusCode Status, JsonElement Body, string Text)
{
    public string Code => Body.ValueKind == JsonValueKind.Object && Body.TryGetProperty("errorCode", out var code) ? code.GetString() ?? string.Empty : string.Empty;
}

/// <summary>One browser session against the console on the security-test stack (cookie handled by hand: Secure + path-scoped).</summary>
public sealed class ConsoleSession : IDisposable
{
    public const string BaseAddress = "http://127.0.0.1:5192";
    private readonly HttpClient client = new() { BaseAddress = new Uri(BaseAddress), Timeout = TimeSpan.FromMinutes(2) };

    public string? Cookie { get; set; }

    public string? Csrf { get; set; }

    public Guid PlatformOperatorId { get; private set; }

    public string LastCode { get; private set; } = string.Empty;

    /// <summary>
    /// Signs in, waiting out the console's deliberate login limit (20 per minute, fixed window) when many test classes run back to back in
    /// one run: the limiter is shared state across the whole run, and a refusal here is the limiter working, not a defect. Tests that
    /// assert the limiter itself use <see cref="TrySignInAsync"/> and are unaffected.
    /// </summary>
    public static async Task<ConsoleSession> SignInAsync(string subject)
    {
        var session = await TrySignInAsync(subject);
        for (var attempt = 1; session.Cookie is null && session.LastCode == "RATE_LIMITED" && attempt <= 4; attempt++)
        {
            session.Dispose();
            await Task.Delay(TimeSpan.FromSeconds(16), TestContext.Current.CancellationToken); // the window is one minute
            session = await TrySignInAsync(subject);
        }

        Assert.True(session.Cookie is not null, "console sign-in failed: " + session.LastCode);
        return session;
    }

    public static async Task<ConsoleSession> TrySignInAsync(string subject)
    {
        var session = new ConsoleSession();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/console-api/v1/session/development-login") { Content = JsonContent.Create(new { subjectIdentifier = subject }) };
        request.Headers.Add("Origin", BaseAddress);
        using var response = await session.client.SendAsync(request, TestContext.Current.CancellationToken);
        var text = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        var result = new ConsoleResult(response.StatusCode, text.Length > 0 ? JsonDocument.Parse(text).RootElement.Clone() : default, text);
        session.LastCode = result.Code;
        if (response.IsSuccessStatusCode && response.Headers.TryGetValues("Set-Cookie", out var cookies))
        {
            session.Cookie = cookies.Select(c => c.Split(';')[0]).Last(c => !c.EndsWith('='));
            session.Csrf = result.Body.GetProperty("csrfToken").GetString();
            session.PlatformOperatorId = result.Body.GetProperty("platformOperatorId").GetGuid();
        }

        return session;
    }

    public async Task<ConsoleResult> SendAsync(HttpMethod method, string path, object? body = null, string? origin = BaseAddress)
    {
        using var request = new HttpRequestMessage(method, path);
        if (origin is not null)
        {
            request.Headers.Add("Origin", origin);
        }

        if (Cookie is not null)
        {
            request.Headers.Add("Cookie", Cookie);
        }

        if (method != HttpMethod.Get && Csrf is not null)
        {
            request.Headers.Add("X-CSRF-Token", Csrf);
        }

        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        var text = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        JsonElement json = default;
        if (text.TrimStart().StartsWith('{'))
        {
            json = JsonDocument.Parse(text).RootElement.Clone();
        }

        var result = new ConsoleResult(response.StatusCode, json, text);
        LastCode = result.Code;
        return result;
    }

    public void Dispose() => client.Dispose();
}
