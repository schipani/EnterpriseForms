using AuthorizationAuthority.Application.Common.Ports;

namespace AuthorizationAuthority.Infrastructure.Faults;

public sealed class FaultInjectionOptions
{
    public const string SectionName = "FaultInjection";

    /// <summary>Only honoured in the Testing environment. Production/Staging always use <see cref="NoOpFaultInjector"/>.</summary>
    public bool Enabled { get; set; }
}

public sealed class NoOpFaultInjector : IFaultInjector
{
    public void CrashIfArmed(string faultName)
    {
    }
}

/// <summary>Testing only: one-shot process termination at the armed crash point (SEC-P09, CONS-25). The container restarts; recovery is reconciled by the processors.</summary>
public sealed class ProcessCrashFaultInjector : IFaultInjector
{
    private static readonly TimeSpan MaximumPause = TimeSpan.FromSeconds(60);
    private readonly object gate = new();
    private string armed = string.Empty;
    private TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int paused;

    public string Armed => armed;

    /// <summary>Operations currently held at a pause point.</summary>
    public int Paused => Volatile.Read(ref paused);

    /// <summary>Arms a fault; arming anything else (including None) releases every operation held at a pause point.</summary>
    public void Arm(string faultName)
    {
        lock (gate)
        {
            armed = faultName ?? string.Empty;
            release.TrySetResult();
            release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }

    public async Task PauseIfArmedAsync(string faultName, CancellationToken cancellationToken)
    {
        Task wait;
        lock (gate)
        {
            if (armed.Length == 0 || !string.Equals(armed, faultName, StringComparison.Ordinal))
            {
                return;
            }

            wait = release.Task;
        }

        Interlocked.Increment(ref paused);
        try
        {
            await wait.WaitAsync(MaximumPause, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // Bounded: a forgotten pause point never holds an operation forever.
        }
        finally
        {
            Interlocked.Decrement(ref paused);
        }
    }

    public void CrashIfArmed(string faultName)
    {
        if (armed.Length == 0 || !string.Equals(armed, faultName, StringComparison.Ordinal))
        {
            return;
        }

        armed = string.Empty;
        Console.Error.WriteLine($"FAULT-INJECTION: terminating process at '{faultName}' (Testing environment)");
        Console.Error.Flush();
        Environment.FailFast($"fault injection {faultName}");
    }
}
