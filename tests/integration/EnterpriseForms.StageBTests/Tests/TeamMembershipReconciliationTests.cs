using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using EnterpriseForms.SecurityTests.Harness;
using EnterpriseForms.StageBTests.Harness;
using Xunit;

namespace EnterpriseForms.StageBTests.Tests;

/// <summary>
/// Phase C8 — AC-9, C8-M1-01 and C8-M1-02 closure set (package §7, D8-22: RC-01…RC-19, RC-21…RC-28) on the isolated
/// security-test stack: real Authority, BusinessDb and AuthorityDb, SpiceDB, Vault and archive. Faults are injected with the
/// existing mechanisms (archive pause, container stop/start/scale/recreate, the hostile BusinessDb administrator). The two
/// Authority states no product path can reach on Stage A — organization integrity quarantine and a FailedReconciliation
/// command — are simulated by writing the disposable AuthorityDb directly (<see cref="SimulateAuthorityState"/>), clearly
/// labelled, never from product code. RC-20 is retired (D8-22); RC-22 and RC-28 run without a stack
/// (TeamMembershipReconcilerServiceTests, TeamMembershipReconciliationUnitTests, ReconcilerBoundaryTests).
/// Needs SECTEST_EXTRA_COMPOSE_FILE when the stack was started with an overlay.
/// </summary>
[Collection(StageBStackCollectionDefinition.Name)]
public sealed class TeamMembershipReconciliationTests(StageBFixture fixture)
{
    private static readonly HttpClient Authority = new() { BaseAddress = new Uri(StageBFixture.AuthorityBaseAddress), Timeout = TimeSpan.FromSeconds(60) };

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ---------------------------------------------------------------- helpers

    private async Task<(StageBScenario S, Guid TeamId)> TeamAsync(string name)
    {
        var s = new StageBScenario(fixture, name);
        await s.BootstrapAsync();
        var created = await fixture.Api.PostAsync("/api/v1/teams", s.Token(s.Designer), new { teamName = name + " " + s.Name });
        Assert.Equal(HttpStatusCode.Created, created.Status);
        await s.WaitGateReadyAsync();
        return (s, created.Root.GetProperty("teamId").GetGuid());
    }

    private static string NewKey(string tag) => tag + "-" + Guid.NewGuid().ToString("N");

    private Task<HttpResult> AddAsync(StageBScenario s, Guid teamId, TestUser actor, TestUser member, string key) =>
        fixture.Api.PostAsync($"/api/v1/teams/{teamId:D}/members", s.Token(actor), new { userId = member.AuthorityUserId }, key);

    private Task<HttpResult> RemoveAsync(StageBScenario s, Guid teamId, TestUser actor, TestUser member, string key) =>
        fixture.Api.PostAsync($"/api/v1/teams/{teamId:D}/members/{member.AuthorityUserId:D}/remove", s.Token(actor), new { }, key);

    private string Sql(string sql) => fixture.Stack.AttackerSql(sql);

    private JsonElement? Operation(StageBScenario s, string authorityKey)
    {
        var text = Sql($"SELECT row_to_json(o)::text FROM authority_command_operations o WHERE tenant_id = '{s.OrganizationId:D}' AND authority_idempotency_key = '{authorityKey}'").Trim();
        return text.Length == 0 ? null : JsonDocument.Parse(text).RootElement.Clone();
    }

    private async Task<JsonElement> WaitOperationAsync(StageBScenario s, string authorityKey, Func<JsonElement, bool> condition, TimeSpan timeout, string what)
    {
        var deadline = DateTime.UtcNow + timeout;
        JsonElement? last = null;
        while (DateTime.UtcNow < deadline)
        {
            last = Operation(s, authorityKey);
            if (last is { } row && condition(row))
            {
                return row;
            }

            await Task.Delay(500, Ct);
        }

        throw new TimeoutException($"{what}: {last?.GetRawText() ?? "no operation row"}");
    }

    private static string State(JsonElement row) => row.GetProperty("operation_state_name").GetString()!;

    private Task<JsonElement> WaitProjectedAsync(StageBScenario s, string authorityKey, int seconds = 90) =>
        WaitOperationAsync(s, authorityKey, r => State(r) == "Projected", TimeSpan.FromSeconds(seconds), "operation not projected");

    private int ActiveRosterRows(StageBScenario s, Guid teamId, TestUser member) =>
        int.Parse(Sql($"SELECT count(*) FROM team_memberships WHERE tenant_id = '{s.OrganizationId:D}' AND team_id = '{teamId:D}' AND user_id = '{member.AuthorityUserId:D}' AND removed_on IS NULL").Trim(), System.Globalization.CultureInfo.InvariantCulture);

    private (bool Present, long Revision)? Watermark(StageBScenario s, Guid teamId, TestUser member)
    {
        var text = Sql($"SELECT projected_membership_present || '|' || projected_membership_revision FROM team_membership_projection_states WHERE tenant_id = '{s.OrganizationId:D}' AND team_id = '{teamId:D}' AND member_user_id = '{member.AuthorityUserId:D}'").Trim();
        if (text.Length == 0)
        {
            return null;
        }

        var parts = text.Split('|');
        return (parts[0] == "true", long.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture));
    }

    private int AuthorityCommands(Guid organizationId, string? authorityKey = null) =>
        int.Parse(fixture.Stack.AuthorityObserverSql($"SELECT count(*) FROM security_commands WHERE organization_id = '{organizationId:D}'" + (authorityKey is null ? string.Empty : $" AND command_idempotency_key = '{authorityKey}'")).Trim(), System.Globalization.CultureInfo.InvariantCulture);

    private int FailureAudits(StageBScenario s, string? code = null) =>
        int.Parse(Sql($"SELECT count(*) FROM audit_events WHERE tenant_id = '{s.OrganizationId:D}' AND event_action = 'TEAM_MEMBERSHIP_RECONCILIATION_FAILED'" + (code is null ? string.Empty : $" AND event_metadata_json::text LIKE '%{code}%'")).Trim(), System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>
    /// TEST-ONLY simulation of Authority-internal state on the disposable stack (read-write AuthorityDb session inside its own
    /// container). Used solely for the two states with no Stage A product trigger: organization integrity quarantine (and the
    /// Authority's clearance of it) and a command marked FailedReconciliation. Never product code; never the attacker client.
    /// </summary>
    private string SimulateAuthorityState(string sql)
    {
        var encodedSql = Convert.ToBase64String(Encoding.UTF8.GetBytes(sql));
        var shell = $"echo {encodedSql} | base64 -d | PGPASSWORD=\"$POSTGRES_PASSWORD\" PGOPTIONS='-c search_path=authority' psql -U \"$POSTGRES_USER\" -d \"$POSTGRES_DB\" -v ON_ERROR_STOP=1 -qAt";
        return fixture.Stack.Compose($"exec -T authority-db sh -c \"echo {Convert.ToBase64String(Encoding.UTF8.GetBytes(shell))} | base64 -d | sh\"", 60);
    }

    private void StopWorkers() => fixture.Stack.Compose("stop worker", 120);

    private async Task StartWorkersAsync(int count = 1)
    {
        var running = fixture.Stack.Compose("ps --status running -q worker", 60, throwOnError: false).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Length;
        if (running == count)
        {
            return; // already running (for example started earlier in the same test)
        }

        var since = DateTime.UtcNow.AddSeconds(-2).ToString("yyyy-MM-ddTHH:mm:ssZ", System.Globalization.CultureInfo.InvariantCulture);
        fixture.Stack.Compose(count == 1 ? "up -d --no-deps worker" : $"up -d --no-deps --scale worker={count} --no-recreate worker", 240);
        var deadline = DateTime.UtcNow.AddSeconds(120);
        while (true)
        {
            var logs = fixture.Stack.Compose($"logs --no-color --since {since} worker", 60, throwOnError: false);
            if (CountOccurrences(logs, "Team membership reconciliation worker started") >= count)
            {
                return;
            }

            Assert.True(DateTime.UtcNow < deadline, "the worker(s) did not start: " + logs[^Math.Min(logs.Length, 2000)..]);
            await Task.Delay(1000, Ct);
        }
    }

    private static int CountOccurrences(string text, string value)
    {
        var count = 0;
        for (var index = text.IndexOf(value, StringComparison.Ordinal); index >= 0; index = text.IndexOf(value, index + value.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }

    private static async Task<(HttpStatusCode Status, string Body, HttpResponseMessage Response)> LookupAsync(string? bearer, Guid organizationId, string authorityKey, string kind = "AddTeamMember", string? assertion = null, Guid? correlation = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/internal/v1/security-command-outcomes/lookup")
        {
            Content = JsonContent.Create(new Dictionary<string, object> { ["organization_id"] = organizationId, ["idempotency_key"] = authorityKey, ["expected_command_kind"] = kind }),
        };
        if (bearer is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        }

        request.Headers.Add("X-Correlation-Id", (correlation ?? Guid.CreateVersion7()).ToString("D"));
        if (assertion is not null)
        {
            request.Headers.Add("X-Delegated-User-Assertion", assertion);
        }

        var response = await Authority.SendAsync(request, Ct);
        return (response.StatusCode, await response.Content.ReadAsStringAsync(Ct), response);
    }

    private static void AssertNotEffectiveYet(HttpResult result)
    {
        if (result.IsSuccess)
        {
            Assert.NotEqual("Projected", result.Root.GetProperty("operation").GetProperty("operationState").GetString());
            Assert.False(result.Root.GetProperty("operation").GetProperty("rosterUpdated").GetBoolean(), result.Body);
        }
    }

    // ---------------------------------------------------------------- RC-01 / RC-02 / RC-16 / RC-21

    [Fact]
    public async Task RC01APendingAddCompletesOnTheServerAfterTheBrowserIsGone()
    {
        var (s, teamId) = await TeamAsync("rc01");
        var key = NewKey("rc01");
        fixture.Stack.Pause("archive-store");
        try
        {
            AssertNotEffectiveYet(await AddAsync(s, teamId, s.Designer, s.Clerk, key));
            Assert.Equal(0, ActiveRosterRows(s, teamId, s.Clerk)); // no optimistic roster write while pending
        }
        finally
        {
            fixture.Stack.Unpause("archive-store");
        }

        // The "browser" is gone: no further request of any kind reaches the business API. The Worker alone completes it.
        var row = await WaitProjectedAsync(s, "team-add:" + key);
        Assert.Equal("MembershipPresentProjected", row.GetProperty("operation_outcome_code").GetString());
        Assert.Equal(1, ActiveRosterRows(s, teamId, s.Clerk));
        Assert.Equal((true, row.GetProperty("committed_authorization_revision").GetInt64()), Watermark(s, teamId, s.Clerk));
        Assert.Equal(1, AuthorityCommands(s.OrganizationId, "team-add:" + key));
    }

    [Fact]
    public async Task RC02APendingRemovalCompletesOnTheServerAfterTheBrowserIsGone()
    {
        var (s, teamId) = await TeamAsync("rc02");
        var addKey = NewKey("rc02-add");
        Assert.True((await AddAsync(s, teamId, s.Designer, s.Clerk, addKey)).IsSuccess);
        await WaitProjectedAsync(s, "team-add:" + addKey);
        var key = NewKey("rc02");
        fixture.Stack.Pause("archive-store");
        try
        {
            AssertNotEffectiveYet(await RemoveAsync(s, teamId, s.Designer, s.Clerk, key));
            Assert.Equal(1, ActiveRosterRows(s, teamId, s.Clerk)); // still a member until the Authority confirms
        }
        finally
        {
            fixture.Stack.Unpause("archive-store");
        }

        var row = await WaitProjectedAsync(s, "team-remove:" + key);
        Assert.Equal("MembershipAbsentProjected", row.GetProperty("operation_outcome_code").GetString());
        Assert.Equal(0, ActiveRosterRows(s, teamId, s.Clerk));
        Assert.Equal((false, row.GetProperty("committed_authorization_revision").GetInt64()), Watermark(s, teamId, s.Clerk));
    }

    [Fact]
    public async Task RC16ASecondAdministratorSeesPendingTurnIntoTheResultByReadingOnly()
    {
        var (s, teamId) = await TeamAsync("rc16");
        var key = NewKey("rc16");
        StopWorkers();
        try
        {
            var added = await AddAsync(s, teamId, s.Designer, s.Clerk, key);
            Assert.True(added.IsSuccess, added.Body);
            Assert.True(added.Root.GetProperty("command").GetProperty("effective").GetBoolean()); // Effective in the Authority…
            Assert.False(added.Root.GetProperty("operation").GetProperty("rosterUpdated").GetBoolean()); // …not yet on the roster
            var operationId = added.Root.GetProperty("operation").GetProperty("operationId").GetGuid();

            var seen = await fixture.Api.GetAsync($"/api/v1/teams/{teamId:D}", s.Token(s.Administrator));
            Assert.True(seen.IsSuccess, seen.Body);
            var open = Assert.Single(seen.Root.GetProperty("membershipOperations").EnumerateArray());
            Assert.Equal(operationId, open.GetProperty("operationId").GetGuid());
            Assert.False(open.GetProperty("completed").GetBoolean());
            Assert.DoesNotContain(key, seen.Body, StringComparison.Ordinal); // never the idempotency key
            Assert.Empty(seen.Root.GetProperty("members").EnumerateArray());
        }
        finally
        {
            await StartWorkersAsync();
        }

        var deadline = DateTime.UtcNow.AddSeconds(60);
        HttpResult view;
        do
        {
            view = await fixture.Api.GetAsync($"/api/v1/teams/{teamId:D}", s.Token(s.Administrator));
            Assert.True(view.IsSuccess, view.Body);
            if (view.Root.GetProperty("members").GetArrayLength() == 1)
            {
                break;
            }

            await Task.Delay(500, Ct);
        }
        while (DateTime.UtcNow < deadline);
        Assert.Equal(s.Clerk.AuthorityUserId, Assert.Single(view.Root.GetProperty("members").EnumerateArray()).GetProperty("userId").GetGuid());
        Assert.Empty(view.Root.GetProperty("membershipOperations").EnumerateArray());
        Assert.Equal(1, AuthorityCommands(s.OrganizationId, "team-add:" + key)); // nobody re-sent anything
    }

    [Fact]
    public async Task RC21AnAddEffectiveWhileALaterRemoveIsPendingIsPresentAtR1ThenAbsentAtR2()
    {
        var (s, teamId) = await TeamAsync("rc21");
        var addKey = NewKey("rc21-add");
        var removeKey = NewKey("rc21-remove");
        StopWorkers();
        var paused = false;
        try
        {
            Assert.True((await AddAsync(s, teamId, s.Designer, s.Clerk, addKey)).Root.GetProperty("command").GetProperty("effective").GetBoolean());
            fixture.Stack.Pause("archive-store");
            paused = true;
            AssertNotEffectiveYet(await RemoveAsync(s, teamId, s.Designer, s.Clerk, removeKey));
            await StartWorkersAsync();

            // Gate closed by the later removal (D8-15): the add is projected from the Authority's state at P = r1.
            var add = await WaitProjectedAsync(s, "team-add:" + addKey, 60);
            var r1 = add.GetProperty("committed_authorization_revision").GetInt64();
            Assert.Equal(r1, add.GetProperty("projected_membership_revision").GetInt64());
            Assert.False(add.GetProperty("superseded_by_later_revision").GetBoolean());
            Assert.Equal(1, ActiveRosterRows(s, teamId, s.Clerk));
            Assert.Equal((true, r1), Watermark(s, teamId, s.Clerk));
            Assert.NotEqual("Projected", State(Operation(s, "team-remove:" + removeKey)!.Value));
        }
        finally
        {
            if (paused)
            {
                fixture.Stack.Unpause("archive-store");
            }

            await StartWorkersAsync();
        }

        var remove = await WaitProjectedAsync(s, "team-remove:" + removeKey);
        Assert.Equal(0, ActiveRosterRows(s, teamId, s.Clerk));
        Assert.Equal((false, remove.GetProperty("committed_authorization_revision").GetInt64()), Watermark(s, teamId, s.Clerk));
    }

    // ---------------------------------------------------------------- RC-03 / RC-04 / RC-05 / RC-06 / RC-07

    [Fact]
    public async Task RC03AnApiRestartWhilePendingLosesNothing()
    {
        var (s, teamId) = await TeamAsync("rc03");
        var key = NewKey("rc03");
        fixture.Stack.Pause("archive-store");
        try
        {
            AssertNotEffectiveYet(await AddAsync(s, teamId, s.Designer, s.Clerk, key));
            fixture.Stack.Compose("restart api", 180);
        }
        finally
        {
            fixture.Stack.Unpause("archive-store");
        }

        await WaitProjectedAsync(s, "team-add:" + key, 120);
        Assert.Equal(1, ActiveRosterRows(s, teamId, s.Clerk));
        Assert.Equal(1, AuthorityCommands(s.OrganizationId, "team-add:" + key));
    }

    [Fact]
    public async Task RC04AWorkerThatWasAbsentCompletesTheOperationWhenItStarts()
    {
        var (s, teamId) = await TeamAsync("rc04");
        var key = NewKey("rc04");
        StopWorkers();
        try
        {
            var added = await AddAsync(s, teamId, s.Designer, s.Clerk, key);
            Assert.True(added.IsSuccess, added.Body);
            Assert.Equal("AwaitingEffective", added.Root.GetProperty("operation").GetProperty("operationState").GetString());
            await Task.Delay(3000, Ct);
            Assert.Equal(0, ActiveRosterRows(s, teamId, s.Clerk)); // the API never writes the roster (R-3)
            Assert.Equal("AwaitingEffective", State(Operation(s, "team-add:" + key)!.Value));
        }
        finally
        {
            await StartWorkersAsync();
        }

        await WaitProjectedAsync(s, "team-add:" + key);
        Assert.Equal(1, ActiveRosterRows(s, teamId, s.Clerk));
    }

    [Fact]
    public async Task RC05AnOperationLeasedByACrashedWorkerIsTakenOverOnlyAfterTheLeaseExpires()
    {
        var (s, teamId) = await TeamAsync("rc05");
        var key = NewKey("rc05");
        StopWorkers();
        try
        {
            Assert.True((await AddAsync(s, teamId, s.Designer, s.Clerk, key)).IsSuccess);
            // A worker crashed mid-lease: its lease is still valid for 12 s.
            Sql($"UPDATE authority_command_operations SET lease_owner_name = 'worker-crashed', lease_expires_on = now() + interval '12 seconds' WHERE tenant_id = '{s.OrganizationId:D}' AND authority_idempotency_key = 'team-add:{key}'");
        }
        finally
        {
            await StartWorkersAsync();
        }

        await Task.Delay(4000, Ct);
        Assert.Equal("worker-crashed", Operation(s, "team-add:" + key)!.Value.GetProperty("lease_owner_name").GetString());
        Assert.Equal(0, ActiveRosterRows(s, teamId, s.Clerk));
        await WaitProjectedAsync(s, "team-add:" + key, 60);
        Assert.Equal(1, ActiveRosterRows(s, teamId, s.Clerk));
    }

    [Fact]
    public async Task RC06ACommandCommittedBeforeTheApiRecordedItIsFoundByKey()
    {
        var (s, teamId) = await TeamAsync("rc06");
        var authorityKey = "team-add:" + NewKey("rc06");
        // The API died after the Authority committed the command and before T2: the Authority holds the command, the operation
        // row holds only T1 (no command id, AwaitingCommit).
        var revision = (await fixture.Authority.GateAsync(s.OrganizationId)).GetProperty("authorization_revision").GetInt64();
        var submitted = await fixture.Authority.SendAsync(HttpMethod.Post, "/internal/v1/security-commands/", TestIdentity.WorkloadToken(), new
        {
            organization_id = s.OrganizationId,
            command_kind = "AddTeamMember",
            resource = new { resource_kind = "Team", resource_id = teamId },
            expected_authorization_revision = revision,
            command_payload = new { team_id = teamId, member_user_id = s.Clerk.AuthorityUserId },
            change_reason = "rc06",
        }, s.Token(s.Designer), authorityKey);
        Assert.True(submitted.IsSuccess, submitted.Body);
        Sql($"""
            INSERT INTO authority_command_operations (authority_command_operation_id, tenant_id, initiated_by_user_id, command_kind_name, team_id, member_user_id, authority_idempotency_key, request_correlation_identifier, operation_created_on, operation_state_name, next_attempt_on, attempt_count, consecutive_error_count, operation_modified_on)
            VALUES (gen_random_uuid(), '{s.OrganizationId:D}', '{s.Designer.AuthorityUserId:D}', 'AddTeamMember', '{teamId:D}', '{s.Clerk.AuthorityUserId:D}', '{authorityKey}', gen_random_uuid(), now(), 'AwaitingCommit', now(), 0, 0, now())
            """);

        var row = await WaitProjectedAsync(s, authorityKey);
        Assert.Equal(submitted.Root.GetProperty("command_id").GetGuid(), row.GetProperty("authority_command_id").GetGuid());
        Assert.Equal(1, ActiveRosterRows(s, teamId, s.Clerk));
        Assert.Equal(1, AuthorityCommands(s.OrganizationId, authorityKey));
    }

    [Fact]
    public async Task RC07AnOperationTheAuthorityNeverReceivedBecomesNotCommittedAndIsNeverSent()
    {
        var (s, teamId) = await TeamAsync("rc07");
        var key = NewKey("rc07");
        var authorityKey = "team-add:" + key;
        // The API recorded T1 and died before submitting.
        Sql($"""
            INSERT INTO authority_command_operations (authority_command_operation_id, tenant_id, initiated_by_user_id, command_kind_name, team_id, member_user_id, authority_idempotency_key, request_correlation_identifier, operation_created_on, operation_state_name, next_attempt_on, attempt_count, consecutive_error_count, operation_modified_on)
            VALUES (gen_random_uuid(), '{s.OrganizationId:D}', '{s.Designer.AuthorityUserId:D}', 'AddTeamMember', '{teamId:D}', '{s.Clerk.AuthorityUserId:D}', '{authorityKey}', gen_random_uuid(), now(), 'AwaitingCommit', now(), 0, 0, now())
            """);
        var started = Stopwatch.StartNew();

        var row = await WaitOperationAsync(s, authorityKey, r => State(r) == "NotCommitted", TimeSpan.FromSeconds(90), "not NotCommitted");
        Assert.True(started.Elapsed >= TimeSpan.FromSeconds(10), "NotCommitted needs the in-flight window plus a second not-found answer a window later");
        Assert.False(row.GetProperty("first_not_found_on").ValueKind == JsonValueKind.Null);
        Assert.Equal(0, AuthorityCommands(s.OrganizationId, authorityKey));
        Assert.Equal(1, FailureAudits(s, "NotCommitted"));

        // I-8: the same key never reaches the Authority again.
        var again = await AddAsync(s, teamId, s.Designer, s.Clerk, key);
        Assert.Equal(HttpStatusCode.Conflict, again.Status);
        Assert.Contains("OPERATION_NOT_COMMITTED", again.Body, StringComparison.Ordinal);
        await Task.Delay(7000, Ct); // a late-commit sweep interval
        Assert.Equal(0, AuthorityCommands(s.OrganizationId, authorityKey));
        Assert.Equal(0, ActiveRosterRows(s, teamId, s.Clerk));
        Assert.Equal("NotCommitted", State(Operation(s, authorityKey)!.Value));
    }

    // ---------------------------------------------------------------- RC-08 / RC-09 / RC-12 / RC-13 / RC-14 / RC-15

    [Fact]
    public async Task RC08RejectedCommandsAreTerminalProjectNothingAndAnswerWithoutMembership()
    {
        var (s, teamId) = await TeamAsync("rc08");
        var selfKey = NewKey("rc08-self");
        var self = await AddAsync(s, teamId, s.Administrator, s.Administrator, selfKey); // G25
        Assert.Equal(HttpStatusCode.Forbidden, self.Status);
        var unknownKey = NewKey("rc08-unknown");
        var unknown = await fixture.Api.PostAsync($"/api/v1/teams/{teamId:D}/members", s.Token(s.Designer), new { userId = Guid.CreateVersion7() }, unknownKey);
        Assert.False(unknown.IsSuccess, unknown.Body);
        var firstKey = NewKey("rc08-first");
        Assert.True((await AddAsync(s, teamId, s.Designer, s.Clerk, firstKey)).IsSuccess);
        await WaitProjectedAsync(s, "team-add:" + firstKey);
        var duplicateKey = NewKey("rc08-duplicate");
        Assert.False((await AddAsync(s, teamId, s.Designer, s.Clerk, duplicateKey)).IsSuccess); // already a member

        foreach (var key in new[] { "team-add:" + selfKey, "team-add:" + unknownKey, "team-add:" + duplicateKey })
        {
            var row = Operation(s, key)!.Value;
            Assert.Equal("Rejected", State(row));
            Assert.False(row.GetProperty("rejection_reason_code").ValueKind == JsonValueKind.Null);
            Assert.True(row.GetProperty("projected_membership_present").ValueKind == JsonValueKind.Null);
        }

        Assert.Equal(1, ActiveRosterRows(s, teamId, s.Clerk));
        Assert.Equal(0, ActiveRosterRows(s, teamId, s.Administrator));
        var (status, body, _) = await LookupAsync(TestIdentity.ReconcilerToken(), s.OrganizationId, "team-add:" + selfKey);
        Assert.Equal(HttpStatusCode.OK, status);
        using var answer = JsonDocument.Parse(body);
        Assert.True(answer.RootElement.GetProperty("found").GetBoolean());
        Assert.Equal("Rejected", answer.RootElement.GetProperty("command_state").GetString());
        Assert.Equal(JsonValueKind.Null, answer.RootElement.GetProperty("membership").ValueKind);
    }

    [Fact]
    public async Task RC09AFailedReconciliationCommandBecomesAuthorityFailedAndProjectsNothing()
    {
        var (s, teamId) = await TeamAsync("rc09");
        var key = NewKey("rc09");
        StopWorkers();
        try
        {
            // Effective in the Authority, not yet projected (worker stopped); no Authority background step is pending for the organization,
            // so nothing rewrites the simulated state below.
            Assert.True((await AddAsync(s, teamId, s.Designer, s.Clerk, key)).Root.GetProperty("command").GetProperty("effective").GetBoolean());
            await s.WaitGateReadyAsync();
            // Simulated Authority state (no Stage A product trigger): projection divergence marks the command FailedReconciliation and quarantines the organization.
            SimulateAuthorityState($"UPDATE security_commands SET command_state_name = 'FailedReconciliation' WHERE organization_id = '{s.OrganizationId:D}' AND command_idempotency_key = 'team-add:{key}'; UPDATE organization_authorization_states SET integrity_quarantine_enabled = true, integrity_quarantine_reason_code = 'PROJECTION_DIVERGENCE' WHERE organization_id = '{s.OrganizationId:D}';");
        }
        finally
        {
            await StartWorkersAsync();
        }

        var row = await WaitOperationAsync(s, "team-add:" + key, r => State(r) == "AuthorityFailed", TimeSpan.FromSeconds(60), "not AuthorityFailed");
        Assert.Equal("FAILED_RECONCILIATION", row.GetProperty("last_reconciliation_error_code").GetString());
        Assert.Equal(0, ActiveRosterRows(s, teamId, s.Clerk));
        Assert.Null(Watermark(s, teamId, s.Clerk));
        Assert.Equal(1, FailureAudits(s, "AuthorityFailed"));
    }

    [Fact]
    public async Task RC12TheSameKeyWithAnotherRequestIsRefusedWithoutASecondOperation()
    {
        var (s, teamId) = await TeamAsync("rc12");
        var key = NewKey("rc12");
        Assert.True((await AddAsync(s, teamId, s.Designer, s.Clerk, key)).IsSuccess);
        var other = await AddAsync(s, teamId, s.Designer, s.Reader, key);
        Assert.Equal(HttpStatusCode.Conflict, other.Status);
        Assert.Contains("IDEMPOTENCY_KEY_REUSED", other.Body, StringComparison.Ordinal);
        var otherActor = await AddAsync(s, teamId, s.Administrator, s.Clerk, key);
        Assert.Equal(HttpStatusCode.Conflict, otherActor.Status);
        Assert.Equal("1", Sql($"SELECT count(*) FROM authority_command_operations WHERE tenant_id = '{s.OrganizationId:D}' AND authority_idempotency_key LIKE '%{key}'").Trim());
        Assert.Equal(1, AuthorityCommands(s.OrganizationId, "team-add:" + key));
        await WaitProjectedAsync(s, "team-add:" + key);
        Assert.Equal(0, ActiveRosterRows(s, teamId, s.Reader));
    }

    [Theory]
    [InlineData("member")]
    [InlineData("team")]
    [InlineData("other-organization-team")]
    public async Task RC13ATamperedOperationRowIsAMismatchAndProjectsNothing(string tamper)
    {
        var (s, teamId) = await TeamAsync("rc13-" + tamper);
        var other = tamper == "other-organization-team" ? await TeamAsync("rc13-other") : default;
        var key = NewKey("rc13");
        var authorityKey = "team-add:" + key;
        StopWorkers();
        try
        {
            Assert.True((await AddAsync(s, teamId, s.Designer, s.Clerk, key)).IsSuccess);
            var assignment = tamper switch
            {
                "member" => $"member_user_id = '{s.Reader.AuthorityUserId:D}'",
                "team" => $"team_id = '{(await fixture.Api.PostAsync("/api/v1/teams", s.Token(s.Designer), new { teamName = "rc13 second " + s.Name })).Root.GetProperty("teamId").GetGuid():D}'",
                _ => $"team_id = '{other.TeamId:D}'",
            };
            // The hostile BusinessDb administrator bypasses the identity guard (F-4) and rewrites the row.
            Sql($"ALTER TABLE authority_command_operations DISABLE TRIGGER trg_authority_command_operations_guard; UPDATE authority_command_operations SET {assignment} WHERE tenant_id = '{s.OrganizationId:D}' AND authority_idempotency_key = '{authorityKey}'; ALTER TABLE authority_command_operations ENABLE TRIGGER trg_authority_command_operations_guard;");
        }
        finally
        {
            await StartWorkersAsync();
        }

        var row = await WaitOperationAsync(s, authorityKey, r => State(r) == "ReconciliationError", TimeSpan.FromSeconds(60), "tampered row not refused");
        Assert.Equal("OPERATION_MISMATCH", row.GetProperty("last_reconciliation_error_code").GetString());
        Assert.False(row.GetProperty("needs_attention_flagged_on").ValueKind == JsonValueKind.Null);
        Assert.Equal("0", Sql($"SELECT count(*) FROM team_memberships WHERE created_on > now() - interval '5 minutes' AND (tenant_id = '{s.OrganizationId:D}'{(other.S is null ? string.Empty : $" OR tenant_id = '{other.S.OrganizationId:D}'")})").Trim());
        Assert.Equal("0", Sql($"SELECT count(*) FROM team_membership_projection_states WHERE tenant_id = '{s.OrganizationId:D}'").Trim());
        Assert.Equal(1, FailureAudits(s, "OPERATION_MISMATCH"));
    }

    [Fact]
    public async Task RC14AnotherOrganizationsKeyIsTheUniformNotFound()
    {
        var (s, teamId) = await TeamAsync("rc14");
        var key = NewKey("rc14");
        Assert.True((await AddAsync(s, teamId, s.Designer, s.Clerk, key)).IsSuccess);
        var other = new StageBScenario(fixture, "rc14-other");
        await other.BootstrapAsync();

        foreach (var (organization, authorityKey, kind) in new[]
                 {
                     (other.OrganizationId, "team-add:" + key, "AddTeamMember"),
                     (Guid.CreateVersion7(), "team-add:" + key, "AddTeamMember"),
                     (s.OrganizationId, "team-add:" + NewKey("rc14-unknown"), "AddTeamMember"),
                     (s.OrganizationId, "team-add:" + key, "RemoveTeamMember"),
                 })
        {
            var (status, body, _) = await LookupAsync(TestIdentity.ReconcilerToken(), organization, authorityKey, kind);
            Assert.Equal(HttpStatusCode.OK, status);
            using var answer = JsonDocument.Parse(body);
            Assert.False(answer.RootElement.GetProperty("found").GetBoolean());
            foreach (var field in new[] { "security_command_id", "command_state", "team_id", "member_user_id", "organization_state", "membership" })
            {
                Assert.Equal(JsonValueKind.Null, answer.RootElement.GetProperty(field).ValueKind);
            }
        }
    }

    [Fact]
    public async Task RC15AnInitiatorWhoLosesTheRoleWhilePendingIsMirroredWithoutANewCommand()
    {
        var (s, teamId) = await TeamAsync("rc15");
        var key = NewKey("rc15");
        StopWorkers();
        int before;
        try
        {
            Assert.True((await AddAsync(s, teamId, s.Administrator, s.Clerk, key)).Root.GetProperty("command").GetProperty("effective").GetBoolean());
            Assert.True((await fixture.Api.PostAsync("/api/v1/organization/security-administrators", s.Token(s.Administrator), new { userId = s.Reader.AuthorityUserId, reason = "rc15 rotation", useDevelopmentStepUp = true })).IsSuccess);
            Assert.True((await fixture.Api.PostAsync($"/api/v1/organization/security-administrators/{s.Administrator.AuthorityUserId:D}/remove", s.Token(s.Reader), new { reason = "rc15 rotation", useDevelopmentStepUp = true })).IsSuccess);
            before = AuthorityCommands(s.OrganizationId);
        }
        finally
        {
            await StartWorkersAsync();
        }

        await WaitProjectedAsync(s, "team-add:" + key);
        Assert.Equal(1, ActiveRosterRows(s, teamId, s.Clerk)); // the Authority decided at commit; the reconciler mirrors it
        Assert.Equal(before, AuthorityCommands(s.OrganizationId)); // no command was submitted or replayed
        Assert.Equal(HttpStatusCode.Forbidden, (await AddAsync(s, teamId, s.Administrator, s.Reader, NewKey("rc15-after"))).Status);
    }

    // ---------------------------------------------------------------- RC-10 / RC-11 (C8-M1-02)

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RC10AddAtR1AndRemoveAtR2BothEffectiveConvergeOnTheAuthorityStateInEitherOrder(bool removeFirst)
    {
        var (s, teamId) = await TeamAsync(removeFirst ? "rc10-remove-first" : "rc10-add-first");
        var addKey = "team-add:" + NewKey("rc10-add");
        var removeKey = "team-remove:" + NewKey("rc10-remove");
        StopWorkers();
        try
        {
            Assert.True((await AddAsync(s, teamId, s.Designer, s.Clerk, addKey["team-add:".Length..])).Root.GetProperty("command").GetProperty("effective").GetBoolean());
            Assert.True((await RemoveAsync(s, teamId, s.Designer, s.Clerk, removeKey["team-remove:".Length..])).Root.GetProperty("command").GetProperty("effective").GetBoolean());
            Sql($"UPDATE authority_command_operations SET next_attempt_on = now() + interval '1 hour' WHERE tenant_id = '{s.OrganizationId:D}' AND authority_idempotency_key = '{(removeFirst ? addKey : removeKey)}'");
        }
        finally
        {
            await StartWorkersAsync();
        }

        var first = await WaitProjectedAsync(s, removeFirst ? removeKey : addKey);
        Sql($"UPDATE authority_command_operations SET next_attempt_on = now() WHERE tenant_id = '{s.OrganizationId:D}' AND authority_idempotency_key = '{(removeFirst ? addKey : removeKey)}'");
        var second = await WaitProjectedAsync(s, removeFirst ? addKey : removeKey);

        var add = removeFirst ? second : first;
        var remove = removeFirst ? first : second;
        var r2 = remove.GetProperty("committed_authorization_revision").GetInt64();
        Assert.True(add.GetProperty("committed_authorization_revision").GetInt64() < r2);
        Assert.Equal(0, ActiveRosterRows(s, teamId, s.Clerk)); // the earlier add never resurrects the removed member
        Assert.Equal((false, r2), Watermark(s, teamId, s.Clerk));
        Assert.True(add.GetProperty("superseded_by_later_revision").GetBoolean());
        Assert.Equal(r2, add.GetProperty("projected_membership_revision").GetInt64());
        Assert.Equal("ProjectionUnchanged", second.GetProperty("operation_outcome_code").GetString());
    }

    [Fact]
    public async Task RC11ThreeWorkersRacingOnOnePairProduceOneRowAMonotonicWatermarkAndNoAlert()
    {
        var (s, teamId) = await TeamAsync("rc11-race");
        var keys = new[] { "team-add:" + NewKey("rc11-a"), "team-remove:" + NewKey("rc11-b"), "team-add:" + NewKey("rc11-c") };
        StopWorkers();
        try
        {
            Assert.True((await AddAsync(s, teamId, s.Designer, s.Clerk, keys[0]["team-add:".Length..])).IsSuccess);
            Assert.True((await RemoveAsync(s, teamId, s.Designer, s.Clerk, keys[1]["team-remove:".Length..])).IsSuccess);
            Assert.True((await AddAsync(s, teamId, s.Designer, s.Clerk, keys[2]["team-add:".Length..])).IsSuccess);
            await StartWorkersAsync(3); // three instances lease the three operations of the same pair at once

            // Six browser polls by two administrators meanwhile: read-only, never a re-POST.
            var polls = Enumerable.Range(0, 6).Select(i => fixture.Api.GetAsync($"/api/v1/teams/{teamId:D}", s.Token(i % 2 == 0 ? s.Designer : s.Administrator))).ToList();
            var rows = new List<JsonElement>();
            foreach (var key in keys)
            {
                rows.Add(await WaitProjectedAsync(s, key));
            }

            Assert.All(await Task.WhenAll(polls), p => Assert.True(p.IsSuccess, p.Body));
            var r3 = rows[2].GetProperty("committed_authorization_revision").GetInt64();
            Assert.Equal(1, ActiveRosterRows(s, teamId, s.Clerk));
            Assert.Equal((true, r3), Watermark(s, teamId, s.Clerk));
            Assert.True(rows[0].GetProperty("superseded_by_later_revision").GetBoolean());
            Assert.True(rows[1].GetProperty("superseded_by_later_revision").GetBoolean());
            Assert.False(rows[2].GetProperty("superseded_by_later_revision").GetBoolean());
            Assert.Equal(0, FailureAudits(s));
            Assert.Equal("0", Sql($"SELECT count(*) FROM authority_command_operations WHERE tenant_id = '{s.OrganizationId:D}' AND operation_state_name = 'ReconciliationError'").Trim());
            foreach (var key in keys)
            {
                Assert.Equal(1, AuthorityCommands(s.OrganizationId, key));
            }
        }
        finally
        {
            fixture.Stack.Compose("up -d --no-deps --scale worker=1 worker", 240);
        }
    }

    [Fact]
    public async Task RC11AStaleAnswerOvertakenByAnotherWritersProjectionIsReLookedUpAndNotAlerted()
    {
        var (s, teamId) = await TeamAsync("rc11-stale");
        var addKey = NewKey("rc11-stale-add");
        var removeKey = NewKey("rc11-stale-remove");
        Sql("CREATE TABLE IF NOT EXISTS c8_test_signals (signal_key text PRIMARY KEY)");
        StopWorkers();
        Task<string>? holder = null;
        try
        {
            Assert.True((await AddAsync(s, teamId, s.Designer, s.Clerk, addKey)).Root.GetProperty("command").GetProperty("effective").GetBoolean());
            Sql($"INSERT INTO team_membership_projection_states (team_membership_projection_state_id, tenant_id, team_id, member_user_id, projected_membership_present, projected_membership_revision, projection_state_modified_on) VALUES (gen_random_uuid(), '{s.OrganizationId:D}', '{teamId:D}', '{s.Clerk.AuthorityUserId:D}', false, 0, now())");
            // "Another writer" holds the pair lock, waits until the removal is Effective, then commits its projection of it (W = r2).
            holder = Task.Run(() => Sql($"""
                BEGIN;
                SELECT 1 FROM team_membership_projection_states WHERE tenant_id = '{s.OrganizationId:D}' AND team_id = '{teamId:D}' AND member_user_id = '{s.Clerk.AuthorityUserId:D}' FOR UPDATE;
                DO $wait$ BEGIN LOOP EXIT WHEN EXISTS (SELECT 1 FROM c8_test_signals WHERE signal_key = '{removeKey}'); PERFORM pg_sleep(0.2); END LOOP; END $wait$;
                UPDATE team_membership_projection_states SET projected_membership_present = false, projected_membership_revision = (SELECT committed_authorization_revision FROM authority_command_operations WHERE tenant_id = '{s.OrganizationId:D}' AND authority_idempotency_key = 'team-remove:{removeKey}'), projection_state_modified_on = now() WHERE tenant_id = '{s.OrganizationId:D}' AND team_id = '{teamId:D}' AND member_user_id = '{s.Clerk.AuthorityUserId:D}';
                UPDATE team_memberships SET removed_on = now() WHERE tenant_id = '{s.OrganizationId:D}' AND team_id = '{teamId:D}' AND user_id = '{s.Clerk.AuthorityUserId:D}' AND removed_on IS NULL;
                COMMIT;
                """), Ct);
            await StartWorkersAsync();

            // The worker has looked up the add (E = r1, present) and now waits for the pair lock.
            var deadline = DateTime.UtcNow.AddSeconds(60);
            while (Sql("SELECT count(*) FROM pg_stat_activity WHERE wait_event_type = 'Lock' AND query ILIKE '%team_membership_projection_states%FOR UPDATE%'").Trim() == "0")
            {
                Assert.True(DateTime.UtcNow < deadline, "the worker never waited for the pair lock");
                await Task.Delay(300, Ct);
            }

            var removed = await RemoveAsync(s, teamId, s.Designer, s.Clerk, removeKey);
            Assert.True(removed.Root.GetProperty("command").GetProperty("effective").GetBoolean(), removed.Body);
            Sql($"INSERT INTO c8_test_signals (signal_key) VALUES ('{removeKey}')");
            await holder;
        }
        finally
        {
            Sql($"INSERT INTO c8_test_signals (signal_key) VALUES ('{removeKey}') ON CONFLICT DO NOTHING");
            if (holder is not null)
            {
                await Task.WhenAny(holder, Task.Delay(TimeSpan.FromSeconds(30), Ct));
            }

            await StartWorkersAsync();
        }

        var add = await WaitProjectedAsync(s, "team-add:" + addKey);
        Assert.Equal("ProjectionUnchanged", add.GetProperty("operation_outcome_code").GetString());
        Assert.True(add.GetProperty("superseded_by_later_revision").GetBoolean());
        Assert.Equal(0, ActiveRosterRows(s, teamId, s.Clerk)); // the stale "present" was never written
        Assert.Equal(0, FailureAudits(s)); // R-6 / I-11: a benign race is never an alert
        Assert.DoesNotContain("LOCAL_WATERMARK_AHEAD", fixture.Stack.Logs("worker", 300), StringComparison.Ordinal);
    }

    [Fact]
    public async Task RC11AFaultInsideTheApplyTransactionRollsBackRosterWatermarkAndCompletionTogether()
    {
        var (s, teamId) = await TeamAsync("rc11-fault");
        var key = NewKey("rc11-fault");
        var function = "c8_fault_" + Guid.NewGuid().ToString("N")[..12];
        StopWorkers();
        try
        {
            Assert.True((await AddAsync(s, teamId, s.Designer, s.Clerk, key)).IsSuccess);
            // Fault injected between the statements of the apply transaction: the watermark UPDATE fails after the roster INSERT.
            Sql($"""
                CREATE FUNCTION {function}() RETURNS trigger LANGUAGE plpgsql AS $f$ BEGIN RAISE EXCEPTION 'injected C8 fault'; END $f$;
                CREATE TRIGGER {function} BEFORE UPDATE ON team_membership_projection_states FOR EACH ROW WHEN (NEW.team_id = '{teamId:D}') EXECUTE FUNCTION {function}();
                """);
            await StartWorkersAsync();
            await WaitOperationAsync(s, "team-add:" + key, r => r.GetProperty("consecutive_error_count").GetInt32() >= 1, TimeSpan.FromSeconds(60), "the injected fault was never hit");
            var row = Operation(s, "team-add:" + key)!.Value;
            Assert.NotEqual("Projected", State(row));
            Assert.StartsWith("RECONCILER_STEP_FAILED", row.GetProperty("last_reconciliation_error_code").GetString(), StringComparison.Ordinal);
            Assert.Equal(0, ActiveRosterRows(s, teamId, s.Clerk)); // roster rolled back
            Assert.Null(Watermark(s, teamId, s.Clerk)); // watermark (even its first insert) rolled back
        }
        finally
        {
            Sql($"DROP TRIGGER IF EXISTS {function} ON team_membership_projection_states; DROP FUNCTION IF EXISTS {function}();");
            await StartWorkersAsync();
        }

        await WaitProjectedAsync(s, "team-add:" + key);
        Assert.Equal(1, ActiveRosterRows(s, teamId, s.Clerk));
    }

    // ---------------------------------------------------------------- RC-17 / RC-19 / RC-23 / RC-24 / RC-25

    [Fact]
    public async Task RC17AnOverdueOperationIsFlaggedAndCheckAgainIsAuditedAndNeverSubmits()
    {
        var (s, teamId) = await TeamAsync("rc17");
        var key = NewKey("rc17");
        var authorityKey = "team-add:" + key;
        StopWorkers();
        try
        {
            Assert.True((await AddAsync(s, teamId, s.Designer, s.Clerk, key)).IsSuccess);
            Sql($"ALTER TABLE authority_command_operations DISABLE TRIGGER trg_authority_command_operations_guard; UPDATE authority_command_operations SET member_user_id = '{s.Reader.AuthorityUserId:D}' WHERE tenant_id = '{s.OrganizationId:D}' AND authority_idempotency_key = '{authorityKey}'; ALTER TABLE authority_command_operations ENABLE TRIGGER trg_authority_command_operations_guard;");
        }
        finally
        {
            await StartWorkersAsync();
        }

        var failed = await WaitOperationAsync(s, authorityKey, r => State(r) == "ReconciliationError", TimeSpan.FromSeconds(60), "no reconciliation error");
        var operationId = failed.GetProperty("authority_command_operation_id").GetGuid();
        var detail = await fixture.Api.GetAsync($"/api/v1/teams/{teamId:D}", s.Token(s.Administrator));
        var listed = Assert.Single(detail.Root.GetProperty("membershipOperations").EnumerateArray());
        Assert.True(listed.GetProperty("needsAttention").GetBoolean());
        Assert.True(listed.GetProperty("canRecheck").GetBoolean());

        var recheck = await fixture.Api.PostAsync($"/api/v1/teams/{teamId:D}/membership-operations/{operationId:D}/recheck", s.Token(s.Administrator), new { });
        Assert.True(recheck.IsSuccess, recheck.Body);
        Assert.Equal("1", Sql($"SELECT count(*) FROM audit_events WHERE tenant_id = '{s.OrganizationId:D}' AND event_action = 'TEAM_MEMBERSHIP_OPERATION_RECHECKED'").Trim());
        await WaitOperationAsync(s, authorityKey, r => State(r) == "ReconciliationError" && r.GetProperty("attempt_count").GetInt32() > failed.GetProperty("attempt_count").GetInt32(), TimeSpan.FromSeconds(60), "recheck did not look up again");
        Assert.Equal(1, AuthorityCommands(s.OrganizationId, authorityKey)); // checking again never submitted anything

        // Repaired row: a further check projects through the normal validation.
        Sql($"ALTER TABLE authority_command_operations DISABLE TRIGGER trg_authority_command_operations_guard; UPDATE authority_command_operations SET member_user_id = '{s.Clerk.AuthorityUserId:D}' WHERE tenant_id = '{s.OrganizationId:D}' AND authority_idempotency_key = '{authorityKey}'; ALTER TABLE authority_command_operations ENABLE TRIGGER trg_authority_command_operations_guard;");
        Assert.True((await fixture.Api.PostAsync($"/api/v1/teams/{teamId:D}/membership-operations/{operationId:D}/recheck", s.Token(s.Designer), new { })).IsSuccess);
        await WaitProjectedAsync(s, authorityKey);
        Assert.Equal(1, ActiveRosterRows(s, teamId, s.Clerk));
        Assert.Equal(HttpStatusCode.Conflict, (await fixture.Api.PostAsync($"/api/v1/teams/{teamId:D}/membership-operations/{operationId:D}/recheck", s.Token(s.Designer), new { })).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await fixture.Api.PostAsync($"/api/v1/teams/{teamId:D}/membership-operations/{operationId:D}/recheck", s.Token(s.Clerk), new { })).Status);

        // Time-based flag: an operation still awaiting the Authority after the attention threshold is flagged.
        var slowKey = NewKey("rc17-slow");
        fixture.Stack.Pause("archive-store");
        try
        {
            AssertNotEffectiveYet(await AddAsync(s, teamId, s.Designer, s.Reader, slowKey));
            await WaitOperationAsync(s, "team-add:" + slowKey, r => r.GetProperty("needs_attention_flagged_on").ValueKind != JsonValueKind.Null, TimeSpan.FromSeconds(60), "overdue operation not flagged");
        }
        finally
        {
            fixture.Stack.Unpause("archive-store");
        }

        await WaitProjectedAsync(s, "team-add:" + slowKey, 120);
    }

    [Fact]
    public async Task RC19TheRuntimeRoleCannotDeleteRewriteIdentitiesReopenTerminalRowsOrLowerTheWatermark()
    {
        var (s, teamId) = await TeamAsync("rc19");
        var key = NewKey("rc19");
        Assert.True((await AddAsync(s, teamId, s.Designer, s.Clerk, key)).IsSuccess);
        await WaitProjectedAsync(s, "team-add:" + key);
        var role = Sql("SELECT grantee FROM information_schema.role_table_grants WHERE table_name = 'authority_command_operations' AND privilege_type = 'INSERT' AND grantee NOT IN (SELECT rolname FROM pg_roles WHERE rolsuper) LIMIT 1").Trim();
        Assert.False(string.IsNullOrEmpty(role));
        string AsRuntime(string sql) => fixture.Stack.AttackerSql($"SET ROLE {role}; {sql}", throwOnError: false);
        var where = $"tenant_id = '{s.OrganizationId:D}' AND authority_idempotency_key = 'team-add:{key}'";

        Assert.Contains("permission denied", AsRuntime($"DELETE FROM authority_command_operations WHERE {where}"), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("permission denied", AsRuntime($"DELETE FROM team_membership_projection_states WHERE tenant_id = '{s.OrganizationId:D}'"), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("permission denied", AsRuntime($"UPDATE authority_command_operations SET member_user_id = '{s.Reader.AuthorityUserId:D}' WHERE {where}"), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("permission denied", AsRuntime($"UPDATE team_membership_projection_states SET member_user_id = '{s.Reader.AuthorityUserId:D}' WHERE tenant_id = '{s.OrganizationId:D}'"), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("AUTHORITY_COMMAND_OPERATION_TERMINAL", AsRuntime($"UPDATE authority_command_operations SET operation_state_name = 'AwaitingEffective' WHERE {where}"), StringComparison.Ordinal);
        Assert.Contains("AUTHORITY_COMMAND_OPERATION_COMMAND_IMMUTABLE", AsRuntime($"UPDATE authority_command_operations SET authority_command_id = gen_random_uuid() WHERE {where}"), StringComparison.Ordinal);
        Assert.Contains("TEAM_MEMBERSHIP_PROJECTION_WATERMARK_LOWERED", AsRuntime($"UPDATE team_membership_projection_states SET projected_membership_revision = 0 WHERE tenant_id = '{s.OrganizationId:D}'"), StringComparison.Ordinal);
        Assert.Equal("Projected", State(Operation(s, "team-add:" + key)!.Value));
        Assert.Equal("f", Sql("SELECT has_function_privilege('public', 'public.authority_command_operations_guard()', 'EXECUTE')").Trim());
    }

    [Fact]
    public async Task RC19TheMigrationGoesDownAndUpCleanlyOnAScratchDatabase()
    {
        var root = ComposeStack.RepositoryRoot;
        var project = Path.Combine(root, "src", "backend", "EnterpriseForms.Infrastructure");
        var scripts = Path.Combine(Path.GetTempPath(), "c8-rc19-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scripts);
        const string Previous = "20261029154322_PhaseC7ReconciliationResumableRounds";
        const string Target = "20261030130521_PhaseC8TeamMembershipReconciliation";
        // dotnet-ef comes from the repository tool manifest (.config/dotnet-tools.json, pinned); hosted runners have not restored
        // it, so restore it here (idempotent; a no-op where it is already restored). A failed restore fails the test.
        ComposeStack.Run("dotnet", "tool restore", 300);
        string Script(string name, string from, string to)
        {
            var file = Path.Combine(scripts, name);
            ComposeStack.Run("dotnet", $"ef migrations script {from} {to} --project \"{project}\" --startup-project \"{project}\" --output \"{file}\"", 600);
            fixture.Stack.Compose($"cp \"{file}\" attacker-sql:/tmp/{name}", 60);
            return name;
        }

        var down = Script("rc19-down.sql", Target, Previous);
        var up = Script("rc19-up.sql", Previous, Target);
        var database = "c8_rc19_" + Guid.NewGuid().ToString("N")[..10];
        string Scratch(string sql) => fixture.Stack.AttackerShell($"psql -d {database} -v ON_ERROR_STOP=1 -qAt -c \"{sql}\"");
        string Objects() => Scratch("SELECT (SELECT count(*) FROM pg_tables WHERE tablename IN ('authority_command_operations','team_membership_projection_states')) || '/' || (SELECT count(*) FROM pg_proc WHERE proname IN ('authority_command_operations_guard','team_membership_projection_states_guard')) || '/' || (SELECT count(*) FROM pg_trigger WHERE tgname IN ('trg_authority_command_operations_guard','trg_team_membership_projection_states_guard'))").Trim();
        try
        {
            Sql($"CREATE DATABASE {database}");
            // Start from the live, fully migrated BusinessDb schema (schema only, no data). A from-empty idempotent script is not
            // used: an older, unrelated migration's raw SQL does not script cleanly.
            fixture.Stack.AttackerShell($"pg_dump --schema-only --no-owner --no-privileges -d \"$PGDATABASE\" | psql -d {database} -v ON_ERROR_STOP=1 -q", timeoutSeconds: 300);
            Assert.Equal("2/2/2", Objects());
            fixture.Stack.AttackerShell($"psql -d {database} -v ON_ERROR_STOP=1 -q -f /tmp/{down}", timeoutSeconds: 120);
            Assert.Equal("0/0/0", Objects());
            fixture.Stack.AttackerShell($"psql -d {database} -v ON_ERROR_STOP=1 -q -f /tmp/{up}", timeoutSeconds: 120);
            Assert.Equal("2/2/2", Objects());
        }
        finally
        {
            Sql($"DROP DATABASE IF EXISTS {database}");
            Directory.Delete(scripts, recursive: true);
        }
    }

    [Fact]
    public async Task RC23AWatermarkRaisedByDirectSqlIsDetectedAfterAFreshLookupAndNothingIsProjected()
    {
        var (s, teamId) = await TeamAsync("rc23");
        var key = NewKey("rc23");
        StopWorkers();
        try
        {
            Assert.True((await AddAsync(s, teamId, s.Designer, s.Clerk, key)).IsSuccess);
            Sql($"INSERT INTO team_membership_projection_states (team_membership_projection_state_id, tenant_id, team_id, member_user_id, projected_membership_present, projected_membership_revision, projection_state_modified_on) VALUES (gen_random_uuid(), '{s.OrganizationId:D}', '{teamId:D}', '{s.Clerk.AuthorityUserId:D}', false, 999999, now())");
        }
        finally
        {
            await StartWorkersAsync();
        }

        var row = await WaitOperationAsync(s, "team-add:" + key, r => State(r) == "ReconciliationError", TimeSpan.FromSeconds(60), "raised watermark not detected");
        Assert.Equal("LOCAL_WATERMARK_AHEAD", row.GetProperty("last_reconciliation_error_code").GetString());
        Assert.Equal(0, ActiveRosterRows(s, teamId, s.Clerk));
        Assert.Equal((false, 999999L), Watermark(s, teamId, s.Clerk));
        Assert.Equal(1, FailureAudits(s, "LOCAL_WATERMARK_AHEAD"));
        Assert.Contains("ALERT team membership reconciliation anomaly LOCAL_WATERMARK_AHEAD", fixture.Stack.Logs("worker", 400), StringComparison.Ordinal);
    }

    [Fact]
    public async Task RC24QuarantineProjectsNothingAndResumesAfterTheAuthoritysClearance()
    {
        var (s, teamId) = await TeamAsync("rc24");
        var key = NewKey("rc24");
        StopWorkers();
        try
        {
            Assert.True((await AddAsync(s, teamId, s.Designer, s.Clerk, key)).IsSuccess);
            SimulateAuthorityState($"UPDATE organization_authorization_states SET integrity_quarantine_enabled = true, integrity_quarantine_reason_code = 'C8_RC24_SIMULATION' WHERE organization_id = '{s.OrganizationId:D}'");
        }
        finally
        {
            await StartWorkersAsync();
        }

        try
        {
            await WaitOperationAsync(s, "team-add:" + key, r => State(r) == "AwaitingQuarantineClearance", TimeSpan.FromSeconds(60), "quarantine not honoured");
            var (status, body, _) = await LookupAsync(TestIdentity.ReconcilerToken(), s.OrganizationId, "team-add:" + key);
            Assert.Equal(HttpStatusCode.OK, status);
            using (var answer = JsonDocument.Parse(body))
            {
                Assert.True(answer.RootElement.GetProperty("organization_state").GetProperty("integrity_quarantined").GetBoolean());
                Assert.Equal(JsonValueKind.Null, answer.RootElement.GetProperty("membership").ValueKind);
            }

            var operationId = Operation(s, "team-add:" + key)!.Value.GetProperty("authority_command_operation_id").GetGuid();
            Sql($"UPDATE authority_command_operations SET next_attempt_on = now() WHERE authority_command_operation_id = '{operationId:D}'"); // an operator "check again" still projects nothing
            await Task.Delay(4000, Ct);
            Assert.Equal("AwaitingQuarantineClearance", State(Operation(s, "team-add:" + key)!.Value));
            Assert.Equal(0, ActiveRosterRows(s, teamId, s.Clerk));
            Assert.Null(Watermark(s, teamId, s.Clerk));
        }
        finally
        {
            // Simulated Authority clearance (no Stage A product path).
            SimulateAuthorityState($"UPDATE organization_authorization_states SET integrity_quarantine_enabled = false, integrity_quarantine_reason_code = NULL WHERE organization_id = '{s.OrganizationId:D}'");
        }

        await WaitProjectedAsync(s, "team-add:" + key, 60);
        Assert.Equal(1, ActiveRosterRows(s, teamId, s.Clerk));
    }

    [Fact]
    public async Task RC25ARotatedReconcilerCredentialMakesTheReconcilerUnavailableAndChangesNoOperation()
    {
        var (s, teamId) = await TeamAsync("rc25");
        var key = NewKey("rc25");
        StopWorkers();
        Environment.SetEnvironmentVariable("RECONCILER_TESTING_WORKER_KEY", Convert.ToBase64String(Encoding.UTF8.GetBytes("rotated-reconciler-key-unknown-to-authority")));
        try
        {
            Assert.True((await AddAsync(s, teamId, s.Designer, s.Clerk, key)).IsSuccess);
            var before = Operation(s, "team-add:" + key)!.Value;
            fixture.Stack.Compose("up -d --no-deps --force-recreate worker", 240);
            var deadline = DateTime.UtcNow.AddSeconds(90);
            while (!fixture.Stack.Logs("worker", 300).Contains("ALERT the Authority refuses the team membership reconciler credential", StringComparison.Ordinal))
            {
                Assert.True(DateTime.UtcNow < deadline, "no credential alert");
                await Task.Delay(1000, Ct);
            }

            var after = Operation(s, "team-add:" + key)!.Value;
            foreach (var column in new[] { "operation_state_name", "attempt_count", "consecutive_error_count", "last_reconciliation_error_code", "operation_outcome_code" })
            {
                Assert.Equal(before.GetProperty(column).GetRawText(), after.GetProperty(column).GetRawText());
            }

            Assert.Equal(0, ActiveRosterRows(s, teamId, s.Clerk));
        }
        finally
        {
            Environment.SetEnvironmentVariable("RECONCILER_TESTING_WORKER_KEY", null);
            fixture.Stack.Compose("up -d --no-deps --force-recreate worker", 240);
        }

        await WaitProjectedAsync(s, "team-add:" + key, 120);
        Assert.Equal(1, ActiveRosterRows(s, teamId, s.Clerk));
    }

    // ---------------------------------------------------------------- RC-18 / RC-26 / RC-27 (AC-9 on the live Authority)

    [Fact]
    public async Task RC18OnTheLiveAuthorityOnlyTheReconcilerCredentialAnswersAndALookupChangesNothing()
    {
        var (s, teamId) = await TeamAsync("rc18");
        var key = NewKey("rc18");
        Assert.True((await AddAsync(s, teamId, s.Designer, s.Clerk, key)).IsSuccess);
        await WaitProjectedAsync(s, "team-add:" + key);
        var authorityKey = "team-add:" + key;

        foreach (var token in new[] { TestIdentity.WorkloadToken(), TestIdentity.VerifierToken(), TestIdentity.OperatorToken(), TestIdentity.ReconcilerToken(role: "business-api"), TestIdentity.ReconcilerToken(keyBase64: Convert.ToBase64String(new byte[32])) })
        {
            Assert.Contains((await LookupAsync(token, s.OrganizationId, authorityKey)).Status, new[] { HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden });
        }

        Assert.Equal(HttpStatusCode.Unauthorized, (await LookupAsync(null, s.OrganizationId, authorityKey)).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await LookupAsync(TestIdentity.ReconcilerToken(), s.OrganizationId, authorityKey, assertion: s.Token(s.Designer))).Status);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await LookupAsync(TestIdentity.ReconcilerToken(), s.OrganizationId, authorityKey, kind: "RegisterTeam")).Status);

        string Counts() => fixture.Stack.AuthorityObserverSql($"SELECT authorization_revision || '/' || projection_applied_revision || '/' || (SELECT count(*) FROM security_journal_entries WHERE organization_id = '{s.OrganizationId:D}') || '/' || (SELECT count(*) FROM projection_outbox_events WHERE organization_id = '{s.OrganizationId:D}') || '/' || (SELECT count(*) FROM security_commands WHERE organization_id = '{s.OrganizationId:D}') FROM organization_authorization_states WHERE organization_id = '{s.OrganizationId:D}'").Trim();
        var before = Counts();
        var correlation = Guid.CreateVersion7();
        var (status, body, response) = await LookupAsync(TestIdentity.ReconcilerToken(), s.OrganizationId, authorityKey, correlation: correlation);
        for (var i = 0; i < 5; i++)
        {
            await LookupAsync(TestIdentity.ReconcilerToken(), s.OrganizationId, authorityKey);
        }

        Assert.Equal(before, Counts()); // no revision, journal, outbox or command row
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(correlation.ToString("D"), response.Headers.GetValues("X-Correlation-Id").Single());
        using var answer = JsonDocument.Parse(body);
        Assert.True(answer.RootElement.GetProperty("membership").GetProperty("effective_member").GetBoolean());
        var names = answer.RootElement.EnumerateObject().Select(p => p.Name).ToList();
        Assert.DoesNotContain(names, n => n.Contains("actor", StringComparison.Ordinal) || n.Contains("issuer", StringComparison.Ordinal) || n.Contains("subject", StringComparison.Ordinal) || n.Contains("reason_text", StringComparison.Ordinal) || n.Contains("idempotency", StringComparison.Ordinal));

        var logs = fixture.Stack.Logs("authority", 2000);
        Assert.Contains("AC-9 lookup", logs, StringComparison.Ordinal);
        Assert.Contains("AC-9 lookup refused with status", logs, StringComparison.Ordinal);
        Assert.DoesNotContain(key, logs, StringComparison.Ordinal); // C-3: never the key
        foreach (var line in logs.Split('\n').Where(l => l.Contains("AC-9 lookup", StringComparison.Ordinal)))
        {
            Assert.DoesNotContain(teamId.ToString("D"), line, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(s.Clerk.AuthorityUserId.ToString("D"), line, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void RC26TheRunningApiContainersHoldNoReconcilerCredential()
    {
        foreach (var service in new[] { "api", "api-secondary" })
        {
            var id = fixture.Stack.Compose($"ps -q {service}", 60).Trim();
            var environment = ComposeStack.Run("docker", $"inspect --format \"{{{{json .Config.Env}}}}\" {id}", 60);
            Assert.DoesNotContain("TeamMembershipReconciler__", environment, StringComparison.Ordinal);
        }

        var worker = fixture.Stack.Compose("ps -q worker", 60).Trim().Split('\n')[0].Trim();
        Assert.Contains("TeamMembershipReconciler__SharedKeyBase64", ComposeStack.Run("docker", $"inspect --format \"{{{{json .Config.Env}}}}\" {worker}", 60), StringComparison.Ordinal);
    }

    [Fact]
    public async Task RC27TheLiveRateLimitAnswers429WithRetryAfterAndNeverFails()
    {
        var organization = Guid.CreateVersion7();
        var results = await Task.WhenAll(Enumerable.Range(0, 80).Select(_ => LookupAsync(TestIdentity.ReconcilerToken(), organization, "team-add:" + NewKey("rc27"))));
        Assert.Contains(results, r => r.Status == HttpStatusCode.TooManyRequests);
        Assert.All(results, r => Assert.Contains(r.Status, new[] { HttpStatusCode.OK, HttpStatusCode.TooManyRequests }));
        var limited = results.First(r => r.Status == HttpStatusCode.TooManyRequests);
        Assert.True(limited.Response.Headers.RetryAfter?.Delta >= TimeSpan.FromSeconds(1));
        Assert.Contains("RATE_LIMITED", limited.Body, StringComparison.Ordinal);
        await Task.Delay(3000, Ct); // let the bucket refill for the reconciler
    }
}
