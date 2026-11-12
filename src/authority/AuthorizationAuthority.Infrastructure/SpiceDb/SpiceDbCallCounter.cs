using System.Collections.Concurrent;
using Authzed.Api.V1;
using Grpc.Core;
using Grpc.Core.Interceptors;

namespace AuthorizationAuthority.Infrastructure.SpiceDb;

/// <summary>
/// C-149 (P-2): measurement-only count of SpiceDB gRPC calls, by attribution (the Authority route that caused the call, or
/// "background" for loops) and RPC method, plus the items carried by CheckBulkPermissions requests. Off unless
/// <c>Diagnostics:SpiceDbCallCounting</c> is true, which is refused outside Development/Testing. Records method names and counts
/// only: never a request body, relationship, subject or token.
/// </summary>
public sealed class SpiceDbCallCounter(bool enabled)
{
    public const string ConfigurationKey = "Diagnostics:SpiceDbCallCounting";

    public const string Background = "background";

    private static readonly AsyncLocal<string?> CurrentAttribution = new();

    private readonly ConcurrentDictionary<(string Attribution, string Method), long> calls = new();

    private readonly ConcurrentDictionary<string, long> bulkItems = new();

    private readonly ConcurrentDictionary<string, long> operations = new();

    public bool Enabled { get; } = enabled;

    /// <summary>Attributes SpiceDB calls made in this asynchronous flow to <paramref name="attribution"/> until disposed.</summary>
    public IDisposable Attribute(string attribution)
    {
        var previous = CurrentAttribution.Value;
        CurrentAttribution.Value = attribution;
        if (Enabled)
        {
            operations.AddOrUpdate(attribution, 1, (_, n) => n + 1);
        }

        return new Restore(previous);
    }

    public void Record(string method, int checkItems)
    {
        if (!Enabled)
        {
            return;
        }

        var attribution = CurrentAttribution.Value ?? Background;
        calls.AddOrUpdate((attribution, method), 1, (_, n) => n + 1);
        if (checkItems > 0)
        {
            bulkItems.AddOrUpdate(attribution, checkItems, (_, n) => n + checkItems);
        }
    }

    public SpiceDbCallSnapshot Snapshot() => new(
        Enabled,
        calls.GroupBy(c => c.Key.Attribution).OrderBy(g => g.Key, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => (IReadOnlyDictionary<string, long>)g.OrderBy(c => c.Key.Method, StringComparer.Ordinal).ToDictionary(c => c.Key.Method, c => c.Value), StringComparer.Ordinal),
        bulkItems.OrderBy(b => b.Key, StringComparer.Ordinal).ToDictionary(b => b.Key, b => b.Value, StringComparer.Ordinal),
        operations.OrderBy(o => o.Key, StringComparer.Ordinal).ToDictionary(o => o.Key, o => o.Value, StringComparer.Ordinal));

    public void Reset()
    {
        calls.Clear();
        bulkItems.Clear();
        operations.Clear();
    }

    private sealed class Restore(string? previous) : IDisposable
    {
        public void Dispose() => CurrentAttribution.Value = previous;
    }
}

/// <summary>Counts by attribution: calls per RPC method, CheckBulkPermissions items, and attributed operations (requests).</summary>
public sealed record SpiceDbCallSnapshot(bool CountingEnabled, IReadOnlyDictionary<string, IReadOnlyDictionary<string, long>> CallsByAttribution, IReadOnlyDictionary<string, long> BulkCheckItemsByAttribution, IReadOnlyDictionary<string, long> OperationsByAttribution);

/// <summary>C-149 (P-2): the client-side interceptor feeding <see cref="SpiceDbCallCounter"/>; installed only when counting is on.</summary>
internal sealed class SpiceDbCountingInterceptor(SpiceDbCallCounter counter) : Interceptor
{
    public override AsyncUnaryCall<TResponse> AsyncUnaryCall<TRequest, TResponse>(TRequest request, ClientInterceptorContext<TRequest, TResponse> context, AsyncUnaryCallContinuation<TRequest, TResponse> continuation)
    {
        counter.Record(context.Method.Name, request is CheckBulkPermissionsRequest bulk ? bulk.Items.Count : 0);
        return continuation(request, context);
    }

    public override TResponse BlockingUnaryCall<TRequest, TResponse>(TRequest request, ClientInterceptorContext<TRequest, TResponse> context, BlockingUnaryCallContinuation<TRequest, TResponse> continuation)
    {
        counter.Record(context.Method.Name, request is CheckBulkPermissionsRequest bulk ? bulk.Items.Count : 0);
        return continuation(request, context);
    }

    public override AsyncServerStreamingCall<TResponse> AsyncServerStreamingCall<TRequest, TResponse>(TRequest request, ClientInterceptorContext<TRequest, TResponse> context, AsyncServerStreamingCallContinuation<TRequest, TResponse> continuation)
    {
        counter.Record(context.Method.Name, 0);
        return continuation(request, context);
    }
}
