using System.Diagnostics;

namespace AuthorizationAuthority.Application.Common;

/// <summary>
/// C-137 (R-37): optional diagnostics observer of organization-lock waits and phase durations of security commands and content
/// operations. Durations, counts, kinds and outcome codes only: never a payload, value, actor or record identifier. Registered
/// only when <c>Diagnostics:OperationTiming</c> is set in Development or Testing; otherwise absent and nothing is measured
/// beyond a stopwatch. It observes; it never changes a decision, an ordering or a transaction.
/// </summary>
public interface IOperationTimingObserver
{
    void SecurityCommand(SecurityCommandTiming timing);

    void ContentOperation(ContentOperationTiming timing);
}

/// <summary>One <c>SecurityCommandService.SubmitAsync</c> call. <c>Outcome</c> is <c>Committed</c>, <c>Replayed</c> or the refusal code.</summary>
public sealed record SecurityCommandTiming(Guid OrganizationId, string CommandKind, string Outcome, double LockWaitMilliseconds, double GraphLoadMilliseconds, int GraphEdgeCount, double PlanMilliseconds, double CommitMilliseconds, double TotalMilliseconds);

/// <summary>One content <c>Prepare</c> or <c>Commit</c> call. <c>Outcome</c> is <c>Completed</c> or the refusal or exception code.</summary>
public sealed record ContentOperationTiming(Guid OrganizationId, string Operation, string Outcome, double LockWaitMilliseconds, double TotalMilliseconds);

/// <summary>Elapsed time of one operation and the moment its organization lock was granted.</summary>
internal sealed class LockTiming
{
    private readonly Stopwatch watch = Stopwatch.StartNew();

    public double? LockedMilliseconds { get; private set; }

    public double ElapsedMilliseconds => watch.Elapsed.TotalMilliseconds;

    public void Locked() => LockedMilliseconds ??= watch.Elapsed.TotalMilliseconds;
}
