using System.Globalization;
using System.Net;
using System.Text.Json;
using EnterpriseForms.SecurityTests.Harness;
using EnterpriseForms.StageBTests.Harness;
using Xunit;

namespace EnterpriseForms.StageBTests.Tests;

/// <summary>
/// C8 owner recovery (C-105) on the real stack: Platform Console BFF (5192) → Authority platform plane (5190) → AuthorityDb, SpiceDB, the
/// archive and Vault Transit, and the business API (5180) for the organization's notice and objection. Three console operators approve
/// with their own Vault-signed evidence; execution waits for the trusted-clock waiting period (20 s on this stack) and only adds owner
/// roles; objections block; recovered owners get no team or record access and cannot add each other to teams. Synthetic operators:
/// never human approval evidence. Each test uses its own fixture organization (never a development organization).
/// </summary>
[Collection(StageBStackCollectionDefinition.Name)]
public sealed class OwnerRecoveryStackTests(StageBFixture fixture)
{
    private const string Justification = "Sole owner unreachable; replacements verified by support callback (INC-105).";
    private static readonly string[] ConsoleSubjects = ["sectest-console-operator-dana", "sectest-console-operator-ellis", "sectest-console-operator-frankie"];
    private static readonly TimeSpan WaitingPeriod = TimeSpan.FromSeconds(20);

    private string Sql(string sql) => fixture.Stack.AuthorityObserverSql(sql).Trim();

    private int Count(string sql) => int.Parse(Sql(sql), CultureInfo.InvariantCulture);

    private static string Suffix() => Guid.NewGuid().ToString("N")[..10];

    private static object NewOwner(TestUser user) =>
        new { existingAuthorityUserId = (Guid?)null, issuerUri = TestIdentity.Issuer, subjectIdentifier = user.Subject, displayNameHint = user.DisplayName, verificationMethod = "SupportCallback", verificationReference = "CASE-" + user.Subject[..Math.Min(user.Subject.Length, 20)] };

    private static object ExistingOwner(TestUser user) =>
        new { existingAuthorityUserId = (Guid?)user.AuthorityUserId, issuerUri = (string?)null, subjectIdentifier = (string?)null, displayNameHint = (string?)null, verificationMethod = "SupportCallback", verificationReference = "CASE-existing" };

    private static Task<ConsoleResult> OpenAsync(ConsoleSession session, Guid organizationId, object[] replacements, object[]? declared = null, string? key = null, bool dryRun = false) =>
        session.SendAsync(HttpMethod.Post, "/console-api/v1/owner-recovery", new
        {
            idempotencyKey = key ?? "rec-" + Guid.NewGuid().ToString("N"),
            organizationId,
            incidentReference = "INC-105-" + Suffix(),
            justification = Justification,
            declaredUnavailable = declared ?? [],
            replacements,
            dryRun,
        });

    private static async Task<JsonElement> DetailAsync(ConsoleSession session, Guid caseId)
    {
        var detail = await session.SendAsync(HttpMethod.Get, $"/console-api/v1/owner-recovery/{caseId:D}");
        Assert.True(detail.Status == HttpStatusCode.OK, detail.Text);
        return detail.Body;
    }

    private static async Task<ConsoleResult> DecideAsync(ConsoleSession session, Guid caseId, string action)
    {
        for (var attempt = 1; ; attempt++)
        {
            var sha = (await DetailAsync(session, caseId)).GetProperty("statementSha256").GetString();
            var result = await session.SendAsync(HttpMethod.Post, $"/console-api/v1/owner-recovery/{caseId:D}{action}", new { expectedStatementSha256 = sha, justification = Justification });
            // Grant-direction platform decisions wait for every earlier platform event to be anchored (fail closed, retryable).
            if (result.Code != "PLATFORM_JOURNAL_ARCHIVE_PENDING" || attempt == 10)
            {
                return result;
            }

            await Task.Delay(300);
        }
    }

    private async Task<(Guid CaseId, ConsoleSession[] Operators)> ApprovedCaseAsync(StageBScenario s, params object[] replacements)
    {
        await PlatformConsoleHarness.EnsureConsoleOperatorsAsync(fixture);
        var operators = new[] { await ConsoleSession.SignInAsync(ConsoleSubjects[0]), await ConsoleSession.SignInAsync(ConsoleSubjects[1]), await ConsoleSession.SignInAsync(ConsoleSubjects[2]) };
        var opened = await OpenAsync(operators[0], s.OrganizationId, replacements);
        Assert.True(opened.Status == HttpStatusCode.OK, opened.Text);
        var caseId = opened.Body.GetProperty("case").GetProperty("caseId").GetGuid();
        Assert.Equal("ApprovalRecorded", (await DecideAsync(operators[0], caseId, "/approvals")).Body.GetProperty("outcome").GetString());
        Assert.Equal("ApprovalRecorded", (await DecideAsync(operators[1], caseId, "/approvals")).Body.GetProperty("outcome").GetString());
        var third = await DecideAsync(operators[2], caseId, "/approvals");
        Assert.True(third.Status == HttpStatusCode.OK, third.Text);
        Assert.Equal("ApprovalsComplete", third.Body.GetProperty("outcome").GetString());
        return (caseId, operators);
    }

    private static async Task<JsonElement> WaitDisplayStateAsync(ConsoleSession session, Guid caseId, string wanted, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        JsonElement detail;
        do
        {
            detail = await DetailAsync(session, caseId);
            if (detail.GetProperty("displayState").GetString() == wanted)
            {
                return detail;
            }

            await Task.Delay(500);
        }
        while (DateTime.UtcNow < deadline);
        throw new TimeoutException($"case did not reach {wanted}: " + detail.GetRawText());
    }

    [Fact]
    public async Task ThreeOperatorsAndTheWaitingPeriodRestoreOwnersWithoutTouchingRecordsTeamsOrExistingOwners()
    {
        var s = new StageBScenario(fixture, "rec-" + Suffix());
        await s.BootstrapAsync(); // Designer is the only owner; Administrator is the Security Administrator
        await s.BuildAccusedWorkspaceAsync();
        var record = await s.CreateAccusedAsync(s.Clerk);
        var recovered = TestUser.New("RecoveredOwner");
        var edgesBefore = Sql($"SELECT string_agg(authority_relationship_edge_id::text, ',' ORDER BY authority_relationship_edge_id) FROM authority_relationship_edges WHERE organization_id = '{s.OrganizationId:D}' AND relationship_removed_on IS NULL;");
        var revision = Count($"SELECT authorization_revision FROM organization_authorization_states WHERE organization_id = '{s.OrganizationId:D}';");

        var (caseId, operators) = await ApprovedCaseAsync(s, NewOwner(recovered), ExistingOwner(s.Reader));
        try
        {
            var waiting = await DetailAsync(operators[0], caseId);
            Assert.Equal("WaitingPeriod", waiting.GetProperty("displayState").GetString());
            Assert.True(waiting.GetProperty("waitingPeriodRemainingSeconds").GetInt64() > 0);
            Assert.Equal("OWNER_RECOVERY_WAITING_PERIOD", (await DecideAsync(operators[1], caseId, "/execute")).Code);
            Assert.Equal(revision, Count($"SELECT authorization_revision FROM organization_authorization_states WHERE organization_id = '{s.OrganizationId:D}';"));

            await WaitDisplayStateAsync(operators[1], caseId, "ReadyForRecovery", WaitingPeriod + TimeSpan.FromSeconds(30));
            var executed = await DecideAsync(operators[1], caseId, "/execute");
            Assert.True(executed.Status == HttpStatusCode.OK, executed.Text);
            Assert.Equal("Recovered", executed.Body.GetProperty("outcome").GetString());
            await WaitDisplayStateAsync(operators[1], caseId, "Recovered", TimeSpan.FromSeconds(60));

            // Add-only: every earlier edge survives; the only new edges are the two owner roles.
            var stillActive = Count($"SELECT count(*) FROM authority_relationship_edges WHERE organization_id = '{s.OrganizationId:D}' AND relationship_removed_on IS NULL AND authority_relationship_edge_id::text = ANY(string_to_array('{edgesBefore}', ','));");
            Assert.Equal(edgesBefore.Split(',').Length, stillActive);
            Assert.Equal("organization_owner|2", Sql($"SELECT relationship_name || '|' || count(*) FROM authority_relationship_edges WHERE organization_id = '{s.OrganizationId:D}' AND relationship_removed_on IS NULL AND authority_relationship_edge_id::text <> ALL(string_to_array('{edgesBefore}', ',')) GROUP BY relationship_name;"));
            Assert.Equal(revision + 1, Count($"SELECT authorization_revision FROM organization_authorization_states WHERE organization_id = '{s.OrganizationId:D}';"));
            Assert.Equal(1, Count($"SELECT count(*) FROM identity_bindings WHERE organization_id = '{s.OrganizationId:D}' AND organization_owner_recovery_case_id = '{caseId:D}';"));
            var recoveredId = Guid.Parse(Sql($"SELECT authority_user_id FROM identity_bindings WHERE organization_id = '{s.OrganizationId:D}' AND organization_owner_recovery_case_id = '{caseId:D}';"));
            Assert.Equal(2, Count($"SELECT count(*) FROM organization_recovered_owner_probations WHERE organization_owner_recovery_case_id = '{caseId:D}';"));
            Assert.Equal(1, Count($"SELECT count(*) FROM security_commands WHERE organization_id = '{s.OrganizationId:D}' AND command_kind_name = 'RecoverOrganizationOwners';"));
            Assert.Equal(0, Count($"SELECT count(*) FROM authority_relationship_edges WHERE organization_id = '{s.OrganizationId:D}' AND relationship_removed_on IS NULL AND resource_type_name <> 'organization' AND subject_entity_id IN ('{recoveredId:D}', '{s.Reader.AuthorityUserId:D}');"));
            foreach (var kind in new[] { "OwnerRecoveryOpened", "OwnerRecoveryApprovalsComplete", "OwnerRecoveryExecuted" })
            {
                Assert.Equal(1, Count($"SELECT count(*) FROM platform_journal_entries WHERE journal_event_kind = '{kind}' AND journal_event_body_json LIKE '%{caseId:D}%';"));
            }

            Assert.Equal(1, Count($"SELECT count(*) FROM security_journal_entries WHERE organization_id = '{s.OrganizationId:D}' AND journal_event_kind = 'OwnerRecoveryExecuted';"));

            // The selected member's immutable user id is what the case recorded, and the People directory shows the Authority's own expiry.
            Assert.Equal(1, Count($"SELECT count(*) FROM organization_owner_recovery_subjects WHERE organization_owner_recovery_case_id = '{caseId:D}' AND recovery_subject_role_name = 'Replacement' AND is_new_identity = false AND subject_authority_user_id = '{s.Reader.AuthorityUserId:D}';"));
            var probationEnds = Sql($"SELECT to_char(probation_ends_on AT TIME ZONE 'UTC', 'YYYY-MM-DD\"T\"HH24:MI:SS.US') FROM organization_recovered_owner_probations WHERE organization_owner_recovery_case_id = '{caseId:D}' AND authority_user_id = '{s.Reader.AuthorityUserId:D}';");
            await s.WaitGateReadyAsync(TimeSpan.FromSeconds(40));
            var people = await fixture.Api.GetAsync("/api/v1/organization/members", s.Token(s.Designer));
            Assert.True(people.IsSuccess, people.Body);
            var readerRow = people.Root.GetProperty("members").EnumerateArray().Single(m => m.GetProperty("userId").GetGuid() == s.Reader.AuthorityUserId);
            Assert.Equal(DateTimeOffset.Parse(probationEnds + "Z", CultureInfo.InvariantCulture), readerRow.GetProperty("recoveryRestrictionsUntil").GetDateTimeOffset()); // the Authority timestamp, not a computed one
            var designerRow = people.Root.GetProperty("members").EnumerateArray().Single(m => m.GetProperty("userId").GetGuid() == s.Designer.AuthorityUserId);
            Assert.True(!designerRow.TryGetProperty("recoveryRestrictionsUntil", out var none) || none.ValueKind == JsonValueKind.Null); // the original owner has none
            Assert.Equal(2, people.Root.GetProperty("members").EnumerateArray().Count(m => m.TryGetProperty("recoveryRestrictionsUntil", out var v) && v.ValueKind != JsonValueKind.Null));

            // The recovered owner is an owner (organization administration) but has no record access through the recovery.
            var newOwner = recovered with { AuthorityUserId = recoveredId };
            var signedIn = await fixture.Api.GetAsync("/api/v1/users/me", TestIdentity.UserToken(newOwner, s.OrganizationId)); // first sign-in, as the SPA does
            Assert.True(signedIn.IsSuccess, signedIn.Body);
            var overview = await fixture.Api.GetAsync("/api/v1/organization/overview", TestIdentity.UserToken(newOwner, s.OrganizationId));
            Assert.True(overview.IsSuccess, overview.Body);
            Assert.False((await s.ProjectionAsync(newOwner, record)).IsSuccess);
            Assert.False((await s.ProjectionAsync(s.Reader, record)).IsSuccess);
            Assert.True((await s.ProjectionAsync(s.Clerk, record)).IsSuccess); // record ownership unchanged

            // OB-F8 and the recovered-owner safeguard: no self-join, and the two recovered owners cannot add each other to a team.
            var team = await fixture.Api.PostAsync("/api/v1/teams", s.Token(s.Designer), new { teamName = "Recovery team " + s.Name });
            Assert.Equal(HttpStatusCode.Created, team.Status);
            var teamId = team.Root.GetProperty("teamId").GetGuid();
            await s.WaitGateReadyAsync(TimeSpan.FromSeconds(40));
            var self = await fixture.Api.PostAsync($"/api/v1/teams/{teamId:D}/members", TestIdentity.UserToken(newOwner, s.OrganizationId), new { userId = recoveredId });
            Assert.True(self.Status == HttpStatusCode.Forbidden, self.Body);
            var eachOther = await fixture.Api.PostAsync($"/api/v1/teams/{teamId:D}/members", TestIdentity.UserToken(newOwner, s.OrganizationId), new { userId = s.Reader.AuthorityUserId });
            Assert.Equal(HttpStatusCode.Forbidden, eachOther.Status);
            Assert.Contains("OWNER_RECOVERY_PROBATION", eachOther.Body, StringComparison.Ordinal);
            Assert.Equal(0, Count($"SELECT count(*) FROM authority_relationship_edges WHERE organization_id = '{s.OrganizationId:D}' AND resource_type_name = 'team' AND relationship_removed_on IS NULL AND subject_entity_id IN ('{recoveredId:D}', '{s.Reader.AuthorityUserId:D}');"));

            // A retried execution replays; nothing changes.
            var replay = await DecideAsync(operators[2], caseId, "/execute");
            Assert.True(replay.Status == HttpStatusCode.OK, replay.Text);
            Assert.True(replay.Body.GetProperty("replayed").GetBoolean());
            Assert.Equal(1, Count($"SELECT count(*) FROM security_commands WHERE organization_id = '{s.OrganizationId:D}' AND command_kind_name = 'RecoverOrganizationOwners';"));
        }
        finally
        {
            foreach (var session in operators)
            {
                session.Dispose();
            }
        }
    }

    [Fact]
    public async Task TheMemberPickerOffersOnlyEligibleMembersOfTheSelectedOrganizationAndIsAudited()
    {
        await PlatformConsoleHarness.EnsureConsoleOperatorsAsync(fixture);
        using var dana = await ConsoleSession.SignInAsync(ConsoleSubjects[0]);
        var s = new StageBScenario(fixture, "rec-pick-" + Suffix());
        await s.BootstrapAsync(withNeverSignedInMember: true);
        var other = new StageBScenario(fixture, "rec-pick-other-" + Suffix());
        await other.BootstrapAsync();
        string Members(Guid organizationId, string query = "") => $"/console-api/v1/owner-recovery/organizations/{organizationId:D}/members{query}";

        var listed = await dana.SendAsync(HttpMethod.Get, Members(s.OrganizationId));
        Assert.True(listed.Status == HttpStatusCode.OK, listed.Text);
        var ids = listed.Body.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("authorityUserId").GetGuid()).ToList();
        // Members only (never the owner), from this organization only, each exactly once.
        var expected = new[] { s.Clerk, s.Reader, s.Administrator, s.DesignerOnly, s.Newcomer }.Select(u => u.AuthorityUserId).Order().ToArray();
        Assert.Equal(expected, ids.Order().ToArray());
        Assert.DoesNotContain(s.Designer.AuthorityUserId, ids); // existing owner
        Assert.DoesNotContain(other.Designer.AuthorityUserId, ids); // another organization
        Assert.Equal(expected.Length, listed.Body.GetProperty("totalCount").GetInt32());
        // Minimum metadata: exactly these members, no roles, teams, records or documents.
        var names = listed.Body.GetProperty("items")[0].EnumerateObject().Select(p => p.Name).Order().ToArray();
        Assert.Equal(["authorityUserId", "displayName", "issuerUri", "memberSince", "subjectIdentifier"], names);

        // Server-side search and cursor paging (opaque, operator bound).
        var clerk = await dana.SendAsync(HttpMethod.Get, Members(s.OrganizationId, "?q=" + Uri.EscapeDataString(s.Clerk.Subject)));
        Assert.Equal(s.Clerk.AuthorityUserId, Assert.Single(clerk.Body.GetProperty("items").EnumerateArray()).GetProperty("authorityUserId").GetGuid());
        var first = await dana.SendAsync(HttpMethod.Get, Members(s.OrganizationId, "?limit=2"));
        var cursor = first.Body.GetProperty("nextCursor").GetString()!;
        var second = await dana.SendAsync(HttpMethod.Get, Members(s.OrganizationId, "?limit=2&cursor=" + Uri.EscapeDataString(cursor)));
        Assert.Equal(2, second.Body.GetProperty("items").GetArrayLength());
        Assert.Empty(first.Body.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("authorityUserId").GetGuid()).Intersect(second.Body.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("authorityUserId").GetGuid())));
        using var ellis = await ConsoleSession.SignInAsync(ConsoleSubjects[1]);
        Assert.Equal("CURSOR_INVALID", (await ellis.SendAsync(HttpMethod.Get, Members(s.OrganizationId, "?limit=2&cursor=" + Uri.EscapeDataString(cursor)))).Code);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await dana.SendAsync(HttpMethod.Get, Members(s.OrganizationId, "?limit=500"))).Status);
        Assert.Equal("NOT_FOUND", (await dana.SendAsync(HttpMethod.Get, Members(Guid.CreateVersion7()))).Code);

        // Every successful search is audited on the organization stream.
        Assert.True(Count($"SELECT count(*) FROM security_journal_entries WHERE organization_id = '{s.OrganizationId:D}' AND journal_event_kind = 'PlatformOwnerRecoveryMembersViewed';") == 4); // one per successful search; refusals disclose nothing and write nothing
        Assert.Equal(0, Count($"SELECT count(*) FROM security_journal_entries WHERE organization_id = '{other.OrganizationId:D}' AND journal_event_kind = 'PlatformOwnerRecoveryMembersViewed';"));

        // Unauthorized enumeration: no session, a business token, and a business owner of the same organization all see nothing.
        using var anonymous = new ConsoleSession(); // no cookie
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.SendAsync(HttpMethod.Get, Members(s.OrganizationId))).Status);
        var businessRoute = await fixture.Api.GetAsync($"/api/v1/organization/owner-recovery/organizations/{s.OrganizationId:D}/members", s.Token(s.Designer));
        Assert.False(businessRoute.IsSuccess); // there is no such business route
    }

    [Fact]
    public async Task OwnersAndSecurityAdministratorsSeeTheNoticeAndAnObjectionBlocksExecution()
    {
        var s = new StageBScenario(fixture, "rec-obj-" + Suffix());
        await s.BootstrapAsync();
        var other = new StageBScenario(fixture, "rec-obj-other-" + Suffix());
        await other.BootstrapAsync();
        var (caseId, operators) = await ApprovedCaseAsync(s, NewOwner(TestUser.New("ProposedOwnerA")), NewOwner(TestUser.New("ProposedOwnerB")));
        try
        {
            var notice = await fixture.Api.GetAsync("/api/v1/organization/owner-recovery", s.Token(s.Designer));
            Assert.True(notice.IsSuccess, notice.Body);
            Assert.Equal(caseId, notice.Root.GetProperty("case").GetProperty("caseId").GetGuid());
            Assert.True(notice.Root.GetProperty("canObject").GetBoolean());
            Assert.Equal(2, notice.Root.GetProperty("case").GetProperty("subjects").GetArrayLength());
            Assert.Equal(caseId, (await fixture.Api.GetAsync("/api/v1/organization/owner-recovery", s.Token(s.Administrator))).Root.GetProperty("case").GetProperty("caseId").GetGuid());

            // Unauthorized members and other organizations see nothing and cannot object.
            Assert.Equal(JsonValueKind.Null, (await fixture.Api.GetAsync("/api/v1/organization/owner-recovery", s.Token(s.Reader))).Root.GetProperty("case").ValueKind);
            Assert.Equal(JsonValueKind.Null, (await fixture.Api.GetAsync("/api/v1/organization/owner-recovery", other.Token(other.Designer))).Root.GetProperty("case").ValueKind);
            var objection = new { reason = "I am the owner and I am available; this is not authorized.", useDevelopmentStepUp = true };
            Assert.Equal(HttpStatusCode.NotFound, (await fixture.Api.PostAsync($"/api/v1/organization/owner-recovery/{caseId:D}/objections", s.Token(s.Reader), objection)).Status);
            Assert.Equal(HttpStatusCode.NotFound, (await fixture.Api.PostAsync($"/api/v1/organization/owner-recovery/{caseId:D}/objections", other.Token(other.Designer), objection)).Status);
            var noStepUp = await fixture.Api.PostAsync($"/api/v1/organization/owner-recovery/{caseId:D}/objections", s.Token(s.Designer), objection with { useDevelopmentStepUp = false });
            Assert.False(noStepUp.IsSuccess, noStepUp.Body);
            Assert.Equal("Open", Sql($"SELECT recovery_state_name FROM organization_owner_recovery_cases WHERE organization_owner_recovery_case_id = '{caseId:D}';"));

            // The owner objects; confirmation comes only after the Authority recorded it.
            var objected = await fixture.Api.PostAsync($"/api/v1/organization/owner-recovery/{caseId:D}/objections", s.Token(s.Designer), objection);
            Assert.True(objected.IsSuccess, objected.Body);
            Assert.Equal("Objected", objected.Root.GetProperty("state").GetString());
            Assert.Equal("Objected", Sql($"SELECT recovery_state_name FROM organization_owner_recovery_cases WHERE organization_owner_recovery_case_id = '{caseId:D}';"));
            Assert.Equal(1, Count($"SELECT count(*) FROM platform_journal_entries WHERE journal_event_kind = 'OwnerRecoveryObjected' AND journal_event_body_json LIKE '%{caseId:D}%';"));
            Assert.Equal(1, Count($"SELECT count(*) FROM security_journal_entries WHERE organization_id = '{s.OrganizationId:D}' AND journal_event_kind = 'OwnerRecoveryObjected';"));

            await Task.Delay(WaitingPeriod + TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
            Assert.Equal("OWNER_RECOVERY_OBJECTED", (await DecideAsync(operators[1], caseId, "/execute")).Code);
            Assert.Equal("BlockedByObjection", (await DetailAsync(operators[0], caseId)).GetProperty("displayState").GetString());
            Assert.Equal(1, Count($"SELECT count(*) FROM authority_relationship_edges WHERE organization_id = '{s.OrganizationId:D}' AND relationship_name = 'organization_owner' AND relationship_removed_on IS NULL;"));
            Assert.Equal(JsonValueKind.Null, (await fixture.Api.GetAsync("/api/v1/organization/owner-recovery", s.Token(s.Designer))).Root.GetProperty("case").ValueKind);
        }
        finally
        {
            foreach (var session in operators)
            {
                session.Dispose();
            }
        }
    }

    [Fact]
    public async Task EligibilityEvidenceAndConcurrencyAreEnforcedThroughTheConsole()
    {
        await PlatformConsoleHarness.EnsureConsoleOperatorsAsync(fixture);
        using var dana = await ConsoleSession.SignInAsync(ConsoleSubjects[0]);
        using var ellis = await ConsoleSession.SignInAsync(ConsoleSubjects[1]);

        // Two eligible owners: recovery does not apply.
        var healthy = new StageBScenario(fixture, "rec-healthy-" + Suffix());
        await healthy.BootstrapAsync(withAdditionalOwners: true);
        Assert.Equal("OWNER_RECOVERY_NOT_APPLICABLE", (await OpenAsync(dana, healthy.OrganizationId, [NewOwner(TestUser.New("Extra"))])).Code);
        // Declaring owners unavailable needs documented evidence, never an assertion alone.
        var noEvidence = new object[] { new { authorityUserId = healthy.SecondOwner.AuthorityUserId, reason = "LeftOrganization", evidenceReference = "", evidenceSummary = "" } };
        Assert.Equal("OWNER_RECOVERY_EVIDENCE_REQUIRED", (await OpenAsync(dana, healthy.OrganizationId, [NewOwner(TestUser.New("Extra"))], noEvidence)).Code);
        Assert.Equal("NOT_FOUND", (await OpenAsync(dana, Guid.CreateVersion7(), [NewOwner(TestUser.New("Ghost"))])).Code);
        // The browser cannot smuggle extra members (e.g. an operator or a ready-made approval).
        var smuggled = await dana.SendAsync(HttpMethod.Post, "/console-api/v1/owner-recovery", new { idempotencyKey = "rec-smuggle-" + Suffix(), organizationId = healthy.OrganizationId, incidentReference = "INC-1", justification = Justification, replacements = Array.Empty<object>(), approvedBy = ConsoleSubjects[1] });
        Assert.Equal(HttpStatusCode.BadRequest, smuggled.Status);
        Assert.Equal(0, Count($"SELECT count(*) FROM organization_owner_recovery_cases WHERE organization_id = '{healthy.OrganizationId:D}';"));

        // Concurrent openings for the same organization: exactly one case.
        var s = new StageBScenario(fixture, "rec-conc-" + Suffix());
        await s.BootstrapAsync();
        var openings = await Task.WhenAll(OpenAsync(dana, s.OrganizationId, [NewOwner(TestUser.New("ConcA")), NewOwner(TestUser.New("ConcB"))]), OpenAsync(ellis, s.OrganizationId, [NewOwner(TestUser.New("ConcC")), NewOwner(TestUser.New("ConcD"))]));
        Assert.Single(openings, o => o.Status == HttpStatusCode.OK);
        Assert.Single(openings, o => o.Code == "OWNER_RECOVERY_CASE_EXISTS");
        Assert.Equal(1, Count($"SELECT count(*) FROM organization_owner_recovery_cases WHERE organization_id = '{s.OrganizationId:D}';"));

        // The same operator cannot give two of the three approvals, also not from a second session.
        var caseId = openings.Single(o => o.Status == HttpStatusCode.OK).Body.GetProperty("case").GetProperty("caseId").GetGuid();
        Assert.Equal("ApprovalRecorded", (await DecideAsync(dana, caseId, "/approvals")).Body.GetProperty("outcome").GetString());
        using var danaAgain = await ConsoleSession.SignInAsync(ConsoleSubjects[0]);
        Assert.Equal("OPERATOR_ALREADY_APPROVED", (await DecideAsync(danaAgain, caseId, "/approvals")).Code);
        Assert.Equal("OWNER_RECOVERY_APPROVALS_INCOMPLETE", (await DecideAsync(ellis, caseId, "/execute")).Code);
        Assert.Equal("Cancelled", (await DecideAsync(ellis, caseId, "/cancel")).Body.GetProperty("outcome").GetString());
        Assert.Equal("OWNER_RECOVERY_NOT_OPEN", (await DecideAsync(dana, caseId, "/approvals")).Code);
    }

    [Fact]
    public async Task AnArchiveOutageFailsClosedAndConcurrentExecutionsProduceOneCommand()
    {
        await PlatformConsoleHarness.EnsureConsoleOperatorsAsync(fixture);
        var s = new StageBScenario(fixture, "rec-outage-" + Suffix());
        await s.BootstrapAsync();
        using (var dana = await ConsoleSession.SignInAsync(ConsoleSubjects[0]))
        {
            fixture.Stack.Pause("archive-store");
            try
            {
                var refused = await OpenAsync(dana, s.OrganizationId, [NewOwner(TestUser.New("OutA")), NewOwner(TestUser.New("OutB"))]);
                Assert.Equal(HttpStatusCode.ServiceUnavailable, refused.Status);
                Assert.Equal("PLATFORM_JOURNAL_UNVERIFIABLE", refused.Code);
            }
            finally
            {
                fixture.Stack.Unpause("archive-store");
            }
        }

        Assert.Equal(0, Count($"SELECT count(*) FROM organization_owner_recovery_cases WHERE organization_id = '{s.OrganizationId:D}';"));
        var (caseId, operators) = await ApprovedCaseAsync(s, NewOwner(TestUser.New("ConcurrentA")), NewOwner(TestUser.New("ConcurrentB")));
        try
        {
            await WaitDisplayStateAsync(operators[0], caseId, "ReadyForRecovery", WaitingPeriod + TimeSpan.FromSeconds(30));
            var executions = await Task.WhenAll(DecideAsync(operators[0], caseId, "/execute"), DecideAsync(operators[1], caseId, "/execute"));
            Assert.All(executions, e => Assert.True(e.Status == HttpStatusCode.OK, e.Text));
            Assert.Equal(["Recovered", "Replayed"], executions.Select(e => e.Body.GetProperty("outcome").GetString()).Order().ToArray());
            Assert.Equal(1, Count($"SELECT count(*) FROM security_commands WHERE organization_id = '{s.OrganizationId:D}' AND command_kind_name = 'RecoverOrganizationOwners';"));
            Assert.Equal(2, Count($"SELECT count(*) FROM identity_bindings WHERE organization_id = '{s.OrganizationId:D}' AND organization_owner_recovery_case_id = '{caseId:D}';"));
            await WaitDisplayStateAsync(operators[0], caseId, "Recovered", TimeSpan.FromSeconds(60));
        }
        finally
        {
            foreach (var session in operators)
            {
                session.Dispose();
            }
        }
    }
}
