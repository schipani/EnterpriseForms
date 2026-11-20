using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Text.Json;
using EnterpriseForms.SecurityTests.Harness;
using EnterpriseForms.StageBTests.Harness;
using Xunit;

namespace EnterpriseForms.StageBTests.Tests;

/// <summary>
/// C8 M4-3 (AC-8.2) on the real stack — Authority, both PostgreSQL databases, SpiceDB, Vault, business API:
/// <list type="bullet">
/// <item>T08 / AC-M4-3.1: envelopes are restricted field by field. Commands created through the real API carry sentinels in every
/// prohibited place: a reason note, a transfer reason, a share's field scope, a rejected self-appointment's note, and a command row
/// inserted by the AuthorityDb administrator with a raw payload and reason text. No response contains a sentinel, and every
/// item's detail keys are exactly its kind's allowlist. Effective, Rejected (journal-backed and command-row-only) and
/// FailedReconciliation (set by the administrator) rows are shown; excluded kinds and attached rejection entries are never items.</item>
/// <item>T09: another organization's auditor sees none of these items; a cursor is bound to caller, organization and filter;
/// non-auditors are refused; a revoked auditor's next page is refused.</item>
/// <item>The keyset (PostgreSQL row comparison) and the kind and state filters, at the database.</item>
/// <item>T22-style: a revocation commits while timeline pages are read at the rate limit; durations are measured and written to
/// the test output.</item>
/// </list>
/// T10 (commit fault) and T20 (revision moved between check and lock) stay unit-only (AuditAccessServiceTests,
/// SecurityCommandTimelineTests in the Authority tests): this harness has no fault hooks. Authored in M4-3; not yet executed.
/// </summary>
[Collection(StageBStackCollectionDefinition.Name)]
public sealed class SecurityCommandTimelineTests(StageBFixture fixture)
{
    private const string Timeline = "/api/v1/security/audit/security-commands";
    private const string Auditors = "/api/v1/organization/security-auditors";

    private static readonly Dictionary<string, string[]> AllowedKeys = new(StringComparer.Ordinal)
    {
        ["GrantSystemRole"] = ["target_user_id", "role_name", "reason_code"],
        ["RevokeSystemRole"] = ["target_user_id", "role_name", "reason_code"],
        ["RegisterTeam"] = ["team_id"],
        ["AddTeamMember"] = ["team_id", "member_user_id"],
        ["RemoveTeamMember"] = ["team_id", "member_user_id"],
        ["CreateShare"] = ["form_record_id", "record_share_id", "recipient", "recipient_role"],
        ["RevokeShare"] = ["form_record_id", "record_share_id"],
        ["TransferOwnership"] = ["form_record_id", "new_owner", "previous_owner"],
        ["UpdateFormUsers"] = ["form_definition_id", "added_principals", "removed_principals"],
        ["AuditAccessAuthorized"] = ["access_kind", "actor_authority_user_id", "authorization_revision", "projection_revision", "filter_digest", "page_size", "audit_event_id", "delivery", "served_by", "correlation_id"],
    };

    private static readonly string[] ExcludedKinds = ["RegisterRecord", "UnlinkChildRecord", "RegisterFormDefinition", "RegisterPublishedFormVersion", "SecurityCommandCommitted", "SecurityAuditorCommandRejected", "SecurityAdministratorCommandRejected", "MembershipCommandRejected", "FieldSearchCommandRejected"];

    private async Task<StageBScenario> AuditorScenarioAsync(string name, string reasonNote = "routine coverage")
    {
        var s = new StageBScenario(fixture, name);
        await s.BootstrapAsync();
        var appointed = await fixture.Api.PostAsync(Auditors, s.Token(s.Administrator), new { userId = s.Reader.AuthorityUserId, reasonCode = "AuditCoverageAssignment", reasonNote, useDevelopmentStepUp = true });
        Assert.Equal(HttpStatusCode.NoContent, appointed.Status);
        return s;
    }

    /// <summary>A read as a compliant client: a busy refusal (nothing served) is retried after a pause, at most three times, and noted.</summary>
    private async Task<HttpResult> ReadAsync(string path, string token)
    {
        for (var retry = 0; ; retry++)
        {
            var result = await fixture.Api.GetAsync(path, token);
            if (result.Status != HttpStatusCode.ServiceUnavailable || result.ErrorCode != "AUDIT_EVIDENCE_BUSY" || retry == 3)
            {
                return result;
            }

            TestContext.Current.TestOutputHelper?.WriteLine($"AUDIT_EVIDENCE_BUSY on the timeline (retry {retry + 1})");
            await Task.Delay(250, TestContext.Current.CancellationToken);
        }
    }

    private async Task<(List<JsonElement> Items, List<string> Bodies)> AllPagesAsync(StageBScenario s, TestUser user, string query = "pageSize=50")
    {
        var items = new List<JsonElement>();
        var bodies = new List<string>();
        string? cursor = null;
        do
        {
            var page = await ReadAsync(Timeline + "?" + query + (cursor is null ? string.Empty : "&cursor=" + Uri.EscapeDataString(cursor)), s.Token(user));
            Assert.True(page.IsSuccess, page.Body);
            bodies.Add(page.Body);
            items.AddRange(page.Root.GetProperty("items").EnumerateArray().Select(i => i.Clone()));
            cursor = page.Root.GetProperty("nextCursor").GetString();
        }
        while (cursor is not null);
        return (items, bodies);
    }

    private static HashSet<string> Keys(JsonElement item) => item.GetProperty("details").EnumerateArray().Select(d => d.GetProperty("key").GetString()!).ToHashSet(StringComparer.Ordinal);

    private static string? Detail(JsonElement item, string key) => item.GetProperty("details").EnumerateArray().Where(d => d.GetProperty("key").GetString() == key).Select(d => d.GetProperty("value").GetString()).FirstOrDefault();

    private string BindingOf(StageBScenario s, TestUser user) =>
        fixture.Stack.AuthorityObserverSql($"SELECT identity_binding_id FROM identity_bindings WHERE organization_id = '{s.OrganizationId:D}' AND authority_user_id = '{user.AuthorityUserId:D}';").Trim();

    /// <summary>T08, AC-M4-3.1: every item's details are exactly its allowlist; no sentinel from any prohibited place appears.</summary>
    [Fact]
    public async Task TimelineEnvelopesAreRestrictedFieldByFieldAndCarryNoSentinel()
    {
        var s = await AuditorScenarioAsync("tl-t08", reasonNote: "SENT-REASON-NOTE");
        await s.BuildAccusedWorkspaceAsync();

        // A refused self-appointment: Rejected, with its audit-stream rejection entry as journal evidence.
        var self = await fixture.Api.PostAsync(Auditors, s.Token(s.Administrator), new { userId = s.Administrator.AuthorityUserId, reasonCode = "AuditCoverageAssignment", reasonNote = "SENT-REJECTED-NOTE", useDevelopmentStepUp = true });
        Assert.False(self.IsSuccess);

        var team = await fixture.Api.PostAsync("/api/v1/teams", s.Token(s.Designer), new { teamName = "SENT-TEAM-NAME " + s.Name });
        Assert.True(team.IsSuccess, team.Body);
        var teamId = team.Root.GetProperty("teamId").GetGuid();
        Assert.True((await fixture.Api.PostAsync($"/api/v1/teams/{teamId:D}/members", s.Token(s.Designer), new { userId = s.Clerk.AuthorityUserId })).IsSuccess);

        var record = await s.CreateAccusedAsync(s.Clerk, new Dictionary<string, string> { ["given_name"] = "SENT-CONFIDENTIAL-VALUE", ["family_name"] = "Doe" });
        var share = await fixture.Api.PostAsync($"/api/v1/form-records/{record:D}/shares", s.Token(s.Clerk), new { recipientUserId = s.Designer.AuthorityUserId, recipientTeamId = (Guid?)null, recipientRole = "Reader", fieldKeys = new[] { "given_name" } });
        Assert.True(share.IsSuccess, share.Body);
        var shareId = share.Root.GetProperty("recordShareId").GetGuid();
        Assert.True((await fixture.Api.PostAsync($"/api/v1/form-records/{record:D}/shares/{shareId:D}/revoke", s.Token(s.Clerk), new { })).IsSuccess);
        var transfer = await fixture.Api.PostAsync($"/api/v1/form-records/{record:D}/transfer-ownership", s.Token(s.Clerk), new { newOwnerUserId = (Guid?)null, newOwnerTeamId = teamId, transferReason = "SENT-TRANSFER-REASON" });
        Assert.True(transfer.IsSuccess, transfer.Body);

        // A command row with no journal entry, inserted by the AuthorityDb administrator: raw payload with a scope, reason text.
        var rowOnly = Guid.CreateVersion7();
        var scopeSentinel = Guid.CreateVersion7();
        var payload = JsonSerializer.Serialize(new { formRecordId = record, recordShareId = Guid.CreateVersion7(), recipientUserId = s.Reader.AuthorityUserId, recipientRole = "Manager", fieldItemIds = new[] { scopeSentinel }, stepUpEvidence = "SENT-STEP-UP" });
        fixture.Stack.AuthorityAdministratorSql($"""
            INSERT INTO security_commands (security_command_id, organization_id, actor_identity_binding_id, command_kind_name, command_idempotency_key, command_request_digest, expected_authorization_revision,
                committed_authorization_revision, command_state_name, command_payload_json, command_reason_text, rejection_reason_code, correlation_identifier, command_created_on, command_modified_on)
            VALUES ('{rowOnly:D}', '{s.OrganizationId:D}', '{BindingOf(s, s.Clerk)}', 'CreateShare', 'tl-t08-{rowOnly:N}', decode(repeat('ab', 32), 'hex'), 1, NULL, 'Rejected',
                '{payload}'::jsonb, 'SENT-ROW-REASON', 'FORBIDDEN', '{Guid.CreateVersion7():D}', now(), now());
            """);

        // FailedReconciliation, set by the administrator on the team-member command (its journal entry stays).
        fixture.Stack.AuthorityAdministratorSql($"UPDATE security_commands SET command_state_name = 'FailedReconciliation' WHERE organization_id = '{s.OrganizationId:D}' AND command_kind_name = 'AddTeamMember';");

        var (items, bodies) = await AllPagesAsync(s, s.Reader, "pageSize=10");
        foreach (var body in bodies)
        {
            Assert.DoesNotContain("SENT-", body, StringComparison.Ordinal);
            Assert.DoesNotContain(scopeSentinel.ToString("D"), body, StringComparison.Ordinal);
            Assert.DoesNotContain("reason_note\":\"", body, StringComparison.Ordinal);
        }

        foreach (var item in items)
        {
            var kind = item.GetProperty("kind").GetString()!;
            Assert.DoesNotContain(kind, ExcludedKinds);
            if (AllowedKeys.TryGetValue(kind, out var allowed))
            {
                Assert.Subset(allowed.ToHashSet(StringComparer.Ordinal), Keys(item));
            }
        }

        JsonElement Command(string kind, string state) => items.First(i => i.GetProperty("source").GetString() == "Command" && i.GetProperty("kind").GetString() == kind && i.GetProperty("commandState").GetString() == state);

        var appointed = Command("GrantSystemRole", "Effective");
        Assert.Equal(("JournalBacked", "authorization"), (appointed.GetProperty("evidenceBasis").GetString(), appointed.GetProperty("evidence").GetProperty("stream").GetString()));
        Assert.Equal(["reason_code", "role_name", "target_user_id"], Keys(appointed).Order(StringComparer.Ordinal));
        Assert.Contains("reason_note", appointed.GetProperty("withheldFields").EnumerateArray().Select(w => w.GetString()));

        var refused = Command("GrantSystemRole", "Rejected");
        Assert.Equal(("JournalBacked", "audit"), (refused.GetProperty("evidenceBasis").GetString(), refused.GetProperty("evidence").GetProperty("stream").GetString()));
        Assert.False(string.IsNullOrEmpty(refused.GetProperty("rejectionCode").GetString()));

        var shared = Command("CreateShare", "Effective");
        Assert.Equal(["form_record_id", "recipient", "recipient_role", "record_share_id"], Keys(shared).Order(StringComparer.Ordinal));
        Assert.Equal("user:" + s.Designer.AuthorityUserId.ToString("D"), Detail(shared, "recipient"));

        var transferred = Command("TransferOwnership", "Effective");
        Assert.Equal("team:" + teamId.ToString("D"), Detail(transferred, "new_owner"));
        Assert.Equal("user:" + s.Clerk.AuthorityUserId.ToString("D"), Detail(transferred, "previous_owner"));

        var onlyRow = items.Single(i => i.GetProperty("commandId").GetString() == rowOnly.ToString("D"));
        Assert.Equal(("CommandRowOnly", JsonValueKind.Null), (onlyRow.GetProperty("evidenceBasis").GetString(), onlyRow.GetProperty("evidence").ValueKind));
        Assert.Equal(["form_record_id", "recipient", "recipient_role", "record_share_id"], Keys(onlyRow).Order(StringComparer.Ordinal));
        Assert.Contains("share_scope", onlyRow.GetProperty("withheldFields").EnumerateArray().Select(w => w.GetString()));
        Assert.Contains("step_up_evidence", onlyRow.GetProperty("withheldFields").EnumerateArray().Select(w => w.GetString()));

        var failed = Command("AddTeamMember", "FailedReconciliation");
        Assert.Equal("JournalBacked", failed.GetProperty("evidenceBasis").GetString());

        // A descending traversal never shows entries committed after it began (they are newer than its position): the access entries
        // of the pages just read appear on a fresh first page, served by the Authority and stating no delivery.
        Assert.DoesNotContain(items, i => i.GetProperty("kind").GetString() == "AuditAccessAuthorized");
        var fresh = await ReadAsync(Timeline + "?pageSize=10", s.Token(s.Reader));
        Assert.True(fresh.IsSuccess, fresh.Body);
        Assert.DoesNotContain("SENT-", fresh.Body, StringComparison.Ordinal);
        var accesses = fresh.Root.GetProperty("items").EnumerateArray().Where(i => i.GetProperty("kind").GetString() == "AuditAccessAuthorized").ToList();
        Assert.Equal(Math.Min(bodies.Count, 10), accesses.Count);
        foreach (var access in accesses)
        {
            Assert.Equal(("CommandTimelinePage", "authority", "not_attested"), (Detail(access, "access_kind"), Detail(access, "served_by"), Detail(access, "delivery")));
            Assert.Equal(AllowedKeys["AuditAccessAuthorized"].Where(k => k != "audit_event_id").Order(StringComparer.Ordinal), Keys(access).Order(StringComparer.Ordinal));
        }

        Assert.Equal(items.Count, items.Select(i => i.GetProperty("commandId").GetString() ?? i.GetProperty("evidence").GetProperty("stream").GetString() + i.GetProperty("evidence").GetProperty("streamRevision").GetInt64()).Distinct().Count());
    }

    /// <summary>T09: no item of another organization; cursor binding; non-auditors refused; a revoked auditor's next page refused.</summary>
    [Fact]
    public async Task AnotherOrganizationsAuditorSeesNoneOfThisOrganizationsCommands()
    {
        var s = await AuditorScenarioAsync("tl-t09-a");
        var other = await AuditorScenarioAsync("tl-t09-b");
        var team = await fixture.Api.PostAsync("/api/v1/teams", s.Token(s.Designer), new { teamName = "Organization A team " + s.Name });
        Assert.True(team.IsSuccess, team.Body);

        var mine = (await AllPagesAsync(s, s.Reader)).Items.Select(i => i.GetProperty("commandId").GetString()).OfType<string>().ToHashSet(StringComparer.Ordinal);
        var minimum = fixture.Stack.AuthorityObserverSql($"SELECT count(*) FROM security_commands WHERE organization_id = '{s.OrganizationId:D}' AND command_kind_name IN ('RegisterTeam', 'GrantSystemRole');").Trim();
        Assert.True(mine.Count >= int.Parse(minimum, CultureInfo.InvariantCulture));
        var theirs = (await AllPagesAsync(other, other.Reader)).Items.Select(i => i.GetProperty("commandId").GetString()).OfType<string>().ToHashSet(StringComparer.Ordinal);
        Assert.Empty(mine.Intersect(theirs));

        var first = await ReadAsync(Timeline + "?pageSize=1", s.Token(s.Reader));
        Assert.True(first.IsSuccess, first.Body);
        var cursor = Uri.EscapeDataString(first.Root.GetProperty("nextCursor").GetString()!);
        var foreign = await fixture.Api.GetAsync(Timeline + "?pageSize=1&cursor=" + cursor, other.Token(other.Reader));
        var otherFilter = await fixture.Api.GetAsync(Timeline + "?pageSize=2&cursor=" + cursor, s.Token(s.Reader));
        Assert.Equal((HttpStatusCode.BadRequest, "CURSOR_INVALID"), (foreign.Status, foreign.ErrorCode));
        Assert.Equal((HttpStatusCode.BadRequest, "CURSOR_INVALID"), (otherFilter.Status, otherFilter.ErrorCode));

        foreach (var user in new[] { s.Administrator, s.Designer, s.Clerk })
        {
            var refused = await fixture.Api.GetAsync(Timeline, s.Token(user));
            Assert.Equal((HttpStatusCode.Forbidden, "AUDIT_VIEW_FORBIDDEN"), (refused.Status, refused.ErrorCode));
        }

        Assert.True((await fixture.Api.PostAsync($"{Auditors}/{s.Reader.AuthorityUserId:D}/remove", s.Token(s.Administrator), new { reasonCode = "AssignmentEnded", useDevelopmentStepUp = true })).IsSuccess);
        var revoked = await fixture.Api.GetAsync(Timeline + "?pageSize=1&cursor=" + cursor, s.Token(s.Reader));
        Assert.Equal((HttpStatusCode.Forbidden, "AUDIT_VIEW_FORBIDDEN"), (revoked.Status, revoked.ErrorCode));
    }

    /// <summary>The keyset (PostgreSQL row comparison on time and id) pages without gaps or repeats; kind and state filters apply in SQL.</summary>
    [Fact]
    public async Task TheKeysetAndFiltersHoldOnPostgreSql()
    {
        var s = await AuditorScenarioAsync("tl-keyset");
        for (var i = 0; i < 5; i++)
        {
            Assert.True((await fixture.Api.PostAsync("/api/v1/teams", s.Token(s.Designer), new { teamName = $"Keyset team {i} " + s.Name })).IsSuccess);
        }

        var expected = fixture.Stack.AuthorityObserverSql($"SELECT security_command_id FROM security_commands WHERE organization_id = '{s.OrganizationId:D}' AND command_kind_name = 'RegisterTeam';")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet(StringComparer.Ordinal);
        var served = (await AllPagesAsync(s, s.Reader, "kind=RegisterTeam&pageSize=2")).Items;
        var ids = served.Select(i => i.GetProperty("commandId").GetString()!).ToList();
        Assert.Equal(expected.Count, ids.Count);
        Assert.Equal(expected, ids.ToHashSet(StringComparer.Ordinal));
        var times = served.Select(i => i.GetProperty("occurredOn").GetDateTimeOffset()).ToList();
        Assert.Equal(times.OrderByDescending(t => t).ToList(), times);

        var effective = (await AllPagesAsync(s, s.Reader, "state=Effective&pageSize=50")).Items;
        Assert.All(effective, i => Assert.Equal(("Command", "Effective"), (i.GetProperty("source").GetString(), i.GetProperty("commandState").GetString())));
        var invalid = await fixture.Api.GetAsync(Timeline + "?kind=RegisterRecord", s.Token(s.Reader));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, invalid.Status);
    }

    /// <summary>
    /// T22-style (measured, recorded): while one auditor reads timeline pages at the rate limit (each page is read under the
    /// organization lock taken NOWAIT), a Security Auditor removal still commits, within one bounded hold of the baseline.
    /// </summary>
    [Fact]
    public async Task ARevocationCommitsUnderATimelineReadFloodAndItsDurationIsRecorded()
    {
        var s = await AuditorScenarioAsync("tl-t22");
        Assert.Equal(HttpStatusCode.NoContent, (await fixture.Api.PostAsync(Auditors, s.Token(s.Administrator), new { userId = s.Clerk.AuthorityUserId, reasonCode = "AuditCoverageAssignment", useDevelopmentStepUp = true })).Status);
        Task<HttpResult> RemoveClerk() => fixture.Api.PostAsync($"{Auditors}/{s.Clerk.AuthorityUserId:D}/remove", s.Token(s.Administrator), new { reasonCode = "AssignmentEnded", useDevelopmentStepUp = true });
        var baseline = Stopwatch.StartNew();
        Assert.True((await RemoveClerk()).IsSuccess);
        baseline.Stop();
        Assert.Equal(HttpStatusCode.NoContent, (await fixture.Api.PostAsync(Auditors, s.Token(s.Administrator), new { userId = s.Clerk.AuthorityUserId, reasonCode = "AuditCoverageAssignment", useDevelopmentStepUp = true })).Status);

        using var stop = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var busy = 0;
        var flood = Task.Run(async () =>
        {
            var served = 0;
            while (!stop.IsCancellationRequested)
            {
                var page = await fixture.Api.GetAsync(Timeline + "?pageSize=50", s.Token(s.Reader));
                if (page.IsSuccess)
                {
                    served++;
                }
                else if (page.ErrorCode == "AUDIT_EVIDENCE_BUSY")
                {
                    Interlocked.Increment(ref busy);
                }

                await Task.Delay(200, CancellationToken.None); // 5/s: the per-user rate limit
            }

            return served;
        }, CancellationToken.None);
        await Task.Delay(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
        var underLoad = Stopwatch.StartNew();
        var removal = await RemoveClerk();
        underLoad.Stop();
        await Task.Delay(TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);
        await stop.CancelAsync();
        var pages = await flood;

        Assert.True(removal.IsSuccess, removal.Body);
        Assert.True(pages > 0, "the flood was served");
        var line = $"T22 timeline: baseline removal {baseline.Elapsed.TotalMilliseconds:F0} ms; under timeline-read flood {underLoad.Elapsed.TotalMilliseconds:F0} ms; timeline pages served {pages}; busy refusals {busy}";
        TestContext.Current.TestOutputHelper?.WriteLine(line);
        TestContext.Current.SendDiagnosticMessage(line);
        Assert.True(underLoad.Elapsed <= baseline.Elapsed + TimeSpan.FromSeconds(2), $"removal under load took {underLoad.Elapsed.TotalMilliseconds:F0} ms against a {baseline.Elapsed.TotalMilliseconds:F0} ms baseline");
    }
}
