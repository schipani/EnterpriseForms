using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using EnterpriseForms.SecurityTests.Harness;
using EnterpriseForms.StageBTests.Harness;
using Xunit;

namespace EnterpriseForms.StageBTests.Tests;

/// <summary>
/// F-C7-01 (C-65) on the real stack: record creation interrupted at every checkpoint (Testing-only fault header, the api
/// of the security-test profile only) completes on a retry with the same idempotency key — one record, one accepted
/// content revision, the submitted values (including encrypted ones); a different request with the same key is refused;
/// later content is never replaced; concurrent duplicates yield one record; hostile business-database edits of the
/// fingerprint or of the staged candidate fail closed instead of being repaired.
/// </summary>
[Collection(StageBStackCollectionDefinition.Name)]
public sealed class RecordCreationIdempotencyTests(StageBFixture fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static TheoryData<string> Checkpoints => new()
    {
        "record-create:after-register",
        "record-create:after-projection",
        "content:after-prepare",
        "content:after-stage",
        "record-create:after-commit",
    };

    private sealed record Result(HttpStatusCode Status, string Body)
    {
        public JsonElement Root => JsonDocument.Parse(Body).RootElement;

        public string? Title => Body.Length > 0 && Root.TryGetProperty("title", out var title) ? title.GetString() : null;
    }

    private static async Task<Result> CreateAsync(StageBScenario s, string key, Dictionary<string, string> values, string? fault = null)
    {
        using var client = new HttpClient { BaseAddress = new Uri(StageBFixture.ApiBaseAddress), Timeout = TimeSpan.FromMinutes(2) };
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/form-definitions/{s.AccusedFormId:D}/records") { Content = JsonContent.Create(new { values, ownerTeamId = (Guid?)null }) };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", s.Token(s.Clerk));
        request.Headers.Add("Idempotency-Key", key);
        if (fault is not null)
        {
            request.Headers.Add("X-Testing-Fault", fault);
        }

        using var response = await client.SendAsync(request, Ct);
        return new Result(response.StatusCode, await response.Content.ReadAsStringAsync(Ct));
    }

    /// <summary>
    /// A client's retry loop: the same request with the same key, resent while the Authority answers the transient
    /// AUTHORITY_NOT_READY (archive receipt pending after the previous change). Never resent on an injected fault.
    /// </summary>
    private static async Task<Result> RetryAsync(StageBScenario s, string key, Dictionary<string, string> values)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(60);
        while (true)
        {
            var result = await CreateAsync(s, key, values);
            if (result.Status != HttpStatusCode.ServiceUnavailable || result.Title != "AUTHORITY_NOT_READY" || DateTime.UtcNow > deadline)
            {
                return result;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(500), Ct);
        }
    }

    /// <summary>The business API's derivation of a creation's record id from its idempotency key (CreateFormDefinitionHandler.DeterministicId).</summary>
    private static Guid RecordIdOf(string key)
    {
        var hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes("record:" + key));
        hash[6] = (byte)((hash[6] & 0x0F) | 0x80);
        hash[8] = (byte)((hash[8] & 0x3F) | 0x80);
        return new Guid(hash.AsSpan(0, 16), bigEndian: true);
    }

    private static string NewKey() => "f-c7-01-" + Guid.CreateVersion7().ToString("N");

    private string AcceptedRevisions(Guid record) =>
        fixture.Stack.AuthorityObserverSql($"SELECT coalesce(string_agg(committed_content_revision::text, ',' ORDER BY committed_content_revision), '') FROM record_manifest_commits WHERE form_record_id = '{record:D}';").Trim();

    private static Dictionary<string, string> Values(JsonElement projection) =>
        projection.GetProperty("record").GetProperty("values").EnumerateArray().ToDictionary(v => v.GetProperty("fieldKey").GetString()!, v => v.GetProperty("stringValue").GetString()!);

    private async Task<StageBScenario> ScenarioAsync(string name)
    {
        var s = new StageBScenario(fixture, name);
        await s.BootstrapAsync();
        await s.BuildAccusedWorkspaceAsync();
        return s;
    }

    [Theory]
    [MemberData(nameof(Checkpoints))]
    public async Task ACreationInterruptedAtAnyCheckpointCompletesOnRetryAsExactlyOneRecord(string checkpoint)
    {
        var s = await ScenarioAsync("fc701-" + checkpoint.Replace(':', '-'));
        var key = NewKey();
        var values = StageBScenario.AccusedValues("Retry", "Checkpoint");

        var interrupted = await CreateAsync(s, key, values, checkpoint);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, interrupted.Status);
        Assert.Equal("TESTING_FAULT_INJECTED", interrupted.Title);

        var retried = await RetryAsync(s, key, values);
        Assert.True(retried.Status == HttpStatusCode.Created, retried.Body);
        var record = retried.Root.GetProperty("formRecordId").GetGuid();
        Assert.Equal(1, retried.Root.GetProperty("contentRevision").GetInt64());

        // The same request again: the same record and revision, never a second accepted version.
        var again = await RetryAsync(s, key, values);
        Assert.True(again.Status == HttpStatusCode.Created, again.Body);
        Assert.Equal(record, again.Root.GetProperty("formRecordId").GetGuid());
        Assert.Equal(1, again.Root.GetProperty("contentRevision").GetInt64());
        Assert.Equal("1", AcceptedRevisions(record));

        // The record holds exactly the submitted values, verified (given_name is encrypted by default).
        var projection = (await s.WaitProjectionAsync(s.Clerk, record)).Root;
        var stored = Values(projection);
        Assert.Equal("Retry", stored["given_name"]);
        Assert.Equal("Checkpoint", stored["family_name"]);
        Assert.Equal("1990-02-28", stored["date_of_birth"]);
        var listed = (await fixture.Api.GetAsync("/api/v1/form-records", s.Token(s.Clerk))).Root.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("formRecordId").GetGuid()).ToList();
        Assert.Equal([record], listed); // the scenario's own organization: exactly one record, no orphan

        if (checkpoint == "content:after-prepare")
        {
            // The candidate prepared by the interrupted attempt was never staged; it stays Prepared until it expires and
            // can never be accepted afterwards (the head moved). Exactly one operation of the record is Committed.
            var states = fixture.Stack.AuthorityObserverSql($"SELECT string_agg(operation_state_name, ',' ORDER BY operation_state_name) FROM content_change_operations WHERE form_record_id = '{record:D}';").Trim();
            Assert.Equal("Committed,Prepared", states);
        }
    }

    [Fact]
    public async Task ADifferentRequestWithTheSameKeyIsAConflictBeforeAndAfterCompletion()
    {
        var s = await ScenarioAsync("fc701-conflict");
        var key = NewKey();
        var values = StageBScenario.AccusedValues("Same", "Key");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await CreateAsync(s, key, values, "content:after-stage")).Status);

        var different = new Dictionary<string, string>(values) { ["given_name"] = "Other" };
        var conflict = await RetryAsync(s, key, different);
        Assert.Equal(HttpStatusCode.Conflict, conflict.Status);
        Assert.Equal("IDEMPOTENCY_CONFLICT", conflict.Title);
        var withoutOne = new Dictionary<string, string>(values);
        withoutOne.Remove("case_status");
        Assert.Equal("IDEMPOTENCY_CONFLICT", (await RetryAsync(s, key, withoutOne)).Title);

        var completed = await RetryAsync(s, key, values);
        Assert.True(completed.Status == HttpStatusCode.Created, completed.Body);
        var record = completed.Root.GetProperty("formRecordId").GetGuid();
        Assert.Equal("IDEMPOTENCY_CONFLICT", (await RetryAsync(s, key, different)).Title);
        Assert.Equal("1", AcceptedRevisions(record));
        Assert.Equal("Same", Values((await s.WaitProjectionAsync(s.Clerk, record)).Root)["given_name"]);
    }

    [Fact]
    public async Task ARetryAfterLaterEditsReturnsTheRecordAndNeverReplacesItsContent()
    {
        var s = await ScenarioAsync("fc701-later-edit");
        var key = NewKey();
        var values = StageBScenario.AccusedValues("First", "Version");
        var created = await RetryAsync(s, key, values);
        Assert.True(created.Status == HttpStatusCode.Created, created.Body);
        var record = created.Root.GetProperty("formRecordId").GetGuid();
        await s.WaitProjectionAsync(s.Clerk, record);
        var edit = await fixture.Api.PatchAsync($"/api/v1/form-records/{record:D}", s.Token(s.Clerk), new { expectedContentRevision = 1, values = new Dictionary<string, string> { ["family_name"] = "Edited" } });
        Assert.True(edit.IsSuccess, edit.Body);
        await s.WaitProjectionAsync(s.Clerk, record);

        var retried = await RetryAsync(s, key, values);
        Assert.True(retried.Status == HttpStatusCode.Created, retried.Body);
        Assert.Equal(record, retried.Root.GetProperty("formRecordId").GetGuid());
        Assert.Equal(2, retried.Root.GetProperty("contentRevision").GetInt64());
        Assert.Equal("1,2", AcceptedRevisions(record));
        Assert.Equal("Edited", Values((await s.WaitProjectionAsync(s.Clerk, record)).Root)["family_name"]);
    }

    [Fact]
    public async Task ConcurrentIdenticalRequestsCreateOneRecord()
    {
        var s = await ScenarioAsync("fc701-concurrent");
        var key = NewKey();
        var values = StageBScenario.AccusedValues("Concurrent", "Duplicate");
        var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => CreateAsync(s, key, values)));

        // Each duplicate either succeeds with the same record or fails transiently; a retry of any failed one succeeds.
        foreach (var result in results.Where(r => r.Status != HttpStatusCode.Created))
        {
            Assert.True((int)result.Status is 409 or 503, result.Body);
        }

        var final = await RetryAsync(s, key, values);
        Assert.True(final.Status == HttpStatusCode.Created, final.Body);
        var record = final.Root.GetProperty("formRecordId").GetGuid();
        Assert.All(results.Where(r => r.Status == HttpStatusCode.Created), r => Assert.Equal(record, r.Root.GetProperty("formRecordId").GetGuid()));
        Assert.Contains(results, r => r.Status == HttpStatusCode.Created);
        Assert.Equal("1", AcceptedRevisions(record));
    }

    [Fact]
    public async Task ATamperedFingerprintIsNeverTreatedAsTheSameRequest()
    {
        var s = await ScenarioAsync("fc701-tamper-fingerprint");
        var key = NewKey();
        var values = StageBScenario.AccusedValues("Tamper", "Fingerprint");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await CreateAsync(s, key, values, "record-create:after-register")).Status);

        // The hostile business DBA rewrites the stored fingerprint: the retry cannot be proven to be the same request.
        fixture.Stack.AttackerSql("UPDATE form_record_creation_attempts SET creation_request_fingerprint = decode(repeat('00', 32), 'hex') WHERE created_on > now() - interval '5 minutes' AND form_record_id IN (SELECT form_record_id FROM form_record_creation_attempts ORDER BY created_on DESC LIMIT 1);");
        var retried = await RetryAsync(s, key, values);
        Assert.Equal(HttpStatusCode.Conflict, retried.Status);
        Assert.Equal("IDEMPOTENCY_CONFLICT", retried.Title);
    }

    [Fact]
    public async Task ATamperedStagedCandidateIsRefusedByTheAuthorityAndNotRepaired()
    {
        var s = await ScenarioAsync("fc701-tamper-staged");
        var key = NewKey();
        var values = StageBScenario.AccusedValues("Tamper", "Staged");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await CreateAsync(s, key, values, "content:after-stage")).Status);

        // The staged (not yet accepted) plaintext date row of this creation is altered by the hostile business DBA.
        var changed = fixture.Stack.AttackerSql("UPDATE field_date_values SET date_value = DATE '1970-01-01' WHERE field_date_value_id = (SELECT v.field_date_value_id FROM field_date_values v JOIN form_record_revisions h ON h.form_record_id = v.form_record_id AND h.content_change_operation_id = v.content_change_operation_id WHERE h.candidate_state = 'Staged' AND h.staged_on > now() - interval '5 minutes' ORDER BY h.staged_on DESC LIMIT 1) RETURNING field_date_value_id;").Trim();
        Assert.False(string.IsNullOrEmpty(changed));

        var retried = await RetryAsync(s, key, values);
        Assert.Equal(HttpStatusCode.Conflict, retried.Status);
        Assert.Equal("CANDIDATE_MISMATCH", retried.Title);
        var record = Guid.Parse(fixture.Stack.AttackerSql($"SELECT form_record_id FROM field_date_values WHERE field_date_value_id = '{changed.Split('\n')[0].Trim()}';").Trim());
        Assert.Equal(string.Empty, AcceptedRevisions(record)); // nothing accepted; the tampered rows were not "repaired" into the record
    }

    [Fact]
    public async Task ABusinessDatabaseOutageDuringCreationIsRecoverable()
    {
        var s = await ScenarioAsync("fc701-business-outage");
        var key = NewKey();
        var values = StageBScenario.AccusedValues("Business", "Outage");
        fixture.Stack.Pause("business-db");
        Result failed;
        try
        {
            failed = await CreateAsync(s, key, values);
        }
        finally
        {
            fixture.Stack.Unpause("business-db");
        }

        Assert.True((int)failed.Status >= 500, failed.Body);
        Assert.NotEqual("IDEMPOTENCY_CONFLICT", failed.Title);
        var retried = await RetryAsync(s, key, values);
        Assert.True(retried.Status == HttpStatusCode.Created, retried.Body);
        Assert.Equal(RecordIdOf(key), retried.Root.GetProperty("formRecordId").GetGuid());
        Assert.Equal("1", AcceptedRevisions(RecordIdOf(key)));
    }

    [Fact]
    public async Task AnAuthorityOutageDuringCreationIsRecoverable()
    {
        var s = await ScenarioAsync("fc701-authority-outage");
        var key = NewKey();
        var values = StageBScenario.AccusedValues("Authority", "Outage");
        fixture.Stack.Pause("authority");
        Result failed;
        try
        {
            failed = await CreateAsync(s, key, values);
        }
        finally
        {
            fixture.Stack.Unpause("authority");
        }

        Assert.Equal(HttpStatusCode.ServiceUnavailable, failed.Status);
        Assert.True(failed.Root.TryGetProperty("retryable", out var retryable) && retryable.GetBoolean(), failed.Body); // recoverable, not a conflict
        await fixture.Authority.WaitReadyAsync(TimeSpan.FromSeconds(90));
        var retried = await RetryAsync(s, key, values);
        Assert.True(retried.Status == HttpStatusCode.Created, retried.Body);
        Assert.Equal("1", AcceptedRevisions(RecordIdOf(key)));
    }

    [Fact]
    public async Task AnApiRestartBetweenStagesLosesNothingTheRetryNeeds()
    {
        var s = await ScenarioAsync("fc701-api-restart");
        var key = NewKey();
        var values = StageBScenario.AccusedValues("Api", "Restart");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await CreateAsync(s, key, values, "content:after-stage")).Status);
        fixture.Stack.Compose("restart api", 300);
        await WaitApiAsync();

        var retried = await RetryAsync(s, key, values);
        Assert.True(retried.Status == HttpStatusCode.Created, retried.Body);
        Assert.Equal("1", AcceptedRevisions(RecordIdOf(key)));
        Assert.Equal("Api", Values((await s.WaitProjectionAsync(s.Clerk, RecordIdOf(key))).Root)["given_name"]);
    }

    [Fact]
    public async Task ConcurrentConflictingRequestsYieldOneRecordAndConflictsNeverAWrongValue()
    {
        var s = await ScenarioAsync("fc701-concurrent-conflict");
        var key = NewKey();
        var first = StageBScenario.AccusedValues("Left", "Racer");
        var second = StageBScenario.AccusedValues("Right", "Racer");
        var results = await Task.WhenAll(CreateAsync(s, key, first), CreateAsync(s, key, second), CreateAsync(s, key, first), CreateAsync(s, key, second));
        foreach (var result in results.Where(r => r.Status != HttpStatusCode.Created))
        {
            Assert.True((int)result.Status is 409 or 503, result.Body);
        }

        var record = RecordIdOf(key);
        var winner = (await RetryAsync(s, key, first)).Status == HttpStatusCode.Created ? "Left" : "Right";
        var loserValues = winner == "Left" ? second : first;
        Assert.Equal("IDEMPOTENCY_CONFLICT", (await RetryAsync(s, key, loserValues)).Title);
        Assert.Equal("1", AcceptedRevisions(record));
        Assert.Equal(winner, Values((await s.WaitProjectionAsync(s.Clerk, record)).Root)["given_name"]);
    }

    [Fact]
    public async Task MissingBusinessDataAfterAPartialCreationIsNeverTrustedAndTheRetryStillCompletes()
    {
        var s = await ScenarioAsync("fc701-missing-business-data");
        var key = NewKey();
        var record = RecordIdOf(key);
        var values = StageBScenario.AccusedValues("Missing", "Rows");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await CreateAsync(s, key, values, "content:after-stage")).Status);

        // The staged candidate and the local record row disappear (a DBA or a failed restore). Nothing of that is re-used:
        // the retry stages a fresh candidate from its own request, and the Authority accepts exactly one first revision.
        fixture.Stack.AttackerSql($"DELETE FROM field_date_values WHERE form_record_id = '{record:D}'; DELETE FROM field_encrypted_values WHERE form_record_id = '{record:D}'; DELETE FROM field_string_values WHERE form_record_id = '{record:D}'; DELETE FROM field_boolean_values WHERE form_record_id = '{record:D}'; DELETE FROM field_option_values WHERE form_record_id = '{record:D}'; DELETE FROM field_date_time_values WHERE form_record_id = '{record:D}'; DELETE FROM field_guid_values WHERE form_record_id = '{record:D}'; DELETE FROM form_record_revisions WHERE form_record_id = '{record:D}'; DELETE FROM audit_events WHERE form_record_id = '{record:D}'; DELETE FROM form_records WHERE form_record_id = '{record:D}';");
        var retried = await RetryAsync(s, key, values);
        Assert.True(retried.Status == HttpStatusCode.Created, retried.Body);
        Assert.Equal("1", AcceptedRevisions(record));
        Assert.Equal("Missing", Values((await s.WaitProjectionAsync(s.Clerk, record)).Root)["given_name"]);
    }

    [Fact]
    public async Task AnotherUserReusingTheKeyLearnsNothingAndGetsNoAccess()
    {
        var s = await ScenarioAsync("fc701-other-user");
        var key = NewKey();
        var values = StageBScenario.AccusedValues("Owner", "Only");
        var created = await RetryAsync(s, key, values);
        Assert.True(created.Status == HttpStatusCode.Created, created.Body);

        using var client = new HttpClient { BaseAddress = new Uri(StageBFixture.ApiBaseAddress) };
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/form-definitions/{s.AccusedFormId:D}/records") { Content = JsonContent.Create(new { values, ownerTeamId = (Guid?)null }) };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", s.Token(s.Designer));
        request.Headers.Add("Idempotency-Key", key);
        using var response = await client.SendAsync(request, Ct);
        var body = await response.Content.ReadAsStringAsync(Ct);
        Assert.True(response.StatusCode is HttpStatusCode.Conflict or HttpStatusCode.Forbidden, body);
        Assert.DoesNotContain(RecordIdOf(key).ToString("D"), body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Owner", body, StringComparison.Ordinal);
        Assert.True((await fixture.Api.GetAsync($"/api/v1/form-records/{RecordIdOf(key):D}/projection", s.Token(s.Designer))).Status is HttpStatusCode.NotFound or HttpStatusCode.Forbidden);
    }

    private static async Task WaitApiAsync()
    {
        using var client = new HttpClient { BaseAddress = new Uri(StageBFixture.ApiBaseAddress), Timeout = TimeSpan.FromSeconds(5) };
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(120);
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                if ((await client.GetAsync("/health/ready", Ct)).IsSuccessStatusCode)
                {
                    return;
                }
            }
            catch (HttpRequestException)
            {
            }
            catch (TaskCanceledException)
            {
            }

            await Task.Delay(TimeSpan.FromSeconds(1), Ct);
        }

        throw new InvalidOperationException("api did not become ready after the restart");
    }
}
