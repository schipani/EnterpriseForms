using AuthorizationAuthority.Application.Common.Ports;
using Authzed.Api.V1;

namespace AuthorizationAuthority.Infrastructure.SpiceDb;

/// <summary>How a per-item error inside a successful CheckBulkPermissions response is treated (P-S1-1 §4 O-1, IX-D10).</summary>
public enum BulkItemErrorPolicy
{
    /// <summary>Existing callers (unchanged behaviour): the item is <see cref="SpiceDbCheckOutcome.Unknown"/>, which every caller treats as not allowed.</summary>
    ReportUnknown = 0,

    /// <summary>O-1 hardening for V2 (IX-3): any per-item error fails the whole call as provider-unavailable (503, no quarantine).</summary>
    FailCall = 1,
}

/// <summary>
/// IX-2, O-1 client hardening (design R1 §A.3 O-1(a), §E.3; P-S1-1 §4): maps a CheckBulkPermissions response to the issued
/// checks by the request each pair echoes, never by position alone. The pair count must equal the item count, and every pair's
/// echoed resource, permission and subject must equal the item issued at that position; anything else fails the call as
/// provider-unavailable. A pure function, so the mapping rules are unit-tested without a SpiceDB server.
/// </summary>
public static class SpiceDbBulkCheckMapping
{
    public static IReadOnlyList<SpiceDbCheckOutcome> Map(IReadOnlyList<SpiceDbCheck> checks, IReadOnlyList<CheckBulkPermissionsPair> pairs, BulkItemErrorPolicy itemErrors)
    {
        ArgumentNullException.ThrowIfNull(checks);
        ArgumentNullException.ThrowIfNull(pairs);
        if (pairs.Count != checks.Count)
        {
            throw new SpiceDbUnavailableException($"bulk check returned {pairs.Count} results for {checks.Count} items");
        }

        var outcomes = new SpiceDbCheckOutcome[checks.Count];
        for (var i = 0; i < checks.Count; i++)
        {
            var pair = pairs[i];
            if (!EchoMatches(checks[i], pair.Request))
            {
                throw new SpiceDbUnavailableException($"bulk check result {i} does not echo the issued item");
            }

            if (pair.ResponseCase != CheckBulkPermissionsPair.ResponseOneofCase.Item)
            {
                if (itemErrors == BulkItemErrorPolicy.FailCall)
                {
                    throw new SpiceDbUnavailableException($"bulk check item {i} failed: " + (pair.Error?.Code.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "no result"));
                }

                outcomes[i] = SpiceDbCheckOutcome.Unknown;
                continue;
            }

            outcomes[i] = pair.Item.Permissionship switch
            {
                CheckPermissionResponse.Types.Permissionship.HasPermission => SpiceDbCheckOutcome.Allowed,
                CheckPermissionResponse.Types.Permissionship.NoPermission => SpiceDbCheckOutcome.Denied,
                _ => SpiceDbCheckOutcome.Unknown, // conditional or unspecified: never allowed
            };
        }

        return outcomes;
    }

    /// <summary>The echoed request equals the issued check: resource type and id, permission, subject type and id, no subject relation.</summary>
    public static bool EchoMatches(SpiceDbCheck check, CheckBulkPermissionsRequestItem? echoed)
    {
        ArgumentNullException.ThrowIfNull(check);
        return echoed is not null
               && string.Equals(echoed.Resource?.ObjectType, check.ResourceType, StringComparison.Ordinal)
               && string.Equals(echoed.Resource?.ObjectId, check.ResourceId, StringComparison.Ordinal)
               && string.Equals(echoed.Permission, check.Permission, StringComparison.Ordinal)
               && string.Equals(echoed.Subject?.Object?.ObjectType, check.SubjectType, StringComparison.Ordinal)
               && string.Equals(echoed.Subject?.Object?.ObjectId, check.SubjectId, StringComparison.Ordinal)
               && string.IsNullOrEmpty(echoed.Subject?.OptionalRelation);
    }

    /// <summary>Splits the items into requests of at most <paramref name="maximumItems"/> (never truncates).</summary>
    public static IEnumerable<IReadOnlyList<SpiceDbCheck>> Chunk(IReadOnlyList<SpiceDbCheck> checks, int maximumItems)
    {
        ArgumentNullException.ThrowIfNull(checks);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumItems, 1);
        for (var start = 0; start < checks.Count; start += maximumItems)
        {
            yield return checks.Skip(start).Take(maximumItems).ToList();
        }
    }
}
