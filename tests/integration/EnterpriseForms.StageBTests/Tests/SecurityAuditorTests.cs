using System.Net;
using System.Text.Json;
using EnterpriseForms.SecurityTests.Harness;
using EnterpriseForms.StageBTests.Harness;
using Xunit;

namespace EnterpriseForms.StageBTests.Tests;

/// <summary>
/// C8 M4-1 (AC-8.1, C-109/C-110) on the real stack — Authority, both PostgreSQL databases, SpiceDB, Vault, business API:
/// Security Auditor appointment and removal by Security Administrators only, never by oneself (D8-62a for both roles), with
/// the owner-approved reason codes and an undisclosed note; <c>view_audit</c> grants nothing else; removal and disabling are
/// immediate for existing tokens; refusals leave only approved evidence; business-database forgery confers nothing.
/// D8-72 (C-113): reason notes, plaintext or encrypted, are refused by the business API and by the Authority until the
/// approved note encryption exists, and nothing of a refused note is persisted, journaled or archived.
/// Contract tests AC8-T01–T05, T07, T13–T15, T17–T19 and T02a. T16 (recovery replay) is not executed here: the harness has
/// no journal-replay rebuild of SpiceDB (see the M4-1 status). Nothing here is mocked.
/// </summary>
[Collection(StageBStackCollectionDefinition.Name)]
public sealed class SecurityAuditorTests(StageBFixture fixture)
{
    private const string Auditors = "/api/v1/organization/security-auditors";
    private const string Note = "NOTE-SENTINEL-7Q2 confidential context about a named colleague";
    private const string Appoint = "AuditCoverageAssignment";
    private const string Remove = "AssignmentEnded";

    private async Task<StageBScenario> ScenarioAsync(string name, bool withAdditionalOwners = false, bool withNeverSignedInMember = false)
    {
        var s = new StageBScenario(fixture, name);
        await s.BootstrapAsync(withAdditionalOwners: withAdditionalOwners, withNeverSignedInMember: withNeverSignedInMember);
        return s;
    }

    private Task<HttpResult> AppointAsync(StageBScenario s, TestUser actor, Guid target, string reasonCode = Appoint, string? note = null, bool stepUp = true) =>
        fixture.Api.PostAsync(Auditors, s.Token(actor), new { userId = target, reasonCode, reasonNote = note, useDevelopmentStepUp = stepUp });

    private Task<HttpResult> RemoveAsync(StageBScenario s, TestUser actor, Guid target, string reasonCode = Remove, string? note = null) =>
        fixture.Api.PostAsync($"{Auditors}/{target:D}/remove", s.Token(actor), new { reasonCode, reasonNote = note, useDevelopmentStepUp = true });

    /// <summary>The Authority's own decision for the user's token (no business API involved).</summary>
    private async Task<bool> AllowedAsync(StageBScenario s, TestUser user, string action)
    {
        var result = await fixture.Authority.SendAsync(HttpMethod.Post, "/internal/v1/authorization/check", TestIdentity.WorkloadToken(),
            new { organization_id = s.OrganizationId, resource = new { resource_kind = "Organization", resource_id = s.OrganizationId }, requested_action = action }, s.Token(user));
        return result.IsSuccess && result.Root.GetProperty("allowed").GetBoolean();
    }

    /// <summary>A security command sent straight to the Authority: the business API's checks play no part.</summary>
    private async Task<HttpResult> DirectAsync(StageBScenario s, TestUser actor, string kind, object payload, string? changeReason = null, long? expectedRevision = null, string? idempotencyKey = null)
    {
        var gate = await fixture.Authority.GateAsync(s.OrganizationId);
        return await fixture.Authority.SendAsync(HttpMethod.Post, "/internal/v1/security-commands", TestIdentity.WorkloadToken(), new
        {
            organization_id = s.OrganizationId,
            command_kind = kind,
            resource = new { resource_kind = "Organization", resource_id = s.OrganizationId },
            expected_authorization_revision = expectedRevision ?? gate.GetProperty("authorization_revision").GetInt64(),
            command_payload = payload,
            change_reason = changeReason,
        }, s.Token(actor), idempotencyKey ?? Guid.NewGuid().ToString("N"));
    }

    /// <summary>A direct command returns at commit, before projection: wait (bounded) until the organization gate is open again.</summary>
    private async Task WaitForGateAsync(StageBScenario s)
    {
        for (var attempt = 0; attempt < 150; attempt++)
        {
            var gate = await fixture.Authority.GateAsync(s.OrganizationId);
            if (gate.GetProperty("authorization_revision").GetInt64() == gate.GetProperty("projection_applied_revision").GetInt64())
            {
                return;
            }

            await Task.Delay(200, TestContext.Current.CancellationToken);
        }

        Assert.Fail("the organization gate did not reopen within 30 seconds");
    }

    private static object AuditorPayload(Guid target, string reasonCode = Appoint, string? note = null) =>
        new { authority_user_id = target, role_name = "security_auditor", reason_code = reasonCode, reason_note = note, step_up_evidence = "synthetic-development-step-up" };

    private string AuditorEdges(StageBScenario s) =>
        fixture.Stack.AuthorityObserverSql($"SELECT coalesce(string_agg(subject_entity_id::text, ',' ORDER BY subject_entity_id), '') FROM authority_relationship_edges WHERE organization_id = '{s.OrganizationId:D}' AND resource_type_name = 'organization' AND relationship_name = 'security_auditor' AND removed_by_security_command_id IS NULL;").Trim();

    private string Journal(StageBScenario s, string stream, string? kind = null) =>
        fixture.Stack.AuthorityObserverSql($"SELECT coalesce(string_agg(journal_event_body_json, ' ;; ' ORDER BY journal_stream_revision), '') FROM security_journal_entries WHERE organization_id = '{s.OrganizationId:D}' AND journal_stream_name = '{stream}'" + (kind is null ? ";" : $" AND journal_event_kind = '{kind}';")).Trim();

    private string RejectedRoleCommands(StageBScenario s) =>
        fixture.Stack.AuthorityObserverSql($"SELECT coalesce(string_agg(command_payload_json::text || ' | ' || coalesce(command_reason_text, ''), ' ;; '), '') FROM security_commands WHERE organization_id = '{s.OrganizationId:D}' AND command_state_name = 'Rejected' AND command_kind_name IN ('GrantSystemRole', 'RevokeSystemRole');").Trim();

    private string BusinessAudit(StageBScenario s, string action) =>
        fixture.Stack.AttackerSql($"SELECT coalesce(string_agg(actor_user_id || '|' || target_entity_id || '|' || event_metadata_json::text, ' ;; '), '') FROM audit_events WHERE tenant_id = '{s.OrganizationId:D}' AND event_action = '{action}';").Trim();

    private async Task<JsonElement> MemberAsync(StageBScenario s, TestUser viewer, Guid userId)
    {
        var result = await fixture.Api.GetAsync("/api/v1/organization/members?pageSize=50", s.Token(viewer));
        Assert.True(result.IsSuccess, result.Body);
        return result.Root.GetProperty("members").EnumerateArray().Single(m => m.GetProperty("userId").GetGuid() == userId).Clone();
    }

    private static List<string?> Strings(JsonElement member, string property) => member.GetProperty(property).EnumerateArray().Select(r => r.GetString()).ToList();

    private async Task<List<string>> CapabilitiesAsync(StageBScenario s, TestUser user)
    {
        var dashboard = await fixture.Api.GetAsync("/api/v1/dashboard", s.Token(user));
        Assert.True(dashboard.IsSuccess, dashboard.Body);
        return dashboard.Root.GetProperty("capabilities").EnumerateArray().Select(c => c.GetString()!).ToList();
    }

    private static void Refused(HttpResult result, string code, HttpStatusCode? status = null)
    {
        Assert.False(result.IsSuccess, result.Body);
        Assert.Equal(code, result.ErrorCode);
        if (status is { } expected)
        {
            Assert.Equal(expected, result.Status);
        }
    }

    /// <summary>T01, T05: a Security Administrator appoints an auditor, who gains view_audit and nothing else; explicit evidence; the note stays in the Authority.</summary>
    [Fact]
    public async Task AnAdministratorAppointsAnAuditorWhoGainsViewAuditAndNoRecordsDesignOrConfiguration()
    {
        var s = await ScenarioAsync("aud-appoint");
        var formId = await s.CreateFormAsync("Auditor records " + s.Name, s.Reader);
        var reference = await s.AddFieldAsync(formId, "case_reference", "Case reference", "String");
        await s.SaveDraftAsync(formId, StageBScenario.Layout("AUD", StageBScenario.Section("main", "Main", StageBScenario.Field(reference, "text", "Case reference", 4))));
        await s.PublishAsync(formId, "v1");
        var created = await fixture.Api.PostAsync($"/api/v1/form-definitions/{formId:D}/records", s.Token(s.Reader), new { values = new Dictionary<string, string> { ["case_reference"] = "AUD-REC-1" }, ownerTeamId = (Guid?)null });
        Assert.True(created.Status == HttpStatusCode.Created, created.Body);
        var recordId = created.Root.GetProperty("formRecordId").GetGuid();

        Assert.False(await AllowedAsync(s, s.Clerk, "ViewAudit"));
        var appointed = await AppointAsync(s, s.Administrator, s.Clerk.AuthorityUserId, "InvestigationSupport");
        Assert.Equal(HttpStatusCode.NoContent, appointed.Status);
        Assert.Equal(s.Clerk.AuthorityUserId.ToString("D"), AuditorEdges(s));

        // view_audit, and nothing else (same token, no new sign-in).
        Assert.True(await AllowedAsync(s, s.Clerk, "ViewAudit"));
        foreach (var action in new[] { "AdministerSecurity", "ManageOrganization", "DesignForm" })
        {
            Assert.False(await AllowedAsync(s, s.Clerk, action), action);
        }

        var capabilities = await CapabilitiesAsync(s, s.Clerk);
        Assert.DoesNotContain("security.administer", capabilities);
        Assert.DoesNotContain("forms.design", capabilities);
        Assert.DoesNotContain("organization.manage", capabilities);
        foreach (var path in new[] { $"/api/v1/form-records/{recordId:D}", $"/api/v1/form-records/{recordId:D}/projection", $"/api/v1/form-records/{recordId:D}/versions" })
        {
            var read = await fixture.Api.GetAsync(path, s.Token(s.Clerk));
            Assert.True(read.Status is HttpStatusCode.Forbidden or HttpStatusCode.NotFound, path + ": " + read.Body);
            Assert.DoesNotContain("AUD-REC-1", read.Body, StringComparison.Ordinal);
        }

        Assert.False((await fixture.Api.GetAsync("/api/v1/organization/search-approvals", s.Token(s.Clerk))).IsSuccess);
        Assert.Equal(HttpStatusCode.Forbidden, (await fixture.Api.GetAsync("/api/v1/organization/members", s.Token(s.Clerk))).Status); // the roster is for owners and administrators

        // D8-68: owners and administrators see the role; seeing it grants nothing.
        Assert.Contains("SecurityAuditor", Strings(await MemberAsync(s, s.Administrator, s.Clerk.AuthorityUserId), "authorityRoles"));
        var ownerView = await MemberAsync(s, s.Designer, s.Clerk.AuthorityUserId);
        Assert.Contains("SecurityAuditor", Strings(ownerView, "authorityRoles"));
        Assert.DoesNotContain(Strings(ownerView, "availableActions"), a => a is "AppointSecurityAuditor" or "RemoveSecurityAuditor");
        Assert.False(await AllowedAsync(s, s.Designer, "ViewAudit"));
        var filtered = await fixture.Api.GetAsync("/api/v1/organization/members?pageSize=50&role=SecurityAuditor", s.Token(s.Administrator));
        Assert.True(filtered.IsSuccess, filtered.Body);
        Assert.Equal([s.Clerk.AuthorityUserId], filtered.Root.GetProperty("members").EnumerateArray().Select(m => m.GetProperty("userId").GetGuid()).ToList());

        // Explicit journal evidence with the structured code (no note: D8-72).
        var authorization = Journal(s, "authorization");
        Assert.Contains("\"role_name\":\"security_auditor\"", authorization, StringComparison.Ordinal);
        Assert.Contains("\"reason_code\":\"InvestigationSupport\"", authorization, StringComparison.Ordinal);
        Assert.Contains("\"reason_note\":null", authorization, StringComparison.Ordinal);
        Assert.DoesNotContain("synthetic-development-step-up", authorization, StringComparison.Ordinal);

        // Business projection: actor, target and the code only.
        var business = BusinessAudit(s, "SECURITY_AUDITOR_APPOINTED");
        Assert.StartsWith($"{s.Administrator.AuthorityUserId:D}|{s.Clerk.AuthorityUserId:D}|", business, StringComparison.Ordinal);
        Assert.Contains("\"reasonCode\": \"InvestigationSupport\"", business, StringComparison.Ordinal); // jsonb text form
        Assert.Equal("0", fixture.Stack.AttackerSql($"SELECT count(*) FROM audit_events WHERE event_metadata_json::text LIKE '%NOTE-SENTINEL%' OR event_description LIKE '%NOTE-SENTINEL%';").Trim());
    }

    /// <summary>The business list equals the owner-approved list (C-110) and the Authority accepts every code for its direction.</summary>
    [Fact]
    public async Task EveryApprovedReasonCodeIsListedExactlyAndAcceptedByTheAuthority()
    {
        var s = await ScenarioAsync("aud-codes", withNeverSignedInMember: true);
        var codes = await fixture.Api.GetAsync(Auditors + "/reason-codes", s.Token(s.Administrator));
        Assert.True(codes.IsSuccess, codes.Body);
        List<(string?, string?)> Pairs(string direction) => codes.Root.GetProperty(direction).EnumerateArray().Select(c => (c.GetProperty("reasonCode").GetString(), c.GetProperty("reasonLabel").GetString())).ToList();
        Assert.Equal([("AuditCoverageAssignment", "Assigned to audit coverage"), ("InvestigationSupport", "Supporting an investigation"), ("ReplacementOfDepartingAuditor", "Replacing a departing auditor"), ("ComplianceReview", "Periodic compliance review")], Pairs("appointment"));
        Assert.Equal([("AssignmentEnded", "Assignment ended"), ("RoleChange", "Changed role or responsibilities"), ("MembershipEnding", "Leaving the organization"), ("ConflictOfInterest", "Conflict of interest"), ("SecurityConcern", "Security concern")], Pairs("removal"));

        var appointments = Pairs("appointment").Select(p => p.Item1!).ToList();
        var removals = Pairs("removal").Select(p => p.Item1!).ToList();
        for (var i = 0; i < removals.Count; i++)
        {
            Assert.Equal(HttpStatusCode.NoContent, (await AppointAsync(s, s.Administrator, s.Newcomer.AuthorityUserId, appointments[i % appointments.Count])).Status);
            Assert.Equal(HttpStatusCode.NoContent, (await RemoveAsync(s, s.Administrator, s.Newcomer.AuthorityUserId, removals[i])).Status);
        }

        Assert.Equal(string.Empty, AuditorEdges(s));
    }

    /// <summary>T02, T03, T18, T19 through the business API and directly at the Authority; refusals leave only approved evidence.</summary>
    [Fact]
    public async Task OnlyAnotherSecurityAdministratorMayChangeTheRoleAndRefusalsLeaveOnlyApprovedEvidence()
    {
        var s = await ScenarioAsync("aud-refuse");
        foreach (var actor in new[] { s.Designer, s.DesignerOnly, s.Reader })
        {
            Refused(await AppointAsync(s, actor, s.Clerk.AuthorityUserId), "FORBIDDEN", HttpStatusCode.Forbidden);
            Refused(await AppointAsync(s, actor, actor.AuthorityUserId), "FORBIDDEN", HttpStatusCode.Forbidden);
            Refused(await DirectAsync(s, actor, "GrantSystemRole", AuditorPayload(s.Clerk.AuthorityUserId)), "FORBIDDEN");
        }

        // D8-62a: never oneself, through the API or directly.
        Refused(await AppointAsync(s, s.Administrator, s.Administrator.AuthorityUserId), "SELF_CHANGE_FORBIDDEN", HttpStatusCode.Forbidden);
        Refused(await DirectAsync(s, s.Administrator, "GrantSystemRole", AuditorPayload(s.Administrator.AuthorityUserId)), "SELF_CHANGE_FORBIDDEN");

        // Exact, case-sensitive, direction-specific codes; no mixing of payload shapes.
        const string invalid = "auditCoverageAssignment-INVALID-SENTINEL-9X";
        Refused(await AppointAsync(s, s.Administrator, s.Clerk.AuthorityUserId, invalid), "INVALID_COMMAND");
        Refused(await AppointAsync(s, s.Administrator, s.Clerk.AuthorityUserId, "auditCoverageAssignment"), "INVALID_COMMAND");
        Refused(await AppointAsync(s, s.Administrator, s.Clerk.AuthorityUserId, Remove), "INVALID_COMMAND");
        Refused(await AppointAsync(s, s.Administrator, s.Clerk.AuthorityUserId, "Other"), "INVALID_COMMAND");
        Refused(await DirectAsync(s, s.Administrator, "GrantSystemRole", new { authority_user_id = s.Clerk.AuthorityUserId, role_name = "security_auditor", reason = "free text NOTE-SENTINEL", reason_code = Appoint, step_up_evidence = "synthetic-development-step-up" }), "INVALID_COMMAND");
        Refused(await DirectAsync(s, s.Administrator, "GrantSystemRole", new { authority_user_id = s.Clerk.AuthorityUserId, role_name = "system_administrator", reason = "governance ticket", reason_code = Appoint, step_up_evidence = "synthetic-development-step-up" }), "INVALID_COMMAND");
        Refused(await RemoveAsync(s, s.Administrator, s.Clerk.AuthorityUserId), "INVALID_COMMAND"); // not an auditor
        Assert.Equal(string.Empty, AuditorEdges(s));
        Assert.False(await AllowedAsync(s, s.Clerk, "ViewAudit"));

        // Rejection evidence on the audit stream: approved fields only.
        var rejections = Journal(s, "audit", "SecurityAuditorCommandRejected");
        foreach (var code in new[] { "FORBIDDEN", "SELF_CHANGE_FORBIDDEN", "INVALID_COMMAND", "\"reason_code_valid\":false", "\"reason_code_valid\":true", "\"role_name\":\"security_auditor\"" })
        {
            Assert.Contains(code, rejections, StringComparison.Ordinal);
        }

        // Neither the evidence nor the rejected command rows keep the note, free text, step-up evidence or an invalid value.
        foreach (var text in new[] { rejections, RejectedRoleCommands(s) })
        {
            Assert.NotEqual(string.Empty, text);
            foreach (var secret in new[] { "NOTE-SENTINEL", "INVALID-SENTINEL", "free text", "synthetic-development-step-up", "reason_note", "governance ticket" })
            {
                Assert.DoesNotContain(secret, text, StringComparison.Ordinal);
            }
        }

        Assert.Equal(string.Empty, BusinessAudit(s, "SECURITY_AUDITOR_APPOINTED")); // nothing was appointed
    }

    /// <summary>T06, T14, T02a: removal is immediate for existing tokens; a dual holder cannot remove themself from either role; administrators never remove themselves.</summary>
    [Fact]
    public async Task RemovalIsImmediateAndNobodyRemovesThemselfFromEitherRole()
    {
        var s = await ScenarioAsync("aud-remove");
        var clerkToken = s.Token(s.Clerk);
        Assert.Equal(HttpStatusCode.NoContent, (await AppointAsync(s, s.Administrator, s.Clerk.AuthorityUserId)).Status);
        var dual = await fixture.Api.PostAsync("/api/v1/organization/security-administrators", s.Token(s.Administrator), new { userId = s.Clerk.AuthorityUserId, reason = "dual holder for AC8-T14", useDevelopmentStepUp = true });
        Assert.Equal(HttpStatusCode.NoContent, dual.Status);
        var clerkRow = await MemberAsync(s, s.Administrator, s.Clerk.AuthorityUserId);
        Assert.Contains("SecurityAuditor", Strings(clerkRow, "authorityRoles"));
        Assert.Contains("SecurityAdministrator", Strings(clerkRow, "authorityRoles"));
        Assert.Contains("RemoveSecurityAuditor", Strings(clerkRow, "availableActions"));
        Assert.Empty(Strings(await MemberAsync(s, s.Clerk, s.Clerk.AuthorityUserId), "availableActions")); // never on one's own row

        // The dual holder cannot remove themself from either role (API and direct); an administrator never removes themself (C-70 changed by D8-62a).
        Refused(await RemoveAsync(s, s.Clerk, s.Clerk.AuthorityUserId), "SELF_CHANGE_FORBIDDEN", HttpStatusCode.Forbidden);
        Refused(await DirectAsync(s, s.Clerk, "RevokeSystemRole", AuditorPayload(s.Clerk.AuthorityUserId, Remove)), "SELF_CHANGE_FORBIDDEN");
        Refused(await fixture.Api.PostAsync($"/api/v1/organization/security-administrators/{s.Clerk.AuthorityUserId:D}/remove", clerkToken, new { reason = "stepping down", useDevelopmentStepUp = true }), "SELF_CHANGE_FORBIDDEN", HttpStatusCode.Forbidden);
        Refused(await fixture.Api.PostAsync($"/api/v1/organization/security-administrators/{s.Administrator.AuthorityUserId:D}/remove", s.Token(s.Administrator), new { reason = "stepping down", useDevelopmentStepUp = true }), "SELF_CHANGE_FORBIDDEN", HttpStatusCode.Forbidden);
        Assert.True(await AllowedAsync(s, s.Clerk, "ViewAudit"));

        // Another administrator removes the auditor role: the same token loses view_audit on its next check; the administrator role stays.
        Assert.Equal(HttpStatusCode.NoContent, (await RemoveAsync(s, s.Administrator, s.Clerk.AuthorityUserId, "ConflictOfInterest")).Status);
        Assert.False(await AllowedAsync(s, s.Clerk, "ViewAudit"));
        Assert.True(await AllowedAsync(s, s.Clerk, "AdministerSecurity"));
        Assert.Equal(string.Empty, AuditorEdges(s));
        Assert.DoesNotContain("SecurityAuditor", Strings(await MemberAsync(s, s.Administrator, s.Clerk.AuthorityUserId), "authorityRoles"));
        Assert.Contains("\"reasonCode\": \"ConflictOfInterest\"", BusinessAudit(s, "SECURITY_AUDITOR_REMOVED"), StringComparison.Ordinal); // jsonb text form
        Assert.Equal("0", fixture.Stack.AttackerSql($"SELECT count(*) FROM audit_events WHERE event_metadata_json::text LIKE '%NOTE-SENTINEL%';").Trim());

        // Another administrator performs the administrator removal that self-removal no longer allows.
        Assert.Equal(HttpStatusCode.NoContent, (await fixture.Api.PostAsync($"/api/v1/organization/security-administrators/{s.Clerk.AuthorityUserId:D}/remove", s.Token(s.Administrator), new { reason = "dual holder test done", useDevelopmentStepUp = true })).Status);
        Assert.False(await AllowedAsync(s, s.Clerk, "AdministerSecurity"));
        Assert.Contains("SELF_CHANGE_FORBIDDEN", Journal(s, "audit", "SecurityAdministratorCommandRejected"), StringComparison.Ordinal);
    }

    /// <summary>T15, T17, T04: step-up, stale authorization state, idempotency and another organization's identities.</summary>
    [Fact]
    public async Task StepUpStaleStateIdempotencyAndOtherOrganizationsAreEnforcedAtTheAuthority()
    {
        var s = await ScenarioAsync("aud-guards");
        var other = await ScenarioAsync("aud-guards-other");
        Refused(await AppointAsync(s, s.Administrator, s.Clerk.AuthorityUserId, stepUp: false), "STEP_UP_REQUIRED");
        Refused(await DirectAsync(s, s.Administrator, "GrantSystemRole", new { authority_user_id = s.Clerk.AuthorityUserId, role_name = "security_auditor", reason_code = Appoint, step_up_evidence = "passkey-verified" }), "STEP_UP_REQUIRED");

        var revision = (await fixture.Authority.GateAsync(s.OrganizationId)).GetProperty("authorization_revision").GetInt64();
        Refused(await DirectAsync(s, s.Administrator, "GrantSystemRole", AuditorPayload(s.Clerk.AuthorityUserId), expectedRevision: revision - 1), "STALE_AUTHORIZATION_REVISION");
        var key = "aud-replay-" + Guid.NewGuid().ToString("N");
        Assert.True((await DirectAsync(s, s.Administrator, "GrantSystemRole", AuditorPayload(s.Clerk.AuthorityUserId), idempotencyKey: key)).IsSuccess);
        Refused(await DirectAsync(s, s.Administrator, "GrantSystemRole", AuditorPayload(s.Reader.AuthorityUserId), idempotencyKey: key), "IDEMPOTENCY_CONFLICT");
        await WaitForGateAsync(s); // fail-closed while the committed grant is projected (AUTHORITY_NOT_READY otherwise)

        // Another organization's member is not a binding here; another organization's administrator acts on nothing here.
        Refused(await AppointAsync(s, s.Administrator, other.Clerk.AuthorityUserId), "INVALID_COMMAND");
        var foreign = await fixture.Authority.SendAsync(HttpMethod.Post, "/internal/v1/security-commands", TestIdentity.WorkloadToken(), new
        {
            organization_id = s.OrganizationId,
            command_kind = "GrantSystemRole",
            resource = new { resource_kind = "Organization", resource_id = s.OrganizationId },
            expected_authorization_revision = (await fixture.Authority.GateAsync(s.OrganizationId)).GetProperty("authorization_revision").GetInt64(),
            command_payload = AuditorPayload(s.Reader.AuthorityUserId),
        }, other.Token(other.Administrator), Guid.NewGuid().ToString("N"));
        Assert.False(foreign.IsSuccess, foreign.Body);
        Assert.Equal(s.Clerk.AuthorityUserId.ToString("D"), AuditorEdges(s));
        Assert.Equal(string.Empty, AuditorEdges(other));
        Assert.False(await AllowedAsync(other, other.Clerk, "ViewAudit"));
    }

    /// <summary>T13: disabling an auditor's membership (AC-7) refuses them at once and keeps the role; re-enabling restores it unchanged.</summary>
    [Fact]
    public async Task ADisabledAuditorIsRefusedAndReEnablingRestoresTheRole()
    {
        var s = await ScenarioAsync("aud-disable", withAdditionalOwners: true);
        Assert.Equal(HttpStatusCode.NoContent, (await AppointAsync(s, s.Administrator, s.Reader.AuthorityUserId)).Status);
        Assert.True(await AllowedAsync(s, s.Reader, "ViewAudit"));
        var disabled = await fixture.Api.PostAsync($"/api/v1/organization/members/{s.Reader.AuthorityUserId:D}/disable", s.Token(s.Designer), new { reason = "left the unit", useDevelopmentStepUp = true });
        Assert.True(disabled.IsSuccess, disabled.Body);
        Assert.False(await AllowedAsync(s, s.Reader, "ViewAudit"));
        var row = await MemberAsync(s, s.Administrator, s.Reader.AuthorityUserId);
        Assert.Equal("Disabled", row.GetProperty("membershipState").GetString());
        Assert.Contains("SecurityAuditor", Strings(row, "authorityRoles")); // the edge is kept (R7)
        Assert.Contains("RemoveSecurityAuditor", Strings(row, "availableActions")); // a disabled auditor can still be removed
        Assert.DoesNotContain("AppointSecurityAuditor", Strings(row, "availableActions"));

        Assert.True((await fixture.Api.PostAsync($"/api/v1/organization/members/{s.Reader.AuthorityUserId:D}/enable", s.Token(s.SecondOwner), new { reason = "returned", useDevelopmentStepUp = true })).IsSuccess);
        Assert.True(await AllowedAsync(s, s.Reader, "ViewAudit"));
        Assert.Equal(s.Reader.AuthorityUserId.ToString("D"), AuditorEdges(s));
    }

    /// <summary>T07: a business database administrator's forgery confers no audit access and no role.</summary>
    [Fact]
    public async Task ABusinessDatabaseForgeryGrantsNoAuditAccess()
    {
        var s = await ScenarioAsync("aud-forge");
        fixture.Stack.AttackerSql($$"""
            INSERT INTO audit_events (audit_event_id, tenant_id, event_occurred_on, event_category, event_action, event_outcome, actor_user_id, actor_type, target_entity_type, target_entity_id, correlation_identifier, event_description, event_metadata_json, event_schema_version)
            VALUES ('{{Guid.CreateVersion7():D}}', '{{s.OrganizationId:D}}', now(), 'Security', 'SECURITY_AUDITOR_APPOINTED', 'Success', '{{s.Administrator.AuthorityUserId:D}}', 'User', 'user', '{{s.Reader.AuthorityUserId:D}}', '{{Guid.CreateVersion7():D}}', 'Security Auditor appointed', '{"reasonCode":"AuditCoverageAssignment"}', 1);
            """);
        Assert.False(await AllowedAsync(s, s.Reader, "ViewAudit"));
        Assert.DoesNotContain("SecurityAuditor", Strings(await MemberAsync(s, s.Administrator, s.Reader.AuthorityUserId), "authorityRoles"));
        Assert.Equal(string.Empty, AuditorEdges(s));
        Assert.DoesNotContain("audit.view", await CapabilitiesAsync(s, s.Reader));
    }

    /// <summary>
    /// D8-72 (C-113): a note is refused by the business API before anything is sent, and by the Authority directly (plaintext
    /// or envelope, appointment or removal); nothing of it is persisted, journaled or archived; commands without a note work.
    /// </summary>
    [Fact]
    public async Task ReasonNotesAreRefusedEverywhereAndNothingOfThemIsKept()
    {
        var s = await ScenarioAsync("aud-note-containment");
        Assert.Equal(HttpStatusCode.NoContent, (await AppointAsync(s, s.Administrator, s.Reader.AuthorityUserId)).Status);
        var commandsBefore = fixture.Stack.AuthorityObserverSql($"SELECT count(*) FROM security_commands WHERE organization_id = '{s.OrganizationId:D}';").Trim();

        // Business API: refused before any Authority call (no command row is created).
        Refused(await AppointAsync(s, s.Administrator, s.Clerk.AuthorityUserId, note: Note), "REASON_NOTE_NOT_ACCEPTED", HttpStatusCode.UnprocessableEntity);
        Refused(await RemoveAsync(s, s.Administrator, s.Reader.AuthorityUserId, note: Note), "REASON_NOTE_NOT_ACCEPTED", HttpStatusCode.UnprocessableEntity);
        Assert.Equal(commandsBefore, fixture.Stack.AuthorityObserverSql($"SELECT count(*) FROM security_commands WHERE organization_id = '{s.OrganizationId:D}';").Trim());

        // Authority, directly: plaintext and envelope, both directions.
        var envelope = new { envelope_version = 1, algorithm_name = "AES-256-GCM", key_reference = "NOTE-SENTINEL-key", note_id = Guid.CreateVersion7(), ciphertext = "Tk9URS1TRU5USU5FTC1jaXBoZXI=", nonce = "AAAAAAAAAAAAAAAA", tag = "AAAAAAAAAAAAAAAAAAAAAA==", wrapped_data_key = "Tk9URS1TRU5USU5FTC13cmFw" };
        Refused(await DirectAsync(s, s.Administrator, "GrantSystemRole", AuditorPayload(s.Clerk.AuthorityUserId, note: Note), changeReason: Note), "REASON_NOTE_NOT_ACCEPTED");
        Refused(await DirectAsync(s, s.Administrator, "GrantSystemRole", new { authority_user_id = s.Clerk.AuthorityUserId, role_name = "security_auditor", reason_code = Appoint, reason_note_envelope = envelope, step_up_evidence = "synthetic-development-step-up" }), "REASON_NOTE_NOT_ACCEPTED");
        Refused(await DirectAsync(s, s.Administrator, "RevokeSystemRole", AuditorPayload(s.Reader.AuthorityUserId, Remove, Note)), "REASON_NOTE_NOT_ACCEPTED");
        Refused(await DirectAsync(s, s.Administrator, "RevokeSystemRole", new { authority_user_id = s.Reader.AuthorityUserId, role_name = "security_auditor", reason_code = Remove, reason_note_envelope = envelope, step_up_evidence = "synthetic-development-step-up" }), "REASON_NOTE_NOT_ACCEPTED");
        Assert.Equal(s.Reader.AuthorityUserId.ToString("D"), AuditorEdges(s)); // nothing changed

        // Nothing of the notes anywhere: rejected rows, the audit stream (whose archived copies are these exact, digest-checked
        // bytes), the authorization journal and the business database.
        var rejections = Journal(s, "audit", "SecurityAuditorCommandRejected");
        Assert.Equal(4, rejections.Split(" ;; ").Length);
        Assert.Contains("REASON_NOTE_NOT_ACCEPTED", rejections, StringComparison.Ordinal);
        foreach (var text in new[] { rejections, RejectedRoleCommands(s), Journal(s, "authorization"), Journal(s, "audit") })
        {
            foreach (var secret in new[] { "NOTE-SENTINEL", "Tk9URS1TRU5USU5FTC", "reason_note_envelope", "wrapped_data_key" })
            {
                Assert.DoesNotContain(secret, text, StringComparison.Ordinal);
            }
        }

        Assert.Equal("0", fixture.Stack.AttackerSql($"SELECT count(*) FROM audit_events WHERE event_metadata_json::text LIKE '%NOTE-SENTINEL%' OR event_description LIKE '%NOTE-SENTINEL%';").Trim());

        // The refusal evidence is archived like every audit-stream entry (receipt recorded, digest verified by the archive processor).
        var deadline = DateTimeOffset.UtcNow.AddSeconds(60);
        string pending;
        do
        {
            pending = fixture.Stack.AuthorityObserverSql($"SELECT count(*) FROM security_journal_entries WHERE organization_id = '{s.OrganizationId:D}' AND journal_event_kind = 'SecurityAuditorCommandRejected' AND archive_delivery_state <> 'Anchored';").Trim();
            if (pending == "0")
            {
                break;
            }

            await Task.Delay(500, TestContext.Current.CancellationToken);
        }
        while (DateTimeOffset.UtcNow < deadline);
        Assert.Equal("0", pending);

        // Without a note, both directions still work.
        await WaitForGateAsync(s);
        Assert.Equal(HttpStatusCode.NoContent, (await RemoveAsync(s, s.Administrator, s.Reader.AuthorityUserId)).Status);
        Assert.Equal(HttpStatusCode.NoContent, (await AppointAsync(s, s.Administrator, s.Clerk.AuthorityUserId)).Status);
        Assert.Equal(s.Clerk.AuthorityUserId.ToString("D"), AuditorEdges(s));
    }
}
