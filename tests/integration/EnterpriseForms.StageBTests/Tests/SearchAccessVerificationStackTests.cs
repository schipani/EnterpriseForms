using System.Text.Json;
using EnterpriseForms.SecurityTests.Harness;
using EnterpriseForms.StageBTests.Harness;
using Xunit;

namespace EnterpriseForms.StageBTests.Tests;

/// <summary>
/// IX-5 M1 fence and M2 sweep on the security-test stack, against real SpiceDB <c>LookupSubjects</c> at pinned snapshots (IX-41, IX-52):
/// <list type="bullet">
/// <item>the verification loop brings a built organization to verified on its own; a permission change opens the fence until the
/// affected record is verified;</item>
/// <item>an Authority-domain deletion of a Tier 2 row after verification is found by the next step (M2-E/M2-S) and quarantines Tier 2;
/// only a rebuild leaves it, and the rows are unverified again until a complete new pass;</item>
/// <item>Tier 1 serves unchanged throughout (Tier 2 serves nothing).</item>
/// </list>
/// </summary>
[Collection(StageBStackCollectionDefinition.Name)]
public sealed class SearchAccessVerificationStackTests(StageBFixture fixture)
{
    private const string Prefix = "/internal/v1/testing/search-access";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<JsonElement> AuthorityAsync(HttpMethod method, string path, object? body = null)
    {
        var result = await fixture.Authority.SendAsync(method, Prefix + path, TestIdentity.WorkloadToken(), body);
        Assert.True(result.IsSuccess, path + ": " + result.Body);
        return result.Root.Clone();
    }

    private Task<JsonElement> VerificationAsync(Guid org) => AuthorityAsync(HttpMethod.Get, $"/{org:D}/verification");

    private Task<JsonElement> StatusAsync(Guid org) => AuthorityAsync(HttpMethod.Get, $"/{org:D}/status");

    /// <summary>Waits until the verification loop reports the organization verified (M1 fence closed, M2 current).</summary>
    private async Task<JsonElement> WaitVerifiedAsync(Guid org, int seconds = 120)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        JsonElement status;
        do
        {
            status = await VerificationAsync(org);
            if (status.GetProperty("verified").GetBoolean())
            {
                return status;
            }

            Assert.NotEqual("Quarantined", (await StatusAsync(org)).GetProperty("status_name").GetString());
            await Task.Delay(500, Ct);
        }
        while (DateTime.UtcNow < deadline);
        Assert.Fail("not verified: " + status);
        return status;
    }

    [Fact]
    public async Task TheLoopVerifiesABuiltOrganizationAndAChangeOpensTheFenceUntilItsRecordIsVerified()
    {
        var w = await C150SearchWorkspace.CreateAsync(fixture, "ix5-verify", 4);
        var verified = await WaitVerifiedAsync(w.S.OrganizationId);
        Assert.True(verified.GetProperty("spice_db_sweep_records_checked").GetInt64() >= w.Records.Count);

        // A share: the rows catch up, the fence is open until the affected record is compared with SpiceDB at a pin.
        await AuthorityAsync(HttpMethod.Post, "/maintenance", new { paused = true });
        try
        {
            await w.ToggleAsync(w.Records[2]);
            await w.S.WaitGateReadyAsync();
            await AuthorityAsync(HttpMethod.Post, $"/{w.S.OrganizationId:D}/catch-up", new { maximum_events = 100 });
            Assert.Equal("M1_FENCE_OPEN", (await VerificationAsync(w.S.OrganizationId)).GetProperty("reason_code").GetString());
            var step = await AuthorityAsync(HttpMethod.Post, $"/{w.S.OrganizationId:D}/verify-step");
            Assert.Equal("VERIFIED", step.GetProperty("outcome_code").GetString());
            Assert.True(step.GetProperty("fenced_records").GetInt32() >= 1, step.ToString());
            Assert.True(step.GetProperty("subject_lookups").GetInt32() > 0);
            Assert.True(step.GetProperty("verified").GetBoolean());
        }
        finally
        {
            await AuthorityAsync(HttpMethod.Post, "/maintenance", new { paused = false });
        }

        // Tier 1 is unaffected throughout.
        using var http = new HttpClient { BaseAddress = new Uri(StageBFixture.ApiBaseAddress), Timeout = TimeSpan.FromMinutes(1) };
        var search = await w.SearchAsync(http, w.S.Reader, await w.SessionAsync(w.S.Reader), 0, 100);
        Assert.Equal(200, search.Status);
        Assert.Contains(w.Records[2], search.Ids!);
    }

    [Fact]
    public async Task ARowDeletedAfterVerificationIsFoundQuarantinedAndOnlyARebuildFollowedByACompletePassRestoresVerification()
    {
        var w = await C150SearchWorkspace.CreateAsync(fixture, "ix5-divergence", 3);
        await w.ToggleAsync(w.Records[1]);
        await w.S.WaitGateReadyAsync();
        await WaitVerifiedAsync(w.S.OrganizationId);
        var org = w.S.OrganizationId;
        await AuthorityAsync(HttpMethod.Post, "/maintenance", new { paused = true });
        try
        {
            // Out-of-band deletion of the Reader's share row (T2-04): no journal event names it.
            var deleted = fixture.Stack.AuthorityAdministratorSql($"WITH d AS (DELETE FROM authority_search_access.search_access_record_grants WHERE organization_id = '{org:D}' AND access_principal_entity_id = '{w.S.Reader.AuthorityUserId:D}' RETURNING 1) SELECT count(*) FROM d;").Trim();
            Assert.Equal("1", deleted);
            var step = await AuthorityAsync(HttpMethod.Post, $"/{org:D}/verify-step?full=true");
            // Found either by the full equivalence check (rows ≠ derivation) or by the SpiceDB pass; both quarantine.
            Assert.Equal("DIVERGED", step.GetProperty("outcome_code").GetString());
            Assert.Equal("Quarantined", (await StatusAsync(org)).GetProperty("status_name").GetString());
            Assert.False((await VerificationAsync(org)).GetProperty("verified").GetBoolean());
        }
        finally
        {
            await AuthorityAsync(HttpMethod.Post, "/maintenance", new { paused = false });
        }

        var rebuilt = await AuthorityAsync(HttpMethod.Post, $"/{org:D}/rebuild", new { rebuild_reason_code = "IX5_VERIFICATION_TEST" });
        Assert.Equal("Ready", rebuilt.GetProperty("status_name").GetString());
        Assert.Equal("M2_NOT_STARTED", (await VerificationAsync(org)).GetProperty("reason_code").GetString()); // reset by the rebuild
        await WaitVerifiedAsync(org);
    }
}
