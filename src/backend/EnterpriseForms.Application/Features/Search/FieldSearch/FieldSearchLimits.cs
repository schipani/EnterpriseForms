namespace EnterpriseForms.Application.Features.Search.FieldSearch;

/// <summary>
/// Phase C7 D4: provisional engineering limits (configuration section <c>FieldSearch</c>), enforced server-side. They are
/// starting values to be measured (PB-09), not production limits. Configuration may lower the approved ceilings (records,
/// verified bytes, execution time, conditions), never raise them (owner decision, Phase C7 M3). A bad configuration fails
/// at startup rather than at the first search.
/// </summary>
public sealed class FieldSearchLimits
{
    public const string SectionName = "FieldSearch";

    /// <summary>V1 hard ceiling for AND-combined conditions (D4/D6). Configuration may lower it, never raise it.</summary>
    public const int ApprovedMaximumConditions = 5;

    /// <summary>Approved V1 ceilings (D4, Phase C7 M3): never raised by configuration.</summary>
    public const int ApprovedMaximumEligibleRecords = 500;

    public const long ApprovedMaximumVerifiedBytes = 64L * 1024 * 1024;

    public const int ApprovedMaximumExecutionSeconds = 15;

    public int MaximumConditions { get; set; } = ApprovedMaximumConditions;

    public int MaximumEligibleRecords { get; set; } = ApprovedMaximumEligibleRecords;

    public long MaximumVerifiedBytes { get; set; } = ApprovedMaximumVerifiedBytes;

    public int MaximumExecutionSeconds { get; set; } = ApprovedMaximumExecutionSeconds;

    public int MaximumTextValueLength { get; set; } = 200;

    public int MinimumPartialTextLength { get; set; } = 3;

    /// <summary>
    /// Phase C7 M3 feature gate for field-value execution. Off by default; the host refuses to start with it on outside the
    /// isolated Development/Testing environments until the production prerequisites (PB-08 step-up for approvals, independent
    /// review, staging evidence) exist. Off ⇒ every search is refused with <c>TRUSTED_SEARCH_NOT_ENABLED</c>.
    /// </summary>
    public bool ExecutionEnabled { get; set; }

    /// <summary>Workload admission (Phase C7 M3): verified searches running at once on this API instance.</summary>
    public int MaximumConcurrentSearches { get; set; } = 4;

    /// <summary>Verified searches one user may run at once on this API instance.</summary>
    public int MaximumConcurrentSearchesPerUser { get; set; } = 1;

    /// <summary>How long a search waits for an execution slot before it is refused as busy (retryable).</summary>
    public int AdmissionWaitMilliseconds { get; set; } = 1500;

    public static readonly IReadOnlySet<int> PageSizes = new HashSet<int> { 10, 25, 50, 100 };

    public void Validate()
    {
        if (MaximumConditions is < 1 or > ApprovedMaximumConditions)
        {
            throw new InvalidOperationException($"FieldSearch:MaximumConditions must be 1..{ApprovedMaximumConditions} (D4/D6)");
        }

        if (MaximumEligibleRecords is < 1 or > ApprovedMaximumEligibleRecords || MaximumVerifiedBytes is < 1 or > ApprovedMaximumVerifiedBytes || MaximumExecutionSeconds is < 1 or > ApprovedMaximumExecutionSeconds)
        {
            throw new InvalidOperationException($"FieldSearch record, byte and time limits must be positive and at most {ApprovedMaximumEligibleRecords} records, {ApprovedMaximumVerifiedBytes} bytes and {ApprovedMaximumExecutionSeconds} seconds");
        }

        if (MaximumTextValueLength < 1 || MinimumPartialTextLength < 1 || MinimumPartialTextLength > MaximumTextValueLength)
        {
            throw new InvalidOperationException("FieldSearch text limits are inconsistent");
        }

        if (MaximumConcurrentSearches is < 1 or > 64 || MaximumConcurrentSearchesPerUser is < 1 or > 8 || AdmissionWaitMilliseconds is < 0 or > 10_000)
        {
            throw new InvalidOperationException("FieldSearch admission limits: 1..64 concurrent searches, 1..8 per user, wait 0..10000 ms");
        }
    }

    /// <summary>The execution gate is a development/test capability until the production prerequisites exist (Phase C7 M3).</summary>
    public void ValidateEnvironment(string environmentName)
    {
        if (ExecutionEnabled && environmentName is not ("Development" or "Testing"))
        {
            throw new InvalidOperationException($"FieldSearch:ExecutionEnabled is a development/test capability and must not be set in the {environmentName} environment (PB-08 and independent review outstanding). Refusing to start.");
        }
    }
}
