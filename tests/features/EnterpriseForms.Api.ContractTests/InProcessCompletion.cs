namespace EnterpriseForms.Api.ContractTests;

/// <summary>
/// E-1 test support. Key operations are asynchronous in the product; with the in-process local keyring and AES-GCM services
/// (or the in-memory fakes) they complete synchronously. Rule-engine tests that assert synchronously unwrap such a task here.
/// This never blocks: a task that has not already completed fails the test instead of being waited on.
/// </summary>
internal static class InProcessCompletion
{
    public static T Completed<T>(Task<T> task)
    {
        ArgumentNullException.ThrowIfNull(task);
        if (!task.IsCompleted)
        {
            throw new InvalidOperationException("expected an in-process key operation that completes synchronously; this helper never waits");
        }

        return task.GetAwaiter().GetResult();
    }
}
