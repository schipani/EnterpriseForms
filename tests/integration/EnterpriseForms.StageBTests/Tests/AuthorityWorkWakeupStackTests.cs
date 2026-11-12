using System.Text.Json;
using EnterpriseForms.SecurityTests.Harness;
using EnterpriseForms.StageBTests.Harness;
using Xunit;

namespace EnterpriseForms.StageBTests.Tests;

/// <summary>
/// C-150 R56-B (and the R56-A revocation race), focused stack tests on the security-test stack:
/// <list type="bullet">
/// <item>a committed authorization change wakes the Authority's work loop through the PostgreSQL notification (signal and notification
/// counters move; the gate reopens well inside the old 250 ms poll), while a content edit sends no wake-up;</item>
/// <item>a killed listener connection loses notifications harmlessly: the poll still reopens the gate, and the listener reconnects;</item>
/// <item>after an Authority restart the listener is back and changes still wake the loop;</item>
/// <item>under the business API's bounded retry, no search that started after a revocation was confirmed returns the revoked record,
/// and none that ended before a share was requested returns the not-yet-shared one.</item>
/// </list>
/// Measurements of the gate-closed time with and without the wake-up are in SearchAvailabilityUnderPermissionChurnTests (Benchmark).
/// </summary>
[Collection(StageBStackCollectionDefinition.Name)]
public sealed class AuthorityWorkWakeupStackTests(StageBFixture fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static long Counter(JsonElement counters, string name) => counters.GetProperty(name).GetInt64();

    private static double Median(List<double> values)
    {
        var sorted = values.Order().ToList();
        return sorted[sorted.Count / 2];
    }

    [Fact]
    public async Task ACommittedChangeWakesTheLoopThroughTheNotificationAndAContentEditDoesNot()
    {
        var w = await C150SearchWorkspace.CreateAsync(fixture, "c150-wake", 4);
        var before = await w.WorkCountersAsync();
        Assert.True(before.GetProperty("commit_wakeup_enabled").GetBoolean());
        Assert.True(before.GetProperty("listener_connected").GetBoolean(), "the notification listener is not connected: " + before);

        var reopened = new List<double>();
        for (var i = 0; i < 12; i++)
        {
            await w.ToggleAsync(w.Records[i % w.Records.Count]);
            reopened.Add(await w.TimeToGateReadyAsync());
        }

        var after = await w.WorkCountersAsync();
        Assert.True(Counter(after, "commit_signals") - Counter(before, "commit_signals") >= 12, "commit signals: " + after);
        Assert.True(Counter(after, "notification_signals") - Counter(before, "notification_signals") >= 12, "notifications: " + after);
        Assert.True(Counter(after, "wakeups") > Counter(before, "wakeups"));
        // The time from the command's response to the reopened gate: well inside the old idle poll (250 ms) at the median.
        Assert.True(Median(reopened) < 250, "median time to the reopened gate: " + Median(reopened) + " ms; all: " + string.Join(", ", reopened.Select(r => Math.Round(r))));

        // A content edit appends no authorization-stream entry: no commit signal.
        var quiet = await w.WorkCountersAsync();
        var record = w.Records[0];
        var revision = (await w.S.WaitProjectionAsync(w.S.Clerk, record)).Root.GetProperty("record").GetProperty("contentRevision").GetInt64();
        var edited = await C150SearchWorkspace.RetryingAsync(() => fixture.Api.PatchAsync($"/api/v1/form-records/{record:D}", w.S.Token(w.S.Clerk), new { expectedContentRevision = revision, values = new Dictionary<string, string> { ["exhibit_count"] = "41" } }));
        Assert.True(edited.IsSuccess, edited.Body);
        await w.S.WaitProjectionAsync(w.S.Clerk, record);
        Assert.Equal(Counter(quiet, "commit_signals"), Counter(await w.WorkCountersAsync(), "commit_signals"));
    }

    [Fact]
    public async Task ALostListenerConnectionFallsBackToThePollAndTheListenerReconnects()
    {
        var w = await C150SearchWorkspace.CreateAsync(fixture, "c150-lost", 2);
        var before = await w.WorkCountersAsync();
        Assert.True(before.GetProperty("listener_connected").GetBoolean());

        // Terminate the LISTEN session (as a network failure would); a change committed meanwhile must still become effective (the poll).
        var killed = fixture.Stack.AuthorityAdministratorSql("SELECT count(pg_terminate_backend(pid)) FROM pg_stat_activity WHERE application_name = 'authority-work-listener';").Trim();
        Assert.Equal("1", killed);
        await w.ToggleAsync(w.Records[0]);
        Assert.True(await w.TimeToGateReadyAsync(pollMilliseconds: 10, timeoutSeconds: 10) < 10_000);

        var deadline = DateTime.UtcNow.AddSeconds(30);
        JsonElement counters;
        while (!(counters = await w.WorkCountersAsync()).GetProperty("listener_connected").GetBoolean() || Counter(counters, "reconnect_signals") <= Counter(before, "reconnect_signals"))
        {
            Assert.True(DateTime.UtcNow < deadline, "the listener did not reconnect: " + counters);
            await Task.Delay(200, Ct);
        }

        var notified = Counter(counters, "notification_signals");
        await w.ToggleAsync(w.Records[1]);
        await w.TimeToGateReadyAsync();
        Assert.True(Counter(await w.WorkCountersAsync(), "notification_signals") > notified, "notifications resume after the reconnection");
    }

    [Fact]
    public async Task TheWakeupIsBackAfterAnAuthorityRestart()
    {
        var w = await C150SearchWorkspace.CreateAsync(fixture, "c150-restart", 2);
        fixture.Stack.Compose("restart authority", 180);
        await fixture.Authority.WaitReadyAsync(TimeSpan.FromSeconds(90));
        var deadline = DateTime.UtcNow.AddSeconds(30);
        JsonElement counters;
        while (!(counters = await w.WorkCountersAsync()).GetProperty("listener_connected").GetBoolean())
        {
            Assert.True(DateTime.UtcNow < deadline, "no listener after the restart: " + counters);
            await Task.Delay(200, Ct);
        }

        await w.S.WaitGateReadyAsync(TimeSpan.FromSeconds(60));
        await w.ToggleAsync(w.Records[0]);
        Assert.True(await w.TimeToGateReadyAsync() < 10_000);
        var after = await w.WorkCountersAsync();
        Assert.True(Counter(after, "notification_signals") >= 1 && Counter(after, "commit_signals") >= 1, "after the restart: " + after);
    }

    [Fact]
    public async Task UnderBoundedRetryNoSearchDisclosesAStaleAuthorizationResult()
    {
        // R56-A with R56-B: the Reader searches continuously while one record's share is toggled. A search that started after a
        // revocation was confirmed never returns the record; one that ended before a share was requested never returns it either.
        var w = await C150SearchWorkspace.CreateAsync(fixture, "c150-race", 3);
        var target = w.Records[1];
        var digest = await w.SessionAsync(w.S.Reader);
        using var http = new HttpClient { BaseAddress = new Uri(StageBFixture.ApiBaseAddress), Timeout = TimeSpan.FromMinutes(1) };
        var windows = new List<(bool SharedAfter, DateTimeOffset Requested, DateTimeOffset Confirmed)>();
        var outcomes = new List<C150SearchWorkspace.SearchOutcome>();
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var searcher = Task.Run(async () =>
        {
            var random = new Random(150);
            while (!stop.IsCancellationRequested)
            {
                outcomes.Add(await w.SearchAsync(http, w.S.Reader, digest, 0, 100));
                await Task.Delay(random.Next(0, 60), CancellationToken.None);
            }
        }, CancellationToken.None);

        for (var i = 0; i < 16; i++)
        {
            windows.Add(await w.ToggleAsync(target));
            await Task.Delay(250 + (i % 4 * 150), Ct);
        }

        await stop.CancelAsync();
        await searcher;

        // The record's share state over time: known between commands, uncertain while a command commits (Requested..Confirmed).
        var intervals = new List<(DateTimeOffset From, DateTimeOffset To, bool? Shared)>();
        var state = false;
        var last = DateTimeOffset.MinValue;
        foreach (var window in windows)
        {
            intervals.Add((last, window.Requested, state));
            intervals.Add((window.Requested, window.Confirmed, null));
            state = window.SharedAfter;
            last = window.Confirmed;
        }

        intervals.Add((last, DateTimeOffset.MaxValue, state));

        bool? Shared(DateTimeOffset from, DateTimeOffset to)
        {
            bool? constant = null;
            foreach (var (_, _, shared) in intervals.Where(k => k.To > from && k.From < to))
            {
                if (shared is null || (constant is { } c && c != shared))
                {
                    return null;
                }

                constant = shared;
            }

            return constant;
        }

        var answered = outcomes.Where(o => o.Ids is not null).ToList();
        var violations = answered.Select(o => (o, definitely: Shared(o.Started, o.Ended)))
            .Where(x => (x.definitely == false && x.o.Ids!.Contains(target)) || (x.definitely == true && !x.o.Ids!.Contains(target)))
            .Select(x => $"{x.o.Started:O}..{x.o.Ended:O} shared={x.definitely} returned={x.o.Ids!.Contains(target)}").ToList();
        Assert.True(violations.Count == 0, string.Join("; ", violations.Take(5)));
        Assert.True(answered.Count >= 10, $"too few answered searches to judge: {answered.Count} of {outcomes.Count}");
        Assert.All(outcomes.Where(o => o.Ids is null), o => Assert.Contains(o.Code, new[] { "SEARCH_SNAPSHOT_CHANGED", "AUTHORITY_NOT_READY", "SEARCH_VERIFICATION_UNAVAILABLE" }));
    }
}
