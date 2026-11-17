using AuthorizationAuthority.Application.Common.Ports;
using AuthorizationAuthority.Application.Features.Journal;

namespace AuthorizationAuthority.Application.Features.Content;

/// <summary>What happened to one refusal's evidence (closure §4, §5.7; reported to the observer, never to the caller).</summary>
public enum CapabilityRefusalEvidenceOutcome
{
    /// <summary>T2 committed one entry (archive delivery is then pending until the existing archive processor anchors it, D-3).</summary>
    Committed,

    /// <summary>T2's conclusive lookup found the key already recorded in the bucket; nothing written.</summary>
    AlreadyRecorded,

    /// <summary>The in-process pre-filter knows the key is recorded in its current bucket; no T2.</summary>
    SuppressedRecorded,

    /// <summary>A retry of the key is already scheduled; no T2.</summary>
    SuppressedPending,

    /// <summary>A concurrent request of this process is already recording the key; no T2 and no attempt consumed.</summary>
    SuppressedInFlight,

    /// <summary>A recent lookup for the key was inconclusive (negative cache); no T2.</summary>
    SuppressedInconclusive,

    /// <summary>The per-process hourly attempt limit is exhausted; no T2 (D-7).</summary>
    QuotaSuppressed,

    /// <summary>T2 did not reach a decision (transient reason); a bounded in-memory retry is scheduled (D-4).</summary>
    Deferred,

    /// <summary>T2's commit outcome is unknown; a retry reconciles against the durable journal before any append (D-6).</summary>
    Ambiguous,

    /// <summary>T2's lookup could not prove absence for a persistent reason; never retried for this bucket (D-7).</summary>
    Inconclusive,

    /// <summary>A scheduled retry was given up (age, or retry capacity) without recording (D-7).</summary>
    Abandoned,

    /// <summary>Not one of the seven approved keys (LK-9); nothing attempted.</summary>
    NotAttempted,
}

/// <summary>Operational signal of the recorder (logs 9253/9254 and counters in the host). Never durable evidence.</summary>
public interface ICapabilityRefusalEvidenceObserver
{
    void Observed(Guid organizationId, string entryPoint, CapabilityRefusalEvidenceOutcome outcome, CapabilityRefusalEvidenceReason reason);
}

/// <summary>
/// Closure §5.7 fixed bounds. Constants, not configuration (PD-06); the constructor parameter exists for tests only.
/// </summary>
public sealed record CapabilityRefusalEvidenceLimits
{
    public static CapabilityRefusalEvidenceLimits Default { get; } = new();

    public int PreFilterCapacity { get; init; } = 4096;

    public int RetryCapacity { get; init; } = 1024;

    public int AttemptLimitPerWindow { get; init; } = 256;

    public TimeSpan AttemptWindow { get; init; } = TimeSpan.FromHours(1);

    public TimeSpan TransientNegativeCache { get; init; } = TimeSpan.FromSeconds(60);

    public TimeSpan RetryInitialDelay { get; init; } = TimeSpan.FromSeconds(5);

    public TimeSpan RetryMaximumDelay { get; init; } = TimeSpan.FromMinutes(5);

    public TimeSpan AbandonAfter { get; init; } = TimeSpan.FromHours(1);

    /// <summary>The whole T2 budget (closure §5.7: about 2 s); lock and statement bounds are set by the store (1 s, 500 ms).</summary>
    public TimeSpan Budget { get; init; } = TimeSpan.FromSeconds(2);
}

/// <summary>
/// E3-M2f-3b (docs/security/E3_M2F3_CONTRACT_CLOSURE.md §3, §5, §7): the ONE producer of <c>EnvelopeCapabilityRefused</c> evidence on
/// the organization audit stream. It is called only by the endpoint filter of the seven capability routes, after the refused
/// transaction T1 has been disposed, and records in its own transaction T2:
/// <list type="number">
/// <item><c>SET LOCAL lock_timeout</c>; lock the organization row (the same first lock as every audit append);</item>
/// <item><c>t_db</c> = AuthorityDb <c>clock_timestamp()</c> read after the lock (statement timeout set); bucket = its UTC hour;</item>
/// <item>the E3-M2f-3i index must be usable; read the newest at most eight evidence entries of the organization;</item>
/// <item><see cref="CapabilityRefusalEvidenceLookup.Decide"/>: append only when the lookup is conclusive and the key is absent;</item>
/// <item><see cref="AuditJournal.Append"/> and commit (insert one entry; audit revision, audit digest and state modification time).</item>
/// </list>
/// The seven-per-organization-per-hour bound (closure §5.4) rests only on that durable, locked lookup. The in-process state below
/// (pre-filter, negative cache, attempt limit, retry set) can only skip or postpone attempts; it never causes an append, so restarts,
/// eviction, retries and several instances cannot break the bound. Nothing here admits a request or changes its response.
/// </summary>
public sealed class CapabilityRefusalEvidenceRecorder
{
    private readonly IAuthorityStore store;
    private readonly TimeProvider clock;
    private readonly ICapabilityRefusalEvidenceObserver? observer;
    private readonly CapabilityRefusalEvidenceLimits limits;
    private readonly Lock gate = new();
    private readonly Dictionary<CapabilityRefusalEvidenceKey, LinkedListNode<CacheItem>> cache = [];
    private readonly LinkedList<CacheItem> recency = new();
    private readonly Dictionary<CapabilityRefusalEvidenceKey, PendingItem> pending = [];
    private readonly HashSet<CapabilityRefusalEvidenceKey> inFlight = [];
    private long windowStart;
    private int attemptsInWindow;

    public CapabilityRefusalEvidenceRecorder(IAuthorityStore store, TimeProvider clock, ICapabilityRefusalEvidenceObserver? observer = null, CapabilityRefusalEvidenceLimits? limits = null)
    {
        this.store = store ?? throw new ArgumentNullException(nameof(store));
        this.clock = clock ?? throw new ArgumentNullException(nameof(clock));
        this.observer = observer;
        this.limits = limits ?? CapabilityRefusalEvidenceLimits.Default;
        windowStart = clock.GetTimestamp();
    }

    /// <summary>Number of keys waiting for a retry (tests, diagnostics).</summary>
    public int PendingCount
    {
        get
        {
            lock (gate)
            {
                return pending.Count;
            }
        }
    }

    /// <summary>Number of pre-filter and negative-cache entries (tests, diagnostics).</summary>
    public int CachedCount
    {
        get
        {
            lock (gate)
            {
                return cache.Count;
            }
        }
    }

    /// <summary>T2 attempts consumed in the current attempt window (tests, diagnostics).</summary>
    public int AttemptsInWindow
    {
        get
        {
            lock (gate)
            {
                RollWindow(clock.GetTimestamp());
                return attemptsInWindow;
            }
        }
    }

    /// <summary>
    /// Records evidence of one capability refusal. Never throws for evidence reasons and never changes the refused response; the caller
    /// rethrows the original refusal whatever this returns. <paramref name="cancellationToken"/> is the host's lifetime, not the
    /// request's (T2-5): a client disconnect does not skip evidence.
    /// </summary>
    public async Task<CapabilityRefusalEvidenceOutcome> RecordAsync(CapabilityRefusalContext refusal, string entryPoint, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(refusal);
        var observed = clock.GetUtcNow();
        var key = CapabilityRefusalEvidenceKey.For(refusal.OrganizationId, entryPoint);
        if (!key.IsApproved)
        {
            return Report(key, CapabilityRefusalEvidenceOutcome.NotAttempted, CapabilityRefusalEvidenceReason.Error);
        }

        var now = clock.GetTimestamp();
        lock (gate)
        {
            if (pending.ContainsKey(key))
            {
                return Report(key, CapabilityRefusalEvidenceOutcome.SuppressedPending, CapabilityRefusalEvidenceReason.None);
            }

            if (Cached(key, now) is { } suppressed)
            {
                return Report(key, suppressed, CapabilityRefusalEvidenceReason.None);
            }

            // Coalescing: a burst of identical refusals costs one T2 and one attempt, so one key cannot exhaust the attempt limit.
            if (inFlight.Contains(key))
            {
                return Report(key, CapabilityRefusalEvidenceOutcome.SuppressedInFlight, CapabilityRefusalEvidenceReason.None);
            }

            if (!TryConsumeAttempt(now))
            {
                // One signal per key per window: the key is then negatively cached until the window ends.
                Remember(key, CacheState.Inconclusive, WindowEnd());
                return Report(key, CapabilityRefusalEvidenceOutcome.QuotaSuppressed, CapabilityRefusalEvidenceReason.AttemptLimit);
            }

            inFlight.Add(key);
        }

        return await AttemptAsync(key, refusal.ClientCapabilityLevel, observed, now, null, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Runs the retries that are due (called by the host's retry service). Never throws for evidence reasons.</summary>
    public async Task<int> RetryDueAsync(CancellationToken cancellationToken)
    {
        var now = clock.GetTimestamp();
        var due = new List<(CapabilityRefusalEvidenceKey Key, PendingItem Item)>();
        var abandoned = new List<CapabilityRefusalEvidenceKey>();
        lock (gate)
        {
            foreach (var (key, item) in pending.Where(p => p.Value.NextDue <= now).ToList())
            {
                if (inFlight.Contains(key))
                {
                    continue; // a request of this process is recording it right now
                }

                if (Elapsed(item.FirstObserved, now) >= limits.AbandonAfter)
                {
                    pending.Remove(key);
                    abandoned.Add(key);
                }
                else if (!TryConsumeAttempt(now))
                {
                    item.NextDue = WindowEnd(); // retried in the next window; counted against it then
                }
                else
                {
                    inFlight.Add(key);
                    due.Add((key, item));
                }
            }
        }

        foreach (var key in abandoned)
        {
            Report(key, CapabilityRefusalEvidenceOutcome.Abandoned, CapabilityRefusalEvidenceReason.Abandoned);
        }

        foreach (var (key, item) in due)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await AttemptAsync(key, item.ClientCapabilityLevel, item.ObservedUtc, item.FirstObserved, item, cancellationToken).ConfigureAwait(false);
        }

        return due.Count;
    }

    private async Task<CapabilityRefusalEvidenceOutcome> AttemptAsync(CapabilityRefusalEvidenceKey key, int level, DateTimeOffset observed, long firstObserved, PendingItem? retry, CancellationToken cancellationToken)
    {
        var (outcome, reason, databaseTime) = await TryOnceAsync(key, level, observed, cancellationToken).ConfigureAwait(false);
        var now = clock.GetTimestamp();
        lock (gate)
        {
            inFlight.Remove(key);
            switch (outcome)
            {
                case CapabilityRefusalEvidenceOutcome.Committed or CapabilityRefusalEvidenceOutcome.AlreadyRecorded:
                    pending.Remove(key);
                    Remember(key, CacheState.Recorded, UntilBucketEnd(databaseTime!.Value));
                    break;
                case CapabilityRefusalEvidenceOutcome.Inconclusive:
                    pending.Remove(key);
                    Remember(key, CacheState.Inconclusive, databaseTime is { } t ? UntilBucketEnd(t) : limits.TransientNegativeCache);
                    break;
                default: // Deferred or Ambiguous: bounded in-memory retry (D-4, D-6)
                    Remember(key, CacheState.Inconclusive, limits.TransientNegativeCache);
                    if (retry is not null)
                    {
                        retry.Attempts++;
                        retry.NextDue = now + Ticks(Backoff(retry.Attempts));
                    }
                    else if (pending.Count >= limits.RetryCapacity)
                    {
                        outcome = CapabilityRefusalEvidenceOutcome.Abandoned;
                        reason = CapabilityRefusalEvidenceReason.RetryCapacity;
                    }
                    else
                    {
                        pending[key] = new PendingItem(level, observed, firstObserved) { Attempts = 1, NextDue = now + Ticks(Backoff(1)) };
                    }

                    break;
            }
        }

        return Report(key, outcome, reason);
    }

    private async Task<(CapabilityRefusalEvidenceOutcome Outcome, CapabilityRefusalEvidenceReason Reason, DateTimeOffset? DatabaseTime)> TryOnceAsync(
        CapabilityRefusalEvidenceKey key, int level, DateTimeOffset observed, CancellationToken cancellationToken)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(limits.Budget);
        var token = budget.Token;
        var committing = false;
        DateTimeOffset? databaseTime = null;
        try
        {
            // T2: a new scope, only after T1 was disposed by the refused entry point (the filter calls this after the handler threw).
            var scope = await store.BeginScopeAsync(token).ConfigureAwait(false);
            await using (scope.ConfigureAwait(false))
            {
                await scope.SetCapabilityEvidenceLockTimeoutAsync(token).ConfigureAwait(false);
                var locked = await scope.LockOrganizationAsync(key.OrganizationId, token).ConfigureAwait(false);
                if (locked is null)
                {
                    return (CapabilityRefusalEvidenceOutcome.Deferred, CapabilityRefusalEvidenceReason.Error, null);
                }

                databaseTime = await scope.StartCapabilityEvidenceLookupAsync(token).ConfigureAwait(false);
                if (!await scope.IsCapabilityRefusalEvidenceIndexUsableAsync(token).ConfigureAwait(false))
                {
                    return (CapabilityRefusalEvidenceOutcome.Deferred, CapabilityRefusalEvidenceReason.IndexUnavailable, databaseTime);
                }

                var rows = await scope.GetNewestCapabilityRefusalEvidenceAsync(key.OrganizationId, token).ConfigureAwait(false);
                var (verdict, reason) = CapabilityRefusalEvidenceLookup.Decide(rows, key, databaseTime.Value);
                switch (verdict)
                {
                    case CapabilityRefusalEvidenceVerdict.AlreadyRecorded:
                        return (CapabilityRefusalEvidenceOutcome.AlreadyRecorded, CapabilityRefusalEvidenceReason.None, databaseTime);
                    case CapabilityRefusalEvidenceVerdict.Inconclusive:
                        // A clock regression passes once t_db overtakes the newest entry; the other reasons persist (closure §5.6).
                        return (reason == CapabilityRefusalEvidenceReason.ClockRegression ? CapabilityRefusalEvidenceOutcome.Deferred : CapabilityRefusalEvidenceOutcome.Inconclusive, reason, databaseTime);
                }

                var body = new CapabilityRefusalEvidenceBody(CapabilityRefusalEvidence.SchemaVersion, key.RefusalCode, key.OrganizationId, key.WorkloadName, key.EntryPoint,
                    level, databaseTime.Value, CapabilityRefusalEvidence.BucketOf(databaseTime.Value), observed);
                AuditJournal.Append(scope, locked, CapabilityRefusalEvidence.EventKind, body, clock);
                committing = true;
                await scope.CommitAsync(token).ConfigureAwait(false);
                return (CapabilityRefusalEvidenceOutcome.Committed, CapabilityRefusalEvidenceReason.None, databaseTime);
            }
        }
        catch (Exception) when (committing)
        {
            // D-6 (TQ-12 default): any failure once the commit was requested is an unknown outcome; nothing is assumed either way.
            return (CapabilityRefusalEvidenceOutcome.Ambiguous, CapabilityRefusalEvidenceReason.Ambiguous, databaseTime);
        }
        catch (OperationCanceledException)
        {
            return (CapabilityRefusalEvidenceOutcome.Deferred, CapabilityRefusalEvidenceReason.Timeout, databaseTime);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // LK-8: lock or statement timeouts, database errors, an unavailable store: no append.
            return (CapabilityRefusalEvidenceOutcome.Deferred, CapabilityRefusalEvidenceReason.Error, databaseTime);
        }
    }

    private CapabilityRefusalEvidenceOutcome Report(CapabilityRefusalEvidenceKey key, CapabilityRefusalEvidenceOutcome outcome, CapabilityRefusalEvidenceReason reason)
    {
        try
        {
            observer?.Observed(key.OrganizationId, key.EntryPoint, outcome, reason);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // telemetry must never affect the refusal
        }

        return outcome;
    }

    // ---- in-process state (load control only; never a reason to append) ----

    private CapabilityRefusalEvidenceOutcome? Cached(CapabilityRefusalEvidenceKey key, long now)
    {
        if (!cache.TryGetValue(key, out var node))
        {
            return null;
        }

        if (node.Value.Deadline <= now)
        {
            recency.Remove(node);
            cache.Remove(key);
            return null;
        }

        recency.Remove(node);
        recency.AddFirst(node);
        return node.Value.State == CacheState.Recorded ? CapabilityRefusalEvidenceOutcome.SuppressedRecorded : CapabilityRefusalEvidenceOutcome.SuppressedInconclusive;
    }

    private void Remember(CapabilityRefusalEvidenceKey key, CacheState state, TimeSpan duration) => Remember(key, state, clock.GetTimestamp() + Ticks(duration));

    private void Remember(CapabilityRefusalEvidenceKey key, CacheState state, long deadline)
    {
        if (cache.Remove(key, out var existing))
        {
            recency.Remove(existing);
        }

        while (cache.Count >= limits.PreFilterCapacity && recency.Last is { } oldest)
        {
            recency.RemoveLast();
            cache.Remove(oldest.Value.Key);
        }

        cache[key] = recency.AddFirst(new CacheItem(key, state, deadline));
    }

    /// <summary>The local monotonic duration until the end of the authoritative bucket of <paramref name="databaseTime"/> (closure §5.7).</summary>
    private static TimeSpan UntilBucketEnd(DateTimeOffset databaseTime) => CapabilityRefusalEvidence.BucketOf(databaseTime).AddHours(1) - databaseTime;

    private bool TryConsumeAttempt(long now)
    {
        RollWindow(now);
        if (attemptsInWindow >= limits.AttemptLimitPerWindow)
        {
            return false;
        }

        attemptsInWindow++;
        return true;
    }

    private void RollWindow(long now)
    {
        if (Elapsed(windowStart, now) >= limits.AttemptWindow)
        {
            windowStart = now;
            attemptsInWindow = 0;
        }
    }

    private long WindowEnd() => windowStart + Ticks(limits.AttemptWindow);

    private TimeSpan Backoff(int attempts)
    {
        var delay = limits.RetryInitialDelay * Math.Pow(2, Math.Min(attempts - 1, 16));
        return delay < limits.RetryMaximumDelay ? delay : limits.RetryMaximumDelay;
    }

    private TimeSpan Elapsed(long from, long to) => clock.GetElapsedTime(from, to);

    private long Ticks(TimeSpan duration) => (long)(duration.TotalSeconds * clock.TimestampFrequency);

    private enum CacheState
    {
        Recorded,
        Inconclusive,
    }

    private sealed record CacheItem(CapabilityRefusalEvidenceKey Key, CacheState State, long Deadline);

    private sealed class PendingItem(int clientCapabilityLevel, DateTimeOffset observedUtc, long firstObserved)
    {
        public int ClientCapabilityLevel { get; } = clientCapabilityLevel;

        public DateTimeOffset ObservedUtc { get; } = observedUtc;

        public long FirstObserved { get; } = firstObserved;

        public int Attempts { get; set; }

        public long NextDue { get; set; }
    }
}
