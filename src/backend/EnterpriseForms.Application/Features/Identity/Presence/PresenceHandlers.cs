using EnterpriseForms.Application.Common;
using EnterpriseForms.Application.Common.Persistence;
using EnterpriseForms.Contracts.Features;

namespace EnterpriseForms.Application.Features.Identity.Presence;

public static class PresenceStates
{
    public const string Available = "Available";
    public const string Away = "Away";
    public const string Offline = "Offline";
    public const string Unknown = "Unknown";
}

/// <summary>
/// Presence rules. A heartbeat is "live" for <see cref="HeartbeatTtl"/> (two missed one-minute beats and the person is Offline, so a
/// network failure never leaves anyone Available); a live person with no interaction for <see cref="AwayAfter"/> is Away.
/// </summary>
public static class PresenceRules
{
    public static readonly TimeSpan HeartbeatTtl = TimeSpan.FromSeconds(150);

    public static readonly TimeSpan AwayAfter = TimeSpan.FromMinutes(5);

    public const int MaximumQueryIds = 100;

    public const int RefreshAfterSeconds = 60;

    public const int MaximumInteractionAgeSeconds = 24 * 60 * 60;

    public static string Compute(DateTimeOffset? lastHeartbeatOn, DateTimeOffset? lastInteractionOn, DateTimeOffset now)
    {
        if (lastHeartbeatOn is null || now - lastHeartbeatOn.Value > HeartbeatTtl)
        {
            return PresenceStates.Offline;
        }

        return lastInteractionOn is null || now - lastInteractionOn.Value > AwayAfter ? PresenceStates.Away : PresenceStates.Available;
    }
}

/// <summary>
/// Shared presence (informational only). The heartbeat identity is always the authenticated session's user (never a client-supplied id).
/// Presence is read for people of the caller's own organization only; a person who is unknown to the organization is reported as
/// Unknown, the same as an unavailable presence store, so nothing distinguishes the cases. Presence never feeds authentication,
/// authorization, membership or session decisions.
/// </summary>
public sealed class PresenceHandlers(IBusinessStore store, TimeProvider clock)
{
    public async Task HeartbeatAsync(RequestContext context, PresenceHeartbeatRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(request);
        if (request.InteractionAgeSeconds is < 0 or > PresenceRules.MaximumInteractionAgeSeconds)
        {
            throw new BusinessException("INVALID_REQUEST", 422, "interactionAgeSeconds must be between 0 and 86400");
        }

        var now = clock.GetUtcNow();
        await using var scope = await store.BeginScopeAsync(cancellationToken).ConfigureAwait(false);
        await scope.TouchPresenceAsync(context.OrganizationId, context.LocalUserId, now, now.AddSeconds(-request.InteractionAgeSeconds), cancellationToken).ConfigureAwait(false);
        await scope.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Sign-out of this session (best effort by callers; the heartbeat expiry covers every other case).</summary>
    public async Task ClearOnSessionEndAsync(Guid organizationId, Guid userId, CancellationToken cancellationToken)
    {
        await using var scope = await store.BeginScopeAsync(cancellationToken).ConfigureAwait(false);
        await scope.ClearPresenceAsync(organizationId, userId, cancellationToken).ConfigureAwait(false);
        await scope.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<PresenceQueryResponse> QueryAsync(RequestContext context, IReadOnlyCollection<Guid> userIds, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(userIds);
        var ids = userIds.Distinct().ToList();
        if (ids.Count is 0 or > PresenceRules.MaximumQueryIds)
        {
            throw new BusinessException("INVALID_REQUEST", 422, $"ask for 1 to {PresenceRules.MaximumQueryIds} people at a time");
        }

        var now = clock.GetUtcNow();
        try
        {
            await using var scope = await store.BeginScopeAsync(cancellationToken).ConfigureAwait(false);
            var rows = (await scope.GetPresenceAsync(context.OrganizationId, ids, cancellationToken).ConfigureAwait(false)).ToDictionary(r => r.UserId);
            var presence = ids.Select(id => new UserPresenceResponse(id, rows.TryGetValue(id, out var row) ? PresenceRules.Compute(row.LastHeartbeatOn, row.LastInteractionOn, now) : PresenceStates.Unknown)).ToList();
            return new PresenceQueryResponse(presence, true, now, PresenceRules.RefreshAfterSeconds);
        }
#pragma warning disable CA1031 // presence is informational: any store failure is reported as Unknown, never as an error that affects a page
        catch (Exception ex) when (ex is not OperationCanceledException && ex is not BusinessException)
#pragma warning restore CA1031
        {
            return new PresenceQueryResponse(ids.Select(id => new UserPresenceResponse(id, PresenceStates.Unknown)).ToList(), false, now, PresenceRules.RefreshAfterSeconds);
        }
    }
}
