using AuthorizationAuthority.Application.Common;
using Xunit;

namespace AuthorizationAuthority.Tests;

/// <summary>
/// C-150 R56-B: the work-loop wake-up is a coalescing hint. A signal raised before or during a pass ends the next wait at once (never
/// lost); many signals collapse into one pass (never queued without bound); without a signal the poll ends the wait (the durable
/// fallback); cancellation ends it. The loop's work is still read from durable state — nothing here decides what to process.
/// </summary>
public sealed class AuthorityWorkSignalTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ASignalRaisedBeforeTheWaitEndsItAtOnce()
    {
        using var signal = new AuthorityWorkSignal();
        signal.Signal(AuthorityWorkSignalSource.Commit);
        var clock = System.Diagnostics.Stopwatch.StartNew();
        Assert.True(await signal.WaitAsync(TimeSpan.FromSeconds(5), Ct));
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(1));
        Assert.Equal((1, 1, 0), (signal.CommitSignals, signal.Wakeups, signal.Polls));
    }

    [Fact]
    public async Task ManySignalsCoalesceIntoOnePassAndThePollIsTheFallback()
    {
        using var signal = new AuthorityWorkSignal();
        for (var i = 0; i < 100; i++)
        {
            signal.Signal(i % 2 == 0 ? AuthorityWorkSignalSource.Notification : AuthorityWorkSignalSource.Commit);
        }

        Assert.True(await signal.WaitAsync(TimeSpan.FromMilliseconds(250), Ct));
        Assert.False(await signal.WaitAsync(TimeSpan.FromMilliseconds(30), Ct)); // coalesced: no second wake-up; the poll ends the wait
        Assert.Equal((50, 50, 1, 1), (signal.CommitSignals, signal.NotificationSignals, signal.Wakeups, signal.Polls));
    }

    [Fact]
    public async Task ASignalDuringTheWaitWakesItPromptly()
    {
        using var signal = new AuthorityWorkSignal();
        var waiting = signal.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        await Task.Delay(50, Ct);
        Assert.False(waiting.IsCompleted);
        var clock = System.Diagnostics.Stopwatch.StartNew();
        signal.Signal(AuthorityWorkSignalSource.Reconnect);
        Assert.True(await waiting);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(1));
        Assert.Equal(1, signal.ReconnectSignals);
    }

    [Fact]
    public async Task SignalsFromManyThreadsAreNeverLostAcrossPasses()
    {
        using var signal = new AuthorityWorkSignal();
        using var stop = new CancellationTokenSource();
        var passes = 0;
        var loop = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                if (await signal.WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None))
                {
                    Interlocked.Increment(ref passes);
                }
            }
        }, Ct);

        // Each producer signals, then waits until a pass has started after its signal: a lost signal would stall it for the 5 s poll.
        var clock = System.Diagnostics.Stopwatch.StartNew();
        for (var i = 0; i < 200; i++)
        {
            var before = Volatile.Read(ref passes);
            signal.Signal(AuthorityWorkSignalSource.Commit);
            while (Volatile.Read(ref passes) == before)
            {
                await Task.Yield();
            }
        }

        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(4), "a signal was lost (a pass waited for the poll)");
        await stop.CancelAsync();
        signal.Signal(AuthorityWorkSignalSource.Commit);
        await loop;
    }

    [Fact]
    public async Task CancellationEndsTheWait()
    {
        using var signal = new AuthorityWorkSignal();
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => signal.WaitAsync(TimeSpan.FromSeconds(10), cancel.Token));
    }

    [Fact]
    public void TheWakeupIsOnByDefaultAndNamesAFixedChannel()
    {
        Assert.True(new AuthorityWorkOptions().CommitWakeupEnabled);
        Assert.Equal("authority_work", AuthorityWorkOptions.NotificationChannel);
    }
}
