using System.Diagnostics;

namespace EnterpriseForms.Application.Common;

/// <summary>
/// C-137 (R-37): optional diagnostics sink for the phase durations of one business operation (bounded synthetic
/// measurements). Every host gets <see cref="NoOperationTiming"/>; only the Testing or Development API host replaces it when
/// <c>Diagnostics:OperationTiming</c> is set. Records durations, a poll count and an outcome code only: never a value, a
/// payload, an actor or a record identifier. It observes; it never changes the operation.
/// </summary>
public interface IOperationTimingSink
{
    bool Enabled { get; }

    void Record(OperationTimingRecord record);
}

public sealed record OperationTimingRecord(string Operation, Guid OrganizationId, string Outcome, IReadOnlyList<KeyValuePair<string, double>> Phases, int StatusPolls, double TotalMilliseconds);

public sealed class NoOperationTiming : IOperationTimingSink
{
    public static readonly NoOperationTiming Instance = new();

    private NoOperationTiming()
    {
    }

    public bool Enabled => false;

    public void Record(OperationTimingRecord record)
    {
    }
}

/// <summary>Named consecutive phases of one operation: each <see cref="Mark"/> closes the phase that started at the previous mark.</summary>
public sealed class OperationPhaseTimer(IOperationTimingSink sink)
{
    private readonly Stopwatch watch = Stopwatch.StartNew();
    private readonly List<KeyValuePair<string, double>> phases = [];
    private double last;
    private int polls;

    public void Mark(string phase)
    {
        if (!sink.Enabled)
        {
            return;
        }

        var now = watch.Elapsed.TotalMilliseconds;
        phases.Add(new KeyValuePair<string, double>(phase, now - last));
        last = now;
    }

    public void Poll() => polls++;

    public void Complete(string operation, Guid organizationId, string outcome)
    {
        if (sink.Enabled)
        {
            sink.Record(new OperationTimingRecord(operation, organizationId, outcome, phases, polls, watch.Elapsed.TotalMilliseconds));
        }
    }
}
