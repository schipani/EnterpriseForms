using System.Collections.Concurrent;

namespace EnterpriseForms.Application.Features.Search.FieldSearch;

/// <summary>
/// Phase C7 M3 workload admission (owner performance decision): a verified search reads and verifies its whole scope, so the
/// number running at once on this API instance — and per user — is bounded. A search that cannot get a slot within the
/// configured wait is refused as busy (503 <c>SEARCH_VERIFICATION_UNAVAILABLE</c>, reason <c>SEARCH_CAPACITY_EXCEEDED</c>,
/// retryable) — never queued without bound, never run partially.
/// </summary>
public sealed class FieldSearchAdmission(FieldSearchLimits limits) : IDisposable
{
    public const string CapacityExceeded = "SEARCH_CAPACITY_EXCEEDED";

    private readonly SemaphoreSlim slots = new(limits.MaximumConcurrentSearches, limits.MaximumConcurrentSearches);
    private readonly ConcurrentDictionary<Guid, int> perUser = new();

    /// <summary>Waits briefly for a slot; the returned lease releases it. Throws the busy refusal when none is free.</summary>
    public async Task<IDisposable> AdmitAsync(Guid userId, CancellationToken cancellationToken)
    {
        var mine = perUser.AddOrUpdate(userId, 1, (_, count) => count + 1);
        if (mine > limits.MaximumConcurrentSearchesPerUser)
        {
            Leave(userId);
            throw Busy();
        }

        bool entered;
        try
        {
            entered = await slots.WaitAsync(limits.AdmissionWaitMilliseconds, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            Leave(userId);
            throw;
        }

        if (!entered)
        {
            Leave(userId);
            throw Busy();
        }

        return new Lease(this, userId);
    }

    /// <summary>Whether a slot is free right now (used before the single automatic retry: never retry into an overloaded instance).</summary>
    public bool HasFreeSlot => slots.CurrentCount > 0;

    public void Dispose() => slots.Dispose();

    private static SearchException Busy() => SearchException.VerificationUnavailable(CapacityExceeded, retryable: true);

    private void Leave(Guid userId)
    {
        if (perUser.AddOrUpdate(userId, 0, (_, count) => count - 1) <= 0)
        {
            perUser.TryRemove(new KeyValuePair<Guid, int>(userId, 0));
        }
    }

    private sealed class Lease(FieldSearchAdmission owner, Guid userId) : IDisposable
    {
        private int released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref released, 1) == 0)
            {
                owner.slots.Release();
                owner.Leave(userId);
            }
        }
    }
}
