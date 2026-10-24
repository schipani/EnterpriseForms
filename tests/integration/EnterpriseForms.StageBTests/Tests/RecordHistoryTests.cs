using System.Net;
using System.Text.Json;
using EnterpriseForms.SecurityTests.Harness;
using EnterpriseForms.StageBTests.Harness;
using Xunit;

namespace EnterpriseForms.StageBTests.Tests;

/// <summary>
/// Phase C6 M2 — trusted record history on the real stack (PostgreSQL ×2, SpiceDB, Vault Transit, Authority, API): the version
/// list comes from the Authorization Authority (AC-1) and matches its accepted manifest commits exactly; an earlier version is
/// shown read-only only after the Authority verified its exact rows; field-share readers, members without access, former
/// owners and other organizations get 404; tampered or missing historical rows fail closed and quarantine the record (AC-4,
/// current behaviour); forged revision numbers are not versions; values of fields no longer in the form stay visible.
/// </summary>
[Collection(StageBStackCollectionDefinition.Name)]
public sealed class RecordHistoryTests(StageBFixture fixture)
{
    private async Task<(StageBScenario S, Guid Record)> AccusedAsync(string name)
    {
        var s = new StageBScenario(fixture, name);
        await s.BootstrapAsync();
        await s.BuildAccusedWorkspaceAsync();
        var record = await s.CreateAccusedAsync(s.Clerk);
        await s.WaitProjectionAsync(s.Clerk, record);
        return (s, record);
    }

    private static long Revision(JsonElement projection) => projection.GetProperty("record").GetProperty("contentRevision").GetInt64();

    private static Dictionary<string, JsonElement> Values(JsonElement projection) =>
        projection.GetProperty("record").GetProperty("values").EnumerateArray().ToDictionary(v => v.GetProperty("fieldKey").GetString()!, v => v);

    private async Task<JsonElement> EditAsync(StageBScenario s, Guid record, Dictionary<string, string> values)
    {
        var view = (await s.WaitProjectionAsync(s.Clerk, record)).Root;
        var edit = await fixture.Api.PatchAsync($"/api/v1/form-records/{record:D}", s.Token(s.Clerk), new { expectedContentRevision = Revision(view), values });
        Assert.True(edit.IsSuccess, edit.Body);
        return (await s.WaitProjectionAsync(s.Clerk, record)).Root;
    }

    private async Task StepAsync(StageBScenario s, Guid record, string transition, string? reason = null)
    {
        var view = (await s.WaitProjectionAsync(s.Clerk, record)).Root;
        var result = await fixture.Api.PostAsync($"/api/v1/form-records/{record:D}/{transition}", s.Token(s.Clerk), new { expectedContentRevision = Revision(view), expectedStatus = view.GetProperty("lifecycle").GetProperty("status").GetString(), reason });
        Assert.True(result.IsSuccess, transition + ": " + result.Body);
    }

    private Task<HttpResult> VersionsAsync(StageBScenario s, TestUser user, Guid record, string query = "") =>
        WhileArchivingAsync(() => fixture.Api.GetAsync($"/api/v1/form-records/{record:D}/versions{query}", s.Token(user)));

    private Task<HttpResult> VersionAsync(StageBScenario s, TestUser user, Guid record, long revision) =>
        WhileArchivingAsync(() => fixture.Api.GetAsync($"/api/v1/form-records/{record:D}/versions/{revision}", s.Token(user)));

    /// <summary>
    /// A verified read fails closed (503 AUTHORITY_NOT_READY, retryable) until the Authority has archived the latest
    /// journal entries; a client retries, so the tests do too, briefly. Any other outcome is returned unchanged.
    /// </summary>
    private static async Task<HttpResult> WhileArchivingAsync(Func<Task<HttpResult>> read)
    {
        for (var attempt = 0; ; attempt++)
        {
            var result = await read();
            if (result.ErrorCode != "AUTHORITY_NOT_READY" || attempt >= 40)
            {
                return result;
            }

            await Task.Delay(250, TestContext.Current.CancellationToken);
        }
    }

    private Guid OperationOf(Guid record, long revision) =>
        Guid.Parse(fixture.Stack.AuthorityObserverSql($"SELECT committed_operation_id FROM record_manifest_commits WHERE form_record_id = '{record:D}' AND committed_content_revision = {revision};").Trim());

    [Fact]
    public async Task TheVersionListIsTheAuthoritysAcceptedHistoryAndEarlierVersionsOpenReadOnly()
    {
        var (s, record) = await AccusedAsync("c6-history");
        var current = await EditAsync(s, record, new() { ["given_name"] = "Blake" });                         // r2
        await StepAsync(s, record, "submit");                                                                   // r3
        await StepAsync(s, record, "reopen", "correcting the given name");                                      // r4

        var list = await VersionsAsync(s, s.Clerk, record);
        Assert.True(list.IsSuccess, list.Body);
        var versions = list.Root.GetProperty("versions").EnumerateArray().ToList();
        Assert.Equal([1L, 2L, 3L, 4L], versions.Select(v => v.GetProperty("contentRevision").GetInt64()));
        Assert.Equal([false, false, false, true], versions.Select(v => v.GetProperty("isCurrent").GetBoolean()));
        Assert.All(versions, v =>
        {
            Assert.Equal("User", v.GetProperty("actorKind").GetString());
            Assert.Equal(s.Clerk.AuthorityUserId, v.GetProperty("actorUserId").GetGuid());
            Assert.True(v.GetProperty("verifiableNow").GetBoolean());
        });
        Assert.Equal([("Created", "Authority"), ("Edited", "ActivityLog"), ("Submitted", "ActivityLog"), ("Reopened", "ActivityLog")],
            versions.Select(v => (v.GetProperty("actionLabel").GetString(), v.GetProperty("actionSource").GetString())));
        Assert.Equal(4, list.Root.GetProperty("currentContentRevision").GetInt64());

        // Exactly the Authority's accepted manifest commits (contiguous; no abandoned candidates).
        Assert.Equal("1,2,3,4", fixture.Stack.AuthorityObserverSql($"SELECT string_agg(committed_content_revision::text, ',' ORDER BY committed_content_revision) FROM record_manifest_commits WHERE form_record_id = '{record:D}';").Trim());

        // Paging in chronological order.
        var first = await VersionsAsync(s, s.Clerk, record, "?pageSize=2");
        Assert.Equal([1L, 2L], first.Root.GetProperty("versions").EnumerateArray().Select(v => v.GetProperty("contentRevision").GetInt64()));
        Assert.Equal(2, first.Root.GetProperty("nextAfterRevision").GetInt64());
        var rest = await VersionsAsync(s, s.Clerk, record, "?after=2&pageSize=2");
        Assert.Equal([3L, 4L], rest.Root.GetProperty("versions").EnumerateArray().Select(v => v.GetProperty("contentRevision").GetInt64()));

        // Version 1: its own values (decrypted for this full reader), its own status, read-only, current labels.
        var one = await VersionAsync(s, s.Clerk, record, 1);
        Assert.True(one.IsSuccess, one.Body);
        var projection = one.Root.GetProperty("projection");
        Assert.Equal(1, Revision(projection));
        Assert.Equal("Avery", Values(projection)["given_name"].GetProperty("stringValue").GetString());
        Assert.Equal(Values(current)["given_name"].GetProperty("applicationEncrypted").GetBoolean(), Values(projection)["given_name"].GetProperty("applicationEncrypted").GetBoolean());
        Assert.True(Values(projection)["given_name"].GetProperty("applicationEncrypted").GetBoolean()); // an encrypted historical field
        Assert.Equal("Draft", projection.GetProperty("lifecycle").GetProperty("status").GetString());
        Assert.Empty(projection.GetProperty("lifecycle").GetProperty("allowedTransitions").EnumerateArray());
        Assert.False(projection.GetProperty("permissions").GetProperty("canEdit").GetBoolean());
        Assert.Equal(string.Empty, projection.GetProperty("eTag").GetString());
        Assert.False(one.Root.GetProperty("version").GetProperty("isCurrent").GetBoolean());
        Assert.Equal("Submitted", (await VersionAsync(s, s.Clerk, record, 3)).Root.GetProperty("projection").GetProperty("lifecycle").GetProperty("status").GetString());
        Assert.Equal("Blake", Values((await VersionAsync(s, s.Clerk, record, 2)).Root.GetProperty("projection"))["given_name"].GetProperty("stringValue").GetString());

        // Forged revision numbers are not versions; nothing is quarantined by asking.
        foreach (var forged in new long[] { 0, 5, 99 })
        {
            var refused = await VersionAsync(s, s.Clerk, record, forged);
            Assert.Equal(HttpStatusCode.NotFound, refused.Status);
        }

        Assert.True((await s.ProjectionAsync(s.Clerk, record)).IsSuccess);
        var activity = await fixture.Api.GetAsync($"/api/v1/form-records/{record:D}/activity", s.Token(s.Clerk));
        Assert.Contains(activity.Root.GetProperty("events").EnumerateArray(), e => e.GetProperty("eventAction").GetString() == "RECORD_VERSION_VIEWED");
        Assert.DoesNotContain("Avery", activity.Body, StringComparison.Ordinal); // no historical values in the activity log
    }

    [Fact]
    public async Task OnlyAPersonWhoMayReadTheWholeRecordNowSeesHistory()
    {
        var (s, record) = await AccusedAsync("c6-history-access");
        await EditAsync(s, record, new() { ["given_name"] = "Blake" });

        // No grant, design rights only, another organization: same 404 as for a record that does not exist.
        var other = new StageBScenario(fixture, "c6-history-other");
        await other.BootstrapAsync();
        foreach (var token in new[] { s.Token(s.Reader), s.Token(s.Designer), other.Token(other.Clerk) })
        {
            Assert.Equal(HttpStatusCode.NotFound, (await fixture.Api.GetAsync($"/api/v1/form-records/{record:D}/versions", token)).Status);
            Assert.Equal(HttpStatusCode.NotFound, (await fixture.Api.GetAsync($"/api/v1/form-records/{record:D}/versions/1", token)).Status);
        }

        Assert.Equal(HttpStatusCode.NotFound, (await VersionsAsync(s, s.Clerk, Guid.CreateVersion7())).Status);

        // A field-share reader sees the shared field of the current version, but no history (V1: history needs full read).
        var share = await fixture.Api.PostAsync($"/api/v1/form-records/{record:D}/shares", s.Token(s.Clerk), new { recipientUserId = s.Reader.AuthorityUserId, recipientTeamId = (Guid?)null, recipientRole = "Reader", fieldKeys = new[] { "given_name" } });
        Assert.True(share.IsSuccess, share.Body);
        await s.WaitGateReadyAsync(TimeSpan.FromSeconds(40));
        Assert.True((await s.WaitProjectionAsync(s.Reader, record, TimeSpan.FromSeconds(40))).IsSuccess);
        Assert.Equal(HttpStatusCode.NotFound, (await VersionsAsync(s, s.Reader, record)).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await VersionAsync(s, s.Reader, record, 1)).Status);

        // Ownership moves to the reader: the former owner loses history at once (stale authority), the new owner sees all of it.
        var transfer = await fixture.Api.PostAsync($"/api/v1/form-records/{record:D}/transfer-ownership", s.Token(s.Clerk), new { newOwnerUserId = s.Reader.AuthorityUserId, newOwnerTeamId = (Guid?)null, transferReason = "handover for history test" });
        Assert.True(transfer.IsSuccess, transfer.Body);
        await s.WaitGateReadyAsync(TimeSpan.FromSeconds(40));
        Assert.Equal(HttpStatusCode.NotFound, (await VersionsAsync(s, s.Clerk, record)).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await VersionAsync(s, s.Clerk, record, 1)).Status);
        var newOwner = await VersionsAsync(s, s.Reader, record);
        Assert.True(newOwner.IsSuccess, newOwner.Body);
        Assert.Equal(2, newOwner.Root.GetProperty("versions").GetArrayLength());
        Assert.Equal("Avery", Values((await VersionAsync(s, s.Reader, record, 1)).Root.GetProperty("projection"))["given_name"].GetProperty("stringValue").GetString());
    }

    [Theory]
    [InlineData("tampered")]
    [InlineData("missing")]
    public async Task ATamperedOrMissingHistoricalRowFailsClosedAndQuarantinesTheRecord(string attack)
    {
        var (s, record) = await AccusedAsync("c6-history-" + attack);
        await EditAsync(s, record, new() { ["date_of_birth"] = "1991-03-01" });
        var operation = OperationOf(record, 1);
        var sql = attack == "tampered"
            ? $"UPDATE field_date_values SET date_value = DATE '2001-01-01' WHERE form_record_id = '{record:D}' AND content_change_operation_id = '{operation:D}'; SELECT count(*) FROM field_date_values WHERE form_record_id = '{record:D}' AND content_change_operation_id = '{operation:D}' AND date_value = DATE '2001-01-01';"
            : $"DELETE FROM field_date_values WHERE form_record_id = '{record:D}' AND content_change_operation_id = '{operation:D}'; SELECT 1;";
        Assert.Equal("1", fixture.Stack.AttackerSql(sql).Trim().Split('\n')[^1].Trim());

        var view = await VersionAsync(s, s.Clerk, record, 1);
        Assert.Equal(HttpStatusCode.Locked, view.Status);
        Assert.Equal("INTEGRITY_VERIFICATION_FAILED", view.ErrorCode);
        Assert.DoesNotContain("2001-01-01", view.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("Avery", view.Body, StringComparison.Ordinal);

        // AC-4 (current behaviour kept): the record itself is quarantined; nothing more is released, the list included.
        Assert.Equal(HttpStatusCode.Locked, (await s.ProjectionAsync(s.Clerk, record)).Status);
        Assert.Equal(HttpStatusCode.Locked, (await VersionsAsync(s, s.Clerk, record)).Status);
        Assert.Equal("t", fixture.Stack.AuthorityObserverSql($"SELECT content_head_quarantined FROM record_content_heads WHERE form_record_id = '{record:D}';").Trim());
    }

    [Fact]
    public async Task AnEarlierVersionStillShowsValuesOfFieldsNoLongerInTheForm()
    {
        var (s, record) = await AccusedAsync("c6-history-removed-field");
        var removed = s.AccusedFields["external_reference"];
        var layout = s.AccusedLayout();
        var element = "," + StageBScenario.Field(removed, "guid", "External reference", 2);
        Assert.Contains(element, layout, StringComparison.Ordinal);
        await s.SaveDraftAsync(s.AccusedFormId, layout.Replace(element, string.Empty, StringComparison.Ordinal));
        await s.PublishAsync(s.AccusedFormId, "external reference removed from the form");
        await EditAsync(s, record, new() { ["given_name"] = "Blake" });

        var one = await VersionAsync(s, s.Clerk, record, 1);
        Assert.True(one.IsSuccess, one.Body);
        var projection = one.Root.GetProperty("projection");
        Assert.DoesNotContain(removed.ToString("D"), projection.GetProperty("layoutVersion").GetProperty("layoutJson").GetString()!, StringComparison.OrdinalIgnoreCase); // current layout
        Assert.Equal("0199a2b3-c4d5-7e6f-8123-456789abcdef", Values(projection)["external_reference"].GetProperty("stringValue").GetString()); // the version's value
        Assert.Contains(projection.GetProperty("fields").EnumerateArray(), f => f.GetProperty("fieldDefinitionId").GetGuid() == removed); // current label available
    }
}
