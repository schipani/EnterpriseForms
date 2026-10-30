using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using EnterpriseForms.SecurityTests.Harness;
using EnterpriseForms.StageBTests.Harness;
using Xunit;

namespace EnterpriseForms.StageBTests.Tests;

/// <summary>
/// Owner message 7 on the real stack (business API, Authorization Authority, SpiceDB, both PostgreSQL servers, real test
/// identities): Security Administrator management. "Security Administrator" is the Authority relation
/// <c>system_administrator</c>; appointments and removals are GrantSystemRole / RevokeSystemRole security commands that only
/// an existing Security Administrator of the same organization may submit, with step-up (the synthetic development step-up
/// is enabled on this isolated stack only). Organization owners cannot elevate themselves or anyone else; the last
/// Security Administrator cannot be removed; removal takes effect on the next request of every existing token and browser
/// session; refusals are recorded on the Authority's own audit stream. The role confers no record content and no Form
/// Designer rights. Nothing here is mocked.
/// </summary>
[Collection(StageBStackCollectionDefinition.Name)]
public sealed class SecurityAdministratorTests(StageBFixture fixture)
{
    private const string Roster = "/api/v1/organization/security-administrators";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<StageBScenario> ScenarioAsync(string name)
    {
        var s = new StageBScenario(fixture, name);
        await s.BootstrapAsync();
        return s;
    }

    private Task<HttpResult> RosterAsync(StageBScenario s, TestUser user) => fixture.Api.GetAsync(Roster, s.Token(user));

    private Task<HttpResult> AppointAsync(StageBScenario s, TestUser actor, Guid target, bool stepUp = true, string reason = "governance ticket SA-1") =>
        fixture.Api.PostAsync(Roster, s.Token(actor), new { userId = target, reason, useDevelopmentStepUp = stepUp });

    private Task<HttpResult> RemoveAsync(StageBScenario s, TestUser actor, Guid target, bool stepUp = true) =>
        fixture.Api.PostAsync($"{Roster}/{target:D}/remove", s.Token(actor), new { reason = "rotation SA-2", useDevelopmentStepUp = stepUp });

    private async Task<List<string>> CapabilitiesAsync(StageBScenario s, TestUser user)
    {
        var dashboard = await fixture.Api.GetAsync("/api/v1/dashboard", s.Token(user));
        Assert.True(dashboard.IsSuccess, dashboard.Body);
        return dashboard.Root.GetProperty("capabilities").EnumerateArray().Select(c => c.GetString()!).ToList();
    }

    private static void AssertOk(HttpResult result) => Assert.True(result.IsSuccess, result.Body);

    private static List<Guid> Administrators(HttpResult roster) =>
        roster.Root.GetProperty("administrators").EnumerateArray().Select(a => a.GetProperty("userId").GetGuid()).ToList();

    /// <summary>Exactly what the business API sends, but without the business API: the Authority alone must refuse.</summary>
    private async Task<HttpResult> DirectCommandAsync(Guid organizationId, string token, string commandKind, Guid target, string stepUp = "synthetic-development-step-up")
    {
        var gate = await fixture.Authority.GateAsync(organizationId);
        return await fixture.Authority.SendAsync(HttpMethod.Post, "/internal/v1/security-commands", TestIdentity.WorkloadToken(), new
        {
            organization_id = organizationId,
            command_kind = commandKind,
            resource = new { resource_kind = "Organization", resource_id = organizationId },
            expected_authorization_revision = gate.GetProperty("authorization_revision").GetInt64(),
            command_payload = new { authority_user_id = target, role_name = "system_administrator", reason = "direct attempt", step_up_evidence = stepUp },
            change_reason = "direct attempt",
        }, token, Guid.NewGuid().ToString("N"));
    }

    private string AuthorityAudit(Guid organizationId, string eventKind) =>
        fixture.Stack.AuthorityObserverSql($"SELECT coalesce(string_agg(journal_event_body_json, ' ;; ' ORDER BY journal_stream_revision), '') FROM security_journal_entries WHERE organization_id = '{organizationId:D}' AND journal_stream_name = 'audit' AND journal_event_kind = '{eventKind}';").Trim();

    private int CommittedCommands(Guid organizationId, string commandKind) =>
        int.Parse(fixture.Stack.AuthorityObserverSql($"SELECT count(*) FROM security_journal_entries WHERE organization_id = '{organizationId:D}' AND journal_stream_name = 'authorization' AND journal_event_body_json LIKE '%\"{commandKind}\"%';").Trim(), CultureInfo.InvariantCulture);

    private string SystemAdministratorEdges(Guid organizationId) =>
        fixture.Stack.AuthorityObserverSql($"SELECT coalesce(string_agg(subject_entity_id::text, ',' ORDER BY subject_entity_id), '') FROM authority_relationship_edges WHERE organization_id = '{organizationId:D}' AND resource_type_name = 'organization' AND relationship_name = 'system_administrator' AND removed_by_security_command_id IS NULL;").Trim();

    /// <summary>Tests 1, 5, 8, 9, 10, 13: an administrator appoints a verified member; the appointee administers search approvals and nothing else.</summary>
    [Fact]
    public async Task ASecurityAdministratorAppointsAMemberWhoGainsSearchAdministrationButNoRecordsOrDesign()
    {
        var s = await ScenarioAsync("sa-appoint");
        var formId = await s.CreateFormAsync("SA records " + s.Name, s.Reader);
        var reference = await s.AddFieldAsync(formId, "case_reference", "Case reference", "String");
        await s.SaveDraftAsync(formId, StageBScenario.Layout("SA", StageBScenario.Section("main", "Main", StageBScenario.Field(reference, "text", "Case reference", 4))));
        await s.PublishAsync(formId, "v1");
        var created = await fixture.Api.PostAsync($"/api/v1/form-definitions/{formId:D}/records", s.Token(s.Reader), new { values = new Dictionary<string, string> { ["case_reference"] = "SA-REC-1" }, ownerTeamId = (Guid?)null });
        Assert.True(created.Status == HttpStatusCode.Created, created.Body);
        var recordId = created.Root.GetProperty("formRecordId").GetGuid();

        // The bootstrap administrator sees the roster with candidates; the clerk is not yet an administrator.
        var before = await RosterAsync(s, s.Administrator);
        Assert.True(before.IsSuccess, before.Body);
        Assert.True(before.Root.GetProperty("canManage").GetBoolean());
        Assert.True(before.Root.GetProperty("appointmentOperational").GetBoolean()); // synthetic development step-up, this stack only
        Assert.Equal([s.Administrator.AuthorityUserId], Administrators(before));
        var administrator = before.Root.GetProperty("administrators")[0];
        Assert.Equal(TestIdentity.Issuer, administrator.GetProperty("identityIssuer").GetString());
        Assert.Equal(s.Administrator.Subject, administrator.GetProperty("identitySubject").GetString());
        Assert.True(administrator.GetProperty("isCurrentUser").GetBoolean());
        Assert.Contains(before.Root.GetProperty("candidates").EnumerateArray(), c => c.GetProperty("userId").GetGuid() == s.Clerk.AuthorityUserId);
        Assert.DoesNotContain("security.administer", await CapabilitiesAsync(s, s.Clerk));
        Assert.False((await fixture.Api.GetAsync("/api/v1/organization/search-approvals", s.Token(s.Clerk))).IsSuccess);

        var appointed = await AppointAsync(s, s.Administrator, s.Clerk.AuthorityUserId);
        Assert.Equal(HttpStatusCode.NoContent, appointed.Status);
        Assert.Equal([s.Administrator.AuthorityUserId, s.Clerk.AuthorityUserId], Administrators(await RosterAsync(s, s.Administrator))); // in order of appointment
        Assert.Contains(s.Clerk.AuthorityUserId.ToString("D"), SystemAdministratorEdges(s.OrganizationId), StringComparison.Ordinal);

        // The appointee administers search approvals at once (same token, no new sign-in) and can manage the roster.
        Assert.Contains("security.administer", await CapabilitiesAsync(s, s.Clerk));
        var approvals = await fixture.Api.GetAsync("/api/v1/organization/search-approvals", s.Token(s.Clerk));
        Assert.True(approvals.IsSuccess, approvals.Body);
        Assert.True((await RosterAsync(s, s.Clerk)).Root.GetProperty("canManage").GetBoolean());

        // No record content (the reader owns the record and shared nothing) and no Form Designer rights.
        var capabilities = await CapabilitiesAsync(s, s.Clerk);
        Assert.DoesNotContain("forms.design", capabilities);
        Assert.DoesNotContain("organization.manage", capabilities);
        foreach (var path in new[] { $"/api/v1/form-records/{recordId:D}", $"/api/v1/form-records/{recordId:D}/projection" })
        {
            var read = await fixture.Api.GetAsync(path, s.Token(s.Clerk));
            Assert.True(read.Status is HttpStatusCode.Forbidden or HttpStatusCode.NotFound, path + ": " + read.Body);
            Assert.DoesNotContain("SA-REC-1", read.Body, StringComparison.Ordinal);
        }

        var design = await fixture.Api.PostAsync("/api/v1/form-definitions/designer", s.Token(s.Clerk), new { formDefinitionName = "Not allowed", formDefinitionDescription = "x", formUserUserIds = Array.Empty<Guid>(), formUserTeamIds = Array.Empty<Guid>() });
        Assert.Equal(HttpStatusCode.Forbidden, design.Status);
        var draft = await fixture.Api.GetAsync($"/api/v1/form-definitions/{formId:D}/draft", s.Token(s.Clerk));
        Assert.False(draft.IsSuccess, draft.Body);

        // Evidence: one committed GrantSystemRole command; a business audit event with actor, target and reason.
        Assert.Equal(1, CommittedCommands(s.OrganizationId, "GrantSystemRole"));
        var audit = fixture.Stack.AttackerSql($"SELECT actor_user_id || '|' || target_entity_id || '|' || event_metadata_json::text FROM audit_events WHERE tenant_id = '{s.OrganizationId:D}' AND event_action = 'SECURITY_ADMINISTRATOR_APPOINTED';").Trim();
        Assert.StartsWith($"{s.Administrator.AuthorityUserId:D}|{s.Clerk.AuthorityUserId:D}|", audit, StringComparison.Ordinal);
        Assert.Contains("governance ticket SA-1", audit, StringComparison.Ordinal);
        Assert.DoesNotContain("synthetic", fixture.Stack.AuthorityObserverSql($"SELECT string_agg(journal_event_body_json, ' ') FROM security_journal_entries WHERE organization_id = '{s.OrganizationId:D}' AND journal_stream_name = 'authorization' AND journal_event_body_json LIKE '%GrantSystemRole%';"), StringComparison.Ordinal);
    }

    /// <summary>Tests 2, 3, 13: owners, designers and members cannot appoint anyone, themselves included — through the API or directly at the Authority; refusals are audited by the Authority.</summary>
    [Fact]
    public async Task OwnersDesignersAndMembersCannotAppointAndEveryRefusalIsAuditedByTheAuthority()
    {
        var s = await ScenarioAsync("sa-refuse");
        var commandsBefore = CommittedCommands(s.OrganizationId, "GrantSystemRole");

        // The organization owner (s.Designer holds organization_owner + designer) sees the roster read-only.
        var ownerView = await RosterAsync(s, s.Designer);
        Assert.True(ownerView.IsSuccess, ownerView.Body);
        Assert.False(ownerView.Root.GetProperty("canManage").GetBoolean());
        Assert.Empty(ownerView.Root.GetProperty("candidates").EnumerateArray());

        foreach (var actor in new[] { s.Designer, s.DesignerOnly, s.Clerk, s.Reader })
        {
            foreach (var target in new[] { actor.AuthorityUserId, s.Reader.AuthorityUserId })
            {
                var attempt = await AppointAsync(s, actor, target);
                Assert.Equal(HttpStatusCode.Forbidden, attempt.Status);
                Assert.Equal("FORBIDDEN", attempt.ErrorCode);
            }
        }

        // Directly at the Authority with a valid workload credential and the owner's own assertion: refused the same way.
        var direct = await DirectCommandAsync(s.OrganizationId, s.Token(s.Designer), "GrantSystemRole", s.Designer.AuthorityUserId);
        Assert.Equal(HttpStatusCode.Forbidden, direct.Status);
        Assert.Equal("FORBIDDEN", direct.ErrorCode);

        // Members other than owners and administrators cannot even read the roster.
        foreach (var user in new[] { s.Clerk, s.Reader, s.DesignerOnly })
        {
            Assert.Equal(HttpStatusCode.Forbidden, (await RosterAsync(s, user)).Status);
        }

        // Repeating the one-shot operator bootstrap for an existing organization creates nobody (no silent second path).
        var intruder = TestUser.New("Intruder");
        var again = await fixture.Authority.OperatorAsync(HttpMethod.Post, "/internal/v1/operator/organizations", new
        {
            organization_id = s.OrganizationId,
            organization_display_name = "Stage B " + s.Name,
            identities = new object[]
            {
                new { authority_user_id = s.Designer.AuthorityUserId, issuer_uri = TestIdentity.Issuer, subject_identifier = s.Designer.Subject, organization_roles = new[] { "organization_owner", "designer", "system_administrator" } },
                new { authority_user_id = intruder.AuthorityUserId, issuer_uri = TestIdentity.Issuer, subject_identifier = intruder.Subject, organization_roles = new[] { "system_administrator" } },
            },
        });
        Assert.True(again.Status != HttpStatusCode.InternalServerError, again.Body);
        Assert.Equal("0", fixture.Stack.AuthorityObserverSql($"SELECT count(*) FROM identity_bindings WHERE organization_id = '{s.OrganizationId:D}' AND authority_user_id = '{intruder.AuthorityUserId:D}';").Trim());

        Assert.Equal(commandsBefore, CommittedCommands(s.OrganizationId, "GrantSystemRole"));
        Assert.Equal(s.Administrator.AuthorityUserId.ToString("D"), SystemAdministratorEdges(s.OrganizationId));
        var refusals = AuthorityAudit(s.OrganizationId, "SecurityAdministratorCommandRejected");
        Assert.Equal(9, refusals.Split(" ;; ").Length); // 8 through the API + 1 direct
        Assert.Contains("\"rejection_reason\":\"FORBIDDEN\"", refusals.Replace(" ", string.Empty, StringComparison.Ordinal), StringComparison.Ordinal);
        Assert.DoesNotContain("SA-1", refusals, StringComparison.Ordinal); // reasons stay out of the refusal evidence
    }

    /// <summary>Test 4: no cross-organization appointment, removal or roster disclosure.</summary>
    [Fact]
    public async Task AnotherOrganizationsAdministratorCannotAppointRemoveOrReadTheRoster()
    {
        var s = await ScenarioAsync("sa-org-a");
        var other = await ScenarioAsync("sa-org-b");

        // Other organization's administrator, with its own valid token: its context is its own organization only.
        var otherRoster = await RosterAsync(other, other.Administrator);
        Assert.True(otherRoster.IsSuccess, otherRoster.Body);
        Assert.Equal([other.Administrator.AuthorityUserId], Administrators(otherRoster));
        Assert.DoesNotContain(otherRoster.Root.GetProperty("candidates").EnumerateArray(), c => c.GetProperty("userId").GetGuid() == s.Clerk.AuthorityUserId);

        // Appointing a member of organization A from organization B: not a principal there.
        var foreign = await AppointAsync(other, other.Administrator, s.Clerk.AuthorityUserId);
        Assert.False(foreign.IsSuccess, foreign.Body);
        Assert.Equal("INVALID_COMMAND", foreign.ErrorCode);

        // Directly at the Authority, naming organization A with organization B's administrator: refused, nothing committed.
        foreach (var kind in new[] { "GrantSystemRole", "RevokeSystemRole" })
        {
            var direct = await DirectCommandAsync(s.OrganizationId, other.Token(other.Administrator), kind, kind == "GrantSystemRole" ? s.Clerk.AuthorityUserId : s.Administrator.AuthorityUserId);
            Assert.True(direct.Status is HttpStatusCode.Forbidden or HttpStatusCode.NotFound or HttpStatusCode.Unauthorized, direct.Body);
        }

        var directRoster = await fixture.Authority.SendAsync(HttpMethod.Post, "/internal/v1/security-administrators", TestIdentity.WorkloadToken(), new { organization_id = s.OrganizationId }, other.Token(other.Administrator));
        Assert.True(directRoster.Status is HttpStatusCode.Forbidden or HttpStatusCode.NotFound or HttpStatusCode.Unauthorized, directRoster.Body);
        Assert.DoesNotContain(s.Administrator.Subject, directRoster.Body, StringComparison.Ordinal);

        Assert.Equal(s.Administrator.AuthorityUserId.ToString("D"), SystemAdministratorEdges(s.OrganizationId));
        Assert.Equal(other.Administrator.AuthorityUserId.ToString("D"), SystemAdministratorEdges(other.OrganizationId));
    }

    /// <summary>Tests 6, 7, 11, 12, 13: removal, last-administrator protection, and immediate loss of every privilege on existing tokens.</summary>
    [Fact]
    public async Task RemovalTakesEffectImmediatelyAndTheLastAdministratorCannotBeRemoved()
    {
        var s = await ScenarioAsync("sa-remove");
        var last = await RemoveAsync(s, s.Administrator, s.Administrator.AuthorityUserId);
        Assert.Equal(HttpStatusCode.Conflict, last.Status);
        Assert.Equal("LAST_SECURITY_ADMINISTRATOR", last.ErrorCode);

        AssertOk(await AppointAsync(s, s.Administrator, s.Clerk.AuthorityUserId));
        var clerkToken = s.Token(s.Clerk); // one token, used before and after removal
        Assert.True((await fixture.Api.GetAsync("/api/v1/organization/search-approvals", clerkToken)).IsSuccess);

        // Removing someone who is not an administrator is refused; the newcomer removes the original administrator.
        Assert.Equal("INVALID_COMMAND", (await RemoveAsync(s, s.Clerk, s.Reader.AuthorityUserId)).ErrorCode);
        AssertOk(await RemoveAsync(s, s.Clerk, s.Administrator.AuthorityUserId));
        Assert.Equal([s.Clerk.AuthorityUserId], Administrators(await RosterAsync(s, s.Clerk)));

        // The removed administrator's existing token loses everything on its next request.
        Assert.DoesNotContain("security.administer", await CapabilitiesAsync(s, s.Administrator));
        Assert.Equal(HttpStatusCode.Forbidden, (await RosterAsync(s, s.Administrator)).Status);
        Assert.False((await fixture.Api.GetAsync("/api/v1/organization/search-approvals", s.Token(s.Administrator))).IsSuccess);
        Assert.Equal(HttpStatusCode.Forbidden, (await AppointAsync(s, s.Administrator, s.Administrator.AuthorityUserId)).Status);

        // Now the clerk is the last one.
        var lastAgain = await RemoveAsync(s, s.Clerk, s.Clerk.AuthorityUserId);
        Assert.Equal("LAST_SECURITY_ADMINISTRATOR", lastAgain.ErrorCode);

        // Restore the bootstrap administrator, then the clerk removes themself; the clerk's same token is then refused.
        AssertOk(await AppointAsync(s, s.Clerk, s.Administrator.AuthorityUserId));
        AssertOk(await RemoveAsync(s, s.Clerk, s.Clerk.AuthorityUserId));
        Assert.False((await fixture.Api.GetAsync("/api/v1/organization/search-approvals", clerkToken)).IsSuccess);
        Assert.Equal(HttpStatusCode.Forbidden, (await fixture.Api.GetAsync(Roster, clerkToken)).Status);

        Assert.Equal(s.Administrator.AuthorityUserId.ToString("D"), SystemAdministratorEdges(s.OrganizationId));
        Assert.Equal(2, CommittedCommands(s.OrganizationId, "RevokeSystemRole"));
        Assert.Contains("LAST_SECURITY_ADMINISTRATOR", AuthorityAudit(s.OrganizationId, "SecurityAdministratorCommandRejected"), StringComparison.Ordinal);
        Assert.Equal("2", fixture.Stack.AttackerSql($"SELECT count(*) FROM audit_events WHERE tenant_id = '{s.OrganizationId:D}' AND event_action = 'SECURITY_ADMINISTRATOR_REMOVED';").Trim());
    }

    /// <summary>Tests 14, 17: step-up is required and only the synthetic marker is accepted here; missing, forged and expired credentials are refused.</summary>
    [Fact]
    public async Task StepUpAndAValidCredentialAreRequired()
    {
        var s = await ScenarioAsync("sa-auth");
        var noStepUp = await AppointAsync(s, s.Administrator, s.Clerk.AuthorityUserId, stepUp: false);
        Assert.Equal(HttpStatusCode.Forbidden, noStepUp.Status);
        Assert.Equal("STEP_UP_REQUIRED", noStepUp.ErrorCode);
        var wrongMarker = await DirectCommandAsync(s.OrganizationId, s.Token(s.Administrator), "GrantSystemRole", s.Clerk.AuthorityUserId, stepUp: "passkey-verified");
        Assert.Equal("STEP_UP_REQUIRED", wrongMarker.ErrorCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await AppointAsync(s, s.Administrator, s.Clerk.AuthorityUserId, reason: "x")).Status);

        foreach (var (label, token) in new (string, string?)[] { ("missing", null), ("forged", TestIdentity.ForgedUserToken(s.Administrator, s.OrganizationId)), ("expired", TestIdentity.ExpiredUserToken(s.Administrator, s.OrganizationId)) })
        {
            var read = await fixture.Api.SendAsync(HttpMethod.Get, Roster, token, null, null);
            Assert.True(read.Status == HttpStatusCode.Unauthorized, label + " roster: " + read.Body);
            var write = await fixture.Api.SendAsync(HttpMethod.Post, Roster, token, new { userId = s.Clerk.AuthorityUserId, reason = "governance ticket", useDevelopmentStepUp = true }, Guid.NewGuid().ToString("N"));
            Assert.True(write.Status == HttpStatusCode.Unauthorized, label + " appoint: " + write.Body);
        }

        Assert.Equal(s.Administrator.AuthorityUserId.ToString("D"), SystemAdministratorEdges(s.OrganizationId));
        Assert.Equal(0, CommittedCommands(s.OrganizationId, "GrantSystemRole"));
    }

    /// <summary>
    /// Tests 12, 15, 16 with real browser sessions of the seeded development organization (G22 CSRF, G23 Authority-held
    /// session state): a cookie session needs its own CSRF token; a removed administrator's live session loses the role at
    /// once; a signed-out session cannot be replayed. The seeded organization ends exactly as it started.
    /// </summary>
    [Fact]
    public async Task BrowserSessionsNeedCsrfAndKeepNoPrivilegeAfterRemovalOrSignOut()
    {
        using var security = await SignInAsync("security");
        using var clerk = await SignInAsync("clerk");
        var roster = await security.Client.GetFromJsonAsync<JsonElement>(Roster, Ct);
        var clerkId = roster.GetProperty("candidates").EnumerateArray().Single(c => c.GetProperty("displayName").GetString()!.StartsWith("Casey Clerk", StringComparison.Ordinal)).GetProperty("userId").GetGuid();
        var before = roster.GetProperty("administrators").EnumerateArray().Select(a => a.GetProperty("userId").GetGuid()).ToList();

        // G22: no token, a wrong token and another session's token are rejected before the endpoint; nothing changes.
        foreach (var token in new[] { null, "not-the-token", clerk.CsrfToken })
        {
            using var rejected = await SendAsync(security, HttpMethod.Post, Roster, token, new { userId = clerkId, reason = "csrf probe", useDevelopmentStepUp = true });
            Assert.Equal(HttpStatusCode.Forbidden, rejected.StatusCode);
            Assert.Equal("CSRF_TOKEN_INVALID", (await rejected.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("title").GetString());
        }

        Assert.Equal(before, (await security.Client.GetFromJsonAsync<JsonElement>(Roster, Ct)).GetProperty("administrators").EnumerateArray().Select(a => a.GetProperty("userId").GetGuid()).ToList());

        try
        {
            using (var appointed = await SendAsync(security, HttpMethod.Post, Roster, security.CsrfToken, new { userId = clerkId, reason = "session test appointment", useDevelopmentStepUp = true }))
            {
                Assert.Equal(HttpStatusCode.NoContent, appointed.StatusCode);
            }

            Assert.True((await clerk.Client.GetFromJsonAsync<JsonElement>(Roster, Ct)).GetProperty("canManage").GetBoolean());
            using (var removed = await SendAsync(security, HttpMethod.Post, $"{Roster}/{clerkId:D}/remove", security.CsrfToken, new { reason = "session test removal", useDevelopmentStepUp = true }))
            {
                Assert.Equal(HttpStatusCode.NoContent, removed.StatusCode);
            }
        }
        finally
        {
            // Leave the seeded organization as it was even when an assertion above failed after the appointment.
            var now = await security.Client.GetFromJsonAsync<JsonElement>(Roster, Ct);
            if (now.GetProperty("administrators").EnumerateArray().Any(a => a.GetProperty("userId").GetGuid() == clerkId))
            {
                using var cleanup = await SendAsync(security, HttpMethod.Post, $"{Roster}/{clerkId:D}/remove", security.CsrfToken, new { reason = "session test cleanup", useDevelopmentStepUp = true });
            }
        }

        // The clerk's live browser session keeps nothing.
        using (var clerkRoster = await clerk.Client.GetAsync(Roster, Ct))
        {
            Assert.Equal(HttpStatusCode.Forbidden, clerkRoster.StatusCode);
        }

        using (var clerkAppoint = await SendAsync(clerk, HttpMethod.Post, Roster, clerk.CsrfToken, new { userId = clerkId, reason = "self appointment after removal", useDevelopmentStepUp = true }))
        {
            Assert.Equal(HttpStatusCode.Forbidden, clerkAppoint.StatusCode);
        }

        // G23: after sign-out the same cookie is refused at the Authority-held session state.
        var cookie = security.CookieHeader;
        var csrf = security.CsrfToken;
        using (var logout = await SendAsync(security, HttpMethod.Post, "/api/v1/session/logout", csrf))
        {
            Assert.True(logout.IsSuccessStatusCode);
        }

        using var replay = new BrowserSession();
        using var request = new HttpRequestMessage(HttpMethod.Post, Roster) { Content = JsonContent.Create(new { userId = clerkId, reason = "replayed cookie", useDevelopmentStepUp = true }) };
        request.Headers.Add("Cookie", cookie);
        request.Headers.Add("X-CSRF-Token", csrf);
        using var replayed = await replay.RawClient.SendAsync(request, Ct);
        Assert.True(replayed.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden, replayed.StatusCode.ToString());
        using var check = await SignInAsync("security");
        Assert.Equal(before, (await check.Client.GetFromJsonAsync<JsonElement>(Roster, Ct)).GetProperty("administrators").EnumerateArray().Select(a => a.GetProperty("userId").GetGuid()).ToList());
    }

    private sealed class BrowserSession : IDisposable
    {
        private readonly HttpClientHandler handler = new() { CookieContainer = new CookieContainer(), UseCookies = true, AllowAutoRedirect = false };
        private readonly HttpClientHandler rawHandler = new() { UseCookies = false, AllowAutoRedirect = false };

        public BrowserSession()
        {
            Client = new HttpClient(handler) { BaseAddress = new Uri(StageBFixture.ApiBaseAddress), Timeout = TimeSpan.FromMinutes(2) };
            RawClient = new HttpClient(rawHandler) { BaseAddress = new Uri(StageBFixture.ApiBaseAddress), Timeout = TimeSpan.FromMinutes(2) };
        }

        public HttpClient Client { get; }

        /// <summary>No cookie container: the test sets the Cookie header itself (replay of a captured cookie).</summary>
        public HttpClient RawClient { get; }

        public string CsrfToken { get; set; } = string.Empty;

        public string CookieHeader => handler.CookieContainer.GetCookieHeader(new Uri(StageBFixture.ApiBaseAddress));

        public void Dispose()
        {
            Client.Dispose();
            RawClient.Dispose();
            handler.Dispose();
            rawHandler.Dispose();
        }
    }

    private static async Task<BrowserSession> SignInAsync(string persona)
    {
        var session = new BrowserSession();
        var login = await session.Client.PostAsJsonAsync("/api/v1/session/development-login", new { personaKey = persona }, Ct);
        Assert.True(login.IsSuccessStatusCode, await login.Content.ReadAsStringAsync(Ct));
        session.CsrfToken = (await login.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("csrfToken").GetString()!;
        return session;
    }

    private static async Task<HttpResponseMessage> SendAsync(BrowserSession session, HttpMethod method, string path, string? csrf, object? body = null)
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

        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return await session.Client.SendAsync(request, Ct);
    }
}
