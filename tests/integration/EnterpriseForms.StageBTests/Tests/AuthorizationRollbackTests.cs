using System.Net;
using System.Text.Json;
using EnterpriseForms.SecurityTests.Harness;
using EnterpriseForms.StageBTests.Harness;
using Xunit;

namespace EnterpriseForms.StageBTests.Tests;

/// <summary>
/// C8 M4 T16-B and T16-S (D8-74, C-113): restoring an older database must not make a revoked authorization effective again.
/// <list type="bullet">
/// <item>T16-B: the business database is restored (hostile, data-only, triggers disabled — the SEC-P10 technique) from a dump
/// taken while the auditor was appointed; the revoke stays committed in the Authority.</item>
/// <item>T16-S: SpiceDB's datastore is restored from a dump taken before the revoke (SpiceDB stopped, pg_restore --clean,
/// SpiceDB restarted), while AuthorityDb keeps the revoke. Covers the Security Auditor (AC-8.1) and, to assess whether the
/// behaviour is general, an AC-7 designer revocation in the same window.</item>
/// </list>
/// T16-A (AuthorityDb and SpiceDB restored together) needs the G14 recovery plane and is not here. These are security-test
/// stack simulations only; nothing here is product code.
/// </summary>
[Collection(StageBStackCollectionDefinition.Name)]
public sealed class AuthorizationRollbackTests(StageBFixture fixture)
{
    private const string Auditors = "/api/v1/organization/security-auditors";

    private async Task<bool> AllowedAsync(StageBScenario s, TestUser user, string action)
    {
        var result = await fixture.Authority.SendAsync(HttpMethod.Post, "/internal/v1/authorization/check", TestIdentity.WorkloadToken(),
            new { organization_id = s.OrganizationId, resource = new { resource_kind = "Organization", resource_id = s.OrganizationId }, requested_action = action }, s.Token(user));
        return result.IsSuccess && result.Root.GetProperty("allowed").GetBoolean();
    }

    private async Task<HttpResult> CheckAsync(StageBScenario s, TestUser user, string action) =>
        await fixture.Authority.SendAsync(HttpMethod.Post, "/internal/v1/authorization/check", TestIdentity.WorkloadToken(),
            new { organization_id = s.OrganizationId, resource = new { resource_kind = "Organization", resource_id = s.OrganizationId }, requested_action = action }, s.Token(user));

    private Task<HttpResult> AppointAsync(StageBScenario s, TestUser target) =>
        fixture.Api.PostAsync(Auditors, s.Token(s.Administrator), new { userId = target.AuthorityUserId, reasonCode = "AuditCoverageAssignment", useDevelopmentStepUp = true });

    private Task<HttpResult> RemoveAsync(StageBScenario s, TestUser target) =>
        fixture.Api.PostAsync($"{Auditors}/{target.AuthorityUserId:D}/remove", s.Token(s.Administrator), new { reasonCode = "AssignmentEnded", useDevelopmentStepUp = true });

    private string CommittedRevokes(StageBScenario s) =>
        fixture.Stack.AuthorityObserverSql($"SELECT count(*) FROM security_journal_entries WHERE organization_id = '{s.OrganizationId:D}' AND journal_stream_name = 'authorization' AND journal_event_body_json LIKE '%\"RevokeSystemRole\"%' AND journal_event_body_json LIKE '%security_auditor%';").Trim();

    private async Task<List<string?>> RolesAsync(StageBScenario s, TestUser viewer, TestUser member)
    {
        var result = await fixture.Api.GetAsync("/api/v1/organization/members?pageSize=50", s.Token(viewer));
        Assert.True(result.IsSuccess, result.Body);
        return result.Root.GetProperty("members").EnumerateArray().Single(m => m.GetProperty("userId").GetGuid() == member.AuthorityUserId)
            .GetProperty("authorityRoles").EnumerateArray().Select(r => r.GetString()).ToList();
    }

    /// <summary>T16-B: an older business database cannot restore a revoked auditor.</summary>
    [Fact]
    public async Task RestoringAnOlderBusinessDatabaseCannotRestoreARevokedAuditor()
    {
        var s = new StageBScenario(fixture, "t16b-business-rollback");
        await s.BootstrapAsync();
        Assert.Equal(HttpStatusCode.NoContent, (await AppointAsync(s, s.Clerk)).Status);
        Assert.True(await AllowedAsync(s, s.Clerk, "ViewAudit"));

        var dumpFile = $"/tmp/t16b-{s.OrganizationId:N}.dump";
        var dump = fixture.Stack.AttackerShell($"pg_dump -Fc -T '\"__EFMigrationsHistory\"' -f {dumpFile} && echo DUMPED");
        Assert.Contains("DUMPED", dump, StringComparison.Ordinal);

        Assert.Equal(HttpStatusCode.NoContent, (await RemoveAsync(s, s.Clerk)).Status);
        Assert.False(await AllowedAsync(s, s.Clerk, "ViewAudit"));
        Assert.Equal("1", fixture.Stack.AttackerSql($"SELECT count(*) FROM audit_events WHERE tenant_id = '{s.OrganizationId:D}' AND event_action = 'SECURITY_AUDITOR_REMOVED';").Trim());

        // Hostile restore of the whole business database to the snapshot (SEC-P10 technique).
        var restore = fixture.Stack.AttackerShell($"""
            psql -v ON_ERROR_STOP=1 -qAt -c "DO \$\$ DECLARE r record; BEGIN FOR r IN SELECT tablename FROM pg_tables WHERE schemaname = 'public' AND tablename <> '__EFMigrationsHistory' LOOP EXECUTE 'TRUNCATE TABLE public.' || quote_ident(r.tablename) || ' CASCADE'; END LOOP; END \$\$;" &&
            pg_restore --data-only --disable-triggers --no-owner -d "$PGDATABASE" {dumpFile} 2>&1 && echo RESTORED &&
            rm -f {dumpFile}
            """, timeoutSeconds: 180);
        Assert.Contains("RESTORED", restore, StringComparison.Ordinal);
        Assert.Equal("0", fixture.Stack.AttackerSql($"SELECT count(*) FROM audit_events WHERE tenant_id = '{s.OrganizationId:D}' AND event_action = 'SECURITY_AUDITOR_REMOVED';").Trim()); // the business really is older

        // The Authority still holds the revoke and decides; nothing in the business database re-grants the role.
        Assert.Equal("1", CommittedRevokes(s));
        Assert.False(await AllowedAsync(s, s.Clerk, "ViewAudit"));
        Assert.DoesNotContain("SecurityAuditor", await RolesAsync(s, s.Administrator, s.Clerk));
        var again = await fixture.Api.PostAsync($"{Auditors}/{s.Clerk.AuthorityUserId:D}/remove", s.Token(s.Administrator), new { reasonCode = "AssignmentEnded", useDevelopmentStepUp = true });
        Assert.Equal("INVALID_COMMAND", again.ErrorCode); // the Authority knows the role is gone
    }

    /// <summary>
    /// T16-S: an older SpiceDB datastore cannot make a revoked authorization effective. Expected: the revoked auditor and the
    /// revoked designer are denied (fail closed), whatever SpiceDB now holds. A stale "allowed" is a security defect (D8-74).
    /// </summary>
    [Fact]
    public async Task RestoringAnOlderSpiceDbDatastoreCannotMakeARevokedAuthorizationEffective()
    {
        var s = new StageBScenario(fixture, "t16s-spicedb-rollback");
        await s.BootstrapAsync();
        Assert.Equal(HttpStatusCode.NoContent, (await AppointAsync(s, s.Clerk)).Status);
        Assert.True(await AllowedAsync(s, s.Clerk, "ViewAudit"));
        Assert.True(await AllowedAsync(s, s.DesignerOnly, "DesignForm"));

        var dumpFile = $"/tmp/t16s-{s.OrganizationId:N}.dump";
        Assert.Contains("DUMPED", fixture.Stack.SpiceDbDatastoreSimulationShell($"pg_dump -Fc -f {dumpFile} && echo DUMPED"), StringComparison.Ordinal);

        // Revoke both after the snapshot: the auditor (AC-8.1) and the designer role (AC-7, one owner).
        Assert.Equal(HttpStatusCode.NoContent, (await RemoveAsync(s, s.Clerk)).Status);
        var designer = await fixture.Api.PostAsync($"/api/v1/organization/members/{s.DesignerOnly.AuthorityUserId:D}/roles/Designer/remove", s.Token(s.Designer), new { reason = "T16-S designer revoke", useDevelopmentStepUp = true });
        Assert.True(designer.IsSuccess, designer.Body);
        Assert.False(await AllowedAsync(s, s.Clerk, "ViewAudit"));
        Assert.False(await AllowedAsync(s, s.DesignerOnly, "DesignForm"));

        // Restore SpiceDB's datastore from the pre-revoke dump; AuthorityDb keeps both revokes.
        try
        {
            fixture.Stack.Stop("spicedb");
            var restored = fixture.Stack.SpiceDbDatastoreSimulationShell($"pg_restore --clean --if-exists --no-owner -d spicedb {dumpFile} 2>&1; echo RESTORED; rm -f {dumpFile}", 180);
            Assert.Contains("RESTORED", restored, StringComparison.Ordinal);
        }
        finally
        {
            fixture.Stack.Compose("up -d --wait spicedb", 180);
        }

        // Observations first (recorded in the test output), then the security expectation.
        var auditor = await CheckAsync(s, s.Clerk, "ViewAudit");
        var revokedDesigner = await CheckAsync(s, s.DesignerOnly, "DesignForm");
        var stillValid = await CheckAsync(s, s.Administrator, "AdministerSecurity");
        var gate = await fixture.Authority.GateAsync(s.OrganizationId);
        var observed = $"auditor ViewAudit {(int)auditor.Status} {auditor.Body}; revoked designer DesignForm {(int)revokedDesigner.Status} {revokedDesigner.Body}; valid administrator AdministerSecurity {(int)stillValid.Status} {stillValid.Body}; gate {gate}";
        var auditorAllowed = auditor.IsSuccess && auditor.Root.GetProperty("allowed").GetBoolean();
        var designerAllowed = revokedDesigner.IsSuccess && revokedDesigner.Root.GetProperty("allowed").GetBoolean();
        Assert.False(auditorAllowed || designerAllowed, $"T16-S DEFECT: a revoked authorization is effective again after a SpiceDB datastore restore (auditor allowed: {auditorAllowed}; AC-7 designer allowed: {designerAllowed}). Observed: {observed}");

        // C8 M4 S1 (RB-01): fail closed for the whole organization, persistent quarantine with audit-stream evidence and a Critical alert.
        Assert.Equal((HttpStatusCode.Locked, "INTEGRITY_QUARANTINE"), (auditor.Status, auditor.ErrorCode));
        Assert.Equal((HttpStatusCode.Locked, "INTEGRITY_QUARANTINE"), (revokedDesigner.Status, revokedDesigner.ErrorCode));
        Assert.Equal(HttpStatusCode.Locked, stillValid.Status);
        Assert.Equal("Quarantined", gate.GetProperty("projection_gate_state").GetString());
        Assert.Equal("true|PROJECTION_MARKER_BEHIND", fixture.Stack.AuthorityObserverSql($"SELECT integrity_quarantine_enabled::text || '|' || integrity_quarantine_reason_code FROM organization_authorization_states WHERE organization_id = '{s.OrganizationId:D}';").Trim());
        Assert.Equal("1", fixture.Stack.AuthorityObserverSql($"SELECT count(*) FROM security_journal_entries WHERE organization_id = '{s.OrganizationId:D}' AND journal_stream_name = 'audit' AND journal_event_kind = 'ProjectionIntegrityQuarantined';").Trim());
        Assert.Contains($"SECURITY ALERT PROJECTION_INTEGRITY_QUARANTINE organization {s.OrganizationId:D}", fixture.Stack.Logs("authority", 5000), StringComparison.Ordinal);
    }
}
