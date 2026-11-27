using System.Globalization;
using System.Net;
using EnterpriseForms.SecurityTests.Harness;
using EnterpriseForms.StageBTests.Harness;
using Xunit;

namespace EnterpriseForms.StageBTests.Tests;

/// <summary>
/// Phase C10 WP-0 §4 test 13 with real elapsed time (owner decision 2, C-201): "Restore an AuthorityDb backup taken before
/// convergence ⇒ the share stays denied; replay converges it." A share is granted with a genuine expiry (one hour ahead, the
/// Authority's minimum) through the supported command; AuthorityDb is backed up with <c>pg_dump</c> while the share is still Active;
/// the share expires naturally and the convergence loop records <c>ExpireShare</c>; then the whole AuthorityDb is restored from that
/// backup with <c>pg_restore --clean</c>, the Authority stopped around it (the T16 restore technique, applied to AuthorityDb). No
/// expiry value is written by SQL and no trigger is suspended. The restore rewrites AuthorityDb for every organization on the stack,
/// so this class runs in a group (and on a stack) of its own.
/// </summary>
[Collection(StageBStackCollectionDefinition.Name)]
public sealed class C10ShareExpiryRestoreTests(StageBFixture fixture)
{
    private static readonly TimeSpan Settle = TimeSpan.FromSeconds(40);

    private string State(Guid share) =>
        fixture.Stack.AuthorityObserverSql($"SELECT resource_lifecycle_state FROM authority_resources WHERE resource_type_name = 'record_share' AND resource_entity_id = '{share:D}';").Trim();

    private int ShareExpiredEvents(Guid share) =>
        int.Parse(fixture.Stack.AuthorityObserverSql($"SELECT count(*) FROM security_journal_entries WHERE journal_event_kind = 'ShareExpired' AND journal_event_body_json LIKE '%{share:D}%';").Trim(), CultureInfo.InvariantCulture);

    /// <summary>Restore simulation only: a shell inside the authority-db container as the AuthorityDb administrator.</summary>
    private string AuthorityDbShell(string shell, int timeoutSeconds = 180)
    {
        var prefixed = "export PGUSER=\"$POSTGRES_USER\" PGPASSWORD=\"$POSTGRES_PASSWORD\" PGDATABASE=\"$POSTGRES_DB\"; " + shell;
        var encoded = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(prefixed));
        return fixture.Stack.Compose($"exec -T authority-db sh -c \"echo {encoded} | base64 -d | sh\"", timeoutSeconds);
    }

    private async Task<string> WaitStateAsync(Guid share, Func<string, bool> done, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        string state;
        while (!done(state = State(share)) && DateTime.UtcNow < deadline)
        {
            await Task.Delay(1000);
        }

        return state;
    }

    [Fact]
    public async Task ARestoreFromBeforeConvergenceLeavesTheShareDeniedAndReplayConvergesIt()
    {
        var s = new StageBScenario(fixture, "c10-expiry-restore");
        await s.BootstrapAsync();
        await s.BuildAccusedWorkspaceAsync();
        var record = await s.CreateAccusedAsync(s.Clerk);
        var expiresOn = DateTimeOffset.UtcNow.AddMinutes(61); // the Authority accepts one hour to 365 days ahead
        var created = await fixture.Api.PostAsync($"/api/v1/form-records/{record:D}/shares", s.Token(s.Clerk), new { useDevelopmentStepUp = true, recipientUserId = s.Reader.AuthorityUserId, recipientTeamId = (Guid?)null, recipientRole = "Reader", fieldKeys = new[] { "given_name" }, shareExpiresOn = expiresOn });
        Assert.True(created.IsSuccess, created.Body);
        var share = created.Root.GetProperty("recordShareId").GetGuid();
        await s.WaitGateReadyAsync(Settle);
        Assert.True((await s.WaitProjectionAsync(s.Reader, record, Settle)).IsSuccess);

        // The backup, taken while the share is Active and before its expiry.
        var dumpFile = $"/tmp/c10-expiry-restore-{share:N}.dump";
        Assert.Contains("DUMPED", AuthorityDbShell($"pg_dump -Fc -f {dumpFile} && echo DUMPED"), StringComparison.Ordinal);
        Assert.Equal("Active", State(share));

        // Natural expiry, then real-loop convergence (30 s passes).
        var wait = expiresOn - DateTimeOffset.UtcNow;
        if (wait > TimeSpan.Zero)
        {
            await Task.Delay(wait, TestContext.Current.CancellationToken);
        }

        Assert.True(Denied(await s.ProjectionAsync(s.Reader, record)));
        Assert.Equal("Expired", await WaitStateAsync(share, state => state == "Expired", TimeSpan.FromSeconds(120)));
        Assert.Equal(1, ShareExpiredEvents(share));
        var convergedAt = DateTimeOffset.UtcNow;

        // Restore the pre-convergence backup over the whole AuthorityDb, the Authority stopped around it.
        try
        {
            fixture.Stack.Stop("authority");
            var restored = AuthorityDbShell($"pg_restore --clean --if-exists --no-owner -d \"$POSTGRES_DB\" {dumpFile} 2>&1; echo RESTORED; rm -f {dumpFile}", 300);
            Assert.Contains("RESTORED", restored, StringComparison.Ordinal);
        }
        finally
        {
            fixture.Stack.Compose("up -d --wait authority", 240);
        }

        var restoredState = State(share);
        var afterRestore = await s.ProjectionAsync(s.Reader, record);
        Assert.Equal("Active", restoredState); // the backup's pre-convergence state
        Assert.True(afterRestore.Status is HttpStatusCode.Forbidden or HttpStatusCode.NotFound or HttpStatusCode.Locked or HttpStatusCode.ServiceUnavailable,
            $"the restored, pre-convergence share released access: {(int)afterRestore.Status} {afterRestore.Body}"); // denied or fail closed, never access

        // Replay converges it: the share reaches Expired again, without a recipient-visible change in between.
        var replayed = await WaitStateAsync(share, state => state == "Expired", TimeSpan.FromSeconds(180));
        var final = await s.ProjectionAsync(s.Reader, record);
        var gate = await fixture.Authority.GateAsync(s.OrganizationId);
        Assert.False(final.IsSuccess, final.Body);
        Assert.True(replayed == "Expired",
            $"§4 test 13: after the restore the share stayed {replayed} (denied: {(int)final.Status} {final.ErrorCode}); converged at {convergedAt:O}; gate {gate}; ShareExpired entries {ShareExpiredEvents(share)}");
    }

    private static bool Denied(HttpResult result) => result.Status is HttpStatusCode.Forbidden or HttpStatusCode.NotFound;
}
