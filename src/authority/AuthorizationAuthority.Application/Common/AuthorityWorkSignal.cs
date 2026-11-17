namespace AuthorizationAuthority.Application.Common;

/// <summary>C-150 R56-B configuration, section <c>AuthorityWork</c>.</summary>
public sealed class AuthorityWorkOptions
{
    public const string SectionName = "AuthorityWork";

    /// <summary>The PostgreSQL notification channel committed authorization changes notify (every Authority instance listens).</summary>
    public const string NotificationChannel = "authority_work";

    /// <summary>
    /// On by default: a committed authorization change wakes the work loop of every Authority instance at once, instead of at its next
    /// 250 ms poll. Off: the poll alone (the pre-C-150 behaviour; used to measure the difference).
    /// </summary>
    public bool CommitWakeupEnabled { get; set; } = true;
}

/// <summary>
/// C-150 R56-B: a coalescing wake-up for <c>AuthorityWorkLoop</c>. A wake-up is only a hint to look for work now: the loop still finds
/// its work from durable state (organizations whose projection is behind, journal entries not yet archived), exactly as on a poll, and
/// keeps its poll as the fallback. A signal never says that work exists or has completed, so a lost, duplicated or spurious signal can
/// only change <i>when</i> the loop looks, never what it does or what the organization gate decides.
/// <para>
/// Sources: the committing scope after a transaction that appended to the authorization stream (this instance, after the commit
/// returned), the PostgreSQL notification listener (every instance, delivered by PostgreSQL only for committed transactions) and the
/// listener's (re)connection (a notification may have been missed while it was disconnected). Signals while the loop is busy are
/// coalesced into one immediate pass: never lost, never queued without bound.
/// </para>
/// </summary>
public sealed class AuthorityWorkSignal : IDisposable
{
    private readonly SemaphoreSlim wake = new(0, 1);
    private long commitSignals;
    private long notificationSignals;
    private long reconnectSignals;
    private long wakeups;
    private long polls;

    public long CommitSignals => Interlocked.Read(ref commitSignals);

    public long NotificationSignals => Interlocked.Read(ref notificationSignals);

    public long ReconnectSignals => Interlocked.Read(ref reconnectSignals);

    /// <summary>Loop waits that ended because of a signal.</summary>
    public long Wakeups => Interlocked.Read(ref wakeups);

    /// <summary>Loop waits that ended at the poll interval.</summary>
    public long Polls => Interlocked.Read(ref polls);

    /// <summary>Whether the notification listener currently holds its LISTEN connection (observability only).</summary>
    public bool ListenerConnected { get; set; }

    public void Signal(AuthorityWorkSignalSource source)
    {
        switch (source)
        {
            case AuthorityWorkSignalSource.Commit:
                Interlocked.Increment(ref commitSignals);
                break;
            case AuthorityWorkSignalSource.Notification:
                Interlocked.Increment(ref notificationSignals);
                break;
            default:
                Interlocked.Increment(ref reconnectSignals);
                break;
        }

        try
        {
            wake.Release();
        }
        catch (SemaphoreFullException)
        {
            // already signalled: coalesced into the pending wake-up
        }
    }

    /// <summary>Waits for a signal or <paramref name="poll"/>, whichever comes first. True when a signal ended the wait.</summary>
    public async Task<bool> WaitAsync(TimeSpan poll, CancellationToken cancellationToken)
    {
        var signalled = await wake.WaitAsync(poll, cancellationToken).ConfigureAwait(false);
        if (signalled)
        {
            Interlocked.Increment(ref wakeups);
        }
        else
        {
            Interlocked.Increment(ref polls);
        }

        return signalled;
    }

    public void Dispose() => wake.Dispose();
}

public enum AuthorityWorkSignalSource
{
    Commit,
    Notification,
    Reconnect,
}
