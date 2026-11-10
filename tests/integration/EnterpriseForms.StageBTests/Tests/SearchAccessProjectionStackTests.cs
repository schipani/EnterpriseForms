using System.Globalization;
using System.Net;
using System.Text.Json;
using EnterpriseForms.SecurityTests.Harness;
using EnterpriseForms.StageBTests.Harness;
using Xunit;

namespace EnterpriseForms.StageBTests.Tests;

/// <summary>
/// IX-5a (C-147) on the real security-test stack: the dark Tier 2 access projection maintained from the Authority's authorization
/// journal (FD-5-ALT). Each test uses its own organization. The rows are compared with the canonical edges (M1) and, sampled, with
/// SpiceDB at a pinned S1 snapshot (team- and user-level); the journal replay is compared with the canonical edges. Covers sharing
/// paths, ownership transfer, child unlinking, revocation and team membership, organization isolation, exact readiness, crash
/// recovery, concurrent maintainers, journal tampering, quarantine and controlled rebuild. No search path reads Tier 2.
/// Synthetic data only. A focused development test class (C-131), not Stage B.
/// </summary>
[Collection(StageBStackCollectionDefinition.Name)]
public sealed class SearchAccessProjectionStackTests(StageBFixture fixture)
{
    private const string Prefix = "/internal/v1/testing/search-access";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed record Rows(List<(string Type, Guid Id, string Kind, Guid? Share)> Records, List<(string Type, Guid Id, Guid Item, Guid Share)> Items);

    // ---- Authority testing endpoints ----------------------------------------------------------------------------------------

    private async Task<JsonElement> AuthorityAsync(HttpMethod method, string path, object? body = null)
    {
        var result = await fixture.Authority.SendAsync(method, Prefix + path, TestIdentity.WorkloadToken(), body);
        Assert.True(result.IsSuccess, path + ": " + result.Body);
        return result.Root.Clone();
    }

    private async Task<JsonElement> StatusAsync(Guid org) => await AuthorityAsync(HttpMethod.Get, $"/{org:D}/status");

    private static string StatusName(JsonElement status) => status.GetProperty("status_name").GetString()!;

    private Task<JsonElement> CatchUpAsync(Guid org) => AuthorityAsync(HttpMethod.Post, $"/{org:D}/catch-up", new { maximum_events = 1000 });

    private Task<JsonElement> VerifyAsync(Guid org) => AuthorityAsync(HttpMethod.Post, $"/{org:D}/verify");

    private Task<JsonElement> ReplayAsync(Guid org) => AuthorityAsync(HttpMethod.Get, $"/{org:D}/journal-replay");

    private Task<JsonElement> PauseAsync(bool paused) => AuthorityAsync(HttpMethod.Post, "/maintenance", new { paused });

    /// <summary>Ready: catches up explicitly (works whether or not the loop runs) and polls until the checkpoint equals the revision.</summary>
    private async Task<JsonElement> ReadyAsync(Guid org, int seconds = 60)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        JsonElement status;
        do
        {
            status = (await CatchUpAsync(org)).GetProperty("status");
            if (StatusName(status) == "Ready")
            {
                return status;
            }

            Assert.NotEqual("Quarantined", StatusName(status));
            await Task.Delay(300, Ct);
        }
        while (DateTime.UtcNow < deadline);
        Assert.Fail("not ready: " + status);
        return status;
    }

    /// <summary>Ready, then M1 + SpiceDB verification CLEAN and journal replay COMPLETE (retrying a concurrent move only).</summary>
    private async Task<JsonElement> ReadyAndVerifiedAsync(Guid org, bool expectSpiceDbChecks = true)
    {
        for (var attempt = 0; ; attempt++)
        {
            await ReadyAsync(org);
            var verified = await VerifyAsync(org);
            var outcome = verified.GetProperty("outcome_code").GetString();
            if (outcome is "MOVED" or "NOT_AT_HEAD" or "SPICEDB_NOT_READY" && attempt < 20)
            {
                await Task.Delay(500, Ct);
                continue;
            }

            Assert.True(outcome == "CLEAN", "verification: " + verified);
            if (expectSpiceDbChecks)
            {
                Assert.True(verified.GetProperty("spice_db_checks").GetInt32() > 0, "no SpiceDB comparison ran: " + verified);
            }

            var replay = await ReplayAsync(org);
            Assert.True(replay.GetProperty("outcome_code").GetString() == "COMPLETE", "journal replay: " + replay);
            return verified;
        }
    }

    private async Task<Rows> RowsAsync(Guid org, Guid record)
    {
        var rows = await AuthorityAsync(HttpMethod.Get, $"/{org:D}/records/{record:D}");
        return new Rows(
            rows.GetProperty("record_grants").EnumerateArray().Select(r => (r.GetProperty("principal_type_name").GetString()!, r.GetProperty("principal_entity_id").GetGuid(), r.GetProperty("access_kind_name").GetString()!, r.GetProperty("record_share_id").ValueKind == JsonValueKind.Null ? (Guid?)null : r.GetProperty("record_share_id").GetGuid())).ToList(),
            rows.GetProperty("field_item_grants").EnumerateArray().Select(r => (r.GetProperty("principal_type_name").GetString()!, r.GetProperty("principal_entity_id").GetGuid(), r.GetProperty("manifest_item_id").GetGuid(), r.GetProperty("record_share_id").GetGuid())).ToList());
    }

    // ---- business API -------------------------------------------------------------------------------------------------------

    private static async Task<T> RetryAsync<T>(Func<Task<HttpResult>> call, Func<HttpResult, T> success, string what)
    {
        for (var attempt = 0; ; attempt++)
        {
            var result = await call();
            if (result.IsSuccess)
            {
                return success(result);
            }

            Assert.True(attempt < 40 && result.ErrorCode is "AUTHORITY_NOT_READY" or "COMMAND_PENDING" or "STALE_AUTHORIZATION_REVISION" or "CONTENT_REVISION_CONFLICT", what + " failed: " + result.Body);
            await Task.Delay(300, Ct);
        }
    }

    private async Task<Guid> ShareAsync(StageBScenario s, TestUser owner, Guid record, Guid? user, Guid? team, params string[] fieldKeys)
    {
        var id = await RetryAsync(() => fixture.Api.PostAsync($"/api/v1/form-records/{record:D}/shares", s.Token(owner), new { recipientUserId = user, recipientTeamId = team, recipientRole = "Reader", fieldKeys }), r => r.Root.GetProperty("recordShareId").GetGuid(), "share");
        await s.WaitGateReadyAsync();
        return id;
    }

    private async Task RevokeAsync(StageBScenario s, TestUser owner, Guid record, Guid share)
    {
        await RetryAsync(() => fixture.Api.PostAsync($"/api/v1/form-records/{record:D}/shares/{share:D}/revoke", s.Token(owner), new { }), _ => true, "revoke");
        await s.WaitGateReadyAsync();
    }

    private async Task<Guid> TeamAsync(StageBScenario s, params TestUser[] members)
    {
        var team = await fixture.Api.PostAsync("/api/v1/teams", s.Token(s.Designer), new { teamName = "IX-5a team " + Guid.NewGuid().ToString("N")[..8] });
        Assert.Equal(HttpStatusCode.Created, team.Status);
        var teamId = team.Root.GetProperty("teamId").GetGuid();
        foreach (var member in members)
        {
            await RetryAsync(() => fixture.Api.PostAsync($"/api/v1/teams/{teamId:D}/members", s.Token(s.Designer), new { userId = member.AuthorityUserId }, "ix5a-add-" + Guid.NewGuid().ToString("N")), _ => true, "add member");
            await s.WaitGateReadyAsync();
        }

        return teamId;
    }

    private async Task RemoveMemberAsync(StageBScenario s, Guid team, TestUser member)
    {
        await RetryAsync(() => fixture.Api.PostAsync($"/api/v1/teams/{team:D}/members/{member.AuthorityUserId:D}/remove", s.Token(s.Designer), new { }), _ => true, "remove member");
        await s.WaitGateReadyAsync();
    }

    private async Task TransferAsync(StageBScenario s, TestUser owner, Guid record, Guid? user, Guid? team)
    {
        await RetryAsync(() => fixture.Api.PostAsync($"/api/v1/form-records/{record:D}/transfer-ownership", s.Token(owner), new { newOwnerUserId = user, newOwnerTeamId = team, transferReason = "IX-5a transfer" }), _ => true, "transfer");
        await s.WaitGateReadyAsync();
    }

    private async Task<StageBScenario> OrganizationAsync(string name)
    {
        var s = new StageBScenario(fixture, "ix5a-" + name);
        await s.BootstrapAsync();
        await s.BuildAccusedWorkspaceAsync();
        return s;
    }

    private string Admin(Guid org, string sql, bool throwOnError = true) =>
        fixture.Stack.AuthorityAdministratorSql($"SELECT set_config('authority_search_access.organization_id', '{org:D}', false); {sql}", throwOnError);

    private string Observer(string sql) => fixture.Stack.AuthorityObserverSql(sql).Trim();

    private long Revision(Guid org) => long.Parse(Observer($"SELECT authorization_revision FROM organization_authorization_states WHERE organization_id = '{org:D}';").Split('\n')[0].Trim(), CultureInfo.InvariantCulture);

    private static bool Has(Rows rows, string type, Guid id, string kind) => rows.Records.Any(r => r.Type == type && r.Id == id && r.Kind == kind);

    private static HashSet<Guid> ItemsOf(Rows rows, string type, Guid id) => rows.Items.Where(i => i.Type == type && i.Id == id).Select(i => i.Item).ToHashSet();

    private static async Task<HashSet<Guid>> FieldItemsAsync(StageBScenario s, Guid record, params string[] fieldKeys)
    {
        var projection = await s.WaitProjectionAsync(s.Clerk, record);
        return projection.Root.GetProperty("record").GetProperty("values").EnumerateArray().Where(v => fieldKeys.Contains(v.GetProperty("fieldKey").GetString())).Select(v => v.GetProperty("fieldValueItemId").GetGuid()).ToHashSet();
    }

    // ---- tests --------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task SharingPathsOwnershipChildrenRevocationAndMembershipProjectExactly()
    {
        var s = await OrganizationAsync("paths");
        var org = s.OrganizationId;
        var t1 = await TeamAsync(s, s.Administrator);
        var t2 = await TeamAsync(s, s.Reader);
        var record = await s.CreateAccusedAsync(s.Clerk);
        var child = await s.CreateAddressAsync(s.Clerk, record, "1 Synthetic Street");
        var given = await FieldItemsAsync(s, record, "given_name");
        var status = await FieldItemsAsync(s, record, "case_status");
        var family = await FieldItemsAsync(s, record, "family_name");
        var direct = await ShareAsync(s, s.Clerk, record, s.Reader.AuthorityUserId, null, "given_name", "case_status");
        await ShareAsync(s, s.Clerk, record, null, t1, "family_name");
        await ShareAsync(s, s.Clerk, record, null, t2, "given_name");
        await ReadyAndVerifiedAsync(org);

        var rows = await RowsAsync(org, record);
        Assert.True(Has(rows, "user", s.Clerk.AuthorityUserId, "EffectiveOwner"));
        Assert.True(Has(rows, "user", s.Reader.AuthorityUserId, "ShareRecipient") && Has(rows, "team", t1, "ShareRecipient") && Has(rows, "team", t2, "ShareRecipient"));
        Assert.True(ItemsOf(rows, "user", s.Reader.AuthorityUserId).SetEquals(given.Concat(status)));
        Assert.True(ItemsOf(rows, "team", t1).SetEquals(family));
        Assert.True(ItemsOf(rows, "team", t2).SetEquals(given));
        // Teams are never expanded (D-1): no row names a team member as a user.
        Assert.DoesNotContain(rows.Records.Concat(rows.Records), r => r.Type == "user" && r.Id == s.Administrator.AuthorityUserId);
        Assert.DoesNotContain(rows.Items, i => i.Type == "user" && i.Id == s.Administrator.AuthorityUserId);
        // The child inherits effective ownership only; parent shares do not reach it.
        var childRows = await RowsAsync(org, child);
        Assert.Equal([("user", s.Clerk.AuthorityUserId, "EffectiveOwner", (Guid?)null)], childRows.Records);
        Assert.Empty(childRows.Items);

        // Revoking the direct path keeps the team path's rows for the same item (distinct-item union at query time).
        await RevokeAsync(s, s.Clerk, record, direct);
        await ReadyAndVerifiedAsync(org);
        rows = await RowsAsync(org, record);
        Assert.DoesNotContain(rows.Records, r => r.Share == direct);
        Assert.Empty(ItemsOf(rows, "user", s.Reader.AuthorityUserId));
        Assert.True(ItemsOf(rows, "team", t2).SetEquals(given));

        // A membership change writes no row; verification (including user-level SpiceDB checks) stays clean.
        var before = await RowsAsync(org, record);
        await RemoveMemberAsync(s, t2, s.Reader);
        await ReadyAndVerifiedAsync(org);
        var after = await RowsAsync(org, record);
        Assert.Equal(before.Records.Order().ToList(), after.Records.Order().ToList());
        Assert.Equal(before.Items.Order().ToList(), after.Items.Order().ToList());

        // Ownership transfer to a team moves effective ownership of the record and of its child; the old owner keeps nothing.
        await TransferAsync(s, s.Clerk, record, null, t1);
        await ReadyAndVerifiedAsync(org);
        rows = await RowsAsync(org, record);
        childRows = await RowsAsync(org, child);
        Assert.True(Has(rows, "team", t1, "EffectiveOwner") && Has(childRows, "team", t1, "EffectiveOwner"));
        Assert.DoesNotContain(rows.Records.Concat(childRows.Records), r => r.Id == s.Clerk.AuthorityUserId);

        // Unlinking the child ends its inherited ownership; the projection equals the canonical edges and SpiceDB again.
        await RetryAsync(() => fixture.Api.PostAsync($"/api/v1/form-records/{record:D}/related/{child:D}/unlink", s.Token(s.Administrator), new { removalReason = "IX-5a unlink" }), _ => true, "unlink");
        await s.WaitGateReadyAsync();
        await ReadyAndVerifiedAsync(org);
    }

    [Fact]
    public async Task OrganizationsAreIsolatedAndTheSearchReaderCannotReadTier2()
    {
        var a = await OrganizationAsync("iso-a");
        var b = await OrganizationAsync("iso-b");
        await a.CreateAccusedAsync(a.Clerk);
        await b.CreateAccusedAsync(b.Clerk);
        await ReadyAsync(a.OrganizationId);
        await ReadyAsync(b.OrganizationId);
        var role = Observer("SELECT grantee FROM information_schema.role_table_grants WHERE table_schema = 'authority' AND table_name = 'security_journal_entries' AND privilege_type = 'INSERT' AND grantee NOT IN (SELECT rolname FROM pg_roles WHERE rolsuper) LIMIT 1;").Split('\n')[0].Trim();
        string As(string asRole, Guid? org, string sql) => fixture.Stack.AuthorityAdministratorSql(
            $"BEGIN; SET LOCAL ROLE {asRole};{(org is { } o ? $" SELECT set_config('authority_search_access.organization_id', '{o:D}', true);" : string.Empty)} {sql}; COMMIT;", throwOnError: false);
        static string Last(string output) => output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.Trim()).Last(l => l.Length > 0 && char.IsDigit(l[0]));

        Assert.NotEqual("0", Last(As(role, a.OrganizationId, "SELECT count(*) FROM authority_search_access.search_access_record_grants")));
        Assert.Equal("0", Last(As(role, a.OrganizationId, $"SELECT count(*) FROM authority_search_access.search_access_record_grants WHERE organization_id = '{b.OrganizationId:D}'")));
        Assert.Equal("0", Last(As(role, null, "SELECT count(*) FROM authority_search_access.search_access_edges")));
        Assert.Contains("row-level security", As(role, a.OrganizationId, $"INSERT INTO authority_search_access.search_access_record_grants (search_access_record_grant_id, organization_id, access_principal_type_name, access_principal_entity_id, form_record_id, access_kind_name, derived_authorization_revision) VALUES (gen_random_uuid(), '{b.OrganizationId:D}', 'user', gen_random_uuid(), gen_random_uuid(), 'EffectiveOwner', 0)"), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("permission denied", As(role, a.OrganizationId, "UPDATE authority_search_access.search_access_record_grants SET derived_authorization_revision = 0 WHERE false"), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("permission denied", As(role, a.OrganizationId, "DELETE FROM authority_search_access.search_access_checkpoints WHERE false"), StringComparison.OrdinalIgnoreCase);
        // The Tier 1 search reader has no access to the Tier 2 schema at all.
        Assert.Contains("permission denied", As("authority_search_reader", a.OrganizationId, "SELECT count(*) FROM authority_search_access.search_access_record_grants"), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ReadinessIsExactAndARevocationIsNeverReadyWhileUnconsumed()
    {
        var s = await OrganizationAsync("ready");
        var org = s.OrganizationId;
        var record = await s.CreateAccusedAsync(s.Clerk);
        await ReadyAsync(org);
        await PauseAsync(true);
        try
        {
            var share = await ShareAsync(s, s.Clerk, record, s.Reader.AuthorityUserId, null, "given_name");
            var status = await StatusAsync(org);
            Assert.Equal("Building", StatusName(status));
            Assert.True(status.GetProperty("consumed_authorization_revision").GetInt64() < status.GetProperty("current_authorization_revision").GetInt64());
            Assert.Equal("NOT_AT_HEAD", (await VerifyAsync(org)).GetProperty("outcome_code").GetString());
            await ReadyAsync(org);
            Assert.NotEmpty(ItemsOf(await RowsAsync(org, record), "user", s.Reader.AuthorityUserId));

            await RevokeAsync(s, s.Clerk, record, share);
            // The stale grant is still in the rows, but Tier 2 is not ready, so nothing could serve it (A-3).
            Assert.Equal("Building", StatusName(await StatusAsync(org)));
            Assert.NotEmpty(ItemsOf(await RowsAsync(org, record), "user", s.Reader.AuthorityUserId));
            await ReadyAsync(org);
            Assert.Empty(ItemsOf(await RowsAsync(org, record), "user", s.Reader.AuthorityUserId));
        }
        finally
        {
            await PauseAsync(false);
        }

        await ReadyAndVerifiedAsync(org);
    }

    [Fact]
    public async Task ACrashBeforeCommitRollsBackAndRecoveryReplaysIdempotently()
    {
        var s = await OrganizationAsync("crash");
        var org = s.OrganizationId;
        var record = await s.CreateAccusedAsync(s.Clerk);
        var before = await ReadyAsync(org);
        const string Marker = "FAULT-INJECTION: terminating process at 'CrashBeforeAccessProjectionCommit'";
        var crashesBefore = Count(fixture.Stack.Logs("authority", 5000), Marker);
        await PauseAsync(true);
        await ShareAsync(s, s.Clerk, record, s.Reader.AuthorityUserId, null, "given_name");
        var arm = await fixture.Authority.OperatorAsync(HttpMethod.Post, "/internal/v1/operator/fault-injection", new { fault_name = "CrashBeforeAccessProjectionCommit" });
        Assert.True(arm.IsSuccess, arm.Body);
        try
        {
            _ = await fixture.Authority.SendAsync(HttpMethod.Post, Prefix + $"/{org:D}/catch-up", TestIdentity.WorkloadToken(), new { maximum_events = 1000 });
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            // the process terminated mid-transaction (expected)
        }

        var deadline = DateTime.UtcNow.AddSeconds(90);
        while (Count(fixture.Stack.Logs("authority", 5000), Marker) <= crashesBefore)
        {
            Assert.True(DateTime.UtcNow < deadline, "the armed crash did not happen");
            await Task.Delay(1000, Ct);
        }

        await WaitAuthorityAsync(s);
        await fixture.Authority.OperatorAsync(HttpMethod.Post, "/internal/v1/operator/fault-injection", new { fault_name = "None" });
        await PauseAsync(false); // a restarted Authority starts unpaused; restore the switch explicitly
        var after = await ReadyAsync(org);
        // Exactly one checkpoint advance for one event: the crashed transaction committed nothing, and the redo advanced once.
        Assert.Equal(before.GetProperty("checkpoint_revision").GetInt64() + 1, after.GetProperty("checkpoint_revision").GetInt64());
        Assert.Equal(Revision(org), after.GetProperty("consumed_authorization_revision").GetInt64());
        await ReadyAndVerifiedAsync(org);
        Assert.NotEmpty(ItemsOf(await RowsAsync(org, record), "user", s.Reader.AuthorityUserId));
    }

    [Fact]
    public async Task ConcurrentMaintainersAreFencedByTheCheckpoint()
    {
        var s = await OrganizationAsync("fence");
        var org = s.OrganizationId;
        var record = await s.CreateAccusedAsync(s.Clerk);
        var before = await ReadyAsync(org);
        await PauseAsync(true);
        try
        {
            var team = await TeamAsync(s, s.Administrator, s.Reader);
            await ShareAsync(s, s.Clerk, record, s.Reader.AuthorityUserId, null, "given_name");
            await ShareAsync(s, s.Clerk, record, null, team, "family_name", "case_status");
            var events = Revision(org) - before.GetProperty("consumed_authorization_revision").GetInt64();
            Assert.True(events >= 4, "events: " + events);
            var calls = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => CatchUpAsync(org)));
            Assert.Equal(events, calls.Sum(c => c.GetProperty("advanced").GetInt32()));
            var after = await StatusAsync(org);
            Assert.Equal("Ready", StatusName(after));
            Assert.Equal(before.GetProperty("checkpoint_revision").GetInt64() + events, after.GetProperty("checkpoint_revision").GetInt64());
        }
        finally
        {
            await PauseAsync(false);
        }

        await ReadyAndVerifiedAsync(org);
    }

    [Theory]
    [InlineData("body", "ACCESS_JOURNAL_DIGEST_MISMATCH")]
    [InlineData("chain", "ACCESS_JOURNAL_CHAIN_BREAK")]
    [InlineData("gap", "ACCESS_JOURNAL_GAP")]
    [InlineData("reorder", "ACCESS_JOURNAL_CHAIN_BREAK")]
    public async Task JournalTamperingQuarantinesTier2OnlyAndIsNeverRepairedSilently(string tamper, string expectedCode)
    {
        var s = await OrganizationAsync("tamper-" + tamper);
        var org = s.OrganizationId;
        var record = await s.CreateAccusedAsync(s.Clerk);
        await ReadyAsync(org);
        await PauseAsync(true);
        try
        {
            await ShareAsync(s, s.Clerk, record, s.Reader.AuthorityUserId, null, "given_name");
            await ShareAsync(s, s.Clerk, record, s.Administrator.AuthorityUserId, null, "family_name");
            var head = Revision(org);
            var where = $"organization_id = '{org:D}' AND journal_stream_name = 'authorization'";
            var sql = tamper switch
            {
                "body" => $"UPDATE authority.security_journal_entries SET journal_event_body_json = replace(journal_event_body_json, '#reader@', '#manager@') WHERE {where} AND journal_stream_revision = {head - 1};",
                "chain" => $"UPDATE authority.security_journal_entries SET journal_previous_digest = decode(repeat('00', 32), 'hex') WHERE {where} AND journal_stream_revision = {head - 1};",
                "gap" => $"UPDATE authority.security_journal_entries SET journal_stream_revision = journal_stream_revision + 1000000 WHERE {where} AND journal_stream_revision = {head - 1};",
                _ => $"UPDATE authority.security_journal_entries SET journal_stream_revision = -1 WHERE {where} AND journal_stream_revision = {head}; " +
                     $"UPDATE authority.security_journal_entries SET journal_stream_revision = {head} WHERE {where} AND journal_stream_revision = {head - 1}; " +
                     $"UPDATE authority.security_journal_entries SET journal_stream_revision = {head - 1} WHERE {where} AND journal_stream_revision = -1;",
            };
            fixture.Stack.AuthorityAdministratorSql(sql);
            var caught = await CatchUpAsync(org);
            Assert.Equal("Quarantined", caught.GetProperty("outcome_name").GetString());
            var status = await StatusAsync(org);
            Assert.Equal("Quarantined", StatusName(status));
            Assert.Equal(expectedCode, status.GetProperty("quarantine_reason_code").GetString());
            Assert.NotEqual("COMPLETE", (await ReplayAsync(org)).GetProperty("outcome_code").GetString());
        }
        finally
        {
            await PauseAsync(false);
        }

        // The maintenance loop never repairs it; the event is journaled on the audit stream.
        await Task.Delay(2000, Ct);
        Assert.Equal("Quarantined", StatusName(await StatusAsync(org)));
        Assert.Equal("Quarantined", (await CatchUpAsync(org)).GetProperty("outcome_name").GetString());
        Assert.NotEqual("0", Observer($"SELECT count(*) FROM security_journal_entries WHERE organization_id = '{org:D}' AND journal_event_kind = 'SearchAccessProjectionQuarantined';").Split('\n')[0].Trim());
    }

    [Fact]
    public async Task DivergedRowsAreQuarantinedWithEvidenceKeptAndOnlyAControlledRebuildRestoresThem()
    {
        var s = await OrganizationAsync("rebuild");
        var org = s.OrganizationId;
        var record = await s.CreateAccusedAsync(s.Clerk);
        await ShareAsync(s, s.Clerk, record, s.Reader.AuthorityUserId, null, "given_name");
        await ReadyAndVerifiedAsync(org);
        var forgedPrincipal = Guid.NewGuid();
        Admin(org, $"INSERT INTO authority_search_access.search_access_record_grants (search_access_record_grant_id, organization_id, access_principal_type_name, access_principal_entity_id, form_record_id, access_kind_name, derived_authorization_revision) VALUES (gen_random_uuid(), '{org:D}', 'user', '{forgedPrincipal:D}', '{record:D}', 'EffectiveOwner', 0);");
        var verified = await VerifyAsync(org);
        Assert.Equal("ACCESS_ROWS_DIVERGED", verified.GetProperty("outcome_code").GetString());
        Assert.Equal("Quarantined", StatusName(await StatusAsync(org)));
        await Task.Delay(2000, Ct);
        Assert.Equal("Quarantined", StatusName(await StatusAsync(org)));
        Assert.Contains((await RowsAsync(org, record)).Records, r => r.Id == forgedPrincipal); // evidence kept, not repaired

        // Commands are unaffected while Tier 2 is quarantined (no Tier 2 work in the command transaction).
        await ShareAsync(s, s.Clerk, record, s.Administrator.AuthorityUserId, null, "family_name");

        var rebuilt = await AuthorityAsync(HttpMethod.Post, $"/{org:D}/rebuild", new { rebuild_reason_code = "IX5A_TEST_REBUILD" });
        Assert.Equal("Ready", StatusName(rebuilt));
        await ReadyAndVerifiedAsync(org);
        Assert.DoesNotContain((await RowsAsync(org, record)).Records, r => r.Id == forgedPrincipal);
        Assert.NotEqual("0", Observer($"SELECT count(*) FROM security_journal_entries WHERE organization_id = '{org:D}' AND journal_event_kind = 'SearchAccessProjectionRebuilt';").Split('\n')[0].Trim());
    }

    private static int Count(string text, string marker)
    {
        var count = 0;
        for (var i = text.IndexOf(marker, StringComparison.Ordinal); i >= 0; i = text.IndexOf(marker, i + marker.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }

    private static async Task WaitAuthorityAsync(StageBScenario s)
    {
        var deadline = DateTime.UtcNow.AddSeconds(240);
        while (true)
        {
            try
            {
                await s.WaitGateReadyAsync(TimeSpan.FromSeconds(30));
                return;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException && DateTime.UtcNow < deadline)
            {
                await Task.Delay(2000, Ct);
            }
        }
    }
}
