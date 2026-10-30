using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using EnterpriseForms.SecurityTests.Harness;
using EnterpriseForms.StageBTests.Harness;
using Xunit;

namespace EnterpriseForms.StageBTests.Tests;

/// <summary>
/// DR-C7-3 on the real stack (Authorization Authority operator plane, AuthorityDb journal, SpiceDB projection, archive,
/// business API, real test identities): the first Security Administrator of an existing organization and recovery when its
/// administrators are unavailable. Execution needs two ES256 approvals from distinct registered approver keys — on this
/// isolated stack the development-only synthetic approvers of compose.security-test.yaml, recorded as synthetic, never as
/// two-human evidence. Nothing about authorization is mocked.
/// </summary>
[Collection(StageBStackCollectionDefinition.Name)]
public sealed class SecurityAdministratorProvisioningTests(StageBFixture fixture)
{
    private const string Approver1 = "test-synthetic-approver-1";
    private const string Approver2 = "test-synthetic-approver-2";
    private const string Justification = "Organization has no Security Administrator (ticket OPS-77)";

    private async Task<StageBScenario> ScenarioAsync(string name, bool withSecurityAdministrator)
    {
        var s = new StageBScenario(fixture, name);
        await s.BootstrapAsync(withSecurityAdministrator);
        return s;
    }

    private static string Path(StageBScenario s, string operation) => $"/internal/v1/operator/organizations/{s.OrganizationId:D}/security-administrators/{operation}";

    private Task<HttpResult> FirstAsync(StageBScenario s, TestUser target, Guid operation, bool dryRun, string[]? approvals = null, Guid? confirm = null, string? subject = null, string justification = Justification) =>
        fixture.Authority.OperatorAsync(HttpMethod.Post, Path(s, "first-appointment"), new
        {
            operation_id = operation,
            confirm_organization_id = confirm ?? s.OrganizationId,
            target_authority_user_id = target.AuthorityUserId,
            target_issuer_uri = TestIdentity.Issuer,
            target_subject_identifier = subject ?? target.Subject,
            justification,
            dry_run = dryRun,
            approvals,
        });

    private Task<HttpResult> RecoverAsync(StageBScenario s, TestUser target, Guid operation, bool dryRun, string? incident, Guid[] removed, string[]? approvals = null) =>
        fixture.Authority.OperatorAsync(HttpMethod.Post, Path(s, "recovery"), new
        {
            operation_id = operation,
            confirm_organization_id = s.OrganizationId,
            target_authority_user_id = target.AuthorityUserId,
            target_issuer_uri = TestIdentity.Issuer,
            target_subject_identifier = target.Subject,
            justification = "Both administrators are unavailable; their passkeys were lost",
            incident_reference = incident,
            removed_administrator_ids = removed,
            dry_run = dryRun,
            approvals,
        });

    private static string[] Approve(HttpResult preview) => [TestIdentity.OperatorApproval(preview.Root, Approver1), TestIdentity.OperatorApproval(preview.Root, Approver2)];

    private async Task<List<string>> CapabilitiesAsync(StageBScenario s, TestUser user)
    {
        var dashboard = await fixture.Api.GetAsync("/api/v1/dashboard", s.Token(user));
        Assert.True(dashboard.IsSuccess, dashboard.Body);
        return dashboard.Root.GetProperty("capabilities").EnumerateArray().Select(c => c.GetString()!).ToList();
    }

    private string Administrators(StageBScenario s) =>
        fixture.Stack.AuthorityObserverSql($"SELECT coalesce(string_agg(subject_entity_id::text, ',' ORDER BY subject_entity_id), '') FROM authority_relationship_edges WHERE organization_id = '{s.OrganizationId:D}' AND resource_type_name = 'organization' AND relationship_name = 'system_administrator' AND removed_by_security_command_id IS NULL;").Trim();

    private string AuditBodies(StageBScenario s, string eventKind) =>
        fixture.Stack.AuthorityObserverSql($"SELECT coalesce(string_agg(journal_event_body_json, ' ;; ' ORDER BY journal_stream_revision), '') FROM security_journal_entries WHERE organization_id = '{s.OrganizationId:D}' AND journal_stream_name = 'audit' AND journal_event_kind = '{eventKind}';").Trim();

    private int Commands(StageBScenario s, string commandKind) =>
        int.Parse(fixture.Stack.AuthorityObserverSql($"SELECT count(*) FROM security_commands WHERE organization_id = '{s.OrganizationId:D}' AND command_kind_name = '{commandKind}';").Trim(), CultureInfo.InvariantCulture);

    private static void AssertRefused(HttpResult result, string code)
    {
        Assert.False(result.IsSuccess, result.Body);
        Assert.Equal(code, result.ErrorCode);
    }

    /// <summary>Tests 1, 9, 13, 18, 19, 20 and the hand-over to normal Security Administrator management.</summary>
    [Fact]
    public async Task TwoIndependentApprovalsAppointTheFirstAdministratorWhoThenManagesTheRestThroughTheNormalUi()
    {
        var s = await ScenarioAsync("prov-first", withSecurityAdministrator: false);
        var formId = await s.CreateFormAsync("Provisioning records " + s.Name, s.Reader);
        var reference = await s.AddFieldAsync(formId, "case_reference", "Case reference", "String");
        await s.SaveDraftAsync(formId, StageBScenario.Layout("P", StageBScenario.Section("main", "Main", StageBScenario.Field(reference, "text", "Case reference", 4))));
        await s.PublishAsync(formId, "v1");
        var created = await fixture.Api.PostAsync($"/api/v1/form-definitions/{formId:D}/records", s.Token(s.Reader), new { values = new Dictionary<string, string> { ["case_reference"] = "PROV-REC-1" }, ownerTeamId = (Guid?)null });
        Assert.True(created.Status == HttpStatusCode.Created, created.Body);
        var recordId = created.Root.GetProperty("formRecordId").GetGuid();

        // Operator view: no usable administrator; dual control available here only as the synthetic development mode.
        var status = await fixture.Authority.OperatorAsync(HttpMethod.Get, $"/internal/v1/operator/organizations/{s.OrganizationId:D}/security-administrators", null);
        Assert.True(status.IsSuccess, status.Body);
        Assert.Equal(0, status.Root.GetProperty("usable_administrator_count").GetInt32());
        Assert.Equal("SyntheticDevelopment", status.Root.GetProperty("approval_mode").GetString());
        Assert.Contains("not two-human", status.Root.GetProperty("approval_assurance").GetString(), StringComparison.Ordinal);
        Assert.Contains(status.Root.GetProperty("members").EnumerateArray(), m => m.GetProperty("authority_user_id").GetGuid() == s.Clerk.AuthorityUserId && m.GetProperty("subject_identifier").GetString() == s.Clerk.Subject);

        // The organization owner still cannot elevate anyone, themself included.
        var self = await fixture.Api.PostAsync("/api/v1/organization/security-administrators", s.Token(s.Designer), new { userId = s.Designer.AuthorityUserId, reason = "no administrator exists", useDevelopmentStepUp = true });
        Assert.Equal(HttpStatusCode.Forbidden, self.Status);
        Assert.Empty((await fixture.Api.GetAsync("/api/v1/organization/security-administrators", s.Token(s.Designer))).Root.GetProperty("administrators").EnumerateArray());

        // Dry run: the exact change, nothing journaled or granted.
        var operation = Guid.CreateVersion7();
        var preview = await FirstAsync(s, s.Clerk, operation, dryRun: true);
        Assert.True(preview.IsSuccess, preview.Body);
        Assert.True(preview.Root.GetProperty("eligible").GetBoolean());
        Assert.Equal(s.Clerk.Subject, preview.Root.GetProperty("target").GetProperty("subject_identifier").GetString());
        Assert.Equal("system_administrator", preview.Root.GetProperty("role_name").GetString());
        Assert.Contains(s.OrganizationId.ToString("D"), preview.Root.GetProperty("approval_statement_json").GetString(), StringComparison.Ordinal);
        Assert.Equal(string.Empty, Administrators(s));
        Assert.DoesNotContain("security.administer", await CapabilitiesAsync(s, s.Clerk));

        var clerkToken = s.Token(s.Clerk); // one token, before and after
        var approvals = Approve(preview);
        var executed = await FirstAsync(s, s.Clerk, operation, dryRun: false, approvals);
        Assert.True(executed.IsSuccess, executed.Body);
        Assert.False(executed.Root.GetProperty("replayed").GetBoolean());
        var commandId = executed.Root.GetProperty("security_command_id").GetGuid();
        await s.WaitGateReadyAsync();
        Assert.Equal(s.Clerk.AuthorityUserId.ToString("D"), Administrators(s));

        // The appointee's existing token has the role at once; the role is security administration only.
        var dashboard = await fixture.Api.GetAsync("/api/v1/dashboard", clerkToken);
        var capabilities = dashboard.Root.GetProperty("capabilities").EnumerateArray().Select(c => c.GetString()).ToList();
        Assert.Contains("security.administer", capabilities);
        Assert.DoesNotContain("forms.design", capabilities);
        Assert.DoesNotContain("organization.manage", capabilities);
        Assert.True((await fixture.Api.GetAsync("/api/v1/organization/search-approvals", clerkToken)).IsSuccess);
        foreach (var path in new[] { $"/api/v1/form-records/{recordId:D}", $"/api/v1/form-records/{recordId:D}/projection" })
        {
            var read = await fixture.Api.GetAsync(path, clerkToken);
            Assert.True(read.Status is HttpStatusCode.Forbidden or HttpStatusCode.NotFound, path + ": " + read.Body);
            Assert.DoesNotContain("PROV-REC-1", read.Body, StringComparison.Ordinal);
        }

        Assert.Equal(HttpStatusCode.Forbidden, (await fixture.Api.PostAsync("/api/v1/form-definitions/designer", clerkToken, new { formDefinitionName = "Not allowed", formDefinitionDescription = "x", formUserUserIds = Array.Empty<Guid>(), formUserTeamIds = Array.Empty<Guid>() })).Status);

        // Hand-over: the new administrator appoints and the normal rules apply (step-up, last-administrator protection).
        Assert.Equal(HttpStatusCode.NoContent, (await fixture.Api.PostAsync("/api/v1/organization/security-administrators", clerkToken, new { userId = s.Reader.AuthorityUserId, reason = "second administrator", useDevelopmentStepUp = true })).Status);
        await s.WaitGateReadyAsync();
        Assert.Equal(2, Administrators(s).Split(',').Length);

        // Evidence: one command, the audit event naming operator, approvers and assurance, and an alert.
        Assert.Equal(1, Commands(s, "ProvisionFirstSecurityAdministrator"));
        var appointed = AuditBodies(s, "SecurityAdministratorFirstAppointed");
        Assert.Contains(Approver1, appointed, StringComparison.Ordinal);
        Assert.Contains(Approver2, appointed, StringComparison.Ordinal);
        Assert.Contains("not two-human", appointed, StringComparison.Ordinal);
        Assert.Contains("stage-a-harness", appointed, StringComparison.Ordinal);
        Assert.Contains("OPS-77", appointed, StringComparison.Ordinal);
        Assert.DoesNotContain(approvals[0], appointed, StringComparison.Ordinal);
        Assert.Contains(commandId.ToString("D"), AuditBodies(s, "SecurityAlertRaised"), StringComparison.Ordinal);
        Assert.Contains("SECURITY ALERT SecurityAdministratorFirstAppointed", fixture.Stack.Logs("authority", 400), StringComparison.Ordinal);

        // Replay: the same operation and statement change nothing; the same id for someone else is a conflict.
        var replay = await FirstAsync(s, s.Clerk, operation, dryRun: false, approvals);
        Assert.True(replay.IsSuccess, replay.Body);
        Assert.True(replay.Root.GetProperty("replayed").GetBoolean());
        Assert.Equal(commandId, replay.Root.GetProperty("security_command_id").GetGuid());
        AssertRefused(await FirstAsync(s, s.DesignerOnly, operation, dryRun: false, approvals), "IDEMPOTENCY_CONFLICT");
        Assert.Equal(1, Commands(s, "ProvisionFirstSecurityAdministrator"));

        // Now that administrators exist, a further first appointment is refused.
        var later = Guid.CreateVersion7();
        var laterPreview = await FirstAsync(s, s.DesignerOnly, later, dryRun: true);
        Assert.False(laterPreview.Root.GetProperty("eligible").GetBoolean());
        Assert.Equal("SECURITY_ADMINISTRATOR_EXISTS", laterPreview.Root.GetProperty("refusal_code").GetString());
        AssertRefused(await FirstAsync(s, s.DesignerOnly, later, dryRun: false, Approve(laterPreview)), "SECURITY_ADMINISTRATOR_EXISTS");
        Assert.Contains("SECURITY_ADMINISTRATOR_EXISTS", AuditBodies(s, "SecurityAdministratorProvisioningRejected"), StringComparison.Ordinal);
    }

    /// <summary>Tests 3–8: only two valid approvals from distinct registered approvers of exactly this statement are accepted.</summary>
    [Fact]
    public async Task ForgedExpiredMisboundOrNonIndependentApprovalsAreRefused()
    {
        var s = await ScenarioAsync("prov-approvals", withSecurityAdministrator: false);
        var other = await ScenarioAsync("prov-approvals-other", withSecurityAdministrator: false);
        var operation = Guid.CreateVersion7();
        var preview = await FirstAsync(s, s.Clerk, operation, dryRun: true);
        var good1 = TestIdentity.OperatorApproval(preview.Root, Approver1);
        using var forgedKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var readerPreview = await FirstAsync(s, s.Reader, operation, dryRun: true);
        var otherPreview = await FirstAsync(other, other.Clerk, operation, dryRun: true);

        var cases = new (string Label, string[]? Approvals, string Code)[]
        {
            ("none", null, "OPERATOR_APPROVAL_INVALID"),
            ("one", [good1], "OPERATOR_APPROVAL_INVALID"),
            ("same approver twice", [good1, TestIdentity.OperatorApproval(preview.Root, Approver1)], "OPERATOR_APPROVALS_NOT_INDEPENDENT"),
            ("forged with approver 2's id", [good1, TestIdentity.OperatorApproval(preview.Root, Approver2, forgedKey)], "OPERATOR_APPROVAL_INVALID"),
            ("unregistered approver", [good1, TestIdentity.OperatorApproval(preview.Root, "unregistered-approver", forgedKey)], "OPERATOR_APPROVAL_INVALID"),
            ("expired", [good1, TestIdentity.OperatorApproval(preview.Root, Approver2, issuedAt: DateTimeOffset.UtcNow.AddMinutes(-20), lifetime: TimeSpan.FromMinutes(5))], "OPERATOR_APPROVAL_INVALID"),
            ("too long-lived", [good1, TestIdentity.OperatorApproval(preview.Root, Approver2, lifetime: TimeSpan.FromHours(2))], "OPERATOR_APPROVAL_INVALID"),
            ("wrong organization", [good1, TestIdentity.OperatorApproval(otherPreview.Root, Approver2)], "OPERATOR_APPROVAL_INVALID"),
            ("wrong member", [good1, TestIdentity.OperatorApproval(readerPreview.Root, Approver2)], "OPERATOR_APPROVAL_INVALID"),
            ("tampered claim", [good1, TestIdentity.OperatorApproval(preview.Root, Approver2, tamper: c => c["operation_kind"] = "Recovery")], "OPERATOR_APPROVAL_INVALID"),
        };
        foreach (var (label, approvals, code) in cases)
        {
            var attempt = await FirstAsync(s, s.Clerk, operation, dryRun: false, approvals);
            Assert.True(attempt.ErrorCode == code, label + ": " + attempt.Body);
        }

        Assert.Equal(string.Empty, Administrators(s));
        Assert.Equal(0, Commands(s, "ProvisionFirstSecurityAdministrator"));
        Assert.Equal(cases.Length, AuditBodies(s, "SecurityAdministratorProvisioningRejected").Split(" ;; ").Length);

        // The same operation then succeeds with valid approvals: refusals do not consume the operation id.
        Assert.True((await FirstAsync(s, s.Clerk, operation, dryRun: false, Approve(preview))).IsSuccess);
    }

    /// <summary>Test 10: concurrent first appointments serialize on the organization; exactly one administrator results.</summary>
    [Fact]
    public async Task ConcurrentFirstAppointmentsProduceExactlyOneAdministrator()
    {
        var s = await ScenarioAsync("prov-race", withSecurityAdministrator: false);
        var a = Guid.CreateVersion7();
        var b = Guid.CreateVersion7();
        var previewA = await FirstAsync(s, s.Clerk, a, dryRun: true);
        var previewB = await FirstAsync(s, s.Reader, b, dryRun: true);
        var results = await Task.WhenAll(FirstAsync(s, s.Clerk, a, dryRun: false, Approve(previewA)), FirstAsync(s, s.Reader, b, dryRun: false, Approve(previewB)));
        Assert.Equal(1, results.Count(r => r.IsSuccess));
        Assert.Equal("SECURITY_ADMINISTRATOR_EXISTS", results.Single(r => !r.IsSuccess).ErrorCode);
        Assert.Single(Administrators(s).Split(','));
    }

    /// <summary>Tests 11, 12, 14 and recovery applicability: non-members, other organizations and every non-operator credential are refused.</summary>
    [Fact]
    public async Task NonMembersOtherOrganizationsAndNonOperatorCredentialsAreRefused()
    {
        var s = await ScenarioAsync("prov-scope", withSecurityAdministrator: false);
        var other = await ScenarioAsync("prov-scope-other", withSecurityAdministrator: true);
        var operation = Guid.CreateVersion7();

        // Another organization's member, a wrong identity, a mismatched confirmation, an unknown organization.
        var foreign = await FirstAsync(s, other.Clerk, operation, dryRun: true);
        Assert.Equal("INVALID_COMMAND", foreign.Root.GetProperty("refusal_code").GetString());
        AssertRefused(await FirstAsync(s, other.Clerk, operation, dryRun: false, Approve(foreign)), "INVALID_COMMAND");
        Assert.Equal("INVALID_COMMAND", (await FirstAsync(s, s.Clerk, Guid.CreateVersion7(), dryRun: true, subject: other.Clerk.Subject)).Root.GetProperty("refusal_code").GetString());
        AssertRefused(await FirstAsync(s, s.Clerk, Guid.CreateVersion7(), dryRun: true, confirm: other.OrganizationId), "INVALID_COMMAND");
        AssertRefused(await FirstAsync(s, s.Clerk, Guid.CreateVersion7(), dryRun: true, justification: "short"), "INVALID_COMMAND");
        var unknown = await fixture.Authority.OperatorAsync(HttpMethod.Post, $"/internal/v1/operator/organizations/{Guid.CreateVersion7():D}/security-administrators/first-appointment", new { operation_id = operation, confirm_organization_id = Guid.Empty, target_authority_user_id = s.Clerk.AuthorityUserId, target_issuer_uri = TestIdentity.Issuer, target_subject_identifier = s.Clerk.Subject, justification = Justification, dry_run = true });
        Assert.False(unknown.IsSuccess, unknown.Body);

        // Recovery is a separate operation and does not apply where no administrator exists.
        Assert.Equal("RECOVERY_NOT_APPLICABLE", (await RecoverAsync(s, s.Clerk, Guid.CreateVersion7(), dryRun: true, "INC-1001", [])).Root.GetProperty("refusal_code").GetString());

        // Only the operator credential reaches the operator plane: not the business workload, not a user token, not nobody.
        foreach (var (label, bearer, assertion) in new (string, string?, string?)[] { ("business workload", TestIdentity.WorkloadToken(), null), ("business workload + owner", TestIdentity.WorkloadToken(), s.Token(s.Designer)), ("user token", s.Token(s.Designer), null), ("none", null, null) })
        {
            foreach (var path in new[] { Path(s, "first-appointment"), Path(s, "recovery") })
            {
                var attempt = await fixture.Authority.SendAsync(HttpMethod.Post, path, bearer, new { operation_id = operation, confirm_organization_id = s.OrganizationId, target_authority_user_id = s.Designer.AuthorityUserId, target_issuer_uri = TestIdentity.Issuer, target_subject_identifier = s.Designer.Subject, justification = Justification, dry_run = false }, assertion);
                Assert.True(attempt.Status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden, label + " " + path + ": " + attempt.Status);
            }
        }

        // The business API has no route to it.
        var business = await fixture.Api.PostAsync("/api/v1/organization/security-administrators/first-appointment", s.Token(s.Designer), new { });
        Assert.True(business.Status is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed, business.Body);
        Assert.Equal(string.Empty, Administrators(s));
    }

    /// <summary>Tests 15, 16, 19: recovery needs an incident, removes only the named administrators, revokes their privileges at once and raises a critical alert.</summary>
    [Fact]
    public async Task RecoveryNeedsAnIncidentRemovesTheNamedAdministratorsAndRaisesACriticalAlert()
    {
        var s = await ScenarioAsync("prov-recovery", withSecurityAdministrator: true);
        var lostToken = s.Token(s.Administrator);
        Assert.Contains("security.administer", await CapabilitiesAsync(s, s.Administrator));
        var operation = Guid.CreateVersion7();

        AssertRefused(await RecoverAsync(s, s.Clerk, operation, dryRun: true, incident: null, [s.Administrator.AuthorityUserId]), "INVALID_COMMAND");
        AssertRefused(await RecoverAsync(s, s.Clerk, operation, dryRun: true, incident: "x", [s.Administrator.AuthorityUserId]), "INVALID_COMMAND");
        Assert.Equal("SECURITY_ADMINISTRATOR_EXISTS", (await FirstAsync(s, s.Clerk, Guid.CreateVersion7(), dryRun: true)).Root.GetProperty("refusal_code").GetString());

        var preview = await RecoverAsync(s, s.Clerk, operation, dryRun: true, "INC-2026-0042", [s.Administrator.AuthorityUserId]);
        Assert.True(preview.Root.GetProperty("eligible").GetBoolean(), preview.Body);
        Assert.Contains("INC-2026-0042", preview.Root.GetProperty("approval_statement_json").GetString(), StringComparison.Ordinal);

        // Approvals for the first-appointment statement cannot authorize a recovery.
        var firstPreview = await FirstAsync(s, s.Clerk, operation, dryRun: true);
        AssertRefused(await RecoverAsync(s, s.Clerk, operation, dryRun: false, "INC-2026-0042", [s.Administrator.AuthorityUserId], Approve(firstPreview)), "OPERATOR_APPROVAL_INVALID");
        Assert.Contains("SecurityAdministratorRecoveryRejected", AuditBodies(s, "SecurityAlertRaised"), StringComparison.Ordinal);

        var executed = await RecoverAsync(s, s.Clerk, operation, dryRun: false, "INC-2026-0042", [s.Administrator.AuthorityUserId], Approve(preview));
        Assert.True(executed.IsSuccess, executed.Body);
        await s.WaitGateReadyAsync();
        Assert.Equal(s.Clerk.AuthorityUserId.ToString("D"), Administrators(s));

        // The removed administrator's existing token loses the role on its next request; the appointee gains it.
        Assert.DoesNotContain("security.administer", (await fixture.Api.GetAsync("/api/v1/dashboard", lostToken)).Root.GetProperty("capabilities").EnumerateArray().Select(c => c.GetString()));
        Assert.Equal(HttpStatusCode.Forbidden, (await fixture.Api.GetAsync("/api/v1/organization/security-administrators", lostToken)).Status);
        Assert.Contains("security.administer", await CapabilitiesAsync(s, s.Clerk));

        var recovered = AuditBodies(s, "SecurityAdministratorRecovered");
        Assert.Contains("INC-2026-0042", recovered, StringComparison.Ordinal);
        Assert.Contains(s.Administrator.AuthorityUserId.ToString("D"), recovered, StringComparison.Ordinal);
        Assert.Contains("\"severity\":\"Critical\"", AuditBodies(s, "SecurityAlertRaised").Replace(" ", string.Empty, StringComparison.Ordinal), StringComparison.Ordinal);
        Assert.Contains("SECURITY ALERT SecurityAdministratorRecovered", fixture.Stack.Logs("authority", 400), StringComparison.Ordinal);
        Assert.Equal(1, Commands(s, "RecoverSecurityAdministrator"));
    }
}
