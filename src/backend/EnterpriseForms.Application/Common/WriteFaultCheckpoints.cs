namespace EnterpriseForms.Application.Common;

/// <summary>
/// Testing-only fault seam for the record-creation fault-injection tests (F-C7-01). Production and development hosts use
/// <see cref="NoWriteFaults"/>; the Testing host registers a header-armed injector only when
/// <see cref="WriteFaultCheckpoints.ConfigurationKey"/> is set, and every other environment refuses to start with it set.
/// A checkpoint fails the request with a transient 503 exactly where a crash or lost response would interrupt it.
/// </summary>
public interface IWriteFaultInjector
{
    void Checkpoint(string checkpointName);
}

public sealed class NoWriteFaults : IWriteFaultInjector
{
    public static NoWriteFaults Instance { get; } = new();

    public void Checkpoint(string checkpointName)
    {
    }
}

public static class WriteFaultCheckpoints
{
    public const string ConfigurationKey = "Testing:WriteFaultInjection";
    public const string RequestHeader = "X-Testing-Fault";
    public const string FaultErrorCode = "TESTING_FAULT_INJECTED";

    /// <summary>After the Authority's RegisterRecord command is Effective, before the local projection row.</summary>
    public const string RecordRegistered = "record-create:after-register";

    /// <summary>After the local record projection, before any content operation.</summary>
    public const string RecordProjected = "record-create:after-projection";

    /// <summary>After the Authority prepared (signed) the candidate, before it is staged.</summary>
    public const string ContentPrepared = "content:after-prepare";

    /// <summary>After the candidate is staged, before the Authority commit.</summary>
    public const string ContentStaged = "content:after-stage";

    /// <summary>After the Authority accepted the content (the response is lost).</summary>
    public const string ContentCommitted = "record-create:after-commit";
}
