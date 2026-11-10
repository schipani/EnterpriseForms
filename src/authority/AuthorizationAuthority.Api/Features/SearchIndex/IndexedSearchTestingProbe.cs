using System.Collections.Concurrent;
using AuthorizationAuthority.Application.Features.SearchIndex;

namespace AuthorizationAuthority.Api.Features.SearchIndex;

/// <summary>
/// C-141 (RQ-2): the Testing-environment probe of the share-only readability step. Keeps the observations of the most recent
/// correlation ids in memory (bounded; counts and codes only, no identifiers of records, fields or items, no values). Registered
/// only in the Testing environment.
/// </summary>
public sealed class IndexedSearchTestingProbe : IIndexedSearchProbe
{
    private const int Capacity = 512;
    private readonly ConcurrentDictionary<Guid, List<ShareOnlyProbeObservation>> observations = new();
    private readonly ConcurrentQueue<Guid> order = new();

    public void ShareOnlyStep(Guid correlationId, ShareOnlyProbeObservation observation)
    {
        var list = observations.GetOrAdd(correlationId, id =>
        {
            order.Enqueue(id);
            return [];
        });
        lock (list)
        {
            list.Add(observation);
        }

        while (order.Count > Capacity && order.TryDequeue(out var oldest))
        {
            observations.TryRemove(oldest, out _);
        }
    }

    public IReadOnlyList<ShareOnlyProbeObservation> Find(Guid correlationId)
    {
        if (!observations.TryGetValue(correlationId, out var list))
        {
            return [];
        }

        lock (list)
        {
            return [.. list];
        }
    }
}
