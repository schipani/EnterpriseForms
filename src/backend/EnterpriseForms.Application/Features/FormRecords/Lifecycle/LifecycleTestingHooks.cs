namespace EnterpriseForms.Application.Features.FormRecords.Lifecycle;

/// <summary>
/// Testing-only hook (Phase C6 AC-2 adversarial tests, same pattern as the Authority's C-39 hooks): lets the integration
/// suite turn off the business API's lifecycle lock on the <b>shared-contributor</b> path, so a test can show that the
/// Authorization Authority alone refuses a contributor write to a Submitted or Archived record when the business API is
/// bypassed. Configuration key <c>Testing:BypassContributorLifecycleLock</c>; honoured only in the Testing environment,
/// and any other environment refuses to start with it set. Owner edits keep the business lock in every case.
/// </summary>
public sealed class LifecycleTestingHooks(bool bypassContributorLifecycleLock)
{
    public const string ConfigurationKey = "Testing:BypassContributorLifecycleLock";

    public static LifecycleTestingHooks Disabled { get; } = new(false);

    public bool BypassContributorLifecycleLock { get; } = bypassContributorLifecycleLock;

    /// <summary>Fail closed: the bypass may be configured only in the Testing environment.</summary>
    public static LifecycleTestingHooks FromConfiguration(bool configured, string environmentName)
    {
        if (!configured)
        {
            return Disabled;
        }

        if (!string.Equals(environmentName, "Testing", StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"{ConfigurationKey} is a Testing-only hook and must not be set in the {environmentName} environment. Refusing to start.");
        }

        return new LifecycleTestingHooks(true);
    }
}
