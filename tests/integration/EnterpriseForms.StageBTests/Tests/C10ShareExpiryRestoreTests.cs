using System.Globalization;
using EnterpriseForms.StageBTests.Harness;
using Xunit;

namespace EnterpriseForms.StageBTests.Tests;

/// <summary>
/// Phase C10 WP-0 §4 test 13 on its own stack (the projection-integrity-restore group, because it rewrites AuthorityDb as a restore
/// would): after an expired share converged, the share's rows are put back to their pre-convergence state as a restore of an
/// AuthorityDb backup taken before convergence would leave them (administrator, triggers suspended for the simulation only, exactly as
/// a physical restore bypasses them). The share stays denied (expiry is enforced at access time, never by the converged state), and
/// convergence runs again from the restored state without a second recipient-visible change.
/// </summary>
[Collection(StageBStackCollectionDefinition.Name)]
public sealed class C10ShareExpiryRestoreTests(StageBFixture fixture)
{
    private static readonly TimeSpan Settle = TimeSpan.FromSeconds(40);

    private string State(Guid share) =>
        fixture.Stack.AuthorityObserverSql($"SELECT resource_lifecycle_state FROM authority_resources WHERE resource_type_name = 'record_share' AND resource_entity_id = '{share:D}';").Trim();

    private async Task WaitStateAsync(Guid share, string state, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (State(share) != state)
        {
            Assert.True(DateTime.UtcNow < deadline, $"share did not reach {state} (now {State(share)})");
            await Task.Delay(1000);
        }
    }

    [Fact]
    public async Task ARestoreFromBeforeConvergenceLeavesTheShareDeniedAndConvergenceRunsAgain()
    {
        var s = new StageBScenario(fixture, "c10-expiry-restore");
        await s.BootstrapAsync();
        await s.BuildAccusedWorkspaceAsync();
        var record = await s.CreateAccusedAsync(s.Clerk);
        var expiresOn = DateTimeOffset.UtcNow.AddSeconds(15);
        var created = await fixture.Api.PostAsync($"/api/v1/form-records/{record:D}/shares", s.Token(s.Clerk), new { useDevelopmentStepUp = true, recipientUserId = s.Reader.AuthorityUserId, recipientTeamId = (Guid?)null, recipientRole = "Reader", fieldKeys = new[] { "given_name" }, shareExpiresOn = expiresOn });
        Assert.True(created.IsSuccess, created.Body);
        var share = created.Root.GetProperty("recordShareId").GetGuid();
        await s.WaitGateReadyAsync(Settle);
        await WaitStateAsync(share, "Expired", TimeSpan.FromSeconds(120));

        // Restore simulation: the share resource as it was before convergence.
        fixture.Stack.AuthorityRestoreSimulationSql($"""
            BEGIN; SET LOCAL session_replication_role = replica;
            UPDATE authority_resources SET resource_lifecycle_state = 'Active' WHERE resource_type_name = 'record_share' AND resource_entity_id = '{share:D}';
            COMMIT;
            """);
        Assert.Equal("Active", State(share));
        var projection = await s.ProjectionAsync(s.Reader, record);
        Assert.True(projection.Status is System.Net.HttpStatusCode.Forbidden or System.Net.HttpStatusCode.NotFound or System.Net.HttpStatusCode.ServiceUnavailable, projection.Body); // denied or fail closed, never access

        await WaitStateAsync(share, "Expired", TimeSpan.FromSeconds(120));
        var after = await s.ProjectionAsync(s.Reader, record);
        Assert.False(after.IsSuccess, after.Body);
        Assert.True(int.Parse(fixture.Stack.AuthorityObserverSql($"SELECT count(*) FROM security_journal_entries WHERE journal_event_kind = 'ShareExpired' AND journal_event_body_json LIKE '%{share:D}%';").Trim(), CultureInfo.InvariantCulture) >= 1);
    }
}
