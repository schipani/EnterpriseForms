using System.Net;
using EnterpriseForms.SecurityTests.Harness;
using Xunit;

namespace EnterpriseForms.SecurityTests.Tests;

/// <summary>SEC-P01, SEC-P02, SEC-P03, SEC-P06: hostile BusinessDb writes to owner/share/team projections and revocation barriers.</summary>
[Collection(StageACollection.Name)]
public sealed class AuthorizationTamperingTests(StageAFixture fixture)
{
    [Fact]
    [Trait("SEC-P", "P01")]
    public async Task SecP01_DirectSqlOwnerChangeDoesNotGrantAccess()
    {
        var s = new Scenario(fixture, "SEC-P01", "Direct SQL owner change: forged owner in BusinessDb remains unauthorized; authority owner unchanged; record read denied");
        await ScenarioRunner.RunAsync(s, async () =>
        {
            await s.BootstrapAsync();
            await s.CreateFormAndRecordAsync();
            var gateBefore = await fixture.Authority.GateAsync(s.OrganizationId);
            s.Evidence.Observe("gate before attack", gateBefore.GetRawText());

            var before = await s.ReadAsync(s.Attacker);
            s.Evidence.ObserveHttp("attacker read before tampering", before);
            Assert.Equal(HttpStatusCode.NotFound, before.Status);

            var rows = fixture.Stack.AttackerSql($"UPDATE form_records SET owner_user_id = '{s.Attacker.AuthorityUserId:D}', owner_team_id = NULL WHERE form_record_id = '{s.FormRecordId:D}'; SELECT owner_user_id FROM form_records WHERE form_record_id = '{s.FormRecordId:D}';");
            s.Evidence.Observe("hostile UPDATE form_records.owner_user_id", new { result = rows.Trim() });
            Assert.Contains(s.Attacker.AuthorityUserId.ToString("D"), rows, StringComparison.OrdinalIgnoreCase);

            var after = await s.ReadAsync(s.Attacker);
            s.Evidence.ObserveHttp("attacker read after tampering", after);
            Assert.Equal(HttpStatusCode.NotFound, after.Status);
            Assert.DoesNotContain("protected note", after.Body, StringComparison.Ordinal);

            var transfer = await fixture.Api.PostAsync($"/api/v1/form-records/{s.FormRecordId:D}/transfer-ownership", s.Token(s.Attacker), new { newOwnerUserId = s.Attacker.AuthorityUserId, transferReason = "forged" });
            s.Evidence.ObserveHttp("attacker transfer-ownership attempt", transfer);
            Assert.True(transfer.Status is HttpStatusCode.Forbidden or HttpStatusCode.UnprocessableEntity or HttpStatusCode.NotFound, transfer.Body);

            var ownerRead = await s.ReadAsync(s.Owner);
            s.Evidence.ObserveHttp("legitimate owner read after tampering", ownerRead, "isCompleteProjection", "contentRevision");
            Assert.Equal(HttpStatusCode.OK, ownerRead.Status);
            Assert.True(ownerRead.Root.GetProperty("isCompleteProjection").GetBoolean());

            var gateAfter = await fixture.Authority.GateAsync(s.OrganizationId);
            s.Evidence.Observe("gate after attack", gateAfter.GetRawText());
            Assert.Equal(gateBefore.GetProperty("authorization_revision").GetInt64(), gateAfter.GetProperty("authorization_revision").GetInt64());
            s.Evidence.AuthorityRevision = gateAfter.GetProperty("authorization_revision").GetInt64();
        });
    }

    [Fact]
    [Trait("SEC-P", "P02")]
    public async Task SecP02_DirectSqlShareRowDoesNotGrantDiscoverOrRead()
    {
        var s = new Scenario(fixture, "SEC-P02", "Direct SQL sharing grant: inserted local share row does not grant discover/read; no new authority edge");
        await ScenarioRunner.RunAsync(s, async () =>
        {
            await s.BootstrapAsync();
            await s.CreateFormAndRecordAsync();
            var gateBefore = await fixture.Authority.GateAsync(s.OrganizationId);
            var forgedShare = Guid.CreateVersion7();
            var sql = $"""
                INSERT INTO record_shares (record_share_id, tenant_id, form_record_id, shared_by_user_id, recipient_user_id, recipient_team_id, recipient_role, share_scope, share_status, share_revision, authority_command_id, share_created_on)
                VALUES ('{forgedShare:D}', '{s.OrganizationId:D}', '{s.FormRecordId:D}', '{s.Owner.AuthorityUserId:D}', '{s.Attacker.AuthorityUserId:D}', NULL, 'Manager', 'SelectedItems', 'Active', 1, NULL, now());
                INSERT INTO record_share_fields (record_share_field_id, tenant_id, record_share_id, field_definition_id, field_value_item_id, can_view_field, can_edit_field)
                SELECT gen_random_uuid(), '{s.OrganizationId:D}', '{forgedShare:D}', field_definition_id, field_value_item_id, true, true FROM field_string_values WHERE form_record_id = '{s.FormRecordId:D}';
                SELECT count(*) FROM record_share_fields WHERE record_share_id = '{forgedShare:D}';
                """;
            var inserted = fixture.Stack.AttackerSql(sql);
            s.Evidence.Observe("hostile INSERT record_shares + record_share_fields", new { field_rows = inserted.Trim() });
            Assert.Equal("3", inserted.Trim());

            var attackerRead = await s.ReadAsync(s.Attacker);
            s.Evidence.ObserveHttp("attacker read with forged share", attackerRead);
            Assert.Equal(HttpStatusCode.NotFound, attackerRead.Status);
            Assert.DoesNotContain("protected note", attackerRead.Body, StringComparison.Ordinal);

            var attackerShare = await fixture.Api.PostAsync($"/api/v1/form-records/{s.FormRecordId:D}/shares", s.Token(s.Attacker), new { recipientUserId = s.Reader.AuthorityUserId, recipientRole = "Reader", fieldKeys = new[] { "case_note" } });
            s.Evidence.ObserveHttp("attacker (forged Manager) tries to delegate", attackerShare);
            Assert.True(attackerShare.Status is HttpStatusCode.Forbidden or HttpStatusCode.NotFound or HttpStatusCode.UnprocessableEntity, attackerShare.Body);

            var gateMid = await fixture.Authority.GateAsync(s.OrganizationId);
            Assert.Equal(gateBefore.GetProperty("authorization_revision").GetInt64(), gateMid.GetProperty("authorization_revision").GetInt64());

            // Legitimate scoped share: the reader sees exactly the shared item, nothing else.
            var share = await fixture.Api.PostAsync($"/api/v1/form-records/{s.FormRecordId:D}/shares", s.Token(s.Owner), new { recipientUserId = s.Reader.AuthorityUserId, recipientRole = "Reader", fieldKeys = new[] { "given_name" } });
            s.Evidence.ObserveHttp("owner shares given_name with reader", share, "recordShareId", "shareStatus");
            Assert.Equal(HttpStatusCode.Created, share.Status);
            var readerRead = await s.WaitReadableAsync(s.Reader);
            s.Evidence.ObserveHttp("reader read", readerRead, "isCompleteProjection");
            Assert.Equal(HttpStatusCode.OK, readerRead.Status);
            Assert.False(readerRead.Root.GetProperty("isCompleteProjection").GetBoolean());
            var keys = readerRead.Root.GetProperty("values").EnumerateArray().Select(v => v.GetProperty("fieldKey").GetString()).ToList();
            Assert.Equal(["given_name"], keys);
            Assert.DoesNotContain("protected note", readerRead.Body, StringComparison.Ordinal);

            var attackerAgain = await s.ReadAsync(s.Attacker);
            Assert.Equal(HttpStatusCode.NotFound, attackerAgain.Status);
            var gateAfter = await fixture.Authority.GateAsync(s.OrganizationId);
            s.Evidence.AuthorityRevision = gateAfter.GetProperty("authorization_revision").GetInt64();
            Assert.Equal(gateBefore.GetProperty("authorization_revision").GetInt64() + 1, s.Evidence.AuthorityRevision);
        });
    }

    [Fact]
    [Trait("SEC-P", "P03")]
    public async Task SecP03_DirectSqlTeamMembershipDoesNotGrantAccess()
    {
        var s = new Scenario(fixture, "SEC-P03", "Direct SQL team membership: forged membership cannot grant access; checked against authority revision");
        await ScenarioRunner.RunAsync(s, async () =>
        {
            await s.BootstrapAsync();
            var team = await fixture.Api.PostAsync("/api/v1/teams", s.Token(s.Owner), new { teamName = "Investigators" });
            s.Evidence.ObserveHttp("create team", team, "teamId");
            Assert.Equal(HttpStatusCode.Created, team.Status);
            var teamId = team.Root.GetProperty("teamId").GetGuid();
            var addOwner = await fixture.Api.PostAsync($"/api/v1/teams/{teamId:D}/members", s.Token(s.Owner), new { userId = s.Owner.AuthorityUserId });
            Assert.True(addOwner.Root.GetProperty("command").GetProperty("effective").GetBoolean(), addOwner.Body);
            await s.CreateFormAndRecordAsync(ownerTeamId: teamId);
            var gateBefore = await fixture.Authority.GateAsync(s.OrganizationId);

            var forged = fixture.Stack.AttackerSql($"INSERT INTO team_memberships (team_membership_id, tenant_id, team_id, user_id, membership_role, authority_command_id, created_on, created_by_user_id) VALUES (gen_random_uuid(), '{s.OrganizationId:D}', '{teamId:D}', '{s.Attacker.AuthorityUserId:D}', 'Member', NULL, now(), '{s.Owner.AuthorityUserId:D}'); SELECT count(*) FROM team_memberships WHERE team_id = '{teamId:D}' AND user_id = '{s.Attacker.AuthorityUserId:D}';");
            s.Evidence.Observe("hostile INSERT team_memberships", new { rows = forged.Trim() });
            Assert.Equal("1", forged.Trim());

            var attackerRead = await s.ReadAsync(s.Attacker);
            s.Evidence.ObserveHttp("attacker read with forged membership", attackerRead);
            Assert.Equal(HttpStatusCode.NotFound, attackerRead.Status);
            var gateMid = await fixture.Authority.GateAsync(s.OrganizationId);
            Assert.Equal(gateBefore.GetProperty("authorization_revision").GetInt64(), gateMid.GetProperty("authorization_revision").GetInt64());

            var addAttacker = await fixture.Api.PostAsync($"/api/v1/teams/{teamId:D}/members", s.Token(s.Owner), new { userId = s.Attacker.AuthorityUserId });
            s.Evidence.ObserveHttp("legitimate AddTeamMember", addAttacker);
            Assert.True(addAttacker.Root.GetProperty("command").GetProperty("effective").GetBoolean(), addAttacker.Body);
            var memberRead = await s.WaitReadableAsync(s.Attacker);
            s.Evidence.ObserveHttp("member read after legitimate membership", memberRead, "isCompleteProjection");
            Assert.Equal(HttpStatusCode.OK, memberRead.Status);
            Assert.True(memberRead.Root.GetProperty("isCompleteProjection").GetBoolean());

            var remove = await fixture.Api.PostAsync($"/api/v1/teams/{teamId:D}/members/{s.Attacker.AuthorityUserId:D}/remove", s.Token(s.Owner), new { });
            s.Evidence.ObserveHttp("RemoveTeamMember", remove);
            Assert.True(remove.Root.GetProperty("command").GetProperty("effective").GetBoolean(), remove.Body);
            var removedRead = await s.ReadAsync(s.Attacker);
            s.Evidence.ObserveHttp("former member read after removal (local projection row still present)", removedRead);
            Assert.Equal(HttpStatusCode.NotFound, removedRead.Status);
            var gateAfter = await fixture.Authority.GateAsync(s.OrganizationId);
            s.Evidence.AuthorityRevision = gateAfter.GetProperty("authorization_revision").GetInt64();
        });
    }

    [Fact]
    [Trait("SEC-P", "P06")]
    public async Task SecP06_RevocationBarrierDefeatsCachedAllow()
    {
        var s = new Scenario(fixture, "SEC-P06", "Revocation with cached allow: after the effective revocation barrier a still-valid session token cannot start protected reads; while the barrier is pending no stale allow is returned");
        await ScenarioRunner.RunAsync(s, async () =>
        {
            await s.BootstrapAsync();
            await s.CreateFormAndRecordAsync();
            var readerToken = s.Token(s.Reader); // one long-lived "session" token reused across the revocation
            var share = await fixture.Api.PostAsync($"/api/v1/form-records/{s.FormRecordId:D}/shares", s.Token(s.Owner), new { recipientUserId = s.Reader.AuthorityUserId, recipientRole = "Reader", fieldKeys = new[] { "given_name", "case_note" } });
            Assert.Equal(HttpStatusCode.Created, share.Status);
            var shareId = share.Root.GetProperty("recordShareId").GetGuid();
            var allowed = await s.WaitReadableAsync(s.Reader);
            s.Evidence.ObserveHttp("reader read before revocation", allowed, "isCompleteProjection");
            Assert.Equal(HttpStatusCode.OK, allowed.Status);
            Assert.Contains("protected note", allowed.Body, StringComparison.Ordinal);

            // Close the projection barrier, revoke, and prove that the committed-but-not-projected revocation never yields a stale allow.
            fixture.Stack.Pause("spicedb");
            try
            {
                // The business API's own pre-check needs SpiceDB, so the revocation is committed through the authority command
                // endpoint exactly as the API would (workload credential + owner assertion); the authority commits it without SpiceDB.
                var revoke = await s.SubmitAuthorityCommandAsync(s.Owner, "RevokeShare", "FormRecord", s.FormRecordId, new { record_share_id = shareId, form_record_id = s.FormRecordId });
                Assert.Equal(HttpStatusCode.Accepted, revoke.Status);
                Assert.Equal("CommittedAwaitingProjection", revoke.Root.GetProperty("command_status").GetString());
                var duringBarrier = await fixture.Api.GetAsync($"/api/v1/form-records/{s.FormRecordId:D}", readerToken);
                s.Evidence.ObserveHttp("reader read with cached token during closed barrier", duringBarrier);
                Assert.NotEqual(HttpStatusCode.OK, duringBarrier.Status);
                Assert.DoesNotContain("protected note", duringBarrier.Body, StringComparison.Ordinal);
                var gate = await fixture.Authority.GateAsync(s.OrganizationId);
                s.Evidence.Observe("gate during barrier", gate.GetRawText());
                Assert.NotEqual("Ready", gate.GetProperty("projection_gate_state").GetString());
            }
            finally
            {
                fixture.Stack.Unpause("spicedb");
            }

            var gateAfter = await s.WaitGateReadyAsync();
            s.Evidence.AuthorityRevision = gateAfter.GetProperty("authorization_revision").GetInt64();
            var afterRevocation = await fixture.Api.GetAsync($"/api/v1/form-records/{s.FormRecordId:D}", readerToken);
            s.Evidence.ObserveHttp("reader read with cached token after effective revocation", afterRevocation);
            Assert.Equal(HttpStatusCode.NotFound, afterRevocation.Status);
            Assert.DoesNotContain("protected note", afterRevocation.Body, StringComparison.Ordinal);
            var ownerRead = await s.ReadAsync(s.Owner);
            Assert.Equal(HttpStatusCode.OK, ownerRead.Status);
        });
    }
}
