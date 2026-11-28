using System.Globalization;
using System.Net;
using EnterpriseForms.SecurityTests.Harness;
using EnterpriseForms.StageBTests.Harness;
using Xunit;

namespace EnterpriseForms.StageBTests.Tests;

/// <summary>
/// Phase C10 WP-0 §4 tests 8, 10 and 13, and CONS-23 on a natural expiry, with real elapsed time (owner decisions 1 and 2, C-201).
/// Every share is granted or updated through the supported commands with a genuine expiry one hour ahead (the Authority's minimum),
/// expires naturally and converges through the real 30 s loop. No expiry value is written by SQL and no Testing clock exists.
/// <para>
/// Everything time-critical runs in one shared timeline, started by whichever test runs first, so the hour is waited once and the
/// order in which xUnit runs the tests cannot move an action after convergence. The timeline observes every outcome and the tests
/// assert what it captured. Its last step is §4 test 13: AuthorityDb is backed up with <c>pg_dump</c> before the instant and, after
/// every other observation, restored over the whole database with <c>pg_restore --clean</c>, the Authority stopped around it (the T16
/// restore technique). That rewrites AuthorityDb for every organization on the stack, so this class has a group and stack of its own.
/// </para>
/// </summary>
[Collection(StageBStackCollectionDefinition.Name)]
public sealed class C10ShareNaturalExpiryTests(StageBFixture fixture)
{
    private static readonly TimeSpan Settle = TimeSpan.FromSeconds(40);
    private static readonly TimeSpan Convergence = TimeSpan.FromSeconds(150); // five 30 s passes
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static Task<Timeline>? timeline;

    private sealed record RaceShares(HttpResult Revoke, HttpResult Update, HttpResult Transfer, string RevokedStateBeforeRevoke,
        string UpdatedFinal, string TransferredFinal, string RevokedFinal, int UpdatedExpired, int TransferredExpired, int RevokedExpired, string RevokedStateLater,
        bool DesignerDenied, bool AdministratorDenied);

    private sealed record InheritedShares(string DelegatedStateAtInstant, string[] DesignerKeysAtInstant,
        string ManagerFinal, string DelegatedFinal, string IndependentFinal, int ManagerExpired, int DelegatedExpired, string[] DesignerKeysAfter, bool ReaderDenied);

    private sealed record ExpiringDownload(long DocumentBytes, long ReceivedAtExpiry, long Received, bool Completed, string? Failure, TimeSpan EndedAfterExpiry, string[] FailureReasons, bool ReaderDenied);

    /// <summary>§4 test 13: the share's state in the restored backup, the access then, and whether replay converged it again.</summary>
    private sealed record RestoredShare(string ConvergedBeforeRestore, int ExpiredBeforeRestore, string StateAfterRestore, HttpStatusCode StatusAfterRestore, string? ErrorAfterRestore,
        string StateAfterReplay, HttpStatusCode StatusAfterReplay, string? ErrorAfterReplay, int ExpiredAfterReplay, string Gate);

    private sealed record Timeline(DateTimeOffset ExpiresOn, RaceShares Race, InheritedShares Inherited, ExpiringDownload Download, RestoredShare Restore);

    private static string Path(Guid record, string suffix = "") => $"/api/v1/form-records/{record:D}/shares{suffix}";

    private static async Task<Guid> ShareAsync(StageBScenario s, TestUser grantor, Guid record, TestUser recipient, string role, string[] fieldKeys, DateTimeOffset? expiresOn, Guid? parent = null)
    {
        var created = await s.Fixture.Api.PostAsync(Path(record), s.Token(grantor), new
        {
            useDevelopmentStepUp = true, recipientUserId = recipient.AuthorityUserId, recipientTeamId = (Guid?)null, recipientRole = role, fieldKeys,
            shareExpiresOn = expiresOn, parentRecordShareId = parent,
        });
        Assert.True(created.IsSuccess, created.Body);
        await s.WaitGateReadyAsync(Settle);
        return created.Root.GetProperty("recordShareId").GetGuid();
    }

    private static async Task<long> ShareRevisionAsync(StageBScenario s, Guid record, Guid share)
    {
        var details = await s.Fixture.Api.GetAsync(Path(record, $"/{share:D}"), s.Token(s.Clerk));
        Assert.True(details.IsSuccess, details.Body);
        return details.Root.GetProperty("shareRevision").GetInt64();
    }

    private static Task<HttpResult> UpdateShareAsync(StageBScenario s, Guid record, Guid share, long revision, string role, string[] fieldKeys, DateTimeOffset? expiresOn) =>
        s.Fixture.Api.SendAsync(HttpMethod.Patch, Path(record, $"/{share:D}"), s.Token(s.Clerk),
            new { recipientRole = role, fieldKeys, shareExpiresOn = expiresOn, noExpiry = expiresOn is null ? true : (bool?)null, useDevelopmentStepUp = true },
            Guid.NewGuid().ToString("N"), "\"" + share.ToString("D") + ":" + revision.ToString(CultureInfo.InvariantCulture) + "\"");

    private static string[] VisibleKeys(HttpResult projection) =>
        [.. projection.Root.GetProperty("record").GetProperty("values").EnumerateArray().Select(v => v.GetProperty("fieldKey").GetString()!).Order(StringComparer.Ordinal)];

    private static bool Denied(HttpResult result) => result.Status is HttpStatusCode.Forbidden or HttpStatusCode.NotFound;

    private static bool DeniedOrFailClosed(HttpStatusCode status) => status is HttpStatusCode.Forbidden or HttpStatusCode.NotFound or HttpStatusCode.Locked or HttpStatusCode.ServiceUnavailable;

    private string State(Guid share) =>
        fixture.Stack.AuthorityObserverSql($"SELECT resource_lifecycle_state FROM authority_resources WHERE resource_type_name = 'record_share' AND resource_entity_id = '{share:D}';").Trim();

    private int JournalEvents(string kind, Guid id) =>
        int.Parse(fixture.Stack.AuthorityObserverSql($"SELECT count(*) FROM security_journal_entries WHERE journal_event_kind = '{kind}' AND journal_event_body_json LIKE '%{id:D}%';").Trim(), CultureInfo.InvariantCulture);

    private async Task<string> WaitStateAsync(Guid share, Func<string, bool> done, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? Convergence);
        string state;
        while (!done(state = State(share)) && DateTime.UtcNow < deadline)
        {
            await Task.Delay(1000);
        }

        return state;
    }

    /// <summary>Restore simulation only (test 13): a shell inside the authority-db container as the AuthorityDb administrator.</summary>
    private string AuthorityDbShell(string shell, int timeoutSeconds = 180)
    {
        var prefixed = "export PGUSER=\"$POSTGRES_USER\" PGPASSWORD=\"$POSTGRES_PASSWORD\" PGDATABASE=\"$POSTGRES_DB\"; " + shell;
        var encoded = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(prefixed));
        return fixture.Stack.Compose($"exec -T authority-db sh -c \"echo {encoded} | base64 -d | sh\"", timeoutSeconds);
    }

    private string[] DownloadFailures(StageBScenario s, Guid record) =>
        fixture.Stack.AttackerSql($"SELECT event_metadata_json->>'reasonCode' FROM audit_events WHERE tenant_id = '{s.OrganizationId:D}' AND form_record_id = '{record:D}' AND event_action = 'DOCUMENT_DOWNLOAD_FAILED' ORDER BY audit_sequence;")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private async Task<(StageBScenario S, Guid Record)> AccusedAsync(string name)
    {
        var s = new StageBScenario(fixture, name);
        await s.BootstrapAsync();
        await s.BuildAccusedWorkspaceAsync();
        return (s, await s.CreateAccusedAsync(s.Clerk));
    }

    private async Task<Timeline> TimelineAsync()
    {
        await Gate.WaitAsync();
        try
        {
            return await (timeline ??= RunTimelineAsync());
        }
        finally
        {
            Gate.Release();
        }
    }

    private async Task<Timeline> RunTimelineAsync()
    {
        // One genuine expiry instant for every share: one hour ahead plus the setup time, re-checked by the Authority on every command.
        var expiresOn = DateTimeOffset.UtcNow.AddMinutes(68);

        // §4 test 8 setup: three shares of one record, each expiring at the instant.
        var (race, raceRecord) = await AccusedAsync("c10-natural-expiry-races");
        var revoked = await ShareAsync(race, race.Clerk, raceRecord, race.Reader, "Reader", ["given_name"], expiresOn);
        var updated = await ShareAsync(race, race.Clerk, raceRecord, race.DesignerOnly, "Reader", ["given_name"], expiresOn);
        var transferred = await ShareAsync(race, race.Clerk, raceRecord, race.Administrator, "Reader", ["family_name"], expiresOn);
        var updatedRevision = await ShareRevisionAsync(race, raceRecord, updated);

        // §4 test 10 setup: a Manager share and its delegated share, then the parent's expiry brought earlier than the delegated
        // share's by an ordinary update (a descendant's effective expiry is the lineage minimum), and an independent grant.
        var (inherited, inheritedRecord) = await AccusedAsync("c10-natural-expiry-inherited");
        var manager = await ShareAsync(inherited, inherited.Clerk, inheritedRecord, inherited.Reader, "Manager", ["given_name", "family_name"], expiresOn.AddHours(2));
        await inherited.WaitProjectionAsync(inherited.Reader, inheritedRecord, Settle);
        var delegated = await ShareAsync(inherited, inherited.Reader, inheritedRecord, inherited.DesignerOnly, "Reader", ["given_name"], expiresOn.AddHours(1), manager);
        var independent = await ShareAsync(inherited, inherited.Clerk, inheritedRecord, inherited.DesignerOnly, "Reader", ["family_name"], null);
        var earlier = await UpdateShareAsync(inherited, inheritedRecord, manager, await ShareRevisionAsync(inherited, inheritedRecord, manager), "Manager", ["given_name", "family_name"], expiresOn); // same role and scope: nothing narrows
        Assert.True(earlier.IsSuccess, earlier.Body);
        await inherited.WaitGateReadyAsync(Settle);
        Assert.Equal(["family_name", "given_name"], VisibleKeys(await inherited.WaitProjectionAsync(inherited.DesignerOnly, inheritedRecord, Settle)));

        // CONS-23 with natural expiry: a share recipient's large download is opened before the instant and paused (the server's writes
        // block on full transport buffers); resumed after the instant, it must stop at the next re-check, audited.
        var (dl, dlRecord) = await AccusedAsync("c10-natural-expiry-download");
        const int documentBytes = 40 * 1024 * 1024;
        var document = await dl.UploadAsync(dl.Clerk, dlRecord, "cons23-expiry.bin", "application/octet-stream", StageBScenario.SyntheticBytes(documentBytes, 241), timeout: TimeSpan.FromMinutes(5));
        var attachment = document.GetProperty("fileAttachmentId").GetGuid();
        var dlShare = await dl.Fixture.Api.PostAsync(Path(dlRecord), dl.Token(dl.Clerk), new
        {
            useDevelopmentStepUp = true, recipientUserId = dl.Reader.AuthorityUserId, recipientTeamId = (Guid?)null, recipientRole = "Reader", fieldKeys = new[] { "given_name" },
            documents = new[] { new { fileAttachmentId = attachment, canDownloadDocument = true, canReplaceDocument = false } }, shareExpiresOn = expiresOn,
        });
        Assert.True(dlShare.IsSuccess, dlShare.Body);
        await dl.WaitGateReadyAsync(Settle);
        await dl.WaitProjectionAsync(dl.Reader, dlRecord, Settle);

        // §4 test 13 setup: one more share expiring at the instant, then the AuthorityDb backup, taken before any convergence.
        var (restore, restoreRecord) = await AccusedAsync("c10-natural-expiry-restore");
        var restoreShare = await ShareAsync(restore, restore.Clerk, restoreRecord, restore.Reader, "Reader", ["given_name"], expiresOn);
        await restore.WaitProjectionAsync(restore.Reader, restoreRecord, Settle);
        var dumpFile = $"/tmp/c10-natural-expiry-{restoreShare:N}.dump";
        Assert.Contains("DUMPED", AuthorityDbShell($"pg_dump -Fc -f {dumpFile} && echo DUMPED"), StringComparison.Ordinal);
        Assert.True(expiresOn - DateTimeOffset.UtcNow > TimeSpan.FromMinutes(1), "setup took too long: the expiry instant is too close");

        // The natural wait (the download opens 15 s before the instant).
        await Task.Delay(expiresOn.AddSeconds(-15) - DateTimeOffset.UtcNow);
        using var download = await fixture.StartPausedDownloadAsync($"/api/v1/form-records/{dlRecord:D}/documents/{attachment:D}/download", dl.Token(dl.Reader), 1024 * 1024);
        Assert.Equal(HttpStatusCode.OK, download.Status);
        await Task.Delay(expiresOn.AddMilliseconds(300) - DateTimeOffset.UtcNow);

        // At the instant: test 10 first observes the inherited denial, then test 8 races the loop.
        var delegatedAtInstant = State(delegated);
        var designerKeys = VisibleKeys(await inherited.WaitProjectionAsync(inherited.DesignerOnly, inheritedRecord, Settle));
        var revokedBefore = State(revoked);
        var revoke = await race.Fixture.Api.PostAsync(Path(raceRecord, $"/{revoked:D}/revoke?useDevelopmentStepUp=true"), race.Token(race.Clerk), new { });
        var update = await UpdateShareAsync(race, raceRecord, updated, updatedRevision, "Reader", ["family_name"], null);
        var transfer = await race.Fixture.Api.PostAsync($"/api/v1/form-records/{raceRecord:D}/transfer-ownership", race.Token(race.Clerk),
            new { useDevelopmentStepUp = true, newOwnerUserId = race.Reader.AuthorityUserId, newOwnerTeamId = (Guid?)null, transferReason = "C10 Stage B natural-expiry transfer" });
        await race.WaitGateReadyAsync(Settle);

        // CONS-23: resume the paused download.
        var receivedAtExpiry = download.Received;
        await download.ReadRestAsync();
        var endedAfterExpiry = DateTimeOffset.UtcNow - expiresOn;
        var expiringDownload = new ExpiringDownload(documentBytes, receivedAtExpiry, download.Received, download.Completed, download.Failure, endedAfterExpiry,
            DownloadFailures(dl, dlRecord), Denied(await dl.ProjectionAsync(dl.Reader, dlRecord)));

        // Real-loop convergence of every share; the test 8 and 10 observations are captured before the restore rewrites AuthorityDb.
        var updatedFinal = await WaitStateAsync(updated, x => x == "Expired");
        var transferredFinal = await WaitStateAsync(transferred, x => x == "Expired");
        var revokedFinal = await WaitStateAsync(revoked, x => x is "Revoked" or "Expired");
        var managerFinal = await WaitStateAsync(manager, x => x == "Expired");
        var delegatedFinal = await WaitStateAsync(delegated, x => x != "Active");
        var restoreConverged = await WaitStateAsync(restoreShare, x => x == "Expired");
        await Task.Delay(TimeSpan.FromSeconds(65)); // two more passes: nothing is recorded twice
        var raceShares = new RaceShares(revoke, update, transfer, revokedBefore,
            updatedFinal, transferredFinal, revokedFinal, JournalEvents("ShareExpired", updated), JournalEvents("ShareExpired", transferred), JournalEvents("ShareExpired", revoked), State(revoked),
            Denied(await race.ProjectionAsync(race.DesignerOnly, raceRecord)), Denied(await race.ProjectionAsync(race.Administrator, raceRecord)));
        var inheritedShares = new InheritedShares(delegatedAtInstant, designerKeys,
            managerFinal, delegatedFinal, State(independent), JournalEvents("ShareExpired", manager), JournalEvents("ShareExpired", delegated),
            VisibleKeys(await inherited.WaitProjectionAsync(inherited.DesignerOnly, inheritedRecord, Settle)), Denied(await inherited.ProjectionAsync(inherited.Reader, inheritedRecord)));
        var expiredBeforeRestore = JournalEvents("ShareExpired", restoreShare);

        // §4 test 13: restore the pre-convergence backup over the whole AuthorityDb, the Authority stopped around it.
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

        var stateAfterRestore = State(restoreShare);
        var afterRestore = await restore.ProjectionAsync(restore.Reader, restoreRecord);
        var stateAfterReplay = await WaitStateAsync(restoreShare, x => x == "Expired", TimeSpan.FromSeconds(180));
        var afterReplay = await restore.ProjectionAsync(restore.Reader, restoreRecord);
        var gate = await fixture.Authority.GateAsync(restore.OrganizationId);
        var restoredShare = new RestoredShare(restoreConverged, expiredBeforeRestore, stateAfterRestore, afterRestore.Status, afterRestore.ErrorCode,
            stateAfterReplay, afterReplay.Status, afterReplay.ErrorCode, JournalEvents("ShareExpired", restoreShare), gate.ToString());

        return new Timeline(expiresOn, raceShares, inheritedShares, expiringDownload, restoredShare);
    }

    [Fact]
    public async Task ExpiryRacingARevokeAnUpdateAndATransferHasExactlyOneOutcome()
    {
        // §4 test 8: after the natural expiry instant, a revoke, an update and a transfer race the real convergence loop. Each share ends
        // in exactly one terminal state, recorded once: the revoked share is Revoked with no ShareExpired if the revoke reached it first,
        // or Expired once with the revoke refused if the loop did; the update is refused and the share converges to Expired once; the
        // transfer neither revives nor blocks the durable share, which converges to Expired once.
        var r = (await TimelineAsync()).Race;
        TestContext.Current.SendDiagnosticMessage($"§4 test 8: the revoked share was {r.RevokedStateBeforeRevoke} at the revoke, revoke {(int)r.Revoke.Status}, final {r.RevokedFinal}");
        Assert.False(r.Update.IsSuccess, r.Update.Body); // an expired share has no active lineage to update
        Assert.True(r.Transfer.IsSuccess, r.Transfer.Body);
        Assert.Equal("Expired", r.UpdatedFinal);
        Assert.Equal("Expired", r.TransferredFinal);
        if (r.RevokedStateBeforeRevoke == "Active" && r.Revoke.IsSuccess)
        {
            Assert.Equal("Revoked", r.RevokedFinal); // the revoke won
            Assert.Equal(0, r.RevokedExpired);
        }
        else
        {
            Assert.Equal("Expired", r.RevokedFinal); // the loop won; the revoke was refused
            Assert.False(r.Revoke.IsSuccess, r.Revoke.Body);
            Assert.Equal(1, r.RevokedExpired);
        }

        Assert.Equal(1, r.UpdatedExpired);
        Assert.Equal(1, r.TransferredExpired);
        Assert.Equal(r.RevokedFinal, r.RevokedStateLater);
        Assert.True(r.DesignerDenied);
        Assert.True(r.AdministratorDenied);
    }

    [Fact]
    public async Task AParentsNaturalExpiryDeniesTheDelegatedShareAtOnceInvalidatesItOnConvergenceAndAnIndependentGrantKeepsItsAccess()
    {
        // §4 test 10: at the instant the delegated share is denied through its lineage (only the independent grant's field remains), on
        // real-loop convergence the parent is Expired once and the delegated share Invalidated, and the independent grant keeps access.
        var i = (await TimelineAsync()).Inherited;
        TestContext.Current.SendDiagnosticMessage($"§4 test 10: the delegated share was {i.DelegatedStateAtInstant} at the instant");
        Assert.Equal(["family_name"], i.DesignerKeysAtInstant);
        Assert.Equal("Expired", i.ManagerFinal);
        Assert.Equal("Invalidated", i.DelegatedFinal);
        Assert.Equal("Active", i.IndependentFinal);
        Assert.Equal(1, i.ManagerExpired);
        Assert.Equal(0, i.DelegatedExpired);
        Assert.Equal(["family_name"], i.DesignerKeysAfter);
        Assert.True(i.ReaderDenied);
    }

    [Fact]
    public async Task ARestoreFromBeforeConvergenceLeavesTheShareDeniedAndReplayConvergesIt()
    {
        // §4 test 13: "Restore an AuthorityDb backup taken before convergence => the share stays denied; replay converges it."
        var t = (await TimelineAsync()).Restore;
        var observed = $"converged {t.ConvergedBeforeRestore} ({t.ExpiredBeforeRestore} ShareExpired); after restore {t.StateAfterRestore}, access {(int)t.StatusAfterRestore} {t.ErrorAfterRestore}; after the replay wait {t.StateAfterReplay}, access {(int)t.StatusAfterReplay} {t.ErrorAfterReplay}, ShareExpired {t.ExpiredAfterReplay}; gate {t.Gate}";
        TestContext.Current.SendDiagnosticMessage("§4 test 13: " + observed);
        Assert.Equal("Expired", t.ConvergedBeforeRestore);
        Assert.Equal(1, t.ExpiredBeforeRestore);
        Assert.Equal("Active", t.StateAfterRestore); // the backup's pre-convergence state
        Assert.True(DeniedOrFailClosed(t.StatusAfterRestore), "the restored share released access: " + observed);
        Assert.True(DeniedOrFailClosed(t.StatusAfterReplay), "access after the restore: " + observed);
        Assert.True(t.StateAfterReplay == "Expired", "§4 test 13: replay did not converge the restored share. " + observed);
    }

    [Fact]
    public async Task ADownloadInProgressStopsAtTheNextRecheckAfterItsSharesNaturalExpiry()
    {
        // CONS-23 with real elapsed time: the recipient's download, opened before the expiry instant, stops within the re-check bound
        // (5 s) plus the time to drain the transport buffers after the instant, with one audited DOCUMENT_DOWNLOAD_FAILED.
        var d = (await TimelineAsync()).Download;
        var observed = $"received {d.Received} of {d.DocumentBytes} bytes ({d.Received - d.ReceivedAtExpiry} after the instant), ended {d.EndedAfterExpiry.TotalSeconds:F1} s after the instant, failure '{d.Failure}', reasons [{string.Join(", ", d.FailureReasons)}]";
        TestContext.Current.SendDiagnosticMessage("CONS-23 natural expiry: " + observed);
        Assert.False(d.Completed, "CONS-23: the download continued past the share's expiry. " + observed);
        Assert.True(d.Received < d.DocumentBytes, observed);
        Assert.True(d.EndedAfterExpiry <= TimeSpan.FromSeconds(45), observed); // the race steps above run first (about 10 s), then 5 s + drain
        Assert.Single(d.FailureReasons);
        Assert.True(d.FailureReasons[0] is not ("AUTHORITY_UNAVAILABLE" or "INTEGRITY_VERIFICATION_FAILED"), observed);
        Assert.True(d.ReaderDenied);
    }
}
