using System.Text.Json;
using EnterpriseForms.SecurityTests.Harness;
using EnterpriseForms.StageBTests.Harness;
using Xunit;

namespace EnterpriseForms.StageBTests.Tests;

/// <summary>
/// C-150 IX-5c shadow preparation, focused stack tests. The comparison is off by default (the Testing status endpoint answers 404). With
/// <c>IX5C_SHADOW=true</c> (the Authority is recreated for the test and restored after it):
/// <list type="bullet">
/// <item>Tier 1 answers exactly as with the comparison off (status codes and result ids of a fixed set of searches);</item>
/// <item>every comparison of a stable organization is a Match; under share/revoke churn there is no Mismatch (a moved snapshot is
/// NotComparable) and no search discloses a revoked record;</item>
/// <item>a Tier 2 value row removed out of band is reported as a Mismatch (counts only), while Tier 1's answer is unchanged;</item>
/// <item>the comparison is organization-bound: the damaged organization's rows never affect another organization's comparisons.</item>
/// </list>
/// Tier 2 serves nothing at any point.
/// </summary>
[Collection(StageBStackCollectionDefinition.Name)]
public sealed class SearchAccessShadowStackTests(StageBFixture fixture)
{
    private const string Prefix = "/internal/v1/testing/search-access";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<HttpResult> ShadowAsync(string suffix = "", HttpMethod? method = null) =>
        await fixture.Authority.SendAsync(method ?? HttpMethod.Get, Prefix + "/shadow" + suffix, TestIdentity.WorkloadToken(), method == HttpMethod.Post ? new { } : null);

    private void RecreateAuthority(bool shadow)
    {
        Environment.SetEnvironmentVariable("IX5C_SHADOW", shadow ? "true" : null);
        fixture.Stack.Compose("up -d --no-deps --force-recreate --wait authority", 300);
    }

    private async Task WaitTier2ReadyAsync(Guid organizationId)
    {
        var deadline = DateTime.UtcNow.AddSeconds(120);
        string? last = null;
        while (DateTime.UtcNow < deadline)
        {
            var status = await fixture.Authority.SendAsync(HttpMethod.Get, Prefix + $"/{organizationId:D}/status", TestIdentity.WorkloadToken(), null);
            last = status.Body;
            if (status.IsSuccess && status.Root.GetProperty("status_name").GetString() == "Ready")
            {
                return;
            }

            await Task.Delay(300, Ct);
        }

        Assert.Fail("Tier 2 not ready: " + last);
    }

    /// <summary>IX-5: waits until the verification loop reports the organization verified (comparisons count only then).</summary>
    private async Task WaitVerifiedAsync(Guid organizationId)
    {
        var deadline = DateTime.UtcNow.AddSeconds(180);
        string? last = null;
        while (DateTime.UtcNow < deadline)
        {
            var status = await fixture.Authority.SendAsync(HttpMethod.Get, Prefix + $"/{organizationId:D}/verification", TestIdentity.WorkloadToken(), null);
            last = status.Body;
            if (status.IsSuccess && status.Root.GetProperty("verified").GetBoolean())
            {
                return;
            }

            await Task.Delay(500, Ct);
        }

        Assert.Fail("Tier 2 not verified: " + last);
    }

    private Task<HttpResult> PauseAsync(bool paused) => fixture.Authority.SendAsync(HttpMethod.Post, Prefix + "/maintenance", TestIdentity.WorkloadToken(), new { paused });

    /// <summary>Waits until every offered observation has been compared (or dropped); returns the snapshot.</summary>
    private async Task<JsonElement> SettledShadowAsync(long expectedOffered)
    {
        var deadline = DateTime.UtcNow.AddSeconds(60);
        JsonElement root;
        do
        {
            var result = await ShadowAsync();
            Assert.True(result.IsSuccess, result.Body);
            root = result.Root.Clone();
            var compared = new[] { "matched", "mismatched", "not_comparable", "cancelled", "failed", "dropped" }.Sum(n => root.GetProperty(n).GetInt64());
            if (root.GetProperty("offered").GetInt64() >= expectedOffered && compared >= root.GetProperty("offered").GetInt64())
            {
                return root;
            }

            await Task.Delay(200, Ct);
        }
        while (DateTime.UtcNow < deadline);
        Assert.Fail("shadow comparisons did not settle: " + root);
        return root;
    }

    private static async Task<List<(int Status, string Ids)>> RunFixedSearchesAsync(C150SearchWorkspace w, HttpClient http, string clerk, string reader)
    {
        var results = new List<(int, string)>();
        foreach (var (user, digest) in new[] { (w.S.Clerk, clerk), (w.S.Reader, reader) })
        {
            foreach (var (lower, upper) in new[] { (0L, 100L), (2L, 2L), (3L, 6L), (50L, 60L) })
            {
                var o = await w.SearchAsync(http, user, digest, lower, upper);
                results.Add((o.Status, o.Ids is null ? o.Code ?? "-" : string.Join(",", o.Ids.Order())));
            }
        }

        return results;
    }

    [Fact]
    public async Task TheShadowComparisonIsOffByDefault() =>
        Assert.Equal(System.Net.HttpStatusCode.NotFound, (await ShadowAsync()).Status);

    [Fact]
    public async Task ShadowComparisonsMatchTier1AndNeverChangeItsAnswersEvenUnderChurnOrDamage()
    {
        var a = await C150SearchWorkspace.CreateAsync(fixture, "c150-shadow-a", 8);
        var b = await C150SearchWorkspace.CreateAsync(fixture, "c150-shadow-b", 3);
        foreach (var record in new[] { a.Records[1], a.Records[2], a.Records[5] })
        {
            await a.ToggleAsync(record);
        }

        await b.ToggleAsync(b.Records[0]);
        await a.S.WaitGateReadyAsync();
        await b.S.WaitGateReadyAsync();
        using var http = new HttpClient { BaseAddress = new Uri(StageBFixture.ApiBaseAddress), Timeout = TimeSpan.FromMinutes(1) };
        var clerk = await a.SessionAsync(a.S.Clerk);
        var reader = await a.SessionAsync(a.S.Reader);
        var off = await RunFixedSearchesAsync(a, http, clerk, reader);
        Assert.Contains(off, r => r.Status == 200 && r.Ids.Length > 0);

        try
        {
            RecreateAuthority(shadow: true);
            await a.S.WaitGateReadyAsync(TimeSpan.FromSeconds(60));
            await WaitTier2ReadyAsync(a.S.OrganizationId);
            await WaitTier2ReadyAsync(b.S.OrganizationId);
            await WaitVerifiedAsync(a.S.OrganizationId);
            await WaitVerifiedAsync(b.S.OrganizationId);
            Assert.True((await ShadowAsync("/reset", HttpMethod.Post)).IsSuccess);

            // 1. Tier 1 is unchanged with the comparison on, and every comparison of the stable organization is a Match.
            var on = await RunFixedSearchesAsync(a, http, clerk, reader);
            Assert.Equal(off, on);
            var stable = await SettledShadowAsync(on.Count(r => r.Status == 200));
            Assert.Equal(0, stable.GetProperty("mismatched").GetInt64());
            Assert.Equal(on.Count(r => r.Status == 200), stable.GetProperty("matched").GetInt64());

            // 2. Churn: share/revoke toggles while the Reader searches. No Mismatch; a revoked record is never returned after its revocation.
            Assert.True((await ShadowAsync("/reset", HttpMethod.Post)).IsSuccess);
            var toggles = new List<(Guid Record, bool SharedAfter, DateTimeOffset Requested, DateTimeOffset Confirmed)>();
            var outcomes = new List<C150SearchWorkspace.SearchOutcome>();
            using (var stop = CancellationTokenSource.CreateLinkedTokenSource(Ct))
            {
                var searcher = Task.Run(async () =>
                {
                    while (!stop.IsCancellationRequested)
                    {
                        outcomes.Add(await a.SearchAsync(http, a.S.Reader, reader, 0, 100));
                    }
                }, CancellationToken.None);
                for (var i = 0; i < 12; i++)
                {
                    var record = a.Records[i % 4];
                    var toggled = await a.ToggleAsync(record);
                    toggles.Add((record, toggled.SharedAfter, toggled.Requested, toggled.Confirmed));
                    await Task.Delay(300, Ct);
                }

                await stop.CancelAsync();
                await searcher;
            }

            var churn = await SettledShadowAsync(outcomes.Count(o => o.Status == 200));
            Assert.Equal(0, churn.GetProperty("mismatched").GetInt64());
            Assert.True(churn.GetProperty("matched").GetInt64() + churn.GetProperty("not_comparable").GetInt64() >= outcomes.Count(o => o.Status == 200));
            Assert.Contains(toggles, t => !t.SharedAfter);
            foreach (var revoked in toggles.Where(t => !t.SharedAfter))
            {
                // A search that started after the revocation committed and ended before the record's next share was requested never returns it.
                var reshared = toggles.Where(t => t.Record == revoked.Record && t.SharedAfter && t.Requested > revoked.Confirmed).Select(t => t.Requested).DefaultIfEmpty(DateTimeOffset.MaxValue).Min();
                Assert.DoesNotContain(outcomes, o => o.Ids is not null && o.Started > revoked.Confirmed && o.Ended < reshared && o.Ids.Contains(revoked.Record));
            }

            // The evidence report so far: samples stored, no unexplained difference, no restart event (counts and codes only).
            var evidence = await fixture.Authority.SendAsync(HttpMethod.Get, Prefix + "/shadow/evidence?hours=2", TestIdentity.WorkloadToken(), null);
            Assert.True(evidence.IsSuccess, evidence.Body);
            Assert.True(evidence.Root.GetProperty("samples").GetInt32() >= on.Count(r => r.Status == 200));
            Assert.Equal(0, evidence.Root.GetProperty("non_canary_mismatches").GetInt32());
            Assert.DoesNotContain(a.Records[1].ToString("D"), evidence.Body, StringComparison.OrdinalIgnoreCase);

            // 3. Out-of-band damage of a Tier 2 row: Tier 1 is unchanged, the comparison reports a Mismatch with counts only. The
            // maintenance and verification loops are paused, so M2 does not quarantine the damage before the comparison sees it.
            await a.S.WaitGateReadyAsync();
            await WaitTier2ReadyAsync(a.S.OrganizationId);
            await WaitVerifiedAsync(a.S.OrganizationId);
            Assert.True((await PauseAsync(true)).IsSuccess);
            var sharedNow = a.Records.First(a.IsShared);
            var deleted = fixture.Stack.AuthorityAdministratorSql($"WITH d AS (DELETE FROM authority_search_access.search_access_value_postings WHERE organization_id = '{a.S.OrganizationId:D}' AND access_principal_entity_id = '{a.S.Reader.AuthorityUserId:D}' AND form_record_id = '{sharedNow:D}' RETURNING 1) SELECT count(*) FROM d;").Trim();
            Assert.Equal("1", deleted);
            Assert.True((await ShadowAsync("/reset", HttpMethod.Post)).IsSuccess);
            var damagedSearch = await a.SearchAsync(http, a.S.Reader, reader, 0, 100);
            Assert.Equal(200, damagedSearch.Status);
            Assert.Contains(sharedNow, damagedSearch.Ids!); // Tier 1 never reads Tier 2
            var damaged = await SettledShadowAsync(1);
            Assert.Equal(1, damaged.GetProperty("mismatched").GetInt64());
            var mismatch = damaged.GetProperty("recent").EnumerateArray().Single(r => r.GetProperty("classification").GetString() == "Mismatch");
            Assert.Equal(1, mismatch.GetProperty("only_in_tier1_count").GetInt32());
            Assert.DoesNotContain(sharedNow.ToString("D"), damaged.GetRawText(), StringComparison.OrdinalIgnoreCase); // counts only
            var restarted = await fixture.Authority.SendAsync(HttpMethod.Get, Prefix + "/shadow/evidence?hours=2", TestIdentity.WorkloadToken(), null);
            Assert.Contains(restarted.Root.GetProperty("restart_events").EnumerateArray(), e => e.GetProperty("reason_code").GetString() == "NON_CANARY_MISMATCH");
            Assert.True((await PauseAsync(false)).IsSuccess);

            // 4. Organization-bound: organization B's comparisons are unaffected by A's damaged rows.
            var bReader = await b.SessionAsync(b.S.Reader);
            Assert.True((await ShadowAsync("/reset", HttpMethod.Post)).IsSuccess);
            var bSearch = await b.SearchAsync(http, b.S.Reader, bReader, 0, 100);
            Assert.Equal(200, bSearch.Status);
            var isolated = await SettledShadowAsync(1);
            Assert.Equal((1, 0), (isolated.GetProperty("matched").GetInt64(), isolated.GetProperty("mismatched").GetInt64()));
        }
        finally
        {
            await PauseAsync(false);
            RecreateAuthority(shadow: false);
            await fixture.Authority.WaitReadyAsync(TimeSpan.FromSeconds(90));
        }
    }
}
