using System.Net;
using System.Text.Json;
using EnterpriseForms.SecurityTests.Harness;
using EnterpriseForms.StageBTests.Harness;
using Xunit;

namespace EnterpriseForms.StageBTests.Tests;

/// <summary>
/// C8 M4-4 (AC-8.4, AC-8.5; PG-052) on the real stack â€” Authority, both PostgreSQL databases, SpiceDB, Vault, business API:
/// <list type="bullet">
/// <item>Per-section authorization: gate, commands and coverage for Security Administrators; archive also for Security Auditors
/// (D8-70); a dual holder reads every section; members, organization owners and designers read none; an unknown section is 404.</item>
/// <item>AC-M4-4.2: counts, codes and kinds only â€” sentinels placed in record values, share scopes, transfer reasons and an
/// auditor note never appear, and no record id appears.</item>
/// <item>T12 (C-171, P1): Effective holders are established through normal API operations while the gate is open; the archive is
/// paused and the test waits until the Authority reports the gate closed; genuine grant and revoke commands are then submitted directly
/// to the Authority's production command endpoint (business workload credential, the Security Administrator's own assertion, the
/// business API's payloads). Pending grants confer nothing; the committed removal denies at once; Effective holders keep exactly their
/// sections; the explorer answers AUTHORITY_GATE_CLOSED; after the archive is restored every command becomes Effective and access
/// follows it.</item>
/// <item>T09: the Authority answers another organization's id with the same 404, at the Authority itself.</item>
/// <item>Coverage: one row per active Authority protection decision (checked against AuthorityDb), Missing without an attestation,
/// directory form names and field ids only.</item>
/// </list>
/// Staleness after a real verifier attestation is unit-tested only (SecurityOverviewTests). Authored in M4-4; not executed.
/// </summary>
[Collection(StageBStackCollectionDefinition.Name)]
public sealed class SecurityHomeTests(StageBFixture fixture)
{
    private const string Overview = "/api/v1/security/overview/";
    private const string Auditors = "/api/v1/organization/security-auditors";
    private static readonly string[] Sections = ["gate", "commands", "archive", "coverage"];

    private async Task<StageBScenario> ScenarioAsync(string name, string reasonNote = "routine coverage")
    {
        var s = new StageBScenario(fixture, name);
        await s.BootstrapAsync();
        var appointed = await fixture.Api.PostAsync(Auditors, s.Token(s.Administrator), new { userId = s.Reader.AuthorityUserId, reasonCode = "AuditCoverageAssignment", reasonNote, useDevelopmentStepUp = true });
        Assert.Equal(HttpStatusCode.NoContent, appointed.Status);
        await s.WaitGateReadyAsync();
        return s;
    }

    private Task<HttpResult> SectionAsync(StageBScenario s, TestUser user, string section) => fixture.Api.GetAsync(Overview + section, s.Token(user));

    [Fact]
    public async Task EachSectionIsAuthorizedSeparatelyAndCarriesCountsAndKindsOnly()
    {
        var s = await ScenarioAsync("sh-sections", reasonNote: "SENT-AUDITOR-NOTE");
        await s.BuildAccusedWorkspaceAsync();
        var record = await s.CreateAccusedAsync(s.Clerk, new Dictionary<string, string> { ["given_name"] = "SENT-CONFIDENTIAL-VALUE", ["family_name"] = "Doe" });
        var share = await fixture.Api.PostAsync($"/api/v1/form-records/{record:D}/shares", s.Token(s.Clerk), new { recipientUserId = s.Designer.AuthorityUserId, recipientTeamId = (Guid?)null, recipientRole = "Reader", fieldKeys = new[] { "given_name" } });
        Assert.True(share.IsSuccess, share.Body);
        await s.WaitGateReadyAsync();

        foreach (var section in Sections)
        {
            var administrator = await SectionAsync(s, s.Administrator, section);
            Assert.True(administrator.IsSuccess, section + ": " + administrator.Body);
            Assert.Equal(section, administrator.Root.GetProperty("section").GetString());
            Assert.DoesNotContain("SENT-", administrator.Body, StringComparison.Ordinal);
            Assert.DoesNotContain(record.ToString("D"), administrator.Body, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("given_name", administrator.Body, StringComparison.Ordinal); // field keys are never shown (OD-3)

            var auditor = await SectionAsync(s, s.Reader, section);
            if (section == "archive")
            {
                Assert.True(auditor.IsSuccess, auditor.Body);
                Assert.Equal(3, auditor.Root.GetProperty("archive").GetProperty("streams").GetArrayLength());
            }
            else
            {
                Assert.Equal((HttpStatusCode.Forbidden, "SECURITY_ADMINISTRATION_FORBIDDEN"), (auditor.Status, auditor.ErrorCode));
            }

            foreach (var other in new[] { s.Clerk, s.Designer /* organization owner and designer */, s.DesignerOnly })
            {
                var refused = await SectionAsync(s, other, section);
                Assert.Equal((HttpStatusCode.Forbidden, "SECURITY_ADMINISTRATION_FORBIDDEN"), (refused.Status, refused.ErrorCode));
            }
        }

        // A dual holder (Security Auditor appointed Security Administrator) reads every section.
        Assert.Equal(HttpStatusCode.NoContent, (await fixture.Api.PostAsync("/api/v1/organization/security-administrators", s.Token(s.Administrator), new { userId = s.Reader.AuthorityUserId, reason = "dual holder for the M4-4 matrix", useDevelopmentStepUp = true })).Status);
        await s.WaitGateReadyAsync();
        foreach (var section in Sections)
        {
            Assert.True((await SectionAsync(s, s.Reader, section)).IsSuccess, section);
        }

        Assert.Equal(HttpStatusCode.NotFound, (await SectionAsync(s, s.Administrator, "incidents")).Status); // OD-6: not an overview section
        var commands = (await SectionAsync(s, s.Administrator, "commands")).Root.GetProperty("commands");
        Assert.Equal(["Pending", "CommittedAwaitingProjection", "FailedReconciliation"], commands.GetProperty("stateCounts").EnumerateArray().Select(c => c.GetProperty("commandState").GetString()).ToArray());
        foreach (var item in commands.GetProperty("commands").EnumerateArray())
        {
            Assert.Equal(["ageSeconds", "commandKind", "commandState", "committedAuthorizationRevision", "createdOn"], item.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal).ToArray());
        }
    }

    private const string Administrators = "/api/v1/organization/security-administrators";

    /// <summary>The edge of <paramref name="role"/> for <paramref name="user"/> that is not removed, with its creating command's state.</summary>
    private string ActiveEdgeCommandState(StageBScenario s, TestUser user, string role) => fixture.Stack.AuthorityObserverSql($"""
        SELECT c.command_state_name FROM authority_relationship_edges e JOIN security_commands c ON c.security_command_id = e.created_by_security_command_id
        WHERE e.organization_id = '{s.OrganizationId:D}' AND e.resource_type_name = 'organization' AND e.resource_entity_id = '{s.OrganizationId:D}' AND e.relationship_name = '{role}'
        AND e.subject_type_name = 'user' AND e.subject_entity_id = '{user.AuthorityUserId:D}' AND e.removed_by_security_command_id IS NULL;
        """).Trim();

    private async Task AssertSectionsAsync(StageBScenario s, TestUser user, params string[] allowed)
    {
        foreach (var section in Sections)
        {
            var result = await SectionAsync(s, user, section);
            if (allowed.Contains(section))
            {
                Assert.True(result.IsSuccess, $"{user.Subject} {section}: {result.Body}");
            }
            else
            {
                Assert.Equal((HttpStatusCode.Forbidden, "SECURITY_ADMINISTRATION_FORBIDDEN", section), (result.Status, result.ErrorCode, section));
            }
        }
    }

    private const string SyntheticStepUp = "synthetic-development-step-up";

    /// <summary>
    /// C-171 P1 (test-only, disposable Stage B stack): submits one genuine security command to the Authority's production command
    /// endpoint exactly as the business API does — the business workload credential, the actor's own delegated assertion, the
    /// business API's payload and change reason, an idempotency key, and the organization's current authorization revision as the
    /// expected revision. The Authority authenticates, binds, authorizes, validates and journals it with its production logic. Used
    /// only while the gate is closed, where the business API's own pre-check refuses every command (T12-L1).
    /// </summary>
    private async Task<(HttpResult Result, Guid CommandId)> SubmitToAuthorityAsync(StageBScenario s, TestUser actor, string kind, object payload, string changeReason)
    {
        var gate = await fixture.Authority.GateAsync(s.OrganizationId);
        var result = await fixture.Authority.SendAsync(HttpMethod.Post, "/internal/v1/security-commands", TestIdentity.WorkloadToken(), new
        {
            organization_id = s.OrganizationId,
            command_kind = kind,
            resource = new { resource_kind = "Organization", resource_id = s.OrganizationId },
            expected_authorization_revision = gate.GetProperty("authorization_revision").GetInt64(),
            command_payload = payload,
            change_reason = changeReason,
        }, s.Token(actor), "sh-t12-" + Guid.CreateVersion7().ToString("N"));
        TestContext.Current.TestOutputHelper?.WriteLine($"{kind} to the Authority while the gate is closed: {(int)result.Status} {result.Body}");
        Assert.True(result.IsSuccess, $"the Authority refused {kind} while the gate was closed (stop and report): {result.Body}");
        return (result, result.Root.GetProperty("command_id").GetGuid());
    }

    /// <summary>State and committed revision of one command, read from AuthorityDb.</summary>
    private (string State, string CommittedRevision) CommandRow(StageBScenario s, Guid commandId)
    {
        var row = fixture.Stack.AuthorityObserverSql($"SELECT command_state_name || '|' || coalesce(committed_authorization_revision::text, '') FROM security_commands WHERE organization_id = '{s.OrganizationId:D}' AND security_command_id = '{commandId:D}';").Trim();
        var parts = row.Split('|');
        Assert.True(parts.Length == 2, "command row not found: " + commandId);
        return (parts[0], parts[1]);
    }

    /// <summary>The creating command id of the active edge of <paramref name="role"/> for <paramref name="user"/>, or empty.</summary>
    private string ActiveEdgeCreator(StageBScenario s, TestUser user, string role) => fixture.Stack.AuthorityObserverSql($"""
        SELECT created_by_security_command_id FROM authority_relationship_edges WHERE organization_id = '{s.OrganizationId:D}' AND resource_type_name = 'organization'
        AND resource_entity_id = '{s.OrganizationId:D}' AND relationship_name = '{role}' AND subject_type_name = 'user' AND subject_entity_id = '{user.AuthorityUserId:D}' AND removed_by_security_command_id IS NULL;
        """).Trim();

    /// <summary>
    /// T12 (C-171, P1). (1) Normal API operations establish Effective holders while the gate is open: Administrator (Security
    /// Administrator), Reader and Clerk (Security Auditors). (2) The archive is paused and the test waits until the Authority reports
    /// the gate closed. (3–5) Genuine grants submitted directly to the Authority — DesignerOnly as Security Administrator, Designer
    /// (organization owner) as Security Auditor — commit but are not Effective, and confer no section. (6–8) A genuine removal of
    /// Reader's auditor role commits (the edge is removed) while not Effective, and Reader is denied at once. (9) Administrator and Clerk
    /// keep exactly their sections. (10) The audit explorer answers AUTHORITY_GATE_CLOSED. (11) After the archive is restored, all
    /// three commands become Effective and access follows them.
    /// </summary>
    [Fact]
    public async Task T12PendingGrantsConferNothingACommittedRevokeDeniesAtOnceAndEffectiveHoldersKeepTheirSections()
    {
        // 1 — Effective holders through normal API operations, gate open.
        var s = await ScenarioAsync("sh-t12");
        Assert.Equal(HttpStatusCode.NoContent, (await fixture.Api.PostAsync(Auditors, s.Token(s.Administrator), new { userId = s.Clerk.AuthorityUserId, reasonCode = "AuditCoverageAssignment", reasonNote = "T12 second auditor", useDevelopmentStepUp = true })).Status);
        await s.WaitGateReadyAsync();
        Assert.Equal("Effective", ActiveEdgeCommandState(s, s.Administrator, "system_administrator"));
        Assert.Equal("Effective", ActiveEdgeCommandState(s, s.Reader, "security_auditor"));
        Assert.Equal("Effective", ActiveEdgeCommandState(s, s.Clerk, "security_auditor"));
        await AssertSectionsAsync(s, s.DesignerOnly);
        await AssertSectionsAsync(s, s.Designer);

        Guid adminGrant, auditorGrant, revoke;
        fixture.Stack.Pause("archive-store");
        try
        {
            // 2 — the gate closed, read from the Authority (a condition, not a timing assumption).
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(60);
            string? gateState;
            while ((gateState = (await fixture.Authority.GateAsync(s.OrganizationId)).GetProperty("projection_gate_state").GetString()) == "Ready")
            {
                Assert.True(DateTime.UtcNow < deadline, "the gate did not close within 60 s of pausing the archive");
                await Task.Delay(500, TestContext.Current.CancellationToken);
            }

            TestContext.Current.TestOutputHelper?.WriteLine($"gate closed: {gateState}");

            // 3 — genuine grants, directly to the Authority, with the business API's payloads.
            (_, adminGrant) = await SubmitToAuthorityAsync(s, s.Administrator, "GrantSystemRole",
                new { authority_user_id = s.DesignerOnly.AuthorityUserId, role_name = "system_administrator", reason = "T12 pending administrator grant", step_up_evidence = SyntheticStepUp }, "T12 pending administrator grant");
            (_, auditorGrant) = await SubmitToAuthorityAsync(s, s.Administrator, "GrantSystemRole",
                new { authority_user_id = s.Designer.AuthorityUserId, role_name = "security_auditor", reason_code = "AuditCoverageAssignment", step_up_evidence = SyntheticStepUp, reason_note_envelope = (object?)null }, "AuditCoverageAssignment");

            // 4 — committed (revision assigned, edge written by that command) but not Effective.
            foreach (var (command, user, role) in new[] { (adminGrant, s.DesignerOnly, "system_administrator"), (auditorGrant, s.Designer, "security_auditor") })
            {
                var row = CommandRow(s, command);
                TestContext.Current.TestOutputHelper?.WriteLine($"{role} grant {command:D}: state {row.State}, committed revision {row.CommittedRevision}");
                Assert.NotEqual(string.Empty, row.CommittedRevision);
                Assert.NotEqual("Effective", row.State);
                Assert.NotEqual("Rejected", row.State);
                Assert.Equal(command.ToString("D"), ActiveEdgeCreator(s, user, role));
            }

            // 5 — pending grants confer nothing.
            await AssertSectionsAsync(s, s.DesignerOnly);
            await AssertSectionsAsync(s, s.Designer);

            // 6, 7 — a genuine removal commits: the edge is removed by it, while it is not Effective.
            (_, revoke) = await SubmitToAuthorityAsync(s, s.Administrator, "RevokeSystemRole",
                new { authority_user_id = s.Reader.AuthorityUserId, role_name = "security_auditor", reason_code = "AssignmentEnded", step_up_evidence = SyntheticStepUp, reason_note_envelope = (object?)null }, "AssignmentEnded");
            var revokeRow = CommandRow(s, revoke);
            TestContext.Current.TestOutputHelper?.WriteLine($"auditor removal {revoke:D}: state {revokeRow.State}, committed revision {revokeRow.CommittedRevision}");
            Assert.NotEqual(string.Empty, revokeRow.CommittedRevision);
            Assert.NotEqual("Effective", revokeRow.State);
            Assert.Equal(string.Empty, ActiveEdgeCreator(s, s.Reader, "security_auditor"));
            Assert.Equal(revoke.ToString("D"), fixture.Stack.AuthorityObserverSql($"""
                SELECT removed_by_security_command_id FROM authority_relationship_edges WHERE organization_id = '{s.OrganizationId:D}' AND relationship_name = 'security_auditor'
                AND subject_entity_id = '{s.Reader.AuthorityUserId:D}' AND removed_by_security_command_id IS NOT NULL ORDER BY relationship_removed_on DESC LIMIT 1;
                """).Trim());

            // 8 — the removed auditor is denied at once (a failure here is a product security defect).
            await AssertSectionsAsync(s, s.Reader);

            // 9 — Effective holders keep exactly their sections; the overview shows the closed gate and the three unresolved commands.
            await AssertSectionsAsync(s, s.Administrator, Sections);
            await AssertSectionsAsync(s, s.Clerk, "archive");
            var gate = await SectionAsync(s, s.Administrator, "gate");
            Assert.NotEqual("Ready", gate.Root.GetProperty("gate").GetProperty("gateState").GetString());
            var unresolved = (await SectionAsync(s, s.Administrator, "commands")).Root.GetProperty("commands").GetProperty("commands").EnumerateArray().Select(c => c.GetProperty("commandKind").GetString()).ToList();
            Assert.Equal(2, unresolved.Count(k => k == "GrantSystemRole"));
            Assert.Equal(1, unresolved.Count(k => k == "RevokeSystemRole"));

            // 10 — the audit explorer stays fail-closed (R-17).
            var explorer = await fixture.Api.GetAsync("/api/v1/security/audit-events?pageSize=10", s.Token(s.Clerk));
            Assert.Equal((HttpStatusCode.ServiceUnavailable, "AUTHORITY_GATE_CLOSED"), (explorer.Status, explorer.ErrorCode));
        }
        finally
        {
            fixture.Stack.Unpause("archive-store");
        }

        // 11 — restored: the gate reopens, all three commands finalize, and access follows them.
        await s.WaitGateReadyAsync(TimeSpan.FromSeconds(90));
        var finalDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(60);
        while (new[] { adminGrant, auditorGrant, revoke }.Any(c => CommandRow(s, c).State != "Effective"))
        {
            Assert.True(DateTime.UtcNow < finalDeadline, "the commands did not become Effective after the archive was restored");
            await Task.Delay(500, TestContext.Current.CancellationToken);
        }

        TestContext.Current.TestOutputHelper?.WriteLine("after restore: all three commands Effective");
        await AssertSectionsAsync(s, s.DesignerOnly, Sections);
        await AssertSectionsAsync(s, s.Designer, "archive");
        await AssertSectionsAsync(s, s.Reader);
        await AssertSectionsAsync(s, s.Clerk, "archive");
        await AssertSectionsAsync(s, s.Administrator, Sections);
    }

    /// <summary>
    /// The fields that must be identical for two refusals to be indistinguishable: status, error code, retryable and detail.
    /// <c>correlation_id</c> is unique per request and carries no information about the organization, so it is excluded; any other
    /// property in the body is a contract change and fails the comparison.
    /// </summary>
    private static (HttpStatusCode Status, string? ErrorCode, bool? Retryable, string? Detail, string Properties) Equivalence(HttpResult result)
    {
        var root = result.Root;
        var properties = string.Join(",", root.EnumerateObject().Select(p => p.Name).Where(n => n != "correlation_id").Order(StringComparer.Ordinal));
        return (result.Status,
            root.TryGetProperty("error_code", out var code) ? code.GetString() : null,
            root.TryGetProperty("retryable", out var retryable) && retryable.ValueKind is JsonValueKind.True or JsonValueKind.False ? retryable.GetBoolean() : null,
            root.TryGetProperty("detail", out var detail) && detail.ValueKind == JsonValueKind.String ? detail.GetString() : null,
            properties);
    }

    [Fact]
    public async Task T09AnotherOrganizationsIdIsTheSameNotFoundAtTheAuthority()
    {
        var a = await ScenarioAsync("sh-t09-a");
        var b = await ScenarioAsync("sh-t09-b");
        foreach (var (path, body) in new (string, object)[]
                 {
                     ("/internal/v1/organizations/security-overview?section=gate", new { organization_id = b.OrganizationId, correlation_id = Guid.CreateVersion7() }),
                     ("/internal/v1/organizations/security-overview?section=archive", new { organization_id = b.OrganizationId, correlation_id = Guid.CreateVersion7() }),
                     ("/internal/v1/field-protection/coverage", new { organization_id = b.OrganizationId, correlation_id = Guid.CreateVersion7() }),
                 })
        {
            var foreign = await fixture.Authority.SendAsync(HttpMethod.Post, path, TestIdentity.WorkloadToken(), body, a.Token(a.Administrator));
            var unknown = await fixture.Authority.SendAsync(HttpMethod.Post, path, TestIdentity.WorkloadToken(), new { organization_id = Guid.CreateVersion7(), correlation_id = Guid.CreateVersion7() }, a.Token(a.Administrator));
            Assert.Equal(HttpStatusCode.NotFound, foreign.Status);
            Assert.Equal(Equivalence(unknown), Equivalence(foreign)); // F-M44-V1: correlation_id is request-specific and excluded
        }

        // Through the business API each administrator sees only its own organization's revisions.
        var gateA = (await SectionAsync(a, a.Administrator, "gate")).Root.GetProperty("gate").GetProperty("authorizationRevision").GetInt64();
        var authorityA = (await fixture.Authority.GateAsync(a.OrganizationId)).GetProperty("authorization_revision").GetInt64();
        Assert.Equal(authorityA, gateA);
    }

    [Fact]
    public async Task CoverageHasOneRowPerAuthorityDecisionMissingWithoutAttestationAndNoRecordIds()
    {
        var s = await ScenarioAsync("sh-coverage");
        await s.BuildAccusedWorkspaceAsync();
        var record = await s.CreateAccusedAsync(s.Clerk, new Dictionary<string, string> { ["given_name"] = "SENT-COVERAGE-VALUE", ["family_name"] = "Doe" });
        await s.WaitGateReadyAsync();

        var decisions = int.Parse(fixture.Stack.AuthorityObserverSql($"""
            SELECT count(*) FROM authority_resources WHERE organization_id = '{s.OrganizationId:D}'
            AND resource_type_name IN ('field_protection_requirement', 'field_protection_release') AND resource_lifecycle_state = 'Active';
            """).Trim(), System.Globalization.CultureInfo.InvariantCulture);
        var attestations = int.Parse(fixture.Stack.AuthorityObserverSql($"SELECT count(*) FROM protection_reconciliation_attestations WHERE organization_id = '{s.OrganizationId:D}';").Trim(), System.Globalization.CultureInfo.InvariantCulture);

        var response = await SectionAsync(s, s.Administrator, "coverage");
        Assert.True(response.IsSuccess, response.Body);
        var coverage = response.Root.GetProperty("coverage");
        Assert.Equal(decisions, coverage.GetProperty("decisionCount").GetInt32());
        Assert.Equal(decisions, coverage.GetProperty("decisions").GetArrayLength());
        if (attestations == 0)
        {
            Assert.Equal(decisions, coverage.GetProperty("missingDecisionCount").GetInt32());
            Assert.All(coverage.GetProperty("decisions").EnumerateArray(), d => Assert.Equal("Missing", d.GetProperty("attestationState").GetString()));
        }

        Assert.Contains("organization-wide", coverage.GetProperty("stalenessStatement").GetString(), StringComparison.Ordinal);
        Assert.DoesNotContain(record.ToString("D"), response.Body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("SENT-", response.Body, StringComparison.Ordinal);
        foreach (var decision in coverage.GetProperty("decisions").EnumerateArray())
        {
            Assert.Equal(JsonValueKind.String, decision.GetProperty("fieldDefinitionId").ValueKind);
            Assert.False(decision.TryGetProperty("fieldKey", out _));
        }
    }

    /// <summary>
    /// Coverage states with a genuine Authority attestation (no inserted rows): a plaintext Internal field is strengthened, the
    /// migration completes through the verifier's attestation, and the decision then reads NoLaterOrganizationContentChange. A record
    /// created afterwards in another form of the same organization advances the organization's content-integrity revision, and the
    /// same decision then reads StaleOrganizationWide (OD-1): the attestation is unchanged and is never presented as current.
    /// </summary>
    [Fact]
    public async Task CoverageStatesFollowARealAttestationAndTheOrganizationWideContentRevision()
    {
        var s = await ScenarioAsync("sh-states");
        var formId = await s.CreateFormAsync("Coverage states " + s.Name, s.Clerk);
        var noteId = (await fixture.Api.PostAsync($"/api/v1/form-definitions/{formId:D}/fields", s.Token(s.Designer), new { fieldKey = "plain_note", fieldDisplayName = "Plain note", fieldDescription = string.Empty, fieldDataType = "String", options = (object?)null, fieldClassification = "Internal", fieldProtectionMode = "Standard" })).Root.GetProperty("fieldDefinitionId").GetGuid();
        await s.SaveDraftAsync(formId, StageBScenario.Layout("Coverage states", StageBScenario.Section("main", "Main", StageBScenario.Field(noteId, "text", "Plain note", 4))));
        await s.PublishAsync(formId, "v1 plaintext");
        var created = await fixture.Api.PostAsync($"/api/v1/form-definitions/{formId:D}/records", s.Token(s.Clerk), new { values = new Dictionary<string, string> { ["plain_note"] = "SENT-STATES-VALUE" }, ownerTeamId = (Guid?)null });
        Assert.Equal(HttpStatusCode.Created, created.Status);
        await s.WaitProjectionAsync(s.Clerk, created.Root.GetProperty("formRecordId").GetGuid());

        var strengthened = await fixture.Api.PostAsync($"/api/v1/form-definitions/{formId:D}/fields/{noteId:D}/protection", s.Token(s.Designer), new { targetClassification = "Confidential", targetProtectionMode = "ApplicationEncrypted", changeReason = "M4-4 coverage states", expectedProtectionRevision = 1 });
        Assert.True(strengthened.IsSuccess, strengthened.Body);
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(300);
        while (true)
        {
            var status = await fixture.Api.GetAsync($"/api/v1/form-definitions/{formId:D}/fields/{noteId:D}/protection", s.Token(s.Designer));
            if (status.IsSuccess && status.Root.GetProperty("authorityReconciledComplete").GetBoolean())
            {
                break;
            }

            Assert.True(DateTime.UtcNow < deadline, "the strengthening did not complete through an attestation: " + status.Body);
            await Task.Delay(1000, TestContext.Current.CancellationToken);
        }

        JsonElement DecisionOf(HttpResult coverage) => coverage.Root.GetProperty("coverage").GetProperty("decisions").EnumerateArray().Single(d => d.GetProperty("fieldDefinitionId").GetGuid() == noteId);

        var current = await SectionAsync(s, s.Administrator, "coverage");
        Assert.True(current.IsSuccess, current.Body);
        var before = DecisionOf(current);
        Assert.Equal("NoLaterOrganizationContentChange", before.GetProperty("attestationState").GetString());
        Assert.Equal(formId, before.GetProperty("formDefinitionId").GetGuid());
        Assert.Equal("Coverage states " + s.Name, before.GetProperty("formDisplayName").GetString()); // the session organization's directory name
        var attestationId = before.GetProperty("attestation").GetProperty("attestationId").GetGuid();
        Assert.True(before.GetProperty("attestation").GetProperty("attestationComplete").GetBoolean());
        Assert.DoesNotContain("SENT-", current.Body, StringComparison.Ordinal);

        // Content changes elsewhere in the organization: the comparison is organization-wide, not per form.
        await s.BuildAccusedWorkspaceAsync();
        var other = await s.CreateAccusedAsync(s.Clerk, new Dictionary<string, string> { ["given_name"] = "Later", ["family_name"] = "Change" });
        await s.WaitProjectionAsync(s.Clerk, other);

        var later = await SectionAsync(s, s.Administrator, "coverage");
        Assert.True(later.IsSuccess, later.Body);
        var after = DecisionOf(later);
        Assert.Equal("StaleOrganizationWide", after.GetProperty("attestationState").GetString());
        Assert.Equal(attestationId, after.GetProperty("attestation").GetProperty("attestationId").GetGuid()); // the same recorded attestation
        Assert.True(later.Root.GetProperty("coverage").GetProperty("staleDecisionCount").GetInt32() >= 1);
        Assert.True(later.Root.GetProperty("coverage").GetProperty("organizationContentIntegrityRevision").GetInt64() > after.GetProperty("attestation").GetProperty("attestedContentIntegrityRevision").GetInt64());
    }
}
