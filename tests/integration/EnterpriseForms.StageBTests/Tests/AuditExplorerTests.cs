using System.Globalization;
using System.Net;
using System.Text.Json;
using EnterpriseForms.SecurityTests.Harness;
using EnterpriseForms.StageBTests.Harness;
using Xunit;

namespace EnterpriseForms.StageBTests.Tests;

/// <summary>
/// C8 M4-2 PG-054/PG-055 on the real stack: keyset paging over PostgreSQL (stable under concurrent inserts, sequence tie-break),
/// every filter, cursor binding to caller, organization and filter, the organization predicate with a second organization
/// present (R-15: no business RLS), the sentinel leak scan over list and detail responses (AC-M4-2.4), forged rows rendered as
/// envelopes, and the business delivery-outcome rows. Rows used for paging, filters and sentinels are inserted by the hostile
/// BusinessDb administrator (the projection is untrusted by design, RR-1), shaped as the call sites write them. Executed in the
/// owner-authorized C-163 verification run (PHASE_C8_STATUS §41.6).
/// </summary>
[Collection(StageBStackCollectionDefinition.Name)]
public sealed class AuditExplorerTests(StageBFixture fixture)
{
    private const string Explorer = "/api/v1/security/audit-events";

    private static readonly string[] Sentinels =
    [
        "SENT-CONFIDENTIAL-VALUE", "SENT-PUBLIC-VALUE", "SENT-PIN-4471", "SENT-FILE.pdf", "SENT-SEARCH-TERM", "SENT-FREE-REASON", "SENT-FIELD-KEY", "SENT-FIELD-LABEL", "SENT-OPTION-TEXT", "SENT-NAME-HINT",
    ];

    private async Task<StageBScenario> AuditorScenarioAsync(string name)
    {
        var s = new StageBScenario(fixture, name);
        await s.BootstrapAsync();
        var appointed = await fixture.Api.PostAsync("/api/v1/organization/security-auditors", s.Token(s.Administrator), new { userId = s.Reader.AuthorityUserId, reasonCode = "AuditCoverageAssignment", useDevelopmentStepUp = true });
        Assert.Equal(HttpStatusCode.NoContent, appointed.Status);
        return s;
    }

    /// <summary>Inserts one projection row as the BusinessDb administrator; returns its id.</summary>
    private Guid Insert(Guid tenant, string action, DateTimeOffset at, object? metadata = null, string category = "Record", string outcome = "Success", Guid? actor = null, Guid? record = null, string? entityType = null, Guid? entityId = null, Guid? correlation = null, string description = "seeded")
    {
        var id = Guid.CreateVersion7();
        var json = JsonSerializer.Serialize(metadata ?? new { }).Replace("'", "''", StringComparison.Ordinal);
        static string Nullable(Guid? value) => value is { } v ? $"'{v:D}'" : "NULL";
        static string Text(string? value) => value is null ? "NULL" : "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";
        fixture.Stack.AttackerSql($"""
            INSERT INTO audit_events (audit_event_id, tenant_id, event_occurred_on, event_category, event_action, event_outcome, actor_user_id, actor_type, form_record_id, target_entity_type, target_entity_id, correlation_identifier, event_description, event_metadata_json, event_schema_version)
            VALUES ('{id:D}', '{tenant:D}', '{at.UtcDateTime.ToString("O", CultureInfo.InvariantCulture)}', {Text(category)}, {Text(action)}, {Text(outcome)}, {Nullable(actor)}, 'User', {Nullable(record)}, {Text(entityType)}, {Nullable(entityId)}, '{correlation ?? Guid.CreateVersion7():D}', {Text(description)}, '{json}', 1);
            """);
        return id;
    }

    /// <summary>
    /// A read as a contract-compliant client makes it: 503 <c>AUDIT_EVIDENCE_BUSY</c> (nothing served; the organization lock was held,
    /// for example by the archive processor delivering the previous read's journal entry) is retried after a pause, at most three
    /// times. Every retry is written to the test output (M4-2 verification finding F-M42-V1); any other answer is returned as is.
    /// </summary>
    private async Task<HttpResult> ReadAsync(string path, string token)
    {
        for (var retry = 0; ; retry++)
        {
            var result = await fixture.Api.GetAsync(path, token);
            if (result.Status != HttpStatusCode.ServiceUnavailable || result.ErrorCode != "AUDIT_EVIDENCE_BUSY" || retry == 3)
            {
                return result;
            }

            TestContext.Current.TestOutputHelper?.WriteLine($"F-M42-V1: AUDIT_EVIDENCE_BUSY on {path.Split('?')[0]} (retry {retry + 1})");
            await Task.Delay(250, TestContext.Current.CancellationToken);
        }
    }

    private async Task<List<JsonElement>> AllPagesAsync(StageBScenario s, string query)
    {
        var items = new List<JsonElement>();
        string? cursor = null;
        do
        {
            var page = await ReadAsync(Explorer + "?" + query + (cursor is null ? string.Empty : "&cursor=" + Uri.EscapeDataString(cursor)), s.Token(s.Reader));
            Assert.True(page.IsSuccess, page.Body);
            items.AddRange(page.Root.GetProperty("items").EnumerateArray().Select(i => i.Clone()));
            cursor = page.Root.GetProperty("nextCursor").GetString();
        }
        while (cursor is not null);
        return items;
    }

    [Fact]
    public async Task KeysetPagingIsStableUnderConcurrentInsertsAndBreaksTiesBySequence()
    {
        var s = await AuditorScenarioAsync("audx-keyset");
        var at = new DateTimeOffset(2026, 1, 5, 10, 0, 0, TimeSpan.Zero);
        var seeded = Enumerable.Range(0, 7).Select(i => Insert(s.OrganizationId, "RECORD_VIEWED", at.AddSeconds(i / 3), category: "KeysetProbe")).ToList(); // equal times: sequence tie-break

        var first = await ReadAsync(Explorer + "?category=KeysetProbe&pageSize=3", s.Token(s.Reader));
        Assert.True(first.IsSuccess, first.Body);
        Insert(s.OrganizationId, "RECORD_VIEWED", at.AddDays(1), category: "KeysetProbe"); // a newer row arrives between pages
        var rest = new List<JsonElement>(first.Root.GetProperty("items").EnumerateArray().Select(i => i.Clone()));
        var cursor = first.Root.GetProperty("nextCursor").GetString();
        while (cursor is not null)
        {
            var page = await ReadAsync(Explorer + "?category=KeysetProbe&pageSize=3&cursor=" + Uri.EscapeDataString(cursor), s.Token(s.Reader));
            Assert.True(page.IsSuccess, page.Body);
            rest.AddRange(page.Root.GetProperty("items").EnumerateArray().Select(i => i.Clone()));
            cursor = page.Root.GetProperty("nextCursor").GetString();
        }

        var served = rest.Select(i => i.GetProperty("auditEventId").GetGuid()).ToList();
        Assert.Equal(7, served.Count);
        Assert.Equal(served.Count, served.Distinct().Count());
        Assert.Equal(seeded.OrderBy(id => id).ToHashSet(), served.ToHashSet());
        var keys = rest.Select(i => (i.GetProperty("occurredOn").GetDateTimeOffset(), long.Parse(i.GetProperty("sequence").ToString(), CultureInfo.InvariantCulture))).ToList();
        Assert.Equal(keys.OrderByDescending(k => k.Item1).ThenByDescending(k => k.Item2).ToList(), keys);
    }

    [Fact]
    public async Task EveryFilterNarrowsToTheMatchingRowsOnly()
    {
        var s = await AuditorScenarioAsync("audx-filters");
        var at = new DateTimeOffset(2026, 2, 1, 8, 0, 0, TimeSpan.Zero);
        var actor = s.Clerk.AuthorityUserId;
        var record = Guid.CreateVersion7();
        var team = Guid.CreateVersion7();
        var correlation = Guid.CreateVersion7();
        var byActor = Insert(s.OrganizationId, "RECORD_UPDATED", at, category: "FilterProbe", actor: actor);
        var byRecord = Insert(s.OrganizationId, "RECORD_VIEWED", at.AddMinutes(1), category: "FilterProbe", record: record);
        var byEntity = Insert(s.OrganizationId, "TEAM_RENAMED", at.AddMinutes(2), category: "FilterProbe", entityType: "team", entityId: team);
        var byCorrelation = Insert(s.OrganizationId, "RECORD_VIEWED", at.AddMinutes(3), category: "FilterProbe", correlation: correlation);
        var denied = Insert(s.OrganizationId, "RECORD_LIFECYCLE_DENIED", at.AddMinutes(4), category: "FilterProbe", outcome: "Denied");
        var late = Insert(s.OrganizationId, "RECORD_VIEWED", at.AddDays(3), category: "FilterProbe");

        async Task<HashSet<Guid>> Ids(string query) => (await AllPagesAsync(s, "category=FilterProbe&" + query)).Select(i => i.GetProperty("auditEventId").GetGuid()).ToHashSet();
        Assert.Equal([byActor], await Ids($"actorUserId={actor:D}"));
        Assert.Equal([byRecord], await Ids($"formRecordId={record:D}"));
        Assert.Equal([byEntity], await Ids($"entityType=team&entityId={team:D}"));
        Assert.Equal([byCorrelation], await Ids($"correlationId={correlation:D}"));
        Assert.Equal([denied], await Ids("outcome=Denied"));
        Assert.Equal([byEntity], await Ids("action=TEAM_RENAMED"));
        Assert.Equal([late], await Ids("from=" + Uri.EscapeDataString(at.AddDays(1).ToString("O", CultureInfo.InvariantCulture))));
        Assert.DoesNotContain(late, await Ids("to=" + Uri.EscapeDataString(at.AddDays(1).ToString("O", CultureInfo.InvariantCulture))));
        var invalid = await ReadAsync(Explorer + "?action=" + Uri.EscapeDataString("record viewed"), s.Token(s.Reader));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, invalid.Status);
    }

    [Fact]
    public async Task ACursorBelongsToItsCallerOrganizationAndFilters()
    {
        var s = await AuditorScenarioAsync("audx-cursor");
        var other = await AuditorScenarioAsync("audx-cursor-other");
        var at = new DateTimeOffset(2026, 3, 1, 8, 0, 0, TimeSpan.Zero);
        foreach (var i in Enumerable.Range(0, 4))
        {
            Insert(s.OrganizationId, "RECORD_VIEWED", at.AddMinutes(i), category: "CursorProbe");
        }

        var first = await ReadAsync(Explorer + "?category=CursorProbe&pageSize=2", s.Token(s.Reader));
        var cursor = Uri.EscapeDataString(first.Root.GetProperty("nextCursor").GetString()!);
        var appointed = await fixture.Api.PostAsync("/api/v1/organization/security-auditors", s.Token(s.Administrator), new { userId = s.Clerk.AuthorityUserId, reasonCode = "AuditCoverageAssignment", useDevelopmentStepUp = true });
        Assert.Equal(HttpStatusCode.NoContent, appointed.Status);

        var attempts = new[]
        {
            await ReadAsync(Explorer + "?category=CursorProbe&pageSize=2&cursor=" + cursor, s.Token(s.Clerk)), // another auditor
            await ReadAsync(Explorer + "?category=CursorProbe&pageSize=2&cursor=" + cursor, other.Token(other.Reader)), // another organization
            await ReadAsync(Explorer + "?category=CursorProbe&pageSize=3&cursor=" + cursor, s.Token(s.Reader)), // another filter
            await ReadAsync(Explorer + "?category=CursorProbe&pageSize=2&cursor=" + cursor[..^4] + "AAAA", s.Token(s.Reader)), // tampered
        };
        Assert.All(attempts, a => Assert.Equal((HttpStatusCode.BadRequest, "CURSOR_INVALID"), (a.Status, a.ErrorCode)));
    }

    /// <summary>AC-M4-2.5: with a second organization's rows present, only the session organization's rows are served; foreign ids are 404.</summary>
    [Fact]
    public async Task TheOrganizationPredicateHoldsWithASecondOrganizationPresent()
    {
        var s = await AuditorScenarioAsync("audx-tenant");
        var other = await AuditorScenarioAsync("audx-tenant-other");
        var at = new DateTimeOffset(2026, 4, 1, 8, 0, 0, TimeSpan.Zero);
        var mine = Insert(s.OrganizationId, "RECORD_VIEWED", at, category: "TenantProbe");
        var theirs = Insert(other.OrganizationId, "RECORD_VIEWED", at, category: "TenantProbe");

        var ids = (await AllPagesAsync(s, "category=TenantProbe")).Select(i => i.GetProperty("auditEventId").GetGuid()).ToList();
        Assert.Equal([mine], ids);
        var foreign = await ReadAsync($"{Explorer}/{theirs:D}", s.Token(s.Reader));
        var unknownId = Guid.CreateVersion7();
        var unknown = await ReadAsync($"{Explorer}/{unknownId:D}", s.Token(s.Reader));
        Assert.Equal((HttpStatusCode.NotFound, "NOT_FOUND"), (foreign.Status, foreign.ErrorCode));
        Assert.Equal((foreign.Status, foreign.ErrorCode), (unknown.Status, unknown.ErrorCode));
        Assert.Equal(ComparableProblem(unknown.Root), ComparableProblem(foreign.Root)); // same body apart from per-request identifiers

        // the delivery rows for the two misses are indistinguishable apart from the requested id
        var outcomes = fixture.Stack.AttackerSql($"SELECT (event_metadata_json - 'filterDigest')::text || '|' || event_outcome || '|' || event_description FROM audit_events WHERE tenant_id = '{s.OrganizationId:D}' AND event_action = 'AUDIT_EVENT_VIEWED' AND target_entity_id IN ('{theirs:D}', '{unknownId:D}') ORDER BY audit_sequence;")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(r => r.Replace(theirs.ToString("D"), "ID", StringComparison.Ordinal).Replace(unknownId.ToString("D"), "ID", StringComparison.Ordinal))
            .Select(r => System.Text.RegularExpressions.Regex.Replace(r, "\"authorityJournalRevision\": ?\\d+", "\"authorityJournalRevision\": N"))
            .ToList();
        Assert.Equal(2, outcomes.Count);
        Assert.Equal(outcomes[0], outcomes[1]);
        Assert.Contains("NotFound", outcomes[0], StringComparison.Ordinal);
        Assert.Equal("0", fixture.Stack.AttackerSql($"SELECT count(*) FROM audit_events WHERE tenant_id = '{other.OrganizationId:D}' AND event_action = 'AUDIT_EVENT_VIEWED';").Trim());
    }

    /// <summary>A problem body without the per-request identifiers, for comparing two refusals.</summary>
    private static string ComparableProblem(JsonElement root)
    {
        var properties = root.EnumerateObject()
            .Where(p => p.Name is not ("correlation_id" or "traceId" or "instance"))
            .OrderBy(p => p.Name, StringComparer.Ordinal)
            .Select(p => p.Name + "=" + p.Value.GetRawText());
        return string.Join("\n", properties);
    }

    /// <summary>AC-M4-2.4: sentinels in withheld and unknown keys, stored descriptions and a forged action never reach a list or detail response.</summary>
    [Fact]
    public async Task NoListOrDetailResponseContainsASentinel()
    {
        var s = await AuditorScenarioAsync("audx-sentinel");
        var at = new DateTimeOffset(2026, 5, 1, 8, 0, 0, TimeSpan.Zero);
        var ids = new List<Guid>
        {
            Insert(s.OrganizationId, "RECORD_UPDATED", at, new { contentRevision = 4, changedFieldKeys = new List<string> { "SENT-FIELD-KEY" }, detail = new { value = "SENT-CONFIDENTIAL-VALUE" } }, category: "SentinelProbe", description: "SENT-PUBLIC-VALUE"),
            Insert(s.OrganizationId, "FIELD_CONFIGURATION_CHANGED", at.AddSeconds(1), new { fieldKey = "SENT-FIELD-KEY", previousDisplayName = "SENT-FIELD-LABEL", newDisplayName = "SENT-FIELD-LABEL" }, category: "SentinelProbe"),
            Insert(s.OrganizationId, "FIELD_OPTIONS_CHANGED", at.AddSeconds(2), new { fieldKey = "SENT-FIELD-KEY", added = new List<string> { "SENT-OPTION-TEXT" } }, category: "SentinelProbe"),
            Insert(s.OrganizationId, "MEMBERSHIP_REQUESTED", at.AddSeconds(3), new { reason = "SENT-FREE-REASON", display_name_hint = "SENT-NAME-HINT" }, category: "SentinelProbe"),
            Insert(s.OrganizationId, "DOCUMENT_REMOVED", at.AddSeconds(4), new { reason = "SENT-FREE-REASON", fileName = "SENT-FILE.pdf", pin = "SENT-PIN-4471" }, category: "SentinelProbe"),
            Insert(s.OrganizationId, "FIELD_SEARCH_APPROVED", at.AddSeconds(5), new { operators = new List<string> { "equals" }, searchTerm = "SENT-SEARCH-TERM" }, category: "SentinelProbe"),
            Insert(s.OrganizationId, "FORGED_ACTION", at.AddSeconds(6), new { value = "SENT-CONFIDENTIAL-VALUE" }, category: "SentinelProbe", description: "SENT-PUBLIC-VALUE"),
        };

        var bodies = new List<string>();
        string? cursor = null;
        do
        {
            var page = await ReadAsync(Explorer + "?category=SentinelProbe&pageSize=3" + (cursor is null ? string.Empty : "&cursor=" + Uri.EscapeDataString(cursor)), s.Token(s.Reader));
            Assert.True(page.IsSuccess, page.Body);
            bodies.Add(page.Body);
            cursor = page.Root.GetProperty("nextCursor").GetString();
        }
        while (cursor is not null);

        foreach (var id in ids)
        {
            var detail = await ReadAsync($"{Explorer}/{id:D}", s.Token(s.Reader));
            Assert.True(detail.IsSuccess, detail.Body);
            bodies.Add(detail.Body);
        }

        var all = string.Join('\n', bodies);
        Assert.All(Sentinels, sentinel => Assert.DoesNotContain(sentinel, all, StringComparison.Ordinal));
        var forged = await ReadAsync($"{Explorer}/{ids[^1]:D}", s.Token(s.Reader));
        Assert.False(forged.Root.GetProperty("event").GetProperty("recognized").GetBoolean());
        Assert.Empty(forged.Root.GetProperty("metadata").EnumerateArray());
    }

    /// <summary>AC-M4-2.2: each served page and detail writes a business delivery row with the Authority journal revision.</summary>
    [Fact]
    public async Task DeliveryOutcomesAreRecordedForPagesDetailsAndNotFound()
    {
        var s = await AuditorScenarioAsync("audx-delivery");
        var page = await ReadAsync(Explorer + "?pageSize=1", s.Token(s.Reader));
        Assert.True(page.IsSuccess, page.Body);
        var eventId = page.Root.GetProperty("items")[0].GetProperty("auditEventId").GetGuid();
        Assert.True((await ReadAsync($"{Explorer}/{eventId:D}", s.Token(s.Reader))).IsSuccess);
        Assert.Equal(HttpStatusCode.NotFound, (await ReadAsync($"{Explorer}/{Guid.CreateVersion7():D}", s.Token(s.Reader))).Status);

        var rows = fixture.Stack.AttackerSql($"SELECT event_action || '|' || (event_metadata_json->>'deliveryOutcome') || '|' || (event_metadata_json->>'authorityJournalRevision') FROM audit_events WHERE tenant_id = '{s.OrganizationId:D}' AND event_action IN ('AUDIT_EVENTS_VIEWED', 'AUDIT_EVENT_VIEWED') ORDER BY audit_sequence;")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        Assert.Equal(3, rows.Length);
        Assert.StartsWith("AUDIT_EVENTS_VIEWED|Delivered|", rows[0], StringComparison.Ordinal);
        Assert.StartsWith("AUDIT_EVENT_VIEWED|Delivered|", rows[1], StringComparison.Ordinal);
        Assert.StartsWith("AUDIT_EVENT_VIEWED|NotFound|", rows[2], StringComparison.Ordinal);
        Assert.All(rows, r => Assert.True(long.Parse(r.Split('|')[2], CultureInfo.InvariantCulture) > 0));
    }
}
