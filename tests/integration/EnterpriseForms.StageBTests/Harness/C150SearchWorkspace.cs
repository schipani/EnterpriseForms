using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using EnterpriseForms.SecurityTests.Harness;
using Xunit;

namespace EnterpriseForms.StageBTests.Harness;

/// <summary>
/// C-150 focused stack tests: one synthetic organization with a form whose Integer field <c>exhibit_count</c> is approved for search
/// (<c>equals</c>, <c>between</c>), <c>field_searcher</c> granted to the Clerk (owner) and the Reader, records created by the Clerk with
/// counts 0..n-1, share and revoke of a record's count to the Reader, Authority sessions, and business-API indexed searches with a
/// caller-chosen correlation id (so the business API's R56-A retry log lines can be attributed to one search).
/// </summary>
public sealed class C150SearchWorkspace
{
    private readonly Dictionary<Guid, Guid?> shareOf = [];

    private C150SearchWorkspace(StageBFixture fixture, StageBScenario scenario, Guid form, Guid count, List<Guid> records)
    {
        Fixture = fixture;
        S = scenario;
        Form = form;
        Count = count;
        Records = records;
    }

    public StageBFixture Fixture { get; }

    public StageBScenario S { get; }

    public Guid Form { get; }

    public Guid Count { get; }

    public IReadOnlyList<Guid> Records { get; }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static async Task<C150SearchWorkspace> CreateAsync(StageBFixture fixture, string name, int recordCount)
    {
        ArgumentNullException.ThrowIfNull(fixture);
        var s = new StageBScenario(fixture, name);
        await s.BootstrapAsync();
        var current = await fixture.Api.GetAsync("/api/v1/organization/security-policy", s.Token(s.Designer));
        Assert.True((await fixture.Api.SendAsync(HttpMethod.Put, "/api/v1/organization/security-policy", s.Token(s.Designer), new { minimumFieldClassification = "Internal", plaintextInternalFieldsPermitted = true, plaintextDowngradePermitted = false, plaintextSearchPermitted = true, policyStatementText = "C-150 (synthetic).", expectedPolicyRevision = current.Root.GetProperty("securityPolicyRevision").GetInt64() }, Guid.NewGuid().ToString("N"))).IsSuccess);
        var form = await s.CreateFormAsync("C-150 " + s.Name, s.Clerk);
        var added = await fixture.Api.PostAsync($"/api/v1/form-definitions/{form:D}/fields", s.Token(s.Designer), new { fieldKey = "exhibit_count", fieldDisplayName = "Count", fieldDescription = string.Empty, fieldDataType = "Integer", fieldClassification = "Internal", fieldProtectionMode = "Standard" });
        Assert.Equal(HttpStatusCode.Created, added.Status);
        var count = added.Root.GetProperty("fieldDefinitionId").GetGuid();
        await s.SaveDraftAsync(form, StageBScenario.Layout("C-150", StageBScenario.Section("main", "Main", StageBScenario.Field(count, "number", "Count", 4))));
        Assert.True((await s.PublishAsync(form, "v1")).GetProperty("authorityRegistered").GetBoolean());
        Assert.True((await fixture.Api.PostAsync($"/api/v1/form-definitions/{form:D}/fields/{count:D}/search-approval", s.Token(s.Administrator), new { operators = new[] { "equals", "between" }, approvalReference = "C-150", useDevelopmentStepUp = true })).IsSuccess);
        Assert.Equal(HttpStatusCode.NoContent, (await fixture.Api.SendAsync(HttpMethod.Put, $"/api/v1/form-definitions/{form:D}/field-searchers", s.Token(s.Administrator), new { grantUserIds = new[] { s.Reader.AuthorityUserId, s.Clerk.AuthorityUserId }, grantTeamIds = Array.Empty<Guid>(), revokeUserIds = Array.Empty<Guid>(), revokeTeamIds = Array.Empty<Guid>() }, Guid.NewGuid().ToString("N"))).Status);
        var records = new List<Guid>();
        for (var i = 0; i < recordCount; i++)
        {
            var created = await RetryingAsync(() => fixture.Api.PostAsync($"/api/v1/form-definitions/{form:D}/records", s.Token(s.Clerk), new { values = new Dictionary<string, string> { ["exhibit_count"] = i.ToString(CultureInfo.InvariantCulture) } }, "c150-" + Guid.CreateVersion7().ToString("N")));
            Assert.True(created.IsSuccess, created.Body);
            records.Add(created.Root.GetProperty("formRecordId").GetGuid());
        }

        if (records.Count > 0)
        {
            await s.WaitProjectionAsync(s.Clerk, records[^1]);
        }

        await s.WaitGateReadyAsync(TimeSpan.FromSeconds(60));
        var deadline = DateTime.UtcNow.AddSeconds(300);
        while (fixture.Stack.AuthorityObserverSql($"SELECT count(*) FROM authority_search.search_field_coverages WHERE organization_id = '{s.OrganizationId:D}' AND coverage_state_name = 'Complete';").Trim().Split('\n')[0].Trim() != "1")
        {
            Assert.True(DateTime.UtcNow < deadline, "coverage not Complete");
            await Task.Delay(1000, Ct);
        }

        return new C150SearchWorkspace(fixture, s, form, count, records);
    }

    /// <summary>Retries the transient refusals of a write (a closed gate, a pending command, a stale revision), as the SPA does.</summary>
    public static async Task<HttpResult> RetryingAsync(Func<Task<HttpResult>> call)
    {
        ArgumentNullException.ThrowIfNull(call);
        for (var attempt = 0; ; attempt++)
        {
            var result = await call();
            if (result.IsSuccess || attempt >= 60 || result.ErrorCode is not ("AUTHORITY_NOT_READY" or "COMMAND_PENDING" or "STALE_AUTHORIZATION_REVISION"))
            {
                return result;
            }

            await Task.Delay(250, Ct);
        }
    }

    public bool IsShared(Guid record) => shareOf.TryGetValue(record, out var share) && share is not null;

    /// <summary>Shares the record's count to the Reader, or revokes the share; returns the window in which the command committed.</summary>
    public async Task<(bool SharedAfter, DateTimeOffset Requested, DateTimeOffset Confirmed)> ToggleAsync(Guid record)
    {
        var requested = DateTimeOffset.UtcNow;
        if (shareOf.TryGetValue(record, out var share) && share is { } id)
        {
            var revoked = await RetryingAsync(() => Fixture.Api.PostAsync($"/api/v1/form-records/{record:D}/shares/{id:D}/revoke", S.Token(S.Clerk), new { }));
            Assert.True(revoked.IsSuccess, revoked.Body);
            shareOf[record] = null;
            return (false, requested, DateTimeOffset.UtcNow);
        }

        var shared = await RetryingAsync(() => Fixture.Api.PostAsync($"/api/v1/form-records/{record:D}/shares", S.Token(S.Clerk), new { recipientUserId = S.Reader.AuthorityUserId, recipientTeamId = (Guid?)null, recipientRole = "Reader", fieldKeys = new[] { "exhibit_count" } }));
        Assert.True(shared.IsSuccess, shared.Body);
        shareOf[record] = shared.Root.GetProperty("recordShareId").GetGuid();
        return (true, requested, DateTimeOffset.UtcNow);
    }

    /// <summary>An Authority session for the user (the indexed engine binds results to a browser session; tests name it with a Testing header).</summary>
    public async Task<string> SessionAsync(TestUser user)
    {
        var digest = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var created = await Fixture.Authority.SendAsync(HttpMethod.Post, "/internal/v1/sessions", TestIdentity.WorkloadToken(), new { organization_id = S.OrganizationId, session_digest_base64 = digest, absolute_expires_on = DateTimeOffset.UtcNow.AddHours(2) }, S.Token(user));
        Assert.True(created.IsSuccess, created.Body);
        return digest;
    }

    public sealed record SearchOutcome(int Status, string? Code, List<Guid>? Ids, Guid CorrelationId, DateTimeOffset Started, DateTimeOffset Ended, double Milliseconds);

    /// <summary>One business-API indexed search of <c>exhibit_count between lower and upper</c> (or <c>equals</c> when they are equal).</summary>
    public async Task<SearchOutcome> SearchAsync(HttpClient http, TestUser user, string digest, long lower, long upper)
    {
        ArgumentNullException.ThrowIfNull(http);
        var condition = lower == upper
            ? (object)new { fieldDefinitionId = Count, @operator = "equals", valueType = "Integer", value = lower.ToString(CultureInfo.InvariantCulture) }
            : new { fieldDefinitionId = Count, @operator = "between", valueType = "Integer", lowerBound = lower.ToString(CultureInfo.InvariantCulture), upperBound = upper.ToString(CultureInfo.InvariantCulture) };
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/form-records/field-search") { Content = JsonContent.Create(new { formDefinitionId = Form, conditions = new[] { condition }, pageSize = 100 }) };
        var correlation = Guid.CreateVersion7();
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", S.Token(user));
        request.Headers.Add("X-Testing-Session-Digest", digest);
        request.Headers.Add("X-Correlation-Id", correlation.ToString("D"));
        var started = DateTimeOffset.UtcNow;
        var clock = Stopwatch.StartNew();
        using var response = await http.SendAsync(request, CancellationToken.None);
        var body = await response.Content.ReadAsStringAsync(CancellationToken.None);
        var elapsed = clock.Elapsed.TotalMilliseconds;
        using var doc = JsonDocument.Parse(body.Length == 0 ? "{}" : body);
        var root = doc.RootElement;
        var code = root.TryGetProperty("errorCode", out var e) && e.ValueKind == JsonValueKind.String ? e.GetString() : root.TryGetProperty("title", out var t) ? t.GetString() : null;
        var ids = response.IsSuccessStatusCode ? root.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("formRecordId").GetGuid()).ToList() : null;
        return new SearchOutcome((int)response.StatusCode, response.IsSuccessStatusCode ? null : code, ids, correlation, started, DateTimeOffset.UtcNow, elapsed);
    }

    /// <summary>Polls the organization gate every <paramref name="pollMilliseconds"/> until Ready; the elapsed time from the call.</summary>
    public async Task<double> TimeToGateReadyAsync(int pollMilliseconds = 5, int timeoutSeconds = 30)
    {
        var clock = Stopwatch.StartNew();
        while (clock.Elapsed < TimeSpan.FromSeconds(timeoutSeconds))
        {
            var gate = await Fixture.Authority.GateAsync(S.OrganizationId);
            if (gate.GetProperty("projection_gate_state").GetString() == "Ready")
            {
                return clock.Elapsed.TotalMilliseconds;
            }

            await Task.Delay(pollMilliseconds, Ct);
        }

        Assert.Fail("the organization gate did not reopen");
        return double.NaN;
    }

    /// <summary>The Authority's R56-B wake-up counters (Testing endpoint).</summary>
    public async Task<JsonElement> WorkCountersAsync()
    {
        var result = await Fixture.Authority.SendAsync(HttpMethod.Get, "/internal/v1/testing/authority-work", TestIdentity.WorkloadToken(), null);
        Assert.True(result.IsSuccess, result.Body);
        return result.Root.Clone();
    }
}
