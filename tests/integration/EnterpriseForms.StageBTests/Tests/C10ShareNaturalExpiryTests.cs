using System.Globalization;
using System.Net;
using EnterpriseForms.SecurityTests.Harness;
using EnterpriseForms.StageBTests.Harness;
using Xunit;

namespace EnterpriseForms.StageBTests.Tests;

/// <summary>
/// Phase C10 WP-0 §4 tests 8 and 10 with real elapsed time (owner decision 2, C-201): every share is granted or updated through the
/// supported commands with a genuine expiry one hour ahead (the Authority's minimum), expires naturally and converges through the real
/// 30 s loop. No expiry value is written by SQL; no Testing clock exists. The time-critical steps of both tests (the actions at the
/// expiry instant) run in one shared timeline, started by whichever test runs first, so the hour is waited once and the order in which
/// xUnit runs the two tests cannot move an action after convergence. Each test then asserts its own outcome.
/// </summary>
[Collection(StageBStackCollectionDefinition.Name)]
public sealed class C10ShareNaturalExpiryTests(StageBFixture fixture)
{
    private static readonly TimeSpan Settle = TimeSpan.FromSeconds(40);
    private static readonly TimeSpan Convergence = TimeSpan.FromSeconds(150); // five 30 s passes
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static Task<Timeline>? timeline;

    private sealed record RaceShares(StageBScenario S, Guid Record, Guid Revoked, Guid Updated, Guid Transferred, string RevokedStateBeforeRevoke, HttpResult Revoke, HttpResult Update, HttpResult Transfer);

    private sealed record InheritedShares(StageBScenario S, Guid Record, Guid Manager, Guid Delegated, Guid Independent, string DelegatedStateAtInstant, string[] DesignerKeysAtInstant);

    private sealed record ExpiringDownload(StageBScenario S, Guid Record, long DocumentBytes, long ReceivedAtExpiry, long Received, bool Completed, string? Failure, TimeSpan EndedAfterExpiry, string[] FailureReasons);

    private sealed record Timeline(DateTimeOffset ExpiresOn, RaceShares Race, InheritedShares Inherited, ExpiringDownload Download);

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

    private string State(Guid share) =>
        fixture.Stack.AuthorityObserverSql($"SELECT resource_lifecycle_state FROM authority_resources WHERE resource_type_name = 'record_share' AND resource_entity_id = '{share:D}';").Trim();

    private int JournalEvents(string kind, Guid id) =>
        int.Parse(fixture.Stack.AuthorityObserverSql($"SELECT count(*) FROM security_journal_entries WHERE journal_event_kind = '{kind}' AND journal_event_body_json LIKE '%{id:D}%';").Trim(), CultureInfo.InvariantCulture);

    private async Task<string> WaitStateAsync(Guid share, Func<string, bool> done)
    {
        var deadline = DateTime.UtcNow + Convergence;
        string state;
        while (!done(state = State(share)) && DateTime.UtcNow < deadline)
        {
            await Task.Delay(1000);
        }

        return state;
    }

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
        // One genuine expiry instant for both tests: one hour ahead plus the setup time, re-checked by the Authority on every command.
        var expiresOn = DateTimeOffset.UtcNow.AddMinutes(66);

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

        var receivedAtExpiry = download.Received;
        await download.ReadRestAsync();
        var endedAfterExpiry = DateTimeOffset.UtcNow - expiresOn;
        var reasons = fixture.Stack.AttackerSql($"SELECT event_metadata_json->>'reasonCode' FROM audit_events WHERE tenant_id = '{dl.OrganizationId:D}' AND form_record_id = '{dlRecord:D}' AND event_action = 'DOCUMENT_DOWNLOAD_FAILED' ORDER BY audit_sequence;")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        return new Timeline(expiresOn,
            new RaceShares(race, raceRecord, revoked, updated, transferred, revokedBefore, revoke, update, transfer),
            new InheritedShares(inherited, inheritedRecord, manager, delegated, independent, delegatedAtInstant, designerKeys),
            new ExpiringDownload(dl, dlRecord, documentBytes, receivedAtExpiry, download.Received, download.Completed, download.Failure, endedAfterExpiry, reasons));
    }

    [Fact]
    public async Task ExpiryRacingARevokeAnUpdateAndATransferHasExactlyOneOutcome()
    {
        // §4 test 8: after the natural expiry instant, a revoke, an update and a transfer race the real convergence loop. Each share ends
        // in exactly one terminal state, recorded once: the revoked share is Revoked with no ShareExpired if the revoke reached it first,
        // or Expired once with the revoke refused if the loop did; the update is refused and the share converges to Expired once; the
        // transfer neither revives nor blocks the durable share, which converges to Expired once.
        var t = await TimelineAsync();
        var r = t.Race;
        Assert.False(r.Update.IsSuccess, r.Update.Body); // an expired share has no active lineage to update
        Assert.True(r.Transfer.IsSuccess, r.Transfer.Body);

        Assert.Equal("Expired", await WaitStateAsync(r.Updated, x => x == "Expired"));
        Assert.Equal("Expired", await WaitStateAsync(r.Transferred, x => x == "Expired"));
        var revokedFinal = await WaitStateAsync(r.Revoked, x => x is "Revoked" or "Expired");
        if (r.RevokedStateBeforeRevoke == "Active" && r.Revoke.IsSuccess)
        {
            Assert.Equal("Revoked", revokedFinal); // the revoke won
            Assert.Equal(0, JournalEvents("ShareExpired", r.Revoked));
        }
        else
        {
            Assert.Equal("Expired", revokedFinal); // the loop won; the revoke was refused
            Assert.False(r.Revoke.IsSuccess, r.Revoke.Body);
            Assert.Equal(1, JournalEvents("ShareExpired", r.Revoked));
        }

        // A few more passes: nothing is recorded twice.
        await Task.Delay(TimeSpan.FromSeconds(65), TestContext.Current.CancellationToken);
        Assert.Equal(1, JournalEvents("ShareExpired", r.Updated));
        Assert.Equal(1, JournalEvents("ShareExpired", r.Transferred));
        Assert.Equal(revokedFinal, State(r.Revoked));
        Assert.True(Denied(await r.S.ProjectionAsync(r.S.DesignerOnly, r.Record)));
        Assert.True(Denied(await r.S.ProjectionAsync(r.S.Administrator, r.Record)));
    }

    [Fact]
    public async Task AParentsNaturalExpiryDeniesTheDelegatedShareAtOnceInvalidatesItOnConvergenceAndAnIndependentGrantKeepsItsAccess()
    {
        // §4 test 10: at the instant the delegated share is denied through its lineage (only the independent grant's field remains), on
        // real-loop convergence the parent is Expired once and the delegated share Invalidated, and the independent grant keeps access.
        var t = await TimelineAsync();
        var i = t.Inherited;
        Assert.Equal(["family_name"], i.DesignerKeysAtInstant);

        Assert.Equal("Expired", await WaitStateAsync(i.Manager, x => x == "Expired"));
        Assert.Equal("Invalidated", await WaitStateAsync(i.Delegated, x => x != "Active"));
        Assert.Equal("Active", State(i.Independent));
        Assert.Equal(1, JournalEvents("ShareExpired", i.Manager));
        Assert.Equal(0, JournalEvents("ShareExpired", i.Delegated));
        Assert.Equal(["family_name"], VisibleKeys(await i.S.WaitProjectionAsync(i.S.DesignerOnly, i.Record, Settle)));
        Assert.True(Denied(await i.S.ProjectionAsync(i.S.Reader, i.Record)));
    }

    [Fact]
    public async Task ADownloadInProgressStopsAtTheNextRecheckAfterItsSharesNaturalExpiry()
    {
        // CONS-23 with real elapsed time: the recipient's download, opened before the expiry instant, stops within the re-check bound
        // (5 s) plus the time to drain the transport buffers after the instant, with one audited DOCUMENT_DOWNLOAD_FAILED.
        var t = await TimelineAsync();
        var d = t.Download;
        var observed = $"received {d.Received} of {d.DocumentBytes} bytes ({d.Received - d.ReceivedAtExpiry} after the instant), ended {d.EndedAfterExpiry.TotalSeconds:F1} s after the instant, failure '{d.Failure}', reasons [{string.Join(", ", d.FailureReasons)}]";
        TestContext.Current.SendDiagnosticMessage("CONS-23 natural expiry: " + observed);
        Assert.False(d.Completed, "CONS-23: the download continued past the share's expiry. " + observed);
        Assert.True(d.Received < d.DocumentBytes, observed);
        Assert.True(d.EndedAfterExpiry <= TimeSpan.FromSeconds(45), observed); // the race steps above run first (about 10 s), then 5 s + drain
        Assert.Single(d.FailureReasons);
        Assert.NotEqual("AUTHORITY_UNAVAILABLE", d.FailureReasons[0]);
        Assert.True(Denied(await d.S.ProjectionAsync(d.S.Reader, d.Record)));
    }
}
