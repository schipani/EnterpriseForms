using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using EnterpriseForms.SecurityTests.Harness;
using EnterpriseForms.StageBTests.Harness;
using Xunit;

namespace EnterpriseForms.StageBTests.Tests;

/// <summary>
/// C8 M2 Stages 1-2 on the real stack (Authority + both PostgreSQL databases + SpiceDB + archive): two-person membership approval
/// by independently authenticated Platform Operators through the platform plane, with the business API used only for the owner's
/// request and edit. Covers PO-T04/T05/T08/T11/T15/T18/T21/T22 against real locks, unique indexes, the immutability trigger,
/// the archive and a simulated AuthorityDb restore. Synthetic operator keys only: never two-human approval evidence.
/// </summary>
[Collection(StageBStackCollectionDefinition.Name)]
public sealed class PlatformOperatorApprovalTests(StageBFixture fixture)
{
    private const string Justification = "Verified with the requesting owner by phone (OPS-77)";

    private sealed record Op(Guid Id, string Subject, string KeyId, string PrivateKey)
    {
        public string Assertion => TestIdentity.PlatformOperatorAssertion(Subject);
    }

    // ---- harness ----

    /// <summary>The three harness operators; registered by the recovery ceremony when a fresh stack has fewer than two active operators.</summary>
    private async Task<IReadOnlyList<Op>> OperatorsAsync()
    {
        var status = await fixture.Authority.OperatorAsync(HttpMethod.Get, "/internal/v1/operator/platform-operators", null);
        Assert.True(status.IsSuccess, status.Body);
        var registered = status.Root.GetProperty("operators").EnumerateArray().ToList();
        var missing = TestIdentity.SyntheticPlatformOperators.Where(t => registered.All(r => r.GetProperty("subject_identifier").GetString() != t.Subject)).ToList();
        if (missing.Count > 0)
        {
            Assert.True(status.Root.GetProperty("active_operator_count").GetInt32() < 2, "the registry has other active operators but not the harness trio: " + status.Body);
            var body = new
            {
                operation_id = Guid.CreateVersion7(),
                operators = missing.Select(t => new { issuer_uri = TestIdentity.PlatformOperatorIssuer, subject_identifier = t.Subject, display_name = t.DisplayName, evidence_kind = "SyntheticDevelopmentJws", synthetic_key_identifier = t.KeyId, synthetic_public_key_base64 = TestIdentity.PlatformOperatorPublicKey(t.PrivateKeyBase64) }).ToArray(),
                justification = "Stage B harness: first platform operators of this disposable stack",
                incident_reference = "SECTEST-C8-PO",
                dry_run = true,
            };
            var preview = await fixture.Authority.OperatorAsync(HttpMethod.Post, "/internal/v1/operator/platform-operators/bootstrap", body);
            Assert.True(preview.IsSuccess && preview.Root.GetProperty("eligible").GetBoolean(), preview.Body);
            var done = await fixture.Authority.OperatorAsync(HttpMethod.Post, "/internal/v1/operator/platform-operators/bootstrap", new
            {
                body.operation_id, body.operators, body.justification, body.incident_reference, dry_run = false,
                approvals = new[] { TestIdentity.OperatorApproval(preview.Root, "test-synthetic-approver-1"), TestIdentity.OperatorApproval(preview.Root, "test-synthetic-approver-2") },
            });
            Assert.True(done.IsSuccess, done.Body);
            status = await fixture.Authority.OperatorAsync(HttpMethod.Get, "/internal/v1/operator/platform-operators", null);
            registered = status.Root.GetProperty("operators").EnumerateArray().ToList();
        }

        return TestIdentity.SyntheticPlatformOperators.Select(t =>
        {
            var row = registered.Single(r => r.GetProperty("subject_identifier").GetString() == t.Subject);
            Assert.Equal("Active", row.GetProperty("status").GetString());
            return new Op(row.GetProperty("platform_operator_id").GetGuid(), t.Subject, t.KeyId, t.PrivateKeyBase64);
        }).ToList();
    }

    private async Task<Guid> RequestAsync(StageBScenario s, string hint = "Nia Newcomer")
    {
        var created = await fixture.Api.PostAsync("/api/v1/organization/membership-requests", s.Token(s.Designer),
            new { subjectIdentifier = "po-person-" + Guid.NewGuid().ToString("N")[..10], displayNameHint = hint, initialRoleName = "Designer", reason = "joins the intake team", useDevelopmentStepUp = true });
        Assert.True(created.IsSuccess, created.Body);
        return created.Root.GetProperty("requestId").GetGuid();
    }

    private static string Path(StageBScenario s, Guid requestId, string action = "") => $"/internal/v1/platform/organizations/{s.OrganizationId:D}/membership-requests/{requestId:D}{action}";

    private async Task<string> StatementAsync(Op op, StageBScenario s, Guid requestId)
    {
        var detail = await fixture.Authority.PlatformAsync(HttpMethod.Get, Path(s, requestId), op.Assertion, null);
        Assert.True(detail.IsSuccess, detail.Body);
        return detail.Root.GetProperty("statement_sha256").GetString()!;
    }

    private static string Evidence(Op op, StageBScenario s, Guid requestId, string sha, string decision = "approve") =>
        TestIdentity.PlatformOperatorEvidence(op.KeyId, op.PrivateKey, op.Id, decision, "MembershipApproval", requestId, s.OrganizationId, sha, Justification);

    private async Task<HttpResult> DecideAsync(Op op, StageBScenario s, Guid requestId, string decision = "approve", string? evidence = null, string? sha = null)
    {
        sha ??= await StatementAsync(op, s, requestId);
        var action = decision switch { "approve" => "/approvals", "withdraw" => "/approvals/withdraw", _ => "/reject" };
        return await fixture.Authority.PlatformAsync(HttpMethod.Post, Path(s, requestId, action), op.Assertion,
            new { expected_statement_sha256 = sha, justification = Justification, dry_run = false, evidence_kind = "SyntheticDevelopmentJws", evidence = evidence ?? Evidence(op, s, requestId, sha, decision) });
    }

    private static void Refused(HttpResult result, params string[] codes)
    {
        Assert.False(result.IsSuccess, result.Body);
        Assert.Contains(result.ErrorCode, codes);
    }

    private string Sql(string sql) => fixture.Stack.AuthorityObserverSql(sql).Trim();

    private int Bindings(Guid requestId) => int.Parse(Sql($"SELECT count(*) FROM identity_bindings WHERE organization_membership_request_id = '{requestId:D}';"), System.Globalization.CultureInfo.InvariantCulture);

    // ---- tests ----

    [Fact]
    public async Task TwoIndependentOperatorsCreateTheMembershipOnlyAfterTheSecondApprovalAndNoOperatorCanActTwice()
    {
        var ops = await OperatorsAsync();
        var s = new StageBScenario(fixture, "po-two-operators");
        await s.BootstrapAsync();
        var requestId = await RequestAsync(s);

        var first = await DecideAsync(ops[0], s, requestId);
        Assert.True(first.IsSuccess, first.Body);
        Assert.Equal("AwaitingSecondApproval", first.Root.GetProperty("outcome").GetString());
        Assert.Equal(0, Bindings(requestId));
        Assert.Equal("Pending", Sql($"SELECT approval_state_name FROM platform_operator_approvals WHERE organization_membership_request_id = '{requestId:D}';"));

        // The same operator again (fresh evidence) and the same operator presenting the second operator's genuine evidence.
        Refused(await DecideAsync(ops[0], s, requestId), "OPERATOR_ALREADY_APPROVED");
        var sha = await StatementAsync(ops[0], s, requestId);
        Refused(await DecideAsync(ops[0], s, requestId, evidence: Evidence(ops[1], s, requestId, sha), sha: sha), "OPERATOR_ALREADY_APPROVED", "OPERATOR_EVIDENCE_IDENTITY_MISMATCH");
        Refused(await DecideAsync(ops[2], s, requestId, evidence: Evidence(ops[1], s, requestId, sha), sha: sha), "OPERATOR_EVIDENCE_IDENTITY_MISMATCH");
        Refused(await DecideAsync(ops[1], s, requestId, sha: new string('c', 64)), "APPROVAL_STATEMENT_CHANGED");
        Assert.Equal(0, Bindings(requestId));

        var second = await DecideAsync(ops[1], s, requestId);
        Assert.True(second.IsSuccess, second.Body);
        Assert.Equal("Approved", second.Root.GetProperty("outcome").GetString());
        Assert.Equal(1, Bindings(requestId));
        Assert.Equal("2", Sql($"SELECT count(*) FROM platform_operator_approvals WHERE organization_membership_request_id = '{requestId:D}' AND approval_state_name = 'Consumed' AND consumed_by_security_command_id IS NOT NULL;"));
        Assert.Equal("1", Sql($"SELECT count(*) FROM security_commands WHERE organization_id = '{s.OrganizationId:D}' AND command_kind_name = 'ApproveOrganizationMembership';"));
        Assert.Equal(ops[1].Id.ToString("D"), Sql($"SELECT request_decided_by_platform_operator_id FROM organization_membership_requests WHERE organization_membership_request_id = '{requestId:D}';"));
        Refused(await DecideAsync(ops[2], s, requestId), "MEMBERSHIP_REQUEST_NOT_PENDING");

        // The events are in the independent archive, and the closed approvals are immutable even to a direct UPDATE (trigger).
        Assert.Equal("0", Sql("SELECT count(*) FROM platform_journal_entries WHERE archive_delivery_state <> 'Anchored';"));
        fixture.Stack.AuthorityRestoreSimulationSql($"UPDATE platform_operator_approvals SET approval_state_name = 'Pending', approval_closed_on = NULL, approval_closure_reason_name = NULL WHERE organization_membership_request_id = '{requestId:D}';");
        Assert.Equal("2", Sql($"SELECT count(*) FROM platform_operator_approvals WHERE organization_membership_request_id = '{requestId:D}' AND approval_state_name = 'Consumed';"));

        // Operator identities never become organization identities.
        foreach (var op in ops)
        {
            Assert.Equal("0", Sql($"SELECT count(*) FROM identity_bindings WHERE identity_subject_identifier = '{op.Subject}';"));
        }
    }

    [Fact]
    public async Task AReplacedRequestInvalidatesTheFirstApprovalAndConcurrentSecondApprovalsCreateExactlyOneMembership()
    {
        var ops = await OperatorsAsync();
        var s = new StageBScenario(fixture, "po-replace-race");
        await s.BootstrapAsync();

        // Replacement after the first approval.
        var original = await RequestAsync(s);
        var oldSha = await StatementAsync(ops[1], s, original);
        Assert.True((await DecideAsync(ops[0], s, original)).IsSuccess);
        var page = await fixture.Api.GetAsync("/api/v1/organization/membership-requests?pageSize=50", s.Token(s.Designer));
        var digest = page.Root.GetProperty("items").EnumerateArray().Single(r => r.GetProperty("requestId").GetGuid() == original).GetProperty("requestDigest").GetString();
        var replaced = await fixture.Api.PostAsync($"/api/v1/organization/membership-requests/{original:D}/replace", s.Token(s.Designer),
            new { expectedRequestDigest = digest, displayNameHint = "Nia Newcomer-Okafor", initialRoleName = (string?)null, reason = "corrected after the intake call", useDevelopmentStepUp = true });
        Assert.True(replaced.IsSuccess, replaced.Body);
        Assert.Equal("Invalidated|RequestReplaced", Sql($"SELECT approval_state_name || '|' || approval_closure_reason_name FROM platform_operator_approvals WHERE organization_membership_request_id = '{original:D}';"));
        Refused(await DecideAsync(ops[1], s, original, sha: oldSha, evidence: Evidence(ops[1], s, original, oldSha)), "APPROVAL_STATEMENT_CHANGED", "MEMBERSHIP_REQUEST_NOT_PENDING");
        Assert.Equal(0, Bindings(original));

        // Two different operators race to give the second approval: exactly one membership.
        var raced = await RequestAsync(s);
        Assert.True((await DecideAsync(ops[0], s, raced)).IsSuccess);
        var shaB = await StatementAsync(ops[1], s, raced);
        var shaC = await StatementAsync(ops[2], s, raced);
        var results = await Task.WhenAll(DecideAsync(ops[1], s, raced, sha: shaB), DecideAsync(ops[2], s, raced, sha: shaC));
        Assert.Single(results, r => r.IsSuccess && r.Root.GetProperty("outcome").GetString() == "Approved");
        Assert.Single(results, r => !r.IsSuccess);
        Assert.Equal(1, Bindings(raced));
        Assert.Equal("1", Sql($"SELECT count(*) FROM security_commands WHERE organization_id = '{s.OrganizationId:D}' AND command_kind_name = 'ApproveOrganizationMembership';"));
    }

    [Fact]
    public async Task ARestoreFromBeforeASuspensionIsDetectedAndReplayKeepsTheOperatorSuspended()
    {
        var ops = await OperatorsAsync();
        // A fresh operator, provisioned by two operators (the harness trio is never suspended).
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var dana = new Op(Guid.CreateVersion7(), "sectest-operator-dana-" + Guid.NewGuid().ToString("N")[..8], "sectest-key-dana-" + Guid.NewGuid().ToString("N")[..8], Convert.ToBase64String(key.ExportPkcs8PrivateKey()));
        var candidate = new { issuer_uri = TestIdentity.PlatformOperatorIssuer, subject_identifier = dana.Subject, display_name = "Dana Operator (synthetic)", evidence_kind = "SyntheticDevelopmentJws", synthetic_key_identifier = dana.KeyId, synthetic_public_key_base64 = Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()) };
        var proposalPreview = await fixture.Authority.PlatformAsync(HttpMethod.Post, "/internal/v1/platform/operators/proposals", ops[0].Assertion, new { operation_id = dana.Id, change = "Provision", candidate, justification = Justification, dry_run = true });
        Assert.True(proposalPreview.IsSuccess && proposalPreview.Root.GetProperty("eligible").GetBoolean(), proposalPreview.Body);
        var proposed = await fixture.Authority.PlatformAsync(HttpMethod.Post, "/internal/v1/platform/operators/proposals", ops[0].Assertion, new
        {
            operation_id = dana.Id, change = "Provision", candidate, justification = Justification, dry_run = false, evidence_kind = "SyntheticDevelopmentJws",
            evidence = TestIdentity.PlatformOperatorEvidence(ops[0].KeyId, ops[0].PrivateKey, ops[0].Id, "propose", "PlatformOperatorChange", dana.Id, Guid.Empty, proposalPreview.Root.GetProperty("statement_sha256").GetString()!, Justification),
        });
        Assert.True(proposed.IsSuccess, proposed.Body);
        var sha = proposalPreview.Root.GetProperty("statement_sha256").GetString()!;
        Refused(await fixture.Authority.PlatformAsync(HttpMethod.Post, $"/internal/v1/platform/operators/proposals/{dana.Id:D}/confirm", ops[0].Assertion, new
        {
            expected_statement_sha256 = sha, justification = Justification, dry_run = false, evidence_kind = "SyntheticDevelopmentJws",
            evidence = TestIdentity.PlatformOperatorEvidence(ops[0].KeyId, ops[0].PrivateKey, ops[0].Id, "confirm", "PlatformOperatorChange", dana.Id, Guid.Empty, sha, Justification),
        }), "OPERATOR_APPROVALS_NOT_INDEPENDENT");
        var confirmed = await fixture.Authority.PlatformAsync(HttpMethod.Post, $"/internal/v1/platform/operators/proposals/{dana.Id:D}/confirm", ops[1].Assertion, new
        {
            expected_statement_sha256 = sha, justification = Justification, dry_run = false, evidence_kind = "SyntheticDevelopmentJws",
            evidence = TestIdentity.PlatformOperatorEvidence(ops[1].KeyId, ops[1].PrivateKey, ops[1].Id, "confirm", "PlatformOperatorChange", dana.Id, Guid.Empty, sha, Justification),
        });
        Assert.True(confirmed.IsSuccess, confirmed.Body);
        Assert.Equal("Provisioned", confirmed.Root.GetProperty("outcome").GetString());

        var s = new StageBScenario(fixture, "po-restore");
        await s.BootstrapAsync();
        var requestId = await RequestAsync(s);
        Assert.True((await DecideAsync(dana, s, requestId)).IsSuccess);

        // Operator A suspends Dana (one operator, immediate); Dana's pending approval is invalidated.
        var operationId = Guid.CreateVersion7();
        var preview = await fixture.Authority.PlatformAsync(HttpMethod.Post, $"/internal/v1/platform/operators/{dana.Id:D}/suspend", ops[0].Assertion, new { operation_id = operationId, justification = Justification, dry_run = true });
        Assert.True(preview.IsSuccess, preview.Body);
        var suspended = await fixture.Authority.PlatformAsync(HttpMethod.Post, $"/internal/v1/platform/operators/{dana.Id:D}/suspend", ops[0].Assertion, new
        {
            operation_id = operationId, justification = Justification, dry_run = false, evidence_kind = "SyntheticDevelopmentJws",
            evidence = TestIdentity.PlatformOperatorEvidence(ops[0].KeyId, ops[0].PrivateKey, ops[0].Id, "suspend", "PlatformOperatorSuspension", operationId, Guid.Empty, preview.Root.GetProperty("statement_sha256").GetString()!, Justification),
        });
        Assert.True(suspended.IsSuccess, suspended.Body);
        Assert.Equal("Anchored", suspended.Root.GetProperty("journal_archive_state").GetString());
        var revision = suspended.Root.GetProperty("journal_revision").GetInt64();
        Assert.Equal("Invalidated|OperatorSuspended", Sql($"SELECT approval_state_name || '|' || approval_closure_reason_name FROM platform_operator_approvals WHERE organization_membership_request_id = '{requestId:D}';"));

        // Simulated AuthorityDb restore from just before the suspension (rows as they were; the archive is untouched).
        fixture.Stack.AuthorityRestoreSimulationSql($"""
            ALTER TABLE platform_operator_approvals DISABLE TRIGGER trg_platform_operator_approvals_keep_closed;
            UPDATE platform_operator_approvals SET approval_state_name = 'Pending', approval_closed_on = NULL, approval_closure_reason_name = NULL WHERE organization_membership_request_id = '{requestId:D}';
            ALTER TABLE platform_operator_approvals ENABLE TRIGGER trg_platform_operator_approvals_keep_closed;
            UPDATE platform_operators SET operator_status_name = 'Active', operator_row_revision = operator_row_revision - 1 WHERE platform_operator_id = '{dana.Id:D}';
            DELETE FROM platform_operator_evidence_uses WHERE evidence_decision_name = 'suspend' AND platform_operator_id = '{ops[0].Id:D}' AND evidence_used_on >= now() - interval '5 minutes';
            DELETE FROM platform_journal_entries WHERE platform_journal_revision >= {revision};
            UPDATE platform_journal_heads SET platform_journal_revision = {revision - 1}, latest_platform_journal_digest = (SELECT journal_event_digest FROM platform_journal_entries WHERE platform_journal_revision = {revision - 1});
            """);
        Assert.Equal("Active", Sql($"SELECT operator_status_name FROM platform_operators WHERE platform_operator_id = '{dana.Id:D}';"));

        // Fail closed: nobody can act, and Dana's resurrected approval cannot complete the membership.
        var behind = await fixture.Authority.PlatformAsync(HttpMethod.Get, "/internal/v1/platform/operator/me", dana.Assertion, null);
        Assert.Equal(HttpStatusCode.Locked, behind.Status);
        Assert.Equal("PLATFORM_JOURNAL_BEHIND_ARCHIVE", behind.ErrorCode);
        Refused(await fixture.Authority.PlatformAsync(HttpMethod.Post, Path(s, requestId, "/approvals"), ops[1].Assertion, new { expected_statement_sha256 = new string('0', 64), justification = Justification, dry_run = true }), "PLATFORM_JOURNAL_BEHIND_ARCHIVE");
        Assert.Equal(0, Bindings(requestId));
        var plane = await fixture.Authority.OperatorAsync(HttpMethod.Get, "/internal/v1/operator/platform-operators", null);
        Assert.Equal("PLATFORM_JOURNAL_BEHIND_ARCHIVE", plane.Root.GetProperty("journal_gate_refusal").GetString());

        // Replay from the independent archive: Dana is suspended again and her approval invalid; the plane reopens.
        var replay = await fixture.Authority.OperatorAsync(HttpMethod.Post, "/internal/v1/operator/platform-journal/replay", new { });
        Assert.True(replay.IsSuccess, replay.Body);
        Assert.True(replay.Root.GetProperty("events_replayed").GetInt32() >= 1, replay.Body);
        Assert.True(replay.Root.GetProperty("gate_open").GetBoolean(), replay.Body);
        Assert.Equal("Suspended", Sql($"SELECT operator_status_name FROM platform_operators WHERE platform_operator_id = '{dana.Id:D}';"));
        Assert.Equal("Invalidated", Sql($"SELECT approval_state_name FROM platform_operator_approvals WHERE organization_membership_request_id = '{requestId:D}';"));
        Refused(await fixture.Authority.PlatformAsync(HttpMethod.Get, "/internal/v1/platform/operator/me", dana.Assertion, null), "PLATFORM_OPERATOR_NOT_ACTIVE");
        var afterReplay = await DecideAsync(ops[1], s, requestId);
        Assert.True(afterReplay.IsSuccess, afterReplay.Body);
        Assert.Equal("AwaitingSecondApproval", afterReplay.Root.GetProperty("outcome").GetString());
        Assert.Equal(0, Bindings(requestId));
    }

    [Fact]
    public async Task OperatorsPageThroughRequestsAcrossOrganizationsAndOrganizationIdentitiesAreNotOperators()
    {
        var ops = await OperatorsAsync();
        var tag = "po-search-" + Guid.NewGuid().ToString("N")[..8];
        var first = new StageBScenario(fixture, "po-search-1");
        await first.BootstrapAsync();
        var second = new StageBScenario(fixture, "po-search-2");
        await second.BootstrapAsync();
        await RequestAsync(first, tag + " one");
        await RequestAsync(second, tag + " two");
        await RequestAsync(first, tag + " three");

        var page = await fixture.Authority.PlatformAsync(HttpMethod.Post, "/internal/v1/platform/membership-requests/search", ops[0].Assertion, new { query = tag, status = "all", limit = 2 });
        Assert.True(page.IsSuccess, page.Body);
        Assert.Equal(3, page.Root.GetProperty("total_count").GetInt32());
        Assert.Equal(2, page.Root.GetProperty("items").GetArrayLength());
        var cursor = page.Root.GetProperty("next_cursor").GetString();
        var next = await fixture.Authority.PlatformAsync(HttpMethod.Post, "/internal/v1/platform/membership-requests/search", ops[0].Assertion, new { query = tag, status = "all", limit = 2, cursor });
        Assert.True(next.IsSuccess, next.Body);
        var all = page.Root.GetProperty("items").EnumerateArray().Concat(next.Root.GetProperty("items").EnumerateArray()).ToList();
        Assert.Equal(3, all.Select(i => i.GetProperty("request_id").GetGuid()).Distinct().Count());
        Assert.Equal(2, all.Select(i => i.GetProperty("organization_id").GetGuid()).Distinct().Count());
        Assert.All(all, i => Assert.False(i.TryGetProperty("reason", out _)));
        Refused(await fixture.Authority.PlatformAsync(HttpMethod.Post, "/internal/v1/platform/membership-requests/search", ops[1].Assertion, new { query = tag, status = "all", limit = 2, cursor }), "CURSOR_INVALID");

        // An organization owner's identity (even with the genuine console credential) is not a Platform Operator; a business user
        // assertion is not an operator assertion; the business workload credential does not reach the platform plane.
        Refused(await fixture.Authority.PlatformAsync(HttpMethod.Get, "/internal/v1/platform/operator/me", TestIdentity.PlatformOperatorAssertion(first.Designer.Subject), null), "PLATFORM_OPERATOR_NOT_ACTIVE");
        Assert.Equal(HttpStatusCode.Unauthorized, (await fixture.Authority.PlatformAsync(HttpMethod.Get, "/internal/v1/platform/operator/me", first.Token(first.Designer), null)).Status);
        Assert.Contains((await fixture.Authority.PlatformAsync(HttpMethod.Get, "/internal/v1/platform/operator/me", ops[0].Assertion, null, TestIdentity.WorkloadToken())).Status, new[] { HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden });
    }
}
