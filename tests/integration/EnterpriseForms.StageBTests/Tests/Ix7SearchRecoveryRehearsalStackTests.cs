using System.Diagnostics;
using System.Net;
using System.Text.Json;
using EnterpriseForms.SecurityTests.Harness;
using EnterpriseForms.StageBTests.Harness;
using Xunit;

namespace EnterpriseForms.StageBTests.Tests;

/// <summary>
/// IX-7 local recovery rehearsal (design §IX-7 "DR rehearsal including the projection rebuild after recovery"), on the security-test
/// stack. It rehearses the part of recovery that the search projections own; it is <b>not</b> the G14 coordinated recovery (AuthorityDb
/// point-in-time restore with journal replay, SpiceDB rebuild, key and archive recovery), which is not implemented, and it is not the
/// staging DR exercise.
/// <para>
/// Scenario: the organization's Tier 1 index (<c>authority_search</c>) and Tier 2 projection (<c>authority_search_access</c>) are restored
/// from a backup taken before a revocation and a new record (what a projection restore from an older backup leaves behind; the canonical
/// journal, heads and SpiceDB are current). Required: the revoked share is never served by any engine (searches and listings decide
/// from fresh SpiceDB, and Tier 2 is never reported verified while holding the revoked grant); the restored index is detected and
/// quarantined (Tier 1) or caught up from the journal and re-verified (Tier 2), never silently trusted; the administrator rebuild restores
/// Tier 1 and the Tier 2 rebuild plus a complete M2 pass restores Tier 2. Times are recorded as local rehearsal evidence, not as an RTO.
/// </para>
/// Synthetic data only; a focused development test class (C-131), not a Stage B group run.
/// </summary>
[Collection(StageBStackCollectionDefinition.Name)]
public sealed class Ix7SearchRecoveryRehearsalStackTests(StageBFixture fixture)
{
    private const string Prefix = "/internal/v1/testing/search-access";

    /// <summary>Restored tables: everything organization-scoped except result snapshots (session-bound, never restored) and shadow evidence.</summary>
    private static readonly string[] RestoredTables =
    [
        "authority_search.search_projection_states", "authority_search.search_form_states", "authority_search.search_field_coverages", "authority_search.search_record_entries",
        "authority_search.search_record_field_anomalies", "authority_search.search_string_postings", "authority_search.search_integer_postings", "authority_search.search_decimal_postings",
        "authority_search.search_date_postings", "authority_search.search_date_time_postings", "authority_search.search_boolean_postings", "authority_search.search_option_postings",
        "authority_search_access.search_access_checkpoints", "authority_search_access.search_access_edges", "authority_search_access.search_access_record_grants",
        "authority_search_access.search_access_field_item_grants", "authority_search_access.search_access_value_postings", "authority_search_access.search_access_verification_states",
    ];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    private string Scalar(string sql) => fixture.Stack.AuthorityObserverSql(sql).Trim().Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim() ?? string.Empty;

    private async Task<JsonElement> AuthorityAsync(HttpMethod method, string path, object? body = null)
    {
        var result = await fixture.Authority.SendAsync(method, Prefix + path, TestIdentity.WorkloadToken(), body);
        Assert.True(result.IsSuccess, path + ": " + result.Body);
        return result.Root.Clone();
    }

    private string Tier1(Guid org) => Scalar($"SELECT projection_status_name || '|' || coalesce(projection_status_reason_code, '') FROM authority_search.search_projection_states WHERE organization_id = '{org:D}';");

    private bool ReaderGrantPresent(Guid org, Guid record, Guid reader) =>
        Scalar($"SELECT count(*) FROM authority_search_access.search_access_record_grants WHERE organization_id = '{org:D}' AND form_record_id = '{record:D}' AND access_principal_entity_id = '{reader:D}';") != "0";

    private async Task<(string Status, bool Verified)> Tier2Async(Guid org) =>
        ((await AuthorityAsync(HttpMethod.Get, $"/{org:D}/status")).GetProperty("status_name").GetString()!, (await AuthorityAsync(HttpMethod.Get, $"/{org:D}/verification")).GetProperty("verified").GetBoolean());

    private static string BackupSchema(Guid org) => "ix7_rehearsal_" + org.ToString("N")[^12..];

    private void Backup(Guid org)
    {
        var schema = BackupSchema(org);
        var statements = string.Join('\n', RestoredTables.Select(t => $"CREATE TABLE {schema}.{t.Replace('.', '_')} AS SELECT * FROM {t} WHERE organization_id = '{org:D}';"));
        fixture.Stack.AuthorityAdministratorSql($"DROP SCHEMA IF EXISTS {schema} CASCADE; CREATE SCHEMA {schema};\n{statements}");
    }

    /// <summary>The restore, as pg_restore --disable-triggers would apply it: the organization's rows replaced by the backup's, in one transaction.</summary>
    private void Restore(Guid org)
    {
        var schema = BackupSchema(org);
        var deletes = string.Join('\n', RestoredTables.Reverse().Select(t => $"DELETE FROM {t} WHERE organization_id = '{org:D}';"));
        var inserts = string.Join('\n', RestoredTables.Select(t => $"INSERT INTO {t} SELECT * FROM {schema}.{t.Replace('.', '_')};"));
        fixture.Stack.AuthorityAdministratorSql($"BEGIN; SET LOCAL session_replication_role = replica;\n{deletes}\n{inserts}\nCOMMIT;");
    }

    private static async Task<List<Guid>> ListingAsync(StageBScenario s, TestUser user, string engine)
    {
        using var http = new HttpClient { BaseAddress = new Uri(StageBFixture.ApiBaseAddress), Timeout = TimeSpan.FromMinutes(2) };
        for (var attempt = 0; ; attempt++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/form-records/?pageSize=100");
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", s.Token(user));
            request.Headers.Add("X-Testing-Record-List-Engine", engine);
            using var response = await http.SendAsync(request, Ct);
            var text = await response.Content.ReadAsStringAsync(Ct);
            if (response.IsSuccessStatusCode)
            {
                return JsonDocument.Parse(text).RootElement.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("formRecordId").GetGuid()).ToList();
            }

            Assert.True(attempt < 20 && response.StatusCode is HttpStatusCode.ServiceUnavailable or HttpStatusCode.Conflict, "listing failed: " + text);
            await Task.Delay(500, Ct);
        }
    }

    private async Task WaitVerifiedAsync(Guid org, int seconds = 180)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (!(await Tier2Async(org)).Verified)
        {
            Assert.True(DateTime.UtcNow < deadline, "Tier 2 not verified");
            await Task.Delay(500, Ct);
        }
    }

    [Fact]
    public async Task AProjectionRestoreFromAnEarlierBackupNeverServesARevokedShareAndRebuildsRestoreBothTiers()
    {
        var w = await C150SearchWorkspace.CreateAsync(fixture, "ix7-rehearsal", 3);
        var org = w.S.OrganizationId;
        var reader = w.S.Reader.AuthorityUserId;
        var revoked = w.Records[0];
        await w.ToggleAsync(revoked);
        await w.S.WaitGateReadyAsync();
        await WaitVerifiedAsync(org);
        Assert.True(ReaderGrantPresent(org, revoked, reader));
        Backup(org);

        // After the backup: the share is revoked and a record is added.
        await w.ToggleAsync(revoked);
        await w.S.WaitGateReadyAsync();
        var added = await C150SearchWorkspace.RetryingAsync(() => fixture.Api.PostAsync($"/api/v1/form-definitions/{w.Form:D}/records", w.S.Token(w.S.Clerk), new { values = new Dictionary<string, string> { ["exhibit_count"] = "7" } }, "ix7-" + Guid.CreateVersion7().ToString("N")));
        Assert.True(added.IsSuccess, added.Body);
        var newRecord = added.Root.GetProperty("formRecordId").GetGuid();
        await w.S.WaitProjectionAsync(w.S.Clerk, newRecord);
        await WaitVerifiedAsync(org);
        Assert.False(ReaderGrantPresent(org, revoked, reader));

        // The restore, with the Tier 2 maintainers paused so it lands as one step.
        await AuthorityAsync(HttpMethod.Post, "/maintenance", new { paused = true });
        var restoredAt = Stopwatch.StartNew();
        try
        {
            Restore(org);
            Assert.True(ReaderGrantPresent(org, revoked, reader)); // the restored Tier 2 rows hold the revoked grant again
            Assert.StartsWith("Active", Tier1(org), StringComparison.Ordinal); // the restored Tier 1 state claims to be current
        }
        finally
        {
            await AuthorityAsync(HttpMethod.Post, "/maintenance", new { paused = false });
        }

        // Nothing serves the revoked share: the Reader's searches and listings decide from SpiceDB at a fresh pin.
        using var http = new HttpClient { BaseAddress = new Uri(StageBFixture.ApiBaseAddress), Timeout = TimeSpan.FromMinutes(1) };
        var readerSession = await w.SessionAsync(w.S.Reader);
        var search = await w.SearchAsync(http, w.S.Reader, readerSession, 0, 100);
        Assert.True(search.Status == 200 || search.Code is not null, "search: " + search.Status);
        Assert.DoesNotContain(revoked, search.Ids ?? []);
        foreach (var engine in new[] { "Indexed", "VerifiedScan" })
        {
            Assert.DoesNotContain(revoked, await ListingAsync(w.S, w.S.Reader, engine));
        }

        // Tier 1: the restored index is detected (the first search or the sweep) and quarantined; V1 answers meanwhile, with the new record.
        var clerkSession = await w.SessionAsync(w.S.Clerk);
        var clerk = await w.SearchAsync(http, w.S.Clerk, clerkSession, 0, 100);
        Assert.Equal(200, clerk.Status);
        Assert.Contains(newRecord, clerk.Ids!);
        var detectDeadline = DateTime.UtcNow.AddSeconds(120);
        while (!Tier1(org).StartsWith("Quarantined", StringComparison.Ordinal))
        {
            Assert.True(DateTime.UtcNow < detectDeadline, "Tier 1 restore not detected: " + Tier1(org));
            await Task.Delay(500, Ct);
        }

        var tier1Detected = restoredAt.Elapsed;

        // Tier 2: never reported verified (or Ready) while it holds the revoked grant. Acceptable outcomes: it quarantines; or it catches up
        // from the journal (the revoked grant gone) and is then either verified, or held not Ready because Tier 1 is quarantined (Tier 2
        // readiness requires an Active Tier 1, A-3).
        var tier2Deadline = DateTime.UtcNow.AddSeconds(120);
        string tier2Outcome;
        while (true)
        {
            var (status, verified) = await Tier2Async(org);
            var grant = ReaderGrantPresent(org, revoked, reader);
            Assert.False((verified || status == "Ready") && grant, $"Tier 2 reported {status} verified={verified} while holding a revoked grant");
            var tier1Quarantined = Tier1(org).StartsWith("Quarantined", StringComparison.Ordinal);
            if (status == "Quarantined" || (verified && !grant) || (!grant && status == "Building" && tier1Quarantined))
            {
                tier2Outcome = status == "Quarantined" ? "Quarantined" : verified ? "CaughtUpAndVerified" : "CaughtUpHeldNotReadyByTier1Quarantine";
                break;
            }

            Assert.True(DateTime.UtcNow < tier2Deadline, $"Tier 2 did not settle: {status} verified={verified} grant={grant} tier1={Tier1(org)}");
            await Task.Delay(500, Ct);
        }

        var tier2Settled = restoredAt.Elapsed;

        // Recovery: the administrator's Tier 1 rebuild; the controlled Tier 2 rebuild and a complete M2 pass.
        var rebuildClock = Stopwatch.StartNew();
        var rebuild = await fixture.Api.PostAsync("/api/v1/organization/search-index/rebuild", w.S.Token(w.S.Administrator), new { rebuildReasonCode = "IX7_RECOVERY_REHEARSAL" });
        Assert.True(rebuild.IsSuccess, rebuild.Body);
        var activeDeadline = DateTime.UtcNow.AddSeconds(240);
        while (Tier1(org) != "Active|")
        {
            Assert.True(DateTime.UtcNow < activeDeadline, "Tier 1 not rebuilt: " + Tier1(org));
            await Task.Delay(1000, Ct);
        }

        var tier1Rebuilt = rebuildClock.Elapsed;
        Assert.Equal((w.Records.Count + 1).ToString(System.Globalization.CultureInfo.InvariantCulture), Scalar($"SELECT count(*) FROM authority_search.search_record_entries e JOIN authority.record_content_heads h ON h.form_record_id = e.form_record_id AND h.committed_operation_id = e.indexed_operation_id WHERE e.organization_id = '{org:D}';"));
        rebuildClock.Restart();
        var tier2 = await AuthorityAsync(HttpMethod.Post, $"/{org:D}/rebuild", new { rebuild_reason_code = "IX7_RECOVERY_REHEARSAL" });
        Assert.Equal("Ready", tier2.GetProperty("status_name").GetString());
        await WaitVerifiedAsync(org);
        var tier2Rebuilt = rebuildClock.Elapsed;
        Assert.False(ReaderGrantPresent(org, revoked, reader));

        var clerkAfter = await w.SearchAsync(http, w.S.Clerk, await w.SessionAsync(w.S.Clerk), 0, 100);
        Assert.Equal(200, clerkAfter.Status);
        Assert.Equal(w.Records.Append(newRecord).Order(), clerkAfter.Ids!.Order());
        Assert.DoesNotContain(revoked, (await w.SearchAsync(http, w.S.Reader, await w.SessionAsync(w.S.Reader), 0, 100)).Ids ?? []);
        fixture.Stack.AuthorityAdministratorSql($"DROP SCHEMA IF EXISTS {BackupSchema(org)} CASCADE;");

        // Local rehearsal evidence (not an RTO): artifacts/ix-7/recovery-rehearsal.json (not versioned).
        var directory = Path.Combine(ComposeStack.RepositoryRoot, "artifacts", "ix-7");
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(Path.Combine(directory, "recovery-rehearsal.json"), JsonSerializer.Serialize(new
        {
            kind = "IX-7 local recovery rehearsal of the search projections (security-test stack; not G14, not staging, not an RTO)",
            measured_on = DateTimeOffset.UtcNow,
            organization_id = org,
            tier1_detected_seconds = Math.Round(tier1Detected.TotalSeconds, 1),
            tier2_outcome = tier2Outcome,
            tier2_settled_seconds = Math.Round(tier2Settled.TotalSeconds, 1),
            tier1_rebuild_seconds = Math.Round(tier1Rebuilt.TotalSeconds, 1),
            tier2_rebuild_and_m2_seconds = Math.Round(tier2Rebuilt.TotalSeconds, 1),
        }, Indented), Ct);
    }
}
