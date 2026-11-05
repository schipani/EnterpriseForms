using System.Collections.Concurrent;
using System.Net;
using EnterpriseForms.SecurityTests.Harness;
using EnterpriseForms.StageBTests.Harness;
using Xunit;

namespace EnterpriseForms.StageBTests.Tests;

/// <summary>
/// C8 M4 S1 rollback regression RB-02…RB-15 (Design B revision 2 §2.11; RB-01 is T16-S in <see cref="AuthorizationRollbackTests"/>).
/// Each test changes authorization after a dump of SpiceDB's datastore, puts an older datastore (or AuthorityDb state) back, and
/// asserts that no revoked authorization becomes effective: the decision is denied, the organization is quarantined
/// persistently, an audit-stream event is journaled and a Critical alert is logged. Restores are security-test simulations only.
/// </summary>
[Collection(StageBStackCollectionDefinition.Name)]
public sealed class ProjectionIntegrityRollbackTests(StageBFixture fixture)
{
    private const string Auditors = "/api/v1/organization/security-auditors";

    // ---------------------------------------------------------------- RB-02…RB-06: every SpiceDB-resolved permission area

    /// <summary>RB-02 record access (AC-5): a revoked record share is not effective again after a SpiceDB rollback.</summary>
    [Fact]
    public async Task Rb02ARevokedRecordShareIsNotRestoredByASpiceDbRollback()
    {
        var (s, accused, shareId) = await SharedRecordAsync("rb02-record-share");
        var dump = Dump();
        await RevokeShareAsync(s, accused, shareId);
        Restore(dump, "restart");

        Assert.False(Allowed(await CheckAsync(s, s.Reader, "FormRecord", accused, "Discover")));
        Assert.False(Allowed(await CheckAsync(s, s.Reader, "FormRecord", accused, "Read")));
        Assert.False((await s.ProjectionAsync(s.Reader, accused)).IsSuccess);
        Assert.False((await fixture.Api.GetAsync($"/api/v1/form-records/{accused:D}/history", s.Token(s.Reader))).IsSuccess);
        await AssertQuarantinedAsync(s, "PROJECTION_MARKER_BEHIND");
        Assert.False((await s.ProjectionAsync(s.Clerk, accused)).IsSuccess); // fail closed for the whole organization, owner included
    }

    /// <summary>RB-03 field-level sharing (AC-5): revoked field-item rights are not effective again (CheckBulk and verified projection paths).</summary>
    [Fact]
    public async Task Rb03RevokedFieldItemRightsAreNotRestoredByASpiceDbRollback()
    {
        var (s, accused, shareId) = await SharedRecordAsync("rb03-field-share");
        var items = LeafItems(accused);
        var before = await FieldReadsAsync(s, s.Reader, items);
        Assert.True(before.Count(a => a) >= 2, "the share must grant the two shared field items before the rollback");
        var dump = Dump();
        await RevokeShareAsync(s, accused, shareId);
        Assert.DoesNotContain(true, await FieldReadsAsync(s, s.Reader, items));
        Restore(dump, "restart");

        var batch = await fixture.Authority.SendAsync(HttpMethod.Post, "/internal/v1/authorization/check-batch", TestIdentity.WorkloadToken(),
            new { checks = items.Select(i => new { organization_id = s.OrganizationId, resource = new { resource_kind = "FieldItem", resource_id = i }, requested_action = "Read" }).ToArray() }, s.Token(s.Reader));
        Assert.False(batch.IsSuccess && batch.Root.GetProperty("decisions").EnumerateArray().Any(d => d.GetProperty("allowed").GetBoolean()), "a revoked field item is readable again: " + batch.Body);
        Assert.Equal(HttpStatusCode.Locked, batch.Status);
        Assert.False((await s.ProjectionAsync(s.Reader, accused)).IsSuccess);
        await AssertQuarantinedAsync(s, "PROJECTION_MARKER_BEHIND");
    }

    /// <summary>RB-04 team permissions (AC-9 area): a member removed from a sharing team does not regain access through a rollback.</summary>
    [Fact]
    public async Task Rb04ARemovedTeamMemberDoesNotRegainSharedAccessAfterASpiceDbRollback()
    {
        var s = new StageBScenario(fixture, "rb04-team-share");
        await s.BootstrapAsync();
        await s.BuildAccusedWorkspaceAsync();
        var accused = await s.CreateAccusedAsync(s.Clerk);
        var created = await fixture.Api.PostAsync("/api/v1/teams", s.Token(s.Designer), new { teamName = "rb04 " + s.Name });
        Assert.Equal(HttpStatusCode.Created, created.Status);
        var teamId = created.Root.GetProperty("teamId").GetGuid();
        await s.WaitGateReadyAsync();
        var added = await fixture.Api.PostAsync($"/api/v1/teams/{teamId:D}/members", s.Token(s.Designer), new { userId = s.Reader.AuthorityUserId }, "rb04-add-" + Guid.NewGuid().ToString("N"));
        Assert.True(added.IsSuccess, added.Body);
        await s.WaitGateReadyAsync();
        var share = await fixture.Api.PostAsync($"/api/v1/form-records/{accused:D}/shares", s.Token(s.Clerk), new { recipientUserId = (Guid?)null, recipientTeamId = teamId, recipientRole = "Reader", fieldKeys = new[] { "given_name" } });
        Assert.True(share.IsSuccess, share.Body);
        Assert.True((await s.WaitProjectionAsync(s.Reader, accused, TimeSpan.FromSeconds(40))).IsSuccess);

        var dump = Dump();
        var removed = await fixture.Api.PostAsync($"/api/v1/teams/{teamId:D}/members/{s.Reader.AuthorityUserId:D}/remove", s.Token(s.Designer), new { }, "rb04-remove-" + Guid.NewGuid().ToString("N"));
        Assert.True(removed.IsSuccess, removed.Body);
        await WaitUntilAsync(async () => !Allowed(await CheckAsync(s, s.Reader, "FormRecord", accused, "Discover")), "team removal effective");
        Restore(dump, "restart");

        Assert.False(Allowed(await CheckAsync(s, s.Reader, "FormRecord", accused, "Discover")));
        Assert.False((await s.ProjectionAsync(s.Reader, accused)).IsSuccess);
        await AssertQuarantinedAsync(s, "PROJECTION_MARKER_BEHIND");
    }

    /// <summary>RB-05 discovery and search (AC-6): LookupResources paths (discover, user catalogue) never list a record after a rollback.</summary>
    [Fact]
    public async Task Rb05DiscoveryAndCatalogueNeverListARevokedRecordAfterASpiceDbRollback()
    {
        var (s, accused, shareId) = await SharedRecordAsync("rb05-discovery");
        var listed = await fixture.Api.GetAsync("/api/v1/form-records?includeChildren=true&pageSize=50", s.Token(s.Reader));
        Assert.Contains(accused, listed.Root.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("formRecordId").GetGuid()));
        var dump = Dump();
        await RevokeShareAsync(s, accused, shareId);
        Restore(dump, "restart");

        var discover = await fixture.Authority.SendAsync(HttpMethod.Post, "/internal/v1/authorization/discover", TestIdentity.WorkloadToken(), new { organization_id = s.OrganizationId, page_size = 50 }, s.Token(s.Reader));
        Assert.Equal(HttpStatusCode.Locked, discover.Status); // no partial or stale list
        var catalogue = await fixture.Authority.SendAsync(HttpMethod.Post, "/internal/v1/search-catalogue/", TestIdentity.WorkloadToken(), new { organization_id = s.OrganizationId, form_definition_id = s.AccusedFormId, maximum_entries = 100 }, s.Token(s.Reader));
        Assert.False(catalogue.IsSuccess, catalogue.Body);
        var after = await fixture.Api.GetAsync("/api/v1/form-records?includeChildren=true&pageSize=50", s.Token(s.Reader));
        Assert.False(after.IsSuccess && after.Root.GetProperty("items").EnumerateArray().Any(i => i.GetProperty("formRecordId").GetGuid() == accused), after.Body);
        await AssertQuarantinedAsync(s, "PROJECTION_MARKER_BEHIND");
    }

    /// <summary>RB-06 form permissions (AC-6/AC-7): revoked form use (create_record) and the organization designer role stay revoked.</summary>
    [Fact]
    public async Task Rb06RevokedFormPermissionsStayRevokedAfterASpiceDbRollback()
    {
        var s = new StageBScenario(fixture, "rb06-form-permissions");
        await s.BootstrapAsync();
        await s.BuildAccusedWorkspaceAsync();
        var grant = await fixture.Api.PostAsync($"/api/v1/form-definitions/{s.AccusedFormId:D}/access", s.Token(s.Designer), Change(grantUsers: [s.Reader.AuthorityUserId], reason: "rb06 grant"));
        Assert.True(grant.IsSuccess, grant.Body);
        await WaitUntilAsync(async () => Allowed(await CheckAsync(s, s.Reader, "FormDefinition", s.AccusedFormId, "CreateRecord")), "form use effective");
        Assert.True(Allowed(await CheckAsync(s, s.DesignerOnly, "Organization", s.OrganizationId, "DesignForm")));

        var dump = Dump();
        var revoke = await fixture.Api.PostAsync($"/api/v1/form-definitions/{s.AccusedFormId:D}/access", s.Token(s.Designer), Change(revokeUsers: [s.Reader.AuthorityUserId], reason: "rb06 revoke"));
        Assert.True(revoke.IsSuccess, revoke.Body);
        var designer = await fixture.Api.PostAsync($"/api/v1/organization/members/{s.DesignerOnly.AuthorityUserId:D}/roles/Designer/remove", s.Token(s.Designer), new { reason = "rb06 designer revoke", useDevelopmentStepUp = true });
        Assert.True(designer.IsSuccess, designer.Body);
        await WaitUntilAsync(async () => !Allowed(await CheckAsync(s, s.Reader, "FormDefinition", s.AccusedFormId, "CreateRecord")), "form use revoke effective");
        Restore(dump, "restart");

        Assert.False(Allowed(await CheckAsync(s, s.Reader, "FormDefinition", s.AccusedFormId, "CreateRecord")));
        Assert.False(Allowed(await CheckAsync(s, s.DesignerOnly, "Organization", s.OrganizationId, "DesignForm")));
        Assert.False(Allowed(await CheckAsync(s, s.DesignerOnly, "FormDefinition", s.AccusedFormId, "DesignForm")));
        await AssertQuarantinedAsync(s, "PROJECTION_MARKER_BEHIND");
    }

    // ---------------------------------------------------------------- RB-07…RB-09: isolation and marker classes

    /// <summary>RB-07 cross-organization isolation: only the changed organization is quarantined; an unchanged one stays correct.</summary>
    [Fact]
    public async Task Rb07OnlyTheRolledBackOrganizationIsQuarantinedAndNoAccessCrossesOrganizations()
    {
        var x = new StageBScenario(fixture, "rb07-org-x");
        await x.BootstrapAsync();
        var y = new StageBScenario(fixture, "rb07-org-y");
        await y.BootstrapAsync();
        Assert.Equal(HttpStatusCode.NoContent, (await AppointAsync(x, x.Clerk)).Status);
        Assert.Equal(HttpStatusCode.NoContent, (await AppointAsync(y, y.Clerk)).Status);
        var dump = Dump();
        Assert.Equal(HttpStatusCode.NoContent, (await RemoveAsync(x, x.Clerk)).Status); // only X changes after the dump
        Restore(dump, "restart");

        Assert.False(Allowed(await CheckAsync(x, x.Clerk, "Organization", x.OrganizationId, "ViewAudit")));
        await AssertQuarantinedAsync(x, "PROJECTION_MARKER_BEHIND");
        Assert.True(Allowed(await CheckAsync(y, y.Clerk, "Organization", y.OrganizationId, "ViewAudit")), "the unchanged organization must keep deciding correctly");
        Assert.False(Allowed(await CheckAsync(y, y.Designer, "Organization", y.OrganizationId, "ViewAudit")));
        Assert.Equal("Ready", (await fixture.Authority.GateAsync(y.OrganizationId)).GetProperty("projection_gate_state").GetString());
        // Neither organization's subjects act in the other: unbound identities are refused before any SpiceDB read.
        Assert.False(Allowed(await fixture.Authority.SendAsync(HttpMethod.Post, "/internal/v1/authorization/check", TestIdentity.WorkloadToken(),
            new { organization_id = y.OrganizationId, resource = new { resource_kind = "Organization", resource_id = y.OrganizationId }, requested_action = "ViewAudit" }, StageBScenario.TokenFor(x.Clerk, y.OrganizationId))));
        Assert.False(Allowed(await fixture.Authority.SendAsync(HttpMethod.Post, "/internal/v1/authorization/check", TestIdentity.WorkloadToken(),
            new { organization_id = x.OrganizationId, resource = new { resource_kind = "Organization", resource_id = x.OrganizationId }, requested_action = "ViewAudit" }, StageBScenario.TokenFor(y.Clerk, x.OrganizationId))));
    }

    /// <summary>RB-08 missing marker: a datastore from before the organization existed has no marker for it.</summary>
    [Fact]
    public async Task Rb08ADatastoreWithoutTheOrganizationsMarkerQuarantinesAsMissing()
    {
        var dump = Dump();
        var s = new StageBScenario(fixture, "rb08-missing");
        await s.BootstrapAsync();
        Assert.True(Allowed(await CheckAsync(s, s.Administrator, "Organization", s.OrganizationId, "AdministerSecurity")));
        Restore(dump, "restart");

        var check = await CheckAsync(s, s.Administrator, "Organization", s.OrganizationId, "AdministerSecurity");
        Assert.Equal(HttpStatusCode.Locked, check.Status);
        await AssertQuarantinedAsync(s, "PROJECTION_MARKER_MISSING");
    }

    /// <summary>RB-09 marker ahead: AuthorityDb older than SpiceDB (simulated AuthorityDb restore) is never decided on.</summary>
    [Fact]
    public async Task Rb09AnAuthorityStateOlderThanSpiceDbQuarantinesAsAhead()
    {
        var s = new StageBScenario(fixture, "rb09-ahead");
        await s.BootstrapAsync();
        Assert.Equal(HttpStatusCode.NoContent, (await AppointAsync(s, s.Clerk)).Status);
        SetAuthorityStateBackOneRevision(s);
        var check = await CheckAsync(s, s.Clerk, "Organization", s.OrganizationId, "ViewAudit");
        Assert.Equal(HttpStatusCode.Locked, check.Status);
        await AssertQuarantinedAsync(s, "PROJECTION_MARKER_AHEAD");
    }

    // ---------------------------------------------------------------- RB-10: permanence

    /// <summary>
    /// RB-10 persistence: a quarantine survives an Authority restart, refuses security commands, keeps the projector from writing
    /// (a pending revision is never projected, the SpiceDB marker is unchanged) and cannot be cleared or re-reasoned by UPDATE.
    /// </summary>
    [Fact]
    public async Task Rb10AQuarantineIsPermanentAcrossRestartsCommandsProjectionAndDirectUpdates()
    {
        var s = new StageBScenario(fixture, "rb10-permanent");
        await s.BootstrapAsync();
        var markerBefore = Markers(s);

        // A committed revision whose projection is still pending (SpiceDB paused), then the organization is quarantined.
        fixture.Stack.Pause("spicedb");
        try
        {
            // Submitted directly to the Authority (the business API's pre-check needs SpiceDB): it commits in AuthorityDb at
            // once and cannot be projected while SpiceDB is paused.
            var gate = await fixture.Authority.GateAsync(s.OrganizationId);
            var committed = await fixture.Authority.SendAsync(HttpMethod.Post, "/internal/v1/security-commands", TestIdentity.WorkloadToken(), new
            {
                organization_id = s.OrganizationId,
                command_kind = "GrantSystemRole",
                resource = new { resource_kind = "Organization", resource_id = s.OrganizationId },
                expected_authorization_revision = gate.GetProperty("authorization_revision").GetInt64(),
                command_payload = new { authority_user_id = s.Clerk.AuthorityUserId, role_name = "security_auditor", reason_code = "AuditCoverageAssignment", step_up_evidence = "synthetic-development-step-up" },
            }, s.Token(s.Administrator), Guid.NewGuid().ToString("N"));
            Assert.True(committed.IsSuccess, committed.Body);
            Assert.True(AuthorityState(s).Split('|') is [var a, var p, ..] && long.Parse(a) == long.Parse(p) + 1, "a committed revision pending projection: " + AuthorityState(s));
            fixture.Stack.AuthorityRestoreSimulationSql($"UPDATE organization_authorization_states SET integrity_quarantine_enabled = true, integrity_quarantine_reason_code = 'RB10_TEST_QUARANTINE' WHERE organization_id = '{s.OrganizationId:D}';");
        }
        finally
        {
            fixture.Stack.Unpause("spicedb");
        }

        await Task.Delay(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken); // several projector passes
        var state = AuthorityState(s).Split('|');
        Assert.Equal(long.Parse(state[0]), long.Parse(state[1]) + 1); // the pending revision was never projected
        Assert.Equal(markerBefore, Markers(s)); // and SpiceDB was not written

        fixture.Stack.Compose("restart authority", 180);
        await fixture.Authority.WaitReadyAsync(TimeSpan.FromSeconds(90));
        Assert.Equal("Quarantined", (await fixture.Authority.GateAsync(s.OrganizationId)).GetProperty("projection_gate_state").GetString());
        Assert.Equal(HttpStatusCode.Locked, (await CheckAsync(s, s.Administrator, "Organization", s.OrganizationId, "AdministerSecurity")).Status);
        Assert.False((await AppointAsync(s, s.Reader)).IsSuccess); // refused through the business API (its pre-check is already 423)
        var afterRestart = await fixture.Authority.GateAsync(s.OrganizationId);
        var direct = await fixture.Authority.SendAsync(HttpMethod.Post, "/internal/v1/security-commands", TestIdentity.WorkloadToken(), new
        {
            organization_id = s.OrganizationId,
            command_kind = "GrantSystemRole",
            resource = new { resource_kind = "Organization", resource_id = s.OrganizationId },
            expected_authorization_revision = afterRestart.GetProperty("authorization_revision").GetInt64(),
            command_payload = new { authority_user_id = s.Reader.AuthorityUserId, role_name = "security_auditor", reason_code = "AuditCoverageAssignment", step_up_evidence = "synthetic-development-step-up" },
        }, s.Token(s.Administrator), Guid.NewGuid().ToString("N"));
        Assert.Equal((HttpStatusCode.Locked, "INTEGRITY_QUARANTINE"), (direct.Status, direct.ErrorCode)); // and directly at the Authority
        Assert.NotEqual("0", fixture.Stack.AuthorityObserverSql($"SELECT count(*) FROM security_commands WHERE organization_id = '{s.OrganizationId:D}' AND rejection_reason_code = 'INTEGRITY_QUARANTINE';").Trim());

        var clear = Assert.Throws<InvalidOperationException>(() => fixture.Stack.AuthorityRestoreSimulationSql($"UPDATE organization_authorization_states SET integrity_quarantine_enabled = false WHERE organization_id = '{s.OrganizationId:D}';"));
        Assert.Contains("integrity quarantine is permanent", clear.Message, StringComparison.Ordinal);
        var rename = Assert.Throws<InvalidOperationException>(() => fixture.Stack.AuthorityRestoreSimulationSql($"UPDATE organization_authorization_states SET integrity_quarantine_reason_code = 'OTHER' WHERE organization_id = '{s.OrganizationId:D}';"));
        Assert.Contains("integrity quarantine is permanent", rename.Message, StringComparison.Ordinal);
        Assert.Equal("true|RB10_TEST_QUARANTINE", fixture.Stack.AuthorityObserverSql($"SELECT integrity_quarantine_enabled::text || '|' || integrity_quarantine_reason_code FROM organization_authorization_states WHERE organization_id = '{s.OrganizationId:D}';").Trim());
        // The rollback fence (C-117): the pre-S1 token column stays NULL, so a pre-S1 Authority build could neither open nor project.
        Assert.Equal("0", fixture.Stack.AuthorityObserverSql("SELECT count(*) FROM organization_authorization_states WHERE projection_zed_token IS NOT NULL;").Trim());
    }

    // ---------------------------------------------------------------- RB-11…RB-15: races, restore variants, periodic check, outage

    /// <summary>RB-11 no false quarantine: concurrent decisions while grants and revokes commit and project.</summary>
    [Fact]
    public async Task Rb11ConcurrentDecisionsDuringLegitimateChangesNeverQuarantine()
    {
        var s = new StageBScenario(fixture, "rb11-no-false-quarantine");
        await s.BootstrapAsync();
        var outcomes = new ConcurrentDictionary<string, int>();
        using var stop = new CancellationTokenSource();
        var workers = Enumerable.Range(0, 8).Select(i => Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                var result = await CheckAsync(s, i % 2 == 0 ? s.Clerk : s.Administrator, "Organization", s.OrganizationId, i % 2 == 0 ? "ViewAudit" : "AdministerSecurity");
                outcomes.AddOrUpdate(result.IsSuccess ? (Allowed(result) ? "allowed" : "denied") : ((int)result.Status).ToString(System.Globalization.CultureInfo.InvariantCulture) + ":" + result.ErrorCode, 1, (_, c) => c + 1);
            }
        }, TestContext.Current.CancellationToken)).ToArray();
        for (var i = 0; i < 10; i++)
        {
            Assert.Equal(HttpStatusCode.NoContent, (await AppointAsync(s, s.Clerk)).Status);
            Assert.Equal(HttpStatusCode.NoContent, (await RemoveAsync(s, s.Clerk)).Status);
        }

        await stop.CancelAsync();
        await Task.WhenAll(workers);
        var summary = string.Join(", ", outcomes.Select(o => o.Key + "=" + o.Value));
        Assert.True(outcomes.Values.Sum() >= 200, "at least 200 concurrent decisions: " + summary);
        Assert.DoesNotContain(outcomes.Keys, k => k.StartsWith("423", StringComparison.Ordinal));
        Assert.Equal("Ready", (await fixture.Authority.GateAsync(s.OrganizationId)).GetProperty("projection_gate_state").GetString());
        Assert.Equal("0", fixture.Stack.AuthorityObserverSql($"SELECT count(*) FROM security_journal_entries WHERE organization_id = '{s.OrganizationId:D}' AND journal_event_kind = 'ProjectionIntegrityQuarantined';").Trim());
    }

    /// <summary>RB-12 restore without restart: an older datastore restored under a running SpiceDB (warm caches) is detected.</summary>
    [Fact]
    public async Task Rb12ALiveRestoreUnderARunningSpiceDbIsDetected()
    {
        var s = new StageBScenario(fixture, "rb12-live-restore");
        await s.BootstrapAsync();
        Assert.Equal(HttpStatusCode.NoContent, (await AppointAsync(s, s.Clerk)).Status);
        Assert.True(Allowed(await CheckAsync(s, s.Clerk, "Organization", s.OrganizationId, "ViewAudit"))); // warm caches
        var dump = Dump();
        Assert.Equal(HttpStatusCode.NoContent, (await RemoveAsync(s, s.Clerk)).Status);
        Restore(dump, "live");

        Assert.False(Allowed(await CheckAsync(s, s.Clerk, "Organization", s.OrganizationId, "ViewAudit")));
        await AssertQuarantinedAsync(s);
    }

    /// <summary>RB-13 other datastore: a replaced, empty datastore and an old dump restored into a recreated one both fail closed.</summary>
    [Fact]
    public async Task Rb13AReplacedOrRecreatedDatastoreFailsClosed()
    {
        var replaced = new StageBScenario(fixture, "rb13-replaced");
        await replaced.BootstrapAsync();
        var full = Dump();
        try
        {
            fixture.Stack.ReplaceSpiceDbDatastoreWithEmpty();
            var check = await CheckAsync(replaced, replaced.Administrator, "Organization", replaced.OrganizationId, "AdministerSecurity");
            Assert.Equal(HttpStatusCode.Locked, check.Status);
            await AssertQuarantinedAsync(replaced); // SCHEMA_MISSING (or MISSING after a schema rewrite)
        }
        finally
        {
            Restore(full, "recreate"); // put the whole datastore back for the rest of the shard
        }

        var recreated = new StageBScenario(fixture, "rb13-recreated");
        await recreated.BootstrapAsync();
        Assert.Equal(HttpStatusCode.NoContent, (await AppointAsync(recreated, recreated.Clerk)).Status);
        var before = Dump();
        Assert.Equal(HttpStatusCode.NoContent, (await RemoveAsync(recreated, recreated.Clerk)).Status);
        Restore(before, "recreate");
        Assert.False(Allowed(await CheckAsync(recreated, recreated.Clerk, "Organization", recreated.OrganizationId, "ViewAudit")));
        await AssertQuarantinedAsync(recreated, "PROJECTION_MARKER_BEHIND");

        // The stack still serves a new, untouched organization correctly afterwards.
        var fresh = new StageBScenario(fixture, "rb13-fresh");
        await fresh.BootstrapAsync();
        Assert.True(Allowed(await CheckAsync(fresh, fresh.Administrator, "Organization", fresh.OrganizationId, "AdministerSecurity")));
    }

    /// <summary>RB-14 periodic check (S2): an idle organization is quarantined without any decision traffic.</summary>
    [Fact]
    public async Task Rb14ThePeriodicCheckQuarantinesAnIdleOrganization()
    {
        var s = new StageBScenario(fixture, "rb14-periodic");
        await s.BootstrapAsync();
        Assert.Equal(HttpStatusCode.NoContent, (await AppointAsync(s, s.Clerk)).Status);
        var dump = Dump();
        Assert.Equal(HttpStatusCode.NoContent, (await RemoveAsync(s, s.Clerk)).Status);
        Restore(dump, "restart");

        // No check, no business call: only the background check can notice (interval 10 s on this stack).
        await WaitUntilAsync(async () => (await fixture.Authority.GateAsync(s.OrganizationId)).GetProperty("projection_gate_state").GetString() == "Quarantined", "periodic quarantine", TimeSpan.FromSeconds(60));
        await AssertQuarantinedAsync(s, "PROJECTION_MARKER_BEHIND", "periodic");
    }

    /// <summary>RB-15 outage: an unavailable SpiceDB fails closed without quarantine and the organization recovers when it returns.</summary>
    [Fact]
    public async Task Rb15ASpiceDbOutageFailsClosedWithoutQuarantine()
    {
        var s = new StageBScenario(fixture, "rb15-outage");
        await s.BootstrapAsync();
        try
        {
            fixture.Stack.Stop("spicedb");
            var down = await CheckAsync(s, s.Administrator, "Organization", s.OrganizationId, "AdministerSecurity");
            Assert.Equal((HttpStatusCode.ServiceUnavailable, "AUTHORITY_NOT_READY"), (down.Status, down.ErrorCode));
        }
        finally
        {
            fixture.Stack.Compose("up -d --wait spicedb", 180);
        }

        await WaitUntilAsync(async () => Allowed(await CheckAsync(s, s.Administrator, "Organization", s.OrganizationId, "AdministerSecurity")), "recovery after the outage");
        Assert.Equal("Ready", (await fixture.Authority.GateAsync(s.OrganizationId)).GetProperty("projection_gate_state").GetString());
        Assert.Equal("false", fixture.Stack.AuthorityObserverSql($"SELECT integrity_quarantine_enabled::text FROM organization_authorization_states WHERE organization_id = '{s.OrganizationId:D}';").Trim());
    }

    // ---------------------------------------------------------------- helpers

    private async Task<(StageBScenario S, Guid Accused, Guid ShareId)> SharedRecordAsync(string name)
    {
        var s = new StageBScenario(fixture, name);
        await s.BootstrapAsync();
        await s.BuildAccusedWorkspaceAsync();
        var accused = await s.CreateAccusedAsync(s.Clerk);
        var share = await fixture.Api.PostAsync($"/api/v1/form-records/{accused:D}/shares", s.Token(s.Clerk), new { recipientUserId = s.Reader.AuthorityUserId, recipientTeamId = (Guid?)null, recipientRole = "Reader", fieldKeys = new[] { "given_name", "family_name" } });
        Assert.True(share.IsSuccess, share.Body);
        Assert.True((await s.WaitProjectionAsync(s.Reader, accused, TimeSpan.FromSeconds(40))).IsSuccess);
        return (s, accused, share.Root.GetProperty("recordShareId").GetGuid());
    }

    private async Task RevokeShareAsync(StageBScenario s, Guid accused, Guid shareId)
    {
        var revoke = await fixture.Api.PostAsync($"/api/v1/form-records/{accused:D}/shares/{shareId:D}/revoke", s.Token(s.Clerk), new { });
        Assert.True(revoke.IsSuccess, revoke.Body);
        await WaitUntilAsync(async () => !Allowed(await CheckAsync(s, s.Reader, "FormRecord", accused, "Discover")), "share revoke effective");
    }

    private List<Guid> LeafItems(Guid formRecordId) =>
        fixture.Stack.AuthorityObserverSql($"SELECT l.manifest_item_id FROM record_manifest_leaves l JOIN record_content_heads h ON h.current_record_manifest_commit_id = l.record_manifest_commit_id WHERE h.form_record_id = '{formRecordId:D}';")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(Guid.Parse).ToList();

    private async Task<List<bool>> FieldReadsAsync(StageBScenario s, TestUser user, List<Guid> items)
    {
        var batch = await fixture.Authority.SendAsync(HttpMethod.Post, "/internal/v1/authorization/check-batch", TestIdentity.WorkloadToken(),
            new { checks = items.Select(i => new { organization_id = s.OrganizationId, resource = new { resource_kind = "FieldItem", resource_id = i }, requested_action = "Read" }).ToArray() }, s.Token(user));
        Assert.True(batch.IsSuccess, batch.Body);
        return batch.Root.GetProperty("decisions").EnumerateArray().Select(d => d.GetProperty("allowed").GetBoolean()).ToList();
    }

    private Task<HttpResult> CheckAsync(StageBScenario s, TestUser user, string kind, Guid resourceId, string action) =>
        fixture.Authority.SendAsync(HttpMethod.Post, "/internal/v1/authorization/check", TestIdentity.WorkloadToken(),
            new { organization_id = s.OrganizationId, resource = new { resource_kind = kind, resource_id = resourceId }, requested_action = action }, s.Token(user));

    private static bool Allowed(HttpResult result) => result.IsSuccess && result.Root.GetProperty("allowed").GetBoolean();

    private Task<HttpResult> AppointAsync(StageBScenario s, TestUser target) =>
        fixture.Api.PostAsync(Auditors, s.Token(s.Administrator), new { userId = target.AuthorityUserId, reasonCode = "AuditCoverageAssignment", useDevelopmentStepUp = true });

    private Task<HttpResult> RemoveAsync(StageBScenario s, TestUser target) =>
        fixture.Api.PostAsync($"{Auditors}/{target.AuthorityUserId:D}/remove", s.Token(s.Administrator), new { reasonCode = "AssignmentEnded", useDevelopmentStepUp = true });

    private static object Change(Guid[]? grantUsers = null, Guid[]? revokeUsers = null, string reason = "rb06") =>
        new { grantUserIds = grantUsers ?? [], grantTeamIds = Array.Empty<Guid>(), revokeUserIds = revokeUsers ?? [], revokeTeamIds = Array.Empty<Guid>(), changeReason = reason };

    private string Dump()
    {
        var file = $"/tmp/rb-{Guid.NewGuid():N}.dump";
        Assert.Contains("DUMPED", fixture.Stack.SpiceDbDatastoreSimulationShell($"pg_dump -Fc -f {file} && echo DUMPED"), StringComparison.Ordinal);
        return file;
    }

    private void Restore(string dumpFile, string mode)
    {
        try
        {
            fixture.Stack.RestoreSpiceDbDatastore(dumpFile, mode);
        }
        finally
        {
            fixture.Stack.SpiceDbDatastoreSimulationShell($"rm -f {dumpFile}");
        }
    }

    private string AuthorityState(StageBScenario s) =>
        fixture.Stack.AuthorityObserverSql($"SELECT authorization_revision || '|' || projection_applied_revision || '|' || integrity_quarantine_enabled FROM organization_authorization_states WHERE organization_id = '{s.OrganizationId:D}';").Trim();

    private string Markers(StageBScenario s) =>
        string.Join(",", fixture.Stack.SpiceDbProbe(SpiceDbBehaviourContractTests.ProbeDirectory(), "marker " + s.OrganizationId.ToString("D"), 120)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Where(l => l.StartsWith("MARKER ", StringComparison.Ordinal)).Order(StringComparer.Ordinal));

    /// <summary>RB-09: what an AuthorityDb restore to the previous revision leaves (revision, applied revision and chain head one step back).</summary>
    private void SetAuthorityStateBackOneRevision(StageBScenario s) =>
        fixture.Stack.AuthorityRestoreSimulationSql($"""
            UPDATE organization_authorization_states o
               SET authorization_revision = o.authorization_revision - 1,
                   projection_applied_revision = o.projection_applied_revision - 1,
                   latest_security_journal_digest = (SELECT j.journal_event_digest FROM security_journal_entries j
                                                      WHERE j.organization_id = o.organization_id AND j.journal_stream_name = 'authorization'
                                                        AND j.journal_stream_revision = o.authorization_revision - 1)
             WHERE o.organization_id = '{s.OrganizationId:D}';
            """);

    private async Task AssertQuarantinedAsync(StageBScenario s, string? reason = null, string? phase = null)
    {
        Assert.Equal("Quarantined", (await fixture.Authority.GateAsync(s.OrganizationId)).GetProperty("projection_gate_state").GetString());
        var stored = fixture.Stack.AuthorityObserverSql($"SELECT integrity_quarantine_enabled::text || '|' || coalesce(integrity_quarantine_reason_code, '') FROM organization_authorization_states WHERE organization_id = '{s.OrganizationId:D}';").Trim();
        if (reason is null)
        {
            Assert.StartsWith("true|PROJECTION_", stored, StringComparison.Ordinal);
        }
        else
        {
            Assert.Equal("true|" + reason, stored);
        }

        var evidence = fixture.Stack.AuthorityObserverSql($"SELECT journal_event_body_json FROM security_journal_entries WHERE organization_id = '{s.OrganizationId:D}' AND journal_stream_name = 'audit' AND journal_event_kind = 'ProjectionIntegrityQuarantined';");
        Assert.False(string.IsNullOrWhiteSpace(evidence), "no ProjectionIntegrityQuarantined audit-stream event");
        if (phase is not null)
        {
            Assert.Contains("\"" + phase + "\"", evidence, StringComparison.Ordinal);
        }

        Assert.DoesNotContain(s.Clerk.AuthorityUserId.ToString("D"), evidence, StringComparison.Ordinal); // no subject identifiers in the evidence
        var logs = fixture.Stack.Logs("authority", 5000);
        Assert.Contains($"SECURITY ALERT PROJECTION_INTEGRITY_QUARANTINE organization {s.OrganizationId:D}", logs, StringComparison.Ordinal);
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> condition, string what, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(40));
        while (DateTime.UtcNow < deadline)
        {
            if (await condition())
            {
                return;
            }

            await Task.Delay(250, TestContext.Current.CancellationToken);
        }

        throw new TimeoutException("timed out waiting for " + what);
    }
}
