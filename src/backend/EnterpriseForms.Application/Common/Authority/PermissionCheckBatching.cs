namespace EnterpriseForms.Application.Common.Authority;

/// <summary>
/// Splits a permission-check batch of any size into authority calls of at most <see cref="MaximumChecksPerCall"/> (the
/// authority's per-call limit) and reassembles the decisions in request order (C-27).
/// <para>
/// Every chunk is an independent authority evaluation, so the chunks are only combined when they were all evaluated at
/// the same authorization and projection revision; otherwise the whole batch is evaluated again, and after
/// <see cref="MaximumAttempts"/> attempts the call fails closed with a retryable error. A failing chunk fails the whole
/// batch — decisions are never returned partially, and nothing is ever treated as allowed because a check failed.
/// </para>
/// </summary>
public static class PermissionCheckBatching
{
    public const int MaximumChecksPerCall = 250;

    public const int MaximumAttempts = 3;

    public static async Task<IReadOnlyList<PermissionDecision>> CheckAllAsync(
        IReadOnlyList<PermissionCheckRequest> requests,
        Func<IReadOnlyList<PermissionCheckRequest>, CancellationToken, Task<IReadOnlyList<PermissionDecision>>> checkChunk,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(requests);
        ArgumentNullException.ThrowIfNull(checkChunk);
        if (requests.Count == 0)
        {
            return [];
        }

        if (requests.Count <= MaximumChecksPerCall)
        {
            var single = await checkChunk(requests, cancellationToken).ConfigureAwait(false);
            RequireCount(single, requests.Count);
            return single;
        }

        for (var attempt = 1; attempt <= MaximumAttempts; attempt++)
        {
            var decisions = new List<PermissionDecision>(requests.Count);
            for (var start = 0; start < requests.Count; start += MaximumChecksPerCall)
            {
                var chunk = Slice(requests, start, Math.Min(MaximumChecksPerCall, requests.Count - start));
                var chunkDecisions = await checkChunk(chunk, cancellationToken).ConfigureAwait(false);
                RequireCount(chunkDecisions, chunk.Count);
                decisions.AddRange(chunkDecisions);
            }

            if (IsConsistent(decisions))
            {
                return decisions;
            }
        }

        throw new AuthorityClientException("AUTHORIZATION_STATE_CHANGED", 503, true, "access changed while permissions were being checked; try again");
    }

    /// <summary>True when every decision was evaluated at one authorization revision and one projection revision.</summary>
    public static bool IsConsistent(IReadOnlyList<PermissionDecision> decisions)
    {
        ArgumentNullException.ThrowIfNull(decisions);
        for (var i = 1; i < decisions.Count; i++)
        {
            if (decisions[i].AuthorizationRevision != decisions[0].AuthorizationRevision || decisions[i].ProjectionRevision != decisions[0].ProjectionRevision)
            {
                return false;
            }
        }

        return true;
    }

    private static void RequireCount(IReadOnlyList<PermissionDecision> decisions, int expected)
    {
        if (decisions is null || decisions.Count != expected)
        {
            throw new AuthorityClientException("AUTHORITY_UNAVAILABLE", 503, true, "the authority returned a different number of decisions than checks");
        }
    }

    private static List<PermissionCheckRequest> Slice(IReadOnlyList<PermissionCheckRequest> requests, int start, int count)
    {
        var slice = new List<PermissionCheckRequest>(count);
        for (var i = start; i < start + count; i++)
        {
            slice.Add(requests[i]);
        }

        return slice;
    }
}
