using System.Diagnostics;
using System.Net;
using EnterpriseForms.SecurityTests.Harness;
using EnterpriseForms.StageBTests.Harness;
using Xunit;

namespace EnterpriseForms.StageBTests.Tests;

/// <summary>
/// C8 M4-2 (AC-8.3, contract §5.3) on the real stack — Authority, both PostgreSQL databases, SpiceDB, Vault, business API:
/// an audit page or detail is served only after the Authority committed <c>AuditAccessAuthorized</c> (T11), the entry survives
/// deletion of the business row and carries no business counts, a revoked auditor's next page is refused (T06), non-auditors
/// are refused with nothing recorded as authorized, a held organization lock is never waited on (T21), the rate limit answers
/// 429 (T23), and a revocation commits while audit reads run at the rate limit (T22, measured). T10 (commit fault injection) and
/// T20 (revision moved between check and lock) are covered by the Authority unit tests (AuditAccessServiceTests); they need
/// fault hooks this harness does not have. Executed in the owner-authorized C-163 verification run (PHASE_C8_STATUS §41.6).
/// </summary>
[Collection(StageBStackCollectionDefinition.Name)]
public sealed class SecurityAuditAccessTests(StageBFixture fixture)
{
    private const string Auditors = "/api/v1/organization/security-auditors";
    private const string Explorer = "/api/v1/security/audit-events";

    private async Task<StageBScenario> ScenarioAsync(string name)
    {
        var s = new StageBScenario(fixture, name);
        await s.BootstrapAsync(withAdditionalOwners: true);
        return s;
    }

    private async Task AppointAsync(StageBScenario s, TestUser target)
    {
        var result = await fixture.Api.PostAsync(Auditors, s.Token(s.Administrator), new { userId = target.AuthorityUserId, reasonCode = "AuditCoverageAssignment", useDevelopmentStepUp = true });
        Assert.Equal(HttpStatusCode.NoContent, result.Status);
    }

    private Task<HttpResult> RemoveAsync(StageBScenario s, TestUser target) =>
        fixture.Api.PostAsync($"{Auditors}/{target.AuthorityUserId:D}/remove", s.Token(s.Administrator), new { reasonCode = "AssignmentEnded", useDevelopmentStepUp = true });

    private string AuditStream(StageBScenario s, string kind) =>
        fixture.Stack.AuthorityObserverSql($"SELECT coalesce(string_agg(journal_event_body_json, ' ;; ' ORDER BY journal_stream_revision), '') FROM security_journal_entries WHERE organization_id = '{s.OrganizationId:D}' AND journal_stream_name = 'audit' AND journal_event_kind = '{kind}';").Trim();

    private int AuditStreamCount(StageBScenario s, string kind) =>
        int.Parse(fixture.Stack.AuthorityObserverSql($"SELECT count(*) FROM security_journal_entries WHERE organization_id = '{s.OrganizationId:D}' AND journal_stream_name = 'audit' AND journal_event_kind = '{kind}';").Trim(), System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>T11, AC-M4-2.1–2.3: the Authority entry exists before data is served, states authorization only, and survives the business row's deletion.</summary>
    [Fact]
    public async Task AnAuditorsPageIsServedWithACommittedAuthorityEntryThatSurvivesDeletionOfTheBusinessRow()
    {
        var s = await ScenarioAsync("aud-access-t11");
        await AppointAsync(s, s.Reader);
        var page = await fixture.Api.GetAsync(Explorer + "?pageSize=10", s.Token(s.Reader));
        Assert.True(page.IsSuccess, page.Body);
        var revision = page.Root.GetProperty("accessEvidence").GetProperty("authorityJournalRevision").GetInt64();
        Assert.True(revision > 0);

        var entries = AuditStream(s, "AuditAccessAuthorized");
        Assert.Contains("\"access_kind\":\"ExplorerPage\"", entries, StringComparison.Ordinal);
        Assert.Contains("\"delivery\":\"not_attested\"", entries, StringComparison.Ordinal);
        Assert.Contains($"\"actor_authority_user_id\":\"{s.Reader.AuthorityUserId:D}\"", entries, StringComparison.Ordinal);
        Assert.DoesNotContain("count", entries.Replace("account", string.Empty, StringComparison.Ordinal), StringComparison.OrdinalIgnoreCase);

        var delivered = fixture.Stack.AttackerSql($"SELECT count(*) FROM audit_events WHERE tenant_id = '{s.OrganizationId:D}' AND event_action = 'AUDIT_EVENTS_VIEWED' AND event_metadata_json->>'deliveryOutcome' = 'Delivered';").Trim();
        Assert.Equal("1", delivered);
        fixture.Stack.AttackerSql($"DELETE FROM audit_events WHERE tenant_id = '{s.OrganizationId:D}' AND event_action = 'AUDIT_EVENTS_VIEWED';");
        Assert.Equal(1, AuditStreamCount(s, "AuditAccessAuthorized"));
    }

    /// <summary>T06: no permission caching — the next page and the next detail after removal are refused.</summary>
    [Fact]
    public async Task ARevokedAuditorsNextPageAndDetailAreRefused()
    {
        var s = await ScenarioAsync("aud-access-t06");
        await AppointAsync(s, s.Reader);
        var first = await fixture.Api.GetAsync(Explorer + "?pageSize=1", s.Token(s.Reader));
        Assert.True(first.IsSuccess, first.Body);
        var eventId = first.Root.GetProperty("items")[0].GetProperty("auditEventId").GetGuid();
        var cursor = first.Root.GetProperty("nextCursor").GetString();

        Assert.True((await RemoveAsync(s, s.Reader)).IsSuccess);
        var next = await fixture.Api.GetAsync(Explorer + "?pageSize=1" + (cursor is null ? string.Empty : "&cursor=" + Uri.EscapeDataString(cursor)), s.Token(s.Reader));
        var detail = await fixture.Api.GetAsync($"{Explorer}/{eventId:D}", s.Token(s.Reader));
        Assert.Equal((HttpStatusCode.Forbidden, "AUDIT_VIEW_FORBIDDEN"), (next.Status, next.ErrorCode));
        Assert.Equal((HttpStatusCode.Forbidden, "AUDIT_VIEW_FORBIDDEN"), (detail.Status, detail.ErrorCode));
    }

    /// <summary>D8-6/D8-70 boundary: Security Administrators and owners are not auditors; nothing is recorded as authorized for them.</summary>
    [Fact]
    public async Task NonAuditorsAreRefusedAndNoAuthorizationIsRecordedForThem()
    {
        var s = await ScenarioAsync("aud-access-deny");
        foreach (var user in new[] { s.Administrator, s.Designer, s.Clerk })
        {
            var result = await fixture.Api.GetAsync(Explorer, s.Token(user));
            Assert.Equal((HttpStatusCode.Forbidden, "AUDIT_VIEW_FORBIDDEN"), (result.Status, result.ErrorCode));
        }

        Assert.Equal(0, AuditStreamCount(s, "AuditAccessAuthorized"));
        Assert.Equal("0", fixture.Stack.AttackerSql($"SELECT count(*) FROM audit_events WHERE tenant_id = '{s.OrganizationId:D}' AND event_action IN ('AUDIT_EVENTS_VIEWED', 'AUDIT_EVENT_VIEWED');").Trim());
    }

    /// <summary>AC-M4-2.5 at the stack: another organization's auditor sees none of this organization's events and gets 404 for its ids.</summary>
    [Fact]
    public async Task AnotherOrganizationsAuditorSeesNothingOfThisOrganization()
    {
        var s = await ScenarioAsync("aud-access-org-a");
        var other = await ScenarioAsync("aud-access-org-b");
        await AppointAsync(s, s.Reader);
        await AppointAsync(other, other.Reader);
        var mine = await fixture.Api.GetAsync(Explorer + "?pageSize=50", s.Token(s.Reader));
        Assert.True(mine.IsSuccess, mine.Body);
        var myIds = mine.Root.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("auditEventId").GetGuid()).ToList();
        Assert.NotEmpty(myIds);

        var theirs = await fixture.Api.GetAsync(Explorer + "?pageSize=50", other.Token(other.Reader));
        Assert.True(theirs.IsSuccess, theirs.Body);
        Assert.Empty(theirs.Root.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("auditEventId").GetGuid()).Intersect(myIds));
        var foreign = await fixture.Api.GetAsync($"{Explorer}/{myIds[0]:D}", other.Token(other.Reader));
        var unknown = await fixture.Api.GetAsync($"{Explorer}/{Guid.CreateVersion7():D}", other.Token(other.Reader));
        Assert.Equal((HttpStatusCode.NotFound, unknown.ErrorCode), (foreign.Status, foreign.ErrorCode));
    }

    /// <summary>T21: a long-held organization lock makes the audit read fail fast with 503 AUDIT_EVIDENCE_BUSY; the lock holder is not delayed.</summary>
    [Fact]
    public async Task AHeldOrganizationLockIsNeverWaitedOnAndTheReadFailsFastAsBusy()
    {
        var s = await ScenarioAsync("aud-access-t21");
        await AppointAsync(s, s.Reader);
        var holder = Task.Run(() => fixture.Stack.AuthorityAdministratorSql(
            $"BEGIN; SELECT 1 FROM organization_authorization_states WHERE organization_id = '{s.OrganizationId:D}' FOR UPDATE; SELECT pg_sleep(6); COMMIT;"), TestContext.Current.CancellationToken);
        await Task.Delay(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken); // the holder has the lock
        var watch = Stopwatch.StartNew();
        var busy = await fixture.Api.GetAsync(Explorer, s.Token(s.Reader));
        watch.Stop();
        await holder;

        Assert.Equal((HttpStatusCode.ServiceUnavailable, "AUDIT_EVIDENCE_BUSY"), (busy.Status, busy.ErrorCode));
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(2.5), $"the busy refusal took {watch.Elapsed.TotalMilliseconds:F0} ms; it must not wait for the lock");
        Assert.Equal(0, AuditStreamCount(s, "AuditAccessAuthorized"));
        Assert.True((await fixture.Api.GetAsync(Explorer, s.Token(s.Reader))).IsSuccess, "after the lock is released the read succeeds");
    }

    /// <summary>T23: the per-user bucket (burst 10, 5/s) refuses a burst with 429 RATE_LIMITED.</summary>
    [Fact]
    public async Task ABurstAboveTheUserLimitIsRefusedWith429()
    {
        var s = await ScenarioAsync("aud-access-t23");
        await AppointAsync(s, s.Reader);
        var results = await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => fixture.Api.GetAsync(Explorer + "?pageSize=1", s.Token(s.Reader))));
        Assert.Contains(results, r => r.Status == HttpStatusCode.TooManyRequests && r.ErrorCode == "RATE_LIMITED");
        Assert.True(results.Count(r => r.IsSuccess) <= 10 + 2, "at most the burst plus the refill during the burst succeeds");
    }

    /// <summary>
    /// T22 (measured, recorded): while one auditor reads at the rate limit, a Security Auditor removal (a security command taking
    /// the organization lock normally) still commits, and its duration stays within one bounded audit-access hold of the baseline.
    /// </summary>
    [Fact]
    public async Task ARevocationCommitsUnderAnAuditReadFloodAndItsDurationIsRecorded()
    {
        var s = await ScenarioAsync("aud-access-t22");
        await AppointAsync(s, s.Reader);
        await AppointAsync(s, s.Clerk);
        var baseline = Stopwatch.StartNew();
        Assert.True((await RemoveAsync(s, s.Clerk)).IsSuccess);
        baseline.Stop();
        await AppointAsync(s, s.Clerk);

        using var stop = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var flood = Task.Run(async () =>
        {
            var served = 0;
            while (!stop.IsCancellationRequested)
            {
                if ((await fixture.Api.GetAsync(Explorer + "?pageSize=1", s.Token(s.Reader))).IsSuccess)
                {
                    served++;
                }

                await Task.Delay(200, CancellationToken.None); // 5/s: the per-user rate limit
            }

            return served;
        }, CancellationToken.None);
        await Task.Delay(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
        var underLoad = Stopwatch.StartNew();
        var removal = await RemoveAsync(s, s.Clerk);
        underLoad.Stop();
        await Task.Delay(TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);
        await stop.CancelAsync();
        var served = await flood;

        Assert.True(removal.IsSuccess, removal.Body);
        Assert.True(served > 0, "the flood was served");
        TestContext.Current.TestOutputHelper?.WriteLine($"T22: baseline removal {baseline.Elapsed.TotalMilliseconds:F0} ms; under audit-read flood {underLoad.Elapsed.TotalMilliseconds:F0} ms; audit pages served {served}");
        TestContext.Current.SendDiagnosticMessage($"T22: baseline removal {baseline.Elapsed.TotalMilliseconds:F0} ms; under audit-read flood {underLoad.Elapsed.TotalMilliseconds:F0} ms; audit pages served {served}");
        Assert.True(underLoad.Elapsed <= baseline.Elapsed + TimeSpan.FromSeconds(2), $"removal under load took {underLoad.Elapsed.TotalMilliseconds:F0} ms against a {baseline.Elapsed.TotalMilliseconds:F0} ms baseline");
    }
}
